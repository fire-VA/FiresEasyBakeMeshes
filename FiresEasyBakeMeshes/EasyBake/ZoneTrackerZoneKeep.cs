using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.312 ([ghost]'s finding Tools\FINDING_loadin_pop_zone_ttl_churn_20261111.md, verified in vanilla 1.0 by [visual]; Fire: far buildings and
    // vegetation "pop in pop out pop in" on the first load, never once settled).
    // Vanilla ZoneSystem.UpdateTTL (ZoneSystem.cs 3355-3370) destroys the first zone with m_ttl > m_zoneTTL (4 s) AND
    // !ZNetScene.HaveInstanceInSector(zone) (ZNetScene.cs 441-451: a live, non-distant ZNetView in that sector); UpdateTTL is its only caller.
    // A near zone's ttl is reset only by PokeLocalZone from CreateLocalZones, which returns after creating ONE zone, so during load-in zones
    // late in its scan age past 4 s. With EBM a baked town zone has its pieces skipped or unloaded (drawn by the bake / the GPU), so vanilla
    // sees it EMPTY and destroys it; the next tick re-creates it; EBM's unload / reload branches flip bake off, live on, bake on, live off.
    //  - Fix (postfix only, the vanilla method and its anchor untouched): where vanilla answers false, answer true for a sector EBM holds a bake
    //    for, while that zone is inside vanilla's active area around the reference position (where those pieces WOULD be live instances).
    //    Outside the active area vanilla would have destroyed the instances too, so the answer stays false and zones unload as before.
    //  - [ZoneFlip] (Debug): one line per EBM unload / reload, with what vanilla's test said, to prove the churn is gone.
    internal static partial class ZoneTracker
    {
        internal static bool ZoneKeepOn => FiresEasyBakeMeshesPlugin.ZoneKeepWithEbmPieces == null || FiresEasyBakeMeshesPlugin.ZoneKeepWithEbmPieces.Value;
        internal static int s_zoneKeepAnswers;   // census: how often the postfix kept a zone (read by the [ZoneFlip] line)

        /// <summary>EBM holds this sector's pieces (a baked zone) and the sector is inside vanilla's active area: vanilla would have live instances there.</summary>
        internal static bool HoldsPiecesInActiveArea(Vector2s sector)
        {
            if (!_zones.TryGetValue(sector, out var st) || st == null || !st.Baked || st.Bake == null) return false;
            var znet = ZNet.instance;
            if (znet == null) return false;
            var center = ZoneSystem.GetZone(znet.GetReferencePosition());
            return ZNetScene.InActiveArea(ZoneSystem.GetZonePos(sector), center);
        }

        private static void ZoneFlipLine(string what, Vector2s coord, ZoneState state, float now)
        {
            float since = state.LastFlipTime < 0f ? -1f : now - state.LastFlipTime;
            state.LastFlipTime = now;
            if (!EasyBakeLog.DebugOn()) return;
            var znet = ZNet.instance; var scene = ZNetScene.instance;
            Vector2s rz = znet != null ? ZoneSystem.GetZone(znet.GetReferencePosition()) : coord;
            bool inArea = znet != null && ZNetScene.InActiveArea(ZoneSystem.GetZonePos(coord), rz);
            bool live = scene != null && ZNetSceneHaveInstanceVanilla(scene, coord);
            bool loadingObj = EbmZones.VanillaLoadingObjects(ZoneSystem.instance, coord);   // 1.2.314: vanilla IsZoneLoaded reads false while this is true
            EasyBakeLog.Debug($"[ZoneFlip] zone {coord} {what} frame {Time.frameCount} t {now:0.0}: ref zone {rz} d ({coord.x - rz.x},{coord.y - rz.y}), inside vanilla active area {inArea}, "
                + $"live instance in sector {live}, vanilla spawning locations there {loadingObj} (counted loaded: {(EbmZones.ZoneLoadedIgnoresObjectLoading ? "yes" : "no")}), EBM baked {state.Baked}, keep fix {(ZoneKeepOn ? "on" : "off")} (kept {s_zoneKeepAnswers} so far), since last flip {(since < 0f ? "first" : since.ToString("0.0") + " s")}");
        }

        // 1.2.513 ([lead] psnow51: zones 24,12 / 23,11 / 22,10 UNLOAD + RELOAD five times each while the town route crossed the 26,8 / 27,8 border back and
        // forth; e.g. 24,12 RELOAD t 401.1 at d (-2,4) from 26,8, UNLOAD t 464.8 at d (-3,4) from 27,8). Vanilla ZoneSystem.CreateLocalZones pokes (ttl = 0) only
        // zones within ZonesWithinRadius(ref, zone, NearSimulationDistance) = (N + 0.5) zones (classic mode: the square), and UpdateTTL destroys a zone
        // 4 s (m_zoneTTL) after its last poke unless ZNetScene.HaveInstanceInSector (a live NON-distant instance) - nothing live exists that far out (the
        // object active area is Chebyshev 1.5 zones, ZNetScene.PointInsideActiveArea), so a zone on the radius edge is destroyed and re-made at every border
        // crossing. Each EBM flip: bake hidden / shown, give-back, stand-ins parked / rebuilt, ZoneUnloaded + ZoneBaked (Vedr re-registers every piece).
        // Band: for a zone EBM holds a bake for, HaveInstanceInSector answers true while it is within (N + 1.5) zones (one zone wider than the poke radius;
        // classic: the square N + 1), so it unloads only once the reference is a whole zone further away. Never beyond that (Fire: nothing loaded farther
        // than the distant render reach; one zone = 64 m, far inside it). 1.2.514: on by default (its leg passed, Fire's word).
        internal static int s_bandKeeps;
        internal static bool KeepBandOn => FiresEasyBakeMeshesPlugin.ZoneKeepBand != null && FiresEasyBakeMeshesPlugin.ZoneKeepBand.Value;
        internal static bool InKeepBand(Vector2s sector)
        {
            if (!KeepBandOn || !_zones.TryGetValue(sector, out var st) || st == null || !st.Baked || st.Bake == null) return false;
            var znet = ZNet.instance; var zs = ZoneSystem.instance;
            if (znet == null || zs == null) return false;
            var sim = znet.GetSyncedSimulationDistance();
            int n = sim.NearSimulationDistance;
            var center = ZoneSystem.GetZone(znet.GetReferencePosition());
            if (sim.IsClassic) return Mathf.Abs(sector.x - center.x) <= n + 1 && Mathf.Abs(sector.y - center.y) <= n + 1;
            float r = (n + 1.5f) * zs.m_zoneSize;   // vanilla ZonesWithinRadius uses (n + 0.5) * zoneSize from zone centre to zone centre
            return (ZoneSystem.GetZonePos(sector) - ZoneSystem.GetZonePos(center)).sqrMagnitude < r * r;
        }

        private static bool s_inVanillaAsk; private static float s_zoneKeepSayNext;
        // vanilla's own answer without our postfix (the proof line must show what vanilla saw)
        private static bool ZNetSceneHaveInstanceVanilla(ZNetScene scene, Vector2s coord)
        {
            s_inVanillaAsk = true;
            try { return scene.HaveInstanceInSector(coord); }
            finally { s_inVanillaAsk = false; }
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.HaveInstanceInSector))]
        private static class ZNetScene_HaveInstanceInSector_KeepEbmZones
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(Vector2s sector, ref bool __result)
            {
                if (__result || s_inVanillaAsk || !ZoneKeepOn) return;
                bool inArea = HoldsPiecesInActiveArea(sector);   // 1.2.514: asked once ([2ic] nit)
                bool band = !inArea && InKeepBand(sector);   // 1.2.513
                if (band || inArea)
                {
                    __result = true; s_zoneKeepAnswers++; if (band) s_bandKeeps++;
                    // a working fix means NO [ZoneFlip] lines, so the kept count needs its own line: the first keep, then at most one per 30 s (Info)
                    float t = Time.unscaledTime;
                    if (s_zoneKeepAnswers == 1 || t >= s_zoneKeepSayNext)
                    {
                        s_zoneKeepSayNext = t + 30f;
                        EasyBakeLog.Info($"[ZoneKeep] kept {s_zoneKeepAnswers} zone unload check(s) so far: vanilla saw no live object in a near zone whose buildings EBM draws (last zone {sector}); of them in the one-zone keep band (ZoneKeepBand, 1.2.513) {s_bandKeeps}");
                    }
                }
            }
        }
    }
}
