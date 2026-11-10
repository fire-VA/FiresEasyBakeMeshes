# Fires Easy Bake Meshes

**Big builds, normal frame rates.** Easy Bake takes the parts of a base that never change and draws them in a much
cheaper way, so huge bases and towns stop dragging your frame rate down. Install it and your bases get faster -
there is nothing to learn.

The world itself is untouched - the server still holds every piece. Only your own game draws them more cheaply.

Easy Bake works on its own. It doesn't need Fires Unified Core or any other Fires mod, and it doesn't rely on
Fires Fixes or Fires Ghetto Networking either. Other mods can connect to it if they want to, but nothing is required.

## What's new in 1.2.262

Changes since 1.2.159, in plain words:

- **No other mod needed.** Easy Bake no longer needs Fires Unified Core. Anything that used to come from another mod
  now either lives inside Easy Bake or is skipped quietly when that mod isn't there.
- **Its own building look.** Easy Bake now has its own shader for building pieces. Baked pieces keep the wet look in
  rain, snow in winter and the dry look under a roof without needing a separate snow or weather mod.
- **Distant bases show up sooner.** Other players' builds beyond the loaded area are drawn by Easy Bake as simple
  far versions. They now appear within the first seconds after you load in or teleport, instead of popping in later.
  Far buildings are white only where snow has really built up (Mountains, Deep North, or a snowy season), not in summer.
- **Smoother object loading.** Easy Bake now paces how fast objects are created around you, based on how quickly your
  own machine is running. It keeps up while you walk, ride or fly, and after a teleport. This job used to belong to
  Fires Ghetto Networking; that mod steps aside on its own when Easy Bake is doing it.
- **Snow on baked pieces behaves like snow on normal ones.** Snow builds up the same way, pieces under a roof stay
  dry, and rain and snow stop indoors while you're sheltered. Each piece waits a moment for its real snow amount
  instead of using the area's average.
- **Stretched or broken pieces are fixed.** These showed up mostly with shadows on. Easy Bake now checks every mesh
  it builds or loads once, and a bad one is left out (and named in the log) instead of being drawn wrong.
- **Big cities load much faster.** In very large towns, saved bakes were thrown away and rebuilt while pieces were
  still arriving from the server, which could take minutes. That no longer happens.
- **Fewer hitches.** A short freeze once a minute is now spread out over many frames, another one every 30 seconds
  is gone, and rocks, trees and item stands are no longer redone over and over while you stand still in a town.
- **Safer alongside other mods.** If something from another mod throws an error inside Easy Bake, you get one
  warning and Easy Bake carries on in its own way. A graphics driver crash on the new drawing path turns that path
  off for the rest of the session instead of crashing again.
- **Turning it off really turns it off.** The main on/off switch now also stops the rain hider, the wet look, the
  shelter check, the light budget and the item skip, and puts back what they changed.
- **New optional extras, off unless you turn them on:** a new way of drawing baked buildings, rocks and trees where
  the graphics card picks what is in view and draws it in a few large batches ("Indirect Renderer"), and untouched
  rocks sharing one model per rock type with a simpler one in the distance ("Bake Intact Rocks"; the first hit gives
  the rock its normal model back).

**One slower first load after updating.** The saved bake format changed, so every area is baked fresh once the
first time you load in. After that it loads from disk as before. Your world save is not touched.

## What you get

- **Smoother big bases.** Walls, floors, roofs and other plain building pieces are merged and drawn together.
- **Building works normally.** Take out the hammer and the real pieces around you come back, so snapping,
  highlighting and removing work as usual. A piece that is hit, burns or is changed goes back to normal at once.
- **Faster loading.** Baked areas are saved and load from disk next time.
- **Distant bases.** Builds beyond the loaded area are drawn as simple far versions, so the horizon isn't empty.
- **Less stutter** when crossing into new areas and in large bases.

## What is never touched

Anything you interact with stays a normal object: doors, chests, item stands, workbenches, fires, beds, wards,
portals, signs, ladders, chairs, feasts, fuel-burning pieces like hanging braziers, and creatures. Glass is left
alone, and map makers' edits are respected (the full readme has the details). Rain wear, support and raids work as
before on pieces that can take damage.

## How to use

Just install it. By default you only see three settings; everything else is under **Advanced**:

- the on/off switch
- baking on/off
- **ExcludedPrefabs** - add any piece that ever looks wrong (for example `stone_floor_2x2`) and it is left alone

You can change them in `BepInEx/config`, in your mod manager's config editor, or in an in-game config window
(with Fires Unified Core installed, that's the Fires config window on F1 or Esc > Mod Settings).

Console commands (handy when a base feels heavier than it should; they change nothing):

- `ebm_census` - lists what's loaded around you and why it wasn't sped up
- `ebm_vegetation` - the same for plants and trees
- `ebm_why` - tells you what Easy Bake is doing with the piece you are looking at

For trying settings out, `ebm_indirect on|off|status` and `ebm_opt <setting> on|off|status|list` switch a setting
for this session only, without writing it to your config.

## Install

- **For players' games.** Install it on each player's machine. That's where all the drawing happens.
- **On a server** it only enforces its config; it doesn't draw or bake anything there.
- The biggest gains are when you play on a server. On a single-player world or when hosting, pieces are still
  drawn faster, but not skipped.
- The package includes a second file, `ebm_indirect`, next to the DLL. Keep the two together. It is only used by the
  optional Indirect Renderer.

## Requires

- BepInExPack for Valheim. Nothing else.

## Compatibility

- Works on its own and doesn't rely on any other mod, including the other Fires performance mods (Fires Fixes and
  Fires Ghetto Networking). You can run any of them alone or together.
- Works alongside Infinity Hammer and Valheim Community Patches. Mods that read or change pieces are unaffected -
  the piece data is never touched.
- With the weather mod Vedr installed, Easy Bake leaves the indoor wet look to Vedr.

## Full readme

Every feature, setting and what stays a real object: [full readme on GitHub](https://github.com/fire-VA/FiresEasyBakeMeshes#readme).

## Screenshots

<!-- ![A large base drawn by Easy Bake, seen from a distance](HOSTED_URL/FiresEasyBakeMeshes/01_ebm_base.png) -->
