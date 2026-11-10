using System;
using System.Reflection;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.193 (lead 11-08, DESIGN_CleanStreaming with [fgn]): FGN 1.6.54+ publishes one zone-streaming signal on
    // FiresGhettoNetworkMod.FgnStreaming: a live flag StreamingSignalActive and two static events, ZoneFilling(Vector2s) (a near zone
    // has objects waiting to be created) and ZoneSettled(Vector2s) (it has none left and is loaded). EBM reads it by reflection only
    // (no build dependency) and re-reads the live flag on use (never an API version). With no FGN, an older FGN, or the flag false,
    // Active is false and EBM keeps its own path. A throw from FGN goes through ForeignCall: one Warning, then off for the session.
    // 1.2.208 (lead 11-08, Fire: "none of the three relies on another"; zero link between FGN / EBM / FF in either direction): EBM no longer
    // looks FGN up. Active is always false, so every caller takes EBM's own zone-settle timing (its quiet windows). Kept as a stub so the callers
    // read the same; nothing here touches another mod.
    internal static class FgnLink
    {
        internal static int SettledEvents, FillingEvents;
        internal static bool Active => false;
        internal static bool Subscribed => false;
    }
}
