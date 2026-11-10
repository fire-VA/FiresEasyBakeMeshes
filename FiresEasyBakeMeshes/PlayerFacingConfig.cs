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
            "General/PluginEnabled",
            "Batching/Enabled",
            "Batching/ExcludedPrefabs",
        };

        private static readonly Dictionary<string, string> Lines = new Dictionary<string, string>
        {
            ["General/PluginEnabled"] = "Turn Easy Bake Meshes on or off.",
            ["General/Verbose Zone Logging"] = "Log a line for every zone baked or loaded from cache.",
            ["Batching/Enabled"] = "Merge big builds into fewer draws for better frame rates.",
            ["Batching/RunOnServer"] = "Also bake on a dedicated server (not needed normally).",
            ["Batching/MinPiecesPerZone"] = "Fewest pieces a zone needs before it is baked.",
            ["Batching/MinPiecesPerBatch"] = "Fewest pieces that share a material before they are merged.",
            ["Batching/InstancingEnabled"] = "Draw repeated pieces with GPU instancing.",
            ["Batching/SkipInstancesOutOfView"] = "Skip drawing baked groups that are off screen.",
            ["Batching/MultiPartInstancing"] = "Allow pieces made of several meshes to be instanced.",
            ["Batching/MinInstancesPerPrefab"] = "Fewest copies of a piece before it is instanced.",
            ["Batching/DamageablePieces"] = "Also bake pieces that can be damaged.",
            ["Batching/FarTierEnabled"] = "Use lighter meshes for baked zones far away.",
            ["Batching/FarTierDistanceMeters"] = "Distance where baked zones switch to lighter meshes.",
            ["Batching/ClassifyBudgetMs"] = "Time per frame spent sorting pieces before a bake.",
            ["Batching/SuppressBudgetMs"] = "Time per frame spent hiding pieces after a bake.",
            ["Batching/SettleDelaySeconds"] = "Wait this long after building stops before baking.",
            ["Batching/VerboseLogging"] = "Log details of every bake.",
            ["Batching/ExcludeTransparentPieces"] = "Leave see-through pieces (glass) out of bakes.",
            ["Batching/ModdedPrefabs"] = "How pieces from other mods are handled.",
            ["Batching/ModdedPrefabAllowlist"] = "Other mods' pieces that may always be baked.",
            ["Batching/ExcludedPrefabs"] = "Pieces that are never baked (comma-separated names).",
            ["Batching/SkipBakedPieceObjects"] = "Don't create objects for pieces a bake already draws.",
            ["Batching/SkipOnHost"] = "Also skip creating baked pieces when hosting.",
            ["Batching/SkipDamageablePieces"] = "Also skip creating damageable baked pieces.",
            ["Optimize/CreateDestroyDirtyFlag"] = "Skip the nearby-object scan while nothing changed.",
            ["Batching/LightSettingsEvent"] = "Cheaper unloading of many lights.",
            ["Batching/LightFlickerViewCull"] = "Update torch and fire flicker less often when out of view.",
            ["Batching/MinimapFogPartialUpload"] = "Cheaper map updates while exploring.",
            ["Batching/SkipVegetation"] = "Also skip creating baked trees and rocks.",
            ["Batching/BuildToolRadiusMeters"] = "Pieces this close show as real pieces while building.",
            ["Prewarm/Enabled"] = "Warm up prefabs at load to avoid hitches later.",
            ["Prewarm/RunOnServer"] = "Also warm up prefabs on a dedicated server.",
            ["Prewarm/PrefabsPerFrame"] = "Prefabs warmed up per frame.",
            ["Prewarm/VerboseLogging"] = "Log every prefab warmed up.",
            ["Prewarm/SkipNameContains"] = "Skip prefabs whose names contain these words.",
            ["Prewarm/FrameBudgetMs"] = "Time per frame spent warming up.",
            ["Prewarm/SlowInstantiateThresholdMs"] = "Prefabs slower than this are skipped next time.",
            ["Prewarm/MaxTransformCount"] = "Skip prefabs bigger than this many parts.",
            ["Cache/MaterialRegistryFrameBudgetMs"] = "Time per frame spent reading the bake cache.",
            ["Cache/PersistToDisk"] = "Save bakes to disk so zones load faster next time.",
            ["Diagnostics/CreationCensusEnabled"] = "Log why each loaded object was created (debugging).",
            ["Optimize/CreateBudgetPerFrame"] = "Most objects created per frame.",
            ["Optimize/CreateBudgetReleaseAbove"] = "Stop holding creation above this many queued objects.",
            ["Optimize/PartialCreateSort"] = "Sort only the nearest waiting objects each frame (faster with long create queues).",
            ["Optimize/ObjectStreamPacer"] = "Create nearby objects from one queue, sized to what this PC can do each frame.",
            ["Optimize/StreamPacerMotionByPosition"] = "Treat any change of position as moving when pacing object creation.",
            ["Optimize/LateJoinFromZdo"] = "Add new pieces to a baked zone without creating them first.",
            ["Optimize/StreamPacerVanillaFloor"] = "While moving, create objects at the game's own rate plus spare frame time.",
            ["Cache/RemoteZoneValidation"] = "Check a saved zone against the server only once its objects have all arrived.",
            ["Cache/SaveOffThread"] = "Write saved zones on a background thread.",
            ["Optimize/FarHandoffCountsSkipped"] = "Remove a zone's far copy as soon as its buildings are drawn.",
            ["Optimize/IndirectCrashFallback"] = "After a graphics driver crash, use the older drawing path for one session.",
            ["Optimize/ShareAwakePropertyBlock"] = "Building pieces share one unused property block instead of one each.",
            ["Cache/SaveSerializeOffThread"] = "Prepare saved zone files on a background thread too.",
            ["Optimize/StreamPacerMotionFromReference"] = "Tell moving from standing still by where the game loads the world around you.",
            ["Optimize/StreamPacerUnfocusedBoostOnlyWhenThrottled"] = "Use the bigger background loading budget only when the game is really slowed behind another window.",
            ["Optimize/StreamPacerMovingCap"] = "While moving, keep up with the game's own object rate and add extras only within a small per-frame time cap.",
            ["Optimize/StreamPacerViewOrder"] = "Create nearby objects in front of you first and behind you last.",
            ["Optimize/StreamPacerRealCostCap"] = "While standing still, size a create pass by what real objects cost.",
            ["Cache/SeparateRoleFolders"] = "Keep saved zones apart for hosting and for joining as a client.",
            ["Optimize/CacheLowGarbage"] = "Make less temporary memory while saving caches.",
            ["Cache/RejectChangedPrefabs"] = "Rebuild a saved zone once when one of its building models changed.",
            ["Cache/EmptyZoneDropsCache"] = "Remove the saved bake of a fully demolished zone.",
            ["Cache/KeepOtherSettings"] = "Keep saved zones made under other settings on disk.",
            ["Cache/PreloadNearOnly"] = "Keep only nearby saved zones in memory; far zones may rebuild after a teleport.",
            ["Cache/MaxMBPerWorld"] = "Disk space for one world's saved zones (0 = no limit).",
            ["Cache/MaxMBAllWorlds"] = "Disk space for all worlds' saved zones (0 = no limit).",
            ["Optimize/CoverAfterBakeEnds"] = "Draw pieces a bake gives back through the faster path again.",
            ["Optimize/CoverageBatchedStart"] = "Hand new trees and rocks to the faster path in one pass per frame.",
            ["Optimize/StreamPacerBaseMs"] = "Least time per frame for creating objects when the PC has no headroom.",
            ["Optimize/StreamPacerMinPerFrame"] = "Fewest objects a create pass may be limited to.",
            ["Optimize/StreamPacerEveryFramePass"] = "While objects are waiting, create them every frame, not 30 times a second.",
            ["Optimize/StreamPacerExtraPasses"] = "Also create waiting objects between the game's own ticks while you move.",
            ["Optimize/TickGroupEventDriven"] = "Re-check shelter, wet and light work only when something changed.",
            ["Optimize/IndirectCellReusePerCell"] = "When objects appear, re-check only their part of the world for drawing.",
            ["Optimize/BakeFrameSplit"] = "With debug logging on, log where the slowest zone work went.",
            ["Optimize/BakePassBudgetMs"] = "Longest time zone baking checks may run in one frame (0 = no limit).",
            ["Optimize/IndirectSyncCleanup"] = "Update changed building groups with less work while zones load.",
            ["Optimize/StreamPacerSelfCheck"] = "Test tool: compare the create queue with the game's own list (debug log).",
            ["Optimize/IdleObjectAreaSkip"] = "Skip the scan of every nearby object when nothing needs creating or removing.",
            ["Optimize/EventDrivenObjectArea"] = "Keep the nearby-object area from events, with a full scan about once a second.",
            ["Optimize/EventAreaFullPassSeconds"] = "Longest time between full scans of the nearby-object area.",
            ["Optimize/DestroyMoversFirst"] = "When leaving an area, remove moving objects at once and loose items before floors.",
            ["Optimize/RemovalIdleScanLimit"] = "Check for objects to remove at most 10 times a second while idle.",
            ["Optimize/TeleportCreateGate"] = "While teleporting, create objects early but only over ground that has loaded.",
            ["Optimize/DistantPrefabStreaming"] = "Show the far objects other mods register.",
            ["Optimize/PieceTypeAudit"] = "Log build pieces that load in the wrong order (debugging).",
            ["Optimize/ZoneLoadDiagnostics"] = "Log what each zone crossing cost (debugging).",
            ["Optimize/CandidateCensusSeconds"] = "How often to log the bake candidate count.",
            ["Optimize/CandidateCensusSample"] = "How many objects that census looks at.",
            ["Optimize/CreateDestroySkipEnabled"] = "Skip object scans while you stand still.",
            ["Optimize/CreateDestroySkipMaxSeconds"] = "Longest time scans may be skipped.",
            ["Optimize/CreateDestroySkipVerbose"] = "Log how many scans were skipped.",
            ["Optimize/WearShortCircuitInvulnerable"] = "Skip wear checks on pieces that can't break.",
            ["Optimize/ZoneKeepaliveEnabled"] = "Keep nearby zones loaded longer.",
            ["Optimize/ZoneKeepaliveRadius"] = "How many zones around you stay loaded.",
            ["Optimize/ZoneKeepaliveSeconds"] = "How long zones stay loaded after you leave.",
            ["Optimize/ZoneKeepaliveVerbose"] = "Log zone keep-alive activity.",
            ["Optimize/DestroyTimeSliceEnabled"] = "Spread object removal over several frames.",
            ["Optimize/DestroyTimeSliceBudgetMs"] = "Time per frame spent removing objects.",
            ["Optimize/DestroyTimeSliceVerbose"] = "Log object removal activity.",
            ["Optimize/SectorMirrorEnabled"] = "Faster lookups of nearby objects.",
            ["Optimize/SectorMirrorRebuildSeconds"] = "How often that lookup table is rebuilt.",
            ["Optimize/SectorMirrorVerbose"] = "Log lookup table rebuilds.",
            ["Optimize/LightFlickerLodEnabled"] = "Update far torch and fire flicker less often.",
            ["Optimize/LightFlickerLodDistance"] = "Lights closer than this flicker at full rate.",
            ["Optimize/LightFlickerMidDistance"] = "Distance for the middle flicker rate.",
            ["Optimize/LightFlickerFarDistance"] = "Distance for the far flicker rate.",
            ["Optimize/LightFlickerMidRate"] = "Flicker updates per second at middle distance.",
            ["Optimize/LightFlickerFarRate"] = "Flicker updates per second far away.",
            ["Optimize/LightFlickerDistantRate"] = "Flicker updates per second very far away.",
            ["Optimize/LightFlickerTierCheckRate"] = "How often a light's distance is checked.",
            ["Optimize/ZSFXLodEnabled"] = "Skip sound updates for far away objects.",
            ["Optimize/ZSFXLodDistance"] = "Sounds farther than this update less often.",
            ["Optimize/ZdoBakeVerifyEnabled"] = "Double-check bakes against world data (debugging).",
            ["Optimize/RemoteItemZSyncSkipEnabled"] = "Skip syncing items lying still on the ground.",
            ["Optimize/ZSyncStaticSkipEnabled"] = "Skip syncing pieces that never move.",
            ["Optimize/EffectAreaIdleSkipEnabled"] = "Skip idle area-effect checks.",
            ["Optimize/SkipUnchangedScans"] = "Skip scans when nothing changed.",
            ["Optimize/ZSyncStaticSkipFixtures"] = "Also skip syncing fixtures that never move.",
            ["Optimize/ZSyncStaticSkipVerbose"] = "Log static sync skipping.",
            ["Optimize/ClutterLodEnabled"] = "Skip grass updates while you stand still.",
            ["Optimize/ClutterLodMoveThreshold"] = "How far you move before grass updates again.",
            ["Optimize/ClutterLodMaxSeconds"] = "Longest time grass updates may be skipped.",
            ["Optimize/ClutterLodVerbose"] = "Log grass update skipping.",
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
    }
}
