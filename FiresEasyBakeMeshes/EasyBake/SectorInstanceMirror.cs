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
        private static Dictionary<Vector2s, int> _countBySector = new Dictionary<Vector2s, int>();

        // ZDO -> sector at the time the ZDO was last observed in this mirror.
        // Required so OnInstanceRemoved knows which sector to decrement (the
        // ZNetView's transform may already be Unity-null by removal time).
        private static Dictionary<ZDO, Vector2s> _zdoSector = new Dictionary<ZDO, Vector2s>();

        // Sector -> the ZDOs in it, INCLUDING distant ones. _countBySector deliberately excludes distant to keep
        // TryHasInstance identical to vanilla's HaveInstanceInSector; this index must not, because its consumer
        // (DestroyTimeSlicer) has to see every instance or distant objects would never be destroyed and would
        // leak silently. Maintained on add/remove only - never per frame.
        private static Dictionary<Vector2s, HashSet<ZDO>> _zdosBySector = new Dictionary<Vector2s, HashSet<ZDO>>();

        // Which ZDOs were kept out of _countBySector, so removal decrements exactly what addition incremented.
        private static HashSet<ZDO> _distantZdos = new HashSet<ZDO>();

        // 1.2.210 ([fgn]'s study finding, our own method): the non-distant instances that can MOVE (they have a Rigidbody: creatures, ships,
        // carts, dropped items). Before the mirror answers "empty" for a sector, it checks these few by their LIVE position, so a mover that
        // walked in since it was filed keeps the zone alive (vanilla would otherwise drop the zone root under it). Static pieces never move.
        private static Dictionary<ZDO, ZNetView> _mobile = new Dictionary<ZDO, ZNetView>();
        internal static int ConfirmedByMover;   // Debug: "empty" answers a mover overturned
        // 1.2.229 heap census: entries per table (a Dictionary entry ~ key + value + 12 B, a HashSet entry ~ value + 8 B)
        internal static long HeapBytes(out int zdos) { zdos = _zdoSector.Count; long n = 0; foreach (var hs in _zdosBySector.Values) n += hs.Count; return (long)_countBySector.Count * 24 + (long)_zdoSector.Count * 28 + n * 16 + (long)_distantZdos.Count * 16 + (long)_mobile.Count * 28; }

        private static float _lastRebuildTime;
        private static bool _fullScanRequested = true;

        private static FieldInfo s_instancesField;
        private static bool s_instancesFieldChecked;

        public static int SectorCount => _countBySector.Count;
        public static int TrackedZdoCount => _zdoSector.Count;

        public static void OnInstanceAdded(ZDO zdo, ZNetView nview)
        {
            if (zdo == null || nview == null) return;
            if (Rebuilding && !s_stZdoSector.ContainsKey(zdo)) StageAdd(zdo, nview);   // 1.2.222: the sliced rebuild sees it too
            if (_zdoSector.ContainsKey(zdo)) return; // already tracked

            var sector = ZoneSystem.GetZone(nview.transform.position);
            _zdoSector[zdo] = sector;
            AddToSector(sector, zdo);

            // Distant ZDOs are excluded by vanilla's HaveInstanceInSector filter (the m_distant check), so they
            // stay out of the COUNT. They are still indexed above: the destroy scan needs them.
            if (nview.m_distant) _distantZdos.Add(zdo);
            else { Increment(sector); if (nview.GetComponent<Rigidbody>() != null) _mobile[zdo] = nview; }
        }

        public static void OnInstanceRemoved(ZDO zdo)
        {
            if (zdo == null) return;
            if (Rebuilding) StageRemove(zdo);   // 1.2.222
            if (!_zdoSector.TryGetValue(zdo, out var sector)) return;
            _zdoSector.Remove(zdo);
            _mobile.Remove(zdo);
            RemoveFromSector(sector, zdo);
            if (!_distantZdos.Remove(zdo)) Decrement(sector);
        }

        private static void AddToSector(Vector2s sector, ZDO zdo)
        {
            if (!_zdosBySector.TryGetValue(sector, out var set))
            {
                set = RentSet();   // 1.2.234 [ghost]: pooled
                _zdosBySector[sector] = set;
            }
            set.Add(zdo);
        }

        private static void RemoveFromSector(Vector2s sector, ZDO zdo)
        {
            if (!_zdosBySector.TryGetValue(sector, out var set)) return;
            set.Remove(zdo);
            if (set.Count == 0) _zdosBySector.Remove(sector);
        }

        /// <summary>Every sector holding a tracked instance. Do not mutate the mirror while iterating this.</summary>
        public static Dictionary<Vector2s, HashSet<ZDO>>.KeyCollection Sectors => _zdosBySector.Keys;

        public static bool TryGetSectorZdos(Vector2s sector, out HashSet<ZDO> zdos) => _zdosBySector.TryGetValue(sector, out zdos);

        /// <summary>False before the mirror holds anything, so a consumer can fall back to a full walk.</summary>
        public static bool HasData => _zdoSector.Count > 0;

        /// <summary>
        /// True once after every rebuild and once at startup. The mirror records an instance's sector when it is
        /// ADDED, so anything that moves sector afterwards is stale until the next rebuild. A consumer that walks
        /// this index INSTEAD of m_instances asks here and does one full walk when it answers true, which bounds
        /// any miss to a single SectorMirrorRebuildSeconds.
        /// </summary>
        /// <summary>1.2.215: a full walk is requested (peek; ConsumeFullScanRequest clears it).</summary>
        public static bool FullScanPending => _fullScanRequested;

        public static bool ConsumeFullScanRequest()
        {
            if (!_fullScanRequested) return false;
            _fullScanRequested = false;
            return true;
        }

        // out result = true if any tracked instance exists in the sector.
        // Returns true if the mirror has data; false to signal "fall back to vanilla."
        public static bool TryHasInstance(Vector2s sector, out bool result)
        {
            result = _countBySector.TryGetValue(sector, out int count) && count > 0;
            if (!result && _mobile.Count > 0) result = MoverInSector(sector);
            return true;
        }

        // the live check behind an "empty" answer: any mobile instance now inside the sector? Movers found filed in the wrong sector are
        // re-filed on the way (the count follows them), so the next answers are right without waiting for the periodic rebuild
        private static readonly List<KeyValuePair<ZDO, ZNetView>> s_refile = new List<KeyValuePair<ZDO, ZNetView>>();
        private static readonly List<ZDO> s_dead = new List<ZDO>();
        private static bool MoverInSector(Vector2s sector)
        {
            bool found = false;
            s_refile.Clear(); s_dead.Clear();
            foreach (var kv in _mobile)
            {
                var nv = kv.Value;
                if (nv == null) { s_dead.Add(kv.Key); continue; }
                var now = ZoneSystem.GetZone(nv.transform.position);
                if (!_zdoSector.TryGetValue(kv.Key, out var filed) || !filed.Equals(now)) s_refile.Add(kv);
                if (now.Equals(sector)) found = true;
            }
            foreach (var z in s_dead) _mobile.Remove(z);
            foreach (var kv in s_refile)
            {
                var zdo = kv.Key;
                if (!_zdoSector.TryGetValue(zdo, out var old)) continue;
                var now = ZoneSystem.GetZone(kv.Value.transform.position);
                RemoveFromSector(old, zdo); Decrement(old);
                _zdoSector[zdo] = now; AddToSector(now, zdo); Increment(now);
            }
            if (found) ConfirmedByMover++;
            return found;
        }

        // Periodic full rebuild — corrects drift from instances that moved sectors
        // since they were instantiated. Called from Plugin.Update at a configurable
        // cadence (default every 60s). The walk cost is similar to a single vanilla
        // HaveInstanceInSector call (one foreach over m_instances), so spreading
        // it once per minute reclaims ~640 ms/sec of UpdateTTL cost in exchange
        // for one ~80 ms hitch per minute. Easily worthwhile.
        // ---- 1.2.222 ([ghost], 1.2.224 O log: EBM's worst plugin Update 115.3 ms, all "sector mirror + zsync report"): the one-frame walk of
        // every instance (134.5k at the town) is a once-a-minute ~115 ms hitch. With [Optimize] SectorMirrorSlicedRebuild the rebuild walks a
        // snapshot of the instances a slice per frame (SliceSize) into STAGING tables and swaps them in when the walk ends, so the live mirror
        // stays complete and consistent the whole time (queries keep reading the old tables until the swap). Instances added or removed during
        // the walk are applied to the staging tables too (OnInstanceAdded / OnInstanceRemoved below), so the swap loses nothing.
        private const int SliceSize = 8000;
        // 1.2.234 ([ghost], 1231 log line 2231: 249.6 KB/s heap growth in a window with the minutely rebuild): the per-sector sets were thrown
        // away and rebuilt (and re-grown) every rebuild. Now pooled; a set comes back cleared (its capacity kept), the pool is bounded.
        private static readonly Stack<HashSet<ZDO>> s_setPool = new Stack<HashSet<ZDO>>();
        private const int SetPoolMax = 16384;
        private static HashSet<ZDO> RentSet() => s_setPool.Count > 0 ? s_setPool.Pop() : new HashSet<ZDO>();
        private static void ReturnSets(Dictionary<Vector2s, HashSet<ZDO>> bySector)
        {
            foreach (var s in bySector.Values) { if (s_setPool.Count >= SetPoolMax) break; s.Clear(); s_setPool.Push(s); }
            bySector.Clear();
        }

        private static readonly List<KeyValuePair<ZDO, ZNetView>> s_snapshot = new List<KeyValuePair<ZDO, ZNetView>>();
        private static int s_slicePos = -1, s_sliceDistant;
        private static Dictionary<Vector2s, int> s_stCount = new Dictionary<Vector2s, int>();
        private static Dictionary<ZDO, Vector2s> s_stZdoSector = new Dictionary<ZDO, Vector2s>();
        private static Dictionary<Vector2s, HashSet<ZDO>> s_stBySector = new Dictionary<Vector2s, HashSet<ZDO>>();
        private static HashSet<ZDO> s_stDistant = new HashSet<ZDO>();
        private static Dictionary<ZDO, ZNetView> s_stMobile = new Dictionary<ZDO, ZNetView>();
        internal static int SlicedRebuilds; internal static float WorstSliceMs;
        private static bool Sliced => FiresEasyBakeMeshesPlugin.SectorMirrorSlicedRebuild != null && FiresEasyBakeMeshesPlugin.SectorMirrorSlicedRebuild.Value;
        private static bool Rebuilding => s_slicePos >= 0;

        private static void StageAdd(ZDO zdo, ZNetView nv)
        {
            var sector = ZoneSystem.GetZone(nv.transform.position);
            s_stZdoSector[zdo] = sector;
            if (!s_stBySector.TryGetValue(sector, out var set)) { set = RentSet(); s_stBySector[sector] = set; }
            set.Add(zdo);
            if (nv.m_distant) { s_stDistant.Add(zdo); s_sliceDistant++; return; }
            s_stCount.TryGetValue(sector, out int c); s_stCount[sector] = c + 1;
            if (nv.GetComponent<Rigidbody>() != null) s_stMobile[zdo] = nv;
        }

        private static void StageRemove(ZDO zdo)
        {
            if (!s_stZdoSector.TryGetValue(zdo, out var sector)) return;
            s_stZdoSector.Remove(zdo); s_stMobile.Remove(zdo);
            if (s_stBySector.TryGetValue(sector, out var set)) { set.Remove(zdo); if (set.Count == 0) s_stBySector.Remove(sector); }
            if (!s_stDistant.Remove(zdo) && s_stCount.TryGetValue(sector, out int c)) { if (c <= 1) s_stCount.Remove(sector); else s_stCount[sector] = c - 1; }
        }

        private static void SliceStep()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int end = System.Math.Min(s_snapshot.Count, s_slicePos + SliceSize);
            for (; s_slicePos < end; s_slicePos++)
            {
                var kv = s_snapshot[s_slicePos];
                if (kv.Key == null || kv.Value == null || s_stZdoSector.ContainsKey(kv.Key)) continue;
                StageAdd(kv.Key, kv.Value);
            }
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds > WorstSliceMs) WorstSliceMs = (float)sw.Elapsed.TotalMilliseconds;
            if (s_slicePos < s_snapshot.Count) return;
            // swap: staging becomes live; the old live tables become the next staging (cleared), so nothing is allocated per rebuild
            Swap(ref _countBySector, ref s_stCount); Swap(ref _zdoSector, ref s_stZdoSector); Swap(ref _zdosBySector, ref s_stBySector); Swap(ref _distantZdos, ref s_stDistant); Swap(ref _mobile, ref s_stMobile);
            s_stCount.Clear(); s_stZdoSector.Clear(); ReturnSets(s_stBySector); s_stDistant.Clear(); s_stMobile.Clear();   // the old live tables, reused as next staging (1.2.234: their sector sets go back to the pool)
            s_snapshot.Clear(); s_slicePos = -1; SlicedRebuilds++;
            _fullScanRequested = true;
            if (FiresEasyBakeMeshesPlugin.SectorMirrorVerbose.Value)
                EasyBakeLog.Debug($"[SectorMirror] Rebuilt in slices: tracking {_zdoSector.Count} ZDOs across {_countBySector.Count} sectors ({s_sliceDistant} distant), {_mobile.Count} mobile; worst slice {WorstSliceMs:F1} ms");
            s_sliceDistant = 0; WorstSliceMs = 0f;
        }

        // swaps the table references (the live fields are not readonly for this): no per-entry copy in the swap frame
        private static void Swap<T>(ref T a, ref T b) { var x = a; a = b; b = x; }   // O(1): reference swap

        public static void MaybeRebuild(ZNetScene scene)
        {
            if (scene == null) return;
            if (Rebuilding) { SliceStep(); return; }
            float now = Time.unscaledTime;
            float interval = FiresEasyBakeMeshesPlugin.SectorMirrorRebuildSeconds.Value;
            if (interval <= 0f) return;
            if ((now - _lastRebuildTime) < interval) return;

            var instances = GetInstancesDict(scene);
            if (instances == null) return;
            if (Sliced && HasData)
            {
                _lastRebuildTime = now;
                s_snapshot.Clear();
                foreach (var kv in instances) s_snapshot.Add(kv);   // one copy of references (no per-instance work in this frame)
                s_stCount.Clear(); s_stZdoSector.Clear(); ReturnSets(s_stBySector); s_stDistant.Clear(); s_stMobile.Clear();
                s_slicePos = 0; s_sliceDistant = 0;
                SliceStep();
                return;
            }

            _countBySector.Clear();
            _zdoSector.Clear();
            _zdosBySector.Clear();
            _distantZdos.Clear();
            _mobile.Clear();
            int distantSkipped = 0;
            foreach (var kv in instances)
            {
                var zdo = kv.Key;
                var nv = kv.Value;
                if (nv == null) continue;
                var sector = ZoneSystem.GetZone(nv.transform.position);
                _zdoSector[zdo] = sector;
                AddToSector(sector, zdo);
                if (nv.m_distant) { distantSkipped++; _distantZdos.Add(zdo); continue; }
                Increment(sector);
                if (nv.GetComponent<Rigidbody>() != null) _mobile[zdo] = nv;
            }
            _lastRebuildTime = now;
            _fullScanRequested = true;

            if (FiresEasyBakeMeshesPlugin.SectorMirrorVerbose.Value)
            {
                EasyBakeLog.Debug(
                    $"[SectorMirror] Rebuilt: tracking {_zdoSector.Count} ZDOs across " +
                    $"{_countBySector.Count} sectors ({distantSkipped} distant skipped), {_mobile.Count} mobile; {ConfirmedByMover} 'empty' answer(s) overturned by a mover since the last rebuild.");
                ConfirmedByMover = 0;
            }
        }

        public static void Reset()
        {
            _countBySector.Clear();
            _zdoSector.Clear();
            _zdosBySector.Clear();
            _distantZdos.Clear();
            _mobile.Clear();
            _lastRebuildTime = 0f;
            _fullScanRequested = true;
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
