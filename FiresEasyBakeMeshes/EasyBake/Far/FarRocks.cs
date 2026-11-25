using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using FiresEasyBakeMeshes.EasyBake.Indirect;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.340 ([visual], lead 11-12; Fire's hand test: "for our farlands type work only some pieces even show from far away.... most dont,
    /// most rocks etc also do not"; Fire: "its meant to be our own code ... but our version NEEDS TO ACTUALLY WORK"). FAR ROCKS, G3 of the
    /// far-drawing plan: the large static rocks and landmarks of zones that are NOT loaded, out to Reach, drawn as instances of the game's
    /// own meshes through EBM's indirect path (GPU culled, EBM's own instanced variants, no shadows), so a mountain side, a sea stack or a
    /// floating island's rock reads from kilometres away. Our own method, our own names:
    ///  - WHICH: a prefab qualifies by RULE, judged once per prefab hash the moment a ZDO of it is met: it is static (no Rigidbody, no
    ///    Character, no TreeBase / TreeLog, no Pickable, no MineRock / MineRock5, no Destructible, no WearNTear, no ZSyncTransform), has
    ///    static mesh renderers (the LODGroup's last level, else its own), every material has an EBM instanced variant (Custom/StaticRock,
    ///    Custom/Vegetation, Custom/Piece ...), and its longest side is at least "Min size (m)". Vanilla's own Distant prefabs are left to
    ///    vanilla. The game's vegetation table is the usual source of such prefabs, but the rule is applied to ANY prefab seen, so a rock
    ///    an admin placed by hand counts too. Nothing is listed by name; the census names what passed and what was refused and why.
    ///  - WHERE FROM: ZDOs, on any process that holds them (a host or server holds every zone: Fire hosts). A sliced walk over
    ///    ZDOMan.m_objectsBySector inside the Reach ring, at most "Main thread budget (ms)" a frame, cycle after cycle every
    ///    "Rescan seconds"; a zone's rocks are kept as (prefab, position, rotation, scale). A client of a dedicated server only holds the
    ///    zones near it, so there this layer shows what it has seen; the server feed for clients is the next step (same shape as
    ///    FarBuildingNet), stated in the ticket.
    ///  - DRAW: for every zone in reach that is NOT loaded, each rock part is one IndirectBatches instance (handle); the moment the zone
    ///    loads (checked every frame for the zones around the reference zone, once a second for the rest) its handles are removed and
    ///    vanilla's real rocks take over; when it unloads they come back. Instances are capped ("Max instances"); the farthest zones are
    ///    the ones left out, and the census says how many.
    ///  - COST: RAM per rock about 64 B (matrix) + 8 B per part (handle) on the CPU and EBM's per-instance slot on the GPU; one shared
    ///    mesh per kind (the game's own, already in memory). Main thread: the walk inside its budget; the draw is EBM's existing indirect
    ///    pass. All stated by the 30 s census.
    ///  OFF by default until the shore-pose gate (Fire's rule); stands down with one Info line when EBM's indirect path is off.
    /// </summary>
    internal static class FarRocks
    {
        internal const string Log = "[FarRocks]";
        private const string Section = "06 - Far Rocks";   // 1.2.444: was "Far rocks" (EbmConfigMove carries saved values)
        private const int SectorWidth = 512, SectorHalf = 256;   // ZDOMan's sector grid (ZoneSystem.cs: x = Sector % 512 - 256, y = Sector / 512 - 256)

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_reach, s_minSize, s_budgetMs, s_rescanSeconds;
        private static ConfigEntry<int> s_maxInstances;
        // 1.2.345 ([ghost], lead 11-12; Fire's run_7: far rocks on = +3-5 MILLION camera triangles a frame through the indirect path, -15 fps).
        // Two bands: inside "Mesh band (m)" a rock is the game's own mesh (indirect), under a hard triangle budget, nearest zones first;
        // beyond it, a rock is an impostor through EBM's impostor engine (one bake per rock kind, one quad per rock), the same engine the far
        // forest uses. The 30 s census names both bands' cost. OFF by default until its rig gate passes.
        private static ConfigEntry<float> s_meshBand;
        private static ConfigEntry<int> s_maxTris;
        private const float InteriorGap = 1000f;   // next EBM ([visual] 11-22): a part farther than this from its prefab's root is an interior (FiresMausoleum, and dungeon interiors generally, sit ~5000 m up), not drawn far
        private static float MeshBand => s_meshBand != null ? Mathf.Clamp(s_meshBand.Value, 0f, 5000f) : 600f;
        private static int MaxTris => s_maxTris != null ? Mathf.Clamp(s_maxTris.Value, 50000, 4000000) : 400000;
        private const string ImpOwner = "EBM-FarRocks";
        private static readonly HashSet<long> s_impCells = new HashSet<long>();
        private static int s_impZones, s_impInstances, s_meshTris, s_zonesOverTris;
        // 1.2.352 ([2ic], [lead]'s dock A/B 3:53-3:58 AM: far impostors on = 1% low 5.7 vs 22.8 fps, worst 175 vs 44 ms; Fire's run_16: 8 of 18 census
        // windows "upload 7-11 ms, ~19,400 instance(s)"): every rescan cycle re-sent EVERY impostor-band zone through SetCell, so the pool took a new
        // range, re-uploaded and dead-filled the old one for rocks that had not changed. A zone is now sent only when its instance list differs
        // from what the engine holds (content print), it is new to the band, or the engine's pool was reset since (ImpostorPool.Generation).
        private static readonly Dictionary<long, ulong> s_impPrint = new Dictionary<long, ulong>();
        private static int s_impPoolGen;   // = ImpostorPool.Generation at our last sync (both start at 0)
        private static int c_impSentNew, c_impSentChanged, c_impSentReset, c_impSkipped, c_impCleared, c_impInstancesSent;
        internal static bool Enabled => s_enabled != null && s_enabled.Value && FarRendering.On;   // 1.2.458: AND the Far Rendering master
        internal static float Reach => FarReach.For(OwnReach);   // 1.2.483: Far Zone Sync = the one shared reach (FarReach.Effective), else OwnReach
        internal static float OwnReach => Mathf.Clamp(FarReach.Or(s_reach != null ? s_reach.Value : 2500f), 300f, 5000f);   // 1.2.344: [Far] Far reach wins when set (1.2.483: the 1.2.482 reach, unchanged)
        private static float MinSize => s_minSize != null ? Mathf.Clamp(s_minSize.Value, 1f, 40f) : 6f;
        private static double BudgetMs => s_budgetMs != null ? Mathf.Clamp(s_budgetMs.Value, 0.1f, 4f) : 0.5;
        private static float RescanSeconds => s_rescanSeconds != null ? Mathf.Clamp(s_rescanSeconds.Value, 10f, 600f) : 60f;
        private static int MaxInstances => s_maxInstances != null ? Mathf.Clamp(s_maxInstances.Value, 1000, 400000) : 60000;

        // ── prefab verdicts (by hash, once) ──
        private sealed class Kind
        {
            public int Hash; public string Name; public bool Ok; public string Why;
            public Mesh[] Meshes; public int[] Submeshes; public Material[] Mats; public Matrix4x4[] Local; public int Layer; public float LongestSide;
            public int Instances;   // census: ZDOs seen this cycle
            public int Tris;        // 1.2.345: triangles of one copy (all parts)
            public int ImpKind = -2; // 1.2.345: impostor engine kind id; -2 not asked yet, -1 refused
        }
        private static readonly Dictionary<int, Kind> s_kinds = new Dictionary<int, Kind>();

        // ── one placed rock, by zone ──
        private struct Rock { public Kind Kind; public Vector3 Pos; public Quaternion Rot; public Vector3 Scale; }
        private sealed class ZoneRocks { public Vector2s Zone; public List<Rock> Rocks = new List<Rock>(); public List<int> Handles; public bool Drawn, BeyondCap, Impostor; public float DistSq; public float KeepFrom; }   // 1.2.484 KeepFrom: Far Zone Sync size thinning (0 = every rock)
        private static Dictionary<Vector2s, ZoneRocks> s_zones = new Dictionary<Vector2s, ZoneRocks>();      // the finished cycle (drawn from)
        private static Dictionary<Vector2s, ZoneRocks> s_building = new Dictionary<Vector2s, ZoneRocks>();   // the cycle being walked
        // 1.2.356 ([2ic], lead 11-13 profiler: FarRocks.Update 5.9 MB/min standing / 9.3 moving, 29.5k-54k allocations of ~200 B): every 60 s cycle
        // built a new dictionary, a new ZoneRocks + List<Rock> per zone and grew each list by doubling, so the whole rock set was reallocated per cycle.
        // The two dictionaries now swap, and a finished cycle's ZoneRocks return to this pool with their list capacity kept.
        private static readonly Stack<ZoneRocks> s_pool = new Stack<ZoneRocks>();
        private static readonly HashSet<long> s_keep = new HashSet<long>();
        private static readonly List<long> s_drop = new List<long>();
        private static readonly List<ImpostorInstance> s_arr = new List<ImpostorInstance>();
        private static ZoneRocks Rent(Vector2s zone)
        {
            if (s_pool.Count == 0) return new ZoneRocks { Zone = zone };
            var z = s_pool.Pop();
            z.Zone = zone; z.Rocks.Clear(); z.Handles?.Clear(); z.Drawn = false; z.BeyondCap = false; z.Impostor = false; z.DistSq = 0f; z.KeepFrom = 0f;
            return z;
        }
        // only for an object nobody else holds: its draw handles are gone (hidden) or were handed to the new cycle (Handles = null)
        private static void Return(ZoneRocks z) { if (z == null || z.Drawn) return; s_pool.Push(z); }
        private static readonly List<Vector2s> s_tmpZones = new List<Vector2s>();

        // ── walk state ──
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_bySector = SafeBySector();
        private static AccessTools.FieldRef<ZDOMan, List<ZDO>[]> SafeBySector()
        {
            try { return AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector"); } catch { return null; }
        }
        private static int s_sector, s_pos; private static bool s_walking; private static float s_cycleStart, s_nextCycle, s_lastCycleSec;
        private static Vector2s s_refZone; private static int s_reachZones; private static Vector3 s_refPos;
        private static readonly int s_scaleHash = "scale".GetStableHashCode(), s_scaleScalarHash = "scaleScalar".GetStableHashCode();   // ZDOVars.cs:257/259

        // ── census ──
        private static float s_nextCensus, s_nextSlowCheck; private static bool s_said, s_saidDown, s_wasActive;
        private static int c_sectorsWalked, c_zdosSeen, c_slices, c_cycles, c_added, c_removed, c_refusedCap; private static double c_walkMs, c_worstSlice, c_drawMs;
        private static int s_instancesDrawn, s_handles, s_zonesDrawn, s_zonesParkedLoaded, s_zonesBeyondCap;
        private static int s_zonesThinned, s_zonesOverCapSync;   // 1.2.484: Far Zone Sync, last cap pass: zones thinned by rock size / zones still over the cap at level 3 (drawn anyway)
        internal static int DrawnZones => s_zonesDrawn + s_impZones;   // 1.2.484: FarZoneSet census (mesh-band zones drawn + impostor-band zones sent)
        private static readonly Stopwatch s_watch = new Stopwatch();

        /// <summary>Called once by EBM (plugin Awake) with its own ConfigFile, after FarBuildings.Initialize.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_enabled != null) return;
            // 1.2.458 (Fire 11-18): [03 - Far Rendering] Far Rocks (was [06 - Far Rocks] Enabled; EbmConfigMove carries a saved value), under the master switch
            s_enabled = EbmConfigMove.Bind(config, FarRendering.Section, "Far Rocks", false /* OFF until the shore-pose gate (Fire 11-11 hard rule) */,
                "Draws the large static rocks and landmarks of zones that are not loaded out to Reach, as instances of the game's own meshes (EBM's indirect path, no shadows), and as impostors past the mesh band when Far Trees is on. Needs Far Rendering on and [40 - Shaders] IndirectRenderer.");
            s_reach = EbmConfigMove.Bind(config, Section, "Reach (m)", 2500f, new ConfigDescription("How far far rocks are drawn.", new AcceptableValueRange<float>(300f, 5000f)));
            s_minSize = EbmConfigMove.Bind(config, Section, "Min size (m)", 6f, new ConfigDescription("A static prefab counts as a far rock when its longest side is at least this many metres.", new AcceptableValueRange<float>(1f, 40f)));
            s_budgetMs = EbmConfigMove.Bind(config, Section, "Main thread budget (ms)", 0.5f, new ConfigDescription("Main-thread time a frame for walking the world's objects.", new AcceptableValueRange<float>(0.1f, 4f)));
            s_rescanSeconds = EbmConfigMove.Bind(config, Section, "Rescan seconds", 60f, new ConfigDescription("How often the world's objects are walked again for new or gone rocks.", new AcceptableValueRange<float>(10f, 600f)));
            s_meshBand = EbmConfigMove.Bind(config, Section, "Mesh band (m)", 600f, new ConfigDescription("1.2.345: inside this distance far rocks are the game's own meshes (under Max triangles); beyond it they are impostors (EBM's impostor engine, [Far Impostors]). 0 = impostors only.", new AcceptableValueRange<float>(0f, 5000f)));
            s_maxTris = EbmConfigMove.Bind(config, Section, "Max triangles", 400000, new ConfigDescription("1.2.345: hard cap on far rock mesh triangles drawn a frame (nearest zones first); the rest wait for the impostor band or are left out, and the census says how many.", new AcceptableValueRange<int>(50000, 4000000)));
            s_maxInstances = EbmConfigMove.Bind(config, Section, "Max instances", 60000, new ConfigDescription("Cap on far rock instances (about 64 bytes each on the CPU plus EBM's instance slot on the GPU). The farthest zones are left out first.", new AcceptableValueRange<int>(1000, 400000)));
            s_enabled.SettingChanged += (_, __) => { if (!Enabled) ReleaseAll("key turned off"); s_said = false; };
            FarRendering.Changed += () => { if (s_enabled.Value) { if (!Enabled) ReleaseAll("Far Rendering turned off"); s_said = false; } };   // 1.2.458: the same body on the master switch
            IndirectBatches.ActiveChanged += () => { if (!IndirectBatches.Active) ReleaseAll("indirect path off"); };
            EbmFarWorldEvents.WorldClosed += () => { ReleaseAll("world closed"); s_zones.Clear(); s_building.Clear(); s_kinds.Clear(); s_walking = false; s_nextCycle = 0f; };
            FarRunner.Register("FarRocks", Update, null);
        }

        // ================================================================== frame
        private static void Update()
        {
            if (!Enabled) return;
            if (!FarRunner.Client || ZDOMan.instance == null || ZNet.instance == null || ZoneSystem.instance == null) return;
            if (s_bySector == null) { SayDown("ZDOMan.m_objectsBySector not found: the world's objects cannot be walked"); return; }
            if (!IndirectBatches.Active) { SayDown("EBM's indirect path is off ([40 - Shaders] IndirectRenderer / IndirectCoverage): nothing to draw with"); return; }
            s_saidDown = false;
            if (!s_said) { s_said = true; EasyBakeLog.Info($"{Log} on: static prefabs with a longest side >= {MinSize:0} m, outside loaded zones, out to {Reach:0} m, drawn as EBM indirect instances (no shadows); objects walked {BudgetMs:0.##} ms a frame, again every {RescanSeconds:0} s; cap {MaxInstances} instances"); }
            float now = Time.unscaledTime;
            Vector3 refPos = ZNet.instance.GetReferencePosition();
            s_refZone = ZoneSystem.GetZone(refPos); s_refPos = refPos;
            s_reachZones = Mathf.CeilToInt(Reach / 64f) + 1;

            // 1. the walk (sliced)
            if (!s_walking && now >= s_nextCycle) BeginCycle(now);
            if (s_walking) Walk(refPos);

            // 2. hand-off: zones around the reference zone every frame (the ones that load / unload next), the rest once a second
            // 1.2.518 ([lead] 11-25, wtL1125 CLEAN row "EBM Far: FarRocks" 0.282 ms/frame with "5112 zone(s) with far rocks in reach"): the every-frame part walked
            // ALL known rock zones only to count the far ones. Now a fast frame looks up just the ring <= 4 zones (81 lookups) and keeps the drawn counts by their
            // changes; the once-a-second pass judges every zone and recounts from scratch, as before. Same zones shown / hidden on the same frames.
            s_watch.Restart();
            bool slow = now >= s_nextSlowCheck; if (slow) s_nextSlowCheck = now + 1f;
            if (slow)
            {
                s_zonesDrawn = s_zonesParkedLoaded = 0; s_instancesDrawn = 0; s_countsStale = false;
                foreach (var kv in s_zones) HandOff(kv.Value, refPos, true);
            }
            else
            {
                for (int ox = -NearRing; ox <= NearRing; ox++)
                    for (int oy = -NearRing; oy <= NearRing; oy++)
                        if (s_zones.TryGetValue(new Vector2s(s_refZone.x + ox, s_refZone.y + oy), out var z)) HandOff(z, refPos, false);
                if (s_countsStale) RecountDrawn();   // a walk cycle swapped the zone set / a release hid zones: counts only, nothing shown or hidden
            }
            c_drawMs += s_watch.Elapsed.TotalMilliseconds;
            Census(now);
        }

        private const int NearRing = 4;   // the zones judged every frame (the ones that load / unload next)
        private static bool s_countsStale;   // set where Drawn / the zone set changes outside HandOff (the walk's swap, ReleaseAll)
        private static void RecountDrawn()
        {
            s_zonesDrawn = 0; s_instancesDrawn = 0; s_countsStale = false;
            foreach (var kv in s_zones) if (kv.Value.Drawn) { s_zonesDrawn++; s_instancesDrawn += kv.Value.Rocks.Count; }
        }
        // one zone's hand-off: show it when it should draw, hide it when it should not. recount = the once-a-second pass (counts rebuilt from zero);
        // otherwise the counts follow this zone's change only (the zones not judged this frame did not change).
        private static void HandOff(ZoneRocks z, Vector3 refPos, bool recount)
        {
            int ring = Math.Max(Math.Abs(z.Zone.x - s_refZone.x), Math.Abs(z.Zone.y - s_refZone.y));
            bool loaded = ZoneSystem.instance.IsZoneLoaded(z.Zone) || ring <= 1;   // ring 0-1: vanilla's own near objects are there or coming
            float dx = (z.Zone.x * 64f + 32f) - refPos.x, dz = (z.Zone.y * 64f + 32f) - refPos.z; z.DistSq = dx * dx + dz * dz;
            bool want = !loaded && z.DistSq <= Reach * Reach && !z.BeyondCap && !z.Impostor   // 1.2.345: impostor-band zones are the engine's
                && FarGroundCover.Covered(z.Zone);   // 1.2.416 [Far] FarNeedsGround: never over ground that is not drawn yet (FarGroundCover)
            bool was = z.Drawn;
            if (want && !z.Drawn) Show(z);
            else if (!want && z.Drawn) Hide(z);
            if (recount) { if (z.Drawn) { s_zonesDrawn++; s_instancesDrawn += z.Rocks.Count; } else if (loaded) s_zonesParkedLoaded++; return; }
            if (z.Drawn == was) return;
            int sign = z.Drawn ? 1 : -1;
            s_zonesDrawn += sign; s_instancesDrawn += sign * z.Rocks.Count;
        }

        // ================================================================== the walk
        private static void BeginCycle(float now)
        {
            foreach (var left in s_building.Values) Return(left);   // 1.2.356: a cycle that stopped mid-walk (never drawn from)
            s_building.Clear();
            foreach (var k in s_kinds.Values) k.Instances = 0;
            s_sector = 0; s_pos = 0; s_walking = true; s_cycleStart = now;
        }

        private static void Walk(Vector3 refPos)
        {
            var lists = s_bySector(ZDOMan.instance);
            if (lists == null) { s_walking = false; return; }
            s_watch.Restart();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            int total = lists.Length, n = 0;
            while (s_sector < total)
            {
                int sx = s_sector % SectorWidth - SectorHalf, sy = s_sector / SectorWidth - SectorHalf;
                // skip whole sectors outside the reach ring (a square around the reference zone)
                if (Math.Abs(sx - s_refZone.x) > s_reachZones || Math.Abs(sy - s_refZone.y) > s_reachZones) { s_sector++; s_pos = 0; continue; }
                var list = lists[s_sector];
                if (list != null)
                {
                    while (s_pos < list.Count)
                    {
                        ZDO zdo = list[s_pos++]; n++; c_zdosSeen++;
                        if (zdo != null && zdo.IsValid()) Consider(zdo, new Vector2s(sx, sy));
                        if ((n & 63) == 0 && s_watch.ElapsedTicks >= budget) goto paused;
                    }
                }
                s_sector++; s_pos = 0; c_sectorsWalked++;
                if ((n & 63) == 0 && s_watch.ElapsedTicks >= budget) goto paused;
            }
            // cycle done: swap, re-plan handles for zones whose rocks changed
            FinishCycle();
        paused:
            double ms = s_watch.Elapsed.TotalMilliseconds; c_slices++; c_walkMs += ms; if (ms > c_worstSlice) c_worstSlice = ms;
        }

        private static void Consider(ZDO zdo, Vector2s sector)
        {
            int hash = zdo.GetPrefab();
            if (!s_kinds.TryGetValue(hash, out Kind kind)) { kind = Judge(hash); s_kinds[hash] = kind; }
            if (!kind.Ok) return;
            kind.Instances++;
            if (!s_building.TryGetValue(sector, out ZoneRocks z)) { z = Rent(sector); s_building[sector] = z; }   // 1.2.356: pooled
            Vector3 scale = zdo.GetVec3(s_scaleHash, Vector3.one);
            float sc = zdo.GetFloat(s_scaleScalarHash, 1f); if (sc != 1f) scale *= sc;
            z.Rocks.Add(new Rock { Kind = kind, Pos = zdo.GetPosition(), Rot = zdo.GetRotation(), Scale = scale });
        }

        private static void FinishCycle()
        {
            s_walking = false;
            s_lastCycleSec = Time.unscaledTime - s_cycleStart; c_cycles++;
            s_nextCycle = Time.unscaledTime + RescanSeconds;
            // cap: nearest zones first
            s_tmpZones.Clear(); s_tmpZones.AddRange(s_building.Keys);
            s_tmpZones.Sort((a, b) => RingOf(a).CompareTo(RingOf(b)));
            int budgetLeft = MaxInstances; s_zonesBeyondCap = 0; s_zonesThinned = 0; s_zonesOverCapSync = 0;   // 1.2.484: + the Sync counters
            // 1.2.345: bands. Mesh band = nearest zones within MeshBand, until Max triangles; impostor band = the rest within Reach (when the
            // impostor engine is live), else beyond the triangle cap = left out and counted.
            int trisLeft = MaxTris; s_meshTris = 0; s_zonesOverTris = 0;
            bool impLive = ImpostorEngine.Enabled && FarRunner.Client;
            float band2 = MeshBand * MeshBand;
            foreach (var zk in s_tmpZones)
            {
                var z = s_building[zk];
                z.KeepFrom = 0f;   // 1.2.484 ([2ic]): every cap pass decides the thinning fresh (Same() still skips a redraw when nothing changed)
                float dx = (z.Zone.x * 64f + 32f) - s_refPos.x, dz = (z.Zone.y * 64f + 32f) - s_refPos.z; float d2 = dx * dx + dz * dz;
                int tris = 0; foreach (var r in z.Rocks) tris += r.Kind.Tris;
                if (d2 <= band2 && tris <= trisLeft && budgetLeft >= z.Rocks.Count) { trisLeft -= tris; s_meshTris += tris; budgetLeft -= z.Rocks.Count; continue; }
                if (impLive) { z.Impostor = true; continue; }
                // 1.2.484 (design 3.3, Far Zone Sync): no impostor engine to hand the zone to: thin the zone by rock size instead of dropping it whole
                // (levels 1-3: keep rocks with longest side >= Min size x 2 / 4 / 8); at level 3 the zone still draws its largest rocks, over the cap if
                // need be (counted, never dropped).
                if (FarZoneSet.On)
                {
                    // [2ic] MUST 2: the scanned list is never edited; z.KeepFrom tells Show which rocks to draw (and Same compares it)
                    int lvl = 0, kept = z.Rocks.Count;
                    while (lvl < 3 && (tris > trisLeft || budgetLeft < kept))
                    {
                        lvl++; float keepFrom = MinSize * (1 << lvl);
                        tris = 0; kept = 0; foreach (var r in z.Rocks) if (r.Kind.LongestSide >= keepFrom) { tris += r.Kind.Tris; kept++; }
                    }
                    z.KeepFrom = lvl > 0 ? MinSize * (1 << lvl) : 0f;
                    if (tris > trisLeft || budgetLeft < kept) s_zonesOverCapSync++;   // drawn anyway; IndirectBatches refuses past its own capacity (c_refusedCap), MaxInstances only budgets
                    s_zonesThinned += lvl > 0 ? 1 : 0;
                    trisLeft -= Math.Min(trisLeft, tris); s_meshTris += tris; budgetLeft -= Math.Min(budgetLeft, kept);
                    continue;
                }
                z.BeyondCap = true; s_zonesBeyondCap++; if (d2 <= band2) s_zonesOverTris++;
            }
            // keep a drawn zone's handles when its rock list is unchanged; otherwise hide the old one, the frame step shows the new
            foreach (var kv in s_zones)
            {
                if (s_building.TryGetValue(kv.Key, out ZoneRocks nz) && Same(kv.Value, nz))
                {
                    // the new object takes the old one's draw handles; 1.2.356: the old one takes the new one's spare (empty or null) list, so no list is dropped
                    var spare = nz.Handles; nz.Handles = kv.Value.Handles; nz.Drawn = kv.Value.Drawn; kv.Value.Handles = spare; kv.Value.Drawn = false;
                }
                else if (kv.Value.Drawn) Hide(kv.Value);
            }
            // 1.2.356: the old cycle's objects go back to the pool and its dictionary becomes the next cycle's (no new dictionary, lists keep capacity)
            var old = s_zones;
            foreach (var z in old.Values) Return(z);
            old.Clear();
            s_zones = s_building; s_building = old;
            s_countsStale = true;   // 1.2.518: the frame step recounts the drawn zones (it no longer walks every zone each frame)
            SyncImpostors();
        }

        // 1.2.345: impostor-band zones go to EBM's impostor engine as one cell per zone (key = zone); the engine bakes each kind once, culls on
        // the GPU and hands a zone back to the game's real rocks when it loads (ImpostorHandOff), so no per-frame work here.
        private static void SyncImpostors()
        {
            var keep = s_keep; keep.Clear(); s_impZones = 0; s_impInstances = 0;   // 1.2.356: reused, not new per cycle
            var arr = s_arr;
            // 1.2.352: the engine dropped every cell (turned off / stood down / world unload): nothing it held is there any more, so all go again
            bool poolReset = s_impPoolGen != ImpostorPool.Generation;
            if (poolReset) { s_impPrint.Clear(); s_impCells.Clear(); s_impPoolGen = ImpostorPool.Generation; }
            foreach (var z in s_zones.Values)
            {
                if (!z.Impostor) continue;
                arr.Clear();
                foreach (var r in z.Rocks)
                {
                    int kind = ImpKindOf(r.Kind); if (kind < 0) continue;
                    Vector3 e = r.Rot.eulerAngles;
                    float s = Mathf.Max(r.Scale.x, Mathf.Max(r.Scale.y, r.Scale.z));
                    arr.Add(new ImpostorInstance(r.Pos, s, e.y * Mathf.Deg2Rad, kind, Mathf.DeltaAngle(0f, e.x) * Mathf.Deg2Rad, Mathf.DeltaAngle(0f, e.z) * Mathf.Deg2Rad));
                }
                long key = ((long)z.Zone.x << 32) | (uint)z.Zone.y;
                keep.Add(key);
                if (arr.Count == 0) { if (s_impCells.Remove(key)) { ImpostorEngine.ClearCell(ImpOwner, key); c_impCleared++; } s_impPrint.Remove(key); continue; }
                s_impZones++; s_impInstances += arr.Count;
                ulong print = PrintOf(arr);
                bool held = s_impCells.Contains(key);
                if (held && s_impPrint.TryGetValue(key, out ulong old) && old == print) { c_impSkipped++; continue; }   // 1.2.352: same rocks, same kinds: the engine keeps its range
                if (!held) { if (poolReset) c_impSentReset++; else c_impSentNew++; } else c_impSentChanged++;
                var b = new Bounds(new Vector3(z.Zone.x * 64f, 0f, z.Zone.y * 64f), new Vector3(64f, 2000f, 64f));
                ImpostorEngine.SetCell(ImpOwner, key, b, arr.ToArray());
                s_impCells.Add(key); s_impPrint[key] = print; c_impInstancesSent += arr.Count;
            }
            var drop = s_drop; drop.Clear(); foreach (var key in s_impCells) if (!keep.Contains(key)) drop.Add(key);
            foreach (var key in drop) { s_impCells.Remove(key); s_impPrint.Remove(key); ImpostorEngine.ClearCell(ImpOwner, key); c_impCleared++; }
        }

        // 1.2.352: FNV-1a over every field the engine stores (position, scale, yaw, kind, tilts), in list order; equal print = the cell's GPU range is
        // already exactly this data. Bit patterns are hashed, so -0 vs 0 or a changed float always differs (a false "changed" only costs one resend).
        private static ulong PrintOf(List<ImpostorInstance> arr)
        {
            ulong h = 14695981039346656037UL;
            h = Mix(h, arr.Count);
            for (int i = 0; i < arr.Count; i++)
            {
                var a = arr[i];
                h = MixF(h, a.Position.x); h = MixF(h, a.Position.y); h = MixF(h, a.Position.z); h = MixF(h, a.Scale); h = MixF(h, a.Yaw);
                h = Mix(h, a.Kind); h = MixF(h, a.TiltX); h = MixF(h, a.TiltZ);
            }
            return h;
        }
        private static ulong Mix(ulong h, int v) => (h ^ (uint)v) * 1099511628211UL;
        private static ulong MixF(ulong h, float f) => Mix(h, new FloatBits { F = f }.I);   // no allocation (BitConverter.GetBytes would allocate per float)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }

        private static int ImpKindOf(Kind k)
        {
            if (k.ImpKind != -2) return k.ImpKind;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(k.Hash) : null;
            k.ImpKind = prefab != null ? ImpostorEngine.RegisterKind(ImpOwner, prefab, KindOptions.Default) : -1;
            return k.ImpKind;
        }

        private static int RingOf(Vector2s z) => Math.Max(Math.Abs(z.x - s_refZone.x), Math.Abs(z.y - s_refZone.y));

        private static bool Same(ZoneRocks a, ZoneRocks b)
        {
            if (a.Rocks.Count != b.Rocks.Count || a.KeepFrom != b.KeepFrom) return false;   // 1.2.484: + the thinning level
            for (int i = 0; i < a.Rocks.Count; i++) { var x = a.Rocks[i]; var y = b.Rocks[i]; if (x.Kind != y.Kind || x.Pos != y.Pos || x.Rot != y.Rot || x.Scale != y.Scale) return false; }
            return true;
        }

        // ================================================================== prefab rule
        private static Kind Judge(int hash)
        {
            var k = new Kind { Hash = hash };
            try
            {
                var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
                if (prefab == null) { k.Why = "no prefab on this client"; return k; }
                k.Name = prefab.name;
                var nv = prefab.GetComponent<ZNetView>();
                if (nv != null && nv.m_distant) { k.Why = "vanilla draws it far itself (Distant)"; return k; }
                if (prefab.GetComponent<Rigidbody>() != null || prefab.GetComponent<ZSyncTransform>() != null) { k.Why = "moves"; return k; }
                if (prefab.GetComponent<Character>() != null) { k.Why = "a creature"; return k; }
                if (prefab.GetComponent<TreeBase>() != null || prefab.GetComponent<TreeLog>() != null) { k.Why = "a tree"; return k; }
                if (prefab.GetComponent<Pickable>() != null) { k.Why = "pickable"; return k; }
                // 1.2.344 ([ghost], Fire run_6 :21860 "kinds 2 qualify / 653 refused"): most vanilla rocks (rock1_mountain, rock4_coast, rock3_*)
                // are MineRock5 / MineRock / Destructible, and this rule refused them all, so only a swamp log and a portal drew far. A far
                // copy only stands where the zone is NOT loaded; the game's real (minable) rock takes over the moment it loads, so a mined
                // rock shows whole only from beyond the loaded area. Static minable rocks now qualify; the size and shader rules still apply.
                if (prefab.GetComponent<WearNTear>() != null || prefab.GetComponent<Piece>() != null) { k.Why = "a building piece (far buildings draw it)"; return k; }
                // renderers: the LODGroup's last level, else the prefab's own static mesh renderers
                var renderers = new List<MeshRenderer>();
                var lg = prefab.GetComponentInChildren<LODGroup>();
                if (lg != null) { var lods = lg.GetLODs(); if (lods.Length > 0) foreach (var r in lods[lods.Length - 1].renderers) if (r is MeshRenderer mr) renderers.Add(mr); }
                if (renderers.Count == 0) renderers.AddRange(prefab.GetComponentsInChildren<MeshRenderer>(true));
                var meshes = new List<Mesh>(); var subs = new List<int>(); var mats = new List<Material>(); var locals = new List<Matrix4x4>();
                Bounds? all = null; Transform root = prefab.transform;
                int skippedFar = 0;   // next EBM ([visual] 11-22): parts far from the root (a location's interior), left out
                foreach (var mr in renderers)
                {
                    var mf = mr.GetComponent<MeshFilter>(); var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var sm = mr.sharedMaterials; if (sm == null) continue;
                    Matrix4x4 local = root.worldToLocalMatrix * mr.transform.localToWorldMatrix;
                    var b = mesh.bounds; Vector3 c = local.MultiplyPoint3x4(b.center); Vector3 e = local.MultiplyVector(b.extents); e = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
                    // next EBM ([visual], [lead] 11-22; Fire's log :21041 "'FiresMausoleumPortal' qualifies: longest side 5004.0 m, 10 part(s)"): a location prefab
                    // can carry its interior in the same prefab, built far above the entrance (FiresMausoleum MausoleumDungeonSpec.cs:30 InteriorYOffset = 5000f).
                    // That part is not part of the far view, and it stretched the kind's bounds to 5 km. A part whose centre is more than InteriorGap from
                    // the root is left out; the size rule and the bounds use only the parts kept.
                    if (c.magnitude > InteriorGap) { skippedFar++; continue; }
                    var wb = new Bounds(c, e * 2f); if (all == null) all = wb; else { var t = all.Value; t.Encapsulate(wb); all = t; }
                    for (int s = 0; s < mesh.subMeshCount && s < sm.Length; s++)
                    {
                        if (sm[s] == null) continue;
                        if (!IndirectBatches.Accepts(sm[s])) { k.Why = $"no EBM instanced variant for '{sm[s].shader?.name}'"; return k; }
                        meshes.Add(mesh); subs.Add(s); mats.Add(sm[s]); locals.Add(local);
                    }
                }
                if (meshes.Count == 0) { k.Why = skippedFar > 0 ? $"no static mesh near its root ({skippedFar} part(s) over {InteriorGap:0} m away: an interior)" : "no static mesh"; return k; }
                Vector3 size = all.Value.size; k.LongestSide = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * Mathf.Max(root.lossyScale.x, Mathf.Max(root.lossyScale.y, root.lossyScale.z));
                if (k.LongestSide < MinSize) { k.Why = $"too small ({k.LongestSide:0.0} m)"; return k; }
                int tris = 0; for (int q = 0; q < meshes.Count; q++) tris += (int)(meshes[q].GetIndexCount(subs[q]) / 3);
                k.Tris = tris;
                k.Meshes = meshes.ToArray(); k.Submeshes = subs.ToArray(); k.Mats = mats.ToArray(); k.Local = locals.ToArray(); k.Layer = prefab.layer; k.Ok = true;
                EasyBakeLog.Debug($"{Log} '{k.Name}' qualifies: longest side {k.LongestSide:0.0} m, {k.Meshes.Length} part(s)"
                    + (skippedFar > 0 ? $"; {skippedFar} part(s) over {InteriorGap:0} m from the root left out (an interior)" : ""));
            }
            catch (Exception e) { k.Why = "judge failed: " + e.Message; }
            return k;
        }

        // ================================================================== show / hide
        private static void Show(ZoneRocks z)
        {
            if (z.Handles == null) z.Handles = new List<int>(z.Rocks.Count * 2);
            z.Handles.Clear();
            var lod = new IndirectLodRange { StartHeight = float.MaxValue, EndHeight = 0f, Last = true };
            foreach (var r in z.Rocks)
            {
                if (r.Kind.LongestSide < z.KeepFrom) continue;   // 1.2.484: Far Zone Sync size thinning (KeepFrom 0 = every rock, as before)
                Matrix4x4 m = Matrix4x4.TRS(r.Pos, r.Rot, r.Scale);
                for (int i = 0; i < r.Kind.Meshes.Length; i++)
                {
                    Matrix4x4 w = m * r.Kind.Local[i];
                    int h = IndirectBatches.Add(r.Kind.Meshes[i], r.Kind.Submeshes[i], r.Kind.Mats[i], in w, ShadowCastingMode.Off, r.Kind.Layer, in lod);
                    if (h >= 0) { z.Handles.Add(h); c_added++; } else c_refusedCap++;
                }
            }
            z.Drawn = true; s_handles += z.Handles.Count;
        }

        private static void Hide(ZoneRocks z)
        {
            if (z.Handles != null) { foreach (var h in z.Handles) { IndirectBatches.Remove(h); c_removed++; } s_handles -= z.Handles.Count; z.Handles.Clear(); }
            z.Drawn = false;
        }

        private static void ReleaseAll(string why)
        {
            int n = 0;
            foreach (var z in s_zones.Values) if (z.Drawn) { Hide(z); n++; }
            s_handles = 0;
            s_countsStale = true;   // 1.2.518
            if (s_impCells.Count > 0) { ImpostorEngine.ClearOwner(ImpOwner); s_impCells.Clear(); }   // 1.2.345
            s_impPrint.Clear();   // 1.2.352: nothing is held by the engine any more
            if (n > 0) EasyBakeLog.Info($"{Log} {n} zone(s) of far rocks released ({why})");
        }

        private static void SayDown(string why) { if (!s_saidDown) { s_saidDown = true; EasyBakeLog.Info($"{Log} on but idle: {why}"); } }

        // ================================================================== census (30 s, Debug)
        private static void Census(float now)
        {
            if (now < s_nextCensus) return;
            if (s_nextCensus > 0f && EasyBakeLog.DebugOn())
            {
                int kindsOk = 0, kindsNo = 0, rocksKnown = 0; var top = new List<Kind>();
                var why = new Dictionary<string, int>();   // 1.2.344: refusals by reason, so a run names what stops a rock
                foreach (var k in s_kinds.Values)
                {
                    if (k.Ok) { kindsOk++; top.Add(k); continue; }
                    kindsNo++;
                    string w = k.Why ?? "?"; if (w.StartsWith("too small", StringComparison.Ordinal)) w = "too small"; else if (w.StartsWith("no EBM instanced variant", StringComparison.Ordinal)) w = "no EBM instanced shader";
                    why[w] = why.TryGetValue(w, out int c) ? c + 1 : 1;
                }
                foreach (var z in s_zones.Values) rocksKnown += z.Rocks.Count;
                top.Sort((a, b) => b.Instances.CompareTo(a.Instances));
                var sb = new StringBuilder();
                for (int i = 0; i < top.Count && i < 8; i++) { if (i > 0) sb.Append(", "); sb.Append(top[i].Name).Append(' ').Append(top[i].Instances).Append(" (").Append(top[i].LongestSide.ToString("0")).Append(" m)"); }
                long ram = rocksKnown * 64L + s_handles * 8L;
                // 1.2.352: what went to the impostor engine this window and why; "skipped" = unchanged zones the engine already holds (no upload, no dead-fill)
                int sent = c_impSentNew + c_impSentChanged + c_impSentReset;
                EasyBakeLog.Debug($"{Log} impostor cells last 30 s: sent {sent} zone(s) / {c_impInstancesSent} instance(s) (new to the band {c_impSentNew}, rocks changed {c_impSentChanged}, engine reset {c_impSentReset}), skipped {c_impSkipped} unchanged, cleared {c_impCleared}; held now {s_impCells.Count} zone(s)");
                EasyBakeLog.Debug($"{Log} last 30 s: {s_zones.Count} zone(s) with far rocks in reach ({rocksKnown} rock(s) known), drawn {s_zonesDrawn} zone(s) / {s_instancesDrawn} instance(s) / {s_handles} handle(s), parked (zone loaded) {s_zonesParkedLoaded}, beyond the {MaxInstances} cap {s_zonesBeyondCap} zone(s); mesh band {MeshBand:0} m: {s_meshTris:N0} tri(s) of {MaxTris:N0} ({s_zonesOverTris} zone(s) over the triangle cap{(FarZoneSet.On ? $"; Far Zone Sync: {s_zonesThinned} zone(s) thinned by rock size, {s_zonesOverCapSync} still over the cap (drawn, never dropped)" : "")}); impostor band: {s_impZones} zone(s) / {s_impInstances} rock(s) in EBM's impostor engine{(ImpostorEngine.Enabled ? "" : " (off: [03 - Far Rendering] Far Rendering or Far Trees is off)")}; kinds {kindsOk} qualify / {kindsNo} refused ({RefusalText(why)}); top: {(sb.Length > 0 ? sb.ToString() : "none")}; walk: {c_cycles} cycle(s) (last {s_lastCycleSec:0.0} s{(s_walking ? ", one running" : "")}), {c_sectorsWalked} sector(s) / {c_zdosSeen} object(s) in {c_slices} slice(s), {c_walkMs:0.0} ms (worst slice {c_worstSlice:0.00}); hand-off {c_added} added / {c_removed} removed, {c_refusedCap} refused by the indirect path; frame step {c_drawMs:0.0} ms; RAM ~{ram / 1024} KB CPU + EBM's GPU slots");
            }
            s_nextCensus = now + 30f; c_sectorsWalked = c_zdosSeen = c_slices = c_cycles = c_added = c_removed = c_refusedCap = 0; c_walkMs = c_worstSlice = c_drawMs = 0;
            c_impSentNew = c_impSentChanged = c_impSentReset = c_impSkipped = c_impCleared = c_impInstancesSent = 0;   // 1.2.352
        }

        private static string RefusalText(Dictionary<string, int> why)
        {
            if (why.Count == 0) return "none";
            var list = new List<KeyValuePair<string, int>>(why); list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < list.Count && i < 6; i++) { if (i > 0) sb.Append(", "); sb.Append(list[i].Key).Append(' ').Append(list[i].Value); }
            return sb.ToString();
        }
        internal static string Status() => !Enabled ? "far rocks off" : $"far rocks: {s_zones.Count} zone(s), {s_instancesDrawn} instance(s) drawn, {s_handles} handle(s), {s_kinds.Count} kind(s) judged";
    }
}
