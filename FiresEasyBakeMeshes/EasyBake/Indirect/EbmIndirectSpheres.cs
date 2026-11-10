using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.210 ([seasons], GPU study G5, Tools\STUDY_sections\seasons_GPU.md): Unity's EXACT cascade split spheres instead of EBM's CPU estimate
    // (camera slices x 1.25 + 2 m, EbmIndirectCascades.SliceSphere), which put up to ~1.5x more casters into each cascade near the disc edges.
    //
    // 1.2.211 v2 ([visual] 11-08, Fire's O log: "[Indirect] cascade spheres:" switched source 14,551 times; c1 6 batches / 103 inst vs 52 / 327 on
    // 1.2.209, c2 ~55 / 600 vs 210 / 7,522; Fire saw shadows flash). Two causes, both fixed here:
    //  (a) the light's command buffers run for EVERY camera that renders sun shadows (reflection probes, other cameras), so the capture came from
    //      whichever camera rendered LAST, and the in-buffer zeroing wiped the main camera's spheres for the others. Now the capture writes only
    //      while the main camera renders (_EbmCaptureOn, set per camera in Camera.onPreRender) and nothing is zeroed: the buffer always holds the
    //      MAIN camera's latest spheres, stamped with their frame (_EbmCaptureFrame);
    //  (b) the async readback lands 1-4 frames late, so "a capture from the last 3 frames" flipped between captured and estimated almost every
    //      frame. Now the age is exact (from the stamp, not guessed from when the read was asked), and the pose the margin starts from is the
    //      main camera's pose IN THAT frame (a 16-frame pose ring). Once the capture is in use it stays in use up to MaxAgeFrames late, the margin
    //      growing with the real move since; only an older capture (no main-camera shadow frame for a while, shadows off) falls back.
    //  The Info line names the source only when it has held for HoldSeconds (no per-frame lines); a Debug line every 30 s counts flips and ages.
    //
    //  - Capture: a one-point draw of Hidden/EBM/CascadeCapture recorded FIRST on the sun light's cascade-0 BeforeShadowMapPass (its own command
    //    buffer, added before the caster buffers). Same-frame GPU users (the cascade prep / ViewMode 3 cull, [saddles]' G6) read the buffer;
    //    [4].x == 1 = it holds a capture, [4].y = its frame tag. RecordCascadeCapture(cb) appends the same draw into a caller's buffer.
    //  - CPU users (the Update-time cascade cull uniforms, the CPU cell cull) run BEFORE this frame renders: they take the last capture grown by
    //    exactly how far each sphere can have moved since that frame (the camera's move + the turn swept over the sphere centre's distance + 0.5 m).
    //  Bounded: one 5-float4 buffer, one readback in flight, a 16-entry pose ring.
    internal static partial class EbmIndirect
    {
        internal static GraphicsBuffer CascadeCaptureBuffer;
        private static readonly int IdCascadeCapture = Shader.PropertyToID("_EbmCascadeCapture");
        private static readonly int IdCaptureOn = Shader.PropertyToID("_EbmCaptureOn"), IdCaptureFrame = Shader.PropertyToID("_EbmCaptureFrame");
        private static readonly Vector4[] s_captureZero = new Vector4[5];
        private static Material s_captureMat;
        private static CommandBuffer s_captureCb;
        private static bool s_captureLooked, s_preRenderHooked;
        private const int FrameMask = 0xFFFFF, MaxAgeFrames = 12, FreshFrames = 3;
        private static bool s_usingExact;
        private const float HoldSeconds = 2f;

        private static readonly Vector4[] s_captured = new Vector4[4];   // last valid capture: centre + RADIUS (not squared)
        private static int s_capturedTag = -1, s_capturedCount;
        private static Vector3 s_capCamPos, s_capCamFwd;                 // the main camera in the captured frame
        private static bool s_captureInFlight;
        private static System.Action<AsyncGPUReadbackRequest> s_onCapture;

        // the main camera's pose per frame tag, so a capture's age and margin start from the frame it really belongs to
        private static readonly Vector3[] s_posePos = new Vector3[16], s_poseFwd = new Vector3[16];
        private static readonly int[] s_poseTag = new int[16];
        private static Camera s_captureCamera;   // the camera EBM culls for (set each frame from SetCascadeUniforms' camera)

        private static bool EnsureCapture()
        {
            // 1.2.224 ([visual], Fire's O bisect 11-08: G5 ON + cascades ON = giant stretched pieces that STAY after every live flip; G5 ON + cascades
            // off = clean): the capture was a PIXEL shader writing a UAV (u1) from inside the sun's shadow pass. A raster UAV there is bound by
            // index next to whatever output targets Unity holds at that moment; a write that lands in another buffer corrupts it for good.
            // Removed: no capture draw, no UAV, no readback. The cascades use the CPU estimate; IndirectExactCascadeSpheres does nothing now.
            if (!s_captureLooked) { s_captureLooked = true; EasyBakeLog.Info("[Indirect] cascade sphere capture removed in 1.2.224 (its shadow-pass UAV write corrupted piece geometry); cascades use the CPU estimate"); }
            return false;
        }

        // every camera, before it renders (its shadow maps render inside that): only the main camera's render may write the capture
        private static void CapturePreRender(Camera cam)
        {
            if (cam != null && cam == s_captureCamera)
            {
                int tag = Time.frameCount & FrameMask, slot = tag & 15;
                var t = cam.transform;
                s_posePos[slot] = t.position; s_poseFwd[slot] = t.forward; s_poseTag[slot] = tag;
                Shader.SetGlobalFloat(IdCaptureFrame, tag);
                Shader.SetGlobalFloat(IdCaptureOn, 1f);
            }
            else Shader.SetGlobalFloat(IdCaptureOn, 0f);
        }

        /// <summary>Appends the capture (the one-point draw writing UAV 1; it writes only during the main camera's render) into a command buffer
        /// that runs on the sun's cascade-0 BeforeShadowMapPass. Nothing is zeroed: the buffer keeps the main camera's latest capture.</summary>
        internal static bool RecordCascadeCapture(CommandBuffer cb)
        {
            if (cb == null || !EnsureCapture()) return false;
            cb.SetRandomWriteTarget(1, CascadeCaptureBuffer);
            cb.DrawProcedural(Matrix4x4.identity, s_captureMat, 0, MeshTopology.Points, 1);
            cb.ClearRandomWriteTargets();
            cb.SetGlobalBuffer(IdCascadeCapture, CascadeCaptureBuffer);
            return true;
        }

        // EBM's own capture buffer: attached to cascade 0 BEFORE the caster buffers (same event: added order), recorded once (its content never changes)
        private static void AttachCapture(Light sun)
        {
            if (!EnsureCapture() || sun == null) return;
            if (s_captureCb == null) { s_captureCb = new CommandBuffer { name = "EBM cascade sphere capture" }; RecordCascadeCapture(s_captureCb); }
            sun.AddCommandBuffer(CascadeEvent, s_captureCb, ShadowMapPass.DirectionalCascade0);
        }

        private static void DetachCapture(Light sun)
        {
            if (sun != null && s_captureCb != null) sun.RemoveCommandBuffer(CascadeEvent, s_captureCb);
        }

        private static void RequestCaptureRead()
        {
            if (CascadeCaptureBuffer == null || s_captureInFlight) return;
            s_captureInFlight = true;
            AsyncGPUReadback.Request(CascadeCaptureBuffer, s_onCapture);
        }

        private static int s_readsNew, s_readsSame, s_readsNoPose;
        private static void OnCaptureRead(AsyncGPUReadbackRequest req)
        {
            s_captureInFlight = false;
            if (req.hasError) return;
            NativeArray<Vector4> d = req.GetData<Vector4>();
            if (d.Length < 5 || d[4].x != 1f) return;              // no main-camera capture yet
            int tag = (int)d[4].y & FrameMask;
            if (tag == s_capturedTag) { s_readsSame++; return; }   // nothing newer since the last read
            int slot = tag & 15;
            if (s_poseTag[slot] != tag) { s_readsNoPose++; return; }   // older than the pose ring (16 frames): too old to use
            int n = 0;
            for (int k = 0; k < 4; k++)
            {
                var s = d[k];
                if (s.w > 0f && !float.IsNaN(s.w) && !float.IsInfinity(s.w)) { s_captured[k] = new Vector4(s.x, s.y, s.z, Mathf.Sqrt(s.w)); n = k + 1; }
                else s_captured[k] = Vector4.zero;
            }
            s_capturedCount = n;
            s_capturedTag = tag; s_capCamPos = s_posePos[slot]; s_capCamFwd = s_poseFwd[slot];
            s_readsNew++;
        }

        /// <summary>Fills s_cascadeSpheres for n cascades from the last capture, grown by how far each sphere can have moved since; false = use the estimate.</summary>
        private static bool CapturedSpheres(Camera cam, int n, out string why)
        {
            s_captureCamera = cam;
            RequestCaptureRead();
            if (CascadeCaptureBuffer == null) { why = "capture removed (1.2.224)"; return false; }
            if (s_capturedTag < 0) { why = "no main-camera capture yet"; return false; }
            int age = ((Time.frameCount & FrameMask) - s_capturedTag) & FrameMask;
            NoteAge(age);
            // 1.2.211 ([visual] + [seasons], one implementation): hysteresis on the exact age: once in use it holds up to MaxAgeFrames late (grown);
            // re-entered only on a capture at most FreshFrames old, so a capture hovering near the limit cannot flip the source each frame
            int limit = s_usingExact ? MaxAgeFrames : FreshFrames;
            if (age > limit) { why = $"last main-camera capture is {age} frames old (> {limit}: shadows skipped or off)"; s_usingExact = false; return false; }
            if (s_capturedCount != n) { why = $"captured {s_capturedCount} cascade(s), {n} wanted"; s_usingExact = false; return false; }
            s_usingExact = true;   // [generator]: set only once the capture is really used
            var t = cam.transform;
            float moved = (t.position - s_capCamPos).magnitude;
            float turn = Vector3.Angle(t.forward, s_capCamFwd) * Mathf.Deg2Rad;
            for (int k = 0; k < n; k++)
            {
                var s = s_captured[k];
                float reach = (new Vector3(s.x, s.y, s.z) - s_capCamPos).magnitude;   // a turn swings the sphere centre around the camera by about reach x angle
                s_cascadeSpheres[k] = new Vector4(s.x, s.y, s.z, s.w + moved + reach * turn + 0.5f);
            }
            why = null;
            return true;
        }

        // ── the source line: Info only when a source has held for HoldSeconds; Debug every 30 s with flips and ages ──
        private static string s_sphereSource = "", s_pendingSource = "", s_pendingFull = "";
        private static float s_pendingSince, s_nextSphereSay;
        private static int s_sourceFlips, s_ageMax, s_ageSum, s_ageN;
        private static void NoteAge(int age) { s_ageSum += age; s_ageN++; if (age > s_ageMax) s_ageMax = age; }

        private static void SaySphereSource(string src)
        {
            // the kind (before any ':' reason) decides a change; the reason may vary frame to frame
            int c = src.IndexOf(':');
            string kind = c > 0 ? src.Substring(0, c) : src;
            float now = Time.unscaledTime;
            if (kind != s_pendingSource) { if (s_pendingSource.Length > 0) s_sourceFlips++; s_pendingSource = kind; s_pendingFull = src; s_pendingSince = now; }
            if (kind != s_sphereSource && now - s_pendingSince >= HoldSeconds)
            {
                s_sphereSource = kind;
                EasyBakeLog.Info("[Indirect] cascade spheres: " + s_pendingFull);
            }
            if (now >= s_nextSphereSay)
            {
                s_nextSphereSay = now + 30f;
                if (s_ageN > 0 || s_sourceFlips > 0)
                    EasyBakeLog.Debug($"[Indirect] cascade spheres 30 s: source {s_sphereSource} ({s_sourceFlips} flip(s)); capture age avg {(s_ageN > 0 ? (float)s_ageSum / s_ageN : 0):0.0} frames, max {s_ageMax}; reads new {s_readsNew}, same {s_readsSame}, past the pose ring {s_readsNoPose}");
                s_sourceFlips = 0; s_ageMax = 0; s_ageSum = 0; s_ageN = 0; s_readsNew = s_readsSame = s_readsNoPose = 0;
            }
        }
    }
}
