using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Pieces a bake already draws are not created on a multiplayer client; see ZoneTracker.TrySkipCreation.
    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    public static class ZNetScene_CreateObject_Skip_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZDO zdo, ref GameObject __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            bool skip;
            try { skip = EasyBake.ZoneTracker.TrySkipCreation(zdo); }
            catch (System.Exception ex)
            {
                EasyBake.ZoneTracker.LogSkipFailureOnce(ex);
                skip = false;
            }
            if (!skip) return true;
            __result = null;
            return false;
        }
    }

    // Vanilla holds a spawn or teleport until every object near the point has an instance, which a skipped piece never
    // gets; the area counts as ready when everything missing is a skipped piece.
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    public static class ZNetScene_IsAreaReady_Skip_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Vector3 point, ref bool __result)
        {
            if (__result) return;
            try { if (EasyBake.ZoneTracker.IsAreaReadyCountingSkipped(point)) __result = true; }
            catch { }
        }
    }

    // ── VANILLA DELETES THE WORLD WHEN CreateObject RETURNS NULL ON A SERVER ──────────────────────────────
    // ZNetScene.CreateObjectsSorted:
    //     if (CreateObject(zdo) != null) { ++created; ... }
    //     else if (ZNet.instance.IsServer())
    //     { zdo.SetOwner(...); ZLog.Log("Destroyed invalid prefab ZDO:" + zdo.m_uid); ZDOMan.instance.DestroyZDO(zdo); }
    //
    // Returning null is how a skip is signalled, so on a host that signal IS vanilla's "invalid prefab, delete
    // it" path - the same process owning both sides is the entire reason skipping was client-only. Blocking
    // exactly that one deletion is what makes a host no different from a joined client.
    //
    // ONE-SHOT, NOT A MEMBERSHIP TEST. ConsumeJustSkipped matches only the ZDO handed over by the skip that
    // just ran, and clears itself. A broader "is this skipped?" test would refuse REAL deletions whenever the
    // tracker disagreed, and a piece that cannot be destroyed is a far worse defect than one drawn twice.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
    public static class ZDOMan_DestroyZDO_SkipGuard_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZDO zdo)
        {
            if (zdo == null) return true;
            if (!EasyBake.ZoneTracker.ConsumeJustSkipped(zdo)) return true;
            s_blocked++;
            return false;
        }

        private static int s_blocked;

        /// <summary>For the status box: how many world-deletions the skip signal would otherwise have caused.</summary>
        internal static int BlockedDeletions => s_blocked;
    }
}
