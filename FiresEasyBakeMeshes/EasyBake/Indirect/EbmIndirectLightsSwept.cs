using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [Optimize] IndirectLightsSwept ([worldgen] 11-09, Tools\AUDIT_shave_gpu_lights_20261109.md #13; the gather was almost all of the 0.18 ms "cells" phase on frames that reused the cell verdict).
    // Off: every LightLod is asked every frame. On: a light's state only changes in LightLod's own 1 s coroutine, so the "can it cast right now" question walks the registry a slice per frame
    // (a whole cycle is about 16 frames) and only the lights that passed it are asked every frame. A light that gains shadows can start up to about 0.2 s late, inside vanilla's fade-in from 0.
    // The registry follows LightLod.Awake / OnDestroy, the same membership as LightLod.m_lights. Selection after that is unchanged: frustum, nearest four.
    internal static partial class EbmIndirect
    {
        private const int SweepMin = 8, SweepCycle = 16;
        private static readonly List<LightLod> s_lodList = new List<LightLod>(256), s_lodCand = new List<LightLod>(16);
        private static readonly Dictionary<LightLod, int> s_lodAt = new Dictionary<LightLod, int>(256);
        private static readonly HashSet<LightLod> s_lodCandSet = new HashSet<LightLod>();
        private static int s_lodCursor;
        private static bool s_lodSeeded;
        private static float s_lodSayAt;

        private static bool LightsSweptOn => FiresEasyBakeMeshesPlugin.IndirectLightsSwept != null && FiresEasyBakeMeshesPlugin.IndirectLightsSwept.Value;

        private static bool CanCastNow(Light l) => l != null && l.isActiveAndEnabled && l.shadows != LightShadows.None && l.shadowStrength > 0f && (l.type == LightType.Point || l.type == LightType.Spot) && l.range > 0f;

        // one LightLod through the frustum test and the nearest-first insertion; false = it cannot cast at all right now
        private static bool GatherOne(LightLod lod, int cap, Vector3 camPos)
        {
            if (lod == null) return false;
            Light l = s_lodLight(lod);
            if (!CanCastNow(l)) return false;
            Vector3 pos = l.transform.position;
            if (!SphereInCamera(pos, l.range)) return true;
            float d = (pos - camPos).sqrMagnitude;
            // keep the `cap` nearest (insertion into a fixed array; no allocation)
            int at = s_localCount;
            while (at > 0 && s_localDist[at - 1] > d) at--;
            if (at >= cap) return true;
            int last = Mathf.Min(s_localCount, cap - 1);
            for (int j = last; j > at; j--) { s_local[j] = s_local[j - 1]; s_localDist[j] = s_localDist[j - 1]; }
            s_local[at] = l; s_localDist[at] = d;
            if (s_localCount < cap) s_localCount++;
            return true;
        }

        private static void GatherSwept(int cap, Vector3 camPos)
        {
            bool whole = !s_lodSeeded;
            if (whole)
            {
                s_lodSeeded = true;
                foreach (var seen in s_lightLods) RegisterLod(seen);
            }
            int step = whole ? s_lodList.Count : Mathf.Min(s_lodList.Count, Mathf.Max(SweepMin, s_lodList.Count / SweepCycle));
            for (int j = 0; j < step && s_lodList.Count > 0; j++)
            {
                if (s_lodCursor >= s_lodList.Count) s_lodCursor = 0;
                var lod = s_lodList[s_lodCursor];
                if (lod == null) { UnregisterLod(lod); continue; }
                s_lodCursor++;
                if (!s_lodCandSet.Contains(lod) && CanCastNow(s_lodLight(lod))) { s_lodCandSet.Add(lod); s_lodCand.Add(lod); }
            }
            for (int i = s_lodCand.Count - 1; i >= 0; i--)
            {
                var lod = s_lodCand[i];
                if (GatherOne(lod, cap, camPos)) continue;
                s_lodCandSet.Remove(lod);
                s_lodCand[i] = s_lodCand[s_lodCand.Count - 1];
                s_lodCand.RemoveAt(s_lodCand.Count - 1);
            }
            if (Time.unscaledTime >= s_lodSayAt && EasyBakeLog.DebugOn())
            {
                s_lodSayAt = Time.unscaledTime + 30f;
                EasyBakeLog.Debug($"[Indirect] lights swept: {s_lodList.Count} LightLod(s) registered, {step} tested this frame, {s_lodCand.Count} can cast now, {s_localCount} picked");
            }
        }

        private static void RegisterLod(LightLod lod)
        {
            if (lod == null || s_lodAt.ContainsKey(lod)) return;
            s_lodAt[lod] = s_lodList.Count;
            s_lodList.Add(lod);
        }

        private static void UnregisterLod(LightLod lod)
        {
            if ((object)lod == null || !s_lodAt.TryGetValue(lod, out int at)) { if ((object)lod == null) s_lodList.Remove(lod); return; }
            s_lodAt.Remove(lod);
            int end = s_lodList.Count - 1;
            if (at != end) { var moved = s_lodList[end]; s_lodList[at] = moved; if ((object)moved != null) s_lodAt[moved] = at; }
            s_lodList.RemoveAt(end);
            if (s_lodCandSet.Remove(lod)) s_lodCand.Remove(lod);
        }

        internal static void LightLodAwake(LightLod lod) { if (s_lodSeeded) RegisterLod(lod); }
        internal static void LightLodGone(LightLod lod) { if (s_lodSeeded) UnregisterLod(lod); }
    }

    [HarmonyPatch(typeof(LightLod), "Awake")]
    internal static class LightLod_Awake_LightsSwept
    {
        [HarmonyPostfix]
        private static void Postfix(LightLod __instance) => EbmIndirect.LightLodAwake(__instance);
    }

    [HarmonyPatch(typeof(LightLod), "OnDestroy")]
    internal static class LightLod_OnDestroy_LightsSwept
    {
        [HarmonyPrefix]
        private static void Prefix(LightLod __instance) => EbmIndirect.LightLodGone(__instance);
    }
}
