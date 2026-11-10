using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Prefix on LightFlicker.CustomUpdate. Persistent lights are throttled by distance
    // tier rather than skipped outright: vanilla zeroes m_light.intensity in OnEnable and
    // only CustomUpdate writes it back, so a light that never ticks stays black. Skipped
    // deltaTime is banked and released to the next real call, keeping fade-in and TTL
    // timing true to wall clock.
    [HarmonyPatch(typeof(LightFlicker), nameof(LightFlicker.CustomUpdate))]
    public static class LightFlicker_CustomUpdate_Patch
    {
        private const float FullRateInterval = 0f;
        private const float SlowestSupportedRate = 0.01f;
        private const float TierCheckJitterFraction = 0.25f;
        private const int TierCheckPhaseSalt = 14961;
        private const uint PhaseMask = 0xFFFFFF;

        private static readonly AccessTools.FieldRef<LightFlicker, Light> LightRef =
            AccessTools.FieldRefAccess<LightFlicker, Light>("m_light");

        private static readonly AccessTools.FieldRef<LightFlicker, float> ElapsedTimeRef =
            AccessTools.FieldRefAccess<LightFlicker, float>("m_time");

        private static readonly ConditionalWeakTable<LightFlicker, ThrottleState> ThrottleStates =
            new ConditionalWeakTable<LightFlicker, ThrottleState>();

        private sealed class ThrottleState
        {
            public float NextUpdateTime;
            public float NextTierCheckTime;
            public float TierInterval;
            public float BankedDeltaTime;
            public bool HasTier;
        }

        [HarmonyPrefix]
        public static bool Prefix(LightFlicker __instance, ref float deltaTime)
        {
            if (!FiresEasyBakeMeshesPlugin.LightFlickerLodEnabled.Value) return true;
            if (__instance == null) return true;
            if (Player.m_localPlayer == null) return true;

            var light = LightRef(__instance);
            if (light == null) return true;

            // 1.2.166: a dark light needs no state of its own, and most lights past their LOD are dark
            if (!light.enabled)
            {
                if (ThrottleStates.TryGetValue(__instance, out var dark)) ClearSchedule(dark);
                return false;
            }

            var state = ThrottleStates.GetOrCreateValue(__instance);

            if (NeedsEveryFrame(__instance))
                return ReleaseBankedDeltaTime(state, ref deltaTime);

            float now = Time.time;
            RefreshTierInterval(__instance, state, now);

            if (state.TierInterval <= FullRateInterval)
            {
                state.NextUpdateTime = 0f;
                return ReleaseBankedDeltaTime(state, ref deltaTime);
            }

            if (IsUpdateDue(state, now, __instance.GetInstanceID()))
                return ReleaseBankedDeltaTime(state, ref deltaTime);

            state.BankedDeltaTime += deltaTime;
            return false;
        }

        private static bool NeedsEveryFrame(LightFlicker flicker)
        {
            if (flicker.m_ttl > 0f) return true;
            return flicker.m_fadeInDuration > 0f && ElapsedTimeRef(flicker) < flicker.m_fadeInDuration;
        }

        private static void RefreshTierInterval(LightFlicker flicker, ThrottleState state, float now)
        {
            if (state.HasTier && now < state.NextTierCheckTime) return;

            state.TierInterval = ResolveTierInterval(flicker);
            if (FiresEasyBakeMeshesPlugin.LightFlickerViewCull.Value && OffView(flicker))   // 1.2.174
                state.TierInterval = Mathf.Max(state.TierInterval, IntervalFromRate(FiresEasyBakeMeshesPlugin.LightFlickerDistantRate.Value));
            state.HasTier = true;

            float checkInterval = IntervalFromRate(FiresEasyBakeMeshesPlugin.LightFlickerTierCheckRate.Value);
            state.NextTierCheckTime = now + checkInterval * JitterScale(flicker.GetInstanceID() ^ TierCheckPhaseSalt);
        }

        // 1.2.174 [Batching] LightFlickerViewCull: a light whose whole range is outside the camera lights nothing the camera sees, so it takes the
        // slowest flicker rate at any distance. Checked at the tier rate; a light turned into view catches up at its next check, keeping its intensity meanwhile.
        private static readonly Plane[] s_planes = new Plane[6];
        private static int s_planesFrame = -1, s_checks, s_off;
        private static float s_nextReport;
        private static bool s_announced;

        private static bool OffView(LightFlicker flicker)
        {
            var cam = Utils.GetMainCamera();
            var light = LightRef(flicker);
            if (cam == null || light == null || light.type == LightType.Directional) return false;
            if (s_planesFrame != Time.frameCount) { s_planesFrame = Time.frameCount; GeometryUtility.CalculateFrustumPlanes(cam, s_planes); }
            s_checks++;
            Vector3 p = flicker.transform.position;
            float reach = light.range + 1f;   // the flicker offset moves the light a little
            bool off = false;
            for (int i = 0; i < 6 && !off; i++) off = s_planes[i].GetDistanceToPoint(p) < -reach;
            if (off) s_off++;
            if (off && !s_announced) { s_announced = true; EasyBakeLog.Info("[LightFlicker] lights out of the camera's view flicker at the slowest rate"); }
            if (Time.time >= s_nextReport)
            {
                s_nextReport = Time.time + 60f;
                EasyBakeLog.Debug($"[LightFlicker] view cull, last 60 s: {s_off} of {s_checks} tier checks out of view");
                s_checks = 0; s_off = 0;
            }
            return off;
        }

        private static float ResolveTierInterval(LightFlicker flicker)
        {
            float nearDistance = FiresEasyBakeMeshesPlugin.LightFlickerLodDistance.Value * EasyBake.PerformanceProfile.Distance;   // 1.2.135
            float midDistance = Mathf.Max(nearDistance, FiresEasyBakeMeshesPlugin.LightFlickerMidDistance.Value * EasyBake.PerformanceProfile.Distance);
            float farDistance = Mathf.Max(midDistance, FiresEasyBakeMeshesPlugin.LightFlickerFarDistance.Value * EasyBake.PerformanceProfile.Distance);

            float sqrDistance = (flicker.transform.position - Player.m_localPlayer.transform.position).sqrMagnitude;

            if (sqrDistance <= nearDistance * nearDistance) return FullRateInterval;
            if (sqrDistance <= midDistance * midDistance)
                return IntervalFromRate(FiresEasyBakeMeshesPlugin.LightFlickerMidRate.Value);
            if (sqrDistance <= farDistance * farDistance)
                return IntervalFromRate(FiresEasyBakeMeshesPlugin.LightFlickerFarRate.Value);
            return IntervalFromRate(FiresEasyBakeMeshesPlugin.LightFlickerDistantRate.Value);
        }

        private static bool IsUpdateDue(ThrottleState state, float now, int phaseSeed)
        {
            if (state.NextUpdateTime <= 0f)
            {
                state.NextUpdateTime = now + state.TierInterval * StablePhase01(phaseSeed);
                return true;
            }
            if (now < state.NextUpdateTime) return false;
            state.NextUpdateTime = now + state.TierInterval;
            return true;
        }

        private static bool ReleaseBankedDeltaTime(ThrottleState state, ref float deltaTime)
        {
            if (state.BankedDeltaTime > 0f)
            {
                deltaTime += state.BankedDeltaTime;
                state.BankedDeltaTime = 0f;
            }
            return true;
        }

        private static void ClearSchedule(ThrottleState state)
        {
            state.NextUpdateTime = 0f;
            state.NextTierCheckTime = 0f;
            state.TierInterval = FullRateInterval;
            state.BankedDeltaTime = 0f;
            state.HasTier = false;
        }

        private static float IntervalFromRate(float updatesPerSecond)
        {
            return 1f / Mathf.Max(SlowestSupportedRate, updatesPerSecond);
        }

        private static float JitterScale(int seed)
        {
            return 1f - TierCheckJitterFraction + TierCheckJitterFraction * StablePhase01(seed);
        }

        private static float StablePhase01(int seed)
        {
            uint hash = (uint)seed;
            hash ^= hash >> 16;
            hash *= 0x85EBCA6BU;
            hash ^= hash >> 13;
            hash *= 0xC2B2AE35U;
            hash ^= hash >> 16;
            return (hash & PhaseMask) / (float)PhaseMask;
        }
    }
}
