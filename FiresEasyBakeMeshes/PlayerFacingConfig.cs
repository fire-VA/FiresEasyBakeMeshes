using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresEasyBakeMeshes
{
    // 1.2.110 (Fire 10-20, team rule "player-facing config"): what a player sees in the config window is short and basic.
    // After every key is bound: each gets one short plain line, and every key except the few basics is marked Advanced
    // (Core's window and ConfigurationManager hide Advanced keys until "Show Advanced" is on). The long developer notes
    // stay in the code next to each Bind. Keys and defaults are unchanged.
    internal static class PlayerFacingConfig
    {
        // Shown without "Show Advanced".
        private static readonly HashSet<string> Basics = new HashSet<string>
        {
            "01 - General/PluginEnabled",
            "02 - Building Bakes/Enabled",
            "02 - Building Bakes/ExcludedPrefabs",
            // 1.2.458 (Fire 11-18): the far rendering master switch and one toggle per far renderer (bound later, in Plugin Awake's far block; the
            // Advanced predicate reads this set lazily, so they show without "Show Advanced"). Their own descriptions are already short.
            "03 - Far Rendering/Far Rendering",
            "03 - Far Rendering/Far Terrain",
            "03 - Far Rendering/Far Trees",
            "03 - Far Rendering/Far Zone Sync",   // 1.2.484: replaces "Far Trees Own Placement" (retired: "Far Trees" itself places EBM's own trees)
            "03 - Far Rendering/Far Rocks",
            "03 - Far Rendering/Far Buildings",
            "03 - Far Rendering/Far Lights",
        };

        private static readonly Dictionary<string, string> Lines = new Dictionary<string, string>
        {
            ["01 - General/PluginEnabled"] = "Turn Easy Bake Meshes on or off.",
            ["01 - General/Verbose Zone Logging"] = "Log a line for every zone baked or loaded from cache.",
            ["02 - Building Bakes/Enabled"] = "Merge big builds into fewer draws for better frame rates.",
            ["02 - Building Bakes/RunOnServer"] = "Also bake on a dedicated server (not needed normally).",
            ["02 - Building Bakes/MinPiecesPerZone"] = "Fewest pieces a zone needs before it is baked.",
            ["02 - Building Bakes/MinPiecesPerBatch"] = "Fewest pieces that share a material before they are merged.",
            ["02 - Building Bakes/InstancingEnabled"] = "Draw repeated pieces with GPU instancing.",
            ["02 - Building Bakes/SkipInstancesOutOfView"] = "Skip drawing baked groups that are off screen.",
            ["02 - Building Bakes/MultiPartInstancing"] = "Allow pieces made of several meshes to be instanced.",
            ["02 - Building Bakes/MinInstancesPerPrefab"] = "Fewest copies of a piece before it is instanced.",
            ["02 - Building Bakes/DamageablePieces"] = "Also bake pieces that can be damaged.",
            ["02 - Building Bakes/FarTierEnabled"] = "Use lighter meshes for baked zones far away.",
            ["02 - Building Bakes/FarTierDistanceMeters"] = "Distance where baked zones switch to lighter meshes.",
            ["26 - Performance - Bakes And Cache/ClassifyBudgetMs"] = "Time per frame spent sorting pieces before a bake.",
            ["26 - Performance - Bakes And Cache/SuppressBudgetMs"] = "Time per frame spent hiding pieces after a bake.",
            ["26 - Performance - Bakes And Cache/SettleDelaySeconds"] = "Wait this long after building stops before baking.",
            ["90 - Debugging/VerboseLogging"] = "Log details of every bake.",
            ["02 - Building Bakes/ExcludeTransparentPieces"] = "Leave see-through pieces (glass) out of bakes.",
            ["02 - Building Bakes/ModdedPrefabs"] = "How pieces from other mods are handled.",
            ["02 - Building Bakes/ModdedPrefabAllowlist"] = "Other mods' pieces that may always be baked.",
            ["02 - Building Bakes/ExcludedPrefabs"] = "Pieces that are never baked (comma-separated names).",
            ["02 - Building Bakes/SkipBakedPieceObjects"] = "Don't create objects for pieces a bake already draws.",
            ["02 - Building Bakes/SkipOnHost"] = "Also skip creating baked pieces when hosting.",
            ["02 - Building Bakes/SkipDamageablePieces"] = "Also skip creating damageable baked pieces.",
            ["20 - Performance - Loading And Streaming/CreateDestroyDirtyFlag"] = "Skip the nearby-object scan while nothing changed.",
            ["21 - Performance - Lights, Fires And Sound/LightSettingsEvent"] = "Cheaper unloading of many lights.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerViewCull"] = "Update torch and fire flicker less often when out of view.",
            ["23 - Performance - World Objects/MinimapFogPartialUpload"] = "Cheaper map updates while exploring.",
            ["02 - Building Bakes/SkipVegetation"] = "Also skip creating baked trees and rocks.",
            ["02 - Building Bakes/BuildToolRadiusMeters"] = "Pieces this close show as real pieces while building.",
            ["11 - Prewarm/Enabled"] = "Warm up prefabs at load to avoid hitches later.",
            ["11 - Prewarm/RunOnServer"] = "Also warm up prefabs on a dedicated server.",
            ["11 - Prewarm/PrefabsPerFrame"] = "Prefabs warmed up per frame.",
            ["11 - Prewarm/VerboseLogging"] = "Log every prefab warmed up.",
            ["11 - Prewarm/SkipNameContains"] = "Skip prefabs whose names contain these words.",
            ["11 - Prewarm/FrameBudgetMs"] = "Time per frame spent warming up.",
            ["11 - Prewarm/SlowInstantiateThresholdMs"] = "Prefabs slower than this are skipped next time.",
            ["11 - Prewarm/MaxTransformCount"] = "Skip prefabs bigger than this many parts.",
            ["26 - Performance - Bakes And Cache/MaterialRegistryFrameBudgetMs"] = "Time per frame spent reading the bake cache.",
            ["09 - Disk Cache/PersistToDisk"] = "Save bakes to disk so zones load faster next time.",
            ["90 - Debugging/CreationCensusEnabled"] = "Log why each loaded object was created (debugging).",
            ["20 - Performance - Loading And Streaming/CreateBudgetPerFrame"] = "Most objects created per frame.",
            ["20 - Performance - Loading And Streaming/CreateBudgetReleaseAbove"] = "Stop holding creation above this many queued objects.",
            ["20 - Performance - Loading And Streaming/PartialCreateSort"] = "Sort only the nearest waiting objects each frame (faster with long create queues).",
            ["20 - Performance - Loading And Streaming/ObjectStreamPacer"] = "Create nearby objects from one queue, sized to what this PC can do each frame.",
            ["20 - Performance - Loading And Streaming/StreamPacerMotionByPosition"] = "Treat any change of position as moving when pacing object creation.",
            ["26 - Performance - Bakes And Cache/LateJoinFromZdo"] = "Add new pieces to a baked zone without creating them first.",
            ["20 - Performance - Loading And Streaming/StreamPacerVanillaFloor"] = "While moving, create objects at the game's own rate plus spare frame time.",
            ["26 - Performance - Bakes And Cache/RemoteZoneValidation"] = "Check a saved zone against the server only once its objects have all arrived.",
            ["26 - Performance - Bakes And Cache/SaveOffThread"] = "Write saved zones on a background thread.",
            ["25 - Performance - Far View/FarHandoffCountsSkipped"] = "Remove a zone's far copy as soon as its buildings are drawn.",
            ["24 - Performance - GPU Drawing/IndirectCrashFallback"] = "After a graphics driver crash, use the older drawing path for one session.",
            ["23 - Performance - World Objects/ShareAwakePropertyBlock"] = "Building pieces share one unused property block instead of one each.",
            ["26 - Performance - Bakes And Cache/SaveSerializeOffThread"] = "Prepare saved zone files on a background thread too.",
            ["20 - Performance - Loading And Streaming/StreamPacerMotionFromReference"] = "Tell moving from standing still by where the game loads the world around you.",
            ["20 - Performance - Loading And Streaming/StreamPacerUnfocusedBoostOnlyWhenThrottled"] = "Use the bigger background loading budget only when the game is really slowed behind another window.",
            ["20 - Performance - Loading And Streaming/StreamPacerMovingCap"] = "While moving, keep up with the game's own object rate and add extras only within a small per-frame time cap.",
            ["20 - Performance - Loading And Streaming/StreamPacerViewOrder"] = "Create nearby objects in front of you first and behind you last.",
            ["20 - Performance - Loading And Streaming/StreamPacerRealCostCap"] = "While standing still, size a create pass by what real objects cost.",
            ["09 - Disk Cache/SeparateRoleFolders"] = "Keep saved zones apart for hosting and for joining as a client.",
            ["26 - Performance - Bakes And Cache/CacheLowGarbage"] = "Make less temporary memory while saving caches.",
            ["09 - Disk Cache/RejectChangedPrefabs"] = "Rebuild a saved zone once when one of its building models changed.",
            ["09 - Disk Cache/EmptyZoneDropsCache"] = "Remove the saved bake of a fully demolished zone.",
            ["09 - Disk Cache/KeepOtherSettings"] = "Keep saved zones made under other settings on disk.",
            ["09 - Disk Cache/PreloadNearOnly"] = "Keep only nearby saved zones in memory; far zones may rebuild after a teleport.",
            ["09 - Disk Cache/PreloadSlim"] = "Keep a saved zone in memory only until it is built; saves RAM.",
            ["26 - Performance - Bakes And Cache/ZdoCombineIdleRelease"] = "Free the first-visit bake's model copies when they are not in use; saves RAM.",
            ["09 - Disk Cache/MaxMBPerWorld"] = "Disk space for one world's saved zones (0 = no limit).",
            ["09 - Disk Cache/MaxMBAllWorlds"] = "Disk space for all worlds' saved zones (0 = no limit).",
            ["26 - Performance - Bakes And Cache/CoverAfterBakeEnds"] = "Draw pieces a bake gives back through the faster path again.",
            ["23 - Performance - World Objects/CoverageBatchedStart"] = "Hand new trees and rocks to the faster path in one pass per frame.",
            ["20 - Performance - Loading And Streaming/StreamPacerBaseMs"] = "Least time per frame for creating objects when the PC has no headroom.",
            ["20 - Performance - Loading And Streaming/StreamPacerMinPerFrame"] = "Fewest objects a create pass may be limited to.",
            ["20 - Performance - Loading And Streaming/StreamPacerEveryFramePass"] = "While objects are waiting, create them every frame, not 30 times a second.",
            ["20 - Performance - Loading And Streaming/StreamPacerExtraPasses"] = "Also create waiting objects between the game's own ticks while you move.",
            ["23 - Performance - World Objects/TickGroupEventDriven"] = "Re-check shelter, wet and light work only when something changed.",
            ["24 - Performance - GPU Drawing/IndirectCellReusePerCell"] = "When objects appear, re-check only their part of the world for drawing.",
            ["90 - Debugging/BakeFrameSplit"] = "With debug logging on, log where the slowest zone work went.",
            ["26 - Performance - Bakes And Cache/BakePassBudgetMs"] = "Longest time zone baking checks may run in one frame (0 = no limit).",
            ["24 - Performance - GPU Drawing/IndirectSyncCleanup"] = "Update changed building groups with less work while zones load.",
            ["90 - Debugging/StreamPacerSelfCheck"] = "Test tool: compare the create queue with the game's own list (debug log).",
            ["20 - Performance - Loading And Streaming/IdleObjectAreaSkip"] = "Skip the scan of every nearby object when nothing needs creating or removing.",
            ["20 - Performance - Loading And Streaming/EventDrivenObjectArea"] = "Keep the nearby-object area from events, with a full scan about once a second.",
            ["20 - Performance - Loading And Streaming/EventAreaFullPassSeconds"] = "Longest time between full scans of the nearby-object area.",
            ["20 - Performance - Loading And Streaming/DestroyMoversFirst"] = "When leaving an area, remove moving objects at once and loose items before floors.",
            ["20 - Performance - Loading And Streaming/RemovalIdleScanLimit"] = "Check for objects to remove at most 10 times a second while idle.",
            ["20 - Performance - Loading And Streaming/TeleportCreateGate"] = "While teleporting, create objects early but only over ground that has loaded.",
            ["25 - Performance - Far View/DistantPrefabStreaming"] = "Show the far objects other mods register.",
            ["90 - Debugging/PieceTypeAudit"] = "Log build pieces that load in the wrong order (debugging).",
            ["90 - Debugging/ZoneLoadDiagnostics"] = "Log what each zone crossing cost (debugging).",
            ["90 - Debugging/CandidateCensusSeconds"] = "How often to log the bake candidate count.",
            ["90 - Debugging/CandidateCensusSample"] = "How many objects that census looks at.",
            ["20 - Performance - Loading And Streaming/CreateDestroySkipEnabled"] = "Skip object scans while you stand still.",
            ["20 - Performance - Loading And Streaming/CreateDestroySkipMaxSeconds"] = "Longest time scans may be skipped.",
            ["90 - Debugging/CreateDestroySkipVerbose"] = "Log how many scans were skipped.",
            ["23 - Performance - World Objects/WearShortCircuitInvulnerable"] = "Skip wear checks on pieces that can't break.",
            ["20 - Performance - Loading And Streaming/ZoneKeepaliveEnabled"] = "Keep nearby zones loaded longer.",
            ["20 - Performance - Loading And Streaming/ZoneKeepaliveRadius"] = "How many zones around you stay loaded.",
            ["20 - Performance - Loading And Streaming/ZoneKeepaliveSeconds"] = "How long zones stay loaded after you leave.",
            ["90 - Debugging/ZoneKeepaliveVerbose"] = "Log zone keep-alive activity.",
            ["20 - Performance - Loading And Streaming/DestroyTimeSliceEnabled"] = "Spread object removal over several frames.",
            ["20 - Performance - Loading And Streaming/DestroyTimeSliceBudgetMs"] = "Time per frame spent removing objects.",
            ["90 - Debugging/DestroyTimeSliceVerbose"] = "Log object removal activity.",
            ["20 - Performance - Loading And Streaming/SectorMirrorEnabled"] = "Faster lookups of nearby objects.",
            ["20 - Performance - Loading And Streaming/SectorMirrorRebuildSeconds"] = "How often that lookup table is rebuilt.",
            ["90 - Debugging/SectorMirrorVerbose"] = "Log lookup table rebuilds.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerLodEnabled"] = "Update far torch and fire flicker less often.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerLodDistance"] = "Lights closer than this flicker at full rate.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerMidDistance"] = "Distance for the middle flicker rate.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerFarDistance"] = "Distance for the far flicker rate.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerMidRate"] = "Flicker updates per second at middle distance.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerFarRate"] = "Flicker updates per second far away.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerDistantRate"] = "Flicker updates per second very far away.",
            ["21 - Performance - Lights, Fires And Sound/LightFlickerTierCheckRate"] = "How often a light's distance is checked.",
            ["21 - Performance - Lights, Fires And Sound/ZSFXLodEnabled"] = "Skip sound updates for far away objects.",
            ["21 - Performance - Lights, Fires And Sound/ZSFXLodDistance"] = "Sounds farther than this update less often.",
            ["90 - Debugging/ZdoBakeVerifyEnabled"] = "Double-check bakes against world data (debugging).",
            ["23 - Performance - World Objects/RemoteItemZSyncSkipEnabled"] = "Skip syncing items lying still on the ground.",
            ["23 - Performance - World Objects/ZSyncStaticSkipEnabled"] = "Skip syncing pieces that never move.",
            ["23 - Performance - World Objects/EffectAreaIdleSkipEnabled"] = "Skip idle area-effect checks.",
            ["20 - Performance - Loading And Streaming/SkipUnchangedScans"] = "Skip scans when nothing changed.",
            ["23 - Performance - World Objects/ZSyncStaticSkipFixtures"] = "Also skip syncing fixtures that never move.",
            ["90 - Debugging/ZSyncStaticSkipVerbose"] = "Log static sync skipping.",
            ["22 - Performance - Grass, Stands And Signs/ClutterLodEnabled"] = "Skip grass updates while you stand still.",
            ["22 - Performance - Grass, Stands And Signs/ClutterLodMoveThreshold"] = "How far you move before grass updates again.",
            ["22 - Performance - Grass, Stands And Signs/ClutterLodMaxSeconds"] = "Longest time grass updates may be skipped.",
            ["90 - Debugging/ClutterLodVerbose"] = "Log grass update skipping.",
        };

        internal static void Apply(ConfigFile config)
        {
            int shortened = 0;
            foreach (var entry in config.Keys.Select(k => config[k]).ToList())
            {
                string id = entry.Definition.Section + "/" + entry.Definition.Key;
                var description = entry.Description;
                if (description == null) continue;
                if (Lines.TryGetValue(id, out string line))
                {
                    AccessTools.Field(typeof(ConfigDescription), "<Description>k__BackingField")?.SetValue(description, line);
                    shortened++;
                }
            }
            // Reused from Core (FiresConfigVisibility, the one Advanced mechanism FAT uses too).
            CoreLink.MarkAdvanced(config, d => !Basics.Contains(d.Section + "/" + d.Key));
            EasyBakeLog.Debug($"[Config] {shortened} key descriptions shortened; all but {Basics.Count} basics marked Advanced.");
        }

        // 1.2.319: a feature marks its own few player-facing keys Basic (the water look); read lazily by the Advanced predicate above.
        internal static void AddBasic(string section, string key) => Basics.Add(section + "/" + key);
    }
}
