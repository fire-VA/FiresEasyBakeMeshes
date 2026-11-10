using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // EBM 1.2.203 ([visual] 11-08, Fire: "make our mod get us 100+"; [Optimize] IndirectRenderer, default off):
    // EBM's baked instance groups drawn GPU-driven. Every loaded zone's instances live in ONE instance buffer; one
    // compute pass per frame culls each instance for the camera (frustum + the prefab's own LOD window + an optional
    // screen-size floor) and for the sun's shadow volume (the camera's shadow slice extruded toward the light), and
    // one RenderMeshIndirect per batch (mesh + submesh + material + shadow mode + layer, across ALL zones) per view
    // draws what passed. At the 615 pose the old path made 1,389 calls a frame x every cascade, all at LOD0.
    // The cull method (instance-per-thread frustum/LOD/size tests writing indirect args) was learned from VPO
    // (ValheimPerformanceOptimizations by ontrigger, MIT, read for reference only). This code is our own.
    // Groups that cannot go this way (a material with no procedural variant, snow/wet ranges active, roof hiding for a
    // camera) keep EBM's RenderMeshInstanced path; the [Indirect] line says how many and why.
    internal static partial class EbmIndirect
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GpuInstance { public Vector4 O0, O1, O2, W0, W1, W2; }   // object-to-world + world-to-object rows 0..2

        [StructLayout(LayoutKind.Sequential)]
        private struct GpuBatch
        {
            public Vector3 Center; public uint VisibleOffset;
            public Vector3 Extents; public uint Flags;          // bit 0: casts shadows
            public Vector4 Lod;                                   // xyz LOD reference (mesh local), w LOD size (0 = no LOD)
            public float StartHeight, EndHeight; public uint Capacity, Pad;
        }

        private const int ArgWords = 5;      // GraphicsBuffer.IndirectDrawIndexedArgs
        private const int Views = 15;         // 1.2.209 [wishbone]: + 7..10 the point / spot lights' casters (EbmIndirectLocalShadows); G4: + 11..14 pre-culled other cameras (EbmIndirectOnePass)
        // was 7:          // 0 camera, 1 sun shadow volume, 2..5 the sun's cascades (1.2.208), 6 the other camera rendering now (1.2.209 EbmIndirectViews)
        private const int CellViews = 2;      // the CPU cell cull judges the camera + the sun volume
        private const int Group = 64;

        internal sealed class Batch
        {
            public int Index;
            public Mesh Mesh; public int SubMesh; public Material Material;
            public int VertCount = -1;   // 1.2.234: the mesh vertex count the args rows were written for (the drift check)
            public float MaxScale = 1f;   // 1.2.233 ([generator]): the largest instance scale ever added (never shrinks), for the caster size skip
            public ShadowCastingMode Shadows; public bool ReceiveShadows; public int Layer; public uint RenderingLayerMask;
            public Bounds LocalBounds; public Vector4 Lod; public float StartHeight, EndHeight;
            public bool LodPerInstance;   // rank 6 ([wishbone]): made with [Optimize] IndirectLodPerInstance: no LOD window in the key, each instance carries its own (packed in _EbmInstLod.w)
            public int Count, Capacity, VisibleOffset; public long Tris;
            public Bounds World; public bool WorldSet;
            public MaterialPropertyBlock CameraProps, ShadowProps, OtherProps;   // 1.2.209: OtherProps = view 6 (other cameras)
            public MaterialPropertyBlock[] SlotProps;                            // G4: views 11..14 (EbmIndirectOnePass), made on first use
            public int CasterPass = -2;   // 1.2.210: the material's ShadowCaster pass, looked up once (-2 = not yet, -1 = none)
            public int CasterBound = -1, LocalBound = -1;
            public RenderParams CachedRp; public Camera CachedRpCamera; public bool CachedRpOk;   // 1.2.241 IndirectDrawParamsCache
            public bool MeshOk, MatOk;   // 1.2.213 IndirectCachedAlive: Mesh / Material still exist, asked once a frame (EbmIndirectCpuTrim.RefreshAlive)
            public bool PlaceQueued, Placed;   // 1.2.210 IndirectStableLayout: waiting for its own range / has one   // 1.2.210: the bind epoch its cascade / light caster blocks were bound in
        }

        // 1.2.210 ([generator]'s spike read): one lane per (LOD part) or (part, level): its batch, the SOURCE matrix of each instance and
        // the slot it fills. Sync matches a group's new matrices against the old ones, so a piece joining or leaving touches only that
        // piece's slots (no whole-group Drop + re-add: no m.inverse, cell churn or relayout for the rest).
        private sealed class Lane { public Batch Batch; public readonly List<Matrix4x4> Mats = new List<Matrix4x4>(); public readonly List<int> Slots = new List<int>(); public Vector4 Lod; public float Start = float.MaxValue, End; }   // rank 6: the lane's own LOD (a batch can now hold several)

        private sealed class GroupSlots
        {
            public int Version = -1; public int Seen;
            public int LkVersion = int.MinValue, LkGen, LkExt, LkSlots; public bool LkActive;   // 1.2.223: the looks state last written for this group
            public readonly List<int> Slots = new List<int>(); public readonly List<int> SlotInstance = new List<int>();   // 1.2.208: the group instance each slot draws (looks)
            public readonly List<Lane> Lanes = new List<Lane>();   // 1.2.210
            public GroupStage Stage;   // [Optimize] IndirectStagedSync: a first registration being prepared (EbmIndirectStagedSync.cs)
        }

        private static readonly Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int, uint, float, float), Batch> s_byKey = new Dictionary<(Mesh, int, Material, ShadowCastingMode, bool, int, uint, float, float), Batch>();   // 1.2.210 (study): + receiveShadows + renderingLayerMask, which the draw takes from the batch   // 1.2.206: + the LOD window (coverage renderers share meshes across LOD setups)
        private static readonly List<Batch> s_batches = new List<Batch>();
        internal static void ForEachBatch(Action<Batch> visit) { for (int i = 0; i < s_batches.Count; i++) visit(s_batches[i]); }   // [seasons] 11-09 E39 census (read only)
        private static readonly Dictionary<ZoneInstanceGroup, GroupSlots> s_groups = new Dictionary<ZoneInstanceGroup, GroupSlots>();
        private static readonly List<ZoneInstanceGroup> s_gone = new List<ZoneInstanceGroup>();

        private static GpuInstance[] s_inst = new GpuInstance[0];
        private static uint[] s_instBatch = new uint[0];
        private static Vector3[] s_instPos = new Vector3[0];
        private static Vector4[] s_instLod = new Vector4[0];   // 1.2.206: per instance: LOD reference point (world) + LOD world size (0 = no LOD)
        private static GraphicsBuffer s_instLodBuf;
        private static readonly FreeSlots s_free = new FreeSlots();   // 1.2.213: lowest free slot first (a min-heap), so the top empties and s_high can shrink
        private static int s_high, s_live, s_dirtyMin = int.MaxValue, s_dirtyMax = -1;
        private static bool s_layoutDirty = true, s_bindingsDirty = true;
        internal static int Epoch;   // 1.2.206: bumped by every release, so an outside handle from before it never removes a reused slot

        private static GraphicsBuffer s_instBuf, s_instBatchBuf, s_batchBuf, s_visibleBuf, s_argsBuf;
        // 1.2.225 ([visual], the stretch: RenderDoc frame4915 shows the camera-read _EbmVisible buffer UAV-written by the shadow-pass cull MID-FRAME
        // (EID 27573, between camera draws 572..50331); on real D3D11 that UAV bind drops the buffer's SRV binding behind Unity's state cache, so later
        // draws read a NULL _EbmVisible = instance 0 for every vertex. RenderDoc re-validates bindings, so it never showed there). The shadow-pass
        // cull now writes s_cascScratch (never bound as an SRV) and copies it into s_cascVisible, which only the cascade caster draws read.
        internal static GraphicsBuffer s_cascScratch, s_cascVisible, s_otherScratch, s_otherVisible;   // + view 6 (other cameras, Camera.onPreCull = mid-frame) the same way
        private static GpuBatch[] s_gpuBatches = new GpuBatch[0];
        private static uint[] s_args = new uint[0];
        private static int s_visibleStride;

        private static readonly Plane[] s_cam = new Plane[6];
        private static readonly Vector4[] s_camV = new Vector4[6], s_shadowV = new Vector4[6];
        private static readonly Vector3[] s_corners = new Vector3[8];
        private static int s_frame;
        // [fgn]'s CPU batch-cell cull (IndirectCells): a batch with nothing in a view's cells is not submitted for that view at all (main thread only)
        private static readonly IndirectCells s_cells = new IndirectCells();
        private static readonly Plane[] s_cellPlanes = new Plane[6 * CellViews];
        private static bool[] s_vis = new bool[0];
        private static int s_visWidth;

        // ── state, for the [Indirect] line and the hand list ──
        private static string s_off = "not started";
        internal static int TakenGroups, RefusedGroups, LastBatches, LastCalls;
        private static int s_lodPartGroups;   // groups registered through per-renderer LOD parts (Debug)
        internal static long LastInstances;
        private static readonly Dictionary<string, int> s_refusedWhy = new Dictionary<string, int>();

        internal static bool Active { get; private set; }
        private static string s_faulted;
        private static bool? s_override;   // 1.2.205 ebm_indirect on|off: this session only, the cfg is never written

        internal static void SetSessionOverride(bool on)
        {
            s_override = on;
            EasyBakeLog.Info($"[Indirect] toggled {(on ? "ON" : "OFF")} by command (ebm_indirect, this session only; the cfg still says {(FiresEasyBakeMeshesPlugin.IndirectRenderer != null && FiresEasyBakeMeshesPlugin.IndirectRenderer.Value ? "on" : "off")})");
        }
        internal static event Action ActiveChanged;

        // Called once a frame from ZoneTracker.DrawInstances before the groups are walked. False = draw as before.
        internal static bool BeginFrame()
        {
            bool want = s_override ?? (FiresEasyBakeMeshesPlugin.IndirectRenderer != null && FiresEasyBakeMeshesPlugin.IndirectRenderer.Value);
            // a throw latches the path off until the key goes off and on again ([generator]: never a broken frame twice)
            if (!want) s_faulted = null;
            if (want && s_faulted != null) { SetActive(false, s_faulted); return false; }
            if (want && !EbmIndirectAssets.Ready(out string why)) { SetActive(false, why); return false; }
            if (want && !SystemInfo.supportsComputeShaders) { SetActive(false, "this GPU has no compute shaders"); return false; }
            if (want && s_override != true && IndirectCrashGuard.FallBackThisSession(out string crashWhy)) { SetActive(false, crashWhy); return false; }   // 1.2.256 [Optimize] IndirectCrashFallback
            SetActive(want, want ? null : "[Optimize] IndirectRenderer is off");
            if (!Active) return false;
            IndirectCrashGuard.Frame();
            HookViews();   // 1.2.209
            s_frame++;
            s_aliveValid = false;   // 1.2.213: last frame's alive flags are not trusted until EndFrame refreshes them
            TakenGroups = RefusedGroups = 0;
            return true;
        }

        private static void SetActive(bool on, string why)
        {
            if (Active == on && (on || s_off == why)) return;
            bool changed = Active != on;
            Active = on;
            s_off = why;
            if (!on && changed) { try { ActiveChanged?.Invoke(); } catch (Exception e) { EasyBakeLog.Warn("[Indirect] an ActiveChanged listener threw: " + e.Message); } }   // listeners release first
            if (!on) ReleaseAll();
            IndirectStats.NoteState(on ? "on" : "off: " + why);
            EasyBakeLog.Info(on ? "[Indirect] on: baked groups are drawn GPU-driven (one draw per batch per view, per-instance cull + LOD)"
                                : $"[Indirect] off: {why}; baked groups draw through EBM's instanced path");
            if (changed && on) { try { ActiveChanged?.Invoke(); } catch (Exception e) { EasyBakeLog.Warn("[Indirect] an ActiveChanged listener threw: " + e.Message); } }
        }

        // The group draws through the indirect path this frame (true), or the caller draws it as before (false).
        internal static bool Take(ZoneInstanceGroup group)
        {
            if (!s_groups.TryGetValue(group, out var reg))
            {
                // a refusal is judged once per group version, never per frame (its reason string is built once)
                if (s_refused.TryGetValue(group, out var refusedAt) && refusedAt.version == group.Version) { s_refused[group] = (refusedAt.version, s_frame); RefusedGroups++; return false; }
                string why = Refusal(group);
                if (why != null) { s_refused[group] = (group.Version, s_frame); RefusedGroups++; Count(why); return false; }
                s_refused.Remove(group);
                reg = new GroupSlots();
                s_groups[group] = reg;
            }
            else if (group.IndirectNeedsLegacy(out string now))   // snow / wet ranges or roof hiding came on: hand back
            {
                Drop(group, reg); s_groups.Remove(group); IndirectLooks.Forget(group); RefusedGroups++; Count(now); return false;
            }
            reg.Seen = s_frame;
            if ((reg.Stage != null || (reg.Version == -1 && StagedSyncOn)) && StageTake(group, reg)) return false;   // [Optimize] IndirectStagedSync: drawn instanced until its one-frame flip
            // 1.2.223 [Optimize] IndirectLooksChangeDriven: a group whose looks inputs did not move skips the prep and the write outright
            if (LooksChangeDrivenOn)
            {
                // 1.2.227 (lead: "0 skips"): the FIRST check that fails is counted, so the line names what re-dirties every group
                int why = reg.Version != group.Version ? 0 : reg.LkVersion != group.Version ? 1 : reg.LkGen != group.SnowGeneration ? 2 : reg.LkExt != group.ExtGen ? 3
                        : reg.LkActive != group.LooksActive ? 4 : reg.LkSlots != reg.Slots.Count ? 5 : group.LooksSettledWhy();
                if (why < 0) { s_looksSkipped++; TakenGroups++; s_takeSettled = true; return true; }
                s_looksWhy[why]++;
            }
            long sp0 = InstancedDraw.SubT();
            group.PrepareLooksForIndirect();
            InstancedDraw.SubAcc(InstancedDraw.SubLooksPrep, sp0);   // A2 ([wishbone]): the snow / wet re-split Draw would have run (may bump Version)
            if (reg.Version != group.Version) { long tS = PhT(); SyncTimed(group, reg); PhAcc(PhSync, tS); }
            long sw0 = InstancedDraw.SubT();
            IndirectLooks.WriteGroup(group, reg.Slots, reg.SlotInstance);
            InstancedDraw.SubAcc(InstancedDraw.SubLooksWrite, sw0);   // A2: a no-op unless version / snow generation / look state moved
            reg.LkVersion = group.Version; reg.LkGen = group.SnowGeneration; reg.LkExt = group.ExtGen; reg.LkActive = group.LooksActive; reg.LkSlots = reg.Slots.Count; EnqueueWork(group);
            TakenGroups++;
            return true;
        }

        private static string Refusal(ZoneInstanceGroup group)
        {
            if (group.IndirectNeedsLegacy(out string why)) return why;
            var lodParts = group.Definition?.LodParts;   // 1.2.208: the per-renderer LOD parts are what the indirect path draws when present
            if (lodParts != null)
            {
                foreach (var lp in lodParts)
                    if (lp.RenderParams.material == null || EbmIndirectAssets.VariantFor(lp.RenderParams.material) == null) return "no procedural variant for shader '" + (lp.RenderParams.material != null && lp.RenderParams.material.shader != null ? lp.RenderParams.material.shader.name : "?") + "'";
                return null;
            }
            var parts = group.Definition?.Parts;
            if (parts == null || parts.Length == 0) return "no parts";
            foreach (var part in parts)
            {
                var mat = part.RenderParams.material;
                if (mat == null || part.NearMesh == null) return "a part has no mesh or material";
                if (EbmIndirectAssets.VariantFor(mat) == null) return "no procedural variant for shader '" + (mat.shader != null ? mat.shader.name : "?") + "'";
            }
            return null;
        }

        private static readonly Dictionary<ZoneInstanceGroup, (int version, int seen)> s_refused = new Dictionary<ZoneInstanceGroup, (int, int)>();

        private static void Count(string why) { s_refusedWhy.TryGetValue(why, out int n); s_refusedWhy[why] = n + 1; }

        // Re-registers a group's instances after it changed (a piece joined or left). Instances never move otherwise.
        private static void Sync(ZoneInstanceGroup group, GroupSlots reg)
        {
            reg.Version = group.Version;
            s_addContext = "baked group of " + group.Matrices.Count + " at " + (group.Matrices.Count > 0 ? ((Vector3)group.Matrices[0].GetColumn(3)).ToString("F0") : "?");   // 1.2.235 BadInstance names it (the mesh name is on the same line)
            reg.Slots.Clear(); reg.SlotInstance.Clear();
            if (SyncCleanupOn) PresizeSlotLists(group, reg);   // [Optimize] IndirectSyncCleanup
            int lane = 0;
            var lodParts = group.Definition.LodParts;
            if (lodParts != null)
            {
                // 1.2.208: one batch per (renderer, submesh, LOD window); the instance's LOD reference point is the LODGroup's, through its matrix
                var ms = group.Matrices;
                foreach (var lp in lodParts)
                {
                    var rp = lp.RenderParams;
                    var batch = BatchOf(lp.Mesh, lp.SubMesh, EbmIndirectAssets.VariantFor(rp.material), rp.shadowCastingMode, rp.receiveShadows, rp.layer, rp.renderingLayerMask, lp.Size > 0f ? lp.Start : float.MaxValue, lp.Size > 0f ? lp.End : 0f);
                    SyncLane(reg, lane++, batch, ms, lp, Vector4.zero, lp.Size > 0f ? lp.Start : float.MaxValue, lp.Size > 0f ? lp.End : 0f);
                }
                s_lodPartGroups++;
            }
            else
            {
                var parts = group.Definition.Parts;
                for (int p = 0; p < parts.Length; p++)
                {
                    var part = parts[p];
                    var matrices = group.MatricesForPart(p);
                    var lods = part.LodMeshes;
                    int levels = lods != null && lods.Length > 0 && part.LodHeights != null && part.LodSize > 0f ? lods.Length : 1;
                    for (int level = 0; level < levels; level++)
                    {
                        var mesh = levels == 1 ? part.NearMesh : lods[level];
                        if (mesh == null) continue;
                        var bf = BatchFor(part, mesh, level, levels, out var bLod, out float bStart, out float bEnd);
                        SyncLane(reg, lane++, bf, matrices, null, bLod, bStart, bEnd);
                    }
                }
            }
            // lanes the definition no longer has (a mesh went null): their slots go
            for (int l = lane; l < reg.Lanes.Count; l++) foreach (int slot in reg.Lanes[l].Slots) RemoveInstance(slot);
            if (reg.Lanes.Count > lane) reg.Lanes.RemoveRange(lane, reg.Lanes.Count - lane);
            if (group.DropNote != null)   // 1.2.238
            {
                EasyBakeLog.Debug($"[Bake] after the drop ({group.DropNote}): group has {group.Matrices.Count} instance(s); {reg.Lanes.Count} lane(s), slots {string.Join("/", reg.Lanes.Select(x => x.Slots.Count.ToString()).ToArray())}, batches {string.Join(", ", reg.Lanes.Select(x => (x.Batch.Mesh != null ? x.Batch.Mesh.name : "?") + " x" + x.Batch.Count).ToArray())}");
                group.DropNote = null;
            }
        }

        // scratch for SyncLane: old instance index by matrix (chained through s_syncNext for duplicates); main thread only, no per-call alloc
        private static readonly Dictionary<Matrix4x4, int> s_syncHead = new Dictionary<Matrix4x4, int>(256);
        private static readonly List<int> s_syncNext = new List<int>(256);

        // 1.2.210: brings one lane to the group's current matrices. An instance whose source matrix is unchanged keeps its slot (and so
        // its GPU data); only new matrices are added and only vanished ones removed. Slots keep the instance order of `src` (looks need it).
        private static void SyncLane(GroupSlots reg, int lane, Batch batch, List<Matrix4x4> src, LodPart lp, Vector4 bakedLod, float lodStart, float lodEnd)
        {
            if (lane == reg.Lanes.Count) reg.Lanes.Add(new Lane { Batch = batch });
            var ln = reg.Lanes[lane];
            ln.Lod = bakedLod; ln.Start = lodStart; ln.End = lodEnd;   // rank 6
            if (ln.Batch != batch)   // the lane's batch changed (a material swap): all its slots move
            {
                foreach (int slot in ln.Slots) RemoveInstance(slot);
                ln.Slots.Clear(); ln.Mats.Clear(); ln.Batch = batch;
            }
            if (SyncCleanupOn) { SyncLaneClean(reg, ln, batch, src, lp, bakedLod, lodStart, lodEnd); return; }   // [Optimize] IndirectSyncCleanup (EbmIndirectSyncCleanup.cs)
            s_syncHead.Clear(); s_syncNext.Clear();
            for (int j = 0; j < ln.Mats.Count; j++)
            {
                s_syncNext.Add(s_syncHead.TryGetValue(ln.Mats[j], out int h) ? h : -1);
                s_syncHead[ln.Mats[j]] = j;
            }
            var oldSlots = s_syncOldSlots; oldSlots.Clear(); oldSlots.AddRange(ln.Slots);
            ln.Mats.Clear(); ln.Slots.Clear();
            for (int i = 0; i < src.Count; i++)
            {
                var m = src[i];
                int slot;
                if (s_syncHead.TryGetValue(m, out int j))
                {
                    slot = oldSlots[j]; oldSlots[j] = -1;
                    int nx = s_syncNext[j];
                    if (nx < 0) s_syncHead.Remove(m); else s_syncHead[m] = nx;
                }
                else if (lp != null)
                {
                    Vector4 lw = Vector4.zero;
                    if (lp.Size > 0f) { var refW = m.MultiplyPoint3x4(lp.RefFirst); lw = new Vector4(refW.x, refW.y, refW.z, lp.Size); }
                    slot = AddInstance(batch, m * lp.FromFirst, lw, lodStart, lodEnd);
                }
                else slot = batch.LodPerInstance ? AddInstance(batch, m, LodWorld(bakedLod, m), lodStart, lodEnd) : AddInstance(batch, m, BakedLod(batch, m));
                ln.Mats.Add(m); ln.Slots.Add(slot);
                reg.Slots.Add(slot); reg.SlotInstance.Add(i);
            }
            foreach (int slot in oldSlots) if (slot >= 0) RemoveInstance(slot);   // matrices that left the group
        }

        private static readonly List<int> s_syncOldSlots = new List<int>(256);

        private static void Drop(ZoneInstanceGroup group, GroupSlots reg)
        {
            if (reg.Stage != null) CancelStage(reg);   // [Optimize] IndirectStagedSync: its staged slots go back
            foreach (var ln in reg.Lanes) foreach (int slot in ln.Slots) RemoveInstance(slot);
            reg.Lanes.Clear();
            reg.Slots.Clear();
            reg.SlotInstance.Clear();
            reg.Version = -1;
        }

        private static Batch BatchFor(InstancePart part, Mesh mesh, int level, int levels, out Vector4 lodOut, out float startOut, out float endOut)
        {
            var rp = part.RenderParams;
            var variant = EbmIndirectAssets.VariantFor(rp.material);
            // the prefab's own LOD window for this level, as vanilla's LODGroup picks it (InstancePart.LodFor); no LODGroup = always level 0
            Vector4 lod = Vector4.zero; float start = float.MaxValue, end = 0f;
            if (part.LodHeights != null && part.LodHeights.Length > 0 && part.LodSize > 0f)   // 1.2.208 ([generator] 2): a one-mesh chain still culls where vanilla does
            {
                var h = part.LodHeights;
                lod = new Vector4(part.NearMesh.bounds.center.x, part.NearMesh.bounds.center.y, part.NearMesh.bounds.center.z, part.LodSize);
                start = level == 0 ? float.MaxValue : h[level - 1];
                // past the last resolved mesh vanilla keeps culling at its last transition; a deeper level we could not resolve keeps this mesh
                end = level < levels - 1 ? h[level] : (h.Length > levels ? 0f : h[Mathf.Min(level, h.Length - 1)]);
            }
            var batch = BatchOf(mesh, part.SubMesh, variant, rp.shadowCastingMode, rp.receiveShadows, rp.layer, rp.renderingLayerMask, start, end);
            batch.Lod = lod;   // the LOD reference (mesh local) + size for baked instances; per-instance world values go to _EbmInstLod
            lodOut = lod; startOut = start; endOut = end;   // rank 6: the lane keeps its own (the batch may be shared)
            return batch;
        }

        // One batch per mesh + submesh + material variant + shadow mode + layer + LOD window, shared by baked groups and coverage renderers.
