using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // One drawable piece of a prefab: a mesh, one of its submeshes with the material for it,
    // and where that sits relative to the first part, so a whole prefab can be redrawn from
    // the instance matrices the group already holds.
    internal sealed class InstancePart
    {
        public Mesh NearMesh;
        public Mesh FarMesh;
        public int SubMesh;
        public Matrix4x4 FromFirst;
        public RenderParams RenderParams;
    }

    // How one prefab is drawn as GPU instances: every renderer it actually shows, lifted off
    // the prefab as parts, plus the offset mapping a piece's root transform onto the first of
    // them (a prefab's mesh usually hangs off a child).
    //
    // Instancing accepts a prefab when everything it would draw is a MeshRenderer with a mesh,
    // one part per material. That preserves the hide-set == draw-set invariant, so suppressing
    // the piece hides nothing that is not redrawn, and it covers both the prefabs a megabase is
    // built from in bulk - palisades, floor tiles, stakewalls - and the two-renderer ones like
    // vines and log poles that a single-renderer rule turned away.
    //
    // Unlike the combine path, instancing does NOT need a CPU-readable mesh, so it can also take
    // prefabs Mesh.CombineMeshes has to reject.
    internal class InstanceDefinition
    {
        // Past a handful of parts a prefab is left to the combine path: each part is its own
        // draw call per zone, and merging geometry is the cheaper trade by then.
        private const int MaxParts = 8;

        public InstancePart[] Parts;
        public Matrix4x4 LocalOffset;
        public bool CastsShadows;
        // Covers every part and both tiers in the first part's own space; a mesh is rarely centred on its pivot.
        public Bounds MeshBounds;

        private static readonly HashSet<string> s_farOffsetLogged = new HashSet<string>();

        public static bool TryBuild(GameObject prefab, out InstanceDefinition definition, out string rejectReason)
        {
            definition = null;
            rejectReason = null;
            if (prefab == null) { rejectReason = "null prefab"; return false; }

            // Only MeshRenderers are silenced when a piece is suppressed, so only they have to be
            // redrawn here; particles and anything else keep drawing themselves as they always did.
            var allRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            if (allRenderers.Length == 0) { rejectReason = "no MeshRenderers"; return false; }

            HashSet<Renderer> lod0Set, lodControlled;
            IndexLodMembership(prefab, out lod0Set, out lodControlled);
            var damageStateRenderers = CollectDamageStateRenderers(prefab);

            var visible = new List<MeshRenderer>();
            for (int i = 0; i < allRenderers.Length; i++)
            {
                var renderer = allRenderers[i];
                if (renderer == null || !renderer.enabled) continue;
                if (damageStateRenderers != null && damageStateRenderers.Contains(renderer)) continue;
                if (!IsActiveWithinPrefab(renderer.transform, prefab.transform)) continue;
                bool isLodControlled = lodControlled != null && lodControlled.Contains(renderer);
                if (isLodControlled && (lod0Set == null || !lod0Set.Contains(renderer))) continue;

                visible.Add(renderer);
            }

            if (visible.Count == 0) { rejectReason = "no visible renderer"; return false; }
            if (visible.Count > 1 && !FiresEasyBakeMeshesPlugin.BatchingMultiPartPieces.Value)
            {
                rejectReason = "more than one visible renderer";
                return false;
            }

            var parts = new List<InstancePart>(visible.Count);
            Matrix4x4 firstOffset = Matrix4x4.identity, firstInverse = Matrix4x4.identity;
            for (int i = 0; i < visible.Count; i++)
            {
                var renderer = visible[i];
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) { rejectReason = "no MeshFilter/mesh"; return false; }

                var nearMesh = meshFilter.sharedMesh;
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0) { rejectReason = "no material"; return false; }
                if (materials.Length > nearMesh.subMeshCount) { rejectReason = "more materials than submeshes"; return false; }

                var offset = prefab.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                if (i == 0)
                {
                    firstOffset = offset;
                    firstInverse = offset.inverse;
                }

                for (int m = 0; m < materials.Length; m++)
                {
                    var material = materials[m];
                    if (material == null) { rejectReason = "null material"; return false; }
                    if (parts.Count == MaxParts) { rejectReason = "more than " + MaxParts + " parts"; return false; }

                    material.enableInstancing = true;
                    parts.Add(new InstancePart
                    {
                        NearMesh = nearMesh,
                        FarMesh = ResolveFarMesh(prefab, renderer, nearMesh, material, damageStateRenderers, m),
                        SubMesh = m,
                        FromFirst = i == 0 ? Matrix4x4.identity : firstInverse * offset,
                        RenderParams = new RenderParams(material)
                        {
                            shadowCastingMode = renderer.shadowCastingMode,
                            receiveShadows = renderer.receiveShadows,
                            layer = renderer.gameObject.layer,
                            lightProbeUsage = LightProbeUsage.Off,
                            reflectionProbeUsage = renderer.reflectionProbeUsage,
                        },
                    });
                }
            }

            definition = new InstanceDefinition
            {
                Parts = parts.ToArray(),
                LocalOffset = firstOffset,
                CastsShadows = AnyCastsShadows(parts),
                MeshBounds = MergeBounds(parts),
            };
            return true;
        }

        private static bool AnyCastsShadows(List<InstancePart> parts)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].RenderParams.shadowCastingMode != ShadowCastingMode.Off) return true;
            return false;
        }

        // Each part's box is carried through its offset from the first part, because that is the
        // space the group's instance matrices are in.
        private static Bounds MergeBounds(List<InstancePart> parts)
        {
            var merged = new Bounds();
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var box = part.NearMesh.bounds;
                if (part.FarMesh != null && part.FarMesh != part.NearMesh) box.Encapsulate(part.FarMesh.bounds);

                var m = part.FromFirst;
                var e = box.extents;
                var centre = m.MultiplyPoint3x4(box.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == 0)
                {
                    merged = new Bounds(centre, reach * 2f);
                    continue;
                }
                merged.Encapsulate(centre + reach);
                merged.Encapsulate(centre - reach);
            }
            return merged;
        }

        // WearNTear carries three damage-state visuals and toggles them with SetActive at runtime,
        // so on the PREFAB every state's renderers are present at once. A piece only draws as an
        // instance while it is healthy, so the worn and broken subtrees are excluded outright -
        // counting them would make every building piece look like it shows renderers it never does.
        private static HashSet<Renderer> CollectDamageStateRenderers(GameObject prefab)
        {
            var wear = prefab.GetComponent<WearNTear>();
            if (wear == null) return null;

            HashSet<Renderer> excluded = null;
            AddSubtreeRenderers(wear.m_worn, wear.m_new, ref excluded);
            AddSubtreeRenderers(wear.m_broken, wear.m_new, ref excluded);
            return excluded;
        }

        private static void AddSubtreeRenderers(GameObject state, GameObject healthyState, ref HashSet<Renderer> into)
        {
            if (state == null || state == healthyState) return;
            var renderers = state.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                if (into == null) into = new HashSet<Renderer>();
                into.Add(renderers[i]);
            }
        }

        // activeInHierarchy is always false on a prefab asset, so activeSelf is walked up to
        // the prefab root instead to find what would actually be visible once placed.
        private static bool IsActiveWithinPrefab(Transform node, Transform root)
        {
            while (node != null)
            {
                if (!node.gameObject.activeSelf) return false;
                if (node == root) return true;
                node = node.parent;
            }
            return true;
        }

        // Instances draw the far mesh with LOD0's offset, so LOD1 only stands in when it is the single renderer in the
        // LOD1 slot of the group that switches this LOD0 renderer, carries this part's material on the same submesh and
        // sits exactly where LOD0 does. Anything else keeps the near mesh, so distance never moves or drops part of a piece.
        private static Mesh ResolveFarMesh(GameObject prefab, MeshRenderer nearRenderer, Mesh nearMesh, Material material,
            HashSet<Renderer> damageStateRenderers, int subMesh)
        {
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lods = lodGroups[g] != null ? lodGroups[g].GetLODs() : null;
                if (lods == null || lods.Length < 2 || !Lists(lods[0].renderers, nearRenderer)) continue;

                MeshRenderer farRenderer = null;
                var slot = lods[1].renderers;
                for (int i = 0; slot != null && i < slot.Length; i++)
                {
                    if (slot[i] == null) continue;
                    if (farRenderer != null) return nearMesh;
                    farRenderer = slot[i] as MeshRenderer;
                    if (farRenderer == null) return nearMesh;
                }
                if (farRenderer == null || farRenderer == nearRenderer || !farRenderer.enabled) return nearMesh;
                if (damageStateRenderers != null && damageStateRenderers.Contains(farRenderer)) return nearMesh;
                if (!IsActiveWithinPrefab(farRenderer.transform, prefab.transform)) return nearMesh;
                if (!Draws(farRenderer.sharedMaterials, material, subMesh)) return nearMesh;
                var farFilter = farRenderer.GetComponent<MeshFilter>();
                if (farFilter == null || farFilter.sharedMesh == null) return nearMesh;
                if (farFilter.sharedMesh.subMeshCount <= subMesh) return nearMesh;
                if (!SamePlacement(farRenderer.transform.localToWorldMatrix, nearRenderer.transform.localToWorldMatrix))
                {
                    if (s_farOffsetLogged.Add(prefab.name))
                        EasyBakeLog.Info($"[Instancing] '{prefab.name}': LOD1 is placed differently from LOD0, so its instances keep the LOD0 mesh at distance.");
                    return nearMesh;
                }
                return farFilter.sharedMesh;
            }
            return nearMesh;
        }

        private static bool Draws(Material[] materials, Material material, int subMesh)
            => materials != null && subMesh < materials.Length && materials[subMesh] == material;

        private static bool Lists(Renderer[] renderers, Renderer renderer)
        {
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] == renderer) return true;
            return false;
        }

        private static bool SamePlacement(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 0.001f) return false;
            return true;
        }

        private static void IndexLodMembership(GameObject prefab,
            out HashSet<Renderer> lod0Set, out HashSet<Renderer> lodControlled)
        {
            lod0Set = null;
            lodControlled = null;

            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lodGroup = lodGroups[g];
                if (lodGroup == null) continue;
                var lods = lodGroup.GetLODs();
                if (lods == null) continue;

                for (int li = 0; li < lods.Length; li++)
                {
                    var renderers = lods[li].renderers;
                    if (renderers == null) continue;
                    for (int ri = 0; ri < renderers.Length; ri++)
                    {
                        var renderer = renderers[ri];
                        if (renderer == null) continue;
                        if (lodControlled == null) lodControlled = new HashSet<Renderer>();
                        lodControlled.Add(renderer);
                        if (li == 0)
                        {
                            if (lod0Set == null) lod0Set = new HashSet<Renderer>();
                            lod0Set.Add(renderer);
                        }
                    }
                }
            }
        }
    }

    // Definitions are derived from prefab assets, so they are world-independent and
    // built once per prefab. A null entry is a cached rejection, which keeps repeat
    // lookups for non-instanceable prefabs off the component-walk path.
    internal static class InstanceDefinitionCache
    {
        private static readonly Dictionary<int, InstanceDefinition> _byPrefabHash
            = new Dictionary<int, InstanceDefinition>();
        private static readonly Dictionary<string, int> _rejectionsByReason
            = new Dictionary<string, int>();

        public static void Clear()
        {
            _byPrefabHash.Clear();
            _rejectionsByReason.Clear();
        }

        public static bool TryGet(int prefabHash, out InstanceDefinition definition)
        {
            if (_byPrefabHash.TryGetValue(prefabHash, out definition)) return definition != null;

            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            InstanceDefinition built;
            string rejectReason;
            InstanceDefinition.TryBuild(prefab, out built, out rejectReason);
            _byPrefabHash[prefabHash] = built;
            definition = built;

            if (built == null && rejectReason != null)
            {
                int seen;
                _rejectionsByReason.TryGetValue(rejectReason, out seen);
                _rejectionsByReason[rejectReason] = seen + 1;
                if (FiresEasyBakeMeshesPlugin.BatchingVerbose.Value)
                {
                    string prefabName = prefab != null ? prefab.name : prefabHash.ToString();
                    EasyBakeLog.Info($"[Instancing] '{prefabName}' not instanceable: {rejectReason}.");
                }
            }
            return definition != null;
        }

        // One line per bake summarising why prefabs were turned away, so a zone reporting
        // zero instanced groups says what stopped it rather than leaving it to guesswork.
        public static string DescribeRejections()
        {
            if (_rejectionsByReason.Count == 0) return "none";
            var parts = new List<string>(_rejectionsByReason.Count);
            foreach (var entry in _rejectionsByReason)
                parts.Add($"{entry.Key} x{entry.Value}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts.ToArray());
        }
    }
}
