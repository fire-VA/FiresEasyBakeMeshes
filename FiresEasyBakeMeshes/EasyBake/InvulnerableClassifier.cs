using System.Collections.Generic;

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

        public static void Forget(WearNTear wnt)
        {
            if (wnt != null) _cache.Remove(wnt);
        }

        public static void Reset() => _cache.Clear();

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
