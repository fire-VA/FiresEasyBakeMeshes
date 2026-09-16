using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Collections;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.Fire.FiresUnifiedCore", BepInDependency.DependencyFlags.HardDependency)]
    // Runs after GameCamera, which moves the camera in its own LateUpdate, so the instanced
    // draw tests the view the frame is about to be rendered from.
    [DefaultExecutionOrder(1000)]
    public class FiresEasyBakeMeshesPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.Fire.FiresEasyBakeMeshes";
        public const string PluginName = "FiresEasyBakeMeshes";
        public const string PluginVersion = "1.2.15";

        public static ConfigEntry<bool> PluginEnabled;
        public static ConfigEntry<bool> VerboseZoneLogging;

        public static ConfigEntry<bool>  BatchingEnabled;
        public static ConfigEntry<bool>  BatchingRunOnServer;
        public static ConfigEntry<int>   BatchingMinPiecesPerZone;
        public static ConfigEntry<int>   BatchingMinPiecesPerBatch;
        public static ConfigEntry<float> BatchingSettleDelaySeconds;
        public static ConfigEntry<bool>  BatchingFarTierEnabled;
        public static ConfigEntry<float> BatchingFarTierDistance;
        public static ConfigEntry<bool>  BatchingInstancingEnabled;
        public static ConfigEntry<bool>  BatchingCullOffScreen;
        public static ConfigEntry<bool>  BatchingMultiPartPieces;
        public static ConfigEntry<float> BatchingBuildRadius;
        public static ConfigEntry<int>   BatchingMinInstancesPerPrefab;
        public static ConfigEntry<bool>  BatchingDamageablePieces;
        public static ConfigEntry<bool>  BatchingVerbose;
        public static ConfigEntry<bool>  BatchingExcludeTransparent;
        public static ConfigEntry<bool>  SkipBakedPieceObjects;

        public static ConfigEntry<bool>   PrewarmEnabled;
        public static ConfigEntry<bool>   PrewarmRunOnServer;
        public static ConfigEntry<int>    PrewarmPrefabsPerFrame;
        public static ConfigEntry<bool>   PrewarmVerbose;
        public static ConfigEntry<string> PrewarmSkipNameContains;
        public static ConfigEntry<float>  PrewarmSlowInstantiateThresholdMs;
        public static ConfigEntry<int>    PrewarmMaxTransformCount;
        public static ConfigEntry<float>  PrewarmFrameBudgetMs;
        public static ConfigEntry<float>  MaterialRegistryFrameBudgetMs;

        public static ConfigEntry<bool> CachePersistEnabled;

        public static ConfigEntry<bool>  CreateDestroySkipEnabled;
        public static ConfigEntry<float> CreateDestroySkipMaxSeconds;
        public static ConfigEntry<bool>  CreateDestroySkipVerbose;

        public static ConfigEntry<bool>  WearShortCircuitEnabled;

        public static ConfigEntry<bool>  ZoneKeepaliveEnabled;
        public static ConfigEntry<int>   ZoneKeepaliveRadius;
        public static ConfigEntry<float> ZoneKeepaliveSeconds;
        public static ConfigEntry<bool>  ZoneKeepaliveVerbose;

        public static ConfigEntry<bool>  DestroyTimeSliceEnabled;
        public static ConfigEntry<float> DestroyTimeSliceBudgetMs;
        public static ConfigEntry<bool>  DestroyTimeSliceVerbose;

        public static ConfigEntry<bool>  SectorMirrorEnabled;
        public static ConfigEntry<float> SectorMirrorRebuildSeconds;
        public static ConfigEntry<bool>  SectorMirrorVerbose;

        public static ConfigEntry<bool>  LightFlickerLodEnabled;
        public static ConfigEntry<float> LightFlickerLodDistance;
        public static ConfigEntry<float> LightFlickerMidDistance;
        public static ConfigEntry<float> LightFlickerFarDistance;
        public static ConfigEntry<float> LightFlickerMidRate;
        public static ConfigEntry<float> LightFlickerFarRate;
        public static ConfigEntry<float> LightFlickerDistantRate;
        public static ConfigEntry<float> LightFlickerTierCheckRate;

        public static ConfigEntry<bool>  ZSFXLodEnabled;
        public static ConfigEntry<float> ZSFXLodDistance;

        public static ConfigEntry<bool>  ZSyncStaticSkipEnabled;
        public static ConfigEntry<bool>  ZSyncStaticSkipVerbose;

        public static ConfigEntry<bool>  ClutterLodEnabled;
        public static ConfigEntry<float> ClutterLodMoveThreshold;
        public static ConfigEntry<float> ClutterLodMaxSeconds;
        public static ConfigEntry<bool>  ClutterLodVerbose;

        // Live-toggle bookkeeping. The Harmony instance is created in Awake
        // unconditionally so the PluginEnabled.SettingChanged handler has
        // somewhere to apply/unapply patches on demand without restart.
        private Harmony _harmony;
        private bool    _patchesApplied;

        private void Awake()
        {
            EasyBakeLog.Bind(Logger);

            try { FiresEasyBakeBanner.PrintBig(); }
            catch (System.Exception ex) { Logger.LogWarning($"Big banner failed: {ex.Message}"); }

            // Detect FiresDebugginTools so EasyBake's own hot paths self-time into
            // the shared FrameTimer only when the profiler is there to drain it.
            EasyBake.Probe.Detect();

            PluginEnabled = Config.Bind("General", "PluginEnabled", true,
                "Master kill switch. When off no Harmony patches are applied at all.");

            VerboseZoneLogging = Config.Bind("General", "Verbose Zone Logging", false,
                "Log the per-zone cache-hit and fresh-bake lines (one line per zone,\n" +
                "~25-30 at login on a megabase). Off by default — the debounced\n" +
                "ZONES BAKED summary box carries the totals either way.");

            BatchingEnabled = Config.Bind("Batching", "Enabled", true,
                "Workstream E.2 v1: at zone load, combine all invulnerable-piece meshes\n" +
                "into one batch per material. Per-piece MeshRenderers are disabled but\n" +
                "GameObjects (and ZNetView, colliders, Piece) stay alive.");

            BatchingRunOnServer = Config.Bind("Batching", "RunOnServer", false,
                "Run batching on dedicated server too. Default off — a headless server\n" +
                "doesn't render so Mesh.CombineMeshes() output is pure wasted CPU + memory.\n" +
                "A player-hosted world (host + client in one process) still gets the benefit\n" +
                "regardless of this setting; this gate only suppresses true dedicated servers.");

            BatchingMinPiecesPerZone = Config.Bind("Batching", "MinPiecesPerZone", 20,
                new ConfigDescription(
                    "Below this many invulnerable pieces in a zone, skip batching entirely.\n" +
                    "Tiny invulnerable counts don't pay back the bake cost.",
                    new AcceptableValueRange<int>(1, 10000)));

            BatchingMinPiecesPerBatch = Config.Bind("Batching", "MinPiecesPerBatch", 10,
                new ConfigDescription(
                    "Below this many pieces sharing a material, that bucket passes through\n" +
                    "to vanilla rendering rather than getting its own combined mesh.",
                    new AcceptableValueRange<int>(1, 10000)));

            BatchingInstancingEnabled = Config.Bind("Batching", "InstancingEnabled", true,
                new ConfigDescription(
                    "Draw uniform single-mesh prefabs (palisades, floor tiles, stakewalls)\nas GPU instances instead of merging them into a combined mesh. Costs no\nvertex duplication, and a removed piece drops out in O(1) instead of\nforcing the zone to rebake."));

            BatchingCullOffScreen = Config.Bind("Batching", "SkipInstancesOutOfView", true,
                new ConfigDescription(
                    "Instanced pieces are handed to the graphics card again every frame, for every\nloaded zone, including the ones behind you. With this on, a group of instances\nwhose box is outside the camera is left out of the frame entirely. The box is\nstretched along the sun first, so shadows still land on what you can see."));

            BatchingMultiPartPieces = Config.Bind("Batching", "MultiPartInstancing", true,
                new ConfigDescription(
                    "Draw pieces built from more than one mesh - vines, log poles, tiled roofs - as GPU\ninstances as well, one draw per mesh and material. Without it only prefabs that come\ndown to a single renderer are instanced and the rest are merged or left to draw\nthemselves. Applies to zones baked after a change."));

            BatchingMinInstancesPerPrefab = Config.Bind("Batching", "MinInstancesPerPrefab", 10,
                new ConfigDescription(
                    "A prefab needs at least this many copies in one zone before it is\ndrawn as instances. Below it the pieces fall through to mesh combining,\nwhere they still batch by material with everything else.",
                    new AcceptableValueRange<int>(2, 10000)));

            BatchingDamageablePieces = Config.Bind("Batching", "DamageablePieces", true,
                new ConfigDescription(
                    "Also draw building pieces that can take damage as GPU instances. They stay\n" +
                    "in the world, so rain wear, support and raids work as normal, and a piece\n" +
                    "goes back to drawing itself the moment it is damaged, burns or is\n" +
                    "highlighted by a hammer. Never skipped. Applies to zones baked after a change."));

            BatchingFarTierEnabled = Config.Bind("Batching", "FarTierEnabled", true,
                new ConfigDescription(
                    "Bake a second combined mesh per zone from each piece's LOD1 geometry\n" +
                    "and swap to it once the viewer is far enough away. Without it a baked\n" +
                    "zone draws full LOD0 detail at every distance."));

            BatchingFarTierDistance = Config.Bind("Batching", "FarTierDistanceMeters", 64f,
                new ConfigDescription(
                    "Distance from the zone centre at which a baked zone swaps to its LOD1\n" +
                    "mesh. A zone is 64m across, so its centre is at most ~45m from any\n" +
                    "point inside it — keep this at or above 64 and the swap can never\n" +
                    "happen while you are standing in the zone watching it.",
                    new AcceptableValueRange<float>(48f, 512f)));

            BatchingSettleDelaySeconds = Config.Bind("Batching", "SettleDelaySeconds", 1.5f,
                new ConfigDescription(
                    "Wait this long after the last piece is added or removed in a zone\n" +
                    "before baking. Cooperates with VAGhetto's time-sliced instantiation:\n" +
                    "pieces arrive across many frames, settle-delay debounces until the\n" +
                    "zone is stable. Each new piece resets the timer.",
                    new AcceptableValueRange<float>(0.25f, 30f)));

            BatchingVerbose = Config.Bind("Batching", "VerboseLogging", false,
                "Log per-zone classification and bake details.");

            BatchingExcludeTransparent = Config.Bind("Batching", "ExcludeTransparentPieces", true,
                "Skip pieces with transparent-queue materials (renderQueue >= 2500) and\n" +
                "FiresGlass windows. Combining transparent geometry into a static batch\n" +
                "breaks per-piece depth sorting — stained glass sorts against the world\n" +
                "per pane, a merged mesh sorts once for the whole batch. Excluded pieces\n" +
                "stay live and render normally.");

            SkipBakedPieceObjects = Config.Bind("Batching", "SkipBakedPieceObjects", true,
                "Multiplayer clients only. Pieces a bake already draws are not created as game objects:\n" +
                "a stand-in collider takes each one's place, so they cost nothing per frame. Only\n" +
                "invulnerable, purely structural pieces qualify; anything that crafts, stores, lights,\n" +
                "protects, comforts or animates is always created. Real pieces come back while a build\n" +
                "tool is out nearby, or when a skipped piece is removed, moved or loses invulnerability.\n" +
                "Never runs on a server, host or single-player world.");
            SkipBakedPieceObjects.SettingChanged += (_, __) =>
            {
                if (!SkipBakedPieceObjects.Value) EasyBake.ZoneTracker.HandBackAllSkipped();
            };

            BatchingBuildRadius = Config.Bind("Batching", "BuildToolRadiusMeters", 24f,
                new ConfigDescription(
                    "While a build tool is out, the real pieces come back within this many metres of you, so the\nhammer can snap to them, highlight them and take them down. Everything further away stays\nbaked. In a dense base this is the most expensive thing skipping does, so keep it near what\nyou can actually reach.",
                    new AcceptableValueRange<float>(4f, 256f)));

            PrewarmEnabled = Config.Bind("Prewarm", "Enabled", true,
                "Workstream B: at ZNetScene.Awake, instantiate every prefab in m_prefabs\n" +
                "once at (0, -1000, 0), SetActive(false), then Destroy. Pays the hidden\n" +
                "first-Instantiate shader-compile / asset-bundle cost (5-35ms per heavy\n" +
                "prefab) once during loading rather than at zone borders during gameplay.");

            PrewarmRunOnServer = Config.Bind("Prewarm", "RunOnServer", false,
                "Run prewarm on dedicated server too. Default off — server doesn't render\n" +
                "so it gains nothing from shader-variant warm-up.");

            PrewarmPrefabsPerFrame = Config.Bind("Prewarm", "PrefabsPerFrame", 25,
                new ConfigDescription(
                    "Batch size of the prewarm coroutine. 25 keeps each loading-screen\n" +
                    "frame under ~50ms even when a batch hits some 5ms heavy prefabs.\n" +
                    "Higher = faster total warm-up but choppier loading screen.",
                    new AcceptableValueRange<int>(1, 500)));

            PrewarmVerbose = Config.Bind("Prewarm", "VerboseLogging", false,
                "Log every prefab + per-prefab ms during warm-up. Off by default; the\n" +
                "summary line carries totals + worst-single only.");

            PrewarmSkipNameContains = Config.Bind("Prewarm", "SkipNameContains", "",
                "Comma-separated list of case-insensitive substrings. Any prefab whose\n" +
                "name contains one of these tokens is skipped during prewarm. Empty by\n" +
                "default — main-thread responsiveness is preserved by the time-budgeted\n" +
                "yield pattern (see TimeBudgetMs below), not by skipping. Use this\n" +
                "config only when a specific prefab is misbehaving badly enough that\n" +
                "you'd rather pay the first-instantiate cost in-game than at load.");

            PrewarmFrameBudgetMs = Config.Bind("Prewarm", "FrameBudgetMs", 8f,
                new ConfigDescription(
                    "Time-budgeted yield: after each Instantiate, if the elapsed wall\n" +
                    "time on THIS frame exceeds this many ms, the coroutine yields and\n" +
                    "the next prefab runs on the next frame. Keeps the main thread\n" +
                    "responsive (loading screen + any open dialogs stay interactive)\n" +
                    "at the cost of longer total wall time. A single very-slow prefab\n" +
                    "(e.g. ConsMassiveTower's ~30s first-Instantiate) will still freeze\n" +
                    "the frame it lands on — Unity's shader-variant compile is\n" +
                    "synchronous and can't be yielded mid-Instantiate — but every\n" +
                    "other prefab around it stops piling on. Lower = smoother but\n" +
                    "longer. 8ms ≈ half a 16ms frame budget.",
                    new AcceptableValueRange<float>(1f, 100f)));

            PrewarmSlowInstantiateThresholdMs = Config.Bind("Prewarm", "SlowInstantiateThresholdMs", 5000f,
                new ConfigDescription(
                    "If a single prefab's Instantiate takes longer than this many ms,\n" +
                    "log a warning naming the prefab AND persist it to\n" +
                    "config/FiresEasyBakeMeshes/prewarm_slow_skips.txt, so every future\n" +
                    "session skips it before instantiating (delete a line from that file\n" +
                    "to give a prefab another chance, e.g. after removing the mod that\n" +
                    "made it slow). 0 to disable the runtime detector entirely.",
                    new AcceptableValueRange<float>(0f, 60000f)));

            PrewarmMaxTransformCount = Config.Bind("Prewarm", "MaxTransformCount", 800,
                new ConfigDescription(
                    "Skip any prefab whose transform hierarchy exceeds this many nodes,\n" +
                    "WITHOUT instantiating it. A single Instantiate is atomic on the main\n" +
                    "thread — the frame budget cannot split it — and combined-build\n" +
                    "mega-prefabs (e.g. ConsMassiveTower, ~29s) freeze the whole client\n" +
                    "on their first-ever warm-up, which the slow-instantiate detector\n" +
                    "above can only prevent from the SECOND session on. The node count is\n" +
                    "a cheap capped traversal; skipped prefabs warm on first real spawn\n" +
                    "instead. Normal pieces are tens of nodes; only combined builds reach\n" +
                    "the hundreds. 0 to disable the gate.",
                    new AcceptableValueRange<int>(0, 100000)));

            MaterialRegistryFrameBudgetMs = Config.Bind("Cache", "MaterialRegistryFrameBudgetMs", 4f,
                new ConfigDescription(
                    "Time-budgeted yield for the material-registry build pass that\n" +
                    "runs on ZNetScene.Awake (BEFORE prewarm starts). The build walks\n" +
                    "every prefab's MeshRenderers to index sharedMaterials by name —\n" +
                    "on a 5000+ prefab world this can take seconds synchronously,\n" +
                    "and the freeze lands right around the password-prompt /\n" +
                    "loading-screen-first-frame window. Yielding every Nms keeps the\n" +
                    "main thread interactive during that build. Lower = smoother but\n" +
                    "more total wall time. 4ms is roughly a quarter of a 16ms frame.",
                    new AcceptableValueRange<float>(1f, 50f)));

            CachePersistEnabled = Config.Bind("Cache", "PersistToDisk", true,
                "Save baked meshes to BepInEx/config/FiresEasyBakeMeshes/cache/<worldUid>/\n" +
                "and reload them on subsequent sessions of the same world. Eliminates the\n" +
                "per-zone Mesh.CombineMeshes cost on re-entry — disk read + Mesh.SetVertices\n" +
                "is ~4x faster than rebuilding from source pieces. Disable to always rebake\n" +
                "(useful when debugging or after admin-removing pieces, which the cache\n" +
                "can't detect and would leave as ghost geometry).");

            CreateDestroySkipEnabled = Config.Bind("Optimize", "CreateDestroySkipEnabled", true,
                "Skip ZNetScene.CreateDestroyObjects when the player hasn't crossed a sector\n" +
                "boundary AND no zone has loaded/unloaded since the last call. Vanilla does\n" +
                "a full O(loaded ZNetView) walk every 33ms; at a megabase that's ~6 ms/tick\n" +
                "of pure overhead finding zero things to remove. Skipping reclaims that for\n" +
                "the steady-state case (stationary at base). Set false to use pure vanilla.");

            CreateDestroySkipMaxSeconds = Config.Bind("Optimize", "CreateDestroySkipMaxSeconds", 1.0f,
                new ConfigDescription(
                    "Maximum time to skip CreateDestroyObjects even when nothing has changed.\n" +
                    "Forces a vanilla pass at this cadence so server-pushed ZDOs (creature\n" +
                    "spawns, remote player drops) eventually get instantiated on the client.\n" +
                    "Lower = more responsive to network events, less optimization gain.",
                    new AcceptableValueRange<float>(0.1f, 10f)));

            CreateDestroySkipVerbose = Config.Bind("Optimize", "CreateDestroySkipVerbose", false,
                "Log a periodic summary of how many CreateDestroyObjects ticks we skipped.");

            WearShortCircuitEnabled = Config.Bind("Optimize", "WearShortCircuitInvulnerable", true,
                "Skip WearNTear.UpdateWear and UpdateCover for invulnerable pieces.\n" +
                "Invulnerable pieces (m_health < 0 or all-Immune damage modifiers)\n" +
                "can't take damage, so vanilla's per-piece wet/roof/support/biome/lava\n" +
                "computation is wasted work — CanBeRemoved() zeros the accumulated\n" +
                "damage at the bottom of the method. At a megabase WearNTearUpdater is\n" +
                "the single largest per-frame cost; this short-circuit reclaims roughly\n" +
                "30 ms/sec. Disable only if a mod conflict requires vanilla behavior.");

            ZoneKeepaliveEnabled = Config.Bind("Optimize", "ZoneKeepaliveEnabled", true,
                "Pin baked zones in ZNetScene.m_instances when the player walks outside\n" +
                "the active ring, instead of letting vanilla destroy and re-Instantiate\n" +
                "them on the next zone-cross. Eliminates the multi-hundred-ms hitches\n" +
                "from cache-mesh reconstruction + mass re-Instantiate when moving around\n" +
                "town. Memory cost scales with KeepaliveRadius; CPU cost is near zero\n" +
                "with the WearNTear short-circuit also enabled.");

            ZoneKeepaliveRadius = Config.Bind("Optimize", "ZoneKeepaliveRadius", 15,
                new ConfigDescription(
                    "Max zone-distance from the player to keep baked zones pinned. Beyond\n" +
                    "this radius the zone is released and vanilla destroys its instances.\n" +
                    "Each zone is 64m. Default 15 = 960m radius — wide enough to cover\n" +
                    "Render Limits' extended view (typically +12 zones) so zones at the\n" +
                    "edge of the rendered area don't get destroyed when you walk around\n" +
                    "within town. With ~30 baked zones at a megabase the memory cost is\n" +
                    "bounded by the bake set, not the radius. Raise to 25-50 for very\n" +
                    "wide cities; drop to 5 for stock-vanilla active area.",
                    new AcceptableValueRange<int>(1, 50)));

            ZoneKeepaliveSeconds = Config.Bind("Optimize", "ZoneKeepaliveSeconds", 60f,
                new ConfigDescription(
                    "How long a baked zone stays pinned after the player last had it in\n" +
                    "the keepalive radius. After this, even though it's still tracked,\n" +
                    "the zone is released and vanilla destroys its instances on the next\n" +
                    "CreateDestroyObjects tick. Prevents stale pins from accumulating\n" +
                    "during long travel sessions.",
                    new AcceptableValueRange<float>(5f, 600f)));

            ZoneKeepaliveVerbose = Config.Bind("Optimize", "ZoneKeepaliveVerbose", false,
                "Log a periodic summary of how many zones are pinned and the refresh /\n" +
                "prune deltas. Useful for tuning the radius when paired with the\n" +
                "FiresDebugginTools profile output.");

            DestroyTimeSliceEnabled = Config.Bind("Optimize", "DestroyTimeSliceEnabled", true,
                "Replace ZNetScene.RemoveObjects' single-tick destroy loop with a queued,\n" +
                "time-budgeted version. Vanilla destroys every unearmarked instance in\n" +
                "one frame; at megabase scale with Render Limits expanding the loaded\n" +
                "ring, a single zone-cross can drop 10k+ pieces and produce a 100+ms\n" +
                "frame hitch. Time-slicing spreads the destroys across multiple frames\n" +
                "so the work is paced rather than spiked. Same total CPU cost, far\n" +
                "smoother frame distribution. Disable to use vanilla destroy semantics.");

            DestroyTimeSliceBudgetMs = Config.Bind("Optimize", "DestroyTimeSliceBudgetMs", 3f,
                new ConfigDescription(
                    "Per-frame ms budget for the destroy queue. Higher = drains faster\n" +
                    "but allows bigger spikes; lower = smoother frames but the queue\n" +
                    "takes more wall-clock time to drain. Default 3ms keeps a 10k-piece\n" +
                    "unload at ~5ms peak frame impact and drains in ~3 seconds.",
                    new AcceptableValueRange<float>(0.5f, 20f)));

            DestroyTimeSliceVerbose = Config.Bind("Optimize", "DestroyTimeSliceVerbose", false,
                "Log a periodic summary of destroyed / re-earmark-skipped counts and\n" +
                "current queue depth. Useful for confirming the slicer is draining at\n" +
                "the configured rate and not falling perpetually behind.");

            SectorMirrorEnabled = Config.Bind("Optimize", "SectorMirrorEnabled", true,
                "Maintain a per-sector instance-count mirror of ZNetScene.m_instances and\n" +
                "intercept ZNetScene.HaveInstanceInSector with an O(1) dictionary lookup.\n" +
                "Vanilla walks every entry in m_instances (146k+ at megabase) for each\n" +
                "ZoneSystem.UpdateTTL zone check — profile shows ZoneSystem.Update\n" +
                "spiking to 80+ ms/call during in-town movement. The mirror eliminates\n" +
                "that scan. Staleness from instances that move sectors after spawn\n" +
                "(creatures, ships) is corrected by a periodic full rebuild.");

            SectorMirrorRebuildSeconds = Config.Bind("Optimize", "SectorMirrorRebuildSeconds", 60f,
                new ConfigDescription(
                    "How often to rebuild the sector mirror from scratch. The rebuild\n" +
                    "walks m_instances once (cost similar to one vanilla HaveInstanceInSector\n" +
                    "call) and corrects any drift from instances that moved sectors since\n" +
                    "instantiation. Lower = tighter accuracy, slightly more cost; higher =\n" +
                    "longer-lived false positives (zones held alive after their creature\n" +
                    "wandered away). 60s is the default sweet spot. Set 0 to disable rebuilds\n" +
                    "entirely (only safe for fully-static worlds).",
                    new AcceptableValueRange<float>(0f, 600f)));

            SectorMirrorVerbose = Config.Bind("Optimize", "SectorMirrorVerbose", false,
                "Log a line each time the sector mirror rebuilds, showing tracked ZDO\n" +
                "and sector counts. Useful for verifying the mirror is staying populated.");

            LightFlickerLodEnabled = Config.Bind("Optimize", "LightFlickerLodEnabled", true,
                "Distance-tiered throttling of LightFlicker.CustomUpdate. Vanilla runs\n" +
                "flicker math (6+ sin/cos calls + position jitter) every frame for every\n" +
                "lit prefab in the scene; at a megabase with hundreds of torches/braziers\n" +
                "this aggregates to 8-10 ms/sec. Distant lights update less often instead\n" +
                "of being skipped outright — vanilla zeroes a light's intensity when it is\n" +
                "enabled and only CustomUpdate writes it back, so a light that never ticks\n" +
                "renders black. Skipped time is banked and handed to the next real update,\n" +
                "so flicker and fade still run at the correct speed. Temporary FX lights\n" +
                "(m_ttl > 0) and lights still fading in always run every frame.");

            LightFlickerLodDistance = Config.Bind("Optimize", "LightFlickerLodDistance", 30f,
                new ConfigDescription(
                    "Radius in meters within which lights flicker at full vanilla rate.\n" +
                    "Beyond it they step down through the mid / far / distant tiers below.",
                    new AcceptableValueRange<float>(5f, 200f)));

            LightFlickerMidDistance = Config.Bind("Optimize", "LightFlickerMidDistance", 60f,
                new ConfigDescription(
                    "Outer radius of the mid tier. Lights between LightFlickerLodDistance\n" +
                    "and this distance update at LightFlickerMidRate.",
                    new AcceptableValueRange<float>(10f, 300f)));

            LightFlickerFarDistance = Config.Bind("Optimize", "LightFlickerFarDistance", 100f,
                new ConfigDescription(
                    "Outer radius of the far tier. Lights between LightFlickerMidDistance\n" +
                    "and this distance update at LightFlickerFarRate; anything beyond uses\n" +
                    "LightFlickerDistantRate.",
                    new AcceptableValueRange<float>(20f, 500f)));

            LightFlickerMidRate = Config.Bind("Optimize", "LightFlickerMidRate", 10f,
                new ConfigDescription(
                    "Updates per second for lights in the mid distance tier.",
                    new AcceptableValueRange<float>(1f, 60f)));

            LightFlickerFarRate = Config.Bind("Optimize", "LightFlickerFarRate", 5f,
                new ConfigDescription(
                    "Updates per second for lights in the far distance tier.",
                    new AcceptableValueRange<float>(1f, 30f)));

            LightFlickerDistantRate = Config.Bind("Optimize", "LightFlickerDistantRate", 2f,
                new ConfigDescription(
                    "Updates per second for lights beyond LightFlickerFarDistance. Keep\n" +
                    "above zero so distant lights still receive an intensity write.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            LightFlickerTierCheckRate = Config.Bind("Optimize", "LightFlickerTierCheckRate", 2f,
                new ConfigDescription(
                    "How often each light re-measures its distance tier. Caching the tier\n" +
                    "is what removes the per-frame distance math; raise it only if lights\n" +
                    "visibly lag a tier change while you sprint past them.",
                    new AcceptableValueRange<float>(0.5f, 10f)));

            ZSFXLodEnabled = Config.Bind("Optimize", "ZSFXLodEnabled", true,
                "Distance-LOD on ZSFX.CustomUpdate. ZSFX is Valheim's per-sound wrapper\n" +
                "around Unity's AudioSource — manages fade in/out, pitch/volume modifiers,\n" +
                "reverb routing, and concurrency suppression. It ticks every frame on every\n" +
                "active sfx in the scene; at a megabase with hundreds of torch / brazier /\n" +
                "fountain loops this aggregates to ~3 ms/sec. Unity's audio engine handles\n" +
                "spatial attenuation independently of CustomUpdate, so beyond the audible\n" +
                "falloff radius the per-frame housekeeping has no perceptible effect on what\n" +
                "the player hears. We skip CustomUpdate entirely beyond the configured\n" +
                "distance; the AudioSource keeps its last set volume / pitch which already\n" +
                "attenuates to ~zero at that range.");

            ZSFXLodDistance = Config.Bind("Optimize", "ZSFXLodDistance", 60f,
                new ConfigDescription(
                    "Distance in meters beyond which ZSFX.CustomUpdate is skipped. 60 m\n" +
                    "is conservative — most ambient sfx have AudioSource.maxDistance under\n" +
                    "50 m so they're inaudible past that even when CustomUpdate is still\n" +
                    "running. Music and ambient zones have longer reach; raise this if you\n" +
                    "hear audio pop in/out at the edge of your hearing range. Lower for\n" +
                    "more aggressive savings.",
                    new AcceptableValueRange<float>(10f, 200f)));

            ZSyncStaticSkipEnabled = Config.Bind("Optimize", "ZSyncStaticSkipEnabled", true,
                "Disable ZSyncTransform on player-built Pieces that never move. ZSyncTransform\n" +
                "is an IMonoUpdater that ticks every frame on every active instance, even when\n" +
                "the piece is a static wall whose position has been settled since placement.\n" +
                "Setting enabled=false on the component removes it from ZSyncTransform.Instances\n" +
                "via OnDisable so the per-frame iteration stops visiting it.\n" +
                "\n" +
                "Eligibility is narrow and conservative: must be a Piece (player-built), must\n" +
                "have no parent transform (rules out ship/cart attachments), no body-velocity\n" +
                "or character-parent sync flag, no non-kinematic Rigidbody, and no component\n" +
                "from the animated/interactable safelist (Door, Container, Sign, Smelter,\n" +
                "CookingStation, Fireplace, Pickable, TeleportWorld, Bed, Ship, Vagon,\n" +
                "MineRock, ShieldGenerator, …) — the same surface as the mesh-bake unsafe\n" +
                "filter plus all interactables.\n" +
                "\n" +
                "Risk surface: in solo this is essentially free. In multiplayer an admin\n" +
                "teleport on a static piece won't propagate to other clients until reload.\n" +
                "Disable this toggle if you hit a sync issue in MP and we'll look at it.");

            ZSyncStaticSkipVerbose = Config.Bind("Optimize", "ZSyncStaticSkipVerbose", false,
                "Log a periodic summary of how many pieces have had their ZSync disabled and\n" +
                "how many prefabs are in the eligibility cache. Useful for verifying the\n" +
                "filter is hitting your prefab set as expected after a deploy.");

            ClutterLodEnabled = Config.Bind("Optimize", "ClutterLodEnabled", true,
                "Skip ClutterSystem.LateUpdate while the player is standing still. Vanilla\n" +
                "re-walks the full grass-patch ring around the camera TWICE every frame\n" +
                "(generate + timeout, ~120 patches at default range) even when parked —\n" +
                "FiresDebugginTools measures this at ~3 ms EVERY frame, the largest steady\n" +
                "cost on a natural (non-megabase) world. The grass set is identical frame to\n" +
                "frame while stationary, so we skip the pass until the player moves, with a\n" +
                "short TTL (below) keeping it ticking a few times a second and an immediate\n" +
                "escape when a rebuild is pending (terrain edit, quality change, ResetGrass).\n" +
                "Moving = full vanilla every frame, so grass-in and the player push effect\n" +
                "are unchanged. Disable if you see grass not refreshing while standing still.");

            ClutterLodMoveThreshold = Config.Bind("Optimize", "ClutterLodMoveThreshold", 0.25f,
                new ConfigDescription(
                    "How far (meters) the player must move from the last clutter pass before\n" +
                    "ClutterSystem.LateUpdate runs again. Below this the player counts as\n" +
                    "parked and the pass is skipped. 0.25 m skips only when genuinely still;\n" +
                    "camera rotation / mouse-look doesn't move the player so it still skips.",
                    new AcceptableValueRange<float>(0.05f, 8f)));

            ClutterLodMaxSeconds = Config.Bind("Optimize", "ClutterLodMaxSeconds", 0.33f,
                new ConfigDescription(
                    "Maximum time ClutterSystem.LateUpdate is skipped while parked before a\n" +
                    "forced vanilla pass. Keeps the grass-push trail decaying and picks up\n" +
                    "async heightmap-ready transitions. Lower = more responsive, less savings;\n" +
                    "higher = more savings, longer-lived frozen push trail. 0.33s ≈ 3 Hz.",
                    new AcceptableValueRange<float>(0.05f, 5f)));

            ClutterLodVerbose = Config.Bind("Optimize", "ClutterLodVerbose", false,
                "Log a one-line summary every ~5 seconds tallying ClutterLod calls: how many were\n" +
                "skipped vs passed through, and the reason for each passthrough (moved past threshold,\n" +
                "ttl expired, m_forceRebuild pending, no local player, freefly, disabled). Useful when\n" +
                "ClutterSystem.LateUpdate cost in the FiresDebugginTools overlay doesn't drop while\n" +
                "parked — the line will tell you whether the prefix is even being called, and if so,\n" +
                "what's forcing it to pass through to vanilla.");

            EasyBake.MeshCacheStore.Initialize();

            // Create the Harmony instance up front so the SettingChanged
            // handler below can patch/unpatch on the fly. The instance is
            // also reused by ApplyPatches / RemovePatches so toggling
            // PluginEnabled live never requires a restart.
            _harmony = new Harmony(PluginGUID);

            // Live master-toggle. When the user flips PluginEnabled in the
            // config manager, apply or remove all our patches immediately,
            // and on the OFF path also call Reset() on every stateful
            // subsystem so any baked zones, pinned ZDOs, destroy queues,
            // cached prefab classifications, etc. are unwound cleanly.
            PluginEnabled.SettingChanged += (_, __) =>
            {
                if (PluginEnabled.Value && !_patchesApplied)
                {
                    ApplyPatches();
                    EasyBakeLog.Info($"{PluginName} re-enabled via config — patches restored.");
                }
                else if (!PluginEnabled.Value && _patchesApplied)
                {
                    RemovePatches();
                    EasyBakeLog.Info($"{PluginName} disabled via config — patches removed, subsystem state reset.");
                }
            };

            if (PluginEnabled.Value)
                ApplyPatches();
            else
                EasyBakeLog.Info(
                    $"{PluginName} v{PluginVersion} loaded but PluginEnabled=false — no patches applied. " +
                    "Flip PluginEnabled to true in the config manager to enable live (no restart needed).");

            try { Utilities.EbmHelpContent.Register(); }
            catch (System.Exception ex) { EasyBakeLog.Warn($"Help registration failed: {ex.Message}"); }

            // Compact "loaded" banner — oven with heat squiggles. Deferred to
            // world-load time (when ZNetScene is up) so it bookends the load;
            // the BIG "loading" banner already fired at the top of Awake.
            StartCoroutine(EmitCompactBannerWhenZNetReady());
        }

        // Waits for ZNetScene + its prefab table to be live (same readiness
        // signal the other Fires mods use), then emits the compact loaded
        // banner. Failure is non-fatal — falls back to a plain "loaded" log
        // line so the load event is still recorded in the file log.
        private IEnumerator EmitCompactBannerWhenZNetReady()
        {
            while (ZNetScene.instance == null
                   || ZNetScene.instance.m_prefabs == null
                   || ZNetScene.instance.m_prefabs.Count == 0)
            {
                yield return null;
            }
            yield return new WaitForEndOfFrame();

            try { FiresEasyBakeBanner.Print(); }
            catch (System.Exception ex)
            {
                EasyBakeLog.Info($"{PluginName} v{PluginVersion} loaded. (banner failed: {ex.Message})");
            }
        }

        // Idempotent. Walks every [HarmonyPatch] class in this assembly and
        // patches it, logging per-patch failures so a single bad target
        // doesn't take down the whole set. Sets _patchesApplied = true on
        // first run; subsequent calls are no-ops.
        private void ApplyPatches()
        {
            if (_patchesApplied) return;

            int succeeded = 0, failed = 0;
            foreach (var type in typeof(FiresEasyBakeMeshesPlugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    _harmony.CreateClassProcessor(type).Patch();
                    succeeded++;
                }
                catch (System.Exception ex)
                {
                    failed++;
                    EasyBakeLog.Error($"Patch {type.Name} failed: {ex.Message}");
                }
            }
            _patchesApplied = true;
            EasyBakeLog.Info(
                $"{PluginName} v{PluginVersion} active — {succeeded} patches registered, {failed} skipped. " +
                $"Batching={(BatchingEnabled.Value ? "on" : "off")}, Prewarm={(PrewarmEnabled.Value ? "on" : "off")}.");
        }

        // Removes every patch this Harmony instance applied (won't touch
        // patches owned by other mods), then resets every EasyBake subsystem
        // that holds state so a re-enable later starts clean. ZoneTracker.Reset
        // is the cascade — it tears down all baked zones (calling
        // MeshBaker.Restore per zone to flip MeshRenderers back on) and
        // internally invokes Reset on InvulnerableClassifier, ZoneKeepalive,
        // DestroyTimeSlicer, SectorInstanceMirror, and StaticPieceZSyncSkip.
        // The two patch classes that carry per-frame skip-cache state
        // (CreateDestroyObjectsPatches, ClutterSystemPatches) need their own
        // ResetState calls — they're not subsystems ZoneTracker knows about.
        private void RemovePatches()
        {
            if (!_patchesApplied) return;
            try { _harmony?.UnpatchSelf(); }
            catch (System.Exception ex) { EasyBakeLog.Error($"UnpatchSelf failed: {ex.Message}"); }

            try { EasyBake.ZoneTracker.Reset(); } catch { }
            try { Patches.ZNetScene_CreateDestroyObjects_Patch.ResetState(); } catch { }
            try { Patches.ClutterSystem_LateUpdate_Patch.ResetState(); } catch { }

            _patchesApplied = false;
        }

        // Tear down on plugin unload so the host process doesn't leave our
        // patches dangling against assembly_valheim. UnpatchSelf is a no-op
        // when _patchesApplied is false.
        private void OnDestroy() => RemovePatches();

        private void Update()
        {
            // Kick the disk-cache background preload as soon as the world UID
            // is available. ZNet.GetWorldUID returns 0 until the server world
            // handshake completes; once it's non-zero, the cache directory
            // for this world is well-defined and we can start reading. The
            // worker runs on a ThreadPool task; touching only file I/O + POCO
            // arrays, no Unity API. By the time pieces start streaming in for
            // any zone, most or all of its cache file is already parsed into
            // memory, so the per-zone hit cost is just Mesh.SetVertices.
            //
            // KickPreload is idempotent and cheap when already kicked, so
            // calling it every Update before the first non-zero UID is fine.
            //
            // Route through MeshCacheStore.TryGetWorldUid() — it pre-checks
            // ZNet.m_world via reflection. Vanilla GetWorldUID() does an
            // unchecked dereference of m_world and NREs every frame in the
            // ~6s window between ZNet.instance being assigned and the world
            // handshake landing.
            if (CachePersistEnabled.Value)
            {
                long uid = EasyBake.MeshCacheStore.TryGetWorldUid();
                if (uid != 0L) EasyBake.MeshCacheStore.KickPreload(uid);
            }

            // Debounced ZONES BAKED mini-box: emits once ~5s after the last
            // cache-construct / fresh-bake event. Runs before the batching
            // gate so the final box still lands if batching is toggled off
            // right after login.
            BakeSummary.Update();

            if (!BatchingActive()) return;
            long tZone = EasyBake.Probe.Start();
            long trackStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EasyBake.ZoneTracker.Update();
            EasyBake.InstancedDraw.NoteZoneTracking(System.Diagnostics.Stopwatch.GetTimestamp() - trackStart);
            EasyBake.Probe.Stop("EasyBake:zoneTrack", tZone);

            // Keepalive lifecycle: refresh entries within the active ring,
            // prune entries that have drifted past KeepaliveRadius or aged
            // out. The RemoveObjects prefix consults the resulting set every
            // CreateDestroyObjects tick.
            if (ZoneKeepaliveEnabled.Value
                && ZNet.instance != null
                && ZoneSystem.instance != null
                && Player.m_localPlayer != null)
            {
                var refPos = ZNet.instance.GetReferencePosition();
                var center = ZoneSystem.GetZone(refPos);
                // Valheim 1.0: the fixed m_activeArea radius became the player-configurable,
                    // server-synced SimulationDistance.
                    int activeArea = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;
                long tKeep = EasyBake.Probe.Start();
                EasyBake.ZoneKeepalive.Update(center, activeArea);
                EasyBake.Probe.Stop("EasyBake:keepalive", tKeep);
            }

            // Sector mirror periodic rebuild — corrects drift from instances
            // that moved sectors since they were instantiated. Cheap when
            // disabled (SectorMirrorRebuildSeconds=0) or interval not yet
            // elapsed; expensive (~1 walk of m_instances) when it actually
            // rebuilds.
            if (SectorMirrorEnabled.Value && ZNetScene.instance != null)
            {
                long tMirror = EasyBake.Probe.Start();
                EasyBake.SectorInstanceMirror.MaybeRebuild(ZNetScene.instance);
                EasyBake.Probe.Stop("EasyBake:sectorMirror", tMirror);
            }

            // Static-piece ZSync skip: periodic verbose summary if enabled.
            // No-op when ZSyncStaticSkipVerbose=false. Cheap enough to call
            // every Update — guards on the throttle internally.
            EasyBake.StaticPieceZSyncSkip.MaybeReport();
        }

        // Instances are drawn here rather than in Update because GameCamera moves the
        // camera in its own LateUpdate: deciding what is out of view any earlier would
        // test last frame's view and let a fast turn clip geometry at the screen edge.
        private void LateUpdate()
        {
            if (!BatchingActive()) return;
            EasyBake.ZoneTracker.DrawInstances();
        }

        public static bool BatchingActive()
        {
            if (!PluginEnabled.Value) return false;
            if (!BatchingEnabled.Value) return false;
            if (BatchingRunOnServer.Value) return true;
            return !(ZNet.instance != null && ZNet.instance.IsDedicated());
        }
    }
}
