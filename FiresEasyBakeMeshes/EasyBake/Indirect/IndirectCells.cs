// IndirectCells.cs ([fgn] 11-08, for [visual]'s EBM indirect renderer, Fire's 100+ fps push at the 615 town pose).
// Method credit: the batch-cell CPU cull idea comes from ValheimPerformanceOptimizations by ontrigger (MIT; see THIRD_PARTY_NOTICES),
// files IndirectRendering/Renderer/BatchCellGrid.cs and VPOUnity/.../Burst/MarkVisibleBatchesJob.cs: instances bucketed in XZ cells,
// a per-cell entry per batch (instance count, bounds that only grow until the last instance leaves, LOD range), and a cull that tests
// the cell box, then the LOD range by the cell's nearest distance, then each entry box. Fire 11-08: same logic is fine, the code is OURS
// (our names, our structure, no copied files). This file is our own code.
// Ours on top of the method: a CPU screen-size cut per entry from its LARGEST single instance (radius) at the entry box's nearest
// distance, so small things across a whole 64 m cell drop out without a view-projection matrix; lazy bounds shrink in Compact().
// Plain C#, no Burst, no allocation per frame (arrays grow by doubling outside CullViews). Not thread-safe: one caller at a time.
using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    internal sealed class IndirectCells
    {
        /// <summary>Cell edge in meters (one zone).</summary>
        internal const float CellSize = 64f;
        private const float InvCellSize = 1f / CellSize;

        // One batch's instances inside one cell.
        private struct Entry
        {
            public int Batch;
            public int Count;
            public float LodEnd;      // largest LOD end distance of its instances; <= 0 = never culled by distance
            public float LodStart;    // 1.2.210: smallest LOD start distance of its instances; 0 = drawn from the camera on (a nearer entry draws another level)
            public float MaxRadius;   // largest single-instance bounding radius (for the screen-size cut)
            public Vector3 Min, Max;  // union of its instances' bounds (grows only; reset when Count reaches 0)
        }

        private sealed class Cell
        {
            public long Key;
            public int Count;          // live instances; the cell stays registered at 0 and resets on its next Add
            public Vector3 Min, Max;   // union of its live entries (grows only; Compact() shrinks it)
            public Entry[] Entries = new Entry[4];
            public int EntryCount;
            public readonly Dictionary<int, int> EntryOfBatch = new Dictionary<int, int>(4);
            public int Index;          // 1.2.254: its slot in _cells
            public bool InDirty;       // 1.2.254: already in _dirty
        }

        // 1.2.254 [Optimize] IndirectCellReusePerCell: the cells that gained an instance since the last walk. A cell that only lost one is not listed: last frame's
        // "visible" for it is still a superset (the GPU culls per instance). Bounded: past MaxDirty, or after a Compact / Clear, the caller walks everything.
        private const int MaxDirty = 256;
        private readonly Cell[] _dirty = new Cell[MaxDirty];
        private int _dirtyCount;
        private bool _dirtyAll = true;
        internal bool TrackDirty;
        internal int DirtyCount => _dirtyCount;
        /// <summary>The dirty list is complete and small enough that re-judging only those cells beats a full walk.</summary>
        internal bool DirtyMergeable => TrackDirty && !_dirtyAll && _dirtyCount * 2 <= _cellCount;

        private void MarkDirty(Cell cell)
        {
            if (!TrackDirty) { _dirtyAll = true; return; }
            if (_dirtyAll || cell.InDirty) return;
            if (_dirtyCount == MaxDirty) { _dirtyAll = true; return; }
            cell.InDirty = true;
            _dirty[_dirtyCount++] = cell;
        }

        /// <summary>After a walk or merge: nothing is outstanding.</summary>
        internal void DirtyReset()
        {
            for (int i = 0; i < _dirtyCount; i++) { _dirty[i].InDirty = false; _dirty[i] = null; }
            _dirtyCount = 0;
            _dirtyAll = !TrackDirty;
        }

        private readonly Dictionary<long, Cell> _cellOfKey = new Dictionary<long, Cell>(256);
        private Cell[] _cells = new Cell[64];
        private int _cellCount;
        private int _batchCount;          // highest batch index seen + 1: the row width of CullViews' output
        private bool[] _cellSeenBy = new bool[8];   // scratch: which views see the current cell (no per-frame alloc; CullSpheres uses at most 4)

        /// <summary>Row width of CullViews' output: outBatchVisible needs at least viewCount × BatchCount slots.</summary>
        internal int BatchCount => _batchCount;
        internal int CellCount => _cellCount;
        /// <summary>1.2.220: bumped by every Add / Remove / Compact / Clear, so a cached cull result knows the cells did not change.</summary>
        internal int Version;

        /// <summary>1.2.222: the farthest live cell corner from p (O(cells), once per cell-walk compute).</summary>
        internal float FarthestCornerFrom(Vector3 p) { float m = 0f; for (int i = 0; i < _cellCount; i++) { var c = _cells[i]; if (c.Count == 0) continue; float d = FarthestDistance(p, c.Min, c.Max); if (d > m) m = d; } return m; }

        /// <summary>Adds an instance. Its cell is picked from the bounds centre; Remove must then pass that same centre.
        /// Prefer the overload with the instance's position when Remove will pass the transform position.</summary>
        internal void Add(int batch, Vector3 boundsMin, Vector3 boundsMax, float lodEndDistance)
            => Add(batch, (boundsMin + boundsMax) * 0.5f, boundsMin, boundsMax, lodEndDistance);

        /// <summary>Adds an instance filed under the cell of position (pass the same position to Remove).</summary>
        internal void Add(int batch, Vector3 position, Vector3 boundsMin, Vector3 boundsMax, float lodEndDistance) => Add(batch, position, boundsMin, boundsMax, lodEndDistance, 0f);

        /// <summary>1.2.210: + where its LOD window starts (0 = from the camera on).</summary>
        internal void Add(int batch, Vector3 position, Vector3 boundsMin, Vector3 boundsMax, float lodEndDistance, float lodStartDistance)
        {
            Version++;
            if (batch < 0) throw new ArgumentOutOfRangeException(nameof(batch));
            if (batch >= _batchCount) _batchCount = batch + 1;
            long key = KeyOf(position);
            if (!_cellOfKey.TryGetValue(key, out var cell))
            {
                cell = new Cell { Key = key, Index = _cellCount };
                _cellOfKey.Add(key, cell);
                if (_cellCount == _cells.Length) Array.Resize(ref _cells, _cells.Length * 2);
                _cells[_cellCount++] = cell;
            }
            MarkDirty(cell);   // 1.2.254
            float radius = (boundsMax - boundsMin).magnitude * 0.5f;
            if (cell.Count == 0) { cell.Min = boundsMin; cell.Max = boundsMax; }
            else { cell.Min = Vector3.Min(cell.Min, boundsMin); cell.Max = Vector3.Max(cell.Max, boundsMax); }
            cell.Count++;

            if (!cell.EntryOfBatch.TryGetValue(batch, out int e))
            {
                if (cell.EntryCount == cell.Entries.Length) Array.Resize(ref cell.Entries, cell.Entries.Length * 2);
                e = cell.EntryCount++;
                cell.EntryOfBatch.Add(batch, e);
                cell.Entries[e] = new Entry { Batch = batch, Count = 1, LodEnd = lodEndDistance, LodStart = lodStartDistance, MaxRadius = radius, Min = boundsMin, Max = boundsMax };
                return;
            }
            ref var en = ref cell.Entries[e];
            en.Count++;
            // a "never culled" instance (LodEnd <= 0) makes the whole entry never culled by distance
            en.LodEnd = (en.LodEnd <= 0f || lodEndDistance <= 0f) ? 0f : Mathf.Max(en.LodEnd, lodEndDistance);
            en.LodStart = Mathf.Min(en.LodStart, lodStartDistance);
            if (radius > en.MaxRadius) en.MaxRadius = radius;
            en.Min = Vector3.Min(en.Min, boundsMin);
            en.Max = Vector3.Max(en.Max, boundsMax);
        }

        /// <summary>Removes one instance of batch filed under position's cell. Unknown cell or batch: nothing happens.</summary>
        internal void Remove(int batch, Vector3 position)
        {
            Version++;
            if (!_cellOfKey.TryGetValue(KeyOf(position), out var cell) || !cell.EntryOfBatch.TryGetValue(batch, out int e)) return;
            if (--cell.Entries[e].Count == 0)
            {
                // swap the last entry into the gap
                cell.EntryOfBatch.Remove(batch);
                int last = --cell.EntryCount;
                if (e != last) { cell.Entries[e] = cell.Entries[last]; cell.EntryOfBatch[cell.Entries[e].Batch] = e; }
                cell.Entries[last] = default;
            }
            if (cell.Count > 0) cell.Count--;
        }

        /// <summary>Lazy shrink: each cell's box becomes the union of its live entries; empty cells are dropped; BatchCount is recounted.
        /// Entry boxes keep growing until their last instance leaves (no per-instance data is kept). Call it rarely (e.g. after a zone unloads).</summary>
        internal void Compact()
        {
            Version++;
            ClearDirtyList();   // 1.2.254: slots renumber below
            int w = 0, maxBatch = -1;
            for (int i = 0; i < _cellCount; i++)
            {
                var c = _cells[i];
                if (c.Count == 0 || c.EntryCount == 0) { _cellOfKey.Remove(c.Key); continue; }
                Vector3 mn = c.Entries[0].Min, mx = c.Entries[0].Max;
                for (int e = 0; e < c.EntryCount; e++)
                {
                    mn = Vector3.Min(mn, c.Entries[e].Min); mx = Vector3.Max(mx, c.Entries[e].Max);
                    if (c.Entries[e].Batch > maxBatch) maxBatch = c.Entries[e].Batch;
                }
                c.Min = mn; c.Max = mx;
                c.Index = w;   // 1.2.254
                _cells[w++] = c;
            }
            for (int i = w; i < _cellCount; i++) _cells[i] = null;
            _cellCount = w;
            _batchCount = maxBatch + 1;
        }

        // 1.2.254: forget the list and demand a full walk (cells renumbered or gone)
        private void ClearDirtyList()
        {
            for (int i = 0; i < _dirtyCount; i++) { _dirty[i].InDirty = false; _dirty[i] = null; }
            _dirtyCount = 0;
            _dirtyAll = true;
        }

        internal void Clear()
        {
            Version++;
            ClearDirtyList();   // 1.2.254
            _cellOfKey.Clear();
            Array.Clear(_cells, 0, _cellCount);
            _cellCount = 0; _batchCount = 0;
        }

        /// <summary>
        /// Marks, per view, every batch with at least one entry that passes: the view's 6 planes (cell box, then entry box), the LOD end
        /// distance × lodScale against the cell's nearest distance from cameraPos, and the screen-size cut. Writes
        /// outBatchVisible[view * BatchCount + batch] (cleared first for viewCount rows) and returns how many flags are set.
        /// planes: 6 per view, normals pointing inward (GeometryUtility.CalculateFrustumPlanes).
        /// minScreenFraction: fraction of the screen HEIGHT (0..1) the largest instance of an entry must cover; 0 = off.
        /// projectionScaleY: the camera projection's [1,1] (= 1 / tan(fovY / 2)); 0 = screen cut off.
        /// </summary>
        internal int CullViews(Plane[] planes, int viewCount, Vector3 cameraPos, float lodScale, float minScreenFraction, bool[] outBatchVisible, float projectionScaleY = 0f)
        {
            if (viewCount <= 0) return 0;
            int width = _batchCount;
            if (planes == null || planes.Length < viewCount * 6) throw new ArgumentException("planes needs 6 per view", nameof(planes));
            if (outBatchVisible == null || outBatchVisible.Length < viewCount * width) throw new ArgumentException("outBatchVisible needs viewCount × BatchCount slots", nameof(outBatchVisible));
            if (_cellSeenBy.Length < viewCount) _cellSeenBy = new bool[Math.Max(viewCount, _cellSeenBy.Length * 2)];   // only when the view count grows
            Array.Clear(outBatchVisible, 0, viewCount * width);

            bool screenCut = minScreenFraction > 0f && projectionScaleY > 0f;
            float lodK = lodScale > 0f ? lodScale : 1f;
            int set = 0;
            for (int ci = 0; ci < _cellCount; ci++)
            {
                var c = _cells[ci];
                if (c.Count == 0 || c.EntryCount == 0) continue;
                Vector3 cc = (c.Min + c.Max) * 0.5f, ce = (c.Max - c.Min) * 0.5f;
                bool any = false;
                for (int v = 0; v < viewCount; v++) { bool s = BoxInPlanes(planes, v * 6, cc, ce); _cellSeenBy[v] = s; any |= s; }
                if (!any) continue;
                float cellNear = NearestDistance(cameraPos, c.Min, c.Max);

                for (int ei = 0; ei < c.EntryCount; ei++)
                {
                    ref var en = ref c.Entries[ei];
                    // cheap reject on the cell's distance first, then the entry's own (finer: a far batch in a cell that also holds
                    // something near the camera still drops out)
                    if (en.LodEnd > 0f && cellNear >= en.LodEnd * lodK) continue;
                    float near = (en.LodEnd > 0f || screenCut) ? NearestDistance(cameraPos, en.Min, en.Max) : 0f;
                    if (en.LodEnd > 0f && near >= en.LodEnd * lodK) continue;
                    // 1.2.210 (study #11): the whole entry box is nearer than its LOD start: the GPU draws another level there (10% margin)
                    if (en.LodStart > 0f && FarthestDistance(cameraPos, en.Min, en.Max) < en.LodStart * lodK * 0.9f) continue;
                    if (screenCut && near > 0f && en.MaxRadius * projectionScaleY < minScreenFraction * near) continue;
                    Vector3 ec = (en.Min + en.Max) * 0.5f, ee = (en.Max - en.Min) * 0.5f;
                    for (int v = 0; v < viewCount; v++)
                    {
                        if (!_cellSeenBy[v]) continue;
                        int slot = v * width + en.Batch;
                        if (outBatchVisible[slot] || !BoxInPlanes(planes, v * 6, ec, ee)) continue;
                        outBatchVisible[slot] = true; set++;
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// 1.2.210 ([visual], Fire's O log: ~2,400 empty caster draws a frame): marks, per sphere view, every batch with an entry that can
        /// reach it. Rows rowStart .. rowStart + count - 1 of outBatchVisible (row width = BatchCount) are cleared, then set.
        /// lightDir != zero (the sun's cascades): the entry box, as a sphere, swept along lightDir must overlap the cascade sphere's disc and not
        /// lie wholly past it downstream (the GPU's own cascade test, on the entry box). lightDir == zero (point / spot lights): sphere against
        /// sphere (the spot cone stays the GPU's job). The LOD end cut is the camera's, as the GPU's (shadows use the camera's LOD level).
        /// margin metres widen every sphere so a list does not flip on and off at an edge. Returns how many flags were set.
        /// </summary>
        internal int CullSpheres(Vector4[] spheres, int count, Vector3 lightDir, float margin, bool[] outBatchVisible, int rowStart, Vector3 cameraPos, float lodScale)
        {
            int width = _batchCount;
            if (count <= 0 || width == 0) return 0;
            if (outBatchVisible == null || outBatchVisible.Length < (rowStart + count) * width) throw new ArgumentException("outBatchVisible is too small", nameof(outBatchVisible));
            Array.Clear(outBatchVisible, rowStart * width, count * width);
            bool swept = lightDir.sqrMagnitude > 0f;
            float lodK = lodScale > 0f ? lodScale : 1f;
            int set = 0;
            for (int ci = 0; ci < _cellCount; ci++)
            {
                var c = _cells[ci];
                if (c.Count == 0 || c.EntryCount == 0) continue;
                Vector3 cc = (c.Min + c.Max) * 0.5f;
                float cr = (c.Max - c.Min).magnitude * 0.5f;
                bool any = false;
                for (int v = 0; v < count; v++) { bool s = SphereReaches(spheres[v], margin, swept, lightDir, cc, cr); _cellSeenBy[v] = s; any |= s; }
                if (!any) continue;
                float cellNear = NearestDistance(cameraPos, c.Min, c.Max);
                for (int ei = 0; ei < c.EntryCount; ei++)
                {
                    ref var en = ref c.Entries[ei];
                    if (en.LodEnd > 0f && cellNear >= en.LodEnd * lodK) continue;
                    if (en.LodEnd > 0f && NearestDistance(cameraPos, en.Min, en.Max) >= en.LodEnd * lodK) continue;
                    if (en.LodStart > 0f && FarthestDistance(cameraPos, en.Min, en.Max) < en.LodStart * lodK * 0.9f) continue;   // 1.2.210: another LOD level draws there
                    Vector3 ec = (en.Min + en.Max) * 0.5f;
                    float er = (en.Max - en.Min).magnitude * 0.5f;
                    for (int v = 0; v < count; v++)
                    {
                        if (!_cellSeenBy[v]) continue;
                        int slot = (rowStart + v) * width + en.Batch;
                        if (outBatchVisible[slot] || !SphereReaches(spheres[v], margin, swept, lightDir, ec, er)) continue;
                        outBatchVisible[slot] = true; set++;
                    }
                }
            }
            return set;
        }

        /// <summary>
        /// 1.2.213 ([saddles], [Optimize] IndirectFusedCells): CullViews (camera views, with the screen cut) + CullSpheres (sun cascades, swept)
        /// + CullSpheres (lights, not swept) + CullViews (light cube faces, no screen cut) in ONE walk: each cell box and each entry's LOD tests
        /// run once for every view kind, instead of once per kind. Writes exactly what those four calls write (same rows, same widths, same
        /// clears): viewOut rows 0..viewCount-1, cascRow.., lightRow.. (one array, row width BatchCount), faceOut rows 0..faceViews-1.
        /// A count of 0 skips that kind (its rows are not touched, as the separate call would not have run).
        /// </summary>
        internal int CullFused(Plane[] viewPlanes, int viewCount, float minScreenFraction, float projectionScaleY, bool[] viewOut,
                               Vector4[] cascSpheres, int cascCount, Vector3 lightDir, int cascRow,
                               Vector4[] lightSpheres, int lightCount, int lightRow, float margin,
                               Plane[] facePlanes, int faceViews, bool[] faceOut,
                               Vector3 cameraPos, float lodScale, bool mergeDirty = false)   // 1.2.254: mergeDirty = keep the rows as they are and OR in only the dirty cells
        {
            int width = _batchCount;
            if (width == 0) return 0;
            if (viewCount < 0 || cascCount < 0 || lightCount < 0 || faceViews < 0) throw new ArgumentOutOfRangeException(nameof(viewCount));
            if (viewCount > 0 && (viewPlanes == null || viewPlanes.Length < viewCount * 6)) throw new ArgumentException("viewPlanes needs 6 per view", nameof(viewPlanes));
            if (faceViews > 0 && (facePlanes == null || facePlanes.Length < faceViews * 6)) throw new ArgumentException("facePlanes needs 6 per view", nameof(facePlanes));
            int rowsNeeded = Math.Max(viewCount, Math.Max(cascCount > 0 ? cascRow + cascCount : 0, lightCount > 0 ? lightRow + lightCount : 0));
            if (viewOut == null || viewOut.Length < rowsNeeded * width) throw new ArgumentException("viewOut is too small", nameof(viewOut));
            if (faceViews > 0 && (faceOut == null || faceOut.Length < faceViews * width)) throw new ArgumentException("faceOut is too small", nameof(faceOut));

            if (!mergeDirty)
            {
                if (viewCount > 0) Array.Clear(viewOut, 0, viewCount * width);
                if (cascCount > 0) Array.Clear(viewOut, cascRow * width, cascCount * width);
                if (lightCount > 0) Array.Clear(viewOut, lightRow * width, lightCount * width);
                if (faceViews > 0) Array.Clear(faceOut, 0, faceViews * width);
            }

            // seen flags for this cell: [views | cascades | lights | faces]
            int c0 = viewCount, l0 = c0 + cascCount, f0 = l0 + lightCount, all = f0 + faceViews;
            if (all == 0) return 0;
            if (_cellSeenBy.Length < all) _cellSeenBy = new bool[Math.Max(all, _cellSeenBy.Length * 2)];
            bool screenCut = minScreenFraction > 0f && projectionScaleY > 0f;
            bool swept = lightDir.sqrMagnitude > 0f;
            float lodK = lodScale > 0f ? lodScale : 1f;
            var a = _fused ?? (_fused = new FusedArgs());   // 1.2.244: one object, no allocation per frame
                            a.ViewPlanes = viewPlanes;
            a.ViewCount = viewCount;
            a.MinScreen = minScreenFraction;
            a.ProjectionY = projectionScaleY;
            a.ViewOut = viewOut;
            a.CascSpheres = cascSpheres;
            a.CascCount = cascCount;
            a.LightDir = lightDir;
            a.CascRow = cascRow;
            a.LightSpheres = lightSpheres;
            a.LightCount = lightCount;
            a.LightRow = lightRow;
            a.Margin = margin;
            a.FacePlanes = facePlanes;
            a.FaceViews = faceViews;
            a.FaceOut = faceOut;
            a.CameraPos = cameraPos;
            a.Width = width;
            a.C0 = c0;
            a.L0 = l0;
            a.F0 = f0;
            a.ScreenCut = screenCut;
            a.Swept = swept;
            a.LodK = lodK;
            a.Seen = all;
            _fusedSet = 0;
            if (mergeDirty)   // 1.2.254: a handful of cells, main thread
            {
                for (int d = 0; d < _dirtyCount; d++) FusedCell(_dirty[d].Index, _cellSeenBy, _cellInside);
                return _fusedSet;
            }
            // 1.2.241: the caller waits on Complete, so nothing can change the cells while the workers read them
            if (FiresEasyBakeMeshesPlugin.IndirectCellsJob != null && FiresEasyBakeMeshesPlugin.IndirectCellsJob.Value && _cellCount >= MinCellsForJob)
            {
                s_fusedOwner = this;
                new FusedCellJob().Schedule(_cellCount, 4).Complete();
                s_fusedOwner = null;
            }
            else
                for (int ci = 0; ci < _cellCount; ci++) FusedCell(ci, _cellSeenBy, _cellInside);
            return _fusedSet;
        }

        // 1.2.241 [Optimize] IndirectCellsJob
        private const int MinCellsForJob = 8;
        private static IndirectCells s_fusedOwner;
        [ThreadStatic] private static bool[] t_seen;
        [ThreadStatic] private static bool[] t_inside;
        private FusedArgs _fused;
        private int _fusedSet;

        private sealed class FusedArgs
        {
            public Plane[] ViewPlanes, FacePlanes; public Vector4[] CascSpheres, LightSpheres; public bool[] ViewOut, FaceOut;
            public int ViewCount, CascCount, CascRow, LightCount, LightRow, FaceViews, Width, C0, L0, F0, Seen;
            public float MinScreen, ProjectionY, Margin, LodK; public Vector3 LightDir, CameraPos; public bool ScreenCut, Swept;
        }

        private struct FusedCellJob : Unity.Jobs.IJobParallelFor
        {
            public void Execute(int ci)
            {
                var owner = s_fusedOwner;
                int need = owner._fused.Seen;
                if (t_seen == null || t_seen.Length < need) { t_seen = new bool[System.Math.Max(need, 32)]; t_inside = new bool[t_seen.Length]; }
                owner.FusedCell(ci, t_seen, t_inside);
            }
        }

        private void FusedCell(int ci, bool[] seen, bool[] inside)
        {
            var a = _fused;
            Plane[] viewPlanes = a.ViewPlanes, facePlanes = a.FacePlanes; Vector4[] cascSpheres = a.CascSpheres, lightSpheres = a.LightSpheres; bool[] viewOut = a.ViewOut, faceOut = a.FaceOut;
            int viewCount = a.ViewCount, cascCount = a.CascCount, cascRow = a.CascRow, lightCount = a.LightCount, lightRow = a.LightRow, faceViews = a.FaceViews, width = a.Width, c0 = a.C0, l0 = a.L0, f0 = a.F0;
            float minScreenFraction = a.MinScreen, projectionScaleY = a.ProjectionY, margin = a.Margin, lodK = a.LodK; Vector3 lightDir = a.LightDir, cameraPos = a.CameraPos; bool screenCut = a.ScreenCut, swept = a.Swept;
            int set = 0; long skipped = 0; bool fastSkip = FastSkipOn;
            var c = _cells[ci];
            if (c.Count == 0 || c.EntryCount == 0) return;
            Vector3 cc = (c.Min + c.Max) * 0.5f, ce = (c.Max - c.Min) * 0.5f;
            float cr = (c.Max - c.Min).magnitude * 0.5f;
            bool any = false;
            for (int v = 0; v < viewCount; v++) { bool s = BoxInPlanes(viewPlanes, v * 6, cc, ce); seen[v] = s; any |= s; if (v < inside.Length) inside[v] = s && BoxFullyInPlanes(viewPlanes, v * 6, cc, ce); }
            for (int v = 0; v < cascCount; v++) { bool s = SphereReaches(cascSpheres[v], margin, swept, lightDir, cc, cr); seen[c0 + v] = s; any |= s; }
            for (int v = 0; v < lightCount; v++) { bool s = SphereReaches(lightSpheres[v], margin, false, Vector3.zero, cc, cr); seen[l0 + v] = s; any |= s; }
            for (int v = 0; v < faceViews; v++) { bool s = BoxInPlanes(facePlanes, v * 6, cc, ce); seen[f0 + v] = s; any |= s; }
            if (!any) return;
            float cellNear = NearestDistance(cameraPos, c.Min, c.Max);

            for (int ei = 0; ei < c.EntryCount; ei++)
            {
                ref var en = ref c.Entries[ei];
                int b = en.Batch;
                // 1.2.214 ([visual], O at 615: the walk scaled with entries, 1.77 ms at 247k): an entry whose batch is already marked in every
                // view whose cell test passed changes nothing, so skip it BEFORE its distance math (exact: same output)
                if (fastSkip && AllMarked(seen, b, width, viewCount, cascCount, cascRow, lightCount, lightRow, faceViews, viewOut, faceOut, c0, l0, f0)) { skipped++; continue; }
                // the LOD tests every kind shares (camera distance), once
                if (en.LodEnd > 0f && cellNear >= en.LodEnd * lodK) continue;
                float near = (en.LodEnd > 0f || screenCut) ? NearestDistance(cameraPos, en.Min, en.Max) : 0f;
                if (en.LodEnd > 0f && near >= en.LodEnd * lodK) continue;
                if (en.LodStart > 0f && FarthestDistance(cameraPos, en.Min, en.Max) < en.LodStart * lodK * 0.9f) continue;
                // the screen-size cut is the camera views' only (the spheres and faces never had it)
                bool tooSmall = screenCut && near > 0f && en.MaxRadius * projectionScaleY < minScreenFraction * near;
                Vector3 ec = (en.Min + en.Max) * 0.5f, ee = (en.Max - en.Min) * 0.5f;
                float er = (en.Max - en.Min).magnitude * 0.5f;
                if (!tooSmall)
                    for (int v = 0; v < viewCount; v++)
                    {
                        if (!seen[v]) continue;
                        int slot = v * width + b;
                        // 1.2.214: a cell wholly inside the view holds every entry inside it (no plane test)
                        if (viewOut[slot] || (!(fastSkip && v < inside.Length && inside[v]) && !BoxInPlanes(viewPlanes, v * 6, ec, ee))) continue;
                        viewOut[slot] = true; set++;
                    }
                for (int v = 0; v < cascCount; v++)
                {
                    if (!seen[c0 + v]) continue;
                    int slot = (cascRow + v) * width + b;
                    if (viewOut[slot] || !SphereReaches(cascSpheres[v], margin, swept, lightDir, ec, er)) continue;
                    viewOut[slot] = true; set++;
                }
                for (int v = 0; v < lightCount; v++)
                {
                    if (!seen[l0 + v]) continue;
                    int slot = (lightRow + v) * width + b;
                    if (viewOut[slot] || !SphereReaches(lightSpheres[v], margin, false, Vector3.zero, ec, er)) continue;
                    viewOut[slot] = true; set++;
                }
                for (int v = 0; v < faceViews; v++)
                {
                    if (!seen[f0 + v]) continue;
                    int slot = v * width + b;
                    if (faceOut[slot] || !BoxInPlanes(facePlanes, v * 6, ec, ee)) continue;
                    faceOut[slot] = true; set++;
                }
            }
            System.Threading.Interlocked.Add(ref _fusedSet, set);
            System.Threading.Interlocked.Add(ref _skipped, skipped);
        }

        // 1.2.214 [Optimize] IndirectCellFastSkip: the two exact shortcuts in CullFused
        internal static bool FastSkipOn => FiresEasyBakeMeshesPlugin.IndirectCellFastSkip == null || FiresEasyBakeMeshesPlugin.IndirectCellFastSkip.Value;
        private bool[] _cellInside = new bool[8];
        internal long _skipped;   // Debug: entries skipped as already marked

        private static bool AllMarked(bool[] seen, int b, int width, int viewCount, int cascCount, int cascRow, int lightCount, int lightRow, int faceViews, bool[] viewOut, bool[] faceOut, int c0, int l0, int f0)
        {
            for (int v = 0; v < viewCount; v++) if (seen[v] && !viewOut[v * width + b]) return false;
            for (int v = 0; v < cascCount; v++) if (seen[c0 + v] && !viewOut[(cascRow + v) * width + b]) return false;
            for (int v = 0; v < lightCount; v++) if (seen[l0 + v] && !viewOut[(lightRow + v) * width + b]) return false;
            for (int v = 0; v < faceViews; v++) if (seen[f0 + v] && !faceOut[v * width + b]) return false;
            return true;
        }

        // the box lies wholly on the inner side of all six planes
        private static bool BoxFullyInPlanes(Plane[] planes, int start, Vector3 center, Vector3 extents)
        {
            for (int i = start; i < start + 6; i++)
            {
                Vector3 n = planes[i].normal;
                float r = Mathf.Abs(n.x) * extents.x + Mathf.Abs(n.y) * extents.y + Mathf.Abs(n.z) * extents.z;
                if (n.x * center.x + n.y * center.y + n.z * center.z + planes[i].distance < r) return false;
            }
            return true;
        }

        private static bool SphereReaches(Vector4 s, float margin, bool swept, Vector3 l, Vector3 c, float r)
        {
            float reach = s.w + margin + r;
            Vector3 rel = new Vector3(c.x - s.x, c.y - s.y, c.z - s.z);
            float rr = rel.x * rel.x + rel.y * rel.y + rel.z * rel.z;
            if (!swept) return rr <= reach * reach;
            float along = rel.x * l.x + rel.y * l.y + rel.z * l.z;
            return rr - along * along <= reach * reach && along <= reach;
        }

        // A box is outside when it lies fully behind any one inward-facing plane.
        private static bool BoxInPlanes(Plane[] planes, int start, Vector3 center, Vector3 extents)
        {
            for (int i = start; i < start + 6; i++)
            {
                Vector3 n = planes[i].normal;
                float r = Mathf.Abs(n.x) * extents.x + Mathf.Abs(n.y) * extents.y + Mathf.Abs(n.z) * extents.z;
                if (n.x * center.x + n.y * center.y + n.z * center.z + planes[i].distance < -r) return false;
            }
            return true;
        }

        // Distance from p to the farthest corner of the box.
        private static float FarthestDistance(Vector3 p, Vector3 min, Vector3 max)
        {
            float dx = Mathf.Max(Mathf.Abs(p.x - min.x), Mathf.Abs(max.x - p.x));
            float dy = Mathf.Max(Mathf.Abs(p.y - min.y), Mathf.Abs(max.y - p.y));
            float dz = Mathf.Max(Mathf.Abs(p.z - min.z), Mathf.Abs(max.z - p.z));
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Distance from p to the nearest point of the box (0 inside).
        private static float NearestDistance(Vector3 p, Vector3 min, Vector3 max)
        {
            // two-argument Max only: Mathf.Max(a, b, c) takes params float[] and allocates on every call
            float dx = Mathf.Max(Mathf.Max(min.x - p.x, 0f), p.x - max.x);
            float dy = Mathf.Max(Mathf.Max(min.y - p.y, 0f), p.y - max.y);
            float dz = Mathf.Max(Mathf.Max(min.z - p.z, 0f), p.z - max.z);
            return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static long KeyOf(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x * InvCellSize), z = Mathf.FloorToInt(p.z * InvCellSize);
            return (x << 32) ^ (z & 0xFFFFFFFFL);
        }
    }
}
