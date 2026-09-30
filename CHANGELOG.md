* v1.2.77 - smoother loading into big bases, less idle work
  - walking or teleporting into a dense town no longer stalls on object creation: CreateBudgetPerFrame stands down once the queue is clearly not draining (new CreateBudgetReleaseAbove, default 8000), and it is lifted while you teleport
  - a respawn or teleport that has waited 30 s for its area goes ahead anyway instead of holding the loading screen
  - less work every frame in built-up worlds: effect areas that can never do anything leave the update list, dropped items that have landed stop re-syncing their position, stations and fixtures that never move (signs, smelters, portals, beds and the like) stop position updates, and the search for objects still missing a model is skipped while nothing has changed. All on by default: EffectAreaIdleSkipEnabled, RemoteItemZSyncSkipEnabled, ZSyncStaticSkipFixtures, SkipUnchangedScans
  - a removed or destroyed piece no longer leaves an invisible collider behind, and stand-in colliders are only made for pieces the server still has
  - new experimental settings, all off: SkipDamageablePieces, SkipVegetation, SkipOnHost
  - diagnostics: the log explains a create queue over 500 deep (CandidateCensusSeconds, sampled so it stays cheap), and an off-by-default ZDO bake verifier (ZdoBakeVerifyEnabled)
  - needs Fires Unified Core 0.2.219 or newer

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
