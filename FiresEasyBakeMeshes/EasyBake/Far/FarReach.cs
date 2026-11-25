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
            s_shared = EbmConfigMove.Bind(config, FarRendering.Section, "Far reach (m)", 0f, new ConfigDescription(   // 1.2.458: was [03 - Far View] (EbmConfigMove carries a saved value)
                "One far-drawing distance for every far system (impostor trees and rocks, far buildings, far rocks, far lights). 0 = each system uses its own distance key.",
                new AcceptableValueRange<float>(0f, 6000f)));
            s_shared.SettingChanged += (_, __) => Say("changed");
            Say("bound");
        }

        /// <summary>The shared reach when set (> 0), else the system's own value.</summary>
        internal static float Or(float own) => s_shared != null && s_shared.Value > 0f ? s_shared.Value : own;

        internal static bool Shared => s_shared != null && s_shared.Value > 0f;

        /// <summary>1.2.483 (FarZoneSet, Sync ON only): the ONE reach every far layer draws to, one clamp for all (600-6000 m): Far reach when set; else
        /// ([2ic] review: never override a reach the player chose) the LARGEST own 1.2.482 reach of the enabled far layers (terrain, trees, rocks, buildings);
        /// else 3000 m. Sync OFF never reads it: each layer keeps its 1.2.482 reach (OwnReach / OwnDistance).</summary>
        internal static float Effective => UnityEngine.Mathf.Clamp(Capped(Pick(out _)), 600f, 6000f);
        // 1.2.485: a client of a dedicated server never draws its far set past the far bases the server can send (FarBuildingNet.CapsReach)
        private static float Capped(float r) => FarBuildingNet.CapsReach(out float cap) ? UnityEngine.Mathf.Min(r, cap) : r;

        /// <summary>1.2.483: where Effective came from, for the [FarZones] line ("Far reach", "largest layer reach: buildings", "default").</summary>
        internal static string EffectiveSource { get { float r = Pick(out string s); return FarBuildingNet.CapsReach(out float cap) && cap < r ? s + $", capped at the server's far-bases reach {cap:0} m" : s; } }

        private static float Pick(out string source)
        {
            if (Shared) { source = "Far reach"; return s_shared.Value; }
            float m = 0f; source = "default 3000 m (no far layer on)";
            if (FarTerrainTiles.Wanted && FarTerrainTiles.OwnReach > m) { m = FarTerrainTiles.OwnReach; source = "largest layer reach: far terrain"; }
            if (ImpostorEngine.Enabled && ImpostorEngine.OwnDistance > m) { m = ImpostorEngine.OwnDistance; source = "largest layer reach: far trees"; }
            if (FarRocks.Enabled && FarRocks.OwnReach > m) { m = FarRocks.OwnReach; source = "largest layer reach: far rocks"; }
            if (FarBuildings.Enabled && FarBuildings.OwnReach > m) { m = FarBuildings.OwnReach; source = "largest layer reach: far buildings"; }
            return m > 0f ? m : 3000f;
        }

        /// <summary>1.2.483: the reach a layer draws to: Effective under Far Zone Sync, else the layer's own 1.2.482 value (passed in unchanged).</summary>
        internal static float For(float ownClamped) => FarZoneSet.On ? Effective : ownClamped;

        private static void Say(string why)
        {
            EasyBakeLog.Info(Shared
                ? $"[Far] Far reach {s_shared.Value:0} m ({why}): far impostors, far buildings, far rocks and far lights all draw to it (each clamped to its own range)"
                : $"[Far] Far reach 0 ({why}): each far system uses its own distance key");
        }
    }
}
