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
        private static float s_lastBucket;
        private static readonly Dictionary<int, int> s_stuckByPrefab = new Dictionary<int, int>();

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

            int noPrefab = 0, absentPrefab = 0, zoneNotReady = 0, wouldSkip = 0, creatable = 0;
            s_stuckByPrefab.Clear();

            for (int i = 0; i < n; i++)
            {
                var zdo = candidates[i];
                if (zdo == null) continue;

                int hash = zdo.GetPrefab();
                if (hash == 0) { noPrefab++; continue; }

                // Absent from THIS client's ZNetScene: uncreatable here, and only a server ever cleans it up.
                if (scene.GetPrefab(hash) == null)
                {
                    absentPrefab++;
                    s_stuckByPrefab.TryGetValue(hash, out int a); s_stuckByPrefab[hash] = a + 1;
                    continue;
                }

                // Vanilla's own `continue` in the create loop — it never even calls CreateObject for these.
                if (!zones.IsZoneReadyForType(zdo.GetSector(), zdo.Type)) { zoneNotReady++; continue; }

                // Ours: a piece a bake will draw, held back until the bake lands. Bounded by an expiry, so it
                // should not accumulate — if it dominates, that expiry is not working.
                if (EasyBake.SkipEligibility.Get(hash).Skippable) { wouldSkip++; continue; }

                creatable++;
            }

            Log.Info($"[Candidates] {n:N0} ZDO(s) queued with no instance: " +
                     $"{creatable:N0} creatable and just waiting for budget, " +
                     $"{wouldSkip:N0} bake-skippable, " +
                     $"{zoneNotReady:N0} in a zone not ready for their type (vanilla skips these every pass), " +
                     $"{absentPrefab:N0} whose prefab is ABSENT from this client, " +
                     $"{noPrefab:N0} with no prefab set.");

            if (absentPrefab > 0)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"[Candidates] {s_stuckByPrefab.Count:N0} distinct ABSENT prefab hash(es) — these can NEVER be " +
                          "created on this client and are re-offered every frame forever:");
                foreach (var kv in s_stuckByPrefab.OrderByDescending(k => k.Value).Take(12))
                    sb.Append($"\n    {kv.Value,7:N0}  hash {kv.Key}");
                Log.Warn(sb.ToString());
            }
        }
    }
}
