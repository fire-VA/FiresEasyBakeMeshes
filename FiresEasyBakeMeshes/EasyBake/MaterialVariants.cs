using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.88. Vanilla has two components that swap a renderer's materials after the object is created:
    //   MaterialVariationWorld  on its first Update after Location.GetZoneLocation finds the zone's location, by dungeon theme
    //                           or biome (mr.materials = variation.m_materials), then disables itself
    //   MaterialVariation       a per-piece variant stored in the ZDO ("MatVar<i>"), checked 5 times 0.2 s apart
    // Both assign through Renderer.materials, which gives the renderer its own "<name> (Instance)" copies of EVERY slot.
    // Before 1.2.88 the bake kept those copies:
    //   - every copy was its own batch (BatchKey compares materials by reference)
    //   - the zone cache saved the copy's name, which no prefab carries, so every such zone was thrown away and baked
    //     fresh on every login
    //   - a bake that came before the swap kept the prefab look
    // So: bake the shared material the copy was made from, and hold a zone's bake while a swap is still due (at most
    // WaitLimitSeconds).
    internal static class MaterialVariants
    {
        internal const float WaitLimitSeconds = 10f;
        private const string InstanceSuffix = " (Instance)";

        private static readonly AccessTools.FieldRef<MaterialVariation, bool> IsSet =
            AccessTools.FieldRefAccess<MaterialVariation, bool>("m_isSet");
        private static readonly AccessTools.FieldRef<MaterialVariation, int> Checks =
            AccessTools.FieldRefAccess<MaterialVariation, int>("m_checks");

        private static readonly List<MaterialVariationWorld> s_worldScratch = new List<MaterialVariationWorld>();
        private static readonly List<MaterialVariation> s_pieceScratch = new List<MaterialVariation>();

        // The shared material an "(Instance)" copy on this renderer was made from. Only renderers under a variation component
        // are mapped: a copy anywhere else may carry a tint another mod set on it, and the shared material would lose that.
        // Returns the copy itself when there is no confident source.
        internal static Material SharedSource(MeshRenderer mr, Material material)
        {
            if (material == null || !material.name.EndsWith(InstanceSuffix, StringComparison.Ordinal)) return material;
            string baseName = StripInstance(material.name);

            mr.GetComponentsInParent(true, s_worldScratch);
            for (int w = 0; w < s_worldScratch.Count; w++)
            {
                var variations = s_worldScratch[w].m_variations;
                if (variations == null) continue;
                for (int v = 0; v < variations.Count; v++)
                {
                    Material found = Match(variations[v].m_materials, baseName, material);
                    if (found != null) return found;
                }
            }

            mr.GetComponents(s_pieceScratch);
            bool underPiece = s_pieceScratch.Count > 0;
            for (int p = 0; p < s_pieceScratch.Count; p++)
            {
                var entries = s_pieceScratch[p].m_materials;
                if (entries == null) continue;
                for (int e = 0; e < entries.Count; e++)
                {
                    Material src = entries[e]?.m_material;
                    if (SameLook(src, baseName, material)) return src;
                }
            }
            if (s_worldScratch.Count == 0 && !underPiece) return material;

            // A slot the variation did not choose was copied too: its source is the prefab's own material.
            Material registered = MeshCacheStore.FindMaterial(MeshCacheStore.MaterialKey(material, baseName));
            return registered != null && SameLook(registered, baseName, material) ? registered : material;
        }

        // Every material a prefab's variation components can assign, for the cache's material registry.
        internal static IEnumerable<Material> VariationMaterials(GameObject root)
        {
            foreach (var world in root.GetComponentsInChildren<MaterialVariationWorld>(true))
            {
                if (world.m_variations == null) continue;
                foreach (var variation in world.m_variations)
                    if (variation.m_materials != null)
                        foreach (var m in variation.m_materials)
                            if (m != null) yield return m;
            }
            foreach (var piece in root.GetComponentsInChildren<MaterialVariation>(true))
            {
                if (piece.m_materials == null) continue;
                foreach (var entry in piece.m_materials)
                    if (entry?.m_material != null) yield return entry.m_material;
            }
        }

        // True while a variation component under this piece has yet to make its swap.
        internal static bool SwapPending(Component piece)
        {
            if (piece == null) return false;
            piece.GetComponentsInChildren(false, s_worldScratch);
            for (int i = 0; i < s_worldScratch.Count; i++)
                if (s_worldScratch[i].enabled) return true;
            piece.GetComponentsInChildren(false, s_pieceScratch);
            for (int i = 0; i < s_pieceScratch.Count; i++)
            {
                var mv = s_pieceScratch[i];
                if (!IsSet(mv) && Checks(mv) < 5) return true;
            }
            return false;
        }

        internal static string StripInstance(string name)
        {
            while (name.EndsWith(InstanceSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - InstanceSuffix.Length);
            return name;
        }

        private static Material Match(Material[] candidates, string baseName, Material copy)
        {
            if (candidates == null) return null;
            for (int i = 0; i < candidates.Length; i++)
                if (SameLook(candidates[i], baseName, copy)) return candidates[i];
            return null;
        }

        private static bool SameLook(Material src, string baseName, Material copy) =>
            src != null && src.name == baseName && src.shader == copy.shader;
    }
}
