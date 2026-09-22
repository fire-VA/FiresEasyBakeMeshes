using System;
using System.Collections.Generic;
using FiresCore.Pieces;
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
            public GameObject Prefab;
            public float DefaultHealth;
            public bool AllImmune;
            public bool SyncsScale;
            public Vector3 PrefabScale;
            // Components the bake cannot stand in for that sit under a wear model, so whether they run depends on the look.
            public readonly List<Component> LookDependentBlockers = new List<Component>();
            private readonly Dictionary<WearLook, string> _blockerByLook = new Dictionary<WearLook, string>();

            public string BlockerFor(WearLook look)
            {
                if (LookDependentBlockers.Count == 0) return null;
                if (_blockerByLook.TryGetValue(look, out string blocker)) return blocker;
                blocker = null;
                for (int i = 0; i < LookDependentBlockers.Count && blocker == null; i++)
                    if (WearLooks.IsShown(Prefab, look, LookDependentBlockers[i].transform)) blocker = LookDependentBlockers[i].GetType().Name;
                _blockerByLook[look] = blocker;
                return blocker;
            }
        }

        // RandomMaterialValues only tints renderers the bake already replaces. SimpleMeshCombine (its own assembly) is an
        // editor tool with no runtime callbacks. DropOnDestroyed has no behaviour of its own: its Awake subscribes to
        // WearNTear.m_onDestroyed and it runs only when that fires, and ZdoMatches skips nothing but pieces that event
        // cannot reach. A breakable Ashlands ruin still fails ZdoMatches on its health and is created for real, so it
        // can still be mined for its drops.
        private static readonly HashSet<Type> AllowedComponents = new HashSet<Type>
        {
            typeof(Transform), typeof(MeshFilter), typeof(MeshRenderer), typeof(LODGroup),
            typeof(BoxCollider), typeof(SphereCollider), typeof(CapsuleCollider), typeof(MeshCollider),
            typeof(Piece), typeof(WearNTear), typeof(ZNetView), typeof(RandomMaterialValues),
            typeof(DropOnDestroyed),
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

            // ZNetView is the only one of the three the skip needs: skipping means never creating the object a ZDO
            // asks for, which only applies to a ZDO-backed prefab. Piece and WearNTear are the opposite of blockers.
            // A prefab with no WearNTear cannot be damaged, cannot be destroyed and has no worn or broken look to
            // diverge into, which is exactly the invulnerability ZdoMatches spends its health check establishing for
            // the prefabs that do have one. Demanding it kept the most static pieces in the world out of the skip.
            if (view == null) return NotSkippable("no ZNetView on its root");
            if (piece != null && piece.m_comfort > 0) return NotSkippable("comfort");
            if (ZoneTracker.IsExcludedFromBaking(prefab)) return NotSkippable("excluded from baking by its mod or [Batching] ExcludedPrefabs");

            var info = new PrefabInfo
            {
                Skippable = true,
                Prefab = prefab,
                DefaultHealth = wear != null ? wear.m_health : 0f,
                AllImmune = wear == null || InvulnerableClassifier.AllImmune(wear.m_damages),
                SyncsScale = view.m_syncInitialScale,
                PrefabScale = prefab.transform.localScale,
            };

            // Only what can ever run matters. A child inactive in the prefab stays inactive, since no allowed component
            // switches objects on - except WearNTear, whose models follow the piece's look, so what sits under them is
            // judged per look.
            var components = prefab.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null) return NotSkippable("a missing script");
                var type = component.GetType();
                if (AllowedComponents.Contains(type) || AllowedComponentNames.Contains(type.Name)) continue;
                if (wear != null && UnderWearModel(component.transform, wear))
                {
                    info.LookDependentBlockers.Add(component);
                    continue;
                }
                if (!ActiveWithinPrefab(component.transform, prefab.transform)) continue;
                return NotSkippable(type.Name);
            }
            return info;
        }

        // The live ZDO must still describe the piece the cache drew: invulnerable, the same look, rotation and scale.
        // Position is part of the identity the caller matched on.
        public static bool ZdoMatches(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached)
        {
            if (!info.Skippable) return false;
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) return false;
            if (!info.AllImmune && !(StoredHealth(zdo, info) < 0f)) return false;
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (look != cached.Look || info.BlockerFor(look) != null) return false;
            if (Quaternion.Angle(zdo.GetRotation(), cached.Rotation) > 0.5f) return false;
            return (ExpectedScale(zdo, info) - cached.Scale).sqrMagnitude < 0.0001f;
        }

        // Which of ZdoMatches' checks fails, for ebm_census; null when the piece matches.
        public static string DescribeMismatch(ZDO zdo, PrefabInfo info, MeshBaker.PieceTransform cached, out string detail)
        {
            detail = null;
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab()))
            {
                detail = PieceData.DescribeLiveEdits(zdo, zdo.GetPrefab());
                return "a field edit or Structure Tweaks key on it changes what the piece shows or offers, which only a real piece carries";
            }

            float health = StoredHealth(zdo, info);
            if (!info.AllImmune && !(health < 0f))
            {
                detail = $"server health {health:F0}, prefab damage modifiers not all Immune, so only this copy counts as invulnerable";
                return "its server health is not below zero (invulnerable on this copy only)";
            }
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (look != cached.Look)
            {
                detail = $"it shows {look}, the bake drew {cached.Look}";
                return "its look changed since the bake";
            }
            string blocker = info.BlockerFor(look);
            if (blocker != null)
            {
                detail = $"its {look} look switches on a {blocker}";
                return "its look shows a component the bake cannot stand in for";
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

        // The health WearNTear's damage gate reads: the ZDO's, defaulting to max health, which a field edit can replace.
        private static float StoredHealth(ZDO zdo, PrefabInfo info)
        {
            float maxHealth = info.DefaultHealth;
            if (zdo.GetBool(WearLooks.FieldsKey) && zdo.GetBool(WearLooks.WearNTearFieldsKey)
                && zdo.GetFloat(WearLooks.MaxHealthKey, out float editedMaxHealth))
                maxHealth = editedMaxHealth;
            return zdo.GetFloat(ZDOVars.s_health, maxHealth);
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

        private static bool UnderWearModel(Transform node, WearNTear wear)
        {
            return Under(node, wear.m_new) || Under(node, wear.m_worn) || Under(node, wear.m_broken);
        }

        private static bool Under(Transform node, GameObject model)
        {
            return model != null && (node == model.transform || node.IsChildOf(model.transform));
        }
    }
}
