using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.459 ([visual] + [worldgen], Fire 11-18: "we will need to build the API and the vanilla read path ... Ebm and fat are both ours and can be built
    /// to work together"). Picks where far vegetation reads the world from, on the MAIN thread at every FarVegRunner Start, and fills FarVegReplay's
    /// Src* delegates BEFORE FarVegReplay.Init():
    ///  - FAT installed with FatApi.FarDataApiVersion >= 3 (FAT 0.2.1483+): the FatApi path on EVERY world (FarSampleZone runs the real HeightmapBuilder.Build in
    ///    FAT's thread-safe corner scope; FarSampleSector / FarBiomeAt / FarForestFactor). FarGenInit() must answer null (ready); otherwise far vegetation stands down
    ///    with FAT's reason. Bound by reflection: EBM never references FAT (soft dependency).
    ///  - FAT absent: the VANILLA path through Harmony REVERSE-PATCH snapshots of WorldGenerator's original methods (exact vanilla IL, other mods' patches on the
    ///    outer methods skipped), unless a mod that is not ours patches them (FarVegRunner's gate stands down first).
    ///  - FAT present but older than API 3: stand down (its plain patched calls are not safe from a worker: [worldgen] audit, FatBiomeResolver.cs:271).
    /// Returns null when ready, else the reason (logged by the runner).
    /// </summary>
    internal static class FarVegSource
    {
        internal static string PathName { get; private set; } = "none";
        private static bool s_snapsMade;

        // 1.2.465: FatApi v4 FarVegetationRevision as read at Pick (bumps on the density key's SettingChanged: a cfg edit or the server's synced value).
        private static Type s_countsFat;
        private static int s_countsRev;
        // Main thread (runner, every 10 s): true once when the revision moved since Pick; the runner's Stop / Start then re-reads the counts.
        internal static bool CountsChanged()
        {
            var t = s_countsFat;
            if (t == null) return false;
            int r = ReadInt(t, "FarVegetationRevision");
            if (r == s_countsRev) return false;
            s_countsRev = r;
            return true;
        }

        internal static string Pick()
        {
            FarVegReplay.SrcZone = null; FarVegReplay.SrcBiomeAt = null; FarVegReplay.SrcSector = null; FarVegReplay.SrcForest = null;
            FarVegReplay.SrcBiome = null; FarVegReplay.SrcBiomeHeight = null;
            FarVegKinds.SrcCounts = null; s_countsFat = null;
            PathName = "none";

            Type fat = FindType("VerdantsAscent.FatApi");
            if (fat != null)
            {
                int ver = ReadInt(fat, "FarDataApiVersion");
                if (ver < 3) return $"FAT is installed but its FatApi FarDataApiVersion is {ver} (far vegetation needs 3, FAT 0.2.1483+): FAT's plain world-generator patches are not safe from a background thread";
                var init = fat.GetMethod("FarGenInit", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (init == null) return "FatApi has no FarGenInit()";
                string why = init.Invoke(null, null) as string;
                if (why != null) return "FAT's far world-gen API is not ready: " + why;
                try
                {
                    FarVegReplay.SrcZone = Bind<FarZoneFn>(fat, "FarSampleZone");
                    FarVegReplay.SrcBiomeAt = Bind<FarBiomeAtFn>(fat, "FarBiomeAt");
                    FarVegReplay.SrcSector = Bind<FarSectorFn>(fat, "FarSampleSector");
                    FarVegReplay.SrcForest = Bind<FarForestFn>(fat, "FarForestFactor");
                    // 1.2.465 (lead 11-19 G5e): v4 = the generating peer's effective entry counts (FAT's vegetation density scale). A v4 FAT without
                    // it would put every far tree in the wrong place, so a failed bind stands far vegetation down (caught below). v3: unchanged.
                    if (ver >= 4)
                    {
                        FarVegKinds.SrcCounts = Bind<FarVegKinds.FarCountsFn>(fat, "FarVegetationCounts");
                        s_countsFat = fat; s_countsRev = ReadInt(fat, "FarVegetationRevision");
                    }
                }
                catch (Exception e) { return "FatApi v" + ver + " methods did not bind: " + e.Message; }
                PathName = $"FAT's far world-gen API v{ver} (flags {ReadInt(fat, "FarGenFlags")})";
                return null;
            }

            // FAT absent: vanilla originals through reverse-patch snapshots, bound to the current WorldGenerator.
            var wg = WorldGenerator.instance;
            if (wg == null) return "no world generator yet";
            try
            {
                if (!s_snapsMade) { MakeSnapshots(); s_snapsMade = true; }
            }
            catch (Exception e) { return "vanilla snapshots failed: " + e.GetType().Name + ": " + e.Message; }
            FarVegReplay.SrcSector = (x, z) => Snap_GetBiomeSector(wg, x, z, false);
            FarVegReplay.SrcForest = (x, z) => Snap_GetForestFactor(new Vector3(x, 0f, z));
            FarVegReplay.SrcBiome = (x, z) => Snap_GetBiome(wg, x, z, 0.02f, false);
            FarVegReplay.SrcBiomeHeight = (Heightmap.Biome b, float x, float z, out Color mask) => Snap_GetBiomeHeight(wg, b, x, z, out mask, false, true);
            PathName = "vanilla originals (Harmony reverse-patch snapshots, FAT absent)";
            return null;
        }

        // ---- reverse-patch snapshots: the ORIGINAL IL of these WorldGenerator methods (REF1.0.15 WorldGenerator.cs signatures) ----
        private static void MakeSnapshots()
        {
            var h = new Harmony("com.Fire.FiresEasyBakeMeshes.FarVegSnapshot");
            var t = typeof(WorldGenerator);
            Snap(h, t.GetMethod("GetBiomeSector", new[] { typeof(float), typeof(float), typeof(bool) }), nameof(Snap_GetBiomeSector));
            Snap(h, t.GetMethod("GetForestFactor", new[] { typeof(Vector3) }), nameof(Snap_GetForestFactor));
            Snap(h, t.GetMethod("GetBiome", new[] { typeof(float), typeof(float), typeof(float), typeof(bool) }), nameof(Snap_GetBiome));
            Snap(h, t.GetMethod("GetBiomeHeight", new[] { typeof(Heightmap.Biome), typeof(float), typeof(float), typeof(Color).MakeByRefType(), typeof(bool), typeof(bool) }), nameof(Snap_GetBiomeHeight));
        }

        private static void Snap(Harmony h, MethodInfo original, string stub)
        {
            if (original == null) throw new MissingMethodException("WorldGenerator", stub.Replace("Snap_", ""));
            h.CreateReversePatcher(original, new HarmonyMethod(typeof(FarVegSource).GetMethod(stub, BindingFlags.NonPublic | BindingFlags.Static))).Patch(HarmonyReversePatchType.Original);
        }

        // Stubs replaced by Harmony with the original bodies (instance methods take the instance first).
        private static BiomeSector Snap_GetBiomeSector(WorldGenerator instance, float wx, float wy, bool clamp) => throw new NotImplementedException("reverse patch");
        private static float Snap_GetForestFactor(Vector3 pos) => throw new NotImplementedException("reverse patch");
        private static Heightmap.Biome Snap_GetBiome(WorldGenerator instance, float wx, float wy, float oceanLevel, bool waterAlwaysOcean) => throw new NotImplementedException("reverse patch");
        private static float Snap_GetBiomeHeight(WorldGenerator instance, Heightmap.Biome biome, float wx, float wy, out Color mask, bool preGeneration, bool riverPreDN) => throw new NotImplementedException("reverse patch");

        // ---- reflection helpers (FAT is never referenced at compile time) ----
        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = a.GetType(fullName, false); } catch { }
                if (t != null) return t;
            }
            return null;
        }

        private static int ReadInt(Type t, string name)
        {
            const BindingFlags f = BindingFlags.Public | BindingFlags.Static;
            var p = t.GetProperty(name, f); if (p != null) try { return Convert.ToInt32(p.GetValue(null)); } catch { }
            var fi = t.GetField(name, f); if (fi != null) try { return Convert.ToInt32(fi.GetValue(null)); } catch { }
            return 0;
        }

        private static D Bind<D>(Type t, string name) where D : Delegate
        {
            var invoke = typeof(D).GetMethod("Invoke");
            var types = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
            var m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, types, null);
            if (m == null) throw new MissingMethodException("FatApi", name);
            return (D)Delegate.CreateDelegate(typeof(D), m);
        }
    }
}
