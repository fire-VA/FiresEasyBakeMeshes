using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Steady-state short-circuit for ZNetScene.CreateDestroyObjects.
    //
    // Vanilla CreateDestroyObjects fires at 30 Hz from ZNetScene.Update. Each
    // call:
    //   1. ZDOMan.FindSectorObjects → fills near/distant ZDO lists from the
    //      active sector ring (~25k refs at megabase density).
    //   2. CreateObjects → already time-budgeted by VAGhetto's AutoTune prefix
    //      on the client path; small cost in steady state.
    //   3. RemoveObjects → earmarks every ZDO in the near+distant lists, then
    //      walks EVERY ZNetView in ZNetScene.m_instances looking for instances
    //      whose ZDO didn't get an earmark (i.e. fell out of the active ring).
    //      At megabase that walk is ~6.6 ms / tick of pure overhead — almost
    //      always finds zero things to remove because the player is parked.
    //
    // When nothing has changed since the last tick, the entire pass is wasted:
    // FindSectorObjects rebuilds the same lists, RemoveObjects re-earmarks the
    // same ZDOs, the full-walk finds no removals, CreateObjects has nothing
    // new to instantiate. We can detect "nothing changed" cheaply and skip
    // the original method.
    //
    // The skip is gated on four cheap signals (ALL must indicate "no change"):
    //   - Player's center sector unchanged since last vanilla pass.
    //   - ZoneSystem.m_zones.Count unchanged (no zone loaded or unloaded).
    //   - ZNetScene.m_instances.Count unchanged (no instantiation or destruction).
    //   - ZDOMan.NrOfObjects() unchanged (no ZDO created / received / destroyed).
    //
    // The instance-count signal is the load-screen safety net. Center and zone
    // count can both plateau while CreateObjects is still draining tens of
    // thousands of uncreated ZDOs in the active area — the player is parked,
    // zones report loaded, but the per-zone ZDO drain hasn't finished. Without
    // the instance-count check we throttle the drain and stall the spawn-in.
    //
    // The ZDO-count signal is what makes another player's freshly-built piece
    // (or a creature spawn, or a dropped item) appear promptly. When a peer
    // builds, the server streams the new ZDO to us via ZDOMan.RPC_ZDOData ->
    // CreateNewZDO -> m_objectsByID.Add — so NrOfObjects() ticks up the frame we
    // RECEIVE it, BEFORE it's instantiated. Center / zone / instance counts all
    // stay flat until CreateObjects actually builds the GameObject, so without
    // this signal the new piece waits out the TTL below (up to MaxSeconds) before
    // we run a pass that instantiates it — the "pieces built by another player
    // take a long time to render" symptom. With it, we run the very next tick.
    // A truly static parked scene has a stable ZDO set, so the skip still engages.
    //
    // TTL backstop: even when all four signals match, fall through to vanilla
    // every MaxSeconds. With the ZDO-count signal this is now a pure safety net
    // rather than the primary path for inbound objects.
    //
    // Interaction with VAGhetto's ServerAuthorityPatches.CreateDestroyObjects_Prefix:
    //   - On dedicated server it runs vanilla's logic itself (returns false).
    //     We bail out before doing anything when IsDedicated() so we don't
    //     interfere with that path.
    //   - On client it returns true (no-op). Our prefix can sit alongside it
    //     in any order; both prefixes run, and as long as at least one returns
    //     false the original is skipped. We're the only one that returns false
    //     on client, so behaviour is deterministic regardless of patch order.
    //
    // Safety:
    //   - If reflection on ZoneSystem.m_zones fails (field renamed by a future
    //     game patch), we never skip — silent fail-open. Better to lose the
    //     optimization than skip CreateDestroyObjects forever.
    //   - If ZNet.instance / ZoneSystem.instance are null (between scene loads),
    //     we don't skip.
    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    public static class ZNetScene_CreateDestroyObjects_Patch
    {
        private static Vector2s _lastCenter = new Vector2s(short.MinValue, short.MinValue);
        private static int _lastZoneCount = -1;
        private static int _lastInstanceCount = -1;
        private static int _lastZdoCount = -1;
        private static float _lastVanillaTime = -1000f;

        private static FieldInfo s_zonesField;
        private static bool s_zonesFieldChecked;
        private static FieldInfo s_instancesField;
        private static bool s_instancesFieldChecked;

        private static int _skippedSinceReport;
        private static float _lastReportTime;
        private static bool s_standDownLogged;

        [HarmonyPrefix]
        public static bool Prefix(ZNetScene __instance, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                if (!s_standDownLogged)
                {
                    s_standDownLogged = true;
                    EasyBakeLog.Info("[CDS] Another mod already replaces ZNetScene.CreateDestroyObjects; the create/destroy skip stands down.");
                }
                return false;
            }
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value) return true;
            if (!FiresEasyBakeMeshesPlugin.CreateDestroySkipEnabled.Value) return true;

            // VAGhetto's ServerAuthorityPatches owns the dedicated-server path.
            // Stay out of it.
            if (ZNet.instance == null) return true;
            if (ZNet.instance.IsDedicated()) return true;

            // Loading-screen / teleport gate. While Player.m_localPlayer is null
            // the world is still streaming in and CreateObjects must drain the
            // active area's uncreated ZDOs at full rate. Skipping here turns a
            // 30-60s spawn-in into multiple minutes. IsTeleporting() covers
            // long-distance teleports which also rebuild the active area.
            if (Player.m_localPlayer == null) return true;
            if (Player.m_localPlayer.IsTeleporting()) return true;

            var zs = ZoneSystem.instance;
            if (zs == null) return true;

            Vector3 refPos = ZNet.instance.GetReferencePosition();
            Vector2s center = ZoneSystem.GetZone(refPos);
            int zoneCount = GetZoneCount(zs);
            if (zoneCount < 0) return true; // reflection failed — fall through
            int instanceCount = GetInstanceCount(__instance);
            if (instanceCount < 0) return true; // reflection failed — fall through

            // Total known-ZDO count. Ticks up the instant we RECEIVE an inbound
            // ZDO (another player's build piece, a creature spawn, a dropped item)
            // over the network — before it's ever instantiated — so we run a pass
            // and build it next tick instead of waiting out the TTL. Public O(1)
            // accessor (m_objectsByID.Count); no reflection needed.
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return true; // between scenes — fall through
            int zdoCount = zdoMan.NrOfObjects();

            float now = Time.unscaledTime;
            float maxSkipSeconds = FiresEasyBakeMeshesPlugin.CreateDestroySkipMaxSeconds.Value;
            bool ttlExpired = (now - _lastVanillaTime) >= maxSkipSeconds;
            bool stateChanged = center.x != _lastCenter.x
                             || center.y != _lastCenter.y
                             || zoneCount != _lastZoneCount
                             || instanceCount != _lastInstanceCount
                             || zdoCount != _lastZdoCount;

            if (!stateChanged && !ttlExpired && _lastZoneCount >= 0)
            {
                _skippedSinceReport++;
                if (FiresEasyBakeMeshesPlugin.CreateDestroySkipVerbose.Value
                    && (now - _lastReportTime) > 5f)
                {
                    EasyBakeLog.Info(
                        $"[CDS] Skipped {_skippedSinceReport} CreateDestroyObjects ticks " +
                        $"in last {now - _lastReportTime:F1}s " +
                        $"(center={center.x},{center.y}, zones={zoneCount}, instances={instanceCount}, zdos={zdoCount}).");
                    _skippedSinceReport = 0;
                    _lastReportTime = now;
                }
                return false;
            }

            _lastCenter = center;
            _lastZoneCount = zoneCount;
            _lastInstanceCount = instanceCount;
            _lastZdoCount = zdoCount;
            _lastVanillaTime = now;
            return true;
        }

        private static int GetZoneCount(ZoneSystem zs)
        {
            if (!s_zonesFieldChecked)
            {
                s_zonesField = typeof(ZoneSystem).GetField("m_zones",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                s_zonesFieldChecked = true;
                if (s_zonesField == null)
                {
                    EasyBakeLog.Warn(
                        "[CDS] ZoneSystem.m_zones field not found via reflection — " +
                        "CreateDestroyObjects skip optimization disabled (fail-open).");
                }
            }
            if (s_zonesField == null) return -1;
            var dict = s_zonesField.GetValue(zs) as System.Collections.ICollection;
            return dict?.Count ?? -1;
        }

        private static int GetInstanceCount(ZNetScene scene)
        {
            if (!s_instancesFieldChecked)
            {
                s_instancesField = typeof(ZNetScene).GetField("m_instances",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                s_instancesFieldChecked = true;
                if (s_instancesField == null)
                {
                    EasyBakeLog.Warn(
                        "[CDS] ZNetScene.m_instances field not found via reflection — " +
                        "CreateDestroyObjects skip optimization disabled (fail-open).");
                }
            }
            if (s_instancesField == null) return -1;
            var dict = s_instancesField.GetValue(scene) as System.Collections.ICollection;
            return dict?.Count ?? -1;
        }

        public static void ResetState()
        {
            _lastCenter = new Vector2s(short.MinValue, short.MinValue);
            _lastZoneCount = -1;
            _lastInstanceCount = -1;
            _lastZdoCount = -1;
            _lastVanillaTime = -1000f;
            _skippedSinceReport = 0;
            _lastReportTime = 0f;
        }
    }
}
