using System;
using System.Reflection;
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
            // 1.2.157 ([visual], Fire 10-31 pick (a)): the vanilla shader index is built here, on the loading screen, instead of inside the
            // first in-world CreateObject that meets a modded piece (59 ms in Fire's 10-31 log, inside a 217 ms frame). Same data.
            try { IndexVanillaShaders(); EasyBakeLog.Debug("[EBM] shader index built on the loading screen (ZNetScene.Awake)"); }
            catch (Exception ex) { EasyBakeLog.Debug($"[EBM] shader index at load failed ({ex.Message}); it is built on first use instead"); s_vanillaShaders = null; s_vanillaKeywords = null; }
            try { LearnSwapsUpFront("ZNetScene.Awake", null); } catch (Exception ex) { EasyBakeLog.Debug("[EBM] up-front swap learning failed: " + ex.Message); }
        }

        // 1.2.187 (lead 11-08, 0107u: zone (26,4)'s cached construct refused stone_wall_1x1_triangular on 'Vedr/Piece' 190 lines before EBM first SAW
        // a Vedr swap and learned the mapping; the cache was thrown away and the zone rebaked): the snow-swap mapping is learned up front from the snow
        // mods themselves, gated on their LIVE flag (re-read every attempt, never ApiVersion alone): Vedr's VedrApi.PieceShaderActive for 'Vedr/Piece'
        // (Vedr PieceShader.ShaderName), FTS's FiresPieceShaderLoader.Enabled + EnsureLoaded() for its Custom/FiresPiece. Both swap the game's
        // Custom/Piece (and solid Standard) in place, so the swap shader is judged with those shaders' keywords, as 1.2.182 does once it sees a swap.
        // Tried at the ZNetScene.Awake index (Vedr's driver may not have switched its shader on yet there) and again at the moment the gate meets an
        // unknown shader of that name, which is where the race bit. Without the APIs, or with the flags off: learn-on-first-sight as before.
        private static readonly string[] SwapSources = { "Custom/Piece", "Standard" };

        // 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the snow mods PUSH their swap through EbmApi.RegisterPieceShaderSwap(owner, shader
        // name, live flag, optional loader) instead of EBM looking up VedrApi / FiresPieceShaderLoader by name. Nothing registered = learn-on-first-
        // sight, exactly as without those mods.
        private sealed class SwapSource { public string Owner, ShaderName; public Func<bool> Active; public Func<Shader> Ensure; }
        private static readonly List<SwapSource> s_swapSources = new List<SwapSource>(2);

        internal static void RegisterSwap(string owner, string shaderName, Func<bool> active, Func<Shader> ensureLoaded)
        {
            if (string.IsNullOrEmpty(owner)) return;
            s_swapSources.RemoveAll(s => s.Owner == owner);
            if (!string.IsNullOrEmpty(shaderName) && active != null)
            {
                s_swapSources.Add(new SwapSource { Owner = owner, ShaderName = shaderName, Active = active, Ensure = ensureLoaded });
                EasyBakeLog.Debug($"[EBM] up-front swap learning: {owner} registered '{shaderName}'");
                try { LearnSwapsUpFront("registration", null); } catch { }
            }
        }

        private static bool IsRegisteredSwapName(string name)
        {
            for (int i = 0; i < s_swapSources.Count; i++) if (s_swapSources[i].ShaderName == name) return true;
            return false;
        }

        // seen: a shader the gate has in hand (a source that hands no shader object out). True when a new swap shader was learned.
        private static bool LearnSwapsUpFront(string where, Shader seen)
        {
            if (s_vanillaShaders == null) return false;
            bool learned = false;
            for (int i = 0; i < s_swapSources.Count; i++)
            {
                var src = s_swapSources[i];
                bool on;
                try { on = src.Active(); } catch (Exception ex) { EasyBakeLog.Debug($"[EBM] {src.Owner}'s swap flag threw: {ex.Message}"); on = false; }
                if (!on) continue;
                Shader shader = null;
                if (src.Ensure != null) { try { shader = src.Ensure(); } catch (Exception ex) { EasyBakeLog.Debug($"[EBM] {src.Owner}'s shader loader threw: {ex.Message}"); } }
                if (shader == null) shader = seen != null && seen.name == src.ShaderName ? seen : Shader.Find(src.ShaderName);
                if (shader != null) learned |= LearnSwap(shader, src.Owner + " (registered swap)", where);
            }
            if (learned) DropRefusalsForNewSwap();
            return learned;
        }

        private static bool LearnSwap(Shader swap, string owner, string where)
        {
            if (s_swapShaders.ContainsKey(swap)) return false;
            var keywords = new HashSet<string>(StringComparer.Ordinal);
            var from = new List<string>();
            foreach (var game in s_vanillaShaders)
            {
                if (game == null || Array.IndexOf(SwapSources, game.name) < 0) continue;
                foreach (string k in s_vanillaKeywords[game]) keywords.Add(k);
                from.Add(game.name);
            }
            if (from.Count == 0) return false;
            s_swapShaders[swap] = keywords;
            EasyBakeLog.Debug($"[EBM] modded-prefab check: shader '{swap.name}' learned up front from {owner} at {where}: materials on it are judged as the game's {string.Join(" / ", from.ToArray())}");
            return true;
        }

        private static void DropRefusalsForNewSwap()
        {
            var refused = new List<int>();
            foreach (var entry in s_refusals) if (entry.Value != null) refused.Add(entry.Key);
            foreach (int hash in refused) s_refusals.Remove(hash);
            int forgotten = InstanceDefinitionCache.ForgetRejections();
            EasyBakeLog.Debug($"[EBM] new swap shader: {refused.Count} refused prefab(s) and {forgotten} rejected instancing definition(s) will be judged again");
        }

        // 1.2.141 ([visual], 10-28 stall ledger: 1.2.140's prewarm coroutine was the TOP site, 37,664 ms over 658 stall frames, "checked 1313
        // piece prefab(s) in 37674 ms of work over 662 frame(s) (93.0 s)"): single steps ran ~57 ms, far over its 3 ms budget, and it
        // checked every piece prefab when login only ever needs the few in view (71 ms lazily in round 45). Removed: the shader index
        // and the prefab checks are lazy again, as in 1.2.139.

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
            // 1.2.257: a networked object's ZDO prefab hash IS GetPrefabName(piece).GetStableHashCode(), so a cached verdict is found without
            // making the name string; the name is read only on a miss (once per prefab) or for an object with no ZDO, as before.
            var view = piece.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo != null && s_refusals.TryGetValue(zdo.GetPrefab(), out why)) { ZoneTracker.AllocsAvoidedNames += 2; return why != null; }
            string name = Utils.GetPrefabName(piece);
            int hash = name.GetStableHashCode();
            if (s_refusals.TryGetValue(hash, out why)) return why != null;
            long gateStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return RefusesUncached(piece, name, hash, out why); }
            finally { ZoneTracker.ModdedGateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gateStart; }
        }

        private static bool RefusesUncached(GameObject piece, string name, int hash, out string why)
        {
            why = null;
            // 1.2.91 (the lead, R-1012T: "modded prefab FiresMiniFader (FiresMounts) passes the bake checks"): a creature is never
            // a static mesh, vanilla or modded. Anything carrying a Character, a BaseAI or MovementDamage (a mount, a tamed pet, a
            // monster built as a piece) stays drawn by itself, checked on the object as it stands, never by making one.
            if (piece.GetComponentInChildren<Character>(true) != null || piece.GetComponentInChildren<BaseAI>(true) != null
                || piece.GetComponentInChildren<MovementDamage>(true) != null)
            {
                why = "a creature (Character / BaseAI / MovementDamage), not a static mesh";
                s_refusals[hash] = why;
                EasyBakeLog.Debug($"[EBM] skipped {name}: {why}");
                return true;
            }
            if (s_vanilla.Contains(hash)) { s_refusals[hash] = null; return false; }   // cached: the scan above runs once per prefab

            GameObject prefab = ZNetScene.instance != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(hash) : null;
            if (prefab == null) prefab = piece;
            string mode = Mode;
            if (Allowed(name)) why = null;
            else if (mode == Bake) why = null;
            else if (mode == Skip) why = "a modded prefab ([Batching] ModdedPrefabs = Skip)";
            else why = WhyUnsafe(prefab);
            s_refusals[hash] = why;
            // 1.2.143 (10-28 perf: each new modded prefab cost 60-110 ms here): the mod's name is only for this log line, so it is
            // worked out only when the line is actually written
            if (EasyBakeLog.DebugOn())
            {
                if (why != null) EasyBakeLog.Debug($"[EBM] skipped {name} ({SourceOf(prefab)}): {why}");
                else EasyBakeLog.Debug($"[EBM] modded prefab {name} ({SourceOf(prefab)}) passes the bake checks");
            }
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
                    // 1.2.182: a material a snow swap moved off a game shader is judged as that game shader; a swap shader seen on a material
                    // EBM did not watch (the snow mod's own wake path) is judged against the keywords of every game shader it replaced
                    HashSet<string> known;
                    if (s_swappedFrom.TryGetValue(material, out var from)) known = s_vanillaKeywords[from];
                    else if (s_vanillaShaders.Contains(shader)) known = s_vanillaKeywords[shader];
                    else if (!s_swapShaders.TryGetValue(shader, out known)
                             && !(IsRegisteredSwapName(shader.name)   // 1.2.187: ask the snow mod now, before refusing (1.2.210: a registered swap)
                                  && LearnSwapsUpFront("first judgement", shader) && s_swapShaders.TryGetValue(shader, out known)))
                        return $"material '{material.name}' draws with shader '{shader.name}', not one the game's own pieces use";
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

        // 1.2.182 (lead 11-07, snow_proof3: Arch_VAMeadowsArch refused after Vedr rebound its Custom/Piece materials to Vedr/Piece, 21 modded prefabs
        // skipped, cached zones thrown away): EBM asks the snow mod (Vedr's ApplyPieceShader, or FTS's ApplyToObject) to swap a prefab's shaders in place.
        // BeforeSwap takes the prefab's verdict on its own materials first and notes each material's shader; AfterSwap records, for every material moved
        // off a game shader, which one it was. The swap shader then counts as the game shader it stands in for. Learning a new swap shader drops the
        // cached refusals once, so prefabs judged before it was known are judged again.
        private static readonly Dictionary<Material, Shader> s_swappedFrom = new Dictionary<Material, Shader>();
        private static readonly Dictionary<Shader, HashSet<string>> s_swapShaders = new Dictionary<Shader, HashSet<string>>();
        private static readonly List<MeshRenderer> s_swapRends = new List<MeshRenderer>();
        private static readonly List<Material> s_snapMats = new List<Material>();
        private static readonly List<Shader> s_snapShaders = new List<Shader>();

        internal static void BeforeSwap(GameObject prefab)
        {
            s_snapMats.Clear(); s_snapShaders.Clear();
            if (prefab == null) return;
            Refuses(prefab);
            prefab.GetComponentsInChildren(true, s_swapRends);
            foreach (var renderer in s_swapRends)
            {
                if (renderer == null) continue;
                renderer.GetSharedMaterials(s_slots);
                foreach (var material in s_slots)
                {
                    if (material == null || material.shader == null) continue;
                    s_snapMats.Add(material); s_snapShaders.Add(material.shader);
                }
            }
            s_swapRends.Clear();
        }

        internal static void AfterSwap(GameObject prefab)
        {
            bool learned = false;
            try
            {
                if (s_snapMats.Count == 0) return;
                IndexVanillaShaders();
                for (int i = 0; i < s_snapMats.Count; i++)
                {
                    var material = s_snapMats[i];
                    var before = s_snapShaders[i];
                    var after = material != null ? material.shader : null;
                    if (after == null || after == before || s_swappedFrom.ContainsKey(material) || !s_vanillaShaders.Contains(before)) continue;
                    s_swappedFrom[material] = before;
                    if (!s_swapShaders.TryGetValue(after, out var keywords))
                    {
                        s_swapShaders[after] = keywords = new HashSet<string>(StringComparer.Ordinal);
                        learned = true;
                        EasyBakeLog.Info($"[EBM] modded-prefab check: shader '{after.name}' is a snow swap of the game's '{before.name}' (first seen on {(prefab != null ? prefab.name : "?")}); materials on it are judged as the game shader they replaced");
                    }
                    foreach (string k in s_vanillaKeywords[before]) keywords.Add(k);
                }
            }
            catch (Exception ex) { EasyBakeLog.Debug("[EBM] swap record failed: " + ex.Message); }
            finally { s_snapMats.Clear(); s_snapShaders.Clear(); }
            if (!learned) return;
            DropRefusalsForNewSwap();
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
        // 1.2.143 (10-28 perf, the CreateObject skip stall: each new modded prefab cost 60-110 ms, two AccessTools.TypeByName scans over
        // every loaded assembly per prefab): the FAP / VA source lookups are resolved ONCE per session (absent is remembered too).
        // 1.2.210 ([ghost]): the prefab-source lookup is PUSHED (EbmApi.RegisterPrefabSourceLookup, FAP: its SourceBundleOf), never looked up by
        // name. Nothing registered = the component-assembly answer below, as without FAP.
        private static Func<string, string> s_sourceBundleOf;
        internal static void RegisterSourceLookup(Func<string, string> sourceBundleOf)
        {
            s_sourceBundleOf = sourceBundleOf;
            EasyBakeLog.Debug(sourceBundleOf != null ? "[EBM] prefab source lookup registered (bundle names for modded prefabs)" : "[EBM] prefab source lookup withdrawn");
        }

        private static string SourceOf(GameObject prefab)
        {
            var lookup = s_sourceBundleOf;
            if (lookup != null)
            {
                try { string bundle = lookup(prefab.name); if (!string.IsNullOrEmpty(bundle)) return "bundle " + bundle; }
                catch (Exception ex) { EasyBakeLog.Debug("[EBM] the registered prefab source lookup threw (" + ex.Message + "): it is dropped for the session"); s_sourceBundleOf = null; }
            }
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