// 1.2.221 ([generator]): the pass lookup once per variant material, not per instance added
        private static readonly Dictionary<Material, bool> s_hasCaster = new Dictionary<Material, bool>();
        private static bool HasCasterPass(Material m) { if (!s_hasCaster.TryGetValue(m, out bool v)) { v = m.FindPass("ShadowCaster") >= 0; s_hasCaster[m] = v; } return v; }

                internal static Batch BatchOf(Mesh mesh, int subMesh, Material variant, ShadowCastingMode shadows, bool receive, int layer, uint renderingLayerMask, float start, float end)
        {
            // rank 6 ([wishbone], VPO study #10): with [Optimize] IndirectLodPerInstance the LOD window leaves the key (-1 / -1 in its place): one batch per
            // mesh + material whatever the LOD setup; each instance carries its own window in _EbmInstLod.w (two half distances). Off = as before.
            // 1.2.221 ([generator]): a variant with no ShadowCaster pass (EBM/RugIndirect: vanilla rugs cast none) is a non-caster batch, so
            // the GPU caster views, the ShadowsOnly draw and the shadow placeholder pick all skip it
            if (shadows != ShadowCastingMode.Off && variant != null && !HasCasterPass(variant)) shadows = ShadowCastingMode.Off;
            bool perInst = LodPerInstanceOn;
            var key = (mesh, subMesh, variant, shadows, receive, layer, renderingLayerMask, perInst ? -1f : start, perInst ? -1f : end);
            if (s_byKey.TryGetValue(key, out var batch)) return batch;
            batch = new Batch
            {
                Index = s_batches.Count, Mesh = mesh, SubMesh = subMesh, Material = variant,
                Shadows = shadows, ReceiveShadows = receive, Layer = layer, RenderingLayerMask = renderingLayerMask,
                LocalBounds = mesh.GetSubMesh(Mathf.Clamp(subMesh, 0, mesh.subMeshCount - 1)).bounds,
                CameraProps = new MaterialPropertyBlock(), ShadowProps = new MaterialPropertyBlock(),
                StartHeight = perInst ? float.MaxValue : start, EndHeight = perInst ? 0f : end, LodPerInstance = perInst,
            };
            if (batch.LocalBounds.size == Vector3.zero) batch.LocalBounds = mesh.bounds;
            s_byKey[key] = batch;
            s_batches.Add(batch);
            global::FiresEasyBakeMeshes.EasyBake.EbmShaderWarm.NoteBatch(variant);   // [seasons] 11-09 E30: first-use census
            MeshFullQueue(mesh);   // 1.2.238: the baseline, taken while the batch is new
            RequestPlace(batch);   // 1.2.210: a free args row + its own visible range, no full relayout (IndirectStableLayout)
            return batch;
        }

        // a baked instance's LOD in world terms: its reference point through its matrix, the prefab-scale size as legacy LodFor uses it
        private static Vector4 BakedLod(Batch b, Matrix4x4 m)
        {
            if (b.Lod.w <= 0f) return Vector4.zero;
            var p = m.MultiplyPoint3x4(new Vector3(b.Lod.x, b.Lod.y, b.Lod.z));
            return new Vector4(p.x, p.y, p.z, b.Lod.w);
        }

        internal static bool LodPerInstanceOn => FiresEasyBakeMeshesPlugin.IndirectLodPerInstance != null && FiresEasyBakeMeshesPlugin.IndirectLodPerInstance.Value;

        // rank 6: a baked instance's LOD reference in world terms from the lane's own (mesh-local reference + size)
        private static Vector4 LodWorld(Vector4 lod, Matrix4x4 m)
        {
            if (lod.w <= 0f) return Vector4.zero;
            var p = m.MultiplyPoint3x4(new Vector3(lod.x, lod.y, lod.z));
            return new Vector4(p.x, p.y, p.z, lod.w);
        }

        // rank 6: the instance's window as camera distances in units of lodBias / (2 tan(fov/2)) (the GPU and the cells multiply by it):
        // drawn while start < d <= end. start = size / startHeight (0 for LOD0), end = size / endHeight (65504 = never culled). Packed as two halves.
        private static void LodDistances(Vector4 lodWorld, float start, float end, out float startDist, out float endDist)
        {
            startDist = 0f; endDist = 0f;
            if (lodWorld.w <= 0f) return;
            startDist = start > 0f && start < float.MaxValue ? lodWorld.w / start : 0f;
            endDist = end > 0f ? Mathf.Max(lodWorld.w / end, 1e-3f) : 65504f;
        }

        private static Vector4 PackLod(Vector4 lodWorld, float startDist, float endDist)
        {
            if (lodWorld.w <= 0f) return Vector4.zero;   // no LOD: w 0
            int bits = Mathf.FloatToHalf(Mathf.Min(startDist, 65504f)) | (Mathf.FloatToHalf(Mathf.Min(endDist, 65504f)) << 16);
            return new Vector4(lodWorld.x, lodWorld.y, lodWorld.z, new IntFloat { I = bits }.F);   // the bits as a float, no allocation (no Int32BitsToSingle on this framework)
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct IntFloat { [System.Runtime.InteropServices.FieldOffset(0)] public int I; [System.Runtime.InteropServices.FieldOffset(0)] public float F; }

        // [seasons] 11-09 E28 census: indirect adds / removes made since the last Upload (not on the GPU yet). Main thread.
        internal static int AddsWaiting, RemovesWaiting;

        internal static int AddInstance(Batch batch, Matrix4x4 m, Vector4 lodWorld) => AddInstance(batch, m, lodWorld, batch.StartHeight, batch.EndHeight);

        internal static int AddInstance(Batch batch, Matrix4x4 m, Vector4 lodWorld, float lodStart, float lodEnd)
        {
            long stT = StBegin();   // EbmIndirectSyncTimer: one add in 16 inside a Sync, Debug only
            AddsWaiting++;   // [seasons] 11-09 E28 census (cleared at Upload)
            int slot = AllocSlot();   // 1.2.213 lowest free slot / s_high + growth, unchanged; [Optimize] IndirectStagedSync: never a staged slot (EbmIndirectStagedSync.cs)
            StMark(0, ref stT);
            bool badInstance = BadInstance(batch, m);
            StMark(1, ref stT);
            if (badInstance)   // 1.2.238 ([generator]): a slot the group can own, drawn as nothing; no inverse, no bounds, no cell
            {
                s_inst[slot] = default; s_instBatch[slot] = (uint)(batch.Index + 1); s_instPos[slot] = Vector3.zero; s_instLod[slot] = Vector4.zero;
                s_live++; s_addsWindow++; batch.Count++;
                if (batch.Count > batch.Capacity) RequestPlace(batch);
                if (batch.Count == 1) NoteFirstInstance();
                MarkDirty(slot);
                return slot;
            }
            var inv = m.inverse; NoteMatrix(m, inv);   // 1.2.232
            NoteScale(batch, m);   // 1.2.233
            StMark(2, ref stT);
            s_inst[slot] = new GpuInstance { O0 = m.GetRow(0), O1 = m.GetRow(1), O2 = m.GetRow(2), W0 = inv.GetRow(0), W1 = inv.GetRow(1), W2 = inv.GetRow(2) };
            s_instBatch[slot] = (uint)(batch.Index + 1);
            s_instPos[slot] = m.GetColumn(3);
            float cellStart, cellEnd;
            if (batch.LodPerInstance)
            {
                LodDistances(lodWorld, lodStart, lodEnd, out cellStart, out float ed);
                s_instLod[slot] = PackLod(lodWorld, cellStart, ed);
                cellEnd = lodWorld.w > 0f && ed < 65504f ? ed : 0f;   // the cells' 0 = never culled by distance
            }
            else { s_instLod[slot] = lodWorld; cellEnd = batch.EndHeight > 0f && lodWorld.w > 0f ? lodWorld.w / batch.EndHeight : 0f; cellStart = LodStartDistance(batch, lodWorld); }
            s_live++; s_addsWindow++;
            batch.Count++;
            if (batch.Count > batch.Capacity) RequestPlace(batch);   // 1.2.210: only this batch moves (IndirectStableLayout)
            // 1.2.209 ([generator]'s review): the light / cascade command buffers record only batches with instances; a batch going 0 -> 1
            // (a zone loads, a piece is built) must get its caster draws, or those pieces cast no sun-cascade or torch shadow until a relayout
            if (batch.Count == 1) NoteFirstInstance();   // 1.2.213 IndirectLocalSteady: the lights' slots catch the gain themselves
            StMark(3, ref stT);
            var wb = TransformBounds(batch.LocalBounds, m);
            if (!batch.WorldSet) { batch.World = wb; batch.WorldSet = true; } else batch.World.Encapsulate(wb);
            StMark(4, ref stT);
            s_cells.Add(batch.Index, s_instPos[slot], wb.min, wb.max, cellEnd, cellStart);
            StMark(5, ref stT);
            MarkDirty(slot);
            return slot;
        }

        internal static void RemoveInstance(int slot)
        {
            if (slot < 0 || slot >= s_high) return;   // a handle from before a release ([generator] 8)
            uint b = s_instBatch[slot];
            if (b == 0 || b > s_batches.Count) return;
            s_batches[(int)b - 1].Count--;
            if (s_inst[slot].O0 != Vector4.zero || s_inst[slot].O1 != Vector4.zero || s_inst[slot].O2 != Vector4.zero)   // 1.2.239 ([generator]): a broken instance has no cell
                s_cells.Remove((int)b - 1, s_instPos[slot]);   // baked instances never move, so the filed position is exact
            s_instBatch[slot] = 0;   // a free slot matches no batch in the cull
            s_live--; s_removesWindow++; RemovesWaiting++;   // [seasons] 11-09 E28 census (cleared at Upload)
            s_free.Push(slot);
            // 1.2.213 IndirectCompactSlots: the top slots that are free come off s_high, so every cull dispatch covers fewer threads
            if (CompactOn && slot == s_high - 1) while (s_high > 0 && s_instBatch[s_high - 1] == 0 && !IsStaged(s_high - 1)) s_high--;   // a staged slot is held (IndirectStagedSync)
            IndirectLooks.Clear(slot);   // A2
            MarkDirty(slot);
        }

        // where the instance's last LOD window ends, in IndirectCells' units (x lodScale = metres); 0 = never culled by distance.
        // The same numbers the GPU test uses: rel = size x scale x lodScale / d >= EndHeight  <=>  d <= (size x scale / EndHeight) x lodScale.
        // 1.2.210 (study #11): where the instance's LOD window STARTS (nearer than this the GPU draws another level), same units; 0 = from the camera
        private static float LodStartDistance(Batch b, Vector4 lodWorld) => b.StartHeight > 0f && b.StartHeight < float.MaxValue && lodWorld.w > 0f ? lodWorld.w / b.StartHeight : 0f;

        private static float LodEndDistance(Batch b, Matrix4x4 m)
        {
            if (b.Lod.w <= 0f || b.EndHeight <= 0f) return 0f;
            return b.Lod.w / b.EndHeight;   // same as the GPU test: LodSize is the world size at prefab scale ([generator] 2)
        }

        // coverage: a renderer moved (handed back, then re-added is the usual path; this is for an explicit SetMatrix)
        internal static void MoveInstance(int slot, Matrix4x4 m)
        {
            if (slot < 0 || slot >= s_high) return;
            uint b = s_instBatch[slot];
            if (b == 0 || b > s_batches.Count) return;
            var batch = s_batches[(int)b - 1];
            if (BadInstance(batch, m)) return;   // 1.2.238: it stays where it was
            s_cells.Remove(batch.Index, s_instPos[slot]);
            Vector4 lodWorld = MovedLod(slot, m, batch, out float cellStart, out float cellEnd);   // the instance keeps its own LOD window; its reference point moves with it
            var inv = m.inverse; NoteMatrix(m, inv);   // 1.2.232
            NoteScale(batch, m);   // 1.2.233
            s_inst[slot] = new GpuInstance { O0 = m.GetRow(0), O1 = m.GetRow(1), O2 = m.GetRow(2), W0 = inv.GetRow(0), W1 = inv.GetRow(1), W2 = inv.GetRow(2) };
            s_instPos[slot] = m.GetColumn(3);
            s_instLod[slot] = lodWorld;
            var wb = TransformBounds(batch.LocalBounds, m);
            batch.World.Encapsulate(wb);
            s_cells.Add(batch.Index, s_instPos[slot], wb.min, wb.max, cellEnd, cellStart);
            MarkDirty(slot);
        }

        // [wishbone]: a moved instance kept no LOD window (the caller has none to give, so the slot was written "no LOD" and every level drew at once).
        // Its reference point goes through the old world-to-object into the new matrix; the window stays as stored (raw, or packed when per instance).
        private static Vector4 MovedLod(int slot, Matrix4x4 m, Batch batch, out float cellStart, out float cellEnd)
        {
            var old = s_instLod[slot]; var inst = s_inst[slot];
            cellStart = cellEnd = 0f;
            if (old.w == 0f || (inst.W0 == Vector4.zero && inst.W1 == Vector4.zero && inst.W2 == Vector4.zero)) return Vector4.zero;
            var r = new Vector4(old.x, old.y, old.z, 1f);
            var p = m.MultiplyPoint3x4(new Vector3(Vector4.Dot(inst.W0, r), Vector4.Dot(inst.W1, r), Vector4.Dot(inst.W2, r)));
            var moved = new Vector4(p.x, p.y, p.z, old.w);
            if (batch.LodPerInstance)
            {
                int bits = new IntFloat { F = old.w }.I;
                cellStart = Mathf.HalfToFloat((ushort)(bits & 0xFFFF));
                float end = Mathf.HalfToFloat((ushort)((bits >> 16) & 0xFFFF));
                cellEnd = end < 65504f ? end : 0f;   // the cells' 0 = never culled by distance
            }
            else { cellEnd = batch.EndHeight > 0f ? old.w / batch.EndHeight : 0f; cellStart = LodStartDistance(batch, moved); }
            return moved;
        }

        private static void MarkDirty(int slot)
        {
            if (slot < s_dirtyMin) s_dirtyMin = slot; if (slot > s_dirtyMax) s_dirtyMax = slot;
            int w = slot >> 6;   // 1.2.210: the exact changed slots too (IndirectSparseUpload)
            if (w >= s_dirtyBits.Length) Array.Resize(ref s_dirtyBits, Mathf.NextPowerOfTwo(w + 1));
            s_dirtyBits[w] |= 1UL << (slot & 63);
        }

        // 1.2.210 (VPO study #2, our own method): one bit per changed instance slot; Upload sends only runs of changed slots, a gap of more
        // than SparseGap clean slots splits a run (one SetData per run per buffer). The span upload it replaces could send everything
        // between a low reused slot and the top in one frame.
        private static ulong[] s_dirtyBits = new ulong[16];
        private const int SparseGap = 256;
        internal static long s_upSlots, s_upRuns, s_upBytes;   // Debug: uploaded since the last [Indirect] line
        private static bool SparseOn => FiresEasyBakeMeshesPlugin.IndirectSparseUpload == null || FiresEasyBakeMeshesPlugin.IndirectSparseUpload.Value;

        private static Bounds TransformBounds(Bounds b, Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(b.center);
            var e = b.extents;
            var r = new Vector3(Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
            return new Bounds(c, r * 2f);
        }

        // Called once a frame after the groups were walked: drops groups no zone drew this frame (unloaded / unbaked), uploads, culls, draws.
        internal static void EndFrame()
        {
            if (!Active) return;
            PhaseBegin();   // 1.2.212: per-phase timers (Debug)
            long tPh = PhT();
            if (!s_settledFrame) foreach (var kv in s_groups) if (kv.Value.Seen != s_frame) s_gone.Add(kv.Key);   // IndirectSettledWalk: a frame that walked only the changed groups has not stamped Seen on the rest; they must not be dropped
            bool dropped = s_gone.Count > 0;
            long ztDrop = dropped ? EbmSelfCost.ZT() : 0L;   // Debug: this frame's unbudgeted group drop
            foreach (var g in s_gone) { Drop(g, s_groups[g]); s_groups.Remove(g); IndirectLooks.Forget(g); }
            EbmSelfCost.ZAcc(EbmSelfCost.ZsIndirectDrop, ztDrop, s_gone.Count);
            if (!s_settledFrame) foreach (var kv in s_refused) if (kv.Value.seen != s_frame) s_gone.Add(kv.Key);   // unloaded groups: not kept alive by a refusal
            foreach (var g in s_gone) s_refused.Remove(g);
            s_gone.Clear();
            StageTick();   // [Optimize] IndirectStagedSync: finished jobs, then commits under the budget (after the drops, so a dropped group's stage is gone first)
            MaybeCompactCells(dropped);   // 1.2.213 IndirectFusedCells: empty cells leave the walk (at most every 5 s)
            RefreshAlive();               // 1.2.213 IndirectCachedAlive: one Mesh / Material check per batch for this frame
            PhAcc(PhGroups, tPh);
            var cam = Utils.GetMainCamera();
            if (cam == null || s_live == 0) { LastCalls = 0; LastInstances = 0; IndirectStats.FrameEnd(); PhaseEnd(); return; }
            try
            {
                tPh = PhT(); PresizeTick(); EnsureBuffers(); EnsureCascadeBuffers(); PhAcc(PhEnsure, tPh);
                tPh = PhT(); Upload(); UploadFlips(); PhAcc(PhUpload, tPh);   // UploadFlips: IndirectStagedSync's flipped batch ids only
                s_drawCamera = cam;
                tPh = PhT(); if (IndirectLooks.Upload()) s_bindingsDirty = true; PhAcc(PhLooks, tPh);   // A2: a new looks buffer = rebind every block
                tPh = PhT(); Cull(cam); PhAcc(PhCull, tPh);
                tPh = PhT(); Draw(); PhAcc(PhDraw, tPh);
            }
            catch (Exception e)
            {
                s_faulted = "it threw " + e.GetType().Name + ": " + e.Message + " (latched until the key goes off and on)";
                SetActive(false, s_faulted);   // never a broken frame twice: back to the instanced path
                return;
            }
            IndirectStats.FrameEnd();
            PhaseEnd();
        }

        private static void EnsureBuffers()
        {
            if (s_instBuf == null || s_instBuf.count < s_inst.Length)
            {
                var nInst = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s_inst.Length, Marshal.SizeOf<GpuInstance>());
                var nBatch = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s_inst.Length, sizeof(uint));
                var nLod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s_inst.Length, 16);
                // 1.2.213 IndirectGpuGrow: copy the old contents on the GPU (the CPU then sends only the changed slots), instead of every slot again
                if (GpuGrowOn && s_instBuf != null && s_instBatchBuf != null && s_instLodBuf != null && EbmIndirectAssets.KernelCopyGrow >= 0 && EbmIndirectAssets.Cull != null)
                {
                    var cs = EbmIndirectAssets.Cull; int kc = EbmIndirectAssets.KernelCopyGrow, n = Mathf.Min(s_instBuf.count, s_inst.Length);
                    cs.SetBuffer(kc, EbmIndirectAssets.IdCopySrcInst, s_instBuf); cs.SetBuffer(kc, EbmIndirectAssets.IdCopyDstInst, nInst);
                    cs.SetBuffer(kc, EbmIndirectAssets.IdCopySrcU, s_instBatchBuf); cs.SetBuffer(kc, EbmIndirectAssets.IdCopyDstU, nBatch);
                    cs.SetBuffer(kc, EbmIndirectAssets.IdCopySrcF, s_instLodBuf); cs.SetBuffer(kc, EbmIndirectAssets.IdCopyDstF, nLod);
                    cs.SetInt(EbmIndirectAssets.IdCopyCount, n);
                    int g = Mathf.CeilToInt(n / (float)Group);
                    cs.Dispatch(kc, Mathf.Min(Mathf.Max(1, g), 65535), Mathf.Max(1, Mathf.CeilToInt(g / 65535f)), 1);
                    s_gpuGrows++;   s_lastGrowFrame = Time.frameCount;   // 1.2.226 integrity
                }
                else { s_dirtyMin = 0; s_dirtyMax = s_high - 1; s_fullUpload = true; }   // 1.2.210: a new buffer gets every slot once
                Release(ref s_instBuf); Release(ref s_instBatchBuf); Release(ref s_instLodBuf);
                s_instBuf = nInst; s_instBatchBuf = nBatch; s_instLodBuf = nLod;
                s_bindingsDirty = true;
            }
            bool stable = StableOn;
            if (stable != s_lastStable) { s_lastStable = stable; s_layoutDirty = true; }   // a live flip relayouts once
            int nb = s_batches.Count;
            if (!s_layoutDirty && s_batchBuf != null)
            {
                if (s_place.Count == 0) return;
                if (stable && nb <= s_batchCap && PlaceQueued()) return;
                s_layoutDirty = true;   // no room (rows or visible space), or the key is off: one full layout
            }
            // full layout: each batch's visible range per view = its current count + headroom
            int offset = 0;
            foreach (var b in s_batches)
            {
                if (b.Mesh == null) b.Count = 0;   // a mesh another mod destroyed (FAP lazy bundles): the batch stays empty ([generator] 1)
                b.Capacity = b.Count + b.Count / 4 + 32;   // 1.2.210 ([generator]): headroom, so +1 at a power of two no longer relayouts every batch
                b.VisibleOffset = offset;
                offset += b.Capacity;
                b.PlaceQueued = false; b.Placed = true;
            }
            s_place.Clear();
            // 1.2.210 IndirectStableLayout: args rows for a power-of-two batch CAPACITY (draw = view x cap + batch), so a new batch takes a free
            // row without moving anyone; and visible space with a quarter to spare, so a batch that outgrows its range can move to the end
            int cap = stable ? Mathf.NextPowerOfTwo(Mathf.Max(Mathf.Max(16, nb + nb / 4), s_presizeBatches)) : nb;
            int stride = Mathf.NextPowerOfTwo(Mathf.Max(1, stable ? Mathf.Max(offset + offset / 4 + 1024, s_presizeStride) : offset));
            s_batchCap = cap;
            s_visibleUsed = offset; s_visibleWaste = 0;
            if (s_gpuBatches.Length < cap) s_gpuBatches = new GpuBatch[Mathf.NextPowerOfTwo(Mathf.Max(1, cap))];
            if (s_args.Length < cap * Views * ArgWords) s_args = new uint[Mathf.NextPowerOfTwo(Mathf.Max(1, cap)) * Views * ArgWords];
            System.Array.Clear(s_args, 0, cap * Views * ArgWords);
            for (int i = 0; i < nb; i++) WriteBatchRows(i);
            if (s_batchBuf == null || s_batchBuf.count < s_gpuBatches.Length) { Release(ref s_batchBuf); s_batchBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s_gpuBatches.Length, Marshal.SizeOf<GpuBatch>()); }
            if (s_argsBuf == null || s_argsBuf.count < s_args.Length) { Release(ref s_argsBuf); s_argsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, s_args.Length, sizeof(uint)); }
            if (s_visibleBuf == null || s_visibleStride < stride) { Release(ref s_visibleBuf); s_visibleStride = stride; s_visibleBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, stride * Views, sizeof(uint)); Release(ref s_cascScratch); Release(ref s_cascVisible); Release(ref s_otherScratch); Release(ref s_otherVisible); }
            if (s_otherVisible == null) { s_otherScratch = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource, s_visibleStride * (OtherView + 1), sizeof(uint)); s_otherVisible = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, s_visibleStride * (OtherView + 1), sizeof(uint)); s_bindEpoch++; s_cascadesDirty = true; s_bindingsDirtyOther = true; }   // 1.2.225: view 6 for the other cameras; views 2..5 for the shadow-pass cull are in EnsureCascadeBuffers
            s_batchBuf.SetData(s_gpuBatches, 0, 0, Mathf.Max(1, nb));
            s_argsBuf.SetData(s_args, 0, 0, Mathf.Max(1, cap) * Views * ArgWords);
            s_lastLayoutFrame = Time.frameCount;   // 1.2.226 integrity
            s_layoutDirty = false;
            s_bindingsDirty = true;
            BuildOrder(nb);
            s_layouts++; NotePresizeLayout(cap, s_visibleStride);
            EasyBakeLog.Debug($"[Indirect] layout: {nb} batch(es) in {cap} row(s), {s_live} instance(s) in {s_high} slot(s), visible stride {s_visibleStride}");
            SayLodMerge(nb);
        }

        // 1.2.220 (lead: IndirectLodPerInstance "never logged a line"): what the key does on THIS world. Batches that share mesh + submesh +
        // material + shadow + layer are the only ones a LOD-window merge can join; the line says how many there are, so "it saves nothing here"
        // is a measured answer, not a silent no-op. Info on the first full layout and whenever the key state changed, Debug after.
        private static bool? s_lodMergeSaid;
        private static void SayLodMerge(int nb)
        {
            bool on = LodPerInstanceOn;
            var seen = new HashSet<(Mesh, int, Material, ShadowCastingMode, int)>();
            int perInst = 0;
            for (int i = 0; i < nb; i++) { var b = s_batches[i]; if (b.LodPerInstance) perInst++; seen.Add((b.Mesh, b.SubMesh, b.Material, b.Shadows, b.Layer)); }
            int sharing = nb - seen.Count;
            string line = $"[Indirect] IndirectLodPerInstance {(on ? "ON" : "off")}: {perInst} of {nb} batch(es) carry their LOD window per instance; {seen.Count} distinct mesh+material+shadow+layer, so a LOD-window merge can join at most {sharing} batch(es) here{(on ? " (already joined)" : "")}";
            if (s_lodMergeSaid != on) { s_lodMergeSaid = on; EasyBakeLog.Info(line); } else EasyBakeLog.Debug(line);
        }

        // ---- 1.2.220 ([visual], VPO compare: prelate.scripts 3.11 vs 1.54 ms, the cell walk ~1.1 ms of it) [Optimize] IndirectCellReuse ----
        // The fused cell walk's output depends on the camera, the sun volume, the cascade spheres, the lights and the cells. While the key is on,
        // the CPU test uses the camera / sun planes widened by CellReuseMargin, so last frame's verdict stays a superset while the camera has moved
        // less than CellReuseMove and turned less than CellReuseTurn (at 1 km that turn moves an edge < 0.9 m, inside the margin), no cell changed
        // (IndirectCells.Version), the cascade spheres moved < 0.5 m (they carry their own 4 m margin) and the light set is the same. The GPU cull
        // stays exact per instance, so nothing new is ever drawn wrongly; at worst a few more batches are submitted. Standing still: ~0 walk cost.
        private const float CellReuseMargin = 3f, CellReuseMove = 0.5f, CellReuseTurnCos = 0.9999996f;   // cos(0.05 deg); [generator]: margin >= move + D sin(turn) up to ~2.9 km
        private static bool LooksChangeDrivenOn => FiresEasyBakeMeshesPlugin.IndirectLooksChangeDriven == null || FiresEasyBakeMeshesPlugin.IndirectLooksChangeDriven.Value;
        internal static readonly int[] s_looksWhy = new int[13];   // 1.2.227
        private static readonly string[] LooksWhyNames = { "version", "not written yet", "snow gen", "ext gen", "looks on/off", "slot count", "not taken (main Draw cleared it)", "snow dirty", "re-split pass running", "wet/field changed", "inside gen", "cell size", "no ranges" };
        internal static string LooksWhyTake() { var sb = new System.Text.StringBuilder(); for (int i = 0; i < s_looksWhy.Length; i++) if (s_looksWhy[i] > 0) { sb.Append(sb.Length > 0 ? ", " : "").Append(LooksWhyNames[i]).Append(' ').Append(s_looksWhy[i]); s_looksWhy[i] = 0; } return sb.Length > 0 ? sb.ToString() : "none"; }
        // 1.2.232 (the stretch hunt): an instance whose matrix or inverse is not finite / absurdly large would draw stretched; said once (Error)
        private static bool s_badMatrixSaid; internal static int s_badMatrices;
        private static bool Finite(Vector4 v) { float s = v.x + v.y + v.z + v.w; return !float.IsNaN(s) && !float.IsInfinity(s) && Mathf.Abs(s) < 1e7f; }
        // 1.2.235 (lead, Fire's live test on 1234: IndirectRenderer off = gone, on = back: PERSISTENT source data EBM re-reads; Integrity only
        // proves GPU == what we uploaded, not that what we computed was sane): an instance matrix with an axis scale over 10 or under 0.05, a
        // near-zero determinant, or a world box over 150 m is NOT drawn (zeroed) and named: one Error with the batch mesh, the matrix, and the
        // caller's context (prefab / part path / zone, set by Sync and coverage); later ones Debug, counted on the [Integrity] line.
        internal static string s_addContext;
        private static int s_badInst; private static bool s_badInstSaid;
        // 1.2.237 (lead): real assets carry 100x rigs, so a raw axis scale is logged and still drawn. Hidden only: not finite, no volume, or a world box over the limit.
        private const int MaxNamedInstances = 512;
        private static readonly System.Collections.Generic.HashSet<string> s_badNamed = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<string> s_scaleNamed = new System.Collections.Generic.HashSet<string>();
        private static bool BadInstance(Batch b, Matrix4x4 m) => b != null && BadInstance(b.Mesh, b.SubMesh, b.LocalBounds, m);

        // 1.2.238 ([generator]): coverage asks before a batch exists; -1 from IndirectBatches.Add rolls back and Unity keeps drawing the piece
        internal static bool RefuseCovered(Mesh mesh, int subMesh, in Matrix4x4 m)
        {
            var bounds = mesh.GetSubMesh(Mathf.Clamp(subMesh, 0, mesh.subMeshCount - 1)).bounds;
            if (bounds.size == Vector3.zero) bounds = mesh.bounds;
            return BadInstance(mesh, subMesh, bounds, m);
        }

        private const float MaxBoxPerMeshSize = 100f;
        private const float MaxInstanceWorldBox = 300f;   // 1.2.239: a big scaled oak is 150 m corner to corner; a stretched instance is far past this

        private static bool BadInstance(Mesh mesh, int subMesh, Bounds localBounds, Matrix4x4 m)
        {
            bool finite = true;
            for (int e = 0; e < 16 && finite; e++) finite = !float.IsNaN(m[e]) && !float.IsInfinity(m[e]);
            float sx = 0f, sy = 0f, sz = 0f, max = 0f, min = 0f, det = 0f, box = 0f;
            string why = null;
            if (!finite) why = "a value that is not finite";
            else
            {
                sx = new Vector3(m.m00, m.m10, m.m20).magnitude; sy = new Vector3(m.m01, m.m11, m.m21).magnitude; sz = new Vector3(m.m02, m.m12, m.m22).magnitude;
                max = Mathf.Max(sx, Mathf.Max(sy, sz)); min = Mathf.Min(sx, Mathf.Min(sy, sz));
                det = m.determinant;
                box = TransformBounds(localBounds, m).size.magnitude;
                // measured against the scales, so a uniform 0.01 rig is not "no volume" but a flattened or collapsed matrix is
                if (min <= 1e-6f || Mathf.Abs(det) < 1e-4f * sx * sy * sz) why = $"no volume (determinant {det:G3})";
                else if (box > MaxInstanceWorldBox && box > MaxBoxPerMeshSize * (2f * localBounds.extents.magnitude)) why = $"world box {box:0} m, {box / Mathf.Max(0.001f, 2f * localBounds.extents.magnitude):0}x its own mesh";   // 1.2.241: rock1_mountain at scale 31.8 is 311 m and healthy
            }
            if (why == null)
            {
                if (max > 10f || min < 0.05f)
                {
                    string scaleName = mesh != null ? mesh.name : "?";   // 1.2.239: only here and below: a native string per call otherwise
                    if (s_scaleNamed.Count < MaxNamedInstances && s_scaleNamed.Add(scaleName + "|" + max.ToString("0.###") + "|" + min.ToString("0.###")))
                        EasyBakeLog.Debug($"[Integrity] unusual scale, still drawn: mesh '{scaleName}' submesh {subMesh}, scales {sx:0.###} / {sy:0.###} / {sz:0.###}, context {s_addContext ?? "?"}, position {m.GetColumn(3)}");   // 1.2.239 (Fire): a normal asset is never red
                }
                return false;
            }
            string name = mesh != null ? mesh.name : "?";
            s_badInst++;
            if (s_badNamed.Count < MaxNamedInstances && s_badNamed.Add(name + "|" + (s_addContext ?? "?")))
                EasyBakeLog.Error($"[Integrity] BAD INSTANCE NOT DRAWN at frame {Time.frameCount}: {why}; mesh '{name}' submesh {subMesh}, context {s_addContext ?? "?"}; scales {sx:0.###} / {sy:0.###} / {sz:0.###}, position {m.GetColumn(3)}");
            else EasyBakeLog.Debug($"[Integrity] bad instance #{s_badInst} not drawn: {why}; mesh '{name}', context {s_addContext ?? "?"}, position {m.GetColumn(3)}");
            return true;
        }

        private static void NoteScale(Batch b, Matrix4x4 m)
        {
            if (b == null) return;
            float s = Mathf.Max(new Vector3(m.m00, m.m10, m.m20).sqrMagnitude, Mathf.Max(new Vector3(m.m01, m.m11, m.m21).sqrMagnitude, new Vector3(m.m02, m.m12, m.m22).sqrMagnitude));
            if (s > b.MaxScale * b.MaxScale && s < 1e8f) b.MaxScale = Mathf.Sqrt(s);
        }

        private static void NoteMatrix(Matrix4x4 m, Matrix4x4 inv)
        {
            bool singular = Mathf.Abs(m.determinant) < 1e-12f;   // [generator]: Unity returns an all-zero inverse here = zero normals = NaN after normalize in the shader
            if (!singular && Finite(m.GetRow(0)) && Finite(m.GetRow(1)) && Finite(m.GetRow(2)) && Finite(inv.GetRow(0)) && Finite(inv.GetRow(1)) && Finite(inv.GetRow(2))) return;
            s_badMatrices++;
            if (s_badMatrixSaid) return;
            s_badMatrixSaid = true;
            EasyBakeLog.Error($"[Integrity] {(singular ? "SINGULAR" : "NON-FINITE")} INSTANCE MATRIX at frame {Time.frameCount} (det {m.determinant}): m rows {m.GetRow(0)} {m.GetRow(1)} {m.GetRow(2)}, inverse rows {inv.GetRow(0)} {inv.GetRow(1)} {inv.GetRow(2)} (scale {m.lossyScale})");
        }

        internal static long HeapBytes() => (long)s_inst.Length * 96 + (long)s_instBatch.Length * 4 + (long)s_args.Length * 4 + (long)s_gpuBatches.Length * 64 + (long)s_vis.Length + (long)s_order.Length * 4;   // 1.2.229 heap census (CPU side)
        internal static long HeapBytesPosLod() => (long)s_instPos.Length * 12 + (long)s_instLod.Length * 16;   // [Heap] by holder: the slot arrays HeapBytes leaves out
        internal static int LooksSkippedTake() { int n = s_looksSkipped; s_looksSkipped = 0; return n; }   // 1.2.225
        internal static int s_looksSkipped;   // 1.2.223 Debug: Take calls that skipped looks prep + write (inputs unchanged)
        private static bool CellReuseOn => FiresEasyBakeMeshesPlugin.IndirectCellReuse == null || FiresEasyBakeMeshesPlugin.IndirectCellReuse.Value;
        private static Vector3 s_crPos, s_crFwd, s_crUp; private static int s_crVersion = -1, s_crFaces = -1, s_crLights = -1, s_crCascades = -1, s_crBatches = -1, s_crWidth = -1;
        private static readonly Vector4[] s_crCasc = new Vector4[4], s_crLight = new Vector4[MaxLocal];
        internal static long s_cellReused;
        private static int s_shadowPlaneCount;
        private static int s_shadowPlaneMask, s_crShadowMask; private static float s_crSunCos = 1f;
        private static Vector3 s_crSun; private static float s_crFov, s_crAspect, s_crLodBias, s_crMin; private static int s_crShadowPlanes;
        private static Vector3 SunFwd() { var e = EnvMan.instance; var l = e != null ? e.m_dirLight : null; return l != null ? l.transform.forward : Vector3.zero; }
        // 1.2.254 [Optimize] IndirectCellReusePerCell: the Version test gives way to a merge of the dirty cells, judged against the POSE OF THE LAST FULL WALK (the stored planes, position and
        // spheres below), so a merged answer holds the same margin as a walked one. Every other test stays: moving more than CellReuseMove or turning at all still walks in full.
        private static readonly Plane[] s_crPerCellPlanes = new Plane[12];
        private static Vector3 s_crPerCellLight; private static bool s_crPerCellStored, s_crPerCellSaid;
        private static readonly string[] CrPerCellWhyNames = { "sun or shadow planes", "projection or LOD", "cells / lights / batches changed", "camera moved", "camera turned", "cascade spheres moved", "lights moved" };
        private static readonly int[] s_crPerCellWhy = new int[7];
        private static int s_crPerCellWhyNow, s_crPerCellMerges, s_crPerCellMergedCells, s_crPerCellRemoveOnly, s_crPerCellFull;
        private static bool CellReusePerCellOn => CellReuseOn && FiresEasyBakeMeshesPlugin.IndirectCellReusePerCell != null && FiresEasyBakeMeshesPlugin.IndirectCellReusePerCell.Value;
        private static string CellReusePerCellLine()
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append($"; per-cell reuse: {s_crPerCellMerges} frame(s) merged {s_crPerCellMergedCells} changed cell(s), {s_crPerCellRemoveOnly} kept the verdict after removals only, {s_crPerCellFull} full walk(s) (why:");
            for (int i = 0; i < s_crPerCellWhy.Length; i++) { sb.Append(' ').Append(CrPerCellWhyNames[i]).Append(' ').Append(s_crPerCellWhy[i]).Append(','); s_crPerCellWhy[i] = 0; }
            sb.Append(')');
            s_crPerCellMerges = s_crPerCellMergedCells = s_crPerCellRemoveOnly = s_crPerCellFull = 0;
            return sb.ToString();
        }

        private static bool CellReuseValid(Camera cam, int faceViews, float min, bool mergeCells = false)
        {
            // [generator]: the sun direction (sun-volume plane choice + the cascade sweep) and the projection / LOD inputs are part of the verdict too
            // 1.2.222: the sun may drift 0.25 deg (cascade sweep: 0.25 deg x 600 m shadow distance = 2.6 m, inside the 4 m sphere margin and the 3 m plane margin; the plane choice is the count check), so outdoors a reuse lasts ~1.25 s, not ~0.25 s
            s_crPerCellWhyNow = 0;
            if (Vector3.Dot(SunFwd(), s_crSun) < s_crSunCos || s_crShadowPlanes != s_shadowPlaneCount || s_crShadowMask != s_shadowPlaneMask) return false;
            s_crPerCellWhyNow = 1;
            if (cam.fieldOfView != s_crFov || cam.aspect != s_crAspect || QualitySettings.lodBias != s_crLodBias || min != s_crMin) return false;
            s_crPerCellWhyNow = 2;
            if ((!mergeCells && s_crVersion != s_cells.Version) || s_crFaces != faceViews || s_crLights != s_localCount || s_crCascades != s_cascadeCount || s_crBatches != s_batches.Count || s_crWidth != s_cells.BatchCount) return false;
            var tr = cam.transform;
            s_crPerCellWhyNow = 3;
            if ((tr.position - s_crPos).sqrMagnitude > CellReuseMove * CellReuseMove) return false;
            s_crPerCellWhyNow = 4;
            if (Vector3.Dot(tr.forward, s_crFwd) < CellReuseTurnCos || Vector3.Dot(tr.up, s_crUp) < CellReuseTurnCos) return false;
            s_crPerCellWhyNow = 5;
            for (int k = 0; k < s_cascadeCount; k++) { var a = s_cascadeSpheres[k]; var b = s_crCasc[k]; if ((new Vector3(a.x - b.x, a.y - b.y, a.z - b.z)).sqrMagnitude > 0.25f || Mathf.Abs(a.w - b.w) > 0.5f) return false; }
            s_crPerCellWhyNow = 6;
            for (int k = 0; k < s_localCount; k++) if (s_localSpheres[k] != s_crLight[k]) return false;
            return true;
        }
        private static void CellReuseStore(Camera cam, int faceViews, float min)
        {
            s_crSun = SunFwd(); s_crShadowPlanes = s_shadowPlaneCount; s_crShadowMask = s_shadowPlaneMask;
            // [generator]: the swept cascade test moves by A sin(turn), A = the entry's distance along the light, so the allowed sun turn comes from the
            // data: asin(3.5 m / farthest cell reach), at most 0.25 deg (3.5 = the 4 m sphere margin less the 0.5 m sphere-centre compare)
            float maxA = s_cells.FarthestCornerFrom(cam.transform.position) + (s_cascadeCount > 0 ? s_cascadeSpheres[s_cascadeCount - 1].w : 0f);
            float ang = maxA > 1f ? Mathf.Asin(Mathf.Clamp01(3.5f / maxA)) : 0.00436f;
            s_crSunCos = Mathf.Cos(Mathf.Min(ang, 0.00436f)); s_crFov = cam.fieldOfView; s_crAspect = cam.aspect; s_crLodBias = QualitySettings.lodBias; s_crMin = min;
            var tr = cam.transform; s_crPos = tr.position; s_crFwd = tr.forward; s_crUp = tr.up;
            s_crVersion = s_cells.Version; s_crFaces = faceViews; s_crLights = s_localCount; s_crCascades = s_cascadeCount; s_crBatches = s_batches.Count; s_crWidth = s_cells.BatchCount;
            for (int k = 0; k < s_cascadeCount; k++) s_crCasc[k] = s_cascadeSpheres[k];
            for (int k = 0; k < s_localCount; k++) s_crLight[k] = s_localSpheres[k];
        }

        // ---- 1.2.210 IndirectStableLayout (VPO study #3, our own method): batches placed one at a time ----
        private static readonly List<Batch> s_place = new List<Batch>();
        private static int s_batchCap, s_visibleUsed, s_visibleWaste;
        private static bool s_lastStable = true;
        internal static int s_moved;   // Debug: batches placed alone since the last [Indirect] line
        private static bool StableOn => FiresEasyBakeMeshesPlugin.IndirectStableLayout == null || FiresEasyBakeMeshesPlugin.IndirectStableLayout.Value;

        private static void RequestPlace(Batch b)
        {
            if (!StableOn || s_batchBuf == null) { s_layoutDirty = true; return; }
            if (b.PlaceQueued) return;
            b.PlaceQueued = true;
            s_place.Add(b);
        }

        // gives each queued batch its own range at the end of the used visible space (new batches also their args rows); false = no room
        private static bool PlaceQueued()
        {
            int need = 0;
            foreach (var b in s_place) need += b.Count + b.Count / 4 + 32;
            if (s_visibleUsed + need > s_visibleStride || s_visibleWaste + need > s_visibleStride / 2) return false;   // full, or too fragmented: relayout
            bool added = false;
            foreach (var b in s_place)
            {
                if (b.Placed) s_visibleWaste += b.Capacity; else added = true;
                if (b.Mesh == null) b.Count = 0;
                b.Capacity = b.Count + b.Count / 4 + 32;
                b.VisibleOffset = s_visibleUsed;
                BindDebugRadius(b);   // 1.2.244: this path binds a new batch without the full rebind
                s_visibleUsed += b.Capacity;
                b.Placed = true; b.PlaceQueued = false;
                WriteBatchRows(b.Index);
                s_batchBuf.SetData(s_gpuBatches, b.Index, b.Index, 1);
                for (int v = 0; v < Views; v++) { int a = (v * s_batchCap + b.Index) * ArgWords; s_argsBuf.SetData(s_args, a, a, ArgWords); }
                // only this batch's blocks re-bind; its caster blocks re-bind before their next record
                Bind(b.CameraProps, b.VisibleOffset);
                Bind(b.ShadowProps, s_visibleStride + b.VisibleOffset);
                if (b.OtherProps != null) BindOther(b.OtherProps, OtherView * s_visibleStride + b.VisibleOffset);
                BindSlotProps(b);   // [wishbone]: the pre-culled cameras' blocks follow the moved range too
                b.CasterBound = -1; b.LocalBound = -1;
                if (b.Shadows != ShadowCastingMode.Off) NoteCasterMoved(b);   // its recorded draws hold the old blocks (1.2.213: only the light slots holding it redo)
                s_moved++;
            }
            s_place.Clear();
            if (added) BuildOrder(s_batches.Count);
            return true;
        }

        // the batch's GPU entry and its args row in every view (zero instances; the cull counts them)
        private static void WriteBatchRows(int i)
        {
            var b = s_batches[i];
            s_gpuBatches[i] = new GpuBatch
            {
                Center = b.LocalBounds.center, Extents = b.LocalBounds.extents, VisibleOffset = (uint)b.VisibleOffset,
                Flags = (b.Shadows != ShadowCastingMode.Off ? 1u : 0u) | (b.LodPerInstance ? 2u : 0u), Lod = b.Lod, StartHeight = b.StartHeight, EndHeight = b.EndHeight, Capacity = (uint)b.Capacity,
            };
            if (b.Mesh == null)
            {
                b.Tris = 0;
                for (int v = 0; v < Views; v++) { int z = (v * s_batchCap + i) * ArgWords; s_args[z] = s_args[z + 1] = s_args[z + 2] = s_args[z + 3] = s_args[z + 4] = 0; }
                return;
            }
            var sm = b.Mesh.GetSubMesh(Mathf.Clamp(b.SubMesh, 0, b.Mesh.subMeshCount - 1));
            b.Tris = sm.indexCount / 3;
            b.VertCount = b.Mesh.vertexCount;   // 1.2.234
            for (int v = 0; v < Views; v++)
            {
                int a = (v * s_batchCap + i) * ArgWords;
                s_args[a] = (uint)sm.indexCount; s_args[a + 1] = 0; s_args[a + 2] = (uint)sm.indexStart; s_args[a + 3] = (uint)sm.baseVertex; s_args[a + 4] = 0;
            }
        }

        // 1.2.210: the batches in (shader, material, mesh, submesh) order. Command buffers execute in the order they were recorded, so the
        // cascade and light casters recorded this way switch shader / material only when they must (fewer SetPass calls). Rebuilt per layout.
        private static int[] s_order = new int[0];
        private static int s_orderCount;
        internal static int s_layouts;   // Debug: layouts since the last [Indirect] line
        private static void BuildOrder(int nb)
        {
            if (s_order.Length < nb) s_order = new int[Mathf.NextPowerOfTwo(nb)];
            for (int i = 0; i < nb; i++) s_order[i] = i;
            Array.Sort(s_order, 0, nb, OrderCompare.Instance);
            s_orderCount = nb;
        }

        private sealed class OrderCompare : IComparer<int>
        {
            internal static readonly OrderCompare Instance = new OrderCompare();
            public int Compare(int x, int y)
            {
                var a = s_batches[x]; var b = s_batches[y];
                int c = Id(a.Material != null ? a.Material.shader : null).CompareTo(Id(b.Material != null ? b.Material.shader : null)); if (c != 0) return c;
                c = Id(a.Material).CompareTo(Id(b.Material)); if (c != 0) return c;
                c = Id(a.Mesh).CompareTo(Id(b.Mesh)); if (c != 0) return c;
                c = a.SubMesh.CompareTo(b.SubMesh); if (c != 0) return c;
                return x.CompareTo(y);
            }
            private static int Id(UnityEngine.Object o) => o != null ? o.GetInstanceID() : 0;
        }

        // the CPU cell cull's verdict for a batch in a row (a batch the cells have not seen yet counts as visible)
        private static bool VisRow(int row, int i) => i >= s_visWidth || s_vis[row * s_visWidth + i];

        // a batch can draw casters at all: instances, a mesh, a material with a ShadowCaster pass, shadows on
        private static bool CastsShadows(Batch b)
        {
            if (b.Count <= 0 || b.Shadows == ShadowCastingMode.Off || !HasMesh(b) || !HasMaterial(b)) return false;   // 1.2.213: cached alive checks, cheapest test first
            if (b.CasterPass == -2) b.CasterPass = b.Material.FindPass("ShadowCaster");
            return b.CasterPass >= 0;
        }

        private const float CpuSphereMargin = 4f;   // metres added to every cascade / light sphere in the CPU cull (lists do not flicker at an edge)
        private static Vector3 s_cullCamPos; private static float s_cullLodScale;

        private static void Upload()
        {
            if (s_dirtyMax < s_dirtyMin) return;
            int w0 = s_dirtyMin >> 6, w1 = Mathf.Min(s_dirtyMax >> 6, s_dirtyBits.Length - 1);
            if (!SparseOn || s_fullUpload)
            {
                int start = s_dirtyMin, n = s_dirtyMax - s_dirtyMin + 1;
                UploadRun(start, n);
            }
            else
            {
                int runStart = -1, runEnd = -1;
                for (int w = w0; w <= w1; w++)
                {
                    ulong bits = s_dirtyBits[w];
                    if (bits == 0) continue;
                    for (int bit = 0; bit < 64; bit++)
                    {
                        if ((bits & (1UL << bit)) == 0) continue;
                        int slot = (w << 6) + bit;
                        if (slot >= s_high) break;
                        if (runStart < 0) { runStart = runEnd = slot; continue; }
                        if (slot - runEnd > SparseGap) { UploadRun(runStart, runEnd - runStart + 1); runStart = slot; }
                        runEnd = slot;
                    }
                }
                if (runStart >= 0) UploadRun(runStart, runEnd - runStart + 1);
            }
            System.Array.Clear(s_dirtyBits, w0, Mathf.Max(0, Mathf.Min(s_dirtyBits.Length, (s_dirtyMax >> 6) + 1) - w0));
            s_dirtyMin = int.MaxValue; s_dirtyMax = -1;
            AddsWaiting = 0; RemovesWaiting = 0;   // [seasons] 11-09 E28 census: everything added / removed so far is on the GPU now
            s_fullUpload = false;
        }

        private static bool s_fullUpload;   // a re-created buffer needs everything once
        private static void UploadRun(int start, int n)
        {
            if (n <= 0) return;
            s_instBuf.SetData(s_inst, start, start, n);
            s_instBatchBuf.SetData(s_instBatch, start, start, n);
            s_instLodBuf.SetData(s_instLod, start, start, n);
            s_upSlots += n; s_upRuns++; s_upBytes += n * 116L;
        }

        private static void Cull(Camera cam)
        {
            CascadeEmptyTick(); CameraEmptyTick();   // 1.2.242: before the clear below, while the args still hold last frame's shadow pass
            var cs = EbmIndirectAssets.Cull;
            int nb = s_batches.Count;
            GeometryUtility.CalculateFrustumPlanes(cam, s_cam);
            for (int i = 0; i < 6; i++) s_camV[i] = new Vector4(s_cam[i].normal.x, s_cam[i].normal.y, s_cam[i].normal.z, s_cam[i].distance);
            int shadowPlanes = ShadowPlanes(cam); s_shadowPlaneCount = shadowPlanes;   // 1.2.220: part of the cell-reuse key
            float halfTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float sizeScale = 1f / (2f * halfTan);
            float min = FiresEasyBakeMeshesPlugin.IndirectMinScreenPercent != null ? FiresEasyBakeMeshesPlugin.IndirectMinScreenPercent.Value * 0.01f : 0f;

            cs.SetInt(EbmIndirectAssets.IdDrawCount, s_batchCap * Views);   // 1.2.210: args rows per view = the batch capacity
            cs.SetInt(EbmIndirectAssets.IdDrawStart, 0);
            cs.SetInt(EbmIndirectAssets.IdViewMode, 0);
            cs.SetBuffer(EbmIndirectAssets.KernelClear, EbmIndirectAssets.IdArgs, s_argsBuf);
            cs.Dispatch(EbmIndirectAssets.KernelClear, Mathf.CeilToInt(s_batchCap * Views / (float)Group), 1, 1);

            int k = EbmIndirectAssets.CullKernel;   // G3: EbmCullGrouped when [Optimize] IndirectCullGrouped
            cs.SetBuffer(k, EbmIndirectAssets.IdInstances, s_instBuf);
            cs.SetBuffer(k, EbmIndirectAssets.IdInstBatch, s_instBatchBuf);
            cs.SetBuffer(k, EbmIndirectAssets.IdInstLod, s_instLodBuf);
            cs.SetBuffer(k, EbmIndirectAssets.IdBatches, s_batchBuf);
            cs.SetBuffer(k, EbmIndirectAssets.IdVisible, s_visibleBuf);
            cs.SetBuffer(k, EbmIndirectAssets.IdArgs, s_argsBuf);
            cs.SetVectorArray(EbmIndirectAssets.IdCamPlanes, s_camV);
            cs.SetVectorArray(EbmIndirectAssets.IdShadowPlanes, s_shadowV);
            cs.SetInt(EbmIndirectAssets.IdShadowPlaneCount, shadowPlanes);
            SetCascadeUniforms(cs, cam);   // 1.2.208: 0 cascades unless [Optimize] IndirectCascadeShadows
            // 1.2.211 ([saddles] G6 v2): the cascade casters are culled in the shadow pass (EbmIndirectReceivers); the Update dispatch then leaves
            // views 2..5 at EbmClear's zero. Its receiver inputs are dummies (the kernel declares them).
            cs.SetInt(IdCascadeInMain, PlanCascadePrep(EnvMan.instance != null ? EnvMan.instance.m_dirLight : null, shadowPlanes));
            cs.SetBuffer(k, IdRxMap, RxMapDummy());   // G3 merge: the kernel actually dispatched
            cs.SetBuffer(k, IdCascadeCapture, CascadeCaptureBuffer != null ? CascadeCaptureBuffer : CaptureDummy());   // G3 merge: the kernel actually dispatched
            cs.SetInt(IdRxOn, 0);
            var p = cam.transform.position;
            cs.SetVector(EbmIndirectAssets.IdCam, new Vector4(p.x, p.y, p.z, sizeScale * QualitySettings.lodBias));
            cs.SetFloat(EbmIndirectAssets.IdSizeScale, sizeScale);
            cs.SetFloat(EbmIndirectAssets.IdMinScreen, min);
            cs.SetInt(EbmIndirectAssets.IdInstanceCount, s_high);
            cs.SetInt(EbmIndirectAssets.IdBatchCount, s_batchCap);   // the args row stride
            cs.SetInt(EbmIndirectAssets.IdVisibleStride, s_visibleStride);
            // the CPU side: which batches have anything in each view's cells (camera planes, then the shadow volume's)
            for (int i = 0; i < 6; i++) { s_cellPlanes[i] = s_cam[i]; s_cellPlanes[6 + i] = new Plane(new Vector3(s_shadowV[i].x, s_shadowV[i].y, s_shadowV[i].z), s_shadowV[i].w); }
            int width = Mathf.Max(nb, s_cells.BatchCount);   // re-read every frame (Compact may shrink it)
            if (s_vis.Length < Views * width) s_vis = new bool[Mathf.NextPowerOfTwo(Views * width)];
            s_cellsFused = FusedCellsOn;
            if (s_cellsFused)
            {
                // 1.2.213 ([saddles], [Optimize] IndirectFusedCells): camera + sun volume (rows 0..1), cascades (2..5), lights (7..10) and the
                // lights' cube faces in ONE walk over the cells. The lights are gathered here, before the main dispatch, so CullLocal /
                // PrepareOnePass use them without a second gather or cell walk. Timed as "cells camera+sun" (the other cell phases read 0).
                long tC = PhT();
                s_cullCamPos = p; s_cullLodScale = sizeScale * QualitySettings.lodBias;
                GatherLocalLights();
                int faceViews = PrepareFaces();
                // 1.2.220 [Optimize] IndirectCellReuse: reuse last frame's verdict while nothing that feeds it changed (see CellReuseValid)
                bool perCell = CellReusePerCellOn;   // 1.2.254
                if (perCell && !s_crPerCellSaid) { s_crPerCellSaid = true; EasyBakeLog.Info("[Indirect] per-cell reuse on: added objects re-judge only their own cell"); }
                s_cells.TrackDirty = perCell;
                bool merge = perCell && s_crPerCellStored && s_crVersion != s_cells.Version && s_cells.DirtyMergeable;
                bool reuse = CellReuseOn && CellReuseValid(cam, faceViews, min, merge);
                if (reuse)
                {
                    s_cellReused++;
                    if (perCell && faceViews > 0) s_faceWidth = s_crWidth;   // PrepareFaces zeroed it; without this a reused frame reads every face as wanting every batch
                    if (merge)
                    {
                        int dirty = s_cells.DirtyCount;
                        if (dirty > 0)
                        {
                            CascadeVerdictChanged(); CameraVerdictChanged();   // as any re-judge: an emptied row may have gained an instance
                            s_cells.CullFused(s_crPerCellPlanes, CellViews, min, cam.projectionMatrix[1, 1], s_vis,
                                              s_crCasc, s_cascadeCount, s_crPerCellLight, 2,
                                              s_localSpheres, s_localCount, LocalView0, CpuSphereMargin,
                                              s_facePlanes, faceViews, s_faceVis, s_crPos, s_cullLodScale, mergeDirty: true);
                            s_crPerCellMerges++; s_crPerCellMergedCells += dirty;
                        }
                        else s_crPerCellRemoveOnly++;
                        s_cells.DirtyReset();
                        s_crVersion = s_cells.Version;
                    }
                    PhAcc(PhCellsView, tC);
                }
                else
                {
                if (perCell) { s_crPerCellFull++; s_crPerCellWhy[s_crPerCellWhyNow]++; }   // 1.2.254
                CascadeVerdictChanged(); CameraVerdictChanged();   // 1.2.242
                if (CellReuseOn) for (int i = 0; i < 12; i++) s_cellPlanes[i].distance += CellReuseMargin;   // widen camera + sun planes so a reuse stays a superset
                s_cells.CullFused(s_cellPlanes, CellViews, min, cam.projectionMatrix[1, 1], s_vis,
                                  s_cascadeSpheres, s_cascadeCount, s_cascadeLight, 2,
                                  s_localSpheres, s_localCount, LocalView0, CpuSphereMargin,
                                  s_facePlanes, faceViews, s_faceVis, p, s_cullLodScale);
                s_visWidth = s_cells.BatchCount;
                if (faceViews > 0) s_faceWidth = s_cells.BatchCount;
                CellReuseStore(cam, faceViews, min);
                s_crPerCellStored = perCell;   // 1.2.254: the pose this walk was judged at
                if (perCell) { System.Array.Copy(s_cellPlanes, s_crPerCellPlanes, 12); s_crPerCellLight = s_cascadeLight; s_cells.DirtyReset(); }
                PhAcc(PhCellsView, tC);
                }
            }
            else
            {
                long tC = PhT();
                s_cells.TrackDirty = false;   // 1.2.254: the per-cell merge is the fused walk's
                s_cells.CullViews(s_cellPlanes, CellViews, p, sizeScale * QualitySettings.lodBias, min, s_vis, cam.projectionMatrix[1, 1]);
                PhAcc(PhCellsView, tC);
                s_visWidth = s_cells.BatchCount;
                // 1.2.210: the same cells judged per cascade (rows 2..5), so a cascade's command buffer records only batches that can reach it
                s_cullCamPos = p; s_cullLodScale = sizeScale * QualitySettings.lodBias;
                tC = PhT();
                if (s_cascadeCount > 0) s_cells.CullSpheres(s_cascadeSpheres, s_cascadeCount, s_cascadeLight, CpuSphereMargin, s_vis, 2, p, s_cullLodScale);
                PhAcc(PhCellsSphere, tC);
            }
            int groups = Mathf.CeilToInt(s_high / (float)Group);
            bool onePass = PrepareOnePass(cs);   // G4 ([wishbone]): lights + last frame's other cameras in this same dispatch ([Optimize] IndirectOneCullPass)
            cs.Dispatch(k, Mathf.Min(groups, 65535), Mathf.Max(1, Mathf.CeilToInt(groups / 65535f)), 1);
            if (!onePass) CullLocal(cs, k, groups);   // 1.2.209 [wishbone]: views 7..10, the point / spot lights (only with IndirectCascadeShadows); G4: in the main pass instead
        }

        // The sun's shadow volume: the camera frustum cut at the shadow distance, keeping only the planes a caster cannot shadow across
        // (those facing away from the light); a caster upstream of the view, toward the sun, still passes. 0 planes = no sun shadows.
        private static int ShadowPlanes(Camera cam)
        {
            var env = EnvMan.instance;
            var sun = env != null ? env.m_dirLight : null;
            float dist = QualitySettings.shadowDistance;
            if (sun == null || !sun.isActiveAndEnabled || sun.shadows == LightShadows.None || dist <= 0f) return 0;
            Vector3 l = sun.transform.forward;   // the direction light travels
            int n = 0;
            s_shadowPlaneMask = 0;
            for (int i = 0; i < 6; i++)
            {
                var pl = s_cam[i];
                if (i == 5) pl = new Plane(-cam.transform.forward, cam.transform.position + cam.transform.forward * dist);   // far plane at the shadow distance
                if (Vector3.Dot(pl.normal, l) > 0f) continue;   // moving along the light raises the plane value: an outside caster can shadow inside
                s_shadowPlaneMask |= 1 << i;   // 1.2.222 ([generator]): which planes, not only how many (a swap keeps the count)
                s_shadowV[n++] = new Vector4(pl.normal.x, pl.normal.y, pl.normal.z, pl.distance);
            }
            for (int i = n; i < 6; i++) s_shadowV[i] = new Vector4(0, 0, 0, 1);   // always passes
            return n == 0 ? 6 : n;   // no plane survived: the always-passing fill keeps every caster
        }

        private static void Draw()
        {
            int nb = s_batches.Count;
            long tTicks = PhT(); ApplyVertexDebug(); ApplyPixelShave(); VertexSweepTick(); GpuValidatorTick(); PhAcc(PhDrawTicks, tTicks);   // 1.2.243, 1.2.245
            if (s_bindingsDirty)
            {
                foreach (var b in s_batches)
                {
                    BindDebugRadius(b);   // 1.2.243
                    Bind(b.CameraProps, b.VisibleOffset);
                    Bind(b.ShadowProps, s_visibleStride + b.VisibleOffset);
                    if (b.OtherProps != null) BindOther(b.OtherProps, OtherView * s_visibleStride + b.VisibleOffset);   // 1.2.210 ([generator]): other cameras never draw a stale block
                    BindSlotProps(b);   // G4
                }
                s_bindingsDirty = false;
                s_bindEpoch++;   // 1.2.210: caster blocks re-bind before their next use
                s_cascadesDirty = true;
                s_localDirty = true;   // 1.2.209 [wishbone]
                s_bindingsDirtyOther = false;   // 1.2.210: bound above
            }
            bool cacheRp = FiresEasyBakeMeshesPlugin.IndirectDrawParamsCache != null && FiresEasyBakeMeshesPlugin.IndirectDrawParamsCache.Value;
            int calls = 0, camB = 0, shB = 0; long held = 0, camTris = 0, shTris = 0; long tLoop = PhT();
            for (int i = 0; i < nb; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0 || !HasMesh(b)) continue;   // 1.2.213: cached alive check
                held += b.Count;
                long tris = b.Tris * b.Count;   // upper bound: held instances (the GPU's visible counts are not read back)
                bool camVis = i >= s_visWidth || s_vis[i], shVis = i >= s_visWidth || s_vis[s_visWidth + i];
                RenderParams rp;
                if (cacheRp && b.CachedRpOk && b.CachedRpCamera == s_drawCamera)
                {
                    rp = b.CachedRp;
                    rp.worldBounds = b.World; rp.shadowCastingMode = ShadowCastingMode.Off; rp.matProps = b.CameraProps;
                }
                else
                {
                    rp = new RenderParams(b.Material)
                    {
                        worldBounds = b.World, layer = b.Layer, renderingLayerMask = b.RenderingLayerMask, receiveShadows = b.ReceiveShadows,
                        shadowCastingMode = ShadowCastingMode.Off, matProps = b.CameraProps, camera = s_drawCamera,   // 1.2.209: main camera only; other cameras get their own view (EbmIndirectViews)
                    };
                    if (cacheRp) { b.CachedRp = rp; b.CachedRpCamera = s_drawCamera; b.CachedRpOk = true; }
                }
                if (camVis && !CameraEmptySkip(i))
                {
                    Graphics.RenderMeshIndirect(in rp, b.Mesh, s_argsBuf, 1, i);
                    calls++; camB++; camTris += tris;
                }
                if (b.Shadows == ShadowCastingMode.Off || !shVis || CascadesOn) continue;   // 1.2.208: with per-cascade shadows the light's command buffers draw the casters
                rp.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                rp.matProps = b.ShadowProps;
                Graphics.RenderMeshIndirect(in rp, b.Mesh, s_argsBuf, 1, s_batchCap + i);
                calls++; shB++; shTris += tris;
            }
            PhAcc(PhCamLoop, tLoop); s_phCamDraws += camB; s_phShadowDraws += shB;   // 1.2.240
            long tTail = PhT(); MaybeReadBack(nb);   // 1.2.208: Debug, every 30 s
            IntegrityTick(nb); PhAcc(PhDrawTicks, tTail);   // 1.2.226
            int sunOnlyDraws = shB, cascadeDraws = 0, localDraws = 0;   // 1.2.254: the draw-source split
            if (CascadesOn) { long tR = PhT(); cascadeDraws = RecordCascades(nb); shB += cascadeDraws; PhAcc(PhRecCasc, tR); tR = PhT(); localDraws = RecordLocal(nb); shB += localDraws; PhAcc(PhRecLocal, tR); }   // 1.2.209 [wishbone]: + the point / spot lights
            else { DetachCascades(); DetachLocal(); }
            if (IndirectStats.SplitOn) IndirectStats.NoteDrawSplit(camB, sunOnlyDraws, cascadeDraws, localDraws);
            long tHold = PhT(); SubmitShadowPlaceholders(s_drawCamera); PhAcc(PhDrawTicks, tHold);   // 1.2.211 ([saddles] §E): keep Unity from skipping shadow passes it thinks are empty
            // held = before the GPU cull (the GPU's own counts are never read back: no stall)
            IndirectStats.NoteView(0, camB, held, camTris);
            IndirectStats.NoteView(1, shB, held, shTris);
            IndirectStats.NoteHeld(held);
            LastCalls = calls; LastInstances = held; LastBatches = nb;
        }

        // 1.2.225: the cascade caster blocks read the shadow-pass buffer while the separate path is on
        private static void BindOther(MaterialPropertyBlock mpb, int visibleBase)
        {
            Bind(mpb, visibleBase);
            if (s_otherVisible != null) mpb.SetBuffer(EbmIndirectAssets.IdVisible, s_otherVisible);   // 1.2.225
        }

        private static void BindCaster(MaterialPropertyBlock mpb, int visibleBase)
        {
            Bind(mpb, visibleBase);
            if (s_cascSeparate && s_cascVisible != null) mpb.SetBuffer(EbmIndirectAssets.IdVisible, s_cascVisible);
        }

        private static void Bind(MaterialPropertyBlock mpb, int visibleBase)
        {
            mpb.SetBuffer(EbmIndirectAssets.IdInstances, s_instBuf);
            mpb.SetBuffer(EbmIndirectAssets.IdVisible, s_visibleBuf);
            mpb.SetInteger(EbmIndirectAssets.IdVisibleBase, visibleBase);
            IndirectLooks.Bind(mpb);   // A2
        }

        internal static string Describe()
        {
            if (!Active) return $"[Indirect] OFF ({s_off ?? "not drawn yet"})";
            var sb = new System.Text.StringBuilder(256);
            sb.Append($"[Indirect] ON: {TakenGroups} group(s) taken, {RefusedGroups} on the instanced path; {s_batches.Count} batch(es), {s_live} instance slot(s) of {s_high}, {LastCalls} indirect draw(s) a frame");
            // 1.2.210: how often the costly steps ran since the last line (the spike proof: both should sit near 0 while standing still)
            sb.Append($"; {s_layouts} full layout(s) + {s_moved} batch(es) placed alone, uploads {s_upSlots} slot(s) in {s_upRuns} run(s) ({s_upBytes / 1024} KB), {s_cascadeRecords} cascade + {s_localRecords} light-buffer re-record(s), {s_lastCascadeDraws} cascade + {s_lastLocalDraws} light caster draw(s) recorded in {s_slotCount} light buffer(s), other cameras {s_otherRenders} render(s) / {s_otherSent} draw(s)");
            sb.Append($" [keys: sparse {(SparseOn ? "on" : "off")}, stable {(StableOn ? "on" : "off")}, caster cull {(CasterCullOn ? "on" : "off")}, point faces {(FacesOn ? "on" : "off")}]");
            s_layouts = 0; s_cascadeRecords = 0; s_moved = 0; s_upSlots = 0; s_upRuns = 0; s_upBytes = 0; s_localRecords = 0; s_otherRenders = 0; s_otherSent = 0;
            if (s_refusedWhy.Count > 0) { sb.Append("; refused:"); foreach (var kv in s_refusedWhy) sb.Append(' ').Append(kv.Value).Append("x ").Append(kv.Key).Append(';'); }
            s_refusedWhy.Clear();
            return sb.ToString();
        }

        // Logout / scene change / quit / plugin destroy: GPU buffers and clones go, Active stays as the key says (the next world re-registers).
        internal static void Shutdown() { try { EbmIndirectGrass.Shutdown(); } catch { } try { ReleaseAll(); } catch (Exception e) { EasyBakeLog.Debug("[Indirect] release threw: " + e.Message); } }

        private static void ReleaseAll()
        {
            try { global::FiresEasyBakeMeshes.EasyBake.EbmShaderWarm.ResetForWorld(); } catch { }   // review 11-09: warm-up tied to the world session (freed on logout, next world warms)
            StageReleaseAll();   // [Optimize] IndirectStagedSync: jobs finished, native memory freed, staged / flip bits cleared
            DetachCascades();
            ReleaseLocal();   // 1.2.209 [wishbone]
            ReleaseReceivers();   // 1.2.209 ([saddles])
            EbmIndirectAssets.ReleaseClones();
            IndirectLooks.Release();   // A2
            s_cells.Clear(); s_hasCaster.Clear(); s_refused.Clear(); s_groups.Clear(); s_byKey.Clear(); s_visWidth = 0; s_batches.Clear(); s_free.Clear();
            Array.Clear(s_instBatch, 0, s_instBatch.Length);
            Epoch = (Epoch + 1) & 0x1FF;
            s_high = s_live = 0; s_dirtyMin = int.MaxValue; s_dirtyMax = -1; s_layoutDirty = true; s_bindingsDirty = true;
            Release(ref s_cascScratch); Release(ref s_cascVisible); Release(ref s_otherScratch); Release(ref s_otherVisible);   /* 1.2.225 */
            Release(ref s_instBuf); Release(ref s_instBatchBuf); Release(ref s_instLodBuf); Release(ref s_batchBuf); Release(ref s_visibleBuf); Release(ref s_argsBuf);
            s_visibleStride = 0;
            global::FiresEasyBakeMeshes.EasyBake.GpuReleaseFence.FlushAll();   // review 11-09 (matches [visual]'s 1257): free the fence queue at shutdown
            PresizeReset();
            s_orderCount = 0; ClearWork(); s_walkAll = true;   // 1.2.210: the record order is rebuilt with the next layout
        }

        private static void Release(ref GraphicsBuffer b) { if (b != null) { global::FiresEasyBakeMeshes.EasyBake.GpuReleaseFence.Release(b); b = null; } }   // [seasons] 11-09 E23: through the fence (immediate when its key is off)
    }
}
