using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.228 ([visual] 11-08, Fire's single-player split: "ebm gets rendering logic, fgn gets network fixes ... they all work together or
    // separately"): EBM paces object creation on a client or a local host. ONE pacer: this takes ZNetScene.CreateObjects only when no earlier
    // prefix already replaced its body (__runOriginal), so while an older FGN still paces, this stands down by itself, never two at once.
    // Written for EBM from what [fgn]'s proven pacer (FGN 1.6.73-1.6.77, 95 % at T+48 s at 615) shows the job needs:
    //  - MEMBERSHIP: vanilla's own near list (CreateDestroyObjects builds it anyway). Seeded once per reference zone; new ZDOs join through
    //    ZDOMan.AddToSector / InitialAddToSector (events); a round-robin slice of the near list is re-checked each pass so nothing is missed.
    //  - ORDER: waves of (zone ring, type rank): the player's zone first, and inside a ring terrain / solid before loose items.
    //    A bucket left waiting over FairAfterS gets up to a quarter of a pass first, so nothing starves.
    //  - BUDGET from the measured frame: "other" = the last frame minus our own create work (smoothed). Arrival (loading screen, no player,
    //    teleport): up to ArrivalTargetMs whole frames. Play: the headroom under the machine's own target frame (vsync / fps cap / refresh);
    //    standing still it fills up to NoHitchMs frames; a frame spike backs off for BackoffFrames. Creates per pass = budget / measured ms
    //    per create. A prefab known to cost more than what is left goes first next pass, alone.
    //  - DISTANT objects get what the near pass leaves, from a round-robin window (never a full walk of the list to skip created ones).
    //  - An extra pass on frames vanilla's 30 Hz tick skips, only while the queue has work (returns at once otherwise).
    //  - Publishes EbmApi.StreamingSignalActive / ZoneFilling / ZoneSettled.
    // [Optimize] ObjectStreamPacer (default on; off = vanilla's create path + EBM's CreateObjectsSorted governor, as before). Throws once = off for
    // the session (objects already created stay; vanilla picks up exactly where it stopped).
    // 1.2.250 (Fire's SP split, plan rows #1, #2, #9): FGN's client create queue moved here; THIS is the one client / local-host pacer (FGN keeps only the
    // dedicated server's path). Arrival stays vanilla (1.2.232, log-proven). A dedicated server never gets here. Base budget and cap floor are EBM's own keys.
    [HarmonyPatch]
    internal static class StreamPacer
    {
        private const int Types = 4, Rings = 16, Waves = Rings + Types - 1;
        private const int SweepPerPass = 4000, DistantWindow = 4000;
        private const float ArrivalTargetMs = 120f, ArrivalMinMs = 8f, ArrivalMaxMs = 40f;
        private const float StillMaxMs = 40f, StillSpeed = 0.5f, NoHitchMs = 45f, UnfocusedMs = 40f, SpikeMinMs = 50f;
        private const int BackoffFrames = 30, ArrivalCap = 1000, MaxCap = 5000;
        // 1.2.357 ([visual], [lead] 11-13 from [2ic]'s rain-arrival arms perfF-ab-ebm1354rain_A/B/B2: frames of 140-157 ms with ZNetScene.CreateObjects 66-94 ms within
        // 2 s of "the create pacer stood down for 3.2 s: arrival ... it creates objects again (queue 11649)"; the 30 s line after it "pass avg 1.53 ms (worst 92.7),
        // budget avg 23.8 ms, other work 15.8 ms, target 6.1 ms"): once the fade ends the player stands still, so Budget() gave the standing-still allowance
        // (up to min(StillMaxMs, NoHitchMs - other) = ~30 ms a pass) to a queue of 11k fresh objects, and a first create of a prefab kind (no cost known yet,
        // so Fits lets it through) ran that pass to 90 ms. Fire's rule: a longer load over post-load hitches. For ArrivalGraceSeconds after an arrival
        // stand-down ends, a still player gets the moving formula instead (the frame's headroom under the target, never above the target): the same objects
        // arrive over more frames. Passes that still overrun their budget 2x log the slowest create (name, cost known or not) so the next leg names the rest.
        private const float ArrivalGraceSeconds = 8f, OverrunLogGapS = 2f;
        private static float s_graceUntil = -1f, s_nextOverrunSaid, s_passWorstMs; private static int s_passWorstPrefab, p_gracePasses, p_overruns; private static bool s_passWorstKnown;
        private const float FairAfterS = 10f;
        // the floor of any pass (vanilla-like pacing when the measured headroom is gone)
        private static float BaseMs => Mathf.Max(1f, (FiresEasyBakeMeshesPlugin.StreamPacerBaseMs != null ? FiresEasyBakeMeshesPlugin.StreamPacerBaseMs.Value : 3) * PerformanceProfile.Budget);
        private static int MinPerPass => Math.Max(10, FiresEasyBakeMeshesPlugin.StreamPacerMinPerFrame != null ? FiresEasyBakeMeshesPlugin.StreamPacerMinPerFrame.Value : 100);

        // 1.2.250: ValheimCommunityPatch creates from its own queue inside CreateObjectsSorted / CreateDestroyObjects prefixes, so this pass would bypass it
        // (__runOriginal cannot show that: its prefix is not on CreateObjects). Looked up once, on the first pass, when every mod has patched.
        private const string VcpGuid = "MidnightsFX.ValheimCommunityPatch";
        private static bool s_vcpChecked, s_vcpOwns;
        private static bool VcpOwnsCreation()
        {
            if (s_vcpChecked) return s_vcpOwns;
            s_vcpChecked = true;
            s_vcpOwns = HasVcpPrefix(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateDestroyObjects", Type.EmptyTypes))
                     || HasVcpPrefix(AccessTools.DeclaredMethod(typeof(ZNetScene), "CreateObjectsSorted"));
            if (s_vcpOwns) EasyBakeLog.Info("[Stream] ValheimCommunityPatch schedules object creation: EBM's create pacer stands down for this session");
            return s_vcpOwns;
        }
        private static bool HasVcpPrefix(System.Reflection.MethodBase target)
        {
            if (target == null) return false;
            var info = Harmony.GetPatchInfo(target);
            if (info == null || info.Prefixes == null) return false;
            foreach (var p in info.Prefixes) if (p.owner == VcpGuid) return true;
            return false;
        }

        // 1.2.250: for the idle object-area skip: the queue is seeded for this reference zone
        internal static bool SeededFor(Vector2s zone) => s_seeded && zone.x == s_refZone.x && zone.y == s_refZone.y;

        internal static bool Ready => On && Resolve();   // 1.2.231: enabled, vanilla helpers found ([generator]: never yield to a pacer that cannot run) and not faulted
        private static bool On => !s_broken && (FiresEasyBakeMeshesPlugin.StreamPacer == null || FiresEasyBakeMeshesPlugin.StreamPacer.Value);
        // 1.2.265 TESTPLAN 0b keys (read live)
        private static bool ThroughArrival => FiresEasyBakeMeshesPlugin.StreamPacerThroughArrival != null && FiresEasyBakeMeshesPlugin.StreamPacerThroughArrival.Value;
        private static bool NearFirst => FiresEasyBakeMeshesPlugin.StreamPacerNearFirst != null && FiresEasyBakeMeshesPlugin.StreamPacerNearFirst.Value;
        private static bool SectorLeave => FiresEasyBakeMeshesPlugin.StreamPacerSectorLeave != null && FiresEasyBakeMeshesPlugin.StreamPacerSectorLeave.Value;
        private static int p_arrivalPaced, p_nearFirst, p_sectorLeft;
        internal static bool Active => s_active && On;
        internal static bool HasWork => s_queued.Count > 0 || s_waiting.Count > 0 || s_wasArrival;
        internal static int LastPassFrame = -1;

        // ---- vanilla helpers (through delegates, so every mod's patch on them still runs) ----
        private static Func<ZNetScene, ZDO, GameObject> s_createObject;

        // ═══ 1.2.323 [Fixes] ArrivalAreaFirst ([lead] 11-11: four "not 'ready' for 30 s" arrivals; profiler leg, respawn at (-1234, 690)) ═══
        // Proof ((rig path)): "[Arrival] waiting 10 s ... missing 34891 ... 34891 still to create" and the SAME 34891 at
        // 27 s: nothing was created at all. Cause, from source: vanilla ZNetScene.IsAreaReady (the hold) needs the zone loaded AND an instance for every
        // valid ZDO in the 3x3 sectors; but ZNetScene.CreateObjectsSorted returns before creating anything while !ZoneSystem.IsActiveAreaLoaded()
        // (ZNetScene.cs:210), i.e. until EVERY zone of the whole near-simulation area exists (ZoneSystem.cs:1348). Zones spawn one per CreateLocalZones
        // call (ZoneSystem.cs:1280) and the arrival frames ran 300-1000 ms (Heightmap.Regenerate 40-65 ms several times a frame), so the 3x3 the hold
        // waits on sat uncreated while far zones of the active area loaded (the [ZoneLoad] lines run to 65 s). EBM's pacer has the same gate (Pass).
        // Fix: while an arrival waits, the objects of the 3x3 the hold checks are created as soon as THEIR zone is loaded and ready for their type
        // (vanilla's own per-object test, IsZoneReadyForType), at the loading-screen rate (100 a frame, vanilla's InLoadingScreen budget), nearest
        // first; everything else still waits for vanilla's whole-area gate. A create that fails (invalid prefab) is not retried this arrival.
        private static readonly List<ZDO> s_arrivalList = new List<ZDO>();
        private static readonly HashSet<ZDOID> s_arrivalFailed = new HashSet<ZDOID>();
        private static float s_arrivalScanAt, s_aafLastCall; private static bool s_aafSaid;   // 1.2.326: one line per ARRIVAL (reset after 2 s without a call)
         private static Vector2s s_arrivalZone; private static int s_arrivalIdx, s_arrivalCreated;
        private static void ArrivalArea(ZNetScene scene, Vector3 refPos)
        {
            if (FiresEasyBakeMeshesPlugin.ArrivalAreaFirst == null || !FiresEasyBakeMeshesPlugin.ArrivalAreaFirst.Value || s_createObject == null) return;
            var zs = ZoneSystem.instance; var zm = ZDOMan.instance;
            if (zs == null || zm == null || zs.IsActiveAreaLoaded()) return;   // the whole area is loaded: vanilla's own pass runs, nothing to add
            Vector2s zone = ZoneSystem.GetZone(refPos);
            float now = Time.unscaledTime;
            if (zone != s_arrivalZone || now >= s_arrivalScanAt || s_arrivalIdx >= s_arrivalList.Count)
            {
                if (zone != s_arrivalZone || now - s_aafLastCall > 2f) { s_arrivalFailed.Clear(); s_arrivalCreated = 0; s_aafSaid = false; }
                s_arrivalZone = zone; s_arrivalScanAt = now + 0.5f; s_arrivalIdx = 0;
                s_arrivalList.Clear();
                zm.FindSectorObjects(zone, new SimulationDistance(1, 0), s_arrivalList);
                s_arrivalList.RemoveAll(z => z == null || z.Created || s_arrivalFailed.Contains(z.m_uid));
                foreach (var z in s_arrivalList) z.m_tempSortValue = Utils.DistanceSqr(refPos, z.GetPosition());
                s_arrivalList.Sort((a, b) => a.m_tempSortValue.CompareTo(b.m_tempSortValue));
            }
            s_aafLastCall = now;   // 1.2.326
            int made = 0;
            while (s_arrivalIdx < s_arrivalList.Count && made < 100)
            {
                var z = s_arrivalList[s_arrivalIdx++];
                if (z == null || z.Created || !z.IsValid()) continue;
                Vector2s zz = ZoneSystem.GetZone(z.GetPosition());
                if (!zs.IsZoneLoaded(zz) || !zs.IsZoneReadyForType(z.GetSector(), z.Type)) continue;   // its own zone not there yet: the next scan tries again
                s_createObject(scene, z);
                if (z.Created) { made++; s_arrivalCreated++; }
                else s_arrivalFailed.Add(z.m_uid);   // invalid prefab: never holds the arrival again (vanilla's IsPrefabZDOValid answers it)
            }
            if (made > 0 && !s_aafSaid) { s_aafSaid = true; EasyBakeLog.Info($"[Arrival] ArrivalAreaFirst: creating the area around ({refPos.x:0}, {refPos.z:0}) while the rest of the active area still loads ({s_arrivalList.Count} object(s) in the 3x3 to go)"); }
        }
        private static Func<ZNetScene, bool> s_inLoadingScreen;
        private static Action<ZNetScene, List<ZDO>, List<ZDO>> s_createObjects;
        private static bool s_resolved, s_broken, s_active, s_saidOn, s_inExtraPass;

        private static bool Resolve()
        {
            if (s_resolved) return !s_broken;
            s_resolved = true;
            try
            {
                s_createObject = AccessTools.MethodDelegate<Func<ZNetScene, ZDO, GameObject>>(AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
                s_inLoadingScreen = AccessTools.MethodDelegate<Func<ZNetScene, bool>>(AccessTools.Method(typeof(ZNetScene), "InLoadingScreen", Type.EmptyTypes));
                s_createObjects = AccessTools.MethodDelegate<Action<ZNetScene, List<ZDO>, List<ZDO>>>(AccessTools.Method(typeof(ZNetScene), "CreateObjects", new[] { typeof(List<ZDO>), typeof(List<ZDO>) }));
            }
            catch (Exception e) { s_broken = true; EasyBakeLog.Warn("[Stream] the create pacer cannot find vanilla's create helpers on this game version; vanilla creates as before: " + e.Message); }
            return !s_broken;
        }

        // ---- queue state ----
        // 1.2.257: a third index, the octant (direction from the reference position, world-fixed) for StreamPacerViewOrder. With the key off every object goes to octant 0,
        // so each (type, ring) holds exactly the list it held before and the drain order is unchanged.
        private const int Octants = 8;
        private static readonly List<ZDO>[,,] s_buckets = new List<ZDO>[Types, Rings, Octants];
        private static readonly float[,,] s_since = new float[Types, Rings, Octants];   // when the bucket last went from empty to waiting (-1 = empty)
        private static readonly Dictionary<ZDOID, ZDO> s_queued = new Dictionary<ZDOID, ZDO>();
        private static readonly Dictionary<ZDOID, Vector2s> s_addSector = new Dictionary<ZDOID, Vector2s>();   // [generator] M1: the sector counted at add time (a ZDO can move before it is made)
        private static readonly HashSet<ZDOID> s_failed = new HashSet<ZDOID>();   // a create that left the ZDO uncreated (invalid prefab): never holds a zone
        private static readonly Dictionary<Vector2s, int> s_pendingByZone = new Dictionary<Vector2s, int>();
        private static readonly Dictionary<Vector2s, float> s_filling = new Dictionary<Vector2s, float>();
        private static readonly Dictionary<Vector2s, List<ZDO>> s_waiting = new Dictionary<Vector2s, List<ZDO>>();   // zone not ready for that type yet
        private static readonly List<Vector2s> s_tmpZones = new List<Vector2s>();
        private static readonly bool[] s_ready = new bool[Types];
        private static Vector2s s_refZone; private static bool s_seeded; private static int s_sweepCursor, s_distantCursor;
        private static SimulationDistance s_sd;

        // ---- budget state ----
        private static float s_otherAvg = -1f, s_frameAvg = -1f, s_lastPassMs, s_costAvg = -1f;
        private static int s_backoff; private static bool s_wasArrival; private static int s_arrivalFrames;   // 1.2.232: passes left to vanilla during arrival
        private static readonly Dictionary<int, long> s_prefabCost = new Dictionary<int, long>();   // ticks, smoothed; bounded by the prefab count

        // ---- proof (30 s, Debug) + one Info per drained fill ----
        private static int p_frames, p_created, p_distant, p_extra, p_seeds, p_events, p_swept, p_waited, p_aged, p_heavyDeferred, p_spikes, p_settled;
        private static double p_passMs, p_budgetMs; private static float p_passMax, p_next;
        private static float f_start = -1f; private static int f_created, f_peak;

        static StreamPacer() { for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) { s_buckets[t, r, o] = new List<ZDO>(); s_since[t, r, o] = -1f; } }

        // ================================================================== the pass
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPrefix, HarmonyPriority(Priority.Low)]   // after any other pacer's prefix, so __runOriginal tells us it already took the pass
        private static bool CreateObjectsPrefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects, bool __runOriginal)
        {
            using (EbmPatchMarkers.PacerPass.Auto()) return CreateObjectsPrefixBody(__instance, currentNearObjects, currentDistantObjects, __runOriginal);   // 1.2.360: profiler marker only
        }
        private static bool CreateObjectsPrefixBody(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects, bool __runOriginal)
        {
            s_prefixCalls++;   // 1.2.315 Watch
            if (!__runOriginal) { s_active = false; StandDown("another mod's prefix already took the create pass"); return false; }   // an earlier prefix (another pacer) already owns this pass: stand down
            if (!On) { s_active = false; StandDown(s_broken ? "off for the session after an error" : "[Optimize] ObjectStreamPacer is off"); return true; }
            var net = ZNet.instance;
            if (net == null || ZoneSystem.instance == null || (net.IsServer() && net.IsDedicated())) { s_active = false; StandDown(net != null && net.IsDedicated() ? "dedicated server" : "no world yet"); return true; }   // a dedicated server keeps its own path
            if (!Resolve()) { StandDown("vanilla's create helpers were not found"); return true; }
            if (VcpOwnsCreation()) { s_active = false; StandDown("ValheimCommunityPatch owns object creation"); return true; }
            try
            {
                bool loading = s_inLoadingScreen(__instance), noPlayer = Player.m_localPlayer == null, teleporting = !noPlayer && Player.m_localPlayer.IsTeleporting();
                // 1.2.232 (lead, 1229 run: Fire spawned at the WORLD SPAWN, "area at the logout point not ready ... zone loaded False"): arrival runs
                // vanilla, exactly as before EBM paced (FGN's proven rule). The pacer only paces once the player is placed and not teleporting.
                if (loading || noPlayer || teleporting)
                {
                    // 1.2.265 [Optimize] StreamPacerThroughArrival (TESTPLAN 0b part 2): vanilla only until the arrival spot is ready (ZNetScene.IsAreaReady
                    // at the reference position: the 3x3 every spawn / teleport hold waits on, with EBM's skip-aware postfix), so the hold is unchanged;
                    // after that the pacer creates the rest of the area with the arrival budget instead of vanilla's full collect + sort of every object.
                    Vector3 refPos = ZNet.instance.GetReferencePosition();   // [ghost] review: the spawn-wait bug has left it at 0,0,0; never pace around the origin then
                    if (ThroughArrival && refPos != Vector3.zero && __instance.IsAreaReady(refPos))
                    {
                        s_arrivalFrames++; p_arrivalPaced++;
                        EndStandDown();
                        Pass(__instance, currentNearObjects, currentDistantObjects, true);
                        return false;
                    }
                    s_wasArrival = false; s_arrivalFrames++;
                    StandDown("arrival (" + (loading ? "loading screen" : noPlayer ? "no player yet" : "teleporting") + "): vanilla creates and sorts the whole near area");
                    if (refPos != Vector3.zero) ArrivalArea(__instance, refPos);   // 1.2.323 [Fixes] ArrivalAreaFirst
                    return true;
                }
                EndStandDown();
                Pass(__instance, currentNearObjects, currentDistantObjects, false);
                return false;
            }
            catch (Exception e)
            {
                s_broken = true; s_active = false; ClearAll();
                EasyBakeLog.Warn("[Stream] the create pacer threw and is off for this session; vanilla creates from here (objects already made stay): " + e);
                return true;
            }
        }

        // 1.2.264 (lead 11-10: a 16,615-object vanilla sort means the pacer had stood down; census legs must show it directly): every stretch
        // the pacer hands creation back to vanilla is logged with its reason and length when it ends (or changes reason). Info for a stretch of
        // 2 s or more, Debug for shorter ones; the [Stream] 30 s line counts them. A new stretch with the same reason is not re-logged.
        private static string s_downWhy;
        private static float s_downSince;
        private static int p_downStretches; private static float p_downSeconds;
        private static void StandDown(string why)
        {
            if (s_downWhy == why) return;
            EndStandDown();
            s_downWhy = why; s_downSince = Time.unscaledTime;
            // 1.2.315 ([lead]: Fire's BF-town log had no [Stream] line at all, so whether vanilla or the pacer owned creation could not be told): the
            // hand-back is logged when it STARTS (the end-of-stretch line never comes for a stand-down that lasts the whole session). Arrival / teleport
            // stretches are normal and short: Debug; any other reason: Info, once per reason per session.
            bool routine = why.StartsWith("arrival", StringComparison.Ordinal);
            string line = $"[Stream] the create pacer hands object creation to vanilla: {why}";
            if (routine) EasyBakeLog.Debug(line);
            else if (s_saidDown.Add(why)) EasyBakeLog.Info(line + " (vanilla collects and sorts every uncreated object near you each pass)");
        }
        private static readonly HashSet<string> s_saidDown = new HashSet<string>();
        // 1.2.315: vanilla's ZNetScene.CreateObjects never reaching this prefix (another mod replacing CreateDestroyObjects upstream) leaves NO [Stream]
        // line at all; after 60 s in the world with no call, say so once (Info). Called from the plugin's LateUpdate.
        private static int s_prefixCalls; private static float s_worldSince = -1f; private static bool s_saidNoCalls;
        internal static void Watch()
        {
            if (s_saidNoCalls || s_prefixCalls > 0) return;
            if (Player.m_localPlayer == null || ZNetScene.instance == null) { s_worldSince = -1f; return; }
            float now = Time.unscaledTime;
            if (s_worldSince < 0f) { s_worldSince = now; return; }
            if (now - s_worldSince < 60f) return;
            s_saidNoCalls = true;
            EasyBakeLog.Info("[Stream] vanilla's ZNetScene.CreateObjects has not run for 60 s in the world, so the create pacer never gets a pass: another mod is creating objects its own way (CreateDestroyObjects / CreateObjects replaced upstream)");
        }
        internal static int QueueCount => s_queued.Count;                       // 1.2.315: EbmApi.StreamQueueCount
        // 1.2.347 ([visual], Fire's bare terrain zones 11-12/13; FDT 1.1.1197's TerrainCompProbe in run_9: 43 of 43 compilers that woke with no heightmap were
        // "created from FiresEasyBakeMeshes.EasyBake.StreamPacer.Create" in zones 4-6 rings out = this DISTANT pass): the distant list vanilla hands over is
        // filtered on the ZDO's own Distant flag (ZDOMan.FindDistantObjects), which a mod can leave true on a prefab that is not drawable at a distance, and a
        // _TerrainCompiler created before its zone's heightmap exists never applies the zone's edits and paint (TerrainComp.Awake returns unregistered).
        // The pass now creates only ZDOs whose PREFAB is a distant prefab (ZNetView.m_distant) and never a terrain compiler; anything else stays for the
        // near pass of its zone. Judged once per prefab hash; one Info per refused prefab the first time, counts in the 30 s census.
        private static readonly Dictionary<int, bool> s_distantOk = new Dictionary<int, bool>();
        private static readonly HashSet<int> s_distantRefusedSaid = new HashSet<int>();
        private static readonly int TerrainCompilerHash = "_TerrainCompiler".GetStableHashCode();
        private static int p_distantRefused;
        private static bool DistantPrefabOk(ZDO zdo)
        {
            int hash = zdo.GetPrefab();
            if (!s_distantOk.TryGetValue(hash, out bool ok))
            {
                ok = false;
                if (hash != TerrainCompilerHash)
                {
                    var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                    var nv = prefab != null ? prefab.GetComponent<ZNetView>() : null;
                    ok = nv != null && nv.m_distant;
                }
                s_distantOk[hash] = ok;
                if (!ok && s_distantRefusedSaid.Add(hash))
                {
                    var p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                    EasyBakeLog.Info($"[Stream] distant pass: '{(p != null ? p.name : hash.ToString())}' is not a distant prefab (its ZDOs carry the Distant flag anyway); it is left for the near pass of its zone (a compiler created before its heightmap would lose the zone's terrain edits)");
                }
            }
            if (!ok) p_distantRefused++;
            return ok;
        }
        // 1.2.346 ([visual], Fire's run_8: whole zones "N of N handed-back piece(s) still not created 60 s after the hand-back ... create pacer queue 0"):
        // the Warning asks the pacer where one example piece stands, so the next run says WHY it was never created
        // 1.2.348 ([visual]; Fire's automated run 12:1x AM on 1.2.347: 32 more "[TerrainCompProbe] compiler woke with NO heightmap ... created from
        // StreamPacer.Create" in zones 2-6 rings out, and NOT ONE "distant pass: '_TerrainCompiler' is not a distant prefab" refusal, so 1.2.347's gate never
        // saw them: the pass that created them is unconfirmed from that log). The guard now sits where every pass meets: a prefab that carries a
        // TerrainComp is not created while Heightmap.FindHeightmap(position) is null (the very test TerrainComp.Awake fails on, vanilla TerrainComp.cs:56),
        // judged by the NEED (the component), not the prefab name. The near pass parks it in the zone's wait list, the distant pass leaves it in place;
        // neither marks it failed. One Warning per 30 s names the pass, the ZDO's sector against its position's zone, its Distant flag and the rings, so
        // the next run proves the path.
        private static readonly Dictionary<int, bool> s_needsHeightmap = new Dictionary<int, bool>();
        private static int p_heightmapHeld; private static float s_nextHeightmapSaid;
        // 1.2.349: ZoneSystem keeps its OWN copy of the simulation distance (ApplySettings, ZoneSystem.cs:3711-3714) and loads zones radially from it, while the near
        // object list and InNear use ZNet.GetSyncedSimulationDistance(); Fire's 1.2.348 run held compilers at ring 5 of a near ring 6 with "zone loaded False, active
        // area loaded True", so the two copies differ on his client (classic vs radial, or near 6 vs a smaller loaded ring): the Warning now prints both.
        private static readonly AccessTools.FieldRef<ZoneSystem, SimulationDistance> s_zsSd = AccessTools.FieldRefAccess<ZoneSystem, SimulationDistance>("m_simulationDistance");
        private static string ZoneSystemSd() { try { var zs = ZoneSystem.instance; if (zs == null || s_zsSd == null) return "?"; var d = s_zsSd(zs); return $"near {d.NearSimulationDistance}, classic {d.IsClassic}, total {d.TotalSimulationDistance}"; } catch (Exception e) { return "unreadable: " + e.GetType().Name; } }
        private static bool NeedsHeightmapNotThere(ZDO zdo, int pass)
        {
            int hash = zdo.GetPrefab();
            if (!s_needsHeightmap.TryGetValue(hash, out bool needs))
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                needs = prefab != null && prefab.GetComponent<TerrainComp>() != null;
                s_needsHeightmap[hash] = needs;
            }
            if (!needs) return false;
            Vector3 pos = zdo.GetPosition();
            if (Heightmap.FindHeightmap(pos) != null) return false;
            p_heightmapHeld++;
            if (Time.unscaledTime >= s_nextHeightmapSaid)
            {
                s_nextHeightmapSaid = Time.unscaledTime + 30f;
                Vector2s sec = zdo.GetSector(); Vector2s pz = ZoneSystem.GetZone(pos);
                var zs = ZoneSystem.instance;
                EasyBakeLog.Warn($"[Stream] held a terrain compiler with no heightmap yet: pass {(pass == 1 ? "near" : "distant")}, prefab hash {hash} (the _TerrainCompiler hash is {TerrainCompilerHash}, DistantPrefabOk says {DistantPrefabOk(zdo)}), position ({pos.x:0},{pos.y:0},{pos.z:0}) zone ({pz.x},{pz.y}), ZDO sector ({sec.x},{sec.y}), Distant {zdo.Distant}, ring from the reference zone {RingOf(sec)} (sector) / {RingOf(pz)} (position), near ring {s_sd.NearSimulationDistance} (classic {s_sd.IsClassic}, total {s_sd.TotalSimulationDistance}; ZoneSystem's own: {ZoneSystemSd()}), within vanilla's radial ring {(zs != null && zs.ZonesWithinRadius(s_refZone, sec, s_sd.NearSimulationDistance))}, InNear(sector) {InNear(sec)}, zone loaded {(zs != null && zs.IsZoneLoaded(pz))}, active area loaded {(zs != null && zs.IsActiveAreaLoaded())}; it is created once its heightmap exists ({p_heightmapHeld} hold(s) this session)");
            }
            return true;
        }

        internal static string Whereabouts(ZDO zdo)
        {
            if (zdo == null) return "no ZDO";
            ZDOID id = zdo.m_uid;
            if (s_queued.ContainsKey(id)) return "queued in the pacer";
            if (s_failed.Contains(id)) return "PARKED as failed by the pacer (a create that left it uncreated; never retried)";
            foreach (var kv in s_waiting) foreach (var w in kv.Value) if (ReferenceEquals(w, zdo)) return "waiting in the pacer for zone " + kv.Key + " to be ready";
            Vector2s sec = zdo.GetSector();
            return "NOT in the pacer at all (sector " + sec + ", ring " + (s_seeded ? RingOf(sec).ToString() : "?") + " from the ref zone " + (s_seeded ? s_refZone.ToString() : "?") + ", near " + (s_seeded && InNear(sec)) + ")";
        }
        internal static bool OwnsCreation => s_active && s_downWhy == null;     // 1.2.315: EbmApi.StreamPacerOwnsCreation
        private static void EndStandDown()
        {
            if (s_downWhy == null) return;
            float d = Time.unscaledTime - s_downSince;
            p_downStretches++; p_downSeconds += d;
            string line = $"[Stream] the create pacer stood down for {d:0.0} s: {s_downWhy}; it creates objects again (queue {s_queued.Count})";
            if (d >= 2f) EasyBakeLog.Info(line); else EasyBakeLog.Debug(line);
            if (s_downWhy.StartsWith("arrival", StringComparison.Ordinal)) s_graceUntil = Time.unscaledTime + ArrivalGraceSeconds;   // 1.2.357: post-arrival grace
            s_downWhy = null;
        }

        private static void Pass(ZNetScene scene, List<ZDO> near, List<ZDO> distant, bool arrival)
        {
            s_active = true;
            if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info("[Stream] EBM create pacer on: objects stream from one event queue, the player's zone first, paced by this machine's measured frame; ZoneFilling / ZoneSettled published on EbmApi"); }
            float now = Time.unscaledTime;
            // 1.2.257 (per-object create allocations, item 4): one Stopwatch restarted per pass instead of a new one; same clock, same ticks, same
            // budget arithmetic. A nested pass (never seen; only if another mod re-entered CreateObjects from inside a create) gets its own, as before.
            Stopwatch sw;
            if (s_inPass) sw = Stopwatch.StartNew(); else { sw = s_passWatch; sw.Restart(); AllocsAvoided++; }
            bool outer = !s_inPass;
            s_inPass = true;
            try { PassBody(scene, near, distant, arrival, now, sw); }
            finally { if (outer) s_inPass = false; }
        }

        private static readonly Stopwatch s_passWatch = new Stopwatch();
        private static bool s_inPass;
        internal static long AllocsAvoided;   // Stopwatches and per-zone wait lists reused instead of made

        private static void PassBody(ZNetScene scene, List<ZDO> near, List<ZDO> distant, bool arrival, float now, Stopwatch sw)
        {

            // the last frame, measured: unscaledDeltaTime is the whole previous frame, s_lastPassMs our own create work in it
            if (LastPassFrame != Time.frameCount) MeasureFrame(arrival);

            // membership
            bool nearList = near != null && near.Count > 0;
            Vector2s zone = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
            s_sd = ZNet.instance.GetSyncedSimulationDistance();
            ViewSetup();   // 1.2.257: reference position + camera forward for this pass (logging always; ordering only with StreamPacerViewOrder)
            if (nearList && (!s_seeded || zone.x != s_refZone.x || zone.y != s_refZone.y))
            {
                using (EbmPatchMarkers.PacerReseed.Auto())   // 1.2.361: profiler marker only (the O(near list) walk on every reference-zone change)
                {
                ClearQueue(); s_refZone = zone; s_seeded = true; p_seeds++;   // [generator] M2: open fills stay open; Signals settles them when nothing is pending
                for (int i = 0; i < near.Count; i++) TryAdd(near[i], now);
                PruneStamps();   // 1.2.257: enqueue stamps survive a reseed (true wait times); drop those of objects no longer queued
                }
            }
            else if (nearList)
            {
                using (EbmPatchMarkers.PacerSweep.Auto())   // 1.2.361: profiler marker only
                {
                int n = near.Count; if (s_sweepCursor >= n) s_sweepCursor = 0;
                int end = Math.Min(n, s_sweepCursor + SweepPerPass);
                for (int i = s_sweepCursor; i < end; i++) if (TryAdd(near[i], now)) p_swept++;
                s_sweepCursor = end;
                }
            }
            using (EbmPatchMarkers.PacerReady.Auto()) ReleaseReadyZones(now);   // 1.2.361: profiler marker only

            // this pass's budget
            float memberMs = (float)(sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency); if (memberMs > p_memberMax) p_memberMax = memberMs;   // 1.2.252: seeding + sweep, inside the pass time
            s_floorThisPass = false; s_passReal = 0;
            s_passWorstMs = 0f; s_passWorstPrefab = 0; s_passWorstKnown = false;   // 1.2.357
            s_mcPass = false; s_mcExtraStart = -1;
            float ms = Budget(arrival, out int cap);
            long budgetTicks = (long)(ms * Stopwatch.Frequency / 1000.0);
            s_wasArrival = arrival;
            p_budgetMs += ms;

            int created = 0, attempts = 0;
            if (s_seeded) DrainCompilers(scene, sw, budgetTicks, ref created, ref attempts);   // 1.2.427: terrain edits first, before the whole-area gate below
            if (s_seeded && ZoneSystem.instance.IsActiveAreaLoaded())
            {
                // fairness first: the oldest bucket waiting over FairAfterS gets up to a quarter of the pass
                // (1.2.257: the scan covers the octants too; with StreamPacerViewOrder off only octant 0 is ever filled, so the pick is the same)
                bool done = false, nearPending = false;
                // 1.2.265 [Optimize] StreamPacerNearFirst (TESTPLAN 0b part 3): the player's zone and the ring around it (rings 0-1) drain first, every type
                // and octant, before the fairness pick and the far rings get budget; the fairness pick runs only once rings 0-1 are empty.
                if (NearFirst)
                {
                    int nb = created;
                    for (int r = 0; r < 2 && r < Rings && !done; r++)
                        for (int o = 0; o < Octants && !done; o++)
                            for (int t = Types - 1; t >= 0 && !done; t--)
                                done = Drain(scene, t, r, o, cap, int.MaxValue, sw, budgetTicks, ref created, ref attempts) == DrainDone;
                    p_nearFirst += created - nb;
                    for (int r = 0; r < 2 && r < Rings && !nearPending; r++) for (int o = 0; o < Octants && !nearPending; o++) for (int t = 0; t < Types; t++) if (s_buckets[t, r, o].Count > 0) { nearPending = true; break; }
                }
                int at = -1, ar = -1, ao = -1; float oldest = now - FairAfterS;
                if (!done && !nearPending)
                for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) if (s_since[t, r, o] >= 0f && s_since[t, r, o] < oldest) { oldest = s_since[t, r, o];at = t; ar = r; ao = o; }
                if (at >= 0)
                {
                    int before = created;
                    done = Drain(scene, at, ar, ao, cap, Math.Max(1, cap / 4), sw, budgetTicks, ref created, ref attempts) == DrainDone;
                    p_aged += created - before;
                    if (s_buckets[at, ar, ao].Count > 0) s_since[at, ar, ao] = now;
                }
                if (!ViewOrder)
                {
                    for (int w = 0; w < Waves && !done; w++)
                        for (int t = Types - 1; t >= 0 && !done; t--)
                        {
                            int r = w - (Types - 1 - t);
                            if (r < 0 || r >= Rings) continue;
                            for (int o = 0; o < Octants && !done; o++)   // octant 0 holds everything while the key is off; the rest only after the key was on this session
                                done = Drain(scene, t, r, o, cap, int.MaxValue, sw, budgetTicks, ref created, ref attempts) == DrainDone;
                        }
                }
                else
                {
                    // 1.2.257 [Optimize] StreamPacerViewOrder: ring first, then octants by angle from the camera's forward (front, the two beside it, ..., straight behind
                    // last), then type rank (terrain / solid before loose items). 16 x 8 x 4 = 512 list heads walked, no sort.
                    for (int r = 0; r < Rings && !done; r++)
                        for (int k = 0; k < Octants && !done; k++)
                        {
                            int o = (s_camOctant + OctantStep[k]) & (Octants - 1);
                            for (int t = Types - 1; t >= 0 && !done; t--)
                                done = Drain(scene, t, r, o, cap, int.MaxValue, sw, budgetTicks, ref created, ref attempts) == DrainDone;
                        }
                }
            }

            // distant objects get what is left, from a round-robin window (a settled area costs a few µs)
            if (distant != null && distant.Count > 0 && (s_mcPass ? !McOver(created, sw) : created < cap && (created == 0 || sw.ElapsedTicks < budgetTicks)))
            {
                int dn = distant.Count; if (s_distantCursor >= dn) s_distantCursor = 0;
                int steps = Math.Min(dn, DistantWindow);
                for (int k = 0; k < steps; k++)
                {
                    ZDO zdo = distant[s_distantCursor];
                    if (++s_distantCursor >= dn) s_distantCursor = 0;
                    if (s_mcPass ? McOver(created, sw) : created >= cap || ((attempts > 0 || created > 0) && sw.ElapsedTicks >= budgetTicks)) break;
                    if (zdo == null || zdo.Created) continue;
                    if (!DistantPrefabOk(zdo)) continue;   // 1.2.347: a non-distant prefab in the distant list waits for its zone like any near object
                    if (NeedsHeightmapNotThere(zdo, 2)) continue;   // 1.2.348: left in the list; the next round-robin tries again
                    if (!Fits(zdo, created, sw.ElapsedTicks, budgetTicks)) { p_heavyDeferred++; break; }
                    attempts++;
                    Create(scene, zdo, sw);
                    if (zdo.Created) { created++; p_distant++; }
                }
            }

            if (s_floorThisPass) { s_floorTokens = Mathf.Max(0f, s_floorTokens - s_passReal); p_floorPasses++; p_floorReal += s_passReal; }   // 1.2.253: only objects really made spend the rate (vanilla counts the same way; a skip returns null)
            if (s_mcPass) McEndPass(sw);
            float passMs = (float)(sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
            s_lastPassMs += passMs; LastPassFrame = Time.frameCount;
            p_frames++; p_created += created; p_passMs += passMs; if (passMs > p_passMax) p_passMax = passMs;
            if (passMs > 2f * ms && passMs > 2f * TargetFrameMs())   // 1.2.357: a pass that ran past twice its budget names what did it (Debug, at most one per OverrunLogGapS)
            {
                p_overruns++;
                if (now >= s_nextOverrunSaid)
                {
                    s_nextOverrunSaid = now + OverrunLogGapS;
                    string pname = "?"; try { var pf = scene.GetPrefab(s_passWorstPrefab); if (pf != null) pname = pf.name; } catch { }
                    EasyBakeLog.Debug($"[Stream] pass {passMs:0.0} ms over its {ms:0.0} ms budget ({(arrival ? "arrival" : Time.unscaledTime < s_graceUntil ? "post-arrival grace" : s_floorThisPass || s_mcPass ? "moving" : "still")}): {created} created in {attempts} attempt(s), slowest create '{pname}' {s_passWorstMs:0.0} ms ({(s_passWorstKnown ? "cost known before it" : "first of its kind, cost unknown")}), other work avg {Mathf.Max(0f, s_otherAvg):0.0} ms, queue {s_queued.Count}");
                }
            }
            if (s_inExtraPass) p_extra++;
            using (EbmPatchMarkers.PacerTail.Auto())   // 1.2.361: profiler marker only
            {
            using (EbmPatchMarkers.PacerSignals.Auto()) Signals(now);   // 1.2.362: profiler markers only
            using (EbmPatchMarkers.PacerFillNote.Auto()) FillNote(now, created);
            using (EbmPatchMarkers.PacerReconcile.Auto()) Reconcile(now, created);   // 1.2.264: entries no bucket holds (a pooled ZDO reused under a new id) or missing prefabs; 1.2.338: sliced, see Reconcile
            }
            if (nearList && FiresEasyBakeMeshesPlugin.StreamPacerSelfCheck != null && FiresEasyBakeMeshesPlugin.StreamPacerSelfCheck.Value && now >= s_nextCheck) { s_nextCheck = now + 10f; SelfCheck(near); }
            Report(now);
        }

        // 1.2.254: the frame measurement, split out so a frame whose extra pass is skipped still measures: the pass's cost lands on the frame after it, and
        // that frame must be the one that subtracts it and sees a spike. Once per frame: a frame runs either a pass or a skip, never both (ExtraPass returns when a pass ran).
        private static void MeasureFrame(bool arrival)
        {
            float frameMs = Time.unscaledDeltaTime * 1000f;
            float other = Mathf.Max(0f, frameMs - s_lastPassMs);
            if (s_frameAvg > 0f && frameMs > SpikeMinMs && frameMs > 2f * s_frameAvg) { p_spikes++; if (!arrival) s_backoff = BackoffFrames; }
            s_otherAvg = s_otherAvg < 0f ? other : s_otherAvg + 0.2f * (other - s_otherAvg);
            s_frameAvg = s_frameAvg < 0f ? frameMs : s_frameAvg + 0.1f * (Mathf.Min(frameMs, 4f * s_frameAvg) - s_frameAvg);
            s_lastPassMs = 0f;
            if (s_backoff > 0) s_backoff--;
        }

        // 1.2.250: test tool (off by default): the queue against vanilla's own near list; "missing" must stay near 0 (the sweep adds them within seconds)
        private static float s_nextCheck;
        private static void SelfCheck(List<ZDO> near)
        {
            int missing = 0, uncreated = 0;
            for (int i = 0; i < near.Count; i++)
            {
                ZDO zdo = near[i];
                if (zdo == null || zdo.Created || !zdo.IsValid()) continue;
                uncreated++;
                if (!s_queued.ContainsKey(zdo.m_uid) && !s_failed.Contains(zdo.m_uid)) missing++;
            }
            EasyBakeLog.Debug($"[Stream] self-check: {missing} of {uncreated} uncreated near object(s) not queued");
        }

        private static float TargetFrameMs()
        {
            if (QualitySettings.vSyncCount > 0) { int hz = Screen.currentResolution.refreshRate; if (hz > 0) return 1000f * QualitySettings.vSyncCount / hz; }
            int cap = Application.targetFrameRate;
            if (cap > 0) return 1000f / cap;
            int r = Screen.currentResolution.refreshRate;
            return r > 0 ? 1000f / Mathf.Max(30, r) : 16.7f;
        }

        // 1.2.252 (the circle test: budget avg 27-34 ms against a 6.1 ms target and passes of 34-40 ms, which is the standing-still limit): a player moved without a
        // rigidbody velocity (debug fly, a scripted walk) read as standing still. [Optimize] StreamPacerMotionByPosition: position change counts too, held 0.5 s so one still frame does not flip it.
        private const float MovingHoldS = 0.5f;
        private static Vector3 s_motionPos; private static bool s_motionHave; private static int s_motionFrame = -1; private static float s_movingUntil;
        private static int p_still, p_moving; private static float p_memberMax;
        private static int p_realCapPasses, p_focused, p_unfocused, p_boosted;
        private static bool BoostOnlyWhenThrottled => FiresEasyBakeMeshesPlugin.StreamPacerUnfocusedBoostOnlyWhenThrottled == null || FiresEasyBakeMeshesPlugin.StreamPacerUnfocusedBoostOnlyWhenThrottled.Value;
        private static bool RealCostCap => FiresEasyBakeMeshesPlugin.StreamPacerRealCostCap != null && FiresEasyBakeMeshesPlugin.StreamPacerRealCostCap.Value;

        private static bool Still(Player me)
        {
            if (me == null) return true;
            bool still = StillNow(me);
            if (still) p_still++; else p_moving++;
            MotionProbe(me, still);
            return still;
        }

        // 1.2.254: the answer without the budget counters, so ExtraPass can ask too; the position sample stays once per frame whoever asks first
        // 1.2.256 [Optimize] StreamPacerMotionFromReference (default on, a fix of the motion test): the moving circles logged "budget from: 0 moving" on every window although the
        // player covered 10 m/s, so the position the old test read did not change between our frames. ZNet's reference position is what decides which zones and objects
        // load (in normal play it is the player's own position), so motion is judged from it. Off = the old player-position test (1.2.252), for the A/B.
        private static bool FromReference => FiresEasyBakeMeshesPlugin.StreamPacerMotionFromReference == null || FiresEasyBakeMeshesPlugin.StreamPacerMotionFromReference.Value;
        // next EBM ([lead] GO 11-22, O_20261122_080519: spot 1 is in water, "in water True" in Vedr's [WaterProbe]; the player treading water at fixed x/z
        // bobbed y 21-27 at up to 9.6 m/s and the 3D test below flipped moving / standing 93 times in ~4 min). On: motion is judged on x/z only. Off = today.
        private static bool Horizontal => FiresEasyBakeMeshesPlugin.StreamPacerMotionHorizontal != null && FiresEasyBakeMeshesPlugin.StreamPacerMotionHorizontal.Value;
        private static float s_vSpeedNow;
        private static float s_speedNow; private static double p_speedSum; private static int p_speedN; private static float p_speedMax;

        // 1.2.355: the far tier asks the same stillness test (velocity + reference-position motion), one shared answer per frame
        internal static bool LocalPlayerStill => StillNow(Player.m_localPlayer);
        private static bool StillNow(Player me)
        {
            if (me == null) return true;
            bool fromRef = FromReference && ZNet.instance != null;
            bool byPosition = fromRef || (FiresEasyBakeMeshesPlugin.StreamPacerMotionByPosition != null && FiresEasyBakeMeshesPlugin.StreamPacerMotionByPosition.Value);
            float now = Time.unscaledTime;
            if (byPosition && s_motionFrame != Time.frameCount)   // once a frame, whoever asks first; sampled before the velocity test so the speed figure is always measured
            {
                s_motionFrame = Time.frameCount;
                Vector3 pos = fromRef ? ZNet.instance.GetReferencePosition() : me.transform.position;
                float dt = Mathf.Max(1e-4f, Time.unscaledDeltaTime);
                if (s_motionHave)
                {
                    Vector3 step = pos - s_motionPos;
                    s_vSpeedNow = Mathf.Abs(step.y) / dt;   // next EBM: the probe's vertical figure, measured whatever the key says
                    if (Horizontal) step.y = 0f;   // next EBM [20] StreamPacerMotionHorizontal: loading is by x/z only (zones, sectors, far rings)
                    float moved = step.magnitude;
                    s_speedNow = moved / dt;
                    if (moved < 50f) { p_speedSum += s_speedNow; p_speedN++; if (s_speedNow > p_speedMax) p_speedMax = s_speedNow; }   // a teleport is not a speed
                    if (moved > dt * StillSpeed) s_movingUntil = now + MovingHoldS;
                }
                s_motionPos = pos; s_motionHave = true;
            }
            Vector3 vel = me.GetVelocity();
            if (Horizontal) vel.y = 0f;   // next EBM: a swim bob / jump / fall is not motion the loader cares about
            if (vel.sqrMagnitude >= StillSpeed * StillSpeed) return false;
            if (!byPosition) return true;
            return now >= s_movingUntil;
        }

        // 1.2.256: the first 60 s after a join, one Debug line a second with what the motion test sees, so one run shows which position moves (proof for the fix above)
        private static float s_probeStart = -1f, s_probeNext;
        private static void MotionProbe(Player me, bool still)
        {
            if (me == null || !EasyBakeLog.DebugOn()) return;
            float now = Time.unscaledTime;
            if (s_probeStart < 0f) { s_probeStart = now; s_probeNext = now; }
            if (now - s_probeStart > 60f || now < s_probeNext) return;
            s_probeNext = now + 1f;
            Vector3 p = me.transform.position, r = ZNet.instance != null ? ZNet.instance.GetReferencePosition() : Vector3.zero;
            EasyBakeLog.Debug($"[Stream] motion probe t+{now - s_probeStart:0}s: player {p.x:0.0},{p.y:0.0},{p.z:0.0}, body speed {me.GetVelocity().magnitude:0.0} m/s, reference {r.x:0.0},{r.y:0.0},{r.z:0.0}, measured {s_speedNow:0.0} m/s ({(FromReference ? "from the reference position" : "from the player position")}{(Horizontal ? ", x/z only" : ", 3D")}; body horizontal {new Vector2(me.GetVelocity().x, me.GetVelocity().z).magnitude:0.0} / vertical {Mathf.Abs(me.GetVelocity().y):0.0} m/s, measured vertical {s_vSpeedNow:0.0} m/s), verdict {(still ? "still" : "moving")}, debug fly {me.InDebugFlyMode()}, riding a ship {me.IsAttachedToShip()}, focused {Application.isFocused}");
        }

        private static float Budget(bool arrival, out int cap)
        {
            float other = Mathf.Max(0f, s_otherAvg), ms;
            bool floorAsked = false, floorStill = false;
            // 1.2.358 ([visual], ship rain-arrival pair 11-13 09:0x, 1350 A1 vs 1357 B2: the 1357 grace ran 204 passes and the post-fade CreateObjects frames stayed
            // 85-117 ms in both arms; B2's first 30 s block: "budget from: 13 moving, 1049 still" and "moving cap on: 2659 floor / 30 capped create(s) over 13 pass(es)"):
            // the landing itself reads as motion for MovingHoldS (StillNow: the teleport displacement sets s_movingUntil, plus any landing velocity), so the first
            // ~0.5 s of passes took the moving-cap / vanilla-floor branches below, whose floor is Max(queued / 100, 10) per tick with no time cap: ~205 creates a
            // pass against an 11-13k queue, and the still-branch grace never saw them. While the grace is on, a "moving" player is a landing, not a walk: the
            // moving branches are skipped and the under-target budget applies; after ArrivalGraceSeconds the moving rules return unchanged.
            bool graceNow = !arrival && Time.unscaledTime < s_graceUntil;
            // 1.2.257: StreamPacerMovingCap supersedes StreamPacerVanillaFloor while it is on (the 1.2.253 branch is not reached; both use the same token count, never twice)
            if (!arrival && !graceNow && MovingCapOn)
            {
                floorStill = Still(Player.m_localPlayer); floorAsked = true;
                if (!floorStill) return MovingCapBudget(other, out cap);
            }
            else if (!arrival && !graceNow && FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor != null && FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor.Value)
            {
                floorStill = Still(Player.m_localPlayer); floorAsked = true;
                if (!floorStill)
                {
                    if (!s_saidFloor) { s_saidFloor = true; EasyBakeLog.Info("[Stream] vanilla-rate floor on: while moving, creates follow vanilla's rate per second plus a share of the measured frame headroom"); }
                    return MovingBudget(other, out cap);
                }
            }
            if (arrival) { ms = Mathf.Clamp(ArrivalTargetMs - other, ArrivalMinMs, ArrivalMaxMs); }
            else if (s_backoff > 0) ms = BaseMs;
            else
            {
                float target = TargetFrameMs(), headroom = target - other;
                var me = Player.m_localPlayer;
                bool still = floorAsked ? floorStill : Still(me);
                bool grace = graceNow;   // 1.2.357/1.2.358: just arrived (standing or the landing's own motion): the moving formula, headroom under the target
                if (grace) p_gracePasses++;
                ms = still && !grace ? Mathf.Clamp(Mathf.Max(headroom * 0.9f, Mathf.Max(target * 0.5f, NoHitchMs - other)), BaseMs, Mathf.Max(BaseMs, Mathf.Min(StillMaxMs, NoHitchMs - other)))
                           : Mathf.Clamp(headroom * 0.9f, BaseMs, Mathf.Max(BaseMs, target));
            }
            if (!Application.isFocused)
            {
                // 1.2.256 [Optimize] StreamPacerUnfocusedBoostOnlyWhenThrottled (default on): the 40 ms ceiling exists so the world keeps loading while the game is
                // alt-tabbed and throttled. An unfocused game still rendering at full rate (second monitor, a chat window in front) keeps its normal budget.
                // Throttled = the frame's work OTHER than our own create passes (smoothed) is over twice the target frame, or the game is not set to run in the background.
                // Measured without our passes on purpose: a raised 40 ms pass lengthens the whole frame and would otherwise keep itself raised.
                p_unfocused++;
                bool throttled = !Application.runInBackground || other > 2f * TargetFrameMs();
                if (!BoostOnlyWhenThrottled || throttled) { ms = Mathf.Max(ms, UnfocusedMs); p_boosted++; }
            }
            else p_focused++;
            float per = s_costAvg > 0f ? s_costAvg : 0.2f;
            // 1.2.256 [Optimize] StreamPacerRealCostCap (default off): a standing-still pass counts its object cap at the measured cost of a REAL create; the average also holds
            // skips, which cost almost nothing, so it allowed about ten times the creates the time would hold. The time ceiling is unchanged.
            if (!arrival && RealCostCap && s_realCostAvg > per) { per = s_realCostAvg; p_realCapPasses++; }
            cap = Math.Max(MinPerPass, Math.Min(MaxCap, (int)(ms / per) + 1));
            if (arrival) cap = Math.Max(cap, ArrivalCap);
            return ms;
        }

        // 1.2.253 [Optimize] StreamPacerVanillaFloor, moving only. The floor is a rate per second kept as tokens, so frame rate cannot multiply it: vanilla's
        // Max(pending / 100, 10) per 30 Hz tick (ZNetScene.CreateObjectsSorted), or MinPerPass read as a per-second rate if that is higher. On top, a share of the
        // measured headroom, once per frame; no headroom (or a spike's backoff) leaves the tokens alone. A known-heavy prefab still goes alone (Fits).
        private const float VanillaTickHz = 30f, FloorBankSeconds = 0.1f, MovingHeadroomShare = 0.5f;
        private const int VanillaFloorPerTick = 10;
        private static float s_floorTokens, s_floorAt = -1f, s_realCostAvg = -1f;
        private static int s_floorFrame = -1, s_passReal, p_floorPasses, p_floorReal;
        private static bool s_floorThisPass, s_saidFloor;

        private static float MovingBudget(float other, out int cap)
        {
            float now = Time.unscaledTime;
            float dt = s_floorAt < 0f ? 0f : Mathf.Clamp(now - s_floorAt, 0f, FloorBankSeconds);
            s_floorAt = now;
            float perSecond = Mathf.Max(Math.Max(s_queued.Count / 100, VanillaFloorPerTick) * VanillaTickHz, MinPerPass);
            s_floorTokens = Mathf.Min(s_floorTokens + perSecond * dt, perSecond * FloorBankSeconds);

            float per = s_costAvg > 0f ? s_costAvg : 0.2f;
            float target = TargetFrameMs(), headroomMs = 0f;
            if (s_backoff == 0 && s_floorFrame != Time.frameCount) headroomMs = Mathf.Clamp((target - other) * MovingHeadroomShare, 0f, target);
            s_floorFrame = Time.frameCount;

            int floorCreates = (int)s_floorTokens;
            cap = Math.Min(MaxCap, floorCreates + (int)(headroomMs / per));
            s_floorThisPass = true;
            return headroomMs + floorCreates * (s_realCostAvg > 0f ? s_realCostAvg : Mathf.Max(per, 0.2f));   // the floor's tokens are real creates, so its time is priced at a real create
        }

        // 1.2.257 [Optimize] StreamPacerMovingCap (default off), moving only (standing still and arrival unchanged). Two parts:
        //  (a) the vanilla floor, uncapped: tokens accrue at vanilla's own rate, Max(queued / 100, 10) per 30 Hz tick (ZNetScene.CreateObjectsSorted / CreateObjects),
        //      on elapsed time with a FloorBankSeconds bank. Floor creates go first each pass with no time limit; only REAL creates spend a token (a skip returns null, free).
        //  (b) creates above the floor get a per-frame time cap C = max(CapMinMs, CapShare x measured headroom (target - other work)); CapMinMs during a spike's backoff.
        //      Unused cap goes to a bank of CapBankSeconds worth of C, so cheap frames catch up; one frame may spend at most max(C, its full headroom) from the bank.
        //      The cap is per FRAME: the tick pass and any extra pass of the same frame draw on one allowance.
        //  A known-heavy prefab still goes alone (Fits, against floor time at the real create cost + the extra allowance). Supersedes StreamPacerVanillaFloor while on.
        private const float CapMinMs = 1f, CapShare = 0.5f, CapBankSeconds = 0.2f;
        // 1.2.358 ([lead] 11-13): the floor phase had no time cap at all ("real creates are uncapped"), so any walk that starts against a big queue (an arrival,
        // a portal, a boat landing) could spend ~200 creates and 100 ms in one pass. The floor phase now ends at FloorCapTargets x the target frame; the floor
        // creates not made are carried to the next pass (owed, on top of its tokens), and a cut is logged (Debug, one per 2 s) and counted.
        private const float FloorCapTargets = 2f;
        private static long s_mcFloorCapTicks; private static int s_mcFloorCarry, p_mcFloorCuts, p_mcFloorCarried; private static float s_nextFloorCutSaid; private static bool s_mcFloorCut;
        private static bool MovingCapOn => FiresEasyBakeMeshesPlugin.StreamPacerMovingCap != null && FiresEasyBakeMeshesPlugin.StreamPacerMovingCap.Value;
        private static float s_capBankMs, s_capAt = -1f, s_capFrameLeftMs; private static int s_capFrame = -1;
        private static bool s_mcPass, s_saidMc; private static long s_mcExtraStart = -1, s_mcExtraTicks; private static int s_mcFloor;
        private static int p_mcPasses, p_mcFloor, p_mcCapped;

        private static float MovingCapBudget(float other, out int cap)
        {
            if (!s_saidMc) { s_saidMc = true; EasyBakeLog.Info($"[Stream] moving cap on: while you move, objects are created at least at the game's own rate, and extra ones only within a per-frame time cap (min {CapMinMs:0.0} ms, {CapBankSeconds:0.00} s bank)" + (FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor != null && FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor.Value ? "; StreamPacerVanillaFloor is set too and stands aside while this is on" : "")); }
            float now = Time.unscaledTime;
            float dt = s_floorAt < 0f ? 0f : Mathf.Clamp(now - s_floorAt, 0f, FloorBankSeconds);
            s_floorAt = now;
            float perSecond = Math.Max(s_queued.Count / 100, VanillaFloorPerTick) * VanillaTickHz;
            s_floorTokens = Mathf.Min(s_floorTokens + perSecond * dt, perSecond * FloorBankSeconds);

            float target = TargetFrameMs(), headroom = Mathf.Max(0f, target - other);
            if (s_capFrame != Time.frameCount)   // once a frame, whichever pass comes first
            {
                s_capFrame = Time.frameCount;
                float c = s_backoff > 0 ? CapMinMs : Mathf.Max(CapMinMs, headroom * CapShare);
                float cdt = s_capAt < 0f ? 0f : Mathf.Clamp(now - s_capAt, 0f, CapBankSeconds);   // on elapsed time, so a frame without a pass (extra passes off) still accrues
                s_capAt = now;
                s_capBankMs = Mathf.Min(s_capBankMs + c * (cdt * 1000f / target), c * Mathf.Max(1f, CapBankSeconds * 1000f / target));
                if (s_capBankMs < c && cdt <= 0f) s_capBankMs = c;   // the first moving frame
                s_capFrameLeftMs = Mathf.Min(s_capBankMs, s_backoff > 0 ? CapMinMs : Mathf.Max(c, headroom));
            }
            s_mcFloor = (int)s_floorTokens + s_mcFloorCarry; s_mcFloorCarry = 0; s_mcFloorCut = false;   // 1.2.358: creates owed from a cut floor phase go first
            s_mcFloorCapTicks = (long)(FloorCapTargets * target * Stopwatch.Frequency / 1000.0);
            s_mcExtraTicks = (long)(s_capFrameLeftMs * Stopwatch.Frequency / 1000.0);
            s_mcPass = true; p_mcPasses++;
            cap = MaxCap;
            float real = s_realCostAvg > 0f ? s_realCostAvg : Mathf.Max(s_costAvg > 0f ? s_costAvg : 0.2f, 0.2f);
            return s_capFrameLeftMs + s_mcFloor * real;   // budgetTicks for Fits and the budget average; the stop rule is McOver
        }

        // the stop rule of a moving-cap pass: floor first (until s_mcFloor real creates), then the extra allowance timed from where the floor ended
        private static bool McOver(int created, Stopwatch sw)
        {
            if (created >= MaxCap) return true;
            // floor phase: real creates are uncapped, but skips spend no token, so a queue of skips (a dedicated-server client) must not run without end: after
            // 4 skips per floor token (+64) the extra allowance's clock starts, as for any other create
            if (s_passReal < s_mcFloor && created - s_passReal < 4 * s_mcFloor + 64)
            {
                if (sw.ElapsedTicks < s_mcFloorCapTicks) return false;
                s_mcFloorCut = true; return true;   // 1.2.358: the floor phase ran past its time cap; McEndPass carries the rest
            }
            long e = sw.ElapsedTicks;
            if (s_mcExtraStart < 0) s_mcExtraStart = e;
            return e - s_mcExtraStart >= s_mcExtraTicks;
        }

        private static void McEndPass(Stopwatch sw)
        {
            int floorReal = Math.Min(s_passReal, s_mcFloor);
            s_floorTokens = Mathf.Max(0f, s_floorTokens - floorReal);
            p_mcFloor += floorReal; p_mcCapped += s_passReal - floorReal;
            if (s_mcFloorCut)   // 1.2.358
            {
                int owed = Math.Min(MaxCap, s_mcFloor - floorReal);
                s_mcFloorCarry = owed; p_mcFloorCuts++; p_mcFloorCarried += owed;
                float now = Time.unscaledTime;
                if (now >= s_nextFloorCutSaid)
                {
                    s_nextFloorCutSaid = now + 2f;
                    EasyBakeLog.Debug($"[Stream] floor phase cut at {sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency:0.0} ms (cap {FloorCapTargets:0}x the {TargetFrameMs():0.0} ms target): {floorReal} of {s_mcFloor} floor create(s) made, {owed} carried to the next pass, queue {s_queued.Count}");
                }
            }
            if (s_mcExtraStart >= 0)
            {
                float spent = (float)((sw.ElapsedTicks - s_mcExtraStart) * 1000.0 / Stopwatch.Frequency);
                s_capFrameLeftMs = Mathf.Max(0f, s_capFrameLeftMs - spent);
                s_capBankMs = Mathf.Max(0f, s_capBankMs - spent);
            }
            s_mcPass = false;
        }

        // 1.2.257 [Optimize] StreamPacerViewOrder (default off). Octants are world-fixed (direction from the reference position when the object is queued), so a turn
        // costs nothing: only the walk order changes, starting at the camera's octant. A zone change reseeds and re-buckets from the new position as before.
        private static bool ViewOrder => FiresEasyBakeMeshesPlugin.StreamPacerViewOrder != null && FiresEasyBakeMeshesPlugin.StreamPacerViewOrder.Value;
        private static readonly int[] OctantStep = { 0, 1, -1, 2, -2, 3, -3, 4 };
        private static Vector3 s_viewRef; private static Vector2 s_camFwd; private static bool s_haveFwd; private static int s_camOctant;

        private static bool s_keepUpLog;   // review 1.2.257 ([generator]): keep-up stamps / behind counts only while Debug is on
        private static void ViewSetup()
        {
            bool log = EasyBakeLog.DebugOn();
            if (!log && s_keepUpLog) s_enqAt.Clear();
            s_keepUpLog = log;
            s_viewRef = ZNet.instance.GetReferencePosition();
            var gc = GameCamera.instance; var me = Player.m_localPlayer;
            Vector3 f = gc != null ? gc.transform.forward : me != null ? me.transform.forward : Vector3.zero;
            s_camFwd = new Vector2(f.x, f.z);
            s_haveFwd = s_camFwd.sqrMagnitude > 1e-4f;
            if (s_haveFwd) { s_camFwd.Normalize(); s_camOctant = OctantOfDir(s_camFwd.x, s_camFwd.y); }
        }

        private static int OctantOf(Vector3 p) => OctantOfDir(p.x - s_viewRef.x, p.z - s_viewRef.z);
        private static int OctantOfDir(float dx, float dz)
            => ((int)Mathf.Floor((Mathf.Atan2(dz, dx) + Mathf.PI) * (Octants / (2f * Mathf.PI)) + 0.5f)) & (Octants - 1);

        // 1.2.257 keep-up logging: enqueue stamps (bounded by the queue; pruned on reseed), wait histogram (fixed, no allocation)
        private static readonly Dictionary<ZDOID, float> s_enqAt = new Dictionary<ZDOID, float>();
        private static readonly List<ZDOID> s_tmpIds = new List<ZDOID>();
        private static readonly float[] WaitEdges = { 0.1f, 0.25f, 0.5f, 1f, 2f, 3f, 5f, 7.5f, 10f, 15f, 20f, 30f, 60f };
        private static readonly int[] p_waitBins = new int[14];
        private static int p_waitN, p_behind, s_qPrev, s_growRun; private static float p_waitMax;

        private static void PruneStamps()
        {
            s_tmpIds.Clear();
            foreach (var kv in s_enqAt) if (!s_queued.ContainsKey(kv.Key)) s_tmpIds.Add(kv.Key);
            for (int i = 0; i < s_tmpIds.Count; i++) s_enqAt.Remove(s_tmpIds[i]);
            s_tmpIds.Clear();
        }

        private static void RecordWait(float s)
        {
            if (s < 0f) s = 0f;
            int i = 0; while (i < WaitEdges.Length && s > WaitEdges[i]) i++;
            p_waitBins[i]++; p_waitN++; if (s > p_waitMax) p_waitMax = s;
        }

        private static string WaitP95()
        {
            if (p_waitN == 0) return "-";
            int need = (int)Math.Ceiling(p_waitN * 0.95), sum = 0;
            for (int i = 0; i < p_waitBins.Length; i++) { sum += p_waitBins[i]; if (sum >= need) return i < WaitEdges.Length ? "<= " + WaitEdges[i].ToString("0.##") + " s" : "> 60 s"; }
            return "> 60 s";
        }

        private const int DrainEmpty = 0, DrainDone = 1, DrainShare = 2;

        private static int Drain(ZNetScene scene, int t, int r, int o, int cap, int share, Stopwatch sw, long budgetTicks, ref int created, ref int attempts)
        {
            List<ZDO> b = s_buckets[t, r, o];
            int start = created;
            while (b.Count > 0)
            {
                if (s_mcPass ? McOver(created, sw) : created >= cap || (attempts > 0 && sw.ElapsedTicks >= budgetTicks)) return DrainDone;
                if (created - start >= share) return DrainShare;
                ZDO zdo = b[b.Count - 1];
                b.RemoveAt(b.Count - 1);
                if (zdo == null || !s_queued.TryGetValue(zdo.m_uid, out ZDO q) || !ReferenceEquals(q, zdo)) continue;   // pooled and reused, or re-added
                Vector2s sec = zdo.GetSector();
                if (zdo.Created || !zdo.IsValid() || !InNear(sec)) { Untrack(zdo); continue; }
                if (!ZoneSystem.instance.IsZoneReadyForType(sec, zdo.Type)) { Wait(sec, zdo); continue; }
                if (!Fits(zdo, created, sw.ElapsedTicks, budgetTicks)) { b.Add(zdo); p_heavyDeferred++; return DrainDone; }   // a known-heavy one goes first next pass
                if (NeedsHeightmapNotThere(zdo, 1)) { Wait(sec, zdo); continue; }   // 1.2.348: a terrain compiler waits for its heightmap, whatever list it came from
                float enq = 0f; bool stamped = s_keepUpLog && s_enqAt.TryGetValue(zdo.m_uid, out enq);   // 1.2.257 logging: read before Untrack drops it
                Untrack(zdo); attempts++;
                bool real = Create(scene, zdo, sw);
                if (real && stamped) RecordWait(Time.unscaledTime - enq);
                if (zdo.Created) created++;                       // EBM's skip sets Created with a null instance: done too
                else s_failed.Add(zdo.m_uid);                     // invalid prefab: parked, never holds a zone
            }
            s_since[t, r, o] = -1f;
            return DrainEmpty;
        }

        private static bool Create(ZNetScene scene, ZDO zdo, Stopwatch sw)
        {
            long t0 = sw.ElapsedTicks;
            bool real;
            using ((s_prefabCost.ContainsKey(zdo.GetPrefab()) ? EbmPatchMarkers.PacerCreateKnown : EbmPatchMarkers.PacerCreateFirst).Auto())   // 1.2.361: profiler marker only (a first-of-kind create is the one Fits cannot hold back)
                real = s_createObject(scene, zdo) != null;
            if (!real && zdo.Created) ZoneTracker.ConsumeJustSkipped(zdo);   // 1.2.318 (found in passing): the pacer never destroys, so a host skip's one-shot must not stay armed and block a later REAL delete of that piece
            if (real)
            {
                s_passReal++;
                if (s_keepUpLog && s_haveFwd) { Vector3 p = zdo.GetPosition(); if ((p.x - s_viewRef.x) * s_camFwd.x + (p.z - s_viewRef.z) * s_camFwd.y < 0f) p_behind++; }   // 1.2.257 logging: over 90 degrees from the camera's forward
            }
            long ticks = sw.ElapsedTicks - t0;
            int prefab = zdo.GetPrefab();
            bool known = s_prefabCost.TryGetValue(prefab, out long old);   // 1.2.357: read once, for the cost smoothing and the overrun line
            if (ticks > (long)(s_passWorstMs * Stopwatch.Frequency / 1000.0)) { s_passWorstMs = (float)(ticks * 1000.0 / Stopwatch.Frequency); s_passWorstPrefab = prefab; s_passWorstKnown = known; }
            s_prefabCost[prefab] = known ? (old * 3 + ticks) / 4 : ticks;
            float ms = (float)(ticks * 1000.0 / Stopwatch.Frequency);
            if (zdo.Created && ms > 0f && ms <= 100f) s_costAvg = s_costAvg < 0f ? ms : s_costAvg + 0.05f * (ms - s_costAvg);   // one monster create says nothing about the next
            if (real && ms > 0f && ms <= 100f) s_realCostAvg = s_realCostAvg < 0f ? ms : s_realCostAvg + 0.05f * (ms - s_realCostAvg);   // 1.2.253: skips are far cheaper, and would shrink the floor's time below its rate
            return real;
        }

        private static bool Fits(ZDO zdo, int created, long elapsed, long budgetTicks)
            => created == 0 || !s_prefabCost.TryGetValue(zdo.GetPrefab(), out long cost) || elapsed + cost <= budgetTicks;

        // ================================================================== membership
        private static bool InNear(Vector2s sector)
        {
            int ring = Math.Max(Math.Abs(sector.x - s_refZone.x), Math.Abs(sector.y - s_refZone.y));
            if (ring == 0) return true;
            if (ring > s_sd.NearSimulationDistance) return false;
            return s_sd.IsClassic || ZoneSystem.instance.ZonesWithinRadius(s_refZone, sector, s_sd.NearSimulationDistance, false);
        }

        private static int RingOf(Vector2s sec) => Math.Max(Math.Abs(sec.x - s_refZone.x), Math.Abs(sec.y - s_refZone.y));

        // ── 1.2.427 [Optimize] StreamPacerCompilersFirst ([worldgen]'s proof on Fire's log: the zone heightmaps drew early but their _TerrainCompiler objects came 5-50 s
        // later for the ring zones and up to 715 s for some): (a) the near drain below waits for vanilla's whole-area gate (IsActiveAreaLoaded, as ZNetScene.cs:199), and
        // (b) once open, compilers sat in the type/ring buckets behind the town's object flood. Now every queued compiler goes to its own lane, drained FIRST each pass,
        // nearest first, whether or not the whole area is loaded, as soon as ITS zone's ground exists (zone loaded, ready for Terrain, Heightmap.FindHeightmap at its
        // position: the test TerrainComp.Awake needs). Same create call, same budget clock (it spends the pass's budget first); everything else is unchanged.
        private static readonly List<ZDO> s_compilers = new List<ZDO>();
        private static bool s_compilersDirty;
        private static int p_compilersFirst; private static bool s_saidCompilers;
        private static bool CompilersFirst => FiresEasyBakeMeshesPlugin.StreamPacerCompilersFirst == null || FiresEasyBakeMeshesPlugin.StreamPacerCompilersFirst.Value;
        private static void DrainCompilers(ZNetScene scene, Stopwatch sw, long budgetTicks, ref int created, ref int attempts)
        {
            if (s_compilers.Count == 0) return;
            if (s_compilersDirty)
            {
                s_compilersDirty = false;
                for (int i = 0; i < s_compilers.Count; i++) { var z = s_compilers[i]; if (z != null) z.m_tempSortValue = Utils.DistanceSqr(s_viewRef, z.GetPosition()); }
                s_compilers.Sort((a, b) => a == null ? 1 : b == null ? -1 : b.m_tempSortValue.CompareTo(a.m_tempSortValue));   // farthest first, so the nearest is at the end (popped first)
            }
            var zs = ZoneSystem.instance;
            int made = 0;
            for (int i = s_compilers.Count - 1; i >= 0; i--)
            {
                if (attempts > 0 && sw.ElapsedTicks >= budgetTicks) break;
                ZDO zdo = s_compilers[i];
                if (zdo == null || !s_queued.TryGetValue(zdo.m_uid, out ZDO q) || !ReferenceEquals(q, zdo)) { s_compilers.RemoveAt(i); continue; }   // reused or untracked
                Vector2s sec = zdo.GetSector();
                if (zdo.Created || !zdo.IsValid() || !InNear(sec)) { Untrack(zdo); s_compilers.RemoveAt(i); continue; }
                if (!zs.IsZoneLoaded(ZoneSystem.GetZone(zdo.GetPosition())) || !zs.IsZoneReadyForType(sec, zdo.Type)) continue;   // its ground is not there yet: stays in the lane
                if (Heightmap.FindHeightmap(zdo.GetPosition()) == null) continue;
                s_compilers.RemoveAt(i);
                Untrack(zdo); attempts++;
                Create(scene, zdo, sw);
                if (zdo.Created) { created++; made++; } else s_failed.Add(zdo.m_uid);
            }
            p_compilersFirst += made;
            if (made > 0 && !s_saidCompilers) { s_saidCompilers = true; EasyBakeLog.Info($"[Stream] terrain edits first (StreamPacerCompilersFirst): {made} zone compiler(s) created ahead of other objects{(zs.IsActiveAreaLoaded() ? "" : " while the rest of the area still loads")}; {s_compilers.Count} still waiting for their ground"); }
        }

        private static void Bucket(ZDO zdo, float now, Vector2s? sector = null)
        {
            if (CompilersFirst && zdo.GetPrefab() == TerrainCompilerHash) { s_compilers.Add(zdo); s_compilersDirty = true; return; }   // 1.2.427: its own lane
            int ring = Math.Min(Rings - 1, RingOf(sector ?? zdo.GetSector()));
            int type = Mathf.Clamp((int)zdo.Type, 0, Types - 1);
            int oct = ViewOrder ? OctantOf(zdo.GetPosition()) : 0;
            List<ZDO> b = s_buckets[type, ring, oct];
            if (b.Count == 0 || s_since[type, ring, oct] < 0f) s_since[type, ring, oct] = now;
            b.Add(zdo);
        }

        private static bool TryAdd(ZDO zdo, float now, Vector2s? sector = null)   // 1.2.265: sector = the one vanilla is moving it into (see SectorOf)
        {
            if (zdo == null || zdo.Created || !zdo.IsValid()) return false;
            ZDOID id = zdo.m_uid;
            if (s_queued.ContainsKey(id) || s_failed.Contains(id)) return false;
            Vector2s sec = sector ?? zdo.GetSector();
            Bucket(zdo, now, sec);
            s_queued[id] = zdo; s_addSector[id] = sec;
            if (s_keepUpLog && !s_enqAt.ContainsKey(id)) s_enqAt[id] = now;   // 1.2.257 logging: first enqueue kept across a reseed
            s_pendingByZone.TryGetValue(sec, out int c); s_pendingByZone[sec] = c + 1;
            if (c == 0 && !s_filling.ContainsKey(sec)) { s_filling[sec] = now; EbmApi.RaiseZoneFilling(sec); }
            return true;
        }

        // ── 1.2.264 (lead 11-10, [ghost]'s town logs: "queue 41 (0 zone(s) not ready)", "0 created over 1667 pass(es)", so HasWork stayed true
        // and EbmObjectArea's idle skip never engaged). Failed creates were already parked (s_failed), so these 41 were never ATTEMPTED: entries
        // in s_queued that no bucket holds any more. The likely way in: s_queued keeps the ZDO object, and the game pools ZDOs; a queued ZDO
        // destroyed and reused before its turn is popped from its bucket under its NEW id, the lookup misses and the entry is skipped (Drain),
        // so its OLD id stays queued for good. Reconcile, at most every 5 s and only after a pass that created nothing with work queued:
        //  - an entry whose ZDO now carries another id, is invalid or is created: dropped;
        //  - an entry whose prefab this client lacks (another mod's object): parked in s_failed, one Info line per prefab hash;
        //  - a live entry that no bucket holds: put back in its bucket.
        // The first time it acts it logs what it found (up to 12 prefab names), so the census names the stuck objects.
        private static float s_nextReconcile;
        private static bool s_saidReconcile, s_saidReconMode;
        private static readonly HashSet<ZDO> s_inBucket = new HashSet<ZDO>();
        private static readonly List<ZDOID> s_dropIds = new List<ZDOID>();
        private static readonly HashSet<int> s_missingSaid = new HashSet<int>();
        private static int p_reconDropped, p_reconMissing, p_reconRebucketed;
        // 1.2.338 ([visual], lead 11-12; Fire: "the hitching every 6 or 7 seconds ... top priority"): the whole-queue walk below WAS that hitch. Deep profiler
        // on the full set ((rig path), frame 13: StreamPacer.Reconcile 3,856 ms deep, Object.op_Equality + HashSet.GetHashCode
        // per entry): every ~5-7 s one CreateDestroyObjects pass took ~50 ms of pure C# (TrinityDriver census: CDO max 49.5-55 ms outside its three inner
        // calls; FDT stamp: "ZNetScene.Update 50-61 ms, vanilla + untimed"). The walk is O(queue + buckets) in ONE pass, and the full set keeps tens of
        // thousands queued (why is the second question, see the ticket), while the perf-only set drained its queue between bursts and never showed it.
        // Fix ([Optimize] StreamPacerReconcileSliced, ON): the same reconcile, as a CYCLE that runs in slices of at most ReconcileSliceMs per create pass and
        // resumes where it stopped: phase 1 collects the bucket membership (the HashSet build, the heavier half), phase 2 walks a key snapshot of s_queued
        // (re-read per key, so an entry created or untracked meanwhile is simply skipped). Same decisions as before per entry (stale / missing prefab /
        // re-bucket); a reseed (ClearQueue) aborts the cycle. An entry bucketed after phase 1 can be re-bucketed once more by phase 2 (a duplicate the
        // Drain already tolerates: the second pop finds it gone from s_queued). The 5 s spacing is kept between cycle END and the next start.
        private const float ReconcileSliceMs = 2f;
        private static int s_reconPhase;              // 0 idle, 1 collecting bucket membership, 2 walking the key snapshot
        private static int s_reconBucket, s_reconBucketPos, s_reconKeyPos;
        private static readonly List<ZDOID> s_reconKeys = new List<ZDOID>();
        private static List<string> s_reconNames;
        private static int s_reconDropped, s_reconMissing, s_reconRebucketed;
        private static readonly Stopwatch s_reconWatch = new Stopwatch();
        private static int p_reconCalls, p_reconWalked, p_reconCycles; private static double p_reconMs, p_reconWorst;
        private static bool ReconcileSliced => FiresEasyBakeMeshesPlugin.StreamPacerReconcileSliced == null || FiresEasyBakeMeshesPlugin.StreamPacerReconcileSliced.Value;

        private static int BucketEntries() { int n = 0; for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) n += s_buckets[t, r, o].Count; return n; }   // 1.2.338 census: how much the reconcile walks besides the queue

        private static void ReconcileAbort() { s_reconPhase = 0; s_inBucket.Clear(); s_reconKeys.Clear(); s_reconNames = null; }

        // called after EVERY pass: starts a cycle when due (a pass that created nothing with work queued, 5 s after the last cycle), else continues one
        private static void Reconcile(float now, int created)
        {
            if (!ReconcileSliced)
            {
                if (!s_saidReconMode) { s_saidReconMode = true; EasyBakeLog.Info("[Stream] queue reconcile: the whole-queue walk every 5 s (StreamPacerReconcileSliced is off; the 1.2.338 fix for the 6-7 s hitch is not running)"); }
                if (created == 0 && s_queued.Count > 0) ReconcileWhole(now);
                return;
            }
            if (s_reconPhase == 0)
            {
                if (created != 0 || s_queued.Count == 0 || now < s_nextReconcile) return;
                if (!s_saidReconMode) { s_saidReconMode = true; EasyBakeLog.Info($"[Stream] queue reconcile runs in slices of at most {ReconcileSliceMs:0} ms per create pass (1.2.338: the whole-queue walk every 5 s was the 6-7 s hitch when the queue stays large; same checks, spread over frames)"); }
                s_reconPhase = 1; s_reconBucket = 0; s_reconBucketPos = 0; s_reconKeyPos = 0;
                s_inBucket.Clear(); s_reconKeys.Clear(); s_reconDropped = s_reconMissing = s_reconRebucketed = 0;
                s_reconNames = s_saidReconcile ? null : new List<string>();
                p_reconCycles++;
            }
            s_reconWatch.Restart();
            long budget = (long)(ReconcileSliceMs * Stopwatch.Frequency / 1000.0);
            int walked = 0;
            if (s_reconPhase == 1)
            {
                int total = Types * Rings * Octants;
                while (s_reconBucket < total)
                {
                    var b = s_buckets[s_reconBucket / (Rings * Octants), (s_reconBucket / Octants) % Rings, s_reconBucket % Octants];
                    while (s_reconBucketPos < b.Count)
                    {
                        var z = b[s_reconBucketPos++]; if (z != null) s_inBucket.Add(z); walked++;
                        if ((walked & 63) == 0 && s_reconWatch.ElapsedTicks >= budget) goto done;
                    }
                    s_reconBucket++; s_reconBucketPos = 0;
                }
                foreach (var z in s_compilers) if (z != null) s_inBucket.Add(z);   // 1.2.427: the compiler lane counts as a bucket (no duplicate re-queue)
                // snapshot the keys once, then walk them (the dictionary itself is never enumerated across frames)
                s_reconKeys.Clear(); if (s_reconKeys.Capacity < s_queued.Count) s_reconKeys.Capacity = s_queued.Count;
                foreach (var k in s_queued.Keys) s_reconKeys.Add(k);
                s_reconPhase = 2; s_reconKeyPos = 0;
            }
            if (s_reconPhase == 2)
            {
                while (s_reconKeyPos < s_reconKeys.Count)
                {
                    ZDOID key = s_reconKeys[s_reconKeyPos++]; walked++;
                    if (s_queued.TryGetValue(key, out ZDO z)) ReconcileOne(key, z, now, s_reconNames, ref s_reconDropped, ref s_reconMissing, ref s_reconRebucketed, true);
                    if ((walked & 63) == 0 && s_reconWatch.ElapsedTicks >= budget) goto done;
                }
                // cycle done
                p_reconDropped += s_reconDropped; p_reconMissing += s_reconMissing; p_reconRebucketed += s_reconRebucketed;
                if (s_reconNames != null && (s_reconDropped + s_reconMissing + s_reconRebucketed) > 0)
                {
                    s_saidReconcile = true;
                    EasyBakeLog.Info($"[Stream] queue repair: {s_reconDropped} stale entr(ies) dropped, {s_reconMissing} with a missing prefab parked, {s_reconRebucketed} re-queued; queue now {s_queued.Count}. First: {string.Join(", ", s_reconNames.GetRange(0, Math.Min(12, s_reconNames.Count)))}");
                }
                s_nextReconcile = now + 5f;
                ReconcileAbort();
            }
        done: ;
            double ms = s_reconWatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            p_reconCalls++; p_reconWalked += walked; p_reconMs += ms; if (ms > p_reconWorst) p_reconWorst = ms;
        }

        // one queued entry: stale (reused / gone / created) -> dropped; no prefab on this client -> parked; live but in no bucket -> re-bucketed
        private static void ReconcileOne(ZDOID key, ZDO z, float now, List<string> names, ref int dropped, ref int missing, ref int rebucketed, bool removeNow)
        {
            if (z == null || z.m_uid != key || !z.IsValid() || z.Created) { if (removeNow) DropQueued(key); else s_dropIds.Add(key); dropped++; names?.Add("(reused / gone)"); return; }
            int hash = z.GetPrefab();
            if (SafePrefab.Get(hash) == null)
            {
                if (removeNow) DropQueued(key); else s_dropIds.Add(key);
                s_failed.Add(key); missing++;
                if (s_missingSaid.Add(hash)) EasyBakeLog.Info($"[Stream] objects of prefab hash {hash} have no prefab on this client (another mod's object): the pacer skips them, as vanilla does");
                names?.Add("missing prefab " + hash);
                return;
            }
            if (!s_inBucket.Contains(z)) { Bucket(z, now); rebucketed++; names?.Add(SafePrefab.Get(hash).name + " (re-queued)"); }
        }

        private static void DropQueued(ZDOID id)
        {
            if (!s_queued.Remove(id)) return;
            if (s_addSector.TryGetValue(id, out Vector2s sec)) { s_addSector.Remove(id); if (s_pendingByZone.TryGetValue(sec, out int c)) { if (c <= 1) s_pendingByZone.Remove(sec); else s_pendingByZone[sec] = c - 1; } }
            if (s_enqAt.Count > 0) s_enqAt.Remove(id);
        }

        // the 1.2.264 whole-queue walk, kept behind StreamPacerReconcileSliced = false (the A/B's other arm)
        private static void ReconcileWhole(float now)
        {
            if (now < s_nextReconcile) return;
            s_nextReconcile = now + 5f;
            s_reconWatch.Restart();
            s_inBucket.Clear(); s_dropIds.Clear();
            for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) foreach (var z in s_buckets[t, r, o]) if (z != null) s_inBucket.Add(z);
            foreach (var z in s_compilers) if (z != null) s_inBucket.Add(z);   // 1.2.427
            int dropped = 0, missing = 0, rebucketed = 0;
            var names = s_saidReconcile ? null : new List<string>();
            foreach (var kv in s_queued) ReconcileOne(kv.Key, kv.Value, now, names, ref dropped, ref missing, ref rebucketed, false);
            foreach (var id in s_dropIds) DropQueued(id);
            s_inBucket.Clear(); s_dropIds.Clear();
            p_reconDropped += dropped; p_reconMissing += missing; p_reconRebucketed += rebucketed;
            if (names != null && (dropped + missing + rebucketed) > 0)
            {
                s_saidReconcile = true;
                EasyBakeLog.Info($"[Stream] queue repair: {dropped} stale entr(ies) dropped, {missing} with a missing prefab parked, {rebucketed} re-queued; queue now {s_queued.Count}. First: {string.Join(", ", names.GetRange(0, Math.Min(12, names.Count)))}");
            }
            double ms = s_reconWatch.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            p_reconCalls++; p_reconCycles++; p_reconWalked += s_queued.Count; p_reconMs += ms; if (ms > p_reconWorst) p_reconWorst = ms;
        }

        private static void Untrack(ZDO zdo)
        {
            if (!s_queued.Remove(zdo.m_uid)) return;
            if (!s_addSector.TryGetValue(zdo.m_uid, out Vector2s sec)) sec = zdo.GetSector();
            s_addSector.Remove(zdo.m_uid); if (s_enqAt.Count > 0) s_enqAt.Remove(zdo.m_uid);
            if (s_pendingByZone.TryGetValue(sec, out int c)) { if (c <= 1) s_pendingByZone.Remove(sec); else s_pendingByZone[sec] = c - 1; }
        }

        private static void Wait(Vector2s sec, ZDO zdo)
        {
            if (!s_waiting.TryGetValue(sec, out List<ZDO> list)) s_waiting[sec] = list = TakeWaitList();
            list.Add(zdo); p_waited++;
        }

        // 1.2.257: per-zone wait lists are pooled (bounded at 64); a list is cleared before it goes back, and only s_waiting ever holds one.
        private static readonly Stack<List<ZDO>> s_waitPool = new Stack<List<ZDO>>(8);
        private static List<ZDO> TakeWaitList() { if (s_waitPool.Count > 0) { AllocsAvoided++; return s_waitPool.Pop(); } return new List<ZDO>(); }
        private static void GiveWaitList(List<ZDO> l) { l.Clear(); if (s_waitPool.Count < 64) s_waitPool.Push(l); }

        private static void ReleaseReadyZones(float now)
        {
            if (s_waiting.Count == 0) return;
            s_tmpZones.Clear(); s_tmpZones.AddRange(s_waiting.Keys);
            foreach (Vector2s z in s_tmpZones)
            {
                bool any = false;
                for (int t = 0; t < Types; t++) { s_ready[t] = ZoneSystem.instance.IsZoneReadyForType(z, (ZDO.ObjectType)t); any |= s_ready[t]; }
                if (!any) continue;
                List<ZDO> list = s_waiting[z];
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    ZDO zdo = list[i];
                    if (!s_ready[Mathf.Clamp((int)zdo.Type, 0, Types - 1)]) continue;
                    list[i] = list[list.Count - 1]; list.RemoveAt(list.Count - 1);
                    if (s_queued.TryGetValue(zdo.m_uid, out ZDO q) && ReferenceEquals(q, zdo)) Bucket(zdo, now);
                }
                if (list.Count == 0) { s_waiting.Remove(z); GiveWaitList(list); }
            }
        }

        private static void Signals(float now)
        {
            if (s_filling.Count == 0) return;
            s_tmpZones.Clear();
            foreach (var kv in s_filling) if (!s_pendingByZone.ContainsKey(kv.Key)) s_tmpZones.Add(kv.Key);   // [generator] nit: an unloaded zone with nothing pending is dropped (no Settled), never kept for the session
            foreach (Vector2s z in s_tmpZones) { s_filling.Remove(z); if (ZoneSystem.instance.IsZoneLoaded(z)) { p_settled++; EbmApi.RaiseZoneSettled(z); } }
        }

        private static void ClearAll() { ClearQueue(); s_filling.Clear(); s_floorTokens = 0f; s_floorAt = -1f; s_enqAt.Clear(); s_capBankMs = 0f; s_capAt = -1f; s_qPrev = 0; s_growRun = 0; }

        private static void ClearQueue()
        {
            for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) { s_buckets[t, r, o].Clear(); s_since[t, r, o] = -1f; }
            s_queued.Clear(); s_addSector.Clear(); s_pendingByZone.Clear(); s_failed.Clear(); foreach (var l in s_waiting.Values) GiveWaitList(l); s_waiting.Clear(); s_sweepCursor = 0; s_distantCursor = 0;
            s_compilers.Clear(); s_compilersDirty = false;   // 1.2.427
            ReconcileAbort();   // 1.2.338: a reseed invalidates a running reconcile cycle
        }

        // events: a ZDO entered a sector (network arrival, a creature moving, a placed piece)
        // 1.2.265 ([ghost] review): vanilla ZDO.SetSector calls RemoveFromSector + AddToSector BEFORE it sets m_position (ZDO.cs:503-506, 528-530), so
        // zdo.GetSector() in these postfixes is still the OLD sector. The sector vanilla passes is the new one: decoded as vanilla does
        // (ZoneSystem.cs:3463-3464: x = Sector % 512 - 256, y = Sector / 512 - 256).
        private static Vector2s SectorOf(ZoneSystem.SectorIndex s) => new Vector2s((int)(s.Sector % 512u) - 256, (int)(s.Sector / 512u) - 256);
        private static void OnSectorAdd(ZDO zdo, Vector2s sector)
        {
            if (!s_active || !s_seeded || zdo == null || !InNear(sector)) return;
            if (TryAdd(zdo, Time.unscaledTime, sector)) p_events++;
        }

        // 1.2.271: AddToSector + InitialAddToSector reach this through SectorHooks (one EBM postfix per vanilla method)
        internal static void OnSectorAddHook(ZDO zdo, ZoneSystem.SectorIndex sectorIndex) => OnSectorAdd(zdo, SectorOf(sectorIndex));

        // 1.2.265 [Optimize] StreamPacerSectorLeave (TESTPLAN 0b part 4): a ZDO leaving its sector (vanilla ZDOMan.RemoveFromSector: a move or a
        // destroy) leaves the queue at once; a move into a near sector re-queues it through AddToSector above. The round-robin sweep stays as the
        // safety net. One dictionary lookup per call; nothing when the ZDO is not queued.
        internal static void OnSectorRemoveHook(ZDO zdo)   // 1.2.271: called from SectorHooks
        {
            try
            {
                if (zdo == null || !SectorLeave || s_queued.Count == 0 || !s_queued.TryGetValue(zdo.m_uid, out ZDO q) || !ReferenceEquals(q, zdo)) return;
                Untrack(zdo); p_sectorLeft++;
            }
            catch { }
        }

        // the extra pass on frames vanilla's 30 Hz tick skipped: only while the queue has work (one bool read otherwise)
        private static bool s_saidExtraOff; private static int p_extraSkipped;   // 1.2.254
        private static readonly List<ZDO> s_none = new List<ZDO>(), s_noneDistant = new List<ZDO>();
        [HarmonyPatch(typeof(ZNetScene), "Update"), HarmonyPostfix]
        private static void ExtraPass(ZNetScene __instance)
        {
            using (EbmPatchMarkers.PacerExtra.Auto()) ExtraPassBody(__instance);   // 1.2.360: profiler marker only
        }
        private static void ExtraPassBody(ZNetScene __instance)
        {
            if (!s_active || !HasWork || LastPassFrame == Time.frameCount || !On || s_createObjects == null) return;
            if (FiresEasyBakeMeshesPlugin.StreamPacerEveryFramePass != null && !FiresEasyBakeMeshesPlugin.StreamPacerEveryFramePass.Value) return;
            // 1.2.254 [Optimize] StreamPacerExtraPasses off: no extra pass while moving; vanilla's 30 Hz tick still runs one. Still and arrival (no player, loading, teleport) unchanged.
            if (FiresEasyBakeMeshesPlugin.StreamPacerExtraPasses != null && !FiresEasyBakeMeshesPlugin.StreamPacerExtraPasses.Value)
            {
                var me = Player.m_localPlayer;
                if (me != null && !me.IsTeleporting() && !s_inLoadingScreen(__instance) && !StillNow(me))
                {
                    if (!s_saidExtraOff) { s_saidExtraOff = true; EasyBakeLog.Info("[Stream] extra create passes off while moving: objects are created on vanilla's 30 per second tick while you move"); }
                    MeasureFrame(false);   // this frame measures as if it had a pass: spikes and the last pass's cost are still seen
                    p_extraSkipped++;
                    return;
                }
            }
            try
            {
                s_none.Clear(); s_noneDistant.Clear();
                s_inExtraPass = true;
                try { s_createObjects(__instance, s_none, s_noneDistant); }   // vanilla's CreateObjects: every mod's patch on it still runs
                finally { s_inExtraPass = false; }
            }
            catch (Exception e) { EasyBakeLog.Debug("[Stream] extra create pass skipped: " + e.Message); }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), HarmonyPostfix]
        private static void OnShutdown() { ClearAll(); s_seeded = false; s_active = false; s_wasArrival = false; f_start = -1f; s_probeStart = -1f; s_motionHave = false; }   // 1.2.256: the next join gets its 60 s probe and a fresh first sample

        // ================================================================== proof
        private static void FillNote(float now, int created)
        {
            if (s_queued.Count > 0 || s_waiting.Count > 0)
            {
                if (f_start < 0f) { f_start = now; f_created = 0; f_peak = 0; }
                f_created += created; if (s_queued.Count > f_peak) f_peak = s_queued.Count;
                return;
            }
            if (f_start < 0f) return;
            f_created += created;
            float secs = now - f_start;
            if (f_created >= 500)
                EasyBakeLog.InfoFirst("Stream queue drained", $"[Stream] queue drained: {f_created} object(s) created in {secs:0.0} s ({(secs > 0f ? f_created / secs : 0f):0}/s), queue peaked at {f_peak}; {s_costAvg:0.000} ms per create, target frame {TargetFrameMs():0.0} ms, other work {s_otherAvg:0.0} ms");
            f_start = -1f;
        }

        private static void Report(float now)
        {
            if (now < p_next) return;
            if (p_next > 0f && p_frames > 0 && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[Stream] last 30 s: {p_created} created over {p_frames} pass(es) ({p_extra} extra), {p_distant} of them distant; pass avg {p_passMs / p_frames:0.00} ms (worst {p_passMax:0.0}), budget avg {p_budgetMs / p_frames:0.0} ms, other work {s_otherAvg:0.0} ms, target {TargetFrameMs():0.0} ms, {s_costAvg:0.000} ms per create; " +
                                  $"queue {s_queued.Count} ({s_waiting.Count} zone(s) not ready; buckets hold {BucketEntries()} entr(ies) incl. stale), filling {s_filling.Count}, settled {p_settled}; reseeds {p_seeds}, by events {p_events}, by the sweep {p_swept}, parked for a loading zone {p_waited}, starved-bucket creates {p_aged}, heavy deferred {p_heavyDeferred}, spikes {p_spikes}, over-budget passes {p_overruns}, post-arrival grace passes {p_gracePasses}, arrival passes left to vanilla {s_arrivalFrames}; {s_prefabCost.Count} prefab cost(s) learned; budget from: {p_moving} moving, {p_still} still; worst membership step {p_memberMax:0.0} ms" + (p_floorPasses > 0 ? $"; vanilla floor: {p_floorPasses} moving pass(es), {p_floorReal} objects really made, {s_floorTokens:0} tokens left" : "") + (p_extraSkipped > 0 ? $"; {p_extraSkipped} extra pass(es) skipped while moving" : "")
                                  + $"; measured speed avg {(p_speedN > 0 ? p_speedSum / p_speedN : 0.0):0.0} m/s (max {p_speedMax:0.0}, {(FromReference ? "reference position" : "player position")}); {p_realCapPasses} pass(es) capped at the real create cost ({(s_realCostAvg > 0f ? s_realCostAvg : 0f):0.000} ms each); focus: {p_focused} focused / {p_unfocused} unfocused pass(es), {p_boosted} raised to the {UnfocusedMs:0} ms background ceiling (raised when other work is over {2f * TargetFrameMs():0.0} ms; background run {Application.runInBackground})"
                                  + $"; keep-up: queue at window end {s_queued.Count} (last window {s_qPrev}), wait enqueue-to-create max {p_waitMax:0.0} s, p95 {WaitP95()} over {p_waitN} create(s), {p_behind} created behind the camera (over 90 degrees), moving cap {(MovingCapOn ? "on" : "off")}: {p_mcFloor} floor / {p_mcCapped} capped create(s) over {p_mcPasses} pass(es), floor phase cut {p_mcFloorCuts}x ({p_mcFloorCarried} carried), cap bank {s_capBankMs:0.0} ms; view order {(ViewOrder ? "on" : "off")}; stood down {p_downStretches} stretch(es), {p_downSeconds:0.0} s{(s_downWhy != null ? " (now: " + s_downWhy + ")" : "")}; queue repair {p_reconDropped} dropped / {p_reconMissing} missing prefab / {p_reconRebucketed} re-queued (reconcile: {p_reconWalked} entries walked in {p_reconCalls} slice(s), {p_reconCycles} cycle(s), {p_reconMs:0.0} ms, worst slice {p_reconWorst:0.00} ms, {(ReconcileSliced ? "sliced" : "WHOLE WALK")}); arrival passes paced {p_arrivalPaced}, near-first creates {p_nearFirst}, left their sector {p_sectorLeft}; terrain compilers first {p_compilersFirst} ({s_compilers.Count} waiting for their ground, {(CompilersFirst ? "on" : "off")})");
            p_compilersFirst = 0;   // 1.2.427
            if (p_next > 0f)
            {
                // 1.2.257: the queue grew two windows in a row while moving = the pacer is not keeping up
                bool moving = p_moving > p_still;
                int q = s_queued.Count;
                s_growRun = moving && q > s_qPrev ? s_growRun + 1 : 0;
                if (s_growRun >= 2) EasyBakeLog.Debug($"[Stream] queue grew {s_growRun} windows in a row while moving: {s_qPrev} -> {q}; speed avg {(p_speedN > 0 ? p_speedSum / p_speedN : 0.0):0.0} m/s, riding a ship {(Player.m_localPlayer != null && Player.m_localPlayer.IsAttachedToShip())}");
                s_qPrev = q;
            }
            p_downStretches = 0; p_downSeconds = 0f; p_reconDropped = p_reconMissing = p_reconRebucketed = 0; p_reconCalls = p_reconWalked = p_reconCycles = 0; p_reconMs = p_reconWorst = 0; p_arrivalPaced = p_nearFirst = p_sectorLeft = 0; p_waitN = 0; p_waitMax = 0f; p_behind = 0; p_mcPasses = p_mcFloor = p_mcCapped = 0; Array.Clear(p_waitBins, 0, p_waitBins.Length);
            p_next = now + 30f;
            p_extraSkipped = 0;
            p_frames = p_created = p_distant = p_extra = p_seeds = p_events = p_swept = p_waited = p_aged = p_heavyDeferred = p_spikes = p_settled = 0;
            p_overruns = p_gracePasses = 0;   // 1.2.357
            p_passMs = p_budgetMs = 0; p_passMax = 0f; p_still = p_moving = 0; p_memberMax = 0f; p_floorPasses = p_floorReal = 0;
            p_speedSum = 0; p_speedN = 0; p_speedMax = 0f; p_realCapPasses = 0; p_focused = p_unfocused = p_boosted = 0;
        }
    }
}
