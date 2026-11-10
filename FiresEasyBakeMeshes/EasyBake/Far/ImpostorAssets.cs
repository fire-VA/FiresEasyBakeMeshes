using System;
using System.IO;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>EBM's own far-view shaders (moved from Core with Far): the embedded "firescoreimpostor" bundle (FatShaderLinear, LINEAR,
    /// FiresCoreImpostorBundleBuilder). Loaded once on first use; missing = the engine stands down with one Info WHY line.</summary>
    internal static class ImpostorAssets
    {
        private const string BundleName = "firescoreimpostor";
        private static bool s_tried;
        internal static Shader Impostor, Mid, Pack;
        internal static ComputeShader Cull;
        internal static string Why;

        internal static bool Ready()
        {
            if (s_tried) return Impostor != null && Pack != null && Cull != null;
            s_tried = true;
            var asm = typeof(ImpostorAssets).Assembly; string res = null;
            foreach (var n in asm.GetManifestResourceNames()) if (n == BundleName || n.EndsWith("." + BundleName, StringComparison.Ordinal)) res = n;
            if (res == null) { Why = "no embedded firescoreimpostor bundle in this EBM"; return false; }
            AssetBundle b = null;
            try
            {
                using (var st = asm.GetManifestResourceStream(res)) using (var ms = new MemoryStream()) { st.CopyTo(ms); b = AssetBundle.LoadFromMemory(ms.ToArray()); }
                if (b == null) { Why = "firescoreimpostor did not load"; return false; }
                Impostor = b.LoadAsset<Shader>("FiresCoreImpostor");
                Mid = b.LoadAsset<Shader>("FiresCoreImpostorMid");
                Pack = b.LoadAsset<Shader>("FiresCoreImpostorPack");
                Cull = b.LoadAsset<ComputeShader>("FiresCoreImpostorCull");
            }
            catch (Exception e) { Why = "firescoreimpostor failed: " + e.Message; return false; }
            finally { if (b != null) b.Unload(false); }
            if (Impostor == null || Pack == null || Cull == null) { Why = "firescoreimpostor lacks " + (Impostor == null ? "the impostor shader" : Pack == null ? "the pack shader" : "the cull compute"); return false; }
            if (!Impostor.isSupported || !Pack.isSupported) { Why = "the impostor shaders are not supported on this GPU"; return false; }
            EasyBakeLog.Debug($"[FarImpostors] shaders loaded: {Impostor.name}, {(Mid != null ? Mid.name : "no mid shader")}, {Pack.name}, cull compute");
            return true;
        }
    }
}
