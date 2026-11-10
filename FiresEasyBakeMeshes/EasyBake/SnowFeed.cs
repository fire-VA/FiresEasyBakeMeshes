using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.117 ([visual], [ghost]/[lead] 10-24: baked pieces never showed snow, because EBM's instanced draw carries no per-renderer property block).
    // 1.2.210 ([ghost], lead: "EBM relies on no other mod"): the snow sources PUSH themselves through EbmApi.RegisterPieceSnowSource instead of
    // EBM looking up Vedr's VedrApi, TossinShade's FiresPieceSnowDriver / FiresPieceSnowField / FiresPieceShaderLoader and Core's SnowField by
    // name. The old precedence is kept as a priority per ROLE: for each role (active, cover, exposure, field, shader swap) the highest-priority
    // source that registered it answers (Vedr 30, TossinShade 20, Core 10 by the agreed numbers: "Vedr, else FTS (+ Core), else Core alone").
    // Change events are pushed too (EbmApi.RaisePieceSnowChanged / RaisePieceSnowAreaChanged), so nothing is polled per frame.
    // Nothing registered = exactly "no Vedr, FTS or Core": baked pieces draw no snow, exposure is vanilla Cover.IsUnderRoof.
    internal static class SnowFeed
    {
        internal const string Property = "_FiresSnowBiome";
        internal static readonly int PropertyId = Shader.PropertyToID(Property);
        internal static readonly int VedrPropertyId = Shader.PropertyToID("_VedrSnowCover");

        internal static bool AreaEventBound;   // true once a source pushed an area-change event (the [SnowBake] summary words it)

        private sealed class Source
        {
            public string Owner; public int Priority;
            public Func<bool> Active; public Func<Vector3, float> Cover, Exposure; public Func<bool> Field; public Func<GameObject, string, int> Apply;
        }
        private static readonly List<Source> s_sources = new List<Source>(3);   // highest priority first
        // the winner per role, recomputed on every (un)registration (read hot: no list walk per call)
        private static Func<bool>[] s_active = new Func<bool>[0];
        private static Source s_cover, s_exposure, s_field, s_apply;

        internal static void Register(string owner, int priority, Func<bool> active, Func<Vector3, float> coverAt, Func<Vector3, float> exposureAt,
                                      Func<bool> fieldActive, Func<GameObject, string, int> applyPieceShader)
        {
            if (string.IsNullOrEmpty(owner)) return;
            s_sources.RemoveAll(s => s.Owner == owner);
            bool any = active != null || coverAt != null || exposureAt != null || fieldActive != null || applyPieceShader != null;
            if (any) s_sources.Add(new Source { Owner = owner, Priority = priority, Active = active, Cover = coverAt, Exposure = exposureAt, Field = fieldActive, Apply = applyPieceShader });
            s_sources.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            var actives = new List<Func<bool>>();
            s_cover = s_exposure = s_field = s_apply = null;
            foreach (var s in s_sources)
            {
                if (s.Active != null) actives.Add(s.Active);
                if (s_cover == null && s.Cover != null) s_cover = s;
                if (s_exposure == null && s.Exposure != null) s_exposure = s;
                if (s_field == null && s.Field != null) s_field = s;
                if (s_apply == null && s.Apply != null) s_apply = s;
            }
            s_active = actives.ToArray();
            EasyBakeLog.Info(any
                ? $"[Snow] snow source {owner} (priority {priority}) registered; baked piece snow: cover from {s_cover?.Owner ?? "nobody"}, exposure from {s_exposure?.Owner ?? "vanilla Cover"}, field from {s_field?.Owner ?? "nobody"}, swap by {s_apply?.Owner ?? "nobody"}"
                : $"[Snow] snow source {owner} withdrawn");
            ZoneTracker.MarkAllSnowDirty();
        }

        /// <summary>True while any registered snow source is live; EBM then writes snow into its instanced draws.</summary>
        internal static bool Active
        {
            get
            {
                var a = s_active;
                for (int i = 0; i < a.Length; i++) { try { if (a[i]()) return true; } catch { } }
                return false;
            }
        }

        /// <summary>The top source registered a cover (the old "Vedr is the source"): its property naming wins in the draw path.</summary>
        internal static bool FromVedr => s_cover != null && s_cover.Owner == "Vedr";

        // ── 1.2.259 (Fire 11-10, DN in Summer: baked + far copies kept snow the real pieces did not) ──────────────────────────────────
        // Baked and far snow are LIVE values from the registered source, re-read only when the source says so (RaisePieceSnowChanged, the
        // far revision). A value read before the source's season resolved, or a missed event, stayed until the next event. Now EBM also
        // checks for itself ([Snow] SnowRefreshPoll, default ON, a fix): once a second it reads the sources' revision ints (the far
        // registration's revision + an optional piece revision, EbmApi.RegisterPieceSnowRevision) and re-reads everything on a change;
        // and it re-reads once when the player first exists in a world and again 10 s later (a season that resolves late).
        // EbmApi.InvalidateSnow(reason) does the same on demand (Info line). Cost: two delegate calls a second.
        internal static ConfigEntry<bool> RefreshPoll;
        private static Func<int> s_pieceRevision;
        private static int s_lastPieceRev = int.MinValue, s_lastFarRev = int.MinValue;
        private static float s_nextPoll, s_loadReread = -1f, s_nextSay;
        private static bool s_sawPlayer;
        private static int w_refreshes; private static string s_lastWhy = "none"; private static float s_lastAt = -1f;

        internal static void Bind(ConfigFile config)
        {
            RefreshPoll = config.Bind("Snow", "SnowRefreshPoll", true,
                "Baked and far buildings re-read the season snow whenever the snow mod's revision changes (checked once a second), when you enter a world and 10 s later. A fix: off = only when the snow mod announces a change, which can leave old snow on baked and far buildings.");
        }

        internal static void RegisterPieceRevision(Func<int> revision)
        {
            s_pieceRevision = revision; s_lastPieceRev = int.MinValue;
            EasyBakeLog.Info(revision != null ? "[Snow] piece snow revision registered: baked buildings re-read snow when it changes" : "[Snow] piece snow revision withdrawn");
        }

        /// <summary>Re-read every baked group's snow and rebuild the far chunks' snow. Info only for an outside InvalidateSnow call.</summary>
        internal static void Refresh(string why, bool info)
        {
            ZoneTracker.MarkAllSnowDirty();
            if (Far.FarSnow.Bound) Far.FarBuildingDraw.MarkSnowDirty();   // no far snow registered = far chunks carry none: no rebuild
            w_refreshes++; s_lastWhy = why; s_lastAt = Time.unscaledTime;
            if (info) EasyBakeLog.Info($"[Snow] snow re-read on baked and far buildings: {why}");
            else EasyBakeLog.Debug($"[Snow] snow re-read on baked and far buildings: {why}");
        }

        // ZoneTracker's per-frame call (the old 1.2.181 site): cheap checks, real work once a second at most
        internal static void PollVedrRevision()
        {
            float now = Time.unscaledTime;
            if (Player.m_localPlayer == null) { s_sawPlayer = false; return; }
            if (!s_sawPlayer) { s_sawPlayer = true; s_loadReread = now + 10f; if (RefreshPoll == null || RefreshPoll.Value) Refresh("entered the world", false); }
            if (now < s_nextPoll) return;
            s_nextPoll = now + 1f;
            Say(now);
            if (RefreshPoll != null && !RefreshPoll.Value) return;
            if (s_loadReread > 0f && now >= s_loadReread) { s_loadReread = -1f; Refresh("10 s after entering the world (a season that resolved late)", false); }
            int piece = 0, far = Far.FarSnow.Revision;
            try { if (s_pieceRevision != null) piece = s_pieceRevision(); } catch { }
            bool first = s_lastPieceRev == int.MinValue;
            bool changed = !first && (piece != s_lastPieceRev || far != s_lastFarRev);
            s_lastPieceRev = piece; s_lastFarRev = far;
            if (changed) Refresh($"the snow source's revision changed (piece {piece}, far {far})", false);
        }

        private static void Say(float now)
        {
            if (now < s_nextSay) return;
            if (s_nextSay > 0f && EasyBakeLog.DebugOn())
            {
                var p = Player.m_localPlayer;
                string cover = p != null ? CoverageAt(p.transform.position).ToString("0.00") : "?";
                EasyBakeLog.Debug($"[Snow] 30 s: cover at the player {cover}; far chunks built with snow {Far.FarBuildingDraw.TakeSnowChunks()}; far snow caps: {Far.FarBuildingDraw.TakeCapCounts()} (cap-only zone changes {Far.FarBuildingDigest.s_capOnlyChanges}); re-reads {w_refreshes}, last {(s_lastAt < 0f ? "never" : $"{now - s_lastAt:0} s ago")} ({s_lastWhy}); poll {(RefreshPoll == null || RefreshPoll.Value ? "on" : "off")}");
            }
            s_nextSay = now + 30f;
            w_refreshes = 0;
        }

        internal static void RaiseChanged() => Refresh("the snow source announced a change", false);

        internal static void RaiseAreaChanged(Vector2s zone, Rect area, bool wholeZone)
        {
            AreaEventBound = true;
            ZoneTracker.MarkSnowDirtyArea(zone, area, wholeZone);
        }

        internal static bool FieldActive
        {
            get
            {
                var s = s_field;
                if (s == null) return false;
                try { return s.Field(); } catch (Exception ex) { EasyBakeLog.Debug($"[Snow] {s.Owner}'s field flag threw: {ex.Message}"); return false; }
            }
        }

        // 1.2.182: the modded-prefab check judges the prefab before the swap and learns which game shader each swapped material left
        internal static void SwapPrefabMaterials(GameObject prefab)
        {
            if (prefab == null) return;
            ModdedPrefabGate.BeforeSwap(prefab);
            try
            {
                var s = s_apply;
                if (s != null) { try { s.Apply(prefab, "ebm definition"); } catch (Exception ex) { EasyBakeLog.Debug($"[Snow] {s.Owner}'s piece shader swap threw on {prefab.name}: {ex.Message}"); } }
            }
            finally { ModdedPrefabGate.AfterSwap(prefab); }
        }

        internal static float CoverageAt(Vector3 pos)
        {
            var s = s_cover;
            if (s == null) return 0f;
            try { return Mathf.Clamp01(s.Cover(pos)); } catch (Exception ex) { EasyBakeLog.Debug($"[Snow] {s.Owner}'s cover threw: {ex.Message}"); return 0f; }
        }

        // 1.2.121 ([visual], halo ring): "up" at a point, from the registered gravity (EbmApi.RegisterGravityUp); world up without one.
        private static bool s_upLogged;
        internal static Vector3 UpAt(Vector3 pos)
        {
            Vector3 up = CoreLink.HasUp ? CoreLink.Up(pos) : Vector3.up;
            if (!s_upLogged && Vector3.Angle(up, Vector3.up) > 1f) { s_upLogged = true; EasyBakeLog.Debug($"[Snow] baked instances on a ring: roof/shelter tested along the local up ({up.x:0.00}, {up.y:0.00}, {up.z:0.00})"); }
            return up;
        }

        internal static float ExposureAt(Vector3 top) => ExposureAt(top, Vector3.up);

        internal static float ExposureAt(Vector3 top, Vector3 up)
        {
            var s = s_exposure;
            if (s != null) { try { return Mathf.Clamp01(s.Exposure(top)); } catch (Exception ex) { EasyBakeLog.Debug($"[Snow] {s.Owner}'s exposure threw: {ex.Message}"); } }
            return Cover.IsUnderRoof(top + up * 0.15f) ? 0f : 1f;
        }
    }
}
