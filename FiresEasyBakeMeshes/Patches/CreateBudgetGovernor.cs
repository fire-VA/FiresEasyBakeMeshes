using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Log = FiresEasyBakeMeshes.EasyBakeLog;

namespace FiresEasyBakeMeshes.Patches
{
    // ═══ THE CREATE BUDGET SCALES WITH THE BACKLOG, AND THAT IS THE HITCH ═════
    // ZNetScene.CreateObjectsSorted decides how many objects it may instantiate
    // this frame with:
    //
    //     int num = Mathf.Max(this.m_tempCurrentObjects2.Count / 100, maxCreatedPerFrame);
    //
    // maxCreatedPerFrame is 10 in play (100 in the loading screen), so on a
    // normal world the budget is 10 and the `/100` term never wins. It is a
    // deliberate catch-up rule: the more objects are waiting, the faster they
    // are allowed to arrive, so a busy area fills in reasonable time.
    //
    // It stops being reasonable when the backlog is large. Measured on a
    // converted 7.68M-ZDO world standing in a built settlement — 50,111 ZDOs in
    // the active area, ~24,000 of them not yet created:
    //
    //     ZNetScene.CreateObjects(total)         4545.43 ms / 37 calls = 122.9 ms avg
    //     ZNetScene.CreateObject(per-instance)   4477.82 ms / 24,494 calls = 0.183 ms
    //
    // Creation is 4,477 of those 4,545 ms; the candidate loop and the sort
    // together are ~67 ms over 30 seconds, about 1.5%. So the per-object cost is
    // ordinary and the SORT IS NOT THE PROBLEM — the problem is that the budget
    // became 24000/100 = 240 objects in a single frame, which at 0.183 ms each
    // is ~44 ms of instantiation in one frame, landing as a 122 ms stall roughly
    // once a second.
    //
    // This does not make the work smaller, it makes it ARRIVE EVENLY. Vanilla's
    // floor is preserved exactly (the budget can never drop below the 10/100 the
    // game asked for), only the backlog-scaled term is capped. A dense area then
    // populates over a few more frames instead of seizing for one.
    //
    // Done as a transpiler on the single Mathf.Max(int,int) call in that method
    // rather than by editing the budget after the fact, because `num` is a local
    // and there is no other way to reach it. One call site, so the swap is
    // unambiguous, and if the game ever changes that expression the count logged
    // at patch time stops being 1 and says so.
    [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
    public static class ZNetScene_CreateObjectsSorted_Budget
    {
        private static int s_peakAsked, s_peakGiven;
        private static long s_frames, s_capped;

        // ═══ WHERE THE TIME IN THIS METHOD ACTUALLY GOES ═════════════════════
        // The comment above concluded "the SORT IS NOT THE PROBLEM" from a run
        // with ~24,000 candidates, where creation was 4,477 of 4,545 ms. On
        // 2026-09-24 the same probes read CreateObjectsSorted 81.9 ms/s against
        // CreateObject(per-instance) 11.5 ms/s - 70 ms/s that is neither
        // creation nor explained by a candidate list the budget says is only
        // ~5,600. Rather than rewrite a vanilla hot path on a model that does
        // not reconcile, split the method and let it say.
        //
        // The transpiler above already gives a free boundary: Mathf.Max is
        // evaluated AFTER the candidate walk and BEFORE the sort, so Budget()
        // is a timestamp exactly between the two halves. Prefix starts the
        // clock, Budget() closes the walk, Postfix closes sort+create-loop.
        private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();
        private static AccessTools.FieldRef<ZNetScene, List<ZDO>> s_candidates;
        private static bool s_candidatesResolved;
        private static long s_entered, s_midpoint;
        private static long s_walkTicks, s_tailTicks;
        private static int s_nearPeak, s_candPeak, s_candLast, s_createdSum;

        [HarmonyPrefix]
        public static bool Prefix(ZNetScene __instance, List<ZDO> currentNearObjects)
        {
            s_entered = s_clock.ElapsedTicks;
            s_midpoint = 0L;
            int near = currentNearObjects != null ? currentNearObjects.Count : 0;
            if (near > s_nearPeak) s_nearPeak = near;

            // Returning false leaves the candidate list EMPTY, so the create loop below simply finds nothing. It
            // cannot destroy or strand an object; the worst case is a piece appearing one deadline late.
            if (EasyBake.ScanGate.Create.CanSkip(near, DistantCount(), InstanceCount(__instance), CurrentZone()))
            {
                s_entered = 0L;
                return false;
            }
            return true;
        }

        private static int InstanceCount(ZNetScene scene) => scene != null ? scene.NrOfInstances() : 0;

        // The distant list is the sibling of the near one and changes for the same reasons, so its length is part of
        // "nothing moved". It lives on ZNetScene as a private temp the same way the near list does.
        private static int DistantCount()
        {
            if (!s_distantResolved)
            {
                s_distantResolved = true;
                try { s_distantList = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentDistantObjects"); }
                catch { s_distantList = null; }
            }
            var scene = ZNetScene.instance;
            if (s_distantList == null || scene == null) return 0;
            try { var list = s_distantList(scene); return list != null ? list.Count : 0; }
            catch { return 0; }
        }

        private static Vector2s CurrentZone()
        {
            var net = ZNet.instance;
            return net != null ? ZoneSystem.GetZone(net.GetReferencePosition()) : new Vector2s(0, 0);
        }

        private static AccessTools.FieldRef<ZNetScene, List<ZDO>> s_distantList;
        private static bool s_distantResolved;

        [HarmonyPostfix]
        public static void Postfix(ZNetScene __instance, int created)
        {
            // Budget() never ran: the method returned at the IsActiveAreaLoaded gate.
            if (s_midpoint == 0L) return;
            s_tailTicks += s_clock.ElapsedTicks - s_midpoint;
            s_createdSum += created;

            if (!s_candidatesResolved)
            {
                s_candidatesResolved = true;
                try { s_candidates = AccessTools.FieldRefAccess<ZNetScene, List<ZDO>>("m_tempCurrentObjects2"); }
                catch { s_candidates = null; }
            }
            if (s_candidates == null || __instance == null) return;
            try
            {
                var list = s_candidates(__instance);
                s_candLast = list != null ? list.Count : 0;
                if (s_candLast > s_candPeak) s_candPeak = s_candLast;
                EasyBake.ScanGate.Create.NoteScanFound(s_candLast);
            }
            catch { }
        }

        // The exact candidate count, for FDT's overlay. Not backlog/100 - that
        // integer division loses two digits at the sizes this matters at.
        public static int LastCandidateCount => s_candLast;
        public static int LastNearCount => s_nearPeak;

        // EBM's budget part of the status box: null while nothing was held back since the last box.
        internal static string TakeStatus()
        {
            if (s_frames == 0) return null;
            double walkMs = s_walkTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double tailMs = s_tailTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string status =
                $"CreateObjectsSorted over {s_frames:N0} pass(es): walk {walkMs:N0} ms of up to {s_nearPeak:N0} near, " +
                $"sort+create {tailMs:N0} ms of up to {s_candPeak:N0} candidate(s), {s_createdSum:N0} created" +
                (s_capped == 0 ? "" : $"; budget held back {s_capped:N0} pass(es) (asked up to {s_peakAsked:N0}, allowed {s_peakGiven:N0})");
            s_frames = 0; s_capped = 0; s_peakAsked = 0; s_peakGiven = 0;
            s_walkTicks = 0; s_tailTicks = 0; s_nearPeak = 0; s_candPeak = 0; s_createdSum = 0;
            return status;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo mathfMax = AccessTools.Method(typeof(Mathf), nameof(Mathf.Max), new[] { typeof(int), typeof(int) });
            MethodInfo ours = AccessTools.Method(typeof(ZNetScene_CreateObjectsSorted_Budget), nameof(Budget));
            int swapped = 0;

            foreach (CodeInstruction ci in instructions)
            {
                if (mathfMax != null && ci.Calls(mathfMax))
                {
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Call, ours);
                    continue;
                }
                yield return ci;
            }

            if (swapped == 1)
                Log.Info("[CreateBudget] governor installed on ZNetScene.CreateObjectsSorted.");
            else
                Log.Warn($"[CreateBudget] expected ONE Mathf.Max in CreateObjectsSorted, swapped {swapped} — " +
                    "the budget expression changed; the governor is not doing what it says and should be re-read against the current game code.");
        }

        // Replaces Mathf.Max(backlog/100, maxCreatedPerFrame).
        public static int Budget(int backlogScaled, int nominal)
        {
            // Mathf.Max sits between the candidate walk and the sort, so this is the split point.
            long now = s_clock.ElapsedTicks;
            if (s_entered != 0L) s_walkTicks += now - s_entered;
            s_midpoint = now;

            int vanilla = Mathf.Max(backlogScaled, nominal);
            s_frames++;
            if (backlogScaled > s_peakAsked) s_peakAsked = backlogScaled;

            // ═══ NO CAP BEFORE THE PLAYER EXISTS, 2026-09-25 ════════════════════════════════════════════════════
            // The cap exists to stop a 240-object frame landing as a 122 ms stall while someone is PLAYING. Before
            // the player is spawned there is no frame to protect - and the cap is actively harmful there, because
            // Game.Start will not spawn until ZNetScene.IsAreaReady, and IsAreaReady wants an instance for every
            // object around the logout point.
            //
            // Measured: "RESPAWN WAIT STUCK 142s after Game.Start - waiting on logoutPoint ... areaReady=False"
            // with "[AreaReady] BLOCKED - 45,428 of 66,968 valid ZDO(s) have no instance". At a cap of 40 and
            // ~20 fps that backlog needs 57 seconds of pure draining before the spawn can even begin; vanilla's
            // own backlog rule would have allowed 454 a frame precisely so a dense area fills fast.
            //
            // So the cap is lifted until Player.m_localPlayer exists, which is exactly the spawn-in window.
            int cap = Player.m_localPlayer == null ? 0
                    : (FiresEasyBakeMeshesPlugin.CreateBudgetPerFrame != null
                    ? FiresEasyBakeMeshesPlugin.CreateBudgetPerFrame.Value : 0);
            // 0 = off, and the cap can never pull the budget below what the game
            // itself asked for — a loading screen still gets its 100.
            int given = cap <= 0 ? vanilla : Mathf.Min(vanilla, Mathf.Max(nominal, cap));
            CreationCensus.LastBudget = given;
            CreationCensus.LastBacklogScaled = backlogScaled;
            CreationCensus.LastNominal = nominal;
            if (given < vanilla) s_capped++;
            if (given > s_peakGiven) s_peakGiven = given;
            return given;
        }
    }
}
