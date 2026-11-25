using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.369 [Cache] ZdoFirstBake: PROTOTYPE, default off ([seasons] 11-14 for [lead], [visual] on the MeshBaker / cache side).
    // Design: Tools\DESIGN_T1_cold_entry_20261114.md section 3.
    //
    // The row: T1, the cold town entry. With no cache, about 48,000 pieces are created real, kept live for 35 s or more, then
    // converted back after their zone bakes (TrinityPerf instances 23.5k warm vs 72k cold, cold pairs 0434_2A / 0452_5A).
    // A cache hit has none of that: ConstructCachedZonesInRange / EnsureZoneFromCache attach the bake before vanilla creates
    // the pieces, and WouldSkip skips them.
    //
    // This builds the instanced part of that bake from the zone's ZDOs on the first visit, so the zone is attached like a cache
    // hit. Same tests as MeshBaker.Classify / BuildInstanceGroups and TryLateJoinFromZdo, read from the ZDO and the prefab:
    //  - eligible: a building piece (WearNTear), pure structure (SkipEligibility), not bake-unsafe, no edits only a real piece
    //    carries, invulnerable or (damageable baking on and showing its healthy look);
    //  - grouped by InstanceKey (prefab, look) where InstanceDefinitionCache has a definition; MinInstancesPerPrefab as at bake;
    //  - matrix TRS(ZDO position, rotation, scale as ZNetView.Awake sets it) carried through the definition's offset (group.Add);
    //  - a saved transform only where the bake records one (invulnerable, or SkipDamageablePieces), so skipping stays WouldSkip's.
    // NOT in the prototype: combined batches and vegetation. Pieces they would draw are LEFT LIVE and draw themselves ([lead]
    // approved). Whole-zone fallback (today's path, one Debug line naming why): an unknown prefab, or a piece prefab carrying
    // MaterialVariation / MaterialVariationWorld ([visual] (b)). No cache file is ever written for a ZDO-baked zone (SaveZone);
    // a zone baked fresh this session (built in, damaged, removed: Dirty) keeps today's path for the rest of the session.
    // 1.2.370 ([lead], cold ABBA on 1.2.369: about half the zones still baked fresh): (1) nothing to instance = a wait, not a decline;
    // (2) CreateZone asks first, so a zone whose first created object came before the range pass is still built from ZDOs.
    // 1.2.371 ([lead], "attach early, keep filling"; 1.2.370 Diagnostic 0912_1B: eight scans > 10 ms in one frame, "from 12104 ZDO(s) in
    // 60.6 ms" (range pass), "from 9158 ZDO(s) in 33.3 ms" (skip check), "from 749 ZDO(s) in 35.1 ms" (first piece created, the session's
    // first zone)). The scan is SLICED under [Cache] ZdoFirstBakeBudgetMs a frame, shared by all zones in scan:
    //  - the range pass starts a scan as soon as the zone has ZDOs; the zone is attached when the scan lands, if no creation asked first;
    //  - a creation ask (skip check, first piece created) on a zone in scan, or on a new one, ATTACHES THE ZONE AT ONCE with what is
    //    classified so far (slicing on, in this frame, until it has at least one group or the scan is done), then the scan keeps
    //    filling the attached zone's groups slice by slice (AddLate + contributor + saved transform, as the late join does);
    //  - a piece is never drawn twice: a ZDO whose object exists by its slice (zdo.Created, not one of ours skipped) or that a group
    //    already holds (late-joined) is not added; the first is counted "created before its slice" and draws itself;
    //  - a ZDO destroyed during the scan is never drawn: its uid is checked when it is classified and again when it is added;
    //  - a key's group is opened once it has MinInstancesPerPrefab pieces (held until then); at the scan's end the rest follow the
    //    bake's low-count rules or stay live;
    //  - RequestFullWalk at most once per frame for all slices; a zone torn down or gone Dirty while filling drops its scan.
    // Not sliced in the loading screen. Budget 0 = the whole scan in one frame at the first ask (1.2.370).
    internal static partial class ZoneTracker
    {
        private static bool ZdoFirstBakeOn => FiresEasyBakeMeshesPlugin.ZdoFirstBake != null && FiresEasyBakeMeshesPlugin.ZdoFirstBake.Value;

        private static readonly ProfilerMarker s_zdoBakeMarker = new ProfilerMarker("EBM check: ZDO first bake");
        private static readonly ProfilerMarker s_zdoSliceMarker = new ProfilerMarker("EBM check: ZDO first bake slice");   // 1.2.371

        // Zones this session will not bake from ZDOs (a hard fallback, or baked fresh from live pieces).
        private static readonly HashSet<Vector2s> s_zdoBakeDeclined = new HashSet<Vector2s>();

        // 1.2.370 lever (1) ([lead], cold ABBA on 1.2.369: fresh_zone_files 26 OFF vs 14 / 12 ON): 1.2.369 declined a zone for the session when its
        // first scan found nothing to instance, which on a dedi is a zone whose pieces had not arrived yet (trees and rocks first). A scan that finds
        // nothing now leaves a wait: the zone is scanned again only once its sector's ZDO count has moved (SectorCount, no walk), at most once a
        // second; the range pass gives up after the count has stood still ZdoWaitSettleSeconds, a creation-time ask (lever 2) still scans.
        private sealed class ZdoWait { public int Count; public int Scans; public float StableSince; public float RetryAt; public bool GaveUp; public bool Said; }
        private static readonly Dictionary<Vector2s, ZdoWait> s_zdoBakeWait = new Dictionary<Vector2s, ZdoWait>();
        private const float ZdoWaitSettleSeconds = 5f;

        // How a zone came to be asked. FirstPiece / FirstTree = lever (2): CreateZone, the one place a zone starts being tracked, asks first.
        internal enum ZdoVia { RangePass = 0, SkipCheck = 1, FirstPiece = 2, FirstTree = 3 }
        private static readonly string[] ZdoViaNames = { "range pass", "skip check", "first piece created", "first tree created" };
        private static readonly int[] s_zdoVia = new int[4];
        private static int s_zdoAfterWait, s_zdoWaits, s_zdoGaveUp;

        // 1.2.371: one zone's scan. The ZDO list is a snapshot with each ZDO's uid as it was then (a pooled ZDO object can be another ZDO later).
        // Before the zone is attached, classified pieces are only held; once attached (State set), each slice adds to the zone's groups.
        private sealed class ZdoScan
        {
            public Vector2s Coord;
            public ZdoVia Via;
            public ZdoVia AttachedBy;
            public readonly List<ZDO> Zdos = new List<ZDO>();
            public readonly List<ZDOID> Uids = new List<ZDOID>();
            public int Next;
            public readonly Dictionary<InstanceKey, ZdoKeyBucket> ByKey = new Dictionary<InstanceKey, ZdoKeyBucket>();
            public readonly int[] LeftLive = new int[ZdoLeftLiveKinds];
            public int Seen, Others, SectorCount, Slices, StartFrame, AttachedAtSlice = -1, Baked, Skippable, JoinedBefore, Gone, EarlierScans, MaxKeyCount;
            public bool Early;   // attached by a creation ask before the scan was done
            public float Now0;
            public long Ticks, WorstSliceTicks, FirstSightEligibility, FirstSightPrefab, Definitions;
            public int FirstSightEligibilityN, FirstSightPrefabN;
            public string Fallback;
            public bool FileOnly;   // 1.2.374: the background file job's scan: nothing is attached or added, pieces are only held
            public readonly Dictionary<InstanceKey, List<ZdoBakePiece>> Combine = new Dictionary<InstanceKey, List<ZdoBakePiece>>();   // 1.2.374 (FileOnly): invulnerable pieces of keys with no instance definition
            public ZoneState State;   // set once attached
            public bool Done => Next >= Zdos.Count;
        }
        // One InstanceKey's pieces in a scan: held until the key has its group, then added as they come.
        private sealed class ZdoKeyBucket
        {
            public readonly List<ZdoBakePiece> Held = new List<ZdoBakePiece>();
            public ZoneInstanceGroup Group;
            public int Count;   // every piece classified to this key, held or added
        }
        private static readonly Dictionary<Vector2s, ZdoScan> s_zdoScans = new Dictionary<Vector2s, ZdoScan>();
        private static readonly List<ZdoScan> s_zdoScanOrder = new List<ZdoScan>();
        private static int s_zdoSliced, s_zdoAttachedEarly, s_zdoScansDropped, s_zdoCreatedBeforeSlice;
        private static long s_zdoWorstSliceTicks;
        private static bool s_zdoWalkDue;

        private sealed class ZdoBakePrefab { public bool Piece; public string Variation; public bool FileInfoTried; public SkipEligibility.PrefabInfo FileInfo; }
        private static readonly Dictionary<int, ZdoBakePrefab> s_zdoBakePrefabs = new Dictionary<int, ZdoBakePrefab>();

        private struct ZdoBakePiece
        {
            public MeshBaker.PieceIdentity Identity;
            public MeshBaker.PieceTransform Transform;
            public bool Invulnerable;
            public ZDO Zdo;     // 1.2.371: re-checked when it is added
            public ZDOID Uid;
        }

        private const int ZdoLeftLiveKinds = 8;
        private static readonly string[] ZdoLeftLiveNames =
        {
            "runtime component", "bake-unsafe", "edits only a real piece carries", "damageable baking off",
            "not its healthy look", "no instance definition (combine candidate)", "below MinInstancesPerPrefab",
            "created before its slice",   // 1.2.371: its object existed when the scan reached it; it draws itself
        };
        private static readonly int[] s_zdoLeftLiveTotal = new int[ZdoLeftLiveKinds];
        private static int s_zonesFromZdo, s_zdoBakeFallbacks, s_zdoBakedPieces, s_zdoGoneDuringScan;
        private static long s_zdoBakeTicks;
        private static bool s_saidZdoBake;

        // 1.2.380 ([lead], Fire: off/on tests toggle keys live in one run; Tools\HOTSWAP_EBM_KEYS_20261114.md): [Cache] ZdoFirstBake is read at every use, but while it is off
        // PumpZdoScans is not called, so work in progress froze where it was: a scan that had not attached its zone, the background file job, a swap still building its
        // batches (its holder inactive). Turned OFF, that work is dropped now: unattached scans go (their zones take today's path), an attached zone's scan stops filling
        // (what it draws stays; the rest stays live), the file job is forgotten (the zone stays wanted), a swap not yet handed to the silencer is cancelled (holder destroyed,
        // contributors taken back) and queued swaps are forgotten. A swap already in MeshBaker's queue finishes there (that pump runs whatever the key says). Zones already
        // attached stay as they are: they are valid bakes. Turned ON nothing is needed. Hooked once, from the range pass (HookZdoKey in ConstructCachedZonesInRange).
        private static bool s_zdoKeyHooked;
        // 1.2.382: ebm_coldcache on | off | status (MeshCacheStore.SwitchColdSession). Registered once, from the range pass, so it exists in every world.
        private static bool s_coldCommandHooked;
        private static void HookColdCommand()
        {
            if (s_coldCommandHooked) return;
            s_coldCommandHooked = true;
            try
            {
                new Terminal.ConsoleCommand("ebm_coldcache",
                    "on|off|status FiresEasyBakeMeshes: test tool. on = this session's saved zones move to an empty test folder (the real cache is untouched and not read), so zones bake as on a first visit after PluginEnabled off -> on; off = back to the real cache. Never saved; a new launch is always the real cache.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string a = args.Args != null && args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "status";
                        string line;
                        if (a == "status") line = MeshCacheStore.ColdSessionStatus();
                        else if (a == "on" || a == "off")
                        {
                            line = MeshCacheStore.SwitchColdSession(a == "on");
                            if (line.StartsWith("[Cache]", System.StringComparison.Ordinal)) { s_zdoFilesWritten.Clear(); s_zdoFileSavedCount.Clear(); s_zdoFileRefused.Clear(); s_zdoFileQuiet.Clear(); }   // this session's ZDO-file notes were about the folder just left
                        }
                        else line = "ebm_coldcache on|off|status";
                        args.Context?.AddString(line);
                    }));
            }
            catch (System.Exception e) { EasyBakeLog.Warn("[Cache] ebm_coldcache could not be registered: " + e.Message); }
        }

        private static void HookZdoKey()
        {
            HookColdCommand();            if (s_zdoKeyHooked || FiresEasyBakeMeshesPlugin.ZdoFirstBake == null) return;
            s_zdoKeyHooked = true;
            FiresEasyBakeMeshesPlugin.ZdoFirstBake.SettingChanged += (_, __) => { if (!ZdoFirstBakeOn) ZdoFirstBakeTurnedOff(); };
        }

        private static void ZdoFirstBakeTurnedOff()
        {
            int scans = 0, attachedScans = 0;
            for (int i = s_zdoScanOrder.Count - 1; i >= 0; i--)
            {
                var scan = s_zdoScanOrder[i];
                if (scan.State != null) attachedScans++; else scans++;
                s_zdoScans.Remove(scan.Coord);
                s_zdoScanOrder.RemoveAt(i);
            }
            bool job = s_zdoFileJob != null;
            s_zdoFileJob = null;   // a ZdoCombine request still running finishes on its worker and is not read
            bool swapCancelled = false;
            if (s_zdoSwap != null && s_zdoSwap.Queued == null) { CancelZdoSwap(s_zdoSwap, "[Cache] ZdoFirstBake was turned off while its batches were built", false); swapCancelled = true; }
            int queued = s_zdoSwapQueue.Count;
            s_zdoSwapQueue.Clear();   // the verifier's queue is left alone: it is built to run with the key off ([visual])
            EasyBakeLog.Info($"[Cache] ZdoFirstBake turned off: dropped {scans} scan(s) of zones not attached yet and {attachedScans} still filling an attached zone, {(job ? "1 file job" : "no file job")}, {(swapCancelled ? "1 swap in its build phase" : "no building swap")}, {queued} queued swap(s); zones already built from ZDOs keep their bake ({s_zdoFileWanted.Count} still without a file)");
            ZdoSessionSummary("the key was turned off");   // 1.2.386
        }

        // The range pass asks only while this is true (a cheap check, run for every loaded zone with no cache, every frame).
        internal static bool ZdoFirstBakeWanted(Vector2s coord)
        {
            if (!ZdoFirstBakeOn || s_zdoBakeDeclined.Contains(coord) || s_zdoScans.ContainsKey(coord)) return false;   // 1.2.371: a scan in progress is the pump's
            if (!s_zdoBakeWait.TryGetValue(coord, out var wait)) return true;
            return !wait.GaveUp && Time.unscaledTime >= wait.RetryAt;
        }

        private static float ZdoSliceBudgetMs()
        {
            var key = FiresEasyBakeMeshesPlugin.ZdoFirstBakeBudgetMs;
            return key != null ? key.Value * PerformanceProfile.Budget : 0f;
        }

        // Mirrors ZNetScene.InLoadingScreen: no local player yet, or teleporting.
        private static bool InLoadingScreenNow() => Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting();

        // The zone's state once it is attached from its ZDOs (now, or already by a scan in progress), or null: today's path, or (range pass)
        // a scan started that attaches the zone when it lands. Callers have checked the zone is not in _zones and has no preloaded cache.
        private static ZoneState TryZdoFirstBake(Vector2s coord, ZdoVia via)
        {
            if (!ZdoFirstBakeOn || s_zdoBakeDeclined.Contains(coord)) return null;
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZNetScene.instance == null) return null;
            bool creationAsk = via != ZdoVia.RangePass;
            if (s_zdoScans.TryGetValue(coord, out var running))
            {
                if (!creationAsk || running.State != null) return running.State;
                using (s_zdoBakeMarker.Auto()) return AttachEarly(running, via);
            }
            float now0 = Time.unscaledTime;
            int sectorCount = SectorCount(coord);   // -1 = no cheap count: the wait then falls back to the 1 s retry alone
            s_zdoBakeWait.TryGetValue(coord, out var wait);
            if (wait != null)
            {
                if (via == ZdoVia.RangePass && wait.GaveUp) return null;
                if (now0 < wait.RetryAt && via != ZdoVia.FirstPiece && via != ZdoVia.FirstTree) return null;   // CreateZone asks once per zone (it tracks the zone either way), so it skips the 1 s timer; the per-ZDO skip check keeps it
                if (sectorCount < 0 && via == ZdoVia.RangePass && now0 - wait.StableSince >= ZdoWaitSettleSeconds) { wait.GaveUp = true; s_zdoGaveUp++; EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): ZdoFirstBake range pass stops asking: no sector count to watch and nothing to instance for {ZdoWaitSettleSeconds:0} s ({wait.Scans} scan(s)); its first piece creation still asks"); return null; }   // [visual] (A): without a count the wait gives up on time alone
                if (sectorCount >= 0 && sectorCount == wait.Count)   // nothing new arrived since the last scan found nothing: no rescan
                {
                    if (via == ZdoVia.RangePass && now0 - wait.StableSince >= ZdoWaitSettleSeconds)
                    {
                        wait.GaveUp = true; s_zdoGaveUp++;
                        EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): ZdoFirstBake range pass stops asking: still nothing to instance after its {wait.Count} ZDO(s) stood still {ZdoWaitSettleSeconds:0} s ({wait.Scans} scan(s)); its first piece creation still asks");
                    }
                    wait.RetryAt = now0 + 1f;
                    return null;
                }
            }
            using (s_zdoBakeMarker.Auto())
            {
                if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return ZdoBakeDecline(coord, "[Batching] InstancingEnabled is off");
                long t0 = Stopwatch.GetTimestamp();
                var scan = new ZdoScan { Coord = coord, Via = via, SectorCount = sectorCount, Now0 = now0, StartFrame = Time.frameCount };
                zdoMan.FindSectorObjects(coord, new SimulationDistance(0, 0), scan.Zdos);
                if (scan.Zdos.Count == 0) { ZdoBakeWait(coord, ref wait, sectorCount, now0, via, "no ZDO delivered yet"); return null; }
                for (int i = 0; i < scan.Zdos.Count; i++) scan.Uids.Add(scan.Zdos[i] != null ? scan.Zdos[i].m_uid : ZDOID.None);
                scan.Ticks += Stopwatch.GetTimestamp() - t0;
                if (ZdoSliceBudgetMs() <= 0f || InLoadingScreenNow()) return FinishZdoScan(scan, via);   // unsliced: the whole scan now (1.2.370)
                s_zdoScans.Add(coord, scan);
                s_zdoScanOrder.Add(scan);
                s_zdoSliced++;
                return creationAsk ? AttachEarly(scan, via) : null;
            }
        }

        // A creation asked about a zone still in scan: classify in this frame until the zone has a group (or the scan is done), then attach it.
        // Its remaining ZDOs keep slicing into the attached zone.
        private static ZoneState AttachEarly(ZdoScan scan, ZdoVia via)
        {
            long t0 = Stopwatch.GetTimestamp();
            while (scan.Fallback == null && !scan.Done && !HasAnyGroupYet(scan)) ClassifyZdo(scan);
            scan.Ticks += Stopwatch.GetTimestamp() - t0;
            if (scan.Fallback != null || scan.Done) return FinishZdoScan(scan, via);
            var state = AttachZdoScan(scan, via);
            if (state != null) { scan.Early = true; s_zdoAttachedEarly++; }
            return state;
        }

        // True once some key has enough pieces for a group (the same minimum the group opening uses); the scan keeps its largest key count.
        private static bool HasAnyGroupYet(ZdoScan scan) => scan.MaxKeyCount >= FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;

        // 1.2.371: once a frame from the range pass. Works the scans in the order they started under one shared budget (the first scan in
        // order always gets at least one ZDO, so every scan lands in turn). A zone that left the loaded area unattached, or whose attached
        // state was torn down or went Dirty, drops its scan.
        private static void PumpZdoScans()
        {
            PumpZdoFiles();   // 1.2.374: the background full-file job
            if (s_zdoScanOrder.Count == 0) return;
            using (s_zdoSliceMarker.Auto())
            {
                var zoneSystem = ZoneSystem.instance;
                long budgetTicks = (long)(ZdoSliceBudgetMs() * Stopwatch.Frequency / 1000.0);
                bool unsliced = budgetTicks <= 0 || InLoadingScreenNow();
                long frameStart = Stopwatch.GetTimestamp();
                for (int s = 0; s < s_zdoScanOrder.Count; s++)
                {
                    var scan = s_zdoScanOrder[s];
                    string drop = null;
                    if (scan.State != null)
                    {
                        if (!_zones.TryGetValue(scan.Coord, out var now) || now != scan.State) drop = "its zone was torn down";
                        else if (scan.State.Dirty || !scan.State.ZdoBaked) drop = "its zone went to today's path (changed)";
                    }
                    else if (_zones.ContainsKey(scan.Coord)) drop = "the zone was tracked meanwhile";
                    else if (zoneSystem == null || !EbmZones.Loaded(zoneSystem, scan.Coord)) drop = "the zone left the loaded area";
                    if (drop != null) { DropZdoScan(scan, drop); s--; continue; }

                    long sliceStart = Stopwatch.GetTimestamp();
                    bool any = false;
                    while (!scan.Done && scan.Fallback == null && (unsliced || !any || Stopwatch.GetTimestamp() - frameStart < budgetTicks))
                    {
                        ClassifyZdo(scan);
                        any = true;
                    }
                    long slice = Stopwatch.GetTimestamp() - sliceStart;
                    if (any) { scan.Slices++; scan.Ticks += slice; if (slice > scan.WorstSliceTicks) scan.WorstSliceTicks = slice; if (slice > s_zdoWorstSliceTicks) s_zdoWorstSliceTicks = slice; }
                    if (scan.Done || scan.Fallback != null) { FinishZdoScan(scan, scan.State != null ? scan.AttachedBy : scan.Via); s--; }   // FinishZdoScan takes it out of s_zdoScanOrder
                    if (!unsliced && Stopwatch.GetTimestamp() - frameStart >= budgetTicks) break;
                }
                if (s_zdoWalkDue) { s_zdoWalkDue = false; Indirect.EbmIndirect.RequestFullWalk(); }   // at most once a frame for all slices
            }
        }

        private static void DropZdoScan(ZdoScan scan, string why)
        {
            s_zdoScans.Remove(scan.Coord);
            s_zdoScanOrder.Remove(scan);
            s_zdoScansDropped++;
            EasyBakeLog.Debug($"[Cache] zone ({scan.Coord.x},{scan.Coord.y}): ZdoFirstBake scan dropped after {scan.Next} of {scan.Zdos.Count} ZDO(s){(scan.State != null ? $" ({scan.Baked} piece(s) already drawn from it)" : "")}: {why}");
        }

        // One ZDO of a scan: classified into its key's bucket (and, once the key has a group and the zone is attached, added), counted as
        // left live or other, or a hard fallback for the whole zone (scan.Fallback). Debug-only sub-timers split the first-sight work.
        private static void ClassifyZdo(ZdoScan scan)
        {
            int i = scan.Next++;
            var zdo = scan.Zdos[i];
            if (zdo == null || !zdo.IsValid() || zdo.m_uid != scan.Uids[i]) { scan.Gone++; return; }   // destroyed since the snapshot (its pooled object may be another ZDO now)
            scan.Seen++;
            int prefabHash = zdo.GetPrefab();
            var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabHash);
            if (prefab == null)
            {
                Vector3 p = zdo.GetPosition();
                scan.Fallback = $"unknown prefab {prefabHash} at ({p.x:0},{p.y:0},{p.z:0})";
                return;
            }
            bool firstEligibility = !SkipEligibility.Known(prefabHash);
            long te = firstEligibility ? Stopwatch.GetTimestamp() : 0;
            var info = SkipEligibility.Get(prefabHash);
            if (firstEligibility) { scan.FirstSightEligibility += Stopwatch.GetTimestamp() - te; scan.FirstSightEligibilityN++; }
            bool firstWalk = !s_zdoBakePrefabs.ContainsKey(prefabHash);
            long tw = firstWalk ? Stopwatch.GetTimestamp() : 0;
            var kind = ZdoBakePrefabOf(prefabHash, prefab, info);
            if (!kind.Piece) { if (firstWalk) { scan.FirstSightPrefab += Stopwatch.GetTimestamp() - tw; scan.FirstSightPrefabN++; } scan.Others++; return; }   // not a building piece: trees, rocks, items, creatures keep today's path
            if (!info.Skippable)
            {
                // 1.2.378 ([lead] option 1; verifier 1349_1A: MISSING hard 18, all "merged live ... given to the combine (not covered there)"): the FILE job takes a bake-safe
                // piece that is not pure structure exactly as the live bake does (MeshBaker.Classify / Bake never test Skippable), so the ZDO-built file equals a live-built one
                // and its material batches reach MinPiecesPerBatch as they do live. Such a piece is never skipped at creation (WouldSkip / ZdoMatches refuse !Skippable): on every
                // visit it is created and silenced on arrival, as with a live file. The first-visit attach keeps leaving them live.
                var fileInfo = scan.FileOnly ? ZdoFileInfoOf(kind, prefab) : null;
                if (fileInfo == null) { if (firstWalk) { scan.FirstSightPrefab += Stopwatch.GetTimestamp() - tw; scan.FirstSightPrefabN++; } scan.LeftLive[0]++; return; }
                info = fileInfo;
            }
            if (!info.BakeUnsafe.HasValue) info.BakeUnsafe = HasBakeUnsafeComponent(info.Prefab);
            if (firstWalk) { scan.FirstSightPrefab += Stopwatch.GetTimestamp() - tw; scan.FirstSightPrefabN++; }
            if (kind.Variation != null) { scan.Fallback = $"prefab '{prefab.name}' carries {kind.Variation}; its swapped materials are not in the prefab"; return; }
            if (info.BakeUnsafe.Value) { scan.LeftLive[1]++; return; }
            if (PieceData.MustStayLive(zdo, prefabHash)) { scan.LeftLive[2]++; return; }
            bool invulnerable = info.AllImmune || SkipEligibility.StoredHealthIsNegative(zdo, info);
            if (!invulnerable && !FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value) { scan.LeftLive[3]++; return; }
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (!invulnerable && look != WearLooks.Healthy(info.Prefab)) { scan.LeftLive[4]++; return; }
            var key = new InstanceKey(prefabHash, look);
            long td = Stopwatch.GetTimestamp();
            bool hasDefinition = InstanceDefinitionCache.TryGet(key, out var definition);
            scan.Definitions += Stopwatch.GetTimestamp() - td;   // all lookups; a first-sight build is what makes it large
            if (!hasDefinition)
            {
                if (scan.FileOnly && invulnerable)   // 1.2.374: a combine candidate for the full file (ZdoCombine decides whether it is covered)
                {
                    if (!scan.Combine.TryGetValue(key, out var candidates)) { candidates = new List<ZdoBakePiece>(); scan.Combine.Add(key, candidates); }
                    candidates.Add(new ZdoBakePiece { Identity = MeshBaker.PieceIdentity.From(zdo), Transform = new MeshBaker.PieceTransform { Position = zdo.GetPosition(), Rotation = zdo.GetRotation(), Scale = SkipEligibility.ExpectedScale(zdo, info), Look = look }, Invulnerable = true, Zdo = zdo, Uid = scan.Uids[i] });
                    return;
                }
                scan.LeftLive[5]++; return;
            }

            if (!scan.ByKey.TryGetValue(key, out var bucket)) { bucket = new ZdoKeyBucket(); scan.ByKey.Add(key, bucket); }
            var piece = new ZdoBakePiece
            {
                Identity = MeshBaker.PieceIdentity.From(zdo),
                Transform = new MeshBaker.PieceTransform { Position = zdo.GetPosition(), Rotation = zdo.GetRotation(), Scale = SkipEligibility.ExpectedScale(zdo, info), Look = look },
                Invulnerable = invulnerable,
                Zdo = zdo,
                Uid = scan.Uids[i],
            };
            bucket.Count++;
            if (bucket.Count > scan.MaxKeyCount) scan.MaxKeyCount = bucket.Count;
            if (scan.State == null) { bucket.Held.Add(piece); return; }   // not attached yet: the groups are built at the attach
            if (bucket.Group == null)
            {
                bucket.Held.Add(piece);
                if (bucket.Count < FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value) return;
                OpenScanGroup(scan, key, bucket, definition);   // the key reached the minimum while attached: its held pieces go in now
                return;
            }
            AddScanPiece(scan, bucket.Group, piece);
        }

        // Opens a key's group in an attached zone and adds its held pieces.
        private static void OpenScanGroup(ZdoScan scan, InstanceKey key, ZdoKeyBucket bucket, InstanceDefinition definition)
        {
            // [visual] (A): a late join (TryJoinInstanceGroup -> OpenInstanceGroup) may already have opened this key's group in the zone: reuse it, never a second one.
            bucket.Group = GroupFor(scan.State, key.PrefabHash, key.Look);
            if (bucket.Group == null)
            {
                bucket.Group = new ZoneInstanceGroup { PrefabHash = key.PrefabHash, Look = key.Look, Definition = definition };
                scan.State.Bake.InstanceGroups.Add(bucket.Group);
                s_zdoWalkDue = true;
            }
            for (int h = 0; h < bucket.Held.Count; h++) AddScanPiece(scan, bucket.Group, bucket.Held[h]);
            bucket.Held.Clear();
        }

        // One piece into an attached zone's group, unless it is gone, already drawn by a group (late-joined), or already a real object.
        private static void AddScanPiece(ZdoScan scan, ZoneInstanceGroup group, ZdoBakePiece piece)
        {
            var bake = scan.State.Bake;
            if (piece.Zdo == null || !piece.Zdo.IsValid() || piece.Zdo.m_uid != piece.Uid) { scan.Gone++; return; }
            if (bake.ContributorIdentities.Contains(piece.Identity)) { scan.JoinedBefore++; return; }   // a group already draws it (TryJoinInstanceGroup)
            if (piece.Zdo.Created && !scan.State.Skipped.ContainsKey(piece.Zdo)) { scan.LeftLive[7]++; return; }   // its object exists and draws itself: never twice
            group.AddLate(Matrix4x4.TRS(piece.Transform.Position, piece.Transform.Rotation, piece.Transform.Scale), piece.Identity);   // AddLate carries it through Definition.LocalOffset, bounds at the next draw
            bake.ContributorIdentities.Add(piece.Identity);
            if (piece.Invulnerable || SkipEligibility.DamageableSkipAllowed) { bake.PieceTransforms[piece.Identity] = piece.Transform; scan.Skippable++; }
            scan.Baked++;   // AddLate marks its group dirty for the indirect path itself; only a new group needs the full walk (OpenScanGroup)
        }

        // Builds a BakeResult from the held pieces of every key that has its minimum (and, at the scan's end, the low-count rules) and
        // attaches the zone like a cache hit. Returns the state, or null if nothing could be grouped.
        private static ZoneState AttachZdoScan(ZdoScan scan, ZdoVia via)
        {
            var coord = scan.Coord;
            var result = new MeshBaker.BakeResult { ContributorIdentities = new HashSet<MeshBaker.PieceIdentity>() };
            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
            foreach (var entry in scan.ByKey)
            {
                var bucket = entry.Value;
                if (bucket.Held.Count == 0) continue;
                bool lowCount = bucket.Count < minInstances;
                if (lowCount && !(scan.Done && ZdoOnlyInstancingCanDraw(entry.Key.PrefabHash, entry.Key.Look, bucket.Held))) continue;   // held: may still reach the minimum; at the end decided by FinishZdoScan
                bool damageableOnly = lowCount && ZdoLowCountDamageableOnly(entry.Key.PrefabHash, entry.Key.Look);   // as LowCountDamageableOnly: invulnerable siblings stay out
                if (!InstanceDefinitionCache.TryGet(entry.Key, out var definition)) continue;
                var group = new ZoneInstanceGroup { PrefabHash = entry.Key.PrefabHash, Look = entry.Key.Look, Definition = definition };
                var keep = new List<ZdoBakePiece>();
                for (int i = 0; i < bucket.Held.Count; i++)
                {
                    var piece = bucket.Held[i];
                    if (piece.Zdo == null || !piece.Zdo.IsValid() || piece.Zdo.m_uid != piece.Uid) { scan.Gone++; continue; }   // destroyed since its slice
                    if (piece.Zdo.Created) { scan.LeftLive[7]++; continue; }   // not attached yet, so no piece of ours is skipped: an existing object draws itself
                    if (damageableOnly && piece.Invulnerable) { keep.Add(piece); continue; }
                    group.Add(Matrix4x4.TRS(piece.Transform.Position, piece.Transform.Rotation, piece.Transform.Scale), piece.Identity);   // Add carries it through Definition.LocalOffset
                    result.ContributorIdentities.Add(piece.Identity);
                    // As MeshBaker.Bake's instanced loop: a stand-in transform only where the piece may skip creation.
                    if (piece.Invulnerable || SkipEligibility.DamageableSkipAllowed) { result.PieceTransforms[piece.Identity] = piece.Transform; scan.Skippable++; }
                    scan.Baked++;
                }
                bucket.Held.Clear();
                bucket.Held.AddRange(keep);
                if (group.Matrices.Count == 0) continue;
                group.RecomputeBounds();
                result.InstanceGroups.Add(group);
                bucket.Group = group;
            }
            if (result.InstanceGroups.Count == 0) return null;

            float now = Time.unscaledTime;
            var parent = new GameObject($"[EasyBake/Zone_{coord.x}_{coord.y}/zdo]");   // as ConstructFromCache: the zone's root, torn down with it
            parent.transform.position = Vector3.zero;
            parent.isStatic = true;
            result.Parent = parent;
            var state = new ZoneState { Coord = coord, LastChangeUnscaledTime = now, ConstructedUnscaledTime = now, ZdoBaked = true };
            _zones.Add(coord, state);
            state.Bake = result;
            state.Baked = true;
            state.FromCache = true; Indirect.EbmIndirect.RequestFullWalk();   // FromCache: the bake landed before vanilla reached the pieces, as for a cache hit
            AnnounceWhenReady(state);   // EbmApi.ZoneBaked
            if (result.HasRenderableContent) ZoneKeepalive.MarkActive(coord);
            QueueStandIns(state);
            state.CacheStale = true;   // 1.2.374: the bake pass offers it to SaveZone, which notes it for the background full file (ZdoFileWanted)
            if (!s_zdoFileRefused.Contains(coord)) s_zdoFileWanted.Add(coord);   // 1.2.378: wanted from the attach on, not only after the bake pass's 35 s settle (an edge zone is in reach for less)
            scan.State = state;
            scan.AttachedBy = via;
            scan.AttachedAtSlice = scan.Slices;
            s_zonesFromZdo++;
            s_zdoVia[(int)via]++;
            if (s_zdoBakeWait.TryGetValue(coord, out var wait)) { s_zdoAfterWait++; scan.EarlierScans = wait.Scans; s_zdoBakeWait.Remove(coord); }
            if (!s_saidZdoBake) { s_saidZdoBake = true; EasyBakeLog.Info("[Cache] ZdoFirstBake on: a zone with no saved bake is built from its ZDOs on the first visit; pieces it cannot draw stay live, and its full cache file is built from its ZDOs in the background while you are in the area"); }   // 1.2.387: text only (was "prototype ... no cache file is written", false since 1.2.374)
            return state;
        }

        // The scan's end: classifies what is left, attaches the zone if it is not attached yet, applies the low-count rules to the keys
        // still held, logs the zone's line, and takes the scan out of the pump's lists. Null = nothing to instance (a wait) or a fallback.
        private static ZoneState FinishZdoScan(ZdoScan scan, ZdoVia via)
        {
            var coord = scan.Coord;
            if (s_zdoScans.Remove(coord)) s_zdoScanOrder.Remove(scan);
            long t0 = Stopwatch.GetTimestamp();
            int restAtFinish = scan.Zdos.Count - scan.Next;
            while (!scan.Done && scan.Fallback == null) ClassifyZdo(scan);
            if (scan.Fallback != null)
            {
                scan.Ticks += Stopwatch.GetTimestamp() - t0; s_zdoBakeTicks += scan.Ticks;
                if (scan.State == null) return ZdoBakeDecline(coord, scan.Fallback);
                // Already attached: what is drawn stays drawn (every piece in it passed the tests); the rest of the zone stays live.
                EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): ZdoFirstBake stopped filling at ZDO {scan.Next} of {scan.Zdos.Count}: {scan.Fallback}; the {scan.Baked} piece(s) already drawn stay, the rest stay live");
                s_zdoBakeDeclined.Add(coord); s_zdoBakeFallbacks++;
                return scan.State;
            }
            s_zdoBakeWait.TryGetValue(coord, out var wait);
            ZoneState state = scan.State;
            if (state == null) state = AttachZdoScan(scan, via);
            else
            {
                // Keys still held at the end: below the minimum, or only drawable by instancing (the bake's low-count rules).
                int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
                foreach (var entry in scan.ByKey)
                {
                    var bucket = entry.Value;
                    if (bucket.Group != null || bucket.Held.Count == 0) continue;
                    if (bucket.Count >= minInstances || ZdoOnlyInstancingCanDraw(entry.Key.PrefabHash, entry.Key.Look, bucket.Held))
                    {
                        if (!InstanceDefinitionCache.TryGet(entry.Key, out var definition)) continue;
                        bool damageableOnly = bucket.Count < minInstances && ZdoLowCountDamageableOnly(entry.Key.PrefabHash, entry.Key.Look);
                        if (damageableOnly) bucket.Held.RemoveAll(p => p.Invulnerable && ++scan.LeftLive[6] > 0);
                        if (bucket.Held.Count > 0) OpenScanGroup(scan, entry.Key, bucket, definition);
                    }
                }
            }
            // Whatever is still held never got a group: below the minimum, stays live.
            foreach (var bucket in scan.ByKey.Values) { scan.LeftLive[6] += bucket.Held.Count; bucket.Held.Clear(); }

            long finishTicks = Stopwatch.GetTimestamp() - t0;
            scan.Ticks += finishTicks;
            s_zdoBakeTicks += scan.Ticks;
            s_zdoGoneDuringScan += scan.Gone;
            s_zdoCreatedBeforeSlice += scan.LeftLive[7];
            double ms = scan.Ticks * 1000.0 / Stopwatch.Frequency;
            int leftLive = 0;
            for (int i = 0; i < scan.LeftLive.Length; i++) { leftLive += scan.LeftLive[i]; s_zdoLeftLiveTotal[i] += scan.LeftLive[i]; }
            string sliced = scan.Slices > 0 || scan.Early
                ? $" (sliced: {scan.Slices} slice(s) over {Time.frameCount - scan.StartFrame + 1} frame(s), worst slice {scan.WorstSliceTicks * 1000.0 / Stopwatch.Frequency:F1} ms, finish {finishTicks * 1000.0 / Stopwatch.Frequency:F1} ms with {restAtFinish} ZDO(s) left{(scan.Early ? $"; attached EARLY at slice {scan.AttachedAtSlice} by {ZdoViaNames[(int)scan.AttachedBy]}" : "")})"
                : "";
            string firstSight = $"; first sight: eligibility {scan.FirstSightEligibility * 1000.0 / Stopwatch.Frequency:F1} ms ({scan.FirstSightEligibilityN} prefab(s)), prefab walk {scan.FirstSightPrefab * 1000.0 / Stopwatch.Frequency:F1} ms ({scan.FirstSightPrefabN}), definition lookups {scan.Definitions * 1000.0 / Stopwatch.Frequency:F1} ms";
            if (state == null)
            {
                ZdoBakeWait(coord, ref wait, scan.SectorCount, scan.Now0, via, $"nothing to instance yet in {scan.Seen} ZDO(s): left live {leftLive} ({DescribeLeftLive(scan.LeftLive)}), {scan.Others} other object(s); {ms:F1} ms{sliced}{firstSight}");   // 1.2.370 lever (1): was a decline for the session
                return null;
            }
            s_zdoBakedPieces += scan.Baked;
            EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): zdo-baked {scan.Baked} piece(s) in {state.Bake.InstanceGroups.Count} group(s) ({scan.Skippable} may skip creation), left live {leftLive} ({DescribeLeftLive(scan.LeftLive)}), {scan.Others} other object(s){(scan.JoinedBefore > 0 ? $", {scan.JoinedBefore} late-joined before their slice" : "")}{(scan.Gone > 0 ? $", {scan.Gone} destroyed during the scan" : "")}; from {scan.Seen} ZDO(s) in {ms:F1} ms{sliced}{firstSight}; via {ZdoViaNames[(int)(scan.State != null ? scan.AttachedBy : via)]}{(scan.EarlierScans > 0 ? $", after {scan.EarlierScans} earlier scan(s) found nothing" : "")}; no cache file written");
            if (s_zdoWalkDue) { s_zdoWalkDue = false; Indirect.EbmIndirect.RequestFullWalk(); }
            return state;
        }

        // 1.2.374 THE FULL FILE FROM ZDOs (Fire 11-14: "Use the one method to load the zone faster and then while were in the area it properly bakes in the
        // background so the cache is correct"; design Tools\DESIGN_ZDO_full_bake_20261114.md, Z1-Z5; [visual] owns ZdoCombine + MeshCacheStore.SaveZdoBuilt).
        // It replaces 1.2.373's groups-only save (key and code removed). With [Cache] ZdoFirstBake on:
        //  - SaveZone never writes a ZDO-baked zone from its live state (that state has no combined batches and misses late pieces); it only notes the
        //    zone as WANTED (ZdoFileWanted), at the attach and again whenever a late join marks it CacheStale (Z4);
        //  - once the zone's sector is quiet (SectorJudgeable) and no scan fills it, ONE job at a time re-collects the zone's ZDOs and classifies them all
        //    again under a frame budget (Z1: a fresh, complete classification; this visit's live state is not read and not changed);
        //  - the partition is the live bake's (Z2): keys with a definition and MinInstancesPerPrefab pieces (or only drawable by instancing) -> instanced
        //    groups; the invulnerable rest -> ZdoCombine, all of the zone's candidates in one call (its covering count is zone-level); damageable pieces are
        //    never merged; 1.2.378: a bake-safe prefab that is not pure structure ("runtime component") is taken as the live bake takes it (created and silenced on every visit);
        //  - contributors + transforms (Z3): every grouped piece (a transform where it may skip), every CoveredId and DrawsNothingId with its transform;
        //  - SELF-CHECK before writing (an invisible building is a hard fail): every identity ZdoCombine reports as covered / drawing nothing was one of
        //    its inputs, no partial piece is a contributor, covered pieces come with batches, every group has one identity per matrix. Any miss = no file,
        //    one Warning, the zone is not asked again this session;
        //  - the sector count must not have moved between the snapshot and the save (else the job starts over later); a cache file this session did not
        //    write from ZDOs is never replaced ([2ic]);
        //  - MeshCacheStore.SaveZdoBuilt writes the file and refreshes the preload copy; the next visit loads it like any saved zone (NeedsCacheValidation).
        private sealed class ZdoFileJob
        {
            public ZoneState State;
            public ZdoScan Scan;
            public bool Partitioned;
            public readonly List<MeshBaker.PieceIdentity> CoveredAll = new List<MeshBaker.PieceIdentity>();   // covered + draws-nothing, for the this-visit swap
            public MeshBaker.BakeResult VerifyAgainst;   // set = the side-by-side verifier's job: compares with this live bake instead of saving
            public ZdoCombine.Request Request;
            public readonly List<MeshCacheStore.CachedInstanceGroupData> Groups = new List<MeshCacheStore.CachedInstanceGroupData>();
            public readonly HashSet<MeshBaker.PieceIdentity> Contributors = new HashSet<MeshBaker.PieceIdentity>();
            public readonly Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> Transforms = new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>();
            public readonly Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> CombineTransforms = new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>();
            public int GroupPieces, CombinePieces, Slices, StartFrame, PieceCount;
            public HashSet<MeshBaker.PieceIdentity> VerifySeen;   // 1.2.377: the identities the live bake had in front of it (verifier jobs)
            public Dictionary<MeshBaker.PieceIdentity, ZDO> VerifyZdos;
            public long Ticks;
        }
        private static readonly HashSet<Vector2s> s_zdoFileWanted = new HashSet<Vector2s>();
        private static readonly HashSet<Vector2s> s_zdoFileRefused = new HashSet<Vector2s>();   // no file from ZDOs this session (existing file, fallback prefab, failed self-check)
        private static readonly HashSet<Vector2s> s_zdoFilesWritten = new HashSet<Vector2s>();   // zones whose file this session wrote from ZDOs (a re-save may replace it)
        private static readonly List<Vector2s> s_zdoFileScratch = new List<Vector2s>();
        private static readonly Dictionary<Vector2s, int> s_zdoFileSavedCount = new Dictionary<Vector2s, int>();   // 1.2.379: the piece count each written file was built from
        private static ZdoFileJob s_zdoFileJob;
        private static float s_zdoFileNextPick;
        private static int s_zdoFilesSaved, s_zdoFileRefusedExisting, s_zdoFileRestarts, s_zdoFileSelfCheckFails, s_zdoFileOtherFails;
        private const float ZdoFileBudgetFloorMs = 1f;   // the background job always slices, also when ZdoFirstBakeBudgetMs is 0 (that key is the first-visit scan's)

        // 1.2.377 (end-to-end leg 1322_2P: "17 written, 13 wanted, ... 10 restart(s)"; the second visit then ZDO-baked 12 zones again, (-16,11) 5505 pieces among them):
        //  - the job asked SectorJudgeable, whose "player within ONE zone" is the remote-validation rule; a zone the player passes two or three zones away was never judged.
        //    The server delivers every sector of the near (active) area in full, so any zone inside it whose arrivals have been quiet CacheZdoQuietSeconds may be built;
        //  - "starts over later (its sector's ZDO count moved)": the raw sector count holds creatures and drops and moves all the time. What must not move is the PIECES.
        private static bool ZdoFileJudgeable(ZoneState state, float now)
        {
            var net = ZNet.instance;
            if (net == null) return false;
            int radius = net.GetSyncedSimulationDistance().NearSimulationDistance;
            var delta = EbmZones.Delta(EbmZones.Of(net.GetReferencePosition()), state.Coord);
            if (Mathf.Abs(delta.x) > radius || Mathf.Abs(delta.y) > radius) return false;
            return true;   // 1.2.378: in reach; "quiet" is the zone's PIECE count standing still (ZdoFileQuiet), not the sector's arrivals
        }

        // 1.2.378 (end-to-end leg 1356_2P on 1.2.377: "25 written, 7 wanted", no save after log line 2876 of 3797 although the laps went on; the seven are the circle's edge
        // zones (-16,8..13) and (-19,14) with 379 non-piece objects): SectorArrivals.LastArrival moves with ANY new ZDO (creatures, drops, a sector re-delivered on every lap),
        // so an edge zone was never 30 s quiet while in reach. What the file needs is that its PIECES stopped arriving: the zone's piece-ZDO count, sampled once a second while
        // the zone waits, unchanged for ZdoFileQuietSeconds. The job itself still starts over if a piece arrives or leaves while it runs, and a late join re-asks for the file.
        private struct ZdoQuiet { public int Count; public float Since; }
        private static readonly Dictionary<Vector2s, ZdoQuiet> s_zdoFileQuiet = new Dictionary<Vector2s, ZdoQuiet>();
        private const float ZdoFileQuietSeconds = 15f;
        private static bool ZdoFileQuiet(Vector2s coord, float now)
        {
            int pieces = ZdoPieceCountNow(coord);
            if (!s_zdoFileQuiet.TryGetValue(coord, out var q) || q.Count != pieces) { s_zdoFileQuiet[coord] = new ZdoQuiet { Count = pieces, Since = now }; return false; }
            return now - q.Since >= ZdoFileQuietSeconds;
        }

        // The zone's building-piece ZDOs right now (a WearNTear prefab; an unknown prefab counts, it would be a fallback). One walk of the sector's list, no classification.
        private static readonly List<ZDO> s_zdoCountScratch = new List<ZDO>();
        private static int ZdoPieceCountNow(Vector2s coord)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return -1;
            s_zdoCountScratch.Clear();
            zdoMan.FindSectorObjects(coord, new SimulationDistance(0, 0), s_zdoCountScratch);
            int n = 0;
            for (int i = 0; i < s_zdoCountScratch.Count; i++)
            {
                var zdo = s_zdoCountScratch[i];
                if (zdo == null || !zdo.IsValid()) continue;
                int hash = zdo.GetPrefab();
                if (!s_zdoBakePrefabs.TryGetValue(hash, out var kind))
                {
                    var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(hash);
                    if (prefab == null) { n++; continue; }
                    kind = ZdoBakePrefabOf(hash, prefab, SkipEligibility.Get(hash));
                }
                if (kind.Piece) n++;
            }
            s_zdoCountScratch.Clear();
            return n;
        }

        // SaveZone's answer for a ZDO-baked zone: never the plain save; the background job is asked for (again).
        private static void ZdoFileWanted(ZoneState state)
        {
            if (s_zdoFileRefused.Contains(state.Coord)) { SaveOutcome(state, "ZDO-baked: no cache file from ZDOs for this zone this session"); return; }
            // 1.2.379 ([visual]; leg 1535_2P: "42 written" for 28 zones, (-18,10) saved three times with the same content): every late join marks the zone CacheStale and came
            // back here, but the file is built from ALL the zone's ZDOs, so it is current as long as the zone's piece count is the one it was built from.
            if (s_zdoFileSavedCount.TryGetValue(state.Coord, out int savedPieces) && savedPieces == ZdoPieceCountNow(state.Coord)) { SaveOutcome(state, "ZDO-baked: its full cache file is current"); return; }
            s_zdoFileWanted.Add(state.Coord);
            SaveOutcome(state, "ZDO-baked: its full cache file is built from ZDOs in the background once its sector is quiet");
        }

        private static void ZdoFileRefuse(ZoneState state, string why, bool warn)
        {
            s_zdoFileWanted.Remove(state.Coord);
            s_zdoFileRefused.Add(state.Coord);
            string line = $"[Cache] zone ({state.Coord.x},{state.Coord.y}): no cache file from ZDOs: {why}; the zone keeps drawing from its ZDO bake and is not asked again this session";
            if (warn) EasyBakeLog.Warn(line); else EasyBakeLog.Debug(line);
        }

        // Once a frame (PumpZdoScans): works the one job, or picks the next wanted zone that is ready (at most one look a second).
        private static void PumpZdoFiles()
        {
            PumpZdoSwap();   // Phase 2
            if (s_zdoFileJob != null) { StepZdoFileJob(s_zdoFileJob); return; }
            float now = Time.unscaledTime;
            if (ZDOMan.instance == null) return;
            if (s_zdoVerifyQueue.Count > 0 && now >= s_zdoVerifyNext && Player.m_localPlayer != null)
            {
                s_zdoVerifyNext = now + 3f;
                var vcoord = s_zdoVerifyQueue.Dequeue();
                if (_zones.TryGetValue(vcoord, out var vstate) && vstate.Baked && !vstate.ZdoBaked && !vstate.Dirty && vstate.Bake != null && vstate.Bake.ContributorIdentities != null) { s_zdoFileJob = StartZdoFileJob(vstate, vstate.Bake, now); s_zdoVerifySeen.TryGetValue(vcoord, out s_zdoFileJob.VerifySeen); s_zdoVerifySeen.Remove(vcoord); return; }
                s_zdoVerifySeen.Remove(vcoord);
                s_zdoVerifySkipped++;
            }
            if (s_zdoFileWanted.Count == 0 || now < s_zdoFileNextPick) return;
            s_zdoFileNextPick = now + 1f;
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value || !MeshCacheStore.DiskListingDone || MeshCacheStore.TryGetWorldUid() == 0L) return;
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return;
            s_zdoFileScratch.Clear();
            s_zdoFileScratch.AddRange(s_zdoFileWanted);
            for (int i = 0; i < s_zdoFileScratch.Count; i++)
            {
                var coord = s_zdoFileScratch[i];
                if (!_zones.TryGetValue(coord, out var state) || !state.ZdoBaked) { s_zdoFileWanted.Remove(coord); continue; }   // torn down, or baked fresh from live pieces (today's save)
                if (state.Dirty || !state.ZoneActive || s_zdoScans.ContainsKey(coord) || !ZdoFileJudgeable(state, now) || !ZdoFileQuiet(coord, now)) continue;   // not yet (in reach, then its piece count standing still)
                if (MeshCacheStore.HasDiskFile(coord) && !s_zdoFilesWritten.Contains(coord))
                {
                    s_zdoFileRefusedExisting++;
                    ZdoFileRefuse(state, "a cache file this session did not write already exists for it (never replaced)", false);
                    continue;
                }
                s_zdoFileJob = StartZdoFileJob(state, null, now);
                return;
            }
        }

        private static void StepZdoFileJob(ZdoFileJob job)
        {
            var state = job.State;
            var coord = state.Coord;
            if (!_zones.TryGetValue(coord, out var current) || current != state || state.Dirty || (job.VerifyAgainst != null ? state.Bake != job.VerifyAgainst : !state.ZdoBaked))
            {
                s_zdoFileJob = null;   // the zone went away or to today's path; a wanted zone that comes back is picked again
                EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): background file job dropped (the zone was torn down or changed)");
                return;
            }
            long t0 = Stopwatch.GetTimestamp();
            if (!job.Partitioned)
            {
                long budgetTicks = (long)(Mathf.Max(ZdoSliceBudgetMs(), ZdoFileBudgetFloorMs * PerformanceProfile.Budget) * Stopwatch.Frequency / 1000.0);
                var scan = job.Scan;
                bool any = false;
                while (!scan.Done && scan.Fallback == null && (!any || Stopwatch.GetTimestamp() - t0 < budgetTicks)) { ClassifyZdo(scan); any = true; }
                job.Slices++;
                if (scan.Fallback != null) { job.Ticks += Stopwatch.GetTimestamp() - t0; s_zdoFileJob = null; ZdoFileFail(job, scan.Fallback, false); return; }
                if (!scan.Done) { job.Ticks += Stopwatch.GetTimestamp() - t0; return; }
                var inputs = PartitionZdoFile(job);
                job.PieceCount = scan.Seen - scan.Others;   // 1.2.377: the building-piece ZDOs this file describes
                job.Partitioned = true;
                if (inputs.Count > 0) job.Request = ZdoCombine.Start(coord, inputs);   // worker; polled below
                job.Ticks += Stopwatch.GetTimestamp() - t0;
                return;
            }
            if (job.Request != null && !job.Request.Done) return;
            s_zdoFileJob = null;
            FinishZdoFile(job);
            job.Ticks += Stopwatch.GetTimestamp() - t0;
        }

        // Z2: the live bake's partition, from the complete classification. Fills the job's groups / contributors / transforms for the instanced
        // side and returns ZdoCombine's inputs (every invulnerable combine candidate of the zone, one Input per prefab + look).
        private static List<ZdoCombine.Input> PartitionZdoFile(ZdoFileJob job)
        {
            var scan = job.Scan;
            var inputs = new List<ZdoCombine.Input>();
            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
            foreach (var entry in scan.ByKey)
            {
                var pieces = entry.Value.Held;
                if (pieces.Count == 0) continue;
                bool lowCount = pieces.Count < minInstances;
                bool instanced = !lowCount || ZdoOnlyInstancingCanDraw(entry.Key.PrefabHash, entry.Key.Look, pieces);
                bool damageableOnly = instanced && lowCount && ZdoLowCountDamageableOnly(entry.Key.PrefabHash, entry.Key.Look);   // as LowCountDamageableOnly: invulnerable siblings go to the combine
                InstanceDefinition definition = null;
                if (instanced && !InstanceDefinitionCache.TryGet(entry.Key, out definition)) instanced = false;
                List<Matrix4x4> matrices = null; List<MeshBaker.PieceIdentity> ids = null;   // the group's
                ZdoCombine.Input input = null;
                for (int i = 0; i < pieces.Count; i++)
                {
                    var piece = pieces[i];
                    if (instanced && !(damageableOnly && piece.Invulnerable))
                    {
                        if (matrices == null) { matrices = new List<Matrix4x4>(pieces.Count); ids = new List<MeshBaker.PieceIdentity>(pieces.Count); }
                        matrices.Add(Matrix4x4.TRS(piece.Transform.Position, piece.Transform.Rotation, piece.Transform.Scale) * definition.LocalOffset);   // as ZoneInstanceGroup.Add
                        ids.Add(piece.Identity);
                        job.Contributors.Add(piece.Identity);
                        if (piece.Invulnerable || SkipEligibility.DamageableSkipAllowed) job.Transforms[piece.Identity] = piece.Transform;   // MeshBaker.cs:457's rule
                        job.GroupPieces++;
                        continue;
                    }
                    if (!piece.Invulnerable) continue;   // a damageable piece is never merged (MeshBaker.cs:453-455): it stays live
                    AddCombinePiece(job, inputs, ref input, entry.Key, piece);
                }
                if (matrices != null) job.Groups.Add(GroupData(entry.Key, matrices, ids));
            }
            foreach (var entry in scan.Combine)   // invulnerable pieces of keys with no instance definition
            {
                ZdoCombine.Input input = null;
                for (int i = 0; i < entry.Value.Count; i++) AddCombinePiece(job, inputs, ref input, entry.Key, entry.Value[i]);
            }
            return inputs;
        }

        private static void AddCombinePiece(ZdoFileJob job, List<ZdoCombine.Input> inputs, ref ZdoCombine.Input input, InstanceKey key, ZdoBakePiece piece)
        {
            if (input == null)
            {
                input = new ZdoCombine.Input { PrefabHash = key.PrefabHash, Look = key.Look, Matrices = new List<Matrix4x4>(), Ids = new List<MeshBaker.PieceIdentity>() };
                inputs.Add(input);
            }
            input.Matrices.Add(Matrix4x4.TRS(piece.Transform.Position, piece.Transform.Rotation, piece.Transform.Scale));   // the piece's own matrix, no LocalOffset
            input.Ids.Add(piece.Identity);
            job.CombineTransforms[piece.Identity] = piece.Transform;
            job.CombinePieces++;
        }

        // As MeshCacheStore.ExtractInstanceGroupDataFromLive, from the lists.
        private static MeshCacheStore.CachedInstanceGroupData GroupData(InstanceKey key, List<Matrix4x4> matrices, List<MeshBaker.PieceIdentity> ids)
        {
            int count = matrices.Count;
            var x = new int[count]; var y = new int[count]; var z = new int[count]; var r = new int[count];
            for (int m = 0; m < count; m++) { x[m] = ids[m].X; y[m] = ids[m].Y; z[m] = ids[m].Z; r[m] = ids[m].R; }
            return new MeshCacheStore.CachedInstanceGroupData { PrefabHash = key.PrefabHash, Look = key.Look.Bits, Matrices = matrices.ToArray(), PositionsX = x, PositionsY = y, PositionsZ = z, PositionsR = r };
        }

        private static void FinishZdoFile(ZdoFileJob job)
        {
            var state = job.State;
            var coord = state.Coord;
            var scan = job.Scan;
            var req = job.Request;
            if (req != null && req.Error != null) { s_zdoFileOtherFails++; ZdoFileFail(job, "the combine reported: " + req.Error, true); return; }
            // The zone must still be what was classified: a ZDO arrived or left since the snapshot = start over later (it stays wanted).
            if (ZdoPieceCountNow(coord) != job.PieceCount || s_zdoScans.ContainsKey(coord))
            {
                if (job.VerifyAgainst != null) { ZdoFileFail(job, "a piece ZDO arrived or left while it was built", false); return; }
                s_zdoFileRestarts++;
                EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): background file job starts over later (a piece ZDO arrived or left while it was built)");
                return;
            }

            // ── SELF-CHECK: nothing is written unless every contributor is provably drawn by the file ──
            string fail = null;
            int covered = 0, drawsNothing = 0, partial = req != null && req.PartialIds != null ? req.PartialIds.Count : 0;
            if (req != null)
            {
                if (req.CoveredIds != null)
                    for (int i = 0; i < req.CoveredIds.Count && fail == null; i++)
                    {
                        var id = req.CoveredIds[i];
                        if (!job.CombineTransforms.TryGetValue(id, out var t)) { fail = $"a covered identity was not one of the combine's inputs (prefab {id.PrefabHash})"; break; }
                        job.Contributors.Add(id); job.Transforms[id] = t; job.CoveredAll.Add(id); covered++;   // MeshBaker.cs:415-422: every combined contributor has a transform
                    }
                if (fail == null && req.DrawsNothingIds != null)
                    for (int i = 0; i < req.DrawsNothingIds.Count && fail == null; i++)
                    {
                        var id = req.DrawsNothingIds[i];
                        if (!job.CombineTransforms.TryGetValue(id, out var t)) { fail = $"a draws-nothing identity was not one of the combine's inputs (prefab {id.PrefabHash})"; break; }
                        job.Contributors.Add(id); job.Transforms[id] = t; job.CoveredAll.Add(id); drawsNothing++;
                    }
                if (fail == null && req.PartialIds != null)
                    for (int i = 0; i < req.PartialIds.Count && fail == null; i++)
                        if (job.Contributors.Contains(req.PartialIds[i])) fail = $"a partly covered piece is also a contributor (prefab {req.PartialIds[i].PrefabHash})";
                if (fail == null && covered > 0 && (req.Near == null || req.Near.Count == 0)) fail = $"{covered} piece(s) reported covered but no combined batch came back";
                if (fail == null && covered + drawsNothing + partial != job.CombinePieces) fail = $"the combine answered for {covered + drawsNothing + partial} piece(s) of the {job.CombinePieces} it was given";
            }
            for (int g = 0; g < job.Groups.Count && fail == null; g++)
            {
                var group = job.Groups[g];
                if (group.Matrices.Length == 0 || group.Matrices.Length != group.PositionsX.Length) fail = $"a group of prefab {group.PrefabHash} has {group.Matrices.Length} matrices for {group.PositionsX.Length} identities";
            }
            if (fail != null) { s_zdoFileSelfCheckFails++; ZdoFileFail(job, "SELF-CHECK FAILED, nothing written: " + fail, true); return; }
            EasyBakeLog.InfoFirst("ZDO full file self-check passed", $"[Cache] zone ({coord.x},{coord.y}): ZDO full file self-check passed ({job.Contributors.Count} contributors, {(req != null && req.Near != null ? req.Near.Count : 0)} batches, {job.Groups.Count} groups{(job.VerifyAgainst != null ? "; verifier job, nothing written" : "")})");   // [lead]: the gate reads a pass (Info once, then Debug); a fail Warns
            if (job.VerifyAgainst != null) { CompareZdoFile(job, req != null && req.Near != null ? req.Near.Count : 0); return; }   // the verifier's job never saves
            if (job.Contributors.Count == 0) { ZdoFileRefuse(state, "nothing to save (no group and no covered piece)", false); return; }

            long uid = MeshCacheStore.TryGetWorldUid();
            if (uid == 0L) return;   // stays wanted
            var prints = new MeshBaker.BakeResult { ContributorIdentities = job.Contributors };   // ZonePrints reads the prefab hashes of the contributors (the grouped pieces are among them)
            var data = new MeshCacheStore.CachedZoneData
            {
                Coord = coord,
                BakeSettings = MeshCacheStore.CurrentBakeSettingsKey(),
                PrefabPrints = MeshCacheStore.ZonePrints(prints),
                ContributorIdentities = job.Contributors,
                PieceTransforms = job.Transforms,
                Batches = req != null && req.Near != null ? req.Near : new List<MeshCacheStore.CachedBatchData>(),
                FarBatches = req != null && req.Far != null ? req.Far : new List<MeshCacheStore.CachedBatchData>(),
                InstanceGroups = job.Groups,
            };
            if (!MeshCacheStore.SaveZdoBuilt(uid, coord, data)) { s_zdoFileOtherFails++; ZdoFileRefuse(state, "the save failed", true); return; }
            s_zdoFileWanted.Remove(coord); s_zdoFileQuiet.Remove(coord);
            s_zdoFilesWritten.Add(coord); s_zdoFileSavedCount[coord] = job.PieceCount;
            s_zdoFilesSaved++;
            QueueZdoSwap(job, data);   // Phase 2: the same batches attached to this visit's zone
            int leftLive = 0;
            for (int i = 0; i < scan.LeftLive.Length; i++) leftLive += scan.LeftLive[i];
            SaveOutcome(state, $"saved from ZDOs ({data.Batches.Count} batch(es), {job.Groups.Count} instance group(s))");
            EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): full cache file built from ZDOs: {job.Groups.Count} group(s) with {job.GroupPieces} piece(s); combine got {job.CombinePieces} piece(s) -> {data.Batches.Count} near + {data.FarBatches.Count} far batch(es), {covered} covered, {drawsNothing} drawing nothing, {partial} partial (live); {job.Contributors.Count} contributor(s), {job.Transforms.Count} transform(s); not in the file (created live on later visits): {leftLive} ({DescribeLeftLive(scan.LeftLive)}) and the damageable pieces of keys below the minimum; from {scan.Seen} ZDO(s), {job.Slices} slice(s) over {Time.frameCount - job.StartFrame + 1} frame(s), {job.Ticks * 1000.0 / Stopwatch.Frequency:F1} ms main thread; self-check passed");
        }

        private static int CountCombineNoDefinition(ZdoScan scan)
        {
            int n = 0;
            foreach (var list in scan.Combine.Values) n += list.Count;
            return n;
        }

        // 1.2.374 PHASE 2, THE THIS-VISIT SWAP (Fire: "while were in the area it properly bakes in the background"; mechanics by [visual]: MeshCacheStore.BuildLiveBatch,
        // MeshBaker.QueueBatchSwap / CancelBatchSwap). Once a zone's full file is saved, its combined batches are also attached to THIS session's zone:
        //  1. the batches are built from the file's arrays, a few per frame, under an inactive holder object (nothing drawn yet);
        //  2. when all exist and the zone is still what the file describes (same state, not Dirty, same sector ZDO count), the covered / draws-nothing
        //     identities become contributors with their transforms FIRST (so a covered piece created from now on is silenced or skipped on arrival), then
        //     the covered pieces that are live now are handed to QueueBatchSwap, which silences them under the suppress budget and shows the holder in
        //     the frame the last one is silenced (a piece is invisible for at most those few frames, as at a fresh bake; never drawn twice);
        //  3. on done the batches join state.Bake.Batches / FarBatches and the silenced invulnerable pieces go to the convert queue, as on a cache hit.
        // The instance groups are left as they are (a piece outside them keeps drawing itself). ZdoBaked stays true on purpose: a later save must go through
        // the rescan path (ZdoFileWanted), never the plain SaveZone, whose file would hold only this visit's groups. Anything that invalidates the swap
        // before it is done (teardown, Dirty, a ZDO arriving or leaving, an unknown material) cancels it: holder destroyed, added contributors taken back;
        // a late join then marks the zone wanted again, and the next file brings the next swap.
        private sealed class ZdoSwap
        {
            public ZoneState State;
            public MeshCacheStore.CachedZoneData Data;
            public List<MeshBaker.PieceIdentity> Covered;
            public Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> CoveredTransforms;
            public int PieceCount, Next, StartFrame;
            public GameObject Holder;
            public readonly List<MeshBaker.BatchInstance> Near = new List<MeshBaker.BatchInstance>();
            public readonly List<MeshBaker.BatchInstance> Far = new List<MeshBaker.BatchInstance>();
            public readonly List<MeshBaker.PieceIdentity> Added = new List<MeshBaker.PieceIdentity>();
            public readonly List<WearNTear> Live = new List<WearNTear>();
            public MeshBaker.BatchSwap Queued;
            public long Ticks;
            // 1.2.379: the file's instance groups, brought into this visit's zone with the batches
            public List<MeshCacheStore.CachedInstanceGroupData> Groups;
            public Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> GroupTransforms;
            public bool GroupsOnly;   // no batches to build: the file has none, or this visit's zone already has its batches (a re-save)
            public readonly List<ZdoGroupAdd> GroupAdds = new List<ZdoGroupAdd>();
        }
        private struct ZdoGroupAdd { public int PrefabHash; public byte Look; public Matrix4x4 Matrix; public MeshBaker.PieceIdentity Id; }
        private static readonly Queue<ZdoSwap> s_zdoSwapQueue = new Queue<ZdoSwap>();
        private static ZdoSwap s_zdoSwap;
        private static int s_zdoSwapsDone, s_zdoSwapsCancelled, s_zdoSwapPiecesSilenced;
        private const float ZdoSwapBuildBudgetMs = 1f;

        private static void QueueZdoSwap(ZdoFileJob job, MeshCacheStore.CachedZoneData data)
        {
            // 1.2.379 (first-visit leg 1535_2P: the hill zones were filed and swapped, e.g. (-16,12) "zdo-baked 761 piece(s) in 24 group(s)" at the attach vs "40 group(s) with 4368 piece(s)"
            // in its file, and the spire was still missing at range): the swap attached the file's BATCHES and left this visit's GROUPS as the first scan made them, so every piece
            // the file instances and the live zone did not (delivered after the scan with no group for its key; the pieces taken as the live bake takes them) stayed a real object
            // and went when the zone left the active area. The swap now brings the file's groups too; it also runs for a file without batches and, groups only, after a re-save.
            if (job.State.Bake == null) return;
            bool groupsOnly = data.Batches == null || data.Batches.Count == 0 || job.State.Bake.Batches.Count > 0;
            s_zdoSwapQueue.Enqueue(new ZdoSwap { State = job.State, Data = data, Covered = job.CoveredAll, CoveredTransforms = job.CombineTransforms, PieceCount = job.PieceCount, Groups = job.Groups, GroupTransforms = job.Transforms, GroupsOnly = groupsOnly });
        }

        private static bool ZdoSwapStillValid(ZdoSwap swap)
        {
            var state = swap.State;
            return _zones.TryGetValue(state.Coord, out var current) && current == state && state.ZdoBaked && !state.Dirty && state.ZoneActive
                   && state.Bake != null && state.Bake.Parent != null && (swap.GroupsOnly || state.Bake.Batches.Count == 0);   // 1.2.377: the piece count is checked once, before the contributors are added (PumpZdoSwap)
        }

        private static void CancelZdoSwap(ZdoSwap swap, string why, bool warn)
        {
            s_zdoSwap = null;
            s_zdoSwapsCancelled++;
            if (swap.Queued != null && !swap.Queued.Done) MeshBaker.CancelBatchSwap(swap.Queued);   // pieces already silenced come back through the bake's Restore
            var bake = swap.State.Bake;
            if (bake != null && bake.ContributorIdentities != null)
                for (int i = 0; i < swap.Added.Count; i++) { bake.ContributorIdentities.Remove(swap.Added[i]); bake.PieceTransforms.Remove(swap.Added[i]); DropStandIn(swap.State, swap.Added[i]); }
            for (int i = 0; i < swap.Near.Count; i++) if (swap.Near[i].Mesh != null) UnityEngine.Object.Destroy(swap.Near[i].Mesh);   // destroying the holder does not free meshes
            for (int i = 0; i < swap.Far.Count; i++) if (swap.Far[i].Mesh != null) UnityEngine.Object.Destroy(swap.Far[i].Mesh);
            if (swap.Holder != null) UnityEngine.Object.Destroy(swap.Holder);
            string line = $"[Cache] zone ({swap.State.Coord.x},{swap.State.Coord.y}): combined batches not attached this visit: {why} (the file is saved; the zone keeps drawing from its ZDO bake)";
            if (warn) EasyBakeLog.Warn(line); else EasyBakeLog.Debug(line);
        }

        // Once a frame (PumpZdoFiles).
        private static void PumpZdoSwap()
        {
            if (s_zdoSwap == null)
            {
                if (s_zdoSwapQueue.Count == 0) return;
                var next = s_zdoSwapQueue.Dequeue();
                if (!ZdoSwapStillValid(next)) { s_zdoSwapsCancelled++; return; }
                next.Holder = new GameObject($"[EasyBake/Zone_{next.State.Coord.x}_{next.State.Coord.y}/zdo-batches]");
                next.Holder.transform.SetParent(next.State.Bake.Parent.transform, false);
                next.Holder.SetActive(false);
                next.StartFrame = Time.frameCount;
                s_zdoSwap = next;
            }
            var swap = s_zdoSwap;
            if (swap.Queued != null) { if (!swap.Queued.Done && !ZdoSwapStillValidWhileQueued(swap)) CancelZdoSwap(swap, "the zone changed while its live pieces were being silenced", false); return; }   // waiting for OnZdoSwapDone
            if (!ZdoSwapStillValid(swap)) { CancelZdoSwap(swap, "the zone was torn down or changed while its batches were built", false); return; }
            long t0 = Stopwatch.GetTimestamp();
            long budgetTicks = (long)(ZdoSwapBuildBudgetMs * PerformanceProfile.Budget * Stopwatch.Frequency / 1000.0);
            int near = swap.Data.Batches != null ? swap.Data.Batches.Count : 0, far = swap.Data.FarBatches != null ? swap.Data.FarBatches.Count : 0;
            if (swap.GroupsOnly) { near = 0; far = 0; }   // 1.2.379
            bool any = false;
            while (swap.Next < near + far && (!any || Stopwatch.GetTimestamp() - t0 < budgetTicks))
            {
                bool farTier = swap.Next >= near;
                var b = farTier ? swap.Data.FarBatches[swap.Next - near] : swap.Data.Batches[swap.Next];
                if (!MeshCacheStore.BuildLiveBatch(swap.State.Coord, b, swap.Holder, farTier ? swap.Far : swap.Near, farTier))
                {
                    swap.Ticks += Stopwatch.GetTimestamp() - t0;
                    CancelZdoSwap(swap, $"batch {swap.Next} could not be built (material '{b.MaterialName}' not known)", true);
                    return;
                }
                swap.Next++;
                any = true;
            }
            if (swap.Next < near + far) { swap.Ticks += Stopwatch.GetTimestamp() - t0; return; }

            if (ZdoPieceCountNow(swap.State.Coord) != swap.PieceCount) { CancelZdoSwap(swap, "a piece ZDO arrived or left since its file was built (the next file brings the next swap)", false); return; }   // 1.2.377: pieces, not the raw sector count (creatures, drops)
            // Every batch exists (holder still inactive). Contributors first, then the live covered pieces are silenced and the holder shown.
            var bake = swap.State.Bake;
            var liveById = new Dictionary<MeshBaker.PieceIdentity, WearNTear>();
            foreach (var wnt in swap.State.Pieces)
            {
                if (wnt == null) continue;
                var zdo = MeshBaker.ZdoOf(wnt);
                if (zdo != null) liveById[MeshBaker.PieceIdentity.From(zdo)] = wnt;
            }
            var livePieces = new List<GameObject>();
            if (!swap.GroupsOnly)   // the covered pieces need this swap's batches; a groups-only swap has none
                for (int i = 0; i < swap.Covered.Count; i++)
                {
                    var id = swap.Covered[i];
                    if (liveById.TryGetValue(id, out var coveredLive)) { livePieces.Add(coveredLive.gameObject); swap.Live.Add(coveredLive); }
                    if (!bake.ContributorIdentities.Add(id)) continue;   // already a contributor (should not be: a covered piece is in no group)
                    bake.PieceTransforms[id] = swap.CoveredTransforms[id];
                    swap.Added.Add(id);
                }
            // 1.2.379: every instance of the file's groups that this visit's zone does not draw yet becomes a contributor now (so its piece is silenced or skipped from here on);
            // its matrix joins the zone's group in OnZdoSwapDone, in the frame its live piece (if one exists) has been silenced. A key with no instance definition stays live.
            if (swap.Groups != null)
                for (int g = 0; g < swap.Groups.Count; g++)
                {
                    var fileGroup = swap.Groups[g];
                    if (!InstanceDefinitionCache.TryGet(new InstanceKey(fileGroup.PrefabHash, new WearLook(fileGroup.Look)), out _)) continue;
                    for (int m = 0; m < fileGroup.Matrices.Length; m++)
                    {
                        var id = new MeshBaker.PieceIdentity { PrefabHash = fileGroup.PrefabHash, X = fileGroup.PositionsX[m], Y = fileGroup.PositionsY[m], Z = fileGroup.PositionsZ[m], R = fileGroup.PositionsR[m] };
                        if (!bake.ContributorIdentities.Add(id)) continue;   // this visit's zone draws it already (or its stacked twin: one identity)
                        swap.Added.Add(id);
                        bool hasTransform = swap.GroupTransforms != null && swap.GroupTransforms.TryGetValue(id, out var groupTransform);
                        if (hasTransform) bake.PieceTransforms[id] = swap.GroupTransforms[id];
                        swap.GroupAdds.Add(new ZdoGroupAdd { PrefabHash = fileGroup.PrefabHash, Look = fileGroup.Look, Matrix = fileGroup.Matrices[m], Id = id });
                        if (liveById.TryGetValue(id, out var groupLive)) { livePieces.Add(groupLive.gameObject); if (hasTransform) swap.Live.Add(groupLive); }
                    }
                }
            swap.Ticks += Stopwatch.GetTimestamp() - t0;
            swap.Queued = MeshBaker.QueueBatchSwap(bake, swap.Holder, livePieces, OnZdoSwapDone);
        }

        // While QueueBatchSwap works: the sector count is not re-checked (a late join in these few frames is handled by the contributor branch);
        // only a zone that went away or Dirty cancels.
        private static bool ZdoSwapStillValidWhileQueued(ZdoSwap swap)
        {
            var state = swap.State;
            return _zones.TryGetValue(state.Coord, out var current) && current == state && !state.Dirty && state.Bake != null && state.Bake.Parent != null;
        }

        private static void OnZdoSwapDone(MeshBaker.BatchSwap done)
        {
            var swap = s_zdoSwap;
            if (swap == null || swap.Queued != done) return;   // cancelled meanwhile
            // 1.2.380 ([visual]): while [Cache] ZdoFirstBake is off PumpZdoSwap does not run, so a swap already in MeshBaker's queue is not re-checked there; if its zone was torn
            // down or went Dirty in those frames, nothing is attached (holder destroyed, contributors taken back).
            if (!ZdoSwapStillValidWhileQueued(swap)) { CancelZdoSwap(swap, "its zone was torn down or changed while its live pieces were being silenced", false); return; }
            s_zdoSwap = null;
            var state = swap.State;
            state.Bake.Batches.AddRange(swap.Near);
            state.Bake.FarBatches.AddRange(swap.Far);
            // 1.2.379: the file's group instances this visit's zone lacked (their live pieces were silenced in the same swap; the matrices come from the file, offset included).
            int groupsOpened = 0;
            var touched = new HashSet<ZoneInstanceGroup>();
            for (int i = 0; i < swap.GroupAdds.Count; i++)
            {
                var add = swap.GroupAdds[i];
                var look = new WearLook(add.Look);
                var group = GroupFor(state, add.PrefabHash, look);
                if (group == null)
                {
                    if (!InstanceDefinitionCache.TryGet(new InstanceKey(add.PrefabHash, look), out var definition)) continue;   // checked when it was queued; cannot miss now
                    group = new ZoneInstanceGroup { PrefabHash = add.PrefabHash, Look = look, Definition = definition };
                    state.Bake.InstanceGroups.Add(group);
                    groupsOpened++;
                }
                group.AddCached(add.Matrix, add.Id);
                touched.Add(group);
            }
            foreach (var group in touched) { group.RecomputeBounds(); EbmSnowStore.GroupMembershipChanged(group); }
            if (groupsOpened > 0) Indirect.EbmIndirect.RequestFullWalk();
            // As OnInstanceCreated's cache-hit branch: a silenced piece the bake draws and that may skip creation is unloaded to its stand-in.
            if (SkipCreationActive() && !ShellPieces.On)
                for (int i = 0; i < swap.Live.Count; i++) if (swap.Live[i] != null) state.ConvertQueue.Enqueue(swap.Live[i]);
            state.LastChangeUnscaledTime = Time.unscaledTime;
            s_zdoSwapsDone++;
            s_zdoSwapPiecesSilenced += swap.Live.Count;
            EasyBakeLog.Debug($"[Cache] zone ({state.Coord.x},{state.Coord.y}): {(swap.GroupsOnly ? "groups brought to the file's state this visit (no batches to attach)" : "combined batches attached this visit")}: {swap.Near.Count} near + {swap.Far.Count} far batch(es), {swap.GroupAdds.Count} group instance(s) added in {touched.Count} group(s) ({groupsOpened} new), {swap.Added.Count} piece(s) became contributors, {swap.Live.Count} live piece(s) silenced and queued to unload; built over {Time.frameCount - swap.StartFrame + 1} frame(s), {swap.Ticks * 1000.0 / Stopwatch.Frequency:F1} ms main thread before the silencing");
        }

        // 1.2.374 SIDE-BY-SIDE VERIFIER ([lead]'s gate: 0 EXTRA strictly; MISSING only from the runtime-component class, each named by prefab with a count).
        // With [Optimize] ZdoBakeVerify on (the existing verifier key), every zone that just baked FRESH FROM LIVE PIECES is queued; the same background job
        // then builds what the full file from ZDOs would hold for that zone (classification, partition, ZdoCombine, self-check) and, instead of saving,
        // compares its contributors and transforms with the live bake's:
        //   EXTRA   = a contributor of the ZDO file the live bake does not have: the file would hide a piece nothing draws (an invisible building). HARD FAIL.
        //   MISSING = a contributor of the live bake the ZDO file does not have: created and drawn live on later visits (safe). Allowed only for prefabs
        //             that are not pure structure (runtime component) and for vegetation (not in the ZDO path); anything else is a HARD FAIL.
        // Run it with ZdoFirstBake OFF (so zones bake live) on a cold cache. One zone at a time, at most one every 3 s, never before the local player exists.
        private static readonly Queue<Vector2s> s_zdoVerifyQueue = new Queue<Vector2s>();
        private static readonly Dictionary<Vector2s, HashSet<MeshBaker.PieceIdentity>> s_zdoVerifySeen = new Dictionary<Vector2s, HashSet<MeshBaker.PieceIdentity>>();
        private static int s_zdoVerifyUnseen, s_zdoVerifyMissingChanged;
        private static float s_zdoVerifyNext;
        private static int s_zdoVerifyZones, s_zdoVerifyExtra, s_zdoVerifyMissingAllowed, s_zdoVerifyMissingHard, s_zdoVerifyTransformMismatch, s_zdoVerifySkipped, s_zdoVerifyParityNear, s_zdoVerifyParityFar;
        private static bool ZdoVerifyOn => FiresEasyBakeMeshesPlugin.ZdoBakeVerifyEnabled != null && FiresEasyBakeMeshesPlugin.ZdoBakeVerifyEnabled.Value;
        internal static bool ZdoVerifyPending => s_zdoVerifyQueue.Count > 0 || (s_zdoFileJob != null && s_zdoFileJob.VerifyAgainst != null);

        // Called after a fresh live bake (ZoneTracker's bake pass).
        private static void ZdoVerifyQueue(ZoneState state)
        {
            if (!ZdoVerifyOn || state == null || state.Bake == null || state.Bake.ContributorIdentities == null) return;
            if (Player.m_localPlayer == null || s_zdoVerifyQueue.Count >= 64) return;
            // 1.2.377 (leg 1315_1A, zone (-18,9): "baked fresh, from 1092 real piece(s)", later "847 real piece(s), 926 skipped"; EXTRA 281 were pieces created after the live bake):
            // the identities the live bake had in front of it, so the compare can tell "saw it and did not draw it" from "never saw it".
            var seen = new HashSet<MeshBaker.PieceIdentity>();
            foreach (var wnt in state.Pieces)
            {
                if (wnt == null) continue;
                var zdo = MeshBaker.ZdoOf(wnt);
                if (zdo != null) seen.Add(MeshBaker.PieceIdentity.From(zdo));
            }
            s_zdoVerifySeen[state.Coord] = seen;
            s_zdoVerifyQueue.Enqueue(state.Coord);
        }

        private static void ZdoFileFail(ZdoFileJob job, string why, bool warn)
        {
            if (job.VerifyAgainst == null) { ZdoFileRefuse(job.State, why, warn); return; }
            s_zdoVerifySkipped++;
            string line = $"[ZdoVerify] zone ({job.State.Coord.x},{job.State.Coord.y}): not compared: {why}";
            if (warn) EasyBakeLog.Warn(line); else EasyBakeLog.Debug(line);
        }

        private static ZdoFileJob StartZdoFileJob(ZoneState state, MeshBaker.BakeResult verifyAgainst, float now)
        {
            var job = new ZdoFileJob { State = state, StartFrame = Time.frameCount, VerifyAgainst = verifyAgainst };
            job.Scan = new ZdoScan { Coord = state.Coord, Via = ZdoVia.RangePass, FileOnly = true, SectorCount = SectorCount(state.Coord), Now0 = now, StartFrame = Time.frameCount };
            ZDOMan.instance.FindSectorObjects(state.Coord, new SimulationDistance(0, 0), job.Scan.Zdos);
            for (int z = 0; z < job.Scan.Zdos.Count; z++) job.Scan.Uids.Add(job.Scan.Zdos[z] != null ? job.Scan.Zdos[z].m_uid : ZDOID.None);
            return job;
        }

        private static void CompareZdoFile(ZdoFileJob job, int nearBatches)
        {
            var state = job.State;
            var coord = state.Coord;
            var live = job.VerifyAgainst;
            s_zdoVerifyZones++;
            int extra = 0, missingAllowed = 0, missingHard = 0, mismatch = 0, unseen = 0, missingChanged = 0;
            var changedBy = new Dictionary<int, int>(); var hardDetail = new StringBuilder();
            var extraBy = new Dictionary<int, int>(); var allowedBy = new Dictionary<int, int>(); var hardBy = new Dictionary<int, int>();
            foreach (var id in job.Contributors)
                if (!live.ContributorIdentities.Contains(id))
                {
                    if (job.VerifySeen != null && !job.VerifySeen.Contains(id)) { unseen++; continue; }   // created after the live bake: it never had the piece
                    extra++; extraBy.TryGetValue(id.PrefabHash, out int n); extraBy[id.PrefabHash] = n + 1;
                }
            // 1.2.375 (true parity for zones with allowed MISSING pieces): the allowed-MISSING pieces the live bake MERGED (a contributor in no live instance group, with its look).
            var instancedLive = new HashSet<MeshBaker.PieceIdentity>();
            for (int g = 0; g < live.InstanceGroups.Count; g++) instancedLive.UnionWith(live.InstanceGroups[g].Identities);
            var mergedLeft = new Dictionary<InstanceKey, int>();
            foreach (var id in live.ContributorIdentities)
            {
                if (job.Contributors.Contains(id)) continue;
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(id.PrefabHash);
                bool allowed = prefab == null || prefab.GetComponent<WearNTear>() == null || !SkipEligibility.Get(id.PrefabHash).Skippable;   // vegetation / not pure structure
                if (!allowed && ZdoChangedSince(job, id)) { missingChanged++; changedBy.TryGetValue(id.PrefabHash, out int cn); changedBy[id.PrefabHash] = cn + 1; continue; }   // the ZDO no longer says what the live bake drew
                if (!allowed && hardDetail.Length < 900) hardDetail.Append(ZdoDescribeMissing(job, id, live));
                var by = allowed ? allowedBy : hardBy;
                if (allowed) missingAllowed++; else missingHard++;
                by.TryGetValue(id.PrefabHash, out int n); by[id.PrefabHash] = n + 1;
                if (allowed && !instancedLive.Contains(id) && live.PieceTransforms != null && live.PieceTransforms.TryGetValue(id, out var merged))
                {
                    var leftKey = new InstanceKey(id.PrefabHash, merged.Look);
                    mergedLeft.TryGetValue(leftKey, out int have); mergedLeft[leftKey] = have + 1;
                }
            }
            foreach (var kv in job.Transforms)
            {
                if (live.PieceTransforms == null || !live.PieceTransforms.TryGetValue(kv.Key, out var theirs)) continue;
                if ((theirs.Position - kv.Value.Position).sqrMagnitude > 0.0001f || Quaternion.Angle(theirs.Rotation, kv.Value.Rotation) > 0.5f
                    || (theirs.Scale - kv.Value.Scale).sqrMagnitude > 0.0001f || theirs.Look != kv.Value.Look) mismatch++;
            }
            s_zdoVerifyExtra += extra; s_zdoVerifyMissingAllowed += missingAllowed; s_zdoVerifyMissingHard += missingHard; s_zdoVerifyTransformMismatch += mismatch;
            // [lead]'s file-parity gate ([visual]'s MeshCacheStore.BatchParity): the live bake's batches vs ZdoCombine's, by material key + renderer flags, vertex and index counts.
            // 1.2.375: where the live bake merged pieces the ZDO file leaves live, their share is taken off the live side first (the overload with leftLive), so
            // every zone gets a true same / DIFFERS. NEAR is the gate; a far-only difference is reported, not a fail ([visual]: the live bake's far tier can fall
            // back to near geometry when a far batch did not materialise).
            string parity = ""; bool nearDiffers = false;
            if (job.Request != null)
            {
                bool nearSame, farSame; string nearReport, farReport;
                if (mergedLeft.Count > 0)
                {
                    var leftLive = new List<ZdoCombine.LeftLive>(mergedLeft.Count);
                    foreach (var kv in mergedLeft) leftLive.Add(new ZdoCombine.LeftLive { PrefabHash = kv.Key.PrefabHash, Look = kv.Key.Look, Count = kv.Value });
                    nearSame = MeshCacheStore.BatchParity(live.Batches, job.Request.Near, leftLive, false, out nearReport);
                    farSame = MeshCacheStore.BatchParity(live.FarBatches, job.Request.Far, leftLive, true, out farReport);
                }
                else
                {
                    nearSame = MeshCacheStore.BatchParity(live.Batches, job.Request.Near, out nearReport);
                    farSame = MeshCacheStore.BatchParity(live.FarBatches, job.Request.Far, out farReport);
                }
                nearDiffers = !nearSame;
                if (nearDiffers) s_zdoVerifyParityNear++;
                if (!farSame) s_zdoVerifyParityFar++;
                parity = $"; batch parity near {(nearSame ? "same" : "DIFFERS")} ({nearReport}), far {(farSame ? "same" : "differs, not a gate")} ({farReport}){(mergedLeft.Count > 0 ? $" [live side less {mergedLeft.Count} left-live prefab look(s)]" : "")}";
            }
            s_zdoVerifyUnseen += unseen; s_zdoVerifyMissingChanged += missingChanged;
            bool comparable = unseen == 0 && missingChanged == 0;   // batch counts can only match when both sides hold the same pieces
            if (nearDiffers && !comparable) parity += " [not a gate here: the live bake held other pieces, see unseen / changed]";
            bool fail = extra > 0 || missingHard > 0 || mismatch > 0 || (nearDiffers && comparable);
            string line = $"[ZdoVerify] zone ({coord.x},{coord.y}): {(fail ? "FAIL" : "ok")}: live bake {live.ContributorIdentities.Count} contributor(s), {live.Batches.Count} batch(es), {live.InstanceGroups.Count} group(s); ZDO file {job.Contributors.Count} contributor(s), {nearBatches} batch(es), {job.Groups.Count} group(s); EXTRA {extra}{DescribeByPrefab(extraBy)}; MISSING outside the allowed class {missingHard}{DescribeByPrefab(hardBy)}{(hardDetail.Length > 0 ? " [" + hardDetail + " ]" : "")}; MISSING allowed (runtime component / vegetation) {missingAllowed}{DescribeByPrefab(allowedBy)}; MISSING because the ZDO changed since the live bake (worn / edited / gone) {missingChanged}{DescribeByPrefab(changedBy)}; contributors the live bake never saw (created after it) {unseen}; transform mismatches {mismatch}{parity}. So far: {s_zdoVerifyZones} zone(s), EXTRA {s_zdoVerifyExtra}, MISSING hard {s_zdoVerifyMissingHard}, MISSING allowed {s_zdoVerifyMissingAllowed}, MISSING changed {s_zdoVerifyMissingChanged}, never seen by the live bake {s_zdoVerifyUnseen}, transform mismatches {s_zdoVerifyTransformMismatch}, near parity DIFFERS {s_zdoVerifyParityNear}, far differs {s_zdoVerifyParityFar}, {s_zdoVerifySkipped} not compared";
            if (fail) EasyBakeLog.Warn(line); else EasyBakeLog.Debug(line);
        }

        // A live contributor the ZDO file lacks: true when the piece's ZDO no longer says what the live bake drew (gone, edits only a real piece carries, a worn look).
        private static bool ZdoChangedSince(ZdoFileJob job, MeshBaker.PieceIdentity id)
        {
            if (job.VerifyZdos == null)
            {
                job.VerifyZdos = new Dictionary<MeshBaker.PieceIdentity, ZDO>();
                var scan = job.Scan;
                for (int i = 0; i < scan.Zdos.Count; i++)
                {
                    var z = scan.Zdos[i];
                    if (z != null && z.IsValid() && z.m_uid == scan.Uids[i]) job.VerifyZdos[MeshBaker.PieceIdentity.From(z)] = z;
                }
            }
            if (!job.VerifyZdos.TryGetValue(id, out var zdo)) return true;   // no such ZDO now
            var info = SkipEligibility.Get(id.PrefabHash);
            if (!info.Skippable || info.Prefab == null) return false;
            if (PieceData.MustStayLive(zdo, id.PrefabHash)) return true;
            bool invulnerable = info.AllImmune || SkipEligibility.StoredHealthIsNegative(zdo, info);
            return !invulnerable && WearLooks.Resolve(info.Prefab, zdo) != WearLooks.Healthy(info.Prefab);
        }

        // One hard-MISSING piece, for the FAIL line: what the live bake drew it with and what its ZDO says now.
        private static string ZdoDescribeMissing(ZdoFileJob job, MeshBaker.PieceIdentity id, MeshBaker.BakeResult live)
        {
            var info = SkipEligibility.Get(id.PrefabHash);
            string name = info.Prefab != null ? info.Prefab.name : id.PrefabHash.ToString();
            string liveLook = live.PieceTransforms != null && live.PieceTransforms.TryGetValue(id, out var t) ? t.Look.ToString() : "no transform";
            bool grouped = false;
            for (int g = 0; g < live.InstanceGroups.Count && !grouped; g++) grouped = live.InstanceGroups[g].PrefabHash == id.PrefabHash && live.InstanceGroups[g].Identities.Contains(id);
            if (job.VerifyZdos == null || !job.VerifyZdos.TryGetValue(id, out var zdo)) return $" {name}: live look {liveLook}, {(grouped ? "instanced" : "merged")} live; no ZDO now;";
            var look = WearLooks.Resolve(info.Prefab, zdo);
            bool invulnerable = info.AllImmune || SkipEligibility.StoredHealthIsNegative(zdo, info);
            int keyCount = job.Scan.ByKey.TryGetValue(new InstanceKey(id.PrefabHash, look), out var bucket) ? bucket.Count : 0;
            bool inCombine = job.CombineTransforms.ContainsKey(id);
            return $" {name}: live look {liveLook}, {(grouped ? "instanced" : "merged")} live; ZDO look {look}, invulnerable {invulnerable}, {keyCount} piece(s) of its key in the ZDO scan, {(inCombine ? "given to the combine (not covered there)" : "not given to the combine")};";
        }

        private static string DescribeByPrefab(Dictionary<int, int> by)
        {
            if (by.Count == 0) return "";
            var sb = new StringBuilder(" (");
            int shown = 0;
            foreach (var kv in by)
            {
                if (shown++ > 0) sb.Append(", ");
                if (shown > 12) { sb.Append("..."); break; }
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(kv.Key);
                sb.Append(prefab != null ? prefab.name : kv.Key.ToString()).Append(" x").Append(kv.Value);
            }
            return sb.Append(')').ToString();
        }

        // A scan that found nothing to instance (lever 1): asked again once the sector's ZDO count moves; one Debug line per zone.
        private static void ZdoBakeWait(Vector2s coord, ref ZdoWait wait, int sectorCount, float now, ZdoVia via, string what)
        {
            if (wait == null) { wait = new ZdoWait { Count = sectorCount, StableSince = now }; s_zdoBakeWait[coord] = wait; }
            else if (sectorCount != wait.Count) { wait.Count = sectorCount; wait.StableSince = now; }
            wait.Scans++;
            wait.RetryAt = now + 1f;
            if (wait.Said) return;
            wait.Said = true; s_zdoWaits++;
            EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): ZdoFirstBake waiting (via {ZdoViaNames[(int)via]}): {what}; scanned again as its ZDOs arrive");
        }

        private static ZoneState ZdoBakeDecline(Vector2s coord, string why)
        {
            s_zdoBakeDeclined.Add(coord);
            s_zdoBakeWait.Remove(coord);
            s_zdoBakeFallbacks++;
            EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): ZdoFirstBake fallback: {why}; the zone takes today's path");
            return null;
        }

        // 1.2.378: what the classification reads from SkipEligibility.PrefabInfo, for a building piece SkipEligibility calls not skippable (its PrefabInfo is empty then). Null = the
        // file leaves the prefab live: no ZNetView, or a material variation (its swapped materials are not in the prefab). Never used for a skip decision (Skippable stays false).
        private static SkipEligibility.PrefabInfo ZdoFileInfoOf(ZdoBakePrefab kind, GameObject prefab)
        {
            if (kind.FileInfoTried) return kind.FileInfo;
            kind.FileInfoTried = true;
            var wear = prefab.GetComponent<WearNTear>();
            var view = prefab.GetComponent<ZNetView>();
            if (wear == null || view == null) return null;
            if (prefab.GetComponentInChildren<MaterialVariation>(true) != null || prefab.GetComponentInChildren<MaterialVariationWorld>(true) != null) return null;
            kind.FileInfo = new SkipEligibility.PrefabInfo
            {
                Skippable = false, Blocker = "not pure structure (file job only)", Prefab = prefab, CanShowSnow = wear.m_snow != null, HasWear = true,
                DefaultHealth = wear.m_health, AllImmune = InvulnerableClassifier.AllImmune(wear.m_damages), SyncsScale = view.m_syncInitialScale, PrefabScale = prefab.transform.localScale,
            };
            return kind.FileInfo;
        }

        private static ZdoBakePrefab ZdoBakePrefabOf(int prefabHash, GameObject prefab, SkipEligibility.PrefabInfo info)
        {
            if (s_zdoBakePrefabs.TryGetValue(prefabHash, out var kind)) return kind;
            kind = new ZdoBakePrefab { Piece = prefab.GetComponent<WearNTear>() != null };
            // [visual] (b): a variation swaps materials after creation (MaterialVariants.cs); the prefab's own are not what the piece shows.
            if (kind.Piece && info.Skippable)
            {
                if (prefab.GetComponentInChildren<MaterialVariation>(true) != null) kind.Variation = nameof(MaterialVariation);
                else if (prefab.GetComponentInChildren<MaterialVariationWorld>(true) != null) kind.Variation = nameof(MaterialVariationWorld);
            }
            s_zdoBakePrefabs[prefabHash] = kind;
            return kind;
        }

        // 1.2.377 (verifier leg 1315_1A: "EXTRA 11 (darkwood_roof_45 x8, darkwood_roof_top_45 x3)", 20 groups vs the live bake's 18): the low-count rules are ONE
        // implementation, [visual]'s in MeshBaker (the live bake's OnlyInstancingCanDraw / LowCountDamageableOnly read from prefab + look); this file keeps no copy.
        private static bool ZdoOnlyInstancingCanDraw(int prefabHash, WearLook look, List<ZdoBakePiece> pieces)
        {
            bool anyDamageable = false, anyInvulnerable = false;
            for (int i = 0; i < pieces.Count; i++) { if (pieces[i].Invulnerable) anyInvulnerable = true; else anyDamageable = true; }
            return MeshBaker.OnlyInstancingCanDrawFromZdo(prefabHash, look, anyDamageable, anyInvulnerable);
        }

        private static bool ZdoLowCountDamageableOnly(int prefabHash, WearLook look) => MeshBaker.LowCountDamageableOnlyFromZdo(prefabHash, look);
        private static string DescribeLeftLive(int[] counts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(ZdoLeftLiveNames[i]).Append(' ').Append(counts[i]);
            }
            return sb.Length > 0 ? sb.ToString() : "none";
        }

        // A fresh bake from live pieces landed (first visit without the key, or a Dirty rebake): the zone is no longer ZDO-baked, saves
        // as today, and is not baked from ZDOs again this session ("zones changed this session keep today's path").
        private static void ZdoBakeFreshBakeLanded(ZoneState state)
        {
            state.ZdoBaked = false;
            s_zdoBakeDeclined.Add(state.Coord);
        }

        private static string ZdoBakeSummary()
        {
            double ms = s_zdoBakeTicks * 1000.0 / Stopwatch.Frequency;
            return $"[Cache] ZdoFirstBake so far: {s_zonesFromZdo} zone(s) built from ZDOs ({s_zdoBakedPieces} piece(s) instanced; via {ZdoViaNames[0]} {s_zdoVia[0]}, {ZdoViaNames[1]} {s_zdoVia[1]}, {ZdoViaNames[2]} {s_zdoVia[2]}, {ZdoViaNames[3]} {s_zdoVia[3]}; {s_zdoAfterWait} after a wait), {s_zdoBakeFallbacks} hard fallback(s), {s_zdoWaits} zone(s) waited, {s_zdoGaveUp} range-pass give-up(s); sliced scans {s_zdoSliced} ({s_zdoAttachedEarly} attached early by a creation, {s_zdoScansDropped} dropped, worst slice {s_zdoWorstSliceTicks * 1000.0 / Stopwatch.Frequency:F1} ms, {s_zdoCreatedBeforeSlice} piece(s) created before their slice, {s_zdoGoneDuringScan} ZDO(s) destroyed during a scan); full files from ZDOs: {s_zdoFilesSaved} written, {s_zdoFileWanted.Count} wanted, {s_zdoFileRefusedExisting} not written over an existing file, {s_zdoFileRestarts} restart(s), {s_zdoFileSelfCheckFails} SELF-CHECK FAIL(S), {s_zdoFileOtherFails} other failure(s); this-visit swaps {s_zdoSwapsDone} done ({s_zdoSwapPiecesSilenced} live piece(s) silenced), {s_zdoSwapsCancelled} cancelled; left live {DescribeLeftLive(s_zdoLeftLiveTotal)}; {ms:F0} ms building in all";
        }

        // 1.2.386 ([lead]; town run 3 logged at Info and its per-zone ZDO lines are Debug, so the log could not say how many zones the ZDO path built): one Info line
        // with the counts since the zones were last reset, at the OFF flip and at the reset itself (world close, plugin disable). Silent when the path built nothing,
        // and not repeated when nothing changed since the last line (an OFF flip followed by a reset).
        private static long s_zdoSummarySaid = -1L;
        private static void ZdoSessionSummary(string when)
        {
            if (s_zonesFromZdo == 0 && s_zdoFilesSaved == 0 && s_zdoSwapsDone == 0) return;
            long stamp = ((long)s_zonesFromZdo << 40) ^ ((long)s_zdoFilesSaved << 20) ^ (long)s_zdoSwapsDone;
            if (stamp == s_zdoSummarySaid) return;
            s_zdoSummarySaid = stamp;
            EasyBakeLog.Info($"[Cache] ZdoFirstBake since the zones were last reset ({when}): {s_zonesFromZdo} zone(s) built from ZDOs, {s_zdoFilesSaved} file(s) written from ZDOs, {s_zdoSwapsDone} this-visit swap(s) done ({s_zdoSwapsCancelled} cancelled), {s_zdoFileSelfCheckFails} file self-check failure(s), {s_zdoFileWanted.Count} zone(s) still without a file");
        }

        private static void ZdoBakeReset()
        {
            ZdoSessionSummary("zones reset: world closed or plugin disabled");   // 1.2.386: before the counters below are cleared
            s_zdoSummarySaid = -1L;
            s_zdoBakeDeclined.Clear(); s_zdoBakeWait.Clear(); s_zdoBakePrefabs.Clear(); s_zdoScans.Clear(); s_zdoScanOrder.Clear();
            System.Array.Clear(s_zdoLeftLiveTotal, 0, s_zdoLeftLiveTotal.Length); System.Array.Clear(s_zdoVia, 0, s_zdoVia.Length);
            s_zonesFromZdo = 0; s_zdoBakeFallbacks = 0; s_zdoBakedPieces = 0; s_zdoBakeTicks = 0; s_zdoAfterWait = 0; s_zdoWaits = 0; s_zdoGaveUp = 0;
            s_zdoSwap = null; s_zdoSwapQueue.Clear(); s_zdoSwapsDone = 0; s_zdoSwapsCancelled = 0; s_zdoSwapPiecesSilenced = 0;
            ZdoCombine.Reset(); s_zdoVerifyQueue.Clear(); s_zdoVerifyZones = 0; s_zdoVerifyExtra = 0; s_zdoVerifyMissingAllowed = 0; s_zdoVerifyMissingHard = 0; s_zdoVerifyTransformMismatch = 0; s_zdoVerifySkipped = 0; s_zdoVerifyParityNear = 0; s_zdoVerifyParityFar = 0; s_zdoVerifySeen.Clear(); s_zdoVerifyUnseen = 0; s_zdoVerifyMissingChanged = 0;   // 1.2.374
            s_zdoFileQuiet.Clear(); s_zdoFileSavedCount.Clear();
            s_zdoFilesWritten.Clear(); s_zdoFileWanted.Clear(); s_zdoFileRefused.Clear(); s_zdoFileJob = null; s_zdoFilesSaved = 0; s_zdoFileRefusedExisting = 0; s_zdoFileRestarts = 0; s_zdoFileSelfCheckFails = 0; s_zdoFileOtherFails = 0;   // 1.2.374
            s_zdoSliced = 0; s_zdoAttachedEarly = 0; s_zdoScansDropped = 0; s_zdoWorstSliceTicks = 0; s_zdoGoneDuringScan = 0; s_zdoCreatedBeforeSlice = 0; s_zdoWalkDue = false;
        }
    }
}
