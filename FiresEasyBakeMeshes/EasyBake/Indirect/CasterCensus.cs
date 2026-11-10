using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    /// <summary>
    /// [ghost] 1.2.216 ([lead]: 1,184 non-EBM shadow-caster draws a frame at the dvergr town; which renderers are they?). Census only,
    /// no behaviour change, Debug only: every 30 s, one walk of the loaded vanilla renderers (MeshRenderer and SkinnedMeshRenderer) that
    /// cast shadows, are enabled and not hidden by coverage, and that Unity drew last frame in some view (isVisible: the camera OR a shadow
    /// pass). Grouped by owning prefab (the ZNetView above it), by layer, by distance band from the camera, and by "past the sun's shadow
    /// distance" (QualitySettings.shadowDistance: casters beyond it can't land in the camera's shadows, unless they're very tall).
    /// Answers which casters coverage or a caster rule could take next.
    /// </summary>
    internal static class CasterCensus
    {
        private static float s_next;
        private static readonly Dictionary<string, int> s_byPrefab = new Dictionary<string, int>();
        private static readonly Dictionary<int, int> s_byLayer = new Dictionary<int, int>();

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 30f;
            if (!EasyBakeLog.DebugOn() || FiresEasyBakeMeshesPlugin.RendererCensus == null || !FiresEasyBakeMeshesPlugin.RendererCensus.Value) return;   // 1.2.218: a scene-wide FindObjectsByType, opt-in only
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            float shadowDist = QualitySettings.shadowDistance;
            s_byPrefab.Clear(); s_byLayer.Clear();
            int total = 0, skinned = 0, beyondShadow = 0, noView = 0;
            var bands = new int[4];
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r == null || !r.enabled || r.forceRenderingOff || r.shadowCastingMode == ShadowCastingMode.Off || !r.isVisible) continue;
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (!r.gameObject.activeInHierarchy) continue;
                total++;
                if (r is SkinnedMeshRenderer) skinned++;
                var view = r.GetComponentInParent<ZNetView>();
                string name = view != null ? Utils.GetPrefabName(view.gameObject) : "(no ZNetView) " + r.transform.root.name;
                if (view == null) noView++;
                s_byPrefab.TryGetValue(name, out int n);
                if (n > 0 || s_byPrefab.Count < 512) s_byPrefab[name] = n + 1;
                s_byLayer.TryGetValue(r.gameObject.layer, out int l); s_byLayer[r.gameObject.layer] = l + 1;
                float d = Vector3.Distance(cp, r.bounds.center);
                bands[d < 32f ? 0 : d < 64f ? 1 : d < 150f ? 2 : 3]++;
                if (d - r.bounds.extents.magnitude > shadowDist) beyondShadow++;
            }
            var list = new List<KeyValuePair<string, int>>(s_byPrefab);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder($"[CasterCensus] vanilla shadow casters drawn last frame: {total} ({skinned} skinned, {noView} without a ZNetView); by distance <32 m {bands[0]}, 32-64 {bands[1]}, 64-150 {bands[2]}, 150+ {bands[3]}; past the {shadowDist:0} m shadow distance {beyondShadow}");
            sb.Append("\n  by layer:");
            foreach (var kv in s_byLayer) sb.Append(' ').Append(LayerMask.LayerToName(kv.Key)).Append(' ').Append(kv.Value).Append(';');
            sb.Append("\n  top prefabs:");
            for (int i = 0; i < list.Count && i < 20; i++) sb.Append(' ').Append(list[i].Key).Append(' ').Append(list[i].Value).Append(';');
            EasyBakeLog.Debug(sb.ToString());
        }
    }
}
