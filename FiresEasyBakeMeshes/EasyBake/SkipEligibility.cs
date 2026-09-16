using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Decides which baked pieces may stay uncreated on this client. Only pure structure qualifies: every component in
    // the prefab must be one the combined mesh and a stand-in collider fully reproduce, so a piece that crafts, stores,
    // lights, protects, comforts, animates or reacts in any way is always created for real.
    internal static class SkipEligibility
    {
        internal sealed class PrefabInfo
        {
            public bool Skippable;
            // What stops a prefab from being skipped, for ebm_census.
            public string Blocker;
            public float DefaultHealth;
            public bool AllImmune;
            public bool SyncsScale;
            public Vector3 PrefabScale;
        }

        // RandomMaterialValues only tints renderers the bake already replaces. SimpleMeshCombine (its own assembly) is an
        // editor tool with no runtime callbacks.
        private static readonly HashSet<Type> AllowedComponents = new HashSet<Type>
        {
            typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(LODGroup),
            typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider), typeof(MeshCollider),
            typeof(Piece), typeof(WearNTear), typeof(ZNetView), typeof(RandomMaterialValues),
        };

        private static readonly HashSet<string> AllowedComponentNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "SimpleMeshCombine", "SimpleMeshCombineMaster",
        };

        private static readonly PrefabInfo NoScene = new PrefabInfo { Blocker = "no scene" };
        private static readonly Dictionary<int, PrefabInfo> _byPrefab = new Dictionary<int, PrefabInfo>();

        public static void Clear() => _byPrefab.Clear();

        public static PrefabInfo Get(int prefabHash)
        {
            if (_byPrefab.TryGetValue(prefabHash, out var info)) return info;
            var scene = ZNetScene.instance;
            if (scene == null) return NoScene;
            info = Build(scene.GetPrefab(prefabHash));
            _byPrefab[prefabHash] = info;
            return info;
        }

        public static bool IsPrefabSkippable(int prefabHash) => Get(prefabHash).Skippable;

        private static PrefabInfo NotSkippable(string blocker) => new PrefabInfo { Blocker = blocker };

        private static PrefabInfo Build(GameObject prefab)
        {
            if (prefab == null) return NotSkippable("an unknown prefab");
            var piece = prefab.GetComponent<Piece>();
            var wear = prefab.GetComponent<WearNTear>();
            var view = prefab.GetComponent<ZNetView>();
            if (piece == null || wear == null || view == null) return NotSkippable("no Piece, WearNTear or ZNetView on its root");
            if (piece.m_comfort > 0) return NotSkippable("comfort");

            // Only what can ever run matters. A child inactive in the prefab stays inactive, since no allowed component
            // switches objects on, and an invulnerable piece never shows its worn or broken state.
            var components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) return NotSkippable("a missing script");
                var type = component.GetType();
                if (AllowedComponents.Contains(type) || AllowedComponentNames.Contains(type.Name)) continue;
                if (!ActiveWithinPrefab(component.transform, prefab.transform) || InDamageState(component.transform, wear)) continue;
                return NotSkippable(type.Name);
            }

            return new PrefabInfo
            {
                Skippable = true,
                DefaultHealth = wear.m_health,
                AllImmune = InvulnerableClassifier.AllImmune(wear.m_damages),
                SyncsScale = view.m_syncInitialScale,
                PrefabScale = prefab.transform.localScale,
            };
        }

        // The live ZDO must still describe the piece the cache drew: invulnerable, same rotation, same scale. Position
        // is part of the identity the caller matched on.
        public static bool ZdoMatches(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached)
        {
            if (!info.Skippable) return false;
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) return false;
            if (!info.AllImmune && !(zdo.GetFloat(ZDOVars.s_health, info.DefaultHealth) < 0f)) return false;
            if (Quaternion.Angle(zdo.GetRotation(), cached.Rotation) > 0.5f) return false;
            return (ExpectedScale(zdo, info) - cached.Scale).sqrMagnitude < 0.0001f;
        }

        // Which of ZdoMatches' checks fails, for ebm_census; null when the piece matches.
        public static string DescribeMismatch(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached, out string detail)
        {
            detail = null;
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab()))
            {
                detail = "vanilla writes these onto the live components in ZNetView.Awake, which never runs for a piece that is not created";
                return "a field edit on it only reaches a real piece (a name, hover text or a swapped model)";
            }

            float health = zdo.GetFloat(ZDOVars.s_health, info.DefaultHealth);
            if (!info.AllImmune && !(health < 0f))
            {
                detail = $"server health {health:F0}, prefab damage modifiers not all Immune, so only this copy counts as invulnerable";
                return "its server health is not below zero (invulnerable on this copy only)";
            }
            float angle = Quaternion.Angle(zdo.GetRotation(), cached.Rotation);
            if (angle > 0.5f)
            {
                detail = $"rotated {angle:F1} degrees from the baked rotation";
                return "rotated since the bake";
            }
            Vector3 expected = ExpectedScale(zdo, info);
            if ((expected - cached.Scale).sqrMagnitude >= 0.0001f)
            {
                detail = $"scale from the ZDO and prefab {expected.ToString("F2")}, baked {cached.Scale.ToString("F2")}, prefab {info.PrefabScale.ToString("F2")}, syncs scale {info.SyncsScale}";
                return "its scale differs from the bake";
            }
            return null;
        }

        // Mirrors ZNetView.Awake: a prefab that syncs its scale takes the ZDO's vector, else its scalar on all three axes,
        // but only when that scalar differs from the prefab's own x; any other prefab keeps its own, possibly non-uniform,
        // scale.
        private static Vector3 ExpectedScale(ZDO zdo, PrefabInfo info)
        {
            if (!info.SyncsScale) return info.PrefabScale;
            Vector3 scale = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.zero);
            if (scale != Vector3.zero) return scale;
            float scalar = zdo.GetFloat(ZDOVars.s_scaleScalarHash, info.PrefabScale.x);
            return info.PrefabScale.x.Equals(scalar) ? info.PrefabScale : new Vector3(scalar, scalar, scalar);
        }

        private static bool ActiveWithinPrefab(Transform node, Transform root)
        {
            while (node != null)
            {
                if (!node.gameObject.activeSelf) return false;
                if (node == root) return true;
                node = node.parent;
            }
            return true;
        }

        private static bool InDamageState(Transform node, WearNTear wear)
        {
            return InSubtree(node, wear.m_worn, wear.m_new) || InSubtree(node, wear.m_broken, wear.m_new);
        }

        private static bool InSubtree(Transform node, GameObject state, GameObject healthyState)
        {
            if (state == null || state == healthyState) return false;
            return node == state.transform || node.IsChildOf(state.transform);
        }
    }
}
