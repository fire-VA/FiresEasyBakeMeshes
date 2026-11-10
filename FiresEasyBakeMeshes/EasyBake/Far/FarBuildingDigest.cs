using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>One placed piece as the far tier needs it: prefab, position, rotation (24 bytes on disk and on the wire).</summary>
    internal struct FarPiece
    {
        public int Prefab; public Vector3 Pos; public Quaternion Rot;
        // 1.2.262: the copy's snow cap level, IN MEMORY ONLY (never on disk or the wire, never in the fingerprint): 0 = no cap (vanilla shows
        // WearNTear.m_snow only above 0.25 build-up), 1-4 = quarters of vanilla's _SnowLevel = Remap(build-up, 0.25, 1, 0, 1). Set by a ZDO scan; a
        // piece from the cache or the server has 0 until its zone is scanned, so an unknown cap is never shown.
        public byte Cap;
    }

    /// <summary>The far-relevant pieces of one 64 m zone, plus an order-independent fingerprint of them.</summary>
    internal sealed class FarZone
    {
        public Vector2s Zone; public FarPiece[] Pieces; public ulong Print; public bool FromServer;
    }

    /// <summary>
    /// Which pieces stand in each zone, read from ZDOs (never from live objects). A host or server holds every ZDO, so it scans
    /// every zone in reach, round-robin, in a main-thread slice. A client only holds the zones it has loaded, so it scans only those
    /// (its visited bases); far zones come from its cache or, when both keys are on, from the server (FarBuildingNet).
    /// Each zone is keyed by an order-independent fingerprint, so an unchanged zone costs nothing downstream.
    /// </summary>
    internal static class FarBuildingDigest
    {
        internal const int MaxPiecesPerZone = 20000;
        internal const int ZoneLimit = 160; // |zone x/y| beyond this is outside any Valheim world (10.5 km / 64 m)

        internal static readonly Dictionary<Vector2s, FarZone> Zones = new Dictionary<Vector2s, FarZone>();
        internal static event Action<Vector2s> Changed;

        private static readonly List<Vector2s> s_ring = new List<Vector2s>();
        private static int s_ringIdx;
        private static Vector3 s_ringCenter = new Vector3(float.MaxValue, 0, 0);
        private static float s_ringBuiltAt = -999f;
        private static readonly List<ZDO> s_buf = new List<ZDO>();
        private static readonly List<FarPiece> s_pieces = new List<FarPiece>();
        private static readonly SimulationDistance s_oneSector = new SimulationDistance(0, 0, false);
        private static bool s_indexDirty;
        private static float s_indexWriteAt;
        private static bool s_cacheLoaded;
        // 1.2.255: reads the index still owes (the index itself, then one per listed zone). Main thread. s_indexGen changes with the world, so a late callback from the old world never touches the new count.
        private static int s_indexReadsPending, s_indexGen;
        private static readonly HashSet<Vector2s> s_indexListed = new HashSet<Vector2s>();

        private static void IndexReadDone(int gen)
        {
            if (gen == s_indexGen && s_indexReadsPending > 0) s_indexReadsPending--;
        }

        // ── Per-prefab classification (main thread, cached) ──
        internal sealed class PrefabClass { public bool Draw; public string Name; public float Height; }
        private static readonly Dictionary<int, PrefabClass> s_class = new Dictionary<int, PrefabClass>();

        internal static PrefabClass Classify(int hash)
        {
            if (s_class.TryGetValue(hash, out var c)) return c;
            c = new PrefabClass();
            s_class[hash] = c;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(hash) : null;
            if (prefab == null) return c;
            c.Name = prefab.name;
            // A building piece: Piece or WearNTear. Never: vanilla's own distant objects, anything that moves, plants.
            if (prefab.GetComponent<Piece>() == null && prefab.GetComponent<WearNTear>() == null) return c;
            var nview = prefab.GetComponent<ZNetView>();
            if (nview != null && nview.m_distant) return c;
            if (prefab.GetComponentInChildren<Rigidbody>(true) != null) return c;
            if (prefab.GetComponent<Ship>() != null || prefab.GetComponent<Vagon>() != null || prefab.GetComponent<Plant>() != null) return c;
            var b = FarBuildingSources.LocalBounds(prefab);
            if (b.size == Vector3.zero) return c;
            var piece = prefab.GetComponent<Piece>();
            float extent = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
            // Small furniture and decor do not read at 1-2.5 km and cost vertices.
            if (piece != null && (piece.m_category == Piece.PieceCategory.Furniture || piece.m_category == Piece.PieceCategory.Misc) && extent < 2.5f) return c;
            c.Height = b.max.y;
            c.Draw = true;
            return c;
        }

        internal static void Step(long deadline)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || ZoneSystem.instance == null) return;
            if (!s_cacheLoaded) LoadIndexFromCache();

            bool authoritative = ZNet.instance.IsServer();
            RebuildRingIfNeeded(authoritative);
            if (s_ring.Count == 0) return;

            int scanned = 0, skippedNow = 0;
            while (Stopwatch.GetTimestamp() < deadline && scanned < s_ring.Count)
            {
                if (s_ringIdx >= s_ring.Count) s_ringIdx = 0;
                var zone = s_ring[s_ringIdx++];
                scanned++;
                // A client's ZDOs are complete only in zones it has loaded; elsewhere an empty scan would wrongly erase a base.
                // 0.2.1210 ([visual], Fire 11-08 shots 613-615: the BlueHills keep 190 m east of the TOWN50 spot never drew, while the
                // client held ~120k ZDOs with no instance): a zone the client holds ZDOs for but has not loaded is read too, ADDITIVELY
                // (a held scan can add or update pieces, never remove one), so a partial set never erases a base.
                bool held = !authoritative && !ZoneSystem.instance.IsZoneLoaded(zone);
                // 1.2.216 ([ghost], O log: 42,360 loaded-zone scans per 30 s with 0 changes): with [Optimize] FarBuildingsChangedZonesOnly a
                // zone is scanned again only when its ZDO count changed since its last scan (an O(1) read of vanilla's sector list: a piece
                // placed, removed, received or unloaded), or after SafetyRescanSeconds (a same-count swap). The first scan of every zone is kept.
                if (ChangedOnly && Unchanged(zdoMan, zone)) { s_skippedUnchanged++; if (++skippedNow >= UnchangedChecksThisStep) break; continue; }
                ScanZone(zdoMan, zone, held);
                Stamp(zdoMan, zone);
            }
            if (Time.unscaledTime >= s_nextDigestLog && (s_heldScans > 0 || s_loadedScans > 0))
            {
                s_nextDigestLog = Time.unscaledTime + 30f;
                EasyBakeLog.Debug($"{FarBuildings.Log} digest, last 30 s: {s_loadedScans} loaded-zone scan(s), {s_heldScans} held-zone scan(s) " +
                    $"({s_heldZonesGrown} zone(s) grew, +{s_heldPiecesAdded} piece(s) from held ZDOs); ring {s_ring.Count} zone(s) out to {FarBuildings.Reach:0} m; {Zones.Count} zone(s) with far pieces; " +
                    $"changed-zones-only {(ChangedOnly ? "on" : "off")}: {s_skippedUnchanged} unchanged zone(s) skipped, {s_safetyRescans} safety rescan(s), {s_safetyFoundChange} of them found a change");
                s_heldScans = s_loadedScans = s_heldZonesGrown = s_heldPiecesAdded = 0;
                s_skippedUnchanged = s_safetyRescans = s_safetyFoundChange = 0;
            }

            // 1.2.255: never while the listed zones are still being read: the index is written from Zones, and a short one would drop the rest at the next launch. The dirty flag stays set.
            if (s_indexDirty && s_indexReadsPending == 0 && Time.unscaledTime >= s_indexWriteAt) { s_indexDirty = false; s_indexWriteAt = Time.unscaledTime + 30f; WriteIndexToCache(); }
        }

        private static void RebuildRingIfNeeded(bool authoritative)
        {
            Vector3 center = ZNet.instance.GetReferencePosition();
            bool moved = (center - s_ringCenter).sqrMagnitude > 128f * 128f;
            if (!moved && Time.unscaledTime - s_ringBuiltAt < 5f) return;
            s_ringBuiltAt = Time.unscaledTime; s_ringCenter = center;

            var seen = s_seen; seen.Clear();   // 1.2.234 [ghost]: reused (was a new ~5k-zone set every 5 s)
            s_ringPrev.Clear(); s_ringPrev.AddRange(s_ring);   // 1.2.250: the lap restarts only when the player moved or the ring's contents changed
            s_ring.Clear();
            AddRing(center, FarBuildings.Reach, seen);   // 0.2.1210: a client reads its held ZDOs out to Reach too (was 400 m, loaded zones only)
            // A server answering clients keeps their reach fresh too.
            if (authoritative && FarBuildings.ServerSends)
                foreach (var peer in ZNet.instance.GetPeers())
                    if (peer != null) AddRing(peer.m_refPos, FarBuildings.Reach, seen);
            if (moved || !SameRing()) s_ringIdx = 0;
            s_ringPrev.Clear();
        }

        // Same zones in the same order as the ring it replaced (the rebuild runs every 5 s; a player who stayed put keeps the lap where it was).
        private static bool SameRing()
        {
            if (s_ring.Count != s_ringPrev.Count) return false;
            for (int i = 0; i < s_ring.Count; i++)
                if (s_ring[i].x != s_ringPrev[i].x || s_ring[i].y != s_ringPrev[i].y) return false;
            return true;
        }

        private static readonly HashSet<Vector2s> s_seen = new HashSet<Vector2s>();
        private static readonly List<Vector2s> s_ringPrev = new List<Vector2s>();

        private static void AddRing(Vector3 center, float reach, HashSet<Vector2s> seen)
        {
            var c = ZoneSystem.GetZone(center);
            int r = Mathf.CeilToInt(reach / ZoneSystem.c_ZoneSize);
            float r2 = (reach + ZoneSystem.c_ZoneSize) * (reach + ZoneSystem.c_ZoneSize);
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c.x + dx, y = c.y + dy;
                    if (Math.Abs(x) > ZoneLimit || Math.Abs(y) > ZoneLimit) continue;
                    if ((dx * dx + dy * dy) * (float)(ZoneSystem.c_ZoneSize * ZoneSystem.c_ZoneSize) > r2) continue;
                    var z = new Vector2s(x, y);
                    if (seen.Add(z)) s_ring.Add(z);
                }
        }

        private static int s_heldScans, s_loadedScans, s_heldZonesGrown, s_heldPiecesAdded;   // 0.2.1210

        // ---- 1.2.216 changed-zones-only ([ghost])
        private const float SafetyRescanSeconds = 60f;
        private const int MaxUnchangedChecksPerStep = 512;   // a full lap of a ~5k-zone ring in ~10 frames; changes are seen within ~0.2 s
        private struct ZoneStamp { public int Count; public float At; public ulong Print; }
        private static readonly Dictionary<Vector2s, ZoneStamp> s_stamps = new Dictionary<Vector2s, ZoneStamp>();
        private static int s_skippedUnchanged, s_safetyRescans, s_safetyFoundChange;
        private static bool s_pendingSafety; private static ulong s_safetyPrint;
        // 1.2.249: a whole lap of the ring in about 40 frames, so the 5 s ring rebuild never starves the outer zones even at 10 fps
        private static bool QuietPolling => FiresEasyBakeMeshesPlugin.FarBuildingsQuietPolling != null && FiresEasyBakeMeshesPlugin.FarBuildingsQuietPolling.Value;
        private static int UnchangedChecksThisStep => QuietPolling ? Mathf.Max(32, s_ring.Count / 40) : MaxUnchangedChecksPerStep;
        // 1.2.249: 0 to 20 s by zone, so a ring first scanned together does not come due for its safety rescan together (about 5,000 in one window, 0 changes found)
        private static float RescanJitter(Vector2s z) => QuietPolling ? (((z.x * 73) ^ (z.y * 179)) & 1023) / 1023f * 20f : 0f;
        internal static bool ChangedOnly => FiresEasyBakeMeshesPlugin.FarBuildingsChangedZonesOnly != null && FiresEasyBakeMeshesPlugin.FarBuildingsChangedZonesOnly.Value;

        // vanilla's private sector lists, read through a typed field ref (never a publicized reference)
        private static readonly HarmonyLib.AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector = MakeSectorsRef();
        private static HarmonyLib.AccessTools.FieldRef<ZDOMan, List<ZDO>[]> MakeSectorsRef()
        {
            try { return HarmonyLib.AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector"); }
            catch (Exception e) { EasyBakeLog.Info($"{FarBuildings.Log} changed-zones-only unavailable (no ZDOMan.m_objectsBySector: {e.Message}): every zone is scanned as before"); return null; }
        }

        private static int SectorCount(ZDOMan zdoMan, Vector2s zone)
        {
            int idx = (int)ZoneSystem.SectorToIndex(zone.x, zone.y).Sector;
            var sectors = s_objectsBySector != null ? s_objectsBySector(zdoMan) : null;
            if (idx < 0 || sectors == null || idx >= sectors.Length) return -1;   // outside the indexed world: always scan
            var list = sectors[idx];
            return list != null ? list.Count : 0;
        }

        private static bool Unchanged(ZDOMan zdoMan, Vector2s zone)
        {
            if (!s_stamps.TryGetValue(zone, out var st)) return false;   // never scanned
            int n = SectorCount(zdoMan, zone);
            if (n < 0 || n != st.Count) return false;
            if (Time.unscaledTime - st.At < SafetyRescanSeconds) return true;
            s_safetyRescans++; s_pendingSafety = true; s_safetyPrint = st.Print;   // due a safety rescan: scan it, and count whether anything changed
            return false;
        }

        private static void Stamp(ZDOMan zdoMan, Vector2s zone)
        {
            ulong print = Zones.TryGetValue(zone, out var z) ? z.Print : 0UL;
            if (s_pendingSafety) { s_pendingSafety = false; if (print != s_safetyPrint) s_safetyFoundChange++; }
            s_stamps[zone] = new ZoneStamp { Count = SectorCount(zdoMan, zone), At = Time.unscaledTime + RescanJitter(zone), Print = print };
            if (s_stamps.Count > 40000) s_stamps.Clear();   // bounded (the ring is at most ~5k zones)
        }
        private static float s_nextDigestLog;
        private static readonly HashSet<long> s_union = new HashSet<long>();
        private static long PieceKey(FarPiece p) =>
            ((long)p.Prefab * 73856093L) ^ ((long)Mathf.RoundToInt(p.Pos.x * 10f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.Pos.y * 10f) * 83492791L) ^ ((long)Mathf.RoundToInt(p.Pos.z * 10f) * 2654435761L);

        private static void ScanZone(ZDOMan zdoMan, Vector2s zone, bool held)
        {
            if (held) s_heldScans++; else s_loadedScans++;
            s_buf.Clear();
            zdoMan.FindSectorObjects(zone, s_oneSector, s_buf, null);
            s_pieces.Clear();
            for (int i = 0; i < s_buf.Count && s_pieces.Count < MaxPiecesPerZone; i++)
            {
                var zdo = s_buf[i];
                if (zdo == null || !zdo.IsValid()) continue;
                Vector3 p = zdo.GetPosition();
                if (p.y > 3000f) continue;                 // dungeon and location interiors sit up high
                if (ZoneSystem.GetZone(p) != zone) continue;
                var c = Classify(zdo.GetPrefab());
                if (!c.Draw) continue;
                s_pieces.Add(new FarPiece { Prefab = zdo.GetPrefab(), Pos = p, Rot = zdo.GetRotation(), Cap = CapLevel(zdo.GetFloat(ZDOVars.s_snow)) });
            }
            s_buf.Clear();
            if (held)
            {
                // 0.2.1210: additive only. Nothing held = nothing learned; otherwise the zone keeps every piece it already had
                // (from cache, server or an earlier visit) and gains the held ones it lacked.
                if (s_pieces.Count == 0) return;
                Zones.TryGetValue(zone, out var known);
                int before = known != null ? known.Pieces.Length : 0;
                if (known != null)
                {
                    s_union.Clear();
                    foreach (var p in s_pieces) s_union.Add(PieceKey(p));
                    foreach (var p in known.Pieces) if (s_union.Add(PieceKey(p))) s_pieces.Add(p);
                }
                if (s_pieces.Count > MaxPiecesPerZone) s_pieces.RemoveRange(MaxPiecesPerZone, s_pieces.Count - MaxPiecesPerZone);
                if (known != null && s_pieces.Count <= before) return;   // nothing new
                s_heldZonesGrown++; s_heldPiecesAdded += s_pieces.Count - before;
                ApplyScan(zone, s_pieces, fromServer: known != null && known.FromServer);
                return;
            }
            ApplyScan(zone, s_pieces, fromServer: false);
        }

        // 1.2.234 [ghost]: Apply's own no-change tests on the scan list first; the array is made only when the zone really changed.
        private static void ApplyScan(Vector2s zone, List<FarPiece> pieces, bool fromServer)
        {
            Zones.TryGetValue(zone, out var old);
            if (pieces.Count == 0 ? old == null : old != null && old.Print == Fingerprint(pieces))
            {
                // 1.2.262: same pieces, but a cap level may have moved (snow built up or melted). In memory only: no cache write (caps are never saved).
                if (old != null && pieces.Count > 0 && CapsDiffer(old.Pieces, pieces))
                {
                    old.Pieces = pieces.ToArray(); s_capOnlyChanges++;
                    Changed?.Invoke(zone);
                }
                return;
            }
            Apply(zone, pieces.ToArray(), fromServer);
        }

        /// <summary>1.2.262: vanilla's cap rule as a level. 0 at or below 0.25 build-up (no cap), else quarters of Remap(build-up, 0.25, 1, 0, 1), at least 1.</summary>
        internal static byte CapLevel(float buildup)
        {
            if (!(buildup > 0.25f)) return 0;
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01((buildup - 0.25f) / 0.75f) * 4f), 1, 4);
        }

        // the scan order is the ZDO order of one sector walk, and an equal print means the same pieces: compared by position key, order-free
        private static readonly Dictionary<long, byte> s_capOld = new Dictionary<long, byte>();
        internal static int s_capOnlyChanges;
        private static bool CapsDiffer(FarPiece[] old, List<FarPiece> now)
        {
            bool any = false;
            foreach (var p in old) if (p.Cap != 0) { any = true; break; }
            if (!any) { foreach (var p in now) if (p.Cap != 0) return true; return false; }
            s_capOld.Clear();
            foreach (var p in old) s_capOld[PieceKey(p)] = p.Cap;
            foreach (var p in now) if (!s_capOld.TryGetValue(PieceKey(p), out var c) || c != p.Cap) { s_capOld.Clear(); return true; }
            s_capOld.Clear();
            return false;
        }

        /// <summary>Stores a zone's pieces if they changed (scan, cache or server). Empty = the zone has no far pieces.</summary>
        // fromCache (1.2.254, [Far Cache] SkipUnchangedSaves): the pieces were just read from that zone's own file, so the file and the index already hold them. Every later scan or server update comes
        // through with fromCache false; the print test below still writes any zone that really changed.
        internal static void Apply(Vector2s zone, FarPiece[] pieces, bool fromServer, bool fromCache = false)
        {
            ulong print = Fingerprint(pieces);
            Zones.TryGetValue(zone, out var old);
            if (old != null && old.Print == print) return;
            if (pieces.Length == 0)
            {
                if (old == null) return;
                Zones.Remove(zone);
                FarCache.Forget(FarCacheScope.World, "far-buildings", ZoneKey(zone));
            }
            else
            {
                Zones[zone] = new FarZone { Zone = zone, Pieces = pieces, Print = print, FromServer = fromServer };
                if (fromCache && FarCache.SkipUnchangedSaves) FarCache.NoteUnchangedSaveSkipped();
                else if (old == null && FarCache.SkipUnchangedSaves && FarCache.Enabled) WriteUnlessSame(zone, pieces, print);
                else { FarCache.Write(FarCacheScope.World, "far-buildings", ZoneKey(zone), Serialize(pieces, print)); s_indexDirty = true; }
            }
            if (pieces.Length == 0) s_indexDirty = true;
            FarBuildings.S.Zones = Zones.Count;
            Changed?.Invoke(zone);
        }

        // 1.2.255 (scan first, on a host): the zone is new to memory but its file may already hold these same pieces. The stored print (the file's first 8 bytes) decides, on the far worker.
        private static void WriteUnlessSame(Vector2s zone, FarPiece[] pieces, ulong print)
        {
            int gen = s_indexGen;
            FarCache.ReadAsync(FarCacheScope.World, "far-buildings", ZoneKey(zone), bytes =>
            {
                if (gen != s_indexGen || !Zones.TryGetValue(zone, out var current) || current.Print != print) return;   // changed again since: that Apply wrote it
                if (bytes != null && bytes.Length >= 8 && BitConverter.ToUInt64(bytes, 0) == print)
                {
                    FarCache.NoteUnchangedSaveSkipped();
                    if (!s_indexListed.Contains(zone)) s_indexDirty = true;   // the file is right but the index does not list it (a crash before its last write)
                    return;
                }
                FarCache.Write(FarCacheScope.World, "far-buildings", ZoneKey(zone), Serialize(pieces, print));
                s_indexDirty = true;
            });
        }

        internal static string ZoneKey(Vector2s z) => FarCache.Key("zone", z.x, z.y);

        // Order-independent: the sum of per-piece hashes (ZDO order in a sector is not stable), plus the count.
        // 0.2.1216 ([generator], Fire 11-08 615 y53: "still not rendering the builds at the back of the town on the hill"; client log
        // "server sent 20 zone(s), 0 applied"): the print hashed the rotation as RoundToInt(eulerAngles.y * 10), but the wire and the cache
        // carry the rotation as four int16s. The receiver recomputed the print from the quantised rotation, and a yaw that sat on a rounding
        // edge or at the 0/360 wrap (a piece at yaw 0 stored as 359.9999 = 3600 vs 0) changed the print, so Deserialize dropped the WHOLE
        // zone (server zones and cached zones alike). The print now hashes the exact int16s that are written, so both sides agree bit for bit.
        internal static ulong Fingerprint(FarPiece[] pieces)
        {
            ulong sum = (ulong)pieces.Length * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < pieces.Length; i++) sum += PieceHash(pieces[i]);
            return sum;
        }

        // 1.2.234 ([ghost], profiler O 11-08: FarRunner.Update 13.75 KB GC a frame): the same print straight from the scan list, so an unchanged
        // zone (nearly every scan, incl. the 60 s safety rescans) is never copied to an array only for Apply to throw it away.
        internal static ulong Fingerprint(List<FarPiece> pieces)
        {
            ulong sum = (ulong)pieces.Count * 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < pieces.Count; i++) sum += PieceHash(pieces[i]);
            return sum;
        }

        private static ulong PieceHash(FarPiece p)
        {
                ulong h = (ulong)(uint)p.Prefab;
                h = Mix(h ^ (ulong)(uint)Mathf.RoundToInt(p.Pos.x * 100f));
                h = Mix(h ^ (ulong)(uint)Mathf.RoundToInt(p.Pos.y * 100f));
                h = Mix(h ^ (ulong)(uint)Mathf.RoundToInt(p.Pos.z * 100f));
                h = Mix(h ^ ((ulong)(ushort)Q(p.Rot.x) | ((ulong)(ushort)Q(p.Rot.y) << 16) | ((ulong)(ushort)Q(p.Rot.z) << 32) | ((ulong)(ushort)Q(p.Rot.w) << 48)));
                return h;
        }

        // 0.2.1216: why the last Deserialize said no (the client's "0 applied" line names it)
        internal static string LastReject;

        private static ulong Mix(ulong x) { x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33; return x; }

        // ── Wire / disk format: [print u64][count i32] then per piece [prefab i32][x y z f32][rot 4 x i16] ──
        internal static byte[] Serialize(FarPiece[] pieces, ulong print)
        {
            using (var ms = new MemoryStream(12 + pieces.Length * 24))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(print); w.Write(pieces.Length);
                foreach (var p in pieces)
                {
                    w.Write(p.Prefab); w.Write(p.Pos.x); w.Write(p.Pos.y); w.Write(p.Pos.z);
                    w.Write(Q(p.Rot.x)); w.Write(Q(p.Rot.y)); w.Write(Q(p.Rot.z)); w.Write(Q(p.Rot.w));
                }
                return ms.ToArray();
            }
        }

        /// <summary>Defensive parse (data may come from the network): null on any bad count, size or value.</summary>
        internal static FarPiece[] Deserialize(byte[] data, Vector2s expectZone, out ulong print)
        {
            print = 0; LastReject = null;
            if (data == null || data.Length < 12) { LastReject = "short"; return null; }
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data, false)))
                {
                    print = r.ReadUInt64();
                    int n = r.ReadInt32();
                    if (n < 0 || n > MaxPiecesPerZone || data.Length != 12 + n * 24) { LastReject = "size"; return null; }
                    var arr = new FarPiece[n];
                    for (int i = 0; i < n; i++)
                    {
                        int prefab = r.ReadInt32();
                        var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        // 0.2.1216: kept exactly as written (D of the int16s, not renormalised), so Q gives back the same int16s for the print
                        var rot = new Quaternion(D(r.ReadInt16()), D(r.ReadInt16()), D(r.ReadInt16()), D(r.ReadInt16()));
                        if (!Finite(pos)) { LastReject = "position not finite"; return null; }
                        if (ZoneSystem.GetZone(pos) != expectZone) { LastReject = "a piece outside the zone"; return null; }
                        float m = Mathf.Sqrt(rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w);
                        if (m < 0.5f) { LastReject = "rotation"; return null; }
                        arr[i] = new FarPiece { Prefab = prefab, Pos = pos, Rot = rot };
                    }
                    if (Fingerprint(arr) != print) { LastReject = "fingerprint"; return null; }
                    // Not renormalised: |q| is within ~3e-5 of 1 (invisible), and keeping the written values makes this side's print equal the
                    // server's, so the next ask's diff says "unchanged" instead of re-sending the zone.
                    return arr;
                }
            }
            catch (Exception e) { LastReject = "parse: " + e.GetType().Name; return null; }
        }

        private static short Q(float v) => (short)Mathf.Clamp(Mathf.RoundToInt(v * 32767f), -32767, 32767);
        private static float D(short v) => v / 32767f;
        private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z));

        // ── Per-world cache (#4): an index of zones with far pieces, then each zone on demand ──
        private static void LoadIndexFromCache()
        {
            if (!FarCache.Ready) return;
            s_cacheLoaded = true;
            int gen = s_indexGen;
            s_indexReadsPending = 1;   // the index itself; a scan landing before it arrives must not write a short one either
            FarCache.ReadAsync(FarCacheScope.World, "far-buildings", FarCache.Key("zone-index"), data =>
            {
                if (gen != s_indexGen) return;   // the old world's answer: the count already belongs to the new one
                if (data == null || data.Length % 4 != 0 || data.Length > 4 * 200000) { IndexReadDone(gen); return; }
                int loaded = 0;
                for (int i = 0; i < data.Length; i += 4)
                {
                    var z = new Vector2s(BitConverter.ToInt16(data, i), BitConverter.ToInt16(data, i + 2));
                    if (Math.Abs(z.x) > ZoneLimit || Math.Abs(z.y) > ZoneLimit) continue;
                    s_indexListed.Add(z);
                    if (Zones.ContainsKey(z)) continue;
                    var zone = z;
                    s_indexReadsPending++;
                    FarCache.ReadAsync(FarCacheScope.World, "far-buildings", ZoneKey(zone), bytes =>
                    {
                        IndexReadDone(gen);   // first, and on every answer: a miss and a rejected zone count too
                        if (gen != s_indexGen) return;
                        var pieces = Deserialize(bytes, zone, out _);
                        if (pieces != null && !Zones.ContainsKey(zone)) Apply(zone, pieces, fromServer: false, fromCache: true);
                    });
                    loaded++;
                }
                IndexReadDone(gen);   // the index's own read, after its zones were counted
                EasyBakeLog.Info($"{FarBuildings.Log} far cache: {loaded} zone(s) with far pieces listed for this world.");
            });
        }

        private static void WriteIndexToCache()
        {
            s_indexListed.Clear();
            foreach (var listed in Zones.Keys) s_indexListed.Add(listed);
            var bytes = new byte[Zones.Count * 4]; int i = 0;
            foreach (var z in Zones.Keys) { BitConverter.GetBytes(z.x).CopyTo(bytes, i); BitConverter.GetBytes(z.y).CopyTo(bytes, i + 2); i += 4; }
            FarCache.Write(FarCacheScope.World, "far-buildings", FarCache.Key("zone-index"), bytes);
        }

        internal static void ResetForWorld()
        {
            Zones.Clear(); s_ring.Clear(); s_ringIdx = 0; s_cacheLoaded = false; s_indexDirty = false; s_stamps.Clear();
            s_indexGen++; s_indexReadsPending = 0; s_indexListed.Clear();
            s_ringCenter = new Vector3(float.MaxValue, 0, 0);
        }
    }
}
