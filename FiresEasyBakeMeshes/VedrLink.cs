using System;
using System.Collections.Generic;

namespace FiresEasyBakeMeshes
{
    // 1.2.177 (Fire 11-07: snow buildup from the season mod, no Core needed). 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the season
    // mod PUSHES these values through EbmApi (SetSnowBuildingBiomeMask, SetPieceSnowCap, RaiseVisualsChanged, the vegetation clone events);
    // EBM looks nobody up. Nothing pushed = every answer is the "season mod absent" answer of before: no snow-building biome, caps undecided,
    // no season events, clones not followed.
    internal static class VedrLink
    {
        // ---- snow on buildings: the biome mask (0 outside winter or with snow on buildings off)
        private static int s_mask;
        private static bool s_maskPushed;
        internal static bool Present => s_maskPushed || s_capsPushed;
        internal static int SnowBuildingBiomeMask => s_mask;
        internal static bool IsSnowBuildingBiome(Heightmap.Biome biome) => s_mask != 0 && (s_mask & (int)biome) != 0;

        internal static void PushBiomeMask(int mask)
        {
            if (!s_maskPushed) { s_maskPushed = true; EasyBakeLog.Info($"[Snow] snow-on-buildings biome mask pushed by the season mod (0x{mask:X})"); }
            s_mask = mask;
        }

        // ---- snow caps per prefab: 1 gets a cap when upright, 0 never, -1 undecided (unknown = -1)
        private static readonly Dictionary<string, int> s_caps = new Dictionary<string, int>(StringComparer.Ordinal);
        private static bool s_capsPushed;
        private static int s_capRevision;
        internal static bool HasCapFor => s_capsPushed;
        internal static int PieceSnowCapRevision => s_capRevision;
        internal static int PieceSnowCapFor(string prefab)
            => prefab != null && s_caps.TryGetValue(prefab, out int v) ? (v > 0 ? 1 : v == 0 ? 0 : -1) : -1;

        internal static void PushCap(string prefab, int capFor)
        {
            if (string.IsNullOrEmpty(prefab)) return;
            s_capsPushed = true;
            int v = capFor > 0 ? 1 : capFor == 0 ? 0 : -1;
            if (s_caps.TryGetValue(prefab, out int old) && old == v) return;
            if (s_caps.Count < 8192 || s_caps.ContainsKey(prefab)) s_caps[prefab] = v;   // bounded: one entry per piece prefab
            s_capRevision++;
        }

        // ---- the season mod's own change event (season or stage changed, or its snow masks changed)
        private static Action<int, int> s_visuals;
        internal static void OnVisualsChanged(Action<int, int> handler) { s_visuals += handler; }
        internal static void RaiseVisualsChanged(int season, int stage)
        {
            var h = s_visuals;
            if (h == null) return;
            foreach (Action<int, int> one in h.GetInvocationList())
            {
                try { one(season, stage); } catch (Exception ex) { EasyBakeLog.Warn("[EbmApi] a season-change handler threw: " + ex.Message); }
            }
        }

        // ---- vegetation clones: EBM announces them (EbmApi.VegetationCloneCreated / Released); the season mod follows them by subscribing
        internal static bool RegisterVegetationClone(UnityEngine.Material clone, UnityEngine.Material source) => EbmApi.AnnounceVegetationClone(clone, source);
        internal static void UnregisterVegetationClone(UnityEngine.Material clone) => EbmApi.AnnounceVegetationCloneReleased(clone);
    }
}
