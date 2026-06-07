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

            byte num = (byte)(Time.frameCount & 0xFF);

            // Step 1: earmark near + distant. Identical to vanilla.
            for (int i = 0; i < near.Count; i++)
                near[i].TempRemoveEarmark = num;
            for (int i = 0; i < distant.Count; i++)
                distant[i].TempRemoveEarmark = num;

            // Step 2: scan m_instances; enqueue new unearmarked ZDOs.
            // HashSet dedupes against ZDOs already queued in previous ticks.
            foreach (var kv in instances)
            {
                var zdo = kv.Key;
                if (zdo.TempRemoveEarmark != num && _inQueue.Add(zdo))
                    _destroyQueue.Enqueue(zdo);
            }

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
