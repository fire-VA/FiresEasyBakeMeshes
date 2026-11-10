using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.250 (moved from FGN's ZoneLoadFrames + ZoneLoadDiag, create / remove side only; Fire's SP split, plan row #8). Debug only, a client / local host only, behind
    // [Optimize] ZoneLoadDiagnostics: per frame the time inside CreateObjects (and per prefab, inside each CreateObject), ZoneSystem.Update and RemoveObjects, plus GC;
    // one [ZoneLoad] line per zone border the player crosses, and a [CreateLoop] line for objects that are retried every pass. FGN keeps its [NetSplit] line.
    [HarmonyPatch]
    internal static class EbmZoneLoadDiag
    {
        private const float CrossingWindowSeconds = 2f, ReportSeconds = 5f;
        private static bool s_saidOn;
        private static bool On
        {
            get
            {
                if (FiresEasyBakeMeshesPlugin.ZoneLoadDiagnostics == null || !FiresEasyBakeMeshesPlugin.ZoneLoadDiagnostics.Value || !EasyBakeLog.DebugOn()) return false;
                if (ZNet.instance != null && ZNet.instance.IsDedicated()) return false;
                if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info("[ZoneLoad] zone load diagnostics on (Debug lines: one per zone crossing, one per 5 s for retried creates)"); }
                return true;
            }
        }

        private sealed class Frame
        {
            internal int Number;
            internal double CreateMs, ZoneMs, RemoveMs;
            internal int Created, Gc0, Gc1, Gc2;
            internal readonly Dictionary<int, double> PrefabMs = new Dictionary<int, double>();
            internal readonly Dictionary<int, int> PrefabCount = new Dictionary<int, int>();
            internal void Reset(int n)
            {
                Number = n; CreateMs = ZoneMs = RemoveMs = 0; Created = 0; PrefabMs.Clear(); PrefabCount.Clear();
                Gc0 = GC.CollectionCount(0); Gc1 = GC.CollectionCount(1); Gc2 = GC.CollectionCount(2);
            }
        }

        private static readonly Frame s_a = new Frame(), s_b = new Frame();
        private static Frame s_cur = s_a, s_prev = s_b;
        private static Frame Current
        {
            get
            {
                int n = Time.frameCount;
                if (s_cur.Number != n) { Frame old = s_prev; s_prev = s_cur; s_cur = old; s_cur.Reset(n); }
                return s_cur;
            }
        }
        private static Frame Previous => s_prev.Number == Time.frameCount - 1 ? s_prev : null;

        private static string Describe(Frame f, float frameMs)
        {
            var top = new StringBuilder();
            foreach (var kv in f.PrefabMs.OrderByDescending(kv => kv.Value).Take(10))
            {
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kv.Key) : null;
                f.PrefabCount.TryGetValue(kv.Key, out int n);
                top.Append($"{(prefab != null ? prefab.name : kv.Key.ToString())} x{n} {kv.Value:0.0} ms, ");
            }
            string topText = top.Length > 0 ? "; top: " + top.ToString().TrimEnd(' ', ',') : "";
            int g0 = GC.CollectionCount(0) - f.Gc0, g1 = GC.CollectionCount(1) - f.Gc1, g2 = GC.CollectionCount(2) - f.Gc2;
            double known = f.CreateMs + f.ZoneMs + f.RemoveMs;
            return $"frame {f.Number}: CreateObjects {f.CreateMs:0.0} ms ({f.Created} made{topText}), ZoneSystem.Update {f.ZoneMs:0.0} ms, RemoveObjects {f.RemoveMs:0.0} ms, GC gen0/1/2 +{g0}/+{g1}/+{g2} (to now), " +
                   $"not in those {Math.Max(0, frameMs - known):0.0} ms";
        }

        // ---- one create, per prefab, and vanilla's retried creates ----
        private static long s_oneStart; private static int s_oneDepth;
        private static long s_createCalls, s_createNull;
        private static readonly Dictionary<int, int> s_nullByPrefab = new Dictionary<int, int>();

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void OnePre() { if (On && s_oneDepth++ == 0) s_oneStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void OnePost(ZDO zdo, GameObject __result)
        {
            if (!On) { s_oneDepth = 0; return; }
            s_createCalls++;
            if (__result == null && zdo != null) { int h = zdo.GetPrefab(); s_nullByPrefab.TryGetValue(h, out int c); s_nullByPrefab[h] = c + 1; s_createNull++; }
            if (--s_oneDepth > 0) return;
            s_oneDepth = 0;
            if (s_oneStart == 0 || zdo == null) return;
            double ms = (Stopwatch.GetTimestamp() - s_oneStart) * 1000.0 / Stopwatch.Frequency;
            s_oneStart = 0;
            Frame f = Current;
            int p = zdo.GetPrefab();
            f.PrefabMs.TryGetValue(p, out double m); f.PrefabMs[p] = m + ms;
            f.PrefabCount.TryGetValue(p, out int n); f.PrefabCount[p] = n + 1;
            if (__result != null) f.Created++;
        }

        // ---- ZoneSystem.Update ----
        private static long s_zoneStart; private static int s_zoneDepth;
        [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void ZonePre() { if (On && s_zoneDepth++ == 0) s_zoneStart = Stopwatch.GetTimestamp(); }

        [HarmonyPatch(typeof(ZoneSystem), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void ZonePost()
        {
            if (!On) { s_zoneDepth = 0; return; }
            if (--s_zoneDepth > 0) return;
            s_zoneDepth = 0;
            if (s_zoneStart != 0) Current.ZoneMs += (Stopwatch.GetTimestamp() - s_zoneStart) * 1000.0 / Stopwatch.Frequency;
            s_zoneStart = 0;
        }

        // ---- the whole create / remove passes, around a crossing ----
        private static long s_createStart, s_removeStart;
        private static int s_createBefore, s_removeBefore, s_created, s_destroyed;
        private static double s_createMs, s_removeMs;

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void CreatePre(ZNetScene __instance) { if (!On) return; s_createStart = Stopwatch.GetTimestamp(); s_createBefore = __instance.NrOfInstances(); }

        [HarmonyPatch(typeof(ZNetScene), "CreateObjects"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void CreatePost(ZNetScene __instance)
        {
            if (!On || s_createStart == 0) return;
            double ms = (Stopwatch.GetTimestamp() - s_createStart) * 1000.0 / Stopwatch.Frequency;
            s_createMs += ms; Current.CreateMs += ms;
            s_created += Mathf.Max(0, __instance.NrOfInstances() - s_createBefore);
            s_createStart = 0;
        }

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void RemovePre(ZNetScene __instance) { if (!On) return; s_removeStart = Stopwatch.GetTimestamp(); s_removeBefore = __instance.NrOfInstances(); }

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void RemovePost(ZNetScene __instance)
        {
            if (!On || s_removeStart == 0) return;
            double ms = (Stopwatch.GetTimestamp() - s_removeStart) * 1000.0 / Stopwatch.Frequency;
            s_removeMs += ms; Current.RemoveMs += ms;
            s_destroyed += Mathf.Max(0, s_removeBefore - __instance.NrOfInstances());
            s_removeStart = 0;
        }

        // ---- vanilla's near walk (when vanilla's body runs) and the per-5-s line ----
        private static long s_sortStart, s_walked, s_uncreated, s_sortFrames; private static double s_sortMs;

        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void SortPre(List<ZDO> currentNearObjects)
        {
            if (!On || currentNearObjects == null) return;
            s_sortStart = Stopwatch.GetTimestamp(); s_sortFrames++;
            s_walked += currentNearObjects.Count;
            for (int i = 0; i < currentNearObjects.Count; i++) if (!currentNearObjects[i].Created) s_uncreated++;
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void SortPost() { if (On && s_sortStart != 0) s_sortMs += (Stopwatch.GetTimestamp() - s_sortStart) * 1000.0 / Stopwatch.Frequency; s_sortStart = 0; }

        private static float s_nextReport = -1f;

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void AfterNetUpdate()
        {
            if (!On) { s_nextReport = -1f; return; }
            float now = Time.realtimeSinceStartup;
            Crossing(now);
            if (s_nextReport < 0f) { s_nextReport = now + ReportSeconds; return; }
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;
            ReportCreateLoop();
        }

        private static void ReportCreateLoop()
        {
            if (s_sortFrames == 0 && s_createCalls == 0) return;
            var top = new List<KeyValuePair<int, int>>(s_nullByPrefab);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < top.Count && i < 6; i++)
            {
                GameObject p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(top[i].Key) : null;
                sb.Append(i == 0 ? " never created: " : ", ").Append(p != null ? p.name : "unknown prefab " + top[i].Key).Append(" x").Append(top[i].Value);
            }
            long frames = Math.Max(1, s_sortFrames);
            EasyBakeLog.Debug($"[CreateLoop] last {ReportSeconds:0} s: near ZDOs walked {s_walked / frames}/frame, not created {s_uncreated / frames}/frame (vanilla's body), " +
                              $"CreateObject calls {s_createCalls} (null {s_createNull}), CreateObjectsSorted {s_sortMs:0.0} ms over {s_sortFrames} frames;{sb}");
            s_walked = s_uncreated = s_createCalls = s_createNull = s_sortFrames = 0; s_sortMs = 0; s_nullByPrefab.Clear();
        }

        // ---- the crossing line ----
        private static Vector2s s_zone, s_crossZone;
        private static bool s_haveZone;
        private static float s_crossAt = -1f, s_worstFrameMs;
        private static string s_worstWhy = "";

        private static void Crossing(float now)
        {
            Player p = Player.m_localPlayer;
            if (p == null) { s_haveZone = false; s_crossAt = -1f; return; }
            if (s_crossAt >= 0f)
            {
                float dtMs = Time.unscaledDeltaTime * 1000f;   // this frame's delta is the PREVIOUS frame's duration
                if (dtMs > s_worstFrameMs) { s_worstFrameMs = dtMs; Frame prev = Previous; s_worstWhy = prev != null ? Describe(prev, dtMs) : "frame breakdown not kept"; }
                if (now - s_crossAt >= CrossingWindowSeconds)
                {
                    EasyBakeLog.Debug($"[ZoneLoad] crossing into {s_crossZone.x},{s_crossZone.y}: created {s_created} / destroyed {s_destroyed} objects over {CrossingWindowSeconds:0} s, " +
                                      $"CreateObjects {s_createMs:0.0} ms, RemoveObjects {s_removeMs:0.0} ms, worst frame {s_worstFrameMs:0.0} ms; worst {s_worstWhy}");
                    s_crossAt = -1f;
                }
            }
            Vector2s z = ZoneSystem.GetZone(p.transform.position);
            if (s_haveZone && (z.x != s_zone.x || z.y != s_zone.y))
            {
                s_crossAt = now; s_crossZone = z; s_worstFrameMs = 0f; s_worstWhy = "";
                s_created = s_destroyed = 0; s_createMs = s_removeMs = 0;
            }
            s_zone = z; s_haveZone = true;
        }
    }
}
