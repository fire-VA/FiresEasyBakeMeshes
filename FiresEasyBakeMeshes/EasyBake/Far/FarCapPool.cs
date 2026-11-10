using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [worldgen] 11-09, from the first far-ON leg on the clean base (Tools\READ_far_moving_cost_20261109.md appendix): far buildings' own main-thread steps are about
    // 0.1-0.2 ms a frame, but with them on, allocation is x3.2 (15,962 vs 4,970 KB/s), full GCs 13 vs 5, hitches 208 vs 106. The vertex cap (2,000,000) is full in every
    // window: 20-64 targets evicted per 10 s while moving, the worker merging 100-2,640 ms per 10 s. Two keys, both default off, read live:
    //
    //  [Optimize] FarCapHysteresis: Plan's cap pass, nearest first as before, but
    //    - a target already drawn stays while the running total is within Max vertices (100 %), as before;
    //    - an EVICTED target comes back only below 90 % of the cap AND after QuietSeconds since its eviction;
    //    - at most MaxEvictionsPerWindow evictions per 10 s; past that a drawn target is kept up to 110 % of the cap (the hard ceiling), so a burst cannot churn.
    //    Look: what is drawn at a moment can differ from today only inside that band: a far target 90-100 % down the list may stay hidden a little longer,
    //    one just past 100 % may stay drawn up to 110 % while the eviction budget is spent. Nearest targets are unaffected.
    //
    //  [Optimize] FarMergePooled: the merge worker reuses its vertex / index / remap lists across jobs (one worker thread), and each chunk's vertex and index arrays
    //    come from a bounded pool (power-of-two buckets, at most PoolIdleMB idle) instead of ToArray per chunk; the main thread uploads by count and returns them.
    //    Chunk GameObjects are reused (hidden, kept in a bounded pool) instead of destroyed and made again. The Mesh is still one per upload: a far chunk's mesh is
    //    uploaded GPU-only (UploadMeshData(true)), and Unity refuses SetVertexBufferData on a non-readable mesh (decompiled Mesh.SetVertexBufferData: canAccess
    //    throws), so reusing it would mean keeping a CPU copy of every far vertex: the low-RAM rule says no. A Mesh is a native object; its managed side is tiny.
    //    Look: identical (the same vertices, indices, materials and blocks).
    internal static class FarCapPool
    {
        internal static bool HysteresisOn => FiresEasyBakeMeshesPlugin.FarCapHysteresis != null && FiresEasyBakeMeshesPlugin.FarCapHysteresis.Value;
        internal static bool PooledOn => FiresEasyBakeMeshesPlugin.FarMergePooled != null && FiresEasyBakeMeshesPlugin.FarMergePooled.Value;

        internal const float AdmitShare = 0.9f, HardShare = 1.1f, QuietSeconds = 20f, WindowSeconds = 10f;
        internal const int MaxEvictionsPerWindow = 16;
        private const long PoolIdleMB = 64;

        // ---- census (30 s, Debug) ----
        internal static int Evictions, Deferred, Readmits, ReadmitBlockedQuiet, ReadmitBlockedBand, GoReused, GoNew;
        private static float s_nextLog; private static int s_gc0;

        internal static void Tick()
        {
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 30f;
            int gc = System.GC.CollectionCount(0), gcs = s_gc0 > 0 ? gc - s_gc0 : 0; s_gc0 = gc;
            if (!HysteresisOn && !PooledOn) { Reset(); return; }
            if (EasyBakeLog.DebugOn())
            {
                double ms = 1000.0 / Stopwatch.Frequency;
                EasyBakeLog.Debug($"{FarBuildings.Log} cap / pool 30 s: hysteresis {(HysteresisOn ? "on" : "off")}: {Evictions} eviction(s), {Deferred} deferred by the {MaxEvictionsPerWindow} per {WindowSeconds:0} s budget, {Readmits} re-admit(s), blocked {ReadmitBlockedQuiet} by the {QuietSeconds:0} s quiet time and {ReadmitBlockedBand} by the {AdmitShare * 100:0} % band; "
                    + $"pooled {(PooledOn ? "on" : "off")}: arrays rented {Verts.Rented + Idx.Rented}, newly allocated {(Verts.NewBytes + Idx.NewBytes) / 1048576.0:0.0} MB, returned {Verts.Returned + Idx.Returned}, dropped (pool full) {Verts.Dropped + Idx.Dropped}, idle {(Verts.IdleBytes + Idx.IdleBytes) / 1048576.0:0.0} MB; "
                    + $"chunk objects reused {GoReused}, new {GoNew}; merge worker {s_workerTicks * ms:0} ms; full GCs {gcs}");
            }
            Reset();
        }

        internal static long s_workerTicks;
        private static void Reset()
        {
            Evictions = Deferred = Readmits = ReadmitBlockedQuiet = ReadmitBlockedBand = GoReused = GoNew = 0; s_workerTicks = 0;
            Verts.ResetCounts(); Idx.ResetCounts();
        }

        // ---- the array pools: the worker rents, the main thread returns ----
        internal static readonly ArrPool<FarVert> Verts = new ArrPool<FarVert>(24);
        internal static readonly ArrPool<ushort> Idx = new ArrPool<ushort>(2);

        internal sealed class ArrPool<T>
        {
            private readonly List<T[]>[] _buckets = new List<T[]>[32];
            private readonly object _lock = new object();
            private readonly int _elem;
            internal long IdleBytes, NewBytes; internal int Rented, Returned, Dropped;
            internal ArrPool(int elemBytes) { _elem = elemBytes; for (int i = 0; i < 32; i++) _buckets[i] = new List<T[]>(); }

            private static int Bucket(int n) { int k = 10; while ((1 << k) < n && k < 30) k++; return k; }   // at least 1024 elements

            internal T[] Rent(int n)
            {
                int k = Bucket(n);
                lock (_lock)
                {
                    Rented++;
                    var b = _buckets[k];
                    if (b.Count > 0) { var a = b[b.Count - 1]; b.RemoveAt(b.Count - 1); IdleBytes -= (long)a.Length * _elem; return a; }
                    NewBytes += (1L << k) * _elem;
                }
                return new T[1 << k];
            }

            internal void Return(T[] a)
            {
                if (a == null || a.Length < 1024 || (a.Length & (a.Length - 1)) != 0) return;   // not one of ours
                long bytes = (long)a.Length * _elem;
                lock (_lock)
                {
                    if (IdleBytes + bytes > PoolIdleMB * 1048576L) { Dropped++; return; }
                    _buckets[Bucket(a.Length)].Add(a); IdleBytes += bytes; Returned++;
                }
            }

            internal void ResetCounts() { lock (_lock) { NewBytes = 0; Rented = Returned = Dropped = 0; } }
            internal void Clear() { lock (_lock) { foreach (var b in _buckets) b.Clear(); IdleBytes = 0; } }
        }

        internal static void ReturnChunk(FarChunk c)
        {
            if (c == null || !c.Pooled) return;
            Verts.Return(c.Verts); Idx.Return(c.Idx);
            c.Verts = null; c.Idx = null; c.Pooled = false;
        }

        internal static void ReturnRange(List<FarChunk> list, int from) { if (list == null) return; for (int i = from; i < list.Count; i++) ReturnChunk(list[i]); }
    }

    internal static partial class FarBuildingDraw
    {
        // ======== FarCapHysteresis: the cap pass ========
        private static int s_evictWindowCount; private static float s_evictWindowStart;

        private static void CapPassHysteresis(List<Target> order, int budget)
        {
            float now = Time.unscaledTime;
            if (now - s_evictWindowStart >= FarCapPool.WindowSeconds) { s_evictWindowStart = now; s_evictWindowCount = 0; }
            long admit = (long)(budget * FarCapPool.AdmitShare), hard = (long)(budget * FarCapPool.HardShare);
            long used = 0;
            foreach (var t in order)
            {
                int est = t.Verts > 0 ? t.Verts : Estimate(t);
                if (!t.Evicted)
                {
                    if (used + est <= budget) used += est;
                    else if (s_evictWindowCount >= FarCapPool.MaxEvictionsPerWindow && used + est <= hard) { used += est; FarCapPool.Deferred++; }   // the burst budget is spent: kept, up to the hard ceiling. Past 110 % it is evicted anyway (1258 review: the 16 is not strict there, by design: memory stays bounded)
                    else { t.Evicted = true; t.EvictedAt = now; DestroyChunks(t); FarBuildings.S.Evictions++; FarCapPool.Evictions++; s_evictWindowCount++; }
                }
                else if (used + est <= admit && now - t.EvictedAt >= FarCapPool.QuietSeconds) { used += est; t.Evicted = false; t.Dirty = true; FarCapPool.Readmits++; }
                else if (used + est <= budget) { if (now - t.EvictedAt < FarCapPool.QuietSeconds) FarCapPool.ReadmitBlockedQuiet++; else FarCapPool.ReadmitBlockedBand++; }   // would fit today: held back by the band or the quiet time
                if (!t.Evicted && t.Dirty && !t.JobOut && !t.WaitingSource && !t.Retained) s_prepQueue.Add(t);
            }
        }

        // ======== FarMergePooled: chunk GameObjects reused ========
        private const int MaxPooledChunkObjects = 512;
        private static readonly Stack<GameObject> s_goPool = new Stack<GameObject>();

        // a retired chunk: its mesh goes, the object waits hidden for the next chunk; false = not taken (the caller destroys as before)
        private static bool PoolChunkObject(GameObject go)
        {
            if (!FarCapPool.PooledOn || go == null || s_goPool.Count >= MaxPooledChunkObjects) return false;
            if (go.activeSelf) go.SetActive(false);
            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null) { Object.Destroy(mf.sharedMesh); mf.sharedMesh = null; }
            s_goPool.Push(go);
            return true;
        }

        // BuildChunk's object: a pooled one set up exactly like a new one (active, at the target, the chunk's material and block), or null = make a new one
        private static GameObject TakeChunkObject(Mesh mesh, Material mat, FarChunk c, Target t)
        {
            if (!FarCapPool.PooledOn) return null;
            while (s_goPool.Count > 0)
            {
                var go = s_goPool.Pop();
                if (go == null) continue;   // destroyed with the root meanwhile
                go.transform.position = t.Origin;
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                if (c.Snow > 0) { s_mpb.Clear(); s_mpb.SetFloat(FarSnow.SnowCoverId, c.Snow / 4f); s_mpb.SetFloat(FarSnow.LegacySnowCoverId, c.Snow / 4f); s_mpb.SetFloat(SnowLevelId, c.Snow / 4f); r.SetPropertyBlock(s_mpb); }   /* 1.2.262: _SnowLevel = a cap chunk's height (vanilla MaterialMan sets it on the whole piece too) */
                else r.SetPropertyBlock(null);
                go.SetActive(true);   // a new GameObject starts active; the callers decide visibility from here as before
                FarCapPool.GoReused++;
                return go;
            }
            return null;
        }

        private static void ClearChunkObjectPool() { s_goPool.Clear(); FarCapPool.Verts.Clear(); FarCapPool.Idx.Clear(); }   // the objects die with the root
    }
}
