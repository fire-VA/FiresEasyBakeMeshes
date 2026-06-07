using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Distance-LOD short-circuit for LightFlicker.CustomUpdate.
    //
    // LightFlicker runs every frame on every lit prefab in the scene — torches,
    // braziers, hearths, candles, item-highlight FX, anything with a flickering
    // Light. Each call computes:
    //   - 6+ sin/cos calls for the flicker pattern
    //   - intensity smoothing math
    //   - a transform.localPosition write for the "flame jitter" offset
    //
    // Cost is microseconds per call, but a megabase with hundreds of lit pieces
    // aggregates to 8-10 ms/sec in profiling. At distance > ~30 m the player
    // can't see the per-frame intensity variation or the sub-meter jitter
    // anyway — Unity's distance falloff dominates and the light reads as
    // "constant" to the eye.
    //
    // We skip CustomUpdate entirely for persistent lights beyond a configurable
    // distance threshold. The light keeps whatever intensity it last had, so
    // it stays visible (just static).
    //
    // We NEVER skip lights with m_ttl > 0 — those are temporary FX (item-drop
    // sparkle, projectile trail, etc.) that must tick their TTL to self-destruct.
    // Suppressing CustomUpdate on them would leak GameObjects forever.
    [HarmonyPatch(typeof(LightFlicker), nameof(LightFlicker.CustomUpdate))]
    public static class LightFlicker_CustomUpdate_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(LightFlicker __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.LightFlickerLodEnabled.Value) return true;

            // Temporary FX lights have a self-destruct timer in CustomUpdate.
            // We must let them tick — skipping would leak the GameObject.
            if (__instance.m_ttl > 0f) return true;

            var player = Player.m_localPlayer;
            if (player == null) return true;

            float maxDist = FiresEasyBakeMeshesPlugin.LightFlickerLodDistance.Value;
            float sqrMax = maxDist * maxDist;

            // sqrMagnitude beats Vector3.Distance — no sqrt, no Vector3 alloc.
            var pp = player.transform.position;
            var lp = __instance.transform.position;
            float dx = pp.x - lp.x;
            float dy = pp.y - lp.y;
            float dz = pp.z - lp.z;
            float sqrDist = dx * dx + dy * dy + dz * dz;

            if (sqrDist > sqrMax)
                return false; // skip vanilla flicker computation

            return true;
        }
    }
}
