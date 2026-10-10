using System.Diagnostics;
using System.Text;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Fire (09-30 22:3x, via [fgn]): speed EBM up with Unity Jobs/Burst, but profile a BlueHills bake first. Valheim is a
    // release player (no Unity Profiler, Profiler.BeginSample compiled out), so MeshBaker.Bake is split into phases timed
    // with one Stopwatch: each Mark() charges the time since the previous mark to a phase. The question it answers: is the
    // bake C# loops over plain data (jobs could help), native mesh calls (they can't), or object creation (main-thread only)?
    //   C#:      InstanceGroups, Collect, GroupCover, CombineArrays, Suppress, Vegetation, Other
    //   native:  CombineNative (Mesh.CombineMeshes + RecalculateBounds)
    //   objects: Objects (new GameObject, AddComponent, renderer setup)
    // One "[BakeProfile] zone" line per bake and a "[BakeProfile] total" line every TotalEvery zones.
    internal static class BakeProfile
    {
        internal enum Phase { InstanceGroups, Collect, GroupCover, CombineArrays, CombineNative, Objects, Suppress, Vegetation, Other, Count }

        private const int TotalEvery = 20;
        private static readonly Stopwatch s_clock = Stopwatch.StartNew();
        private static readonly long[] s_zone = new long[(int)Phase.Count];
        private static readonly long[] s_total = new long[(int)Phase.Count];
        private static long s_last;
        private static int s_zMeshes, s_zSources, s_zObjects;
        private static long s_zVerts;
        private static int s_zones, s_tMeshes, s_tSources, s_tObjects, s_tPieces;
        private static long s_tVerts;

        internal static void BeginZone()
        {
            for (int i = 0; i < s_zone.Length; i++) s_zone[i] = 0;
            s_zMeshes = s_zSources = s_zObjects = 0;
            s_zVerts = 0;
            s_zLoop = s_zInlineTicks = s_zGroups = 0;
            s_zPrepared = false;
            s_zPrepWhy = null;
            s_zRechecked = 0;
            s_last = s_clock.ElapsedTicks;
        }

        internal static void Mark(Phase phase, int meshes = 0, int sourceMeshes = 0, int vertices = 0, int gameObjects = 0)
        {
            long now = s_clock.ElapsedTicks;
            s_zone[(int)phase] += now - s_last;
            s_last = now;
            s_zMeshes += meshes;
            s_zSources += sourceMeshes;
            s_zVerts += vertices;
            s_zObjects += gameObjects;
        }

        internal static void EndZone(Vector2s coord, int pieces, int candidates, int contributions, int batches)
        {
            for (int i = 0; i < s_zone.Length; i++) s_total[i] += s_zone[i];
            s_zones++;
            s_tMeshes += s_zMeshes;
            s_tSources += s_zSources;
            s_tVerts += s_zVerts;
            s_tObjects += s_zObjects;
            s_tPieces += pieces;
            EasyBakeLog.Debug($"[BakeProfile] zone ({coord.x},{coord.y}): {Split(s_zone)}; pieces {pieces}, candidates {candidates}, contributions {contributions}, " +
                             $"batches {batches}, combined meshes {s_zMeshes} from {s_zSources} source meshes, {s_zVerts} vertices, {s_zObjects} GameObjects; {ClassifyShare()}");
            if (s_zones % TotalEvery == 0)
                EasyBakeLog.Debug($"[BakeProfile] total after {s_zones} zones: {Split(s_total)}; pieces {s_tPieces}, combined meshes {s_tMeshes} from {s_tSources} source meshes, " +
                                 $"{s_tVerts} vertices, {s_tObjects} GameObjects; per vertex {PerUnit(s_total[(int)Phase.CombineNative], s_tVerts)} us native, " +
                                 $"per source mesh {PerUnit(s_total[(int)Phase.Collect] + s_total[(int)Phase.CombineArrays], s_tSources)} us C# collect+arrays; {LookShare()}");
        }

        // Time-sliced suppression (1.2.79): the pump's work per frame, so the totals show where the old in-bake suppress went.
        private static long s_sliceTicks, s_sliceMax;
        private static int s_sliceFrames, s_slicePieces;

        internal static void NoteSuppressSlice(long ticks, int pieces, int bakesLeft)
        {
            if (pieces == 0) return;
            s_sliceTicks += ticks;
            s_slicePieces += pieces;
            s_sliceFrames++;
            if (ticks > s_sliceMax) s_sliceMax = ticks;
            // 1.2.82: also when the queue empties (BlueHills F/G finished under 200 frames and never printed), then a new burst.
            if (s_sliceFrames % 200 == 0 || bakesLeft == 0)
                EasyBakeLog.Debug($"[BakeProfile] suppress slices: {s_slicePieces} pieces over {s_sliceFrames} frames, {Ms(s_sliceTicks):0.0} ms total, " +
                                 $"worst frame {Ms(s_sliceMax):0.0} ms, {PerUnit(s_sliceTicks, s_slicePieces)} us per piece, {bakesLeft} bake(s) still queued");
        }

        // 1.2.80: inside instance grouping, the share spent resolving each piece's wear look (Core WearLooks.Resolve).
        private static long s_lookTicks;
        private static int s_lookCalls;
        internal static long Now() => s_clock.ElapsedTicks;
        internal static void NoteInstanceLook(long start)
        {
            s_lookTicks += s_clock.ElapsedTicks - start;
            s_lookCalls++;
        }
        internal static string LookShare() => $"instance look (WearLooks.Resolve) {Ms(s_lookTicks):0.0} ms over {s_lookCalls} pieces ({PerUnit(s_lookTicks, s_lookCalls)} us each)";

        // 1.2.82: pieces classified ahead of the bake (cached at bake) vs at bake time (inline), per zone; and the classify pump.
        private static int s_zCached, s_zInline;
        internal static void NoteClassify(int cached, int inline) { s_zCached = cached; s_zInline = inline; }
        internal static string ClassifyShare() => $"classified ahead {s_zCached}, at bake {s_zInline}; instance = loop {Ms(s_zLoop):0.0} + at-bake classify {Ms(s_zInlineTicks):0.0} + groups {Ms(s_zGroups):0.0} ms; " +
                                                  (s_zPrepared ? $"groups prepared ahead (taken; {s_zRechecked} re-checked)" : $"groups built at bake{(s_zPrepWhy != null ? $" (prepared ones not taken: {s_zPrepWhy})" : "")}");

        // 1.2.84: whether this zone's instance groups were prepared ahead of the bake and taken, or why not.
        private static bool s_zPrepared;
        private static string s_zPrepWhy;
        internal static void NotePrepared(bool taken, string why) { s_zPrepared = taken; s_zPrepWhy = why; }

        // 1.2.85: grouped pieces whose ZDO revision moved since the prep, classified again at take time (same group: prep kept).
        private static int s_zRechecked;
        internal static void NotePreparedRechecked(int moved) { s_zRechecked = moved; }

        // 1.2.83: instance grouping split: the per-piece loop (lookups, without classification), the classification done at bake
        // time, and building the groups (matrices) - to see which part a slow zone spends in.
        private static long s_zLoop, s_zInlineTicks, s_zGroups;
        internal static void NoteInstanceSplit(long loop, long inline, long groups) { s_zLoop = loop; s_zInlineTicks = inline; s_zGroups = groups; }

        private static long s_clsTicks, s_clsMax;
        private static int s_clsFrames, s_clsPieces;
        internal static void NoteClassifySlice(long ticks, int pieces, int left)
        {
            if (pieces == 0) return;
            s_clsTicks += ticks;
            s_clsPieces += pieces;
            s_clsFrames++;
            if (ticks > s_clsMax) s_clsMax = ticks;
            if (s_clsFrames % 200 == 0 || left == 0)
                EasyBakeLog.Debug($"[BakeProfile] classify slices: {s_clsPieces} pieces over {s_clsFrames} frames, {Ms(s_clsTicks):0.0} ms total, " +
                                 $"worst frame {Ms(s_clsMax):0.0} ms, {PerUnit(s_clsTicks, s_clsPieces)} us per piece, {left} queued");
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static string PerUnit(long ticks, long units) => units > 0 ? (Ms(ticks) * 1000.0 / units).ToString("0.000") : "-";

        // "total 12.3 ms = C# 4.1 (collect 3.0, …) + native 6.2 (combine) + objects 2.0 (…)".
        private static string Split(long[] t)
        {
            double cs = Ms(t[(int)Phase.InstanceGroups] + t[(int)Phase.Collect] + t[(int)Phase.GroupCover] + t[(int)Phase.CombineArrays]
                           + t[(int)Phase.Suppress] + t[(int)Phase.Vegetation] + t[(int)Phase.Other]);
            double native = Ms(t[(int)Phase.CombineNative]);
            double objects = Ms(t[(int)Phase.Objects]);
            var sb = new StringBuilder();
            sb.Append($"total {cs + native + objects:0.0} ms = C# {cs:0.0} (");
            sb.Append($"instance {Ms(t[(int)Phase.InstanceGroups]):0.0}, collect {Ms(t[(int)Phase.Collect]):0.0}, group/cover {Ms(t[(int)Phase.GroupCover]):0.0}, ");
            sb.Append($"arrays {Ms(t[(int)Phase.CombineArrays]):0.0}, suppress {Ms(t[(int)Phase.Suppress]):0.0}, vegetation {Ms(t[(int)Phase.Vegetation]):0.0}, other {Ms(t[(int)Phase.Other]):0.0})");
            sb.Append($" + native {native:0.0} (CombineMeshes+bounds) + objects {objects:0.0} (GameObject/renderer)");
            return sb.ToString();
        }
    }
}
