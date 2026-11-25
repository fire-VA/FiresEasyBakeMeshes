using System.Collections.Concurrent;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.446 ([visual], Fire 11-18: "EBM should be self-contained"): EBM's own port of FAT's seen-trees rule (FAT FarBridge.cs:82-194, 0.2.1417 +
    /// 0.2.1453), "the real tree wins". Every zone the local player has loaded is read from its real ZDOs of the far kinds; those trees REPLACE
    /// the replay's trees for that zone for the rest of the session (felled trees stay gone, planted ones appear). A read is COMMITTED only when
    /// the zone is complete: loaded, no arrival in the last Settle s, and every creatable ZDO created (vanilla IsAreaReady's test) OR two reads
    /// >= Reread s apart match. Removal PROOF (a stump / log of a far kind's TreeBase, or a count drop seen while loaded) lets a sparse read win;
    /// without proof the runner keeps the replay where the read is far below it (FarVegRunner.MergeZone). Re-read every 10 s; dirty cells are
    /// re-sent by the runner at most once per 2 s; ImpostorEngine.MarkZoneStanding(zone, true) after each commit.
    /// Threads: Observe / Start / Clear = main thread. TryGet = any thread (ConcurrentDictionary; a SeenZone is immutable once stored).
    /// </summary>
    internal static class FarVegSeen
    {
        internal sealed class SeenZone { public FarVegInstance[] Trees; public bool Proven; }

        internal const float ZoneSize = 64f, Settle = 5f, Reread = 10f, PendingReread = 1f;
        internal const float SparseFraction = 0.5f; internal const int SparseSlack = 3;

        private static readonly ConcurrentDictionary<long, SeenZone> s_seen = new ConcurrentDictionary<long, SeenZone>();
        private static readonly Dictionary<long, float> s_zoneFirst = new Dictionary<long, float>(), s_zoneNextRead = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> s_commitEpoch = new Dictionary<long, float>();
        private static readonly Dictionary<long, (FarVegInstance[] Trees, float At)> s_pending = new Dictionary<long, (FarVegInstance[], float)>();
        private static readonly List<ZDO> s_zdos = new List<ZDO>();
        private static readonly List<FarVegInstance> s_read = new List<FarVegInstance>();
        private static HashSet<int> s_proofHashes = new HashSet<int>();
        private static float s_arrivalAt;
        internal static int ZonesSeen, ZoneReads, ZoneChanges, DeferUncreated, DeferArrival, StableCommits, ProvenZones;

        /// <summary>Changed zones (zone keys) the runner turns into cell re-sends. Main thread only.</summary>
        internal static readonly HashSet<long> DirtyZones = new HashSet<long>();

        internal static long ZoneKey(int zx, int zz) => ((long)zx << 32) | (uint)zz;
        internal static void Unkey(long k, out int x, out int z) { x = (int)(k >> 32); z = (int)(uint)k; }
        internal static int ZoneOf(float w) => Mathf.FloorToInt((w + ZoneSize * 0.5f) / ZoneSize);   // vanilla ZoneSystem.GetZone
        internal static int Count => s_seen.Count;

        internal static bool TryGet(long zoneKey, out SeenZone z) => s_seen.TryGetValue(zoneKey, out z);

        /// <summary>Main thread, after FarVegKinds.Build: removal proof = the stumps / logs the far kinds' own TreeBase leaves (FAT FarBridge.cs:415-431).</summary>
        internal static int Start()
        {
            var proof = new HashSet<int>();
            for (int k = 0; k < FarVegKinds.Count; k++)
            {
                var prefab = FarVegKinds.Prefab(k);
                var tb = prefab != null ? prefab.GetComponent<TreeBase>() : null;
                if (tb == null) continue;
                if (tb.m_stubPrefab != null) proof.Add(tb.m_stubPrefab.name.GetStableHashCode());
                var log = tb.m_logPrefab;
                for (int depth = 0; log != null && depth < 4; depth++)
                {
                    if (!proof.Add(log.name.GetStableHashCode())) break;
                    var tl = log.GetComponent<TreeLog>();
                    log = tl != null ? tl.m_subLogPrefab : null;
                }
            }
            proof.RemoveWhere(h => FarVegKinds.KindOfPrefabHash(h) >= 0);
            s_proofHashes = proof;
            return proof.Count;
        }

        /// <summary>Main thread: forget everything (world closed). A key toggle keeps the records (they are real trees, as FAT's Restart did).</summary>
        internal static void Clear()
        {
            s_seen.Clear(); s_zoneFirst.Clear(); s_zoneNextRead.Clear(); s_commitEpoch.Clear(); s_pending.Clear(); DirtyZones.Clear();
            ZonesSeen = ZoneReads = ZoneChanges = DeferUncreated = DeferArrival = StableCommits = ProvenZones = 0;
        }

        /// <summary>Main thread, once a second: the 5 x 5 zones around the player (FAT FarBridge.cs:117-137). Cost: at most 25 sector reads, each a
        /// walk of that sector's ZDO list; only zones due a read (every 10 s, or 1 s while pending) are walked.</summary>
        internal static void Observe()
        {
            var zs = ZoneSystem.instance; var zdm = ZDOMan.instance; var lp = Player.m_localPlayer;
            float now = Time.realtimeSinceStartup;
            if (lp == null || lp.IsTeleporting()) { s_arrivalAt = now; return; }   // FAT FarBridge.cs:383
            if (zs == null || zdm == null || FarVegKinds.Count == 0) return;
            if (!zs.IsActiveAreaLoaded()) s_arrivalAt = now;
            if (now - s_arrivalAt < Settle) { DeferArrival++; return; }
            Vector3 c = lp.transform.position;
            int czx = ZoneOf(c.x), czz = ZoneOf(c.z), r = 2;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var z = new Vector2s(czx + dx, czz + dz);
                    long zk = ZoneKey(z.x, z.y);
                    if (!zs.IsZoneLoaded(z)) { s_zoneFirst.Remove(zk); s_pending.Remove(zk); continue; }
                    if (!s_zoneFirst.TryGetValue(zk, out float t0)) { s_zoneFirst[zk] = now; continue; }
                    if (now - t0 < Settle) continue;
                    if (s_zoneNextRead.TryGetValue(zk, out float next) && now < next) continue;
                    s_zoneNextRead[zk] = now + Reread;
                    ReadZone(z, zk, zdm, now, t0);
                }
        }

        private static void ReadZone(Vector2s z, long zk, ZDOMan zdm, float now, float loadEpoch)
        {
            s_zdos.Clear(); s_read.Clear();
            zdm.FindSectorObjects(z, new SimulationDistance(0, 0), s_zdos);   // (0, 0): this sector's own objects only
            var scene = ZNetScene.instance;
            int uncreated = 0, proof = 0;
            foreach (var zdo in s_zdos)
            {
                if (zdo == null) continue;
                int ph = zdo.GetPrefab();
                if (ph != 0 && !zdo.Created && scene != null && scene.HasPrefab(ph)) uncreated++;
                int kind = FarVegKinds.KindOfPrefabHash(ph);
                bool tree = kind >= 0;
                if (!tree && !s_proofHashes.Contains(ph)) continue;
                Vector3 p = zdo.GetPosition();
                if (ZoneOf(p.x) != z.x || ZoneOf(p.z) != z.y) continue;
                if (!tree) { proof++; continue; }
                Vector3 sc = zdo.GetVec3(ZDOVars.s_scaleHash, Vector3.one);
                s_read.Add(new FarVegInstance { Kind = kind, Pos = p, Yaw = zdo.GetRotation().eulerAngles.y, Scale = sc.x > 0f ? sc.x : 1f });
            }
            ZoneReads++;
            var arr = s_read.ToArray();
            if (uncreated > 0)
            {
                if (s_pending.TryGetValue(zk, out var pend) && Same(pend.Trees, arr))
                {
                    if (now - pend.At < Reread) { DeferUncreated++; s_zoneNextRead[zk] = now + PendingReread; return; }
                    StableCommits++;
                }
                else { s_pending[zk] = (arr, now); DeferUncreated++; s_zoneNextRead[zk] = now + PendingReread; return; }
            }
            s_pending.Remove(zk);
            bool had = s_seen.TryGetValue(zk, out var old);
            bool proven = proof > 0 || (had && old.Proven);
            if (!proven && had && arr.Length < old.Trees.Length && s_commitEpoch.TryGetValue(zk, out float ep) && ep == loadEpoch) proven = true;
            s_commitEpoch[zk] = loadEpoch;
            if (had && old.Proven == proven && Same(old.Trees, arr)) return;
            if (proven && (!had || !old.Proven)) ProvenZones++;
            s_seen[zk] = new SeenZone { Trees = arr, Proven = proven };
            if (!had) ZonesSeen++; else ZoneChanges++;
            ImpostorEngine.MarkZoneStanding(z, true);
            DirtyZones.Add(zk);
        }

        private static bool Same(FarVegInstance[] a, FarVegInstance[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i].Kind != b[i].Kind || (a[i].Pos - b[i].Pos).sqrMagnitude > 0.0001f) return false;
            return true;
        }
    }
}
