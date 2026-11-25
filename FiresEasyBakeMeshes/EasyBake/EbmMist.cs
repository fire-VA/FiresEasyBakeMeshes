using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // [visual] 11-14, EBM 1.2.388 ([lead]; Fire, hands-on in the Mistlands: "It's the mist itself that causes the problems for performance; our frames drop
    // 50-60 fps whenever it's on screen, and if we pass through a clear spot we jump back up to 300+"). EBM's OWN mist perf path, ported from our FTS code
    // ([saddles]' FiresLitParticlesShaderLoader Lux pass + FiresMistTuner, FTS 0.3.397; Tools\HANDOFF_mistlux_to_EBM_20261114.md). No reference to FTS.
    //  - The costly draw ([ghost]'s RenderDoc, T4 eid 10215): the vanilla shader "Lux Lit Particles/ Tess Bumped" (vertex + hull + domain + pixel stage, 8 lights
    //    per tessellated vertex), 2.5-2.7 ms, ~27x overdraw. In the rip that shader is on two materials only: heavymist_mistlands and
    //    heavymist_mistlands_small_lux (the ParticleMist object in the Mistlands / Deep North location lists, mistvolume, vfx_mistlands_mist).
    //  - [Optimize] MistShader: those materials draw with EBM/MistLux (ebm_indirect; no tessellation, the sun per pixel with the same normal tap, other
    //    lights + ambient + fog per vertex, the vanilla fog formula fed by _EbmMistFog). Only mat.shader changes, the render queue is kept, so the draw
    //    order and every material value stay. Key off = the Lux shader and its queue go back. Live.
    //  - [Optimize] MistParticleCount / MistOpacity (1 = vanilla): scale the vanilla ParticleMist singleton's emission values and the start colour's alpha.
    //    Originals saved once per instance, restored exactly at 1. Gameplay untouched: IsInMist / IsMistBlocked read only the Mister / Demister lists.
    //  - Priority (Fire: EBM > Vedr > FTS > game): EbmApi.HandlesShader("Lux Lit Particles/ Tess Bumped") is true while Active; FTS stands down (swap and tuner).
    // Event-driven: materials are looked for when the key turns on and when a new ParticleMist instance appears (one reference compare a frame), not on a timer.
    internal static class EbmMist
    {
        internal const string Vanilla = "Lux Lit Particles/ Tess Bumped";
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<float> Count, Opacity;

        private struct Orig { public Shader shader; public int queue; }
        private static readonly Dictionary<Material, Orig> s_swapped = new Dictionary<Material, Orig>();
        private static readonly List<Material> s_dead = new List<Material>();
        private static string s_said;                // the last swap Info line (one line per state change)
        private static ParticleMist s_inst;          // the instance the last scan / tune ran for
        private static bool s_scanAsked;
        private static readonly int IdFog = Shader.PropertyToID("_EbmMistFog");
        private static Vector4 s_fog = new Vector4(-1f, -1f, -1f, 0f);

        internal static void Bind(ConfigFile config)
        {
            On = EbmConfigMove.Bind(config, "40 - Shaders", "MistShader", true /* 1.2.388 ([lead]: default ON; the same shader measured +6-8 fps in two FTS runs and Fire passed its look) */,
                "EBM draws the Mistlands mist with its own lighter shader (no tessellation; same colours, fog and fades). Off = EBM stands down; FiresTossinShade's mist shader, or the game's, takes over. Applies live.");
            Count = EbmConfigMove.Bind(config, "40 - Shaders", "MistParticleCount", 1f, new ConfigDescription(
                "How many mist puffs the Mistlands mist emits, as a share of the game's own amount. Lower = thinner mist and higher fps (the wall of mist around a Wisplight thins too). 1 = the game's amount. Range 0.1 to 1. Default 1. Applies live.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            Opacity = EbmConfigMove.Bind(config, "40 - Shaders", "MistOpacity", 1f, new ConfigDescription(
                "How opaque each mist puff is, as a share of the game's own. Lower = more see-through mist. 1 = the game's look. Range 0.1 to 1. Default 1. Applies live.",
                new AcceptableValueRange<float>(0.1f, 1f)));
            On.SettingChanged += (_, __) =>
            {
                if (Wanted) { EbmApi.RecheckShaderOwnership(On.Definition.Key + " key", true); s_scanAsked = true; }   // on: the lower mod gives the materials back first, EBM scans on the next Tick
                else { Release("[40 - Shaders] MistShader off"); EbmApi.RecheckShaderOwnership(On.Definition.Key + " key", true); }   // off ([seasons] review): the vanilla shader goes back first, then the lower mod is told and can take the materials
            };
            Count.SettingChanged += (_, __) => Tune();
            Opacity.SettingChanged += (_, __) => Tune();
        }

        private static bool HasGraphics => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        private static bool Wanted => On != null && On.Value && HasGraphics;
        /// <summary>True while the key is on and EBM/MistLux is loaded: EbmApi.HandlesShader(Vanilla) answers this (FTS stands down, swap and tuner).</summary>
        internal static bool Active => Wanted && Indirect.EbmIndirectAssets.Mist(out _) != null;

        // From the plugin's per-frame tick list (the lu[12] slot, beside EbmWater.Tick), every frame: one reference compare; the fog inputs while a material is on EBM/MistLux (three reads, one global set when they change).
        internal static void Tick()
        {
            if (On == null || !HasGraphics) return;
            var inst = ParticleMist.instance;
            if (!ReferenceEquals(inst, s_inst))
            {
                s_inst = inst;
                s_tuned = null; s_orig = null; s_ps = null;   // a fresh instance carries the game's values
                if (inst != null) { s_scanAsked = true; Tune(); }
            }
            if (s_scanAsked) { s_scanAsked = false; if (Wanted) Scan(); }
            if (s_swapped.Count == 0) return;
            var fog = new Vector4(RenderSettings.fog ? RenderSettings.fogDensity : 0f, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance, 0f);
            if (fog == s_fog) return;
            s_fog = fog;
            Shader.SetGlobalVector(IdFog, fog);   // the vanilla Lux fog inputs (raw density + linear start / end); EnvMan changes them per environment
        }

        private static bool IsMist(Material mat) => mat.name.IndexOf("mist", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Scan()
        {
            var ours = Indirect.EbmIndirectAssets.Mist(out string why);
            if (ours == null) { Say("[Mist] Lux mist: vanilla; [40 - Shaders] MistShader is on but EBM/MistLux is not available: " + why); return; }
            foreach (var kv in s_swapped) if (kv.Key == null) s_dead.Add(kv.Key);
            foreach (var m in s_dead) s_swapped.Remove(m);
            s_dead.Clear();
            var all = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < all.Length; i++)
            {
                var mat = all[i];
                if (mat == null || s_swapped.ContainsKey(mat)) continue;
                var sh = mat.shader;
                if (sh == null || sh.name != Vanilla || !IsMist(mat)) continue;
                try
                {
                    int q = mat.renderQueue;   // kept, so the draw order never changes
                    s_swapped[mat] = new Orig { shader = sh, queue = q };
                    mat.shader = ours;
                    mat.renderQueue = q;
                }
                catch (Exception ex) { s_swapped.Remove(mat); EasyBakeLog.Warn("[Mist] swap failed on '" + mat.name + "': " + ex.Message); }
            }
            s_fog = new Vector4(-1f, -1f, -1f, 0f);   // pushed again on this frame
            var names = new List<string>();
            foreach (var kv in s_swapped) if (kv.Key != null) names.Add(kv.Key.name);
            names.Sort(StringComparer.Ordinal);
            Say("[Mist] Lux mist: EBM shader, " + names.Count + " material(s) on " + Indirect.EbmIndirectAssets.MistShaderName + (names.Count > 0 ? ": " + string.Join(", ", names) : " (none loaded in this scene)"));
        }

        private static void Release(string why)
        {
            int n = 0;
            foreach (var kv in s_swapped)
            {
                if (kv.Key == null) continue;
                try { kv.Key.shader = kv.Value.shader; kv.Key.renderQueue = kv.Value.queue; n++; } catch { }
            }
            s_swapped.Clear();
            Say("[Mist] Lux mist: vanilla (" + why + "), " + n + " material(s) back on " + Vanilla);
        }

        private static void Say(string line)
        {
            if (line == s_said) return;
            s_said = line;
            EasyBakeLog.Info(line);
        }

        // ---- ParticleMist tuner (count + opacity) ----
        private class TuneOrig
        {
            public int localEmission, localPerUnit, maxParticles;
            public float emissionMax, emissionPerUnit, distantMax;
            public ParticleSystem.MinMaxGradient startColor;
        }
        private static ParticleMist s_tuned;
        private static ParticleSystem s_ps;
        private static TuneOrig s_orig;
        private static float s_appliedOpacity = 1f, s_appliedCount = 1f;

        private static void Tune()
        {
            try
            {
                if (Count == null || Opacity == null || !HasGraphics) return;
                var inst = ParticleMist.instance;
                if (inst == null) return;
                if (!ReferenceEquals(s_tuned, inst)) { s_tuned = inst; s_ps = null; s_orig = null; s_appliedOpacity = 1f; s_appliedCount = 1f; }
                float op = Mathf.Clamp(Opacity.Value, 0.1f, 1f), ct = Mathf.Clamp(Count.Value, 0.1f, 1f);
                if (Mathf.Approximately(op, s_appliedOpacity) && Mathf.Approximately(ct, s_appliedCount)) return;
                if (s_ps == null) s_ps = inst.GetComponent<ParticleSystem>();
                if (s_ps == null) return;
                var main = s_ps.main;
                if (s_orig == null)
                    s_orig = new TuneOrig
                    {
                        localEmission = inst.m_localEmission, localPerUnit = inst.m_localEmissionPerUnit,
                        emissionMax = inst.m_emissionMax, emissionPerUnit = inst.m_emissionPerUnit,
                        distantMax = inst.m_distantEmissionMax, maxParticles = main.maxParticles, startColor = main.startColor,
                    };
                var o = s_orig;
                inst.m_localEmission = Mathf.RoundToInt(o.localEmission * ct);
                inst.m_localEmissionPerUnit = Mathf.Max(1, Mathf.RoundToInt(o.localPerUnit * ct));   // a divisor in ParticleMist.Update: never 0
                inst.m_emissionMax = o.emissionMax * ct;
                inst.m_emissionPerUnit = o.emissionPerUnit * ct;
                inst.m_distantEmissionMax = o.distantMax * ct;
                main.maxParticles = Mathf.Max(1, Mathf.RoundToInt(o.maxParticles * ct));
                string colorNote;
                var g = o.startColor;
                if (op >= 0.999f) { main.startColor = g; colorNote = "start colour vanilla"; }
                else if (g.mode == ParticleSystemGradientMode.Color)
                {
                    var c = g.color; c.a *= op;
                    main.startColor = c; colorNote = "start alpha " + g.color.a.ToString("0.00") + "->" + c.a.ToString("0.00");
                }
                else if (g.mode == ParticleSystemGradientMode.TwoColors)
                {
                    Color a = g.colorMin, b = g.colorMax; a.a *= op; b.a *= op;
                    main.startColor = new ParticleSystem.MinMaxGradient(a, b); colorNote = "start alpha (two colours) x" + op.ToString("0.00");
                }
                else { main.startColor = g; colorNote = "start colour mode " + g.mode + ": opacity NOT applied"; }
                s_appliedOpacity = op; s_appliedCount = ct;
                bool vanilla = op >= 0.999f && ct >= 0.999f;
                EasyBakeLog.Info(vanilla
                    ? $"[Mist] tune: opacity 1.00, count 1.00 -> ParticleMist '{inst.name}': vanilla values restored (maxParticles {main.maxParticles})"
                    : $"[Mist] tune: opacity {op:0.00}, count {ct:0.00} -> ParticleMist '{inst.name}' maxParticles {o.maxParticles}->{main.maxParticles}, local {o.localEmission}->{inst.m_localEmission}, localPerUnit {o.localPerUnit}->{inst.m_localEmissionPerUnit}, demisterMax {o.emissionMax:0.###}->{inst.m_emissionMax:0.###}, demisterPerUnit {o.emissionPerUnit:0.#}->{inst.m_emissionPerUnit:0.#}, distantMax {o.distantMax:0.###}->{inst.m_distantEmissionMax:0.###}, {colorNote}");
            }
            catch (Exception ex) { EasyBakeLog.Warn("[Mist] tune failed: " + ex.Message); }
        }
    }
}
