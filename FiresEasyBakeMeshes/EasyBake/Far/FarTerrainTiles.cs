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
        // 1.2.485 ([visual], design 6b fix (b), [lead] GO, default OFF until the super-flat look leg decides): far tiles at their TRUE height under loaded zones (no 1.5 m
        // BandSink) and pushed back in DEPTH instead (EbmHeightmap 1.2.485 Offset [_EbmFarOffsetFactor], [_EbmFarOffsetUnits]), so the near ground still wins where both
        // draw and nothing steps or sinks under the sea. Only when the loaded shader has the property (a 1.2.485+ bundle); else the sink stays and one line says why.
        internal static ConfigEntry<bool> DepthPriority;
        private const float DepthOffsetFactor = 1f, DepthOffsetUnits = 8f;   // UNCONFIRMED values: the look leg tunes them
        private static readonly int Id_OffFactor = Shader.PropertyToID("_EbmFarOffsetFactor"), Id_OffUnits = Shader.PropertyToID("_EbmFarOffsetUnits");
        private static bool DepthPriorityActive => DepthPriority != null && DepthPriority.Value && s_mat != null && s_mat.HasProperty(Id_OffUnits);
        private const int Q = 64, V = 65, GridVerts = V * V, VertCount = GridVerts + 4 * V;
        private const int MaxLevel = 5;
        private const float BaseSpacing = 8f, SplitFactor = 2f, Replan = 256f, SinkDepth = 80f, BandSink = 1.5f, HugeHide = 1e7f;
        private const int CacheVersion = 1;

        internal struct Key : IEquatable<Key>   // 1.2.501: internal (was private) so VedrFar's internal members can take it
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
            public Heightmap.Biome[] B; public float[] BH;   // 1.2.501: biome + BASE height (world-gen, FAT near-blend when FAT answers, before edits) per vertex, in memory only (the owner's far snow), null when no source
            public int Content;        // 1.2.504: the content epoch the tile was built under (colour mode / FAT paint / world); an edit revision alone does not change it
            public bool Keep;          // 1.2.504: the shown view already holds this exact tile (same key, edit print, content epoch): no rebuild (H etc. null unless a Debug compare)
        }

        internal sealed class View   // 1.2.501: internal (was private) so VedrFar's internal members can take it
        {
            public Key K; public GameObject Go; public MeshRenderer R; public Mesh Mesh; public Texture2D Mask;
            public float[] H; public bool Shown; public uint Print; public int Epoch;
            public Color[] M; public Heightmap.Biome[] B; public float[] BH; public Color[] Up;   // 1.2.501: the cached world-gen mask, biomes, base heights, the upload copy
            public int Content;   // 1.2.504: TileData.Content of what it shows
        }

        // ---------------- 1.2.504 DEEP-ROW FIX ([visual], [lead] 11-23, allocation capture (rig path), Fire's VADNtest route, Vedr .26 / EBM 1.2.502) ----------------
        // Capture row: "[FiresEBM far terrain] 859.6 MB = 52.9% of 1,624.5 MB in 45 s; heaviest stack 793.8 MB in 49,162 allocations of EXACTLY 16,932 bytes" (a float[4225]
        // + header): EditPrint's `new float[GridVerts]` for EVERY zone under a level 0-1 tile (81 / 289 per tile), on every tile build, cached tiles included (EditPrint ran
        // before the disk read), thrown away whenever FAT had no edit there. And the rebuild rate behind it: every FAT edit revision (5 in 30 s on the stretch, 54 in the
        // session) re-epoched ALL ~107 tiles, so each re-read its file (H + C + M = 101 KB new arrays) and re-uploaded, although only the tiles over the edited zone changed.
        // Now (same tile content, nothing else changes):
        //  1. EditPrint fills ONE scratch array (builder thread only) and keeps it only when FAT returns an edit (FAT's TryZoneDelta writes all 4225 cells on true and
        //     leaves the array untouched on false, FAT DistantHeightCache.TryZoneDelta), so a zone without an edit allocates nothing.
        //  2. Every per-tile array (H, M, C, B, BH and the kept deltas) is rented from TilePool and handed back when its tile is replaced, dropped or discarded.
        //  3. Build once: a tile whose shown view already has the same key, edit print and content epoch (colour mode / FAT paint / world unchanged; an edit revision
        //     alone does not touch it) is KEPT, not rebuilt: the builder posts a keep marker and the main thread only moves the view to the new epoch. With Debug on,
        //     1 keep in 16 is built anyway and compared with the shown view (heights + mask, bit for bit): the 60 s line counts compares and mismatches.
        // Proof: one Info line per 60 s while far tiles build (tiles built / from cache / kept, arrays rented / new, MB of new tile arrays).
        private static class TilePool
        {
            private const int Cap = 768;   // per array kind: ~110 tiles x their arrays plus kept deltas; beyond that a returned array is left to the GC
            private static readonly ConcurrentBag<float[]> s_f = new ConcurrentBag<float[]>();
            private static readonly ConcurrentBag<Color[]> s_m = new ConcurrentBag<Color[]>();
            private static readonly ConcurrentBag<Color32[]> s_c = new ConcurrentBag<Color32[]>();
            private static readonly ConcurrentBag<Heightmap.Biome[]> s_b = new ConcurrentBag<Heightmap.Biome[]>();
            private static int s_fn, s_mn, s_cn, s_bn;   // pooled counts (ConcurrentBag.Count locks every thread's list)
            internal static long Rented, Fresh, FreshBytes;
            private static void Took() => Interlocked.Increment(ref Rented);
            private static void Made(long bytes) { Interlocked.Increment(ref Fresh); Interlocked.Add(ref FreshBytes, bytes + 32); }
            internal static float[] F() { if (s_f.TryTake(out var a)) { Interlocked.Decrement(ref s_fn); Took(); return a; } Made(GridVerts * 4L); return new float[GridVerts]; }
            internal static Color[] M() { if (s_m.TryTake(out var a)) { Interlocked.Decrement(ref s_mn); Took(); return a; } Made(GridVerts * 16L); return new Color[GridVerts]; }
            internal static Color32[] C() { if (s_c.TryTake(out var a)) { Interlocked.Decrement(ref s_cn); Took(); return a; } Made(GridVerts * 4L); return new Color32[GridVerts]; }
            internal static Heightmap.Biome[] B() { if (s_b.TryTake(out var a)) { Interlocked.Decrement(ref s_bn); Took(); return a; } Made(GridVerts * 4L); return new Heightmap.Biome[GridVerts]; }
            internal static void Give(float[] a) { if (a != null && a.Length == GridVerts && Volatile.Read(ref s_fn) < Cap) { s_f.Add(a); Interlocked.Increment(ref s_fn); } }
            internal static void Give(Color[] a) { if (a != null && a.Length == GridVerts && Volatile.Read(ref s_mn) < Cap) { s_m.Add(a); Interlocked.Increment(ref s_mn); } }
            internal static void Give(Color32[] a) { if (a != null && a.Length == GridVerts && Volatile.Read(ref s_cn) < Cap) { s_c.Add(a); Interlocked.Increment(ref s_cn); } }
            internal static void Give(Heightmap.Biome[] a) { if (a != null && a.Length == GridVerts && Volatile.Read(ref s_bn) < Cap) { s_b.Add(a); Interlocked.Increment(ref s_bn); } }
            /// <summary>Every array a TileData still owns (a discarded or kept tile; Show hands over H / M / B / BH first and nulls them).</summary>
            internal static void Give(TileData d) { if (d == null) return; Give(d.H); Give(d.M); Give(d.C); Give(d.B); Give(d.BH); d.H = null; d.M = null; d.C = null; d.B = null; d.BH = null; }
        }
        private static float[] s_editScratch;   // builder thread only (EditPrint)
        private static readonly ConcurrentDictionary<Key, long> s_shownSig = new ConcurrentDictionary<Key, long>();   // shown view: (content epoch << 32) | edit print; the builder reads it
        private static readonly ConcurrentDictionary<Key, byte> s_forceBuild = new ConcurrentDictionary<Key, byte>();   // a keep marker that found no matching view: build this key for real
        private static int s_contentEpoch = 1, s_contentEpochBox = 1, s_keepSeq;
        private static long s_wBuilt, s_wDisk, s_wKept, s_wCompares, s_wMismatch, s_lastRented, s_lastFresh, s_lastFreshBytes, s_wPrintChanged, s_wRevs;
        private static float s_nextProof;
        private static long Sig(int content, uint print) => ((long)content << 32) | print;
        // A colour mode / FAT paint / world change: shown tiles hold other content, so none of them may be kept (an edit revision does NOT call this)
        private static void NewContent() { s_contentEpoch++; Interlocked.Exchange(ref s_contentEpochBox, s_contentEpoch); }

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
        // 1.2.484 ([lead] 11-21): the "Far Terrain Exact Seam" key is retired (it chose nothing since 1.2.444): no longer bound, its saved cfg line stays, unread.
        private const int SeamSize = 64;
        private static Texture2D s_seamTex;
        private static readonly byte[] s_seamData = new byte[SeamSize * SeamSize], s_seamPrev = new byte[SeamSize * SeamSize];
        private static int s_seamFrame = -1;
        private static bool s_seamPublished;
        private static readonly int Id_SeamTex = Shader.PropertyToID("_EbmFarZoneMask"), Id_SeamOrigin = Shader.PropertyToID("_EbmFarZoneMaskOrigin"), Id_FarTile = Shader.PropertyToID("_EbmFarTile");
        // 1.2.444 ([visual], lead 11-18 player report: a sunken band / trench / missing ground close to the player at a low Simulation Distance; Fire: "vanilla
        // terrain already handles this just fine"): the game's near chunks dissolve by DISTANCE in the terrain shader (gone at L = max(1, synced near sim) x 90.5 m,
        // thinning from 0.7 L; Heightmap.cs:1030-1048 writes L into every chunk's renderer block), and vanilla's distant terrain fills exactly that ring. The old
        // hand-over was by ZONE (80 m sink under every loaded zone, or 1.2.440's per-zone clip), so wherever loaded zones reached past L (corners at sim 2, everything
        // past ~63 m at sim 0) neither drew the ground. Now always: true heights, and EBM/Heightmap drops a far pixel only where the near chunk keeps that same pixel
        // (zone drawn AND the same noise dither against the same L, published here as _EbmFarNearHide). The Exact Seam key no longer chooses anything.
        private static readonly int Id_NearHide = Shader.PropertyToID("_EbmFarNearHide");
        /// <summary>1.2.458: the far ground is wanted = its own toggle AND [03 - Far Rendering] Far Rendering (the master switch).</summary>
        internal static bool Wanted => On != null && On.Value && FarRendering.On;
        /// <summary>1.2.483 (EbmApi.FarGroundOwned): EBM's far tiles cover the view and every TerrainLod is switched off by HandOff (until Shutdown).</summary>
        internal static bool GroundOwned => s_lodOff;
        internal static int ShownViews { get { int n = 0; foreach (var v in s_views.Values) if (v.Shown) n++; return n; } }   // 1.2.484: FarZoneSet census
        private static bool SeamWanted => Wanted && !s_failed;
        internal static float NearHideDistance()
        {
            if (ZNet.instance == null || ZoneSystem.instance == null) return 0f;
            float diag = Mathf.Sqrt(ZoneSystem.instance.m_zoneSize * ZoneSystem.instance.m_zoneSize * 2f);   // vanilla Heightmap.GetLodHideDistance
            return Mathf.Max(1, ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance) * diag;
        }

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
            Shader.SetGlobalFloat(Id_NearHide, NearHideDistance());   // 1.2.444: the near chunks' own L, read the way vanilla computes it, every frame (the setting is live)
            s_seamPublished = true;
        }

        private static void SeamOff()
        {
            Shader.SetGlobalVector(Id_SeamOrigin, Vector4.zero);
            Shader.SetGlobalFloat(Id_NearHide, 0f);
            s_seamPublished = false;
        }

        private static void ResetVertices() { foreach (var v in s_views.Values) if (v.Shown) SetVertices(v); }

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || On != null) return;
            // 1.2.458 (Fire 11-18): [03 - Far Rendering] Far Terrain (was [03 - Far View] FarTerrain; EbmConfigMove carries a saved value), under the master switch
            On = EbmConfigMove.Bind(config, FarRendering.Section, "Far Terrain", false,
                "Draws the far ground (out to the far reach) in its real biome colours, with terrain edits from the terrain mod when it offers them; "
                + "the game's distant terrain is switched off once EBM's covers the view. Off = the game's distant terrain. Needs Far Rendering on. Live.");
            On.SettingChanged += (_, __) => KeyChanged("key turned off");
            DepthPriority = config.Bind("90 - Debugging", "Far Terrain Depth Priority", false,
                "Test switch (default off): EBM's far ground keeps its true height under the loaded area and gives way to the near ground by depth instead of sitting "
                + "1.5 m lower. Needs Far Terrain on and EBM's 1.2.485 shaders. Live.");
            DepthPriority.SettingChanged += (_, __) => { ApplyDepthPriority(); ResetVertices(); };
            // 1.2.497 (H1, [lead] 11-22, Fire: "we can go with V now, but we will build H"): with Badgers' HD Terrain loaded, the far tiles draw with HD's OWN shader and
            // HD's full arrays, so HD's look reaches the horizon (1.2.496 V = EBM/Heightmap on vanilla's arrays). Nothing of HD's code is copied: its shader and its
            // texture objects are used as HD made them. Default OFF until Fire's eyes pass it; off (or no HD) = exactly 1.2.496. Live: the far ground rebuilds.
            HdLook = EbmConfigMove.Bind(config, FarRendering.Section, "Far Terrain HD Look", false,
                "With Badgers' HD Terrain installed: the far ground is drawn by HD Terrain's own shader and textures, so it matches the near ground's HD look out to the horizon. "
                + "Off = the far ground uses the game's own terrain textures. No effect without HD Terrain. Live.");
            HdLook.SettingChanged += (_, __) => { if (HdPresent) Shutdown("Far Terrain HD Look changed: the far ground rebuilds"); };
            FarRendering.Changed += () => { if (On.Value) KeyChanged("Far Rendering turned off"); };   // own toggle off: already shut down by its own handler
            if (!s_registered) { s_registered = true; FarRunner.Register("FarTerrain", Update, null); Camera.onPreCull += SeamPreCull; }
            EasyBakeLog.Info($"[FarTerrain] {(Wanted ? "on" : "off")} at start ([03 - Far Rendering] Far Terrain {(On.Value ? "on" : "off")}, Far Rendering {(FarRendering.On ? "on" : "off")})");
        }

        // 1.2.458: the toggle's old SettingChanged body, shared with the master switch (FarRendering.Changed)
        private static void KeyChanged(string offWhy)
        {
            if (!Wanted) Shutdown(offWhy); else EasyBakeLog.Info("[FarTerrain] on: EBM's far ground builds now");
        }

        // ---------------- main thread ----------------
        private static void Update()
        {
            if (!Wanted || s_failed) return;
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
            CheckHdOurs();   // 1.2.506: one property read every 2 s, only with HD loaded
            if (s_mat == null) return;   // CheckHdOurs may have shut the far ground down for a rebuild
            EnsureThread();
            FollowFat();
            VedrFar.Push();   // 1.2.470: the far winter snow class globals (1.2.501: pushed values; at once after a push, else every 5 s)
            VedrFar.PumpReupload();   // 1.2.501: HD-look snow re-upload, a few tiles per frame (nothing when idle)
            Vector3 p = cam.transform.position;
            if (float.IsNaN(s_planAt.x) || Mathf.Max(Mathf.Abs(p.x - s_planAt.x), Mathf.Abs(p.z - s_planAt.z)) > Replan) Plan(p);
            Fill(2.0);
            PollLoaded(p);
            HandOff();
            Report();
            SeamCensus(cam, p);
        }

        // 1.2.443 ([visual], lead 11-18 far-seam bug, Debug only): every 5 s, the game's near terrain chunks against what the far tiles assume. The far tiles sink
        // (seam off) or clip (seam on) by "zone loaded" / "renderer enabled"; this counts chunks in loaded zones that are inside the camera frustum but were NOT drawn
        // last frame (Renderer.isVisible false), i.e. ground the far tiles hide while nothing else draws it. Read-only; nothing changes.
        private static float s_nextCensus;
        private static readonly System.Text.StringBuilder s_censusSb = new System.Text.StringBuilder(512);
        private static readonly Plane[] s_censusPlanes = new Plane[6];
        private static void SeamCensus(Camera cam, Vector3 p)
        {
            if (!EasyBakeLog.DebugOn() || Time.unscaledTime < s_nextCensus) return;
            s_nextCensus = Time.unscaledTime + 5f;
            var zs = ZoneSystem.instance;
            GeometryUtility.CalculateFrustumPlanes(cam, s_censusPlanes);
            int total = 0, inLoaded = 0, enabled = 0, meshed = 0, visible = 0, withLod = 0, inFrustum = 0, hidden = 0, listed = 0;
            float hidMin = float.MaxValue, hidMax = 0f;
            var sb = s_censusSb; sb.Length = 0;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || hm.IsDistantLod) continue;
                total++;
                Vector2s z = ZoneSystem.GetZone(hm.transform.position);
                if (!zs.IsZoneLoaded(z)) continue;
                inLoaded++;
                var mf = hm.GetComponent<MeshFilter>(); var mr = hm.GetComponent<MeshRenderer>();
                bool en = mr != null && mr.enabled && hm.isActiveAndEnabled, me = mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0;
                bool vis = mr != null && mr.isVisible, lod = hm.GetComponentInParent<LODGroup>() != null;
                if (en) enabled++; if (me) meshed++; if (vis) visible++; if (lod) withLod++;
                bool fr = mr != null && me && GeometryUtility.TestPlanesAABB(s_censusPlanes, mr.bounds);
                if (!fr) continue;
                inFrustum++;
                if (vis) continue;
                hidden++;
                float d = Vector2.Distance(new Vector2(p.x, p.z), new Vector2(hm.transform.position.x, hm.transform.position.z));
                hidMin = Mathf.Min(hidMin, d); hidMax = Mathf.Max(hidMax, d);
                if (listed++ < 6) sb.Append($" ({z.x},{z.y}) {d:0} m enabled {(en ? "yes" : "NO")} lodgroup {(lod ? "yes" : "no")};");
            }
            EasyBakeLog.Debug($"[FarTerrain] seam census: lodBias {QualitySettings.lodBias:0.##}, far clip {cam.farClipPlane:0} m; near chunks {total}, in loaded zones {inLoaded} "
                + $"(enabled {enabled}, mesh built {meshed}, drawn last frame {visible}, under a LODGroup {withLod}); in the camera frustum {inFrustum}, of those NOT drawn {hidden}"
                + (hidden > 0 ? $" at {hidMin:0}-{hidMax:0} m:{sb}" : "") + $"; far tiles shown {s_shownCount}");
            EdgeMismatch(zs);
        }

        // 1.2.484 ([lead] 11-21, Fire's super-flat ring, design 6b (c'); Debug only, inside the 5 s seam census): along every edge where a built near chunk meets an
        // UNLOADED zone, the near ground's height (vanilla Heightmap.GetHeight(x, y) + the chunk's y, as WaterHideBuried.cs:254 reads it) against the far tile's TRUE
        // height (v.H, bilinear, before the SetVertices sink) at the same x/z, 9 samples per edge. Read-only. Proof: |max| <= 0.1 m on a flat world = the heights meet.
        private static void EdgeMismatch(ZoneSystem zs)
        {
            int samples = 0, noFar = 0; float worst = 0f; Vector3 worstAt = Vector3.zero;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || hm.IsDistantLod || !hm.isActiveAndEnabled) continue;
                var mf = hm.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) continue;   // not built yet
                Vector3 hp = hm.transform.position;
                Vector2s z = ZoneSystem.GetZone(hp);
                if (!zs.IsZoneLoaded(z)) continue;
                int w = hm.m_width; float sc = hm.m_scale;
                for (int side = 0; side < 4; side++)
                {
                    int dx = side == 0 ? -1 : side == 1 ? 1 : 0, dz = side == 2 ? -1 : side == 3 ? 1 : 0;
                    if (zs.IsZoneLoaded(new Vector2s(z.x + dx, z.y + dz))) continue;   // only edges that face an unloaded zone
                    for (int k = 0; k <= 8; k++)
                    {
                        int t = k * w / 8;
                        int ix = dx < 0 ? 0 : dx > 0 ? w : t, iy = dz < 0 ? 0 : dz > 0 ? w : t;
                        float nearY;
                        try { nearY = hm.GetHeight(ix, iy) + hp.y; } catch { continue; }
                        float wx = hp.x + (ix - w * 0.5f) * sc, wz = hp.z + (iy - w * 0.5f) * sc;
                        if (!FarHeightAt(wx, wz, out float farY)) { noFar++; continue; }
                        samples++;
                        float d = farY - nearY;
                        if (Mathf.Abs(d) > Mathf.Abs(worst)) { worst = d; worstAt = new Vector3(wx, nearY, wz); }
                    }
                }
            }
            EasyBakeLog.Debug($"[FarTerrain] edge mismatch (loaded edge, far true height minus near height): {samples} sample(s), max {worst:+0.00;-0.00;0.00} m"
                + (samples > 0 ? $" at ({worstAt.x:0},{worstAt.z:0}) near y {worstAt.y:0.00}" : "") + (noFar > 0 ? $"; {noFar} with no shown far tile there" : "")
                + (DepthPriorityActive ? "; far ground at its true height (Depth Priority, depth offset)" : $"; far ground under loaded zones drawn {BandSink:0.0} m lower (BandSink)"));
        }

        /// <summary>1.2.484 (FarGroundCover.Covered, design 3.2): true when a SHOWN EBM far tile holds this world x/z (its ground is drawn there). Main thread.</summary>
        internal static bool CoversPoint(float wx, float wz) => Wanted && !s_failed && s_views.Count > 0 && FarHeightAt(wx, wz, out _);

        // The far tile's true height (v.H, before the sink) at world x/z: the finest SHOWN view that holds the point, bilinear between its grid vertices.
        private static bool FarHeightAt(float wx, float wz, out float y)
        {
            y = 0f;
            for (int l = 0; l <= MaxLevel; l++)
            {
                float size = BaseSpacing * (1 << l) * Q;
                var k = new Key(l, Mathf.FloorToInt(wx / size), Mathf.FloorToInt(wz / size));
                if (!s_views.TryGetValue(k, out View v) || !v.Shown || v.H == null) continue;
                float s = k.Spacing, fx = (wx - k.MinX) / s, fz = (wz - k.MinZ) / s;
                int j = Mathf.Clamp(Mathf.FloorToInt(fx), 0, Q - 1), i = Mathf.Clamp(Mathf.FloorToInt(fz), 0, Q - 1);
                float tx = Mathf.Clamp01(fx - j), tz = Mathf.Clamp01(fz - i);
                float a = v.H[i * V + j], b = v.H[i * V + j + 1], c = v.H[(i + 1) * V + j], d = v.H[(i + 1) * V + j + 1];
                y = Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), tz);
                return true;
            }
            return false;
        }

        private static float Reach => FarReach.For(OwnReach);   // 1.2.483: Far Zone Sync = the one shared reach (FarReach.Effective), else OwnReach
        internal static float OwnReach => Mathf.Clamp(FarReach.Or(3000f), 800f, 6000f);   // 1.2.483: the 1.2.482 reach, unchanged

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
                if (d.Keep)   // 1.2.504: the shown view already holds this tile
                {
                    if (d.Epoch == s_epoch && s_wanted.Contains(d.K))
                    {
                        if (s_views.TryGetValue(d.K, out var kv) && kv.Shown && kv.Print == d.Print && kv.Content == d.Content)
                        {
                            if (d.H != null) CompareKept(kv, d);   // Debug sample: built anyway, compared bit for bit
                            kv.Epoch = d.Epoch;
                            Interlocked.Increment(ref s_wKept);
                        }
                        else if (d.H != null) { d.Keep = false; Show(d); }   // the sample carries real data: show it
                        else { s_forceBuild[d.K] = 1; lock (s_lock) { s_queue.Add(d.K); Monitor.PulseAll(s_lock); } }   // the view changed meanwhile: build this key for real
                    }
                    TilePool.Give(d);
                }
                else if (d.Epoch == s_epoch && s_wanted.Contains(d.K)) Show(d);
                else TilePool.Give(d);   // 1.2.504: a stale or unwanted tile hands its arrays back
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
            // 1.2.504: the arrays the view showed before go back to the pool (a pooled view from Release holds none)
            if (!ReferenceEquals(v.H, d.H)) TilePool.Give(v.H);
            if (!ReferenceEquals(v.M, d.M)) TilePool.Give(v.M);
            if (!ReferenceEquals(v.B, d.B)) TilePool.Give(v.B);
            if (!ReferenceEquals(v.BH, d.BH)) TilePool.Give(v.BH);
            v.K = d.K; v.H = d.H; v.Print = d.Print; v.Epoch = d.Epoch; v.Content = d.Content;
            v.M = d.M; v.B = d.B; v.BH = d.BH;   // 1.2.501 (a pooled view's old upload copy v.Up is reused: same size)
            d.H = null; d.M = null; d.B = null; d.BH = null;   // 1.2.504: the view owns them now
            s_shownSig[d.K] = Sig(d.Content, d.Print);
            if (v.R.sharedMaterial != s_mat) v.R.sharedMaterial = s_mat;   // a pooled view from before a world change holds the old (destroyed) material
            if (d.C == null)
            {
                d.C = TilePool.C();   // 1.2.504: rented (was new Color32[GridVerts])
                for (int i = 0; i < GridVerts; i++) d.C[i] = SectorColour(d.S[i]);
            }
            float s = d.K.Spacing, half = d.K.Size * 0.5f;
            v.Go.transform.position = new Vector3(d.K.MinX + half, 0f, d.K.MinZ + half);
            // 1.2.504 (deep-row fix, capture row "FarTerrainTiles.UpdateInner() 60.6 MB in 2,142 allocations, avg 29,653 B"): one main-thread colour array, refilled per tile
            // (Mesh.colors32 copies it), and the uvs, which are the same for every tile, built once (was new Color32[VertCount] + new Vector2[VertCount] per tile shown).
            var cols = s_showCols ?? (s_showCols = new Color32[VertCount]);
            var uvs = s_showUvs ?? (s_showUvs = TileUvs());
            for (int i = 0; i < GridVerts; i++) cols[i] = d.C[i];
            for (int k = 0; k < 4 * V; k++) cols[GridVerts + k] = cols[SkirtSource(k)];
            TilePool.Give(d.C); d.C = null;   // 1.2.504: the vertex colours are in cols now; the tile keeps none
            v.Mesh.Clear();
            SetVertices(v);
            v.Mesh.colors32 = cols; v.Mesh.uv = uvs;
            v.Mesh.SetTriangles(s_indices ?? (s_indices = Indices()), 0, true);
            v.Mesh.RecalculateNormals(); v.Mesh.RecalculateTangents();
            VedrFar.UploadMask(v);   // 1.2.501: the world-gen mask, with the owner's final G on HD-look tiles (was v.Mask.SetPixels(d.M); v.Mask.Apply(false, false))
            v.R.GetPropertyBlock(s_mpb);
            s_mpb.SetFloat(Id_Hide, HugeHide);
            s_mpb.SetTexture(Id_Mask, v.Mask);
            // 1.2.497 H1 ([2ic] eyes note, RenderDoc-proven): HD tiles its arrays by uv0, not world position (its vert: o1 = uv0 x ST, :14-15; its frag: x int cb0[9].x,
            // = HD's global _TileAmount per 64 m chunk, Mod.cs:237). A far tile spans d.K.Size (512 m+), so per tile the tiling is HD's value x (Size / 64) to keep HD's
            // texel size; the paint mask keeps uv0 0..1 (its own transform, vert :16-17). EBM/Heightmap (V / no HD) never reads _TileAmount.
            if (HdLookActive) s_mpb.SetFloat(Id_TileAmount, HdTileAmount() * (d.K.Size / ChunkSize));
            v.R.SetPropertyBlock(s_mpb);
            v.Go.SetActive(true); v.Shown = true; s_shownCount++;
        }

        // 1.2.504: main-thread scratch for Show / SetVertices (Mesh.vertices / colors32 / uv copy the data, so one array serves every tile)
        private static Color32[] s_showCols;
        private static Vector2[] s_showUvs;
        private static Vector3[] s_setVerts;
        // The grid's uvs (j / Q, i / Q) and the skirt's copies of its edge vertices: identical for every tile (the old per-tile loop wrote exactly these values).
        private static Vector2[] TileUvs()
        {
            var uvs = new Vector2[VertCount];
            for (int i = 0; i < V; i++) for (int j = 0; j < V; j++) uvs[i * V + j] = new Vector2(j / (float)Q, i / (float)Q);
            for (int k = 0; k < 4 * V; k++) uvs[GridVerts + k] = uvs[SkirtSource(k)];
            return uvs;
        }

        // vertices with the sink under loaded zones; renderer off when every grid vertex is sunk
        private static void SetVertices(View v)
        {
            var verts = s_setVerts ?? (s_setVerts = new Vector3[VertCount]);   // 1.2.504 (UpdateInner row): every slot is written below; was new Vector3[VertCount] per call (Show and every loaded-set change, :501)
            float s = v.K.Spacing, half = v.K.Size * 0.5f, minX = v.K.MinX, minZ = v.K.MinZ;
            int sunk = 0;
            const bool exact = true;   // 1.2.444: always true heights (no 80 m sink); EBM/Heightmap hands over pixel by pixel against the near chunks' own distance dither
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
                        // 1.2.445: under loaded zones the far ground sits BandSink lower, so in the near chunks' dissolve band (0.7-1 L, where both draw) the near ground
                        // wins the depth test and the far ground only fills the dither holes (EbmHeightmap.shader far-tile clip comment).
                        if (UnderLoaded(wx - s, wz) && UnderLoaded(wx + s, wz) && UnderLoaded(wx, wz - s) && UnderLoaded(wx, wz + s)) { if (!DepthPriorityActive) y -= exact ? BandSink : SinkDepth; sunk++; }   // 1.2.485: no sink under Depth Priority (true height, depth offset instead)
                        else if (!exact) y -= 1f;
                    }
                    verts[n] = new Vector3(j * s - half, y, i * s - half);
                }
            float drop = 2f * s;
            for (int k = 0; k < 4 * V; k++) verts[GridVerts + k] = verts[SkirtSource(k)] + Vector3.down * drop;
            v.Mesh.vertices = verts;
            v.Mesh.RecalculateBounds();
            v.R.enabled = true;   // 1.2.444: never off by "under loaded zones": near ground past its hide distance is gone even inside loaded zones, so the far tile must still draw there (sunk count kept for the log)
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
            EbmApi.RaiseFarGroundOwnedChanged();   // 1.2.483: FarGroundOwned is now true (FAT's distant ring stands down)
            if (!s_saidReady) { s_saidReady = true; EasyBakeLog.Info($"[FarTerrain] EBM's far ground covers the view ({s_wanted.Count} tiles to {Reach:0} m, data from {(Fat.Height != null ? "FAT's far data API" : "the game's world generator")}); the game's distant terrain is off ({s_lods.Count} TerrainLod)"); }
        }

        private static void Shutdown(string why)
        {
            lock (s_lock) { s_queue = new List<Key>(); }
            s_epoch++;
            NewContent();   // 1.2.504: nothing built before this may be kept
            foreach (var v in s_views.Values) Release(v);
            s_views.Clear(); s_wanted.Clear(); s_pendingDrop.Clear();
            s_forceBuild.Clear();
            while (s_ready.TryDequeue(out var dd)) TilePool.Give(dd);   // 1.2.504: their arrays back to the pool
            s_planAt = new Vector3(float.NaN, 0f, float.NaN);
            if (s_lodOff)
            {
                int n = 0;
                foreach (var l in s_lods) if (l != null) { l.enabled = true; n++; }
                s_lods.Clear(); s_lodOff = false;
                EasyBakeLog.Info($"[FarTerrain] the game's distant terrain is back ({n} TerrainLod): {why}");
                EbmApi.RaiseFarGroundOwnedChanged();   // 1.2.483: FarGroundOwned is now false (FAT may build its distant ring again)
            }
            if (s_mat != null) { UnityEngine.Object.Destroy(s_mat); s_mat = null; }
            s_sectorColour.Clear(); s_saidReady = false;
        }

        private static void Release(View v)
        {
            if (v.Go != null) v.Go.SetActive(false);
            v.Shown = false;
            s_shownSig.TryRemove(v.K, out _);   // 1.2.504: nothing to keep for this key any more
            TilePool.Give(v.H); TilePool.Give(v.M); TilePool.Give(v.B); TilePool.Give(v.BH);   // 1.2.504: its arrays back to the pool (v.Up stays: the view's own upload copy)
            v.H = null; v.M = null; v.B = null; v.BH = null;
            s_pool.Push(v);
        }

        // 1.2.504 (Debug sample of a kept tile): the freshly built copy against what the view shows, heights and mask bit for bit. Vertex colours are not
        // compared (the view keeps none after Show): every input a colour depends on (colour mode, FAT paint, world) bumps the content epoch, so no keep can span a colour change.
        private static void CompareKept(View v, TileData d)
        {
            Interlocked.Increment(ref s_wCompares);
            int bad = -1;
            if (v.H == null || v.M == null || d.M == null) bad = -2;
            else for (int i = 0; i < GridVerts; i++) if (!(v.H[i] == d.H[i]) || !(v.M[i].r == d.M[i].r && v.M[i].g == d.M[i].g && v.M[i].b == d.M[i].b && v.M[i].a == d.M[i].a)) { bad = i; break; }
            if (bad == -1) return;
            Interlocked.Increment(ref s_wMismatch);
            EasyBakeLog.Debug($"[FarTerrain] kept-tile compare MISMATCH at tile {d.K.L}/{d.K.X}/{d.K.Z} (print {d.Print:x8}): " + (bad == -2 ? "arrays missing" : $"vertex {bad}: shown h {v.H[bad]} mask {v.M[bad]}, rebuilt h {d.H[bad]} mask {d.M[bad]}") + " (1.2.504)");
        }

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
        // 1.2.485: the far material's depth offset follows the Depth Priority key (0 / 0 = off = as before). Says once when the shader lacks the property.
        private static bool s_saidNoOffset;
        private static void ApplyDepthPriority()
        {
            if (s_mat == null) return;
            bool want = DepthPriority != null && DepthPriority.Value;
            if (want && !s_mat.HasProperty(Id_OffUnits))
            {
                if (!s_saidNoOffset) { s_saidNoOffset = true; EasyBakeLog.Info("[FarTerrain] Far Terrain Depth Priority is on but this EBM/Heightmap has no depth offset (shaders older than 1.2.485): the far ground keeps its 1.5 m sink"); }
                return;
            }
            bool on = DepthPriorityActive;
            s_mat.SetFloat(Id_OffFactor, on ? DepthOffsetFactor : 0f);
            s_mat.SetFloat(Id_OffUnits, on ? DepthOffsetUnits : 0f);
            EasyBakeLog.Info($"[FarTerrain] Far Terrain Depth Priority {(on ? $"on: far ground at its true height, depth offset {DepthOffsetFactor:0.##} / {DepthOffsetUnits:0.##}" : "off: far ground 1.5 m lower under loaded zones (as before)")}");
        }

        // 1.2.496 ([visual], [lead] 11-22, Fire 16:25 "our distant terrain with our current settings is off color" / "the far terrain is loading as dirt"): with
        // Badgers' HD Terrain loaded, the far material below is a copy of HD's near material, so it carried HD's texture arrays. EBM/Heightmap reads them with
        // vanilla's logic (slot content AND alpha masks: L2 diffuse + alpha as slope-rock coverage incl. Meadows, EbmHeightmap.shader:896-904; L0 grass gated by
        // it, :917-931), and HD's content is not vanilla's (HD swaps its own distant slot 2, its Mod.cs:448): far Meadows drew tan. Rig, same pose
        // (1623,153,211): arm A (HD, Far Terrain on) tan, Far Terrain off = dark vanilla distant LOD under HD; arm D (no HD, Far Terrain on) green
        // (perfcircle_20261122_171414 T_arrive_0). Option V ([lead] GO, draft): with HD loaded, the far material uses VANILLA's arrays, captured once before HD
        // swaps them (HD's Heightmap.Awake prefix sets m_material's _DiffuseArrayTex / _NormalArrayTex, HD HarmonyPatches :72-87). Held here, so never unloaded.
        // No HD: none of this runs (the capture returns first thing, MakeMaterial is unchanged). HD loaded but no capture: HD's arrays as before + one Warning.
        private const string HdGuid = "Badgers.ValheimHDTerrain";
        private const string VanillaTerrainShader = "Custom/Heightmap";
        private static readonly int Id_DiffArr = Shader.PropertyToID("_DiffuseArrayTex"), Id_NormArr = Shader.PropertyToID("_NormalArrayTex");
        private static Texture s_vanDiff, s_vanNorm;
        private static bool? s_hd;
        private static bool s_saidVanillaArrays, s_saidNoVanillaArrays;
        private static bool HdPresent
        {
            get { if (s_hd == null) s_hd = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(HdGuid); return s_hd.Value; }
        }

        // First in line on Heightmap.Awake (HD's prefix has the default priority), once: the shared terrain material still holds vanilla's arrays here.
        [HarmonyLib.HarmonyPatch(typeof(Heightmap), "Awake")]
        private static class VanillaArraysCapture
        {
            [HarmonyLib.HarmonyPrefix, HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
            private static void Prefix(Heightmap __instance)
            {
                if (s_vanDiff != null || s_captureTried || !HdPresent) return;   // one try: a rejected capture is not retried
                try
                {
                    Material m = __instance != null ? __instance.m_material : null;
                    if (m == null || m.shader == null || m.shader.name != VanillaTerrainShader || !m.HasProperty(Id_DiffArr)) return;
                    Texture d = m.GetTexture(Id_DiffArr);
                    if (d == null) return;
                    Texture n = m.HasProperty(Id_NormArr) ? m.GetTexture(Id_NormArr) : null;
                    s_captureTried = true;
                    // [2ic] MUST 1: validate before keeping. Rejected (one Warn, far ground keeps HD's arrays as before) when either texture IS one of HD's own arrays
                    // (HD's static Mod.DiffuseArray / NormalArray / *Lod fields, read by reflection: someone swapped them before this first Awake), or a name is not
                    // vanilla's (terrain_d_array / terrain_n_array, EbmHeightmap.shader:198).
                    string why = Reject(d, VanillaDiffName) ?? (n != null ? Reject(n, VanillaNormName) : null);
                    if (why != null) { EasyBakeLog.Warn($"[FarTerrain] HD Terrain is loaded but the terrain arrays at the first Heightmap.Awake are not vanilla's ({why}): not captured, the far ground keeps HD's arrays (1.2.496)"); return; }
                    s_vanDiff = d; s_vanNorm = n;
                    // [2ic] MUST 2: holding these keeps vanilla's two arrays in memory for an HD player (they would unload once HD replaced them): their size, once, here.
                    EasyBakeLog.Info($"[FarTerrain] captured vanilla's terrain arrays for the far ground (HD Terrain loaded): {Describe(d)}; {(n != null ? Describe(n) : "normal array: none")}; held = {(MemBytes(d) + MemBytes(n)) / 1048576.0:0.0} MB kept loaded (1.2.496)");
                }
                catch (Exception e) { EasyBakeLog.Warn($"[FarTerrain] vanilla terrain array capture skipped: {e.GetType().Name}: {e.Message} (the far ground keeps HD's arrays)"); }
            }
        }

        private const string VanillaDiffName = "terrain_d_array", VanillaNormName = "terrain_n_array";
        private static bool s_captureTried;
        private static FieldInfo[] s_hdArrayFields;
        private static string Reject(Texture t, string wantName)
        {
            if (!(t is Texture2DArray)) return $"'{t.name}' is a {t.GetType().Name}, not a Texture2DArray";
            if (t.name != wantName) return $"'{t.name}' is not '{wantName}'";
            if (s_hdArrayFields == null)
            {
                var mod = Type.GetType("ValheimHDTerrain.Mod, ValheimHDTerrain");
                var list = new List<FieldInfo>();
                if (mod != null) foreach (string f in new[] { "DiffuseArray", "NormalArray", "DiffuseArrayLod", "NormalArrayLod" })
                    { var fi = mod.GetField(f, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); if (fi != null) list.Add(fi); }
                s_hdArrayFields = list.ToArray();
            }
            foreach (var fi in s_hdArrayFields) { try { if (ReferenceEquals(fi.GetValue(null), t)) return $"'{t.name}' is HD Terrain's own {fi.Name}"; } catch { } }
            return null;
        }
        // [2ic] note 2: Profiler.GetRuntimeMemorySizeLong is 0 in a release player (Fire's), so the size is COMPUTED from the array: every mip's size for its format
        // (GraphicsFormatUtility.ComputeMipmapSize, block-compressed formats included) x layers. The profiler figure is printed beside it (dev builds only, else 0).
        private static long MemBytes(Texture t)
        {
            var a = t as Texture2DArray;
            if (a == null) return 0L;
            try
            {
                long per = 0L;
                for (int i = 0; i < a.mipmapCount; i++)
                    per += UnityEngine.Experimental.Rendering.GraphicsFormatUtility.ComputeMipmapSize(Math.Max(1, a.width >> i), Math.Max(1, a.height >> i), a.graphicsFormat);
                return per * a.depth;
            }
            catch { return 0L; }
        }
        private static string Describe(Texture t)
        {
            var a = t as Texture2DArray;
            return a != null ? $"'{a.name}' {a.width}x{a.height} x {a.depth} layers, {a.format}, {a.mipmapCount} mips, {MemBytes(a) / 1048576.0:0.0} MB computed (profiler {UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(a) / 1048576.0:0.0} MB, 0 in a release build)" : $"'{t.name}'";
        }

        // ---- 1.2.497 H1: HD Terrain's own shader on the far tiles ----
        // RenderDoc (Tools\Captures\O_20261122_183812_frame9155.rdc, dump Tools\_visual_rdc_hd_20261122): HD's near shader 'Custom/HDTerrainHeightMap' samples its arrays'
        // slices 0-14 + 16 per pixel and blends them by its own slope / height weights; it has no camera-distance fade and no discard (its near chunks never dissolve).
        // So: a copy of the near material KEEPING HD's shader, the distant keyword off (the dark look T_faroff showed is HD's distant-LOD path), HD's FULL arrays set
        // explicitly (HD's Heightmap.Initialize postfix puts its 128 px LOD arrays on the SHARED material for distant chunks, so the template may hold those).
        // Seam: HD's shader has no EBM depth offset, so DepthPriorityActive is false and the far vertices under loaded zones take the BandSink / SinkDepth path (:450).
        internal static ConfigEntry<bool> HdLook;
        private const string HdShaderName = "Custom/HDTerrainHeightMap";
        private static readonly int Id_Caustics = Shader.PropertyToID("_WaterCaustics");
        private static readonly int Id_TileAmount = Shader.PropertyToID("_TileAmount");
        private const float ChunkSize = 64f;   // a vanilla terrain chunk (ZoneSystem zone size), the span HD's _TileAmount is tuned for
        // HD's tiling as HD set it (Shader.SetGlobalFloat("_TileAmount", tilingAmount), its Mod.cs:237); 8 = HD's TerrainConfig default if the global is not set yet.
        private static float HdTileAmount() { float t = Shader.GetGlobalFloat(Id_TileAmount); return t > 0f ? t : 8f; }
        private static bool s_saidHdLook, s_saidNoHdLook;
        internal static bool HdLookActive;   // the current far material is HD's
        private static Texture HdStatic(Type mod, string field)
        {
            try { var f = mod != null ? mod.GetField(field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null; return f != null ? f.GetValue(null) as Texture : null; }
            catch { return null; }
        }
        private static void NoHdLook(string why)
        {
            if (s_saidNoHdLook) return;
            s_saidNoHdLook = true;
            EasyBakeLog.Warn($"[FarTerrain] Far Terrain HD Look is on but cannot be used ({why}): the far ground uses the game's own terrain textures instead (1.2.497)");
        }
        private static bool MakeHdMaterial()
        {
            Material template = null;
            foreach (var hm in Heightmap.GetAllHeightmaps()) if (hm != null && !hm.IsDistantLod && hm.m_material != null) { template = hm.m_material; break; }
            if (template == null) return false;   // no near chunk yet: asked again next time
            if (template.shader == null || template.shader.name != HdShaderName) { NoHdLook($"the near ground's shader is '{(template.shader != null ? template.shader.name : "none")}', not '{HdShaderName}'"); return false; }
            var mod = Type.GetType("ValheimHDTerrain.Mod, ValheimHDTerrain");
            Texture d = HdStatic(mod, "DiffuseArray"), n = HdStatic(mod, "NormalArray"), c = HdStatic(mod, "WaterCaustics");
            if (d == null || n == null) { NoHdLook("HD Terrain's texture arrays were not found"); return false; }
            s_mat = new Material(template) { name = "FiresEBM_FarTerrain_HD", hideFlags = HideFlags.DontSave };   // HD's shader, as on the near ground
            s_mat.SetTexture(Id_DiffArr, d);
            if (s_mat.HasProperty(Id_NormArr)) s_mat.SetTexture(Id_NormArr, n);
            if (c != null && s_mat.HasProperty(Id_Caustics)) s_mat.SetTexture(Id_Caustics, c);
            s_mat.DisableKeyword("_ISDISTANTLOD_ON");
            if (s_mat.HasProperty(Id_Distant)) s_mat.SetFloat(Id_Distant, 0f);
            HdLookActive = true;
            if (!s_saidHdLook)
            {
                s_saidHdLook = true;
                var a = d as Texture2DArray;
                EasyBakeLog.Info($"[FarTerrain] HD look: the far ground draws with HD Terrain's own shader '{HdShaderName}' and its full arrays ('{d.name}'{(a != null ? $" {a.width}x{a.height} x {a.depth} layers" : "")} / '{n.name}'); "
                    + "far ground under the loaded area takes the sink path (no EBM depth offset on this shader) (1.2.497)");
            }
            return true;
        }

        // 1.2.497 ([2ic] toggle proof, [generator] ask): one Info line per far material BUILD (a rare event: world start, a key toggle), with how many far materials
        // exist right after it. Shutdown's Destroy runs at the end of that frame, so a toggle shows "live 1" (or 2 for that one frame); a count that keeps growing = a leak.
        private static int s_matBuilds;
        private static void SayMaterialBuilt(string kind)
        {
            s_matBuilds++;
            int live = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m != null && m.name.StartsWith("FiresEBM_FarTerrain", StringComparison.Ordinal)) live++;
            EasyBakeLog.Info($"[FarTerrain] far material #{s_matBuilds} built: {kind}; far materials alive now: {live} (1.2.497)");
            VedrFar.OnMaterialMade();   // 1.2.501: the owner's season texture onto the new material (allow-list), its own value remembered
            if (VedrFar.SnowG != null) VedrFar.Reupload("far material built");   // 1.2.501: HD look may have flipped: shown masks follow
        }

        private static bool MakeMaterial()
        {
            HdLookActive = false;
            // 1.2.507 ([lead] proof34, Fire: "our own terrain shader with HD's textures", near AND far): when the near ground is ours on HD's arrays, the ours path
            // wins whatever Far Terrain HD Look says (that key returned first here in 1.2.506, so the far ground drew with HD's shader beside our near ground).
            // The key keeps its meaning only while the near ground is not ours (Vedr not drawing it).
            bool nearOurs = HdPresent && NearHdOurs(out _);
            if (!nearOurs && HdPresent && HdLook != null && HdLook.Value && MakeHdMaterial()) { SayMaterialBuilt("HD Terrain's own shader (Far Terrain HD Look)"); return true; }   // 1.2.497 H1; any failure falls through to EBM/Heightmap (V when HD is loaded)
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
            ApplyDepthPriority();   // 1.2.485
            // 1.2.496: HD loaded -> vanilla's arrays on the far copy (see VanillaArraysCapture). The proof line prints here, when the first such material is made.
            // 1.2.506: unless the NEAR ground is our shader on HD's arrays (Vedr/Heightmap with _TerrainHdLayers = 1): then the far copy takes exactly the same
            // inputs (HdOursMaterial), so near and far match.
            s_hdOurs = false;
            if (nearOurs && NearHdOurs(out var nearMat) && HdOursMaterial(nearMat)) { }
            else if (HdPresent)
            {
                if (s_vanDiff != null)
                {
                    s_mat.SetTexture(Id_DiffArr, s_vanDiff);
                    if (s_vanNorm != null && s_mat.HasProperty(Id_NormArr)) s_mat.SetTexture(Id_NormArr, s_vanNorm);
                    if (!s_saidVanillaArrays) { s_saidVanillaArrays = true; EasyBakeLog.Info($"[FarTerrain] HD Terrain is loaded: the far ground uses vanilla's terrain arrays ('{s_vanDiff.name}' / '{(s_vanNorm != null ? s_vanNorm.name : "normal not found, HD's kept")}'), captured before HD swapped them; the near ground stays HD's (1.2.496)"); }
                }
                else if (!s_saidNoVanillaArrays) { s_saidNoVanillaArrays = true; EasyBakeLog.Warn($"[FarTerrain] HD Terrain is loaded but vanilla's terrain arrays were not captured ({(s_captureTried ? "rejected, see the capture Warning" : "no Heightmap.Awake saw a 'Custom/Heightmap' material")}): the far ground keeps HD's arrays, as before 1.2.496"); }
            }
            EasyBakeLog.Info($"[FarTerrain] material: a copy of '{template.name}' on EBM/Heightmap (texture arrays carried over{(s_hdOurs ? ", then HD's diffuse array + vanilla's normal / coverage arrays set, as the near ground" : HdPresent && s_vanDiff != null ? ", then vanilla's arrays set: HD Terrain is loaded" : "")})");
            SayMaterialBuilt(s_hdOurs ? "EBM/Heightmap on HD's diffuse array, as the near ground (1.2.506)" : HdPresent && s_vanDiff != null ? "EBM/Heightmap on vanilla's arrays (V)" : "EBM/Heightmap");   // 1.2.497
            return true;
        }
        private static bool s_saidNoShader;

        // ---- 1.2.506 ([visual] + [seasons], Fire 11-23: "we just need the textures and we use our own damn terrain shader"): HD ground drawn by OUR shader ----
        // The near ground's owner (Vedr/Heightmap) marks each chunk it draws on HD's arrays with _TerrainHdLayers = 1 and gives it HD's full diffuse array, vanilla's
        // normal array and vanilla's diffuse array as _TerrainVanillaDiffArr (the slot-2 rock tap reads HD slice 4, its coverage alpha vanilla's; shader lines agreed
        // with [seasons], EbmHeightmap.shader 1.2.506). The far copy takes the same: HD's Mod.DiffuseArray (full, never the 128 px LOD array the shared template may
        // hold), vanilla's two arrays from VanillaArraysCapture, _TerrainHdLayers 1, and the near chunk's own _UVScale (the same tiling as near). Any input missing =
        // V as before, one Warning. No HD, or the near ground not on HD's arrays = nothing here runs beyond one property read.
        private static readonly int Id_HdLayers = Shader.PropertyToID("_TerrainHdLayers"), Id_VanDiffArr = Shader.PropertyToID("_TerrainVanillaDiffArr"), Id_UVScale = Shader.PropertyToID("_UVScale");
        private static bool s_hdOurs, s_saidHdOurs, s_saidNoHdOurs;
        private static float s_nextOursCheck;

        /// <summary>A near (not distant) chunk whose own material instance says our shader draws it on HD's arrays.</summary>
        // 1.2.508 ([lead] full39s: Vedr .33 logged "HD Terrain ground on Vedr/Heightmap", EBM 1.2.507 never printed "HD textures on our shader"): the 1.2.506 test read
        // only _TerrainHdLayers, which Vedr sets on the chunk material BEFORE it swaps the shader (VedrHeightmap.Swap :345 then :349), i.e. while the material's
        // shader is HD's and lacks that property - whether Unity keeps such a value across the shader change is UNCONFIRMED, and the census below shows it. Now a
        // chunk counts as ours on HD's arrays when its shader DECLARES _TerrainHdLayers (ours: Vedr/Heightmap or EBM/Heightmap) and either the flag reads 1 or its
        // _DiffuseArrayTex is a 17+ slice array (HD's; the game's has 16; a Vedr tint of HD's keeps its depth).
        private static bool NearHdOurs(out Material mat)
        {
            mat = null;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || hm.IsDistantLod) continue;
                var m = EbmTerrainShader.MatOf(hm);   // m_materialInstance is private in the reference: EBM's one accessor
                if (IsOursOnHd(m)) { mat = m; return true; }
            }
            return false;
        }
        private static bool IsOursOnHd(Material m)
        {
            if (m == null || m.shader == null || !m.HasProperty(Id_HdLayers)) return false;
            if (m.GetFloat(Id_HdLayers) >= 0.5f) return true;
            return m.HasProperty(Id_DiffArr) && m.GetTexture(Id_DiffArr) is Texture2DArray a && a.depth >= 17;
        }

        // 1.2.508: once a minute while HD is loaded, what the near ground is drawn with and what the far ground uses (Debug), so a leg shows WHY the far path is what it is
        private static float s_nextNearCensus;
        private static void NearCensus(bool near)
        {
            float now = Time.unscaledTime;
            if (now < s_nextNearCensus || !EasyBakeLog.DebugOn()) return;
            s_nextNearCensus = now + 60f;
            int total = 0, hd = 0, oursHd = 0, oursFlag = 0, oursDepth = 0, other = 0;
            foreach (var hm in Heightmap.GetAllHeightmaps())
            {
                if (hm == null || hm.IsDistantLod) continue;
                var m = EbmTerrainShader.MatOf(hm);
                if (m == null || m.shader == null) continue;
                total++;
                if (m.shader.name == HdShaderName) { hd++; continue; }
                if (IsOursOnHd(m)) { oursHd++; if (m.GetFloat(Id_HdLayers) >= 0.5f) oursFlag++; else oursDepth++; continue; }
                other++;
            }
            EasyBakeLog.Debug($"[FarTerrain] near ground census (1.2.508): {total} near chunk(s): HD's shader {hd}, ours on HD's arrays {oursHd} (flag 1: {oursFlag}, by array depth: {oursDepth}), other {other}; "
                + $"far ground: {(s_hdOurs ? "ours on HD's arrays" : HdLookActive ? "HD's own shader (Far Terrain HD Look)" : "EBM/Heightmap on vanilla's arrays (V)")}; near counts as ours: {near}");
        }

        private static bool HdOursMaterial(Material near)
        {
            var mod = Type.GetType("ValheimHDTerrain.Mod, ValheimHDTerrain");
            Texture d = HdStatic(mod, "DiffuseArray");
            string missing = d == null ? "HD Terrain's diffuse array" : s_vanDiff == null ? "vanilla's diffuse array (not captured)" : s_vanNorm == null ? "vanilla's normal array (not captured)" : null;
            if (missing != null)
            {
                if (!s_saidNoHdOurs) { s_saidNoHdOurs = true; EasyBakeLog.Warn($"[FarTerrain] the near ground draws HD's textures with our shader, but {missing} was not found: the far ground keeps vanilla's arrays (1.2.506)"); }
                return false;
            }
            if (!s_mat.HasProperty(Id_HdLayers)) { if (!s_saidNoHdOurs) { s_saidNoHdOurs = true; EasyBakeLog.Warn("[FarTerrain] the near ground draws HD's textures with our shader, but EBM/Heightmap here lacks _TerrainHdLayers (old ebm_indirect bundle?): the far ground keeps vanilla's arrays (1.2.506)"); } return false; }
            s_mat.SetTexture(Id_DiffArr, d);
            if (s_mat.HasProperty(Id_NormArr)) s_mat.SetTexture(Id_NormArr, s_vanNorm);
            s_mat.SetTexture(Id_VanDiffArr, s_vanDiff);
            s_mat.SetFloat(Id_HdLayers, 1f);
            float uv = near.HasProperty(Id_UVScale) ? near.GetFloat(Id_UVScale) : -1f;
            if (uv > 0f && s_mat.HasProperty(Id_UVScale)) s_mat.SetFloat(Id_UVScale, uv);
            s_hdOurs = true;
            if (!s_saidHdOurs)
            {
                s_saidHdOurs = true;
                var a = d as Texture2DArray;
                EasyBakeLog.Info($"[FarTerrain] HD textures on our shader: the far ground takes the near ground's inputs - diffuse '{d.name}'{(a != null ? $" {a.width}x{a.height} x {a.depth} layers" : "")}, normals '{s_vanNorm.name}', coverage '{s_vanDiff.name}', _UVScale {(uv > 0f ? uv.ToString("0.####") : "template's")} (1.2.506)");
            }
            return true;
        }

        /// <summary>Once every 2 s while HD is loaded: the near ground switched to / from our shader on HD's arrays after the far material was built -> rebuild it.</summary>
        private static void CheckHdOurs()
        {
            if (!HdPresent || s_mat == null) return;
            float now = Time.unscaledTime;
            if (now < s_nextOursCheck) return;
            s_nextOursCheck = now + 2f;
            bool near = NearHdOurs(out _);
            NearCensus(near);   // 1.2.508: Debug, once a minute
            if (near != s_hdOurs && !(near && s_saidNoHdOurs)) EasyBakeLog.Info($"[FarTerrain] the near ground {(near ? "now draws HD's textures with our shader" : "left HD's textures on our shader")}: the far material rebuilds ({FiresEasyBakeMeshesPlugin.PluginVersion})");   // 1.2.508: the switch, visible
            if (near != s_hdOurs && !(near && s_saidNoHdOurs)) Shutdown(near ? "the near ground now draws HD's textures with our shader: the far ground rebuilds on the same inputs (1.2.506)" : "the near ground left HD's textures on our shader: the far ground rebuilds (1.2.506)");
        }

        private static Color32 SectorColour(BiomeSector s)
        {
            if (s == null) return new Color32(0, 0, 0, 0);
            if (!s_sectorColour.TryGetValue(s, out var c))
            {
                // 1.2.470: with Vedr's true colour bound, the TRUE biome colour (Vedr's winter-to-Mountain report bypassed); the winter snow class rides the
                // _EbmFarWinterA/B globals instead (VedrFar below). Main thread (Show), as the vanilla call it replaces.
                bool ok = false;
                if (VedrFar.TrueMode) c = VedrFar.TrueColour(s, out ok);
                if (!ok) c = Heightmap.GetBiomeColor(s);
                s_sectorColour[s] = c;
            }
            return c;
        }

        // ── 1.2.470 ([visual], lead GO 11-19, Fire "all snow in all forms should gradually melt"): far tiles take the TRUE biome colour + Vedr's winter snow class ──
        // Before: SectorColour asked the game's GetBiomeColor, which Vedr reports as Mountain for its winter biomes, and the tiles' material is a copy of the shared
        // template (MakeMaterial) without Vedr's per-chunk true corners (_VedrTrueOn, VedrHeightmap.SetTrueBiome), so far winter land drew Mountain's rock with the
        // snow-BASE fill. Now the vertex colour is the true biome (ground textures right) and EBM/Heightmap, on far tiles only (_EbmFarTile = 1), takes the snow
        // class from two globals: _EbmFarWinterA = (Meadows, BlackForest, Swamp, Plains), _EbmFarWinterB = (Mistlands, AshLands, -, on), 1 = Vedr's
        // WinterSnowClass(biome). Mountain / Deep North keep their own class from the colour. No vertex stream is added (the surface shader's tessellation
        // allows no custom vertex data, EbmHeightmap Input :209-215): the class is a per-biome table, the weights the shader already has.
        // 1.2.501 ([visual], design DESIGN_ebm_far_terrain_season_snow_20261122.md v1-v5, [2ic] APPROVED; Vedr .15 side by [seasons]): the snow owner PUSHES
        // everything here through EbmApi; EBM looks nobody up (VedrLink.cs:6-9 rule). Before 1.2.501 this class reflected into VedrApi (TrueBiomeColor / WinterSnowClass).
        //  - true colour by biome (EbmApi.RegisterFarTrueBiomeColour), winter snow class mask (EbmApi.SetFarWinterSnowClass): as 1.2.470, now pushed;
        //  - HD-look snow (EbmApi.RegisterFarTerrainSnowSource + RaiseFarTerrainSnowChanged): HD's own shader reads none of EBM/Heightmap's per-pixel winter,
        //    so on HD-look tiles the owner's FINAL paint G is written into an upload copy of the mask (never the cache);
        //  - season texture (EbmApi.SetFarTerrainTexture, allow-list _DiffuseArrayTex): the owner's fall / spring tinted array on the far material.
        // All main thread. Nothing pushed (Vedr older than .15) = the game's colours, no EBM far shader snow, world-gen G: one snow (the 1.2.470 rule).
        internal static class VedrFar
        {
            private static string s_pathSaid;
            internal static volatile bool TrueMode;
            private static float s_pushAt; private static Vector4 s_a, s_b = new Vector4(0, 0, 0, -1f);
            private static readonly int Id_A = Shader.PropertyToID("_EbmFarWinterA"), Id_B = Shader.PropertyToID("_EbmFarWinterB");
            private static Func<Heightmap.Biome, Color32> s_trueColour;
            private static int s_classMask = -1;   // -1 = never pushed
            internal static Func<float, float, Heightmap.Biome, float, float, float> SnowG;   // (x, z, biome, base height (world-gen / FAT, before edits), base G) -> final G
            internal static volatile bool SnowSourceOn;   // read by the builder thread (keep biome + base height in memory)

            internal static void RegisterTrueColour(Func<Heightmap.Biome, Color32> f) { s_trueColour = f; s_pushAt = 0f; EasyBakeLog.Info($"[FarTerrain] far true biome colour {(f != null ? "pushed" : "withdrawn")} by the snow owner (1.2.501)"); }
            internal static void SetClassMask(int mask) { if (mask == s_classMask) return; s_classMask = mask; s_pushAt = 0f; }   // applied at the next Update (Push), this frame or the next
            internal static void RegisterSnow(Func<float, float, Heightmap.Biome, float, float, float> f)
            {
                SnowG = f; SnowSourceOn = f != null;
                EasyBakeLog.Info($"[FarTerrain] far terrain snow {(f != null ? "pushed" : "withdrawn")} by the snow owner: HD-look far tiles {(f != null ? "take the owner's paint G" : "keep the world-gen G")} (1.2.501)");
                Reupload("snow source " + (f != null ? "registered" : "withdrawn"));
            }

            internal static Color32 TrueColour(BiomeSector s, out bool ok)
            {
                ok = false;
                var f = s_trueColour;
                if (f == null) return default;
                // the biome vanilla's sector overload colours by (REF Heightmap.cs:427-435): the first alt-biome texture override, else the sector's biome (EBM's, not the owner's)
                Heightmap.Biome b = s.Biome;
                if (s.AltBiomes != null) foreach (var alt in s.AltBiomes) if (alt != null && alt.m_terrainTextureOverride != Heightmap.Biome.None) { b = alt.m_terrainTextureOverride; break; }
                try { var c = f(b); ok = true; return c; }
                catch (Exception e) { s_trueColour = null; s_pushAt = 0f; EasyBakeLog.Warn("[FarTerrain] the pushed far true colour threw, far tiles use the game's colour: " + e.Message); }
                return default;
            }
            private static float Cls(Heightmap.Biome b) => s_classMask > 0 && (s_classMask & (int)b) != 0 ? 1f : 0f;

            /// <summary>From Update: applies the pushed state at once after a push (s_pushAt = 0), else re-checks every 5 s (FAT's colour binding can change).</summary>
            internal static void Push()
            {
                if (Time.unscaledTime < s_pushAt) return;
                s_pushAt = Time.unscaledTime + 5f;
                // 1.2.470: shader snow on far tiles only when their colours carry NO winter: the owner's true colour and, when FAT feeds the colours,
                // FAT's TryFarTrueColor (v5). An older FAT (TryFarColor, winterized) = shader snow OFF: one snow, FAT's, as before
                bool vedrTrue = s_trueColour != null;
                bool fatWinterized = Fat.Color != null && Fat.TrueColor == null;
                bool on = s_classMask >= 0 && vedrTrue && !fatWinterized;
                if (on != TrueMode) { TrueMode = on; s_sectorColour.Clear(); NewContent(); s_epoch++; Interlocked.Exchange(ref s_threadEpochBox, s_epoch); s_planAt = new Vector3(float.NaN, 0f, float.NaN); }   // read by CachePath (any thread) and SectorColour; a flip rebuilds the tiles once
                string path = Fat.TrueColor != null ? "FAT true colour (far API v5) + EBM shader snow" : fatWinterized ? "FAT winterized colour (far API < 5): EBM far shader snow OFF (single snow)" : vedrTrue ? "pushed true colour + EBM shader snow" : "game colour, no shader snow";
                if (path != s_pathSaid)
                {
                    s_pathSaid = path;
                    EasyBakeLog.Info($"[FarTerrain] far colour path: {path}; true colour {(vedrTrue ? "pushed" : "not pushed")}, winter snow class {(s_classMask >= 0 ? "pushed" : "not pushed")}"
                        + (!vedrTrue && s_classMask < 0 ? " (no snow-owner far push: Vedr .15+ pushes these; older Vedr = the game's colours, no EBM far shader snow)" : "") + " (1.2.501)");
                }
                var a = on ? new Vector4(Cls(Heightmap.Biome.Meadows), Cls(Heightmap.Biome.BlackForest), Cls(Heightmap.Biome.Swamp), Cls(Heightmap.Biome.Plains)) : Vector4.zero;
                var b = on ? new Vector4(Cls(Heightmap.Biome.Mistlands), Cls(Heightmap.Biome.AshLands), 0f, 1f) : Vector4.zero;
                if (a == s_a && b == s_b) return;
                s_a = a; s_b = b;
                Shader.SetGlobalVector(Id_A, a); Shader.SetGlobalVector(Id_B, b);
                EasyBakeLog.Info(on ? $"[FarTerrain] far winter snow class (pushed): Meadows {a.x:0} BlackForest {a.y:0} Swamp {a.z:0} Plains {a.w:0} Mistlands {b.x:0} AshLands {b.y:0}; far tiles follow the owner's live fill"
                                    : "[FarTerrain] far winter snow class: off (nothing pushed); far tiles as before");
            }

            // ---- 1.2.501 HD-look snow: re-upload every shown tile's mask copy, a few per frame (RaiseFarTerrainSnowChanged / a source change / HD look on)
            private const int ReuploadPerFrame = 4;
            private static readonly List<Key> s_reup = new List<Key>();
            private static int s_reupAt, s_reupDone, s_reupFrames, s_mainBiomes; private static long s_reupTicks; private static string s_reupWhy;
            internal static void Reupload(string why)
            {
                s_reup.Clear(); s_reupAt = 0; s_reupDone = 0; s_reupFrames = 0; s_reupTicks = 0; s_reupWhy = why;   // a repeat restarts from the first shown tile; no queue growth
                foreach (var kv in s_views) if (kv.Value.Shown) s_reup.Add(kv.Key);
            }
            internal static void PumpReupload()
            {
                if (s_reupAt >= s_reup.Count) return;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int n = 0; n < ReuploadPerFrame && s_reupAt < s_reup.Count; s_reupAt++)
                    if (s_views.TryGetValue(s_reup[s_reupAt], out var v) && v.Shown && v.M != null) { UploadMask(v); n++; s_reupDone++; }
                s_reupFrames++; s_reupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                if (s_reupAt < s_reup.Count) return;
                EasyBakeLog.Info($"[FarTerrain] far mask re-upload ({s_reupWhy}): {s_reupDone} tile(s) in {s_reupFrames} frame(s), {s_reupTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.0} ms total"
                    + (s_mainBiomes > 0 ? $", {s_mainBiomes} tile(s) needed biomes / heights on the main thread" : "") + " (1.2.501)");
                s_reup.Clear(); s_reupAt = 0; s_mainBiomes = 0;
            }

            /// <summary>Show / re-upload: the tile's mask, with the owner's final G on HD-look tiles. The view keeps the cached world-gen mask (v.M) untouched.</summary>
            internal static void UploadMask(View v)
            {
                var f = SnowG;
                if (!HdLookActive || f == null) { v.Mask.SetPixels(v.M); v.Mask.Apply(false, false); return; }
                if (v.B == null || v.BH == null) { FillBiomes(v.K, out v.B, out v.BH); s_mainBiomes++; }   // the source arrived after this tile was built
                if (v.Up == null) v.Up = new Color[GridVerts];
                float s = v.K.Spacing, minX = v.K.MinX, minZ = v.K.MinZ;
                for (int i = 0; i < V; i++)
                    for (int j = 0; j < V; j++)
                    {
                        int n = i * V + j; var m = v.M[n];
                        float g;
                        try { g = f(minX + j * s, minZ + i * s, v.B[n], v.BH[n], m.g); }
                        catch (Exception e) { SnowG = null; SnowSourceOn = false; EasyBakeLog.Warn("[FarTerrain] the pushed far terrain snow threw, HD-look far tiles keep the world-gen G: " + e.Message); v.Mask.SetPixels(v.M); v.Mask.Apply(false, false); return; }
                        if (!(g >= 0f)) g = m.g;   // negative / NaN = keep the base
                        m.g = g; v.Up[n] = m;
                    }
                v.Mask.SetPixels(v.Up); v.Mask.Apply(false, false);
            }

            // ---- 1.2.501 season texture (EbmApi.SetFarTerrainTexture): allow-list only; HD far material always, EBM/Heightmap only without HD (V runs on vanilla's arrays)
            private static readonly string[] TexAllowed = { "_DiffuseArrayTex" };
            private static readonly Dictionary<string, Texture> s_texPush = new Dictionary<string, Texture>(), s_texOrig = new Dictionary<string, Texture>();
            internal static bool SetTexture(string property, Texture tex)
            {
                if (Array.IndexOf(TexAllowed, property) < 0) { EasyBakeLog.Warn($"[FarTerrain] SetFarTerrainTexture('{property}') refused: only {string.Join(", ", TexAllowed)} (1.2.501)"); return false; }
                if (tex == null) s_texPush.Remove(property); else s_texPush[property] = tex;
                ApplyTextures("pushed");
                return true;
            }
            /// <summary>MakeMaterial (a new far material): remember its own values of the allowed properties, then apply what the owner pushed.</summary>
            internal static void OnMaterialMade()
            {
                s_texOrig.Clear();
                if (s_mat == null) return;
                foreach (var p in TexAllowed) if (s_mat.HasProperty(p)) s_texOrig[p] = s_mat.GetTexture(p);
                ApplyTextures("material made");
            }
            private static void ApplyTextures(string why)
            {
                if (s_mat == null) return;
                bool allowed = HdLookActive || s_hdOurs || !HdPresent;   // 1.2.506: + HD textures on our shader (the owner's season array = a tint of what its near ground draws)
                foreach (var p in TexAllowed)
                {
                    if (!s_mat.HasProperty(p) || !s_texOrig.TryGetValue(p, out var orig)) continue;
                    Texture want = allowed && s_texPush.TryGetValue(p, out var t) && t != null ? t : orig;   // a destroyed pushed texture (Unity null) = the material's own back
                    if (s_mat.GetTexture(p) == want) continue;
                    s_mat.SetTexture(p, want);
                    EasyBakeLog.Info($"[FarTerrain] far material {p}: {(want == orig ? "its own" : "the owner's")} ('{(want != null ? want.name : "none")}', {why}) (1.2.501)");
                }
            }
        }

        // 1.2.501: biome + BASE height per vertex (GetBiomeHeight, then FAT's near-blend height when FAT answers, before edits; the same call as vanilla's near/distant builder, REF HeightmapBuilder.cs:229/:233 worldGen.GetBiomeHeight),
        // kept in memory only for the owner's far snow (never in the disk cache). Any thread.
        private static void FillBiomes(Key k, out Heightmap.Biome[] b, out float[] bh)
        {
            var wg = WorldGenerator.instance;
            b = TilePool.B(); bh = TilePool.F();   // 1.2.504: rented (every cell is written below); handed back with the tile
            float s = k.Spacing, minX = k.MinX, minZ = k.MinZ;
            for (int i = 0; i < V; i++)
                for (int j = 0; j < V; j++)
                {
                    int n = i * V + j; float wx = minX + j * s, wz = minZ + i * s;
                    b[n] = wg.GetBiome(wx, wz);
                    float h = wg.GetBiomeHeight(b[n], wx, wz, out _);
                    if (Fat.Height != null) { try { if (Fat.Height(wx, wz, out float fh)) h = fh; } catch { } }   // the same as Build (:1027): the height the tile draws, before edits
                    bh[n] = h;
                }
        }

        // 1.2.504 proof line: one Info per 60 s while far tiles build (silent when the window had no build, cache read or keep).
        private static void Proof()
        {
            float now = Time.unscaledTime;
            if (s_nextProof == 0f) { s_nextProof = now + 60f; return; }
            if (now < s_nextProof) return;
            s_nextProof = now + 60f;
            long built = Interlocked.Exchange(ref s_wBuilt, 0), disk = Interlocked.Exchange(ref s_wDisk, 0), kept = Interlocked.Exchange(ref s_wKept, 0);
            long cmp = Interlocked.Exchange(ref s_wCompares, 0), bad = Interlocked.Exchange(ref s_wMismatch, 0);
            long changed = Interlocked.Exchange(ref s_wPrintChanged, 0), revs = Interlocked.Exchange(ref s_wRevs, 0);
            long rented = Interlocked.Read(ref TilePool.Rented), fresh = Interlocked.Read(ref TilePool.Fresh), bytes = Interlocked.Read(ref TilePool.FreshBytes);
            long dr = rented - s_lastRented, df = fresh - s_lastFresh, db = bytes - s_lastFreshBytes;
            s_lastRented = rented; s_lastFresh = fresh; s_lastFreshBytes = bytes;
            if (built + disk + kept + dr + df + revs == 0) return;
            EasyBakeLog.Info($"[FarTerrain] last 60 s (1.2.504): FAT edit revisions {revs}; tiles built {built}, from cache {disk}, kept (edit print unchanged, no rebuild) {kept}, rebuilt because their edit print changed {changed}; far-worker tile arrays reused {dr} / new {df} ({db / 1048576.0:0.0} MB of new tile arrays)"
                + (cmp > 0 ? $"; kept-tile compares {cmp}, mismatches {bad}" : ""));
        }

        private static void FollowFat()
        {
            Fat.Bind();
            // 1.2.470: FAT-fed true colours follow FAT's biome paint (PaintVersion), never its winter revision (the look's snow is the shader's, per pixel)
            if (Fat.PaintVersion != null)
            {
                int pv; try { pv = Fat.PaintVersion(); } catch { pv = s_fatPaint; }
                if (pv != s_fatPaint) { bool firstP = s_fatPaint == int.MinValue; s_fatPaint = pv; if (!firstP) { NewContent(); s_epoch++; Interlocked.Exchange(ref s_threadEpochBox, s_epoch); s_planAt = new Vector3(float.NaN, 0f, float.NaN); EasyBakeLog.Debug($"[FarTerrain] FAT biome paint changed (PaintVersion {pv}): far tiles rebuild"); } }
            }
            if (Fat.Revision == null) return;
            int r; try { r = Fat.Revision(); } catch { return; }
            if (r == s_fatRev) return;
            bool first = s_fatRev == int.MinValue; s_fatRev = r;
            if (first) return;
            Interlocked.Increment(ref s_wRevs);   // 1.2.504 proof: FAT revisions seen
            s_epoch++; Interlocked.Exchange(ref s_threadEpochBox, s_epoch);   // every tile is re-checked with the new edits; old views stay until replaced. 1.2.504: a tile whose edit print did not change is KEPT (Build), not rebuilt
            s_planAt = new Vector3(float.NaN, 0f, float.NaN);
            EasyBakeLog.Debug($"[FarTerrain] FAT far edits changed (revision {r}): far tiles rebuild");
        }
        private static int s_threadEpochBox = 1;
        private static int s_fatPaint = int.MinValue;   // 1.2.470

        private static void Report()
        {
            Proof();   // 1.2.504
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
            int content = Volatile.Read(ref s_contentEpochBox);   // 1.2.504: read before the inputs below
            uint print = EditPrint(k, out var deltas);
            try { return BuildInner(k, print, content, deltas); }
            finally { if (deltas != null) foreach (var a in deltas.Values) TilePool.Give(a); }   // 1.2.504: the kept edit deltas are only needed while this tile builds
        }

        private static TileData BuildInner(Key k, uint print, int content, Dictionary<long, float[]> deltas)
        {
            var wg = WorldGenerator.instance;
            float s = k.Spacing, minX = k.MinX, minZ = k.MinZ;
            // 1.2.504 build once: the shown view already holds this key with the same edit print under the same content epoch = the same tile. Keep it
            // (a marker, no arrays), unless the main thread asked for a real build of this key; with Debug on, 1 keep in 16 is built anyway and compared.
            bool forced = s_forceBuild.TryRemove(k, out _);
            bool haveShown = s_shownSig.TryGetValue(k, out long shown);
            bool keep = !forced && haveShown && shown == Sig(content, print);
            if (haveShown && !keep && (int)(shown >> 32) == content && (uint)shown != print) Interlocked.Increment(ref s_wPrintChanged);   // the proof: an edit under this tile changed its print, so it rebuilds
            if (keep && !(EasyBakeLog.DebugOn() && (Interlocked.Increment(ref s_keepSeq) & 15) == 0))
                return new TileData { K = k, Print = print, Content = content, Keep = true };
            bool keepBiomes = VedrFar.SnowSourceOn;   // 1.2.501: only while an owner's far snow source is registered (else nothing kept)
            if (TryRead(k, print, out var cached))
            {
                Interlocked.Increment(ref s_fromDisk); Interlocked.Increment(ref s_wDisk);
                if (keepBiomes) FillBiomes(k, out cached.B, out cached.BH);   // 1.2.501: not in the disk cache
                cached.Content = content; cached.Keep = keep;
                return cached;
            }
            var d = new TileData { K = k, Print = print, Content = content, Keep = keep, H = TilePool.F(), M = TilePool.M() };   // 1.2.504: rented (every cell is written below)
            if (keepBiomes) { d.B = TilePool.B(); d.BH = TilePool.F(); }
            var fatFn = Fat.TrueColor ?? Fat.Color;   // 1.2.470: FAT 0.2.1492+ (far API v5) TryFarTrueColor = no winter delta (the shader adds Vedr's snow); older = TryFarColor (winterized: shader snow off, VedrFar.ShaderSnow)
            bool fatColour = fatFn != null;
            if (fatColour) d.C = TilePool.C(); else d.S = new BiomeSector[GridVerts];
            for (int i = 0; i < V; i++)
                for (int j = 0; j < V; j++)
                {
                    int n = i * V + j;
                    float wx = minX + j * s, wz = minZ + i * s;
                    var biome = wg.GetBiome(wx, wz);
                    float h = wg.GetBiomeHeight(biome, wx, wz, out Color mask);   // vanilla's distant method (HeightmapBuilder.Build, m_distantLod)
                    if (Fat.Height != null) { try { if (Fat.Height(wx, wz, out float fh)) h = fh; } catch { } }
                    if (keepBiomes) { d.B[n] = biome; d.BH[n] = h; }   // 1.2.501 ([2ic] MUST): the BASE height the tile draws = GetBiomeHeight (REF HeightmapBuilder.cs:229/:233), then FAT's near-blend height when FAT answers (the line above); BEFORE terrain-edit deltas, as the near m_baseHeights
                    if (deltas != null) h += DeltaAt(deltas, wx, wz);
                    d.H[n] = h; d.M[n] = mask;
                    if (fatColour) { Color32 c = default; bool ok = false; try { ok = fatFn(wx, wz, out c); } catch { } d.C[n] = ok ? c : default; if (!ok) { TilePool.Give(d.C); d.C = null; d.S = new BiomeSector[GridVerts]; fatColour = false; for (int b = 0; b <= n; b++) d.S[b] = wg.GetBiomeSector(minX + (b % V) * s, minZ + (b / V) * s); } }
                    else d.S[n] = wg.GetBiomeSector(wx, wz);
                }
            Interlocked.Increment(ref s_built); Interlocked.Increment(ref s_wBuilt);
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
                    // 1.2.504 (deep-row fix, capture row "793.8 MB in 49,162 allocations of EXACTLY 16,932 bytes"): one scratch array, refilled per zone; it is
                    // kept (and a new scratch rented) only when FAT returns an edit. FAT's TryZoneDelta writes all 4225 cells on true and none on false.
                    var arr = s_editScratch ?? (s_editScratch = TilePool.F()); uint p;
                    bool ok; try { ok = Fat.EditDelta(zx, zz, arr, out p); } catch { ok = false; p = 0; }
                    if (!ok) continue;
                    s_editScratch = null;   // this array is the zone's delta now (handed back by Build when the tile is built)
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
        private static string CachePath(Key k, uint print) => s_cacheDir == null ? null : Path.Combine(s_cacheDir, $"v{CacheVersion}{(VedrFar.TrueMode ? "t" : "")}{(Fat.PaintVersion != null && s_fatPaint != int.MinValue ? "p" + s_fatPaint : "")}_{k.L}_{k.X}_{k.Z}_{print:x8}.bin");   // 1.2.470: true-colour tiles in their own files

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
                    d = new TileData { K = k, Print = print, H = TilePool.F(), C = TilePool.C(), M = TilePool.M() };   // 1.2.504: rented (every cell is read below)
                    for (int i = 0; i < GridVerts; i++) d.H[i] = br.ReadSingle();
                    for (int i = 0; i < GridVerts; i++) { uint c = br.ReadUInt32(); d.C[i] = new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24)); }
                    for (int i = 0; i < GridVerts; i++) d.M[i] = new Color(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                }
                return true;
            }
            catch { TilePool.Give(d); d = null; try { File.Delete(path); } catch { } return false; }   // 1.2.504: a half-read tile hands its arrays back
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
            internal static ColorFn TrueColor; internal static Func<int> PaintVersion;   // 1.2.470: FAT far API v5 TryFarTrueColor (no winter, any thread) + PaintVersion (biome paint recolours)
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
                    // 1.2.470 ([worldgen] 11-19: TryFarColor carries the winter delta, FatFarData.cs:59-67 WinterizeAnyThread, so FAT colour + EBM shader snow = double snow):
                    // from v5 the TRUE colour (no winter); its far tiles follow FAT's PaintVersion (biome paint), never FarWinterRevision (no melt recolours)
                    if (apiVer >= 5 && Color != null) { TrueColor = Make<ColorFn>(t, "TryFarTrueColor"); var pv = t.GetProperty("PaintVersion", BindingFlags.Public | BindingFlags.Static); if (TrueColor != null && pv != null) PaintVersion = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), pv.GetGetMethod()); }
                    var rev = t.GetProperty("FarEditRevision", BindingFlags.Public | BindingFlags.Static);
                    if (rev != null) Revision = (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), rev.GetGetMethod());
                    EasyBakeLog.Info($"[FarTerrain] FAT far data API v{ver.GetValue(null)}: heights {(Height != null ? "yes" : "no")}, edits {(EditDelta != null ? "yes" : "no")}, colours {(Color != null ? "yes" : "no")}, revision {(Revision != null ? "yes" : "no")}");
                }
                catch (Exception e) { Height = null; EditDelta = null; Color = null; Revision = null; TrueColor = null; PaintVersion = null; EasyBakeLog.Warn("[FarTerrain] FAT far data API could not be bound: " + e.Message); }
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
