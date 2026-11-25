using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.326 ([lead] 11-12: the ~30 s old-town teleport; [generator]: vanilla takes ~53 s there, ours ~32 s): ONE Info line per local-player teleport,
    // written when the fade ends, timing vanilla's own hold conditions (Player.UpdateTeleport, assembly_valheim Player.cs:6394-6449) in order:
    //   placed       m_teleportTimer > 2: the player is moved to the target (:6421-6426)
    //   area ready   distant teleports: timer > 8 AND ZNetScene.IsAreaReady(target) (:6432); the line says which IsAreaReady term was false last
    //                (zone not loaded = a location / dungeon still spawning there, vs objects still missing in the 3x3)
    //   floor        ZoneSystem.FindFloor(target): one Raycast down from 1 m above, m_solidRayMask = Default / static_solid / Default_small / piece /
    //                terrain (ZoneSystem.cs:690). If it never hits, vanilla snaps the player at timer > 15 (:6443): "15 s fallback taken"
    //   fade end     m_teleporting false
    // 1.2.329 ([lead]: sp10 printed "area ready never, floor found 1.8 s ... held last by the 15 s floor fallback"): 1.2.326 sampled every 0.25 s, so it
    // could miss the ONE frame IsAreaReady turned true (vanilla ends the fade on that frame), and it set "fallback" from a timer sample regardless of
    // the floor. Now every condition is checked EVERY frame while a teleport runs (vanilla calls IsAreaReady every frame then anyway), the state of
    // the final frame is reported ("fade ended with"), and "15 s fallback taken" is decided only at the end: floor never found AND timer > 15.
    // When the floor check failed while the area was ready, a probe says what is below the target.
    internal static class ArrivalTimeline
    {
        private static readonly AccessTools.FieldRef<Player, Vector3> s_target = AccessTools.FieldRefAccess<Player, Vector3>("m_teleportTargetPos");
        private static readonly AccessTools.FieldRef<Player, float> s_timer = AccessTools.FieldRefAccess<Player, float>("m_teleportTimer");
        private static readonly AccessTools.FieldRef<Player, bool> s_distant = AccessTools.FieldRefAccess<Player, bool>("m_distantTeleport");

        private static bool s_on, s_distantNow, s_lastArea, s_lastFloor;
        private static float s_t0, s_placed, s_zoneLoaded, s_areaReady, s_floor, s_lastTimer;
        private static Vector3 s_where;
        private static string s_lastBlock, s_probe;

        internal static void Tick()
        {
            var me = Player.m_localPlayer;
            if (me == null) { s_on = false; return; }
            bool tp = me.IsTeleporting();
            if (!s_on)
            {
                if (!tp) return;
                s_on = true; s_t0 = Time.unscaledTime;
                s_placed = s_zoneLoaded = s_areaReady = s_floor = -1f; s_lastTimer = 0f; s_lastArea = s_lastFloor = false; s_lastBlock = null; s_probe = null;
                try { s_where = s_target(me); s_distantNow = s_distant(me); } catch { s_where = me.transform.position; s_distantNow = true; }
                return;
            }
            float t = Time.unscaledTime - s_t0;
            if (!tp) { Report(t); s_on = false; return; }
            try
            {
                float timer = s_timer(me);
                s_lastTimer = timer;
                if (s_placed < 0f && timer > 2f) s_placed = t;
                var zs = ZoneSystem.instance; var scene = ZNetScene.instance;
                if (zs == null || scene == null || s_placed < 0f) return;
                if (s_zoneLoaded < 0f && zs.IsZoneLoaded(s_where)) s_zoneLoaded = t;
                s_lastArea = scene.IsAreaReady(s_where);
                if (s_lastArea) { if (s_areaReady < 0f) s_areaReady = t; }
                else s_lastBlock = zs.IsZoneLoaded(s_where) ? "objects still missing in the 3x3" : "zone not loaded (a location / dungeon still spawning there)";
                s_lastFloor = zs.FindFloor(s_where, out _);
                if (s_lastFloor) { if (s_floor < 0f) s_floor = t; }
                else if (s_lastArea && s_probe == null) s_probe = Probe(s_where);
            }
            catch (System.Exception e) { s_lastBlock = "check threw: " + e.Message; }
        }

        private static string Probe(Vector3 p)
        {
            var hm = Heightmap.FindHeightmap(p);
            string terrain = hm == null ? "no Heightmap at the point" : (hm.GetComponent<MeshCollider>() is MeshCollider mc
                ? $"Heightmap {hm.name}: collider {(mc.enabled ? "on" : "OFF")}, mesh {(mc.sharedMesh != null ? "yes" : "NONE")}" : $"Heightmap {hm.name}: no MeshCollider");
            string hit = "nothing";
            if (Physics.Raycast(p + Vector3.up * 1f, Vector3.down, out RaycastHit h, 1000f, ~0, QueryTriggerInteraction.Ignore))
                hit = $"'{h.collider.name}' on layer {LayerMask.LayerToName(h.collider.gameObject.layer)} {h.distance:0.0} m down{(h.collider.transform.root.name.Contains("standins") ? " (an EBM stand-in)" : "")}";
            float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetSolidHeight(p) : float.NaN;
            return $"below the target: {terrain}; any-layer ray hits {hit}; target y {p.y:0.0} vs solid ground {ground:0.0}{(ground > p.y + 1f ? " (the target is UNDER the ground: the ray starts below the surface)" : "")}";
        }

        private static void Report(float t)
        {
            string F(float v) => v < 0f ? "never" : v.ToString("0.0") + " s";
            bool fallback = s_floor < 0f && s_lastTimer > 15f;   // vanilla's :6443 branch: no floor ever found and the timer passed 15 s
            string last = fallback ? "the 15 s floor fallback (FindFloor never hit)"
                : s_floor >= 0f && s_areaReady >= 0f ? (s_floor >= s_areaReady ? "the floor check (FindFloor)" : "area ready (" + (s_lastBlock ?? "objects / zone") + ")")
                : s_areaReady >= 0f ? "area ready" : "unknown (neither area ready nor a floor was seen)";
            EasyBakeLog.Info($"[Arrival] teleport to ({s_where.x:0}, {s_where.z:0}){(s_distantNow ? " (distant)" : "")}: placed {F(s_placed)}, zone loaded {F(s_zoneLoaded)}, area ready {F(s_areaReady)}"
                + $"{(s_lastBlock != null ? $" (last blocker before that: {s_lastBlock})" : "")}, floor found {F(s_floor)}, fade end {t:0.0} s (vanilla timer {s_lastTimer:0.0} s); "
                + $"fade ended with area ready {(s_lastArea ? "yes" : "no")}, floor {(s_lastFloor ? "yes" : "no")} on the last checked frame; held last by {last}"
                + (s_probe != null ? "; " + s_probe : ""));
        }
    }
}
