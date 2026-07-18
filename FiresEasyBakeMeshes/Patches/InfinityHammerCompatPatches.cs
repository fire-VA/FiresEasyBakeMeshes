using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Infinity Hammer compatibility for mesh batching.
    //
    // IH builds its placement ghost by Instantiate()ing the LIVE hovered
    // object (ObjectSelection ctor → HammerHelper.SafeInstantiate /
    // ChildInstantiate — it clones the scene object, not the prefab, so the
    // ghost carries scale/wear/visual state). When batching has baked that
    // object's zone, the source piece's renderers + LODGroups are disabled
    // (the combined mesh draws instead) and the clone INHERITS the disabled
    // state — the player gets an invisible ghost. Field report: "selected a
    // 2x2 stone floor from another player's build — it was on my hammer with
    // no preview other than the gizmo axis." Own/fresh pieces and nature
    // objects were fine because those are never batched, which made it look
    // creator-dependent; the actual split is baked vs live.
    //
    // The postfixes below re-enable on the CLONE exactly the visuals the bake
    // disabled on the SOURCE (ZoneTracker.ReenableCloneVisuals pairs them by
    // traversal order). Prepare() gates the classes: without IH installed
    // they are skipped entirely, and Harmony's class processor logs nothing.
    internal static class InfinityHammerReflection
    {
        internal static Type HammerHelper =>
            Type.GetType("InfinityHammer.HammerHelper, InfinityHammer");
    }

    [HarmonyPatch]
    public static class IH_SafeInstantiate_BakedGhost_Patch
    {
        public static bool Prepare() => InfinityHammerReflection.HammerHelper != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(InfinityHammerReflection.HammerHelper, "SafeInstantiate",
                new[] { typeof(ZNetView), typeof(GameObject) });

        [HarmonyPostfix]
        public static void Postfix(ZNetView view, GameObject __result)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            try { EasyBake.ZoneTracker.ReenableCloneVisuals(view, __result); }
            catch (Exception ex) { EasyBakeLog.Warn($"[IHCompat] ghost visual restore failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch]
    public static class IH_ChildInstantiate_BakedGhost_Patch
    {
        public static bool Prepare() => InfinityHammerReflection.HammerHelper != null;

        public static MethodBase TargetMethod() =>
            AccessTools.Method(InfinityHammerReflection.HammerHelper, "ChildInstantiate",
                new[] { typeof(ZNetView), typeof(GameObject) });

        [HarmonyPostfix]
        public static void Postfix(ZNetView view, GameObject __result)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return;
            try { EasyBake.ZoneTracker.ReenableCloneVisuals(view, __result); }
            catch (Exception ex) { EasyBakeLog.Warn($"[IHCompat] ghost visual restore failed: {ex.Message}"); }
        }
    }

    // Genuine removals (vanilla hammer remove, IH pick/undo, WEC object
    // remove) destroy the piece's ZDO — zone unloads never do, persistent
    // ZDOs survive the player walking away. That makes OnZDODestroyed the
    // clean "removed for real" signal ZoneTracker.OnPieceDestroyed lacks:
    // route it to the tracker so a baked zone drops the removed piece's
    // geometry via a settle-delayed rebake instead of ghost-rendering it
    // ("the piece was deleted and the texture stayed there").
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
