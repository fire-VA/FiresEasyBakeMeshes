using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.275 [Optimize] LightCensus (lead 11-10; profile at swamp spot4 on 1.2.274: RenderDeferred.Lighting 1.30 ms is the top item now).
    /// Census only, changes nothing: every 30 s (one FindObjectsByType per window, Debug) every enabled Light by type x shadows x render mode,
    /// whether its sphere touches the main camera's view frustum, its rough screen size (range / distance for point and spot lights), who owns
    /// it (vanilla LightLod-managed or not) and the top roots. Deferred rendering draws one light volume per point / spot light that reaches
    /// the view, plus the sun's full-screen pass with its cascades; Unity already skips lights wholly outside the frustum, so this says what
    /// is left that could be cut (tiny on-screen lights, shadowed lights whose shadow cannot be seen) before any cut is built.
    /// </summary>
    internal static class EbmLightCensus
    {
        private static bool On => FiresEasyBakeMeshesPlugin.LightCensus != null && FiresEasyBakeMeshesPlugin.LightCensus.Value;
        private static float s_next;
        private static readonly Plane[] s_planes = new Plane[6];

        internal static void Tick()
        {
            if (!s_hooked && CutOn) HookCut();
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 30f;
            if (!On || !EasyBakeLog.DebugOn() || Player.m_localPlayer == null) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            try { EasyBakeLog.Debug(Line(cam)); } catch (System.Exception e) { EasyBakeLog.Debug("[LightCensus] failed: " + e.Message); }
        }

        // ---- 1.2.275 [Optimize] LightTinyCut (lead 11-10: results, not only measuring). Point and spot lights NOT managed by vanilla's LightLod
        // whose sphere is tiny on screen: under LightTinyShadowScreen (fraction of the screen height) they skip their shadow, under LightTinyOffScreen
        // the light itself is skipped. Per frame only: set in the main camera's pre-cull, put back in its post-render, so nothing persists and
        // every other camera and the next frame see the light exactly as the game left it. Only what WE changed is put back, and only if it is
        // still in the state we left it. Candidates refreshed once a second (one FindObjectsByType); per frame = one distance per candidate.
        private static bool CutOn => FiresEasyBakeMeshesPlugin.LightTinyCut != null && FiresEasyBakeMeshesPlugin.LightTinyCut.Value;
        private static readonly List<Light> s_cand = new List<Light>(128);
        private static readonly List<Light> s_offShadow = new List<Light>(32);
        private static readonly List<LightShadows> s_offShadowWas = new List<LightShadows>(32);
        private static readonly List<Light> s_offLight = new List<Light>(32);
        private static float s_nextCand;
        private static bool s_hooked;
        private static int c_frames, c_shadowsCut, c_lightsCut, c_candidates;

        internal static void HookCut()
        {
            if (s_hooked) return;
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return;
            s_hooked = true;
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
        }

        private static void PreCull(Camera cam)
        {
            if (!CutOn || cam == null || cam != Utils.GetMainCamera()) return;
            float now = Time.unscaledTime;
            if (now >= s_nextCand)
            {
                s_nextCand = now + 1f;
                s_cand.Clear();
                foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (l != null && (l.type == LightType.Point || l.type == LightType.Spot) && l.GetComponent<LightLod>() == null) s_cand.Add(l);
                c_candidates = s_cand.Count;
            }
            if (s_cand.Count == 0) return;
            float shadowAt = FiresEasyBakeMeshesPlugin.LightTinyShadowScreen != null ? FiresEasyBakeMeshesPlugin.LightTinyShadowScreen.Value : 0.03f;
            float offAt = FiresEasyBakeMeshesPlugin.LightTinyOffScreen != null ? FiresEasyBakeMeshesPlugin.LightTinyOffScreen.Value : 0.01f;
            Vector3 cp = cam.transform.position;
            float fov = Mathf.Max(1f, cam.fieldOfView);
            c_frames++;
            for (int i = 0; i < s_cand.Count; i++)
            {
                var l = s_cand[i];
                if (l == null || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(cp, l.transform.position);
                if (d <= l.range) continue;   // the camera is inside its reach: never touched
                float frac = (2f * Mathf.Atan(l.range / d) * Mathf.Rad2Deg) / fov;
                if (frac < offAt) { l.enabled = false; s_offLight.Add(l); c_lightsCut++; }
                else if (frac < shadowAt && l.shadows != LightShadows.None) { s_offShadowWas.Add(l.shadows); l.shadows = LightShadows.None; s_offShadow.Add(l); c_shadowsCut++; }
            }
        }

        private static void PostRender(Camera cam)
        {
            if (cam == null || (s_offLight.Count == 0 && s_offShadow.Count == 0) || cam != Utils.GetMainCamera()) return;
            for (int i = 0; i < s_offLight.Count; i++) { var l = s_offLight[i]; if (l != null && !l.enabled) l.enabled = true; }
            for (int i = 0; i < s_offShadow.Count; i++) { var l = s_offShadow[i]; if (l != null && l.shadows == LightShadows.None) l.shadows = s_offShadowWas[i]; }
            s_offLight.Clear(); s_offShadow.Clear(); s_offShadowWas.Clear();
        }

        private static string CutLine()
        {
            if (c_frames == 0) return CutOn ? "; tiny-light cut: no frames yet" : "; tiny-light cut off";
            string s = $"; tiny-light cut (LightTinyCut) over {c_frames} frame(s): {c_candidates} candidate(s) (point/spot, not LightLod), per frame {c_shadowsCut / (float)c_frames:0.0} shadow(s) skipped, {c_lightsCut / (float)c_frames:0.0} light(s) skipped";
            c_frames = c_shadowsCut = c_lightsCut = 0;
            return s;
        }

        private static string Line(Camera cam)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            Vector3 cp = cam.transform.position;
            int total = 0, dir = 0, dirShadow = 0;
            int pt = 0, ptView = 0, ptShadow = 0, ptShadowView = 0, ptTiny = 0, ptLod = 0, ptImportant = 0;
            int sp = 0, spView = 0, spShadow = 0, spTiny = 0;
            var roots = new Dictionary<string, int>();
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0f) continue;
                total++;
                bool shadow = l.shadows != LightShadows.None;
                if (l.type == LightType.Directional) { dir++; if (shadow) dirShadow++; continue; }
                Vector3 p = l.transform.position;
                float d = Vector3.Distance(cp, p);
                bool view = d <= l.range || GeometryUtility.TestPlanesAABB(s_planes, new Bounds(p, Vector3.one * (2f * l.range)));
                // screen size: the light's sphere angular size vs the vertical fov; under 2 % of the screen height = tiny
                float frac = d <= l.range ? 1f : (2f * Mathf.Atan(l.range / d) * Mathf.Rad2Deg) / Mathf.Max(1f, cam.fieldOfView);
                bool tiny = view && frac < 0.02f;
                if (l.type == LightType.Point)
                {
                    pt++; if (view) ptView++; if (shadow) { ptShadow++; if (view) ptShadowView++; } if (tiny) ptTiny++;
                    if (l.GetComponent<LightLod>() != null) ptLod++;
                    if (l.renderMode == LightRenderMode.ForcePixel) ptImportant++;
                }
                else if (l.type == LightType.Spot) { sp++; if (view) spView++; if (shadow) spShadow++; if (tiny) spTiny++; }
                if (view)
                {
                    var r = l.transform.root;
                    string n = (r != null ? r.name : l.name).Replace("(Clone)", "");
                    roots.TryGetValue(n, out int c); roots[n] = c + 1;
                }
            }
            var sb = new StringBuilder(512);
            sb.Append($"[LightCensus] {total} enabled light(s), walk {sw.Elapsed.TotalMilliseconds:0.0} ms: directional {dir} ({dirShadow} shadowed, cascades {QualitySettings.shadowCascades}, shadow distance {QualitySettings.shadowDistance:0} m)");
            sb.Append($" | point {pt}: reaching the view {ptView}, shadowed {ptShadow} ({ptShadowView} in view), tiny on screen (< 2 % height) {ptTiny}, LightLod-managed {ptLod}, ForcePixel {ptImportant}");
            sb.Append($" | spot {sp}: in view {spView}, shadowed {spShadow}, tiny {spTiny}; pixel light count {QualitySettings.pixelLightCount}");
            var l2 = new List<KeyValuePair<string, int>>(roots); l2.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.Append(" | top roots of lights in view:");
            for (int i = 0; i < l2.Count && i < 8; i++) sb.Append($" {l2[i].Key} {l2[i].Value}");
            sb.Append(CutLine());
            return sb.ToString();
        }
    }
}
