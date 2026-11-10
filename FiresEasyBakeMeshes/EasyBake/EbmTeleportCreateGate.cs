using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.250 (moved from FGN's TeleportCreateGate, Fire's SP split, plan row #4). While the local player teleports, ZoneSystem.IsActiveAreaLoaded answers true ONLY
    // inside ZNetScene.CreateObjects, so objects at the destination are created as their zones arrive; inside that scope IsZoneReadyForType also needs the object's own
    // zone to exist, so nothing is created over a zone with no ground yet. Every caller outside a create pass gets the real answer. No local player on a dedicated
    // server means this never engages there. [Optimize] TeleportCreateGate (default on).
    [HarmonyPatch]
    internal static class EbmTeleportCreateGate
    {
        private static int s_depth;          // > 0 while ZNetScene.CreateObjects runs
        private static bool s_teleporting;   // read once per outermost pass
        private static int s_passes, s_early, s_held;
        private static float s_nextLog;
        private static bool s_said;

        private static bool On => FiresEasyBakeMeshesPlugin.TeleportCreateGate != null && FiresEasyBakeMeshesPlugin.TeleportCreateGate.Value;

        // ZoneSystem.m_zones is private and its value type is private too, so it is read as a plain dictionary (only while teleporting).
        private static FieldInfo s_zonesField; private static bool s_zonesResolved;
        private static bool ZoneExists(ZoneSystem zs, Vector2s zone)
        {
            if (!s_zonesResolved) { s_zonesResolved = true; s_zonesField = AccessTools.Field(typeof(ZoneSystem), "m_zones"); }
            var zones = s_zonesField != null ? s_zonesField.GetValue(zs) as IDictionary : null;
            return zones == null || zones.Contains(zone);
        }

        // First of all CreateObjects prefixes, so the create pass runs inside the scope.
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
        private static void Enter()
        {
            if (s_depth++ > 0) return;
            Player me = Player.m_localPlayer;
            s_teleporting = On && me != null && me.IsTeleporting();
            if (!s_teleporting) return;
            s_passes++;
            if (!s_said) { s_said = true; EasyBakeLog.Info("[TeleportGate] teleport create gate on: create passes may start early, each object waits for its own zone's ground"); }
        }

        // A finalizer runs even when a prefix skipped the original or something threw, so the scope always closes.
        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyFinalizer]
        private static void Leave()
        {
            if (s_depth > 0 && --s_depth > 0) return;
            s_teleporting = false;
            Report();
        }

        [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool AreaPrefix(ref bool __result)
        {
            if (s_depth == 0 || !s_teleporting) return true;
            __result = true;
            s_early++;
            return false;
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.IsZoneReadyForType)), HarmonyPostfix]
        private static void ReadyPostfix(ZoneSystem __instance, Vector2s zoneID, ref bool __result)
        {
            if (!__result || s_depth == 0 || !s_teleporting) return;
            if (ZoneExists(__instance, zoneID)) return;
            __result = false;   // no terrain root for this zone yet: the object waits, as it would for a zone still loading
            s_held++;
        }

        private static void Report()
        {
            if (s_passes == 0 || !EasyBakeLog.DebugOn()) return;
            float now = Time.unscaledTime;
            if (now < s_nextLog) return;
            if (s_nextLog > 0f)
                EasyBakeLog.Debug($"[TeleportGate] last 30 s: {s_passes} create pass(es) while teleporting, area answered loaded early {s_early} time(s), {s_held} object check(s) held for a zone with no ground yet");
            s_nextLog = now + 30f;
            s_passes = s_early = s_held = 0;
        }
    }
}
