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
            bool result = HasNegativeHealth(wnt) || AllImmune(wnt.m_damages);
            _cache[wnt] = result;
            return result;
        }

        // 1.2.167 ([generator] B3, B5, checked in WearNTear.cs): what UpdateWear and UpdateCover may skip is narrower than "cannot be hit". UpdateWear calls ApplyDamage directly,
        // past ApplyResistance, so an all-Immune piece still wears and collapses; only the health <= 0 return in ApplyDamage makes it harmless. And snow lives in
        // UpdateWear, so a piece that can show snow in the Deep North needs it to run.
        public static bool CanSkipWear(WearNTear wnt)
        {
            if (wnt == null) return false;
            if (_skipWear.TryGetValue(wnt, out var cached)) return cached;
            bool result = DecideSkipWear(wnt);
            _skipWear[wnt] = result;
            return result;
        }

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
