using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.253 [Cache] RemoteZoneValidation: when a new ZDO last arrived per sector, so a cached zone is judged on what the server delivered.
    // ZDOMan.RPC_ZDOData cannot do this: it takes only (ZRpc, ZPackage), the package is already consumed in a postfix, and re-reading it means parsing every
    // ZDO twice. AddToSector is ZDOMan's own per-ZDO path and is handed the sector index. It also runs when a ZDO moves between sectors, so only a ZDO with
    // DataRevision 0 counts: CreateNewZDO reaches AddToSector before RPC_ZDOData writes the received revision, and a creature walking across keeps none at 0.
    // Main thread only. No FGN, Core or FiresFixes involved.
    [HarmonyPatch]
    internal static class SectorArrivals
    {
        private const int MaxSectors = 4096;
        private const float PruneOlderThanS = 120f;
        private static readonly Dictionary<uint, float> s_lastArrival = new Dictionary<uint, float>();
        private static readonly List<uint> s_pruneScratch = new List<uint>();

        internal static bool KeyOn => FiresEasyBakeMeshesPlugin.RemoteZoneValidation != null && FiresEasyBakeMeshesPlugin.RemoteZoneValidation.Value;

        // A dedicated server keeps today's rule: it has no player to be near a zone.
        internal static bool RuleActive => KeyOn && ZNet.instance != null && !(ZNet.instance.IsServer() && ZNet.instance.IsDedicated());

        internal static void Clear() { s_lastArrival.Clear(); s_pruneScratch.Clear(); }

        // Negative when nothing has arrived for this sector since the stamps began (or it was pruned as long quiet).
        internal static float LastArrival(Vector2s coord)
            => s_lastArrival.TryGetValue(ZoneSystem.SectorToIndex(coord).Sector, out float at) ? at : -1f;

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        private static void AddToSectorPostfix(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            if ((!KeyOn && !(FiresEasyBakeMeshesPlugin.CacheEmptyZoneDropsCache != null && FiresEasyBakeMeshesPlugin.CacheEmptyZoneDropsCache.Value)) || zdo == null || zdo.DataRevision != 0U) return;   // 1.2.255: EmptyZoneDropsCache reads these stamps too
            s_lastArrival[sectorIndex.Sector] = Time.unscaledTime;
            if (s_lastArrival.Count > MaxSectors) Prune();
        }

        private static void Prune()
        {
            float cutoff = Time.unscaledTime - PruneOlderThanS;
            s_pruneScratch.Clear();
            foreach (var kv in s_lastArrival) if (kv.Value < cutoff) s_pruneScratch.Add(kv.Key);
            for (int i = 0; i < s_pruneScratch.Count; i++) s_lastArrival.Remove(s_pruneScratch[i]);
            s_pruneScratch.Clear();
            if (s_lastArrival.Count > MaxSectors) s_lastArrival.Clear();   // every entry recent is not a real world; the zones only wait longer
        }
    }
}
