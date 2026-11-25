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

        // ---- 1.2.491 [Hitch-EBM] ([visual], [lead] GO 11-22: 322 untimed hitches around EBM's create/destroy handoff in Fire's 03:52 log) ----
        // What EBM handed to Unity in ONE frame: Object.Destroy is deferred to the end of that frame (plus physics after collider changes) and a new
        // mesh uploads at render, so no stopwatch around the call sees that cost. Counted per frame (an int add per event, no allocation); when the
        // frame took 50 ms or more, the NEXT frame's LateUpdate prints one Debug line with that frame's counts, to read next to FDT's [Hitch] line.
        internal const int HfStandIn = 0, HfColliders = 1, HfParent = 2, HfParentMeshes = 3, HfParentVerts = 4, HfCacheBuild = 5, HfCacheVerts = 6, HfCds = 7, HfHidden = 8;
        private const int HfSlots = 9;
        private static readonly long[] s_hfCur = new long[HfSlots], s_hfPrev = new long[HfSlots];
        private static int s_hfFrame = -1, s_hfPrevFrame = -1;
        private static void HfRoll()
        {
            int f = UnityEngine.Time.frameCount;
            if (f == s_hfFrame) return;
            bool next = f == s_hfFrame + 1;
            for (int i = 0; i < HfSlots; i++) { s_hfPrev[i] = next ? s_hfCur[i] : 0; s_hfCur[i] = 0; }
            s_hfPrevFrame = next ? s_hfFrame : -1;
            s_hfFrame = f;
        }
        // MAIN THREAD ONLY ([2ic] 11-22): HfRoll reads Time.frameCount, which throws off the main thread; every call site destroys objects or builds a Mesh (Unity API)
        internal static void Hf(int slot, long n = 1) { HfRoll(); s_hfCur[slot] += n; }
        // Plugin.LateUpdateInner, first thing every frame: report the previous frame if it was a hitch and EBM handed anything to Unity in it
        internal static void HitchFrameTick()
        {
            HfRoll();
            if (s_hfPrevFrame < 0 || s_hfPrevFrame != s_hfFrame - 1) return;
            float dt = UnityEngine.Time.unscaledDeltaTime;
            if (dt < 0.05f) return;
            bool any = false; for (int i = 0; i < HfSlots; i++) if (s_hfPrev[i] != 0) { any = true; break; }
            if (!any || !EasyBakeLog.DebugOn()) return;
            var p = s_hfPrev;
            EasyBakeLog.Debug($"[Hitch-EBM] frame {s_hfPrevFrame} ({dt * 1000f:0} ms): stand-in destroys {p[HfStandIn]} (colliders detached {p[HfColliders]}), "
                + $"parent destroys {p[HfParent]} (batch meshes {p[HfParentMeshes]}, verts {p[HfParentVerts]}), cache mesh builds {p[HfCacheBuild]} (verts {p[HfCacheVerts]}), "
                + $"cds destroys {p[HfCds]}, hidden releases {p[HfHidden]} (Object.Destroy runs at the end of that frame; new meshes upload at render)");
        }

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
