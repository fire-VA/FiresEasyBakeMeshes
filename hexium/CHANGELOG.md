## 1.2.113
- Pieces removed from a build (for example by an admin) no longer leave an old baked copy behind.

## 1.2.112
- Cleaner settings screen: only the basics show, the rest is under Show Advanced (your saved values are kept).
- Fixed the in-game help: settings are under F1 > Config settings or Esc > Mod Settings (not F8), and it lists the ebm_census, ebm_vegetation, ebm_why and ebm_rebake commands.
- Test commands now need devcommands.
- Needs Fires Unified Core 0.2.586.

## 1.2.101
- Fixed building pieces (stone floors first) turning invisible until you took out the hammer.
- Needs Fires Unified Core 0.2.586.

## 1.2.100
- Lower memory use: hidden zone bakes are released after a while.
- Far detail distance is measured to the nearest edge of a zone instead of its centre, and creatures are never baked.
- Logging out is about 2 seconds faster.
- Caches move to BepInEx\cache\FiresEasyBakeMeshes on first run (copied, nothing deleted).
- Needs Fires Unified Core 0.2.547.

## 1.2.89
- Smoother big bases.
- Zone keep-alive is now off by default (turn it on for a base you walk in and out of).
- Fixes and optimizations.

## 1.2.77
- Smoother loading into big bases.
- Worn, damaged and invisible pieces are baked with the look they show.
- Pieces edited by map makers or Structure Tweaks stay real, so adventure maps work as built.
- More of a big build gets baked, including admin-placed ruins and decoration.
- New CreateBudgetPerFrame setting spreads object creation over a few frames in dense areas.
- New ExcludedPrefabs setting for anything the bake draws wrong.
- No more once-a-second stutter in large bases, and holding a hammer costs less.
- Status lines moved into the shared Fires status box.

## 1.2.15
- Optimizations for Valheim 1.0.
