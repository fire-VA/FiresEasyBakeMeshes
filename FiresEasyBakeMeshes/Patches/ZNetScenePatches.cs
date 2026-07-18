using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class ZNetScene_Awake_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ZNetScene __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value) return;

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
                var prewarmEnum = FiresCore.Async.PrewarmCoroutine.Run(
                    scene.m_prefabs,
                    prewarmOptions,
                    EasyBakeLog.Info,
                    EasyBakeLog.Warn,
                    EasyBakeLog.Debug);
                while (prewarmEnum.MoveNext())
                    yield return prewarmEnum.Current;
            }

            // ── PASS 2: CONDITIONAL REGISTRY ───────────────────────────────
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
            {
                EasyBakeLog.Info(
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
                EasyBakeLog.Info(
                    "[Cache] Timed out waiting for world UID — skipping material registry. " +
                    "Cache reattach disabled this session; bakes will happen fresh.");
                yield break;
            }

            if (!EasyBake.MeshCacheStore.WorldHasCache(worldUid))
            {
                EasyBakeLog.Info(
                    $"[Cache] No on-disk cache for world {worldUid:x16} — skipping material registry. " +
                    "First-bake-from-scratch path is fine without it; next session will have a cache " +
                    "and the registry will build then.");
                yield break;
            }

            EasyBakeLog.Info($"[Cache] World {worldUid:x16} has a cache — building material registry.");
            var registryEnum = EasyBake.MeshCacheStore.BuildMaterialRegistryAsync(scene);
            while (registryEnum.MoveNext())
                yield return registryEnum.Current;
            yield break;
        }

        // Extracted so the prewarm option construction is reusable + readable.
        private static FiresCore.Async.PrewarmOptions BuildPrewarmOptions()
        {
            return new FiresCore.Async.PrewarmOptions
            {
                PrefabsPerFrame             = FiresEasyBakeMeshesPlugin.PrewarmPrefabsPerFrame.Value,
                FrameBudgetMs               = FiresEasyBakeMeshesPlugin.PrewarmFrameBudgetMs != null
                                                  ? FiresEasyBakeMeshesPlugin.PrewarmFrameBudgetMs.Value
                                                  : 8f,
                SlowInstantiateThresholdMs  = FiresEasyBakeMeshesPlugin.PrewarmSlowInstantiateThresholdMs.Value,
                MaxTransformCount           = FiresEasyBakeMeshesPlugin.PrewarmMaxTransformCount.Value,
                // Slow/oversize prefabs persist here so a 29s mega-prefab
                // (ConsMassiveTower) freezes at most ONE login ever, not every one.
                PersistSlowSkipPath         = System.IO.Path.Combine(
                                                  BepInEx.Paths.ConfigPath, "FiresEasyBakeMeshes", "prewarm_slow_skips.txt"),
                Verbose                     = FiresEasyBakeMeshesPlugin.PrewarmVerbose.Value,
                SkipNameContains            = FiresEasyBakeMeshesPlugin.PrewarmSkipNameContains.Value ?? string.Empty,
                SkipStatefulComponents      = true,
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
        [HarmonyPrefix]
        public static bool Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingActive()) return true;

            // Layer 1: keepalive injection. Pass player center + vanilla's
            // active area so keepalive can skip injecting for zones vanilla
            // already covered in currentNearObjects — those would just be
            // duplicate AddRange work.
            if (FiresEasyBakeMeshesPlugin.ZoneKeepaliveEnabled.Value)
            {
                var zs = ZoneSystem.instance;
                var net = ZNet.instance;
                if (zs != null && net != null)
                {
                    var center = ZoneSystem.GetZone(net.GetReferencePosition());
                    int vanillaCovered = zs.m_activeArea; // strict near area
                    EasyBake.ZoneKeepalive.AppendKeepaliveZDOs(currentNearObjects, center, vanillaCovered);
                }
                else
                {
                    EasyBake.ZoneKeepalive.AppendKeepaliveZDOs(currentNearObjects, new Vector2i(0, 0), -1);
                }
            }

            // Layer 2: time-sliced destroy. Returns true if we did the work;
            // we skip vanilla in that case. Returns false on reflection
            // failure — fall through and let vanilla run normally.
            if (FiresEasyBakeMeshesPlugin.DestroyTimeSliceEnabled.Value)
            {
                long t = EasyBake.Probe.Start();
                bool handled = EasyBake.DestroyTimeSlicer.Run(__instance, currentNearObjects, currentDistantObjects);
                EasyBake.Probe.Stop("EasyBake:destroy", t);
                if (handled) return false;
            }

            return true;
        }
    }
}
