using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresEasyBakeMeshes.EasyBake.Indirect.Coverage;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.240 ([visual] 11-08, Fire: pieces arriving as he flies show snow in Meadows, then it clears). What turns a snow cap on and off, for the log, and a count
    // on demand. Reads only; changes nothing. Debug logging only for the per-piece lines.
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateSnowVisual))]
    internal static class EbmSnowCapLog
    {
        private const int MaxLines = 400;
        private static readonly Dictionary<int, float> s_onAt = new Dictionary<int, float>();
        private static int s_lines;
        private static readonly MaterialPropertyBlock s_block = new MaterialPropertyBlock();
        private static readonly int s_snowLevel = Shader.PropertyToID("_SnowLevel");

        private static bool CapShown(WearNTear w) =>
            (w.m_snow != null && w.m_snow.gameObject.activeSelf) || (w.m_snowWorn != null && w.m_snowWorn.gameObject.activeSelf) || (w.m_snowBroken != null && w.m_snowBroken.gameObject.activeSelf);

        private static string Where(WearNTear w)
        {
            var at = w.transform.position;
            var me = Player.m_localPlayer;
            string biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(at).ToString() : "?";
            float saved = -1f;
            var nv = w.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid()) saved = nv.GetZDO().GetFloat(ZDOVars.s_snow, 0f);
            return $"{Utils.GetPrefabName(w.gameObject.name)} at {at:F0}, biome {biome}, {(me != null ? Vector3.Distance(me.transform.position, at) : -1f):0} m away, buildup {w.m_snowBuildup:0.##}, saved {saved:0.##}, {Drawn(w)}";
        }

        private static string Drawn(WearNTear w) => (w.GetComponent<CoveredObject>() != null ? "covered" : "not covered") + ", " + (w.GetComponent<EasyBakeSuppressedVisuals>() != null ? "baked" : "own renderers");

        [HarmonyPostfix]
        private static void Postfix(WearNTear __instance)
        {
            if (__instance == null || __instance.m_snow == null || s_lines >= MaxLines || !EasyBakeLog.DebugOn()) return;
            int id = __instance.GetInstanceID();
            bool shown = CapShown(__instance);
            bool was = s_onAt.TryGetValue(id, out float since);
            if (shown == was) return;
            s_lines++;
            if (shown) { s_onAt[id] = Time.unscaledTime; EasyBakeLog.Debug("[SnowCap] on: " + Where(__instance)); }
            else { s_onAt.Remove(id); EasyBakeLog.Debug($"[SnowCap] off after {Time.unscaledTime - since:0.0} s: " + Where(__instance)); }
        }

        internal static IEnumerable<string> Report()
        {
            var say = new List<string>();
            var all = WearNTear.GetAllInstances();
            var me = Player.m_localPlayer;
            int withCap = 0, shown = 0, savedOver = 0, near = 0, nearLevel = 0, shownCovered = 0, shownBaked = 0;
            var byPrefab = new Dictionary<string, int>();
            foreach (var w in all)
            {
                if (w == null || w.m_snow == null) continue;
                withCap++;
                var nv = w.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && nv.GetZDO().GetFloat(ZDOVars.s_snow, 0f) > 0.25f) savedOver++;
                if (CapShown(w))
                {
                    shown++;
                    if (w.GetComponent<CoveredObject>() != null) shownCovered++;
                    if (w.GetComponent<EasyBakeSuppressedVisuals>() != null) shownBaked++;
                    string name = Utils.GetPrefabName(w.gameObject.name);
                    byPrefab.TryGetValue(name, out int n); byPrefab[name] = n + 1;
                }
                if (me != null && (w.transform.position - me.transform.position).sqrMagnitude < 40000f)
                {
                    near++;
                    var part = w.GetComponentInChildren<MeshRenderer>();
                    if (part != null) { part.GetPropertyBlock(s_block); if (s_block.HasFloat(s_snowLevel) && s_block.GetFloat(s_snowLevel) > 0f) nearLevel++; }
                }
            }
            say.Add($"[SnowCap] {withCap} pieces with a cap of {all.Count} loaded; {shown} caps showing ({shownCovered} on covered pieces, {shownBaked} on baked pieces); {savedOver} with saved snow over 0.25; within 200 m: {near} pieces, {nearLevel} with a per-piece _SnowLevel above 0");
            say.Add("[SnowCap] showing by prefab: " + (byPrefab.Count > 0 ? string.Join(", ", byPrefab.OrderByDescending(p => p.Value).Take(20).Select(p => p.Key + " x" + p.Value).ToArray()) : "none"));
            return say;
        }
    }
}
