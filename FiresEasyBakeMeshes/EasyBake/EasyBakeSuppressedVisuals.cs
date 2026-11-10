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
        [System.NonSerialized] internal bool Restored;   // 1.2.255: the bake gave this piece back; the component is only waiting to be destroyed

        public static EasyBakeSuppressedVisuals SuppressPiece(GameObject pieceRoot)
        {
            if (pieceRoot == null) return null;
            Indirect.Coverage.CoveredObject.ReleaseFor(pieceRoot);   // 1.2.209 ([ghost]): a covered live piece the bake takes over is handed back first (never drawn twice)

            var alreadySuppressed = pieceRoot.GetComponent<EasyBakeSuppressedVisuals>();
            if (alreadySuppressed != null) return alreadySuppressed;

            var suppression = pieceRoot.AddComponent<EasyBakeSuppressedVisuals>();
            suppression.SilenceAndRecordPriorState();
            EbmHandoffCensus.NoteHide();   // [seasons] 11-09 E28 census
            return suppression;
        }

        // 1.2.257 (per-object create allocations, item 1): the hierarchy is read into reused scratch lists (the List overload walks in the same
        // order as the array one), and a piece with no LODGroup / no renderer shares one empty array instead of a new bool[0]. The bool[]
        // fields stay as they were: Instantiate copies them onto clones, which pair them by index. The lists are cleared after each use and never
        // stored; main thread only (Unity calls). Separate lists for record and restore, so neither can see the other's contents.
        private static readonly System.Collections.Generic.List<LODGroup> s_lodScratch = new System.Collections.Generic.List<LODGroup>(4);
        private static readonly System.Collections.Generic.List<MeshRenderer> s_rendScratch = new System.Collections.Generic.List<MeshRenderer>(32);
        private static readonly System.Collections.Generic.List<LODGroup> s_lodRestore = new System.Collections.Generic.List<LODGroup>(4);
        private static readonly System.Collections.Generic.List<MeshRenderer> s_rendRestore = new System.Collections.Generic.List<MeshRenderer>(32);
        private static readonly bool[] s_noFlags = new bool[0];
        internal static long AllocsAvoided;   // estimate: 1 per array GetComponentsInChildren skipped + 1 per empty bool[] shared

        private static bool[] Flags(int n) { if (n == 0) { AllocsAvoided++; return s_noFlags; } return new bool[n]; }

        public void RestoreVisuals()
        {
            if (EbmHandoffCensus.Enabled != null && EbmHandoffCensus.Enabled.Value)   // [visual] 1257 merge: the GetComponent only runs when the census is on
            {
                var censusView = GetComponent<ZNetView>();
                if (censusView != null && censusView.IsValid()) EbmHandoffCensus.NoteShow();   // [seasons] 11-09 E28 census (a ZDO-less clone is not a hand-back)
            }
            var lodGroups = s_lodRestore;
            GetComponentsInChildren(true, lodGroups); AllocsAvoided++;
            try
            {
                if (LodGroupPriorEnabled != null)
                {
                    int count = Mathf.Min(lodGroups.Count, LodGroupPriorEnabled.Length);
                    for (int i = 0; i < count; i++)
                        if (lodGroups[i] != null) lodGroups[i].enabled = LodGroupPriorEnabled[i];
                }
            }
            finally { lodGroups.Clear(); }

            var renderers = s_rendRestore;
            GetComponentsInChildren(true, renderers); AllocsAvoided++;
            try
            {
                if (RendererPriorEnabled != null)
                {
                    int count = Mathf.Min(renderers.Count, RendererPriorEnabled.Length);
                    var wear = GetComponent<WearNTear>();
                    for (int i = 0; i < count; i++)
                        if (renderers[i] != null && !UnderSnowCap(renderers[i].transform, wear)) renderers[i].enabled = RendererPriorEnabled[i];   // 1.2.179: caps stay vanilla's
                }
            }
            finally { renderers.Clear(); }
            // 1.2.198 ([saddles] / [generator]): the one point a live piece the bake drew gets its own renderers back (unbake, dirty,
            // teardown, build hold, damage): visual hooks that deferred on EbmBakedPieces.Draws run now
            FiresEasyBakeMeshes.EbmBakedPieces.RaiseLeft(gameObject);
            Restored = true;
            Indirect.Coverage.CoveredObject.BakeEnded(gameObject);
        }

        // Records prior state in the same traversal order RestoreVisuals replays, so
        // a clone of this hierarchy pairs its own components to the recorded state.
        private void SilenceAndRecordPriorState()
        {
            var lodGroups = s_lodScratch;
            GetComponentsInChildren(true, lodGroups); AllocsAvoided++;
            try
            {
                LodGroupPriorEnabled = Flags(lodGroups.Count);
                for (int i = 0; i < lodGroups.Count; i++)
                {
                    if (lodGroups[i] == null) continue;
                    LodGroupPriorEnabled[i] = lodGroups[i].enabled;
                    lodGroups[i].enabled = false;
                }
            }
            finally { lodGroups.Clear(); }

            // Traversal includes inactive children so a clone can pair its own
            // components to these arrays by index, but only renderers that are
            // actually drawing get silenced — the combined mesh only stands in for
            // those, and a renderer parked on an inactive child still owes its state
            // to whatever re-activates it.
            var renderers = s_rendScratch;
            GetComponentsInChildren(true, renderers); AllocsAvoided++;
            var wear = GetComponent<WearNTear>();
            int caps = 0;
            try
            {
                RendererPriorEnabled = Flags(renderers.Count);
                for (int i = 0; i < renderers.Count; i++)
                {
                    if (renderers[i] == null) continue;
                    RendererPriorEnabled[i] = renderers[i].enabled || Indirect.Coverage.CoveredObject.HiddenByCoverage(renderers[i]);   // 1.2.21x: a part coverage switched off was shown: record (and later restore) it as enabled
                    if (UnderSnowCap(renderers[i].transform, wear)) { caps++; continue; }
                    if (renderers[i].gameObject.activeInHierarchy) renderers[i].enabled = false;
                }
            }
            finally { renderers.Clear(); }
            if (caps > 0 && CapsNewHere())
                EasyBakeLog.Debug($"[Bake] {Utils.GetPrefabName(gameObject.name)}: snow caps left to vanilla: {caps}");
        }

        // 1.2.179 ([seasons] from Fire's [PieceSnow] dump: "snow mesh [floor_2x2_snow off ...], own renderers 0/21 on" on a baked pier with buildup over 0.25):
        // the combined mesh leaves snow caps out on purpose (WearLooks.IsSnowCap), and vanilla's WearNTear.UpdateSnowVisual only switches the cap's
        // GameObject (m_snow / m_snowWorn / m_snowBroken .gameObject.SetActive), never renderer.enabled. So a cap renderer EBM switched off stayed
        // invisible for good. Their renderers, and their children's, are never silenced: vanilla owns them, on a baked piece as on a live one.
        private static readonly System.Collections.Generic.HashSet<string> s_capsSaid = new System.Collections.Generic.HashSet<string>();
        private static readonly System.Collections.Generic.HashSet<int> s_capsSaidHash = new System.Collections.Generic.HashSet<int>();

        // 1.2.255 [Optimize] CacheLowGarbage: the object's name is a new string each read, so it is read only for a line that will really be written (keyed by the ZDO's prefab hash).
        private bool CapsNewHere()
        {
            if (!EasyBake.MeshCacheStore.LowGarbageOn) return s_capsSaid.Add(Utils.GetPrefabName(gameObject.name));
            if (!EasyBakeLog.DebugOn()) return false;
            var view = GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            return zdo != null ? s_capsSaidHash.Add(zdo.GetPrefab()) : s_capsSaid.Add(Utils.GetPrefabName(gameObject.name));
        }

        private bool UnderSnowCap(Transform t, WearNTear wear)
        {
            if (wear == null) return false;
            for (; t != null && t != transform; t = t.parent)
                if ((wear.m_snow != null && t == wear.m_snow.transform)
                    || (wear.m_snowWorn != null && t == wear.m_snowWorn.transform)
                    || (wear.m_snowBroken != null && t == wear.m_snowBroken.transform)) return true;
            return false;
        }

        // 1.2.184 (lead 11-07, stone_wall_2x1 body invisible, cap showing): every live suppression, for ZoneTracker's Debug orphan census.
        // Changes bumps on every add / destroy, so the census only scans after something changed.
        internal static readonly System.Collections.Generic.HashSet<EasyBakeSuppressedVisuals> All = new System.Collections.Generic.HashSet<EasyBakeSuppressedVisuals>();
        internal static int Changes;

        private void Awake() { All.Add(this); Changes++; }
        private void OnDestroy() { All.Remove(this); Changes++; }

        private void Start()
        {
            var view = GetComponent<ZNetView>();
            if (view != null && view.IsValid()) return;
            RestoreVisuals();
            Destroy(this);
        }
    }
}
