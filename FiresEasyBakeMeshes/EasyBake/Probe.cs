using System.Diagnostics;
using BepInEx.Bootstrap;


namespace FiresEasyBakeMeshes.EasyBake
{
    // Records EasyBake's own hot-path timings into the shared FiresUnifiedCore
    // FrameTimer so they show up alongside the vanilla probes in
    // FiresDebugginTools' per-second report and on-screen overlay under
    // "EasyBake:" labels. This makes EasyBake's per-frame overhead measurable;
    // its WINS (reduced vanilla cost) are read from the existing vanilla labels
    // (ZNetScene.CreateDestroyObjects, ClutterSystem.LateUpdate, ZSFX, etc.).
    //
    // Gated on FiresDebugginTools being installed: that mod owns the rotate/dump
    // that clears FrameTimer's accumulator. Without it nothing would ever drain
    // the Stats dictionary, so recording into it would leak. When the profiler
    // is absent, Start() returns 0 and Stop() bails — near-zero overhead, same
    // pattern as the vanilla ProbeHelpers.
    internal static class Probe
    {
        private const string DebugToolsGuid = "com.Fire.FiresDebugginTools";
        private static bool _profilerPresent;

        public static void Detect()
        {
            _profilerPresent = true;   // 1.2.208: no PluginInfos lookup; the timer only runs when a FrameTimer was registered through EbmApi (CoreLink.TimerEnabled)
        }

        public static long Start() =>
            (_profilerPresent && CoreLink.TimerEnabled) ? Stopwatch.GetTimestamp() : 0L;

        public static void Stop(string label, long start)
        {
            if (start == 0L) return;
            CoreLink.Record(label, Stopwatch.GetTimestamp() - start);
        }
    }
}
