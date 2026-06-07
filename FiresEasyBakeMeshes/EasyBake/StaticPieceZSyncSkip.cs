using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // Bulk-disable ZSyncTransform on static building pieces.
    //
    // ZSyncTransform is a MonoBehaviour:IMonoUpdater that ticks every frame
    // (CustomLateUpdate -> OwnerSync) and every fixed update (CustomFixedUpdate
    // -> ClientSync) on every active instance in the scene. For pieces that
    // never move post-placement (walls, floors, roofs, posts, fences, decorative
    // beams, …) every tick is pure overhead — the owner side compares
    // m_positionCached.Equals(transform.position), finds nothing changed, exits;
    // the non-owner side reads the ZDO position, finds it matches the cached
    // transform, exits.
    //
    // We can't beat vanilla's per-call cost with a Harmony prefix — its
    // IsOwner / IsValid fast-path is already ~30 ns. The only real win is
    // taking the component OUT of ZSyncTransform.Instances so the per-frame
    // iteration over the list never visits it. Setting enabled=false triggers
    // OnDisable which calls Instances.Remove. Re-enabling (e.g. another mod
    // flipping the bool) re-adds via OnEnable. No bookkeeping on our side.
    //
    // Eligibility — conservatively narrow because the consequence of being
    // wrong is "this piece silently de-syncs":
    //   - Must have a Piece component (player-built building piece, not a mob
    //     or item or projectile).
    //   - transform.parent must be null (rules out pieces attached to a ship
    //     or cart — vanilla syncs relative position for parented setups).
    //   - ZSyncTransform must NOT have m_syncBodyVelocity or m_characterParentSync
    //     set (both indicate something moves).
    //   - Any Rigidbody must be kinematic (non-kinematic = physics-driven).
    //   - No "this animates its own transform" component anywhere in the
    //     subtree — same surface as the mesh-bake unsafe list plus a broader
    //     interactable safelist (Sign, Smelter, CookingStation, Pickable, …).
    //
    // Eligibility is a property of the PREFAB, not the instance, so we cache
    // by prefab hash after the first evaluation. Cache size is bounded by the
    // ~3000 prefabs in ObjectDB; per-instance cost after warmup is one dict
    // lookup + the transform.parent null-check.
    //
    // Risk surface:
    //   * Admin teleport on a static piece would move the transform without
    //     bumping the ZDO (ZSync.OwnerSync isn't running to write). In solo
    //     the local player IS the source of truth so no one sees a wrong
    //     position. In multiplayer non-owners would see the piece at its
    //     original location until reload. Acceptable — admin teleporting a
    //     building piece is rare.
    //   * If a mod adds a moving behavior to a vanilla static piece prefab
    //     (e.g. patches an Animator onto a Wall) it'd render at the original
    //     position for non-owners. Mod-of-mod scenario; if we hit it, raise
    //     the master toggle (ZSyncStaticSkipEnabled=false).
    internal static class StaticPieceZSyncSkip
    {
        // Keyed by ZDO.GetPrefab() — the int prefab hash. Value: is this prefab
        // safe to disable ZSync on. Populated lazily on first sight.
        private static readonly Dictionary<int, bool> _prefabEligible = new Dictionary<int, bool>();

        private static int _disabledCount;
        private static int _evaluatedCount;
        private static float _lastReportTime;

        public static void TryDisable(GameObject go)
        {
            if (!FiresEasyBakeMeshesPlugin.ZSyncStaticSkipEnabled.Value) return;
            if (go == null) return;

            // Fastest possible bail: no ZSync component, nothing to disable.
            var zsync = go.GetComponent<ZSyncTransform>();
            if (zsync == null) return;

            _evaluatedCount++;

            // Instance-level checks that aren't prefab-cacheable.
            //
            // transform.parent: if the piece is parented to another GameObject
            // (ship deck, cart bed) ZSyncTransform handles relative-position
            // sync — we'd break that by disabling. Player-built pieces dropped
            // straight into the world have null parent.
            if (go.transform.parent != null) return;

            // Per-prefab fast path. After warmup every check lands here.
            int prefabHash = TryGetPrefabHash(go);
            if (prefabHash != 0 && _prefabEligible.TryGetValue(prefabHash, out bool cachedOk))
            {
                if (!cachedOk) return;
                zsync.enabled = false;
                _disabledCount++;
                return;
            }

            // Cache miss — do the full check. The result is cached for every
            // future instance of this prefab.
            bool eligible = EvaluateFresh(go, zsync);
            if (prefabHash != 0) _prefabEligible[prefabHash] = eligible;
            if (!eligible) return;

            zsync.enabled = false;
            _disabledCount++;
        }

        public static void MaybeReport()
        {
            if (!FiresEasyBakeMeshesPlugin.ZSyncStaticSkipVerbose.Value) return;
            float now = Time.unscaledTime;
            if (now - _lastReportTime < 30f) return;
            _lastReportTime = now;
            EasyBakeLog.Info(
                $"[ZSync-skip] evaluated={_evaluatedCount} disabled={_disabledCount} " +
                $"prefabs-classified={_prefabEligible.Count} (cumulative)");
        }

        public static void Reset()
        {
            _prefabEligible.Clear();
            _disabledCount = 0;
            _evaluatedCount = 0;
            _lastReportTime = 0f;
        }

        private static bool EvaluateFresh(GameObject go, ZSyncTransform zsync)
        {
            // ZSyncTransform's own flags. m_syncBodyVelocity = the piece writes
            // its body's velocity to the ZDO each frame — something moves.
            // m_characterParentSync = the piece is meant to sync as a child of
            // a moving Character (boats, riders). Either flag means "leave it
            // alone, vanilla needs the tick."
            if (zsync.m_syncBodyVelocity) return false;
            if (zsync.m_characterParentSync) return false;

            // Non-kinematic rigidbody = Unity physics moves it. Carts, projectiles,
            // dropped items, ships. Vanilla ZSyncTransform handles those.
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) return false;

            // Must be a player-built piece. Excludes mobs, items, projectiles,
            // world-gen vegetation/rocks.
            if (go.GetComponent<Piece>() == null) return false;

            // The big interactable / animated-transform safelist. Anything that
            // could plausibly move its transform — through animation, scripted
            // rotation, interaction state — stays vanilla.
            if (HasAnimatedComponent(go)) return false;

            return true;
        }

        // Mirrors ZoneTracker.HasBakeUnsafeComponent + broader interactable set.
        // The mesh-bake filter only cares about pieces that animate their
        // visual; we care about pieces that animate their transform OR have any
        // interactive component that might do so (admin actions, mod-added
        // behaviors). When in doubt, keep vanilla ZSync running.
        private static bool HasAnimatedComponent(GameObject go)
        {
            // Category A — components from the mesh-bake unsafe set whose
            // transform also might move with state changes.
            if (HasComponentAnywhere<Door>(go))            return true;
            if (HasComponentAnywhere<Container>(go))       return true;
            if (HasComponentAnywhere<ShieldGenerator>(go)) return true;
            if (HasComponentAnywhere<MineRock5>(go))       return true;
            if (HasComponentAnywhere<MineRock>(go))        return true;

            // Category B — interactable stations / fixtures. Their main
            // transform IS typically static, but any one of them could grow
            // an animation in a future patch or via a mod. Conservative.
            if (HasComponentAnywhere<Sign>(go))            return true;
            if (HasComponentAnywhere<ItemStand>(go))       return true;
            if (HasComponentAnywhere<ArmorStand>(go))      return true;
            if (HasComponentAnywhere<Smelter>(go))         return true;
            if (HasComponentAnywhere<CookingStation>(go))  return true;
            if (HasComponentAnywhere<Fermenter>(go))       return true;
            if (HasComponentAnywhere<Beehive>(go))         return true;
            if (HasComponentAnywhere<Pickable>(go))        return true;
            if (HasComponentAnywhere<TeleportWorld>(go))   return true;
            if (HasComponentAnywhere<Fireplace>(go))       return true;
            if (HasComponentAnywhere<CraftingStation>(go)) return true;
            if (HasComponentAnywhere<PrivateArea>(go))     return true;
            if (HasComponentAnywhere<Switch>(go))          return true;
            if (HasComponentAnywhere<Bed>(go))             return true;

            // Category C — vehicles and large physics bodies. Should be
            // caught above by the rigidbody / Piece checks, but defensive.
            if (HasComponentAnywhere<Ship>(go))            return true;
            if (HasComponentAnywhere<Vagon>(go))           return true;

            return false;
        }

        private static bool HasComponentAnywhere<T>(GameObject go) where T : Component
        {
            if (go.GetComponentInChildren<T>(includeInactive: true) != null) return true;
            if (go.GetComponentInParent<T>(includeInactive: true) != null) return true;
            return false;
        }

        private static int TryGetPrefabHash(GameObject go)
        {
            var nv = go.GetComponent<ZNetView>();
            if (nv == null) return 0;
            var zdo = nv.GetZDO();
            if (zdo == null) return 0;
            return zdo.GetPrefab();
        }
    }
}
