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
            public List<Vector2s> Zones = new List<Vector2s>();
            public bool Dirty = true, Drawn, Evicted; public int Generation; public bool JobOut;
            public int PrepZone, PrepPiece; public FarJob Prep; public Dictionary<long, FarJob.Bucket> PrepBuckets; public Dictionary<Material, int> PrepMats;
            public bool WaitingSource; public float HoldUntil = -1f; public bool ShownKnown, Shown;   // 1.2.249: the chunks' state, so an unchanged target touches no GameObject
            public List<GameObject> Chunks = new List<GameObject>(); public int Verts; public long Bytes;
            public FarIndexRanges.Table Runs;   // [Optimize] FarIndexRanges: the drawn chunks' per-piece index ranges (null = not tracked)
            public bool Retained; public float RetainedAt;
            public float EvictedAt;   // [Optimize] FarCapHysteresis: when the vertex cap last evicted it (quiet time before it may return)   // [Optimize] FarKeepHandedOff: handed off, hidden, kept for a quick re-show (FarBuildingDrawHandoff.cs)
        }

        private static readonly Dictionary<long, Target> s_targets = new Dictionary<long, Target>();
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
        private static int s_capsIn, s_capsOut;
        internal static string TakeCapCounts() { string s = $"{s_capsIn} cap part(s) included, {s_capsOut} left out"; s_capsIn = s_capsOut = 0; return s; }
        private static byte CapShown(FarPiece piece)
        {
            if (piece.Cap == 0) return 0;
            var wg = WorldGenerator.instance;
            if (wg != null)
            {
                var biome = wg.GetBiome(piece.Pos);
                if (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth) return piece.Cap;
            }
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
                int rev = FarSnow.Active ? FarSnow.Revision : 0;
                if (rev != s_snowRevision) { if (s_snowRevision != int.MinValue) foreach (var t in s_targets.Values) t.Dirty = true; s_snowRevision = rev; }
            }

            if (s_planDirty || (center - s_planCenter).sqrMagnitude > 64f * 64f || Time.unscaledTime - s_planAt > 2f) Plan(center);

            // Hand-off first (cheap), then uploads, then prep with what is left.
            long t0 = Stopwatch.GetTimestamp();
            // 1.2.249: the walk over every drawn target four times a second; a hold of 10 s does not need it each frame
            if (!QuietPolling || Time.unscaledTime >= s_nextHandOff) { s_nextHandOff = Time.unscaledTime + 0.25f; HandOff(); ResolvePending(); FarIndexRanges.Tick(); FarZoneGrid.MaybeLog(); LogRequeues(); LogHandoffAndDiscipline(); FarCapPool.Tick(); }
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

        private static void OnZoneChanged(Vector2s zone)
        {
            if (s_targets.TryGetValue(KeyFor(zone, false), out var t) && !HideRemovedInstead(t, zone)) t.Dirty = true;
            if (s_targets.TryGetValue(KeyFor(zone, true), out var c) && !HideRemovedInstead(c, zone)) c.Dirty = true;
            s_planDirty = true;
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

        private static long KeyFor(Vector2s z, bool cell)
        {
            int x = z.x, y = z.y;
            if (cell) { x = FloorDiv(x, CellZones); y = FloorDiv(y, CellZones); }
            return (cell ? 1L << 40 : 0L) | ((long)(x & 0xFFFFF) << 20) | (long)(y & 0xFFFFF);
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        private static void Plan(Vector3 center)
        {
            s_planDirty = false; s_planAt = Time.unscaledTime; s_planCenter = center;
            float reach = FarBuildings.Reach, cellFrom = FarBuildings.CellFrom;
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
                if (ZoneLoaded(z)) continue;   // the loaded area is vanilla's, EBM's and FAP's ([Optimize] FarZoneGrid: the same answer from the frame's grid)
                bool cell = d > cellFrom;
                long key = KeyFor(z, cell);
                if (!want.TryGetValue(key, out var w))
                {
                    w = NextScratch(); w.IsCell = cell;
                    if (cell) { int cx = FloorDiv(z.x, CellZones), cy = FloorDiv(z.y, CellZones); w.Origin = new Vector3((cx * CellZones + CellZones / 2f) * ZoneSystem.c_ZoneSize, 0, (cy * CellZones + CellZones / 2f) * ZoneSystem.c_ZoneSize); }
                    else w.Origin = zc;
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
                else s_targets[kv.Key] = t = new Target { Key = kv.Key, IsCell = kv.Value.IsCell, Origin = kv.Value.Origin, Zones = new List<Vector2s>(kv.Value.Zones) };
                t.Dist = Vector2.Distance(new Vector2(t.Origin.x, t.Origin.z), new Vector2(center.x, center.z));
            }

            // Vertex cap: nearest first; past the cap a target is evicted (counted, not drawn).
            var order = s_order; order.Clear(); foreach (var tv in s_targets.Values) order.Add(tv);
            order.Sort(s_byDist);
            int budget = FarBuildings.MaxVertices, used = 0;
            s_prepQueue.Clear();
            if (FarCapPool.HysteresisOn) CapPassHysteresis(order, budget);   // [Optimize] FarCapHysteresis (FarCapPool.cs): 90 % re-admit band, quiet time, eviction budget
            else
            foreach (var t in order)
            {
                int est = t.Verts > 0 ? t.Verts : Estimate(t);
                bool fits = used + est <= budget;
                if (!fits && !t.Evicted) { t.Evicted = true; DestroyChunks(t); FarBuildings.S.Evictions++; }
                if (fits) { used += est; if (t.Evicted) { t.Evicted = false; t.Dirty = true; } }
                if (!t.Evicted && t.Dirty && !t.JobOut && !t.WaitingSource && !t.Retained) s_prepQueue.Add(t);   // a kept copy is rebuilt once its zone unloads (shown meanwhile)
            }
            ViewFirst(center);   // [Optimize] FarBuildViewFirst: build order only
            FarBuildings.S.Targets = s_targets.Count;
        }

        // 1.2.234 [ghost]: Plan's reused scratch (bounded by the far zones in reach)
        private sealed class PlanScratch { public bool IsCell; public Vector3 Origin; public readonly List<Vector2s> Zones = new List<Vector2s>(); }
        private static readonly Dictionary<long, PlanScratch> s_want = new Dictionary<long, PlanScratch>();
        private static readonly List<PlanScratch> s_scratch = new List<PlanScratch>();
        private static int s_scratchUsed;
        private static PlanScratch NextScratch()
        {
            if (s_scratchUsed == s_scratch.Count) s_scratch.Add(new PlanScratch());
            var s = s_scratch[s_scratchUsed++]; s.Zones.Clear(); return s;
        }
        private static readonly List<long> s_drop = new List<long>();
        private static readonly List<Target> s_order = new List<Target>();
        private static readonly Comparison<Target> s_byDist = (a, b) => a.Dist.CompareTo(b.Dist);
        private static readonly HashSet<Vector2s> s_sameSet = new HashSet<Vector2s>();

        private static bool SameZones(List<Vector2s> a, List<Vector2s> b)
        {
            if (a.Count != b.Count) return false;
            var set = s_sameSet; set.Clear(); foreach (var z in a) set.Add(z);
            foreach (var z in b) if (!set.Contains(z)) return false;
            return true;
        }

        private static int Estimate(Target t)
        {
            int v = 0;
            foreach (var z in t.Zones)
                if (FarBuildingDigest.Zones.TryGetValue(z, out var fz))
                    foreach (var p in fz.Pieces) { var s = FarBuildingSources.Get(p.Prefab); v += s != null ? s.Vertices : 200; }
            return t.IsCell ? v / 3 : v;   // cells are clustered
        }

        // â”€â”€ Prep: gather pieces into (material, snow) buckets on the main thread, budgeted, then hand the job to the worker â”€â”€
        private static void Prep(Vector3 center, long deadline)
        {
            bool snow = FarSnow.Active;
            for (int qi = 0; qi < s_prepQueue.Count; qi++)
            {
                var t = s_prepQueue[qi];
                if (t.Prep == null)
                {
                    t.Generation++;
                    t.Dirty = false;   // [worldgen] 11-09 lost-change fix: cleared when the gather STARTS, so a change during it (zone, snow, E16 fallback) keeps the target dirty
                    t.Prep = new FarJob { TargetKey = t.Key, Generation = t.Generation, Simplify = t.IsCell };
                    if (FarIndexRanges.On && VaDistantReach.Count == 0) { t.Prep.TrackRuns = true; t.Prep.RunKeys = new List<long>(); t.Prep.RunZones = new List<Vector2s>(); }   // [Optimize] FarIndexRanges
                    t.Prep.Pooled = FarCapPool.PooledOn;   // [Optimize] FarMergePooled: decided per job, so a live toggle never mixes
                    t.PrepBuckets = new Dictionary<long, FarJob.Bucket>(); t.PrepMats = new Dictionary<Material, int>();
                    t.PrepZone = 0; t.PrepPiece = 0;
                }
                bool waiting = false;
                while (t.PrepZone < t.Zones.Count)
                {
                    if (Stopwatch.GetTimestamp() >= deadline) return;
                    if (!FarBuildingDigest.Zones.TryGetValue(t.Zones[t.PrepZone], out var fz) || t.PrepPiece >= fz.Pieces.Length) { t.PrepZone++; t.PrepPiece = 0; continue; }
                    var piece = fz.Pieces[t.PrepPiece];
                    var src = FarBuildingSources.Get(piece.Prefab);
                    if (src == null)
                    {
                        if (FarBuildingSources.IsFailed(piece.Prefab)) { t.PrepPiece++; continue; }
                        waiting = true; t.PrepPiece++; continue;    // drawn without it now, rebuilt once the source is ready
                    }
                    t.PrepPiece++;
                    // Our VA distant method stays the bar: skip what it already shows at this distance (EBM port: was Core's DistantRenderer.TryGetDistance; now EBM's own VaDistantReach table, empty until something registers into it - see HANDOVER open items).
                    if (TryGetVaDistance(src.Name, out float vaReach) && Vector3.Distance(piece.Pos, center) <= vaReach) continue;
                    byte level = 0;
                    if (snow)
                    {
                        var cls = FarBuildingDigest.Classify(piece.Prefab);
                        level = FarSnow.Level(piece.Pos, piece.Pos + piece.Rot * new Vector3(0, cls.Height, 0));
                    }
                    int run = -1;
                    if (t.Prep.TrackRuns) { run = t.Prep.RunKeys.Count; t.Prep.RunKeys.Add(FarIndexRanges.PieceKey(piece)); t.Prep.RunZones.Add(t.Zones[t.PrepZone]); }   // one run per drawn piece
                    Matrix4x4 world = Matrix4x4.TRS(piece.Pos - t.Origin, piece.Rot, Vector3.one);
                    byte cap = CapShown(piece);   // 1.2.262: 0 = this copy's cap parts are left out
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
                            if (!t.PrepMats.TryGetValue(mat, out int mi)) { mi = t.PrepMats.Count; t.PrepMats[mat] = mi; }
                            // a cap part buckets by its cap level (its chunk's _SnowLevel), in its own key space (bit 7), never mixed with the body's cover level
                            byte bl = part.IsCap ? cap : level;
                            long bk = ((long)mi << 8) | bl | (part.IsCap ? 0x80L : 0L);
                            if (!t.PrepBuckets.TryGetValue(bk, out var b)) { b = new FarJob.Bucket { MatIdx = mi, Snow = bl }; t.PrepBuckets[bk] = b; }
                            b.Items.Add(new FarJob.Item { Part = part, Sub = s, M = m, Mirror = mirror, Run = run });
                        }
                    }
                }
                // Done gathering: send to the worker.
                var job = t.Prep;
                job.Mats = new Material[t.PrepMats.Count];
                foreach (var kv in t.PrepMats) job.Mats[kv.Value] = kv.Key;
                job.Buckets.AddRange(t.PrepBuckets.Values);
                t.Prep = null; t.PrepBuckets = null; t.PrepMats = null;
                // was "t.Dirty = false" here: a zone already gathered that changed meanwhile was lost until its next change. Now Dirty survives and Plan re-queues
                // the target once this job lands (JobOut gates it until then).
                if (t.Dirty) { s_prepRequeues++; s_planDirty = true; }
                t.JobOut = true; t.WaitingSource = waiting;
                FarBuildingMerge.Queue(job);
                s_prepQueue.RemoveAt(qi); qi--;
            }
        }

        // â”€â”€ Upload a finished job as chunk objects â”€â”€
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
            if (pooledGo != null) return pooledGo;
            if (FarCapPool.PooledOn) FarCapPool.GoNew++;
            var go = new GameObject("FarBuildings chunk") { hideFlags = HideFlags.HideAndDontSave, layer = Layer() };
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
            t.Runs = meshes != null ? FarIndexRanges.Build(job, meshes) : null;
            SetVisible(t, !AnyZoneLoaded(t) || t.HoldUntil >= 0f);
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
            t.Runs = cur.Meshes != null ? FarIndexRanges.Build(cur.Job, cur.Meshes) : null;   // [Optimize] FarIndexRanges
            SetVisible(t, !AnyZoneLoaded(t) || t.HoldUntil >= 0f);
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
                bool loaded = AnyZoneLoaded(t);
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

        private static int Layer()
        {
            if (s_layer == -2) { s_layer = LayerMask.NameToLayer("piece"); if (s_layer < 0) s_layer = 0; }
            return s_layer;
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

