using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.126 ([visual], lead/Fire 10-26: "getting wet inside due to ebm not blocking wind and rain"): read-only Debug line at the
    // local player every 10 s while it rains. It casts the same rays as vanilla Cover (GetCoverForPoint's fan, IsUnderRoof's sphere
    // up, same layer mask) and names what each hit is: a live piece collider, or an EBM stand-in. Nothing is changed.
    internal static class ShelterProbe
    {
        private static float s_next;
        private static bool s_ran;
        private static string s_idle = "";
        private static int s_mask = -1;
        private static Vector3[] s_rays;

        internal static void Tick()
        {
            if (!s_ran) { s_ran = true; EasyBakeLog.Debug("[EBM] shelter probe executing (first tick): Debug line every 10 s while it rains"); }
            if (Time.time < s_next) return;
            s_next = Time.time + 10f;
            // 1.2.254 [Optimize] TickGroupEventDriven: everything below only writes Debug lines; with Debug off its raycasts have no reader
            if (EbmSelfCost.EventDriven && !EasyBakeLog.DebugOn()) { EbmSelfCost.Sub(EbmSelfCost.SubProbe, false); return; }
            EbmSelfCost.Sub(EbmSelfCost.SubProbe, true);
            var player = Player.m_localPlayer;
            string why = player == null ? "no local player" : EnvMan.instance == null ? "no EnvMan" : !EnvMan.IsWet() ? "not raining here (EnvMan.IsWet false)" : null;
            if (why != s_idle) { s_idle = why; if (why != null) EasyBakeLog.Debug("[EBM] shelter probe idle: " + why); }
            if (why != null) return;
            try { Log(player); }
            catch (System.Exception ex) { EasyBakeLog.Debug("[EBM] shelter probe failed: " + ex.Message); }
        }

        private static void Log(Player player)
        {
            if (s_mask == -1) s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            Vector3 centre = player.GetCenterPoint();
            Cover.GetCoverForPoint(centre, out float cover, out bool underRoof);
            if (s_rays == null) s_rays = Traverse.Create(typeof(Cover)).Field("m_coverRays").GetValue<Vector3[]>();

            int blocked = 0, standIn = 0, live = 0, n = s_rays != null ? s_rays.Length : 0;
            var byRoot = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                Vector3 dir = s_rays[i];
                if (!Physics.Raycast(centre + dir * 0.5f, dir, out RaycastHit hit, 29.5f, s_mask)) continue;
                blocked++;
                bool si = IsStandIn(hit.collider);
                if (si) standIn++; else live++;
                string key = $"{(si ? "stand-in" : "live")} {hit.collider.transform.root.name.Replace("(Clone)", "")} [{LayerMask.LayerToName(hit.collider.gameObject.layer)}]";
                byRoot[key] = byRoot.TryGetValue(key, out int k) ? k + 1 : 1;
            }
            var rayHits = new StringBuilder();
            foreach (var kv in byRoot) if (rayHits.Length < 500) rayHits.Append(rayHits.Length > 0 ? ", " : "").Append(kv.Key).Append(" x").Append(kv.Value);

            var hits = new StringBuilder();
            if (Physics.SphereCast(centre, 0.1f, Vector3.up, out RaycastHit roof, 100f, s_mask))
                hits.Append(Describe(roof.collider)).Append(" at ").Append(roof.distance.ToString("0.0")).Append(" m");
            else hits.Append("nothing above");

            bool wetSe = player.GetSEMan() != null && player.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectWet);
            EasyBakeLog.Debug($"[EBM] shelter probe: cover {blocked}/{n} rays ({cover:0.00}; {live} live piece, {standIn} EBM stand-in), " +
                              $"roof {(underRoof ? "yes" : "no")}, InShelter {(player.InShelter() ? "yes" : "no")}, wet SE {(wetSe ? "yes" : "no")}; up: {hits}; ray hits: {(rayHits.Length > 0 ? rayHits.ToString() : "none")}");
            EasyBakeLog.Debug("[EBM] shelter probe " + ShelterRain.Summary());
            EasyBakeLog.Debug("[EBM] wet probe: " + WetUnderfoot(centre));
        }

        // 1.2.128: what decides the floor's wet look right here: the global _Wet, the floor's shader, whether its material declares
        // _Wet, and the renderer's MaterialPropertyBlock value (a live piece) or "baked" (EBM draws it, no renderer).
        private static readonly int s_wetId = Shader.PropertyToID("_Wet");
        private static readonly MaterialPropertyBlock s_block = new MaterialPropertyBlock();

        private static string WetUnderfoot(Vector3 centre)
        {
            string global = Shader.GetGlobalFloat(s_wetId).ToString("0.00");
            if (!Physics.Raycast(centre, Vector3.down, out RaycastHit hit, 4f, s_mask)) return $"global _Wet {global}; nothing underfoot";
            if (IsStandIn(hit.collider)) return $"global _Wet {global}; floor is an EBM stand-in ('{hit.collider.transform.root.name}'): drawn baked";
            var r = hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.transform.root.GetComponentInChildren<Renderer>();
            if (r == null) return $"global _Wet {global}; floor '{hit.collider.transform.root.name}' has no renderer";
            var m = r.sharedMaterial;
            string mpb = "none";
            if (r.HasPropertyBlock()) { r.GetPropertyBlock(s_block); mpb = s_block.HasFloat(s_wetId) ? s_block.GetFloat(s_wetId).ToString("0.00") : "no _Wet"; }
            return $"global _Wet {global}; floor '{r.transform.root.name}/{r.name}' enabled {r.enabled}, shader '{(m != null && m.shader != null ? m.shader.name : "null")}', " +
                   $"material declares _Wet {(m != null && m.HasProperty(s_wetId) ? "yes " + m.GetFloat(s_wetId).ToString("0.00") : "no")}, MPB {mpb}";
        }

        private static bool IsStandIn(Collider c)
        {
            for (var t = c.transform; t != null; t = t.parent)
                if (t.name.StartsWith("StandIn") || t.name.StartsWith("[EasyBake/")) return true;
            return false;
        }

        private static string Describe(Collider c)
        {
            var root = c.transform.root;
            return $"'{c.name}' on '{root.name}' layer {LayerMask.LayerToName(c.gameObject.layer)}{(c.CompareTag("leaky") ? " (leaky)" : "")}{(IsStandIn(c) ? " [EBM stand-in]" : "")}";
        }
    }
}
