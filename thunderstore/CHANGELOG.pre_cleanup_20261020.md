## 1.2.112
- The rig-proven build of 2026-10-20.

## 1.2.110
- The rig-proven build of 2026-10-20.

## 1.2.101
- Fix: building pieces (stone floors first) could turn invisible, collision still there, until the hammer was out, after another mod released idle prefab meshes. Those zones now hand drawing back to the pieces and bake again later.
- Requires FiresUnifiedCore 0.2.586.

## 1.2.100
- Lower memory use: hidden zone bakes are released after 10 minutes or once more than 32 are held.
- Far LOD distance is measured to the zone nearest edge, not its centre; creatures are never baked.
- Logging out is about 2 s faster.
- Caches move from BepInEx\config to BepInEx\cache\FiresEasyBakeMeshes on first run (copied and verified, nothing deleted).
- Built and tested on the 10-15 rig. Requires FiresUnifiedCore 0.2.547.

* v1.2.89 - updates and fixes
  - fixes and optimizations

* v1.2.86
  - smoother big bases, and zone keep-alive is now off by default (turn it on for a base you walk in and out of)

* v1.2.77
  - smoother loading into big bases

* v1.2.30 - quieter logs
  - EBM's periodic log lines moved into the shared Fires status box (needs FiresUnifiedCore 0.2.35)

* v1.2.29 - bakes what you actually see, smoother loading
  - worn, damaged and invisible pieces are baked with the look they show, and invisible pieces keep only their collision
  - pieces with map-maker field edits or Structure Tweaks overrides stay real, so adventure maps work as built
  - more of a big build gets skipped, including admin-placed ruins and decoration
  - new CreateBudgetPerFrame setting spreads object creation over a few frames when you walk into a dense area
  - new ExcludedPrefabs setting for anything the bake draws wrong
  - no more once-a-second stutter in large bases, and holding a hammer costs less
  - every zone rebakes once after updating, and it needs Fires Unified Core 0.2.29 or newer

* v1.2.15 - 1.0 optimizations

* v1.1.0 - updated for Valheim 1.0

* v1.0.4 - maintenance and fixes
