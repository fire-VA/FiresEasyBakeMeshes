using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // On-disk persistence for baked zone meshes, with an async preload so we
    // don't stall the main thread on file I/O while pieces are streaming in.
    //
    // Lifecycle (per session):
    //   1. ZNetScene.Awake → BuildMaterialRegistry walks every prefab's
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
    //      That's a sub-millisecond per zone — no perceptible hitch.
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
        //   1 — initial format.
        //   2 — Door + Container added to HasBakeUnsafeComponent. v1 caches
        //       contain frozen door/chest geometry; reject them so they
        //       rebake cleanly on next login.
        //   3 — batches are grouped by full renderer settings rather than material
        //       alone, and each zone also stores a far-tier (LOD1) batch list. v2
        //       caches have neither; reject them so they rebake.
        //   4 — GPU-instanced prefab groups added alongside the combined batches.
        //       A v3 cache suppressed those pieces without recording what draws
        //       them, so reject it and rebake.
        //   5 — each contributor's position, rotation and scale, which stand-in colliders need to replace pieces
        //       that are never created.
        //   6 — damageable pieces join instanced groups; a v5 zone would keep drawing them itself until it rebaked.
        //   7 — each identity carries a rotation key, so copies stacked on one spot turned for looks stay distinct.
        //   8 — tinted grausten merges into combined batches, and item and armor stands are never baked.
        private const uint MAGIC = 0x434D4245;
        private const int VERSION = 11;

        private static string _cacheRoot;
        private static readonly Dictionary<string, Material> _materialsByName = new Dictionary<string, Material>();
        private static readonly HashSet<int> _indexedPrefabs = new HashSet<int>();
        private static bool _materialsBuilt;

        // Preload state. _preloaded is filled by the background worker as each
        // zone finishes parsing. _preloadKicked guards against multiple kickoffs.
        private static readonly ConcurrentDictionary<Vector2s, CachedZoneData> _preloaded
            = new ConcurrentDictionary<Vector2s, CachedZoneData>();
        private static volatile bool _preloadKicked;
        private static volatile bool _preloadFinished;
        private static volatile int _preloadZonesLoaded;
        private static volatile int _preloadZonesFailed;
        private static volatile int _preloadGeneration;
        private static long _preloadWorldUid;

        // Intermediate off-thread representation of one cached zone. Holds
        // arrays only — no Unity Mesh/GameObject refs. The Construct step
        // turns this into a MeshBaker.BakeResult on the main thread.
        internal class CachedZoneData
        {
            public Vector2s Coord;
            public HashSet<MeshBaker.PieceIdentity> ContributorIdentities;
            public Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> PieceTransforms;
            public List<CachedBatchData> Batches;
            public List<CachedBatchData> FarBatches;
            public List<CachedInstanceGroupData> InstanceGroups;
        }

        internal class CachedInstanceGroupData
        {
            public int PrefabHash;
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

        public static void Initialize()
        {
            _cacheRoot = Path.Combine(Paths.ConfigPath, "FiresEasyBakeMeshes", "cache");
        }

        // True iff a per-world cache directory exists on disk. Used by the
        // ZNetScene.Awake postfix to decide whether the material registry needs
        // to be built at all — the registry's sole consumer is cached zone
        // reattach (ConstructFromCache → FindMaterial). No cache directory =>
        // no possible reattach => no need to spend ~50 main-thread-seconds
        // walking 3600+ prefabs to index materials we'll never look up.
        public static bool WorldHasCache(long worldUid)
        {
            if (_cacheRoot == null || worldUid == 0L) return false;
            try
            {
                var dir = Path.Combine(_cacheRoot, worldUid.ToString("x16"));
                return Directory.Exists(dir);
            }
            catch { return false; }
        }

        // Synchronous entry point — preserved for callers that need the
        // registry immediately. Internally this is now a thin wrapper that
        // drains the coroutine in one frame. Prefer BuildMaterialRegistryAsync
        // when running on the main thread during world load.
        public static void BuildMaterialRegistry(ZNetScene scene)
        {
            if (_materialsBuilt) return;
            if (scene == null || scene.m_prefabs == null) { _materialsBuilt = true; return; }

            var enumerator = BuildMaterialRegistryAsync(scene, runSynchronously: true);
            while (enumerator.MoveNext()) { /* drain — no yields when synchronous */ }
        }

        // Time-budgeted coroutine. Walks every prefab's MeshRenderers and
        // indexes sharedMaterials by Material.name. Yields when the per-frame
        // budget (FiresEasyBakeMeshesPlugin.MaterialRegistryFrameBudgetMs) is
        // exhausted so the main thread stays responsive — the password dialog
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
                ? FiresEasyBakeMeshesPlugin.MaterialRegistryFrameBudgetMs.Value
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
            EasyBakeLog.Info(
                $"[Cache] Material registry built: {_materialsByName.Count} unique materials " +
                $"across {prefabCount} prefabs in {wall.Elapsed.TotalSeconds:F2}s " +
                $"({(runSynchronously ? "sync" : "yielded")}).");
        }

        public static Material FindMaterial(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _materialsByName.TryGetValue(name, out var mat);
            return mat;
        }

        private static void IndexRendererMaterials(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int r = 0; r < renderers.Length; r++)
            {
                var mats = renderers[r].sharedMaterials;
                for (int m = 0; m < mats.Length; m++)
                    RegisterMaterial(mats[m]);
            }
        }

        private static void RegisterMaterial(Material mat)
        {
            if (mat == null || string.IsNullOrEmpty(mat.name)) return;
            if (!_materialsByName.ContainsKey(mat.name))
                _materialsByName[mat.name] = mat;
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
                var prefab = scene.GetPrefab(identity.PrefabHash);
                if (prefab == null) continue;
                _indexedPrefabs.Add(identity.PrefabHash);
                IndexRendererMaterials(prefab);
            }
        }

        private static string FirstMissingMaterial(List<CachedBatchData> batches)
        {
            if (batches == null) return null;
            for (int i = 0; i < batches.Count; i++)
                if (FindMaterial(batches[i].MaterialName) == null) return batches[i].MaterialName;
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
        public static void KickPreload(long worldUid)
        {
            if (worldUid == 0L || _cacheRoot == null) return;
            if (_preloadKicked && worldUid == _preloadWorldUid) return;
            if (_preloadKicked && !_preloadFinished) return;

            _preloadGeneration++;
            _preloaded.Clear();
            _preloadZonesLoaded = 0;
            _preloadZonesFailed = 0;
            _preloadFinished = false;
            _preloadKicked = true;
            _preloadWorldUid = worldUid;

            var dir = Path.Combine(_cacheRoot, worldUid.ToString("x16"));
            if (!Directory.Exists(dir))
            {
                _preloadFinished = true;
                EasyBakeLog.Info($"[Cache] No on-disk cache directory for world {worldUid:x16}; skipping preload.");
                return;
            }

            // Background Task. No Unity API allowed in here.
            int generation = _preloadGeneration;
            Task.Run(() => PreloadWorker(worldUid, dir, generation));
            EasyBakeLog.Info($"[Cache] Preload started for world {worldUid:x16}.");
        }

        private static void PreloadWorker(long worldUid, string dir, int generation)
        {
            int unreadableRemoved = 0;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string[] files;
                try { files = Directory.GetFiles(dir, "zone_*.bin"); }
                catch (Exception ex)
                {
                    EasyBakeLog.Warn($"[Cache] Preload directory scan failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                for (int i = 0; i < files.Length; i++)
                {
                    if (generation != _preloadGeneration) return;
                    var file = files[i];
                    Vector2s coord;
                    if (!TryParseCoord(Path.GetFileNameWithoutExtension(file), out coord))
                    {
                        _preloadZonesFailed++;
                        continue;
                    }
                    try
                    {
                        using (var fs = File.OpenRead(file))
                        using (var br = new BinaryReader(fs))
                        {
                            var data = ReadCachedZoneData(br, worldUid, coord);
                            if (data != null)
                            {
                                _preloaded[coord] = data;
                                _preloadZonesLoaded++;
                            }
                            else
                            {
                                _preloadZonesFailed++;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException)
                    {
                        // Wrong format, version, coordinates or world, or truncated: it can never load, so remove it
                        // rather than fail it again every login. The zone bakes fresh and saves a readable file.
                        _preloadZonesFailed++;
                        try { File.Delete(file); unreadableRemoved++; }
                        catch (Exception deleteEx) { EasyBakeLog.Warn($"[Cache] Could not remove unreadable {Path.GetFileName(file)}: {deleteEx.Message}"); }
                    }
                    catch (Exception ex)
                    {
                        _preloadZonesFailed++;
                        EasyBakeLog.Warn($"[Cache] Preload of {Path.GetFileName(file)} failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                sw.Stop();
                EasyBakeLog.Info(
                    $"[Cache] Preload finished in {sw.Elapsed.TotalSeconds:F2}s: " +
                    $"{_preloadZonesLoaded} zones loaded, {_preloadZonesFailed} failed" +
                    (unreadableRemoved > 0 ? $"; removed {unreadableRemoved} unreadable cache file(s), those zones bake fresh." : "."));
            }
            finally
            {
                if (generation == _preloadGeneration) _preloadFinished = true;
            }
        }

        // Filename like "zone_-17_11.bin" → Vector2s(-17, 11).
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

        // --- Construction (main thread) ----------------------------------------

        // Turns the intermediate arrays into a live BakeResult with real Unity
        // Mesh + GameObject instances. Cheap — just buffer copies into Unity's
        // mesh API; no disk I/O, no parsing.
        public static MeshBaker.BakeResult ConstructFromCache(CachedZoneData data)
        {
            if (data == null) return null;
            if (data.Batches == null || data.Batches.Count == 0) return null;

            // Every piece in ContributorIdentities gets hidden on the strength of this cache, so a batch that cannot
            // be rebuilt would leave its pieces invisible. Rebuild all of them or none, and bake fresh otherwise.
            IndexContributorPrefabs(data);
            string missing = FirstMissingMaterial(data.Batches) ?? FirstMissingMaterial(data.FarBatches);
            if (missing != null)
            {
                _preloaded.TryRemove(data.Coord, out _);
                EasyBakeLog.Warn($"[Cache] Zone ({data.Coord.x},{data.Coord.y}): cached material '{missing}' is not loaded; baking the zone fresh instead.");
                return null;
            }

            var result = new MeshBaker.BakeResult
            {
                ContributorIdentities = data.ContributorIdentities,
                PieceTransforms = data.PieceTransforms ?? new Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform>(),
            };

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
                    InstanceDefinition definition;
                    if (!InstanceDefinitionCache.TryGet(cachedGroup.PrefabHash, out definition))
                    {
                        EasyBakeLog.Warn(
                            $"[Cache] Zone ({data.Coord.x},{data.Coord.y}): instanced prefab {cachedGroup.PrefabHash} " +
                            "no longer resolves — discarding cache so the zone rebakes.");
                        UnityEngine.Object.Destroy(parent);
                        return null;
                    }

                    var group = new ZoneInstanceGroup { PrefabHash = cachedGroup.PrefabHash, Definition = definition };
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
                return null;
            }
            return result;
        }

        private static void ConstructTier(CachedZoneData data, List<CachedBatchData> cached, GameObject parent,
            List<MeshBaker.BatchInstance> into, bool farTier)
        {
            if (cached == null) return;
            for (int i = 0; i < cached.Count; i++)
            {
                var b = cached[i];
                var mat = FindMaterial(b.MaterialName);
                if (mat == null) continue;

                string batchName = b.MaterialName + (farTier ? MeshBaker.FarTierNameSuffix : string.Empty);
                var mesh = new Mesh { name = $"EasyBakeCached_{data.Coord.x}_{data.Coord.y}_{batchName}" };
                if (b.IndexFormatUInt32) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
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
                if (farTier) go.SetActive(false);

                into.Add(new MeshBaker.BatchInstance { Combined = go, Mesh = mesh });
            }
        }

        // --- Save (main thread; runs after a successful Bake) ------------------

        public static void Save(long worldUid, Vector2s coord, MeshBaker.BakeResult bake)
        {
            if (_cacheRoot == null || worldUid == 0L) return;
            if (bake == null || !bake.HasRenderableContent) return;

            var path = GetZonePath(worldUid, coord);
            if (path == null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var fs = File.Create(path))
                using (var bw = new BinaryWriter(fs))
                {
                    WriteBakeResult(bw, worldUid, coord, bake);
                }

                // Also refresh the preload entry so an immediate revisit of
                // this zone (without restarting the game) sees the new bake.
                RegisterLiveMaterials(bake.Batches);
                RegisterLiveMaterials(bake.FarBatches);
                var refreshed = new CachedZoneData
                {
                    Coord = coord,
                    ContributorIdentities = bake.ContributorIdentities,
                    PieceTransforms = bake.PieceTransforms,
                    Batches = ExtractBatchDataFromLive(bake.Batches),
                    FarBatches = ExtractBatchDataFromLive(bake.FarBatches),
                    InstanceGroups = ExtractInstanceGroupDataFromLive(bake.InstanceGroups),
                };
                _preloaded[coord] = refreshed;
            }
            catch (Exception ex)
            {
                EasyBakeLog.Warn($"[Cache] Save failed for zone ({coord.x},{coord.y}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Drops a zone's cache file AND its preloaded copy. Called when a
        // rebake produced no batches or a dirty zone tore down below the bake
        // threshold — leaving the old file would resurrect the removed pieces'
        // ghost geometry next session.
        public static void Delete(long worldUid, Vector2s coord)
        {
            try
            {
                _preloaded.TryRemove(coord, out _);
                var path = GetZonePath(worldUid, coord);
                if (path != null && File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                EasyBakeLog.Warn($"[Cache] Delete failed for zone ({coord.x},{coord.y}): {ex.Message}");
            }
        }

        private static string GetZonePath(long worldUid, Vector2s coord)
        {
            if (_cacheRoot == null) return null;
            return Path.Combine(_cacheRoot, worldUid.ToString("x16"), $"zone_{coord.x}_{coord.y}.bin");
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

        private static List<CachedBatchData> ExtractBatchDataFromLive(List<MeshBaker.BatchInstance> batches)
        {
            if (batches == null) return new List<CachedBatchData>();
            var list = new List<CachedBatchData>(batches.Count);
            for (int i = 0; i < batches.Count; i++)
            {
                var batch = batches[i];
                var mr = batch.Combined != null ? batch.Combined.GetComponent<MeshRenderer>() : null;
                var matName = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.name : "";
                var mesh = batch.Mesh;
                if (mesh == null) continue;

                var verts = mesh.vertices;
                var normals = mesh.normals;
                var tangents = mesh.tangents;
                var uvs = mesh.uv;
                var indices = mesh.GetIndices(0);

                list.Add(new CachedBatchData
                {
                    MaterialName = matName,
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
            return list;
        }

        // --- Binary format -----------------------------------------------------

        private static void WriteBakeResult(BinaryWriter bw, long worldUid, Vector2s coord, MeshBaker.BakeResult bake)
        {
            bw.Write(MAGIC);
            bw.Write(VERSION);
            // Valheim 1.0's Vector2s holds shorts; the format stores int32, which the reader expects.
            bw.Write((int)coord.x);
            bw.Write((int)coord.y);
            bw.Write(worldUid);

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
                }
            }

            bw.Write(bake.Batches.Count);
            for (int i = 0; i < bake.Batches.Count; i++)
                WriteBatch(bw, bake.Batches[i]);

            int farCount = bake.FarBatches?.Count ?? 0;
            bw.Write(farCount);
            for (int i = 0; i < farCount; i++)
                WriteBatch(bw, bake.FarBatches[i]);

            int instanceGroupCount = bake.InstanceGroups?.Count ?? 0;
            bw.Write(instanceGroupCount);
            for (int i = 0; i < instanceGroupCount; i++)
            {
                var group = bake.InstanceGroups[i];
                bw.Write(group.PrefabHash);
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

        private static void WriteBatch(BinaryWriter bw, MeshBaker.BatchInstance batch)
        {
            var mr = batch.Combined?.GetComponent<MeshRenderer>();
            var matName = mr?.sharedMaterial?.name ?? "";
            var matBytes = Encoding.UTF8.GetBytes(matName);
            bw.Write(matBytes.Length);
            bw.Write(matBytes);

            var mesh = batch.Mesh;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uvs = mesh.uv;
            var indices = mesh.GetIndices(0);

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
        private static CachedZoneData ReadCachedZoneData(BinaryReader br, long expectedWorldUid, Vector2s expectedCoord)
        {
            uint magic = br.ReadUInt32();
            if (magic != MAGIC) throw new InvalidDataException($"bad magic 0x{magic:X8}");
            int version = br.ReadInt32();
            if (version != VERSION) throw new InvalidDataException($"version {version} != {VERSION}");
            int zx = br.ReadInt32();
            int zy = br.ReadInt32();
            long worldUid = br.ReadInt64();
            if (zx != expectedCoord.x || zy != expectedCoord.y)
                throw new InvalidDataException($"coord {zx},{zy} != expected {expectedCoord.x},{expectedCoord.y}");
            if (worldUid != expectedWorldUid)
                throw new InvalidDataException($"worldUid {worldUid:x16} != expected {expectedWorldUid:x16}");

            int identityCount = br.ReadInt32();
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

            int transformCount = br.ReadInt32();
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
                };
            }

            int batchCount = br.ReadInt32();
            var batches = new List<CachedBatchData>(batchCount);
            for (int i = 0; i < batchCount; i++)
            {
                var b = ReadCachedBatchData(br);
                if (b != null) batches.Add(b);
            }

            int farBatchCount = br.ReadInt32();
            var farBatches = new List<CachedBatchData>(farBatchCount);
            for (int i = 0; i < farBatchCount; i++)
            {
                var farBatch = ReadCachedBatchData(br);
                if (farBatch != null) farBatches.Add(farBatch);
            }

            int instanceGroupCount = br.ReadInt32();
            var instanceGroups = new List<CachedInstanceGroupData>(instanceGroupCount);
            for (int i = 0; i < instanceGroupCount; i++)
            {
                int prefabHash = br.ReadInt32();
                int matrixCount = br.ReadInt32();
                var matrices = new Matrix4x4[matrixCount];
                var positionsX = new int[matrixCount];
                var positionsY = new int[matrixCount];
                var positionsZ = new int[matrixCount];
                var positionsR = new int[matrixCount];
                for (int m = 0; m < matrixCount; m++)
                {
                    var matrix = new Matrix4x4();
                    for (int e = 0; e < 16; e++) matrix[e] = br.ReadSingle();
                    matrices[m] = matrix;
                    positionsX[m] = br.ReadInt32();
                    positionsY[m] = br.ReadInt32();
                    positionsZ[m] = br.ReadInt32();
                    positionsR[m] = br.ReadInt32();
                }
                instanceGroups.Add(new CachedInstanceGroupData
                {
                    PrefabHash = prefabHash,
                    Matrices = matrices,
                    PositionsX = positionsX,
                    PositionsY = positionsY,
                    PositionsZ = positionsZ,
                    PositionsR = positionsR,
                });
            }

            if (batches.Count == 0 && instanceGroups.Count == 0) return null;

            return new CachedZoneData
            {
                Coord = expectedCoord,
                ContributorIdentities = identities,
                PieceTransforms = transforms,
                Batches = batches,
                FarBatches = farBatches,
                InstanceGroups = instanceGroups,
            };
        }

        private static CachedBatchData ReadCachedBatchData(BinaryReader br)
        {
            int matLen = br.ReadInt32();
            var matName = Encoding.UTF8.GetString(br.ReadBytes(matLen));

            int vertCount = br.ReadInt32();
            int indexCount = br.ReadInt32();
            byte indexFormatByte = br.ReadByte();
            byte flags = br.ReadByte();
            bool hasNormals  = (flags & 0x1) != 0;
            bool hasTangents = (flags & 0x2) != 0;
            bool hasUvs      = (flags & 0x4) != 0;

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
