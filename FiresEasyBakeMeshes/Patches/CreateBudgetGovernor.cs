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
        private static float s_lastReport;

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
            int vanilla = Mathf.Max(backlogScaled, nominal);
            s_frames++;
            if (backlogScaled > s_peakAsked) s_peakAsked = backlogScaled;

            int cap = FiresEasyBakeMeshesPlugin.CreateBudgetPerFrame != null
                    ? FiresEasyBakeMeshesPlugin.CreateBudgetPerFrame.Value : 0;
            // 0 = off, and the cap can never pull the budget below what the game
            // itself asked for — a loading screen still gets its 100.
            int given = cap <= 0 ? vanilla : Mathf.Min(vanilla, Mathf.Max(nominal, cap));
            CreationCensus.LastBudget = given;
            CreationCensus.LastBacklogScaled = backlogScaled;
            CreationCensus.LastNominal = nominal;
            if (given < vanilla) s_capped++;
            if (given > s_peakGiven) s_peakGiven = given;

            if (Time.realtimeSinceStartup - s_lastReport > 30f)
            {
                s_lastReport = Time.realtimeSinceStartup;
                if (s_frames > 0)
                    Log.Info($"[CreateBudget] {s_capped:N0} of {s_frames:N0} pass(es) capped; " +
                        $"the backlog asked for up to {s_peakAsked:N0} object(s) in one frame, allowed {s_peakGiven:N0}. " +
                        (cap <= 0 ? "Cap is OFF." : $"Cap {cap}."));
                s_frames = 0; s_capped = 0; s_peakAsked = 0; s_peakGiven = 0;
            }
            return given;
        }
    }
}
