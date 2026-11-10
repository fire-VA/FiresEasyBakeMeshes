using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.212 ([visual], lead: "break EBM's 5.54 ms [Draw] down by phase; find what is O(instances) per frame"): main-thread time per phase of the
    // indirect path, averaged per frame over 30 s, plus the worst frame and the phase that made it. Debug only (two timestamps per phase per
    // frame when Debug is on, nothing otherwise). Log-proof first, then the fix (our rule).
    internal static partial class EbmIndirect
    {
        private const int PhGroups = 0, PhEnsure = 1, PhUpload = 2, PhLooks = 3, PhCull = 4, PhCellsView = 5, PhCellsSphere = 6, PhCellsFace = 7,
                          PhDraw = 8, PhRecCasc = 9, PhRecLocal = 10, PhOther = 11, PhSync = 12, PhCamLoop = 13, PhDrawTicks = 14, PhN = 15;
        private static readonly string[] PhName = { "group walk", "ensure/layout", "upload", "looks upload", "cull (all)", "cells camera+sun", "cells cascades",
                                                    "cells light faces", "draw (all)", "record cascades", "record lights", "other cameras", "group sync", "camera + sun draw loop (inside draw)", "draw ticks: debug, integrity, placeholders (inside draw)" };
        private static readonly long[] s_ph = new long[PhN], s_phFrame = new long[PhN], s_phWorstFrame = new long[PhN];
        private static long s_worstFrameTicks; private static int s_phFrames; private static float s_phNextLog;
        private static int s_addsWindow, s_removesWindow;
        private static bool s_phOn;
        private static long s_phCamDraws, s_phShadowDraws;   // 1.2.240: (a) of the perf round: how many draws the shadow passes get

        private static long PhT() => s_phOn ? Stopwatch.GetTimestamp() : 0L;
        private static void PhAcc(int p, long t0) { if (s_phOn && t0 != 0L) s_phFrame[p] += Stopwatch.GetTimestamp() - t0; }

        // start of EndFrame: is Debug on this frame
        private static void PhaseBegin() { s_phOn = EasyBakeLog.DebugOn(); IndirectStats.SplitOn = s_phOn || EbmRenderCensus.On; }   // [seasons] 11-09 E39: the census needs the split

        // end of EndFrame: fold the frame, log every 30 s
        private static void PhaseEnd()
        {
            if (!s_phOn) return;
            long frame = s_phFrame[PhGroups] + s_phFrame[PhEnsure] + s_phFrame[PhUpload] + s_phFrame[PhLooks] + s_phFrame[PhCull] + s_phFrame[PhDraw] + s_phFrame[PhSync];
            if (frame > s_worstFrameTicks) { s_worstFrameTicks = frame; System.Array.Copy(s_phFrame, s_phWorstFrame, PhN); }
            for (int p = 0; p < PhN; p++) { s_ph[p] += s_phFrame[p]; s_phFrame[p] = 0; }
            s_phFrames++;
            SyncTimerFrameEnd();   // EbmIndirectSyncTimer
            if (Time.unscaledTime < s_phNextLog) return;
            s_phNextLog = Time.unscaledTime + 30f;
            double toMs = 1000.0 / Stopwatch.Frequency;
            var sb = new System.Text.StringBuilder(512);
            sb.Append("[Indirect] main-thread phases, ms a frame over ").Append(s_phFrames).Append(" frames:");
            for (int p = 0; p < PhN; p++) sb.Append(' ').Append(p == PhCellsView && s_cellsFused ? "cells (fused: camera+sun+cascades+lights+faces)" : PhName[p]).Append(' ').Append((s_ph[p] * toMs / Mathf.Max(1, s_phFrames)).ToString("0.000")).Append(';');
            sb.Append(" (cull and draw include their own cells / record rows; other cameras run in their pre-cull, outside [Draw])");
            int wp = 0; for (int p = 1; p < PhN; p++) if (p != PhCull && p != PhDraw && s_phWorstFrame[p] > s_phWorstFrame[wp]) wp = p;
            sb.Append($". Worst frame {s_worstFrameTicks * toMs:0.00} ms, most in '{PhName[wp]}' {s_phWorstFrame[wp] * toMs:0.00} ms (cull {s_phWorstFrame[PhCull] * toMs:0.00}, draw {s_phWorstFrame[PhDraw] * toMs:0.00}, sync {s_phWorstFrame[PhSync] * toMs:0.00})");
            int cascadeDraws = 0; if (s_recCount != null) for (int k = 0; k < s_recCount.Length; k++) cascadeDraws += s_recCount[k];
            sb.Append($". Draws a frame: camera {(double)s_phCamDraws / System.Math.Max(1, s_phFrames):0}, sun shadows-only {(double)s_phShadowDraws / System.Math.Max(1, s_phFrames):0}; recorded in the cascade buffers now {cascadeDraws} over {s_cascadeCount} cascades, shadowDistance {QualitySettings.shadowDistance:0}");
            s_phCamDraws = s_phShadowDraws = 0;
            sb.Append($". Instances {s_live} live in {s_high} slots, {s_addsWindow} added / {s_removesWindow} removed this window, {s_batches.Count} batches, {s_cells.CellCount} cells, {s_gpuGrows} GPU buffer growth(s), {s_cells._skipped / Mathf.Max(1, s_phFrames)} cell entries skipped as already marked a frame, {s_cellReused} of {s_phFrames} frame(s) reused the last cell verdict");
            if (FiresEasyBakeMeshesPlugin.IndirectCellReusePerCell != null && FiresEasyBakeMeshesPlugin.IndirectCellReusePerCell.Value) sb.Append(CellReusePerCellLine());   // 1.2.254
            EasyBakeLog.Debug(sb.ToString());
            for (int p = 0; p < PhN; p++) { s_ph[p] = 0; s_phWorstFrame[p] = 0; }
            s_worstFrameTicks = 0; s_cells._skipped = 0; s_cellReused = 0; s_phFrames = 0; s_addsWindow = 0; s_removesWindow = 0;
        }
    }
}
