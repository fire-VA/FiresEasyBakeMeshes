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

        // Source shader name -> EBM's own procedural variant in the ebm_indirect bundle (Fire 11-08: performance code lives in our 3 performance mods).
        // FiresPiece / Vedr wrappers include that mod's own surface code (.cginc) at build time, so the look never drifts; FTS and Vedr ship unchanged.
        // Custom/Piece is listed too: on a rig without TossinShade/Vedr it is what the pieces draw with.
        private static readonly (string source, string variant)[] VariantNames =
        {
            ("Custom/Vegetation", "EBM/VegetationIndirect"),
            ("Custom/StaticRock", "EBM/StaticRockIndirect"),
            // 1.2.204 (Fire: "the new ebm shader needs to do everything tossin shades shader and vedrs shader does"): one EBM/Piece for all three
            ("Custom/FiresPiece", "EBM/Piece"),
            ("Custom/Piece", "EBM/Piece"),
            ("Vedr/Piece", "EBM/Piece"),
            // 1.2.209 ([ghost], VPO parity item 5): Standard-material pieces and dropped items
            ("Standard", "EBM/StandardIndirect"),
            ("Custom/Rug", "EBM/RugIndirect"),   // 1.2.221 [seasons]; only with [Optimize] IndirectRugVariant (VariantFor)
            ("Standard (Specular setup)", "EBM/StandardSpecularIndirect"),
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
        private static readonly Dictionary<Material, Material> s_clones = new Dictionary<Material, Material>();
        private static readonly HashSet<Material> s_vedrRegistered = new HashSet<Material>();

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
                string path = Path.Combine(dir, BundleFile);
                if (!File.Exists(path)) { s_why = $"the indirect bundle '{BundleFile}' is not next to the DLL ({dir})"; return; }
                s_bundle = AssetBundle.LoadFromFile(path);
                if (s_bundle == null) { s_why = $"the indirect bundle '{BundleFile}' did not load"; return; }
                var cs = s_bundle.LoadAsset<ComputeShader>("EbmIndirectCull");
                if (cs == null) { s_why = "the indirect bundle has no EbmIndirectCull compute shader"; return; }
                KernelClear = cs.FindKernel("EbmClear");
                KernelCopyGrow = cs.HasKernel("EbmCopyGrow") ? cs.FindKernel("EbmCopyGrow") : -1;   // 1.2.213
                KernelCull = cs.FindKernel("EbmCull");
                KernelCullGrouped = cs.HasKernel("EbmCullGrouped") ? cs.FindKernel("EbmCullGrouped") : -1;   // G3
                EasyBakeLog.Debug("[Indirect] cull kernel: " + (KernelCullGrouped >= 0 ? "EbmCullGrouped available ([Optimize] IndirectCullGrouped picks it)" : "this bundle has no EbmCullGrouped: EbmCull only"));
                KernelCullCascades = cs.HasKernel("EbmCullCascades") ? cs.FindKernel("EbmCullCascades") : -1;   // 1.2.211 ([saddles] G6 v2)
                foreach (var sh in s_bundle.LoadAllAssets<Shader>()) if (sh != null) s_variants[sh.name] = sh;
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
            s_clones[source] = clone;
            string sn = source.shader.name;
            if ((sn == "Custom/Vegetation" || sn == "Custom/StaticRock") && VedrLink.RegisterVegetationClone(clone, source)) s_vedrRegistered.Add(clone);
            return clone;
        }

        private static bool sourceShaderIsFiresPiece(Material m) => m.shader != null && m.shader.name == "Custom/FiresPiece";

        // TossinShade's tuning knobs carry the same meaning under Vedr's names in EBM/Piece (no-op at their defaults)
        // 1.2.206: EBM/Piece's own knob names ([ghost] merge); a source material's TossinShade / Vedr knob values carry over (no-op at defaults)
        private static readonly (string from, string to)[] Knobs = { ("_FiresWetScale", "_EbmWetStrength"), ("_FiresWetSmoothness", "_EbmWetSmoothness"), ("_FiresSmoothnessScale", "_EbmReflectionScale"), ("_FiresMetallicScale", "_EbmMetallicScale"), ("_VedrWetSmoothness", "_EbmWetSmoothness"), ("_VedrSmoothnessScale", "_EbmReflectionScale"), ("_VedrMetallicScale", "_EbmMetallicScale") };
        private static void CopyFiresKnobs(Material source, Material clone)
        {
            foreach (var (from, to) in Knobs) if (source.HasProperty(from) && clone.HasProperty(to)) clone.SetFloat(to, source.GetFloat(from));
        }

        private static readonly string[] StandardRefusedKeywords = { "_ALPHABLEND_ON", "_ALPHAPREMULTIPLY_ON", "_PARALLAXMAP", "_DETAIL_MULX2", "_SPECULARHIGHLIGHTS_OFF", "_GLOSSYREFLECTIONS_OFF" };
        private static bool StandardRefused(Material m) { foreach (var k in StandardRefusedKeywords) if (m.IsKeywordEnabled(k)) return true; return false; }

        // [ghost]'s EBM/Standard variants read features as floats (one keyword, EBM_ALPHATEST), set here from the source's keywords
        private static void StandardFlags(Material src, Material clone)
        {
            clone.SetFloat("_EbmUseNormalMap", src.IsKeywordEnabled("_NORMALMAP") ? 1f : 0f);
            clone.SetFloat("_EbmUseGlossMap", src.IsKeywordEnabled("_METALLICGLOSSMAP") || src.IsKeywordEnabled("_SPECGLOSSMAP") ? 1f : 0f);
            clone.SetFloat("_EbmSmoothFromAlbedoA", src.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") ? 1f : 0f);
            clone.SetFloat("_EbmUseEmission", src.IsKeywordEnabled("_EMISSION") ? 1f : 0f);
            if (src.IsKeywordEnabled("_ALPHATEST_ON")) clone.EnableKeyword("EBM_ALPHATEST"); else clone.DisableKeyword("EBM_ALPHATEST");
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
