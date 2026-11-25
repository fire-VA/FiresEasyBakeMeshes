using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // On-disk persistence for baked zone meshes, with an async preload so we
    // don't stall the main thread on file I/O while pieces are streaming in.
    //
    // Lifecycle (per session):
    //   1. ZNetScene.Awake â†’ BuildMaterialRegistry walks every prefab's
    //      MeshRenderers and indexes their sharedMaterials by Material.name.
    //      Resolves cached material names back to live refs at construct time.
    //   2. Plugin.Update polls ZNet.GetWorldUID() each frame. As soon as a
    //      non-zero UID appears (i.e. server world handshake completed) it
    //      calls KickPreload(uid). KickPreload spawns a background Task that
    //      reads every zone_*.bin file from disk and parses each into a
    //      CachedZoneData (intermediate POCO with Vector3[]/int[] arrays).
    //      ConcurrentDictionary makes the preload visible to the main thread
    //      as each zone finishes. No Unity API is touched off-thread.
    //   3. ZoneTracker.OnInstanceCreated, when it first sees a zone this
    //      session, calls TryGetPreloaded. If the data is in the dictionary,
    //      ConstructFromCache (main thread) builds the Mesh + GameObject
    //      hierarchy via Mesh.SetVertices/SetIndices on the prepared arrays.
    //      That's a sub-millisecond per zone â€” no perceptible hitch.
    //   4. ZoneTracker.Update, after a fresh Bake completes, writes the new
    //      BakeResult to disk (also fail-soft). Save still runs on main thread
    //      but only after a bake actually happened (Login 1 or admin edits).
    //
    // Cache identity is per-world: BepInEx/config/FiresEasyBakeMeshes/cache/
    // <worldUidHex>/zone_<x>_<y>.bin. Different worlds get different caches.
    // Same world reloaded across sessions reuses the same cache directory.
    //
    // Invalidation strategy: per-piece cache-hit matching in OnInstanceCreated.
    // Pieces matching the cache's ContributorIdentities get reattached; pieces
    // not in the cache render unbatched but do NOT trigger a rebake. To force
    // a fresh bake after major edits, delete the per-zone .bin file.
    internal static class MeshCacheStore
    {
        // "EBMC" (EasyBake Mesh Cache) as a little-endian uint32. Sanity check
        // at load time; a corrupted or stale file fails closed and we rebake.
        //
        // Bumping VERSION invalidates every existing on-disk cache. Do this
        // when the bake's filter or output changes such that old cached
        // meshes would render incorrectly (e.g., a new unsafe-component
        // filter that used to let doors/chests into the static mesh).
        //
        // Version history:
        //   1 â€” initial format.
        //   2 â€” Door + Container added to HasBakeUnsafeComponent. v1 caches
        //       contain frozen door/chest geometry; reject them so they
        //       rebake cleanly on next login.
        //   3 â€” batches are grouped by full renderer settings rather than material
        //       alone, and each zone also stores a far-tier (LOD1) batch list. v2
        //       caches have neither; reject them so they rebake.
        //   4 â€” GPU-instanced prefab groups added alongside the combined batches.
        //       A v3 cache suppressed those pieces without recording what draws
        //       them, so reject it and rebake.
        //   5 â€” each contributor's position, rotation and scale, which stand-in colliders need to replace pieces
        //       that are never created.
        //   6 â€” damageable pieces join instanced groups; a v5 zone would keep drawing them itself until it rebaked.
        //   7 â€” each identity carries a rotation key, so copies stacked on one spot turned for looks stay distinct.
        //   8 â€” tinted grausten merges into combined batches, and item and armor stands are never baked.
        //  12 â€” each piece transform and instanced group carries the wear look it was drawn with, so worn, broken and
        //       hidden pieces bake and skip with their own look.
        //  14 — the batching thresholds that produced the bake are stored in the header and rechecked on load.
        //       They decide what a bake CONTAINS - a piece with fewer copies than MinInstancesPerPrefab is left
        //       out of the instanced group and created for real - but they were not part of the key, so a cache
        //       built at one setting was served forever at another. Measured 2026-09-25: zones loaded
        //       "from cache: 2, baked fresh: 0", so lowering a threshold changed nothing and read as a null
        //       result rather than a stale one. Same class as v2/v3/v4/v6: when the rule changes, the cache is
        //       wrong and has to rebake.
        private const uint MAGIC = 0x434D4245;
        //  15 - (1.2.85) a batch's material is found by name, shader and main texture (MaterialKey), not by name alone, and
        //       another mod's prefab bakes only past ModdedPrefabGate; a v14 zone may hold a modded look under a shared name.
        // 1.2.238 held this at 15 to keep files that might reproduce the stretch. The stretch stopped after 1.2.247 (Fire's eye test) and the lead released the hold for 1.2.255; the cause for its readable FOUND meshes was never proven.
        //  16 - (1.2.255) the header lists a content print per contributing prefab ([Cache] RejectChangedPrefabs), every count is bounded by the bytes left, and the last 4 bytes are a CRC-32 of the rest.
        private const int VERSION = 16;
        private const float MaxSavedTriangleEdge = 150f;   // the world-box limit of the 1.2.235 draw gate

        // The settings whose values change what a bake contains. Compared as a unit on load; any difference
        // rejects the cache exactly as a version mismatch does, so only the zones affected by a retune pay to
        // rebake and nobody has to remember to clear a cache by hand.
        internal static int CurrentBakeSettingsKey()   // 1.2.374: internal for the ZDO-built file ([seasons], ZoneTrackerZdoBake.cs)
        {
            unchecked
            {
                int key = 17;
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerZone?.Value ?? -1);
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerBatch?.Value ?? -1);
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab?.Value ?? -1);
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled?.Value ?? true) ? 1 : 0);
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingMultiPartPieces?.Value ?? true) ? 1 : 0);
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingDamageablePieces?.Value ?? true) ? 1 : 0);
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingFarTierEnabled?.Value ?? true) ? 1 : 0);
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingFarTierDistance?.Value ?? 0f).GetHashCode();
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingExcludeTransparent?.Value ?? false) ? 1 : 0);
                // SkipDamageablePieces BELONGS HERE, which is not obvious: it changes nothing about the MESHES,
                // so the instinct is that a cached bake stays valid across it. But a bake also records
                // PieceTransforms, and whether a damageable piece gets one depends on this setting - and
                // PieceTransforms IS cached.
                //
                // Measured 2026-09-25: an A/B that turned the setting on between two runs reused the caches
                // written with it off, so 24,619 pieces reported "baked, but no stand-in transform was
                // recorded" - the largest single reason - and the feature looked half-broken when it was simply
                // reading caches that predate it. A piece with no recorded transform cannot be skipped at all,
                // because ZdoMatches has no cached rotation, scale or look to compare against.
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.SkipDamageablePieces?.Value ?? false) ? 1 : 0);
                // 1.2.338 ([visual]; [generator]'s dlcg-ruglook read: the "fewer copies" row stayed 9239 OFF and ON): DamageableLowCountGroups decides
                // whether a damageable prefab under MinInstancesPerPrefab gets a group, so it changes what a bake CONTAINS; left out of this key, the
                // ON leg reused every bake written with it off (ON log: "99 from cache, 0 fresh") and the key never ran. Its census row only re-labelled
                // small groups the old combiner-refused path had already built (the "drawn by the bake" row fell by the same 141).
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.BatchingDamageableLowCountGroups?.Value ?? false) ? 1 : 0);
                // SkipVegetation belongs here for exactly the same reason, and more strongly: it decides whether
                // the bake contains vegetation instance groups AND their PieceTransforms at all.
                key = key * 31 + ((FiresEasyBakeMeshesPlugin.SkipVegetation?.Value ?? false) ? 1 : 0);
                // GetStableHashCode, never string.GetHashCode: the latter is not guaranteed stable across
                // processes, and an unstable key here would reject every cache on every launch - a permanent
                // rebake loop that would look like the cache simply never working.
                key = key * 31 + (FiresEasyBakeMeshesPlugin.BatchingExcludedPrefabs?.Value ?? string.Empty).GetStableHashCode();
                key = key * 31 + ModdedPrefabGate.SettingsKey();   // 1.2.85: which modded prefabs may bake
                // 1.2.152: which built-in classes are never baked (feasts 1.2.148, fuel-toggled Fireplaces 1.2.152). A cache written
                // before a new class was excluded still holds those pieces and would draw them a second time beside the live one.
                key = key * 31 + "never-baked:feast,fireplace-fuel,interactable-v1".GetStableHashCode();
                return key;
            }
        }

        private static string _cacheRoot;
        private static readonly Dictionary<string, Material> _materialsByName = new Dictionary<string, Material>();
        private static readonly HashSet<int> _indexedPrefabs = new HashSet<int>();
        private static bool _materialsBuilt;

        // Preload state. _preloaded is filled by the background worker as each
        // zone finishes parsing. _preloadKicked guards against multiple kickoffs.
        private static readonly ConcurrentDictionary<Vector2s, CachedZoneData> _preloaded
            = new ConcurrentDictionary<Vector2s, CachedZoneData>();

        // 1.2.93 memory line: the preloaded cache arrays held right now (array payload only). Entries stay after a
        // zone is constructed from them, so this is the whole world's bake cache for the session.
        internal static long PreloadedBytes(out int zones)
        {
            zones = 0;
            long b = 0;
            foreach (var z in _preloaded.Values)
            {
                if (z == null) continue;
                zones++;
                b += BatchBytes(z.Batches) + BatchBytes(z.FarBatches);
                if (z.InstanceGroups != null)
                    foreach (var g in z.InstanceGroups)
                    {
                        if (g == null) continue;
                        if (g.Matrices != null) b += g.Matrices.Length * 64L;
                        if (g.PositionsX != null) b += g.PositionsX.Length * 16L;
                    }
            }
            return b;
        }

        // [Heap] by holder (Debug, inside the 60 s census): the preload's id sets and transforms, which PreloadedBytes does not count. By Count (a hash container's capacity is not readable).
        internal static long PreloadedSetBytes(out int ids, out int transforms)
        {
            ids = 0; transforms = 0;
            foreach (var z in _preloaded.Values)
            {
                if (z == null) continue;
                if (z.ContributorIdentities != null) ids += z.ContributorIdentities.Count;
                if (z.PieceTransforms != null) transforms += z.PieceTransforms.Count;
            }
            int idSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<MeshBaker.PieceIdentity>(), tfSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<MeshBaker.PieceTransform>();
            return ids * (idSize + 16L) + transforms * (idSize + tfSize + 16L);
        }

        private static long BatchBytes(List<CachedBatchData> list)
        {
            if (list == null) return 0;
            long b = 0;
            foreach (var d in list)
            {
                if (d == null) continue;
                if (d.Vertices != null) b += d.Vertices.Length * 12L;
                if (d.Normals != null) b += d.Normals.Length * 12L;
                if (d.Tangents != null) b += d.Tangents.Length * 16L;
                if (d.Uvs != null) b += d.Uvs.Length * 8L;
                if (d.Indices != null) b += d.Indices.Length * 4L;
            }
            return b;
        }
        private static volatile bool _preloadKicked;
        private static volatile bool _preloadFinished;
        private static volatile int _preloadZonesLoaded;
        private static volatile int _preloadZonesFailed;
        private static volatile int _preloadGeneration;
        private static long _preloadWorldUid;

        // --- 1.2.255 cache correctness: prefab prints (D1), disk accounting and caps (D3), and the numbers for the [Skip] line -----------------
        private static bool RejectChangedPrefabsOn => FiresEasyBakeMeshesPlugin.CacheRejectChangedPrefabs == null || FiresEasyBakeMeshesPlugin.CacheRejectChangedPrefabs.Value;
        private static bool KeepOtherSettingsOn => FiresEasyBakeMeshesPlugin.CacheKeepOtherSettings == null || FiresEasyBakeMeshesPlugin.CacheKeepOtherSettings.Value;
        private static bool PreloadNearOnlyOn => FiresEasyBakeMeshesPlugin.CachePreloadNearOnly != null && FiresEasyBakeMeshesPlugin.CachePreloadNearOnly.Value;
        // 1.2.389 ([seasons] 11-15, [lead]; Tools\EBM_1388_managed_heap_stores_20261115.md row 1: the whole world's saved bakes stayed decoded in managed memory for the
        // session, ~214 MB on the rig's client folder of 129 files). [Cache] PreloadSlim, read live: see PumpNearOnly, SlimVisit, HasSavedBake and TryGetOrLoad.
        private static bool SlimOn => FiresEasyBakeMeshesPlugin.CachePreloadSlim != null && FiresEasyBakeMeshesPlugin.CachePreloadSlim.Value;
        private const float SlimIdleSeconds = 30f;   // as [Cache] ReleaseIdleSeconds' default: a queued background write of the zone has landed by then
        private static volatile bool s_heldNear;   // this session's preload holds less than the whole world (PreloadNearOnly or PreloadSlim was on at some point)
        private static Vector2s s_nearCenter;
        private static bool s_saidSlim;
        private static int s_slimDropped, s_slimSyncLoads; private static long s_slimDroppedBytes, s_slimSyncTicks;
        private static byte[] s_syncBuffer;   // main thread only (TryGetOrLoad)
        // 1.2.390: zones whose file was read and is not usable now (ReadCachedZoneData returned null: [Cache] KeepOtherSettings keeps a file baked under another
        // settings fingerprint on disk, unused). Such a zone is in the disk table and never in _preloaded, so without this the pump read its file again every 2 s.
        private static readonly ConcurrentDictionary<Vector2s, byte> s_unusable = new ConcurrentDictionary<Vector2s, byte>();
        private static int s_unusableKey;   // the settings fingerprint the set was filled under (main thread, the pump)
        private static byte[] s_nearBuffer;   // the near worker's read buffer, kept between its runs (one worker at a time: s_nearWorker)
        private static readonly List<KeyValuePair<int, Vector2s>> s_nearScratch = new List<KeyValuePair<int, Vector2s>>();
        private static readonly Comparison<KeyValuePair<int, Vector2s>> s_nearestFirst = (a, b) => a.Key.CompareTo(b.Key);
        private static readonly Action<Vector2s, MeshBaker.BakeResult, bool> s_slimVisit = SlimVisit;
        private static long WorldCapBytes => (long)(FiresEasyBakeMeshesPlugin.CacheMaxMBPerWorld?.Value ?? 0) << 20;
        private static long AllWorldsCapBytes => (long)(FiresEasyBakeMeshesPlugin.CacheMaxMBAllWorlds?.Value ?? 0) << 20;

        private const long MaxZoneFileBytes = 512L * 1024 * 1024;   // no zone is near this (largest measured 6.2 MB); past it the file is treated as damaged
        private const long ProtectUsedTicks = 60L * TimeSpan.TicksPerSecond;   // a zone used this recently is never evicted
        private const long RestampTicks = TimeSpan.TicksPerHour;   // the file's LRU stamp is rewritten at most hourly
        private const int NearOnlyRadiusZones = 12, NearOnlyReleaseExtra = 4;

        private sealed class DiskEntry { public long Bytes, LastUse, Stamped; }
        private static readonly ConcurrentDictionary<Vector2s, DiskEntry> s_disk = new ConcurrentDictionary<Vector2s, DiskEntry>();   // this world's files, from the one listing at preload, then kept by Save / Delete
        private static readonly ConcurrentDictionary<Vector2s, byte> s_touched = new ConcurrentDictionary<Vector2s, byte>();   // D4: saved or deleted since this preload began
        private static readonly ConcurrentQueue<Vector2s> s_nearQueue = new ConcurrentQueue<Vector2s>();
        private static readonly ConcurrentDictionary<Vector2s, byte> s_nearLoading = new ConcurrentDictionary<Vector2s, byte>();
        private static long s_diskBytes, s_bytesRead, s_bytesEvicted;
        private static string _preloadFolder;
        private static bool s_saidFolder;
        internal static bool LowGarbageOn => FiresEasyBakeMeshesPlugin.CacheLowGarbage != null && FiresEasyBakeMeshesPlugin.CacheLowGarbage.Value;

        // 1.2.255 [Cache] SeparateRoleFolders: the one builder of a world's folder name; every path goes through it. Main thread only (workers use _preloadFolder).
        // ZNet.SetServer fixes the role before the scene loads, and a client's world UID arrives later with the server's peer info, so a non-zero UID means the role is already right.
        // Null = no ZNet and not this session's world: the caller does nothing, never a guessed folder.
        private static string WorldFolder(long uid)
        {
            string hex = uid.ToString("x16");
            if (FiresEasyBakeMeshesPlugin.CacheSeparateRoleFolders != null && !FiresEasyBakeMeshesPlugin.CacheSeparateRoleFolders.Value) return hex + s_coldSuffix;   // 1.2.382: + the cold test suffix (null outside ebm_coldcache)
            var net = ZNet.instance;
            if (net == null) return uid == _preloadWorldUid ? _preloadFolder : null;
            return hex + (net.IsServer() ? "_host" : "_client") + s_coldSuffix;   // 1.2.382: + the cold test suffix (null outside ebm_coldcache)
        }

        // 1.2.382 ([lead], Fire: off / on tests inside ONE run; TD's REBAKE is warm only: the cache files and the in-memory preload stay): the console command
        // ebm_coldcache on | off | status. ON moves this SESSION's saved zones to the test folder "<world>_client_coldtest" (or _host_coldtest): the test folder
        // is emptied (only a folder whose name ends in the suffix is ever deleted from), the in-memory preload and the file listing are dropped and the empty
        // folder is preloaded, so every zone is a first visit again once the zones are rebuilt (PluginEnabled off -> on). The world's real folder is neither read
        // nor written while it is on. OFF goes back to the real folder (its files are preloaded again: a warm arm later in the same run). The suffix is a static:
        // it never reaches the cfg, a new launch or another world. Not a key on purpose: a saved key would run a player cold in every later session.
        // The far cache and the prewarm lists are separate stores and stay warm in both arms.
        internal const string ColdSuffix = "_coldtest";
        private static string s_coldSuffix;
        private static bool s_coldNoTrim;
        internal static bool ColdSession => s_coldSuffix != null;

        // 1.2.384 ([visual]'s review note on 1.2.382, [lead]): the cold test folder ended only when ANOTHER world's id arrived, so a logout and login to the same world in the
        // same launch stayed cold with no line saying so. It now ends when the world closes (ZNetScene.Shutdown): the next login preloads the real folder (KickPreload sees
        // a different folder name and re-arms). One Info line, only when it was on.
        internal static void ColdSessionWorldClosed()
        {
            if (s_coldSuffix == null) return;
            s_coldSuffix = null;
            EasyBakeLog.Info("[Cache] ebm_coldcache ended with the world: the next login uses the world's real cache folder again");
        }

        internal static string ColdSessionStatus()
        {
            long uid = TryGetWorldUid();
            string folder = uid != 0L ? WorldFolder(uid) : null;
            return $"ebm_coldcache status: {(s_coldSuffix != null ? "ON (cold test folder)" : "off (the world's real folder)")}, folder {folder ?? "(no world yet)"}, preload {(_preloadKicked ? (_preloadFinished ? "finished" : "RUNNING") : "not started")}, {_preloaded.Count} zone(s) in memory, {s_disk.Count} file(s) listed";
        }

        // Main thread (a console command). Returns the line to show; changes nothing unless it says so.
        internal static string SwitchColdSession(bool on)
        {
            long uid = TryGetWorldUid();
            if (uid == 0L || _cacheRoot == null) return "ebm_coldcache: no world yet; nothing changed";
            if (_preloadKicked && !_preloadFinished) return "ebm_coldcache: the cache preload is still running; nothing changed, run the command again in a second";
            string before = s_coldSuffix;
            s_coldSuffix = on ? ColdSuffix : null;
            string folder = WorldFolder(uid);
            if (folder == null) { s_coldSuffix = before; return "ebm_coldcache: this world's cache folder is not known yet; nothing changed"; }
            int deleted = 0;
            try
            {
                if (s_sliceJobs.Count > 0) { EasyBakeLog.Debug($"[Cache] {s_sliceJobs.Count} pending sliced save(s) dropped: ebm_coldcache switches the session's cache folder"); s_sliceJobs.Clear(); }   // as DeleteWorld: a job finishing later would mirror its zone into the new session's preload
                AsyncSaves.FlushIfBusy();   // a write still queued must not land after the delete below
                if (on)
                {
                    // The ONLY delete: zone files directly under a folder whose name ends in the test suffix. Anything else is refused.
                    if (!folder.EndsWith(ColdSuffix, StringComparison.Ordinal)) { s_coldSuffix = before; return $"ebm_coldcache: REFUSED, the folder name '{folder}' does not end in {ColdSuffix}; nothing changed"; }
                    var dir = Path.Combine(_cacheRoot, folder);
                    if (Directory.Exists(dir))
                        foreach (var file in Directory.GetFiles(dir, "zone_*.bin")) { File.Delete(file); deleted++; }
                }
            }
            catch (Exception ex)
            {
                s_coldSuffix = before;
                return $"ebm_coldcache: FAILED ({ex.GetType().Name}: {ex.Message}); nothing changed";
            }
            // Drop what is in memory for the folder being left and preload the folder now named (KickPreload returns at once for an unchanged folder name, so it is cleared here).
            _preloadGeneration++;
            _preloadFinished = true;
            _preloaded.Clear();
            s_disk.Clear(); s_unusable.Clear(); Interlocked.Exchange(ref s_diskBytes, 0L);
            _preloadFolder = null;
            s_coldNoTrim = true;   // [visual]: TrimOtherWorlds treats every folder but the current one as another world; with the test folder current, this world's REAL folder would be an eviction candidate
            try { KickPreload(uid); } finally { s_coldNoTrim = false; }
            string line = on
                ? $"[Cache] ebm_coldcache on: this session's saved zones now live in {folder} ({deleted} old test file(s) removed, so it starts empty); the world's real folder is untouched and not read; far / prewarm caches stay warm. Rebuild the zones (PluginEnabled off -> on) for a cold first visit"
                : $"[Cache] ebm_coldcache off: back to the world's real folder {folder} (its saved zones are preloaded again); rebuild the zones (PluginEnabled off -> on) for a warm visit";
            EasyBakeLog.Info(line);
            return line;
        }
        private static int s_nearWorker, s_evicted, s_worldsEvicted, s_badFiles;
        private static int s_hits, s_missNoFile, s_missPrint, s_missOffset, s_missOther;
        private static int s_missSettings, s_missSettingsKept, s_missMaterial; private static string s_firstPrint, s_firstMaterial;   // 1.2.319: + the first missing material, fully named (DescribeMissingMaterial); 1.2.317 [Cache] summary: the reason split + the first changed prefab
        private static float s_nextEvict, s_nextNearPump;
        private static bool s_saidPrint, s_saidOffset, s_saidCaps, s_saidKeepOther, s_saidNearOnly;
        // 1.2.338 (lead 11-12, after the DamageableLowCountGroups gate ran on cached bakes): ONE Info line at the first cache decision of the session,
        // so a leg log says which fingerprint this launch bakes under and whether the first zone read was reused or rebuilt. Keep KeyCount equal to
        // the number of terms folded in CurrentBakeSettingsKey (ModdedPrefabGate.SettingsKey and the never-baked string count one each; a new bake-content key = +1 here and a term there, in the same build).
        private const int BakeSettingsKeyCount = 15;
        private static int s_saidFingerprint;   // Interlocked: the preload worker (ReadCachedZoneData) and the main thread can both get here first
        private static void SayFingerprint(Vector2s coord, string outcome)
        {
            if (System.Threading.Interlocked.Exchange(ref s_saidFingerprint, 1) != 0) return;
            EasyBakeLog.Info($"[Cache] bake cache: fingerprint {CurrentBakeSettingsKey()} ({BakeSettingsKeyCount} keys); first zone ({coord.x},{coord.y}): {outcome}");
        }

        internal static int StatTotal => s_hits + s_missNoFile + s_missPrint + s_missOffset + s_missOther + Volatile.Read(ref s_evicted) + Volatile.Read(ref s_badFiles);

        private static int s_missKeptZone, p_keptZone;   // 1.2.392: zones created while their file is a read, not usable one (kept other-settings file)
        internal static void NoteNoFile(Vector2s coord) { s_missNoFile++; if (s_unusable.ContainsKey(coord)) s_missKeptZone++; }   // 1.2.392: the total stays in s_missNoFile (StatTotal, CacheSummary unchanged); the kept-file share was taken from the file READ count before

        // 1.2.317 ([lead] 11-11, Fire's hand-over rebaked the whole town on every arrival with no line to say why): ONE Info line, at most every 30 s and
        // only when something happened, with counters only (no per-zone lines): zones baked fresh by reason, attached from cache, zone files written,
        // pieces given back / re-adopted around unloads, and host skips next to the deletions the guard blocked. Called from the plugin's LateUpdate.
        private static float s_sumNext;
        private static int p_hits, p_noFile, p_print, p_offset, p_other, p_settings, p_settingsKept, p_material, p_saves, p_given, p_readopt, p_hostSkips, p_blocked;
        internal static void SummaryTick()
        {
            float now = Time.unscaledTime;
            if (now < s_sumNext) return;
            s_sumNext = now + 30f;
            int hits = s_hits - p_hits, settings = (s_missSettings - p_settings) + (s_missKeptZone - p_keptZone), material = s_missMaterial - p_material, print = s_missPrint - p_print;   // 1.2.392: zones created over a kept file, not file reads
            int noFile = Math.Max(0, (s_missNoFile - p_noFile) - (s_missKeptZone - p_keptZone));   // 1.2.392: minus the zones created over a kept file (was: minus the file reads)
            int keptReads = s_missSettingsKept - p_settingsKept;   // 1.2.392: reads of kept other-settings files (preload / near pump / on demand), whether or not the zone is ever built
            int other = Math.Max(0, (s_missOther - p_other) - (s_missSettings - p_settings) - material) + (s_missOffset - p_offset);
            int saves = ZoneTracker.SavesTotal - p_saves, given = ZoneTracker.UnloadGivenBack - p_given, readopt = ZoneTracker.UnloadReadopted - p_readopt;
            int hostSkips = ZoneTracker.HostSkipsTotal - p_hostSkips, blocked = FiresEasyBakeMeshes.Patches.ZDOMan_DestroyZDO_SkipGuard_Patch.BlockedDeletions - p_blocked;
            int fresh = settings + print + material + noFile + other;
            p_hits = s_hits; p_noFile = s_missNoFile; p_print = s_missPrint; p_offset = s_missOffset; p_other = s_missOther; p_settings = s_missSettings; p_settingsKept = s_missSettingsKept; p_material = s_missMaterial; p_keptZone = s_missKeptZone;
            p_saves = ZoneTracker.SavesTotal; p_given = ZoneTracker.UnloadGivenBack; p_readopt = ZoneTracker.UnloadReadopted; p_hostSkips = ZoneTracker.HostSkipsTotal; p_blocked = FiresEasyBakeMeshes.Patches.ZDOMan_DestroyZDO_SkipGuard_Patch.BlockedDeletions;
            if (fresh + hits + saves + given + readopt + hostSkips + blocked + keptReads == 0) return;
            // 1.2.363 ([visual] read of 1.2.362: "EBM LateUpdate: CacheSummary" ~50 ms every 30 s; every value read here is a plain field): build vs log write, markers only
            string sumLine; using (SummaryMarkers.BuildLine.Auto()) sumLine = ($"[Cache] last 30 s: zones baked fresh {fresh} (rejected: settings {settings} / changed prefab {print}{(print > 0 && s_firstPrint != null ? $" (first '{s_firstPrint}')" : "")} / missing material {material}{(material > 0 && s_firstMaterial != null ? $" (first {s_firstMaterial})" : "")} / no cache file {noFile} / other {other}); material matched by identity after a runtime change (1.2.320) {s_idFallbacks} so far{(s_firstIdFallback != null ? $" (first {s_firstIdFallback})" : "")}; prefabs with no materials yet (a stub; asked again later) {s_emptyPrefabs} so far; "
                + $"attached from cache {hits}; old-settings files read and skipped {keptReads} (kept on disk, no rebake by itself); zone files written {saves}; pieces given back at unload {given}, re-adopted at reload {readopt}; host skips {hostSkips} (1.2.318: kept from the game's delete by the create-loop gate), deletions blocked by the guard {blocked}"); using (SummaryMarkers.LogWrite.Auto()) EasyBakeLog.InfoFirst("Cache 30 s", sumLine);   // 1.2.364: first Info, then Debug (repeating)
        }

        internal static string CacheSummary()
        {
            if (StatTotal == 0) return null;
            long cap = WorldCapBytes;
            return $"zone cache: {s_hits} hit(s), misses {s_missNoFile} no file, {s_missPrint} changed prefab, {s_missOffset} changed pivot, {s_missOther} other; "
                + $"{Interlocked.Read(ref s_bytesRead) >> 10} KB read, {Interlocked.Read(ref s_diskBytes) >> 20} MB on disk (cap {(cap > 0 ? (cap >> 20) + " MB" : "none")}); "
                + $"{Volatile.Read(ref s_evicted)} evicted ({Interlocked.Read(ref s_bytesEvicted) >> 20} MB), {Volatile.Read(ref s_worldsEvicted)} other world(s) removed, {Volatile.Read(ref s_badFiles)} damaged file(s)";
        }

        // [Cache] RejectChangedPrefabs: what a prefab looks like, once per prefab, from what is reachable with no GPU read, so a non-readable mesh is covered the same way:
        // each renderer's object name and its transform chain up to the prefab root, its mesh's name, vertex count, submesh count, index count per submesh and bounds, and its material slots by name.
        // CANNOT see: vertices moved inside the same bounds with the same counts and name; UVs; or a same-named replacement of identical shape. Shader, texture and colour are left out on purpose:
        // snow and season mods change them in place, which would rebake on every swap, and the MaterialKey check below already catches them for merged meshes while instanced pieces draw the live material.
        // 0 = cannot tell yet (prefab not registered, or a lazily loaded prefab with its assets released): never compared, never kept.
        private static readonly Dictionary<int, int> s_prefabPrints = new Dictionary<int, int>();   // one entry per prefab
        private static readonly List<MeshRenderer> s_printRenderers = new List<MeshRenderer>(16);
        private static readonly List<Material> s_printMaterials = new List<Material>(4);
        private static readonly HashSet<int> s_printSeen = new HashSet<int>();

        internal static void ForgetPrefabPrints() => s_prefabPrints.Clear();

        private static int PrefabPrint(int prefabHash)
        {
            if (s_prefabPrints.TryGetValue(prefabHash, out int known)) return known;
            var prefab = ZNetScene.instance != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabHash) : null;
            if (prefab == null) return 0;
            int print = ComputePrint(prefab);
            if (print != 0) s_prefabPrints[prefabHash] = print;
            return print;
        }

        private static int Q(float v) => Mathf.RoundToInt(v * 1000f);

        private static int ComputePrint(GameObject prefab)
        {
            unchecked
            {
                int h = 17;
                prefab.GetComponentsInChildren(true, s_printRenderers);
                int drawn = 0;
                for (int i = 0; i < s_printRenderers.Count; i++)
                {
                    var renderer = s_printRenderers[i];
                    if (renderer == null) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null) continue;
                    var mesh = filter.sharedMesh;
                    if (mesh == null) return 0;   // released or not built yet: unknown, not changed
                    drawn++;
                    h = h * 31 + renderer.gameObject.name.GetStableHashCode();
                    for (var t = renderer.transform; t != null && t != prefab.transform; t = t.parent)
                    {
                        Vector3 p = t.localPosition, s = t.localScale; Quaternion r = t.localRotation;
                        h = h * 31 + Q(p.x); h = h * 31 + Q(p.y); h = h * 31 + Q(p.z);
                        h = h * 31 + Q(r.x); h = h * 31 + Q(r.y); h = h * 31 + Q(r.z); h = h * 31 + Q(r.w);
                        h = h * 31 + Q(s.x); h = h * 31 + Q(s.y); h = h * 31 + Q(s.z);
                    }
                    h = h * 31 + mesh.name.GetStableHashCode();
                    h = h * 31 + mesh.vertexCount;
                    h = h * 31 + mesh.subMeshCount;
                    for (int sub = 0; sub < mesh.subMeshCount; sub++) h = h * 31 + (int)mesh.GetIndexCount(sub);
                    Bounds b = mesh.bounds;
                    h = h * 31 + Q(b.center.x); h = h * 31 + Q(b.center.y); h = h * 31 + Q(b.center.z);
                    h = h * 31 + Q(b.extents.x); h = h * 31 + Q(b.extents.y); h = h * 31 + Q(b.extents.z);
                    renderer.GetSharedMaterials(s_printMaterials);
                    for (int m = 0; m < s_printMaterials.Count; m++)
                        h = h * 31 + (s_printMaterials[m] != null ? s_printMaterials[m].name.Replace(" (Instance)", "").GetStableHashCode() : 1);
                }
                s_printRenderers.Clear(); s_printMaterials.Clear();
                if (drawn == 0) return 0;
                return h == 0 ? 1 : h;
            }
        }

        internal static int[] ZonePrints(MeshBaker.BakeResult bake)   // 1.2.374: internal for the ZDO-built file
        {
            s_printSeen.Clear();
            if (bake.ContributorIdentities != null) foreach (var id in bake.ContributorIdentities) s_printSeen.Add(id.PrefabHash);
            if (bake.InstanceGroups != null) for (int i = 0; i < bake.InstanceGroups.Count; i++) s_printSeen.Add(bake.InstanceGroups[i].PrefabHash);
            var prints = new int[s_printSeen.Count * 2];
            int n = 0;
            foreach (int hash in s_printSeen) { prints[n++] = hash; prints[n++] = PrefabPrint(hash); }
            s_printSeen.Clear();
            return prints;
        }

        // Null = every prefab this zone was baked from still looks the same (or cannot be told yet); otherwise the first that changed.
        private static string ChangedPrefab(CachedZoneData data)
        {
            var prints = data.PrefabPrints;
            if (prints == null) return null;
            for (int i = 0; i + 1 < prints.Length; i += 2)
            {
                if (prints[i + 1] == 0) continue;
                int now = PrefabPrint(prints[i]);
                if (now == 0 || now == prints[i + 1]) continue;
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prints[i]);
                return prefab != null ? prefab.name : prints[i].ToString();
            }
            return null;
        }

        private static void NoteDisk(Vector2s coord, long bytes)
        {
            long now = DateTime.UtcNow.Ticks;
            s_unusable.TryRemove(coord, out _);   // 1.2.390: a save replaces the file
            var entry = s_disk.GetOrAdd(coord, _ => new DiskEntry());
            Interlocked.Add(ref s_diskBytes, bytes - Interlocked.Exchange(ref entry.Bytes, bytes));
            entry.LastUse = now; entry.Stamped = now;
        }

        private static void NoteGone(Vector2s coord)
        {
            s_unusable.TryRemove(coord, out _);   // 1.2.390
            if (s_disk.TryRemove(coord, out var entry)) Interlocked.Add(ref s_diskBytes, -Interlocked.Read(ref entry.Bytes));
        }

        private static void NoteUsed(Vector2s coord)
        {
            if (!s_disk.TryGetValue(coord, out var entry)) return;
            long now = DateTime.UtcNow.Ticks;
            entry.LastUse = now;
            if (now - entry.Stamped < RestampTicks) return;
            entry.Stamped = now;
            var path = GetZonePath(_preloadWorldUid, coord);
            if (path != null) AsyncSaves.QueueTouch(path);
        }

        // Main thread, after a save. O(1) until the running total is over the cap; then the in-memory table is sorted, the folder is never listed.
        private static void EvictOverCap(long worldUid)
        {
            long cap = WorldCapBytes;
            if (cap <= 0L || Interlocked.Read(ref s_diskBytes) <= cap || Time.unscaledTime < s_nextEvict) return;
            s_nextEvict = Time.unscaledTime + 5f;
            SayCaps();
            var entries = new List<KeyValuePair<Vector2s, DiskEntry>>(s_disk);
            entries.Sort((a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));
            long goal = cap * 9 / 10, recent = DateTime.UtcNow.Ticks - ProtectUsedTicks;
            int removed = 0; long freed = 0;
            for (int i = 0; i < entries.Count && Interlocked.Read(ref s_diskBytes) > goal; i++)
            {
                if (entries[i].Value.LastUse >= recent) continue;
                var path = GetZonePath(worldUid, entries[i].Key);
                if (path == null) continue;
                freed += Interlocked.Read(ref entries[i].Value.Bytes);
                s_touched.TryAdd(entries[i].Key, 0);
                NoteGone(entries[i].Key);   // 1.2.389: the disk row first (LoadOne afterPreload)
                _preloaded.TryRemove(entries[i].Key, out _);
                AsyncSaves.Queue(path, null);
                removed++;
            }
            Interlocked.Add(ref s_evicted, removed);
            Interlocked.Add(ref s_bytesEvicted, freed);
            if (removed > 0) EasyBakeLog.Debug($"[Cache] over the {cap >> 20} MB world cap: removed {removed} least recently used zone file(s), {freed >> 20} MB");
        }

        private static void SayCaps()
        {
            if (s_saidCaps) return;
            s_saidCaps = true;
            EasyBakeLog.Info($"[Cache] zone cache size caps engaged: least recently used zones are removed past {WorldCapBytes >> 20} MB per world and {AllWorldsCapBytes >> 20} MB across worlds");
        }

        // Worker, once per world join: the one listing of the other worlds' folders. The current world is never removed.
        private static void TrimOtherWorlds(string current)
        {
            try
            {
                // v15 and older files in this world's unsuffixed folder can never be read again (the format moved on and the role folders replaced it): gone once, or they would sit on disk until the all-worlds cap
                if (_cacheRoot != null && current.Length > 16)
                {
                    var legacy = Path.Combine(_cacheRoot, current.Substring(0, 16));
                    if (Directory.Exists(legacy)) { Directory.Delete(legacy, true); EasyBakeLog.Debug($"[Cache] removed this world's old-format folder {current.Substring(0, 16)}"); }
                }
                long cap = AllWorldsCapBytes;
                if (cap <= 0L || _cacheRoot == null || !Directory.Exists(_cacheRoot)) return;
                // current: this session's folder, passed in from the main thread
                var worlds = new List<KeyValuePair<DirectoryInfo, long>>();
                long total = 0;
                foreach (var d in new DirectoryInfo(_cacheRoot).GetDirectories())
                {
                    if (d.Name.Length != 16 && !d.Name.EndsWith("_host", StringComparison.Ordinal) && !d.Name.EndsWith("_client", StringComparison.Ordinal)) continue;   // only the per-world folders this store made; both roles count
                    long bytes = 0;
                    foreach (var f in d.GetFiles("zone_*.bin")) bytes += f.Length;
                    total += bytes;
                    if (d.Name != current) worlds.Add(new KeyValuePair<DirectoryInfo, long>(d, bytes));
                }
                if (total <= cap) return;
                SayCaps();
                worlds.Sort((a, b) => a.Key.LastWriteTimeUtc.CompareTo(b.Key.LastWriteTimeUtc));
                for (int i = 0; i < worlds.Count && total > cap; i++)
                {
                    try
                    {
                        worlds[i].Key.Delete(true);
                        total -= worlds[i].Value;
                        Interlocked.Increment(ref s_worldsEvicted);
                        Interlocked.Add(ref s_bytesEvicted, worlds[i].Value);
                        EasyBakeLog.Debug($"[Cache] over the {cap >> 20} MB all-worlds cap: removed the least recently visited world folder {worlds[i].Key.Name}, {worlds[i].Value >> 20} MB");
                    }
                    catch (Exception ex) { EasyBakeLog.Debug($"[Cache] could not remove world folder {worlds[i].Key.Name}: {ex.Message}"); }
                }
            }
            catch (Exception ex) { EasyBakeLog.Debug($"[Cache] all-worlds cap check failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        // [Cache] PreloadNearOnly (default off): main thread, from the 0.25 s range pass. Only zones near the player are parsed and held in RAM. A far zone reached quickly
        // (a teleport) may miss its cache that visit and be built from live pieces: the visible cost of the key.
        internal static void PumpNearOnly(Vector2s center)
        {
            if (!_preloadFinished) return;
            bool slim = SlimOn;
            if (!slim && !PreloadNearOnlyOn)
            {
                if (!s_heldNear) return;
                // 1.2.389: the key went off while playing. Every saved zone that is not in memory is read again, so the session is back to holding the whole world.
                s_heldNear = false;
                int asked = 0;
                foreach (var kv in s_disk)
                    if (!_preloaded.ContainsKey(kv.Key) && !s_unusable.ContainsKey(kv.Key) && s_nearLoading.TryAdd(kv.Key, 0)) { s_nearQueue.Enqueue(kv.Key); asked++; }
                EasyBakeLog.Info($"[Cache] PreloadSlim / PreloadNearOnly off: {asked} saved zone(s) of this world are read back into memory");
                StartNearWorker();
                return;
            }
            s_heldNear = true;
            if (center.x != s_nearCenter.x || center.y != s_nearCenter.y) { s_nearCenter = center; s_nextNearPump = 0f; }   // 1.2.389: at once when the player's zone changes (a teleport), not up to 2 s later
            if (Time.unscaledTime < s_nextNearPump) return;
            s_nextNearPump = Time.unscaledTime + 2f;
            int fingerprint = CurrentBakeSettingsKey();
            if (fingerprint != s_unusableKey) { s_unusableKey = fingerprint; s_unusable.Clear(); }   // 1.2.390
            if (slim)
            {
                if (!s_saidSlim) { s_saidSlim = true; EasyBakeLog.Info("[Cache] PreloadSlim on: a built zone's saved copy leaves memory once its file holds the same bake, zones not built are held near you only, and a zone asked for sooner is read from its file right then"); }
                ZoneTracker.ForEachBakedZone(s_slimVisit);
            }
            s_nearScratch.Clear();
            foreach (var kv in s_disk)
            {
                var d = EbmZones.Delta(center, kv.Key);
                int far = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.y));
                if (far <= NearOnlyRadiusZones && !_preloaded.ContainsKey(kv.Key) && !s_unusable.ContainsKey(kv.Key) && !(slim && ZoneTracker.IsTracked(kv.Key)) && s_nearLoading.TryAdd(kv.Key, 0))
                    s_nearScratch.Add(new KeyValuePair<int, Vector2s>(far, kv.Key));
            }
            if (s_nearScratch.Count > 1) s_nearScratch.Sort(s_nearestFirst);   // 1.2.389: nearest first
            for (int i = 0; i < s_nearScratch.Count; i++) s_nearQueue.Enqueue(s_nearScratch[i].Value);
            int release = NearOnlyRadiusZones + NearOnlyReleaseExtra;
            foreach (var kv in _preloaded)
            {
                var d = EbmZones.Delta(center, kv.Key);
                if ((Mathf.Abs(d.x) > release || Mathf.Abs(d.y) > release) && s_disk.ContainsKey(kv.Key)) _preloaded.TryRemove(kv.Key, out _);
            }
            StartNearWorker();
        }

        private static void StartNearWorker()
        {
            if (!s_nearQueue.IsEmpty && Interlocked.CompareExchange(ref s_nearWorker, 1, 0) == 0)
            {
                long uid = _preloadWorldUid; int generation = _preloadGeneration;
                Task.Run(() => NearWorker(uid, generation));
            }
        }

        // 1.2.389 [Cache] PreloadSlim, main thread, every pump: a tracked, baked zone needs its preload copy only for its next save, and Save already reads a zone's
        // own file when the arrays are not held (DiskBatch, the [Cache] ReleaseBakedCpuCopies path). Dropped under the same conditions as that key frees the mesh
        // copies: not stale (the file holds this bake), a file on disk, saved SlimIdleSeconds ago or not in this session, no sliced save still reading it.
        private static void SlimVisit(Vector2s coord, MeshBaker.BakeResult bake, bool stale)
        {
            if (stale || !s_disk.ContainsKey(coord) || SavedAgo(coord) < SlimIdleSeconds || SavePending(coord)) return;
            if (_preloaded.TryRemove(coord, out var gone)) { s_slimDropped++; s_slimDroppedBytes += ZoneDataBytes(gone); }
        }

        /// <summary>1.2.389: for the [Heap] census line.</summary>
        internal static string SlimSummary()
        {
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return $"preload hold: {(SlimOn ? "slim" : PreloadNearOnlyOn ? "near only" : "whole world")}, {_preloaded.Count} of {s_disk.Count} saved zone(s) in memory, {s_unusable.Count} file(s) not usable under these settings; slim dropped {s_slimDropped} built zone(s) ({s_slimDroppedBytes >> 20} MB), read on demand {s_slimSyncLoads} ({s_slimSyncTicks * ms:0.0} ms)";
        }

        private static void NearWorker(long worldUid, int generation)
        {
            try
            {
                byte[] buffer = s_nearBuffer; int removed = 0;   // 1.2.390: the last run's buffer (was a new one per run)
                while (generation == _preloadGeneration && s_nearQueue.TryDequeue(out var coord))
                {
                    var path = Path.Combine(_cacheRoot, _preloadFolder, $"zone_{coord.x}_{coord.y}.bin");   // never ZNet here: the folder was fixed on the main thread
                    LoadOne(path, worldUid, coord, generation, ref buffer, ref removed, true);   // 1.2.389: after the preload, the disk table decides (see LoadOne)
                    s_nearLoading.TryRemove(coord, out _);
                }
                s_nearBuffer = buffer != null && buffer.Length <= KeepSaveBufferBytes ? buffer : null;
            }
            catch (Exception ex) { EasyBakeLog.Warn($"[Cache] near-only preload stopped: {ex.GetType().Name}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref s_nearWorker, 0); }
        }

        // Intermediate off-thread representation of one cached zone. Holds
        // arrays only â€” no Unity Mesh/GameObject refs. The Construct step
        // turns this into a MeshBaker.BakeResult on the main thread.
        internal class CachedZoneData
        {
            public Vector2s Coord;
            public int BakeSettings;   // 1.2.168
            public int[] PrefabPrints;  // 1.2.255: prefab hash, print, prefab hash, print...
            public HashSet<MeshBaker.PieceIdentity> ContributorIdentities;
            public Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> PieceTransforms;
            public List<CachedBatchData> Batches;
            public List<CachedBatchData> FarBatches;
            public List<CachedInstanceGroupData> InstanceGroups;
        }

        internal class CachedInstanceGroupData
        {
            public int PrefabHash;
            public byte Look;
            public Matrix4x4[] Matrices;
            // Quantised piece positions, parallel to Matrices, so a restored group can
            // still drop an individual piece without rebaking the zone.
            public int[] PositionsX;
            public int[] PositionsY;
            public int[] PositionsZ;
            public int[] PositionsR;
        }

        internal class CachedBatchData
        {
            public string MaterialName;
            public int SourceMeshId;      // 1.2.253: the live mesh these arrays are a copy of, or 0; baked meshes are never edited after they are built
            public Vector3[] Vertices;
            public Vector3[] Normals;     // may be null
            public Vector4[] Tangents;    // may be null
            public Vector2[] Uvs;         // may be null
            public int[] Indices;
            public bool IndexFormatUInt32;
            public byte ShadowCastingMode;
            public byte ReceiveShadows;
            public byte LightProbeUsage;
            public byte ReflectionProbeUsage;
            public byte MotionVectorMode;
        }

        internal static string BaseDir;   // 1.2.100: the folder holding cache\ and prewarm_slow_skips.txt

        public static void Initialize()
        {
            // 1.2.100 (Fire 10-14: no caches under BepInEx config): BepInEx/cache/FiresEasyBakeMeshes, moved once from config.
            BaseDir = FiresCachePaths.Migrate(Path.Combine(Paths.ConfigPath, "FiresEasyBakeMeshes"), FiresCachePaths.For("FiresEasyBakeMeshes"),
                m => EasyBakeLog.Debug("[FiresEasyBakeMeshes] " + m), m => UnityEngine.Debug.LogWarning("[FiresEasyBakeMeshes] " + m));
            _cacheRoot = Path.Combine(BaseDir, "cache");
            EasyBakeLog.Debug($"[FiresEasyBakeMeshes] mesh cache at {_cacheRoot}");
        }

        // True iff a per-world cache directory exists on disk. Used by the
        // ZNetScene.Awake postfix to decide whether the material registry needs
        // to be built at all â€” the registry's sole consumer is cached zone
        // reattach (ConstructFromCache â†’ FindMaterial). No cache directory =>
        // no possible reattach => no need to spend ~50 main-thread-seconds
        // walking 3600+ prefabs to index materials we'll never look up.
        public static bool WorldHasCache(long worldUid)
        {
            if (_cacheRoot == null || worldUid == 0L) return false;
            try
            {
                string folder = WorldFolder(worldUid);
                return folder != null && Directory.Exists(Path.Combine(_cacheRoot, folder));
            }
            catch { return false; }
        }

        // Synchronous entry point â€” preserved for callers that need the
        // registry immediately. Internally this is now a thin wrapper that
        // drains the coroutine in one frame. Prefer BuildMaterialRegistryAsync
        // when running on the main thread during world load.
        public static void BuildMaterialRegistry(ZNetScene scene)
        {
            if (_materialsBuilt) return;
            if (scene == null || scene.m_prefabs == null) { _materialsBuilt = true; return; }

            var enumerator = BuildMaterialRegistryAsync(scene, runSynchronously: true);
            while (enumerator.MoveNext()) { /* drain â€” no yields when synchronous */ }
        }

        // Time-budgeted coroutine. Walks every prefab's MeshRenderers and
        // indexes sharedMaterials by Material.name. Yields when the per-frame
        // budget (FiresEasyBakeMeshesPlugin.MaterialRegistryFrameBudgetMs) is
        // exhausted so the main thread stays responsive â€” the password dialog
        // and loading screen don't freeze during the walk.
        //
        // Sync mode (runSynchronously=true): never yields, runs to completion
        // in the calling frame. Used by the synchronous BuildMaterialRegistry
        // wrapper above for any caller that needs the registry built before
        // it can continue. The async-yield mode is the path the ZNetScene.Awake
        // postfix kicks via StartCoroutine.
        public static System.Collections.IEnumerator BuildMaterialRegistryAsync(
            ZNetScene scene, bool runSynchronously = false)
        {
            if (_materialsBuilt) yield break;
            _materialsBuilt = true;
            if (scene == null || scene.m_prefabs == null) yield break;

            float budgetMs = FiresEasyBakeMeshesPlugin.MaterialRegistryFrameBudgetMs != null
                ? FiresEasyBakeMeshesPlugin.MaterialRegistryFrameBudgetMs.Value * PerformanceProfile.Budget   // 1.2.135
                : 4f;
            long budgetTicks = (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var wall = System.Diagnostics.Stopwatch.StartNew();

            int prefabCount = 0;
            for (int i = 0; i < scene.m_prefabs.Count; i++)
            {
                var prefab = scene.m_prefabs[i];
                if (prefab == null) continue;
                prefabCount++;

                IndexRendererMaterials(prefab);

                if (!runSynchronously && sw.ElapsedTicks >= budgetTicks)
                {
                    sw.Restart();
                    yield return null;
                }
            }
            wall.Stop();
            EasyBakeLog.Debug(
                $"[Cache] Material registry built: {_materialsByName.Count} unique materials " +
                $"across {prefabCount} prefabs in {wall.Elapsed.TotalSeconds:F2}s " +
                $"({(runSynchronously ? "sync" : "yielded")}).");
        }

        public static Material FindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_materialsByName.TryGetValue(name, out var mat)) return mat;
            // 1.2.320 ([lead] 11-11: the cache key must not fail on what other mods change at runtime): the exact key (name / shader / main texture /
            // look) missed. Another mod may have swapped the shader in place (FTS FiresPiece, FiresPieceShaderLoader.cs:194) or tuned a colour / gloss,
            // which changes the key but not the look's identity. Fall back to the IDENTITY part only, name + main texture, and draw with that live
            // material (the registry holds the prefab's own shared material, as it is NOW). Only when exactly ONE live material has that name + texture:
            // VA bundles carry several materials of one name (1.2.168), so an ambiguous identity still misses and the zone bakes fresh as before.
            string id = IdentityOfKey(name);
            if (id != null && _materialsById.TryGetValue(id, out mat) && mat != null && !_ambiguousIds.Contains(id)) { s_idFallbacks++; if (s_firstIdFallback == null) s_firstIdFallback = name.Replace(MaterialKeySeparator, '/') + " -> live '" + MaterialKey(mat).Replace(MaterialKeySeparator, '/') + "'"; return mat; }
            return null;
        }

        // 1.2.320: name + main texture, the identity a runtime shader / colour swap leaves alone
        private static readonly Dictionary<string, Material> _materialsById = new Dictionary<string, Material>();
        private static readonly HashSet<string> _ambiguousIds = new HashSet<string>();
        internal static int s_idFallbacks; internal static string s_firstIdFallback;
        private static string IdentityOf(Material mat)
        {
            Texture main = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            return StripInstance(mat.name) + MaterialKeySeparator + (main != null ? main.name : "");
        }
        private static string IdentityOfKey(string key)
        {
            var parts = key.Split(MaterialKeySeparator);
            return parts.Length >= 3 ? StripInstance(parts[0]) + MaterialKeySeparator + parts[2] : null;   // key = name / shader / main texture / look
        }
        private static string StripInstance(string n) => n != null && n.EndsWith(" (Instance)", StringComparison.Ordinal) ? n.Substring(0, n.Length - 11) : n;

        // 1.2.321: returns how many non-null materials it saw (0 = a renderer-less stub; the caller does not mark it indexed)
        internal static int s_emptyPrefabs;
        private static int IndexRendererMaterials(GameObject root)
        {
            int seen = 0;
            var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int r = 0; r < renderers.Length; r++)
            {
                var mats = renderers[r].sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                    if (mats[m] != null) { RegisterMaterial(mats[m]); seen++; }
            }
            // 1.2.88: a bake records the shared variation material a MaterialVariation(World) copy came from; register those too.
            foreach (var mat in MaterialVariants.VariationMaterials(root))
                if (mat != null) { RegisterMaterial(mat); seen++; }
            return seen;
        }

        // 1.2.374 ([visual]): ZdoCombine names a batch's material from the prefab ASSET (no live batch exists); registered so FindMaterial resolves it.
        internal static void RegisterAssetMaterial(Material mat) => RegisterMaterial(mat);

        private static void RegisterMaterial(Material mat)
        {
            if (mat == null || string.IsNullOrEmpty(mat.name)) return;
            string key = MaterialKey(mat);
            if (!_materialsByName.ContainsKey(key))
                _materialsByName[key] = mat;
            // 1.2.320: the identity index; two DIFFERENT materials with one identity make it ambiguous (never used for a fallback)
            string id = IdentityOf(mat);
            if (_materialsById.TryGetValue(id, out var had)) { if (!ReferenceEquals(had, mat)) _ambiguousIds.Add(id); }
            else _materialsById[id] = mat;
        }

        // 1.2.85 ([ghost]: bundle material names collide across mods - "Wood", "Material", "default" - and a FAP mock slot
        // carries vanilla's name because it IS vanilla's): a cached batch names its material by name, shader and main
        // texture, so two different looks that share a name never stand in for each other.
        internal const char MaterialKeySeparator = '\u001f';

        internal static string MaterialKey(Material mat) => MaterialKey(mat, mat != null ? mat.name : null);

        // The key mat would have under another name (MaterialVariants looks up an "(Instance)" copy's source by its stripped name).
        internal static string MaterialKey(Material mat, string name)
        {
            if (mat == null) return "";
            Texture main = mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null;
            // 1.2.168: VA bundles carry several materials of one name (stone_mat_Copy x3 in one log); name, shader and main texture alone gave a cached batch the first one registered.
            // _Cutoff stays out: FTS changes it in the swap.
            // 1.2.170: typed checks. HasProperty is true for any type, and GetFloat on a non-float logs an error each call (PolySoft/Mining_tint_Shader _Metallic, 130 a session).
            Texture bump = mat.HasTexture("_BumpMap") ? mat.GetTexture("_BumpMap") : null;
            string look = (bump != null ? bump.name : "")
                + (mat.HasColor("_Color") ? "|" + ColorUtility.ToHtmlStringRGBA(mat.GetColor("_Color")) : "")
                + (mat.HasFloat("_Glossiness") ? "|" + mat.GetFloat("_Glossiness").ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "")
                + (mat.HasFloat("_Metallic") ? "|" + mat.GetFloat("_Metallic").ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "");
            return name + MaterialKeySeparator + (mat.shader != null ? mat.shader.name : "") + MaterialKeySeparator + (main != null ? main.name : "") + MaterialKeySeparator + look;
        }

        // A cached zone's materials come from the prefabs of the pieces that fed it, so indexing those few prefabs
        // resolves the zone without waiting for the whole-scene registry, which is skipped on a world's first session
        // and still building early in later ones.
        private static void IndexContributorPrefabs(CachedZoneData data)
        {
            var scene = ZNetScene.instance;
            if (scene == null || data.ContributorIdentities == null) return;
            foreach (var identity in data.ContributorIdentities)
            {
                if (_indexedPrefabs.Contains(identity.PrefabHash)) continue;
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(identity.PrefabHash);
                if (prefab == null) continue;
                SnowFeed.SwapPrefabMaterials(prefab);   // 1.2.164: a cached batch's key names the shader, so swap before indexing
                // 1.2.321 ([saddles] / [generator] MATERIALS_FAP item F): a prefab that yields NO materials is not marked indexed. FAP's lazy bundles hand back
                // a renderer-less STUB while a realize fails (VALazyBundles.cs:256-261, "[LazyBundles] ... the stub stays"); marking it indexed then made every
                // zone holding it reject "missing material" for the whole session. Unmarked, the next zone check asks GetPrefab again and indexes the real one.
                if (IndexRendererMaterials(prefab) > 0) _indexedPrefabs.Add(identity.PrefabHash);
                else s_emptyPrefabs++;
            }
        }

        // 1.2.182 (lead 11-07: the warning gave only a hash): names the prefab and why it has no instancing definition.
        // 1.2.190 (lead 11-08, 0107w/x sweep: Building_VAGaziboRavensRest in zone (25,8), its material 'blackmarble' now sets _TRIPLANARMAP_ON
        // after the vapieces repack, so the modded-prefab gate refuses it and the old cache no longer matches): this is EBM healing itself. The
        // zone rebakes once, the new cache no longer lists the prefab, and nothing is lost. Players see warnings, so it is Debug every time.
        private static void SayUnresolved(Vector2s coord, InstanceKey key)
        {
            var prefab = ZNetScene.instance != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(key.PrefabHash) : null;
            EasyBakeLog.Debug($"[Cache] Zone ({coord.x},{coord.y}): instanced prefab '{(prefab != null ? prefab.name : "not in ZNetScene")}' ({key.PrefabHash}, {key.Look}) no longer resolves ({InstanceDefinitionCache.WhyNot(key)}); discarding cache so the zone rebakes (expected once after a mod changes that prefab; the new cache leaves it out).");
        }

        // 1.2.319 ([lead] 11-11 hand-over: 34 of 36 fresh bakes "missing material" with Core + FAP + FTS): names the first miss fully, so the log shows
        // WHAT differs. The cached key is name/shader/main texture/look; the same-named material on a contributor prefab is looked up live and its key
        // printed beside it: the shader part differing = a shader swap (FTS FiresPiece), the texture part = a lazy / swapped bundle (FAP), the look part =
        // a colour / gloss change, and no prefab carrying the name = the prefab's materials were replaced outright. Once per session (the first miss).
        private static string DescribeMissingMaterial(CachedZoneData data, string cachedKey)
        {
            try
            {
                string name = cachedKey; int cut = name.IndexOf('/'); if (cut >= 0) name = name.Substring(0, cut);
                if (data.ContributorIdentities != null && ZNetScene.instance != null)
                    foreach (var id in data.ContributorIdentities)
                    {
                        var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(id.PrefabHash);
                        if (prefab == null) continue;
                        foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
                            foreach (var m in r.sharedMaterials)
                                if (m != null && m.name == name)
                                    return $"'{cachedKey}' on '{prefab.name}', live key '{MaterialKey(m).Replace(MaterialKeySeparator, '/')}', zone ({data.Coord.x},{data.Coord.y})";
                    }
                return $"'{cachedKey}', zone ({data.Coord.x},{data.Coord.y}); no contributor prefab carries a material named '{name}' now";
            }
            catch (System.Exception e) { return $"'{cachedKey}' (prefab lookup threw: {e.Message})"; }
        }

        private static string FirstMissingMaterial(List<CachedBatchData> batches)
        {
            if (batches == null) return null;
            for (int i = 0; i < batches.Count; i++)
                if (FindMaterial(batches[i].MaterialName) == null) return batches[i].MaterialName.Replace(MaterialKeySeparator, '/');
            return null;
        }

        // Vanilla ZNet.GetWorldUID() does `return ZNet.m_world.m_uid;` with no
        // null-check (assembly_valheim/ZNet.cs:1418). m_world is null between
        // ZNet.instance being assigned (early in connection) and the server
        // sending its world handshake (~6s later). Calling GetWorldUID in
        // that window NREs every frame.
        //
        // Pre-check the private static m_world field via cached reflection so
        // we fail-soft without throwing. Once m_world is set, the normal
        // GetWorldUID call is safe.
        private static readonly System.Reflection.FieldInfo s_zNetWorldField =
            typeof(ZNet).GetField("m_world",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        public static long TryGetWorldUid()
        {
            if (ZNet.instance == null) return 0L;
            if (s_zNetWorldField == null) return 0L;
            if (s_zNetWorldField.GetValue(null) == null) return 0L;
            return ZNet.instance.GetWorldUID();
        }

        // --- Preload (background) ----------------------------------------------

        // Runs once per world. A relog into the same world keeps what is already in memory, which Save keeps current;
        // joining a different world drops the previous world's zones and reads the new world's cache.
        // 1.2.156: lets the per-frame caller stop once the preload for this world really started
        internal static bool PreloadKickedFor(long worldUid) => _preloadKicked && worldUid == _preloadWorldUid && WorldFolder(worldUid) == _preloadFolder;

        public static void KickPreload(long worldUid)
        {
            if (worldUid == 0L || _cacheRoot == null) return;
            if (s_coldSuffix != null && _preloadWorldUid != 0L && worldUid != _preloadWorldUid) s_coldSuffix = null;   // 1.2.382: another world never inherits ebm_coldcache
            string folder = WorldFolder(worldUid);
            if (folder == null) return;
            if (_preloadKicked && worldUid == _preloadWorldUid && folder == _preloadFolder) return;
            if (_preloadKicked && !_preloadFinished) return;

            _preloadGeneration++;
            _preloaded.Clear();
            _preloadZonesLoaded = 0;
            _preloadZonesFailed = 0;
            _preloadFinished = false;
            _preloadKicked = true;
            _preloadWorldUid = worldUid;
            _preloadFolder = folder;
            if (!s_saidFolder) { s_saidFolder = true; EasyBakeLog.Info($"[Cache] saved zones for this session are in folder {folder}"); }
            s_disk.Clear(); s_unusable.Clear(); Interlocked.Exchange(ref s_diskBytes, 0L); s_touched.Clear(); s_nearLoading.Clear();
            while (s_nearQueue.TryDequeue(out _)) { }

            var dir = Path.Combine(_cacheRoot, folder);
            try { if (Directory.Exists(dir)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow); } catch { }   // the all-worlds LRU stamp
            if (!s_coldNoTrim && s_coldSuffix == null) Task.Run(() => TrimOtherWorlds(folder));   // 1.2.382: never while ebm_coldcache switches or is on (the real folder must not be evicted as another world)
            if (!Directory.Exists(dir))
            {
                _preloadFinished = true;
                EasyBakeLog.Debug($"[Cache] No on-disk cache directory for world {worldUid:x16}; skipping preload.");
                return;
            }

            // Background Task. No Unity API allowed in here.
            int generation = _preloadGeneration;
            Task.Run(() => PreloadWorker(worldUid, dir, generation));
            EasyBakeLog.Debug($"[Cache] Preload started for world {worldUid:x16}.");
        }

        private static void PreloadWorker(long worldUid, string dir, int generation)
        {
            int unreadableRemoved = 0;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                FileInfo[] files;
                try { files = new DirectoryInfo(dir).GetFiles("zone_*.bin"); }   // the one listing of this world's folder; Save and Delete keep the table from here on
                catch (Exception ex)
                {
                    EasyBakeLog.Warn($"[Cache] Preload directory scan failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                // Newest first, so the world cap keeps the zones used most recently and an over-cap world is never parsed into RAM.
                Array.Sort(files, (x, y) => y.LastWriteTimeUtc.CompareTo(x.LastWriteTimeUtc));
                long cap = WorldCapBytes, kept = 0;
                byte[] buffer = null;
                bool nearOnly = PreloadNearOnlyOn || SlimOn;   // 1.2.389: PreloadSlim lists the files and reads none here, as PreloadNearOnly
                if (nearOnly) s_heldNear = true;
                if (PreloadNearOnlyOn && !s_saidNearOnly) { s_saidNearOnly = true; EasyBakeLog.Info("[Cache] PreloadNearOnly on: only zones near you are held in memory; a far zone reached quickly may rebuild from its live pieces that visit"); }
                for (int i = 0; i < files.Length; i++)
                {
                    if (generation != _preloadGeneration) return;
                    var info = files[i];
                    var file = info.FullName;
                    Vector2s coord;
                    if (!TryParseCoord(Path.GetFileNameWithoutExtension(file), out coord))
                    {
                        _preloadZonesFailed++;
                        continue;
                    }
                    if (s_touched.ContainsKey(coord)) continue;   // saved or deleted since the preload began (D4): the newer state stands
                    if (cap > 0L && kept + info.Length > cap)
                    {
                        SayCaps();
                        try
                        {
                            File.Delete(file);
                            Interlocked.Increment(ref s_evicted);
                            Interlocked.Add(ref s_bytesEvicted, info.Length);
                        }
                        catch (Exception ex) { EasyBakeLog.Debug($"[Cache] could not remove {info.Name} over the world cap: {ex.Message}"); }
                        continue;
                    }
                    kept += info.Length;
                    long stamp = info.LastWriteTimeUtc.Ticks;
                    if (s_disk.TryAdd(coord, new DiskEntry { Bytes = info.Length, LastUse = stamp, Stamped = stamp })) Interlocked.Add(ref s_diskBytes, info.Length);
                    if (!nearOnly) LoadOne(file, worldUid, coord, generation, ref buffer, ref unreadableRemoved);
                }

                sw.Stop();
                EasyBakeLog.Debug(
                    $"[Cache] Preload finished in {sw.Elapsed.TotalSeconds:F2}s: " +
                    $"{_preloadZonesLoaded} zones loaded, {_preloadZonesFailed} failed" +
                    (unreadableRemoved > 0 ? $"; removed {unreadableRemoved} unreadable cache file(s), those zones bake fresh." : "."));
            }
            finally
            {
                if (generation == _preloadGeneration) _preloadFinished = true;
            }
        }

        // Worker threads only: one zone file, read whole (the buffer is reused for the whole pass), checked, parsed and published. No Unity API.
        // 1.2.389 afterPreload (the near worker, TryGetOrLoad): s_touched only guards the world-load pass (a zone saved in its first seconds stayed "touched" for the
        // session and could never be read again). After that pass a read is published only while the disk table still lists the zone, so a deleted or evicted
        // zone's old file cannot come back; a zone saved meanwhile is already in memory (TryAdd then loses).
        private static void LoadOne(string file, long worldUid, Vector2s coord, int generation, ref byte[] buffer, ref int unreadableRemoved, bool afterPreload = false)
        {
            try
            {
                int length;
                using (var fs = File.OpenRead(file))
                {
                    if (fs.Length > MaxZoneFileBytes) throw new InvalidDataException($"{BadSavedData}{fs.Length >> 20} MB is past any real zone");
                    length = (int)fs.Length;
                    if (buffer == null || buffer.Length < length) buffer = new byte[Math.Max(length, 1 << 20)];
                    int got = 0;
                    while (got < length) { int n = fs.Read(buffer, got, length - got); if (n <= 0) throw new EndOfStreamException("short read"); got += n; }
                }
                Interlocked.Add(ref s_bytesRead, length);
                using (var ms = new MemoryStream(buffer, 0, length, false))
                using (var br = new BinaryReader(ms))
                {
                    var data = ReadCachedZoneData(br, worldUid, coord, buffer, length);
                    if (data == null) { _preloadZonesFailed++; if (!_preloaded.ContainsKey(coord)) s_unusable.TryAdd(coord, 0); return; }   // 1.2.390: not asked again until its file changes; never a zone saved meanwhile (a late read of the old file, [visual])
                    if (generation != _preloadGeneration || (afterPreload ? !s_disk.ContainsKey(coord) : s_touched.ContainsKey(coord))) return;
                    if (_preloaded.TryAdd(coord, data))
                    {
                        _preloadZonesLoaded++;
                        // A Delete between the test above and the add must still win; remove only this entry, so a newer Save stays.
                        if (afterPreload ? !s_disk.ContainsKey(coord) : s_touched.ContainsKey(coord)) ((ICollection<KeyValuePair<Vector2s, CachedZoneData>>)_preloaded).Remove(new KeyValuePair<Vector2s, CachedZoneData>(coord, data));
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException)
            {
                // Wrong format, version, coordinates or world, or truncated: it can never load, so remove it
                // rather than fail it again every login. The zone bakes fresh and saves a readable file.
                _preloadZonesFailed++;
                // 1.2.236: why, for each one: bad content is a warning, an old format only a debug line
                string rejected = $"[Cache] {Path.GetFileName(file)} rejected, the zone bakes fresh: {ex.Message}";
                if (ex.Message.StartsWith(BadSavedData, StringComparison.Ordinal)) { Interlocked.Increment(ref s_badFiles); EasyBakeLog.Warn(rejected); } else EasyBakeLog.Debug(rejected);
                try { File.Delete(file); unreadableRemoved++; NoteGone(coord); }
                catch (Exception deleteEx) { EasyBakeLog.Warn($"[Cache] Could not remove unreadable {Path.GetFileName(file)}: {deleteEx.Message}"); }
            }
            catch (Exception ex)
            {
                _preloadZonesFailed++;
                EasyBakeLog.Warn($"[Cache] Preload of {Path.GetFileName(file)} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Filename like "zone_-17_11.bin" â†’ Vector2s(-17, 11).
        private static bool TryParseCoord(string fileNameNoExt, out Vector2s coord)
        {
            coord = default;
            if (!fileNameNoExt.StartsWith("zone_")) return false;
            var parts = fileNameNoExt.Substring(5).Split('_');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], out var x)) return false;
            if (!int.TryParse(parts[1], out var y)) return false;
            coord = new Vector2s(x, y);
            return true;
        }

        public static bool TryGetPreloaded(Vector2s coord, out CachedZoneData data)
        {
            return _preloaded.TryGetValue(coord, out data);
        }

        // 1.2.389 [Cache] PreloadSlim: "this zone has a saved bake" without holding it. Key off = exactly TryGetPreloaded(coord, out _). A dictionary lookup either way.
        internal static bool HasSavedBake(Vector2s coord)
        {
            return _preloaded.ContainsKey(coord) || (SlimOn && _preloadFinished && s_disk.ContainsKey(coord) && !s_unusable.ContainsKey(coord));
        }

        // 1.2.389 [Cache] PreloadSlim, main thread: the zone is being built now. In memory = as TryGetPreloaded. Not in memory but on disk (the pump has not reached it
        // yet: a teleport) = its file is read here, a few ms, instead of baking the zone from its live pieces. Key off = exactly TryGetPreloaded.
        internal static bool TryGetOrLoad(Vector2s coord, out CachedZoneData data)
        {
            if (_preloaded.TryGetValue(coord, out data)) return true;
            if (!SlimOn || !_preloadFinished || _cacheRoot == null || _preloadFolder == null || !s_disk.ContainsKey(coord) || s_unusable.ContainsKey(coord)) return false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int removed = 0;
            LoadOne(Path.Combine(_cacheRoot, _preloadFolder, $"zone_{coord.x}_{coord.y}.bin"), _preloadWorldUid, coord, _preloadGeneration, ref s_syncBuffer, ref removed, true);
            if (s_syncBuffer != null && s_syncBuffer.Length > KeepSaveBufferBytes) s_syncBuffer = null;
            s_slimSyncLoads++; s_slimSyncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return _preloaded.TryGetValue(coord, out data);
        }

        // --- Construction (main thread) ----------------------------------------

        // Turns the intermediate arrays into a live BakeResult with real Unity
        // Mesh + GameObject instances. Cheap â€” just buffer copies into Unity's
        // mesh API; no disk I/O, no parsing.
        public static MeshBaker.BakeResult ConstructFromCache(CachedZoneData data)
        {
            if (data == null) return null;
            // 1.2.167: a zone can be instances only; requiring batches rebaked it every session
            if ((data.Batches == null || data.Batches.Count == 0) && (data.InstanceGroups == null || data.InstanceGroups.Count == 0)) return null;

            // 1.2.168: the preload of this world survives a relog, so a key checked only at read lets a retune serve old bakes
            if (data.BakeSettings != CurrentBakeSettingsKey())
            {
                _preloaded.TryRemove(data.Coord, out _);
                SayFingerprint(data.Coord, $"rebuilt (preloaded zone was baked under fingerprint {data.BakeSettings})");
                s_missOther++;
                s_missSettings++; EasyBakeLog.Debug($"[Cache] Zone ({data.Coord.x},{data.Coord.y}): baked under other settings; baking the zone fresh.");
                return null;
            }

            // 1.2.255 [Cache] RejectChangedPrefabs: a mod or game update changed a model this zone was baked from.
            if (RejectChangedPrefabsOn)
            {
                string changed = ChangedPrefab(data);
                if (changed != null)
                {
                    _preloaded.TryRemove(data.Coord, out _);
                    s_missPrint++;
                    if (s_firstPrint == null) s_firstPrint = changed;   // 1.2.317
                    if (!s_saidPrint) { s_saidPrint = true; EasyBakeLog.Info($"[Cache] RejectChangedPrefabs: a building model changed since its zone was saved (first: '{changed}'); zones using a changed model rebuild once"); }
                    EasyBakeLog.Debug($"[Cache] Zone ({data.Coord.x},{data.Coord.y}): prefab '{changed}' changed since this zone was baked; baking the zone fresh.");
                    return null;
                }
            }

            // Every piece in ContributorIdentities gets hidden on the strength of this cache, so a batch that cannot
            // be rebuilt would leave its pieces invisible. Rebuild all of them or none, and bake fresh otherwise.
            IndexContributorPrefabs(data);
            string missing = FirstMissingMaterial(data.Batches) ?? FirstMissingMaterial(data.FarBatches);
            if (missing != null)
            {
                _preloaded.TryRemove(data.Coord, out _);
                s_missOther++;
                // Debug since 1.2.88: nothing is lost, the zone bakes fresh from its live pieces and the cache file is rewritten.
                if (s_firstMaterial == null) s_firstMaterial = DescribeMissingMaterial(data, missing);   // 1.2.319 ([lead]: WHICH material)
                s_missMaterial++; EasyBakeLog.Debug($"[Cache] Zone ({data.Coord.x},{data.Coord.y}): cached material '{missing}' is not loaded; baking the zone fresh instead.");
                return null;
            }

            var result = new MeshBaker.BakeResult
            {
                // 1.2.167: copies. The live bake edits these as pieces come and go, and sharing them made the kept cache entry agree with geometry it does not hold
                ContributorIdentities = new HashSet<MeshBaker.PieceIdentity>(data.ContributorIdentities),
                PieceTransforms = data.PieceTransforms != null ? new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(data.PieceTransforms) : new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(),
            };

            // 1.2.167: before any mesh exists; a Destroy(parent) does not free meshes
            if (data.InstanceGroups != null)
                foreach (var check in data.InstanceGroups)
                {
                    var checkKey = new InstanceKey(check.PrefabHash, new WearLook(check.Look));
                    if (!InstanceDefinitionCache.TryGet(checkKey, out var checkDefinition))
                    {
                        s_missOther++;
                        SayUnresolved(data.Coord, checkKey);
                        return null;
                    }
                    // 1.2.236
                    string mismatch = SavedInstanceMismatch(data, check, checkDefinition);
                    if (mismatch != null && RejectChangedPrefabsOn)
                    {
                        _preloaded.TryRemove(data.Coord, out _);
                        s_missOffset++;
                        if (!s_saidOffset) { s_saidOffset = true; EasyBakeLog.Info("[Cache] RejectChangedPrefabs: a building model's pivot no longer matches its saved instances; zones like it rebuild once"); }
                        EasyBakeLog.Debug($"[Cache] Zone ({data.Coord.x},{data.Coord.y}): saved instances differ from today's prefab; baking the zone fresh: {mismatch}");
                        return null;
                    }
                    if (mismatch != null)
                    {
                        EasyBakeLog.Warn($"[Cache] Zone ({data.Coord.x},{data.Coord.y}) saved instances differ from today's prefab (kept and drawn, 1.2.238 only reports): {mismatch}");
                    }
                }

            var parent = new GameObject($"[EasyBake/Zone_{data.Coord.x}_{data.Coord.y}/cached]");
            parent.transform.position = Vector3.zero;
            parent.isStatic = true;
            result.Parent = parent;

            ConstructTier(data, data.Batches, parent, result.Batches, farTier: false);
            ConstructTier(data, data.FarBatches, parent, result.FarBatches, farTier: true);

            // Instanced groups must resolve completely. Their pieces get suppressed on
            // the strength of this cache's ContributorIdentities, so a group we cannot
            // rebuild would hide pieces with nothing drawing them. Reject the whole
            // cached zone instead and let it bake fresh.
            if (data.InstanceGroups != null)
            {
                for (int i = 0; i < data.InstanceGroups.Count; i++)
                {
                    var cachedGroup = data.InstanceGroups[i];
                    var key = new InstanceKey(cachedGroup.PrefabHash, new WearLook(cachedGroup.Look));
                    InstanceDefinition definition;
                    if (!InstanceDefinitionCache.TryGet(key, out definition))
                    {
                        SayUnresolved(data.Coord, key);
                        UnityEngine.Object.Destroy(parent);
                        return null;
                    }

                    var group = new ZoneInstanceGroup { PrefabHash = cachedGroup.PrefabHash, Look = key.Look, Definition = definition, FromCache = true };
                    for (int m = 0; m < cachedGroup.Matrices.Length; m++)
                    {
                        group.AddCached(cachedGroup.Matrices[m], new MeshBaker.PieceIdentity
                        {
                            PrefabHash = cachedGroup.PrefabHash,
                            X = cachedGroup.PositionsX[m],
                            Y = cachedGroup.PositionsY[m],
                            Z = cachedGroup.PositionsZ[m],
                            R = cachedGroup.PositionsR[m],
                        });
                    }
                    group.RecomputeBounds();
                    result.InstanceGroups.Add(group);
                }
            }

            if (!result.HasRenderableContent)
            {
                UnityEngine.Object.Destroy(parent);
                s_missOther++;
                return null;
            }
            SayFingerprint(data.Coord, "reused");
            s_hits++;
            NoteUsed(data.Coord);
            return result;
        }

        private static void ConstructTier(CachedZoneData data, List<CachedBatchData> cached, GameObject parent,
            List<MeshBaker.BatchInstance> into, bool farTier)
        {
            if (cached == null) return;
            for (int i = 0; i < cached.Count; i++) BuildLiveBatch(data.Coord, cached[i], parent, into, farTier);   // 1.2.374: the loop body moved to BuildLiveBatch, unchanged
        }

        // 1.2.374 ([visual], [lead]: the file-parity check for the ZDO full bake). Compares a LIVE bake's combined batches of one tier with the batches
        // ZdoCombine built for the same zone, matched by material key + renderer flags: same batches, same vertex and index counts. Main thread (it reads
        // the live renderers). Returns true when they are equal; `report` always says what was compared (for the verifier's line).
        internal static bool BatchParity(List<MeshBaker.BatchInstance> live, List<CachedBatchData> built, out string report)
        {
            var liveBy = new Dictionary<string, long[]>();   // key -> { vertices, indices, batches }
            if (live != null)
                for (int i = 0; i < live.Count; i++)
                {
                    var mesh = live[i].Mesh;
                    var mr = live[i].Combined != null ? live[i].Combined.GetComponent<MeshRenderer>() : null;
                    if (mesh == null || mr == null) continue;
                    string key = MaterialKey(mr.sharedMaterial) + "|" + (int)mr.shadowCastingMode + (mr.receiveShadows ? "r" : "-") + (int)mr.lightProbeUsage + (int)mr.reflectionProbeUsage + (int)mr.motionVectorGenerationMode;
                    if (!liveBy.TryGetValue(key, out var a)) liveBy[key] = a = new long[3];
                    a[0] += mesh.vertexCount; a[1] += (long)mesh.GetIndexCount(0); a[2]++;
                }
            var builtBy = new Dictionary<string, long[]>();
            if (built != null)
                for (int i = 0; i < built.Count; i++)
                {
                    var b = built[i];
                    if (b == null || b.Vertices == null) continue;
                    string key = b.MaterialName + "|" + (int)b.ShadowCastingMode + (b.ReceiveShadows != 0 ? "r" : "-") + (int)b.LightProbeUsage + (int)b.ReflectionProbeUsage + (int)b.MotionVectorMode;
                    if (!builtBy.TryGetValue(key, out var a)) builtBy[key] = a = new long[3];
                    a[0] += b.Vertices.Length; a[1] += b.Indices != null ? b.Indices.Length : 0; a[2]++;
                }
            int same = 0, countDiffer = 0, onlyLive = 0, onlyBuilt = 0;
            long liveVertices = 0, builtVertices = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in liveBy)
            {
                liveVertices += kv.Value[0];
                string name = kv.Key.Split(MaterialKeySeparator)[0];
                if (!builtBy.TryGetValue(kv.Key, out var b)) { onlyLive++; if (onlyLive + onlyBuilt + countDiffer <= 6) sb.Append($"; only live '{name}' ({kv.Value[0]} v)"); continue; }
                if (b[0] == kv.Value[0] && b[1] == kv.Value[1]) same++;
                else { countDiffer++; if (onlyLive + onlyBuilt + countDiffer <= 6) sb.Append($"; '{name}' live {kv.Value[0]} v / {kv.Value[1]} i vs built {b[0]} v / {b[1]} i"); }
            }
            foreach (var kv in builtBy)
            {
                builtVertices += kv.Value[0];
                if (liveBy.ContainsKey(kv.Key)) continue;
                onlyBuilt++; if (onlyLive + onlyBuilt + countDiffer <= 6) sb.Append($"; only built '{kv.Key.Split(MaterialKeySeparator)[0]}' ({kv.Value[0]} v)");
            }
            bool equal = countDiffer == 0 && onlyLive == 0 && onlyBuilt == 0;
            report = $"batches live {liveBy.Count} / built {builtBy.Count}: {same} equal, {countDiffer} differ in counts, {onlyLive} only live, {onlyBuilt} only built; vertices live {liveVertices} / built {builtVertices}{sb}";
            return equal;
        }

        // 1.2.375 ([visual], [lead] via [seasons]): the same comparison for a zone where the live bake MERGED pieces the ZDO file leaves live (bake-safe
        // prefabs that are not skippable). Their geometry (count x what one copy feeds each batch, read from the prefab by ZdoCombine.PartCounts) is taken
        // off the LIVE side first, so what is left must equal the built batches. A batch on one side only is reported apart: the zone-level
        // [Batching] MinPiecesPerBatch count is different once those pieces are out, so a batch can exist in the live bake alone (or the reverse)
        // without the combine being wrong. True = every batch both sides have is equal and nothing was over-subtracted.
        internal static bool BatchParity(List<MeshBaker.BatchInstance> live, List<CachedBatchData> built, List<ZdoCombine.LeftLive> leftLive, bool farTier, out string report)
        {
            var liveBy = new Dictionary<string, long[]>();
            if (live != null)
                for (int i = 0; i < live.Count; i++)
                {
                    var mesh = live[i].Mesh;
                    var mr = live[i].Combined != null ? live[i].Combined.GetComponent<MeshRenderer>() : null;
                    if (mesh == null || mr == null) continue;
                    string key = MaterialKey(mr.sharedMaterial) + "|" + (int)mr.shadowCastingMode + (mr.receiveShadows ? "r" : "-") + (int)mr.lightProbeUsage + (int)mr.reflectionProbeUsage + (int)mr.motionVectorGenerationMode;
                    if (!liveBy.TryGetValue(key, out var a)) liveBy[key] = a = new long[3];
                    a[0] += mesh.vertexCount; a[1] += (long)mesh.GetIndexCount(0); a[2]++;
                }
            int subtractedPieces = 0, subtractedNoBatch = 0;
            var parts = new List<KeyValuePair<string, long[]>>();
            if (leftLive != null)
                for (int n = 0; n < leftLive.Count; n++)
                {
                    parts.Clear();
                    ZdoCombine.PartCounts(leftLive[n].PrefabHash, leftLive[n].Look, farTier, parts);
                    subtractedPieces += leftLive[n].Count;
                    for (int k = 0; k < parts.Count; k++)
                    {
                        if (!liveBy.TryGetValue(parts[k].Key, out var a)) { subtractedNoBatch++; continue; }   // the live bake has no such batch: that piece's part was not merged there
                        a[0] -= parts[k].Value[0] * leftLive[n].Count; a[1] -= parts[k].Value[1] * leftLive[n].Count;
                    }
                }
            var builtBy = new Dictionary<string, long[]>();
            if (built != null)
                for (int i = 0; i < built.Count; i++)
                {
                    var b = built[i];
                    if (b == null || b.Vertices == null) continue;
                    string key = b.MaterialName + "|" + (int)b.ShadowCastingMode + (b.ReceiveShadows != 0 ? "r" : "-") + (int)b.LightProbeUsage + (int)b.ReflectionProbeUsage + (int)b.MotionVectorMode;
                    if (!builtBy.TryGetValue(key, out var a)) builtBy[key] = a = new long[3];
                    a[0] += b.Vertices.Length; a[1] += b.Indices != null ? b.Indices.Length : 0; a[2]++;
                }
            int same = 0, countDiffer = 0, onlyLive = 0, onlyBuilt = 0, emptied = 0, overSubtracted = 0, said = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var kv in liveBy)
            {
                string name = kv.Key.Split(MaterialKeySeparator)[0];
                bool has = builtBy.TryGetValue(kv.Key, out var b);
                if (kv.Value[0] < 0 || kv.Value[1] < 0) { overSubtracted++; if (said++ < 6) sb.Append($"; over-subtracted '{name}' ({kv.Value[0]} v left)"); continue; }
                if (kv.Value[0] == 0 && !has) { emptied++; continue; }   // the batch held only left-live pieces: nothing for the ZDO file to build
                if (!has) { onlyLive++; if (said++ < 6) sb.Append($"; only live '{name}' ({kv.Value[0]} v after subtraction)"); continue; }
                if (b[0] == kv.Value[0] && b[1] == kv.Value[1]) same++;
                else { countDiffer++; if (said++ < 6) sb.Append($"; '{name}' live {kv.Value[0]} v / {kv.Value[1]} i (after subtraction) vs built {b[0]} v / {b[1]} i"); }
            }
            foreach (var kv in builtBy)
            {
                if (liveBy.ContainsKey(kv.Key)) continue;
                onlyBuilt++; if (said++ < 6) sb.Append($"; only built '{kv.Key.Split(MaterialKeySeparator)[0]}' ({kv.Value[0]} v)");
            }
            report = $"batches live {liveBy.Count} / built {builtBy.Count} after taking {subtractedPieces} left-live piece(s) off the live side: {same} equal, {countDiffer} differ in counts, {overSubtracted} over-subtracted; batch on one side only (the MinPiecesPerBatch count differs without those pieces): {onlyLive} only live, {onlyBuilt} only built; {emptied} live batch(es) held left-live pieces only, {subtractedNoBatch} left-live part(s) had no live batch{sb}";
            return countDiffer == 0 && overSubtracted == 0;
        }

        // 1.2.374 ([visual] + [seasons]): ONE cached batch as a live batch object under `parent` (ConstructTier's loop body, unchanged), so a zone that
        // gets its combined batches while it is loaded (the ZDO full bake's this-visit swap) can build them one per step. False = its material is not known.
        internal static bool BuildLiveBatch(Vector2s coord, CachedBatchData b, GameObject parent, List<MeshBaker.BatchInstance> into, bool farTier)
        {
            var mat = FindMaterial(b.MaterialName);
            if (mat == null) return false;

            string batchName = b.MaterialName.Split(MaterialKeySeparator)[0] + (farTier ? MeshBaker.FarTierNameSuffix : string.Empty);
            var mesh = new Mesh { name = $"EasyBakeCached_{coord.x}_{coord.y}_{batchName}" };
            if (b.IndexFormatUInt32 || (b.Vertices != null && b.Vertices.Length > 65535)) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;   // 1.2.234: never 16-bit over 65,535 vertices, whatever the cache file says
            EbmTrace.Step("cache mesh build", mesh.GetInstanceID(), "zone " + coord.x + "," + coord.y + " " + batchName + " verts " + (b.Vertices != null ? b.Vertices.Length : 0));
            mesh.SetVertices(b.Vertices);
            if (b.Normals  != null) mesh.SetNormals(b.Normals);
            if (b.Tangents != null) mesh.SetTangents(b.Tangents);
            if (b.Uvs      != null) mesh.SetUVs(0, b.Uvs);
            mesh.SetIndices(b.Indices, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();

            var go = new GameObject($"Batch_{batchName}");
            go.transform.parent = parent.transform;
            go.transform.position = Vector3.zero;
            go.isStatic = true;
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode      = (UnityEngine.Rendering.ShadowCastingMode)b.ShadowCastingMode;
            mr.receiveShadows         = b.ReceiveShadows != 0;
            mr.lightProbeUsage        = (UnityEngine.Rendering.LightProbeUsage)b.LightProbeUsage;
            mr.reflectionProbeUsage   = (UnityEngine.Rendering.ReflectionProbeUsage)b.ReflectionProbeUsage;
            mr.motionVectorGenerationMode = (MotionVectorGenerationMode)b.MotionVectorMode;
            if (!EbmMeshCheck.Sane(mesh, "cache rebuild " + coord.x + "," + coord.y)) mr.forceRenderingOff = true;   // 1.2.234
            if (farTier) go.SetActive(false);

            b.SourceMeshId = mesh.GetInstanceID();   // 1.2.253: this mesh was built from these arrays
            into.Add(new MeshBaker.BatchInstance { Combined = go, Mesh = mesh });
            return true;
        }

        // --- Save (main thread; runs after a successful Bake) ------------------

        // True when the bytes were written or queued; false only when this save failed (ZoneTracker retries it once).
        // 1.2.374 ([visual] + [seasons], Fire 11-14): a zone's FULL file built from its ZDOs in the background (groups from ZoneTrackerZdoBake, combined
        // batches from ZdoCombine), with no live BakeResult to read. The same file and the same preload copy Save makes, written by the same writer thread.
        // The caller built `data` from a complete, settled classification of the zone and nothing edits it afterwards. False = not written (no cache folder).
        internal static bool SaveZdoBuilt(long worldUid, Vector2s coord, CachedZoneData data)
        {
            if (_cacheRoot == null || worldUid == 0L || data == null) return false;
            if ((data.Batches == null || data.Batches.Count == 0) && (data.InstanceGroups == null || data.InstanceGroups.Count == 0)) return false;
            var path = GetZonePath(worldUid, coord);
            if (path == null) return false;
            if (!_preloadFinished) s_touched.TryAdd(coord, 0);   // D4, as Save
            data.Coord = coord;
            _preloaded[coord] = data;
            long written = ZoneDataBytes(data);
            AsyncSaves.QueueData(path, worldUid, data);
            NoteDisk(coord, written);
            s_savedAt[coord] = Time.realtimeSinceStartup;
            EvictOverCap(worldUid);
            return true;
        }

        public static bool Save(long worldUid, Vector2s coord, MeshBaker.BakeResult bake)
        {
            if (_cacheRoot == null || worldUid == 0L) return true;
            if (bake == null || !bake.HasRenderableContent) return true;

            var path = GetZonePath(worldUid, coord);
            if (path == null) return true;

            // [seasons] 11-09 E25: a baked mesh whose CPU copy was released is read back from the preload copy or this zone's own file
            s_saveUid = worldUid; s_saveCoord = coord; s_saveBake = bake; s_diskCopy = null; s_diskTried = false;
            try
            {
                if (!_preloadFinished) s_touched.TryAdd(coord, 0);   // D4
                int[] prints = ZonePrints(bake);
                long written = -1L;   // -1 = [Cache] SaveSerializeOffThread: queued below from the preload copy
                // 1.2.253: with the key on, each mesh is read at most once per save, and not at all when the arrays already held came from that same mesh
                List<CachedBatchData> nearData = null, farData = null;
                if (SaveOffThreadOn)
                {
                    _preloaded.TryGetValue(coord, out CachedZoneData prior);
                    BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveRead);
                    nearData = ExtractBatchDataFromLive(bake.Batches, prior?.Batches);
                    farData = ExtractBatchDataFromLive(bake.FarBatches, prior?.FarBatches);
                    if (!SaveSerializeOffThreadOn)
                    {
                        BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveSerialise);
                        written = QueueSave(worldUid, coord, bake, path, nearData, farData, prints);
                    }
                }
                else
                {
                BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveWrite);
                AsyncSaves.FlushIfBusy();   // a write still queued from before the key went off must not land after this one
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (LowGarbageOn || EbmBakeMemRelease.AnyReleased(bake))   // E25: a released mesh can only be written from arrays read up front
                {
                    // the mesh arrays are read once here and used for both the file and the preload copy, as the off-thread path does (WriteBatchData writes the same bytes as WriteBatch)
                    _preloaded.TryGetValue(coord, out CachedZoneData heldNow);
                    BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveRead);
                    nearData = ExtractBatchDataFromLive(bake.Batches, heldNow?.Batches);
                    farData = ExtractBatchDataFromLive(bake.FarBatches, heldNow?.FarBatches);
                }
                BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveSerialise);
                var filled = FillSaveBuffer(worldUid, coord, bake, nearData, farData, prints);   // 1.2.255: in memory first, for the checksum
                BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveWrite);
                written = filled.Length;
                using (var fs = File.Create(path))
                    fs.Write(filled.GetBuffer(), 0, (int)filled.Length);
                if (filled.Capacity > KeepSaveBufferBytes) s_saveBuffer = null;
                }

                // Also refresh the preload entry so an immediate revisit of
                // this zone (without restarting the game) sees the new bake.
                BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveRefresh);
                RegisterLiveMaterials(bake.Batches);
                RegisterLiveMaterials(bake.FarBatches);
                var refreshed = new CachedZoneData
                {
                    Coord = coord,
                    BakeSettings = CurrentBakeSettingsKey(),
                    PrefabPrints = prints,
                    ContributorIdentities = new HashSet<MeshBaker.PieceIdentity>(bake.ContributorIdentities),
                    PieceTransforms = bake.PieceTransforms != null ? new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(bake.PieceTransforms) : null,
                    Batches = nearData ?? ExtractBatchDataFromLive(bake.Batches),
                    FarBatches = farData ?? ExtractBatchDataFromLive(bake.FarBatches),
                    InstanceGroups = ExtractInstanceGroupDataFromLive(bake.InstanceGroups),
                };
                _preloaded[coord] = refreshed;
                if (written < 0L)
                {
                    // 1.2.256 [Cache] SaveSerializeOffThread: the preload copy just made is a snapshot nothing edits (a zone built from it copies it), so the writer thread serialises from it.
                    if (!s_saidOffThread) { s_saidOffThread = true; EasyBakeLog.Info("[Cache] SaveOffThread + SaveSerializeOffThread on: zone cache files are serialised and written on a background thread and replaced atomically"); }
                    written = ZoneDataBytes(refreshed);
                    AsyncSaves.QueueData(path, worldUid, refreshed);
                }
                NoteDisk(coord, written);
                s_savedAt[coord] = Time.realtimeSinceStartup;   // E25: a release waits for the file to be older than its idle time
                EvictOverCap(worldUid);
                return true;
            }
            catch (ReleasedMeshUnreadable ex)
            {
                // E25: a released mesh's arrays could not be found again. The file may be older than the zone now: remove it, so the zone
                // bakes fresh on its next visit instead of loading an out-of-date copy. Nothing on screen changes (the GPU copy draws on).
                EasyBakeLog.Warn($"[Cache] zone ({coord.x},{coord.y}) not saved: {ex.Message}; its cache file is removed and it bakes fresh next visit");
                EbmBakeMemRelease.NoteRereadFailed();
                Delete(worldUid, coord);
                return false;
            }
            catch (Exception ex)
            {
                EasyBakeLog.Warn($"[Cache] Save failed for zone ({coord.x},{coord.y}): {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally { s_saveBake = null; s_diskCopy = null; }
        }

        // ── [seasons] 11-09 E25 (EbmBakeMemRelease): released CPU copies ────────────────────────────────────────────────────────────────
        private sealed class ReleasedMeshUnreadable : Exception { public ReleasedMeshUnreadable(string m) : base(m) { } }
        private static long s_saveUid; private static Vector2s s_saveCoord; private static MeshBaker.BakeResult s_saveBake;
        private static CachedZoneData s_diskCopy; private static bool s_diskTried;
        private static readonly Dictionary<Vector2s, float> s_savedAt = new Dictionary<Vector2s, float>();   // main thread

        internal static bool HasDiskFile(Vector2s coord) => s_disk.ContainsKey(coord);
        internal static bool DiskListingDone => _preloadFinished;   // 1.2.373 ([seasons]): HasDiskFile is a full answer only once the preload listing has run
        /// <summary>Seconds since this session last saved the zone; infinity when it has not (its file is from an earlier session or the preload).</summary>
        internal static float SavedAgo(Vector2s coord) => s_savedAt.TryGetValue(coord, out float t) ? Time.realtimeSinceStartup - t : float.PositiveInfinity;

        // The batch at file position `index` of this save's zone, from its own file on disk (read once per save). Its material key must match.
        // review 11-09: matched by position, material key AND the live mesh's vertex and index counts (readable or not, a mesh keeps both),
        // so a shifted position (a batch the reader dropped) between two batches of one material is caught, never silently used
        private static CachedBatchData DiskBatch(bool far, int index, string matName, int vertexCount, int indexCount)
        {
            if (!s_diskTried)
            {
                s_diskTried = true;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var path = GetZonePath(s_saveUid, s_saveCoord);
                if (path != null && File.Exists(path))
                {
                    using (EbmPatchMarkers.SaveDiskReread.Auto())   // 1.2.362: profiler marker only
                    {
                    byte[] bytes = File.ReadAllBytes(path);
                    using (var ms = new MemoryStream(bytes, 0, bytes.Length, false))
                    using (var br = new BinaryReader(ms))
                        s_diskCopy = ReadCachedZoneData(br, s_saveUid, s_saveCoord, bytes, bytes.Length);
                    }
                }
                EbmBakeMemRelease.NoteReread(sw.Elapsed.TotalMilliseconds);
            }
            var list = s_diskCopy == null ? null : (far ? s_diskCopy.FarBatches : s_diskCopy.Batches);
            if (list == null || index >= list.Count) return null;
            var b = list[index];
            return b != null && b.MaterialName == matName && b.Vertices != null && b.Indices != null
                && b.Vertices.Length == vertexCount && b.Indices.Length == indexCount ? b : null;
        }

        // Drops a zone's cache file AND its preloaded copy. Called when a
        // rebake produced no batches or a dirty zone tore down below the bake
        // threshold â€” leaving the old file would resurrect the removed pieces'
        // ghost geometry next session.
        public static void Delete(long worldUid, Vector2s coord)
        {
            try
            {
                DropPendingSave(coord, "its cache file is being deleted");   // 1.2.361: a pending sliced save must never write it back
                if (!_preloadFinished) s_touched.TryAdd(coord, 0);   // D4: before the remove, so the worker cannot bring it back
                NoteGone(coord);   // 1.2.389: the disk row first (LoadOne afterPreload)
                _preloaded.TryRemove(coord, out _);
                var path = GetZonePath(worldUid, coord);
                if (path != null && (SaveOffThreadOn || AsyncSaves.Busy)) { AsyncSaves.Queue(path, null); return; }   // 1.2.253: in order with any queued write of this zone
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                EasyBakeLog.Warn($"[Cache] Delete failed for zone ({coord.x},{coord.y}): {ex.Message}");
            }
        }

        // D7 (ebm_rebake all): every zone file of this world, tracked or not, through the writer so it stays in order with queued saves. A command, so the one listing is fine.
        internal static void DeleteWorld(long worldUid)
        {
            try
            {
                if (s_sliceJobs.Count > 0) { EasyBakeLog.Debug($"[Cache] {s_sliceJobs.Count} pending sliced save(s) dropped: the world's cache is being cleared"); s_sliceJobs.Clear(); }   // 1.2.361
                _preloadGeneration++;
                _preloadFinished = true;
                _preloaded.Clear();
                s_disk.Clear(); s_unusable.Clear(); Interlocked.Exchange(ref s_diskBytes, 0L);
                if (_cacheRoot == null || worldUid == 0L) return;
                string folder = WorldFolder(worldUid);
                if (folder == null) return;
                var dir = Path.Combine(_cacheRoot, folder);
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.GetFiles(dir, "zone_*.bin")) AsyncSaves.Queue(file, null);
            }
            catch (Exception ex) { EasyBakeLog.Warn($"[Cache] could not clear this world's cache: {ex.GetType().Name}: {ex.Message}"); }
        }

        // --- 1.2.253 [Cache] SaveOffThread -------------------------------------
        private const int KeepSaveBufferBytes = 8 * 1024 * 1024;
        private static MemoryStream s_saveBuffer;   // main thread only
        private static bool s_saidOffThread;
        private static long s_serializeTicks;

        private static bool SaveOffThreadOn => FiresEasyBakeMeshesPlugin.SaveOffThread != null && FiresEasyBakeMeshesPlugin.SaveOffThread.Value;

        internal static void FlushPendingSaves(int waitMs) => AsyncSaves.Flush(waitMs);

        // ═══ 1.2.361 [Cache] SaveSliced ([seasons] 11-13, [lead] / Fire: "make the reads, get the fixes in") ═══════════════════════════════════════════
        // Proof row: O town circle with 1.2.360's markers, (rig path), STALL FRAMES: "frame 250: 358.3 ms; top self: EBM bake:save
        // zone 342.4 ms", "frame 612: 174.4 ms; ... save zone 149.6 ms", "frame 721: 118.5 ms; ... save zone 111.1 ms" (the worst EBM step: 603 ms over 3 frames;
        // MeshBaker.Bake itself in no stall frame). With SaveOffThread + SaveSerializeOffThread on (the defaults) the file is already serialised and written on
        // the writer thread; Save's main-thread work is ExtractBatchDataFromLive: mesh.vertices / normals / tangents / uv / GetIndices of EVERY combined mesh
        // of the zone, near and far, in the bake frame. Here the same reads run one batch per step (ExtractOneBatch = the old loop body, same bytes, same
        // reuse of the preload copy, same E25 disk fallback), within SaveSliceBudgetMs a frame, at least one step a frame, from the zone tracker's pumps
        // (never inside the bake pass). The last step builds the preload copy and queues the write exactly as Save did, from one frame's consistent view.
        // Guards: MeshBaker.Restore finishes a pending save at once before it destroys the meshes (teardown, hidden release, world exit = as before);
        // Delete / DeleteWorld drop it (a deleted file must not come back); EbmBakeMemRelease skips a zone with a save pending. Off = Save as before.
        private sealed class SliceJob
        {
            public long Uid; public Vector2s Coord; public MeshBaker.BakeResult Bake; public string Path; public Action<bool> Done;
            public int Step; public CachedZoneData Prior; public List<CachedBatchData> Near, Far;
            public CachedZoneData DiskCopy; public bool DiskTried;
            public int Steps, Frames, LastFrame = -1; public double WorstMs, TotalMs; public float StartedAt;
        }
        private static readonly List<SliceJob> s_sliceJobs = new List<SliceJob>();
        private static bool s_saidSliced;
        private static int s_slicePumpFrame = -1;

        private static bool SaveSlicedOn => SaveOffThreadOn && SaveSerializeOffThreadOn && FiresEasyBakeMeshesPlugin.SaveSliced != null && FiresEasyBakeMeshesPlugin.SaveSliced.Value;

        internal static bool SavePending(Vector2s coord)
        {
            for (int i = 0; i < s_sliceJobs.Count; i++) if (s_sliceJobs[i].Coord.Equals(coord)) return true;
            return false;
        }

        /// <summary>Starts a sliced save; false = not taken (the caller saves at once, as before). `done(ok)` runs once, when the file is queued or the save failed.</summary>
        internal static bool TrySaveSliced(long worldUid, Vector2s coord, MeshBaker.BakeResult bake, Action<bool> done) => TrySaveSliced(worldUid, coord, bake, done, out _);

        // 1.2.363 ([visual] read of the 1.2.362 circle O_20261114_005404: "EBM bake:save zone" 377.9 / 237.9 / 45.5 ms SELF again while sliced saves also ran): the reason a
        // save was NOT taken, so SaveZone can say it (the code shows no path that should refuse with the rig's keys on; the next leg's log answers it).
        internal static bool TrySaveSliced(long worldUid, Vector2s coord, MeshBaker.BakeResult bake, Action<bool> done, out string notWhy)
        {
            notWhy = !SaveSlicedOn ? $"SaveSliced needs SaveOffThread ({SaveOffThreadOn}), SaveSerializeOffThread ({SaveSerializeOffThreadOn}) and SaveSliced ({FiresEasyBakeMeshesPlugin.SaveSliced != null && FiresEasyBakeMeshesPlugin.SaveSliced.Value}) on"
                   : _cacheRoot == null ? "no cache folder" : worldUid == 0L ? "no world uid" : bake == null ? "no bake" : !bake.HasRenderableContent ? "bake has no content" : null;
            if (notWhy != null) return false;
            var path = GetZonePath(worldUid, coord);
            if (path == null) { notWhy = "no zone path for this world"; return false; }
            for (int i = 0; i < s_sliceJobs.Count; i++)
            {
                var j = s_sliceJobs[i];
                if (!j.Coord.Equals(coord)) continue;
                if (ReferenceEquals(j.Bake, bake) && j.Uid == worldUid) { j.Done = done; return true; }   // this very bake is already being saved: one save, the latest caller told
                s_sliceJobs.RemoveAt(i);
                EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): a newer bake replaces its pending sliced save ({j.Steps} step(s) done); the new bake is saved instead");
                break;
            }
            if (!s_saidSliced)
            {
                s_saidSliced = true;
                EasyBakeLog.Info($"[Cache] SaveSliced on: a freshly baked zone's cache save reads its meshes one batch per step over the next frames, about {SliceBudgetMs():0.#} ms a frame (one save read them all at once: up to 342 ms in a single frame on the town circle); the zone draws from its bake meanwhile, only the file lands later");
            }
            s_sliceJobs.Add(new SliceJob { Uid = worldUid, Coord = coord, Bake = bake, Path = path, Done = done, StartedAt = Time.unscaledTime });
            return true;
        }

        private static float SliceBudgetMs() => Mathf.Max(0.1f, (FiresEasyBakeMeshesPlugin.SaveSliceBudgetMs != null ? FiresEasyBakeMeshesPlugin.SaveSliceBudgetMs.Value : 2f) * PerformanceProfile.Budget);

        /// <summary>Zone tracker pumps, once a frame: steps of the oldest pending save within the budget; at least one step a frame so a save always ends.</summary>
        internal static void PumpSlicedSaves()
        {
            if (s_sliceJobs.Count == 0 || s_slicePumpFrame == Time.frameCount) return;
            s_slicePumpFrame = Time.frameCount;
            long budget = (long)(SliceBudgetMs() * System.Diagnostics.Stopwatch.Frequency / 1000.0), start = System.Diagnostics.Stopwatch.GetTimestamp();
            bool first = true;
            while (s_sliceJobs.Count > 0 && (first || System.Diagnostics.Stopwatch.GetTimestamp() - start < budget))
            {
                first = false;
                RunSliceStep(s_sliceJobs[0]);
            }
        }

        /// <summary>MeshBaker.Restore, before it destroys a bake's meshes: a save still pending for that bake finishes now (the file it would have written, as before 1.2.361).</summary>
        internal static void FinishPendingFor(MeshBaker.BakeResult bake)
        {
            if (bake == null || s_sliceJobs.Count == 0) return;
            for (int i = 0; i < s_sliceJobs.Count; i++)
            {
                var job = s_sliceJobs[i];
                if (!ReferenceEquals(job.Bake, bake)) continue;
                EasyBakeLog.Debug($"[Cache] zone ({job.Coord.x},{job.Coord.y}): its bake is being torn down while its sliced save is pending ({job.Steps} step(s) done): the rest runs now, so the file is written as before");
                int guard = 0;
                while (s_sliceJobs.Contains(job) && guard++ < 100000) RunSliceStep(job);
                return;
            }
        }

        // Delete / DeleteWorld: a pending save of a file being removed must never write it back.
        internal static void DropPendingSave(Vector2s coord, string why)
        {
            for (int i = s_sliceJobs.Count - 1; i >= 0; i--)
            {
                var job = s_sliceJobs[i];
                if (!job.Coord.Equals(coord)) continue;
                s_sliceJobs.RemoveAt(i);
                EasyBakeLog.Debug($"[Cache] zone ({coord.x},{coord.y}): pending sliced save dropped ({why})");
            }
        }

        private static bool BakeMeshesAlive(MeshBaker.BakeResult bake)
        {
            if (bake.Batches != null) for (int i = 0; i < bake.Batches.Count; i++) if (bake.Batches[i].Mesh == null) return false;
            if (bake.FarBatches != null) for (int i = 0; i < bake.FarBatches.Count; i++) if (bake.FarBatches[i].Mesh == null) return false;
            return true;
        }

        // One step: 0 = start (preload copy to reuse arrays from), 1..near = one near batch, then one far batch per step, last = finish (Save's tail).
        private static void RunSliceStep(SliceJob job)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (job.LastFrame != Time.frameCount) { job.LastFrame = Time.frameCount; job.Frames++; }
            s_saveUid = job.Uid; s_saveCoord = job.Coord; s_saveBake = job.Bake; s_diskCopy = job.DiskCopy; s_diskTried = job.DiskTried;   // E25 disk fallback state, per job
            bool finished = false, ok = false; string why = null;
            try
            {
                var bake = job.Bake;
                int nNear = bake.Batches != null ? bake.Batches.Count : 0, nFar = bake.FarBatches != null ? bake.FarBatches.Count : 0;
                if (!BakeMeshesAlive(bake)) { finished = true; why = "a mesh of its bake was destroyed before the save finished (no partial file is written)"; }
                else if (job.Step == 0)
                {
                    if (!_preloadFinished) s_touched.TryAdd(job.Coord, 0);   // D4, as Save
                    _preloaded.TryGetValue(job.Coord, out job.Prior);
                    job.Near = new List<CachedBatchData>(nNear); job.Far = new List<CachedBatchData>(nFar);
                }
                else if (job.Step <= nNear) { using (EbmPatchMarkers.SaveStepBatch.Auto()) ExtractOneBatch(bake.Batches, job.Step - 1, job.Prior?.Batches, job.Near); }   // 1.2.362: markers only
                else if (job.Step <= nNear + nFar) { using (EbmPatchMarkers.SaveStepBatch.Auto()) ExtractOneBatch(bake.FarBatches, job.Step - 1 - nNear, job.Prior?.FarBatches, job.Far); }
                else { using (EbmPatchMarkers.SaveStepFinish.Auto()) FinishSlicedSave(job); finished = true; ok = true; }
                job.Step++;
            }
            catch (ReleasedMeshUnreadable ex)
            {
                // as Save: the file may be older than the zone now, so it goes and the zone bakes fresh next visit (nothing on screen changes)
                finished = true; why = "released mesh unreadable";
                s_sliceJobs.Remove(job);
                EasyBakeLog.Warn($"[Cache] zone ({job.Coord.x},{job.Coord.y}) not saved: {ex.Message}; its cache file is removed and it bakes fresh next visit");
                EbmBakeMemRelease.NoteRereadFailed();
                Delete(job.Uid, job.Coord);
            }
            catch (Exception ex)
            {
                finished = true; why = ex.GetType().Name + ": " + ex.Message;
                EasyBakeLog.Warn($"[Cache] Save failed for zone ({job.Coord.x},{job.Coord.y}) (sliced): {why}");
            }
            finally { job.DiskCopy = s_diskCopy; job.DiskTried = s_diskTried; s_saveBake = null; s_diskCopy = null; }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            job.Steps++; job.TotalMs += ms; if (ms > job.WorstMs) job.WorstMs = ms;
            if (!finished) return;
            s_sliceJobs.Remove(job);
            if (ok) EasyBakeLog.Debug($"[Cache] zone ({job.Coord.x},{job.Coord.y}) saved sliced: {job.Steps} step(s) over {job.Frames} frame(s) in {Time.unscaledTime - job.StartedAt:0.0} s, worst step {job.WorstMs:0.0} ms, main-thread total {job.TotalMs:0.0} ms");
            else if (why != null) EasyBakeLog.Debug($"[Cache] zone ({job.Coord.x},{job.Coord.y}) sliced save ended without a file: {why}");
            try { job.Done?.Invoke(ok); } catch (Exception e) { EasyBakeLog.Debug("[Cache] sliced save callback threw: " + e.Message); }
        }

        // Save's tail (the SaveOffThread + SaveSerializeOffThread branch), unchanged, from the arrays the steps read. Prints, contributors and instance groups
        // are taken here, in one frame, so the file matches the bake as it is now.
        private static void FinishSlicedSave(SliceJob job)
        {
            var bake = job.Bake; var coord = job.Coord;
            int[] prints = ZonePrints(bake);
            RegisterLiveMaterials(bake.Batches);
            RegisterLiveMaterials(bake.FarBatches);
            var refreshed = new CachedZoneData
            {
                Coord = coord,
                BakeSettings = CurrentBakeSettingsKey(),
                PrefabPrints = prints,
                ContributorIdentities = new HashSet<MeshBaker.PieceIdentity>(bake.ContributorIdentities),
                PieceTransforms = bake.PieceTransforms != null ? new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(bake.PieceTransforms) : null,
                Batches = job.Near,
                FarBatches = job.Far,
                InstanceGroups = ExtractInstanceGroupDataFromLive(bake.InstanceGroups),
            };
            _preloaded[coord] = refreshed;
            if (!s_saidOffThread) { s_saidOffThread = true; EasyBakeLog.Info("[Cache] SaveOffThread + SaveSerializeOffThread on: zone cache files are serialised and written on a background thread and replaced atomically"); }
            long written = ZoneDataBytes(refreshed);
            AsyncSaves.QueueData(job.Path, job.Uid, refreshed);
            NoteDisk(coord, written);
            s_savedAt[coord] = Time.realtimeSinceStartup;   // E25
            EvictOverCap(job.Uid);
        }
        internal static int AsyncSaveCount => AsyncSaves.Saves;

        // Null until an off-thread save has happened.
        internal static string AsyncSaveSummary()
        {
            int saves = AsyncSaves.Saves;
            if (saves == 0) return null;
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return $"off-thread saves: {saves}, {AsyncSaves.Bytes >> 10} KB, {s_serializeTicks * f:F0} ms serialising on the main thread, {AsyncSaves.WriteTicks * f:F0} ms writing in the background, {AsyncSaves.Pending} queued; mesh arrays reused {s_arraysReused}, read {s_arraysRead}";
        }

        private static int s_arraysReused, s_arraysRead;

        private static CachedBatchData SameMesh(List<CachedBatchData> held, int meshId)
        {
            if (held == null) return null;
            for (int i = 0; i < held.Count; i++)
                if (held[i].SourceMeshId == meshId && held[i].Vertices != null && held[i].Indices != null) return held[i];
            return null;
        }

        // 1.2.255 [Optimize] CacheLowGarbage: the same little-endian bytes as BinaryWriter, without Mono's new byte[4] per value. Everything not overridden (bytes, byte arrays) goes through the base, which writes straight to the same stream, so order holds.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }

        private sealed class LowGarbageWriter : BinaryWriter
        {
            private readonly byte[] m_bytes = new byte[8];
            public LowGarbageWriter(Stream stream) : base(stream) { }
            private void Put(int v)
            {
                m_bytes[0] = (byte)v; m_bytes[1] = (byte)(v >> 8); m_bytes[2] = (byte)(v >> 16); m_bytes[3] = (byte)(v >> 24);
                OutStream.Write(m_bytes, 0, 4);
            }
            public override void Write(int value) => Put(value);
            public override void Write(uint value) => Put(unchecked((int)value));
            public override void Write(float value) { var bits = new FloatBits { F = value }; Put(bits.I); }
            public override void Write(long value)
            {
                for (int i = 0; i < 8; i++) m_bytes[i] = (byte)(value >> (i * 8));
                OutStream.Write(m_bytes, 0, 8);
            }
        }

        // The whole file in the reused buffer, ending with the CRC-32 of everything before it (read back by ReadCachedZoneData).
        private static MemoryStream FillSaveBuffer(long worldUid, Vector2s coord, MeshBaker.BakeResult bake, List<CachedBatchData> nearData, List<CachedBatchData> farData, int[] prints)
        {
            var buffer = s_saveBuffer ?? (s_saveBuffer = new MemoryStream(1 << 16));
            buffer.SetLength(0);
            BinaryWriter bw = LowGarbageOn ? new LowGarbageWriter(buffer) : new BinaryWriter(buffer);   // not disposed: that would close the reused stream
            WriteBakeResult(bw, worldUid, coord, bake, prints, nearData, farData);
            bw.Flush();
            bw.Write(Far.FarCache.Crc32(buffer.GetBuffer(), (int)buffer.Length));
            bw.Flush();
            return buffer;
        }

        private static bool SaveSerializeOffThreadOn => FiresEasyBakeMeshesPlugin.SaveSerializeOffThread != null && FiresEasyBakeMeshesPlugin.SaveSerializeOffThread.Value;

        // The file's exact length, for the disk table (the bytes themselves appear on the writer thread).
        private static long ZoneDataBytes(CachedZoneData d)
        {
            long n = 28L + 4L + (d.PrefabPrints?.Length ?? 0) * 4L + 4L + (d.ContributorIdentities?.Count ?? 0) * 20L + 4L + (d.PieceTransforms?.Count ?? 0) * 61L + 4L + 4L + 4L + 4L;   // header 28: magic, version, settings, x, y, uid; then the four counts + prints count; CRC last
            n += BatchListBytes(d.Batches) + BatchListBytes(d.FarBatches);
            if (d.InstanceGroups != null) foreach (var g in d.InstanceGroups) n += 9L + g.Matrices.Length * 80L;
            return n;
        }

        private static long BatchListBytes(List<CachedBatchData> list)
        {
            long n = 0L;
            if (list != null)
                foreach (var b in list)
                    n += 19L + Encoding.UTF8.GetByteCount(b.MaterialName ?? "") + b.Vertices.Length * (12L + (b.Normals != null ? 12L : 0L) + (b.Tangents != null ? 16L : 0L) + (b.Uvs != null ? 8L : 0L)) + b.Indices.Length * 4L;
            return n;
        }

        // Writer thread. The same bytes, in the same order, as WriteBakeResult, from the snapshot. No Unity API (Matrix4x4's indexer is plain managed code).
        private static void WriteZoneData(BinaryWriter bw, long worldUid, CachedZoneData d)
        {
            bw.Write(MAGIC);
            bw.Write(VERSION);
            bw.Write(d.BakeSettings);
            bw.Write((int)d.Coord.x);
            bw.Write((int)d.Coord.y);
            bw.Write(worldUid);
            int[] prints = d.PrefabPrints ?? new int[0];
            bw.Write(prints.Length / 2);
            for (int p = 0; p < prints.Length; p++) bw.Write(prints[p]);
            bw.Write(d.ContributorIdentities?.Count ?? 0);
            if (d.ContributorIdentities != null)
                foreach (var id in d.ContributorIdentities) { bw.Write(id.PrefabHash); bw.Write(id.X); bw.Write(id.Y); bw.Write(id.Z); bw.Write(id.R); }
            bw.Write(d.PieceTransforms?.Count ?? 0);
            if (d.PieceTransforms != null)
                foreach (var entry in d.PieceTransforms)
                {
                    bw.Write(entry.Key.PrefabHash); bw.Write(entry.Key.X); bw.Write(entry.Key.Y); bw.Write(entry.Key.Z); bw.Write(entry.Key.R);
                    var t = entry.Value;
                    bw.Write(t.Position.x); bw.Write(t.Position.y); bw.Write(t.Position.z);
                    bw.Write(t.Rotation.x); bw.Write(t.Rotation.y); bw.Write(t.Rotation.z); bw.Write(t.Rotation.w);
                    bw.Write(t.Scale.x); bw.Write(t.Scale.y); bw.Write(t.Scale.z);
                    bw.Write(t.Look.Bits);
                }
            bw.Write(d.Batches.Count);
            for (int i = 0; i < d.Batches.Count; i++) WriteBatchData(bw, d.Batches[i]);
            bw.Write(d.FarBatches.Count);
            for (int i = 0; i < d.FarBatches.Count; i++) WriteBatchData(bw, d.FarBatches[i]);
            bw.Write(d.InstanceGroups.Count);
            foreach (var group in d.InstanceGroups)
            {
                bw.Write(group.PrefabHash);
                bw.Write(group.Look);
                bw.Write(group.Matrices.Length);
                for (int m = 0; m < group.Matrices.Length; m++)
                {
                    var matrix = group.Matrices[m];
                    for (int e = 0; e < 16; e++) bw.Write(matrix[e]);
                    bw.Write(group.PositionsX[m]); bw.Write(group.PositionsY[m]); bw.Write(group.PositionsZ[m]); bw.Write(group.PositionsR[m]);
                }
            }
        }

        private static long QueueSave(long worldUid, Vector2s coord, MeshBaker.BakeResult bake, string path, List<CachedBatchData> nearData, List<CachedBatchData> farData, int[] prints)
        {
            if (!s_saidOffThread) { s_saidOffThread = true; EasyBakeLog.Info("[Cache] SaveOffThread on: zone cache files are written on a background thread and replaced atomically"); }
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            var buffer = FillSaveBuffer(worldUid, coord, bake, nearData, farData, prints);
            int length = (int)buffer.Length;
            byte[] bytes = AsyncSaves.Rent(length);
            Buffer.BlockCopy(buffer.GetBuffer(), 0, bytes, 0, length);
            if (buffer.Capacity > KeepSaveBufferBytes) s_saveBuffer = null;
            s_serializeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            AsyncSaves.Queue(path, bytes, length);
            return length;
        }

        // One writer at a time and only the newest job per file, so an older write can never land after a newer one. The temporary file is flushed and then swapped
        // over the real one: a crash leaves the old file or the new one, never half of one. No Unity API in here.
        private static class AsyncSaves
        {
            private const long MaxPendingBytes = 128L * 1024 * 1024;
            private static readonly object s_lock = new object();
            private struct Job { public byte[] Bytes; public int Length; public CachedZoneData Data; public long Uid; }
            private static MemoryStream s_workerBuffer;   // the one writer thread only

            // 1.2.256: newest job per file as for bytes. The snapshot is the preload copy, already held, so it adds nothing to the pending-bytes bound.
            internal static void QueueData(string path, long worldUid, CachedZoneData data)
            {
                lock (s_lock)
                {
                    if (s_jobs.TryGetValue(path, out Job replaced) && replaced.Bytes != null) { s_pendingBytes -= replaced.Length; ReturnLocked(replaced.Bytes); }
                    s_jobs[path] = new Job { Data = data, Uid = worldUid };
                    if (!s_running) { s_running = true; Task.Run((Action)Pump); }
                }
            }

            private static void WriteData(string path, long worldUid, CachedZoneData data)
            {
                var buffer = s_workerBuffer ?? (s_workerBuffer = new MemoryStream(1 << 16));
                buffer.SetLength(0);
                BinaryWriter bw = LowGarbageOn ? new LowGarbageWriter(buffer) : new BinaryWriter(buffer);   // not disposed: that would close the reused stream
                WriteZoneData(bw, worldUid, data);
                bw.Flush();
                bw.Write(Far.FarCache.Crc32(buffer.GetBuffer(), (int)buffer.Length));
                bw.Flush();
                int length = (int)buffer.Length;
                WriteAtomic(path, buffer.GetBuffer(), length);
                Interlocked.Increment(ref s_saves);
                Interlocked.Add(ref s_bytes, length);
                if (buffer.Capacity > KeepSaveBufferBytes) s_workerBuffer = null;
            }
            private const int MaxPooledBuffers = 4;
            private static readonly List<byte[]> s_pool = new List<byte[]>();   // under s_lock
            private static readonly Dictionary<string, Job> s_jobs = new Dictionary<string, Job>(StringComparer.Ordinal);   // null bytes = delete the file

            internal static byte[] Rent(int length)
            {
                lock (s_lock)
                    for (int i = s_pool.Count - 1; i >= 0; i--)
                        if (s_pool[i].Length >= length) { byte[] found = s_pool[i]; s_pool.RemoveAt(i); return found; }
                return new byte[Math.Max(length + length / 4, 1 << 16)];
            }

            private static void ReturnLocked(byte[] bytes) { if (bytes.Length > 0 && bytes.Length <= KeepSaveBufferBytes && s_pool.Count < MaxPooledBuffers) s_pool.Add(bytes); }

            private static long s_pendingBytes, s_bytes, s_writeTicks;
            private static int s_saves;
            private static volatile bool s_running;

            internal static bool Busy => s_running;
            internal static int Saves => Volatile.Read(ref s_saves);
            internal static long Bytes => Interlocked.Read(ref s_bytes);
            internal static long WriteTicks => Interlocked.Read(ref s_writeTicks);
            internal static int Pending { get { lock (s_lock) return s_jobs.Count; } }

            internal static void Queue(string path, byte[] bytes, int length = 0)
            {
                bool overBound;
                lock (s_lock)
                {
                    if (s_jobs.TryGetValue(path, out Job replaced) && replaced.Bytes != null) { s_pendingBytes -= replaced.Length; ReturnLocked(replaced.Bytes); }
                    s_jobs[path] = new Job { Bytes = bytes, Length = length };
                    if (bytes != null) s_pendingBytes += length;
                    overBound = s_pendingBytes > MaxPendingBytes;
                    if (!s_running) { s_running = true; Task.Run((Action)Pump); }
                }
                if (overBound) Flush(2000);
            }

            // 1.2.255: the LRU stamp of a file just used. Never replaces a queued write or delete of the same file.
            private static readonly byte[] s_touch = new byte[0];
            internal static void QueueTouch(string path)
            {
                lock (s_lock)
                {
                    if (s_jobs.ContainsKey(path)) return;
                    s_jobs[path] = new Job { Bytes = s_touch, Length = 0 };
                    if (!s_running) { s_running = true; Task.Run((Action)Pump); }
                }
            }

            internal static void FlushIfBusy() { if (s_running) Flush(5000); }

            internal static void Flush(int waitMs)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                lock (s_lock)
                {
                    while (s_running)
                    {
                        int left = waitMs - (int)clock.ElapsedMilliseconds;
                        if (left <= 0) { EasyBakeLog.Warn($"[Cache] {s_jobs.Count} queued cache write(s) not finished after {waitMs} ms; their zones keep the previous file"); return; }
                        Monitor.Wait(s_lock, left);
                    }
                }
            }

            private static void Pump()
            {
                try
                {
                    for (;;)
                    {
                        string path = null; byte[] bytes = null; int length = 0; CachedZoneData data = null; long uid = 0L;
                        lock (s_lock)
                        {
                            foreach (var job in s_jobs) { path = job.Key; bytes = job.Value.Bytes; length = job.Value.Length; data = job.Value.Data; uid = job.Value.Uid; break; }
                            if (path == null) { s_running = false; Monitor.PulseAll(s_lock); return; }   // in the same lock as the empty check: a job queued after it starts a new writer
                            s_jobs.Remove(path);
                            if (bytes != null) s_pendingBytes -= length;
                        }
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            if (data != null) WriteData(path, uid, data);
                            else if (bytes == null) { if (File.Exists(path)) File.Delete(path); }
                            else if (ReferenceEquals(bytes, s_touch)) { if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
                            else
                            {
                                WriteAtomic(path, bytes, length);
                                Interlocked.Increment(ref s_saves);
                                Interlocked.Add(ref s_bytes, length);
                            }
                        }
                        catch (Exception ex) { EasyBakeLog.Warn($"[Cache] background write of {Path.GetFileName(path)} failed: {ex.GetType().Name}: {ex.Message}"); }
                        Interlocked.Add(ref s_writeTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
                        if (bytes != null) lock (s_lock) ReturnLocked(bytes);
                    }
                }
                catch (Exception fatal)
                {
                    EasyBakeLog.Warn($"[Cache] the background cache writer stopped: {fatal.GetType().Name}: {fatal.Message}");
                    lock (s_lock) { s_running = false; Monitor.PulseAll(s_lock); }
                }
            }

            private static void WriteAtomic(string path, byte[] bytes, int length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";   // the preload's zone_*.bin scan does not pick this up
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, length);
                    fs.Flush(true);
                }
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException) when (attempt < 3) { Thread.Sleep(20); }   // a reader or a virus scanner holding the file for a moment
                }
            }
        }

        private static string GetZonePath(long worldUid, Vector2s coord)
        {
            if (_cacheRoot == null) return null;
            string folder = WorldFolder(worldUid);
            return folder == null ? null : Path.Combine(_cacheRoot, folder, $"zone_{coord.x}_{coord.y}.bin");
        }

        // After a fresh save, mirror the live mesh into the preloaded
        // intermediate form so a future TryGetPreloaded hit doesn't have to
        // re-read from disk. Reading mesh.vertices etc. on main thread is fine
        // (the mesh is readable since we just built it via CombineMeshes).
        private static List<CachedInstanceGroupData> ExtractInstanceGroupDataFromLive(List<ZoneInstanceGroup> groups)
        {
            if (groups == null) return new List<CachedInstanceGroupData>();
            var list = new List<CachedInstanceGroupData>(groups.Count);
            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                int count = group.Matrices.Count;
                var positionsX = new int[count];
                var positionsY = new int[count];
                var positionsZ = new int[count];
                var positionsR = new int[count];
                for (int m = 0; m < count; m++)
                {
                    positionsX[m] = group.Identities[m].X;
                    positionsY[m] = group.Identities[m].Y;
                    positionsZ[m] = group.Identities[m].Z;
                    positionsR[m] = group.Identities[m].R;
                }
                list.Add(new CachedInstanceGroupData
                {
                    PrefabHash = group.PrefabHash,
                    Look = group.Look.Bits,
                    Matrices = group.Matrices.ToArray(),
                    PositionsX = positionsX,
                    PositionsY = positionsY,
                    PositionsZ = positionsZ,
                    PositionsR = positionsR,
                });
            }
            return list;
        }

        private static void RegisterLiveMaterials(List<MeshBaker.BatchInstance> batches)
        {
            if (batches == null) return;
            for (int i = 0; i < batches.Count; i++)
            {
                var mr = batches[i].Combined != null ? batches[i].Combined.GetComponent<MeshRenderer>() : null;
                if (mr != null) RegisterMaterial(mr.sharedMaterial);
            }
        }

        private static List<CachedBatchData> ExtractBatchDataFromLive(List<MeshBaker.BatchInstance> batches, List<CachedBatchData> reuse = null)
        {
            if (batches == null) return new List<CachedBatchData>();
            var list = new List<CachedBatchData>(batches.Count);
            for (int i = 0; i < batches.Count; i++) ExtractOneBatch(batches, i, reuse, list);   // 1.2.361: the loop body moved to ExtractOneBatch, unchanged, so the sliced save reads the same bytes one batch per step
            return list;
        }

        // 1.2.361: one batch of ExtractBatchDataFromLive (the old loop body, unchanged). Appends to `list`; a destroyed mesh is skipped as before.
        private static void ExtractOneBatch(List<MeshBaker.BatchInstance> batches, int i, List<CachedBatchData> reuse, List<CachedBatchData> list)
        {
            {
                var batch = batches[i];
                var mr = batch.Combined != null ? batch.Combined.GetComponent<MeshRenderer>() : null;
                var matName = mr != null ? MaterialKey(mr.sharedMaterial) : "";
                var mesh = batch.Mesh;
                if (mesh == null) return;

                // 1.2.253: renderer values below are always read fresh; only the geometry is reused
                int meshId = mesh.GetInstanceID();
                var same = SameMesh(reuse, meshId);
                if (same != null) s_arraysReused++; else if (reuse != null) s_arraysRead++;
                if (same == null && !mesh.isReadable)
                {
                    // E25: this mesh's CPU copy was released (only ever after this zone's file held exactly it, and no change since).
                    // The arrays come back from that file, matched by position and material key.
                    bool far = s_saveBake != null && ReferenceEquals(batches, s_saveBake.FarBatches);
                    same = s_saveBake != null ? DiskBatch(far, list.Count, matName, mesh.vertexCount, (int)mesh.GetIndexCount(0)) : null;
                    if (same == null) throw new ReleasedMeshUnreadable($"the released mesh '{mesh.name}' was not found in the zone's file");
                }
                var verts = same != null ? same.Vertices : mesh.vertices;
                var normals = same != null ? same.Normals : mesh.normals;
                var tangents = same != null ? same.Tangents : mesh.tangents;
                var uvs = same != null ? same.Uvs : mesh.uv;
                var indices = same != null ? same.Indices : mesh.GetIndices(0);

                list.Add(new CachedBatchData
                {
                    MaterialName = matName,
                    SourceMeshId = meshId,
                    Vertices = verts,
                    Normals  = (normals  != null && normals.Length  == verts.Length) ? normals  : null,
                    Tangents = (tangents != null && tangents.Length == verts.Length) ? tangents : null,
                    Uvs      = (uvs      != null && uvs.Length      == verts.Length) ? uvs      : null,
                    Indices = indices,
                    IndexFormatUInt32 = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32,
                    ShadowCastingMode    = mr != null ? (byte)mr.shadowCastingMode    : (byte)0,
                    ReceiveShadows       = mr != null && mr.receiveShadows ? (byte)1 : (byte)0,
                    LightProbeUsage      = mr != null ? (byte)mr.lightProbeUsage      : (byte)0,
                    ReflectionProbeUsage = mr != null ? (byte)mr.reflectionProbeUsage : (byte)0,
                    MotionVectorMode     = mr != null ? (byte)mr.motionVectorGenerationMode : (byte)0,
                });
            }
        }

        // --- Binary format -----------------------------------------------------

        private static void WriteBakeResult(BinaryWriter bw, long worldUid, Vector2s coord, MeshBaker.BakeResult bake, int[] prints, List<CachedBatchData> nearData = null, List<CachedBatchData> farData = null)
        {
            bw.Write(MAGIC);
            bw.Write(VERSION);
            bw.Write(CurrentBakeSettingsKey());
            // Valheim 1.0's Vector2s holds shorts; the format stores int32, which the reader expects.
            bw.Write((int)coord.x);
            bw.Write((int)coord.y);
            bw.Write(worldUid);

            bw.Write(prints.Length / 2);
            for (int p = 0; p < prints.Length; p++) bw.Write(prints[p]);

            int identityCount = bake.ContributorIdentities?.Count ?? 0;
            bw.Write(identityCount);
            if (bake.ContributorIdentities != null)
            {
                foreach (var id in bake.ContributorIdentities)
                {
                    bw.Write(id.PrefabHash);
                    bw.Write(id.X);
                    bw.Write(id.Y);
                    bw.Write(id.Z);
                    bw.Write(id.R);
                }
            }

            int transformCount = bake.PieceTransforms?.Count ?? 0;
            bw.Write(transformCount);
            if (bake.PieceTransforms != null)
            {
                foreach (var entry in bake.PieceTransforms)
                {
                    bw.Write(entry.Key.PrefabHash);
                    bw.Write(entry.Key.X);
                    bw.Write(entry.Key.Y);
                    bw.Write(entry.Key.Z);
                    bw.Write(entry.Key.R);
                    var t = entry.Value;
                    bw.Write(t.Position.x); bw.Write(t.Position.y); bw.Write(t.Position.z);
                    bw.Write(t.Rotation.x); bw.Write(t.Rotation.y); bw.Write(t.Rotation.z); bw.Write(t.Rotation.w);
                    bw.Write(t.Scale.x); bw.Write(t.Scale.y); bw.Write(t.Scale.z);
                    bw.Write(t.Look.Bits);
                }
            }

            if (nearData != null)
            {
                bw.Write(nearData.Count);
                for (int i = 0; i < nearData.Count; i++) WriteBatchData(bw, nearData[i]);
            }
            else
            {
                bw.Write(bake.Batches.Count);
                for (int i = 0; i < bake.Batches.Count; i++)
                    WriteBatch(bw, bake.Batches[i]);
            }

            if (farData != null)
            {
                bw.Write(farData.Count);
                for (int i = 0; i < farData.Count; i++) WriteBatchData(bw, farData[i]);
            }
            else
            {
                int farCount = bake.FarBatches?.Count ?? 0;
                bw.Write(farCount);
                for (int i = 0; i < farCount; i++)
                    WriteBatch(bw, bake.FarBatches[i]);
            }

            int instanceGroupCount = bake.InstanceGroups?.Count ?? 0;
            bw.Write(instanceGroupCount);
            for (int i = 0; i < instanceGroupCount; i++)
            {
                var group = bake.InstanceGroups[i];
                bw.Write(group.PrefabHash);
                bw.Write(group.Look.Bits);
                bw.Write(group.Matrices.Count);
                for (int m = 0; m < group.Matrices.Count; m++)
                {
                    var matrix = group.Matrices[m];
                    for (int e = 0; e < 16; e++) bw.Write(matrix[e]);
                    var identity = group.Identities[m];
                    bw.Write(identity.X);
                    bw.Write(identity.Y);
                    bw.Write(identity.Z);
                    bw.Write(identity.R);
                }
            }
        }

        // 1.2.253: the same bytes as WriteBatch, from arrays already held. WriteBatch stays as it was for the key-off path.
        private static void WriteBatchData(BinaryWriter bw, CachedBatchData d)
        {
            var matBytes = Encoding.UTF8.GetBytes(d.MaterialName ?? "");
            bw.Write(matBytes.Length);
            bw.Write(matBytes);

            bw.Write(d.Vertices.Length);
            bw.Write(d.Indices.Length);
            bw.Write((byte)(d.IndexFormatUInt32 ? 1 : 0));
            bw.Write((byte)((d.Normals != null ? 0x1 : 0) | (d.Tangents != null ? 0x2 : 0) | (d.Uvs != null ? 0x4 : 0)));

            foreach (var v in d.Vertices) { bw.Write(v.x); bw.Write(v.y); bw.Write(v.z); }
            if (d.Normals != null) foreach (var n in d.Normals) { bw.Write(n.x); bw.Write(n.y); bw.Write(n.z); }
            if (d.Tangents != null) foreach (var t in d.Tangents) { bw.Write(t.x); bw.Write(t.y); bw.Write(t.z); bw.Write(t.w); }
            if (d.Uvs != null) foreach (var uv in d.Uvs) { bw.Write(uv.x); bw.Write(uv.y); }
            foreach (int index in d.Indices) bw.Write(index);

            bw.Write(d.ShadowCastingMode);
            bw.Write(d.ReceiveShadows);
            bw.Write(d.LightProbeUsage);
            bw.Write(d.ReflectionProbeUsage);
            bw.Write(d.MotionVectorMode);
        }

        private static void WriteBatch(BinaryWriter bw, MeshBaker.BatchInstance batch)
        {
            var mr = batch.Combined?.GetComponent<MeshRenderer>();
            var matName = MaterialKey(mr?.sharedMaterial);
            var matBytes = Encoding.UTF8.GetBytes(matName);
            bw.Write(matBytes.Length);
            bw.Write(matBytes);

            var mesh = batch.Mesh;
            BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveRead);
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var indices = mesh.GetIndices(0);

            BakeFrameSplit.Enter(BakeFrameSplit.Step.SaveSerialise);
            bool hasNormals  = normals  != null && normals.Length  == verts.Length;
            bool hasTangents = tangents != null && tangents.Length == verts.Length;
            bool hasUvs      = uvs      != null && uvs.Length      == verts.Length;

            bw.Write(verts.Length);
            bw.Write(indices.Length);
            bw.Write((byte)(mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 1 : 0));

            byte flags = 0;
            if (hasNormals)  flags |= 0x1;
            if (hasTangents) flags |= 0x2;
            if (hasUvs)      flags |= 0x4;
            bw.Write(flags);

            for (int i = 0; i < verts.Length; i++)
            {
                bw.Write(verts[i].x); bw.Write(verts[i].y); bw.Write(verts[i].z);
            }
            if (hasNormals)
            {
                for (int i = 0; i < normals.Length; i++)
                {
                    bw.Write(normals[i].x); bw.Write(normals[i].y); bw.Write(normals[i].z);
                }
            }
            if (hasTangents)
            {
                for (int i = 0; i < tangents.Length; i++)
                {
                    bw.Write(tangents[i].x); bw.Write(tangents[i].y); bw.Write(tangents[i].z); bw.Write(tangents[i].w);
                }
            }
            if (hasUvs)
            {
                for (int i = 0; i < uvs.Length; i++)
                {
                    bw.Write(uvs[i].x); bw.Write(uvs[i].y);
                }
            }

            for (int i = 0; i < indices.Length; i++)
                bw.Write(indices[i]);

            if (mr != null)
            {
                bw.Write((byte)mr.shadowCastingMode);
                bw.Write(mr.receiveShadows ? (byte)1 : (byte)0);
                bw.Write((byte)mr.lightProbeUsage);
                bw.Write((byte)mr.reflectionProbeUsage);
                bw.Write((byte)mr.motionVectorGenerationMode);
            }
            else
            {
                for (int i = 0; i < 5; i++) bw.Write((byte)0);
            }
        }

        // Read path is split so the background worker can call it WITHOUT
        // touching Unity API. It returns intermediate arrays; ConstructFromCache
        // turns those into Mesh + GameObject on the main thread.
        // D5: a count read from a file can never ask for more than the bytes left in it.
        private static void NeedBytes(BinaryReader br, long bytes, string what)
        {
            if (bytes < 0L || bytes > br.BaseStream.Length - br.BaseStream.Position) throw new InvalidDataException($"{BadSavedData}{what} needs {bytes} bytes, more than the file holds");
        }

        private static int ReadCount(BinaryReader br, int minBytesEach, string what)
        {
            int count = br.ReadInt32();
            if (count < 0) throw new InvalidDataException($"{BadSavedData}{what} count is {count}");
            NeedBytes(br, count * (long)minBytesEach, what);
            return count;
        }

        private static CachedZoneData ReadCachedZoneData(BinaryReader br, long expectedWorldUid, Vector2s expectedCoord, byte[] file, int fileLength)
        {
            uint magic = br.ReadUInt32();
            if (magic != MAGIC) throw new InvalidDataException($"bad magic 0x{magic:X8}");
            int version = br.ReadInt32();
            if (version != VERSION) throw new InvalidDataException($"version {version} != {VERSION}");
            // After the version test, so a file from an older format stays a Debug line.
            if (fileLength < 12 || Far.FarCache.Crc32(file, fileLength - 4) != BitConverter.ToUInt32(file, fileLength - 4))
                throw new InvalidDataException($"{BadSavedData}checksum does not match");
            int bakeSettings = br.ReadInt32();
            if (bakeSettings != CurrentBakeSettingsKey()) SayFingerprint(expectedCoord, $"rebuilt (file was baked under fingerprint {bakeSettings})");
            if (bakeSettings != CurrentBakeSettingsKey() && KeepOtherSettingsOn)
            {
                // D6 [Cache] KeepOtherSettings: an A/B on one setting no longer throws away the world's cache; the file stays until this zone bakes again.
                if (!s_saidKeepOther) { s_saidKeepOther = true; EasyBakeLog.Info("[Cache] KeepOtherSettings: saved zones baked under other settings are kept on disk and not used; a zone that bakes again replaces its file"); }
                s_missSettingsKept++; EasyBakeLog.Debug($"[Cache] Zone ({expectedCoord.x},{expectedCoord.y}): baked under other settings ({bakeSettings}); file kept, not used.");
                return null;
            }
            if (bakeSettings != CurrentBakeSettingsKey())
                throw new InvalidDataException(
                    $"baked under different batching settings ({bakeSettings} != {CurrentBakeSettingsKey()})");
            int zx = br.ReadInt32();
            int zy = br.ReadInt32();
            long worldUid = br.ReadInt64();
            if (zx != expectedCoord.x || zy != expectedCoord.y)
                throw new InvalidDataException($"coord {zx},{zy} != expected {expectedCoord.x},{expectedCoord.y}");
            if (worldUid != expectedWorldUid)
                throw new InvalidDataException($"worldUid {worldUid:x16} != expected {expectedWorldUid:x16}");

            int printPairs = ReadCount(br, 8, "prefab print");
            var prefabPrints = new int[printPairs * 2];
            for (int p = 0; p < prefabPrints.Length; p++) prefabPrints[p] = br.ReadInt32();

            int identityCount = ReadCount(br, 20, "identity");
            var identities = new HashSet<MeshBaker.PieceIdentity>();
            for (int i = 0; i < identityCount; i++)
            {
                identities.Add(new MeshBaker.PieceIdentity
                {
                    PrefabHash = br.ReadInt32(),
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    R = br.ReadInt32(),
                });
            }

            int transformCount = ReadCount(br, 61, "transform");
            var transforms = new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(transformCount);
            for (int i = 0; i < transformCount; i++)
            {
                var id = new MeshBaker.PieceIdentity
                {
                    PrefabHash = br.ReadInt32(),
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    R = br.ReadInt32(),
                };
                transforms[id] = new MeshBaker.PieceTransform
                {
                    Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Rotation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Scale = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                    Look = new WearLook(br.ReadByte()),
                };
            }

            int batchCount = ReadCount(br, 19, "batch");
            var batches = new List<CachedBatchData>(batchCount);
            for (int i = 0; i < batchCount; i++)
            {
                var b = ReadCachedBatchData(br);
                if (b != null) batches.Add(b);
            }

            int farBatchCount = ReadCount(br, 19, "far batch");
            var farBatches = new List<CachedBatchData>(farBatchCount);
            for (int i = 0; i < farBatchCount; i++)
            {
                var farBatch = ReadCachedBatchData(br);
                if (farBatch != null) farBatches.Add(farBatch);
            }

            int instanceGroupCount = ReadCount(br, 9, "instance group");
            var instanceGroups = new List<CachedInstanceGroupData>(instanceGroupCount);
            for (int i = 0; i < instanceGroupCount; i++)
            {
                int prefabHash = br.ReadInt32();
                byte look = br.ReadByte();
                int matrixCount = ReadCount(br, 80, "matrix");
                var matrices = new Matrix4x4[matrixCount];
                var positionsX = new int[matrixCount];
                var positionsY = new int[matrixCount];
                var positionsZ = new int[matrixCount];
                var positionsR = new int[matrixCount];
                for (int m = 0; m < matrixCount; m++)
                {
                    var matrix = new Matrix4x4();
                    for (int e = 0; e < 16; e++) matrix[e] = br.ReadSingle();
                    CheckSavedMatrix(in matrix, prefabHash);   // 1.2.236
                    matrices[m] = matrix;
                    positionsX[m] = br.ReadInt32();
                    positionsY[m] = br.ReadInt32();
                    positionsZ[m] = br.ReadInt32();
                    positionsR[m] = br.ReadInt32();
                }
                instanceGroups.Add(new CachedInstanceGroupData
                {
                    PrefabHash = prefabHash,
                    Look = look,
                    Matrices = matrices,
                    PositionsX = positionsX,
                    PositionsY = positionsY,
                    PositionsZ = positionsZ,
                    PositionsR = positionsR,
                });
            }

            if (br.BaseStream.Position != fileLength - 4) throw new InvalidDataException($"{BadSavedData}{fileLength - 4 - br.BaseStream.Position} bytes left over");
            if (batches.Count == 0 && instanceGroups.Count == 0) return null;

            return new CachedZoneData
            {
                Coord = expectedCoord,
                BakeSettings = bakeSettings,
                PrefabPrints = prefabPrints,
                ContributorIdentities = identities,
                PieceTransforms = transforms,
                Batches = batches,
                FarBatches = farBatches,
                InstanceGroups = instanceGroups,
            };
        }

        // 1.2.236: run on the preload worker, so no Unity objects. A throw here deletes the file and the zone bakes fresh.
        private const string BadSavedData = "bad saved data: ";

        private static bool FiniteVector(Vector3 v) => !float.IsNaN(v.x + v.y + v.z) && !float.IsInfinity(v.x + v.y + v.z);

        private static void CheckSavedMesh(string material, Vector3[] verts, int[] indices)
        {
            if (indices.Length % 3 != 0) throw new InvalidDataException($"{BadSavedData}'{material}' has {indices.Length} indices, not whole triangles");
            for (int v = 0; v < verts.Length; v++)
                if (!FiniteVector(verts[v])) throw new InvalidDataException($"{BadSavedData}'{material}' vertex {v} is not finite");
            float limit = MaxSavedTriangleEdge * MaxSavedTriangleEdge;
            for (int t = 0; t < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= verts.Length || b >= verts.Length || c >= verts.Length)
                    throw new InvalidDataException($"{BadSavedData}'{material}' triangle {t / 3} indexes past its {verts.Length} vertices ({a}, {b}, {c})");
                float edge = Mathf.Max((verts[a] - verts[b]).sqrMagnitude, Mathf.Max((verts[b] - verts[c]).sqrMagnitude, (verts[c] - verts[a]).sqrMagnitude));
                if (edge > limit) throw new InvalidDataException($"{BadSavedData}'{material}' triangle {t / 3} has a {Mathf.Sqrt(edge):0} m edge");
            }
        }

        private static void CheckSavedMatrix(in Matrix4x4 matrix, int prefabHash)
        {
            for (int e = 0; e < 16; e++)
                if (float.IsNaN(matrix[e]) || float.IsInfinity(matrix[e])) throw new InvalidDataException($"{BadSavedData}prefab {prefabHash} has a matrix that is not finite");
            float scaleProduct = new Vector3(matrix.m00, matrix.m10, matrix.m20).magnitude * new Vector3(matrix.m01, matrix.m11, matrix.m21).magnitude * new Vector3(matrix.m02, matrix.m12, matrix.m22).magnitude;   // 1.2.237: relative, a 0.01 rig has volume
            if (scaleProduct <= 1e-18f || Mathf.Abs(matrix.determinant) < 1e-4f * scaleProduct) throw new InvalidDataException($"{BadSavedData}prefab {prefabHash} has a matrix with no volume at {(Vector3)matrix.GetColumn(3)}");
        }

        // 1.2.236: a saved instance matrix includes the prefab's LocalOffset of the session that saved it, and prefabs are built per session in load order.
        // Null = every instance still agrees with today's definition; otherwise why not. Instances without a saved transform cannot be compared.
        private static string SavedInstanceMismatch(CachedZoneData data, CachedInstanceGroupData group, InstanceDefinition definition)
        {
            if (data.PieceTransforms == null) return null;
            for (int m = 0; m < group.Matrices.Length; m++)
            {
                var identity = new MeshBaker.PieceIdentity { PrefabHash = group.PrefabHash, X = group.PositionsX[m], Y = group.PositionsY[m], Z = group.PositionsZ[m], R = group.PositionsR[m] };
                if (!data.PieceTransforms.TryGetValue(identity, out var piece)) continue;
                var expected = Matrix4x4.TRS(piece.Position, piece.Rotation, piece.Scale) * definition.LocalOffset;
                var saved = group.Matrices[m];
                float moved = ((Vector3)expected.GetColumn(3) - (Vector3)saved.GetColumn(3)).magnitude;
                float turned = 0f;
                for (int e = 0; e < 12; e++) turned = Mathf.Max(turned, Mathf.Abs(expected[e] - saved[e]));
                if (moved > 0.05f || turned > 0.02f)
                    return $"prefab {group.PrefabHash} at {piece.Position:F1}: the saved matrix differs from today's definition by {moved:0.##} m, {turned:0.###} in rotation/scale";
            }
            return null;
        }

        private static CachedBatchData ReadCachedBatchData(BinaryReader br)
        {
            int matLen = ReadCount(br, 1, "material name");
            var matName = Encoding.UTF8.GetString(br.ReadBytes(matLen));

            int vertCount = br.ReadInt32();
            int indexCount = br.ReadInt32();
            byte indexFormatByte = br.ReadByte();
            byte flags = br.ReadByte();
            bool hasNormals  = (flags & 0x1) != 0;
            bool hasTangents = (flags & 0x2) != 0;
            bool hasUvs      = (flags & 0x4) != 0;
            if (vertCount < 0 || indexCount < 0) throw new InvalidDataException($"{BadSavedData}'{matName}' has {vertCount} vertices, {indexCount} indices");
            NeedBytes(br, vertCount * (12L + (hasNormals ? 12L : 0L) + (hasTangents ? 16L : 0L) + (hasUvs ? 8L : 0L)) + indexCount * 4L + 5L, "mesh '" + matName + "'");

            var verts = new Vector3[vertCount];
            for (int i = 0; i < vertCount; i++) verts[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

            Vector3[] normals = null;
            if (hasNormals)
            {
                normals = new Vector3[vertCount];
                for (int i = 0; i < vertCount; i++) normals[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            }

            Vector4[] tangents = null;
            if (hasTangents)
            {
                tangents = new Vector4[vertCount];
                for (int i = 0; i < vertCount; i++) tangents[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            }

            Vector2[] uvs = null;
            if (hasUvs)
            {
                uvs = new Vector2[vertCount];
                for (int i = 0; i < vertCount; i++) uvs[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
            }

            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++) indices[i] = br.ReadInt32();
            CheckSavedMesh(matName, verts, indices);   // 1.2.236

            byte shadowCastingMode    = br.ReadByte();
            byte receiveShadows       = br.ReadByte();
            byte lightProbeUsage      = br.ReadByte();
            byte reflectionProbeUsage = br.ReadByte();
            byte motionVectorMode     = br.ReadByte();

            return new CachedBatchData
            {
                MaterialName = matName,
                Vertices = verts,
                Normals = normals,
                Tangents = tangents,
                Uvs = uvs,
                Indices = indices,
                IndexFormatUInt32 = indexFormatByte == 1,
                ShadowCastingMode = shadowCastingMode,
                ReceiveShadows = receiveShadows,
                LightProbeUsage = lightProbeUsage,
                ReflectionProbeUsage = reflectionProbeUsage,
                MotionVectorMode = motionVectorMode,
            };
        }
    }
}
