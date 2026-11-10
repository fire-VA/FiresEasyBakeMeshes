using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// fires_impostors (dev / admin, cheat): proof of the impostor engine without an owner feeding it (lead 11-08 option (b)).
    ///   fires_impostors status            one status line
    ///   fires_impostors on | off | cfg    session override of [Far Impostors] Enabled (never saved); cfg = back to the config value
    ///   fires_impostors test [radius] [per hectare]   registers vanilla trees and fills 512 m cells around you, biome by biome (built on a worker)
    ///   fires_impostors clear             removes the test trees
    /// Lines start "[FarImpostors] test:" (Info: it is a one-time command action).
    /// </summary>
    // EBM port: Core's [FiresOwnedFeature(FarImpostors)] (patch only when an owner is installed) is gone: EBM owns the feature, so EBM's ApplyPatches patches this class like its others.
    [HarmonyPatch(typeof(Terminal), "InitTerminal")]
    internal static class ImpostorTestCommand
    {
        private const string Owner = "ebm.test";   // EBM port: was "core.test"
        private static bool s_done;

        // vanilla trees per biome (names from ZNetScene; a missing one is skipped with a line)
        private static readonly Dictionary<Heightmap.Biome, string[]> Trees = new Dictionary<Heightmap.Biome, string[]>
        {
            { Heightmap.Biome.Meadows, new[] { "Beech1", "Oak1", "Birch1" } },
            { Heightmap.Biome.BlackForest, new[] { "FirTree", "Pinetree_01" } },
            { Heightmap.Biome.Swamp, new[] { "SwampTree1" } },
            { Heightmap.Biome.Mountain, new[] { "FirTree" } },
            { Heightmap.Biome.Plains, new[] { "Birch1" } },
        };

        private static void Postfix()
        {
            if (s_done) return;
            s_done = true;
            new Terminal.ConsoleCommand("fires_impostors",
                "[FiresEasyBakeMeshes] fires_impostors status|on|off|cfg|test [radius m] [trees per hectare]|clear - DEV: the far impostor engine proof.",
                args =>
                {
                    string sub = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "status";
                    string line;
                    switch (sub)
                    {
                        case "on": ImpostorEngine.SessionOverride(true); line = "session override ON (not saved)"; break;
                        case "off": ImpostorEngine.SessionOverride(false); line = "session override OFF"; break;
                        case "cfg": ImpostorEngine.SessionOverride(null); line = "back to the config value"; break;
                        case "clear": ImpostorEngine.ClearOwner(Owner); line = "test trees removed"; break;
                        case "test":
                            float radius = Arg(args, 2, 2000f), perHa = Arg(args, 3, 12f);
                            line = Test(Mathf.Clamp(radius, 256f, 6000f), Mathf.Clamp(perHa, 0.1f, 40f));
                            break;
                        default: line = ImpostorEngine.Status(); break;
                    }
                    args.Context?.AddString("[FarImpostors] " + line);
                    EasyBakeLog.Info(ImpostorEngine.Tag + "test: " + line);
                }, isCheat: true);
        }

        private static float Arg(Terminal.ConsoleEventArgs a, int i, float def) =>
            a.Args.Length > i && float.TryParse(a.Args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : def;

        private static string Test(float radius, float perHa)
        {
            var p = Player.m_localPlayer; var wg = WorldGenerator.instance; var scene = ZNetScene.instance;
            if (p == null || wg == null || scene == null) return "test: no player / world";
            if (!ImpostorEngine.Enabled) return "test: the engine is off ([Far Impostors] Enabled, or 'fires_impostors on' for this session)";
            var kinds = new Dictionary<Heightmap.Biome, int[]>(); var missing = new List<string>(); int registered = 0;
            foreach (var kv in Trees)
            {
                var ids = new List<int>();
                foreach (var name in kv.Value)
                {
                    var prefab = scene.GetPrefab(name);
                    if (prefab == null) { missing.Add(name); continue; }
                    int id = ImpostorEngine.RegisterKind(Owner, prefab, KindOptions.Default);
                    if (id >= 0) { ids.Add(id); registered++; }
                }
                if (ids.Count > 0) kinds[kv.Key] = ids.ToArray();
            }
            if (kinds.Count == 0) return "test: no tree kind could be registered" + (missing.Count > 0 ? " (missing: " + string.Join(", ", missing) + ")" : "");
            Vector3 c = p.transform.position;
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            int cellsSide = Mathf.CeilToInt(radius * 2f / 512f);
            int cx0 = Mathf.FloorToInt((c.x - radius) / 512f), cz0 = Mathf.FloorToInt((c.z - radius) / 512f);
            float spacing = Mathf.Sqrt(10000f / perHa);
            // build every cell on a worker, hand each to the engine on the main thread (SetCell is main-thread only)
            var built = new List<(long key, Bounds b, ImpostorInstance[] data)>();
            var task = Task.Run(() =>
            {
                var rnd = new System.Random(1234);
                for (int i = 0; i < cellsSide; i++)
                    for (int j = 0; j < cellsSide; j++)
                    {
                        int cx = cx0 + i, cz = cz0 + j;
                        var list = new List<ImpostorInstance>();
                        for (float x = cx * 512f; x < cx * 512f + 512f; x += spacing)
                            for (float z = cz * 512f; z < cz * 512f + 512f; z += spacing)
                            {
                                float wx = x + (float)rnd.NextDouble() * spacing, wz = z + (float)rnd.NextDouble() * spacing;
                                if ((new Vector2(wx - c.x, wz - c.z)).sqrMagnitude > radius * radius) continue;
                                var biome = wg.GetBiome(wx, wz);
                                if (!kinds.TryGetValue(biome, out var ids)) continue;
                                float h = wg.GetHeight(wx, wz);
                                if (h < water + 1f) continue;
                                int kind = ids[rnd.Next(ids.Length)];
                                list.Add(new ImpostorInstance(new Vector3(wx, h - 0.2f, wz), 0.8f + (float)rnd.NextDouble() * 0.6f, (float)(rnd.NextDouble() * Math.PI * 2), kind,
                                    ((float)rnd.NextDouble() - 0.5f) * 0.08f, ((float)rnd.NextDouble() - 0.5f) * 0.08f));
                            }
                        if (list.Count > 0)
                            lock (built) built.Add((((long)cx << 32) | (uint)cz, new Bounds(new Vector3(cx * 512f + 256f, c.y, cz * 512f + 256f), new Vector3(512f, 600f, 512f)), list.ToArray()));
                    }
            });
            task.Wait();   // dev command: a short one-time wait is fine (worker work, main thread blocked once)
            int total = 0;
            foreach (var (key, b, data) in built) { ImpostorEngine.SetCell(Owner, key, b, data); total += data.Length; }
            return $"test: {registered} tree kind(s){(missing.Count > 0 ? " (missing: " + string.Join(", ", missing) + ")" : "")}, {total} trees in {built.Count} cell(s) of 512 m within {radius:0} m ({perHa:0.#} a hectare); bakes start now";
        }
    }
}
