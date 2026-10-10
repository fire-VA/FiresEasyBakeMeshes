using System;
using System.Collections.Generic;
using FiresCore.Pieces;
using Unity.Profiling;
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
            // Time-sliced suppression (BlueHills profile, Fire 09-30: suppress was 55 % of the bake, one zone 193 ms in a
            // frame). Bake queues the pieces to silence here; PumpSuppression works them off under a per-frame budget.
            // Until the last is silenced the bake is not Ready: its Parent stays inactive and its instance groups don't
            // draw, so the originals keep drawing and nothing is ever drawn twice.
            public readonly List<GameObject> PendingSuppress = new List<GameObject>();
            public bool Ready = true;
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
            // The wear look the bake drew the piece with; a skipped piece has to still show it.
            public WearLook Look;

            public static PieceTransform From(Transform transform, WearLook look) => new PieceTransform
            {
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = transform.localScale,
                Look = look,
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
            public WearLook Look;
            public int NearCandidates;
            public List<BatchKey> ContributedBatchKeys = new List<BatchKey>();
            public List<MeshContribution> NearContributions = new List<MeshContribution>();
            public List<MeshContribution> FarContributions = new List<MeshContribution>();
            public bool HadIneligibleEnabled;
            // RandomMaterialValues only tints through property blocks; merging drops the tint, as instancing already does.
            public bool TintOnlyPropertyBlocks;
            public bool FarGeometryUnusable;
            public bool FullyCovered;
            public bool AnyContribution => ContributedBatchKeys.Count > 0;
            // Its look shows no model at all, like an invisible piece: nothing to draw, so only its stand-in colliders
            // take its place.
            public bool DrawsNothing => NearCandidates == 0 && !HadIneligibleEnabled;
        }

        public static BakeResult Bake(Vector2s coord, HashSet<WearNTear> pieces, HashSet<ZNetView> vegetation)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int candidates = 0, skippedNonLod0 = 0, skippedPropertyBlock = 0, skippedNonReadable = 0;
            BakeProfile.BeginZone();

            var result = new BakeResult();
            var instancedPieces = BuildInstanceGroups(pieces, result.InstanceGroups);
            BakeProfile.Mark(BakeProfile.Phase.InstanceGroups);

            EnsureBakeMarkers();
            s_collectMarker.Begin();
            var pieceInfos = new List<PieceLodInfo>(pieces.Count);
            foreach (var wnt in pieces)
            {
                if (wnt == null || wnt.gameObject == null) continue;
                if (instancedPieces.Contains(wnt)) continue;
                // A damageable piece can only leave an instanced group; merged geometry would need a rebake per hit.
                if (!InvulnerableClassifier.IsInvulnerable(wnt)) continue;
                if (CarriesEditsTheBakeCannotDraw(wnt)) continue;
                var info = new PieceLodInfo
                {
                    Source = wnt,
                    Look = LookOf(wnt),
                    TintOnlyPropertyBlocks = ScanPieceComponents(wnt.gameObject),
                };
                CollectContributions(wnt.gameObject, info,
                    ref candidates, ref skippedNonLod0, ref skippedPropertyBlock, ref skippedNonReadable);
                pieceInfos.Add(info);
            }

            s_collectMarker.End();
            BakeProfile.Mark(BakeProfile.Phase.Collect);

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

            BakeProfile.Mark(BakeProfile.Phase.GroupCover);
            var parent = new GameObject($"[EasyBake/Zone_{coord.x}_{coord.y}]");
            parent.transform.position = Vector3.zero;
            parent.isStatic = true;
            result.Parent = parent;
            BakeProfile.Mark(BakeProfile.Phase.Objects, gameObjects: 1);

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

            BakeProfile.Mark(BakeProfile.Phase.GroupCover);
            s_combineMarker.Begin();
            totalContributions = BuildTierBatches(coord, parent, materialized, nearCovered, result.Batches, farTier: false);
            BuildTierBatches(coord, parent, materialized, farCovered, result.FarBatches, farTier: true);
            s_combineMarker.End();

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
            BakeProfile.Mark(BakeProfile.Phase.Other);
            s_suppressMarker.Begin();
            result.ContributorIdentities = new HashSet<PieceIdentity>();
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                if (info.Source == null) continue;
                bool drawn = info.FullyCovered && info.AnyContribution;
                if (!drawn && !info.DrawsNothing) continue;

                var sourceZdo = ZdoOf(info.Source); var identity = sourceZdo != null ? PieceIdentity.From(sourceZdo) : PieceIdentity.From(info.Source.gameObject, info.Source.transform.position);
                result.ContributorIdentities.Add(identity);
                // 1.2.83: a static piece classified at this ZDO revision already holds its placement (read ahead of the bake).
                if (sourceZdo != null && s_classes.TryGetValue(info.Source, out PieceClass known) && known.Static && known.Eligible
                    && known.Revision == sourceZdo.DataRevision)
                {
                    var placed = known.Transform;
                    placed.Look = info.Look;
                    result.PieceTransforms[identity] = placed;
                }
                else result.PieceTransforms[identity] = PieceTransform.From(info.Source.transform, info.Look);
                if (!drawn) continue;

                result.PendingSuppress.Add(info.Source.gameObject);
            }

            // Instanced pieces are suppressed on the same terms: their whole prefab is
            // now drawn by the instanced mesh, so hiding the original redraws nothing.
            BakeProfile.Mark(BakeProfile.Phase.Suppress);
            var instancedVegetation = VegetationInstancing.BuildInstanceGroups(vegetation, result.InstanceGroups, result.PieceTransforms);
            BakeProfile.Mark(BakeProfile.Phase.Vegetation);
            foreach (var view in instancedVegetation)
            {
                result.ContributorIdentities.Add(PieceIdentity.From(view.gameObject, view.transform.position));
                result.PendingSuppress.Add(view.gameObject);
            }

            foreach (var piece in instancedPieces)
            {
                if (piece == null) continue;
                bool known = s_instancedInfo.TryGetValue(piece, out var info2);
                var instancedIdentity = known ? info2.Identity : PieceIdentity.From(piece.gameObject, piece.transform.position);
                result.ContributorIdentities.Add(instancedIdentity);
                // A stand-in transform is what skipping and stand-ins key on, so a piece with no entry here
                // CANNOT be skipped however eligible it looks - ZdoMatches needs the cached rotation, scale and
                // look to compare against, and StandInColliders needs the transform to place colliders at.
                //
                // Measured 2026-09-25: with SkipDamageablePieces on but this still gated on invulnerability,
                // 24,619 pieces reported "baked, but no stand-in transform was recorded" - the single largest
                // reason, and the feature looked half-broken rather than blocked.
                //
                // Damageable pieces reach the bake ONLY as instances, never merged (see the combine candidate
                // filter above: merged geometry would need a rebake per hit), so this loop is the only place
                // that can record one for them.
                // 1.2.83: the classification's own copies (read ahead of the bake) for a static piece.
                if ((known ? info2.Invulnerable : InvulnerableClassifier.IsInvulnerable(piece)) || SkipEligibility.DamageableSkipAllowed)
                    result.PieceTransforms[instancedIdentity] = known && info2.Static ? info2.Transform
                        : PieceTransform.From(piece.transform, known ? info2.Key.Look : LookOf(piece));
                result.PendingSuppress.Add(piece.gameObject);
            }

            s_suppressMarker.End();
            BakeProfile.Mark(BakeProfile.Phase.Suppress);

            sw.Stop();
            BakeProfile.EndZone(coord, pieces.Count, candidates, totalContributions, result.Batches.Count + result.FarBatches.Count);
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
                    $"queued {result.PendingSuppress.Count} pieces to suppress (time-sliced), " +
                    $"left {partialPieces} partial pieces vanilla (un-bakeable parts), " +
                    $"cached {result.ContributorIdentities?.Count ?? 0} identities in {sw.ElapsedMilliseconds}ms.");
            }
            if (result.Batches.Count == 0)
            {
                UnityEngine.Object.Destroy(parent);
                result.Parent = null;
            }
            if (result.PendingSuppress.Count > 0)
            {
                result.Ready = false;
                if (result.Parent != null) result.Parent.SetActive(false);
                s_pendingBakes.Add(result);
            }
            return result;
        }

        private static readonly List<BakeResult> s_pendingBakes = new List<BakeResult>();

        // Once a frame (ZoneTracker.Update): silence queued pieces until the frame's budget is spent. A bake whose last
        // piece is silenced turns Ready and shows its combined meshes (and its instance groups start drawing).
        public static void PumpSuppression()
        {
            if (s_pendingBakes.Count == 0) return;
            float budgetMs = FiresEasyBakeMeshesPlugin.SuppressBudgetMs.Value;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int done = 0;
            while (s_pendingBakes.Count > 0)
            {
                var result = s_pendingBakes[0];
                var pending = result.PendingSuppress;
                while (pending.Count > 0)
                {
                    if (budgetMs > 0f && done > 0 && clock.Elapsed.TotalMilliseconds >= budgetMs) break;
                    int last = pending.Count - 1;
                    var go = pending[last];
                    pending.RemoveAt(last);
                    done++;
                    if (go == null) continue;
                    var suppression = EasyBakeSuppressedVisuals.SuppressPiece(go);
                    if (suppression != null) result.SuppressedPieces.Add(suppression);
                }
                if (pending.Count > 0) break;
                s_pendingBakes.RemoveAt(0);
                if (!result.Ready)
                {
                    result.Ready = true;
                    if (result.Parent != null) result.Parent.SetActive(true);
                }
            }
            BakeProfile.NoteSuppressSlice(clock.ElapsedTicks, done, s_pendingBakes.Count);
        }

        // A bake torn down or unloaded before its queue ran out: nothing more to silence (what was silenced is restored by
        // Restore as before).
        internal static void DropPending(BakeResult result)
        {
            if (result == null) return;
            result.PendingSuppress.Clear();
            s_pendingBakes.Remove(result);
            result.Ready = true;
        }

        public static void Restore(BakeResult result)
        {
            if (result == null) return;
            DropPending(result);

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
            // The buffers were filled by ScanPieceComponents for this same piece, one walk earlier.
            var lodGroups = s_pieceLodGroups;
            var lod0Set = s_lod0Set;
            var lodControlled = s_lodControlled;
            var farSet = s_farSet;
            lod0Set.Clear();
            lodControlled.Clear();
            farSet.Clear();

            for (int g = 0; g < lodGroups.Count; g++)
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
                        lodControlled.Add(r);
                        if (li == 0) lod0Set.Add(r);
                    }
                }

                int farSlot = HasAnyRenderer(lods, 1) ? 1 : 0;
                var farRenderers = lods[farSlot].renderers;
                if (farRenderers == null) continue;
                for (int ri = 0; ri < farRenderers.Length; ri++)
                {
                    if (farRenderers[ri] == null) continue;
                    farSet.Add(farRenderers[ri]);
                }
            }

            var renderers = s_pieceRenderers;
            for (int i = 0; i < renderers.Count; i++)
            {
                var mr = renderers[i];
                if (mr == null) continue;

                bool isLodControlled = lodControlled.Contains(mr);
                bool inNearTier = !isLodControlled || lod0Set.Contains(mr);
                bool inFarTier = isLodControlled ? farSet.Contains(mr) : mr.enabled;

                if (inNearTier && mr.enabled)
                {
                    candidates++;
                    info.NearCandidates++;
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

            mr.GetSharedMaterials(s_bakeMaterials);
            int subCount = Mathf.Min(mesh.subMeshCount, s_bakeMaterials.Count);
            var localToWorld = mr.transform.localToWorldMatrix;
            bool meshNeeds32 = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32;

            for (int s = 0; s < subCount; s++)
            {
                var material = s_bakeMaterials[s];
                if (material == null) continue;
                material = MaterialVariants.SharedSource(mr, material);   // 1.2.88: one batch per variant, nameable in the cache
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

            // "Can be skipped" is the test, and what can be skipped widens with SkipDamageablePieces. Left as
            // invulnerable-only, a prefab the combiner refuses and that has fewer than MinInstancesPerPrefab
            // copies would form no group, never become a contributor, and so never be skippable however
            // eligible the pieces themselves are - which is the census reason "damageable pieces the bake does
            // not draw, fewer copies of its prefab in the zone".
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] == null) continue;
                if (InvulnerableClassifier.IsInvulnerable(candidates[i])) return true;
                if (SkipEligibility.DamageableSkipAllowed) return true;
            }
            return false;
        }

        // One walk per piece instead of three. A fresh zone bake analyses every piece in it, and the typed
        // GetComponentsInChildren overloads return a new array each time, which made the bake the largest allocator in
        // the 2026-09-22 profile (2.4 GB, 53.8 M allocations). The list overload reuses these buffers, so a bake of
        // 1,500 pieces allocates nothing to find their renderers.
        private static readonly List<Component> s_pieceComponents = new List<Component>();
        private static readonly List<LODGroup> s_pieceLodGroups = new List<LODGroup>();
        private static readonly List<MeshRenderer> s_pieceRenderers = new List<MeshRenderer>();
        private static readonly HashSet<Renderer> s_lod0Set = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> s_lodControlled = new HashSet<Renderer>();
        private static readonly HashSet<Renderer> s_farSet = new HashSet<Renderer>();
        // Renderer.sharedMaterials returns a fresh Material[] per access, and AppendContributions runs twice per
        // renderer (near tier, far tier). GetSharedMaterials fills this reused list instead.
        private static readonly List<Material> s_bakeMaterials = new List<Material>();

        // A profile names the phase it lands in, so the bake reports its own three: reading the pieces, combining the
        // meshes, and hiding the originals. Created on first bake — a ProfilerMarker built in a field initializer runs
        // before the Profiler is up.
        private static ProfilerMarker s_collectMarker, s_combineMarker, s_suppressMarker;
        private static bool s_bakeMarkersReady;

        private static void EnsureBakeMarkers()
        {
            if (s_bakeMarkersReady) return;
            s_bakeMarkersReady = true;
            s_collectMarker = new ProfilerMarker("EBM bake:collect");
            s_combineMarker = new ProfilerMarker("EBM bake:combine");
            s_suppressMarker = new ProfilerMarker("EBM bake:suppress");
        }

        // GetComponentsInChildren<MeshRenderer>(includeInactive: false) skips components on inactive GameObjects, so the
        // one-pass walk has to include everything and apply that test itself. The renderer's own enabled flag is a
        // separate question the tiers ask later, exactly as before.
        private static bool ScanPieceComponents(GameObject root)
        {
            s_pieceLodGroups.Clear();
            s_pieceRenderers.Clear();
            bool tintOnly = false;

            root.GetComponentsInChildren(true, s_pieceComponents);
            for (int i = 0; i < s_pieceComponents.Count; i++)
            {
                var component = s_pieceComponents[i];
                if (component == null) continue;

                if (component is LODGroup lodGroup) s_pieceLodGroups.Add(lodGroup);
                else if (component is MeshRenderer renderer) { if (renderer.gameObject.activeInHierarchy) s_pieceRenderers.Add(renderer); }
                else if (component is RandomMaterialValues) tintOnly = true;
            }
            s_pieceComponents.Clear();
            return tintOnly;
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

        // Partitions the zone's pieces: every prefab and wear look that is instanceable, and appears at least
        // MinInstancesPerPrefab times or cannot be combined at all, becomes a GPU-instanced group, and those pieces are held
        // back so the combine path skips them. The rest fall through to combining, where they still batch by material with
        // everything else.
        private static readonly Dictionary<WearNTear, PieceClass> s_instancedInfo =
            new Dictionary<WearNTear, PieceClass>();

        private static HashSet<WearNTear> BuildInstanceGroups(HashSet<WearNTear> pieces, List<ZoneInstanceGroup> into)
        {
            s_instancedInfo.Clear();
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return new HashSet<WearNTear>();
            // 1.2.84: the groups built ahead of the bake (PumpGroups) are handed over when the zone hasn't changed since; the bake
            // only checks each piece is alive at the revision it was grouped at.
            if (TakePrepared(pieces, into, out HashSet<WearNTear> ready)) return ready;
            var instanced = new HashSet<WearNTear>();

            var candidatesByKey = new Dictionary<InstanceKey, List<WearNTear>>();
            int cached = 0, inline = 0;
            long inlineTicks = 0;
            long loopStart = BakeProfile.Now();
            foreach (var wnt in pieces)
            {
                // 1.2.83: one native liveness check (a live WearNTear always has its GameObject; the second check was a
                // second native call per piece).
                if (wnt == null) continue;
                var zdo = ZdoOf(wnt);
                if (zdo == null) continue;
                // 1.2.82 (BlueHills F/G: instance grouping ~75 % of the worst zone, 60-63 ms in one frame): the per-piece
                // classification was worked out while the zone's pieces arrived (PumpClassify, a per-frame budget, during the
                // settle delay); a piece whose ZDO changed since (its data revision) is classified again here.
                if (s_classes.TryGetValue(wnt, out PieceClass c) && c.Revision == zdo.DataRevision) cached++;
                else
                {
                    long t = BakeProfile.Now();
                    c = Classify(wnt, zdo);
                    s_classes[wnt] = c;
                    inline++;
                    inlineTicks += BakeProfile.Now() - t;
                }
                if (!c.Eligible) continue;
                var key = c.Key;
                // 1.2.80: each piece's identity and look worked out once and reused by Bake's instanced loop; 1.2.83: the whole
                // classification (matrix, transform, invulnerability) for the group build and the suppress bookkeeping.
                s_instancedInfo[wnt] = c;
                if (!InstanceDefinitionCache.TryGet(key, out _)) continue;

                if (!candidatesByKey.TryGetValue(key, out var list))
                {
                    list = new List<WearNTear>();
                    candidatesByKey.Add(key, list);
                }
                list.Add(wnt);
            }

            long groupStart = BakeProfile.Now();
            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
            foreach (var entry in candidatesByKey)
            {
                if (entry.Value.Count < minInstances && !OnlyInstancingCanDraw(entry.Key.PrefabHash, entry.Value)) continue;

                InstanceDefinition definition;
                if (!InstanceDefinitionCache.TryGet(entry.Key, out definition)) continue;

                var group = new ZoneInstanceGroup { PrefabHash = entry.Key.PrefabHash, Look = entry.Key.Look, Definition = definition };
                for (int i = 0; i < entry.Value.Count; i++)
                {
                    var piece = entry.Value[i];
                    var pc = s_instancedInfo[piece];
                    group.Add(pc.Static ? pc.Matrix : piece.transform.localToWorldMatrix, pc.Identity);
                    instanced.Add(piece);
                }
                group.RecomputeBounds();
                into.Add(group);
            }
            long end = BakeProfile.Now();
            BakeProfile.NoteClassify(cached, inline);
            BakeProfile.NoteInstanceSplit(groupStart - loopStart - inlineTicks, inlineTicks, end - groupStart);
            return instanced;
        }

        // ── Instance groups built ahead of the bake (1.2.84) ─────────────────────────────────────────────────────────
        // [fgn]'s BlueHills round on 1.2.83: (-17,11) still spent "instance = loop 26.5 + at-bake classify 0.1 + groups 17.3 ms"
        // in the bake frame with every piece classified ahead. A zone due to bake (ZoneTracker, after its classification) gets a
        // ZonePrep: its pieces copied once, then walked under the classify budget (PumpClassify's frame time) into buckets and,
        // when all are walked, into finished ZoneInstanceGroups and the instanced set. The bake takes them when the zone's piece
        // count is the same and every grouped piece is still alive at the ZDO revision it was grouped at; otherwise it groups the
        // usual way. A prep is dropped once taken, and on unload.
        private sealed class ZonePrep
        {
            public WearNTear[] Items;
            public int Next;
            public readonly Dictionary<InstanceKey, List<WearNTear>> ByKey = new Dictionary<InstanceKey, List<WearNTear>>();
            public readonly Dictionary<WearNTear, PieceClass> Info = new Dictionary<WearNTear, PieceClass>();
            public List<ZoneInstanceGroup> Groups;
            public HashSet<WearNTear> Instanced;
            public int Cached, Inline;
            public bool Done => Groups != null;
        }

        private static readonly Dictionary<HashSet<WearNTear>, ZonePrep> s_preps = new Dictionary<HashSet<WearNTear>, ZonePrep>();

        // True when a prep was started now (1.2.85: the zone's wait for it counts from here).
        internal static bool PrepareGroups(HashSet<WearNTear> pieces)
        {
            if (pieces == null || !FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value || s_preps.ContainsKey(pieces)) return false;
            var items = new WearNTear[pieces.Count];
            pieces.CopyTo(items);
            s_preps[pieces] = new ZonePrep { Items = items };
            return true;
        }

        internal static bool GroupsReady(HashSet<WearNTear> pieces) => pieces != null && s_preps.TryGetValue(pieces, out var p) && p.Done;

        internal static void ForgetPrep(HashSet<WearNTear> pieces) { if (pieces != null) s_preps.Remove(pieces); }

        // One slice of the preps, inside PumpClassify's budget (after its urgent queue): true while work is left.
        private static void PumpGroups(System.Diagnostics.Stopwatch clock, float budgetMs)
        {
            if (s_preps.Count == 0) return;
            foreach (var prep in s_preps.Values)
            {
                if (prep.Done) continue;
                while (prep.Next < prep.Items.Length)
                {
                    if (clock.Elapsed.TotalMilliseconds >= budgetMs) return;
                    var wnt = prep.Items[prep.Next++];
                    if (wnt == null) continue;
                    var zdo = ZdoOf(wnt);
                    if (zdo == null) continue;
                    if (s_classes.TryGetValue(wnt, out PieceClass c) && c.Revision == zdo.DataRevision) prep.Cached++;
                    else { c = Classify(wnt, zdo); s_classes[wnt] = c; prep.Inline++; }
                    if (!c.Eligible) continue;
                    prep.Info[wnt] = c;
                    if (!InstanceDefinitionCache.TryGet(c.Key, out _)) continue;
                    if (!prep.ByKey.TryGetValue(c.Key, out var list)) prep.ByKey[c.Key] = list = new List<WearNTear>();
                    list.Add(wnt);
                }
                // All walked: the groups themselves (as the bake would build them), in one go (a fraction of the walk).
                var groups = new List<ZoneInstanceGroup>();
                var instanced = new HashSet<WearNTear>();
                int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
                foreach (var entry in prep.ByKey)
                {
                    if (entry.Value.Count < minInstances && !OnlyInstancingCanDraw(entry.Key.PrefabHash, entry.Value)) continue;
                    if (!InstanceDefinitionCache.TryGet(entry.Key, out InstanceDefinition definition)) continue;
                    var group = new ZoneInstanceGroup { PrefabHash = entry.Key.PrefabHash, Look = entry.Key.Look, Definition = definition };
                    foreach (var piece in entry.Value)
                    {
                        var pc = prep.Info[piece];
                        group.Add(pc.Static ? pc.Matrix : piece.transform.localToWorldMatrix, pc.Identity);
                        instanced.Add(piece);
                    }
                    group.RecomputeBounds();
                    groups.Add(group);
                }
                prep.Instanced = instanced;
                prep.Groups = groups;
                return;
            }
        }

        // The bake's side: the prepared groups when they still hold for this zone (see ZonePrep).
        private static bool TakePrepared(HashSet<WearNTear> pieces, List<ZoneInstanceGroup> into, out HashSet<WearNTear> instanced)
        {
            instanced = null;
            if (!s_preps.TryGetValue(pieces, out var prep)) return false;
            s_preps.Remove(pieces);
            long start = BakeProfile.Now();
            string why = null;
            if (!prep.Done) why = $"not finished ({prep.Next}/{prep.Items.Length} walked)";
            else if (prep.Items.Length != pieces.Count) why = $"the zone changed ({prep.Items.Length} -> {pieces.Count} pieces)";
            else
            {
                // 1.2.85 ([fgn] BlueHills round 20261001-112023: (-17,10) 97.2 ms, "a grouped piece changed or went"): the owner writes
                // support and health into piece ZDOs all the time, so in a 9000-piece zone one revision has nearly always moved. A piece
                // whose revision moved is classified again here; the prep holds while it still classifies to the same group (eligible,
                // same key); a piece gone, or one that changed group, falls back to grouping at bake.
                int moved = 0;
                foreach (var kv in prep.Info)
                {
                    PieceClass c = kv.Value;
                    if (kv.Key == null || c.Zdo == null || !c.Zdo.IsValid()) { why = "a grouped piece went"; break; }
                    if (c.Zdo.DataRevision == c.Revision) continue;
                    PieceClass now = Classify(kv.Key, c.Zdo);
                    s_classes[kv.Key] = now;
                    moved++;
                    if (!now.Eligible || !now.Key.Equals(c.Key)) { why = $"a grouped piece changed group ({Utils.GetPrefabName(kv.Key.gameObject)})"; break; }
                    // Same group: the prepared matrix and identity still hold (a static piece doesn't move; its identity is its ZDO's).
                    c.Revision = now.Revision;
                }
                if (why == null) BakeProfile.NotePreparedRechecked(moved);
            }
            if (why != null) { BakeProfile.NotePrepared(false, why); return false; }
            foreach (var kv in prep.Info) s_instancedInfo[kv.Key] = kv.Value;
            into.AddRange(prep.Groups);
            instanced = prep.Instanced;
            BakeProfile.NoteClassify(prep.Cached, prep.Inline);
            BakeProfile.NoteInstanceSplit(BakeProfile.Now() - start, 0, 0);
            BakeProfile.NotePrepared(true, null);
            return true;
        }

        // ── Per-piece classification, worked out ahead of the bake (1.2.82) ─────────────────────────────────────────
        // 1.2.84: a class (it was a struct of ~180 bytes, copied on every dictionary read and write in the bake's loops), holding
        // the ZDO it was classified from so a prepared group can be checked without a component lookup.
        internal sealed class PieceClass
        {
            public ZDO Zdo;
            public bool Eligible;          // an instancing candidate (invulnerable, or damageable showing its healthy look; not live)
            public InstanceKey Key;
            public PieceIdentity Identity;
            public uint Revision;          // the ZDO's data revision when classified; any change re-classifies at bake
            // 1.2.83 ([fgn] BlueHills (-17,11): 54 ms of instance grouping with 88 % classified ahead, ~4.8 us a piece, and
            // 34.8 ms of suppress bookkeeping in the bake frame, both mostly native transform reads): a static piece's
            // placement is read once, here, ahead of the bake. Static = nothing under a Rigidbody (a ship's pieces move);
            // a static piece's transform is set at creation and never synced after, so the copy stays right.
            public bool Static;
            public Matrix4x4 Matrix;
            public PieceTransform Transform;   // the stand-in transform the suppress loop records (with the look)
            public bool Invulnerable;
        }

        private static readonly Dictionary<WearNTear, PieceClass> s_classes = new Dictionary<WearNTear, PieceClass>();
        private static readonly Queue<WearNTear> s_classQueue = new Queue<WearNTear>();
        private static int s_classSweepAt = 65536;

        private static PieceClass Classify(WearNTear wnt, ZDO zdo)
        {
            var c = new PieceClass { Revision = zdo.DataRevision, Zdo = zdo };
            c.Eligible = (InvulnerableClassifier.IsInvulnerable(wnt)
                          || (FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value && ShowsHealthyState(wnt)))
                         && !PieceData.MustStayLive(zdo, zdo.GetPrefab());
            if (!c.Eligible) return c;
            long lookStart = BakeProfile.Now();
            c.Key = new InstanceKey(zdo.GetPrefab(), LookOf(zdo));
            BakeProfile.NoteInstanceLook(lookStart);
            c.Identity = PieceIdentity.From(zdo);
            c.Invulnerable = InvulnerableClassifier.IsInvulnerable(wnt);
            c.Static = wnt.GetComponentInParent<Rigidbody>() == null;
            if (c.Static)
            {
                Transform t = wnt.transform;
                c.Matrix = t.localToWorldMatrix;
                c.Transform = PieceTransform.From(t, c.Key.Look);
            }
            return c;
        }

        // A piece joined a zone (ZoneTracker): classified later under the frame budget.
        internal static void QueueClassify(WearNTear wnt)
        {
            if (wnt != null && FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) s_classQueue.Enqueue(wnt);
        }

        internal static void ForgetClass(WearNTear wnt)
        {
            if (wnt != null) s_classes.Remove(wnt);
        }

        // ForgetClass inside a condition that must stay true (the piece was in the zone).
        internal static bool ForgetClassed(WearNTear wnt)
        {
            ForgetClass(wnt);
            return true;
        }

        // Once a frame (ZoneTracker.Update): classify queued pieces until [Batching] ClassifyBudgetMs is spent; drop entries of
        // pieces that were destroyed once the table has doubled.
        public static void PumpClassify()
        {
            if (s_classes.Count > s_classSweepAt)
            {
                var dead = new List<WearNTear>();
                foreach (var key in s_classes.Keys) if (key == null) dead.Add(key);
                foreach (var key in dead) s_classes.Remove(key);
                s_classSweepAt = Mathf.Max(65536, s_classes.Count * 2);
            }
            if (s_classQueue.Count == 0 && s_classUrgent.Count == 0 && s_preps.Count == 0) return;
            float budgetMs = FiresEasyBakeMeshesPlugin.ClassifyBudgetMs.Value;
            if (budgetMs <= 0f) { s_classQueue.Clear(); s_classUrgent.Clear(); s_preps.Clear(); return; }   // 0 = the old behaviour: all of it at bake time
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int done = 0;
            // 1.2.83: a zone due to bake (ClassifyFirst) is served first; 1.2.84: then its prepared groups (PumpGroups); then the
            // shared queue with what is left of the budget.
            while (s_classUrgent.Count > 0 && (done == 0 || clock.Elapsed.TotalMilliseconds < budgetMs))
                if (ClassifyOne(s_classUrgent.Dequeue())) done++;
            if (s_classUrgent.Count == 0 && clock.Elapsed.TotalMilliseconds < budgetMs) PumpGroups(clock, budgetMs);
            while (s_classQueue.Count > 0 && (done == 0 || clock.Elapsed.TotalMilliseconds < budgetMs))
                if (ClassifyOne(s_classQueue.Dequeue())) done++;
            BakeProfile.NoteClassifySlice(clock.ElapsedTicks, done, s_classQueue.Count + s_classUrgent.Count);
        }

        private static bool ClassifyOne(WearNTear wnt)
        {
            if (wnt == null) return false;
            var zdo = ZdoOf(wnt);
            if (zdo == null) return false;
            if (s_classes.TryGetValue(wnt, out PieceClass c) && c.Revision == zdo.DataRevision) return false;
            s_classes[wnt] = Classify(wnt, zdo);
            return true;
        }

        // 1.2.83: a zone due to bake puts its pieces in front of the shared queue; the pump skips the ones already classified at
        // their current revision. Returns how many were queued (0 = nothing to wait for).
        private static readonly Queue<WearNTear> s_classUrgent = new Queue<WearNTear>();
        internal static bool UrgentClassifyPending => s_classUrgent.Count > 0;

        internal static int ClassifyFirst(HashSet<WearNTear> pieces)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return 0;
            int n = 0;
            foreach (var wnt in pieces)
            {
                if ((object)wnt == null) continue;
                s_classUrgent.Enqueue(wnt);
                n++;
            }
            return n;
        }

        // Mirrors WearNTear.SetHealthVisual: above 75% health a piece shows its healthy look, the only one a damageable
        // piece is drawn with, since its next hit would change it.
        internal static bool ShowsHealthyState(WearNTear wnt)
        {
            if (wnt.GetHealthPercentage() <= WearLooks.NewModelAbove) return false;
            // activeSelf, not activeInHierarchy: the question is whether the undamaged model is switched on, which is
            // what WearNTear and Core's WearLooks both read. activeInHierarchy also answers false while the piece
            // itself is switched off - mid-creation, parked, or reparented - and a piece that is merely not on screen
            // yet is not worn. That read excluded 907 signs from the bake in one census.
            return wnt.m_new == null || wnt.m_new.activeSelf;
        }

        // 1.2.81 ([fgn], BlueHills: the bake's per-piece cost is mostly component lookups): vanilla's WearNTear already holds
        // its ZNetView (m_nview, set in Awake), so the ZDO comes from that field instead of a GetComponent per call. ZdoOf runs
        // several times per piece (instance grouping, collect, edits check, look); GetComponent remains the fallback.
        private static readonly HarmonyLib.AccessTools.FieldRef<WearNTear, ZNetView> WearView =
            HarmonyLib.AccessTools.FieldRefAccess<WearNTear, ZNetView>("m_nview");

        internal static ZDO ZdoOf(WearNTear wnt)
        {
            ZNetView view = WearView(wnt);
            if (view == null) view = wnt.GetComponent<ZNetView>();
            return view != null ? view.GetZDO() : null;
        }

        internal static WearLook LookOf(ZDO zdo)
        {
            var scene = ZNetScene.instance;
            return WearLooks.Resolve(scene != null ? scene.GetPrefab(zdo.GetPrefab()) : null, zdo);
        }

        internal static WearLook LookOf(WearNTear wnt)
        {
            var zdo = ZdoOf(wnt);
            return zdo != null ? LookOf(zdo) : default(WearLook);
        }

        internal static bool CarriesEditsTheBakeCannotDraw(WearNTear wnt)
        {
            var zdo = ZdoOf(wnt);
            return zdo != null && PieceData.MustStayLive(zdo, zdo.GetPrefab());
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
                bool wide = NeedsUInt32(contributions);
                BakeProfile.Mark(BakeProfile.Phase.CombineArrays);
                var combined = new Mesh { name = $"EasyBake_{coord.x}_{coord.y}_{batchName}" };
                if (wide)
                    combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                combined.CombineMeshes(combineArr, mergeSubMeshes: true, useMatrices: true);
                combined.RecalculateBounds();
                BakeProfile.Mark(BakeProfile.Phase.CombineNative, meshes: 1, sourceMeshes: contributions.Count, vertices: combined.vertexCount);

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
                BakeProfile.Mark(BakeProfile.Phase.Objects, gameObjects: 1);
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
