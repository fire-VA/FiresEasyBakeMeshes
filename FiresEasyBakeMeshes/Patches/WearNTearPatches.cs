using HarmonyLib;

namespace FiresEasyBakeMeshes.Patches
{
    [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
    public static class WearNTear_OnDestroy_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(WearNTear __instance)
        {
            // Always evict the classifier cache regardless of which features are
            // active — otherwise the cache slowly grows with Unity-null refs.
            EasyBake.InvulnerableClassifier.Forget(__instance);
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            EasyBake.ZoneTracker.OnPieceDestroyed(__instance);
        }
    }

    // WearNTear short-circuit for invulnerable pieces.
    //
    // WearNTearUpdater (vanilla MonoUpdater) is the single largest per-frame
    // cost on a megabase — ~40 ms/sec in profiling. It runs two passes over
    // WearNTear.GetAllInstances():
    //   - Once per second: UpdateCover (Physics.SphereCastNonAlloc x2 — roof
    //     and ash roof) on every piece, plus UpdateAshlandsMaterialValues.
    //   - Every frame: 50-100 pieces through UpdateWear (adaptive rate).
    //
    // For invulnerable pieces (m_health < 0, or all-Immune damage modifiers):
    //
    //   UpdateWear: every code path that could accumulate damage — rain wear,
    //   support collapse, ashlands burn, lava — is gated at the bottom by
    //   `CanBeRemoved()` returning false → `num1 = 0`, no damage applied. The
    //   entire upstream chain (rain check, roof check, support graph walk,
    //   biome lookup, lava height query, ashlands timing) is wasted compute.
    //   UpdateVisual at the end is also wasted: m_healthPercentage stays at
    //   1.0 forever, m_new GameObject stays active, no SetActive change to
    //   make. For our baked pieces the visual GameObjects are sitting under
    //   disabled renderers anyway.
    //
    //   UpdateCover: roof state is only consumed by UpdateWear's rain-damage
    //   timer. Invulnerable piece → no rain damage → no need to know the roof
    //   state. Skipping kills two SphereCasts per piece per 4s.
    //
    // Both prefixes return false to skip vanilla entirely. The InvulnerableClassifier
    // result is cached per-WearNTear instance, so the per-piece overhead of this
    // patch is one dictionary lookup.
    //
    // Limitations:
    //   - A piece that becomes vulnerable mid-session (admin removes invuln
    //     flag) will keep its cached classification — its WearNTear updates
    //     stay short-circuited until the piece is destroyed and respawned.
    //     Same caveat as the existing bake-cache identity match. Acceptable.
    //   - Other mods that depend on UpdateWear / UpdateCover side-effects on
    //     invulnerable pieces would break. Gated on a config so users can
    //     opt out if such a conflict appears.
    // A piece drawn as an instance only matches its healthy look. Worn or broken (the same 75% line SetHealthVisual uses),
    // burning in the Ashlands, or highlighted by a hammer, it goes back to drawing itself.
    [HarmonyPatch(typeof(WearNTear), "SetHealthVisual")]
    public static class WearNTear_SetHealthVisual_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(WearNTear __instance, float health)
        {
            if (health > 0.75f || !FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            EasyBake.ZoneTracker.HandBackPiece(__instance);
        }
    }

    [HarmonyPatch(typeof(WearNTear), "SetAshlandsMaterialValue")]
    public static class WearNTear_SetAshlandsMaterialValue_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(WearNTear __instance, float v)
        {
            if (v <= 0f || !FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            EasyBake.ZoneTracker.HandBackPiece(__instance);
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Highlight))]
    public static class WearNTear_Highlight_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(WearNTear __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            EasyBake.ZoneTracker.HandBackPiece(__instance);
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))]
    public static class WearNTear_UpdateWear_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(WearNTear __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value) return true;
            if (!FiresEasyBakeMeshesPlugin.WearShortCircuitEnabled.Value) return true;
            if (!EasyBake.InvulnerableClassifier.IsInvulnerable(__instance)) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateCover))]
    public static class WearNTear_UpdateCover_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(WearNTear __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value) return true;
            if (!FiresEasyBakeMeshesPlugin.WearShortCircuitEnabled.Value) return true;
            if (!EasyBake.InvulnerableClassifier.IsInvulnerable(__instance)) return true;
            return false;
        }
    }
}
