using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    /// <summary>
    /// [wishbone] 11-08 for [visual]'s EBM indirect path (our own code, our names; VPO's diagnostics read for ideas only, nothing copied).
    /// The proof line for "[Optimize] IndirectRenderer": every 30 s one Debug "[Indirect]" line with what each draw source did, averaged
    /// per frame over the window:
    ///   - indirect batches drawn, instances and tris submitted, per view (camera, shadow cascades 1-4, other views);
    ///   - instances the indirect path held vs submitted (what the cull removed);
    ///   - EBM's legacy RenderMeshInstanced path (tris + calls, the numbers InstancedDraw already counts);
    ///   - vanilla MeshRenderers still drawing, counted once per window in slices (Core's FrameBudgetScheduler when present).
    /// Plus one Info line whenever the path's state changes, saying WHY (key on/off, bundle missing, compute unsupported, a variant missing).
    /// Cost: a few array adds a call; no allocation per frame (one reused StringBuilder; the renderer array once per window).
    /// Main thread only (all callers are render callbacks / Update).
    /// </summary>
    internal static class IndirectStats
    {
        // view slots: 0 camera, 1-4 shadow cascades, 5 everything else (reflection, sky, extra cameras)
        internal const int ViewCamera = 0, ViewOther = 5;
        private const int Views = 6;
        private static readonly string[] ViewNames = { "camera", "cascade 1", "cascade 2", "cascade 3", "cascade 4", "other" };
        private const float WindowSeconds = 30f;
        private const int SliceSize = 2000;          // renderers per scan slice
        private const double SliceBudgetMs = 0.25;   // own-slicing fallback: at most this per frame

        private static readonly long[] s_batches = new long[Views], s_instances = new long[Views], s_tris = new long[Views];
        private static readonly int[] s_viewFrames = new int[Views];   // frames a view drew at least once (cascade count changes with settings)
        private static readonly bool[] s_viewThisFrame = new bool[Views];
        private static long s_held, s_legacyTris, s_legacyCalls;
        private static int s_frames;
        private static float s_windowStart = -1f;
        private static readonly StringBuilder s_sb = new StringBuilder(512);

        private static string s_state;   // the last state line, so a repeat says nothing

        // ---- the API [visual]'s renderer calls ------------------------------------------------------------------------------------

        /// <summary>One view's indirect draws this frame. view: 0 camera, 1-4 cascade, anything else = other. Call once per view
        /// per frame (repeat calls for the same view add up).</summary>
        internal static void NoteView(int view, int batchesDrawn, long instances, long tris)
        {
            int v = view >= 0 && view < ViewOther ? view : ViewOther;
            s_batches[v] += batchesDrawn; s_instances[v] += instances; s_tris[v] += tris;
            s_viewThisFrame[v] = true;
            if (EbmRenderCensus.On) EbmRenderCensus.NoteView(view, instances, tris);   // [seasons] 11-09 E39
        }

        /// <summary>Instances the indirect path holds this frame (before culling); paired with the camera's submitted count.</summary>
        internal static void NoteHeld(long held) => s_held += held;

        /// <summary>EBM's legacy instanced path this frame (InstancedDraw's totals).</summary>
        internal static void NoteLegacy(long tris, int calls) { s_legacyTris += tris; s_legacyCalls += calls; }

        // ---- 1.2.254: where the draw calls come from. Debug only: SplitOn follows EbmIndirect.PhaseBegin; with Debug off nothing below is called ----
        internal static bool SplitOn;
        private static long s_splitCam, s_splitSun, s_splitCasc, s_splitLocal, s_splitOther, s_splitGrass, s_splitLegacy, s_splitImp;
        internal static void NoteDrawSplit(int cam, int sunOnly, int cascades, int lights) { if (EbmRenderCensus.On) EbmRenderCensus.NoteSplit(cam, sunOnly, cascades, lights); s_splitCam += cam; s_splitSun += sunOnly; s_splitCasc += cascades; s_splitLocal += lights; }
        internal static void NoteSplitOther(int n) { s_splitOther += n; if (EbmRenderCensus.On) EbmRenderCensus.NoteOther(n); }
        internal static void NoteSplitGrass(int n) { s_splitGrass += n; if (EbmRenderCensus.On) EbmRenderCensus.NoteGrass(n); }
        internal static void NoteSplitLegacy(int n) { s_splitLegacy += n; if (EbmRenderCensus.On) EbmRenderCensus.NoteLegacy(n); }
        internal static void NoteSplitImpostors(int n) { s_splitImp += n; if (EbmRenderCensus.On) EbmRenderCensus.NoteImpostors(n); }

        // Unity's own total for the frame, where this build of the player publishes it; the part EBM did not submit is what names the scene's renderers
        private static Unity.Profiling.ProfilerRecorder s_totalDraws;
        private static bool s_totalStarted; private static long s_totalSum; private static int s_totalFrames;
        private static void SplitFrame()
        {
            if (!s_totalStarted)
            {
                s_totalStarted = true;
                try { s_totalDraws = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count"); }
                catch (Exception e) { EasyBakeLog.Debug("[Indirect] Unity's draw call counter could not start: " + e.Message); }
            }
            if (s_totalDraws.Valid) { s_totalSum += s_totalDraws.LastValue; s_totalFrames++; }
        }

        private static void AppendSplit(StringBuilder sb, double f)
        {
            long ebm = s_splitCam + s_splitSun + s_splitCasc + s_splitLocal + s_splitOther + s_splitGrass + s_splitLegacy + s_splitImp;
            sb.Append(". Draw calls a frame by source: indirect camera ").Append((s_splitCam / f).ToString("F0"))
              .Append(", indirect sun shadows ").Append((s_splitSun / f).ToString("F0"))
              .Append(", indirect cascades ").Append((s_splitCasc / f).ToString("F0"))
              .Append(", indirect lights ").Append((s_splitLocal / f).ToString("F0"))
              .Append(", other cameras ").Append((s_splitOther / f).ToString("F0"))
              .Append(", grass ").Append((s_splitGrass / f).ToString("F0"))
              .Append(", legacy instanced ").Append((s_splitLegacy / f).ToString("F0"))
              .Append(", impostors ").Append((s_splitImp / f).ToString("F0"))
              .Append("; EBM submitted ").Append((ebm / f).ToString("F0"))
              .Append("; far buildings up to ").Append(Far.FarBuildings.S.Draws).Append(" chunk(s), each a Unity renderer, so inside the rest");
            bool haveTotal = s_totalFrames > 0 && s_totalSum > 0;
            if (haveTotal)
            {
                double total = s_totalSum / (double)s_totalFrames;
                sb.Append("; Unity total ").Append(total.ToString("F0")).Append(", the rest (skinned, particles, terrain, uncovered pieces, far chunks, sky, extra forward light passes) ").Append(Math.Max(0.0, total - ebm / f).ToString("F0"));
            }
            else sb.Append("; Unity's total not published by this build, so the rest is not known");
            s_splitCam = s_splitSun = s_splitCasc = s_splitLocal = s_splitOther = s_splitGrass = s_splitLegacy = s_splitImp = 0;
            s_totalSum = 0; s_totalFrames = 0;
        }

        /// <summary>The path's state, with its reason ("on", "off: [Optimize] IndirectRenderer = false", "off: compute shaders not
        /// supported on this GPU", "fallback: no procedural variant for material X"...). Logged at Info once per change.</summary>
        internal static void NoteState(string why)
        {
            if (string.IsNullOrEmpty(why) || why == s_state) return;
            s_state = why;
            EasyBakeLog.Info("[Indirect] state: " + why);
        }

        /// <summary>Once per frame, after the last view drew (e.g. Camera.onPostRender of the main camera, or LateUpdate).</summary>
        internal static void FrameEnd()
        {
            for (int v = 0; v < Views; v++) if (s_viewThisFrame[v]) { s_viewFrames[v]++; s_viewThisFrame[v] = false; }
            s_frames++;
            if (SplitOn) SplitFrame();   // 1.2.254
            float now = Time.realtimeSinceStartup;
            if (s_windowStart < 0f) { s_windowStart = now; HookScenes(); StartScan(); }
            if (s_scanArray != null && !s_schedulerUsed) ScanStep(SliceBudgetMs);
            else if (s_scanArray != null && Time.frameCount - s_lastSliceFrame > StuckFrames)
            {
                // the scheduler took the chain and then stopped running it (its host went away): finish with our own slices
                EasyBakeLog.Debug($"[Indirect] Core scheduler ran no scan slice for {StuckFrames} frames: own slices from now");
                s_chainToken++; s_schedulerUsed = false; s_scheduleBroken = true; s_scanBy = "own slices after a stuck scheduler chain";
            }
            if (now - s_windowStart < WindowSeconds) return;
            if (EasyBakeLog.DebugOn()) EasyBakeLog.Debug(Build(now - s_windowStart));
            Reset(now);
        }

        // ---- the window line ------------------------------------------------------------------------------------------------------

        private static string Build(float seconds)
        {
            double f = Math.Max(1, s_frames);
            var sb = s_sb; sb.Length = 0;
            sb.Append("[Indirect] last ").Append(seconds.ToString("F0")).Append(" s, ").Append(s_frames).Append(" frames, a frame: ");
            long allTris = 0;
            bool any = false;
            for (int v = 0; v < Views; v++)
            {
                allTris += s_tris[v];
                if (s_viewFrames[v] == 0) continue;
                if (any) sb.Append("; ");
                any = true;
                sb.Append(ViewNames[v]).Append(' ').Append((s_batches[v] / f).ToString("F0")).Append(" batches, ")
                  .Append((s_instances[v] / f).ToString("F0")).Append(" inst, ").Append((s_tris[v] / f / 1000.0).ToString("F0")).Append("k tris");
                if (s_viewFrames[v] < s_frames) sb.Append(" (in ").Append(s_viewFrames[v]).Append(" frames)");
            }
            if (!any) sb.Append("no indirect draws");
            if (s_held > 0)
                sb.Append(". Held ").Append((s_held / f).ToString("F0")).Append(" inst, camera submitted ")
                  .Append((s_instances[ViewCamera] / f).ToString("F0")).Append(" (").Append((100.0 * s_instances[ViewCamera] / s_held).ToString("F0")).Append("%)");
            sb.Append(". By source: indirect ").Append((allTris / f / 1000.0).ToString("F0")).Append("k tris in ");
            long allBatches = 0; for (int v = 0; v < Views; v++) allBatches += s_batches[v];
            sb.Append((allBatches / f).ToString("F0")).Append(" draws; EBM legacy ").Append((s_legacyTris / f / 1000.0).ToString("F0")).Append("k tris in ")
              .Append((s_legacyCalls / f).ToString("F0")).Append(" calls; vanilla MeshRenderers ");
            if (s_scanDone) sb.Append(s_scanVisible).Append(" visible of ").Append(s_scanTotal).Append(" enabled: ")
                              .Append(s_lastForced).Append(" hidden by forceRenderingOff (coverage), ").Append(s_lastLodHeld).Append(" under an enabled LODGroup not drawn (other level / off-screen), ")
                              .Append(s_lastOffscreen).Append(" off-screen (once this window, ").Append(s_scanBy).Append(')');
            else if (!s_scanStarted) sb.Append("not counted (the scan runs only while Debug logging is on at the window start)");
            else sb.Append("scan not finished this window (").Append(s_scanIndex).Append('/').Append(s_scanArray != null ? s_scanArray.Length : 0).Append(')');
            AppendSplit(sb, f);   // 1.2.254
            sb.Append(". State: ").Append(s_state ?? "not reported");
            return sb.ToString();
        }

        private static void Reset(float now)
        {
            Array.Clear(s_batches, 0, Views); Array.Clear(s_instances, 0, Views); Array.Clear(s_tris, 0, Views); Array.Clear(s_viewFrames, 0, Views);
            s_held = s_legacyTris = s_legacyCalls = 0; s_frames = 0; s_windowStart = now;
            if (!SplitOn)   // 1.2.254: Debug went off: drop the counters and stop Unity's recorder
            {
                s_splitCam = s_splitSun = s_splitCasc = s_splitLocal = s_splitOther = s_splitGrass = s_splitLegacy = s_splitImp = 0; s_totalSum = 0; s_totalFrames = 0;
                if (s_totalStarted) { s_totalStarted = false; if (s_totalDraws.Valid) s_totalDraws.Dispose(); }
            }
            StartScan();
        }

        // ---- vanilla renderers left: one visible-only count per window, sliced ------------------------------------------------------
        // [generator]'s review 11-08: (1) the scene scan is proof only, so it runs ONLY while Debug logging is on (the rigs); players
        // (Debug off) never pay the FindObjectsByType. No scene-wide renderer registry exists in Core or EBM to count from instead
        // (checked 11-08), so the array is taken once per 30 s window on the rigs. (2) A scene change drops the chain: scene unload
        // stops it, the next window re-arms it; a chain the scheduler stops running (StuckFrames with no slice) falls back to own slices.

        private const int StuckFrames = 300;
        private static MeshRenderer[] s_scanArray;
        private static int s_scanIndex, s_scanVisible, s_scanTotal, s_scanCounting, s_scanEnabled, s_lastSliceFrame;
        private static int s_scanForced, s_scanLodHeld, s_scanOffscreen, s_lastForced, s_lastLodHeld, s_lastOffscreen;   // 1.2.211
        private static bool s_scanDone, s_scanStarted, s_schedulerUsed, s_scheduleBroken, s_scenesHooked;
        private static string s_scanBy = "own slices";
        private static readonly Stopwatch s_watch = new Stopwatch();

        private static void StartScan()
        {
            s_chainToken++;   // any chain still queued from the last window ends at its next slice
            s_scanArray = null; s_scanIndex = 0; s_scanCounting = 0; s_scanEnabled = 0; s_scanDone = false; s_schedulerUsed = false;
            s_scanForced = s_scanLodHeld = s_scanOffscreen = 0;
            // 1.2.218 ([visual], O at 615: the once-per-30 s 77 ms frame was this FindObjectsByType over 80k+ renderers): opt-in only
            s_scanStarted = EasyBakeLog.DebugOn() && FiresEasyBakeMeshesPlugin.RendererCensus != null && FiresEasyBakeMeshesPlugin.RendererCensus.Value;
            if (!s_scanStarted) return;
            s_scanArray = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
            s_lastSliceFrame = Time.frameCount;
            s_schedulerUsed = ScheduleChain();
            s_scanBy = s_schedulerUsed ? "Core scheduler" : "own slices";
        }

        private static void HookScenes()
        {
            if (s_scenesHooked) return;
            s_scenesHooked = true;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += _ =>
            {
                // the renderers in the array are going away and the scheduler may drop queued work: stop; the next window starts afresh
                s_chainToken++; s_scanArray = null; s_scanDone = false; s_scanStarted = false; s_schedulerUsed = false;
                s_windowStart = -1f;   // FrameEnd re-arms (new window + scan) on the first frame after the load
            };
        }

        // one slice: renderers enabled + visible to any camera or shadow pass last frame (Renderer.isVisible)
        private static bool Slice()
        {
            var a = s_scanArray;
            if (a == null) return true;
            s_lastSliceFrame = Time.frameCount;
            int end = Math.Min(a.Length, s_scanIndex + SliceSize);
            for (int i = s_scanIndex; i < end; i++)
            {
                var r = a[i];
                if (r == null || !r.enabled) continue;
                s_scanEnabled++;
                // 1.2.211 ([wishbone], the 79k study): where the enabled-but-not-drawn renderers are. forceRenderingOff = hidden by coverage (or
                // anyone): still in Unity's renderer scene. Not visible under an enabled LODGroup = LOD-held (another level shows) or off-screen;
                // not visible without one = off every camera / shadow pass.
                if (r.forceRenderingOff) { s_scanForced++; continue; }
                if (r.isVisible) { s_scanCounting++; continue; }
                var lg = r.GetComponentInParent<LODGroup>();
                if (lg != null && lg.enabled) s_scanLodHeld++; else s_scanOffscreen++;
            }
            s_scanIndex = end;
            if (end < a.Length) return false;
            s_scanVisible = s_scanCounting; s_scanTotal = s_scanEnabled; s_scanDone = true;
            s_lastForced = s_scanForced; s_lastLodHeld = s_scanLodHeld; s_lastOffscreen = s_scanOffscreen;
            s_scanArray = null;   // let the array go until the next window
            return true;
        }

        private static void ScanStep(double budgetMs)
        {
            if (s_scanDone || s_scanArray == null) return;
            s_watch.Restart();
            while (!Slice() && s_watch.Elapsed.TotalMilliseconds < budgetMs) { }
        }

        // Core's FrameBudgetScheduler.Schedule(WorkPriority.Low, Action) by reflection (EBM runs without Core too); false = slice ourselves.
        private static MethodInfo s_schedule;
        private static object s_low;
        private static bool s_scheduleLooked;
        private static int s_chainToken;
        private static readonly object[] s_args = new object[2];

        /// <summary>One chain per window: a closure holding its token (one small allocation per 30 s, Debug on only). A chain whose
        /// token is no longer current (new window, scene unload, stuck fallback) stops at its next slice, so two never run.</summary>
        private static bool ScheduleChain()
        {
            if (!s_scheduleLooked)
            {
                s_scheduleLooked = true;
                try
                {
                    Type sched = null, prio = null;   // 1.2.208: EBM never looks Core up; the scan always uses its own slices
                    if (sched != null && prio != null)
                    {
                        s_schedule = sched.GetMethod("Schedule", BindingFlags.Public | BindingFlags.Static, null, new[] { prio, typeof(Action) }, null);
                        s_low = Enum.Parse(prio, "Low");
                    }
                }
                catch (Exception e) { s_schedule = null; EasyBakeLog.Debug("[Indirect] Core scheduler not usable, own slices: " + e.Message); }
                EasyBakeLog.Debug("[Indirect] vanilla renderer scan: " + (s_schedule != null ? "through Core's FrameBudgetScheduler (Low)" : "own slices, " + SliceBudgetMs + " ms a frame (no Core scheduler)"));
            }
            if (s_schedule == null || s_scheduleBroken || s_scanArray == null) return false;
            int token = s_chainToken;
            Action step = null;
            step = () =>
            {
                if (token != s_chainToken) return;                 // a newer window / scene change / fallback owns the scan now
                if (Slice()) return;
                if (!Queue(step)) { s_schedulerUsed = false; }      // the scheduler went away: FrameEnd finishes with own slices
            };
            if (Queue(step)) return true;
            EasyBakeLog.Debug("[Indirect] Core scheduler call failed, own slices from now");
            return false;
        }

        private static bool Queue(Action step)
        {
            if (s_schedule == null) return false;
            try { s_args[0] = s_low; s_args[1] = step; s_schedule.Invoke(null, s_args); return true; }
            catch { s_schedule = null; return false; }
        }
    }
}
