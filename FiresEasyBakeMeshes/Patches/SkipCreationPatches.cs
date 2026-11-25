using System.Reflection.Emit;
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
            try { using (EasyBake.EbmPatchMarkers.SkipCreation.Auto()) skip = EasyBake.ZoneTracker.TrySkipCreation(zdo); }   // 1.2.360: profiler marker only
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
            if (__result) { RespawnNet.Ready(point); return; }
            try { if (EasyBake.ZoneTracker.IsAreaReadyCountingSkipped(point)) { __result = true; RespawnNet.Ready(point); return; } }
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
    // 1.2.87 (the lead 10-06, overnight Q1): the clock times ONE continuous "not ready" at ONE point. It used to keep running
    // through ready answers while the player was teleporting, and across different points, so a test chaining short teleports
    // added them up and read as a 30 s hang it never had.
    internal static class RespawnNet
    {
        private const float WaitSeconds = 30f;
        private const float SamePointMetres = 32f;
        private static float s_since = -1f;
        private static bool s_forced;
        private static Vector3 s_point;

        internal static void Check(Vector3 point, ref bool ready)
        {
            string what = Waiting();
            if (what == null) { Reset(); return; }
            float now = Time.realtimeSinceStartup;
            if (s_since >= 0f && (point - s_point).sqrMagnitude > SamePointMetres * SamePointMetres) Reset();
            if (s_since < 0f) { s_since = now; s_point = point; }
            if (now - s_since < WaitSeconds) return;
            if (!s_forced)
            {
                s_forced = true;
                EasyBakeLog.Warn($"{what}: the area at ({point.x:0}, {point.z:0}) not 'ready' for {now - s_since:0} s straight " +
                                 $"(zone loaded {ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(point)}); going ahead anyway");
            }
            ready = true;
        }

        // Ready at the point being timed (or nothing waiting): that wait is over, so the next "not ready" starts a fresh clock.
        // A ready answer somewhere else (another caller asking about another place) leaves a real wait's clock running.
        internal static void Ready(Vector3 point)
        {
            if (Waiting() == null || (s_since >= 0f && (point - s_point).sqrMagnitude <= SamePointMetres * SamePointMetres)) Reset();
        }

        private static void Reset() { s_since = -1f; s_forced = false; }

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
    // 1.2.318 ([lead] 11-11 BLOCKER, Tools\WORLD_LOSS_CHECK_20261111.md: 30,045 host-skipped pieces went down vanilla's "Destroyed invalid prefab"
    // branch and lived ONLY because this one-shot matched): the guard is now the SECOND net. The first is ZNetScene_CreateLoop_HostSkipGate below,
    // which keeps vanilla's loop from ever reaching that branch for a skip. If this guard still fires, something else destroyed a skipped piece
    // (another mod's create loop, or the gate not installed): a Warning, the first time and every 1000th.
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
    public static class ZDOMan_DestroyZDO_SkipGuard_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZDO zdo)
        {
            if (zdo == null) return true;
            if (!EasyBake.ZoneTracker.ConsumeJustSkipped(zdo)) return true;
            s_blocked++;
            if (s_blocked == 1 || s_blocked % 1000 == 0)
                EasyBakeLog.Warn($"[SkipGuard] blocked a world deletion of a piece EBM skipped (uid {zdo.m_uid}, {s_blocked} so far): a create loop other than vanilla's own "
                    + "(gate " + (ZNetScene_CreateLoop_HostSkipGate.Installed == 2 ? "installed" : "NOT installed: " + ZNetScene_CreateLoop_HostSkipGate.Installed + "/2 loops") + ") treated EBM's skip as an invalid prefab. The piece is kept.");
            return false;
        }

        private static int s_blocked;

        /// <summary>For the status box: how many world-deletions the skip signal would otherwise have caused.</summary>
        internal static int BlockedDeletions => s_blocked;
    }

    // ═══ 1.2.318: THE SOURCE FIX: VANILLA'S LOOP NEVER SEES A SKIP AS AN INVALID PREFAB ═══════════════════════════════════════════════════
    // ZNetScene.CreateObjectsSorted / CreateDistantObjects:  if (CreateObject(zdo) != null) {...} else if (ZNet.instance.IsServer()) { ... DestroyZDO(zdo); }
    // In both loops the CreateObject call is swapped for LoopCreate (which calls the real CreateObject, so every mod's patch on it still runs) and
    // the IsServer() right after it for DestroyAllowed. LoopCreate notes, for THIS call, whether the null was a skip: vanilla sets zdo.Created only
    // in AddInstance (ZNetScene.cs:74, from ZNetView.Awake), so null + Created == true can only be a skip (EBM's, which sets Created itself); a real
    // invalid prefab (hash 0 / missing prefab) returns null with Created still false and is destroyed exactly as vanilla does. The note is written
    // on every call and read by the very next IsServer, so no state outlives one loop iteration: nothing for another mod's DestroyZDO prefix, load
    // order, or two skips in a row to break. The one-shot armed by the skip is cleared here too (no destroy follows, so it must not stay armed).
    // Each loop must swap exactly one of each, or that loop is left untouched (Warning) and the guard above stays the net.
    [HarmonyPatch]
    public static class ZNetScene_CreateLoop_HostSkipGate
    {
        internal static int Installed;   // loops patched (2 = both)
        private static bool s_lastWasSkip;
        private static System.Func<ZNetScene, ZDO, GameObject> s_create;

        [HarmonyTargetMethods]
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ZNetScene), "CreateObjectsSorted");
            yield return AccessTools.Method(typeof(ZNetScene), "CreateDistantObjects");
        }

        [HarmonyTranspiler]
        public static System.Collections.Generic.IEnumerable<CodeInstruction> GateLoop(System.Collections.Generic.IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase original)
        {
            var create = AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) });
            var isServer = AccessTools.Method(typeof(ZNet), nameof(ZNet.IsServer));
            var loopCreate = AccessTools.Method(typeof(ZNetScene_CreateLoop_HostSkipGate), nameof(LoopCreate));
            var allowed = AccessTools.Method(typeof(ZNetScene_CreateLoop_HostSkipGate), nameof(DestroyAllowed));
            var list = new System.Collections.Generic.List<CodeInstruction>(instructions);
            int creates = 0, servers = 0;
            foreach (var ci in list) { if (ci.Calls(create)) creates++; else if (creates == 1 && ci.Calls(isServer)) servers++; }
            if (creates != 1 || servers != 1)
            {
                EasyBakeLog.Warn($"[SkipGuard] {original?.Name}: expected one CreateObject and one IsServer after it, found {creates} / {servers}; this loop is left as the game has it "
                    + "(the DestroyZDO guard still keeps skipped pieces). Re-read the game's create loop.");
                return list;
            }
            bool afterCreate = false, swappedServer = false;
            for (int i = 0; i < list.Count; i++)
            {
                var ci = list[i];
                if (ci.Calls(create)) { list[i] = new CodeInstruction(OpCodes.Call, loopCreate).MoveLabelsFrom(ci).MoveBlocksFrom(ci); afterCreate = true; continue; }
                if (afterCreate && !swappedServer && ci.Calls(isServer)) { list[i] = new CodeInstruction(OpCodes.Call, allowed).MoveLabelsFrom(ci).MoveBlocksFrom(ci); swappedServer = true; }
            }
            Installed++;
            EasyBakeLog.Debug($"[SkipGuard] {original?.Name}: a skipped piece can no longer reach the game's invalid-prefab delete");
            return list;
        }

        // Same stack as the call it replaces (instance, zdo -> GameObject).
        public static GameObject LoopCreate(ZNetScene scene, ZDO zdo)
        {
            if (s_create == null) s_create = AccessTools.MethodDelegate<System.Func<ZNetScene, ZDO, GameObject>>(AccessTools.Method(typeof(ZNetScene), "CreateObject", new[] { typeof(ZDO) }));
            GameObject go = s_create(scene, zdo);
            s_lastWasSkip = go == null && zdo != null && zdo.Created;
            if (s_lastWasSkip) { EasyBake.ZoneTracker.ConsumeJustSkipped(zdo); s_gated++; }
            return go;
        }

        // Replaces ZNet.instance.IsServer() on the null branch: a skip is never destroyed; anything else is exactly vanilla's answer.
        public static bool DestroyAllowed(ZNet net)
        {
            if (s_lastWasSkip) { s_lastWasSkip = false; return false; }
            return net.IsServer();
        }

        private static int s_gated;
        /// <summary>Host skips the gate kept away from vanilla's invalid-prefab delete (the [Cache] summary).</summary>
        internal static int Gated => s_gated;
    }
}
