using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // W2 ([seasons] 11-11, Tools\PLAN_ebm_water_draws_20261111.md; lead: "write W2 now ... ON to test"): vanilla water tiles that a pixel light touches
    // are drawn as 16 smaller pieces, so each light's extra draws cover only the pieces it reaches.
    //
    // Why: [visual]'s replay at swamp spot 4 (frame 5199) had Custom/Water as the biggest GPU item. 248 draws: 78 tiles x the shader's 2 base passes, plus
    // 46 light-on-tile pairs x its 2 forward-add passes. Unity gives a renderer every pixel light whose range touches its bounds, and redraws the WHOLE
    // 64 m tile twice per light (0.63 ms of the 1.89 ms in the replay) for a torch that reaches a few metres of it.
    //
    // How:
    //  - tiles come from vanilla's own WaterVolume.Instances (no patches);
    //  - the tile mesh (one shared asset) is cut once into a 4 x 4 grid by triangle centre. Each piece keeps every vertex channel and the original's full
    //    height range in its bounds (the waves), and the pieces are shared by all tiles;
    //  - a tile is split only while a pixel light's sphere touches its bounds (checked 4 times a second, lights re-read once a second), and goes back to
    //    one renderer after 5 s without one;
    //  - the pieces are children of the tile's own renderer, with its material instance (the per-tile values vanilla sets every frame), layer, probes and
    //    sorting. They mirror its enabled state and material each tick. The original is hidden with forceRenderingOff (restored after), so anything
    //    that reads or sets its enabled flag keeps working.
    // Look: the same mesh, material and values, drawn in pieces. Transparent sorting against OTHER transparent objects uses each piece's centre instead of
    // the tile's (closer to the truth); nothing else changes.
    // Verify (WaterSplitLitTilesVerify): nothing is split. The census counts the light draws the split would keep (light-on-piece pairs) against today's.
    internal static class WaterSplit
    {
        // WaterSplitGrid ([visual]'s review: a split tile's 2 base draws become 2 x Pieces): 4 = 16 pieces (smallest light footprint), 2 = 4 pieces (fewer draws)
        private static int Grid = 4, Pieces = 16;
        private const float LitHoldSeconds = 5f, TestSeconds = 0.25f, LightListSeconds = 1f, CensusSeconds = 30f;

        internal static ConfigEntry<bool> Enabled, Verify;
        internal static ConfigEntry<int> GridKey;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = EbmConfigMove.Bind(cfg, "41 - Water Performance", "WaterSplitLitTiles", false /* 1.2.296 OFF: [lead] A/B at swamp spot4, ON 4.452 ms vs OFF 4.103 ms (the extra base draws cost more than the light passes saved) */,
                "Water tiles that a torch or other pixel light reaches are drawn as 16 smaller pieces, so the light's extra passes only cover the pieces it reaches instead of the whole 64 m tile. Same water look. Off = as before.");
            Verify = EbmConfigMove.Bind(cfg, "90 - Debugging", "WaterSplitLitTilesVerify", false,
                "Test twin of WaterSplitLitTiles: nothing is split; the Debug log counts how many light passes the split would keep. Off = no count.");
            GridKey = EbmConfigMove.Bind(cfg, "41 - Water Performance", "WaterSplitGrid", 4, new ConfigDescription(
                "With WaterSplitLitTiles: a lit water tile is cut into Grid x Grid pieces. 4 = 16 pieces (a light covers the least water, more draws), 2 = 4 pieces (fewer draws). Changes apply live.",
                new AcceptableValueRange<int>(2, 4)));
        }

        // a changed grid: every piece and cut is rebuilt from scratch on the next test
        private static void ApplyGrid()
        {
            int g = GridKey != null ? (GridKey.Value >= 4 ? 4 : GridKey.Value >= 3 ? 3 : 2) : 4;
            if (g == Grid) return;
            MergeAll(destroy: true);
            s_tiles.Clear();
            foreach (var cut in s_slices.Values) foreach (var p in cut.Parts) if (p != null) Object.Destroy(p);
            s_slices.Clear(); s_unusable.Clear();
            Grid = g; Pieces = g * g;
            EasyBakeLog.Info($"[WaterSplit] grid {g} x {g} ({Pieces} pieces per lit tile)");
        }

        private sealed class Slices { public Mesh[] Parts; public Bounds[] LocalBounds; }
        private sealed class Tile
        {
            public WaterVolume Volume; public MeshRenderer Orig; public MeshRenderer[] Subs; public Slices Cut;
            public bool Split, PriorForceOff; public float LitUntil = -1f;
        }

        private static readonly Dictionary<Mesh, Slices> s_slices = new Dictionary<Mesh, Slices>();   // a handful of meshes: the zone plane, cave cubes
        private static readonly HashSet<Mesh> s_unusable = new HashSet<Mesh>();
        private static readonly Dictionary<WaterVolume, Tile> s_tiles = new Dictionary<WaterVolume, Tile>();
        private static readonly List<WaterVolume> s_dead = new List<WaterVolume>();
        private static readonly List<Light> s_lights = new List<Light>(64);
        private static readonly LightType[] s_lightTypes = { LightType.Point, LightType.Spot };
        private static MaterialPropertyBlock s_block;
        private static float s_nextTest, s_nextLights, s_nextCensus;
        private static string s_state = "";
        private static bool s_wasOn;

        // census since the last line
        private static int w_tests, w_tilesSum, w_litSum, w_splitSum, w_pairsBefore, w_pairsAfter, w_splits, w_merges, w_baseExtra;

        internal static void Tick()
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            bool on = !headless && Enabled != null && Enabled.Value;
            bool verify = !headless && Verify != null && Verify.Value;
            if (!on && !verify)
            {
                if (s_wasOn) { MergeAll(destroy: true); s_wasOn = false; }
                Say(headless ? "off (no graphics: dedicated server)" : "off (WaterSplitLitTiles and its Verify are off)");
                return;
            }
            if (!on && s_wasOn) { MergeAll(destroy: true); s_wasOn = false; }   // Verify only: nothing split
            s_wasOn = on;
            ApplyGrid();
            Say(on ? "on (tiles a pixel light touches are drawn as 16 pieces)" : "VERIFY (nothing split; light passes the split would keep are counted)");

            float now = Time.time;
            if (now >= s_nextLights) { s_nextLights = now + LightListSeconds; ReadLights(); }
            if (now >= s_nextTest)
            {
                s_nextTest = now + TestSeconds;
                Test(now, on);
            }
            else if (on) Mirror();
            if (Time.unscaledTime >= s_nextCensus) Census();
        }

        // the lights Unity can give a forward-add pass: point / spot, on, not forced to vertex, lighting the water layer (Light.GetLights: one small array a second)
        private static void ReadLights()
        {
            s_lights.Clear();
            int waterLayer = 4;
            foreach (var type in s_lightTypes)
                foreach (var l in Light.GetLights(type, waterLayer))
                    if (l != null && l.isActiveAndEnabled && l.intensity > 0f && l.renderMode != LightRenderMode.ForceVertex) s_lights.Add(l);
        }

        private static void Test(float now, bool on)
        {
            var list = WaterVolume.Instances;
            int tiles = 0, lit = 0, split = 0, before = 0, after = 0;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null || v.m_waterSurface == null) continue;
                    var t = TileFor(v);
                    if (t == null) continue;
                    tiles++;
                    if (!t.Orig.enabled || !t.Orig.gameObject.activeInHierarchy) { if (t.Split && on) Merge(t); continue; }
                    if (WaterHideBuried.IsHidden(v)) { if (t.Split && on) Merge(t); continue; }   // A1 hid it (buried): never split; WaterHideBuried re-asserts its own hide after the merge
                    Bounds tb = t.Orig.bounds;
                    int touching = 0, pieceHits = 0;
                    Matrix4x4 m = t.Orig.transform.localToWorldMatrix;
                    for (int k = 0; k < s_lights.Count; k++)
                    {
                        var l = s_lights[k];
                        if (l == null) continue;
                        Vector3 p = l.transform.position; float r = l.range;
                        if ((tb.ClosestPoint(p) - p).sqrMagnitude > r * r) continue;
                        touching++;
                        for (int s = 0; s < Pieces; s++)
                        {
                            Bounds pb = WorldBounds(m, t.Cut.LocalBounds[s]);
                            if ((pb.ClosestPoint(p) - p).sqrMagnitude <= r * r) pieceHits++;
                        }
                    }
                    if (touching > 0) { lit++; t.LitUntil = now + LitHoldSeconds; before += touching; after += pieceHits; }
                    if (on)
                    {
                        if (!t.Split && touching > 0) SplitTile(t);
                        else if (t.Split && now > t.LitUntil) Merge(t);
                        if (t.Split) { split++; MirrorTile(t); }
                    }
                }
            }
            s_dead.Clear();
            foreach (var kv in s_tiles) if (kv.Key == null || (kv.Value != null && kv.Value.Orig == null)) s_dead.Add(kv.Key);   // null value = a tile left whole on purpose
            for (int i = 0; i < s_dead.Count; i++) s_tiles.Remove(s_dead[i]);   // their pieces went with the tile's GameObject
            w_tests++; w_tilesSum += tiles; w_litSum += lit; w_splitSum += split; w_pairsBefore += before; w_pairsAfter += after;
            w_baseExtra += (on ? split : lit) * (Pieces - 1) * 2;   // the shader's 2 base passes per piece instead of per tile (upper bound: before Unity's frustum cull of pieces)
        }

        private static Tile TileFor(WaterVolume v)
        {
            if (s_tiles.TryGetValue(v, out var t)) return t;
            var mf = v.m_waterSurface.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            var cut = mesh != null ? SlicesFor(mesh) : null;
            if (cut == null) { s_tiles[v] = null; return null; }   // remembered: not tried again for this tile
            t = new Tile { Volume = v, Orig = v.m_waterSurface, Cut = cut };
            s_tiles[v] = t;
            return t;
        }

        // ---------------------------------------------------------------- the cut (once per mesh)
        private static Slices SlicesFor(Mesh mesh)
        {
            if (s_slices.TryGetValue(mesh, out var cut)) return cut;
            if (s_unusable.Contains(mesh)) return null;
            string why = null;
            if (!mesh.isReadable) why = "not readable";
            else if (mesh.subMeshCount != 1) why = mesh.subMeshCount + " submeshes";
            else if (mesh.GetIndexCount(0) < 3 * 512) why = "under 512 triangles (nothing to gain)";
            if (why != null) { s_unusable.Add(mesh); EasyBakeLog.Debug($"[WaterSplit] water mesh '{mesh.name}' left whole: {why}"); return null; }

            int[] tris = mesh.triangles;
            Vector3[] pos = mesh.vertices; Vector3[] nrm = mesh.normals; Vector4[] tan = mesh.tangents; Color[] col = mesh.colors;
            var uvs = new List<Vector4>[4];
            for (int c = 0; c < 4; c++) { var u = new List<Vector4>(); mesh.GetUVs(c, u); uvs[c] = u.Count == pos.Length ? u : null; }
            Bounds b = mesh.bounds;
            float sx = Mathf.Max(1e-5f, b.size.x / Grid), sz = Mathf.Max(1e-5f, b.size.z / Grid);
            var cellTris = new List<int>[Pieces];
            for (int s = 0; s < Pieces; s++) cellTris[s] = new List<int>();
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 c = (pos[tris[i]] + pos[tris[i + 1]] + pos[tris[i + 2]]) / 3f;
                int cx = Mathf.Clamp((int)((c.x - b.min.x) / sx), 0, Grid - 1), cz = Mathf.Clamp((int)((c.z - b.min.z) / sz), 0, Grid - 1);
                cellTris[cz * Grid + cx].Add(i);
            }
            cut = new Slices { Parts = new Mesh[Pieces], LocalBounds = new Bounds[Pieces] };
            var map = new int[pos.Length];
            float padX = b.size.x * 0.02f, padZ = b.size.z * 0.02f;
            for (int s = 0; s < Pieces; s++)
            {
                for (int i = 0; i < map.Length; i++) map[i] = -1;
                var vIdx = new List<int>(); var outTris = new int[cellTris[s].Count * 3];
                int o = 0;
                foreach (int ti in cellTris[s])
                    for (int j = 0; j < 3; j++)
                    {
                        int src = tris[ti + j];
                        if (map[src] < 0) { map[src] = vIdx.Count; vIdx.Add(src); }
                        outTris[o++] = map[src];
                    }
                var part = new Mesh { name = $"EBM water piece {s} of {mesh.name}" };
                var pv = new Vector3[vIdx.Count];
                Vector3 lo = new Vector3(float.MaxValue, 0f, float.MaxValue), hi = new Vector3(float.MinValue, 0f, float.MinValue);
                for (int i = 0; i < vIdx.Count; i++) { pv[i] = pos[vIdx[i]]; lo = Vector3.Min(lo, pv[i]); hi = Vector3.Max(hi, pv[i]); }
                part.vertices = pv;
                if (nrm != null && nrm.Length == pos.Length) { var a = new Vector3[vIdx.Count]; for (int i = 0; i < a.Length; i++) a[i] = nrm[vIdx[i]]; part.normals = a; }
                if (tan != null && tan.Length == pos.Length) { var a = new Vector4[vIdx.Count]; for (int i = 0; i < a.Length; i++) a[i] = tan[vIdx[i]]; part.tangents = a; }
                if (col != null && col.Length == pos.Length) { var a = new Color[vIdx.Count]; for (int i = 0; i < a.Length; i++) a[i] = col[vIdx[i]]; part.colors = a; }
                for (int c = 0; c < 4; c++)
                {
                    if (uvs[c] == null) continue;
                    var a = new List<Vector4>(vIdx.Count); for (int i = 0; i < vIdx.Count; i++) a.Add(uvs[c][vIdx[i]]);
                    part.SetUVs(c, a);
                }
                part.SetTriangles(outTris, 0, false);
                if (vIdx.Count == 0) { lo = hi = b.center; }
                // the original's full height range (it is padded for the waves) and a small sideways margin for the wave sway
                var pb = new Bounds();
                pb.SetMinMax(new Vector3(lo.x - padX, b.min.y, lo.z - padZ), new Vector3(hi.x + padX, b.max.y, hi.z + padZ));
                part.bounds = pb;
                part.UploadMeshData(true);   // GPU copy only
                cut.Parts[s] = part; cut.LocalBounds[s] = pb;
            }
            s_slices[mesh] = cut;
            EasyBakeLog.Info($"[WaterSplit] cut water mesh '{mesh.name}' ({tris.Length / 3} triangles) into {Pieces} pieces for lit tiles");
            return cut;
        }

        private static Bounds WorldBounds(Matrix4x4 m, Bounds local)
        {
            Vector3 c = m.MultiplyPoint3x4(local.center), e = local.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0f, 0f)), ay = m.MultiplyVector(new Vector3(0f, e.y, 0f)), az = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            Vector3 we = new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y), Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, we * 2f);
        }

        // ---------------------------------------------------------------- split / merge
        private static void SplitTile(Tile t)
        {
            if (t.Subs == null)
            {
                t.Subs = new MeshRenderer[Pieces];
                var o = t.Orig;
                for (int s = 0; s < Pieces; s++)
                {
                    var go = new GameObject("EBM_WaterPiece" + s) { layer = o.gameObject.layer };
                    go.transform.SetParent(o.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = t.Cut.Parts[s];
                    var r = go.AddComponent<MeshRenderer>();
                    r.shadowCastingMode = o.shadowCastingMode; r.receiveShadows = o.receiveShadows;
                    r.lightProbeUsage = o.lightProbeUsage; r.reflectionProbeUsage = o.reflectionProbeUsage; r.probeAnchor = o.probeAnchor != null ? o.probeAnchor : o.transform;
                    r.sortingLayerID = o.sortingLayerID; r.sortingOrder = o.sortingOrder; r.rendererPriority = o.rendererPriority;
                    r.renderingLayerMask = o.renderingLayerMask; r.motionVectorGenerationMode = o.motionVectorGenerationMode; r.allowOcclusionWhenDynamic = o.allowOcclusionWhenDynamic;
                    r.enabled = false;
                    t.Subs[s] = r;
                }
            }
            t.PriorForceOff = t.Orig.forceRenderingOff;
            MirrorTile(t);
            for (int s = 0; s < Pieces; s++) t.Subs[s].enabled = t.Orig.enabled;
            t.Orig.forceRenderingOff = true;
            t.Split = true; w_splits++;
        }

        private static void Merge(Tile t)
        {
            if (!t.Split) return;
            if (t.Subs != null) for (int s = 0; s < Pieces; s++) if (t.Subs[s] != null) t.Subs[s].enabled = false;
            if (t.Orig != null) t.Orig.forceRenderingOff = t.PriorForceOff;
            t.Split = false; w_merges++;
        }

        private static void MergeAll(bool destroy)
        {
            foreach (var t in s_tiles.Values)
            {
                if (t == null) continue;
                Merge(t);
                if (destroy && t.Subs != null) { for (int s = 0; s < Pieces; s++) if (t.Subs[s] != null) Object.Destroy(t.Subs[s].gameObject); t.Subs = null; }
            }
        }

        // between tests: the pieces follow the tile's material instance (vanilla instances it on first use; FTS may swap its shader), its enabled flag and any property block
        private static void Mirror()
        {
            foreach (var t in s_tiles.Values) if (t != null && t.Split && t.Orig != null) MirrorTile(t);
        }

        private static void MirrorTile(Tile t)
        {
            var o = t.Orig; var mat = o.sharedMaterial; bool en = o.enabled; bool block = o.HasPropertyBlock();
            if (block) { if (s_block == null) s_block = new MaterialPropertyBlock(); o.GetPropertyBlock(s_block); }
            for (int s = 0; s < Pieces; s++)
            {
                var r = t.Subs[s];
                if (r == null) continue;
                if (r.sharedMaterial != mat) r.sharedMaterial = mat;
                if (t.Split && r.enabled != en) r.enabled = en;
                if (block) r.SetPropertyBlock(s_block); else if (r.HasPropertyBlock()) r.SetPropertyBlock(null);
            }
            if (t.Split && !o.forceRenderingOff) o.forceRenderingOff = true;   // someone cleared it: hide the whole tile again
        }

        // ---------------------------------------------------------------- lines
        private static void Say(string state)
        {
            if (state == s_state) return;
            s_state = state;
            EasyBakeLog.Info("[WaterSplit] " + state);
        }

        private static void Census()
        {
            s_nextCensus = Time.unscaledTime + CensusSeconds;
            if (w_tests > 0 && EasyBakeLog.DebugOn())
            {
                float n = w_tests;
                double kept = w_pairsBefore > 0 ? (double)w_pairsAfter / (w_pairsBefore * (double)Pieces) : 0;
                EasyBakeLog.Debug($"[WaterSplit] 30 s ({w_tests} tests): water tiles {w_tilesSum / n:0.0}, lit by a pixel light {w_litSum / n:0.0}, split {w_splitSum / n:0.0} (splits {w_splits}, merges {w_merges}); "
                    + $"light draws {2 * w_pairsBefore / n:0.0} whole-tile -> {2 * w_pairsAfter / n:0.0} piece-sized (each light covers {kept:P0} of a tile's area on average); "
                    + $"base draws +{w_baseExtra / n:0.0} (grid {Grid} x {Grid}, upper bound before frustum cull); {s_lights.Count} pixel light(s) on the water layer");
            }
            w_tests = w_tilesSum = w_litSum = w_splitSum = w_pairsBefore = w_pairsAfter = w_splits = w_merges = w_baseExtra = 0;
        }
    }
}
