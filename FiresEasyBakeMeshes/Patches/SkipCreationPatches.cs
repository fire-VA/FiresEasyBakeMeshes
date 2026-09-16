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
}
