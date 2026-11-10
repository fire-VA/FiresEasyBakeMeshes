using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.234 ([visual], lead: the stretch is NEAR and intermittent by load order; [Integrity] only sees the indirect buffers): every mesh EBM
    // BUILDS (zone combines, cache rebuilds, simplified LODs, rock copies) is checked once, at build: a 16-bit index format over 65,535 vertices
    // (indices wrap), any index at or past the vertex count, or a non-finite / absurd vertex. A failing mesh is logged as an Error (where, zone,
    // name, the first bad value) and the caller does not draw it. Bake-time only: never per frame.
    internal static class EbmMeshCheck
    {
        private static readonly List<int> s_idx = new List<int>(65536);
        private static readonly List<Vector3> s_pos = new List<Vector3>(65536);
        private static int s_bad; private static bool s_said;
        internal static int Checked, Bad, Wide; private static int s_saidChecked; private static float s_sayAt;

        // an Info line (proof) whenever the counts moved, at most once a minute
        private static void SayCounts()
        {
            if (Time.unscaledTime < s_sayAt || Checked == s_saidChecked) return;
            s_sayAt = Time.unscaledTime + 60f; s_saidChecked = Checked;
            EasyBakeLog.Info($"[MeshCheck] {Checked} baked mesh(es) checked so far: {Wide} with 32-bit indices, {Bad} bad (not drawn)");
        }

        internal static bool Sane(Mesh m, string where)
        {
            if (m == null) return true;
            Checked++;
            string why = null;
            int nv = m.vertexCount;
            bool wide = m.indexFormat == IndexFormat.UInt32; if (wide) Wide++;
            EasyBakeLog.Debug($"[MeshCheck] {where} '{m.name}': {nv} vertices, {(wide ? "32" : "16")}-bit indices, {m.subMeshCount} submesh(es)");   // lead: every baked mesh, for the starburst hunt
            SayCounts();
            if (m.indexFormat == IndexFormat.UInt16 && nv > 65535) why = $"16-bit indices with {nv} vertices (indices wrap)";
            if (why == null && m.isReadable)
            {
                for (int s = 0; s < m.subMeshCount && why == null; s++)
                {
                    s_idx.Clear(); m.GetIndices(s_idx, s, true);
                    for (int i = 0; i < s_idx.Count; i++) if ((uint)s_idx[i] >= (uint)nv) { why = $"submesh {s} index {i} = {s_idx[i]} past the {nv} vertices"; break; }
                }
                if (why == null)
                {
                    s_pos.Clear(); m.GetVertices(s_pos);
                    for (int i = 0; i < s_pos.Count; i++) { var p = s_pos[i]; float t = p.x + p.y + p.z; if (float.IsNaN(t) || float.IsInfinity(t) || p.sqrMagnitude > 1e12f) { why = $"vertex {i} at {p}"; break; } }
                }
                s_idx.Clear(); s_pos.Clear();
            }
            if (why == null) return true;
            Bad++; s_bad++;
            if (!s_said) { s_said = true; EasyBakeLog.Error($"[MeshCheck] BAD MESH at frame {Time.frameCount} ({where}, '{m.name}'): {why}. It is not drawn. Later bad meshes are logged at Debug."); }
            else EasyBakeLog.Debug($"[MeshCheck] bad mesh #{s_bad} ({where}, '{m.name}'): {why}");
            return false;
        }
    }
}
