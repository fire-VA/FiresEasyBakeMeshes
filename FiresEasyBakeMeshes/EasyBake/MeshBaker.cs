using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    internal static class MeshBaker
    {
        internal const string FarTierNameSuffix = "_far";

        // Result of one Bake() call. Holds everything Restore() needs to reverse,
        // plus the data we need to reattach the cache on zone reload.
        //
        // Lifecycle:
        //   - Bake() fills Parent, Batches, SuppressedPieces, ContributorIdentities.
        //   - ZoneTracker keeps the BakeResult alive across zone-unload/reload
        //     cycles. On unload it clears SuppressedPieces (their references go
        //     Unity-null when ZNetScene destroys the pieces) and hides Parent via
        //     SetActive(false). On reload, new piece instances stream in via
        //     OnInstanceCreated, and DisableForCacheHit re-populates
        //     SuppressedPieces against the fresh references.
        //   - Restore() is only called for full teardown: dirty rebake or genuine
        //     "base destroyed" detection. It destroys Parent + mesh assets.
        internal class BakeResult
        {
            public GameObject Parent;
            // Near tier draws while the viewer is inside the switch radius; far tier
            // is the same coverage rebuilt from each piece's LOD1 geometry. Exactly
            // one tier is active at a time, so draw-call count never changes.
            public List<BatchInstance> Batches = new List<BatchInstance>();
            public List<BatchInstance> FarBatches = new List<BatchInstance>();
            public bool ShowingFarTier;
            public List<EasyBakeSuppressedVisuals> SuppressedPieces = new List<EasyBakeSuppressedVisuals>();
            // Identity of every piece that fed at least one batch. Used on zone
            // reload to recognise the same pieces returning and disable their
            // renderers without rebaking.
            public HashSet<PieceIdentity> ContributorIdentities;
            // Uniform single-mesh prefabs drawn as GPU instances instead of being merged
            // into a combined mesh: no vertex duplication, and a removed piece drops in
            // O(1) where the combine path has to rebake the whole zone.
            public List<ZoneInstanceGroup> InstanceGroups = new List<ZoneInstanceGroup>();
            // Where each contributor sits, so stand-in colliders can take the place of pieces that are never created.
            public Dictionary<PieceIdentity, PieceTransform> PieceTransforms = new Dictionary<PieceIdentity, PieceTransform>();

            // A zone renders if it produced combined batches, instance groups, or both.
            // An instance-only zone has no combined mesh and therefore no Parent, which is
            // not the same as having baked nothing.
            public bool HasRenderableContent =>
                (Batches != null && Batches.Count > 0) || (InstanceGroups != null && InstanceGroups.Count > 0);
        }

        internal class BatchInstance
        {
            public GameObject Combined;
            public Mesh Mesh;
        }

        // Every renderer setting a batch flattens onto its merged output. Grouping by
        // the whole set means a batch can only contain renderers that already agree on
        // all of it; keying on Material alone let a zone mixing ShadowsOnly and On
        // pieces hand the entire batch whichever mode the first contributor happened
        // to have.
        private struct BatchKey : IEquatable<BatchKey>
        {
            public Material Material;
            public UnityEngine.Rendering.ShadowCastingMode ShadowCastingMode;
            public bool ReceiveShadows;
            public UnityEngine.Rendering.LightProbeUsage LightProbeUsage;
            public UnityEngine.Rendering.ReflectionProbeUsage ReflectionProbeUsage;
            public MotionVectorGenerationMode MotionVectorMode;

            public static BatchKey From(MeshRenderer renderer, Material material)
            {
                return new BatchKey
                {
                    Material = material,
                    ShadowCastingMode = renderer.shadowCastingMode,
                    ReceiveShadows = renderer.receiveShadows,
                    LightProbeUsage = renderer.lightProbeUsage,
                    ReflectionProbeUsage = renderer.reflectionProbeUsage,
                    MotionVectorMode = renderer.motionVectorGenerationMode,
                };
            }

            public void ApplyTo(MeshRenderer renderer)
            {
                renderer.sharedMaterial = Material;
                renderer.shadowCastingMode = ShadowCastingMode;
                renderer.receiveShadows = ReceiveShadows;
                renderer.lightProbeUsage = LightProbeUsage;
                renderer.reflectionProbeUsage = ReflectionProbeUsage;
                renderer.motionVectorGenerationMode = MotionVectorMode;
            }

            public string BuildBatchName() =>
                $"{(Material != null ? Material.name : "NoMaterial")}_{ShadowCastingMode}";

            public bool Equals(BatchKey other) =>
                ReferenceEquals(Material, other.Material)
                && ShadowCastingMode == other.ShadowCastingMode
                && ReceiveShadows == other.ReceiveShadows
                && LightProbeUsage == other.LightProbeUsage
                && ReflectionProbeUsage == other.ReflectionProbeUsage
                && MotionVectorMode == other.MotionVectorMode;

            public override bool Equals(object obj) => obj is BatchKey key && Equals(key);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Material != null ? Material.GetHashCode() : 0;
                    hash = (hash * 397) ^ (int)ShadowCastingMode;
                    hash = (hash * 397) ^ (ReceiveShadows ? 1 : 0);
                    hash = (hash * 397) ^ (int)LightProbeUsage;
                    hash = (hash * 397) ^ (int)ReflectionProbeUsage;
                    hash = (hash * 397) ^ (int)MotionVectorMode;
                    return hash;
                }
            }
        }

        // Identity used to recognise "the same piece coming back" across a zone
        // unload/reload cycle: prefab hash, world position quantised to 1 cm and a
        // rotation key, all read from the piece's ZDO so every session computes the
        // same values. Pieces in a Valheim base don't move; an admin teleport that
        // nudges one makes it a "new" piece, which is the correct outcome. Builders
        // stack copies of a piece on one spot turned for looks (2,832 on the SDG rig),
        // and without the rotation those copies shared an identity and overwrote each
        // other's transform, which read as a change and rebaked the zone in a loop.
        internal struct PieceIdentity : IEquatable<PieceIdentity>
        {
            public int PrefabHash;
            public int X;
            public int Y;
            public int Z;
            public int R;

            public bool Equals(PieceIdentity o) => PrefabHash == o.PrefabHash && X == o.X && Y == o.Y && Z == o.Z && R == o.R;
            public override bool Equals(object obj) => obj is PieceIdentity p && Equals(p);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = PrefabHash;
                    h = (h * 397) ^ X;
                    h = (h * 397) ^ Y;
                    h = (h * 397) ^ Z;
                    h = (h * 397) ^ R;
                    return h;
                }
            }

            public static PieceIdentity From(GameObject go, Vector3 worldPos)
            {
                var view = go != null ? go.GetComponent<ZNetView>() : null;
                var zdo = view != null ? view.GetZDO() : null;
                if (zdo != null) return From(zdo);
                return From(0, worldPos, go != null ? go.transform.rotation : Quaternion.identity);
            }

            public static PieceIdentity From(ZDO zdo) => From(zdo.GetPrefab(), zdo.GetPosition(), zdo.GetRotation());

            public static PieceIdentity From(int prefabHash, Vector3 worldPos, Quaternion rotation)
            {
                return new PieceIdentity
                {
                    PrefabHash = prefabHash,
                    X = Mathf.RoundToInt(worldPos.x * 100f),
                    Y = Mathf.RoundToInt(worldPos.y * 100f),
                    Z = Mathf.RoundToInt(worldPos.z * 100f),
                    R = RotationKey(rotation),
                };
            }

            // q and -q are the same rotation, so the sign is fixed before the components are quantised.
            private static int RotationKey(Quaternion q)
            {
                if (q.w < 0f || (q.w == 0f && (q.z < 0f || (q.z == 0f && (q.y < 0f || (q.y == 0f && q.x < 0f))))))
                {
                    q.x = -q.x;
                    q.y = -q.y;
                    q.z = -q.z;
                    q.w = -q.w;
                }
                unchecked
                {
                    int key = Mathf.RoundToInt(q.x * 1000f);
                    key = key * 2003 + Mathf.RoundToInt(q.y * 1000f);
                    key = key * 2003 + Mathf.RoundToInt(q.z * 1000f);
                    return key * 2003 + Mathf.RoundToInt(q.w * 1000f);
                }
            }
        }

        internal struct PieceTransform
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;

            public static PieceTransform From(Transform transform) => new PieceTransform
            {
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = transform.localScale,
            };
        }

        private struct MeshContribution
        {
            public BatchKey Key;
            public Mesh Mesh;
            public int SubMeshIndex;
            public Matrix4x4 LocalToWorld;
            public bool NeedsUInt32;
            public PieceLodInfo Piece;
        }

        // Per-piece bookkeeping populated during CollectContributions.
        //   - ContributedBatchKeys: every batch this piece fed. The piece is only safe
        //     to suppress if all of them actually materialised.
        //   - HadIneligibleEnabled: an ENABLED renderer was skipped for a reason its
        //     geometry can never be baked (MaterialPropertyBlock or non-readable mesh).
        //     A brazier's emissive coal-bowl uses a property block — this is the flag
        //     that stops us hiding it. Such a piece can NEVER be fully covered.
        //   - FullyCovered: set after batching. Only fully-covered pieces get their
        //     originals hidden; partial pieces stay entirely vanilla so nothing vanishes.
        //   - FarContributions: the same coverage drawn from LOD1 geometry. Falls back
        //     to NearContributions whenever LOD1 can't reproduce this piece into the
        //     batches it already feeds, so the far tier can never drop a piece.
        //   - FarGeometryUnusable: an LOD1 renderer couldn't be baked at all.
        private class PieceLodInfo
        {
            public WearNTear Source;
            public List<BatchKey> ContributedBatchKeys = new List<BatchKey>();
            public List<MeshContribution> NearContributions = new List<MeshContribution>();
            public List<MeshContribution> FarContributions = new List<MeshContribution>();
            public bool HadIneligibleEnabled;
            // RandomMaterialValues only tints through property blocks; merging drops the tint, as instancing already does.
            public bool TintOnlyPropertyBlocks;
            public bool FarGeometryUnusable;
            public bool FullyCovered;
            public bool AnyContribution => ContributedBatchKeys.Count > 0;
        }

        public static BakeResult Bake(Vector2s coord, HashSet<WearNTear> pieces)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int candidates = 0, skippedNonLod0 = 0, skippedPropertyBlock = 0, skippedNonReadable = 0;

            var result = new BakeResult();
            var instancedPieces = BuildInstanceGroups(pieces, result.InstanceGroups);

            var pieceInfos = new List<PieceLodInfo>(pieces.Count);
            foreach (var wnt in pieces)
            {
                if (wnt == null || wnt.gameObject == null) continue;
                if (instancedPieces.Contains(wnt)) continue;
                // A damageable piece can only leave an instanced group; merged geometry would need a rebake per hit.
                if (!InvulnerableClassifier.IsInvulnerable(wnt)) continue;
                var info = new PieceLodInfo { Source = wnt, TintOnlyPropertyBlocks = wnt.GetComponentInChildren<RandomMaterialValues>(true) != null };
                CollectContributions(wnt.gameObject, info,
                    ref candidates, ref skippedNonLod0, ref skippedPropertyBlock, ref skippedNonReadable);
                pieceInfos.Add(info);
            }

            var byBatchKey = new Dictionary<BatchKey, List<MeshContribution>>();
            for (int p = 0; p < pieceInfos.Count; p++)
                GroupContributions(pieceInfos[p].NearContributions, byBatchKey);

            int minPerBatch = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerBatch.Value;
            int totalContributions = 0;

            // Which batches actually cleared the min-per-batch bar. Computed once
            // (counting every contribution) and reused for both the batch loop and
            // the coverage test below.
            var materialized = new HashSet<BatchKey>();
            foreach (var kv in byBatchKey)
                if (kv.Value.Count >= minPerBatch) materialized.Add(kv.Key);

            // Coverage gate — the fix for "floating fire coals". A piece is only safe
            // to hide if EVERY part of it made it into a batch. Two ways a part can be
            // left un-baked yet the piece still fed some other batch:
            //   1. HadIneligibleEnabled — an enabled renderer was skipped outright
            //      (MaterialPropertyBlock, e.g. a brazier's emissive coal-bowl, or a
            //      non-readable mesh). Its geometry is in NO batch.
            //   2. A batch it contributed to didn't clear minPerBatch, so that batch
            //      never materialised.
            // In either case the piece is NOT fully covered: we must leave it entirely
            // vanilla (all renderers keep drawing) rather than hide a part that nothing
            // redraws. Only fully-covered pieces get baked + suppressed. This makes the
            // hide-set == the draw-set, the same all-or-nothing invariant PUP enforces.
            int partialPieces = 0;
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                bool covered = !info.HadIneligibleEnabled;
                if (covered)
                {
                    for (int i = 0; i < info.ContributedBatchKeys.Count; i++)
                    {
                        if (!materialized.Contains(info.ContributedBatchKeys[i])) { covered = false; break; }
                    }
                }
                info.FullyCovered = covered;
                if (!covered && info.AnyContribution) partialPieces++;
            }

            var parent = new GameObject($"[EasyBake/Zone_{coord.x}_{coord.y}]");
            parent.transform.position = Vector3.zero;
            parent.isStatic = true;
            result.Parent = parent;

            // A covered piece keeps its LOD1 geometry for the far tier only if that
            // geometry lands in the very same batches its LOD0 geometry feeds. Anything
            // else — an unbakeable LOD1 renderer, or an LOD1 material that would open a
            // batch of its own — falls the whole piece back to its near geometry, so the
            // far tier is always a like-for-like stand-in and can never drop a piece or
            // swap its material at distance.
            int piecesWithFarGeometry = 0;
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                if (!info.FullyCovered || !info.AnyContribution) continue;

                bool farUsable = !info.FarGeometryUnusable && info.FarContributions.Count > 0;
                if (farUsable)
                {
                    for (int i = 0; i < info.FarContributions.Count; i++)
                    {
                        if (!materialized.Contains(info.FarContributions[i].Key)) { farUsable = false; break; }
                    }
                }

                if (farUsable) piecesWithFarGeometry++;
                else info.FarContributions = info.NearContributions;
            }

            // Bake ONLY geometry from fully-covered pieces — their originals are about
            // to be hidden. A partial piece's contributions are dropped here so they
            // don't double-draw with the vanilla renderers we leave on.
            var nearCovered = new Dictionary<BatchKey, List<MeshContribution>>();
            var farCovered = new Dictionary<BatchKey, List<MeshContribution>>();
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                if (!info.FullyCovered || !info.AnyContribution) continue;
                GroupContributions(info.NearContributions, nearCovered);
                GroupContributions(info.FarContributions, farCovered);
            }

            totalContributions = BuildTierBatches(coord, parent, materialized, nearCovered, result.Batches, farTier: false);
            BuildTierBatches(coord, parent, materialized, farCovered, result.FarBatches, farTier: true);

            // Unified suppression pass — runs once per Bake instead of per-material.
            // Every FULLY-COVERED piece (each of its parts is now in a batch) hands its
            // renderers and LODGroups to an EasyBakeSuppressedVisuals component, which
            // owns silencing them and restoring their exact prior state. Safe here
            // precisely because the piece is fully covered — nothing we hide is left
            // un-redrawn.
            //
            // We also record an identity (prefab hash + cm-quantised world pos) so that
            // when this zone unloads + reloads later, the new piece instances can be
            // matched against the cache and re-suppressed without rebaking.
            result.ContributorIdentities = new HashSet<PieceIdentity>();
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                if (!info.FullyCovered || !info.AnyContribution) continue;
                if (info.Source == null) continue;

                var identity = PieceIdentity.From(info.Source.gameObject, info.Source.transform.position);
                result.ContributorIdentities.Add(identity);
                result.PieceTransforms[identity] = PieceTransform.From(info.Source.transform);

                var suppression = EasyBakeSuppressedVisuals.SuppressPiece(info.Source.gameObject);
                if (suppression != null) result.SuppressedPieces.Add(suppression);
            }

            // Instanced pieces are suppressed on the same terms: their whole prefab is
            // now drawn by the instanced mesh, so hiding the original redraws nothing.
            foreach (var piece in instancedPieces)
            {
                if (piece == null || piece.gameObject == null) continue;
                var instancedIdentity = PieceIdentity.From(piece.gameObject, piece.transform.position);
                result.ContributorIdentities.Add(instancedIdentity);
                // Only invulnerable pieces get a stand-in transform, which is what skipping and stand-ins key on.
                if (InvulnerableClassifier.IsInvulnerable(piece))
                    result.PieceTransforms[instancedIdentity] = PieceTransform.From(piece.transform);
                var instancedSuppression = EasyBakeSuppressedVisuals.SuppressPiece(piece.gameObject);
                if (instancedSuppression != null) result.SuppressedPieces.Add(instancedSuppression);
            }

            sw.Stop();
            BakeSummary.RecordFreshBake(result.Batches.Count, sw.ElapsedMilliseconds);
            if ((result.Batches.Count > 0 && FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                || FiresEasyBakeMeshesPlugin.BatchingVerbose.Value)
            {
                EasyBakeLog.Info(
                    $"[Bake] Zone ({coord.x},{coord.y}): {pieces.Count} pieces, " +
                    $"{candidates} candidate renderers ({skippedNonLod0} nonLod0-skip, {skippedPropertyBlock} propBlock-skip, {skippedNonReadable} nonReadable-skip), " +
                    $"baked {result.Batches.Count} batches covering {totalContributions} contributions, " +
                    $"far tier {result.FarBatches.Count} batches ({piecesWithFarGeometry} pieces with real LOD1 geometry), " +
                    $"instanced {result.InstanceGroups.Count} prefab groups covering {instancedPieces.Count} pieces, " +
                    (result.InstanceGroups.Count == 0 ? $"instancing rejections: {InstanceDefinitionCache.DescribeRejections()}, " : "") +
                    $"suppressed {result.SuppressedPieces.Count} pieces, " +
                    $"left {partialPieces} partial pieces vanilla (un-bakeable parts), " +
                    $"cached {result.ContributorIdentities?.Count ?? 0} identities in {sw.ElapsedMilliseconds}ms.");
            }
            if (result.Batches.Count == 0)
            {
                UnityEngine.Object.Destroy(parent);
                result.Parent = null;
            }
            return result;
        }

        public static void Restore(BakeResult result)
        {
            if (result == null) return;

            if (result.SuppressedPieces != null)
            {
                for (int i = 0; i < result.SuppressedPieces.Count; i++)
                {
                    var suppression = result.SuppressedPieces[i];
                    if (suppression == null) continue;
                    suppression.RestoreVisuals();
                    UnityEngine.Object.Destroy(suppression);
                }
                result.SuppressedPieces.Clear();
            }

            DestroyBatches(result.Batches);
            DestroyBatches(result.FarBatches);
            if (result.Parent != null) UnityEngine.Object.Destroy(result.Parent);
        }

        // Called when a WearNTear comes back into a zone that has a cached
        // BakeResult AND whose PieceIdentity matches an entry in that cache. The
        // combined mesh is already drawing this piece's contribution; all we need
        // to do is silence the fresh source renderers/LODGroup the same way a
        // full bake would. Adds entries to the BakeResult's disable lists so that
        // a subsequent Restore() puts them back correctly.
        //
        // Cheap: one GetComponentsInChildren pair, no CombineMeshes work. This
        // is the whole point of caching — turning a 30-70ms per-zone bake into
        // a sub-millisecond per-piece reattach.
        public static void DisableForCacheHit(GameObject pieceRoot, BakeResult cache)
        {
            if (pieceRoot == null || cache == null) return;

            var suppression = EasyBakeSuppressedVisuals.SuppressPiece(pieceRoot);
            if (suppression != null) cache.SuppressedPieces.Add(suppression);
        }

        // Walk a piece's subtree and split its bakeable geometry into two tiers.
        //
        // Near tier — what draws up close:
        //   * Renderer is enabled (skip otherwise — vanilla had it off for a reason).
        //   * If the renderer is registered in ANY LODGroup's LOD array, it must be in
        //     the LOD0 slot. Combining every LOD would multi-count the piece.
        //   * If the renderer is NOT registered in any LODGroup (LOD-orphaned), we
        //     accept it. Modded prefabs sometimes have ad-hoc decorative renderers that
        //     live alongside a LODGroup without being part of it; those should bake.
        //
        // Far tier — the same coverage from each LODGroup's LOD1 slot, plus the
        // LOD-orphaned renderers, which have no cheaper variant to offer. Slot
        // renderers are taken as the group lists them; an orphan joins only while
        // enabled, as in the near tier, so a renderer vanilla switched off never
        // appears at distance. A group with no usable LOD1 contributes its LOD0
        // geometry instead, keeping the far tier a complete stand-in.
        //
        // Both tiers require: no MaterialPropertyBlock (per-renderer overrides don't
        // survive a merge) and a CPU-readable mesh (Mesh.CombineMeshes needs it).
        private static void CollectContributions(GameObject root, PieceLodInfo info,
            ref int candidates, ref int skippedNonLod0, ref int skippedPropertyBlock, ref int skippedNonReadable)
        {
            var lodGroups = root.GetComponentsInChildren<LODGroup>(includeInactive: true);

            HashSet<Renderer> lod0Set = null;
            HashSet<Renderer> lodControlled = null;
            HashSet<Renderer> farSet = null;
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lg = lodGroups[g];
                if (lg == null) continue;
                var lods = lg.GetLODs();
                if (lods == null || lods.Length == 0) continue;

                for (int li = 0; li < lods.Length; li++)
                {
                    var rs = lods[li].renderers;
                    if (rs == null) continue;
                    for (int ri = 0; ri < rs.Length; ri++)
                    {
                        var r = rs[ri];
                        if (r == null) continue;
                        if (lodControlled == null) lodControlled = new HashSet<Renderer>();
                        lodControlled.Add(r);
                        if (li == 0)
                        {
                            if (lod0Set == null) lod0Set = new HashSet<Renderer>();
                            lod0Set.Add(r);
                        }
                    }
                }

                int farSlot = HasAnyRenderer(lods, 1) ? 1 : 0;
                var farRenderers = lods[farSlot].renderers;
                if (farRenderers == null) continue;
                for (int ri = 0; ri < farRenderers.Length; ri++)
                {
                    if (farRenderers[ri] == null) continue;
                    if (farSet == null) farSet = new HashSet<Renderer>();
                    farSet.Add(farRenderers[ri]);
                }
            }

            var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var mr = renderers[i];
                if (mr == null) continue;

                bool isLodControlled = lodControlled != null && lodControlled.Contains(mr);
                bool inNearTier = !isLodControlled || (lod0Set != null && lod0Set.Contains(mr));
                bool inFarTier = isLodControlled ? farSet != null && farSet.Contains(mr) : mr.enabled;

                if (inNearTier && mr.enabled)
                {
                    candidates++;
                    // An enabled renderer we can't bake means this piece can never be
                    // fully covered — its geometry lands in no batch. Flag it so the
                    // coverage gate leaves the whole piece vanilla instead of hiding a
                    // part nothing redraws.
                    if (!AppendContributions(mr, info, info.NearContributions, info.ContributedBatchKeys,
                            ref skippedPropertyBlock, ref skippedNonReadable))
                        info.HadIneligibleEnabled = true;
                }
                else if (isLodControlled && !inNearTier && mr.enabled)
                {
                    candidates++;
                    skippedNonLod0++;
                }

                if (inFarTier)
                {
                    int ignoredPropertyBlock = 0, ignoredNonReadable = 0;
                    if (!AppendContributions(mr, info, info.FarContributions, null,
                            ref ignoredPropertyBlock, ref ignoredNonReadable))
                        info.FarGeometryUnusable = true;
                }
            }
        }

        private static bool HasAnyRenderer(LOD[] lods, int slot)
        {
            if (slot >= lods.Length) return false;
            var renderers = lods[slot].renderers;
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) return true;
            return false;
        }

        // Appends one renderer's per-submesh contributions. Returns false when the
        // renderer holds geometry that can never be baked, which is what the coverage
        // gate keys off. A renderer with no mesh contributes nothing but is not a
        // failure — there is no geometry to leave un-redrawn.
        private static bool AppendContributions(MeshRenderer mr, PieceLodInfo info,
            List<MeshContribution> into, List<BatchKey> recordKeysInto,
            ref int skippedPropertyBlock, ref int skippedNonReadable)
        {
            if (mr.HasPropertyBlock() && !info.TintOnlyPropertyBlocks) { skippedPropertyBlock++; return false; }

            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return true;
            var mesh = mf.sharedMesh;
            if (!mesh.isReadable) { skippedNonReadable++; return false; }

            var mats = mr.sharedMaterials;
            int subCount = Mathf.Min(mesh.subMeshCount, mats.Length);
            var localToWorld = mr.transform.localToWorldMatrix;
            bool meshNeeds32 = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32;

            for (int s = 0; s < subCount; s++)
            {
                var material = mats[s];
                if (material == null) continue;
                var batchKey = BatchKey.From(mr, material);
                if (recordKeysInto != null) recordKeysInto.Add(batchKey);
                into.Add(new MeshContribution
                {
                    Key = batchKey,
                    Mesh = mesh,
                    SubMeshIndex = s,
                    LocalToWorld = localToWorld,
                    NeedsUInt32 = meshNeeds32,
                    Piece = info,
                });
            }
            return true;
        }

        private static void DestroyBatches(List<BatchInstance> batches)
        {
            if (batches == null) return;
            foreach (var batch in batches)
            {
                if (batch.Combined != null) UnityEngine.Object.Destroy(batch.Combined);
                if (batch.Mesh != null) UnityEngine.Object.Destroy(batch.Mesh);
            }
        }

        // Swaps which tier's batch objects are active. Cheap and idempotent — the
        // caller only invokes it on an actual threshold crossing.
        public static void SetFarTierActive(BakeResult result, bool showFar)
        {
            if (result == null || result.ShowingFarTier == showFar) return;
            if (result.FarBatches == null || result.FarBatches.Count == 0) return;

            for (int i = 0; i < result.Batches.Count; i++)
                if (result.Batches[i].Combined != null) result.Batches[i].Combined.SetActive(!showFar);
            for (int i = 0; i < result.FarBatches.Count; i++)
                if (result.FarBatches[i].Combined != null) result.FarBatches[i].Combined.SetActive(showFar);

            result.ShowingFarTier = showFar;
        }

        // The per-zone minimum exists so a handful of pieces do not take their own draw call where the combine path would
        // batch them in with everything else. A prefab the combiner cannot take has no such alternative - its pieces either
        // join an instanced group or stay live objects - so any number of them is worth a group, as long as one of them is
        // invulnerable and can therefore also be skipped. A prefab can arrive as both kinds, so every candidate is checked.
        private static bool OnlyInstancingCanDraw(int prefabHash, List<WearNTear> candidates)
        {
            var sample = candidates[0];
            if (sample == null || sample.gameObject == null) return false;
            if (WhyCombinerRefuses(prefabHash, sample.gameObject) == null) return false;

            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i] != null && InvulnerableClassifier.IsInvulnerable(candidates[i])) return true;
            return false;
        }

        private static readonly Dictionary<int, string> _combinerRefusals = new Dictionary<int, string>();

        // Why Mesh.CombineMeshes could never take this prefab, or null when it can. Mirrors what CollectContributions
        // accepts in the near tier: a CPU-readable mesh on every enabled LOD0 renderer, and no per-object material values
        // unless the piece only tints itself. The census reports the same answer, so both read one rule.
        internal static string WhyCombinerRefuses(int prefabHash, GameObject root)
        {
            string refusal;
            if (_combinerRefusals.TryGetValue(prefabHash, out refusal)) return refusal;

            bool tintOnly = root.GetComponentInChildren<RandomMaterialValues>(true) != null;
            HashSet<Renderer> lodControlled = null, lod0 = null;
            var lodGroups = root.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lods = lodGroups[g] != null ? lodGroups[g].GetLODs() : null;
                if (lods == null) continue;
                for (int li = 0; li < lods.Length; li++)
                {
                    var slot = lods[li].renderers;
                    if (slot == null) continue;
                    for (int ri = 0; ri < slot.Length; ri++)
                    {
                        if (slot[ri] == null) continue;
                        (lodControlled ?? (lodControlled = new HashSet<Renderer>())).Add(slot[ri]);
                        if (li == 0) (lod0 ?? (lod0 = new HashSet<Renderer>())).Add(slot[ri]);
                    }
                }
            }

            bool propertyBlock = false, unreadable = false;
            var renderers = root.GetComponentsInChildren<MeshRenderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (lodControlled != null && lodControlled.Contains(renderer) && (lod0 == null || !lod0.Contains(renderer))) continue;
                if (renderer.HasPropertyBlock() && !tintOnly) propertyBlock = true;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null && !filter.sharedMesh.isReadable) unreadable = true;
            }

            refusal = unreadable ? "a mesh the combiner cannot read (only instancing could draw it)"
                : propertyBlock ? "a renderer with per-object material values"
                : null;
            _combinerRefusals[prefabHash] = refusal;
            return refusal;
        }

        // Partitions the zone's pieces: any prefab that is instanceable, and appears at least MinInstancesPerPrefab times
        // or cannot be combined at all, becomes a GPU-instanced group, and those pieces are held back so the combine path
        // skips them. Prefabs below the threshold fall through to combining, where they still batch by material with
        // everything else.
        private static HashSet<WearNTear> BuildInstanceGroups(HashSet<WearNTear> pieces, List<ZoneInstanceGroup> into)
        {
            var instanced = new HashSet<WearNTear>();
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return instanced;

            var candidatesByPrefab = new Dictionary<int, List<WearNTear>>();
            foreach (var wnt in pieces)
            {
                if (wnt == null || wnt.gameObject == null) continue;
                var view = wnt.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                if (zdo == null) continue;
                if (!InvulnerableClassifier.IsInvulnerable(wnt)
                    && (!FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value || !ShowsHealthyState(wnt))) continue;

                int prefabHash = zdo.GetPrefab();
                InstanceDefinition definition;
                if (!InstanceDefinitionCache.TryGet(prefabHash, out definition)) continue;

                List<WearNTear> list;
                if (!candidatesByPrefab.TryGetValue(prefabHash, out list))
                {
                    list = new List<WearNTear>();
                    candidatesByPrefab.Add(prefabHash, list);
                }
                list.Add(wnt);
            }

            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
            foreach (var entry in candidatesByPrefab)
            {
                if (entry.Value.Count < minInstances && !OnlyInstancingCanDraw(entry.Key, entry.Value)) continue;

                InstanceDefinition definition;
                if (!InstanceDefinitionCache.TryGet(entry.Key, out definition)) continue;

                var group = new ZoneInstanceGroup { PrefabHash = entry.Key, Definition = definition };
                for (int i = 0; i < entry.Value.Count; i++)
                {
                    var piece = entry.Value[i];
                    group.Add(piece.transform.localToWorldMatrix,
                        PieceIdentity.From(piece.gameObject, piece.transform.position));
                    instanced.Add(piece);
                }
                group.RecomputeBounds();
                into.Add(group);
            }
            return instanced;
        }

        // Mirrors WearNTear.SetHealthVisual: above 75% health a piece shows its healthy look, which is all an instance draws.
        internal static bool ShowsHealthyState(WearNTear wnt)
        {
            if (wnt.GetHealthPercentage() <= 0.75f) return false;
            return wnt.m_new == null || wnt.m_new.activeInHierarchy;
        }

        private static void GroupContributions(List<MeshContribution> contributions,
            Dictionary<BatchKey, List<MeshContribution>> byBatchKey)
        {
            for (int i = 0; i < contributions.Count; i++)
            {
                var contribution = contributions[i];
                if (!byBatchKey.TryGetValue(contribution.Key, out var list))
                {
                    list = new List<MeshContribution>();
                    byBatchKey.Add(contribution.Key, list);
                }
                list.Add(contribution);
            }
        }

        // Combines one tier's geometry into a mesh per batch key. The far tier is built
        // inactive; ZoneTracker swaps which tier is showing as the player crosses the
        // switch distance.
        private static int BuildTierBatches(Vector2s coord, GameObject parent, HashSet<BatchKey> materialized,
            Dictionary<BatchKey, List<MeshContribution>> byBatchKey, List<BatchInstance> into, bool farTier)
        {
            int totalContributions = 0;
            foreach (var kv in byBatchKey)
            {
                var batchKey = kv.Key;
                if (!materialized.Contains(batchKey)) continue;
                var contributions = kv.Value;
                if (contributions.Count == 0) continue;

                var combineArr = new CombineInstance[contributions.Count];
                for (int i = 0; i < contributions.Count; i++)
                {
                    combineArr[i] = new CombineInstance
                    {
                        mesh = contributions[i].Mesh,
                        subMeshIndex = contributions[i].SubMeshIndex,
                        transform = contributions[i].LocalToWorld,
                    };
                }

                string batchName = batchKey.BuildBatchName() + (farTier ? FarTierNameSuffix : string.Empty);
                var combined = new Mesh { name = $"EasyBake_{coord.x}_{coord.y}_{batchName}" };
                if (NeedsUInt32(contributions))
                    combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                combined.CombineMeshes(combineArr, mergeSubMeshes: true, useMatrices: true);
                combined.RecalculateBounds();

                var go = new GameObject($"Batch_{batchName}");
                go.transform.parent = parent.transform;
                go.transform.position = Vector3.zero;
                go.isStatic = true;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = combined;
                var mr = go.AddComponent<MeshRenderer>();
                batchKey.ApplyTo(mr);
                if (farTier) go.SetActive(false);

                into.Add(new BatchInstance { Combined = go, Mesh = combined });
                totalContributions += contributions.Count;
            }
            return totalContributions;
        }

        private static bool NeedsUInt32(List<MeshContribution> contributions)
        {
            long totalVerts = 0;
            for (int i = 0; i < contributions.Count; i++)
            {
                if (contributions[i].NeedsUInt32) return true;
                totalVerts += contributions[i].Mesh.vertexCount;
                if (totalVerts > 65000) return true;
            }
            return false;
        }
    }
}
