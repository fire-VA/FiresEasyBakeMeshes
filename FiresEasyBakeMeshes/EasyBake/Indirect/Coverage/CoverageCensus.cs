using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.20x: "ebm_coverage [radius]" (read-only). What decides which procedural shader variant buys the most draws:
    /// live pickables / trees / destructibles / rocks within the radius, by class and by shader name - renderers (active + enabled),
    /// in the camera frustum, distinct materials, under a LODGroup, Accepts() - plus what the coverage hands over right now and why
    /// the rest stays vanilla. Objects SkipVegetation leaves uncreated are EBM groups and are not counted here (ebm_census has them).
    /// </summary>
    internal static class CoverageCensus
    {
        private sealed class Row { public int Objects, Renderers, InView, UnderLod, Covered; public HashSet<Material> Materials = new HashSet<Material>(); public bool Accepted; }

        private static readonly Plane[] s_planes = new Plane[6];
        private static readonly List<MeshRenderer> s_renderers = new List<MeshRenderer>(16);
        private static readonly List<Material> s_materials = new List<Material>(4);

        internal static string Run(float radius)
        {
            var instances = ZoneTracker.SceneInstances();
            var cam = Utils.GetMainCamera();
            if (instances == null || cam == null) return "[Coverage] no world or camera";
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            Vector3 cp = cam.transform.position;
            float r2 = radius * radius;

            var byKind = new Dictionary<CoverageRules.Kind, Row>();
            var byShader = new Dictionary<string, Row>();
            var objRefusals = new Dictionary<string, int>();
            int sticky = 0;

            foreach (var kv in instances)
            {
                var view = kv.Value;
                if (view == null || kv.Key == null) continue;
                var go = view.gameObject;
                if ((go.transform.position - cp).sqrMagnitude > r2) continue;
                var kind = CoverageRules.KindOf(go);
                if (kind == CoverageRules.Kind.None) continue;
                var v = CoverageRules.For(kv.Key.GetPrefab(), go);
                if (v.Refusal != null) { objRefusals.TryGetValue(v.Refusal, out int n); objRefusals[v.Refusal] = n + 1; }
                var co = go.GetComponent<CoveredObject>();
                if (co != null && co.StickyReason != null) sticky++;

                if (!byKind.TryGetValue(kind, out var kr)) byKind[kind] = kr = new Row();
                kr.Objects++;
                go.GetComponentsInChildren(false, s_renderers);
                foreach (var r in s_renderers)
                {
                    if (r == null) continue;
                    if (!r.enabled && !(co != null && co.Covers(r))) continue;   // disabled by someone else (ours are counted: disable mode)
                    bool covered = co != null && co.Covers(r);
                    if (r.forceRenderingOff && !covered) continue;   // hidden by someone else
                    bool inView = GeometryUtility.TestPlanesAABB(s_planes, r.bounds);
                    bool underLod = r.GetComponentInParent<LODGroup>() != null;
                    kr.Renderers++; if (inView) kr.InView++; if (underLod) kr.UnderLod++; if (covered) kr.Covered++;
                    r.GetSharedMaterials(s_materials);
                    foreach (var m in s_materials)
                    {
                        if (m == null) continue;
                        string sh = m.shader != null ? m.shader.name : "?";
                        if (!byShader.TryGetValue(sh, out var sr)) { byShader[sh] = sr = new Row(); sr.Accepted = IndirectBatches.Accepts(m); }
                        sr.Renderers++; if (inView) sr.InView++; if (covered) sr.Covered++;
                        sr.Materials.Add(m);
                        kr.Materials.Add(m);
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append($"[Coverage] within {radius:0} m: indirect batches {(IndirectBatches.Active ? "ACTIVE" : "off")}; handed over now: {CoveredObject.CoveredObjects} object(s), {CoveredObject.CoveredRenderers} renderer(s), {CoveredObject.Handles} batch instance(s); {sticky} object(s) handed back for good; hidden by {(CoveredObject.DisableMode ? "enabled = false" : "forceRenderingOff")} ({CoveredObject.DisabledNow} disabled now), {CoveredObject.ContestedObjects} object(s) handed back because something drew a covered part\n");
            foreach (var k in byKind)
                sb.Append($"[Coverage] class {k.Key}: {k.Value.Objects} object(s), {k.Value.Renderers} renderer(s) ({k.Value.InView} in view, {k.Value.UnderLod} under a LODGroup), {k.Value.Materials.Count} material(s), {k.Value.Covered} drawn by the batches\n");
            var shaders = new List<KeyValuePair<string, Row>>(byShader);
            shaders.Sort((a, b) => b.Value.InView.CompareTo(a.Value.InView));
            foreach (var s in shaders)
                sb.Append($"[Coverage] shader {s.Key}: {s.Value.Renderers} renderer-material(s) ({s.Value.InView} in view), {s.Value.Materials.Count} distinct material(s), variant {(s.Value.Accepted ? "YES" : "no")}, {s.Value.Covered} drawn by the batches\n");
            AppendGroupShaders(sb, cp, r2);
            foreach (var o in objRefusals) sb.Append($"[Coverage] objects left vanilla: {o.Value} x {o.Key}\n");
            // if one common property (wet, ripple, value noise) shows up here with a big count, coverage is collapsing on it: look first
            foreach (var p in CoveredObject.HandBacksByProperty) sb.Append($"[Coverage] handed back for good by a MaterialMan property: {p.Value} object(s) x property id {p.Key}\n");
            foreach (var o in CoveredObject.RendererRefusals) sb.Append($"[Coverage] renderer refusals since load: {o.Value} x {o.Key}\n");
            return sb.ToString().TrimEnd('\n');
        }

        // EBM's baked instance groups within the radius, by the shader their parts draw with (what the indirect path's variants must
        // cover: Custom/FiresPiece, Vedr/Piece, Custom/Piece, Custom/Vegetation, ...). ZoneTracker keeps its zones private, so this
        // read-only diagnostic reaches them by reflection: ZoneTracker._zones -> ZoneState.Bake -> BakeResult.InstanceGroups.
        private static System.Reflection.FieldInfo f_zones, f_bake, f_groups;

        private static void AppendGroupShaders(StringBuilder sb, Vector3 cp, float r2)
        {
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            try
            {
                if (f_zones == null) f_zones = typeof(ZoneTracker).GetField("_zones", F);
                if (!(f_zones?.GetValue(null) is System.Collections.IDictionary zones)) { sb.Append("[Coverage] baked groups: zone table not found\n"); return; }
                var byShader = new Dictionary<string, int[]>();   // shader -> groups, instances, parts
                int groups = 0;
                foreach (var state in zones.Values)
                {
                    if (state == null) continue;
                    if (f_bake == null) f_bake = state.GetType().GetField("Bake", F);
                    var bake = f_bake?.GetValue(state);
                    if (bake == null) continue;
                    if (f_groups == null) f_groups = bake.GetType().GetField("InstanceGroups", F);
                    if (!(f_groups?.GetValue(bake) is List<ZoneInstanceGroup> list)) continue;
                    foreach (var g in list)
                    {
                        if (g == null || g.Count == 0 || g.Definition?.Parts == null) continue;
                        if ((g.WorldBounds.center - cp).sqrMagnitude > r2) continue;
                        groups++;
                        var seen = new HashSet<string>();
                        foreach (var part in g.Definition.Parts)
                        {
                            var m = part?.RenderParams.material;
                            string sh = m != null && m.shader != null ? m.shader.name : "?";
                            if (!byShader.TryGetValue(sh, out var row)) byShader[sh] = row = new int[3];
                            row[2]++;
                            if (seen.Add(sh)) { row[0]++; row[1] += g.Count; }
                        }
                    }
                }
                sb.Append($"[Coverage] baked groups within the radius: {groups}\n");
                var rows = new List<KeyValuePair<string, int[]>>(byShader);
                rows.Sort((a, b) => b.Value[1].CompareTo(a.Value[1]));
                foreach (var row in rows)
                    sb.Append($"[Coverage] baked group shader {row.Key}: {row.Value[0]} group(s), {row.Value[1]} instance(s), {row.Value[2]} part(s)\n");
            }
            catch (System.Exception e) { sb.Append($"[Coverage] baked groups: read failed ({e.Message})\n"); }
        }
    }
}
