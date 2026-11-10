using System.Collections.Generic;

using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Rebuilds a piece's solid colliders from its prefab at the piece's cached transform and look, without creating the
    // piece. Players, creatures, projectiles and support checks hit the same shapes on the same layers as the real piece;
    // a support check that finds no WearNTear on them treats them as solid ground. A piece whose look hides every model
    // keeps only the colliders outside them, the invisible wall its builder made.
    internal static class StandInColliders
    {
        private sealed class Template
        {
            public Collider Source;
            public Matrix4x4 RootToCollider;
            public int Layer;
            public bool Leaky;   // 1.2.127: vanilla Cover.IsUnderRoof skips "leaky" colliders; a stand-in must too
        }

        private static readonly Template[] NoTemplates = new Template[0];
        private static readonly Dictionary<InstanceKey, Template[]> _byKey = new Dictionary<InstanceKey, Template[]>();

        public static void Clear() => _byKey.Clear();

        // Returns the object holding this piece's stand-in colliders, or null when the piece has none to stand in.
        public static GameObject Build(Transform root, MeshBaker.PieceIdentity identity, MeshBaker.PieceTransform piece, out int colliders)
        {
            colliders = 0;
            var templates = GetTemplates(new InstanceKey(identity.PrefabHash, piece.Look));
            if (templates.Length == 0) return null;

            var pieceMatrix = Matrix4x4.TRS(piece.Position, piece.Rotation, piece.Scale);
            if (templates.Length == 1)
            {
                colliders = 1;
                return Mark(CreateCollider(root, templates[0], pieceMatrix), identity, piece);
            }

            var holder = new GameObject("StandIn");
            holder.transform.SetParent(root, false);
            for (int i = 0; i < templates.Length; i++)
                CreateCollider(holder.transform, templates[i], pieceMatrix);
            colliders = templates.Length;
            return Mark(holder, identity, piece);
        }

        // 1.2.130: the piece this stands in for, for other mods (EbmStandIns.StandsInFor)
        private static GameObject Mark(GameObject go, MeshBaker.PieceIdentity identity, MeshBaker.PieceTransform piece)
        {
            var mark = go.AddComponent<EbmStandIn>();
            mark.PrefabHash = identity.PrefabHash;
            mark.Position = piece.Position;
            return go;
        }

        private static GameObject CreateCollider(Transform parent, Template template, Matrix4x4 pieceMatrix)
        {
            Matrix4x4 world = pieceMatrix * template.RootToCollider;
            var go = new GameObject("StandInCollider") { layer = template.Layer };
            if (template.Leaky) go.tag = "leaky";
            var transform = go.transform;
            transform.SetParent(parent, false);
            transform.SetPositionAndRotation(world.GetColumn(3), world.rotation);
            transform.localScale = world.lossyScale;
            Copy(template.Source, go);
            return go;
        }

        private static Template[] GetTemplates(InstanceKey key)
        {
            if (_byKey.TryGetValue(key, out var cached)) return cached;
            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(key.PrefabHash) : null;
            if (prefab == null) return NoTemplates;

            var root = prefab.transform;
            var list = new List<Template>();
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled || collider.isTrigger) continue;
                if (!WearLooks.IsShown(prefab, key.Look, collider.transform)) continue;
                list.Add(new Template
                {
                    Source = collider,
                    RootToCollider = root.worldToLocalMatrix * collider.transform.localToWorldMatrix,
                    Layer = collider.gameObject.layer,
                    Leaky = collider.gameObject.CompareTag("leaky"),
                });
            }

            var templates = list.ToArray();
            _byKey[key] = templates;
            return templates;
        }

        private static void Copy(Collider source, GameObject target)
        {
            switch (source)
            {
                case BoxCollider box:
                    var boxCopy = target.AddComponent<BoxCollider>();
                    boxCopy.center = box.center;
                    boxCopy.size = box.size;
                    boxCopy.sharedMaterial = box.sharedMaterial;
                    break;
                case SphereCollider sphere:
                    var sphereCopy = target.AddComponent<SphereCollider>();
                    sphereCopy.center = sphere.center;
                    sphereCopy.radius = sphere.radius;
                    sphereCopy.sharedMaterial = sphere.sharedMaterial;
                    break;
                case CapsuleCollider capsule:
                    var capsuleCopy = target.AddComponent<CapsuleCollider>();
                    capsuleCopy.center = capsule.center;
                    capsuleCopy.radius = capsule.radius;
                    capsuleCopy.height = capsule.height;
                    capsuleCopy.direction = capsule.direction;
                    capsuleCopy.sharedMaterial = capsule.sharedMaterial;
                    break;
                case MeshCollider mesh:
                    var meshCopy = target.AddComponent<MeshCollider>();
                    meshCopy.cookingOptions = mesh.cookingOptions;
                    meshCopy.convex = mesh.convex;
                    meshCopy.sharedMesh = mesh.sharedMesh;
                    meshCopy.sharedMaterial = mesh.sharedMaterial;
                    break;
            }
        }
    }
}
