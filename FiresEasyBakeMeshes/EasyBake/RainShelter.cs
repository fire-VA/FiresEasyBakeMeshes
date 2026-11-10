using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    // 1.2.128 ([visual], Fire 10-26: "the fix for rain indoors needs to live in ebm"): vanilla only decides whether rain FX run when the
    // environment CHANGES (EnvMan.UpdateEnvironment, m_psystemsOutsideOnly), so walking into a house during rain keeps it falling
    // through the roof. While the local player is in shelter, the falling-weather particle systems of the current environment
    // (m_currentPSystems and the m_currentEnvObject children) stop emitting; drops already in the air finish their fall, so it fades
    // out. Outside again, the systems EBM stopped play again. Standalone: needs nothing but vanilla.
    public static class ShelterRain
    {
        // [seasons] FTS 0.3.221 FiresShelterWeather: Fire's rain sits under the env's other systems, so everything EXCEPT these is hidden.
        private static readonly string[] Keep = { "fog", "cloud", "mist", "ambient", "dust", "aurora", "lightning", "smoke" };
        // FTS 0.3.222 reads this by name (FiresEasyBakeMeshes.ShelterRain.Active) and stands its own hider down: one owner.
        public static bool Active { get; private set; }
        private static readonly List<ParticleSystem> s_stopped = new List<ParticleSystem>();
        private static readonly List<ParticleSystem> s_scratch = new List<ParticleSystem>();
        private static float s_next, s_nextSweep;
        private static bool s_inside;
        private static bool s_announced;
        private static object s_lastEnvObject, s_lastPSystems;
        private static readonly AccessTools.FieldRef<EnvMan, GameObject[]> s_pSystems = MakeRef<GameObject[]>("m_currentPSystems");   // 1.2.156
        private static readonly AccessTools.FieldRef<EnvMan, GameObject> s_envObject = MakeRef<GameObject>("m_currentEnvObject");     // 1.2.156

        private static AccessTools.FieldRef<EnvMan, T> MakeRef<T>(string field)
        {
            try { return AccessTools.FieldRefAccess<EnvMan, T>(field); }
            catch (System.Exception ex) { EasyBakeLog.Debug($"[EBM] shelter rain hider: EnvMan.{field} not found ({ex.Message})"); return null; }
        }

        // 1.2.169: PluginEnabled off. FTS reads Active and takes its own hider back.
        internal static void HandBack()
        {
            Release();
            s_inside = false;
            s_cache.Clear();
            Active = false;
        }

        internal static void Tick()
        {
            if (!Active) EasyBakeLog.Debug("[EBM] shelter weather hider executing (first tick): hides the current env's falling-weather particles while the local player is InShelter; FTS 0.3.222+ stands its own hider down");
            Active = true;
            if (Time.time < s_next) return;
            s_next = Time.time + 0.25f;
            var player = Player.m_localPlayer;
            var env = EnvMan.instance;
            if (player == null || env == null) { Bail(player == null ? "no local player" : "no EnvMan"); Release(); return; }

            // 1.2.156 ([visual], Fire 10-31 cache rule): the two EnvMan fields are read through FieldRefs made once, not a new
            // Traverse + two field lookups every 0.25 s; same fields, same values
            var psystems = s_pSystems != null ? s_pSystems(env) : null;
            var envObject = s_envObject != null ? s_envObject(env) : null;
            bool envChanged = !ReferenceEquals(psystems, s_lastPSystems) || !ReferenceEquals(envObject, s_lastEnvObject);
            s_lastPSystems = psystems; s_lastEnvObject = envObject;

            if (envChanged) { s_cache.Clear(); s_cacheUntil = 0f; s_said.Clear(); }

            bool inside = player.InShelter();
            Bail(psystems == null && envObject == null ? "the current env has no particle systems or env object (nothing to hide)" : null);
            if (envChanged && s_inside) { Release(); s_inside = false; }   // a new env: vanilla just (re)enabled its own systems
            bool flipped = inside != s_inside;   // 1.2.254
            if (inside != s_inside)
            {
                s_inside = inside;
                if (!inside) { Release(); return; }
            }
            if (!inside) return;
            // 1.2.254 [Optimize] TickGroupEventDriven: a new env or stepping inside sweeps at once; a system that starts late has no event, so 1 Hz catches it (4 Hz when off)
            if (EasyBake.EbmSelfCost.EventDriven)
            {
                if (!envChanged && !flipped && Time.time < s_nextSweep) { EasyBake.EbmSelfCost.Sub(EasyBake.EbmSelfCost.SubRain, false); return; }
                s_nextSweep = Time.time + 1f;
                EasyBake.EbmSelfCost.Sub(EasyBake.EbmSelfCost.SubRain, true);
            }

            // 1.2.178 (Fire 11-07, longhouse 1458,458 in a winter storm: "snow flakes float all through the room"): while inside, every tick, not only on the
            // step in, so a system that starts later (env blend, a child switched on late, another mod) stops too. The list is cached per env object (2 s).
            if (Time.time >= s_cacheUntil)
            {
                s_cacheUntil = Time.time + 2f;
                s_cache.Clear();
                if (psystems != null) foreach (var go in psystems) Collect(go, s_cache);
                Collect(envObject, s_cache);
            }
            int stoppedNow = 0;
            var names = new StringBuilder();
            foreach (var ps in s_cache)
            {
                if (ps == null) continue;
                var emission = ps.emission;
                if (!emission.enabled) { Say(ps, "already off"); continue; }
                emission.enabled = false;
                // Rain lives well under a second and falls out: a soft fade. Snow and wind-blown flakes live for seconds, drift and follow the player,
                // so they are cleared: the same emission-off + Clear() pair vanilla's EnvMan.SetParticleArrayEnabled uses on an instant switch.
                float life = MaxLifetime(ps);
                bool clear = life > ClearAboveSeconds;
                if (clear) ps.Clear(true);
                Say(ps, clear ? $"culled (emission off + cleared, lifetime {life:0.0} s)" : $"culled (emission off, soft fade, lifetime {life:0.0} s)");
                if (s_stoppedSet.Add(ps)) s_stopped.Add(ps);
                stoppedNow++;
                if (names.Length < 400) names.Append(names.Length > 0 ? ", " : "").Append(ps.name);
            }
            if (!s_announced || stoppedNow > 0)
            {
                s_announced = true;
                EasyBakeLog.Debug($"[EBM] shelter weather executing: in shelter, {stoppedNow} falling-weather system(s) stop emitting now ({s_stopped.Count} held): {(names.Length > 0 ? names.ToString() : "none running")}");
            }
        }

        private const float ClearAboveSeconds = 1.5f;
        private static readonly List<ParticleSystem> s_cache = new List<ParticleSystem>();
        private static readonly HashSet<ParticleSystem> s_stoppedSet = new HashSet<ParticleSystem>();
        private static readonly HashSet<int> s_said = new HashSet<int>();
        private static float s_cacheUntil;

        // Debug, once per system per env: what was done to it and why.
        private static void Say(ParticleSystem ps, string what)
        {
            if (ps == null || !s_said.Add(ps.GetInstanceID())) return;
            EasyBakeLog.Debug($"[EBM] shelter weather: {PathOf(ps)} {what}");
        }

        private static string PathOf(ParticleSystem ps) => (ps.transform.parent != null ? ps.transform.parent.name + "/" : "") + ps.name;

        private static float MaxLifetime(ParticleSystem ps)
        {
            var c = ps.main.startLifetime;
            switch (c.mode)
            {
                case ParticleSystemCurveMode.Constant: return c.constant;
                case ParticleSystemCurveMode.TwoConstants: return c.constantMax;
                default: return c.curveMultiplier;
            }
        }

        private static string s_bail;

        // 1.2.129 (Fire: "logging in place to tell WHY"): every bail-out says why, once each time the reason changes.
        private static void Bail(string why)
        {
            if (why == s_bail) return;
            s_bail = why;
            if (why != null) EasyBakeLog.Debug("[EBM] rain shelter idle: " + why);
        }

        // For the shelter probe line: what the hider holds and what the env is drawing right now.
        internal static string Summary()
        {
            var env = EnvMan.instance;
            if (env == null) return "rain FX: no EnvMan";
            var t = Traverse.Create(env);
            s_scratch.Clear();
            var ps = t.Field("m_currentPSystems").GetValue<GameObject[]>();
            if (ps != null) foreach (var go in ps) Collect(go);
            Collect(t.Field("m_currentEnvObject").GetValue<GameObject>());
            var sb = new StringBuilder();
            int playing = 0, off = 0;
            foreach (var p in s_scratch)
            {
                bool on = p.emission.enabled && p.gameObject.activeInHierarchy;
                if (on) playing++; else off++;
                if (sb.Length < 300) sb.Append(sb.Length > 0 ? ", " : "").Append(p.name).Append(on ? " playing" : (s_stopped.Contains(p) ? " stopped by EBM" : " off"));
            }
            return $"rain FX: hider {(s_inside ? "HIDING (in shelter)" : "not hiding")}, {playing} falling system(s) playing, {off} off, EBM holds {s_stopped.Count}" + (s_bail != null ? $", idle: {s_bail}" : "") + (sb.Length > 0 ? $" [{sb}]" : "");
        }

        // 1.2.178: a falling-weather word wins over the keep words, so a wind-snow system named *dust / *cloud or parented under "Ambient" is culled too.
        private static readonly string[] Falling = { "snow", "rain", "sleet", "hail", "ash", "ember", "blizzard", "flake" };

        private static void Collect(GameObject go) => Collect(go, s_scratch);

        private static void Collect(GameObject go, List<ParticleSystem> into)
        {
            if (go == null) return;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))   // 1.2.178: inactive children too, so a child switched on later is held
            {
                string n = (ps.name + "/" + (ps.transform.parent != null ? ps.transform.parent.name : "")).ToLowerInvariant();
                string fall = null, keep = null;
                foreach (var f in Falling) if (n.Contains(f)) { fall = f; break; }
                if (fall == null) foreach (var k in Keep) if (n.Contains(k)) { keep = k; break; }
                if (keep != null) { if (into == s_cache) Say(ps, $"kept (keep word '{keep}')"); continue; }
                into.Add(ps);
            }
        }

        private static void Release()
        {
            if (s_stopped.Count == 0) return;
            int n = 0;
            foreach (var ps in s_stopped)
                if (ps != null) { var emission = ps.emission; emission.enabled = true; n++; }
            s_stopped.Clear();
            s_stoppedSet.Clear();
            s_said.Clear();
            EasyBakeLog.Debug($"[EBM] shelter weather: out of shelter (or env changed), {n} system(s) emitting again");
        }
    }
}
