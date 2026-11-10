# Fires Easy Bake Meshes

**Big builds, normal frame rates.** In a big base, your machine treats every wall, floor and beam as its own object, and the frame rate collapses. Easy Bake finds the parts that never change and draws them cheaply. The world itself is untouched: the server still holds every piece, and only your own game does less work.

There's nothing to learn. Install it and big bases get faster. Current version: 1.2.168.

## How it speeds up a base

**Merged pieces** - `[Batching] Enabled` (on)
- **Without it:** a base is thousands of separate draws.
- **What it does:** when a zone loads, pieces sharing a material are combined into one mesh. Colliders, building and interaction still work.

**Repeated pieces drawn as copies** - `InstancingEnabled` (on), `MinInstancesPerPrefab` (10)
- **Without it:** palisades, floor tiles, vines and tiled roofs each cost their own draw.
- **What it does:** draws them as copies of one model. Removing one piece just drops it out, with no rebuild of the zone.

**Pieces that don't need to exist** - `SkipBakedPieceObjects` (on)
- **Without it:** every piece stays a full object, even when something else draws it. This is the biggest cost left in a dense town.
- **What it does:** on a multiplayer client, a plain structural piece already being drawn is never created. An invisible stand-in of the same shape takes its place, so you still walk on it and arrows still hit it.
- **Hosts and single player:** skipping stays off, because that machine runs the world. `SkipOnHost` turns it on; it's off by default because a piece that loses support while skipped keeps standing until it's created again.

**Real pieces come back when you need them** - `BuildToolRadiusMeters` (24)
- **What it does:** with a build tool out, real pieces return within this distance, so snapping, highlighting and removing work. Damage, burning, a change of look or removal also brings a piece straight back.
- **Hits on skipped pieces:** a hit on an uncreated piece you own is held, the piece is brought back, and the hit is then applied.

**Far zones get simpler** - `FarTierEnabled` (on), `FarTierDistanceMeters` (64)
- Baked zones switch to a lower-detail mesh past this distance, measured to the zone's nearest point.

**Off-screen copies skipped** - `SkipInstancesOutOfView` (on)
- Copies behind you aren't drawn, but their shadows still land on what you can see.

**Bakes saved to disk** - `[Cache] PersistToDisk` (on)
- Baked zones reload next session instead of rebuilding. A saved bake made with different settings is rejected. Old zones you've left are released from memory after 10 minutes.

## Rain and snow

**No rain indoors** - always on
- **Without it:** walk into a house during rain and it keeps falling through the roof, because vanilla only rechecks when the weather changes.
- **What it does:** stops falling weather while you're sheltered, and starts it again outside. Fog and mist stay.

**Dry insides** - always on
- **Without it:** roofed pieces and roof undersides look soaked from inside.
- **What it does:** draws roofed pieces dry while it rains. Stand-ins also copy what vanilla needs to tell whether you're sheltered, so you don't get wet inside a baked home.

**Snow on baked pieces** - needs Fires Unified Core with dynamic snow
- **Without it:** baked pieces stay bare while live ones beside them turn white, or snow shows under roofs.
- **What it does:** gives baked pieces the right snow, and keeps roofed ones clear. In the Deep North, pieces carrying snow stay real objects so their look stays right.

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

