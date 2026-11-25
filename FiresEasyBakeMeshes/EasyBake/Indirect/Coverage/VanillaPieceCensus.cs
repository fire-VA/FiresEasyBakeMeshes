using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// 1.2.296 ([lead] 11-11, town capture frame3434: Custom/Piece = 816 draws EBM does not take, 14 % of the GPU frame): "ebm_vanillapieces [radius]"
    /// (read-only, one shot). Every live building piece within the radius whose renderers Unity still draws itself (enabled, not forceRenderingOff,
    /// so neither baked nor handed to the batches), grouped by prefab: objects, renderers, renderers in the camera frustum, the shaders they use, and
    /// what about the prefab most likely keeps EBM off it (moving parts, animator, particles, lights, a LODGroup, several materials). Sorted by
    /// renderers in view, top 40. Written at Info: the player typed the command.
    /// </summary>
    internal static class VanillaPieceCensus
    {
        private sealed class Row
        {
            public int Objects, Renderers, InView;
            public readonly HashSet<string> Shaders = new HashSet<string>();
            public bool Animator, Particles, Lights, Lod, Rigidbody, MultiMat;
            // 1.2.301 ([lead] / [wishbone]: why coverage leaves them): per-object owner state, and the first object's renderers for the dump
            public int BakedByEbm, Covered, Plain;
            public string Dump;
        }

        private static readonly System.Type s_animatorType = System.Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
        private static readonly Plane[] s_planes = new Plane[6];
        private static readonly List<MeshRenderer> s_renderers = new List<MeshRenderer>(16);
        private static readonly List<Material> s_materials = new List<Material>(4);
        private static readonly List<MeshRenderer> s_all = new List<MeshRenderer>(16);

        internal static string Run(float radius)
        {
            var instances = ZoneTracker.SceneInstances();
            var cam = Utils.GetMainCamera();
            if (instances == null || cam == null) return "[VanillaPieces] no world or camera";
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            Vector3 cp = cam.transform.position;
            float r2 = radius * radius;
            var rows = new Dictionary<string, Row>();
            int pieces = 0, drawnPieces = 0, renderers = 0, inView = 0;

            foreach (var kv in instances)
            {
                var view = kv.Value;
                if (view == null || kv.Key == null) continue;
                var go = view.gameObject;
                if ((go.transform.position - cp).sqrMagnitude > r2) continue;
                if (go.GetComponent<Piece>() == null) continue;
                pieces++;
                go.GetComponentsInChildren(false, s_renderers);
                Row row = null;
                foreach (var r in s_renderers)
                {
                    if (r == null || !r.enabled || r.forceRenderingOff) continue;   // baked, handed over, or hidden by someone: not a Unity draw
                    if (row == null)
                    {
                        string name = Utils.GetPrefabName(go);
                        if (!rows.TryGetValue(name, out row))
                        {
                            rows[name] = row = new Row
                            {
                                Animator = s_animatorType != null && go.GetComponentInChildren(s_animatorType, true) != null,   // by name: the csproj has no AnimationModule reference
                                Particles = go.GetComponentInChildren<ParticleSystem>(true) != null,
                                Lights = go.GetComponentInChildren<Light>(true) != null,
                                Lod = go.GetComponentInChildren<LODGroup>(true) != null,
                                Rigidbody = go.GetComponentInChildren<Rigidbody>(true) != null,
                            };
                        }
                        row.Objects++; drawnPieces++;
                        var sup = go.GetComponent<EasyBakeSuppressedVisuals>();
                        var co = go.GetComponent<CoveredObject>();
                        if (sup != null) row.BakedByEbm++; else if (co != null) row.Covered++; else row.Plain++;
                        if (row.Dump == null)
                        {
                            var d = new StringBuilder();
                            go.GetComponentsInChildren(true, s_all);
                            foreach (var ar in s_all)
                            {
                                if (ar == null || d.Length > 700) continue;
                                var m = ar.sharedMaterial;
                                d.Append(d.Length > 0 ? "; " : "").Append(CoverageRules.PathOf(go.transform, ar.transform)).Append(" [")
                                 .Append(m != null && m.shader != null ? m.shader.name : "?").Append(ar.gameObject.activeInHierarchy ? ", active" : ", inactive")
                                 .Append(ar.enabled ? ", enabled" : ", off").Append(ar.forceRenderingOff ? ", forceOff" : "")
                                 .Append(co != null && co.Covers(ar) ? ", covered" : "").Append("]");
                            }
                            row.Dump = d.Length > 0 ? d.ToString() : "(no mesh renderers)";
                        }
                    }
                    row.Renderers++; renderers++;
                    if (GeometryUtility.TestPlanesAABB(s_planes, r.bounds)) { row.InView++; inView++; }
                    r.GetSharedMaterials(s_materials);
                    if (s_materials.Count > 1) row.MultiMat = true;
                    foreach (var m in s_materials) if (m != null && row.Shaders.Count < 4) row.Shaders.Add(m.shader != null ? m.shader.name : "?");
                }
            }

            var list = new List<KeyValuePair<string, Row>>(rows);
            list.Sort((a, b) => b.Value.InView.CompareTo(a.Value.InView));
            var sb = new StringBuilder();
            sb.Append($"[VanillaPieces] within {radius:0} m: {pieces} live piece(s), {drawnPieces} still drawn by Unity ({renderers} renderer(s), {inView} in view) in {rows.Count} prefab(s); top by renderers in view:\n");
            int shown = 0;
            foreach (var kv in list)
            {
                if (shown++ >= 40) break;
                var r = kv.Value;
                var flags = new StringBuilder();
                if (r.Animator) flags.Append(" animator"); if (r.Particles) flags.Append(" particles"); if (r.Lights) flags.Append(" light");
                if (r.Lod) flags.Append(" LODGroup"); if (r.Rigidbody) flags.Append(" rigidbody"); if (r.MultiMat) flags.Append(" several-materials");
                sb.Append($"[VanillaPieces] {kv.Key}: {r.Objects} object(s), {r.Renderers} renderer(s), {r.InView} in view; shaders {string.Join(", ", r.Shaders)}; has:{(flags.Length > 0 ? flags.ToString() : " nothing special")}; objects: baked by EBM (bake left these renderers live) {r.BakedByEbm}, covered (parts kept live) {r.Covered}, neither {r.Plain}\n");
                if (shown <= 12 && r.Dump != null) sb.Append($"[VanillaPieces]   {kv.Key} renderers (first object): {r.Dump}\n");
            }
            return sb.ToString().TrimEnd('\n');
        }
    }

    /// <summary>
    /// 1.2.300 ([lead]: what is Custom/Flow in town, before any flow work): "ebm_shaderuse <shader name>" (one shot, Info). Every active, enabled
    /// renderer whose material uses that shader (exact name), grouped by material: render queue, enabled keywords, renderer kind, how many, how many in
    /// the camera frustum, and the top owner objects (the root's prefab name). Zero cost until typed.
    /// </summary>
    internal static class ShaderUseCensus
    {
        internal static string Run(string shaderName)
        {
            var cam = Utils.GetMainCamera();
            Plane[] planes = cam != null ? GeometryUtility.CalculateFrustumPlanes(cam) : null;
            var rows = new Dictionary<string, int[]>();          // material key -> mesh, skinned, particle, other, inView
            var owners = new Dictionary<string, Dictionary<string, int>>();
            var mats = new List<Material>(4);
            int renderers = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled) continue;
                r.GetSharedMaterials(mats);
                bool counted = false;
                foreach (var m in mats)
                {
                    if (m == null || m.shader == null || m.shader.name != shaderName) continue;
                    string key = $"material '{m.name}' | queue {m.renderQueue} | keywords [{string.Join(" ", m.shaderKeywords)}]";
                    if (!rows.TryGetValue(key, out var c)) { rows[key] = c = new int[5]; owners[key] = new Dictionary<string, int>(); }
                    int kind = r is MeshRenderer ? 0 : r is SkinnedMeshRenderer ? 1 : r is ParticleSystemRenderer ? 2 : 3;
                    c[kind]++;
                    if (planes != null && GeometryUtility.TestPlanesAABB(planes, r.bounds)) c[4]++;
                    string owner = Utils.GetPrefabName(r.transform.root.gameObject);
                    owners[key].TryGetValue(owner, out int n); owners[key][owner] = n + 1;
                    if (!counted) { renderers++; counted = true; }
                }
            }
            var sb = new StringBuilder();
            sb.Append($"[ShaderUse] '{shaderName}': {renderers} enabled renderer(s) in {rows.Count} material(s)\n");
            var list = new List<KeyValuePair<string, int[]>>(rows);
            list.Sort((a, b) => (b.Value[0] + b.Value[1] + b.Value[2] + b.Value[3]).CompareTo(a.Value[0] + a.Value[1] + a.Value[2] + a.Value[3]));
            foreach (var kv in list)
            {
                var o = new List<KeyValuePair<string, int>>(owners[kv.Key]);
                o.Sort((a, b) => b.Value.CompareTo(a.Value));
                var top = new StringBuilder();
                for (int i = 0; i < o.Count && i < 5; i++) top.Append(i > 0 ? ", " : "").Append(o[i].Key).Append(' ').Append(o[i].Value);
                sb.Append($"[ShaderUse] mesh {kv.Value[0]}, skinned {kv.Value[1]}, particle {kv.Value[2]}, other {kv.Value[3]} ({kv.Value[4]} in view): {kv.Key}; owners: {top}\n");
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}