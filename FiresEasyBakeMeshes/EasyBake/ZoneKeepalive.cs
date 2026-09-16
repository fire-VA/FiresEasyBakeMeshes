using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Pins baked zones in ZNetScene.m_instances even after the player walks
    // outside the active ring. Vanilla CreateDestroyObjects destroys ZNetViews
    // whose ZDOs fall out of FindSectorObjects' near+distant lists every 33ms;
    // at a megabase that means re-paying thousands of Instantiate calls each
    // time the player crosses a zone boundary within town. The Mesh.SetVertices
    // cache reconstruct lands on the same frame as the mass re-Instantiate and
    // shows up as a multi-hundred-ms hitch.
    //
    // Mechanism: a prefix on ZNetScene.RemoveObjects appends our kept-alive
    // zones' ZDOs to the currentNearObjects list before vanilla's earmark loop
    // runs. Earmarked ZDOs are spared from destruction; their ZNetViews + our
    // bake's Parent GameObject stay alive across zone-cross transitions.
    //
    // Lifecycle:
    //   - MarkActive(coord) is called whenever a zone's bake completes or
    //     attaches from cache. The entry tracks the most recent time the
    //     zone was in the player's active ring.
    //   - Update(playerCenter, activeArea) refreshes the timestamp for any
    //     entry currently in the active ring, and prunes entries that have
    //     either drifted past KeepaliveRadius or aged past KeepaliveSeconds
    //     since they were last active. Pruned entries fall back to vanilla
    //     destroy behavior on the next CreateDestroyObjects tick.
    //
    // Memory bound: |entries| <= (2 * KeepaliveRadius + 1)^2. Per-frame CPU
    // cost is one Mathf.Max + Mathf.Abs per entry plus one ZDOMan.FindObjects
    // per entry inside the RemoveObjects prefix. With WearNTear short-circuit
    // active, kept-alive ZNetViews contribute near-zero additional cost.
    internal static class ZoneKeepalive
    {
        private struct Entry
        {
            public float LastActiveTime;
        }

        private static readonly Dictionary<Vector2s, Entry> _entries = new Dictionary<Vector2s, Entry>();
        private static readonly List<ZDO> _scratch = new List<ZDO>();
        private static readonly List<Vector2s> _refreshScratch = new List<Vector2s>();
        private static readonly List<Vector2s> _pruneScratch = new List<Vector2s>();

        public static int Count => _entries.Count;

        public static bool IsKeptAlive(Vector2s coord) => _entries.ContainsKey(coord);

        public static void MarkActive(Vector2s coord)
        {
            _entries[coord] = new Entry { LastActiveTime = Time.unscaledTime };
        }

        // Append our pinned zones' ZDOs to the caller's near list, BUT skip
        // any zone whose coord is within `vanillaCoveredRadius` of the player
        // center. Vanilla's FindSectorObjects has already filled the near list
        // for that ring, so re-fetching would duplicate work. Net effect:
        // keepalive only does the FindSectorObjects work for zones that fell
        // outside vanilla's coverage but are still within our keepalive radius.
        //
        // Pass `vanillaCoveredRadius = -1` to disable dedup (always inject).
        public static void AppendKeepaliveZDOs(List<ZDO> nearList, Vector2s playerCenter, int vanillaCoveredRadius)
        {
            if (_entries.Count == 0) return;
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return;
            // FindSectorObjects with area=0, distantArea=0 reduces to a single
            // FindObjects(coord, list) call — no ring walk, no allocations
            // beyond the AddRange copy into _scratch.
            foreach (var coord in _entries.Keys)
            {
                if (vanillaCoveredRadius >= 0)
                {
                    int dist = Mathf.Max(
                        Mathf.Abs(coord.x - playerCenter.x),
                        Mathf.Abs(coord.y - playerCenter.y));
                    if (dist <= vanillaCoveredRadius) continue;
                }
                _scratch.Clear();
                // (0, 0) = this sector only, no near/far expansion - the 1.0 signature takes a
                    // SimulationDistance where the old one took two int radii.
                    zdoMan.FindSectorObjects(coord, new SimulationDistance(0, 0), _scratch);
                for (int i = 0; i < _scratch.Count; i++)
                    nearList.Add(_scratch[i]);
            }
        }

        public static void Update(Vector2s playerCenter, int activeArea)
        {
            if (_entries.Count == 0) return;
            float now = Time.unscaledTime;
            float maxAge = FiresEasyBakeMeshesPlugin.ZoneKeepaliveSeconds.Value;
            int maxRadius = FiresEasyBakeMeshesPlugin.ZoneKeepaliveRadius.Value;

            _refreshScratch.Clear();
            _pruneScratch.Clear();
            foreach (var kv in _entries)
            {
                var coord = kv.Key;
                int dist = Mathf.Max(
                    Mathf.Abs(coord.x - playerCenter.x),
                    Mathf.Abs(coord.y - playerCenter.y));
                // Refresh while within keepalive radius — not just vanilla's
                // m_activeArea. Mods like Render Limits expand the visible /
                // loaded ring via separate mechanisms that may not bump
                // m_activeArea, so refreshing only on the vanilla active ring
                // would let zones in the player's visible area age out and get
                // destroyed mid-movement.
                if (dist <= maxRadius)
                {
                    _refreshScratch.Add(coord);
                    continue;
                }
                // Outside the keepalive radius: prune if it's been long enough
                // since the player was last within radius.
                if ((now - kv.Value.LastActiveTime) > maxAge)
                    _pruneScratch.Add(coord);
            }
            for (int i = 0; i < _refreshScratch.Count; i++)
                _entries[_refreshScratch[i]] = new Entry { LastActiveTime = now };
            for (int i = 0; i < _pruneScratch.Count; i++)
                _entries.Remove(_pruneScratch[i]);

            if (FiresEasyBakeMeshesPlugin.ZoneKeepaliveVerbose.Value
                && (_refreshScratch.Count > 0 || _pruneScratch.Count > 0))
            {
                if ((now - _lastVerboseReportTime) > 5f)
                {
                    EasyBakeLog.Info(
                        $"[Keepalive] {_entries.Count} zones pinned at " +
                        $"center=({playerCenter.x},{playerCenter.y}), radius={maxRadius}. " +
                        $"This tick: refreshed={_refreshScratch.Count}, pruned={_pruneScratch.Count}.");
                    _lastVerboseReportTime = now;
                }
            }
        }

        private static float _lastVerboseReportTime;

        public static void Reset()
        {
            _entries.Clear();
            _lastVerboseReportTime = 0f;
        }
    }
}
