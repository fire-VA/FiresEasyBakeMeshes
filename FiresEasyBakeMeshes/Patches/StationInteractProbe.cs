using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // 1.2.115 ([visual], Fire 10-24 00:21: "when i just went to interact with the forge visually it disappeared"; the hover still said
    // "Forge [E] Use", so the real forge existed with its own renderers silenced by EBM and the zone bake not drawing it). His log had no
    // EBM line about the forge, so the cause could not be proven. This probe writes, the moment a crafting station is used, exactly what
    // `ebm_why` writes for the piece under the crosshair (own renderers silenced or drawing, instanced group alive / bounds, combined mesh,
    // far tier, whether a fresh instance definition builds), once per station per minute. Read-only: nothing is changed.
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.Interact))]
    public static class StationInteractProbe
    {
        private const float RepeatSeconds = 60f;
        private static readonly Dictionary<int, float> s_lastLogged = new Dictionary<int, float>();

        [HarmonyPostfix]
        public static void Postfix(CraftingStation __instance, bool repeat)
        {
            if (repeat || __instance == null || !FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            try
            {
                int id = __instance.GetInstanceID();
                if (s_lastLogged.TryGetValue(id, out float last) && Time.time - last < RepeatSeconds) return;
                s_lastLogged[id] = Time.time;
                bool silenced = __instance.GetComponentInParent<EasyBake.EasyBakeSuppressedVisuals>() != null;
                EasyBakeLog.Debug($"[StationProbe] used '{Utils.GetPrefabName(__instance.gameObject)}' at {__instance.transform.position:F1}: own renderers {(silenced ? "SILENCED by EBM" : "drawing")}; ebm_why for it:");
                foreach (string line in EasyBake.ZoneTracker.WhyFor(__instance.GetComponentInParent<ZNetView>())) EasyBakeLog.Debug("[StationProbe] " + line);
            }
            catch (System.Exception ex) { EasyBakeLog.Debug($"[StationProbe] failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
}
