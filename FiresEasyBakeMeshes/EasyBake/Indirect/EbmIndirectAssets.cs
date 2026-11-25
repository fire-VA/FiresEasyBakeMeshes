using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.203: EBM's own indirect bundle ("ebm_indirect", next to the DLL): the cull compute (EbmIndirectCull) and EBM's procedural-instancing
    // shader variants. Vedr's variants come from Vedr's own bundle, found by name. A material is drawn indirect only through a clone of
    // itself on the variant shader: the source material (and so every live renderer of the prefab) is never touched. Loaded once, lazily,
    // the first frame the key is on; a missing bundle or kernel turns the path off with one Info line and EBM draws as before.
    internal static class EbmIndirectAssets
    {
        internal const string BundleFile = "ebm_indirect";
        internal const string BundleFileLinux = "ebm_indirect_linux";   // 1.2.279: StandaloneLinux64 build (Vulkan + GLCore)
        internal static string LoadedFile = "none";

        // Source shader name -> EBM's own procedural variant in the ebm_indirect bundle (Fire 11-08: performance code lives in our 3 performance mods).
        // FiresPiece / Vedr wrappers include that mod's own surface code (.cginc) at build time, so the look never drifts; FTS and Vedr ship unchanged.
        // Custom/Piece is listed too: on a rig without TossinShade/Vedr it is what the pieces draw with.
        private static readonly (string source, string variant)[] VariantNames =
        {
            ("Custom/Vegetation", "EBM/VegetationIndirect"),
            ("Custom/Creature", "EBM/CreatureIndirect"),   // 1.2.314 [wishbone]; only with [Optimize] IndirectCreatureVariant (VariantFor)
            ("Custom/StaticRock", "EBM/StaticRockIndirect"),
            // 1.2.204 (Fire: "the new ebm shader needs to do everything tossin shades shader and vedrs shader does"): one EBM/Piece for all three
            ("Custom/FiresPiece", "EBM/Piece"),
            ("Custom/Piece", "EBM/Piece"),
            ("Vedr/Piece", "EBM/Piece"),
            // 1.2.209 ([ghost], VPO parity item 5): Standard-material pieces and dropped items
            ("Standard", "EBM/StandardIndirect"),
            ("Custom/Rug", "EBM/RugIndirect"),   // 1.2.221 [seasons]; only with [Optimize] IndirectRugVariant (VariantFor)
            ("Standard (Specular setup)", "EBM/StandardSpecularIndirect"),
            ("Custom/FlowOpaque", "EBM/FlowOpaqueIndirect"),   // 1.2.301 [visual]: Ashlands lava, ported from our FiresFlowOpaque; only with [Optimize] IndirectLavaVariant (VariantFor)
        };
        internal static ComputeShader Cull { get; private set; }
        internal static int KernelClear, KernelCull;
        // G3 ([wishbone] 11-08): EbmCullGrouped, the same cull with one global atomic per batch per thread group; -1 = an older bundle without it
        internal static int KernelCullGrouped = -1;
        internal static int KernelCopyGrow = -1;   // 1.2.213: GPU-side instance buffer growth (-1 = an older bundle: the CPU re-upload as before)
        internal static readonly int IdCopySrcInst = Shader.PropertyToID("_EbmCopySrcInst"), IdCopyDstInst = Shader.PropertyToID("_EbmCopyDstInst"), IdCopySrcU = Shader.PropertyToID("_EbmCopySrcU"), IdCopyDstU = Shader.PropertyToID("_EbmCopyDstU"), IdCopySrcF = Shader.PropertyToID("_EbmCopySrcF"), IdCopyDstF = Shader.PropertyToID("_EbmCopyDstF"), IdCopyCount = Shader.PropertyToID("_EbmCopyCount");
        // the cull kernel every dispatch site uses (and binds its buffers for): grouped while [Optimize] IndirectCullGrouped and the bundle has it
        internal static int CullKernel => KernelCullGrouped >= 0 && FiresEasyBakeMeshesPlugin.IndirectCullGrouped != null && FiresEasyBakeMeshesPlugin.IndirectCullGrouped.Value
            ? KernelCullGrouped : KernelCull;
        internal static int KernelCullCascades = -1;   // 1.2.211 ([saddles] G6 v2): + EbmCullCascades (-1 = an older bundle)
        // 1.2.209 ([saddles]): the shadow receiver map kernels (EbmShadowReceivers.compute); null = an older bundle, the receiver test stays off
        internal static ComputeShader Receivers { get; private set; }
        internal static int KernelRxClear, KernelRxTiles, KernelRxScatter, KernelRxMips;   // 1.2.211: + EbmRxTiles (v5)

        internal static readonly int IdInstances = Shader.PropertyToID("_EbmInstances");
        internal static readonly int IdInstBatch = Shader.PropertyToID("_EbmInstBatch");
        internal static readonly int IdInstLod = Shader.PropertyToID("_EbmInstLod");   // 1.2.206
        internal static readonly int IdDrawStart = Shader.PropertyToID("_EbmDrawStart");   // 1.2.209
        internal static readonly int IdViewMode = Shader.PropertyToID("_EbmViewMode");
        internal static readonly int IdCascadeSpheres = Shader.PropertyToID("_EbmCascadeSpheres");   // 1.2.208
        internal static readonly int IdCascadeCount = Shader.PropertyToID("_EbmCascadeCount");
        internal static readonly int IdLightDir = Shader.PropertyToID("_EbmLightDir");
        internal static readonly int IdBatches = Shader.PropertyToID("_EbmBatches");
        internal static readonly int IdVisible = Shader.PropertyToID("_EbmVisible");
        internal static readonly int IdVisibleBase = Shader.PropertyToID("_EbmVisibleBase");
        internal static readonly int IdArgs = Shader.PropertyToID("_EbmArgs");
        internal static readonly int IdCamPlanes = Shader.PropertyToID("_EbmCamPlanes");
        internal static readonly int IdShadowPlanes = Shader.PropertyToID("_EbmShadowPlanes");
        internal static readonly int IdShadowPlaneCount = Shader.PropertyToID("_EbmShadowPlaneCount");
        internal static readonly int IdCam = Shader.PropertyToID("_EbmCam");
        internal static readonly int IdSizeScale = Shader.PropertyToID("_EbmSizeScale");
        internal static readonly int IdMinScreen = Shader.PropertyToID("_EbmMinScreen");
        internal static readonly int IdInstanceCount = Shader.PropertyToID("_EbmInstanceCount");
        internal static readonly int IdBatchCount = Shader.PropertyToID("_EbmBatchCount");
        internal static readonly int IdVisibleStride = Shader.PropertyToID("_EbmVisibleStride");
        internal static readonly int IdDrawCount = Shader.PropertyToID("_EbmDrawCount");

        private static bool s_tried;
        private static string s_why = "not loaded yet";
        private static AssetBundle s_bundle;
        private static readonly Dictionary<string, Shader> s_variants = new Dictionary<string, Shader>();
        private static readonly List<string> s_droppedVariants = new List<string>();   // 1.2.263: bundle shaders left out (unsupported or no procedural variant)
        private static readonly Dictionary<Material, Material> s_clones = new Dictionary<Material, Material>();
        private static readonly HashSet<Material> s_vedrRegistered = new HashSet<Material>();

        // 1.2.313 ([ghost]): EBM's own terrain shader (EbmTerrainShader); null with TerrainWhy saying why
        internal const string TerrainShaderName = "EBM/Heightmap";
        internal static Shader TerrainShader { get; private set; }
        internal static string TerrainWhy { get; private set; }
        internal static Shader Terrain(out string why)
        {
            if (!s_tried) Load();
            why = TerrainShader != null ? null : (TerrainWhy ?? s_why ?? "the indirect bundle did not load");
            return TerrainShader;
        }

        // [seasons] 11-11 (EBM water STEP 1, same shape as Terrain): EBM's own water shader (EbmWater); null with WaterWhy saying why
        internal const string WaterShaderName = "EBM/Water";
        internal static Shader WaterShader { get; private set; }
        internal static string WaterWhy { get; private set; }
        internal static Shader Water(out string why)
        {
            if (!s_tried) Load();
            why = WaterShader != null ? null : (WaterWhy ?? s_why ?? "the indirect bundle did not load");
            return WaterShader;
        }

        // 1.2.388 ([visual], same shape as Water): EBM's own Mistlands mist shader (EbmMist); null with MistWhy saying why
        internal const string MistShaderName = "EBM/MistLux";
        internal static Shader MistShader { get; private set; }
        internal static string MistWhy { get; private set; }
        internal static Shader Mist(out string why)
        {
            if (!s_tried) Load();
            why = MistShader != null ? null : (MistWhy ?? s_why ?? "the indirect bundle did not load");
            return MistShader;
        }

        // [wishbone] 11-11: Custom/Creature stays on Unity unless [Optimize] IndirectCreatureVariant is on, and always when it uses what
        // EBM/CreatureIndirect does not draw: NOISEGLOW (not compiled in) and _AddRain (vanilla's rain mask needs globals we could not name
        // from the dump, Creature.shader:93665-93702). Each refusal reason is said once at Debug with the first material that hit it.
        private static readonly HashSet<string> s_creatureWhySaid = new HashSet<string>();
        internal static readonly Dictionary<string, int> CreatureRefusals = new Dictionary<string, int>();
        private static bool CreatureRefused(Material m)
        {
            string why = null;
            if (FiresEasyBakeMeshesPlugin.IndirectCreatureVariant == null || !FiresEasyBakeMeshesPlugin.IndirectCreatureVariant.Value) why = "key off";
            else if (m.IsKeywordEnabled("NOISEGLOW")) why = "noise glow";
            else if (m.IsKeywordEnabled("_ADDRAIN_ON") || (m.HasProperty("_AddRain") && m.GetFloat("_AddRain") > 0.5f)) why = "add rain";
            if (why == null) return false;
            CreatureRefusals.TryGetValue(why, out int n); CreatureRefusals[why] = n + 1;
            if (why != "key off" && s_creatureWhySaid.Add(why)) EasyBakeLog.Debug($"[Indirect] Custom/Creature material '{m.name}' stays on Unity ({why}); EBM/CreatureIndirect does not draw that");
            return true;
        }

        internal static bool Ready(out string why)
        {
            if (!s_tried) Load();
            why = s_why;
            return Cull != null;
        }

        private static void Load()
        {
            s_tried = true;
            try
            {
                string dir = Path.GetDirectoryName(typeof(EbmIndirectAssets).Assembly.Location) ?? "";
                // 1.2.279 (lead 11-11): a native Linux player loads the StandaloneLinux64 bundle (Vulkan + GLCore variants) when it ships next to the DLL;
                // Windows (and Proton / Deck through DXVK) loads ebm_indirect (D3D11 + Vulkan variants). Each shader is still checked (isSupported) below.
                string file = BundleFile;
                if (Application.platform == RuntimePlatform.LinuxPlayer && File.Exists(Path.Combine(dir, BundleFileLinux))) file = BundleFileLinux;
                else if (Application.platform == RuntimePlatform.LinuxPlayer) EasyBakeLog.Info($"[Indirect] native Linux player but no '{BundleFileLinux}' next to the DLL: trying '{BundleFile}' (its shaders are checked one by one)");
                string path = Path.Combine(dir, file);
                if (!File.Exists(path)) { s_why = $"the indirect bundle '{file}' is not next to the DLL ({dir})"; return; }
                s_bundle = AssetBundle.LoadFromFile(path);
                if (s_bundle == null) { s_why = $"the indirect bundle '{file}' did not load"; return; }
                LoadedFile = file;
                // 1.2.313 ([ghost]): EBM's own terrain shader rides in the same bundle. Taken before the indirect checks, so a GPU without the cull kernels
                // still gets it; it has no procedural-instancing variant (a plain surface shader), so the variant loop below skips it by name.
                // [seasons] 11-11: EBM's own water shader, the same way (a tessellated transparent shader, no procedural-instancing variant)
                foreach (var tsh in s_bundle.LoadAllAssets<Shader>())
                {
                    if (tsh == null) continue;
                    if (tsh.name == TerrainShaderName) { TerrainShader = tsh.isSupported ? tsh : null; TerrainWhy = tsh.isSupported ? null : "EBM/Heightmap cannot run on this graphics card / API"; }
                    else if (tsh.name == WaterShaderName) { WaterShader = tsh.isSupported ? tsh : null; WaterWhy = tsh.isSupported ? null : "EBM/Water cannot run on this graphics card / API (it needs tessellation, shader model 4.6)"; }
                    else if (tsh.name == MistShaderName) { MistShader = tsh.isSupported ? tsh : null; MistWhy = tsh.isSupported ? null : "EBM/MistLux cannot run on this graphics card / API"; }   // 1.2.388
                }
                if (MistShader == null && MistWhy == null) MistWhy = "this bundle has no EBM/MistLux";   // 1.2.388
                if (TerrainShader == null && TerrainWhy == null) TerrainWhy = "this bundle has no EBM/Heightmap";
                if (WaterShader == null && WaterWhy == null) WaterWhy = "this bundle has no EBM/Water";
                var cs = s_bundle.LoadAsset<ComputeShader>("EbmIndirectCull");
                if (cs == null) { s_why = "the indirect bundle has no EbmIndirectCull compute shader"; return; }
                // 1.2.263 (Fire 11-10: the indirect keys default ON): every check that can leave a player with invisible pieces stops the path for the session
                // instead (BeginFrame then draws through the normal instanced path, one Warning line naming why)
                if (!cs.HasKernel("EbmClear") || !cs.HasKernel("EbmCull")) { s_why = "the cull compute shader lacks its EbmClear / EbmCull kernel on this GPU"; return; }
                KernelClear = cs.FindKernel("EbmClear");
                KernelCopyGrow = cs.HasKernel("EbmCopyGrow") ? cs.FindKernel("EbmCopyGrow") : -1;   // 1.2.213
                KernelCull = cs.FindKernel("EbmCull");
                KernelCullGrouped = cs.HasKernel("EbmCullGrouped") ? cs.FindKernel("EbmCullGrouped") : -1;   // G3
                EasyBakeLog.Debug("[Indirect] cull kernel: " + (KernelCullGrouped >= 0 ? "EbmCullGrouped available ([Optimize] IndirectCullGrouped picks it)" : "this bundle has no EbmCullGrouped: EbmCull only"));
                KernelCullCascades = cs.HasKernel("EbmCullCascades") ? cs.FindKernel("EbmCullCascades") : -1;   // 1.2.211 ([saddles] G6 v2)
                // E2 ([seasons] 11-11): both cull kernels declare the depth-pyramid inputs; valid dummies now, so no pass ever dispatches them unbound
                EbmIndirect.DepthCullBindDummies(cs, KernelCull);
                EbmIndirect.DepthCullBindDummies(cs, KernelCullGrouped);
                EbmIndirect.HookDepthCull();
                foreach (var sh in s_bundle.LoadAllAssets<Shader>())
                {
                    if (sh == null || sh.name == TerrainShaderName || sh.name == WaterShaderName || sh.name == MistShaderName) continue;   // 1.2.313 / 1.2.314: terrain and water are not indirect variants (taken above)
                    // 1.2.263: a drawn variant must run here AND carry the procedural instancing keyword its draws need; one that does not is left out
                    // (its groups stay on the normal path) with a Warning. EBM's capture shader writes data, it is not drawn: kept as it is.
                    bool drawn = sh.name.IndexOf("Capture", StringComparison.OrdinalIgnoreCase) < 0;
                    if (drawn && (!sh.isSupported || !sh.keywordSpace.FindKeyword("PROCEDURAL_INSTANCING_ON").isValid))
                    {
                        s_droppedVariants.Add(sh.name);
                        EasyBakeLog.Warn($"[Indirect] shader {sh.name} {(sh.isSupported ? "has no PROCEDURAL_INSTANCING_ON variant in the bundle" : "cannot run on this graphics card")}: what it would draw stays on the normal path");
                        continue;
                    }
                    s_variants[sh.name] = sh;
                }
                if (!s_variants.ContainsKey("EBM/Piece") && !s_variants.ContainsKey("EBM/VegetationIndirect") && !s_variants.ContainsKey("EBM/StaticRockIndirect"))
                { s_why = "none of EBM's GPU-path shaders can run here (" + string.Join(", ", s_droppedVariants) + ")"; return; }
                var rx = s_bundle.LoadAsset<ComputeShader>("EbmShadowReceivers");   // optional (1.2.209)
                if (rx != null)
                {
                    try { KernelRxClear = rx.FindKernel("EbmRxClear"); KernelRxTiles = rx.FindKernel("EbmRxTiles"); KernelRxScatter = rx.FindKernel("EbmRxScatter"); KernelRxMips = rx.FindKernel("EbmRxMips"); Receivers = rx; }
                    catch (Exception e) { Receivers = null; EasyBakeLog.Info("[Indirect] the receiver kernels did not load (" + e.Message + "): casters are not receiver-tested"); }
                }
                var am = s_bundle.LoadAsset<ComputeShader>("EbmAlphaMin");   // optional (1.2.210, G1): GPU min-alpha of unreadable piece albedos
                if (am != null) { try { s_alphaKernel = am.FindKernel("EbmAlphaMin"); s_alphaMin = am; } catch { s_alphaMin = null; } }

                Cull = cs;
                s_why = null;
                EasyBakeLog.Info($"[Indirect] bundle loaded: cull compute + {s_variants.Count} shader variant(s) ({string.Join(", ", s_variants.Keys)})");
            }
            catch (Exception e) { Cull = null; s_why = "loading the indirect bundle threw " + e.GetType().Name + ": " + e.Message; }
        }

        // The clone of `source` on our procedural variant, or null when its shader has none (the group then stays on the instanced path).
        private static readonly HashSet<string> s_unsupportedSaid = new HashSet<string>();

        internal static Material VariantFor(Material source)
        {
            if (source == null || source.shader == null) return null;
            if (s_clones.TryGetValue(source, out var clone)) return clone;
            var variant = FindVariant(source.shader.name);
            if (variant != null && source.shader.name == "Custom/Rug" && (FiresEasyBakeMeshesPlugin.IndirectRugVariant == null || !FiresEasyBakeMeshesPlugin.IndirectRugVariant.Value)) variant = null;   // 1.2.221: off until Fire's look check
            if (variant != null && source.shader.name == "Custom/Creature" && CreatureRefused(source)) variant = null;   // 1.2.314 [wishbone]
            if (variant != null && source.shader.name == "Custom/FlowOpaque" && (FiresEasyBakeMeshesPlugin.IndirectLavaVariant == null || !FiresEasyBakeMeshesPlugin.IndirectLavaVariant.Value)) variant = null;   // 1.2.301: OFF until the Ashlands gate
            // VPO's lesson, our check: vegetation with the metal map has no procedural variant compiled; keep it vanilla
            if (variant != null && source.shader.name == "Custom/Vegetation" && source.IsKeywordEnabled("_USEMETALMAP_ON")) variant = null;
            if (variant != null && source.shader.name.StartsWith("Standard") && StandardRefused(source)) variant = null;   // 1.2.209: blended / parallax / detail / no-highlight Standard stays vanilla
            // 1.2.211 ([ghost]'s VPE study): a variant this GPU / API cannot run (Vulkan, some Linux players) stays vanilla, never a broken
            // clone another mod's material hook would then swap; said once per shader
            if (variant != null && !variant.isSupported)
            {
                if (s_unsupportedSaid.Add(variant.name)) EasyBakeLog.Info($"[Indirect] '{variant.name}' is not supported on this GPU / graphics API ({SystemInfo.graphicsDeviceType}); materials on '{source.shader.name}' keep EBM's instanced path");
                variant = null;
            }
            if (variant == null) { s_clones[source] = null; return null; }
            clone = new Material(source) { name = source.name + " (EBM indirect)", shader = variant, enableInstancing = true };
            clone.renderQueue = source.renderQueue;
            if (source.shader.name.StartsWith("Standard")) StandardFlags(source, clone);
            else if (variant.name == "EBM/Piece") PieceFlags(source, clone);   // 1.2.210 ([seasons], GPU study G1)
            CopyFiresKnobs(source, clone);   // any source with the TossinShade / Vedr knobs
            SyncSnowKeyword(source, clone);   // 1.2.359 ([seasons]/[lead] 11-13): the rock variant reads a keyword where vanilla has only a float
            s_clones[source] = clone;
            HookSeason();   // 1.2.342
            string sn = source.shader.name;
            if ((sn == "Custom/Vegetation" || sn == "Custom/StaticRock") && VedrLink.RegisterVegetationClone(clone, source)) s_vedrRegistered.Add(clone);
            return clone;
        }

        // 1.2.342 ([visual]; Fire's run_4 11-12: bare trunks / white rocks after winter -> summer with Vedr; [seasons]' cause): a clone is `new Material(source)`,
        // a snapshot of the source's textures and colours at cover time, and Vedr repaints the SOURCE (Custom/Vegetation / StaticRock) each season; Vedr's own
        // follow of EBM's clones needs its subscription (Vedr 0.8.243), and EBM owns its clones, so EBM re-copies them itself too: on every VedrLink visuals
        // change (season, stage, snow masks), every property both materials share, by name, except EBM's own _Ebm* knobs, goes source -> clone (same values Vedr's
        // Sync writes, so no fight). [Fixes] IndirectClonesFollowSeason (ON). One Debug line per change.
        private static bool s_seasonHooked;
        private static void HookSeason()
        {
            if (s_seasonHooked) return;
            s_seasonHooked = true;
            VedrLink.OnVisualsChanged((season, stage) => { if (FiresEasyBakeMeshesPlugin.IndirectClonesFollowSeason == null || FiresEasyBakeMeshesPlugin.IndirectClonesFollowSeason.Value) ResyncClones($"season {season} stage {stage}"); });
        }

        internal static int ResyncClones(string why)
        {
            int n = 0, props = 0;
            foreach (var kv in s_clones)
            {
                var source = kv.Key; var clone = kv.Value;
                if (source == null || clone == null) continue;
                try { props += CopyShared(source, clone); n++; }
                catch (Exception e) { EasyBakeLog.Debug($"[Indirect] clone re-sync of '{source.name}' threw: {e.Message}"); }
            }
            if (n > 0) EasyBakeLog.Debug($"[Indirect] {n} clone(s) re-synced from their source materials ({props} propert(ies); {why})");
            return n;
        }

        private static int CopyShared(Material source, Material clone)
        {
            var sh = source.shader; if (sh == null) return 0;
            int count = sh.GetPropertyCount(), copied = 0;
            for (int i = 0; i < count; i++)
            {
                string name = sh.GetPropertyName(i);
                if (name.StartsWith("_Ebm", StringComparison.Ordinal) || !clone.HasProperty(name)) continue;
                switch (sh.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        clone.SetTexture(name, source.GetTexture(name)); clone.SetTextureOffset(name, source.GetTextureOffset(name)); clone.SetTextureScale(name, source.GetTextureScale(name)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color: clone.SetColor(name, source.GetColor(name)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: clone.SetVector(name, source.GetVector(name)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range: clone.SetFloat(name, source.GetFloat(name)); break;
                    default: continue;
                }
                copied++;
            }
            if (SyncSnowKeyword(source, clone)) copied++;   // 1.2.359: every resync carries the snow state too (a winter clone stayed snowy all summer)
            return copied;
        }

        // 1.2.359 ([seasons] 11-13, [lead]): EbmStaticRock.shader draws snow from the keyword _ADDSNOW_ON ([Toggle(_ADDSNOW_ON)] _AddSnow, dynamic_branch_local),
        // but vanilla Custom/StaticRock has only the _AddSnow float (a MaterialToggle with no keyword, rip Custom_StaticRock.asset:280-283), so a clone made
        // with new Material(source) never carries it and CopyShared copied floats only: EBM-drawn rocks got no snow when Vedr set _AddSnow for winter, and a
        // clone whose keyword was ever set stayed snowy in summer. The clone's keyword follows the source float (> 0.5 = on) at creation and on every resync.
        // Returns true when the keyword changed.
        // 1.2.364 ([lead], [generator]'s grep: 132 Unity warnings "Material '<x>_lowlod' with Shader 'Custom/Piece' doesn't have a float or range property '_AddSnow'"):
        // this ran for EVERY clone, pieces too, and Material.HasProperty("_AddSnow") is true for a value saved in the material even when its shader does not declare
        // it, so GetFloat warned. Both sides are now asked through their SHADER (a Float or Range property named _AddSnow): a source shader without it (Custom/Piece)
        // or a clone variant without it (EBM/Piece) is left alone; rocks (Custom/StaticRock -> EBM/StaticRockIndirect, both declare it) work as before.
        // 1.2.434 ([visual], lead 11-16): _AddRain had the same gap. Vanilla Custom/StaticRock's rain wetness is gated on the _AddRain FLOAT
        // (VanillaShaders\StaticRock.shader frag_main: `0.5 < _AddRain` with y < 2000), EbmStaticRock.shader:133 on the keyword _ADDRAIN_ON, and nothing set it,
        // so EBM rocks never went wet in rain. Both toggles now follow the source float the same way (per shader, per property).
        private static readonly Dictionary<(Shader, string), bool> s_shaderHasFloat = new Dictionary<(Shader, string), bool>();
        private static bool ShaderHasFloat(Shader shader, string prop)
        {
            if (shader == null) return false;
            if (s_shaderHasFloat.TryGetValue((shader, prop), out bool has)) return has;
            int i = shader.FindPropertyIndex(prop);
            if (i >= 0)
            {
                var t = shader.GetPropertyType(i);
                has = t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range;
            }
            s_shaderHasFloat[(shader, prop)] = has;
            return has;
        }

        private static bool SyncToggleKeyword(Material source, Material clone, string prop, string keyword)
        {
            if (!ShaderHasFloat(source.shader, prop) || !ShaderHasFloat(clone.shader, prop)) return false;
            bool on = source.GetFloat(prop) > 0.5f;
            if (clone.IsKeywordEnabled(keyword) == on) return false;
            if (on) clone.EnableKeyword(keyword); else clone.DisableKeyword(keyword);
            clone.SetFloat(prop, on ? 1f : 0f);
            return true;
        }

        private static bool SyncSnowKeyword(Material source, Material clone)
        {
            if (source == null || clone == null) return false;
            bool snow = SyncToggleKeyword(source, clone, "_AddSnow", "_ADDSNOW_ON");
            bool rain = SyncToggleKeyword(source, clone, "_AddRain", "_ADDRAIN_ON");   // 1.2.434
            return snow || rain;
        }

        private static bool sourceShaderIsFiresPiece(Material m) =>
 m.shader != null && m.shader.name == "Custom/FiresPiece";

        // TossinShade's tuning knobs carry the same meaning under Vedr's names in EBM/Piece (no-op at their defaults)
        // 1.2.206: EBM/Piece's own knob names ([ghost] merge); a source material's TossinShade / Vedr knob values carry over (no-op at defaults)
        private static readonly (string from, string to)[] Knobs = { ("_FiresWetScale", "_EbmWetStrength"), ("_FiresWetSmoothness", "_EbmWetSmoothness"), ("_FiresSmoothnessScale", "_EbmReflectionScale"), ("_FiresMetallicScale", "_EbmMetallicScale"), ("_VedrWetSmoothness", "_EbmWetSmoothness"), ("_VedrSmoothnessScale", "_EbmReflectionScale"), ("_VedrMetallicScale", "_EbmMetallicScale") };
        private static void CopyFiresKnobs(Material source, Material clone)
        {
            foreach (var (from, to) in Knobs) if (source.HasProperty(from) && clone.HasProperty(to)) clone.SetFloat(to, source.GetFloat(from));
        }

        private static readonly string[] StandardRefusedKeywords = { "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_PARALLAXMAP", "_DETAIL_MULX2", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" };
        // 1.2.301 [Optimize] IndirectStandardMoreKeywords (OFF until gate): the specular-highlights-off / glossy-reflections-off materials get the clone too
        // (EBM/StandardIndirect now compiles both keywords); blended / parallax / detail stay vanilla
        private static bool MoreKeywordsOn => FiresEasyBakeMeshesPlugin.IndirectStandardMoreKeywords != null && FiresEasyBakeMeshesPlugin.IndirectStandardMoreKeywords.Value;
        private static bool StandardRefused(Material m)
        {
            bool more = MoreKeywordsOn;
            foreach (var k in StandardRefusedKeywords)
            {
                if (more && (k == "_SPECULARHIGHLIGHTS_OFF" || k == "_GLOSSYREFLECTIONS_OFF")) continue;
                if (m.IsKeywordEnabled(k)) return true;
            }
            return false;
        }

        // [ghost]'s EBM/Standard variants read features as floats (one keyword, EBM_ALPHATEST), set here from the source's keywords
        private static void StandardFlags(Material src, Material clone)
        {
            clone.SetFloat("_EbmUseNormalMap", src.IsKeywordEnabled("_NORMALMAP") ? 1f : 0f);
            clone.SetFloat("_EbmUseGlossMap", src.IsKeywordEnabled("_METALLICGLOSSMAP") || src.IsKeywordEnabled("_SPECGLOSSMAP") ? 1f : 0f);
            clone.SetFloat("_EbmSmoothFromAlbedoA", src.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") ? 1f : 0f);
            clone.SetFloat("_EbmUseEmission", src.IsKeywordEnabled("_EMISSION") ? 1f : 0f);
            if (src.IsKeywordEnabled("_ALPHATEST_ON")) clone.EnableKeyword("EBM_ALPHATEST"); else clone.DisableKeyword("EBM_ALPHATEST");
            // 1.2.301: the source's own lighting switches, the same keyword names Unity's BRDF reads
            if (src.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF")) clone.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); else clone.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            if (src.IsKeywordEnabled("_GLOSSYREFLECTIONS_OFF")) clone.EnableKeyword("_GLOSSYREFLECTIONS_OFF"); else clone.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
        }

        // 1.2.210 ([seasons], GPU study G1; Tools\STUDY_sections\seasons_GPU.md): EBM/Piece cuts out only under EBM_ALPHATEST. A piece material needs
        // it only when its albedo x _Color alpha can fall below _Cutoff somewhere; otherwise it draws opaque (early depth in the G-buffer, a shadow
        // caster with no texture work). The answer is never "opaque" by guess:
        //  - cutoff <= 0: nothing can cut -> opaque;
        //  - _Color.a < _Cutoff (with a texture alpha of 1 the material already cuts): cutout;
        //  - no _MainTex: the white default, alpha 1 -> opaque by the colour rule;
        //  - a texture format with no alpha channel (DXT1 RGB, BC5, RGB24 ...): alpha 1 everywhere -> opaque by the colour rule;
        //  - a readable texture: its lowest alpha over every texel (scanned once, cached per texture) decides;
        //  - anything else (an alpha channel we cannot read): cutout, as before (the safe answer).
        // One Debug census every 30 s: clones made opaque / cutout and why (the share that gained early depth).
        private static readonly Dictionary<Texture, float> s_minAlpha = new Dictionary<Texture, float>();
        private static readonly Dictionary<string, int> s_pieceWhy = new Dictionary<string, int>();
        private static int s_pieceOpaque, s_pieceCutout; private static float s_pieceSayAt;
        private static void PieceFlags(Material src, Material clone)
        {
            string why;
            bool cut = PieceNeedsCutout(src, out why);
            if (cut) { clone.EnableKeyword("EBM_ALPHATEST"); s_pieceCutout++; } else { clone.DisableKeyword("EBM_ALPHATEST"); OpaqueQueue(src, clone); s_pieceOpaque++; }
            if (cut && why.StartsWith("albedo alpha being measured")) MeasureAlpha(src.GetTexture("_MainTex"), src, clone);   // decided again when the GPU answers
            s_pieceWhy.TryGetValue(why, out int n); s_pieceWhy[why] = n + 1;
            if (s_pieceWhy.Count > 64) s_pieceWhy.Clear();
            if (Time.unscaledTime >= s_pieceSayAt)
            {
                s_pieceSayAt = Time.unscaledTime + 30f;
                var parts = new List<string>(); foreach (var kv in s_pieceWhy) parts.Add($"{kv.Key} x{kv.Value}");
                EasyBakeLog.Debug($"[Indirect] EBM/Piece clones so far: {s_pieceOpaque} opaque (early depth, no-texture shadow caster), {s_pieceCutout} cutout; why: {string.Join(", ", parts)}");
            }
        }

        // [generator]: an opaque clone of an AlphaTest-queue source draws in the Geometry queue, so it keeps the early-depth benefit
        private static void OpaqueQueue(Material src, Material clone) { if (src.renderQueue >= 2450 && src.renderQueue < 2500) clone.renderQueue = 2000; }

        private static bool PieceNeedsCutout(Material m, out string why)
        {
            float cutoff = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0f;
            if (cutoff <= 0f) { why = "cutoff 0"; return false; }
            float colorA = m.HasProperty("_Color") ? m.GetColor("_Color").a : 1f;
            if (colorA < cutoff) { why = "colour alpha under cutoff"; return true; }
            var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (tex == null) { why = "no albedo texture"; return false; }
            if (!UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(tex.graphicsFormat)) { why = "albedo format has no alpha"; return false; }
            if (!s_minAlpha.TryGetValue(tex, out float minA))
            {
                minA = -1f;   // unknown
                if (tex is Texture2D t2 && t2.isReadable)
                {
                    if (t2.format == TextureFormat.RGBA32)   // [generator]: a NativeArray view of mip 0, no 64 MB managed copy; Color32.a is alpha only in RGBA32 (ARGB32 and every other format go to the GPU)
                    try { var px = t2.GetPixelData<Color32>(0); byte lo = 255; for (int i = 0; i < px.Length; i++) if (px[i].a < lo) lo = px[i].a; minA = lo / 255f; }
                    catch { minA = -1f; }
                }
                if (s_minAlpha.Count > 4096) s_minAlpha.Clear();
                s_minAlpha[tex] = minA;
            }
            if (minA < 0f) { why = s_alphaMin != null ? "albedo alpha being measured on the GPU (cutout until the answer)" : "albedo alpha not readable"; return true; }
            if (minA * colorA >= cutoff) { why = "albedo alpha never under cutoff"; return false; }
            why = "albedo alpha cuts"; return true;
        }

        // ── GPU min-alpha (1.2.210, G1): vanilla's piece albedos are not CPU-readable, so their lowest alpha is measured once per texture on the GPU
        // (EbmAlphaMin.compute, one atomic per 32 x 32 texels) and read back asynchronously. Until the answer arrives the clone stays cutout (the
        // safe look); then every clone waiting on that texture is decided again and switched to opaque when its alpha never cuts. Bounded: one
        // pending entry per texture, a 1-uint buffer each, released in the callback.
        private static ComputeShader s_alphaMin; private static int s_alphaKernel;
        private static readonly int IdAlphaTex = Shader.PropertyToID("_EbmAlphaTex"), IdAlphaOut = Shader.PropertyToID("_EbmAlphaOut"), IdAlphaSize = Shader.PropertyToID("_EbmAlphaSize");
        private static readonly Dictionary<Texture, List<(Material src, Material clone)>> s_alphaWait = new Dictionary<Texture, List<(Material, Material)>>();
        private static int s_alphaMeasured, s_alphaOpaqued;

        private static void MeasureAlpha(Texture tex, Material src, Material clone)
        {
            if (s_alphaMin == null || tex == null) return;
            if (s_alphaWait.TryGetValue(tex, out var waiting)) { waiting.Add((src, clone)); return; }
            GraphicsBuffer outBuf = null;
            try
            {
                outBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                outBuf.SetData(new uint[] { 255u });
                s_alphaMin.SetTexture(s_alphaKernel, IdAlphaTex, tex);
                s_alphaMin.SetBuffer(s_alphaKernel, IdAlphaOut, outBuf);
                s_alphaMin.SetInts(IdAlphaSize, tex.width, tex.height);
                s_alphaMin.Dispatch(s_alphaKernel, Mathf.Max(1, Mathf.CeilToInt(tex.width / 32f)), Mathf.Max(1, Mathf.CeilToInt(tex.height / 32f)), 1);
                s_alphaWait[tex] = new List<(Material, Material)> { (src, clone) };
                var buf = outBuf;
                UnityEngine.Rendering.AsyncGPUReadback.Request(buf, req => AlphaDone(tex, buf, req));
            }
            catch (Exception e) { outBuf?.Release(); s_alphaWait.Remove(tex); EasyBakeLog.Debug($"[Indirect] GPU min-alpha of '{tex.name}' not measured ({e.Message}); its pieces stay cutout"); }
        }

        private static void AlphaDone(Texture tex, GraphicsBuffer buf, UnityEngine.Rendering.AsyncGPUReadbackRequest req)
        {
            try
            {
                if (!s_alphaWait.TryGetValue(tex, out var waiting)) return;
                s_alphaWait.Remove(tex);
                if (req.hasError || tex == null) return;   // stays cutout (the safe answer)
                float minA = req.GetData<uint>()[0] / 255f;
                if (s_minAlpha.Count > 4096) s_minAlpha.Clear();
                s_minAlpha[tex] = minA;
                s_alphaMeasured++;
                foreach (var (src, clone) in waiting)
                {
                    if (src == null || clone == null) continue;
                    if (!PieceNeedsCutout(src, out _)) { clone.DisableKeyword("EBM_ALPHATEST"); OpaqueQueue(src, clone); s_alphaOpaqued++; s_pieceOpaque++; s_pieceCutout--; }
                }
                EasyBakeLog.Debug($"[Indirect] GPU min-alpha: '{tex.name}' lowest alpha {minA:0.000}; {s_alphaMeasured} texture(s) measured, {s_alphaOpaqued} EBM/Piece clone(s) switched to opaque so far");
            }
            catch (Exception e) { EasyBakeLog.Debug($"[Indirect] GPU min-alpha callback failed: {e.Message}"); }
            finally { buf.Release(); }
        }

        /// <summary>1.2.212 ([seasons], GPU grass): a compute shader from EBM's own bundle by asset name (e.g. EbmGrass), or null. Loaded once.</summary>
        private static readonly Dictionary<string, ComputeShader> s_computes = new Dictionary<string, ComputeShader>();
        internal static ComputeShader BundleCompute(string name)
        {
            if (!s_tried) Load();
            if (name == null || s_bundle == null) return null;
            if (s_computes.TryGetValue(name, out var cs)) return cs;
            try { cs = s_bundle.LoadAsset<ComputeShader>(name); } catch { cs = null; }
            s_computes[name] = cs;
            return cs;
        }


        /// <summary>1.2.210 (G5): a helper shader from EBM's own bundle by name (e.g. Hidden/EBM/CascadeCapture), or null.</summary>
        // [seasons] 11-09 E30: every shader in EBM's own bundle (EbmShaderWarm warms only these)
        internal static IEnumerable<Shader> OwnShaders { get { if (!s_tried) Load(); return s_variants.Values; } }

        internal static Shader BundleShader(string name)
        {
            if (!s_tried) Load();
            return name != null && s_variants.TryGetValue(name, out var sh) ? sh : null;
        }

        private static Shader FindVariant(string sourceName)
        {
            foreach (var (src, name) in VariantNames)
            {
                if (src != sourceName) continue;
                if (s_variants.TryGetValue(name, out var sh)) return sh;
                return null;   // only our own bundle's variants: never another mod's shader by name
            }
            return null;
        }

        // The key went off / the scene ends: clones go, Vedr forgets them. The bundle stays loaded (small).
        internal static void ReleaseClones()
        {
            foreach (var c in s_vedrRegistered) VedrLink.UnregisterVegetationClone(c);
            s_vedrRegistered.Clear();
            foreach (var kv in s_clones) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value);
            s_clones.Clear();
        }
    }
}
