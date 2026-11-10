using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.209 (VPO parity item 1): every OTHER camera that renders (reflection probe faces, the vanilla sky-depth camera, a planar water
    // reflection, the live map) gets its OWN culled view. Until now the main view's draws had no camera set, so every camera drew the main
    // camera's culled list: whatever was behind the player was missing from a reflection, and everything in view was drawn again into
    // every probe face. Now the main view draws for the main camera only, and in each other camera's pre-cull EBM culls once more for THAT
    // camera (frustum + LOD from its own position and field of view, no screen-size floor), into view 6, and draws that list for it alone.
    // Views run one after another on the GPU, so view 6 is reused by each camera in turn. The method (a per-camera culled view for
    // secondary cameras) was learned from VPO's renderer (ontrigger, MIT, read for reference only); this code is ours.
    internal static partial class EbmIndirect
    {
        private const int OtherView = 6;
        private static Camera s_drawCamera;
        private static bool s_viewsHooked;
        private static readonly Plane[] s_otherPlanesV = new Plane[6];
        private static readonly Vector4[] s_otherPlanes4 = new Vector4[6];
        private static int s_otherRenders;   // Debug: other-camera views drawn this window

        private static void HookViews()
        {
            if (s_viewsHooked) return;
            s_viewsHooked = true;
            Camera.onPreCull += OtherCameraPreCull;
        }

        private static void OtherCameraPreCull(Camera cam)
        {
            if (!Active || cam == null || cam == s_drawCamera || s_argsBuf == null || s_batches.Count == 0 || s_otherScratch == null) return;
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.Reflection) return;
            if (EbmIndirectAssets.Cull == null) return;
            long tO = PhT();   // 1.2.212 phase timer (the own pass below; a pre-culled view returns early and costs ~nothing)
            try { if (OnePassPreCull(cam)) { s_otherRenders++; return; } }   // G4 (EbmIndirectOnePass): drawn from this frame's main pass, no own pass
            catch (System.Exception e) { EasyBakeLog.Debug("[Indirect] pre-culled view for '" + cam.name + "' threw: " + e.Message + " (own pass instead)"); }
            try
            {
                int nb = s_batches.Count;
                var cs = EbmIndirectAssets.Cull;
                GeometryUtility.CalculateFrustumPlanes(cam, s_otherPlanesV);
                for (int i = 0; i < 6; i++) s_otherPlanes4[i] = new Vector4(s_otherPlanesV[i].normal.x, s_otherPlanesV[i].normal.y, s_otherPlanesV[i].normal.z, s_otherPlanesV[i].distance);
                // clear only view 6's counts
                cs.SetInt(EbmIndirectAssets.IdDrawStart, OtherView * s_batchCap);
                cs.SetInt(EbmIndirectAssets.IdDrawCount, s_batchCap);
                cs.SetBuffer(EbmIndirectAssets.KernelClear, EbmIndirectAssets.IdArgs, s_argsBuf);
                cs.Dispatch(EbmIndirectAssets.KernelClear, Mathf.CeilToInt(s_batchCap / 64f), 1, 1);
                int k = EbmIndirectAssets.CullKernel;   // G3: EbmCullGrouped when [Optimize] IndirectCullGrouped
                cs.SetBuffer(k, EbmIndirectAssets.IdInstances, s_instBuf);
                cs.SetBuffer(k, EbmIndirectAssets.IdInstBatch, s_instBatchBuf);
                cs.SetBuffer(k, EbmIndirectAssets.IdInstLod, s_instLodBuf);
                cs.SetBuffer(k, EbmIndirectAssets.IdBatches, s_batchBuf);
                cs.SetBuffer(k, EbmIndirectAssets.IdVisible, s_otherScratch);   // 1.2.225 ([generator]): this runs mid-frame too: never UAV-bind the camera-read buffer
                cs.SetBuffer(k, EbmIndirectAssets.IdArgs, s_argsBuf);
                cs.SetVectorArray(EbmIndirectAssets.IdCamPlanes, s_otherPlanes4);
                float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                float sizeScale = 1f / (2f * Mathf.Max(halfTan, 0.01f));
                var p = cam.transform.position;
                cs.SetVector(EbmIndirectAssets.IdCam, new Vector4(p.x, p.y, p.z, sizeScale * QualitySettings.lodBias));
                cs.SetFloat(EbmIndirectAssets.IdSizeScale, sizeScale);
                cs.SetFloat(EbmIndirectAssets.IdMinScreen, 0f);
                cs.SetInt(EbmIndirectAssets.IdInstanceCount, s_high);
                cs.SetInt(EbmIndirectAssets.IdBatchCount, s_batchCap);
                cs.SetInt(EbmIndirectAssets.IdVisibleStride, s_visibleStride);
                cs.SetInt(EbmIndirectAssets.IdViewMode, 1);   // only view 6
                int groups = Mathf.CeilToInt(s_high / 64f);
                cs.Dispatch(k, Mathf.Min(groups, 65535), Mathf.Max(1, Mathf.CeilToInt(groups / 65535f)), 1);
                cs.SetInt(EbmIndirectAssets.IdViewMode, 0);
                Graphics.CopyBuffer(s_otherScratch, s_otherVisible);   // 1.2.225: a copy into the buffer only the other-camera draws read
                if (s_bindingsDirtyOther)
                {
                    foreach (var b in s_batches) if (b.OtherProps != null) BindOther(b.OtherProps, OtherView * s_visibleStride + b.VisibleOffset);
                    s_bindingsDirtyOther = false;
                }
                // 1.2.210 (VPO study #6, our own method): the CPU cells judge this camera too, so it submits only batches with an entry in its
                // frustum (and on its culling mask), not every non-empty batch (IndirectCasterCpuCull also gates this; off = as 1.2.209)
                bool cpu = CasterCullOn;
                int w = 0;
                if (cpu)
                {
                    w = s_cells.BatchCount;
                    if (s_otherVis.Length < w) s_otherVis = new bool[Mathf.NextPowerOfTwo(Mathf.Max(1, w))];
                    s_cells.CullViews(s_otherPlanesV, 1, p, sizeScale * QualitySettings.lodBias, 0f, s_otherVis, 0f);
                }
                // [seasons] 11-09 E31: the sky-depth camera renders with a replacement shader that cannot place EBM's instances; with
                // IndirectSkyDepth on EBM draws nothing here and adds its batches to the mask itself right after (EbmIndirectSkyDepth.cs)
                if (SkyDepthKeep(cam, cpu, w)) { s_otherRenders++; PhAcc(PhOther, tO); return; }
                int mask = cam.cullingMask, sent = 0;
                for (int i = 0; i < nb; i++)
                {
                    var b = s_batches[i];
                    if (b.Count <= 0 || !HasMesh(b)) continue;   // 1.2.213: cached alive check
                    if (cpu && ((mask & (1 << b.Layer)) == 0 || (i < w && !s_otherVis[i]))) continue;
                    sent++;
                    if (b.OtherProps == null) { b.OtherProps = new MaterialPropertyBlock(); BindOther(b.OtherProps, OtherView * s_visibleStride + b.VisibleOffset); }
                    var rp = new RenderParams(b.Material)
                    {
                        worldBounds = b.World, layer = b.Layer, renderingLayerMask = b.RenderingLayerMask, receiveShadows = b.ReceiveShadows,
                        shadowCastingMode = ShadowCastingMode.Off, matProps = b.OtherProps, camera = cam,
                    };
                    Graphics.RenderMeshIndirect(in rp, b.Mesh, s_argsBuf, 1, OtherView * s_batchCap + i);
                }
                s_otherRenders++;
                s_otherSent += sent;
                if (IndirectStats.SplitOn) IndirectStats.NoteSplitOther(sent);   // 1.2.254
            }
            catch (System.Exception e) { EasyBakeLog.Debug("[Indirect] other-camera view for '" + cam.name + "' threw: " + e.Message); }
            PhAcc(PhOther, tO);
        }

        private static bool s_bindingsDirtyOther = true;
        private static bool[] s_otherVis = new bool[0];
        internal static long s_otherSent;   // Debug: other-camera draws submitted since the last [Indirect] line
    }
}
