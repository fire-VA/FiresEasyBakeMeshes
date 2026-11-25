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
        public float Cluster = 1f;   // 1.2.353: the simplify grid (m): 1 for cells, [Far buildings] Coarse cluster for the coarse tier
        public Material[] Mats;
        public List<Bucket> Buckets = new List<Bucket>();
        public List<FarChunk> Result; public long Ticks;
        public bool TrackRuns; public List<long> RunKeys; public List<Vector2s> RunZones;   // [Optimize] FarIndexRanges: one run per piece (key + zone), set on the main thread at prep
        public bool Pooled;   // [Optimize] FarMergePooled, set at prep: the worker reuses its lists and rents chunk arrays
        public int SplitCount = -1;   // 1.2.429: a loaded zone's copy holds only the pieces whose real LOD is culled: how many (-1 = a full copy)
        public ulong SplitSig;        // 1.2.463: the split copy's membership signature (FarBuildingDraw.SigOf), compared by Plan instead of the count alone
        public List<int> RunPrefabs; public List<Vector3> RunPos;   // 1.2.463: per run (same index as RunKeys) the piece's prefab and position, for the per-piece hand-off
        public bool HandRuns;         // 1.2.463 measure: runs tracked for the per-piece hand-off (its range table build is counted)
        public HashSet<long> InKeys;  // 1.2.463: a split copy built WITHOUT runs: the pieces it holds (FarBuildingDraw.StandKey), for the whole-copy fallback
        public int[] MatRefs; public string CacheKey;   // 1.2.425 FarMergedCache: per material (prefab, part, sub); the merged-cache key to save the result under (null = not saved)

        internal sealed class Bucket
        {
            public int MatIdx; public byte Snow;
            public List<Item> Items = new List<Item>();
        }
        internal struct Item { public FarPart Part; public int Sub; public Matrix4x4 M; public bool Mirror; public int Run; public int Piece; }   // Piece 1.2.431: index into Boxes (-1 = no box)

        // 1.2.431 interior cut: one oriented box per piece (target-local centre, rotation, half extents of its far look), filled on the main thread at prep
        internal struct PieceBox { public Vector3 C; public Quaternion R; public Vector3 H; }
        public List<PieceBox> Boxes;              // null = no interior cut for this job
        public int InteriorPieces, InteriorTris;  // results: pieces left out as enclosed, triangles saved (written by the worker)   // Mirror: determinant < 0, set on the main thread; Run: the piece's run (-1 = not tracked)
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
        internal static void Deliver(FarJob job) { lock (s_lock) s_out.Enqueue(job); }   // 1.2.425: a merged-cache hit goes straight to the upload queue

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
                // 1.2.425 FarMergedCache: saved from the worker, before the pooled arrays go back after upload
                if (job.CacheKey != null && job.Result != null && job.Result.Count > 0) { try { var bytes = FarMergedCache.Serialize(job); if (bytes != null) { FarCache.Write(FarCacheScope.World, FarMergedCache.Bucket, job.CacheKey, bytes); System.Threading.Interlocked.Increment(ref FarMergedCache.Writes); } } catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} merged cache save failed: {e.Message}"); } }
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
            t_cluster = job.Cluster > 0f ? job.Cluster : ClusterSize;   // 1.2.353: this job's grid (worker thread)
            if (pooled) { verts.Clear(); idx.Clear(); remap.Clear(); cluster?.Clear(); }
            var runs = job.TrackRuns ? (t_runs ?? (t_runs = new RunRecorder())).Reset() : null;   // [Optimize] FarIndexRanges; null = exactly the old output. 1.2.287: one per worker thread, reused

            bool[] interior = job.Boxes != null && job.Boxes.Count >= MinPiecesForInterior ? Interior(job.Boxes) : null;   // 1.2.431
            if (interior != null) { int n = 0; foreach (bool x in interior) if (x) n++; job.InteriorPieces = n; }
            foreach (var b in job.Buckets)
            {
                verts.Clear(); idx.Clear(); cluster?.Clear();
                foreach (var it in b.Items)
                {
                    var part = it.Part;
                    if (it.Sub >= part.Sub.Length) continue;
                    var tri = part.Sub[it.Sub];
                    if (tri.Length == 0) continue;
                    if (interior != null && it.Piece >= 0 && it.Piece < interior.Length && interior[it.Piece]) { job.InteriorTris += tri.Length / 3; continue; }   // 1.2.431: enclosed, nobody can see it
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

        // ── 1.2.431 ([visual], lead approved; Fire: "some of the far pieces would be naturally covered and not shown"): a piece is ENCLOSED when a ray from its centre in
        // each of 10 directions (+-x, +-y, +-z and the four horizontal diagonals) enters ANOTHER piece's oriented box (its far look's bounds, at its own rotation, so a
        // rotated wall never covers open air the way a world box would) in front of it. Boxes that contain the start point do not count (overlapping pieces never hide
        // each other), and a ray that leaves the copy's bounds is open. A piece visible from outside has at least its outward direction free, so the outer shell always
        // stays; only pieces boxed in on every side go (inner walls, floors under a roof, stacked foundations). Uniform 4 m grid of box ids; plain arrays, worker thread.
        internal const int MinPiecesForInterior = 20;
        private const float GridCell = 4f, RayStep = 1f, MinEnter = 0.1f;
        private static readonly Vector3[] s_dirs =
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            new Vector3(0.70710678f, 0, 0.70710678f), new Vector3(-0.70710678f, 0, 0.70710678f), new Vector3(0.70710678f, 0, -0.70710678f), new Vector3(-0.70710678f, 0, -0.70710678f),
        };
        private static bool[] Interior(List<FarJob.PieceBox> boxes)
        {
            int n = boxes.Count;
            var inv = new Quaternion[n]; var wmin = new Vector3[n]; var wmax = new Vector3[n];
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            for (int i = 0; i < n; i++)
            {
                var b = boxes[i]; inv[i] = Quaternion.Inverse(b.R);
                // world AABB of the oriented box (for the grid only)
                Vector3 ax = b.R * new Vector3(b.H.x, 0, 0), ay = b.R * new Vector3(0, b.H.y, 0), az = b.R * new Vector3(0, 0, b.H.z);
                var ext = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y), Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
                wmin[i] = b.C - ext; wmax[i] = b.C + ext;
                lo = Vector3.Min(lo, wmin[i]); hi = Vector3.Max(hi, wmax[i]);
            }
            lo -= Vector3.one; hi += Vector3.one;
            int gx = Mathf.Max(1, Mathf.CeilToInt((hi.x - lo.x) / GridCell)), gy = Mathf.Max(1, Mathf.CeilToInt((hi.y - lo.y) / GridCell)), gz = Mathf.Max(1, Mathf.CeilToInt((hi.z - lo.z) / GridCell));
            if ((long)gx * gy * gz > 4000000) return null;   // absurd extent: no cut (never guess)
            var cells = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int x0 = (int)((wmin[i].x - lo.x) / GridCell), x1 = (int)((wmax[i].x - lo.x) / GridCell), y0 = (int)((wmin[i].y - lo.y) / GridCell), y1 = (int)((wmax[i].y - lo.y) / GridCell), z0 = (int)((wmin[i].z - lo.z) / GridCell), z1 = (int)((wmax[i].z - lo.z) / GridCell);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                {
                    int key = (x * gy + y) * gz + z;
                    if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>(4);
                    l.Add(i);
                }
            }
            var result = new bool[n];
            var tested = new int[n]; int stamp = 0;
            for (int i = 0; i < n; i++)
            {
                bool allBlocked = true;
                foreach (var d in s_dirs)
                {
                    stamp++;
                    if (!Blocked(i, boxes[i].C, d, boxes, inv, cells, lo, hi, gy, gz, tested, stamp)) { allBlocked = false; break; }
                }
                result[i] = allBlocked;
            }
            return result;
        }

        private static bool Blocked(int self, Vector3 o, Vector3 d, List<FarJob.PieceBox> boxes, Quaternion[] inv, Dictionary<int, List<int>> cells, Vector3 lo, Vector3 hi, int gy, int gz, int[] tested, int stamp)
        {
            float maxT = (hi - lo).magnitude;
            for (float t = 0f; t <= maxT; t += RayStep)
            {
                Vector3 p = o + d * t;
                if (p.x < lo.x || p.y < lo.y || p.z < lo.z || p.x > hi.x || p.y > hi.y || p.z > hi.z) return false;   // left the copy: open
                int key = ((int)((p.x - lo.x) / GridCell) * gy + (int)((p.y - lo.y) / GridCell)) * gz + (int)((p.z - lo.z) / GridCell);
                if (!cells.TryGetValue(key, out var l)) continue;
                foreach (int j in l)
                {
                    if (j == self || tested[j] == stamp) continue;
                    tested[j] = stamp;
                    var b = boxes[j];
                    Vector3 lo2 = inv[j] * (o - b.C), ld = inv[j] * d;
                    float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity; bool miss = false;
                    for (int a = 0; a < 3 && !miss; a++)
                    {
                        float oa = lo2[a], da = ld[a], h = b.H[a];
                        if (Mathf.Abs(da) < 1e-6f) { if (oa < -h || oa > h) miss = true; continue; }
                        float t1 = (-h - oa) / da, t2 = (h - oa) / da; if (t1 > t2) { float s = t1; t1 = t2; t2 = s; }
                        if (t1 > tmin) tmin = t1; if (t2 < tmax) tmax = t2; if (tmin > tmax) miss = true;
                    }
                    if (!miss && tmin > MinEnter && tmin <= maxT) return true;   // a box ahead (not one around the start point)
                }
            }
            return false;
        }

        // [Optimize] FarMergePooled: the worker's own lists (Run is the only caller, on the one worker thread)
        // 1258 review: per THREAD, so an old worker still finishing after a stop never writes into the new worker's lists
        [ThreadStatic] private static List<FarVert> t_wVerts;
        [ThreadStatic] private static List<ushort> t_wIdx;
        [ThreadStatic] private static Dictionary<int, int> t_wRemap;
        [ThreadStatic] private static Dictionary<long, int> t_wCluster;
        [ThreadStatic] private static float t_cluster;   // 1.2.353: the running job's simplify grid (m); 0 = ClusterSize
        [ThreadStatic] private static RunRecorder t_runs;   // 1.2.287
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
            public RunRecorder Reset() { m_run = -1; m_start = 0; Run.Clear(); Start.Clear(); Count.Clear(); return this; }   // 1.2.287: reused per worker thread
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
                long key = EbmMeshSimplify.ClusterKey(p, n, t_cluster > 0f ? t_cluster : ClusterSize);   // 0.2.1212: the one simplifier key (EBM port: EBM's own EbmMeshSimplify, also the rock LOD1); 1.2.353: the job's grid
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
        // 1.2.287 (lead 11-11 allocation capture: "FarBuildingMerge.Run -> BitConverter.GetBytes 425 MB, 12.4M allocations"): the float's bits through an
        // overlapping struct (no byte[] per value); same bits as before
        [StructLayout(LayoutKind.Explicit)] private struct FloatBits { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; }
        private static uint Half(float f)
        {
            uint x = new FloatBits { F = f }.U;
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

