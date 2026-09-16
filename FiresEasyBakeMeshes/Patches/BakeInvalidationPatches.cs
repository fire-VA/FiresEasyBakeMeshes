using HarmonyLib;

namespace FiresEasyBakeMeshes.Patches
{
    // Genuine removals (vanilla hammer remove, Infinity Hammer pick/undo, WEC object
    // remove) destroy the piece's ZDO — zone unloads never do, persistent ZDOs survive
    // the player walking away. That makes OnZDODestroyed the clean "removed for real"
    // signal ZoneTracker.OnPieceDestroyed lacks: route it to the tracker so a baked
    // zone drops the removed piece's geometry via a settle-delayed rebake instead of
    // ghost-rendering it.
    [HarmonyPatch(typeof(ZNetScene), "OnZDODestroyed")]
    public static class ZNetScene_OnZDODestroyed_BakeInvalidate_Patch
    {
        [HarmonyPrefix]
        public static void Prefix(ZDO zdo)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            try { EasyBake.ZoneTracker.OnZdoDestroyed(zdo); }
            catch { }
        }
    }
}
