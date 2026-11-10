using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// #6 far lights: which prefabs glow at night ([generator], 11-08). Built once from ZNetScene's prefabs:
    ///  - a fire = a prefab with a Fireplace and no CraftingStation / CookingStation / Smelter / Fermenter (workshops stay dark),
    ///    lit only while vanilla's own rule says it burns (state on, fuel left after the time since its last update, or infinite fuel);
    ///  - a lamp = a Piece with a Light whose root has nothing but Piece / ZNetView / WearNTear / HoverText (always lit).
    /// Colour and size come from the prefab's own Light (colour, range), so a far glow matches the near one.
    /// </summary>
    internal static class FarLightKinds
    {
        internal sealed class Kind
        {
            public string Name; public int Hash; public bool Fire, Infinite;
            public float SecPerFuel, Range, Size; public Color Color;
        }

        private static readonly Dictionary<int, Kind> s_kinds = new Dictionary<int, Kind>();
        private static bool s_built;
        private static readonly HashSet<Type> s_lampRoot = new HashSet<Type> { typeof(Piece), typeof(ZNetView), typeof(WearNTear), typeof(HoverText) };

        internal static int Count => s_kinds.Count;
        internal static bool TryGet(int prefabHash, out Kind k) => s_kinds.TryGetValue(prefabHash, out k);

        /// <summary>Main thread, once ZNetScene exists. Returns false (try again later) while it doesn't.</summary>
        internal static bool Build()
        {
            if (s_built) return true;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null || scene.m_prefabs.Count == 0) return false;
            int fires = 0, lamps = 0, workshops = 0;
            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                try
                {
                    var light = go.GetComponentInChildren<Light>(true);
                    var fp = go.GetComponent<Fireplace>();
                    Kind k = null;
                    if (fp != null)
                    {
                        if (go.GetComponent<CraftingStation>() || go.GetComponent<CookingStation>() || go.GetComponent<Smelter>() || go.GetComponent<Fermenter>()) { workshops++; continue; }
                        k = new Kind { Fire = true, Infinite = fp.m_infiniteFuel, SecPerFuel = fp.m_secPerFuel };
                        fires++;
                    }
                    else if (light != null && go.GetComponent<Piece>() != null && LampRoot(go))
                    {
                        k = new Kind { Fire = false, Infinite = true };
                        lamps++;
                    }
                    if (k == null) continue;
                    k.Name = go.name; k.Hash = go.name.GetStableHashCode();
                    k.Range = light != null ? Mathf.Max(2f, light.range) : 8f;
                    k.Color = light != null ? light.color : new Color(1f, 0.62f, 0.3f);
                    k.Size = Mathf.Clamp(k.Range * 0.18f, 0.6f, 5f);   // glow core in metres: torch ~1.5 m, bonfire ~4 m
                    s_kinds[k.Hash] = k;
                }
                catch (Exception e) { EasyBakeLog.Debug($"[FarLights] prefab {go.name} skipped: {e.GetType().Name}: {e.Message}"); }
            }
            s_built = true;
            EasyBakeLog.Debug($"[FarLights] glow kinds: {fires} fire(s), {lamps} lamp(s); {workshops} workshop fire(s) left dark");
            return true;
        }

        private static bool LampRoot(GameObject go)
        {
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb != null && !s_lampRoot.Contains(mb.GetType())) return false;
            return true;
        }

        /// <summary>Vanilla's burning rule (Fireplace.IsBurning + the fuel spent since lastTime), read from the ZDO only.</summary>
        internal static bool Burning(Kind k, ZDO zdo, DateTime now)
        {
            if (!k.Fire) return true;
            if (zdo.GetInt(ZDOVars.s_state, 1) != 1) return false;
            if (k.Infinite) return true;
            float fuel = zdo.GetFloat(ZDOVars.s_fuel);
            if (fuel <= 0f) return false;
            if (k.SecPerFuel <= 0f) return true;
            long last = zdo.GetLong(ZDOVars.s_lastTime, now.Ticks);
            double since = Math.Max(0.0, (now - new DateTime(last)).TotalSeconds);
            return fuel - since / k.SecPerFuel > 0.0;
        }

        /// <summary>The same rule from a cached snapshot (a fire seen earlier whose ZDO this client no longer has).</summary>
        internal static bool Burning(Kind k, int state, float fuel, long lastTicks, DateTime now)
        {
            if (!k.Fire) return true;
            if (state != 1) return false;
            if (k.Infinite) return true;
            if (fuel <= 0f) return false;
            if (k.SecPerFuel <= 0f || lastTicks == 0) return true;
            double since = Math.Max(0.0, (now - new DateTime(lastTicks)).TotalSeconds);
            return fuel - since / k.SecPerFuel > 0.0;
        }
    }
}
