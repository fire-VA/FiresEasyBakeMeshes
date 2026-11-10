using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>24-byte far vertex: position f32x3, normal and tangent SNorm8x4, uv f16x2.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FarVert { public Vector3 P; public uint N; public uint T; public uint UV; }

    /// <summary>One draw: a material, a snow level, and up to 65k vertices (16-bit indices).</summary>
    internal sealed class FarChunk
    {
        public int MatIdx; public byte Snow; public FarVert[] Verts; public ushort[] Idx; public Bounds Bounds;
        public int[] SegRun, SegStart, SegCount;   // [Optimize] FarIndexRanges: per piece run, its index range in Idx (even start and length); null when the job does not track runs
        public int VertCount, IdxCount;   // [Optimize] FarMergePooled: the used length of Verts / Idx (pooled arrays are longer); equal to the lengths otherwise
        public bool Pooled;               // Verts / Idx came from FarCapPool and go back after upload
    }

    /// <summary>The work for one target (a 64 m zone or a 512 m cell): pieces grouped by (material, snow) on the main thread.</summary>
    internal sealed class FarJob
    {
        public long TargetKey; public int Generation; public bool Simplify;
        public Material[] Mats;
        public List<Bucket> Buckets = new List<Bucket>();
        public List<FarChunk> Result; public long Ticks;
        public bool TrackRuns; public List<long> RunKeys; public List<Vector2s> RunZones;   // [Optimize] FarIndexRanges: one run per piece (key + zone), set on the main thread at prep
        public bool Pooled;   // [Optimize] FarMergePooled, set at prep: the worker reuses its lists and rents chunk arrays

        internal sealed class Bucket
        {
            public int MatIdx; public byte Snow;
            public List<Item> Items = new List<Item>();
        }
        internal struct Item { public FarPart Part; public int Sub; public Matrix4x4 M; public bool Mirror; public int Run; }   // Mirror: determinant < 0, set on the main thread; Run: the piece's run (-1 = not tracked)
    }

    /// <summary>
    /// The merge runs on ONE background thread at below-normal priority, on plain arrays only (no Unity calls): it transforms each
    /// piece's cached far mesh into the target's space and appends it to its (material, snow) stream, splitting at 65k vertices.
    /// Cells past "Cell merge from" are grid-clustered to about 1 m, keeping faces apart by normal direction so walls stay walls.
    /// The main thread only queues jobs and uploads finished ones (FarBuildingDraw).
    /// </summary>
    internal static class FarBuildingMerge
    {
        private const int MaxChunkVerts = 65000;
        private const float ClusterSize = 1f;

        private static readonly Queue<FarJob> s_in = new Queue<FarJob>();
        private static readonly Queue<FarJob> s_out = new Queue<FarJob>();
        private static readonly object s_lock = new object();
        private static Thread s_thread, s_prevThread;
        // 1258 review (far3 MEDIUM): one token per start. A stopped worker sees its token gone at the next check and exits, dropping any result it was merging,
        // so a quick stop + start never has two workers queueing results; its pooled lists are thread-local (t_w*), so an old thread still finishing a merge
        // never shares them with the new one.
        private static volatile object s_token;
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);

        internal static void StartWorker()
        {
            if (s_thread != null) return;
            var prev = s_prevThread; s_prevThread = null;
            if (prev != null && prev.IsAlive && !prev.Join(1000))
                EasyBakeLog.Debug($"{FarBuildings.Log} the previous merge worker is still finishing a merge; it drops its result when done (its lists are its own)");
            var token = new object();
            s_token = token;
            s_thread = new Thread(() => Run(token)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "FiresEBM FarBuildings merge" };
            s_thread.Start();
        }

        internal static void StopWorker()
        {
            s_token = null; s_wake.Set();
            s_prevThread = s_thread; s_thread = null;
            lock (s_lock) { s_in.Clear(); s_out.Clear(); }
        }

        internal static void Queue(FarJob job) { lock (s_lock) s_in.Enqueue(job); s_wake.Set(); }

        internal static bool TryTake(out FarJob job) { lock (s_lock) { if (s_out.Count > 0) { job = s_out.Dequeue(); return true; } } job = null; return false; }

        internal static int Waiting { get { lock (s_lock) return s_in.Count; } }

        private static void Run(object token)
        {
            while (ReferenceEquals(s_token, token))
            {
                FarJob job = null;
                lock (s_lock) if (s_in.Count > 0 && ReferenceEquals(s_token, token)) job = s_in.Dequeue();
                if (job == null) { s_wake.WaitOne(500); continue; }   // an old worker that takes a wake meant for the new one exits on the token; the new one waits at most 500 ms
                long t0 = Stopwatch.GetTimestamp();
                try { job.Result = Merge(job); }
                catch (Exception e) { job.Result = new List<FarChunk>(); EasyBakeLog.Debug($"{FarBuildings.Log} merge failed for target {job.TargetKey}: {e.Message}"); }
                job.Ticks = Stopwatch.GetTimestamp() - t0;
                lock (s_lock)
                {
                    if (!ReferenceEquals(s_token, token)) { FarCapPool.ReturnRange(job.Result, 0); break; }   // stopped meanwhile: the result is dropped, pooled arrays go back
                    s_out.Enqueue(job);
                }
            }
        }

        private static List<FarChunk> Merge(FarJob job)
        {
            var chunks = new List<FarChunk>();
            // [Optimize] FarMergePooled: the one worker thread keeps its lists between jobs (their capacity stays at the largest chunk seen, at most 65k vertices)
            bool pooled = job.Pooled;
            var verts = pooled ? s_wVerts : new List<FarVert>(8192);
            var idx = pooled ? s_wIdx : new List<ushort>(16384);
            var remap = pooled ? s_wRemap : new Dictionary<int, int>();
            var cluster = job.Simplify ? (pooled ? s_wCluster : new Dictionary<long, int>()) : null;
            if (pooled) { verts.Clear(); idx.Clear(); remap.Clear(); cluster?.Clear(); }
            var runs = job.TrackRuns ? new RunRecorder() : null;   // [Optimize] FarIndexRanges; null = exactly the old output

            foreach (var b in job.Buckets)
            {
                verts.Clear(); idx.Clear(); cluster?.Clear();
                foreach (var it in b.Items)
                {
                    var part = it.Part;
                    if (it.Sub >= part.Sub.Length) continue;
                    var tri = part.Sub[it.Sub];
                    if (tri.Length == 0) continue;
                    var m = it.M;
                    remap.Clear();
                    runs?.Begin(it.Run, idx);
                    for (int t = 0; t + 2 < tri.Length; t += 3)
                    {
                        // Start a new chunk before this triangle could overflow 16-bit indices.
                        if (verts.Count + 3 > MaxChunkVerts) { runs?.Close(idx); Flush(chunks, b, verts, idx, runs, pooled); remap.Clear(); cluster?.Clear(); runs?.Reopen(); }
                        int a = Vert(part, tri[t], m, it.Mirror, verts, remap, cluster);
                        int c = Vert(part, tri[t + 1], m, it.Mirror, verts, remap, cluster);
                        int d = Vert(part, tri[t + 2], m, it.Mirror, verts, remap, cluster);
                        if (a == c || c == d || a == d) continue;   // collapsed by clustering
                        idx.Add((ushort)a); idx.Add((ushort)c); idx.Add((ushort)d);
                    }
                    if (runs != null) { runs.Close(idx); runs.End(); }
                }
                Flush(chunks, b, verts, idx, runs, pooled);
            }
            return chunks;
        }

        // [Optimize] FarMergePooled: the worker's own lists (Run is the only caller, on the one worker thread)
        // 1258 review: per THREAD, so an old worker still finishing after a stop never writes into the new worker's lists
        [ThreadStatic] private static List<FarVert> t_wVerts;
        [ThreadStatic] private static List<ushort> t_wIdx;
        [ThreadStatic] private static Dictionary<int, int> t_wRemap;
        [ThreadStatic] private static Dictionary<long, int> t_wCluster;
        private static List<FarVert> s_wVerts => t_wVerts ?? (t_wVerts = new List<FarVert>(MaxChunkVerts));
        private static List<ushort> s_wIdx => t_wIdx ?? (t_wIdx = new List<ushort>(MaxChunkVerts * 2));
        private static Dictionary<int, int> s_wRemap => t_wRemap ?? (t_wRemap = new Dictionary<int, int>());
        private static Dictionary<long, int> s_wCluster => t_wCluster ?? (t_wCluster = new Dictionary<long, int>());

        // [Optimize] FarIndexRanges: each item's triangles are one run segment in the chunk they land in. A run starts at an even index and has an even
        // length (a 0,0,0 triangle pads it: it draws nothing), so the main thread can zero it in whole 32-bit words. Worker thread only, one per job.
        private sealed class RunRecorder
        {
            private int m_run = -1, m_start;
            public readonly List<int> Run = new List<int>(), Start = new List<int>(), Count = new List<int>();

            public void Begin(int run, List<ushort> idx)
            {
                m_run = run;
                if (run < 0) return;
                if ((idx.Count & 1) != 0) Pad(idx);   // odd only after triangles, so vertex 0 exists
                m_start = idx.Count;
            }

            public void Close(List<ushort> idx)
            {
                if (m_run < 0) return;
                int n = idx.Count - m_start;
                if (n <= 0) return;
                if ((n & 1) != 0) { Pad(idx); n += 3; }
                Run.Add(m_run); Start.Add(m_start); Count.Add(n);
            }

            public void Reopen() { m_start = 0; }   // a new chunk: the same run goes on from index 0
            public void End() { m_run = -1; }

            private static void Pad(List<ushort> idx) { idx.Add(0); idx.Add(0); idx.Add(0); }
        }

        private static int Vert(FarPart part, int i, Matrix4x4 m, bool mirror, List<FarVert> verts, Dictionary<int, int> remap, Dictionary<long, int> cluster)
        {
            if (remap.TryGetValue(i, out int got)) return got;
            Vector3 p = m.MultiplyPoint3x4(part.Pos[i]);
            Vector3 n = m.MultiplyVector(part.Nrm[i]); float nl = n.magnitude; n = nl > 1e-6f ? n / nl : Vector3.up;
            int at;
            if (cluster != null)
            {
                long key = EbmMeshSimplify.ClusterKey(p, n, ClusterSize);   // 0.2.1212: the one simplifier key (EBM port: EBM's own EbmMeshSimplify, also the rock LOD1)
                if (cluster.TryGetValue(key, out at)) { remap[i] = at; return at; }
                at = Add(part, i, m, mirror, p, n, verts);
                cluster[key] = at;
            }
            else at = Add(part, i, m, mirror, p, n, verts);
            remap[i] = at;
            return at;
        }

        private static int Add(FarPart part, int i, Matrix4x4 m, bool mirror, Vector3 p, Vector3 n, List<FarVert> verts)
        {
            Vector4 t4 = part.Tan[i];
            Vector3 t = m.MultiplyVector(new Vector3(t4.x, t4.y, t4.z)); float tl = t.magnitude; t = tl > 1e-6f ? t / tl : Vector3.right;
            float w = t4.w < 0f ? -1f : 1f;
            if (mirror) w = -w;   // mirrored pieces keep correct bitangents
            Vector2 uv = part.Uv[i];
            verts.Add(new FarVert { P = p, N = Pack(n.x, n.y, n.z, 0f), T = Pack(t.x, t.y, t.z, w), UV = Half(uv.x) | (Half(uv.y) << 16) });
            return verts.Count - 1;
        }

        // Plain C# float -> half (the worker never calls into Unity's native code). Round-to-nearest, clamps to +-65504, no NaN.
        private static uint Half(float f)
        {
            uint x = (uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
            uint sign = (x >> 16) & 0x8000u;
            int exp = (int)((x >> 23) & 0xFF) - 127 + 15;
            uint mant = x & 0x7FFFFFu;
            if (exp <= 0) return sign;                       // too small: zero (UVs never need subnormals)
            if (exp >= 31) return sign | 0x7BFFu;            // too large: max half
            uint h = sign | ((uint)exp << 10) | (mant >> 13);
            if ((mant & 0x1000u) != 0) h++;                  // round
            return h;
        }

        private static uint Pack(float x, float y, float z, float w) =>
            (uint)(byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(x * 127f), -127, 127)
            | ((uint)(byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(y * 127f), -127, 127) << 8)
            | ((uint)(byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(z * 127f), -127, 127) << 16)
            | ((uint)(byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(w * 127f), -127, 127) << 24);

        private static void Flush(List<FarChunk> chunks, FarJob.Bucket b, List<FarVert> verts, List<ushort> idx, RunRecorder runs = null, bool pooled = false)
        {
            if (verts.Count > 0 && idx.Count > 0)
            {
                Vector3 min = verts[0].P, max = verts[0].P;
                for (int i = 1; i < verts.Count; i++) { min = Vector3.Min(min, verts[i].P); max = Vector3.Max(max, verts[i].P); }
                var bounds = new Bounds(); bounds.SetMinMax(min, max);
                FarChunk chunk;
                if (pooled)
                {
                    // [Optimize] FarMergePooled: rented arrays (power-of-two, longer than the counts), filled with CopyTo: no ToArray per chunk
                    var va = FarCapPool.Verts.Rent(verts.Count); verts.CopyTo(va);
                    var ia = FarCapPool.Idx.Rent(idx.Count); idx.CopyTo(ia);
                    chunk = new FarChunk { MatIdx = b.MatIdx, Snow = b.Snow, Verts = va, VertCount = verts.Count, Idx = ia, IdxCount = idx.Count, Bounds = bounds, Pooled = true };
                }
                else
                {
                    var arr = verts.ToArray(); var ix = idx.ToArray();
                    chunk = new FarChunk { MatIdx = b.MatIdx, Snow = b.Snow, Verts = arr, VertCount = arr.Length, Idx = ix, IdxCount = ix.Length, Bounds = bounds };
                }
                if (runs != null) { chunk.SegRun = runs.Run.ToArray(); chunk.SegStart = runs.Start.ToArray(); chunk.SegCount = runs.Count.ToArray(); }
                chunks.Add(chunk);
            }
            if (runs != null) { runs.Run.Clear(); runs.Start.Clear(); runs.Count.Clear(); }
            verts.Clear(); idx.Clear();
        }
    }
}

