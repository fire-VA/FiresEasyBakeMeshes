# Fires Easy Bake Meshes

**Big builds, normal frame rates.** In a big base, your machine treats every wall, floor and beam as its own object, and the frame rate collapses. Easy Bake finds the parts that never change and draws them cheaply. The world itself is untouched: the server still holds every piece, and only your own game does less work.

There's nothing to learn. Install it and big bases get faster. Current version: 1.2.240.

## How it speeds up a base

**Merged pieces** - `[Batching] Enabled` (on)
- **Without it:** a base is thousands of separate draws.
- **What it does:** when a zone loads, pieces sharing a material are combined into one mesh. Colliders, building and interaction still work.

**Repeated pieces drawn as copies** - `InstancingEnabled` (on), `MinInstancesPerPrefab` (10)
- **Without it:** palisades, floor tiles, vines and tiled roofs each cost their own draw.
- **What it does:** draws them as copies of one model. Removing one piece just drops it out, with no rebuild of the zone.

**Pieces that don't need to exist** - `SkipBakedPieceObjects` (on)
- **Without it:** every piece stays a full object, even when something else draws it. In a dense town that's one of the biggest costs left.
- **What it does:** on a multiplayer client, a plain structural piece already being drawn is never created. An invisible stand-in of the same shape takes its place, so you still walk on it and arrows still hit it.
- **Hosts and single player:** skipping stays off, because that machine runs the world. `SkipOnHost` turns it on; it's off by default because a piece that loses support while skipped keeps standing until it's created again.

**Real pieces come back when you need them** - `BuildToolRadiusMeters` (24)
- **What it does:** with a build tool out, real pieces return within this distance, so snapping, highlighting and removing work. Damage, burning, a change of look or removal also brings a piece straight back.
- **Hits on skipped pieces:** a hit on an uncreated piece you own is held, the piece is brought back, and the hit is then applied.

**Far zones get simpler** - `FarTierEnabled` (on), `FarTierDistanceMeters` (64)
- Baked zones switch to a lower-detail mesh past this distance, measured to the zone's nearest point.

**Far-away bases** - `[Far buildings] Enabled` (on), `Reach (m)` (2500), `Max vertices` (2000000)
- **Without it:** a base outside the loaded area just isn't there until you get close.
- **What it does:** draws bases past the loaded area as simple merged shapes in their own materials. Nothing inside the loaded area changes. If the limit is reached, the farthest are dropped first.
- **What you'll see:** a host or single-player world knows every piece, so it can draw any base in reach. A joined client only knows the zones it has loaded, so by default it draws only bases you've already been near this session. Two keys in the same section, both off by default, let a client ask the server for the rest; the server needs its key on too.

**Off-screen copies skipped** - `SkipInstancesOutOfView` (on)
- Copies behind you aren't drawn, but their shadows still land on what you can see.

**Bakes saved to disk** - `[Cache] PersistToDisk` (on)
- **Without it:** every zone is rebuilt every session.
- **What it does:** baked zones reload next session instead of rebuilding. A saved bake that's damaged, or made with different settings, is rejected and that zone bakes fresh. A saved bake is not thrown away just because the server hasn't finished sending the zone yet. Old zones you've left are released from memory after 10 minutes.

## Rain and snow

**No rain or snow indoors** - always on
- **Without it:** walk into a house during a storm and it keeps falling through the roof, because vanilla only rechecks when the weather changes.
- **What it does:** stops falling weather while you're sheltered, clears flakes already drifting inside, and starts it again outside. Fog and mist stay.

**Dry insides** - always on
- **Without it:** roofed pieces and roof undersides look soaked from inside.
- **What it does:** draws roofed pieces dry while it rains. Stand-ins also copy what vanilla needs to tell whether you're sheltered, so you don't get wet inside a baked home. If Vedr is installed and handles indoor wetness itself, Easy Bake steps aside for pieces Vedr draws.

