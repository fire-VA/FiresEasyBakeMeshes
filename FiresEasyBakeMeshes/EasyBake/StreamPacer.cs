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
        internal static bool Active => s_active && On;
        internal static bool HasWork => s_queued.Count > 0 || s_waiting.Count > 0 || s_wasArrival;
        internal static int LastPassFrame = -1;

        // ---- vanilla helpers (through delegates, so every mod's patch on them still runs) ----
        private static Func<ZNetScene, ZDO, GameObject> s_createObject;
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
            if (!__runOriginal) { s_active = false; return false; }   // an earlier prefix (another pacer) already owns this pass: stand down
            if (!On) { s_active = false; return true; }
            var net = ZNet.instance;
            if (net == null || ZoneSystem.instance == null || (net.IsServer() && net.IsDedicated())) { s_active = false; return true; }   // a dedicated server keeps its own path
            if (!Resolve()) return true;
            if (VcpOwnsCreation()) { s_active = false; return true; }
            try
            {
                bool arrival = s_inLoadingScreen(__instance) || Player.m_localPlayer == null || Player.m_localPlayer.IsTeleporting();
                // 1.2.232 (lead, 1229 run: Fire spawned at the WORLD SPAWN, "area at the logout point not ready ... zone loaded False"): arrival runs
                // vanilla, exactly as before EBM paced (FGN's proven rule). The pacer only paces once the player is placed and not teleporting.
                if (arrival) { s_wasArrival = false; s_arrivalFrames++; return true; }
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
                ClearQueue(); s_refZone = zone; s_seeded = true; p_seeds++;   // [generator] M2: open fills stay open; Signals settles them when nothing is pending
                for (int i = 0; i < near.Count; i++) TryAdd(near[i], now);
                PruneStamps();   // 1.2.257: enqueue stamps survive a reseed (true wait times); drop those of objects no longer queued
            }
            else if (nearList)
            {
                int n = near.Count; if (s_sweepCursor >= n) s_sweepCursor = 0;
                int end = Math.Min(n, s_sweepCursor + SweepPerPass);
                for (int i = s_sweepCursor; i < end; i++) if (TryAdd(near[i], now)) p_swept++;
                s_sweepCursor = end;
            }
            ReleaseReadyZones(now);

            // this pass's budget
            float memberMs = (float)(sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency); if (memberMs > p_memberMax) p_memberMax = memberMs;   // 1.2.252: seeding + sweep, inside the pass time
            s_floorThisPass = false; s_passReal = 0;
            s_mcPass = false; s_mcExtraStart = -1;
            float ms = Budget(arrival, out int cap);
            long budgetTicks = (long)(ms * Stopwatch.Frequency / 1000.0);
            s_wasArrival = arrival;
            p_budgetMs += ms;

            int created = 0, attempts = 0;
            if (s_seeded && ZoneSystem.instance.IsActiveAreaLoaded())
            {
                // fairness first: the oldest bucket waiting over FairAfterS gets up to a quarter of the pass
                // (1.2.257: the scan covers the octants too; with StreamPacerViewOrder off only octant 0 is ever filled, so the pick is the same)
                int at = -1, ar = -1, ao = -1; float oldest = now - FairAfterS;
                for (int t = 0; t < Types; t++) for (int r = 0; r < Rings; r++) for (int o = 0; o < Octants; o++) if (s_since[t, r, o] >= 0f && s_since[t, r, o] < oldest) { oldest = s_since[t, r, o]; at = t; ar = r; ao = o; }
                bool done = false;
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
            if (s_inExtraPass) p_extra++;
            Signals(now);
            FillNote(now, created);
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
        private static float s_speedNow; private static double p_speedSum; private static int p_speedN; private static float p_speedMax;

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
                    float moved = (pos - s_motionPos).magnitude;
                    s_speedNow = moved / dt;
                    if (moved < 50f) { p_speedSum += s_speedNow; p_speedN++; if (s_speedNow > p_speedMax) p_speedMax = s_speedNow; }   // a teleport is not a speed
                    if (moved > dt * StillSpeed) s_movingUntil = now + MovingHoldS;
                }
                s_motionPos = pos; s_motionHave = true;
            }
            if (me.GetVelocity().sqrMagnitude >= StillSpeed * StillSpeed) return false;
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
            EasyBakeLog.Debug($"[Stream] motion probe t+{now - s_probeStart:0}s: player {p.x:0.0},{p.y:0.0},{p.z:0.0}, body speed {me.GetVelocity().magnitude:0.0} m/s, reference {r.x:0.0},{r.y:0.0},{r.z:0.0}, measured {s_speedNow:0.0} m/s ({(FromReference ? "from the reference position" : "from the player position")}), verdict {(still ? "still" : "moving")}, debug fly {me.InDebugFlyMode()}, riding a ship {me.IsAttachedToShip()}, focused {Application.isFocused}");
        }

        private static float Budget(bool arrival, out int cap)
        {
            float other = Mathf.Max(0f, s_otherAvg), ms;
            bool floorAsked = false, floorStill = false;
            // 1.2.257: StreamPacerMovingCap supersedes StreamPacerVanillaFloor while it is on (the 1.2.253 branch is not reached; both use the same token count, never twice)
            if (!arrival && MovingCapOn)
            {
                floorStill = Still(Player.m_localPlayer); floorAsked = true;
                if (!floorStill) return MovingCapBudget(other, out cap);
            }
            else if (!arrival && FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor != null && FiresEasyBakeMeshesPlugin.StreamPacerVanillaFloor.Value)
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
                ms = still ? Mathf.Clamp(Mathf.Max(headroom * 0.9f, Mathf.Max(target * 0.5f, NoHitchMs - other)), BaseMs, Mathf.Max(BaseMs, Mathf.Min(StillMaxMs, NoHitchMs - other)))
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
            s_mcFloor = (int)s_floorTokens;
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
            if (s_passReal < s_mcFloor && created - s_passReal < 4 * s_mcFloor + 64) return false;
            long e = sw.ElapsedTicks;
            if (s_mcExtraStart < 0) s_mcExtraStart = e;
            return e - s_mcExtraStart >= s_mcExtraTicks;
        }

        private static void McEndPass(Stopwatch sw)
        {
            int floorReal = Math.Min(s_passReal, s_mcFloor);
            s_floorTokens = Mathf.Max(0f, s_floorTokens - floorReal);
            p_mcFloor += floorReal; p_mcCapped += s_passReal - floorReal;
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
            bool real = s_createObject(scene, zdo) != null;
            if (real)
            {
                s_passReal++;
                if (s_keepUpLog && s_haveFwd) { Vector3 p = zdo.GetPosition(); if ((p.x - s_viewRef.x) * s_camFwd.x + (p.z - s_viewRef.z) * s_camFwd.y < 0f) p_behind++; }   // 1.2.257 logging: over 90 degrees from the camera's forward
            }
            long ticks = sw.ElapsedTicks - t0;
            int prefab = zdo.GetPrefab();
            s_prefabCost[prefab] = s_prefabCost.TryGetValue(prefab, out long old) ? (old * 3 + ticks) / 4 : ticks;
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

        private static void Bucket(ZDO zdo, float now)
        {
            int ring = Math.Min(Rings - 1, RingOf(zdo.GetSector()));
            int type = Mathf.Clamp((int)zdo.Type, 0, Types - 1);
            int oct = ViewOrder ? OctantOf(zdo.GetPosition()) : 0;
            List<ZDO> b = s_buckets[type, ring, oct];
            if (b.Count == 0 || s_since[type, ring, oct] < 0f) s_since[type, ring, oct] = now;
            b.Add(zdo);
        }

        private static bool TryAdd(ZDO zdo, float now)
        {
            if (zdo == null || zdo.Created || !zdo.IsValid()) return false;
            ZDOID id = zdo.m_uid;
            if (s_queued.ContainsKey(id) || s_failed.Contains(id)) return false;
            Vector2s sec = zdo.GetSector();
            Bucket(zdo, now);
            s_queued[id] = zdo; s_addSector[id] = sec;
            if (s_keepUpLog && !s_enqAt.ContainsKey(id)) s_enqAt[id] = now;   // 1.2.257 logging: first enqueue kept across a reseed
            s_pendingByZone.TryGetValue(sec, out int c); s_pendingByZone[sec] = c + 1;
            if (c == 0 && !s_filling.ContainsKey(sec)) { s_filling[sec] = now; EbmApi.RaiseZoneFilling(sec); }
            return true;
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
        }

        // events: a ZDO entered a sector (network arrival, a creature moving, a placed piece)
        private static void OnSectorAdd(ZDO zdo)
        {
            if (!s_active || !s_seeded || zdo == null || !InNear(zdo.GetSector())) return;
            if (TryAdd(zdo, Time.unscaledTime)) p_events++;
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        private static void AddToSectorPostfix(ZDO zdo) { try { OnSectorAdd(zdo); } catch { } }

        [HarmonyPatch(typeof(ZDOMan), "InitialAddToSector"), HarmonyPostfix]
        private static void InitialAddToSectorPostfix(ZDO zdo) { try { OnSectorAdd(zdo); } catch { } }

        // the extra pass on frames vanilla's 30 Hz tick skipped: only while the queue has work (one bool read otherwise)
        private static bool s_saidExtraOff; private static int p_extraSkipped;   // 1.2.254
        private static readonly List<ZDO> s_none = new List<ZDO>(), s_noneDistant = new List<ZDO>();
        [HarmonyPatch(typeof(ZNetScene), "Update"), HarmonyPostfix]
        private static void ExtraPass(ZNetScene __instance)
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
                EasyBakeLog.Info($"[Stream] queue drained: {f_created} object(s) created in {secs:0.0} s ({(secs > 0f ? f_created / secs : 0f):0}/s), queue peaked at {f_peak}; {s_costAvg:0.000} ms per create, target frame {TargetFrameMs():0.0} ms, other work {s_otherAvg:0.0} ms");
            f_start = -1f;
        }

        private static void Report(float now)
        {
            if (now < p_next) return;
            if (p_next > 0f && p_frames > 0 && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[Stream] last 30 s: {p_created} created over {p_frames} pass(es) ({p_extra} extra), {p_distant} of them distant; pass avg {p_passMs / p_frames:0.00} ms (worst {p_passMax:0.0}), budget avg {p_budgetMs / p_frames:0.0} ms, other work {s_otherAvg:0.0} ms, target {TargetFrameMs():0.0} ms, {s_costAvg:0.000} ms per create; " +
                                  $"queue {s_queued.Count} ({s_waiting.Count} zone(s) not ready), filling {s_filling.Count}, settled {p_settled}; reseeds {p_seeds}, by events {p_events}, by the sweep {p_swept}, parked for a loading zone {p_waited}, starved-bucket creates {p_aged}, heavy deferred {p_heavyDeferred}, spikes {p_spikes}, arrival passes left to vanilla {s_arrivalFrames}; {s_prefabCost.Count} prefab cost(s) learned; budget from: {p_moving} moving, {p_still} still; worst membership step {p_memberMax:0.0} ms" + (p_floorPasses > 0 ? $"; vanilla floor: {p_floorPasses} moving pass(es), {p_floorReal} objects really made, {s_floorTokens:0} tokens left" : "") + (p_extraSkipped > 0 ? $"; {p_extraSkipped} extra pass(es) skipped while moving" : "")
                                  + $"; measured speed avg {(p_speedN > 0 ? p_speedSum / p_speedN : 0.0):0.0} m/s (max {p_speedMax:0.0}, {(FromReference ? "reference position" : "player position")}); {p_realCapPasses} pass(es) capped at the real create cost ({(s_realCostAvg > 0f ? s_realCostAvg : 0f):0.000} ms each); focus: {p_focused} focused / {p_unfocused} unfocused pass(es), {p_boosted} raised to the {UnfocusedMs:0} ms background ceiling (raised when other work is over {2f * TargetFrameMs():0.0} ms; background run {Application.runInBackground})"
                                  + $"; keep-up: queue at window end {s_queued.Count} (last window {s_qPrev}), wait enqueue-to-create max {p_waitMax:0.0} s, p95 {WaitP95()} over {p_waitN} create(s), {p_behind} created behind the camera (over 90 degrees), moving cap {(MovingCapOn ? "on" : "off")}: {p_mcFloor} floor / {p_mcCapped} capped create(s) over {p_mcPasses} pass(es), cap bank {s_capBankMs:0.0} ms; view order {(ViewOrder ? "on" : "off")}");
            if (p_next > 0f)
            {
                // 1.2.257: the queue grew two windows in a row while moving = the pacer is not keeping up
                bool moving = p_moving > p_still;
                int q = s_queued.Count;
                s_growRun = moving && q > s_qPrev ? s_growRun + 1 : 0;
                if (s_growRun >= 2) EasyBakeLog.Debug($"[Stream] queue grew {s_growRun} windows in a row while moving: {s_qPrev} -> {q}; speed avg {(p_speedN > 0 ? p_speedSum / p_speedN : 0.0):0.0} m/s, riding a ship {(Player.m_localPlayer != null && Player.m_localPlayer.IsAttachedToShip())}");
                s_qPrev = q;
            }
            p_waitN = 0; p_waitMax = 0f; p_behind = 0; p_mcPasses = p_mcFloor = p_mcCapped = 0; Array.Clear(p_waitBins, 0, p_waitBins.Length);
            p_next = now + 30f;
            p_extraSkipped = 0;
            p_frames = p_created = p_distant = p_extra = p_seeds = p_events = p_swept = p_waited = p_aged = p_heavyDeferred = p_spikes = p_settled = 0;
            p_passMs = p_budgetMs = 0; p_passMax = 0f; p_still = p_moving = 0; p_memberMax = 0f; p_floorPasses = p_floorReal = 0;
            p_speedSum = 0; p_speedN = 0; p_speedMax = 0f; p_realCapPasses = 0; p_focused = p_unfocused = p_boosted = 0;
        }
    }
}
