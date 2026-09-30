using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Log = FiresEasyBakeMeshes.EasyBakeLog;

namespace FiresEasyBakeMeshes.Patches
{
    // ═══ WHAT IS ACTUALLY BEING CREATED, AND UNDER WHAT BUDGET ═══════════════
    // Measured on a converted 7.68M-ZDO world while walking through a built
    // settlement:
    //
    //     ZNetScene.CreateObject(per-instance)  12,077 ms / 65,596 calls / 30 s
    //     ZNetScene.CreateObjects(total)        12,284 ms /    188 calls
    //     ZNetScene.RemoveObjects                   84 ms /    188 calls
    //
    // THE ANSWER IT GAVE, so nobody re-derives it from the numbers above:
    //
    //     [Creations] 1,961 object(s) over 217 CreateObjects call(s) = 9.0 per
    //                 call (worst 12); mean budget handed out 10.1.
    //
    // Nine created per call against a budget of ten — the budget is honoured and
    // was never the problem. The reconciliation failure was in the READING: the
    // probe counts CreateObject CALLS, and ~59,000 of the 61,194 per 30 s return
    // null and create nothing while still costing 0.187 ms each. That is 11 of
    // every 30 seconds spent on calls that produce no object.
    //
    // They are ZDOs whose prefab does not exist in this mod stack — a converted
    // world carrying pieces its original stack had. CreateObjectsSorted only
    // cleans those up on a SERVER:
    //     else if (ZNet.instance.IsServer()) { ... DestroyZDO(zdo); }
    // On a client the null is discarded, Created is never set, and the ZDO is
    // retried EVERY pass forever. One hash appeared 162,456 times in a single
    // log. Creation cost was never the issue; FAILED creation was.
    //
    // What it counts:
    //
    //   1. PER CALL — the budget the governor handed out against the number of
    //      objects that were actually created inside that same call. If those
    //      disagree there is a second creation path, and that is where the 40%
    //      lives.
    //   2. PER PREFAB — which prefabs make up the creations. Dominated by a
    //      handful means a targeted instancing or far-tier problem; spread flat
    //      means raw density, and the only lever left is making the far tier
    //      cover more of it.
    //
    // Measurement only. Nothing here changes behaviour, which after three
    // changes today that did not help is the point.
    [HarmonyPatch]
    public static class CreationCensus
    {
        private static bool Active =>
            FiresEasyBakeMeshesPlugin.CreationCensusEnabled != null
            && FiresEasyBakeMeshesPlugin.CreationCensusEnabled.Value;

        private static int s_inCall;                 // creations inside the current CreateObjects call
        private static bool s_insideCreateObjects;

        private static int s_calls, s_created;
        private static int s_worstInOneCall;
        private static int s_budgetSum, s_budgetSamples;
        private static readonly Dictionary<int, int> s_byPrefab = new Dictionary<int, int>();
        private static float s_lastReport;

