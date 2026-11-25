using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.346 ([ghost], lead 11-12; Fire's run_8 WHERE line: "of 309 drawn 141 in the main view, 141 rendered ...; fog on (ExponentialSquared,
    /// density 0.0021, ~1096 m to 90% fog)": the far ring is drawn and fogged out). [Far] "Far fog scale": the fog density the MAIN camera
    /// renders with is multiplied by this, for that camera's render only. Set just before the main camera culls (Camera.onPreCull) and put
    /// back right after it renders (Camera.onPostRender), so every other reader of RenderSettings.fogDensity (Vedr, FTS, other mods,
    /// transparents of other cameras) keeps the value its owner set, and whichever mod owns fog this frame still owns its colour and mode.
    /// Why a whole-view scale and not per far object: Valheim fogs opaque deferred geometry in a full-screen pass, not in each object's shader
    /// (the FarLands study, Tools\RESEARCH_FarLands_20261108.md §2.6, replaces that pass for the same reason), so a per-renderer override
    /// would not reach it. UNCONFIRMED until the RenderDoc capture at Fire's far pose shows where the fog is applied.
    /// 1 (default) = no change at all (nothing is hooked). Fire's open look decision; the gate decides any other default.
    /// </summary>
    internal static class FarFog
    {
        private static ConfigEntry<float> s_scale;
        private static bool s_hooked, s_applied;
        private static float s_saved;
        private static Camera s_cam;
        private static bool s_saidDistance;   // 1.2.357: the "~N m to 90% fog" figure, once per Apply, from PreCull

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_scale != null) return;
            s_scale = config.Bind("Far", "Far fog scale", 1f, new ConfigDescription(
                "Multiplies the fog density of the main view (0.3 = fog thins to about a third, so far buildings, rocks and trees read out to ~3x the distance). 1 = the game's fog unchanged. Colour and fog mode stay the game's / the fog owner's.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            s_scale.SettingChanged += (_, __) => Apply();
            Apply();
        }

        private static void Apply()
        {
            bool want = s_scale != null && s_scale.Value < 0.999f;
            if (want && !s_hooked) { Camera.onPreCull += PreCull; Camera.onPostRender += PostRender; s_hooked = true; }
            else if (!want && s_hooked) { Camera.onPreCull -= PreCull; Camera.onPostRender -= PostRender; s_hooked = false; }
            // 1.2.357 ([visual], Fire's install 11-13 06:43, rig leg look-farfog-fog035, Crash_2026-11-13_114326223: "Far fog scale = 0.35" in the cfg at launch =
            // native crash in the chainloader: "(wrapper managed-to-native) UnityEngine.RenderSettings:get_fogDensity () <- FarFog:Apply () <- FarFog:Initialize
            // <- FiresEasyBakeMeshesPlugin:Awake ()". This line used to read RenderSettings.fogDensity for its "~N m to 90% fog" figure, and at Awake there is
            // no scene yet, so the native side has no render settings to answer from (scale 1 never read it, which is why every default launch was fine).
            // Nothing of Unity's scene state is read here any more; the distance figure moves to the first PreCull, where the camera exists.
            EasyBakeLog.Info(want ? $"[Far] Far fog scale {s_scale.Value:0.##}: the main view's fog density is multiplied by it while it renders (the 90% distance is logged once the main camera first renders)"
                                  : "[Far] Far fog scale 1: the game's fog is unchanged");
            s_saidDistance = false;
        }

        private static void PreCull(Camera cam)
        {
            if (cam == null || cam != Camera.main || s_applied) return;
            s_cam = cam; s_saved = RenderSettings.fogDensity; s_applied = true;
            if (!s_saidDistance) { s_saidDistance = true; EasyBakeLog.Info($"[Far] Far fog scale {s_scale.Value:0.##}: game fog density {s_saved:0.0000} (~{(s_saved > 0f ? 2.3f / s_saved : 0f):0} m to 90% fog), scaled ~{(s_saved > 0f ? 2.3f / (s_saved * s_scale.Value) : 0f):0} m while the main view renders"); }
            RenderSettings.fogDensity = s_saved * s_scale.Value;
        }

        private static void PostRender(Camera cam)
        {
            if (!s_applied || cam != s_cam) return;
            RenderSettings.fogDensity = s_saved; s_applied = false;
        }
    }
}
