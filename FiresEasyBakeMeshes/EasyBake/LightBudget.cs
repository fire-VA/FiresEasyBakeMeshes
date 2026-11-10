using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.136 ([visual], Fire 10-27 YES, the homestead at 8 fps: FDT's RENDER TOP counted 13.8M triangles drawn for a 1.27M-triangle
    // scene, x10.9, with 22 point lights, 4 of them shadowed). In forward rendering each pixel light draws everything it touches
    // again, and a shadowed point light renders its range six more times. Lights on BUILD PIECES only (torches, fires, our stations'
    // glow lights; never the sun, players or creatures): the nearest MaxPixel near the camera keep their own mode and shadows; every
    // other piece light becomes vertex-lit (LightRenderMode.ForceVertex) with no shadows.
    // 1.2.139 (Fire 10-27: "fires shadows flash in and out every frame", the smelter's light pool flipping 628/629.webp): vanilla
    // LightLod (on most fire/smelter lights) sets shadows by distance itself, so the budget switched them off and LightLod back on,
    // and an original captured while LightLod had them off stayed "None" (the log's "0 shadowed"). Now: shadows on a light with a
    // LightLod are never touched (LightLod owns them); a light enters the budget inside EnterRange and leaves only past LeaveRange,
    // and holds any change for HoldSeconds; every flip writes one Debug line.
    [HarmonyLib.HarmonyPatch]
    internal static class LightBudget
    {
        // 1.2.254 (Fire: "the fire or adding fuel to it should count as an event"): every path that lights, refuels, douses or toggles a fire ends in Fireplace.UpdateState: the RPCs on the owner, and
        // UpdateFireplace's 2 s tick on every client, which is how a fire another game owns follows its ZDO fuel. So the change of the objects holding the light, seen around UpdateState, is the event.
        // Smelters / kilns and cooking stations switch their glow the same way. An event relists at the next rank tick (0.5 s), at most once a second, so fires streaming in cannot run the scene search every tick.
        private const float EventRelistMinSeconds = 1f;
        private static bool s_lightsChanged; private static float s_lastRelist = -999f; private static int s_fireEvents;
        private static int LitMask(GameObject a, GameObject b = null, GameObject c = null)
        {
            if (!EbmSelfCost.EventDriven) return 0;
            return (a != null && a.activeSelf ? 1 : 0) | (b != null && b.activeSelf ? 2 : 0) | (c != null && c.activeSelf ? 4 : 0);
        }
        private static void Compare(int before, int after) { if (before != after) { s_lightsChanged = true; s_fireEvents++; } }

        [HarmonyLib.HarmonyPatch(typeof(Fireplace), "UpdateState"), HarmonyLib.HarmonyPrefix]
        private static void FireBefore(Fireplace __instance, out int __state) { __state = LitMask(__instance.m_enabledObject, __instance.m_enabledObjectHigh, __instance.m_enabledObjectLow); }
        [HarmonyLib.HarmonyPatch(typeof(Fireplace), "UpdateState"), HarmonyLib.HarmonyPostfix]
        private static void FireAfter(Fireplace __instance, int __state) { Compare(__state, LitMask(__instance.m_enabledObject, __instance.m_enabledObjectHigh, __instance.m_enabledObjectLow)); }
        [HarmonyLib.HarmonyPatch(typeof(Smelter), "UpdateState"), HarmonyLib.HarmonyPrefix]
        private static void SmelterBefore(Smelter __instance, out int __state) { __state = LitMask(__instance.m_enabledObject); }
        [HarmonyLib.HarmonyPatch(typeof(Smelter), "UpdateState"), HarmonyLib.HarmonyPostfix]
        private static void SmelterAfter(Smelter __instance, int __state) { Compare(__state, LitMask(__instance.m_enabledObject)); }
        [HarmonyLib.HarmonyPatch(typeof(CookingStation), "UpdateVisual"), HarmonyLib.HarmonyPrefix]
        private static void StationBefore(CookingStation __instance, out int __state) { __state = LitMask(__instance.m_haveFireObject); }
        [HarmonyLib.HarmonyPatch(typeof(CookingStation), "UpdateVisual"), HarmonyLib.HarmonyPostfix]
        private static void StationAfter(CookingStation __instance, int __state) { Compare(__state, LitMask(__instance.m_haveFireObject)); }

        private const int MaxPixel = 8;
        private const float EnterRange = 18f, LeaveRange = 22f, HoldSeconds = 3f, RankSeconds = 0.5f, ListSeconds = 2f, LogSeconds = 10f;

        private class State { public LightRenderMode Mode; public LightShadows Shadows; public bool LodOwnsShadows; public bool Pixel = true; public float Since = -999f; }

        private static readonly Dictionary<Light, State> s_states = new Dictionary<Light, State>();
        private static readonly List<Light> s_pieceLights = new List<Light>();
        private static readonly List<(Light light, float d2)> s_ranked = new List<(Light, float)>();
        private static float s_nextRank, s_nextList, s_nextLog;
        private static bool s_ran;
        private static string s_lastLine;

        // 1.2.254 [Optimize] TickGroupEventDriven
        private const float ListFallback = 10f, RankFallback = 2f;
        private static float s_listFallback, s_rankFallback; private static int s_listPieces = -1; private static bool s_flipWaiting; private static Vector3 s_rankEye;
        private static System.Collections.IList s_allPieces; private static bool s_allPiecesLooked;
        private static int PieceCount()   // vanilla's private Piece.s_allPieces (a static readonly list: looked up once); -1 = not found, so the timer rules
        {
            if (!s_allPiecesLooked)
            {
                s_allPiecesLooked = true;
                var f = HarmonyLib.AccessTools.Field(typeof(Piece), "s_allPieces");
                s_allPieces = f != null ? f.GetValue(null) as System.Collections.IList : null;
                if (s_allPieces == null) EasyBakeLog.Debug("[EBM] light budget: Piece.s_allPieces not found; the scene search stays on its timer");
            }
            return s_allPieces != null ? s_allPieces.Count : -1;
        }

        internal static void Tick()
        {
            if (FiresEasyBakeMeshesPlugin.LightBudgetEnabled == null || !FiresEasyBakeMeshesPlugin.LightBudgetEnabled.Value) { RestoreAll(); return; }
            float now = Time.time;
            if (now < s_nextRank) return;
            s_nextRank = now + RankSeconds;
            var cam = Camera.main;
            if (cam == null) return;
            if (!s_ran) { s_ran = true; EasyBakeLog.Debug($"[EBM] light budget executing (first tick): nearest {MaxPixel} piece lights (enter {EnterRange:0} m, leave {LeaveRange:0} m, hold {HoldSeconds:0} s) keep pixel, the rest vertex-lit; LightLod lights keep their own shadows"); }
            // 1.2.254 [Optimize] TickGroupEventDriven: the scene search runs when a piece was created or destroyed (Piece's own list count) or a fire / smelter / station changed state, else every ListFallback (a safety net for lights no hook sees);
            // the ranking runs when the eye moved, the list changed or a flip is waiting on its hold, else every RankFallback (a light switched on or off has no event)
            bool eventDriven = EbmSelfCost.EventDriven, relisted = false;
            bool fireEvent = eventDriven && s_lightsChanged && now - s_lastRelist >= EventRelistMinSeconds;
            if (now >= s_nextList || fireEvent)
            {
                int pieces = eventDriven ? PieceCount() : -1;
                if (!eventDriven || fireEvent || pieces < 0 || pieces != s_listPieces || now >= s_listFallback)
                {
                    s_nextList = now + ListSeconds; Relist(); relisted = true;
                    s_lightsChanged = false; s_lastRelist = now;
                    s_listPieces = pieces; s_listFallback = now + ListFallback;
                    if (eventDriven) EbmSelfCost.Sub(EbmSelfCost.SubLightList, true);
                }
                else EbmSelfCost.Sub(EbmSelfCost.SubLightList, false);
            }

            Vector3 eye = cam.transform.position;
            if (eventDriven)
            {
                if (!relisted && !s_flipWaiting && (eye - s_rankEye).sqrMagnitude < 1f && now < s_rankFallback) { EbmSelfCost.Sub(EbmSelfCost.SubLightRank, false); return; }
                s_rankEye = eye; s_rankFallback = now + RankFallback;
                EbmSelfCost.Sub(EbmSelfCost.SubLightRank, true);
            }
            bool flipWaiting = false;
            s_ranked.Clear();
            foreach (var l in s_pieceLights)
                if (l != null && l.isActiveAndEnabled) s_ranked.Add((l, (l.transform.position - eye).sqrMagnitude));
            s_ranked.Sort((a, b) => a.d2.CompareTo(b.d2));

            int pixel = 0, vertex = 0, shadowed = 0;
            float enter2 = EnterRange * EnterRange, leave2 = LeaveRange * LeaveRange;
            for (int i = 0; i < s_ranked.Count; i++)
            {
                var l = s_ranked[i].light;
                float d2 = s_ranked[i].d2;
                if (!s_states.TryGetValue(l, out var st))
                {
                    var lod = l.GetComponentInParent<LightLod>();
                    st = new State { Mode = l.renderMode, Shadows = l.shadows, LodOwnsShadows = lod != null };
                    if (st.Mode == LightRenderMode.ForceVertex) st.Mode = LightRenderMode.Auto;
                    s_states[l] = st;
                }
                bool want = pixel < MaxPixel && (st.Pixel ? d2 <= leave2 : d2 <= enter2);
                if (want != st.Pixel && now - st.Since >= HoldSeconds)
                {
                    EasyBakeLog.Debug($"[EBM] light budget flip: '{l.name}' on '{PieceName(l)}' {(st.Pixel ? "pixel" : "vertex")} -> {(want ? "pixel" : "vertex")} at {Mathf.Sqrt(d2):0.0} m, rank {i + 1}/{s_ranked.Count}, shadows {(st.LodOwnsShadows ? "left to LightLod" : (want ? st.Shadows.ToString() : "None"))}");
                    st.Pixel = want;
                    st.Since = now;
                }
                else if (want != st.Pixel) flipWaiting = true;
                if (st.Pixel)
                {
                    if (l.renderMode != st.Mode) l.renderMode = st.Mode;
                    if (!st.LodOwnsShadows && l.shadows != st.Shadows) l.shadows = st.Shadows;
                    pixel++;
                    if (l.shadows != LightShadows.None) shadowed++;
                }
                else
                {
                    if (l.renderMode != LightRenderMode.ForceVertex) l.renderMode = LightRenderMode.ForceVertex;
                    if (!st.LodOwnsShadows && l.shadows != LightShadows.None) l.shadows = LightShadows.None;
                    vertex++;
                }
            }
            s_flipWaiting = flipWaiting;
            if (now >= s_nextLog)
            {
                s_nextLog = now + LogSeconds;
                string line = $"[EBM] light budget: {pixel} pixel ({shadowed} shadowed) / {vertex} vertex-lit of {s_ranked.Count} piece light(s); budget {MaxPixel}, enter {EnterRange:0} m / leave {LeaveRange:0} m; fire state events {s_fireEvents}";
                if (line != s_lastLine) { s_lastLine = line; EasyBakeLog.Debug(line); }
            }
        }

        private static string PieceName(Light l)
        {
            var p = l.GetComponentInParent<Piece>();
            return p != null ? Utils.GetPrefabName(p.gameObject) : "?";
        }

        private static void Relist()
        {
            s_pieceLights.Clear();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l == null || l.type == LightType.Directional) continue;
                if (l.GetComponentInParent<Piece>() == null) continue;   // pieces only: never players, creatures or effects
                s_pieceLights.Add(l);
            }
            // forget destroyed lights
            var dead = new List<Light>();
            foreach (var k in s_states.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) s_states.Remove(k);
        }

        internal static void RestoreAll()
        {
            if (s_states.Count == 0) return;
            foreach (var kv in s_states)
                if (kv.Key != null)
                {
                    kv.Key.renderMode = kv.Value.Mode;
                    if (!kv.Value.LodOwnsShadows) kv.Key.shadows = kv.Value.Shadows;
                }
            s_states.Clear();
            EasyBakeLog.Debug("[EBM] light budget off: every piece light back to its own mode + shadows");
        }
    }
}
