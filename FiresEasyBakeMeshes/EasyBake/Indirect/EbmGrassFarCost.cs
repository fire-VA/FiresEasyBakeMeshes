using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E15 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E15; DROP_NOTES_seasons_grass2.md). Two cost levers on grass, both OFF by default,
    // each with its visible effect stated in its config text. EBM's own code; relies on no other mod.
    //
    //  [Optimize] GrassPatchSize (metres, 0 = the game's 8): bigger grass patches = fewer patches, fewer renderers (vanilla) or rows (EBM grass),
    //    fewer patch builds while walking. Vanilla places m_amount blades PER PATCH (ClutterSystem GenerateVegPatch), so m_amountScale is raised by
    //    (size / 8)^2 to keep the density. VISIBLE: the blades sit in different places (vanilla seeds per patch cell); a kind dense enough to pass
    //    1023 blades in one patch is cut at 1023 (thinner), counted in the Debug line; each step through deep snow or a terrain edit rebuilds a
    //    bigger patch; the distance fade works per patch, so it moves in coarser steps. Changing it rebuilds all grass once.
    //  [Optimize] GrassFarSwayStop (metres, 0 = off): EBM's grass shader stops the wind on blades further than this, easing over the last 4 m.
    //    VISIBLE: far grass stands still. Only with IndirectGrass on (EBM's shader); vanilla-drawn grass is not touched. GPU vertex work only:
    //    with the main thread as the limit (moving rig, 11-08 audits) it is not expected to move the frame rate.
    [HarmonyPatch]
    internal static class EbmGrassFarCost
    {
        private static readonly int IdSwayFar = Shader.PropertyToID("_EbmGrassSwayFar");
        private static float s_baseSize = -1f, s_baseAmount = -1f, s_setAmount = -1f, s_ratio = 1f;
        private static bool s_hooked;
        private static float s_nextLog;

        private static float SizeKey => FiresEasyBakeMeshesPlugin.GrassPatchSize != null ? FiresEasyBakeMeshesPlugin.GrassPatchSize.Value : 0f;
        private static float SwayKey => FiresEasyBakeMeshesPlugin.GrassFarSwayStop != null ? FiresEasyBakeMeshesPlugin.GrassFarSwayStop.Value : 0f;

        [HarmonyPatch(typeof(ClutterSystem), "Awake"), HarmonyPostfix]
        private static void AwakePostfix(ClutterSystem __instance)
        {
            if (__instance == null || __instance.m_menuHack) return;
            s_baseSize = __instance.m_grassPatchSize; s_baseAmount = __instance.m_amountScale; s_setAmount = -1f; s_ratio = 1f;
            if (!s_hooked)
            {
                s_hooked = true;
                if (FiresEasyBakeMeshesPlugin.GrassPatchSize != null) FiresEasyBakeMeshesPlugin.GrassPatchSize.SettingChanged += (_, __) => Apply(ClutterSystem.instance);
                if (FiresEasyBakeMeshesPlugin.GrassFarSwayStop != null) FiresEasyBakeMeshesPlugin.GrassFarSwayStop.SettingChanged += (_, __) => ApplySway();
            }
            Apply(__instance);
            ApplySway();
        }

        private static float s_swayLogged = -1f;
        private static void ApplySway()
        {
            float far = Mathf.Max(0f, SwayKey);
            Shader.SetGlobalFloat(IdSwayFar, far);
            if (far == s_swayLogged) return;   // review 1.2.257 ([generator]): Info only when the value changes, not at every ClutterSystem.Awake
            s_swayLogged = far;
            EasyBakeLog.Info(far > 0f ? $"[Grass] far sway stop: EBM-drawn grass further than {far:0} m stands still (eases over 4 m)" : "[Grass] far sway stop off: grass sways at every distance");
        }

        private static void Apply(ClutterSystem cs)
        {
            if (cs == null || cs.m_menuHack || s_baseSize <= 0f) return;
            // another mod changed the amount since we set it: that is the new base
            if (s_setAmount >= 0f && !Mathf.Approximately(cs.m_amountScale, s_setAmount)) s_baseAmount = cs.m_amountScale / s_ratio;
            float key = SizeKey;
            float want = key > 0f ? Mathf.Clamp(key, 4f, 32f) : s_baseSize;
            if (Mathf.Approximately(cs.m_grassPatchSize, want) && s_setAmount >= 0f == key > 0f) return;
            s_ratio = (want / s_baseSize) * (want / s_baseSize);
            cs.m_grassPatchSize = want;
            cs.m_amountScale = s_baseAmount * s_ratio;
            s_setAmount = key > 0f ? cs.m_amountScale : -1f;
            cs.ClearAll();   // vanilla's own full rebuild (the settings-change path)
            EasyBakeLog.Info(key > 0f
                ? $"[Grass] patch size {want:0.#} m (game {s_baseSize:0.#} m): blade count per patch x{s_ratio:0.##} to keep the density; grass rebuilt once; blades sit in new places"
                : $"[Grass] patch size back to the game's {s_baseSize:0.#} m; grass rebuilt once");
        }

        // Debug every 30 s while the size key is on: renderers that hit vanilla's 1023-blade cap (density lost there)
        [HarmonyPatch(typeof(ClutterSystem), "LateUpdate"), HarmonyPostfix]
        private static void LateUpdatePostfix(ClutterSystem __instance)
        {
            if (SizeKey <= 0f || __instance == null || Time.unscaledTime < s_nextLog || !EasyBakeLog.DebugOn()) return;
            s_nextLog = Time.unscaledTime + 30f;
            int all = 0, capped = 0;
            foreach (var r in __instance.GetComponentsInChildren<InstanceRenderer>(false))
            {
                all++;
                if (s_count(r) >= 1023) capped++;
            }
            EasyBakeLog.Debug($"[Grass] patch size {__instance.m_grassPatchSize:0.#} m: {all} grass renderer(s), {capped} at the 1023-blade cap (thinner there)");
        }
        private static readonly AccessTools.FieldRef<InstanceRenderer, int> s_count = AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");
    }
}
