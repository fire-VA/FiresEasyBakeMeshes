// 1.2.165 (Fire 11-05): EBM's own copy, so EBM needs no Core. Core carries the same method for its own users; keep the two in step.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Frame-budgeted warm-up: instantiates each prefab at a hidden position and destroys it at the next frame so
    // shader-variant compilation and asset import happen during loading instead of on the first real spawn, where
    // a multi-second compile freezes the game. Callers pass PrewarmOptions, including optional skip rules.
    // ZNetView.m_forceDisableInit is set around each Instantiate in try/finally: without it every clone creates a
    // ZDO that ZNetScene.RemoveObjects keeps tripping over, and being global it must not leak into other code.
    //
    // 1.2.189 (SanEggs 11-06: Jewelcrafting's Projectile.Awake postfix threw a NullReferenceException from this warm-up; Fire 11-08: "make sure
    // ebm is safe for all mods"): clones are made UNDER AN INACTIVE HOLDER, so they are inactive in the hierarchy from the start. No Awake,
    // OnEnable or OnDestroy runs on any component, vanilla or modded, and no other mod's Awake patch is reached. Before 1.2.189 the clone was
    // made active and switched off on the next line, after every Awake had already run. The clone / deserialise cost the warm-up exists for is
    // still paid. Callers may also restrict the set (PrewarmOptions.Eligible); EBM warms only prefabs it can bake or instance.
    internal static class PrewarmCoroutine
    {
        private static readonly Vector3 BelowWorld = new Vector3(0f, -1000f, 0f);
        private static GameObject s_holder;

        // A hidden, inactive parent: anything instantiated under it is inactive in the hierarchy, so Unity calls no Awake / OnEnable on it.
        private static Transform Holder()
        {
            if (s_holder == null)
            {
                s_holder = new GameObject("EBM_PrewarmHolder (inactive)");
                s_holder.SetActive(false);
                s_holder.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
            }
            return s_holder.transform;
        }

        public static IEnumerator Run(
            IList<GameObject> prefabs,
            PrewarmOptions options = null,
            Action<string> logInfo = null,
            Action<string> logWarn = null,
            Action<string> logDebug = null)
        {
            options ??= PrewarmOptions.Default;
            logInfo  ??= UnityEngine.Debug.Log;
            logWarn  ??= UnityEngine.Debug.LogWarning;
            logDebug ??= _ => { };

            if (prefabs == null) yield break;

            int perFrame = Math.Max(1, options.PrefabsPerFrame);
            float budgetMs = Math.Max(1f, options.FrameBudgetMs);
            long budgetTicks = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
            int total = prefabs.Count;

            int processed = 0, skipped = 0, errors = 0, notEligible = 0;   // 1.2.189: notEligible = refused by options.Eligible
            long totalTicks = 0, worstTicks = 0;
            string worstName = string.Empty;

            var runtimeSlowSkips = new HashSet<string>(StringComparer.Ordinal);
            LoadPersistedSkips(options.PersistSlowSkipPath, runtimeSlowSkips, logInfo);
            var nameTokens = ParseSkipTokens(options.SkipNameContains);

            var wall = Stopwatch.StartNew();
            var frameBudget = Stopwatch.StartNew();
            logInfo($"[Prewarm] Starting prefab warm-up: {total} prefabs, budget {budgetMs:F1}ms/frame (hard cap {perFrame}/frame).");

            var frameInstances = new List<GameObject>(perFrame);

            for (int i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i];
                if (prefab == null) { skipped++; continue; }

                // Cheapest first: known-slow (persisted from prior sessions) +
                // config name tokens are string checks — a 29s mega-prefab on
                // the persisted list must not even pay a component scan.
                if (ShouldSkipByName(prefab.name, nameTokens, runtimeSlowSkips))
                {
                    skipped++;
                    if (options.Verbose) logDebug($"[Prewarm] Skipped {prefab.name} (name-pattern / known-slow).");
                    continue;
                }

                // Pre-flight size gate: a single Instantiate is atomic on the
                // main thread — no frame budget can split it. Instantiate cost
                // scales with hierarchy size, so counting transforms (a cheap
                // traversal, no Awake, no shader compile) rejects mega-prefabs
                // (combined-build pieces with thousands of children) BEFORE the
                // first-ever 20-30s freeze, not after.
                if (options.MaxTransformCount > 0)
                {
                    int cap = options.MaxTransformCount;
                    int nodes = CountTransforms(prefab.transform, cap + 1);
                    if (nodes > cap)
                    {
                        skipped++;
                        if (runtimeSlowSkips.Add(prefab.name))
                        {
                            logWarn($"[Prewarm] '{prefab.name}' exceeds {cap} transforms — skipping warm-up "
                                + "(a single mega-prefab Instantiate can freeze the client for tens of seconds; "
                                + "it will warm on first real spawn instead). Persisted for future sessions.");
                            AppendPersistedSkip(options.PersistSlowSkipPath, prefab.name, logWarn);
                        }
                        continue;
                    }
                }

                // 1.2.189: the caller's own filter first (EBM: only what it can bake or instance)
                if (options.Eligible != null)
                {
                    bool eligible;
                    try { eligible = options.Eligible(prefab); } catch { eligible = false; }
                    if (!eligible) { skipped++; notEligible++; continue; }
                }

                bool stateful;
                try { stateful = options.SkipStatefulComponents && HasStatefulComponent(prefab); }   // 1.2.189: a broken prefab is skipped, not fatal
                catch { stateful = true; }
                if (stateful)
                {
                    skipped++;
                    if (options.Verbose) logDebug($"[Prewarm] Skipped {prefab.name} (stateful component).");
                    continue;
                }

                long startTicks = Stopwatch.GetTimestamp();
                GameObject inst = null;

                // ZNetView.m_forceDisableInit scoping: per-Instantiate
                // try/finally so the global flag is on for as little time
                // as possible. Concurrent Instantiates from other code
                // paths during our yields would otherwise see the flag.
                ZNetView.m_forceDisableInit = true;
                try
                {
                    inst = UnityEngine.Object.Instantiate(prefab, BelowWorld, Quaternion.identity, Holder());   // 1.2.189: inactive in hierarchy, no Awake
                    frameInstances.Add(inst);
                }
                catch (Exception ex)
                {
                    errors++;
                    logWarn($"[Prewarm] Instantiate failed for '{prefab.name}': {ex.Message}");
                }
                finally
                {
                    ZNetView.m_forceDisableInit = false;
                }

                long elapsed = Stopwatch.GetTimestamp() - startTicks;
                totalTicks += elapsed;
                if (elapsed > worstTicks) { worstTicks = elapsed; worstName = prefab.name; }
                processed++;

                if (options.SlowInstantiateThresholdMs > 0f)
                {
                    double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= options.SlowInstantiateThresholdMs && runtimeSlowSkips.Add(prefab.name))
                    {
                        // Persist: without this the stall repeats on EVERY login
                        // (each prefab warms once per session, so a session-only
                        // list never actually saved anyone anything).
                        logWarn(
                            $"[Prewarm] '{prefab.name}' took {elapsedMs:F0}ms to instantiate " +
                            $"(threshold {options.SlowInstantiateThresholdMs:F0}ms). " +
                            $"Persisted to the slow-prefab skip list — future sessions won't warm it.");
                        AppendPersistedSkip(options.PersistSlowSkipPath, prefab.name, logWarn);
                    }
                }

                if (options.Verbose)
                {
                    double ms = elapsed * 1000.0 / Stopwatch.Frequency;
                    if (ms > 1.0) logDebug($"[Prewarm]   {prefab.name} = {ms:F2}ms");
                }

                bool frameFull = frameBudget.ElapsedTicks >= budgetTicks
                                 || frameInstances.Count >= perFrame;
                if (frameFull)
                {
                    yield return null;
                    DestroyBatch(frameInstances);
                    frameBudget.Restart();
                }
            }

            if (frameInstances.Count > 0)
            {
                yield return null;
                DestroyBatch(frameInstances);
            }

            wall.Stop();
            double wallTotalMs = totalTicks * 1000.0 / Stopwatch.Frequency;
            double worstMs = worstTicks * 1000.0 / Stopwatch.Frequency;
            logInfo(
                $"[Prewarm] DONE in {wall.Elapsed.TotalSeconds:F2}s wall — " +
                $"{processed} processed (inactive, no Awake), {skipped} skipped ({notEligible} not bake-able), {errors} errors. " +
                $"Total Instantiate cost: {wallTotalMs:F0}ms. " +
                $"Worst single: {worstMs:F1}ms ({worstName}).");
        }

        private static void DestroyBatch(List<GameObject> batch)
        {
            for (int j = 0; j < batch.Count; j++)
                if (batch[j] != null) UnityEngine.Object.Destroy(batch[j]);
            batch.Clear();
        }

        private static string[] ParseSkipTokens(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();
            var parts = raw.Split(',');
            var tokens = new List<string>(parts.Length);
            foreach (var part in parts)
            {
                string trimmed = part?.Trim();
                if (!string.IsNullOrEmpty(trimmed)) tokens.Add(trimmed);
            }
            return tokens.ToArray();
        }

        private static bool ShouldSkipByName(string prefabName, string[] tokens, HashSet<string> runtimeSlowSkips)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            if (runtimeSlowSkips.Contains(prefabName)) return true;
            for (int i = 0; i < tokens.Length; i++)
                if (prefabName.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        // Transform-hierarchy node count with an early-exit cap, so probing a
        // 10k-node mega-prefab costs cap+1 visits instead of a full traversal
        // (and no array allocation like GetComponentsInChildren would).
        private static int CountTransforms(Transform root, int cap)
        {
            int count = 1;
            for (int i = 0; i < root.childCount && count < cap; i++)
                count += CountTransforms(root.GetChild(i), cap - count);
            return count;
        }

        // One prefab name per line; '#' comments allowed. Missing file = empty.
        private static void LoadPersistedSkips(string path, HashSet<string> into, Action<string> logInfo)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (!System.IO.File.Exists(path)) return;
                int added = 0;
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    string trimmed = line?.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (into.Add(trimmed)) added++;
                }
                if (added > 0)
                    logInfo($"[Prewarm] Loaded {added} persisted slow-prefab skip(s) from {System.IO.Path.GetFileName(path)}.");
            }
            catch (Exception ex)
            {
                logInfo($"[Prewarm] Could not read slow-skip file '{path}': {ex.Message}");
            }
        }

        private static void AppendPersistedSkip(string path, string prefabName, Action<string> logWarn)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(prefabName)) return;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(path, prefabName + Environment.NewLine);
            }
            catch (Exception ex)
            {
                logWarn($"[Prewarm] Could not persist slow-skip '{prefabName}': {ex.Message}");
            }
        }

        // Prefabs that throw or log errors when instantiated at the prewarm position before a world exists: their Awake
        // needs world state (TreeLog reads the biome, TerrainComp the heightmap), a component requires the ZNetView that disabled init destroys
        // (TriggerPersistentEventOnDestroy), or vanilla authoring left a cloth wind shelter without its cloth.
        private static bool HasStatefulComponent(GameObject prefab)
        {
            if (prefab.GetComponentInChildren<Player>()           != null) return true;
            if (prefab.GetComponentInChildren<Character>()        != null) return true;
            if (prefab.GetComponentInChildren<MineRock>()         != null) return true;
            if (prefab.GetComponentInChildren<MineRock5>()        != null) return true;
            if (prefab.GetComponentInChildren<Fire>()             != null) return true;
            if (prefab.GetComponentInChildren<Fireplace>()        != null) return true;
            if (prefab.GetComponentInChildren<ShieldGenerator>()  != null) return true;
            if (prefab.GetComponentInChildren<TombStone>()        != null) return true;
            if (prefab.GetComponentInChildren<Valkyrie>()         != null) return true;
            if (prefab.GetComponentInChildren<RandomFlyingBird>() != null) return true;
            if (prefab.GetComponentInChildren<Ragdoll>()          != null) return true;
            if (prefab.GetComponentInChildren<DungeonGenerator>() != null) return true;
            if (prefab.GetComponentInChildren<LocationProxy>()    != null) return true;
            if (prefab.GetComponentInChildren<TreeLog>()          != null) return true;
            if (prefab.GetComponentInChildren<TerrainComp>()      != null) return true;
            if (prefab.GetComponentInChildren<TriggerPersistentEventOnDestroy>() != null) return true;
            // 1.2.189: gameplay objects are never warmed, whoever patches them (Jewelcrafting's Projectile.Awake postfix threw from here before
            // the inactive holder). Belt and braces: with the holder no Awake runs anyway.
            if (prefab.GetComponentInChildren<Projectile>()      != null) return true;
            if (prefab.GetComponentInChildren<ItemDrop>()        != null) return true;
            if (prefab.GetComponentInChildren<Ship>()            != null) return true;
            if (prefab.GetComponentInChildren<Vagon>()           != null) return true;
            if (prefab.GetComponentInChildren<CreatureSpawner>() != null) return true;
            if (prefab.GetComponentInChildren<SpawnArea>()       != null) return true;
            if (prefab.GetComponentInChildren<OfferingBowl>()    != null) return true;
            if (prefab.GetComponentInChildren<Aoe>()             != null) return true;
            if (prefab.GetComponentInChildren<SpawnAbility>()    != null) return true;
            foreach (PlayerClothWindShelter shelter in prefab.GetComponentsInChildren<PlayerClothWindShelter>())
                if (shelter.GetComponent("MagicaCloth") == null) return true;
            return false;
        }
    }

    internal class PrewarmOptions
    {
        public static readonly PrewarmOptions Default = new PrewarmOptions();

        // Maximum prefabs Instantiated per frame even if the time budget
        // hasn't been spent. Hard upper bound — typical config is the
        // time budget governing, this only matters when many prefabs
        // Instantiate cheaply enough to overflow the frame.
        public int PrefabsPerFrame = 24;

        // Wall-time budget per frame in milliseconds. Yield as soon as
        // this elapses, regardless of how many prefabs that took.
        public float FrameBudgetMs = 8f;

        // When a single prefab's Instantiate exceeds this, remember its
        // name (and persist it via PersistSlowSkipPath when set) so it is
        // never warmed again. Zero = disabled.
        public float SlowInstantiateThresholdMs = 500f;

        // Pre-flight hierarchy gate: skip any prefab with more transform
        // nodes than this BEFORE Instantiating. A single Instantiate is
        // atomic on the main thread — no frame budget can split it — and a
        // combined-build mega-prefab can freeze the client for 20-30s. The
        // count is a cheap capped traversal. Zero = disabled.
        public int MaxTransformCount = 0;

        // File persisting slow/oversize prefab names across sessions (one
        // name per line, '#' comments). Loaded at Run start; appended when
        // the threshold or the transform gate trips. Null/empty = in-memory
        // only (the pre-persistence behavior, which re-pays every stall on
        // every login).
        public string PersistSlowSkipPath = null;

        // Per-line debug logging during the pass.
        public bool Verbose = false;

        // Comma-separated substring tokens. Any prefab whose name
        // contains any token (case-insensitive) is skipped. Use for
        // known-slow / known-broken bundle prefabs.
        public string SkipNameContains = string.Empty;

        // Apply the built-in stateful-component skip list (Player,
        // Character, MineRock, Fire, Valkyrie, etc.). Set false to
        // attempt Instantiate on every prefab (rare — used for shader
        // pre-warm runs that don't care about Awake errors).
        public bool SkipStatefulComponents = true;

        // 1.2.189: the caller's own eligibility test, run before the built-in list (null = every prefab). A throw counts as "not eligible".
        // EBM passes "can EBM bake or instance this" so it never touches gameplay prefabs.
        public Func<GameObject, bool> Eligible = null;
    }
}
