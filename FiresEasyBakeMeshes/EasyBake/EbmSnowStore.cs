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
            s_announced++;
            if (zone.Ids.Count == 0 || liveZdos == null) return Array.Empty<ZDOID>();
            var list = new List<ZDOID>(zone.Ids.Count);
            foreach (var id in zone.Ids) if (liveZdos.TryGetValue(id, out var zdo) && zdo != null) list.Add(zdo.m_uid);
            return list.ToArray();
        }

        /// <summary>The zone left the loaded area or was torn down: drop its entries. raise = the caller raises EbmApi.ZoneUnloaded.</summary>
        internal static bool Unload(Vector2s coord, bool count = true)
        {
            if (!s_zones.TryGetValue(coord, out var zone)) return false;
            // stored values first, while the owner map still names their groups: each group gives its count back and re-splits plain
            foreach (var id in zone.Stored)
            {
                if (!s_snow.Remove(id)) continue;
                if (s_owner.TryGetValue(id, out var o) && o.group != null && o.group.ExtCount > 0) { o.group.ExtCount--; o.group.ExternalLooksChanged(); }
            }
            foreach (var id in zone.Ids)
            {
                if (s_owner.TryGetValue(id, out var o) && o.zone.Equals(coord)) s_owner.Remove(id);
                s_caps.Remove(id);
            }
            foreach (var g in zone.Groups) if (s_groupZone.TryGetValue(g, out var gz) && gz.Equals(coord)) s_groupZone.Remove(g);
            s_zones.Remove(coord);
            s_holdEnd.Remove(coord);
            if (count) { s_unloaded++; s_pending.Remove(coord); }
            return true;
        }

        internal static void Clear()
        {
            s_owner.Clear(); s_snow.Clear(); s_caps.Clear(); s_zones.Clear(); s_groupZone.Clear(); s_pending.Clear(); s_holdEnd.Clear();
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
            bool had = s_snow.TryGetValue(key, out var old);
            if (had && old == v) return true;
            s_snow[key] = v; s_pusherSeen = true;
            if (!had)
            {
                owner.group.ExtCount++;
                if (s_zones.TryGetValue(owner.zone, out var zone)) zone.Stored.Add(key);
            }
            owner.group.ExternalLooksChanged();
            s_set++;
            Summary();
            return true;
        }

        /// <summary>EbmApi.SetCapSnow: stored only (snow caps are not drawn by EBM yet).</summary>
        internal static bool SetCap(ZDOID id, float level, Vector4 edges, Vector4 neighbours)
        {
            if (!TryOwner(id, out var key, out _)) return false;
            s_caps[key] = (level, edges, neighbours);
            return true;
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
        private static bool s_pusherSeen;
        private static readonly Dictionary<Vector2s, float> s_holdEnd = new Dictionary<Vector2s, float>();
        private static readonly List<Vector2s> s_holdScratch = new List<Vector2s>();
        private static int w_held, w_holdTimedOut, w_dropped;

        internal static void Bind(BepInEx.Configuration.ConfigFile config)
        {
            WaitForPushed = config.Bind("Snow", "WaitForPushedSnow", true,
                "A baked building whose snow mod answers per piece draws plain for up to 3 s until that answer arrives, instead of the area's snow amount (which can show snow the real piece does not have). A fix.");
        }

        /// <summary>True = draw this piece plain for now (its zone was just announced, a per-piece owner exists, no answer yet).</summary>
        internal static bool HoldPlain(MeshBaker.PieceIdentity id)
        {
            if (!s_pusherSeen || (WaitForPushed != null && !WaitForPushed.Value) || s_holdEnd.Count == 0) return false;
            if (!s_owner.TryGetValue(id, out var o) || !s_holdEnd.TryGetValue(o.zone, out float end)) return false;
            if (Time.unscaledTime >= end) return false;
            w_held++;
            return true;
        }

        /// <summary>ZoneTracker, once a frame: zones whose hold ended re-split, so pieces still unanswered take the computed cover.</summary>
        internal static void PumpHolds(float now)
        {
            if (s_holdEnd.Count == 0) return;
            s_holdScratch.Clear();
            foreach (var kv in s_holdEnd) if (now >= kv.Value) s_holdScratch.Add(kv.Key);
            for (int i = 0; i < s_holdScratch.Count; i++)
            {
                var c = s_holdScratch[i];
                s_holdEnd.Remove(c);
                if (!s_zones.TryGetValue(c, out var z)) continue;
                if (z.Stored.Count < z.Ids.Count) w_holdTimedOut++;
                foreach (var g in z.Groups) g?.MarkSnowDirty();
            }
        }

        /// <summary>EbmApi.InvalidateSnow: every pushed value goes, every announced zone is re-announced (the owner answers afresh).</summary>
        internal static void DropAllAndReannounce()
        {
            if (s_zones.Count == 0) return;
            float now = Time.unscaledTime;
            foreach (var kv in s_zones)
            {
                var zone = kv.Value;
                foreach (var id in zone.Stored)
                {
                    if (!s_snow.Remove(id)) continue;
                    w_dropped++;
                    if (s_owner.TryGetValue(id, out var o) && o.group != null && o.group.ExtCount > 0) { o.group.ExtCount--; o.group.ExternalLooksChanged(); }
                }
                zone.Stored.Clear();
                s_pending[kv.Key] = now;   // ZoneTracker's TakeDue re-announces it this frame (EbmApi.ZoneBaked again)
            }
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
                              $"{s_snow.Count} stored over {s_zones.Count} announced zone(s) ({s_announced} announced, {s_unloaded} unloaded so far); waiting for answers: {w_held} plain draw(s), {w_holdTimedOut} zone(s) timed out to the area amount, {w_dropped} value(s) dropped by InvalidateSnow");
            s_set = s_unknown = s_keyMiss = 0; s_keyMissSample = null; w_held = w_holdTimedOut = w_dropped = 0;
        }
    }
}
