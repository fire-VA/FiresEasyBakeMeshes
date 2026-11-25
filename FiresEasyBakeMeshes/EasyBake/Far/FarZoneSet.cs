using BepInEx.Configuration;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.483 ([visual], Fire 11-21 via [lead]: "trees, rocks, builds, and distant terrain should all sync up so that you never see one without the other";
    /// design Tools\DESIGN_far_zone_set_20261121.md, [2ic] OK for step 1). ONE far-zone set every far layer draws: one reach, ground first, caps thin inside it.
    /// Step 1 (this file now): the key and the shared reach. [03 - Far Rendering] "Far Zone Sync", default ON, under the master "Far Rendering" (default OFF,
    /// FarRendering.cs:28), so nothing changes for a player who has not opted in ([lead] 11-21 call). Sync OFF = every layer exactly as 1.2.482 (kept for one
    /// version for the A/B, then removed).
    /// </summary>
    internal static class FarZoneSet
    {
        private static ConfigEntry<bool> s_sync;

        /// <summary>True when the shared far-zone set rules the far layers: its own key AND the Far Rendering master.</summary>
        internal static bool On => s_sync != null && s_sync.Value && FarRendering.On;

        internal static void Initialize(ConfigFile config)
        {
            if (config == null || s_sync != null) return;
            s_sync = EbmConfigMove.Bind(config, FarRendering.Section, "Far Zone Sync", true,
                "Far terrain, far trees, far rocks and far buildings follow ONE set of far zones: one far reach for all of them. Off = each far layer uses its own reach "
                + "and limits, as before 1.2.483. Needs Far Rendering on. Live.");
            s_sync.SettingChanged += (_, __) => Say("changed");
            FarRendering.Changed += () => Say("Far Rendering changed");
            EbmFarWorldEvents.WorldClosed += Reset;   // 1.2.484: the buildings coarse band starts over per world
            FarRunner.Register("FarZones", Tick, null);   // 1.2.484: the 30 s census (a few reads; nothing while Sync is off)
            Say("bound");
        }

        private static void Say(string why)
        {
            EasyBakeLog.Info(On
                ? $"[FarZones] sync on ({why}): every far layer draws to one reach, {FarReach.Effective:0} m ({FarReach.EffectiveSource})"
                : $"[FarZones] sync off ({why}): {(FarRendering.On ? "each far layer uses its own reach and limits (as before 1.2.483)" : "Far Rendering is off")}");
        }

        // -- 1.2.484 step 2 (design 3.2): Ready = ground first. ONE ground test for every far layer: FarGroundCover.Covered (a built near chunk, a built
        // distant-LOD region, or, since 1.2.484, a SHOWN EBM far tile). Under Sync a far layer starts drawing a zone only when it is Ready; what already
        // draws is never hidden by this test mid-show ([2ic] MUST 2). --
        internal static bool Ready(Vector2s zone) => FarGroundCover.Covered(zone);

        // -- 1.2.484 step 3 (design 3.4, per layer, [2ic] note 2): far buildings thin INSIDE the set instead of evicting whole targets. Over "Max vertices", the
        // farthest 512 m band goes to the coarse tier (FarBuildings' existing coarse cells, FarBuildings.cs:61-62 / ClusterFor); below 90 % of the cap for
        // 20 s quiet, the nearest coarse band comes back. At most one step per 10 s (FarCapPool's constants, FarCapPool.cs:29). If every band past the loaded
        // edge is already coarse and the cap is still over, nothing is dropped: the overrun is reported ("caps too low for the set"). --
        internal const float Band = 512f;
        private static float s_buildCoarseFrom = float.PositiveInfinity, s_lastStep = -999f, s_underSince = -1f;
        private static int s_overCapNow; private static long s_overCapVerts; private static int s_steps;
        /// <summary>Distance from which far building targets use the coarse tier under Sync (infinity = none forced).</summary>
        internal static float BuildingsCoarseFrom => On ? s_buildCoarseFrom : float.PositiveInfinity;

        /// <summary>FarBuildingDraw.Plan, after its cap pass under Sync: the BUILT vertices (real, no estimates: [2ic] MUST 1) against the cap. Main thread.</summary>
        internal static void NoteBuildings(long used, int cap, float loadedEdge, float reach)
        {
            float now = UnityEngine.Time.unscaledTime;
            if (used > cap)
            {
                s_underSince = -1f; s_overCapNow = 1; s_overCapVerts = used - cap;
                if (now - s_lastStep < FarCapPool.WindowSeconds) return;
                float from = float.IsPositiveInfinity(s_buildCoarseFrom) ? reach : s_buildCoarseFrom;
                float next = UnityEngine.Mathf.Max(loadedEdge, from - Band);
                if (next < from) { s_buildCoarseFrom = next; s_lastStep = now; s_steps++; EasyBakeLog.Info($"[FarZones] far buildings over their cap ({used:N0} built of {cap:N0} vertices): coarse from {next:0} m (was {(float.IsPositiveInfinity(from) || from >= reach ? "none" : from.ToString("0") + " m")})"); }
                return;
            }
            s_overCapNow = 0; s_overCapVerts = 0;
            if (float.IsPositiveInfinity(s_buildCoarseFrom)) return;
            if (used >= (long)(cap * FarCapPool.AdmitShare)) { s_underSince = -1f; return; }
            if (s_underSince < 0f) { s_underSince = now; return; }
            if (now - s_underSince < FarCapPool.QuietSeconds || now - s_lastStep < FarCapPool.WindowSeconds) return;
            float back = s_buildCoarseFrom + Band;
            s_buildCoarseFrom = back >= reach ? float.PositiveInfinity : back; s_lastStep = now; s_underSince = now; s_steps++;
            EasyBakeLog.Info($"[FarZones] far buildings under 90 % of their cap ({used:N0} of {cap:N0}): coarse from {(float.IsPositiveInfinity(s_buildCoarseFrom) ? "none" : s_buildCoarseFrom.ToString("0") + " m")}");
        }

        internal static string BuildingsSummary() => On
            ? $"buildings coarse from {(float.IsPositiveInfinity(s_buildCoarseFrom) ? "none" : s_buildCoarseFrom.ToString("0") + " m")}, level steps {s_steps}{(s_overCapNow > 0 ? $", OVER the cap by {s_overCapVerts:N0} vertices with every band coarse or stepping (caps too low for the set; nothing dropped)" : "")}"
            : null;

        internal static void Reset() { s_buildCoarseFrom = float.PositiveInfinity; s_lastStep = -999f; s_underSince = -1f; s_overCapNow = 0; s_overCapVerts = 0; s_steps = 0; s_worldAt = -1f; s_nextCensus = 0f; }

        // -- 1.2.484 (design 5 / 6c, [lead] GO): the [FarZones] census, every 30 s while Sync is on (Debug; Info when a layer is silent) in a world: the shared reach and its source, each far
        // layer's draw count, zones held for ground, the buildings band; and any layer that is ON but has drawn NOTHING for 2 minutes in the world is named. --
        private static float s_worldAt = -1f, s_nextCensus;
        private const float CensusSeconds = 30f, SilentAfter = 120f;
        private static void Tick()
        {
            if (!On || ZNetScene.instance == null || Player.m_localPlayer == null) return;
            float now = UnityEngine.Time.unscaledTime;
            if (s_worldAt < 0f) { s_worldAt = now; s_nextCensus = now + CensusSeconds; return; }
            if (now < s_nextCensus) return;
            s_nextCensus = now + CensusSeconds;
            int tiles = FarTerrainTiles.ShownViews, rocks = FarRocks.DrawnZones, builds = FarBuildingDraw.DrawnTargets; long trees = FarVegRunner.LiveInstances;
            var silent = new System.Collections.Generic.List<string>(4);
            if (now - s_worldAt >= SilentAfter)
            {
                if (FarTerrainTiles.Wanted && tiles == 0) silent.Add("far terrain (no tile shown)");
                if (ImpostorEngine.Enabled && trees == 0) silent.Add(FarVegRunner.Started ? "far trees (placement running, 0 trees sent)" : "far trees (no source: see the [FarVeg] line)");
                if (FarRocks.Enabled && rocks == 0) silent.Add("far rocks (0 zones)");
                if (FarBuildings.Enabled && builds == 0) silent.Add("far buildings (0 targets drawn)");
            }
            string b = BuildingsSummary();
            string line = ($"[FarZones] reach {FarReach.Effective:0} m ({FarReach.EffectiveSource}); drawing: far terrain {tiles} tile(s){(FarTerrainTiles.Wanted ? "" : " (off)")}, "
                + $"far trees {trees} instance(s){(ImpostorEngine.Enabled ? "" : " (off)")}, far rocks {rocks} zone(s){(FarRocks.Enabled ? "" : " (off)")}, far buildings {builds} target(s){(FarBuildings.Enabled ? "" : " (off)")}; "
                + $"far building zones held for ground {FarBuildingDraw.HeldForGround}{(b != null ? "; " + b : "")}"
                + (silent.Count > 0 ? $"; ON BUT DRAWING NOTHING: {string.Join(", ", silent.ToArray())}" : ""));
            if (silent.Count > 0) EasyBakeLog.Info(line); else EasyBakeLog.Debug(line);   // Info only when a layer is on but silent (players see it); the routine census is Debug
        }
    }
}
