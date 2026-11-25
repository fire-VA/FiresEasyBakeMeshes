using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // A3 ([seasons] 11-11, Tools\PLAN_ebm_water_tile_cull_20261111.md; lead approved): each zone water tile's renderer gets bounds that are the REAL wave
    // slab instead of the mesh's padded 0..20 m box.
    // Why (RenderDoc, 11-11 2:29 PM replay, frames O 5199 / O cap5 / A2): 36 of 92 torch passes on water came from lights that sit higher above the
    // water than their own range (light position + range read by register from vanilla's torch pass); they lit 0 pixels and cost 0.236-0.248 ms (replay).
    // The likely reason is the 20 m tall tile box overlapping those lights (Unity's light-to-renderer rule is internal: UNCONFIRMED, proven by the A/B).
    //  - Slab (local y, around the water surface): from -(4.66 x depth + 0.5 m) to +(18.64 x depth + 0.5 m). Vanilla WaterVolume.CreateWave terms lie in
    //    [-0.2 h, +0.8 h] with the heights summing to 23.3 (troughs to -4.66, crests to +18.64), times Lerp(0, wind <= 1, depth) - the same bound A1 uses
    //    (WaterHideBuried.WaveDepth01, one method). xz stays the mesh's own.
    //  - Bounds only: no draw, material, renderer state or physics changes. Restored with Renderer.ResetLocalBounds on key off / tile gone.
    //  - Verify (WaterTightTileBoundsVerify): nothing changed; the census shows a MODEL count of torch-on-tile pairs (light spheres overlapping the box)
    //    with today's box vs the slab, and a wave check: vanilla's own CPU wave height (WaterVolume.GetWaterSurface) is sampled at 9 points of a tile
    //    every 2 s (round-robin); a surface outside the slab = TOO TIGHT, logged with its position. The GPU running the same wave terms is UNCONFIRMED
    //    (the domain disassembly read is pending), so Fire's look check at the water's edge stays.
    internal static class WaterTightBounds
    {
        internal static ConfigEntry<bool> Enabled, Verify;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Optimize", "WaterTightTileBounds", true /* 1.2.305 ON: KEEP -0.016 ms spot4 + verify 135 samples TOO TIGHT 0 (DefaultsMigrated1305) */,
                "Each water tile's culling box is made as tall as its waves can really reach instead of 20 m, so torches and lamps above the water that cannot light it no longer make the game draw that tile again for them. Same water look. (Tiles split by WaterSplitLitTiles keep their pieces' own boxes, so it does nothing on those.) Off = as before.");
            Verify = cfg.Bind("Optimize", "WaterTightTileBoundsVerify", false,
                "Test twin of WaterTightTileBounds: nothing is changed; the log counts torch-on-water overlaps with today's box and the tight one, and checks the real wave height against the tight box (TOO TIGHT must be 0). Off = no test.");
        }

        private sealed class Tile { public WaterVolume Volume; public MeshRenderer Renderer; public bool Applied; public int Seen; public Bounds Last; }
        private static readonly Dictionary<WaterVolume, Tile> s_tiles = new Dictionary<WaterVolume, Tile>();
        private static readonly List<Tile> s_order = new List<Tile>();
        private static readonly List<WaterVolume> s_gone = new List<WaterVolume>();
        private static readonly List<Light> s_lights = new List<Light>(64);
        private static readonly LightType[] s_lightTypes = { LightType.Point, LightType.Spot };
        private static int s_stamp, s_rr; private static float s_nextScan, s_nextCount, s_nextWave, s_nextCensus;
        private static bool s_wasActive; private static string s_state = "";
        private static int w_pairsBefore, w_pairsAfter, w_countRuns, w_waveSamples, w_tooTight;

        internal static void Tick()
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            bool on = !headless && Enabled != null && Enabled.Value;
            bool verify = !headless && Verify != null && Verify.Value;
            if (!on && !verify)
            {
                if (s_wasActive) ReleaseAll();
                s_wasActive = false;
                Say(headless ? "off (no graphics: dedicated server)" : "off (WaterTightTileBounds and its Verify are off)");
                return;
            }
            s_wasActive = true;
            Say(on ? (verify ? "on + VERIFY counts" : "on (water tile boxes = their wave slab)") : "VERIFY (nothing changed; torch overlaps and wave heights counted)");
            float now = Time.unscaledTime;
            if (now >= s_nextScan) { s_nextScan = now + 0.5f; Scan(on && !verify); }
            // [visual]'s review: the counts and the wave check are census work, run only when they are printed (Verify or Debug), so the shipped ON path is
            // the 0.5 s scan alone (no Light.GetLights arrays, no per-second work in a perf leg)
            bool census = verify || EasyBakeLog.DebugOn();
            if (census && now >= s_nextCount) { s_nextCount = now + 1f; CountPairs(); }
            if (census && now >= s_nextWave && s_order.Count > 0) { s_nextWave = now + 2f; s_rr = (s_rr + 1) % s_order.Count; CheckWaves(s_order[s_rr]); }
            if (now >= s_nextCensus) Census(verify);
        }

        private static void Scan(bool apply)
        {
            var list = WaterVolume.Instances; int stamp = ++s_stamp;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null) continue;
                    if (s_tiles.TryGetValue(v, out var known)) { if (known != null) { known.Seen = stamp; Sync(known, apply); } continue; }
                    if (!WaterHideBuried.IsZoneTile(v)) { s_tiles[v] = null; continue; }
                    var t = new Tile { Volume = v, Renderer = v.m_waterSurface, Seen = stamp };
                    s_tiles[v] = t; s_order.Add(t);
                    Sync(t, apply);
                }
            s_gone.Clear();
            foreach (var kv in s_tiles) if (kv.Key == null || (kv.Value != null && (kv.Value.Renderer == null || kv.Value.Seen != stamp))) s_gone.Add(kv.Key);
            for (int i = 0; i < s_gone.Count; i++)
            {
                if (s_tiles.TryGetValue(s_gone[i], out var t) && t != null) { if (t.Applied && t.Renderer != null) t.Renderer.ResetLocalBounds(); s_order.Remove(t); }
                s_tiles.Remove(s_gone[i]);
            }
        }

        // the slab in the renderer's LOCAL space: the mesh's own xz, y = the wave bound around the surface (surface offset included)
        private static Bounds SlabLocal(Tile t) => SlabLocal(t.Volume, t.Renderer);
        // 1.2.305: shared with A2 (WaterHideOccluded tests the same slab against the depth pyramid): ONE slab method
        internal static Bounds SlabWorld(WaterVolume v, MeshRenderer r) => WorldBounds(r.transform.localToWorldMatrix, SlabLocal(v, r));
        private static Bounds SlabLocal(WaterVolume v, MeshRenderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            Bounds mb = mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : r.localBounds;
            float sy = Mathf.Max(1e-4f, Mathf.Abs(r.transform.lossyScale.y));
            float d = WaterHideBuried.WaveDepth01(v);
            float lo = (v.m_surfaceOffset - (WaterHideBuried.TroughPerDepth * d + WaterHideBuried.WaveSpare)) / sy;
            float hi = (v.m_surfaceOffset + (WaterHideBuried.CrestPerDepth * d + WaterHideBuried.WaveSpare)) / sy;
            var b = new Bounds();
            b.SetMinMax(new Vector3(mb.min.x, lo, mb.min.z), new Vector3(mb.max.x, hi, mb.max.z));
            return b;
        }

        private static void Sync(Tile t, bool apply)
        {
            if (t.Renderer == null) return;
            if (apply)
            {
                // [visual]'s review: assigned only when the slab changed (in practice once per tile, then on a heightmap regen moving the ocean depth),
                // so Unity's renderer bounds are not dirtied twice a second per tile
                Bounds slab = SlabLocal(t);
                if (!t.Applied || slab.min != t.Last.min || slab.max != t.Last.max) { t.Renderer.localBounds = slab; t.Last = slab; }
                t.Applied = true;
            }
            else if (t.Applied) { t.Renderer.ResetLocalBounds(); t.Applied = false; }
        }

        // MODEL (Unity's own light-to-renderer rule is internal): torch spheres overlapping today's box vs the slab, for lights that can light the water layer
        private static void CountPairs()
        {
            s_lights.Clear();
            int layer = 4;   // the vanilla "Water" layer (the zone water surface's layer in _Zone.prefab)
            foreach (var type in s_lightTypes)
                foreach (var l in Light.GetLights(type, layer))
                    if (l != null && l.isActiveAndEnabled && l.intensity > 0f && l.renderMode != LightRenderMode.ForceVertex) s_lights.Add(l);
            int before = 0, after = 0;
            for (int i = 0; i < s_order.Count; i++)
            {
                var t = s_order[i];
                if (t.Renderer == null || !t.Renderer.enabled || t.Renderer.forceRenderingOff) continue;
                var tr = t.Renderer.transform;
                var mf = t.Renderer.GetComponent<MeshFilter>();
                Bounds orig = mf != null && mf.sharedMesh != null ? WorldBounds(tr.localToWorldMatrix, mf.sharedMesh.bounds) : t.Renderer.bounds;
                Bounds slab = WorldBounds(tr.localToWorldMatrix, SlabLocal(t));
                for (int k = 0; k < s_lights.Count; k++)
                {
                    Vector3 p = s_lights[k].transform.position; float r = s_lights[k].range;
                    if ((orig.ClosestPoint(p) - p).sqrMagnitude <= r * r) before++;
                    if ((slab.ClosestPoint(p) - p).sqrMagnitude <= r * r) after++;
                }
            }
            w_pairsBefore += before; w_pairsAfter += after; w_countRuns++;
        }

        // vanilla's CPU wave height at 9 points of the tile, against the slab
        private static void CheckWaves(Tile t)
        {
            if (t.Renderer == null || t.Volume == null) return;
            var tr = t.Renderer.transform;
            Bounds slab = WorldBounds(tr.localToWorldMatrix, SlabLocal(t));
            Bounds b = t.Renderer.bounds;
            for (int ix = 0; ix < 3; ix++)
                for (int iz = 0; iz < 3; iz++)
                {
                    var p = new Vector3(Mathf.Lerp(b.min.x, b.max.x, 0.1f + 0.4f * ix), 0f, Mathf.Lerp(b.min.z, b.max.z, 0.1f + 0.4f * iz));
                    float s;
                    try { s = t.Volume.GetWaterSurface(p); } catch { continue; }
                    w_waveSamples++;
                    if (s > slab.max.y || s < slab.min.y)
                    {
                        w_tooTight++;
                        EasyBakeLog.Say(Verify != null && Verify.Value, $"[WaterBounds] TOO TIGHT: wave surface {s:0.00} m outside the slab {slab.min.y:0.00}..{slab.max.y:0.00} m at ({p.x:0}, {p.z:0})");
                    }
                }
        }

        private static Bounds WorldBounds(Matrix4x4 m, Bounds local)
        {
            Vector3 c = m.MultiplyPoint3x4(local.center), e = local.extents;
            Vector3 ax = m.MultiplyVector(new Vector3(e.x, 0f, 0f)), ay = m.MultiplyVector(new Vector3(0f, e.y, 0f)), az = m.MultiplyVector(new Vector3(0f, 0f, e.z));
            return new Bounds(c, 2f * new Vector3(Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x), Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y), Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z)));
        }

        private static void ReleaseAll()
        {
            for (int i = 0; i < s_order.Count; i++) { var t = s_order[i]; if (t.Applied && t.Renderer != null) t.Renderer.ResetLocalBounds(); }
            s_order.Clear(); s_tiles.Clear();
        }

        private static void Say(string state)
        {
            if (state == s_state) return;
            s_state = state;
            EasyBakeLog.Info("[WaterBounds] " + state);
        }

        private static void Census(bool verify)
        {
            s_nextCensus = Time.unscaledTime + 30f;
            if ((verify || EasyBakeLog.DebugOn()) && w_countRuns > 0)
            {
                float n = w_countRuns;
                EasyBakeLog.Say(verify, $"[WaterBounds] 30 s: {s_order.Count} zone water tile(s); MODEL torch-on-tile overlaps per check: today's box {w_pairsBefore / n:0.0} -> wave slab {w_pairsAfter / n:0.0} ({s_lights.Count} pixel light(s) on the water layer); "
                    + $"wave check: {w_waveSamples} sample(s), TOO TIGHT {w_tooTight}");
            }
            w_pairsBefore = w_pairsAfter = w_countRuns = w_waveSamples = w_tooTight = 0;
        }
    }
}
