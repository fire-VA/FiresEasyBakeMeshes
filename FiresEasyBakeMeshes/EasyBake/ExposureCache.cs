using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.124 ([visual], [seasons] Tools\SNOW_PERF_DIAG_20261026.md): the roof / shelter answer for a test point, cached for a while.
    // Snow coverage changes often (weather, season, melt) but a roof over a spot almost never does, and EnsureSnow re-ran one
    // FTS SnowExposureAt raycast per instance on every change (460 for one stone wall group). Keyed by the test point rounded to
    // 0.25 m. An entry lives 30 s, so a roof built or removed shows in the snow within half a minute. Capped at 20000 entries.
    // 1.2.354 ([2ic], lead 11-13; run_16:22712 "(raining) ... worst frame of EBM re-split 5.0 ms (Core coverage 0.0 ms, exposure test (FTS / roof ray)
    // 4.4 ms ...)"): in rain every instance asks this (the dry flag), so a miss is the arrival hitch.
    //  - Keyed by the piece's prefab hash + its test point (lead's ask), so two pieces never share an answer.
    //  - Core's shelter grid is NOT used: it holds ground-level 1 m columns (SnowShelter.cs: "a ray up from ground + 0.5 m"), while this asks at
    //    the TOP of a piece; a roof piece over a sheltered column is itself open (it would draw dry in rain = a look change). Works without Core.
    //  - Full: expired entries are evicted first (before 1.2.354 the whole cache was cleared at 20000, so a town re-cast every spot), and
    //    only when every entry is still live is it cleared (counted).
    //  - Life stays 30 s (a new roof still shows within half a minute: no look change).
    //  - Proof: every 64th hit is re-asked fresh and its dry flag compared (mismatches counted); counts + miss time go to the 30 s [SnowBake] line.
    internal static class ExposureCache
    {
        private const float LifeSeconds = 30f;
        private const int MaxEntries = 20000;
        private const int VerifyEvery = 64;

        private struct Key : IEquatable<Key>
        {
            public int Prefab, X, Y, Z;
            public bool Equals(Key o) => Prefab == o.Prefab && X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() { unchecked { int h = Prefab; h = h * 486187739 + X; h = h * 486187739 + Y; return h * 486187739 + Z; } }
        }

        private static readonly Dictionary<Key, (float Exposure, float Until)> s_cache = new Dictionary<Key, (float, float)>();
        private static readonly List<Key> s_evict = new List<Key>();
        private static int s_hits, s_misses;
        private static float s_nextReport;
        // 1.2.354: the 30 s counters ([SnowBake] passes line)
        private static int c_hits, c_misses, c_expired, c_evicted, c_cleared, c_verified, c_mismatch, s_hitSerial;
        private static long c_missTicks;
        // 1.2.356 ([lead] 11-13: 1.2.354's rain arrival legs kept a 4.1 ms worst re-split frame with the budget checked before every instance, while a
        // cast averaged 0.18-0.20 ms (B: 3284 casts in 580.3 ms): only single casts near 2 ms explain it. The slowest single cast of the window, and
        // how many took 1 ms or more, prove or kill that FTS exposure spike.
        private static long c_worstCastTicks; private static int c_slowCasts;
        private static readonly long OneMsTicks = System.Diagnostics.Stopwatch.Frequency / 1000;

        internal static float Get(int prefab, Vector3 top, Vector3 up)
        {
            float now = Time.unscaledTime;
            var key = new Key { Prefab = prefab, X = Mathf.RoundToInt(top.x * 4f), Y = Mathf.RoundToInt(top.y * 4f), Z = Mathf.RoundToInt(top.z * 4f) };
            if (s_cache.TryGetValue(key, out var e))
            {
                if (e.Until > now)
                {
                    s_hits++; c_hits++; Report(now);
                    if (++s_hitSerial >= VerifyEvery)
                    {
                        s_hitSerial = 0;
                        float fresh = SnowFeed.ExposureAt(top, up);
                        c_verified++;
                        if ((fresh <= 0.001f) != (e.Exposure <= 0.001f)) c_mismatch++;
                    }
                    return e.Exposure;
                }
                c_expired++;
            }
            if (s_cache.Count >= MaxEntries) MakeRoom(now);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float exposure = SnowFeed.ExposureAt(top, up);
            long castTicks = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            c_missTicks += castTicks;
            if (castTicks > c_worstCastTicks) c_worstCastTicks = castTicks;   // 1.2.356: the slowest single cast
            if (castTicks >= OneMsTicks) c_slowCasts++;
            s_cache[key] = (exposure, now + LifeSeconds);
            s_misses++; c_misses++;
            Report(now);
            return exposure;
        }

        // 1.2.354: expired entries go first; a full cache of live entries is cleared as before (counted)
        private static void MakeRoom(float now)
        {
            s_evict.Clear();
            foreach (var kv in s_cache) if (kv.Value.Until <= now) s_evict.Add(kv.Key);
            for (int i = 0; i < s_evict.Count; i++) s_cache.Remove(s_evict[i]);
            c_evicted += s_evict.Count;
            s_evict.Clear();
            if (s_cache.Count >= MaxEntries) { s_cache.Clear(); c_cleared++; }
        }

        /// <summary>1.2.354: the last 30 s of roof tests for the [SnowBake] line; resets the counters.</summary>
        internal static string Take30s()
        {
            double missMs = c_missTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double worstMs = c_worstCastTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string s = $"roof tests: {c_hits} cached, {c_misses} cast ({missMs:0.0} ms in casts, slowest single cast {worstMs:0.00} ms, {c_slowCasts} cast(s) of 1 ms or more; exposure from {SnowFeed.ExposureOwner}), {c_expired} expired, {c_evicted} evicted, {c_cleared} full clear(s), {s_cache.Count} held; dry flag re-checked {c_verified}, mismatches {c_mismatch}";
            c_hits = c_misses = c_expired = c_evicted = c_cleared = c_verified = c_mismatch = 0; c_missTicks = 0; c_worstCastTicks = 0; c_slowCasts = 0;
            return s;
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
