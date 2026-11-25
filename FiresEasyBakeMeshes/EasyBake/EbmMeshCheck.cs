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
        private const int ScratchStart = 65536;   // 1.2.392: the lists' start size (the two "new List" above)
        private static float s_lastUseAt; private static int s_trims;   // 1.2.392

        // 1.2.392, from the 60 s heap tick: the two scratch lists kept the size of the largest mesh ever checked (4 B an index, 12 B a vertex) for the
        // session. After a minute without a check they go back to their start size; the next large mesh grows them again.
        internal static void TrimIdle(float now)
        {
            if (now - s_lastUseAt < 60f || (s_idx.Capacity <= ScratchStart && s_pos.Capacity <= ScratchStart)) return;
            s_idx.Clear(); s_pos.Clear();
            s_idx.Capacity = ScratchStart; s_pos.Capacity = ScratchStart;
            s_trims++;
        }
        internal static long HeapBytes(out int trims) { trims = s_trims; return s_idx.Capacity * 4L + s_pos.Capacity * 12L; }

        // an Info line (proof) whenever the counts moved, at most once a minute
        private static void SayCounts()
        {
            if (Time.unscaledTime < s_sayAt || Checked == s_saidChecked) return;
            s_sayAt = Time.unscaledTime + 60f; s_saidChecked = Checked;
            EasyBakeLog.InfoFirst("meshcheck-count", $"[MeshCheck] {Checked} baked mesh(es) checked so far: {Wide} with 32-bit indices, {Bad} bad (not drawn)"
                + (EasyBakeLog.DebugOn() ? $"; NaN-risk scan (1.2.487, Debug): {NormalsBad} mesh(es) with bad normals, {TangentsBad} with bad tangents, {DegenerateMatrices} degenerate transform(s)" : ""));
        }

        // ---- 1.2.487 ([visual], [lead] GO 11-21; FF 0.1.182's guard caught a NaN in the reflection cube at arrival, 0.4 s after an EBM cache rebuild): normals and
        // tangents, which the check above never read. A NaN / Inf / zero-length normal or a NaN / Inf tangent makes that surface's lighting NaN in any camera
        // (the reflection camera writes it into the cube). Cache rebuilds restore both exactly as saved (MeshCacheStore.cs:1435-1436), so one bad combine
        // survives every reload. Debug only: counted and named, the mesh still draws (a repair comes only after a log line names the culprit).
        private static readonly List<Vector4> s_tan = new List<Vector4>(4096);
        internal static int NormalsBad, TangentsBad;
        private static void ScanNormals(Mesh m, string where)
        {
            int badN = 0, firstN = -1; Vector3 nv0 = Vector3.zero;
            s_pos.Clear(); m.GetNormals(s_pos);
            for (int i = 0; i < s_pos.Count; i++)
            {
                var n = s_pos[i]; float t = n.x + n.y + n.z;
                if (float.IsNaN(t) || float.IsInfinity(t) || n.sqrMagnitude < 1e-12f) { if (badN++ == 0) { firstN = i; nv0 = n; } }
            }
            int badT = 0, firstT = -1; Vector4 tv0 = Vector4.zero;
            s_tan.Clear(); m.GetTangents(s_tan);
            for (int i = 0; i < s_tan.Count; i++)
            {
                var tg = s_tan[i]; float t = tg.x + tg.y + tg.z + tg.w;
                if (float.IsNaN(t) || float.IsInfinity(t)) { if (badT++ == 0) { firstT = i; tv0 = tg; } }
            }
            s_tan.Clear();
            if (badN == 0 && badT == 0) return;
            if (badN > 0) NormalsBad++;
            if (badT > 0) TangentsBad++;
            EasyBakeLog.Debug($"[MeshCheck] NaN-risk {where} '{m.name}': {badN} bad normal(s) (NaN / Inf / zero length){(badN > 0 ? $", first #{firstN} = ({nv0.x}, {nv0.y}, {nv0.z})" : "")}; "
                + $"{badT} bad tangent(s){(badT > 0 ? $", first #{firstT} = ({tv0.x}, {tv0.y}, {tv0.z}, {tv0.w})" : "")}; still drawn (1.2.487: report only)");
        }

        // ---- 1.2.487 steps 2-3: a degenerate transform (|det| < 1e-9, a NaN / Inf element) turns normals into NaN: CombineMeshes transforms them by the inverse
        // transpose, and the instanced shaders by the instance's inverse, while positions stay finite (so the position check above never sees it).
        // Debug only, called by the zone combine (per source part) and the instanced groups (per instance); the first 5 per session are named, the rest counted.
        internal static int DegenerateMatrices; private static int s_degenNamed;
        private static bool Degenerate(Matrix4x4 m, out float det, out bool nan)
        {
            det = m.determinant;
            nan = false;
            for (int i = 0; i < 16 && !nan; i++) { float v = m[i]; nan = float.IsNaN(v) || float.IsInfinity(v); }
            return nan || float.IsNaN(det) || Mathf.Abs(det) < 1e-9f;
        }

        private static void SayDegenerate(Matrix4x4 m, float det, bool nan, string where)
        {
            DegenerateMatrices++;
            if (s_degenNamed >= 5) return;
            s_degenNamed++;
            Vector3 p = m.GetColumn(3);
            EasyBakeLog.Debug($"[MeshCheck] NaN-risk transform ({where}) at ({p.x:0.0}, {p.y:0.0}, {p.z:0.0}): "
                + $"{(nan ? "a NaN / Inf element" : $"determinant {det:E2} (scale ~0 or flattened)")}; its normals become NaN when drawn (#{DegenerateMatrices}; first 5 named)");
        }

        // the zone combine, per source part (Debug only at the caller)
        internal static void NoteMatrix(Matrix4x4 m, string kind, int zx, int zz, Object what)
        {
            if (!Degenerate(m, out float det, out bool nan)) return;
            SayDegenerate(m, det, nan, $"{kind} {zx},{zz}, '{(what != null ? what.name : "?")}'");
        }

        // an instanced group, per instance matrix added (Debug only here); the prefab is named from its hash on a hit only
        internal static void NoteInstance(Matrix4x4 m, int prefabHash, string how)
        {
            if (!EasyBakeLog.DebugOn()) return;
            if (!Degenerate(m, out float det, out bool nan)) return;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabHash) : null;
            SayDegenerate(m, det, nan, $"instanced {how}, '{(prefab != null ? prefab.name : "#" + prefabHash)}'");
        }

        internal static bool Sane(Mesh m, string where)
        {
            if (m == null) return true;
            Checked++;
            s_lastUseAt = Time.unscaledTime;   // 1.2.392 (TrimIdle)
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
                if (why == null && EasyBakeLog.DebugOn()) ScanNormals(m, where);   // 1.2.487: Debug only, reports (never hides the mesh)
                s_idx.Clear(); s_pos.Clear();
            }
            if (why == null) return true;
            Bad++; s_bad++;
            // (1.2.487 note: the normal / tangent scan above never sets why; a NaN normal is reported, not hidden, until a log line names the culprit)
            if (!s_said) { s_said = true; EasyBakeLog.Error($"[MeshCheck] BAD MESH at frame {Time.frameCount} ({where}, '{m.name}'): {why}. It is not drawn. Later bad meshes are logged at Debug."); }
            else EasyBakeLog.Debug($"[MeshCheck] bad mesh #{s_bad} ({where}, '{m.name}'): {why}");
            return false;
        }
    }
}
