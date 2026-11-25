using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.313 ([ghost], lead 11-11; Fire: "Terrain and water should be really easy since we already have working shaders for both"). EBM's OWN terrain
    // shader, STEP 1: EBM/Heightmap (Shaders\EbmHeightmap.shader) is a straight copy of our working FTS Custom/FiresHeightmap, in EBM's own bundle.
    // EBM swaps it onto the same chunks FTS's lava swap takes today (FTS FiresHeightmapShaderLoader.RebuildSwappable, its non-snow path), with the same
    // skip rules, and FTS stands down through EbmApi.HandlesShader("Custom/Heightmap"):
    //  - only a GENERATED heightmap's own material instance (m_buildData set): never the shared template every zone clones (FTS's fail-closed lesson);
    //  - only while that material is still on vanilla Custom/Heightmap (a chunk Vedr / FTS already swapped is theirs);
    //  - never the distant-LOD mesh (_IsDistantLod / _ISDISTANTLOD_ON: a near shader on it z-fights the near mesh);
    //  - never FAT's placed tiles (material name contains FiresAdminTerrain);
    //  - 1.2.460 ([visual], lead 11-18; Fire: "we will do A for now, and see what might be lost.... so that ebm can work WITH vedr when both are installed"):
    //    snow chunks are ours too. EBM/Heightmap is now Vedr's terrain shader ported in (Tools\PORT_vedr_terrain_into_ebm_1460.md), so it draws Vedr's snow
    //    look; Vedr 0.8.278 stands down on EbmApi.HandlesShader("Custom/Heightmap") and releases its chunks to Custom/Heightmap, and they are taken at the
    //    next pass. Was (1.2.459): never a snow chunk (Mountain / Deep North / Vedr's snow-terrain mask), and a chunk that turned snowy was handed back.
    // Value-preserving: only mat.shader changes; arrays, paint mask, ocean depth and lava knobs carry over (EBM/Heightmap keeps FTS's property names,
    // whose defaults are vanilla's constants). Key off: every material is put back at once. Swap at Heightmap.Generate (a chunk takes the shader as it
    // generates, so neighbours never disagree at a border) and a once-a-second pass (catch-up, hand-back, prune).
    // Verify twin (off): every 30 s, every material on EBM/Heightmap is re-tested against the rules above (a Warning names any that should not be ours)
    // and every generated chunk that qualifies but is still vanilla is counted.
    // N1 helper (Tools\DIFF_fireshightmap_vs_vanilla10_terrain_ps_20261111.md): once per session, one Debug line lists a vanilla terrain material's
    // float properties, so the two floats vanilla 1.0 divides the distance dither by can be named from a log instead of guessed.
    // Census (Debug, 30 s): chunks ours / handed back / skipped by reason.
    internal static class EbmTerrainShader
    {
        internal static ConfigEntry<bool> On, Verify;
        internal static void Bind(ConfigFile config)
        {
            On = EbmConfigMove.Bind(config, "40 - Shaders", "TerrainShader", /* OFF until gate ABBA (Fire 11-11 hard rule) */ false,
                "EBM draws all the ground, snow included, with its own terrain shader (Vedr's terrain look ported in: snow, mud, trails, wet ground, Ashlands lava glow). "
                + "Vedr's terrain shader stands down while this is on. Off = EBM stands down; Vedr's or FiresTossinShade's terrain shader, or the game's, takes over. OFF until gate ABBA.");
            Verify = EbmConfigMove.Bind(config, "90 - Debugging", "TerrainShaderVerify", false,
                "Needs [40 - Shaders] TerrainShader = true. Every 30 s, re-checks that EBM's terrain shader is only on chunks it should be (never distant or FAT tiles, never a chunk on another mod's shader) and counts chunks still waiting.");
            On.SettingChanged += (_, __) => { if (!On.Value) ReleaseAll("key turned off"); EbmApi.RecheckShaderOwnership(On.Definition.Key + " key", true); SayOwner("TerrainShader changed"); if (ShadowNoTess != null) ApplyTess("TerrainShader changed"); };   // 1.2.444: the tess keys follow the shader key   // 1.2.318: lower mods pick the shader up live
            BindCheapPaths(config);
            BindTess(config);
            BindFarLook(config);
            BindPieceSpec(config);
        }

        // 1.2.423 ([visual], lead approved ON: Fire 11-16 "metallic reflection of light probably should be faded out past a certain distance ... or at the very
        // least not continue to redraw the reflection every frame and shimmer"). Globals read by EbmSpecTame.cginc (EBM/Piece, EBM/Standard*), neutral names so
        // Vedr's piece shader can honour them too: _FiresSpecAA (specular AA strength), _FiresGlossFade (start m, end m, on). Live.
        internal static ConfigEntry<bool> PieceSpecularAA, PieceGlossFade;
        internal static ConfigEntry<float> GlossFadeStart, GlossFadeEnd;
        private static void BindPieceSpec(ConfigFile config)
        {
            PieceSpecularAA = EbmConfigMove.Bind(config, "03 - Far View", "PieceSpecularAA", true,
                "Thin shiny edges (iron fences, metal trims, far pieces) widen their highlight instead of flickering pixel to pixel as you move. Costs ~nothing. Live.");
            PieceGlossFade = EbmConfigMove.Bind(config, "03 - Far View", "PieceGlossFade", true,
                "Shine and metal fade out with distance (between GlossFadeStart and GlossFadeEnd), so far pieces stop sparkling in the sun. Live.");
            GlossFadeStart = EbmConfigMove.Bind(config, "03 - Far View", "GlossFadeStart", 80f, new ConfigDescription("Metres where the far shine fade starts.", new AcceptableValueRange<float>(10f, 1000f)));
            GlossFadeEnd = EbmConfigMove.Bind(config, "03 - Far View", "GlossFadeEnd", 200f, new ConfigDescription("Metres where far pieces are fully matte.", new AcceptableValueRange<float>(20f, 2000f)));
            System.EventHandler h = (_, __) => ApplyPieceSpec("changed");
            PieceSpecularAA.SettingChanged += h; PieceGlossFade.SettingChanged += h; GlossFadeStart.SettingChanged += h; GlossFadeEnd.SettingChanged += h;
            ApplyPieceSpec("at start");
        }

        private static void ApplyPieceSpec(string when)
        {
            float s = GlossFadeStart.Value, e = Mathf.Max(GlossFadeEnd.Value, s + 1f);
            Shader.SetGlobalFloat("_FiresSpecAA", PieceSpecularAA.Value ? 1f : 0f);
            Shader.SetGlobalVector("_FiresGlossFade", new Vector4(s, e, PieceGlossFade.Value ? 1f : 0f, 0f));
            EasyBakeLog.Info($"[Pieces] shine ({when}): specular AA {(PieceSpecularAA.Value ? "on" : "off")}, distance fade {(PieceGlossFade.Value ? $"on {s:0}-{e:0} m" : "off")} (globals _FiresSpecAA / _FiresGlossFade)");
        }

        // 1.2.418 ([visual], [lead]; Fire 11-16 DIRECT ORDER: "FIX EBM'S SHADER TO SHOW DISTANT TERRAIN AS REAL BIOME COLORS"; ON by his order, which overrides
        // the default-off gate rule). Vanilla's terrain shader multiplies the ground's colour by (1 - lodBlend), lodBlend = smoothstep((distance - 200) * 0.005),
        // so ground goes black from 200 to 400 m and only up-facing ground above y 70 turns 0.2 grey (decompile D7536-7537, D8177-8190; EbmHeightmap.shader
        // keeps those lines). This key sets the GLOBAL _FiresFarLook = 1: EBM/Heightmap keeps the real biome colour at every distance, keeps the detail
        // layers vanilla drops past 400 m, and reads mipped array taps far out (no shimmer). One neutral name so Vedr's and FAT's terrain shaders can honour
        // the same switch on the chunks they draw. Live; no restart.
        internal static ConfigEntry<bool> FarLook;
        private static void BindFarLook(ConfigFile config)
        {
            FarLook = EbmConfigMove.Bind(config, "03 - Far View", "FarLook", true,
                "Far ground keeps its real biome colours at every distance (the game's terrain fades its ground to black from 200 to 400 m and leaves it to the fog). "
                + "Applies to EBM's terrain shader, and to Vedr's / the terrain mod's when they read the same switch. Off = the game's look exactly. Live.");
            FarLook.SettingChanged += (_, __) => ApplyFarLook("changed");
            ApplyFarLook("at start");
        }

        private static void ApplyFarLook(string when)
        {
            bool on = FarLook.Value;
            Shader.SetGlobalFloat("_FiresFarLook", on ? 1f : 0f);
            if (on) EasyBakeLog.Info($"[Terrain] FarLook on ({when}): far ground keeps its biome colours at every distance (no fade to black past 200 m; mipped far taps); global _FiresFarLook = 1 for EBM/Heightmap and any terrain shader that reads it");
            else EasyBakeLog.Info($"[Terrain] FarLook off ({when}): the game's distant fade (ground darkens from 200 m, black by 400 m)");
        }

        // 1.2.338 ([visual], lead 11-12, STEP 3; Tools\DESIGN_ebm_terrain_shader_20261111.md §3c "Step 3 as written"): EBM/Heightmap's tessellation levers,
        // in its hull constant function (FiresTess). Globals only EBM/Heightmap reads (live). All OFF until each one's gate (Fire's rule); each says why
        // it acts (Info) or stays off (Debug). No census row can count hull patches from C#: the gate is one ON leg vs the latest same-build run plus a
        // same-pose RenderDoc pair (design §3c), and the 30 s [Terrain] line carries the key state so a log reader knows which leg ran.
        //  - TerrainShadowNoTess (step 1): shadow casters (sun cascades + local lights) tessellate at factor 1. The caster keeps the Ashlands / paint rise
        //    at the mesh vertices and loses the sub-vertex detail (design look risk: Ashlands paint edges; non-Ashlands, non-snow chunks rise 0 = same surface).
        //  - TerrainCameraTessCut (step 3): camera passes drop a patch whose three vertices all lie outside one side plane of the view frustum (it was
        //    clipped anyway: nothing drawn changes) and end vanilla's distance curve at TerrainTessFullDistance metres (40 = vanilla's curve exactly).
        //  - Step 2 of the design (skip a chunk's LOCAL-light shadow draw when its bounds miss the light) is NOT here: the built-in pipeline has no per-light
        //    caster filter (Renderer.shadowCastingMode and Light.cullingMask are per renderer / per layer, and terrain chunks share their layer with their
        //    collider), and Unity already culls casters by bounds vs the light's range, which is the test the design names; the 0-primitive draws it saw
        //    are patches that pass that test and raster nothing. Step 1 makes them one triangle per patch. See the 1.2.338 ticket.
        internal static ConfigEntry<bool> ShadowNoTess, CameraTessCut;
        internal static ConfigEntry<float> TessFullDistance;
        private static void BindTess(ConfigFile config)
        {
            ShadowNoTess = EbmConfigMove.Bind(config, "40 - Shaders", "TerrainShadowNoTess", /* OFF until its gate (Fire 11-11 hard rule) */ false,
                "Needs [40 - Shaders] TerrainShader = true. EBM's terrain shader: the ground's SHADOW casters (sun and torches) skip the extra tessellation triangles; the ground itself "
                + "still tessellates. Shadows of Ashlands lava-paint edges can shift by under a metre; elsewhere the shadow surface is the same. OFF until tested.");
            CameraTessCut = EbmConfigMove.Bind(config, "40 - Shaders", "TerrainCameraTessCut", /* OFF until its gate (Fire 11-11 hard rule) */ false,
                "Needs [40 - Shaders] TerrainShader = true. EBM's terrain shader: ground patches wholly outside the screen are dropped before tessellation (they were clipped anyway, "
                + "nothing visible changes), and the tessellation distance curve ends at TerrainTessFullDistance. OFF until tested.");
            TessFullDistance = EbmConfigMove.Bind(config, "40 - Shaders", "TerrainTessFullDistance", 40f, new ConfigDescription(
                "Needs [40 - Shaders] TerrainShader = true. With TerrainCameraTessCut on: metres from the camera where the ground's extra tessellation has faded out. 40 = the game's own curve (no look change); "
                + "lower = fewer tessellated triangles between 10 m and here, a LOOK change (silhouette detail) for Fire's eyes only.", new AcceptableValueRange<float>(15f, 40f)));
            ShadowNoTess.SettingChanged += (_, __) => ApplyTess("changed");
            CameraTessCut.SettingChanged += (_, __) => ApplyTess("changed");
            TessFullDistance.SettingChanged += (_, __) => ApplyTess("changed");
            ApplyTess("at start");
        }

        private static void ApplyTess(string when)
        {
            bool shaderOn = On != null && On.Value;   // 1.2.444 (Fire rule 11-18): these three only act through EBM/Heightmap, so they need [Optimize] TerrainShader = true
            if (!shaderOn && (ShadowNoTess.Value || CameraTessCut.Value)) EasyBakeLog.Info($"[Terrain] TerrainShadowNoTess / TerrainCameraTessCut stand down ({when}): they need [40 - Shaders] TerrainShader = true");
            bool sh = shaderOn && ShadowNoTess.Value, cut = shaderOn && CameraTessCut.Value; float far = Mathf.Clamp(TessFullDistance.Value, 15f, 40f);
            Shader.SetGlobalFloat("_EbmShadowNoTess", sh ? 1f : 0f);
            Shader.SetGlobalFloat("_EbmTessCut", cut ? 1f : 0f);
            Shader.SetGlobalFloat("_EbmTessFar", far);
            if (sh) EasyBakeLog.Info($"[Terrain] TerrainShadowNoTess on ({when}): EBM/Heightmap shadow casters tessellate at factor 1 (sun cascades and local lights); the camera pass keeps the game's curve. Applies only while TerrainShader draws the ground");
            else EasyBakeLog.Debug($"[Terrain] TerrainShadowNoTess off ({when}): shadow casters tessellate like the ground (step-1 hull)");
            if (cut) EasyBakeLog.Info($"[Terrain] TerrainCameraTessCut on ({when}): off-screen ground patches are dropped before tessellation; the distance curve ends at {far:0} m{(far >= 40f ? " (the game's own curve)" : " (LOOK CHANGE: shorter than the game's 40 m)")}. Applies only while TerrainShader draws the ground");
            else EasyBakeLog.Debug($"[Terrain] TerrainCameraTessCut off ({when}): the step-1 hull (every patch tessellated, curve to 40 m)");
        }

        // 1.2.335 ([ghost], lead 11-12, STEP 2; Tools\DESIGN_ebm_terrain_shader_20261111.md §3/§3a): EBM/Heightmap's cheap paths, one key per group so each is
        // its own A/B. Each skips texture taps whose weight is a true 0 (C3: a true 1), so the ground looks the same (Verify pass mark: <= 1/255 per channel).
        // The keys set shader GLOBALS that only EBM/Heightmap reads (live: no restart, no material walk). All OFF until each one's A/B (Fire's rule).
        private static readonly string[][] CheapPaths =   // { key, global, what }
        {
            new[] { "TerrainCheapMistlands", "_EbmCheapMist", "C1+C2: ground outside the Mistlands skips the Mistlands base and cliff textures (their weight is exactly 0 there)." },
            new[] { "TerrainCheapCliff", "_EbmCheapCliff", "C3: gentle slopes (ground normal y >= 0.851) skip the cliff textures, whose blend weight is already exactly 1 toward the ground there." },
            new[] { "TerrainCheapSnow", "_EbmCheapSnow", "C4: ground outside Mountain / Deep North skips the snow overlay textures (snow weight exactly 0)." },
            new[] { "TerrainCheapPaint", "_EbmCheapPaint", "C5-C7: unpainted ground skips the dug-dirt / cultivated / paved textures of the paint channel that is 0 there." },
            new[] { "TerrainCheapDryWeather", "_EbmCheapWet", "C8: in dry weather, ground outside Mountain / Deep North skips the cloud-shadow wetness lookup (its weight is exactly 0)." },
            new[] { "TerrainCheapNoSnowMud", "_EbmCheapFiresSnow", "C9: ground outside Mountain / Deep North skips the snow mud, trail and fire-ring work (all of it is multiplied by a 0 snow weight there)." },
            new[] { "(1.2.385, no key)", "_EbmCheapMudTrail", "C10: on snow ground the trail tap runs only with a mud gate, the mud / stone taps only where a trail makes mud, the trodden base tap only where it blends." },
            new[] { "(1.2.385, no key)", "_EbmCheapDomain", "C11: the tessellation stage samples the paint only where the vertex has snow or ash weight (the game's stage takes 1 tap, this one took 5)." },
        };
        private static readonly List<ConfigEntry<bool>> s_cheap = new List<ConfigEntry<bool>>();

        // 1.2.385 ([visual], [lead]; Fire 11-14: "All of those extra shader checks for ebm's heightmap shouldn't even run outside of vedr winter or if vedr isn't
        // installed tho right?" / "get it fixed"): Fire's own cfg ran TerrainShader with all six TerrainCheap keys false, so every ground pixel in every biome paid
        // the snow-mud taps (EbmHeightmap.shader, the block behind cMud) although their weight is exactly 0 outside Mountain / Deep North. Each of these paths
        // skips work whose weight is a true 0, so they are no longer keys: every global is set to 1 at start. The six old [Optimize] TerrainCheap* entries are not
        // read any more (a saved "false" has no effect). One verify switch stays, off by default, so a capture can compare the picture and the cost with and
        // without the skips in one launch.
        private static ConfigEntry<bool> s_exactSkipsOff;
        private static void BindCheapPaths(ConfigFile config)
        {
            s_exactSkipsOff = EbmConfigMove.Bind(config, "90 - Debugging", "TerrainExactSkipsOff", false,
                "Needs [40 - Shaders] TerrainShader = true. Verify switch for EBM's terrain shader: true turns its exact work skips OFF (the ground looks the same, it only costs more), so a capture can compare with and without them in one launch. Leave false. Applies live.");
            s_exactSkipsOff.SettingChanged += (_, __) => ApplyCheap("changed");
            ApplyCheap("at start");
        }

        private static void ApplyCheap(string when)
        {
            bool on = !s_exactSkipsOff.Value;
            foreach (var row in CheapPaths) Shader.SetGlobalFloat(row[1], on ? 1f : 0f);
            EasyBakeLog.Info($"[Terrain] exact skips {(on ? "on" : "OFF by the verify switch [Debugging] TerrainExactSkipsOff")} ({when}): {CheapPaths.Length} path(s) of EBM/Heightmap that skip work whose weight is exactly 0 (C1-C11); applies only while TerrainShader is on");
            SayCheap(when);
        }

        // Debug: which cheap paths are on (once at start and on each change)
        private static void SayCheap(string when)
        {
            var on = new List<string>();
            for (int i = 0; i < s_cheap.Count; i++) if (s_cheap[i].Value) on.Add(CheapPaths[i][0]);   // 1.2.385: s_cheap stays empty (the keys are gone); the globals are listed instead
            if (s_exactSkipsOff != null && !s_exactSkipsOff.Value) for (int i = 0; i < CheapPaths.Length; i++) on.Add(CheapPaths[i][1]);
            EasyBakeLog.Debug($"[Terrain] cheap paths {when}: {(on.Count == 0 ? "none on (the step-1 shader chain)" : string.Join(", ", on))}");
        }

        internal const string Vanilla = "Custom/Heightmap";
        private static readonly AccessTools.FieldRef<List<Heightmap>> s_maps = AccessTools.StaticFieldRefAccess<List<Heightmap>>(AccessTools.Field(typeof(Heightmap), "s_heightmaps"));
        private static readonly AccessTools.FieldRef<Heightmap, Material> s_matOf = AccessTools.FieldRefAccess<Heightmap, Material>("m_materialInstance");
        internal static Material MatOf(Heightmap hm) => hm != null ? s_matOf(hm) : null;   // 1.2.506: the same accessor for FarTerrainTiles.NearHdOurs (one FieldRef in EBM)
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> s_buildOf = AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static readonly int Prop_IsDistantLod = Shader.PropertyToID("_IsDistantLod");

        private static readonly Dictionary<Material, Shader> s_swapped = new Dictionary<Material, Shader>();
        private static readonly List<Material> s_scratch = new List<Material>();
        private static float s_nextPass, s_nextReport;
        private static bool s_saidOn, s_saidWhy, s_n1Said;
        private static int c_swapped, c_skipLod, c_skipFat, c_skipOther, c_skipNotGenerated;
        private static int c_baseCarried, c_baseMissing, c_trueCarried, s_snowBaseId = -1, s_trueOnId = -1;   // 1.2.464 G3 trail census

        private static bool Wanted => On != null && On.Value && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        /// <summary>True while the key is on and EBM/Heightmap is loaded: EbmApi.HandlesShader("Custom/Heightmap") answers this (Vedr 0.8.278+ and FTS stand down).</summary>
        internal static bool Active => Wanted && Indirect.EbmIndirectAssets.Terrain(out _) != null;

        // 1.2.460: one Info line at start and on every change of who owns the terrain shader (Active flips: the key, the bundle loading)
        private static int s_ownerSaid = -1;
        private static void SayOwner(string why)
        {
            int now = Active ? 1 : 0;
            if (now == s_ownerSaid) return;
            s_ownerSaid = now;
            if (now == 1) EasyBakeLog.Info($"[Terrain] terrain shader owner: EBM/Heightmap (all chunks; Vedr's terrain shader stands down, FAT adds none) ({why})");
            else EasyBakeLog.Info($"[Terrain] terrain shader owner: not EBM (TerrainShader off) ({why})");
        }

        private static bool IsDistantLod(Material mat) => (mat.HasProperty(Prop_IsDistantLod) && mat.GetFloat(Prop_IsDistantLod) >= 0.5f) || mat.IsKeywordEnabled("_ISDISTANTLOD_ON");
        // 1.2.466 (G7-g9 GC.Alloc census: "EbmTerrainShader.Tick() 1.5 MB/min, 27,948 allocations, 57 bytes avg"): Material.name and Shader.name build a new
        // string on every read, and the 1 s pass read both for every chunk. A material's name never changes, so the fat-tile answer is cached per material
        // (pruned with the swapped table's dead keys); a shader's name never changes, so the "is it vanilla Custom/Heightmap" answer is cached per shader.
        private static readonly Dictionary<Material, bool> s_fatTile = new Dictionary<Material, bool>();
        private static readonly Dictionary<Shader, bool> s_isVanilla = new Dictionary<Shader, bool>();
        private static bool IsFatTile(Material mat)
        {
            if (s_fatTile.TryGetValue(mat, out bool fat)) return fat;
            fat = mat.name.IndexOf("FiresAdminTerrain", StringComparison.OrdinalIgnoreCase) >= 0;
            s_fatTile[mat] = fat;
            return fat;
        }
        private static bool IsVanillaShader(Shader sh)
        {
            if (sh == null) return false;
            if (s_isVanilla.TryGetValue(sh, out bool v)) return v;
            v = sh.name == Vanilla;
            s_isVanilla[sh] = v;
            return v;
        }

        // 0 = ours to draw; otherwise why not (counted in the census). `mine` = the material already wears EBM/Heightmap.
        // 1.2.460: no snow rule (was Why.Snow, 1.2.459 :239). A chunk still on another mod's shader (Vedr/Heightmap before Vedr releases it, FTS, anyone) is
        // never taken: only vanilla Custom/Heightmap or our own; Vedr 0.8.278 hands its chunks back to Custom/Heightmap when it stands down, so they are taken then.
        private enum Why { Ours, NotGenerated, DistantLod, FatTile, OtherShader }
        private static Why Judge(Heightmap hm, Material mat, Shader ours)
        {
            if (s_buildOf(hm) == null) return Why.NotGenerated;            // corner biomes not real yet
            if (hm.IsDistantLod || IsDistantLod(mat)) return Why.DistantLod;
            if (IsFatTile(mat)) return Why.FatTile;
            var sh = mat.shader;
            if (sh == null || (sh != ours && !IsVanillaShader(sh))) return Why.OtherShader;   // Vedr / FTS / anyone else's (1.2.466: cached name test)
            return Why.Ours;
        }

        [HarmonyPatch(typeof(Heightmap), "Generate")]
        private static class TakeAtGenerate
        {
            private static void Postfix(Heightmap __instance)
            {
                using (EbmPatchMarkers.HeightmapGenerate.Auto()) GenerateBody(__instance);   // 1.2.360: profiler marker only
            }
            private static void GenerateBody(Heightmap __instance)
            {
                try
                {
                    if (!Wanted || __instance == null) return;
                    var ours = Indirect.EbmIndirectAssets.Terrain(out _);
                    var mat = ours != null ? s_matOf(__instance) : null;
                    if (mat == null || mat.shader == ours) return;
                    if (Judge(__instance, mat, ours) == Why.Ours) Swap(mat, ours);
                }
                catch (Exception e) { EasyBakeLog.Debug("[Terrain] generate swap skipped: " + e.Message); }
            }
        }

        // EBM's Update, every frame: one compare until the next 1 s pass
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < s_nextPass) return;
            s_nextPass = now + 1f;
            EbmVedrDefaults.Tick();   // 1.2.460: _EbmVedrOn switch (start + world start)
            SayOwner(s_ownerSaid < 0 ? "at start" : "changed");   // 1.2.460: one compare a second; logs only on a flip
            if (!Wanted) { if (s_swapped.Count > 0) ReleaseAll("key off"); return; }
            if (ZNetScene.instance == null) return;
            var ours = Indirect.EbmIndirectAssets.Terrain(out string why);
            if (ours == null)
            {
                if (!s_saidWhy) { s_saidWhy = true; EasyBakeLog.Info($"[Terrain] TerrainShader is on but EBM/Heightmap is not available ({why}): the ground stays on the game's shader"); }
                return;
            }
            var maps = s_maps();
            if (maps == null) return;
            for (int i = 0; i < maps.Count; i++)
            {
                var hm = maps[i];
                if (hm == null) continue;
                var mat = s_matOf(hm);
                if (mat == null) continue;
                if (!s_n1Said && IsVanillaShader(mat.shader) && !IsDistantLod(mat)) SayFloats(mat);
                Why w = Judge(hm, mat, ours);
                bool mine = mat.shader == ours;
                if (w == Why.Ours) { if (!mine) Swap(mat, ours); if (!s_hideSaid) SayHide(hm); continue; }
                // 1.2.460: no snowy hand-back (was 1.2.459 :292); EBM/Heightmap draws snow chunks itself
                switch (w) { case Why.DistantLod: c_skipLod++; break; case Why.FatTile: c_skipFat++; break; case Why.NotGenerated: c_skipNotGenerated++; break; default: c_skipOther++; break; }
            }
            Prune();
            Report(maps, ours);
        }

        // 1.2.419 ([visual], [lead] / [seasons] 11-16): once per session, the hide distance a swapped near chunk really draws to. Vanilla writes
        // _LodHideDistance into each terrain renderer's property block (Heightmap.ApplySettings: max(1, near simulation distance) x zone diagonal 90.5 m),
        // which overrides the material; this line reads it back from the renderer, with the synced near distance, so a log shows where near ground ends.
        private static bool s_hideSaid;
        private static readonly MaterialPropertyBlock s_hideMpb = new MaterialPropertyBlock();
        private static readonly int s_hideId = Shader.PropertyToID("_LodHideDistance");
        private static void SayHide(Heightmap hm)
        {
            s_hideSaid = true;
            try
            {
                var mr = hm.GetComponent<MeshRenderer>();
                if (mr == null) return;
                mr.GetPropertyBlock(s_hideMpb);
                float hide = s_hideMpb.GetFloat(s_hideId);
                string near = "unknown";
                if (ZNet.instance != null) { var sd = ZNet.instance.GetSyncedSimulationDistance(); near = sd.NearSimulationDistance.ToString(); }
                EasyBakeLog.Info($"[Terrain] near chunk hide distance {hide:0} m (renderer block; synced near simulation distance {near}): near ground draws to about there, past it the distant terrain draws");
            }
            catch (Exception e) { EasyBakeLog.Debug("[Terrain] near chunk hide distance not read: " + e.Message); }
        }

        private static void Swap(Material mat, Shader ours)
        {
            if (s_swapped.ContainsKey(mat)) return;
            try
            {
                s_swapped[mat] = mat.shader;
                mat.shader = ours;   // value-preserving: arrays / paint mask / _depth / lava knobs carry over
                c_swapped++;
                // 1.2.464 ([visual], lead 11-19 G3 mountain summer: Vedr's walked trail gone under EBM/Heightmap). The trail is Vedr's per-chunk material texture
                // _VedrSnowBase (G = base snow, compared with paint G: EbmHeightmap.shader:1397 = Vedr's own line), with the true-biome colours _VedrTrueOn/C0..C3,
                // both set ONLY on the chunk's material by Vedr's SnowBaseMask.PushBaseTexture (Vedr 0.8.284 SnowBaseMask.cs:337-355), event-driven on rebuild /
                // terrain op. This counts, per taken chunk, whether those values arrived with the material, so a log proves whether the trail is missing its data.
                if (s_snowBaseId < 0) { s_snowBaseId = Shader.PropertyToID("_VedrSnowBase"); s_trueOnId = Shader.PropertyToID("_VedrTrueOn"); }
                if (EbmVedrDefaults.VedrPresent)
                {
                    if (mat.GetTexture(s_snowBaseId) != null) c_baseCarried++; else c_baseMissing++;
                    if (mat.HasProperty(s_trueOnId) && mat.GetFloat(s_trueOnId) > 0.5f) c_trueCarried++;
                }
                if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info("[Terrain] EBM's terrain shader draws the ground (EBM/Heightmap) on every generated chunk, snow included; the distant LOD, FAT tiles and chunks still on another mod's shader stay as they were"); }
            }
            catch (Exception e) { s_swapped.Remove(mat); EasyBakeLog.Debug($"[Terrain] swap failed on '{mat.name}': {e.Message}"); }
        }

        private static void HandBack(Material mat)
        {
            if (!s_swapped.TryGetValue(mat, out var orig)) return;
            try { mat.shader = orig != null ? orig : Shader.Find(Vanilla); } catch { }
            s_swapped.Remove(mat);
        }

        private static void ReleaseAll(string why)
        {
            int n = 0;
            s_scratch.Clear(); s_scratch.AddRange(s_swapped.Keys);
            foreach (var mat in s_scratch) { if (mat == null) continue; HandBack(mat); n++; }
            s_swapped.Clear(); s_scratch.Clear();
            if (n > 0) EasyBakeLog.Info($"[Terrain] {n} chunk(s) back on the game's terrain shader ({why})");
        }

        private static void Prune()
        {
            s_scratch.Clear();
            foreach (var kv in s_swapped) if (kv.Key == null) s_scratch.Add(kv.Key);
            foreach (var m in s_scratch) s_swapped.Remove(m);
            s_scratch.Clear();
            foreach (var kv in s_fatTile) if (kv.Key == null) s_scratch.Add(kv.Key);   // 1.2.466: destroyed chunk materials leave the name cache too
            foreach (var m in s_scratch) s_fatTile.Remove(m);
            s_scratch.Clear();
        }

        // N1: the float properties of one vanilla terrain material, once (Debug)
        private static void SayFloats(Material mat)
        {
            s_n1Said = true;
            try
            {
                var sh = mat.shader; var sb = new StringBuilder();
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    var t = sh.GetPropertyType(i);
                    if (t != ShaderPropertyType.Float && t != ShaderPropertyType.Range) continue;
                    string name = sh.GetPropertyName(i);
                    sb.Append(name).Append('=').Append(mat.GetFloat(name).ToString("0.###")).Append(' ');
                }
                EasyBakeLog.Debug($"[Terrain] vanilla terrain material '{mat.name}' floats (for the 1.0 distance-dither divisor, N1): {sb}");
            }
            catch (Exception e) { EasyBakeLog.Debug("[Terrain] float census failed: " + e.Message); }
        }

        private static void Report(List<Heightmap> maps, Shader ours)
        {
            float t = Time.unscaledTime;
            if (t < s_nextReport) return;
            s_nextReport = t + 30f;
            EasyBakeLog.Debug($"[Terrain] last 30 s: {s_swapped.Count} chunk(s) on EBM/Heightmap (snow included), {c_swapped} swapped; passes skipped distant LOD {c_skipLod}, FAT tile {c_skipFat}, other shader (Vedr / FTS) {c_skipOther}, not generated yet {c_skipNotGenerated}; tess levers: shadowNoTess {(ShadowNoTess != null && ShadowNoTess.Value ? "on" : "off")}, cameraCut {(CameraTessCut != null && CameraTessCut.Value ? $"on (to {TessFullDistance.Value:0} m)" : "off")}");
            // 1.2.466 ([visual], lead 11-19 G3c): the swap-time count is taken in the Heightmap.Generate postfix, BEFORE Vedr's push in the same Regenerate
            // (Vedr's ApplyModifiers push is gated out on the vanilla shader; its RebuildRenderMesh postfix pushes after EBM's swap, REF Heightmap.cs:197-214), so on a
            // fresh chunk it always read "missing". The answer that matters is whether the data ENDS UP there: re-read every live material EBM owns, each report.
            if (EbmVedrDefaults.VedrPresent && s_swapped.Count > 0)
            {
                if (s_snowBaseId < 0) { s_snowBaseId = Shader.PropertyToID("_VedrSnowBase"); s_trueOnId = Shader.PropertyToID("_VedrTrueOn"); }
                int live = 0, baseOn = 0, trueOn = 0;
                foreach (var kv in s_swapped)
                {
                    var m = kv.Key;
                    if (m == null || m.shader != ours) continue;
                    live++;
                    if (m.GetTexture(s_snowBaseId) != null) baseOn++;
                    if (m.HasProperty(s_trueOnId) && m.GetFloat(s_trueOnId) > 0.5f) trueOn++;
                }
                if (live > 0)   // Info, so it reaches the disk log at the players' level too
                    EasyBakeLog.Info($"[Terrain] Vedr's chunk data on the live EBM chunks now: snow base (_VedrSnowBase, Vedr's trail / mud data) on {baseOn} of {live} live EBM chunks, true-biome colours (_VedrTrueOn) on {trueOn}; " +
                                     $"at swap time in the last 30 s (before Vedr's push, so fresh chunks read missing): on {c_baseCarried}, missing on {c_baseMissing}. A chunk without the snow base cannot show Vedr's trails and mud");
            }
            c_baseCarried = c_baseMissing = c_trueCarried = 0;
            c_swapped = c_skipLod = c_skipFat = c_skipOther = c_skipNotGenerated = 0;
            if (Verify == null || !Verify.Value) return;
            int bad = 0, waiting = 0; string first = null;
            for (int i = 0; i < maps.Count; i++)
            {
                var hm = maps[i]; if (hm == null) continue;
                var mat = s_matOf(hm); if (mat == null) continue;
                Why w = Judge(hm, mat, ours);
                if (mat.shader == ours && w != Why.Ours) { bad++; if (first == null) first = $"chunk {hm.transform.position.x:0},{hm.transform.position.z:0} ({w})"; }
                else if (w == Why.Ours && mat.shader != ours) waiting++;
            }
            if (bad == 0) EasyBakeLog.Debug($"[Terrain] verify: every EBM/Heightmap chunk qualifies; {waiting} qualifying chunk(s) still on the game's shader (taken at the next pass)");
            else EasyBakeLog.Warn($"[Terrain] verify: {bad} chunk(s) on EBM/Heightmap that should not be (first: {first}); {waiting} qualifying chunk(s) waiting");
        }
    }
}
