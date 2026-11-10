using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [Optimize] FarGroundProbe ([worldgen] 11-09, round 2 row E20 "stop drawing hidden ground"; our own method). Default off. COUNTS ONLY: it never
    // changes what is drawn. Lead's rule: skip ground only when provably hidden, any doubt = draw. Today there is doubt, so it draws everything and says why.
    //
    // What vanilla does (Tools\_valheim_diff_20261028\new): TerrainLod.cs draws the far ground as 3 x 3 Heightmap tiles of 800 m (m_terrainSize 2400,
    // m_regionsPerAxis 3, a vertex every 10 m), re-centred on the camera only after it moved m_updateStepDistance (256 m) AND the new terrain is ready.
    // Heightmap.ApplySettings (:1550-1566) gives every terrain renderer _LodHideDistance = max(1, near simulation distance) x the zone diagonal (90.5 m);
    // the ripped material also carries _LodHideModifier -30. The terrain shader hides distant-LOD ground inside that radius, where the real zone
    // heightmaps draw. The shader is a compiled blob in the rip (Assets\Shaders\Heightmap.asset), so its exact test cannot be read here.
    //
    // PROOF RULE (what would have to hold before one far-ground triangle could be skipped):
    //   1. the shader hides by XZ distance from the camera at radius R = _LodHideDistance + min(0, _LodHideModifier)   [UNCONFIRMED: needs RenderDoc]
    //   2. the triangle is inside R from EVERY camera position its tile serves: farthest vertex XZ distance from the tile's centre point
    //      (TerrainLod.m_lastPoint) + the farthest the camera has been from that point (at least the 256 m step) + 16 m margin < R
    //   3. the tile casts no shadows (its shadow pass may not apply the same hide), and no other camera draws terrain from elsewhere
    // At vanilla's default near distance R is about 150-181 m, below the 256 m step, so rule 2 proves zero triangles before the shader question even
    // matters. The probe measures that on the player's own settings and prints it.
    internal static class FarGroundProbe
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.FarGroundProbe != null && FiresEasyBakeMeshesPlugin.FarGroundProbe.Value;
        private const float Margin = 16f;
        private static readonly int s_hideId = Shader.PropertyToID("_LodHideDistance"), s_modId = Shader.PropertyToID("_LodHideModifier");

        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainLod, Vector3> s_lastPoint = Ref<Vector3>("m_lastPoint");
        private static readonly HarmonyLib.AccessTools.FieldRef<TerrainLod, float> s_step = Ref<float>("m_updateStepDistance");
        private static HarmonyLib.AccessTools.FieldRef<TerrainLod, T> Ref<T>(string name)
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<TerrainLod, T>(name); }
            catch (System.Exception e) { EasyBakeLog.Info($"[FarGround] probe unavailable (no TerrainLod.{name}: {e.Message})"); return null; }
        }

        private static TerrainLod s_lod; private static float s_nextFind, s_nextLog;
        private static Vector3 s_point = new Vector3(float.MaxValue, 0f, 0f);
        private static float s_maxCamFromPoint;
        private static readonly List<Vector3> s_verts = new List<Vector3>(8192);
        private static readonly List<int> s_tris = new List<int>(49152);
        private static readonly MaterialPropertyBlock s_mpb = new MaterialPropertyBlock();
        private static string s_last;
        private static bool s_saidOnce;

        internal static void Late()
        {
            if (!On || s_lastPoint == null || s_step == null) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (s_lod == null)
            {
                if (Time.unscaledTime < s_nextFind) return;
                s_nextFind = Time.unscaledTime + 10f;
                s_lod = Object.FindObjectOfType<TerrainLod>();
                if (s_lod == null) return;
            }
            Vector3 point = s_lastPoint(s_lod);
            if (point != s_point) { s_point = point; s_maxCamFromPoint = 0f; }   // the tiles re-centred
            Vector3 c = cam.transform.position;
            float camD = new Vector2(c.x - point.x, c.z - point.z).magnitude;
            if (camD > s_maxCamFromPoint) s_maxCamFromPoint = camD;
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 30f;
            Measure(point, s_step(s_lod));
        }

        private static void Measure(Vector3 point, float step)
        {
            var tiles = s_lod.GetComponentsInChildren<Heightmap>(false);
            float slack = Mathf.Max(step, s_maxCamFromPoint) + Margin;
            int total = 0, inside = 0, casting = 0; float radius = -1f, mod = 0f;
            foreach (var hm in tiles)
            {
                if (hm == null || !hm.IsDistantLod) continue;
                var r = hm.GetComponent<MeshRenderer>(); var mf = hm.GetComponent<MeshFilter>();
                if (r == null || mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                if (radius < 0f)
                {
                    r.GetPropertyBlock(s_mpb);
                    radius = s_mpb.GetFloat(s_hideId);
                    var mat = r.sharedMaterial;
                    mod = mat != null && mat.HasProperty(s_modId) ? mat.GetFloat(s_modId) : 0f;
                }
                if (r.shadowCastingMode != ShadowCastingMode.Off) casting++;
                var mesh = mf.sharedMesh;
                int tris = (int)(mesh.GetIndexCount(0) / 3);
                total += tris;
                float R = radius + Mathf.Min(0f, mod);
                if (R <= slack) continue;   // rule 2 fails for every triangle: no mesh read needed
                mesh.GetVertices(s_verts); mesh.GetTriangles(s_tris, 0);
                var m = hm.transform.localToWorldMatrix;
                float limit = R - slack;
                for (int i = 0; i + 2 < s_tris.Count; i += 3)
                {
                    if (Far(m, s_verts[s_tris[i]], point) < limit && Far(m, s_verts[s_tris[i + 1]], point) < limit && Far(m, s_verts[s_tris[i + 2]], point) < limit) inside++;
                }
            }
            float rr = radius + Mathf.Min(0f, mod);
            string why = rr <= slack ? $"hide radius {rr:0} m is inside the re-centre slack {slack:0} m (step {step:0} m, camera got {s_maxCamFromPoint:0} m from the tile centre, +{Margin:0} m)"
                       : casting > 0 ? $"{casting} far-ground tile(s) cast shadows (rule 3)"
                       : "the shader's hide test is not confirmed (rule 1: RenderDoc first)";
            string line = $"[FarGround] probe: {total} far-ground triangle(s) in {tiles.Length} tile(s); provably hidden by rule 2: {inside}; hide radius {radius:0} m, modifier {mod:0}; nothing skipped: {why}";
            if (!s_saidOnce) { s_saidOnce = true; EasyBakeLog.Info(line + ". Counts only, the ground is drawn exactly as vanilla."); }
            else if (line != s_last && EasyBakeLog.DebugOn()) EasyBakeLog.Debug(line);
            s_last = line;
        }

        private static float Far(Matrix4x4 m, Vector3 v, Vector3 point) { var w = m.MultiplyPoint3x4(v); return new Vector2(w.x - point.x, w.z - point.z).magnitude; }
    }
}
