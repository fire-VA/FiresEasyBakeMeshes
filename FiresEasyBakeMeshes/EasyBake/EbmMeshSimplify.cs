// EBM 1.2.206 (Fire: "ebm cannot rely on any other mod"): EBM's own copy of the team's Core FiresCore.Far.MeshSimplify (our code), for the intact-rock LOD1.
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 0.2.1212 ([ghost], DESIGN_OcclusionLOD §5, lead approved 11-08): the team's grid-cluster mesh simplifier (EBM's own copy since 1.2.206). It is the
    /// method the far-building cell merge already used (FarBuildingMerge, past "Cell merge from"), lifted here so EBM's intact-rock LOD1
    /// reuses it instead of a second copy (Fire: "reuse our working code/methods before writing new ones").
    ///
    /// Method: every vertex snaps to a cubic cell of side cellSize AND one of six dominant-normal buckets (so the two faces of a thin wall,
    /// or a rock's top and side, never merge); the first vertex seen in a (cell, bucket) represents it; triangles that collapse (two or
    /// three corners in one cluster) are dropped. Submeshes are kept (materials stay matched). Pure arrays, no allocation per vertex after
    /// the first pass, safe on a worker thread for the array overload (the Mesh overload must run on the main thread).
    /// EBM's own copy (no Core reference): EBM's code calls it directly.
    /// </summary>
    internal static class EbmMeshSimplify
    {
        /// <summary>The cluster key: cell (x 20 bits, y 16, z 20) + dominant normal axis (3 bits). Shared with the far-building merge.</summary>
        public static long ClusterKey(Vector3 p, Vector3 n, float cellSize)
        {
            float c = cellSize > 1e-4f ? cellSize : 1e-4f;
            return ((long)Mathf.FloorToInt(p.x / c) & 0xFFFFF) | (((long)Mathf.FloorToInt(p.y / c) & 0xFFFF) << 20)
                 | (((long)Mathf.FloorToInt(p.z / c) & 0xFFFFF) << 36) | ((long)DominantAxis(n) << 56);
        }

        /// <summary>0..5: +Y, -Y, +X, -X, +Z, -Z (the largest normal component and its sign).</summary>
        public static int DominantAxis(Vector3 n)
        {
            float ax = Mathf.Abs(n.x), ay = Mathf.Abs(n.y), az = Mathf.Abs(n.z);
            if (ay >= ax && ay >= az) return n.y >= 0 ? 0 : 1;
            if (ax >= az) return n.x >= 0 ? 2 : 3;
            return n.z >= 0 ? 4 : 5;
        }

        /// <summary>
        /// A simplified copy of a readable mesh (main thread). cellSize in the mesh's own units. trisBefore / trisAfter for the proof line.
        /// Returns null when the mesh is not readable or nothing is left.
        /// </summary>
        public static Mesh Simplify(Mesh source, float cellSize, out int trisBefore, out int trisAfter)
        {
            trisBefore = trisAfter = 0;
            if (source == null || !source.isReadable) return null;
            var pos = source.vertices;
            var nrm = source.normals; if (nrm == null || nrm.Length != pos.Length) nrm = new Vector3[pos.Length];
            var tan = source.tangents; if (tan == null || tan.Length != pos.Length) tan = null;
            var uv = source.uv; if (uv == null || uv.Length != pos.Length) uv = null;
            var subs = new int[source.subMeshCount][];
            for (int s = 0; s < subs.Length; s++) subs[s] = source.GetTopology(s) == MeshTopology.Triangles ? source.GetTriangles(s) : new int[0];

            if (!Simplify(pos, nrm, tan, uv, subs, cellSize, out var oPos, out var oNrm, out var oTan, out var oUv, out var oSubs, out trisBefore, out trisAfter))
                return null;

            var mesh = new Mesh { name = source.name + " (simplified)" };
            if (oPos.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(oPos);
            mesh.SetNormals(oNrm);
            if (oTan != null) mesh.SetTangents(oTan);
            if (oUv != null) mesh.SetUVs(0, oUv);
            mesh.subMeshCount = oSubs.Length;
            for (int s = 0; s < oSubs.Length; s++) mesh.SetTriangles(oSubs[s], s, false);
            EbmMeshCheck.Sane(mesh, "simplified LOD");   // 1.2.234 (logged; the caller keeps its own fallbacks)
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The array form (any thread). Output arrays hold only the kept vertices; submesh index lists are remapped.</summary>
        public static bool Simplify(Vector3[] pos, Vector3[] nrm, Vector4[] tan, Vector2[] uv, int[][] subs, float cellSize,
            out Vector3[] oPos, out Vector3[] oNrm, out Vector4[] oTan, out Vector2[] oUv, out int[][] oSubs, out int trisBefore, out int trisAfter)
        {
            oPos = null; oNrm = null; oTan = null; oUv = null; oSubs = null; trisBefore = trisAfter = 0;
            if (pos == null || pos.Length == 0 || subs == null) return false;
            var cluster = new Dictionary<long, int>(pos.Length / 2 + 1);
            var map = new int[pos.Length];
            var keepPos = new List<Vector3>(pos.Length / 3 + 1);
            var keepNrm = new List<Vector3>(pos.Length / 3 + 1);
            var keepTan = tan != null ? new List<Vector4>(pos.Length / 3 + 1) : null;
            var keepUv = uv != null ? new List<Vector2>(pos.Length / 3 + 1) : null;
            for (int i = 0; i < pos.Length; i++)
            {
                Vector3 n = nrm != null && i < nrm.Length ? nrm[i] : Vector3.up;
                long key = ClusterKey(pos[i], n, cellSize);
                if (!cluster.TryGetValue(key, out int at))
                {
                    at = keepPos.Count; cluster[key] = at;
                    keepPos.Add(pos[i]); keepNrm.Add(n);
                    keepTan?.Add(tan[i]); keepUv?.Add(uv[i]);
                }
                map[i] = at;
            }
            oSubs = new int[subs.Length][];
            var tri = new List<int>();
            for (int s = 0; s < subs.Length; s++)
            {
                tri.Clear();
                var t = subs[s] ?? new int[0];
                trisBefore += t.Length / 3;
                for (int k = 0; k + 2 < t.Length; k += 3)
                {
                    int a = map[t[k]], b = map[t[k + 1]], c = map[t[k + 2]];
                    if (a == b || b == c || a == c) continue;
                    tri.Add(a); tri.Add(b); tri.Add(c);
                }
                trisAfter += tri.Count / 3;
                oSubs[s] = tri.ToArray();
            }
            if (trisAfter == 0) return false;
            oPos = keepPos.ToArray(); oNrm = keepNrm.ToArray(); oTan = keepTan?.ToArray(); oUv = keepUv?.ToArray();
            return true;
        }
    }
}
