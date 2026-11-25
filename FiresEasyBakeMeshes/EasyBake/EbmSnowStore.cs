using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.206 ([wishbone] for [visual], Fire 11-08: "ebm cannot rely on any other mod"): snow values PUSHED into EBM by whichever mod owns
    // snow (Vedr builds against EbmApi; EBM never calls it). EBM announces what it bakes (EbmApi.ZoneBaked / ZoneUnloaded); the snow owner
    // answers per piece with EbmApi.SetPieceSnow. Values reach both draw paths:
    //   - the instanced path: ZoneInstanceGroup.EnsureSnow takes a stored value before computing its own (re-split on change);
    //   - the indirect path: ZoneInstanceGroup.LookOf returns it first (IndirectLooks.WriteGroup re-runs on the group's ExtGen).
    // Bounded: only pieces an ANNOUNCED zone's instance groups draw can be stored (anything else answers false); a zone's entries go
    // when it unloads or is torn down; everything goes on ZoneTracker.Reset. Main thread only. No per-frame work.
    internal static class EbmSnowStore
    {
        private sealed class Zone
        {
            public readonly List<MeshBaker.PieceIdentity> Ids = new List<MeshBaker.PieceIdentity>();   // what this zone's groups draw
            public readonly List<MeshBaker.PieceIdentity> Stored = new List<MeshBaker.PieceIdentity>(); // what has a value now
            public readonly List<ZoneInstanceGroup> Groups = new List<ZoneInstanceGroup>();
            public float AnnouncedAt;
        }

        // piece -> the instance group drawing it (only announced zones), and the zone that announced it
        private static readonly Dictionary<MeshBaker.PieceIdentity, (ZoneInstanceGroup group, Vector2s zone)> s_owner =
            new Dictionary<MeshBaker.PieceIdentity, (ZoneInstanceGroup, Vector2s)>();
        private static readonly Dictionary<MeshBaker.PieceIdentity, Vector2> s_snow = new Dictionary<MeshBaker.PieceIdentity, Vector2>();   // x snow, y wet
        private static readonly Dictionary<MeshBaker.PieceIdentity, (float level, Vector4 edges, Vector4 nbr)> s_caps =
            new Dictionary<MeshBaker.PieceIdentity, (float, Vector4, Vector4)>();
        private static readonly Dictionary<Vector2s, Zone> s_zones = new Dictionary<Vector2s, Zone>();
        private static int s_set, s_unknown, s_announced, s_unloaded;
        private static float s_nextLog;

        internal static int Pieces => s_snow.Count;

        /// <summary>A zone's bake is Ready: register what its instance groups draw. Returns their ZDOIDs for EbmApi.ZoneBaked
        /// (resolved from the zone's ZDO list, which ZoneTracker already keeps). Once per bake.</summary>
        internal static ZDOID[] Announce(Vector2s coord, MeshBaker.BakeResult bake, Dictionary<MeshBaker.PieceIdentity, ZDO> liveZdos)
        {
            s_pending.Remove(coord);
            // a re-announce (a piece built / removed, the zone back in range, a rebake) keeps the values of pieces it still draws,
            // so nothing flickers back to plain while the snow owner answers the new list
            s_keep.Clear();
            if (s_zones.TryGetValue(coord, out var old))
            {
                foreach (var id in old.Stored) if (s_snow.TryGetValue(id, out var v)) s_keep[id] = v;
                s_keepShape.Clear();   // 1.2.437: shape values ride a re-announce the same way
                foreach (var id in old.Ids) if (s_shape.TryGetValue(id, out var sv)) s_keepShape[id] = sv;
                s_keepVariant.Clear();   // 1.2.508: cap variant overrides ride a re-announce too (a tilted piece never flips back to the base cap while the owner re-answers)
                foreach (var id in old.Ids) if (s_capVariant.TryGetValue(id, out var cv)) s_keepVariant[id] = cv;
                s_keepCapLevel.Clear();   // 1.2.510: cap levels ride a re-announce the same way
                foreach (var id in old.Ids) if (s_capLevel.TryGetValue(id, out var cl)) s_keepCapLevel[id] = cl;
                s_keepJoin.Clear();   // 1.2.512: cap joins ride a re-announce too (they were the one cap value dropped at every re-announce)
                foreach (var id in old.Ids) if (s_caps.TryGetValue(id, out var cj)) s_keepJoin[id] = cj;
                Unload(coord, false);
            }
            var zone = new Zone { AnnouncedAt = Time.unscaledTime };
            s_holdEnd[coord] = Time.unscaledTime + HoldSeconds;   // 1.2.260 WaitForPushedSnow
            s_zones[coord] = zone;
            var groups = bake?.InstanceGroups;
            if (groups != null)
                foreach (var g in groups)
                {
                    if (g == null) continue;
                    zone.Groups.Add(g);
                    s_groupZone[g] = coord;
                    var ids = g.Identities;
                    for (int i = 0; i < ids.Count; i++) { s_owner[ids[i]] = (g, coord); zone.Ids.Add(ids[i]); }
                }
            foreach (var kv in s_keep)
                if (s_owner.TryGetValue(kv.Key, out var o) && o.zone.Equals(coord) && o.group != null)
                {
                    s_snow[kv.Key] = kv.Value; zone.Stored.Add(kv.Key);
                    o.group.ExtCount++; o.group.ExternalLooksChanged();
                }
            s_keep.Clear();
            foreach (var kv in s_keepShape)
                if (s_owner.TryGetValue(kv.Key, out var o) && o.zone.Equals(coord) && o.group != null && !s_shape.ContainsKey(kv.Key))
                { s_shape[kv.Key] = kv.Value; o.group.ShapeCount++; o.group.ShapeLooksChanged(); }
            s_keepShape.Clear();
            foreach (var kv in s_keepVariant)   // 1.2.508
                if (s_owner.TryGetValue(kv.Key, out var o) && o.zone.Equals(coord) && o.group != null && !s_capVariant.ContainsKey(kv.Key)) s_capVariant[kv.Key] = kv.Value;
            foreach (var kv in s_keepCapLevel)   // 1.2.510
                if (s_owner.TryGetValue(kv.Key, out var o) && o.zone.Equals(coord) && o.group != null && !s_capLevel.ContainsKey(kv.Key)) s_capLevel[kv.Key] = kv.Value;
            foreach (var kv in s_keepJoin)   // 1.2.512
                if (s_owner.TryGetValue(kv.Key, out var o) && o.zone.Equals(coord) && o.group != null && !s_caps.ContainsKey(kv.Key)) s_caps[kv.Key] = kv.Value;
            RestoreParked(coord);   // 1.2.512: values parked when this zone last left the loaded area, or when a piece of it went live
            ParkLeavers(coord);     // 1.2.512: pieces this re-announce no longer draws (went live / handed back): their kept cap values wait for their return
            s_keepJoin.Clear(); s_keepVariant.Clear(); s_keepCapLevel.Clear();
            s_announced++;
            foreach (var g in zone.Groups) if (g != null) g.CapAnnounceGen++;   // 1.2.507: this zone's cap lists only (was one global count: every re-announce anywhere rebuilt every list)
            if (zone.Ids.Count == 0 || liveZdos == null) return Array.Empty<ZDOID>();
            var list = new List<ZDOID>(zone.Ids.Count);
            foreach (var id in zone.Ids) if (liveZdos.TryGetValue(id, out var zdo) && zdo != null) list.Add(zdo.m_uid);
            return list.ToArray();
        }

        /// <summary>The zone left the loaded area or was torn down: drop its entries. raise = the caller raises EbmApi.ZoneUnloaded.</summary>
        internal static bool Unload(Vector2s coord, bool count = true)
        {
            if (!s_zones.TryGetValue(coord, out var zone)) return false;
            if (count) Park(coord, zone);   // 1.2.512: a real unload (not a re-announce): this zone's cap values wait for its return
            // stored values first, while the owner map still names their groups: each group gives its count back and re-splits plain
            foreach (var id in zone.Stored)
            {
                if (!s_snow.Remove(id)) continue;
                if (s_owner.TryGetValue(id, out var o) && o.group != null && o.group.ExtCount > 0) { o.group.ExtCount--; o.group.ExternalLooksChanged(); }
            }
            foreach (var id in zone.Ids)
            {
                // 1.2.437: shape values go with the zone (no ShapeGen bump: the group is leaving or being re-announced)
                if (s_shape.Remove(id) && s_owner.TryGetValue(id, out var so) && so.group != null && so.group.ShapeCount > 0) so.group.ShapeCount--;
                if (s_owner.TryGetValue(id, out var o) && o.zone.Equals(coord)) s_owner.Remove(id);
                s_caps.Remove(id);
                s_capVariant.Remove(id);   // 1.2.508
                s_capLevel.Remove(id);   // 1.2.510
            }
            foreach (var g in zone.Groups) if (s_groupZone.TryGetValue(g, out var gz) && gz.Equals(coord)) s_groupZone.Remove(g);
            s_zones.Remove(coord);
            s_holdEnd.Remove(coord);
            if (count) { s_unloaded++; s_pending.Remove(coord); }
            foreach (var g in zone.Groups) if (g != null) g.CapAnnounceGen++;   // 1.2.507: this zone's cap lists only
            return true;
        }

        internal static void Clear()
        {
            ClearGen++;   // 1.2.507: everything dropped: every cap list rebuilds (rare: world reset)
            s_owner.Clear(); s_snow.Clear(); s_shape.Clear(); s_keepShape.Clear(); s_caps.Clear(); s_capVariant.Clear(); s_keepVariant.Clear(); s_capLevel.Clear(); s_keepCapLevel.Clear(); s_keepJoin.Clear(); s_parked.Clear(); s_parkedEntries = 0; s_zones.Clear(); s_groupZone.Clear(); s_pending.Clear(); s_holdEnd.Clear();
        }

        // ── re-announce after a piece is built / removed in an announced group ([visual] 11-08): at most once per zone per second ──
        private const float ReannounceSeconds = 1f;
        private static readonly Dictionary<ZoneInstanceGroup, Vector2s> s_groupZone = new Dictionary<ZoneInstanceGroup, Vector2s>();
        private static readonly Dictionary<Vector2s, float> s_pending = new Dictionary<Vector2s, float>();   // zone -> when it is due
        private static readonly Dictionary<MeshBaker.PieceIdentity, Vector2> s_keep = new Dictionary<MeshBaker.PieceIdentity, Vector2>();
        private static readonly List<Vector2s> s_dueScratch = new List<Vector2s>();

        /// <summary>ZoneInstanceGroup.AddLate / TryRemove. Unannounced groups (most of the time: no snow owner listening) cost one lookup.</summary>
        internal static void GroupMembershipChanged(ZoneInstanceGroup group)
        {
            if (group == null || s_groupZone.Count == 0 || !s_groupZone.TryGetValue(group, out var coord) || s_pending.ContainsKey(coord)) return;
            float last = s_zones.TryGetValue(coord, out var z) ? z.AnnouncedAt : 0f;
            s_pending[coord] = Mathf.Max(Time.unscaledTime, last + ReannounceSeconds);
        }

        /// <summary>ZoneTracker, once a frame: the zones whose re-announce is due (an empty dictionary check otherwise).</summary>
        internal static List<Vector2s> TakeDue(float now)
        {
            s_dueScratch.Clear();
            if (s_pending.Count == 0) return s_dueScratch;
            foreach (var kv in s_pending) if (kv.Value <= now) s_dueScratch.Add(kv.Key);
            for (int i = 0; i < s_dueScratch.Count; i++) s_pending.Remove(s_dueScratch[i]);
            return s_dueScratch;
        }

        /// <summary>EbmApi.SetPieceSnow. False = EBM draws no instance of that piece (not loaded, not baked, or in a combined mesh).</summary>
        internal static bool SetPiece(ZDOID id, float snowCover, float wetScale)
        {
            if (!TryOwner(id, out var key, out var owner)) { s_unknown++; Summary(); return false; }
            var v = new Vector2(Mathf.Clamp01(snowCover), Mathf.Clamp01(wetScale));
            s_lastPushAt = Time.unscaledTime;   // 1.2.464: the owner is still answering (an unchanged value counts too)
            bool had = s_snow.TryGetValue(key, out var old);
            if (had && old == v) return true;
            s_snow[key] = v; s_pusherSeen = true;
            if (!had)
            {
                owner.group.ExtCount++;
                if (s_zones.TryGetValue(owner.zone, out var zone)) zone.Stored.Add(key);
            }
            owner.group.ExternalLooksChanged();
            s_set++; s_amountPushes++;   // 1.2.437: [Snow] shape mode line
            EbmChurn.NotePush();   // 1.2.480
            Summary();
            return true;
        }

        /// <summary>EbmApi.SetCapSnow: stored only (snow caps are not drawn by EBM yet).</summary>
        internal static bool SetCap(ZDOID id, float level, Vector4 edges, Vector4 neighbours)
        {
            if (!TryOwner(id, out var key, out var owner)) return false;
            // 1.2.505: the cap draw (PieceCaps) forwards edges / neighbours. 1.2.507: a changed join bumps only its group's CapJoinGen (a values refresh of that list)
            if (s_caps.TryGetValue(key, out var old) && old.edges == edges && old.nbr == neighbours) { s_caps[key] = (level, edges, neighbours); return true; }
            s_caps[key] = (level, edges, neighbours);
            owner.group.CapJoinGen++;
            return true;
        }

        // ── 1.2.505 ([visual], piece snow caps v1, agreed [seasons] / [2ic] / [lead] 11-23): what PieceCaps reads ──
        // 1.2.507 ([lead] proof34: 65,071 cap list rebuilds in 60 s): the per-change counts live on the groups (ZoneInstanceGroup.CapJoinGen / CapAnnounceGen),
        // so a change rebuilds only the lists of that group; ClearGen (Clear: world reset) is the one global. Main thread only.
        internal static int ClearGen;

        // 1.2.508 ([visual] + [seasons] .41 tilted caps): per-piece cap variant (EbmApi.SetPieceCapOverride): 0 / absent = the prefab's base cap, 1..32 = that variant,
        // -1 = no cap for this piece. Stored for owned pieces only, kept across a re-announce, dropped with the zone / Clear. A change bumps only its group's
        // CapVariantGen (a structural rebuild of that list, the same frame). Main thread only.
        private static readonly Dictionary<MeshBaker.PieceIdentity, int> s_capVariant = new Dictionary<MeshBaker.PieceIdentity, int>();
        private static readonly Dictionary<MeshBaker.PieceIdentity, int> s_keepVariant = new Dictionary<MeshBaker.PieceIdentity, int>();
        internal static bool SetCapVariant(ZDOID id, int variant)
        {
            if (variant < -1 || variant > 32) return false;
            if (!TryOwner(id, out var key, out var owner)) return false;
            int old = s_capVariant.TryGetValue(key, out var ov) ? ov : 0;
            if (old == variant) return true;
            if (variant == 0) s_capVariant.Remove(key); else s_capVariant[key] = variant;
            owner.group.CapVariantGen++;
            return true;
        }
        // 1.2.510 ([seasons] .46: roof rows share one snow amount per touching group, each body keeps its own shelter): EbmApi.SetPieceCapLevel. The CAP's own
        // level, encoded as SetPieceSnowShape (clamped 0..3); it replaces the shape value for that piece's cap _SnowLevel only (the body keeps the shape).
        // < 0 clears it. Owned pieces only; kept across a re-announce; dropped with the zone / Clear. A change bumps the group's CapJoinGen (a values refresh,
        // <= 2 / s, no rebuild). It never makes a cap appear by itself: a piece still needs a shape value to get a cap.
        private static readonly Dictionary<MeshBaker.PieceIdentity, float> s_capLevel = new Dictionary<MeshBaker.PieceIdentity, float>();
        private static readonly Dictionary<MeshBaker.PieceIdentity, float> s_keepCapLevel = new Dictionary<MeshBaker.PieceIdentity, float>();
        internal static bool SetCapLevel(ZDOID id, float encoded)
        {
            if (!TryOwner(id, out var key, out var owner)) return false;
            bool had = s_capLevel.TryGetValue(key, out var old);
            if (float.IsNaN(encoded) || encoded < 0f) { if (!had) return true; s_capLevel.Remove(key); owner.group.CapJoinGen++; return true; }
            float v = Mathf.Min(encoded, 3f);
            if (had && old == v) return true;
            s_capLevel[key] = v;
            owner.group.CapJoinGen++;
            return true;
        }
        internal static bool TryGetCapLevel(MeshBaker.PieceIdentity id, out float level) { if (s_capLevel.Count > 0 && s_capLevel.TryGetValue(id, out level)) return true; level = 0f; return false; }
        // ── 1.2.512 ([lead] / [seasons] psnow51: "18,709 same-place re-adds revived" a minute in town = EBM zones leaving and re-entering the loaded area
        // ([ZoneFlip] UNLOAD / RELOAD at its edge), each a ZoneUnloaded + ZoneBaked of the whole zone, after which Vedr re-sent join + level for every piece).
        // EBM now keeps a piece's CAP values (join edges / neighbours, cap level, variant override) across both: a re-announce (s_keep* above) and a real
        // unload -> the zone's values are PARKED by coord and restored at its next announce for the pieces it still draws (same identity = same piece at the
        // same place); a piece that is gone is simply not restored. Bounded: parked zones older than ParkSeconds and the oldest beyond ParkCap entries are
        // dropped; InvalidateSnow and Clear drop all. Shape / snow values are NOT parked (they follow the season; the owner re-answers them as before).
        private sealed class Parked { public float At; public readonly List<(MeshBaker.PieceIdentity id, (float level, Vector4 edges, Vector4 nbr) join, bool hasJoin, int variant, float capLevel, bool hasLevel)> Items = new List<(MeshBaker.PieceIdentity, (float, Vector4, Vector4), bool, int, float, bool)>(); }
        private static readonly Dictionary<Vector2s, Parked> s_parked = new Dictionary<Vector2s, Parked>();
        private static readonly Dictionary<MeshBaker.PieceIdentity, (float level, Vector4 edges, Vector4 nbr)> s_keepJoin = new Dictionary<MeshBaker.PieceIdentity, (float, Vector4, Vector4)>();
        private static int s_parkedEntries;
        private static float s_unrestoredAt;
        private const float ParkSeconds = 600f;
        private const int ParkCap = 300000;
        internal static int KeptPieces, KeptZones, ParkDropped;   // PieceCaps' 60 s line reads and resets these
        internal static int ParkedNow => s_parkedEntries;
        private static readonly List<Vector2s> s_parkScratch = new List<Vector2s>();

        private static void Park(Vector2s coord, Zone zone)
        {
            Parked p = null;
            foreach (var id in zone.Ids)
            {
                bool hj = s_caps.TryGetValue(id, out var j);
                int v = s_capVariant.TryGetValue(id, out var vv) ? (vv > 0 ? vv : 0) : 0;   // [2ic] MUST: -1 is the owner's derived state (buried / tilted / pending), re-derived after a return: never parked
                bool hl = s_capLevel.TryGetValue(id, out var lv);
                if (!hj && v == 0 && !hl) continue;
                if (p == null) p = new Parked { At = Time.unscaledTime };
                p.Items.Add((id, j, hj, v, lv, hl));
            }
            if (s_parked.TryGetValue(coord, out var had))
            {   // pieces of this zone parked earlier while live (ParkLeavers) stay parked unless the zone's own values replace them
                s_parkedEntries -= had.Items.Count; s_parked.Remove(coord);
                s_leaverSeen.Clear();
                if (p != null) foreach (var it in p.Items) s_leaverSeen.Add(it.id);
                foreach (var it in had.Items) if (!s_leaverSeen.Contains(it.id)) { if (p == null) p = new Parked { At = had.At }; p.Items.Add(it); }
                s_leaverSeen.Clear();
            }
            if (p == null) return;
            s_parked[coord] = p; s_parkedEntries += p.Items.Count;
            ParkBounds(coord);
        }

        // At a re-announce: pieces whose values were kept (s_keep*) but which this announce no longer draws (handed back / went live) -> parked under the
        // zone, restored when they are baked into it again (RestoreParked at a later announce). The variant rule is Park's: 1..32 only. Called after
        // RestoreParked, so the coord's earlier parked set is already consumed; anything not restored by it goes back into the new set.
        private static void ParkLeavers(Vector2s coord)
        {
            Parked p = null;
            void Add(MeshBaker.PieceIdentity id)
            {
                if (s_owner.TryGetValue(id, out var o) && o.zone.Equals(coord) && o.group != null) return;   // drawn by this announce: already restored
                bool hj = s_keepJoin.TryGetValue(id, out var j);
                int v = s_keepVariant.TryGetValue(id, out var vv) && vv > 0 ? vv : 0;
                bool hl = s_keepCapLevel.TryGetValue(id, out var lv);
                if (!hj && v == 0 && !hl) return;
                if (p == null) p = new Parked { At = Time.unscaledTime };
                p.Items.Add((id, j, hj, v, lv, hl));
            }
            s_leaverSeen.Clear();
            foreach (var id in s_keepJoin.Keys) if (s_leaverSeen.Add(id)) Add(id);
            foreach (var id in s_keepVariant.Keys) if (s_leaverSeen.Add(id)) Add(id);
            foreach (var id in s_keepCapLevel.Keys) if (s_leaverSeen.Add(id)) Add(id);
            // parked items this announce did not restore (the piece is still live): parked again, keeping their first park time (the 600 s bound counts from it)
            float firstAt = float.MaxValue;
            foreach (var it in s_unrestored)
            {
                if (!s_leaverSeen.Add(it.id)) continue;   // a fresher kept value for the same piece won above
                if (s_owner.TryGetValue(it.id, out var o) && o.zone.Equals(coord) && o.group != null) continue;
                if (p == null) p = new Parked { At = Time.unscaledTime };
                p.Items.Add(it);
                firstAt = Mathf.Min(firstAt, s_unrestoredAt);
            }
            s_unrestored.Clear();
            s_leaverSeen.Clear();
            if (p == null) return;
            if (firstAt < p.At) p.At = firstAt;
            if (s_parked.TryGetValue(coord, out var had)) { had.Items.AddRange(p.Items); had.At = p.At; s_parkedEntries += p.Items.Count; }
            else { s_parked[coord] = p; s_parkedEntries += p.Items.Count; }
            ParkBounds(coord);
        }
        private static readonly HashSet<MeshBaker.PieceIdentity> s_leaverSeen = new HashSet<MeshBaker.PieceIdentity>();
        private static readonly List<(MeshBaker.PieceIdentity id, (float level, Vector4 edges, Vector4 nbr) join, bool hasJoin, int variant, float capLevel, bool hasLevel)> s_unrestored = new List<(MeshBaker.PieceIdentity, (float, Vector4, Vector4), bool, int, float, bool)>();

        // bounds: old zones first, then the oldest while over the cap (never the coord just parked)
        private static void ParkBounds(Vector2s coord)
        {
            float now = Time.unscaledTime;
            s_parkScratch.Clear();
            foreach (var kv in s_parked) if (now - kv.Value.At > ParkSeconds) s_parkScratch.Add(kv.Key);
            for (int i = 0; i < s_parkScratch.Count; i++) { s_parkedEntries -= s_parked[s_parkScratch[i]].Items.Count; s_parked.Remove(s_parkScratch[i]); ParkDropped++; }
            while (s_parkedEntries > ParkCap && s_parked.Count > 1)
            {
                Vector2s oldest = coord; float oldestAt = float.MaxValue;
                foreach (var kv in s_parked) if (kv.Value.At < oldestAt && !kv.Key.Equals(coord)) { oldestAt = kv.Value.At; oldest = kv.Key; }
                if (oldest.Equals(coord)) break;
                s_parkedEntries -= s_parked[oldest].Items.Count; s_parked.Remove(oldest); ParkDropped++;
            }
        }

        private static void RestoreParked(Vector2s coord)
        {
            s_unrestored.Clear();
            if (!s_parked.TryGetValue(coord, out var p)) return;
            s_parked.Remove(coord); s_parkedEntries -= p.Items.Count;
            s_unrestoredAt = p.At;
            int n = 0;
            foreach (var it in p.Items)
            {
                if (!s_owner.TryGetValue(it.id, out var o) || !o.zone.Equals(coord) || o.group == null) { s_unrestored.Add(it); continue; }   // not drawn by this announce: ParkLeavers parks it again (still live), or the bounds drop it
                bool any = false;
                if (it.hasJoin && !s_caps.ContainsKey(it.id)) { s_caps[it.id] = it.join; any = true; }
                if (it.variant > 0 && !s_capVariant.ContainsKey(it.id)) { s_capVariant[it.id] = it.variant; any = true; }   // variants 1..32 only ([2ic] MUST)
                if (it.hasLevel && !s_capLevel.ContainsKey(it.id)) { s_capLevel[it.id] = it.capLevel; any = true; }
                if (any) n++;
            }
            if (n > 0) { KeptPieces += n; KeptZones++; }
        }

        internal static int CapVariantOf(MeshBaker.PieceIdentity id) => s_capVariant.Count > 0 && s_capVariant.TryGetValue(id, out var v) ? v : 0;
        internal static bool TryGetCapJoin(MeshBaker.PieceIdentity id, out Vector4 edges, out Vector4 neighbours)
        {
            if (s_caps.TryGetValue(id, out var v)) { edges = v.edges; neighbours = v.nbr; return true; }
            edges = Vector4.zero; neighbours = Vector4.zero; return false;
        }

        internal static bool TryGet(MeshBaker.PieceIdentity id, out Vector2 v) => s_snow.TryGetValue(id, out v);

        // ── 1.2.260 (Fire 11-10, Raven's Rest in Summer: the settlement loaded fully white, clean seconds later) ──────────────────────
        // A baked piece's value comes from the snow owner's per-piece answer (SetPieceSnow) once it arrives; until then the group used
        // the source's position-only cover, which is not what the real piece shows (the owner's near path caps it per piece). Now, once
        // a per-piece owner has answered this session, a piece of a freshly announced zone with no answer yet draws PLAIN for up to
        // HoldSeconds, then the computed cover takes over (an owner that never answers costs at most that delay). [Snow] WaitForPushedSnow,
        // default ON (a fix). On EbmApi.InvalidateSnow every pushed value is dropped and every announced zone re-announced, so the owner
        // pushes current values instead of old ones being kept (Announce keeps a zone's values on a re-announce, by design otherwise).
        internal static BepInEx.Configuration.ConfigEntry<bool> WaitForPushed;
        private const float HoldSeconds = 3f;
        // 1.2.464 ([visual], lead 11-19 G11c (rig path) first arrival at the Mountain platform, floor WHITE at +2/+10 s,
        // stone at +30 s; client.log:19688 "waiting for answers: 22070 plain draw(s), 34 zone(s) timed out to the area amount" with :19294 "cover at
        // the player 1.00", while Vedr was still answering (:19690 "584 first exposure test(s) deferred to the next sweep by the per-frame cap"; 986
        // values set that window). The 3 s hold ran out mid-answer and every unanswered piece drew the area amount (full Mountain snow) until its
        // answer came. Now the hold runs on while the owner keeps answering (a SetPieceSnow within OwnerQuietSeconds), up to MaxHoldSeconds from the
        // announce; only an owner gone quiet (or the cap) lets the area amount in, as before.
        private const float OwnerQuietSeconds = 5f;
        private const float MaxHoldSeconds = 60f;
        private static float s_lastPushAt = -1000f;
        private static int w_holdExtended, w_holdQuiet, w_holdCapped;
        private static bool OwnerAnswering(float now) => now - s_lastPushAt < OwnerQuietSeconds;
        private static bool s_pusherSeen;
        private static readonly Dictionary<Vector2s, float> s_holdEnd = new Dictionary<Vector2s, float>();
        private static readonly List<Vector2s> s_holdScratch = new List<Vector2s>();
        private static int w_held, w_holdTimedOut, w_dropped;

        internal static void Bind(BepInEx.Configuration.ConfigFile config)
        {
            WaitForPushed = EbmConfigMove.Bind(config, "08 - Snow And Seasons", "WaitForPushedSnow", true,
                "A baked building whose snow mod answers per piece draws plain for up to 3 s until that answer arrives, instead of the area's snow amount (which can show snow the real piece does not have). A fix.");
        }

        /// <summary>True = draw this piece plain for now (its zone was just announced, a per-piece owner exists, no answer yet).</summary>
        internal static bool HoldPlain(MeshBaker.PieceIdentity id)
        {
            if (!s_pusherSeen || (WaitForPushed != null && !WaitForPushed.Value) || s_holdEnd.Count == 0) return false;
            if (!s_owner.TryGetValue(id, out var o) || !s_holdEnd.TryGetValue(o.zone, out float end)) return false;
            // 1.2.464: PumpHolds removes the zone's entry when the hold really ends; until then (past the 3 s, owner still answering) it stays plain
            if (Time.unscaledTime >= end && !OwnerAnswering(Time.unscaledTime)) return false;
            w_held++;
            return true;
        }

        /// <summary>ZoneTracker, once a frame: zones whose hold ended re-split, so pieces still unanswered take the computed cover.</summary>
        internal static void PumpHolds(float now)
        {
            ShapeSummary(now);   // 1.2.437
            if (s_holdEnd.Count == 0) return;
            s_holdScratch.Clear();
            bool answering = OwnerAnswering(now);
            foreach (var kv in s_holdEnd)
            {
                if (now < kv.Value) continue;
                // 1.2.464: past the 3 s, a zone keeps its hold while the owner is still answering, up to MaxHoldSeconds from its announce
                bool capped = s_zones.TryGetValue(kv.Key, out var hz) && now - hz.AnnouncedAt >= MaxHoldSeconds;
                bool complete = hz != null && hz.Stored.Count >= hz.Ids.Count;
                if (answering && !capped && !complete) continue;
                s_holdScratch.Add(kv.Key);
            }
            for (int i = 0; i < s_holdScratch.Count; i++)
            {
                var c = s_holdScratch[i];
                s_holdEnd.Remove(c);
                if (!s_zones.TryGetValue(c, out var z)) continue;
                if (z.Stored.Count < z.Ids.Count)
                {
                    w_holdTimedOut++;
                    if (now - z.AnnouncedAt >= MaxHoldSeconds) w_holdCapped++;
                    else if (now - z.AnnouncedAt > HoldSeconds + 0.5f) w_holdExtended++;   // released after an extended hold, owner went quiet
                    else w_holdQuiet++;
                }
                foreach (var g in z.Groups) g?.MarkSnowDirty();
                EbmChurn.NoteHoldReleased();   // 1.2.480
            }
        }

        internal static int HoldCount => s_holdEnd.Count;   // 1.2.480 [Settle]: zones still waiting for the snow owner's per-piece answers

        /// <summary>EbmApi.InvalidateSnow: every pushed value goes, every announced zone is re-announced (the owner answers afresh).</summary>
        internal static void DropAllAndReannounce()
        {
            s_parked.Clear(); s_parkedEntries = 0;   // 1.2.512: InvalidateSnow: the owner answers afresh, nothing parked comes back
            if (s_zones.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var kv in s_zones)
            {
                var zone = kv.Value;
                foreach (var g in zone.Groups) if (g != null) g.CapAnnounceGen++;   // 1.2.507: shape values dropped below: this zone's cap lists rebuild
                foreach (var id in zone.Stored)
                {
                    if (!s_snow.Remove(id)) continue;
                    w_dropped++;
                    if (s_owner.TryGetValue(id, out var o) && o.group != null && o.group.ExtCount > 0) { o.group.ExtCount--; o.group.ExternalLooksChanged(); }
                }
                zone.Stored.Clear();
                foreach (var id in zone.Ids)   // 1.2.437: shape values are answered afresh too (owner push, or the shape source at the next look write)
                    if (s_shape.Remove(id) && s_owner.TryGetValue(id, out var so) && so.group != null) { if (so.group.ShapeCount > 0) so.group.ShapeCount--; so.group.ShapeLooksChanged(); }
                s_pending[kv.Key] = now;   // ZoneTracker's TakeDue re-announces it this frame (EbmApi.ZoneBaked again)
            }
        }

        // ── 1.2.437 (Vedr snow redesign step 4, contract with [seasons]): per-piece snow SHAPE, EbmApi.SetPieceSnowShape / RegisterPieceSnowShapeSource ──
        // encoded: 0 = no snow; 0..1 = winter ground, value = shelter; 2..3 = snow base, value - 2 = shelter. The amount is NOT stored: EBM/Piece
        // multiplies by Vedr's _VedrSnowA global per pixel. Stored as encoded; the look's .z carries encoded + 1 (0 = unset) - ShapeLookZ.
        // A shape change touches only the look writes of the piece's group (ZoneInstanceGroup.ShapeLooksChanged -> ShapeGen -> IndirectLooks.WriteGroup,
        // which re-uploads only slots whose value moved). It never goes through SetPiece / ExternalLooksChanged / the re-split / RaisePieceSnowChanged.
        // Bounded like s_snow: only pieces of announced zones; dropped on unload, InvalidateSnow and Clear. Main thread only.
        private static readonly Dictionary<MeshBaker.PieceIdentity, float> s_shape = new Dictionary<MeshBaker.PieceIdentity, float>();
        private static readonly Dictionary<MeshBaker.PieceIdentity, float> s_keepShape = new Dictionary<MeshBaker.PieceIdentity, float>();
        private static Func<Vector3, float> s_shapeSource;
        private static int s_shapeUpdates, s_amountPushes;
        private static float s_nextShapeLog;

        /// <summary>True while a shape source is registered (shape mode on): baked pieces with no pushed shape ask it at their first look write.</summary>
        internal static bool ShapeSourceOn => s_shapeSource != null;
        internal static void RegisterShapeSource(Func<Vector3, float> shapeAt)
        {
            if (s_shapeSource == shapeAt) return;
            s_shapeSource = shapeAt;
            // every announced group re-writes its looks once (LooksActive may have flipped; source values are asked at that write)
            foreach (var kv in s_zones) foreach (var g in kv.Value.Groups) g?.ShapeLooksChanged();
        }
        internal static void NoteAmountPush() => s_amountPushes++;   // EbmApi.RaisePieceSnowChanged / RaisePieceSnowAreaChanged / InvalidateSnow

        private static float EncodeClamp(float encoded) => float.IsNaN(encoded) || encoded <= 0f ? 0f : Mathf.Min(encoded, 3f);

        /// <summary>EbmApi.SetPieceSnowShape. False = EBM draws no instance of that piece. A no-op when the value did not change.</summary>
        internal static bool SetShape(ZDOID id, float encoded)
        {
            if (!TryOwner(id, out var key, out var owner)) return false;
            float v = EncodeClamp(encoded);
            bool had = s_shape.TryGetValue(key, out var old);
            if (had && old == v) return true;
            s_shape[key] = v;
            if (!had) owner.group.ShapeCount++;
            owner.group.ShapeLooksChanged();
            s_shapeUpdates++; EbmChurn.NoteShape();   // 1.2.480
            return true;
        }

        /// <summary>The look's .z for a piece: stored shape + 1, else (shape source on) the source's value at pos, stored once (bake time); 0 = unset.
        /// Called from ZoneInstanceGroup.LookOf while the group's looks are written; a source fill bumps nothing (that write is already running).</summary>
        internal static float ShapeLookZ(ZoneInstanceGroup group, MeshBaker.PieceIdentity id, Vector3 pos)
        {
            if (s_shape.TryGetValue(id, out var v)) return v + 1f;
            var src = s_shapeSource;
            if (src == null || !s_owner.TryGetValue(id, out var o) || o.group != group) return 0f;
            float got;
            try { got = src(pos); } catch (Exception e) { EasyBakeLog.Warn("[Snow] the snow shape source threw, shape mode off until it registers again: " + e.Message); s_shapeSource = null; return 0f; }
            if (float.IsNaN(got) || float.IsInfinity(got) || got < 0f) return 0f;   // negative = the source has no answer here
            v = EncodeClamp(got);
            s_shape[id] = v; group.ShapeCount++;
            return v + 1f;
        }

        private static void ShapeSummary(float now)
        {
            if (now < s_nextShapeLog) return;
            s_nextShapeLog = now + 30f;
            if (s_shape.Count == 0 && s_shapeUpdates == 0 && s_shapeSource == null) { s_amountPushes = 0; return; }   // shape mode not in use: silent
            EasyBakeLog.Debug($"[Snow] shape mode: {s_shape.Count} pieces with a shape value, {s_shapeUpdates} shape updates, {s_amountPushes} amount pushes last 30 s");
            s_shapeUpdates = 0; s_amountPushes = 0;
        }

        private static bool TryOwner(ZDOID id, out MeshBaker.PieceIdentity key, out (ZoneInstanceGroup group, Vector2s zone) owner)
        {
            key = default; owner = default;
            var zdo = id.IsNone() || ZDOMan.instance == null ? null : ZDOMan.instance.GetZDO(id);
            if (zdo == null) return false;
            key = MeshBaker.PieceIdentity.From(zdo);
            if (s_owner.TryGetValue(key, out owner) && owner.group != null) return true;
            // [generator]'s check: a ZDO in an ANNOUNCED zone that matches no group identity = a key mismatch (e.g. a piece keyed from its
            // transform at bake time, From(GameObject, pos), against its ZDO's rotation now). Counted, one sample per window, at Debug.
            if (s_zones.ContainsKey(EbmZones.Of(zdo.GetPosition())))
            {
                s_keyMiss++;
                if (s_keyMissSample == null) s_keyMissSample = $"prefab {zdo.GetPrefab()} at ({zdo.GetPosition().x:0.00}, {zdo.GetPosition().y:0.00}, {zdo.GetPosition().z:0.00}) key R {key.R}";
            }
            return false;
        }

        private static int s_keyMiss;
        private static string s_keyMissSample;

        private static void Summary()
        {
            float now = Time.unscaledTime;
            if (now < s_nextLog) return;
            s_nextLog = now + 30f;
            if (s_set == 0 && s_unknown == 0 && w_held == 0 && w_dropped == 0) return;
            EasyBakeLog.Debug($"[EbmApi] snow values last 30 s: {s_set} set, {s_unknown} for pieces EBM does not draw (answered false), " +
                              $"of those {s_keyMiss} in an announced zone with no matching piece key{(s_keyMissSample != null ? " (first: " + s_keyMissSample + ")" : "")}; " +
                              $"{s_snow.Count} stored over {s_zones.Count} announced zone(s) ({s_announced} announced, {s_unloaded} unloaded so far); waiting for answers: {w_held} plain draw(s), {w_holdTimedOut} zone(s) timed out to the area amount (owner quiet at 3 s {w_holdQuiet}, after a longer hold {w_holdExtended}, 60 s cap {w_holdCapped}), {w_dropped} value(s) dropped by InvalidateSnow");
            s_set = s_unknown = s_keyMiss = 0; s_keyMissSample = null; w_held = w_holdTimedOut = w_dropped = 0; w_holdQuiet = w_holdExtended = w_holdCapped = 0;
        }
    }
}
