using System;
using HarmonyLib;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// EBM port (Core 0.2.1216 Far\ moved into EBM; Fire 11-08: EBM relies on no other mod): the three world events the far view used
    /// from Core's CoreEvents, with the same semantics, raised from the same vanilla methods by EBM's own Harmony patches (patched by
    /// EBM's ApplyPatches like its other [HarmonyPatch] classes).
    ///  - WorldReady(uid): ZNet.Awake on a server / host (Postfix, Priority.Last), ZNet.RPC_PeerInfo on a client (the first one while no
    ///    world is ready). <see cref="WhenWorldReady"/> subscribes AND replays at once when a world is already up.
    ///  - WorldClosed: ZNet.OnDestroy (Prefix), only when a world was ready; clears IsWorldReady / WorldUid first.
    /// Every handler is called in try/catch on the main thread (a throw = one Warning naming it). Debug:
    /// "[EbmFarWorldEvents] &lt;event&gt; raised (...) to N subscriber(s)".
    /// </summary>
    internal static class EbmFarWorldEvents
    {
        internal static event Action<long> WorldReady;
        internal static event Action WorldClosed;

        internal static bool IsWorldReady { get; private set; }
        internal static long WorldUid { get; private set; }

        /// <summary>Subscribes to WorldReady and replays it at once when a world is already up.</summary>
        internal static void WhenWorldReady(Action<long> a)
        {
            if (a == null) return;
            WorldReady += a;
            if (IsWorldReady)
            {
                long uid = WorldUid;
                try { a(uid); } catch (Exception e) { EasyBakeLog.Warn($"[EbmFarWorldEvents] a {nameof(WorldReady)} subscriber threw: {e.Message}"); }
            }
        }

        private static void Raise(Delegate h, string name, Action<Delegate> call, string detail = null)
        {
            int n = 0;
            if (h != null)
                foreach (var d in h.GetInvocationList())
                {
                    n++;
                    try { call(d); } catch (Exception e) { EasyBakeLog.Warn($"[EbmFarWorldEvents] {name} subscriber {d.Method.DeclaringType?.FullName}.{d.Method.Name} threw: {e.Message}"); }
                }
            EasyBakeLog.Debug($"[EbmFarWorldEvents] {name} raised{(detail != null ? " (" + detail + ")" : "")} to {n} subscriber(s)");
        }

        private static void SetWorld(long uid, string where)
        {
            IsWorldReady = true; WorldUid = uid;
            Raise(WorldReady, nameof(WorldReady), d => ((Action<long>)d)(uid), $"uid {uid}, {where}");
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNet_Awake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNet __instance)
            {
                try { if (__instance.IsServer() && ZNet.World != null) SetWorld(ZNet.World.m_uid, "server start"); }
                catch (Exception e) { EasyBakeLog.Debug($"[EbmFarWorldEvents] world ready (server) skipped: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class ZNet_RPC_PeerInfo
        {
            private static void Postfix(ZNet __instance)
            {
                try { if (!__instance.IsServer() && !IsWorldReady && ZNet.World != null) SetWorld(ZNet.World.m_uid, "joined server"); }
                catch (Exception e) { EasyBakeLog.Debug($"[EbmFarWorldEvents] peer info skipped: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy
        {
            private static void Prefix()
            {
                if (!IsWorldReady) return;
                IsWorldReady = false; WorldUid = 0L;
                Raise(WorldClosed, nameof(WorldClosed), d => ((Action)d)());
            }
        }
    }
}
