using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.416 ([lead] / Fire 11-16 hand test at x -1421 z 742, frozen lake: far rocks and trees float in the sky; "the vegetation renders before the terrain
    /// ever does"). [Far] FarNeedsGround: far content is drawn only over ground that is actually drawn.
    /// Why there can be no ground (vanilla TerrainLod.cs:116-214): the far ground is 3 x 3 distant-LOD Heightmap regions (m_terrainSize 2400). On arrival
    /// m_lastPoint starts at (99999, 0, 99999), so nothing is built until every region is ready; and after the camera moves 256 m m_lastPoint jumps at once,
    /// but the regions keep their OLD position and mesh until ALL new ones are ready (IsAllTerrainReady), then all move and regenerate together. Meanwhile
    /// far rocks / impostors drawn from EBM's cache stand where no ground is drawn yet.
    /// Covered = a zone's centre lies inside a BUILT distant-LOD region (any Heightmap with IsDistantLod: vanilla's TerrainLod and FAT's second one alike),
    /// using the region's real transform (its drawn centre) and size (m_width x m_scale), or the zone is loaded (its own heightmap is there).
    /// "Built" = the region's render mesh exists with vertices and its renderer is enabled. The region list is refreshed at most every 0.25 s.
    /// </summary>
    internal static class FarGroundCover
    {
        private static readonly AccessTools.FieldRef<List<Heightmap>> s_maps = AccessTools.StaticFieldRefAccess<List<Heightmap>>(AccessTools.Field(typeof(Heightmap), "s_heightmaps"));
        private static readonly AccessTools.FieldRef<Heightmap, Mesh> s_mesh = AccessTools.FieldRefAccess<Heightmap, Mesh>("m_renderMesh");
        private static readonly AccessTools.FieldRef<Heightmap, MeshRenderer> s_renderer = AccessTools.FieldRefAccess<Heightmap, MeshRenderer>("m_meshRenderer");

        // 1.2.417 ([lead]: FarNeedsGround "didn't work" on Fire's island shot): 1.2.416 counted a point as ground whenever a built distant region
        // contained it, even where that region's own shader HIDES it (inside its _LodHideDistance from the camera, where the near tiles should draw),
        // and counted a loaded zone as ground before its own heightmap mesh existed. Now a distant region covers a point only beyond its hide radius
        // (read from the renderer's property block, as vanilla / any mod wrote it), and inside that radius only a BUILT near heightmap counts.
        private static readonly int s_hideId = Shader.PropertyToID("_LodHideDistance");
        private static readonly MaterialPropertyBlock s_mpb = new MaterialPropertyBlock();
        private struct Near { public float MinX, MaxX, MinZ, MaxZ; }
        private static readonly List<Near> s_near = new List<Near>(256);
        private static float s_nearMinX = float.MaxValue, s_nearMaxX = float.MinValue, s_nearMinZ = float.MaxValue, s_nearMaxZ = float.MinValue;   // 1.2.485: the near list's box (empty = rejects all)
        private static Vector3 s_cam;
        private struct Region { public float MinX, MaxX, MinZ, MaxZ, Hide; }
        private static readonly List<Region> s_regions = new List<Region>(16);
        private static float s_nextRefresh;
        internal static int Held;   // zones held back this window because no ground is drawn under them (census)

        internal static bool On => FiresEasyBakeMeshesPlugin.FarNeedsGround == null || FiresEasyBakeMeshesPlugin.FarNeedsGround.Value;

        private static void Refresh()
        {
            float now = Time.unscaledTime;
            if (now < s_nextRefresh) return;
            s_nextRefresh = now + 0.25f;
            s_regions.Clear(); s_near.Clear();
            s_nearMinX = s_nearMinZ = float.MaxValue; s_nearMaxX = s_nearMaxZ = float.MinValue;   // 1.2.485: bounding box of every built near chunk
            var cam = Utils.GetMainCamera();
            s_cam = cam != null ? cam.transform.position : Vector3.zero;
            var maps = s_maps();
            if (maps == null) return;
            for (int i = 0; i < maps.Count; i++)
            {
                var hm = maps[i];
                if (hm == null) continue;
                var mesh = s_mesh(hm); var mr = s_renderer(hm);
                if (mesh == null || mesh.vertexCount == 0 || mr == null || !mr.enabled || !hm.gameObject.activeInHierarchy) continue;   // not built / not drawn = no ground
                Vector3 c = hm.transform.position;
                float half = hm.m_width * hm.m_scale * 0.5f;
                if (!hm.IsDistantLod)
                {
                    s_near.Add(new Near { MinX = c.x - half, MaxX = c.x + half, MinZ = c.z - half, MaxZ = c.z + half });
                    s_nearMinX = Mathf.Min(s_nearMinX, c.x - half); s_nearMaxX = Mathf.Max(s_nearMaxX, c.x + half);
                    s_nearMinZ = Mathf.Min(s_nearMinZ, c.z - half); s_nearMaxZ = Mathf.Max(s_nearMaxZ, c.z + half);
                    continue;
                }
                mr.GetPropertyBlock(s_mpb);
                float hide = s_mpb.GetFloat(s_hideId);   // 0 when nobody set one: then the region hides nothing
                s_regions.Add(new Region { MinX = c.x - half, MaxX = c.x + half, MinZ = c.z - half, MaxZ = c.z + half, Hide = hide });
            }
        }

        /// <summary>True when ground is drawn under this zone's centre (a loaded zone, or inside a built distant-LOD region). Key off = always true.</summary>
        internal static bool Covered(Vector2s zone)
        {
            if (!On) return true;
            Refresh();
            float x = zone.x * 64f + 32f, z = zone.y * 64f + 32f;
            // a BUILT near heightmap under the point (loaded and generated; a loaded zone whose mesh is not built yet does not count)
            if (x >= s_nearMinX && x <= s_nearMaxX && z >= s_nearMinZ && z <= s_nearMaxZ)   // 1.2.485: a far zone (outside every near chunk's box) skips the list
                for (int i = 0; i < s_near.Count; i++)
                {
                    var n = s_near[i];
                    if (x >= n.MinX && x <= n.MaxX && z >= n.MinZ && z <= n.MaxZ) return true;
                }
            // 1.2.484 ([visual], design 3.2 "Ready = ground first"): EBM's own far ground. With [03 - Far Rendering] Far Terrain on, FarTerrainTiles.HandOff switches every
            // TerrainLod off (their regions are destroyed, TerrainLod.OnDisable), so the region test below found NO ground past the loaded zones and FarNeedsGround hid
            // every far rock (FarRocks.cs:175) and masked every far tree zone (ImpostorHandOff.cs:65-75) over EBM's drawn far ground. A SHOWN far tile holding the
            // zone centre is ground. One ground test for every far layer: this method.
            if (FarTerrainTiles.CoversPoint(x, z)) return true;
            // a built distant region whose shader draws at this point: inside the region and BEYOND its hide radius from the camera
            float dx = x - s_cam.x, dz = z - s_cam.z, d = Mathf.Sqrt(dx * dx + dz * dz);
            for (int i = 0; i < s_regions.Count; i++)
            {
                var r = s_regions[i];
                if (x >= r.MinX && x <= r.MaxX && z >= r.MinZ && z <= r.MaxZ && d > r.Hide) return true;
            }
            Held++;
            return false;
        }
    }
}
