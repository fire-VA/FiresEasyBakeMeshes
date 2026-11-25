using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.419 ([visual], [lead]; Fire 11-16: "STOP FAR TERRAIN FROM RENDERING BLACK AND MAKE IT ACTUALLY RENDER AT THE DISTANCE LIKE VANILLA"; his ruling:
    /// far-view drawing lives in EBM, FAT only supplies data). Design: Tools\DESIGN_ebm_far_terrain_20261116.md §3c. [Far] FarTerrain (default OFF).
    /// EBM's own far ground replaces vanilla's distant LOD (TerrainLod: 3 x 3 regions, 2400 m square, drawn by Custom/Heightmap whose distant matte
    /// turns it black past 400 m, decompile D8177-8179):
    ///  - Tiles of 65 x 65 vertices (64 quads) plus a skirt dropped 2 x spacing, spacing 8 m x 2^level (levels 0-5), chosen by a quadtree split on
    ///    distance (split while nearer than SplitFactor x tile size), out to the far reach ([Far] Far reach, else 3000 m). Re-planned after a 256 m move.
    ///  - Heights / colours / mask built on ONE background thread (nearest first): FAT's FatApi far data when present (TryFarHeight = vanilla's near-zone
    ///    blend so it meets loaded zones without a step; TryFarEditDelta = terrain edits, loaded or not; TryFarColor), else vanilla's own distant method
    ///    (HeightmapBuilder.Build distant path: GetBiome + GetBiomeHeight per vertex; RebuildRenderMesh: GetBiomeColor(GetBiomeSector) per vertex).
    ///  - Drawn with EBM/Heightmap on a copy of the near ground's material (texture arrays, HD packs included) with the far look (_FiresFarLook), a
    ///    per-tile paint mask, and _LodHideDistance huge so the near-chunk dither never cuts a far tile.
    ///  - Seam: vertices under LOADED zones (all zones touching the vertex loaded) sink 80 m, so the near ground draws over them; a tile wholly under
    ///    loaded ground switches its renderer off. Re-checked when the loaded set changes (0.5 s poll of a hash), never per frame.
    ///    1.2.440 [Far] Far Terrain Exact Seam (OFF): no sink (true heights); EBM/Heightmap clips far-tile pixels in zones whose near chunk is drawn
    ///    this frame (_EbmFarZoneMask, built in Camera.onPreCull). The renderer-off rule above stays.
    ///  - Vanilla's TerrainLod is disabled only once every wanted tile has been shown (its OnDisable destroys its regions, TerrainLod.cs:55-58 / 89-98);
    ///    enabled again when the key goes off, the world ends, or this part fails.
    ///  - Disk cache: one file per tile under BepInEx\cache\FiresEBM\farterrain\&lt;seed&gt;_&lt;worldUID&gt;\, keyed by level/x/z and the tile's edit print.
    /// </summary>
    internal static class FarTerrainTiles
    {
        internal static ConfigEntry<bool> On;
        private const int Q = 64, V = 65, GridVerts = V * V, VertCount = GridVerts + 4 * V;
        private const int MaxLevel = 5;
        private const float BaseSpacing = 8f, SplitFactor = 2f, Replan = 256f, SinkDepth = 80f, HugeHide = 1e7f;
        private const int CacheVersion = 1;

        private struct Key : IEquatable<Key>
        {
            public int L, X, Z;
            public Key(int l, int x, int z) { L = l; X = x; Z = z; }
            public bool Equals(Key o) => L == o.L && X == o.X && Z == o.Z;
            public override bool Equals(object o) => o is Key k && Equals(k);
            public override int GetHashCode() => (L * 73856093) ^ (X * 19349663) ^ (Z * 83492791);
            public float Spacing => BaseSpacing * (1 << L);
            public float Size => Spacing * Q;
            public float MinX => X * Size;
            public float MinZ => Z * Size;
        }

        private sealed class TileData
        {
            public Key K; public uint Print; public int Epoch;
            public float[] H;          // GridVerts heights
            public Color32[] C;        // GridVerts vertex colours (null = take sectors on the main thread)
            public BiomeSector[] S;    // GridVerts sectors when C is null
            public Color[] M;          // GridVerts paint mask
        }

        private sealed class View
        {
            public Key K; public GameObject Go; public MeshRenderer R; public Mesh Mesh; public Texture2D Mask;
            public float[] H; public bool Shown; public uint Print; public int Epoch;
        }

        // ---------------- state ----------------
        private static bool s_registered, s_lodOff, s_failed, s_saidReady;
        private static Vector3 s_planAt = new Vector3(float.NaN, 0f, float.NaN);
        private static readonly HashSet<Key> s_wanted = new HashSet<Key>();
        private static readonly Dictionary<Key, View> s_views = new Dictionary<Key, View>();
        private static readonly Stack<View> s_pool = new Stack<View>();
        private static Material s_mat;
        private static readonly MaterialPropertyBlock s_mpb = new MaterialPropertyBlock();
        private static readonly List<TerrainLod> s_lods = new List<TerrainLod>();
        private static int s_loadedHash, s_epoch = 1, s_fatRev = int.MinValue;
        private static float s_nextLoadedPoll, s_nextReport;
        private static readonly Dictionary<BiomeSector, Color32> s_sectorColour = new Dictionary<BiomeSector, Color32>();
        private static int s_built, s_fromDisk, s_shownCount;

        // builder thread
        private static Thread s_thread;
        private static volatile bool s_stop;
        private static readonly object s_lock = new object();
        private static List<Key> s_queue = new List<Key>();
        private static readonly ConcurrentQueue<TileData> s_ready = new ConcurrentQueue<TileData>();
        private static string s_cacheDir;

        private static readonly int Id_Hide = Shader.PropertyToID("_LodHideDistance"), Id_Mask = Shader.PropertyToID("_ClearedMaskTex"), Id_Distant = Shader.PropertyToID("_IsDistantLod");
        private static int[] s_indices;

        // ---------------- 1.2.440 [Far] Far Terrain Exact Seam ----------------
        // The mask says "the game's near ground chunk is drawn in this zone THIS frame": built in Camera.onPreCull (after every Update / LateUpdate and
        // after Unity's delayed Destroy, which runs before rendering), from vanilla's own near-chunk list Heightmap.GetAllHeightmaps() (Awake adds every
        // non-distant Heightmap, OnDestroy removes it, Heightmap.cs:69-70 / 93-94), each chunk's zone by vanilla ZoneSystem.GetZone (floor((x + 32) / 64)),
        // the same rounding the shader uses. Frame-exact both ways: a chunk gone this frame is not in the list at pre-cull, so its zone draws far ground.
        // Kept apart from ImpostorHandOff's mask on purpose: that one is a different signal (zone loaded + 3 s stand delay + FarNeedsGround 255 overlay).
        internal static ConfigEntry<bool> ExactSeam;
        private const int SeamSize = 64;
        private static Texture2D s_seamTex;
        private static readonly byte[] s_seamData = new byte[SeamSize * SeamSize], s_seamPrev = new byte[SeamSize * SeamSize];
        private static int s_seamFrame = -1;
        private static bool s_seamPublished;
        private static readonly int Id_SeamTex = Shader.PropertyToID("_EbmFarZoneMask"), Id_SeamOrigin = Shader.PropertyToID("_EbmFarZoneMaskOrigin"), Id_FarTile = Shader.PropertyToID("_EbmFarTile");
        private static bool SeamWanted => On != null && On.Value && ExactSeam != null && ExactSeam.Value && !s_failed;

        private static void SeamPreCull(Camera cam)
        {
            if (!SeamWanted || s_mat == null || ZoneSystem.instance == null) { if (s_seamPublished) SeamOff(); return; }
            if (Time.frameCount == s_seamFrame) return;   // once a frame, before the first camera of the frame
            s_seamFrame = Time.frameCount;
            var main = Utils.GetMainCamera();
            Vector2s pz = ZoneSystem.GetZone(main != null ? main.transform.position : cam.transform.position);
            int ox = pz.x - SeamSize / 2, oz = pz.y - SeamSize / 2;
            if (s_seamTex == null)
                s_seamTex = new Texture2D(SeamSize, SeamSize, TextureFormat.R8, false, true) { name = "FiresEBM_FarSeamMask", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            Array.Clear(s_seamData, 0, s_seamData.Length);
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || !hm.isActiveAndEnabled || hm.IsDistantLod) continue;
                // [visual] review: only a chunk whose mesh is built and drawn hides far ground (vanilla builds m_renderMesh lazily, Heightmap.cs:736); an enabled chunk with no
                // mesh yet would otherwise cut a hole. GetComponent per chunk per frame is bounded by the near-chunk count (~100).
                var mf = hm.GetComponent<MeshFilter>(); var mr = hm.GetComponent<MeshRenderer>();
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0 || mr == null || !mr.enabled) continue;
                Vector2s z = ZoneSystem.GetZone(hm.transform.position);
                int tx = z.x - ox, tz = z.y - oz;
                if ((uint)tx < SeamSize && (uint)tz < SeamSize) s_seamData[tz * SeamSize + tx] = 255;
            }
            bool changed = !s_seamPublished;
            for (int i = 0; i < s_seamData.Length && !changed; i++) changed = s_seamData[i] != s_seamPrev[i];
            var origin = new Vector4(ox, oz, SeamSize, 1f);
            if (changed) { Buffer.BlockCopy(s_seamData, 0, s_seamPrev, 0, s_seamData.Length); s_seamTex.SetPixelData(s_seamData, 0); s_seamTex.Apply(false, false); }
            Shader.SetGlobalTexture(Id_SeamTex, s_seamTex);
            Shader.SetGlobalVector(Id_SeamOrigin, origin);
            s_seamPublished = true;
        }

        private static void SeamOff()
        {
            Shader.SetGlobalVector(Id_SeamOrigin, Vector4.zero);
            s_seamPublished = false;
        }

        private static void ResetVertices() { foreach (var v in s_views.Values) if (v.Shown) SetVertices(v); }

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || On != null) return;
            On = config.Bind("Far", "FarTerrain", false,
                "EBM draws the far ground itself (out to the far reach), in its real biome colours, with terrain edits from the terrain mod when it offers them; "
                + "the game's distant terrain is switched off once EBM's covers the view. Off = the game's distant terrain. Live.");
            On.SettingChanged += (_, __) => { if (!On.Value) Shutdown("key turned off"); else EasyBakeLog.Info("[FarTerrain] on: EBM's far ground builds now"); };
            ExactSeam = config.Bind("Far", "Far Terrain Exact Seam", false,
                "1.2.440: far ground keeps its true heights everywhere (no 80 m sink under loaded zones) and the far-ground shader hides it, pixel by pixel, "
                + "in every zone where the game's near ground chunk is drawn this frame (zone mask rebuilt just before the camera renders), so no trench opens "
                + "along the loaded-zone edge. Off = the 80 m sink, as before. Live.");
            ExactSeam.SettingChanged += (_, __) => { ResetVertices(); if (!ExactSeam.Value) SeamOff(); EasyBakeLog.Info($"[FarTerrain] Exact Seam {(ExactSeam.Value ? "on" : "off")}"); };
            if (!s_registered) { s_registered = true; FarRunner.Register("FarTerrain", Update, null); Camera.onPreCull += SeamPreCull; }
            EasyBakeLog.Info($"[FarTerrain] {(On.Value ? "on" : "off")} at start ([Far] FarTerrain)");
        }

        // ---------------- main thread ----------------
        private static void Update()
        {
            if (On == null || !On.Value || s_failed) return;
            try { UpdateInner(); }
            catch (Exception e)
            {
                s_failed = true;
                EasyBakeLog.Warn($"[FarTerrain] failed and is off until the game restarts (the game's distant terrain is back): {e.GetType().Name}: {e.Message}");
                EasyBakeLog.Debug("[FarTerrain] failure: " + e);
                Shutdown("failure");
            }
        }

        private static void UpdateInner()
        {
            if (ZNetScene.instance == null || ZoneSystem.instance == null || WorldGenerator.instance == null || ZNet.instance == null)
            {
                if (s_views.Count > 0 || s_lodOff) Shutdown("no world");
                return;
            }
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (s_mat == null && !MakeMaterial()) return;
            EnsureThread();
            FollowFat();
            Vector3 p = cam.transform.position;
            if (float.IsNaN(s_planAt.x) || Mathf.Max(Mathf.Abs(p.x - s_planAt.x), Mathf.Abs(p.z - s_planAt.z)) > Replan) Plan(p);
            Fill(2.0);
            PollLoaded(p);
            HandOff();
            Report();
        }

        private static float Reach => Mathf.Clamp(FarReach.Or(3000f), 800f, 6000f);

        private static void Plan(Vector3 p)
        {
            s_planAt = new Vector3(Mathf.Round(p.x / 8f) * 8f, 0f, Mathf.Round(p.z / 8f) * 8f);
            s_wanted.Clear();
            float reach = Reach, top = BaseSpacing * (1 << MaxLevel) * Q;
            int x0 = Mathf.FloorToInt((s_planAt.x - reach) / top), x1 = Mathf.FloorToInt((s_planAt.x + reach) / top);
            int z0 = Mathf.FloorToInt((s_planAt.z - reach) / top), z1 = Mathf.FloorToInt((s_planAt.z + reach) / top);
            for (int x = x0; x <= x1; x++) for (int z = z0; z <= z1; z++) Visit(new Key(MaxLevel, x, z), reach);
            // drop views no longer wanted, queue the missing ones nearest first
            var drop = new List<Key>();
            foreach (var kv in s_views) if (!s_wanted.Contains(kv.Key)) drop.Add(kv.Key);
            // keep old views until the new set is shown (no hole): only drop the ones whose area a shown wanted view already covers is costly to test,
            // so drop now only when this is not the first plan and every wanted view is already built; otherwise drop at the swap in Fill.
            s_pendingDrop.Clear(); s_pendingDrop.AddRange(drop);
            var need = new List<Key>();
            foreach (var k in s_wanted) if (!s_views.TryGetValue(k, out var v) || v.Epoch != s_epoch) need.Add(k);
            need.Sort((a, b) => Dist(a, s_planAt).CompareTo(Dist(b, s_planAt)));
            lock (s_lock) { s_queue = need; Monitor.PulseAll(s_lock); }
        }
        private static readonly List<Key> s_pendingDrop = new List<Key>();

        private static void Visit(Key k, float reach)
        {
            if (!Overlaps(k, s_planAt, reach)) return;
            if (Dist(k, s_planAt) > reach) return;
            if (k.L > 0 && Dist(k, s_planAt) < SplitFactor * k.Size)
            {
                for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) Visit(new Key(k.L - 1, k.X * 2 + i, k.Z * 2 + j), reach);
                return;
            }
            s_wanted.Add(k);
        }

        private static bool Overlaps(Key k, Vector3 c, float r) => k.MinX < c.x + r && k.MinX + k.Size > c.x - r && k.MinZ < c.z + r && k.MinZ + k.Size > c.z - r;
        private static float Dist(Key k, Vector3 c)
        {
            float dx = Mathf.Max(Mathf.Max(k.MinX - c.x, 0f), c.x - (k.MinX + k.Size));
            float dz = Mathf.Max(Mathf.Max(k.MinZ - c.z, 0f), c.z - (k.MinZ + k.Size));
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static void Fill(double budgetMs)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            while (s_ready.TryDequeue(out var d))
            {
                if (d.Epoch == s_epoch && s_wanted.Contains(d.K)) Show(d);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * tick > budgetMs) break;
            }
            // the old views go once every wanted view is up (no frame without ground)
            if (s_pendingDrop.Count > 0 && AllWantedShown())
            {
                foreach (var k in s_pendingDrop) if (s_views.TryGetValue(k, out var v)) { Release(v); s_views.Remove(k); }
                s_pendingDrop.Clear();
            }
        }

        private static bool AllWantedShown()
        {
            if (s_wanted.Count == 0) return false;
            foreach (var k in s_wanted) if (!s_views.TryGetValue(k, out var v) || v.Epoch != s_epoch) return false;
            return true;
        }

        private static void Show(TileData d)
        {
            if (!s_views.TryGetValue(d.K, out var v)) { v = s_pool.Count > 0 ? s_pool.Pop() : NewView(); s_views[d.K] = v; }
            v.K = d.K; v.H = d.H; v.Print = d.Print; v.Epoch = d.Epoch;
            if (v.R.sharedMaterial != s_mat) v.R.sharedMaterial = s_mat;   // a pooled view from before a world change holds the old (destroyed) material
            if (d.C == null)
            {
                d.C = new Color32[GridVerts];
                for (int i = 0; i < GridVerts; i++) d.C[i] = SectorColour(d.S[i]);
            }
            float s = d.K.Spacing, half = d.K.Size * 0.5f;
            v.Go.transform.position = new Vector3(d.K.MinX + half, 0f, d.K.MinZ + half);
            var cols = new Color32[VertCount]; var uvs = new Vector2[VertCount];
            for (int i = 0; i < V; i++) for (int j = 0; j < V; j++) { int n = i * V + j; cols[n] = d.C[n]; uvs[n] = new Vector2(j / (float)Q, i / (float)Q); }
            for (int k = 0; k < 4 * V; k++) { int src = SkirtSource(k); cols[GridVerts + k] = cols[src]; uvs[GridVerts + k] = uvs[src]; }
            v.Mesh.Clear();
            SetVertices(v);
            v.Mesh.colors32 = cols; v.Mesh.uv = uvs;
            v.Mesh.SetTriangles(s_indices ?? (s_indices = Indices()), 0, true);
            v.Mesh.RecalculateNormals(); v.Mesh.RecalculateTangents();
            v.Mask.SetPixels(d.M); v.Mask.Apply(false, false);
            v.R.GetPropertyBlock(s_mpb);
            s_mpb.SetFloat(Id_Hide, HugeHide);
            s_mpb.SetTexture(Id_Mask, v.Mask);
            v.R.SetPropertyBlock(s_mpb);
            v.Go.SetActive(true); v.Shown = true; s_shownCount++;
        }

        // vertices with the sink under loaded zones; renderer off when every grid vertex is sunk
        private static void SetVertices(View v)
        {
            var verts = new Vector3[VertCount];
            float s = v.K.Spacing, half = v.K.Size * 0.5f, minX = v.K.MinX, minZ = v.K.MinZ;
            int sunk = 0;
            bool exact = ExactSeam != null && ExactSeam.Value;   // 1.2.440: true heights; the shader clips under near chunks. sunk still counts for the renderer-off test
            for (int i = 0; i < V; i++)
                for (int j = 0; j < V; j++)
                {
                    int n = i * V + j;
                    float wx = minX + j * s, wz = minZ + i * s;
                    float y = v.H[n];
                    // sink only when the vertex AND its neighbours one spacing away are under loaded ground, so every slanted triangle stays under the
                    // near ground (a vertex on the loaded edge sinking 80 m would open a trench just outside it); the edge band drops 1 m (no z-fight)
                    if (UnderLoaded(wx, wz))
                    {
                        if (UnderLoaded(wx - s, wz) && UnderLoaded(wx + s, wz) && UnderLoaded(wx, wz - s) && UnderLoaded(wx, wz + s)) { if (!exact) y -= SinkDepth; sunk++; }
                        else if (!exact) y -= 1f;
                    }
                    verts[n] = new Vector3(j * s - half, y, i * s - half);
                }
            float drop = 2f * s;
            for (int k = 0; k < 4 * V; k++) verts[GridVerts + k] = verts[SkirtSource(k)] + Vector3.down * drop;
            v.Mesh.vertices = verts;
            v.Mesh.RecalculateBounds();
            v.R.enabled = sunk < GridVerts;
        }

        // a vertex is under loaded ground when every zone it touches is loaded (zone centres at multiples of 64, edges at +-32)
        private static bool UnderLoaded(float wx, float wz)
        {
            var zs = ZoneSystem.instance;
            int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
            bool ex = Mathf.Approximately(Mathf.Repeat(wx + 32f, 64f), 0f), ez = Mathf.Approximately(Mathf.Repeat(wz + 32f, 64f), 0f);
            if (!zs.IsZoneLoaded(new Vector2s(zx, zz))) return false;
            if (ex && !zs.IsZoneLoaded(new Vector2s(zx - 1, zz))) return false;
            if (ez && !zs.IsZoneLoaded(new Vector2s(zx, zz - 1))) return false;
            if (ex && ez && !zs.IsZoneLoaded(new Vector2s(zx - 1, zz - 1))) return false;
            return true;
        }

        private static void PollLoaded(Vector3 p)
        {
            if (Time.unscaledTime < s_nextLoadedPoll) return;
            s_nextLoadedPoll = Time.unscaledTime + 0.5f;
            var zs = ZoneSystem.instance; var pz = ZoneSystem.GetZone(p);
            int h = pz.x * 7919 + pz.y * 104729;
            for (int dz = -10; dz <= 10; dz++) for (int dx = -10; dx <= 10; dx++)
                if (zs.IsZoneLoaded(new Vector2s(pz.x + dx, pz.y + dz))) h = h * 31 + (dx + 11) * 23 + dz;
            if (h == s_loadedHash) return;
            s_loadedHash = h;
            float near = 11f * 64f + 64f;
            foreach (var v in s_views.Values) if (v.Shown && Dist(v.K, p) < near) SetVertices(v);
        }

        private static void HandOff()
        {
            if (s_lodOff || !AllWantedShown()) return;
            s_lods.Clear();
            foreach (var l in UnityEngine.Object.FindObjectsByType<TerrainLod>(FindObjectsSortMode.None)) if (l != null && l.enabled) { l.enabled = false; s_lods.Add(l); }
            s_lodOff = true;
            if (!s_saidReady) { s_saidReady = true; EasyBakeLog.Info($"[FarTerrain] EBM's far ground covers the view ({s_wanted.Count} tiles to {Reach:0} m, data from {(Fat.Height != null ? "FAT's far data API" : "the game's world generator")}); the game's distant terrain is off ({s_lods.Count} TerrainLod)"); }
        }

        private static void Shutdown(string why)
        {
            lock (s_lock) { s_queue = new List<Key>(); }
            s_epoch++;
            foreach (var v in s_views.Values) Release(v);
            s_views.Clear(); s_wanted.Clear(); s_pendingDrop.Clear();
            while (s_ready.TryDequeue(out _)) { }
            s_planAt = new Vector3(float.NaN, 0f, float.NaN);
            if (s_lodOff)
            {
                int n = 0;
                foreach (var l in s_lods) if (l != null) { l.enabled = true; n++; }
                s_lods.Clear(); s_lodOff = false;
                EasyBakeLog.Info($"[FarTerrain] the game's distant terrain is back ({n} TerrainLod): {why}");
            }
            if (s_mat != null) { UnityEngine.Object.Destroy(s_mat); s_mat = null; }
            s_sectorColour.Clear(); s_saidReady = false;
        }

        private static void Release(View v) { if (v.Go != null) v.Go.SetActive(false); v.Shown = false; s_pool.Push(v); }

        private static View NewView()
        {
            var go = new GameObject("FiresEBM_FarTerrainTile") { hideFlags = HideFlags.DontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            var mf = go.AddComponent<MeshFilter>(); var mr = go.AddComponent<MeshRenderer>();
            var mesh = new Mesh { name = "FiresEBM_FarTerrain" };
            mesh.indexFormat = IndexFormat.UInt16;
            mf.sharedMesh = mesh;
            mr.sharedMaterial = s_mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
            mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            var mask = new Texture2D(V, V, TextureFormat.RGBA32, false, false) { name = "FiresEBM_FarTerrainMask"   /* sRGB, as vanilla's paint mask (Heightmap.cs:406 new Texture2D(num, num)) */, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            return new View { Go = go, R = mr, Mesh = mesh, Mask = mask };
        }

        // a copy of the near ground's material template on EBM/Heightmap, not distant (the far look is the global _FiresFarLook)
        private static bool MakeMaterial()
        {
            var shader = Indirect.EbmIndirectAssets.Terrain(out string why);
            if (shader == null) { if (!s_saidNoShader) { s_saidNoShader = true; EasyBakeLog.Info($"[FarTerrain] on, but EBM/Heightmap is not available ({why}): the game's distant terrain stays"); } return false; }
            Material template = null;
            foreach (var hm in Heightmap.GetAllHeightmaps()) if (hm != null && !hm.IsDistantLod && hm.m_material != null) { template = hm.m_material; break; }
            if (template == null) return false;
            s_mat = new Material(template) { name = "FiresEBM_FarTerrain", shader = shader, hideFlags = HideFlags.DontSave };
            s_mat.DisableKeyword("_ISDISTANTLOD_ON");
            if (s_mat.HasProperty(Id_Distant)) s_mat.SetFloat(Id_Distant, 0f);
            s_mat.SetFloatArray("_depth", new float[4]);
            s_mat.SetFloat(Id_FarTile, 1f);   // 1.2.440: marks the far tiles for the Exact Seam clip (near chunks never carry it; the clip also needs the global's w = 1)
            EasyBakeLog.Info($"[FarTerrain] material: a copy of '{template.name}' on EBM/Heightmap (texture arrays carried over)");
            return true;
        }
        private static bool s_saidNoShader;

        private static Color32 SectorColour(BiomeSector s)
        {
            if (s == null) return new Color32(0, 0, 0, 0);
            if (!s_sectorColour.TryGetValue(s, out var c)) { c = Heightmap.GetBiomeColor(s); s_sectorColour[s] = c; }
            return c;
        }

        private static void FollowFat()
        {
            Fat.Bind();
            if (Fat.Revision == null) return;
            int r; try { r = Fat.Revision(); } catch { return; }
            if (r == s_fatRev) return;
            bool first = s_fatRev == int.MinValue; s_fatRev = r;
            if (first) return;
            s_epoch++; Interlocked.Exchange(ref s_threadEpochBox, s_epoch);   // every tile rebuilds with the new edits; old views stay until replaced
            s_planAt = new Vector3(float.NaN, 0f, float.NaN);
            EasyBakeLog.Debug($"[FarTerrain] FAT far edits changed (revision {r}): far tiles rebuild");
        }
        private static int s_threadEpochBox = 1;

        private static void Report()
        {
            if (Time.unscaledTime < s_nextReport) return;
            s_nextReport = Time.unscaledTime + 30f;
            int q; lock (s_lock) q = s_queue.Count;
            EasyBakeLog.Debug($"[FarTerrain] last 30 s: {s_wanted.Count} wanted, {s_views.Count} views, {s_shownCount} shown, {s_built} built, {s_fromDisk} from disk, queue {q}, game distant terrain {(s_lodOff ? "off" : "on")}");
            s_shownCount = 0;
        }

        // ---------------- builder thread ----------------
        private static void EnsureThread()
        {
            Interlocked.Exchange(ref s_threadEpochBox, s_epoch);
            if (s_thread != null && s_thread.IsAlive) return;
            try
            {
                long uid = ZNet.instance.GetWorldUID(); int seed = WorldGenerator.instance.GetSeed();
                s_cacheDir = Path.Combine(BepInEx.Paths.CachePath, "FiresEBM", "farterrain", $"{seed}_{uid:x16}");
                Directory.CreateDirectory(s_cacheDir);
            }
            catch (Exception e) { s_cacheDir = null; EasyBakeLog.Debug("[FarTerrain] no disk cache: " + e.Message); }
            s_stop = false;
            s_thread = new Thread(Run) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "FiresEBM far terrain" };
            s_thread.Start();
        }

        private static void Run()
        {
            while (!s_stop)
            {
                Key k = default; bool have = false;
                lock (s_lock)
                {
                    if (s_queue.Count > 0) { k = s_queue[0]; s_queue.RemoveAt(0); have = true; }
                    else Monitor.Wait(s_lock, 250);
                }
                if (!have) continue;
                try
                {
                    int epoch = Volatile.Read(ref s_threadEpochBox);
                    var d = Build(k); d.Epoch = epoch;
                    s_ready.Enqueue(d);
                }
                catch (Exception e) { EasyBakeLog.Debug($"[FarTerrain] tile {k.L}/{k.X}/{k.Z} failed: {e.Message}"); }
            }
        }

        private static TileData Build(Key k)
        {
            var wg = WorldGenerator.instance;
            float s = k.Spacing, minX = k.MinX, minZ = k.MinZ;
            uint print = EditPrint(k, out var deltas);
            if (TryRead(k, print, out var cached)) { Interlocked.Increment(ref s_fromDisk); return cached; }
            var d = new TileData { K = k, Print = print, H = new float[GridVerts], M = new Color[GridVerts] };
            bool fatColour = Fat.Color != null;
            if (fatColour) d.C = new Color32[GridVerts]; else d.S = new BiomeSector[GridVerts];
            for (int i = 0; i < V; i++)
                for (int j = 0; j < V; j++)
                {
                    int n = i * V + j;
                    float wx = minX + j * s, wz = minZ + i * s;
                    var biome = wg.GetBiome(wx, wz);
                    float h = wg.GetBiomeHeight(biome, wx, wz, out Color mask);   // vanilla's distant method (HeightmapBuilder.Build, m_distantLod)
                    if (Fat.Height != null) { try { if (Fat.Height(wx, wz, out float fh)) h = fh; } catch { } }
                    if (deltas != null) h += DeltaAt(deltas, wx, wz);
                    d.H[n] = h; d.M[n] = mask;
                    if (fatColour) { Color32 c = default; bool ok = false; try { ok = Fat.Color(wx, wz, out c); } catch { } d.C[n] = ok ? c : default; if (!ok) { d.C = null; d.S = new BiomeSector[GridVerts]; fatColour = false; for (int b = 0; b <= n; b++) d.S[b] = wg.GetBiomeSector(minX + (b % V) * s, minZ + (b / V) * s); } }
                    else d.S[n] = wg.GetBiomeSector(wx, wz);
                }
            Interlocked.Increment(ref s_built);
            if (d.C != null) TryWrite(d);   // colours from FAT are thread-built and cacheable; sector colours are made on the main thread
            return d;
        }

        // the zones a tile's vertices fall in that FAT reports an edit for (only where the tile spacing can show an edit: levels 0-1)
        private static uint EditPrint(Key k, out Dictionary<long, float[]> deltas)
        {
            deltas = null;
            if (Fat.EditDelta == null || k.L > 1) return 0u;
            uint print = 2166136261u; bool any = false;
            int zx0 = Mathf.FloorToInt((k.MinX + 32f) / 64f), zx1 = Mathf.FloorToInt((k.MinX + k.Size + 32f) / 64f);
            int zz0 = Mathf.FloorToInt((k.MinZ + 32f) / 64f), zz1 = Mathf.FloorToInt((k.MinZ + k.Size + 32f) / 64f);
            for (int zz = zz0; zz <= zz1; zz++)
                for (int zx = zx0; zx <= zx1; zx++)
                {
                    var arr = new float[GridVerts]; uint p;
                    bool ok; try { ok = Fat.EditDelta(zx, zz, arr, out p); } catch { ok = false; p = 0; }
                    if (!ok) continue;
                    any = true;
                    (deltas ?? (deltas = new Dictionary<long, float[]>()))[((long)zx << 32) | (uint)zz] = arr;
                    print = (print ^ (uint)zx) * 16777619; print = (print ^ (uint)zz) * 16777619; print = (print ^ p) * 16777619;
                }
            return any ? (print == 0 ? 1u : print) : 0u;
        }

        // FAT's layout: 65 x 65, 1 m apart, row-major z*65+x, vertex 0 at (zone*64-32); bilinear inside the zone
        private static float DeltaAt(Dictionary<long, float[]> deltas, float wx, float wz)
        {
            int zx = Mathf.FloorToInt((wx + 32f) / 64f), zz = Mathf.FloorToInt((wz + 32f) / 64f);
            if (!deltas.TryGetValue(((long)zx << 32) | (uint)zz, out var a)) return 0f;
            float lx = wx - (zx * 64f - 32f), lz = wz - (zz * 64f - 32f);
            int ix = Mathf.Clamp((int)lx, 0, 63), iz = Mathf.Clamp((int)lz, 0, 63);
            float fx = lx - ix, fz = lz - iz;
            float a0 = a[iz * V + ix], a1 = a[iz * V + ix + 1], b0 = a[(iz + 1) * V + ix], b1 = a[(iz + 1) * V + ix + 1];
            return Mathf.Lerp(Mathf.Lerp(a0, a1, fx), Mathf.Lerp(b0, b1, fx), fz);
        }

        // ---------------- disk cache (heights + colours + mask; only tiles whose colours were built off the main thread) ----------------
        private static string CachePath(Key k, uint print) => s_cacheDir == null ? null : Path.Combine(s_cacheDir, $"v{CacheVersion}_{k.L}_{k.X}_{k.Z}_{print:x8}.bin");

        private static bool TryRead(Key k, uint print, out TileData d)
        {
            d = null;
            string path = CachePath(k, print);
            if (path == null || !File.Exists(path)) return false;
            try
            {
                using (var br = new BinaryReader(File.OpenRead(path)))
                {
                    if (br.ReadInt32() != CacheVersion || br.ReadInt32() != GridVerts) return false;
                    d = new TileData { K = k, Print = print, H = new float[GridVerts], C = new Color32[GridVerts], M = new Color[GridVerts] };
                    for (int i = 0; i < GridVerts; i++) d.H[i] = br.ReadSingle();
                    for (int i = 0; i < GridVerts; i++) { uint c = br.ReadUInt32(); d.C[i] = new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)); }
                    for (int i = 0; i < GridVerts; i++) d.M[i] = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                }
                return true;
            }
            catch { d = null; try { File.Delete(path); } catch { } return false; }
        }

        private static void TryWrite(TileData d)
        {
            string path = CachePath(d.K, d.Print);
            if (path == null) return;
            try
            {
                string tmp = path + ".tmp";
                using (var bw = new BinaryWriter(File.Create(tmp)))
                {
                    bw.Write(CacheVersion); bw.Write(GridVerts);
                    for (int i = 0; i < GridVerts; i++) bw.Write(d.H[i]);
                    for (int i = 0; i < GridVerts; i++) { var c = d.C[i]; bw.Write((uint)(c.r | (c.g << 8) | (c.b << 16) | (c.a << 24))); }
                    for (int i = 0; i < GridVerts; i++) { var m = d.M[i]; bw.Write(m.r); bw.Write(m.g); bw.Write(m.b); bw.Write(m.a); }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { EasyBakeLog.Debug("[FarTerrain] cache write skipped: " + e.Message); }
        }

        // ---------------- mesh layout (own code: a 64 x 64 quad grid and a skirt on each edge) ----------------
        private static int SkirtSource(int k)
        {
            int side = k / V, t = k % V;
            switch (side) { case 0: return t; case 1: return Q * V + t; case 2: return t * V; default: return t * V + Q; }
        }

        private static int[] Indices()
        {
            var idx = new List<int>(Q * Q * 6 + 4 * Q * 12);
            for (int i = 0; i < Q; i++)
                for (int j = 0; j < Q; j++)
                {
                    int a = i * V + j, b = a + 1, c = a + V, d = c + 1;
                    idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d);
                }
            for (int side = 0; side < 4; side++)
                for (int t = 0; t < Q; t++)
                {
                    int s0 = SkirtSource(side * V + t), s1 = SkirtSource(side * V + t + 1);
                    int d0 = GridVerts + side * V + t, d1 = d0 + 1;
                    // both windings: the skirt is seen from whichever side the gap is on
                    idx.Add(s0); idx.Add(d0); idx.Add(s1); idx.Add(s1); idx.Add(d0); idx.Add(d1);
                    idx.Add(s0); idx.Add(s1); idx.Add(d0); idx.Add(s1); idx.Add(d1); idx.Add(d0);
                }
            return idx.ToArray();
        }

        // ---------------- FatApi far data (FAT 0.2.1444+), by name ----------------
        private static class Fat
        {
            internal delegate bool HeightFn(float x, float z, out float h);
            internal delegate bool DeltaFn(int zx, int zz, float[] d, out uint print);
            internal delegate bool ColorFn(float x, float z, out Color32 c);
            internal static HeightFn Height; internal static DeltaFn EditDelta; internal static ColorFn Color; internal static Func<int> Revision;
            private static bool s_bound;
            internal static void Bind()
            {
                if (s_bound) return; s_bound = true;
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { t = asm.GetType("VerdantsAscent.FatApi", false); } catch { } if (t != null) break; }
                if (t == null) { EasyBakeLog.Info("[FarTerrain] no FAT far data API: far ground from the game's world generator (no terrain edits far out)"); return; }
                var ver = t.GetField("FarDataApiVersion", BindingFlags.Public | BindingFlags.Static);
                if (ver == null) { EasyBakeLog.Info("[FarTerrain] FAT has no far data API (FarDataApiVersion): far ground from the game's world generator"); return; }
                try
                {
                    var fires = t.GetProperty("FarFiresWorld", BindingFlags.Public | BindingFlags.Static);
                    Height = Make<HeightFn>(t, "TryFarHeight"); EditDelta = Make<DeltaFn>(t, "TryFarEditDelta"); Color = Make<ColorFn>(t, "TryFarColor");
                    // 1.2.420 ([visual]/[worldgen] 11-16, Fire's lost near snow): FAT 1444/1445 TryFarColor called the vanilla Heightmap.GetBiomeColor overloads,
                    // which Vedr patches with a NON-atomic static paint counter (Vedr LandscapeSeasons.cs:385-396); called from this builder thread the ++/--
                    // race left it off by one and the main thread's chunk paints lost their winter white. FAT's colours are used off the main thread only from
                    // FarDataApiVersion 2 (FAT's promise: no patched vanilla calls); below that the colours come from biome sectors on the main thread.
                    int apiVer = 0; try { apiVer = System.Convert.ToInt32(ver.GetValue(null)); } catch { }
                    if (apiVer < 2 && Color != null) { Color = null; EasyBakeLog.Info($"[FarTerrain] FAT far data API v{apiVer}: its colours are not used (they are thread-safe only from v2); far colours come from the game's biome colours on the main thread"); }
                    var rev = t.GetProperty("FarEditRevision", BindingFlags.Public | BindingFlags.Static);
                    if (rev != null) Revision = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), rev.GetGetMethod());
                    EasyBakeLog.Info($"[FarTerrain] FAT far data API v{ver.GetValue(null)}: heights {(Height != null ? "yes" : "no")}, edits {(EditDelta != null ? "yes" : "no")}, colours {(Color != null ? "yes" : "no")}, revision {(Revision != null ? "yes" : "no")}");
                }
                catch (Exception e) { Height = null; EditDelta = null; Color = null; Revision = null; EasyBakeLog.Warn("[FarTerrain] FAT far data API could not be bound: " + e.Message); }
            }
            private static T Make<T>(Type t, string name) where T : class
            {
                var inv = typeof(T).GetMethod("Invoke"); var ps = inv.GetParameters(); var types = new Type[ps.Length];
                for (int i = 0; i < ps.Length; i++) types[i] = ps[i].ParameterType;
                var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, types, null);
                return m == null ? null : Delegate.CreateDelegate(typeof(T), m) as T;
            }
        }
    }
}
