using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.166 (Fire 11-05: "even small gains add up", group A of Tools\GAP_optimizers_20261104.md): Update methods that keep rewriting a value that has reached its end, and
    // looping particles that simulate off screen on placed pieces. Nothing here changes what ends up drawn on screen. Our own design from the 1.0 decompile.
    internal static class IdleGates
    {
        internal static ConfigEntry<bool> Enabled;
        internal static bool On => Enabled == null || Enabled.Value;

        internal static void Bind(ConfigFile config)
        {
            Enabled = EbmConfigMove.Bind(config, "23 - Performance - World Objects", "IdleGates", true,
                "Skip per-frame writes of finished fades (material faders, portals, effect fades) and pause looping particles of placed pieces while off screen.");
            InvulnerableClassifier.AllImmuneWear = EbmConfigMove.Bind(config, "23 - Performance - World Objects", "VanillaWearOnInvulnerablePieces", false,
                "Run vanilla wear and support on invulnerable pieces: all-Immune pieces can then collapse, and pieces leaning on a floating negative-health piece can too. Off keeps what stands today standing. Applies to pieces loaded after the change.");
            EasyBakeLog.Debug($"[IdleGates] {(On ? "on" : "off")}");
            Enabled.SettingChanged += (_, __) => ApplyToTracked();   // 1.2.380: the particle half follows a live flip (the three Update gates read On every call)
        }

        // A portal or fade is written once at its end value, then left alone. The first Update always runs, since Awake does not write the start value.
        internal static readonly HashSet<int> Written = new HashSet<int>();

        internal static bool FirstLook(Object owner) => Written.Add(owner.GetInstanceID());

        private static readonly List<ParticleSystem> s_systems = new List<ParticleSystem>(8);
        private static readonly HashSet<string> s_noParticles = new HashSet<string>();
        private static int s_paused;
        private static bool s_said;
        // 1.2.380 ([lead], Fire: off/on tests toggle keys live in one run; Tools\HOTSWAP_EBM_KEYS_20261114.md section 4): the culling mode written below stayed on the
        // particle system after the key went off (nothing restored it), and a piece created while the key was off was never looked at again, so an off / on pair in one
        // run compared the same state twice. The systems this class may pause are remembered whatever the key says; a flip sets every live one to the key's side
        // (Automatic = what the game had: only a system found on Automatic is ever taken).
        private static readonly List<ParticleSystem> s_tracked = new List<ParticleSystem>();
        private static int s_trackedPruneAt = 256;

        private static void ApplyToTracked()
        {
            bool on = On;
            int live = 0, write = 0;
            for (int i = 0; i < s_tracked.Count; i++)
            {
                var system = s_tracked[i];
                if (system == null) continue;
                s_tracked[write++] = system; live++;
                var main = system.main;
                main.cullingMode = on ? ParticleSystemCullingMode.PauseAndCatchup : ParticleSystemCullingMode.Automatic;
            }
            s_tracked.RemoveRange(write, s_tracked.Count - write);
            s_trackedPruneAt = Mathf.Max(256, s_tracked.Count * 2);
            EasyBakeLog.Info($"[IdleGates] turned {(on ? "on" : "off")}: {live} looping particle system(s) on placed pieces set to {(on ? "pause off screen" : "the game's own culling")}");
        }

        // Instances, not prefabs: FAP's lazy bundles realize prefabs late, and other mods own theirs. A prefab with no particle system costs one set lookup after its first copy.
        internal static void PauseOffScreen(GameObject go)
        {
            if (go.GetComponent<Piece>() == null || s_noParticles.Contains(go.name)) return;   // 1.2.380: looked at whatever the key says, so a later flip to on reaches this piece
            go.GetComponentsInChildren(true, s_systems);
            if (s_systems.Count == 0) { s_noParticles.Add(go.name); return; }
            bool on = On;
            foreach (var system in s_systems)
            {
                var main = system.main;
                if (!main.loop || main.cullingMode != ParticleSystemCullingMode.Automatic) continue;
                s_tracked.Add(system);   // 1.2.380
                if (!on) continue;
                main.cullingMode = ParticleSystemCullingMode.PauseAndCatchup;   // 1.2.378 ([lead]): the 1.2.366 test key [Batching] IdleGatesCullingMode is gone (T6 captures 11-14: PauseAndCatchup / Pause / Off made no measurable difference); this mode stays
                s_paused++;
            }
            s_systems.Clear();
            if (s_tracked.Count >= s_trackedPruneAt) { s_tracked.RemoveAll(s => s == null); s_trackedPruneAt = Mathf.Max(256, s_tracked.Count * 2); }   // 1.2.380: destroyed pieces leave the list
            if (!s_said && s_paused > 0) { s_said = true; EasyBakeLog.Debug($"[IdleGates] looping particles on placed pieces pause off screen; first: {go.name}"); }
        }
    }

    [HarmonyPatch(typeof(MaterialFader), "Update")]
    internal static class IdleGates_MaterialFader
    {
        private static readonly AccessTools.FieldRef<MaterialFader, bool> s_started = AccessTools.FieldRefAccess<MaterialFader, bool>("m_started");
        private static readonly AccessTools.FieldRef<MaterialFader.FadeProperty, bool> s_finished = AccessTools.FieldRefAccess<MaterialFader.FadeProperty, bool>("m_finished");

        private static bool Prefix(MaterialFader __instance)
        {
            if (!IdleGates.On || !s_started(__instance)) return true;
            var properties = __instance.m_fadeProperties;
            if (properties == null || properties.Count == 0) return true;
            for (int i = 0; i < properties.Count; i++)
                if (!s_finished(properties[i])) return true;
            return false;   // the Update that finished the last one already wrote its block
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "Update")]
    internal static class IdleGates_Portal
    {
        private static readonly AccessTools.FieldRef<TeleportWorld, float> s_alpha = AccessTools.FieldRefAccess<TeleportWorld, float>("m_colorAlpha");
        private static readonly AccessTools.FieldRef<TeleportWorld, bool> s_hadTarget = AccessTools.FieldRefAccess<TeleportWorld, bool>("m_hadTarget");

        private static bool Prefix(TeleportWorld __instance)
        {
            if (!IdleGates.On || IdleGates.FirstLook(__instance)) return true;
            return s_alpha(__instance) != (s_hadTarget(__instance) ? 1f : 0f);
        }
    }

    [HarmonyPatch(typeof(EffectFade), "Update")]
    internal static class IdleGates_EffectFade
    {
        private static readonly AccessTools.FieldRef<EffectFade, float> s_intensity = AccessTools.FieldRefAccess<EffectFade, float>("m_intensity");
        private static readonly AccessTools.FieldRef<EffectFade, bool> s_active = AccessTools.FieldRefAccess<EffectFade, bool>("m_active");

        private static bool Prefix(EffectFade __instance)
        {
            if (!IdleGates.On || IdleGates.FirstLook(__instance)) return true;
            return s_intensity(__instance) != (s_active(__instance) ? 1f : 0f);
        }
    }
}