**Snow on baked pieces** - needs a snow mod that connects to Easy Bake (Vedr, FiresTossinShade, or Fires Unified Core's dynamic snow)
- **Without it:** baked pieces stay bare while live ones beside them turn white, or snow shows under roofs.
- **What it does:** gives baked pieces the right snow, and keeps roofed ones clear. With no snow mod, baked pieces draw no snow of this kind.

**Snow caps** - always on
- **Without it:** a baked piece would lose its snow cap, or wear it forever.
- **What it does:** baked pieces leave their snow caps to the game. In the Deep North, and wherever Vedr's winter builds snow on pieces, pieces that can carry snow stay real objects so their look stays right.

**Seasons** - always on
- **Without it:** with a seasons mod, copies could keep the wrong look until reloaded, such as frosted trees in autumn.
- **What it does:** copies follow their source's shader, textures and values.

## What stays a real object

**Never baked**, because they'd break:
- doors, chests, item and armor stands, and shield generators
- mine rocks, feasts, and fires that change with fuel
- glass and other see-through pieces: `ExcludeTransparentPieces` (on)
- anything you list in `ExcludedPrefabs`

**Never skipped:** anything with a job or comfort stays an object, including stations, signs, beds, wards, portals, ladders and chairs. Some, such as a workbench, can still be drawn by a bake while the real object works underneath.

**Damageable pieces** stay real by default. They can be drawn by a bake (`DamageablePieces`, on), but rain wear, support and raids run on them as normal. Skipping them is a separate option, `SkipDamageablePieces` (off), because rain wear and support don't run on an uncreated piece you own.

**Trees and bushes** are left alone by default. `SkipVegetation` (off) draws them as copies. It went off by default after a public report of white trees.

**Pieces that can't be damaged:** pieces that can't take damage skip wear checks by default (`WearShortCircuitInvulnerable`, on), so they don't wear or collapse. The exception is a piece where snow can build: it keeps vanilla's checks, so the snow still grows and melts. `[Batching] VanillaWearOnInvulnerablePieces` (off) puts vanilla back for all of them, which means some that stand today could collapse.

**Edited pieces:**
- Edits to a piece's look are baked as shown: infinite health, new, worn or broken looks, and invisible pieces.
- Edits to text, models or linked objects keep the piece real.
- Number and flag edits don't by themselves, and Structure Tweaks edits keep a piece real only when that mod is installed.

**Other mods' pieces** - `ModdedPrefabs` (Checked), `ModdedPrefabAllowlist`
- **Without it:** baking could break a mod's textures.
- **What it does:** bakes a modded piece only if its materials are safe. Creatures are never baked.

## Smoother play

All under `[Optimize]` and on by default, unless noted. These cut the wasted work that isn't about drawing.

| Setting | What vanilla does | What this does |
|---|---|---|
| `LightBudgetEnabled` | Every nearby build-piece light lights everything, very costly in a lit base | Nearest 8 keep full lighting; the rest use a cheaper mode |
| `LightFlickerLodEnabled` | Every torch runs its flicker every frame | Far torches flicker less often |
| `[Batching] LightFlickerViewCull` | Torches you can't see still flicker at full rate | A light out of view flickers at the slowest rate |
| `[Batching] LightSettingsEvent` | Each light signs up for settings changes on its own | All lights share one, so unloading many lights costs less |
| `[Batching] MinimapFogPartialUpload` | Sends the whole map fog picture when you explore | Sends only the part that changed |
| `ZSFXLodEnabled` | Keeps up sounds you can't hear | Skips it past 60 m; far one-shot sounds still play |
| `[Batching] IdleGates` | Rewrites finished fades; runs particles off screen | Stops both until needed |
| `ClutterLodEnabled` | Refreshes grass every frame | About 3 times a second while you stand still |
| `ArrivalCountsBakedPieces` | The loading screen waits for every piece in the area to be created | Pieces already drawn and solid count as loaded |
| `CreateBudgetPerFrame` (40) | A big backlog creates hundreds of objects in one frame | Spreads them out, without ever getting stuck |
| `PartialCreateSort` | Sorts every waiting object each frame | Sorts only the nearest it will create |
| `SkipUnchangedScans`, `CreateDestroySkipEnabled`, `CreateDestroyDirtyFlag` | Walks every object 30 times a second to find nothing | Skips while nothing changed, and checks itself |
| `DestroyTimeSliceEnabled` | Removes a whole zone in one long frame | Removes a little each frame |
| `SectorMirrorEnabled`, `SectorMirrorSlicedRebuild` | Searches every object for a zone's contents | Keeps a list and looks it up, and refreshes that list a bit at a time |
| `ZSyncStaticSkipEnabled` | Position sync on walls and fixtures that never move | Turns it off. An admin teleporting a static piece won't show for others until reload |
| `RemoteItemZSyncSkipEnabled` | Position sync on others' dropped items at rest | Stops once settled, resumes if it moves |
| `EffectAreaIdleSkipEnabled` | Updates areas that can never do anything | Takes them off the list |
| `[Prewarm] Enabled` | First spawn of a prefab can hitch at a zone border | Spawns the building pieces and vegetation Easy Bake can draw once, hidden, during loading, without running their startup code |

**Off by default:**
- `ZoneKeepaliveEnabled`: keeps baked zones loaded after you leave, using more memory.

**Off by default, not proven yet:**
- `IndirectRenderer`: a newer way of drawing baked pieces, where the graphics card works out what's visible. It uses Easy Bake's own shaders and needs the `ebm_indirect` file next to the mod. See Known issues before turning it on. Keys starting with `Indirect` only matter with this on, except `IndirectGrass`.
- `IndirectGrass`: grass drawn the same way.
- `StreamPacer`: Easy Bake paces object creation itself.
- `FarSmallNoShadows`: no shadows from far or tiny copies.
- `SpreadFirstSightChecks`: spreads the first checks of new piece kinds over frames.
- `BakeIntactRocks`: undamaged rocks share one mesh per kind.
- `InstancedCellSizeMeters` (0), `InstancedCellLod`, `InstancedShadowProxies`, `InstancedInstanceCull`: finer culling and detail for copies.
- `DestroyLeanEarmark`, `FarBuildingsChangedZonesOnly`: cheaper zone-leave and far-base scans.
- `[Far Impostors] Enabled`, `[Far Lights] Enabled`, `[Far Cache] Enabled`: far trees as flat images, far lights, and saving the far view to disk.

**Weak PCs:** `[General] PerformanceProfile` = LowEnd halves the per-frame work and brings far distances in. Zones take longer to bake. Applies on next start.

**For testers:** the `[Diagnostics]` section, the `Verbose` keys and the commands `ebm_opt`, `ebm_indirect`, `ebm_rocks`, `ebm_coverage`, `ebm_drawtest`, `ebm_unload_test`, `ebm_batches`, `ebm_bisect`, `ebm_meshhash` and `ebm_snowcaps` are for finding problems. Players can ignore them.

## Safety nets

- **Respawn or teleport never hangs:** if the area never reads ready at one spot for 30 seconds, you go through anyway, and a warning is logged.
- **Broken shield generators:** a bad one throws an error several times a second forever in vanilla. It's now logged once, and that generator stops updating.
- **Mods replacing the same code:** if another mod already replaced object creation or removal, Easy Bake's own version stands down.
- **Another mod's error:** if a call into another mod fails, you get one warning naming it and Easy Bake carries on without it.
- **A patch that can't apply:** after a game update, for example, that one part turns itself off for the session and the rest keeps working.
- **Damaged meshes:** a mesh Easy Bake builds or loads is checked once. A bad one isn't drawn, and an error is logged.

## Install and settings

<!-- Fire 11-08: "everything needs to work without core." The Core lines here describe the build as it stands. Owner to close each gap, then delete them. -->
- **Needs:** BepInEx only. Fires Unified Core is optional, and Easy Bake has been run without it in single-player testing.
- **What Core adds:** help pages, live reload when you edit the config file, the tidy settings list, and snow on baked pieces if you use its dynamic snow. If you do use Core, use 0.2.1219 or newer; older versions run their own far view, which clashes with this one.
- **Client mod.** Install on players' machines; it's not meant for a dedicated server. A VAngarde server lists `com.Fire.FiresEasyBakeMeshes` in `mod_whitelist.txt`, so players may use any version.
- **Settings:** `BepInEx/config/com.Fire.FiresEasyBakeMeshes.cfg`. With Core, the window shows only `PluginEnabled`, `Enabled` and `ExcludedPrefabs`; the rest are under Show Advanced. Without Core, expect to see them all.
- **Turning it off:** `[General] PluginEnabled` stops the mod right away, including rain hiding, dry insides and the light budget, and hands back what they changed. Far-away bases have their own key, `[Far buildings] Enabled`. Relog after turning it back on.

## Console

Anyone can run these read-only reports:
- `ebm_census`: every loaded object and why it wasn't baked away.
- `ebm_why`: what Easy Bake is doing with the piece you're looking at.
- `ebm_vegetation`: the same report for vegetation.

With devcommands:
- `ebm_rebake [all|here]`: throws away bakes around you, in memory and on disk, to rebuild them.

## Known issues

**Stretched shapes with `IndirectRenderer`.** This setting is off by default and not proven yet. With it on, huge stretched flat triangles or planks have sometimes appeared across part of a town. It doesn't happen every time, and the cause hasn't been found. Recent test runs that didn't show it are not proof it's gone. To stop it, set `[Optimize] IndirectRenderer` to false, or type `ebm_indirect off` in the console for that session. If you've never turned it on, this doesn't affect you.

**Snow on pieces in the Meadows.** Building pieces have sometimes shown snow in the Meadows while flying in, then cleared. The cause hasn't been found.

**A piece turns invisible.** A piece can draw invisible as a copy if its shader doesn't support copies, while its collision stays. This has been seen with FiresTossinShade's piece shader. Easy Bake writes one warning to the log for each such shader. To work around it, add the piece to **ExcludedPrefabs** (for example `stone_floor_2x2`).

## Compatibility

- **Infinity Hammer:** placement ghosts and selections see the real pieces.
- **FiresGhettoNetworkMod:** works alongside. When its time-sliced object creation is on, that replaces the step Easy Bake's create budget acts on, so only one of them paces.
- **Vedr:** works alongside. See Rain and snow.
- **Valheim Community Patch:** works alongside, but if it replaces object removal, Easy Bake's gradual removal and zone keep-alive do nothing.
- **Mods reading pieces through their saved data:** unaffected. The data is never touched.

## Screenshots

<!-- ![A large base drawn by Easy Bake, seen from a distance](HOSTED_URL/FiresEasyBakeMeshes/01_ebm_base.png) -->
