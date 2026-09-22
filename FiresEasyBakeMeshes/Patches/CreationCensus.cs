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
    }
}
