using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.281 (lead 11-11, Fire: "we shouldn't create far baked pieces until we're in range"; census FIRST, then the 1.2.282 design): every 30 s
    // (Debug only), the LIVE pieces of baked zones split by distance from the local player and by kind: pure static structure (no gameplay
    // component) vs pieces with behaviour (door, bed, station, container, fire, light, ward, sign, item stand, smelter ...). It says how many of a
    // town's instances 1.2.282 could leave uncreated. Reads EBM's own zone lists (no scene walk); the kind is decided once per ZDO prefab hash.
    internal static partial class ZoneTracker
    {
        private static float s_skipCensusNext;
        private static readonly HarmonyLib.AccessTools.FieldRef<WearNTear, ZNetView> s_wntView = HarmonyLib.AccessTools.FieldRefAccess<WearNTear, ZNetView>("m_nview");
        private static readonly Dictionary<int, bool> s_behaviourByPrefab = new Dictionary<int, bool>();
        private static readonly int[] s_bandStatic = new int[4], s_bandBehaviour = new int[4];
        private static readonly float[] s_bandEdge = { 32f, 64f, 128f };

        internal static void SkipCensusTick()
        {
            float now = Time.unscaledTime;
            if (now < s_skipCensusNext) return;
            s_skipCensusNext = now + 30f;
            var me = Player.m_localPlayer;
            if (me == null || !EasyBakeLog.DebugOn() || _zones.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.Array.Clear(s_bandStatic, 0, 4); System.Array.Clear(s_bandBehaviour, 0, 4);
            Vector3 p = me.transform.position;
            int zonesBaked = 0, skippedNow = 0;
            foreach (var kv in _zones)
            {
                var st = kv.Value;
                if (st == null || !st.Baked) continue;
                zonesBaked++; skippedNow += st.Skipped.Count;
                foreach (var w in st.Pieces)
                {
                    if (w == null) continue;
                    var nv = s_wntView(w); var zdo = nv != null ? nv.GetZDO() : null;   // FieldRef: m_nview is private (a publicized read throws at JIT)
                    int h = zdo != null ? zdo.GetPrefab() : 0;
                    if (!s_behaviourByPrefab.TryGetValue(h, out bool beh)) { beh = HasBehaviour(w.gameObject); s_behaviourByPrefab[h] = beh; }
                    float d = Vector3.Distance(p, w.transform.position);
                    int band = d < s_bandEdge[0] ? 0 : d < s_bandEdge[1] ? 1 : d < s_bandEdge[2] ? 2 : 3;
                    if (beh) s_bandBehaviour[band]++; else s_bandStatic[band]++;
                }
            }
            EasyBakeLog.Debug($"[SkipCensus] live pieces in {zonesBaked} baked zone(s), by distance <32 / 32-64 / 64-128 / 128+ m: static structure {s_bandStatic[0]} / {s_bandStatic[1]} / {s_bandStatic[2]} / {s_bandStatic[3]}, "
                + $"with behaviour {s_bandBehaviour[0]} / {s_bandBehaviour[1]} / {s_bandBehaviour[2]} / {s_bandBehaviour[3]}; already skipped at creation {skippedNow}; walk {sw.Elapsed.TotalMilliseconds:0.0} ms "
                + "(1.2.282 would leave the far static structure uncreated)");
        }

        private static bool HasBehaviour(GameObject go)
        {
            if (go == null) return true;
            return go.GetComponentInChildren<Door>(true) != null || go.GetComponentInChildren<Bed>(true) != null || go.GetComponentInChildren<CraftingStation>(true) != null
                || go.GetComponentInChildren<Container>(true) != null || go.GetComponentInChildren<Fireplace>(true) != null || go.GetComponentInChildren<Light>(true) != null
                || go.GetComponentInChildren<PrivateArea>(true) != null || go.GetComponentInChildren<Sign>(true) != null || go.GetComponentInChildren<ItemStand>(true) != null
                || go.GetComponentInChildren<Smelter>(true) != null || go.GetComponentInChildren<Fermenter>(true) != null || go.GetComponentInChildren<Beehive>(true) != null
                || go.GetComponentInChildren<CookingStation>(true) != null || go.GetComponentInChildren<TeleportWorld>(true) != null || go.GetComponentInChildren<StationExtension>(true) != null
                || go.GetComponentInChildren<Ship>(true) != null || go.GetComponentInChildren<Vagon>(true) != null || go.GetComponentInChildren<ArmorStand>(true) != null;
        }
    }
}
