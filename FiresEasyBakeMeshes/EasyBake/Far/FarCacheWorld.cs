using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// #4 far cache: the per-world folder and its identity ([generator], 11-08). On each world (EbmFarWorldEvents.WorldReady: a server or host
    /// at ZNet.Awake, a client at its peer info) the world-gen fingerprint is sampled on the main thread in small steps (1024
    /// WorldGenerator height + biome samples over the whole world, 128 a frame), then the worker compares identity.txt
    /// (seed, seed name, world-gen version, game version, fingerprint) and wipes that world's folder on a mismatch, so a world-gen mod
    /// change or a game update can never show a stale far view. It also keeps all worlds under "Max MB all worlds" by removing the
    /// least recently used other worlds. Then <see cref="FarCache.Ready"/> turns true and one Info line says what was found.
    /// </summary>
    internal static class FarCacheWorld
    {
        private const int Grid = 32, PerStep = 128;
        private const float Half = 10000f;
        private static bool s_hooked, s_opening;
        private static long s_uid;
        private static int s_openSerial;

        internal static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            EbmFarWorldEvents.WhenWorldReady(uid => { s_uid = uid; Open(); });
            EbmFarWorldEvents.WorldClosed += () => { s_openSerial++; s_opening = false; FarCache.ClearWorld(); };
        }

        /// <summary>Main thread: (re)opens the current world's folder when the cache is on and no open is running.</summary>
        internal static void Open()
        {
            if (FarCache.Ready || s_opening) return;
            if (!FarCache.Enabled) { EasyBakeLog.Info("[FarCache] off (Far Cache / Enabled = false): far data is rebuilt each visit, nothing is written"); return; }
            s_opening = true;
            int serial = ++s_openSerial;
            EbmFarJobs.ScheduleCoroutine(WorkPriority.Low, Fingerprint(serial));
        }

        private static IEnumerator Fingerprint(int serial)
        {
            float until = Time.realtimeSinceStartup + 120f;
            while (WorldGenerator.instance == null || ZNet.World == null)
            {
                if (serial != s_openSerial) yield break;
                if (Time.realtimeSinceStartup > until) { s_opening = false; EasyBakeLog.Info("[FarCache] stood down for this world: no world generator after 120 s, so the world identity cannot be checked"); yield break; }
                yield return null;
            }
            global::World w = ZNet.World;
            var gen = WorldGenerator.instance;
            ulong h = 14695981039346656037UL;
            int n = 0;
            for (int i = 0; i < Grid * Grid; i++)
            {
                if (serial != s_openSerial) yield break;
                float x = -Half + (i % Grid + 0.5f) * (2 * Half / Grid), z = -Half + (i / Grid + 0.5f) * (2 * Half / Grid);
                int height = Mathf.RoundToInt(gen.GetHeight(x, z) * 10f);   // 0.1 m: float noise stays stable at this step
                int biome = (int)gen.GetBiome(x, z);
                h = Mix(Mix(h, (uint)height), (uint)biome);
                if (++n % PerStep == 0) yield return null;
            }
            string worldName = string.IsNullOrEmpty(w.m_name) ? "world" : w.m_name;
            long uid = w.m_uid != 0 ? w.m_uid : s_uid;
            string id = FarCache.Safe(worldName) + "_" + uid.ToString(CultureInfo.InvariantCulture);
            string identity = "format=1\nseed=" + w.m_seed.ToString(CultureInfo.InvariantCulture) + "\nseedName=" + (w.m_seedName ?? "")
                + "\nworldGen=" + w.m_worldGenVersion.ToString(CultureInfo.InvariantCulture) + "\ngame=" + Version.CurrentVersion.ToString()
                + "\nfingerprint=" + h.ToString("x16", CultureInfo.InvariantCulture) + "\n";
            string dir = Path.Combine(Path.Combine(FarCache.Root, "worlds"), id);
            FarCache.Queue(() => Check(serial, dir, id, identity));
        }

        private static ulong Mix(ulong h, uint v)
        {
            for (int b = 0; b < 4; b++) { h ^= (byte)(v >> (b * 8)); h *= 1099511628211UL; }
            return h;
        }

        /// <summary>Worker: identity check, wipe on mismatch, all-worlds cap. Returns the main-thread finish.</summary>
        private static Action Check(int serial, string dir, string id, string identity)
        {
            string state;
            string idFile = Path.Combine(dir, "identity.txt");
            string old = null;
            // 1.2.255 D14: a file that cannot be read this moment says nothing about the world; only a real mismatch wipes it
            try { if (File.Exists(idFile)) old = File.ReadAllText(idFile, Encoding.UTF8); }
            catch (Exception e) { return () => { s_opening = false; EasyBakeLog.Info($"[FarCache] stood down for this world: identity.txt could not be read ({e.Message}); nothing was removed"); }; }
            if (old == identity) state = "identity ok";
            else
            {
                state = old == null ? (Directory.Exists(dir) ? "reset (no identity file)" : "new") : "reset (" + Changed(old, identity) + " changed)";
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception e) { state += ", old files not removed: " + e.Message; }
                try { Directory.CreateDirectory(dir); File.WriteAllText(idFile, identity, Encoding.UTF8); }
                catch (Exception e) { return () => { s_opening = false; EasyBakeLog.Info($"[FarCache] stood down for this world: cannot write {dir} ({e.Message})"); }; }
            }
            try { new DirectoryInfo(dir).LastWriteTimeUtc = DateTime.UtcNow; } catch { }   // the all-worlds LRU stamp
            long worldBytes = FarCache.Count(dir);
            string pruned = PruneWorlds(dir);
            long shared = 0;
            try { foreach (var d in new DirectoryInfo(FarCache.Root).GetDirectories()) if (d.Name != "worlds") shared += FarCache.Count(d.FullName); } catch { }
            int files = 0;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.fc", SearchOption.AllDirectories).Length : 0; } catch { }
            return () =>
            {
                if (serial != s_openSerial) return;
                s_opening = false;
                FarCache.SetWorld(dir, id);
                EasyBakeLog.Info($"[FarCache] on: world {id}, {state}; {files} file(s), {worldBytes >> 20} MB for this world, {shared >> 20} MB shared{pruned}");
            };
        }

        private static string Changed(string a, string b)
        {
            var was = new Dictionary<string, string>();
            foreach (var line in a.Split('\n')) { int e = line.IndexOf('='); if (e > 0) was[line.Substring(0, e)] = line.Substring(e + 1); }
            var diff = new List<string>();
            foreach (var line in b.Split('\n')) { int e = line.IndexOf('='); if (e > 0 && (!was.TryGetValue(line.Substring(0, e), out string v) || v != line.Substring(e + 1))) diff.Add(line.Substring(0, e)); }
            return diff.Count == 0 ? "format" : string.Join(", ", diff.ToArray());
        }

        /// <summary>Worker: removes the least recently visited other worlds while all worlds together are over the cap.</summary>
        private static string PruneWorlds(string current)
        {
            try
            {
                var root = new DirectoryInfo(Path.Combine(FarCache.Root, "worlds"));
                if (!root.Exists) return "";
                var worlds = new List<KeyValuePair<DirectoryInfo, long>>();
                long total = 0;
                foreach (var d in root.GetDirectories()) { long b = FarCache.Count(d.FullName); total += b; worlds.Add(new KeyValuePair<DirectoryInfo, long>(d, b)); }
                long cap = FarCache.AllWorldsCapBytes;
                if (total <= cap) return "";
                worlds.Sort((x, y) => x.Key.LastWriteTimeUtc.CompareTo(y.Key.LastWriteTimeUtc));
                int n = 0;
                foreach (var kv in worlds)
                {
                    if (total <= cap) break;
                    if (string.Equals(kv.Key.FullName.TrimEnd('\\', '/'), current.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                    try { kv.Key.Delete(true); total -= kv.Value; n++; } catch { }
                }
                return n > 0 ? $"; removed {n} least recently visited world(s) to stay under {cap >> 20} MB" : "";
            }
            catch { return ""; }
        }
    }
}
