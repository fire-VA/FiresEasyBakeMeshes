using UnityEngine;

namespace FiresEasyBakeMeshes
{
    // 1.2.130 ([seasons] 10-26): marks an EBM stand-in collider with the piece it stands in for, so another mod's roof / shelter test
    // can tell "this is the piece itself" from "another piece above me". No Update; data only. Add-only contract.
    public sealed class EbmStandIn : MonoBehaviour
    {
        public int PrefabHash;
        public Vector3 Position;
    }

    public static class EbmStandIns
    {
        // True when the collider is an EBM stand-in for this piece (same prefab, same spot within 5 cm).
        public static bool StandsInFor(Collider collider, GameObject piece)
        {
            if (collider == null || piece == null) return false;
            var mark = collider.GetComponentInParent<EbmStandIn>();
            if (mark == null) return false;
            var view = piece.GetComponent<ZNetView>();
            int hash = view != null && view.GetZDO() != null ? view.GetZDO().GetPrefab() : Utils.GetPrefabName(piece).GetStableHashCode();
            return hash == mark.PrefabHash && (piece.transform.position - mark.Position).sqrMagnitude < 0.0025f;
        }

        // True when the collider is any EBM stand-in.
        public static bool IsStandIn(Collider collider) => collider != null && collider.GetComponentInParent<EbmStandIn>() != null;
    }
}

namespace FiresEasyBakeMeshes
{
    // 1.2.133 ([ghost] live map): add-only contract. One baked piece: prefab hash (ZNetScene.GetPrefab(hash)), world pose, and
    // whether it is SKIPPED right now (no live object, only an EBM stand-in collider).
    public struct EbmBakedPiece
    {
        public int PrefabHash;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public bool Skipped;
    }

    public static class EbmBakedPieces
    {
        // Appends every baked piece within radius (horizontal) of centre; returns how many were added.
        public static int Near(Vector3 centre, float radius, System.Collections.Generic.List<EbmBakedPiece> into)
            => into == null ? 0 : EasyBake.ZoneTracker.BakedPiecesNear(centre, radius, into);

        // 1.2.197 ([generator] piece-create audit): true when EBM's bake draws this piece in place of its own renderers (its zone is
        // baked and the bake holds this exact piece). Valid from ZNetView.Awake on, so a visual-only Piece / WearNTear Awake hook may
        // skip its renderer work. Main thread only. Add-only contract.
        public static bool Draws(GameObject piece) => EasyBake.ZoneTracker.DrawsPiece(piece);

        // 1.2.198 ([saddles] FAP 1.1.407, [generator]): raised on the main thread right after a live piece the bake was drawing gets its own
        // renderers back (zone unbake / dirty / teardown, build-tool hold, damage). With Draws() this is the whole contract: every piece
        // Draws() said true for either stays drawn by the bake or gets exactly this call (a piece being handed back as a new object is
        // never reported by Draws). A listener that throws is removed after one Warning naming it. Add-only contract.
        public static event System.Action<GameObject> PieceLeftBake;
        private static BepInEx.Logging.ManualLogSource s_log;

        internal static void RaiseLeft(GameObject piece)
        {
            var handlers = PieceLeftBake;
            if (handlers == null || piece == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try { ((System.Action<GameObject>)d)(piece); }
                catch (System.Exception e)
                {
                    PieceLeftBake -= (System.Action<GameObject>)d;
                    (s_log ?? (s_log = BepInEx.Logging.Logger.CreateLogSource("FiresEasyBakeMeshes.Api"))).LogWarning(
                        $"[EBM] a PieceLeftBake listener ({d.Method.DeclaringType?.FullName}.{d.Method.Name}) threw {e.GetType().Name}: {e.Message}; it was removed");
                }
            }
        }
    }
}

namespace FiresEasyBakeMeshes
{
    // 1.2.137 ([ghost] live map ROOFS toggle): add-only contract.
    public static class EbmRoofs
    {
        // A camera that must not draw EBM's baked roof pieces (true), or that may again (false). Each other camera keeps them.
        public static void HideRoofsFor(Camera camera, bool hide) => EasyBake.InstancedDraw.HideRoofsFor(camera, hide);

        // The same roof rule for live pieces: the prefab's name holds "roof" and is not a "wall_roof" gable.
        public static bool IsRoofPrefab(int prefabHash)
        {
            var prefab = ZNetScene.instance != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabHash) : null;
            return prefab != null && IsRoofName(prefab.name);
        }

        public static bool IsRoofName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("roof") && !n.Contains("wall_roof");
        }
    }
}
