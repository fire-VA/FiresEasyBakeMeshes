using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // O(1) replacement for ZNetScene.HaveInstanceInSector.
    //
    // Vanilla HaveInstanceInSector walks every entry in m_instances and computes
    // ZoneSystem.GetZone(instance.transform.position) for each one. Called from
    // ZoneSystem.UpdateTTL once per zone in m_zones that has aged past m_zoneTTL —
    // at megabase scale with 146k+ instances and ~600 loaded zones, that's tens
    // of millions of per-instance checks per UpdateTTL call (~80 ms/call). Profile
    // shows ZoneSystem.Update spiking to 80-87 ms/call during in-town movement,
    // dropping the frame to 5-10 fps.
    //
    // This module maintains a Dictionary<Vector2s, int> "count of non-distant
    // instances per sector" updated incrementally via Harmony patches on:
    //   - ZNetScene.AddInstance (postfix): increment
    //   - ZNetScene.OnZDODestroyed (prefix): decrement
    //   - ZNetScene.Destroy (prefix): decrement
    //   - DestroyTimeSlicer's drain loop: decrement explicitly
    //
    // HaveInstanceInSector becomes a single dictionary lookup. UpdateTTL's cost
    // drops from O(m_zones * m_instances) to O(m_zones).
    //
    // Staleness caveat: vanilla reads transform.position at query time. Our cache
    // records the sector at AddInstance time. If an instance MOVES sectors after
    // instantiation (creature wandering, ship sailing), our cache disagrees with
    // vanilla's computed answer.
    //   - False positive (we say "has instance" when really empty): vanilla keeps
    //     the zone root alive a little longer. Mild memory cost, no correctness
    //     issue.
    //   - False negative (we say "empty" when really has instance): vanilla would
    //     destroy the zone root, taking out the terrain under a wandering creature.
    //     This is the bad case.
    //
    // To bound staleness we trigger a periodic full rebuild from Plugin.Update
    // every SectorMirrorRebuildSeconds. The rebuild walks m_instances once (cost
    // similar to one vanilla HaveInstanceInSector call) and re-populates the
    // mirror. So instead of the 8x/sec * 80 ms = 640 ms/sec spent today, we pay
    // ~80 ms / RebuildSeconds (e.g., 80 ms / 60 s = 1.3 ms/sec) plus a near-zero
    // per-query cost.
    //
    // For megabases the world is mostly static building pieces — staleness
    // approaches zero in practice. The periodic rebuild is a safety valve for
    // anywhere with mobile entities.
    internal static class SectorInstanceMirror
    {
        // Per-sector count of non-distant ZNetViews currently tracked.
        private static readonly Dictionary<Vector2s, int> _countBySector = new Dictionary<Vector2s, int>();

        // ZDO -> sector at the time the ZDO was last observed in this mirror.
        // Required so OnInstanceRemoved knows which sector to decrement (the
        // ZNetView's transform may already be Unity-null by removal time).
        private static readonly Dictionary<ZDO, Vector2s> _zdoSector = new Dictionary<ZDO, Vector2s>();

        private static float _lastRebuildTime;

        private static FieldInfo s_instancesField;
        private static bool s_instancesFieldChecked;

        public static int SectorCount => _countBySector.Count;
        public static int TrackedZdoCount => _zdoSector.Count;

        public static void OnInstanceAdded(ZDO zdo, ZNetView nview)
        {
            if (zdo == null || nview == null) return;
            // Distant ZDOs are excluded by vanilla's HaveInstanceInSector filter
            // (the m_distant check). Mirror them out too so our count matches.
            if (nview.m_distant) return;
            if (_zdoSector.ContainsKey(zdo)) return; // already tracked

            var sector = ZoneSystem.GetZone(nview.transform.position);
            _zdoSector[zdo] = sector;
            Increment(sector);
        }

        public static void OnInstanceRemoved(ZDO zdo)
        {
            if (zdo == null) return;
            if (!_zdoSector.TryGetValue(zdo, out var sector)) return;
            _zdoSector.Remove(zdo);
            Decrement(sector);
        }

        // out result = true if any tracked instance exists in the sector.
        // Returns true if the mirror has data; false to signal "fall back to vanilla."
        public static bool TryHasInstance(Vector2s sector, out bool result)
        {
            result = _countBySector.TryGetValue(sector, out int count) && count > 0;
            return true;
        }

        // Periodic full rebuild — corrects drift from instances that moved sectors
        // since they were instantiated. Called from Plugin.Update at a configurable
        // cadence (default every 60s). The walk cost is similar to a single vanilla
        // HaveInstanceInSector call (one foreach over m_instances), so spreading
        // it once per minute reclaims ~640 ms/sec of UpdateTTL cost in exchange
        // for one ~80 ms hitch per minute. Easily worthwhile.
        public static void MaybeRebuild(ZNetScene scene)
        {
            if (scene == null) return;
            float now = Time.unscaledTime;
            float interval = FiresEasyBakeMeshesPlugin.SectorMirrorRebuildSeconds.Value;
            if (interval <= 0f) return;
            if ((now - _lastRebuildTime) < interval) return;

            var instances = GetInstancesDict(scene);
            if (instances == null) return;

            _countBySector.Clear();
            _zdoSector.Clear();
            int distantSkipped = 0;
            foreach (var kv in instances)
            {
                var zdo = kv.Key;
                var nv = kv.Value;
                if (nv == null) continue;
                if (nv.m_distant) { distantSkipped++; continue; }
                var sector = ZoneSystem.GetZone(nv.transform.position);
                _zdoSector[zdo] = sector;
                Increment(sector);
            }
            _lastRebuildTime = now;

            if (FiresEasyBakeMeshesPlugin.SectorMirrorVerbose.Value)
            {
                EasyBakeLog.Info(
                    $"[SectorMirror] Rebuilt: tracking {_zdoSector.Count} ZDOs across " +
                    $"{_countBySector.Count} sectors ({distantSkipped} distant skipped).");
            }
        }

        public static void Reset()
        {
            _countBySector.Clear();
            _zdoSector.Clear();
            _lastRebuildTime = 0f;
        }

        private static void Increment(Vector2s sector)
        {
            _countBySector.TryGetValue(sector, out int count);
            _countBySector[sector] = count + 1;
        }

        private static void Decrement(Vector2s sector)
        {
            if (!_countBySector.TryGetValue(sector, out int count)) return;
            if (count <= 1) _countBySector.Remove(sector);
            else _countBySector[sector] = count - 1;
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
                        "[SectorMirror] ZNetScene.m_instances field not found via reflection — " +
                        "periodic rebuild disabled (incremental updates still apply).");
                }
            }
            if (s_instancesField == null) return null;
            return s_instancesField.GetValue(scene) as Dictionary<ZDO, ZNetView>;
        }
    }
}
