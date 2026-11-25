using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // Caster receiver cull ([seasons] 11-11, lead's round-2 candidate #2 "shadow-cascade caster culling"; plan Tools\PLAN_ebm_caster_receiver_cull_20261111.md).
    //
    // The same-frame receiver test (EbmIndirectReceivers.cs + EbmShadowReceivers.compute + EbmIndirectCull.compute CascadeBits) drops a baked caster
    // from a sun cascade when its shadow footprint holds no surface the camera sees at or past it. It needed Unity's own cascade spheres from the G5
    // capture; 1.2.224 removed that capture (its shadow-pass UAV write corrupted piece geometry), so the test has never run since.
    //
    // This runs it on EBM's OWN spheres, with no capture and nothing written from inside a raster pass (the prep's compute dispatches only):
    //  - per cascade, ONE sphere enclosing both standard slice spheres - the corner-centroid one EBM already uses (x 1.25 + 2 m) and the on-axis one
    //    equidistant from the slice's near and far corners - grown by IndirectCasterReceiverCullMarginM + IndirectCasterReceiverCullMarginPct;
    //  - the receiver map is built with these spheres and the cull reads it with the same ones, so the cells line up;
    //  - a bigger sphere only marks MORE receivers into a cascade, so it can only keep more casters. CloseFit picks the cascade by view depth (the slice,
    //    inside both spheres); StableFit picks it by Unity's own sphere, which encloses the same slice. The "earlier sphere" skip stays capture-only.
    // Verify (IndirectCasterReceiverCullVerify): the test runs, nothing is dropped, and every would-be drop is re-tested against a second map built
    // with spheres x 1.5; the census counts the drops that the wider map would have kept (the sphere edge decided them). 0 there = the exact sphere
    // does not matter at this margin.
    // Census (Debug, 30 s): one prep frame every 5 s, 1 in 64 instances, x 64: per cascade tested / dropped (+ Verify wide keeps).
    internal static partial class EbmIndirect
    {
        private static readonly int IdRxSpheresGrown = Shader.PropertyToID("_EbmRxSpheresGrown");
        private static readonly int IdRxSpheresWide = Shader.PropertyToID("_EbmRxSpheresWide");
        private static readonly int IdRxOpts = Shader.PropertyToID("_EbmRxOpts");
        private static readonly int IdRxMapWide = Shader.PropertyToID("_EbmRxMapWide");
        private static readonly int IdRxStats = Shader.PropertyToID("_EbmRxStats");
        private const int RxStatWords = 12;
        private const float RxWideFactor = 1.5f;

        private static readonly Vector4[] s_rxGrown = new Vector4[4], s_rxWide = new Vector4[4];
        private static GraphicsBuffer s_rxMapWide, s_rxStats;
        private static readonly uint[] s_rxStatsZero = new uint[RxStatWords];
        private static System.Action<AsyncGPUReadbackRequest> s_onRxStats;

        internal static bool CasterRxKeyOn => FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCull != null && FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCull.Value;
        internal static bool CasterRxVerifyOn => FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullVerify != null && FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullVerify.Value;
        private static bool CasterRxWanted => CasterRxKeyOn || CasterRxVerifyOn;   // the Verify twin runs the test on its own (drops nothing)
        private static float CasterRxMarginM => FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullMarginM != null ? Mathf.Max(0f, FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullMarginM.Value) : 2f;
        private static float CasterRxMarginPct => FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullMarginPct != null ? Mathf.Max(0f, FiresEasyBakeMeshesPlugin.IndirectCasterReceiverCullMarginPct.Value) : 10f;

        // ---------------------------------------------------------------- spheres (from RecordCascadePrep, the main camera, n CPU cascades)
        private static void CasterRxSpheres(Camera cam, int n)
        {
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float diag2 = tanHalf * tanHalf * (1f + cam.aspect * cam.aspect);   // squared corner distance from the view axis per metre of depth
            var t = cam.transform;
            Vector3 pos = t.position, fwd = t.forward;
            float marginM = CasterRxMarginM, marginPct = CasterRxMarginPct * 0.01f;
            for (int k = 0; k < 4; k++)
            {
                if (k >= n || s_cascadeSpheres[k].w <= 0f || s_cascadeFar[k] <= s_cascadeNear[k]) { s_rxGrown[k] = Vector4.zero; s_rxWide[k] = Vector4.zero; continue; }
                float near = s_cascadeNear[k], far = s_cascadeFar[k];
                // the on-axis sphere through the slice's near and far corners (its centre clamped to the far plane when the slice is wide)
                float along = Mathf.Min((far + near) * (1f + diag2) * 0.5f, far);
                float rEq = Mathf.Sqrt(Mathf.Max((far - along) * (far - along) + far * far * diag2, (along - near) * (along - near) + near * near * diag2));
                Vector4 both = EncloseSpheres(s_cascadeSpheres[k], pos + fwd * along, rEq);
                float r = both.w + marginM + marginPct * both.w;
                s_rxGrown[k] = new Vector4(both.x, both.y, both.z, r);
                s_rxWide[k] = new Vector4(both.x, both.y, both.z, r * RxWideFactor);
            }
        }

        // the smallest sphere holding sphere a (xyz centre, w radius) and sphere (cb, rb)
        private static Vector4 EncloseSpheres(Vector4 a, Vector3 cb, float rb)
        {
            var ca = new Vector3(a.x, a.y, a.z);
            Vector3 dv = cb - ca;
            float dd = dv.magnitude;
            if (dd + rb <= a.w) return a;
            if (dd + a.w <= rb) return new Vector4(cb.x, cb.y, cb.z, rb);
            float r = (dd + a.w + rb) * 0.5f;
            Vector3 c = ca + dv * ((r - a.w) / dd);   // dd > 0 here (equal centres end in one of the two returns above)
            return new Vector4(c.x, c.y, c.z, r);
        }

        // ---------------------------------------------------------------- the Verify map (after the normal map, same tiles and light frame)
        private static void RecordCasterRxWideMap(CommandBuffer cb, ComputeShader rx, int n)
        {
            if (s_rxMapWide == null) s_rxMapWide = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RxCascadeStride * 4, sizeof(uint));
            cb.SetComputeVectorArrayParam(rx, IdRxSpheresCpu, s_rxWide);
            cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxClear, IdRxMap, s_rxMapWide);
            cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxClear, Mathf.CeilToInt(RxCascadeStride * n / 64f), 1, 1);
            cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxScatter, IdRxMap, s_rxMapWide);
            cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxScatter, Mathf.CeilToInt(s_rxTilesX / 8f), Mathf.CeilToInt(s_rxTilesY / 8f), 1);
            cb.SetComputeBufferParam(rx, EbmIndirectAssets.KernelRxMips, IdRxMap, s_rxMapWide);
            cb.DispatchCompute(rx, EbmIndirectAssets.KernelRxMips, n, 1, 1);
        }

        // ---------------------------------------------------------------- the cull's inputs (every prep frame: the cascade kernel always has them bound)
        private static float s_rxCensusNext; private static bool s_rxCensusPending;
        private static bool CasterRxBind(CommandBuffer cb, ComputeShader cull, int k3, GraphicsBuffer map, bool on, bool verify)
        {
            if (s_rxStats == null) s_rxStats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, RxStatWords, sizeof(uint));
            if (s_rxCensusPending && Time.unscaledTime > s_rxCensusNext + 5f) s_rxCensusPending = false;   // a prep buffer Unity never ran (cascade 0 skipped): its readback never comes
            bool census = on && !s_rxCensusPending && Time.unscaledTime >= s_rxCensusNext;
            cb.SetComputeBufferParam(cull, k3, IdRxMapWide, verify && s_rxMapWide != null ? s_rxMapWide : map);
            cb.SetComputeBufferParam(cull, k3, IdRxStats, s_rxStats);
            cb.SetComputeVectorArrayParam(cull, IdRxSpheresGrown, s_rxGrown);
            cb.SetComputeVectorArrayParam(cull, IdRxSpheresWide, s_rxWide);
            cb.SetComputeVectorParam(cull, IdRxOpts, new Vector4(on ? 1f : 0f, on && verify ? 1f : 0f, census ? 1f : 0f, 0f));
            if (census) cb.SetBufferData(s_rxStats, s_rxStatsZero);
            return census;
        }

        private static long w_rxReads; private static readonly long[] w_rxTested = new long[4], w_rxDropped = new long[4], w_rxWide = new long[4];
        private static void CasterRxAfterDispatch(CommandBuffer cb, bool census)
        {
            if (!census) return;
            s_rxCensusPending = true;
            s_rxCensusNext = Time.unscaledTime + 5f;
            if (s_onRxStats == null) s_onRxStats = OnRxStats;
            cb.RequestAsyncReadback(s_rxStats, s_onRxStats);
        }

        private static void OnRxStats(AsyncGPUReadbackRequest r)
        {
            s_rxCensusPending = false;
            if (r.hasError) return;
            var d = r.GetData<uint>();
            if (d.Length < RxStatWords) return;
            for (int k = 0; k < 4; k++) { w_rxTested[k] += d[k] * 64L; w_rxDropped[k] += d[4 + k] * 64L; w_rxWide[k] += d[8 + k] * 64L; }
            w_rxReads++;
        }

        // the "on" state text, rebuilt only when a margin changes ([visual]'s review: no new string every prep frame)
        private static string s_rxOnText; private static float s_rxOnM = -1f, s_rxOnPct = -1f;
        private static string CasterRxOnText()
        {
            float m = CasterRxMarginM, pct = CasterRxMarginPct;
            if (s_rxOnText == null || m != s_rxOnM || pct != s_rxOnPct)
            {
                s_rxOnM = m; s_rxOnPct = pct;
                s_rxOnText = $"on (EBM's own cascade spheres: both slice spheres enclosed + {m:0.#} m + {pct:0.#}%)";
            }
            return s_rxOnText;
        }

        // ---------------------------------------------------------------- lines: Info on a state change (held 2 s), Debug every 30 s
        private static string s_rxState = "", s_rxPending = ""; private static float s_rxPendingSince, s_rxSayNext; private static int w_rxFrames;
        private static void CasterRxSay(string state)
        {
            float now = Time.unscaledTime;
            if (state != s_rxPending) { s_rxPending = state; s_rxPendingSince = now; }
            if (state != s_rxState && now - s_rxPendingSince >= 2f) { s_rxState = state; EasyBakeLog.Info("[Indirect] caster receiver cull: " + state); }
            if (now < s_rxSayNext) return;
            if (s_rxSayNext > 0f && w_rxReads > 0 && (CasterRxVerifyOn || EasyBakeLog.DebugOn()))   // 1.2.296: Verify on = the verdict at Info
            {
                var sb = new System.Text.StringBuilder(200);
                sb.Append($"[Indirect] caster receiver cull 30 s: on in {w_rxFrames} prep frame(s); {w_rxReads} census frame(s), 1 in 64 x64:");
                bool verify = CasterRxVerifyOn;
                for (int k = 0; k < 4; k++)
                {
                    if (w_rxTested[k] == 0) continue;
                    sb.Append($" c{k + 1} tested {w_rxTested[k]}, {(verify ? "would drop" : "dropped")} {w_rxDropped[k]} ({(double)w_rxDropped[k] / w_rxTested[k]:P0})");
                    if (verify) sb.Append($", WIDE MAP KEEPS {w_rxWide[k]}");
                    sb.Append(';');
                }
                if (verify) sb.Append(" VERIFY: nothing dropped");
                EasyBakeLog.Say(verify, sb.ToString());
            }
            s_rxSayNext = now + 30f;
            w_rxReads = 0; w_rxFrames = 0;
            for (int k = 0; k < 4; k++) { w_rxTested[k] = 0; w_rxDropped[k] = 0; w_rxWide[k] = 0; }
        }

        // from ReleaseReceivers
        private static void ReleaseCasterRx()
        {
            if (s_rxMapWide != null) { s_rxMapWide.Release(); s_rxMapWide = null; }
            if (s_rxStats != null) { s_rxStats.Release(); s_rxStats = null; }
            s_rxCensusPending = false;
        }
    }
}
