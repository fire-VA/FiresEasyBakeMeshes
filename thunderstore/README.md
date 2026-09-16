# Fires Easy Bake Meshes

**Big builds, normal frame rates.** Easy Bake takes the parts of a base that never change and stops your
machine from treating each one as a separate object: it merges what it can into one mesh per zone, draws the
rest as GPU instances, and on a multiplayer client it stops creating the objects altogether once something
else is drawing them.

Measured on one town of about 135,000 objects, standing in the same spot:

| | objects loaded | frame time |
|---|---|---|
| without Easy Bake | 117,517 | 186 ms (5 FPS) |
| with Easy Bake | 42,220 | 19-22 ms (45-52 FPS) |

The world itself is untouched — the server still holds every piece, your client just stops rebuilding three
quarters of it.

There is no UI and nothing to learn. Install it and the base gets faster.

## What it does

- **Mesh batching.** Structural pieces in a zone that share a material are combined into one mesh, turning
  hundreds of draw calls into a handful. Colliders, building and interaction are untouched.
- **GPU instancing.** Prefabs that come down to one mesh and material - palisades, floor tiles, stakewalls,
  grausten - are drawn as instances instead, which duplicates no vertices and lets a single removed piece
  drop out without rebuilding the zone. Pieces made of several meshes, like vines and tiled roofs, are drawn
  the same way.
- **Skipped piece objects (multiplayer clients).** A piece that a bake already draws does not need to exist
  as an object at all. Easy Bake leaves it uncreated and puts a stand-in collider in its place, so you can
  still walk on it, build against it and stand on it. This is where most of the frame time comes back.
- **Real pieces come back when you need them.** Take out a build tool and everything within reach of it is
  created again, so snapping, highlighting and removing work normally. Damage, removal or a change to a piece
  hands that piece straight back.
- **Distance tier.** A baked zone keeps a second mesh built from each piece's own LOD1 geometry and swaps to
  it past 64 m, far enough that it never happens while you are standing in that zone.
- **Nothing off screen is drawn.** Instanced pieces behind you are skipped for that frame, with their boxes
  stretched along the sun first so their shadows still land on what you can see.
- **Disk cache.** Baked zones are stored per world and reload from disk next session instead of being rebuilt.
- **Prefab prewarm.** Every prefab is instantiated once while the game loads, paying the hidden first-spawn
  shader cost up front instead of hitching at a zone border.
- **Steady-state trims.** Time-sliced object destruction, wear-and-tear short circuits for pieces that cannot
  be damaged, distance LOD on light flicker, sound and clutter updates, and sync skips for static pieces.

## What is never touched

Anything that does something stays a real object: doors, chests, item and armor stands, glass, shields,
mine rocks, workbenches and station extensions, signs, fireplaces, beds, wards, portals, ladders, chairs and
anything that gives comfort. Pieces that can be damaged stay in the world too - they are drawn by the bake,
but rain wear, support and raids run on them exactly as before, and a piece goes back to drawing itself the
moment it is hit, burns or is highlighted by a hammer.

Per-piece edits are respected as well. A piece with an edited name, hover text or model is always created, so
the edit still shows; edited numbers like health or wear are left to the bake, since a piece that is not
there cannot wear or fall, and the value is applied again whenever it is created.

## Requirements

- BepInEx
- FiresUnifiedCore
- Client side. Skipping piece objects only happens when you are a client on a server; on a host or a
  single-player world the pieces are always created, because that machine is the one simulating them.

## Settings

Everything is under `[Batching]` in `BepInEx/config/com.Fire.FiresEasyBakeMeshes.cfg`, and the defaults are
what the numbers above were measured with. The ones worth knowing:

- **SkipBakedPieceObjects** - the big one. Off means pieces are still drawn by the bake but always created.
- **BuildToolRadiusMeters** (24) - how far around you real pieces come back while a build tool is out.
- **InstancingEnabled**, **MinInstancesPerPrefab** (10) - how many copies of a prefab a zone needs before
  they are drawn as instances.
- **FarTierDistanceMeters** (64) - where a zone swaps to its distance mesh.
- **SkipInstancesOutOfView** - leave instanced pieces out of a frame when they are behind you.
- **DamageablePieces** - also draw pieces that can be damaged. They are never skipped, only drawn.
- **ExcludeTransparentPieces** - glass and other transparent pieces are left alone, because merged
  transparency sorts once for the whole batch and would draw through walls.
- **PluginEnabled** under `[General]` switches the whole mod off live, without a restart.

## Console

`ebm_census` lists every object loaded around you and why each one was not baked away, grouped by reason with
the most common prefabs behind each. Useful when a base feels heavier than it should.

## Compatibility

Works alongside Infinity Hammer (its placement ghosts and selections see the real pieces), Valheim Community
Patches and FiresGhettoNetworking. Any mod that reads or writes pieces through their ZDO is unaffected: the
data is never touched, only whether your own machine builds an object for it.
