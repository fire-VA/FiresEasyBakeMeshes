using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The far view's one frame driver (Fire 11-08: FarLands moved up; Core Far\ = #1 impostor engine [visual], #4 far cache + #6 far
    /// lights [generator], #5 far buildings [ghost]). One hidden MonoBehaviour, created the first time a Far feature asks for it, that
    /// gives each feature an Update and a late hook (after EBM's LateUpdate draws, like LiveMap) and times them for the 30 s Debug lines
    /// and FDT's FrameTimer labels ("Far:&lt;name&gt;"). A feature that throws is switched off for the session with one Warning naming it,
    /// so one broken part never takes the others down. Nothing exists until a feature registers; nothing runs on a headless server
    /// unless the feature asks for it (Register(..., serverToo: true)).
    /// </summary>
    [DefaultExecutionOrder(31000)]
    internal sealed class FarRunner : MonoBehaviour
    {
        private sealed class Part
        {
            public string Name; public Action Update, Late; public bool ServerToo, Off;
            public long Ticks, Worst; public int Frames;
        }

        private static FarRunner s_instance;
        private static readonly List<Part> s_parts = new List<Part>();
        private static float s_nextLog;

        /// <summary>True on a client with a graphics device (the far view draws nothing on a dedicated server).</summary>
        public static bool Client => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Adds a far part's hooks (main thread). update runs in Update, late in LateUpdate after EBM's draws. Idempotent by name.</summary>
        public static void Register(string name, Action update, Action late, bool serverToo = false)
        {
            if (string.IsNullOrEmpty(name)) return;
            foreach (var p in s_parts) if (p.Name == name) { p.Update = update; p.Late = late; p.ServerToo = serverToo; p.Off = false; return; }
            s_parts.Add(new Part { Name = name, Update = update, Late = late, ServerToo = serverToo });
            if (s_instance == null)
            {
                var go = new GameObject("FiresEBM_FarRunner") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<FarRunner>();
            }
        }

        private void Update()
        {
            long tSelf = EasyBake.EbmSelfCost.T();   // 1.2.219: EBM's own per-frame cost
            try { UpdateInner(); } finally { EasyBake.EbmSelfCost.Acc(2, tSelf); }
        }

        private void UpdateInner()
        {
            // EBM port: EBM's own far job queue (replaces Core's FrameBudgetScheduler), drained here first, about 1 ms a frame, client and server.
            try { EbmFarJobs.Drain(); } catch (Exception e) { EasyBakeLog.Warn($"[Far] jobs failed this frame: {e.GetType().Name}: {e.Message}"); }
            foreach (var p in s_parts) Run(p, p.Update);
        }
        private void LateUpdate()
        {
            long tSelf = EasyBake.EbmSelfCost.T();   // 1.2.219: EBM's own per-frame cost
            try { LateUpdateInner(); } finally { EasyBake.EbmSelfCost.Acc(3, tSelf); }
        }

        private void LateUpdateInner()
        {
            foreach (var p in s_parts) Run(p, p.Late);
            if (Time.unscaledTime >= s_nextLog) { s_nextLog = Time.unscaledTime + 30f; Report(); }
        }

        private static void Run(Part p, Action a)
        {
            if (a == null || p.Off || (!p.ServerToo && !Client)) return;
            long t0 = Stopwatch.GetTimestamp();
            try { a(); }
            catch (Exception e)
            {
                p.Off = true;
                EasyBakeLog.Warn($"[Far] {p.Name} failed and is off until the game restarts: {e.GetType().Name}: {e.Message}");
                EasyBakeLog.Debug($"[Far] {p.Name} failure: {e}");
            }
            long d = Stopwatch.GetTimestamp() - t0; p.Ticks += d; if (d > p.Worst) p.Worst = d; p.Frames++;
        }

        private static void Report()
        {
            if (s_parts.Count == 0) return;
            double us = 1e6 / Stopwatch.Frequency;
            var parts = new List<string>();
            foreach (var p in s_parts)
            {
                if (p.Frames == 0) continue;
                parts.Add($"{p.Name} avg {p.Ticks * us / p.Frames:0} us, worst {p.Worst * us:0} us{(p.Off ? " (off)" : "")}");
                p.Ticks = 0; p.Worst = 0; p.Frames = 0;
            }
            if (parts.Count > 0) EasyBakeLog.Debug("[Far] main thread, last 30 s: " + string.Join("; ", parts.ToArray()));
        }
    }
}
