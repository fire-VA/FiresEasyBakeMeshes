using HarmonyLib;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.271 (lead 11-10 10 PM, Fire's layout rule "no stacked hot-method patches"): EBM's ONE postfix on each vanilla ZDO sector method.
    // Before, the same method carried up to four EBM postfixes (ZdoEventDirty, ScanGate's event counter, SectorArrivals, StreamPacer), each a
    // separate Harmony call per ZDO add / remove, and these run for every ZDO that streams in, moves sector or is destroyed. Now one call per
    // event runs each owner's step in the order they ran before. Every step keeps its own key gate and its own try, so one failing never stops
    // the others. Default priority, as all four had, so the order against other mods' patches is unchanged.
    [HarmonyPatch]
    internal static class SectorHooks
    {
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        private static void Added(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            global::FiresEasyBakeMeshes.Patches.ZdoEventDirty.Dirty = true;
            try { ScanGate.NoteSectorAdd(zdo, sectorIndex); } catch { }   // 1.2.492: pairs with the remove of a sector move (ScanGateIgnoreInAreaMoves)
            try { SectorArrivals.OnAdd(zdo, sectorIndex); } catch { }
            try { StreamPacer.OnSectorAddHook(zdo, sectorIndex); } catch { }
        }

        [HarmonyPatch(typeof(ZDOMan), "InitialAddToSector"), HarmonyPostfix]
        private static void InitialAdded(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            global::FiresEasyBakeMeshes.Patches.ZdoEventDirty.Dirty = true;
            try { StreamPacer.OnSectorAddHook(zdo, sectorIndex); } catch { }
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPostfix]
        private static void Removed(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            global::FiresEasyBakeMeshes.Patches.ZdoEventDirty.Dirty = true;
            try { ScanGate.NoteSectorRemove(zdo, sectorIndex); } catch { }   // 1.2.492: held until the next add or the next gate decision
            try { StreamPacer.OnSectorRemoveHook(zdo); } catch { }
        }
    }
}
