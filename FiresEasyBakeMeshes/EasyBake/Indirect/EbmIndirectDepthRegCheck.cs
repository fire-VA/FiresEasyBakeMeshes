using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.307 ([lead] 11-11, E2 false culls all past 64 m; the orientation self-test only proves v up/down with near rays at 15 %):
    // "[DepthCull] registration" - Verify only (IndirectDepthCullVerify), every 30 s with a still camera: 16 physics rays spread over the screen,
    // out to 300 m (terrain + solids), each compared with the pyramid's mip-0 depth at the texel the HiZ test would read AND its 8 neighbours.
    // Reports, in metres and relative: the centre error, how often a NEIGHBOUR matches better than the centre (and which offset), split near
    // (<= 64 m) / far (> 64 m). A pyramid that is registered right has its best match at the centre; a systematic offset (half texel, scale,
    // aspect) shows as one neighbour winning, mostly far. Mip 0 holds the FARTHEST of each 2x2, so a ray on a depth edge can read farther: that
    // is why the neighbour must beat the centre by a margin (5 %) to count.
    internal static partial class EbmIndirect
    {
        private const int RegRays = 16; private const float RegMaxM = 300f, RegEvery = 30f, RegBeat = 0.05f;
        private struct RegSample { public float U, V, Eye; }
        private static readonly RegSample[] s_regSamples = new RegSample[RegRays];
        private static float s_regNext; private static bool s_regPending; private static int s_regMask = -1;
        private static Vector3 s_regLastPos; private static Quaternion s_regLastRot = Quaternion.identity;

        /// <summary>From DepthCullPreCull after the pyramid is recorded (same frame, same vp as the levels just built).</summary>
        private static void RegCheck(Camera cam, Matrix4x4 vp, Matrix4x4 gpuProj)
        {
            if (DepthCullVerifyOn == null || !DepthCullVerifyOn.Value || !s_orientOk || s_regPending || s_levels.Length == 0) return;
            var ct = cam.transform;
            bool still = (ct.position - s_regLastPos).sqrMagnitude < 0.01f && Quaternion.Angle(ct.rotation, s_regLastRot) < 0.5f;
            s_regLastPos = ct.position; s_regLastRot = ct.rotation;
            if (!still || Time.unscaledTime < s_regNext) return;
            s_regNext = Time.unscaledTime + RegEvery;
            if (s_regMask == -1) s_regMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            int n = 0;
            for (int gy = 0; gy < 4; gy++)
                for (int gx = 0; gx < 4 && n < RegRays; gx++)
                {
                    float vx = 0.12f + 0.25f * gx, vy = 0.2f + 0.2f * gy;   // a 4x4 grid, upper rows reach far (horizon side)
                    Ray r = cam.ViewportPointToRay(new Vector3(vx, vy, 0f));
                    if (!Physics.Raycast(r, out RaycastHit hit, RegMaxM, s_regMask, QueryTriggerInteraction.Ignore)) continue;
                    Vector4 clip = vp * new Vector4(hit.point.x, hit.point.y, hit.point.z, 1f);
                    if (clip.w <= 0.01f) continue;
                    float u = clip.x / clip.w * 0.5f + 0.5f, v = clip.y / clip.w * 0.5f + 0.5f;
                    if (s_flipV) v = 1f - v;   // exactly as HiZTestEx reads it
                    s_regSamples[n++] = new RegSample { U = u, V = v, Eye = Vector3.Dot(hit.point - ct.position, ct.forward) };
                }
            if (n < 4) return;
            s_regPending = true;
            var lvl = s_levels[0]; int w = lvl.width, h = lvl.height; float p22 = gpuProj.m22, p23 = gpuProj.m23; bool rev = SystemInfo.usesReversedZBuffer;
            var samples = (RegSample[])s_regSamples.Clone(); int count = n; int camW = cam.pixelWidth, camH = cam.pixelHeight;
            AsyncGPUReadback.Request(lvl, 0, TextureFormat.RFloat, req =>
            {
                s_regPending = false;
                if (req.hasError) return;
                NativeArray<float> data = req.GetData<float>();
                int nearN = 0, farN = 0, nearNb = 0, farNb = 0; float nearErr = 0f, farErr = 0f, nearM = 0f, farM = 0f;
                var offs = new int[9];
                for (int k = 0; k < count; k++)
                {
                    var s = samples[k];
                    int x = Mathf.Clamp((int)(s.U * w), 0, w - 1), y = Mathf.Clamp((int)(s.V * h), 0, h - 1);
                    float centre = EyeFromRaw(data[y * w + x], p22, p23, rev), eC = Mathf.Abs(centre - s.Eye) / Mathf.Max(0.5f, s.Eye);
                    int best = 4; float eBest = eC;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int xx = Mathf.Clamp(x + dx, 0, w - 1), yy = Mathf.Clamp(y + dy, 0, h - 1);
                            float e = Mathf.Abs(EyeFromRaw(data[yy * w + xx], p22, p23, rev) - s.Eye) / Mathf.Max(0.5f, s.Eye);
                            if (e + RegBeat < eBest) { eBest = e; best = (dy + 1) * 3 + (dx + 1); }
                        }
                    bool far = s.Eye > 64f;
                    if (far) { farN++; farErr += Mathf.Min(eC, 9.99f); farM += Mathf.Abs(centre - s.Eye); if (best != 4) farNb++; }
                    else { nearN++; nearErr += Mathf.Min(eC, 9.99f); nearM += Mathf.Abs(centre - s.Eye); if (best != 4) nearNb++; }
                    offs[best]++;
                }
                EasyBakeLog.Say(true, $"[DepthCull] registration ({w}x{h} mip 0 for a {camW}x{camH} camera, v {(s_flipV ? "flipped" : "straight")}): near <= 64 m {nearN} ray(s), centre error {(nearN > 0 ? nearErr / nearN : 0):P0} ({(nearN > 0 ? nearM / nearN : 0):0.0} m), a neighbour better in {nearNb}; "
                    + $"far > 64 m {farN} ray(s), centre error {(farN > 0 ? farErr / farN : 0):P0} ({(farN > 0 ? farM / farN : 0):0.0} m), a neighbour better in {farNb}; best offset counts (dy,dx from -1,-1 to +1,+1): {string.Join(" ", offs)}");
            });
        }
    }
}
