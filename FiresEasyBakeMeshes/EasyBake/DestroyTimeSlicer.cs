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

        private static bool TryDequeue(out ZDO zdo)
        {
            if (_destroyQueue.Count > 0) { zdo = _destroyQueue.Dequeue(); return true; }
            if (_solidQueue.Count > 0) { zdo = _solidQueue.Dequeue(); return true; }
            zdo = null;
            return false;
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
        private static bool LeanOn => FiresEasyBakeMeshesPlugin.DestroyLeanEarmark != null && FiresEasyBakeMeshesPlugin.DestroyLeanEarmark.Value;
        private static bool s_leanTick; private static Vector2s s_leanHere; private static int s_leanRing; private static long s_cLean;
        private static bool InRing(ZDO zdo) { var s = zdo.GetSector(); return Mathf.Abs(s.x - s_leanHere.x) <= s_leanRing && Mathf.Abs(s.y - s_leanHere.y) <= s_leanRing; }

        // 1.2.216 ([generator]): lean assumes near[0..vanillaNear) is ONLY vanilla's FindSectorObjects output (all in the ring). If another mod
        // appended keep-alive ZDOs there before our prefix, lean would not stamp them and could destroy one outside the ring. The first five
        // lean ticks of a session check it; any ZDO outside the ring there = someone appended: name up to 5 prefabs and refuse lean for the session.
        private static bool s_leanRefused; private static int s_leanChecks; private static Vector2s s_lastCheckZone = new Vector2s(int.MinValue, int.MinValue); private static float s_nextLeanCheck; private static long s_cChecks;
        private static bool ForeignKeepalive(List<ZDO> near, int vanillaNear)
        {
            int found = 0; var names = new System.Text.StringBuilder();
            for (int i = 0; i < vanillaNear; i++)
            {
                var z = near[i];
                if (z == null || InRing(z)) continue;
                found++;
                if (found <= 5) { var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(z.GetPrefab()) : null; names.Append(found > 1 ? ", " : "").Append(go != null ? go.name : z.GetPrefab().ToString()); }
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
            EasyBakeLog.Debug($"[DTS] census 30 s: {s_cCalls} RemoveObjects call(s), {s_cGated} skipped by the gate ({s_cIdleGated} by the idle 10 Hz limit), {s_cMovers} mover(s) destroyed in-frame; of the rest {s_cIndex} sector-diff ({s_cLean} lean, {s_cChecks} foreign-keepalive check(s){(s_leanRefused ? ", LEAN REFUSED" : "")}) + {s_cFull} FULL walk(s); earmarked {s_cEarmarked / ran} ZDOs a call, walked {s_cWalked / Mathf.Max(1, (int)s_cFull)} instances a full walk; {s_cTicks * ms / Mathf.Max(1, (int)s_cCalls):0.000} ms a call, worst {s_cWorst * ms:0.00} ms, total {s_cTicks * ms / 30.0:0.00} ms/s");
            s_cIdleGated = 0; s_cMovers = 0;
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
            { s_leanChecks++; s_lastCheckZone = s_leanHere; s_nextLeanCheck = Time.unscaledTime + 30f; s_cChecks++; if (ForeignKeepalive(near, vanillaNear)) lean = false; }
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
                int ring = ZNet.instance.GetSyncedSimulationDistance().TotalSimulationDistance;
                foreach (var sector in SectorInstanceMirror.Sectors)
                    if (Mathf.Abs(sector.x - here.x) > ring || Mathf.Abs(sector.y - here.y) > ring) _leavingSectors.Add(sector);

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

            if (Pending) EbmTrace.Step("cds batch begin", _destroyQueue.Count + _solidQueue.Count, "frame " + Time.frameCount);   // 1.2.96
            _sw.Restart();
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

                if (!DestroyOne(instances, zdo)) continue;
                drainedThisTick++;

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
            _destroyedSinceReport += drainedThisTick;
            _reEarmarkSkippedSinceReport += reEarmarkThisTick;
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
