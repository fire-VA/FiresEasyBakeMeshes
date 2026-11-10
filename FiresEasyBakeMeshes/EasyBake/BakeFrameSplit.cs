using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // [Optimize] BakeFrameSplit ([saddles] 11-09, Tools\READ_EBM_bake_20261109.md): the circle log's 263 and 95 ms "baking" frames held little or no MeshBaker.Bake (worst zone 9.2 ms),
    // and some zone tracker frames were 59-115 ms with every phase near 0. This names where one ZoneTracker.Update spent its time, step by step, and which zone took the largest
    // single piece. Enter() charges the time since the previous Enter to the step that was running, so a `continue` in the walk needs no closing call.
    // Only while the key is on AND Debug logging is on; otherwise each call is one bool check. No effect on the game. One line every 30 s.
    internal static class BakeFrameSplit
    {
        internal enum Step { Suppress, Classify, Census, ZoneWalk, Other, Walk, CacheCheck, Trim, Teardown, Bake, AfterBake, SaveRead, SaveSerialise, SaveWrite, SaveRefresh, Tail, Announce, HiddenRelease, Count }

        private static readonly string[] Names =
        {
            "silence pump", "classify pump", "orphan census", "zone load/unload walk", "other (build hold, checks, stand-ins, convert)", "bake walk gates", "cache check", "trim",
            "teardown", "MeshBaker.Bake", "after bake", "save read meshes", "save serialise", "save write", "save refresh preload", "stand-in flush + empty zones", "re-announce", "hidden release",
        };

        private const float ReportSeconds = 30f;
        private const int Steps = (int)Step.Count;
        private static readonly long[] s_frame = new long[Steps], s_sum = new long[Steps], s_worst = new long[Steps];
        private static readonly int[] s_entries = new int[Steps], s_worstEntries = new int[Steps], s_frameEntries = new int[Steps];
        private static bool s_inFrame, s_hasZone, s_bigHasZone, s_worstHasZone;
        private static Step s_current, s_bigStep, s_worstStep;
        private static Vector2s s_zone, s_bigZone, s_worstZone;
        private static long s_last, s_bigTicks, s_worstTotal, s_worstBig;
        private static int s_frames, s_gcAtStart, s_worstGc, s_worstFrame, s_passStops;
        private static float s_worstTime, s_nextReport;
        private static bool s_saidStop;

        private static bool KeyOn => FiresEasyBakeMeshesPlugin.BakeFrameSplit != null && FiresEasyBakeMeshesPlugin.BakeFrameSplit.Value;

        internal static void BeginFrame()
        {
            s_inFrame = KeyOn && EasyBakeLog.DebugOn();
            if (!s_inFrame) return;
            for (int i = 0; i < Steps; i++) { s_frame[i] = 0L; s_frameEntries[i] = 0; }
            s_bigTicks = 0L; s_bigHasZone = false; s_hasZone = false;
            s_current = Step.Suppress;
            s_gcAtStart = System.GC.CollectionCount(0);
            s_last = Stopwatch.GetTimestamp();
        }

        internal static void Enter(Step step)
        {
            if (!s_inFrame) return;
            Charge();
            s_current = step;
            s_frameEntries[(int)step]++;
            if (step == Step.Other || step == Step.Tail || step == Step.Announce || step == Step.HiddenRelease) s_hasZone = false;
        }

        internal static void Enter(Step step, Vector2s zone)
        {
            if (!s_inFrame) return;
            Charge();
            s_current = step;
            s_frameEntries[(int)step]++;
            s_zone = zone;
            s_hasZone = true;
        }

        private static void Charge()
        {
            long now = Stopwatch.GetTimestamp(), spent = now - s_last;
            s_last = now;
            s_frame[(int)s_current] += spent;
            if (spent > s_bigTicks) { s_bigTicks = spent; s_bigStep = s_current; s_bigHasZone = s_hasZone; s_bigZone = s_zone; }
        }

        // BakePassBudgetMs ended this frame's walk early. Counted with or without Debug; said once at Info.
        internal static void NotePassStop(float budgetMs, Vector2s nextZone)
        {
            s_passStops++;
            if (s_saidStop) return;
            s_saidStop = true;
            EasyBakeLog.Info($"[Bake] BakePassBudgetMs on: the bake walk spent its {budgetMs:0.##} ms and left the remaining zones for the next frame (first time at zone ({nextZone.x},{nextZone.y})); their originals keep drawing until then");
        }

        internal static void EndFrame()
        {
            if (!s_inFrame) return;
            Charge();
            s_inFrame = false;
            long total = 0L;
            for (int i = 0; i < Steps; i++) { total += s_frame[i]; s_sum[i] += s_frame[i]; s_entries[i] += s_frameEntries[i]; }
            s_frames++;
            if (total > s_worstTotal)
            {
                s_worstTotal = total;
                System.Array.Copy(s_frame, s_worst, Steps);
                System.Array.Copy(s_frameEntries, s_worstEntries, Steps);
                s_worstBig = s_bigTicks; s_worstStep = s_bigStep; s_worstHasZone = s_bigHasZone; s_worstZone = s_bigZone;
                s_worstFrame = Time.frameCount; s_worstTime = Time.unscaledTime;
                s_worstGc = System.GC.CollectionCount(0) - s_gcAtStart;
            }
            if (Time.unscaledTime >= s_nextReport) Report();
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Report()
        {
            s_nextReport = Time.unscaledTime + ReportSeconds;
            if (s_frames == 0) return;
            var sb = new StringBuilder(640);
            string zone = s_worstHasZone ? $" on zone ({s_worstZone.x},{s_worstZone.y})" : "";
            sb.Append($"[BakeFrameSplit] last {ReportSeconds:0} s, {s_frames} frames. Worst zone tracker frame #{s_worstFrame} at t={s_worstTime:0.0}: {Ms(s_worstTotal):0.0} ms, largest single step {Names[(int)s_worstStep]} {Ms(s_worstBig):0.0} ms{zone}, GCs in it {s_worstGc}; its steps:");
            for (int i = 0; i < Steps; i++)
                if (Ms(s_worst[i]) >= 0.05) sb.Append($" {Names[i]} {Ms(s_worst[i]):0.0} (x{s_worstEntries[i]});");
            sb.Append(" Whole window ms:");
            for (int i = 0; i < Steps; i++)
                if (Ms(s_sum[i]) >= 0.05) sb.Append($" {Names[i]} {Ms(s_sum[i]):0.0} (x{s_entries[i]});");
            sb.Append($" Walks stopped by BakePassBudgetMs: {s_passStops}.");
            string asyncSaves = MeshCacheStore.AsyncSaveSummary();
            if (asyncSaves != null) sb.Append(' ').Append(asyncSaves).Append('.');
            EasyBakeLog.Debug(sb.ToString());
            for (int i = 0; i < Steps; i++) { s_sum[i] = 0L; s_worst[i] = 0L; s_entries[i] = 0; s_worstEntries[i] = 0; }
            s_frames = 0; s_worstTotal = 0L; s_worstBig = 0L; s_worstGc = 0; s_passStops = 0;
        }
    }
}
