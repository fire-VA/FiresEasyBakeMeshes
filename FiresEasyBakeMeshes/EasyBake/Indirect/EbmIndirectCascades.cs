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
        internal static readonly float[] s_cascadeFar = new float[4];   // [seasons] 11-11: the split's far distance (the caster receiver cull's spheres)
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
        // 1.2.367 ([visual], [lead]: tail row 5 of O_20261114_015419): the re-record (Clear + one DrawMeshInstancedIndirect per recorded batch per cascade) in its own
        // scope, so a capture separates the frames that RE-RECORD from the per-frame REPLAY of the four cascade buffers. Marker only.
        private static readonly Unity.Profiling.ProfilerMarker s_mReRecord = new Unity.Profiling.ProfilerMarker("EBM casters: re-record");
        private static int s_bindEpoch;                     // 1.2.210: bumped by every rebind (Draw); a block bound in an older epoch is re-bound before use
        // 1.2.372 ([visual], [lead] approved; cold-entry capture O_20261114_104123, worst 5 s: "EBM casters: re-record" 1.5 ms in 56 of 82 frames): on a cold
        // entry every arriving caster batch is placed alone and trips "want && !rec", and the four cascade buffers were CLEARED and re-recorded for that one
        // batch (one DrawMeshInstancedIndirect per caster batch per cascade). A batch placed alone changes nothing of the batches already recorded (their
        // VisibleOffset, args rows by b.Index and s_batchCap stay; a MOVED batch dirties the cascades through NoteCasterMoved as before), so with
        // [Optimize] CascadeAppendOnGain the new rows are APPENDED to the existing buffers instead. A full re-record still runs when forced (layout, light,
        // cascade count, epoch, moved batch), when a quarter of a cascade's rows went stale, or when the appended rows exceed a quarter of the recorded ones
        // (the appended tail is not in material order). Debug: the re-record reasons and the appends on the [Indirect] 30-s line.
        private static readonly Unity.Profiling.ProfilerMarker s_mAppend = new Unity.Profiling.ProfilerMarker("EBM casters: append");
        internal static int s_recForced, s_recGained, s_recStale, s_recAppends, s_recAppendedDraws;   // Debug: since the last [Indirect] line
        private static readonly int[] s_appendCount = new int[4];   // rows appended to cascade k since its last full record
        private static bool AppendOnGainOn => FiresEasyBakeMeshesPlugin.CascadeAppendOnGain != null && FiresEasyBakeMeshesPlugin.CascadeAppendOnGain.Value;
        // 1.2.388 ([visual], [lead]; Fire: the Mistlands gap vs the rival; 11-13 gcircle3 O log: "record cascades 0.023" ms a frame with "279 recorded ... replayed every
        // frame" and "8815 of 9228 frame(s) reused the last cell verdict"): on a frame that re-records nothing, RecordCascades still walked every batch x every cascade to
        // compare "wanted" with "recorded". With [Optimize] IndirectCascadeScanReuse that scan is skipped while everything it reads is unchanged since the last scan that
        // found nothing to do (or the last full record): the CPU rows are exactly last frame's (s_cellVerdictSame, the flag the light-shadow scan already uses: same
        // camera cell verdict, no placement change), the empty-cascade samples have not been read back since (s_emptyStamp), and the size-skip inputs (the cascades'
        // near distances, the field of view) are equal. [seasons]' review: s_emptyStamp also moves on a removal-only frame and when RefreshAlive sees a mesh /
        // material die or come back; with IndirectCachedAlive off (no cached alive bits to compare) the scan is never skipped. Every other input already marks s_cascadesDirty (keys, light, cascade count, layout, a moved batch). Exact:
        // the scan's answer is a pure function of those inputs.
        private static bool ScanReuseOn => FiresEasyBakeMeshesPlugin.IndirectCascadeScanReuse != null && FiresEasyBakeMeshesPlugin.IndirectCascadeScanReuse.Value;
        private static bool s_scanClean, s_lastScanReuse;
        private static int s_scanEmptyStamp = -1;
        internal static int s_emptyStamp;                   // bumped whenever the empty-cascade streaks may have changed (a sample read back, the streaks cleared)
        private static readonly float[] s_scanNear = new float[4];
        private static float s_scanHalfTan;
        internal static int s_recScanSkipped;               // Debug: since the last [Indirect] line
        private static readonly int[] s_recMatSwitch = new int[4];        // Debug: caster material changes between consecutive draws, per cascade, at the last full record
        private static readonly Material[] s_recLastMat = new Material[4];
        private static void ScanStore()
        {
            s_scanClean = true; s_scanEmptyStamp = s_emptyStamp; s_scanHalfTan = s_sizeHalfTan;
            for (int k = 0; k < 4; k++) s_scanNear[k] = s_cascadeNear[k];
        }
        private static bool ScanInputsSame()
        {
            if (s_scanEmptyStamp != s_emptyStamp || s_scanHalfTan != s_sizeHalfTan) return false;
            for (int k = 0; k < 4; k++) if (s_scanNear[k] != s_cascadeNear[k]) return false;
            return true;
        }

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
                        s_cascadeFar[k] = far;
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
            // 1.2.300 ([lead]/[wishbone] VPO read: Unity's real split spheres vs our x1.25 + 2 m estimate): the safety factors are keys now; the
            // defaults are the old values (no change), a smaller sphere = fewer casters per cascade but a risk of a missing shadow at a cascade edge
            float scale = FiresEasyBakeMeshesPlugin.IndirectCascadeSphereScale != null ? Mathf.Clamp(FiresEasyBakeMeshesPlugin.IndirectCascadeSphereScale.Value, 1f, 2f) : 1.25f;
            float margin = FiresEasyBakeMeshesPlugin.IndirectCascadeSphereMarginM != null ? Mathf.Clamp(FiresEasyBakeMeshesPlugin.IndirectCascadeSphereMarginM.Value, 0f, 20f) : 2f;
            return new Vector4(c.x, c.y, c.z, r * scale + margin);
        }

        // (re)records the per-cascade caster draws when the layout, the bindings, the light or the cascade count changed; returns the draws recorded
        private static int RecordCascades(int nb)
        {
            var sun = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
            if (sun == null) { DetachCascades(); return 0; }
            if (sun != s_cbLight) { DetachCascades(); s_cascadesDirty = true; }
            if (CasterCullOn != s_lastCasterCull) { s_lastCasterCull = CasterCullOn; s_cascadesDirty = true; s_localDirty = true; }   // a live A/B flip re-records once
            if (SizeSkipOn != s_lastSizeSkip) { s_lastSizeSkip = SizeSkipOn; s_cascadesDirty = true; }   // 1.2.233: the same for the size skip
            if (ScanReuseOn != s_lastScanReuse) { s_lastScanReuse = ScanReuseOn; s_scanClean = false; EasyBakeLog.Info($"[Indirect] IndirectCascadeScanReuse {(s_lastScanReuse ? "on: the per-frame cascade scan is skipped while nothing it reads changed" : "off: the cascade scan runs every frame")}"); }   // 1.2.388
            // 1.2.380 ([visual], [lead] hot-swap audit): the same for the two values that only take rows OUT. A row recorded before the flip kept casting until a
            // quarter of its cascade went stale (IndirectFarCardShadows turned on; IndirectCasterMinScreen raised), so a live OFF / ON pair measured the old rows.
            if (FarCardOn != s_lastFarCard && s_lastCascadeDraws > 0) NoteCasterFlip("IndirectFarCardShadows " + (FarCardOn ? "on" : "off"));   // 1.2.384: before the line below takes the new value
            if (FarCardOn != s_lastFarCard) { s_lastFarCard = FarCardOn; s_cascadesDirty = true; }
            float casterMin = FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen != null ? FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen.Value : 0.01f;
            if (casterMin != s_lastCasterMin) { s_lastCasterMin = casterMin; s_cascadesDirty = true; }
            // 1.2.210 (Fire's O log: ~2,400 empty caster draws a frame): each cascade records only the batches the CPU cell cull says can
            // reach it (and the sun volume), in material order; it re-records only when one of those lists changed, not on every rebind.
            if (s_recCap < nb || s_orderCount != nb) { if (s_recCap < nb) { s_recCap = Mathf.NextPowerOfTwo(nb); s_recCascade = new bool[4 * s_recCap]; } s_cascadesDirty = true; }
            if (!s_cascadesDirty)
            {
                // 1.2.388 [Optimize] IndirectCascadeScanReuse: nothing the scan below reads has changed since it last found nothing to do
                if (s_lastScanReuse && s_scanClean && s_cellVerdictSame && s_aliveValid && ScanInputsSame()) { s_recScanSkipped++; return s_lastCascadeDraws; }
                // re-record when a cascade GAINED a batch (it would miss its shadow), or a quarter of a cascade's recorded batches went stale
                // (they draw 0 instances but still cost a call); a batch that only left stays recorded until then
                bool changed = false, gained = false, stale = false;   // 1.2.372: the reasons apart
                for (int k = 0; k < s_cascadeCount; k++) s_staleScratch[k] = 0;
                for (int i = 0; i < nb && !changed; i++)
                {
                    bool casts = CasterRowCasts(i);   // E9: a merged member is never a caster row; its leader stands for the group
                    for (int k = 0; k < s_cascadeCount; k++)
                    {
                        // 1.2.299 ([lead] town_FarCards: ON 0.68 ms SLOWER): a far card the recording skips was still "wanted" here, so want && !rec held for
                        // every card batch on every frame and the cascade buffers re-recorded EVERY FRAME (town: hundreds of card batches). Skipped = not wanted.
                        bool want = casts && CasterRowWantsCascade(k, i) && !(k >= FarCardFromCascade && FarCardSkip(s_batches[i])), rec = s_recCascade[k * s_recCap + i];
                        if (want && !rec) { gained = true; if (!AppendOnGainOn) { changed = true; break; } }   // 1.2.372: with the key on the scan completes, so stale is known too
                        if (rec && !want) s_staleScratch[k]++;
                    }
                }
                for (int k = 0; k < s_cascadeCount && !changed; k++) if (s_staleScratch[k] > 8 && s_staleScratch[k] * 4 > s_recCount[k]) { changed = true; stale = true; }
                if (!changed && gained) { if (AppendShareOk()) { AppendGained(nb); return s_lastCascadeDraws; } changed = true; }   // 1.2.372: a gain alone appends the new rows; too many appended = a full record restores the material order
                if (!changed) ScanStore();   // 1.2.388: this scan found nothing to do; the next ones are skipped while its inputs stand
                if (!changed) return s_lastCascadeDraws;
                if (stale) s_recStale++; else s_recGained++;   // 1.2.372 Debug: why this full re-record
            }
            else s_recForced++;   // 1.2.372 Debug: layout / light / cascade count / epoch / moved batch
            s_cascadesDirty = false;
            s_cascadeRecords++;
            System.Array.Clear(s_sizeSkipped, 0, 4);   // 1.2.233: counted for this recording only
            System.Array.Clear(s_recCascade, 0, s_recCascade.Length);
            for (int k = 0; k < 4; k++) s_recCount[k] = 0;
            for (int k = 0; k < 4; k++) { s_recMatSwitch[k] = 0; s_recLastMat[k] = null; }   // 1.2.388 Debug
            System.Array.Clear(s_appendCount, 0, 4);   // 1.2.372: a full record starts a fresh appended count
            int draws = 0;
            using (s_mReRecord.Auto())   // 1.2.367 ([visual]): the re-record frames, apart from the per-frame replay of the cascade buffers (loop bodies unchanged)
            {
            for (int k = 0; k < 4; k++)
            {
                if (s_cascadeCbs[k] == null) s_cascadeCbs[k] = new CommandBuffer { name = "EBM indirect casters, cascade " + k };
                s_cascadeCbs[k].Clear();
            }
            for (int j = 0; j < nb; j++)
            {
                int i = s_order[j];
                var b = s_batches[i];
                if (!CasterRowCasts(i)) continue;   // E9: members skipped; a leader is recorded when any member casts and is wanted
                bool anyK = false;
                for (int k = 0; k < s_cascadeCount && !anyK; k++) anyK = CasterRowWantsCascade(k, i);
                if (!anyK) continue;
                var casterMat = CasterMaterialOf(b, out int casterPass);   // E9: EBM/SharedCaster for a group, the batch's own otherwise
                if (casterPass < 0) continue;
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
                    if (!CasterRowWantsCascade(k, i)) continue;
                    if (k >= FarCardFromCascade && FarCardSkip(b)) { s_farCardSkipped++; continue; }   // 1.2.297: no far-cascade shadow for small alpha-cut cards
                    if (!ReferenceEquals(casterMat, s_recLastMat[k])) { s_recMatSwitch[k]++; s_recLastMat[k] = casterMat; }   // 1.2.388 Debug: the record is in (shader, material, mesh) order since 1.2.210, so this is the least SetPass count this cascade can have
                    s_cascadeCbs[k].DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, casterMat, casterPass, s_argsBuf, ((2 + k) * s_batchCap + i) * ArgWords * sizeof(uint), props[k]);
                    s_recCascade[k * s_recCap + i] = true; s_recCount[k]++;
                    draws++;
                }
            }
            }   // 1.2.367: end of the re-record scope
            if (s_cbLight != sun || s_cbCascades != s_cascadeCount)
            {
                DetachCascades();
                AttachCapture(sun);   // 1.2.210 (G5): the sphere capture first on cascade 0 (same event: added order), then the casters
                AttachCascadePrep(sun);   // 1.2.211 ([saddles] G6 v2): after the capture, before the casters
                for (int k = 0; k < s_cascadeCount; k++) sun.AddCommandBuffer(CascadeEvent, s_cascadeCbs[k], CascadePass[k]);
                s_cbLight = sun; s_cbCascades = s_cascadeCount;
                EasyBakeLog.Debug($"[Indirect] per-cascade casters: {s_cascadeCount} cascade command buffer(s) on '{sun.name}', {draws} caster draw(s)");
            }
            // 1.2.384 ([visual], [lead]: the same standard as the walk report): a live flip of a caster key says once what it did to the recorded caster draws.
            if (s_flipNote != null)
            {
                EasyBakeLog.Info($"[Indirect] {s_flipNote}: cascade caster draws {s_flipBefore} -> {draws} (per cascade now {s_recCount[0]}/{s_recCount[1]}/{s_recCount[2]}/{s_recCount[3]}), re-recorded once{(draws == s_flipBefore ? "; no effect in this scene" : "")}");
                s_flipNote = null;
            }
            s_lastCascadeDraws = draws;
            ScanStore();   // 1.2.388: a full record leaves "recorded" equal to "wanted" for these inputs
            CensusCascades(nb);   // [seasons] 11-09 E9 census (Debug, at a re-record only)
            if (SizeSkipOn && Time.unscaledTime >= s_sizeSayAt && EasyBakeLog.DebugOn()) { s_sizeSayAt = Time.unscaledTime + 30f; EasyBakeLog.Debug($"[Indirect] caster size skip (last recording): cascade 2 skipped {s_sizeSkipped[1]}, cascade 3 {s_sizeSkipped[2]}, cascade 4 {s_sizeSkipped[3]} batch draw(s) under {(FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen != null ? FiresEasyBakeMeshesPlugin.IndirectCasterMinScreen.Value : 0.01f):0.000} of the screen from their split (near {s_cascadeNear[1]:0} / {s_cascadeNear[2]:0} / {s_cascadeNear[3]:0} m); {draws} caster draw(s) recorded"); }
            return draws;
        }

        // 1.2.297 [Optimize] IndirectFarCardShadows ([lead] swamp replay: the swamp trees' "deadbranch" cards cost ~0.06-0.07 ms per cascade x 4): in the far
        // cascades (from the 3rd) a batch whose mesh part is a small alpha-cut card (at most FarCardMaxTris triangles, a material with _Cutoff / the
        // AlphaTest queue) is not recorded as a caster. Its shadow there is a few texels of dappled noise at shadow-map resolution; the near cascades and
        // the camera keep it exactly. Off = as before. Judged once per batch.
        private const int FarCardFromCascade = 2, FarCardMaxTris = 64;
        internal static int s_farCardSkipped;
        private static bool s_lastFarCard; private static float s_lastCasterMin = -1f;   // 1.2.380: a live flip re-records once (RecordCascades)
        private static bool FarCardOn => FiresEasyBakeMeshesPlugin.IndirectFarCardShadows != null && FiresEasyBakeMeshesPlugin.IndirectFarCardShadows.Value;   // 1.2.299: default OFF until the fixed re-record path is measured
        private static bool FarCardSkip(Batch b)
        {
            if (!FarCardOn || b == null) return false;
            if (b.FarCard < 0)
            {
                bool card = false;
                try
                {
                    var m = b.Material; var mesh = b.Mesh;
                    if (m != null && mesh != null && b.SubMesh >= 0 && b.SubMesh < mesh.subMeshCount)
                    {
                        bool cut = (m.HasProperty("_Cutoff") && m.GetFloat("_Cutoff") > 0f) || (m.renderQueue >= 2450 && m.renderQueue < 2500) || m.IsKeywordEnabled("_ALPHATEST_ON");
                        card = cut && mesh.GetIndexCount(b.SubMesh) / 3 <= FarCardMaxTris;
                    }
                }
                catch { card = false; }
                b.FarCard = (sbyte)(card ? 1 : 0);
            }
            return b.FarCard == 1;
        }

        // 1.2.372: appended rows stay a small tail of each cascade's buffer (same shape as the stale rule: more than 8 and over a quarter = full record)
        private static bool AppendShareOk()
        {
            for (int k = 0; k < s_cascadeCount; k++) if (s_appendCount[k] > 8 && s_appendCount[k] * 4 > s_recCount[k]) return false;
            return true;
        }

        // 1.2.372: records only the rows a cascade gained (want && !rec) into the EXISTING buffers, no Clear. The buffers are attached and every
        // recorded row is still right (not dirty), so the new batch's shadow appears in this frame as it would after a full re-record.
        private static void AppendGained(int nb)
        {
            s_recAppends++;
            using (s_mAppend.Auto())
            {
                for (int j = 0; j < nb; j++)
                {
                    int i = s_order[j];
                    if (!CasterRowCasts(i)) continue;   // E9: members skipped; the leader stands for the group
                    var b = s_batches[i];
                    bool need = false;
                    for (int k = 0; k < s_cascadeCount && !need; k++) need = !s_recCascade[k * s_recCap + i] && CasterRowWantsCascade(k, i) && !(k >= FarCardFromCascade && FarCardSkip(b));
                    if (!need) continue;
                    var casterMat = CasterMaterialOf(b, out int casterPass);
                    if (casterPass < 0) continue;
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
                        if (s_recCascade[k * s_recCap + i] || !CasterRowWantsCascade(k, i)) continue;
                        if (k >= FarCardFromCascade && FarCardSkip(b)) { s_farCardSkipped++; continue; }   // as the full record: no far-cascade shadow for small alpha-cut cards
                        s_cascadeCbs[k].DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, casterMat, casterPass, s_argsBuf, ((2 + k) * s_batchCap + i) * ArgWords * sizeof(uint), props[k]);
                        s_recCascade[k * s_recCap + i] = true; s_recCount[k]++; s_appendCount[k]++; s_recAppendedDraws++; s_lastCascadeDraws++;
                    }
                }
            }
        }

        private static int s_lastCascadeDraws;
        // 1.2.384: set when a caster key flips mid-run (while draws are recorded), printed and cleared by the next full record (RecordCascades).
        private static string s_flipNote; private static int s_flipBefore;
        private static void NoteCasterFlip(string what)
        {
            if (s_flipNote == null) { s_flipNote = what; s_flipBefore = s_lastCascadeDraws; }
            else if (s_flipNote.IndexOf(what, System.StringComparison.Ordinal) < 0) s_flipNote += " + " + what;
        }

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
        // 1.2.338 ([visual]; [generator] on e8-ashd2-census-b: "cascade 1..4 0 inst" with the same-frame cull on): with IndirectShadowSameFrame the
        // Update cull's EbmClear zeroes views 2..5 and the shadow-pass prep (EbmCullCascades) fills them LATER in the frame, so a readback requested
        // here captured the zeros. Now, while the prep runs, the request is ARMED here and recorded by RecordCascadePrep right after its dispatch
        // (CommandBuffer.RequestAsyncReadback, as the caster receiver census does), so the cascade rows hold the shadow pass's own counts. If the
        // prep does not run within 5 s (no sun shadow that frame, another camera), the next MaybeReadBack requests from here as before.
        private static bool s_readbackArmed; private static float s_readbackArmedAt;
        private static long[] s_rbTris; private static byte[] s_rbLodKind; private static int s_rbNb; private static bool s_rbAfterPrep;
        private static System.Action<UnityEngine.Rendering.AsyncGPUReadbackRequest> s_onArgsReadback;
        private static void MaybeReadBack(int nb)
        {
            if (s_readbackArmed && Time.unscaledTime > s_readbackArmedAt + 5f)
            {
                // [generator]: the prep never recorded it (or a clear lost it): stay armed for the next prep, never the LateUpdate request (that reads
                // the zeroed cascade rows) - unless the same-frame path has stopped meanwhile (sun shadows off, key off): then LateUpdate IS the right read
                if (s_cascSeparate && s_prepThisFrame) { s_readbackArmedAt = Time.unscaledTime; return; }
                s_readbackArmed = false; s_rbAfterPrep = false;
                UnityEngine.Rendering.AsyncGPUReadback.Request(s_argsBuf, s_batchCap * Views * ArgWords * sizeof(uint), 0, s_onArgsReadback);
                return;
            }
            if (s_readbackPending && !s_readbackArmed && Time.unscaledTime > s_readbackArmedAt + 10f) s_readbackPending = false;   // a recorded request whose callback never came (prep executed on a skipped cascade 0)
            if (s_readbackPending || Time.unscaledTime < s_nextReadback || s_argsBuf == null || nb == 0 || !EasyBakeLog.DebugOn()) return;
            s_nextReadback = Time.unscaledTime + 30f;
            s_readbackPending = true;
            var tris = new long[nb]; for (int i = 0; i < nb; i++) tris[i] = s_batches[i].Mesh != null ? s_batches[i].Tris : 0;   // once per 30 s, Debug only
            // 1.2.337 ([generator]'s review): the LOD class is snapshotted here too; the callback never reads s_batches (it may be rebuilt before then)
            var lodKind = new byte[nb]; for (int i = 0; i < nb; i++) { var lb = s_batches[i]; lodKind[i] = lb.LodPerInstance ? (byte)2 : lb.StartHeight >= float.MaxValue ? (byte)0 : (byte)1; }
            s_rbTris = tris; s_rbLodKind = lodKind; s_rbNb = nb;
            if (s_onArgsReadback == null) s_onArgsReadback = OnArgsReadback;
            if (s_cascSeparate && s_prepThisFrame) { s_readbackArmed = true; s_readbackArmedAt = Time.unscaledTime; s_rbAfterPrep = true; return; }   // RecordCascadePrep records it
            s_rbAfterPrep = false;
            UnityEngine.Rendering.AsyncGPUReadback.Request(s_argsBuf, s_batchCap * Views * ArgWords * sizeof(uint), 0, s_onArgsReadback);
        }
        /// <summary>From RecordCascadePrep, after the cascade cull dispatch: records the armed readback into the prep buffer.</summary>
        private static void RecordArmedReadback(UnityEngine.Rendering.CommandBuffer cb)
        {
            if (!s_readbackArmed) return;
            s_readbackArmed = false; s_readbackArmedAt = Time.unscaledTime;
            cb.RequestAsyncReadback(s_argsBuf, s_batchCap * Views * ArgWords * sizeof(uint), 0, s_onArgsReadback);
        }
        private static void OnArgsReadback(UnityEngine.Rendering.AsyncGPUReadbackRequest req)
        {
            // [generator]: a nested camera (planar reflection) keeps and re-executes main's prep buffer in the same frame, so one recorded request
            // can call back twice; the first callback is the shadow pass's, the rest are ignored
            if (!s_readbackPending) return;
            s_readbackPending = false;
            var tris = s_rbTris; var lodKind = s_rbLodKind; int nb = s_rbNb, views = Views, words = ArgWords;
            if (tris == null || lodKind == null) return;
            {
                if (req.hasError) { EasyBakeLog.Debug("[Indirect] GPU readback failed"); return; }
                var data = req.GetData<uint>();
                var sb = new System.Text.StringBuilder(s_rbAfterPrep ? "[Indirect] GPU drew (read back after the shadow-pass cascade cull):" : "[Indirect] GPU drew (read back, post-cull; cascades not culled this frame or culled the 1.2.208 way):");
                string[] names = { "camera", "sun volume", "cascade 1", "cascade 2", "cascade 3", "cascade 4", "other camera", "light 1", "light 2", "light 3", "light 4",
                                   "pre-culled camera 1", "pre-culled camera 2", "pre-culled camera 3", "pre-culled camera 4" };   // G4: Views = 15
                for (int v = 0; v < views; v++)
                {
                    long inst = 0, tr = 0, tr0 = 0, trC = 0, trM = 0; int batches = 0;
                    for (int i = 0; i < nb && (v * s_batchCap + i) * words + 1 < data.Length; i++)
                    {
                        uint n = data[(v * s_batchCap + i) * words + 1]; if (n == 0) continue;
                        batches++; inst += n; long t = n * tris[i]; tr += t;
                        // 1.2.337 ([visual]; E8 IndirectCoarseCasters was only ever measured in the Swamp, noise): in the cascade views, the triangles by the
                        // batch's LOD level: LOD0 / single-level (StartHeight MaxValue), a coarser level, or a per-instance-window batch (its lanes mix levels).
                        // Says whether E8 actually moves the far casters to the lighter meshes (the LodCensus counts near meshes whatever the cull picks).
                        if (v >= 2 && v <= 5) { if (lodKind[i] == 2) trM += t; else if (lodKind[i] == 0) tr0 += t; else trC += t; }   // from the request-time snapshot, as tris[]
                    }
                    if (v >= 2 && v <= 5 && v - 2 >= s_cascadeCount) continue;
                    sb.Append(' ').Append(names[v]).Append(' ').Append(inst).Append(" inst / ").Append(tr / 1000).Append("k tris in ").Append(batches).Append(" batch(es)");
                    if (v >= 2 && v <= 5 && tr > 0) sb.Append(" [LOD0/single ").Append(tr0 / 1000).Append("k, coarser ").Append(trC / 1000).Append("k").Append(trM > 0 ? ", mixed lanes " + (trM / 1000) + "k" : "").Append(']');   // 1.2.337
                    sb.Append(';');
                }
                EasyBakeLog.Debug(sb.ToString());
            }
        }
    }
}
