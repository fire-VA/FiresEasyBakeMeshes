using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.208 (gap item 3, [Optimize] IndirectCascadeShadows, default off): per-cascade shadow casters.
    // Without it, one sun-volume list is drawn ShadowsOnly, and Unity puts that draw into every cascade it touches (x4 at 615).
    // With it, the GPU cull also tests each caster against each cascade's sphere, swept along the light (views 2..5).
    // The casters are drawn by command buffers on the sun light, one per cascade (LightEvent.BeforeShadowMapPass with
    // ShadowMapPass.DirectionalCascadeN), with the material's own ShadowCaster pass. Unity has set that cascade's matrices there.
    // The cascade spheres come from the camera's slices at the shadow cascade splits, made larger (x1.25 + 2 m) so a caster at a
    // cascade's edge is never dropped. The method (per-cascade caster lists drawn from the light's command buffers) was learned from
    // VPO's renderer (ontrigger, MIT, read for reference); this code is ours. Off or failing = the ShadowsOnly path as before.
    internal static partial class EbmIndirect
    {
        private static readonly Vector4[] s_cascadeSpheres = new Vector4[4];
        private static readonly Vector3[] s_cornersNear = new Vector3[4], s_cornersFar = new Vector3[4];
        private static readonly CommandBuffer[] s_cascadeCbs = new CommandBuffer[4];
        private static readonly LightEvent CascadeEvent = LightEvent.BeforeShadowMapPass;
        private static readonly ShadowMapPass[] CascadePass = { ShadowMapPass.DirectionalCascade0, ShadowMapPass.DirectionalCascade1, ShadowMapPass.DirectionalCascade2, ShadowMapPass.DirectionalCascade3 };
        private static Light s_cbLight;
        private static int s_cbCascades, s_cascadeCount;
        private static bool s_cascadesDirty = true; private static Vector3 s_cascadeLight;
        private static readonly Dictionary<Batch, MaterialPropertyBlock[]> s_casterInfo = new Dictionary<Batch, MaterialPropertyBlock[]>();   // 1.2.210: the pass lives on Batch.CasterPass
        private static bool[] s_recCascade = new bool[0];   // 1.2.210: [k * s_recCap + i] = batch i is recorded in cascade k's buffer
        private static int s_recCap;
        private static readonly int[] s_recCount = new int[4], s_staleScratch = new int[4];
        // [seasons] 11-09 E39 (EbmRenderCensus): batches cascade k's command buffer replays each frame; 0 when the cascades are not attached
        internal static int CascadeRecorded(int k) => s_cbLight != null && k >= 0 && k < s_cascadeCount ? s_recCount[k] : 0;
        private static bool CasterCullOn => FiresEasyBakeMeshesPlugin.IndirectCasterCpuCull == null || FiresEasyBakeMeshesPlugin.IndirectCasterCpuCull.Value;
        // 1.2.210 IndirectCasterCpuCull: the CPU cells say batch i can reach cascade k (inside the sun volume and the swept cascade sphere); off = every caster
        private static bool CascadeWants(int k, int i) => (!CasterCullOn || (VisRow(1, i) && VisRow(2 + k, i))) && !SizeSkip(k, i) && !EmptySkip(k, i);

        // 1.2.233 ([visual], lead: "setpass ~= draws everywhere; ~1,195 of our ~1,700 indirect draws are cascade casters"): in every cascade past the first (2-4; lead: c2 too)
        // a batch whose largest instance, seen from the cascade's NEAR split distance, covers under IndirectCasterMinScreen of the screen height
        // is not RECORDED into that cascade at all (its shadow there is at most that small too): a whole draw call, not just zero instances.
        // [Optimize] IndirectCasterSizeSkip (off = as 1.2.232). The census counts skips per cascade.
        internal static readonly float[] s_cascadeNear = new float[4];
        private static readonly int[] s_sizeSkipped = new int[4];
        private static float s_sizeHalfTan = 0.5f, s_sizeSayAt; private static bool s_lastSizeSkip = true;
        private static bool SizeSkipOn => FiresEasyBakeMeshesPlugin.IndirectCasterSizeSkip == null || FiresEasyBakeMeshesPlugin.IndirectCasterSizeSkip.Value;
        private static bool SizeSkip(int k, int i)
        {
            if (k < 1 || !SizeSkipOn || i >= s_batches.Count) return false;
            float near = s_cascadeNear[k];
            if (near <= 0f) return false;
            float min = FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen != null ? FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen.Value : 0.01f;
            if (min <= 0f) return false;
            var bb = s_batches[i];
            float size = 2f * bb.LocalBounds.extents.magnitude * bb.MaxScale;   // [generator]: x the largest instance scale added (big trees, modded giants)
            bool skip = size / (2f * near * s_sizeHalfTan) < min;
            if (skip) s_sizeSkipped[k]++;
            return skip;
        }
        private static bool s_lastCasterCull = true;
        internal static int s_cascadeRecords;               // Debug: cascade re-records since the last [Indirect] line
        private static int s_bindEpoch;                     // 1.2.210: bumped by every rebind (Draw); a block bound in an older epoch is re-bound before use

        private static bool CascadesOn => FiresEasyBakeMeshesPlugin.IndirectCascadeShadows != null && FiresEasyBakeMeshesPlugin.IndirectCascadeShadows.Value && s_cascadeCount > 0;

        private static void SetCascadeUniforms(ComputeShader cs, Camera cam)
        {
            s_cascadeCount = 0;
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            bool want = FiresEasyBakeMeshesPlugin.IndirectCascadeShadows != null && FiresEasyBakeMeshesPlugin.IndirectCascadeShadows.Value;
            float dist = QualitySettings.shadowDistance;
            s_sizeHalfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);   // 1.2.233
            if (want && sun != null && sun.isActiveAndEnabled && sun.shadows != LightShadows.None && dist > 0f)
            {
                int n = Mathf.Clamp(QualitySettings.shadowCascades, 1, 4);
                // 1.2.210 (G5, EbmIndirectSpheres.cs): Unity's own spheres from the last frame's capture, grown by how far they can have moved;
                // the CPU estimate below only while no recent capture exists. Unity selects cascades by split sphere only with 2+ cascades.
                string capWhy = null;
                if (n >= 2 && (FiresEasyBakeMeshesPlugin.IndirectExactCascadeSpheres == null || FiresEasyBakeMeshesPlugin.IndirectExactCascadeSpheres.Value) && CapturedSpheres(cam, n, out capWhy)) SaySphereSource("Unity's own split spheres (GPU capture), grown by the camera's move since");
                else
                {
                    SaySphereSource("CPU estimate (camera slices x 1.25 + 2 m): " + (n < 2 ? "one cascade" : capWhy));
                    float prev = cam.nearClipPlane;
                    for (int k = 0; k < n; k++)
                    {
                        float frac = k == n - 1 ? 1f : n == 2 ? QualitySettings.shadowCascade2Split : (k == 0 ? QualitySettings.shadowCascade4Split.x : k == 1 ? QualitySettings.shadowCascade4Split.y : QualitySettings.shadowCascade4Split.z);
                        float far = Mathf.Max(prev + 0.1f, frac * dist);
                        s_cascadeNear[k] = prev;   // 1.2.233: the split's near distance (the caster size skip)
                        s_cascadeSpheres[k] = SliceSphere(cam, prev, far);
                        prev = far;
                    }
                }
                for (int k = n; k < 4; k++) s_cascadeSpheres[k] = Vector4.zero;
                s_cascadeCount = n;
                Vector3 l = sun.transform.forward;
                s_cascadeLight = l;   // 1.2.210: the CPU cascade cull sweeps along it
                cs.SetVector(EbmIndirectAssets.IdLightDir, new Vector4(l.x, l.y, l.z, 0f));
            }
            cs.SetVectorArray(EbmIndirectAssets.IdCascadeSpheres, s_cascadeSpheres);
            cs.SetInt(EbmIndirectAssets.IdCascadeCount, s_cascadeCount);
            if (s_cascadeCount != s_cbCascades) s_cascadesDirty = true;
        }

        // the camera slice [near, far]'s 8 corners, enclosed by a sphere around their centre, made larger so no edge caster is lost
        private static Vector4 SliceSphere(Camera cam, float near, float far)
        {
            var vp = new Rect(0f, 0f, 1f, 1f);
            cam.CalculateFrustumCorners(vp, near, Camera.MonoOrStereoscopicEye.Mono, s_cornersNear);
            cam.CalculateFrustumCorners(vp, far, Camera.MonoOrStereoscopicEye.Mono, s_cornersFar);
            var t = cam.transform;
            Vector3 c = Vector3.zero;
            for (int i = 0; i < 4; i++) { s_cornersNear[i] = t.TransformPoint(s_cornersNear[i]); s_cornersFar[i] = t.TransformPoint(s_cornersFar[i]); c += s_cornersNear[i] + s_cornersFar[i]; }
            c /= 8f;
            float r = 0f;
            for (int i = 0; i < 4; i++) r = Mathf.Max(r, Mathf.Max((s_cornersNear[i] - c).magnitude, (s_cornersFar[i] - c).magnitude));
            return new Vector4(c.x, c.y, c.z, r * 1.25f + 2f);
        }

        // (re)records the per-cascade caster draws when the layout, the bindings, the light or the cascade count changed; returns the draws recorded
        private static int RecordCascades(int nb)
        {
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            if (sun == null) { DetachCascades(); return 0; }
            if (sun != s_cbLight) { DetachCascades(); s_cascadesDirty = true; }
            if (CasterCullOn != s_lastCasterCull) { s_lastCasterCull = CasterCullOn; s_cascadesDirty = true; s_localDirty = true; }   // a live A/B flip re-records once
            if (SizeSkipOn != s_lastSizeSkip) { s_lastSizeSkip = SizeSkipOn; s_cascadesDirty = true; }   // 1.2.233: the same for the size skip
            // 1.2.210 (Fire's O log: ~2,400 empty caster draws a frame): each cascade records only the batches the CPU cell cull says can
            // reach it (and the sun volume), in material order; it re-records only when one of those lists changed, not on every rebind.
            if (s_recCap < nb || s_orderCount != nb) { if (s_recCap < nb) { s_recCap = Mathf.NextPowerOfTwo(nb); s_recCascade = new bool[4 * s_recCap]; } s_cascadesDirty = true; }
            if (!s_cascadesDirty)
            {
                // re-record when a cascade GAINED a batch (it would miss its shadow), or a quarter of a cascade's recorded batches went stale
                // (they draw 0 instances but still cost a call); a batch that only left stays recorded until then
                bool changed = false;
                for (int k = 0; k < s_cascadeCount; k++) s_staleScratch[k] = 0;
                for (int i = 0; i < nb && !changed; i++)
                {
                    bool casts = CastsShadows(s_batches[i]);
                    for (int k = 0; k < s_cascadeCount; k++)
                    {
                        bool want = casts && CascadeWants(k, i), rec = s_recCascade[k * s_recCap + i];
                        if (want && !rec) { changed = true; break; }
                        if (rec && !want) s_staleScratch[k]++;
                    }
                }
                for (int k = 0; k < s_cascadeCount && !changed; k++) if (s_staleScratch[k] > 8 && s_staleScratch[k] * 4 > s_recCount[k]) changed = true;
                if (!changed) return s_lastCascadeDraws;
            }
            s_cascadesDirty = false;
            s_cascadeRecords++;
            System.Array.Clear(s_sizeSkipped, 0, 4);   // 1.2.233: counted for this recording only
            System.Array.Clear(s_recCascade, 0, s_recCascade.Length);
            for (int k = 0; k < 4; k++) s_recCount[k] = 0;
            int draws = 0;
            for (int k = 0; k < 4; k++)
            {
                if (s_cascadeCbs[k] == null) s_cascadeCbs[k] = new CommandBuffer { name = "EBM indirect casters, cascade " + k };
                s_cascadeCbs[k].Clear();
            }
            for (int j = 0; j < nb; j++)
            {
                int i = s_order[j];
                var b = s_batches[i];
                if (!CastsShadows(b)) continue;
                bool anyK = false;
                for (int k = 0; k < s_cascadeCount && !anyK; k++) anyK = CascadeWants(k, i);
                if (!anyK) continue;
                if (!s_casterInfo.TryGetValue(b, out var props))
                {
                    props = new MaterialPropertyBlock[4];
                    for (int k = 0; k < 4; k++) { props[k] = new MaterialPropertyBlock(); BindCaster(props[k], (2 + k) * CasterStride + b.VisibleOffset); }
                    s_casterInfo[b] = props;
                }
                else if (b.CasterBound != s_bindEpoch) for (int k = 0; k < 4; k++) BindCaster(props[k], (2 + k) * CasterStride + b.VisibleOffset);
                b.CasterBound = s_bindEpoch;
                for (int k = 0; k < s_cascadeCount; k++)
                {
                    if (!CascadeWants(k, i)) continue;
                    s_cascadeCbs[k].DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, b.Material, b.CasterPass, s_argsBuf, ((2 + k) * s_batchCap + i) * ArgWords * sizeof(uint), props[k]);
                    s_recCascade[k * s_recCap + i] = true; s_recCount[k]++;
                    draws++;
                }
            }
            if (s_cbLight != sun || s_cbCascades != s_cascadeCount)
            {
                DetachCascades();
                AttachCapture(sun);   // 1.2.210 (G5): the sphere capture first on cascade 0 (same event: added order), then the casters
                AttachCascadePrep(sun);   // 1.2.211 ([saddles] G6 v2): after the capture, before the casters
                for (int k = 0; k < s_cascadeCount; k++) sun.AddCommandBuffer(CascadeEvent, s_cascadeCbs[k], CascadePass[k]);
                s_cbLight = sun; s_cbCascades = s_cascadeCount;
                EasyBakeLog.Debug($"[Indirect] per-cascade casters: {s_cascadeCount} cascade command buffer(s) on '{sun.name}', {draws} caster draw(s)");
            }
            s_lastCascadeDraws = draws;
            CensusCascades(nb);   // [seasons] 11-09 E9 census (Debug, at a re-record only)
            if (SizeSkipOn && Time.unscaledTime >= s_sizeSayAt && EasyBakeLog.DebugOn()) { s_sizeSayAt = Time.unscaledTime + 30f; EasyBakeLog.Debug($"[Indirect] caster size skip (last recording): cascade 2 skipped {s_sizeSkipped[1]}, cascade 3 {s_sizeSkipped[2]}, cascade 4 {s_sizeSkipped[3]} batch draw(s) under {(FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen != null ? FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen.Value : 0.01f):0.000} of the screen from their split (near {s_cascadeNear[1]:0} / {s_cascadeNear[2]:0} / {s_cascadeNear[3]:0} m); {draws} caster draw(s) recorded"); }
            return draws;
        }

        private static int s_lastCascadeDraws;

        private static void DetachCascades()
        {
            if (s_cbLight != null)
            {
                for (int k = 0; k < 4; k++) if (s_cascadeCbs[k] != null) s_cbLight.RemoveCommandBuffer(CascadeEvent, s_cascadeCbs[k]);
                DetachCapture(s_cbLight);   // 1.2.210 (G5)
            DetachCascadePrep();   // 1.2.211 ([saddles] G6 v2)
            }
            s_cbLight = null; s_cbCascades = 0; s_cascadesDirty = true;
            s_casterInfo.Clear();
            ForgetCasterKeys();   // [seasons] 11-09 E9 census: batches may be new after a detach
        }
        // ── 1.2.208 ([generator] / lead: "submitted" was pre-cull): the GPU's real per-view instance counts, read back asynchronously every 30 s (a frame
        // late, never a stall), Debug: drawn instances + tris per view (camera, sun volume, cascades). Proves the cull works.
        private static float s_nextReadback;
        private static bool s_readbackPending;
        private static void MaybeReadBack(int nb)
        {
            if (s_readbackPending || Time.unscaledTime < s_nextReadback || s_argsBuf == null || nb == 0 || !EasyBakeLog.DebugOn()) return;
            s_nextReadback = Time.unscaledTime + 30f;
            s_readbackPending = true;
            int views = Views, words = ArgWords, count = s_batchCap * views * words;
            var tris = new long[nb]; for (int i = 0; i < nb; i++) tris[i] = s_batches[i].Mesh != null ? s_batches[i].Tris : 0;   // once per 30 s, Debug only
            UnityEngine.Rendering.AsyncGPUReadback.Request(s_argsBuf, count * sizeof(uint), 0, req =>
            {
                s_readbackPending = false;
                if (req.hasError) { EasyBakeLog.Debug("[Indirect] GPU readback failed"); return; }
                var data = req.GetData<uint>();
                var sb = new System.Text.StringBuilder("[Indirect] GPU drew (read back, post-cull):");
                string[] names = { "camera", "sun volume", "cascade 1", "cascade 2", "cascade 3", "cascade 4", "other camera", "light 1", "light 2", "light 3", "light 4",
                                   "pre-culled camera 1", "pre-culled camera 2", "pre-culled camera 3", "pre-culled camera 4" };   // G4: Views = 15
                for (int v = 0; v < views; v++)
                {
                    long inst = 0, tr = 0; int batches = 0;
                    for (int i = 0; i < nb && (v * s_batchCap + i) * words + 1 < data.Length; i++) { uint n = data[(v * s_batchCap + i) * words + 1]; if (n > 0) { batches++; inst += n; tr += n * tris[i]; } }
                    if (v >= 2 && v <= 5 && v - 2 >= s_cascadeCount) continue;
                    sb.Append(' ').Append(names[v]).Append(' ').Append(inst).Append(" inst / ").Append(tr / 1000).Append("k tris in ").Append(batches).Append(" batch(es);");
                }
                EasyBakeLog.Debug(sb.ToString());
            });
        }
    }
}
