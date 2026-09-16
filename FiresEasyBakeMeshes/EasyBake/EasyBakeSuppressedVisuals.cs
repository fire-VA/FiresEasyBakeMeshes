using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Owns visual suppression for one baked piece: silences its renderers and
    // LODGroups when the combined mesh takes over, and restores their exact prior
    // state on teardown.
    //
    // Instantiate copies this component and its recorded state onto any clone, so a
    // clone that carries no ZDO (a placement ghost or a copy-tool preview) restores
    // itself in Start. That keeps cloned ghosts visible without the mod that cloned
    // them needing any compatibility patch.
    public class EasyBakeSuppressedVisuals : MonoBehaviour
    {
        public bool[] RendererPriorEnabled;
        public bool[] LodGroupPriorEnabled;

        public static EasyBakeSuppressedVisuals SuppressPiece(GameObject pieceRoot)
        {
            if (pieceRoot == null) return null;

            var alreadySuppressed = pieceRoot.GetComponent<EasyBakeSuppressedVisuals>();
            if (alreadySuppressed != null) return alreadySuppressed;

            var suppression = pieceRoot.AddComponent<EasyBakeSuppressedVisuals>();
            suppression.SilenceAndRecordPriorState();
            return suppression;
        }

        public void RestoreVisuals()
        {
            var lodGroups = GetComponentsInChildren<LODGroup>(true);
            if (LodGroupPriorEnabled != null)
            {
                int count = Mathf.Min(lodGroups.Length, LodGroupPriorEnabled.Length);
                for (int i = 0; i < count; i++)
                    if (lodGroups[i] != null) lodGroups[i].enabled = LodGroupPriorEnabled[i];
            }

            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            if (RendererPriorEnabled != null)
            {
                int count = Mathf.Min(renderers.Length, RendererPriorEnabled.Length);
                for (int i = 0; i < count; i++)
                    if (renderers[i] != null) renderers[i].enabled = RendererPriorEnabled[i];
            }
        }

        // Records prior state in the same traversal order RestoreVisuals replays, so
        // a clone of this hierarchy pairs its own components to the recorded state.
        private void SilenceAndRecordPriorState()
        {
            var lodGroups = GetComponentsInChildren<LODGroup>(true);
            LodGroupPriorEnabled = new bool[lodGroups.Length];
            for (int i = 0; i < lodGroups.Length; i++)
            {
                if (lodGroups[i] == null) continue;
                LodGroupPriorEnabled[i] = lodGroups[i].enabled;
                lodGroups[i].enabled = false;
            }

            // Traversal includes inactive children so a clone can pair its own
            // components to these arrays by index, but only renderers that are
            // actually drawing get silenced — the combined mesh only stands in for
            // those, and a renderer parked on an inactive child still owes its state
            // to whatever re-activates it.
            var renderers = GetComponentsInChildren<MeshRenderer>(true);
            RendererPriorEnabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                RendererPriorEnabled[i] = renderers[i].enabled;
                if (renderers[i].gameObject.activeInHierarchy) renderers[i].enabled = false;
            }
        }

        private void Start()
        {
            var view = GetComponent<ZNetView>();
            if (view != null && view.IsValid()) return;
            RestoreVisuals();
            Destroy(this);
        }
    }
}
