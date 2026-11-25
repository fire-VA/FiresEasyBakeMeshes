using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace FiresEasyBakeMeshes
{
    // 1.2.444 ([visual]; Fire 11-18: "Shader should not be the very first section ... reorganized the configs"; plan Tools\EBM_CONFIG_REORG_PLAN_20261115.md):
    // EBM's keys live in numbered sections (BepInEx writes sections sorted by name: 01 - General first, 40 - Shaders near the bottom, 90 - Debugging last).
    // Key NAMES did not change, only sections (1.2.458: except the far renderers' toggles, see Renames). Every key is bound through Bind below, which carries a value saved at the key's old home:
    //   - the file already has the key at its NEW place: that value stays; an old-place line of the same key is dropped (logged);
    //   - else the file has it at an OLD place (Moves lists every old home, newest first): that text is loaded through the entry's own type converter
    //     (TomlTypeConverter, then BoxedValue, which clamps exactly as a hand edit does) and the old line is removed;
    //   - else the default. Defaults are never changed; lines the table does not know are left alone. No "done" flag: a second start finds no old lines.
    // Saving is held from Begin (top of Awake) to End (after the last bind), so the file is written once; before that write the cfg is copied once to
    // "<cfg>.before-reorg" beside it (only when it holds an old-place line; an existing backup is never overwritten).
    internal static class EbmConfigMove
    {
        // ONE table: old section, key, new section. A key with several old homes has one row each, newest first.
        private static readonly string[,] Moves =
        {
            { "General", "PerformanceProfile", "01 - General" },
            { "General", "PluginEnabled", "01 - General" },
            { "General", "Verbose Zone Logging", "01 - General" },
            { "Batching", "BuildToolRadiusMeters", "02 - Building Bakes" },
            { "Batching", "DamageableLowCountGroups", "02 - Building Bakes" },
            { "Batching", "DamageablePieces", "02 - Building Bakes" },
            { "Batching", "Enabled", "02 - Building Bakes" },
            { "Batching", "ExcludedPrefabs", "02 - Building Bakes" },
            { "Batching", "ExcludeTransparentPieces", "02 - Building Bakes" },
            { "Batching", "FarTierDistanceMeters", "02 - Building Bakes" },
            { "Batching", "FarTierEnabled", "02 - Building Bakes" },
            { "Batching", "InstancingEnabled", "02 - Building Bakes" },
            { "Batching", "MinInstancesPerPrefab", "02 - Building Bakes" },
            { "Batching", "MinPiecesPerBatch", "02 - Building Bakes" },
            { "Batching", "MinPiecesPerZone", "02 - Building Bakes" },
            { "Batching", "ModdedPrefabAllowlist", "02 - Building Bakes" },
            { "Batching", "ModdedPrefabs", "02 - Building Bakes" },
            { "Batching", "MultiPartInstancing", "02 - Building Bakes" },
            { "Batching", "RunOnServer", "02 - Building Bakes" },
            { "Batching", "SkipBakedPieceObjects", "02 - Building Bakes" },
            { "Batching", "SkipDamageablePieces", "02 - Building Bakes" },
            { "Batching", "SkipInstancesOutOfView", "02 - Building Bakes" },
            { "Batching", "SkipOnHost", "02 - Building Bakes" },
            { "Batching", "SkipVegetation", "02 - Building Bakes" },
            { "Far", "Far fog scale", "03 - Far View" },
            { "03 - Far View", "Far reach (m)", "03 - Far Rendering" },   // 1.2.458
            { "Far", "Far reach (m)", "03 - Far Rendering" },
            { "Far", "Far Terrain Exact Seam", "03 - Far View" },
            { "Far", "FarLook", "03 - Far View" },
            { "Far", "GlossFadeEnd", "03 - Far View" },
            { "Far", "GlossFadeStart", "03 - Far View" },
            { "Far", "PieceGlossFade", "03 - Far View" },
            { "Far", "PieceSpecularAA", "03 - Far View" },
            { "Far buildings", "Arrival budget (ms)", "04 - Far Buildings" },
            { "Far buildings", "Ask the server for far bases", "04 - Far Buildings" },
            { "Far buildings", "Cell merge from (m)", "04 - Far Buildings" },
            { "Far buildings", "Coarse cluster (m)", "04 - Far Buildings" },
            { "Far buildings", "Coarse small piece (m)", "04 - Far Buildings" },
            { "Far buildings", "Coarse tier", "04 - Far Buildings" },
            { "Far buildings", "Coarse tier from (m)", "04 - Far Buildings" },
            { "Far buildings", "Cold-set arrival budget", "04 - Far Buildings" },
            { "Far buildings", "Interior pieces left out", "04 - Far Buildings" },
            { "Far buildings", "Main thread budget (ms)", "04 - Far Buildings" },
            { "Far buildings", "Max vertices", "04 - Far Buildings" },
            { "Far buildings", "Reach (m)", "04 - Far Buildings" },
            { "Far buildings", "Server sends far bases to clients", "04 - Far Buildings" },
            { "Far buildings", "Simplify zones from (m)", "04 - Far Buildings" },
            { "Far buildings", "Size cut grows with distance", "04 - Far Buildings" },
            { "Far buildings", "Size cut max (m)", "04 - Far Buildings" },
            { "Far buildings", "Timing log", "04 - Far Buildings" },
            { "Far Impostors", "Bake budget ms", "05 - Far Trees" },
            { "Far Impostors", "Cache atlases on disk", "05 - Far Trees" },
            { "Far Impostors", "Impostor distance", "05 - Far Trees" },
            { "Far Impostors", "Mid-LOD distance", "05 - Far Trees" },
            { "Far Impostors", "Mid-LOD trees", "05 - Far Trees" },
            { "Far Impostors", "Sharpness", "05 - Far Trees" },
            { "Far Impostors", "Smooth views", "05 - Far Trees" },
            { "Far Impostors", "Upload budget ms", "05 - Far Trees" },
            { "Far rocks", "Main thread budget (ms)", "06 - Far Rocks" },
            { "Far rocks", "Max instances", "06 - Far Rocks" },
            { "Far rocks", "Max triangles", "06 - Far Rocks" },
            { "Far rocks", "Mesh band (m)", "06 - Far Rocks" },
            { "Far rocks", "Min size (m)", "06 - Far Rocks" },
            { "Far rocks", "Reach (m)", "06 - Far Rocks" },
            { "Far rocks", "Rescan seconds", "06 - Far Rocks" },
            { "Far Lights", "Falloff", "07 - Far Lights" },
            { "Far Lights", "Max distance", "07 - Far Lights" },
            { "Far Lights", "Sleep By Day", "07 - Far Lights" },
            { "Snow", "SnowNeedsSnowMod", "08 - Snow And Seasons" },
            { "Snow", "SnowRefreshPoll", "08 - Snow And Seasons" },
            { "Snow", "WaitForPushedSnow", "08 - Snow And Seasons" },
            { "Cache", "EmptyZoneDropsCache", "09 - Disk Cache" },
            { "Cache", "KeepOtherSettings", "09 - Disk Cache" },
            { "Cache", "MaxMBAllWorlds", "09 - Disk Cache" },
            { "Cache", "MaxMBPerWorld", "09 - Disk Cache" },
            { "Cache", "PersistToDisk", "09 - Disk Cache" },
            { "Cache", "PreloadNearOnly", "09 - Disk Cache" },
            { "Cache", "PreloadSlim", "09 - Disk Cache" },
            { "Cache", "RejectChangedPrefabs", "09 - Disk Cache" },
            { "Cache", "SeparateRoleFolders", "09 - Disk Cache" },
            { "Far Cache", "Enabled", "10 - Far View Disk Cache" },
            { "Far Cache", "Max MB all worlds", "10 - Far View Disk Cache" },
            { "Far Cache", "Max MB per world", "10 - Far View Disk Cache" },
            { "Far Cache", "Max MB shared", "10 - Far View Disk Cache" },
            { "Far Cache", "SkipUnchangedSaves", "10 - Far View Disk Cache" },
            { "Prewarm", "Enabled", "11 - Prewarm" },
            { "Prewarm", "FrameBudgetMs", "11 - Prewarm" },
            { "Prewarm", "MaxTransformCount", "11 - Prewarm" },
            { "Prewarm", "PrefabsPerFrame", "11 - Prewarm" },
            { "Prewarm", "RunOnServer", "11 - Prewarm" },
            { "Prewarm", "SkipNameContains", "11 - Prewarm" },
            { "Prewarm", "SlowInstantiateThresholdMs", "11 - Prewarm" },
            { "Prewarm", "VerboseLogging", "11 - Prewarm" },
            { "Optimize", "ArrivalCountsBakedPieces", "20 - Performance - Loading And Streaming" },
            { "Optimize", "CreateBudgetPerFrame", "20 - Performance - Loading And Streaming" },
            { "Optimize", "CreateBudgetReleaseAbove", "20 - Performance - Loading And Streaming" },
            { "Optimize", "CreateDestroyDirtyFlag", "20 - Performance - Loading And Streaming" },
            { "Optimize", "CreateDestroySkipEnabled", "20 - Performance - Loading And Streaming" },
            { "Optimize", "CreateDestroySkipMaxSeconds", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyArrivalDrain", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyCostCap", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyDistantBandSweep", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyHeightmapBulkDetach", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyLeanEarmark", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyMoversFirst", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyTimeSliceBudgetMs", "20 - Performance - Loading And Streaming" },
            { "Optimize", "DestroyTimeSliceEnabled", "20 - Performance - Loading And Streaming" },
            { "Optimize", "EventAreaFullPassSeconds", "20 - Performance - Loading And Streaming" },
            { "Optimize", "EventDrivenObjectArea", "20 - Performance - Loading And Streaming" },
            { "Optimize", "IdleObjectAreaSkip", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ObjectStreamPacer", "20 - Performance - Loading And Streaming" },
            { "Optimize", "PartialCreateSort", "20 - Performance - Loading And Streaming" },
            { "Optimize", "RemovalIdleScanLimit", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ScanGateEventHash", "20 - Performance - Loading And Streaming" },
            { "Optimize", "SectorMirrorEnabled", "20 - Performance - Loading And Streaming" },
            { "Optimize", "SectorMirrorRebuildSeconds", "20 - Performance - Loading And Streaming" },
            { "Optimize", "SectorMirrorSlicedRebuild", "20 - Performance - Loading And Streaming" },
            { "Optimize", "SkipUnchangedScans", "20 - Performance - Loading And Streaming" },
            { "Optimize", "SpreadFirstSightChecks", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerBaseMs", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerCompilersFirst", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerEveryFramePass", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerExtraPasses", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerMinPerFrame", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerMotionByPosition", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerMotionFromReference", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerMovingCap", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerNearFirst", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerRealCostCap", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerReconcileSliced", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerSectorLeave", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerThroughArrival", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerUnfocusedBoostOnlyWhenThrottled", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerVanillaFloor", "20 - Performance - Loading And Streaming" },
            { "Optimize", "StreamPacerViewOrder", "20 - Performance - Loading And Streaming" },
            { "Optimize", "TeleportCreateGate", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ZoneKeepaliveEnabled", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ZoneKeepaliveRadius", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ZoneKeepaliveSeconds", "20 - Performance - Loading And Streaming" },
            { "Optimize", "ZoneTransitionBudgetMs", "20 - Performance - Loading And Streaming" },
            { "Optimize", "GpuFires", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "GpuFiresExclude", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "GpuFiresNearM", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "GpuFiresPhases", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightBudgetEnabled", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightBudgetPixelLights", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightBudgetScreenRank", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerDistantRate", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerFarDistance", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerFarRate", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerLodDistance", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerLodEnabled", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerMidDistance", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerMidRate", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightFlickerTierCheckRate", "21 - Performance - Lights, Fires And Sound" },
            { "Batching", "LightFlickerViewCull", "21 - Performance - Lights, Fires And Sound" },
            { "Batching", "LightSettingsEvent", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightTinyCut", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightTinyOffScreen", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "LightTinyShadowScreen", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "ZSFXLodDistance", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "ZSFXLodEnabled", "21 - Performance - Lights, Fires And Sound" },
            { "Optimize", "ClutterLodEnabled", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "ClutterLodMaxSeconds", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "ClutterLodMoveThreshold", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "DungeonInteriorCull", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "GrassFarSwayStop", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "GrassPatchSize", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "IndirectGrass", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "IndirectGrassArrayRecycle", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "IndirectGrassFadeCarry", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "IndirectGrassStepGate", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "IndirectGrassUploadMs", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "SignTextCull", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "SignTextDrawM", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "StandItemCull", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "StandItemDrawM", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "StandItemShadowCut", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "StandItemShadowM", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "StandQuickCheck", "22 - Performance - Grass, Stands And Signs" },
            { "Optimize", "CoverageBatchedStart", "23 - Performance - World Objects" },
            { "Optimize", "CoverageRegistryAttach", "23 - Performance - World Objects" },
            { "Optimize", "CoverageRegistryReachGate", "23 - Performance - World Objects" },
            { "Optimize", "EffectAreaIdleSkipEnabled", "23 - Performance - World Objects" },
            { "Batching", "IdleGates", "23 - Performance - World Objects" },
            { "Batching", "MinimapFogPartialUpload", "23 - Performance - World Objects" },
            { "Batching", "ParticleIdlePause", "23 - Performance - World Objects" },
            { "Optimize", "PiecesGroupedPerTile", "23 - Performance - World Objects" },
            { "Optimize", "RemoteItemZSyncSkipEnabled", "23 - Performance - World Objects" },
            { "Optimize", "ShareAwakePropertyBlock", "23 - Performance - World Objects" },
            { "Optimize", "TickGroupEventDriven", "23 - Performance - World Objects" },
            { "Batching", "VanillaWearOnInvulnerablePieces", "23 - Performance - World Objects" },
            { "Optimize", "WearShortCircuitInvulnerable", "23 - Performance - World Objects" },
            { "Optimize", "ZSyncStaticSkipEnabled", "23 - Performance - World Objects" },
            { "Optimize", "ZSyncStaticSkipFixtures", "23 - Performance - World Objects" },
            { "Optimize", "BakeIntactRocks", "24 - Performance - GPU Drawing" },
            { "Optimize", "CascadeAppendOnGain", "24 - Performance - GPU Drawing" },
            { "Optimize", "FarSmallNoShadows", "24 - Performance - GPU Drawing" },
            { "Optimize", "GpuReleaseFence", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCachedAlive", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCameraDrawSort", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCameraEmptySkip", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeEmptySkip", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeScanReuse", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeShadows", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeSphereMarginM", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeSphereScale", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCascadeTightStride", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterCpuCull", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterMinScreen", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterReceiverCull", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterReceiverCullMarginM", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterReceiverCullMarginPct", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCasterSizeSkip", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCellFastSkip", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCellReuse", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCellReusePerCell", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCellsJob", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoarseCasters", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCompactSlots", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverage", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverageDisablePrefabs", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverageDisableRenderers", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverageLocationProps", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverageLodLevels", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCoverageReach", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCrashFallback", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCullBindCache", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectCullGrouped", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectDepthCull", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectDepthCullNearM", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectDrawParamsCache", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectExactCascadeSpheres", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectFarCardShadows", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectFusedCells", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectGpuGrow", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLightsSwept", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLocalDirtyScan", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLocalDirtyScanRelaxed", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLocalSteady", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLodPerInstance", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectLooksChangeDriven", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectMergedCasters", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectMeshDriftSlow", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectMinScreenSize", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectOneCullPass", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectPlaceholderCachedBounds", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectPointFaces", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectPresize", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSelfGate", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSelfGateDownAt", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSelfGateForce", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSelfGateUpAt", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSettledWalk", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectShadowPlaceholders", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectShadowReceiverCull", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectShadowSameFrame", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectShadowSizeCut", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSkyDepth", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSlotCompact", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSlotTrim", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSparseUpload", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectStableLayout", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectStagedSync", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectStaticClusters", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectSyncCleanup", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectVegetationPrepass", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectVegetationPrepassMaxInstances", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectVegetationPrepassMaxTris", "24 - Performance - GPU Drawing" },
            { "Optimize", "IndirectVegetationPrepassNoClip", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedCellLod", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedCellSizeMeters", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedFollowChecksEveryFrame", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedInstanceCull", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedOtherCameraRedraw", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedPiecesDrawLate", "24 - Performance - GPU Drawing" },
            { "Optimize", "InstancedShadowProxies", "24 - Performance - GPU Drawing" },
            { "Optimize", "IntactRockLod1CellsAcross", "24 - Performance - GPU Drawing" },
            { "Optimize", "IntactRockLod1ScreenHeight", "24 - Performance - GPU Drawing" },
            { "Optimize", "NoShadowBeyondMeters", "24 - Performance - GPU Drawing" },
            { "Optimize", "NoShadowSmallerThanMeters", "24 - Performance - GPU Drawing" },
            { "Optimize", "SkyDepthThrottle", "24 - Performance - GPU Drawing" },
            { "Optimize", "DistantPrefabScanSliced", "25 - Performance - Far View" },
            { "Optimize", "DistantPrefabStreaming", "25 - Performance - Far View" },
            { "Optimize", "FarBuildingsChangedZonesOnly", "25 - Performance - Far View" },
            { "Optimize", "FarBuildingsQuietPolling", "25 - Performance - Far View" },
            { "Optimize", "FarBuildViewFirst", "25 - Performance - Far View" },
            { "Optimize", "FarCapHysteresis", "25 - Performance - Far View" },
            { "Optimize", "FarHandoffCountsSkipped", "25 - Performance - Far View" },
            { "Optimize", "FarHandoffProvenDrawn", "25 - Performance - Far View" },
            { "Optimize", "FarIndexRanges", "25 - Performance - Far View" },
            { "Optimize", "FarKeepHandedOff", "25 - Performance - Far View" },
            { "Optimize", "FarMergePooled", "25 - Performance - Far View" },
            { "Optimize", "FarMeshReadNoRawFlags", "25 - Performance - Far View" },
            { "Optimize", "FarRetireSliced", "25 - Performance - Far View" },
            { "Optimize", "FarSourceAsync", "25 - Performance - Far View" },
            { "Optimize", "FarSourceIdleRelease", "25 - Performance - Far View" },
            { "Optimize", "FarUploadByteCap", "25 - Performance - Far View" },
            { "Optimize", "FarUploadSliced", "25 - Performance - Far View" },
            { "Optimize", "FarZoneGrid", "25 - Performance - Far View" },
            { "Optimize", "BakeFollowChanges", "26 - Performance - Bakes And Cache" },
            { "Optimize", "BakeGateSliced", "26 - Performance - Bakes And Cache" },
            { "Optimize", "BakePassBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Optimize", "CacheLowGarbage", "26 - Performance - Bakes And Cache" },
            { "Batching", "ClassifyBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Optimize", "CoverAfterBakeEnds", "26 - Performance - Bakes And Cache" },
            { "Optimize", "LateJoinFromZdo", "26 - Performance - Bakes And Cache" },
            { "Cache", "MaterialRegistryFrameBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Cache", "ReleaseBakedCpuCopies", "26 - Performance - Bakes And Cache" },
            { "Cache", "ReleaseIdleSeconds", "26 - Performance - Bakes And Cache" },
            { "Cache", "RemoteZoneValidation", "26 - Performance - Bakes And Cache" },
            { "Cache", "SaveOffThread", "26 - Performance - Bakes And Cache" },
            { "Cache", "SaveSerializeOffThread", "26 - Performance - Bakes And Cache" },
            { "Cache", "SaveSliceBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Cache", "SaveSliced", "26 - Performance - Bakes And Cache" },
            { "Batching", "SettleDelaySeconds", "26 - Performance - Bakes And Cache" },
            { "Optimize", "SkipFarBakedPieces", "26 - Performance - Bakes And Cache" },
            { "Batching", "SnowResplitBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Batching", "SuppressBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Cache", "ZdoCombineIdleRelease", "26 - Performance - Bakes And Cache" },
            { "Cache", "ZdoFirstBake", "26 - Performance - Bakes And Cache" },
            { "Cache", "ZdoFirstBakeBudgetMs", "26 - Performance - Bakes And Cache" },
            { "Fixes", "ArrivalAreaFirst", "30 - Fixes" },
            { "Fixes", "FarNeedsGround", "30 - Fixes" },
            { "Fixes", "IndirectClonesFollowSeason", "30 - Fixes" },
            { "Fixes", "IndirectFaceRowsOnReuse", "30 - Fixes" },
            { "Fixes", "StandInsKeptPastCap", "30 - Fixes" },
            { "Fixes", "ZoneKeepWithEbmPieces", "30 - Fixes" },
            { "Fixes", "ZoneLoadedIgnoresObjectLoading", "30 - Fixes" },
            { "Water", "FAT Water Look", "40 - Shaders" },
            { "01 - Shaders", "IndirectCreatureVariant", "40 - Shaders" },
            { "Optimize", "IndirectCreatureVariant", "40 - Shaders" },
            { "01 - Shaders", "IndirectLavaVariant", "40 - Shaders" },
            { "Optimize", "IndirectLavaVariant", "40 - Shaders" },
            { "Optimize", "IndirectPiecePixelShave", "40 - Shaders" },
            { "01 - Shaders", "IndirectRenderer", "40 - Shaders" },
            { "Optimize", "IndirectRenderer", "40 - Shaders" },
            { "Optimize", "IndirectRugVariant", "40 - Shaders" },
            { "Optimize", "IndirectStandardMoreKeywords", "40 - Shaders" },
            { "Optimize", "MistOpacity", "40 - Shaders" },
            { "Optimize", "MistParticleCount", "40 - Shaders" },
            { "Optimize", "MistShader", "40 - Shaders" },
            { "Optimize", "TerrainCameraTessCut", "40 - Shaders" },
            { "01 - Shaders", "TerrainShader", "40 - Shaders" },
            { "Optimize", "TerrainShader", "40 - Shaders" },
            { "Optimize", "TerrainShadowNoTess", "40 - Shaders" },
            { "Optimize", "TerrainTessFullDistance", "40 - Shaders" },
            { "Optimize", "WarmEbmShaders", "40 - Shaders" },
            { "01 - Shaders", "WaterShader", "40 - Shaders" },
            { "Optimize", "WaterShader", "40 - Shaders" },
            { "Optimize", "WaterHideBuriedTiles", "41 - Water Performance" },
            { "Optimize", "WaterHideOccludedTiles", "41 - Water Performance" },
            { "Optimize", "WaterSplitGrid", "41 - Water Performance" },
            { "Optimize", "WaterSplitLitTiles", "41 - Water Performance" },
            { "Optimize", "WaterTessCut", "41 - Water Performance" },
            { "Optimize", "WaterTessFarEnd", "41 - Water Performance" },
            { "Optimize", "WaterTessFarStart", "41 - Water Performance" },
            { "Optimize", "WaterTightTileBounds", "41 - Water Performance" },
            { "Optimize", "WaterVanillaDistanceDetail", "41 - Water Performance" },
            { "Water - Colour & Depth", "Absorption Colour Loss", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Churn Follows Water Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Churn Motion", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water Depth", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water Edge Guard", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water No Surface Tint", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water One Body Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water Optics", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Clear Water Shore Foam By Real Depth", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Depth Colour Absorption", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Depth Colour Shift", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Game Lighting", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Game Water Colours", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Glow Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Glow Only On Water", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Glow Strength", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Minimum Water Depth For Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Murk", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Murk Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Murk Darkness", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Opaque Water Depth", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Rain Murk", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Sea Bed Brightness", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Churn", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Water Colour Distance", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Water Colour Floor", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Water Haze", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Water Max Opacity", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shallow Water Opacity Distance", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Shoreline Softness", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Sun Glow", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Sun Glow Focus", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Vanilla Water Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Absorbs Along The View", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Body By Depth", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Body Light Limit", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Body Murk Tint", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Colour In The Shallows", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Colour Strength", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Lit By Waves", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Water Top Colour", "42 - Water - Colour & Depth" },
            { "Water - Colour & Depth", "Wave Crest Glow", "42 - Water - Colour & Depth" },
            { "Water - Foam & Shore", "Foam Bubbles", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Colour", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Coverage", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Depth Effect", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam In Sunlight", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Layer 1", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Layer 1 Size", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Layer 2", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Layer 2 Size", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam On Steep Waves", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam On Wave Tops", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Opacity", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Foam Speed", "43 - Water - Foam & Shore" },
            { "Water", "Shore Field", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Shore Foam", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Shore Foam Amount", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Shore Foam Blend", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Shore Foam Contrast", "43 - Water - Foam & Shore" },
            { "Water - Foam & Shore", "Shore Foam Reach", "43 - Water - Foam & Shore" },
            { "Water - Surface & Reflection", "Distant Reflection", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Distant Sparkle Softness", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Distant Water Brightness", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Distant Water Tint", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Game Ripples", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Physical Reflection Mix", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Reflection Angle Softness", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Reflection Cap Looking Down", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Reflection Looking Down", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Reflection Strength", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Sea Fog On Water", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Sky Reflection Boost", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Sun Sparkle", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Underwater Check Once Per Frame", "44 - Water - Surface & Reflection" },
            { "Water - Surface & Reflection", "Underwater View Follows Waves", "44 - Water - Surface & Reflection" },
            { "Water", "WaterEdgeBlend", "44 - Water - Surface & Reflection" },
            { "Water", "WaterEdgeBlendBand", "44 - Water - Surface & Reflection" },
            { "Water", "WaterEdgeCrossFade", "44 - Water - Surface & Reflection" },
            { "Water", "WaterEdgeCrossFadeBand", "44 - Water - Surface & Reflection" },
            { "Water", "WaterSeamStencil", "44 - Water - Surface & Reflection" },
            { "Water - Underwater & Caustics", "Light Pattern Sharpness", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Light Pattern Size", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Light Pattern Speed", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Light Pattern Wobble", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Near Refraction Range", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Near Refraction Start", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Shore Refraction Fade", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Surface Light Patterns", "45 - Water - Underwater & Caustics" },
            { "Water - Underwater & Caustics", "Underwater Dapple", "45 - Water - Underwater & Caustics" },
            { "Water - Waves", "Rain Ring Distance", "46 - Water - Waves" },
            { "Water - Waves", "Rain Ring Spacing", "46 - Water - Waves" },
            { "Water - Waves", "Rain Ring Strength", "46 - Water - Waves" },
            { "Water - Waves", "Rain Rings", "46 - Water - Waves" },
            { "Water - Waves", "Rain Rings From Below", "46 - Water - Waves" },
            { "Water - Waves", "Ripple Fade Distance", "46 - Water - Waves" },
            { "Water - Waves", "Ripple Speed", "46 - Water - Waves" },
            { "Water - Waves", "Smooth Wind Drift", "46 - Water - Waves" },
            { "Water - Waves", "Wave Detail Limit", "46 - Water - Waves" },
            { "Water - Waves", "Wave Opacity", "46 - Water - Waves" },
            { "Debugging", "AsyncLog", "90 - Debugging" },
            { "Optimize", "BakeCoverageCensus", "90 - Debugging" },
            { "Optimize", "BakeFrameSplit", "90 - Debugging" },
            { "Optimize", "BakeVerifyCoverage", "90 - Debugging" },
            { "Diagnostics", "BlackFrameWatch", "90 - Debugging" },
            { "Diagnostics", "BlackFrameWatchDarkBelow", "90 - Debugging" },
            { "Diagnostics", "BlackFrameWatchEverySeconds", "90 - Debugging" },
            { "Diagnostics", "BlackFrameWatchSkyAbove", "90 - Debugging" },
            { "Optimize", "CandidateCensusSample", "90 - Debugging" },
            { "Optimize", "CandidateCensusSeconds", "90 - Debugging" },
            { "Optimize", "ClutterLodVerbose", "90 - Debugging" },
            { "Diagnostics", "CoverageCensus", "90 - Debugging" },
            { "Optimize", "CoverageRegistryVerify", "90 - Debugging" },
            { "Optimize", "CreateDestroySkipVerbose", "90 - Debugging" },
            { "Diagnostics", "CreationCensusEnabled", "90 - Debugging" },
            { "Water - Debug", "Debug View", "90 - Debugging" },
            { "Optimize", "DestroyTimeSliceVerbose", "90 - Debugging" },
            { "Optimize", "DungeonInteriorCullVerify", "90 - Debugging" },
            { "Optimize", "FarGroundProbe", "90 - Debugging" },
            { "Debugging", "HandoffCensus", "90 - Debugging" },
            { "Diagnostics", "IndirectCascadeCounts", "90 - Debugging" },
            { "Optimize", "IndirectCasterReceiverCullVerify", "90 - Debugging" },
            { "Optimize", "IndirectCreatureVariantVerify", "90 - Debugging" },
            { "Optimize", "IndirectDepthCullVerify", "90 - Debugging" },
            { "Diagnostics", "IndirectGpuMeshValidator", "90 - Debugging" },
            { "Debugging", "IndirectGrassStepGateVerify", "90 - Debugging" },
            { "Diagnostics", "IndirectIntegrityCheck", "90 - Debugging" },
            { "Diagnostics", "IndirectMeshContentCheck", "90 - Debugging" },
            { "Optimize", "IndirectVertexDebug", "90 - Debugging" },
            { "Optimize", "LightCensus", "90 - Debugging" },
            { "Diagnostics", "LodLevelCensus", "90 - Debugging" },
            { "Debugging", "PerfCensus", "90 - Debugging" },
            { "Water - Debug", "Per-Pixel Depth", "90 - Debugging" },
            { "Water - Debug", "Per-Pixel Foam", "90 - Debugging" },
            { "Diagnostics", "PhysicsCensus", "90 - Debugging" },
            { "Optimize", "PieceTypeAudit", "90 - Debugging" },
            { "Debugging", "RenderCensus", "90 - Debugging" },
            { "Diagnostics", "RendererCensus", "90 - Debugging" },
            { "Optimize", "SectorMirrorVerbose", "90 - Debugging" },
            { "Debugging", "SignTextCullVerify", "90 - Debugging" },
            { "Optimize", "StaticClustersVerify", "90 - Debugging" },
            { "Optimize", "StreamPacerSelfCheck", "90 - Debugging" },
            { "Debugging", "TerrainExactSkipsOff", "90 - Debugging" },
            { "Debugging", "TerrainShaderVerify", "90 - Debugging" },
            { "Batching", "VerboseLogging", "90 - Debugging" },
            { "Optimize", "WaterHideBuriedTilesVerify", "90 - Debugging" },
            { "Optimize", "WaterHideOccludedTilesVerify", "90 - Debugging" },
            { "Debugging", "WaterShaderVerify", "90 - Debugging" },
            { "Optimize", "WaterSplitLitTilesVerify", "90 - Debugging" },
            { "Optimize", "WaterTightTileBoundsVerify", "90 - Debugging" },
            { "Optimize", "ZdoBakeVerifyEnabled", "90 - Debugging" },
            { "Optimize", "ZoneKeepaliveVerbose", "90 - Debugging" },
            { "Optimize", "ZoneLoadDiagnostics", "90 - Debugging" },
            { "Optimize", "ZSyncStaticSkipVerbose", "90 - Debugging" },
        };

        // 1.2.458 ([visual], Fire 11-18: "It will all get its own config section. With a master switch ... as well as a toggle for each far render type"):
        // keys that changed NAME as well as section (each far renderer's "Enabled" becomes its own named toggle in [03 - Far Rendering]; several old
        // "Enabled" keys cannot share one section). Old section, old key, new section, new key; several old homes = one row each, newest first.
        // Carried exactly as Moves (same Bind path: new-place line wins, else the newest old line's value, else the default).
        // The master "Far Rendering" has no old home: a fresh and an upgraded cfg both get its default (false).
        private static readonly string[,] Renames =
        {
            { "03 - Far View", "FarTerrain", "03 - Far Rendering", "Far Terrain" },
            { "Far", "FarTerrain", "03 - Far Rendering", "Far Terrain" },
            { "05 - Far Trees", "Enabled", "03 - Far Rendering", "Far Trees" },
            { "Far Impostors", "Enabled", "03 - Far Rendering", "Far Trees" },
            { "05 - Far Trees", "Own Placement", "03 - Far Rendering", "Far Trees Own Placement" },
            { "06 - Far Rocks", "Enabled", "03 - Far Rendering", "Far Rocks" },
            { "Far rocks", "Enabled", "03 - Far Rendering", "Far Rocks" },
            { "04 - Far Buildings", "Enabled", "03 - Far Rendering", "Far Buildings" },
            { "Far buildings", "Enabled", "03 - Far Rendering", "Far Buildings" },
            { "07 - Far Lights", "Enabled", "03 - Far Rendering", "Far Lights" },
            { "Far Lights", "Enabled", "03 - Far Rendering", "Far Lights" },
        };

        private static Dictionary<ConfigDefinition, List<ConfigDefinition>> s_oldHomes;   // new (section, key) -> old (section, key), newest first
        private static readonly List<string> s_moved = new List<string>();     // keys whose old line was found
        private static readonly List<string> s_carried = new List<string>();   // "key=value" carried from an old line
        private static readonly List<string> s_dropped = new List<string>();   // old lines dropped because the new place had a value
        private static bool s_holding, s_saveWas = true, s_ended;
        private static string s_backupName;   // the backup made by Begin this start, if any

        private static Dictionary<ConfigDefinition, List<ConfigDefinition>> OldHomes()
        {
            if (s_oldHomes != null) return s_oldHomes;
            var d = new Dictionary<ConfigDefinition, List<ConfigDefinition>>();
            for (int i = 0; i < Moves.GetLength(0); i++)
            {
                var def = new ConfigDefinition(Moves[i, 2], Moves[i, 1]);
                if (!d.TryGetValue(def, out var list)) d[def] = list = new List<ConfigDefinition>();
                list.Add(new ConfigDefinition(Moves[i, 0], Moves[i, 1]));
            }
            for (int i = 0; i < Renames.GetLength(0); i++)   // 1.2.458
            {
                var def = new ConfigDefinition(Renames[i, 2], Renames[i, 3]);
                if (!d.TryGetValue(def, out var list)) d[def] = list = new List<ConfigDefinition>();
                list.Add(new ConfigDefinition(Renames[i, 0], Renames[i, 1]));
            }
            return s_oldHomes = d;
        }

        /// <summary>True when the key's newest old home was <paramref name="section"/> (ebm_opt keeps its pre-reorg [Optimize] key set).</summary>
        internal static bool CameFrom(ConfigDefinition def, string section)
            => def != null && OldHomes().TryGetValue(def, out var list) && list.Count > 0 && list[0].Section == section;

        private static IDictionary Orphans(ConfigFile cfg)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            return (typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(cfg, null)
                    ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(cfg)) as IDictionary;
        }

        /// <summary>Top of Awake, before the first bind: hold saving, and back the file up once if it holds any old-place line.</summary>
        internal static void Begin(ConfigFile cfg)
        {
            if (cfg == null || s_holding || s_ended) return;
            s_saveWas = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;
            s_holding = true;
            try
            {
                var orphans = Orphans(cfg);
                if (orphans == null) return;
                bool any = false;
                for (int i = 0; i < Moves.GetLength(0) && !any; i++) any = orphans.Contains(new ConfigDefinition(Moves[i, 0], Moves[i, 1]));
                for (int i = 0; i < Renames.GetLength(0) && !any; i++) any = orphans.Contains(new ConfigDefinition(Renames[i, 0], Renames[i, 1]));   // 1.2.458
                if (!any) return;
                string path = cfg.ConfigFilePath, backup = path + ".before-reorg";
                if (File.Exists(path) && !File.Exists(backup))
                {
                    File.Copy(path, backup, false);
                    s_backupName = Path.GetFileName(backup);   // named in End's one Info line
                }
            }
            catch (Exception ex) { EasyBakeLog.Warn($"[Config] could not back up the config before the section move: {ex.Message}"); }
        }

        internal static ConfigEntry<T> Bind<T>(ConfigFile cfg, string section, string key, T defaultValue, string description)
            => Bind(cfg, section, key, defaultValue, new ConfigDescription(description ?? string.Empty));

        internal static ConfigEntry<T> Bind<T>(ConfigFile cfg, string section, string key, T defaultValue, ConfigDescription description = null)
        {
            var def = new ConfigDefinition(section, key);
            if (cfg.ContainsKey(def) || !OldHomes().TryGetValue(def, out var homes)) return cfg.Bind(def, defaultValue, description);

            IDictionary orphans = null; bool newLine = false;
            try { orphans = Orphans(cfg); newLine = orphans != null && orphans.Contains(def); }
            catch (Exception ex) { EasyBakeLog.Warn($"[Config] could not read the saved lines for [{section}] {key}: {ex.Message}"); }

            var entry = cfg.Bind(def, defaultValue, description);   // a NEW-place line is loaded here by BepInEx itself
            if (orphans == null) return entry;

            bool changed = false, saveWas = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false;   // a carried value must not write the file before End
            try
            {
                foreach (var oldDef in homes)   // 1.2.458: old (section, key) pairs, so a renamed key (Renames) carries too
                {
                    string oldSection = oldDef.Section, oldKey = oldDef.Key;
                    if (!orphans.Contains(oldDef)) continue;
                    string text = orphans[oldDef] as string;
                    if (newLine)
                    {
                        s_dropped.Add($"[{oldSection}] {oldKey}={text?.Trim()} (kept [{section}] {key}={entry.GetSerializedValue()})");
                        orphans.Remove(oldDef);
                        changed = true;
                        continue;
                    }
                    try
                    {
                        object v = TomlTypeConverter.ConvertToValue(text ?? string.Empty, typeof(T));
                        entry.BoxedValue = v;   // the entry's own setter: AcceptableValue clamping as for a hand edit
                        s_carried.Add($"{key}={entry.GetSerializedValue()}");
                    }
                    catch (Exception ex)
                    {
                        EasyBakeLog.Warn($"[Config] the old line [{oldSection}] {oldKey} = {text} could not be read ({ex.Message}); [{section}] {key} keeps {entry.GetSerializedValue()}");
                    }
                    orphans.Remove(oldDef);
                    newLine = true;   // older homes of the same key are dropped, not carried over this value
                    changed = true;
                }
            }
            finally { cfg.SaveOnConfigSet = saveWas; }

            if (changed)
            {
                s_moved.Add(key);
                if (s_ended) Say(cfg, true);   // a key bound after startup: report and write now
            }
            return entry;
        }

        /// <summary>After the last bind of startup (and before the dedicated-server return): one log line, saving restored, the file written once.</summary>
        internal static void End(ConfigFile cfg)
        {
            if (cfg == null || s_ended) return;
            s_ended = true;
            if (s_holding) { cfg.SaveOnConfigSet = s_saveWas; s_holding = false; }
            Say(cfg, false);
            try { cfg.Save(); } catch (Exception ex) { EasyBakeLog.Warn("[Config] could not save the config: " + ex.Message); }
        }

        private static void Say(ConfigFile cfg, bool save)
        {
            if (s_moved.Count == 0) return;
            EasyBakeLog.Info($"[Config] {s_moved.Count} settings moved to their new sections; {s_carried.Count} carried your value: {(s_carried.Count == 0 ? "none" : string.Join(", ", s_carried))}{(s_backupName != null ? $" (your old file was copied to {s_backupName})" : "")}");
            if (s_dropped.Count > 0)
                EasyBakeLog.Info($"[Config] {s_dropped.Count} old line(s) dropped because the new section already had the key: {string.Join("; ", s_dropped)}");
            s_moved.Clear(); s_carried.Clear(); s_dropped.Clear(); s_backupName = null;
            if (save) { try { cfg.Save(); } catch (Exception ex) { EasyBakeLog.Warn("[Config] could not save the moved keys: " + ex.Message); } }
        }
    }
}
