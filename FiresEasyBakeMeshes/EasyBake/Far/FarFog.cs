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
            s_scale = EbmConfigMove.Bind(config, "03 - Far View", "Far fog scale", 1f, new ConfigDescription(
                "Multiplies the fog density of the main view (0.3 = fog thins to about a third, so far buildings, rocks and trees read out to ~3x the distance). 1 = the game's fog unchanged. Colour and fog mode stay the game's / the fog owner's. With Vedr installed, use Vedr's [30 - Air fog] 'Game fog density scale' instead (Vedr's sky and air fog follow that one, not this) and leave this at 1; a Warning is logged when both are set.",
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
            s_vedrSaid = null; if (s_vedrResolved) CheckVedr();   // 1.2.467: re-say the Vedr overlap on an EBM change
        }

        private static void PreCull(Camera cam)
        {
            if (cam == null || cam != Camera.main || s_applied) return;
            if (!s_vedrResolved) ResolveVedr();   // 1.2.467: once, in a world (every plugin's Awake has run by the first render)
            s_cam = cam; s_saved = RenderSettings.fogDensity; s_applied = true;
            if (!s_saidDistance) { s_saidDistance = true; EasyBakeLog.Info($"[Far] Far fog scale {s_scale.Value:0.##}: game fog density {s_saved:0.0000} (~{(s_saved > 0f ? 2.3f / s_saved : 0f):0} m to 90% fog), scaled ~{(s_saved > 0f ? 2.3f / (s_saved * s_scale.Value) : 0f):0} m while the main view renders"); }
            RenderSettings.fogDensity = s_saved * s_scale.Value;
        }

        private static void PostRender(Camera cam)
        {
            if (!s_applied || cam != s_cam) return;
            RenderSettings.fogDensity = s_saved; s_applied = false;
        }

        // 1.2.467 ([visual], lead 11-19; Fire: "yes vedr should own it"): Vedr 0.8.292 owns the game's fog thickness ("30 - Air fog" / "Game fog density scale",
        // VedrVolFog.cs:80, an EnvMan.SetEnv postfix; active when != 1, VedrVolFog.cs:625). Vedr's sky fog and air fog follow it; this key does not reach them
        // ([seasons] 11-19: Vedr reads RenderSettings in LateUpdate, before this onPreCull window). Both set = the main view's fog is scaled twice, so warn.
        // Soft read only (no reference to Vedr): Chainloader -> Vedr's ConfigFile -> TryGetEntry. Nothing is changed; the Warning is the whole effect.
        // Runs only while this key is below 1 (PreCull is hooked only then), so scale 1 costs nothing.
        private static bool s_vedrResolved;
        private static ConfigEntry<float> s_vedrScale;
        private static ConfigEntryBase s_vedrMode;
        private static string s_vedrSaid;

        private static void ResolveVedr()
        {
            s_vedrResolved = true;
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("Norger.Vedr", out var pi) || pi == null || pi.Instance == null) return;
                var vc = pi.Instance.Config;
                if (!vc.TryGetEntry(new ConfigDefinition("30 - Air fog", "Game fog density scale"), out s_vedrScale) || s_vedrScale == null) return;
                s_vedrScale.SettingChanged += (_, __) => { s_vedrSaid = null; CheckVedr(); };
                // "Game fog mode" is Vedr's own enum (GameFogMode { Game, ExponentialSquared }, VedrVolFog.cs:36 / :81): read untyped, by its name
                var modeDef = new ConfigDefinition("30 - Air fog", "Game fog mode");
                if (vc.ContainsKey(modeDef))
                {
                    s_vedrMode = vc[modeDef];   // ConfigEntryBase has no SettingChanged (BepInExREF ConfigEntry`1.cs:24 only): the file's event, this entry only
                    vc.SettingChanged += (_, a) => { if (a != null && a.ChangedSetting == s_vedrMode) { s_vedrSaid = null; CheckVedr(); } };
                }
                CheckVedr();
            }
            catch (System.Exception e) { s_vedrScale = null; EasyBakeLog.Debug($"[Far] Far fog scale: Vedr's game fog key not read ({e.GetType().Name})"); }
        }

        private static void CheckVedr()
        {
            if (s_scale == null || s_vedrScale == null) return;
            string mode = s_vedrMode?.BoxedValue?.ToString() ?? "Game";
            bool vedrOn = Mathf.Abs(s_vedrScale.Value - 1f) > 0.0001f || mode != "Game";   // [lead] 11-19: active = scale != 1 or mode != Game
            bool both = s_scale.Value < 0.999f && vedrOn;
            string say = both ? $"[Far] Far fog scale {s_scale.Value:0.##} is set while Vedr owns the game's fog ([30 - Air fog] 'Game fog density scale' {s_vedrScale.Value:0.###}, 'Game fog mode' {mode}): the main view's fog is thinned on top of Vedr's (x{s_scale.Value * s_vedrScale.Value:0.###} in all), and Vedr's sky and air fog do not follow this key. Set EBM [03 - Far View] 'Far fog scale' back to 1." : "";
            if (say == s_vedrSaid) return;
            s_vedrSaid = say;
            if (both) EasyBakeLog.Warn(say);
        }
    }
}
