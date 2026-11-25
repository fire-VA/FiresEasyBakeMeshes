using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Turns zone digests into drawn chunks. A target is one 64 m zone (nearer than "Cell merge from") or one 512 m cell (farther).
    /// Only zones that are NOT loaded are drawn. When a drawn zone loads, its far mesh is held until every far piece there has its
    /// real instance (or 10 s), then dropped: walking in leaves no gap and nothing inside the loaded area is ever touched.
    /// Chunks are plain hidden GameObjects with the pieces' own shared materials, so Unity culls them and the game's light and fog
    /// apply; shadows off (all of this is past the shadow distance). Main-thread work is time-sliced to the budget.
    /// </summary>
    internal static partial class FarBuildingDraw   // partial: E17 / E18 in FarBuildingDrawHandoff.cs ([worldgen] 11-09)
    {
        private const float HoldSeconds = 10f;
        private const int CellZones = 8; // 8 x 64 m = 512 m

        private sealed class Target
        {
            public long Key; public bool IsCell; public Vector3 Origin; public float Dist;
            public bool Coarse;   // 1.2.353: a cell of the coarse tier ([Far buildings] Coarse tier): coarser grid, small pieces left out
            public List<Vector2s> Zones = new List<Vector2s>();
            public bool Dirty = true, Drawn, Evicted; public int Generation; public bool JobOut;
            public int PrepZone, PrepPiece; public FarJob Prep; public Dictionary<long, FarJob.Bucket> PrepBuckets; public Dictionary<Material, int> PrepMats;
            public bool WaitingSource; public float HoldUntil = -1f; public bool ShownKnown, Shown;   // 1.2.249: the chunks' state, so an unchanged target touches no GameObject
            public List<GameObject> Chunks = new List<GameObject>(); public int Verts; public long Bytes;
            public FarIndexRanges.Table Runs;   // [Optimize] FarIndexRanges: the drawn chunks' per-piece index ranges (null = not tracked)
            public bool Retained; public float RetainedAt;
            public string CacheKey; public int CacheState; public List<int> PrepRefs;   // 1.2.425 FarMergedCache: key last asked, 0 ask / 1 reading / 2 miss (build + save); the job's material refs
            public bool Edge;   // 1.2.424
            public float SnowNow, SnowSent = -1f;
            public float CacheAskedAt;
            public int SplitBuilt = -1, PrepSplit = -1;
            public float Rank; public int Est, EstAt = -1;   // 1.2.432: cap order (lower first) and the cached vertex estimate   // 1.2.429: pieces in the built / in-prep split copy of a loaded zone (-1 = a full copy)   // 1.2.428: when its merged-cache read went out (a read older than 10 s is asked again)   // 1.2.428: live far snow (eased) and the value last set on its chunks (-1 = new chunks, set at the next pass): a LOADED zone on the edge of the loaded area, built ahead and hidden so it is ready the moment it unloads
            public float AdmittedAt;   // 1.2.355: when Plan first admitted this target (the moving backlog line's oldest-unbuilt age)
            public float EvictedAt;   // [Optimize] FarCapHysteresis: when the vertex cap last evicted it (quiet time before it may return)   // [Optimize] FarKeepHandedOff: handed off, hidden, kept for a quick re-show (FarBuildingDrawHandoff.cs)
        }

        private static readonly Dictionary<long, Target> s_targets = new Dictionary<long, Target>();

        // 1.2.344 ([ghost], lead 11-12; Fire's run_6 logged scans and zones but never how much was DRAWN): one 30 s Debug line from
        // FarBuildings' frame step: draw targets, drawn ones, zones and vertices they cover, nearest / farthest drawn distance.
        private static float s_nextDrawnCensus;
        private static int s_capBuilt, s_capEst;   // 1.2.346: last cap pass: built targets over the cap / unbuilt targets held back by estimate
        private static int s_coarseSmallOut;   // 1.2.353: pieces left out of coarse cells for size, this 30 s window
        internal static int ColdTargets { get; private set; }   // 1.2.346: admitted far targets not built yet (set by Plan, every 2 s)
        internal static void DrawnCensus()
        {
            float now = Time.unscaledTime;
            if (now < s_nextDrawnCensus) return;
            s_nextDrawnCensus = now + 30f;
            if (!EasyBakeLog.DebugOn()) return;
            // 1.2.345 ([visual]/[lead], run_7: "the rest handed to loaded zones" hid that the vertex cap was full): targets left out by the cap
            // (Evicted, nearest first in Plan) are counted with the nearest of them, and the farthest target the cap kept
            int targets = 0, drawn = 0, zones = 0, chunks = 0, shown = 0, capped = 0, waiting = 0, wSrc = 0, wJob = 0, wPrep = 0; long verts = 0; float near = float.MaxValue, far = 0f, capNear = float.MaxValue, kept = 0f;
            // 1.2.353: per tier (0 zone, 1 cell, 2 coarse cell): kept (drawn), their vertices, evicted by the cap, nearest evicted
            var tKept = new int[3]; var tEvicted = new int[3]; var tVerts = new long[3]; var tNear = new[] { float.MaxValue, float.MaxValue, float.MaxValue }; var tFar = new float[3];
            foreach (var t in s_targets.Values)
            {
                targets++;
                int tier = t.Coarse ? 2 : t.IsCell ? 1 : 0;
                if (t.Evicted) { tEvicted[tier]++; if (t.Dist < tNear[tier]) tNear[tier] = t.Dist; }
                else if (t.Drawn) { tKept[tier]++; tVerts[tier] += t.Verts; if (t.Dist > tFar[tier]) tFar[tier] = t.Dist; }
                if (t.Evicted) { capped++; if (t.Dist < capNear) capNear = t.Dist; continue; }
                if (t.Dist > kept) kept = t.Dist;
                if (!t.Drawn) { waiting++; if (t.WaitingSource) wSrc++; else if (t.JobOut) wJob++; else if (s_prepQueue.Contains(t) || t.Prep != null) wPrep++; continue; }
                drawn++; zones += t.Zones.Count; chunks += t.Chunks.Count; verts += t.Verts;
                if (t.ShownKnown && t.Shown) shown++;
                if (t.Dist < near) near = t.Dist; if (t.Dist > far) far = t.Dist;
            }
            EasyBakeLog.Debug($"{FarBuildings.Log} DRAWN, now: {drawn} of {targets} target(s) drawn ({shown} shown, {drawn - shown} handed to loaded zones), {waiting} not built yet ({wSrc} waiting on a source, {wPrep} in the prep queue, {wJob} job(s) out), " +
                              $"evicted by the vertex cap {capped} (built {s_capBuilt}, est. {s_capEst}){(capped > 0 ? $" (nearest evicted {capNear:0} m)" : "")} (farthest kept {kept:0} m, cap {FarBuildings.MaxVertices:N0}); " +
                              $"{zones} zone(s), {chunks} chunk(s), {verts:N0} vertices; " +
                              (drawn > 0 ? $"nearest {near:0} m, farthest {far:0} m" : "none drawn") + $"; reach {FarBuildings.Reach:0} m");
            // 1.2.353: the same targets by tier, so a cap change or the coarse tier shows where whole builds are kept or dropped
            string Tier(int i, string name) => $"{name} kept {tKept[i]} ({tVerts[i]:N0} verts{(tKept[i] > 0 ? $", farthest {tFar[i]:0} m" : "")}) / evicted {tEvicted[i]}{(tEvicted[i] > 0 ? $" (nearest {tNear[i]:0} m)" : "")}";
            EasyBakeLog.Debug($"{FarBuildings.Log} TIERS, now: {Tier(0, "zones")}; {Tier(1, "cells")}; {Tier(2, "coarse")}; coarse tier {(FarBuildings.CoarseOn ? $"on from {FarBuildings.CoarseFrom:0} m ({FarBuildings.CoarseCluster:0.#} m grid, {s_coarseSmallOut} small piece(s) under {FarBuildings.CoarseSmall:0.#} m left out in the last 30 s)" : "off")}; cap {FarBuildings.MaxVertices:N0}");
            s_coarseSmallOut = 0;
            WhereLine();
        }

        // 1.2.345 ([lead] 11-12, Fire at a Meadows hill with 293 targets "drawn" to 2.3 km: "that changed nothing"): the 10 biggest drawn targets
        // from the player: bearing (0 = north / +z, clockwise), distance, vertices, inside the main camera's frustum now, and whether Unity
        // actually rendered any of its chunks last frame (Renderer.isVisible: any camera, so a shadow or mirror pass counts too; the frustum
        // column says whether the MAIN view holds it). Also the main camera's far clip and the fog, the two things that can hide a drawn chunk.
        private static readonly List<Target> s_where = new List<Target>();
        private static void WhereLine()
        {
            var cam = Camera.main; var p = Player.m_localPlayer;
            if (cam == null || p == null) return;
            Vector3 me = p.transform.position;
            s_where.Clear();
            foreach (var t in s_targets.Values) if (t.Drawn && !t.Evicted) s_where.Add(t);
            if (s_where.Count == 0) return;
            s_where.Sort((a, b) => b.Verts.CompareTo(a.Verts));
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            var sb = new System.Text.StringBuilder();
            int rendered = 0, inView = 0;
            foreach (var t in s_where)
            {
                bool anyVis = false, anyIn = false;
                foreach (var go in t.Chunks)
                {
                    if (go == null) continue;
                    var r = go.GetComponent<Renderer>(); if (r == null) continue;
                    if (r.isVisible) anyVis = true;
                    if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) anyIn = true;
                }
                if (anyVis) rendered++; if (anyIn) inView++;
            }
            for (int i = 0; i < s_where.Count && i < 10; i++)
            {
                var t = s_where[i];
                Vector3 c = t.Origin; float dx = c.x - me.x, dz = c.z - me.z;
                float bearing = (Mathf.Atan2(dx, dz) * Mathf.Rad2Deg + 360f) % 360f, dist = Mathf.Sqrt(dx * dx + dz * dz);
                bool vis = false, inn = false; float y = c.y;
                foreach (var go in t.Chunks)
                {
                    if (go == null) continue; var r = go.GetComponent<Renderer>(); if (r == null) continue;
                    if (r.isVisible) vis = true; if (GeometryUtility.TestPlanesAABB(planes, r.bounds)) inn = true; y = r.bounds.center.y;
                }
                sb.Append(i == 0 ? "" : "; ").Append($"#{i + 1} bearing {bearing:0}°, {dist:0} m, y {y:0}, {t.Verts:N0} verts, in view {(inn ? "yes" : "no")}, rendered {(vis ? "yes" : "no")}");
            }
            EasyBakeLog.Debug($"{FarBuildings.Log} WHERE from you ({me.x:0},{me.z:0}), facing {(cam.transform.eulerAngles.y + 360f) % 360f:0}°: of {s_where.Count} drawn target(s) {inView} in the main view, {rendered} rendered by some camera last frame; " +
                              $"main camera far clip {cam.farClipPlane:0} m; fog {(RenderSettings.fog ? $"on ({RenderSettings.fogMode}, density {RenderSettings.fogDensity:0.#####}, ~{(RenderSettings.fogDensity > 0f ? 2.3f / RenderSettings.fogDensity : 0f):0} m to 90% fog for Exp)" : "off")}; biggest: {sb}");
        }
        private static readonly List<Target> s_prepQueue = new List<Target>();
        private static GameObject s_root;
        private static Vector3 s_planCenter = new Vector3(float.MaxValue, 0, 0);
        private static float s_planAt = -999f;
        private static bool s_planDirty = true;
        private static int s_snowRevision = int.MinValue;
        private static int s_snowChunks;   // 1.2.259: far chunks built with snow, for SnowFeed's 30 s line
        internal static int TakeSnowChunks() { int n = s_snowChunks; s_snowChunks = 0; return n; }

        // ── 1.2.262 far snow caps (Fire 11-10 2:45 AM: "dont show far snow caps if its not winter or not mountain/deep north") ──────────────
        // A copy's healthy-cap parts (FarPart.IsCap) are merged iff its ZDO build-up is over vanilla's 0.25 (FarPiece.Cap > 0, set by a scan;
        // unknown = 0) AND (the copy stands in Mountain / DeepNorth, OR the registered far snow source says snow is live there: FarSnow.Active
        // and its cover at the position > 0). Nothing registered = the biome alone. A stored value anywhere else (a zone nobody visited since
        // winter) shows no cap. Returns the cap level (1-4, the chunk's _SnowLevel in quarters) or 0. Chunks rebuild on a cap-only scan change
        // (FarBuildingDigest.ApplyScan), a far snow revision change and SnowFeed.Refresh, so caps follow seasons and the clear command.
        internal static readonly int SnowLevelId = Shader.PropertyToID("_SnowLevel");   // vanilla WearNTear.s_snowLevel
        private static int s_capsIn, s_capsOut, s_capsNoSnowMod;
        internal static string TakeCapCounts() { string s = $"{s_capsIn} cap part(s) included, {s_capsOut} left out ({s_capsNoSnowMod} copies outside the Deep North with saved snow and no snow mod loaded)"; s_capsIn = s_capsOut = s_capsNoSnowMod = 0; return s; }
        // 1.2.266 [Optimize] SnowNeedsSnowMod (Fire 11-10 2:15 PM: "it should not be drawn if Vedr is not installed"): vanilla writes a piece's saved
        // snow (ZDO "snow") ONLY in WearNTear's Deep North branch, so saved snow anywhere else came from a snow mod in an earlier session. With no snow
        // mod loaded now (no far snow and no piece snow source registered), a far cap shows only in the Deep North; with one loaded, the 2:45 AM rule.
        // 1.2.266 ([fgn], FF 0.1.100's same gate): snow mods that save piece snow but never register with EBM count as loaded too, so near (FF) and far
        // (EBM) agree. A presence test of BepInEx's loaded plugin list only, read once: nothing of those mods is called or relied on.
        private static bool? s_snowModInstalled;
        private static bool SnowModInstalled
        {
            get
            {
                if (s_snowModInstalled == null)
                {
                    var p = BepInEx.Bootstrap.Chainloader.PluginInfos;
                    s_snowModInstalled = p != null && (p.ContainsKey("Norger.Vedr") || p.ContainsKey("shudnal.Seasons") || p.ContainsKey("RustyMods.Seasonality"));
                }
                return s_snowModInstalled.Value;
            }
        }

        // 1.2.267 (lead 11-10, [ghost]'s note 1): the biome of a piece decided exactly as vanilla's WearNTear sees it (m_heightmap.GetBiome, WearNTear.cs:807),
        // so a cap vanilla legitimately snowed at a Deep North border never drops at range. A zone heightmap reads WorldGenerator.GetBiomeSector at its four
        // corners (zone centre -/+ 32 m, HeightmapBuilder.cs:190-200); all four equal = that biome; else each corner adds (1.414 - dist)^3 for the point
        // normalised to 0..1 across the zone (Heightmap.Distance :475, GetBiome :493-523) and the heaviest biome wins. A distant-LOD heightmap uses the exact
        // point (WorldGenerator.GetBiome), but WearNTear only snows on a loaded zone's heightmap, so the corner rule is the one that matters.
        // 1.2.268 ([ghost] review): exact to vanilla on near ties too: Distance in double then cast (Heightmap.cs:477-481), weights by BiomeIndex,
        // and the first maximum in BiomeIndex order (strict >, j from 1), as Heightmap.GetBiome picks.
        private static float[] s_biomeIndexWeight;
        private static float VanillaDistance(float x, float y, float rx, float ry)
        {
            float num = (float)((double)x - (double)rx);
            float num2 = (float)((double)y - (double)ry);
            float num3 = Mathf.Sqrt((float)((double)num * (double)num + (double)num2 * (double)num2));
            float num4 = (float)(1.4140000343322754 - (double)num3);
            return (float)((double)num4 * (double)num4 * (double)num4);
        }
        internal static Heightmap.Biome VanillaPieceBiome(WorldGenerator wg, Vector3 pos)
        {
            Vector2s zone = ZoneSystem.GetZone(pos);
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float x0 = c.x - ZoneSystem.c_ZoneSizeHalf, z0 = c.z - ZoneSystem.c_ZoneSizeHalf, s = ZoneSystem.c_ZoneSize;
            Heightmap.Biome b0 = wg.GetBiomeSector(x0, z0).Biome, b1 = wg.GetBiomeSector(x0 + s, z0).Biome, b2 = wg.GetBiomeSector(x0, z0 + s).Biome, b3 = wg.GetBiomeSector(x0 + s, z0 + s).Biome;
            if (b0 == b1 && b0 == b2 && b0 == b3) return b0;
            float nx = (pos.x - c.x) / s + 0.5f, ny = (pos.z - c.z) / s + 0.5f;
            if (s_biomeIndexWeight == null) s_biomeIndexWeight = new float[System.Math.Max(10, System.Enum.GetValues(typeof(Heightmap.BiomeIndex)).Length)];   // vanilla: new float[10] (Heightmap.cs:66)
            var w = s_biomeIndexWeight;
            for (int i = 1; i < w.Length; i++) w[i] = 0f;
            w[b0.ToIndex()] += VanillaDistance(nx, ny, 0f, 0f);
            w[b1.ToIndex()] += VanillaDistance(nx, ny, 1f, 0f);
            w[b2.ToIndex()] += VanillaDistance(nx, ny, 0f, 1f);
            w[b3.ToIndex()] += VanillaDistance(nx, ny, 1f, 1f);
            int best = 0; float bestW = -99999f;
            for (int j = 1; j < w.Length; j++) if (w[j] > bestW) { best = j; bestW = w[j]; }
            return ((Heightmap.BiomeIndex)best).ToBiome();
        }

        private static byte CapShown(FarPiece piece)
        {
            if (piece.Cap == 0) return 0;
            var wg = WorldGenerator.instance;
            bool snowMod = FarSnow.Bound || SnowFeed.AnySource || SnowModInstalled;
            bool strict = !snowMod && (FiresEasyBakeMeshesPlugin.SnowNeedsSnowMod == null || FiresEasyBakeMeshesPlugin.SnowNeedsSnowMod.Value);
            if (wg != null)
            {
                var biome = VanillaPieceBiome(wg, piece.Pos);   // 1.2.267: vanilla WearNTear's own biome for this spot
                if (biome == Heightmap.Biome.DeepNorth) return piece.Cap;
                if (biome == Heightmap.Biome.Mountain && !strict) return piece.Cap;
            }
            if (strict) { s_capsNoSnowMod++; return 0; }
            if (FarSnow.Active && FarSnow.CoverAt(piece.Pos) > 0f) return piece.Cap;
            return 0;
        }
        /// <summary>1.2.259 (SnowFeed.Refresh): every far target re-preps, so its chunks take the snow level read now.</summary>
        internal static void MarkSnowDirty() { foreach (var t in s_targets.Values) t.Dirty = true; }
        private static float s_snowCheckAt;
        private static int s_layer = -2;

        // EBM port (replaces Core's Services DistantRenderer.TryGetDistance, which EBM may not read): prefab name -> the
        // distance out to which the VA distant method already shows real objects. Same semantics as Core's table (exact prefab
        // name, ordinal). Empty by default = nothing skipped. Main thread only.
        internal static readonly Dictionary<string, float> VaDistantReach = new Dictionary<string, float>(StringComparer.Ordinal);

        private static bool TryGetVaDistance(string prefabName, out float maxDistanceMeters)
        {
            if (string.IsNullOrEmpty(prefabName)) { maxDistanceMeters = 0f; return false; }
            return VaDistantReach.TryGetValue(prefabName, out maxDistanceMeters);
        }

        private static readonly List<ZDO> s_buf = new List<ZDO>();
        private static readonly SimulationDistance s_oneSector = new SimulationDistance(0, 0, false);
        private static MaterialPropertyBlock s_mpb;
        private static bool s_subscribed;

        internal static void OnSourceReady(int prefab)
        {
            foreach (var t in s_targets.Values) if (t.WaitingSource) { t.WaitingSource = false; t.Dirty = true; }
        }

        internal static void Update(long deadline)
        {
            if (!s_subscribed) { FarBuildingDigest.Changed += OnZoneChanged; FarIndexRanges.OnLatched = OnIndexRangesLatched; s_subscribed = true; }
            Vector3 center = ZNet.instance.GetReferencePosition();

            if (Time.unscaledTime >= s_snowCheckAt)
            {
                s_snowCheckAt = Time.unscaledTime + 10f;
                // 1.2.428 ([visual], lead approved; Fire 11-16: "zones constantly churn and rebuild ... snow buildup gradual ... without the full rebuild flash"): a far snow
                // revision no longer marks every far target dirty (a full far rebuild + swap at every weather change or 0.02 of snow amount, [seasons]: PieceSnowDriver.Revision).
                // Far snow is a live per-target value now (LiveSnow below); the revision is only counted for the census.
                int rev = FarSnow.Active ? FarSnow.Revision : 0;
                if (rev != s_snowRevision) { if (s_snowRevision != int.MinValue) s_snowBumps++; s_snowRevision = rev; }
            }
            LiveSnow();

            if (s_planDirty || (center - s_planCenter).sqrMagnitude > 64f * 64f || Time.unscaledTime - s_planAt > 2f) Plan(center);

            // Hand-off first (cheap), then uploads, then prep with what is left.
            long t0 = Stopwatch.GetTimestamp();
            // 1.2.249: the walk over every drawn target four times a second; a hold of 10 s does not need it each frame
            if (!QuietPolling || Time.unscaledTime >= s_nextHandOff) { s_nextHandOff = Time.unscaledTime + 0.25f; HandOff(); ResolvePending(); FarIndexRanges.Tick(); FarZoneGrid.MaybeLog(); LogRequeues(); LogHandoffAndDiscipline(); FarCapPool.Tick(); FarMergedCache.MaybeLog(); InfoCensus(); }
            FarBuildings.S.HandoffTicks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            int uploaded = 0;
            if (SlicedOn || s_cursor != null) uploaded = StepUploads(deadline);
            else while ((uploaded == 0 || Stopwatch.GetTimestamp() < deadline) && FarBuildingMerge.TryTake(out var job)) { Upload(job); uploaded++; }
            RetireStep(deadline);   // [Optimize] FarRetireSliced: queued retired chunks, inside the budget (nothing queued while the key is off)
            FarBuildings.S.UploadTicks += Stopwatch.GetTimestamp() - t0;

            t0 = Stopwatch.GetTimestamp();
            Prep(center, deadline);
            FarBuildings.S.SnowTicks += Stopwatch.GetTimestamp() - t0;
        }

        internal static void Late() { }

        // 1.2.355 ([lead]: the moving budget cap trades far-build speed for frame time; the trade must be visible and bounded): cold backlog while moving
        internal static string ColdBacklog()
        {
            int cold = 0, built = 0, total = 0, coarse = 0; float oldest = 0f, now = Time.unscaledTime;
            foreach (var t in s_targets.Values)
            {
                if (t.Evicted || t.Retained) continue;
                total++; if (t.Coarse) coarse++;
                if (t.Drawn) built++; else { cold++; float age = now - t.AdmittedAt; if (age > oldest) oldest = age; }
            }
            return $"{cold} admitted target(s) not built yet (oldest admitted {oldest:0} s ago), {built} of {total} built{(coarse > 0 ? $" (of which {coarse} coarse)" : "")}, far budget {FarBuildings.BudgetMs:0.0} ms a frame while moving";
        }

        // [Heap] by holder (Debug, inside EbmHeapCensus's 60 s census; never per frame, nothing with Debug off). Every far holder on its own: layout sizes x counts, shells estimated.
        // Other classes' private holders are read by name through reflection so this stays one file; a renamed field prints 0. Runs on the main thread, where the digest, targets and
        // source table are written. The workers only touch: a FarPart's own arrays (read once into locals, a half-filled part simply counts less), the merge queues and FarCache's
        // pending writes (walked under their own locks), and the worker queue (a count, tolerant of a change). The merge worker's local lists cannot be seen from here.
        internal static long HeapHolders(System.Text.StringBuilder sb)
        {
            const double Mb = 1.0 / (1024 * 1024);
            const System.Reflection.BindingFlags Priv = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            int itemSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FarJob.Item>();
            int vertSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FarVert>();
            int pieceSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<FarPiece>();

            // draw targets and the prep in progress
            long targets = 0, prep = 0; int chunks = 0, targetZones = 0, drawn = 0;
            foreach (var t in s_targets.Values)
            {
                targets += 232L + t.Zones.Capacity * 4L + t.Chunks.Capacity * 8L;   // shell + its two lists
                targetZones += t.Zones.Count; chunks += t.Chunks.Count; if (t.Drawn) drawn++;
                if (t.PrepBuckets != null) foreach (var b in t.PrepBuckets.Values) prep += 64L + b.Items.Capacity * (long)itemSize;
                if (t.PrepMats != null) prep += 48L * t.PrepMats.Count;
            }
            long planScratch = s_want.Count * 48L + s_scratch.Count * 96L + s_order.Capacity * 8L + s_drop.Capacity * 8L + s_prepQueue.Capacity * 8L + s_sameSet.Count * 24L + s_buf.Capacity * 8L + VaDistantReach.Count * 64L;
            long cursor = s_cursor != null ? HeapChunkBytes(s_cursor.Job.Result, vertSize) + s_cursor.Built.Capacity * 8L : 0L;

            // merge queues: jobs waiting for the worker (piece lists), results waiting for upload (the CPU copy of the vertices until BuildChunk has run)
            long mergeIn = 0, mergeOut = 0; int jobsIn = 0, jobsOut = 0;
            var qIn = typeof(FarBuildingMerge).GetField("s_in", Priv)?.GetValue(null) as Queue<FarJob>;
            var qOut = typeof(FarBuildingMerge).GetField("s_out", Priv)?.GetValue(null) as Queue<FarJob>;
            var mergeLock = typeof(FarBuildingMerge).GetField("s_lock", Priv)?.GetValue(null);
            if (qIn != null && qOut != null && mergeLock != null)
                lock (mergeLock)
                {
                    jobsIn = qIn.Count; jobsOut = qOut.Count;
                    foreach (var j in qIn) foreach (var b in j.Buckets) mergeIn += 64L + b.Items.Capacity * (long)itemSize;
                    foreach (var j in qOut) mergeOut += HeapChunkBytes(j.Result, vertSize);
                }

            // one far mesh per prefab, kept as CPU arrays for the merges (never released in a session)
            long srcVerts = 0, srcIdx = 0, srcShell = 0; int srcCount = 0, srcReady = 0, srcParts = 0;
            var sources = typeof(FarBuildingSources).GetField("s_sources", Priv)?.GetValue(null) as Dictionary<int, FarSource>;
            if (sources != null)
                foreach (var s in sources.Values)
                {
                    srcCount++; if (s.Ready) srcReady++;
                    srcShell += 144L;
                    foreach (var p in s.Parts)
                    {
                        srcParts++; srcShell += 136L + (p.Mats != null ? p.Mats.Length * 8L : 0L);
                        var pos = p.Pos; var nrm = p.Nrm; var tan = p.Tan; var uv = p.Uv; var sub = p.Sub;
                        if (pos != null) srcVerts += pos.Length * 12L;
                        if (nrm != null) srcVerts += nrm.Length * 12L;
                        if (tan != null) srcVerts += tan.Length * 16L;
                        if (uv != null) srcVerts += uv.Length * 8L;
                        if (sub != null) for (int k = 0; k < sub.Length; k++) { var tri = sub[k]; if (tri != null) srcIdx += 32L + tri.Length * 4L; }
                    }
                }

            // the digest: every zone seen this world, not only those in reach
            long digest = 0, pieces = 0;
            foreach (var z in FarBuildingDigest.Zones.Values)
            {
                int n = z.Pieces != null ? z.Pieces.Length : 0;
                pieces += n; digest += 120L + n * (long)pieceSize;
            }
            var dig = typeof(FarBuildingDigest);
            long digestScratch = HeapCount(dig, "s_stamps") * 48L + HeapCount(dig, "s_ring") * 4L + HeapCount(dig, "s_ringPrev") * 4L + HeapCount(dig, "s_seen") * 20L + HeapCount(dig, "s_class") * 96L + HeapCount(dig, "s_union") * 24L + HeapCount(dig, "s_pieces") * (long)pieceSize + HeapCount(dig, "s_buf") * 8L;

            // disk cache writes still queued (the bytes stay here until the worker lands them), and the two work queues
            long cachePending = 0; int cachePendingCount = 0, cacheQueued = 0;
            var pending = typeof(FarCache).GetField("s_pending", Priv)?.GetValue(null) as Dictionary<string, byte[]>;
            var cacheLock = typeof(FarCache).GetField("s_lock", Priv)?.GetValue(null);
            if (pending != null && cacheLock != null)
                lock (cacheLock)
                {
                    cachePendingCount = pending.Count;
                    foreach (var kv in pending) cachePending += 32L + (kv.Value != null ? kv.Value.Length : 0) + kv.Key.Length * 2L;
                }
            var worker = typeof(FarCache).GetField("s_worker", Priv)?.GetValue(null);
            if (worker != null)
            {
                var jobs = worker.GetType().GetField("_jobs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(worker) as System.Collections.ICollection;
                if (jobs != null) cacheQueued = jobs.Count;
            }
            int farJobs = EbmFarJobs.Pending;
            long queues = (cacheQueued + farJobs) * 96L;

            // far lights and the impostor engine (the mirror is the pool's CPU copy of its GPU buffer)
            var fl = typeof(FarLights);
            long lights = HeapCount(fl, "s_recs") * 144L + HeapCount(fl, "s_near") * 16L + HeapCount(fl, "s_nonEmpty") * 24L + HeapCount(fl, "s_offsets") * 8L + HeapCount(fl, "s_parts") * (long)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<ParticleSystem.Particle>();
            var ip = typeof(ImpostorPool);
            long impMirror = ImpostorPool.Capacity * (long)ImpostorInstance.Stride;
            long impOther = HeapCount(ip, "s_cells") * 128L + HeapCount(ip, "s_free") * 48L + HeapCount(ip, "s_uploads") * 8L + HeapCount(ip, "s_dead") * 48L + ImpostorKinds.Count * 512L + HeapCount(typeof(ImpostorHandOff), "s_loadedAt") * 32L;

            long managed = targets + prep + planScratch + cursor + mergeIn + mergeOut + srcVerts + srcIdx + srcShell + digest + digestScratch + cachePending + queues + lights + impMirror + impOther;
            sb.Append($"Far managed {managed * Mb:0.0} MB: targets {targets * Mb:0.0} MB ({s_targets.Count} targets, {drawn} drawn, {targetZones} zones, {chunks} chunks); prep {prep * Mb:0.0} MB; plan scratch {planScratch * Mb:0.00} MB; upload cursor {cursor * Mb:0.0} MB; ");
            sb.Append($"merge in {mergeIn * Mb:0.0} MB ({jobsIn} jobs), merge results waiting {mergeOut * Mb:0.0} MB ({jobsOut} jobs); source meshes (CPU copy, session-long) vertices {srcVerts * Mb:0.0} MB + indices {srcIdx * Mb:0.0} MB + shells {srcShell * Mb:0.0} MB ({srcCount} prefabs, {srcReady} ready, {srcParts} parts); ");
            sb.Append($"digest {digest * Mb:0.0} MB ({FarBuildingDigest.Zones.Count} zones, {pieces} pieces), digest scratch {digestScratch * Mb:0.00} MB; cache writes queued {cachePending * Mb:0.0} MB ({cachePendingCount}); work queues {queues * Mb:0.00} MB ({cacheQueued} cache, {farJobs} far jobs); far lights {lights * Mb:0.00} MB; impostor mirror {impMirror * Mb:0.0} MB + other {impOther * Mb:0.00} MB; ");
            // the GPU side is native memory: not in the Mono heap and not in the returned bytes
            long gpuChunks = FarBuildings.S.GpuBytes + (s_cursor != null ? s_cursor.Bytes : 0L);
            long gpuImp = ImpostorPool.Capacity * (long)ImpostorInstance.Stride + ImpostorPool.Capacity * 4L + (ImpostorAtlas.Current != null ? ImpostorAtlas.Current.Bytes : 0L) + (ImpostorAtlas.Next != null ? ImpostorAtlas.Next.Bytes : 0L);
            sb.Append($"Far GPU (estimate, native, NOT counted above) chunks {gpuChunks * Mb:0.0} MB ({FarBuildings.S.Vertices} vertices), impostors {gpuImp * Mb:0.0} MB; ");
            return managed;
        }

        private static long HeapChunkBytes(List<FarChunk> result, int vertSize)
        {
            if (result == null) return 0L;
            long b = 0;
            foreach (var c in result)
            {
                b += 72L;
                if (c.Verts != null) b += 32L + c.Verts.Length * (long)vertSize;
                if (c.Idx != null) b += 32L + c.Idx.Length * 2L;
            }
            return b;
        }

        private static int HeapCount(Type owner, string field)
        {
            try
            {
                object v = owner.GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (v == null) return 0;
                var c = v as System.Collections.ICollection;
                if (c != null) return c.Count;
                var p = v.GetType().GetProperty("Count");   // a HashSet is no non-generic ICollection
                return p != null ? (int)p.GetValue(v, null) : 0;
            }
            catch (Exception) { return 0; }
        }

        // lost-change fix: targets that changed while being gathered, re-queued after their job (Debug, 30 s)
        private static int s_prepRequeues; private static float s_nextRequeueLog;
        private static void LogRequeues()
        {
            if (Time.unscaledTime < s_nextRequeueLog) return;
            s_nextRequeueLog = Time.unscaledTime + 30f;
            if (s_prepRequeues > 0 && EasyBakeLog.DebugOn()) EasyBakeLog.Debug($"{FarBuildings.Log} {s_prepRequeues} target(s) changed while being gathered, re-queued after their job (before 11-09 those changes were lost until the next one), last 30 s");
            s_prepRequeues = 0;
        }

        // ── 1.2.428 live far snow: once a second each SHOWN target's body chunks get the season snow cover at its origin (FarSnow.CoverAt: [seasons], a smooth
        // time function between mask flips), eased 0.05 a second so a mask flip fades in too, through a MaterialPropertyBlock (_SnowCover + the legacy name). Cap chunks
        // (healthy snow caps, merged only where the piece's own ZDO says snow) keep the block they were built with. Sent only when the value moves 0.005 or more.
        internal const string CapChunkName = "FarBuildings chunk cap";
        private static float s_liveSnowAt; private static int s_snowBumps, s_liveSnowSets;
        private static MaterialPropertyBlock s_liveMpb;
        private static void LiveSnow()
        {
            float now = Time.unscaledTime;
            if (now < s_liveSnowAt) return;
            float dt = s_liveSnowAt <= 0f ? 1f : Mathf.Min(5f, now - s_liveSnowAt + 1f);
            s_liveSnowAt = now + 1f;
            bool active = FarSnow.Active;
            if (s_liveMpb == null) s_liveMpb = new MaterialPropertyBlock();
            foreach (var t in s_targets.Values)
            {
                if (!t.Drawn || t.Chunks.Count == 0) continue;
                float want = active ? Mathf.Clamp01(FarSnow.CoverAt(t.Origin)) : 0f;
                t.SnowNow = Mathf.MoveTowards(t.SnowNow, want, 0.05f * dt);
                if (t.SnowSent >= 0f && Mathf.Abs(t.SnowNow - t.SnowSent) < 0.005f) continue;
                t.SnowSent = t.SnowNow;
                s_liveMpb.Clear();
                s_liveMpb.SetFloat(FarSnow.SnowCoverId, t.SnowNow); s_liveMpb.SetFloat(FarSnow.LegacySnowCoverId, t.SnowNow);
                foreach (var go in t.Chunks)
                {
                    if (go == null || go.name == CapChunkName) continue;
                    var r = go.GetComponent<Renderer>(); if (r != null) { r.SetPropertyBlock(s_liveMpb); s_liveSnowSets++; }
                }
            }
        }

        private static void OnZoneChanged(Vector2s zone)
        {
            if (s_targets.TryGetValue(KeyFor(zone, false), out var t) && !HideRemovedInstead(t, zone)) t.Dirty = true;
            if (s_targets.TryGetValue(KeyFor(zone, true), out var c) && !HideRemovedInstead(c, zone)) c.Dirty = true;
            if (s_targets.TryGetValue(KeyFor(zone, true, true), out var cc) && !HideRemovedInstead(cc, zone)) cc.Dirty = true;   // 1.2.353: the coarse tier's cell
            s_planDirty = true;
        }

        // 1.2.353: a coarse-tier key changed. On / off or the start distance re-plans (zones move between the cell and coarse targets); the grid or the
        // small-piece size rebuilds every coarse target in place.
        internal static void TiersChanged()
        {
            foreach (var t in s_targets.Values) if (t.Coarse) { t.Dirty = true; t.Prep = null; }
            s_planDirty = true;
            EasyBakeLog.Info($"{FarBuildings.Log} coarse tier {(FarBuildings.CoarseOn ? $"on from {FarBuildings.CoarseFrom:0} m: {FarBuildings.CoarseCluster:0.#} m grid, pieces under {FarBuildings.CoarseSmall:0.#} m left out" : "off")}; far targets re-planned");
        }

        // ---- [Optimize] FarIndexRanges (FarIndexRanges.cs) ----
        // True = the change was only pieces leaving: their index ranges are zeroed and the target is NOT rebuilt. False = rebuild as before.
        private static bool HideRemovedInstead(Target t, Vector2s zone)
        {
            if (!FarIndexRanges.On || !t.Drawn || !t.Zones.Contains(zone)) return false;   // not drawn / not a member: exactly as before
            string why = t.Runs == null ? "chunks built without ranges" : t.JobOut || t.Prep != null ? "a job in flight" : t.WaitingSource ? "a source still loading" : VaDistantReach.Count > 0 ? "VA distant reach in use" : null;
            if (why == null)
            {
                FarBuildingDigest.Zones.TryGetValue(zone, out var fz);
                if (!FarIndexRanges.OnlyRemovals(t.Runs, zone, fz != null ? fz.Pieces : null, out why)) { }
                else if (!FarIndexRanges.HideListed(t.Runs)) why = "a range write failed";
                else { FarIndexRanges.RebuildsAvoided++; FarIndexRanges.VertsNotRemerged += t.Verts; return true; }
            }
            FarIndexRanges.NoteFallback(why);
            return false;
        }

        // A kept target lost zones (Plan). True = handled without a rebuild: zones with nothing left, or whose own zone target is drawn, are hidden now;
        // the rest wait for their zone target (ResolvePending). False = rebuild as before (a zone joined, a zone loaded, a job in flight, ...).
        private static bool HideLeftZonesInstead(Target t, List<Vector2s> keep)
        {
            if (!FarIndexRanges.On || !t.Drawn) return false;
            string why = t.Runs == null ? "chunks built without ranges" : t.JobOut || t.Prep != null ? "a job in flight" : t.WaitingSource ? "a source still loading" : null;
            if (why == null)
            {
                s_sameSet.Clear(); foreach (var z in t.Zones) s_sameSet.Add(z);
                foreach (var z in keep) if (!s_sameSet.Contains(z)) { why = "a zone joined the target"; break; }
            }
            if (why == null)
            {
                s_left.Clear(); s_sameSet.Clear(); foreach (var z in keep) s_sameSet.Add(z);
                foreach (var z in t.Zones) if (!s_sameSet.Contains(z)) { if (ZoneLoaded(z)) { why = "a zone that left had loaded"; break; } s_left.Add(z); }
            }
            if (why != null) { FarIndexRanges.NoteFallback(why); return false; }
            foreach (var z in s_left)
            {
                if (!FarBuildingDigest.Zones.ContainsKey(z) || ZoneTargetShown(z))
                {
                    FarIndexRanges.ListZone(t.Runs, z);
                    if (!FarIndexRanges.HideListed(t.Runs)) { FarIndexRanges.NoteFallback("a range write failed"); return false; }
                    FarIndexRanges.CellZonesHidden++;
                }
                else { s_pending.Add(new PendingHide { Target = t.Key, Zone = z, Since = Time.unscaledTime }); FarIndexRanges.CellZonesPending++; }
            }
            FarIndexRanges.RebuildsAvoided++; FarIndexRanges.VertsNotRemerged += t.Verts;
            return true;
        }

        private struct PendingHide { public long Target; public Vector2s Zone; public float Since; }
        private static readonly List<PendingHide> s_pending = new List<PendingHide>();
        private static readonly List<Vector2s> s_left = new List<Vector2s>();

        // ── 1.2.428 ROOT FIX ([visual]; Fire 11-16: "something else toggling out the build ... the bridge build further away shows just fine"): a far copy was shown only
        // where its zone was NOT loaded (Plan skipped loaded zones, :553; Upload / FinishCursor / HandOff hid a copy whose zone was loaded; a kept copy re-showed only
        // on unload). But the REAL pieces of a loaded zone draw only inside their own LOD window (vanilla LODGroup; EBM's indirect draw uses the same window,
        // EbmIndirect.cs:364), so between the pieces' LOD cull (a few hundred metres) and the loaded edge (near x 64 m, 416 m at near 6) NOTHING drew: the town across
        // the bay vanished while the farther, never-loaded bridge showed. Now a loaded zone counts as "real pieces not drawn" once the camera is past the LOD cull of
        // EVERY piece in it (the largest reach, so a real piece and its copy never draw together); such a zone is planned, built and shown like an unloaded one.
        // A zone with a never-culled piece (no LODGroup) is never treated so (its real piece draws at any distance). 8 m hysteresis so the hand-off cannot flicker.
        private static readonly Dictionary<Vector2s, KeyValuePair<ulong, float>> s_zoneLodU = new Dictionary<Vector2s, KeyValuePair<ulong, float>>();
        private static float s_lodK; private static int s_lodKFrame = -1; private static Vector3 s_lodCam;
        private static float LodK()
        {
            if (s_lodKFrame == Time.frameCount) return s_lodK;
            s_lodKFrame = Time.frameCount;
            var cam = Camera.main;
            if (cam == null) { s_lodK = 0f; return 0f; }
            s_lodCam = cam.transform.position;
            s_lodK = QualitySettings.lodBias / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            return s_lodK;
        }
        private static float ZoneMaxLodU(Vector2s z)
        {
            if (!FarBuildingDigest.Zones.TryGetValue(z, out var fz)) return 0f;   // no far pieces known: nothing real to wait for
            if (s_zoneLodU.TryGetValue(z, out var kv) && kv.Key == fz.Print) return kv.Value;
            float u = 0f;
            foreach (var p in fz.Pieces) { float pu = FarBuildingDigest.Classify(p.Prefab).LodU; if (pu > u) u = pu; if (float.IsPositiveInfinity(u)) break; }
            s_zoneLodU[z] = new KeyValuePair<ulong, float>(fz.Print, u);
            if (s_zoneLodU.Count > 20000) s_zoneLodU.Clear();
            return u;
        }
        // true = this zone's real pieces are all past their LOD cull from the camera (so its far copy should draw)
        private static bool ZoneRealCulled(Vector2s z, float margin)
        {
            float k = LodK(); if (k <= 0f) return false;
            float u = ZoneMaxLodU(z); if (float.IsPositiveInfinity(u)) return false;
            Vector3 zc = ZoneSystem.GetZonePos(z);
            float d = Vector2.Distance(new Vector2(zc.x, zc.z), new Vector2(s_lodCam.x, s_lodCam.z)) - ZoneSystem.c_ZoneSize * 0.7072f;   // nearest corner
            return d > u * k + margin;
        }
        private static int p_realCulledShown, p_splitRebuilds;
        private static long s_intPieces, s_intTris, s_sizeCutPieces, s_sizeCutVerts; private static int s_intSaid;   // 1.2.431 census (session totals)
        private static int CountSplitTargets() { int n = 0; foreach (var t in s_targets.Values) if (t.Drawn && t.SplitBuilt >= 0) n++; return n; }
        // 1.2.429 ([visual]; Fire's 1.2.428 census at (-1810,705): "74 hidden because real pieces still draw there", the town still missing): the 1.2.428 rule hid a
        // loaded zone's whole copy while ANY piece in it was still inside its real LOD (one LOD-less piece kept the zone "real"). Now it is per PIECE: a loaded zone's
        // far copy holds exactly the pieces whose real LOD is culled from the camera (PieceRealDrawn false), and is rebuilt when that set changes (Plan); a piece the
        // game still draws is never in the copy. So the copy is never hidden for its zone being loaded, never doubles a real piece, and covers every culled one.
        // An unloaded zone's copy holds every piece, as before. The keep / edge pre-build paths become idle for loaded zones (their copies are simply shown).
        private const float SplitMargin = 16f;   // a piece joins the copy 16 m past its LOD cull, so the two never draw at once at the boundary
        private static bool PieceRealDrawn(FarPiece p)
        {
            float k = LodK(); if (k <= 0f) return true;
            float u = FarBuildingDigest.Classify(p.Prefab).LodU;
            if (float.IsPositiveInfinity(u)) return true;   // never culled by the game: always the real piece
            return (p.Pos - s_lodCam).magnitude <= u * k + SplitMargin;
        }
        private static int CountSplit(Target t)
        {
            int n = 0;
            foreach (var z in t.Zones)
            {
                if (!FarBuildingDigest.Zones.TryGetValue(z, out var fz)) continue;
                bool loaded = ZoneLoaded(z);
                foreach (var p in fz.Pieces) if (!loaded || !PieceRealDrawn(p)) n++;
            }
            return n;
        }
        // a copy is hidden only during the old near hand-off, which no longer applies (split copies never overlap real pieces)
        private static bool RealDrawing(Target t) => false;

        private static bool ZoneTargetShown(Vector2s z) => s_targets.TryGetValue(KeyFor(z, false), out var zt) && zt.Drawn && zt.Chunks.Count > 0 && zt.ShownKnown && zt.Shown;
        private static bool ZoneLoaded(Vector2s z) => FarZoneGrid.On ? FarZoneGrid.Loaded(z) : ZoneSystem.instance.IsZoneLoaded(z);

        // a cell's left zone waits for its own target: hidden the moment that target shows; rebuilt the old way if the zone loads or 10 s pass
        private static void ResolvePending()
        {
            for (int i = s_pending.Count - 1; i >= 0; i--)
            {
                var p = s_pending[i];
                if (!s_targets.TryGetValue(p.Target, out var t) || t.Runs == null || t.JobOut || t.Prep != null) { s_pending.RemoveAt(i); continue; }   // rebuilt / dropped since: the new chunks leave the zone out
                string why = ZoneLoaded(p.Zone) ? "a zone loaded while waiting" : Time.unscaledTime - p.Since > HoldSeconds ? "the zone's own target did not draw in 10 s" : null;
                if (why == null && (!FarBuildingDigest.Zones.ContainsKey(p.Zone) || ZoneTargetShown(p.Zone)))
                {
                    FarIndexRanges.ListZone(t.Runs, p.Zone);
                    if (FarIndexRanges.HideListed(t.Runs)) { FarIndexRanges.CellZonesHidden++; s_pending.RemoveAt(i); continue; }
                    // a failed write latches the key off, and the latch (OnIndexRangesLatched) already cleared this list and marked every tracked target dirty
                    if (i >= s_pending.Count) return;
                    why = "a range write failed";
                }
                if (why == null) continue;
                FarIndexRanges.NoteFallback(why);
                t.Dirty = true; s_planDirty = true;
                s_pending.RemoveAt(i);
            }
        }

        // the self-check or the raw probe failed: every target holding raw chunks is rebuilt without them
        private static void OnIndexRangesLatched()
        {
            foreach (var t in s_targets.Values) if (t.Runs != null) t.Dirty = true;
            s_pending.Clear(); s_planDirty = true;
        }

        private static long KeyFor(Vector2s z, bool cell, bool coarse = false)
        {
            int x = z.x, y = z.y;
            if (cell) { x = FloorDiv(x, CellZones); y = FloorDiv(y, CellZones); }
            return (cell ? 1L << 40 : 0L) | (cell && coarse ? 1L << 41 : 0L) | ((long)(x & 0xFFFFF) << 20) | (long)(y & 0xFFFFF);   // 1.2.353: bit 41 = the coarse tier's cell
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        private static void Plan(Vector3 center)
        {
            s_planDirty = false; s_planAt = Time.unscaledTime; s_planCenter = center;
            float reach = FarBuildings.Reach, cellFrom = FarBuildings.CellFrom;
            bool coarseOn = FarBuildings.CoarseOn; float coarseFrom = FarBuildings.CoarseFrom;   // 1.2.353
            // 1.2.234 ([ghost], profiler O 11-08: FarRunner.Update 13.75 KB GC a frame): Plan runs every 2 s and on every zone change; it used to make a
            // Target (2 lists) per far zone / cell, a set per kept target and 2 lists each time. Now pooled scratch; a Target or zone list is
            // made only for a new target or one whose zones changed.
            var want = s_want; want.Clear(); s_scratchUsed = 0;
            foreach (var kv in FarBuildingDigest.Zones)
            {
                var z = kv.Key;
                Vector3 zc = ZoneSystem.GetZonePos(z);
                float d = Vector2.Distance(new Vector2(zc.x, zc.z), new Vector2(center.x, center.z));
                if (d > reach) continue;
                // the loaded area is vanilla's, EBM's and FAP's ([Optimize] FarZoneGrid: the same answer from the frame's grid). 1.2.424: except its EDGE ring
                // (with FarKeepHandedOff): those zones are built ahead and kept hidden, so a zone that unloads always has its far copy ready (Fire 11-16)
                bool edge = false;
                if (ZoneLoaded(z)) edge = true;   // 1.2.429: every loaded zone with far pieces gets a zone-level split copy (only its LOD-culled pieces)
                bool coarse = !edge && coarseOn && d > coarseFrom;   // 1.2.353: per zone, as the cell split is
                bool cell = !edge && (d > cellFrom || coarse);
                long key = KeyFor(z, cell, coarse);
                if (!want.TryGetValue(key, out var w))
                {
                    w = NextScratch(); w.IsCell = cell; w.Coarse = coarse;
                    if (cell) { int cx = FloorDiv(z.x, CellZones), cy = FloorDiv(z.y, CellZones); w.Origin = new Vector3((cx * CellZones + CellZones / 2f) * ZoneSystem.c_ZoneSize, 0, (cy * CellZones + CellZones / 2f) * ZoneSystem.c_ZoneSize); }
                    else w.Origin = zc;
                    w.Edge = edge;
                    want[key] = w;
                }
                w.Zones.Add(z);
            }

            // Drop targets no longer wanted (unless holding for a hand-off), merge membership into kept ones.
            var drop = s_drop; drop.Clear();
            foreach (var kv in s_targets) if (!want.ContainsKey(kv.Key) && kv.Value.HoldUntil < 0f && !kv.Value.Retained) drop.Add(kv.Key);   // a kept copy (FarKeepHandedOff) stays until it expires
            foreach (var k in drop) { DestroyChunks(s_targets[k]); s_targets.Remove(k); }
            foreach (var kv in want)
            {
                if (s_targets.TryGetValue(kv.Key, out var t))
                {
                    if (t.HoldUntil < 0f && !SameZones(t.Zones, kv.Value.Zones))   // never while holding a hand-off
                    {
                        if (HideLeftZonesInstead(t, kv.Value.Zones)) t.Zones = new List<Vector2s>(kv.Value.Zones);   // [Optimize] FarIndexRanges: left zones hidden, no rebuild
                        else { t.Zones = new List<Vector2s>(kv.Value.Zones); t.Dirty = true; t.Prep = null; }
                    }
                }
                else s_targets[kv.Key] = t = new Target { Key = kv.Key, IsCell = kv.Value.IsCell, Coarse = kv.Value.Coarse, Origin = kv.Value.Origin, Zones = new List<Vector2s>(kv.Value.Zones), AdmittedAt = Time.unscaledTime };   // 1.2.355 AdmittedAt + 1.2.353 Coarse (merged 1.2.357)
                t.Dist = Vector2.Distance(new Vector2(t.Origin.x, t.Origin.z), new Vector2(center.x, center.z));
                t.Edge = kv.Value.Edge;
            }

            // 1.2.429: a split copy (loaded zone) is rebuilt when the set of its LOD-culled pieces changes, and a copy whose zone loaded or unloaded switches kind
            foreach (var st in s_targets.Values)
            {
                if (!st.Drawn || st.JobOut || st.Prep != null || st.Dirty) continue;
                if (AnyZoneLoaded(st)) { if (CountSplit(st) != st.SplitBuilt) { st.Dirty = true; p_splitRebuilds++; } }
                else if (st.SplitBuilt >= 0) { st.Dirty = true; p_splitRebuilds++; }
            }
            // Vertex cap: nearest first; past the cap a target is evicted (counted, not drawn).
            var order = s_order; order.Clear(); foreach (var tv in s_targets.Values) order.Add(tv);
            // 1.2.432 ([visual], lead approved; Fire's census: 42 targets evicted from 658 m while dense near copies filled the cap): inside the loaded edge the
            // order stays nearest first; beyond it, targets rank by screen value per vertex = (target size / distance) / vertices (built, else the estimate), so a
            // town's outline far out beats a dense pile of beams nearer. The cap then keeps the highest value and evicts the lowest first.
            float loadedEdge = ZNet.instance != null && ZoneSystem.instance != null ? ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance * ZoneSystem.instance.m_zoneSize : 0f;
            // 1.2.435 ([visual], lead 11-17, Fire BlueHills bay (-1808,711) HandTest :17399-17415: "evicted by the vertex cap 4 target(s) (nearest 721 m)"
            // with 2,818,309 of 8,000,000 built): Estimate() is raw piece vertices (/3 cell, /6 coarse), BEFORE zone simplification, interior cut and
            // size cut; the same log's raw totals (422,850 + 13,813,658 + 48,204,644 v) against 2.8M built show built is a few % of raw. So a dense
            // near town estimated past the free budget and was held unbuilt. Now every target's raw estimate is cached, the built ones give the
            // measured ratio (sum real / sum raw, clamped 0.02-1; 1 until something is built), and unbuilt targets count raw x ratio everywhere.
            long rawBuilt = 0, realBuilt = 0;
            foreach (var tv in order)
            {
                if (tv.Dirty || tv.EstAt < 0) { tv.Est = Math.Max(1, Estimate(tv)); tv.EstAt = 1; }
                if (tv.Verts > 0) { rawBuilt += tv.Est; realBuilt += tv.Verts; }
            }
            s_estRatio = rawBuilt > 0 ? Mathf.Clamp((float)((double)realBuilt / rawBuilt), 0.02f, 1f) : 1f;
            foreach (var tv in order)
            {
                if (tv.Dist <= loadedEdge) { tv.Rank = tv.Dist - 1e7f; continue; }
                float verts = tv.Verts > 0 ? tv.Verts : ScaledEst(tv);
                float size = tv.IsCell ? CellZones * ZoneSystem.c_ZoneSize * 1.4142f : ZoneSystem.c_ZoneSize * 1.4142f;
                tv.Rank = -(size / Mathf.Max(1f, tv.Dist)) / Mathf.Max(1f, verts) * 1e6f;   // higher value = earlier (lower rank)
            }
            order.Sort(s_byRank);
            int budget = FarBuildings.MaxVertices, used = 0;
            s_prepQueue.Clear();
            if (FarCapPool.HysteresisOn) CapPassHysteresis(order, budget);   // [Optimize] FarCapHysteresis (FarCapPool.cs): 90 % re-admit band, quiet time, eviction budget
            else
            {
            // 1.2.346 ([visual] / [ghost], Fire run_8 :81049 "75 of 524 drawn, 225 not built yet, evicted by the vertex cap 224 (nearest evicted
            // 824 m) ... 816,466 vertices" of an 8,000,000 cap): unbuilt targets were counted at their ESTIMATE (pieces x full prefab vertices),
            // so 225 nearer unbuilt targets filled the cap and evicted everything past 824 m before any of it was built. Now only BUILT targets'
            // real vertices count against the cap and evict (nearest first, as before); an unbuilt target is admitted for building, nearest
            // first, while built + pending estimates fit, and is otherwise only held back (never evicts a built farther target).
            int usedBuilt = 0, pendingEst = 0; s_capBuilt = s_capEst = 0;
            foreach (var t in order)
            {
                bool built = t.Verts > 0;
                bool fits;
                if (built) { fits = usedBuilt + t.Verts <= budget; if (fits) usedBuilt += t.Verts; }
                else
                {
                    int est = ScaledEst(t); fits = usedBuilt + pendingEst + est <= budget;
                    if (!fits && pendingEst == 0 && usedBuilt < budget) { fits = true; s_capForced++; }   // 1.2.435: the best-ranked unbuilt one is always built while nothing else is pending; its REAL vertices then count
                    if (fits) pendingEst += est;
                }
                if (!fits && !t.Evicted) { t.Evicted = true; DestroyChunks(t); FarBuildings.S.Evictions++; }
                if (!fits) { if (built) s_capBuilt++; else s_capEst++; }
                if (fits && t.Evicted) { t.Evicted = false; t.Dirty = true; }
                if (!t.Evicted && t.Dirty && !t.JobOut && !t.WaitingSource && !t.Retained) s_prepQueue.Add(t);   // a kept copy is rebuilt once its zone unloads (shown meanwhile)
            }
            used = usedBuilt + pendingEst;
            }
            ViewFirst(center);   // [Optimize] FarBuildViewFirst: build order only
            EdgeFirst();         // 1.2.424: edge-ring targets go first (stable), whatever the view order
            // 1.2.346 ([visual], Fire run_8: 225 targets inside 824 m "not built yet" after 20 min): how many admitted targets still wait to be built;
            // FarBuildings reads it to keep the arrival budget while the far set is cold (see FarColdBudget)
            int cold = 0, edgeCold = 0; foreach (var t in s_targets.Values) if (!t.Evicted && !t.Drawn && !t.Retained) { cold++; if (t.Edge) edgeCold++; }
            ColdTargets = cold; EdgeColdTargets = edgeCold;
            FarBuildings.S.Targets = s_targets.Count;
        }

        // 1.2.234 [ghost]: Plan's reused scratch (bounded by the far zones in reach)
        private sealed class PlanScratch { public bool IsCell, Coarse, Edge; public Vector3 Origin; public readonly List<Vector2s> Zones = new List<Vector2s>(); }   // Coarse: 1.2.353
        private static readonly Dictionary<long, PlanScratch> s_want = new Dictionary<long, PlanScratch>();
        private static readonly List<PlanScratch> s_scratch = new List<PlanScratch>();
        private static int s_scratchUsed;
        private static PlanScratch NextScratch()
        {
            if (s_scratchUsed == s_scratch.Count) s_scratch.Add(new PlanScratch());
            var s = s_scratch[s_scratchUsed++]; s.Zones.Clear(); s.Edge = false; return s;
        }
        private static readonly List<long> s_drop = new List<long>();
        private static readonly List<Target> s_order = new List<Target>();
        private static readonly Comparison<Target> s_byDist = (a, b) => a.Dist.CompareTo(b.Dist);
        private static readonly Comparison<Target> s_byRank = (a, b) => a.Rank.CompareTo(b.Rank);   // 1.2.432
        private static readonly HashSet<Vector2s> s_sameSet = new HashSet<Vector2s>();

        private static bool SameZones(List<Vector2s> a, List<Vector2s> b)
        {
            if (a.Count != b.Count) return false;
            var set = s_sameSet; set.Clear(); foreach (var z in a) set.Add(z);
            foreach (var z in b) if (!set.Contains(z)) return false;
            return true;
        }

        // 1.2.435: raw estimate x the measured built/raw ratio (see the cap pass); the raw value is cached on the target (Est)
        internal static float s_estRatio = 1f; internal static int s_capForced;
        private static int ScaledEst(Target t)
        {
            if (t.EstAt < 0) { t.Est = Math.Max(1, Estimate(t)); t.EstAt = 1; }
            return Math.Max(1, (int)(t.Est * s_estRatio));
        }

        private static int Estimate(Target t)
        {
            int v = 0;
            foreach (var z in t.Zones)
                if (FarBuildingDigest.Zones.TryGetValue(z, out var fz))
                    foreach (var p in fz.Pieces) { var s = FarBuildingSources.Get(p.Prefab); v += s != null ? s.Vertices : 200; }
            return t.Coarse ? v / 6 : t.IsCell ? v / 3 : v;   // cells are clustered; 1.2.353: coarse cells a guess of half again (only gates building order; built targets count their real vertices)
        }

        // â”€â”€ Prep: gather pieces into (material, snow) buckets on the main thread, budgeted, then hand the job to the worker â”€â”€
        private static void Prep(Vector3 center, long deadline)
        {
            bool snow = FarSnow.Active;
            for (int qi = 0; qi < s_prepQueue.Count; qi++)
            {
                var t = s_prepQueue[qi];
                // 1.2.425 FarMergedCache: an unchanged target is read from disk (worker) and shown without a gather or a merge; a miss builds as before and is saved
                if (t.Prep == null && FarMergedCache.On && !AnyZoneLoaded(t))   // 1.2.429: split copies (loaded zones) depend on the camera: never cached
                {
                    if (t.CacheState == 1 && Time.unscaledTime - t.CacheAskedAt < 10f) continue;   // its read is out (1.2.428: never longer than 10 s, a lost answer is asked again)
                    string ck = FarMergedCache.KeyFor(t.Key, t.IsCell, t.Coarse, t.Zones, FarBuildings.ClusterFor(t.Dist, t.IsCell, t.Coarse));
                    if (ck != t.CacheKey || t.CacheState != 2) { t.CacheKey = ck; t.CacheState = 1; t.CacheAskedAt = Time.unscaledTime; StartCacheRead(t, ck); continue; }
                }
                if (t.Prep == null)
                {
                    t.Generation++;
                    t.Dirty = false;   // [worldgen] 11-09 lost-change fix: cleared when the gather STARTS, so a change during it (zone, snow, E16 fallback) keeps the target dirty
                    float clusterM = FarBuildings.ClusterFor(t.Dist, t.IsCell, t.Coarse);   // 1.2.432: zone copies past "Simplify zones from" are clustered too
                    t.Prep = new FarJob { TargetKey = t.Key, Generation = t.Generation, Simplify = clusterM > 0f, Cluster = clusterM > 0f ? clusterM : 1f };   // 1.2.353: coarse grid
                    if (FarIndexRanges.On && VaDistantReach.Count == 0) { t.Prep.TrackRuns = true; t.Prep.RunKeys = new List<long>(); t.Prep.RunZones = new List<Vector2s>(); }   // [Optimize] FarIndexRanges
                    t.Prep.Pooled = FarCapPool.PooledOn;   // [Optimize] FarMergePooled: decided per job, so a live toggle never mixes
                    t.PrepBuckets = new Dictionary<long, FarJob.Bucket>(); t.PrepMats = new Dictionary<Material, int>();
                    t.PrepZone = 0; t.PrepPiece = 0;
                    t.PrepRefs = new List<int>();
                    t.Prep.Boxes = FarBuildings.InteriorOn ? new List<FarJob.PieceBox>() : null;   // 1.2.431 interior cut
                    t.PrepSplit = AnyZoneLoaded(t) ? 0 : -1;   // 1.2.429: a loaded zone's copy takes only its LOD-culled pieces
                    t.Prep.CacheKey = FarMergedCache.On && t.CacheState == 2 ? t.CacheKey : null;   // 1.2.425: a miss is saved after its merge
                }
                bool waiting = false;
                while (t.PrepZone < t.Zones.Count)
                {
                    if (Stopwatch.GetTimestamp() >= deadline) return;
                    if (!FarBuildingDigest.Zones.TryGetValue(t.Zones[t.PrepZone], out var fz) || t.PrepPiece >= fz.Pieces.Length) { t.PrepZone++; t.PrepPiece = 0; continue; }
                    var piece = fz.Pieces[t.PrepPiece];
                    if (t.PrepSplit >= 0)   // 1.2.429: counted exactly as CountSplit counts (before any other skip), so an unchanged set never rebuilds
                    {
                        if (ZoneLoaded(t.Zones[t.PrepZone]) && PieceRealDrawn(piece)) { t.PrepPiece++; continue; }   // the game draws this one itself
                        t.PrepSplit++;
                    }
                    var src = FarBuildingSources.Get(piece.Prefab);
                    if (src == null)
                    {
                        if (FarBuildingSources.IsFailed(piece.Prefab)) { t.PrepPiece++; continue; }
                        waiting = true; t.PrepPiece++; continue;    // drawn without it now, rebuilt once the source is ready
                    }
                    t.PrepPiece++;
                    // Our VA distant method stays the bar: skip what it already shows at this distance (EBM port: was Core's DistantRenderer.TryGetDistance; now EBM's own VaDistantReach table, empty until something registers into it - see HANDOVER open items).
                    if (TryGetVaDistance(src.Name, out float vaReach) && Vector3.Distance(piece.Pos, center) <= vaReach) continue;
                    // 1.2.353: the coarse tier leaves out pieces whose longest side is under Coarse small piece (they are under a pixel past ~1.5 km)
                    // 1.2.431: the size cut grows with distance past Coarse tier from (unloaded copies only, so a split copy's piece count stays exact)
                    if (t.PrepSplit < 0) { float small = FarBuildings.SmallCutAt(t.Dist, t.Coarse); if (small > 0f && FarBuildingDigest.Classify(piece.Prefab).Extent < small) { s_coarseSmallOut++; s_sizeCutPieces++; s_sizeCutVerts += src.Vertices; continue; } }
                    byte level = 0;   // 1.2.428: no snow level baked into the merge (it was FarSnow.Level in quarter steps); the chunk's snow is set live (LiveSnow)
                    int run = -1;
                    if (t.Prep.TrackRuns) { run = t.Prep.RunKeys.Count; t.Prep.RunKeys.Add(FarIndexRanges.PieceKey(piece)); t.Prep.RunZones.Add(t.Zones[t.PrepZone]); }   // one run per drawn piece
                    Matrix4x4 world = Matrix4x4.TRS(piece.Pos - t.Origin, piece.Rot, Vector3.one);
                    byte cap = CapShown(piece);   // 1.2.262: 0 = this copy's cap parts are left out
                    int pieceBox = -1;   // 1.2.431: this piece's box for the interior cut (body parts only)
                    if (t.Prep.Boxes != null && src.HasBounds) { pieceBox = t.Prep.Boxes.Count; t.Prep.Boxes.Add(new FarJob.PieceBox { C = piece.Pos - t.Origin + piece.Rot * src.LocalBounds.center, R = piece.Rot, H = src.LocalBounds.extents }); }
                    foreach (var part in src.Parts)
                    {
                        if (part.IsCap)
                        {
                            if (cap == 0) { s_capsOut++; continue; }
                            s_capsIn++;
                        }
                        Matrix4x4 m = world * part.Local;
                        bool mirror = m.determinant < 0f;
                        int subs = Math.Min(part.Sub.Length, part.Mats.Length);
                        for (int s = 0; s < subs; s++)
                        {
                            var mat = part.Mats[s];
                            if (!t.PrepMats.TryGetValue(mat, out int mi)) { mi = t.PrepMats.Count; t.PrepMats[mat] = mi; t.PrepRefs.Add(piece.Prefab); t.PrepRefs.Add(src.Parts.IndexOf(part)); t.PrepRefs.Add(s); }   // 1.2.425: (prefab, part, sub) for the merged cache
                            // a cap part buckets by its cap level (its chunk's _SnowLevel), in its own key space (bit 7), never mixed with the body's cover level
                            byte bl = part.IsCap ? cap : level;
                            long bk = ((long)mi << 8) | bl | (part.IsCap ? 0x80L : 0L);
                            if (!t.PrepBuckets.TryGetValue(bk, out var b)) { b = new FarJob.Bucket { MatIdx = mi, Snow = bl }; t.PrepBuckets[bk] = b; }
                            b.Items.Add(new FarJob.Item { Part = part, Sub = s, M = m, Mirror = mirror, Run = run, Piece = part.IsCap ? -1 : pieceBox });   // 1.2.431: caps never cut
                        }
                    }
                }
                // Done gathering: send to the worker.
                var job = t.Prep;
                job.Mats = new Material[t.PrepMats.Count];
                foreach (var kv in t.PrepMats) job.Mats[kv.Value] = kv.Key;
                job.Buckets.AddRange(t.PrepBuckets.Values);
                job.MatRefs = t.PrepRefs != null && t.PrepRefs.Count == job.Mats.Length * 3 ? t.PrepRefs.ToArray() : null;   // 1.2.425
                if (job.MatRefs == null) job.CacheKey = null;
                t.Prep = null; t.PrepBuckets = null; t.PrepMats = null; t.PrepRefs = null;
                // was "t.Dirty = false" here: a zone already gathered that changed meanwhile was lost until its next change. Now Dirty survives and Plan re-queues
                // the target once this job lands (JobOut gates it until then).
                if (t.Dirty) { s_prepRequeues++; s_planDirty = true; }
                job.SplitCount = t.PrepSplit;   // 1.2.429
                t.JobOut = true; t.WaitingSource = waiting;
                FarBuildingMerge.Queue(job);
                s_prepQueue.RemoveAt(qi); qi--;
            }
        }

        // â”€â”€ Upload a finished job as chunk objects â”€â”€
        // 1.2.425 FarMergedCache: read + parse on the far cache worker; the main thread resolves materials and hands the job to the upload queue
        private static void StartCacheRead(Target t, string key)
        {
            int gen = t.Generation; long tk = t.Key;
            FarCache.Queue(() =>
            {
                FarMergedCache.Parsed p = null;
                if (FarCache.TryRead(FarCacheScope.World, FarMergedCache.Bucket, key, out var bytes) && bytes != null) p = FarMergedCache.Parse(bytes);
                return () => CacheReadDone(tk, key, gen, p);
            });
        }

        private static void CacheReadDone(long tk, string key, int gen, FarMergedCache.Parsed p)
        {
            if (!s_targets.TryGetValue(tk, out var t)) return;   // dropped meanwhile
            // 1.2.428 (Fire's log: "oldest admitted 909 s ago", 702, 604 s: targets that never built): a target re-keyed while its read was out stayed at CacheState 1
            // forever (Prep skips state 1, and this returned before resetting it). A stale answer now hands the target back to Prep, which asks again with its new key.
            if (t.CacheKey != key || t.CacheState != 1) { if (t.CacheState == 1) { t.CacheState = 0; s_planDirty = true; } return; }
            if (t.Generation != gen || t.Prep != null || t.JobOut || t.Evicted) { t.CacheState = 0; s_planDirty = true; return; }
            if (p == null) { t.CacheState = 2; FarMergedCache.Misses++; s_planDirty = true; return; }
            var mats = FarMergedCache.Resolve(p.MatRefs);
            if (mats == null) { t.CacheState = 2; FarMergedCache.Unresolved++; s_planDirty = true; return; }
            t.Generation++; t.Dirty = false;
            t.JobOut = true; t.WaitingSource = false;
            s_prepQueue.Remove(t);
            FarBuildingMerge.Deliver(new FarJob { TargetKey = t.Key, Generation = t.Generation, Mats = mats, Result = p.Chunks });
            FarMergedCache.Hits++;
        }

        private static readonly VertexAttributeDescriptor[] s_layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float16, 2),
        };
        private const MeshUpdateFlags QuietUpload = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;

        private static GameObject BuildChunk(FarJob job, FarChunk c, Target t)
        {
            var mesh = new Mesh { name = "FarBuildings chunk", indexFormat = IndexFormat.UInt16 };
            if (job.TrackRuns) mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;   // [Optimize] FarIndexRanges: set on our own new mesh BEFORE its first upload, so ranges can be zeroed later
            // VertCount / IdxCount: the used part of the arrays ([Optimize] FarMergePooled rents longer, power-of-two arrays; equal to the lengths otherwise)
            mesh.SetVertexBufferParams(c.VertCount, s_layout);
            mesh.SetVertexBufferData(c.Verts, 0, 0, c.VertCount, 0, QuietUpload);
            mesh.SetIndexBufferParams(c.IdxCount, IndexFormat.UInt16);
            mesh.SetIndexBufferData(c.Idx, 0, 0, c.IdxCount, QuietUpload);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, c.IdxCount) { bounds = c.Bounds, vertexCount = c.VertCount }, QuietUpload);
            mesh.bounds = c.Bounds;
            mesh.UploadMeshData(true);   // GPU only: no CPU copy kept
            if (job.TrackRuns) FarIndexRanges.NoteRawChunk(mesh);   // proof 2: still has its index buffer 3 s later

            if (c.Snow > 0) s_snowChunks++;
            var pooledGo = TakeChunkObject(mesh, job.Mats[c.MatIdx], c, t);   // [Optimize] FarMergePooled: a kept chunk object, set up like a new one
            if (pooledGo != null) { pooledGo.name = c.Snow > 0 ? CapChunkName : "FarBuildings chunk"; return pooledGo; }   // 1.2.428: the name marks cap chunks for LiveSnow
            if (FarCapPool.PooledOn) FarCapPool.GoNew++;
            var go = new GameObject(c.Snow > 0 ? CapChunkName : "FarBuildings chunk") { hideFlags = HideFlags.HideAndDontSave, layer = Layer() };   // 1.2.428: cap chunks keep their own block (LiveSnow skips them)
            go.transform.SetParent(s_root.transform, false);
            go.transform.position = t.Origin;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = job.Mats[c.MatIdx];
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.BlendProbes; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (c.Snow > 0) { s_mpb.Clear(); s_mpb.SetFloat(FarSnow.SnowCoverId, c.Snow / 4f); s_mpb.SetFloat(FarSnow.LegacySnowCoverId, c.Snow / 4f); s_mpb.SetFloat(SnowLevelId, c.Snow / 4f); r.SetPropertyBlock(s_mpb); }   /* 1.2.262: _SnowLevel = a cap chunk's height (vanilla MaterialMan sets it on the whole piece too) */
            return go;
        }

        private static void Upload(FarJob job)
        {
            FarBuildings.S.WorkerTicks += job.Ticks; FarCapPool.s_workerTicks += job.Ticks;
            if (!s_targets.TryGetValue(job.TargetKey, out var t)) { FarCapPool.ReturnRange(job.Result, 0); return; }
            t.JobOut = false;
            if (job.Generation != t.Generation || t.Evicted) { FarCapPool.ReturnRange(job.Result, 0); return; }   // stale: a newer prep is coming (pooled arrays go back)

            EnsureRoot();
            DestroyChunks(t);
            if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
            int verts = 0; long bytes = 0;
            var meshes = job.TrackRuns ? new Mesh[job.Result.Count] : null;   // [Optimize] FarIndexRanges: by job.Result index
            for (int ci = 0; ci < job.Result.Count; ci++)
            {
                var c = job.Result[ci];
                if (!ChunkSane(c, t.Origin)) { FarCapPool.ReturnChunk(c); continue; }   // 1.2.232: a chunk that would draw stretched (index past its vertices, non-finite position) is never uploaded
                var go = BuildChunk(job, c, t);
                t.Chunks.Add(go);
                if (meshes != null) meshes[ci] = go.GetComponent<MeshFilter>().sharedMesh;
                verts += c.VertCount; bytes += c.VertCount * 24L + c.IdxCount * 2L;
                FarCapPool.ReturnChunk(c);   // [Optimize] FarMergePooled: uploaded, the arrays go back (no-op for unpooled chunks)
            }
            t.Verts = verts; t.Bytes = bytes; t.Drawn = true; FillTracker.NoteFarDrawn();   // 1.2.219: "Far first drawn at T+"
            t.SplitBuilt = job.SplitCount;   // 1.2.429
            s_intPieces += job.InteriorPieces; s_intTris += job.InteriorTris;   // 1.2.431
            t.CacheState = 0;   // 1.2.425: the next build of this target asks the merged cache first again
            t.Runs = meshes != null ? FarIndexRanges.Build(job, meshes) : null;
            SetVisible(t, !RealDrawing(t) || t.HoldUntil >= 0f);   // 1.2.428: hidden only while real pieces of a loaded zone still draw
            RecountStats();
            if (s_pending.Count > 0) ResolvePending();   // a zone target just drew: its cell can hide that zone now
        }

        // [Optimize] FarUploadSliced ([saddles] 11-09): a job's chunks (up to 65k vertices each) are built up to the frame's budget, at least one a frame, hidden. The target's old
        // chunks keep drawing until the whole new set exists, then swap in one step, so a part-built target is never seen. JobOut stays set meanwhile, so no second job overlaps it.
        private sealed class UploadCursor { public FarJob Job; public Target T; public int Next, Verts, Frames; public long Bytes; public readonly List<GameObject> Built = new List<GameObject>(); public Mesh[] Meshes; }   // Meshes: [Optimize] FarIndexRanges, by job.Result index
        private static UploadCursor s_cursor;
        private static bool s_saidSliced;
        private static bool SlicedOn => FiresEasyBakeMeshesPlugin.FarUploadSliced != null && FiresEasyBakeMeshesPlugin.FarUploadSliced.Value;

        private static int StepUploads(long deadline)
        {
            int uploaded = 0;
            bool first = true;
            while (first || Stopwatch.GetTimestamp() < deadline)
            {
                if (s_cursor == null)
                {
                    if (!FarBuildingMerge.TryTake(out var job)) return uploaded;
                    FarBuildings.S.WorkerTicks += job.Ticks; FarCapPool.s_workerTicks += job.Ticks;
                    if (!s_targets.TryGetValue(job.TargetKey, out var t)) { FarCapPool.ReturnRange(job.Result, 0); continue; }
                    if (job.Generation != t.Generation || t.Evicted) { t.JobOut = false; FarCapPool.ReturnRange(job.Result, 0); continue; }   // stale: a newer prep is coming
                    EnsureRoot();
                    if (s_mpb == null) s_mpb = new MaterialPropertyBlock();
                    s_cursor = new UploadCursor { Job = job, T = t, Meshes = job.TrackRuns ? new Mesh[job.Result.Count] : null };
                }
                var cur = s_cursor;
                var list = cur.Job.Result;
                cur.Frames++;
                while (cur.Next < list.Count && (first || (Stopwatch.GetTimestamp() < deadline && UnderByteCap())))   // UnderByteCap: [Optimize] FarUploadByteCap
                {
                    first = false;
                    var c = list[cur.Next++];
                    if (!ChunkSane(c, cur.T.Origin)) { FarCapPool.ReturnChunk(c); continue; }
                    NoteUploadBytes(c.VertCount * 24L + c.IdxCount * 2L);
                    var go = BuildChunk(cur.Job, c, cur.T);
                    go.SetActive(false);
                    cur.Built.Add(go);
                    if (cur.Meshes != null) cur.Meshes[cur.Next - 1] = go.GetComponent<MeshFilter>().sharedMesh;
                    cur.Verts += c.VertCount; cur.Bytes += c.VertCount * 24L + c.IdxCount * 2L;
                    FarCapPool.ReturnChunk(c);   // [Optimize] FarMergePooled: uploaded, the arrays go back
                }
                if (cur.Next < list.Count) return uploaded;
                FinishCursor();
                uploaded++;
                first = false;
            }
            return uploaded;
        }

        private static void FinishCursor()
        {
            var cur = s_cursor; s_cursor = null;
            var t = cur.T;
            t.JobOut = false;
            if (!s_targets.TryGetValue(t.Key, out var live) || !ReferenceEquals(live, t) || cur.Job.Generation != t.Generation || t.Evicted) { DestroyBuilt(cur); return; }
            if (!s_saidSliced && cur.Frames > 1) { s_saidSliced = true; EasyBakeLog.Info($"{FarBuildings.Log} chunk uploads are spread over frames (FarUploadSliced): first spread job, {cur.Built.Count} chunk(s) over {cur.Frames} frame(s)."); }
            DestroyChunks(t);
            t.Chunks.AddRange(cur.Built);
            t.Verts = cur.Verts; t.Bytes = cur.Bytes; t.Drawn = true; FillTracker.NoteFarDrawn();
            t.SplitBuilt = cur.Job.SplitCount;   // 1.2.429
            s_intPieces += cur.Job.InteriorPieces; s_intTris += cur.Job.InteriorTris;   // 1.2.431
            if (cur.Job.Boxes != null && cur.Job.Boxes.Count >= FarBuildingMerge.MinPiecesForInterior && s_intSaid < 8) { s_intSaid++; EasyBakeLog.Info($"[FarBuildings] interior cut proof #{s_intSaid}: target at ({t.Origin.x:0},{t.Origin.z:0}) {(t.IsCell ? "cell" : "zone")}: {cur.Job.Boxes.Count} piece(s), {cur.Job.InteriorPieces} enclosed and left out, {cur.Job.Boxes.Count - cur.Job.InteriorPieces} kept, {cur.Job.InteriorTris:N0} triangles saved"); }
            t.CacheState = 0;   // 1.2.425
            t.Runs = cur.Meshes != null ? FarIndexRanges.Build(cur.Job, cur.Meshes) : null;   // [Optimize] FarIndexRanges
            SetVisible(t, !RealDrawing(t) || t.HoldUntil >= 0f);   // 1.2.428: hidden only while real pieces of a loaded zone still draw
            RecountStats();
            if (s_pending.Count > 0) ResolvePending();   // a zone target just drew: its cell can hide that zone now
        }

        private static void DestroyBuilt(UploadCursor cur)
        {
            long t0 = Stopwatch.GetTimestamp();   // E18 census
            foreach (var go in cur.Built) RetireChunk(go);   // destroyed now, or hidden + queued with [Optimize] FarRetireSliced (these were never shown)
            cur.Built.Clear();
            NoteRetireTicks(Stopwatch.GetTimestamp() - t0);
        }

        // 1.2.232 ([visual], the stretch hunt: far chunks are MeshRenderers with 16-bit indices uploaded with DontValidateIndices, so an index past the
        // vertex count or a NaN position would draw exactly "stretched planks in the sky"; [Integrity] never saw this tier). Checked once per chunk.
        private static int s_badChunks; private static bool s_badSaid;
        private static bool ChunkSane(FarChunk c, Vector3 origin)
        {
            int nv = c.VertCount, badIdx = -1, badPos = -1;   // counts: a pooled array is longer than its chunk
            for (int i = 0; i < c.IdxCount; i++) if (c.Idx[i] >= nv) { badIdx = i; break; }
            for (int i = 0; i < nv && badIdx < 0; i++) { var p = c.Verts[i].P; float s = p.x + p.y + p.z; if (float.IsNaN(s) || float.IsInfinity(s) || p.sqrMagnitude > 1e10f) { badPos = i; break; } }
            if (badIdx < 0 && badPos < 0) return true;
            s_badChunks++;
            if (!s_badSaid)
            {
                s_badSaid = true;
                EasyBakeLog.Error($"[FarBuildings] BAD CHUNK at frame {Time.frameCount} (zone origin {origin}, material #{c.MatIdx}): " + (badIdx >= 0 ? $"index {badIdx} = {c.Idx[badIdx]} past the {nv} vertices" : $"vertex {badPos} at {c.Verts[badPos].P}") + $"; not drawn ({c.Idx.Length} indices). Total bad chunks so far: {s_badChunks}.");
            }
            else EasyBakeLog.Debug($"[FarBuildings] bad chunk #{s_badChunks} (zone origin {origin}), not drawn");
            return false;
        }

        // â”€â”€ Hand-off: a drawn zone that loads keeps its far mesh until its real pieces exist, then lets go â”€â”€
        private static void HandOff()
        {
            List<long> done = null, expire = null;
            foreach (var t in s_targets.Values)
            {
                if (!t.Drawn) continue;
                bool loaded = RealDrawing(t);   // 1.2.428 root fix: "loaded" = a loaded zone of it still draws its real pieces (not merely loaded)
                if (TickRetained(t, loaded, ref expire)) continue;   // [Optimize] FarKeepHandedOff: a kept copy shows again at once when its zone unloads
                if (!loaded) { if (t.HoldUntil >= 0f) t.HoldUntil = -1f; SetVisible(t, true); continue; }
                if (t.HoldUntil < 0f) t.HoldUntil = Time.unscaledTime + HoldSeconds;
                bool proven = AllInstanced(t);
                if (proven || Time.unscaledTime >= t.HoldUntil)
                {
                    (done ?? (done = new List<long>())).Add(t.Key);
                    FarBuildings.S.Handoffs++;
                    if (!proven) FarBuildings.S.HandoffTimeouts++;
                    NoteHandoff(proven, t);   // E17 census: by proof / timeout reason
                }
            }
            if (expire != null) foreach (var k in expire) Expire(s_targets[k]);
            if (done == null) { if (expire != null) RecountStats(); return; }
            foreach (var k in done)
            {
                var t = s_targets[k];
                if (Retain(t)) continue;   // [Optimize] FarKeepHandedOff: hidden and kept, not destroyed
                DestroyChunks(t); t.Drawn = false; t.HoldUntil = -1f; t.Dirty = true;
            }
            s_planDirty = true; RecountStats();
        }

        private static bool AnyZoneLoaded(Target t)
        {
            if (FarZoneGrid.On) return FarZoneGrid.AnyLoaded(t.Zones);   // [Optimize] FarZoneGrid: an exact "none" in four reads, else the same per-zone loop
            foreach (var z in t.Zones) if (ZoneSystem.instance.IsZoneLoaded(z)) return true;
            return false;
        }

        private static bool AllInstanced(Target t)
        {
            var scene = ZNetScene.instance;
            s_blockWhy = 0; s_anySkippedProof = false;
            foreach (var z in t.Zones)
            {
                if (!ZoneLoaded(z)) continue;
                s_buf.Clear();
                ZDOMan.instance.FindSectorObjects(z, s_oneSector, s_buf, null);
                foreach (var zdo in s_buf)
                    if (zdo != null && zdo.IsValid() && FarBuildingDigest.Classify(zdo.GetPrefab()).Draw && !PieceStands(zdo, scene)) { s_buf.Clear(); return false; }   // PieceStands: instance, or (1.2.256) skipped; with FarHandoffProvenDrawn, skipped AND its group drawing
            }
            s_buf.Clear();
            return true;
        }

        internal static float HoldSecondsForLog => HoldSeconds;
        private static bool CountsSkipped => FiresEasyBakeMeshesPlugin.FarHandoffCountsSkipped == null || FiresEasyBakeMeshesPlugin.FarHandoffCountsSkipped.Value;
        private static float s_nextHandOff;
        private static bool QuietPolling => FiresEasyBakeMeshesPlugin.FarBuildingsQuietPolling != null && FiresEasyBakeMeshesPlugin.FarBuildingsQuietPolling.Value;

        private static void SetVisible(Target t, bool on)
        {
            if (QuietPolling && t.ShownKnown && t.Shown == on) return;
            foreach (var go in t.Chunks) if (go != null && go.activeSelf != on) go.SetActive(on);
            t.ShownKnown = true; t.Shown = on;
        }

        private static void DestroyChunks(Target t)
        {
            long t0 = Stopwatch.GetTimestamp();   // E18 census
            foreach (var go in t.Chunks) RetireChunk(go);   // destroyed now (as before), or hidden at once + destroyed under the budget ([Optimize] FarRetireSliced)
            t.Chunks.Clear(); t.Verts = 0; t.Bytes = 0; t.Drawn = false; t.ShownKnown = false; t.Runs = null; t.Retained = false;
            t.SnowSent = -1f;   // 1.2.428: the next chunks get the live snow at the next LiveSnow pass
            NoteRetireTicks(Stopwatch.GetTimestamp() - t0);
        }

        private static void RecountStats()
        {
            int v = 0, d = 0, p = 0; long b = 0;
            foreach (var t in s_targets.Values)
            {
                if (!t.Drawn) continue;
                v += t.Verts; d += t.Chunks.Count; b += t.Bytes;
                foreach (var z in t.Zones) if (FarBuildingDigest.Zones.TryGetValue(z, out var fz)) p += fz.Pieces.Length;
            }
            FarBuildings.S.Vertices = v; FarBuildings.S.Draws = d; FarBuildings.S.GpuBytes = b; FarBuildings.S.Pieces = p;
        }

        // 1.2.428 ([visual], Fire 11-16: "we still seem to be culling build pieces at a distance a lot closer than we are culling distant veg"; his log shows the far set
        // BUILT, "140 of 140", and every edge ring ready, so the loss is on the draw side): a camera can cull a whole layer at a distance (Camera.layerCullDistances;
        // vanilla's scene camera or another mod may set one for "piece"). The far chunks take the first layer of piece / static_solid / Default whose cull distance
        // on the main camera is 0 (none) or beyond the far reach, and one Info line names the layer and every candidate's distance.
        private static int Layer()
        {
            if (s_layer != -2) return s_layer;
            var cam = Camera.main;
            if (cam == null) { int p = LayerMask.NameToLayer("piece"); return p >= 0 ? p : 0; }   // no camera yet: not cached, asked again
            float[] d = cam.layerCullDistances; float reach = FarBuildings.Reach;
            var sb = new System.Text.StringBuilder();
            s_layer = -1;
            foreach (string n in new[] { "piece", "static_solid", "Default" })
            {
                int l = LayerMask.NameToLayer(n); if (l < 0) continue;
                float c = l < d.Length ? d[l] : 0f;
                sb.Append($"{n} {(c > 0f ? c.ToString("0") + " m" : "none")}; ");
                if (s_layer < 0 && (c <= 0f || c >= reach)) s_layer = l;
            }
            if (s_layer < 0) s_layer = 0;
            EasyBakeLog.Info($"[FarBuildings] draw layer '{LayerMask.LayerToName(s_layer)}' (main camera layer cull distances: {sb}far clip {cam.farClipPlane:0} m, reach {reach:0} m)");
            return s_layer;
        }

        // 1.2.428: the Info census (60 s while far buildings draw): what is BUILT vs SHOWN vs actually RENDERED, the farthest rendered, and the camera's far clip and fog,
        // so one run proves where far pieces go missing (the Debug WHERE line is off on a player's log level).
        private static float s_nextInfoCensus;
        internal static void InfoCensus()
        {
            if (Time.unscaledTime < s_nextInfoCensus) return;
            s_nextInfoCensus = Time.unscaledTime + 60f;
            var cam = Camera.main; var p = Player.m_localPlayer;
            if (cam == null || p == null) return;
            int built = 0, shown = 0, rendered = 0, retained = 0, hiddenLoaded = 0, shownInLoaded = 0; long verts = 0; float farR = 0f, farShown = 0f; string farName = "";
            Vector3 me = p.transform.position;
            foreach (var t in s_targets.Values)
            {
                if (!t.Drawn) continue;
                built++; verts += t.Verts;
                if (t.Retained) retained++;
                bool vis = t.ShownKnown && t.Shown;
                if (!vis) { if (RealDrawing(t)) hiddenLoaded++; continue; }
                shown++; if (AnyZoneLoaded(t)) shownInLoaded++;
                float d = Vector2.Distance(new Vector2(t.Origin.x, t.Origin.z), new Vector2(me.x, me.z));
                if (d > farShown) farShown = d;
                foreach (var go in t.Chunks)
                {
                    var r = go != null ? go.GetComponent<Renderer>() : null;
                    if (r == null || !r.isVisible) continue;
                    rendered++; if (d > farR) { farR = d; farName = r.sharedMaterial != null && r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "?"; }
                    break;
                }
            }
            EasyBakeLog.Info($"[FarBuildings] census at ({me.x:0},{me.z:0}) y {me.y:0}: {built} target(s) built ({verts:N0} vertices, cap {FarBuildings.MaxVertices:N0}), {shown} shown, {rendered} rendered by a camera last frame (farthest rendered {farR:0} m, shader '{farName}'; farthest shown {farShown:0} m), {hiddenLoaded} hidden because real pieces still draw there, {shownInLoaded} shown inside the loaded area (real pieces past their LOD cull: the 1.2.428 fix), {retained} kept hidden; layer '{LayerMask.LayerToName(Layer())}', far clip {cam.farClipPlane:0} m, fog {(RenderSettings.fog ? $"{RenderSettings.fogMode} {RenderSettings.fogDensity:0.#####}" : "off")}; {s_targets.Count} target(s) in reach {FarBuildings.Reach:0} m; far snow revision bumps last 60 s {s_snowBumps} (no rebuilds from them since 1.2.428: 0 targets marked dirty), live snow sets {s_liveSnowSets}; split copies (loaded zones, LOD-culled pieces only) {CountSplitTargets()}, split rebuilds {p_splitRebuilds}");
            p_splitRebuilds = 0;
            // 1.2.430 ([lead]; Fire: "imposter vegetation is eating up all our vert budget"): what fills the cap, by prefab (drawn targets' pieces x the far mesh's
            // vertices: before a cell's ~1 m clustering, so an upper bound for cells), and what the cap left out
            var byPrefab = new Dictionary<int, long>(); var countBy = new Dictionary<int, int>();
            int evicted = 0; float nearestEvicted = float.MaxValue; long worldPc = 0, worldV = 0, playerPc = 0, playerV = 0, unkPc = 0, unkV = 0;   // 1.2.432 creator split
            foreach (var t in s_targets.Values)
            {
                if (t.Evicted) { evicted++; if (t.Dist < nearestEvicted) nearestEvicted = t.Dist; continue; }
                if (!t.Drawn) continue;
                foreach (var z in t.Zones)
                {
                    if (!FarBuildingDigest.Zones.TryGetValue(z, out var fz)) continue;
                    foreach (var pc in fz.Pieces)
                    {
                        var src = FarBuildingSources.Get(pc.Prefab); if (src == null) continue;
                        byPrefab.TryGetValue(pc.Prefab, out long v); byPrefab[pc.Prefab] = v + src.Vertices;
                        countBy.TryGetValue(pc.Prefab, out int n); countBy[pc.Prefab] = n + 1;
                        if (!pc.CreatorKnown) { unkPc++; unkV += src.Vertices; } else if (pc.World) { worldPc++; worldV += src.Vertices; } else { playerPc++; playerV += src.Vertices; }   // 1.2.432
                    }
                }
            }
            var top = new List<KeyValuePair<int, long>>(byPrefab); top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sbTop = new System.Text.StringBuilder();
            for (int i = 0; i < top.Count && i < 15; i++) { var src = FarBuildingSources.Get(top[i].Key); sbTop.Append($"{(src != null ? src.Name : top[i].Key.ToString())} {countBy[top[i].Key]} pc / {top[i].Value:N0} v; "); }
            EasyBakeLog.Info($"[FarBuildings] cap use at ({me.x:0},{me.z:0}): top prefabs by far vertices: {sbTop}evicted by the vertex cap {evicted} target(s){(evicted > 0 ? $" (nearest {nearestEvicted:0} m)" : "")}; unbuilt estimates x {s_estRatio:0.000} (built real / raw), admitted over their estimate while nothing else pending {s_capForced} (session); by creator (piece far-mesh vertices before simplification): player-built {playerPc} pc / {playerV:N0} v, world-generated (locations) {worldPc} pc / {worldV:N0} v, unknown (cached before 1.2.432, until rescanned) {unkPc} pc / {unkV:N0} v; zones simplified from {FarBuildings.SimplifyFrom:0} m; cached pieces dropped as not a build piece (no WearNTear) this session {FarBuildingDigest.s_cacheDropped}; interior cut {(FarBuildings.InteriorOn ? "on" : "off")}: {s_intPieces} enclosed piece(s), {s_intTris:N0} triangles saved; size cut {(FarBuildings.SizeCutOn ? $"on (to {FarBuildings.SizeCutMax:0.#} m at the reach)" : "off")}: {s_sizeCutPieces} piece(s), ~{s_sizeCutVerts:N0} vertices saved (session totals)");
            s_snowBumps = 0; s_liveSnowSets = 0;
        }

        private static void EnsureRoot()
        {
            if (s_root != null) return;
            s_root = new GameObject("FiresEBM_FarBuildings") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(s_root);
        }

        internal static void Clear()
        {
            if (s_cursor != null) { DestroyBuilt(s_cursor); FarCapPool.ReturnRange(s_cursor.Job.Result, s_cursor.Next); s_cursor = null; }
            foreach (var t in s_targets.Values) DestroyChunks(t);
            s_targets.Clear(); s_prepQueue.Clear(); s_pending.Clear();
            RetireAllNow();   // E18: nothing queued outlives the world
            ClearChunkObjectPool();   // [Optimize] FarMergePooled: the pooled objects die with the root below
            s_planDirty = true; s_planCenter = new Vector3(float.MaxValue, 0, 0);
            if (s_root != null) { UnityEngine.Object.Destroy(s_root); s_root = null; }
            FarBuildings.S.Vertices = FarBuildings.S.Draws = FarBuildings.S.Pieces = FarBuildings.S.Targets = 0; FarBuildings.S.GpuBytes = 0;
        }
    }
}

