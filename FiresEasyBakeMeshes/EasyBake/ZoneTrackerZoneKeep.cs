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
                if (HoldsPiecesInActiveArea(sector))
                {
                    __result = true; s_zoneKeepAnswers++;
                    // a working fix means NO [ZoneFlip] lines, so the kept count needs its own line: the first keep, then at most one per 30 s (Info)
                    float t = Time.unscaledTime;
                    if (s_zoneKeepAnswers == 1 || t >= s_zoneKeepSayNext)
                    {
                        s_zoneKeepSayNext = t + 30f;
                        EasyBakeLog.Info($"[ZoneKeep] kept {s_zoneKeepAnswers} zone unload check(s) so far: vanilla saw no live object in a near zone whose buildings EBM draws (last zone {sector})");
                    }
                }
            }
        }
    }
}
