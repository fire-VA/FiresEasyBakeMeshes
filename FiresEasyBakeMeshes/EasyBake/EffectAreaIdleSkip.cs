using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ── THE LARGEST UPDATER LIST IN A BUILT WORLD, DOING NOTHING ──────────────────────────────────────────
    //
    // The 2026-09-26 va_updaters census on a town: EffectArea 848, ahead of LightFlicker 562 and
    // ZSyncTransform 354. In the same session MonoUpdaters.FixedUpdate(total) measured 68.5 ms/s - the single
    // biggest cost in the game - with every other EBM lever (ZSyncStaticSkip, ClutterLod, LightFlickerLod,
    // ZSFXLod, DestroyTimeSlice, CreateDestroySkip, SectorMirror) already ON.
    //
    // The cost is POPULATION, not work. MonoUpdaters does `container.AddRange(source)` before iterating, so
    // every registered instance costs a copy slot plus an interface dispatch 50x/second before its body runs.
    //
    // WHY MOST OF THEM CAN LEAVE THE LIST PERMANENTLY, read straight off EffectArea:
    //   - CustomFixedUpdate returns unless m_collisions > 0 AND m_collidedWithCharacter.Count > 0.
    //   - OnTriggerEnter only ever ADDS to m_collidedWithCharacter when (m_isHeatType || m_statusEffectHash != 0).
    // So an area that is neither a Heat type nor carries a status effect has a permanently empty list, and its
    // CustomFixedUpdate is dead code for the object's whole lifetime. That is the PlayerBase / NoMonsters /
    // PrivateProperty population: every workbench and base marker in a settlement.
    //
    // NO REFLECTION NEEDED. m_isHeatType is just m_type.HasFlag(Type.Heat), and m_statusEffectHash is set in
    // Awake only when m_statusEffect is non-empty - so `hash != 0` and `!IsNullOrEmpty(m_statusEffect)` are the
    // same test. Both m_type and m_statusEffect are public fields.
    //
    // NOTHING ELSE READS THE UPDATER LIST. The static area queries are populated in Awake (s_allAreas,
    // s_noMonsterAreas, s_noMonsterCloseToAreas, s_BurningAreas) and IsPointInsideArea / GetBaseValue /
    // IsPointPlus025InsideBurningArea run Physics.OverlapSphereNonAlloc against the collider or scan those
    // static lists. Updater membership is an input to none of them, so removing it changes no game behaviour.
    //
    // WHY enabled STAYS TRUE, unlike the piece ZSync skip. OnTriggerEnter and OnTriggerExit live on this SAME
    // component, and Unity does not deliver trigger callbacks to a disabled MonoBehaviour. Disabling an
    // EffectArea would stop m_collisions from ever being counted again and quietly break the NoMonsters and
    // Burning bookkeeping that spawners and fire damage read. So membership is removed from Instances directly
    // and the component is left enabled - Instances is the only part MonoUpdaters looks at.
    //
    // HEAT AND STATUS AREAS ARE LEFT REGISTERED. Gating those on an empty list needs a wake-up, and the only
    // wake-up signal is OnTriggerEnter on a component that must stay enabled anyway. The saving is real but the
    // correctness argument is not free, so measure what this tier removes before reaching for it.
    internal static class EffectAreaIdleSkip
    {
        private const float ReportSeconds = 30f;

        private static int s_deregistered;
        private static int s_kept;
        private static int s_reportedAt = -1;
        private static float s_nextReport;

        internal static void Reset()
        {
            s_deregistered = 0;
            s_kept = 0;
            s_reportedAt = -1;
            s_nextReport = 0f;
        }

        // True when CustomFixedUpdate can never do work for this area, for the reasons in the header.
        private static bool CanNeverTick(EffectArea area)
        {
            if ((area.m_type & EffectArea.Type.Heat) != 0) return false;
            return string.IsNullOrEmpty(area.m_statusEffect);
        }

        internal static void OnEnabled(EffectArea area)
        {
            if (area == null) return;
            if (!CanNeverTick(area)) { s_kept++; Report(); return; }

            // OnEnable has already added it; drop it back out. Remove is a no-op if it is somehow absent.
            EffectArea.Instances.Remove(area);
            s_deregistered++;
            Report();
        }

        // A rolling summary rather than one line per area: it prints only when the count has actually moved
        // since the last print, so it converges to silence once the area set is stable instead of flooding.
        private static void Report()
        {
            float now = Time.realtimeSinceStartup;
            if (now < s_nextReport) return;
            if (s_deregistered == s_reportedAt) return;
            s_nextReport = now + ReportSeconds;
            s_reportedAt = s_deregistered;
            EasyBakeLog.Info(
                $"EffectArea idle skip: {s_deregistered} inert area(s) out of the MonoUpdaters list, {s_kept} left "
                + "registered (Heat or status-effect areas, which can tick).");
        }
    }

    [HarmonyPatch(typeof(EffectArea), "OnEnable")]
    internal static class EffectArea_OnEnable_IdleSkip
    {
        private static void Postfix(EffectArea __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.EffectAreaIdleSkipEnabled.Value) return;
            EffectAreaIdleSkip.OnEnabled(__instance);
        }
    }
}
