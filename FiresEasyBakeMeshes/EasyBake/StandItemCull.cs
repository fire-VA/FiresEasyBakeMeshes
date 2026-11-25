using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.300 ([lead] / Fire 11-11: "see if there's any way to optimize item stands"). Town census: the HUNG ITEMS on item stands are the biggest group
    // Unity still draws itself (itemstandh 179 + itemstand 153 renderers in view). EBM already draws the stand's own base (coverage); the hung item is
    // vanilla's: ItemStand.SetVisualItem instantiates the item's "attach" child under the stand and then leaves it static (UpdateVisual every 4 s returns at
    // once while the item, variant and orientation are unchanged).
    //   [Optimize] StandItemCull (OFF until gate ABBA) + StandItemDrawM: past that distance the hung item's mesh renderers are switched off; they come back
    //     at the distance (2 m hysteresis). Look change: items appear as you come closer.
    //   [Optimize] StandItemShadowCut (OFF until gate ABBA) + StandItemShadowM: past that distance the hung item casts no shadow (restored nearer).
    // Only MeshRenderer / SkinnedMeshRenderer under ItemStand.m_visualItem are touched (never the stand, never a ParticleSystemRenderer: a particle
    // effect keeps simulating and drawing, so nothing restarts). Only what this switched is put back: a renderer that was already off, or had no shadow,
    // is never touched. An eighth of the stands per frame, no allocations on the frame path.
    internal static class StandItemCull
    {
        internal static ConfigEntry<bool> CullOn, ShadowOn;
        internal static ConfigEntry<float> DrawM, ShadowM;

        internal static void Bind(ConfigFile config)
        {
            CullOn = EbmConfigMove.Bind(config, "22 - Performance - Grass, Stands And Signs", "StandItemCull", false /* OFF until gate ABBA (Fire's hard rule) */,
                "Items hung on item stands farther away than StandItemDrawM are not drawn; they appear as you come closer. The stand itself always shows. Off = as before.");
            DrawM = EbmConfigMove.Bind(config, "22 - Performance - Grass, Stands And Signs", "StandItemDrawM", 25f, new ConfigDescription("Hung items closer than this many metres are always drawn.", new AcceptableValueRange<float>(5f, 200f)));
            ShadowOn = EbmConfigMove.Bind(config, "22 - Performance - Grass, Stands And Signs", "StandItemShadowCut", false /* OFF until gate ABBA */,
                "Items hung on item stands farther away than StandItemShadowM cast no shadow. Off = as before.");
            ShadowM = EbmConfigMove.Bind(config, "22 - Performance - Grass, Stands And Signs", "StandItemShadowM", 12f, new ConfigDescription("Hung items closer than this many metres keep their shadow.", new AcceptableValueRange<float>(2f, 200f)));
            CullOn.SettingChanged += (_, __) => { if (!CullOn.Value) RestoreAll(cull: true, shadow: false); };
            ShadowOn.SettingChanged += (_, __) => { if (!ShadowOn.Value) RestoreAll(cull: false, shadow: true); };
        }

        private const float HysteresisM = 2f;
        private const int Slices = 8;
        private static readonly AccessTools.FieldRef<ItemStand, GameObject> s_visual = AccessTools.FieldRefAccess<ItemStand, GameObject>("m_visualItem");

        private sealed class Entry
        {
            public ItemStand Stand; public GameObject Item;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<ShadowCastingMode> Shadows = new List<ShadowCastingMode>();
            public bool Hidden, NoShadow;
        }
        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly List<Renderer> s_scratch = new List<Renderer>(16);
        private static int s_cursor, c_hid, c_shown;
        private static float s_nextReport;

        [HarmonyPatch(typeof(ItemStand), "Awake")]
        private static class StandAwake
        {
            private static void Postfix(ItemStand __instance)
            {
                try { if (__instance != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) s_entries.Add(new Entry { Stand = __instance }); }
                catch (System.Exception e) { EasyBakeLog.Debug("[StandItems] register skipped: " + e.Message); }
            }
        }

        // the hung item changed (new item, removed, re-oriented into a new object): give back what was switched on the old one, list the new one
        private static void Relist(Entry e, GameObject item)
        {
            Restore(e, cull: true, shadow: true);
            e.Renderers.Clear(); e.Shadows.Clear(); e.Item = item;
            if (item == null) return;
            s_scratch.Clear(); item.GetComponentsInChildren(true, s_scratch);
            foreach (var r in s_scratch)
                if (r is MeshRenderer || r is SkinnedMeshRenderer) { e.Renderers.Add(r); e.Shadows.Add(r.shadowCastingMode); }
        }

        private static void Restore(Entry e, bool cull, bool shadow)
        {
            for (int i = 0; i < e.Renderers.Count; i++)
            {
                var r = e.Renderers[i];
                if (r == null) continue;
                if (cull && e.Hidden) r.enabled = true;
                if (shadow && e.NoShadow) r.shadowCastingMode = e.Shadows[i];
            }
            if (cull) e.Hidden = false;
            if (shadow) e.NoShadow = false;
        }

        private static void RestoreAll(bool cull, bool shadow)
        {
            foreach (var e in s_entries) Restore(e, cull, shadow);
        }

        // 1.2.300 ([lead]: research before any hung-item shader work): "ebm_standitems" lists every material on the hung items of the known stands:
        // shader, enabled keywords, render queue, renderer kind (mesh / skinned / particle), how many renderers, and whether EBM has a variant for it today
        internal static string Report()
        {
            var rows = new Dictionary<string, int[]>();   // key -> mesh, skinned, particle, inView
            var cam = Utils.GetMainCamera();
            Plane[] planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            int stands = 0, withItem = 0;
            foreach (var e in s_entries)
            {
                if (e.Stand == null) continue;
                stands++;
                var item = s_visual(e.Stand);
                if (item == null) continue;
                withItem++;
                s_scratch.Clear(); item.GetComponentsInChildren(true, s_scratch);
                foreach (var r in s_scratch)
                {
                    if (r == null) continue;
                    int kind = r is SkinnedMeshRenderer ? 1 : r is ParticleSystemRenderer ? 2 : 0;
                    bool inView = planes != null && GeometryUtility.TestPlanesAABB(planes, r.bounds);
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string sh = m.shader != null ? m.shader.name : "?";
                        bool variant = Indirect.EbmIndirectAssets.VariantFor(m) != null;
                        string key = $"{sh} | queue {m.renderQueue} | keywords [{string.Join(" ", m.shaderKeywords)}] | EBM variant {(variant ? "YES" : "no")}";
                        if (!rows.TryGetValue(key, out var c)) rows[key] = c = new int[4];
                        c[kind]++; if (inView) c[3]++;
                    }
                }
            }
            var list = new List<KeyValuePair<string, int[]>>(rows);
            list.Sort((a, b) => (b.Value[0] + b.Value[1] + b.Value[2]).CompareTo(a.Value[0] + a.Value[1] + a.Value[2]));
            var sb = new System.Text.StringBuilder();
            sb.Append($"[StandItems] {stands} stand(s) known, {withItem} with an item; materials on the hung items:\n");
            foreach (var kv in list) sb.Append($"[StandItems] mesh {kv.Value[0]}, skinned {kv.Value[1]}, particle {kv.Value[2]} ({kv.Value[3]} in view): {kv.Key}\n");
            return sb.ToString().TrimEnd('\n');
        }

        // EBM's LateUpdate
        // 1.2.312 ([lead]: a rig leg could not prove these acted, 0 [StandItems] lines at Info): one Info line per state change of each key
        private static int s_saidCull = -1, s_saidShadow = -1;
        internal static void Tick()
        {
            bool cullOn = CullOn != null && CullOn.Value, shadowOn = ShadowOn != null && ShadowOn.Value;
            if ((cullOn ? 1 : 0) != s_saidCull) { s_saidCull = cullOn ? 1 : 0; EasyBakeLog.Info($"[StandItems] StandItemCull {(cullOn ? "on: hung items past " + (DrawM != null ? DrawM.Value : 0f).ToString("0") + " m are not drawn" : "off")}"); }
            if ((shadowOn ? 1 : 0) != s_saidShadow) { s_saidShadow = shadowOn ? 1 : 0; EasyBakeLog.Info($"[StandItems] StandItemShadowCut {(shadowOn ? "on: hung items past " + (ShadowM != null ? ShadowM.Value : 0f).ToString("0") + " m cast no shadow" : "off")}"); }
            if (s_entries.Count == 0) return;
            if (!cullOn && !shadowOn) return;
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            float show = DrawM.Value, hide = show + HysteresisM, shadowIn = ShadowM.Value, shadowOut = shadowIn + HysteresisM;
            float show2 = show * show, hide2 = hide * hide, sIn2 = shadowIn * shadowIn, sOut2 = shadowOut * shadowOut;
            int n = s_entries.Count, step = (n + Slices - 1) / Slices;
            if (s_cursor >= n) s_cursor = 0;
            int end = Mathf.Min(n, s_cursor + step);
            for (int i = s_cursor; i < end; i++)
            {
                var e = s_entries[i];
                if (e.Stand == null) { s_entries[i] = s_entries[n - 1]; s_entries.RemoveAt(n - 1); n--; end = Mathf.Min(end, n); i--; continue; }
                var item = s_visual(e.Stand);
                if (item != e.Item) Relist(e, item);
                if (e.Renderers.Count == 0) continue;
                float d2 = (e.Stand.transform.position - eye).sqrMagnitude;
                if (cullOn)
                {
                    if (e.Hidden && d2 <= show2) { Restore(e, cull: true, shadow: false); c_shown++; }
                    else if (!e.Hidden && d2 > hide2)
                    {
                        bool any = false;
                        for (int k = 0; k < e.Renderers.Count; k++) { var r = e.Renderers[k]; if (r != null && r.enabled) { any = true; break; } }
                        if (any)   // switch only what is on now; an item someone else hid stays theirs
                        {
                            for (int k = 0; k < e.Renderers.Count; k++) { var r = e.Renderers[k]; if (r != null && !r.enabled) e.Renderers[k] = null; }   // never "restore" one that was off
                            for (int k = 0; k < e.Renderers.Count; k++) { var r = e.Renderers[k]; if (r != null) r.enabled = false; }
                            e.Hidden = true; c_hid++;
                        }
                    }
                }
                if (shadowOn)
                {
                    if (e.NoShadow && d2 <= sIn2) Restore(e, cull: false, shadow: true);
                    else if (!e.NoShadow && d2 > sOut2)
                    {
                        for (int k = 0; k < e.Renderers.Count; k++)
                        {
                            var r = e.Renderers[k]; if (r == null) continue;
                            e.Shadows[k] = r.shadowCastingMode;   // what it is right now is what comes back
                            if (r.shadowCastingMode != ShadowCastingMode.Off) r.shadowCastingMode = ShadowCastingMode.Off;
                        }
                        e.NoShadow = true;
                    }
                }
            }
            s_cursor = end >= n ? 0 : end;
            float t = Time.realtimeSinceStartup;
            if (t >= s_nextReport)
            {
                s_nextReport = t + 30f;
                {   // 1.2.312: at Info while either key is on (this point is reached only then), so a rig leg proves it acted
                    int hidden = 0, noShadow = 0;
                    foreach (var e in s_entries) { if (e.Hidden) hidden++; if (e.NoShadow) noShadow++; }
                    EasyBakeLog.Info($"[StandItems] 30 s: {s_entries.Count} stand(s) known; hung item hidden on {hidden} (past {DrawM.Value:0} m, cull {(cullOn ? "on" : "off")}), shadow cut on {noShadow} (past {ShadowM.Value:0} m, {(shadowOn ? "on" : "off")}); {c_hid} hidden / {c_shown} shown this window");
                }
                c_hid = c_shown = 0;
            }
        }
    }
}
