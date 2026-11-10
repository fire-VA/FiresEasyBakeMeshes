using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// Instanced groups are immediate-mode draws, so every loaded zone hands Unity its whole matrix list again every
    /// frame, including the zones behind the player. A group whose box is out of view is dropped here instead, after
    /// the box is stretched along the sun so anything whose shadow reaches the screen still draws. The running cost of
    /// what is left is reported so the drawing can be weighed against the frame it sits in.
    /// </summary>
    internal static class InstancedDraw
    {
        private const float ReportSeconds = 30f;
        // The four side planes are pushed out by this much so a group sliding into view is never a frame late.
        private const float EdgeMarginMeters = 5f;
        private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
        private static readonly Plane[] s_planes = new Plane[6];

        // What ZoneTracker.Update spends its frame on, so the tracking figure can be read rather than guessed at.
        internal enum Phase
        {
            Zones,
            StandIns,
            Unloading,
            Checks,
            Baking,
            BuildHold,
            Pumps,   // 1.2.206 (lead 11-08: zoneTrack 14.4 ms/s standing still): the suppression / classify pumps + snow poll + orphan census, never timed before
        }

        private static readonly string[] PhaseNames = { "walking zones", "stand-ins", "unloading", "checks", "baking", "holding for build tools", "pumps" };
        private static readonly double[] s_phaseMs = new double[PhaseNames.Length];
        internal static int PhaseCount => PhaseNames.Length;   // 1.2.224: EbmSelfCost snapshots the phases around one ZoneTracker.Update
        internal static string PhaseName(int i) => PhaseNames[i];
        internal static double PhaseMsRaw(int i) => s_phaseMs[i];
        // One Profiler sample per phase, so a profile attributes every allocation in ZoneTracker.Update to its phase.
        private static ProfilerMarker[] s_phaseMarkers;

        private static bool s_culling;
        private static Vector3 s_shadowSweep;
        private static long s_drawStart;
        private static long s_trackTicks;

        private static int s_frames;
        private static long s_zoneVisits;
        private static long s_groups, s_groupsDrawn, s_instances, s_instancesDrawn;
        private static double s_drawMs, s_worstDrawMs, s_trackMs, s_frameMs;
        private static float s_nextReport;

        internal static void Reset()
        {
            s_frames = 0;
            s_zoneVisits = s_groups = s_groupsDrawn = s_instances = s_instancesDrawn = 0L;
            s_shadowParts = s_shadowCut = 0L;   // 1.2.149
            s_drawMs = s_worstDrawMs = s_trackMs = s_frameMs = 0.0;
            s_nextReport = 0f;
            s_trackTicks = 0L;
            for (int i = 0; i < s_phaseMs.Length; i++) s_phaseMs[i] = 0.0;
        }

        internal static void NoteZoneTracking(long ticks) => s_trackTicks = ticks;

        internal static long Mark(Phase phase)
        {
            if (s_phaseMarkers == null)
            {
                s_phaseMarkers = new ProfilerMarker[PhaseNames.Length];
                for (int i = 0; i < PhaseNames.Length; i++) s_phaseMarkers[i] = new ProfilerMarker("EBM " + PhaseNames[i]);
            }
            s_phaseMarkers[(int)phase].Begin();
            return Stopwatch.GetTimestamp();
        }

        internal static void NotePhase(Phase phase, long start)
        {
            s_phaseMs[(int)phase] += (Stopwatch.GetTimestamp() - start) * MsPerTick;
            s_phaseMarkers[(int)phase].End();
        }

        // 1.2.114 (Fire 10-20: the large map's Live map "All levels" missed build pieces): the cull above uses the MAIN camera, but a
        // group culled for it was drawn for no camera at all, while its pieces' own renderers stay silenced. Any other camera that
        // renders this frame (RPGmaker's live map calls cam.Render() on its own top-down camera; a water reflection) gets those
        // groups drawn for it alone, from its own pre-cull, if they are in ITS view. The main view's cull is unchanged.
        private static readonly List<(ZoneInstanceGroup group, bool far)> s_culled = new List<(ZoneInstanceGroup, bool)>();
        private static readonly Plane[] s_otherPlanes = new Plane[6];
        private static bool s_hooked;
        private static Camera s_main;

        internal static void NoteCulled(ZoneInstanceGroup group, bool far) => s_culled.Add((group, far));

        // 1.2.137 ([visual] for [ghost]'s live map ROOFS toggle, Fire 10-27): cameras that must not see baked ROOF pieces. While any
        // is registered, roof groups are not drawn for every camera at once; each camera gets them in its own pre-cull, unless it is
        // on this list. Nothing changes while the list is empty.
        private static readonly HashSet<Camera> s_noRoofCameras = new HashSet<Camera>();
        private static readonly List<(ZoneInstanceGroup group, bool far)> s_roofs = new List<(ZoneInstanceGroup, bool)>();
        internal static bool RoofHidingActive { get { s_noRoofCameras.RemoveWhere(c => c == null); return s_noRoofCameras.Count > 0; } }
        internal static void NoteRoof(ZoneInstanceGroup group, bool far) => s_roofs.Add((group, far));

        internal static void HideRoofsFor(Camera cam, bool hide)
        {
            if (cam == null) return;
            bool changed = hide ? s_noRoofCameras.Add(cam) : s_noRoofCameras.Remove(cam);
            if (changed) EasyBakeLog.Debug($"[Draw] baked roofs {(hide ? "hidden from" : "shown again to")} camera '{cam.name}' ({s_noRoofCameras.Count} camera(s) without roofs)");
        }

        private static void DrawRoofsFor(Camera cam)
        {
            if (s_roofs.Count == 0 || cam == null || s_noRoofCameras.Contains(cam)) return;
            if (cam != s_main && cam.cameraType != CameraType.Game && cam.cameraType != CameraType.Reflection) return;
            try { foreach (var (group, far) in s_roofs) if (group != null && group.Count > 0) group.Draw(far, cam); }
            catch (System.Exception ex) { EasyBakeLog.Debug($"[Draw] drawing roof groups for camera '{cam.name}' threw: {ex.Message}"); }
        }

        private static void DrawCulledFor(Camera cam)
        {
            long c0 = Split ? Stopwatch.GetTimestamp() : 0L;
            int drawn = 0;
            DrawRoofsFor(cam);   // 1.2.137
            if (s_culled.Count == 0 || cam == null || cam == s_main) { NoteCamera(cam, c0, drawn); return; }
            if (cam.cameraType != CameraType.Game && cam.cameraType != CameraType.Reflection) { NoteCamera(cam, c0, drawn); return; }
            // 1.2.186 toggle [Optimize] InstancedOtherCameraRedraw (default true = as before): false stops re-drawing main-view-culled groups
            // for other game / reflection cameras (the 1.2.114 live-map fix); roof groups for cameras are untouched
            if (!FiresEasyBakeMeshesPlugin.InstancedOtherCameraRedraw.Value) { NoteCamera(cam, c0, drawn); return; }
            try
            {
                GeometryUtility.CalculateFrustumPlanes(cam, s_otherPlanes);
                foreach (var (group, far) in s_culled)
                {
                    if (group == null || group.Count == 0) continue;
                    if (GeometryUtility.TestPlanesAABB(s_otherPlanes, group.WorldBounds)) { group.Draw(far, cam); drawn++; }
                }
            }
            catch (System.Exception ex) { EasyBakeLog.Debug($"[Draw] drawing culled groups for camera '{cam.name}' threw: {ex.Message}"); }
            NoteCamera(cam, c0, drawn);
        }

        // ── 1.2.186 (lead 11-08, EBM's draw ~30-40 ms/s in Fire's sessions): Debug-only split of the draw ─────────────────────
        // Split is read once a frame (BeginFrame) from Debug logging; with Debug off nothing below runs. Main-view group draws report
        // prep (snow / part matrices / follow checks / shadow test) against submit (RenderMeshInstanced), calls and instances a frame;
        // per-camera pre-cull draws (roofs, other-camera redraws) are timed by camera name, which the [Draw] figure never counted.
        internal static bool Split;
        private static long s_prepTicks, s_submitTicks, s_calls, s_snowCalls, s_partInstances;
        private static long s_camGroupTicks, s_camGroupSubmit, s_camGroupCalls;
        private static long s_trisNear, s_trisFar, s_trisShadow;   // 1.2.194

        // 1.2.196: instances drawn per LOD level (index 0..3, 4 = level 4+), culled by LOD, and the tris of the shadow-proxy draws
        private static readonly long[] s_lodInstances = new long[5];
        private static long s_lodCulled, s_proxyTris;
        internal static void NoteLod(int level, int n)
        {
            if (level < 0) { s_lodCulled += n; return; }
            s_lodInstances[Mathf.Min(level, 4)] += n;
        }
        private static long s_proxyCalls;   // 1.2.199
        // 1.2.200 InstanceCull proof: part instances held vs submitted by culled draws, compacted rebuilds
        private static long s_cullHeld, s_cullSubmitted, s_cullRebuilds;
        internal static void NoteCull(int held, int submitted) { s_cullHeld += held; s_cullSubmitted += submitted; }
        internal static void NoteCullRebuild() => s_cullRebuilds++;
        private static string CullText(double frames)
        {
            if (!InstanceCull.Enabled) return "; [Optimize] InstancedInstanceCull off";
            return $"; [Optimize] InstancedInstanceCull on: culled groups submit {s_cullSubmitted / frames:F0} of {s_cullHeld / frames:F0} part instances a frame, {s_cullRebuilds} compacted rebuild(s) this window, worker {InstanceCull.WorkerTicks * MsPerTick / frames:F2} ms a frame over {InstanceCull.Jobs} job(s)";
        }
        internal static void NoteShadowProxyTris(long tris) { s_proxyTris += tris; s_proxyCalls++; }

        private static string LodText(double frames)
        {
            var lod = FiresEasyBakeMeshesPlugin.InstancedCellLod; var px = FiresEasyBakeMeshesPlugin.InstancedShadowProxies;
            bool lodOn = lod != null && lod.Value, pxOn = px != null && px.Value;
            if (!lodOn && !pxOn) return "; [Optimize] InstancedCellLod off, InstancedShadowProxies off";
            return $"; [Optimize] InstancedCellLod {(lodOn ? "on" : "off")}: instances a frame by LOD 0/1/2/3/4+ " +
                   $"{s_lodInstances[0] / frames:F0}/{s_lodInstances[1] / frames:F0}/{s_lodInstances[2] / frames:F0}/{s_lodInstances[3] / frames:F0}/{s_lodInstances[4] / frames:F0}, " +
                   $"{s_lodCulled / frames:F0} past vanilla's LOD cull; InstancedShadowProxies {(pxOn ? "on" : "off")}: proxy shadow tris {s_proxyTris / frames / 1000.0:F0}k in {s_proxyCalls / frames:F0} shadow-only call(s) a frame (only parts with a lower LOD; the full-mesh shadow tris they replace drop out of shadow-casting above)" +
                   "";
        }

        internal static void NoteTris(long tris, bool far, bool castsShadow)
        {
            if (far) s_trisFar += tris; else s_trisNear += tris;
            if (castsShadow) s_trisShadow += tris;
        }
        private static readonly Dictionary<string, (long ticks, int frames, long groups)> s_cams = new Dictionary<string, (long, int, long)>();

        internal static void NoteGroupDraw(long totalTicks, long submitTicks, int calls, int snowCalls, int partInstances, bool forCamera)
        {
            if (Indirect.IndirectStats.SplitOn) Indirect.IndirectStats.NoteSplitLegacy(calls);   // 1.2.254
            if (forCamera) { s_camGroupTicks += totalTicks; s_camGroupSubmit += submitTicks; s_camGroupCalls += calls; return; }
            s_prepTicks += totalTicks - submitTicks;
            s_submitTicks += submitTicks;
            s_calls += calls;
            s_snowCalls += snowCalls;
            s_partInstances += partInstances;
        }

        private static void NoteCamera(Camera cam, long c0, int drawn)
        {
            if (c0 == 0L || cam == null) return;
            if (drawn == 0 && s_roofs.Count == 0) return;   // nothing drawn for it: not worth a row
            string name = cam.name;
            s_cams.TryGetValue(name, out var row);
            s_cams[name] = (row.ticks + (Stopwatch.GetTimestamp() - c0), row.frames + 1, row.groups + drawn);
        }

        private static string SplitText(double frames)
        {
            if (s_calls == 0 && s_cams.Count == 0) return "";
            var sb = new System.Text.StringBuilder(" Split: main view prep ");
            sb.Append((s_prepTicks * MsPerTick / frames).ToString("F2")).Append(" ms + submit ").Append((s_submitTicks * MsPerTick / frames).ToString("F2"))
              .Append(" ms a frame, ").Append((s_calls / frames).ToString("F0")).Append(" RenderMeshInstanced call(s) (")
              .Append((s_snowCalls / frames).ToString("F0")).Append(" per snow range), ").Append((s_partInstances / frames).ToString("F0")).Append(" part instances")
              .Append($" ({(s_calls > 0 ? s_partInstances / (double)s_calls : 0):F0} per call; [Optimize] InstancedCellSizeMeters {ZoneInstanceGroup.CellSize:0.#}{(ZoneInstanceGroup.CellSize > 0f ? " m: one draw per cell run, own bounds" : " = one bounds per group")})")
              .Append($"; tris submitted a frame (main view, before GPU culling): near mesh {s_trisNear / frames / 1000.0:F0}k + far mesh {s_trisFar / frames / 1000.0:F0}k, of them shadow-casting {s_trisShadow / frames / 1000.0:F0}k (drawn again per shadow cascade)")
              .Append(LodText(frames)).Append(CullText(frames));
            if (s_cams.Count > 0)
            {
                sb.Append("; pre-cull per camera:");
                foreach (var kv in s_cams)
                    sb.Append(' ').Append(kv.Key).Append(' ').Append((kv.Value.ticks * MsPerTick / frames).ToString("F2")).Append(" ms a frame (")
                      .Append(kv.Value.frames).Append(" render(s), ").Append(kv.Value.frames > 0 ? (kv.Value.groups / (double)kv.Value.frames).ToString("F0") : "0").Append(" redrawn group(s) each)");
                sb.Append($", of it group draws {s_camGroupTicks * MsPerTick / frames:F2} ms (submit {s_camGroupSubmit * MsPerTick / frames:F2}, {s_camGroupCalls / frames:F0} call(s))");
            }
            sb.Append('.');
            return sb.ToString();
        }

        private static void ResetSplit()
        {
            s_prepTicks = s_submitTicks = s_calls = s_snowCalls = s_partInstances = 0L;
            s_camGroupTicks = s_camGroupSubmit = s_camGroupCalls = 0L;
            s_trisNear = s_trisFar = s_trisShadow = 0L;
            System.Array.Clear(s_lodInstances, 0, s_lodInstances.Length); s_lodCulled = s_proxyTris = s_proxyCalls = 0L; s_cullHeld = s_cullSubmitted = s_cullRebuilds = 0L; InstanceCull.ResetStats();
            s_cams.Clear();
        }

        internal static void BeginFrame()
        {
            if (!s_hooked) { s_hooked = true; Camera.onPreCull += DrawCulledFor; }
            s_roofs.Clear();   // 1.2.137
            s_culled.Clear();
            s_main = null;
            Split = EasyBakeLog.DebugOn();   // 1.2.186
            s_drawStart = Stopwatch.GetTimestamp();
            s_gcAtStart = System.GC.CollectionCount(0);   // 1.2.214: was a GC pause inside this frame's draw timer
            s_culling = false;
            s_shadowSweep = Vector3.zero;
            if (!FiresEasyBakeMeshesPlugin.BatchingCullOffScreen.Value) return;

            var camera = Utils.GetMainCamera();
            s_main = camera;
            if (camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(camera, s_planes);
            for (int i = 0; i < 4; i++) s_planes[i].distance += EdgeMarginMeters;

            var env = EnvMan.instance;
            var sun = env != null ? env.m_dirLight : null;
            float shadowDistance = QualitySettings.shadowDistance;
            if (sun != null && sun.isActiveAndEnabled && sun.shadows != LightShadows.None && shadowDistance > 0f)
                s_shadowSweep = sun.transform.forward * shadowDistance;
            s_culling = true;
        }

        internal static void NoteZone() => s_zoneVisits++;
        internal static void NoteZones(int n) => s_zoneVisits += n;   // IndirectSettledWalk: a frame that did not walk the zones

        // 1.2.149 (Fire 10-30 shadow option C, [Optimize] FarSmallNoShadows, default off): a group whose nearest point is beyond
        // NoShadowBeyondMeters from the main camera, or whose piece mesh is smaller than NoShadowSmallerThanMeters, casts no shadow.
        // Counted per frame for the [Draw] line (parts drawn without a shadow / parts that would have cast).
        private static long s_shadowParts, s_shadowCut;
        internal static bool NoShadowFor(Bounds groupBounds, Mesh mesh)
        {
            if (!FiresEasyBakeMeshesPlugin.FarSmallNoShadows.Value || s_main == null) return false;
            s_shadowParts++;
            float far = FiresEasyBakeMeshesPlugin.NoShadowBeyondMeters.Value, small = FiresEasyBakeMeshesPlugin.NoShadowSmallerThanMeters.Value;
            bool cut = groupBounds.SqrDistance(s_main.transform.position) > far * far
                    || (small > 0f && mesh != null && mesh.bounds.size.magnitude < small);
            if (cut) s_shadowCut++;
            return cut;
        }

        internal static bool Submit(ZoneInstanceGroup group)
        {
            int count = group.Count;
            s_groups++;
            s_instances += count;
            if (s_culling && !Visible(group)) return false;
            s_groupsDrawn++;
            s_instancesDrawn += count;
            return true;
        }

        // 1.2.216 ([visual], lead: the 98-117 ms frames are "no GC: EBM work"): per-frame sub-timers inside the [Draw] loop, kept for the worst slow frame
        internal const int SubDead = 0, SubTake = 1, SubLooksPrep = 2, SubLooksWrite = 3, SubLegacy = 4, SubIndirect = 5, SubN = 6;
        private static readonly string[] SubName = { "dead-group checks", "indirect Take (incl. sync + looks)", "looks prep (snow/wet re-split)", "looks write", "legacy instanced draws", "indirect EndFrame (cull + draw)" };
        private static readonly long[] s_sub = new long[SubN], s_subWorst = new long[SubN], s_subSum = new long[SubN];
        private static double s_subSumTotal; private static int s_subFrames; private static float s_subSayAt;
        internal static long SubT() => Split ? Stopwatch.GetTimestamp() : 0L;
        internal static void SubAcc(int i, long t0) { if (t0 != 0L) s_sub[i] += Stopwatch.GetTimestamp() - t0; }
        private static int s_gcAtStart, s_slowFrames, s_slowWithGc; private static double s_slowWorst; private static bool s_slowWorstGc; private static float s_slowSayAt;

        private static string SubSplit()
        {
            var sb = new System.Text.StringBuilder(); double total = 0;
            for (int i = 0; i < SubN; i++) { double v = s_subWorst[i] * MsPerTick; if (i != SubLooksPrep && i != SubLooksWrite) total += v; sb.Append(SubName[i]).Append(' ').Append(v.ToString("0.0")).Append(" ms; "); }
            sb.Append("untimed rest ").Append(System.Math.Max(0.0, s_slowWorst - total).ToString("0.0")).Append(" ms (looks prep / write are inside Take)");
            return sb.ToString();
        }

        internal static void EndFrame()
        {
            double ms = (Stopwatch.GetTimestamp() - s_drawStart) * MsPerTick;
            s_frames++;
            s_drawMs += ms;
            if (ms > s_worstDrawMs) s_worstDrawMs = ms;
            // 1.2.214 ([visual], O: [Draw] worst 130-153 ms while every timed indirect phase stayed under 7 ms): name the slow frames' cause
            int gcRan = System.GC.CollectionCount(0) - s_gcAtStart;
            if (ms > 50.0) { s_slowFrames++; if (gcRan > 0) s_slowWithGc++; if (ms > s_slowWorst) { s_slowWorst = ms; s_slowWorstGc = gcRan > 0; System.Array.Copy(s_sub, s_subWorst, SubN); } }
            // 1.2.222: the average split too (the 1.86 ms [Draw] had ~0.6 ms no phase named)
            for (int i = 0; i < SubN; i++) s_subSum[i] += s_sub[i];
            s_subSumTotal += ms; s_subFrames++;
            if (Split && Time.unscaledTime >= s_subSayAt && s_subFrames > 0)
            {
                s_subSayAt = Time.unscaledTime + 30f;
                var sb = new System.Text.StringBuilder("[Draw] average split, ms a frame over ").Append(s_subFrames).Append(" frames: total ").Append((s_subSumTotal / s_subFrames).ToString("0.000")).Append(';');
                double named = 0; for (int i = 0; i < SubN; i++) { double v = s_subSum[i] * MsPerTick / s_subFrames; if (i != SubLooksPrep && i != SubLooksWrite) named += v; sb.Append(' ').Append(SubName[i]).Append(' ').Append(v.ToString("0.000")).Append(';'); }
                sb.Append(" untimed rest ").Append(System.Math.Max(0.0, s_subSumTotal / s_subFrames - named).ToString("0.000"));
                sb.Append("; looks change-driven: ").Append(Indirect.EbmIndirect.LooksSkippedTake()).Append(" group take(s) skipped prep + write; not skipped because: ").Append(Indirect.EbmIndirect.LooksWhyTake()).Append(" (key ").Append(FiresEasyBakeMeshesPlugin.IndirectLooksChangeDriven == null || FiresEasyBakeMeshesPlugin.IndirectLooksChangeDriven.Value ? "on" : "off").Append(")");   // 1.2.225 (lead: is it on?)
                EasyBakeLog.Debug(sb.ToString());
                System.Array.Clear(s_subSum, 0, SubN); s_subSumTotal = 0; s_subFrames = 0;
            }
            System.Array.Clear(s_sub, 0, SubN);
            if (Split && Time.unscaledTime >= s_slowSayAt && s_slowFrames > 0)
            {
                s_slowSayAt = Time.unscaledTime + 30f;
                EasyBakeLog.Debug($"[Draw] slow frames (> 50 ms) in the last 30 s: {s_slowFrames}, {s_slowWithGc} of them with a garbage collection inside the draw timer; worst {s_slowWorst:0.0} ms ({(s_slowWorstGc ? "a GC ran in it" : "no GC: EBM work")}); its split: {SubSplit()}");
                s_slowFrames = 0; s_slowWithGc = 0; s_slowWorst = 0; s_slowWorstGc = false;
            }
            s_trackMs += s_trackTicks * MsPerTick;
            s_trackTicks = 0L;
            s_frameMs += Time.unscaledDeltaTime * 1000f;
            Report();
        }

        private static bool Visible(ZoneInstanceGroup group)
        {
            var bounds = group.WorldBounds;
            if (group.CastsShadows && s_shadowSweep != Vector3.zero)
            {
                bounds.Encapsulate(bounds.min + s_shadowSweep);
                bounds.Encapsulate(bounds.max + s_shadowSweep);
            }
            return GeometryUtility.TestPlanesAABB(s_planes, bounds);
        }

        private static void Report()
        {
            float now = Time.unscaledTime;
            if (s_nextReport <= 0f) { s_nextReport = now + ReportSeconds; return; }
            if (now < s_nextReport) return;
            s_nextReport = now + ReportSeconds;

            if (s_frames == 0 || s_groups == 0)
            {
                Reset();
                ResetSplit();
                return;
            }

            double frames = s_frames;
            string tail = FiresEasyBakeMeshesPlugin.BatchingCullOffScreen.Value
                ? "the rest were out of view"
                : "skipping what is out of view is off";
            EasyBakeLog.Debug(
                $"[Draw] Over {s_frames} frames of {s_frameMs / frames:F1} ms: drawing instances took " +
                $"{s_drawMs / frames:F2} ms a frame (worst {s_worstDrawMs:F2} ms) and tracking zones {s_trackMs / frames:F2} ms " +
                $"({Phases(frames)}); {s_groupsDrawn / frames:F0} of {s_groups / frames:F0} groups drawn a frame in " +
                $"{s_zoneVisits / frames:F0} zones, {s_instancesDrawn / frames:F0} of {s_instances / frames:F0} pieces, {tail}." +
                (FiresEasyBakeMeshesPlugin.FarSmallNoShadows.Value
                    ? $" Far/small no shadows: {s_shadowCut / frames:F0} of {s_shadowParts / frames:F0} casting part draws a frame without a shadow (beyond {FiresEasyBakeMeshesPlugin.NoShadowBeyondMeters.Value:0} m or under {FiresEasyBakeMeshesPlugin.NoShadowSmallerThanMeters.Value:0.##} m)."
                    : "") + SplitText(frames));

            Reset();
            ResetSplit();
            s_nextReport = now + ReportSeconds;
        }

        private static string Phases(double frames)
        {
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < s_phaseMs.Length; i++)
            {
                if (parts.Length > 0) parts.Append(", ");
                parts.Append(PhaseNames[i]).Append(' ').Append((s_phaseMs[i] / frames).ToString("F2"));
            }
            return parts.ToString();
        }
    }
}
