using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [worldgen] 11-09 (lead: "so the per-step numbers stop being guesses", Tools\READ_EBM_take_sync_20261109.md). Debug only: nothing runs while Debug logging is off.
    // Every group Sync: adds, removes, ms, first registration or a change, whether a GC ran inside it. Every 30 s with activity: totals, the worst frame, the slowest groups.
    // One AddInstance in 16 inside a Sync is timed step by step (the timestamps stay out of the other 15). What the steps do not cover is the lane matching, the lists and the fixed cost of a Sync.
    internal static partial class EbmIndirect
    {
        private const int StSteps = 6, StTop = 8, StEvery = 16;
        private static readonly string[] StName = { "free slot", "bad-instance test", "inverse + checks", "GPU row + LOD", "world box", "cells" };
        private static readonly long[] s_stTicks = new long[StSteps];
        private static readonly int[] s_stHits = new int[StSteps];
        private static bool s_stInSync;
        private static int s_stCounter;

        private struct StRow { public int Prefab, Adds, Removes, Instances, Lanes; public long Ticks; public bool First, Gc; }
        private static readonly StRow[] s_stTop = new StRow[StTop];
        private static int s_stTopCount;

        private static long s_stFrameTicks, s_stWorstTicks, s_stTotalTicks;
        private static int s_stFrameGroups, s_stFrameAdds, s_stFrameRemoves, s_stWorstGroups, s_stWorstAdds, s_stWorstRemoves;
        private static int s_stGroups, s_stFirstGroups, s_stAdds, s_stRemoves, s_stGcSyncs;
        private static bool s_stFrameGc, s_stWorstGc;
        private static float s_stNextLog;

        private static long StBegin()
        {
            if (!s_stInSync || (++s_stCounter & (StEvery - 1)) != 0) return 0L;
            return Stopwatch.GetTimestamp();
        }

        private static void StMark(int step, ref long t)
        {
            if (t == 0L) return;
            long now = Stopwatch.GetTimestamp();
            s_stTicks[step] += now - t; s_stHits[step]++;
            t = now;
        }

        private static void SyncTimed(ZoneInstanceGroup group, GroupSlots reg)
        {
            if (!s_phOn) { Sync(group, reg); return; }
            bool first = reg.Version == -1;
            int adds0 = s_addsWindow, removes0 = s_removesWindow, gc0 = System.GC.CollectionCount(0);
            long t0 = Stopwatch.GetTimestamp();
            s_stInSync = true;
            try { Sync(group, reg); }
            finally { s_stInSync = false; }
            long ticks = Stopwatch.GetTimestamp() - t0;
            int adds = s_addsWindow - adds0, removes = s_removesWindow - removes0;
            bool gc = System.GC.CollectionCount(0) != gc0;

            s_stFrameTicks += ticks; s_stFrameGroups++; s_stFrameAdds += adds; s_stFrameRemoves += removes; s_stFrameGc |= gc;
            s_stGroups++; if (first) s_stFirstGroups++; if (gc) s_stGcSyncs++;
            s_stTotalTicks += ticks; s_stAdds += adds; s_stRemoves += removes;

            int at = s_stTopCount < StTop ? s_stTopCount++ : -1;
            if (at < 0)
            {
                int low = 0;
                for (int i = 1; i < StTop; i++) if (s_stTop[i].Ticks < s_stTop[low].Ticks) low = i;
                if (ticks <= s_stTop[low].Ticks) return;
                at = low;
            }
            s_stTop[at] = new StRow { Prefab = group.PrefabHash, Adds = adds, Removes = removes, Instances = group.Matrices.Count, Lanes = reg.Lanes.Count, Ticks = ticks, First = first, Gc = gc };
        }

        // from PhaseEnd, every frame Debug is on
        private static void SyncTimerFrameEnd()
        {
            if (s_stFrameTicks > s_stWorstTicks)
            {
                s_stWorstTicks = s_stFrameTicks; s_stWorstGroups = s_stFrameGroups; s_stWorstAdds = s_stFrameAdds; s_stWorstRemoves = s_stFrameRemoves; s_stWorstGc = s_stFrameGc;
            }
            s_stFrameTicks = 0L; s_stFrameGroups = s_stFrameAdds = s_stFrameRemoves = 0; s_stFrameGc = false;
            if (s_stGroups == 0 || Time.unscaledTime < s_stNextLog) return;
            s_stNextLog = Time.unscaledTime + 30f;

            double toMs = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder(640);
            sb.Append($"[Indirect] sync 30 s: {s_stGroups} group sync(s) ({s_stFirstGroups} first registrations), {s_stAdds} adds, {s_stRemoves} removes, {s_stTotalTicks * toMs:0.0} ms in all");
            if (s_stAdds > 0) sb.Append($" = {s_stTotalTicks * toMs * 1000.0 / s_stAdds:0.00} us per add");
            sb.Append($", {s_stGcSyncs} with a GC inside. Worst frame {s_stWorstTicks * toMs:0.0} ms: {s_stWorstGroups} group(s), {s_stWorstAdds} adds, {s_stWorstRemoves} removes, GC {(s_stWorstGc ? "yes" : "no")}. One add in {StEvery}, ns a step:");
            long stepTicks = 0L;
            for (int i = 0; i < StSteps; i++)
            {
                double ns = s_stHits[i] > 0 ? s_stTicks[i] * 1e9 / Stopwatch.Frequency / s_stHits[i] : 0.0;
                sb.Append($" {StName[i]} {ns:0}");
                if (s_stHits[i] > 0) stepTicks += s_stTicks[i] / s_stHits[i];
            }
            sb.Append($" (sum {stepTicks * 1e9 / Stopwatch.Frequency:0}; the rest of the per-add figure is lane matching, lists and the fixed cost of a Sync).");
            EasyBakeLog.Debug(sb.ToString());

            sb.Length = 0;
            sb.Append("[Indirect] slowest group syncs:");
            for (int n = 0; n < s_stTopCount; n++)
            {
                int best = n;
                for (int i = n + 1; i < s_stTopCount; i++) if (s_stTop[i].Ticks > s_stTop[best].Ticks) best = i;
                var row = s_stTop[best]; s_stTop[best] = s_stTop[n]; s_stTop[n] = row;
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(row.Prefab);
                sb.Append($" '{(prefab != null ? prefab.name : row.Prefab.ToString())}' {row.Ticks * toMs:0.00} ms, {row.Instances} instances x {row.Lanes} lanes, {row.Adds} adds, {row.Removes} removes, {(row.First ? "first" : "change")}{(row.Gc ? ", GC" : "")};");
            }
            EasyBakeLog.Debug(sb.ToString());

            System.Array.Clear(s_stTicks, 0, StSteps); System.Array.Clear(s_stHits, 0, StSteps);
            s_stTopCount = 0; s_stGroups = s_stFirstGroups = s_stAdds = s_stRemoves = s_stGcSyncs = 0; s_stTotalTicks = 0L;
            s_stWorstTicks = 0L; s_stWorstGroups = s_stWorstAdds = s_stWorstRemoves = 0; s_stWorstGc = false;
        }
    }
}
