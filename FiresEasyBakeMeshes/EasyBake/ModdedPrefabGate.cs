using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Which prefabs EBM may bake (1.2.85; an external report via Fire, 10-01: "your mod breaks all the textures in my mod";
    // Fire: bake other mods' pieces when it can be done safely, skip the rest).
    //
    // Vanilla prefabs (the ones in ZNetScene's own list when the scene wakes, before any mod adds to it) always may. Another
    // mod's prefab may only when everything it draws looks the same merged or instanced as it does on its own:
    //   - every material uses a shader the game's own prefabs use (that very Shader object, not a bundle's copy of the name),
    //   - with no keyword the game never sets on that shader,
    //   - no texture array, 3D texture or cubemap array among its textures,
    //   - and no per-object material values (a MaterialPropertyBlock) on the prefab's renderers.
    // Anything else stays exactly as the mod draws it, with one line per prefab:
    //   "[EBM] skipped <prefab> (<mod>): <reason>"
    // [Batching] ModdedPrefabs = Checked (the rules above, the default) | Skip (no modded prefab) | Bake (the pre-1.2.85 way),
    // and [Batching] ModdedPrefabAllowlist names prefabs that bake whatever the checks say.
    // EBM itself never changes another mod's material any more: instanced draws use EBM's own instancing copy
    // (InstanceDefinition), and the disk cache finds a material by name, shader and main texture (MeshCacheStore).
    [HarmonyPatch]
    internal static class ModdedPrefabGate
    {
        internal const string Checked = "Checked", Skip = "Skip", Bake = "Bake";

        private static HashSet<int> s_vanilla;
        private static List<GameObject> s_vanillaPrefabs;
        private static HashSet<Shader> s_vanillaShaders;
        private static Dictionary<Shader, HashSet<string>> s_vanillaKeywords;
        private static readonly Dictionary<int, string> s_refusals = new Dictionary<int, string>();
        private static HashSet<string> s_allow;
        private static string s_allowFrom;
        private static readonly List<Material> s_slots = new List<Material>();
        private static readonly List<int> s_texIds = new List<int>();

        // ZNetScene's own prefab list, as the scene serialised it, before the mods that register into it in Awake.
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void CaptureVanilla(ZNetScene __instance)
        {
            if (s_vanilla != null || __instance == null) return;
            s_vanilla = new HashSet<int>();
            s_vanillaPrefabs = new List<GameObject>();
            foreach (var list in new[] { __instance.m_prefabs, __instance.m_nonNetViewPrefabs })
            {
                if (list == null) continue;
                foreach (var prefab in list)
                {
                    if (prefab == null) continue;
                    s_vanilla.Add(prefab.name.GetStableHashCode());
                    s_vanillaPrefabs.Add(prefab);
                }
            }
            EasyBakeLog.Debug($"[EBM] {s_vanilla.Count} vanilla prefab(s) recorded before the mods add theirs; any other prefab is checked before it bakes ({Mode}).");
        }

        private static string Mode
        {
            get
            {
                string mode = FiresEasyBakeMeshesPlugin.BatchingModdedPrefabs?.Value ?? Checked;
                return mode.Equals(Skip, StringComparison.OrdinalIgnoreCase) ? Skip : mode.Equals(Bake, StringComparison.OrdinalIgnoreCase) ? Bake : Checked;
            }
        }

        internal static bool Refuses(GameObject piece) => Refuses(piece, out _);

        // True when EBM must leave this prefab to draw itself; why names the reason (logged once per prefab).
        internal static bool Refuses(GameObject piece, out string why)
        {
            why = null;
            if (piece == null || s_vanilla == null) return false;   // before ZNetScene woke nothing bakes anyway
            string name = Utils.GetPrefabName(piece);
            int hash = name.GetStableHashCode();
            if (s_vanilla.Contains(hash)) return false;
            if (s_refusals.TryGetValue(hash, out why)) return why != null;

            GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab == null) prefab = piece;
            string mode = Mode;
            if (Allowed(name)) why = null;
            else if (mode == Bake) why = null;
            else if (mode == Skip) why = "a modded prefab ([Batching] ModdedPrefabs = Skip)";
            else why = WhyUnsafe(prefab);
            s_refusals[hash] = why;
            if (why != null) EasyBakeLog.Debug($"[EBM] skipped {name} ({SourceOf(prefab)}): {why}");
            else EasyBakeLog.Debug($"[EBM] modded prefab {name} ({SourceOf(prefab)}) passes the bake checks");
            return why != null;
        }

        // The settings decide what a bake contains, so they belong in the cache's settings key.
        internal static int SettingsKey()
        {
            unchecked
            {
                int key = Mode.GetStableHashCode();
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingModdedPrefabAllowlist?.Value ?? "").GetStableHashCode();
                return key;
            }
        }

        private static bool Allowed(string name)
        {
            string raw = FiresEasyBakeMeshesPlugin.BatchingModdedPrefabAllowlist?.Value ?? "";
            if (s_allow == null || s_allowFrom != raw)
            {
                s_allowFrom = raw;
                s_allow = new HashSet<string>(raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            }
            return s_allow.Contains(name);
        }

        private static string WhyUnsafe(GameObject prefab)
        {
            IndexVanillaShaders();
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (renderer == null) continue;   // only MeshRenderers are ever baked or instanced
                if (renderer.HasPropertyBlock()) return $"'{renderer.name}' carries per-object material values (a property block)";
                renderer.GetSharedMaterials(s_slots);
                foreach (var material in s_slots)
                {
                    if (material == null) return $"'{renderer.name}' has an empty material slot";
                    Shader shader = material.shader;
                    if (shader == null) return $"material '{material.name}' has no shader";
                    if (!s_vanillaShaders.Contains(shader))
                        return $"material '{material.name}' draws with shader '{shader.name}', not one the game's own pieces use";
                    var known = s_vanillaKeywords[shader];
                    foreach (string keyword in material.shaderKeywords)
                        if (!known.Contains(keyword)) return $"material '{material.name}' sets keyword {keyword}, which the game never sets on '{shader.name}'";
                    material.GetTexturePropertyNameIDs(s_texIds);
                    foreach (int id in s_texIds)
                    {
                        Texture texture = material.GetTexture(id);
                        if (texture is Texture2DArray || texture is Texture3D || texture is CubemapArray)
                            return $"material '{material.name}' uses a {texture.GetType().Name} ('{texture.name}')";
                    }
                }
            }
            return null;
        }

        // Every shader the vanilla prefabs draw with, and the keywords they set on each; built once, on the first modded prefab.
        private static void IndexVanillaShaders()
        {
            if (s_vanillaShaders != null) return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            s_vanillaShaders = new HashSet<Shader>();
            s_vanillaKeywords = new Dictionary<Shader, HashSet<string>>();
            foreach (var prefab in s_vanillaPrefabs ?? new List<GameObject>())
            {
                if (prefab == null) continue;
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null) continue;
                    renderer.GetSharedMaterials(s_slots);
                    foreach (var material in s_slots)
                    {
                        if (material == null || material.shader == null) continue;
                        s_vanillaShaders.Add(material.shader);
                        if (!s_vanillaKeywords.TryGetValue(material.shader, out var keywords)) s_vanillaKeywords[material.shader] = keywords = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string keyword in material.shaderKeywords) keywords.Add(keyword);
                    }
                }
            }
            EasyBakeLog.Debug($"[EBM] indexed {s_vanillaShaders.Count} shader(s) the game's own prefabs use, in {clock.ElapsedMilliseconds} ms");
        }

        // The mod a prefab comes from, for the skip line: FAP's bundle when FAP knows it (FapExternalPrefabs.SourceBundleOf from
        // FAP 1.1.157, else VAExternalBundleLoader.TryGetSourceBundle; both read by name so EBM keeps no reference to FAP), else
        // the first component from an assembly that isn't the game's or
        // Unity's, else "a mod".
        private static string SourceOf(GameObject prefab)
        {
            try
            {
                var api = AccessTools.TypeByName("VerdantsAscent.Api.FapExternalPrefabs");
                var method = api != null ? AccessTools.Method(api, "SourceBundleOf", new[] { typeof(string) }) : null;
                if (method != null && method.Invoke(null, new object[] { prefab.name }) is string bundle && bundle.Length > 0) return "bundle " + bundle;
                // FAP before 1.1.157: VAExternalBundleLoader.TryGetSourceBundle(string, out string), public static.
                var loader = AccessTools.TypeByName("VerdantsAscent.Managers.VAExternalBundleLoader");
                var tryGet = loader != null ? AccessTools.Method(loader, "TryGetSourceBundle", new[] { typeof(string), typeof(string).MakeByRefType() }) : null;
                if (tryGet != null)
                {
                    var args = new object[] { prefab.name, null };
                    if (tryGet.Invoke(null, args) is bool found && found && args[1] is string fromLoader && fromLoader.Length > 0) return "bundle " + fromLoader;
                }
            }
            catch { }
            foreach (var component in prefab.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string assembly = component.GetType().Assembly.GetName().Name;
                if (assembly.StartsWith("UnityEngine", StringComparison.Ordinal) || assembly == "assembly_valheim" || assembly == "Unity.TextMeshPro") continue;
                return assembly;
            }
            return "a mod";
        }
    }
}
