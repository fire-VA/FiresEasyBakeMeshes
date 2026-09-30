using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Pieces a bake already draws are not created on a multiplayer client; see ZoneTracker.TrySkipCreation.
    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    public static class ZNetScene_CreateObject_Skip_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZDO zdo, ref GameObject __result, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            bool skip;
            try { skip = EasyBake.ZoneTracker.TrySkipCreation(zdo); }
            catch (System.Exception ex)
            {
                EasyBake.ZoneTracker.LogSkipFailureOnce(ex);
                skip = false;
            }
            if (!skip) return true;
            __result = null;
            return false;
        }
    }

    // Vanilla holds a spawn or teleport until every object near the point has an instance, which a skipped piece never
    // gets; the area counts as ready when everything missing is a skipped piece.
    [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.IsAreaReady))]
    public static class ZNetScene_IsAreaReady_Skip_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Vector3 point, ref bool __result)
        {
            if (__result) { RespawnNet.Ready(); return; }
            try { if (EasyBake.ZoneTracker.IsAreaReadyCountingSkipped(point)) { __result = true; RespawnNet.Ready(); return; } }
            catch { }
            RespawnNet.Check(point, ref __result);
        }
    }

    // ═══ A RESPAWN MUST NOT WAIT FOREVER ON AN AREA THAT NEVER READS READY (2026-09-29, R75) ════════════════════
    // Game.FindSpawnPoint (a death respawn, and the login spawn) waits on ZNetScene.IsAreaReady at the spawn point with no
    // timeout. On the test rig it stayed false for good with the 3x3 fully instanced ("1165 ZDO(s) ... 0 missing an instance")
    // and the player sat on the loading screen until the game was killed. Whatever the cause, a player must get in: while
    // the game is waiting to respawn, after WaitSeconds of "not ready" the area counts as ready (logged once per wait).
    // The lead (R76): a teleport waits the same way (Player.UpdateTeleport asks IsAreaReady at the target with no timeout;
    // a portal hang is a loading screen for good too), so the local player's teleport gets the same net.
    internal static class RespawnNet
    {
        private const float WaitSeconds = 30f;
        private static float s_since = -1f;
        private static bool s_forced;

        internal static void Check(Vector3 point, ref bool ready)
        {
            string what = Waiting();
            if (what == null) { s_since = -1f; s_forced = false; return; }
            float now = Time.realtimeSinceStartup;
            if (s_since < 0f) s_since = now;
            if (now - s_since < WaitSeconds) return;
            if (!s_forced)
            {
                s_forced = true;
                EasyBakeLog.Warn($"{what}: the area at ({point.x:0}, {point.z:0}) not 'ready' after {WaitSeconds:0} s " +
                                 $"(zone loaded {ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(point)}); going ahead anyway");
            }
            ready = true;
        }

        // Ready on its own: the next wait starts fresh.
        internal static void Ready()
        {
            if (Waiting() == null) { s_since = -1f; s_forced = false; }
        }

        // What is waiting on the area: the respawn (a death or the login spawn), the local player's teleport, or nothing.
        private static string Waiting()
        {
            if (Game.instance != null && Game.instance.WaitingForRespawn()) return "respawn";
            return Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting() ? "teleport" : null;
        }
    }

    // ── VANILLA DELETES THE WORLD WHEN CreateObject RETURNS NULL ON A SERVER ──────────────────────────────
    // ZNetScene.CreateObjectsSorted:
    //     if (CreateObject(zdo) != null) { ++created; ... }
    //     else if (ZNet.instance.IsServer())
    //     { zdo.SetOwner(...); ZLog.Log("Destroyed invalid prefab ZDO:" + zdo.m_uid); ZDOMan.instance.DestroyZDO(zdo); }
    //
    // Returning null is how a skip is signalled, so on a host that signal IS vanilla's "invalid prefab, delete
    // it" path - the same process owning both sides is the entire reason skipping was client-only. Blocking
    // exactly that one deletion is what makes a host no different from a joined client.
    //
    // ONE-SHOT, NOT A MEMBERSHIP TEST. ConsumeJustSkipped matches only the ZDO handed over by the skip that
    // just ran, and clears itself. A broader "is this skipped?" test would refuse REAL deletions whenever the
    // tracker disagreed, and a piece that cannot be destroyed is a far worse defect than one drawn twice.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
    public static class ZDOMan_DestroyZDO_SkipGuard_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZDO zdo)
        {
            if (zdo == null) return true;
            if (!EasyBake.ZoneTracker.ConsumeJustSkipped(zdo)) return true;
            s_blocked++;
            return false;
        }

        private static int s_blocked;

        /// <summary>For the status box: how many world-deletions the skip signal would otherwise have caused.</summary>
        internal static int BlockedDeletions => s_blocked;
    }
}
