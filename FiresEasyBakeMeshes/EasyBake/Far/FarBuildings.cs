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
        private const string Section = "Far buildings";

        // â”€â”€ Config (defaults are what readers see until EBM binds) â”€â”€
        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_reach;
        private static ConfigEntry<int> s_maxVertices;
        private static ConfigEntry<float> s_budgetMs;
        private static ConfigEntry<float> s_cellFrom;
        private static ConfigEntry<bool> s_timingLog;
        private static ConfigEntry<bool> s_clientAsks;
        private static ConfigEntry<bool> s_serverSends;

        internal static bool Enabled => s_enabled != null && s_enabled.Value;
        internal static float Reach => s_reach != null ? Mathf.Clamp(s_reach.Value, 300f, 5000f) : 2500f;
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
        internal static float CellFrom => s_cellFrom != null ? Mathf.Clamp(s_cellFrom.Value, 400f, 5000f) : 1000f;
        internal static bool TimingLog => s_timingLog != null && s_timingLog.Value;
        internal static bool ClientAsks => s_clientAsks != null && s_clientAsks.Value;
        internal static bool ServerSends => s_serverSends != null && s_serverSends.Value;

        /// <summary>Called once by EBM (plugin Awake) with its own ConfigFile. Binds [Far buildings]; every key defaults off.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_enabled != null) return;
            s_enabled = config.Bind(Section, "Enabled", true /* 1.2.211: default ON (Fire wants the distant builds) */,
                "Draw other bases beyond the loaded area as merged far meshes (pieces' own materials). On by default (1.2.211, Fire: the distant builds).");
            s_reach = config.Bind(Section, "Reach (m)", 2500f,
                new ConfigDescription("How far far buildings are drawn.", new AcceptableValueRange<float>(300f, 5000f)));
            s_maxVertices = config.Bind(Section, "Max vertices", 2000000,
                new ConfigDescription("Cap on far-building vertices (24 bytes each on the GPU). The farthest are dropped first.",
                    new AcceptableValueRange<int>(100000, 8000000)));
            s_arrivalMs = config.Bind(Section, "Arrival budget (ms)", 6f, new ConfigDescription("1.2.219: main-thread ms a frame for far buildings while you arrive (loading screen, teleport, and until the area has filled in), so distant builds show from the first seconds. 0 = use the normal budget.", new AcceptableValueRange<float>(0f, 12f)));
            s_budgetMs = config.Bind(Section, "Main thread budget (ms)", 0.5f,
                new ConfigDescription("Main-thread time per frame for scanning, snow and uploads. Merging runs on a worker thread.",
                    new AcceptableValueRange<float>(0.1f, 4f)));
            s_cellFrom = config.Bind(Section, "Cell merge from (m)", 1000f,
                new ConfigDescription("Past this distance zones merge into 512 m cells and are simplified to about 1 m.",
                    new AcceptableValueRange<float>(400f, 5000f)));
            s_timingLog = config.Bind(Section, "Timing log", false,
                "Every 10 s, a Debug line with main-thread and worker time, zones, pieces, vertices, draws and GPU memory.");
            s_clientAsks = config.Bind(Section, "Ask the server for far bases", false,
                "On a client: ask the server for the bases you have not visited (needs the server key below on the server).");
            s_serverSends = config.Bind(Section, "Server sends far bases to clients", false,
                "On a server or host: answer clients' far-base requests with compact zone lists (changed zones only, paced).");
            s_enabled.SettingChanged += (_, __) => s_wantRestate = true;
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
            }

            long frameStart = Stopwatch.GetTimestamp();
            // 1.2.219 ([visual], Fire: VPO shows distant builds at once): while the player is not spawned yet, is teleporting, or the arrival
            // fill is still running (FillTracker), the far tier gets ArrivalBudgetMs a frame so it draws from the first seconds; then BudgetMs
            long budget = (long)((Arriving() ? System.Math.Max(BudgetMs, ArrivalBudgetMs) : BudgetMs) * Stopwatch.Frequency / 1000.0);

            // Host / server: keep the zone digests fresh from the ZDOs it holds. A client scans what it holds too (its visited zones).
            long t0 = Stopwatch.GetTimestamp();
            FarBuildingDigest.Step(frameStart + budget / 2);
            S.ScanTicks += Stopwatch.GetTimestamp() - t0;

            FarBuildingNet.Update();

            if (FarRunner.Client)
            {
                FarBuildingSources.Step(frameStart + budget);
                FarBuildingDraw.Update(frameStart + budget);
            }
            long d = Stopwatch.GetTimestamp() - frameStart; if (d > S.WorstTicks) S.WorstTicks = d; S.Frames++;
        }

        private static void Late()
        {
            if (!s_running || !FarRunner.Client) return;
            FarBuildingDraw.Late();
            if (TimingLog && Time.unscaledTime >= s_nextTimingLog) { s_nextTimingLog = Time.unscaledTime + 10f; LogTiming(); }
        }

        private static bool Start()
        {
            s_standDown = FindStandDown();
            if (s_standDown != null)
            {
                if (s_running) Stop(s_standDown);
                else EasyBakeLog.Info($"{Log} stood down: {s_standDown}.");
                return false;
            }
            if (s_running) return true;
            s_running = true;
            FarBuildingMerge.StartWorker();
            EasyBakeLog.Info($"{Log} started: reach {Reach:0} m, cells from {CellFrom:0} m, cap {MaxVertices} vertices, budget {BudgetMs:0.##} ms/frame, " +
                                $"{(FarRunner.Client ? "client" : "server")}, source = {(ZNet.instance != null && ZNet.instance.IsServer() ? "this machine's ZDOs" : ClientAsks ? "visited zones + server" : "visited zones only")}, " +
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
            if (s_running && FindStandDown() != null) Stop(FindStandDown());
        }

        // Two far tiers on the same bases would double-draw. Ours steps aside (WHY logged) for a pushed request, or for FarLands, which
        // cannot push (not ours): for it only BepInEx's own plugin list is read (its name, never its types, code or config).
        private static string FindStandDown()
        {
            foreach (var kv in s_requests) return $"{kv.Key} asked: {kv.Value}";
            foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                string guid = kv.Key ?? "", name = kv.Value?.Metadata?.Name ?? "";
                if (guid.IndexOf("FarLands", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("FarLands", StringComparison.OrdinalIgnoreCase) >= 0)
                    return $"FarLands ({guid}) is installed and draws far buildings itself";
            }
            return null;
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


