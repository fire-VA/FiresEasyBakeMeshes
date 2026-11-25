using BepInEx.Configuration;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E2 ([seasons] 11-11, Tools\PLAN_ebm_E2_depth_pyramid_cull_20261111.md; plan OK'd by [visual], who owns EBM). EBM's own code.
    // Hide GPU-drawn instances that stand behind what the camera already drew (walls, terrain, other buildings): a depth pyramid (EbmHiZ.compute)
    // is built from the main camera's depth texture at CameraEvent.AfterDepthTexture; next frame EbmIndirectCull's view-0 test (EbmHiZTest.cginc)
    // drops an instance whose whole screen rect lies behind the pyramid's farthest depth there. The main cull runs in Update, BEFORE the camera
    // renders, so the pyramid it reads is last frame's: a motion guard turns the test off for a frame while the camera moves or turns fast.
    // Never culled: the near band (IndirectDepthCullNearM), a box the camera is inside, anything off view 0 (shadows, cascades, other cameras, lights).
    //  - capability: compute + R32_SFloat LoadStore + CopyTexture + EbmHiZ in the bundle; any miss = the test off and ONE Warning;
    //  - orientation self-test: the first pyramids are read back once and compared with physics rays at two screen heights; the test stays off until
    //    the depth they hold matches (then it knows whether v is flipped on this API); never matching = off and ONE Warning;
    //  - Verify ([Optimize] IndirectDepthCullVerify): nothing is dropped; a sample of what WOULD be dropped is re-tested next frame against that
    //    frame's own depth: still visible = a false cull (census);
    //  - CameraEmptySkip: a batch whose view-0 count is 0 only because of occlusion is not "empty" (_EbmOccl flags it), so it never stays skipped.
    internal static partial class EbmIndirect
    {
        internal static ConfigEntry<bool> DepthCullOn, DepthCullVerifyOn;
        internal static ConfigEntry<float> DepthCullNearM;
        internal static void BindDepthCull(ConfigFile config)
        {
            DepthCullOn = config.Bind("Optimize", "IndirectDepthCull", /* 1.2.299 OFF ([lead] 11-11: the first real Verify leg, town 615 D3D11 on 1.2.298, read FALSE CULLS ~0.75 % of re-tests; off until a Verify leg reads 0) */ false,
                "Pieces EBM draws on the graphics card are not drawn while they stand fully behind something the camera already sees (a wall, a hill, another building). Never near you or around you. Off = as before.");
            DepthCullNearM = config.Bind("Optimize", "IndirectDepthCullNearM", 8f,
                new ConfigDescription("Nothing within this many metres of the camera is hidden by IndirectDepthCull.", new AcceptableValueRange<float>(0f, 64f)));
            DepthCullVerifyOn = config.Bind("Optimize", "IndirectDepthCullVerify", false,
                "Test mode for IndirectDepthCull: nothing is hidden; a sample of what would be is checked against the next frame and any that is visible then is counted in the Debug census (should be 0).");
        }

        private static bool DepthCullWanted => DepthCullOn != null && DepthCullOn.Value && Active;

        // ── resources ────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static ComputeShader s_hiz; private static int s_kCopy = -1, s_kReduce = -1, s_kClear = -1, s_kVerify = -1;
        private static bool s_dcCapLooked, s_dcCapable, s_dcWarned;
        private static RenderTexture[] s_levels = new RenderTexture[0];
        private static RenderTexture s_pyramid, s_dummyTex;
        private static GraphicsBuffer s_occl, s_stats, s_verifyA, s_verifyB, s_dummyU;
        // 1.2.297 ([lead] Vulkan Verify: "listed 6621, re-tested 0"): the Verify kernel runs in the NEXT frame's pyramid build (BeforeLighting), after that
        // frame's per-cull clear already zeroed s_stats and after the census readback (queued right after the cull). Its counts were wiped before anyone
        // read them. It now writes its own buffer, read beside the census and zeroed only after each read.
        private static GraphicsBuffer s_vstats;
        private const int StatWords = 32;   // 1.2.301: HIZ_STAT_WORDS (the false-cull breakdown, EbmHiZTest.cginc words 8..26)
        private static readonly uint[] s_vstatsZero = new uint[StatWords];
        // 1.2.301 false-cull breakdown: per listed entry the hide's (farthest, zNear, mip, px), two lists like the instance lists, and a per-slot streak
        private static GraphicsBuffer s_auxA, s_auxB, s_auxDummy, s_streak;
        private static readonly int IdVerifyAux = Shader.PropertyToID("_EbmVerifyAux"), IdVerifyAuxIn = Shader.PropertyToID("_EbmVerifyAuxIn"), IdFalseStreak = Shader.PropertyToID("_EbmFalseStreak"), IdHiZCamPos = Shader.PropertyToID("_HiZCamPos");
        private static GraphicsBuffer AuxDummy() { if (s_auxDummy == null) s_auxDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 16); return s_auxDummy; }
        private static long[] w_dcFc = new long[StatWords];
        private static int s_occlCap;
        private static CommandBuffer s_dcCb; private static Camera s_dcCam;
        private static CameraEvent s_dcEvent = CameraEvent.AfterDepthTexture; private static bool s_dcDeferred;
        private const int VerifyCap = 4096;
        private static readonly int IdHiZ = Shader.PropertyToID("_EbmHiZ"), IdHiZVP = Shader.PropertyToID("_EbmHiZVP"), IdHiZSize = Shader.PropertyToID("_EbmHiZSize");
        private static readonly int IdHiZCam = Shader.PropertyToID("_EbmHiZCam"), IdHiZOpts = Shader.PropertyToID("_EbmHiZOpts");
        private static readonly int IdOccl = Shader.PropertyToID("_EbmOccl"), IdHiZStats = Shader.PropertyToID("_EbmHiZStats"), IdVerifyOut = Shader.PropertyToID("_EbmVerifyOut");
        private static readonly int IdHiZSrc = Shader.PropertyToID("_HiZSrc"), IdHiZDst = Shader.PropertyToID("_HiZDst"), IdHiZSizes = Shader.PropertyToID("_HiZSizes");
        private static readonly int IdHiZBatchCount = Shader.PropertyToID("_HiZBatchCount"), IdVerifyIn = Shader.PropertyToID("_EbmVerifyIn"), IdVerifyMax = Shader.PropertyToID("_HiZVerifyMax");
        private static readonly int IdInstV = Shader.PropertyToID("_EbmInstancesV"), IdInstBatchV = Shader.PropertyToID("_EbmInstBatchV"), IdBatchesV = Shader.PropertyToID("_EbmBatchesV");
        private static readonly int IdCameraDepth = Shader.PropertyToID("_CameraDepthTexture");

        private static bool DepthCullCapable()
        {
            if (s_dcCapLooked) return s_dcCapable;
            s_dcCapLooked = true;
            string why = null;
            if (!SystemInfo.supportsComputeShaders) why = "no compute shaders";
            else if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, FormatUsage.LoadStore)) why = "the R32 float format cannot be written from compute (LoadStore)";
            else if (SystemInfo.copyTextureSupport == CopyTextureSupport.None) why = "no CopyTexture";
            else
            {
                s_hiz = EbmIndirectAssets.BundleCompute("EbmHiZ");
                if (s_hiz == null) why = "EBM's bundle has no EbmHiZ compute (an older bundle)";
                else if (!s_hiz.HasKernel("HiZCopy") || !s_hiz.HasKernel("HiZReduce") || !s_hiz.HasKernel("HiZClear") || !s_hiz.HasKernel("HiZVerify")) why = "the EbmHiZ compute lacks a kernel on this GPU";
                else { s_kCopy = s_hiz.FindKernel("HiZCopy"); s_kReduce = s_hiz.FindKernel("HiZReduce"); s_kClear = s_hiz.FindKernel("HiZClear"); s_kVerify = s_hiz.FindKernel("HiZVerify"); }
            }
            s_dcCapable = why == null;
            if (!s_dcCapable) DcWarn(why);
            else EasyBakeLog.Info($"[DepthCull] ready on {SystemInfo.graphicsDeviceType} ({SystemInfo.graphicsDeviceName}): reversed Z {SystemInfo.usesReversedZBuffer}; the test starts once the orientation self-test passes (E2)");
            return s_dcCapable;
        }

        private static void DcWarn(string why)
        {
            if (s_dcWarned) return;
            // 1.2.289 (lead 11-11: players saw this Warning from the MENU camera, then "occlusion culling ON" in the world): outside the world (no local
            // player: menu, loading) it is a Debug line and does not latch, so the in-world main camera is the one that decides and, if it must, warns.
            if (Player.m_localPlayer == null) { EasyBakeLog.Debug($"[DepthCull] not in the world yet: {why} (decided again in the world)"); return; }
            s_dcWarned = true;
            EasyBakeLog.Warn($"[DepthCull] occlusion culling is off this session: {why}. Everything is drawn as before (E2).");
        }

        private static RenderTexture DummyTex()
        {
            if (s_dummyTex != null) return s_dummyTex;
            s_dummyTex = new RenderTexture(1, 1, 0, GraphicsFormat.R32_SFloat) { name = "EBM HiZ dummy", hideFlags = HideFlags.HideAndDontSave };
            s_dummyTex.Create();
            return s_dummyTex;
        }

        private static GraphicsBuffer DummyU()
        {
            if (s_dummyU == null) s_dummyU = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerifyCap + 1, sizeof(uint));
            return s_dummyU;
        }

        /// <summary>From EbmIndirectAssets after the cull kernels are found: every kernel that declares the E2 resources gets a valid binding at once,
        /// so a pass that runs before the first main cull (another camera, the lights) never meets an unbound texture / buffer.</summary>
        internal static void DepthCullBindDummies(ComputeShader cs, int kernel)
        {
            if (cs == null || kernel < 0) return;
            try
            {
                cs.SetTexture(kernel, IdHiZ, DummyTex());
                cs.SetBuffer(kernel, IdOccl, DummyU()); cs.SetBuffer(kernel, IdHiZStats, DummyU()); cs.SetBuffer(kernel, IdVerifyOut, DummyU());
                cs.SetBuffer(kernel, IdVerifyAux, AuxDummy());   // 1.2.301
                cs.SetVector(IdHiZSize, Vector4.zero);
            }
            catch (System.Exception e) { EasyBakeLog.Debug("[DepthCull] dummy binding skipped: " + e.Message); }
        }

        private static void EnsureCounters()
        {
            int cap = Mathf.Max(64, s_batchCap);
            if (s_occl == null || s_occlCap < cap)
            {
                Release(ref s_occl);
                s_occl = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, sizeof(uint)); s_occlCap = cap;
            }
            if (s_stats == null) s_stats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, StatWords, sizeof(uint));
            if (s_vstats == null) { s_vstats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, StatWords, sizeof(uint)); s_vstats.SetData(s_vstatsZero); }
            if (s_auxA == null) s_auxA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerifyCap, 16);
            if (s_auxB == null) s_auxB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerifyCap, 16);
            int slots = Mathf.Max(64, s_inst.Length);
            if (s_streak == null || s_streak.count < slots) { if (s_streak != null) s_streak.Release(); s_streak = new GraphicsBuffer(GraphicsBuffer.Target.Structured, slots, sizeof(uint)); s_streak.SetData(new uint[slots]); }
            if (s_verifyA == null) { s_verifyA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerifyCap + 1, sizeof(uint)); s_verifyA.SetData(new uint[VerifyCap + 1]); }
            if (s_verifyB == null) { s_verifyB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VerifyCap + 1, sizeof(uint)); s_verifyB.SetData(new uint[VerifyCap + 1]); }
        }

        // the pyramid for a camera of w x h: level 0 = half size, down to 1x1
        private static int s_pyrW, s_pyrH, s_pyrLevels;
        private static bool EnsurePyramid(int w, int h)
        {
            int w0 = Mathf.Max(1, (w + 1) / 2), h0 = Mathf.Max(1, (h + 1) / 2);
            if (s_pyramid != null && s_pyrW == w0 && s_pyrH == h0) return true;
            ReleasePyramid();
            int levels = 1; for (int s = Mathf.Max(w0, h0); s > 1; s = (s + 1) / 2) levels++;
            s_levels = new RenderTexture[levels];
            for (int l = 0; l < levels; l++)
            {
                int lw = Mathf.Max(1, w0 >> l), lh = Mathf.Max(1, h0 >> l);
                var rt = new RenderTexture(lw, lh, 0, GraphicsFormat.R32_SFloat) { enableRandomWrite = true, name = "EBM HiZ level " + l, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                if (!rt.Create()) { DcWarn("a depth pyramid texture could not be created"); ReleasePyramid(); return false; }
                s_levels[l] = rt;
            }
            s_pyramid = new RenderTexture(w0, h0, 0, GraphicsFormat.R32_SFloat) { useMipMap = true, autoGenerateMips = false, name = "EBM HiZ pyramid", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            if (!s_pyramid.Create()) { DcWarn("the depth pyramid texture could not be created"); ReleasePyramid(); return false; }
            s_pyrW = w0; s_pyrH = h0; s_pyrLevels = Mathf.Min(levels, s_pyramid.mipmapCount);
            s_pyrValid = false; s_orientOk = false; s_orientTries = 0;
            OrientFromApi();   // 1.2.310: D3D11 / D3D12 decide at once (the self-test then only cross-checks, in Verify)
            EasyBakeLog.Debug($"[DepthCull] pyramid {w0}x{h0}, {s_pyrLevels} level(s), for a {w}x{h} camera");
            return true;
        }

        private static void ReleasePyramid()
        {
            foreach (var rt in s_levels) if (rt != null) { rt.Release(); Object.Destroy(rt); }
            s_levels = new RenderTexture[0];
            if (s_pyramid != null) { s_pyramid.Release(); Object.Destroy(s_pyramid); s_pyramid = null; }
            s_pyrW = s_pyrH = s_pyrLevels = 0; s_pyrValid = false;
        }

        // ── the pyramid's pose (what the cull reads next frame) and the motion guard ───────────────────────────────────────────────
        private static Matrix4x4 s_pyrVP; private static Vector3 s_pyrPos, s_prevPos; private static Quaternion s_pyrRot, s_prevRot;
        private static float s_pyrFov; private static int s_pyrFrame = -10, s_pyrCamW, s_pyrCamH; private static bool s_pyrValid;
        private const float MoveGuardM = 2f, TurnGuardDeg = 3f;
        private static int w_dcFrames, w_dcOn, w_dcGuardMove, w_dcGuardTurn, w_dcGuardStale, w_dcGuardOrient, w_dcGuardTeleport, w_dcGuardSize;

        private static void DepthCullPreCull(Camera cam)
        {
            // 1.2.305 A2: the pyramid is also built for WaterHideOccludedTiles alone (lead-approved (b)); E2's instance cull still needs DepthCullWanted (DepthCullBind)
            // 1.2.380 ([visual], [lead] hot-swap audit, Tools\HOTSWAP_EBM_KEYS_20261114.md): with both keys off this returned before anything was detached, so the pyramid's
            // command buffer stayed on the camera with its last recorded build and ran every frame: an OFF arm after an ON arm in one run still paid for it.
            if (s_dcCb != null && !(DepthCullWanted || WaterOcclWanted)) { DetachDepthCull(); EasyBakeLog.Info("[DepthCull] off: the depth pyramid's command buffer left the camera"); }
            if (!(DepthCullWanted || WaterOcclWanted) || cam == null || cam != Utils.GetMainCamera()) return;
            if (!DepthCullCapable()) return;
            try
            {
                // 1.2.285 ([lead] 11-11, the 1.2.284 rig log: "the main camera renders no depth texture"): Valheim's main camera renders DEFERRED, where the
                // GBuffer's depth exists without depthTextureMode (and AfterDepthTexture is the forward path's event). The pyramid now reads what the
                // camera already has, never an extra depth pass: deferred = the resolved GBuffer depth at BeforeLighting; forward = the depth texture at
                // AfterDepthTexture, only when the camera makes one anyway (depthTextureMode is never turned on by EBM).
                bool deferred = cam.actualRenderingPath == RenderingPath.DeferredShading;
                if (!deferred && (cam.depthTextureMode & (DepthTextureMode.Depth | DepthTextureMode.DepthNormals)) == 0) { DcWarn($"the main camera renders forward ({cam.actualRenderingPath}) with no depth texture, and EBM does not add a depth pass for it"); return; }
                CameraEvent evt = deferred ? CameraEvent.BeforeLighting : CameraEvent.AfterDepthTexture;
                if (s_dcCam != cam || s_dcEvent != evt)
                {
                    DetachDepthCull(); s_dcCam = cam; s_dcEvent = evt; s_dcDeferred = deferred;
                    s_dcCb = new CommandBuffer { name = "EBM depth pyramid (E2)" }; cam.AddCommandBuffer(evt, s_dcCb);
                    EasyBakeLog.Info($"[DepthCull] depth source: {(deferred ? "the deferred GBuffer depth (resolved) at BeforeLighting" : "the forward depth texture at AfterDepthTexture")}, no extra depth pass (1.2.285)");
                }
                // 1.2.295 ([lead] queue2: E2 cost ~0.07 ms at swamp spot4 with little to hide): no pyramid when it cannot be used.
                //  - moving / turning past the guard this frame: next frame's guard fails on this very motion, so the build would be thrown away;
                //  - paused: the last 3 census reads hid under 2 % of what they tested, so the test is not paying for its pyramid (20 s, then tried again).
                // Either way the command buffer is emptied (no GPU work) and the pyramid reads stale, so the guard keeps the cull off.
                Vector3 camPos = cam.transform.position; Quaternion camRot = cam.transform.rotation;
                bool moving = (camPos - s_dcLastCamPos).sqrMagnitude > MoveGuardM * MoveGuardM || Quaternion.Angle(camRot, s_dcLastCamRot) > TurnGuardDeg;
                s_dcLastCamPos = camPos; s_dcLastCamRot = camRot;
                if (moving || Time.unscaledTime < s_dcPauseUntil) { s_dcCb.Clear(); if (moving) w_dcSkipMove++; else w_dcSkipPause++; return; }
                if (!EnsurePyramid(cam.pixelWidth, cam.pixelHeight)) return;
                EnsureCounters();
                var gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);   // the depth texture is a render target
                Matrix4x4 vp = gpuProj * cam.worldToCameraMatrix;
                RecordPyramid(vp, cam);
                // this frame's pose becomes the pyramid's (read by next frame's cull)
                s_prevPos = s_pyrPos; s_prevRot = s_pyrRot;
                s_pyrVP = vp; s_pyrPos = cam.transform.position; s_pyrRot = cam.transform.rotation; s_pyrFov = cam.fieldOfView;
                s_pyrCamW = cam.pixelWidth; s_pyrCamH = cam.pixelHeight; s_pyrFrame = Time.frameCount; s_pyrValid = true;
                if (!s_orientOk || (s_orientFromApi && !s_orientCrossChecked && DepthCullVerifyOn != null && DepthCullVerifyOn.Value)) OrientSelfTest(cam, vp, gpuProj);
                else RegCheck(cam, vp, gpuProj);   // 1.2.307: Verify-only registration check (near / far, centre vs neighbours)
            }
            catch (System.Exception e) { DcWarn("the pyramid build failed: " + e.Message); DetachDepthCull(); }
        }

        private static void RecordPyramid(Matrix4x4 vp, Camera cam)
        {
            var cb = s_dcCb; cb.Clear();
            int w0 = s_pyrW, h0 = s_pyrH;
            // 1.2.285: deferred = the GBuffer's resolved depth (a readable copy of the depth buffer); forward = the depth texture the camera made
            cb.SetComputeTextureParam(s_hiz, s_kCopy, IdHiZSrc, s_dcDeferred ? new RenderTargetIdentifier(BuiltinRenderTextureType.ResolvedDepth) : new RenderTargetIdentifier(IdCameraDepth));
            cb.SetComputeTextureParam(s_hiz, s_kCopy, IdHiZDst, s_levels[0]);
            cb.SetComputeIntParams(s_hiz, IdHiZSizes, cam.pixelWidth, cam.pixelHeight, w0, h0);
            cb.DispatchCompute(s_hiz, s_kCopy, (w0 + 7) / 8, (h0 + 7) / 8, 1);
            for (int l = 1; l < s_levels.Length; l++)
            {
                var src = s_levels[l - 1]; var dst = s_levels[l];
                cb.SetComputeTextureParam(s_hiz, s_kReduce, IdHiZSrc, src);
                cb.SetComputeTextureParam(s_hiz, s_kReduce, IdHiZDst, dst);
                cb.SetComputeIntParams(s_hiz, IdHiZSizes, src.width, src.height, dst.width, dst.height);
                cb.DispatchCompute(s_hiz, s_kReduce, (dst.width + 7) / 8, (dst.height + 7) / 8, 1);
            }
            for (int l = 0; l < s_pyrLevels; l++) cb.CopyTexture(s_levels[l], 0, 0, s_pyramid, 0, l);
            WaterOcclRecord(cb, vp, cam);   // 1.2.305 A2: zone water tile slabs against THIS pyramid + pose (no-op unless the key is on and the orientation is known)
            // Verify: last frame's list against THIS frame's pyramid and pose (the same HiZTest), before the next cull swaps the lists
            if (DepthCullVerifyOn != null && DepthCullVerifyOn.Value && s_orientOk && s_instBuf != null && s_instBatchBuf != null && s_batchBuf != null)
            {
                var input = s_verifyFlip ? s_verifyA : s_verifyB;   // the list the previous cull wrote
                cb.SetComputeTextureParam(s_hiz, s_kVerify, IdHiZ, s_pyramid);
                cb.SetComputeMatrixParam(s_hiz, IdHiZVP, vp);
                cb.SetComputeVectorParam(s_hiz, IdHiZSize, new Vector4(w0, h0, s_pyrLevels, 1f));
                cb.SetComputeVectorParam(s_hiz, IdHiZOpts, new Vector4(s_flipV ? 1f : 0f, DepthBias, 0f, VerifyCap));
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdVerifyIn, input);
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdInstV, s_instBuf);
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdInstBatchV, s_instBatchBuf);
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdBatchesV, s_batchBuf);
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdHiZStats, s_vstats);   // 1.2.297: its own counters (s_stats is cleared per cull)
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdOccl, s_occl); cb.SetComputeBufferParam(s_hiz, s_kVerify, IdVerifyOut, DummyU());
                // 1.2.301 breakdown inputs: the list's hide data, the per-slot streaks, the pyramid camera
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdVerifyAuxIn, s_verifyFlip ? s_auxA : s_auxB);
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdVerifyAux, AuxDummy());
                cb.SetComputeBufferParam(s_hiz, s_kVerify, IdFalseStreak, s_streak);
                var cp = cam.transform.position; cb.SetComputeVectorParam(s_hiz, IdHiZCamPos, new Vector4(cp.x, cp.y, cp.z, 0f));
                cb.SetComputeIntParam(s_hiz, IdVerifyMax, VerifyCap);
                cb.DispatchCompute(s_hiz, s_kVerify, (VerifyCap + 63) / 64, 1, 1);
            }
        }

        private const float DepthBias = 1e-5f;

        /// <summary>From EbmIndirect.Cull, right before the main dispatch: the guard's verdict, the pyramid inputs, and this frame's counters cleared.</summary>
        private static void DepthCullBind(ComputeShader cs, int kernel, Camera cam)
        {
            bool on = false;
            if (DepthCullWanted && s_dcCapable && s_pyramid != null && s_occl != null && s_pyrValid && Time.frameCount - s_pyrFrame <= 1)   // 1.2.295: no clear dispatch for a skipped pyramid
            {
                w_dcFrames++;
                // the counters for this frame's cull (per-batch occlusion flags, census, this frame's Verify list)
                var output = s_verifyFlip ? s_verifyB : s_verifyA;
                s_hiz.SetInt(IdHiZBatchCount, s_batchCap);
                s_hiz.SetBuffer(s_kClear, IdOccl, s_occl); s_hiz.SetBuffer(s_kClear, IdHiZStats, s_stats); s_hiz.SetBuffer(s_kClear, IdVerifyOut, output);
                s_hiz.SetTexture(s_kClear, IdHiZ, DummyTex());
                s_hiz.Dispatch(s_kClear, Mathf.Max(1, (Mathf.Max(s_batchCap, 8) + 63) / 64), 1, 1);
                cs.SetBuffer(kernel, IdOccl, s_occl); cs.SetBuffer(kernel, IdHiZStats, s_stats); cs.SetBuffer(kernel, IdVerifyOut, output);
                cs.SetBuffer(kernel, IdVerifyAux, s_verifyFlip ? s_auxB : s_auxA);   // 1.2.301: paired with output (same flip)
                s_verifyFlip = !s_verifyFlip;   // the verify in this frame's pyramid build reads the list just cleared + written by this cull... next frame
                on = GuardPasses(cam);
                if (on) w_dcOn++;
            }
            cs.SetTexture(kernel, IdHiZ, on ? (Texture)s_pyramid : DummyTex());
            cs.SetMatrix(IdHiZVP, s_pyrVP);
            cs.SetVector(IdHiZSize, on ? new Vector4(s_pyrW, s_pyrH, s_pyrLevels, 1f) : Vector4.zero);
            cs.SetVector(IdHiZCam, new Vector4(s_pyrPos.x, s_pyrPos.y, s_pyrPos.z, DepthCullNearM != null ? DepthCullNearM.Value : 8f));
            cs.SetVector(IdHiZOpts, new Vector4(s_flipV ? 1f : 0f, DepthBias, DepthCullVerifyOn != null && DepthCullVerifyOn.Value ? 1f : 0f, VerifyCap));
            s_dcCensusDue = on;   // read AFTER the cull dispatch (DepthCullAfterDispatch): a readback queued here read the buffer just cleared, so every census said tested 0
        }
        private static bool s_dcCensusDue;
        private static void DepthCullAfterDispatch() { if (s_dcCensusDue) { s_dcCensusDue = false; DepthCullCensus(); } }
        private static bool s_verifyFlip;

        // 1.2.295 idle: the last camera pose (motion skip) and the low-yield pause, decided from the census reads
        private static Vector3 s_dcLastCamPos; private static Quaternion s_dcLastCamRot = Quaternion.identity;
        private static float s_dcPauseUntil; private static int s_dcLowReads, w_dcSkipMove, w_dcSkipPause;
        private const float PauseShare = 0.02f, PauseSeconds = 20f; private const int PauseAfterReads = 3;
        private static void DcYield(uint tested, uint hidden)
        {
            if (tested < 16u || (DepthCullVerifyOn != null && DepthCullVerifyOn.Value)) return;   // too few sampled to judge (1 in 64); a Verify leg never pauses (1.2.296)
            if (hidden < tested * PauseShare) s_dcLowReads++; else s_dcLowReads = 0;
            if (s_dcLowReads < PauseAfterReads) return;
            s_dcLowReads = 0; s_dcPauseUntil = Time.unscaledTime + PauseSeconds;
            if (!s_dcSaidPause) { s_dcSaidPause = true; EasyBakeLog.Info($"[DepthCull] paused for {PauseSeconds:0} s: the last {PauseAfterReads} reads hid under {PauseShare:P0} of what they tested (no pyramid built while paused; tried again after). Shown once; the Debug census counts later pauses"); }
        }
        private static bool s_dcSaidPause;

        private static bool GuardPasses(Camera cam)
        {
            if (!s_pyrValid || Time.frameCount - s_pyrFrame > 1) { w_dcGuardStale++; return false; }
            if (!s_orientOk) { w_dcGuardOrient++; return false; }
            if (cam.pixelWidth != s_pyrCamW || cam.pixelHeight != s_pyrCamH || !Mathf.Approximately(cam.fieldOfView, s_pyrFov)) { w_dcGuardSize++; return false; }
            var p = Player.m_localPlayer;
            if (p != null && p.IsTeleporting()) { w_dcGuardTeleport++; return false; }
            // the camera's speed between the last two pyramids: a fast move or turn = the next frame may see what last frame's depth hid
            if ((s_pyrPos - s_prevPos).sqrMagnitude > MoveGuardM * MoveGuardM || (cam.transform.position - s_pyrPos).sqrMagnitude > MoveGuardM * MoveGuardM) { w_dcGuardMove++; return false; }
            if (Quaternion.Angle(s_pyrRot, s_prevRot) > TurnGuardDeg || Quaternion.Angle(cam.transform.rotation, s_pyrRot) > TurnGuardDeg) { w_dcGuardTurn++; return false; }
            return true;
        }

        // ── orientation self-test: does row v of the pyramid hold what the shader's uv.y = ndc.y * 0.5 + 0.5 points at? ────────────
        private static bool s_orientOk, s_flipV, s_orientPending; private static int s_orientTries, s_orientAgree;
        private static float s_orientNext;
        private static int s_solidMask = -1;
        private struct OrientSample { public float Uv, UvX, EyeDepth; }
        private const int OrientMaxRays = 8;
        private static readonly OrientSample[] s_orientSamples = new OrientSample[OrientMaxRays];
        private static int s_orientN;
        private static int s_orientRound; private static Vector3 s_orientLastPos; private static Quaternion s_orientLastRot = Quaternion.identity;
        private const float OrientMaxM = 48f;
        private const int OrientRounds = 4; private const float OrientRoundGapSeconds = 30f;

        // 1.2.310 ([lead] 11-11 e2-on-town: E2 never switched on: the self-test made 2 "no verdict" tries and then none). Cause, from this code: a try
        // runs only on a frame where the camera is STILL (< 0.1 m and < 0.5 deg since the last pyramid, the `still` gate below) AND a pyramid is built
        // that frame (the 1.2.295 motion skip builds none past 2 m / 3 deg), and needs 2+ near hits; a camera that keeps circling (the rig's town
        // pose, or a player walking) gets few or no tries, and after OrientRounds rounds the test latched E2 off for the session. Now:
        //  - Direct3D11 / Direct3D12: the orientation is known (the depth target is read with v flipped: every self-test on D3D11 today said
        //    "flipped"), set at once; the self-test still runs in Verify as a cross-check and takes over (Warning) if it ever disagrees;
        //  - every other API: the self-test keeps trying, a round every OrientRoundGapSeconds, never latching off.
        private static bool s_orientFromApi, s_orientCrossChecked;
        private static void OrientFromApi()
        {
            var api = SystemInfo.graphicsDeviceType;
            s_orientFromApi = api == GraphicsDeviceType.Direct3D11 || api == GraphicsDeviceType.Direct3D12;
            if (!s_orientFromApi) return;
            s_flipV = true; s_orientOk = true;
            if (!s_orientApiSaid) { s_orientApiSaid = true; EasyBakeLog.Info($"[DepthCull] occlusion culling ON: depth orientation from the graphics API ({api}: v flipped); the self-test only cross-checks it in Verify (1.2.310)"); }
        }
        private static bool s_orientApiSaid;

        private static void OrientSelfTest(Camera cam, Matrix4x4 vp, Matrix4x4 gpuProj)
        {
            if (s_orientPending || Time.unscaledTime < s_orientNext || s_levels.Length == 0) return;
            // 1.2.297 ([lead]: the test failed in 7 of 40 sessions, nearly all at town 615 while it loaded): a try only while the camera is still (the
            // readback holds the depth the camera saw a frame earlier, so a moving camera compares two poses) and the zones are not streaming in
            var ct = cam.transform;
            bool still = (ct.position - s_orientLastPos).sqrMagnitude < 0.01f && Quaternion.Angle(ct.rotation, s_orientLastRot) < 0.5f;
            s_orientLastPos = ct.position; s_orientLastRot = ct.rotation;
            if (!still) return;
            s_orientNext = Time.unscaledTime + 2f;
            if (s_solidMask == -1) s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            s_orientN = 0;
            // two rays high and low on the screen, slightly off centre: the depth the camera saw there, in eye metres
            // 1.2.298 ([lead] 1.2.297 town: "straight 65,000 %, flipped 48 % over 2 rays" on every try = ONE ray matched flipped, the other never
            // matched): a ray can pass something the camera draws but physics does not hit (the player in front of a close third-person camera, a
            // collider-less far bake), so rays are judged ONE BY ONE and a ray that matches neither orientation is ignored. More ray positions, spread out.
            float[] vys = { 0.3f, 0.7f, 0.25f, 0.75f, 0.6f, 0.4f, 0.8f, 0.2f };
            float[] vxs = { 0.45f, 0.45f, 0.2f, 0.8f, 0.15f, 0.85f, 0.3f, 0.7f };
            for (int s = 0; s < vys.Length && s_orientN < OrientMaxRays; s++)
            {
                Ray r = cam.ViewportPointToRay(new Vector3(vxs[s], vys[s], 0f));
                // 1.2.297: only NEAR hits (OrientMaxM). Past vanilla's active area EBM draws pieces that have no collider (SkipFarBakedPieces / far bakes),
                // so a long ray passes through a drawn building to a farther collider and the depth "mismatches" for good: why town 615 failed most often
                if (!Physics.Raycast(r, out RaycastHit hit, OrientMaxM, s_solidMask, QueryTriggerInteraction.Ignore)) continue;
                Vector4 clip = vp * new Vector4(hit.point.x, hit.point.y, hit.point.z, 1f);
                if (clip.w <= 0.01f) continue;
                float eye = Vector3.Dot(hit.point - cam.transform.position, cam.transform.forward);
                s_orientSamples[s_orientN++] = new OrientSample { Uv = clip.y / clip.w * 0.5f + 0.5f, UvX = clip.x / clip.w * 0.5f + 0.5f, EyeDepth = eye };
            }
            if (s_orientN < 2) return;   // the sky in both: try later
            s_orientPending = true;
            var lvl = s_levels[0]; int w = lvl.width, h = lvl.height; float p22 = gpuProj.m22, p23 = gpuProj.m23; bool rev = SystemInfo.usesReversedZBuffer;
            int n = s_orientN; var samples = (OrientSample[])s_orientSamples.Clone();
            AsyncGPUReadback.Request(lvl, 0, TextureFormat.RFloat, req =>
            {
                s_orientPending = false;
                if (req.hasError) return;
                NativeArray<float> data = req.GetData<float>();
                // per ray: which orientation(s) it matches (the level holds the FARTHEST of each 2x2, so an edge may read a little deeper: 15 % is a match)
                int votesStraight = 0, votesFlipped = 0;
                float errStraight = 0f, errFlipped = 0f;
                for (int k = 0; k < n; k++)
                {
                    var sm = samples[k];
                    int x = Mathf.Clamp((int)(sm.UvX * w), 0, w - 1);
                    int yA = Mathf.Clamp((int)(sm.Uv * h), 0, h - 1), yB = Mathf.Clamp((int)((1f - sm.Uv) * h), 0, h - 1);
                    float eS = Rel(EyeFromRaw(data[yA * w + x], p22, p23, rev), sm.EyeDepth), eF = Rel(EyeFromRaw(data[yB * w + x], p22, p23, rev), sm.EyeDepth);
                    bool mS = eS <= 0.15f, mF = eF <= 0.15f;
                    if (mS && !mF) votesStraight++; else if (mF && !mS) votesFlipped++;   // a ray matching both (a flat wall) or neither (an unseen collider) says nothing
                    errStraight += Mathf.Min(eS, 9.99f); errFlipped += Mathf.Min(eF, 9.99f);
                }
                errStraight /= n; errFlipped /= n;
                bool flip = votesFlipped > votesStraight;
                // a verdict needs at least 2 deciding rays, all for the same orientation
                bool decided = (votesFlipped >= 2 && votesStraight == 0) || (votesStraight >= 2 && votesFlipped == 0);
                float best = decided ? 0f : 1f;
                s_orientTries++;
                if (s_orientFromApi)
                {
                    // 1.2.310 cross-check only (Verify): one decided verdict settles it
                    if (!decided) return;
                    s_orientCrossChecked = true;
                    if (flip == s_flipV) EasyBakeLog.Info($"[DepthCull] orientation cross-check agrees with the graphics API ({(flip ? "flipped" : "straight")}; {n} ray(s), votes straight {votesStraight} / flipped {votesFlipped})");
                    else { s_flipV = flip; EasyBakeLog.Warn($"[DepthCull] orientation cross-check DISAGREES with the graphics API: the self-test says {(flip ? "flipped" : "straight")} ({n} ray(s), votes straight {votesStraight} / flipped {votesFlipped}); using the self-test from now on"); }
                    return;
                }
                if (decided && (s_orientAgree == 0 || flip == s_flipV)) { s_flipV = flip; s_orientAgree++; }
                else s_orientAgree = 0;
                EasyBakeLog.Info($"[DepthCull] orientation self-test round {s_orientRound + 1}, try {s_orientTries}: {n} ray(s), votes straight {votesStraight} / flipped {votesFlipped} (mean error straight {errStraight:P0}, flipped {errFlipped:P0}) -> {(best <= 0.15f ? (flip ? "flipped" : "straight") : "no verdict")} ({s_orientAgree} in a row)");
                if (s_orientAgree >= 3 && !s_orientOk) { s_orientOk = true; EasyBakeLog.Info($"[DepthCull] occlusion culling ON: the depth pyramid matches the scene ({(s_flipV ? "v flipped" : "v straight")} on {SystemInfo.graphicsDeviceType}); near band {(DepthCullNearM != null ? DepthCullNearM.Value : 8f):0} m (E2)"); }
                else if (s_orientTries >= 12 && !s_orientOk)
                {
                    // 1.2.297: not latched on the first round: a loading town can keep the depth and the colliders apart for a while. Up to OrientRounds rounds,
                    // OrientRoundGapSeconds apart; only then off for the session.
                    // 1.2.310: never latched off (a moving camera can take many rounds); one Info line for the first OrientRounds rounds, Debug after
                    s_orientRound++; s_orientTries = 0; s_orientAgree = 0;
                    s_orientNext = Time.unscaledTime + OrientRoundGapSeconds;
                    string line = $"[DepthCull] orientation self-test round {s_orientRound} found no match; trying again in {OrientRoundGapSeconds:0} s; the test stays off meanwhile";
                    if (s_orientRound <= OrientRounds) EasyBakeLog.Info(line); else EasyBakeLog.Debug(line);
                }
            });
        }

        // raw depth -> eye metres through the GPU projection: ndc z = (P22 z_view + P23) / -z_view (GL stores ndc * 0.5 + 0.5)
        private static float EyeFromRaw(float raw, float p22, float p23, bool reversed)
        {
            float ndc = reversed ? raw : raw * 2f - 1f;
            float denom = -ndc - p22;
            if (Mathf.Abs(denom) < 1e-7f) return 1e9f;
            return -(p23 / denom);
        }
        private static float Rel(float a, float b) => Mathf.Abs(a - b) / Mathf.Max(0.5f, b);

        // ── census ────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static float s_dcReadNext, s_dcSayNext; private static bool s_dcReading;
        private static long w_dcTested, w_dcOccluded, w_dcNear, w_dcInside, w_dcVerified, w_dcFalse, w_dcListed; private static int w_dcReads;
        private static void DepthCullCensus()
        {
            float now = Time.unscaledTime;
            if (!s_dcReading && now >= s_dcReadNext && s_stats != null)
            {
                s_dcReadNext = now + 5f; s_dcReading = true;
                AsyncGPUReadback.Request(s_stats, r =>
                {
                    s_dcReading = false;
                    if (r.hasError) return;
                    var d = r.GetData<uint>();
                    DcYield(d[0], d[1]);   // 1.2.295: the low-yield pause
                    w_dcTested += d[0] * 64L; w_dcOccluded += d[1] * 64L; w_dcNear += d[2] * 64L; w_dcInside += d[3] * 64L; w_dcListed += d[6]; w_dcReads++;
                });
                if (s_vstats != null && DepthCullVerifyOn != null && DepthCullVerifyOn.Value)
                {
                    // the Verify counts since the last read (every frame's re-test adds to them), then zeroed in command order after this read
                    AsyncGPUReadback.Request(s_vstats, rv => { if (rv.hasError) return; var v = rv.GetData<uint>(); w_dcVerified += v[4]; w_dcFalse += v[5]; for (int q = 8; q < 26 && q < v.Length; q++) w_dcFc[q] += v[q]; if (v.Length > 26 && v[26] > w_dcFc[26]) w_dcFc[26] = v[26]; });
                    s_vstats.SetData(s_vstatsZero);
                }
            }
            if (now < s_dcSayNext) return;
            // 1.2.296 ([lead]): with Verify on, the verdict line is written at Info whatever PerfCensus says (a Verify leg is void without it)
            bool dcVerify = DepthCullVerifyOn != null && DepthCullVerifyOn.Value;
            if (s_dcSayNext > 0f && (dcVerify || EasyBakeLog.DebugOn()))
                EasyBakeLog.Say(dcVerify, $"[DepthCull] 30 s: pyramid skipped {w_dcSkipMove} frame(s) moving, {w_dcSkipPause} paused (low yield); test on in {w_dcOn} of {w_dcFrames} frame(s) (guard: moving {w_dcGuardMove}, turning {w_dcGuardTurn}, stale {w_dcGuardStale}, orientation {w_dcGuardOrient}, size/fov {w_dcGuardSize}, teleport {w_dcGuardTeleport}); "
                    + $"one frame in every 5 s sampled 1 in 64 instances, x64: tested {w_dcTested}, hidden {w_dcOccluded} ({(w_dcTested > 0 ? (double)w_dcOccluded / w_dcTested : 0):P0}), near band {w_dcNear}, camera inside {w_dcInside} over {w_dcReads} read(s)"
                    + (DepthCullVerifyOn != null && DepthCullVerifyOn.Value ? $"; VERIFY (nothing hidden): listed {w_dcListed}, re-tested {w_dcVerified}, FALSE CULLS {w_dcFalse}"
                      + $"; false culls by cause: occluder moved {w_dcFc[8]}, occluder unchanged {w_dcFc[9]}, box changed {w_dcFc[10]}; by distance 0-16/16-32/32-64/64-128/>128 m: {w_dcFc[11]}/{w_dcFc[12]}/{w_dcFc[13]}/{w_dcFc[14]}/{w_dcFc[15]}"
                      + $"; by box size <4/4-16/16-64/>64 px: {w_dcFc[16]}/{w_dcFc[17]}/{w_dcFc[18]}/{w_dcFc[19]}; by mip 0-2/3-5/6+: {w_dcFc[20]}/{w_dcFc[21]}/{w_dcFc[22]}"
                      + $"; frames in a row 1/2/3+: {w_dcFc[23]}/{w_dcFc[24]}/{w_dcFc[25]} (longest {w_dcFc[26]})" : ""));
            s_dcSayNext = now + 30f;
            w_dcFrames = w_dcOn = w_dcGuardMove = w_dcGuardTurn = w_dcGuardStale = w_dcGuardOrient = w_dcGuardSize = w_dcGuardTeleport = w_dcSkipMove = w_dcSkipPause = 0;
            w_dcTested = w_dcOccluded = w_dcNear = w_dcInside = w_dcVerified = w_dcFalse = w_dcListed = 0; w_dcReads = 0;
            System.Array.Clear(w_dcFc, 0, w_dcFc.Length);
        }

        // ── CameraEmptySkip: occlusion zeros are not "empty" ─────────────────────────────────────────────────────────────────────
        private static byte[] s_occlSeen = new byte[0];
        private static bool s_occlPending;
        /// <summary>Requested in the same frame as CameraEmptyTick's args read (and before it), so both describe the same cull.</summary>
        private static void DepthCullOcclRead(int cap)
        {
            if (s_occl == null || s_occlPending || !DepthCullWanted || !s_dcCapable) return;
            int n = Mathf.Min(cap, s_occlCap);
            s_occlPending = true;
            AsyncGPUReadback.Request(s_occl, n * sizeof(uint), 0, r =>
            {
                s_occlPending = false;
                if (r.hasError) return;
                var d = r.GetData<uint>();
                if (s_occlSeen.Length < d.Length) s_occlSeen = new byte[d.Length];
                for (int i = 0; i < d.Length; i++) s_occlSeen[i] = (byte)(d[i] != 0u ? 1 : 0);
                s_occlSeenFrame = Time.frameCount;
            });
        }
        private static int s_occlSeenFrame = -10;
        /// <summary>True when batch i had something hidden by occlusion in the last read (CameraSample treats it as drawn).</summary>
        private static bool DepthCullHid(int i) => DepthCullWanted && i < s_occlSeen.Length && s_occlSeen[i] != 0;

        private static void DetachDepthCull()
        {
            if (s_dcCam != null && s_dcCb != null) { try { s_dcCam.RemoveCommandBuffer(s_dcEvent, s_dcCb); } catch { } }
            s_dcCb?.Release(); s_dcCb = null; s_dcCam = null; s_pyrValid = false;
        }

        private static bool s_dcHooked;
        /// <summary>From the indirect path's start (once): the per-camera hook that builds the pyramid.</summary>
        internal static void HookDepthCull()
        {
            if (s_dcHooked) return;
            s_dcHooked = true;
            // 1.2.307 E2 FIX ([lead]'s 2x2 11-11: town-stand, wind frozen: AA on 40,559 3+-frame false culls, AA off 5). Valheim's TAA is the legacy
            // PostProcessing stack: PostProcessingBehaviour.OnPreCull (a MonoBehaviour message) calls TaaComponent.SetProjectionMatrix, which sets
            // camera.projectionMatrix to a JITTERED matrix for this frame (nonJitteredProjectionMatrix keeps the plain one), and OnPostRender calls
            // ResetProjectionMatrix (assembly_postprocessing: PostProcessingBehaviour.cs:103/148/167-171, TaaComponent.cs:56-75). Our static
            // Camera.onPreCull ran BEFORE that message, so the pyramid's VP was the un-jittered matrix while the depth it was built from was rendered
            // jittered: every frame a different sub-pixel misregistration, which at mip 3-5 on small far boxes flipped the verdict. onPreRender runs
            // after OnPreCull (culling is done, rendering not started; the BeforeLighting / AfterDepthTexture command buffer still executes after it),
            // so cam.projectionMatrix there IS the matrix this frame's depth is rendered with. The cull (next frame) and Verify (this frame) both test
            // with the VP of the pyramid they read, so the only gap left is next frame's own jitter (<= 0.5 px, see the registration check).
            Camera.onPreRender += DepthCullPreCull;
        }
    }
}
