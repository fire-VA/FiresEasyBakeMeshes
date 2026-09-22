using System.Collections.Generic;
using UnityEngine;
using Log = FiresEasyBakeMeshes.EasyBakeLog;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ═══ DON'T BUILD IT JUST TO TEAR IT DOWN ═════════════════════════════════
    // TrySkipCreation can only skip a piece once its zone is BAKED — it needs the
    // cached transform to hang a stand-in on. ZNetScene does not wait for that,
    // so when you walk into a zone vanilla instantiates everything first and
    // ConvertLivePieces destroys it again a moment later:
    //
    //     "Pieces vanilla created before their stand-in existed are unloaded
    //      again once it does"
    //
    // Measured on a converted 7.68M-ZDO world: 4,072 skipped at creation but
    // 5,282 unloaded AFTER creation. Those 5,282 were built, registered in the
    // scene, mirrored into the sector index and then destroyed — about 970 ms of
    // instantiation, plus the destroy, for nothing. It is the one part of the
    // ZNetScene cost that is genuinely waste rather than volume.
    //
    // So: while a zone's bake is still pending, a piece that WOULD be skippable
    // is deferred instead of created. ZNetScene simply retries it on a later
    // pass, and by then the bake has usually landed and the normal skip path
    // takes it — built zero times instead of once-then-destroyed.
    //
    // THE TWO THINGS THAT MAKE THIS SAFE:
    //
    //  1. CLIENT ONLY, and not by convention — by consequence. In
    //     CreateObjectsSorted a null return means something very different on a
    //     server:
    //         else if (ZNet.instance.IsServer()) { ... ZDOMan.instance.DestroyZDO(zdo); }
    //     Deferring on a server would DELETE the object from the world. The
    //     caller gates on SkipCreationActive(), which already requires
    //     !ZNet.instance.IsServer(); this adds its own check as well rather than
    //     trusting that to stay true.
    //
    //  2. A DEADLINE. A zone that never bakes — bake disabled, an error, a zone
    //     that holds real pieces — must not leave its contents invisible
    //     forever. After DeferSeconds the zone is released and everything in it
    //     is created normally. The deferral is an optimisation that expires, not
    //     a gate that can strand the world.
    // ═══ MEASURED, AND IT MADE THINGS WORSE. OFF BY DEFAULT. ═════════════════
    // The reasoning above is sound and the result was not. Measured on the same
    // world, same spot, with the defer ON versus OFF:
    //
    //                         OFF          ON
    //     fps (avg)           51.7         20.1
    //     ZNetScene.Update    4,632 ms     7,286 ms   (per 30 s)
    //     CreateObject calls  24,494       39,620
    //     unloaded after      5,282        10,916
    //     frames over 16 ms   547 / 1553   604 / 604
    //
    // Two things went wrong, both visible in its own counters
    // ("245,836 held for a pending bake ... 0 zone(s) released once baked"):
    //
    //  1. A deferred ZDO is never marked Created, so ZNetScene retries it on
    //     EVERY pass. Each retry re-enters TrySkipCreation, re-resolves the zone
    //     and rebuilds a PieceIdentity, and the piece stays in the candidate
    //     list to be walked and sorted again. The hold is not free; it is paid
    //     for once per pass until it resolves.
    //  2. The bake almost never lands inside the window — 0 zones released
    //     against thousands that gave up — so after 3 s the piece is built
    //     anyway. All of the retry cost, none of the saving, and the creation
    //     merely happens later, which is why "unloaded after creation" DOUBLED
    //     instead of falling.
    //
    // Kept rather than deleted because the underlying waste is real (pieces
    // built and then destroyed) and the fix is to make the BAKE land before
    // vanilla populates the zone, not to stall vanilla until it does. Anyone
    // turning this on should expect the numbers above until that changes.
    internal static class DeferCreation
    {
        private const float DeferSeconds = 3f;

        internal static bool Enabled =>
            FiresEasyBakeMeshesPlugin.DeferCreationWhileBaking != null
            && FiresEasyBakeMeshesPlugin.DeferCreationWhileBaking.Value;

        private static readonly Dictionary<Vector2s, float> s_firstSeen = new Dictionary<Vector2s, float>();
        private static int s_deferred, s_released, s_expired;
        private static float s_lastReport;

        internal static bool ShouldDefer(ZDO zdo, Vector2s zone, bool bakePending)
        {
            if (!Enabled) return false;
            if (!bakePending) { s_firstSeen.Remove(zone); return false; }
            if (ZNet.instance == null || ZNet.instance.IsServer()) return false;

            float now = Time.unscaledTime;
            if (!s_firstSeen.TryGetValue(zone, out float since))
            {
                s_firstSeen[zone] = now;
                s_deferred++;
                return true;
            }
            if (now - since < DeferSeconds) { s_deferred++; return true; }

            // Waited long enough: this zone is not going to bake. Let it build.
            s_expired++;
            return false;
        }

        // Called when a zone finishes baking, so the next pass takes the real skip path.
        internal static void OnZoneBaked(Vector2s zone)
        {
            if (s_firstSeen.Remove(zone)) s_released++;
        }

        internal static void Report()
        {
            if (Time.unscaledTime - s_lastReport < 30f) return;
            s_lastReport = Time.unscaledTime;
            if (s_deferred == 0 && s_expired == 0) return;
            Log.Info($"[Defer] {s_deferred:N0} creation(s) held while their zone was still baking " +
                     $"({s_released:N0} zone(s) released once baked, {s_expired:N0} gave up after {DeferSeconds:0}s and built normally). " +
                     "Each hold that reaches the bake is a piece never built and never destroyed.");
            s_deferred = 0; s_released = 0; s_expired = 0;
        }
    }
}
