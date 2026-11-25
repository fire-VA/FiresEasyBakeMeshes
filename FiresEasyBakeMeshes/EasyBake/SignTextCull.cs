using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.295-ghost (lead 11-11, [visual]'s town capture report_3434_5199: TextMeshPro/Distance Field (Surface) 2.17 ms = 5.6 % of the GPU frame,
    // 290 draws at town 615). EBM's own code from the 1.0.21 decompile (Sign.cs).
    //
    // VANILLA. Every sign's text is a TextMeshProUGUI on the sign's own world-space Canvas, with TMP's lit Surface shader. A surface shader in
    // a transparent queue draws forward: one ForwardBase pass plus one ForwardAdd per pixel light on the sign (a torch-lit town = several draws
    // per sign). Unity already skips a canvas outside the camera frustum, but nothing skips a sign that is too far to read, so every sign
    // in view costs its draws however small its text is on screen. Separate canvases never batch together, so sharing a material would
    // not merge their draws; the saving has to come from not drawing text nobody can read.
    //
    // OURS. A sign whose text is farther from the camera than SignTextDrawM has its own Canvas component switched off (Unity: a disabled
    // Canvas issues no draws and keeps its built mesh, so switching it back on is not a rebuild). It comes back at SignTextDrawM; it goes
    // only past SignTextDrawM + 2 m, so standing at the edge never flickers. The sign itself (board, post, collider, hover text, editing,
    // vanilla's 2 s text refresh) is untouched: only the text's canvas is switched. A canvas another mod switched off is left alone,
    // and only canvases this code switched off are ever switched back on.
    //  - One flat list, one pass over an eighth of it per frame (a sign's state is at most 8 frames old: at 30 m away and sprint speed, a
    //    few centimetres; the hysteresis band is 2 m). No per-sign Update, no dictionary on the frame path, no allocations.
    //  - Key off: every canvas this switched off is switched back on at once.
    //  - Census (Debug, 30 s): signs known, text drawn / hidden far, switches this window.
    //  - Verify twin (SignTextCullVerify, off): every 30 s, re-measures each sign by its canvas corners (independent of the centre distance
    //    the pass uses) and warns if any sign whose nearest corner is within SignTextDrawM is hidden: the "no change at reading distance" check.
    internal static class SignTextCull
    {
        internal static ConfigEntry<bool> On, Verify;
        internal static ConfigEntry<float> DrawM;
        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("Optimize", "SignTextCull", /* on to test (lead 11-11) */ true,
                "Writing on signs farther away than SignTextDrawM is not drawn (too small to read there); it appears again as you come closer. The sign board itself always shows. Off = every sign's writing is drawn at any distance, as before.");
            DrawM = config.Bind("Optimize", "SignTextDrawM", 30f,
                new ConfigDescription("Sign writing closer than this many metres is always drawn exactly as the game draws it.", new AcceptableValueRange<float>(5f, 200f)));
            Verify = config.Bind("Debugging", "SignTextCullVerify", false,
                "Every 30 s, checks that no sign within SignTextDrawM has its writing hidden; any found are named in a warning.");
            On.SettingChanged += (_, __) => { if (!On.Value) ShowAll("key turned off"); };
        }

        private const float HysteresisM = 2f, ReportSeconds = 30f;
        private const int Slices = 8;
        private static bool Wanted => On != null && On.Value && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        private sealed class Entry
        {
            public Sign Sign; public Canvas Canvas; public Transform Text;
            public bool HiddenByUs;
        }
        private static readonly List<Entry> s_entries = new List<Entry>();
        private static int s_cursor, c_hid, c_shown;
        private static float s_nextReport;
        private static readonly Vector3[] s_corners = new Vector3[4];

        [HarmonyPatch(typeof(Sign), "Awake")]
        private static class SignAwake
        {
            private static void Postfix(Sign __instance)
            {
                try { if (__instance != null && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) Register(__instance); }   // [visual] 1.2.295: registered always on a client (a list entry), so turning the key on mid-session covers signs already loaded; only the Tick is gated
                catch (System.Exception e) { EasyBakeLog.Debug("[SignText] register skipped: " + e.Message); }
            }
        }
        // Sign has no OnDestroy (vanilla 1.0.21): a destroyed sign reads null in the pass and is dropped there.

        private static void Register(Sign sign)
        {
            // the text's own canvas: inside the sign's hierarchy and world-space; a canvas that also holds other UI is not ours to switch
            Canvas canvas = sign.GetComponentInChildren<Canvas>(true);
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace || !canvas.transform.IsChildOf(sign.transform)) return;
            s_entries.Add(new Entry { Sign = sign, Canvas = canvas, Text = canvas.transform });
        }

        // EBM's LateUpdate (after GameCamera has moved the camera this frame)
        internal static void Tick()
        {
            if (s_entries.Count == 0) return;
            if (!Wanted) { ShowAll("key off"); return; }
            Camera cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 eye = cam.transform.position;
            float show = DrawM.Value, hide = show + HysteresisM;
            float show2 = show * show, hide2 = hide * hide;

            int n = s_entries.Count;
            int step = (n + Slices - 1) / Slices;
            if (s_cursor >= n) s_cursor = 0;
            int end = Mathf.Min(n, s_cursor + step);
            for (int i = s_cursor; i < end; i++)
            {
                Entry e = s_entries[i];
                if (e.Sign == null || e.Canvas == null)
                {
                    // swap-remove; the swapped-in entry is looked at at once (same index)
                    s_entries[i] = s_entries[n - 1]; s_entries.RemoveAt(n - 1); n--; end = Mathf.Min(end, n); i--;
                    continue;
                }
                float d2 = (e.Text.position - eye).sqrMagnitude;
                if (e.HiddenByUs)
                {
                    if (d2 <= show2) { e.HiddenByUs = false; e.Canvas.enabled = true; c_shown++; }
                }
                else if (d2 > hide2 && e.Canvas.enabled)   // a canvas someone else switched off stays theirs
                {
                    e.HiddenByUs = true; e.Canvas.enabled = false; c_hid++;
                }
            }
            s_cursor = end >= n ? 0 : end;
            Report(eye);
        }

        private static void ShowAll(string why)
        {
            int back = 0;
            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (!e.HiddenByUs) continue;
                e.HiddenByUs = false;
                if (e.Canvas != null) { e.Canvas.enabled = true; back++; }
            }
            if (back > 0) EasyBakeLog.Info($"[SignText] {back} sign text(s) shown again ({why})");
        }

        private static void Report(Vector3 eye)
        {
            float t = Time.realtimeSinceStartup;
            if (t < s_nextReport) return;
            s_nextReport = t + ReportSeconds;
            int hidden = 0;
            for (int i = 0; i < s_entries.Count; i++) if (s_entries[i].HiddenByUs) hidden++;
            if (EasyBakeLog.DebugOn()) EasyBakeLog.Debug($"[SignText] last 30 s: {s_entries.Count} sign(s) known, writing drawn on {s_entries.Count - hidden}, hidden on {hidden} (farther than {DrawM.Value:F0} m); {c_hid} hidden / {c_shown} shown again this window");
            c_hid = c_shown = 0;
            if (Verify != null && Verify.Value) VerifyNow(eye);
        }

        // independent re-measure: nearest canvas corner (not the centre the pass uses) against the plain draw distance (no hysteresis)
        private static void VerifyNow(Vector3 eye)
        {
            // 1.5 m under the draw distance: a sign inside the 2 m show/hide band may stay hidden by design, and the pass sees each sign every 8 frames
            float lim = Mathf.Max(0f, DrawM.Value - 1.5f), lim2 = lim * lim;
            int checkedN = 0, bad = 0; string first = null;
            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (e.Sign == null || e.Canvas == null || !e.HiddenByUs) continue;
                checkedN++;
                var rt = e.Canvas.transform as RectTransform;
                float near2 = (e.Text.position - eye).sqrMagnitude;
                if (rt != null)
                {
                    rt.GetWorldCorners(s_corners);
                    for (int c = 0; c < 4; c++) near2 = Mathf.Min(near2, (s_corners[c] - eye).sqrMagnitude);
                }
                if (near2 < lim2) { bad++; if (first == null) first = $"{Utils.GetPrefabName(e.Sign.gameObject)} at {e.Sign.transform.position:F0} ({Mathf.Sqrt(near2):F1} m)"; }
            }
            if (bad == 0) EasyBakeLog.Info($"[SignText] verify: {checkedN} hidden sign text(s), none within {lim:F1} m");
            else EasyBakeLog.Warn($"[SignText] verify: {bad} of {checkedN} hidden sign text(s) are within {lim:F1} m by their nearest corner, first: {first}");
        }
    }
}
