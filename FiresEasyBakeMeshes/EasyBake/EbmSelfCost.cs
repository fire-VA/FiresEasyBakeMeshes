using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.219 ([visual], lead: "an EBM self-cost line so update.scripts can be split by mod"): main-thread ms a frame of each of EBM's own Unity
    // entry points (the plugin's Update / LateUpdate, the far tier's Update / LateUpdate), every 30 s at Debug. Harmony patches on vanilla
    // methods are not in here (they run inside vanilla's own timings). Two timestamps per entry point per frame, only when Debug is on.
    internal static class EbmSelfCost
    {
        private static readonly string[] Names = { "plugin Update (ticks, bake pumps)", "plugin LateUpdate (all draws)", "far Update (jobs + parts)", "far LateUpdate" };
        private static readonly long[] s_ticks = new long[4], s_worst = new long[4], s_frame = new long[4];
        private static int s_frames, s_lastFrame = -1; private static float s_next; private static bool s_on;

        // 1.2.229 (lead / [fgn]: Mono's GC is non-generational, every collection a full ~2.9 GB sweep; the lever is allocation): managed bytes each
        // entry point / group allocates, from the Mono heap size before and after (a GC in between reads negative: that sample is dropped)
        private static long s_monoAtT, s_monoAtU; private static readonly long[] s_alloc = new long[4], s_uAlloc = new long[10]; private static float s_allocSince;
        private static long Mono() => UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();

        internal static long T()
        {
            if (Time.frameCount != s_lastFrame) Fold();
            if (s_on) s_monoAtT = Mono();
            return s_on ? Stopwatch.GetTimestamp() : 0L;
        }

        // 1.2.222 (lead: "plugin Update worst 115.9 ms"): plugin Update split by tick group, the worst frame's split kept and logged
        private static readonly string[] UNames = { "item/shelter/rain/wet/light ticks", "fill tracker", "bake summary + damage replay", "zone tracker (bake decisions, pumps)", "zone keepalive", "coverage reach", "caster census", "trace heartbeat", "cache preload kick", "sector mirror + zsync report" };   // 1.2.224: + the rest of plugin Update (lead: "130 ms with every sub-timer 0")   // 1.2.223: 1 split into fill / reach / census (lead: "27.8 ms in fill + coverage reach + census")
        private static readonly long[] s_u = new long[10], s_uWorst = new long[10]; private static long s_uWorstTotal;
        private static readonly long[] s_uSum = new long[10], s_uMax = new long[10];   // 1.2.254: per-group average and own worst, Debug only

        // 1.2.254 [Optimize] TickGroupEventDriven: read by the shelter / wet / light ticks. Never on a dedicated server (those ticks have no player there).
        internal const int SubProbe = 0, SubRain = 1, SubWetWalk = 2, SubWetFollow = 3, SubLightRank = 4, SubLightList = 5, SubCount = 6;
        private static readonly string[] SubNames = { "shelter probe", "rain hider sweep", "wet walk", "wet follow-global", "light rank", "light relist" };
        private static readonly int[] s_subRun = new int[SubCount], s_subSkip = new int[SubCount];
        private static bool s_saidEvents;
        internal static void Sub(int i, bool ran) { if (ran) s_subRun[i]++; else s_subSkip[i]++; }
        internal static bool EventDriven
        {
            get
            {
                var k = FiresEasyBakeMeshesPlugin.TickGroupEventDriven;
                if (k == null || !k.Value || (ZNet.instance != null && ZNet.instance.IsDedicated())) return false;
                if (!s_saidEvents) { s_saidEvents = true; EasyBakeLog.Info("[EBM] tick group event-driven: shelter, wet and light checks now follow changes, with slow timed fallbacks"); }
                return true;
            }
        }
        internal static long UT() { if (!s_on) return 0L; s_monoAtU = Mono(); return Stopwatch.GetTimestamp(); }
        internal static void U(int i, long t0) { if (t0 == 0L) return; s_u[i] += Stopwatch.GetTimestamp() - t0; long d = Mono() - s_monoAtU; if (d > 0) s_uAlloc[i] += d; }

        // 1.2.224 (lead: "48.8 ms in zone tracker"): the worst ZoneTracker frame's own phase split (walking zones, baking, pumps ...),
        // from InstancedDraw's phase sums read before and after that one call. Debug only.
        private static double[] s_ztBefore, s_ztWorst; private static double s_ztWorstTotal;
        // ZoneTracker step split, Debug only: the steps of the "walking zones" phase and the tail of Update that no phase covers, kept for the worst frame and averaged; the last two run outside Update.
        // Off: ZT is one bool read and returns 0, ZAcc returns on that 0. Inside-branch steps (save, park, unpark, queue) are also inside their branch.
        internal const int ZsUnload = 0, ZsUnloadSave = 1, ZsPark = 2, ZsReload = 3, ZsUnpark = 4, ZsQueueStandIns = 5, ZsFarTier = 6, ZsGiveBack = 7, ZsFlushStandIns = 8, ZsEmptyTeardown = 9, ZsReannounce = 10, ZsHiddenRelease = 11, ZsInsideUpdate = 12, ZsIndirectDrop = 12, ZsPieceDestroyed = 13, ZsCount = 14;
        private static readonly string[] ZsNames = { "unload branch", "unload save (in branch)", "stand-in park (in branch)", "reload branch", "unpark (in branch)", "queue stand-ins (in branch)", "far tier", "give-back pump", "tail stand-in events", "tail empty-zone teardown", "tail re-announce", "tail hidden release", "indirect group drop (LateUpdate)", "OnPieceDestroyed (end of frame)" };
        private static readonly long[] s_zs = new long[ZsCount], s_zsWorst = new long[ZsCount], s_zsSum = new long[ZsCount], s_zsMax = new long[ZsCount], s_zsN = new long[ZsCount];
        private static int s_ztGc; private static bool s_ztWorstGc;
        internal static long ZT() => s_on ? Stopwatch.GetTimestamp() : 0L;
        internal static void ZAcc(int i, long t0, int n = 1) { if (t0 == 0L) return; s_zs[i] += Stopwatch.GetTimestamp() - t0; s_zsN[i] += n; }
        internal static void ZtBegin()
        {
            if (!s_on) return;
            int n = InstancedDraw.PhaseCount;
            if (s_ztBefore == null) { s_ztBefore = new double[n]; s_ztWorst = new double[n]; }
            for (int i = 0; i < n; i++) s_ztBefore[i] = InstancedDraw.PhaseMsRaw(i);
            s_ztGc = System.GC.CollectionCount(0);
        }
        internal static void ZtEnd(long t0)
        {
            if (!s_on || t0 == 0L || s_ztBefore == null) return;
            double total = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (total <= s_ztWorstTotal) return;
            s_ztWorstTotal = total;
            for (int i = 0; i < s_ztWorst.Length; i++) s_ztWorst[i] = System.Math.Max(0.0, InstancedDraw.PhaseMsRaw(i) - s_ztBefore[i]);
            System.Array.Copy(s_zs, s_zsWorst, ZsCount); s_ztWorstGc = System.GC.CollectionCount(0) != s_ztGc;
        }
        internal static void Acc(int i, long t0) { if (t0 == 0L) return; s_frame[i] += Stopwatch.GetTimestamp() - t0; long d = Mono() - s_monoAtT; if (d > 0) s_alloc[i] += d; }

        private static void Fold()
        {
            if (s_lastFrame >= 0 && s_on)
            {
                if (s_frame[0] > s_uWorstTotal) { s_uWorstTotal = s_frame[0]; System.Array.Copy(s_u, s_uWorst, 10); }
                for (int i = 0; i < 10; i++) { s_uSum[i] += s_u[i]; if (s_u[i] > s_uMax[i]) s_uMax[i] = s_u[i]; }   // 1.2.254
                System.Array.Clear(s_u, 0, 10);
                for (int i = 0; i < ZsCount; i++) { s_zsSum[i] += s_zs[i]; if (s_zs[i] > s_zsMax[i]) s_zsMax[i] = s_zs[i]; s_zs[i] = 0; }
                for (int i = 0; i < 4; i++) { s_ticks[i] += s_frame[i]; if (s_frame[i] > s_worst[i]) s_worst[i] = s_frame[i]; s_frame[i] = 0; }
                s_frames++;
            }
            s_lastFrame = Time.frameCount;
            s_on = EasyBakeLog.DebugOn();
            if (!s_on || Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 30f;
            if (s_frames == 0) return;
            double ms = 1000.0 / Stopwatch.Frequency, total = 0;
            var sb = new System.Text.StringBuilder("[EBM] own cost, ms a frame over ").Append(s_frames).Append(" frames:");
            for (int i = 0; i < 4; i++) { double a = s_ticks[i] * ms / s_frames; total += a; sb.Append(' ').Append(Names[i]).Append(' ').Append(a.ToString("0.000")).Append(" (worst ").Append((s_worst[i] * ms).ToString("0.0")).Append(");"); }
            sb.Append(" total ").Append(total.ToString("0.000")).Append(" ms a frame (Harmony patches on vanilla methods are counted inside vanilla's own timings). Worst plugin Update ").Append((s_uWorstTotal * ms).ToString("0.0")).Append(" ms, split:");
            for (int i = 0; i < 10; i++) sb.Append(' ').Append(UNames[i]).Append(' ').Append((s_uWorst[i] * ms).ToString("0.0")).Append(';');
            { long named = 0; for (int i = 0; i < 10; i++) named += s_uWorst[i]; sb.Append(" untimed rest ").Append((System.Math.Max(0L, s_uWorstTotal - named) * ms).ToString("0.0")).Append(';'); }
            // 1.2.254: every group's average beside its own worst single frame (the split above is only the worst whole frame's)
            sb.Append(" Plugin Update groups, average ms a frame (own worst frame):");
            for (int i = 0; i < 10; i++) { sb.Append(' ').Append(UNames[i]).Append(' ').Append((s_uSum[i] * ms / s_frames).ToString("0.000")).Append(" (").Append((s_uMax[i] * ms).ToString("0.0")).Append(");"); s_uSum[i] = 0; s_uMax[i] = 0; }
            sb.Append(" Tick checks run / skipped (TickGroupEventDriven ").Append(FiresEasyBakeMeshesPlugin.TickGroupEventDriven != null && FiresEasyBakeMeshesPlugin.TickGroupEventDriven.Value ? "on" : "off").Append("):");
            for (int i = 0; i < SubCount; i++) { sb.Append(' ').Append(SubNames[i]).Append(' ').Append(s_subRun[i]).Append('/').Append(s_subSkip[i]).Append(';'); s_subRun[i] = 0; s_subSkip[i] = 0; }
            if (s_ztWorst != null && s_ztWorstTotal > 0.0)
            {
                sb.Append(" Worst zone tracker frame ").Append(s_ztWorstTotal.ToString("0.0")).Append(" ms, its phases:");
                for (int i = 0; i < s_ztWorst.Length; i++) sb.Append(' ').Append(InstancedDraw.PhaseName(i)).Append(' ').Append(s_ztWorst[i].ToString("0.0")).Append(';');
                sb.Append(" its steps:");
                double named = 0.0; for (int i = 0; i < s_ztWorst.Length; i++) named += s_ztWorst[i];
                for (int i = 0; i < ZsInsideUpdate; i++) { double v = s_zsWorst[i] * ms; if (i >= ZsFlushStandIns) named += v; sb.Append(' ').Append(ZsNames[i]).Append(' ').Append(v.ToString("0.0")).Append(';'); }
                sb.Append(" in no phase or tail step ").Append(System.Math.Max(0.0, s_ztWorstTotal - named).ToString("0.0")).Append("; GC ran in it: ").Append(s_ztWorstGc ? "yes" : "no").Append(';');
                s_ztWorstTotal = 0.0;
            }
            sb.Append(" Zone tracker steps, average ms a frame (own worst frame; times):");
            for (int i = 0; i < ZsCount; i++) { sb.Append(' ').Append(ZsNames[i]).Append(' ').Append((s_zsSum[i] * ms / s_frames).ToString("0.000")).Append(" (").Append((s_zsMax[i] * ms).ToString("0.0")).Append("; ").Append(s_zsN[i]).Append(");"); s_zsSum[i] = 0; s_zsMax[i] = 0; s_zsN[i] = 0; }
            { float secs = Mathf.Max(1f, Time.unscaledTime - s_allocSince); s_allocSince = Time.unscaledTime; long tot = 0; sb.Append(" Managed heap growth during EBM's time (approx., process-wide, an upper bound; KB/s):"); for (int i = 0; i < 4; i++) { tot += s_alloc[i]; sb.Append(' ').Append(Names[i]).Append(' ').Append((s_alloc[i] / 1024f / secs).ToString("0.0")).Append(';'); s_alloc[i] = 0; } sb.Append(" total ").Append((tot / 1024f / secs).ToString("0.0")).Append("; plugin Update groups:"); for (int i = 0; i < 10; i++) { if (s_uAlloc[i] > 0) sb.Append(' ').Append(UNames[i]).Append(' ').Append((s_uAlloc[i] / 1024f / secs).ToString("0.0")).Append(';'); s_uAlloc[i] = 0; } }
            EasyBakeLog.Debug(sb.ToString());
            for (int i = 0; i < 4; i++) { s_ticks[i] = 0; s_worst[i] = 0; }
            s_uWorstTotal = 0; System.Array.Clear(s_uWorst, 0, 10);
            s_frames = 0;
        }
    }
}
