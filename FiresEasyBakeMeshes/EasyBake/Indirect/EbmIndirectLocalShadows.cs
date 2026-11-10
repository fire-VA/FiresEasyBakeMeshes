using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.209 (VPO parity item 2, [wishbone] for [visual]): point and spot light shadows for the indirect batches.
    // With [Optimize] IndirectCascadeShadows on, the sun's casters are drawn from its cascade command buffers and the ShadowsOnly draws
    // are skipped, so until now no point or spot light got an indirect caster: a lit fireplace cast no shadow of a baked wall.
    // Now the lights vanilla's LightLod has given shadows this moment (light.shadows != None, enabled, its range inside the camera
    // frustum; at most LightLod.m_shadowLimit and never more than 4, nearest first) each get ONE culled caster list (views 7..10):
    //   - point light: the instance's bounding sphere against the light's range sphere;
    //   - spot light: that, AND the instance sphere against the light's cone (axis + half angle), so a spot draws only what it can light
    //     (tighter than a range sphere: VPO tests spheres only).
    // One command buffer per light on LightEvent.BeforeShadowMapPass, attached with ShadowMapPass.Pointlight (all six faces share the one
    // list; Unity sets each face's matrices) or ShadowMapPass.Spotlight, drawing with the material's own ShadowCaster pass, the same way
    // EbmIndirectCascades draws the sun's cascades. Re-recorded only when the light set, the layout or the bindings change.
    // The method (per-light caster lists from the light's command buffers) was learned from VPO's renderer (ontrigger, MIT, read for
    // reference only: IndirectRenderer.LocalShadows.cs, ShadowedLights.cs); this code is ours.
    internal static partial class EbmIndirect
    {
        private const int LocalView0 = 7, MaxLocal = 4;
        private static readonly Light[] s_local = new Light[MaxLocal];            // this frame's lights, nearest first
        private static readonly float[] s_localDist = new float[MaxLocal];
        private static readonly Vector4[] s_localSpheres = new Vector4[MaxLocal]; // xyz position, w range
        private static readonly Vector4[] s_localCones = new Vector4[MaxLocal];   // xyz axis, w cos(half angle); w <= -1 = a point light
        private static readonly Vector4[] s_localSinV = new Vector4[MaxLocal];    // x sin(half angle) (a float4 array uploads one per light)
        private static int s_localCount, s_lastLocalDraws;
        private static bool s_localDirty = true;
        private static readonly Dictionary<Batch, MaterialPropertyBlock[]> s_localCasterInfo = new Dictionary<Batch, MaterialPropertyBlock[]>();   // 1.2.210: pass on Batch.CasterPass

        // vanilla LightLod's private registry, read through a field ref (a publicized private field throws at JIT in this pack)
        private static HashSet<LightLod> s_lightLods;
        private static AccessTools.FieldRef<LightLod, Light> s_lodLight;
        private static bool s_lightLodLooked;
        private static string s_localState;

        private static readonly int IdLocalSpheres = Shader.PropertyToID("_EbmLocalSpheres");
        private static readonly int IdLocalCones = Shader.PropertyToID("_EbmLocalCones");
        private static readonly int IdLocalSin = Shader.PropertyToID("_EbmLocalSin");
        private static readonly int IdLocalCount = Shader.PropertyToID("_EbmLocalCount");

        private static bool LocalOn => CascadesOn && s_localCount > 0;

        // Called at the end of Cull (after the main dispatch, which already cleared every view's counts): pick the lights, cull views 7..10.
        private static void CullLocal(ComputeShader cs, int kernel, int groups)
        {
            if (!s_cellsFused) GatherLocalLights();   // 1.2.213 IndirectFusedCells: gathered (and their cells culled) before the main dispatch
            if (s_localCount == 0) return;
            if (!s_cellsFused)
            {
                // 1.2.210: the CPU cells judge each light's range sphere too (rows 7..10), so a light records only batches it can reach
                s_cells.CullSpheres(s_localSpheres, s_localCount, Vector3.zero, CpuSphereMargin, s_vis, LocalView0, s_cullCamPos, s_cullLodScale);
                long tF = PhT(); CullFaces(); PhAcc(PhCellsFace, tF);   // 1.2.210 IndirectPointFaces: + each cube face's frustum
            }
            cs.SetVectorArray(IdLocalSpheres, s_localSpheres);
            cs.SetVectorArray(IdLocalCones, s_localCones);
            cs.SetVectorArray(IdLocalSin, s_localSinV);
            cs.SetInt(IdLocalCount, s_localCount);
            cs.SetInt(EbmIndirectAssets.IdViewMode, 2);   // only views 7..10 (the other uniforms are the main pass's, set just before)
            cs.Dispatch(kernel, Mathf.Min(groups, 65535), Mathf.Max(1, Mathf.CeilToInt(groups / 65535f)), 1);
            cs.SetInt(EbmIndirectAssets.IdViewMode, 0);
        }

        private static void GatherLocalLights()
        {
            s_localCount = 0;
            if (!CascadesOn) return;   // with cascades off the ShadowsOnly draws already reach every light
            if (!s_lightLodLooked)
            {
                s_lightLodLooked = true;
                try
                {
                    s_lightLods = AccessTools.Field(typeof(LightLod), "m_lights")?.GetValue(null) as HashSet<LightLod>;
                    s_lodLight = AccessTools.FieldRefAccess<LightLod, Light>("m_light");
                }
                catch (System.Exception e) { s_lightLods = null; EasyBakeLog.Debug("[Indirect] local shadows: LightLod's light list is not readable (" + e.Message + ")"); }
                NoteLocalState(s_lightLods != null ? "on: casters for the " + MaxLocal + " nearest point / spot lights LightLod gives shadows (a 5th or later shadowed light shadows live pieces only, not baked ones)"
                                                   : "off: vanilla LightLod's light list is missing on this game version");
            }
            if (s_lightLods == null) return;
            int limit = LightLod.m_shadowLimit;
            int cap = limit >= 0 ? Mathf.Min(MaxLocal, limit) : MaxLocal;
            if (cap <= 0) return;
            Vector3 camPos = s_drawCamera != null ? s_drawCamera.transform.position : Vector3.zero;
            if (LightsSweptOn) GatherSwept(cap, camPos);   // [Optimize] IndirectLightsSwept (EbmIndirectLightsSwept.cs)
            else foreach (var lod in s_lightLods) GatherOne(lod, cap, camPos);
            for (int k = 0; k < s_localCount; k++)
            {
                var l = s_local[k];
                Vector3 pos = l.transform.position;
                s_localSpheres[k] = new Vector4(pos.x, pos.y, pos.z, l.range);
                if (l.type == LightType.Spot)
                {
                    float half = Mathf.Clamp(l.spotAngle * 0.5f, 0.1f, 89.9f) * Mathf.Deg2Rad;
                    Vector3 ax = l.transform.forward;
                    s_localCones[k] = new Vector4(ax.x, ax.y, ax.z, Mathf.Cos(half));
                    s_localSinV[k] = new Vector4(Mathf.Sin(half), 0f, 0f, 0f);
                }
                else { s_localCones[k] = new Vector4(0f, 0f, 0f, -2f); s_localSinV[k] = Vector4.zero; }
            }
            for (int k = s_localCount; k < MaxLocal; k++) { s_local[k] = null; s_localSpheres[k] = Vector4.zero; s_localCones[k] = new Vector4(0f, 0f, 0f, -2f); s_localSinV[k] = Vector4.zero; }
        }

        // the light's range sphere against the main camera's frustum planes (s_cam, set by Cull this frame)
        private static bool SphereInCamera(Vector3 c, float r)
        {
            for (int i = 0; i < 6; i++) if (s_cam[i].GetDistanceToPoint(c) < -r) return false;
            return true;
        }

        // ---- 1.2.210 (VPO study #9, our own method): one command buffer SLOT per spot light, or per cube FACE of a point light
        // (IndirectPointFaces); each slot records only the batches the CPU cells say it can reach, in material order, and re-records only
        // when it GAINED a batch or a quarter of its list went stale. The GPU list stays one per light (views 7..10); faces only filter batches.
        private sealed class CasterSlot
        {
            public CommandBuffer Cb; public Light Light; public ShadowMapPass Pass; public int LightIndex, Face = -1;
            public bool[] Rec = new bool[0]; public int RecCount; public bool Bias;
            public int Parked;   // 1.2.213 IndirectLocalSteady: frames this slot has been past the wanted list, still attached
        }
        private static int s_slotAttached;   // 1.2.213: slots 0..s_slotAttached-1 may hold an attached buffer (>= s_slotCount while some are parked)
        private const int MaxSlots = MaxLocal * 6;
        private static readonly CasterSlot[] s_slots = new CasterSlot[MaxSlots];
        private static int s_slotCount;
        internal static int s_localRecords;   // Debug: slot re-records since the last [Indirect] line
        private static readonly ShadowMapPass[] FacePass = { ShadowMapPass.PointlightPositiveX, ShadowMapPass.PointlightNegativeX, ShadowMapPass.PointlightPositiveY, ShadowMapPass.PointlightNegativeY, ShadowMapPass.PointlightPositiveZ, ShadowMapPass.PointlightNegativeZ };
        private static readonly Quaternion[] FaceRot = { Quaternion.LookRotation(Vector3.right, Vector3.up), Quaternion.LookRotation(Vector3.left, Vector3.up), Quaternion.LookRotation(Vector3.up, Vector3.forward), Quaternion.LookRotation(Vector3.down, Vector3.forward), Quaternion.LookRotation(Vector3.forward, Vector3.up), Quaternion.LookRotation(Vector3.back, Vector3.up) };
        private static readonly Plane[] s_facePlanes = new Plane[MaxLocal * 6 * 6];
        private static readonly Plane[] s_faceScratch = new Plane[6];
        private static bool[] s_faceVis = new bool[0];
        private static int s_faceWidth;
        private static bool FacesOn => FiresEasyBakeMeshesPlugin.IndirectPointFaces == null || FiresEasyBakeMeshesPlugin.IndirectPointFaces.Value;
        private static bool s_lastFaces = true;

        // the six face frusta of every gathered light (92 degrees, 1 m padding), judged by the CPU cells into s_faceVis (Cull, after the sphere rows)
        private static void CullFaces()
        {
            int views = PrepareFaces();
            if (views == 0) return;
            s_cells.CullViews(s_facePlanes, views, s_cullCamPos, s_cullLodScale, 0f, s_faceVis, 0f);
            s_faceWidth = s_cells.BatchCount;
        }

        // 1.2.213: the face planes + output size, without the cull (IndirectFusedCells culls them in the one walk); 0 = no faces this frame
        private static int PrepareFaces()
        {
            s_faceWidth = 0;
            if (!FacesOn || s_localCount == 0) return 0;
            for (int k = 0; k < s_localCount; k++)
            {
                var l = s_local[k];
                Vector3 pos = l.transform.position;
                float near = Mathf.Max(0.01f, l.shadowNearPlane), far = Mathf.Max(near * 2f, l.range);
                for (int f = 0; f < 6; f++)
                {
                    var view = Matrix4x4.TRS(pos, FaceRot[f], new Vector3(1f, 1f, -1f)).inverse;
                    GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(92f, 1f, near, far) * view, s_faceScratch);
                    for (int q = 0; q < 6; q++) { var pl = s_faceScratch[q]; pl.distance += 1f; s_facePlanes[(k * 6 + f) * 6 + q] = pl; }
                }
            }
            int views = s_localCount * 6, width = Mathf.Max(1, s_cells.BatchCount);
            if (s_faceVis.Length < views * width) s_faceVis = new bool[Mathf.NextPowerOfTwo(views * width)];
            return views;
        }

        private static bool FaceVis(int k, int f, int i) => s_faceWidth == 0 || i >= s_faceWidth || s_faceVis[(k * 6 + f) * s_faceWidth + i];

        private static bool SlotWants(CasterSlot s, int i)
        {
            if (!CasterCullOn) return true;
            if (!VisRow(LocalView0 + s.LightIndex, i)) return false;
            return s.Face < 0 || FaceVis(s.LightIndex, s.Face, i);
        }

        // Called from Draw after the cascades: brings every slot to this frame's lights and lists; returns the caster draws recorded in all slots.
        private static int RecordLocal(int nb)
        {
            bool steady = LocalSteadyOn;
            if (!LocalOn)
            {
                // 1.2.213 IndirectLocalSteady: a light that left EBM's list for a moment (LightLod's shadow toggle, the frustum edge) keeps its
                // buffers for LocalGraceFrames. They draw nothing meanwhile (EbmClear zeroed views 7..10 and no light was culled into them).
                if (steady && CascadesOn && s_slotAttached > 0 && s_localOffFrames < LocalGraceFrames) { s_localOffFrames++; s_localGraceHolds++; return 0; }
                DetachLocal(); s_localOffFrames = 0; return 0;
            }
            s_localOffFrames = 0;
            bool faces = FacesOn;
            if (faces != s_lastFaces) { s_lastFaces = faces; DetachLocal(); }
            // this frame's wanted slots: (light, face) in order
            int want = 0;
            for (int k = 0; k < s_localCount; k++)
            {
                var l = s_local[k];
                bool perFace = faces && l.type == LightType.Point;
                for (int f = perFace ? 0 : -1; f < (perFace ? 6 : 0); f++)
                {
                    var pass = f < 0 ? PassFor(l) : FacePass[f];
                    var s = s_slots[want] ?? (s_slots[want] = new CasterSlot { Cb = new CommandBuffer { name = "EBM indirect casters, local slot " + want } });
                    if (s.Light != l || s.Pass != pass)
                    {
                        if (s.Light != null) s.Light.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, s.Cb);
                        s.Cb.Clear(); s.RecCount = 0; System.Array.Clear(s.Rec, 0, s.Rec.Length);
                        s.Light = l; s.Pass = pass;
                        l.AddCommandBuffer(LightEvent.BeforeShadowMapPass, s.Cb, pass);
                        s.RecCount = -1;   // record below
                    }
                    if (s.LightIndex != k) s.RecCount = -1;   // 1.2.213: its draws read light k's args rows; a new index = record again
                    s.LightIndex = k; s.Face = f; s.Parked = 0;
                    want++;
                    if (want == MaxSlots) break;
                }
            }
            // slots past this frame's list: 1.2.213 IndirectLocalSteady parks them for LocalGraceFrames (a light count that blinks 2 -> 1 -> 2
            // keeps the second light's buffers); they are not recorded or counted meanwhile and draw nothing (no light culled into their rows)
            int attached = want;
            for (int j = want; j < s_slotAttached; j++)
            {
                var s = s_slots[j];
                if (s == null) continue;
                if (steady && s.Light != null && s.Parked < LocalGraceFrames) { s.Parked++; attached = j + 1; continue; }
                DetachSlot(s);
            }
            s_slotCount = want;
            s_slotAttached = attached;
            bool force = s_localDirty || s_orderCount != nb;
            s_localDirty = false;
            int draws = 0, records = 0;
            for (int j = 0; j < s_slotCount; j++)
            {
                var s = s_slots[j];
                if (s.Rec.Length < nb) { System.Array.Resize(ref s.Rec, Mathf.NextPowerOfTwo(nb)); s.RecCount = -1; }
                bool redo = force || s.RecCount < 0 || s.Bias != faces;
                if (!redo && s_localMoved.Count > 0 && SlotHoldsMoved(s)) { redo = true; s_localMovedRedos++; }   // 1.2.213: one of ITS batches moved
                if (!redo)
                {
                    int stale = 0;
                    for (int i = 0; i < nb && !redo; i++)
                    {
                        bool w = CastsShadows(s_batches[i]) && SlotWants(s, i);
                        if (w && !s.Rec[i]) redo = true; else if (!w && s.Rec[i]) stale++;
                    }
                    if (!redo && stale > 8 && stale * 4 > s.RecCount) redo = true;
                }
                if (redo) { RecordSlot(s, nb, faces); records++; }
                draws += s.RecCount;
            }
            s_localMoved.Clear();   // 1.2.213: every current slot has seen this frame's moves
            s_localRecords += records;
            s_lastLocalDraws = draws;
            if (records > 0) CensusSlots(nb);   // [seasons] 11-09 E9 census (Debug, at a re-record only)
            if (records > 0 && EasyBakeLog.DebugOn()) EasyBakeLog.Debug($"[Indirect] local shadows: {s_localCount} light(s) ({LightNames()}), {s_slotCount} buffer(s), {records} re-recorded, {draws} caster draw(s)");
            return draws;
        }

        private static void RecordSlot(CasterSlot s, int nb, bool bias)
        {
            var cb = s.Cb;
            cb.Clear();
            System.Array.Clear(s.Rec, 0, s.Rec.Length);
            s.RecCount = 0; s.Bias = bias;
            int k = s.LightIndex, view = LocalView0 + k;
            // Unity sets the casters' depth bias only inside its own shadow job, after this buffer runs; ours needs it set here or it self-shadows
            if (bias) cb.SetGlobalDepthBias(1f, 1f);
            for (int j = 0; j < nb; j++)
            {
                int i = s_order[j];
                var b = s_batches[i];
                if (!CastsShadows(b) || !SlotWants(s, i)) continue;
                if (!s_localCasterInfo.TryGetValue(b, out var props))
                {
                    props = new MaterialPropertyBlock[MaxLocal];
                    for (int q = 0; q < MaxLocal; q++) { props[q] = new MaterialPropertyBlock(); Bind(props[q], (LocalView0 + q) * s_visibleStride + b.VisibleOffset); }
                    s_localCasterInfo[b] = props;   // once per batch
                }
                else if (b.LocalBound != s_bindEpoch) for (int q = 0; q < MaxLocal; q++) Bind(props[q], (LocalView0 + q) * s_visibleStride + b.VisibleOffset);
                b.LocalBound = s_bindEpoch;
                cb.DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, b.Material, b.CasterPass, s_argsBuf, (view * s_batchCap + i) * ArgWords * sizeof(uint), props[k]);
                s.Rec[i] = true; s.RecCount++;
            }
            if (bias) cb.SetGlobalDepthBias(0f, 0f);
        }

        private static ShadowMapPass PassFor(Light l) => l != null && l.type == LightType.Spot ? ShadowMapPass.Spotlight : ShadowMapPass.Pointlight;

        private static void DetachSlot(CasterSlot s)
        {
            if (s == null) return;
            if (s.Light != null && s.Cb != null) s.Light.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, s.Cb);   // a destroyed light took its buffers with it
            s.Light = null; s.RecCount = -1;
            s.Cb?.Clear();
        }

        private static void DetachLocal()
        {
            int n = Mathf.Max(s_slotCount, s_slotAttached);   // 1.2.213: parked slots too
            for (int j = 0; j < n; j++) DetachSlot(s_slots[j]);
            s_slotCount = 0; s_slotAttached = 0;
            s_localMoved.Clear();   // every slot records from scratch next time
            s_localDirty = true;
        }

        // ReleaseAll: the buffers go with the layout; per-batch blocks are rebuilt on the next record
        private static void ReleaseLocal()
        {
            DetachLocal();
            s_localCasterInfo.Clear();
            s_localCount = 0;
        }
        private static string LightNames()   // Debug only, at a record
        {
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < s_localCount; k++) { if (k > 0) sb.Append(", "); sb.Append(s_local[k].type == LightType.Spot ? "spot " : "point ").Append(s_local[k].name); }
            return sb.ToString();
        }

        private static void NoteLocalState(string why)
        {
            if (why == s_localState) return;
            s_localState = why;
            EasyBakeLog.Info("[Indirect] local light shadows " + why);
        }
    }
}
