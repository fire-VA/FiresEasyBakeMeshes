using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Two recognised conventions for "this piece cannot be damaged in gameplay":
    //
    //   1. Negative-health convention (InfinityHammer default, WorldEdit, custom
    //      blueprints). WearNTear.ApplyDamage early-returns without applying
    //      damage when current health is <= 0 (assembly_valheim/WearNTear.cs:755).
    //      A piece with m_health = -1 (or its ZDO s_health key = -1) is therefore
    //      indestructible. This is the dominant convention on player-built
    //      megabases. Check BOTH the prefab default and the live ZDO value: the
    //      ZDO override wins at runtime, but the prefab default catches pieces
    //      whose creator baked invulnerability into the prefab itself.
    //
    //   2. All-Immune damage modifiers convention. WearNTear.m_damages.* all set
    //      to HitData.DamageModifier.Immune. Less common in modded bases but
    //      used by certain admin/protected-area workflows. ApplyResistance filters
    //      every damage type out before ApplyDamage is even called.
    //
    // Either convention qualifies. Result is cached per-WearNTear-instance so we
    // pay the check once per piece and not on every ZoneTracker scan.
    internal static class InvulnerableClassifier
    {
        private static readonly Dictionary<WearNTear, bool> _cache = new Dictionary<WearNTear, bool>();

        public static bool IsInvulnerable(WearNTear wnt)
        {
            if (wnt == null) return false;
            if (_cache.TryGetValue(wnt, out var cached)) return cached;
            bool result;
            using (EbmPatchMarkers.InvulnerableJudge.Auto()) result = HasNegativeHealth(wnt) || AllImmune(wnt.m_damages);   // next EBM: profiler marker only (cache miss)
            _cache[wnt] = result;
            return result;
        }

        // 1.2.167 ([generator] B3, B5, checked in WearNTear.cs): what UpdateWear and UpdateCover may skip is narrower than "cannot be hit". UpdateWear calls ApplyDamage directly,
        // past ApplyResistance, so an all-Immune piece still wears and collapses; only the health <= 0 return in ApplyDamage makes it harmless. And snow lives in
        // UpdateWear, so a piece that can show snow in the Deep North needs it to run.
        public static bool CanSkipWear(WearNTear wnt)
        {
            if (wnt == null) return false;
            // 1.2.498 ([lead] 11-22, Fire shots 243-245, [seasons]' dry run: 7 shovel-held stairs at Raven's Rest, 0 stored snow, mound still drawn): vanilla hides a
            // piece's snow mound only in UpdateSnowVisual, which runs at the end of UpdateWear (WearNTear.cs:493-495 -> :869), at Awake and in ChangeSnow. A writer
            // that zeroes the build-up without that refresh (Vedr's shovel hold, its PieceSnowVanilla.cs:295) leaves the mound on, and skipping UpdateWear here took
            // away the last refresh, so it stayed until a relog. While any snow mound child is drawn, the piece keeps its wear visits (one is enough to hide it).
            // Checked BEFORE the cache: a cached "skip" must not freeze a visible mound. Three activeSelf reads per call.
            if (SnowMoundShown(wnt)) return false;
            if (_skipWear.TryGetValue(wnt, out var cached)) return cached;
            bool result;
            using (EbmPatchMarkers.WearSkipJudge.Auto()) result = DecideSkipWear(wnt);   // next EBM: profiler marker only (cache miss; the hit path is not wrapped, see BakeFrameSplit.cs)
            _skipWear[wnt] = result;
            return result;
        }

        // 1.2.498: a snow mound (vanilla's m_snow / m_snowWorn / m_snowBroken children) is drawn on this piece right now
        private static bool SnowMoundShown(WearNTear wnt) =>
            (wnt.m_snow != null && wnt.m_snow.gameObject.activeSelf) || (wnt.m_snowWorn != null && wnt.m_snowWorn.gameObject.activeSelf) || (wnt.m_snowBroken != null && wnt.m_snowBroken.gameObject.activeSelf);

        private static bool DecideSkipWear(WearNTear wnt)
        {
            bool vanilla = AllImmuneWear != null && AllImmuneWear.Value;   // Fire 11-05: vanilla wear and support for all of them, behind a key
            if (vanilla) return false;
            return HasNegativeHealth(wnt) ? !BuildsSnowNow(wnt) : AllImmune(wnt.m_damages);
        }

        // 1.2.176/177 (Fire 11-07: piece snow never built up in winter at Raven's Rest on Infinity Hammer pieces): vanilla grows m_snowBuildup inside UpdateWear, and Vedr's
        // BuildingSnow remaps that Deep North check to its winter snow biomes. So UpdateWear runs wherever snow can build on this piece now: the real Deep North, a Vedr
        // snow-building biome (asked of Vedr directly, 1.2.177: no Core needed), or Core snow on the ground when Core is present. Safe: an invulnerable piece takes no damage in UpdateWear, and Vedr keeps heavy-snow damage to the Deep North.
        // Without Vedr (and Core) only the Deep North counts, as before.
        private static bool BuildsSnowNow(WearNTear wnt)
        {
            if (wnt.m_snow == null) return false;
            if (wnt.m_snowBuildup > 0f) return true;   // snow already on it keeps vanilla running, so whoever melts it in UpdateWear can
            Vector3 at = wnt.transform.position;
            if (WorldGenerator.IsDeepnorth(at.x, at.z)) return true;
            if (WorldGenerator.instance != null && VedrLink.IsSnowBuildingBiome(WorldGenerator.instance.GetBiome(at))) return true;
            return CoreLink.SnowBaseAt(at) > 0f;
        }

        // The skip answer is cached per piece, so a season change asks every cached piece again. Event driven (Vedr.VedrApi.VisualsChanged), one Info line per change.
        internal static void FollowSeasons()
        {
            VedrLink.OnVisualsChanged(OnSeasonChanged);
            VedrLink.OnVisualsChanged(ZoneTracker.RecheckSkippedForSeason);   // 1.2.180: skipped pieces asked again for winter snow
        }

        private static readonly List<WearNTear> s_recheck = new List<WearNTear>();

        private static void OnSeasonChanged(int season, int stage)
        {
            using (EbmPatchMarkers.WearSkipSeason.Auto())   // next EBM: profiler marker only
            try
            {
                s_recheck.Clear();
                s_recheck.AddRange(_skipWear.Keys);
                int nowRun = 0, nowSkip = 0, gone = 0;
                foreach (var wnt in s_recheck)
                {
                    if (wnt == null) { _skipWear.Remove(wnt); _cache.Remove(wnt); gone++; continue; }
                    bool was = _skipWear[wnt];
                    bool now = DecideSkipWear(wnt);
                    if (was == now) continue;
                    _skipWear[wnt] = now;
                    if (now) nowSkip++; else nowRun++;
                }
                s_recheck.Clear();
                EasyBakeLog.Info($"[Wear] season {season} stage {stage}: invulnerable pieces re-checked for snow, {nowRun} now run vanilla wear (snow builds), {nowSkip} skip again, {_skipWear.Count} cached, {gone} gone.");
            }
            catch (System.Exception ex) { EasyBakeLog.Warn("[Wear] season re-check failed: " + ex.Message); }
        }

        // Off keeps what stands today standing: running vanilla here can collapse all-Immune pieces in existing worlds, so it stays off until Fire says.
        internal static BepInEx.Configuration.ConfigEntry<bool> AllImmuneWear;

        private static readonly Dictionary<WearNTear, bool> _skipWear = new Dictionary<WearNTear, bool>();

        public static void Forget(WearNTear wnt)
        {
            if (wnt == null) return;
            _cache.Remove(wnt);
            _skipWear.Remove(wnt);
        }

        public static void Reset() { _cache.Clear(); _skipWear.Clear(); }

        // ---- 1.2.492 [WearSkip] census ([visual], [lead] GO 11-22: step 0 of "switch WearNTear off for invulnerable pieces", Tools\_visual_drafts_20261122\ebm_wearntear_disable_design.txt) ----
        // Read-only, Debug only, every 30 s: how many live pieces the wear skip judged and how many it can skip NOW (season / snow), so the disable is
        // sized before it is built. Counts only the skip cache (filled by the UpdateWear / UpdateCover prefixes); it judges nothing new and changes nothing.
        private static float s_nextCensus;
        private static readonly List<WearNTear> s_censusScratch = new List<WearNTear>();
        internal static void CensusTick()   // Plugin.LateUpdateInner, every frame: one key read while [90] WearSkipCensus is off (the default)
        {
            // [2ic] MUST: Fire records Debug in normal play, so the walk is behind its own key (default off; the rig sets it), never on Debug alone
            var key = FiresEasyBakeMeshesPlugin.WearSkipCensus; if (key == null || !key.Value) return;
            if (Time.unscaledTime < s_nextCensus) return;
            s_nextCensus = Time.unscaledTime + 30f;
            if (!EasyBakeLog.DebugOn()) return;
            using (EbmPatchMarkers.WearSkipCensus.Auto())   // next EBM: profiler marker only (past the key, timer and Debug gates)
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int live = WearNTear.GetAllInstances().Count;
                int judged = 0, skip = 0, skipNeg = 0, skipImmune = 0, run = 0, runSnow = 0, gone = 0;
                s_censusScratch.Clear(); s_censusScratch.AddRange(_skipWear.Keys);
                foreach (var wnt in s_censusScratch)
                {
                    if (wnt == null) { gone++; continue; }
                    judged++;
                    bool neg = HasNegativeHealth(wnt);
                    if (_skipWear[wnt]) { skip++; if (neg) skipNeg++; else skipImmune++; }
                    else { run++; if (neg && (AllImmuneWear == null || !AllImmuneWear.Value)) runSnow++; }   // invulnerable, still running = its snow builds now
                }
                s_censusScratch.Clear();
                EasyBakeLog.Debug($"[WearSkip] live WearNTear {live}; judged by the wear skip {judged} ({gone} destroyed entries); can skip now {skip} "
                    + $"(negative health {skipNeg}, all-immune {skipImmune}); run vanilla wear {run}, of them invulnerable but snow builds now {runSnow}; "
                    + $"census {sw.Elapsed.TotalMilliseconds:0.00} ms (1.2.492 step 0, read-only)");
            }
            catch (System.Exception ex) { EasyBakeLog.Debug("[WearSkip] census failed: " + ex.Message); }
        }

        private static bool HasNegativeHealth(WearNTear wnt)
        {
            // Strict less-than: zero health means "dying / queued for destroy",
            // which is a transient state we should NOT bake as invulnerable.
            // Negative is the unambiguous IH-style sentinel.
            if (wnt.m_health < 0f) return true;
            var nv = wnt.GetComponent<ZNetView>();
            if (nv == null) return false;
            var zdo = nv.GetZDO();
            if (zdo == null) return false;
            float health = zdo.GetFloat(ZDOVars.s_health, wnt.m_health);
            return health < 0f;
        }

        internal static bool AllImmune(HitData.DamageModifiers m)
        {
            return m.m_blunt     == HitData.DamageModifier.Immune
                && m.m_slash     == HitData.DamageModifier.Immune
                && m.m_pierce    == HitData.DamageModifier.Immune
                && m.m_chop      == HitData.DamageModifier.Immune
                && m.m_pickaxe   == HitData.DamageModifier.Immune
                && m.m_fire      == HitData.DamageModifier.Immune
                && m.m_frost     == HitData.DamageModifier.Immune
                && m.m_lightning == HitData.DamageModifier.Immune
                && m.m_poison    == HitData.DamageModifier.Immune
                && m.m_spirit    == HitData.DamageModifier.Immune;
        }
    }
}
