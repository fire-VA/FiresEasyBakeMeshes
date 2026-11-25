using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.446 ([visual], Fire 11-18: "EBM should be self-contained ... works similar to the farlands mod"): EBM's own far vegetation, no FAT.
    /// The wiring of FAT's FarBridge (FAT FarBridge.cs:369-547) onto EBM's own placement: [worldgen]'s port of FAT's exact replay
    /// (FarVegKinds / FarVegRngSelfTest / FarVegReplay) fills 512 m cells (8 x 8 vanilla zones) on a worker thread, nearest first; the seen-trees
    /// rule (FarVegSeen) replaces the replay with the real trees wherever the player has been; ImpostorEngine draws (owner "EBM", direct calls,
    /// no EbmApi round trip). Key [03 - Far Rendering] Far Trees Own Placement (1.2.458; under the Far Rendering master), default OFF, live: on fills, off clears every cell.
    ///
    /// Cells: key = ((long)cx &lt;&lt; 32) | (uint)cz over ZONES (cell cx holds zones cx*8 .. cx*8+7), so a zone sits in exactly one cell (FAT's cells
    /// were in metres and a zone could straddle two). Kept to ImpostorEngine.Distance, cleared beyond it + 1024 m (FAT's hysteresis).
    /// Main-thread budget: Scan / Observe once a second (cell list sort + at most 25 sector reads); at most 2 SetCell a frame (the pool slices
    /// the upload itself under Upload budget ms); when the RNG self-test fails, the replay runs here instead under MainReplayBudgetMs (1 ms).
    /// Stands down with one Info line when FAT's own bridge feeds the engine (a kind registered by owner "FAT").
    /// TODO (lead 11-18 minimal first build): the host pass over ZoneSystem.m_generatedZones (per-cell observed mask), rocks (FarRocks already
    /// covers ZDO zones; the double-draw rule). (1.2.458: the per-world disk cache is in, per zone; see ZoneCacheKey.)
    /// </summary>
    internal static class FarVegRunner
    {
        private const string Log = "[FarVeg]";
        private const string Owner = "EBM";
        private const int ZonesPerCell = 8;
        private const float CellSize = ZonesPerCell * FarVegSeen.ZoneSize, ClearMargin = 1024f, Resend = 2f;
        private const double MainReplayBudgetMs = 1.0;

        private static ConfigEntry<bool> s_exact, s_matchTest;   // 1.2.484: s_own ("Far Trees Own Placement") retired: "Far Trees" itself switches EBM's own placement
        private static bool s_bound;   // 1.2.484: Initialize ran
        private static bool s_started, s_fatSaid, s_workerOk, s_selfTestRan, s_selfTestPassed;
        private static float s_nextScan, s_nextLog, s_nextTry, s_nextObserve, s_nextFatCheck;
        private static int[] s_engineKind = new int[0];   // FarVegKinds index -> ImpostorEngine kind id (-1 refused)
        private static int s_poolGeneration;
        private static readonly HashSet<long> s_live = new HashSet<long>(), s_queued = new HashSet<long>();
        private static readonly Dictionary<long, float> s_cellSentAt = new Dictionary<long, float>();
        private static readonly Dictionary<long, int> s_liveCount = new Dictionary<long, int>();
        internal static long LiveInstances { get { long n = 0; foreach (var v in s_liveCount.Values) n += v; return n; } }   // 1.2.484: FarZoneSet census
        internal static bool Started => s_started;   // 1.2.484: FarZoneSet census
        private static readonly HashSet<long> s_dirtyCells = new HashSet<long>();
        private static readonly ConcurrentQueue<(long key, int epoch)> s_todo = new ConcurrentQueue<(long, int)>();   // 1.2.458: the epoch travels with the key (a worker dequeuing just before Stop() must not tag the cell with the NEW epoch)
        private static readonly ConcurrentQueue<Built> s_done = new ConcurrentQueue<Built>();
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);
        private static Thread[] s_workers;   // 1.2.458: 1-3 threads (WorkerCount), each with its own CellBuild
        private static volatile int s_epoch;   // +1 on every Stop: a build from an older epoch is dropped
        // 1.2.458: worker counters are Interlocked (several workers): build time in microseconds, cells built.
        private static long s_workerUs; private static int s_workerCells, s_cellsSent;
        private static long s_instancesSent;

        // 1.2.458 disk cache of the replay output per ZONE (FarCache World scope; the world folder carries seed / world-gen identity, FarCacheWorld.cs:70-72).
        // Read before PlaceZone on the worker path only (FarCache.TryRead is "Any thread", FarCache.cs:113-130); the seen-zone override is applied AFTER,
        // exactly as before. Main-thread fallback path: no cache (unchanged).
        private const string CacheBucket = "farveg";
        private const int CacheFormat = 1;               // bump when the record layout below changes
        private const int CacheMagic = 0x315A5646;       // "FVZ1"
        private const int RecordBytes = 4 + 7 * 4;       // kind, x, y, z, yaw, scale, tiltX, tiltZ
        private static long s_cacheHits, s_cacheMisses;              // last 30 s (Interlocked)
        private static long s_fillHits, s_fillMisses;                // since Start (Interlocked)
        private static int s_cacheChecked;                           // Debug stale-cache check: 0 = not yet this session
        private static bool s_fillPending; private static int s_fillCells;   // main thread
        private static readonly System.Diagnostics.Stopwatch s_fillSw = new System.Diagnostics.Stopwatch();
        private static int s_workerCount = 1;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }

        private static int WorkerCount() => Mathf.Clamp(Environment.ProcessorCount / 2 - 1, 1, 3);

        private sealed class Built { public long Key; public int Epoch; public Bounds Bounds; public ImpostorInstance[] Data; }
        // 1.2.458: the last built data of every live cell (main thread), so an engine reset re-sends instead of replaying (32 B per tree: ~2 MB at 70k trees).
        private static readonly Dictionary<long, Built> s_cellData = new Dictionary<long, Built>();

        // 1.2.484 ([lead] GO 11-21, repro shot 126: "Far Trees = true" + "Own Placement = false" drew 0 trees once FAT stopped feeding them, FAT 0.2.1482):
        // ONE method: "Far Trees" on (ImpostorEngine.Enabled = its key AND the Far Rendering master) = EBM places its own far trees whenever no other owner feeds
        // the impostor engine (FatFeeds stands it down, as before). "Far Trees Own Placement" is no longer bound: its saved cfg line stays, unread.
        internal static bool On => s_bound && ImpostorEngine.Enabled;

        /// <summary>Plugin Awake, after ImpostorEngine.Initialize (same section). Main thread.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_bound) return;
            s_bound = true;
            // 1.2.457 (lead, 456 legs proven: Plains show/clear/return, crypt zone 81/81): default ON. BepInEx only uses the default for a key missing
            // from the cfg, so a saved value (on or off) is never rewritten; only fresh configs get true.
            // 1.2.458 (Fire 11-18: "none of the far lands esc code in ebm will default to on"): default OFF again, and moved to [03 - Far Rendering]
            // "Far Trees Own Placement" (was [05 - Far Trees] Own Placement; EbmConfigMove carries a saved value), under the master switch.
            // 1.2.484: no own key any more (see On); Update stops the trees the frame "Far Trees" or the master goes off.
            // 1.2.456 (lead, 455 leg: "D + IsBlocked EMULATED (shippable) 62/62 extra 0" on a fresh dedi zone = proven gate, so it ships ON).
            s_exact = config.Bind("05 - Far Trees", "Exact Vanilla Placement", true,
                "Copies the game's own tree placement exactly, including trees the game leaves out because a rock or an earlier plant already stands " +
                "there (blocking by rocks), so each far tree stands where the real tree will. Off = a little faster to build, but some far trees stand " +
                "where the game puts none. Background thread only (no frame cost). Needs [03 - Far Rendering] Far Trees. Live (far trees rebuild).");
            FarVegReplay.EmulateBlocking = s_exact.Value;
            s_exact.SettingChanged += (_, __) =>
            {
                FarVegReplay.EmulateBlocking = s_exact.Value;
                EasyBakeLog.Info($"{Log} Exact Vanilla Placement {(s_exact.Value ? "on" : "off")}: far trees rebuild.");
                if (s_started) { Stop("Exact Vanilla Placement changed"); s_nextTry = 0f; }
            };
            // 1.2.466 (lead 11-19 G9-clean-farcensus: "[Far] main thread, last 30 s: FarVeg ... worst 123284 us" once per 30 s window, every window after the fill):
            // the proof hook below (MatchZone + MatchZoneDetail + MatchZoneTrace = ~20 whole-zone replays with raycasts, on the main thread) ran whenever Debug
            // was kept, so every Debug perf leg took a 120-240 ms hitch per window. It now needs its own key; proof legs turn it on, perf legs leave it off.
            s_matchTest = config.Bind("90 - Debugging", "Far Tree Match Test", false,
                "Debug only: every 30 s, compares EBM's far tree placement with the real trees of the zone you stand in (match test / detail / trace lines). " +
                "Heavy: about 120 ms on the main thread each time, so leave it off except for placement proof runs. Needs Debug logging. Live.");
            EbmFarWorldEvents.WorldClosed += () => { Stop("world closed"); FarVegSeen.Clear(); s_mainOnlyWhy = null; s_mainOnlySaid = false; };   // 1.2.486: the main-only stand-down is per session
            FarRunner.Register("FarVeg", Update, null);
        }

        // 1.2.458: the toggle's old SettingChanged body, shared with the master switch (FarRendering.Changed)
        // 1.2.484: OwnChanged retired with its key; Update handles "Far Trees" / master off (Stop) and on (Start on its 10 s retry, from 0 after a Stop).

        private static void Update()
        {
            if (!On) { if (s_started) { Stop("Far Trees or Far Rendering turned off"); s_nextTry = 0f; } return; }   // 1.2.484: was the Own Placement key's SettingChanged
            if (s_mainOnlyWhy != null)   // 1.2.486 ([2ic]): a worker hit a main-thread-only Unity call: off for this session (reset at world close)
            {
                if (!s_mainOnlySaid) { s_mainOnlySaid = true; EasyBakeLog.Info($"{Log} far trees stand down: a world-gen patch on this world uses main-thread-only Unity calls ({s_mainOnlyWhy}). Far trees stay off for this session."); }
                if (s_started) Stop("a world-gen patch needs the main thread");
                return;
            }
            if (ZoneSystem.instance == null || ZNet.instance == null || Camera.main == null) return;
            float now = Time.realtimeSinceStartup;
            if (!s_started)
            {
                if (now < s_nextTry) return;
                s_nextTry = now + 10f;
                if (!Start()) return;
            }
            if (now >= s_nextFatCheck)
            {
                s_nextFatCheck = now + 10f;
                if (FatFeeds()) { Stop("FAT's Far Forest feeds the impostor engine"); return; }
                // 1.2.465 ([worldgen] FAT 0.2.1489): the density the counts came from changed (a cfg edit, or the server's synced value arriving after Start).
                if (FarVegSource.CountsChanged()) { Stop("FAT's vegetation density changed (FarVegetationRevision): entry counts re-read"); return; }
            }
            if (ImpostorPool.Generation != s_poolGeneration)   // the engine dropped every cell: send them again
            {
                // 1.2.458 ([visual], lead: 457 default leg cells fell back to 0 twice at load): re-send the cells already BUILT from memory
                // (SetCell again, no replay); only cells never built go back to the worker. Logged so a leg shows when and why it happened.
                s_poolGeneration = ImpostorPool.Generation;
                int resent = 0, requeued = 0;
                foreach (var k in s_live)
                {
                    if (s_cellData.TryGetValue(k, out var cd)) { if (cd.Data.Length > 0) ImpostorEngine.SetCell(Owner, k, cd.Bounds, cd.Data, cd.Data.Length); resent++; }
                    else if (s_queued.Add(k)) { s_todo.Enqueue((k, s_epoch)); requeued++; }
                }
                if (requeued == 0) { /* every live cell re-sent: s_live / counts stay */ } else { foreach (var k in s_live) if (!s_cellData.ContainsKey(k)) s_liveCount.Remove(k); s_live.RemoveWhere(k => !s_cellData.ContainsKey(k)); }
                EasyBakeLog.Info($"{Log} the impostor engine dropped its cells (pool generation {s_poolGeneration}): {resent} cell(s) re-sent from memory, {requeued} re-queued");
                s_wake.Set();
            }
            if (!s_workerOk) MainReplay();
            Drain();
            // 1.2.458 A/B timer: the first time every queued cell has been sent after a Start (cold vs warm cache load).
            if (s_fillPending && s_fillCells > 0 && s_queued.Count == 0)
            {
                s_fillPending = false;
                EasyBakeLog.Info($"{Log} fill: first {s_fillCells} cells in {s_fillSw.Elapsed.TotalSeconds:0.0} s ({(s_workerOk ? s_workerCount : 0)} worker(s)), " +
                                 $"cache hits {Interlocked.Read(ref s_fillHits)} / misses {Interlocked.Read(ref s_fillMisses)}{(CacheOn ? "" : " (cache off)")}");
            }
            if (now >= s_nextScan) { s_nextScan = now + 1f; Scan(Camera.main.transform.position); }
            if (now >= s_nextObserve) { s_nextObserve = now + 1f; FarVegSeen.Observe(); RequeueDirty(now); }
            if (now >= s_nextLog)
            {
                s_nextLog = now + 30f;
                long inst = 0; foreach (var v in s_liveCount.Values) inst += v;
                int cellsBuilt = Volatile.Read(ref s_workerCells);
                double msPerCell = cellsBuilt > 0 ? Interlocked.Read(ref s_workerUs) / 1000.0 / cellsBuilt : 0;
                long hits = Interlocked.Exchange(ref s_cacheHits, 0), misses = Interlocked.Exchange(ref s_cacheMisses, 0);
                EasyBakeLog.InfoFirst("farveg-30s", $"{Log} far vegetation: {s_live.Count} cells / {inst} instances ({FarVegKinds.Count} kinds), self-test {(s_selfTestPassed ? "PASSED" : "FAILED")}, " +
                                 $"seen zones {FarVegSeen.Count}, generated-zone pass 0/0 (not built yet); exact vanilla placement {(FarVegReplay.EmulateBlocking ? "on" : "off")}, " +
                                 $"build {msPerCell:0.0} ms per 512 m cell on the {(s_workerOk ? s_workerCount + " worker thread(s) (no frame cost)" : "MAIN thread")}; " +
                                 $"zone cache {(CacheOn ? $"hits {hits} / misses {misses} (last 30 s)" : "off")}");   // 1.2.456 (lead); 1.2.458 workers + cache
                EasyBakeLog.Debug($"{Log} {s_cellsSent} cell(s) sent, {s_instancesSent} instance(s) sent, {s_queued.Count} queued; replay {(s_workerOk ? "worker" : "main thread")} " +
                                  $"{msPerCell:0.0} ms/cell avg; seen: {FarVegSeen.ZonesSeen} committed, {FarVegSeen.ZoneReads} reads, " +
                                  $"{FarVegSeen.ZoneChanges} changed, {FarVegSeen.StableCommits} by two matching reads, deferred {FarVegSeen.DeferUncreated} uncreated / {FarVegSeen.DeferArrival} arrival, " +
                                  $"{FarVegSeen.ProvenZones} with removal proof");
                // Proof hook ([worldgen] MatchZone, main thread): the player's own loaded zone, replay vs the real ZDOs. Info so Fire's proof leg has it.
                // 1.2.448: Debug only. MatchZone replays a whole zone ON THE MAIN THREAD (~8 ms at 447's 502 ms / 64-zone cell): that was the recurring
                // "FarVeg worst 3-9 ms" in every 30 s window of the 447 leg. Proof legs run with [90 - Debugging] PerfCensus on.
                if (s_matchTest != null && s_matchTest.Value && EasyBakeLog.DebugOn() && ZoneSystem.instance != null && Player.m_localPlayer != null)   // 1.2.466: own key (see Init)
                {
                    var pz = ZoneSystem.GetZone(Player.m_localPlayer.transform.position);
                    if (ZoneSystem.instance.IsZoneLoaded(pz))
                    {
                        try { FarVegReplay.MatchZone(pz, out int real, out int matched, out int extra);
                              EasyBakeLog.Info($"{Log} match test zone ({pz.x},{pz.y}): real {real}, matched {matched} ({(real > 0 ? 100f * matched / real : 0f):0.0}%), extra {extra}, awake mode {FarVegReplay.AwakeMode}");
                              EasyBakeLog.Debug($"{Log} match detail " + FarVegReplay.MatchZoneDetail(pz));
                              EasyBakeLog.Debug($"{Log} match trace " + FarVegReplay.MatchZoneTrace(pz, "Pinetree_01", 60)); }   // 1.2.451 ([worldgen]): variants A / B (clear areas) / C (+IsBlocked from real colliders); heavy, Debug only   // 1.2.449 ([worldgen]): modes 0/1/2 + per-entry first miss
                        catch (Exception e) { EasyBakeLog.Debug($"{Log} match test threw: {e.GetType().Name}: {e.Message}"); }
                    }
                }
                if (EasyBakeLog.DebugOn()) { try { var census = FarVegReplay.CensusLine(); if (!string.IsNullOrEmpty(census)) EasyBakeLog.Debug($"{Log} draw census: {census}"); } catch { } }
            }
        }

        // FAT presence = a kind registered by FAT's bridge (FAT FarBridge.cs:25 Owner "FAT", through EbmApi.FarForestRegisterKind). Main thread.
        private static bool s_patchSaid;
        private static string s_srcSaid;   // 1.2.459: last world-source line logged (said once per change)
        private static readonly string[] s_replayCalls = { "GetBiomeSector", "GetBiome", "GetBiomeHeight", "GetForestFactor" };
        // 1.2.458: any Harmony patch (prefix / postfix / transpiler / finalizer) by another owner on a WorldGenerator method the replay calls; null = none.
        private static string ForeignWorldGenPatches()
        {
            var hits = new List<string>();
            foreach (var m in typeof(WorldGenerator).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(s_replayCalls, m.Name) < 0) continue;
                var info = HarmonyLib.Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var owner in info.Owners)
                    if (!IsOurs(owner)) hits.Add($"WorldGenerator.{m.Name} is patched by {owner ?? "?"}");
            }
            return hits.Count == 0 ? null : string.Join("; ", hits.ToArray());
        }

        // 1.2.458 (Fire via lead: "Ebm and fat are both ours and can be built to work together"): only patches by mods that are NOT ours stand far
        // vegetation down. Ours = Harmony ids starting "com.Fire." plus FAT's two extra ids ([worldgen]: VerdantsAscent.TileHammerCompat.Ruler,
        // VerdantsAscent.WecTerrainBridge). UNCONFIRMED until [worldgen]'s audit: that FAT's patches on these methods are thread-safe (vanilla's own
        // HeightmapBuilder already calls GetBiomeSector / GetBiome / GetBiomeHeight off the main thread; GetForestFactor is main-thread-only in vanilla).
        private static bool IsOurs(string owner) =>
            owner != null && (owner.StartsWith("com.Fire.", StringComparison.Ordinal)
                              || owner == "VerdantsAscent.TileHammerCompat.Ruler" || owner == "VerdantsAscent.WecTerrainBridge");

        private static bool FatFeeds()
        {
            var all = ImpostorKinds.All;
            for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].Owner == "FAT") return true;
            return false;
        }

        private static bool Start()
        {
            if (!FarRunner.Client) return false;   // a dedicated server draws nothing
            if (FatFeeds())
            {
                if (!s_fatSaid) { s_fatSaid = true; EasyBakeLog.Info($"{Log} far trees: FAT's Far Forest already feeds far trees to EBM (owner FAT seen)."); }
                return false;
            }
            if (WorldGenerator.instance == null || ZoneSystem.instance == null) return false;
            // 1.2.458 (lead; memory rule "never call a patched vanilla method off the main thread", the Vedr GetBiomeColor counter case): the replay calls
            // WorldGenerator.GetBiomeSector / GetBiome / GetBiomeHeight / GetForestFactor on worker threads (FarVegReplay.cs:282-312, 511, 558, 809). If any other
            // mod has a Harmony patch on one of them (FAT patches the world generator on its worlds), stand down with one Info line naming it.
            // 1.2.486: that gate now runs AFTER FarVegSource.Pick and only stands down on the VANILLA path (see below).
            var swStart = System.Diagnostics.Stopwatch.StartNew(); double tInit, tKinds, tTest;   // 1.2.448 (lead: 447 "FarVeg worst 51190 us"): split Start's main-thread cost
            // 1.2.459: where the world is read from (FatApi v3 on every world when FAT is installed; vanilla reverse-patch snapshots when FAT is absent).
            // The delegates must be set BEFORE FarVegReplay.Init ([worldgen]).
            string srcWhy = FarVegSource.Pick();
            if (srcWhy != null)
            {
                if (s_srcSaid != srcWhy) { s_srcSaid = srcWhy; EasyBakeLog.Info($"{Log} Far Trees on but no source: EBM's own placement stands down: {srcWhy}."); }
                return false;
            }
            // 1.2.486 ([lead] / Fire 11-21: "make ebm use expand worlds data properly and still run on the background thread"; Expand World Data 1.79 patches
            // WorldGenerator.GetBiomeHeight): the replay reads the world ONLY through FarVegReplay's Src* delegates (FarVegReplay.cs:308-611). On the FAT path those are
            // FatApi's far world-gen calls (FarVegSource.cs:47-69), the same source EBM's far TERRAIN uses, which already honours EWD on Fire's world: FAT's
            // FarSampleZone runs the game's real HeightmapBuilder.Build, so EWD's height patch applies, exactly as on vanilla's own heightmap builder thread
            // (vanilla calls GetBiomeHeight off the main thread itself). So on the FAT path a foreign world-generator patch is NOT a reason to stand down; it is
            // named once. The gate stays for the VANILLA path only: its reverse-patch snapshots run the ORIGINAL methods (other mods' patches skipped), so
            // trees would stand where the patched world has none.
            string patched = ForeignWorldGenPatches();
            if (patched != null)
            {
                if (!FarVegReplay.FatPath)
                {
                    if (!s_patchSaid) { s_patchSaid = true; EasyBakeLog.Info($"{Log} Far Trees on but no source: EBM's own placement stands down: {patched}. Without FAT the replay reads the ORIGINAL world-generator methods, so it would place trees where that mod's world has none."); }
                    return false;
                }
                if (!s_patchSaid) { s_patchSaid = true; EasyBakeLog.Info($"{Log} {patched}: far trees read the world through FAT's far world-gen API (the same source as EBM's far terrain), which includes that mod's changes; replay stays on the worker thread."); }
            }
            if (s_srcSaid != FarVegSource.PathName) { s_srcSaid = FarVegSource.PathName; EasyBakeLog.Info($"{Log} world read from: {FarVegSource.PathName}"); }
            if (!FarVegReplay.Init()) return false;
            tInit = swStart.Elapsed.TotalMilliseconds;
            // 1.2.449 (lead): the shipped Awake mode comes from THIS session. Only the peer that generates a zone runs vanilla's placement; on trees the
            // one Awake RNG draw is LodFadeInOut.Awake, made only with a camera > 20 m away ([worldgen], LodFadeInOut.cs:15-26). A client of a dedicated
            // server sees zones the server generated (no camera: 0 draws); a single player / host generates them itself (camera: 1 draw). Mode 2 (probe)
            // is never shipped; it only appears in the Debug match detail line.
            // 1.2.454 ([worldgen] offline proof, 453 leg save _main.8, zone (-101,-21): every real Pinetree_01 / FirTree_small / FirTree located EXACTLY in
            // its entry's stream; a placed tree burns 7 + groundTilt 1 + ONE Awake word even on the dedicated server): the session rule above was wrong.
            // Mode 1 always (LodFade + fixed Awake words), on every peer.
            FarVegReplay.AwakeMode = 1;
            EasyBakeLog.Info($"{Log} awake mode {FarVegReplay.AwakeMode} = {FarVegReplay.AwakeModeName(FarVegReplay.AwakeMode)} (always; proven on a dedicated-server-generated zone, 1.2.454)");   // [worldgen]: main thread after ZoneSystem.Start (seed, zone prefab width/scale); false = not ready, retried in 10 s; every Start (world load) re-inits
            if (!FarVegKinds.Build() || FarVegKinds.Count == 0) return false;   // retried in 10 s
            tKinds = swStart.Elapsed.TotalMilliseconds;
            if (!s_selfTestRan) { s_selfTestRan = true; s_selfTestPassed = FarVegRngSelfTest.Run(); }
            tTest = swStart.Elapsed.TotalMilliseconds;
            if (EasyBakeLog.DebugOn() && ZNet.instance != null && ZNet.instance.IsServer()) { try { FarVegReplay.CensusStart(); } catch (Exception e) { EasyBakeLog.Debug($"{Log} census start threw: {e.Message}"); } }   // [worldgen]: Harmony on Object.Instantiate + PlaceVegetation prefix, Debug + host only
            s_workerOk = s_selfTestPassed && FarVegReplay.WorkerOk;
            int proofs = FarVegSeen.Start();
            int n = FarVegKinds.Count, registered = 0;
            s_engineKind = new int[n];
            var names = new List<string>(n);
            for (int k = 0; k < n; k++)
            {
                s_engineKind[k] = -1;
                var prefab = FarVegKinds.Prefab(k);
                if (prefab == null) continue;
                var o = KindOptions.Default;   // as EbmApi.FarForestRegisterKind / FAT's ImpostorSink.RegisterKind
                o.MidLodSource = MidLodSource.AuthoredFarLod;
                o.Distant = FarVegKinds.Distant(k);
                o.ReachScale = 1f;
                s_engineKind[k] = ImpostorEngine.RegisterKind(Owner, prefab, o);
                if (s_engineKind[k] >= 0) registered++;
                names.Add(FarVegKinds.Name(k) + (o.Distant ? " (distant)" : ""));
            }
            s_poolGeneration = ImpostorPool.Generation;
            s_epoch++;
            s_started = true; s_fatSaid = false;
            // 1.2.458: fill timer + per-session cache check reset; workers made once (each owns its CellBuild; FarVegReplay's grid cache is [ThreadStatic]).
            Interlocked.Exchange(ref s_fillHits, 0); Interlocked.Exchange(ref s_fillMisses, 0); Interlocked.Exchange(ref s_cacheChecked, 0);
            s_fillCells = 0; s_fillPending = true; s_fillSw.Restart();
            if (s_workers == null)
            {
                s_workerCount = WorkerCount();
                s_workers = new Thread[s_workerCount];
                for (int w = 0; w < s_workerCount; w++)
                {
                    s_workers[w] = new Thread(WorkerLoop) { IsBackground = true, Name = "EBM FarVeg " + (w + 1), Priority = System.Threading.ThreadPriority.BelowNormal };
                    s_workers[w].Start();
                }
            }
            EasyBakeLog.Info($"{Log} cache key: seed {FarVegReplay.Seed}, replay v{FarVegReplay.Version}, list hash {FarVegKinds.ListHash:X8}, awake mode {FarVegReplay.AwakeMode}, " +
                             $"exact {(FarVegReplay.EmulateBlocking ? 1 : 0)}, format {CacheFormat} (+ zone coords and the zone's location clear area; World scope; " +
                             $"cache {(FarCache.Enabled ? (FarCache.Ready ? "on" : "on, world folder not ready yet") : "off")}; {s_workerCount} worker thread(s))");
            EasyBakeLog.Info($"{Log} on: {registered} of {n} kind(s) registered: {string.Join(", ", names.ToArray())}; replay {(s_workerOk ? "on a worker" : "on the main thread (" + MainReplayBudgetMs + " ms a frame)")}; " +
                             $"512 m cells to {ImpostorEngine.Distance:0} m; seen trees: complete zones only, {proofs} stump / log prefab(s) as removal proof; " +
                             $"start cost on the main thread {swStart.Elapsed.TotalMilliseconds:0.0} ms (replay init {tInit:0.0}, kinds {tKinds - tInit:0.0}, RNG self-test {tTest - tKinds:0.0}, kind registration {swStart.Elapsed.TotalMilliseconds - tTest:0.0}).");
            return true;
        }

        private static void Stop(string why)
        {
            if (!s_started) return;
            ImpostorEngine.ClearOwner(Owner);
            s_live.Clear(); s_queued.Clear(); s_cellData.Clear(); s_liveCount.Clear(); s_cellSentAt.Clear(); s_dirtyCells.Clear();
            while (s_todo.TryDequeue(out _)) { }
            while (s_done.TryDequeue(out _)) { }
            while (s_mainTodo.TryDequeue(out _)) { }
            s_main = null;
            s_epoch++;
            s_started = false;
            s_nextTry = Time.realtimeSinceStartup + 1f;
            EasyBakeLog.Info($"{Log} off ({why}): every cell cleared.");
        }

        internal static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
        private static void Unkey(long k, out int cx, out int cz) { cx = (int)(k >> 32); cz = (int)(uint)k; }
        private static int CellOfZone(int z) => z >= 0 ? z / ZonesPerCell : -((-z - 1) / ZonesPerCell) - 1;   // floor division
        private static float CellMin(int c) => c * CellSize - FarVegSeen.ZoneSize * 0.5f;   // zone z covers [z*64-32, z*64+32)

        private static readonly List<(long key, float d)> s_scan = new List<(long, float)>();
        private static readonly List<long> s_drop = new List<long>();

        // Main thread, once a second (FAT FarBridge.cs:473-500).
        // 1.2.464 ([visual], lead 11-19 G9 (rig path) ~40 "N cell(s) dropped: farther than 4024 m from the
        // camera at (-6464,-1348)" with the camera standing still): cells queued round the SPAWN point were still built after the teleport (1255.8 ms of worker
        // time each, :15935), uploaded by Drain, then dropped by the next Scan. Now the worker skips a queued cell that is already past the drop line of the
        // camera's last Scan position, and Drain does not upload one that went out of reach while it was building. Same cells drawn (the drop would have
        // removed them anyway); no wasted worker builds or pool upload / clear.
        private static volatile float s_camX, s_camZ, s_dropFar = float.MaxValue;
        private static int s_skipWorker, s_skipDrain;
        private static float s_skipSayAt;   // at most one skip line per 30 s (Info: test legs keep EBM Debug off)
        private static bool OutOfReach(long k)
        {
            Unkey(k, out int x, out int z);
            float far = s_dropFar;
            return Mathf.Abs(CellMin(x) + CellSize * 0.5f - s_camX) > far || Mathf.Abs(CellMin(z) + CellSize * 0.5f - s_camZ) > far;
        }

        private static void Scan(Vector3 c)
        {
            float reach = ImpostorEngine.Distance;
            s_camX = c.x; s_camZ = c.z; s_dropFar = reach + ClearMargin;   // 1.2.464: the drop line the workers / Drain test against
            if ((s_skipWorker | s_skipDrain) != 0 && Time.unscaledTime >= s_skipSayAt) { s_skipSayAt = Time.unscaledTime + 30f; EasyBakeLog.Info($"{Log} {s_skipWorker} queued cell(s) skipped before building and {s_skipDrain} not uploaded: already past the drop line ({reach + ClearMargin:0} m) of the camera at ({c.x:0},{c.z:0})"); s_skipWorker = 0; s_skipDrain = 0; }
            int a0 = CellOfZone(FarVegSeen.ZoneOf(c.x - reach)), a1 = CellOfZone(FarVegSeen.ZoneOf(c.x + reach));
            int b0 = CellOfZone(FarVegSeen.ZoneOf(c.z - reach)), b1 = CellOfZone(FarVegSeen.ZoneOf(c.z + reach));
            s_scan.Clear();
            for (int z = b0; z <= b1; z++)
                for (int x = a0; x <= a1; x++)
                {
                    long k = Key(x, z);
                    if (s_live.Contains(k) || s_queued.Contains(k)) continue;
                    float dx = CellMin(x) + CellSize * 0.5f - c.x, dz = CellMin(z) + CellSize * 0.5f - c.z;
                    s_scan.Add((k, dx * dx + dz * dz));
                }
            s_scan.Sort((p, q) => p.d.CompareTo(q.d));
            foreach (var t in s_scan) { s_queued.Add(t.key); s_todo.Enqueue((t.key, s_epoch)); }
            if (s_scan.Count > 0) s_wake.Set();
            float far = reach + ClearMargin;
            s_drop.Clear();
            foreach (var k in s_live)
            {
                Unkey(k, out int x, out int z);
                float dx = Mathf.Abs(CellMin(x) + CellSize * 0.5f - c.x), dz = Mathf.Abs(CellMin(z) + CellSize * 0.5f - c.z);
                if (dx > far || dz > far) s_drop.Add(k);
            }
            foreach (var k in s_drop) { s_live.Remove(k); s_liveCount.Remove(k); s_cellSentAt.Remove(k); s_cellData.Remove(k); ImpostorEngine.ClearCell(Owner, k); }
            if (s_drop.Count > 0) EasyBakeLog.Info($"{Log} {s_drop.Count} cell(s) dropped: farther than {far:0} m from the camera at ({c.x:0},{c.z:0}) ({s_live.Count} still live)");   // 1.2.458 (lead: 457 load resets)
        }

        // Main thread: zones the seen rule changed -> their cells, re-sent at most once per 2 s each (FAT FarBridge.cs:197-211).
        private static readonly List<long> s_doneDirty = new List<long>();
        private static void RequeueDirty(float now)
        {
            foreach (var zk in FarVegSeen.DirtyZones) { FarVegSeen.Unkey(zk, out int zx, out int zz); s_dirtyCells.Add(Key(CellOfZone(zx), CellOfZone(zz))); }
            FarVegSeen.DirtyZones.Clear();
            if (s_dirtyCells.Count == 0) return;
            s_doneDirty.Clear();
            foreach (var k in s_dirtyCells)
            {
                if (!s_live.Contains(k)) { if (!s_queued.Contains(k)) s_doneDirty.Add(k); continue; }   // not built yet: its build reads the seen zones anyway
                if (s_queued.Contains(k)) continue;
                if (s_cellSentAt.TryGetValue(k, out float at) && now - at < Resend) continue;
                s_queued.Add(k); s_todo.Enqueue((k, s_epoch)); s_doneDirty.Add(k);
            }
            foreach (var k in s_doneDirty) s_dirtyCells.Remove(k);
            if (s_doneDirty.Count > 0) s_wake.Set();
        }

        // Main thread: at most 2 SetCell a frame (FAT FarBridge.cs:502-519); the pool time-slices the upload.
        private static void Drain()
        {
            int budget = 2;
            while (budget-- > 0 && s_done.TryDequeue(out var b))
            {
                if (b.Epoch != s_epoch || !s_started) continue;
                s_queued.Remove(b.Key);
                if (b.Data == null) continue;   // the build threw (Warning logged); retried on the next Scan
                if (OutOfReach(b.Key)) { s_skipDrain++; continue; }   // 1.2.464: went past the drop line while building: the next Scan would drop it, so no upload
                if (b.Data.Length == 0) ImpostorEngine.ClearCell(Owner, b.Key);
                else ImpostorEngine.SetCell(Owner, b.Key, b.Bounds, b.Data, b.Data.Length);
                s_live.Add(b.Key);
                s_cellData[b.Key] = b;   // 1.2.458
                if (s_fillPending) s_fillCells++;   // 1.2.458 A/B timer
                s_liveCount[b.Key] = b.Data.Length;
                s_cellSentAt[b.Key] = Time.realtimeSinceStartup;
                s_cellsSent++; s_instancesSent += b.Data.Length;
            }
        }

        // ---------------------------------------------------------------- the cell build (worker, or main thread when the self-test failed)
        private sealed class CellBuild
        {
            public long Key; public int Epoch, Cx, Cz, Next;
            public readonly List<FarVegInstance> Trees = new List<FarVegInstance>(4096);
            public readonly List<FarVegInstance> Zone = new List<FarVegInstance>(256);
            public double Ms;
            public bool Worker;   // 1.2.458: the disk cache is used on the worker path only
        }

        private static void WorkerLoop()
        {
            var cb = new CellBuild { Worker = true };
            while (true)
            {
                if (!s_todo.TryDequeue(out var item)) { s_wake.WaitOne(1000); continue; }
                long key = item.key;
                if (!s_todo.IsEmpty) s_wake.Set();   // 1.2.458: AutoResetEvent wakes one waiter; pass the wake on so every worker gets a cell
                if (item.epoch != s_epoch) continue;   // s_epoch is volatile   // queued before a Stop(): drop it (the new session queues its own)
                if (!s_started || !s_workerOk) { if (!s_workerOk && s_started) s_mainTodo.Enqueue(key); continue; }
                if (OutOfReach(key)) { Interlocked.Increment(ref s_skipWorker); s_done.Enqueue(new Built { Key = key, Epoch = item.epoch }); continue; }   // 1.2.464: Data null -> Drain un-queues it; the next Scan re-queues it if it comes back into reach
                Begin(cb, key, item.epoch);
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (cb.Next < ZonesPerCell * ZonesPerCell) StepZone(cb);
                    cb.Ms += sw.Elapsed.TotalMilliseconds;
                    Finish(cb);
                }
                catch (UnityException e)
                {
                    // 1.2.486 ([2ic]): a world-gen patch on this world called a main-thread-only Unity API from our worker (e.g. EWD's lazy lava seed: Random.InitState).
                    // Stop the off-main replay for the session (Update does it on the main thread, one Info line); no per-cell Warn, no retry spam.
                    if (s_mainOnlyWhy == null) s_mainOnlyWhy = $"{e.GetType().Name}: {e.Message} (at {FirstFrame(e)})";
                    s_done.Enqueue(new Built { Key = key, Epoch = cb.Epoch });
                }
                catch (Exception e) { EasyBakeLog.Warn($"{Log} worker threw (cell {key}): {e.GetType().Name}: {e.Message}"); s_done.Enqueue(new Built { Key = key, Epoch = cb.Epoch }); }
            }
        }

        // 1.2.486 ([2ic]): set by a worker that caught a UnityException (a main-thread-only Unity call inside another mod's world-gen patch); read on the main thread.
        private static volatile string s_mainOnlyWhy;
        private static bool s_mainOnlySaid;
        private static string FirstFrame(Exception e)
        {
            string st = e.StackTrace;
            if (string.IsNullOrEmpty(st)) return "?";
            int nl = st.IndexOf('\n');
            return (nl > 0 ? st.Substring(0, nl) : st).Trim();
        }

        // Main-thread path (FarVegReplay.WorkerOk false): one cell at a time, zones until MainReplayBudgetMs is spent this frame.
        private static readonly ConcurrentQueue<long> s_mainTodo = new ConcurrentQueue<long>();
        private static CellBuild s_main;
        private static readonly System.Diagnostics.Stopwatch s_mainSw = new System.Diagnostics.Stopwatch();
        private static void MainReplay()
        {
            while (s_todo.TryDequeue(out var it)) if (it.epoch == s_epoch) s_mainTodo.Enqueue(it.key);   // the worker idles on this path; keep the nearest-first order
            s_mainSw.Restart();
            while (s_mainSw.Elapsed.TotalMilliseconds < MainReplayBudgetMs)
            {
                if (s_main == null)
                {
                    if (!s_mainTodo.TryDequeue(out long key)) return;
                    s_main = new CellBuild();
                    Begin(s_main, key);
                }
                try
                {
                    double t0 = s_mainSw.Elapsed.TotalMilliseconds;
                    StepZone(s_main);
                    s_main.Ms += s_mainSw.Elapsed.TotalMilliseconds - t0;
                    if (s_main.Next >= ZonesPerCell * ZonesPerCell) { Finish(s_main); s_main = null; }
                }
                catch (Exception e) { EasyBakeLog.Warn($"{Log} main-thread replay threw (cell {s_main.Key}): {e.GetType().Name}: {e.Message}"); s_done.Enqueue(new Built { Key = s_main.Key, Epoch = s_main.Epoch }); s_main = null; }
            }
        }

        private static void Begin(CellBuild cb, long key) => Begin(cb, key, s_epoch);   // main-thread path: same thread as Stop(), no race
        private static void Begin(CellBuild cb, long key, int epoch)
        {
            cb.Key = key; cb.Epoch = epoch; cb.Next = 0; cb.Ms = 0; cb.Trees.Clear();
            Unkey(key, out cb.Cx, out cb.Cz);
        }

        // One zone of the cell: the replay, then the seen rule (FAT FarBridge.cs:213-255, per zone: a zone is in one cell here).
        private static void StepZone(CellBuild cb)
        {
            int zx = cb.Cx * ZonesPerCell + cb.Next % ZonesPerCell, zz = cb.Cz * ZonesPerCell + cb.Next / ZonesPerCell;
            cb.Next++;
            cb.Zone.Clear();
            // 1.2.458: the replay output from the disk cache when stored (worker path only), else the replay (written back). Seen rule below, unchanged.
            string ck = cb.Worker && CacheOn ? ZoneCacheKey(zx, zz) : null;
            bool cached = false;
            if (ck != null)
            {
                cached = FarCache.TryRead(FarCacheScope.World, CacheBucket, ck, out byte[] bytes) && Decode(bytes, cb.Zone);
                if (!cached) cb.Zone.Clear();
                if (cached) { Interlocked.Increment(ref s_cacheHits); Interlocked.Increment(ref s_fillHits); }
                else { Interlocked.Increment(ref s_cacheMisses); Interlocked.Increment(ref s_fillMisses); }
            }
            if (!cached)
            {
                FarVegReplay.PlaceZone(new Vector2s(zx, zz), cb.Zone);   // ZoneSystem's zone id type is Vector2s (publicTest ZoneSystem.cs:1851)
                // written only when nothing the key holds changed during the replay (a live Exact toggle / restart = new epoch)
                if (ck != null && cb.Epoch == s_epoch && ck == ZoneCacheKey(zx, zz)) FarCache.Write(FarCacheScope.World, CacheBucket, ck, Encode(cb.Zone));
            }
            else if (EasyBakeLog.DebugOn() && Interlocked.CompareExchange(ref s_cacheChecked, 1, 0) == 0) CacheCheck(zx, zz, cb.Zone);
            if (FarVegSeen.TryGet(FarVegSeen.ZoneKey(zx, zz), out var seen))
            {
                int place = cb.Zone.Count, real = seen.Trees.Length;
                // a read far below the replay with no removal proof keeps the replay (FAT FarBridge.cs:243)
                if (seen.Proven || place - real <= FarVegSeen.SparseSlack || real >= place * FarVegSeen.SparseFraction) { cb.Zone.Clear(); cb.Zone.AddRange(seen.Trees); }
            }
            cb.Trees.AddRange(cb.Zone);
        }

        // Packs exactly as FAT's ImpostorSink.MakeArray (FAT FarBridge.cs:612-628): position, scale, yaw in radians, engine kind, tilt 0.
        private static void Finish(CellBuild cb)
        {
            var kinds = s_engineKind;
            int n = 0;
            foreach (var t in cb.Trees) if (t.Kind >= 0 && t.Kind < kinds.Length && kinds[t.Kind] >= 0) n++;
            var arr = new ImpostorInstance[n];
            float lo = float.MaxValue, hi = float.MinValue;
            int i = 0;
            foreach (var t in cb.Trees)
            {
                int kind = t.Kind >= 0 && t.Kind < kinds.Length ? kinds[t.Kind] : -1;
                if (kind < 0) continue;
                arr[i++] = new ImpostorInstance(t.Pos, t.Scale, t.Yaw * Mathf.Deg2Rad, kind, 0f, 0f);
                if (t.Pos.y < lo) lo = t.Pos.y;
                if (t.Pos.y > hi) hi = t.Pos.y;
            }
            if (n == 0) { lo = 0f; hi = 0f; }
            hi += 40f;   // the tallest crowns above the ground point (FAT used kind height x scale; the engine's kind info is main-thread only)
            float x0 = CellMin(cb.Cx), z0 = CellMin(cb.Cz);
            var b = new Bounds(new Vector3(x0 + CellSize * 0.5f, (lo + hi) * 0.5f, z0 + CellSize * 0.5f), new Vector3(CellSize, Mathf.Max(1f, hi - lo), CellSize));
            s_done.Enqueue(new Built { Key = cb.Key, Epoch = cb.Epoch, Bounds = b, Data = arr });
            Interlocked.Add(ref s_workerUs, (long)(cb.Ms * 1000.0)); Interlocked.Increment(ref s_workerCells);   // 1.2.458: several workers
        }

        // ---------------------------------------------------------------- 1.2.458 per-zone disk cache
        // On only with [10 - Far View Disk Cache] Enabled, the world folder ready, and no Debug census measurements (they change the replay mid-session).
        private static bool CacheOn => FarCache.Enabled && FarCache.Ready && !FarVegReplay.CensusActive;

        // Seed, replay version, kind list hash, Awake mode, exact placement, record format, zone, and the zone's location clear area (per peer).
        private static string ZoneCacheKey(int zx, int zz)
        {
            Vector3 clear = FarVegReplay.ClearOf(zx, zz);
            return FarCache.Key("FarVegZone", CacheFormat, FarVegReplay.Seed, FarVegReplay.Version, FarVegKinds.ListHash, FarVegReplay.AwakeMode,
                                FarVegReplay.EmulateBlocking, zx, zz, clear.x, clear.y, clear.z);
        }

        // Layout (little-endian): magic, format, count, then count x (kind int, x, y, z, yaw, scale, tiltX, tiltZ floats).
        private static byte[] Encode(List<FarVegInstance> list)
        {
            var b = new byte[12 + list.Count * RecordBytes];
            int o = 0;
            PutI(b, ref o, CacheMagic); PutI(b, ref o, CacheFormat); PutI(b, ref o, list.Count);
            foreach (var t in list)
            {
                PutI(b, ref o, t.Kind); PutF(b, ref o, t.Pos.x); PutF(b, ref o, t.Pos.y); PutF(b, ref o, t.Pos.z);
                PutF(b, ref o, t.Yaw); PutF(b, ref o, t.Scale); PutF(b, ref o, t.TiltX); PutF(b, ref o, t.TiltZ);
            }
            return b;
        }

        // false = malformed (treated as a miss; the replay runs and overwrites the entry).
        private static bool Decode(byte[] b, List<FarVegInstance> into)
        {
            if (b == null || b.Length < 12) return false;
            int o = 0;
            if (GetI(b, ref o) != CacheMagic || GetI(b, ref o) != CacheFormat) return false;
            int n = GetI(b, ref o);
            if (n < 0 || b.Length != 12 + (long)n * RecordBytes) return false;
            int kinds = FarVegKinds.Count;
            for (int i = 0; i < n; i++)
            {
                var t = new FarVegInstance { Kind = GetI(b, ref o) };
                float x = GetF(b, ref o), y = GetF(b, ref o), z = GetF(b, ref o);
                t.Pos = new Vector3(x, y, z);
                t.Yaw = GetF(b, ref o); t.Scale = GetF(b, ref o); t.TiltX = GetF(b, ref o); t.TiltZ = GetF(b, ref o);
                if (t.Kind < 0 || t.Kind >= kinds) return false;
                into.Add(t);
            }
            return true;
        }

        private static void PutI(byte[] b, ref int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); o += 4; }
        private static void PutF(byte[] b, ref int o, float f) => PutI(b, ref o, new FloatBits { F = f }.I);
        private static int GetI(byte[] b, ref int o) { int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24); o += 4; return v; }
        private static float GetF(byte[] b, ref int o) => new FloatBits { I = GetI(b, ref o) }.F;

        // Debug stale-cache proof, once per session on the first cached zone: the zone replayed fresh vs the cached copy (worker thread; PlaceZone is worker-safe).
        private static void CacheCheck(int zx, int zz, List<FarVegInstance> cachedList)
        {
            try
            {
                var fresh = new List<FarVegInstance>(cachedList.Count + 16);
                FarVegReplay.PlaceZone(new Vector2s(zx, zz), fresh);
                bool same = fresh.Count == cachedList.Count;
                for (int i = 0; same && i < fresh.Count; i++)
                {
                    FarVegInstance a = fresh[i], c = cachedList[i];
                    same = a.Kind == c.Kind && a.Pos.x == c.Pos.x && a.Pos.y == c.Pos.y && a.Pos.z == c.Pos.z && a.Yaw == c.Yaw && a.Scale == c.Scale && a.TiltX == c.TiltX && a.TiltZ == c.TiltZ;
                }
                EasyBakeLog.Info($"{Log} cache check zone ({zx},{zz}): cached {cachedList.Count}, fresh {fresh.Count}, identical {(same ? "yes" : "no")}");
            }
            catch (Exception e) { EasyBakeLog.Debug($"{Log} cache check threw: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
