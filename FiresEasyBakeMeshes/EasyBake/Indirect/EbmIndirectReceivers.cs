using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // EBM 1.2.211 ([saddles] 11-08, G6 v2; G6 of Tools\STUDY_sections\seasons_GPU.md + §E of saddles_ShadowFixes.md; [generator] reviewed v1).
    // Replaces 1.2.209's one-frame-late receiver map. Our own code; the method was learned from VPO's renderer (ontrigger, MIT, reference only;
    // THIRD_PARTY_NOTICES.txt). Base: the frozen 1.2.210 (35EA2F505DDF), with [seasons]' G5 sphere capture (EbmIndirectSpheres.cs).
    //
    // CASCADE PREP: one command buffer on the sun's cascade-0 BeforeShadowMapPass, attached AFTER G5's capture buffer and BEFORE the
    // cascade-0 caster buffer (EbmIndirectCascades attaches all three in that order; same event = added order). It runs inside this
    // frame's shadow pass and reads G5's CascadeCaptureBuffer, written by the capture buffer just before it:
    //   1. the receiver map from THIS frame's depth (EbmShadowReceivers.compute: clear, tiles, scatter, mips), when the key is on;
    //   2. the cascade cull (EbmIndirectCull.compute kernel EbmCullCascades, group-aggregated) into views 2..5, which the cascade draws read.
    // Recorded in Camera.onPreCull for the MAIN camera only ([generator] A: the light's buffers run for every camera that renders sun
    // shadows, and the prep uses the main camera's matrices). For any other camera the buffer is cleared, so that camera reads whatever
    // views 2..5 hold (main's lists, or zero): never a cull made from the wrong depth.
    // The Update cull runs with _EbmCascadeInMain = 0, so it leaves views 2..5 at EbmClear's zero: if Unity skips cascade 0 this frame, the
    // cascade draws are empty, never stale. The placeholders (§E) keep Unity from skipping.
    // Keys: [Optimize] IndirectShadowSameFrame (on = this path; off = 1.2.208's cascade test in the Update cull, for the A/B),
    // IndirectShadowReceiverCull (the receiver test), IndirectShadowSizeCut (texels; 0 = off), IndirectShadowPlaceholders.
    // A bundle without the v5 kernels = the old path, one Info line.
    //
    // PLACEHOLDERS (§E): Unity renders a cascade or a light's shadow map, and runs its command buffers, only when it finds casters of its own
    // there, and fits the cascade depth range to them. At a fully baked base it may find none: EBM's casters then vanish or lose their tops.
    // One EMPTY ShadowsOnly RenderMeshIndirect a frame, over the sun-volume batches' bounds CLAMPED to the shadowed region (the cascade
    // spheres' box, grown toward the sun by the shadow distance; [generator] B: a batch's World box only ever grows), and one small one
    // at each shadowed local light. Zero instances: no pixels, no vertices.
    internal static partial class EbmIndirect
    {
        private const int RxRes = 128, RxCascadeStride = 21845, RxTile = 16;

        private static readonly int IdRxMap = Shader.PropertyToID("_EbmRxMap");
        private static readonly int IdRxTileBuf = Shader.PropertyToID("_EbmRxTileBuf");
        private static readonly int IdRxDepth = Shader.PropertyToID("_EbmRxDepth");
        private static readonly int IdRxInvViewProj = Shader.PropertyToID("_EbmRxInvViewProj");
        private static readonly int IdRxDepthSize = Shader.PropertyToID("_EbmRxDepthSize");
        private static readonly int IdRxRight = Shader.PropertyToID("_EbmRxRight");
        private static readonly int IdRxUp = Shader.PropertyToID("_EbmRxUp");
        private static readonly int IdRxLight = Shader.PropertyToID("_EbmRxLight");
        private static readonly int IdRxSpheresCpu = Shader.PropertyToID("_EbmRxSpheresCpu");
        private static readonly int IdRxCascades = Shader.PropertyToID("_EbmRxCascades");
        private static readonly int IdRxStableFit = Shader.PropertyToID("_EbmRxStableFit");
        internal static readonly int IdCascadeInMain = Shader.PropertyToID("_EbmCascadeInMain");
        internal static readonly int IdRxOn = Shader.PropertyToID("_EbmRxOn");
        private static readonly int IdRxMapRes = Shader.PropertyToID("_EbmRxMapRes");
        private static readonly int IdRxSizeFactor = Shader.PropertyToID("_EbmRxSizeFactor");
        private static readonly int IdRxBias = Shader.PropertyToID("_EbmRxBias");
        // IdCascadeCapture ("_EbmCascadeCapture") is declared by G5 in EbmIndirectSpheres.cs
        private const float RxBias = 0.25f;

        private static GraphicsBuffer s_rxMap, s_rxTiles, s_rxMapDummy, s_captureDummy, s_placeholderArgs;
        private static CommandBuffer s_prepCb;
        private static Light s_prepLight;
        private static int s_rxTilesX, s_rxTilesY;
        private static bool s_rxSaidOld, s_preCullHooked;
        private static int s_prepRecords, s_prepMaps, s_prepOther; private static float s_prepNextLog;
        private static readonly Vector4[] s_rxSphereScratch = new Vector4[4];

        // what the Update cull decided for this frame, for the main camera's onPreCull
        private static bool s_prepThisFrame; private static int s_prepFrame = -1, s_prepShadowPlanes;

        private static bool SameFrameWanted => FiresEasyBakeMeshesPlugin.IndirectShadowSameFrame == null || FiresEasyBakeMeshesPlugin.IndirectShadowSameFrame.Value;
        private static bool ReceiverCullWanted => FiresEasyBakeMeshesPlugin.IndirectShadowReceiverCull != null && FiresEasyBakeMeshesPlugin.IndirectShadowReceiverCull.Value;
        private static bool PlaceholdersWanted => FiresEasyBakeMeshesPlugin.IndirectShadowPlaceholders == null || FiresEasyBakeMeshesPlugin.IndirectShadowPlaceholders.Value;
        private static float SizeCutFactor => FiresEasyBakeMeshesPlugin.IndirectShadowSizeCut != null ? Mathf.Max(0f, FiresEasyBakeMeshesPlugin.IndirectShadowSizeCut.Value) : 0f;

        // ---------------------------------------------------------------- attach (EbmIndirectCascades: after AttachCapture, before the casters)
        internal static void AttachCascadePrep(Light sun)
        {
            if (sun == null) return;
            if (s_prepCb == null) s_prepCb = new CommandBuffer { name = "EBM cascade prep (receivers, cascade cull)" };
            if (s_prepLight == sun) return;
            DetachCascadePrep();
            sun.AddCommandBuffer(LightEvent.BeforeShadowMapPass, s_prepCb, ShadowMapPass.DirectionalCascade0);
            s_prepLight = sun;
            if (!s_preCullHooked) { s_preCullHooked = true; Camera.onPreCull += PrepOnPreCull; Camera.onPostRender += PrepOnPostRender; }
        }

        internal static void DetachCascadePrep()
        {
            if (s_prepLight != null && s_prepCb != null) s_prepLight.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, s_prepCb);
            s_prepLight = null;
            if (s_prepCb != null) s_prepCb.Clear();
        }

        // ---------------------------------------------------------------- per frame, from Cull after SetCascadeUniforms
        // Decides whether the shadow pass culls the cascades this frame; returns the _EbmCascadeInMain value for the Update dispatch
        // (0 = the prep buffer does them, 1 = the 1.2.208 path). The prep itself is recorded in the main camera's onPreCull.
        internal static int PlanCascadePrep(Light sun, int shadowPlanes)
        {
            bool separate = !s_prepFaulted && SameFrameWanted && CascadesOn && EbmIndirectAssets.Receivers != null && EbmIndirectAssets.KernelCullCascades >= 0 && s_cascVisible != null;
            if (separate != s_cascSeparate) { s_cascSeparate = separate; s_bindEpoch++; s_cascadesDirty = true; }   // 1.2.225: the casters read the other buffer from now on
            bool can = separate && sun != null && s_prepLight == sun;
            if (!can && SameFrameWanted && CascadesOn && EbmIndirectAssets.Receivers == null && !s_rxSaidOld)
            { s_rxSaidOld = true; EasyBakeLog.Info("[Indirect] same-frame cascade cull: this bundle has no EbmShadowReceivers v5 / EbmCullCascades kernels; cascades culled the 1.2.208 way"); }
            s_prepThisFrame = can; s_prepFrame = Time.frameCount; s_prepShadowPlanes = shadowPlanes;
            s_mainRendering = false;   // the Update runs between renders; a lost onPostRender can never pin the flag
            return separate ? 0 : 1;   // 1.2.225: with the separate buffer the Update cull never writes views 2..5 (a frame the prep cannot run has no EBM casters, never mixed buffers)
        }

        // [generator]'s re-check: a camera rendered NESTED inside the main camera's render (Camera.Render() from OnWillRenderObject, e.g. a
        // planar water reflection) fires its onPreCull after main recorded the prep but before main's shadow pass. While main is rendering,
        // other cameras leave the prep alone (they run main's prep against their own depth: extra cost only; main's own pass rebuilds it after).
        internal static bool s_cascSeparate;   // 1.2.225
        private static bool s_prepFaulted;   // 1.2.226: a throw latches the same-frame path off for the session (never a broken path every frame)
        private static bool s_prepSaidOn;
        private static bool s_mainRendering;
        private static readonly System.Collections.Generic.HashSet<string> s_otherNames = new System.Collections.Generic.HashSet<string>();
        private static bool s_saidOthers;

        private static void PrepOnPostRender(Camera cam) { if (cam == s_drawCamera) s_mainRendering = false; }

        private static void PrepOnPreCull(Camera cam)
        {
            if (s_prepCb == null) return;
            if (cam != s_drawCamera)
            {
                s_prepOther++;
                if (!s_saidOthers)
                {
                    if (s_otherNames.Count < 16) s_otherNames.Add(cam.name + (s_mainRendering ? " (nested in main)" : ""));
                    if (s_prepOther >= 30) { s_saidOthers = true; EasyBakeLog.Info("[Indirect] other cameras run the sun's shadow buffers (nested ones keep main's cascade prep, the rest get it empty): " + string.Join(", ", s_otherNames)); }
                }
                if (!s_mainRendering) s_prepCb.Clear();   // a camera between frames: nothing culled with the main camera's view
                return;
            }
            if (!s_prepThisFrame || s_prepFrame != Time.frameCount) { s_prepCb.Clear(); return; }
            s_mainRendering = true;
            var sun = s_prepLight;
            if (sun == null) { s_prepCb.Clear(); return; }
            try { RecordCascadePrep(cam, sun, s_prepShadowPlanes); }
            catch (System.Exception e) { s_prepCb.Clear(); s_prepThisFrame = false; s_prepFaulted = true; EasyBakeLog.Error("[Indirect] cascade prep threw (" + e.Message + "); same-frame cascade cull OFF for this session (cascades culled in Update, the 1.2.208 way)"); }
        }

        private static void RecordCascadePrep(Camera cam, Light sun, int shadowPlaneCount)
        {
            var rx = EbmIndirectAssets.Receivers;
            var cull = EbmIndirectAssets.Cull;
            EnsureRxBuffers(cam);
            var cb = s_prepCb;
            cb.Clear();
            var capture = CascadeCaptureBuffer != null ? CascadeCaptureBuffer : CaptureDummy();   // G5: written by its capture buffer just before ours
            int n = s_cascadeCount;
            for (int k = 0; k < 4; k++) s_rxSphereScratch[k] = k < n ? s_cascadeSpheres[k] : Vector4.zero;
            var lt = sun.transform;
            Vector4 right = lt.right, up = lt.up, light = lt.forward;
            bool stableFit = QualitySettings.shadowProjection == ShadowProjection.StableFit;

            bool receivers = ReceiverCullWanted;
            if (receivers)
            {
                var gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
                cb.SetComputeIntParam(rx, IdRxCascades, n);
                cb.SetComputeIntParam(rx, IdRxStableFit, stableFit ? 1 : 0);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxClear, IdRxMap, s_rxMap);
                cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxClear, Mathf.CeilToInt(RxCascadeStride * n / 64f), 1, 1);
                cb.SetComputeVectorParam(rx, IdRxDepthSize, new Vector4(cam.pixelWidth, cam.pixelHeight, s_rxTilesX, s_rxTilesY));
                cb.SetComputeTextureParam(rx, EbmIndirectAssets.KernelRxTiles, IdRxDepth, BuiltinRenderTextureType.ResolvedDepth);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxTiles, IdRxTileBuf, s_rxTiles);
                cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxTiles, s_rxTilesX, s_rxTilesY, 1);
                cb.SetComputeMatrixParam(rx, IdRxInvViewProj, (gpuProj * cam.worldToCameraMatrix).inverse);
                cb.SetComputeVectorParam(rx, IdRxRight, right); cb.SetComputeVectorParam(rx, IdRxUp, up); cb.SetComputeVectorParam(rx, IdRxLight, light);
                cb.SetComputeVectorArrayParam(rx, IdRxSpheresCpu, s_rxSphereScratch);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxScatter, IdRxTileBuf, s_rxTiles);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxScatter, IdRxMap, s_rxMap);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxScatter, IdCascadeCapture, capture);
                cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxScatter, Mathf.CeilToInt(s_rxTilesX / 8f), Mathf.CeilToInt(s_rxTilesY / 8f), 1);
                cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxMips, IdRxMap, s_rxMap);
                cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxMips, n, 1, 1);
                s_prepMaps++;
            }

            // the cascade cull (EbmCullCascades) into views 2..5, with every value it reads set here
            int k3 = EbmIndirectAssets.KernelCullCascades;
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdInstances, s_instBuf);
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdInstBatch, s_instBatchBuf);
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdInstLod, s_instLodBuf);
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdBatches, s_batchBuf);
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdVisible, s_cascScratch);   // 1.2.225: never the camera-read buffer (see s_cascScratch)
            cb.SetComputeBufferParam(cull, k3, EbmIndirectAssets.IdArgs, s_argsBuf);
            cb.SetComputeBufferParam(cull, k3, IdRxMap, receivers ? s_rxMap : RxMapDummy());
            cb.SetComputeBufferParam(cull, k3, IdCascadeCapture, capture);
            cb.SetComputeVectorArrayParam(cull, EbmIndirectAssets.IdShadowPlanes, s_shadowV);
            cb.SetComputeIntParam(cull, EbmIndirectAssets.IdShadowPlaneCount, shadowPlaneCount);
            cb.SetComputeVectorArrayParam(cull, EbmIndirectAssets.IdCascadeSpheres, s_rxSphereScratch);
            cb.SetComputeIntParam(cull, EbmIndirectAssets.IdCascadeCount, n);
            var p = cam.transform.position;
            float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            cb.SetComputeVectorParam(cull, EbmIndirectAssets.IdCam, new Vector4(p.x, p.y, p.z, QualitySettings.lodBias / (2f * halfTan)));
            cb.SetComputeIntParam(cull, EbmIndirectAssets.IdInstanceCount, s_high);
            cb.SetComputeIntParam(cull, EbmIndirectAssets.IdBatchCount, s_batchCap);   // 1.2.210: the args row stride
            cb.SetComputeIntParam(cull, EbmIndirectAssets.IdVisibleStride, s_cascStride);   // [Optimize] IndirectCascadeTightStride: the cascade buffers' own
            cb.SetComputeIntParam(cull, IdRxOn, receivers ? 1 : 0);
            cb.SetComputeVectorParam(cull, IdRxRight, right); cb.SetComputeVectorParam(cull, IdRxUp, up); cb.SetComputeVectorParam(cull, IdRxLight, light);
            float factor = SizeCutFactor;
            cb.SetComputeFloatParam(cull, IdRxSizeFactor, factor);
            cb.SetComputeFloatParam(cull, IdRxMapRes, factor > 0f ? CascadeMapSize(sun) : 0f);
            cb.SetComputeFloatParam(cull, IdRxBias, RxBias);
            int groups = Mathf.CeilToInt(s_high / (float)Group);
            if (groups > 0) cb.DispatchCompute(cull, k3, Mathf.Min(groups, 65535), Mathf.Max(1, Mathf.CeilToInt(groups / 65535f)), 1);
            cb.CopyBuffer(s_cascScratch, s_cascVisible);   // 1.2.225: a copy, not a UAV bind, into the buffer the caster draws read
            if (!s_prepSaidOn) { s_prepSaidOn = true; EasyBakeLog.Info("[Indirect] same-frame cascade cull on: the shadow-pass cull writes its own scratch buffer and copies it to the casters-only buffer (the camera buffer is never written mid-frame)"); }
            s_prepRecords++;

            if (Time.unscaledTime >= s_prepNextLog)
            {
                s_prepNextLog = Time.unscaledTime + 30f;
                EasyBakeLog.Debug($"[Indirect] cascade prep 30 s: {s_prepRecords} frame(s) culled in the shadow pass, receiver map built {s_prepMaps} time(s) ({s_rxTilesX}x{s_rxTilesY} tiles), {s_prepOther} other-camera pass(es) left empty, size cut {(factor > 0f ? factor.ToString("0.0") + " texel(s)" : "off")}, capture {(CascadeCaptureBuffer != null ? "G5" : "none (CPU spheres)")}, receiver earlier-sphere rule {(stableFit ? "on (StableFit)" : "off (CloseFit)")}");
                s_prepRecords = 0; s_prepMaps = 0; s_prepOther = 0;
            }
        }

        // per-cascade shadow map size: the light's own, else the quality setting, assuming the LARGER size when unsure (smaller texel = fewer cuts)
        private static float CascadeMapSize(Light sun)
        {
            int atlas = sun.shadowCustomResolution > 0 ? sun.shadowCustomResolution : 0;
            if (atlas <= 0)
            {
                int q = sun.shadowResolution != LightShadowResolution.FromQualitySettings ? (int)sun.shadowResolution : (int)QualitySettings.shadowResolution;
                atlas = q <= 0 ? 1024 : q == 1 ? 2048 : q == 2 ? 4096 : 8192;
            }
            return Mathf.Max(64, s_cascadeCount > 1 ? atlas / 2 : atlas);
        }

        private static void EnsureRxBuffers(Camera cam)
        {
            if (s_rxMap == null) s_rxMap = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RxCascadeStride * 4, sizeof(uint));
            int tx = Mathf.Max(1, Mathf.CeilToInt(cam.pixelWidth / (float)RxTile)), ty = Mathf.Max(1, Mathf.CeilToInt(cam.pixelHeight / (float)RxTile));
            if (s_rxTiles == null || tx != s_rxTilesX || ty != s_rxTilesY)
            {
                if (s_rxTiles != null) s_rxTiles.Release();
                s_rxTiles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tx * ty, sizeof(uint) * 2);
                s_rxTilesX = tx; s_rxTilesY = ty;
            }
        }

        internal static GraphicsBuffer RxMapDummy()
        {
            if (s_rxMapDummy == null) { s_rxMapDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint)); s_rxMapDummy.SetData(new uint[1]); }
            return s_rxMapDummy;
        }

        internal static GraphicsBuffer CaptureDummy()
        {
            if (s_captureDummy == null) { s_captureDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 5, sizeof(float) * 4); s_captureDummy.SetData(new Vector4[5]); }   // [4].x = 0: not valid
            return s_captureDummy;
        }

        // ---------------------------------------------------------------- §E placeholders, from the end of Draw
        internal static void SubmitShadowPlaceholders(Camera cam)
        {
            if (!PlaceholdersWanted || !CascadesOn || cam == null) return;
            if (s_placeholderArgs == null) { s_placeholderArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size); s_placeholderArgs.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[1]); }
            int nb = s_batches.Count;
            Batch any = null; Bounds union = default; bool has = false;
            for (int i = 0; i < nb; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0 || b.Shadows == ShadowCastingMode.Off || !VisRow(1, i)) continue;   // row 1 (the sun volume) of this frame's CPU cell cull
                if (!HasMesh(b) || !HasMaterial(b)) continue;   // 1.2.213: cached alive checks, after the cheap tests
                if (any == null) any = b;
                if (!has) { union = b.World; has = true; } else union.Encapsulate(b.World);
            }
            if (any == null) return;
            // [generator] B: clamp to the shadowed region - the cascade spheres' box, grown toward the sun by the shadow distance
            if (s_cascadeCount > 0 && ShadowedRegion(out Bounds region))
            {
                Vector3 lo = Vector3.Max(union.min, region.min), hi = Vector3.Min(union.max, region.max);
                if (lo.x > hi.x || lo.y > hi.y || lo.z > hi.z) return;   // nothing of ours can shadow the view
                union.SetMinMax(lo, hi);
            }
            var rp = new RenderParams(any.Material) { worldBounds = union, layer = any.Layer, shadowCastingMode = ShadowCastingMode.ShadowsOnly, receiveShadows = false, camera = cam, matProps = any.ShadowProps };
            Graphics.RenderMeshIndirect(in rp, any.Mesh, s_placeholderArgs);
            // each shadowed local light: a small box at the light, past its near plane on every side (inside that light's map only)
            for (int k = 0; k < s_localCount; k++)
            {
                var l = s_local[k];
                if (l == null) continue;
                float near = Mathf.Max(0.01f, l.shadowNearPlane);
                rp.worldBounds = new Bounds(l.transform.position, Vector3.one * (near * 2f + 0.5f));
                Graphics.RenderMeshIndirect(in rp, any.Mesh, s_placeholderArgs);
            }
        }

        // the box of every cascade sphere (this frame's CPU spheres), swept toward the sun by the shadow distance (casters upstream of the view)
        private static bool ShadowedRegion(out Bounds region)
        {
            region = default;
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            if (sun == null) return false;
            Vector3 toSun = -sun.transform.forward * Mathf.Max(1f, QualitySettings.shadowDistance);
            bool has = false;
            for (int k = 0; k < s_cascadeCount && k < 4; k++)
            {
                var s = s_cascadeSpheres[k];
                if (s.w <= 0f) continue;
                var c = new Vector3(s.x, s.y, s.z);
                var box = new Bounds(c, Vector3.one * (2f * s.w));
                box.Encapsulate(new Bounds(c + toSun, Vector3.one * (2f * s.w)));
                if (!has) { region = box; has = true; } else region.Encapsulate(box);
            }
            return has;
        }

        // from ReleaseAll
        private static void ReleaseReceivers()
        {
            DetachCascadePrep();
            if (s_rxMap != null) { s_rxMap.Release(); s_rxMap = null; }
            if (s_rxTiles != null) { s_rxTiles.Release(); s_rxTiles = null; s_rxTilesX = s_rxTilesY = 0; }
            if (s_rxMapDummy != null) { s_rxMapDummy.Release(); s_rxMapDummy = null; }
            if (s_captureDummy != null) { s_captureDummy.Release(); s_captureDummy = null; }
            if (s_placeholderArgs != null) { s_placeholderArgs.Release(); s_placeholderArgs = null; }
        }
    }
}
