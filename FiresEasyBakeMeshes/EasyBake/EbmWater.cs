using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // [seasons] 11-11 (lead; Fire: "Terrain and water should be really easy since we already have working shaders for both"; Fire 11-11: EBM self-contained,
    // FTS stands down; Tools\PLAN_ebm_water_shader_step1_20261111.md). EBM's OWN water shader, STEP 1: EBM/Water (FatShaderLinear Assets\EbmIndirect\
    // EbmWater.shader) is a byte-for-byte copy of our working FTS Custom/FiresWater (same passes, same look), in EBM's own bundle. Same shape as [ghost]'s
    // EbmTerrainShader (1.2.313). Nothing redesigned; the optimisations (torch pass without tessellation, a tessellation key) are STEP 2 behind their own keys.
    //  - Swap exactly where FTS's FiresWaterShaderLoader swaps today, with its rules: zone water only (a WaterVolume under a ZNetView is a built piece,
    //    e.g. the hot tub, and keeps its own material); only mat.shader changes (value-preserving: vanilla's _WaterTime / _depth / _UseGlobalWind, foam
    //    textures and colours carry over); _IsLod pinned to 0 (uniform tessellation on zone tiles, as FTS); _VolumeSize = the volume's collider width.
    //    The prefab materials at ZNetScene.Awake (tiles are born on EBM/Water: no spawn-then-swap), each WaterVolume.OnEnable, and a once-a-second pass
    //    (key turned on mid-game, catch-up, prune). Only a material still on Custom/Water or FTS's Custom/FiresWater is taken; anyone else's stays theirs.
    //  - FTS stands down through EbmApi.HandlesShader("Custom/Water") (its own swap skips; its tuning keeps feeding the SAME _Fires* globals, which
    //    EBM/Water reads unchanged). FTS registers as the globals owner (EbmApi.RegisterWaterGlobalsOwner) with its per-material foam writer, so its HD
    //    foam and foam values land on EBM/Water exactly as on FiresWater today.
    //  - No owner registered (EBM alone): EBM pushes FTS's DEFAULT values for every global the shader would otherwise read as 0 (FTS ConfigManager Bind
    //    defaults, Tools\_ebm_water_fts_config_defaults.txt; statics in FiresWaterTuning / FiresWaterFoamGlobals / FiresWaterFoamW2 / FiresShoreField /
    //    FiresAdminTerrain 1320-1325), once per world, and keeps the main camera's depth texture on (FTS FiresUnderwaterEnv.LateUpdate does the same for
    //    _FiresPixelDepth). FTS's own systems are NOT in EBM: no planar mirror (sky cubemap instead), no shore field, procedural caustics, no rain rings,
    //    no lace foam texture, vanilla foam texture. Every one of those has its own on/off global in the shader that reads 0 = its fallback.
    // Key off: every material goes back to the shader it had (live tiles born from a swapped prefab material go back to that material's original).
    // Verify twin (off): every 30 s, every zone tile is counted by shader (EBM/Water / vanilla / FiresWater / other) and an EBM/Water material on a
    // built piece's water is a Warning; owner, depth-texture state and the camera's rendering path are logged.
    internal static class EbmWater
    {
        internal static ConfigEntry<bool> On, Verify;
        internal static void Bind(ConfigFile config)
        {
            On = EbmConfigMove.Bind(config, "40 - Shaders", "WaterShader", /* OFF until gate ABBA (Fire 11-11 hard rule) */ false,
                "EBM draws the water. Off = EBM stands down; Vedr's or FiresTossinShade's water shader, or the game's, takes over. "
                + "EBM draws the sea and lakes with its own water shader (a copy of our FiresWater: same look). With TossinShade installed it keeps feeding the water "
                + "its settings; without it the water uses its built-in fallbacks (sky reflection instead of the mirror, no shore field, built-in caustics). OFF until gate ABBA.");
            EbmFtsGlobals.Bind(config);   // 1.2.428
            EbmFatWaterLook.Bind(config);   // 1.2.437: EBM as FAT's water look provider ([Water] FAT Water Look, default off)
            Verify = EbmConfigMove.Bind(config, "90 - Debugging", "WaterShaderVerify", false,
                "Needs [40 - Shaders] WaterShader = true. Every 30 s, counts the water tiles by shader and checks EBM's water shader is never on a built piece's water (the hot tub).");
            On.SettingChanged += (_, __) => { if (!On.Value) ReleaseAll("key turned off"); EbmApi.RecheckShaderOwnership(On.Definition.Key + " key", true); };   // 1.2.318: lower mods pick the shader up live
            // 1.2.316 (Fire: "ebms water shader sliders need to live in ebms own config"): every look value EBM/Water reads is a key here, in EBM's own
            // cfg (defaults = vanilla for the 41h-41k shallow terms, FTS's defaults for the rest), pushed by EBM itself under the shader's global names
            // (Fire 11-11: with EBM loaded FTS turns its conflicting shaders off, so the two never push at once). EBM never reads FTS's cfg; no hand-off.
            s_cfg = config;
            Look(push: false);   // binds every key now, so they are in the cfg before a world loads
            MoveOldLookSection(config);
            // 1.2.322 ([lead]: FTS stands down fully; EBM owns the cheap water looks itself): rain rings and wind drift, our own code (FTS's FiresWaterRain /
            // FiresWaterWindTravel logic), EBM's own keys. Defaults = FTS's, so the water looks as it did while FTS fed it.
            RainOn = EbmConfigMove.Bind(config, SecWaves, "Rain Rings", true, "Needs [40 - Shaders] WaterShader = true. Rings on the water while it rains. Off = the water ignores rain.");
            RainSize = EbmConfigMove.Bind(config, SecWaves, "Rain Ring Spacing", 1.2f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. How far apart the rain rings are. Higher = bigger, sparser rings. In metres. Range 0.2 to 5. Default 1.2.", new AcceptableValueRange<float>(0.2f, 5f)));
            RainStrength = EbmConfigMove.Bind(config, SecWaves, "Rain Ring Strength", 0.6f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. How strongly the rings ripple the surface. Higher = stronger rings. 0 = none. Range 0 to 2. Default 0.6.", new AcceptableValueRange<float>(0f, 2f)));
            RainUnder = EbmConfigMove.Bind(config, SecWaves, "Rain Rings From Below", 1.5f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. How much stronger the rings look from under the water. Range 0 to 4. Default 1.5.", new AcceptableValueRange<float>(0f, 4f)));
            RainFade = EbmConfigMove.Bind(config, SecWaves, "Rain Ring Distance", 90f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. Rings fade out by this distance from you. Higher = rings further out. In metres. Range 1 to 500. Default 90.", new AcceptableValueRange<float>(1f, 500f)));
            WindDrift = EbmConfigMove.Bind(config, SecWaves, "Smooth Wind Drift", true, "Needs [40 - Shaders] WaterShader = true. Foam and light patterns drift steadily with the wind instead of swinging back and forth when the wind turns. Off = the old swinging drift.");
            FiresEasyBakeMeshesPlugin_AddBasicRain();
            // 1.2.391 ([visual], [lead]; Fire: never worse than vanilla): EBM/Water tessellated every zone patch at the full factor, in view or not, near or far.
            TessCut = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterTessCut", false /* 1.2.392 ([lead]): OFF. Its 1.2.391 legs showed no gain (frame ms off / on: Deep North 3.440 / 3.445, Plains 4.990 / 5.017; ocean WaterTessFarEnd 0 vs 160: 4.568 / 4.565), and a key ships on only after its leg shows the win. The key never shipped, so no saved value exists */,
                "Needs [40 - Shaders] WaterShader = true. With this on, EBM's water does less work where it cannot be seen: water outside your view is not subdivided at all, and far water can be subdivided less (WaterTessFarStart / WaterTessFarEnd). Applies live. Off (the default) = every water tile is subdivided fully.");   // 1.2.393: wording for a key that is off by default ([seasons])
            TessFarStart = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterTessFarStart", 80f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With WaterTessCut: water closer than this keeps its full wave detail. In metres. Range 0 to 500. Default 80.", new AcceptableValueRange<float>(0f, 500f)));
            TessFarEnd = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterTessFarEnd", 0f /* 1.2.391 ([seasons] review, Fire: no look change for fps): the far falloff is OFF until its shot pair; 160 is the value to test */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With WaterTessCut: from WaterTessFarStart to this distance the wave detail of far water falls to its lowest (try 160). 0, or anything not past WaterTessFarStart, keeps full detail at every distance. In metres. Range 0 to 1000. Default 0 (full detail everywhere).", new AcceptableValueRange<float>(0f, 1000f)));
            TessCut.SettingChanged += (_, __) => TessChanged("WaterTessCut changed");
            TessFarStart.SettingChanged += (_, __) => TessChanged("WaterTessFarStart changed");
            TessFarEnd.SettingChanged += (_, __) => TessChanged("WaterTessFarEnd changed");
            PushTessCut(null);   // the global is set before any world; the Info line comes with the first world (it counts the tiles)
            // 1.2.475 ([visual], lead GO 11-19; Vedr-only legs 11.5M tris vs same7 3.9M): while Vedr's FFT waves drive the water, EBM/Water's zone tiles kept the full
            // factor at every distance (EbmWater.shader hsConstantVD: "Not while Vedr's FFT waves drive the surface: they keep the uniform factor"). OFF until the A/B.
            FftFalloff = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterFftTessFalloff", false /* 1.2.475: OFF until the Vedr-water A/B + Fire's look check (new-key rule) */,
                "Needs [40 - Shaders] WaterShader and WaterVanillaDistanceDetail = true, and Vedr's wave physics on. Vedr's waves keep their full detail near you (WaterFftTessStart) and far water gets less, down to WaterFftTessFloor by WaterFftTessEnd, instead of full detail on every water tile at every distance. Off = full detail everywhere while Vedr's waves are on, as before. Applies live.");
            FftStart = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterFftTessStart", 60f, new ConfigDescription("With WaterFftTessFalloff: Vedr's waves keep their full detail closer than this. In metres. Range 10 to 500. Default 60.", new AcceptableValueRange<float>(10f, 500f)));
            FftEnd = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterFftTessEnd", 200f, new ConfigDescription("With WaterFftTessFalloff: water this far away and beyond gets the floor detail. In metres. Range 20 to 2000. Default 200.", new AcceptableValueRange<float>(20f, 2000f)));
            FftFloor = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterFftTessFloor", 4f, new ConfigDescription("With WaterFftTessFalloff: the least wave detail far water keeps (a subdivision factor; 1 = none). Range 1 to 16. Default 4.", new AcceptableValueRange<float>(1f, 16f)));
            FftFalloff.SettingChanged += (_, __) => PushFftFalloff(true); FftStart.SettingChanged += (_, __) => PushFftFalloff(false); FftEnd.SettingChanged += (_, __) => PushFftFalloff(false); FftFloor.SettingChanged += (_, __) => PushFftFalloff(false);
            PushFftFalloff(false);   // the Info line comes with the first world (beside WaterTessCut's), when VanillaDetail is bound and Vedr's flag is known
            // 1.2.396 ([visual], [lead]; FIRE: a toggle, default OFF, until he has hand-checked the look)
            VanillaDetail = EbmConfigMove.Bind(config, "41 - Water Performance", "WaterVanillaDistanceDetail", true /* 1.2.403: ON. DN leg 1.2.396 ((rig path)): off 3.369 / on 3.224 / off 3.365 ms, water shader off 3.252 (on = 0.028 ms under the game's water); Fire passed his water look with it on. A saved value is not changed */,
                "Needs [40 - Shaders] WaterShader = true. EBM's water gives its surface as much wave detail as the game's own water does: full detail within about 10 m of you, fading to none by about 40 m, instead of full detail at every distance. It also shares the game's screen copy for the see-through effect instead of making its own. Faster where water is far away or hidden under ice or ground. Applies live. Not used for the wave shape while Vedr's wave physics drives the water. On by default. Off = full wave detail at every distance and its own screen copy (slower).");
            VanillaDetail.SettingChanged += (_, __) => ApplyVanillaDetail("WaterVanillaDistanceDetail changed");
            // 1.2.426 ([visual], lead approved; Fire 11-16 "mesh splitting at the seams between water lod levels where it disagrees with the waves" + a pale strip at the far plane)
            SeamStencil = EbmConfigMove.Bind(config, "44 - Water - Surface & Reflection", "WaterSeamStencil", false /* 1.2.426: OFF = the game's own read-only stencil (full read: the tiles butt edge to edge, EbmWater.shader comment at the alpha block) */,
                "Needs [40 - Shaders] WaterShader = true. On: each water pixel is drawn by the first water tile that reaches it (FiresWater's old overlap guard); a nearer wave crest over a farther tile can then be cut off, a step along the tile border. Off (default): like the game's own water, the nearer tile draws over the farther one, so crests stay whole. Flip it to compare. Applies at once.");
            SeamStencil.SettingChanged += (_, __) => ApplySeamStencilAll("WaterSeamStencil changed");
            EdgeBlend = EbmConfigMove.Bind(config, "44 - Water - Surface & Reflection", "WaterEdgeBlend", false /* 1.2.426: OFF until Fire's look check (the far plane's own look is vanilla's, not read in full) */,
                "Needs [40 - Shaders] WaterShader = true. Where EBM's near water meets the game's distant water plane, the near waves ease to calm and take the full sea fog over the last stretch, so the two meet without a step or a pale strip. Off = the near water keeps full waves to its edge. Applies at once.");
            EdgeBand = EbmConfigMove.Bind(config, "44 - Water - Surface & Reflection", "WaterEdgeBlendBand", 48f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With WaterEdgeBlend: how many metres before the distant plane the blend starts. Range 8 to 200. Default 48.", new AcceptableValueRange<float>(8f, 200f)));
            EdgeBlend.SettingChanged += (_, __) => { s_edgeSent = new Vector4(-1f, 0f, 0f, 0f); PushEdge(true); };
            EdgeBand.SettingChanged += (_, __) => PushEdge(false);
            // 1.2.435 ([visual]; FIRE 11-17: a hard circle where the near water meets the game's far plane, seen from high above a bay). Default OFF until his look check.
            XFade = EbmConfigMove.Bind(config, "44 - Water - Surface & Reflection", "WaterEdgeCrossFade", false,
                "Needs [40 - Shaders] WaterShader = true. Over a wide band before the game's distant water plane begins, the distant plane starts drawing early under EBM's near water, the near waves ease to calm, and then the near water fades out onto the distant plane's own look, so at the edge the two are the same picture and the circle where they meet is gone. Centred where the game centres the distant plane's hole (the network reference point). Works on its own (WaterEdgeBlend not needed) and without FiresTossinShade. Off = as before. Applies at once.");
            XFadeBand = EbmConfigMove.Bind(config, "44 - Water - Surface & Reflection", "WaterEdgeCrossFadeBand", 160f, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With WaterEdgeCrossFade: how many metres before the distant plane's edge the waves start to calm; the near water fades out over the last half. Range 16 to 360. Default 160.", new AcceptableValueRange<float>(16f, 360f)));
            XFade.SettingChanged += (_, __) => { s_xfSay = true; };
            EbmWaterFarRing.Bind(config);   // 1.2.469 [44] WaterFarRing (+ radius / tess / detail range)
            XFadeBand.SettingChanged += (_, __) => { s_xfSay = true; };
            // 1.2.397 ([visual], [lead]; FIRE 11-15: our near water read green / tan beside the game's blue far water in the same frame). Default OFF until his look check.
            GameColour = EbmConfigMove.Bind(config, SecColour, "Game Water Colours", false /* 1.2.397: OFF until Fire's look check; off = exactly 1.2.396's colours */,
                "Needs [40 - Shaders] WaterShader = true. EBM's water takes its colours the way the game's own water does: the dark deep and shallow water colours everywhere, the bright top colour only on wave crests, and the sea bed seen in its own colour through the water instead of tinted by the water colour. Applies live. Off (the default) = EBM's own colour mix.");
            GameColour.SettingChanged += (_, __) => PushGameColour(true);
            PushGameColour(false);   // the global is set before any world
            WaveLit = EbmConfigMove.Bind(config, SecColour, "Water Lit By Waves", false /* 1.2.397: OFF until Fire's look check; off = exactly 1.2.396's lighting */,
                "Needs [40 - Shaders] WaterShader = true. The water's colour is lit wave by wave, as the game's own water is: the side of each ripple that faces the sun is brighter, the side that faces away is darker, and it takes the sky's light from its own direction. Makes the water read as a lit surface instead of a flat sheet. Applies live. Off (the default) = one light value for the whole water surface.");
            WaveLit.SettingChanged += (_, __) => PushWaveLit(true);
            PushWaveLit(false);
            // 1.2.399 ([visual], [lead]; FIRE 11-15: the deeper water read brown, "shouldnt realistic water get deeper blue as it gets deeper?"). Default OFF until his look check.
            DepthHue = EbmConfigMove.Bind(config, SecColour, "Depth Colour Shift", false /* 1.2.399: OFF until Fire's look check; off = exactly 1.2.398's haze */,
                "Needs [40 - Shaders] WaterShader = true. The haze in the water changes colour with the length of your view through it the way real water does: red light is lost first, then green, then blue, so clear shallows turn green-teal and then blue with depth (how fast each colour goes is Absorption Colour Loss). The haze also keeps the hue of Murk Colour in any sunlight instead of being tinted by the sun. The amount of haze is not raised. Applies live. Off (the default) = the haze takes all colours at once and is tinted by the sun.");
            DepthHue.SettingChanged += (_, __) => PushDepthHue(true);
            PushDepthHue(false);
            SkyBoost = EbmConfigMove.Bind(config, SecSurface, "Sky Reflection Boost", 0f /* 1.2.399: OFF (0) until Fire's look check */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. Deep water seen at a low angle shows more of the sky in it, so far water reads sky-blue toward the horizon, as on a real lake. Uses the sky reflection the water already has: no extra cost. Does not change shallow water or water you look straight down into. 0 = off (the default). 1 = full. Range 0 to 1.", new AcceptableValueRange<float>(0f, 1f)));
            SkyBoost.SettingChanged += (_, __) => PushSkyBoost(true);
            PushSkyBoost(false);
            // 1.2.400 ([visual], [lead]; FIRE 11-15: "Our waves in our near water look like s***"). Default OFF until his look check.
            GameRipples = EbmConfigMove.Bind(config, SecSurface, "Game Ripples", true /* 1.2.402: ON (Fire 11-15 on 1.2.400: "the ripples look good and should default on"); was false since 1.2.400; off = EBM's two ripple layers */,
                "Needs [40 - Shaders] WaterShader = true. Over deeper water the small ripples are drawn the way the game's own water draws them: the same ripple texture at the game's size (about four times finer than EBM's), layered three deep, lined up with the wind and drifting along it. Near water then carries the same ripple pattern as the far water, so the two meet without a change in pattern. Water 2 m deep or less keeps EBM's own ripples. EBM's waves, foam, glitter and rain rings stay. On by default. Off: EBM's own two ripple layers everywhere. Applies at once.");
            GameRipples.SettingChanged += (_, __) => PushGameRipples(true);
            // 1.2.407 ([visual], [lead]; FIRE 11-16: "water is still completely clear if there is no wave ... ZERO color looking straight down ... FIX THE SHADER"). ON by default (Fire's order).
            WaterBody = EbmConfigMove.Bind(config, SecColour, "Water Body By Depth", true /* 1.2.407: ON (Fire's order); off = 1.2.406's water exactly */,
                "Needs [40 - Shaders] WaterShader = true. The water gets its colour and how see-through it is from how much water you look through, at any angle, straight down included: a little water stays clear, deep water hides the bottom and shows the water's own colour. Off: the older colour terms (deep water could show the bottom and take the Murk Colour). Applies at once.");
            WaterBody.SettingChanged += (_, __) => PushWaterBody(true);
            WaterBodyMurk = EbmConfigMove.Bind(config, SecColour, "Water Body Murk Tint", 0.3f /* 1.2.407 */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With Water Body By Depth: how far the deep water's colour leans toward the Murk Colour. 0 = the game's own water colour only. 1 = the Murk Colour only. Applies at once.", new AcceptableValueRange<float>(0f, 1f)));
            WaterBodyMurk.SettingChanged += (_, __) => PushWaterBody(false);
            PushWaterBody(false);
            // 1.2.408 ([visual], [lead]; FIRE 11-16 on 1.2.407: "slightly less green", "it white washes anything it passes over", "churn ... too brown", "top color shouldnt be bright green")
            WaterTopColour = EbmConfigMove.Bind(config, SecColour, "Water Top Colour", new Color(0x61 / 255f, 0x95 / 255f, 0xA0 / 255f, 1f) /* 1.2.408: #6195A0 */,
                "Needs [40 - Shaders] WaterShader = true. The water's main near colour (the game's own is a bright mint green). Darker = the shallows look deeper and do not lighten what is under them. Set the alpha to 0 for the game's own colour. Applies at once. Default #6195A0.");
            WaterTopColour.SettingChanged += (_, __) => PushLook408();
            GlowOnlyOnWater = EbmConfigMove.Bind(config, SecColour, "Glow Only On Water", true /* 1.2.408 */,
                "Needs [40 - Shaders] WaterShader = true. The water's glow (Glow Strength, Sun Glow) only where the water's own colour covers what is under it, so things just under the surface are not lit up. Off: the glow everywhere, as before. Applies at once.");
            GlowOnlyOnWater.SettingChanged += (_, __) => PushLook408();
            ChurnFollowsWater = EbmConfigMove.Bind(config, SecColour, "Churn Follows Water Colour", 0.7f /* 1.2.408 */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. How much the stirred-up silt in shallow water takes the water's own colour instead of the Murk Colour (at the same brightness). 0 = the Murk Colour, as before. Applies at once.", new AcceptableValueRange<float>(0f, 1f)));
            ChurnFollowsWater.SettingChanged += (_, __) => PushLook408();
            PushLook408();
            // 1.2.409 ([visual], [lead]; FIRE 11-16 on 1.2.408: things under the water "should maintain something close to their original color ... just with a MINOR hue"). ON by default.
            ClearOptics = EbmConfigMove.Bind(config, SecColour, "Clear Water Optics", true /* 1.2.409: ON (Fire's words); off = 1.2.408's water exactly */,
                "Needs [40 - Shaders] WaterShader = true. Things under the water (you, fish, build pieces, the bottom) keep their own colour and are tinted only by the water between the surface and them: just under the surface = almost their own colour, deeper = more of the water's colour, deep water hides the bottom. Clear Water Depth and Opaque Water Depth set how fast. Off: the older look (a fixed water-colour layer over anything under about 0.35 m of water). Applies at once.");
            ClearOptics.SettingChanged += (_, __) => PushClearOptics();
            // 1.2.410 ([visual], [lead]; FIRE 11-16: "everything in the distance gets covered in blue fog?" with the camera at the waterline). ON by default.
            SelfSubmerged = EbmConfigMove.Bind(config, SecSurface, "Underwater View Follows Waves", true /* 1.2.410: ON; off = 1.2.409 exactly */,
                "Needs [40 - Shaders] WaterShader = true. When your view dips under a wave, the water is drawn as seen from under the surface (the world stays its own colour near the surface) instead of a flat water-coloured sheet over half the screen. Off: the older behaviour. Applies at once.");
            SelfSubmerged.SettingChanged += (_, __) => PushSelfSubmerged();
            // 1.2.411 ([visual], [lead]; FIRE 11-16: "the water itself should properly be applying opacity and hue based off depth and that alone"). All ON; off = 1.2.410.
            ClearOneBody = EbmConfigMove.Bind(config, SecColour, "Clear Water One Body Colour", true /* 1.2.411 */,
                "Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: the water has one colour (its deep colour) and how much of it you see comes only from how much water you look through. Off: shallow water also changed colour by its depth (the older mix). Applies at once.");
            ClearNoTint = EbmConfigMove.Bind(config, SecColour, "Clear Water No Surface Tint", true /* 1.2.411 */,
                "Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: no flat grey tint over everything seen through the water. Off: the older tint. Applies at once.");
            ClearFoamRaw = EbmConfigMove.Bind(config, SecColour, "Clear Water Shore Foam By Real Depth", true /* 1.2.411 */,
                "Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: shore foam follows the real water depth (no foam on a dry-looking sandbar). Off: the older foam depth. Applies at once.");
            ClearEdgeGuard = EbmConfigMove.Bind(config, SecColour, "Clear Water Edge Guard", true /* 1.2.411 */,
                "Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: no bright outline on things under the water from the ripple distortion. Off: the older distortion. Applies at once.");
            CamUnderOnce = EbmConfigMove.Bind(config, SecSurface, "Underwater Check Once Per Frame", true /* 1.2.411 */,
                "Needs [40 - Shaders] WaterShader = true. With Underwater View Follows Waves: whether your view is under a wave is decided once a frame from the game's own water level (cheaper). Off: checked per pixel on the screen. Applies at once.");
            ClearOneBody.SettingChanged += (_, __) => PushLook411();
            ClearNoTint.SettingChanged += (_, __) => PushLook411();
            ClearFoamRaw.SettingChanged += (_, __) => PushLook411();
            ClearEdgeGuard.SettingChanged += (_, __) => PushLook411();
            CamUnderOnce.SettingChanged += (_, __) => PushLook411();
            // 1.2.412 ([visual], [lead]; FIRE 11-16: "our color should have stayed nearly identical to vanilla" / "not change the entire color of the water"). ON.
            VanillaColour = EbmConfigMove.Bind(config, SecColour, "Vanilla Water Colour", true /* 1.2.412: ON (Fire's order); off = 1.2.411's colour keys apply */,
                "Needs [40 - Shaders] WaterShader = true. The water keeps the game's own colour, near and far, so it matches the game's distant water. Our extras stay (ripples, clearer shallows by depth, foam and churn, reflections, the underwater view). While this is on, these keys do nothing: Game Water Colours, Water Top Colour, Clear Water One Body Colour, Water Body Murk Tint, Murk (its tint), Water Body Light Limit, the Glow keys, Absorption Colour Loss (its hue), Minimum Water Depth For Colour, Reflection Strength, Physical Reflection Mix, Sea Fog On Water. Applies at once.");
            VanillaColour.SettingChanged += (_, __) => PushVanillaColour();
            // 1.2.413 ([visual], [lead]: the water lit as the game lights its water). ON; acts with Vanilla Water Colour + Clear Water Optics.
            GameLighting = EbmConfigMove.Bind(config, SecColour, "Game Lighting", true /* 1.2.413: ON; off = 1.2.412 exactly */,
                "Needs [40 - Shaders] WaterShader = true. With Vanilla Water Colour: the water is lit the way the game lights its own water (the sun and sky on every wave, the game's reflections and sun highlight), so near water and the game's distant water look the same. Applies at once.");
            GameLighting.SettingChanged += (_, __) => PushGameLighting();
            PushGameLighting();
            PushVanillaColour();
            PushLook411();
            PushSelfSubmerged();
            ClearDepth = EbmConfigMove.Bind(config, SecColour, "Clear Water Depth", 2.5f /* 1.2.409 */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: about this much water (along your view) stays nearly clear; past it the water's colour builds up. Higher = clearer shallows. 0 = the colour builds up from the first centimetre. In metres. Applies at once.", new AcceptableValueRange<float>(0f, 10f)));
            ClearDepth.SettingChanged += (_, __) => PushClearOptics();
            OpaqueDepth = EbmConfigMove.Bind(config, SecColour, "Opaque Water Depth", 6f /* 1.2.409 */, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. With Clear Water Optics: by this much water (along your view) the water hides what is under it (about 95 %). Lower = deep water turns solid sooner. In metres. Applies at once.", new AcceptableValueRange<float>(0.5f, 60f)));
            OpaqueDepth.SettingChanged += (_, __) => PushClearOptics();
            PushClearOptics();
            PushGameRipples(false);
        }

        // ── 1.2.426 WaterSeamStencil + WaterEdgeBlend ──
        internal static ConfigEntry<bool> SeamStencil, EdgeBlend;
        internal static ConfigEntry<float> EdgeBand;
        private static readonly int Prop_StencilWrite = Shader.PropertyToID("_FiresStencilWriteMask"), IdEdge = Shader.PropertyToID("_EbmWaterEdge");
        // the stencil write mask is render state: a MATERIAL value (EbmWater.shader WriteMask [_FiresStencilWriteMask]); 16 = the old first-tile-claims guard, 0 = read only (the
        // game's own water), the boat-interior bit (ReadMask 18) is tested either way
        private static void ApplySeamStencil(Material mat)
        {
            if (mat != null && mat.HasProperty(Prop_StencilWrite)) mat.SetFloat(Prop_StencilWrite, SeamStencil != null && SeamStencil.Value ? 16f : 0f);
        }
        private static void ApplySeamStencilAll(string why)
        {
            var ours = Indirect.EbmIndirectAssets.WaterShader;
            int n = 0;
            var list = WaterVolume.Instances;
            if (ours != null && list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    var mat = v.m_waterSurface.sharedMaterial;
                    if (mat == null || mat.shader != ours) continue;
                    ApplySeamStencil(mat); n++;
                }
            foreach (var kv in s_swapped) if (kv.Key != null) ApplySeamStencil(kv.Key);   // prefab materials too, so tiles born later carry it
            foreach (var m in EbmFatWaterLook.Built) if (m != null) ApplySeamStencil(m);   // 1.2.437: FAT's water wearing our copies
            EasyBakeLog.Info($"[Water] WaterSeamStencil {(SeamStencil.Value ? "on: first tile claims each pixel (old overlap guard)" : "off: read-only stencil as the game's water, nearer tiles draw over farther ones")} ({why}); {n} tile(s) set");
        }
        private static Vector4 s_edgeSent = new Vector4(-1f, 0f, 0f, 0f);
        // the far plane's hole radius, as vanilla Water.ApplySettings computes it (Water.cs: synced near simulation distance x zone size); sent when it changes
        private static void PushEdge(bool say)
        {
            float r = 0f;
            if (ZNet.instance != null && ZoneSystem.instance != null) r = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance * ZoneSystem.instance.m_zoneSize;
            bool on = EdgeBlend != null && EdgeBlend.Value && r > 1f && !EbmWaterFarRing.Drawing;   // 1.2.469: no plane edge to ease into while the far ring draws
            var v = on ? new Vector4(r, EdgeBand != null ? Mathf.Clamp(EdgeBand.Value, 8f, 200f) : 48f, 1f, 0f) : Vector4.zero;
            if (v == s_edgeSent) return;
            s_edgeSent = v;
            Shader.SetGlobalVector(IdEdge, v);
            if (say || on) EasyBakeLog.Info(on ? $"[Water] WaterEdgeBlend on: near waves ease to calm and full fog over the last {v.y:0} m before the distant plane's edge at {r:0} m" : "[Water] WaterEdgeBlend off: near water keeps full waves to its edge");
        }

        // ── 1.2.435 / 1.2.436 WaterEdgeCrossFade ──
        // _EbmWaterXFade = (centre x, centre z, band m, hole radius r m; all 0 = off). Centre = ZNet.GetReferencePosition(), the value vanilla EnvMan sends as _NetRefPos (EnvMan.cs:321),
        // on which Custom/Water centres the far plane's hole (VanillaShaders/Water.shader:1386-1388 + :1518 depth-pass discard; :9397-9398 the colour pass's alpha ramp over the last
        // 20 m inside _VisibleMaxDistance). r = Water.ApplySettings' _VisibleMaxDistance (Water.cs:35-37), computed here, never read back from the plane.
        // 1.2.436: the far plane's own hole is moved in to r - band/2 (its _VisibleMaxDistance, on its own property block as Water.cs:36-38 does), so it draws its full final
        // look under the near water there; EbmWater.shader fades the near water's alpha to 0 over [r - band/2, r] (it draws after the plane: Queue Transparent-99 vs 2900), so
        // at r the screen shows exactly the far plane's output. Off = the plane's vanilla value back (Water.ApplySettings()). Nothing here depends on another mod.
        internal static ConfigEntry<bool> XFade;
        internal static ConfigEntry<float> XFadeBand;
        private static readonly int IdXFade = Shader.PropertyToID("_EbmWaterXFade"), IdVisMax = Shader.PropertyToID("_VisibleMaxDistance");
        private static Vector4 s_xfSent = new Vector4(-1f, 0f, 0f, 0f);
        private static bool s_xfSay = true, s_xfGateSaid;
        private static string s_xfState;
        private static float s_xfPlaneAt, s_xfPlaneHole = -1f;
        private static MaterialPropertyBlock s_xfMpb;
        private static void PushXFade()
        {
            float r = 0f;
            bool want = XFade != null && XFade.Value && Wanted;   // 1.2.444 (Fire rule 11-18: a feature that needs EBM's own shader is gated by that shader's key): needs [Optimize] WaterShader = true
            if (want && ZNet.instance != null && ZoneSystem.instance != null) r = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance * ZoneSystem.instance.m_zoneSize;
            bool on = want && r > 1f && !EbmWaterFarRing.Drawing;   // 1.2.469: stands down while the far ring draws (the plane is hidden)
            if (XFade != null && XFade.Value && !Wanted && !s_xfGateSaid) { s_xfGateSaid = true; EasyBakeLog.Info("[Water] WaterEdgeCrossFade stands down: it needs [40 - Shaders] WaterShader = true (the game's own water is left as it is)"); }
            float band = XFadeBand != null ? Mathf.Clamp(XFadeBand.Value, 16f, 360f) : 160f;
            Vector4 v = Vector4.zero;
            if (on) { Vector3 c = ZNet.instance.GetReferencePosition(); v = new Vector4(c.x, c.z, band, r); }
            if (v != s_xfSent) { s_xfSent = v; Shader.SetGlobalVector(IdXFade, v); }   // every frame while on: the reference point moves; sent only when it changed
            float hole = on ? Mathf.Max(1f, r - band * 0.5f) : 0f;
            // the far plane's hole: once a second (vanilla's ApplySettings writes r back on OnEnable / a synced distance change), and at once on a change here
            if (hole != s_xfPlaneHole || Time.unscaledTime >= s_xfPlaneAt) { s_xfPlaneAt = Time.unscaledTime + 1f; ApplyXFadeHole(hole); }
            string state = on ? $"on|{band:0}|{r:0}" : (want ? "wait" : "off");
            if (s_xfSay || state != s_xfState)
            {
                s_xfSay = false; s_xfState = state;
                EasyBakeLog.Info(on ? $"[Water] WaterEdgeCrossFade on: the distant plane draws from {hole:0} m (its hole moved in from {r:0} m), the near water's waves calm over {r - band:0}-{hole:0} m and it fades out over {hole:0}-{r:0} m onto the distant plane's own look, centred on the network reference point"
                    : want ? "[Water] WaterEdgeCrossFade waiting: no world yet (the distant plane's edge is not known)" : "[Water] WaterEdgeCrossFade off: near water and the distant plane as the game has them");
            }
        }
        private static void ApplyXFadeHole(float hole)   // hole 0 = give the plane its vanilla value back
        {
            var list = Water.Instances;
            if (list == null) return;
            if (s_xfMpb == null) s_xfMpb = new MaterialPropertyBlock();
            bool restore = hole <= 0f;
            if (restore && s_xfPlaneHole <= 0f) return;   // never touched it
            if (restore && (ZNet.instance == null || ZoneSystem.instance == null)) { s_xfPlaneHole = hole; return; }   // ApplySettings reads both (Water.cs:33-34); a new world re-applies on its own (Awake / OnEnable)
            // [visual] review: the fade needs the near water to draw AFTER the plane (2900). A swapped material keeps a custom renderQueue copied from the vanilla
            // material if it had one (UNCONFIRMED whether it does), so while on, any swapped zone material below 2901 is lifted to its shader's queue.
            if (!restore)
                foreach (var kv in s_swapped)
                    if (kv.Key != null && kv.Key.shader != null && kv.Key.renderQueue < 2901) kv.Key.renderQueue = kv.Key.shader.renderQueue;
            for (int i = 0; i < list.Count; i++)
            {
                var w = list[i];
                if (w == null) continue;
                if (restore) { w.ApplySettings(); continue; }
                var mr = w.GetComponent<MeshRenderer>();
                if (mr == null) continue;
                mr.GetPropertyBlock(s_xfMpb);
                if (s_xfMpb.GetFloat(IdVisMax) != hole) { s_xfMpb.SetFloat(IdVisMax, hole); mr.SetPropertyBlock(s_xfMpb); }
            }
            s_xfPlaneHole = hole;
        }

        // ── 1.2.397 Game Water Colours ──
        internal static ConfigEntry<bool> GameColour;
        private static readonly int IdGameColour = Shader.PropertyToID("_EbmWaterGameColour");
        private static void PushGameColour(bool say)
        {
            if (GameColour == null) return;
            bool on = GameColour.Value;
            Shader.SetGlobalFloat(IdGameColour, on ? 1f : 0f);
            if (say) EasyBakeLog.Info(on ? "[Water] Game Water Colours on: the body colour mix and the see-through bed are computed as the game's own water does" : "[Water] Game Water Colours off: EBM's own colour mix, as before");
        }
        internal static ConfigEntry<bool> WaterBody;
        internal static ConfigEntry<float> WaterBodyMurk;
        private static readonly int IdWaterBody = Shader.PropertyToID("_EbmWaterBody"), IdWaterBodyMurk = Shader.PropertyToID("_EbmWaterBodyMurk");
        private static void PushWaterBody(bool say)
        {
            if (WaterBody == null) return;
            bool on = WaterBody.Value;
            Shader.SetGlobalFloat(IdWaterBody, on ? 1f : 0f);
            Shader.SetGlobalFloat(IdWaterBodyMurk, WaterBodyMurk != null ? Mathf.Clamp01(WaterBodyMurk.Value) : 0.3f);
            if (say) EasyBakeLog.Info(on ? "[Water] Water Body By Depth on: the water's colour and opacity come from how much water you look through, at any angle" : "[Water] Water Body By Depth off: the older colour terms");
        }
        internal static ConfigEntry<Color> WaterTopColour;
        internal static ConfigEntry<bool> GlowOnlyOnWater;
        internal static ConfigEntry<float> ChurnFollowsWater;
        private static readonly int IdTopColour = Shader.PropertyToID("_EbmTopColour"), IdGlowCover = Shader.PropertyToID("_EbmGlowCover"), IdChurnHue = Shader.PropertyToID("_EbmChurnHue");
        private static void PushLook408()
        {
            if (WaterTopColour != null)
            {
                Color c = WaterTopColour.Value;
                Color lin = QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;   // the key is entered like any colour (sRGB hex); pushed as a vector so Unity never converts it a second time
                Shader.SetGlobalVector(IdTopColour, new Vector4(lin.r, lin.g, lin.b, c.a > 0.5f ? 1f : 0f));
            }
            Shader.SetGlobalFloat(IdGlowCover, GlowOnlyOnWater != null && GlowOnlyOnWater.Value ? 1f : 0f);
            Shader.SetGlobalFloat(IdChurnHue, ChurnFollowsWater != null ? Mathf.Clamp01(ChurnFollowsWater.Value) : 0f);
        }
        // 1.2.408 ([lead], for Core's caustics pass, [generator]): _FiresWaterViewDepth = metres of view path through EBM's water at which the water hides ~95 % of the bed
        // (Shallow Water Colour Distance: the 1.2.407 body keeps exp(-3) of its slowest colour there). 0 = EBM's water body is not drawing (WaterShader off, no tile swapped,
        // Water Body By Depth off, or the distance 0). A reader fades by exp(-3 x pathM / value) when value > 0, and changes nothing when it is 0.
        private static readonly int IdViewDepth = Shader.PropertyToID("_FiresWaterViewDepth");
        private static float s_viewDepthSent = -1f;
        private static void PushViewDepth()
        {
            float d = 0f;
            if (Wanted && s_swapped.Count > 0 && WaterBody != null && WaterBody.Value && s_lookF.TryGetValue("_FiresShallowBodyM", out var e) && e.Value > 0.001f) d = Mathf.Max(e.Value, 1f);
            if (Wanted && s_swapped.Count > 0 && ClearOptics != null && ClearOptics.Value && OpaqueDepth != null && s_lookF.TryGetValue("_FiresShallowBodyM", out var e2) && e2.Value > 0.001f) d = Mathf.Clamp(OpaqueDepth.Value, 0.5f, 60f);   // 1.2.409: with Clear Water Optics the water hides the bed by Opaque Water Depth (same e^-3 meaning)
            if (d != s_viewDepthSent) { s_viewDepthSent = d; Shader.SetGlobalFloat(IdViewDepth, d); }
        }
        internal static ConfigEntry<bool> GameLighting;
        private static readonly int IdGameLighting = Shader.PropertyToID("_EbmGameLighting");
        private static void PushGameLighting() => Shader.SetGlobalFloat(IdGameLighting, GameLighting != null && GameLighting.Value ? 1f : 0f);
        internal static ConfigEntry<bool> VanillaColour;
        private static readonly int IdVanillaColour = Shader.PropertyToID("_EbmVanillaColour");
        private static void PushVanillaColour() => Shader.SetGlobalFloat(IdVanillaColour, VanillaColour != null && VanillaColour.Value ? 1f : 0f);
        internal static ConfigEntry<bool> ClearOneBody, ClearNoTint, ClearFoamRaw, ClearEdgeGuard, CamUnderOnce;
        private static readonly int IdClearOneBody = Shader.PropertyToID("_EbmClearOneBody"), IdClearNoTint = Shader.PropertyToID("_EbmClearNoTint"),
            IdClearFoamRaw = Shader.PropertyToID("_EbmClearFoamRaw"), IdClearEdgeGuard = Shader.PropertyToID("_EbmClearEdgeGuard"),
            IdCamUnderSet = Shader.PropertyToID("_EbmCamUnderSet"), IdCamUnder = Shader.PropertyToID("_EbmCamUnder");
        private static bool On411(ConfigEntry<bool> e) => e != null && e.Value;
        private static void PushLook411()
        {
            Shader.SetGlobalFloat(IdClearOneBody, On411(ClearOneBody) ? 1f : 0f);
            Shader.SetGlobalFloat(IdClearNoTint, On411(ClearNoTint) ? 1f : 0f);
            Shader.SetGlobalFloat(IdClearFoamRaw, On411(ClearFoamRaw) ? 1f : 0f);
            Shader.SetGlobalFloat(IdClearEdgeGuard, On411(ClearEdgeGuard) ? 1f : 0f);
            Shader.SetGlobalFloat(IdCamUnderSet, On411(CamUnderOnce) ? 1f : 0f);
            s_camUnderSent = -1f;   // resend on the next frame
        }
        // 1.2.411 ([seasons] 1.2.410 review): the camera-under-the-wave test once a frame, from vanilla's own water level at the camera (the same call as Core
        // CompanionExits.cs:133); sent only when it changes.
        private static float s_camUnderSent = -1f;
        private static void PushCamUnder()
        {
            float u = 0f;
            if (Wanted && On411(CamUnderOnce) && On411(SelfSubmerged))
            {
                var cam = Utils.GetMainCamera();
                if (cam != null)
                {
                    Vector3 p = cam.transform.position;
                    float level = Floating.GetLiquidLevel(p, 1f, LiquidType.Water);
                    if (p.y < level) u = 1f;
                }
            }
            if (u != s_camUnderSent) { s_camUnderSent = u; Shader.SetGlobalFloat(IdCamUnder, u); }
        }
        internal static ConfigEntry<bool> SelfSubmerged;
        private static readonly int IdSelfSubmerged = Shader.PropertyToID("_EbmSelfSubmerged");
        private static void PushSelfSubmerged() => Shader.SetGlobalFloat(IdSelfSubmerged, SelfSubmerged != null && SelfSubmerged.Value ? 1f : 0f);
        internal static ConfigEntry<bool> ClearOptics;
        internal static ConfigEntry<float> ClearDepth, OpaqueDepth;
        private static readonly int IdClearOptics = Shader.PropertyToID("_EbmClearOptics"), IdClearDepth = Shader.PropertyToID("_EbmClearDepth"), IdOpaqueDepth = Shader.PropertyToID("_EbmOpaqueDepth");
        private static void PushClearOptics()
        {
            Shader.SetGlobalFloat(IdClearOptics, ClearOptics != null && ClearOptics.Value ? 1f : 0f);
            Shader.SetGlobalFloat(IdClearDepth, ClearDepth != null ? Mathf.Clamp(ClearDepth.Value, 0f, 10f) : 2.5f);
            Shader.SetGlobalFloat(IdOpaqueDepth, OpaqueDepth != null ? Mathf.Clamp(OpaqueDepth.Value, 0.5f, 60f) : 6f);
        }
        internal static ConfigEntry<bool> GameRipples;
        private static readonly int IdGameRipples = Shader.PropertyToID("_EbmWaterGameRipples");
        private static void PushGameRipples(bool say)
        {
            if (GameRipples == null) return;
            bool on = GameRipples.Value;
            Shader.SetGlobalFloat(IdGameRipples, on ? 1f : 0f);
            if (say) EasyBakeLog.Info(on ? "[Water] Game Ripples on: over deeper water the ripple normal is built as the game's own water builds it (three layers of its ripple texture at the game's scale, along the wind)" : "[Water] Game Ripples off: EBM's own two ripple layers");
        }
        internal static ConfigEntry<float> SkyBoost;
        private static readonly int IdSkyBoost = Shader.PropertyToID("_EbmWaterSkyBoost");
        private static float s_skySaidAt = -1f;
        private static void PushSkyBoost(bool say)
        {
            if (SkyBoost == null) return;
            float v = Mathf.Clamp01(SkyBoost.Value);
            Shader.SetGlobalFloat(IdSkyBoost, v);
            if (say && Time.unscaledTime - s_skySaidAt > 2f) { s_skySaidAt = Time.unscaledTime; EasyBakeLog.Info($"[Water] Sky Reflection Boost {v:0.##}: {(v > 0f ? "deep water at low angles takes more of the sky reflection" : "off")}"); }   // a slider drag fires this per step: one line every 2 s at most
        }
        internal static ConfigEntry<bool> DepthHue;
        private static readonly int IdDepthHue = Shader.PropertyToID("_EbmWaterDepthHue");
        private static void PushDepthHue(bool say)
        {
            if (DepthHue == null) return;
            bool on = DepthHue.Value;
            Shader.SetGlobalFloat(IdDepthHue, on ? 1f : 0f);
            if (say) EasyBakeLog.Info(on ? "[Water] Depth Colour Shift on: the haze takes red first, then green, then blue, and is lit by the light's brightness only" : "[Water] Depth Colour Shift off: the haze takes all colours at once and is tinted by the sun, as before");
        }
        internal static ConfigEntry<bool> WaveLit;
        private static readonly int IdWaveLit = Shader.PropertyToID("_EbmWaterWaveLit");
        private static void PushWaveLit(bool say)
        {
            if (WaveLit == null) return;
            bool on = WaveLit.Value;
            Shader.SetGlobalFloat(IdWaveLit, on ? 1f : 0f);
            if (say) EasyBakeLog.Info(on ? "[Water] Water Lit By Waves on: the water's colour is lit at each pixel's wave normal" : "[Water] Water Lit By Waves off: one light value for the whole water surface, as before");
        }

        // ── 1.2.396 WaterVanillaDistanceDetail ──
        // EBM/Water holds two SubShaders: the first (LOD 200) is 1.2.395's, the second (LOD 100) has vanilla's tessellation distance rule, fractional_odd
        // partitioning and vanilla's grab name. Unity uses the first SubShader whose LOD is not above the shader's maximumLOD, so lowering it to 100 picks the
        // second, and putting the value back picks the first again. Nothing else is touched; with the key off the shader object is left as Unity made it.
        internal static ConfigEntry<bool> VanillaDetail;
        private const int VanillaDetailLod = 100;
        private static Shader s_vdShader; private static int s_vdOrigLod; private static int s_vdApplied = -1;   // -1 = not applied to this shader object yet, 0 = off, 1 = on
        private static void ApplyVanillaDetail(string why)
        {
            var sh = Indirect.EbmIndirectAssets.WaterShader;
            if (sh == null || VanillaDetail == null) return;
            if (!ReferenceEquals(sh, s_vdShader))   // a new shader object: remember the value Unity gave it
            {
                if (s_vdApplied == 1 && s_vdShader != null) s_vdShader.maximumLOD = s_vdOrigLod;   // [seasons]: the old object gets its own value back first, so if it is ever handed out again its limit is not read as 100
                s_vdShader = sh; s_vdOrigLod = sh.maximumLOD; s_vdApplied = -1;
            }
            int want = VanillaDetail.Value ? 1 : 0;
            if (want != s_vdApplied)
            {
                bool say = want == 1 || s_vdApplied == 1;   // off at the start is the default state: the start line below covers it
                if (want == 1) sh.maximumLOD = VanillaDetailLod; else if (s_vdApplied == 1) sh.maximumLOD = s_vdOrigLod;
                s_vdApplied = want;
                if (say) EasyBakeLog.Info(want == 1
                    ? $"[Water] WaterVanillaDistanceDetail on ({why}): EBM/Water draws with the game's own distance rule (full wave detail within 10 m, none past about 40 m, blended) and shares the game's screen copy (shader LOD limit {s_vdOrigLod} -> {VanillaDetailLod})"
                    : $"[Water] WaterVanillaDistanceDetail off ({why}): EBM/Water as before (full wave detail at every distance, its own screen copy; shader LOD limit back to {s_vdOrigLod})");
            }
            // [lead]'s guard: with the key OFF the first SubShader (LOD 200) must be the one Unity picks. A limit under 200 on this shader or on all shaders
            // (a quality preset, another mod) would make Unity draw the LOD 100 SubShader without the key: said once at the start, warned whenever it is so.
            int shLod = sh.maximumLOD, glLod = Shader.globalMaximumLOD;
            if (!s_vdSaidStart) { s_vdSaidStart = true; EasyBakeLog.Info($"[Water] EBM/Water shader LOD limits at the start: this shader {s_vdOrigLod} as loaded, all shaders {glLod}; WaterVanillaDistanceDetail {(want == 1 ? "on" : "off")} (off needs both limits at 200 or more, or unlimited)"); }
            if (want == 1) { s_vdLow = false; return; }   // [seasons]: with the key on the shader's limit is 100 by our own hand; nothing to say (and no "back" line)
            bool low = (shLod >= 0 && shLod < 200) || (glLod >= 0 && glLod < 200);
            if (low != s_vdLow) { s_vdLow = low; if (low) EasyBakeLog.Warn($"[Water] a shader LOD limit is under 200 (this shader {shLod}, all shaders {glLod}) while WaterVanillaDistanceDetail is OFF: Unity now draws EBM/Water with its vanilla-detail SubShader (or its fallback) although the key is off. Something else lowered the limit; EBM does not change it back."); else EasyBakeLog.Info($"[Water] the shader LOD limits are back at 200 or more (this shader {shLod}, all shaders {glLod}): EBM/Water follows the WaterVanillaDistanceDetail key again"); }
        }
        private static bool s_vdSaidStart, s_vdLow;

        // ── 1.2.391 WaterTessCut ──
        internal static ConfigEntry<bool> TessCut;
        internal static ConfigEntry<float> TessFarStart, TessFarEnd;

        // ── 1.2.475 WaterFftTessFalloff: the global EbmWater.shader's hsConstantVD reads (_EbmFftTessFar = on, start m, end m, floor) ──
        internal static ConfigEntry<bool> FftFalloff;
        internal static ConfigEntry<float> FftStart, FftEnd, FftFloor;
        private static readonly int IdFftTessFar = Shader.PropertyToID("_EbmFftTessFar");
        private static void PushFftFalloff(bool say)
        {
            if (FftFalloff == null || FftStart == null || FftEnd == null || FftFloor == null) return;
            bool on = FftFalloff.Value;
            float s = Mathf.Max(0f, FftStart.Value), e = Mathf.Max(s + 1f, FftEnd.Value), fl = Mathf.Clamp(FftFloor.Value, 1f, 16f);
            Shader.SetGlobalVector(IdFftTessFar, on ? new Vector4(1f, s, e, fl) : Vector4.zero);
            if (say) EasyBakeLog.Info(on
                ? $"[Water] WaterFftTessFalloff on (1.2.475): while Vedr's waves drive the water, zone tiles keep the full detail within {s:0} m and fall to factor {fl:0} by {e:0} m (needs WaterVanillaDistanceDetail on: {(VanillaDetail != null && VanillaDetail.Value ? "yes" : "NO, no effect")}); Vedr's waves {(Shader.GetGlobalFloat(IdVedrFftOn) > 0.5f ? "on now" : "off now")}"
                : "[Water] WaterFftTessFalloff off: while Vedr's waves drive the water, every zone tile keeps the full detail at every distance (as before 1.2.475)");
        }
        private static readonly int IdTessCut = Shader.PropertyToID("_EbmWaterTessCut");
        // How far the domain stage can move a vertex ([seasons] review): BuildOutput adds CalcWave x _WaveAmplitude (shader Range 0..2, default 1; the vanilla crest is at
        // most CrestPerDepth = 18.64 m), the tide and whirl offsets (0 / downward without their feeders) and, with Vedr's water physics, an FFT displacement that also
        // moves sideways and has no fixed bound: the shader skips the cut while _VedrFftOn is set ([2ic] review). Twice the vanilla crest + 5 m covers the amplitude range.
        private const float TessCullMargin = WaterHideBuried.CrestPerDepth * 2f + 5f;

        private static readonly int IdVedrFftOn = Shader.PropertyToID("_VedrFftOn");
        private static string s_tessSayWhy;   // why the next WaterTessCut line is due

        // A key changed ([2ic] review): the global at once; the Info line once, 1 s after the last change (a slider drag fires this for every step, and each
        // Info line is a disk write on the main thread). The line is written by Tick's once-a-second pass, in a world.
        private static void TessChanged(string why)
        {
            PushTessCut(null);
            s_tessSayWhy = why;
            s_tessSayAt = Time.unscaledTime + 1f;
        }

        // when == null: the global only. Otherwise also one Info line with the tile counts by distance from the camera now.
        private static void PushTessCut(string when)
        {
            if (TessCut == null || TessFarStart == null || TessFarEnd == null) return;
            bool on = TessCut.Value;
            float start = Mathf.Max(0f, TessFarStart.Value), end = Mathf.Max(0f, TessFarEnd.Value);
            Shader.SetGlobalVector(IdTessCut, on ? new Vector4(1f, TessCullMargin, start, end) : Vector4.zero);
            if (when == null) return;
            var ours = Indirect.EbmIndirectAssets.WaterShader;
            var cam = Utils.GetMainCamera();
            int tiles = 0, near = 0, mid = 0, far = 0;
            var list = WaterVolume.Instances;
            if (ours != null && list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    var mat = v.m_waterSurface.sharedMaterial;
                    if (mat == null || mat.shader != ours) continue;
                    tiles++;
                    if (cam == null) continue;
                    float d = Vector3.Distance(cam.transform.position, v.m_waterSurface.bounds.ClosestPoint(cam.transform.position));
                    if (d <= start) near++; else if (end > start && d >= end) far++; else mid++;
                }
            bool fft = Shader.GetGlobalFloat(IdVedrFftOn) > 0.5f;
            EasyBakeLog.Info(on
                ? $"[Water] WaterTessCut on ({when}): water patches outside the view by more than {TessCullMargin:0} m are not tessellated; "
                  + (end > start ? $"full detail to {start:0} m, lowest by {end:0} m; {tiles} water tile(s) EBM put on EBM/Water now: {near} within {start:0} m, {mid} between, {far} past {end:0} m"
                                 : $"full detail at every distance (WaterTessFarEnd is not past WaterTessFarStart); {tiles} water tile(s) EBM put on EBM/Water now")
                  + " (the cut applies to every patch drawn with EBM/Water)"
                  + (fft ? "; Vedr's wave physics is on now: the cut is SKIPPED while it is (its waves have no fixed height bound)" : "; skipped whenever Vedr's wave physics drives the water")
                : $"[Water] WaterTessCut off ({when}): every patch of the {tiles} zone tile(s) on EBM/Water is tessellated at the full factor, as before");
        }

        // ── 1.2.322 rain rings + wind drift ──
        internal static ConfigEntry<bool> RainOn, WindDrift;
        internal static ConfigEntry<float> RainSize, RainStrength, RainUnder, RainFade;
        private static void FiresEasyBakeMeshesPlugin_AddBasicRain() => FiresEasyBakeMeshes.PlayerFacingConfig.AddBasic(SecWaves, "Rain Rings");
        private static readonly int P_Rain = Shader.PropertyToID("_FiresRainRipples"), P_RainSize = Shader.PropertyToID("_FiresRainRippleSize"),
            P_RainStrength = Shader.PropertyToID("_FiresRainRippleStrength"), P_RainUnder = Shader.PropertyToID("_FiresRainRippleUnder"),
            P_RainFade = Shader.PropertyToID("_FiresRainRippleFade"), P_GlobalWind = Shader.PropertyToID("_GlobalWind1"),
            P_Travel = Shader.PropertyToID("_FiresWindTravel"), P_TravelOn = Shader.PropertyToID("_FiresWindTravelOn");
        private static float s_nextRain, s_rain = -1f, s_travelOn = -1f; private static Vector2 s_travel;
        private static void Weather()
        {
            // wind drift: per frame (it sums the wind direction over time)
            bool drift = WindDrift != null && WindDrift.Value;
            float on = drift ? 1f : 0f;
            if (on != s_travelOn) { s_travelOn = on; Shader.SetGlobalFloat(P_TravelOn, on); }
            if (drift)
            {
                Vector4 w = Shader.GetGlobalVector(P_GlobalWind);
                var dir = new Vector2(w.x, w.z);
                if (dir.sqrMagnitude > 1e-8f) { dir.Normalize(); s_travel += dir * Time.deltaTime; Shader.SetGlobalVector(P_Travel, new Vector4(s_travel.x, s_travel.y, 0f, 0f)); }
            }
            // rain rings: twice a second, eased toward the weather
            if (Time.unscaledTime < s_nextRain) return;
            s_nextRain = Time.unscaledTime + 0.5f;
            bool rainOn = RainOn != null && RainOn.Value;
            EnvSetup env = rainOn && EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null;
            float target = rainOn && env != null && env.m_isWet ? Mathf.Clamp01(0.35f + 0.65f * env.m_rainCloudAlpha) : 0f;
            float rain = s_rain < 0f ? target : Mathf.MoveTowards(s_rain, target, 0.15f);
            if (rain != s_rain) { s_rain = rain; Shader.SetGlobalFloat(P_Rain, rain); }
            Shader.SetGlobalFloat(P_RainSize, Mathf.Max(0.2f, RainSize.Value));
            Shader.SetGlobalFloat(P_RainStrength, RainStrength.Value);
            Shader.SetGlobalFloat(P_RainUnder, RainUnder.Value);
            Shader.SetGlobalFloat(P_RainFade, Mathf.Max(1f, RainFade.Value));
        }

        // 1.2.319: the 1.2.316-1.2.318 single section [Water - EBM/Water look] (keys = the global name without "_Fires"). A value a player changed there
        // is carried to its new key once, and the old section is taken out of the file (nothing else reads it). One Info line if anything moved.
        private const string OldLookSection = "Water - EBM/Water look";
        private static void MoveOldLookSection(ConfigFile config)
        {
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var orphans = (typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(config, null)
                               ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(config)) as System.Collections.IDictionary;
                if (orphans == null) return;
                var old = new List<ConfigDefinition>();
                foreach (System.Collections.DictionaryEntry kv in orphans) if (kv.Key is ConfigDefinition d && d.Section == OldLookSection) old.Add(d);
                if (old.Count == 0) return;
                int moved = 0;
                foreach (var d in old)
                {
                    string text = orphans[d] as string, global = "_Fires" + d.Key;
                    if (text != null && s_lookF.TryGetValue(global, out var f)) { try { var was = f.Value; f.SetSerializedValue(text); if (!Equals(was, f.Value)) moved++; } catch { } }
                    else if (text != null && s_lookC.TryGetValue(global, out var c)) { try { var was = c.Value; c.SetSerializedValue(text); if (!Equals(was, c.Value)) moved++; } catch { } }
                    orphans.Remove(d);
                }
                config.Save();
                EasyBakeLog.Info($"[Water] the old [{OldLookSection}] section is gone: {old.Count} key(s) removed, {moved} changed value(s) carried to the new [Water - ...] sections");
            }
            catch (Exception e) { EasyBakeLog.Warn($"[Water] moving the old [{OldLookSection}] section failed ({e.Message}); its keys stay in the file unused"); }
        }

        // 1.2.319 (Fire 11-11: "no explanation as to what anything does, no organization.... no sections"): the look keys live in six sections in the
        // order a player tunes them, each with a plain description (what you see going up / down, range, default), no internal numbers. The ~10 that
        // matter are Basic (PlayerFacingConfig.AddBasic); the rest are Advanced. Values the shader needs but nobody should tune (switches with one right
        // answer, and the underwater-caustic values only FTS's caustics pass reads) are pushed as constants (K) and are not in the cfg at all.
        // 1.2.444: the six sections were "Water - Colour & Depth" ... "Water - Debug" (EbmConfigMove carries saved values; Water - Debug joined 90 - Debugging)
        private const string SecColour = "42 - Water - Colour & Depth", SecSurface = "44 - Water - Surface & Reflection", SecWaves = "46 - Water - Waves",
            SecFoam = "43 - Water - Foam & Shore", SecUnder = "45 - Water - Underwater & Caustics", SecDebug = "90 - Debugging";
        private static ConfigFile s_cfg;
        private static readonly Dictionary<string, ConfigEntry<float>> s_lookF = new Dictionary<string, ConfigEntry<float>>();
        private static readonly Dictionary<string, ConfigEntry<Color>> s_lookC = new Dictionary<string, ConfigEntry<Color>>();
        private static readonly Dictionary<string, float> s_lookK = new Dictionary<string, float>();

        // one look value: bound once (live: a change pushes at once), pushed when asked
        private static void F(bool push, string global, string section, string key, float def, float min, float max, bool basic, string text)
        {
            if (s_cfg == null) return;
            if (!s_lookF.TryGetValue(global, out var e))
            {
                string inv(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                e = EbmConfigMove.Bind(s_cfg, section, key, def, new ConfigDescription("Needs [40 - Shaders] WaterShader = true. " + text + " Range " + inv(min) + " to " + inv(max) + ". Default " + inv(def) + ".", new AcceptableValueRange<float>(min, max)));
                var entry = e; string name = global;
                e.SettingChanged += (_, __) => Shader.SetGlobalFloat(name, entry.Value);
                s_lookF[global] = e;
                if (basic) FiresEasyBakeMeshes.PlayerFacingConfig.AddBasic(section, key);
            }
            if (push) Shader.SetGlobalFloat(global, e.Value);
        }
        private static void C(bool push, string global, string section, string key, Color def, bool basic, string text)
        {
            if (s_cfg == null) return;
            if (!s_lookC.TryGetValue(global, out var e))
            {
                e = EbmConfigMove.Bind(s_cfg, section, key, def, "Needs [40 - Shaders] WaterShader = true. " + text + " Default #" + ColorUtility.ToHtmlStringRGB(def) + ".");
                var entry = e; string name = global;
                e.SettingChanged += (_, __) => Shader.SetGlobalColor(name, entry.Value);
                s_lookC[global] = e;
                if (basic) FiresEasyBakeMeshes.PlayerFacingConfig.AddBasic(section, key);
            }
            if (push) Shader.SetGlobalColor(global, e.Value);
        }
        // a fixed value the shader reads, not a setting
        private static void K(bool push, string global, float value)
        {
            s_lookK[global] = value;
            if (push) Shader.SetGlobalFloat(global, value);
        }

        internal const string Vanilla = "Custom/Water";
        internal const string FtsWater = "Custom/FiresWater";
        private static readonly int Prop_IsLod = Shader.PropertyToID("_IsLod");
        private static readonly int Prop_VolumeSize = Shader.PropertyToID("_VolumeSize");

        private static bool Wanted => On != null && On.Value && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
        private static float s_tessSayAt;   // 1.2.391: when the per-world WaterTessCut line is due (0 = not due)

        /// <summary>True while the key is on and EBM/Water is loaded: EbmApi.HandlesShader("Custom/Water") answers this (FTS stands down).</summary>
        internal static bool Active => Wanted && Indirect.EbmIndirectAssets.Water(out _) != null;

        // 1.2.316: the old "settings owner" hand-off is gone (EBM is standalone: its own cfg drives EBM/Water). The add-only EbmApi entry stays so an FTS
        // that calls it keeps working; EBM only notes it once. s_owner / s_ownerPerMaterial stay null (no other mod writes EBM/Water's values).
        private static string s_owner;
        private static Action<Material> s_ownerPerMaterial;
        private static bool s_saidOwner;
        internal static void RegisterOwner(string owner, Action<Material> perMaterial)
        {
            if (!string.IsNullOrEmpty(owner) && !s_saidOwner) { s_saidOwner = true; EasyBakeLog.Info($"[Water] {owner} offered to feed the water settings; EBM/Water uses EBM's own [Water - ...] sections settings (standalone), so nothing changes"); }
        }

        private static readonly Dictionary<Material, Shader> s_swapped = new Dictionary<Material, Shader>();   // material -> its shader before ours
        private static readonly HashSet<Material> s_perVolumeDone = new HashSet<Material>();
        private static readonly Dictionary<WaterVolume, bool> s_isZone = new Dictionary<WaterVolume, bool>();
        private static readonly List<Material> s_scratchM = new List<Material>();
        private static readonly Predicate<Material> s_isDead = m => m == null;   // cached: no delegate per pass
        private static readonly List<WaterVolume> s_scratchV = new List<WaterVolume>();
        private static Shader s_restoreFallback;   // the first original seen: for tile instances cloned from a swapped prefab material (never swapped one by one)
        private static object s_defaultsFor;       // the ZNetScene the defaults were pushed for (null = push at the next pass)
        private static float s_nextPass, s_nextReport, s_fatLookAt;
        private static bool s_saidOn, s_saidWhy, s_saidPath;
        private static int c_swapped, c_perVolume, c_skipOther;

        // zone water only: a WaterVolume under a ZNetView belongs to a built piece (the hot tub) and keeps its own material (FTS's rule, the same test)
        private static bool IsZoneWater(WaterVolume v)
        {
            if (v == null || v.m_waterSurface == null) return false;
            if (s_isZone.TryGetValue(v, out bool z)) return z;
            z = v.GetComponentInParent<ZNetView>(true) == null;
            s_isZone[v] = z;
            return z;
        }

        private static bool Takeable(Shader s) => s != null && (s.name == Vanilla || s.name == FtsWater);

        // 1.2.437 EbmFatWaterLook: the same take rule, a zone sea material already on EBM/Water as the inland template, and PerVolume's per-material part
        internal static bool TakeableShader(Shader s) => Takeable(s);
        internal static Material ZoneTemplate()
        {
            var ours = Indirect.EbmIndirectAssets.WaterShader;
            if (ours == null) return null;
            foreach (var kv in s_swapped) if (kv.Key != null && kv.Key.shader == ours) return kv.Key;
            return null;
        }
        internal static void ForProvidedMaterial(Material mat)
        {
            ApplySeamStencil(mat);
            var per = s_ownerPerMaterial;
            if (per == null) return;
            try { per(mat); }
            catch (Exception e) { s_ownerPerMaterial = null; EasyBakeLog.Warn($"[Water] {s_owner}'s per-material water writer threw ({e.Message}); EBM stops calling it this session"); }
        }

        // prefab materials at ZNetScene.Awake: every zone tile is born on EBM/Water (FTS ZNetScene_Awake_FiresWaterPrefabSwap, the same scan)
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class TakeAtSceneAwake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {
                try
                {
                    if (!Wanted || ZNetScene.instance == null || ZNetScene.instance.m_prefabs == null) return;
                    var ours = Indirect.EbmIndirectAssets.Water(out _);
                    if (ours == null) return;
                    int n = 0;
                    foreach (var prefab in ZNetScene.instance.m_prefabs)
                    {
                        if (prefab == null) continue;
                        var wv = prefab.GetComponent<WaterVolume>();
                        if (wv == null) wv = prefab.GetComponentInChildren<WaterVolume>(true);
                        if (wv == null || wv.m_waterSurface == null || wv.GetComponentInParent<ZNetView>(true) != null) continue;   // not cached: prefabs are not live tiles
                        var mat = wv.m_waterSurface.sharedMaterial;
                        if (mat != null && mat.shader != ours && Takeable(mat.shader) && Swap(mat, ours)) { PerVolume(wv, mat); n++; }
                    }
                    EasyBakeLog.Debug($"[Water] prefab water at ZNetScene.Awake: {n} material(s) now on EBM/Water (zone water is born with it)");
                }
                catch (Exception e) { EasyBakeLog.Debug("[Water] prefab swap skipped: " + e.Message); }
            }
        }

        // each volume as it enables (FTS WaterVolume_OnEnable_FiresSwap): its per-volume values at once, its material taken if it is not ours yet
        [HarmonyPatch(typeof(WaterVolume), "OnEnable")]
        private static class TakeAtEnable
        {
            private static void Postfix(WaterVolume __instance)
            {
                try
                {
                    if (!Wanted || !IsZoneWater(__instance)) return;
                    var ours = Indirect.EbmIndirectAssets.Water(out _);
                    var mat = ours != null ? __instance.m_waterSurface.sharedMaterial : null;
                    if (mat == null) return;
                    if (mat.shader != ours) { if (!Takeable(mat.shader) || !Swap(mat, ours)) return; }
                    if (!s_perVolumeDone.Contains(mat)) PerVolume(__instance, mat);
                }
                catch (Exception e) { EasyBakeLog.Debug("[Water] enable swap skipped: " + e.Message); }
            }
        }

        // EBM's LateUpdate, every frame: the depth-texture keep (EBM alone) and one compare until the next 1 s pass
        internal static void Tick()
        {
            EbmFtsGlobals.Tick();   // 1.2.428: the globals only FiresTossinShade wrote (shore field, caustic day mask, season, footprints), EBM's own while FTS is absent
            PushViewDepth();
            PushXFade();   // 1.2.435/436: WaterEdgeCrossFade centre (sent only when it changes) + the far plane hole (once a second)
            if (Time.unscaledTime >= s_fatLookAt) { s_fatLookAt = Time.unscaledTime + 1f; EbmFatWaterLook.Tick(); }   // 1.2.437: bind / register / tell FAT of drawing changes, once a second (before the key gate: FAT must hear EBM stop)
            EbmWaterFarRing.Tick(Wanted && Indirect.EbmIndirectAssets.WaterShader != null);   // 1.2.469: before the key gate (a key off gives the game's plane back)
            PushCamUnder();   // 1.2.411: once a frame (sent only when it changes)   // 1.2.408: _FiresWaterViewDepth for Core's caustics (sent only when it changes)
            if (!Wanted) { if (s_swapped.Count > 0) ReleaseAll("key off"); return; }
            if (s_owner == null && s_defaultsFor != null)
            {
                // _FiresPixelDepth (on by default) reads _CameraDepthTexture: kept on as FTS does (FiresUnderwaterEnv.LateUpdate); a new camera gets it in its first frame
                var cam = Utils.GetMainCamera();
                if (cam != null && (cam.depthTextureMode & DepthTextureMode.Depth) == 0) cam.depthTextureMode |= DepthTextureMode.Depth;
            }
            if (s_defaultsFor != null) Weather();   // 1.2.322: rain rings + wind drift, EBM's own (FTS stands down fully while EBM draws the water)
            float now = Time.unscaledTime;
            if (now < s_nextPass) return;
            s_nextPass = now + 1f;
            if (ZNetScene.instance == null) return;
            var ours = Indirect.EbmIndirectAssets.Water(out string why);
            if (ours == null)
            {
                if (!s_saidWhy) { s_saidWhy = true; EasyBakeLog.Info($"[Water] WaterShader is on but EBM/Water is not available ({why}): the water stays on its current shader"); }
                return;
            }
            if (s_owner == null && !ReferenceEquals(s_defaultsFor, ZNetScene.instance)) { Look(push: true); s_defaultsFor = ZNetScene.instance; s_tessSayAt = Time.unscaledTime + 5f; s_tessSayWhy = null; }
            PushEdge(false);   // 1.2.426: the far plane's hole radius follows the synced near distance (sent only when it changes)
            ApplyVanillaDetail("applied");   // 1.2.396: once a second, so a shader that loads late (or a new shader object) gets the key's state, and the LOD-limit guard looks again; a compare and two int reads when nothing changed
            if (s_tessSayAt > 0f && Time.unscaledTime >= s_tessSayAt) { s_tessSayAt = 0f; PushTessCut(s_tessSayWhy ?? "world loaded"); if (s_tessSayWhy == null) PushFftFalloff(true); s_tessSayWhy = null; }   // 1.2.391: once per world (5 s in, so the tiles around the player are counted) and once per key change (1 s after the last step)

            var list = WaterVolume.Instances;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    var mat = v.m_waterSurface.sharedMaterial;
                    if (mat == null) continue;
                    if (mat.shader != ours)
                    {
                        if (!Takeable(mat.shader)) { c_skipOther++; continue; }   // another mod's shader on it: theirs
                        if (!Swap(mat, ours)) continue;
                    }
                    if (!s_perVolumeDone.Contains(mat)) PerVolume(v, mat);
                }
            Prune();
            Report(ours);
        }

        private static bool Swap(Material mat, Shader ours)
        {
            if (s_swapped.ContainsKey(mat)) { if (mat.shader != ours) mat.shader = ours; return true; }   // someone put it back: ours again
            try
            {
                s_swapped[mat] = mat.shader;
                if (s_restoreFallback == null) s_restoreFallback = mat.shader;
                mat.shader = ours;   // value-preserving: every vanilla property name is declared by EBM/Water (FiresWater's design)
                if (mat.HasProperty(Prop_IsLod)) mat.SetFloat(Prop_IsLod, 0f);   // uniform tessellation on zone tiles (FTS TrySwapOne)
                c_swapped++;
                EbmFatWaterLook.ZoneSwapped();   // 1.2.437: an inland sheet waiting for a sea template is asked again
                if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info($"[Water] EBM's water shader draws the zone water (EBM/Water; was {s_swapped[mat].name}); built pieces' water (the hot tub) stays as it was"); }
                return true;
            }
            catch (Exception e) { s_swapped.Remove(mat); EasyBakeLog.Debug($"[Water] swap failed on '{mat.name}': {e.Message}"); return false; }
        }

        // FTS ApplyPerVolumeProperties: _VolumeSize from the volume's collider (the source vanilla's own depth lookup uses); then the owner's foam values
        private static void PerVolume(WaterVolume v, Material mat)
        {
            s_perVolumeDone.Add(mat);
            c_perVolume++;
            ApplySeamStencil(mat);   // 1.2.426
            try
            {
                var col = v.GetComponent<Collider>();
                if (col != null) { float sx = col.bounds.size.x; if (sx > 0.001f && mat.HasProperty(Prop_VolumeSize)) mat.SetFloat(Prop_VolumeSize, sx); }
            }
            catch { }
            var per = s_ownerPerMaterial;
            if (per != null)
            {
                try { per(mat); }
                catch (Exception e) { s_ownerPerMaterial = null; EasyBakeLog.Warn($"[Water] {s_owner}'s per-material water writer threw ({e.Message}); EBM stops calling it this session"); }
            }
        }

        private static void ReleaseAll(string why)
        {
            var ours = Indirect.EbmIndirectAssets.WaterShader;
            int n = 0;
            foreach (var kv in s_swapped)
            {
                if (kv.Key == null || kv.Value == null) continue;
                try { if (kv.Key.shader == ours) { kv.Key.shader = kv.Value; n++; } } catch { }
            }
            // tile instances cloned (vanilla SetupMaterial) from a swapped prefab material were never swapped one by one: back to that material's original
            var list = WaterVolume.Instances;
            if (list != null && ours != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    var mat = v.m_waterSurface.sharedMaterial;
                    if (mat == null || mat.shader != ours) continue;
                    var back = s_restoreFallback != null ? s_restoreFallback : Shader.Find(Vanilla);
                    if (back != null) { try { mat.shader = back; n++; } catch { } }
                }
            s_swapped.Clear(); s_perVolumeDone.Clear();
            s_saidOn = false;
            if (n > 0) EasyBakeLog.Info($"[Water] {n} water material(s) back on their own shader ({why})");
        }

        private static void Prune()
        {
            s_scratchM.Clear();
            foreach (var kv in s_swapped) if (kv.Key == null) s_scratchM.Add(kv.Key);
            foreach (var m in s_scratchM) s_swapped.Remove(m);
            s_perVolumeDone.RemoveWhere(s_isDead);
            s_scratchV.Clear();
            foreach (var kv in s_isZone) if (kv.Key == null) s_scratchV.Add(kv.Key);
            foreach (var v in s_scratchV) s_isZone.Remove(v);
            s_scratchM.Clear(); s_scratchV.Clear();
        }

        // ── EBM alone: FTS's DEFAULT value for every global EBM/Water reads (FTS's code reads its config; these are that config's Bind defaults) ──
        // Only globals that are NOT a feature gate: a gate left at 0 is that feature's fallback (planar mirror, shore field, caustic texture, rain rings,
        // lace foam, ice, tide field, wind travel), so EBM never switches on something it has no data for.
        private static void Look(bool push)
        {
            // ── Colour & Depth (Basic first: how clear the near water is, then its colour) ──
            F(push, "_FiresShallowOpaqueM", SecColour, "Shallow Water Opacity Distance", 0.10f, 0f, 5f, true,
                "How far you see into shallow water before it turns solid. Lower = shallows go opaque sooner (murkier, the bottom hides). Higher = clearer shallows, you see further down. 0 = this effect off. In metres of view.");
            F(push, "_FiresShallowBodyM", SecColour, "Shallow Water Colour Distance", 15f, 0f, 60f, true,
                "How quickly the water's colour covers the sea bed in the shallows. Lower = the bed disappears under the water colour sooner. Higher = you see the bed's own colour longer. 0 = this effect off. In metres of view.");
            // [seasons] near-water fix (lead GO 11-11; defaults PROVISIONAL until Fire's live values): the body-cover floor + the cover terms as keys
            F(push, "_FiresShallowBodyFloorM", SecColour, "Shallow Water Colour Floor", 15f, 0f, 60f, true,
                "Looking straight down into shallow water: it takes its colour as if your view went through at least this many metres of water, so you see water, not the ground (the far water already looks this way). Lower = clearer shallows. 0 = off. In metres.");
            F(push, "_FiresRefractBright", SecColour, "Sea Bed Brightness", 1.4f, 0.2f, 3f, false,
                "Brightness of the sea bed seen through the water. Lower = the bed shows less through the water colour.");
            F(push, "_FiresBodyAlphaBase", SecColour, "Water Colour In The Shallows", 0.35f, 0.001f, 1f, false,
                "How much of the water's own colour shows even where it is very shallow. Higher = less see-through everywhere.");
            F(push, "_FiresBodyTint", SecColour, "Water Colour Strength", 1f, 0.1f, 4f, false,
                "How strongly the water's colour covers the bed. Higher = more tinted, less see-through.");
            F(push, "_FiresClarityRampM", SecColour, "Shoreline Softness", 0.35f, 0.05f, 3f, false,
                "Over how many metres of depth the shallow-water colour fades in from the waterline. Higher = a softer, wider shore edge. In metres.");
            F(push, "_FiresBodyLightCap", SecColour, "Water Body Light Limit", 8f, 1f, 16f, false,
                "The brightest light the water body, murk and foam can receive. Lower = water stays darker in bright sun.");
            F(push, "_FiresMurkDark", SecColour, "Murk Darkness", 0.32f, 0.02f, 1.5f, false,
                "How bright the murk is. Lower = darker, deeper-looking murk. Higher = paler murk.");
            F(push, "_FiresSurfaceAlphaMax", SecColour, "Shallow Water Max Opacity", 0.95f, 0f, 1f, true,
                "The most solid the shallow water can get. Lower = shallows never go fully solid, you always see a little through. 1 = fully solid.");
            F(push, "_FiresMurkPathFloorM", SecColour, "Shallow Water Haze", 0f, 0f, 40f, true,
                "Adds some of the deep water's haze to the shallows. Higher = hazier near water. 0 = off (the game's own look). In metres.");
            F(push, "_FiresClarityFloorM", SecColour, "Minimum Water Depth For Colour", 2.5f, 0f, 10f, false,
                "Water shallower than this is coloured as if it were this deep, so a thin sheet of water over sand still has some colour. Higher = more colour over sandbars. 0 = off. In metres.");
            F(push, "_FiresRayExtinction", SecColour, "Water Absorbs Along The View", 0f, 0f, 4f, true,   // [saddles] 11-13 (Fire 11-11 "FAR too clear"); EBM 1.2.359; shader hunk EbmWater.shader _FiresRayExtinction, 0 = bit-identical old look
                "Water absorbs light along your line of sight, like real water (and like Vedr's water): the farther your view travels through water to reach the bed, the more the bed fades into the water colour and the more solid the surface looks. 0 = off (the old look). 1 = Vedr's strength. Higher = murkier.");
            F(push, "_FiresAbsorbBlend", SecColour, "Depth Colour Absorption", 0.85f, 0f, 1f, true,
                "How strongly deep water swallows light (reds go first, then greens). Higher = deep water darker and bluer. 0 = no absorption.");
            C(push, "_FiresAbsorbExtinction", SecColour, "Absorption Colour Loss", new Color(0.60f, 0.39f, 0.29f, 1f), false,
                "How fast each colour fades with depth (red, green, blue). A bigger number for a colour = that colour disappears sooner.");
            F(push, "_FiresMurkAmount", SecColour, "Murk", 1f, 0f, 3f, true,
                "Suspended silt in the water. Higher = murkier, cloudier water. 0 = crystal clear.");
            C(push, "_FiresMurkColor", SecColour, "Murk Colour", new Color(0.55f, 0.54f, 0.46f, 1f), true,
                "The colour of the silt in the water.");
            F(push, "_FiresShallowMurk", SecColour, "Shallow Churn", 0.35f, 0f, 2f, false,
                "Extra stirred-up silt where the water is shallow. Higher = muddier shallows. 0 = none.");
            F(push, "_FiresMurkChurn", SecColour, "Churn Motion", 1f, 0f, 3f, false,
                "How much the silt moves with the water. Higher = livelier swirling. 0 = still.");
            F(push, "_FiresMurkRain", SecColour, "Rain Murk", 0.4f, 0f, 2f, false,
                "Extra murk while it rains. Higher = rain stirs the water up more. 0 = rain changes nothing.");
            C(push, "_FiresSSSColor", SecColour, "Glow Colour", new Color(0.10f, 0.45f, 0.40f, 1f), false,
                "The colour light picks up passing through a wave (the green-blue glow in a backlit wave).");
            F(push, "_FiresSSSBase", SecColour, "Glow Strength", 0.16f, 0f, 2f, false,
                "How much of that glow the water always has. Higher = brighter, more see-through looking water. 0 = none.");
            F(push, "_FiresSSSSun", SecColour, "Sun Glow", 1.7f, 0f, 5f, false,
                "Extra glow when looking toward the sun through a wave. Higher = stronger backlit waves.");
            F(push, "_FiresSSSSunFalloff", SecColour, "Sun Glow Focus", 5f, 1f, 20f, false,
                "How tightly the sun glow gathers around the sun. Higher = a smaller, sharper glow spot.");
            F(push, "_FiresSSSWaveBoost", SecColour, "Wave Crest Glow", 1.5f, 0f, 5f, false,
                "Extra glow on wave crests. Higher = crests light up more.");

            // ── Surface & Reflection ──
            F(push, "_FiresReflectionBlend", SecSurface, "Reflection Strength", 0.795f, 0f, 1f, true,
                "How mirror-like the water is at a low angle. Higher = more sky reflected. 0 = no reflection.");
            F(push, "_FiresReflectionFloor", SecSurface, "Reflection Looking Down", 0.054f, 0f, 1f, false,
                "How much the water still reflects when you look straight down. Higher = more reflection even from above.");
            F(push, "_FiresFresnelPower", SecSurface, "Reflection Angle Softness", 8f, 1f, 20f, false,
                "How quickly reflection grows as you look across the water. Higher = reflection only near the horizon. Lower = reflection everywhere.");
            F(push, "_FiresSchlickBlend", SecSurface, "Physical Reflection Mix", 0.65f, 0f, 1f, false,
                "Blends toward real-world reflection behaviour. Higher = more realistic reflection falloff.");
            F(push, "_FiresNadirReflCap", SecSurface, "Reflection Cap Looking Down", 1f, 0f, 1f, false,
                "Limits reflection when looking straight down. Lower = clearer view into the water from above.");
            F(push, "_FiresDistantReflect", SecSurface, "Distant Reflection", 0.5f, 0f, 1f, false,
                "Reflection on water far from you. Higher = far water more mirror-like.");
            C(push, "_FiresDistantTint", SecSurface, "Distant Water Tint", Color.white, false,
                "A colour tint over far water. White = no tint.");
            F(push, "_FiresDistantBright", SecSurface, "Distant Water Brightness", 1f, 0f, 3f, false,
                "Brightness of far water. Higher = brighter far water.");
            F(push, "_FiresSunSpecScale", SecSurface, "Sun Sparkle", 1f, 0f, 5f, true,
                "The bright sun glint on the water. Higher = stronger sparkle. 0 = none.");
            F(push, "_FiresDistSpecRough", SecSurface, "Distant Sparkle Softness", 0.6f, 0f, 1f, false,
                "How soft the sun glint is on far water. Higher = a softer, wider glint far away.");
            F(push, "_FiresSurfaceFogAmount", SecSurface, "Sea Fog On Water", 0.45f, 0f, 2f, false,
                "How much the game's fog covers the water surface. Higher = foggier sea. 0 = the water ignores the fog.");

            // ── Waves ──
            F(push, "_FiresNormalSpeed", SecWaves, "Ripple Speed", 0.222f, 0f, 2f, true,
                "How fast the small ripples move. Higher = busier water. 0 = frozen ripples.");
            F(push, "_FiresWaveOpacity", SecWaves, "Wave Opacity", 0.95f, 0f, 1f, false,
                "How solid the waves look. Lower = waves more see-through.");
            F(push, "_FiresWaveTexels", SecWaves, "Wave Detail Limit", 3f, 0f, 8f, false,
                "Smooths ripple detail that is too small to see, to stop shimmer. Higher = smoother, less shimmer. 0 = off (sharpest, may shimmer).");
            F(push, "_FiresDistNormalFade", SecWaves, "Ripple Fade Distance", 900f, 50f, 5000f, false,
                "How far away ripples fade to a calm surface. Higher = ripples visible further out. In metres.");

            // ── Foam & Shore ──
            F(push, "_FiresShoreFoam", SecFoam, "Shore Foam", 1f, 0f, 1f, true,
                "Foam where water meets land. 1 = on, 0 = off.");
            F(push, "_FiresShoreFoamAmount", SecFoam, "Shore Foam Amount", 0.6f, 0f, 2f, false,
                "How much foam gathers at the shore. Higher = more foam.");
            F(push, "_FiresShoreFoamDepth", SecFoam, "Shore Foam Reach", 3f, 0f, 10f, false,
                "How far out from the shore the foam reaches. Higher = a wider foam band. In metres of depth.");
            F(push, "_FiresFoamShoreAmount", SecFoam, "Shore Foam Blend", 0.5f, 0f, 2f, false,
                "How strongly foam builds where the depth gets shallow. Higher = stronger foam at the edge.");
            F(push, "_FiresFoamShoreContrast", SecFoam, "Shore Foam Contrast", -1f, -1f, 4f, false,
                "Sharpness of the shore foam edge. Higher = a crisper foam edge. -1 = automatic.");
            F(push, "_FiresFoamIntensityA", SecFoam, "Foam Layer 1", 0.6f, 0f, 2f, false,
                "Strength of the first foam pattern. Higher = more visible foam.");
            F(push, "_FiresFoamIntensityB", SecFoam, "Foam Layer 2", 0.4f, 0f, 2f, false,
                "Strength of the second, larger foam pattern. Higher = more visible foam.");
            F(push, "_FiresFoamTilingA", SecFoam, "Foam Layer 1 Size", 1f, 0.05f, 10f, false,
                "Size of the first foam pattern. Higher = smaller, more repeated foam.");
            F(push, "_FiresFoamTilingB", SecFoam, "Foam Layer 2 Size", 0.5f, 0.05f, 10f, false,
                "Size of the second foam pattern. Higher = smaller, more repeated foam.");
            F(push, "_FiresFoamSpeed", SecFoam, "Foam Speed", 2.5f, 0f, 10f, false,
                "How fast foam drifts. Higher = faster foam. 0 = still.");
            F(push, "_FiresFoamWaveHeightDriven", SecFoam, "Foam On Wave Tops", 0.55f, 0f, 2f, false,
                "Foam on tall wave crests. Higher = more whitecaps in rough seas.");
            F(push, "_FiresFoamSlopeDriven", SecFoam, "Foam On Steep Waves", 0.1f, 0f, 2f, false,
                "Foam on the steep faces of waves. Higher = more foam on breaking faces.");
            F(push, "_FiresFoamBubbleIntensity", SecFoam, "Foam Bubbles", 0.6f, 0f, 2f, false,
                "Bubbly texture inside the foam. Higher = more bubbles.");
            F(push, "_FiresFoamSunBoost", SecFoam, "Foam In Sunlight", 0.36f, 0f, 2f, false,
                "How much brighter foam gets in direct sun. Higher = brighter foam in daylight.");
            C(push, "_FiresFoamColor", SecFoam, "Foam Colour", Color.white, false,
                "The colour of the foam.");
            F(push, "_FiresFoamOpacity", SecFoam, "Foam Opacity", 0.11f, 0f, 1f, false,
                "How solid the shore foam is (the foam band where water meets land; needs Shore Foam on). Higher = whiter shore foam. 0 = no shore foam. It does not change the foam on waves: use Foam Coverage and Foam On Wave Tops for that.");   // [visual] 11-15: text corrected; in EBM this value scales only FiresShoreFoamMask (EbmWater.shader:1413 in the 1.2.400 tree)
            F(push, "_FiresFoamCoverage", SecFoam, "Foam Coverage", 0.83f, 0f, 1f, false,
                "How much of the water the foam pattern covers. Higher = more foam patches. 0 = no foam at all.");
            K(push, "_FiresFoamFeather", 0.15f);    // [visual] 11-15: was the key "Foam Edge Softness"; the shader reads it only in the cell-foam branch (EbmWater.shader:1495 in the 1.2.400 tree, inside "if (_FiresFoamCells > 0.5)" :1483), which "_FiresFoamCells" 0 below keeps off, so the key did nothing. Same value, no key.
            F(push, "_FiresFoamBubbleParallax", SecFoam, "Foam Depth Effect", 0.14f, 0f, 1f, false,
                "Makes foam bubbles look like they sit at different depths. Higher = stronger 3D look.");

            // ── Underwater & Caustics ──
            F(push, "_FiresCausticStrength", SecUnder, "Surface Light Patterns", 0f, 0f, 5f, false,
                "Dancing light patterns (caustics) on the sea bed seen from above water. Higher = brighter patterns. 0 = off.");
            F(push, "_FiresCausticScale", SecUnder, "Light Pattern Size", 0.007f, 0.001f, 0.1f, false,
                "Size of the light patterns. Higher = smaller, tighter patterns.");
            F(push, "_FiresCausticSpeed", SecUnder, "Light Pattern Speed", 0.024f, 0f, 0.5f, false,
                "How fast the light patterns move. Higher = faster. 0 = still.");
            F(push, "_FiresCausticWarp", SecUnder, "Light Pattern Wobble", 0f, 0f, 2f, false,
                "How much the light patterns wobble. Higher = more distorted patterns.");
            F(push, "_FiresCausticSharp", SecUnder, "Light Pattern Sharpness", 0.3f, 0f, 1f, false,
                "How crisp the light patterns are. Higher = thinner, sharper lines.");
            F(push, "_FiresUnderwaterDapple", SecUnder, "Underwater Dapple", 0f, 0f, 1f, false,
                "Light patterns on the surface when looking up from under water. 1 = on, 0 = off.");
            F(push, "_FiresRefractShoreFade", SecUnder, "Shore Refraction Fade", 2.5f, 0f, 10f, false,
                "Calms the bending of the view through water near the shore, so the bed does not swim. Higher = calmer near shore. In metres.");
            F(push, "_FiresRefractNearStart", SecUnder, "Near Refraction Start", 8f, 0f, 50f, false,
                "Distance from you where the view through the water starts to bend fully. In metres.");
            F(push, "_FiresRefractNearRange", SecUnder, "Near Refraction Range", 30f, 1f, 200f, false,
                "Over how many metres the bending fades in after that start. In metres.");

            // ── Debug (Advanced) ──
            F(push, "_FiresWaterDebugView", SecDebug, "Debug View", 0f, 0f, 10f, false,
                "Shows one of the shader's inner values as colour, for troubleshooting. 0 = normal water.");
            F(push, "_FiresPixelDepth", SecDebug, "Per-Pixel Depth", 1f, 0f, 1f, false,
                "1 = water depth is read per pixel (accurate shores). 0 = the older per-vertex depth (blotchy patches over bumpy beds). Leave at 1.");
            F(push, "_FiresFoamPerPixel", SecDebug, "Per-Pixel Foam", 1f, 0f, 1f, false,
                "1 = foam worked out per pixel (smooth). 0 = per vertex (cheaper, blockier). Leave at 1.");

            // ── fixed values the shader reads (not settings) ──
            K(push, "_FiresMurkOn", 1f);
            K(push, "_FiresMurkDepth", 4f);
            K(push, "_FiresMurkSoftness", 1.8f);
            K(push, "_FiresCausticUseTex", 0f);     // EBM ships no caustic texture: the shader's built-in caustic
            K(push, "_FiresFoamCells", 0f);         // lace foam needs FTS's cell texture
            K(push, "_FiresFoamCellsScale", 4f);
            K(push, "_FiresDepthDarken", 1f);       // no shore field: 1 = no darkening (0 would be black)
            // read only by FTS's underwater caustics pass, which stays silent while EBM draws the water: FTS's defaults, so that pass still has its values
            K(push, "_FiresUWCausticScale", 0.144f);
            K(push, "_FiresUWCausticSharp", 0.706f);
            K(push, "_FiresUWCausticStrength", 0.1f);
            K(push, "_FiresUWCausticSpeed", 0.019f);
            K(push, "_FiresUWCausticWarp", 0.6f);
            if (push) Shader.SetGlobalFloat("_FiresFogMode", FogModeId());         // RenderSettings.fogMode, as FiresWaterTuning.FogModeId (EnvMan exists: we are in a world)
            if (push) EasyBakeLog.Info($"[Water] EBM/Water pushed its own look settings ({s_lookF.Count + s_lookC.Count} settings in the six [Water - ...] sections + {s_lookK.Count} fixed values; sky reflection, no shore field, built-in caustics)");
        }

        private static void G(string name, float v) => Shader.SetGlobalFloat(name, v);
        private static void Gc(string name, Color c) => Shader.SetGlobalColor(name, c);

        // FiresWaterTuning.FogModeId: 0 Linear, 1 Exp, 2 Exp2 (Valheim's); RenderSettings.fogMode is only read once EnvMan exists (pre-scene access violates)
        private static float FogModeId()
        {
            if (EnvMan.instance == null) return 2f;
            switch (RenderSettings.fogMode) { case FogMode.Linear: return 0f; case FogMode.Exponential: return 1f; default: return 2f; }
        }

        private static void Report(Shader ours)
        {
            float t = Time.unscaledTime;
            if (t < s_nextReport) return;
            s_nextReport = t + 30f;
            var cam = Utils.GetMainCamera();
            if (!s_saidPath && cam != null) { s_saidPath = true; EasyBakeLog.Info($"[Water] main camera renders {cam.actualRenderingPath}; depth texture {cam.depthTextureMode} (EBM/Water's per-pixel depth reads it)"); }
            EasyBakeLog.Debug($"[Water] last 30 s: {s_swapped.Count} material(s) taken, {c_swapped} swapped, {c_perVolume} per-volume write(s), {c_skipOther} pass skip(s) on another mod's shader; owner {(s_owner ?? "none (EBM defaults)")}");
            c_swapped = c_perVolume = c_skipOther = 0;
            if (Verify == null || !Verify.Value) return;
            int mine = 0, vanilla = 0, fts = 0, other = 0, bad = 0; string first = null;
            var list = WaterVolume.Instances;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || v.m_waterSurface == null) continue;
                    var mat = v.m_waterSurface.sharedMaterial;
                    if (mat == null || mat.shader == null) continue;
                    bool zone = IsZoneWater(v);
                    if (mat.shader == ours) { if (zone) mine++; else { bad++; if (first == null) first = $"'{v.name}' at {v.transform.position.x:0},{v.transform.position.z:0}"; } }
                    else if (!zone) continue;
                    else if (mat.shader.name == Vanilla) vanilla++;
                    else if (mat.shader.name == FtsWater) fts++;
                    else other++;
                }
            string tail = $"zone tiles on EBM/Water {mine}, still vanilla {vanilla}, still FiresWater {fts}, another shader {other}; owner {(s_owner ?? "none")}; depth texture {(cam != null ? cam.depthTextureMode.ToString() : "no camera")}";
            if (bad == 0) EasyBakeLog.Debug("[Water] verify: " + tail);
            else EasyBakeLog.Warn($"[Water] verify: {bad} built piece water(s) on EBM/Water (first: {first}); " + tail);
        }
    }
}
