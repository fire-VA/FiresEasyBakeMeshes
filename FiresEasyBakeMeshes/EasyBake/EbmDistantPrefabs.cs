using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.250 (moved from FGN's DistantPrefabs, client half only; Fire's SP split, plan row #5). For every prefab registered through EbmApi.RegisterDistantPrefab
    // the in-range ZDOs (per-prefab max distance) are cached, rebuilt one prefab per pass and swapped in whole, and a FindSectorObjects postfix, armed only inside
    // ZNetScene.CreateDestroyObjects, appends them to vanilla's distant list, so vanilla's own create / remove handle them. Never runs on a dedicated server, and
    // nothing runs until something registers. Marking ZDOs Distant and force-sending them stays FGN's (network work). [Optimize] DistantPrefabStreaming (default on).
    internal static class EbmDistantPrefabs
    {
        internal const float CacheSeconds = 2f;

        private static readonly Dictionary<string, float> s_distance = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<ZDO> s_scan = new List<ZDO>(), s_build = new List<ZDO>();
        internal static readonly List<ZDO> Cached = new List<ZDO>();
        internal static readonly HashSet<ZDO> Dedupe = new HashSet<ZDO>();
        internal static bool Injecting;
        private static bool s_dirty = true, s_scanActive, s_said;
        private static List<string> s_prefabs;
        private static int s_idx;
        private static float s_timer;
        // 1.2.339 ([visual], lead 11-12; Fire's hand test on his 2,506,610-ZDO world, EBM 1.2.338: PAIRS of 50-67 ms frames every 5.5-5.9 s while
        // standing still, "ZNetScene.Update 37-43 ms, CreateDestroyObjects 37-43 ms" with no create work; two prefabs registered (Core's
        // DistantRenderer: ConsMassiveTower, RavensRestGuildHall, 1500 m)). Step() forced vanilla's chunked GetAllZDOsWithPrefabIterative
        // (400 sector lists per call, ZDOMan.cs) over EVERY sector list of the world to completion in ONE pass, one prefab per pass = one heavy
        // frame per registered prefab, every CacheSeconds of accumulated 30 Hz deltas (~2x real time). sp27 had no Core, so no registrations and
        // no walk: why the perf-only legs never showed it. Fix ([Optimize] DistantPrefabScanSliced, ON): the same rebuild as a CYCLE of slices of
        // at most SliceMs per pass, resuming the iterative walk where it stopped (its own ref index); a prefab's hits are filtered when its walk
        // completes, the cache swaps whole at cycle end as before, and the next cycle starts CacheSeconds after the LAST one FINISHED. Census
        // (30 s, Debug): cycles, last cycle seconds, slices, worst slice ms, cached count. Off = the 1.2.250 whole-walk-per-pass (A/B arm).
        // End state (parked, Tools\PARKED_PERF_WORK.md): keep the set from ZDO add / remove by prefab hash, no walk after the seed.
        private const float SliceMs = 1f;
        private static int s_iterIdx;                 // vanilla's ref index into m_objectsBySector for the current prefab
        private static bool s_saidSliced;
        private static readonly System.Diagnostics.Stopwatch s_sliceWatch = new System.Diagnostics.Stopwatch();
        private static float s_cycleStart, s_lastCycleSec, s_nextReport;
        private static int p_cycles, p_slices, p_calls; private static double p_worstSlice;
        private static bool Sliced => FiresEasyBakeMeshesPlugin.DistantPrefabScanSliced == null || FiresEasyBakeMeshesPlugin.DistantPrefabScanSliced.Value;

        internal static int Count => s_distance.Count;
        internal static bool On => FiresEasyBakeMeshesPlugin.DistantPrefabStreaming == null || FiresEasyBakeMeshesPlugin.DistantPrefabStreaming.Value;

        internal static bool Register(string prefab, float maxMeters)
        {
            if (string.IsNullOrEmpty(prefab)) { EasyBakeLog.Warn("[DistantPrefabs] RegisterDistantPrefab refused: empty prefab name"); return false; }
            if (maxMeters <= 0f) return Unregister(prefab);
            bool isNew = !s_distance.ContainsKey(prefab);
            s_distance[prefab] = maxMeters;
            s_dirty = true;
            if (!s_said) { s_said = true; EasyBakeLog.Info("[DistantPrefabs] first registration: distant prefab streaming on"); }
            if (isNew) EasyBakeLog.Debug($"[DistantPrefabs] '{prefab}' registered to {maxMeters:0} m ({s_distance.Count} prefab(s))");
            return true;
        }

        internal static bool Unregister(string prefab)
        {
            if (string.IsNullOrEmpty(prefab) || !s_distance.Remove(prefab)) return false;
            Cached.Clear(); s_dirty = true;
            return true;
        }

        internal static void Refresh() { Cached.Clear(); s_dirty = true; }

        // The lean removal pass does not stamp the distant list; these can lie outside the ring it trusts.
        internal static void Stamp(byte mark)
        {
            for (int i = 0; i < Cached.Count; i++) { ZDO z = Cached[i]; if (z != null) z.TempRemoveEarmark = mark; }
        }

        internal static void Arm()
        {
            if (s_distance.Count == 0 || !On || ZDOMan.instance == null || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            s_timer += Time.deltaTime;
            // on the interval or after a registration change, never just because the cache is empty (empty is a valid state)
            // 1.2.339 sliced: the interval counts from the end of the last cycle (s_timer is reset there), so a long cycle never back-to-backs the next
            if (!s_scanActive && (s_dirty || s_timer >= CacheSeconds)) { s_dirty = false; if (!Sliced) s_timer = 0f; Begin(); }
            if (s_scanActive) { if (Sliced) StepSliced(); else Step(); }
            Report();
            Injecting = true;
        }

        private static void Begin()
        {
            s_build.Clear(); s_scan.Clear();
            s_prefabs = new List<string>(s_distance.Keys);
            s_idx = 0; s_iterIdx = 0;
            s_scanActive = s_prefabs.Count > 0;
            if (!s_scanActive) Cached.Clear();
            else
            {
                s_cycleStart = Time.unscaledTime;
                if (Sliced && !s_saidSliced) { s_saidSliced = true; EasyBakeLog.Info($"[DistantPrefabs] cache rebuild runs in slices of at most {SliceMs:0} ms per create pass (1.2.339: the whole-world walk per registered prefab in one frame was a pair of 40 ms frames every ~5 s on big worlds); {s_prefabs.Count} prefab(s)"); }
            }
        }

        // the 1.2.250 walk: one registered prefab per pass, vanilla's chunked walk forced to completion (kept behind DistantPrefabScanSliced = false)
        private static void Step()
        {
            Vector3 refPos = ZNet.instance.GetReferencePosition();
            string prefab = s_prefabs[s_idx];
            float max2 = s_distance.TryGetValue(prefab, out float d) ? d * d : 0f;
            s_scan.Clear();
            int idx = 0;
            s_sliceWatch.Restart();
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, s_scan, ref idx)) { p_calls++; }
            p_calls++;
            for (int i = 0; i < s_scan.Count; i++) { ZDO zdo = s_scan[i]; if (zdo != null && (refPos - zdo.GetPosition()).sqrMagnitude <= max2) s_build.Add(zdo); }
            s_scan.Clear();
            Slice(s_sliceWatch);
            if (++s_idx < s_prefabs.Count) return;
            FinishCycle();
        }

        // 1.2.339: the same walk, at most SliceMs per pass, resumed across passes through vanilla's own ref index
        private static void StepSliced()
        {
            s_sliceWatch.Restart();
            long budget = (long)(SliceMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (s_idx < s_prefabs.Count)
            {
                string prefab = s_prefabs[s_idx];
                bool done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, s_scan, ref s_iterIdx);
                p_calls++;
                if (done)
                {
                    Vector3 refPos = ZNet.instance.GetReferencePosition();
                    float max2 = s_distance.TryGetValue(prefab, out float d) ? d * d : 0f;
                    for (int i = 0; i < s_scan.Count; i++) { ZDO zdo = s_scan[i]; if (zdo != null && (refPos - zdo.GetPosition()).sqrMagnitude <= max2) s_build.Add(zdo); }
                    s_scan.Clear();
                    s_idx++; s_iterIdx = 0;
                }
                if (s_sliceWatch.ElapsedTicks >= budget) break;
            }
            Slice(s_sliceWatch);
            if (s_idx >= s_prefabs.Count) FinishCycle();
        }

        private static void Slice(System.Diagnostics.Stopwatch sw)
        {
            double ms = sw.ElapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            p_slices++; if (ms > p_worstSlice) p_worstSlice = ms;
        }

        private static void FinishCycle()
        {
            Cached.Clear(); Cached.AddRange(s_build); s_build.Clear();
            s_scanActive = false;
            s_lastCycleSec = Time.unscaledTime - s_cycleStart; p_cycles++;
            if (Sliced) s_timer = 0f;   // the interval runs from here
        }

        // 30 s census (Debug, PerfCensus): the rebuild's cost, so the hitch legs read it instead of inferring it from frame stamps
        private static void Report()
        {
            float now = Time.unscaledTime;
            if (now < s_nextReport) return;
            if (s_nextReport > 0f && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[DistantPrefabs] last 30 s: {p_cycles} rebuild cycle(s) (last took {s_lastCycleSec:0.0} s), {p_slices} slice(s) / {p_calls} sector-chunk call(s), worst slice {p_worstSlice:0.00} ms, {(Sliced ? "sliced" : "WHOLE WALK per pass")}; {s_distance.Count} prefab(s) registered, {Cached.Count} cached in range{(s_scanActive ? ", a cycle is running" : "")}");
            s_nextReport = now + 30f; p_cycles = p_slices = p_calls = 0; p_worstSlice = 0;
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    internal static class EbmDistantPrefabs_Arm
    {
        [HarmonyPrefix]
        private static void Prefix() { using (EbmPatchMarkers.DistantArm.Auto()) EbmDistantPrefabs.Arm(); }   // 1.2.360: profiler marker only

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception) { EbmDistantPrefabs.Injecting = false; return __exception; }
    }

    [HarmonyPatch]
    internal static class EbmDistantPrefabs_Inject
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(ZDOMan), "FindSectorObjects", new[] { typeof(Vector2s), typeof(SimulationDistance), typeof(List<ZDO>), typeof(List<ZDO>) });

        [HarmonyPostfix]
        private static void Postfix(List<ZDO> distantSectorObjects)
        {
            if (!EbmDistantPrefabs.Injecting || distantSectorObjects == null || EbmDistantPrefabs.Cached.Count == 0) return;
            var man = ZDOMan.instance;
            if (man == null) return;
            var dedupe = EbmDistantPrefabs.Dedupe;
            dedupe.Clear();
            foreach (var z in distantSectorObjects) if (z != null) dedupe.Add(z);
            foreach (var zdo in EbmDistantPrefabs.Cached)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                if (!ReferenceEquals(man.GetZDO(zdo.m_uid), zdo)) continue;   // the cache can be 2 s old: skip an entry ZDOMan replaced
                if (dedupe.Add(zdo)) distantSectorObjects.Add(zdo);
            }
            dedupe.Clear();
        }
    }
}
