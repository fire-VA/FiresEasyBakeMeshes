using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Every owner's far instances in ONE GPU buffer (32-byte structs), first-fit ranges per cell (SPEC §4). SetCell copies the array,
    /// takes a new range and uploads it in slices under [Far Impostors] Upload budget ms; the cell's old range keeps drawing until the
    /// new one is complete, then it is dead-filled (kind -1, the cull skips it) in slices and only then freed: no flicker, no gap. The
    /// buffer starts at 64k instances (2 MB, dead-filled at once) and doubles on demand. 0.2.1204 (Fire's 0108e log: every FAT SetCell threw
    /// "CopyBuffer source and destination buffers must be the same size"): Graphics.CopyBuffer only copies equal-sized buffers, so a grow
    /// never uses it; the pool keeps a CPU mirror of every slot it writes and a grown buffer is filled from that mirror in one upload (one
    /// Debug line per grow). Per-kind live counts feed the mid-LOD segments.
    /// </summary>
    internal static class ImpostorPool
    {
        private sealed class Range { public int Start, Count; }
        private sealed class Cell
        {
            public string Owner; public long Key; public Bounds Bounds;
            public Range Live, Pending; public ImpostorInstance[] Data; public int Uploaded;
            public int[] KindCounts;   // of the live range
        }
        private sealed class DeadFill { public Range Range; public int Done; }

        internal static GraphicsBuffer Buffer;
        private static ImpostorInstance[] s_mirror = new ImpostorInstance[0];   // what the GPU buffer holds, slot for slot
        internal static int Capacity, HighWater;
        private static readonly Dictionary<(string, long), Cell> s_cells = new Dictionary<(string, long), Cell>();
        private static readonly List<Range> s_free = new List<Range>();
        private static readonly List<Cell> s_uploads = new List<Cell>();
        private static readonly List<DeadFill> s_dead = new List<DeadFill>();
        private static ImpostorInstance[] s_deadChunk;
        internal static int[] KindCounts = new int[0];
        internal static bool CountsChanged;
        private static readonly Stopwatch s_sw = new Stopwatch();
        internal static double SpentMs; internal static long Uploaded;
        private const int Chunk = 4096;

        internal static object SetCell(string owner, long key, Bounds bounds, ImpostorInstance[] data, int count)
        {
            owner = owner ?? "?";
            count = Math.Max(0, Math.Min(count, data?.Length ?? 0));
            if (!s_cells.TryGetValue((owner, key), out var c)) s_cells[(owner, key)] = c = new Cell { Owner = owner, Key = key };
            c.Bounds = bounds;
            if (c.Pending != null) { s_uploads.Remove(c); Free(c.Pending); c.Pending = null; }   // a newer replacement wins
            if (count == 0) { Retire(c); return c; }
            var copy = new ImpostorInstance[count]; Array.Copy(data, copy, count);
            var cam = Camera.main; Vector3 cp = cam != null ? cam.transform.position : Vector3.zero;
            for (int i = 0; i < count; i++)
            {
                int k = copy[i].Kind; if (k < 0) continue;
                var kind = ImpostorKinds.Get(k); if (kind == null) { copy[i].Kind = -1; continue; }
                float d = (copy[i].Position - cp).magnitude;
                if (d < kind.NeedDistance) kind.NeedDistance = d;
            }
            c.Data = copy; c.Uploaded = 0; c.Pending = Alloc(count);
            s_uploads.Add(c);
            return c;
        }

        internal static void ClearCell(string owner, long key)
        {
            if (!s_cells.TryGetValue((owner ?? "?", key), out var c)) return;
            if (c.Pending != null) { s_uploads.Remove(c); Free(c.Pending); c.Pending = null; }
            Retire(c); s_cells.Remove((owner ?? "?", key));
        }

        internal static void ClearOwner(string owner)
        {
            var keys = new List<(string, long)>();
            foreach (var kv in s_cells) if (kv.Key.Item1 == (owner ?? "?")) keys.Add(kv.Key);
            foreach (var k in keys) ClearCell(k.Item1, k.Item2);
        }

        private static void Retire(Cell c)
        {
            if (c.Live != null) { Free(c.Live); SubCounts(c); c.Live = null; }
        }

        // a range that held (or may hold) instances: dead-filled in slices, then back on the free list (never re-used while live data is in it)
        private static void Free(Range r) => s_dead.Add(new DeadFill { Range = r });

        private static Range Alloc(int count)
        {
            EnsureBuffer(count);
            for (int i = 0; i < s_free.Count; i++)
            {
                var f = s_free[i];
                if (f.Count < count) continue;
                var r = new Range { Start = f.Start, Count = count };
                f.Start += count; f.Count -= count; if (f.Count == 0) s_free.RemoveAt(i);
                HighWater = Math.Max(HighWater, r.Start + r.Count);
                return r;
            }
            Grow(Math.Max(Capacity * 2, Capacity + count));
            return Alloc(count);
        }

        private static void Release(Range r)
        {
            if (r.Count <= 0) return;
            s_free.Add(new Range { Start = r.Start, Count = r.Count });
            s_free.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = s_free.Count - 2; i >= 0; i--)
                if (s_free[i].Start + s_free[i].Count == s_free[i + 1].Start) { s_free[i].Count += s_free[i + 1].Count; s_free.RemoveAt(i + 1); }
            // the high water drops when the top of the buffer is free
            var top = s_free[s_free.Count - 1];
            if (top.Start + top.Count >= Capacity) HighWater = Math.Min(HighWater, top.Start);
        }

        private static void EnsureBuffer(int need)
        {
            if (Buffer != null) return;
            Capacity = Math.Max(65536, need);
            Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, ImpostorInstance.Stride) { name = "FiresEBM_FarInstances" };
            DeadNow(0, Capacity);
            s_free.Clear(); s_free.Add(new Range { Start = 0, Count = Capacity }); HighWater = 0;
            ImpostorDraw.PoolResized();
        }

        private static void Grow(int cap)
        {
            int old = Capacity;
            var mirror = new ImpostorInstance[cap];
            Array.Copy(s_mirror, mirror, old);
            for (int i = old; i < cap; i++) mirror[i].Kind = -1;
            s_mirror = mirror;
            var nb = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, ImpostorInstance.Stride) { name = "FiresEBM_FarInstances" };
            nb.SetData(s_mirror, 0, 0, cap);   // never Graphics.CopyBuffer: it needs equal sizes
            Buffer.Release(); Buffer = nb; Capacity = cap;
            Release(new Range { Start = old, Count = cap - old });
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"instance pool grew to {cap} ({cap * ImpostorInstance.Stride / (1024 * 1024)} MB)");
            ImpostorDraw.PoolResized();
        }

        // a new buffer's (or a grown half's) contents are undefined: dead-fill before anything is allocated there
        private static void DeadNow(int start, int count)
        {
            if (s_mirror.Length < start + count) Array.Resize(ref s_mirror, start + count);
            for (int i = start; i < start + count; i++) s_mirror[i] = new ImpostorInstance { Kind = -1 };
            Buffer.SetData(s_mirror, start, start, count);
        }

        /// <summary>Main thread, each frame: dead fills, then uploads, inside the budget.</summary>
        internal static void Upload()
        {
            if (Buffer == null) return;
            s_sw.Restart();
            double budget = ImpostorEngine.UploadBudgetMs;
            while (s_dead.Count > 0 && s_sw.Elapsed.TotalMilliseconds < budget)
            {
                var d = s_dead[0];
                if (s_deadChunk == null) { s_deadChunk = new ImpostorInstance[Chunk]; for (int i = 0; i < Chunk; i++) s_deadChunk[i].Kind = -1; }
                int n = Math.Min(Chunk, d.Range.Count - d.Done);
                if (n > 0) { Buffer.SetData(s_deadChunk, 0, d.Range.Start + d.Done, n); Array.Copy(s_deadChunk, 0, s_mirror, d.Range.Start + d.Done, n); }
                d.Done += n;
                if (d.Done >= d.Range.Count) { s_dead.RemoveAt(0); Release(d.Range); }
            }
            while (s_uploads.Count > 0 && s_sw.Elapsed.TotalMilliseconds < budget)
            {
                var c = s_uploads[0];
                int n = Math.Min(Chunk, c.Data.Length - c.Uploaded);
                Buffer.SetData(c.Data, c.Uploaded, c.Pending.Start + c.Uploaded, n);
                Array.Copy(c.Data, c.Uploaded, s_mirror, c.Pending.Start + c.Uploaded, n);
                c.Uploaded += n; Uploaded += n;
                if (c.Uploaded < c.Data.Length) continue;
                s_uploads.RemoveAt(0);
                Retire(c);                       // the old range goes now that the new one is complete
                c.Live = c.Pending; c.Pending = null;
                c.KindCounts = CountKinds(c.Data); AddCounts(c);
                c.Data = null;
                ImpostorEngine.RaiseInstancesChanged();
            }
            SpentMs += s_sw.Elapsed.TotalMilliseconds;
        }

        private static int[] CountKinds(ImpostorInstance[] data)
        {
            var counts = new int[Math.Max(1, ImpostorKinds.Count)];
            foreach (var i in data) if (i.Kind >= 0 && i.Kind < counts.Length) counts[i.Kind]++;
            return counts;
        }

        private static void AddCounts(Cell c)
        {
            if (c.KindCounts == null) return;
            if (KindCounts.Length < c.KindCounts.Length) Array.Resize(ref KindCounts, c.KindCounts.Length);
            for (int i = 0; i < c.KindCounts.Length; i++) KindCounts[i] += c.KindCounts[i];
            CountsChanged = true;
        }

        private static void SubCounts(Cell c)
        {
            if (c.KindCounts == null) return;
            for (int i = 0; i < c.KindCounts.Length && i < KindCounts.Length; i++) KindCounts[i] -= c.KindCounts[i];
            c.KindCounts = null; CountsChanged = true;
        }

        internal static int LiveInstances { get { int n = 0; foreach (var c in s_cells.Values) if (c.Live != null) n += c.Live.Count; return n; } }

        internal static void Clear()
        {
            s_cells.Clear(); s_free.Clear(); s_uploads.Clear(); s_dead.Clear();
            Buffer?.Release(); Buffer = null; Capacity = 0; HighWater = 0; KindCounts = new int[0]; CountsChanged = true; s_mirror = new ImpostorInstance[0];
        }

        internal static string StatusText() => $"instances {LiveInstances} in {s_cells.Count} cell(s), pool {Capacity} (high water {HighWater}), uploads pending {s_uploads.Count}";
    }
}
