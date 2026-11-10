using System;
using System.Reflection;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.121 ([visual], lead 10-24: EBM on the halo ring). FAT 0.2.1050 files OBJECT sectors (ZDO sectors: what instantiates and
    // syncs) by ring sector on HaloFlat, while TERRAIN zones (ZoneSystem.GetZone / GetZonePos / m_zones / IsZoneLoaded) stay x,z.
    // EBM's zones group OBJECTS, so on the ring they must use the same key as the objects:
    //   Of(pos)          = FatApi.HaloZone(pos)       (ZoneSystem.GetZone off the ring)
    //   Pos(key)         = FatApi.HaloZonePos(key)    (ZoneSystem.GetZonePos off the ring)
    //   Delta(a, b)      = FatApi.HaloZoneDelta(a, b) (b - a; wrapped at the seam on the ring)
    //   Loaded(key)      = within the near simulation area of the player's object sector on the ring (what FAT's ring
    //                      CreateDestroyObjects instantiates); ZoneSystem.IsZoneLoaded off the ring
    // FAT is read by reflection (EBM does not reference it): no FAT / an older FAT / any other world = exactly the old behaviour.
    // Terrain-keyed code (SectorInstanceMirror for ZoneSystem.UpdateTTL / HaveInstanceInSector) keeps ZoneSystem.GetZone on purpose.
    internal static class EbmZones
    {
        private static bool s_bound;
        private static Func<bool> s_on;
        private static Func<Vector3, Vector2s> s_zone;
        private static Func<Vector2s, Vector3> s_pos;
        private static Func<Vector2s, Vector2s, Vector2s> s_delta;
        private static bool s_onLogged;

        // 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the ring keys are PUSHED (EbmApi.RegisterHaloZones, FAT: HaloZonesOn / HaloZone /
        // HaloZonePos / HaloZoneDelta); nothing registered = x,z zones, exactly as without FAT.
        private static void Bind() { }

        internal static void Register(Func<bool> on, Func<Vector3, Vector2s> zone, Func<Vector2s, Vector3> pos, Func<Vector2s, Vector2s, Vector2s> delta)
        {
            bool all = on != null && zone != null && pos != null && delta != null;
            s_on = all ? on : null; s_zone = all ? zone : null; s_pos = all ? pos : null; s_delta = all ? delta : null;
            s_bound = true;
            EasyBakeLog.Debug(all ? "[Ring] halo zone keys registered: EBM follows ring object sectors while they are on" : "[Ring] halo zone keys withdrawn (or incomplete): EBM keeps x,z zones");
        }

        /// <summary>True on a world whose object sectors are ring-keyed (FAT HaloFlat).</summary>
        internal static bool Ring
        {
            get
            {
                Bind();
                if (s_on == null) return false;
                bool on;
                if (ForeignCall.Off("FAT", "HaloZonesOn")) return false; try { on = s_on(); } catch (Exception ex) { ForeignCall.Failed("FAT", "HaloZonesOn", ex); return false; }   // 1.2.189
                if (on && !s_onLogged) { s_onLogged = true; EasyBakeLog.Debug("[Ring] object sectors are ring-keyed (FAT HaloZone): EBM zones, loading, LOD, keep-alive and the destroy index follow the ring sectors"); }
                return on;
            }
        }

        internal static Vector2s Of(Vector3 p)
        {
            if (Ring) { try { return s_zone(p); } catch (Exception ex) { ForeignCall.Failed("FAT", "HaloZonesOn", ex); } }   // 1.2.189: a throw switches the ring keys off (HaloZonesOn) for the session
            return ZoneSystem.GetZone(p);
        }

        internal static Vector3 Pos(Vector2s key)
        {
            if (Ring) { try { return s_pos(key); } catch (Exception ex) { ForeignCall.Failed("FAT", "HaloZonesOn", ex); } }
            return ZoneSystem.GetZonePos(key);
        }

        internal static Vector2s Delta(Vector2s a, Vector2s b)
        {
            if (Ring) { try { return s_delta(a, b); } catch (Exception ex) { ForeignCall.Failed("FAT", "HaloZonesOn", ex); } }
            return new Vector2s(b.x - a.x, b.y - a.y);
        }

        /// <summary>A key normalised onto the ring grid (wraps the around-the-ring index at the seam). Identity off the ring.</summary>
        internal static Vector2s Norm(Vector2s key) => Ring ? Of(Pos(key)) : key;

        /// <summary>Is this EBM zone's content loaded: terrain IsZoneLoaded off the ring; within the near area of the player's
        /// object sector on the ring (the same area FAT's ring CreateDestroyObjects instantiates).</summary>
        internal static bool Loaded(ZoneSystem zs, Vector2s key)
        {
            if (!Ring) return zs != null && zs.IsZoneLoaded(key);
            var net = ZNet.instance;
            if (net == null) return false;
            var d = Delta(Of(net.GetReferencePosition()), key);
            int area = net.GetSyncedSimulationDistance().NearSimulationDistance;
            return Mathf.Abs(d.x) <= area && Mathf.Abs(d.y) <= area;
        }

        /// <summary>Squared distance from a point to the nearest point of a zone: x,z box off the ring, 3D to the sector anchor minus
        /// a half sector on the ring (sectors are ~64 m cells on the curved floor).</summary>
        internal static float NearestSqr(Vector2s key, Vector3 viewer)
        {
            var c = Pos(key);
            if (Ring)
            {
                float d = Mathf.Max(0f, Vector3.Distance(viewer, c) - ZoneSystem.c_ZoneSizeHalf * 1.414f);
                return d * d;
            }
            float dx = Mathf.Max(0f, Mathf.Abs(viewer.x - c.x) - ZoneSystem.c_ZoneSizeHalf);
            float dz = Mathf.Max(0f, Mathf.Abs(viewer.z - c.z) - ZoneSystem.c_ZoneSizeHalf);
            return dx * dx + dz * dz;
        }
    }
}
