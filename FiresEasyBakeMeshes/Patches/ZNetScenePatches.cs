using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class ZNetScene_Awake_Patch
    {
        private static bool s_censusRegistered;

        // ebm_opt: the plugin's own [Optimize] bool entries, set in memory (SaveOnConfigSet off around the set, so this command never writes the
        // .cfg; a later save triggered by something else would write the current values, as for any config UI)
        // 1.2.212 ([generator]): which keys take effect at once. Checked in the code: these are read every frame (or have a SettingChanged path).
        private static readonly HashSet<string> s_liveKeys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "IndirectRenderer", "IndirectCascadeShadows", "IndirectCoverage", "IndirectCoverageDisableRenderers", "IndirectCoverageDisablePrefabs",
            "IndirectSparseUpload", "IndirectStableLayout", "IndirectCasterCpuCull", "IndirectPointFaces", "IndirectExactCascadeSpheres",
            "IndirectShadowReceiverCull", "IndirectShadowSameFrame", "IndirectCullGrouped", "IndirectOneCullPass",
            "IndirectVertexDebug", "IndirectCascadeEmptySkip", "IndirectCellsJob", "IndirectDrawParamsCache", "IndirectGpuGrow", "IndirectCompactSlots", "IndirectFusedCells", "IndirectCachedAlive", "IndirectLocalSteady", "IndirectCellFastSkip", "IndirectLooksChangeDriven", "IndirectCasterSizeSkip", "SectorMirrorSlicedRebuild", "DestroyLeanEarmark", "IndirectCoverageReach", "IndirectCoverageLocationProps", "FarBuildingsChangedZonesOnly", "IndirectCoverageLodLevels", "IndirectCellReuse",   // 1.2.216: read per frame / per pass in their code
            "ObjectStreamPacer", "StreamPacerEveryFramePass", "IdleObjectAreaSkip", "EventDrivenObjectArea", "DestroyMoversFirst", "RemovalIdleScanLimit", "TeleportCreateGate", "DistantPrefabStreaming",   // 1.2.250
        };
        // read when a batch is made: existing batches keep the old setting until the next world load
        private static readonly HashSet<string> s_nextLoadKeys = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "IndirectLodPerInstance" };
        private static string When(string key) => s_liveKeys.Contains(key) ? "(live)" : s_nextLoadKeys.Contains(key) ? "(next load)" : "(unverified: may need the next load)";

        private static IEnumerable<string> OptCommand(string key, string word)
        {
            var cfg = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(FiresEasyBakeMeshesPlugin.PluginGUID, out var pi) && pi.Instance != null ? pi.Instance.Config : null;
            if (cfg == null) { yield return "[ebm_opt] EBM's config is not available"; yield break; }
            var keys = new List<BepInEx.Configuration.ConfigDefinition>();
            foreach (var d in cfg.Keys) if (d.Section == "Optimize" && cfg[d] is BepInEx.Configuration.ConfigEntry<bool>) keys.Add(d);
            if (string.Equals(key, "list", System.StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder("[ebm_opt] [Optimize] on/off keys:");
                foreach (var d in keys) sb.Append(' ').Append(d.Key).Append('=').Append(((BepInEx.Configuration.ConfigEntry<bool>)cfg[d]).Value ? "on" : "off").Append(' ').Append(When(d.Key)).Append(';');
                yield return sb.ToString();
                yield break;
            }
            BepInEx.Configuration.ConfigEntry<bool> entry = null;
            foreach (var d in keys) if (string.Equals(d.Key, key, System.StringComparison.OrdinalIgnoreCase)) { entry = (BepInEx.Configuration.ConfigEntry<bool>)cfg[d]; break; }
            if (entry == null) { yield return $"[ebm_opt] no [Optimize] on/off key '{key}' (ebm_opt list shows them)"; yield break; }
            word = (word ?? "status").ToLowerInvariant();
            if (word == "on" || word == "off" || word == "true" || word == "false")
            {
                bool want = word == "on" || word == "true", save = cfg.SaveOnConfigSet;
                cfg.SaveOnConfigSet = false;
                try { entry.Value = want; } finally { cfg.SaveOnConfigSet = save; }
                yield return $"[ebm_opt] {entry.Definition.Key} -> {(want ? "ON" : "OFF")} for this session {When(entry.Definition.Key)} (cfg file not written by this command)";
                yield break;
            }
            yield return $"[ebm_opt] {entry.Definition.Key} = {(entry.Value ? "on" : "off")} (default {((bool)entry.DefaultValue ? "on" : "off")}) {When(entry.Definition.Key)}";
        }

        [HarmonyPostfix]
        public static void Postfix(ZNetScene __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value) return;

            if (!s_censusRegistered)
            {
                s_censusRegistered = true;
                new Terminal.ConsoleCommand("ebm_census",
                    "FiresEasyBakeMeshes: count every loaded object and why it was created instead of skipped (also written to the log).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string report = EasyBake.ZoneTracker.Census();
                        EasyBakeLog.Debug(report);
                        foreach (var line in report.Split('\n')) args.Context?.AddString(line);
                    }));
                // 1.2.205 ([classes-ui] / lead: halve A/B legs): ebm_indirect on|off|status flips [Optimize] IndirectRenderer for THIS session only (in memory, the cfg file is never written), logged
                new Terminal.ConsoleCommand("ebm_indirect",
                    "on|off|status - FiresEasyBakeMeshes: turn the GPU-driven baked draws on or off for this session only (the cfg is not changed), or print the [Indirect] state.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string word = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                        if (word == "on" || word == "off") EasyBake.Indirect.EbmIndirect.SetSessionOverride(word == "on");
                        string line = EasyBake.Indirect.EbmIndirect.Describe();
                        EasyBakeLog.Info(line);
                        args.Context?.AddString(line);
                    }));
                // 1.2.211 (lead / Fire: bisect live in set O, which has no Core cfg watcher): ebm_opt <Key> on|off|status for every [Optimize] bool key
                // 1.2.238 (Fire: name the mesh): run these while the stretch is on screen
                new Terminal.ConsoleCommand("ebm_bisect",
                    "start | stays | gone | next | off - FiresEasyBakeMeshes: hide half of the indirect batches; say whether the stretch stays or goes, a few steps name the batch.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        foreach (var line in EasyBake.Indirect.EbmIndirect.BisectCommand(args.Length > 1 ? args[1].ToLowerInvariant() : "")) { EasyBakeLog.Info(line); args.Context?.AddString(line); }
                    }), isCheat: true);
                new Terminal.ConsoleCommand("ebm_batchread",
                    "[batch index] - FiresEasyBakeMeshes: read one batch back from the GPU and compare it with EBM's own data; no number = the last ebm_bisect FOUND.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string line;
                        try { line = EasyBake.Indirect.EbmIndirect.BatchRead(args.Length > 1 ? args[1] : null); } catch (System.Exception e) { line = "[BatchRead] ERROR " + e.GetType().Name + ": " + e.Message; }
                        EasyBakeLog.Info(line); args.Context?.AddString(line);
                    }), isCheat: true);
                new Terminal.ConsoleCommand("ebm_snowcaps",
                    "FiresEasyBakeMeshes: count the snow caps showing now, and the saved snow behind them (reads only).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        foreach (var line in EasyBake.EbmSnowCapLog.Report()) { EasyBakeLog.Info(line); args.Context?.AddString(line); }
                    }), isCheat: true);
                new Terminal.ConsoleCommand("ebm_batches",
                    "FiresEasyBakeMeshes: write every live indirect batch to BepInEx\\EbmBatches_<time>.txt, compared with Unity's mesh now (read-only).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string line;
                        try { line = EasyBake.Indirect.EbmIndirect.DumpBatches(); } catch (System.Exception e) { line = "[Batches] ERROR " + e.GetType().Name + ": " + e.Message; }
                        EasyBakeLog.Info(line); args.Context?.AddString(line);
                    }), isCheat: true);
                // PhysicsCensusOnMark: one scene search on request only (a harness mark in the circle), Debug logging on; works without [Diagnostics] PhysicsCensus
                new Terminal.ConsoleCommand("ebm_physcensus",
                    "[label] - FiresEasyBakeMeshes: one scan of rigidbodies, ZSyncTransforms and colliders right now, written to the log (Debug logging on; causes one hitch).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string line;
                        try { line = EasyBake.Indirect.PhysicsCensus.RunNow(args.Length > 1 ? args[1] : null); } catch (System.Exception e) { line = "[PhysicsCensus] ERROR " + e.GetType().Name + ": " + e.Message; }
                        EasyBakeLog.Debug(line); args.Context?.AddString(line);
                    }), isCheat: true);
                new Terminal.ConsoleCommand("ebm_meshhash",
                    "[base] - FiresEasyBakeMeshes: re-hash every batch mesh's whole GPU data against its baseline (read-only); 'base' takes a new baseline.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string line = EasyBake.Indirect.EbmIndirect.MeshHashCommand(args.Length > 1 && args[1] == "base");
                        EasyBakeLog.Info(line); args.Context?.AddString(line);
                    }), isCheat: true);
                new Terminal.ConsoleCommand("ebm_opt",
                    "<Key> on|off|status, or 'list' - FiresEasyBakeMeshes: flip any [Optimize] on/off key for this session (applied at once, not saved by this command).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        // 1.2.216 (lead: one call left no line at all): every call logs what it got and ends in an ack or an error line
                        var lines = new List<string>();
                        string k = args.Length > 1 ? args[1] : "list", w = args.Length > 2 ? args[2] : "status";
                        try { foreach (var line in OptCommand(k, w)) lines.Add(line); }
                        catch (System.Exception e) { lines.Add($"[ebm_opt] ERROR for '{k} {w}': {e.GetType().Name}: {e.Message}"); }
                        if (lines.Count == 0) lines.Add($"[ebm_opt] '{k} {w}': no result (please report)");
                        foreach (var line in lines) { EasyBakeLog.Info(line); args.Context?.AddString(line); }
                    }));
                EasyBake.EbmObjectArea.RegisterCommand();   // 1.2.250: ebm_eventarea
                EasyBake.UnloadStressTest.Register();   // 1.2.96
                // 1.2.102: what EBM does with the piece under the crosshair (read-only), and a forced rebake (devcommands).
                new Terminal.ConsoleCommand("ebm_why",
                    "FiresEasyBakeMeshes: what EBM does with the piece you look at - silenced, instanced, combined, skipped - and what it draws (read-only).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        foreach (var line in EasyBake.ZoneTracker.Why()) { EasyBakeLog.Debug(line); args.Context?.AddString(line); }
                    }));
                new Terminal.ConsoleCommand("ebm_drawtest",
                    "[prefab] FiresEasyBakeMeshes (diagnostic): for 30 s draw the piece you look at (or the nearest piece of [prefab]) as numbered copies 2 m above it, one per draw path, to see which one fails.",
                    new Terminal.ConsoleEvent(args => args.Context?.AddString(EasyBake.ZoneTracker.DrawTest(args.Length > 1 ? args[1] : null))), isCheat: true);   // 1.2.111: devcommands only; 1.2.112: optional prefab name
                new Terminal.ConsoleCommand("ebm_rebake",
                    "[all|here] FiresEasyBakeMeshes: drop EBM's bakes (memory + disk cache) for every loaded zone or yours; they bake again when settled.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        bool all = args.Length < 2 || !string.Equals(args[1], "here", System.StringComparison.OrdinalIgnoreCase);
                        args.Context?.AddString(EasyBake.ZoneTracker.Rebake(all));
                    }), isCheat: true);
                // 1.2.197 ([ghost], DESIGN_OcclusionLOD §5): the fractured-rock read (distance, LOD, tris per LOD, shadows) - read-only
                new Terminal.ConsoleCommand("ebm_rocks",
                    "[radius m] FiresEasyBakeMeshes: every fractured rock (MineRock5) near the camera - distance, intact, LOD drawn now, tris per LOD, shadows (read-only, also written to the log).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        float radius = 300f;
                        if (args.Length > 1) float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
                        string report = EasyBake.RockReport.Run(radius > 0f ? radius : 300f);
                        EasyBakeLog.Debug(report);
                        foreach (var line in report.Split('\n')) args.Context?.AddString(line);
                    }));
                new Terminal.ConsoleCommand("ebm_vegetation",
                    "FiresEasyBakeMeshes: how much of the live tree, shrub, bush and rock population the instancing path could draw, per prefab (also written to the log).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string report = EasyBake.VegetationInstancing.Audit();
                        EasyBakeLog.Debug(report);
                        foreach (var line in report.Split('\n')) args.Context?.AddString(line);
                    }));
            }

            EasyBake.EbmPieceTypeAudit.Begin(__instance);   // 1.2.250

            // Prefab-derived verdicts are rebuilt per scene: mods can register different prefabs each session.
            EasyBake.SkipEligibility.Clear();
            EasyBake.StandInColliders.Clear();
            EasyBake.StandInDamageBinding.ClearDestructibleTypeCache();
            EasyBake.VegetationInstancing.ClearPrefabCache();

            // The material-registry walk + prewarm both run on the main
            // thread inside ZNetScene.Awake's lifecycle window. Pre-fix:
            // BuildMaterialRegistry ran SYNCHRONOUSLY for ~5000 prefabs
            // (multi-second freeze) and then the prewarm started — landing
            // the worst of the stall right around the password-prompt /
            // loading-screen-first-frame window.
            //
            // Now both run as time-budgeted coroutines, chained so the
            // prewarm waits for the registry to finish but neither blocks
            // the main thread for more than the configured per-frame
            // budget (MaterialRegistryFrameBudgetMs / PrewarmFrameBudgetMs).
            // Wall time is the same or slightly longer; main-thread
            // responsiveness stays intact for the password dialog +
            // loading screen.
            bool prewarmAllowed = FiresEasyBakeMeshesPlugin.PrewarmEnabled.Value;
            if (prewarmAllowed)
            {
                bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
                if (isServer && !FiresEasyBakeMeshesPlugin.PrewarmRunOnServer.Value)
                    prewarmAllowed = false;
            }

            __instance.StartCoroutine(BuildRegistryThenPrewarm(__instance, prewarmAllowed));
        }

        private static System.Collections.IEnumerator BuildRegistryThenPrewarm(
            ZNetScene scene, bool prewarmAllowed)
        {
            // ── PASS 1: PREWARM FIRST ──────────────────────────────────────
            // Prewarm is load-bearing for in-game smoothness — it pays the
            // hidden first-Instantiate shader-compile cost during loading so
            // we don't hitch at zone borders. The material registry, by
            // contrast, is ONLY useful for cached zone-mesh reattach (its sole
            // consumer is ConstructFromCache → FindMaterial). For a world with
            // no on-disk cache the registry is 50+ seconds of pure main-thread
            // overhead for zero benefit — and worse, it eats budget that FAP's
            // hammer-setup coroutine needs to complete (the user-visible
            // symptom = "hammer never finishes setting up").
            //
            // Order is therefore: prewarm first (always useful when enabled)
            // → then wait for the world UID → then build registry ONLY if a
            // cache dir exists for this world. Prewarm runs while the network
            // handshake is happening, so by the time prewarm completes the UID
            // is usually already available; the wait below is a safety net.
            if (prewarmAllowed)
            {
                var prewarmOptions = BuildPrewarmOptions();
                var prewarmEnum = EasyBake.PrewarmCoroutine.Run(
                    scene.m_prefabs,
                    prewarmOptions,
                    EasyBakeLog.Info,
                    EasyBakeLog.Warn,
                    EasyBakeLog.Debug);
                // 1.2.189: a throw inside a step (another mod's code reached through a prefab) ends the warm-up with one Warning; the
                // registry pass below still runs. Before, it killed this whole coroutine.
                while (true)
                {
                    bool more;
                    try { more = prewarmEnum.MoveNext(); }
                    catch (System.Exception ex) { EasyBakeLog.Warn($"[Prewarm] stopped early: a step threw ({ex.GetType().Name}: {ex.Message}). Loading carries on; prefabs not warmed yet warm on first spawn."); break; }
                    if (!more) break;
                    yield return prewarmEnum.Current;
                }
            }

            // ── PASS 2: CONDITIONAL REGISTRY ───────────────────────────────
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
            {
                EasyBakeLog.Debug(
                    "[Cache] CachePersistEnabled=false — skipping material registry build " +
                    "(saves ~50s of main-thread budget; registry only feeds cache reattach).");
                yield break;
            }

            // Wait up to 30 seconds for the world UID to materialize. Without
            // it we can't know which world's cache to look for; we'd rather
            // skip the registry than build it speculatively.
            float waitStart = Time.unscaledTime;
            long worldUid = 0L;
            while (Time.unscaledTime - waitStart < 30f)
            {
                worldUid = EasyBake.MeshCacheStore.TryGetWorldUid();
                if (worldUid != 0L) break;
                yield return null;
            }
            if (worldUid == 0L)
            {
                EasyBakeLog.Debug(
                    "[Cache] Timed out waiting for world UID — skipping material registry. " +
                    "Cache reattach disabled this session; bakes will happen fresh.");
                yield break;
            }

            if (!EasyBake.MeshCacheStore.WorldHasCache(worldUid))
            {
                EasyBakeLog.Debug(
                    $"[Cache] No on-disk cache for world {worldUid:x16} — skipping material registry. " +
                    "First-bake-from-scratch path is fine without it; next session will have a cache " +
                    "and the registry will build then.");
                yield break;
            }

            EasyBakeLog.Debug($"[Cache] World {worldUid:x16} has a cache — building material registry.");
            EasyBake.InstanceDefinitionCache.Clear();
            var registryEnum = EasyBake.MeshCacheStore.BuildMaterialRegistryAsync(scene);
            while (true)   // 1.2.189: same guard as the warm-up
            {
                bool more;
                try { more = registryEnum.MoveNext(); }
                catch (System.Exception ex) { EasyBakeLog.Warn($"[Cache] material registry stopped early: a step threw ({ex.GetType().Name}: {ex.Message}). Cached zones that need it bake fresh instead."); break; }
                if (!more) break;
                yield return registryEnum.Current;
            }
            yield break;
        }

        // Extracted so the prewarm option construction is reusable + readable.
        private static EasyBake.PrewarmOptions BuildPrewarmOptions()
        {
            return new EasyBake.PrewarmOptions
            {
                PrefabsPerFrame             = FiresEasyBakeMeshesPlugin.PrewarmPrefabsPerFrame.Value,
                FrameBudgetMs               = FiresEasyBakeMeshesPlugin.PrewarmFrameBudgetMs != null
                                                  ? FiresEasyBakeMeshesPlugin.PrewarmFrameBudgetMs.Value
                                                  : 8f,
                SlowInstantiateThresholdMs  = FiresEasyBakeMeshesPlugin.PrewarmSlowInstantiateThresholdMs.Value,
                MaxTransformCount           = FiresEasyBakeMeshesPlugin.PrewarmMaxTransformCount.Value,
                // Slow/oversize prefabs persist here so a 29s mega-prefab
                // (ConsMassiveTower) freezes at most ONE login ever, not every one.
                PersistSlowSkipPath         = System.IO.Path.Combine(EasyBake.MeshCacheStore.BaseDir ?? FiresCachePaths.For("FiresEasyBakeMeshes"), "prewarm_slow_skips.txt"),   // 1.2.99
                Verbose                     = FiresEasyBakeMeshesPlugin.PrewarmVerbose.Value,
                SkipNameContains            = FiresEasyBakeMeshesPlugin.PrewarmSkipNameContains.Value ?? string.Empty,
                SkipStatefulComponents      = true,
                // 1.2.189 (Fire 11-08: "make sure ebm is safe for all mods"): only prefabs EBM can bake or instance are warmed, a building
                // piece (WearNTear / Piece) or a tree / bush EBM's vegetation instancing takes. Never a projectile, creature, item, ship or spawner.
                Eligible                    = go => go != null
                                                    && (go.GetComponent<WearNTear>() != null || go.GetComponent<Piece>() != null
                                                        || EasyBake.VegetationInstancing.IsSkipCandidate(EasyBake.VegetationInstancing.KindOf(go))),
            };
        }

    }

    [HarmonyPatch(typeof(ZNetScene), "CreateObject")]
    public static class ZNetScene_CreateObject_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(GameObject __result)
        {
            if (__result == null) return;

            long t = EasyBake.Probe.Start();

            // Static-piece ZSync skip runs independently of the mesh-batching
            // active state — it's its own optimization with its own config gate.
            // Eligible pieces have their ZSyncTransform disabled; vanilla's
            // OnDisable handler removes them from ZSyncTransform.Instances, so
            // the per-frame iteration over that list stops visiting them.
            EasyBake.StaticPieceZSyncSkip.TryDisable(__result);
            EasyBake.RemoteItemZSyncSkip.Consider(__result);
            EasyBake.IdleGates.PauseOffScreen(__result);
            EasyBake.EbmParticleIdle.Consider(__result);   // [seasons] 11-09 E34 (key off)

            if (FiresEasyBakeMeshesPlugin.BatchingActive())
                EasyBake.ZoneTracker.OnInstanceCreated(__result);

            EasyBake.Probe.Stop("EasyBake:hook", t);
        }
    }

    // Two-layer optimization on vanilla RemoveObjects:
    //
    //   1. KEEPALIVE INJECTION: append our pinned zones' ZDOs to the near list
    //      so the upcoming earmark pass marks them. Their instances are spared
    //      from the destroy phase — they ride out the zone-cross transition
    //      and don't need a re-Instantiate + cache-rebuild on the way back.
    //
    //   2. TIME-SLICED DESTROY: when DestroyTimeSliceEnabled, replace vanilla's
    //      single-tick destroy loop with a queued, budget-capped version (see
    //      DestroyTimeSlicer). Spreads multi-thousand-piece zone-unloads across
    //      multiple frames so they don't land as 100+ms frame hitches.
    //
    // Order matters: keepalive injection must run BEFORE earmark so the
    // earmark pass sees the keepalive ZDOs in the near list. DestroyTimeSlicer
    // does its own earmark (identical to vanilla's) and consumes the near list
    // including our keepalive additions.
    [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
    public static class ZNetScene_RemoveObjects_Patch
    {
        private static bool s_standDownLogged;

        // A prefix that already replaced RemoveObjects (ValheimCommunityPatch's zone-diff removal) hands on lists
        // that do not describe the active area; destroying against them unloads everything it just created.
        [HarmonyPrefix]
        public static bool Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects, bool __runOriginal)
        {
            if (!__runOriginal)
            {
                if (!s_standDownLogged)
                {
                    s_standDownLogged = true;
                    EasyBakeLog.Debug("[DTS] Another mod already replaces ZNetScene.RemoveObjects; destroy time-slicing and zone keepalive stand down.");
                }
                return false;
            }
            // 1.2.250: the spread removal is its own job (it moved here from FGN), so a client / local host gets it with batching off too. A dedicated server
            // still takes this path only through BatchingRunOnServer, as before.
            bool batching = FiresEasyBakeMeshesPlugin.BatchingActive();
            bool clientSpread = FiresEasyBakeMeshesPlugin.PluginEnabled.Value && ZNet.instance != null && !ZNet.instance.IsDedicated();
            if (!batching && !clientSpread) return true;

            // Layer 1: keepalive injection. Pass player center + vanilla's
            // active area so keepalive can skip injecting for zones vanilla
            // already covered in currentNearObjects — those would just be
            // duplicate AddRange work.
            // ═══ NOT WHEN THE SKIP IS OFF, 2026-09-25 ════════════════════════════════════════════════════════
            // Keepalive pins up to (2*Radius+1)^2 zones - 961 at the default radius of 15, a 960 m circle - so
            // their ZNetViews survive the player crossing a zone boundary. That trade only pays BECAUSE the bake
            // draws those pieces: what stays pinned is meant to be a cheap stand-in, not a live piece.
            //
            // BatchingActive() excludes only a DEDICATED server. On a listen host - someone playing their own
            // hosted world - it returns true while SkipCreationActive() returns FALSE, because a server treats a
            // CreateObject that returns null as an invalid prefab and destroys the ZDO. So keepalive was pinning
            // tens of thousands of FULL LIVE instances and refusing to let RemoveObjects release any of them,
            // with no bake benefit at all.
            //
            // Measured on a hosted clone of the live server: "0 baked pieces skipped, 87,484 created", instances
            // climbing 86,355 -> 87,080 -> 87,869 while loaded zones went 1,217 -> 1,251, at 5 fps.
            //
            // The pin is only ever worth taking where the skip that justifies it is actually running.
            int vanillaNear = currentNearObjects != null ? currentNearObjects.Count : -1;   // 1.2.215: the keepalive ZDOs are appended after this
            if (batching && FiresEasyBakeMeshesPlugin.ZoneKeepaliveEnabled.Value && !EasyBake.EbmZones.Ring && EasyBake.ZoneTracker.SkipCreationActive())   // 1.2.122: not on the ring
            {
                var zs = ZoneSystem.instance;
                var net = ZNet.instance;
                if (zs != null && net != null)
                {
                    var center = ZoneSystem.GetZone(net.GetReferencePosition());
                    // strict near area - 1.0 replaced m_activeArea with SimulationDistance
                        int vanillaCovered = net.GetSyncedSimulationDistance().NearSimulationDistance;
                    EasyBake.ZoneKeepalive.AppendKeepaliveZDOs(currentNearObjects, center, vanillaCovered);
                }
                else
                {
                    EasyBake.ZoneKeepalive.AppendKeepaliveZDOs(currentNearObjects, new Vector2s(0, 0), -1);
                }
            }

            // Layer 2: time-sliced destroy. Returns true if we did the work;
            // we skip vanilla in that case. Returns false on reflection
            // failure — fall through and let vanilla run normally.
            if (FiresEasyBakeMeshesPlugin.DestroyTimeSliceEnabled.Value)
            {
                long t = EasyBake.Probe.Start();
                bool handled = EasyBake.DestroyTimeSlicer.Run(__instance, currentNearObjects, currentDistantObjects, vanillaNear);
                EasyBake.Probe.Stop("EasyBake:destroy", t);
                if (handled) return false;
            }

            return true;
        }
    }
}
