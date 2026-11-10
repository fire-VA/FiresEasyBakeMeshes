using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// #6 far lights ([generator], Fire 11-08: FarLands-class features; our own code, method from Tools\RESEARCH_FarLands_20261108.md
    /// §2.4 prose only). The night glow of distant fires, torches and lamps beyond where the real light reaches:
    ///  - WHAT: FarLightKinds (fires that burn by vanilla's fuel rule, lamps always; workshops dark).
    ///  - WHERE FROM: ZDOs, never instances. A time-sliced sector scan around the player (0.2 ms a frame), nearest sectors first,
    ///    through vanilla's public ZDOMan.FindSectorObjects. A host has every ZDO; a client has the ones it has received, plus the
    ///    fires it saw in earlier sessions from the far cache (#4, World "far-lights"), whose burning is worked out from the cached fuel
    ///    with the same rule, so a fire left burning goes out on time. No network traffic, no RPCs, no patches.
    ///  - DRAW (v1): one ParticleSystem fed by SetParticles = one draw call, depth-tested, so terrain and buildings hide what is
    ///    behind them. Material = the vanilla campfire's own glow material (found at run time, named in the Info line).
    ///    Per light: a falloff with distance ([Far Lights] Falloff), a minimum on-screen size, the game's live fog transmittance at
    ///    that distance, night only (EnvMan day fraction), faded in beyond the real light's range, a slow flicker that is off when the
    ///    game's "reduce flashing lights" is on. Hidden indoors / in dungeons. At most 512 drawn, the nearest win.
    /// [Far Lights] Enabled defaults OFF (new behaviour ships off until the A/B and Fire's look check).
    /// </summary>
    internal static class FarLights
    {
        private const int MaxDrawn = 512, MaxCached = 4096, CacheFormat = 1;
        private const float SectorSize = 64f, ScanBudgetMs = 0.2f, MinPixels = 2.5f;
        private const string CacheBucket = "far-lights";

        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_falloff, s_maxDistance;
        private static bool s_bound, s_saidOn, s_saidNoMaterial;

        private sealed class Rec
        {
            public FarLightKinds.Kind Kind; public ZDOID Id; public Vector3 Pos;
            public int State = 1; public float Fuel; public long LastTicks;
            public bool Burning, Live; public float Phase; public int Sweep;
        }
        private static readonly Dictionary<ZDOID, Rec> s_recs = new Dictionary<ZDOID, Rec>();

        // scan state
        private static Vector2Int[] s_offsets; private static int s_offsetsRadius = -1;
        private static int s_scanIndex, s_sweep = 1; private static Vector2s s_scanCentre; private static bool s_scanning;
        private static readonly List<ZDO> s_tmp = new List<ZDO>();
        private static readonly HashSet<long> s_nonEmpty = new HashSet<long>();

        // draw state
        private static GameObject s_go; private static ParticleSystem s_ps; private static ParticleSystem.Particle[] s_parts;
        private static readonly List<KeyValuePair<float, Rec>> s_near = new List<KeyValuePair<float, Rec>>();
        private static float s_nextDraw, s_nextBurn, s_nextSave, s_nextLog; private static bool s_dirty, s_cacheAsked;
        private static int s_drawn, s_burningCount, s_cacheLoaded;
        private static long s_scanTicks, s_drawTicks;

        /// <summary>True when the far lights are switched on (live key).</summary>
        public static bool Enabled => s_enabled != null && s_enabled.Value;

        /// <summary>Binds [Far Lights] in the given config (EBM's own Awake calls it; once per process) and registers with FarRunner.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (s_bound || config == null) return;
            s_bound = true;
            s_enabled = config.Bind("Far Lights", "Enabled", false,
                "At night, distant fires, torches and lamps glow beyond where their real light reaches (fires only while they have fuel; "
                + "workshops stay dark). Hidden by fog and by terrain or buildings in front of them.");
            s_falloff = config.Bind("Far Lights", "Falloff", 1f,
                new ConfigDescription("How fast distant glows fade with distance: higher = they fade sooner, lower = they carry further.", new AcceptableValueRange<float>(0.25f, 4f)));
            s_maxDistance = config.Bind("Far Lights", "Max distance", 2500f,
                new ConfigDescription("The furthest a glow is drawn, in metres.", new AcceptableValueRange<float>(300f, 6000f)));
            s_sleepByDay = config.Bind("Far Lights", "Sleep By Day", true,
                "In full daylight (when no distant glow can be drawn) the far-light scan pauses, and resumes ahead of dusk. Same picture, less work by day.");
            s_enabled.SettingChanged += (_, __) => { if (!Enabled) Clear(false); };
            EbmFarWorldEvents.WorldClosed += () => Clear(true);
            FarCache.OnWorldReady += () => s_cacheAsked = false;
            FarRunner.Register("FarLights", Tick, null);
        }

        // ---- per frame (FarRunner, client only) -------------------------------------------------------------------------------

        private static void Tick()
        {
            if (!Enabled) return;
            var player = Player.m_localPlayer;
            if (player == null || ZDOMan.instance == null || ZNet.instance == null || ZoneSystem.instance == null) return;
            if (!FarLightKinds.Build()) return;
            if (s_ps == null && !MakeDrawer()) return;
            if (!s_saidOn)
            {
                s_saidOn = true;
                EasyBakeLog.Info($"[FarLights] on: {FarLightKinds.Count} glow kind(s), material {s_ps.GetComponent<ParticleSystemRenderer>().sharedMaterial.name}, "
                    + $"source {(ZNet.instance.IsServer() ? "host scan (every ZDO)" : "client ZDOs + far cache")}, max {s_maxDistance.Value:0} m, falloff {s_falloff.Value:0.##}");
            }
            if (!s_cacheAsked && FarCache.Ready) { s_cacheAsked = true; LoadCache(); }

            long t0 = Stopwatch.GetTimestamp();
            // 1.2.216 ([ghost], O log: 354-617 ms of scanning per 30 s at darkness 0.00, 0 drawn): with [Far Lights] Sleep By Day the scan and
            // the burn update stop in full daylight (nothing can be drawn: Draw needs darkness > 0) and wake ahead of dusk with a fresh sweep, so
            // the lights are known by the time the first glow can show. Draw still runs at 10 Hz (it clears to 0 by day, as before).
            bool sleep = SleepByDay && FullDay();
            if (sleep != s_asleep)
            {
                s_asleep = sleep;
                if (!sleep) s_scanning = false;   // woke: start a new sweep (anything placed or gone while asleep is found again)
                EasyBakeLog.Debug($"[FarLights] {(sleep ? "asleep for the day (no scan, no burn update)" : "awake for dusk: new sweep")}");
            }
            if (sleep) s_sleptFrames++;
            else Scan(player.transform.position);
            long t1 = Stopwatch.GetTimestamp();
            float now = Time.unscaledTime;
            if (!sleep && now >= s_nextBurn) { s_nextBurn = now + 1f; UpdateBurning(); }
            if (now >= s_nextDraw) { s_nextDraw = now + 0.1f; Draw(player); }
            if (now >= s_nextSave && s_dirty && FarCache.Ready) { s_nextSave = now + 60f; SaveCache(player.transform.position); }
            long t2 = Stopwatch.GetTimestamp();
            s_scanTicks += t1 - t0; s_drawTicks += t2 - t1;
            if (now >= s_nextLog) { s_nextLog = now + 30f; Report(); }
        }

        // ---- scan: nearest sectors first, a little each frame ----------------------------------------------------------------

        private static void Scan(Vector3 at)
        {
            int radius = Mathf.CeilToInt(s_maxDistance.Value / SectorSize) + 1;
            if (radius != s_offsetsRadius) { s_offsets = Spiral(radius); s_offsetsRadius = radius; s_scanning = false; }
            if (!s_scanning)
            {
                s_scanCentre = ZoneSystem.GetZone(at); s_scanIndex = 0; s_sweep++; s_nonEmpty.Clear(); s_scanning = true;
            }
            long budget = (long)(ScanBudgetMs * Stopwatch.Frequency / 1000f), start = Stopwatch.GetTimestamp();
            var zdoMan = ZDOMan.instance;
            var none = new SimulationDistance(0, 0);
            while (s_scanIndex < s_offsets.Length)
            {
                var o = s_offsets[s_scanIndex++];
                var sector = new Vector2s(s_scanCentre.x + o.x, s_scanCentre.y + o.y);
                s_tmp.Clear();
                zdoMan.FindSectorObjects(sector, none, s_tmp, null);
                if (s_tmp.Count > 0) s_nonEmpty.Add(SectorKey(sector.x, sector.y));
                foreach (var zdo in s_tmp)
                {
                    if (zdo == null || !FarLightKinds.TryGet(zdo.GetPrefab(), out var kind)) continue;
                    if (!s_recs.TryGetValue(zdo.m_uid, out var r)) { r = new Rec { Kind = kind, Id = zdo.m_uid, Phase = Hash01(zdo.m_uid) * 6.283f }; s_recs[zdo.m_uid] = r; s_dirty = true; }
                    var p = zdo.GetPosition();
                    if ((p - r.Pos).sqrMagnitude > 0.01f) { r.Pos = p; s_dirty = true; }
                    Snapshot(r, zdo);
                    r.Live = true; r.Sweep = s_sweep;
                }
                if (Stopwatch.GetTimestamp() - start > budget) return;
            }
            EndSweep();
        }

        private static void Snapshot(Rec r, ZDO zdo)
        {
            if (!r.Kind.Fire) return;
            int st = zdo.GetInt(ZDOVars.s_state, 1); float fuel = zdo.GetFloat(ZDOVars.s_fuel); long last = zdo.GetLong(ZDOVars.s_lastTime, 0L);
            if (st != r.State || Math.Abs(fuel - r.Fuel) > 0.01f || last != r.LastTicks) { r.State = st; r.Fuel = fuel; r.LastTicks = last; s_dirty = true; }
        }

        /// <summary>End of a full sweep: a light not seen is gone when its sector is known here (has ZDOs) and its ZDO is gone.</summary>
        private static void EndSweep()
        {
            List<ZDOID> drop = null;
            foreach (var kv in s_recs)
            {
                var r = kv.Value;
                if (r.Sweep == s_sweep) continue;
                var z = ZDOMan.instance.GetZDO(r.Id);
                r.Live = z != null;
                if (z != null) continue;
                var sec = ZoneSystem.GetZone(r.Pos);
                if (Math.Abs(sec.x - s_scanCentre.x) > s_offsetsRadius || Math.Abs(sec.y - s_scanCentre.y) > s_offsetsRadius) continue;   // outside this sweep: keep
                if (s_nonEmpty.Contains(SectorKey(sec.x, sec.y))) (drop ?? (drop = new List<ZDOID>())).Add(kv.Key);   // its sector is known here and it is gone: removed
            }
            if (drop != null) { foreach (var id in drop) s_recs.Remove(id); s_dirty = true; }
            s_scanning = false;
        }

        private static long SectorKey(int x, int y) => ((long)x << 32) | (uint)y;

        private static Vector2Int[] Spiral(int radius)
        {
            var list = new List<Vector2Int>((2 * radius + 1) * (2 * radius + 1));
            for (int x = -radius; x <= radius; x++) for (int y = -radius; y <= radius; y++) if (x * x + y * y <= radius * radius) list.Add(new Vector2Int(x, y));
            list.Sort((a, b) => (a.x * a.x + a.y * a.y).CompareTo(b.x * b.x + b.y * b.y));
            return list.ToArray();
        }

        private static float Hash01(ZDOID id) { uint h = (uint)id.ID * 2654435761u ^ (uint)id.UserID; return (h & 0xFFFF) / 65535f; }

        // ---- burning (1 Hz) ----------------------------------------------------------------------------------------------------

        private static void UpdateBurning()
        {
            var now = ZNet.instance.GetTime();
            int n = 0;
            foreach (var r in s_recs.Values)
            {
                if (r.Live) { var z = ZDOMan.instance.GetZDO(r.Id); if (z != null) { Snapshot(r, z); r.Burning = FarLightKinds.Burning(r.Kind, z, now); } else r.Live = false; }
                if (!r.Live) r.Burning = FarLightKinds.Burning(r.Kind, r.State, r.Fuel, r.LastTicks, now);
                if (r.Burning) n++;
            }
            s_burningCount = n;
        }

        // ---- draw (10 Hz) --------------------------------------------------------------------------------------------------------

        private static bool MakeDrawer()
        {
            var mat = FindGlowMaterial(out string why);
            if (mat == null)
            {
                if (!s_saidNoMaterial) { s_saidNoMaterial = true; EasyBakeLog.Info($"[FarLights] stood down: {why}"); }
                return false;
            }
            s_go = new GameObject("FiresEBM_FarLights") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(s_go);
            s_ps = s_go.AddComponent<ParticleSystem>();
            s_ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = s_ps.main;
            main.loop = false; main.playOnAwake = false; main.maxParticles = MaxDrawn; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 1e5f; main.startSpeed = 0f; main.gravityModifier = 0f; main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var em = s_ps.emission; em.enabled = false;
            var sh = s_ps.shape; sh.enabled = false;
            var pr = s_go.GetComponent<ParticleSystemRenderer>();
            pr.renderMode = ParticleSystemRenderMode.Billboard; pr.sharedMaterial = mat;
            pr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; pr.receiveShadows = false;
            pr.minParticleSize = 0f; pr.maxParticleSize = 0.2f;
            s_parts = new ParticleSystem.Particle[MaxDrawn];
            s_ps.Play();
            return true;
        }

        /// <summary>The vanilla campfire's own glow (a particle material named flare / glow), else its first particle material.</summary>
        private static Material FindGlowMaterial(out string why)
        {
            why = null;
            var fire = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("fire_pit") : null;
            if (fire == null) { why = "no vanilla fire_pit prefab to take the glow material from"; return null; }
            Material first = null;
            foreach (var r in fire.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                var m = r != null ? r.sharedMaterial : null;
                if (m == null) continue;
                if (first == null) first = m;
                string n = m.name.ToLowerInvariant();
                if (n.Contains("flare") || n.Contains("glow")) return m;
            }
            if (first == null) why = "fire_pit has no particle material";
            return first;
        }

        private static void Draw(Player player)
        {
            var cam = Camera.main;
            if (cam == null || s_ps == null) return;
            bool hide = player.InInterior();
            float dark = Darkness();
            int count = 0;
            if (!hide && dark > 0.001f)
            {
                Vector3 eye = cam.transform.position;
                float maxD = s_maxDistance.Value, maxD2 = maxD * maxD, falloff = s_falloff.Value;
                float pixelToMetres = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
                s_near.Clear();
                foreach (var r in s_recs.Values)
                {
                    if (!r.Burning) continue;
                    float d2 = (r.Pos - eye).sqrMagnitude;
                    if (d2 > maxD2 || d2 < r.Kind.Range * r.Kind.Range * 0.36f) continue;
                    s_near.Add(new KeyValuePair<float, Rec>(d2, r));
                }
                if (s_near.Count > MaxDrawn) s_near.Sort((a, b) => a.Key.CompareTo(b.Key));
                bool flicker = !Settings.ReduceFlashingLights;
                float t = Time.time;
                for (int i = 0; i < s_near.Count && count < MaxDrawn; i++)
                {
                    var r = s_near[i].Value; float d = Mathf.Sqrt(s_near[i].Key);
                    float handoff = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(r.Kind.Range * 0.6f, r.Kind.Range * 1.2f, d));
                    float k = d / 800f;
                    float b = dark * handoff * Fog(d) / (1f + falloff * k * k);
                    float size = r.Kind.Size, minSize = d * pixelToMetres * MinPixels;
                    if (minSize > size) { b *= Mathf.Max(0.15f, (size * size) / (minSize * minSize)); size = minSize; }   // fixed on-screen size: dimmer, never gone
                    if (flicker) b *= 1f + 0.08f * Mathf.Sin(t * 2.3f + r.Phase) + 0.04f * Mathf.Sin(t * 5.1f + r.Phase * 1.7f);
                    if (b < 0.01f) continue;
                    var c = r.Kind.Color;
                    s_parts[count++] = new ParticleSystem.Particle
                    {
                        position = r.Pos + Vector3.up * (r.Kind.Fire ? 0.6f : 0.3f),
                        startSize = size, remainingLifetime = 1e5f, startLifetime = 1e5f,
                        startColor = new Color(c.r, c.g, c.b, Mathf.Clamp01(b)),
                    };
                }
            }
            s_ps.SetParticles(s_parts, count);
            s_drawn = count;
        }

        // ---- 1.2.216 sleep by day
        private static ConfigEntry<bool> s_sleepByDay;
        private static bool s_asleep; private static int s_sleptFrames;
        private static bool SleepByDay => s_sleepByDay == null || s_sleepByDay.Value;
        /// <summary>Full daylight with margin: Darkness() is 0 from 0.30 to 0.70; asleep only from 0.31 to 0.62, so the scan wakes about an
        /// hour of game time before the first glow (a full sweep at 0.2 ms a frame finishes long before 0.70). Indoors does not sleep (EnvMan time
        /// still runs, and the player can step out).</summary>
        private static bool FullDay()
        {
            var env = EnvMan.instance;
            if (env == null) return false;
            float f = env.GetDayFraction();
            return f > 0.31f && f < 0.62f;
        }

        /// <summary>1 at night, 0 by day, eased over dusk and dawn (EnvMan's smoothed day fraction: 0.25 sunrise, 0.75 sunset).</summary>
        private static float Darkness()
        {
            var env = EnvMan.instance;
            if (env == null) return 0f;
            float f = env.GetDayFraction();
            float day = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.20f, 0.30f, f)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.70f, 0.80f, f)));
            return 1f - day;
        }

        /// <summary>The game's live fog between the camera and a light at distance d (Unity's own fog formulas).</summary>
        private static float Fog(float d)
        {
            if (!RenderSettings.fog) return 1f;
            switch (RenderSettings.fogMode)
            {
                case FogMode.Linear: { float s = RenderSettings.fogStartDistance, e = RenderSettings.fogEndDistance; return e > s ? Mathf.Clamp01((e - d) / (e - s)) : 1f; }
                case FogMode.Exponential: return Mathf.Exp(-RenderSettings.fogDensity * d);
                default: { float x = RenderSettings.fogDensity * d; return Mathf.Exp(-x * x); }
            }
        }

        // ---- far cache (#4): fires a client saw in earlier sessions -------------------------------------------------------------

        private static void LoadCache()
        {
            FarCache.ReadAsync(FarCacheScope.World, CacheBucket, "lights-v1", data =>
            {
                if (data == null) return;
                int added = 0;
                try
                {
                    using (var br = new BinaryReader(new MemoryStream(data)))
                    {
                        if (br.ReadInt32() != CacheFormat) return;
                        int n = br.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            int prefab = br.ReadInt32(); var pos = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            var id = new ZDOID(br.ReadInt64(), br.ReadUInt32()); int st = br.ReadInt32(); float fuel = br.ReadSingle(); long last = br.ReadInt64();
                            if (!FarLightKinds.TryGet(prefab, out var kind) || s_recs.ContainsKey(id)) continue;
                            s_recs[id] = new Rec { Kind = kind, Id = id, Pos = pos, State = st, Fuel = fuel, LastTicks = last, Live = false, Phase = Hash01(id) * 6.283f };
                            added++;
                        }
                    }
                }
                catch (Exception e) { EasyBakeLog.Debug($"[FarLights] far cache record unreadable ({e.GetType().Name}): ignored, rebuilt from what this client sees"); }
                s_cacheLoaded = added;
                EasyBakeLog.Debug($"[FarLights] far cache: {added} light(s) seen in earlier sessions");
            });
        }

        private static void SaveCache(Vector3 at)
        {
            var list = new List<Rec>(s_recs.Values);
            if (list.Count > MaxCached) list.Sort((a, b) => (a.Pos - at).sqrMagnitude.CompareTo((b.Pos - at).sqrMagnitude));
            int n = Math.Min(list.Count, MaxCached);
            var ms = new MemoryStream(8 + n * 40);
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(CacheFormat); bw.Write(n);
                for (int i = 0; i < n; i++)
                {
                    var r = list[i];
                    bw.Write(r.Kind.Hash); bw.Write(r.Pos.x); bw.Write(r.Pos.y); bw.Write(r.Pos.z);
                    bw.Write(r.Id.UserID); bw.Write(r.Id.ID); bw.Write(r.State); bw.Write(r.Fuel); bw.Write(r.LastTicks);
                }
            }
            FarCache.Write(FarCacheScope.World, CacheBucket, "lights-v1", ms.ToArray());
            s_dirty = false;
        }

        // ---- housekeeping ------------------------------------------------------------------------------------------------------

        private static void Report()
        {
            double ms = 1000.0 / Stopwatch.Frequency;
            if (EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[FarLights] last 30 s: {s_recs.Count} known ({s_cacheLoaded} from the far cache), {s_burningCount} burning, {s_drawn} drawn; "
                    + $"scan {s_scanTicks * ms:0.0} ms, burn+draw {s_drawTicks * ms:0.0} ms; darkness {Darkness():0.00}; sleep by day {(SleepByDay ? "on" : "off")}, asleep {s_sleptFrames} frame(s)");
            s_scanTicks = 0; s_drawTicks = 0; s_sleptFrames = 0;
        }

        private static void Clear(bool worldClosed)
        {
            if (s_ps != null) s_ps.SetParticles(s_parts, 0);
            s_drawn = 0;
            if (!worldClosed) return;
            s_recs.Clear(); s_scanning = false; s_cacheAsked = false; s_cacheLoaded = 0; s_dirty = false;
        }
    }
}
