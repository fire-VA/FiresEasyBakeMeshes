using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // E34 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E34; Tools\READ_particles_20261109.md: ours updated 1,141 particle systems a
    // frame at the town vs VPO's 600, cause not shown). Key [Batching] ParticleIdlePause, OFF until the A/B. EBM's own code; relies on no other mod.
    //
    // The rule (provably invisible, so the look is identical): a looping particle system that is PLAYING with ZERO live particles and its
    // EMISSION OFF draws nothing and will draw nothing until something changes, on screen or off. Such a system is paused. It is played again in
    // the SAME frame (checked in the main camera's onPreCull, after every Update / LateUpdate) as soon as anything could make it draw:
    // emission switched on (a fire lit or fuelled, EBM's own rain shelter), a particle appeared (another script's Emit), or the game played,
    // stopped or re-enabled it itself (then it is simply dropped from our list). A system that is emitting is never touched, so no catch-up
    // question arises. Fire's bar: lighting / fuelling shows the frame it happens.
    // Scope: systems on objects created through ZNetScene (pieces and world objects), looping, culling mode still Automatic (a system another
    // mod set to Pause / PauseAndCatchup / AlwaysSimulate is theirs). When FiresFixes' own fire-particle culling is live (its public ACTIVE
    // flag FixesApi.ParticleCullingActive, read by reflection; absent = off), the system names FF handles are skipped as well, so nothing is
    // handled twice.
    // Debug census every 30 s: watched, paused now, paused / resumed this window by reason, skipped (not Automatic, FF's), ms a frame.
    internal static class EbmParticleIdle
    {
        internal static ConfigEntry<bool> Enabled;
        private static bool On => Enabled != null && Enabled.Value;

        private const int MaxWatched = 8192, SlicePerFrame = 128;
        private static readonly List<ParticleSystem> s_running = new List<ParticleSystem>();
        private static readonly List<ParticleSystem> s_paused = new List<ParticleSystem>();
        private static readonly HashSet<int> s_known = new HashSet<int>();
        private static readonly List<ParticleSystem> s_scratch = new List<ParticleSystem>(16);
        private static readonly HashSet<string> s_none = new HashSet<string>();
        private static int s_cursor, s_lastFrame = -1;
        private static bool s_hooked;

        // census (window)
        private static int c_paused, c_resEmission, c_resParticles, c_resGame, c_skipMode, c_skipFF, c_full, c_dropRecheck;
        private static long s_ticks; private static int s_frames; private static float s_nextSay;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Batching", "ParticleIdlePause", false,
                "Looping particle effects on pieces and world objects that have no particles and are not emitting (nothing to draw) are paused, and resume the same frame they emit again (a fire lit or fuelled shows at once). No visible change. Off = the game updates them every frame.");
            Enabled.SettingChanged += (_, __) => { if (!On) ResumeAll("key off"); };
        }

        // From ZNetScene.CreateObject's postfix (ZNetScenePatches), beside IdleGates.PauseOffScreen
        internal static void Consider(GameObject go)
        {
            if (!On || go == null || s_none.Contains(go.name)) return;
            Hook();
            go.GetComponentsInChildren(true, s_scratch);
            if (s_scratch.Count == 0) { if (s_none.Count < 8192) s_none.Add(go.name); s_scratch.Clear(); return; }
            bool ff = FfParticleActive();
            foreach (var ps in s_scratch)
            {
                if (ps == null || s_running.Count + s_paused.Count >= MaxWatched) { c_full++; continue; }
                var main = ps.main;
                if (!main.loop) continue;
                if (main.cullingMode != ParticleSystemCullingMode.Automatic) { c_skipMode++; continue; }
                bool ffName = FfHandles(ps.gameObject.name);
                if (ff && ffName) { c_skipFF++; continue; }
                int id = ps.GetInstanceID();
                if (!s_known.Add(id)) continue;
                s_running.Add(ps);
                if (ffName) s_ffNamed.Add(id);   // review 11-09: dropped later if FF's flag goes live
            }
            s_scratch.Clear();
        }

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += OnPreCull;
        }

        private static void OnPreCull(Camera cam)
        {
            if (Time.frameCount == s_lastFrame || cam == null || cam != Utils.GetMainCamera()) return;
            s_lastFrame = Time.frameCount;
            if (!On) return;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                // 1. every paused system, every frame: resume the frame anything could make it draw
                for (int i = s_paused.Count - 1; i >= 0; i--)
                {
                    var ps = s_paused[i];
                    if (ps == null) { Drop(s_paused, i, ps); continue; }
                    if (NotOurs(ps)) { if (ps.isPaused) ps.Play(false); c_dropRecheck++; Drop(s_paused, i, ps); continue; }   // review 11-09: mode changed or FF took it: hand back, stop watching
                    if (!ps.isPaused) { c_resGame++; Move(s_paused, i, s_running); continue; }   // the game played / stopped / re-enabled it
                    bool emits = ps.emission.enabled, has = ps.particleCount > 0;
                    if (!emits && !has) continue;
                    ps.Play(false);
                    if (emits) c_resEmission++; else c_resParticles++;
                    Move(s_paused, i, s_running);
                }
                // 2. a slice of the running ones: pause the playing, empty, non-emitting ones
                int n = Mathf.Min(SlicePerFrame, s_running.Count);
                for (int k = 0; k < n && s_running.Count > 0; k++)
                {
                    if (s_cursor >= s_running.Count) s_cursor = 0;
                    var ps = s_running[s_cursor];
                    if (ps == null) { Drop(s_running, s_cursor, ps); continue; }
                    if (NotOurs(ps)) { c_dropRecheck++; Drop(s_running, s_cursor, ps); continue; }   // review 11-09: re-checked every visit, not only at creation
                    if (ps.isPlaying && !ps.isPaused && ps.particleCount == 0 && !ps.emission.enabled && ps.gameObject.activeInHierarchy)
                    {
                        ps.Pause(false);
                        c_paused++;
                        Move(s_running, s_cursor, s_paused);
                        continue;
                    }
                    s_cursor++;
                }
            }
            catch (Exception e) { EasyBakeLog.Debug("[Particles] idle pause pass threw (resuming all): " + e.Message); ResumeAll("fault"); Enabled.Value = false; }
            finally { s_ticks += Stopwatch.GetTimestamp() - t0; s_frames++; Say(); }
        }

        private static void Move(List<ParticleSystem> from, int i, List<ParticleSystem> to) { var ps = from[i]; from[i] = from[from.Count - 1]; from.RemoveAt(from.Count - 1); to.Add(ps); }
        // review 11-09: the ever-seen set shrinks with the lists (a destroyed system's id is still readable from its managed wrapper)
        private static void Drop(List<ParticleSystem> from, int i, ParticleSystem ps)
        {
            var it = from[i];
            if ((object)it != null) { int id = it.GetInstanceID(); s_known.Remove(id); s_ffNamed.Remove(id); }
            from[i] = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
        }

        // review 11-09: culling mode and FiresFixes' flag re-checked on every visit (the flag can go live after creation; a mod can change the mode)
        private static readonly HashSet<int> s_ffNamed = new HashSet<int>();   // ids whose object name is one FF handles (names do not change; read once)
        private static bool NotOurs(ParticleSystem ps)
        {
            if (ps.main.cullingMode != ParticleSystemCullingMode.Automatic) return true;
            return s_ffNamed.Count > 0 && s_ffNamed.Contains(ps.GetInstanceID()) && FfParticleActive();
        }

        private static void ResumeAll(string why)
        {
            int n = 0;
            foreach (var ps in s_paused) if (ps != null && ps.isPaused) { ps.Play(false); n++; }
            s_running.AddRange(s_paused); s_paused.Clear();
            if (n > 0) EasyBakeLog.Info($"[Particles] idle pause: {n} paused system(s) resumed ({why})");
        }

        private static void Say()
        {
            float now = Time.unscaledTime;
            if (now < s_nextSay) return;
            if (s_nextSay > 0f && EasyBakeLog.DebugOn())
            {
                double ms = s_frames > 0 ? s_ticks * 1000.0 / Stopwatch.Frequency / s_frames : 0;
                EasyBakeLog.Debug($"[Particles] idle pause 30 s: watching {s_running.Count + s_paused.Count} looping system(s), paused now {s_paused.Count}; this window paused {c_paused}, resumed {c_resEmission} (emission on) + {c_resParticles} (a particle appeared) + {c_resGame} (the game played or stopped it); " +
                    $"not watched: culling mode not Automatic {c_skipMode}, FiresFixes' own {c_skipFF} (FF culling {(FfParticleActive() ? "live" : "not live")}), over the {MaxWatched} cap {c_full}; dropped on re-check (culling mode changed or FF took it) {c_dropRecheck}; {ms:0.000} ms/frame");
            }
            s_nextSay = now + 30f;
            c_paused = c_resEmission = c_resParticles = c_resGame = c_skipMode = c_skipFF = c_full = c_dropRecheck = 0; s_ticks = 0; s_frames = 0;
        }

        // ── FiresFixes' public ACTIVE flag (FixesApi.ParticleCullingActive), by reflection: no reference to FF ──
        private static Func<bool> s_ffActive; private static bool s_ffLooked;
        private static bool FfParticleActive()
        {
            if (!s_ffLooked)
            {
                s_ffLooked = true;
                try
                {
                    Type t = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        if ((t = asm.GetType("FiresFixes.Api.FixesApi", false)) != null) break;
                    var p = t?.GetProperty("ParticleCullingActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (p != null && p.PropertyType == typeof(bool)) s_ffActive = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), p.GetGetMethod());
                }
                catch { s_ffActive = null; }
            }
            try { return s_ffActive != null && s_ffActive(); } catch { return false; }
        }

        // the names FF's fire-particle culling handles (VpoMethods0171.PrefabParticleCulling); kept equal by the FF owner's API note
        private static bool FfHandles(string name)
        {
            switch (name) { case "flare": case "flames": case "flames (1)": case "embers": case "low_flames": case "sparcs (1)": case "smoke (1)": return true; default: return false; }
        }
    }
}
