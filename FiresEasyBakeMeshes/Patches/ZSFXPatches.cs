using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Distance-LOD short-circuit for ZSFX.CustomUpdate.
    //
    // ZSFX is Valheim's MonoBehaviour wrapper around Unity's AudioSource —
    // it ticks every frame on every active sfx in the scene and handles:
    //   - m_time / m_delay accumulation for delayed-start sounds
    //   - fade-in / fade-out timer math
    //   - concurrency-disable volume crossfade
    //   - per-second reverb / spread update for looping sounds
    //   - final volume * fade * concurrency * modifier composition
    //   - pitch composition with reverb pitch modifier
    //
    // None of this is audible past the AudioSource's own falloff radius — Unity's
    // audio engine handles spatial attenuation independently. At a megabase
    // hundreds of looping torch / brazier / fountain sources tick in parallel,
    // aggregating to ~3 ms/sec of pure housekeeping for sounds the player can't
    // hear.
    //
    // We skip CustomUpdate entirely for sfx beyond a configurable distance from
    // the player. The AudioSource keeps whatever volume / pitch it last had —
    // which is effectively zero at the audible falloff edge — and resumes
    // normal behaviour when the player walks back into range.
    //
    // Risk surface:
    //   - A delayed one-shot far away won't tick its m_delay → won't Play() →
    //     player won't hear it. But by definition, far-away one-shots are
    //     inaudible anyway. When the player approaches, m_time resumes counting
    //     and the play fires a few seconds later — no real downside.
    //   - An active fade-out won't complete → the AudioSource stays "playing"
    //     inaudibly until either the player returns (fade completes) or the
    //     ZSFX is destroyed. Worst case a few voice slots stay reserved. Unity
    //     has 32 voice slots and even the loudest megabase rarely keeps more
    //     than ~5 concurrent fades active, so this isn't a real concern.
    [HarmonyPatch(typeof(ZSFX), nameof(ZSFX.CustomUpdate))]
    public static class ZSFX_CustomUpdate_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(ZSFX __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.ZSFXLodEnabled.Value) return true;

            var player = Player.m_localPlayer;
            if (player == null) return true;

            float maxDist = FiresEasyBakeMeshesPlugin.ZSFXLodDistance.Value;
            float sqrMax = maxDist * maxDist;

            // sqrMagnitude — no sqrt, no Vector3 allocation.
            var pp = player.transform.position;
            var sp = __instance.transform.position;
            float dx = pp.x - sp.x;
            float dy = pp.y - sp.y;
            float dz = pp.z - sp.z;
            float sqrDist = dx * dx + dy * dy + dz * dz;

            if (sqrDist > sqrMax)
                return false; // skip vanilla housekeeping; AudioSource carries last state

            return true;
        }
    }
}
