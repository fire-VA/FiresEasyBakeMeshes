using System;
using System.IO;

namespace FiresEasyBakeMeshes
{
    // Fire 10-14: no caches, bundles or textures under BepInExnfig (modpack config profiles stay small). Every Fires mod keeps
    // its caches under <BepInEx>che<Mod> (the same root as FAP; inside the game/profile folder per Fire's 2026-09-30 rule).
    // First run with an old config folder: copy it to <new>.partial, verify file count + bytes, rename into place, then MOVE the
    // old folder out of config to <BepInEx>che<Mod>_migrated_from_config_<yyyyMMdd><name> as the backup. Nothing is
    // deleted. Any failure: nothing moves and this session keeps the old path.
    internal static class FiresCachePaths
    {
        private static readonly System.Collections.Generic.Dictionary<string, string> s_redirect = new System.Collections.Generic.Dictionary<string, string>();

        internal static string Root
        {
            // <BepInEx>che (FAP's root, VAFapCachePaths): inside the game/profile folder (Fire 2026-09-30: only our private mods
            // ITTO/FDT reach outside the Valheim folder), outside config/plugins (what a modpack export sweeps). A dedi uses its own.
            get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "cache"); }
        }

        // <Root>\<mod>[\sub...]
        internal static string For(string mod, params string[] sub)
        {
            string p = Path.Combine(Root, mod);
            foreach (var s in sub) p = Path.Combine(p, s);
            // a folder whose move failed this session keeps being read from (and written to) its old place
            foreach (var kv in s_redirect)
                if (p.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value + p.Substring(kv.Key.Length);
            return p;
        }

        // Moves oldDir's content to newDir once. Returns the directory to use this session.
        internal static string Migrate(string oldDir, string newDir, Action<string> info, Action<string> warn)
        {
            try
            {
                if (!Directory.Exists(oldDir)) return newDir;
                if (Directory.Exists(newDir) && Directory.GetFileSystemEntries(newDir).Length > 0)
                {
                    warn?.Invoke($"[FiresCache] both {oldDir} and {newDir} exist; using the new one, the old folder is left as is (move it out of config by hand)");
                    return newDir;
                }
                // copy into <new>.partial first, so a failed copy never looks like a finished one on the next run
                string tmp = newDir + ".partial";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);   // our own leftover from a failed run, never user data
                long bytes = 0; int files = 0;
                foreach (var f in Directory.GetFiles(oldDir, "*", SearchOption.AllDirectories))
                {
                    string rel = f.Substring(oldDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string dst = Path.Combine(tmp, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(f, dst, true);
                    long len = new FileInfo(f).Length;
                    if (new FileInfo(dst).Length != len) throw new IOException($"copy size mismatch on {rel}");
                    bytes += len; files++;
                }
                Directory.CreateDirectory(tmp);
                int got = Directory.GetFiles(tmp, "*", SearchOption.AllDirectories).Length;
                if (got < files) throw new IOException($"copied {got} of {files} file(s)");
                if (Directory.Exists(newDir)) Directory.Delete(newDir);   // empty (checked above)
                Directory.CreateDirectory(Path.GetDirectoryName(newDir));
                Directory.Move(tmp, newDir);
                string modRoot = Path.Combine(Root, newDir.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]);
                string backup = Path.Combine(modRoot, "_migrated_from_config_" + DateTime.Now.ToString("yyyyMMdd"), new DirectoryInfo(oldDir).Name);
                if (Directory.Exists(backup)) backup += "_" + DateTime.Now.ToString("HHmmss");
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                try { Directory.Move(oldDir, backup); }
                catch (Exception ex)
                {
                    warn?.Invoke($"[FiresCache] copied {files} file(s) to {newDir} but could not move {oldDir} out of config ({ex.Message}); using the new folder, delete the old one by hand");
                    return newDir;
                }
                info?.Invoke($"[FiresCache] moved {files} file(s), {bytes / 1048576.0:0.0} MB, from {oldDir} to {newDir}; the old folder is backed up at {backup}");
                return newDir;
            }
            catch (Exception ex)
            {
                warn?.Invoke($"[FiresCache] migration of {oldDir} failed ({ex.Message}); keeping the old path this session, nothing moved");
                s_redirect[newDir] = oldDir;
                return oldDir;
            }
        }
    }
}
