using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.219 ([visual], Fire: "vpo loads all this and shows the distant build pieces immediately, we take 3 min to settle"): one Info line per
    // arrival that PROVES the fill time, the same way on any stack (ours, VPO's, vanilla's: it only reads ZNetScene's instance count).
    //  - starts when the local player first exists (and again after a teleport's loading screen);
    //  - samples ZNetScene.NrOfInstances() once a second;
    //  - "settled" = under 1 % change across the last 10 samples, at least 15 s after the start (or a 600 s cap);
    //  - then: time to 95 % and 99.5 % of the settled count, the count at +30 / +60 / +120 / +180 s, and when far buildings first drew.
    // Cost: one int read a second while a fill is being watched; nothing once it has reported.
    internal static class FillTracker
    {
        private static bool s_watching, s_hadPlayer, s_wasTeleporting;
        private static float s_start, s_nextSample, s_farAt = -1f;
        private static readonly List<int> s_counts = new List<int>(600);

        internal static bool Watching => s_watching;
        internal static float SecondsWatching => s_watching ? Time.unscaledTime - s_start : 0f;

        private static float s_farFirstEver = -1f;   // 1.2.223 (lead: "none this fill" on a run whose far tier drew): far meshes built under the loading screen, before the player existed
        internal static void NoteFarDrawn() { if (s_farFirstEver < 0f) s_farFirstEver = Time.unscaledTime; if (s_watching && s_farAt < 0f) s_farAt = Time.unscaledTime - s_start; }

        internal static void Tick()
        {
            var p = Player.m_localPlayer;
            bool tele = p != null && p.IsTeleporting();
            if (tele && !s_wasTeleporting) s_farFirstEver = -1f;   // 1.2.223: a teleport starts: only far draws from here on count for its fill
            if (p != null && (!s_hadPlayer || (s_wasTeleporting && !tele))) Begin(s_hadPlayer ? "after a teleport" : "after the spawn");
            s_hadPlayer = p != null; s_wasTeleporting = tele;
            if (!s_watching || Time.unscaledTime < s_nextSample) return;
            s_nextSample = Time.unscaledTime + 1f;
            var scene = ZNetScene.instance;
            if (scene == null) { s_watching = false; return; }
            s_counts.Add(scene.NrOfInstances());
            float t = Time.unscaledTime - s_start;
            int n = s_counts.Count;
            bool settled = false;
            if (t >= 15f && n >= 10)
            {
                int now = s_counts[n - 1], then = s_counts[n - 10];
                settled = now > 0 && Mathf.Abs(now - then) <= now * 0.01f;
            }
            if (settled || t >= 600f) Report(settled);
        }

        private static string s_why;
        private static void Begin(string why)
        {
            s_watching = true; s_why = why; s_start = Time.unscaledTime; s_nextSample = s_start; s_farAt = -1f; s_counts.Clear();
        }

        private static void Report(bool settled)
        {
            s_watching = false;
            int n = s_counts.Count;
            if (n == 0) return;
            int final = s_counts[n - 1];
            int t95 = -1, t995 = -1;
            for (int i = 0; i < n; i++)
            {
                if (t95 < 0 && s_counts[i] >= final * 0.95f) t95 = i;
                if (t995 < 0 && s_counts[i] >= final * 0.995f) { t995 = i; break; }
            }
            string At(int s) => s < n ? s_counts[s].ToString("N0") : "-";
            EasyBakeLog.Info($"[Fill] {s_why}: {(settled ? "settled" : "NOT settled after 600 s")} at {final:N0} instances; 95 % at T+{t95} s, 99.5 % at T+{t995} s; " +
                             $"count at T+0 {At(0)}, +30 {At(30)}, +60 {At(60)}, +120 {At(120)}, +180 {At(180)} s; far buildings first drawn at " +
                             (s_farAt >= 0f ? "T+" + s_farAt.ToString("0.0") + " s" : s_farFirstEver >= 0f && s_farFirstEver <= s_start ? "before the spawn (T-" + (s_start - s_farFirstEver).ToString("0.0") + " s, under the loading screen)" : "- (none this fill)") + " (EBM's measure; reads only the instance count, so it compares across stacks)");
        }
    }
}
