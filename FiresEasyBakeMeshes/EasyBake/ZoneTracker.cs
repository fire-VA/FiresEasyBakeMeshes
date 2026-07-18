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
            // Set when this zone's bake came from the on-disk cache. After the
            // zone settles, a one-shot reconciliation checks that every cached
            // contributor still has a live piece — pieces removed while this
            // client was offline (or before the removal-detection fix shipped)
            // otherwise ghost-render out of the stale combined mesh forever.
            public bool NeedsCacheValidation;
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
                    var swCache = System.Diagnostics.Stopwatch.StartNew();
                    state.Bake = MeshCacheStore.ConstructFromCache(cachedData);
                    swCache.Stop();
                    if (state.Bake != null)
                    {
                        state.Baked = true;
                        state.NeedsCacheValidation = true;
                        // Only pin via keepalive if the cached bake actually
                        // contains combined mesh batches. Empty bakes (no
                        // batches survived the bake-time filtering) provide
                        // no rendering win — pinning them just wastes memory
                        // and per-tick FindSectorObjects work.
                        if (state.Bake.Batches != null && state.Bake.Batches.Count > 0)
                            ZoneKeepalive.MarkActive(coord);
                        BakeSummary.RecordCacheConstruct(
                            state.Bake.Batches?.Count ?? 0, swCache.ElapsedMilliseconds);
                        if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
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
                    // Deliberately NOT marking dirty here — GameObject destruction
                    // fires for both real removes AND zone unloads (the pieces come
                    // back at the same positions next visit), and marking dirty on
                    // unload would force a rebake every zone-crossing. REAL removals
                    // are handled by OnZdoDestroyed below: a genuine remove destroys
                    // the piece's ZDO, a zone unload never does (persistent ZDOs
                    // survive) — that's the distinguisher this path lacks.
                    return;
                }
            }
        }

        // Real-removal detection, driven by ZNetScene.OnZDODestroyed (fires on
        // every client when a ZDO is genuinely destroyed: vanilla remove,
        // Infinity Hammer pick, World Edit undo — never on zone unload). If the
        // destroyed ZDO fed a live combined mesh, queue a settle-delayed rebake
        // so its geometry drops out. Before this, removed pieces ghost-rendered
        // in the bake until a manual rebake — player-visible as "the piece is
        // gone but its image is still there" when IH picks up a baked piece.
        public static void OnZdoDestroyed(ZDO zdo)
        {
            if (zdo == null) return;
            Vector3 pos = zdo.GetPosition();
            if (!_zones.TryGetValue(ZoneSystem.GetZone(pos), out var state)) return;
            if (!state.Baked || state.Bake == null) return;
            var ids = state.Bake.ContributorIdentities;
            if (ids == null || ids.Count == 0) return;

            var identity = new MeshBaker.PieceIdentity
            {
                PrefabHash = zdo.GetPrefab(),
                X = Mathf.RoundToInt(pos.x * 100f),
                Y = Mathf.RoundToInt(pos.y * 100f),
                Z = Mathf.RoundToInt(pos.z * 100f),
            };
            if (!ids.Remove(identity)) return;   // not part of the combined mesh

            state.Dirty = true;
            state.LastChangeUnscaledTime = Time.unscaledTime;
            if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                EasyBakeLog.Info($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): batched piece removed for real — rebake queued.");
        }

        // Infinity Hammer builds its placement ghost by Instantiate()ing the
        // LIVE hovered piece. If this zone is baked, the source's renderers/
        // LODGroups are disabled and the clone inherits that — an invisible
        // ghost (only IH's gizmo shows). Re-enable on the CLONE exactly what
        // the bake disabled on the SOURCE, paired by traversal order (the
        // clone mirrors the source hierarchy; IH strips only non-visual
        // components). Fail-open: any shape mismatch leaves the clone as-is.
        public static void ReenableCloneVisuals(ZNetView sourceView, GameObject clone)
        {
            if (sourceView == null || clone == null) return;
            if (!_zones.TryGetValue(ZoneSystem.GetZone(sourceView.transform.position), out var state)) return;
            var bake = state.Bake;
            if (bake == null) return;
            int disabledR = bake.DisabledRenderers != null ? bake.DisabledRenderers.Count : 0;
            int disabledL = bake.DisabledLodGroups != null ? bake.DisabledLodGroups.Count : 0;
            if (disabledR == 0 && disabledL == 0) return;

            if (disabledR > 0)
            {
                var src = sourceView.GetComponentsInChildren<Renderer>(true);
                var dst = clone.GetComponentsInChildren<Renderer>(true);
                if (src.Length == dst.Length && src.Length > 0)
                {
                    var map = new Dictionary<Renderer, bool>(disabledR);
                    for (int i = 0; i < bake.DisabledRenderers.Count; i++)
                    {
                        var s = bake.DisabledRenderers[i];
                        if (s.Renderer != null) map[s.Renderer] = s.PriorEnabled;
                    }
                    for (int i = 0; i < src.Length; i++)
                        if (src[i] != null && dst[i] != null && map.TryGetValue(src[i], out bool prior))
                            dst[i].enabled = prior;
                }
            }
            if (disabledL > 0)
            {
                var src = sourceView.GetComponentsInChildren<LODGroup>(true);
                var dst = clone.GetComponentsInChildren<LODGroup>(true);
                if (src.Length == dst.Length && src.Length > 0)
                {
                    var map = new Dictionary<LODGroup, bool>(disabledL);
                    for (int i = 0; i < bake.DisabledLodGroups.Count; i++)
                    {
                        var s = bake.DisabledLodGroups[i];
                        if (s.Group != null) map[s.Group] = s.PriorEnabled;
                    }
                    for (int i = 0; i < src.Length; i++)
                        if (src[i] != null && dst[i] != null && map.TryGetValue(src[i], out bool prior))
                            dst[i].enabled = prior;
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
                // A dirty zone that dropped below the bake threshold can never
                // rebake — tear it down (restores the remaining live renderers,
                // drops the removed pieces' ghost geometry) and delete its cache
                // file so next session doesn't resurrect the stale mesh.
                if (state.Baked && state.Dirty && state.Pieces.Count < minPerZone)
                {
                    if (now - state.LastChangeUnscaledTime < settleDelay) continue;
                    TearDown(state);
                    if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
                    {
                        long uidDrop = MeshCacheStore.TryGetWorldUid();
                        if (uidDrop != 0L) MeshCacheStore.Delete(uidDrop, state.Coord);
                    }
                    continue;
                }
                if (state.Pieces.Count < minPerZone) continue;
                if (now - state.LastChangeUnscaledTime < settleDelay) continue;

                // One-shot stale-cache reconciliation for reattached zones: any
                // cached contributor with no live piece was removed while this
                // client was offline (or before removal-detection shipped) and is
                // ghost-rendering out of the stale combined mesh — rebake now.
                if (state.Baked && !state.Dirty && state.NeedsCacheValidation)
                {
                    state.NeedsCacheValidation = false;
                    var ids = state.Bake != null ? state.Bake.ContributorIdentities : null;
                    if (ids != null && ids.Count > 0)
                    {
                        int live = 0;
                        foreach (var piece in state.Pieces)
                        {
                            if (piece == null) continue;
                            if (ids.Contains(MeshBaker.PieceIdentity.From(piece.gameObject, piece.transform.position))) live++;
                        }
                        if (live < ids.Count)
                        {
                            state.Dirty = true;
                            if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                                EasyBakeLog.Info($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): cached mesh has "
                                    + $"{ids.Count - live} contributor(s) with no live piece — rebaking to drop ghost geometry.");
                        }
                    }
                }

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
                // on) — disk I/O issues mustn't break gameplay. A rebake that
                // produced 0 batches must DELETE the old file instead (Save
                // early-outs on empty bakes and would leave the stale mesh to
                // resurrect ghost geometry next session).
                if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
                {
                    long uid = MeshCacheStore.TryGetWorldUid();
                    if (uid != 0L)
                    {
                        if (state.Bake != null && state.Bake.Batches != null && state.Bake.Batches.Count > 0)
                            MeshCacheStore.Save(uid, state.Coord, state.Bake);
                        else
                            MeshCacheStore.Delete(uid, state.Coord);
                    }
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
            _transparentByName.Clear();
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
            // Category C — transparent geometry. Combining transparent pieces
            // into a static batch breaks per-piece depth sorting: each glass
            // pane must sort against the world individually, but a merged mesh
            // sorts once for the whole batch (panes draw through walls / in
            // the wrong order). Gate: any material in the transparent queue
            // (>= 2500), plus FiresGlass windows by name as belt-and-braces
            // against shader/queue changes. Excluded pieces simply stay live.
            if (FiresEasyBakeMeshesPlugin.BatchingExcludeTransparent != null
                && FiresEasyBakeMeshesPlugin.BatchingExcludeTransparent.Value
                && IsTransparentPiece(go)) return true;
            return false;
        }

        // Per-prefab-name verdict cache — the renderer/material walk runs once
        // per prefab kind, not per piece instance per zone scan.
        private static readonly Dictionary<string, bool> _transparentByName
            = new Dictionary<string, bool>(System.StringComparer.Ordinal);

        private static bool IsTransparentPiece(GameObject go)
        {
            string key = go.name;
            int paren = key.IndexOf('(');
            if (paren > 0) key = key.Substring(0, paren).Trim();
            if (_transparentByName.TryGetValue(key, out bool cached)) return cached;

            bool result = key.StartsWith("FiresGlass", System.StringComparison.Ordinal);
            if (!result)
            {
                var renderers = go.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                for (int i = 0; i < renderers.Length && !result; i++)
                {
                    var mats = renderers[i] != null ? renderers[i].sharedMaterials : null;
                    if (mats == null) continue;
                    for (int mi = 0; mi < mats.Length; mi++)
                    {
                        if (mats[mi] != null && mats[mi].renderQueue >= 2500) { result = true; break; }
                    }
                }
            }
            _transparentByName[key] = result;
            return result;
        }

        private static bool HasComponentAnywhere<T>(GameObject go) where T : Component
        {
            if (go.GetComponentInChildren<T>(includeInactive: true) != null) return true;
            if (go.GetComponentInParent<T>(includeInactive: true)   != null) return true;
            return false;
        }
    }
}
