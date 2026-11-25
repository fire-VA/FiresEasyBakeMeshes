using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.333 ([visual], Tools\DESIGN_ebm_ashlands_cascade_casters_20261112.md; [ghost] / [lead]: the capture cannot say which sun casters have
    // coarser LOD levels, the indirect draws carry no prefab identity): every 30 s, with Debug logging AND [Diagnostics] LodLevelCensus on
    // (default off: nothing runs), one walk of EBM's baked groups and covered objects that CAST shadows, counted by how many LOD levels their
    // prefab has (1 / 2 / 3+) and by camera distance (<64, 64-128, 128-192, 192+ m), instances and near-mesh triangles x instances; plus the
    // top 10 single-LOD meshes by triangles x instances (the coarse-caster candidates). Read-only; a throw is one Debug line, never Update.
    internal static class EbmLodLevelCensus
    {
        private static float s_next;
        private static bool On => FiresEasyBakeMeshesPlugin.LodLevelCensus != null && FiresEasyBakeMeshesPlugin.LodLevelCensus.Value;
        private static readonly long[,] s_inst = new long[2, 12], s_tris = new long[2, 12], s_tris1 = new long[2, 12];   // [baked 0 / covered 1, levels(0..2) x 4 + band]; s_tris1 = the LAST LOD level's triangles (1.2.337: what far cascades would cast from LOD1)
        // 1.2.337 (lead: "'default' is a mesh NAME shared by several prefabs"): the top list keys by mesh instance id and prints "mesh (prefab)"
        private static readonly Dictionary<int, long> s_single = new Dictionary<int, long>();
        private static readonly Dictionary<int, string> s_singleName = new Dictionary<int, string>();
        private static readonly List<KeyValuePair<int, long>> s_sort = new List<KeyValuePair<int, long>>();
        private static string PrefabName(int hash) { var p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null; return p != null ? p.name : hash.ToString(); }
        private static readonly Dictionary<int, string> s_singlePrefab = new Dictionary<int, string>();   // the first prefab seen per mesh; a second one adds "+1" ([generator]'s review)
        private static void NoteSingle(Mesh m, string prefab, long tris)
        {
            if (m == null) return;
            int id = m.GetInstanceID(); s_single.TryGetValue(id, out long v); s_single[id] = v + tris;
            if (!s_singlePrefab.TryGetValue(id, out var first)) { s_singlePrefab[id] = prefab; s_singleName[id] = m.name + " (" + prefab + ")"; }
            else if (first != prefab && !s_singleName[id].EndsWith("+)")) s_singleName[id] = m.name + " (" + first + " +)";   // shared by more prefabs than the one named
        }
        private static Vector3 s_cam;

        internal static void Tick()
        {
            if (!On || !EasyBakeLog.DebugOn()) return;
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 30f;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            try { s_cam = cam.transform.position; Walk(); } catch (System.Exception e) { EasyBakeLog.Debug("[LodCensus] skipped: " + e.Message); }
        }

        private static int Band(Vector3 p) { float d = Vector3.Distance(p, s_cam); return d < 64f ? 0 : d < 128f ? 1 : d < 192f ? 2 : 3; }
        private static int LevelsIdx(int levels) => levels <= 1 ? 0 : levels == 2 ? 1 : 2;
        private static long Tris(Mesh m) { if (m == null) return 0; long t = 0; for (int s = 0; s < m.subMeshCount; s++) t += m.GetIndexCount(s) / 3; return t; }

        private static void Walk()
        {
            System.Array.Clear(s_inst, 0, s_inst.Length); System.Array.Clear(s_tris, 0, s_tris.Length); System.Array.Clear(s_tris1, 0, s_tris1.Length); s_single.Clear(); s_singleName.Clear(); s_singlePrefab.Clear();
            ZoneTracker.ForEachGroup(g =>
            {
                var def = g.Definition; int n = g.Matrices.Count;
                if (def == null || def.Parts == null || n == 0) return;
                int levels = 1; long tris = 0, trisLast = 0; bool casts = false;
                foreach (var p in def.Parts)
                {
                    if (p == null) continue;
                    if (p.LodMeshes != null && p.LodMeshes.Length > levels) levels = p.LodMeshes.Length;
                    if (p.RenderParams.shadowCastingMode != ShadowCastingMode.Off) casts = true;
                    tris += Tris(p.NearMesh);
                    trisLast += p.LodMeshes != null && p.LodMeshes.Length > 1 ? Tris(p.LodMeshes[p.LodMeshes.Length - 1]) : Tris(p.NearMesh);
                }
                if (!casts) return;
                int k = LevelsIdx(levels) * 4 + Band(g.WorldBounds.center);
                s_inst[0, k] += n; s_tris[0, k] += tris * n; s_tris1[0, k] += trisLast * n;
                if (levels <= 1) NoteSingle(def.Parts[0]?.NearMesh, PrefabName(g.PrefabHash), tris * n);
            });
            foreach (var co in Coverage.CoveredObject.Live)
            {
                if (co == null || !co.IsCovering) continue;
                var r = co.GetComponentInChildren<Renderer>();
                if (r == null || r.shadowCastingMode == ShadowCastingMode.Off) continue;
                var lg = co.GetComponentInChildren<LODGroup>();
                int levels = lg != null ? lg.lodCount : 1;
                var mf = r.GetComponent<MeshFilter>();
                long tris = mf != null ? Tris(mf.sharedMesh) : 0, trisLast = tris;
                if (lg != null && levels > 1)
                {
                    var lods = lg.GetLODs(); var last = lods[lods.Length - 1];
                    trisLast = 0;
                    if (last.renderers != null) foreach (var lr in last.renderers) { var lmf = lr != null ? lr.GetComponent<MeshFilter>() : null; if (lmf != null) trisLast += Tris(lmf.sharedMesh); }
                }
                int k = LevelsIdx(levels) * 4 + Band(co.transform.position);
                s_inst[1, k] += 1; s_tris[1, k] += tris; s_tris1[1, k] += trisLast;
                if (levels <= 1 && mf != null && mf.sharedMesh != null) NoteSingle(mf.sharedMesh, co.Verdict != null ? co.Verdict.Name : co.gameObject.name, tris);
            }
            var sb = new System.Text.StringBuilder("[LodCensus] sun-casting instances by LOD levels x distance (inst / near-mesh triangles / last-level triangles):");
            string[] src = { "baked", "covered" }, lv = { "1 level", "2 levels", "3+ levels" }, bd = { "<64", "64-128", "128-192", "192+" };
            for (int s = 0; s < 2; s++)
                for (int l = 0; l < 3; l++)
                {
                    sb.Append("\n  ").Append(src[s]).Append(' ').Append(lv[l]).Append(':');
                    for (int b = 0; b < 4; b++) sb.Append(' ').Append(bd[b]).Append(' ').Append(s_inst[s, l * 4 + b]).Append(" / ").Append(s_tris[s, l * 4 + b]).Append(" / ").Append(s_tris1[s, l * 4 + b]).Append(';');
                }
            s_sort.Clear(); s_sort.AddRange(s_single); s_sort.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.Append("\n  top single-LOD meshes by triangles x instances (mesh (prefab)):");
            for (int i = 0; i < s_sort.Count && i < 10; i++) sb.Append(' ').Append(s_singleName.TryGetValue(s_sort[i].Key, out var nm) ? nm : s_sort[i].Key.ToString()).Append(' ').Append(s_sort[i].Value).Append(';');
            EasyBakeLog.Debug(sb.ToString());
        }
    }
}
