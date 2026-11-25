using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    /// <summary>
    /// 1.2.279 (lead 11-11, Fire: EBM's GPU paths on every GPU and graphics API). What this machine can run, asked once, before any indirect path:
    /// compute shaders, structured buffers readable from the vertex stage (our procedural instancing reads _EbmInstances / _EbmVisible / _EbmInstLod
    /// there; some Intel / GL drivers report 0), indirect argument buffers, and shader model 4.5+. A miss = the indirect path stays off for the
    /// session with one Warning naming why (BeginFrame's FallBack), and EBM draws through its normal instanced path. Newer SystemInfo members are
    /// read by reflection, so the DLL runs on every store build's Unity. One Info line at startup names the GPU, the API and every answer, so a
    /// tester's log says exactly what ran.
    /// </summary>
    internal static class EbmGpuCaps
    {
        private const int NeedVertexBuffers = 4;   // _EbmInstances, _EbmVisible, _EbmInstLod + one spare
        private static bool s_asked, s_said;
        private static string s_why;

        /// <summary>Null = the indirect path can run here; otherwise the reason it cannot.</summary>
        internal static string WhyNot()
        {
            if (s_asked) return s_why;
            s_asked = true;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) s_why = "no graphics device";
            else if (!SystemInfo.supportsComputeShaders) s_why = "this graphics card / API cannot run compute shaders";
            else if (SystemInfo.graphicsShaderLevel < 45) s_why = $"shader model {SystemInfo.graphicsShaderLevel / 10.0:0.0} (4.5 needed)";
            else
            {
                int vb = IntProp("maxComputeBufferInputsVertex", -1);
                bool? ind = BoolProp("supportsIndirectArgumentsBuffer");
                if (vb >= 0 && vb < NeedVertexBuffers) s_why = $"this driver allows {vb} structured buffer(s) in vertex shaders ({NeedVertexBuffers} needed)";
                else if (ind == false) s_why = "this graphics API has no indirect argument buffers";
            }
            return s_why;
        }

        internal static void SayOnce(string indirectState)
        {
            if (s_said || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            s_said = true;
            int vb = IntProp("maxComputeBufferInputsVertex", -1);
            bool? ind = BoolProp("supportsIndirectArgumentsBuffer");
            EasyBakeLog.Info($"[EBM] GPU: {SystemInfo.graphicsDeviceVendor} {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB), API {SystemInfo.graphicsDeviceType} "
                + $"({SystemInfo.graphicsDeviceVersion}), shader model {SystemInfo.graphicsShaderLevel / 10.0:0.0}, compute {(SystemInfo.supportsComputeShaders ? "yes" : "no")}, "
                + $"vertex structured buffers {(vb >= 0 ? vb.ToString() : "n/a")}, indirect args {(ind.HasValue ? (ind.Value ? "yes" : "no") : "n/a")}, platform {Application.platform}; "
                + $"indirect draw path: {indirectState}");
        }

        private static int IntProp(string name, int fallback)
        {
            try { var p = typeof(SystemInfo).GetProperty(name, BindingFlags.Public | BindingFlags.Static); return p != null ? System.Convert.ToInt32(p.GetValue(null, null)) : fallback; }
            catch { return fallback; }
        }

        private static bool? BoolProp(string name)
        {
            try { var p = typeof(SystemInfo).GetProperty(name, BindingFlags.Public | BindingFlags.Static); return p != null ? (bool?)(bool)p.GetValue(null, null) : null; }
            catch { return null; }
        }
    }
}
