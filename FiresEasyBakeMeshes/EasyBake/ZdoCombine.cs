using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.374 ([visual], Fire 11-14 via [lead]: "use the one method to load the zone faster and then while we're in the area it properly bakes in the
    // background so the cache is correct"). The combined batches of a zone built WITHOUT live pieces: from each prefab's own meshes (the wear child its
    // look shows) and the pieces' ZDO matrices, as plain arrays on a worker thread (no Mesh.CombineMeshes), in the cache file's own unit
    // (MeshCacheStore.CachedBatchData). Design: Tools\DESIGN_ZDO_full_bake_20261114.md section 2.
    //
    // It mirrors MeshBaker.Bake's combine path (MeshBaker.cs: CollectContributions / AppendContributions, the min-per-batch materialisation :303-311, the
    // coverage gate :325-338, the far-tier fallback :346-372, "only fully covered pieces are baked" :374-384):
    //  - per (prefab, look), once: which renderers a copy showing that look draws (WearLooks.IsShown instead of activeInHierarchy: an asset is never active),
    //    near tier = LOD0 + LOD-orphans, far tier = LOD1 (or LOD0) + orphans, one part per submesh with its material and renderer flags;
    //  - per zone: a batch key materialises when the NEAR parts of ALL the zone's combine candidates reach [Batching] MinPiecesPerBatch;
    //  - a piece is covered when it has no refused renderer and every key it feeds materialised; partial pieces are left out of the batches (they keep
    //    drawing themselves); a piece with no near renderer at all draws nothing;
    //  - worker: positions by the piece matrix x the renderer's matrix in the prefab, normals by the cofactor matrix, tangents by the 3x3, winding flipped
    //    for a mirrored matrix, concatenated per key and tier.
    // Callers ([seasons], ZoneTrackerZdoBake.cs) pass ALL of a zone's invulnerable combine candidates in ONE call (the materialisation is a zone count).
    internal static class ZdoCombine
    {
        // One combine key: a prefab at a wear look, every piece of it in the zone.
        internal sealed class Input
        {
            public int PrefabHash;
            public WearLook Look;
            public List<Matrix4x4> Matrices;                 // piece TRS (position, rotation, ExpectedScale), no Definition.LocalOffset
            public List<MeshBaker.PieceIdentity> Ids;        // same index as Matrices
        }

        internal sealed class Request
        {
            public volatile bool Done;
            public List<MeshCacheStore.CachedBatchData> Near, Far;
            public List<MeshBaker.PieceIdentity> CoveredIds, DrawsNothingIds, PartialIds;
            public string Error;                             // null = the lists are valid
            // Debug figures for the caller's line (the worker never logs)
            public double MainMs, WorkerMs;
            public int Pieces, Keys, MaterialisedKeys, NearVertices, FarVertices, NewPrefabLooks, NewMeshes;
        }

        // ---- per (prefab, look): what a copy showing that look feeds the combine, read from the asset once ------------------------------------------

        private struct BatchKey : IEquatable<BatchKey>
        {
            public int MaterialId;
            public byte Shadow, Receive, LightProbe, Reflection, Motion;
            public bool Equals(BatchKey o) => MaterialId == o.MaterialId && Shadow == o.Shadow && Receive == o.Receive && LightProbe == o.LightProbe && Reflection == o.Reflection && Motion == o.Motion;
            public override bool Equals(object obj) => obj is BatchKey o && Equals(o);
            public override int GetHashCode() { unchecked { int h = MaterialId; h = h * 31 + Shadow; h = h * 31 + Receive; h = h * 31 + LightProbe; h = h * 31 + Reflection; h = h * 31 + Motion; return h; } }
        }

        // One submesh of an asset mesh as plain arrays (its own vertex range, indices rebased to it). Immutable once built: the worker only reads it.
        private sealed class MeshPart
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;    // null when the mesh has none
            public Vector4[] Tangents;   // null when the mesh has none
            public Vector2[] Uvs;        // null when the mesh has none
            public int[] Indices;        // triangles, relative to this part's first vertex
            public bool Wide;            // the source mesh used 32-bit indices
        }

        private sealed class Part
        {
            public BatchKey Key;
            public MeshPart Mesh;
            public Matrix4x4 Local;      // the renderer's matrix in the prefab root's space
        }

        private sealed class AssetPiece
        {
            public readonly List<Part> Near = new List<Part>();
            public readonly List<Part> Far = new List<Part>();
            public readonly List<BatchKey> ContributedKeys = new List<BatchKey>();   // one per near part, as MeshBaker's ContributedBatchKeys
            public int NearCandidates;
            public bool HadIneligibleEnabled, FarGeometryUnusable;
            public bool AnyContribution => ContributedKeys.Count > 0;
            public bool DrawsNothing => NearCandidates == 0 && !HadIneligibleEnabled;
        }

        private static readonly Dictionary<long, AssetPiece> s_assetPieces = new Dictionary<long, AssetPiece>();         // (prefab hash, look bits)
        private static readonly Dictionary<long, MeshPart> s_meshParts = new Dictionary<long, MeshPart>();              // (mesh instance id, submesh)
        private static readonly Dictionary<int, string> s_materialNames = new Dictionary<int, string>();               // material instance id -> cache key
        private static readonly List<Material> s_materials = new List<Material>();
        private static readonly HashSet<Renderer> s_lod0 = new HashSet<Renderer>(), s_lodControlled = new HashSet<Renderer>(), s_far = new HashSet<Renderer>();

        // A new world: the asset tables are per session of a world only because modded prefabs can differ between logins.
        internal static void Reset()
        {
            s_assetPieces.Clear(); s_meshParts.Clear(); s_materialNames.Clear();
        }

        // 1.2.389 ([lead], [seasons]: Tools\EBM_1388_managed_heap_stores_20261115.md; with [Cache] ZdoFirstBake on by default since 1.2.387 these copies of every
        // combined prefab's mesh data, 48 B a vertex + 4 B an index, stayed until world close). [Cache] ZdoCombineIdleRelease, read live: after IdleReleaseSeconds
        // without a use (AssetPieceOf) the two asset tables are cleared. They are a cache: the next ZDO bake, verify or part count re-reads what it needs. A job
        // still on its worker keeps its own references to the parts it placed, so nothing it reads goes away. Main thread only, as every other access here.
        private const float IdleReleaseSeconds = 60f;
        private static float s_lastUse;
        private static int s_idleReleases; private static long s_idleReleasedBytes;
        private static bool IdleReleaseOn => FiresEasyBakeMeshesPlugin.CacheZdoCombineIdleRelease != null && FiresEasyBakeMeshesPlugin.CacheZdoCombineIdleRelease.Value;

        internal static void ReleaseIdle(float now)
        {
            if (!IdleReleaseOn || (s_meshParts.Count == 0 && s_assetPieces.Count == 0) || now - s_lastUse < IdleReleaseSeconds) return;
            s_idleReleasedBytes += HeapBytes(out _);
            s_idleReleases++;
            s_assetPieces.Clear(); s_meshParts.Clear();
        }

        /// <summary>1.2.389: the managed arrays the mesh-part table holds now (the [Heap] census).</summary>
        internal static long HeapBytes(out int parts)
        {
            parts = 0;
            long b = 0;
            foreach (var p in s_meshParts.Values)
            {
                if (p == null) continue;
                parts++;
                b += (p.Vertices?.Length ?? 0) * 12L + (p.Normals?.Length ?? 0) * 12L + (p.Tangents?.Length ?? 0) * 16L + (p.Uvs?.Length ?? 0) * 8L + (p.Indices?.Length ?? 0) * 4L;
            }
            return b;
        }

        internal static string HeapSummary(long bytes, int parts)
        {
            return $"ZDO bake asset mesh copies {bytes / 1048576.0:0.0} MB ({parts} mesh part(s), {s_assetPieces.Count} prefab look(s)); idle release {(IdleReleaseOn ? "on" : "off")}: {s_idleReleases} time(s), {s_idleReleasedBytes / 1048576.0:0.0} MB; ";
        }

        private static AssetPiece AssetPieceOf(int prefabHash, WearLook look, Request req)
        {
            s_lastUse = Time.realtimeSinceStartup;   // 1.2.389: any use keeps the tables (ReleaseIdle)
            long id = ((long)prefabHash << 8) | look.Bits;
            if (s_assetPieces.TryGetValue(id, out var piece)) return piece;
            piece = new AssetPiece();
            s_assetPieces[id] = piece;
            req.NewPrefabLooks++;
            var prefab = SafePrefab.Get(prefabHash);
            if (prefab == null) { piece.HadIneligibleEnabled = true; return piece; }   // unknown prefab: never covered, the piece stays live
            SnowFeed.SwapPrefabMaterials(prefab);   // as InstanceDefinition.TryBuild: the materials a live copy would carry

            bool tintOnly = prefab.GetComponentInChildren<RandomMaterialValues>(true) != null;
            s_lod0.Clear(); s_lodControlled.Clear(); s_far.Clear();
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lods = lodGroups[g] != null ? lodGroups[g].GetLODs() : null;
                if (lods == null || lods.Length == 0) continue;
                for (int li = 0; li < lods.Length; li++)
                {
                    var rs = lods[li].renderers;
                    if (rs == null) continue;
                    for (int ri = 0; ri < rs.Length; ri++)
                    {
                        if (rs[ri] == null) continue;
                        s_lodControlled.Add(rs[ri]);
                        if (li == 0) s_lod0.Add(rs[ri]);
                    }
                }
                int farSlot = HasAnyRenderer(lods, 1) ? 1 : 0;
                var farRenderers = lods[farSlot].renderers;
                if (farRenderers == null) continue;
                for (int ri = 0; ri < farRenderers.Length; ri++) if (farRenderers[ri] != null) s_far.Add(farRenderers[ri]);
            }

            Matrix4x4 rootInverse = prefab.transform.worldToLocalMatrix;
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                var mr = renderers[i];
                if (mr == null || !WearLooks.IsShown(prefab, look, mr.transform)) continue;   // the live walk keeps activeInHierarchy renderers only
                bool isLodControlled = s_lodControlled.Contains(mr);
                bool shown = mr.enabled || Indirect.Coverage.CoveredObject.HiddenByCoverage(mr);
                bool inNearTier = !isLodControlled || s_lod0.Contains(mr);
                bool inFarTier = isLodControlled ? s_far.Contains(mr) : shown;
                Matrix4x4 local = rootInverse * mr.transform.localToWorldMatrix;

                if (inNearTier && shown)
                {
                    piece.NearCandidates++;
                    if (!AppendParts(mr, tintOnly, local, piece.Near, piece.ContributedKeys, req)) piece.HadIneligibleEnabled = true;
                }
                if (inFarTier)
                {
                    if (!AppendParts(mr, tintOnly, local, piece.Far, null, req)) piece.FarGeometryUnusable = true;
                }
            }
            s_lod0.Clear(); s_lodControlled.Clear(); s_far.Clear();
            return piece;
        }

        private static bool HasAnyRenderer(LOD[] lods, int slot)
        {
            if (slot >= lods.Length) return false;
            var renderers = lods[slot].renderers;
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++) if (renderers[i] != null) return true;
            return false;
        }

        // MeshBaker.AppendContributions on an asset renderer. False = geometry that can never be baked (the piece is then never fully covered).
        private static bool AppendParts(MeshRenderer mr, bool tintOnly, Matrix4x4 local, List<Part> into, List<BatchKey> recordKeys, Request req)
        {
            if (mr.HasPropertyBlock() && !tintOnly) return false;
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return true;
            var mesh = mf.sharedMesh;
            if (!mesh.isReadable) return false;

            mr.GetSharedMaterials(s_materials);
            int subCount = Mathf.Min(mesh.subMeshCount, s_materials.Count);
            for (int s = 0; s < subCount; s++)
            {
                var material = s_materials[s];
                if (material == null) continue;
                material = MaterialVariants.SharedSource(mr, material);
                int materialId = material.GetInstanceID();
                if (!s_materialNames.ContainsKey(materialId))
                {
                    s_materialNames[materialId] = MeshCacheStore.MaterialKey(material);
                    MeshCacheStore.RegisterAssetMaterial(material);   // so the file's name resolves when the zone is built from it
                }
                var key = new BatchKey
                {
                    MaterialId = materialId,
                    Shadow = (byte)mr.shadowCastingMode,
                    Receive = mr.receiveShadows ? (byte)1 : (byte)0,
                    LightProbe = (byte)mr.lightProbeUsage,
                    Reflection = (byte)mr.reflectionProbeUsage,
                    Motion = (byte)mr.motionVectorGenerationMode,
                };
                var part = MeshPartOf(mesh, s, req);
                if (part == null) continue;   // an empty submesh: nothing to draw
                if (recordKeys != null) recordKeys.Add(key);
                into.Add(new Part { Key = key, Mesh = part, Local = local });
            }
            s_materials.Clear();
            return true;
        }

        private static MeshPart MeshPartOf(Mesh mesh, int subMesh, Request req)
        {
            long id = ((long)mesh.GetInstanceID() << 8) | (uint)(subMesh & 0xFF);
            if (s_meshParts.TryGetValue(id, out var part)) return part;
            req.NewMeshes++;
            part = null;
            var desc = mesh.GetSubMesh(subMesh);
            if (desc.topology == MeshTopology.Triangles && desc.indexCount >= 3 && desc.vertexCount > 0)
            {
                int first = desc.firstVertex, count = desc.vertexCount;
                var allVertices = mesh.vertices; var allNormals = mesh.normals; var allTangents = mesh.tangents; var allUvs = mesh.uv;
                int[] indices = mesh.GetIndices(subMesh);   // base vertex applied
                bool inRange = first + count <= allVertices.Length;
                for (int i = 0; i < indices.Length && inRange; i++) if (indices[i] < first || indices[i] >= first + count) inRange = false;
                if (!inRange) { first = 0; count = allVertices.Length; }   // a descriptor that does not bound its indices: take the whole vertex buffer
                part = new MeshPart
                {
                    Vertices = Slice(allVertices, first, count),
                    Normals = allNormals != null && allNormals.Length == allVertices.Length ? Slice(allNormals, first, count) : null,
                    Tangents = allTangents != null && allTangents.Length == allVertices.Length ? Slice(allTangents, first, count) : null,
                    Uvs = allUvs != null && allUvs.Length == allVertices.Length ? Slice(allUvs, first, count) : null,
                    Indices = indices,
                    Wide = mesh.indexFormat == IndexFormat.UInt32,
                };
                if (first != 0) for (int i = 0; i < indices.Length; i++) indices[i] -= first;
            }
            s_meshParts[id] = part;
            return part;
        }

        private static T[] Slice<T>(T[] source, int first, int count)
        {
            var slice = new T[count];
            Array.Copy(source, first, slice, 0, count);
            return slice;
        }

        // 1.2.375 ([visual], [lead] via [seasons]: a TRUE batch parity for town zones). One piece the live bake merged but the ZDO file leaves live
        // (a bake-safe prefab that is not skippable), as the verifier names it.
        internal struct LeftLive
        {
            public int PrefabHash;
            public WearLook Look;
            public int Count;
        }

        // What ONE copy of a prefab at a look feeds each batch of a tier: the batch's parity key (MeshCacheStore.BatchParity's: material key + renderer
        // flags) with the vertices and indices it adds. The far tier answers with the piece's own far geometry when it has usable one, else its near
        // geometry (the live bake also falls back when a far batch did not materialise; that case shows as a count difference, named in the report).
        internal static void PartCounts(int prefabHash, WearLook look, bool farTier, List<KeyValuePair<string, long[]>> into)
        {
            var piece = AssetPieceOf(prefabHash, look, s_scratchRequest);
            var parts = farTier && !piece.FarGeometryUnusable && piece.Far.Count > 0 ? piece.Far : piece.Near;
            for (int i = 0; i < parts.Count; i++)
            {
                var key = parts[i].Key;
                s_materialNames.TryGetValue(key.MaterialId, out string name);
                string parity = (name ?? "") + "|" + (int)key.Shadow + (key.Receive != 0 ? "r" : "-") + (int)key.LightProbe + (int)key.Reflection + (int)key.Motion;
                into.Add(new KeyValuePair<string, long[]>(parity, new long[] { parts[i].Mesh.Vertices.Length, parts[i].Mesh.Indices.Length }));
            }
        }
        private static readonly Request s_scratchRequest = new Request();   // AssetPieceOf counts new assets on a request; these lookups have none

        // 1.2.377: a renderer the look draws near that the combiner can never take (MeshBaker.WhyCombinerRefuses' test on the asset: a non-readable
        // mesh; an asset renderer carries no runtime property block). An unknown prefab counts as refused (it is never combined).
        internal static bool AssetRefuses(int prefabHash, WearLook look) => AssetPieceOf(prefabHash, look, s_scratchRequest).HadIneligibleEnabled;

        // ---- per zone ------------------------------------------------------------------------------------------------------------------------------

        private sealed class Placed { public Part Part; public Matrix4x4 Piece; }

        private sealed class Job
        {
            public Request Request;
            public readonly Dictionary<BatchKey, List<Placed>> Near = new Dictionary<BatchKey, List<Placed>>();
            public readonly Dictionary<BatchKey, List<Placed>> Far = new Dictionary<BatchKey, List<Placed>>();
            public readonly Dictionary<BatchKey, string> Names = new Dictionary<BatchKey, string>();
        }

        // Main thread: reads what is new of the assets, decides which batches materialise and which pieces they cover, then hands the array work to a
        // worker. The returned request's id lists are final at once; Near / Far are valid when Done is true and Error is null.
        internal static Request Start(Vector2s coord, List<Input> inputs)
        {
            var req = new Request
            {
                Near = new List<MeshCacheStore.CachedBatchData>(),
                Far = new List<MeshCacheStore.CachedBatchData>(),
                CoveredIds = new List<MeshBaker.PieceIdentity>(),
                DrawsNothingIds = new List<MeshBaker.PieceIdentity>(),
                PartialIds = new List<MeshBaker.PieceIdentity>(),
            };
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (inputs == null || inputs.Count == 0) { req.Done = true; return req; }
                int minPerBatch = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerBatch.Value;

                // 1. every input's asset piece, and the zone's near-part count per batch key (MeshBaker.Bake :298-311 counts every piece's near parts)
                var pieces = new AssetPiece[inputs.Count];
                var counts = new Dictionary<BatchKey, int>();
                for (int n = 0; n < inputs.Count; n++)
                {
                    var input = inputs[n];
                    int copies = input != null && input.Matrices != null ? input.Matrices.Count : 0;
                    if (copies == 0) continue;
                    var piece = AssetPieceOf(input.PrefabHash, input.Look, req);
                    pieces[n] = piece;
                    req.Pieces += copies;
                    for (int k = 0; k < piece.Near.Count; k++)
                    {
                        counts.TryGetValue(piece.Near[k].Key, out int have);
                        counts[piece.Near[k].Key] = have + copies;
                    }
                }
                req.Keys = counts.Count;
                var materialised = new HashSet<BatchKey>();
                foreach (var kv in counts) if (kv.Value >= minPerBatch) materialised.Add(kv.Key);
                req.MaterialisedKeys = materialised.Count;

                // 2. the coverage gate per input (every copy of a prefab + look has the same parts), and the covered pieces' parts placed per key
                var job = new Job { Request = req };
                for (int n = 0; n < inputs.Count; n++)
                {
                    var piece = pieces[n];
                    if (piece == null) continue;
                    var input = inputs[n];
                    bool covered = !piece.HadIneligibleEnabled;
                    for (int k = 0; k < piece.ContributedKeys.Count && covered; k++) if (!materialised.Contains(piece.ContributedKeys[k])) covered = false;
                    bool drawn = covered && piece.AnyContribution;
                    var ids = drawn ? req.CoveredIds : piece.DrawsNothing ? req.DrawsNothingIds : req.PartialIds;
                    if (input.Ids != null) ids.AddRange(input.Ids);
                    if (!drawn) continue;

                    // the far tier keeps its own geometry only when it lands in batches that exist; else it is the near geometry (MeshBaker.Bake :346-372)
                    bool farUsable = !piece.FarGeometryUnusable && piece.Far.Count > 0;
                    for (int k = 0; k < piece.Far.Count && farUsable; k++) if (!materialised.Contains(piece.Far[k].Key)) farUsable = false;
                    var farParts = farUsable ? piece.Far : piece.Near;
                    for (int c = 0; c < input.Matrices.Count; c++)
                    {
                        Matrix4x4 m = input.Matrices[c];
                        Place(job.Near, piece.Near, m);
                        Place(job.Far, farParts, m);
                    }
                }
                foreach (var key in materialised) job.Names[key] = s_materialNames.TryGetValue(key.MaterialId, out string name) ? name : "";

                req.MainMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (job.Near.Count == 0 && job.Far.Count == 0) { req.Done = true; return req; }
                ThreadPool.QueueUserWorkItem(Work, job);
                return req;
            }
            catch (Exception ex)
            {
                req.Error = ex.GetType().Name + ": " + ex.Message;
                req.MainMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                req.Done = true;
                return req;
            }
        }

        private static void Place(Dictionary<BatchKey, List<Placed>> into, List<Part> parts, Matrix4x4 piece)
        {
            for (int k = 0; k < parts.Count; k++)
            {
                if (!into.TryGetValue(parts[k].Key, out var list)) { list = new List<Placed>(); into.Add(parts[k].Key, list); }
                list.Add(new Placed { Part = parts[k], Piece = piece });
            }
        }

        // ---- worker: arrays only, no UnityEngine object is touched (Vector / Matrix structs and managed maths only) ---------------------------------

        private static void Work(object state)
        {
            var job = (Job)state;
            var req = job.Request;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                foreach (var kv in job.Near) { var batch = Combine(kv.Key, kv.Value, job.Names); req.Near.Add(batch); req.NearVertices += batch.Vertices.Length; }
                foreach (var kv in job.Far) { var batch = Combine(kv.Key, kv.Value, job.Names); req.Far.Add(batch); req.FarVertices += batch.Vertices.Length; }
            }
            catch (Exception ex)
            {
                req.Error = ex.GetType().Name + ": " + ex.Message;
            }
            req.WorkerMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            req.Done = true;
        }

        private static MeshCacheStore.CachedBatchData Combine(BatchKey key, List<Placed> placed, Dictionary<BatchKey, string> names)
        {
            int vertexTotal = 0, indexTotal = 0;
            bool anyNormals = false, anyTangents = false, anyUvs = false, wide = false;
            for (int i = 0; i < placed.Count; i++)
            {
                var mesh = placed[i].Part.Mesh;
                vertexTotal += mesh.Vertices.Length; indexTotal += mesh.Indices.Length;
                anyNormals |= mesh.Normals != null; anyTangents |= mesh.Tangents != null; anyUvs |= mesh.Uvs != null; wide |= mesh.Wide;
            }
            if (vertexTotal > 65000) wide = true;   // MeshBaker.NeedsUInt32

            var vertices = new Vector3[vertexTotal];
            var normals = anyNormals ? new Vector3[vertexTotal] : null;      // a source without the channel leaves zeros, as Mesh.CombineMeshes does
            var tangents = anyTangents ? new Vector4[vertexTotal] : null;
            var uvs = anyUvs ? new Vector2[vertexTotal] : null;
            var indices = new int[indexTotal];

            int v0 = 0, i0 = 0;
            for (int p = 0; p < placed.Count; p++)
            {
                var mesh = placed[p].Part.Mesh;
                Matrix4x4 m = placed[p].Piece * placed[p].Part.Local;
                // the upper 3x3, its cofactors (= determinant x inverse transpose: the normal matrix without a division) and its determinant
                float a = m.m00, b = m.m01, c = m.m02, d = m.m10, e = m.m11, f = m.m12, g = m.m20, h = m.m21, k = m.m22;
                float c00 = e * k - f * h, c01 = f * g - d * k, c02 = d * h - e * g;
                float c10 = c * h - b * k, c11 = a * k - c * g, c12 = b * g - a * h;
                float c20 = b * f - c * e, c21 = c * d - a * f, c22 = a * e - b * d;
                float det = a * c00 + b * c01 + c * c02;
                bool mirrored = det < 0f;
                float sign = mirrored ? -1f : 1f;

                var src = mesh.Vertices;
                for (int i = 0; i < src.Length; i++)
                {
                    Vector3 s = src[i];
                    vertices[v0 + i] = new Vector3(a * s.x + b * s.y + c * s.z + m.m03, d * s.x + e * s.y + f * s.z + m.m13, g * s.x + h * s.y + k * s.z + m.m23);
                }
                if (mesh.Normals != null)
                {
                    var sn = mesh.Normals;
                    for (int i = 0; i < sn.Length; i++)
                    {
                        Vector3 s = sn[i];
                        float x = (c00 * s.x + c01 * s.y + c02 * s.z) * sign, y = (c10 * s.x + c11 * s.y + c12 * s.z) * sign, z = (c20 * s.x + c21 * s.y + c22 * s.z) * sign;
                        float len = (float)Math.Sqrt(x * x + y * y + z * z);
                        normals[v0 + i] = len > 1e-12f ? new Vector3(x / len, y / len, z / len) : s;
                    }
                }
                if (mesh.Tangents != null)
                {
                    var st = mesh.Tangents;
                    for (int i = 0; i < st.Length; i++)
                    {
                        Vector4 s = st[i];
                        float x = a * s.x + b * s.y + c * s.z, y = d * s.x + e * s.y + f * s.z, z = g * s.x + h * s.y + k * s.z;
                        float len = (float)Math.Sqrt(x * x + y * y + z * z);
                        tangents[v0 + i] = len > 1e-12f ? new Vector4(x / len, y / len, z / len, mirrored ? -s.w : s.w) : s;
                    }
                }
                if (mesh.Uvs != null) Array.Copy(mesh.Uvs, 0, uvs, v0, mesh.Uvs.Length);

                var si = mesh.Indices;
                if (!mirrored) for (int i = 0; i < si.Length; i++) indices[i0 + i] = si[i] + v0;
                else for (int i = 0; i + 2 < si.Length; i += 3) { indices[i0 + i] = si[i] + v0; indices[i0 + i + 1] = si[i + 2] + v0; indices[i0 + i + 2] = si[i + 1] + v0; }   // a mirrored piece: winding flipped

                v0 += src.Length; i0 += si.Length;
            }

            names.TryGetValue(key, out string name);
            return new MeshCacheStore.CachedBatchData
            {
                MaterialName = name ?? "",
                SourceMeshId = 0,
                Vertices = vertices,
                Normals = normals,
                Tangents = tangents,
                Uvs = uvs,
                Indices = indices,
                IndexFormatUInt32 = wide,
                ShadowCastingMode = key.Shadow,
                ReceiveShadows = key.Receive,
                LightProbeUsage = key.LightProbe,
                ReflectionProbeUsage = key.Reflection,
                MotionVectorMode = key.Motion,
            };
        }
    }
}
