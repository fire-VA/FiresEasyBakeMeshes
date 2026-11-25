using System;
using BepInEx.Configuration;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.458 ([visual], Fire 11-18: "none of the far lands esc code in ebm will default to on. It will all get its own config section. With a master
    /// switch that disables all our custom far rendering as well as a toggle for each far render type individually."): section [03 - Far Rendering].
    /// "Far Rendering" (default OFF) is the master: while it is off every custom far renderer is off whatever its own toggle says. Each renderer ANDs
    /// <see cref="On"/> into its own on check (FarTerrainTiles.Wanted, ImpostorEngine.Enabled, FarVegRunner.On, FarRocks.Enabled, FarBuildings.Enabled,
    /// FarLights.Enabled) and subscribes <see cref="Changed"/> with the same stop / start code its own toggle's SettingChanged runs, so the master is live.
    /// The per-renderer toggles are bound by each renderer in this same section (Section). Bound first of the far keys (Plugin Awake).
    /// </summary>
    internal static class FarRendering
    {
        internal const string Section = "03 - Far Rendering";
        private static ConfigEntry<bool> s_master;

        /// <summary>The master switch: false = every custom far renderer is off.</summary>
        internal static bool On => s_master != null && s_master.Value;

        /// <summary>Raised (main thread) when the master switch changes; each renderer re-runs its own toggle's stop / start code.</summary>
        internal static event Action Changed;

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_master != null) return;
            s_master = EbmConfigMove.Bind(config, Section, "Far Rendering", false,
                "Master switch for all of EBM's own far drawing (far terrain, far trees, far rocks, far buildings, far lights). Off = none of them draw, " +
                "whatever their own switches below say. On = each one draws when its own switch is on. Live.");
            s_master.SettingChanged += (_, __) =>
            {
                EasyBakeLog.Info($"[Far] Far Rendering {(On ? "on: each far renderer follows its own switch" : "off: every far renderer is off")}");
                var h = Changed;
                if (h == null) return;
                foreach (Action a in h.GetInvocationList())
                {
                    try { a(); }
                    catch (Exception e) { EasyBakeLog.Warn($"[Far] a far renderer threw on the Far Rendering change: {e.GetType().Name}: {e.Message}"); }
                }
            };
            EasyBakeLog.Info($"[Far] Far Rendering {(On ? "on" : "off")} at start ([{Section}] Far Rendering)");
        }
    }
}
