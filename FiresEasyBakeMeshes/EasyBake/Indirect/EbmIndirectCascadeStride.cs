using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [Optimize] IndirectCascadeTightStride ([worldgen] 11-09, Tools\AUDIT_shave_gpu_lights_20261109.md #5). The shadow-pass cull writes its own scratch buffer, and the whole of it is copied to the
    // casters-only buffer each frame (1.2.225; the copy stays). Its size was 6 views x the visible stride, a power of two with up to half unused. On: the two cascade buffers get their own stride,
    // the visible space really in use plus an eighth, rounded to 4 KB slots. EbmCullCascades reads _EbmVisibleStride as a uniform, so no shader change. Off: the same size as before.
    internal static partial class EbmIndirect
    {
        internal static int s_cascStride;
        private static bool s_cascTight;

        private static bool CascadeTightOn => FiresEasyBakeMeshesPlugin.IndirectCascadeTightStride != null && FiresEasyBakeMeshesPlugin.IndirectCascadeTightStride.Value;

        // the stride the caster draws index with: the separate buffer's own while the shadow-pass path is on, else the shared buffer's (the 1.2.208 path writes views 2..5 there)
        private static int CasterStride => s_cascSeparate && s_cascVisible != null ? s_cascStride : s_visibleStride;

        // from EndFrame after EnsureBuffers (every frame: a batch placed alone grows s_visibleUsed without a layout, and the key is read live)
        private static void EnsureCascadeBuffers()
        {
            if (s_visibleStride <= 0) return;
            bool tight = CascadeTightOn;
            if (s_cascVisible != null && tight == s_cascTight && (tight ? s_visibleUsed <= s_cascStride && s_cascStride <= s_visibleStride : s_cascStride == s_visibleStride)) return;
            int stride = s_visibleStride;
            if (tight)
            {
                long want = (long)s_visibleUsed + s_visibleUsed / 8 + 4096;
                want = (want + 4095) / 4096 * 4096;
                stride = (int)System.Math.Min(want, s_visibleStride);
            }
            Release(ref s_cascScratch); Release(ref s_cascVisible);
            s_cascScratch = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, stride * 6, sizeof(uint));
            s_cascVisible = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, stride * 6, sizeof(uint));
            s_cascStride = stride; s_cascTight = tight;
            s_bindEpoch++; s_cascadesDirty = true;   // the caster blocks re-bind to the new buffer and bases before the next record
            EasyBakeLog.Debug($"[Indirect] cascade buffers: stride {stride} ({(tight ? "tight" : "as the visible stride")}), visible space used {s_visibleUsed} of {s_visibleStride}, {stride * 6L * 4L / 1048576f:0.0} MB copied a frame");
        }
    }
}
