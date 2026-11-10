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
            if (!s_scanActive && (s_dirty || s_timer >= CacheSeconds)) { s_dirty = false; s_timer = 0f; Begin(); }
            if (s_scanActive) Step();
            Injecting = true;
        }

        private static void Begin()
        {
            s_build.Clear(); s_scan.Clear();
            s_prefabs = new List<string>(s_distance.Keys);
            s_idx = 0;
            s_scanActive = s_prefabs.Count > 0;
            if (!s_scanActive) Cached.Clear();
        }

        private static void Step()
        {
            Vector3 refPos = ZNet.instance.GetReferencePosition();
            string prefab = s_prefabs[s_idx];
            float max2 = s_distance.TryGetValue(prefab, out float d) ? d * d : 0f;
            s_scan.Clear();
            int idx = 0;
            while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, s_scan, ref idx)) { }
            for (int i = 0; i < s_scan.Count; i++) { ZDO zdo = s_scan[i]; if (zdo != null && (refPos - zdo.GetPosition()).sqrMagnitude <= max2) s_build.Add(zdo); }
            s_scan.Clear();
            if (++s_idx < s_prefabs.Count) return;
            Cached.Clear(); Cached.AddRange(s_build); s_build.Clear();
            s_scanActive = false;
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    internal static class EbmDistantPrefabs_Arm
    {
        [HarmonyPrefix]
        private static void Prefix() => EbmDistantPrefabs.Arm();

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
