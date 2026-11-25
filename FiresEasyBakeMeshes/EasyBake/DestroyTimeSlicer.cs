using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Time-sliced replacement of ZNetScene.RemoveObjects' destroy loop.
    //
    // Vanilla RemoveObjects:
    //   1. Earmark every ZDO in currentNearObjects + currentDistantObjects.
    //   2. Walk m_instances; for each ZNetView whose ZDO isn't earmarked, add
    //      to m_tempRemoved.
    //   3. For each entry in m_tempRemoved: ResetZDO, Object.Destroy(GO),
    //      maybe ZDOMan.DestroyZDO, remove from m_instances.
    //
    // The destroy loop (step 3) is the hitch source. Each Object.Destroy on a
    // building piece triggers OnDestroy on WearNTear, ZNetView, ZSyncTransform,
    // Piece, Collider, Renderer, ... — costs ~5-10us per piece. A megabase
    // zone-unload of ~10k pieces lands as a single ~100ms frame stall (seen
    // in the user's profile as RemoveObjects max=130-150ms during in-town
    // movement with Render Limits extending the loaded ring).
    //
    // This module replaces step 3 with a static queue drained on a per-frame
    // time budget. Items left in the queue at the budget cutoff persist to
    // the next tick. Items re-earmarked while sitting in the queue (player
    // walked back into the zone, our keepalive added them, a transient zone
    // re-load) are skipped at drain time and never destroyed — the queue is
    // self-correcting.
    //
    // Steps 1 and 2 still run on every call (cheap; same as vanilla). Only
    // step 3 is sliced. Mirrors vanilla's exact per-item order so OnDestroy /
    // OnZDODestroyed callbacks see identical state.
    internal static class DestroyTimeSlicer
    {
        // 1.2.250 (moved from FGN's ClientCleanupThrottle, Fire's SP split; this is the one removal pass): _destroyQueue holds the LOOSE objects, _solidQueue the
        // solid ones, drained loose first so containment outlives its contents; anything that moves itself is destroyed in the same frame (never left simulating
        // while the geometry around it goes); and an idle pass in the same zone scans at most 10 times a second. Client / local host only.
        private static readonly Queue<ZDO> _destroyQueue = new Queue<ZDO>();
        private static readonly Queue<ZDO> _solidQueue = new Queue<ZDO>();
        private static readonly List<ZDO> _destroyNow = new List<ZDO>(64);
        private static readonly HashSet<ZDO> _inQueue = new HashSet<ZDO>();
        private const float IdleScanHz = 10f;
        private static Vector2s s_idleZone = new Vector2s(short.MinValue, short.MinValue);
        private static float s_nextIdleScan;
        private static bool s_idleSaid, s_orderSaid;
        private static long s_cIdleGated, s_cMovers;

        private static bool IdleScanOn => FiresEasyBakeMeshesPlugin.RemovalIdleScanLimit != null && FiresEasyBakeMeshesPlugin.RemovalIdleScanLimit.Value
                                          && ZNet.instance != null && !ZNet.instance.IsDedicated();
        private static bool OrderedOn => FiresEasyBakeMeshesPlugin.DestroyMoversFirst != null && FiresEasyBakeMeshesPlugin.DestroyMoversFirst.Value
                                         && ZNet.instance != null && !ZNet.instance.IsDedicated();
        // For the idle object-area skip: the removal pass is on, and whether the next scan is due in this zone.
        internal static bool RemovalPassOn => FiresEasyBakeMeshesPlugin.DestroyTimeSliceEnabled != null && FiresEasyBakeMeshesPlugin.DestroyTimeSliceEnabled.Value;
        internal static bool ScannedThisZone { get { var z = CurrentZone(); return z.x == s_idleZone.x && z.y == s_idleZone.y; } }
        internal static bool IdleScanDue => Time.unscaledTime >= s_nextIdleScan;

        private static void Enqueue(Dictionary<ZDO, ZNetView> instances, ZDO zdo)
        {
            if (!OrderedOn) { _destroyQueue.Enqueue(zdo); return; }
            if (!s_orderSaid) { s_orderSaid = true; EasyBakeLog.Info("[DTS] removal order on: objects that move themselves go in the frame they leave, loose objects before solid ones"); }
            if (instances.TryGetValue(zdo, out var nv) && nv != null && IsMover(nv)) { _destroyNow.Add(zdo); return; }   // after the scan: the scan walks the table this edits
            if (zdo.Type == ZDO.ObjectType.Solid || zdo.Type == ZDO.ObjectType.Terrain) _solidQueue.Enqueue(zdo);
            else _destroyQueue.Enqueue(zdo);
        }

        private static bool IsMover(ZNetView view)
        {
            if (view.GetComponent<Character>() != null) return true;
            var body = view.GetComponent<Rigidbody>();
            return body != null && !body.isKinematic;
        }

        private static bool s_dequeuedSolid;
        private static readonly List<ZDO> s_batch = new List<ZDO>(512);
        private static readonly List<bool> s_batchSolid = new List<bool>(512);
        private static bool TryDequeue(out ZDO zdo)
        {
            if (_destroyQueue.Count > 0) { zdo = _destroyQueue.Dequeue(); s_dequeuedSolid = false; return true; }
            if (_solidQueue.Count > 0) { zdo = _solidQueue.Dequeue(); s_dequeuedSolid = true; return true; }
            zdo = null;
            return false;
        }

        // ---- 1.2.280 [Optimize] DestroyCostCap (lead 11-11, Fire's town -> swamp flight: "the frame after a drain: 88 frame(s), avg 95.9 ms, worst 692.7 ms";
        // 8,032 destroyed "while arriving" with no count cap). Object.Destroy runs at the END of the frame, so the drain's real cost lands on the NEXT frame,
        // where a time budget cannot see it. Here that next frame is measured: (its ms - a rolling baseline of frames with no drain before them) / the
        // destroys behind it = the cost of one destroy, learned separately for loose objects and solid ones (pieces: colliders, WearNTear), from frames
        // that drained only one kind. Each drain then takes as many as fit a next-frame target (4 ms in play, 25 ms while vanilla's black screen is fully
        // up), never fewer than a backlog floor (backlog / 600, at least 16: 60k objects still go in about 10-20 s, Fire's "low RAM over load hitch") and
        // never more than 512. Loose before solid is kept (cheap first). "Arriving" counts only while the black screen is fully up (Hud.m_loadingScreen,
        // alpha 1), so nothing big lands in visible frames after it fades. The drain curve (instances each second until the queue empties) is one Debug line.
        private static bool CostCapOn => FiresEasyBakeMeshesPlugin.DestroyCostCap != null && FiresEasyBakeMeshesPlugin.DestroyCostCap.Value;
        private static float s_baseMs = 16f, s_costLooseMs = 0.25f, s_costSolidMs = 0.6f;
        private static int s_drainLoose, s_drainSolid, s_lastLoose, s_lastSolid;
        private const float PlayTargetMs = 4f, CoveredTargetMs = 25f;

        private static bool ScreenCovered()
        {
            var hud = Hud.instance;
            if (hud == null || hud.m_loadingScreen == null) return true;   // no HUD (menu / loading the world): nothing is visible
            return hud.m_loadingScreen.gameObject.activeInHierarchy && hud.m_loadingScreen.alpha >= 0.99f;
        }

        private static void NoteBaseline(float ms) { if (ms > 0f && ms < 250f) s_baseMs = s_baseMs * 0.95f + ms * 0.05f; }

        private static void LearnCost(float frameMs)
        {
            int n = s_lastLoose + s_lastSolid;
            if (n <= 0) return;
            float per = Mathf.Max(0f, frameMs - s_baseMs) / n;
            if (s_lastSolid == 0) s_costLooseMs = s_costLooseMs * 0.8f + per * 0.2f;
            else if (s_lastLoose == 0) s_costSolidMs = s_costSolidMs * 0.8f + per * 0.2f;
        }

        private static int CostCount(bool covered)
        {
            float target = covered ? CoveredTargetMs : PlayTargetMs;
            float per = _destroyQueue.Count > 0 ? s_costLooseMs : s_costSolidMs;
            int fit = per > 0.001f ? Mathf.FloorToInt(target / per) : 512;
            int backlog = _destroyQueue.Count + _solidQueue.Count;
            int floor = Mathf.Max(16, backlog / 600);
            return Mathf.Clamp(Mathf.Max(fit, floor), 1, 512);
        }

        // drain curve: from the first frame with a backlog of 200+ until the queue is empty, instances sampled each second; one Debug line at the end
        private static bool s_curveOn; private static float s_curveStart, s_curveNext; private static int s_curvePeak; private static float s_curveWorst;
        private static readonly System.Text.StringBuilder s_curve = new System.Text.StringBuilder(256);
        private static void DrainCurveTick(int instances)
        {
            int backlog = _destroyQueue.Count + _solidQueue.Count;
            float now = Time.unscaledTime;
            if (!s_curveOn)
            {
                if (backlog < 200) return;
                s_curveOn = true; s_curveStart = now; s_curveNext = now + 1f; s_curvePeak = backlog; s_curveWorst = 0f;
                s_curve.Length = 0; s_curve.Append(instances.ToString("N0"));
                return;
            }
            if (backlog > s_curvePeak) s_curvePeak = backlog;
            float dt = Time.unscaledDeltaTime * 1000f; if (dt > s_curveWorst) s_curveWorst = dt;
            if (now >= s_curveNext && s_curve.Length < 900) { s_curveNext = now + 1f; s_curve.Append(" > ").Append(instances.ToString("N0")); }
            if (backlog > 0) return;
            s_curveOn = false;
            if (EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[DTS] drain curve: instances {s_curve} (each 1 s) in {now - s_curveStart:0.0} s; queue peak {s_curvePeak:N0}; worst frame during it {s_curveWorst:0} ms; "
                    + $"learned end-of-frame cost per destroy: loose {s_costLooseMs * 1000f:0} us, solid {s_costSolidMs * 1000f:0} us (baseline frame {s_baseMs:0.0} ms){(CostCapOn ? "" : "; DestroyCostCap off")}");
        }

        // vanilla's order for one removal, shared by the in-frame movers and the budgeted drain
        private static bool DestroyOne(Dictionary<ZDO, ZNetView> instances, ZDO zdo)
        {
            // Find the instance. Skip if it's already gone: an admin remove, an OnZDODestroyed callback firing earlier in the same tick, or another mod's
            // destroy could clear it ahead of us.
            if (!instances.TryGetValue(zdo, out var nv) || nv == null) return false;

            // Destroy in vanilla order: reset the view's ZDO ref first (so OnDestroy callbacks see a null ZDO), queue the GO for destruction, conditionally
            // destroy the ZDO itself (fires OnZDODestroyed which is a no-op since instances.Remove happens immediately after), then remove from m_instances.
            //
            // We bypass ZNetScene.Destroy / OnZDODestroyed for persistent ZDOs, so the SectorInstanceMirror won't get notified by the normal patch surface.
            // Notify it explicitly here so the mirror's per-sector count stays in sync. For non-persistent owned ZDOs, OnZDODestroyed fires via DestroyZDO and
            // the prefix patch handles it; we still call this; the mirror's _zdoSector check makes the second call a no-op.
            nv.ResetZDO();
            EbmTrace.Step("cds destroy", nv.gameObject.GetInstanceID(), nv.gameObject.name);
            Object.Destroy(nv.gameObject);
            if (!zdo.Persistent && zdo.IsOwner() && ZDOMan.instance != null)
                ZDOMan.instance.DestroyZDO(zdo);
            SectorInstanceMirror.OnInstanceRemoved(zdo);
            instances.Remove(zdo);
            return true;
        }
        private static readonly Stopwatch _sw = new Stopwatch();

        // Reused every tick. This runs inside CreateDestroyObjects, so allocating here would trade a CPU spike
        // for a GC one. _leavingSectors is a snapshot because the drain below removes instances from the mirror,
        // which would invalidate an enumerator over its keys.
        private static readonly List<Vector2s> _leavingSectors = new List<Vector2s>();

        private static Vector2s CurrentZone()
        {
            var net = ZNet.instance;
            return net != null ? EbmZones.Of(net.GetReferencePosition()) : new Vector2s(0, 0);
        }

        private static FieldInfo s_instancesField;
        private static bool s_instancesFieldChecked;

        private static int _destroyedSinceReport;
        private static int _reEarmarkSkippedSinceReport;
        private static int _peakQueueSinceReport;
        private static float _lastReportTime;

        // Returns true if we did the destroy work (caller should skip vanilla).
        // Returns false if reflection/state lookup failed — caller falls back
        // to vanilla.
        internal static bool Pending => _destroyQueue.Count > 0 || _solidQueue.Count > 0;   // 1.2.173: the dirty flag keeps passes running while this drains

        // 1.2.214 ([visual], lead / [seasons]: "no DTS census printed tonight"): one Debug line every 30 s - calls, how many the gate skipped,
        // index (sector diff) vs FULL m_instances walks, ZDOs earmarked and instances walked per call, ms a call and the worst call
        private static long s_cCalls, s_cGated, s_cIndex, s_cFull, s_cEarmarked, s_cWalked, s_cTicks, s_cWorst; private static float s_cSayAt;
        // 1.2.268 DestroyArrivalDrain
        private static bool ArrivalDrainOn => FiresEasyBakeMeshesPlugin.DestroyArrivalDrain != null && FiresEasyBakeMeshesPlugin.DestroyArrivalDrain.Value;
        private static int s_lastDrainCount, s_lastDrainFrame, s_cPostDrainFrames, s_cCapped, s_cArrivalDrained; private static float s_cPostDrainSum, s_cPostDrainWorst;
        private static bool LeanOn => FiresEasyBakeMeshesPlugin.DestroyLeanEarmark != null && FiresEasyBakeMeshesPlugin.DestroyLeanEarmark.Value;
        // 1.2.401 [Optimize] DestroyDistantBandSweep ([seasons], [lead]; Tools\INDIRECT_STALE_INSTANCES_READ_20261115.md section 6): sector-diff ticks also
        // list the sectors inside the ring but outside the near area. Vanilla keeps only distant-type objects there (ZDOMan.FindSectorObjects), so an ordinary
        // object left in that band (the town just travelled away from) is not stamped and is queued on the next tick, instead of waiting for the next FULL
        // walk (once per SectorMirrorRebuildSeconds, 60 s). The stamp test decides per object, exactly as for the sectors outside the ring.
        private static bool BandSweepOn => FiresEasyBakeMeshesPlugin.DestroyDistantBandSweep != null && FiresEasyBakeMeshesPlugin.DestroyDistantBandSweep.Value;
        private static readonly List<Vector2s> _bandSectors = new List<Vector2s>();
        private static long s_cSpared, s_cBand; private static bool s_bandSaid;   // census: queued objects spared by a fresh stamp; objects queued from the band
        // the near area as vanilla's FindSectorObjects and StreamPacer.InNear judge it (a radius, or the square on the classic setting)
        private static bool InNearArea(Vector2s sector, Vector2s here, SimulationDistance sd)
        {
            int d = Mathf.Max(Mathf.Abs(sector.x - here.x), Mathf.Abs(sector.y - here.y));
            if (d == 0) return true;
            if (d > sd.NearSimulationDistance) return false;
            return sd.IsClassic || ZoneSystem.instance == null || ZoneSystem.instance.ZonesWithinRadius(here, sector, sd.NearSimulationDistance, false);
        }
        private static bool s_leanTick; private static Vector2s s_leanHere; private static int s_leanRing; private static long s_cLean;
        private static bool InRing(ZDO zdo) { var s = zdo.GetSector(); return Mathf.Abs(s.x - s_leanHere.x) <= s_leanRing && Mathf.Abs(s.y - s_leanHere.y) <= s_leanRing; }

        // 1.2.216 ([generator]): lean assumes near[0..vanillaNear) is ONLY vanilla's FindSectorObjects output (all in the ring). If another mod
        // appended keep-alive ZDOs there before our prefix, lean would not stamp them and could destroy one outside the ring. The first five
        // lean ticks of a session check it; any ZDO outside the ring there = someone appended: name up to 5 prefabs and refuse lean for the session.
        private static bool s_leanRefused; private static int s_leanChecks; private static Vector2s s_lastCheckZone = new Vector2s(int.MinValue, int.MinValue); private static float s_nextLeanCheck; private static long s_cChecks;
        // 1.2.264 ([visual], finishing [generator]'s near-list writer check before this key goes default ON): the pack check found a writer of the
        // DISTANT list: TechPriestDhakharsPrefabs' ZDOMan.FindSectorObjects postfix (DistantPieceRenderer.FindSectorObjects_Postfix) appends its own
        // kept-alive far pieces to distantSectorObjects. Lean stamps only the near keepalive tail, never the distant list, so those ZDOs would go
        // unstamped and be destroyed where vanilla keeps them. The same check now also scans the distant list: any ZDO there outside the ring =
        // a foreign keep-alive = lean refused for the session (the vanilla stamp runs, exactly as without the key).
        private static readonly HashSet<ZDO> s_ownDistant = new HashSet<ZDO>();
        private static bool ForeignKeepalive(List<ZDO> near, int vanillaNear, List<ZDO> distant)
        {
            int found = 0; var names = new System.Text.StringBuilder();
            for (int i = 0; i < vanillaNear; i++)
            {
                var z = near[i];
                if (z == null || InRing(z)) continue;
                found++;
                if (found <= 5) { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(z.GetPrefab()) : null; names.Append(found > 1 ? ", " : "").Append(go != null ? go.name : z.GetPrefab().ToString()); }
            }
            if (distant != null)
            {
                // EBM's own distant prefabs (EbmDistantPrefabs, appended by EBM's postfix) are stamped by lean itself (EbmDistantPrefabs.Stamp): not foreign
                s_ownDistant.Clear(); foreach (var own in EbmDistantPrefabs.Cached) if (own != null) s_ownDistant.Add(own);
                for (int i = 0; i < distant.Count; i++)
                {
                    var z = distant[i];
                    if (z == null || InRing(z) || s_ownDistant.Contains(z)) continue;
                    found++;
                    if (found <= 5) { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(z.GetPrefab()) : null; names.Append(found > 1 ? ", " : "").Append(go != null ? go.name : z.GetPrefab().ToString()).Append(" (distant list)"); }
                }
            }
            if (found == 0) return false;
            s_leanRefused = true;
            EasyBakeLog.Warn($"[DTS] DestroyLeanEarmark refused for this session: {found} ZDO(s) outside the ring are in the near list before EBM's keepalive (another mod keeps them alive: {names}). The vanilla stamp is used instead, so nothing of theirs is destroyed.");
            return true;
        }

        private static void CensusEnd(long t0)
        {
            long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0; s_cTicks += d; if (d > s_cWorst) s_cWorst = d;
            if (Time.unscaledTime < s_cSayAt || !EasyBakeLog.DebugOn()) return;
            s_cSayAt = Time.unscaledTime + 30f;
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency; long ran = Mathf.Max(1, (int)(s_cCalls - s_cGated));
            EasyBakeLog.Debug($"[DTS] census 30 s: {s_cCalls} RemoveObjects call(s), {s_cGated} skipped by the gate ({s_cIdleGated} by the idle 10 Hz limit), {s_cMovers} mover(s) destroyed in-frame; of the rest {s_cIndex} sector-diff ({s_cLean} lean, {s_cChecks} foreign-keepalive check(s){(s_leanRefused ? ", LEAN REFUSED" : "")}) + {s_cFull} FULL walk(s); earmarked {s_cEarmarked / ran} ZDOs a call, walked {s_cWalked / Mathf.Max(1, (int)s_cFull)} instances a full walk; {s_cTicks * ms / Mathf.Max(1, (int)s_cCalls):0.000} ms a call, worst {s_cWorst * ms:0.00} ms, total {s_cTicks * ms / 30.0:0.00} ms/s; {s_cSpared} queued object(s) spared (stamped again while queued), {s_cBand} queued from the distant band (DestroyDistantBandSweep {(BandSweepOn ? "on" : "off")})");
            s_cIdleGated = 0; s_cMovers = 0; s_cSpared = 0; s_cBand = 0;   // 1.2.401
            if (ArrivalDrainOn && (s_cPostDrainFrames > 0 || s_cArrivalDrained > 0)) EasyBakeLog.Debug(HeightmapBulkDetach.TakeLine()); if (ArrivalDrainOn && (s_cPostDrainFrames > 0 || s_cArrivalDrained > 0)) EasyBakeLog.Debug($"[DTS] drains 30 s: {s_cArrivalDrained} destroyed while arriving (8 ms budget), {s_cCapped} drain(s) stopped at 256; the frame after a drain: {s_cPostDrainFrames} frame(s), avg {(s_cPostDrainFrames > 0 ? s_cPostDrainSum / s_cPostDrainFrames : 0f):0.0} ms, worst {s_cPostDrainWorst:0.0} ms");   // 1.2.268
            s_cPostDrainFrames = 0; s_cPostDrainSum = 0f; s_cPostDrainWorst = 0f; s_cCapped = 0; s_cArrivalDrained = 0;
            s_cLean = 0; s_cChecks = 0; s_cCalls = s_cGated = s_cIndex = s_cFull = s_cEarmarked = s_cWalked = s_cTicks = s_cWorst = 0;
        }

        public static bool Run(ZNetScene scene, List<ZDO> near, List<ZDO> distant, int vanillaNear = -1)
        {
            var instances = GetInstancesDict(scene);
            if (instances == null) return false;
            long cT0 = System.Diagnostics.Stopwatch.GetTimestamp(); s_cCalls++;   // 1.2.214 census

            // ═══ THE GATE, AND WHY THE EMPTY-QUEUE CONDITION IS NOT OPTIONAL ═══
            // Steps 1-2 below stamp ~113,000 ZDOs and then walk every live
            // ZNetView to find the ones without a stamp. Standing still they
            // find nothing, every pass, forever - the same waste the create
            // side had, measured at ~45 ms/s here.
            //
            // Skipping is far more dangerous on this side: a ZDO with no fresh
            // earmark is DESTROYED. So the gate carries a second condition that
            // has nothing to do with change detection - the queue must be EMPTY.
            // An empty queue means the last scan that ran enqueued nothing; if
            // nothing has changed since, this scan would enqueue nothing too.
            // With work pending we always run the real scan, so a skip can never
            // leave something queued and undrained, and step 3's re-earmark
            // reprieve is never evaluated against stamps we failed to write.
            // 1.2.250: nothing queued, same zone, scanned under 0.1 s ago: leave it (FGN's 1.5.99 rule). Before ScanGate, so its audit only sees scans that may run.
            Vector2s idleZone = CurrentZone(); float idleNow = Time.unscaledTime;
            if (!Pending && IdleScanOn && idleZone.x == s_idleZone.x && idleZone.y == s_idleZone.y && idleNow < s_nextIdleScan)
            {
                s_cGated++; s_cIdleGated++;
                if (!s_idleSaid) { s_idleSaid = true; EasyBakeLog.Info("[DTS] idle removal scan limit on: with nothing queued and the same zone, the removal scan runs at most 10 times a second"); }
                CensusEnd(cT0);
                return true;
            }
            s_idleZone = idleZone; s_nextIdleScan = idleNow + 1f / IdleScanHz;

            if (!Pending
                && ScanGate.Destroy.CanSkip(near.Count, distant.Count, instances.Count, CurrentZone()))
            {
                s_cGated++; CensusEnd(cT0);
                return true;
            }

            byte num = (byte)(Time.frameCount & 0xFF);
            // 1.2.401 ([seasons]): the stamp is one byte, and the drain below compares a queued ZDO's stamp with THIS tick's value on later ticks. A stamp
            // left from the tick that last listed it equals "num" once in 256 ticks; the ZDO was then dropped from the queue as "stamped again" and stayed
            // alive until the next full walk (log 11-15: queue peak 16,460, 2,816 destroyed, the rest gone from the queue). Every queued ZDO now gets a
            // value that is not this tick's before the lists are stamped; one the game wants again is stamped right below and is still spared.
            if (Pending)
            {
                byte notNow = (byte)(num + 1);
                foreach (var queued in _destroyQueue) queued.TempRemoveEarmark = notNow;
                foreach (var queued in _solidQueue) queued.TempRemoveEarmark = notNow;
            }

            // 1.2.215 [Optimize] DestroyLeanEarmark ([visual], [seasons]' read of the 134k town fill): with the sector diff below, only instances
            // in sectors OUTSIDE the ring are candidates, and vanilla's near / distant lists hold only ZDOs whose current sector is INSIDE the
            // ring plus our keepalive tail. So stamping all ~134k ZDOs a call is replaced by stamping the keepalive tail and asking each
            // candidate its live sector: kept when inside the ring. Never destroys more than vanilla would (a non-distant ZDO in the distant
            // band, which vanilla destroys, is spared a moment longer, as the index path already did).
            bool useIndexEarly = !EbmZones.Ring && SectorInstanceMirror.HasData && ZNet.instance != null && !SectorInstanceMirror.FullScanPending;
            bool lean = LeanOn && useIndexEarly && vanillaNear >= 0 && vanillaNear <= near.Count;
            s_leanHere = CurrentZone();
            s_leanRing = ZNet.instance != null ? ZNet.instance.GetSyncedSimulationDistance().TotalSimulationDistance : 0;
            if (lean && s_leanRefused) lean = false;
            // 1.2.216 ([generator]): a foreign keep-alive ZDO only leaves the ring when the reference zone moves, so check on every zone change and every 30 s
            if (lean && (s_leanChecks < 5 || !s_leanHere.Equals(s_lastCheckZone) || Time.unscaledTime >= s_nextLeanCheck))
            { s_leanChecks++; s_lastCheckZone = s_leanHere; s_nextLeanCheck = Time.unscaledTime + 30f; s_cChecks++; if (ForeignKeepalive(near, vanillaNear, distant)) lean = false; }
            s_leanTick = lean;
            if (lean)
            {
                for (int i = vanillaNear; i < near.Count; i++) near[i].TempRemoveEarmark = num;   // the keepalive tail only
                s_cEarmarked += near.Count - vanillaNear; s_cLean++;
                EbmDistantPrefabs.Stamp(num);   // 1.2.250: registered distant prefabs can sit outside the ring lean trusts, so they are stamped too
            }
            else
            {
                // Step 1: earmark near + distant. Identical to vanilla.
                for (int i = 0; i < near.Count; i++)
                    near[i].TempRemoveEarmark = num;
                for (int i = 0; i < distant.Count; i++)
                    distant[i].TempRemoveEarmark = num;
                s_cEarmarked += near.Count + distant.Count;
            }

            // Step 2: enqueue un-earmarked ZDOs. HashSet dedupes against ZDOs already queued on previous ticks.
            //
            // The full m_instances walk is the zone-border spike, and the time budget below never covered it
            // (_sw.Restart runs after). ScanGate skips most ticks but cannot skip a border crossing, so the walk
            // lands on the worst frame: 4.4 ms at 48k instances on the dedi client, 56.6 ms/s at 136,688 in
            // single player.
            //
            // Only sectors outside the active ring can be leaving (1.2.167; see below).
            // SectorInstanceMirror indexes instances by sector, and the active set comes from ZoneSystem's loaded
            // zones - 137 of them on the measured world.
            //
            // THE ACTIVE SET MUST COME FROM SOMETHING SMALL. Taking it from the near/distant ZDO lists instead
            // cost a GetSector() and a hash insert per ZDO, and `near` reaches 74,929 at a border: that version
            // made the worst call 4.37 -> 19.35 ms and was reverted. Loaded zones are O(137), read once a tick.
            //
            // Using loaded zones is deliberately CONSERVATIVE: a zone still loaded keeps its objects, so the
            // worst case is sparing something a moment longer, never destroying something that should live. The
            // mirror also records a sector at ADD time, so a moved instance is stale until the next rebuild -
            // hence the full walk whenever the mirror asks for one.
            int enqueuedThisTick = 0;
            // 1.2.122: on the ring "a loaded terrain zone keeps its instances" is wrong: the loaded x,z zones are the whole band column
            // while FAT instantiates by ring sector, so the index would never let ring objects go (the ~14.5k column again).
            // 1.2.167 ([generator] B1): the set was "sectors with no loaded zone". But a zone only unloads once its sector has no instances (ZoneSystem.UpdateTTL), so
            // each waited on the other until the next full walk, up to 60 s, and for ever with SectorMirrorRebuildSeconds 0. The set is now every sector outside
            // the active + distant ring; the earmark test below still decides, so nothing the game wants is touched.
            bool useIndex = !EbmZones.Ring && SectorInstanceMirror.HasData
                            && ZNet.instance != null
                            && !SectorInstanceMirror.ConsumeFullScanRequest();

            if (useIndex) s_cIndex++; else { s_cFull++; s_cWalked += instances.Count; }
            if (useIndex)
            {
                _leavingSectors.Clear();
                Vector2s here = CurrentZone();
                var sd = ZNet.instance.GetSyncedSimulationDistance();   // 1.2.401: read once (was the ring only)
                int ring = sd.TotalSimulationDistance;
                bool band = BandSweepOn && !lean;   // 1.2.401 [Optimize] DestroyDistantBandSweep
                _bandSectors.Clear();
                foreach (var sector in SectorInstanceMirror.Sectors)
                {
                    if (Mathf.Abs(sector.x - here.x) > ring || Mathf.Abs(sector.y - here.y) > ring) _leavingSectors.Add(sector);
                    else if (band && !InNearArea(sector, here, sd)) _bandSectors.Add(sector);
                }
                if (band && !s_bandSaid) { s_bandSaid = true; EasyBakeLog.Info("[DTS] DestroyDistantBandSweep on: ordinary objects left in the distant band (inside the ring, outside the near area) are queued for removal on the next pass, as the game does, instead of waiting for the next full walk"); }
                for (int i = 0; i < _bandSectors.Count; i++)
                {
                    if (!SectorInstanceMirror.TryGetSectorZdos(_bandSectors[i], out var inBand)) continue;
                    foreach (var zdo in inBand)
                    {
                        if (zdo.TempRemoveEarmark != num && _inQueue.Add(zdo))
                        {
                            Enqueue(instances, zdo);
                            enqueuedThisTick++; s_cBand++;
                        }
                    }
                }

                for (int i = 0; i < _leavingSectors.Count; i++)
                {
                    if (!SectorInstanceMirror.TryGetSectorZdos(_leavingSectors[i], out var leaving)) continue;
                    foreach (var zdo in leaving)
                    {
                        if (zdo.TempRemoveEarmark != num && !(lean && InRing(zdo)) && _inQueue.Add(zdo))
                        {
                            Enqueue(instances, zdo);
                            enqueuedThisTick++;
                        }
                    }
                }
            }
            else
            {
                foreach (var kv in instances)
                {
                    var zdo = kv.Key;
                    if (zdo.TempRemoveEarmark != num && _inQueue.Add(zdo))
                    {
                        Enqueue(instances, zdo);
                        enqueuedThisTick++;
                    }
                }
            }
            // What this scan found is exactly what a skip would have missed, so it is the audit's answer.
            ScanGate.Destroy.NoteScanFound(enqueuedThisTick);

            // 1.2.250: movers go now, outside the budget. Deferred to here because a destroy edits the tables the scan above walks.
            for (int i = 0; i < _destroyNow.Count; i++)
            {
                _inQueue.Remove(_destroyNow[i]);
                if (DestroyOne(instances, _destroyNow[i])) s_cMovers++;
            }
            _destroyNow.Clear();

            // Step 3: drain queue under a per-frame time budget.
            float budgetMs = FiresEasyBakeMeshesPlugin.DestroyTimeSliceBudgetMs.Value * PerformanceProfile.Budget;   // 1.2.135
            int drainedThisTick = 0;
            int reEarmarkThisTick = 0;
            // 1.2.268 [Optimize] DestroyArrivalDrain ([generator] READ_vpo220_vs_ours item 4): (1) with no player yet or while teleporting the screen is
            // covered, so the queue drains with at least 8 ms; (2) in play at most 256 destroys a frame: Unity destroys after the frame (Object.Destroy), so
            // their cost lands on the NEXT frame and a time budget alone cannot cap it; (3) that next frame's time is recorded (the census line below).
            bool arrivalDrain = false; int countCap = int.MaxValue;
            if (ArrivalDrainOn)
            {
                var me = Player.m_localPlayer;
                arrivalDrain = (me == null && ZNet.instance != null && !ZNet.instance.IsDedicated()) || (me != null && me.IsTeleporting());   // [generator]: no local player is also true on a dedicated server (BatchingRunOnServer)
                if (CostCapOn && arrivalDrain && !ScreenCovered()) arrivalDrain = false;   // 1.2.280 (lead note a): "arriving" only while vanilla's black screen is fully up
                if (arrivalDrain) budgetMs = Mathf.Max(budgetMs, 8f); else countCap = 256;
                if (s_lastDrainCount > 0 && Time.frameCount == s_lastDrainFrame + 1)
                {
                    float ms = Time.unscaledDeltaTime * 1000f;
                    s_cPostDrainFrames++; s_cPostDrainSum += ms; if (ms > s_cPostDrainWorst) s_cPostDrainWorst = ms;
                    LearnCost(ms);
                }
                else if (Time.frameCount != s_lastDrainFrame) NoteBaseline(Time.unscaledDeltaTime * 1000f);
                s_lastDrainCount = 0;
                if (CostCapOn) countCap = CostCount(arrivalDrain);   // 1.2.280 [Optimize] DestroyCostCap: count from the learned end-of-frame cost, floor from the backlog
            }
            DrainCurveTick(instances.Count);

            if (Pending) EbmTrace.Step("cds batch begin", _destroyQueue.Count + _solidQueue.Count, "frame " + Time.frameCount);   // 1.2.96
            _sw.Restart();
            if (HeightmapBulkDetach.On)
            {
                // 1.2.281: the batch is chosen first (same skips, same caps), each affected terrain's support list is rebuilt once, then all of it goes
                // 1.2.283 ([generator] review of 1.2.281, MEDIUM): chunks of 32 - pick, detach, destroy - with the ms budget checked between chunks, so the
                // synchronous destroy work counts against the budget (1.2.281 checked it only while picking, so a batch nearly always filled to the cap) and
                // nothing detached is ever left alive. Arrival (no count cap) is bounded by the ms budget again, as before 1.2.281.
                int cap = countCap;
                bool stop = false;
                while (!stop && drainedThisTick < cap)
                {
                    s_batch.Clear(); s_batchSolid.Clear();
                    while (s_batch.Count < 32 && drainedThisTick + s_batch.Count < cap && TryDequeue(out var bz))
                    {
                        _inQueue.Remove(bz);
                        if (bz.TempRemoveEarmark == num || (s_leanTick && InRing(bz))) { reEarmarkThisTick++; continue; }
                        if (!instances.TryGetValue(bz, out var bnv) || bnv == null) continue;
                        s_batch.Add(bz); s_batchSolid.Add(s_dequeuedSolid);
                    }
                    if (s_batch.Count == 0) break;
                    HeightmapBulkDetach.Detach(instances, s_batch);
                    for (int i = 0; i < s_batch.Count; i++)
                    {
                        if (!DestroyOne(instances, s_batch[i])) continue;
                        drainedThisTick++;
                        if (s_batchSolid[i]) s_drainSolid++; else s_drainLoose++;
                    }
                    if (_sw.Elapsed.TotalMilliseconds >= budgetMs) stop = true;
                }
                if (drainedThisTick >= cap) s_cCapped++;
                s_batch.Clear(); s_batchSolid.Clear();
            }
            else
            while (TryDequeue(out var zdo))
            {
                _inQueue.Remove(zdo);

                // Re-earmarked while queued? Skip. Keepalive injection adding
                // this ZDO, the player walking back into the zone, or a
                // transient zone re-load all produce this signal. This is
                // exactly the spared-from-destruction case vanilla's single-
                // pass earmark would have handled — we just observed it on
                // a later tick.
                if (zdo.TempRemoveEarmark == num || (s_leanTick && InRing(zdo)))   // 1.2.215: in a lean tick, a ZDO back inside the ring is spared too
                {
                    reEarmarkThisTick++;
                    continue;
                }

                bool solid = s_dequeuedSolid;
                if (!DestroyOne(instances, zdo)) continue;
                drainedThisTick++;
                if (solid) s_drainSolid++; else s_drainLoose++;
                if (drainedThisTick >= countCap) { s_cCapped++; break; }   // 1.2.268: the count ceiling (the rest wait a frame)

                // Budget check every 32 destroys. Stopwatch.Elapsed ≈ 100ns
                // per call; per-item polling would add ~3% overhead, every-32
                // amortizes to under 0.1%.
                if ((drainedThisTick & 31) == 0
                    && _sw.Elapsed.TotalMilliseconds >= budgetMs)
                    break;
            }

            if (drainedThisTick > 0)
            {
                EbmTrace.Step("cds batch end", drainedThisTick, string.Format(System.Globalization.CultureInfo.InvariantCulture, "frame {0}, {1:0.0} ms, queue left {2} (GameObjects destroy at the end of this frame)", Time.frameCount, _sw.Elapsed.TotalMilliseconds, _destroyQueue.Count + _solidQueue.Count));
                EbmTrace.NoteDestroyActivity();
            }
            if (drainedThisTick > 0) { s_lastDrainCount = drainedThisTick; s_lastDrainFrame = Time.frameCount; if (arrivalDrain) s_cArrivalDrained += drainedThisTick; }   // 1.2.268
            s_lastLoose = s_drainLoose; s_lastSolid = s_drainSolid; s_drainLoose = s_drainSolid = 0;   // 1.2.280: what the next frame's cost belongs to
            _destroyedSinceReport += drainedThisTick;
            _reEarmarkSkippedSinceReport += reEarmarkThisTick;
            s_cSpared += reEarmarkThisTick;   // 1.2.401: for the census line
            if (_destroyQueue.Count + _solidQueue.Count > _peakQueueSinceReport)
                _peakQueueSinceReport = _destroyQueue.Count + _solidQueue.Count;

            if (FiresEasyBakeMeshesPlugin.DestroyTimeSliceVerbose.Value)
            {
                float now = Time.unscaledTime;
                if ((now - _lastReportTime) > 5f
                    && (_destroyedSinceReport > 0 || Pending))
                {
                    EasyBakeLog.Debug(
                        $"[DTS] destroyed={_destroyedSinceReport} " +
                        $"skipped(re-earmark)={_reEarmarkSkippedSinceReport} " +
                        $"queue-now={_destroyQueue.Count + _solidQueue.Count} peak={_peakQueueSinceReport} " +
                        $"in last {now - _lastReportTime:F1}s.");
                    _destroyedSinceReport = 0;
                    _reEarmarkSkippedSinceReport = 0;
                    _peakQueueSinceReport = _destroyQueue.Count + _solidQueue.Count;
                    _lastReportTime = now;
                }
            }

            CensusEnd(cT0);   // 1.2.214
            return true;
        }

        public static void Reset()
        {
            _destroyQueue.Clear();
            _solidQueue.Clear();
            _destroyNow.Clear();
            _inQueue.Clear();
            s_idleZone = new Vector2s(short.MinValue, short.MinValue); s_nextIdleScan = 0f;
            _destroyedSinceReport = 0;
            _reEarmarkSkippedSinceReport = 0;
            _peakQueueSinceReport = 0;
            _lastReportTime = 0f;
        }

        private static Dictionary<ZDO, ZNetView> GetInstancesDict(ZNetScene scene)
        {
            if (!s_instancesFieldChecked)
            {
                s_instancesField = typeof(ZNetScene).GetField("m_instances",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                s_instancesFieldChecked = true;
                if (s_instancesField == null)
                {
                    EasyBakeLog.Warn(
                        "[DTS] ZNetScene.m_instances field not found via reflection — " +
                        "destroy time-slicing disabled (fail-open).");
                }
            }
            if (s_instancesField == null) return null;
            return s_instancesField.GetValue(scene) as Dictionary<ZDO, ZNetView>;
        }
    }
}
