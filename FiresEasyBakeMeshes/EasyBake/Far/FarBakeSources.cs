using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.341 ([visual], lead 11-12, G2 of the far-drawing plan; Fire's shore shot 49: a baked-blueprint build shows as "a handful of
    /// scattered pieces"). FAP's runtime-baked blueprints (VA_BakeParent placements) are ONE Piece prefab per whole structure whose combined
    /// meshes are attached per instance from FAP's bake cache, so the far tier had no mesh for them (FarBuildingSources reads the prefab, the
    /// template has none) and skipped them anyway (RegisterFarSkip). Now FAP 1.1.426 hands EBM the bake's parts by CACHE NAME through
    /// EbmApi.RegisterFarBakeSource(meshesByCacheName, materialsByCacheName) (index-aligned Mesh[] / Material[][], parent-local, scale 1,
    /// the coarsest LOD per zone key; null = not in memory, FAP queues one paced rebake and raises EbmApi.RaiseFarBakeReady(name) when it
    /// lands, on Set / SetMaterialized / Remove). On this side a VA_BakeParent ZDO enters the digest as a piece whose "prefab" is a BAKE ID
    /// (the stable hash of "VA_BakeParent|" + cache name, kept in a name table here); FarBuildingSources makes that id's FarSource from the
    /// FAP arrays (opaque / cutout materials only, read once like any prefab mesh, never written to the disk mesh cache), and the merge draws
    /// it like any other piece. A bake id whose name this session has not seen yet (a digest from disk) waits; the next scan of its zone
    /// names it. Nothing happens without a registration (the digest then treats VA_BakeParent as before: a piece with no far mesh).
    /// </summary>
    internal static class FarBakeSources
    {
        internal const string BakeParentPrefab = "VA_BakeParent";
        internal static readonly int BakeParentHash = BakeParentPrefab.GetStableHashCode();
        internal static readonly int CacheNameKey = "va_bp_cache_name".GetStableHashCode();   // VABakeParentPrefab.ZdoKeyCacheName
        private const string IdPrefix = "VA_BakeParent|";

        private static Func<string, Mesh[]> s_meshes;
        private static Func<string, Material[][]> s_materials;
        private static readonly Dictionary<int, string> s_names = new Dictionary<int, string>();        // bake id -> cache name
        private static readonly Dictionary<string, int> s_ids = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<int> s_waiting = new HashSet<int>();                          // bake ids whose source waits for FAP's Ready
        internal static event Action<int> BakeReady;                                                  // bake id, main thread
        private static bool s_said;
        internal static int Registered => s_names.Count;
        internal static int Waiting => s_waiting.Count;

        internal static bool Available => s_meshes != null && s_materials != null;

        static FarBakeSources() { EbmFarWorldEvents.WorldClosed += Clear; }   // a new world starts with an empty name table (bake ids are per world)

        internal static void Register(Func<string, Mesh[]> meshes, Func<string, Material[][]> materials)
        {
            s_meshes = meshes; s_materials = materials;
            if (Available && !s_said) { s_said = true; EasyBakeLog.Info($"{FarBuildings.Log} baked blueprints (VA_BakeParent) are drawn far from FAP's bake meshes (EbmApi.RegisterFarBakeSource)"); }
        }

        /// <summary>The bake id for a VA_BakeParent ZDO (0 when it has no cache name or nothing is registered).</summary>
        internal static int IdFor(ZDO zdo)
        {
            if (!Available || zdo == null) return 0;
            string name = zdo.GetString(CacheNameKey, "");
            if (string.IsNullOrEmpty(name)) return 0;
            if (!s_ids.TryGetValue(name, out int id))
            {
                id = (IdPrefix + name).GetStableHashCode();
                if (id == 0) id = 1;
                s_ids[name] = id; s_names[id] = name;
            }
            return id;
        }

        internal static bool IsBake(int id) => s_names.ContainsKey(id);
        internal static bool TryGetName(int id, out string name) => s_names.TryGetValue(id, out name);

        /// <summary>FAP's parts for a bake id, or null while they are not in memory (then the id is marked waiting).</summary>
        internal static bool TryGetParts(int id, out Mesh[] meshes, out Material[][] materials)
        {
            meshes = null; materials = null;
            if (!Available || !s_names.TryGetValue(id, out string name)) return false;
            try { meshes = s_meshes(name); materials = s_materials(name); }
            catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} bake source '{name}' threw in FAP: {e.Message}"); meshes = null; materials = null; }
            if (meshes == null || materials == null || meshes.Length == 0 || materials.Length != meshes.Length) { s_waiting.Add(id); meshes = null; materials = null; return false; }
            s_waiting.Remove(id);
            return true;
        }

        /// <summary>EbmApi.RaiseFarBakeReady: FAP says the bake landed, changed or went; the far source for it reads again.</summary>
        internal static void Ready(string name)
        {
            if (string.IsNullOrEmpty(name) || !s_ids.TryGetValue(name, out int id)) return;
            s_waiting.Remove(id);
            var ev = BakeReady; if (ev == null) return;
            try { ev(id); } catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} bake-ready handler threw for '{name}': {e.Message}"); }
        }

        internal static void Clear() { s_names.Clear(); s_ids.Clear(); s_waiting.Clear(); }
    }
}
