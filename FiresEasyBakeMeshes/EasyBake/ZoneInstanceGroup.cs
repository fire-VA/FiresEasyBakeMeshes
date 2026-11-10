using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Every instance of one prefab inside one zone. Scoping groups per zone rather
    // than globally is what gives each draw a tight worldBounds, so Unity can cull a
    // whole zone's instances in one test; it also makes invalidation O(1) - a removed
    // piece drops its matrix instead of forcing the zone to re-combine.
    internal class ZoneInstanceGroup
    {
        // Graphics.RenderMeshInstanced draws at most this many instances per call.
        private const int MaxInstancesPerDraw = 1023;

        public bool FromCache;   // 1.2.238: built from a saved zone (ebm_batches)
        public string DropNote;   // 1.2.238: what the cache check just dropped; Sync logs the slots after it
        public int PrefabHash;
        // 1.2.137: a roof piece (name has "roof", not a "wall_roof" gable), for cameras that hide roofs
        private int _roofKnown;   // 0 unknown, 1 roof, 2 not
        internal bool IsRoof { get { if (_roofKnown == 0 && ZNetScene.instance != null) _roofKnown = EbmRoofs.IsRoofPrefab(PrefabHash) ? 1 : 2; return _roofKnown == 1; } }
        public WearLook Look;
        public InstanceDefinition Definition;
        // In the first part's space: the piece transform with that part's offset folded in.
        // Later parts are this list carried through their own offset from the first.
        public readonly List<Matrix4x4> Matrices = new List<Matrix4x4>();
        // Parallel to Matrices. Lets a removed piece drop out of the group directly
        // instead of dirtying the zone into a full rebake, which is the whole reason
        // instancing is worth having for pieces players actually tear down.
        public readonly List<MeshBaker.PieceIdentity> Identities = new List<MeshBaker.PieceIdentity>();

        private Bounds _worldBounds;
        private bool _boundsDirty;
        private List<Matrix4x4>[] _partMatrices;
        private bool _partsDirty = true;

        // 1.2.117: Core's dynamic snow per instance (SnowFeed), one property block per draw batch; rebuilt only when marked dirty
        // (a piece joins / leaves, or SnowField.ZoneChanged names this group's zone), never per frame.
        private bool _snowDirty = true;
        private bool _wetSeen;
        private int _insideGen = -1;
        private readonly List<MaterialPropertyBlock> _snowBlocks = new List<MaterialPropertyBlock>();
        private readonly List<float[]> _snowValues = new List<float[]>();
        public void MarkSnowDirty() { _snowDirty = true; Indirect.EbmIndirect.MarkGroupDirty(this); }
        internal bool IndirectWork;   // IndirectSettledWalk: on the indirect path's work list

        public int Count => Matrices.Count;

        public Bounds WorldBounds
        {
            get
            {
                if (_boundsDirty)
                {
                    _boundsDirty = false;
                    RecomputeBounds();
                }
                return _worldBounds;
            }
        }

        public bool CastsShadows => Definition == null || Definition.CastsShadows;

        public void Add(Matrix4x4 pieceLocalToWorld, MeshBaker.PieceIdentity identity)
        {
            Matrices.Add(pieceLocalToWorld * Definition.LocalOffset);
            Identities.Add(identity);
            _ver++; Indirect.EbmIndirect.MarkGroupDirty(this);   // 1.2.195
            _partsDirty = true;
            _snowDirty = true;
        }

        // A piece joining after the bake; many can land in one frame, so bounds are recomputed once at the next draw.
        public void AddLate(Matrix4x4 pieceLocalToWorld, MeshBaker.PieceIdentity identity)
        {
            Add(pieceLocalToWorld, identity);
            _boundsDirty = true;
            EbmSnowStore.GroupMembershipChanged(this);   // 1.2.206: a piece built after the bake: its zone is re-announced (throttled)
        }

        public void AddCached(Matrix4x4 instanceMatrix, MeshBaker.PieceIdentity identity)
        {
            Matrices.Add(instanceMatrix);
            Identities.Add(identity);
            _ver++; Indirect.EbmIndirect.MarkGroupDirty(this);   // 1.2.195
            _partsDirty = true;
            _snowDirty = true;
        }

        public bool TryRemove(MeshBaker.PieceIdentity identity)
        {
            int index = Identities.IndexOf(identity);
            if (index < 0) return false;

            int last = Matrices.Count - 1;
            Matrices[index] = Matrices[last];
            Identities[index] = Identities[last];
            Matrices.RemoveAt(last);
            Identities.RemoveAt(last);
            _ver++; Indirect.EbmIndirect.MarkGroupDirty(this);   // 1.2.195
            _partsDirty = true;
            _snowDirty = true;
            RecomputeBounds();
            EbmSnowStore.GroupMembershipChanged(this);   // 1.2.206: a piece removed after the bake
            return true;
        }

        // Bounds are recomputed once after the group is filled. Pieces in a base do
        // not move, so there is nothing to keep in sync per frame. Each instance's
        // mesh box is carried through its whole matrix: wood_floor's vertices sit
        // 52 m from its pivot, and a box around the pivot let Unity cull the group
        // while it was on screen.
        public void RecomputeBounds()
        {
            if (Matrices.Count == 0)
            {
                _worldBounds = new Bounds(Vector3.zero, Vector3.zero);
                return;
            }

            var local = Definition.MeshBounds;
            var e = local.extents;
            for (int i = 0; i < Matrices.Count; i++)
            {
                var m = Matrices[i];
                var centre = m.MultiplyPoint3x4(local.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == 0)
                {
                    _worldBounds = new Bounds(centre, reach * 2f);
                    continue;
                }
                _worldBounds.Encapsulate(centre + reach);
                _worldBounds.Encapsulate(centre - reach);
            }
        }

        // 1.2.114: a camera given = drawn for that camera only (a group the main view culled, for another camera that sees it).
        public void Draw(bool farTier, Camera camera = null)
        {
            int count = Matrices.Count;
            // 1.2.205: the main view drew it through the instanced path (cell runs as configured). Only the main-view draw says so: a
            // per-camera draw of a group the indirect path also takes must not flip the layout every frame (= a re-split loop)
            if (camera == null)
            {
                _indirectTaken = false;
                if (_extPendingResplit) { _extPendingResplit = false; _snowDirty = true; }   // 1.2.208: pushes that arrived while the indirect path held it
            }
            if (count == 0) return;
            // 1.2.137: while a camera hides baked roofs (the live map), a roof group draws per camera in pre-cull instead
            if (camera == null && IsRoof && InstancedDraw.RoofHidingActive) { InstancedDraw.NoteRoof(this, farTier); return; }

            var parts = Definition.Parts;
            if (parts == null || parts.Length == 0) return;
            bool split = InstancedDraw.Split;   // 1.2.186: Debug-only prep / submit split
            long t0 = split ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L, submit = 0L;
            int calls = 0, snowCalls = 0;
            // 1.2.131 (Fire 10-26 "stop the inside of pieces from looking wet"): the roofed / open split also runs while it rains, so a
            // roofed range can draw with _Wet 0 (WetOverride), with or without snow.
            bool wet = WetOverride.BakedDries;   // 1.2.188: false while Vedr owns indoor wet (VedrApi.IndoorWetActive)
            if (wet != _wetSeen) { _wetSeen = wet; _snowDirty = true; }
            bool field = SnowFeed.FieldActive;   // 1.2.162
            if (field != _fieldSeen) { _fieldSeen = field; _snowDirty = true; }
            if (wet && WetOverride.InsideGeneration != _insideGen) { _insideGen = WetOverride.InsideGeneration; _snowDirty = true; }
            bool snow = SnowFeed.Active || wet || ExtCount > 0;   // 1.2.206: pushed values need the per-instance ranges too
            float cell = SnowCellSize;   // 1.2.195; 1.2.205: 0 while the indirect path holds it (no mismatch = no per-frame re-split)
            if (snow && cell != _snowCell && _sIdx < 0) _snowDirty = true;   // the key changed: re-split once in the new layout
            if (snow) EnsureSnow(count);   // first: it may reorder Matrices (roofed instances first), which re-derives the part lists
            else if (cell > 0f) EnsurePlainCells(count, cell);
            bool snowCells = snow && cell > 0f && _snowCell == cell && _snowBounds.Count == _snowRanges.Count;
            bool plainCells = !snow && cell > 0f && _plainVer == _ver && _plainRanges.Count > 0;
            EnsurePartMatrices(parts);
            var bounds = WorldBounds;
            var view = RunView.For(camera);   // 1.2.196: the camera the per-run LOD is chosen for, once per group draw
            // 1.2.186 toggle [Optimize] InstancedFollowChecksEveryFrame (default true = as before): false asks FollowRenderer / FollowSource
            // once a second per group instead of every frame per part (both already act at most once a second; this saves the lookups)
            bool followNow = true;
            if (!FiresEasyBakeMeshesPlugin.InstancedFollowChecksEveryFrame.Value)
            {
                float rt = Time.realtimeSinceStartup;
                followNow = rt >= _nextFollow;
                if (followNow) _nextFollow = rt + 1f;
            }
            // 1.2.200: the main camera's draw uses this group's compacted visible list when it is current (InstanceCull)
            bool useCull = camera == null && cell <= 0f && InstanceCull.Enabled && PrepareCull(parts, snow, count);

            for (int p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                if (followNow)
                {
                    part.FollowRenderer();   // 1.2.155: a new material on the prefab renderer
                    InstanceDefinition.FollowSource(part.RenderParams.material);
                }
                var mesh = farTier ? part.FarMesh : part.NearMesh;
                if (mesh == null) continue;

                var matrices = p == 0 ? Matrices : _partMatrices[p];
                var renderParams = part.RenderParams;
                renderParams.worldBounds = bounds;
                if (camera != null) renderParams.camera = camera;
                // 1.2.149 (Fire's shadow option C): far or small baked pieces draw without casting
                if (renderParams.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On && InstancedDraw.NoShadowFor(bounds, mesh))
                    renderParams.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // 1.2.194 (lead 11-08, Fire's town at 147 M tris): Debug-only, the tris EBM submits (near / far mesh) and how many of them also cast shadows
                // 1.2.196: cell runs note their own tris per run (their mesh is chosen per run)
                // 1.2.199: with InstancedShadowProxies or InstancedCellLod on, every draw goes through DrawRun (cells or not; group bounds without cells)
                bool viaRun = view.Proxies || view.Lod;
                if (useCull)   // 1.2.200: the compacted visible instances, same ranges logic, no extra call
                {
                    long c0 = split ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    var cm = p == 0 ? _cM : _cPart[p];
                    for (int r = 0; r < _cRanges.Count; r++)
                    {
                        var (cs, cn) = _cRanges[r];
                        renderParams.matProps = _cSnow ? _cBlocks[r] : null;
                        if (viaRun) { DrawRun(ref calls, part, mesh, renderParams, cm, cs, cn, bounds, view, split, farTier); continue; }
                        Graphics.RenderMeshInstanced(in renderParams, mesh, part.SubMesh, cm, cn, cs);
                        calls++;
                        if (split) InstancedDraw.NoteTris((long)(mesh.GetIndexCount(part.SubMesh) / 3) * cn, farTier, renderParams.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);
                    }
                    if (split) submit += System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                    continue;
                }
                bool runs = viaRun || (snow && SnowRangesUsable(count) && snowCells) || (!snow && plainCells);
                if (split && camera == null && !runs) InstancedDraw.NoteTris((long)(mesh.GetIndexCount(part.SubMesh) / 3) * count, farTier, renderParams.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off);

                if (snow && SnowRangesUsable(count))   // 1.2.146: while a pass is still running on a changed count, draw plainly below
                {
                    // one draw per snow range: roofed and open instances never share a batch (1.2.119)
                    long s0 = split ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                    int before = calls;
                    for (int r = 0; r < _snowRanges.Count; r++)
                    {
                        renderParams.matProps = _snowBlocks[r];
                        if (snowCells || viaRun) { DrawRun(ref calls, part, mesh, renderParams, matrices, _snowRanges[r].start, _snowRanges[r].n, snowCells ? _snowBounds[r] : bounds, view, split && camera == null, farTier); continue; }   // 1.2.195/196/199
                        Graphics.RenderMeshInstanced(in renderParams, mesh, part.SubMesh, matrices, _snowRanges[r].n, _snowRanges[r].start);
                        calls++;
                    }
                    if (split) { submit += System.Diagnostics.Stopwatch.GetTimestamp() - s0; snowCalls += calls - before; }
                    continue;
                }
                long b0 = split ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                if (plainCells)   // 1.2.195: one draw per cell run, each with its own bounds
                {
                    for (int r = 0; r < _plainRanges.Count; r++)
                        DrawRun(ref calls, part, mesh, renderParams, matrices, _plainRanges[r].start, _plainRanges[r].n, _plainBounds[r], view, split && camera == null, farTier);
                    if (split) submit += System.Diagnostics.Stopwatch.GetTimestamp() - b0;
                    continue;
                }
                for (int start = 0; start < count; start += MaxInstancesPerDraw)
                {
                    int batchCount = Mathf.Min(MaxInstancesPerDraw, count - start);
                    if (viaRun) { DrawRun(ref calls, part, mesh, renderParams, matrices, start, batchCount, bounds, view, split && camera == null, farTier); continue; }   // 1.2.199
                    Graphics.RenderMeshInstanced(in renderParams, mesh, part.SubMesh, matrices, batchCount, start);
                    calls++;
                }
                if (split) submit += System.Diagnostics.Stopwatch.GetTimestamp() - b0;
            }
            if (split) InstancedDraw.NoteGroupDraw(System.Diagnostics.Stopwatch.GetTimestamp() - t0, submit, calls, snowCalls, count * parts.Length, camera != null);
        }

        private float _nextFollow;   // 1.2.186

        // ── 1.2.200 InstanceCull: the compacted visible lists of this group (main camera) ──
        private float[] _instVals; private int _instCovered, _snowGen;
        private Vector4[] _spheres; private int _spheresVer = -1;
        private InstanceCull.Job _cullJob; private bool _cullInFlight;
        private int[] _visIdx = new int[0]; private int _visCount = -1, _visVer = -1, _visSnowGen = -1; private bool _visSnow;
        private Vector3 _visCamPos, _visCamFwd;
        private readonly List<Matrix4x4> _cM = new List<Matrix4x4>();
        private List<Matrix4x4>[] _cPart;
        private readonly List<(int start, int n)> _cRanges = new List<(int start, int n)>();
        private readonly List<MaterialPropertyBlock> _cBlocks = new List<MaterialPropertyBlock>();
        private readonly List<float[]> _cVals = new List<float[]>();
        private bool _cSnow;

        // Main thread, once per main-camera draw: take a finished job, rebuild the compacted lists if the visible set changed, post the next
        // job. True = draw the compacted lists this frame.
        private bool PrepareCull(InstancePart[] parts, bool snow, int count)
        {
            if (!InstanceCull.View(out var camPos, out var camFwd)) return false;
            if (snow && !SnowRangesUsable(count)) return false;   // a snow pass is still running: values not final, draw full
            if (_spheresVer != _ver) BuildSpheres(count);
            if (_cullInFlight && _cullJob.Done)
            {
                _cullInFlight = false;
                var job = _cullJob;
                if (job.Ver == _ver && job.OutCount >= 0)
                {
                    bool changed = job.OutCount != _visCount || _visVer != _ver || _visSnowGen != _snowGen || _visSnow != snow;
                    if (!changed) for (int i = 0; i < job.OutCount; i++) if (job.Out[i] != _visIdx[i]) { changed = true; break; }
                    if (changed) { RebuildCompacted(parts, job.Out, job.OutCount, snow); InstancedDraw.NoteCullRebuild(); }
                    _visCamPos = job.CamPos; _visCamFwd = job.CamFwd;
                }
            }
            if (!_cullInFlight)
            {
                if (_cullJob == null) _cullJob = new InstanceCull.Job { Group = this };
                _cullJob.Spheres = _spheres; _cullJob.Count = count; _cullJob.Ver = _ver;
                InstanceCull.Post(_cullJob);
                _cullInFlight = true;
            }
            if (_visVer != _ver || _visSnowGen != _snowGen || _visSnow != snow || _visCount < 0) return false;
            // the list is a frame old: a sharp turn or a jump since then draws everything this frame
            if (Vector3.Angle(camFwd, _visCamFwd) > InstanceCull.MaxTurnDeg || (camPos - _visCamPos).sqrMagnitude > InstanceCull.MaxJumpM * InstanceCull.MaxJumpM) return false;
            InstancedDraw.NoteCull(count * parts.Length, _visCount * parts.Length);
            return true;
        }

        private void BuildSpheres(int count)
        {
            // a NEW array per change: the worker may still be reading the old one (changes are rare; nothing per frame)
            var s = new Vector4[Mathf.Max(count, 1)];
            var local = Definition.MeshBounds;
            var e = local.extents;
            for (int i = 0; i < count; i++)
            {
                var m = Matrices[i];
                var c = m.MultiplyPoint3x4(local.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                s[i] = new Vector4(c.x, c.y, c.z, reach.magnitude);
            }
            _spheres = s; _spheresVer = _ver;
        }

        private void RebuildCompacted(InstancePart[] parts, int[] idx, int n, bool snow)
        {
            if (_visIdx.Length < n) _visIdx = new int[Mathf.NextPowerOfTwo(Mathf.Max(n, 8))];
            System.Array.Copy(idx, _visIdx, n);
            _visCount = n; _visVer = _ver; _visSnowGen = _snowGen; _visSnow = snow;
            _cM.Clear();
            for (int i = 0; i < n; i++) _cM.Add(Matrices[idx[i]]);
            if (parts.Length > 1)
            {
                if (_cPart == null || _cPart.Length != parts.Length) _cPart = new List<Matrix4x4>[parts.Length];
                for (int p = 1; p < parts.Length; p++)
                {
                    var src = _partMatrices[p];
                    var dst = _cPart[p] ?? (_cPart[p] = new List<Matrix4x4>(n));
                    dst.Clear();
                    for (int i = 0; i < n; i++) dst.Add(src[idx[i]]);
                }
            }
            // ranges: the roofed section first (visible indices keep the group's roofed-first order), each cut at 1023
            int cCovered = 0;
            if (snow) while (cCovered < n && idx[cCovered] < _instCovered) cCovered++;
            _cRanges.Clear();
            for (int s0 = 0; s0 < cCovered; s0 += MaxInstancesPerDraw) _cRanges.Add((s0, Mathf.Min(MaxInstancesPerDraw, cCovered - s0)));
            for (int s0 = cCovered; s0 < n; s0 += MaxInstancesPerDraw) _cRanges.Add((s0, Mathf.Min(MaxInstancesPerDraw, n - s0)));
            _cSnow = snow;
            if (!snow) return;
            for (int r = 0; r < _cRanges.Count; r++)
            {
                var (start, cnt) = _cRanges[r];
                int size = SizeClass(cnt);
                if (r >= _cBlocks.Count) { _cBlocks.Add(new MaterialPropertyBlock()); _cVals.Add(new float[size]); }
                else if (_cVals[r].Length != size) { _cBlocks[r] = new MaterialPropertyBlock(); _cVals[r] = new float[size]; }
                var values = _cVals[r];
                float sum = 0f;
                for (int i = 0; i < cnt; i++) { values[i] = _instVals[idx[start + i]]; sum += values[i]; }
                for (int i = cnt; i < values.Length; i++) values[i] = 0f;
                var block = _cBlocks[r];
                block.Clear();
                block.SetFloatArray(SnowFeed.PropertyId, values);
                block.SetFloat(SnowFeed.PropertyId, cnt > 0 ? sum / cnt : 0f);
                block.SetFloatArray(SnowFeed.VedrPropertyId, values);
                block.SetFloat(SnowFeed.VedrPropertyId, cnt > 0 ? sum / cnt : 0f);
                if (_wetSeen && start < cCovered) block.SetFloat(WetOverride.WetId, 0f);
            }
        }

        // ── 1.2.196 (DESIGN_OcclusionLOD 2b + 2c), both need cell runs (InstancedCellSizeMeters > 0) ──
        // [Optimize] InstancedCellLod: each run draws the LOD vanilla's LODGroup would pick at the run's nearest point (and nothing past
        // vanilla's cull height). [Optimize] InstancedShadowProxies: the run's main draw casts no shadow, and a ShadowsOnly draw of one LOD
        // lower (the last level when there is no lower) casts it. No allocation: RenderParams is a struct, the view is computed once per group.
        internal struct RunView
        {
            public bool Lod, Proxies; public Vector3 Pos; public float HalfFovTan;
            internal static RunView For(Camera camera)
            {
                var v = new RunView
                {
                    Lod = FiresEasyBakeMeshesPlugin.InstancedCellLod != null && FiresEasyBakeMeshesPlugin.InstancedCellLod.Value,
                    Proxies = FiresEasyBakeMeshesPlugin.InstancedShadowProxies != null && FiresEasyBakeMeshesPlugin.InstancedShadowProxies.Value,
                };
                if (!v.Lod && !v.Proxies) return v;
                var cam = camera != null ? camera : Camera.main;
                if (cam == null) { v.Lod = false; return v; }
                v.Pos = cam.transform.position;
                v.HalfFovTan = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                return v;
            }
        }

        private static void DrawRun(ref int calls, InstancePart part, Mesh mesh, RenderParams rp, List<Matrix4x4> matrices, int start, int n,
            Bounds runBounds, RunView view, bool note, bool farTier)
        {
            rp.worldBounds = runBounds;
            var lods = part.LodMeshes;
            int level = 0;
            if (view.Lod && lods != null && lods.Length > 0)
            {
                float d = Mathf.Sqrt(runBounds.SqrDistance(view.Pos));
                level = part.LodFor(d, 1f, view.HalfFovTan);
                if (level < 0) { if (note) InstancedDraw.NoteLod(-1, n); return; }   // vanilla culls the piece here
                mesh = lods[level];
                if (note) InstancedDraw.NoteLod(level, n);
            }
            bool casts = rp.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
            long tris = note ? (long)(mesh.GetIndexCount(part.SubMesh) / 3) * n : 0L;
            // 1.2.199: the proxy is one LOD below what this run draws (the zone far tier draws LOD1 = level 1), and only when such a lower
            // mesh EXISTS: a part with no lower LOD keeps casting from its own draw, so it costs no extra call
            Mesh shadowMesh = null;
            if (view.Proxies && casts && rp.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On && lods != null && lods.Length > 1)
            {
                int drawn = view.Lod ? level : (farTier && mesh != lods[0] ? 1 : 0);
                int below = Mathf.Min(drawn + 1, lods.Length - 1);
                if (lods[below] != mesh) shadowMesh = lods[below];
            }
            if (shadowMesh != null)
            {
                rp.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                Graphics.RenderMeshInstanced(in rp, mesh, part.SubMesh, matrices, n, start);
                rp.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                Graphics.RenderMeshInstanced(in rp, shadowMesh, part.SubMesh, matrices, n, start);
                calls += 2;
                if (note) { InstancedDraw.NoteTris(tris, view.Lod ? level > 0 : farTier, false); InstancedDraw.NoteShadowProxyTris((long)(shadowMesh.GetIndexCount(part.SubMesh) / 3) * n); }
                return;
            }
            Graphics.RenderMeshInstanced(in rp, mesh, part.SubMesh, matrices, n, start);
            calls++;
            if (note) InstancedDraw.NoteTris(tris, view.Lod ? level > 0 : farTier, casts);   // 1.2.198: the zone far tier counted as far again (1.2.196 counted it as near)
        }

        // Each instance's _FiresSnowBiome = SnowField.CoverageAt(its position), written as the batch's per-instance array (a shader that
        // declares the property per instance reads its own value); the batch average also goes in as a plain float (a shader that
        // declares it as an ordinary uniform still gets the right snow for the area). Fixed 1023-slot arrays: Unity sizes an MPB array once.
        // 1.2.119 (Fire 10-24: "these are baked pieces so it's EBM's job for roofs to keep their shelter/roof tags"; snow on the floor inside
        // his roofed house): an instance under a roof gets 0, by vanilla's own test (Cover.IsUnderRoof, as Smelter.cs:316 uses it) from just
        // above the piece. The building's colliders are untouched by baking, so that test answers exactly as for live pieces (his Shelter
        // icon showed it working). Roofed instances are moved to the front of the group and drawn in their own batches, so even with a
        // shader that reads _FiresSnowBiome as a plain uniform (FTS 0.3.199) a roofed batch is 0 instead of the open pieces' average.
        private bool _fieldSeen;   // 1.2.162
        private readonly List<(int start, int n)> _snowRanges = new List<(int start, int n)>();

        private static float s_nextSnowLog;

        // 1.2.144: re-split cost per frame. A Core snow change dirties every group of a zone and each re-splits on its next Draw, so
        // they can stack into one frame. The frame's total is flushed on the next frame that re-splits (or by the 30 s summary).
        private static int s_rsFrame = -1, s_rsGroups, s_rsInstances, s_rsSlowFrames;
        private static long s_rsTicks, s_rsWorstTicks;
        private const double ResplitSlowMs = 20.0;

        // 1.2.158 ([visual], [release]: what is inside a 104.7 ms frame): the re-split's time split three ways - Core's CoverageAt, the
        // exposure test (ExposureCache -> FTS SnowExposureAt / the roof raycast) and the finish (reorder + ranges + property blocks);
        // the rest is EBM's own loop. Kept per frame and for the worst frame of the 30 s window.
        private static long s_frCoverTicks, s_frExposureTicks, s_frFinishTicks;
        private static long s_worstCoverTicks, s_worstExposureTicks, s_worstFinishTicks;

        private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static string ResplitSplit(long total, long cover, long exposure, long finish)
            => $"Core coverage {Ms(cover):F1} ms, exposure test (FTS / roof ray) {Ms(exposure):F1} ms, finish {Ms(finish):F1} ms, EBM loop {Ms(System.Math.Max(0, total - cover - exposure - finish)):F1} ms";

        private static void NoteResplit(long ticks, int instances)
        {
            int frame = Time.frameCount;
            if (frame != s_rsFrame) { FlushResplitFrame(); s_rsFrame = frame; }
            s_rsTicks += ticks; s_rsGroups++; s_rsInstances += instances;
        }

        private static void FlushResplitFrame()
        {
            if (s_rsGroups == 0) return;
            double ms = s_rsTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (s_rsTicks > s_rsWorstTicks)
            {
                s_rsWorstTicks = s_rsTicks;
                s_worstCoverTicks = s_frCoverTicks; s_worstExposureTicks = s_frExposureTicks; s_worstFinishTicks = s_frFinishTicks;
            }
            if (ms >= ResplitSlowMs)
            {
                s_rsSlowFrames++;
                EasyBakeLog.Debug($"[SnowBake] frame {s_rsFrame}: EBM re-split (incl. Core/FTS calls) {ms:F1} ms over {s_rsGroups} group(s), {s_rsInstances} instance(s): {ResplitSplit(s_rsTicks, s_frCoverTicks, s_frExposureTicks, s_frFinishTicks)} (snow {(SnowFeed.Active ? "on" : "off")}, rain {(WetOverride.Raining ? "on" : "off")})");
            }
            s_rsTicks = 0; s_rsGroups = 0; s_rsInstances = 0;
            s_frCoverTicks = s_frExposureTicks = s_frFinishTicks = 0;
        }

        // 1.2.146 ([visual], lead 10-28, dense58 "[SnowBake] frame 4933: re-split 161.5 ms over 25 group(s)" in the settled world): the
        // re-split is sliced to a per-frame budget SHARED by every group (Batching / SnowResplitBudgetMs, default 2 ms; 0 = no slicing).
        // The budget is checked every SliceStep instances INSIDE a group (the 10-28 lesson: a budget between heavy steps never holds when one
        // step alone is over it), a pass resumes where it stopped on the group's next Draw, and until it finishes the group keeps drawing
        // with its last finished split (or plainly when it has none / its instance count changed since).
        // 1.2.147 ([visual], lead dense65 "worst 41.7 ms"): the log's slow frames were 23 groups x ~14 instances (313 instances, ~0.13 ms each),
        // not one heavy step: every group took its forced first 16 instances AFTER earlier groups had spent the shared budget. Now a group
        // waits for the next frame when the budget is already spent, only the frame's first re-split is guaranteed progress, and the budget
        // is checked every 4 instances.
        private const int SliceStep = 4;
        private static int s_budgetFrame = -1;
        private static long s_budgetUsedTicks;
        private static int s_slicedGroups, s_slicedCalls, s_deferredCalls;   // 1.2.147: deferred = draws that waited for a later frame
        private int _sIdx = -1, _sCount, _sCovered, _sCalls;
        private float[] _sVal;
        private bool[] _sRoofed;
        private int _snowRangesCount = -1;            // the instance count the current _snowRanges were built for

        /// <summary>True while the snow ranges match the current instances (else Draw uses the plain batches).</summary>
        private bool SnowRangesUsable(int count) => _snowRanges.Count > 0 && _snowRangesCount == count;

        private static long BudgetTicks()
        {
            var cfg = FiresEasyBakeMeshesPlugin.SnowResplitBudgetMs;
            float ms = cfg != null ? cfg.Value : 2f;
            return ms <= 0f ? long.MaxValue : (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        }

        private void EnsureSnow(int count)
        {
            if (!_snowDirty && _sIdx < 0 && _snowRanges.Count > 0) return;
            if (_snowDirty || _sIdx < 0 || _sCount != count)
            {
                // (re)start a pass: a new dirty mark or a changed instance count restarts it from the first instance
                _snowDirty = false;
                _sIdx = 0; _sCount = count; _sCovered = 0; _sCalls = 0;
                if (_sVal == null || _sVal.Length != count) { _sVal = new float[count]; _sRoofed = new bool[count]; }
            }
            long rsStart = System.Diagnostics.Stopwatch.GetTimestamp();   // 1.2.144: re-split step timer (FlushResplitFrame)
            // 1.2.159 (launch-1 log: "worst frame 4.6 ms (Core coverage 1.8, exposure 3.4 ...)" summed past its total): close the previous
            // frame BEFORE this frame's coverage / exposure / finish ticks are added, so they land on the frame they ran in
            if (Time.frameCount != s_rsFrame) { FlushResplitFrame(); s_rsFrame = Time.frameCount; }
            int frame = Time.frameCount;
            if (frame != s_budgetFrame) { s_budgetFrame = frame; s_budgetUsedTicks = 0; }
            long budget = BudgetTicks();
            // 1.2.147: this frame's budget is already spent by other groups -> no work here; the group keeps its last split (or draws plain)
            if (budget != long.MaxValue && s_budgetUsedTicks >= budget) { s_deferredCalls++; return; }
            bool firstThisFrame = s_budgetUsedTicks == 0;
            _sCalls++;

            // value per instance, roofed first (stable): Matrices and Identities are reordered together (only IndexOf reads them)
            // 1.2.120 ([seasons]): exposure from FTS's own test (SnowFeed.ExposureAt), asked at the TOP of each instance's bounds: a pivot is
            // usually the piece's base, so a point just above it is still inside a roof piece and roofs lost their snow
            var value = _sVal;
            var roofed = _sRoofed;
            int covered = _sCovered;
            var local = Definition.MeshBounds;
            var e = local.extents;
            int startIdx = _sIdx;
            for (int i = _sIdx; i < count; i++)
            {
                // 1.2.146: out of this frame's budget -> stop here, resume next frame. 1.2.147: only the frame's first re-split is
                // guaranteed SliceStep instances (so every pass still ends); any later group may stop before its first instance
                if ((i - startIdx) % SliceStep == 0 && (!firstThisFrame || i > startIdx)
                    && s_budgetUsedTicks + (System.Diagnostics.Stopwatch.GetTimestamp() - rsStart) > budget)
                {
                    _sIdx = i; _sCovered = covered;
                    long spent = System.Diagnostics.Stopwatch.GetTimestamp() - rsStart;
                    s_budgetUsedTicks += spent;
                    NoteResplit(spent, i - startIdx);
                    return;
                }
                // 1.2.206 EbmApi.SetPieceSnow: the snow owner's pushed value wins (no coverage / roof test for it). wetScale 0 = the dry range.
                if (ExtCount > 0 && EbmSnowStore.TryGet(Identities[i], out var ext))
                {
                    value[i] = ext.x;
                    roofed[i] = ext.y <= 0f;
                    if (roofed[i]) covered++;
                    continue;
                }
                // 1.2.260 WaitForPushedSnow: a just-announced piece with no per-piece answer yet draws plain (not the area amount)
                if (EbmSnowStore.HoldPlain(Identities[i])) { value[i] = 0f; roofed[i] = false; continue; }
                var m = Matrices[i];
                var pos = new Vector3(m.m03, m.m13, m.m23);
                Vector3 centre = m.MultiplyPoint3x4(local.center);
                // 1.2.121 ([visual], lead 10-24 halo ring): the top of the bounds along the LOCAL up (Core GravityUp; world up off a ring),
                // not along world y: on the ring's wall world y runs along the floor, so the test point sat beside the piece, not above it
                Vector3 up = SnowFeed.UpAt(centre);
                float reach = Mathf.Abs(Vector3.Dot(up, m.GetColumn(0))) * e.x + Mathf.Abs(Vector3.Dot(up, m.GetColumn(1))) * e.y + Mathf.Abs(Vector3.Dot(up, m.GetColumn(2))) * e.z;
                // 1.2.124 ([seasons] SNOW_PERF_DIAG 10-26: one roof raycast per instance on EVERY snow change, 460 for one wall
                // group): coverage first, and no roof test at all where there is no snow; the exposure of a spot is cached
                // (ExposureCache, 30 s), so a weather / season step re-reads it instead of re-casting.
                long tc = System.Diagnostics.Stopwatch.GetTimestamp();   // 1.2.158 split
                float coverage = SnowFeed.CoverageAt(pos);
                long te = System.Diagnostics.Stopwatch.GetTimestamp();
                s_frCoverTicks += te - tc;
                if (coverage <= 0.001f && !_wetSeen) { value[i] = 0f; roofed[i] = false; continue; }
                // 1.2.162 (Fire 11-04: "many baked meshes and va pieces meshes do not get snow still"): with FTS's world field on, the shader shelters per pixel
                // and a live piece's value is the area cover alone (FiresPieceSnowDriver), so a baked one gets the same and neighbours match. The per-piece
                // test here read a neighbour's stand-in as a roof and zeroed the piece. It still runs in rain, for the dry range.
                float exposure = _fieldSeen && !_wetSeen ? 1f : ExposureCache.Get(centre + up * reach, up);
                s_frExposureTicks += System.Diagnostics.Stopwatch.GetTimestamp() - te;
                roofed[i] = exposure <= 0.001f;
                value[i] = _fieldSeen ? coverage : roofed[i] ? 0f : coverage * exposure;
                // 1.2.132: the player is inside and this piece is over their head: its underside is what they see, so it draws dry too
                // (its snow value stays; only the range's _Wet changes)
                if (!roofed[i] && _wetSeen && WetOverride.Overhead(centre - up * reach)) roofed[i] = true;
                if (roofed[i]) covered++;
            }
            long tFinish = System.Diagnostics.Stopwatch.GetTimestamp();   // 1.2.158 split: the finish step from here
            _sIdx = -1;                                                  // 1.2.146: this pass is complete
            if (_snowDirtyAgain) { _snowDirtyAgain = false; _snowDirty = true; }   // 1.2.208: values pushed during the pass: one more pass, after this one
            if (_sCalls > 1) { s_slicedGroups++; s_slicedCalls += _sCalls; }
            if (_wetSeen) WetOverride.NoteBaked(covered, count - covered);
            // 1.2.205 ([generator]): already roofed-first (the last pass's order still holds) = no reorder, no Version bump, no re-Sync
            bool partitioned = true;
            for (int i = covered; i < count && partitioned; i++) if (roofed[i]) partitioned = false;
            if (covered > 0 && covered < count && !partitioned)
            {
                var m2 = new List<Matrix4x4>(count); var id2 = new List<MeshBaker.PieceIdentity>(count); var v2 = new float[count];
                int k = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int i = 0; i < count; i++)
                    {
                        if (roofed[i] != (pass == 0)) continue;
                        m2.Add(Matrices[i]); id2.Add(Identities[i]); v2[k++] = value[i];
                    }
                Matrices.Clear(); Matrices.AddRange(m2);
                Identities.Clear(); Identities.AddRange(id2);
                value = v2;
                _partsDirty = true;
                _ver++; Indirect.EbmIndirect.MarkGroupDirty(this);   // 1.2.195: the plain cell runs were built for the old order
            }

            // ranges: [0, covered) and [covered, count), each cut at 1023; 1.2.195: with a cell size, also cut where the cell changes,
            // after ordering each section by cell (values move with their instances), and every range gets its own bounds
            float cell = SnowCellSize;   // 1.2.205: 0 for a group on the indirect path (no cell runs, no re-sort, no Version bump)
            _snowRanges.Clear(); _snowBounds.Clear();
            if (cell > 0f)
            {
                SortByCell(0, covered, value, cell);
                SortByCell(covered, count, value, cell);
                AddCellRanges(0, covered, cell, _snowRanges, _snowBounds);
                AddCellRanges(covered, count, cell, _snowRanges, _snowBounds);
                _partsDirty = true; _ver++;
            }
            else
            {
                for (int start = 0; start < covered; start += MaxInstancesPerDraw) _snowRanges.Add((start, Mathf.Min(MaxInstancesPerDraw, covered - start)));
                for (int start = covered; start < count; start += MaxInstancesPerDraw) _snowRanges.Add((start, Mathf.Min(MaxInstancesPerDraw, count - start)));
            }
            _snowCell = cell;
            _snowRangesCount = count;
            // 1.2.200: the per-instance values in the final order, for the compacted (culled) lists
            if (_instVals == null || _instVals.Length < count) _instVals = new float[Mathf.NextPowerOfTwo(Mathf.Max(count, 8))];
            System.Array.Copy(value, _instVals, count);
            _instCovered = covered; _snowGen++;
            // 1.2.198 (lead / [classes-ui] cell-16 leg: submit 4.7 -> 19.4 ms a frame, GPU 29 -> 53 ms, GC 3 per 5 s): every range had its own block
            // with two 1023-float arrays, uploaded whole on every draw; cell runs hold a few instances each, so thousands of near-empty 8 KB arrays
            // went up per frame and every new range allocated 4 KB. Now a range's array is sized to its run (power of two, 8..1023): a block is
            // replaced only when its run's size class changes, so a stable layout allocates nothing and uploads about what it draws.
            for (int r = 0; r < _snowRanges.Count; r++)
            {
                var (start, n) = _snowRanges[r];
                int size = SizeClass(n);
                if (r >= _snowBlocks.Count) { _snowBlocks.Add(new MaterialPropertyBlock()); _snowValues.Add(new float[size]); }
                else if (_snowValues[r].Length != size) { _snowBlocks[r] = new MaterialPropertyBlock(); _snowValues[r] = new float[size]; }   // an MPB array keeps its first size
                var values = _snowValues[r];
                float sum = 0f;
                for (int i = 0; i < n; i++) { values[i] = value[start + i]; sum += values[i]; }
                for (int i = n; i < values.Length; i++) values[i] = 0f;
                var block = _snowBlocks[r];
                block.Clear();
                block.SetFloatArray(SnowFeed.PropertyId, values);
                block.SetFloat(SnowFeed.PropertyId, n > 0 ? sum / n : 0f);
                // 1.2.181: Vedr/Piece reads _VedrSnowCover (an instanced prop, same layout as FiresPiece's _FiresSnowBiome); both are written, so a batch
                // draws right on whichever shader its material carries (a material not yet swapped, or a pack without Vedr).
                block.SetFloatArray(SnowFeed.VedrPropertyId, values);
                block.SetFloat(SnowFeed.VedrPropertyId, n > 0 ? sum / n : 0f);
                if (_wetSeen && start < covered) block.SetFloat(WetOverride.WetId, 0f);   // 1.2.131: a roofed range draws dry
            }
            // 1.2.144 ([visual], lead 10-28: [ghost]'s spike table has 170-308 ms settled stalls right after a Core snow step + a re-split,
            // a timing link only): this re-split's cost goes into the frame's total; a frame over 20 ms gets one Debug line
            long doneAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long spentDone = doneAt - rsStart;
            s_frFinishTicks += doneAt - tFinish;   // 1.2.158
            s_budgetUsedTicks += spentDone;
            NoteResplit(spentDone, count - startIdx);
            // 1.2.133 ([visual], overnight #9 log census: ~1000 per-group lines in 20 min): one summary every 30 s; the per-group
            // shader line only with [Batching] VerboseLogging on.
            s_splits++; s_splitInstances += count; s_splitRoofed += covered;
            if (Time.time >= s_nextSplitSummary)
            {
                s_nextSplitSummary = Time.time + 30f;
                EasyBakeLog.Debug($"[SnowBake] last 30 s: {s_splits} group re-split(s), {s_splitInstances} instance(s), {s_splitRoofed} roofed/dry{(WetOverride.Raining ? " (raining)" : "")}; worst frame of EBM re-split (incl. Core/FTS calls) {s_rsWorstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms ({ResplitSplit(s_rsWorstTicks, s_worstCoverTicks, s_worstExposureTicks, s_worstFinishTicks)}), {s_rsSlowFrames} frame(s) over {ResplitSlowMs:0} ms; {ZoneTracker.SnowAreaSummary()}; per-group lines: [Batching] VerboseLogging");
                if (s_slicedGroups > 0)
                {
                    float budgetMs = FiresEasyBakeMeshesPlugin.SnowResplitBudgetMs != null ? FiresEasyBakeMeshesPlugin.SnowResplitBudgetMs.Value : 2f;
                    EasyBakeLog.Debug($"[SnowBake] re-split sliced: {s_slicedGroups} group(s) over {s_slicedCalls} frame(s), {s_deferredCalls} deferred draw(s), worst {s_rsWorstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ms (budget {budgetMs:0.#} ms a frame)");
                }
                s_slicedGroups = s_slicedCalls = s_deferredCalls = 0;
                s_splits = s_splitInstances = s_splitRoofed = 0;
                s_rsWorstTicks = 0; s_rsSlowFrames = 0;
                s_worstCoverTicks = s_worstExposureTicks = s_worstFinishTicks = 0;   // 1.2.158
            }
            if (FiresEasyBakeMeshesPlugin.BatchingVerbose != null && FiresEasyBakeMeshesPlugin.BatchingVerbose.Value && Time.time >= _nextSnowDebug)
            {
                // 1.2.125 ([seasons] 10-26): what the baked pass draws, to compare with fts_snow_probe on the live piece
                _nextSnowDebug = Time.time + 10f;
                var sc = ZNetScene.instance;
                var pf = sc != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(PrefabHash) : null;
                float mean = 0f;
                for (int i = 0; i < count; i++) mean += value[i];
                var sb = new System.Text.StringBuilder();
                foreach (var part in Definition.Parts)
                {
                    var mat = part.RenderParams.material;
                    var sh = mat != null ? mat.shader : null;
                    sb.Append($" '{(mat != null ? mat.name : "null")}'={(sh != null ? sh.name : "null")}#{(sh != null ? sh.GetInstanceID() : 0)}");
                }
                EasyBakeLog.Debug($"[SnowBake] '{(pf != null ? pf.name : PrefabHash.ToString())}' {count} instance(s), mean _FiresSnowBiome {(count > 0 ? mean / count : 0f):0.00}, {covered} roofed; shaders:{sb}");
            }
            if (Time.time >= s_nextSnowLog && FiresEasyBakeMeshesPlugin.BatchingVerbose != null)
            {
                s_nextSnowLog = Time.time + 10f;
                var scene = ZNetScene.instance;
                var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(PrefabHash) : null;
                EasyBakeLog.Debug($"[Snow] '{(prefab != null ? prefab.name : PrefabHash.ToString())}' group: {covered} of {count} instance(s) under a roof (snow 0), {count - covered} open; {_snowRanges.Count} draw(s)");
            }
        }

        // Parts after the first draw the same pieces from a fixed offset, so their matrix lists
        // are derived once per change rather than rebuilt per frame.
        private float _nextSnowDebug;
        private static int s_splits, s_splitInstances, s_splitRoofed;
        private static float s_nextSplitSummary;

        // ── 1.2.195 (TOWN50: a group's one zone-sized bounds sent every instance to the main view and to every sun-shadow cascade) ──
        // [Optimize] InstancedCellSizeMeters > 0: instances are ordered by an x/z cell and each draw covers one cell's run with its own
        // bounds, so Unity's per-camera and per-cascade culling skips the cells it cannot see. Built only when the instances or the key
        // change (never per frame); the order, ranges and bounds lists are reused, so a rebuild allocates nothing once warm.
        internal static float CellSize { get { var c = FiresEasyBakeMeshesPlugin.InstancedCellSizeMeters; return c != null ? c.Value : 0f; } }
        private float _snowCell = -1f;
        private readonly List<Bounds> _snowBounds = new List<Bounds>();
        private readonly List<(int start, int n)> _plainRanges = new List<(int start, int n)>();
        private readonly List<Bounds> _plainBounds = new List<Bounds>();
        private int _ver, _plainVer = -1;
        private float _plainCell = -1f;
        private long[] _cellKeys; private int[] _cellOrder;
        private readonly List<Matrix4x4> _sortM = new List<Matrix4x4>();
        private readonly List<MeshBaker.PieceIdentity> _sortId = new List<MeshBaker.PieceIdentity>();
        private float[] _sortV;

        // 1.2.198: an instanced-array length per run: the next power of two from 8, capped at the 1023 instances one draw can take
        private static int SizeClass(int n) => n <= 8 ? 8 : Mathf.Min(MaxInstancesPerDraw, Mathf.NextPowerOfTwo(n));

        // 1.2.198: one comparer for every sort (was a new closure per sort), reading the keys of the sort in progress
        private sealed class CellOrder : IComparer<int>
        {
            internal long[] Keys;
            public int Compare(int a, int b) { int c = Keys[a].CompareTo(Keys[b]); return c != 0 ? c : a.CompareTo(b); }
        }
        private static readonly CellOrder s_cellOrder = new CellOrder();

        private static long CellKey(Matrix4x4 m, float cell)
        {
            long cx = (long)Mathf.Floor(m.m03 / cell), cz = (long)Mathf.Floor(m.m23 / cell);
            return (cx << 32) ^ (cz & 0xFFFFFFFFL);
        }

        // orders [from, to) by cell (stable within a cell), carrying Identities and, when given, the snow values
        private void SortByCell(int from, int to, float[] values, float cell)
        {
            int n = to - from;
            if (n < 2) return;
            if (_cellKeys == null || _cellKeys.Length < n) { _cellKeys = new long[Mathf.NextPowerOfTwo(n)]; _cellOrder = new int[_cellKeys.Length]; }
            for (int i = 0; i < n; i++) { _cellKeys[i] = CellKey(Matrices[from + i], cell); _cellOrder[i] = i; }
            // stable: the key ties are broken by the original index
            s_cellOrder.Keys = _cellKeys;
            System.Array.Sort(_cellOrder, 0, n, s_cellOrder);
            s_cellOrder.Keys = null;
            _sortM.Clear(); _sortId.Clear();
            if (values != null && (_sortV == null || _sortV.Length < n)) _sortV = new float[_cellKeys.Length];
            for (int i = 0; i < n; i++)
            {
                int src = from + _cellOrder[i];
                _sortM.Add(Matrices[src]); _sortId.Add(Identities[src]);
                if (values != null) _sortV[i] = values[src];
            }
            for (int i = 0; i < n; i++)
            {
                Matrices[from + i] = _sortM[i]; Identities[from + i] = _sortId[i];
                if (values != null) values[from + i] = _sortV[i];
            }
        }

        // runs of one cell in [from, to), each at most 1023, with the bounds of exactly the instances in the run
        private void AddCellRanges(int from, int to, float cell, List<(int start, int n)> ranges, List<Bounds> bounds)
        {
            int start = from;
            while (start < to)
            {
                long key = CellKey(Matrices[start], cell);
                int end = start + 1;
                while (end < to && end - start < MaxInstancesPerDraw && CellKey(Matrices[end], cell) == key) end++;
                ranges.Add((start, end - start));
                bounds.Add(BoundsOf(start, end - start));
                start = end;
            }
        }

        private Bounds BoundsOf(int start, int n)
        {
            var local = Definition.MeshBounds;
            var e = local.extents;
            var b = new Bounds();
            for (int i = start; i < start + n; i++)
            {
                var m = Matrices[i];
                var centre = m.MultiplyPoint3x4(local.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == start) { b = new Bounds(centre, reach * 2f); continue; }
                b.Encapsulate(centre + reach); b.Encapsulate(centre - reach);
            }
            // Definition.MeshBounds already merges every part (near and far mesh) in the first part's space (InstanceDefinition.MergeBounds)
            return b;
        }

        // the plain (no snow) path's cell runs, rebuilt when the instances or the key change
        private void EnsurePlainCells(int count, float cell)
        {
            if (_plainVer == _ver && _plainCell == cell && _plainRanges.Count > 0) return;
            SortByCell(0, count, null, cell);
            _ver++; _partsDirty = true;
            _plainRanges.Clear(); _plainBounds.Clear();
            AddCellRanges(0, count, cell, _plainRanges, _plainBounds);
            _plainVer = _ver; _plainCell = cell;
            _snowDirty = true;   // the snow ranges were built for the old order
        }

        // ── 1.2.203 [Optimize] IndirectRenderer (EbmIndirect) ──
        // Bumped whenever an instance joins, leaves or is reordered: the indirect path re-registers the group then, never per frame.
        internal int Version => _ver;

        // Part p's instance matrices (part 0 = Matrices; later parts carried through their offset), built on demand.
        internal List<Matrix4x4> MatricesForPart(int p)
        {
            if (p == 0) return Matrices;
            EnsurePartMatrices(Definition.Parts);
            return _partMatrices[p];
        }

        // The cases only the instanced path draws right (per-range snow / wet property blocks, a camera that hides baked roofs).
        // Constant strings: this runs every frame for every taken group.
        internal bool IndirectNeedsLegacy(out string why)
        {
            // 1.2.204: snow / roofed-dry ranges go indirect too: per-instance looks (IndirectLooks, _EbmInstLooks) read by EBM/Piece
            if (IsRoof && InstancedDraw.RoofHidingActive) { why = "a camera hides baked roofs (live map)"; return true; }
            why = null;
            return false;
        }

        // ── Step A2 ([wishbone] 11-08): per-instance looks for the indirect path (IndirectLooks) ──
        // Bumped at the end of every finished snow / wet re-split (EnsureSnow): the indirect path rewrites the group's looks then.
        internal int SnowGeneration => _snowGen;
        // The look ranges are in force (the same test the instanced Draw uses); off = every instance plain.
        internal bool LooksActive => SnowFeed.Active || WetOverride.BakedDries || ExtCount > 0;

        // ── 1.2.206 EbmApi.SetPieceSnow (EbmSnowStore) ──
        // ExtCount = how many of this group's pieces have a pushed value; ExtGen bumps on every change (IndirectLooks.WriteGroup reads it).
        internal int ExtCount;
        internal int ExtGen;
        // 1.2.208 ([generator]'s review): pushes must not starve the sliced re-split. The indirect path reads the store through LookOf (ExtGen
        // re-runs WriteGroup), so a taken group needs no re-split at all. On the instanced path a push during a pass in flight is held until it ends.
        internal void ExternalLooksChanged()
        {
            ExtGen++; Indirect.EbmIndirect.MarkGroupDirty(this);
            if (_indirectTaken) { _extPendingResplit = true; return; }   // re-split only if it goes back to the instanced path
            if (_sIdx >= 0) _snowDirtyAgain = true; else _snowDirty = true;
        }
        private bool _snowDirtyAgain, _extPendingResplit;

        // A group the indirect path takes is never drawn by Draw, so the snow / wet dirty checks and the (budgeted, sliced) re-split
        // that Draw runs first are run from here instead, once a frame per taken group, BEFORE its Version is compared (a finished
        // re-split may reorder the instances and bump it). Same lines as the top of Draw.
        // 1.2.223 ([visual], lead: "looks prep 0.129 ms every frame while standing"): true when PrepareLooksForIndirect would change nothing:
        // the exact no-op conditions of each of its branches, as field compares (no EnsureSnow call, no budget bookkeeping).
        internal bool LooksSettled() => LooksSettledWhy() < 0;

        // 1.2.227: -1 = settled; else the first reason it is not (6 not taken, 7 dirty, 8 pass running, 9 wet / field changed, 10 inside gen,
        // 11 cell size, 12 no ranges yet) - EbmIndirect counts these so the [Draw] line names what re-dirties groups
        internal int LooksSettledWhy()
        {
            if (!_indirectTaken) return 6;
            if (Matrices.Count == 0) return -1;
            bool wet = WetOverride.BakedDries;
            if (wet != _wetSeen || SnowFeed.FieldActive != _fieldSeen) return 9;
            if (wet && WetOverride.InsideGeneration != _insideGen) return 10;
            bool snow = SnowFeed.Active || wet || ExtCount > 0;
            // 1.2.230 (lead, 1227 log: "not skipped because: snow dirty 2,090,091" in a set with NO snow mod): _snowDirty starts true and only
            // EnsureSnow clears it, which Prepare calls only with snow / wet / pushed looks on. With none of them, Prepare does nothing whatever
            // the flag says, so it is settled; the dirty flag waits for the first real look (and LooksActive flipping re-runs Prepare).
            if (!snow) return -1;
            if (_snowDirty) return 7;
            if (_sIdx >= 0) return 8;
            if (_snowCell != 0f) return 11;
            return _snowRanges.Count > 0 ? -1 : 12;
        }

        internal void PrepareLooksForIndirect()
        {
            _indirectTaken = true;   // 1.2.205: EnsureSnow builds no cell runs for it (indirect draws whole batches); the main-view Draw clears it
            int count = Matrices.Count;
            if (count == 0) return;
            bool wet = WetOverride.BakedDries;
            if (wet != _wetSeen) { _wetSeen = wet; _snowDirty = true; }
            bool field = SnowFeed.FieldActive;
            if (field != _fieldSeen) { _fieldSeen = field; _snowDirty = true; }
            if (wet && WetOverride.InsideGeneration != _insideGen) { _insideGen = WetOverride.InsideGeneration; _snowDirty = true; }
            bool snow = SnowFeed.Active || wet || ExtCount > 0;   // 1.2.206
            float cell = SnowCellSize;
            if (snow && cell != _snowCell && _sIdx < 0) _snowDirty = true;
            if (snow) EnsureSnow(count);
        }

        // 1.2.205 ([wishbone], [generator]'s A2 review): with a cell size, every finished re-split re-sorted by cell and bumped Version = a full
        // Drop + Sync of a taken group on every snow step. The indirect path needs no cell runs, so a taken group's snow layout uses none.
        // Back on the instanced path, _snowCell (0) differs from CellSize, so Draw's own check re-splits it once in cell form.
        private bool _indirectTaken;
        private float SnowCellSize => _indirectTaken ? 0f : CellSize;

        // Instance i's look in the group's current order: x snow cover, y wet scale (0 = a roofed range drawn dry, 1 = open).
        // Until a re-split has finished for the current count, plain. A pass still running keeps the last finished values: the
        // instances are only reordered when a pass ends, so they still line up.
        internal Vector4 LookOf(int i)
        {
            // 1.2.206: a pushed value first, by the piece's identity (Identities moves with Matrices, so i still names the same piece)
            if (ExtCount > 0 && i >= 0 && i < Identities.Count && EbmSnowStore.TryGet(Identities[i], out var ext)) return new Vector4(ext.x, ext.y, 0f, 0f);
            if (i >= 0 && i < Identities.Count && EbmSnowStore.HoldPlain(Identities[i])) return new Vector4(0f, 1f, 0f, 0f);   // 1.2.260 WaitForPushedSnow
            if (!LooksActive || _instVals == null || _snowRangesCount != Matrices.Count || i < 0 || i >= _snowRangesCount) return new Vector4(0f, 1f, 0f, 0f);
            return new Vector4(_instVals[i], _wetSeen && i < _instCovered ? 0f : 1f, 0f, 0f);
        }


        private void EnsurePartMatrices(InstancePart[] parts)
        {
            if (parts.Length < 2) return;
            if (!_partsDirty && _partMatrices != null && _partMatrices.Length == parts.Length) return;

            if (_partMatrices == null || _partMatrices.Length != parts.Length)
                _partMatrices = new List<Matrix4x4>[parts.Length];

            for (int p = 1; p < parts.Length; p++)
            {
                var list = _partMatrices[p];
                if (list == null)
                {
                    list = new List<Matrix4x4>(Matrices.Count);
                    _partMatrices[p] = list;
                }
                list.Clear();
                var fromFirst = parts[p].FromFirst;
                for (int i = 0; i < Matrices.Count; i++) list.Add(Matrices[i] * fromFirst);
            }
            _partsDirty = false;
        }
    }
}