**Pieces that can't be damaged:** pieces that can't take damage skip wear checks by default (`WearShortCircuitInvulnerable`, on), so they don't wear or collapse. `VanillaWearOnInvulnerablePieces` puts vanilla back.

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
| `ZSFXLodEnabled` | Keeps up sounds you can't hear | Skips it past 60 m; far one-shot sounds still play |
| `[Batching] IdleGates` | Rewrites finished fades; runs particles off screen | Stops both until needed |
| `ClutterLodEnabled` | Refreshes grass every frame | About 3 times a second while you stand still |
| `CreateBudgetPerFrame` (40) | A big backlog creates hundreds of objects in one frame | Spreads them out, without ever getting stuck |
| `PartialCreateSort` | Sorts every waiting object each frame | Sorts only the nearest it will create |
| `SkipUnchangedScans`, `CreateDestroySkipEnabled` | Walks every object 30 times a second to find nothing | Skips while nothing changed, and checks itself |
| `DestroyTimeSliceEnabled` | Removes a whole zone in one long frame | Removes a little each frame |
| `SectorMirrorEnabled` | Searches every object for a zone's contents | Keeps a list and looks it up |
| `ZSyncStaticSkipEnabled` | Position sync on walls and fixtures that never move | Turns it off. An admin teleporting a static piece won't show for others until reload |
| `RemoteItemZSyncSkipEnabled` | Position sync on others' dropped items at rest | Stops once settled, resumes if it moves |
| `EffectAreaIdleSkipEnabled` | Updates areas that can never do anything | Takes them off the list |
| `[Prewarm] Enabled` | First spawn of a prefab can hitch at a zone border | Spawns most prefabs once, hidden, during loading |

**Off by default:**
- `FarSmallNoShadows`: no shadows from far or tiny copies. Not tested yet.
- `SpreadFirstSightChecks`: spreads the first checks of new piece kinds over frames. Not tested yet.
- `ZoneKeepaliveEnabled`: keeps baked zones loaded after you leave, using more memory.

**Weak PCs:** `[General] PerformanceProfile` = LowEnd halves the per-frame work and brings far distances in. Zones take about twice as long to bake. Applies on next start.

## Safety nets

- **Respawn or teleport never hangs:** if the area never reads ready at one spot for 30 seconds, you go through anyway, and a warning is logged.
- **Broken shield generators:** a bad one throws an error several times a second forever in vanilla. It's now logged once, and that generator stops updating.
- **Mods replacing the same code:** if another mod already replaced object creation or removal, Easy Bake's own version stands down.

## Install and settings

- **Needs:** BepInEx, and Fires Unified Core for now. Core gives snow on baked pieces, help pages, live config reload and the tidy settings list. This build no longer links to Core directly, but running without it hasn't been tested yet.
- **Client mod.** Install on players' machines; it's not meant for a dedicated server. A VAngarde server lists `com.Fire.FiresEasyBakeMeshes` in `mod_whitelist.txt`, so players may use any version.
- **Settings:** `BepInEx/config/com.Fire.FiresEasyBakeMeshes.cfg`. With Core, the window shows only `PluginEnabled`, `Enabled` and `ExcludedPrefabs`; the rest are under Show Advanced.
- **Turning it off:** `[General] PluginEnabled` stops most of the mod right away. Rain hiding and dry insides keep running, and so does the light budget, which has its own key, `LightBudgetEnabled`. Relog after turning it back on.

## Console

Anyone can run these read-only reports:
- `ebm_census`: every loaded object and why it wasn't baked away.
- `ebm_why`: what Easy Bake is doing with the piece you're looking at.
- `ebm_vegetation`: the same report for vegetation.

With devcommands:
- `ebm_rebake [all|here]`: throws away bakes around you, in memory and on disk, to rebuild them.
- `ebm_drawtest` and `ebm_unload_test`: test tools. The second teleports you 1500 m away and back.

## If a piece turns invisible

A piece can draw invisible as a copy if its shader doesn't support copies, while its collision stays. This has been seen with FiresTossinShade's piece shader. Easy Bake writes one warning to the log for each such shader. To work around it, add the piece to **ExcludedPrefabs** (for example `stone_floor_2x2`).

## Compatibility

- **Infinity Hammer:** placement ghosts and selections see the real pieces.
- **FiresGhettoNetworking:** works alongside.
- **Valheim Community Patch:** works alongside, but if it replaces object removal, Easy Bake's gradual removal and zone keep-alive do nothing.
- **Mods reading pieces through their saved data:** unaffected. The data is never touched.

## Screenshots

<!-- ![A large base drawn by Easy Bake, seen from a distance](HOSTED_URL/FiresEasyBakeMeshes/01_ebm_base.png) -->
