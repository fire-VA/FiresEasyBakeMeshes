using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.256 [Optimize] IndirectCrashFallback (default on). A driver or D3D11 crash on the GPU-driven path kills the process, so no C# catch can latch it: the next launch would crash the same way.
    // EBM leaves a small marker file while the path has been on for less than GoodSeconds, and removes it on a clean quit or after that time. If the marker survived to the next launch, the
    // previous Unity log (Player-prev.log, read without locking) decides: only a crash with graphics evidence counts, so a killed process, a power cut or another mod's crash does not. That one
    // session then uses the instanced path; the marker is already gone, so the launch after it tries the GPU path again. The player's config is never written. ebm_indirect on overrides it.
    internal static class IndirectCrashGuard
    {
        private const string FileName = "indirect_running.txt";
        private const float GoodSeconds = 600f;
        private const int TailBytes = 512 * 1024;
        private static bool s_checked, s_marked;
        private static string s_reason;
        private static float s_clearAt = -1f;

        private static bool On => FiresEasyBakeMeshesPlugin.IndirectCrashFallback == null || FiresEasyBakeMeshesPlugin.IndirectCrashFallback.Value;
        private static string MarkerPath => MeshCacheStore.BaseDir == null ? null : Path.Combine(MeshCacheStore.BaseDir, FileName);

        // Asked before the path goes on; the file work happens once a session.
        internal static bool FallBackThisSession(out string why)
        {
            if (!s_checked) { s_checked = true; Check(); }
            why = s_reason;
            return s_reason != null;
        }

        // Once a frame while the path is on: one bool test and one float compare once the marker is written.
        internal static void Frame()
        {
            if (!s_marked) { s_marked = true; WriteMarker(); return; }
            if (s_clearAt >= 0f && Time.unscaledTime >= s_clearAt) { s_clearAt = -1f; RemoveMarker(); }
        }

        internal static void CleanExit() => RemoveMarker();

        private static void WriteMarker()
        {
            if (!On) return;
            try
            {
                string path = MarkerPath;
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Application.version + "\n");
                s_clearAt = Time.unscaledTime + GoodSeconds;
            }
            catch (Exception e) { EasyBakeLog.Debug("[Indirect] crash marker not written: " + e.Message); }
        }

        private static void RemoveMarker()
        {
            try { string path = MarkerPath; if (path != null && File.Exists(path)) File.Delete(path); }
            catch (Exception e) { EasyBakeLog.Debug("[Indirect] crash marker not removed: " + e.Message); }
        }

        private static void Check()
        {
            try
            {
                string path = MarkerPath;
                if (path == null || !File.Exists(path)) return;
                DateTime markedAt = File.GetLastWriteTimeUtc(path);
                File.Delete(path);   // whatever the verdict: one launch only, and the next one tries again
                if (!On) return;
                string found = FindGpuCrash(markedAt);
                if (found == null) { EasyBakeLog.Debug("[Indirect] the last session ended without a clean quit, with no graphics crash in its log (killed, or another cause): the GPU path stays on"); return; }
                s_reason = "the last session crashed in the graphics driver on this path (" + found + "), so this one session uses the instanced path; the next launch tries it again, or ebm_indirect on now";
                EasyBakeLog.Warn("[Indirect] " + s_reason);
            }
            catch (Exception e) { EasyBakeLog.Debug("[Indirect] crash marker check skipped: " + e.Message); }
        }

        // What in the previous log shows a graphics crash, or null. A log older than the marker is some other session's.
        private static string FindGpuCrash(DateTime markedAt)
        {
            string current = Application.consoleLogPath;
            if (string.IsNullOrEmpty(current)) return null;
            string previous = Path.Combine(Path.GetDirectoryName(current), "Player-prev.log");
            if (!File.Exists(previous) || File.GetLastWriteTimeUtc(previous) < markedAt) return null;
            string text;
            using (var fs = new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int length = (int)Math.Min(fs.Length, TailBytes);
                fs.Seek(-length, SeekOrigin.End);
                var bytes = new byte[length];
                int got = 0;
                while (got < length) { int n = fs.Read(bytes, got, length - got); if (n <= 0) break; got += n; }
                text = Encoding.UTF8.GetString(bytes, 0, got);
            }
            foreach (string hard in new[] { "failed to create buffer", "DEVICE_REMOVED", "DEVICE_HUNG", "DEVICE_RESET" })
                if (text.IndexOf(hard, StringComparison.OrdinalIgnoreCase) >= 0) return hard;
            if (text.IndexOf("Crash!!!", StringComparison.Ordinal) < 0) return null;
            foreach (string gfx in new[] { "d3d11", "dxgi", "nvlddmkm", "nvwgf2um", "atidxx", "igd10" })
                if (text.IndexOf(gfx, StringComparison.OrdinalIgnoreCase) >= 0) return "a crash naming " + gfx;
            return null;
        }
    }
}
