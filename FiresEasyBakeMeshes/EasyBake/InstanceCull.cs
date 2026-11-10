using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.200 (DESIGN_OcclusionLOD 2b as approved 11-08, TOWN50): per-instance culling for baked (instanced) groups WITHOUT more draw calls.
    // [Optimize] InstancedInstanceCull (default off). Each group's instances are tested on ONE worker thread against the main camera's
    // frustum, with a conservative shadow sweep: an instance is kept when its bounds sphere, swept along the sun's direction by the shadow
    // distance, can reach the view (per plane: d(c) + D * max(0, n . sunDir) >= -r), so no caster whose shadow can fall into view is lost.
    // The frustum is widened by a fov margin and pushed out by the camera's travel, because the result is used one frame later. A camera
    // that turned more than MaxTurnDeg or jumped since the job's frame gets no culling that frame. Main thread: posts jobs and reads finished
    // results; the group rebuilds its compacted lists only when the visible set changed. Other cameras keep the full lists.
    internal static class InstanceCull
    {
        internal const float FovMargin = 1.15f, MaxTurnDeg = 10f, MaxJumpM = 20f, TravelMarginM = 2f;

        internal sealed class Job
        {
            public ZoneInstanceGroup Group;
            public Vector4[] Spheres; public int Count; public int Ver;
            public Plane[] Planes = new Plane[6];
            public Vector3 SunDir; public float ShadowDistance; public bool HasSun;
            public int[] Out; public int OutCount;
            public Vector3 CamPos, CamFwd;
            public volatile bool Done;
        }

        private static readonly Queue<Job> s_in = new Queue<Job>();
        private static readonly object s_lock = new object();
        private static readonly AutoResetEvent s_wake = new AutoResetEvent(false);
        private static Thread s_thread;
        private static long s_workerTicks; private static int s_jobs;
        internal static long WorkerTicks => Interlocked.Read(ref s_workerTicks);
        internal static int Jobs => s_jobs;
        internal static void ResetStats() { Interlocked.Exchange(ref s_workerTicks, 0L); s_jobs = 0; }

        internal static bool Enabled => FiresEasyBakeMeshesPlugin.InstancedInstanceCull != null && FiresEasyBakeMeshesPlugin.InstancedInstanceCull.Value;

        // ── this frame's view, computed once per frame on the main thread ──
        private static int s_frame = -1;
        private static bool s_viewOk;
        private static readonly Plane[] s_planes = new Plane[6];
        private static Vector3 s_camPos, s_camFwd, s_sunDir; private static float s_shadowDist; private static bool s_hasSun;
        private static Vector3 s_lastCamPos; private static float s_speed;

        internal static bool View(out Vector3 camPos, out Vector3 camFwd)
        {
            int f = Time.frameCount;
            if (f != s_frame)
            {
                s_frame = f;
                s_viewOk = false;
                var cam = Camera.main;
                if (cam != null)
                {
                    s_camPos = cam.transform.position; s_camFwd = cam.transform.forward;
                    float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
                    s_speed = Mathf.Min((s_camPos - s_lastCamPos).magnitude / dt, 200f);
                    s_lastCamPos = s_camPos;
                    var proj = Matrix4x4.Perspective(Mathf.Min(cam.fieldOfView * FovMargin, 170f), cam.aspect * FovMargin, cam.nearClipPlane, cam.farClipPlane);
                    GeometryUtility.CalculateFrustumPlanes(proj * cam.worldToCameraMatrix, s_planes);
                    float push = TravelMarginM + s_speed * dt * 2f;   // the result is used a frame later
                    for (int i = 0; i < 6; i++) s_planes[i].distance += push;
                    var sun = RenderSettings.sun;
                    s_hasSun = sun != null && sun.isActiveAndEnabled && sun.shadows != LightShadows.None;
                    s_sunDir = s_hasSun ? sun.transform.forward : Vector3.down;
                    s_shadowDist = QualitySettings.shadowDistance;
                    s_viewOk = true;
                }
            }
            camPos = s_camPos; camFwd = s_camFwd;
            return s_viewOk;
        }

        internal static void Post(Job job)
        {
            for (int i = 0; i < 6; i++) job.Planes[i] = s_planes[i];
            job.SunDir = s_sunDir; job.ShadowDistance = s_shadowDist; job.HasSun = s_hasSun;
            job.CamPos = s_camPos; job.CamFwd = s_camFwd;
            job.Done = false;
            EnsureThread();
            lock (s_lock) s_in.Enqueue(job);
            s_wake.Set();
        }

        private static void EnsureThread()
        {
            if (s_thread != null) return;
            s_thread = new Thread(Loop) { IsBackground = true, Name = "EBM instance cull", Priority = System.Threading.ThreadPriority.BelowNormal };
            s_thread.Start();
        }

        private static void Loop()
        {
            while (true)
            {
                s_wake.WaitOne();
                while (true)
                {
                    Job job = null;
                    lock (s_lock) if (s_in.Count > 0) job = s_in.Dequeue();
                    if (job == null) break;
                    long t0 = Stopwatch.GetTimestamp();
                    try { Run(job); } catch { job.OutCount = -1; }   // -1 = no result: the group draws its full lists
                    Interlocked.Add(ref s_workerTicks, Stopwatch.GetTimestamp() - t0);
                    s_jobs++;
                    job.Done = true;
                }
            }
        }

        // pure data: spheres + planes in, visible indices (ascending) out
        private static void Run(Job job)
        {
            int n = job.Count;
            if (job.Out == null || job.Out.Length < n) job.Out = new int[Math.Max(8, Mathf.NextPowerOfTwo(n))];
            var planes = job.Planes; var sp = job.Spheres; var o = job.Out;
            // a caster can sit further from its shadow than the shadow distance when the sun is low: half as much again, conservative
            float sweep = job.HasSun ? job.ShadowDistance * 1.5f : 0f;
            Vector3 l = job.SunDir;
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                var s = sp[i];
                var c = new Vector3(s.x, s.y, s.z);
                bool keep = true;
                for (int p = 0; p < 6; p++)
                {
                    float d = planes[p].GetDistanceToPoint(c);
                    float reach = sweep > 0f ? sweep * Mathf.Max(0f, Vector3.Dot(planes[p].normal, l)) : 0f;
                    if (d + reach < -s.w) { keep = false; break; }
                }
                if (keep) o[k++] = i;
            }
            job.OutCount = k;
        }
    }
}
