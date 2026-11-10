## 1.2.262

### New
- New GPU drawing path for baked buildings, rocks and trees ("Indirect Renderer", OFF by default). Instead of one draw per zone, the graphics card picks what is in view and in the sun's shadow and draws everything in a few calls. It needs the extra file `ebm_indirect` next to the DLL. Anything it can't handle is drawn the old way.
- EasyBakeMeshes now has its own piece shader. It does the same wet look, winter snow and roof-dry look as the team's snow and weather mods, so baked pieces keep those looks without needing another mod.
- Far buildings: distant builds are drawn by EasyBakeMeshes itself and show up in the first seconds after loading or teleporting instead of popping in later. Far impostors stay off.
- Object streaming for single player and clients moved in from FiresGhettoNetworking. EasyBakeMeshes now paces how fast objects are created around you, adjusting to your frame time, and keeps up while you move or after a teleport. FiresGhettoNetworking steps aside automatically when EasyBakeMeshes' pacer is running.
- Intact rocks can share one mesh per rock type, with a simpler model in the distance ("Bake Intact Rocks", OFF by default). The first hit gives the rock its normal model back.
- Console commands for testing: `ebm_indirect on|off|status` and `ebm_opt <setting> on|off|status|list` switch settings for this session only, without writing your config.
- Mods can talk to EasyBakeMeshes through its own public API. EasyBakeMeshes no longer looks for any other mod.

### Changed
- Settings marked "default off" stay off until you turn them on. New ones this release include the rug variant, the grass path, pixel shave, late join from saved data, remote zone checks, a vanilla minimum for the pacer, and saving off the main thread.
- The cache format changed, so every zone is baked fresh once the first time you load in after updating. This is a one-time slower load. Your world save is not touched.
- Repeating and test log lines print only at Debug. Info shows load and on/off.
- Without the core mod, EasyBakeMeshes runs on its own. Parts that need the core mod or the snow mod skip quietly when they are absent.
- Indoor wet: when the weather mod (Vedr) handles indoor wet, EasyBakeMeshes leaves it to Vedr.

### Fixed
- Stretched or broken building pieces, mainly with shadows on. The shadow pass wrote into data the camera was still reading. Every mesh EasyBakeMeshes builds or reads from its cache is now checked once, and a bad one is not drawn and is logged by name. A bad cached zone rebakes.
- Big cities (100k+ objects) taking minutes to load: cached zones were wrongly thrown away and rebuilt while their pieces were still arriving from the server.
- Arriving or teleporting no longer waits on baked pieces that are already standing in.
- Snow on baked pieces: snow caps now build up on baked pieces the same way as on normal ones. Pieces under a roof stay dry, and indoor rain and snow stop while you are sheltered.
- Pieces that went invisible after a zone unloaded and reloaded get their looks back.
- The loading step that pre-checks prefabs no longer wakes up other mods' objects (one mod's projectile threw an error there). Every call into another mod is guarded: if it throws, there is one warning and EasyBakeMeshes falls back to its own behaviour.
- A graphics-driver crash on the GPU path turns that path off for the rest of the session instead of crashing again.
- A dedicated server no longer runs the far-building arrival budget forever.
- Settings changes after a relog no longer show old bakes.
- Red "_Metallic" log lines no longer appear on some modded shaders.
- Turning the mod off (Plugin Enabled) now also stops the rain hider, wet override, shelter check, light budget and item skip, and gives back what they changed.

### Performance
- Zone checks run 10 times a second while nothing is queued, instead of every frame.
- The once-a-minute 115 ms hitch from the sector rebuild is now spread over frames.
- The once-every-30-seconds 77 ms frame from a debug count is gone (that count is now opt-in).
- Rocks, trees and item stands no longer get re-processed while nothing changed. Before, thousands were redone every 30 seconds while standing still in a town.
- Growing the GPU buffer no longer re-uploads everything from the CPU, which removes 100+ ms frames as more objects come into view.
- Far buildings check for changes much less often when nothing changed.
- Fewer memory allocations when objects are created.

### Also in this release (1.2.258 to 1.2.262)
- Fixed far buildings looking white in summer: they no longer copy the snow caps every building piece carries. Snow caps on far buildings now show only where snow has really built up (Mountains, Deep North, or when the season has snow).
- Baked buildings wait up to 3 seconds for each piece's real snow amount instead of using the area's average ([Snow] Wait For Pushed Snow, on).
- Snow on baked buildings is checked again once a second and shortly after you enter a world ([Snow] Snow Refresh Poll, on).

## 1.2.159
- Feasts and fuel-burning pieces like hanging braziers are never baked, so they show the right food and fuel state. (baking)
- Less lag walking into big towns: only the nearest objects are sorted for loading each frame ("PartialCreateSort", on by default). (performance)
- "SkipVegetation", "SkipOnHost" and "SkipDamageablePieces" are now off by default (fixes white trees and chests falling); your saved values are kept. (config)
- Baked pieces now follow material changes from other mods, so seasonal looks like autumn groves show right without toggling the mod. (visuals)
- Snow on baked pieces updates only the area around a placed or removed piece. (snow)
- New optional shadow saver: far or small baked pieces stop casting shadows ("FarSmallNoShadows", off by default). New "SpreadFirstSightChecks" option spreads first-load checks over frames (off by default). (performance)
- Shorter first-frame hitch on entering a world, and fewer per-frame checks. (performance)
- "DeferCreationWhileBaking" has been removed. (config)

## 1.2.147
- Baked roofs keep their shelter: no more snow inside roofed houses, and baked pieces get the same dynamic snow as normal pieces.
- Light budget: only the nearest piece lights cast full light and shadows; fire shadows and smelter lights no longer flicker.
- On by default now: "SkipOnHost", "SkipDamageablePieces", "SkipVegetation" (one-time update for existing configs).
- Fewer stutters when snow changes on baked pieces ("SnowResplitBudgetMs" 2) and when joining a world.
- Map roof toggle hides baked roofs too.

## 1.2.122
- Bug fixes and improvements.

## 1.2.114
- Fixed build pieces missing from the large map's Live map view (All levels).
- Pieces removed from a build (for example by an admin) no longer leave an old baked copy behind.

## 1.2.112
- Cleaner settings screen: only the basics show, the rest is under Advanced (your saved values are kept).
- Fixed the in-game help: settings are under F1 or Esc > Mod Settings, and it lists the console commands.
- Test commands now need devcommands.

## 1.2.101
- Fixed building pieces (stone floors first) turning invisible until you took out the hammer.

## 1.2.100
- Lower memory use.
- Creatures are never baked.
- Logging out is about 2 seconds faster.

## 1.2.89
- Smoother big bases.
- Fixes and optimizations.

## 1.2.77
- Smoother loading into big bases, and no more once-a-second stutter.
- Worn, damaged and invisible pieces now look the way they should.
- Pieces edited by map makers or Structure Tweaks stay untouched, so adventure maps work as built.
- More of a big build gets sped up, including admin-placed ruins and decoration.
- Added a setting to exclude any piece that looks wrong.

## 1.2.15
- Optimizations for Valheim 1.0.
