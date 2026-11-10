using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Far bases for clients of a dedicated server ([lead] 11-08: option (a), own keys, default off). A client only holds ZDOs
    /// near itself, so on its own it shows only the bases it has visited (its cache). With "Ask the server for far bases" on the
    /// client and "Server sends far bases to clients" on the server, the client sends the fingerprints of the zones it knows within
    /// reach; the server answers ONLY the zones that differ (new, changed, or gone), as compact piece lists (24 bytes a piece),
    /// paced a package at a time. Security (our RPC rule): the server accepts asks only from connected peers, rate-limited and
    /// size-capped; the client accepts zone lists only from the server, and every list is parsed defensively and must match its
    /// fingerprint and zone.
    /// </summary>
    internal static class FarBuildingNet
    {
        private const string RpcAsk = "FiresEBM_FarBuildingsAsk";
        private const string RpcZones = "FiresEBM_FarBuildingsZones";

        // EBM port: Core compiled against the publicized assembly_valheim and called the private ZRoutedRpc.GetServerPeerID directly;
        // EBM compiles against the plain one, so the same vanilla method is bound once by name (vanilla only, no other mod).
        private static readonly Func<ZRoutedRpc, long> s_serverPeerId =
            HarmonyLib.AccessTools.MethodDelegate<Func<ZRoutedRpc, long>>(HarmonyLib.AccessTools.Method(typeof(ZRoutedRpc), "GetServerPeerID"));
        private static long ServerPeerId() => s_serverPeerId(ZRoutedRpc.instance);
        private const int Version = 1;
        private const int MaxAskZones = 8192;
        private const int MaxReplyZones = 512;
        private const int PackageBytes = 60 * 1024;
        private const int MaxBytesPerAsk = 4 * 1024 * 1024;
        private const float AskEvery = 30f, AskMinGapPerPeer = 10f, SendGap = 0.25f;

        private static ZRoutedRpc s_registeredOn;
        private static float s_nextAsk;
        private static Vector3 s_askedAt = new Vector3(float.MaxValue, 0, 0);
        private static readonly Dictionary<long, float> s_lastAsk = new Dictionary<long, float>();
        private static readonly Dictionary<long, Queue<Vector2s>> s_outbox = new Dictionary<long, Queue<Vector2s>>();
        private static readonly Dictionary<long, int> s_sentBytes = new Dictionary<long, int>();
        private static float s_nextSend;
        private static bool s_loggedAnswering, s_loggedReceiving;
        private static string s_lastReceiveState;   // 0.2.1216

        internal static void Update()
        {
            var rpc = ZRoutedRpc.instance;
            if (rpc == null || ZNet.instance == null) return;
            if (s_registeredOn != rpc)
            {
                s_registeredOn = rpc;
                rpc.Register<ZPackage>(RpcAsk, OnAsk);
                rpc.Register<ZPackage>(RpcZones, OnZones);
                s_lastAsk.Clear(); s_outbox.Clear(); s_sentBytes.Clear();
            }

            if (ZNet.instance.IsServer()) { if (FarBuildings.ServerSends) Flush(); return; }
            if (!FarBuildings.ClientAsks || !FarRunner.Client) return;

            Vector3 here = ZNet.instance.GetReferencePosition();
            if (Time.unscaledTime < s_nextAsk && (here - s_askedAt).sqrMagnitude < 256f * 256f) return;
            s_nextAsk = Time.unscaledTime + AskEvery; s_askedAt = here;
            Ask(here);
        }

        // ── Client: "these are the zones I know within reach, with their fingerprints" ──
        private static void Ask(Vector3 here)
        {
            var pkg = new ZPackage();
            var list = new List<FarZone>();
            float reach = FarBuildings.Reach;
            foreach (var z in FarBuildingDigest.Zones.Values)
            {
                Vector3 c = ZoneSystem.GetZonePos(z.Zone);
                if (new Vector2(c.x - here.x, c.z - here.z).sqrMagnitude <= reach * reach) list.Add(z);
                if (list.Count >= MaxAskZones) break;
            }
            pkg.Write(Version); pkg.Write(list.Count);
            foreach (var z in list) { pkg.Write((int)z.Zone.x); pkg.Write((int)z.Zone.y); pkg.Write((long)z.Print); }
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeerId(), RpcAsk, pkg);
            EasyBakeLog.Debug($"{FarBuildings.Log} asked the server for far bases ({list.Count} zone(s) known within {reach:0} m).");
        }

        // ── Server: diff the client's list against ours and queue only what differs ──
        private static void OnAsk(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer() || !FarBuildings.ServerSends) return;
                var peer = ZNet.instance.GetPeer(sender);
                if (peer == null) return;                                         // only connected peers
                if (s_lastAsk.TryGetValue(sender, out float last) && Time.unscaledTime - last < AskMinGapPerPeer) return;
                s_lastAsk[sender] = Time.unscaledTime;
                if (pkg == null || pkg.Size() > 16 + MaxAskZones * 16) return;
                if (pkg.ReadInt() != Version) return;
                int n = pkg.ReadInt();
                if (n < 0 || n > MaxAskZones) return;
                var known = new Dictionary<Vector2s, ulong>(n);
                for (int i = 0; i < n; i++)
                {
                    int x = pkg.ReadInt(), y = pkg.ReadInt(); ulong print = (ulong)pkg.ReadLong();
                    if (Math.Abs(x) > FarBuildingDigest.ZoneLimit || Math.Abs(y) > FarBuildingDigest.ZoneLimit) return;
                    known[new Vector2s(x, y)] = print;
                }

                float reach = FarBuildings.Reach; Vector3 at = peer.m_refPos;
                var q = new Queue<Vector2s>();
                foreach (var z in FarBuildingDigest.Zones.Values)
                {
                    Vector3 c = ZoneSystem.GetZonePos(z.Zone);
                    if (new Vector2(c.x - at.x, c.z - at.z).sqrMagnitude > reach * reach) continue;
                    if (known.TryGetValue(z.Zone, out ulong theirs) && theirs == z.Print) { known.Remove(z.Zone); continue; }
                    known.Remove(z.Zone);
                    q.Enqueue(z.Zone);
                }
                foreach (var gone in known.Keys) q.Enqueue(gone);                  // zones they have that we know are empty now
                s_outbox[sender] = q; s_sentBytes[sender] = 0;
                if (!s_loggedAnswering) { s_loggedAnswering = true; EasyBakeLog.Info($"{FarBuildings.Log} answering clients' far-base requests (changed zones only)."); }
                EasyBakeLog.Debug($"{FarBuildings.Log} {peer.m_playerName}: {q.Count} zone(s) to send.");
            }
            catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} bad far-base ask from {sender}: {e.Message}"); }
        }

        private static void Flush()
        {
            if (s_outbox.Count == 0 || Time.unscaledTime < s_nextSend) return;
            s_nextSend = Time.unscaledTime + SendGap;
            var done = new List<long>();
            foreach (var kv in s_outbox)
            {
                long peerId = kv.Key; var q = kv.Value;
                if (ZNet.instance.GetPeer(peerId) == null || q.Count == 0 || s_sentBytes[peerId] >= MaxBytesPerAsk) { done.Add(peerId); continue; }
                var pkg = new ZPackage();
                var entries = new List<KeyValuePair<Vector2s, byte[]>>();
                int bytes = 0;
                while (q.Count > 0 && entries.Count < MaxReplyZones)
                {
                    var z = q.Peek();
                    byte[] data = FarBuildingDigest.Zones.TryGetValue(z, out var fz) ? FarBuildingDigest.Serialize(fz.Pieces, fz.Print) : new byte[0];
                    if (entries.Count > 0 && bytes + data.Length > PackageBytes) break;
                    q.Dequeue(); entries.Add(new KeyValuePair<Vector2s, byte[]>(z, data)); bytes += data.Length + 12;
                }
                pkg.Write(Version); pkg.Write(entries.Count);
                foreach (var e in entries) { pkg.Write((int)e.Key.x); pkg.Write((int)e.Key.y); pkg.Write(e.Value); }
                ZRoutedRpc.instance.InvokeRoutedRPC(peerId, RpcZones, pkg);
                s_sentBytes[peerId] += bytes;
            }
            foreach (var id in done) { s_outbox.Remove(id); s_sentBytes.Remove(id); }
        }

        // ── Client: accept zone lists from the server only ──
        private static void OnZones(long sender, ZPackage pkg)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer() || !FarBuildings.ClientAsks) return;
                if (sender != ServerPeerId()) return;
                if (pkg == null || pkg.Size() > PackageBytes + 20000 * 24 + 64 * 1024) return;
                if (pkg.ReadInt() != Version) return;
                int n = pkg.ReadInt();
                if (n < 0 || n > MaxReplyZones) return;
                int applied = 0, loaded = 0, rejected = 0; string why = null;   // 0.2.1216: the line says why a zone was not applied
                for (int i = 0; i < n; i++)
                {
                    int x = pkg.ReadInt(), y = pkg.ReadInt();
                    byte[] data = pkg.ReadByteArray();
                    if (Math.Abs(x) > FarBuildingDigest.ZoneLimit || Math.Abs(y) > FarBuildingDigest.ZoneLimit) return;
                    var zone = new Vector2s(x, y);
                    if (ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone)) { loaded++; continue; }   // our own scan owns loaded zones
                    if (data == null || data.Length == 0) { FarBuildingDigest.Apply(zone, new FarPiece[0], fromServer: true); applied++; continue; }
                    var pieces = FarBuildingDigest.Deserialize(data, zone, out _);
                    if (pieces == null) { rejected++; why = FarBuildingDigest.LastReject; continue; }   // malformed: dropped
                    FarBuildingDigest.Apply(zone, pieces, fromServer: true); applied++;
                }
                if (!s_loggedReceiving) { s_loggedReceiving = true; EasyBakeLog.Info($"{FarBuildings.Log} receiving far bases from the server."); }
                string line = $"{FarBuildings.Log} server sent {n} zone(s), {applied} applied" + (loaded > 0 ? $", {loaded} loaded here (our own scan has them)" : "") + (rejected > 0 ? $", {rejected} rejected (last: {why ?? "?"})" : "") + ".";
                // 0.2.1216 ([lead]): Info once per state change (applying / rejecting + why), Debug for every receive
                string state = rejected > 0 ? "rejecting: " + (why ?? "?") : applied > 0 ? "applying" : "nothing to apply";
                if (state != s_lastReceiveState) { s_lastReceiveState = state; EasyBakeLog.Info(line + " (receive state: " + state + ")"); }
                else EasyBakeLog.Debug(line);
            }
            catch (Exception e) { EasyBakeLog.Debug($"{FarBuildings.Log} bad far-base list from the server: {e.Message}"); }
        }
    }
}
