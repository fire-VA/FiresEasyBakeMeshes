using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using BepInEx.Configuration;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>Where a far-cache entry lives: shared by every world on this install, or kept per world.</summary>
    internal enum FarCacheScope { Global, World }

    /// <summary>
    /// #4 per-world far cache ([generator], Fire 11-08: "FarLands-class features ... get it done"; API names agreed with [visual]).
    /// One disk store for far data so the far view shows at once on a later visit: impostor atlases (#1, Global), far forest cells
    /// (#2, World), far terrain (#3), far building digests and meshes (#5). Our own design: one file per key under
    /// <c>BepInEx/cache/FiresEasyBakeMeshes/Far/&lt;bucket&gt;/</c> (Global) or <c>.../Far/worlds/&lt;name&gt;_&lt;uid&gt;/&lt;bucket&gt;/</c> (World).
    ///  - Each file: magic, raw length, CRC32 of the raw bytes, then Deflate. A bad file is a miss and is deleted.
    ///  - Writes are queued to one below-normal worker thread and land atomically (temp file + move). A read of a key whose write is
    ///    still queued answers from the queue, so a write followed by a read never misses.
    ///  - Versioning is in the key: callers fold their look revision / sharpness / piece fingerprint into <see cref="Key"/>, so a new
    ///    revision is a new key and the old one ages out under the size caps (least recently used first).
    ///  - A world's folder carries identity.txt (seed, world-gen version, game version, a world-gen fingerprint). A mismatch (a
    ///    world-gen mod changed, the game updated) wipes that world's folder before anything reads it.
    ///  - Low RAM: nothing is held after a read except the bytes handed to the caller; only file sizes are counted.
    ///  - [Far Cache] Enabled (default OFF): while off TryRead answers false, ReadAsync answers null and Write drops, so every caller
    ///    simply rebuilds. Nothing touches disk.
    /// </summary>
    internal static class FarCache
    {
        public const int ApiVersion = 1;

        private const uint FileMagic = 0x314B4346;   // "FCK1"
        private const string Ext = ".fc";
        private static ConfigEntry<bool> s_enabled, s_skipUnchangedSaves;
        private static long s_skippedUnchanged;
        internal static bool SkipUnchangedSaves => s_skipUnchangedSaves == null || s_skipUnchangedSaves.Value;
        private static bool s_saidSkipUnchanged;
        internal static void NoteUnchangedSaveSkipped()
        {
            Interlocked.Increment(ref s_skippedUnchanged);
            if (!s_saidSkipUnchanged) { s_saidSkipUnchanged = true; EasyBakeLog.Debug("[FarCache] SkipUnchangedSaves: far zones just read from the cache are not written back"); }
        }
        private static ConfigEntry<int> s_worldMb, s_allWorldsMb, s_globalMb;
        private static bool s_bound;

        private static readonly object s_lock = new object();
        private static readonly Dictionary<string, byte[]> s_pending = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> s_dirBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static EbmFarWorker<Job, Done> s_worker;
        private static string s_worldDir;   // null until the world identity is checked
        private static int s_session;

        // stats for the 30 s Debug line (Interlocked: reads come from any thread)
        private static long s_reads, s_hits, s_writes, s_bytesWritten, s_bytesRead, s_evicted, s_bad, s_workerTicks, s_worstReadTicks, s_evictLists;
        // 1.2.255 (D13): bytes in each scope, kept by every write and delete, so the folder is listed once and then only when over its cap. -1 = not counted yet. Under s_lock.
        private static long s_worldBytes = -1L, s_globalBytes = -1L;
        private static float s_nextStats;

        /// <summary>True once the current world's identity is checked; World-scope calls miss until then.</summary>
        public static bool Ready => s_worldDir != null;

        /// <summary>The current world's folder name (name_uid), or null before <see cref="Ready"/>.</summary>
        public static string WorldId { get; private set; }

        /// <summary>Raised on the main thread when <see cref="Ready"/> turns true for a world.</summary>
        public static event Action OnWorldReady;

        /// <summary>True when the cache is switched on (live key).</summary>
        public static bool Enabled => s_enabled != null && s_enabled.Value;

        internal static string Root => FiresCachePaths.For("FiresEasyBakeMeshes", "Far");   // EBM port: was Core's own folder under BepInEx/cache; same root (BepInEx/cache), EBM's folder

        /// <summary>Binds [Far Cache] in the given config (EBM's own Awake calls it; once per process) and starts the world hook.</summary>
        internal static void Initialize(ConfigFile config)
        {
            if (s_bound || config == null) return;
            s_bound = true;
            s_enabled = config.Bind("Far Cache", "Enabled", false,
                "Keeps far-view data on disk (impostor pictures, far forest, far buildings) so distant land appears at once when you come back. "
                + "Off: everything far is rebuilt each visit and nothing is written. Stored under BepInEx/cache/FiresEasyBakeMeshes/Far.");
            s_skipUnchangedSaves = config.Bind("Far Cache", "SkipUnchangedSaves", true, "A far zone just read from the cache is not written straight back. A zone that really changed is still saved. No visible effect; off = written back as before.");
            s_worldMb = config.Bind("Far Cache", "Max MB per world", 256,
                new ConfigDescription("Disk space one world's far data may use; the least recently used is removed first.", new AcceptableValueRange<int>(16, 4096)));
            s_allWorldsMb = config.Bind("Far Cache", "Max MB all worlds", 1024,
                new ConfigDescription("Disk space all worlds' far data may use together; the least recently visited world is removed first.", new AcceptableValueRange<int>(64, 16384)));
            s_globalMb = config.Bind("Far Cache", "Max MB shared", 512,
                new ConfigDescription("Disk space for far data shared by every world (impostor pictures).", new AcceptableValueRange<int>(32, 8192)));
            FarCacheWorld.Hook();
            s_enabled.SettingChanged += (_, __) => { if (Enabled && EbmFarWorldEvents.IsWorldReady) FarCacheWorld.Open(); };   // turned on mid-world: open now
            FarRunner.Register("FarCache", null, StatsTick, serverToo: true);
        }

        // ---- public API ------------------------------------------------------------------------------------------------------

        /// <summary>A stable 16-hex-digit key from the parts (strings, numbers, bools; invariant culture). Same parts, same key, on every machine.</summary>
        public static string Key(params object[] parts)
        {
            ulong h = 14695981039346656037UL;
            if (parts != null)
                for (int i = 0; i < parts.Length; i++)
                {
                    string s = Part(parts[i]);
                    for (int c = 0; c < s.Length; c++) { h ^= s[c]; h *= 1099511628211UL; }
                    h ^= 0x1F; h *= 1099511628211UL;   // part separator, so ("ab","c") != ("a","bc")
                }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>Any thread. True with the bytes when the key is stored (or its write is still queued); false = miss / off / not ready.</summary>
        public static bool TryRead(FarCacheScope scope, string bucket, string key, out byte[] data)
        {
            data = null;
            if (!Enabled) return false;
            string path = PathFor(scope, bucket, key);
            if (path == null) return false;
            Interlocked.Increment(ref s_reads);
            lock (s_lock) { if (s_pending.TryGetValue(path, out byte[] q)) { data = q; Interlocked.Increment(ref s_hits); return true; } }
            long t0 = Stopwatch.GetTimestamp();
            data = ReadFile(path);
            long dt = Stopwatch.GetTimestamp() - t0;
            if (dt > Interlocked.Read(ref s_worstReadTicks)) Interlocked.Exchange(ref s_worstReadTicks, dt);
            if (data == null) return false;
            Interlocked.Increment(ref s_hits); Interlocked.Add(ref s_bytesRead, data.Length);
            Queue(() => { Touch(path); return null; });
            return true;
        }

        /// <summary>Main thread. The disk read runs on the worker; <paramref name="done"/> runs on the main thread with the bytes, or null on a miss.</summary>
        public static void ReadAsync(FarCacheScope scope, string bucket, string key, Action<byte[]> done)
        {
            if (done == null) return;
            int session = s_session;
            if (!Enabled || PathFor(scope, bucket, key) == null) { EbmFarJobs.Schedule(WorkPriority.Normal, () => done(null)); return; }
            Queue(() =>
            {
                TryRead(scope, bucket, key, out byte[] d);
                return () => done(session == s_session ? d : null);   // a world change in between: the caller gets a miss
            });
        }

        /// <summary>Any thread. Queued; lands atomically. The caller must not change <paramref name="data"/> after the call.</summary>
        public static void Write(FarCacheScope scope, string bucket, string key, byte[] data)
        {
            if (!Enabled || data == null) return;
            string path = PathFor(scope, bucket, key);
            if (path == null) return;
            lock (s_lock) s_pending[path] = data;
            Queue(() => { WriteFile(scope, path, data); return null; });
        }

        /// <summary>Any thread. Removes the key (queued).</summary>
        public static void Forget(FarCacheScope scope, string bucket, string key)
        {
            if (!Enabled) return;
            string path = PathFor(scope, bucket, key);
            if (path == null) return;
            lock (s_lock) s_pending.Remove(path);
            Queue(() => { Delete(path, "forget"); return null; });
        }

        /// <summary>Bytes on disk in that bucket (counted on the worker; 0 until counted, or when off / not ready).</summary>
        public static long BytesUsed(FarCacheScope scope, string bucket)
        {
            string dir = DirFor(scope, bucket);
            if (dir == null) return 0;
            lock (s_lock) { if (s_dirBytes.TryGetValue(dir, out long b)) return b; s_dirBytes[dir] = 0; }
            Queue(() => { long b = Count(dir); lock (s_lock) s_dirBytes[dir] = b; return null; });
            return 0;
        }

        // ---- world hand-off (FarCacheWorld) -------------------------------------------------------------------------------------

        internal static void SetWorld(string worldDir, string worldId)
        {
            s_session++;
            lock (s_lock) s_worldBytes = -1L;
            s_worldDir = worldDir; WorldId = worldId;
            if (worldDir == null) return;
            try { OnWorldReady?.Invoke(); }
            catch (Exception e) { EasyBakeLog.Warn($"[FarCache] an OnWorldReady handler failed: {e.Message}"); }
        }

        internal static void ClearWorld() { s_session++; lock (s_lock) s_worldBytes = -1L; s_worldDir = null; WorldId = null; }

        internal static long WorldCapBytes => (long)(s_worldMb?.Value ?? 256) << 20;
        internal static long AllWorldsCapBytes => (long)(s_allWorldsMb?.Value ?? 1024) << 20;
        internal static long GlobalCapBytes => (long)(s_globalMb?.Value ?? 512) << 20;

        // ---- worker ----------------------------------------------------------------------------------------------------------

        internal sealed class Job { public Func<Action> Work; }
        internal sealed class Done { public Action Main; }

        internal static void Queue(Func<Action> work)
        {
            // 1.2.255 (D15): any thread may be first, so the one worker is made under the lock
            lock (s_lock)
            {
                if (s_worker == null) s_worker = new EbmFarWorker<Job, Done>("FarCache", (j, d) =>
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { d.Main = j.Work(); }
                    catch (Exception e) { d.Main = null; EasyBakeLog.Debug($"[FarCache] worker job failed: {e.GetType().Name}: {e.Message}"); }
                    Interlocked.Add(ref s_workerTicks, Stopwatch.GetTimestamp() - t0);
                });
            }
            s_worker.QueueJob(new Job { Work = work }, d => d.Main?.Invoke());
        }

        // ---- paths -----------------------------------------------------------------------------------------------------------

        private static string DirFor(FarCacheScope scope, string bucket)
        {
            if (!Enabled || string.IsNullOrEmpty(bucket)) return null;
            string b = Safe(bucket);
            if (scope == FarCacheScope.Global) return Path.Combine(Root, b);
            string w = s_worldDir;
            return w == null ? null : Path.Combine(w, b);
        }

        private static string PathFor(FarCacheScope scope, string bucket, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string dir = DirFor(scope, bucket);
            return dir == null ? null : Path.Combine(dir, Safe(key) + Ext);
        }

        /// <summary>A file-safe name: kept when it is short and plain, else hashed (so any caller string is a valid file name).</summary>
        internal static string Safe(string s)
        {
            if (s.Length <= 96 && s != "worlds")
            {
                bool plain = true;
                foreach (char c in s) if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.')) { plain = false; break; }
                if (plain && s[0] != '.') return s;
            }
            return Key(s);
        }

        private static string Part(object o)
        {
            switch (o)
            {
                case null: return "\u0000";
                case string s: return s;
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case double d: return d.ToString("R", CultureInfo.InvariantCulture);
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return o.ToString();
            }
        }

        // ---- file format -----------------------------------------------------------------------------------------------------

        // 1.2.255 [Optimize] CacheLowGarbage. The inflated bytes are handed to the caller, so those stay a new array. Same bytes on disk.
        private static bool LowGarbage => FiresEasyBakeMeshesPlugin.CacheLowGarbage != null && FiresEasyBakeMeshesPlugin.CacheLowGarbage.Value;
        private const int KeepBufferBytes = 4 * 1024 * 1024;
        [ThreadStatic] private static byte[] s_readBuffer;
        private static MemoryStream s_compressBuffer;       // worker only
        private static readonly byte[] s_header = new byte[16];   // worker only

        private static byte[] RentRead(int bytes)
        {
            if (bytes > KeepBufferBytes) return new byte[bytes];
            if (s_readBuffer == null || s_readBuffer.Length < bytes) s_readBuffer = new byte[Math.Max(bytes, 1 << 16)];
            return s_readBuffer;
        }

        private static void Put32(int at, uint v) { s_header[at] = (byte)v; s_header[at + 1] = (byte)(v >> 8); s_header[at + 2] = (byte)(v >> 16); s_header[at + 3] = (byte)(v >> 24); }

        private static byte[] ReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] all; int fileBytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < 16 || fs.Length > int.MaxValue) { Bad(path, "size"); return null; }
                    fileBytes = (int)fs.Length;
                    all = LowGarbage ? RentRead(fileBytes) : new byte[fileBytes];
                    int got = 0; while (got < fileBytes) { int n = fs.Read(all, got, fileBytes - got); if (n <= 0) break; got += n; }
                    if (got != fileBytes) { Bad(path, "short read"); return null; }
                }
                if (BitConverter.ToUInt32(all, 0) != FileMagic) { Bad(path, "magic"); return null; }
                int raw = BitConverter.ToInt32(all, 4); uint crc = BitConverter.ToUInt32(all, 8); int comp = BitConverter.ToInt32(all, 12);
                if (raw < 0 || comp < 0 || 16L + comp != fileBytes || raw > comp * 1032L) { Bad(path, "lengths"); return null; }   // 1.2.255 D15: deflate cannot expand past 1032:1, so a bad length never allocates
                var outBytes = new byte[raw];
                using (var ms = new MemoryStream(all, 16, comp, false))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                {
                    int got = 0; while (got < raw) { int n = ds.Read(outBytes, got, raw - got); if (n <= 0) break; got += n; }
                    if (got != raw) { Bad(path, "inflate"); return null; }
                }
                if (Crc32(outBytes) != crc) { Bad(path, "crc"); return null; }
                return outBytes;
            }
            catch (IOException) { return null; }   // being replaced right now: a miss, never a delete
            catch (UnauthorizedAccessException) { return null; }
            catch (Exception e) { Bad(path, e.GetType().Name); return null; }
        }

        private static void Bad(string path, string why)
        {
            Interlocked.Increment(ref s_bad);
            EasyBakeLog.Debug($"[FarCache] bad entry {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)} ({why}): treated as a miss and removed");
            Queue(() => { Delete(path, "bad"); return null; });
        }

        private static void WriteFile(FarCacheScope scope, string path, byte[] data)
        {
            try
            {
                lock (s_lock) { if (!s_pending.TryGetValue(path, out byte[] cur) || !ReferenceEquals(cur, data)) return; }   // replaced or forgotten since
                string dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                bool low = LowGarbage;
                var ms = low ? (s_compressBuffer ?? (s_compressBuffer = new MemoryStream(1 << 16))) : new MemoryStream();   // worker only
                ms.SetLength(0);
                using (var ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length);
                byte[] comp = low ? ms.GetBuffer() : ms.ToArray();
                int compBytes = (int)ms.Length;
                string tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (low)
                    {
                        Put32(0, FileMagic); Put32(4, (uint)data.Length); Put32(8, Crc32(data)); Put32(12, (uint)compBytes);
                        fs.Write(s_header, 0, 16);
                    }
                    else
                    {
                        fs.Write(BitConverter.GetBytes(FileMagic), 0, 4);
                        fs.Write(BitConverter.GetBytes(data.Length), 0, 4);
                        fs.Write(BitConverter.GetBytes(Crc32(data)), 0, 4);
                        fs.Write(BitConverter.GetBytes(compBytes), 0, 4);
                    }
                    fs.Write(comp, 0, compBytes);
                }
                if (low && ms.Capacity > KeepBufferBytes) s_compressBuffer = null;
                long old = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (old > 0) File.Replace(tmp, path, null); else File.Move(tmp, path);
                long now = 16L + compBytes;
                lock (s_lock)
                {
                    if (s_pending.TryGetValue(path, out byte[] cur) && ReferenceEquals(cur, data)) s_pending.Remove(path);
                    if (s_dirBytes.TryGetValue(dir, out long b)) s_dirBytes[dir] = b - old + now;
                }
                AddScopeBytes(path, now - old);
                Interlocked.Increment(ref s_writes); Interlocked.Add(ref s_bytesWritten, now);
                Evict(scope);
            }
            catch (Exception e)
            {
                lock (s_lock) { if (s_pending.TryGetValue(path, out byte[] cur) && ReferenceEquals(cur, data)) s_pending.Remove(path); }
                EasyBakeLog.Debug($"[FarCache] write {Path.GetFileName(path)} failed ({e.GetType().Name}: {e.Message}): the caller rebuilds next time");
            }
        }

        internal static void Delete(string path, string why)
        {
            try
            {
                if (!File.Exists(path)) return;
                long len = new FileInfo(path).Length;
                File.Delete(path);
                string dir = Path.GetDirectoryName(path);
                lock (s_lock) { if (s_dirBytes.TryGetValue(dir, out long b)) s_dirBytes[dir] = Math.Max(0, b - len); }
                AddScopeBytes(path, -len);
            }
            catch (Exception e) { EasyBakeLog.Debug($"[FarCache] could not remove {Path.GetFileName(path)} ({why}): {e.Message}"); }
        }

        private static void Touch(string path)
        {
            try { var fi = new FileInfo(path); if (fi.Exists && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalHours >= 1) fi.LastWriteTimeUtc = DateTime.UtcNow; }   // LRU stamp, at most hourly
            catch { }
        }

        internal static long Count(string dir)
        {
            try { if (!Directory.Exists(dir)) return 0; long n = 0; foreach (var f in new DirectoryInfo(dir).GetFiles("*" + Ext, SearchOption.AllDirectories)) n += f.Length; return n; }
            catch { return 0; }
        }

        private static void AddScopeBytes(string path, long delta)
        {
            string world = s_worldDir, root = Root;
            bool isWorld = world != null && path.StartsWith(world, StringComparison.OrdinalIgnoreCase);
            bool isGlobal = !isWorld && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(Path.Combine(root, "worlds"), StringComparison.OrdinalIgnoreCase);
            lock (s_lock)
            {
                if (isWorld && s_worldBytes >= 0L) s_worldBytes += delta;
                else if (isGlobal && s_globalBytes >= 0L) s_globalBytes += delta;
            }
        }

        /// <summary>Worker: keeps the written scope under its cap, removing the least recently used files down to 90%.</summary>
        private static void Evict(FarCacheScope scope)
        {
            string root = scope == FarCacheScope.World ? s_worldDir : Root;
            if (root == null || !Directory.Exists(root)) return;
            long cap = scope == FarCacheScope.World ? WorldCapBytes : GlobalCapBytes;
            long counted;
            lock (s_lock) counted = scope == FarCacheScope.World ? s_worldBytes : s_globalBytes;
            if (counted >= 0L && counted <= cap) return;   // 1.2.255 D13: known to be under the cap, nothing is listed
            Interlocked.Increment(ref s_evictLists);
            var files = new List<FileInfo>();
            long total = 0;
            foreach (var d in new DirectoryInfo(root).GetDirectories())
            {
                if (scope == FarCacheScope.Global && d.Name == "worlds") continue;
                foreach (var f in d.GetFiles("*" + Ext)) { files.Add(f); total += f.Length; }
            }
            lock (s_lock) { if (scope == FarCacheScope.World) s_worldBytes = total; else s_globalBytes = total; }
            if (total <= cap) return;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            long goal = cap * 9 / 10; int n = 0; long freed = 0;
            foreach (var f in files) { if (total - freed <= goal) break; freed += f.Length; Delete(f.FullName, "cap"); n++; }
            Interlocked.Add(ref s_evicted, n);
            EasyBakeLog.Debug($"[FarCache] {scope} over its {cap >> 20} MB cap ({total >> 20} MB): removed {n} least recently used file(s), {freed >> 20} MB");
        }

        // ---- stats ------------------------------------------------------------------------------------------------------------

        private static void StatsTick()
        {
            if (UnityEngine.Time.unscaledTime < s_nextStats) return;
            s_nextStats = UnityEngine.Time.unscaledTime + 30f;
            long reads = Interlocked.Exchange(ref s_reads, 0), hits = Interlocked.Exchange(ref s_hits, 0), writes = Interlocked.Exchange(ref s_writes, 0);
            long bw = Interlocked.Exchange(ref s_bytesWritten, 0), br = Interlocked.Exchange(ref s_bytesRead, 0), ev = Interlocked.Exchange(ref s_evicted, 0);
            long bad = Interlocked.Exchange(ref s_bad, 0), wt = Interlocked.Exchange(ref s_workerTicks, 0), worst = Interlocked.Exchange(ref s_worstReadTicks, 0);
            long skippedUnchanged = Interlocked.Exchange(ref s_skippedUnchanged, 0L);
            long lists = Interlocked.Exchange(ref s_evictLists, 0L);
            if (reads == 0 && writes == 0 && ev == 0 && bad == 0 && skippedUnchanged == 0L) return;
            if (!EasyBakeLog.DebugOn()) return;
            double ms = 1000.0 / Stopwatch.Frequency;
            EasyBakeLog.Debug($"[FarCache] last 30 s: {reads} read(s), {hits} hit(s), {reads - hits} miss(es), {br >> 10} KB read (worst {worst * ms:0.0} ms); "
                + $"{writes} write(s), {bw >> 10} KB; worker {wt * ms:0} ms; {ev} evicted, {bad} bad; {skippedUnchanged} save(s) skipped, unchanged; {lists} folder listing(s) for the caps");
        }

        // ---- CRC32 (IEEE) -------------------------------------------------------------------------------------------------------

        private static uint[] s_crcTable;
        internal static uint Crc32(byte[] data) => Crc32(data, data.Length);

        internal static uint Crc32(byte[] data, int count)
        {
            var t = s_crcTable;
            if (t == null)
            {
                t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
                s_crcTable = t;
            }
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < count; i++) crc = t[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
