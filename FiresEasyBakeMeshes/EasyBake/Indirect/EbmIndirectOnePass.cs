using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // G4 ([wishbone] 11-08, [seasons]' study row G4, [Optimize] IndirectOneCullPass, default off): ONE cull pass a frame.
    // Before: the main pass read every instance for the camera + sun + cascades; the point / spot lights read every instance again (mode 2,
    // EbmIndirectLocalShadows.CullLocal); each other camera read every instance again at its pre-cull (mode 1, view 6, EbmIndirectViews).
    // With the key on, the main pass also culls, from the same instance read (EbmIndirectCull.compute, mode 0):
    //   - the lights (views 7..10): the same CPU work and uniforms as CullLocal, set before the main dispatch, and no mode-2 dispatch after it;
    //   - the other cameras that rendered LAST frame (up to 4, views 11..14), from their matrices at main-cull time, each with its own LOD.
    // At a camera's own pre-cull its view-projection is compared with the one culled: equal = drawn from its pre-culled view (no dispatch);
    // changed (a reflection-probe face turning, a planar reflection camera placed just before it renders) or not culled = today's mode-1 pass.
    // Correct either way; the Debug [Indirect] readback lists "pre-culled camera 1..4". Our own code (the idea of culling every view in one
    // pass is in VPO's renderer, read for reference only).
    internal static partial class EbmIndirect
    {
        private const int PreSlots = 4, PreView0 = 11;
        private static readonly Camera[] s_preCam = new Camera[PreSlots];
        private static readonly Matrix4x4[] s_preVP = new Matrix4x4[PreSlots];
        private static int s_preCount, s_preFrame = -1;
        private static readonly Camera[] s_seenCam = new Camera[PreSlots];   // other cameras that rendered this frame, culled in the next main pass
        private static int s_seenCount;
        private static readonly Vector4[] s_prePlanes = new Vector4[PreSlots * 6];
        private static readonly Vector4[] s_preCams = new Vector4[PreSlots];
        private static readonly Plane[] s_prePlaneScratch = new Plane[6];
        internal static int PreHits, PreMisses;   // Debug: pre-culled views used / fallen back to their own pass
        private static readonly int IdOtherPlanes = Shader.PropertyToID("_EbmOtherPlanes");
        private static readonly int IdOtherCams = Shader.PropertyToID("_EbmOtherCams");
        private static readonly int IdOtherCount = Shader.PropertyToID("_EbmOtherCount");
        private static readonly int IdMergeLocal = Shader.PropertyToID("_EbmMergeLocal");

        private static bool OnePassOn => FiresEasyBakeMeshesPlugin.IndirectOneCullPass != null && FiresEasyBakeMeshesPlugin.IndirectOneCullPass.Value;

        // From Cull, right before the main dispatch (after s_cullCamPos / s_cullLodScale are set). True = the lights are culled in this pass
        // (Cull then skips CullLocal's mode-2 dispatch).
        private static bool PrepareOnePass(ComputeShader cs)
        {
            if (!OnePassOn)
            {
                cs.SetInt(IdOtherCount, 0); cs.SetInt(IdMergeLocal, 0);
                s_preCount = 0; s_seenCount = 0;
                return false;
            }
            // the lights: exactly CullLocal's CPU work and uniforms, without its dispatch
            if (!s_cellsFused) GatherLocalLights();   // 1.2.213 IndirectFusedCells: gathered and culled in Cull's one cell walk
            bool lights = s_localCount > 0;
            if (lights)
            {
                if (!s_cellsFused)
                {
                    s_cells.CullSpheres(s_localSpheres, s_localCount, Vector3.zero, CpuSphereMargin, s_vis, LocalView0, s_cullCamPos, s_cullLodScale);
                    CullFaces();
                }
                cs.SetVectorArray(IdLocalSpheres, s_localSpheres);
                cs.SetVectorArray(IdLocalCones, s_localCones);
                cs.SetVectorArray(IdLocalSin, s_localSinV);
                cs.SetInt(IdLocalCount, s_localCount);
            }
            cs.SetInt(IdMergeLocal, lights ? 1 : 0);

            // the other cameras seen last frame, at their matrices now
            s_preCount = 0;
            for (int j = 0; j < s_seenCount; j++)
            {
                var c = s_seenCam[j];
                s_seenCam[j] = null;
                if (c == null || !c.isActiveAndEnabled) continue;
                var vp = c.projectionMatrix * c.worldToCameraMatrix;
                GeometryUtility.CalculateFrustumPlanes(vp, s_prePlaneScratch);
                int s = s_preCount++;
                for (int q = 0; q < 6; q++) { var pl = s_prePlaneScratch[q]; s_prePlanes[s * 6 + q] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance); }
                float halfTan = Mathf.Tan(c.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var pos = c.transform.position;
                s_preCams[s] = new Vector4(pos.x, pos.y, pos.z, QualitySettings.lodBias / (2f * Mathf.Max(halfTan, 0.01f)));
                s_preCam[s] = c; s_preVP[s] = vp;
            }
            for (int s = s_preCount; s < PreSlots; s++) s_preCam[s] = null;
            s_seenCount = 0;
            cs.SetVectorArray(IdOtherPlanes, s_prePlanes);
            cs.SetVectorArray(IdOtherCams, s_preCams);
            cs.SetInt(IdOtherCount, s_preCount);
            s_preFrame = Time.frameCount;
            return true;   // with the key on, the mode-2 pass never runs (no lights = nothing to cull)
        }

        // From OtherCameraPreCull, first thing: remember the camera for the next main pass; draw from its pre-culled view if this frame's pass
        // culled it at the matrices it renders with now. True = drawn (skip the mode-1 pass).
        private static bool OnePassPreCull(Camera cam)
        {
            if (!OnePassOn) return false;
            bool seen = false;
            for (int j = 0; j < s_seenCount; j++) if (s_seenCam[j] == cam) { seen = true; break; }
            if (!seen && s_seenCount < PreSlots) s_seenCam[s_seenCount++] = cam;
            if (s_preFrame != Time.frameCount) return false;
            int slot = -1;
            for (int j = 0; j < s_preCount; j++) if (s_preCam[j] == cam) { slot = j; break; }
            if (slot < 0) return false;
            if (!SameMatrix(cam.projectionMatrix * cam.worldToCameraMatrix, s_preVP[slot])) { PreMisses++; return false; }
            int nb = s_batches.Count, view = PreView0 + slot, mask = cam.cullingMask;
            for (int i = 0; i < nb; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0 || (mask & (1 << b.Layer)) == 0 || !HasMesh(b)) continue;   // 1.2.213: cached alive check
                if (b.SlotProps == null)
                {
                    b.SlotProps = new MaterialPropertyBlock[PreSlots];
                    for (int q = 0; q < PreSlots; q++) { b.SlotProps[q] = new MaterialPropertyBlock(); Bind(b.SlotProps[q], (PreView0 + q) * s_visibleStride + b.VisibleOffset); }
                }
                var rp = new RenderParams(b.Material)
                {
                    worldBounds = b.World, layer = b.Layer, renderingLayerMask = b.RenderingLayerMask, receiveShadows = b.ReceiveShadows,
                    shadowCastingMode = ShadowCastingMode.Off, matProps = b.SlotProps[slot], camera = cam,
                };
                Graphics.RenderMeshIndirect(in rp, b.Mesh, s_argsBuf, 1, view * s_batchCap + i);
            }
            PreHits++;
            return true;
        }

        // the bindings block in Draw re-binds the slot blocks with the others (a visible-stride or layout change)
        private static void BindSlotProps(Batch b)
        {
            if (b.SlotProps == null) return;
            for (int q = 0; q < PreSlots; q++) Bind(b.SlotProps[q], (PreView0 + q) * s_visibleStride + b.VisibleOffset);
        }

        private static bool SameMatrix(in Matrix4x4 a, in Matrix4x4 b)
        {
            for (int e = 0; e < 16; e++) { float x = a[e], y = b[e]; if (Mathf.Abs(x - y) > 1e-4f * (1f + Mathf.Abs(x))) return false; }
            return true;
        }
    }
}
