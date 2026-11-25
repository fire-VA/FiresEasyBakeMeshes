using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// #5 Far buildings ([ghost], Fire 11-08: "far buildings we want but we need to do it clean as possible and performative").
    /// Draws the pieces of zones that are NOT loaded, out to Reach, as merged meshes that use the pieces' own shared materials
    /// (normal maps, live sun, the game's fog, Vedr's snow look). Inside the loaded area nothing is touched: vanilla, EBM and FAP
    /// own it. VA prefabs that FAP's VA distant method (registered into FarBuildingDraw.VaDistantReach) already shows at that distance are skipped, so our VA distant method stays
    /// the bar. Nothing runs until EBM calls Initialize (its own Awake) and [Far buildings] Enabled
    /// is true. No Harmony patches.
    ///
    /// Parts: FarBuildingDigest (which pieces stand in each zone), FarBuildingSources (one far mesh per prefab, read once),
    /// FarBuildingMerge (worker-thread merge per zone / 512 m cell), FarBuildingDraw (upload, chunks, hand-off),
    /// FarBuildingNet (server sends zone digests to clients, its own keys, default off).
    /// </summary>
    internal static class FarBuildings
    {
        internal const string Log = "[FarBuildings]";
        private const string Section = "04 - Far Buildings";   // 1.2.444: was "Far buildings" (EbmConfigMove carries saved values)

        // â”€â”€ Config (defaults are what readers see until EBM binds) â”€â”€
        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_reach;
        private static ConfigEntry<int> s_maxVertices;
        private static ConfigEntry<float> s_budgetMs;
        private static ConfigEntry<float> s_cellFrom;
        private static ConfigEntry<bool> s_timingLog;
        private static ConfigEntry<bool> s_clientAsks;
        private static ConfigEntry<bool> s_serverSends;

        internal static bool Enabled => s_enabled != null && s_enabled.Value && FarRendering.On;   // 1.2.458: AND the Far Rendering master
        internal static float Reach => FarReach.For(OwnReach);   // 1.2.483: Far Zone Sync = the one shared reach (FarReach.Effective), else OwnReach
        internal static float OwnReach => Mathf.Clamp(FarReach.Or(s_reach != null ? s_reach.Value : 2500f), 300f, 5000f);   // 1.2.344: [Far] Far reach wins when set (1.2.483: the 1.2.482 reach, unchanged)
        internal static int MaxVertices => s_maxVertices != null ? Mathf.Clamp(s_maxVertices.Value, 100000, 8000000) : 2000000;
        private static ConfigEntry<float> s_arrivalMs;
        internal static double ArrivalBudgetMs => s_arrivalMs != null ? Mathf.Clamp(s_arrivalMs.Value, 0f, 12f) : 6.0;
        // 1.2.220 ([generator]): client only (a dedicated server never has a local player, so it ran the arrival budget forever), and at most 90 s
        // after an arrival began whatever the fill tracker is still measuring
        private static bool Arriving()
        {
            if (!FarRunner.Client || (ZNet.instance != null && ZNet.instance.IsDedicated())) return false;
            var p = Player.m_localPlayer;
            if (p == null || p.IsTeleporting()) return true;
            return FillTracker.Watching && FillTracker.SecondsWatching < 90f;
        }
        internal static double BudgetMs => s_budgetMs != null ? Mathf.Clamp(s_budgetMs.Value, 0.1f, 4f) : 0.5;
        private static ConfigEntry<bool> s_coldBudget; private static bool s_coldSaid;   // 1.2.346
        private static bool s_movingCapSaid; private static float s_nextBacklogSaid;   // 1.2.355
        private static bool s_edgeSaid; private const double EdgeBudgetMs = 1.5;   // 1.2.424 edge ring budget
        internal static bool ColdBudgetOn => s_coldBudget == null || s_coldBudget.Value;
        private static ConfigEntry<bool> s_digestBaseAfterLap;   // 1.2.476
        // 1.2.477 (lead 11-20: split the FarRunner row in a normal capture; ENABLE_PROFILER is defined in the csproj): the far-buildings steps
        private static readonly Unity.Profiling.ProfilerMarker s_markDigest = new Unity.Profiling.ProfilerMarker("EBM Far buildings: digest scan"), s_markSources = new Unity.Profiling.ProfilerMarker("EBM Far buildings: sources"), s_markDraw = new Unity.Profiling.ProfilerMarker("EBM Far buildings: plan + prep + upload + hand-off");
        internal static bool DigestBaseAfterLap => s_digestBaseAfterLap != null && s_digestBaseAfterLap.Value;
        internal static float CellFrom => s_cellFrom != null ? Mathf.Clamp(s_cellFrom.Value, 400f, 5000f) : 1000f;
        // 1.2.353 ([2ic], [lead] 11-13 overnight; Fire: "far drawing must ACTUALLY WORK: whole builds + rocks from far"; run_16 "evicted by the vertex cap 88
        // (nearest evicted 994 m) ... 7,999,767 vertices"): a COARSE tier past "Coarse tier from (m)": the same 512 m cells, clustered at "Coarse cluster (m)"
        // instead of 1 m, with pieces whose longest side is under "Coarse small piece (m)" left out. Whole silhouettes stay (each piece's outline moves by at
        // most one cluster cell); only the detail coarsens. OFF until its rig gate (dock ABBA + Fire's look at 1-2 km): a look change.
        private static ConfigEntry<bool> s_coarseOn;
        private static ConfigEntry<float> s_coarseFrom, s_coarseCluster, s_coarseSmall;
        internal static bool CoarseOn => s_coarseOn != null && s_coarseOn.Value;
        private static ConfigEntry<bool> s_interior, s_sizeCut; private static ConfigEntry<float> s_sizeCutMax;   // 1.2.431
        // 1.2.432 ([visual], lead approved; Fire's census: 50 zone copies = 7.99 M vertices within 660 m, stone_wall_2x1 ~429 v each): zone copies were merged at FULL
        // detail inside 'Cell merge from' (Simplify = IsCell only); past this distance they are clustered like cells, the grid growing from 0.5 m here to 1 m at 'Cell merge from'.
        private static ConfigEntry<float> s_simplifyFrom;
        internal static float SimplifyFrom => s_simplifyFrom != null ? Mathf.Clamp(s_simplifyFrom.Value, 100f, 5000f) : 300f;
        // the cluster grid (m) for a target at distance d (0 = no clustering), quantized to 0.25 m so a moving player keeps cache hits
        internal static float ClusterFor(float d, bool isCell, bool coarse)
        {
            if (coarse) return CoarseCluster;
            if (isCell) return 1f;
            if (d <= SimplifyFrom) return 0f;
            float g = Mathf.Lerp(0.5f, 1f, Mathf.Clamp01((d - SimplifyFrom) / Mathf.Max(1f, CellFrom - SimplifyFrom)));
            return Mathf.Round(g * 4f) / 4f;
        }
        internal static bool InteriorOn => s_interior == null || s_interior.Value;
        internal static bool SizeCutOn => s_sizeCut == null || s_sizeCut.Value;
        internal static float SizeCutMax => s_sizeCutMax != null ? Mathf.Clamp(s_sizeCutMax.Value, 0.5f, 8f) : 3f;
        // the size under which a piece is left out at a distance: 0 inside 'Coarse tier from', then Coarse small piece rising to Size cut max at the reach
        internal static float SmallCutAt(float d, bool coarse)
        {
            float baseCut = coarse ? CoarseSmall : 0f;
            if (!SizeCutOn || d <= CoarseFrom) return baseCut;
            float k = Mathf.Clamp01((d - CoarseFrom) / Mathf.Max(1f, Reach - CoarseFrom));
            return Mathf.Max(baseCut, Mathf.Lerp(Mathf.Max(CoarseSmall, 0.5f), Mathf.Max(SizeCutMax, CoarseSmall), k));
        }
        internal static float CoarseFrom => Mathf.Max(CellFrom, s_coarseFrom != null ? Mathf.Clamp(s_coarseFrom.Value, 600f, 5000f) : 1500f);
        internal static float CoarseCluster => s_coarseCluster != null ? Mathf.Clamp(s_coarseCluster.Value, 1f, 4f) : 2f;
        internal static float CoarseSmall => s_coarseSmall != null ? Mathf.Clamp(s_coarseSmall.Value, 0f, 3f) : 1f;
        internal static bool TimingLog => s_timingLog != null && s_timingLog.Value;
        internal static bool ClientAsks => s_clientAsks != null && s_clientAsks.Value;
        internal static bool ServerSends => s_serverSends != null && s_serverSends.Value;

        /// <summary>Called once by EBM (plugin Awake) with its own ConfigFile. Binds [Far buildings]; every key defaults off.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_enabled != null) return;
            // 1.2.458 (Fire 11-18): [03 - Far Rendering] Far Buildings (was [04 - Far Buildings] Enabled; EbmConfigMove carries a saved value), under the master switch
            s_enabled = EbmConfigMove.Bind(config, FarRendering.Section, "Far Buildings", false /* 1.2.211 had it ON; 1.2.263 (Fire 11-10 4:15 AM: far rendering defaults off) OFF. Off = no scans, no worker, no GPU readbacks (Update returns first) */,
                "Draws your visited buildings out to the far view distance (merged far meshes with the pieces' own materials). Needs Far Rendering on (on a server too, for the far-base keys in [04 - Far Buildings]).");
            s_reach = EbmConfigMove.Bind(config, Section, "Reach (m)", 2500f,
                new ConfigDescription("How far far buildings are drawn.", new AcceptableValueRange<float>(300f, 5000f)));
            s_maxVertices = EbmConfigMove.Bind(config, Section, "Max vertices", 2000000,
                new ConfigDescription("Cap on far-building vertices (24 bytes each on the GPU). The farthest are dropped first.",
                    new AcceptableValueRange<int>(100000, 8000000)));
            s_arrivalMs = EbmConfigMove.Bind(config, Section, "Arrival budget (ms)", 6f, new ConfigDescription("1.2.219: main-thread ms a frame for far buildings while you arrive (loading screen, teleport, and until the area has filled in), so distant builds show from the first seconds. 0 = use the normal budget.", new AcceptableValueRange<float>(0f, 12f)));
            s_digestBaseAfterLap = EbmConfigMove.Bind(config, Section, "FarDigestBaseBudgetAfterLap", false /* 1.2.476: OFF until the fire_bf1 A/B (new-key rule) */,
                "Once every zone in far reach has been read once, keeping the far buildings' list fresh (pieces placed or removed) uses only the main-thread budget instead of the arrival / cold budget. Building far copies keeps the arrival / cold budget. Off = the list is re-read with the full far budget every frame while far copies are still waiting. Applies live.");
            s_coldBudget = EbmConfigMove.Bind(config, Section, "Cold-set arrival budget", true /* 1.2.346 FIX: the far set never finished building at 0.5 ms a frame */,
                "While far buildings in reach are still waiting to be built, the far tier uses the arrival budget each frame instead of the main-thread budget, so the whole far set is up within the first minutes after a load or a long move; back to the main-thread budget once everything in reach is built. Off = the steady 0.5 ms from the start (slow to fill).");
            s_budgetMs = EbmConfigMove.Bind(config, Section, "Main thread budget (ms)", 0.5f,
                new ConfigDescription("Main-thread time per frame for scanning, snow and uploads. Merging runs on a worker thread.",
                    new AcceptableValueRange<float>(0.1f, 4f)));
            s_cellFrom = EbmConfigMove.Bind(config, Section, "Cell merge from (m)", 1000f,
                new ConfigDescription("Past this distance zones merge into 512 m cells and are simplified to about 1 m.",
                    new AcceptableValueRange<float>(400f, 5000f)));
            s_coarseOn = EbmConfigMove.Bind(config, Section, "Coarse tier", false /* 1.2.353: OFF until its gate (dock ABBA + Fire's look); a look change */,
                "Past 'Coarse tier from (m)', far buildings are merged with a coarser simplification and small pieces are left out, so more whole builds fit under 'Max vertices' farther out. Outlines stay; fine detail goes.");
            s_coarseFrom = EbmConfigMove.Bind(config, Section, "Coarse tier from (m)", 1500f,
                new ConfigDescription("Where the coarse tier starts (never nearer than 'Cell merge from (m)').", new AcceptableValueRange<float>(600f, 5000f)));
            s_coarseCluster = EbmConfigMove.Bind(config, Section, "Coarse cluster (m)", 2f,
                new ConfigDescription("Simplification grid of the coarse tier, in metres (the cell tier uses 1 m). Larger = fewer vertices, blockier.", new AcceptableValueRange<float>(1f, 4f)));
            s_coarseSmall = EbmConfigMove.Bind(config, Section, "Coarse small piece (m)", 1f,
                new ConfigDescription("In the coarse tier, pieces whose longest side is under this are left out (0 = keep every piece).", new AcceptableValueRange<float>(0f, 3f)));
            // 1.2.431 ([visual], lead approved; Fire: "some of the far pieces would be naturally covered and not shown so they could be thinned out"): two cuts, each its own key
            s_interior = EbmConfigMove.Bind(config, Section, "Interior pieces left out", true,
                "Far buildings leave out pieces that are boxed in on every side by other pieces of the same building (inner walls, floors under a roof, stacked foundations). The outer walls and roofs always stay. Frees room under 'Max vertices' for more buildings far away.");
            s_sizeCut = EbmConfigMove.Bind(config, Section, "Size cut grows with distance", true,
                "Past 'Coarse tier from (m)', small pieces are left out, and the size left out grows with distance up to 'Size cut max (m)' at the far reach. Outlines stay; small detail that is under a pixel goes.");
            s_sizeCutMax = EbmConfigMove.Bind(config, Section, "Size cut max (m)", 3f, new ConfigDescription("With 'Size cut grows with distance': pieces whose longest side is under this are left out at the far reach (rising from 'Coarse small piece (m)' at 'Coarse tier from (m)').", new AcceptableValueRange<float>(0.5f, 8f)));
            s_simplifyFrom = EbmConfigMove.Bind(config, Section, "Simplify zones from (m)", 300f, new ConfigDescription("1.2.432: zone copies farther than this are simplified (vertex clustering, 0.5 m here rising to 1 m at 'Cell merge from'), as the 512 m cells already are. At these distances one cluster is under a pixel; many more buildings fit under 'Max vertices'.", new AcceptableValueRange<float>(100f, 5000f)));
            s_simplifyFrom.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_interior.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_sizeCut.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_sizeCutMax.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_coarseOn.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_coarseFrom.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_coarseCluster.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_coarseSmall.SettingChanged += (_, __) => FarBuildingDraw.TiersChanged();
            s_timingLog = EbmConfigMove.Bind(config, Section, "Timing log", false,
                "Every 10 s, a Debug line with main-thread and worker time, zones, pieces, vertices, draws and GPU memory.");
            s_clientAsks = EbmConfigMove.Bind(config, Section, "Ask the server for far bases", false,
                "On a client: ask the server for the bases you have not visited (needs the server key below on the server).");
            s_serverSends = EbmConfigMove.Bind(config, Section, "Server sends far bases to clients", false,
                "On a server or host: answer clients' far-base requests with compact zone lists (changed zones only, paced).");
            s_enabled.SettingChanged += (_, __) => { s_wantRestate = true; s_standDownDirty = true; };   // 1.2.269: a toggle re-judges the stand-down
            FarRendering.Changed += () => { s_wantRestate = true; s_standDownDirty = true; };   // 1.2.458: the same on the master switch (Update stops / starts on Enabled)
            FarRunner.Register("FarBuildings", Update, Late, serverToo: true);
            FarRunner.Register("FarGroundProbe", null, FarGroundProbe.Late);   // [Optimize] FarGroundProbe (E20): counts only, client only, idle while the key is off
        }

        // â”€â”€ State â”€â”€
        private static bool s_wantRestate = true;
        private static bool s_running;
        private static string s_standDown;
        private static float s_nextTimingLog;
        private static ZNet s_world;
        internal static readonly Stats S = new Stats();

        internal sealed class Stats
        {
            public long ScanTicks, SnowTicks, UploadTicks, HandoffTicks, WorkerTicks; public int Frames;
            public int Zones, Targets, Pieces, Vertices, Draws, Evictions, Handoffs, HandoffTimeouts, ReadbackFails; public int MeshCpu, MeshGpu, MeshCached; public long GpuBytes; public long WorstTicks;
            public void ResetWindow() { ScanTicks = SnowTicks = UploadTicks = HandoffTicks = WorkerTicks = 0; Frames = 0; WorstTicks = 0; Evictions = Handoffs = HandoffTimeouts = 0; }
        }

        private static void Update()
        {
            bool live = Enabled;   // EBM port: Core had CoreFeatures.IsLive(FarBuildings) && Enabled; EBM owns the feature, so live = its own key
            if (!live) { if (s_running) Stop("the key is off"); return; }
            if (s_wantRestate || !s_running) { s_wantRestate = false; if (!Start()) return; }
            if (ZNet.instance == null || ZDOMan.instance == null || ZNetScene.instance == null) return;
            if (!ReferenceEquals(s_world, ZNet.instance))
            {
                // A new world or session: nothing from the last one may be drawn here.
                s_world = ZNet.instance;
                FarBuildingDraw.Clear();
                FarBuildingDigest.ResetForWorld();
                // 1.2.344 ([ghost]): the start line runs before ZNet exists (run_6 :2594 said "visited zones only" on a HOST); the source is
                // only known once the world is up, so it is stated here, once per world.
                EasyBakeLog.Info($"{Log} world up: source = {(ZNet.instance.IsServer() ? "this machine's ZDOs (every generated zone)" : ClientAsks ? "visited zones + server" : "visited zones only")}, reach {Reach:0} m");
            }
            FarBuildingDraw.DrawnCensus();   // 1.2.344: 30 s DRAWN line

            long frameStart = Stopwatch.GetTimestamp();
            // 1.2.219 ([visual], Fire: VPO shows distant builds at once): while the player is not spawned yet, is teleporting, or the arrival
            // fill is still running (FillTracker), the far tier gets ArrivalBudgetMs a frame so it draws from the first seconds; then BudgetMs
            // 1.2.346 ([visual], Fire run_8 "far building still not working as intended": 225 targets inside 824 m not built after 20 minutes while the
            // merge worker idled; the 0.5 ms main-thread budget is the player-facing steady state, not a rate the far set can be BUILT at). While the
            // far set is cold (admitted targets not built yet, FarBuildingDraw.ColdTargets) the arrival budget applies, as it does while arriving; back
            // to BudgetMs when every admitted target is built. [Optimize] FarColdBudget (ON). One Info per switch.
            bool cold = ColdBudgetOn && FarBuildingDraw.ColdTargets > 0;
            if (cold != s_coldSaid) { s_coldSaid = cold; EasyBakeLog.Info(cold ? $"{Log} far set cold: {FarBuildingDraw.ColdTargets} admitted target(s) not built yet: {System.Math.Max(BudgetMs, ArrivalBudgetMs):0.##} ms a frame until they are (FarColdBudget)" : $"{Log} far set warm: every admitted target is built, budget back to {BudgetMs:0.##} ms a frame"); }
            // 1.2.355 ([visual]; prof-fire-dock-move-3 FIRE_20261113_044545: FarRunner.Update 4.00 ms SELF while moving vs 0.71 standing, 42 MB/min of GC in
            // FarBuildings.Update): Arriving() stays true for 90 s after EVERY spawn or teleport (FillTracker watches until the instance count settles, which
            // it never does while the player walks), and the cold set stays cold while new targets are admitted, so the 6 ms arrival budget ran the
            // whole moving leg: Plan + the per-frame chunk upload + Prep filled it. Moving hitches are Fire's priority (longer load > post-load hitches
            // applies to arrival, not to walking): while the local player MOVES the far tier gets the base budget only; the arrival / cold budget
            // applies when he stands still (or is still teleporting / unspawned). One Info per flip. Expected: ~4 ms -> under 1 ms a frame while moving.
            bool moving = !(Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting()) && !StreamPacer.LocalPlayerStill;
            if (moving != s_movingCapSaid) { s_movingCapSaid = moving; EasyBakeLog.Debug(moving ? $"{Log} moving: far budget capped at {BudgetMs:0.0} ms a frame until you stand still (arrival {Arriving()}, cold targets {FarBuildingDraw.ColdTargets})" : $"{Log} standing: far budget back to {((Arriving() || cold) ? System.Math.Max(BudgetMs, ArrivalBudgetMs) : BudgetMs):0.0} ms a frame (arrival {Arriving()}, cold targets {FarBuildingDraw.ColdTargets})"); }
            // 1.2.424 ([visual], Fire 11-16: town builds popped out 76-191 s while moving): the edge ring of the loaded area (the zones that unload next, built ahead and
            // hidden, FarKeepHandedOff) gets EdgeBudgetMs even while moving until it is built; everything else keeps the 0.5 ms moving cap. One Info per flip.
            bool edgeCold = FarBuildingDraw.EdgeColdTargets > 0;
            if (edgeCold != s_edgeSaid) { s_edgeSaid = edgeCold; EasyBakeLog.Info(edgeCold ? $"{Log} edge ring cold: {FarBuildingDraw.EdgeColdTargets} zone(s) at the edge of the loaded area have no far copy yet: at least {EdgeBudgetMs:0.#} ms a frame (even moving) until they do" : $"{Log} edge ring ready: every zone at the edge of the loaded area has its far copy"); }
            double ms = ((Arriving() || cold) && !moving) ? System.Math.Max(BudgetMs, ArrivalBudgetMs) : BudgetMs;
            if (edgeCold) ms = System.Math.Max(ms, EdgeBudgetMs);
            long budget = (long)(ms * Stopwatch.Frequency / 1000.0);
            if (moving && Time.unscaledTime >= s_nextBacklogSaid) { s_nextBacklogSaid = Time.unscaledTime + 30f; if (EasyBakeLog.InfoFirstWouldWrite("far-moving-backlog")) EasyBakeLog.InfoFirst("far-moving-backlog", $"{Log} moving: {FarBuildingDraw.ColdBacklog()}"); }   // next EBM ([lead] GO 11-22): Info once, Debug after; not built when dropped   // 1.2.355: the trade, visible every 30 s while moving

            // Host / server: keep the zone digests fresh from the ZDOs it holds. A client scans what it holds too (its visited zones).
            long t0 = Stopwatch.GetTimestamp();
            // 1.2.476 [Optimize] FarDigestBaseBudgetAfterLap: after the ring's first full lap the digest gets half of the BASE budget, not half of the arrival / cold one
            bool baseDigest = DigestBaseAfterLap && FarBuildingDigest.LapDone;
            long digestBudget = baseDigest ? (long)(BudgetMs * Stopwatch.Frequency / 1000.0) : budget;
            using (s_markDigest.Auto()) FarBuildingDigest.Step(frameStart + digestBudget / 2);   // 1.2.477 marker
            FarBuildingDigest.NoteLapState(DigestBaseAfterLap);
            S.ScanTicks += Stopwatch.GetTimestamp() - t0;

            FarBuildingNet.Update();

            if (FarRunner.Client)
            {
                using (s_markSources.Auto()) FarBuildingSources.Step(frameStart + budget);   // 1.2.477 marker
                using (s_markDraw.Auto()) FarBuildingDraw.Update(frameStart + budget);   // 1.2.477 marker
            }
            long d = Stopwatch.GetTimestamp() - frameStart; if (d > S.WorstTicks) S.WorstTicks = d; S.Frames++;
        }

        private static void Late()
        {
            if (!s_running || !FarRunner.Client) return;
            FarBuildingDraw.Late();
            if (TimingLog && Time.unscaledTime >= s_nextTimingLog) { s_nextTimingLog = Time.unscaledTime + 10f; LogTiming(); }
        }

        // 1.2.269 (player DanLo' via Fire, 11-10: with FarLands installed the console filled with "stood down" EVERY frame -> big framerate drops):
        // Update calls Start every frame while the key is on and the tier is not running. Now (1) while stood down Start returns on one bool unless the
        // stand-down inputs changed (a request pushed / withdrawn, the key toggled), (2) the FarLands plugin-list check runs once (the list never changes
        // after load), (3) a reason is logged once, again only when it changes.
        private static bool s_standDownDirty = true;
        private static string s_standDownLogged;
        private static bool Start()
        {
            if (s_standDown != null && !s_standDownDirty) return false;   // stood down, nothing changed: one bool, no scan, no log
            s_standDownDirty = false;
            s_standDown = FindStandDown();
            if (s_standDown != null)
            {
                if (s_running) Stop(s_standDown);
                else if (s_standDown != s_standDownLogged) EasyBakeLog.Info($"{Log} stood down: {s_standDown}.");
                s_standDownLogged = s_standDown;
                return false;
            }
            s_standDownLogged = null;
            if (s_running) return true;
            s_running = true;
            FarBuildingMerge.StartWorker();
            EasyBakeLog.Info($"{Log} started: reach {Reach:0} m, cells from {CellFrom:0} m, cap {MaxVertices} vertices, budget {BudgetMs:0.##} ms/frame, " +
                                $"{(FarRunner.Client ? "client" : "server")}, source = {(ZNet.instance == null ? "stated when the world is up" : ZNet.instance.IsServer() ? "this machine's ZDOs" : ClientAsks ? "visited zones + server" : "visited zones only")}, " +
                                $"snow {(FarSnow.Bound ? "follows the registered season snow" : "off (no season snow registered through EbmApi.RegisterFarSnow)")}.");
            return true;
        }

        private static void Stop(string why)
        {
            s_running = false;
            FarBuildingDraw.Clear();
            FarBuildingMerge.StopWorker();
            EasyBakeLog.Info($"{Log} stopped: {why}.");
        }

        // 1.2.210: another mod that draws far buildings PUSHES its request (EbmApi.RequestFarBuildingsStandDown); owner -> why.
        private static readonly Dictionary<string, string> s_requests = new Dictionary<string, string>(StringComparer.Ordinal);
        internal static void RequestStandDown(string owner, string why)
        {
            if (string.IsNullOrEmpty(owner)) return;
            if (string.IsNullOrEmpty(why)) s_requests.Remove(owner); else s_requests[owner] = why;
            EasyBakeLog.Info($"{Log} stand-down request from {owner}: {(string.IsNullOrEmpty(why) ? "withdrawn" : why)}");
            s_standDownDirty = true;   // 1.2.269: the next Start re-judges (and logs a changed reason once)
            string sd = FindStandDown();
            if (s_running && sd != null) { Stop(sd); s_standDownLogged = sd; }   // Stop logged it; the next Start does not repeat it ([ghost])
        }

        // 1.2.269: FarLands' presence read ONCE from BepInEx's plugin list (it does not change after load); null = not installed
        private static bool s_farLandsChecked; private static string s_farLands;
        private static string FarLandsReason()
        {
            if (s_farLandsChecked) return s_farLands;
            s_farLandsChecked = true;
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                string guid = kv.Key ?? "", name = kv.Value?.Metadata?.Name ?? "";
                if (guid.IndexOf("FarLands", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("FarLands", StringComparison.OrdinalIgnoreCase) >= 0)
                { s_farLands = $"FarLands ({guid}) is installed and draws far buildings itself"; break; }
            }
            return s_farLands;
        }

        // Two far tiers on the same bases would double-draw. Ours steps aside (WHY logged) for a pushed request, or for FarLands, which
        // cannot push (not ours): for it only BepInEx's own plugin list is read (its name, never its types, code or config).
        private static string FindStandDown()
        {
            foreach (var kv in s_requests) return $"{kv.Key} asked: {kv.Value}";
            return FarLandsReason();
        }

        private static void LogTiming()
        {
            double ms = 1000.0 / Stopwatch.Frequency; int f = Math.Max(1, S.Frames);
            EasyBakeLog.Debug($"{Log} last 10 s: main thread scan {S.ScanTicks * ms / f:0.000} / snow {S.SnowTicks * ms / f:0.000} / upload {S.UploadTicks * ms / f:0.000} / hand-off {S.HandoffTicks * ms / f:0.000} ms per frame, " +
                                 $"worst frame {S.WorstTicks * ms:0.00} ms, worker {S.WorkerTicks * ms:0} ms; zones {S.Zones}, targets {S.Targets}, pieces {S.Pieces}, " +
                                 $"vertices {S.Vertices}, draws {S.Draws}, GPU memory {S.GpuBytes / 1048576.0:0.0} MB, evicted {S.Evictions}, hand-offs {S.Handoffs} ({S.HandoffTimeouts} by the {FarBuildingDraw.HoldSecondsForLog:0} s timeout); far meshes read: {S.MeshCpu} CPU, {S.MeshGpu} GPU, {S.MeshCached} from cache, {S.ReadbackFails} failed (session totals)");
            S.ResetWindow();
        }

    }
}


