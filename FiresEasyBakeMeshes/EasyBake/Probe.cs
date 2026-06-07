using System.Diagnostics;
using BepInEx.Bootstrap;
using FiresCore.Diagnostics;

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
            _profilerPresent = Chainloader.PluginInfos.ContainsKey(DebugToolsGuid);
        }

        public static long Start() =>
            (_profilerPresent && FrameTimer.Enabled) ? Stopwatch.GetTimestamp() : 0L;

        public static void Stop(string label, long start)
        {
            if (start == 0L) return;
            FrameTimer.Record(label, Stopwatch.GetTimestamp() - start);
        }
    }
}
