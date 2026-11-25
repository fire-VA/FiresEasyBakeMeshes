using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    internal struct FarVegInstance { public int Kind; public Vector3 Pos; public float Yaw; public float Scale; public float TiltX, TiltZ; }   // Yaw / tilt in degrees; Pos = world, ground + m_groundOffset

    // 1.2.459 GROUND SOURCES ([worldgen] + [visual], Fire 11-18: "ebm would need to be able to properly read the world information from FAT when both
    // are installed"). [visual]'s FarVegSource fills FarVegReplay.Src* on the main thread before Init: with FAT (FatApi FarDataApiVersion >= 3) the
    // FAT path (FatApi.FarSampleZone / FarSampleSector / FarBiomeAt / FarForestFactor, FAT 0.2.1483); without FAT, Harmony reverse-patch snapshots of
    // vanilla's WorldGenerator originals. The replay never calls a patched WorldGenerator method on a worker.
    internal delegate bool FarZoneFn(int zoneX, int zoneZ, int width, float scale, float[] heights, float[] maskAlpha, BiomeSector[] corners4, out bool edge, out Heightmap.Biome painted);
    internal delegate Heightmap.Biome FarBiomeAtFn(float x, float z, Heightmap.Biome cornerWeighted);
    internal delegate BiomeSector FarSectorFn(float x, float z);
    internal delegate float FarForestFn(float x, float z);
    internal delegate Heightmap.Biome VanBiomeFn(float x, float z);
    internal delegate float VanBiomeHeightFn(Heightmap.Biome biome, float x, float z, out Color mask);

    // [worldgen] 11-18: vanilla's ZoneSystem.PlaceVegetation (assembly_valheimREF1.0.15 ZoneSystem.cs:746-933) replayed for the far KINDS only,
    // for zones that are NOT loaded (no Heightmap, no collider), with NO FAT references and NO Heightmap.* / ZoneSystem.* instance calls:
    //  - Vanilla seeds every m_vegetation entry on its own (seed + zone.x*4271 + zone.y*9187 + prefab hash), so replaying only our entries is exact
    //    in its draw sequence.
    //  - GROUND: each zone's height grid is vanilla HeightmapBuilder.Build's non-distant path (HeightmapBuilder.cs), which vanilla itself runs on
    //    its builder thread: WorldGenerator.GetBiomeSector / GetBiome / GetBiomeHeight per vertex, the 4-corner smoothstep blend. On it:
    //      GetGroundData  = height + face normal on the collision mesh's own triangles (Heightmap.RebuildCollisionMesh index order), biome by
    //                       Heightmap.GetBiome's corner weights, area by IsBiomeEdge; a point outside the zone reads the neighbour zone's grid
    //                       (the ray hits that zone's collider);
    //      GetVegetationMask = the paint mask alpha (GetBiomeHeight's out mask, WorldToVertexMask, clamp), 8-bit like the RGBA32 texture (UNCONFIRMED);
    //      GetOceanDepth  = UpdateCornerDepths' four corners, lerped as Heightmap.GetOceanDepth;
    //      GetTerrainDelta = 10 x insideUnitCircle on the grid ground (ZoneSystem.GetTerrainDelta / GetGroundHeight).
    //  - SKIPPED (no colliders far out; marked in code): IsBlocked (earlier entries' trees / rocks, location pieces), the static-solid snap, location
    //    terrain flattening and player terrain edits (Heightmap.ApplyModifiers). Clear areas come from ZoneSystem.m_locationInstances (snapshot at
    //    Init; a host / server has them, a client has none). The seen-trees rule replaces a zone's replay once its real trees are read.
    //  - RNG: FarVegRng on any thread once FarVegRngSelfTest passed (WorkerOk); otherwise PlaceZone must run on the MAIN thread and uses
    //    UnityEngine.Random (state saved and restored, as vanilla does).
    //  - Each placed instance consumes its prefab's Awake draws: the census when measured this session (host only), else AwakeMode decides
    //    (1.2.449: 0 = dedicated-server generation, 1 = LodFadeInOut words for host generation, 2 = probe-measured); MatchZoneDetail compares them.
    internal static class FarVegReplay
    {
        internal const int Version = 2;   // bump when the replay's output changes (cache key); 2 = 1.2.449 (lazy grid, same output; Awake-word modes; key the cache with AwakeMode too). 1.2.450 adds only the Debug trace.

        private static volatile WorldGenerator s_gen;
        private static int s_seed;
        private static int s_w = 64;          // the zone prefab Heightmap's m_width / m_scale, read at Init
        private static float s_scale = 1f;
        private static volatile Dictionary<long, Vector3> s_clear = new Dictionary<long, Vector3>();   // zone -> (x, z, radius): its location's clear area

        internal static bool WorkerOk => FarVegRngSelfTest.Trusted;

        internal static long Key(int zx, int zz) => ((long)zx << 32) | (uint)zz;

        // 1.2.458 (disk cache key, any thread): the zone's location clear area (x, z, radius; zero = none) the replay uses. It differs per peer (a host
        // has m_locationInstances, a client none), so the per-zone cache key carries it. CensusActive: a Debug census changes the Awake words mid-session
        // (s_awakeSnap), so the runner neither reads nor writes the cache while it holds measurements.
        internal static Vector3 ClearOf(int zx, int zz) { s_clear.TryGetValue(Key(zx, zz), out Vector3 c); return c; }
        internal static bool CensusActive => s_awakeSnap.Count > 0;
        internal static int Seed => s_seed;

        // Main thread, after ZoneSystem.Start, on every world start. false = not ready (retry).
        // 1.2.459: the ground sources (see the delegate note above). SrcZone non-null = the FAT path.
        internal static volatile FarZoneFn SrcZone;
        internal static volatile FarBiomeAtFn SrcBiomeAt;
        internal static volatile FarSectorFn SrcSector;
        internal static volatile FarForestFn SrcForest;
        internal static volatile VanBiomeFn SrcBiome;
        internal static volatile VanBiomeHeightFn SrcBiomeHeight;
        internal static bool FatPath => SrcZone != null;

        internal static bool Init()
        {
            var gen = WorldGenerator.instance;
            var zs = ZoneSystem.instance;
            if (gen == null || zs == null || zs.m_zonePrefab == null) return false;
            // 1.2.459: every source this path reads must be set; no silent fallback to a patched call on a worker.
            string missing = SrcZone != null
                ? (SrcSector == null ? "SrcSector " : "") + (SrcForest == null ? "SrcForest" : "")
                : (SrcSector == null ? "SrcSector " : "") + (SrcForest == null ? "SrcForest " : "") + (SrcBiome == null ? "SrcBiome " : "") + (SrcBiomeHeight == null ? "SrcBiomeHeight" : "");
            if (missing.Length > 0) { EasyBakeLog.Info($"[FarVeg] replay not ready: ground source(s) missing on the {(SrcZone != null ? "FAT" : "vanilla")} path: {missing.Trim()}."); return false; }
            var hm = zs.m_zonePrefab.GetComponentInChildren<Heightmap>();
            if (hm == null) return false;
            if (hm.IsDistantLod) EasyBakeLog.Warn("[FarVeg] the zone prefab's Heightmap is a distant LOD; the replay uses the non-distant build (vanilla zones never are).");
            s_w = hm.m_width; s_scale = hm.m_scale;
            s_seed = gen.GetSeed();
            var clear = new Dictionary<long, Vector3>();
            if (zs.m_locationInstances != null)
                foreach (var kv in zs.m_locationInstances)
                {
                    var loc = kv.Value.m_location;
                    if (loc == null || !loc.m_clearArea) continue;   // ZoneSystem.PlaceLocations: only m_clearArea adds one (radius m_exteriorRadius)
                    clear[Key(kv.Key.x, kv.Key.y)] = new Vector3(kv.Value.m_position.x, kv.Value.m_position.z, loc.m_exteriorRadius);
                }
            s_clear = clear;
            s_gen = gen;
            System.Threading.Interlocked.Increment(ref s_world);   // every thread's grid cache drops grids of the previous world
            s_probe = null;
            EasyBakeLog.Info($"[FarVeg] replay v{Version} ready: seed {s_seed}, zone grid {s_w} x {s_scale:0.##} m, {clear.Count} location clear area(s){(clear.Count == 0 ? " (none known on this peer)" : "")}, " +
                             $"Awake words: {AwakeModeName(AwakeMode)} (the RNG path is named by the self-test line); ground: " +
                             (SrcZone != null ? "FAT (FatApi.FarSampleZone, FAT's real world generation)" : "vanilla originals (reverse-patch snapshots)") + ".");
            return true;
        }

        // ---------------------------------------------------------------- Awake words (1.2.449)
        // Random words each placed instance's Instantiate (Awake / OnEnable) consumed in the REAL generation. They depend on WHO generated the zone:
        // only a server / host generates (ZoneSystem.PokeLocalZone :651: a client spawns zones in SpawnMode.Client), and the one camera-dependent
        // tree draw (LodFadeInOut.Awake, LodFadeInOut.cs:15-26) happens only when a camera exists and is > 20 m away: none on a dedicated server,
        // nearly every tree on a single-player / listen host. A census measured this session always wins.
        //   0 = 0 words (dedicated-server generation)    1 = LodFadeInOut words (host generation, camera far)    2 = the probe's measured words
        internal static volatile int AwakeMode = 1;   // 1.2.454: PROVEN from the 453 leg's saved world (every real Pinetree_01 / FirTree_small / FirTree located in its RNG stream): a placed tree burns 7 + groundTilt + 1 words ON A DEDICATED SERVER too, the 1 = LodFadeInOut (LodFadeWords). Mode 0 was wrong.

        // 1.2.451: emulate vanilla's IsBlocked (ZoneSystem.cs:795 / :1929) from the replay's OWN earlier placements in the zone: every entry before
        // the last far kind whose prefab has block-layer colliders is replayed too (not drawn), and each candidate's vertical ray is tested against
        // the colliders placed so far (FarVegKinds.Entry.Shapes). Default OFF until MatchZoneTrace's variant D proves it; then [visual] keys it.
        // Not emulated: objects of neighbour zones generated earlier, location pieces (clear areas cover a location's footprint on a host).
        internal static volatile bool EmulateBlocking = false;

        // Per PlaceZone call: what the replay placed so far (blocker and kind entries), in a 4 m spatial hash, for the emulated IsBlocked.
        internal sealed class ZoneBlocks
        {
            private const float Cell = 4f;
            private readonly List<(Vector3 P, Quaternion Q, float S, FarVegKinds.Entry E)> m_items = new List<(Vector3, Quaternion, float, FarVegKinds.Entry)>();
            private readonly Dictionary<long, List<int>> m_cells = new Dictionary<long, List<int>>();
            private static long CellKey(int cx, int cz) => ((long)cx << 32) | (uint)cz;

            public void Add(Vector3 p, Quaternion q, float s, FarVegKinds.Entry e)
            {
                float r = e.FootR * s;
                int idx = m_items.Count;
                m_items.Add((p, q, s, e));
                int x0 = Mathf.FloorToInt((p.x - r) / Cell), x1 = Mathf.FloorToInt((p.x + r) / Cell);
                int z0 = Mathf.FloorToInt((p.z - r) / Cell), z1 = Mathf.FloorToInt((p.z + r) / Cell);
                for (int cz = z0; cz <= z1; cz++)
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        long k = CellKey(cx, cz);
                        if (!m_cells.TryGetValue(k, out var l)) m_cells[k] = l = new List<int>(4);
                        l.Add(idx);
                    }
            }

            // Vanilla IsBlocked(p): a ray from p.y + 2000 down 10000 m (p.y is 0 at that point, ZoneSystem.cs:782), i.e. the vertical line at (x, z).
            public bool Blocked(float x, float z) => By(x, z) != null;

            // 1.2.455: what blocked (prefab, shape type, distance), or null.
            public string By(float x, float z)
            {
                if (!m_cells.TryGetValue(CellKey(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(z / Cell)), out var l)) return null;   // [visual] 1.2.455 build fix: By() returns string (was "return false")
                foreach (int idx in l)
                {
                    var it = m_items[idx];
                    float r = it.E.FootR * it.S, dx = x - it.P.x, dz = z - it.P.z;
                    if (dx * dx + dz * dz > r * r) continue;
                    foreach (var s in it.E.Shapes) if (LineHits(s, it.P, it.Q, it.S, x, z)) return $"{s.Owner} {(s.Type == 0 ? "sphere" : s.Type == 1 ? "capsule" : s.Type == 2 ? "box" : s.Type == 3 ? "mesh" : "convex")} at ({it.P.x:0.00},{it.P.z:0.00}) {Mathf.Sqrt(dx * dx + dz * dz):0.00} m";
                }
                return null;
            }

            private static Vector3 W(Vector3 v, Vector3 p, Quaternion q, float s) => p + q * (v * s);
            private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

            private static bool LineHits(FarVegKinds.Shape sh, Vector3 p, Quaternion q, float s, float x, float z)
            {
                switch (sh.Type)
                {
                    case 0:
                    {
                        Vector3 c = W(sh.A, p, q, s); float r = sh.R * s, dx = x - c.x, dz = z - c.z;
                        return dx * dx + dz * dz <= r * r;
                    }
                    case 1:
                    {
                        Vector3 a = W(sh.A, p, q, s), b = W(sh.B, p, q, s); float r = sh.R * s;
                        float abx = b.x - a.x, abz = b.z - a.z, apx = x - a.x, apz = z - a.z, l2 = abx * abx + abz * abz;
                        float t = l2 > 1e-9f ? Mathf.Clamp01((apx * abx + apz * abz) / l2) : 0f;
                        float ex = apx - t * abx, ez = apz - t * abz;
                        return ex * ex + ez * ez <= r * r;
                    }
                    case 3:
                    {
                        // 1.2.455: the vertical line hits a triangle when (x, z) is inside its XZ projection
                        var pts = sh.Pts;
                        for (int i = 0; i + 2 < pts.Length; i += 3)
                        {
                            Vector3 a = W(pts[i], p, q, s), b = W(pts[i + 1], p, q, s), c = W(pts[i + 2], p, q, s);
                            float d1 = (x - b.x) * (a.z - b.z) - (a.x - b.x) * (z - b.z);
                            float d2 = (x - c.x) * (b.z - c.z) - (b.x - c.x) * (z - c.z);
                            float d3 = (x - a.x) * (c.z - a.z) - (c.x - a.x) * (z - a.z);
                            bool neg = d1 < 0f || d2 < 0f || d3 < 0f, pos = d1 > 0f || d2 > 0f || d3 > 0f;
                            if (!(neg && pos)) return true;
                        }
                        return false;
                    }
                    case 4:
                    {
                        // 1.2.455: a convex collider = the hull of its vertices; inside the XZ hull (monotone chain) = hit
                        int n = sh.Pts.Length;
                        if (n < 3) return false;
                        var v = new Vector2[n];
                        for (int i = 0; i < n; i++) { Vector3 w = W(sh.Pts[i], p, q, s); v[i] = new Vector2(w.x, w.z); }
                        Array.Sort(v, (u1, u2) => u1.x != u2.x ? u1.x.CompareTo(u2.x) : u1.y.CompareTo(u2.y));
                        var hull = new Vector2[2 * n]; int k = 0;
                        for (int i = 0; i < n; i++) { while (k >= 2 && Cross(hull[k - 2], hull[k - 1], v[i]) <= 0f) k--; hull[k++] = v[i]; }
                        for (int i = n - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(hull[k - 2], hull[k - 1], v[i]) <= 0f) k--; hull[k++] = v[i]; }
                        var pt = new Vector2(x, z);
                        for (int i = 0; i + 1 < k; i++) if (Cross(hull[i], hull[i + 1], pt) < 0f) return false;
                        return k >= 3;
                    }
                    default:
                    {
                        // slab test of the vertical line (x, *, z) against the oriented box (centre c, half-axes u, v, w)
                        Vector3 c = W(sh.A, p, q, s), lp = new Vector3(x, c.y, z) - c;
                        float tmin = float.NegativeInfinity, tmax = float.PositiveInfinity;
                        Vector3[] ax = { q * (sh.U * s), q * (sh.V * s), q * (sh.W * s) };
                        for (int i = 0; i < 3; i++)
                        {
                            float l2 = ax[i].sqrMagnitude;
                            if (l2 < 1e-12f) return false;   // a flat box: a vertical line hits it only edge-on
                            float o = Vector3.Dot(lp, ax[i]) / l2, d = ax[i].y / l2;
                            if (Mathf.Abs(d) < 1e-9f) { if (o < -1f || o > 1f) return false; continue; }
                            float t1 = (-1f - o) / d, t2 = (1f - o) / d;
                            if (t1 > t2) { float tt = t1; t1 = t2; t2 = tt; }
                            if (t1 > tmin) tmin = t1;
                            if (t2 < tmax) tmax = t2;
                            if (tmin > tmax) return false;
                        }
                        return true;
                    }
                }
            }
        }
        internal static string AwakeModeName(int m) => m == 1 ? "LodFadeInOut + fixed Awake draws (any generating peer, proven on a dedi 1.2.454)" : m == 2 ? "probe-measured" : "0 (no Awake draws: DISPROVEN 1.2.454, a dedi burns LodFadeInOut too)";

        private static int WordsFor(FarVegKinds.Entry e, int mode, Dictionary<int, int> census, Dictionary<int, int> probe)
        {
            if (census.TryGetValue(e.PrefabHash, out int c) && c >= 0) return c;
            if (mode == 1) return e.LodFadeWords + e.FixedWords;   // 1.2.454: + camera-independent Awake draws
            if (mode == 2 && probe != null && probe.TryGetValue(e.PrefabHash, out int p) && p >= 0) return p;
            return 0;
        }

        // The probe (main thread, Debug): instantiates each kind's prefab once with ZNetView.m_forceDisableInit (ZNetView.cs:36: no ZDO is made),
        // 1000 m from the camera (LodFadeInOut's > 20 m branch), and counts the words its Awake / OnEnable consumed (FarVegRng stepped to the after
        // state), then DestroyImmediate. Components that bail out without a ZDO (TreeBase etc.) may draw less than in a real generation:
        // UNCONFIRMED; the match detail compares all three modes.
        private static volatile Dictionary<int, int> s_probe;
        internal static Dictionary<int, int> ProbeWords()
        {
            var have = s_probe;
            if (have != null) return have;
            var res = new Dictionary<int, int>();
            var cam = Camera.main;
            Vector3 at = (cam != null ? cam.transform.position : Vector3.zero) + new Vector3(1000f, 0f, 1000f);
            var saved = UnityEngine.Random.state;
            bool oldForce = ZNetView.m_forceDisableInit;
            try
            {
                ZNetView.m_forceDisableInit = true;
                for (int k = 0; k < FarVegKinds.Count; k++)
                {
                    var prefab = FarVegKinds.Prefab(k);
                    if (prefab == null) continue;
                    int hash = prefab.name.GetStableHashCode();
                    UnityEngine.Random.InitState(20261118 + k);
                    FarVegRngSelfTest.ReadState(out int a, out int b, out int c, out int d);
                    GameObject go = null;
                    try { go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity); }
                    catch (Exception ex) { EasyBakeLog.Debug($"[FarVeg] probe {prefab.name}: Instantiate threw {ex.GetType().Name}"); }
                    FarVegRngSelfTest.ReadState(out int a1, out int b1, out int c1, out int d1);
                    if (go != null) UnityEngine.Object.DestroyImmediate(go);
                    var r = FarVegRng.FromState(a, b, c, d);
                    int words = 0;
                    while (!r.SameState(a1, b1, c1, d1) && words <= MaxSteps) { r.NextU32(); words++; }
                    res[hash] = words > MaxSteps ? -1 : words;
                }
            }
            finally { ZNetView.m_forceDisableInit = oldForce; UnityEngine.Random.state = saved; }
            s_probe = res;
            return res;
        }

        // ---------------------------------------------------------------- zone ground (worker-safe)
        // 1.2.449: LAZY grid. A vertex's height + mask depend only on (zone, vertex index), so each is computed the first time a placement point,
        // a terrain-delta sample, a mask read or a corner depth needs it, with the exact HeightmapBuilder.Build steps. 447 built all (w+1)^2
        // vertices of every touched zone (502 ms a 64-zone cell); a zone's placement reads a few hundred.
        private sealed class Grid
        {
            public float Cx, Cz, Ox, Oz;   // zone centre (the Heightmap's transform position); vertex (0,0)
            public float[] H;              // (w+1)^2 heights, row-major by z; NaN = not computed yet
            public float[] M;              // (w+1)^2 vegetation mask (paint mask alpha)
            public readonly Heightmap.Biome[] Corner = new Heightmap.Biome[4];   // m_cornerBiomes[i].Biome
            public Heightmap.Biome B1, B2, B3, B4;                              // HeightmapBuilder.Build's biome1..4 (GetBiome at the corners)
            public bool Same, Edge, HaveOD;
            public Heightmap.Biome Painted;                                      // 1.2.459 FAT path: biomes FAT paint adds over the zone (HaveBiome)
            public readonly float[] OD = new float[4];                           // m_oceanDepth
            public WorldGenerator Gen;
        }

        private static Grid NewGrid(WorldGenerator wg, int zx, int zz)
        {
            int w = s_w; float scale = s_scale;
            int num1 = w + 1;
            var g = new Grid { Gen = wg, Cx = zx * 64f, Cz = zz * 64f, H = new float[num1 * num1], M = new float[num1 * num1] };
            g.Ox = g.Cx + (float)((double)w * (double)scale * -0.5); g.Oz = g.Cz + (float)((double)w * (double)scale * -0.5);
            // 1.2.459 FAT PATH: the whole zone from FAT's real HeightmapBuilder.Build (FatApi.FarSampleZone): heights, mask, corner sectors, edge,
            // painted biomes, exactly what the zone's Heightmap gets. A refusal (FAT stood down mid-session) leaves a sea-floor zone: nothing places.
            var zone = SrcZone;
            if (zone != null)
            {
                var corners = new BiomeSector[4];
                if (zone(zx, zz, w, scale, g.H, g.M, corners, out bool edge, out Heightmap.Biome painted))
                {
                    for (int i = 0; i < g.M.Length; i++) g.M[i] = Mathf.Round(Mathf.Clamp01(g.M[i]) * 255f) / 255f;   // RGBA32 paint mask, as below
                    for (int k = 0; k < 4; k++) g.Corner[k] = corners[k] != null ? corners[k].Biome : Heightmap.Biome.Ocean;
                    g.Edge = edge; g.Painted = painted;
                    return g;
                }
                for (int i = 0; i < g.H.Length; i++) { g.H[i] = -1000f; g.M[i] = 0f; }
                for (int k = 0; k < 4; k++) g.Corner[k] = Heightmap.Biome.Ocean;
                return g;
            }
            for (int i = 0; i < g.H.Length; i++) g.H[i] = float.NaN;
            // VANILLA PATH: HeightmapBuilder.Build (non-distant), same float steps, through the reverse-patch snapshots (SrcSector / SrcBiome / SrcBiomeHeight).
            float span = (float)w * scale;
            var sec = SrcSector; var bio = SrcBiome;
            g.Corner[0] = sec(g.Ox, g.Oz).Biome;
            g.Corner[1] = sec(g.Ox + span, g.Oz).Biome;
            g.Corner[2] = sec(g.Ox, g.Oz + span).Biome;
            g.Corner[3] = sec(g.Ox + span, g.Oz + span).Biome;
            g.B1 = bio(g.Ox, g.Oz); g.B2 = bio(g.Ox + span, g.Oz); g.B3 = bio(g.Ox, g.Oz + span); g.B4 = bio(g.Ox + span, g.Oz + span);
            g.Same = g.B3 == g.B1 && g.B2 == g.B1 && g.B4 == g.B1;
            g.Edge = g.Corner[0] != g.Corner[1] || g.Corner[0] != g.Corner[2] || g.Corner[0] != g.Corner[3];
            return g;
        }

        // Vertex (i2 = column / x, i1 = row / z): HeightmapBuilder.Build's inner loop body for that one vertex.
        private static int Vertex(Grid g, int i2, int i1)
        {
            int w = s_w, num1 = w + 1;
            int idx = i1 * num1 + i2;
            if (!float.IsNaN(g.H[idx])) return idx;
            float scale = s_scale;
            var bh = SrcBiomeHeight;   // 1.2.459: vanilla path only (the FAT path fills every vertex in NewGrid)
            float wy = g.Oz + (float)i1 * scale;
            float t1 = DUtils.SmoothStep(0.0f, 1f, (float)i1 / (float)w);
            float wx = g.Ox + (float)i2 * scale;
            float t2 = DUtils.SmoothStep(0.0f, 1f, (float)i2 / (float)w);
            Color mask;
            float h;
            if (g.Same) h = bh(g.B1, wx, wy, out mask);
            else
            {
                float h1 = bh(g.B1, wx, wy, out Color c0);
                float h2 = bh(g.B2, wx, wy, out Color c1);
                float h3 = bh(g.B3, wx, wy, out Color c2);
                float h4 = bh(g.B4, wx, wy, out Color c3);
                h = DUtils.Lerp(DUtils.Lerp(h1, h2, t2), DUtils.Lerp(h3, h4, t2), t1);
                mask = Color.Lerp(Color.Lerp(c0, c1, t2), Color.Lerp(c2, c3, t2), t1);
            }
            // m_paintMask is a Texture2D(w+1, w+1), RGBA32: SetPixels stores 8 bits, GetPixel returns byte / 255 (UNCONFIRMED rounding).
            g.M[idx] = Mathf.Round(Mathf.Clamp01(mask.a) * 255f) / 255f;
            g.H[idx] = h;   // last: NaN marks "not computed" for both arrays
            return idx;
        }

        // Heightmap.UpdateCornerDepths: [0] = (0, w), [1] = (w, w), [2] = (w, 0), [3] = (0, 0); max(0, 30 - h).
        private static void EnsureOD(Grid g)
        {
            if (g.HaveOD) return;
            int w = s_w;
            g.OD[0] = Mathf.Max(0f, 30f - g.H[Vertex(g, 0, w)]);
            g.OD[1] = Mathf.Max(0f, 30f - g.H[Vertex(g, w, w)]);
            g.OD[2] = Mathf.Max(0f, 30f - g.H[Vertex(g, w, 0)]);
            g.OD[3] = Mathf.Max(0f, 30f - g.H[Vertex(g, 0, 0)]);
            g.HaveOD = true;
        }

        // ZoneSystem.GetZone, same math (no vanilla call on the worker).
        private static int ZoneCoord(float v) => Mathf.FloorToInt((float)(((double)v + 32.0) / 64.0));

        // 1.2.449: per-thread grid cache across PlaceZone calls (a cell build's neighbouring zones share their grids); dropped on a new world (Init)
        // and when it passes MaxCachedGrids. Grids are only ever touched by the thread that owns the cache.
        private const int MaxCachedGrids = 256;
        private static int s_world;
        [ThreadStatic] private static Dictionary<long, Grid> t_grids;
        [ThreadStatic] private static int t_world;

        private sealed class Ctx
        {
            public WorldGenerator Gen;
            public Dictionary<long, Grid> Grids;
            public readonly List<float> TempVeg = new List<float>();
            public readonly float[] Weights = new float[10];
            public Grid At(float x, float z)
            {
                int zx = ZoneCoord(x), zz = ZoneCoord(z);
                long k = Key(zx, zz);
                if (!Grids.TryGetValue(k, out var g))
                {
                    if (Grids.Count >= MaxCachedGrids) Grids.Clear();
                    Grids[k] = g = NewGrid(Gen, zx, zz);
                }
                return g;
            }
        }

        private static Ctx NewCtx(WorldGenerator gen)
        {
            int world = System.Threading.Volatile.Read(ref s_world);
            if (t_grids == null || t_world != world) { t_grids = new Dictionary<long, Grid>(); t_world = world; }
            return new Ctx { Gen = gen, Grids = t_grids };
        }

        // The collision mesh: vertex (c, r) at (Cx - w*s/2 + c*s, H, Cz - w*s/2 + r*s); per cell triangles (r,c)(r+1,c)(r,c+1) and
        // (r,c+1)(r+1,c)(r+1,c+1) (Heightmap.RebuildCollisionMesh). Returns the height; normal = the hit face's normal.
        private static float GroundOn(Grid g, float x, float z, out Vector3 normal)
        {
            int w = s_w; float s = s_scale; int n1 = w + 1;
            float lx = (float)((x - (g.Cx - w * s * 0.5f)) / s), lz = (float)((z - (g.Cz - w * s * 0.5f)) / s);
            int c = Mathf.Clamp(Mathf.FloorToInt(lx), 0, w - 1), r = Mathf.Clamp(Mathf.FloorToInt(lz), 0, w - 1);
            float u = Mathf.Clamp01(lx - c), v = Mathf.Clamp01(lz - r);
            float h00 = g.H[Vertex(g, c, r)], h10 = g.H[Vertex(g, c + 1, r)], h01 = g.H[Vertex(g, c, r + 1)], h11 = g.H[Vertex(g, c + 1, r + 1)];
            if (u + v <= 1f)
            {
                normal = new Vector3(-(h10 - h00), s, -(h01 - h00)).normalized;   // cross(v6 - v0, v4 - v0)
                return h00 + u * (h10 - h00) + v * (h01 - h00);
            }
            normal = new Vector3(h01 - h11, s, h10 - h11).normalized;            // cross(v6 - v4, v5 - v4)
            return h11 + (1f - u) * (h01 - h11) + (1f - v) * (h10 - h11);
        }

        // Heightmap.GetBiome (non-distant): one corner biome, or the corner weights (1.414 - d)^3.
        // 1.2.459: then FAT's Heightmap.GetBiome postfix (paint, then territories) on the FAT path.
        private static Heightmap.Biome BiomeOn(Ctx ctx, Grid g, Vector3 p)
        {
            var b = BiomeWeighted(ctx, g, p);
            var at = SrcBiomeAt;
            return at != null ? at(p.x, p.z, b) : b;
        }

        private static Heightmap.Biome BiomeWeighted(Ctx ctx, Grid g, Vector3 p)
        {
            if (g.Corner[0] == g.Corner[1] && g.Corner[0] == g.Corner[2] && g.Corner[0] == g.Corner[3]) return g.Corner[0];
            float num = (float)s_w * s_scale;
            float x = (float)((double)(p.x - g.Cx) / (double)num + 0.5);
            float y = (float)((double)(p.z - g.Cz) / (double)num + 0.5);
            var wts = ctx.Weights;
            for (int i = 1; i < wts.Length; ++i) wts[i] = 0f;
            wts[g.Corner[0].ToIndex()] += Dist(x, y, 0f, 0f);
            wts[g.Corner[1].ToIndex()] += Dist(x, y, 1f, 0f);
            wts[g.Corner[2].ToIndex()] += Dist(x, y, 0f, 1f);
            wts[g.Corner[3].ToIndex()] += Dist(x, y, 1f, 1f);
            int b = 0; float best = -99999f;
            for (int i = 1; i < wts.Length; ++i) if (wts[i] > best) { b = i; best = wts[i]; }
            return ((Heightmap.BiomeIndex)b).ToBiome();
        }

        private static float Dist(float x, float y, float rx, float ry)
        {
            float a = x - rx, b = y - ry;
            float d = 1.414f - Mathf.Sqrt((float)((double)a * (double)a + (double)b * (double)b));
            return d * d * d;
        }

        // Heightmap.GetVegetationMask: -0.5 m, WorldToVertexMask, GetPixel with Clamp wrap.
        private static float VegMaskOn(Grid g, Vector3 p)
        {
            float px = p.x - 0.5f, pz = p.z - 0.5f;
            int num = (s_w + 1) / 2;
            int x = Mathf.FloorToInt((float)((double)(px - g.Cx) / (double)s_scale + 0.5) + (float)num);
            int y = Mathf.FloorToInt((float)((double)(pz - g.Cz) / (double)s_scale + 0.5) + (float)num);
            x = Mathf.Clamp(x, 0, s_w); y = Mathf.Clamp(y, 0, s_w);
            return g.M[Vertex(g, x, y)];
        }

        // Heightmap.GetOceanDepth: WorldToVertex, then the corner lerp (unclamped, as vanilla).
        private static float OceanDepthOn(Grid g, Vector3 p)
        {
            int num = s_w / 2;
            int x = Mathf.FloorToInt((float)((double)(p.x - g.Cx) / (double)s_scale + 0.5)) + num;
            int y = Mathf.FloorToInt((float)((double)(p.z - g.Cz) / (double)s_scale + 0.5)) + num;
            float t1 = (float)x / (float)s_w, t2 = (float)y / (float)s_w;
            EnsureOD(g);
            return DUtils.Lerp(DUtils.Lerp(g.OD[3], g.OD[2], t1), DUtils.Lerp(g.OD[0], g.OD[1], t1), t2);
        }

        // ---------------------------------------------------------------- RNG (FarVegRng, or UnityEngine.Random on the main thread)
        private struct Rng
        {
            public bool Unity; public FarVegRng R;
            public int N;   // 1.2.454: words drawn since Init (trace)
            public void Init(int seed) { N = 0; if (Unity) UnityEngine.Random.InitState(seed); else R = FarVegRng.FromSeed(seed); }
            public float Value() { N++; return Unity ? UnityEngine.Random.value : R.value; }
            public float Range(float a, float b) { N++; return Unity ? UnityEngine.Random.Range(a, b) : R.Range(a, b); }
            public int Range(int a, int b) { if (a != b) N++; return Unity ? UnityEngine.Random.Range(a, b) : R.Range(a, b); }
            public Vector2 InsideUnitCircle() { N += 2; return Unity ? UnityEngine.Random.insideUnitCircle : R.insideUnitCircle; }
            public void Skip(int n)
            {
                if (n <= 0) return;
                N += n;
                if (!Unity) { for (int i = 0; i < n; i++) R.NextU32(); return; }
                FarVegRngSelfTest.ReadState(out int a, out int b, out int c, out int d);
                var r = FarVegRng.FromState(a, b, c, d);
                for (int i = 0; i < n; i++) r.NextU32();
                FarVegRngSelfTest.WriteState(r);
            }
        }

        // ---------------------------------------------------------------- the replay
        // Appends the zone's far-kind instances to outList. Any thread when WorkerOk; otherwise MAIN thread only.
        internal static void PlaceZone(Vector2s zone, List<FarVegInstance> outList) => PlaceZoneCore(zone, outList, AwakeMode, s_probe, null);

        // spans (diagnostics): per replayed entry, (entry index in Entries, first instance, end) in outList.
        private static void PlaceZoneCore(Vector2s zone, List<FarVegInstance> outList, int awakeMode, Dictionary<int, int> probe, List<(int Entry, int From, int To)> spans, Diag dg = null)
        {
            var gen = s_gen;
            var entries = FarVegKinds.Entries;
            if (gen == null || entries.Length == 0 || outList == null) return;
            var ctx = NewCtx(gen);
            var rng = new Rng { Unity = !WorkerOk };
            UnityEngine.Random.State saved = default;
            if (rng.Unity) saved = UnityEngine.Random.state;
            try
            {
                Vector3 zoneCenter = new Vector3(zone.x * 64f, 0f, zone.y * 64f);   // ZoneSystem.GetZonePos
                Grid own = ctx.At(zoneCenter.x, zoneCenter.z);   // corners only; heights are computed on demand
                s_clear.TryGetValue(Key(zone.x, zone.y), out Vector3 clear);   // z = radius; 0 = none
                var census = s_awakeSnap;
                bool emulate = dg != null ? dg.Emulate : EmulateBlocking;
                var zb = emulate ? new ZoneBlocks() : null;
                for (int ei = 0; ei < entries.Length; ei++)
                {
                    var e = entries[ei];
                    if (e.Kind < 0 && !emulate) continue;   // a blocker-only entry: replayed only to emulate IsBlocked
                    // hmap.HaveBiome(veg.m_biome): the zone's corner biomes.
                    if ((own.Corner[0] & e.Biome) == 0 && (own.Corner[1] & e.Biome) == 0 && (own.Corner[2] & e.Biome) == 0 && (own.Corner[3] & e.Biome) == 0 && (own.Painted & e.Biome) == 0) continue;   // 1.2.459: + FAT paint (HaveBiome postfix)
                    int from = outList.Count;
                    ReplayEntry(ctx, ref rng, e, zone, zoneCenter, clear, (dg != null && dg.WordsOverride >= 0 ? dg.WordsOverride + e.FixedWords : WordsFor(e, awakeMode, census, probe)), outList, zb, dg);
                    spans?.Add((ei, from, outList.Count));
                }
            }
            finally { if (rng.Unity) UnityEngine.Random.state = saved; }
        }

        private static void ReplayEntry(Ctx ctx, ref Rng rng, FarVegKinds.Entry e, Vector2s zoneID, Vector3 zoneCenterPos, Vector3 clear, int awakeWords, List<FarVegInstance> into, ZoneBlocks zb = null, Diag dg = null)
        {
            int entryStart = into.Count;
            rng.Init(s_seed + zoneID.x * 4271 + zoneID.y * 9187 + e.PrefabHash);
            int num2 = 1;
            if (e.Max < 1f) { if (rng.Value() > e.Max) return; }
            else num2 = rng.Range((int)e.Min, (int)e.Max + 1);
            float num3 = Mathf.Cos((float)Math.PI / 180f * e.MaxTilt);
            float num4 = Mathf.Cos((float)Math.PI / 180f * e.MinTilt);
            float num5 = 32f - e.GroupRadius;
            var tempVeg = ctx.TempVeg;
            tempVeg.Clear();
            int num6 = e.ForcePlacement ? num2 * 50 : num2;
            int num7 = 0;
            for (int index1 = 0; index1 < num6; ++index1)
            {
                if (dg != null) dg.AttemptN = rng.N;   // 1.2.454 trace: the absolute draw index at this attempt's start
                Vector3 vector3 = new Vector3(rng.Range(zoneCenterPos.x - num5, zoneCenterPos.x + num5), 0.0f, rng.Range(zoneCenterPos.z - num5, zoneCenterPos.z + num5));
                BiomeSector sector = SrcSector(vector3.x, vector3.z);   // 1.2.459: FAT path or vanilla snapshot (vanilla Vector3 overload = the same table read)
                if (!AltBiomeOk(sector, e)) { dg?.Note(e, index1, -1, vector3, "alt biome"); continue; }
                int num8 = rng.Range(e.GroupSizeMin, e.GroupSizeMax + 1);
                bool flag2 = false;
                for (int index2 = 0; index2 < num8; ++index2)
                {
                    Vector3 p = index2 == 0 ? vector3 : RandomPointInRadius(ref rng, vector3, e.GroupRadius);
                    float y = (float)rng.Range(0, 360);
                    float num9 = rng.Range(e.ScaleMin, e.ScaleMax);
                    float x = rng.Range(-e.RandTilt, e.RandTilt);
                    float z = rng.Range(-e.RandTilt, e.RandTilt);
                    // SKIPPED in production: veg.m_blockCheck && IsBlocked(p) (no colliders far out). Diagnostics on a loaded zone: dg.Blocked.
                    if (e.BlockCheck && dg?.Blocked != null) { string by = dg.Blocked(p, e, into, entryStart); if (by != null) { dg.Note(e, index1, index2, p, "BLOCKED by " + by); continue; } }
                    // 1.2.451 EMULATED IsBlocked (shippable): the vertical ray against the colliders of everything this replay placed earlier in the zone.
                    if (e.BlockCheck && zb != null) { string zby = zb.By(p.x, p.z); if (zby != null) { dg?.Note(e, index1, index2, p, "BLOCKED (emulated) by " + zby); continue; } }
                    Grid g = ctx.At(p.x, p.z);                              // GetGroundData: the ray hits the zone under p
                    p.y = GroundOn(g, p.x, p.z, out Vector3 normal1);
                    Heightmap.Biome biome = BiomeOn(ctx, g, p);
                    Heightmap.BiomeArea area = g.Edge ? Heightmap.BiomeArea.Edge : Heightmap.BiomeArea.Median;
                    if (dg != null && dg.TraceEntryIndex == e.Index && dg.Lines.Count < dg.TraceMax) dg.Vals = Values(ctx, rng, e, g, p, normal1, biome, area, num3, num4);   // 1.2.452 trace: every filter value (no RNG consumed)
                    if ((e.Biome & biome) == Heightmap.Biome.None || (e.BiomeArea & area) == 0) { dg?.Note(e, index1, index2, p, "biome/area"); continue; }
                    // SKIPPED: m_snapToStaticSolid (GetStaticSolidHeight needs colliders); ground height kept.
                    float num10 = p.y - 30f;
                    if (num10 < e.MinAltitude || num10 > e.MaxAltitude) { dg?.Note(e, index1, index2, p, "altitude"); continue; }
                    if (!e.MinVegetation.Equals(e.MaxVegetation))
                    {
                        float vm = VegMaskOn(g, p);
                        if (vm > e.MaxVegetation || vm < e.MinVegetation) { dg?.Note(e, index1, index2, p, "veg mask"); continue; }
                    }
                    if (!e.MinOceanDepth.Equals(e.MaxOceanDepth))
                    {
                        float od = OceanDepthOn(g, p);
                        if (od < e.MinOceanDepth || od > e.MaxOceanDepth) { dg?.Note(e, index1, index2, p, "ocean depth"); continue; }
                    }
                    if (normal1.y < num3 || normal1.y > num4) { dg?.Note(e, index1, index2, p, "tilt"); continue; }
                    if (e.TerrainDeltaRadius > 0f)
                    {
                        float delta = TerrainDelta(ctx, ref rng, p, e.TerrainDeltaRadius);
                        if (delta > e.MaxTerrainDelta || delta < e.MinTerrainDelta) { dg?.Note(e, index1, index2, p, "terrain delta"); continue; }
                    }
                    if (e.MinDistanceFromCenter > 0f || e.MaxDistanceFromCenter > 0f)
                    {
                        float num11 = Utils.LengthXZ(p);   // vanilla's own (pure math)
                        if (e.MinDistanceFromCenter > 0f && num11 < e.MinDistanceFromCenter || e.MaxDistanceFromCenter > 0f && num11 > e.MaxDistanceFromCenter) { dg?.Note(e, index1, index2, p, "distance"); continue; }
                    }
                    if (e.InForest)
                    {
                        float ff = SrcForest(p.x, p.z);   // 1.2.459: FAT path or vanilla snapshot
                        if (ff < e.ForestMin || ff > e.ForestMax) { dg?.Note(e, index1, index2, p, "forest"); continue; }
                    }
                    if (e.SurroundCheckVegetation)
                    {
                        float num12 = 0f;
                        for (int index3 = 0; index3 < e.SurroundCheckLayers; ++index3)
                        {
                            float num13 = (float)(index3 + 1) / (float)e.SurroundCheckLayers * e.SurroundCheckDistance;
                            for (int index4 = 0; index4 < 6; ++index4)
                            {
                                float f = (float)((double)index4 / 6.0 * 3.1415927410125732 * 2.0);
                                float vm = VegMaskOn(g, p + new Vector3(Mathf.Sin(f) * num13, 0f, Mathf.Cos(f) * num13));   // hmap1 = the hit zone's
                                float num14 = (float)((1.0 - (double)num13) / ((double)e.SurroundCheckDistance * 2.0));
                                num12 += vm * num14;
                            }
                        }
                        tempVeg.Add(num12);
                        if (tempVeg.Count >= 10)
                        {
                            float num15 = tempVeg.Max();
                            float num16 = tempVeg.Average();
                            float num17 = num16 + (num15 - num16) * e.SurroundBetterThanAverage;
                            if (num12 < num17) { dg?.Note(e, index1, index2, p, "surround < avg"); continue; }
                        }
                        else { dg?.Note(e, index1, index2, p, "surround < 10 samples"); continue; }
                    }
                    // InsideClearArea: the zone's location, a box of +-radius around its centre.
                    if (clear.z > 0f && p.x > clear.x - clear.z && p.x < clear.x + clear.z && p.z > clear.y - clear.z && p.z < clear.y + clear.z) { dg?.Note(e, index1, index2, p, "clear area (zone location)"); continue; }
                    if (dg?.Clear != null && InClear(dg.Clear, p)) { dg.Note(e, index1, index2, p, "clear area (LocationProxy)"); continue; }
                    if (e.SnapToWater) p.y = 30f;
                    p.y += e.GroundOffset;
                    float tiltX = x, tiltZ = z;
                    bool groundTilt = false; Quaternion q = Quaternion.identity;
                    if (e.ChanceToUseGroundTilt > 0f && rng.Value() <= e.ChanceToUseGroundTilt)
                    {
                        // Quaternion.LookRotation(Cross(normal, Euler(0, y, 0) * forward), normal): yaw y on the ground's tilt.
                        q = Quaternion.LookRotation(Vector3.Cross(normal1, Quaternion.Euler(0f, y, 0f) * Vector3.forward), normal1);
                        groundTilt = true;
                    }
                    // 1.2.451: the instance's colliders block later candidates (vanilla's rotation: ground tilt, else Euler(x, y, z); scale num9).
                    if (zb != null && e.Shapes.Length > 0) zb.Add(p, groundTilt ? q : Quaternion.Euler(x, y, z), num9, e);
                    if (groundTilt) { Vector3 eul = q.eulerAngles; y = eul.y; tiltX = eul.x; tiltZ = eul.z; }
                    if (e.Kind >= 0) into.Add(new FarVegInstance { Kind = e.Kind, Pos = p, Yaw = y, Scale = num9, TiltX = tiltX, TiltZ = tiltZ });   // blocker-only entries are not drawn
                    dg?.Note(e, index1, index2, p, "PLACED");
                    rng.Skip(awakeWords);   // the prefab's Awake draws inside the real Instantiate (both branches instantiate, ZoneSystem.cs:893 / :913)
                    flag2 = true;
                }
                if (flag2) ++num7;
                if (num7 >= num2) break;
            }
        }

        private static bool AltBiomeOk(BiomeSector sector, FarVegKinds.Entry e)
        {
            var alts = sector.AltBiomes;
            if (e.AltBiomeParent != null)
            {
                bool found = false;
                if (alts != null) for (int i = 0; i < alts.Count && !found; i++) found = alts[i].m_name == e.AltBiomeParent;
                if (!found) return false;
            }
            if (alts != null) for (int i = 0; i < alts.Count; i++) if (alts[i].m_blockVegetationNames.Contains(e.Name)) return false;
            return true;
        }

        // ZoneSystem.GetRandomPointInRadius, same draws (ZoneSystem.cs:739).
        private static Vector3 RandomPointInRadius(ref Rng rng, Vector3 center, float radius)
        {
            float f = (float)((double)rng.Value() * 3.1415927410125732 * 2.0);
            float num = rng.Range(0f, radius);
            return center + new Vector3(Mathf.Sin(f) * num, 0f, Mathf.Cos(f) * num);
        }

        // ZoneSystem.GetTerrainDelta: 10 x insideUnitCircle * radius, GetGroundHeight on the zone under each sample.
        private static float TerrainDelta(Ctx ctx, ref Rng rng, Vector3 center, float radius)
        {
            float hi = -999999f, lo = 999999f;
            for (int i = 0; i < 10; ++i)
            {
                Vector2 v = rng.InsideUnitCircle() * radius;
                float px = center.x + v.x, pz = center.z + v.y;
                float h = GroundOn(ctx.At(px, pz), px, pz, out _);
                if (h < lo) lo = h;
                if (h > hi) hi = h;
            }
            return hi - lo;
        }

        // ---------------------------------------------------------------- proof: replay vs the real trees of a loaded zone (main thread)
        private static readonly List<ZDO> s_zdos = new List<ZDO>();
        private static readonly List<FarVegInstance> s_matchList = new List<FarVegInstance>();
        private const float MatchRadius2 = 0.25f;   // 0.5 m XZ: a replayed tree stands exactly where vanilla put it, or the sequence diverged

        // real = the zone's ZDOs of far kinds; matched = real ones with a replayed instance of the same kind within 0.5 m (XZ);
        // extra = replayed instances with no real tree. Read it on untouched ground (felled / planted trees count against it).
        internal static void MatchZone(Vector2s zone, out int real, out int matched, out int extra)
        {
            real = 0; matched = 0; extra = 0;
            if (s_gen == null || ZDOMan.instance == null) return;
            ReadReal(zone);
            s_matchList.Clear();
            PlaceZoneCore(zone, s_matchList, AwakeMode, s_probe, null);
            Score(s_matchList, out real, out matched, out extra, null, null);
        }

        // The zone's real ZDOs of far kinds, kept in s_zdos.
        private static void ReadReal(Vector2s zone)
        {
            s_zdos.Clear();
            var all = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(0, 0), all);
            foreach (var zdo in all)
            {
                if (zdo == null || FarVegKinds.KindOfPrefabHash(zdo.GetPrefab()) < 0) continue;
                Vector3 zp = zdo.GetPosition();
                if (ZoneCoord(zp.x) != zone.x || ZoneCoord(zp.z) != zone.y) continue;
                s_zdos.Add(zdo);
            }
        }

        // usedBy[i] = the real ZDO index replay instance i matched (-1 none); nearestReal[i] = XZ distance to the nearest real tree of its kind.
        private static void Score(List<FarVegInstance> list, out int real, out int matched, out int extra, int[] usedBy, float[] nearestReal)
        {
            real = s_zdos.Count; matched = 0; extra = 0;
            var used = new bool[list.Count];
            for (int z = 0; z < s_zdos.Count; z++)
            {
                int kind = FarVegKinds.KindOfPrefabHash(s_zdos[z].GetPrefab());
                Vector3 zp = s_zdos[z].GetPosition();
                for (int i = 0; i < list.Count; i++)
                {
                    if (used[i] || list[i].Kind != kind) continue;
                    float dx = list[i].Pos.x - zp.x, dz = list[i].Pos.z - zp.z;
                    if (dx * dx + dz * dz <= MatchRadius2) { used[i] = true; if (usedBy != null) usedBy[i] = z; matched++; break; }
                }
            }
            for (int i = 0; i < used.Length; i++)
            {
                if (!used[i]) extra++;
                if (nearestReal == null) continue;
                float best = float.MaxValue;
                foreach (var zdo in s_zdos)
                {
                    if (FarVegKinds.KindOfPrefabHash(zdo.GetPrefab()) != list[i].Kind) continue;
                    Vector3 zp = zdo.GetPosition();
                    float dx = list[i].Pos.x - zp.x, dz = list[i].Pos.z - zp.z;
                    best = Mathf.Min(best, dx * dx + dz * dz);
                }
                nearestReal[i] = best == float.MaxValue ? -1f : Mathf.Sqrt(best);
            }
        }

        // 1.2.449 DIAGNOSTIC (main thread, Debug): which assumption reproduces the zone, and where each entry's sequence breaks.
        //  - one "mode N: matched m/real (extra e)" per Awake-word mode (0 dedicated, 1 LodFadeInOut, 2 probe);
        //  - for the best mode, per replayed entry: replayed n, matched m, the first instance that missed (#k), its nearest real tree of the same
        //    kind (m) and its height y; plus the real trees per kind nobody matched.
        // An entry whose first instances match and then stop = an RNG shift at that point (Awake words, or an accept / reject that differs).
        // An entry that never matches = its zone gate / seed / first test differs.
        internal static string MatchZoneDetail(Vector2s zone)
        {
            if (s_gen == null || ZDOMan.instance == null || FarVegKinds.Entries.Length == 0) return "not ready";
            ReadReal(zone);
            var probe = ProbeWords();
            var sb = new System.Text.StringBuilder();
            sb.Append($"zone ({zone.x},{zone.y}) real {s_zdos.Count}");
            int bestMode = 0, bestMatched = -1;
            for (int mode = 0; mode <= 2; mode++)
            {
                var l = new List<FarVegInstance>();
                PlaceZoneCore(zone, l, mode, probe, null);
                Score(l, out _, out int m, out int ex, null, null);
                sb.Append($"; mode {mode} ({AwakeModeName(mode)}): matched {m} extra {ex}");
                if (m > bestMatched) { bestMatched = m; bestMode = mode; }
            }
            var list = new List<FarVegInstance>();
            var spans = new List<(int Entry, int From, int To)>();
            PlaceZoneCore(zone, list, bestMode, probe, spans);
            var usedBy = new int[list.Count];
            for (int i = 0; i < usedBy.Length; i++) usedBy[i] = -1;
            var near = new float[list.Count];
            Score(list, out _, out _, out _, usedBy, near);
            var entries = FarVegKinds.Entries;
            sb.Append($" || best mode {bestMode}, per entry:");
            foreach (var s in spans)
            {
                if (s.To == s.From || entries[s.Entry].Kind < 0) continue;
                var e = entries[s.Entry];
                int m = 0, firstMiss = -1;
                for (int i = s.From; i < s.To; i++) { if (usedBy[i] >= 0) m++; else if (firstMiss < 0) firstMiss = i; }
                sb.Append($" [{FarVegKinds.Name(e.Kind)} #{e.Index} words L{e.LodFadeWords}/P{(probe.TryGetValue(e.PrefabHash, out int pw) ? pw : -2)}: {s.To - s.From} replayed, {m} matched");
                if (firstMiss >= 0) sb.Append($", first miss #{firstMiss - s.From} at ({list[firstMiss].Pos.x:0.0},{list[firstMiss].Pos.z:0.0}) y {list[firstMiss].Pos.y:0.00}, nearest real same kind {near[firstMiss]:0.00} m");
                sb.Append(']');
            }
            // real trees nobody matched, per kind
            var matchedReal = new HashSet<int>();
            foreach (int z in usedBy) if (z >= 0) matchedReal.Add(z);
            var missing = new Dictionary<int, int>();
            for (int z = 0; z < s_zdos.Count; z++)
                if (!matchedReal.Contains(z)) { int k = FarVegKinds.KindOfPrefabHash(s_zdos[z].GetPrefab()); missing[k] = (missing.TryGetValue(k, out int n) ? n : 0) + 1; }
            if (missing.Count > 0) sb.Append(" || real unmatched: " + string.Join(", ", missing.Select(kv => $"{FarVegKinds.Name(kv.Key)} {kv.Value}")));
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 1.2.450 TRACE (main thread, Debug, a LOADED zone)
        // Proves where the replay leaves vanilla's sequence. Variants on the same zone:
        //   A = as shipped (no IsBlocked, clear areas only from m_locationInstances = none on a client)
        //   B = A + the zone's location clear areas, from its LocationProxy ZDO (ZDOVars.s_location -> ZoneSystem.m_locations: m_clearArea,
        //       m_exteriorRadius; vanilla PlaceLocations ZoneSystem.cs:1457-1461)
        //   C = B + IsBlocked AS IT WAS DURING GENERATION, from the loaded zone's real colliders (vanilla IsBlocked ZoneSystem.cs:1929-1933: a ray
        //       from y 2000 down 10000 m on Default / static_solid / Default_small / piece, ZoneSystem.cs:152). A hit counts when it is a location
        //       piece / non-vegetation, vegetation of an EARLIER m_vegetation entry, or this entry's own instance the replay already placed
        //       (FAT FarReplay.IsBlockedAtGeneration). Later entries' objects did not stand yet and are ignored.
        // If C reproduces the zone, the production fix is IsBlocked + clear areas; the trace shows each candidate's decision.
        internal sealed class Diag
        {
            public List<Vector3> Clear;   // (x, z, radius)
            public bool Emulate;          // 1.2.451: emulated IsBlocked (the shippable path) for this run
            public int WordsOverride = -1;   // 1.2.454: k-sweep: extra words per placed instance (replaces WordsFor) when >= 0
            public int AttemptN;             // 1.2.454: draw index at the current attempt's start
            public string Vals;           // 1.2.452: the traced candidate's filter values, appended by Note
            public Func<Vector3, FarVegKinds.Entry, List<FarVegInstance>, int, string> Blocked;   // 1.2.452: what blocked, or null
            public int TraceEntryIndex = -1, TraceMax = 40;
            public readonly List<string> Lines = new List<string>();
            public Func<Vector3, string> RealAt;   // nearest real objects to a point (for PLACED / rejected lines)
            public void Note(FarVegKinds.Entry e, int a, int m, Vector3 p, string what)
            {
                if (e.Index != TraceEntryIndex || Lines.Count >= TraceMax) return;
                Lines.Add($"a{a}.m{m}@w{AttemptN} ({p.x:0.00},{p.z:0.00}) {what}{(Vals != null ? " [" + Vals + "]" : "")}{(RealAt != null ? " | real: " + RealAt(p) : "")}");
                Vals = null;
            }
        }

        // 1.2.452 trace: every PlaceVegetation filter's value at a candidate, with the entry's range and pass (+) / fail (-). The terrain delta
        // is computed on a COPY of the RNG (Rng is a struct), so the trace consumes no draws; on the main-thread UnityEngine.Random path it is skipped.
        private static string Values(Ctx ctx, Rng rng, FarVegKinds.Entry e, Grid g, Vector3 p, Vector3 normal, Heightmap.Biome biome, Heightmap.BiomeArea area, float num3, float num4)
        {
            string P(bool ok) => ok ? "+" : "-";
            var sb = new System.Text.StringBuilder();
            sb.Append($"biome {biome}{P((e.Biome & biome) != 0)} area {area}{P((e.BiomeArea & area) != 0)}");
            float alt = p.y - 30f;
            sb.Append($" alt {alt:0.00} [{e.MinAltitude:0.#}..{e.MaxAltitude:0.#}]{P(alt >= e.MinAltitude && alt <= e.MaxAltitude)}");
            if (!e.MinVegetation.Equals(e.MaxVegetation)) { float vm = VegMaskOn(g, p); sb.Append($" vm {vm:0.000} [{e.MinVegetation:0.##}..{e.MaxVegetation:0.##}]{P(vm >= e.MinVegetation && vm <= e.MaxVegetation)}"); }
            if (!e.MinOceanDepth.Equals(e.MaxOceanDepth)) { float od = OceanDepthOn(g, p); sb.Append($" od {od:0.00} [{e.MinOceanDepth:0.#}..{e.MaxOceanDepth:0.#}]{P(od >= e.MinOceanDepth && od <= e.MaxOceanDepth)}"); }
            sb.Append($" ny {normal.y:0.0000} [{num3:0.0000}..{num4:0.0000}]{P(normal.y >= num3 && normal.y <= num4)}");
            if (e.TerrainDeltaRadius > 0f)
            {
                if (rng.Unity) sb.Append(" delta (main-thread RNG: not traced)");
                else { var copy = rng; float d = TerrainDelta(ctx, ref copy, p, e.TerrainDeltaRadius); sb.Append($" delta {d:0.00} r{e.TerrainDeltaRadius:0.#} [{e.MinTerrainDelta:0.##}..{e.MaxTerrainDelta:0.##}]{P(d >= e.MinTerrainDelta && d <= e.MaxTerrainDelta)}"); }
            }
            if (e.MinDistanceFromCenter > 0f || e.MaxDistanceFromCenter > 0f) sb.Append($" dist {Utils.LengthXZ(p):0} [{e.MinDistanceFromCenter:0}..{e.MaxDistanceFromCenter:0}]");
            if (e.InForest) { float ff = SrcForest(p.x, p.z); sb.Append($" ff {ff:0.000} [{e.ForestMin:0.##}..{e.ForestMax:0.##}]{P(ff >= e.ForestMin && ff <= e.ForestMax)}"); }
            if (e.SurroundCheckVegetation) sb.Append(" surround-check");
            if (e.BlockCheck) sb.Append(" blockCheck");
            return sb.ToString();
        }

        private static bool InClear(List<Vector3> areas, Vector3 p)
        {
            foreach (var c in areas) if (p.x > c.x - c.z && p.x < c.x + c.z && p.z > c.y - c.z && p.z < c.y + c.z) return true;
            return false;
        }

        private static readonly RaycastHit[] s_hits = new RaycastHit[64];

        internal static string MatchZoneTrace(Vector2s zone, string prefabName, int maxLines = 40)
        {
            var zs = ZoneSystem.instance;
            if (s_gen == null || zs == null || ZDOMan.instance == null || FarVegKinds.Entries.Length == 0) return "not ready";
            ReadReal(zone);
            // every object in the zone and its neighbours (for "real:" and the clear areas)
            var near = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 1), near);
            // clear areas: LocationProxy ZDOs in this zone
            var clear = new List<Vector3>();
            int proxyHash = "LocationProxy".GetStableHashCode();
            var locNames = new List<string>();
            foreach (var zdo in near)
            {
                if (zdo == null || zdo.GetPrefab() != proxyHash) continue;
                Vector3 lp = zdo.GetPosition();
                if (ZoneCoord(lp.x) != zone.x || ZoneCoord(lp.z) != zone.y) continue;
                int lh = zdo.GetInt(ZDOVars.s_location);
                foreach (var loc in zs.m_locations)
                    if (loc != null && !string.IsNullOrEmpty(loc.m_prefabName) && loc.m_prefabName.GetStableHashCode() == lh)
                    {
                        locNames.Add($"{loc.m_prefabName} at ({lp.x:0.0},{lp.z:0.0}) clearArea {loc.m_clearArea} r {loc.m_exteriorRadius:0.#}");
                        if (loc.m_clearArea) clear.Add(new Vector3(lp.x, lp.z, loc.m_exteriorRadius));
                        break;
                    }
            }
            // first m_vegetation index of every enabled prefab (a vegetation object blocks only later entries)
            var first = new Dictionary<int, int>();
            for (int i = 0; i < zs.m_vegetation.Count; i++)
            {
                var v = zs.m_vegetation[i];
                if (v == null || v.m_prefab == null || !v.m_enable) continue;
                int h = v.m_prefab.name.GetStableHashCode();
                if (!first.ContainsKey(h)) first[h] = i;
            }
            int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");   // ZoneSystem.cs:152
            // 1.2.452: returns WHAT blocked (null = not blocked). A non-networked vegetation instance (no ZNetView, ZoneSystem.cs:913) is parented to
            // the zone root, so its name is the root's direct child, not transform.root (451 read the zone root there and over-blocked).
            string zoneRootName = zs.m_zonePrefab != null ? zs.m_zonePrefab.name : "_Zone";
            Func<Vector3, FarVegKinds.Entry, List<FarVegInstance>, int, string> blocked = (p, e, into, entryStart) =>
            {
                int n = Physics.RaycastNonAlloc(new Vector3(p.x, p.y + 2000f, p.z), Vector3.down, s_hits, 10000f, mask);
                for (int i = 0; i < n; i++)
                {
                    var col = s_hits[i].collider;
                    if (col == null) continue;
                    var nv = col.GetComponentInParent<ZNetView>();
                    var zdo = nv != null ? nv.GetZDO() : null;
                    Transform obj = col.transform;
                    while (obj.parent != null && !obj.parent.name.StartsWith(zoneRootName, StringComparison.Ordinal)) obj = obj.parent;
                    string name = obj.name; int cut = name.IndexOf("(Clone)", StringComparison.Ordinal); if (cut >= 0) name = name.Substring(0, cut); name = name.Trim();
                    int h = zdo != null ? zdo.GetPrefab() : name.GetStableHashCode();
                    string tag = (zdo != null ? PrefabName(h) : name + " (no ZDO)") + $" layer {LayerMask.LayerToName(col.gameObject.layer)}";
                    Vector3 hp = zdo != null ? zdo.GetPosition() : obj.position;   // 1.2.455: an object of a NEIGHBOUR zone did not exist yet when this zone generated (454: rock4_forest of (-102,-21) blocked a19)
                    if (ZoneCoord(hp.x) != zone.x || ZoneCoord(hp.z) != zone.y) continue;
                    if (!first.TryGetValue(h, out int f)) return "non-vegetation " + tag;   // a location piece / static: stood there
                    if (f < e.Index) return $"earlier entry #{f} {tag}";
                    if (h != e.PrefabHash) continue;                                          // a later entry's: not there yet
                    Vector3 zp = zdo != null ? zdo.GetPosition() : obj.position;
                    for (int t = entryStart; t < into.Count; t++)
                    {
                        float dx = into[t].Pos.x - zp.x, dz = into[t].Pos.z - zp.z;
                        if (dx * dx + dz * dz <= 0.01f) return "own earlier instance " + tag;
                    }
                }
                return null;
            };
            Func<Vector3, string> realAt = p =>
            {
                var best = near.Where(z => z != null).Select(z => (z, d: Mathf.Sqrt((z.GetPosition().x - p.x) * (z.GetPosition().x - p.x) + (z.GetPosition().z - p.z) * (z.GetPosition().z - p.z))))
                               .OrderBy(t => t.d).Take(2).ToList();
                return string.Join(", ", best.Select(t => $"{PrefabName(t.z.GetPrefab())} {t.d:0.00} m"));
            };
            var sb = new System.Text.StringBuilder();
            sb.Append($"zone ({zone.x},{zone.y}) real {s_zdos.Count}; locations: {(locNames.Count > 0 ? string.Join("; ", locNames) : "none")}");
            var variants = new (string Name, Diag D)[]
            {
                ("A as shipped", null),
                ("B + clear areas", new Diag { Clear = clear }),
                ("C + clear areas + IsBlocked(real colliders)", new Diag { Clear = clear, Blocked = blocked }),
                ("D + IsBlocked EMULATED (shippable, no clear areas)", new Diag { Emulate = true }),
                ("E = D + clear areas", new Diag { Emulate = true, Clear = clear }),
            };
            foreach (var v in variants)
            {
                var l = new List<FarVegInstance>();
                PlaceZoneCore(zone, l, AwakeMode, s_probe, null, v.D);
                Score(l, out _, out int m, out int ex, null, null);
                sb.Append($"; {v.Name}: matched {m}/{s_zdos.Count} extra {ex}");
            }
            // 1.2.454: k-sweep = extra words per PLACED instance (in place of the Awake words), under C (real colliders) and D (emulated blocking)
            sb.Append(" || k-sweep:");
            foreach (var lab in new[] { "C", "D" })
                for (int k = 0; k <= 3; k++)
                {
                    var d = lab == "C" ? new Diag { Clear = clear, Blocked = blocked, WordsOverride = k } : new Diag { Emulate = true, WordsOverride = k };
                    var l = new List<FarVegInstance>();
                    PlaceZoneCore(zone, l, AwakeMode, s_probe, null, d);
                    Score(l, out _, out int m, out _, null, null);
                    sb.Append($" {lab} k={k} {m}/{s_zdos.Count}");
                }
            // trace the named entry (its first enabled replayed entry) under A and C
            int traceIdx = -1;
            foreach (var e in FarVegKinds.Entries) if (FarVegKinds.Name(e.Kind) == prefabName) { traceIdx = e.Index; break; }
            if (traceIdx < 0) return sb.Append($" || no replayed entry for {prefabName}").ToString();
            foreach (var e in FarVegKinds.Entries)   // 1.2.452: every replayed entry of that prefab, with its rules
                if (FarVegKinds.Name(e.Kind) == prefabName)
                    sb.Append($" || RULES #{e.Index} \"{e.Name}\": count {e.Min:0.##}..{e.Max:0.##} force {e.ForcePlacement} group {e.GroupSizeMin}..{e.GroupSizeMax} r {e.GroupRadius:0.#} biome {e.Biome} area {e.BiomeArea} alt {e.MinAltitude:0.#}..{e.MaxAltitude:0.#} tilt {e.MinTilt:0.#}..{e.MaxTilt:0.#} delta r{e.TerrainDeltaRadius:0.#} {e.MinTerrainDelta:0.##}..{e.MaxTerrainDelta:0.##} forest {e.InForest} {e.ForestMin:0.##}..{e.ForestMax:0.##} veg {e.MinVegetation:0.##}..{e.MaxVegetation:0.##} ocean {e.MinOceanDepth:0.#}..{e.MaxOceanDepth:0.#} block {e.BlockCheck} groundTilt {e.ChanceToUseGroundTilt:0.##} surround {e.SurroundCheckVegetation} altParent {e.AltBiomeParent ?? "-"} words L{e.LodFadeWords}");
            foreach (var v in new[] { ("A", new Diag()), ("C", new Diag { Clear = clear, Blocked = blocked }), ("D", new Diag { Emulate = true }) })
            {
                v.Item2.TraceEntryIndex = traceIdx; v.Item2.TraceMax = maxLines; v.Item2.RealAt = realAt;
                PlaceZoneCore(zone, new List<FarVegInstance>(), AwakeMode, s_probe, null, v.Item2);
                sb.Append($" || TRACE {v.Item1} {prefabName} #{traceIdx}: ").Append(string.Join(" ; ", v.Item2.Lines));
            }
            return sb.ToString();
        }

        private static Dictionary<int, string> s_names;
        private static string PrefabName(int hash)
        {
            if (s_names == null && ZNetScene.instance != null)
            {
                s_names = new Dictionary<int, string>();
                foreach (var go in ZNetScene.instance.m_prefabs) if (go != null) s_names[go.name.GetStableHashCode()] = go.name;
            }
            return s_names != null && s_names.TryGetValue(hash, out var n) ? n : "#" + hash;
        }

        // ---------------------------------------------------------------- draw census (Awake words per prefab)
        // Measures how many UnityEngine.Random words each vegetation prefab's Instantiate (Awake) consumes inside the REAL PlaceVegetation: the
        // state before / after the outermost Object.Instantiate(Object, Vector3, Quaternion) made inside it, FarVegRng stepped until they match.
        // Patches only when CensusStart is called (host, debug); PlaceVegetation's IL is never changed (Expand World Data anchors on it): a
        // prefix / finalizer pair only counts "inside". Results feed the replay (s_awakeSnap) for the rest of the session.
        private const int MaxSteps = 256;
        private sealed class Census { public int Calls, Unresolved, MinDraws = int.MaxValue, MaxDraws; }
        private static readonly Dictionary<string, Census> s_census = new Dictionary<string, Census>();
        private static readonly Dictionary<int, int> s_awake = new Dictionary<int, int>();   // main thread
        private static volatile Dictionary<int, int> s_awakeSnap = new Dictionary<int, int>();   // worker reads; replaced whole
        private static bool s_censusPatched;
        private static int s_inVeg, s_instDepth;   // main thread only (PlaceVegetation runs there)
        private struct CensusState { public bool Measure; public int A, B, C, D; }

        internal static void CensusStart()
        {
            if (s_censusPatched) return;
            var inst = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new[] { typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion) });
            var place = AccessTools.Method(typeof(ZoneSystem), "PlaceVegetation");
            if (inst == null || place == null) { EasyBakeLog.Info($"[FarVeg] census stands down: Object.Instantiate {(inst != null)}, ZoneSystem.PlaceVegetation {(place != null)}."); s_censusPatched = true; return; }
            var h = new Harmony("com.Fire.FiresEasyBakeMeshes.FarVegCensus");
            h.Patch(place, prefix: new HarmonyMethod(typeof(FarVegReplay), nameof(PlacePrefix)), finalizer: new HarmonyMethod(typeof(FarVegReplay), nameof(PlaceFinalizer)));
            h.Patch(inst, prefix: new HarmonyMethod(typeof(FarVegReplay), nameof(InstPrefix)), postfix: new HarmonyMethod(typeof(FarVegReplay), nameof(InstPostfix)));
            s_censusPatched = true;
            EasyBakeLog.Info("[FarVeg] draw census on: Object.Instantiate measured while inside ZoneSystem.PlaceVegetation (its IL untouched); counts go to the replay.");
        }

        private static void PlacePrefix() => s_inVeg++;
        private static Exception PlaceFinalizer(Exception __exception) { if (s_inVeg > 0) s_inVeg--; s_instDepth = 0; return __exception; }

        private static void InstPrefix(UnityEngine.Object original, out CensusState __state)
        {
            __state = default;
            if (s_inVeg <= 0 || s_instDepth++ > 0 || !(original is GameObject)) return;
            __state.Measure = true;
            FarVegRngSelfTest.ReadState(out __state.A, out __state.B, out __state.C, out __state.D);
        }

        private static void InstPostfix(UnityEngine.Object original, CensusState __state)
        {
            if (s_inVeg > 0 && s_instDepth > 0) s_instDepth--;
            if (!__state.Measure) return;
            var prefab = original as GameObject;
            if (prefab == null) return;
            FarVegRngSelfTest.ReadState(out int a1, out int b1, out int c1, out int d1);
            int draws = 0;
            var r = FarVegRng.FromState(__state.A, __state.B, __state.C, __state.D);
            while (!r.SameState(a1, b1, c1, d1) && draws <= MaxSteps) { r.NextU32(); draws++; }
            if (!s_census.TryGetValue(prefab.name, out var c)) s_census[prefab.name] = c = new Census();
            c.Calls++;
            int hash = prefab.name.GetStableHashCode();
            int before = s_awake.TryGetValue(hash, out int known) ? known : int.MinValue;
            if (draws > MaxSteps) { c.Unresolved++; s_awake[hash] = -1; }
            else
            {
                c.MinDraws = Math.Min(c.MinDraws, draws); c.MaxDraws = Math.Max(c.MaxDraws, draws);
                if (before == int.MinValue) s_awake[hash] = draws;
                else if (before != draws) s_awake[hash] = -1;   // not constant: the replay can't consume a fixed count (counted as 0)
            }
            if (!s_awake.TryGetValue(hash, out int now) || now != before) s_awakeSnap = new Dictionary<int, int>(s_awake);
        }

        // For the 30 s Debug line: prefabs that draw (and the ones that don't vary), or "" when nothing was measured.
        internal static string CensusLine()
        {
            if (s_census.Count == 0) return "";
            int calls = s_census.Values.Sum(c => c.Calls);
            var rows = s_census.Where(kv => kv.Value.MaxDraws > 0 || kv.Value.Unresolved > 0).OrderByDescending(kv => kv.Value.Calls)
                .Select(kv => $"{kv.Key} x{kv.Value.Calls}: words {(kv.Value.MinDraws == int.MaxValue ? "-" : kv.Value.MinDraws.ToString())}..{kv.Value.MaxDraws}{(kv.Value.Unresolved > 0 ? $", {kv.Value.Unresolved} unresolved" : "")}").ToList();
            return $"{s_census.Count} prefab(s), {calls} instance(s) measured; " + (rows.Count > 0 ? "drawing: " + string.Join("; ", rows) : "none draws in Awake");
        }
    }
}
