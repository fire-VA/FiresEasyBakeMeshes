using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using Unity.Profiling;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E39 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E39; DROP_NOTES_seasons_renderdiag.md). One complete render line per 30 s
    // window, to put side by side with other runs (VPO legs: the Unity totals part; EBM's own split is ours only). EBM's own code.
    // Switch: [Debugging] RenderCensus or the launch argument -ebmrendercensus. Both absent = ZERO cost: no hook registered, no recorder
    // started, the per-call notes in IndirectStats test one bool. Printed at Debug (the rigs run Debug logging).
    //
    // A frame, averaged over the window:
    //  - Unity's own render counters (ProfilerRecorder, Render category; published in release players): draw calls, set-pass calls, batches,
    //    triangles, vertices, shadow casters, visible skinned meshes
    //  - FrameTimingManager: GPU, CPU main, CPU render thread ms (n/a when the player does not publish frame timing)
    //  - EBM by path: indirect camera / sun-only shadows / cascades (and per cascade, recorded batches) / local lights / other cameras;
    //    grass (EBM GPU grass draws); legacy instanced; impostors; far building chunks; EBM combined zone meshes drawn (sampled every 2 s);
    //    indirect instances held; vanilla grass renderers still on the game's list
    //  - the rest = Unity total - what EBM submitted (skinned, particles, terrain, uncovered pieces, far chunks, sky, extra light passes)
    //  - per kind: top 10 indirect batches by instances (material, instances, tris)
    internal static class EbmRenderCensus
    {
        internal static ConfigEntry<bool> Enabled;
        private static bool s_arg, s_hooked;
        internal static bool On;   // read by IndirectStats' notes (one bool)

        // EBM split (accumulated while On)
        private static long a_cam, a_sun, a_casc, a_local, a_other, a_grass, a_legacy, a_imp, a_held, a_camTris;
        private static readonly long[] a_cascBatches = new long[4];
        private static int s_frames, s_lastFrame = -1;
        private static float s_windowStart = -1f, s_nextSample;
        // Unity counters
        private static ProfilerRecorder r_draws, r_setPass, r_batches, r_tris, r_verts, r_casters, r_skinned;
        private static long u_draws, u_setPass, u_batches, u_tris, u_verts, u_casters, u_skinned; private static int u_frames;
        private static readonly FrameTiming[] s_timing = new FrameTiming[1];
        private static double t_gpu, t_main, t_render; private static int t_frames;
        // combined zone meshes, sampled
        private static int c_drawn, c_total; private static long c_tris;
        private static readonly StringBuilder s_sb = new StringBuilder(1024);
        private static readonly List<EbmIndirect.Batch> s_top = new List<EbmIndirect.Batch>(16);

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Debugging", "RenderCensus", false,
                "Testing aid: one Debug line every 30 s with the frame's draw calls, set-pass calls, triangles and shadow casters (Unity's own counters), GPU / CPU frame times, and EBM's draws by path and by kind. Also on with the launch argument -ebmrendercensus. Off = no cost.");
            foreach (var a in Environment.GetCommandLineArgs()) if (string.Equals(a, "-ebmrendercensus", StringComparison.OrdinalIgnoreCase)) s_arg = true;
            Enabled.SettingChanged += (_, __) => Apply();
            Apply();
        }

        private static void Apply()
        {
            bool want = s_arg || (Enabled != null && Enabled.Value);
            if (want == On) return;
            On = want;
            if (On)
            {
                if (!s_hooked) { s_hooked = true; Camera.onPostRender += OnPostRender; }
                StartRecorders();
                s_windowStart = -1f;
                EasyBakeLog.Info($"[RenderCensus] on ({(s_arg ? "launch argument -ebmrendercensus" : "[Debugging] RenderCensus")}): one Debug line every 30 s");
            }
            else
            {
                if (s_hooked) { s_hooked = false; Camera.onPostRender -= OnPostRender; }
                StopRecorders();
            }
        }

        private static void StartRecorders()
        {
            r_draws = Start("Draw Calls Count"); r_setPass = Start("SetPass Calls Count"); r_batches = Start("Batches Count");
            r_tris = Start("Triangles Count"); r_verts = Start("Vertices Count"); r_casters = Start("Shadow Casters Count"); r_skinned = Start("Visible Skinned Meshes Count");
        }
        private static ProfilerRecorder Start(string name)
        {
            try { return ProfilerRecorder.StartNew(ProfilerCategory.Render, name); }
            catch (Exception e) { EasyBakeLog.Debug($"[RenderCensus] Unity counter '{name}' not available: {e.Message}"); return default; }
        }
        private static void StopRecorders()
        {
            foreach (var r in new[] { r_draws, r_setPass, r_batches, r_tris, r_verts, r_casters, r_skinned }) if (r.Valid) r.Dispose();
            r_draws = r_setPass = r_batches = r_tris = r_verts = r_casters = r_skinned = default;
        }
        private static long V(ProfilerRecorder r) => r.Valid ? r.LastValue : -1;
        private static string U(ProfilerRecorder r, long sum, double frames, double div, string unit) => r.Valid ? (sum / frames / div).ToString("F0") + unit : "n/a";

        // ── notes from IndirectStats (only while On) ──
        internal static void NoteSplit(int cam, int sunOnly, int cascades, int lights) { a_cam += cam; a_sun += sunOnly; a_casc += cascades; a_local += lights; }
        internal static void NoteOther(int n) => a_other += n;
        internal static void NoteGrass(int n) => a_grass += n;
        internal static void NoteLegacy(int n) => a_legacy += n;
        internal static void NoteImpostors(int n) => a_imp += n;
        internal static void NoteView(int view, long held, long tris) { if (view == 0) { a_held += held; a_camTris += tris; } }

        private static void OnPostRender(Camera cam)
        {
            if (!On || cam == null || cam != Utils.GetMainCamera() || Time.frameCount == s_lastFrame) return;
            s_lastFrame = Time.frameCount;
            float now = Time.realtimeSinceStartup;
            if (s_windowStart < 0f) { s_windowStart = now; Clear(); }
            s_frames++;
            long d = V(r_draws);
            if (d >= 0) { u_draws += d; u_setPass += Math.Max(0, V(r_setPass)); u_batches += Math.Max(0, V(r_batches)); u_tris += Math.Max(0, V(r_tris)); u_verts += Math.Max(0, V(r_verts)); u_casters += Math.Max(0, V(r_casters)); u_skinned += Math.Max(0, V(r_skinned)); u_frames++; }
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, s_timing) > 0 && s_timing[0].gpuFrameTime > 0)
                { t_gpu += s_timing[0].gpuFrameTime; t_main += s_timing[0].cpuMainThreadFrameTime; t_render += s_timing[0].cpuRenderThreadFrameTime; t_frames++; }
            }
            catch { }
            for (int k = 0; k < 4; k++) a_cascBatches[k] += EbmIndirect.CascadeRecorded(k);
            if (now >= s_nextSample) { s_nextSample = now + 2f; SampleCombined(); }
            if (now - s_windowStart < 30f) return;
            if (EasyBakeLog.DebugOn()) EasyBakeLog.Debug(Build(now - s_windowStart));
            s_windowStart = now; Clear();
        }

        // EBM combined zone meshes: drawn last frame (renderer visible to some camera or shadow pass), sampled
        private static void SampleCombined()
        {
            int drawn = 0, total = 0; long tris = 0;
            ZoneTracker.ForEachBakedZone((coord, bake, stale) =>
            {
                var list = bake.ShowingFarTier ? bake.FarBatches : bake.Batches;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    if (b.Combined == null || b.Mesh == null || !b.Combined.activeInHierarchy) continue;
                    total++;
                    var mr = b.Combined.GetComponent<MeshRenderer>();
                    if (mr == null || !mr.enabled || mr.forceRenderingOff || !mr.isVisible) continue;
                    drawn++;
                    for (int s = 0; s < b.Mesh.subMeshCount; s++) tris += b.Mesh.GetIndexCount(s) / 3;
                }
            });
            c_drawn = drawn; c_total = total; c_tris = tris;
        }

        private static string Build(float seconds)
        {
            double f = Math.Max(1, s_frames), uf = Math.Max(1, u_frames);
            var sb = s_sb; sb.Length = 0;
            sb.Append("[RenderCensus] last ").Append(seconds.ToString("F0")).Append(" s, ").Append(s_frames).Append(" frames; a frame: ");
            // review 11-09: a counter this player does not publish prints n/a, never 0
            if (u_frames > 0)
                sb.Append("Unity draw calls ").Append(U(r_draws, u_draws, uf, 1.0, "")).Append(", set-pass ").Append(U(r_setPass, u_setPass, uf, 1.0, ""))
                  .Append(", batches ").Append(U(r_batches, u_batches, uf, 1.0, "")).Append(", tris ").Append(U(r_tris, u_tris, uf, 1000.0, "k")).Append(", verts ")
                  .Append(U(r_verts, u_verts, uf, 1000.0, "k")).Append(", shadow casters ").Append(U(r_casters, u_casters, uf, 1.0, ""))
                  .Append(", skinned visible ").Append(U(r_skinned, u_skinned, uf, 1.0, ""));
            else sb.Append("Unity render counters not published by this player");
            sb.Append("; GPU ").Append(t_frames > 0 ? (t_gpu / t_frames).ToString("F2") + " ms" : "n/a")
              .Append(", CPU main ").Append(t_frames > 0 ? (t_main / t_frames).ToString("F2") + " ms" : "n/a")
              .Append(", render thread ").Append(t_frames > 0 ? (t_render / t_frames).ToString("F2") + " ms" : "n/a");
            long ebm = a_cam + a_sun + a_casc + a_local + a_other + a_grass + a_legacy + a_imp;
            sb.Append(". EBM draws: indirect camera ").Append((a_cam / f).ToString("F0")).Append(", sun-only shadows ").Append((a_sun / f).ToString("F0"))
              .Append(", cascades ").Append((a_casc / f).ToString("F0")).Append(" (recorded batches per cascade ");
            for (int k = 0; k < 4; k++) sb.Append(k > 0 ? "/" : "").Append((a_cascBatches[k] / f).ToString("F0"));
            sb.Append("), local lights ").Append((a_local / f).ToString("F0")).Append(", other cameras ").Append((a_other / f).ToString("F0"))
              .Append(", grass ").Append((a_grass / f).ToString("F0")).Append(", legacy instanced ").Append((a_legacy / f).ToString("F0"))
              .Append(", impostors ").Append((a_imp / f).ToString("F0")).Append("; EBM submitted ").Append((ebm / f).ToString("F0"));
            if (u_frames > 0) sb.Append("; the rest (vanilla renderers, skinned, particles, terrain, far chunks, sky) ").Append(Math.Max(0.0, u_draws / uf - ebm / f).ToString("F0"));
            sb.Append(". Indirect instances held ").Append((a_held / f).ToString("F0")).Append(", camera tris ").Append((a_camTris / f / 1000.0).ToString("F0")).Append("k")
              .Append("; combined zone meshes drawn ").Append(c_drawn).Append(" of ").Append(c_total).Append(" (").Append((c_tris / 1000.0).ToString("F0")).Append("k tris, last sample)")
              .Append("; far building chunks ").Append(Far.FarBuildings.S.Draws)
              .Append("; vanilla grass renderers on the game's list ").Append(InstanceRenderer.Instances.Count);
            // per kind: the 10 indirect batches holding the most instances
            s_top.Clear();
            EbmIndirect.ForEachBatch(b => { if (b.Count > 0) s_top.Add(b); });
            s_top.Sort((x, y) => y.Count.CompareTo(x.Count));
            sb.Append(". Top kinds:");
            for (int i = 0; i < s_top.Count && i < 10; i++)
            {
                var b = s_top[i];
                sb.Append(' ').Append(b.Material != null ? b.Material.name : "?").Append(' ').Append(b.Count).Append(" inst ");
                long t = b.Mesh != null && b.SubMesh < b.Mesh.subMeshCount ? b.Mesh.GetIndexCount(b.SubMesh) / 3 * (long)b.Count : 0;
                sb.Append((t / 1000.0).ToString("F0")).Append("k tris").Append(b.Shadows != UnityEngine.Rendering.ShadowCastingMode.Off ? " casts" : "").Append(';');
            }
            if (s_top.Count == 0) sb.Append(" none (indirect path off or empty)");
            s_top.Clear();
            return sb.ToString();
        }

        private static void Clear()
        {
            a_cam = a_sun = a_casc = a_local = a_other = a_grass = a_legacy = a_imp = a_held = a_camTris = 0;
            Array.Clear(a_cascBatches, 0, 4);
            s_frames = 0;
            u_draws = u_setPass = u_batches = u_tris = u_verts = u_casters = u_skinned = 0; u_frames = 0;
            t_gpu = t_main = t_render = 0; t_frames = 0;
        }
    }
}
