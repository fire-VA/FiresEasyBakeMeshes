using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.344 ([ghost], lead 11-12; Fire's run_6: he set [Far Impostors] Impostor distance 3000 while far buildings, far rocks and far lights
    /// stayed at their own 2500). ONE reach every far system reads: [Far] "Far reach (m)". 0 (default) = each system keeps its own key, exactly
    /// as before; above 0 it wins over Far Impostors' Impostor distance, Far buildings' Reach, Far rocks' Reach and Far Lights' Max distance,
    /// each still clamped to that system's own range. One Info line at bind and on change says which reach each system draws to.
    /// </summary>
    internal static class FarReach
    {
        private static ConfigEntry<float> s_shared;

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_shared != null) return;
            s_shared = config.Bind("Far", "Far reach (m)", 0f, new ConfigDescription(
                "One far-drawing distance for every far system (impostor trees and rocks, far buildings, far rocks, far lights). 0 = each system uses its own distance key.",
                new AcceptableValueRange<float>(0f, 6000f)));
            s_shared.SettingChanged += (_, __) => Say("changed");
            Say("bound");
        }

        /// <summary>The shared reach when set (> 0), else the system's own value.</summary>
        internal static float Or(float own) => s_shared != null && s_shared.Value > 0f ? s_shared.Value : own;

        internal static bool Shared => s_shared != null && s_shared.Value > 0f;

        private static void Say(string why)
        {
            EasyBakeLog.Info(Shared
                ? $"[Far] Far reach {s_shared.Value:0} m ({why}): far impostors, far buildings, far rocks and far lights all draw to it (each clamped to its own range)"
                : $"[Far] Far reach 0 ({why}): each far system uses its own distance key");
        }
    }
}
