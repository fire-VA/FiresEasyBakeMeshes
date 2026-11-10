using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [Optimize] FarIndexRanges ([worldgen] 11-09, round 2 row E16 "far chunks hide by index range"; our own method). Default off, read live for new jobs.
    // Today any change to a far zone marks its whole target dirty: the 64 m zone or the whole 512 m cell is gathered again on the main thread, merged
    // again on the worker and uploaded again (FarBuildingDraw.OnZoneChanged). And when the player moves, a zone crossing "Cell merge from" leaves its
    // 512 m cell: the cell is rebuilt whole while the zone gets its own target, and both draw that zone until the cell's new chunks land.
    // With the key on:
    //   - every chunk remembers, per piece, the index range its triangles fill (a "run"; the merge writes each run at an even start and length);
    //   - a zone change that only REMOVES pieces zeroes those pieces' index ranges on the GPU (a triangle 0,0,0 draws nothing) instead of a rebuild;
    //   - a cell losing a zone to that zone's own target hides the zone's runs the moment the zone target is drawn (no rebuild, no double draw);
    //   - anything else (a piece added or moved, a source still loading, a job in flight, VA distant reach in use, a zone that loaded) rebuilds as before.
    // Hidden ranges are never shown again: the next rebuild of that target simply leaves those pieces out, the same as today.
    // Proof, both at run time, either failing switches the key off for the session and rebuilds every target it touched (Warning says why):
    //   1. the first write is read back from the GPU and must come back as zeros;
    //   2. a chunk built with the raw index flag must still hand out its index buffer 3 s later (a non-readable mesh with the flag set AFTER upload lost its
    //      index buffer in 1.2.246; here the flag is set on our own new mesh before its first upload, which is a different case, but it is checked anyway).
    internal static class FarIndexRanges
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.FarIndexRanges != null && FiresEasyBakeMeshesPlugin.FarIndexRanges.Value && !s_latched && SystemInfo.supportsAsyncGPUReadback;
        private static bool s_latched;
        private static string s_latchWhy;
        internal static Action OnLatched;   // FarBuildingDraw: mark every target with hidden runs (or raw chunks) dirty

        // one target's runs: per run its piece key, zone and segments; per job.Result index its uploaded mesh (null = not uploaded)
        internal sealed class Table
        {
            public long[] Keys; public Vector2s[] Zones; public bool[] Hidden;
            public int[] Head, SegNext, SegChunk, SegStart, SegCount;
            public Mesh[] Meshes;
            public int HiddenCount;
        }

        // the merge's per-chunk segments, from FarChunk
        internal static Table Build(FarJob job, Mesh[] meshes)
        {
            if (!job.TrackRuns || job.RunKeys == null) return null;
            int runs = job.RunKeys.Count, segs = 0;
            foreach (var c in job.Result) if (c.SegRun != null) segs += c.SegRun.Length;
            var t = new Table
            {
                Keys = job.RunKeys.ToArray(), Zones = job.RunZones.ToArray(), Hidden = new bool[runs], Head = new int[runs],
                SegNext = new int[segs], SegChunk = new int[segs], SegStart = new int[segs], SegCount = new int[segs], Meshes = meshes,
            };
            for (int r = 0; r < runs; r++) t.Head[r] = -1;
            int s = 0;
            for (int ci = 0; ci < job.Result.Count; ci++)
            {
                var c = job.Result[ci];
                if (c.SegRun == null) continue;
                for (int k = 0; k < c.SegRun.Length; k++, s++)
                {
                    int r = c.SegRun[k];
                    t.SegChunk[s] = ci; t.SegStart[s] = c.SegStart[k]; t.SegCount[s] = c.SegCount[k];
                    t.SegNext[s] = t.Head[r]; t.Head[r] = s;
                }
            }
            return t;
        }

        // a piece's identity across digests: prefab + exact position + exact rotation (the same floats come back from the same source)
        internal static long PieceKey(FarPiece p)
        {
            ulong h = (ulong)(uint)p.Prefab * 0x9E3779B97F4A7C15UL;
            h = Mix(h ^ Bits(p.Pos.x)); h = Mix(h ^ Bits(p.Pos.y)); h = Mix(h ^ Bits(p.Pos.z));
            h = Mix(h ^ Bits(p.Rot.x)); h = Mix(h ^ Bits(p.Rot.y)); h = Mix(h ^ Bits(p.Rot.z)); h = Mix(h ^ Bits(p.Rot.w));
            return (long)h;
        }
        private static ulong Bits(float f) => (ulong)(uint)new FloatBits { F = f }.I;
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }
        private static ulong Mix(ulong x) { x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33; return x; }

        private static readonly Dictionary<long, int> s_newCount = new Dictionary<long, int>();
        internal static readonly List<int> Removed = new List<int>();

        /// <summary>
        /// The zone's new pieces against the runs this target drew for it. True = only removals (listed in Removed, may be none); false = something was
        /// added or moved, or a piece's source is still loading: rebuild as before. Uses Prep's own filter (failed sources are never drawn).
        /// </summary>
        internal static bool OnlyRemovals(Table t, Vector2s zone, FarPiece[] pieces, out string why)
        {
            Removed.Clear(); s_newCount.Clear(); why = null;
            if (pieces != null)
                foreach (var p in pieces)
                {
                    if (FarBuildingSources.Get(p.Prefab) == null)
                    {
                        if (FarBuildingSources.IsFailed(p.Prefab)) continue;   // Prep skips it too
                        why = "a source still loading"; return false;
                    }
                    long k = PieceKey(p);
                    s_newCount.TryGetValue(k, out int n); s_newCount[k] = n + 1;
                }
            for (int r = 0; r < t.Keys.Length; r++)
            {
                if (t.Hidden[r] || t.Zones[r].x != zone.x || t.Zones[r].y != zone.y) continue;
                if (s_newCount.TryGetValue(t.Keys[r], out int n) && n > 0) { s_newCount[t.Keys[r]] = n - 1; continue; }
                Removed.Add(r);
            }
            foreach (var kv in s_newCount) if (kv.Value > 0) { why = "a piece added or moved"; Removed.Clear(); return false; }
            return true;
        }

        /// <summary>Every run of this zone, hidden (the zone left the target).</summary>
        internal static void ListZone(Table t, Vector2s zone)
        {
            Removed.Clear();
            for (int r = 0; r < t.Keys.Length; r++) if (!t.Hidden[r] && t.Zones[r].x == zone.x && t.Zones[r].y == zone.y) Removed.Add(r);
        }

        private static uint[] s_zeros = new uint[256];
        internal static long PiecesHidden, SegWrites, BytesWritten, RebuildsAvoided, VertsNotRemerged, CellZonesHidden, CellZonesPending, Fallbacks;
        internal static readonly Dictionary<string, int> FallbackWhy = new Dictionary<string, int>();
        private static bool s_saidFirst, s_checkSent, s_checkDone;

        internal static void NoteFallback(string why) { FallbackWhy.TryGetValue(why, out int n); FallbackWhy[why] = n + 1; Fallbacks++; }

        /// <summary>Zero the index ranges of the runs in Removed. False = a write failed (the key is latched off; the caller rebuilds).</summary>
        internal static bool HideListed(Table t)
        {
            foreach (int r in Removed)
            {
                if (t.Hidden[r]) continue;
                for (int s = t.Head[r]; s >= 0; s = t.SegNext[s])
                {
                    var mesh = t.Meshes[t.SegChunk[s]];
                    if (mesh == null) continue;   // that chunk was refused at upload (ChunkSane): nothing drawn there
                    int start = t.SegStart[s], count = t.SegCount[s];
                    if ((start & 1) != 0 || (count & 1) != 0) { Latch($"an odd index range ({start}, {count}) from the merge"); return false; }
                    int words = count / 2;
                    if (s_zeros.Length < words) s_zeros = new uint[Mathf.NextPowerOfTwo(words)];
                    GraphicsBuffer ib = null;
                    try
                    {
                        ib = mesh.GetIndexBuffer();
                        if (ib == null) { Latch("a chunk's index buffer was null"); return false; }
                        ib.SetData(s_zeros, 0, start / 2, words);   // uint units: offset start / 2 words = start x 2 bytes
                        SegWrites++; BytesWritten += count * 2L;
                        if (!s_checkSent) { s_checkSent = true; SelfCheck(ib, start, count); ib = null; }   // the check owns this wrapper now
                    }
                    catch (Exception e) { Latch("an index write threw " + e.GetType().Name + ": " + e.Message); return false; }
                    finally { ib?.Dispose(); }
                }
                t.Hidden[r] = true; t.HiddenCount++; PiecesHidden++;
            }
            if (!s_saidFirst && Removed.Count > 0) { s_saidFirst = true; EasyBakeLog.Info($"{FarBuildings.Log} FarIndexRanges: first {Removed.Count} far piece(s) hidden by their index ranges instead of a rebuild (proof: the first write is read back from the GPU)."); }
            return true;
        }

        private static void SelfCheck(GraphicsBuffer ib, int start, int count)
        {
            AsyncGPUReadback.Request(ib, count * 2, start * 2, req =>
            {
                try
                {
                    if (req.hasError) { Latch("the GPU readback of the first write failed"); return; }
                    var data = req.GetData<uint>();
                    for (int i = 0; i < data.Length; i++) if (data[i] != 0u) { Latch($"the first write did not land (word {i} reads {data[i]:X8})"); return; }
                    s_checkDone = true;
                    EasyBakeLog.Info($"{FarBuildings.Log} FarIndexRanges self-check: the first write read back as {count} zero indices; index-range hiding is proven on this GPU.");
                }
                finally { ib.Dispose(); }
            });
        }

        // ---- proof 2: a raw chunk keeps its index buffer
        private static Mesh s_rawProbe; private static float s_rawProbeAt = -1f; private static bool s_rawProven;
        internal static void NoteRawChunk(Mesh m) { if (s_rawProven || s_rawProbe != null || m == null) return; s_rawProbe = m; s_rawProbeAt = Time.unscaledTime + 3f; }

        /// <summary>From the hand-off tick: the raw probe, then the 30 s line.</summary>
        internal static void Tick()
        {
            if (s_rawProbe != null && Time.unscaledTime >= s_rawProbeAt)
            {
                var m = s_rawProbe; s_rawProbe = null;
                if (m != null)   // destroyed meanwhile: probe the next raw chunk instead
                {
                    GraphicsBuffer ib = null;
                    try { ib = m.GetIndexBuffer(); if (ib == null) Latch("a raw chunk's index buffer was null 3 s after upload"); else { s_rawProven = true; EasyBakeLog.Debug($"{FarBuildings.Log} FarIndexRanges: a raw chunk still has its index buffer 3 s after upload ({ib.count} entries)"); } }
                    catch (Exception e) { Latch("a raw chunk's index buffer threw " + e.GetType().Name + ": " + e.Message); }
                    finally { ib?.Dispose(); }
                }
            }
            MaybeLog();
        }

        private static void Latch(string why)
        {
            if (s_latched) return;
            s_latched = true; s_latchWhy = why;
            EasyBakeLog.Warn($"{FarBuildings.Log} FarIndexRanges switched off for this session: {why}. Every far target it touched is rebuilt the normal way.");
            try { OnLatched?.Invoke(); } catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} FarIndexRanges latch rebuild threw: {e.Message}"); }
        }

        private static float s_nextLog;
        private static void MaybeLog()
        {
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 30f;
            if (PiecesHidden == 0 && Fallbacks == 0 && CellZonesPending == 0 && CellZonesHidden == 0) return;
            if (EasyBakeLog.DebugOn())
            {
                var sb = new System.Text.StringBuilder(320);
                sb.Append($"{FarBuildings.Log} FarIndexRanges 30 s: {PiecesHidden} piece(s) hidden by index range ({SegWrites} range write(s), {BytesWritten / 1024.0:0.0} KB) instead of {RebuildsAvoided} target rebuild(s) ({VertsNotRemerged} vertices not merged and uploaded again); ");
                sb.Append($"cells: {CellZonesHidden} zone(s) hidden as their own target drew, {CellZonesPending} waited for it; {Fallbacks} rebuilt the old way (why:");
                foreach (var kv in FallbackWhy) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value).Append(';');
                sb.Append($"); self-check {(s_latched ? "FAILED: " + s_latchWhy : s_checkDone ? "passed" : s_checkSent ? "waiting" : "not run yet")}, raw probe {(s_rawProven ? "passed" : "not run yet")}");
                EasyBakeLog.Debug(sb.ToString());
            }
            PiecesHidden = SegWrites = BytesWritten = RebuildsAvoided = VertsNotRemerged = CellZonesHidden = CellZonesPending = Fallbacks = 0;
            FallbackWhy.Clear();
        }
    }
}
