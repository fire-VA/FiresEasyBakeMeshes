using System.Diagnostics;
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
            s_drawMs = s_worstDrawMs = s_trackMs = s_frameMs = 0.0;
            s_nextReport = 0f;
            s_trackTicks = 0L;
        }

        internal static void NoteZoneTracking(long ticks) => s_trackTicks = ticks;

        internal static void BeginFrame()
        {
            s_drawStart = Stopwatch.GetTimestamp();
            s_culling = false;
            s_shadowSweep = Vector3.zero;
            if (!FiresEasyBakeMeshesPlugin.BatchingCullOffScreen.Value) return;

            var camera = Utils.GetMainCamera();
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

        internal static void EndFrame()
        {
            double ms = (Stopwatch.GetTimestamp() - s_drawStart) * MsPerTick;
            s_frames++;
            s_drawMs += ms;
            if (ms > s_worstDrawMs) s_worstDrawMs = ms;
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
                return;
            }

            double frames = s_frames;
            string tail = FiresEasyBakeMeshesPlugin.BatchingCullOffScreen.Value
                ? "the rest were out of view"
                : "skipping what is out of view is off";
            EasyBakeLog.Info(
                $"[Draw] Over {s_frames} frames of {s_frameMs / frames:F1} ms: drawing instances took " +
                $"{s_drawMs / frames:F2} ms a frame (worst {s_worstDrawMs:F2} ms) and tracking zones {s_trackMs / frames:F2} ms; " +
                $"{s_groupsDrawn / frames:F0} of {s_groups / frames:F0} groups drawn a frame in " +
                $"{s_zoneVisits / frames:F0} zones, {s_instancesDrawn / frames:F0} of {s_instances / frames:F0} pieces, {tail}.");

            Reset();
            s_nextReport = now + ReportSeconds;
        }
    }
}
