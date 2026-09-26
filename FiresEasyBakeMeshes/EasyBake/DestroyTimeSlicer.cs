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
        private static readonly Queue<ZDO> _destroyQueue = new Queue<ZDO>();
        private static readonly HashSet<ZDO> _inQueue = new HashSet<ZDO>();
        private static readonly Stopwatch _sw = new Stopwatch();

        // Reused every tick. This runs inside CreateDestroyObjects, so allocating here would trade a CPU spike
        // for a GC one. _leavingSectors is a snapshot because the drain below removes instances from the mirror,
        // which would invalidate an enumerator over its keys.
        private static readonly HashSet<Vector2s> _loadedZones = new HashSet<Vector2s>();
        private static readonly List<Vector2s> _leavingSectors = new List<Vector2s>();

        private static FieldInfo s_zonesField;
        private static bool s_zonesFieldChecked;

        // ZoneSystem.m_zones is private. Returns false when it cannot be read, so the caller falls back to the
        // full walk rather than silently treating every sector as leaving - which would destroy the world.
        private static bool TryCollectLoadedZones()
        {
            var zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null) return false;

            if (!s_zonesFieldChecked)
            {
                s_zonesField = typeof(ZoneSystem).GetField("m_zones", BindingFlags.NonPublic | BindingFlags.Instance);
                s_zonesFieldChecked = true;
                if (s_zonesField == null)
                    EasyBakeLog.Warn("[DestroySlicer] ZoneSystem.m_zones not found; the destroy scan stays on the full walk.");
            }
            if (s_zonesField == null) return false;

            if (!(s_zonesField.GetValue(zoneSystem) is IDictionary zones) || zones.Count == 0) return false;

            _loadedZones.Clear();
            foreach (var key in zones.Keys) _loadedZones.Add((Vector2s)key);
            return true;
        }


        private static Vector2s CurrentZone()
        {
            var net = ZNet.instance;
            return net != null ? ZoneSystem.GetZone(net.GetReferencePosition()) : new Vector2s(0, 0);
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
        public static bool Run(ZNetScene scene, List<ZDO> near, List<ZDO> distant)
        {
            var instances = GetInstancesDict(scene);
            if (instances == null) return false;

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
            if (_destroyQueue.Count == 0
                && ScanGate.Destroy.CanSkip(near.Count, distant.Count, instances.Count, CurrentZone()))
            {
                return true;
            }

            byte num = (byte)(Time.frameCount & 0xFF);

            // Step 1: earmark near + distant. Identical to vanilla.
            for (int i = 0; i < near.Count; i++)
                near[i].TempRemoveEarmark = num;
            for (int i = 0; i < distant.Count; i++)
                distant[i].TempRemoveEarmark = num;

            // Step 2: enqueue un-earmarked ZDOs. HashSet dedupes against ZDOs already queued on previous ticks.
            //
            // The full m_instances walk is the zone-border spike, and the time budget below never covered it
            // (_sw.Restart runs after). ScanGate skips most ticks but cannot skip a border crossing, so the walk
            // lands on the worst frame: 4.4 ms at 48k instances on the dedi client, 56.6 ms/s at 136,688 in
            // single player.
            //
            // A sector with a LOADED ZONE keeps its instances, so only sectors without one can be leaving.
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
            bool useIndex = SectorInstanceMirror.HasData
                            && !SectorInstanceMirror.ConsumeFullScanRequest()
                            && TryCollectLoadedZones();

            if (useIndex)
            {
                _leavingSectors.Clear();
                foreach (var sector in SectorInstanceMirror.Sectors)
                    if (!_loadedZones.Contains(sector)) _leavingSectors.Add(sector);

                for (int i = 0; i < _leavingSectors.Count; i++)
                {
                    if (!SectorInstanceMirror.TryGetSectorZdos(_leavingSectors[i], out var leaving)) continue;
                    foreach (var zdo in leaving)
                    {
                        if (zdo.TempRemoveEarmark != num && _inQueue.Add(zdo))
                        {
                            _destroyQueue.Enqueue(zdo);
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
                        _destroyQueue.Enqueue(zdo);
                        enqueuedThisTick++;
                    }
                }
            }
            // What this scan found is exactly what a skip would have missed, so it is the audit's answer.
            ScanGate.Destroy.NoteScanFound(enqueuedThisTick);

            // Step 3: drain queue under a per-frame time budget.
            float budgetMs = FiresEasyBakeMeshesPlugin.DestroyTimeSliceBudgetMs.Value;
            int drainedThisTick = 0;
            int reEarmarkThisTick = 0;

            _sw.Restart();
            while (_destroyQueue.Count > 0)
            {
                var zdo = _destroyQueue.Dequeue();
                _inQueue.Remove(zdo);

                // Re-earmarked while queued? Skip. Keepalive injection adding
                // this ZDO, the player walking back into the zone, or a
                // transient zone re-load all produce this signal. This is
                // exactly the spared-from-destruction case vanilla's single-
                // pass earmark would have handled — we just observed it on
                // a later tick.
                if (zdo.TempRemoveEarmark == num)
                {
                    reEarmarkThisTick++;
                    continue;
                }

                // Find the instance. Skip if it's already gone — an admin
                // remove, an OnZDODestroyed callback firing earlier in the
                // same tick, or another mod's destroy could clear it ahead
                // of us.
                if (!instances.TryGetValue(zdo, out var nv) || nv == null)
                    continue;

                // Destroy in vanilla order: reset the view's ZDO ref first
                // (so OnDestroy callbacks see a null ZDO), queue the GO for
                // destruction, conditionally destroy the ZDO itself (fires
                // OnZDODestroyed which is a no-op since instances.Remove
                // happens immediately after), then remove from m_instances.
                //
                // We bypass ZNetScene.Destroy / OnZDODestroyed for persistent
                // ZDOs, so the SectorInstanceMirror won't get notified by the
                // normal patch surface. Notify it explicitly here so the
                // mirror's per-sector count stays in sync. For non-persistent
                // owned ZDOs, OnZDODestroyed fires via DestroyZDO and the
                // prefix patch handles it — we still call this; the mirror's
                // _zdoSector check makes the second call a no-op.
                nv.ResetZDO();
                Object.Destroy(nv.gameObject);
                if (!zdo.Persistent && zdo.IsOwner() && ZDOMan.instance != null)
                    ZDOMan.instance.DestroyZDO(zdo);
                SectorInstanceMirror.OnInstanceRemoved(zdo);
                instances.Remove(zdo);
                drainedThisTick++;

                // Budget check every 32 destroys. Stopwatch.Elapsed ≈ 100ns
                // per call; per-item polling would add ~3% overhead, every-32
                // amortizes to under 0.1%.
                if ((drainedThisTick & 31) == 0
                    && _sw.Elapsed.TotalMilliseconds >= budgetMs)
                    break;
            }

            _destroyedSinceReport += drainedThisTick;
            _reEarmarkSkippedSinceReport += reEarmarkThisTick;
            if (_destroyQueue.Count > _peakQueueSinceReport)
                _peakQueueSinceReport = _destroyQueue.Count;

            if (FiresEasyBakeMeshesPlugin.DestroyTimeSliceVerbose.Value)
            {
                float now = Time.unscaledTime;
                if ((now - _lastReportTime) > 5f
                    && (_destroyedSinceReport > 0 || _destroyQueue.Count > 0))
                {
                    EasyBakeLog.Info(
                        $"[DTS] destroyed={_destroyedSinceReport} " +
                        $"skipped(re-earmark)={_reEarmarkSkippedSinceReport} " +
                        $"queue-now={_destroyQueue.Count} peak={_peakQueueSinceReport} " +
                        $"in last {now - _lastReportTime:F1}s.");
                    _destroyedSinceReport = 0;
                    _reEarmarkSkippedSinceReport = 0;
                    _peakQueueSinceReport = _destroyQueue.Count;
                    _lastReportTime = now;
                }
            }

            return true;
        }

        public static void Reset()
        {
            _destroyQueue.Clear();
            _inQueue.Clear();
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
