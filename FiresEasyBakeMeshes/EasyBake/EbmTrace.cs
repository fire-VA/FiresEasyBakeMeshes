using System;
using System.Reflection;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.95 (the lead 10-13: native client crash hunt, ntdll c0000005 +0x3ed12). EBM's native steps (mesh combine, cached-mesh rebuild,
    // batch/parent destroy, hidden-bake release, the time-sliced object destroy) go into FAT's crash-proof trace file
    // (BepInEx\FiresTests\fat_native_trace.log, written and flushed per line BEFORE the step), so one tail covers both mods.
    // Found by reflection: FAT 0.2.895+'s public VerdantsAscent.Terrain.Voxel.FatNativeTrace.Step(string, int, string). Without it EBM
    // traces nothing. FAT's own config toggle ([VoxelTerrain Performance] "Native crash trace") switches both off.
    internal static class EbmTrace
    {
        private static Action<string, int, string> s_step;
        private static bool s_looked;

        // 1.2.96 (the lead 10-13, crash at 18:56: the trace ended in a cds destroy burst 3 s before the crash, then nothing). A heartbeat
        // twice a second bounds where an untraced crash happened; for 3 s after any destroy batch, the instanced draw and the frame end
        // are traced too, so a crash in the deferred destroy or the next draw leaves its last line.
        private static float s_hotUntil, s_nextBeat;
        internal static void NoteDestroyActivity() => s_hotUntil = UnityEngine.Time.unscaledTime + 3f;
        internal static bool Hot => UnityEngine.Time.unscaledTime < s_hotUntil;
        internal static void Heartbeat()
        {
            float now = UnityEngine.Time.unscaledTime;
            if (now < s_nextBeat) return;
            s_nextBeat = now + 0.5f;
            Step("heartbeat", UnityEngine.Time.frameCount, null);
        }

        internal static Action<string, int, string> Registered;

        internal static void Step(string step, int id = 0, string detail = null)
        {
            if (!s_looked)
            {
                s_looked = true;
                try
                {
                    s_step = Registered;   // 1.2.208: a native trace registers through EbmApi.RegisterTrace; EBM never looks FAT up
                }
                catch { s_step = null; }
            }
            if (s_step == null) return;
            try { s_step("EBM " + step, id, detail); } catch { }
        }
    }
}
