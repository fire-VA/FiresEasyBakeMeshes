using HarmonyLib;

namespace FiresEasyBakeMeshes.Patches
{
    // ═══ THE ONE PLACE A HIT FOR A MISSING PIECE CAN BE SEEN ══════════════════
    // ZRoutedRpc.HandleRoutedRPC is private, which does not matter to Harmony, and RoutedRPCData is a public
    // class with public fields, so the call can be read as-is with no reflection.
    //
    // POSTFIX, NOT PREFIX, DELIBERATELY. Every routed RPC in the game passes through here, so the cheapest
    // correct thing is to let vanilla do its work and then look at what it declined to deliver. A prefix would
    // have to decide whether to return false, and returning false here would suppress delivery for whatever
    // else is in flight - and under HarmonyX a prefix returning false does NOT skip other mods' prefixes, so
    // that decision would not even be reliably ours to make. A postfix cannot break anything it does not touch.
    //
    // The filter that keeps this off the hot path is in DamageReplay.OnRoutedRpc, and it is one int compare
    // against "RPC_Damage".GetStableHashCode() before anything else is looked at.
    [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
    internal static class ZRoutedRpc_HandleRoutedRPC_DamageCatch
    {
        private static void Postfix(ZRoutedRpc.RoutedRPCData data)
        {
            if (!FiresEasyBakeMeshesPlugin.SkipBakedPieceObjects.Value) return;
            EasyBake.DamageReplay.OnRoutedRpc(data);
        }
    }
}
