using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.273 [Optimize] DungeonInteriorCull (lead 11-10, FDT 1.1.1182 census at swamp spot4: DG_SunkenCrypt = 7,390 enabled renderers in
    /// both our set and VPO's while the player stands outside). Vanilla builds every dungeon interior 5,000 m above its entrance
    /// (Location.Awake, Location.cs:69) and keeps all of it enabled, so Unity culls every one of those renderers for every camera every
    /// frame although nothing outside can ever see them. Vanilla's own test for "inside" is y > 3000 (Character.InInterior).
    ///
    /// What this does, client only (a dedicated server does nothing):
    ///  - when a dungeon is built (DungeonGenerator.Spawn on a client after its rooms load, DungeonGenerator.Generate(int, mode) on a host /
    ///    single player), its STATIC interior is listed once: the active Renderers under the generator's rooms (Room children), Lights
    ///    without LightLod / LightFlicker (those vanilla scripts switch lights themselves), and looping ParticleSystems. Networked objects
    ///    (doors, chests, pickables, spawners, enemies, loot) are separate root objects with their own ZNetView, never children of a room,
    ///    and anything with a ZNetView between it and the generator is skipped too: none of them is ever touched. No colliders, no AI, no ZDOs.
    ///  - just before the main camera culls (Camera.onPreCull, so a teleport in is restored before its first frame renders): camera y > 3000
    ///    and within 200 m (XZ) of a dungeon = that dungeon is shown, every other dungeon is held off. Held off = renderers enabled = false
    ///    (out of Unity's culling), listed lights off, looping particles paused. Only what WE switched is put back: a renderer someone
    ///    else had already turned off is never listed as ours.
    ///  - idle = one compare per main-camera frame (state unchanged: return).
    /// [Optimize] DungeonInteriorCullVerify (default off): on restore, anything we switched off that is already back on (someone else
    /// changed it) is counted, Warning once. Debug census every 30 s.
    /// </summary>
    [HarmonyPatch]
    internal static class DungeonInteriorCull
    {
        private static bool KeyOn => FiresEasyBakeMeshesPlugin.DungeonInteriorCull != null && FiresEasyBakeMeshesPlugin.DungeonInteriorCull.Value;
        private static bool VerifyOn => FiresEasyBakeMeshesPlugin.DungeonInteriorCullVerify != null && FiresEasyBakeMeshesPlugin.DungeonInteriorCullVerify.Value;
        private const float InteriorY = 3000f, ShowRadius = 200f;

        private sealed class Dungeon
        {
            public DungeonGenerator Gen; public string Name; public Vector3 Pos;
            public readonly List<Renderer> Renderers = new List<Renderer>();
            public readonly List<Light> Lights = new List<Light>();
            public readonly List<ParticleSystem> Particles = new List<ParticleSystem>();
            public bool Off;
            public int HoldAfterFrame;   // 1.2.286 StaticClusters: no hold-off before coverage covered the rooms
            // what WE switched at the last hold-off (restored exactly these)
            public readonly List<Renderer> OffR = new List<Renderer>();
            public readonly List<Light> OffL = new List<Light>();
            public readonly List<ParticleSystem> OffP = new List<ParticleSystem>();
        }

        private static readonly List<Dungeon> s_dungeons = new List<Dungeon>();
        private static readonly List<Renderer> s_r = new List<Renderer>(1024);
        private static readonly List<Light> s_l = new List<Light>(64);
        private static readonly List<ParticleSystem> s_p = new List<ParticleSystem>(64);
        private static bool s_hooked, s_lastInside, s_lastKey, s_said, s_saidVerify;
        private static Dungeon s_lastShown;
        private static float s_nextCensus;
        private static int c_listed, c_offs, c_restores, c_mismatch; private static double c_worstRestoreMs, c_worstOffMs;

        private static bool Client => ZNet.instance != null && !ZNet.instance.IsDedicated() && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        // ---- dungeon built: list its static interior once
        [HarmonyPatch(typeof(DungeonGenerator), "Spawn"), HarmonyPostfix]
        private static void AfterSpawn(DungeonGenerator __instance) { using (EbmPatchMarkers.DungeonSpawn.Auto()) { try { List(__instance); } catch (System.Exception e) { EasyBakeLog.Debug("[DungeonCull] list skipped: " + e.Message); } } }   // 1.2.360: profiler marker

        [HarmonyPatch(typeof(DungeonGenerator), nameof(DungeonGenerator.Generate), new[] { typeof(int), typeof(ZoneSystem.SpawnMode) }), HarmonyPostfix]
        private static void AfterGenerate(DungeonGenerator __instance, ZoneSystem.SpawnMode mode)
        {
            if (mode == ZoneSystem.SpawnMode.Ghost) return;   // ghost mode destroys its rooms again (DungeonGenerator.cs:262)
            using (EbmPatchMarkers.DungeonSpawn.Auto()) { try { List(__instance); } catch (System.Exception e) { EasyBakeLog.Debug("[DungeonCull] list skipped: " + e.Message); } }   // 1.2.360: profiler marker
        }

        private static void List(DungeonGenerator gen)
        {
            if (gen == null || !Client) return;
            for (int i = s_dungeons.Count - 1; i >= 0; i--)
            {
                var old = s_dungeons[i];
                if (old.Gen == null) { s_dungeons.RemoveAt(i); continue; }
                if (old.Gen == gen) { Show(old, true); s_dungeons.RemoveAt(i); }   // rebuilt: put back what we held, then list again (1.2.277: was Show(old, false), which HID the old list and dropped the record, so it was never restored: Fire 11-10, crypt floor missing)
            }
            var d = new Dungeon { Gen = gen, Name = gen.name.Replace("(Clone)", ""), Pos = gen.transform.position };
            var root = gen.transform;
            for (int c = 0; c < root.childCount; c++)
            {
                var room = root.GetChild(c);
                if (room.GetComponent<Room>() == null) continue;
                s_r.Clear(); room.GetComponentsInChildren(false, s_r);
                foreach (var r in s_r) if (r != null && Static(r.transform, root)) d.Renderers.Add(r);
                s_l.Clear(); room.GetComponentsInChildren(false, s_l);
                foreach (var l in s_l) if (l != null && Static(l.transform, root) && l.GetComponent<LightLod>() == null && l.GetComponent<LightFlicker>() == null) d.Lights.Add(l);
                s_p.Clear(); room.GetComponentsInChildren(false, s_p);
                foreach (var p in s_p) if (p != null && p.main.loop && Static(p.transform, root)) d.Particles.Add(p);
            }
            if (d.Renderers.Count == 0 && d.Lights.Count == 0 && d.Particles.Count == 0) return;
            s_dungeons.Add(d);
            if (Indirect.Coverage.StaticClusters.On) { Indirect.Coverage.StaticClusters.CoverDungeon(gen); d.HoldAfterFrame = Time.frameCount + 30; }   // 1.2.286: rooms drawn by the batches; first hold waits until coverage has seen them enabled
            c_listed++;
            Hook();
            s_lastKey = !KeyOn;   // force the next pre-cull to judge every dungeon again (the new one included)
            if (!s_said) { s_said = true; EasyBakeLog.Info($"[DungeonCull] first dungeon interior listed ({d.Name}: {d.Renderers.Count} renderer(s), {d.Lights.Count} light(s), {d.Particles.Count} looping particle system(s)); held off while the camera is outside it"); }
        }

        // nothing networked: no ZNetView between the part and the generator (the generator itself has one)
        private static bool Static(Transform t, Transform root)
        {
            for (var p = t; p != null && p != root; p = p.parent) if (p.GetComponent<ZNetView>() != null) return false;
            return true;
        }

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += PreCull;
        }

        // ---- per main-camera frame: one compare when nothing changed
        private static void PreCull(Camera cam)
        {
            if (s_dungeons.Count == 0 || cam == null || cam != Utils.GetMainCamera()) return;
            bool key = KeyOn;
            Vector3 p = cam.transform.position;
            bool inside = p.y > InteriorY;
            if (key == s_lastKey && inside == s_lastInside) { Census(); return; }
            s_lastKey = key; s_lastInside = inside;
            s_lastShown = null;
            for (int i = s_dungeons.Count - 1; i >= 0; i--)
            {
                var d = s_dungeons[i];
                if (d.Gen == null) { s_dungeons.RemoveAt(i); continue; }   // unloaded with its zone
                if (!inside && Time.frameCount < d.HoldAfterFrame) { s_lastKey = !key; continue; }   // 1.2.286: re-judged next frame
                bool show = !key || inside;   // 1.2.277: inside (y > 3000) = every dungeon shown; no XZ distance test (the interior sits at the zone centre, not the entrance)
                if (show && inside) s_lastShown = d;
                Show(d, show);
            }
            Census();
        }

        private static bool Near(Dungeon d, Vector3 cam) { float dx = cam.x - d.Pos.x, dz = cam.z - d.Pos.z; return dx * dx + dz * dz <= ShowRadius * ShowRadius; }

        private static void Show(Dungeon d, bool show)
        {
            if (show == !d.Off) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!show)
            {
                d.OffR.Clear(); d.OffL.Clear(); d.OffP.Clear();
                foreach (var r in d.Renderers) if (r != null && r.enabled && !r.forceRenderingOff) { r.enabled = false; d.OffR.Add(r); }   // 1.2.286: a part the batches draw (forceRenderingOff) is not held
                foreach (var l in d.Lights) if (l != null && l.enabled) { l.enabled = false; d.OffL.Add(l); }
                foreach (var ps in d.Particles) if (ps != null && ps.isPlaying) { ps.Pause(false); d.OffP.Add(ps); }
                d.Off = true; c_offs++;
                double ms = Ms(t0); if (ms > c_worstOffMs) c_worstOffMs = ms;
                return;
            }
            bool verify = VerifyOn;
            foreach (var r in d.OffR) { if (r == null) continue; if (verify && r.enabled) c_mismatch++; r.enabled = true; }
            foreach (var l in d.OffL) { if (l == null) continue; if (verify && l.enabled) c_mismatch++; l.enabled = true; }
            foreach (var ps in d.OffP) if (ps != null && ps.isPaused) ps.Play(false);
            d.OffR.Clear(); d.OffL.Clear(); d.OffP.Clear();
            d.Off = false; c_restores++;
            double rms = Ms(t0); if (rms > c_worstRestoreMs) c_worstRestoreMs = rms;
            if (verify && c_mismatch > 0 && !s_saidVerify) { s_saidVerify = true; EasyBakeLog.Warn($"[DungeonCull] verify: {c_mismatch} part(s) of {d.Name} we held off were already back on at restore (something else switched them); see the Debug census"); }
        }

        private static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>ZNetScene.Shutdown: forget every dungeon (their objects go with the scene).</summary>
        // 1.2.290 (lead: a pose inside the crypt for the StaticClusters Verify leg): each listed dungeon's interior position (its generator), up to 4
        private static string Positions()
        {
            var sb = new System.Text.StringBuilder();
            int n = 0;
            foreach (var d in s_dungeons) { if (d.Gen == null || n >= 4) continue; var p = d.Gen.transform.position; sb.Append(n > 0 ? ", " : "").Append(d.Name).Append(" (").Append(p.x.ToString("0")).Append(' ').Append(p.y.ToString("0")).Append(' ').Append(p.z.ToString("0")).Append(')'); n++; }
            return n > 0 ? sb.ToString() : "none";
        }

        internal static void Clear() { s_dungeons.Clear(); s_lastShown = null; }

        private static void Census()
        {
            float now = Time.unscaledTime;
            if (now < s_nextCensus) return;
            s_nextCensus = now + 30f;
            if (!(VerifyOn || EasyBakeLog.DebugOn()) || s_dungeons.Count == 0) return;   // 1.2.296: Verify on = the verdict at Info
            int heldR = 0, heldL = 0, heldP = 0, off = 0;
            foreach (var d in s_dungeons) if (d.Off) { off++; heldR += d.OffR.Count; heldL += d.OffL.Count; heldP += d.OffP.Count; }
            EasyBakeLog.Say(VerifyOn, $"[DungeonCull] 30 s: {s_dungeons.Count} dungeon(s) listed ({c_listed} new), {off} held off now: {heldR} renderer(s), {heldL} light(s), {heldP} particle system(s); "
                + $"{c_offs} hold-off(s) (worst {c_worstOffMs:0.00} ms), {c_restores} restore(s) (worst {c_worstRestoreMs:0.00} ms){(VerifyOn ? $", verify mismatches {c_mismatch}" : "")}; camera {(s_lastInside ? "inside" : "outside")}; interiors at {Positions()}");
            c_listed = c_offs = c_restores = 0; c_worstRestoreMs = c_worstOffMs = 0;
        }
    }
}
