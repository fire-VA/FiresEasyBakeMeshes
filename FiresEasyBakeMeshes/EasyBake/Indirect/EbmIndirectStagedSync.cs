using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [Optimize] IndirectStagedSync ([worldgen] 11-09, Tools\READ_EBM_take_sync_20261109.md "Key 2"; lead: "math on a job, commit under 2 ms a frame, staged slots
    // invisible until a one-group flip, cells added only at the flip"). Default off, read live.
    // A cached zone becomes Ready all at once, so every group in it reached Take on the same frame and Sync registered each one whole: 96.5 ms in one frame
    // with no GC in the run. With the key on, a group's FIRST registration of StageMinAdds adds or more goes in four steps instead:
    //   1. Take snapshots the group's matrices and lanes (the same batches Sync would pick) and returns false: the group draws through the instanced
    //      path meanwhile, the same picture as before indirect.
    //   2. A job works out each slot: final matrix, inverse, the bad-instance tests, LOD, world box (pure math; Matrix4x4.Inverse / GetDeterminant are thread safe).
    //   3. EndFrame commits the results under StageCommitBudgetMs a frame: a slot each, its GPU data written, its batch id left at 0 so the cull skips it.
    //      A CPU bit marks a staged slot, so slot compaction and stale free-heap entries never hand it out again.
    //   4. Take flips the whole group in one frame (s_instBatch, batch counts, cells, lanes), then the group is indirect from that frame on. Only the
    //      batch ids upload at the flip. Flips share StageFlipBudgetMs a frame (the first always goes).
    // A group that changed while staging gets the normal Sync for the difference right after its flip. Dropped, handed back or key off: staged slots go back.
    // Small groups, deltas and coverage are untouched. Same pixels as Sync; the cost is a few frames of instanced draws for the groups being staged.
    internal static partial class EbmIndirect
    {
        private static bool StagedSyncOn => FiresEasyBakeMeshesPlugin.IndirectStagedSync != null && FiresEasyBakeMeshesPlugin.IndirectStagedSync.Value;
        private const int StageMinAdds = 256;
        private const double StageCommitBudgetMs = 2.0, StageFlipBudgetMs = 1.0;
        private const int SfBad = 1, SfScaleNote = 2, SfMatrixNote = 4;

        // one lane as Sync would set it up (Kind 0 = a LOD part, 1 = a part level)
        private struct StageLane
        {
            public Matrix4x4 PartFromFirst, LodFromFirst;
            public Vector4 Lod; public Vector3 RefFirst; public float Size;
            public float LodStart, LodEnd, BatchStart, BatchEnd;
            public Bounds LocalBounds;
            public int Kind, PerInst, PartOffset;
        }

        // one slot's prepared data
        private struct StageOut
        {
            public GpuInstance G; public Matrix4x4 Src;
            public Vector4 LodWorld; public Vector3 Pos; public Bounds Wb;
            public float CellStart, CellEnd, EndDist, SqScale; public int Flags;
        }

        private sealed class GroupStage
        {
            public ZoneInstanceGroup Group;
            public int Version, N, Entries, Cursor, ScheduledFrame, Adds;
            public bool LodParts, JobDone;
            public Batch[] Batches;
            public string Context;
            public NativeArray<Matrix4x4> M;
            public NativeArray<StageLane> Lanes;
            public NativeArray<StageOut> Out;
            public NativeArray<int> Slot;
            public JobHandle Job;
        }

        private static readonly List<GroupStage> s_stageList = new List<GroupStage>();
        private static readonly List<Batch> s_stBatchScratch = new List<Batch>();
        private static readonly List<StageLane> s_stLaneScratch = new List<StageLane>();
        private static ulong[] s_stagedBits = new ulong[16];
        private static int s_stagedCount;
        private static ulong[] s_flipBits = new ulong[16];
        private static int s_flipMin = int.MaxValue, s_flipMax = -1;

        // ---- the job ----
        private struct StagePrepJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Matrix4x4> M;
            [ReadOnly] public NativeArray<StageLane> Lanes;
            public int N;
            public NativeArray<StageOut> Out;

            public void Execute(int e)
            {
                int lane = e / N, i = e - lane * N;
                var L = Lanes[lane];
                // the lane's source matrix: what SyncLane's src list holds (part 0 = Matrices, later parts carried through their offset, as EnsurePartMatrices)
                var src = L.PartOffset != 0 ? M[i] * L.PartFromFirst : M[i];
                Matrix4x4 m; Vector4 lw;
                if (L.Kind == 0)
                {
                    m = src * L.LodFromFirst;
                    lw = Vector4.zero;
                    if (L.Size > 0f) { var r = src.MultiplyPoint3x4(L.RefFirst); lw = new Vector4(r.x, r.y, r.z, L.Size); }
                }
                else { m = src; lw = LodWorld(L.Lod, m); }   // = BakedLod(batch, m) too: the lane's Lod is batch.Lod as BatchFor set it
                var o = new StageOut { Src = src, LodWorld = lw };
                bool scaleNote;
                if (StageBadMath(L.LocalBounds, m, out scaleNote) != 0) { o.Flags = SfBad; Out[e] = o; return; }
                if (scaleNote) o.Flags |= SfScaleNote;
                var inv = m.inverse;
                // NoteMatrix's own test; main logs it at commit
                if (Mathf.Abs(m.determinant) < 1e-12f || !Finite(m.GetRow(0)) || !Finite(m.GetRow(1)) || !Finite(m.GetRow(2)) || !Finite(inv.GetRow(0)) || !Finite(inv.GetRow(1)) || !Finite(inv.GetRow(2)))
                    o.Flags |= SfMatrixNote;
                o.SqScale = Mathf.Max(new Vector3(m.m00, m.m10, m.m20).sqrMagnitude, Mathf.Max(new Vector3(m.m01, m.m11, m.m21).sqrMagnitude, new Vector3(m.m02, m.m12, m.m22).sqrMagnitude));   // NoteScale's
                o.G = new GpuInstance { O0 = m.GetRow(0), O1 = m.GetRow(1), O2 = m.GetRow(2), W0 = inv.GetRow(0), W1 = inv.GetRow(1), W2 = inv.GetRow(2) };
                o.Pos = m.GetColumn(3);
                // AddInstance's LOD branches
                if (L.PerInst != 0)
                {
                    float sd, ed;
                    LodDistances(lw, L.LodStart, L.LodEnd, out sd, out ed);
                    o.CellStart = sd; o.EndDist = ed;
                    o.CellEnd = lw.w > 0f && ed < 65504f ? ed : 0f;
                }
                else
                {
                    o.CellEnd = L.BatchEnd > 0f && lw.w > 0f ? lw.w / L.BatchEnd : 0f;
                    o.CellStart = L.BatchStart > 0f && L.BatchStart < float.MaxValue && lw.w > 0f ? lw.w / L.BatchStart : 0f;   // LodStartDistance
                }
                o.Wb = TransformBounds(L.LocalBounds, m);
                Out[e] = o;
            }
        }

        // BadInstance's tests with no strings and no logging (0 = drawn). scaleNote = the case BadInstance names at Debug and still draws.
        private static int StageBadMath(Bounds localBounds, Matrix4x4 m, out bool scaleNote)
        {
            scaleNote = false;
            for (int e = 0; e < 16; e++) if (float.IsNaN(m[e]) || float.IsInfinity(m[e])) return 1;
            float sx = new Vector3(m.m00, m.m10, m.m20).magnitude, sy = new Vector3(m.m01, m.m11, m.m21).magnitude, sz = new Vector3(m.m02, m.m12, m.m22).magnitude;
            float max = Mathf.Max(sx, Mathf.Max(sy, sz)), min = Mathf.Min(sx, Mathf.Min(sy, sz));
            float det = m.determinant;
            float box = TransformBounds(localBounds, m).size.magnitude;
            if (min <= 1e-6f || Mathf.Abs(det) < 1e-4f * sx * sy * sz) return 2;
            if (box > MaxInstanceWorldBox && box > MaxBoxPerMeshSize * (2f * localBounds.extents.magnitude)) return 3;
            scaleNote = max > 10f || min < 0.05f;
            return 0;
        }

        // ---- slots ----
        private static bool IsStaged(int slot) { int w = slot >> 6; return w < s_stagedBits.Length && (s_stagedBits[w] & (1UL << (slot & 63))) != 0; }

        private static void SetStaged(int slot, bool on)
        {
            int w = slot >> 6;
            if (w >= s_stagedBits.Length) { if (!on) return; System.Array.Resize(ref s_stagedBits, Mathf.NextPowerOfTwo(w + 1)); }
            ulong bit = 1UL << (slot & 63);
            bool was = (s_stagedBits[w] & bit) != 0;
            if (on == was) return;
            if (on) { s_stagedBits[w] |= bit; s_stagedCount++; } else { s_stagedBits[w] &= ~bit; s_stagedCount--; }
        }

        // AddInstance's slot pick, shared with the commit. A staged slot looks free to the free heap (its batch id is 0), so a stale heap entry for it is dropped here.
        private static int AllocSlot()
        {
            while (s_free.Count > 0)
            {
                int s = s_free.PopBelow(s_high, s_instBatch);
                if (s < 0) break;
                if (!IsStaged(s)) return s;   // a staged one is a stale duplicate: dropped, as PopBelow drops live ones
            }
            int slot = s_high++;
            if (slot >= s_inst.Length)
            {
                int n = Mathf.Max(1024, s_inst.Length * 2);
                System.Array.Resize(ref s_inst, n); System.Array.Resize(ref s_instBatch, n); System.Array.Resize(ref s_instPos, n); System.Array.Resize(ref s_instLod, n);
                IndirectLooks.EnsureCapacity(n);   // A2
                s_bindingsDirty = true;   // the GPU buffers grow at the next frame
            }
            return slot;
        }

        // a staged slot that never flipped: back to the free heap (its batch id is still 0 on both sides)
        private static void ReleaseStagedSlot(int slot)
        {
            SetStaged(slot, false);
            s_free.Push(slot);
            if (CompactOn && slot == s_high - 1) while (s_high > 0 && s_instBatch[s_high - 1] == 0 && !IsStaged(s_high - 1)) s_high--;
        }

        private static void FlipDirty(int slot)
        {
            if (slot < s_flipMin) s_flipMin = slot; if (slot > s_flipMax) s_flipMax = slot;
            int w = slot >> 6;
            if (w >= s_flipBits.Length) System.Array.Resize(ref s_flipBits, Mathf.NextPowerOfTwo(w + 1));
            s_flipBits[w] |= 1UL << (slot & 63);
        }

        // from EndFrame after Upload: the flipped slots' batch ids only (4 bytes a slot; their 112 bytes of instance data went up at commit)
        private static void UploadFlips()
        {
            if (s_flipMax < s_flipMin || s_instBatchBuf == null) return;
            int w0 = s_flipMin >> 6, w1 = Mathf.Min(s_flipMax >> 6, s_flipBits.Length - 1);
            int runStart = -1, runEnd = -1;
            for (int w = w0; w <= w1; w++)
            {
                ulong bits = s_flipBits[w];
                if (bits == 0) continue;
                for (int bit = 0; bit < 64; bit++)
                {
                    if ((bits & (1UL << bit)) == 0) continue;
                    int slot = (w << 6) + bit;
                    if (slot >= s_high) break;
                    if (runStart < 0) { runStart = runEnd = slot; continue; }
                    if (slot - runEnd > SparseGap) { FlipRun(runStart, runEnd - runStart + 1); runStart = slot; }
                    runEnd = slot;
                }
            }
            if (runStart >= 0) FlipRun(runStart, runEnd - runStart + 1);
            System.Array.Clear(s_flipBits, w0, w1 - w0 + 1);
            s_flipMin = int.MaxValue; s_flipMax = -1;
        }

        private static void FlipRun(int start, int n)
        {
            s_instBatchBuf.SetData(s_instBatch, start, start, n);
            s_upRuns++; s_upBytes += n * 4L; s_stFlipUpSlots += n;
        }

        // ---- Take ----
        // From Take after Seen is stamped. True = the group draws through the instanced path this frame (Take returns false).
        private static bool StageTake(ZoneInstanceGroup group, GroupSlots reg)
        {
            var st = reg.Stage;
            if (st == null)
            {
                if (!StagedSyncOn || reg.Version != -1 || reg.Lanes.Count != 0) return false;
                int adds = StageCountAdds(group);
                if (adds < StageMinAdds) { s_stSmall++; return false; }
                BeginStage(group, reg, adds);
                s_stLegacyFrames++;
                return true;
            }
            if (!StagedSyncOn) { CancelStage(reg); return false; }   // key went off: the normal Sync registers it whole now
            if (!st.JobDone || st.Cursor < st.Entries) { s_stLegacyFrames++; return true; }
            if (s_stFlipFrame == s_frame && s_stFlipTicksFrame >= (long)(StageFlipBudgetMs * Stopwatch.Frequency / 1000.0))
            {
                s_stDeferred++; s_stLegacyFrames++;
                RequestFullWalk();   // a settled frame would not call this Take again
                return true;
            }
            Flip(reg, st);
            return false;
        }

        // the adds Sync would make: lanes x instances (every lane walks all of the group's instances)
        private static int StageCountAdds(ZoneInstanceGroup group)
        {
            int n = group.Matrices.Count;
            if (n == 0 || group.Definition == null) return 0;
            var lodParts = group.Definition.LodParts;
            if (lodParts != null) return lodParts.Length * n;
            int lanes = 0;
            var parts = group.Definition.Parts;
            if (parts == null) return 0;
            foreach (var part in parts)
            {
                var lods = part.LodMeshes;
                int levels = lods != null && lods.Length > 0 && part.LodHeights != null && part.LodSize > 0f ? lods.Length : 1;
                for (int level = 0; level < levels; level++) if ((levels == 1 ? part.NearMesh : lods[level]) != null) lanes++;
            }
            return lanes * n;
        }

        private static bool s_stSaid;

        // the lanes exactly as Sync sets them up (same BatchOf / BatchFor calls, same order), the matrices copied, the job scheduled
        private static void BeginStage(ZoneInstanceGroup group, GroupSlots reg, int adds)
        {
            int n = group.Matrices.Count;
            s_stBatchScratch.Clear(); s_stLaneScratch.Clear();
            var lodParts = group.Definition.LodParts;
            if (lodParts != null)
            {
                foreach (var lp in lodParts)
                {
                    var rp = lp.RenderParams;
                    float start = lp.Size > 0f ? lp.Start : float.MaxValue, end = lp.Size > 0f ? lp.End : 0f;
                    var batch = BatchOf(lp.Mesh, lp.SubMesh, EbmIndirectAssets.VariantFor(rp.material), rp.shadowCastingMode, rp.receiveShadows, rp.layer, rp.renderingLayerMask, start, end);
                    s_stBatchScratch.Add(batch);
                    s_stLaneScratch.Add(new StageLane { Kind = 0, LodFromFirst = lp.FromFirst, RefFirst = lp.RefFirst, Size = lp.Size, Lod = Vector4.zero, LodStart = start, LodEnd = end,
                        BatchStart = batch.StartHeight, BatchEnd = batch.EndHeight, LocalBounds = batch.LocalBounds, PerInst = batch.LodPerInstance ? 1 : 0, PartFromFirst = Matrix4x4.identity });
                }
            }
            else
            {
                var parts = group.Definition.Parts;
                for (int p = 0; p < parts.Length; p++)
                {
                    var part = parts[p];
                    var lods = part.LodMeshes;
                    int levels = lods != null && lods.Length > 0 && part.LodHeights != null && part.LodSize > 0f ? lods.Length : 1;
                    for (int level = 0; level < levels; level++)
                    {
                        var mesh = levels == 1 ? part.NearMesh : lods[level];
                        if (mesh == null) continue;
                        var bf = BatchFor(part, mesh, level, levels, out var bLod, out float bStart, out float bEnd);
                        s_stBatchScratch.Add(bf);
                        s_stLaneScratch.Add(new StageLane { Kind = 1, PartOffset = p > 0 ? 1 : 0, PartFromFirst = p > 0 ? part.FromFirst : Matrix4x4.identity, LodFromFirst = Matrix4x4.identity,
                            Lod = bLod, LodStart = bStart, LodEnd = bEnd,   // what Sync passes to SyncLane (and AddInstance uses them only for a per-instance batch)
                            BatchStart = bf.StartHeight, BatchEnd = bf.EndHeight, LocalBounds = bf.LocalBounds, PerInst = bf.LodPerInstance ? 1 : 0 });
                    }
                }
            }
            int lanes = s_stLaneScratch.Count;
            var st = new GroupStage
            {
                Group = group, Version = group.Version, N = n, Entries = lanes * n, Adds = adds, LodParts = lodParts != null, ScheduledFrame = s_frame,
                Batches = s_stBatchScratch.ToArray(),
                Context = "baked group of " + n + " at " + ((Vector3)group.Matrices[0].GetColumn(3)).ToString("F0"),   // Sync's s_addContext, for BadInstance's lines
                M = new NativeArray<Matrix4x4>(n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Lanes = new NativeArray<StageLane>(lanes, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Out = new NativeArray<StageOut>(lanes * n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
                Slot = new NativeArray<int>(lanes * n, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            };
            var ms = group.Matrices;
            for (int i = 0; i < n; i++) st.M[i] = ms[i];
            for (int l = 0; l < lanes; l++) st.Lanes[l] = s_stLaneScratch[l];
            st.Job = new StagePrepJob { M = st.M, Lanes = st.Lanes, N = n, Out = st.Out }.Schedule(st.Entries, 64);
            JobHandle.ScheduleBatchedJobs();
            reg.Stage = st;
            s_stageList.Add(st);
            s_stBegun++; s_stgAdds += adds;
            if (!s_stSaid)
            {
                s_stSaid = true;
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(group.PrefabHash);
                EasyBakeLog.Info($"[Indirect] IndirectStagedSync on: first group staged ('{(prefab != null ? prefab.name : group.PrefabHash.ToString())}', {adds} adds). Groups of {StageMinAdds}+ adds are prepared on a job, filled at most {StageCommitBudgetMs:0.0} ms a frame and flipped in one frame; they draw instanced until then. Numbers: the Debug line '[Indirect] staged sync 30 s'.");
            }
        }

        // one slot: picked, written, still invisible (batch id 0)
        private static void CommitEntry(GroupStage st, int e)
        {
            var o = st.Out[e];
            int lane = e / st.N;
            var batch = st.Batches[lane];
            int slot = AllocSlot();
            st.Slot[e] = slot;
            SetStaged(slot, true);
            if ((o.Flags & SfBad) != 0)
            {
                s_addContext = st.Context;
                if (!BadInstance(batch, StageMatrix(st, lane, o))) s_stBadDisagree++;   // AddInstance's call: logs the same lines
                s_inst[slot] = default; s_instPos[slot] = Vector3.zero; s_instLod[slot] = Vector4.zero;
            }
            else
            {
                if ((o.Flags & (SfScaleNote | SfMatrixNote)) != 0)
                {
                    s_addContext = st.Context;
                    var m = StageMatrix(st, lane, o);
                    if ((o.Flags & SfScaleNote) != 0 && BadInstance(batch, m)) s_stBadDisagree++;   // the Debug "unusual scale, still drawn" line
                    if ((o.Flags & SfMatrixNote) != 0) NoteMatrix(m, m.inverse);
                }
                if (o.SqScale > batch.MaxScale * batch.MaxScale && o.SqScale < 1e8f) batch.MaxScale = Mathf.Sqrt(o.SqScale);   // NoteScale
                s_inst[slot] = o.G;
                s_instPos[slot] = o.Pos;
                s_instLod[slot] = batch.LodPerInstance ? PackLod(o.LodWorld, o.CellStart, o.EndDist) : o.LodWorld;
            }
            MarkDirty(slot);   // uploads with batch id 0: the cull matches it to no batch
        }

        private static Matrix4x4 StageMatrix(GroupStage st, int lane, StageOut o)
        {
            var L = st.Lanes[lane];
            return L.Kind == 0 ? o.Src * L.LodFromFirst : o.Src;
        }

        // the whole group at once: what SyncLane's adds would have left, in the same lane and instance order (looks need it)
        private static void Flip(GroupSlots reg, GroupStage st)
        {
            long t0 = Stopwatch.GetTimestamp();
            int n = st.N;
            reg.Slots.Clear(); reg.SlotInstance.Clear(); reg.Lanes.Clear();
            if (reg.Slots.Capacity < st.Entries) reg.Slots.Capacity = st.Entries;
            if (reg.SlotInstance.Capacity < st.Entries) reg.SlotInstance.Capacity = st.Entries;
            for (int l = 0; l < st.Batches.Length; l++)
            {
                var L = st.Lanes[l];
                var batch = st.Batches[l];
                var ln = new Lane { Batch = batch, Lod = L.Lod, Start = L.LodStart, End = L.LodEnd };   // SyncLane's ln.Lod = bakedLod (zero for a LOD part), Start / End = lodStart / lodEnd
                ln.Mats.Capacity = n; ln.Slots.Capacity = n;
                reg.Lanes.Add(ln);
                uint id = (uint)(batch.Index + 1);
                for (int i = 0; i < n; i++)
                {
                    int e = l * n + i;
                    var o = st.Out[e];
                    int slot = st.Slot[e];
                    SetStaged(slot, false);
                    s_instBatch[slot] = id;
                    FlipDirty(slot);
                    s_live++; s_addsWindow++;
                    batch.Count++;
                    if (batch.Count > batch.Capacity) RequestPlace(batch);   // 1.2.210: only this batch moves
                    if (batch.Count == 1) NoteFirstInstance();
                    if ((o.Flags & SfBad) == 0)
                    {
                        if (!batch.WorldSet) { batch.World = o.Wb; batch.WorldSet = true; } else batch.World.Encapsulate(o.Wb);
                        s_cells.Add(batch.Index, o.Pos, o.Wb.min, o.Wb.max, o.CellEnd, o.CellStart);
                    }
                    ln.Mats.Add(o.Src); ln.Slots.Add(slot);
                    reg.Slots.Add(slot); reg.SlotInstance.Add(i);
                }
            }
            reg.Version = st.Version;   // changed since the snapshot: Take's Sync now handles just the difference
            if (st.LodParts) s_lodPartGroups++;
            reg.Stage = null;
            s_stageList.Remove(st);
            DisposeStage(st);
            long ticks = Stopwatch.GetTimestamp() - t0;
            if (s_stFlipFrame != s_frame) { s_stFlipFrame = s_frame; s_stFlipTicksFrame = 0L; s_stFlipGroupsFrame = 0; }
            s_stFlipTicksFrame += ticks; s_stFlipGroupsFrame++;
            s_stFlips++; s_stFlipTicks += ticks; s_stFlipAdds += st.Entries;
            if (ticks > s_stFlipWorstGroupTicks) { s_stFlipWorstGroupTicks = ticks; s_stFlipWorstPrefab = st.Group.PrefabHash; s_stFlipWorstAdds = st.Entries; }
        }

        // Drop / hand-back / key off: the job finished or waited for, the committed slots returned, the native memory freed
        private static void CancelStage(GroupSlots reg)
        {
            var st = reg.Stage;
            if (st == null) return;
            if (!st.JobDone) { st.Job.Complete(); st.JobDone = true; }
            for (int e = 0; e < st.Cursor; e++) ReleaseStagedSlot(st.Slot[e]);
            reg.Stage = null;
            s_stageList.Remove(st);
            DisposeStage(st);
            s_stCancels++;
        }

        private static void DisposeStage(GroupStage st)
        {
            if (st.M.IsCreated) st.M.Dispose();
            if (st.Lanes.IsCreated) st.Lanes.Dispose();
            if (st.Out.IsCreated) st.Out.Dispose();
            if (st.Slot.IsCreated) st.Slot.Dispose();
        }

        // from ReleaseAll: every slot array is cleared there, so only the jobs and the native memory matter here
        private static void StageReleaseAll()
        {
            foreach (var st in s_stageList)
            {
                if (!st.JobDone) { st.Job.Complete(); st.JobDone = true; }
                DisposeStage(st);
            }
            s_stageList.Clear();
            System.Array.Clear(s_stagedBits, 0, s_stagedBits.Length); s_stagedCount = 0;
            System.Array.Clear(s_flipBits, 0, s_flipBits.Length); s_flipMin = int.MaxValue; s_flipMax = -1;
        }

        // ---- EndFrame: finished jobs, then commits under the budget ----
        private static void StageTick()
        {
            if (s_stageList.Count > 0)
            {
                long entries0 = s_stCommitEntries;
                long t0 = Stopwatch.GetTimestamp(), budget = (long)(StageCommitBudgetMs * Stopwatch.Frequency / 1000.0);
                bool spent = false;
                for (int k = 0; k < s_stageList.Count && !spent; k++)
                {
                    var st = s_stageList[k];
                    if (!st.JobDone)
                    {
                        if (!st.Job.IsCompleted) continue;
                        st.Job.Complete(); st.JobDone = true;
                        int wait = s_frame - st.ScheduledFrame;
                        s_stJobs++; s_stWaitFrames += wait; if (wait > s_stWaitMax) s_stWaitMax = wait;
                    }
                    if (st.Cursor >= st.Entries) continue;
                    while (st.Cursor < st.Entries)
                    {
                        CommitEntry(st, st.Cursor++);
                        s_stCommitEntries++;
                        if ((st.Cursor & 31) == 0 && Stopwatch.GetTimestamp() - t0 >= budget) { spent = true; break; }
                    }
                    if (st.Cursor >= st.Entries) RequestFullWalk();   // ready: the next frame's walk calls its Take, which flips it
                }
                long frameCommit = Stopwatch.GetTimestamp() - t0;
                if (s_stCommitEntries > entries0) { s_stCommitFrames++; s_stCommitTicks += frameCommit; if (frameCommit > s_stCommitWorst) s_stCommitWorst = frameCommit; }
            }
            if (s_stFlipFrame == s_frame && s_stFlipTicksFrame > s_stFlipWorstFrame) { s_stFlipWorstFrame = s_stFlipTicksFrame; s_stFlipWorstFrameGroups = s_stFlipGroupsFrame; }
            StageLog();
        }

        // ---- the timer line (Debug, every 30 s with activity) ----
        private static int s_stBegun, s_stFlips, s_stCancels, s_stSmall, s_stDeferred, s_stLegacyFrames, s_stJobs, s_stWaitFrames, s_stWaitMax, s_stCommitFrames, s_stBadDisagree;
        private static long s_stgAdds, s_stFlipAdds, s_stCommitEntries, s_stCommitTicks, s_stCommitWorst, s_stFlipTicks, s_stFlipWorstFrame, s_stFlipWorstGroupTicks, s_stFlipUpSlots;
        private static int s_stFlipFrame = -1, s_stFlipGroupsFrame, s_stFlipWorstFrameGroups, s_stFlipWorstPrefab, s_stFlipWorstAdds;
        private static long s_stFlipTicksFrame;
        private static float s_stNextLogAt;

        private static void StageLog()
        {
            if (Time.unscaledTime < s_stNextLogAt) return;
            if (s_stBegun == 0 && s_stFlips == 0 && s_stCancels == 0 && s_stSmall == 0 && s_stageList.Count == 0) return;
            s_stNextLogAt = Time.unscaledTime + 30f;
            if (EasyBakeLog.DebugOn())
            {
                double toMs = 1000.0 / Stopwatch.Frequency;
                var prefab = s_stFlips > 0 ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(s_stFlipWorstPrefab) : null;
                var sb = new System.Text.StringBuilder(640);
                sb.Append($"[Indirect] staged sync 30 s: {s_stBegun} group(s) staged ({s_stgAdds} adds), {s_stFlips} flipped, {s_stCancels} cancelled, {s_stSmall} first registration(s) under {StageMinAdds} adds left to Sync, {s_stageList.Count} in flight now ({s_stagedCount} slots staged)");
                sb.Append($"; job ready after {(s_stJobs > 0 ? (double)s_stWaitFrames / s_stJobs : 0.0):0.0} frame(s) on average, {s_stWaitMax} at most");
                sb.Append($"; commit {s_stCommitTicks * toMs:0.0} ms over {s_stCommitFrames} frame(s), worst frame {s_stCommitWorst * toMs:0.00} ms (budget {StageCommitBudgetMs:0.0}), {(s_stCommitEntries > 0 ? s_stCommitTicks * toMs * 1000.0 / s_stCommitEntries : 0.0):0.00} us a slot");
                sb.Append($"; flip {s_stFlipTicks * toMs:0.0} ms in all, {(s_stFlipAdds > 0 ? s_stFlipTicks * toMs * 1000.0 / s_stFlipAdds : 0.0):0.00} us a slot, worst frame {s_stFlipWorstFrame * toMs:0.00} ms ({s_stFlipWorstFrameGroups} group(s)), slowest group '{(prefab != null ? prefab.name : s_stFlipWorstPrefab.ToString())}' {s_stFlipWorstGroupTicks * toMs:0.00} ms for {s_stFlipWorstAdds} slots, {s_stFlipUpSlots} batch ids uploaded");
                sb.Append($"; {s_stDeferred} flip(s) waited a frame for the flip budget, {s_stLegacyFrames} group-frame(s) drawn instanced while staging, {s_stBadDisagree} job/main bad-instance disagreement(s)");
                EasyBakeLog.Debug(sb.ToString());
            }
            s_stBegun = s_stFlips = s_stCancels = s_stSmall = s_stDeferred = s_stLegacyFrames = s_stJobs = s_stWaitFrames = s_stWaitMax = s_stCommitFrames = s_stBadDisagree = 0;
            s_stgAdds = s_stFlipAdds = s_stCommitEntries = s_stCommitTicks = s_stCommitWorst = s_stFlipTicks = s_stFlipWorstFrame = s_stFlipWorstGroupTicks = s_stFlipUpSlots = 0L;
            s_stFlipWorstFrameGroups = s_stFlipWorstPrefab = s_stFlipWorstAdds = 0;
        }
    }
}
