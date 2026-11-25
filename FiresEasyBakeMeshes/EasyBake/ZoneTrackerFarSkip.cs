using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ═══ 1.2.282 [Optimize] SkipFarBakedPieces (Fire 11-11: "we shouldn't create far baked pieces until we're in range"; plan approved by the lead) ═══
    // The baked-piece skip existed only for a joined client and invulnerable pieces (SkipOnHost / SkipDamageablePieces off), so a single-player or host
    // town created every piece (134k instances at the big town) and destroyed every one on leaving. This mode lets the host / single player and
    // damageable pieces skip too, but ONLY beyond vanilla's own ACTIVE AREA of every player (1.2.284, [generator] review / lead / Fire's "no game-function
    // loss for fps"): vanilla WearNTear.UpdateWear (WearNTear.cs:617-627) simulates nothing outside the active area (it pins support to max and returns),
    // so out there a piece exists only to be drawn, which EBM already does. Inside it (wear, support, snow, event and underwater damage, monsters)
    // every piece is created as vanilla. 1.2.282's 64 m band was inside the active area and dropped those; its rain catch-up and raid doubling are gone.
    //  - band = ZNetScene.InActiveArea(point, zone of the local player / of every peer's reference point on the host); a far skip needs one extra zone
    //    ring of distance (hysteresis), a hand-back happens on entering the active area;
    //  - only pure static structure (no door / bed / station / container / fire / light / ward / sign / stand / smelter ... : HasBehaviour) is ever
    //    left uncreated; everything with behaviour is created as vanilla wherever it is;
    //  - a far-skipped piece keeps its stand-in collider (the existing skip machinery: hits are replayed, build tools bring pieces back, a changed
    //    piece becomes real); entering the active area brings it back the vanilla way (ZDO.Created = false), at most 400 a tick; no support chain:
    //    vanilla pins support to max outside the active area, so a stand-in neighbour read as ground there is vanilla's own state.
    // Nothing in any ZDO changes by the skip itself.
    internal static partial class ZoneTracker
    {
        internal static bool FarOn => FiresEasyBakeMeshesPlugin.SkipFarBakedPieces != null && FiresEasyBakeMeshesPlugin.SkipFarBakedPieces.Value;



        private static readonly List<Vector3> s_bandCenters = new List<Vector3>(8);
        private static readonly List<Vector2s> s_bandZones = new List<Vector2s>(8);
        private static float s_bandNext;
        private static readonly HashSet<ZDO> s_farSkipped = new HashSet<ZDO>();
        private static bool s_lastDecisionFar;

        // census
        private static int c_farSkipped, c_lateBand, c_lateOther;
        private static float s_farCensusNext;

        // ---- band: vanilla's active area around each player
        private static void RefreshBand()
        {
            float now = Time.unscaledTime;
            if (now < s_bandNext) return;
            s_bandNext = now + 0.5f;
            s_bandCenters.Clear(); s_bandZones.Clear();
            var me = Player.m_localPlayer;
            if (me != null) s_bandCenters.Add(me.transform.position);
            var net = ZNet.instance;
            if (net != null && net.IsServer())
                foreach (var peer in net.GetPeers()) if (peer != null) s_bandCenters.Add(peer.m_refPos);
            foreach (var c in s_bandCenters) s_bandZones.Add(ZoneSystem.GetZone(c));
            // 1.2.287 (lead 11-11 allocation capture: ZoneTracker.FarTick 212 MB, 5.0M allocations of 44 B): vanilla's InActiveArea asks
            // ZNet.GetSyncedSimulationDistance() -> GetDesiredSimulationDistance() on EVERY call; asked once here per band refresh instead
            if (net != null) s_simDist = net.GetSyncedSimulationDistance();
            s_zoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : 64f;
        }

        private static SimulationDistance s_simDist;
        private static float s_zoneSize = 64f;

        // vanilla ZNetScene.PointInsideActiveArea, line for line, with the synced simulation distance cached for the band refresh
        private static bool InActive(Vector3 point, Vector2s zone)
        {
            point.y = 0f;
            float num = s_simDist.NearSimulationDistance == 1 ? 1f : 1.5f;
            Vector3 zonePos = ZoneSystem.GetZonePos(zone);
            bool flag = Utils.ChebyshevDistance(zonePos, point) <= num * s_zoneSize;
            if (s_simDist.NearSimulationDistance == 2 && !s_simDist.IsClassic)
            {
                float num2 = s_zoneSize * 1.75f;
                return flag && (zonePos - point).sqrMagnitude < num2 * num2;
            }
            return flag;
        }

        // ring 0 = vanilla's active area; ring 1 = the active area of the centre zone or any of its 8 neighbours (one zone of hysteresis)
        private static bool InBand(Vector3 p, int ring)
        {
            for (int i = 0; i < s_bandZones.Count; i++)
            {
                var z = s_bandZones[i];
                if (ring == 0) { if (InActive(p, z)) return true; continue; }
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                    if (InActive(p, new Vector2s(z.x + dx, z.y + dy))) return true;
            }
            return false;
        }

        // ---- decision (called from WouldSkip when the classic skip would refuse: host without SkipOnHost, or a damageable piece)
        private static readonly Dictionary<int, bool> s_staticByPrefab = new Dictionary<int, bool>();
        private static bool FarEligible(ZDO zdo, SkipEligibility.PrefabInfo info, Vector3 position)
        {
            if (!FarOn) return false;
            RefreshBand();
            if (s_bandZones.Count == 0 || InBand(position, 1)) return false;   // beyond the active area by one more zone ring
            int h = zdo.GetPrefab();
            if (!s_staticByPrefab.TryGetValue(h, out bool st)) { st = info != null && info.Prefab != null && !HasBehaviour(info.Prefab); s_staticByPrefab[h] = st; }
            return st;
        }

        /// <summary>After TrySkipCreation skipped a piece: remember it as far-skipped when the far rule was what allowed it.</summary>
        private static void NoteFarSkip(ZDO zdo)
        {
            if (!s_lastDecisionFar) return;
            s_lastDecisionFar = false;
            s_farSkipped.Add(zdo); c_farSkipped++;
        }

        // ---- per tick: hand back the far-skipped pieces that entered a player's active area. 1.2.284 (lead 11-11, Fire: the 1.2.282 moving circle
        // regression, "41389 support chain" in 30 s): NO support-chain flood. Vanilla pins support to max for every piece outside the active area
        // (WearNTear.UpdateWear), so a live piece at the edge reading a stand-in neighbour as solid ground is exactly vanilla's state; only pieces that
        // ENTER the active area come back, at most HandBackCap a tick (the rest next tick), and the census shows the largest hand-back per trigger.
        private const int HandBackCap = 400;
        private static readonly List<ZDO> s_bringUp = new List<ZDO>(256);
        private static readonly Queue<ZDO> s_pending = new Queue<ZDO>();
        private static int c_largestTrigger, c_capped;

        private static readonly List<Vector3> s_lastScanCenters = new List<Vector3>(8);
        private static bool BandMoved()
        {
            if (s_lastScanCenters.Count != s_bandCenters.Count) return true;
            for (int i = 0; i < s_bandCenters.Count; i++) if ((s_bandCenters[i] - s_lastScanCenters[i]).sqrMagnitude > 64f) return true;   // 8 m
            return false;
        }

        internal static void FarTick()
        {
            if (s_farSkipped.Count == 0 && s_pending.Count == 0) { FarCensus(); return; }
            if (!FarOn)   // key off: everything held comes back through the same capped queue, never all in one frame
            {
                if (s_pending.Count == 0) foreach (var z in s_farSkipped) s_pending.Enqueue(z);
                int k = 0; while (s_pending.Count > 0 && k < HandBackCap) { if (HandBackFar(s_pending.Dequeue())) { k++; c_lateOther++; } }
                FarCensus(); return;
            }
            RefreshBand();
            if (s_pending.Count == 0)
            {
                if (!BandMoved()) { FarCensus(); return; }   // a scan only when a player moved 8 m or the player count changed
                s_lastScanCenters.Clear(); s_lastScanCenters.AddRange(s_bandCenters);
                s_bringUp.Clear();
                foreach (var z in s_farSkipped) if (z != null && z.IsValid() && InBand(z.GetPosition(), 0)) s_bringUp.Add(z);
                if (s_bringUp.Count > c_largestTrigger) c_largestTrigger = s_bringUp.Count;
                foreach (var z in s_bringUp) s_pending.Enqueue(z);
            }
            int n = 0;
            while (s_pending.Count > 0 && n < HandBackCap) { if (HandBackFar(s_pending.Dequeue())) { n++; c_lateBand++; } }
            if (s_pending.Count > 0) c_capped++;
            FarCensus();
        }


        // the vanilla way back: Created = false, vanilla's next create pass makes it; the existing hand-back bookkeeping waits for it
        private static bool HandBackFar(ZDO zdo)
        {
            if (!s_farSkipped.Contains(zdo)) return false;
            s_farSkipped.Remove(zdo);
            if (zdo == null || !zdo.IsValid()) return false;
            var coord = EbmZones.Of(zdo.GetPosition());
            if (!_zones.TryGetValue(coord, out var state) || !state.Skipped.TryGetValue(zdo, out var sp)) return false;
            if (state.AwaitingRecreate == null)
            {
                state.AwaitingRecreate = new List<ZDO>(16);
                state.AwaitingIdentities = new List<MeshBaker.PieceIdentity>(16);
                state.AwaitingSince = Time.unscaledTime;
            }
            zdo.Created = false; Patches.ZdoEventDirty.Mark();
            state.AwaitingRecreate.Add(zdo);
            state.AwaitingIdentities?.Add(sp.Identity);
            state.Skipped.Remove(zdo);
            state.LastChangeUnscaledTime = Time.unscaledTime;

            return true;
        }

        internal static void FarForget() { s_farSkipped.Clear(); s_pending.Clear(); s_lastScanCenters.Clear(); s_staticByPrefab.Clear(); }

        private static void FarCensus()
        {
            float now = Time.unscaledTime;
            if (now < s_farCensusNext) return;
            s_farCensusNext = now + 30f;
            if (!EasyBakeLog.DebugOn() || (c_farSkipped == 0 && s_farSkipped.Count == 0 && c_lateBand + c_lateOther == 0)) return;
            EasyBakeLog.Debug($"[SkipFar] 30 s: {c_farSkipped} static piece(s) beyond the active area left uncreated (now held {s_farSkipped.Count}), active area of {s_bandZones.Count} player(s); "
                + $"created late: {c_lateBand} entered the active area (largest hand-back per trigger {c_largestTrigger}, capped at {HandBackCap} a tick in {c_capped} tick(s); no support-chain flood), {c_lateOther} key off / other");
            c_farSkipped = c_lateBand = c_lateOther = c_largestTrigger = c_capped = 0;
        }
    }
}
