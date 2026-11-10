using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.124 ([visual], [seasons] Tools\SNOW_PERF_DIAG_20261026.md): the roof / shelter answer for a test point, cached for a while.
    // Snow coverage changes often (weather, season, melt) but a roof over a spot almost never does, and EnsureSnow re-ran one
    // FTS SnowExposureAt raycast per instance on every change (460 for one stone wall group). Keyed by the test point rounded to
    // 0.25 m. An entry lives 30 s, so a roof built or removed shows in the snow within half a minute. Capped at 20000 entries.
    internal static class ExposureCache
    {
        private const float LifeSeconds = 30f;
        private const int MaxEntries = 20000;
        private static readonly Dictionary<Vector3Int, (float Exposure, float Until)> s_cache = new Dictionary<Vector3Int, (float, float)>();
        private static int s_hits, s_misses;
        private static float s_nextReport;

        internal static float Get(Vector3 top, Vector3 up)
        {
            float now = Time.unscaledTime;
            var key = new Vector3Int(Mathf.RoundToInt(top.x * 4f), Mathf.RoundToInt(top.y * 4f), Mathf.RoundToInt(top.z * 4f));
            if (s_cache.TryGetValue(key, out var e) && e.Until > now) { s_hits++; Report(now); return e.Exposure; }
            if (s_cache.Count >= MaxEntries) s_cache.Clear();
            float exposure = SnowFeed.ExposureAt(top, up);
            s_cache[key] = (exposure, now + LifeSeconds);
            s_misses++;
            Report(now);
            return exposure;
        }

        private static void Report(float now)
        {
            if (now < s_nextReport) return;
            s_nextReport = now + 120f;
            if (s_hits + s_misses == 0) return;
            EasyBakeLog.Debug($"[Snow] roof tests: {s_hits} cached, {s_misses} cast in the last 2 min ({s_cache.Count} spots held)");
            s_hits = 0; s_misses = 0;
        }
    }
}