        // Set by the budget governor each time it hands out a budget.
        internal static int LastBudget;
        internal static int LastBacklogScaled;
        internal static int LastNominal;

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void BeforeCreateObjects()
        {
            if (!Active) return;
            s_insideCreateObjects = true;
            s_inCall = 0;
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AfterCreateObjects()
        {
            if (!Active) return;
            s_insideCreateObjects = false;
            s_calls++;
            s_created += s_inCall;
            if (s_inCall > s_worstInOneCall) s_worstInOneCall = s_inCall;
            if (LastBudget > 0) { s_budgetSum += LastBudget; s_budgetSamples++; }
            Report();
        }

        // Counts only creations that actually produced an object; a skipped
        // piece returns null from the prefix and is not a creation.
        [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AfterCreateObject(ZDO zdo, GameObject __result)
        {
            if (!Active) return;
            if (__result == null || zdo == null) return;
            if (s_insideCreateObjects) s_inCall++;
            int hash = zdo.GetPrefab();
            s_byPrefab.TryGetValue(hash, out int n);
            s_byPrefab[hash] = n + 1;
        }

        private static void Report()
        {
            if (Time.realtimeSinceStartup - s_lastReport < 30f) return;
            s_lastReport = Time.realtimeSinceStartup;
            if (s_calls == 0) { s_byPrefab.Clear(); return; }

            float perCall = (float)s_created / s_calls;
            float meanBudget = s_budgetSamples > 0 ? (float)s_budgetSum / s_budgetSamples : -1f;
            Log.Info($"[Creations] {s_created:N0} object(s) over {s_calls:N0} CreateObjects call(s) = {perCall:0.0} per call " +
                     $"(worst {s_worstInOneCall:N0}); mean budget handed out {meanBudget:0.0} " +
                     $"(last backlog/100 = {LastBacklogScaled}, nominal = {LastNominal}). " +
                     (meanBudget > 0 && perCall > meanBudget * 1.5f
                        ? "MORE CREATED THAN THE BUDGET ALLOWS — something is creating outside the budgeted loop."
                        : "Creations are within the budget, so the budget is the whole story."));

            var scene = ZNetScene.instance;
            var top = s_byPrefab.OrderByDescending(k => k.Value).Take(12).ToList();
            long total = 0; foreach (var kv in s_byPrefab) total += kv.Value;
            long topSum = 0; foreach (var kv in top) topSum += kv.Value;
            var sb = new System.Text.StringBuilder();
            sb.Append($"[Creations] {s_byPrefab.Count:N0} distinct prefab(s); the top 12 are {100.0 * topSum / System.Math.Max(1, total):0}% of them:");
            foreach (var kv in top)
            {
                string name = "?";
                if (scene != null)
                {
                    var go = scene.GetPrefab(kv.Key);
                    if (go != null) name = go.name;
                }
                sb.Append($"\n    {kv.Value,7:N0}  {name}");
            }
            Log.Info(sb.ToString());

            s_calls = 0; s_created = 0; s_worstInOneCall = 0;
            s_budgetSum = 0; s_budgetSamples = 0;
            s_byPrefab.Clear();
        }

        // ═══ WHY THE QUEUE NEVER EMPTIES — BY REASON, NOT BY THEORY ═══════════════════════════════════════════
        // Measured in town, 2026-09-26:
        //     walk 1,912 ms of up to 107,306 near
        //     sort+create 5,357 ms of up to 24,187 candidate(s), 20,480 created
        //     budget held back 454 of 512 pass(es) (asked up to 241, allowed 40)
        // 20,480 over 512 passes is exactly 40/pass — the cap, every pass — with the queue parked at 24,187. It
        // is a treadmill, not a backlog, and every frame it survives pays the full walk, the full O(n log n)
        // sort, and a CreateObject call per entry.
        //
        // I have now reached the wrong cause twice from reading code alone. First I concluded EBM's skip left
        // ZDOs uncreated forever; ZoneTracker sets zdo.Created on that path, so no. Then I concluded they were
        // prefabs missing from this stack, which the comment at the top of this file had already measured — but
        // vanilla logs "Missing prefab hash" on exactly that path and this run has ZERO of them (Unity capture
        // verified live with 3,260 [Unity] lines, so that is a real negative, not a broken probe).
        //
        // So this stops guessing. Every candidate is sorted into the reason it has no instance, straight from
        // the same predicates vanilla and we use, and the cheapest question — does the reason even belong to
        // us — is answered before anything is changed. Once every CandidateCensusSeconds so a 24k walk is a
        // rounding error, and only when there is a queue worth explaining.
        //
        // ── THE FIRST VERSION OF THIS CENSUS ANSWERED THE WRONG QUESTION ─────────────────────────────────────
        // It asked SkipEligibility.Get(hash).Skippable and called the result "bake-skippable". That is prefab
        // CLASS eligibility - the first of eight tests in the skip decision - so it also swallowed every piece
        // that was class-eligible and then failed one of the seven runtime tests behind it. Those pieces are the
        // exact opposite of held back: TrySkipCreation looked at each one and declined it, which is WHY it is
        // still queued, because a real skip sets zdo.Created and a skipped ZDO is never a candidate again. The
        // bucket read as "EBM is holding these back" while listing pieces EBM had waved through, and it took
        // them out of the bucket that decides what to do next - creatable, waiting on nothing but budget.
        //
        // It now asks ZoneTracker.WouldSkip, the same predicate TrySkipCreation decides on, so the two cannot
        // drift: a reason can only appear here by existing there. Order mirrors the real pipeline - vanilla's
        // zone-ready gate runs in the create loop BEFORE CreateObject, so it is tested before the skip, and a
        // ZDO failing both is filed under the one it reaches first.
        //
        // The walk is timed and reports its own cost. Buckets past the class test are not free - the last two
        // reach PieceIdentity and ZdoMatches - and the honest way to find out whether a diagnostic has become
        // a hitch in a mod about frame time is to measure it rather than to cap it on a guess.
        private static float s_lastBucket;
        private static readonly Dictionary<int, int> s_stuckByPrefab = new Dictionary<int, int>();
        private static readonly int[] s_byReason =
            new int[System.Enum.GetValues(typeof(EasyBake.ZoneTracker.SkipReason)).Length];

        internal static void BucketCandidates(List<ZDO> candidates)
        {
            if (!Active || candidates == null) return;
            int n = candidates.Count;
            if (n < 500) return;   // nothing to explain; the queue is draining normally

            float every = FiresEasyBakeMeshesPlugin.CandidateCensusSeconds != null
                ? FiresEasyBakeMeshesPlugin.CandidateCensusSeconds.Value : 15f;
            if (every <= 0f) return;
            if (Time.realtimeSinceStartup - s_lastBucket < every) return;
            s_lastBucket = Time.realtimeSinceStartup;

            var scene = ZNetScene.instance;
            var zones = ZoneSystem.instance;
            if (scene == null || zones == null) return;

            // ── SAMPLED, BECAUSE THE FULL WALK COST 83.8 ms OF A 183 ms FRAME ───────────────────────────────
            // Measured in town on 1.2.73: 83.8 / 66.5 / 58 / 31.5 / 22.4 ms against queues up to 56,568 — a
            // third of the frame, in a diagnostic whose whole job is to explain that frame. The cost does NOT
            // track queue length: 56,568 entries cost 66.5 ms while a 2,364 queue cost 0.4 ms, because those
            // 2,364 all short-circuited on `zone awaiting recreate` before reaching PieceIdentity.From. It is
            // the two deepest buckets that cost — NotInBake reaches PieceIdentity.From, ZdoChanged reaches
            // ZdoMatches — so capping the WALK is what bounds it, and 1.2.73 deliberately shipped uncapped to
            // get that number rather than guess it.
            //
            // A stride is honest here specifically because of WHERE this runs. Vanilla sorts the candidate
            // list AFTER the budget call this sits inside, so at this moment the list is still in near-walk
            // order, not distance order. Every k-th entry is therefore a sample of the whole queue rather than
            // of its nearest slice, which a head-truncation would have given.
            int cap = FiresEasyBakeMeshesPlugin.CandidateCensusSample != null
                ? FiresEasyBakeMeshesPlugin.CandidateCensusSample.Value : 4000;
            int stride = (cap > 0 && n > cap) ? (n + cap - 1) / cap : 1;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            int noPrefab = 0, absentPrefab = 0, zoneNotReady = 0, nullZdo = 0, sampled = 0;
            System.Array.Clear(s_byReason, 0, s_byReason.Length);
            s_stuckByPrefab.Clear();

            for (int i = 0; i < n; i += stride)
            {
                sampled++;
                var zdo = candidates[i];
                if (zdo == null) { nullZdo++; continue; }

                int hash = zdo.GetPrefab();
                if (hash == 0) { noPrefab++; continue; }

                // Absent from THIS client's ZNetScene: uncreatable here, and only a server ever cleans it up.
                // Resolved here rather than inside WouldSkip so the hash list below still works when skipping is
                // switched off entirely, and so the CreateObject path does not pay for a label only this reads.
                if (scene.GetPrefab(hash) == null)
                {
                    absentPrefab++;
                    s_stuckByPrefab.TryGetValue(hash, out int a); s_stuckByPrefab[hash] = a + 1;
                    continue;
                }

                // Vanilla's own `continue` in the create loop — it never even calls CreateObject for these.
                if (!zones.IsZoneReadyForType(zdo.GetSector(), zdo.Type)) { zoneNotReady++; continue; }

                EasyBake.ZoneTracker.WouldSkip(zdo, out var reason);
                s_byReason[(int)reason]++;
            }
            clock.Stop();

            s_scale = sampled > 0 ? (double)n / sampled : 1.0;

            // ── "OURS" MEANS THE PIECE IS NOT GOING TO BE CREATED, NOT THAT THE SKIP LOOKED AT IT ────────────
            // Every reason below except one ends in TrySkipCreation returning FALSE, and false means vanilla goes
            // on to create the piece exactly as if this mod were not installed. A build hold, a zone holding its
            // real pieces, a pending hand-back, a piece that no longer matches its bake - the skip declined all
            // of them, so they are queued for budget like anything else. Grouping them as "held back by EBM"
            // would be the very mistake this census was rewritten to stop making, one level up.
            //
            // The single reason that really withholds a piece is the defer, and only while
            // DeferCreationWhileBaking is on - which it is not by default, for reasons measured in DeferCreation.
            // So with the defer off this line should read zero held back, and the queue is budget, all of it.
            int bakePending = Reason(EasyBake.ZoneTracker.SkipReason.ZoneBakePending);
            bool deferHolds = EasyBake.DeferCreation.Enabled;
            int heldByUs = deferHolds ? bakePending : 0;
            int waitingOnBudget = Reason(EasyBake.ZoneTracker.SkipReason.SkipInactive)
                                + Reason(EasyBake.ZoneTracker.SkipReason.PrefabNotSkippable)
                                + Reason(EasyBake.ZoneTracker.SkipReason.BuildHold)
                                + Reason(EasyBake.ZoneTracker.SkipReason.ZoneUnknown)
                                + Reason(EasyBake.ZoneTracker.SkipReason.AwaitingRecreate)
                                + Reason(EasyBake.ZoneTracker.SkipReason.ZoneHoldingReal)
                                + Reason(EasyBake.ZoneTracker.SkipReason.NotInBake)
                                + Reason(EasyBake.ZoneTracker.SkipReason.ZdoChanged)
                                + (deferHolds ? 0 : bakePending);

            // ── THE BAKE ABSORBS THESE FOR FREE, AND THAT IS THE POINT OF THE MOD ───────────────────────────
            // A skip returns null from CreateObject, so vanilla's create loop neither counts it against the
            // budget nor breaks on it:
            //     if (CreateObject(item) != null) { created++; if (created >= num) break; }
            // Only a REAL creation spends budget. So the queue splits into the part that costs 0.183 ms each
            // and a budget slot, and the part the bake takes for nothing — and the second is the number that
            // says how much work EBM is actually removing from the frame.
            int freeToSkip = Reason(EasyBake.ZoneTracker.SkipReason.WouldBeSkipped);
            string sample = stride == 1 ? "" : $", 1-in-{stride} sample of {sampled:N0} scaled up";

            Log.Info($"[Candidates] {n:N0} ZDO(s) queued with no instance, bucketed in {clock.Elapsed.TotalMilliseconds:0.0} ms{sample}: " +
                     $"{waitingOnBudget:N0} need a real create (0.183 ms and a budget slot each), " +
                     $"{freeToSkip:N0} the bake absorbs FREE when the loop reaches them, " +
                     $"{heldByUs:N0} withheld by EBM's defer, " +
                     $"{Scale(zoneNotReady):N0} in a zone not ready for their type (vanilla skips these every pass), " +
                     $"{Scale(absentPrefab):N0} whose prefab is ABSENT from this client, " +
                     $"{Scale(noPrefab):N0} with no prefab set" +
                     (nullZdo == 0 ? "" : $", {Scale(nullZdo):N0} null") + ".");

            // Why the skip passed each one over, in the order the stages are evaluated. This does not change who
            // creates them - vanilla does - it sizes how much of the queue the skip could ever take, which is the
            // only actionable thing in the breakdown.
            Log.Info($"[Candidates] why the skip passed them over: " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.SkipInactive):N0} skipping inactive this session, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.PrefabNotSkippable):N0} prefab can never be skipped, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.BuildHold):N0} near a build tool, " +
                     $"{bakePending:N0} zone's bake has not landed" + (deferHolds ? " (withheld)" : "") + ", " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.ZoneUnknown):N0} zone has no bake and no cache, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.AwaitingRecreate):N0} zone awaiting recreate, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.ZoneHoldingReal):N0} zone holding its real pieces, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.NotInBake):N0} not in their zone's bake, " +
                     $"{Reason(EasyBake.ZoneTracker.SkipReason.ZdoChanged):N0} no longer match what was baked.");

            // ── THE WARNING THAT USED TO BE HERE WAS WRONG, AND IT FIRED 12 TIMES SAYING SO ─────────────────
            // 1.2.73 warned that a queued ZDO which WouldSkip accepts "was skipped and did not stay skipped".
            // It fired at 29,851 of a 56,568 queue — 53% — and read as the skip collapsing. It is not. The
            // invariant it asserted is checked at a point in the frame where it cannot hold:
            //
            //     int num = Mathf.Max(count / 100, maxCreatedPerFrame);   <- this census runs INSIDE this call
            //     m_tempCurrentObjects2.Sort(ZDOCompare);
            //     foreach (ZDO item in m_tempCurrentObjects2) { ... CreateObject(item) ... }
            //
            // The census is called from the budget transpiler, which replaces that Mathf.Max — so it runs
            // BEFORE the sort and BEFORE a single CreateObject of this frame. Every candidate it sees is
            // unprocessed BY CONSTRUCTION. zdo.Created cannot have been set yet, so "would be skipped and is
            // still queued" is the normal, expected state of every skippable piece at that instant, and the
            // warning was structurally guaranteed to fire whenever the bake had anything to absorb.
            //
            // The same reading also explains why it was not merely noisy but backwards: those 29,851 were the
            // mod WORKING — a fifty-three percent share of the queue that costs no budget at all. It is now
            // reported as that, on the line above.
            //
            // Left as a comment rather than deleted: this is the second time in two versions that an asserted
            // invariant produced a confident false alarm in this file, both times by ignoring where in the
            // frame the question is asked. A third instrument here should state its observation point first.

            if (absentPrefab > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[Candidates] {s_stuckByPrefab.Count:N0} distinct ABSENT prefab hash(es) — these can NEVER be " +
                          "created on this client and are re-offered every frame forever" +
                          (stride == 1 ? ":" : $" (seen in a 1-in-{stride} sample, so rare hashes may be missing entirely):"));
                foreach (var kv in s_stuckByPrefab.OrderByDescending(k => k.Value).Take(12))
                    sb.Append($"\n    {Scale(kv.Value),7:N0}  hash {kv.Key}");
                Log.Warn(sb.ToString());
            }
        }

        // How much of the queue one walked entry stands for. 1.0 when the whole queue was walked.
        private static double s_scale = 1.0;

        /// <summary>A sampled count scaled back to the whole queue; exact when the walk was not strided.</summary>
        private static int Scale(int v) => s_scale <= 1.0 ? v : (int)(v * s_scale + 0.5);

        private static int Reason(EasyBake.ZoneTracker.SkipReason reason) => Scale(s_byReason[(int)reason]);
    }
}
