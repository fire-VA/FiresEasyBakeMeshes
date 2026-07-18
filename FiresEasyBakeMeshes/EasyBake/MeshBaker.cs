using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    internal static class MeshBaker
    {
        // Result of one Bake() call. Holds everything Restore() needs to reverse,
        // plus the data we need to reattach the cache on zone reload.
        //
        // Lifecycle:
        //   - Bake() fills Parent, Batches, DisabledRenderers, DisabledLodGroups,
        //     ContributorIdentities.
        //   - ZoneTracker keeps the BakeResult alive across zone-unload/reload
        //     cycles. On unload it clears DisabledRenderers/DisabledLodGroups (their
        //     references go Unity-null when ZNetScene destroys the pieces) and
        //     hides Parent via SetActive(false). On reload, new piece instances
        //     stream in via OnInstanceCreated, and DisableForCacheHit re-populates
        //     DisabledRenderers/DisabledLodGroups against the fresh references.
        //   - Restore() is only called for full teardown: dirty rebake or genuine
        //     "base destroyed" detection. It destroys Parent + mesh assets.
        internal class BakeResult
        {
            public GameObject Parent;
            public List<BatchInstance> Batches = new List<BatchInstance>();
            public List<RendererState> DisabledRenderers = new List<RendererState>();
            public List<LodGroupState> DisabledLodGroups = new List<LodGroupState>();
            // Identity of every piece that fed at least one batch. Used on zone
            // reload to recognise the same pieces returning and disable their
            // renderers without rebaking.
            public HashSet<PieceIdentity> ContributorIdentities;
        }

        internal class BatchInstance
        {
            public GameObject Combined;
            public Mesh Mesh;
        }

        internal struct RendererState
        {
            public Renderer Renderer;
            public bool PriorEnabled;
        }

        internal struct LodGroupState
        {
            public LODGroup Group;
            public bool PriorEnabled;
        }

        // Identity used to recognise "the same piece coming back" across a zone
        // unload/reload cycle. Prefab hash (stable across instances) plus the
        // world position quantised to 1 cm. Pieces in a Valheim base don't move,
        // so this is exact; an admin teleport that nudges a piece would make it
        // a "new" piece from the cache's perspective and trigger a rebake — that's
        // the correct outcome.
        internal struct PieceIdentity : IEquatable<PieceIdentity>
        {
            public int PrefabHash;
            public int X;
            public int Y;
            public int Z;

            public bool Equals(PieceIdentity o) => PrefabHash == o.PrefabHash && X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object obj) => obj is PieceIdentity p && Equals(p);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = PrefabHash;
                    h = (h * 397) ^ X;
                    h = (h * 397) ^ Y;
                    h = (h * 397) ^ Z;
                    return h;
                }
            }

            public static PieceIdentity From(GameObject go, Vector3 worldPos)
            {
                // Pull the int prefab hash straight out of the ZDO — ZDO.GetPrefab()
                // already stores it. Avoids a string allocation + rehash on every
                // OnInstanceCreated call (~6500/zone in a megabase).
                int hash = 0;
                if (go != null)
                {
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null)
                    {
                        var zdo = nv.GetZDO();
                        if (zdo != null) hash = zdo.GetPrefab();
                    }
                }
                return new PieceIdentity
                {
                    PrefabHash = hash,
                    X = Mathf.RoundToInt(worldPos.x * 100f),
                    Y = Mathf.RoundToInt(worldPos.y * 100f),
                    Z = Mathf.RoundToInt(worldPos.z * 100f),
                };
            }
        }

        private struct MeshContribution
        {
            public Mesh Mesh;
            public int SubMeshIndex;
            public Matrix4x4 LocalToWorld;
            public MeshRenderer SourceRenderer;
            public bool NeedsUInt32;
            public PieceLodInfo Piece;
        }

        // Per-piece bookkeeping populated during CollectContributions. Three roles:
        //   1. Enumerate every renderer + LODGroup the piece owns, so Bake() can
        //      suppress them as a unit if the piece's LOD0 actually fed a batch.
        //   2. Carry the "this piece contributed to a batch" flag so we only touch
        //      pieces that we actually replaced visually.
        //   3. Hold the source WearNTear so the post-batch identity-tagging pass
        //      can record this piece in ContributorIdentities.
        private class PieceLodInfo
        {
            public WearNTear Source;
            public List<LODGroup> LodGroups = new List<LODGroup>();
            public List<MeshRenderer> AllMeshRenderers = new List<MeshRenderer>();
            public bool ContributedToBatch;
        }

        public static BakeResult Bake(Vector2i coord, HashSet<WearNTear> pieces)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var byMaterial = new Dictionary<Material, List<MeshContribution>>();
            int candidates = 0, skippedNonLod0 = 0, skippedPropertyBlock = 0, skippedNonReadable = 0;

            var pieceInfos = new List<PieceLodInfo>(pieces.Count);
            foreach (var wnt in pieces)
            {
                if (wnt == null || wnt.gameObject == null) continue;
                var info = new PieceLodInfo { Source = wnt };
                CollectContributions(wnt.gameObject, info, byMaterial,
                    ref candidates, ref skippedNonLod0, ref skippedPropertyBlock, ref skippedNonReadable);
                pieceInfos.Add(info);
            }

            int minPerBatch = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerBatch.Value;
            var result = new BakeResult();
            int totalContributions = 0;

            var parent = new GameObject($"[EasyBake/Zone_{coord.x}_{coord.y}]");
            parent.transform.position = Vector3.zero;
            parent.isStatic = true;
            result.Parent = parent;

            foreach (var kv in byMaterial)
            {
                var material = kv.Key;
                var contributions = kv.Value;
                if (contributions.Count < minPerBatch) continue;

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

                var combined = new Mesh { name = $"EasyBake_{coord.x}_{coord.y}_{material.name}" };
                if (NeedsUInt32(contributions))
                    combined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                combined.CombineMeshes(combineArr, mergeSubMeshes: true, useMatrices: true);
                combined.RecalculateBounds();

                var go = new GameObject($"Batch_{material.name}");
                go.transform.parent = parent.transform;
                go.transform.position = Vector3.zero;
                go.isStatic = true;
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = combined;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = material;
                CopyRendererSettings(mr, contributions[0].SourceRenderer);

                // Flag every piece that contributed to this batch so the unified
                // suppression pass below knows which pieces have actually been
                // visually replaced and need their originals silenced.
                for (int i = 0; i < contributions.Count; i++)
                {
                    var info = contributions[i].Piece;
                    if (info != null) info.ContributedToBatch = true;
                }

                result.Batches.Add(new BatchInstance { Combined = go, Mesh = combined });
                totalContributions += contributions.Count;
            }

            // Unified suppression pass — runs once per Bake instead of per-material.
            // For each piece that contributed to at least one batch:
            //   - Disable the LODGroup(s) it owns. The combined mesh will draw at all
            //     distances; we don't want vanilla LOD switching toggling LOD1/LOD2
            //     renderers underneath. NOTE: LODGroup.enabled=false stops LOD
            //     selection but does NOT auto-disable the renderers — each renderer
            //     keeps whatever enabled state it had at the moment we disabled the
            //     group. So we still have to walk and disable each MeshRenderer.
            //   - Disable every MeshRenderer in the piece subtree (LOD0, LOD1, LOD2,
            //     and any LOD-orphaned renderers). This is what actually stops the
            //     originals from drawing.
            //   - Record an identity (prefab hash + cm-quantised world pos) so that
            //     when this zone unloads + reloads later, the new piece instances
            //     can be matched against the cache and re-disabled without rebaking.
            // We record prior-enabled state on both renderer + LODGroup lists so
            // Restore() puts everything back exactly as it was, even if vanilla had
            // a renderer disabled for its own reasons.
            result.ContributorIdentities = new HashSet<PieceIdentity>();
            for (int p = 0; p < pieceInfos.Count; p++)
            {
                var info = pieceInfos[p];
                if (!info.ContributedToBatch) continue;

                if (info.Source != null)
                {
                    result.ContributorIdentities.Add(
                        PieceIdentity.From(info.Source.gameObject, info.Source.transform.position));
                }

                for (int g = 0; g < info.LodGroups.Count; g++)
                {
                    var lg = info.LodGroups[g];
                    if (lg == null) continue;
                    result.DisabledLodGroups.Add(new LodGroupState { Group = lg, PriorEnabled = lg.enabled });
                    lg.enabled = false;
                }

                for (int r = 0; r < info.AllMeshRenderers.Count; r++)
                {
                    var renderer = info.AllMeshRenderers[r];
                    if (renderer == null) continue;
                    if (!renderer.enabled) continue;
                    result.DisabledRenderers.Add(new RendererState { Renderer = renderer, PriorEnabled = true });
                    renderer.enabled = false;
                }
            }

            sw.Stop();
            BakeSummary.RecordFreshBake(result.Batches.Count, sw.ElapsedMilliseconds);
            if ((result.Batches.Count > 0 && FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                || FiresEasyBakeMeshesPlugin.BatchingVerbose.Value)
            {
                EasyBakeLog.Info(
                    $"[Bake] Zone ({coord.x},{coord.y}): {pieces.Count} invulnerable pieces, " +
                    $"{candidates} candidate renderers ({skippedNonLod0} nonLod0-skip, {skippedPropertyBlock} propBlock-skip, {skippedNonReadable} nonReadable-skip), " +
                    $"baked {result.Batches.Count} batches covering {totalContributions} contributions, " +
                    $"disabled {result.DisabledRenderers.Count} renderers + {result.DisabledLodGroups.Count} LODGroups, " +
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

            // LODGroups first, then renderers — symmetric inverse of the disable
            // order. Order doesn't matter for correctness because everything is
            // explicit prior-state restoration, but matching the disable order
            // makes the diff-in-Unity-state predictable when debugging.
            if (result.DisabledLodGroups != null)
            {
                for (int i = 0; i < result.DisabledLodGroups.Count; i++)
                {
                    var s = result.DisabledLodGroups[i];
                    if (s.Group != null) s.Group.enabled = s.PriorEnabled;
                }
            }
            if (result.DisabledRenderers != null)
            {
                for (int i = 0; i < result.DisabledRenderers.Count; i++)
                {
                    var s = result.DisabledRenderers[i];
                    if (s.Renderer != null) s.Renderer.enabled = s.PriorEnabled;
                }
            }

            if (result.Batches != null)
            {
                foreach (var b in result.Batches)
                {
                    if (b.Combined != null) UnityEngine.Object.Destroy(b.Combined);
                    if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh);
                }
            }
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

            var lodGroups = pieceRoot.GetComponentsInChildren<LODGroup>(includeInactive: true);
            for (int i = 0; i < lodGroups.Length; i++)
            {
                var lg = lodGroups[i];
                if (lg == null) continue;
                cache.DisabledLodGroups.Add(new LodGroupState { Group = lg, PriorEnabled = lg.enabled });
                lg.enabled = false;
            }

            var renderers = pieceRoot.GetComponentsInChildren<MeshRenderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                if (!r.enabled) continue;
                cache.DisabledRenderers.Add(new RendererState { Renderer = r, PriorEnabled = true });
                r.enabled = false;
            }
        }

        // Walk a piece's subtree and:
        //   - record every MeshRenderer + LODGroup we find on PieceLodInfo so the
        //     post-batch suppression pass has the full set to silence;
        //   - feed each ELIGIBLE renderer's submeshes into the per-material
        //     contribution dictionary.
        //
        // Eligibility rules:
        //   * Renderer is enabled (skip otherwise — vanilla had it off for a reason).
        //   * If the renderer is registered in ANY LODGroup's LOD array, it must be
        //     in the LOD0 slot. LOD1/LOD2 are lower-detail variants of the same
        //     geometry; combining all three would multi-count the piece and produce
        //     a mesh that's both wrong and bigger than necessary. The combined mesh
        //     stands in for LOD0 at every distance.
        //   * If the renderer is NOT registered in any LODGroup (LOD-orphaned), we
        //     accept it. Modded prefabs sometimes have ad-hoc decorative renderers
        //     that live alongside a LODGroup without being part of it; those should
        //     still bake.
        //   * No MaterialPropertyBlock (per-renderer overrides don't survive a merge).
        //   * Mesh must be CPU-readable for Mesh.CombineMeshes to use it.
        private static void CollectContributions(GameObject root, PieceLodInfo info,
            Dictionary<Material, List<MeshContribution>> byMaterial,
            ref int candidates, ref int skippedNonLod0, ref int skippedPropertyBlock, ref int skippedNonReadable)
        {
            // Catalogue every LODGroup in the piece subtree, then index which
            // renderers belong to LOD0 (eligible) vs any other LOD (ineligible).
            var lodGroups = root.GetComponentsInChildren<LODGroup>(includeInactive: true);
            for (int i = 0; i < lodGroups.Length; i++)
                if (lodGroups[i] != null) info.LodGroups.Add(lodGroups[i]);

            HashSet<Renderer> lod0Set = null;
            HashSet<Renderer> lodControlled = null;
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lg = lodGroups[g];
                if (lg == null) continue;
                var lods = lg.GetLODs();
                if (lods == null) continue;
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
            }

            var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive: false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var mr = renderers[i];
                if (mr == null) continue;
                info.AllMeshRenderers.Add(mr);

                if (!mr.enabled) continue;
                candidates++;

                bool isLodControlled = lodControlled != null && lodControlled.Contains(mr);
                if (isLodControlled && (lod0Set == null || !lod0Set.Contains(mr)))
                {
                    skippedNonLod0++;
                    continue;
                }

                if (mr.HasPropertyBlock()) { skippedPropertyBlock++; continue; }

                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                if (!mesh.isReadable) { skippedNonReadable++; continue; }

                var mats = mr.sharedMaterials;
                int subCount = Mathf.Min(mesh.subMeshCount, mats.Length);
                var localToWorld = mr.transform.localToWorldMatrix;
                bool meshNeeds32 = mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32;

                for (int s = 0; s < subCount; s++)
                {
                    var m = mats[s];
                    if (m == null) continue;
                    if (!byMaterial.TryGetValue(m, out var list))
                    {
                        list = new List<MeshContribution>();
                        byMaterial.Add(m, list);
                    }
                    list.Add(new MeshContribution
                    {
                        Mesh = mesh,
                        SubMeshIndex = s,
                        LocalToWorld = localToWorld,
                        SourceRenderer = mr,
                        NeedsUInt32 = meshNeeds32,
                        Piece = info,
                    });
                }
            }
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

        private static void CopyRendererSettings(MeshRenderer dst, MeshRenderer src)
        {
            if (src == null) return;
            dst.shadowCastingMode = src.shadowCastingMode;
            dst.receiveShadows = src.receiveShadows;
            dst.lightProbeUsage = src.lightProbeUsage;
            dst.reflectionProbeUsage = src.reflectionProbeUsage;
            dst.motionVectorGenerationMode = src.motionVectorGenerationMode;
        }
    }
}
