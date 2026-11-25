using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.360 ([seasons] 11-13 for [lead], [visual] OK): Unity Profiler sub-markers inside the "EBM baking" phase. The O town circle ((rig path))
    // put 41-112 ms of SELF time on "EBM baking" in 21 stall frames ("frame 1283: 123.9 ms; top self: EBM baking 112.3 ms"), while its existing children (bake:collect /
    // combine / suppress) were small: the cost is in code between them that no marker names. These scoped markers (using ... .Auto(), so a continue / break cannot unbalance
    // them) split that self time by step in the next capture, before any slicing ([visual]: "add sub-markers first, read the next capture"). No profiler attached = no cost.
    internal static class BakeMarkers
    {
        internal static readonly Unity.Profiling.ProfilerMarker CacheCheck = new Unity.Profiling.ProfilerMarker("EBM bake:cache check");
        internal static readonly Unity.Profiling.ProfilerMarker Teardown = new Unity.Profiling.ProfilerMarker("EBM bake:teardown");
        internal static readonly Unity.Profiling.ProfilerMarker HoldReal = new Unity.Profiling.ProfilerMarker("EBM bake:hold real pieces");
        internal static readonly Unity.Profiling.ProfilerMarker ClassifyWait = new Unity.Profiling.ProfilerMarker("EBM bake:classify + prep gate");
        internal static readonly Unity.Profiling.ProfilerMarker Bake = new Unity.Profiling.ProfilerMarker("EBM bake:MeshBaker.Bake");
        internal static readonly Unity.Profiling.ProfilerMarker Verify = new Unity.Profiling.ProfilerMarker("EBM bake:verify");
        internal static readonly Unity.Profiling.ProfilerMarker Announce = new Unity.Profiling.ProfilerMarker("EBM bake:announce + keepalive");
        internal static readonly Unity.Profiling.ProfilerMarker AfterBake = new Unity.Profiling.ProfilerMarker("EBM bake:after bake");
        internal static readonly Unity.Profiling.ProfilerMarker Save = new Unity.Profiling.ProfilerMarker("EBM bake:save zone");
        internal static readonly Unity.Profiling.ProfilerMarker Gates = new Unity.Profiling.ProfilerMarker("EBM bake:settle + variation gates");
    }

    // 1.2.360 ([lead] 11-13: the deep route for town is dead, "DeepAfterGo: the launch flag alone crawls the load"): EBM's Harmony patches run INSIDE vanilla's
    // ZNetScene.Update / ZoneSystem.Update samples, so a non-deep capture charges them to vanilla ("ZoneSystem.Update 44, ZNetScene.Update 42" of O's 275 stall
    // frames). One named marker per EBM patch body on those paths, so the one non-deep town circle after the window splits EBM's share out. Scoped (.Auto()),
    // markers only: no behaviour change, no cost without a profiler. [ghost] (FF) and [fgn] (FGN) mark their own patches the same way.
    // 1.2.363 ([visual] read of the 1.2.362 town circle O_20261114_005404): the open rows, markers only.
    //  - SaveZone ("EBM bake:save zone" 377.9 / 237.9 / 45.5 ms SELF while sliced saves also ran): world uid / try sliced / synchronous Save / delete.
    //  - checks phase ("EBM checks" 611.6 / 402.0 / 175.2 / 108.1 / 48.1 ms): each call, and one marker per zone built from cache.
    //  - gate ("settle + variation gates" 350.5 ms in one frame): the variation walk apart from BakeSettle.
    //  - CacheSummary (~50 ms every 30 s): building the line vs writing it to the log.
    internal static class SaveZoneMarkers
    {
        private static Unity.Profiling.ProfilerMarker M(string n) => new Unity.Profiling.ProfilerMarker("EBM save zone: " + n);
        internal static readonly Unity.Profiling.ProfilerMarker WorldUid = M("world uid");
        internal static readonly Unity.Profiling.ProfilerMarker TrySliced = M("try sliced");
        internal static readonly Unity.Profiling.ProfilerMarker Synchronous = M("synchronous Save (not sliced)");
        internal static readonly Unity.Profiling.ProfilerMarker Delete = M("delete (no content)");
    }
    internal static class CheckMarkers
    {
        private static Unity.Profiling.ProfilerMarker M(string n) => new Unity.Profiling.ProfilerMarker("EBM checks: " + n);
        internal static readonly Unity.Profiling.ProfilerMarker FinishHandBacks = M("finish hand-backs");
        internal static readonly Unity.Profiling.ProfilerMarker TransitionReport = M("transition report");
        internal static readonly Unity.Profiling.ProfilerMarker ConstructCached = M("construct cached zones in range");
        internal static readonly Unity.Profiling.ProfilerMarker ConstructOne = M("construct one zone from cache");
        internal static readonly Unity.Profiling.ProfilerMarker PumpNearOnly = M("preload near-only pump");
        internal static readonly Unity.Profiling.ProfilerMarker ReleaseParked = M("release distant parked stand-ins");
        internal static readonly Unity.Profiling.ProfilerMarker WatchSkipped = M("watch skipped");
        internal static readonly Unity.Profiling.ProfilerMarker ReportSkipping = M("report skipping");
    }
    internal static class GateMarkers
    {
        internal static readonly Unity.Profiling.ProfilerMarker VariationWalk = new Unity.Profiling.ProfilerMarker("EBM gate: variation walk");
    }
    internal static class SummaryMarkers
    {
        internal static readonly Unity.Profiling.ProfilerMarker BuildLine = new Unity.Profiling.ProfilerMarker("EBM cache summary: build line");
        internal static readonly Unity.Profiling.ProfilerMarker LogWrite = new Unity.Profiling.ProfilerMarker("EBM cache summary: log write");
    }

    // 1.2.367 ([seasons], [lead]: tail row "EBM LateUpdate: DrawInstances" 1.24 ms vs 0.86 clean, all self): ZoneTracker.DrawInstances' DwSeg segments as profiler
    // scopes (the "begin" segment declares the frame's flags and stays unscoped; it read 0.008 ms). Markers only.
    internal static class DwMarkers
    {
        internal static readonly Unity.Profiling.ProfilerMarker SettledWalk = new Unity.Profiling.ProfilerMarker("EBM draw: settled walk");
        internal static readonly Unity.Profiling.ProfilerMarker FullWalk = new Unity.Profiling.ProfilerMarker("EBM draw: full zone walk");
        internal static readonly Unity.Profiling.ProfilerMarker EndFrame = new Unity.Profiling.ProfilerMarker("EBM draw: indirect EndFrame (cull + draw)");
        internal static readonly Unity.Profiling.ProfilerMarker Rest = new Unity.Profiling.ProfilerMarker("EBM draw: rest (instanced end frame, dead bakes)");
    }

    // 1.2.367 ([seasons], [lead]: GC row "FiresEasyBakeMeshesPlugin.Update() > GC.Alloc" 2.17 KB/frame, the #1 allocator in clean frames): one scope per tick
    // Plugin.Update calls, so the next capture puts each GC.Alloc under the tick that makes it. Markers only; the fix follows from the named row.
    internal static class UpdateMarkers
    {
        private static Unity.Profiling.ProfilerMarker M(string n) => new Unity.Profiling.ProfilerMarker("EBM Update: " + n);
        internal static readonly Unity.Profiling.ProfilerMarker Heartbeat = M("trace heartbeat");
        internal static readonly Unity.Profiling.ProfilerMarker RemoteItem = M("remote item zsync skip");
        internal static readonly Unity.Profiling.ProfilerMarker ShelterProbe = M("shelter probe");
        internal static readonly Unity.Profiling.ProfilerMarker ShelterRain = M("shelter rain");
        internal static readonly Unity.Profiling.ProfilerMarker WetOverride = M("wet override");
        internal static readonly Unity.Profiling.ProfilerMarker LightBudget = M("light budget");
        internal static readonly Unity.Profiling.ProfilerMarker FillTracker = M("fill tracker");
        internal static readonly Unity.Profiling.ProfilerMarker CoverageReach = M("coverage reach");
        internal static readonly Unity.Profiling.ProfilerMarker Censuses = M("caster / physics / lod / heap census");
        internal static readonly Unity.Profiling.ProfilerMarker WhileOff = M("wet follow while off + hand-back");
        internal static readonly Unity.Profiling.ProfilerMarker PreloadKick = M("cache preload kick");
        internal static readonly Unity.Profiling.ProfilerMarker BakeSummary = M("bake summary");
        internal static readonly Unity.Profiling.ProfilerMarker DamageReplay = M("damage replay");
        internal static readonly Unity.Profiling.ProfilerMarker ZoneTracker = M("zone tracker");
        internal static readonly Unity.Profiling.ProfilerMarker Keepalive = M("zone keepalive");
        internal static readonly Unity.Profiling.ProfilerMarker SectorMirror = M("sector mirror");
        internal static readonly Unity.Profiling.ProfilerMarker ZSyncReport = M("zsync skip report");
    }

    // 1.2.362: the zone walk's steps ("EBM walking zones" 260 ms once in O_20261114_003628). Markers only.
    internal static class WalkMarkers
    {
        private static Unity.Profiling.ProfilerMarker M(string n) => new Unity.Profiling.ProfilerMarker("EBM walk:" + n);
        internal static readonly Unity.Profiling.ProfilerMarker UnloadHide = M("unload: hide bake parent");
        internal static readonly Unity.Profiling.ProfilerMarker UnloadSave = M("unload: save zone");
        internal static readonly Unity.Profiling.ProfilerMarker UnloadGiveBack = M("unload: give back");
        internal static readonly Unity.Profiling.ProfilerMarker UnloadDropPending = M("unload: drop pending silencing");
        internal static readonly Unity.Profiling.ProfilerMarker UnloadForgetClasses = M("unload: forget piece classes");   // 1.2.392
        internal static readonly Unity.Profiling.ProfilerMarker UnloadRelease = M("unload: release skipped + park stand-ins");
        internal static readonly Unity.Profiling.ProfilerMarker UnloadUnannounce = M("unload: unannounce");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadFlush = M("reload: give-back flush");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadShow = M("reload: show bake parent");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadUnpark = M("reload: unpark stand-ins");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadQueue = M("reload: queue stand-ins");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadAnnounce = M("reload: announce");
        internal static readonly Unity.Profiling.ProfilerMarker ReloadReadopt = M("reload: re-adopt survivors");
        internal static readonly Unity.Profiling.ProfilerMarker FarTier = M("far tier update");
        internal static readonly Unity.Profiling.ProfilerMarker GiveBackPump = M("give-back pump");
        internal static readonly Unity.Profiling.ProfilerMarker ProofTick = M("bake proof tick");
    }

    internal static class EbmPatchMarkers
    {
        private static Unity.Profiling.ProfilerMarker M(string n) => new Unity.Profiling.ProfilerMarker("EBM patch:" + n);
        internal static readonly Unity.Profiling.ProfilerMarker PacerPass = M("pacer CreateObjects pass");
        internal static readonly Unity.Profiling.ProfilerMarker PacerExtra = M("pacer extra pass (ZNetScene.Update)");
        internal static readonly Unity.Profiling.ProfilerMarker SkipCreation = M("TrySkipCreation (CreateObject)");
        internal static readonly Unity.Profiling.ProfilerMarker CreatePost = M("CreateObject postfix (zsync skip, stand-ins, registry)");
        internal static readonly Unity.Profiling.ProfilerMarker RemoveObjects = M("RemoveObjects prefix (destroy slicer)");
        internal static readonly Unity.Profiling.ProfilerMarker CreateDestroy = M("CreateDestroyObjects prefix (idle skip)");
        internal static readonly Unity.Profiling.ProfilerMarker DirtyAfterPass = M("CreateObjects postfix (ZdoEventDirty)");
        internal static readonly Unity.Profiling.ProfilerMarker ObjectArea = M("CreateDestroyObjects postfix (object area)");
        internal static readonly Unity.Profiling.ProfilerMarker DistantArm = M("CreateDestroyObjects prefix (distant prefabs)");
        internal static readonly Unity.Profiling.ProfilerMarker HeightmapGenerate = M("Heightmap.Generate (terrain shader)");
        internal static readonly Unity.Profiling.ProfilerMarker LocationAwake = M("Location.Awake (static clusters)");
        internal static readonly Unity.Profiling.ProfilerMarker DungeonSpawn = M("DungeonGenerator Spawn/Generate (interior cull)");
        internal static readonly Unity.Profiling.ProfilerMarker PumpSuppress = M("pump: suppression");
        internal static readonly Unity.Profiling.ProfilerMarker PumpClassify = M("pump: classify");
        internal static readonly Unity.Profiling.ProfilerMarker PumpShell = M("pump: shell pieces + Vedr snow poll");
        internal static readonly Unity.Profiling.ProfilerMarker PumpCensus = M("pump: orphan census + alloc report");
        internal static readonly Unity.Profiling.ProfilerMarker PumpSave = M("pump: sliced cache saves");   // 1.2.361
        // 1.2.361 ([lead]: "the pacer pass spikes", O_20261113_232209 "frame 135: 291.9 ms; top self: EBM patch:pacer CreateObjects pass 259.6 ms"): the pass's parts, so
        // the next capture says whether the spike is the re-seed walk, one first-of-its-kind create (Fits cannot price it), the known creates, or the tail. Markers only.
        internal static readonly Unity.Profiling.ProfilerMarker PacerReseed = M("pacer: re-seed walk (reference zone changed)");
        internal static readonly Unity.Profiling.ProfilerMarker PacerSweep = M("pacer: membership sweep");
        internal static readonly Unity.Profiling.ProfilerMarker PacerReady = M("pacer: release ready zones");
        internal static readonly Unity.Profiling.ProfilerMarker PacerCreateFirst = M("pacer: create, first of its kind (cost unknown)");
        internal static readonly Unity.Profiling.ProfilerMarker PacerCreateKnown = M("pacer: create, cost known");
        internal static readonly Unity.Profiling.ProfilerMarker PacerTail = M("pacer: signals + fill note + reconcile");
        // 1.2.362 ([visual] read of the 1.2.361 town circle O_20261114_003628): "pump: sliced cache saves" 108.0 ms in one stall frame = one step, which one unknown; the
        // pacer tail 75-115 ms in 7 frames ("frame 689: 138.2 ms; top self: EBM patch:pacer: signals + fill note + reconcile 114.8 ms"), which part unknown. Markers only.
        internal static readonly Unity.Profiling.ProfilerMarker SaveStepBatch = M("save step: one batch's mesh arrays");
        internal static readonly Unity.Profiling.ProfilerMarker SaveStepFinish = M("save step: finish (prints, contributors, instance groups, preload copy, queue)");
        internal static readonly Unity.Profiling.ProfilerMarker SaveDiskReread = M("save: E25 re-read of the zone file (released mesh)");
        internal static readonly Unity.Profiling.ProfilerMarker PacerSignals = M("pacer tail: signals");
        internal static readonly Unity.Profiling.ProfilerMarker PacerFillNote = M("pacer tail: fill note");
        internal static readonly Unity.Profiling.ProfilerMarker PacerReconcile = M("pacer tail: reconcile");
        // 1.2.362 ([lead]: "frame 664: 316.5 ms; top self: EBM walking zones 260.0 ms" in O_20261114_003628): the zone walk's steps (unload / reload branches,
        // far tier, give-back pump, proof tick); what stays in "EBM walking zones" self after this is the loop and its loaded / keepalive checks.
        // (class WalkMarkers, above EbmPatchMarkers)
        // 1.2.362 ([lead]: the LateUpdate self 117 ms once, frame 1181, unattributed): one scope per LateUpdate call, in the [SelfCost] LateUpdate slot order
        // (Plugin.s_luNames 0-18), then 19 ArrivalTimeline, 20 BlackFrameWatch, 21 the 30 s report.
        internal static readonly Unity.Profiling.ProfilerMarker[] Lu = BuildLu();
        private static Unity.Profiling.ProfilerMarker[] BuildLu()
        {
            string[] n = { "LightCensus", "SkipCensus", "WaterHideBuried", "WaterTightBounds", "WaterOccl", "WaterSplit", "GpuFires", "StreamPacerWatch",
                "CacheSummary", "StandItemCull", "SignTextCull", "TerrainShader", "WaterShader", "ShaderOwnership", "FarTick", "StaticClusters",
                "CoverageRegistry", "CoveredFlush", "DrawInstances", "ArrivalTimeline", "BlackFrameWatch", "SelfCost report" };
            var m = new Unity.Profiling.ProfilerMarker[n.Length];
            for (int i = 0; i < n.Length; i++) m[i] = new Unity.Profiling.ProfilerMarker("EBM LateUpdate: " + n[i]);
            return m;
        }
    }

    // [Optimize] BakeFrameSplit ([saddles] 11-09, Tools\READ_EBM_bake_20261109.md): the circle log's 263 and 95 ms "baking" frames held little or no MeshBaker.Bake (worst zone 9.2 ms),
    // and some zone tracker frames were 59-115 ms with every phase near 0. This names where one ZoneTracker.Update spent its time, step by step, and which zone took the largest
    // single piece. Enter() charges the time since the previous Enter to the step that was running, so a `continue` in the walk needs no closing call.
    // Only while the key is on AND Debug logging is on; otherwise each call is one bool check. No effect on the game. One line every 30 s.
    internal static class BakeFrameSplit
    {
        internal enum Step { Suppress, Classify, Census, ZoneWalk, Other, Walk, CacheCheck, Trim, Teardown, Bake, AfterBake, SaveRead, SaveSerialise, SaveWrite, SaveRefresh, Tail, Announce, HiddenRelease, Count }

        private static readonly string[] Names =
        {
            "silence pump", "classify pump", "orphan census", "zone load/unload walk", "other (build hold, checks, stand-ins, convert)", "bake walk gates", "cache check", "trim",
            "teardown", "MeshBaker.Bake", "after bake", "save read meshes", "save serialise", "save write", "save refresh preload", "stand-in flush + empty zones", "re-announce", "hidden release",
        };

        private const float ReportSeconds = 30f;
        private const int Steps = (int)Step.Count;
        private static readonly long[] s_frame = new long[Steps], s_sum = new long[Steps], s_worst = new long[Steps];
        private static readonly int[] s_entries = new int[Steps], s_worstEntries = new int[Steps], s_frameEntries = new int[Steps];
        private static bool s_inFrame, s_hasZone, s_bigHasZone, s_worstHasZone;
        private static Step s_current, s_bigStep, s_worstStep;
        private static Vector2s s_zone, s_bigZone, s_worstZone;
        private static long s_last, s_bigTicks, s_worstTotal, s_worstBig;
        private static int s_frames, s_gcAtStart, s_worstGc, s_worstFrame, s_passStops;
        private static float s_worstTime, s_nextReport;
        private static bool s_saidStop;

        private static bool KeyOn => FiresEasyBakeMeshesPlugin.BakeFrameSplit != null && FiresEasyBakeMeshesPlugin.BakeFrameSplit.Value;

        internal static void BeginFrame()
        {
            s_inFrame = KeyOn && EasyBakeLog.DebugOn();
            if (!s_inFrame) return;
            for (int i = 0; i < Steps; i++) { s_frame[i] = 0L; s_frameEntries[i] = 0; }
            s_bigTicks = 0L; s_bigHasZone = false; s_hasZone = false;
            s_current = Step.Suppress;
            s_gcAtStart = System.GC.CollectionCount(0);
            s_last = Stopwatch.GetTimestamp();
        }

        internal static void Enter(Step step)
        {
            if (!s_inFrame) return;
            Charge();
            s_current = step;
            s_frameEntries[(int)step]++;
            if (step == Step.Other || step == Step.Tail || step == Step.Announce || step == Step.HiddenRelease) s_hasZone = false;
        }

        internal static void Enter(Step step, Vector2s zone)
        {
            if (!s_inFrame) return;
            Charge();
            s_current = step;
            s_frameEntries[(int)step]++;
            s_zone = zone;
            s_hasZone = true;
        }

        private static void Charge()
        {
            long now = Stopwatch.GetTimestamp(), spent = now - s_last;
            s_last = now;
            s_frame[(int)s_current] += spent;
            if (spent > s_bigTicks) { s_bigTicks = spent; s_bigStep = s_current; s_bigHasZone = s_hasZone; s_bigZone = s_zone; }
        }

        // BakePassBudgetMs ended this frame's walk early. Counted with or without Debug; said once at Info.
        internal static void NotePassStop(float budgetMs, Vector2s nextZone)
        {
            s_passStops++;
            if (s_saidStop) return;
            s_saidStop = true;
            EasyBakeLog.Info($"[Bake] BakePassBudgetMs on: the bake walk spent its {budgetMs:0.##} ms and left the remaining zones for the next frame (first time at zone ({nextZone.x},{nextZone.y})); their originals keep drawing until then");
        }

        internal static void EndFrame()
        {
            if (!s_inFrame) return;
            Charge();
            s_inFrame = false;
            long total = 0L;
            for (int i = 0; i < Steps; i++) { total += s_frame[i]; s_sum[i] += s_frame[i]; s_entries[i] += s_frameEntries[i]; }
            s_frames++;
            if (total > s_worstTotal)
            {
                s_worstTotal = total;
                System.Array.Copy(s_frame, s_worst, Steps);
                System.Array.Copy(s_frameEntries, s_worstEntries, Steps);
                s_worstBig = s_bigTicks; s_worstStep = s_bigStep; s_worstHasZone = s_bigHasZone; s_worstZone = s_bigZone;
                s_worstFrame = Time.frameCount; s_worstTime = Time.unscaledTime;
                s_worstGc = System.GC.CollectionCount(0) - s_gcAtStart;
            }
            if (Time.unscaledTime >= s_nextReport) Report();
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private static void Report()
        {
            s_nextReport = Time.unscaledTime + ReportSeconds;
            if (s_frames == 0) return;
            var sb = new StringBuilder(640);
            string zone = s_worstHasZone ? $" on zone ({s_worstZone.x},{s_worstZone.y})" : "";
            sb.Append($"[BakeFrameSplit] last {ReportSeconds:0} s, {s_frames} frames. Worst zone tracker frame #{s_worstFrame} at t={s_worstTime:0.0}: {Ms(s_worstTotal):0.0} ms, largest single step {Names[(int)s_worstStep]} {Ms(s_worstBig):0.0} ms{zone}, GCs in it {s_worstGc}; its steps:");
            for (int i = 0; i < Steps; i++)
                if (Ms(s_worst[i]) >= 0.05) sb.Append($" {Names[i]} {Ms(s_worst[i]):0.0} (x{s_worstEntries[i]});");
            sb.Append(" Whole window ms:");
            for (int i = 0; i < Steps; i++)
                if (Ms(s_sum[i]) >= 0.05) sb.Append($" {Names[i]} {Ms(s_sum[i]):0.0} (x{s_entries[i]});");
            sb.Append($" Walks stopped by BakePassBudgetMs: {s_passStops}.");
            string asyncSaves = MeshCacheStore.AsyncSaveSummary();
            if (asyncSaves != null) sb.Append(' ').Append(asyncSaves).Append('.');
            EasyBakeLog.Debug(sb.ToString());
            for (int i = 0; i < Steps; i++) { s_sum[i] = 0L; s_worst[i] = 0L; s_entries[i] = 0; s_worstEntries[i] = 0; }
            s_frames = 0; s_worstTotal = 0L; s_worstBig = 0L; s_worstGc = 0; s_passStops = 0;
        }
    }
}
