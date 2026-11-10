using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [Optimize] FarZoneGrid ([worldgen] 11-09, round 2 row E19 "rectangle coverage grid"; our own method). Default off, read live.
    // The far tier asks "is this zone loaded" for every digest zone in Plan, for every zone of every drawn target in the hand-off tick (a 512 m cell
    // target is up to 64 zones), and 625 times in the impostor mask tick. Each ask is vanilla's ZoneSystem.IsZoneLoaded: two dictionary lookups.
    // With the key on, the loaded set is read ONCE a frame, the first time it is asked, from the same two dictionaries IsZoneLoaded reads
    // (m_zones and m_loadingObjectsInZones), into a small box (the loaded zones' bounding rectangle) plus a summed-area table over it. Then:
    //   - Loaded(z) is one array read (same answer as IsZoneLoaded this frame: same data, same rule);
    //   - AnyLoaded(x0, y0, x1, y1) answers "is any zone in this rectangle loaded" in four reads.
    // The rectangle answer is only ever used as an exact "none" (a target's zones all lie inside its rectangle, so a zero count proves none of them is
    // loaded); a non-zero count falls back to the per-zone check, so every caller gets exactly the answer it got before.
    // Built at most once a frame; a frame that never asks never builds. If vanilla's fields cannot be read, the key does nothing (logged once).
    internal static class FarZoneGrid
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.FarZoneGrid != null && FiresEasyBakeMeshesPlugin.FarZoneGrid.Value && s_fieldsOk;

        // m_zones is Dictionary<Vector2s, ZoneSystem.ZoneData> with a PRIVATE value type: read through a generic helper bound once to that type (keys copied, no boxing)
        private static readonly System.Reflection.FieldInfo s_zonesField = HarmonyLib.AccessTools.Field(typeof(ZoneSystem), "m_zones");
        private static readonly System.Action<object, List<Vector2s>> s_collect = MakeCollect();
        private static readonly HarmonyLib.AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> s_loading = MakeLoadingRef();
        private static bool s_fieldsOk = s_zonesField != null && s_collect != null && s_loading != null;
        private static readonly List<Vector2s> s_keys = new List<Vector2s>(256);

        private static System.Action<object, List<Vector2s>> MakeCollect()
        {
            try
            {
                var ft = s_zonesField != null ? s_zonesField.FieldType : null;
                if (ft == null || !ft.IsGenericType || ft.GetGenericArguments()[0] != typeof(Vector2s)) { Unavailable("m_zones is not a Dictionary<Vector2s, ...>"); return null; }
                var m = typeof(FarZoneGrid).GetMethod(nameof(Collect), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MakeGenericMethod(ft.GetGenericArguments()[1]);
                return (System.Action<object, List<Vector2s>>)System.Delegate.CreateDelegate(typeof(System.Action<object, List<Vector2s>>), m);
            }
            catch (System.Exception e) { Unavailable(e.Message); return null; }
        }

        private static void Collect<T>(object dict, List<Vector2s> into) { foreach (var k in ((Dictionary<Vector2s, T>)dict).Keys) into.Add(k); }

        private static HarmonyLib.AccessTools.FieldRef<ZoneSystem, Dictionary<Vector2s, List<ZDO>>> MakeLoadingRef()
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<ZoneSystem, Dictionary<Vector2s, List<ZDO>>>("m_loadingObjectsInZones"); }
            catch (System.Exception e) { Unavailable(e.Message); return null; }
        }

        private static void Unavailable(string why) => EasyBakeLog.Info($"{FarBuildings.Log} FarZoneGrid unavailable ({why}): every zone is asked as before");

        private static int s_frame = -1, s_x0, s_y0, s_w, s_h, s_loadedCount;
        private static bool[] s_cells = new bool[0];
        private static int[] s_sat = new int[0];   // (w + 1) x (h + 1), s_sat[(y + 1) * (w + 1) + (x + 1)] = loaded zones in [x0..x0+x] x [y0..y0+y]
        internal static long Builds, PointReads, RectReads, RectNone, RectFallbacks;

        // 1257 review ([visual] item 1): a snapshot taken earlier in the frame could miss a zone that loads later in the SAME frame. Every ask now compares the two
        // dictionaries' counts with the snapshot's (two reads, no allocation) and re-snapshots on any difference, so a load, an unload or a loading zone finishing
        // is seen by the very next ask. The one case counts cannot see is an add and a remove of the same count between two asks in one frame (INFERRED: rare).
        private static int s_zonesN = -1, s_loadingN = -1;
        internal static long Rebuilds;

        private static void Build()
        {
            int frame = Time.frameCount;
            var zs = ZoneSystem.instance;
            object zones = zs != null ? s_zonesField.GetValue(zs) : null;
            var loading = zs != null ? s_loading(zs) : null;
            int zonesN = zones is System.Collections.ICollection zc ? zc.Count : 0, loadingN = loading != null ? loading.Count : 0;
            if (frame == s_frame && zonesN == s_zonesN && loadingN == s_loadingN) return;
            if (frame == s_frame) Rebuilds++;   // same frame, the loaded set moved since the last snapshot
            s_frame = frame; s_zonesN = zonesN; s_loadingN = loadingN; Builds++;
            s_loadedCount = 0; s_w = s_h = 0;
            if (zs == null) return;
            s_keys.Clear();
            if (zones != null) s_collect(zones, s_keys);
            if (s_keys.Count == 0) return;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            foreach (var z in s_keys)
            {
                if (loading != null && loading.ContainsKey(z)) continue;   // IsZoneLoaded's second half
                if (z.x < minX) minX = z.x; if (z.x > maxX) maxX = z.x;
                if (z.y < minY) minY = z.y; if (z.y > maxY) maxY = z.y;
                s_loadedCount++;
            }
            if (s_loadedCount == 0) return;
            s_x0 = minX; s_y0 = minY; s_w = maxX - minX + 1; s_h = maxY - minY + 1;
            int n = s_w * s_h, ns = (s_w + 1) * (s_h + 1);
            if (s_cells.Length < n) s_cells = new bool[Mathf.NextPowerOfTwo(n)];
            if (s_sat.Length < ns) s_sat = new int[Mathf.NextPowerOfTwo(ns)];
            System.Array.Clear(s_cells, 0, n);
            foreach (var z in s_keys)
                if (loading == null || !loading.ContainsKey(z)) s_cells[(z.y - s_y0) * s_w + (z.x - s_x0)] = true;
            int stride = s_w + 1;
            System.Array.Clear(s_sat, 0, stride);
            for (int y = 0; y < s_h; y++)
            {
                int row = 0, o = (y + 1) * stride;
                s_sat[o] = 0;
                for (int x = 0; x < s_w; x++)
                {
                    if (s_cells[y * s_w + x]) row++;
                    s_sat[o + x + 1] = s_sat[o - stride + x + 1] + row;
                }
            }
        }

        /// <summary>The same answer as ZoneSystem.instance.IsZoneLoaded(z) this frame (caller checks On first).</summary>
        internal static bool Loaded(Vector2s z)
        {
            Build(); PointReads++;
            int x = z.x - s_x0, y = z.y - s_y0;
            return s_loadedCount > 0 && x >= 0 && y >= 0 && x < s_w && y < s_h && s_cells[y * s_w + x];
        }

        /// <summary>Loaded zones in the inclusive zone rectangle [x0..x1] x [y0..y1] this frame.</summary>
        internal static int CountLoaded(int x0, int y0, int x1, int y1)
        {
            Build(); RectReads++;
            if (s_loadedCount == 0) return 0;
            int ax = Mathf.Max(x0, s_x0) - s_x0, ay = Mathf.Max(y0, s_y0) - s_y0;
            int bx = Mathf.Min(x1, s_x0 + s_w - 1) - s_x0, by = Mathf.Min(y1, s_y0 + s_h - 1) - s_y0;
            if (ax > bx || ay > by) return 0;
            int stride = s_w + 1;
            return s_sat[(by + 1) * stride + bx + 1] - s_sat[ay * stride + bx + 1] - s_sat[(by + 1) * stride + ax] + s_sat[ay * stride + ax];
        }

        /// <summary>Any of these zones loaded: an exact "none" from the rectangle they lie in, else the per-zone answer (identical to the old loop).</summary>
        internal static bool AnyLoaded(List<Vector2s> zones)
        {
            if (zones.Count == 0) return false;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int i = 0; i < zones.Count; i++) { var z = zones[i]; if (z.x < x0) x0 = z.x; if (z.x > x1) x1 = z.x; if (z.y < y0) y0 = z.y; if (z.y > y1) y1 = z.y; }
            if (CountLoaded(x0, y0, x1, y1) == 0) { RectNone++; return false; }
            RectFallbacks++;
            for (int i = 0; i < zones.Count; i++) if (Loaded(zones[i])) return true;
            return false;
        }

        private static float s_nextLog;
        internal static void MaybeLog()
        {
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 30f;
            if (Builds == 0) return;
            if (EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"{FarBuildings.Log} FarZoneGrid 30 s: {Builds} build(s) ({Rebuilds} of them a same-frame re-snapshot after the loaded set moved, {s_loadedCount} loaded zone(s) in a {s_w}x{s_h} box now), {PointReads} zone read(s) instead of IsZoneLoaded, {RectReads} rectangle read(s): {RectNone} answered 'none' in four reads, {RectFallbacks} fell back to the per-zone check (same answer either way)");
            Builds = Rebuilds = PointReads = RectReads = RectNone = RectFallbacks = 0;
        }
    }
}
