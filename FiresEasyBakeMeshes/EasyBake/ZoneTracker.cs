using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    internal static class ZoneTracker
    {
        private class ZoneState
        {
            public Vector2i Coord;
            public HashSet<WearNTear> Pieces = new HashSet<WearNTear>();
            public float LastChangeUnscaledTime;
            public bool Baked;
            public bool Dirty;
            // True iff the player is currently within range of this zone (it's
            // loaded in ZoneSystem). When the player walks away, ZoneActive flips
            // to false but the cached BakeResult stays alive so the next visit
            // can reattach instead of rebaking.
            public bool ZoneActive = true;
            public MeshBaker.BakeResult Bake;
        }

        private static readonly Dictionary<Vector2i, ZoneState> _zones = new Dictionary<Vector2i, ZoneState>();
        private static readonly List<Vector2i> _dropScratch = new List<Vector2i>();

        public static void OnInstanceCreated(GameObject go)
        {
            if (go == null) return;
            var wnt = go.GetComponent<WearNTear>();
            if (wnt == null) return;
            if (!InvulnerableClassifier.IsInvulnerable(wnt)) return;
            if (HasBakeUnsafeComponent(go)) return;

            var coord = ZoneSystem.GetZone(wnt.transform.position);
            if (!_zones.TryGetValue(coord, out var state))
            {
                state = new ZoneState { Coord = coord };
                _zones.Add(coord, state);

                // Preload check: the disk read for this zone (if it had a
                // cache file) already happened off-thread in MeshCacheStore's
                // background preload worker. TryGetPreloaded is a single
                // ConcurrentDictionary.TryGet — no I/O, no allocation in the
                // miss case. ConstructFromCache does the Unity-side work
                // (Mesh.SetVertices etc.) on this thread; it's fast (sub-ms
                // for typical zones) because the arrays are already in RAM.
                //
                // If the background preload hasn't reached this zone yet,
                // TryGetPreloaded returns false and we fall through to the
                // normal "no cache, bake fresh" path. This zone misses the
                // cache for this session but Login N+1 will pick it up.
                if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value
                    && MeshCacheStore.TryGetPreloaded(coord, out var cachedData))
                {
                    state.Bake = MeshCacheStore.ConstructFromCache(cachedData);
                    if (state.Bake != null)
                    {
                        state.Baked = true;
                        // Only pin via keepalive if the cached bake actually
                        // contains combined mesh batches. Empty bakes (no
                        // batches survived the bake-time filtering) provide
                        // no rendering win — pinning them just wastes memory
                        // and per-tick FindSectorObjects work.
                        if (state.Bake.Batches != null && state.Bake.Batches.Count > 0)
                            ZoneKeepalive.MarkActive(coord);
                        EasyBakeLog.Info(
                            $"[Cache] Zone ({coord.x},{coord.y}) constructed from preload: " +
                            $"{state.Bake.Batches.Count} batches, {state.Bake.ContributorIdentities?.Count ?? 0} cached identities.");
                    }
                }
            }
            if (!state.Pieces.Add(wnt)) return;

            state.LastChangeUnscaledTime = Time.unscaledTime;

            // Cache reattach path: if this zone has a live BakeResult AND this
            // piece's identity matches an entry in ContributorIdentities, just
            // silence the fresh renderers + LODGroup without a rebake. The
            // combined mesh already covers this piece's contribution.
            //
            // On a cache MISS we deliberately do nothing: we don't mark dirty,
            // we don't bake. ContributorIdentities only holds pieces that fed a
            // batch in the original bake — sub-threshold pieces (material with
            // < MinPiecesPerBatch contributors) are in state.Pieces but were
            // never in ContributorIdentities to begin with, so every one of
            // them would "miss" the cache and trigger a rebake. That defeats
            // the whole point of persisting the bake: we'd pay the disk-load
            // cost AND the rebake cost on every login.
            //
            // Tradeoff: pieces added since the cache was written render via
            // their own renderers (un-batched) rather than being folded into
            // the combined mesh. Small visual / draw-call cost; correct.
            // To force a fresh bake after major edits, delete the cache file
            // for this zone manually.
            if (state.Baked && state.Bake != null && state.Bake.ContributorIdentities != null)
            {
                var identity = MeshBaker.PieceIdentity.From(go, wnt.transform.position);
                if (state.Bake.ContributorIdentities.Contains(identity))
                    MeshBaker.DisableForCacheHit(go, state.Bake);
                return;
            }

            // No cache yet — mark dirty if we'd already baked (admin placed a
            // new piece into a baked zone). Otherwise the first bake will pick
            // it up at settle time.
            if (state.Baked) state.Dirty = true;
        }

        public static void OnPieceDestroyed(WearNTear wnt)
        {
            if (wnt == null) return;
            InvulnerableClassifier.Forget(wnt);
            foreach (var state in _zones.Values)
            {
                if (state.Pieces.Remove(wnt))
                {
                    state.LastChangeUnscaledTime = Time.unscaledTime;
                    // Deliberately NOT marking dirty here. Piece destruction
                    // happens both for admin removes (real change) AND for
                    // ZoneSystem unloads (just the player walking away — the
                    // pieces will come back at the same positions next visit).
                    // Without a way to distinguish, marking dirty would force a
                    // rebake every zone-crossing, defeating the cache.
                    // Tradeoff: admin-removed pieces leave ghost geometry in the
                    // cached mesh until that zone is forcibly rebaked.
                    return;
                }
            }
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            float settleDelay = FiresEasyBakeMeshesPlugin.BatchingSettleDelaySeconds.Value;
            int minPerZone = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerZone.Value;

            var zoneSystem = ZoneSystem.instance;
            _dropScratch.Clear();

            // Soft unload / reload pass. Zones that ZoneSystem stops tracking
            // get their combined-mesh parent hidden but kept in memory; their
            // disable lists are wiped because the underlying WearNTear/Renderer
            // references go Unity-null as ZNetScene destroys the instances.
            // Zones that come back get their combined-mesh parent re-shown.
            //
            // Keepalive override: if ZoneKeepalive holds this coord, the
            // per-piece ZNetViews are still alive (we pin them via the
            // RemoveObjects prefix), so we skip the unload branch entirely.
            // Bake.Parent stays visible, state.Pieces stays populated, and the
            // next time the player walks back the combined mesh is already on
            // screen — no Mesh.SetVertices, no mass re-Instantiate, no hitch.
            foreach (var kv in _zones)
            {
                var coord = kv.Key;
                var state = kv.Value;
                bool zoneLoaded = zoneSystem != null && zoneSystem.IsZoneLoaded(coord);
                bool keptAlive = FiresEasyBakeMeshesPlugin.ZoneKeepaliveEnabled.Value
                    && ZoneKeepalive.IsKeptAlive(coord);

                if (!zoneLoaded && state.ZoneActive && !keptAlive)
                {
                    if (state.Baked && state.Bake != null && state.Bake.Parent != null)
                        state.Bake.Parent.SetActive(false);
                    state.Pieces.Clear();
                    if (state.Bake != null)
                    {
                        state.Bake.DisabledRenderers?.Clear();
                        state.Bake.DisabledLodGroups?.Clear();
                    }
                    state.ZoneActive = false;
                }
                else if (zoneLoaded && !state.ZoneActive)
                {
                    if (state.Baked && state.Bake != null && state.Bake.Parent != null)
                        state.Bake.Parent.SetActive(true);
                    state.ZoneActive = true;
                    state.LastChangeUnscaledTime = now;
                }
            }

            // Now the per-frame bake decision. Only active zones with enough
            // settled invulnerable pieces qualify. A zone that's Baked AND not
            // Dirty is in steady state — skip; the combined mesh keeps drawing.
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive) continue;
                if (state.Pieces.Count < minPerZone) continue;
                if (now - state.LastChangeUnscaledTime < settleDelay) continue;
                if (state.Baked && !state.Dirty) continue;

                if (state.Baked && state.Dirty) TearDown(state);
                long tBake = Probe.Start();
                state.Bake = MeshBaker.Bake(state.Coord, state.Pieces);
                Probe.Stop("EasyBake:bake", tBake);
                state.Baked = true;
                state.Dirty = false;
                // Only pin via keepalive if the fresh bake produced batches.
                // Zones with sub-threshold materials or all-PropBlock /
                // all-non-LOD0 renderers bake to 0 batches and don't benefit
                // from being kept alive across zone-cross.
                if (state.Bake != null && state.Bake.Batches != null && state.Bake.Batches.Count > 0)
                    ZoneKeepalive.MarkActive(state.Coord);

                // Persist the freshly-baked result so the next session can
                // skip CombineMeshes entirely. Save is fail-soft (logs + moves
                // on) — disk I/O issues mustn't break gameplay.
                if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
                {
                    long uid = MeshCacheStore.TryGetWorldUid();
                    if (uid != 0L) MeshCacheStore.Save(uid, state.Coord, state.Bake);
                }
            }

            // Anything that fully collapses (an unloaded zone we never saw return,
            // or a zone where all pieces were genuinely destroyed while loaded) is
            // dropped. The latter case — zoneLoaded && Pieces.Count == 0 — only
            // happens if an admin demolishes every invulnerable piece in a loaded
            // zone, which we treat as cache invalidation.
            foreach (var kv in _zones)
            {
                var state = kv.Value;
                if (state.ZoneActive && state.Pieces.Count == 0 && state.Baked)
                {
                    TearDown(state);
                    _dropScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < _dropScratch.Count; i++) _zones.Remove(_dropScratch[i]);
        }

        private static void TearDown(ZoneState state)
        {
            if (state.Bake != null)
            {
                MeshBaker.Restore(state.Bake);
                state.Bake = null;
            }
            state.Baked = false;
            state.Dirty = false;
        }

        public static void Reset()
        {
            foreach (var s in _zones.Values) TearDown(s);
            _zones.Clear();
            InvulnerableClassifier.Reset();
            ZoneKeepalive.Reset();
            DestroyTimeSlicer.Reset();
            SectorInstanceMirror.Reset();
            StaticPieceZSyncSkip.Reset();
        }

        // Two categories of unsafe-to-bake components:
        //
        // A. NRE-on-renderer-disable. Components whose lifecycle assumes their
        //    own renderers stay live and whose UpdateXxx methods do NOT
        //    null-check intermediates before iterating them. Baking these
        //    would leave their cached renderer lists pointing at renderers we
        //    disabled, and vanilla code NREs on the next tick.
        //      ShieldGenerator     — UpdateShield iterates m_meshRenderers.
        //      MineRock5 / MineRock — UpdateMesh rebuilds per-cell mesh.
        //
        // B. Moving / animated visuals. The combined mesh is a static snapshot
        //    at bake time. If a piece's visual is supposed to MOVE in response
        //    to player interaction, baking freezes it in the snapshot pose
        //    while the underlying logic still ticks (door open state, chest
        //    lid, etc.) — invisible. These pieces need to keep their own live
        //    renderers so vanilla animation drives them.
        //      Door       — covers regular doors AND gates (both vanilla use the
        //                   same Door component class).
        //      Container  — chests, drawers, any storage with an animated lid.
        //
        // Two filter robustness rules learned the hard way:
        //   - includeInactive: true. ShieldGenerator at ShieldGenerator.cs:83-85
        //     supports living on a child whose sibling m_enabledObject/m_disabledObject
        //     toggles active state. A shield that spawns with fuel=0 leaves
        //     m_enabledObject inactive — if ShieldGenerator sits on it, the
        //     default GetComponentInChildren (includeInactive=false) misses it
        //     and the filter silently fails.
        //   - GetComponentInParent too. WearNTear and the unsafe component are
        //     not guaranteed siblings; in some prefabs the unsafe component is
        //     on the root and WearNTear is on a child (or vice versa). Check
        //     both directions to make this filter robust to prefab shape.
        private static bool HasBakeUnsafeComponent(GameObject go)
        {
            // Category A — NRE on renderer disable
            if (HasComponentAnywhere<ShieldGenerator>(go)) return true;
            if (HasComponentAnywhere<MineRock5>(go))       return true;
            if (HasComponentAnywhere<MineRock>(go))        return true;
            // Category B — animated/moving visuals that the static combined
            // mesh can't reproduce.
            if (HasComponentAnywhere<Door>(go))            return true;
            if (HasComponentAnywhere<Container>(go))       return true;
            return false;
        }

        private static bool HasComponentAnywhere<T>(GameObject go) where T : Component
        {
            if (go.GetComponentInChildren<T>(includeInactive: true) != null) return true;
            if (go.GetComponentInParent<T>(includeInactive: true)   != null) return true;
            return false;
        }
    }
}
