using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // A1 ([seasons] 11-11, Tools\PLAN_ebm_water_tile_cull_20261111.md, [visual] approved): vanilla zone water tiles that lie wholly UNDER the terrain are
    // not drawn. Facts behind it (RenderDoc, swamp spot 4, frame 5199): 51 of 78 water tiles had ZERO visible pixels and still cost 104 base draws
    // (0.725 ms of 1.313 ms replay), because vanilla Custom/Water is tessellated and pays per draw whether or not a pixel survives.
    //
    //  - Buried = every terrain vertex the tile covers (Heightmap.GetHeight + its transform y; the terrain is drawn from that grid with linear triangles,
    //    so vertices above means the drawn surface is above) is at or above the water level + a wave margin.
    //  - Wave margin, from vanilla WaterVolume.CreateWave / CalcWave (WaterVolume.cs:204-276): each wave term lies in [-0.2 h, +0.8 h], the heights sum
    //    to 23.3, so the crest is at most 18.64 m, times Lerp(0, wind <= 1, depth) with depth = Clamp01(oceanDepth / 10) per corner (public
    //    Heightmap.GetOceanDepth(); m_forceDepth overrides) -> margin = 18.64 x max corner depth + 0.5 m. Shallow swamp tiles get a small margin, deep
    //    sea ~19 m (never hidden).
    //  - Hide = m_waterSurface.forceRenderingOff on the RENDERER only (prior value kept and restored). Never enabled / SetActive / colliders / the
    //    WaterVolume itself: floating, swimming, buoyancy, FGN's ship physics and everything that reads WaterVolume stay vanilla.
    //  - Re-test: when a tile appears, when vanilla Heightmap.Regenerate fires its m_clearConnectedWearNTearCache event for the tile's heightmap (one
    //    forwarder per heightmap, no Harmony; [visual]'s hub only rewrites WearNTear's own call sites), and a round-robin backstop (all tiles every
    //    ~30 s). A tile that fails a re-test is shown again at once.
    //  - Vetoes (EbmApi.RegisterWaterHideVeto, e.g. FAT's voxel caves / cave rivers): any veto answering true for the tile's bounds keeps it drawn.
    //    Asked on every decision, never cached.
    //  - Zone water only: a WaterVolume under a ZNetView (a built piece: the hot tub) is never touched (the same rule FTS uses).
    // Verify (WaterHideBuriedTilesVerify): nothing hidden; would-be hides are counted and each is checked with 4 downward raycasts on the terrain layer at
    // random points in the tile: a hit below the water + margin = a FALSE HIDE, logged with its position.
    internal static class WaterHideBuried
    {
        internal static ConfigEntry<bool> Enabled, Verify;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Optimize", "WaterHideBuriedTiles", true /* 1.2.305 ON: KEEP -0.026 ms spot4 + verify2 972 rays 0 false (DefaultsMigrated1305) */,
                "Water tiles that lie completely under the ground (the terrain above the water and its highest wave everywhere on the tile) are not drawn. Swimming, boats and everything else about the water are unchanged. Off = as before.");
            Verify = cfg.Bind("Optimize", "WaterHideBuriedTilesVerify", false,
                "Test twin of WaterHideBuriedTiles: nothing is hidden; the log counts the tiles it would hide and checks each with ground raycasts (FALSE HIDES must be 0). Off = no test.");
        }

        private const float BackstopSeconds = 30f, ScanSeconds = 0.5f;

        // ── the vanilla wave bound, ONE method for A1 (buried) and A3 (WaterTightBounds) ──
        // WaterVolume.CreateWave terms lie in [-0.2 h, +0.8 h]; the heights sum to 23.3 -> crest <= 18.64 m, trough >= -4.66 m, both x Lerp(0, wind <= 1, depth)
        internal const float CrestPerDepth = 18.64f, TroughPerDepth = 4.66f, WaveSpare = 0.5f;
        // depth as vanilla's DetectWaterDepth makes it (WaterVolume.cs:107-125): Clamp01(oceanDepth / 10) per corner of its heightmap, the largest corner; m_forceDepth overrides
        internal static float WaveDepth01(WaterVolume v)
        {
            if (v == null) return 1f;
            if (v.m_forceDepth >= 0f) return Mathf.Clamp01(v.m_forceDepth);
            float[] od = null;
            try { od = v.m_heightmap != null ? v.m_heightmap.GetOceanDepth() : null; } catch { }
            if (od == null || od.Length < 4) return 1f;   // unknown: the full bound
            float d = 0f;
            for (int i = 0; i < 4; i++) d = Mathf.Max(d, Mathf.Clamp01(od[i] / 10f));
            return d;
        }
        // zone water only (a WaterVolume under a ZNetView is a built piece, e.g. the hot tub), with a surface and a heightmap
        internal static bool IsZoneTile(WaterVolume v) => v != null && v.m_waterSurface != null && v.m_heightmap != null && v.GetComponentInParent<ZNetView>() == null;

        private sealed class Hook { public Heightmap Hm; public Action Fwd; public int Users; public bool Dirty; }
        private sealed class Tile
        {
            public WaterVolume Volume; public MeshRenderer Renderer; public Hook Hook;
            public bool Hidden, WouldHide, PriorForceOff; public string Why = ""; public int Seen;
        }

        private static readonly ConditionalWeakTable<Heightmap, Hook> s_hooks = new ConditionalWeakTable<Heightmap, Hook>();
        private static readonly List<Hook> s_hookList = new List<Hook>();
        private static readonly Dictionary<WaterVolume, Tile> s_tiles = new Dictionary<WaterVolume, Tile>();
        private static readonly List<Tile> s_order = new List<Tile>();   // round-robin backstop
        private static readonly List<WaterVolume> s_gone = new List<WaterVolume>();
        private static readonly List<Func<Bounds, bool>> s_vetoes = new List<Func<Bounds, bool>>();
        private static int s_rr, s_scanStamp; private static float s_nextScan, s_nextCensus, s_nextBackstop;
        private static bool s_wasActive; private static string s_state = "";
        private static int s_terrainMask = -1;

        // census since the last line
        private static int w_tests, w_byNew, w_byTerrain, w_byBackstop, w_hides, w_shows, w_vetoed, w_falseHides, w_raycasts;
        private static int w_whyBuried, w_whyPast, w_whyAbove, w_whyNotBuilt, w_whyOther;
        private static long s_totalRays, s_totalFalse;   // session totals (never reset), so no window can read as an empty pass

        internal static void AddVeto(Func<Bounds, bool> keepVisible) { if (keepVisible != null && !s_vetoes.Contains(keepVisible)) s_vetoes.Add(keepVisible); }
        internal static bool IsHidden(WaterVolume v) => v != null && s_tiles.TryGetValue(v, out var t) && t != null && t.Hidden;

        internal static void Tick()
        {
            bool headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
            bool on = !headless && Enabled != null && Enabled.Value;
            bool verify = !headless && Verify != null && Verify.Value;
            bool active = on || verify;
            if (!active)
            {
                if (s_wasActive) ReleaseAll();
                s_wasActive = false;
                Say(headless ? "off (no graphics: dedicated server)" : "off (WaterHideBuriedTiles and its Verify are off)");
                return;
            }
            if (s_wasActive && !on) ShowAll();   // switched to Verify only: nothing stays hidden
            s_wasActive = true;
            Say(on && !verify ? "on (buried water tiles are not drawn)" : on ? "on + VERIFY counts" : "VERIFY (nothing hidden; would-be hides checked with ground raycasts)");

            float now = Time.unscaledTime;
            if (now >= s_nextScan) { s_nextScan = now + ScanSeconds; Scan(on, verify); }
            // terrain changes: tiles over a heightmap whose Regenerate fired since the last tick
            for (int h = 0; h < s_hookList.Count; h++)
            {
                var hook = s_hookList[h];
                if (!hook.Dirty) continue;
                hook.Dirty = false;
                for (int i = 0; i < s_order.Count; i++) if (s_order[i].Hook == hook) { Decide(s_order[i], on, verify); w_byTerrain++; }
            }
            // backstop paced by TIME, not frames: every tile once per BackstopSeconds (80 tiles = one test every 0.375 s, whatever the frame rate)
            if (s_order.Count > 0 && now >= s_nextBackstop)
            {
                s_nextBackstop = now + BackstopSeconds / s_order.Count;
                s_rr = (s_rr + 1) % s_order.Count;
                Decide(s_order[s_rr], on, verify); w_byBackstop++;
            }
            // a hidden tile stays hidden (another mod, or EBM's own WaterSplit merge, may have cleared the flag)
            if (on) for (int i = 0; i < s_order.Count; i++) { var t = s_order[i]; if (t.Hidden && t.Renderer != null && !t.Renderer.forceRenderingOff) t.Renderer.forceRenderingOff = true; }
            if (now >= s_nextCensus) Census(on, verify);
        }

        // new tiles in, vanished ones out (WaterVolume.OnEnable / OnDisable maintain vanilla's own Instances list)
        private static void Scan(bool on, bool verify)
        {
            var list = WaterVolume.Instances;
            int stamp = ++s_scanStamp;   // [visual]'s review: a stamp per scan instead of List.Contains per tile
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null) continue;
                    if (s_tiles.TryGetValue(v, out var known)) { if (known != null) known.Seen = stamp; continue; }
                    if (!IsZoneTile(v)) { s_tiles[v] = null; continue; }   // left alone for good
                    var t = new Tile { Volume = v, Renderer = v.m_waterSurface, Hook = HookFor(v.m_heightmap), Seen = stamp };
                    s_tiles[v] = t; s_order.Add(t);
                    Decide(t, on, verify); w_byNew++;
                }
            }
            s_gone.Clear();
            foreach (var kv in s_tiles)
            {
                if (kv.Key == null) { s_gone.Add(kv.Key); continue; }
                if (kv.Value != null && (kv.Value.Renderer == null || kv.Value.Seen != stamp)) s_gone.Add(kv.Key);   // left vanilla's Instances (OnDisable) since the last scan
            }
            for (int i = 0; i < s_gone.Count; i++) Forget(s_gone[i]);
        }

        private static Hook HookFor(Heightmap hm)
        {
            if (!s_hooks.TryGetValue(hm, out var hook))
            {
                hook = new Hook { Hm = hm };
                var captured = hook;
                hook.Fwd = () => captured.Dirty = true;   // Regenerate's event: flag only, decided on the next tick (never inside vanilla's call)
                hm.m_clearConnectedWearNTearCache += hook.Fwd;
                s_hooks.Add(hm, hook); s_hookList.Add(hook);
            }
            hook.Users++;
            return hook;
        }

        private static void Unhook(Hook hook)
        {
            if (hook == null || --hook.Users > 0) return;
            if (hook.Hm != null) { try { hook.Hm.m_clearConnectedWearNTearCache -= hook.Fwd; } catch { } s_hooks.Remove(hook.Hm); }
            s_hookList.Remove(hook);
        }

        private static void Forget(WaterVolume key)
        {
            if (!s_tiles.TryGetValue(key, out var t)) { s_tiles.Remove(key); return; }
            if (t != null)
            {
                if (t.Hidden && t.Renderer != null) t.Renderer.forceRenderingOff = t.PriorForceOff;
                Unhook(t.Hook);
                s_order.Remove(t);
            }
            s_tiles.Remove(key);
        }

        // ---------------------------------------------------------------- the decision
        private static void Decide(Tile t, bool on, bool verify)
        {
            if (t.Renderer == null || t.Volume == null) return;
            w_tests++;
            bool buried = IsBuried(t, out t.Why);
            if (buried)
            {
                Bounds b = t.Renderer.bounds;
                for (int i = 0; i < s_vetoes.Count; i++)
                {
                    bool keep;
                    try { keep = s_vetoes[i](b); } catch { keep = true; }   // a throwing veto keeps the tile (never hide on doubt)
                    if (keep) { buried = false; t.Why = "vetoed"; w_vetoed++; break; }
                }
            }
            switch (t.Why)   // [visual]'s review: why, per decision, in the census (a first leg that is all "reaches past" shows at once)
            {
                case "buried": w_whyBuried++; break;
                case "tile reaches past its heightmap": w_whyPast++; break;
                case "water above the ground somewhere": w_whyAbove++; break;
                case "heightmap not built yet": w_whyNotBuilt++; break;
                case "vetoed": break;   // counted as w_vetoed
                default: w_whyOther++; break;
            }
            // 1.2.304 (lead, gate-waterburied: "Verify cast 0 rays"): rays were cast only when a tile FIRST became would-hide - at tile load, before any
            // measured window - so every window's census read 0 rays and FALSE HIDES 0 tested nothing. Now EVERY buried decision in Verify is ray-checked
            // (new tile, terrain change, and the 30 s backstop re-test: each buried tile about twice a minute), and the census carries session totals too.
            if (buried && verify) CheckWithRays(t);
            t.WouldHide = buried;
            bool hide = buried && on && !verify;
            if (hide && !t.Hidden)
            {
                t.PriorForceOff = t.Renderer.forceRenderingOff;
                t.Renderer.forceRenderingOff = true;
                t.Hidden = true; w_hides++;
            }
            else if (!hide && t.Hidden)
            {
                t.Renderer.forceRenderingOff = t.PriorForceOff;
                t.Hidden = false; w_shows++;
            }
        }

        private static bool IsBuried(Tile t, out string why)
        {
            var v = t.Volume; var hm = v.m_heightmap;
            if (hm == null) { why = "no heightmap"; return false; }
            float level = v.transform.position.y + v.m_surfaceOffset;
            float need = level + CrestPerDepth * WaveDepth01(v) + WaveSpare;
            Bounds b = t.Renderer.bounds;
            int width = hm.m_width; float scale = hm.m_scale;
            if (width <= 0 || scale <= 0f) { why = "heightmap not sized"; return false; }
            Vector3 hp = hm.transform.position; int half = width / 2;
            SaySize(b, width, scale);
            // the tile's xz extent, one vertex wider on each side (terrain triangles that straddle the tile edge), CLAMPED to the grid.
            // [visual]'s review + _Zone.prefab: the zone heightmap is m_width 64, m_scale 1 (vertices at +-32 m) and the water surface is scaled 32.1 on a
            // +-1 mesh (+-32.1 m), so every tile overhangs its grid by 0.1 m. That strip lies over the NEIGHBOUR zone, whose own water tile covers it: if
            // that tile is buried, its vertices all clear the margin (so does the strip); if not, it draws the strip itself. So the edge clamps.
            // Rejected only when the tile overhangs by more than one cell (a geometry this test was not made for).
            float ox0 = (b.min.x - hp.x) / scale + half, ox1 = (b.max.x - hp.x) / scale + half, oy0 = (b.min.z - hp.z) / scale + half, oy1 = (b.max.z - hp.z) / scale + half;
            if (ox0 < -1f || oy0 < -1f || ox1 > width + 1f || oy1 > width + 1f) { why = "tile reaches past its heightmap"; return false; }
            int x0 = Mathf.Max(0, Mathf.FloorToInt(ox0) - 1), x1 = Mathf.Min(width, Mathf.CeilToInt(ox1) + 1);
            int y0 = Mathf.Max(0, Mathf.FloorToInt(oy0) - 1), y1 = Mathf.Min(width, Mathf.CeilToInt(oy1) + 1);
            try
            {
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if (hm.GetHeight(x, y) + hp.y < need) { why = "water above the ground somewhere"; return false; }
            }
            catch { why = "heightmap not built yet"; return false; }
            why = "buried";
            return true;
        }

        // once per distinct tile geometry ([visual]'s proof item): the water mesh's xz size against its heightmap's, so the first leg shows the overhang
        private static readonly HashSet<string> s_sizesSaid = new HashSet<string>();
        private static void SaySize(Bounds b, int width, float scale)
        {
            string key = b.size.x.ToString("0.0") + "x" + b.size.z.ToString("0.0") + "/" + width + "x" + scale.ToString("0.##");
            if (s_sizesSaid.Count >= 8 || !s_sizesSaid.Add(key)) return;
            EasyBakeLog.Say(Verify != null && Verify.Value, $"[WaterHide] tile geometry: water {b.size.x:0.0} x {b.size.z:0.0} m over a heightmap of {width} x {scale:0.##} m = {width * scale:0.0} m (overhang {(b.size.x - width * scale) * 0.5f:0.00} m a side, clamped)");
        }

        // Verify: 4 downward rays on the terrain layer at random points of a would-be-hidden tile; ground below the water + margin = FALSE HIDE
        private static void CheckWithRays(Tile t)
        {
            if (s_terrainMask < 0) s_terrainMask = LayerMask.GetMask("terrain");
            Bounds b = t.Renderer.bounds;
            float level = t.Volume.transform.position.y + t.Volume.m_surfaceOffset;
            for (int i = 0; i < 4; i++)
            {
                var p = new Vector3(UnityEngine.Random.Range(b.min.x, b.max.x), level + 200f, UnityEngine.Random.Range(b.min.z, b.max.z));
                w_raycasts++; s_totalRays++;
                if (Physics.Raycast(p, Vector3.down, out var hit, 400f, s_terrainMask) && hit.point.y < level + WaveSpare)
                {
                    w_falseHides++; s_totalFalse++;
                    EasyBakeLog.Say(true, $"[WaterHide] FALSE HIDE: ground at {hit.point.y:0.00} m below water {level:0.00} m at ({hit.point.x:0}, {hit.point.z:0}) on a tile judged buried");
                }
            }
        }

        // ---------------------------------------------------------------- off / lines
        private static void ShowAll()
        {
            for (int i = 0; i < s_order.Count; i++) { var t = s_order[i]; if (t.Hidden && t.Renderer != null) { t.Renderer.forceRenderingOff = t.PriorForceOff; t.Hidden = false; } }
        }

        private static void ReleaseAll()
        {
            ShowAll();
            // [visual]'s review: every hook leaves the table here (GetAllHeightmaps misses distant LOD and heightmaps already gone from its list, and a
            // surviving entry would come back after off -> on with its forwarder unsubscribed)
            for (int i = s_hookList.Count - 1; i >= 0; i--)
            {
                var h = s_hookList[i];
                if (h.Hm != null) { try { h.Hm.m_clearConnectedWearNTearCache -= h.Fwd; } catch { } s_hooks.Remove(h.Hm); }
            }
            s_hookList.Clear(); s_tiles.Clear(); s_order.Clear();
        }

        private static void Say(string state)
        {
            if (state == s_state) return;
            s_state = state;
            EasyBakeLog.Info("[WaterHide] " + state);
        }

        private static void Census(bool on, bool verify)
        {
            s_nextCensus = Time.unscaledTime + 30f;
            if (verify || EasyBakeLog.DebugOn())
            {
                int buried = 0, hidden = 0;
                for (int i = 0; i < s_order.Count; i++) { if (s_order[i].WouldHide) buried++; if (s_order[i].Hidden) hidden++; }
                EasyBakeLog.Say(verify, $"[WaterHide] 30 s: {s_order.Count} zone water tile(s), {buried} buried, {hidden} hidden now; tests {w_tests} (new {w_byNew}, terrain change {w_byTerrain}, backstop {w_byBackstop}); "
                    + $"decisions: buried {w_whyBuried}, water above the ground {w_whyAbove}, reaches past its heightmap {w_whyPast}, heightmap not built {w_whyNotBuilt}, vetoed {w_vetoed}, other {w_whyOther}; "
                    + $"hides {w_hides}, shows {w_shows}, {s_hookList.Count} heightmap hook(s)" + (verify ? $"; VERIFY: {w_raycasts} ground ray(s), FALSE HIDES {w_falseHides} (session: {s_totalRays} ray(s), {s_totalFalse} false hide(s))" : ""));
            }
            w_tests = w_byNew = w_byTerrain = w_byBackstop = w_hides = w_shows = w_vetoed = w_falseHides = w_raycasts = 0;
            w_whyBuried = w_whyPast = w_whyAbove = w_whyNotBuilt = w_whyOther = 0;
        }
    }
}
