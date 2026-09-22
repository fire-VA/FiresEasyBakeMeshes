using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using FiresCore.Pieces;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    internal static class ZoneTracker
    {
        private class ZoneState
        {
            public Vector2s Coord;
            public HashSet<WearNTear> Pieces = new HashSet<WearNTear>();
            public float LastChangeUnscaledTime;
            public float ConstructedUnscaledTime;
            public bool Baked;
            // True when this zone's bake came from the on-disk cache rather than
            // being built here. It is the difference between a revisit and a
            // first visit, and it is the only thing that decides whether the
            // stand-ins could possibly have been ready before vanilla populated.
            public bool FromCache;
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

            // Pieces this client never created, or unloaded again, because the bake already draws them and a stand-in
            // collider takes their place. Handing one back to vanilla is just Created = false.
            public readonly Dictionary<ZDO, SkippedPiece> Skipped = new Dictionary<ZDO, SkippedPiece>();
            public float NextWatch;
            public GameObject StandInRoot;
            public readonly Dictionary<MeshBaker.PieceIdentity, GameObject> StandIns = new Dictionary<MeshBaker.PieceIdentity, GameObject>();
            public readonly Queue<MeshBaker.PieceIdentity> StandInQueue = new Queue<MeshBaker.PieceIdentity>();
            public readonly Queue<WearNTear> ConvertQueue = new Queue<WearNTear>();
            // While set, the zone keeps real pieces: a build tool is out nearby, or a removal needs a rebake that reads
            // them. Skipped pieces handed back wait in AwaitingRecreate so the stand-ins stay until the real ones exist.
            public bool HoldReal;
            public float HoldRealUntil;
            // The hold covers the whole zone (a rebake or teardown reads its real pieces). A build tool's hold only keeps
            // the pieces within reach real, so the rest of the zone can go on skipping while the tool is out.
            public bool HoldWholeZone;
            public List<ZDO> AwaitingRecreate;
            // Set when only part of the zone was handed back (a build tool nearby): exactly these stand-ins go when the
            // real pieces are back, instead of every stand-in in the zone.
            public List<MeshBaker.PieceIdentity> AwaitingIdentities;
            public float AwaitingSince;
            // The bake changed in place (late joins, hand-backs, removals); the zone's cache file is rewritten once it settles.
            public bool CacheStale;

            public int PresentPieces => Pieces.Count + Skipped.Count;
        }

        // A skipped piece is compared with its bake again only once its ZDO has taken new data since it last matched. A
        // ZDO returned to the pool loses its uid, so a destroyed or reused one never passes as unchanged.
        private struct SkippedPiece
        {
            public MeshBaker.PieceIdentity Identity;
            public ZDOID Uid;
            public uint MatchedRevision;

            public SkippedPiece(ZDO zdo, MeshBaker.PieceIdentity identity)
            {
                Identity = identity;
                Uid = zdo.m_uid;
                MatchedRevision = zdo.DataRevision;
            }

            public bool Unchanged(ZDO zdo) => zdo != null && zdo.m_uid == Uid && zdo.DataRevision == MatchedRevision;
        }

        private static readonly Dictionary<Vector2s, ZoneState> _zones = new Dictionary<Vector2s, ZoneState>();
        private static readonly List<Vector2s> _dropScratch = new List<Vector2s>();

        private const float BuildHoldSeconds = 30f;
        private const float BuildHoldRescanMeters = 2f;
        private const float RecreateTimeoutSeconds = 20f;
        private const float EmptyZoneGraceSeconds = 10f;
        private const float CacheValidationQuietSeconds = 10f;
        private const double StandInBudgetMs = 4.0;
        private const double LoadingStandInBudgetMs = 12.0;
        private const double ConvertBudgetMs = 2.0;
        private const float SkipReportSeconds = 30f;
        private const float WatchSeconds = 1f;
        private const double WatchBudgetMs = 1.0;
        // An unloaded zone keeps its stand-ins switched off while the player stays within this many zones of the loaded
        // area, so walking back in reuses them instead of rebuilding every collider.
        private const int ParkedStandInExtraZones = 8;

        private static bool s_buildingNearby;
        private static bool s_buildHoldScanned;
        private static Vector3 s_buildHoldCenter;
        private static float s_buildHoldRadius;
        private static Vector2s s_buildHoldMinZone;
        private static Vector2s s_buildHoldMaxZone;
        private static float s_nextRangeCheck;
        private static float s_nextSkipReport;
        private static int s_skippedAtCreation;
        private static int s_convertedLive;
        private static int s_deferredForBake;
        private static int s_zonesFromCache, s_zonesBakedFresh;
        private static int s_convertedOnCached, s_convertedOnFresh;
        private static int s_standInColliders;
        private static long s_standInTicks;
        private static int s_standInsOnDemand;
        private static int s_standInZonesReused;
        private static int s_handedBackForBuilding;
        private static int s_handedBackForChanges;
        private static int s_handedBackLooks;
        private static int s_joinedLate;
        private static int s_lastReportedTotal;
        private static bool s_skipFailureLogged;
        private static FieldInfo s_instancesField;
        private static readonly List<ZoneState> _zoneScratch = new List<ZoneState>();
        private static readonly List<ZDO> _areaScratch = new List<ZDO>();
        private static readonly List<ZDO> _handBackScratch = new List<ZDO>();
        private static Vector3 s_sortReference;

        public static void OnInstanceCreated(GameObject go)
        {
            if (go == null) return;
            var wnt = go.GetComponent<WearNTear>();
            if (wnt == null) return;
            bool invulnerable = InvulnerableClassifier.IsInvulnerable(wnt);
            if (!invulnerable && !FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value) return;
            if (HasBakeUnsafeComponent(go)) return;

            var coord = ZoneSystem.GetZone(wnt.transform.position);
            if (!_zones.TryGetValue(coord, out var state))
            {
                state = new ZoneState { Coord = coord, ConstructedUnscaledTime = Time.unscaledTime };
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
                    ConstructCached(state, cachedData);
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
                {
                    if (!StillMatchesBake(state, wnt, identity))
                    {
                        HandBackChanged(state, wnt, identity, invulnerable);
                        return;
                    }
                    MeshBaker.DisableForCacheHit(go, state.Bake);
                    if (invulnerable && SkipCreationActive()) state.ConvertQueue.Enqueue(wnt);
                }
                else
                {
                    TryJoinInstanceGroup(state, wnt, identity, invulnerable);
                }
                return;
            }

            // No cache yet — mark dirty if we'd already baked (admin placed a
            // new piece into a baked zone). Otherwise the first bake will pick
            // it up at settle time.
            if (state.Baked) state.Dirty = true;
        }

        // A piece that arrives after its zone baked joins the zone's instanced group for its prefab, the same way a removed
        // one leaves: no rebake. A busy server delivers a big zone's pieces in waves, so many land after the bake.
        private static void TryJoinInstanceGroup(ZoneState state, WearNTear wnt, MeshBaker.PieceIdentity identity, bool invulnerable)
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return;
            if (!invulnerable && !MeshBaker.ShowsHealthyState(wnt)) return;
            if (MeshBaker.CarriesEditsTheBakeCannotDraw(wnt)) return;
            var look = MeshBaker.LookOf(wnt);
            var group = GroupFor(state, identity.PrefabHash, look) ?? OpenInstanceGroup(state, wnt, identity.PrefabHash, look, invulnerable);
            if (group == null) return;

            group.AddLate(wnt.transform.localToWorldMatrix, identity);
            state.Bake.ContributorIdentities.Add(identity);
            if (invulnerable) state.Bake.PieceTransforms[identity] = MeshBaker.PieceTransform.From(wnt.transform, look);
            MeshBaker.DisableForCacheHit(wnt.gameObject, state.Bake);
            if (invulnerable && SkipCreationActive()) state.ConvertQueue.Enqueue(wnt);
            state.CacheStale = true;
            s_joinedLate++;
        }

        // A prefab the combiner cannot take has no other way into a bake, so the first of its pieces to arrive after the zone
        // baked opens the group the zone never built for it.
        private static ZoneInstanceGroup OpenInstanceGroup(ZoneState state, WearNTear wnt, int prefabHash, WearLook look, bool invulnerable)
        {
            if (!invulnerable || state.Bake?.InstanceGroups == null) return null;
            if (MeshBaker.WhyCombinerRefuses(prefabHash, wnt.gameObject) == null) return null;
            if (!InstanceDefinitionCache.TryGet(new InstanceKey(prefabHash, look), out var definition)) return null;

            var group = new ZoneInstanceGroup { PrefabHash = prefabHash, Look = look, Definition = definition };
            state.Bake.InstanceGroups.Add(group);
            return group;
        }

        private static ZoneInstanceGroup GroupFor(ZoneState state, int prefabHash, WearLook look)
        {
            var groups = state.Bake?.InstanceGroups;
            if (groups == null) return null;
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].PrefabHash == prefabHash && groups[i].Look == look) return groups[i];
            return null;
        }

        // The bake drew this piece with the look it had then and without edits it cannot reproduce; a piece that has
        // changed either since has to draw itself.
        private static bool StillMatchesBake(ZoneState state, WearNTear wnt, MeshBaker.PieceIdentity identity)
        {
            if (MeshBaker.CarriesEditsTheBakeCannotDraw(wnt)) return false;
            return MeshBaker.LookOf(wnt) == BakedLook(state, identity);
        }

        // Invulnerable pieces record their look; a damageable one only ever joins a group while it shows its healthy look.
        private static WearLook BakedLook(ZoneState state, MeshBaker.PieceIdentity identity)
        {
            if (state.Bake.PieceTransforms.TryGetValue(identity, out var baked)) return baked.Look;
            return HealthyLookOf(identity.PrefabHash);
        }

        private static WearLook HealthyLookOf(int prefabHash)
        {
            var scene = ZNetScene.instance;
            return WearLooks.Healthy(scene != null ? scene.GetPrefab(prefabHash) : null);
        }

        // An instanced piece leaves its group, and joins the one for its new look if the zone has one; merged geometry
        // cannot drop a single piece, so its zone rebakes.
        private static void HandBackChanged(ZoneState state, WearNTear wnt, MeshBaker.PieceIdentity identity, bool invulnerable)
        {
            if (HandBack(state, wnt, identity))
            {
                TryJoinInstanceGroup(state, wnt, identity, invulnerable);
                return;
            }
            state.Dirty = true;
            state.LastChangeUnscaledTime = Time.unscaledTime;
        }

        private static void ConstructCached(ZoneState state, MeshCacheStore.CachedZoneData cachedData)
        {
            var swCache = Stopwatch.StartNew();
            state.Bake = MeshCacheStore.ConstructFromCache(cachedData);
            swCache.Stop();
            if (state.Bake == null) return;

            state.Baked = true;
            state.FromCache = true;
            s_zonesFromCache++;
            state.NeedsCacheValidation = true;
            // Only pin via keepalive if the cached bake actually
            // contains combined mesh batches. Empty bakes (no
            // batches survived the bake-time filtering) provide
            // no rendering win — pinning them just wastes memory
            // and per-tick FindSectorObjects work.
            if (state.Bake.HasRenderableContent)
                ZoneKeepalive.MarkActive(state.Coord);
            BakeSummary.RecordCacheConstruct(
                state.Bake.Batches?.Count ?? 0, swCache.ElapsedMilliseconds);
            if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                EasyBakeLog.Info(
                    $"[Cache] Zone ({state.Coord.x},{state.Coord.y}) constructed from preload: " +
                    $"{state.Bake.Batches.Count} batches, {state.Bake.ContributorIdentities?.Count ?? 0} cached identities.");
            QueueStandIns(state);
        }

        // A piece drawn as an instance stops matching it when it is worn, broken, burning or highlighted: its matrix leaves
        // the group and its own renderers draw again. Pieces merged into combined meshes would need a rebake and keep
        // their baked look.
        internal static void HandBackPiece(WearNTear wnt)
        {
            if (wnt == null || _zones.Count == 0) return;
            Vector3 position = wnt.transform.position;
            if (!_zones.TryGetValue(ZoneSystem.GetZone(position), out var state) || !state.Baked || state.Bake?.ContributorIdentities == null) return;
            var identity = MeshBaker.PieceIdentity.From(wnt.gameObject, position);
            if (state.Bake.ContributorIdentities.Contains(identity)) HandBack(state, wnt, identity);
        }

        // Health changed on a piece that already exists. SetHealthVisual's first call, from WearNTear.Awake, is left to
        // OnInstanceCreated, which compares the look against the bake once the piece is registered.
        internal static void OnLookMayHaveChanged(WearNTear wnt)
        {
            if (wnt == null || _zones.Count == 0) return;
            Vector3 position = wnt.transform.position;
            if (!_zones.TryGetValue(ZoneSystem.GetZone(position), out var state) || !state.Baked || state.Bake?.ContributorIdentities == null) return;
            var identity = MeshBaker.PieceIdentity.From(wnt.gameObject, position);
            if (!state.Bake.ContributorIdentities.Contains(identity) || StillMatchesBake(state, wnt, identity)) return;
            if (HandBack(state, wnt, identity)) TryJoinInstanceGroup(state, wnt, identity, InvulnerableClassifier.IsInvulnerable(wnt));
        }

        private static bool HandBack(ZoneState state, WearNTear wnt, MeshBaker.PieceIdentity identity)
        {
            var groups = state.Bake.InstanceGroups;
            bool removed = false;
            for (int i = 0; i < groups.Count && !removed; i++) removed = groups[i].TryRemove(identity);
            if (!removed) return false;

            state.Bake.ContributorIdentities.Remove(identity);
            state.Bake.PieceTransforms?.Remove(identity);
            DropStandIn(state, identity);
            var suppression = wnt.GetComponent<EasyBakeSuppressedVisuals>();
            if (suppression != null)
            {
                suppression.RestoreVisuals();
                state.Bake.SuppressedPieces.Remove(suppression);
                Object.Destroy(suppression);
            }
            state.CacheStale = true;
            s_handedBackLooks++;
            return true;
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
            state.Skipped.Remove(zdo);
            if (!state.Baked || state.Bake == null) return;
            var ids = state.Bake.ContributorIdentities;
            if (ids == null || ids.Count == 0) return;

            var identity = MeshBaker.PieceIdentity.From(zdo);
            DropStandIn(state, identity);
            state.Bake.PieceTransforms?.Remove(identity);
            if (!ids.Remove(identity)) return;   // not part of the combined mesh

            // Instanced pieces drop straight out of their group. No geometry was merged
            // for them, so there is nothing to rebuild — this is the invalidation win
            // instancing exists for.
            var instanceGroups = state.Bake.InstanceGroups;
            for (int i = 0; i < instanceGroups.Count; i++)
            {
                if (!instanceGroups[i].TryRemove(identity)) continue;
                state.CacheStale = true;
                if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                    EasyBakeLog.Info($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): instanced piece removed — dropped without rebake.");
                return;
            }

            state.Dirty = true;
            state.LastChangeUnscaledTime = Time.unscaledTime;
            // The rebake reads real pieces, so anything the zone skipped has to be created first.
            if (state.Skipped.Count > 0)
            {
                s_handedBackForChanges += state.Skipped.Count;
                HoldRealPieces(state, 0f);
            }
            if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                EasyBakeLog.Info($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): batched piece removed for real — rebake queued.");
        }


        // Width of the dead band around the switch distance. Crossing out to far tier
        // happens at the configured distance; crossing back to near happens this much
        // closer, so a player loitering exactly on the boundary can't flip the zone
        // every frame.
        private const float FarTierHysteresisMeters = 4f;

        // Swaps a baked zone between its LOD0 and LOD1 meshes on real distance rather
        // than LODGroup screen height, so the switch distance is exactly the configured
        // metres regardless of the viewer's FOV or quality-settings LOD bias.
        private static void UpdateFarTier(Vector2s coord, ZoneState state)
        {
            if (state.Bake == null) return;
            if (!FiresEasyBakeMeshesPlugin.BatchingFarTierEnabled.Value)
            {
                MeshBaker.SetFarTierActive(state.Bake, false);
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null) return;

            float switchDistance = FiresEasyBakeMeshesPlugin.BatchingFarTierDistance.Value;
            float enterFar = switchDistance;
            float leaveFar = Mathf.Max(0f, switchDistance - FarTierHysteresisMeters);
            float threshold = state.Bake.ShowingFarTier ? leaveFar : enterFar;

            var zoneCentre = ZoneSystem.GetZonePos(coord);
            var viewer = player.transform.position;
            float dx = viewer.x - zoneCentre.x;
            float dz = viewer.z - zoneCentre.z;
            float sqrDistance = dx * dx + dz * dz;

            MeshBaker.SetFarTierActive(state.Bake, sqrDistance > threshold * threshold);
        }

        // Instanced groups are immediate-mode draws, so they must be re-issued every
        // frame. Only zones that are loaded and baked draw, which reuses the same
        // lifecycle gate the combined meshes already follow, and each group carries its
        // own worldBounds so Unity culls a whole zone's instances in one test. Groups
        // InstancedDraw finds out of view never reach Unity at all.
        public static void DrawInstances()
        {
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return;

            InstancedDraw.BeginFrame();
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked) continue;
                var bake = state.Bake;
                if (bake == null || bake.InstanceGroups.Count == 0) continue;

                InstancedDraw.NoteZone();
                for (int i = 0; i < bake.InstanceGroups.Count; i++)
                {
                    var group = bake.InstanceGroups[i];
                    if (InstancedDraw.Submit(group)) group.Draw(bake.ShowingFarTier);
                }
            }
            InstancedDraw.EndFrame();
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            float settleDelay = FiresEasyBakeMeshesPlugin.BatchingSettleDelaySeconds.Value;
            int minPerZone = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerZone.Value;

            var zoneSystem = ZoneSystem.instance;
            _dropScratch.Clear();

            bool skipping = SkipCreationActive();
            var localPlayer = Player.m_localPlayer;
            s_buildingNearby = skipping && localPlayer != null && localPlayer.InPlaceMode();
            long holdStart = InstancedDraw.Mark();
            if (s_buildingNearby) HoldForBuildTools(localPlayer.transform.position, now);
            else s_buildHoldScanned = false;
            InstancedDraw.NotePhase(InstancedDraw.Phase.BuildHold, holdStart);

            long walkStart = InstancedDraw.Mark();

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
                    if (state.CacheStale && state.Baked && !state.Dirty) SaveZone(state);
                    state.Pieces.Clear();
                    state.Bake?.SuppressedPieces?.Clear();
                    ReleaseSkipped(state, parkStandIns: true);
                    state.ZoneActive = false;
                }
                else if (zoneLoaded && !state.ZoneActive)
                {
                    if (state.Baked && state.Bake != null && state.Bake.Parent != null)
                        state.Bake.Parent.SetActive(true);
                    UnparkStandIns(state);
                    state.ZoneActive = true;
                    state.LastChangeUnscaledTime = now;
                    state.ConstructedUnscaledTime = now;
                    if (state.Baked) QueueStandIns(state);
                }

                if (state.ZoneActive && state.Baked) UpdateFarTier(coord, state);
            }
            InstancedDraw.NotePhase(InstancedDraw.Phase.Zones, walkStart);

            long checkStart = InstancedDraw.Mark();
            FinishHandBacks(now);
            if (skipping)
            {
                if (now >= s_nextRangeCheck)
                {
                    s_nextRangeCheck = now + 0.25f;
                    ConstructCachedZonesInRange(zoneSystem);
                }
                InstancedDraw.NotePhase(InstancedDraw.Phase.Checks, checkStart);

                long standInStart = InstancedDraw.Mark();
                BuildStandIns();
                InstancedDraw.NotePhase(InstancedDraw.Phase.StandIns, standInStart);

                long unloadStart = InstancedDraw.Mark();
                ConvertLivePieces();
                InstancedDraw.NotePhase(InstancedDraw.Phase.Unloading, unloadStart);

                checkStart = InstancedDraw.Mark();
                WatchSkipped(now);
                ReportSkipping(now);
            }
            InstancedDraw.NotePhase(InstancedDraw.Phase.Checks, checkStart);

            long bakeStart = InstancedDraw.Mark();

            // Now the per-frame bake decision. Only active zones with enough
            // settled invulnerable pieces qualify. A zone that's Baked AND not
            // Dirty is in steady state — skip; the combined mesh keeps drawing.
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive) continue;
                // Skipped pieces handed back to vanilla are still being created; a bake now would miss them.
                if (state.AwaitingRecreate != null) continue;
                // A dirty zone that dropped below the bake threshold can never
                // rebake — tear it down (restores the remaining live renderers,
                // drops the removed pieces' ghost geometry) and delete its cache
                // file so next session doesn't resurrect the stale mesh.
                if (state.Baked && state.Dirty && state.PresentPieces < minPerZone)
                {
                    if (now - state.LastChangeUnscaledTime < settleDelay) continue;
                    if (state.Skipped.Count > 0) { HoldRealPieces(state, 0f); continue; }
                    TearDown(state);
                    if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
                    {
                        long uidDrop = MeshCacheStore.TryGetWorldUid();
                        if (uidDrop != 0L) MeshCacheStore.Delete(uidDrop, state.Coord);
                    }
                    continue;
                }
                if (state.PresentPieces < minPerZone) continue;
                if (now - state.LastChangeUnscaledTime < settleDelay) continue;

                // One-shot stale-cache reconciliation for reattached zones: any
                // cached contributor with no live piece was removed while this
                // client was offline (or before removal-detection shipped) and is
                // ghost-rendering out of the stale combined mesh — rebake now.
                // A skipped piece counts as present: its ZDO matched the identity when it was skipped.
                if (state.Baked && !state.Dirty && state.NeedsCacheValidation)
                {
                    // A busy server can deliver a zone's pieces in waves; judging too early rebakes half a base.
                    if (now - state.LastChangeUnscaledTime < CacheValidationQuietSeconds) continue;
                    state.NeedsCacheValidation = false;
                    var ids = state.Bake != null ? state.Bake.ContributorIdentities : null;
                    if (ids != null && ids.Count > 0)
                    {
                        int live = state.Skipped.Count;
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

                if (state.Baked && !state.Dirty)
                {
                    if (state.CacheStale) SaveZone(state);
                    continue;
                }
                if (state.Skipped.Count > 0) { HoldRealPieces(state, 0f); continue; }

                if (state.Baked && state.Dirty) TearDown(state);
                long tBake = Probe.Start();
                state.Bake = MeshBaker.Bake(state.Coord, state.Pieces);
                Probe.Stop("EasyBake:bake", tBake);
                state.Baked = true;
                state.FromCache = false;
                s_zonesBakedFresh++;
                state.Dirty = false;
                // Only pin via keepalive if the fresh bake produced batches.
                // Zones with sub-threshold materials or all-PropBlock /
                // all-non-LOD0 renderers bake to 0 batches and don't benefit
                // from being kept alive across zone-cross.
                if (state.Bake != null && state.Bake.HasRenderableContent)
                    ZoneKeepalive.MarkActive(state.Coord);
                AfterFreshBake(state, now);

                // Persist the freshly-baked result so the next session can
                // skip CombineMeshes entirely. Save is fail-soft (logs + moves
                // on) — disk I/O issues mustn't break gameplay. A rebake that
                // produced 0 batches must DELETE the old file instead (Save
                // early-outs on empty bakes and would leave the stale mesh to
                // resurrect ghost geometry next session).
                SaveZone(state);
            }

            InstancedDraw.NotePhase(InstancedDraw.Phase.Baking, bakeStart);

            // Anything that fully collapses (an unloaded zone we never saw return,
            // or a zone where all pieces were genuinely destroyed while loaded) is
            // dropped. The latter case — zoneLoaded && Pieces.Count == 0 — only
            // happens if an admin demolishes every invulnerable piece in a loaded
            // zone, which we treat as cache invalidation. A zone built straight from
            // the cache gets a grace period for its pieces to arrive first.
            foreach (var kv in _zones)
            {
                var state = kv.Value;
                if (state.ZoneActive && state.Baked && state.PresentPieces == 0 && state.AwaitingRecreate == null
                    && now - state.ConstructedUnscaledTime >= EmptyZoneGraceSeconds)
                {
                    TearDown(state);
                    _dropScratch.Add(kv.Key);
                }
            }
            for (int i = 0; i < _dropScratch.Count; i++) _zones.Remove(_dropScratch[i]);
        }

        private static void SaveZone(ZoneState state)
        {
            state.CacheStale = false;
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value) return;
            long uid = MeshCacheStore.TryGetWorldUid();
            if (uid == 0L) return;
            if (state.Bake != null && state.Bake.HasRenderableContent)
                MeshCacheStore.Save(uid, state.Coord, state.Bake);
            else
                MeshCacheStore.Delete(uid, state.Coord);
        }

        private static void TearDown(ZoneState state)
        {
            ReleaseSkipped(state);
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
            s_buildingNearby = false;
            s_buildHoldScanned = false;
            s_skippedAtCreation = 0;
            s_convertedLive = 0;
            s_deferredForBake = 0;
            s_zonesFromCache = 0; s_zonesBakedFresh = 0;
            s_convertedOnCached = 0; s_convertedOnFresh = 0;
            s_standInColliders = 0;
            s_standInTicks = 0;
            s_standInsOnDemand = 0;
            s_standInZonesReused = 0;
            s_handedBackForBuilding = 0;
            s_handedBackForChanges = 0;
            s_handedBackLooks = 0;
            s_joinedLate = 0;
            s_lastReportedTotal = 0;
            InstancedDraw.Reset();
            PieceData.Reset();
        }

        // --- Skipping piece creation -------------------------------------------

        // Only a multiplayer client may skip: on a server, host or single-player world vanilla treats a CreateObject
        // that returns null as an invalid prefab and destroys the ZDO.
        internal static bool SkipCreationActive()
        {
            return FiresEasyBakeMeshesPlugin.SkipBakedPieceObjects != null
                && FiresEasyBakeMeshesPlugin.SkipBakedPieceObjects.Value
                && FiresEasyBakeMeshesPlugin.BatchingActive()
                && ZNet.instance != null
                && !ZNet.instance.IsServer();
        }

        // ZNetScene.CreateObject prefix. True means the piece stays uncreated: its cached bake draws it and its
        // stand-in collider already exists.
        public static bool TrySkipCreation(ZDO zdo)
        {
            if (zdo == null || !SkipCreationActive()) return false;
            int prefabHash = zdo.GetPrefab();
            var info = SkipEligibility.Get(prefabHash);
            if (!info.Skippable) return false;

            Vector3 position = zdo.GetPosition();
            if (s_buildingNearby && InBuildHold(position)) return false;
            var zoneCoord = ZoneSystem.GetZone(position);
            var state = EnsureZoneFromCache(zoneCoord);
            if (state == null || !state.Baked || state.Bake == null)
            {
                // The bake has not landed yet, so this piece cannot be skipped —
                // but it is a piece that WOULD be skipped, and creating it now
                // only means destroying it again when the bake arrives. Hold it
                // for a moment instead. See DeferCreation for why this is client
                // only and why it expires.
                if (DeferCreation.ShouldDefer(zdo, zoneCoord, bakePending: state != null))
                {
                    s_deferredForBake++;
                    return true;
                }
                return false;
            }
            DeferCreation.OnZoneBaked(zoneCoord);
            // A handed-back piece skipped again before it exists would have its stand-in dropped when the hand-back ends.
            if (state.AwaitingRecreate != null) return false;
            if (state.HoldReal && (state.HoldWholeZone || !s_buildingNearby)) return false;

            var identity = MeshBaker.PieceIdentity.From(zdo);
            if (!state.Bake.PieceTransforms.TryGetValue(identity, out var cached)) return false;
            if (!SkipEligibility.ZdoMatches(zdo, info, cached))
            {
                return false;
            }
            PieceData.Sample(zdo, prefabHash);
            EnsureStandIn(state, identity, cached);

            zdo.Created = true;
            state.Skipped[zdo] = new SkippedPiece(zdo, identity);
            state.LastChangeUnscaledTime = Time.unscaledTime;
            s_skippedAtCreation++;
            return true;
        }

        internal static void LogSkipFailureOnce(System.Exception ex)
        {
            if (s_skipFailureLogged) return;
            s_skipFailureLogged = true;
            EasyBakeLog.Warn($"[Skip] Skipping piece creation failed and is off for that piece: {ex.GetType().Name}: {ex.Message}");
        }

        // ZNetScene.IsAreaReady waits for an instance of every object around a point, which a skipped piece never gets.
        public static bool IsAreaReadyCountingSkipped(Vector3 point)
        {
            if (!AnySkipped()) return false;
            var zoneSystem = ZoneSystem.instance;
            var scene = ZNetScene.instance;
            var zdoMan = ZDOMan.instance;
            if (zoneSystem == null || scene == null || zdoMan == null) return false;

            var zone = ZoneSystem.GetZone(point);
            if (!zoneSystem.IsZoneLoaded(zone)) return false;
            _areaScratch.Clear();
            zdoMan.FindSectorObjects(zone, new SimulationDistance(1, 0), _areaScratch);
            for (int i = 0; i < _areaScratch.Count; i++)
            {
                var zdo = _areaScratch[i];
                int prefab = zdo.GetPrefab();
                if (prefab == 0 || scene.GetPrefab(prefab) == null) continue;
                if (scene.FindInstance(zdo) != null || IsSkipped(zdo)) continue;
                return false;
            }
            return true;
        }

        private static bool AnySkipped()
        {
            foreach (var state in _zones.Values)
                if (state.Skipped.Count > 0) return true;
            return false;
        }

        private static bool IsSkipped(ZDO zdo)
        {
            return _zones.TryGetValue(ZoneSystem.GetZone(zdo.GetPosition()), out var state) && state.Skipped.ContainsKey(zdo);
        }

        // The config was switched off: every skipped piece goes back to vanilla.
        public static void HandBackAllSkipped()
        {
            foreach (var state in _zones.Values)
            {
                HoldRealPieces(state, 0f);
                state.HoldReal = false;
                state.HoldWholeZone = false;
                state.StandInQueue.Clear();
                state.ConvertQueue.Clear();
            }
        }

        private static ZoneState EnsureZoneFromCache(Vector2s coord)
        {
            if (_zones.TryGetValue(coord, out var state)) return state;
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value) return null;
            if (!MeshCacheStore.TryGetPreloaded(coord, out var cachedData)) return null;

            float now = Time.unscaledTime;
            state = new ZoneState { Coord = coord, LastChangeUnscaledTime = now, ConstructedUnscaledTime = now };
            _zones.Add(coord, state);
            ConstructCached(state, cachedData);
            return state;
        }

        // Builds cached zones as soon as their terrain loads, so stand-ins are ready before vanilla gets to the pieces.
        private static void ConstructCachedZonesInRange(ZoneSystem zoneSystem)
        {
            var net = ZNet.instance;
            if (zoneSystem == null || net == null) return;
            var center = ZoneSystem.GetZone(net.GetReferencePosition());
            int radius = net.GetSyncedSimulationDistance().NearSimulationDistance;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    var coord = new Vector2s(center.x + dx, center.y + dy);
                    if (_zones.ContainsKey(coord) || !zoneSystem.IsZoneLoaded(coord)) continue;
                    if (!MeshCacheStore.TryGetPreloaded(coord, out _)) continue;
                    EnsureZoneFromCache(coord);
                }
            }
            ReleaseDistantParkedStandIns(center, radius + ParkedStandInExtraZones);
        }

        private static void ReleaseDistantParkedStandIns(Vector2s center, int keepRadius)
        {
            foreach (var state in _zones.Values)
            {
                if (state.ZoneActive || state.StandInRoot == null) continue;
                if (Mathf.Abs(state.Coord.x - center.x) <= keepRadius && Mathf.Abs(state.Coord.y - center.y) <= keepRadius) continue;
                DestroyStandIns(state);
            }
        }

        private static void UnparkStandIns(ZoneState state)
        {
            if (state.StandInRoot == null || state.StandInRoot.activeSelf) return;
            // A zone held for build tools gets its real pieces back, which must not share their place with stand-ins.
            if (state.HoldReal)
            {
                DestroyStandIns(state);
                return;
            }
            state.StandInRoot.SetActive(true);
            s_standInZonesReused++;
        }

        // A piece that arrives before the queue reached it gets its stand-in right away; building a few colliders costs
        // far less than creating the whole piece and unloading it again.
        private static void EnsureStandIn(ZoneState state, MeshBaker.PieceIdentity identity, MeshBaker.PieceTransform transform)
        {
            UnparkStandIns(state);
            if (state.StandIns.ContainsKey(identity)) return;
            long started = Stopwatch.GetTimestamp();
            if (state.StandInRoot == null)
                state.StandInRoot = new GameObject($"[EasyBake/Zone_{state.Coord.x}_{state.Coord.y}/standins]");
            state.StandIns[identity] = StandInColliders.Build(state.StandInRoot.transform, identity, transform, out int colliders);
            s_standInColliders += colliders;
            s_standInsOnDemand++;
            s_standInTicks += Stopwatch.GetTimestamp() - started;
        }

        private static void QueueStandIns(ZoneState state)
        {
            state.StandInQueue.Clear();
            if (!SkipCreationActive() || state.Bake?.PieceTransforms == null) return;
            foreach (var identity in state.Bake.PieceTransforms.Keys)
            {
                if (state.StandIns.ContainsKey(identity)) continue;
                if (!SkipEligibility.IsPrefabSkippable(identity.PrefabHash)) continue;
                state.StandInQueue.Enqueue(identity);
            }
        }

        // A fresh bake read the zone's real pieces; stand-ins for the new identities are built and the pieces are then
        // unloaded again as their stand-ins come up.
        private static void AfterFreshBake(ZoneState state, float now)
        {
            if (!SkipCreationActive() || state.Bake == null) return;
            state.HoldWholeZone = false;
            if (state.HoldRealUntil <= now) state.HoldReal = false;
            DestroyStandIns(state);
            QueueStandIns(state);
            state.ConvertQueue.Clear();
            foreach (var piece in state.Pieces)
            {
                if (piece == null || !InvulnerableClassifier.IsInvulnerable(piece)) continue;
                if (state.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(piece.gameObject, piece.transform.position)))
                    state.ConvertQueue.Enqueue(piece);
            }
        }

        private static void BuildStandIns()
        {
            _zoneScratch.Clear();
            foreach (var state in _zones.Values)
                if (state.ZoneActive && state.Baked && state.Bake != null && !state.HoldReal && state.StandInQueue.Count > 0)
                    _zoneScratch.Add(state);
            if (_zoneScratch.Count == 0) return;

            s_sortReference = ZNet.instance != null ? ZNet.instance.GetReferencePosition() : Vector3.zero;
            _zoneScratch.Sort(CompareByDistance);

            var player = Player.m_localPlayer;
            double budget = player == null || player.IsTeleporting() ? LoadingStandInBudgetMs : StandInBudgetMs;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                for (int z = 0; z < _zoneScratch.Count; z++)
                {
                    var state = _zoneScratch[z];
                    if (state.StandInRoot == null)
                        state.StandInRoot = new GameObject($"[EasyBake/Zone_{state.Coord.x}_{state.Coord.y}/standins]");
                    while (state.StandInQueue.Count > 0)
                    {
                        if (stopwatch.Elapsed.TotalMilliseconds >= budget) return;
                        var identity = state.StandInQueue.Dequeue();
                        if (state.StandIns.ContainsKey(identity)) continue;
                        if (!state.Bake.PieceTransforms.TryGetValue(identity, out var transform)) continue;
                        state.StandIns[identity] = StandInColliders.Build(state.StandInRoot.transform, identity, transform, out int colliders);
                        s_standInColliders += colliders;
                    }
                }
            }
            finally
            {
                s_standInTicks += stopwatch.ElapsedTicks;
            }
        }

        private static readonly System.Comparison<ZoneState> CompareByDistance = (a, b) =>
            ZoneDistanceSq(a.Coord).CompareTo(ZoneDistanceSq(b.Coord));

        private static float ZoneDistanceSq(Vector2s coord)
        {
            var centre = ZoneSystem.GetZonePos(coord);
            float dx = centre.x - s_sortReference.x;
            float dz = centre.z - s_sortReference.z;
            return dx * dx + dz * dz;
        }

        // Pieces vanilla created before their stand-in existed are unloaded again once it does, the same way
        // ZNetScene.RemoveObjects unloads an object, except that the ZDO stays marked as created.
        private static void ConvertLivePieces()
        {
            var instances = SceneInstances();
            if (instances == null) return;
            var stopwatch = Stopwatch.StartNew();
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked || state.Bake == null || state.HoldReal || state.ConvertQueue.Count == 0) continue;
                int passes = state.ConvertQueue.Count;
                while (passes-- > 0)
                {
                    if (stopwatch.Elapsed.TotalMilliseconds >= ConvertBudgetMs) return;
                    var wnt = state.ConvertQueue.Dequeue();
                    if (wnt == null) continue;
                    var view = wnt.GetComponent<ZNetView>();
                    var zdo = view != null ? view.GetZDO() : null;
                    if (zdo == null) continue;

                    int prefabHash = zdo.GetPrefab();
                    var info = SkipEligibility.Get(prefabHash);
                    if (!info.Skippable) continue;
                    var identity = MeshBaker.PieceIdentity.From(zdo);
                    if (!state.Bake.PieceTransforms.TryGetValue(identity, out var cached)) continue;
                    if (!SkipEligibility.ZdoMatches(zdo, info, cached)) continue;
                    EnsureStandIn(state, identity, cached);

                    state.Pieces.Remove(wnt);
                    instances.Remove(zdo);
                    SectorInstanceMirror.OnInstanceRemoved(zdo);
                    view.ResetZDO();
                    Object.Destroy(wnt.gameObject);
                    zdo.Created = true;
                    state.Skipped[zdo] = new SkippedPiece(zdo, identity);
                    s_convertedLive++;
                    if (state.FromCache) s_convertedOnCached++; else s_convertedOnFresh++;
                }
            }
        }

        private static Dictionary<ZDO, ZNetView> SceneInstances()
        {
            var scene = ZNetScene.instance;
            if (scene == null) return null;
            if (s_instancesField == null)
                s_instancesField = typeof(ZNetScene).GetField("m_instances", BindingFlags.NonPublic | BindingFlags.Instance);
            return s_instancesField?.GetValue(scene) as Dictionary<ZDO, ZNetView>;
        }

        // A skipped piece that moved, turned, lost its invulnerability or changed scale no longer matches the bake. An
        // instanced one just leaves its group and is created again; merged geometry needs the zone's real pieces back
        // for a rebake. Each zone is looked at about once a second, spread over frames within a small budget.
        private static void WatchSkipped(float now)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return;
            _watchScratch.Clear();
            foreach (var state in _zones.Values)
                if (state.Skipped.Count > 0 && state.Bake?.PieceTransforms != null && now >= state.NextWatch)
                    _watchScratch.Add(state);
            if (_watchScratch.Count == 0) return;
            _watchScratch.Sort(CompareByNextWatch);

            var stopwatch = Stopwatch.StartNew();
            for (int z = 0; z < _watchScratch.Count; z++)
            {
                if (z > 0 && stopwatch.Elapsed.TotalMilliseconds >= WatchBudgetMs) return;
                var state = _watchScratch[z];
                state.NextWatch = now + WatchSeconds;
                WatchZone(state, zdoMan);
            }
        }

        private static void WatchZone(ZoneState state, ZDOMan zdoMan)
        {
            _changedScratch.Clear();
            _rematchedScratch.Clear();
            foreach (var entry in state.Skipped)
            {
                var zdo = entry.Key;
                var skipped = entry.Value;
                if (skipped.Unchanged(zdo)) continue;
                if (zdo != null && zdo.m_uid == skipped.Uid && zdoMan.GetZDO(zdo.m_uid) == zdo
                    && MeshBaker.PieceIdentity.From(zdo).Equals(skipped.Identity)
                    && state.Bake.PieceTransforms.TryGetValue(skipped.Identity, out var cached)
                    && SkipEligibility.ZdoMatches(zdo, SkipEligibility.Get(skipped.Identity.PrefabHash), cached))
                {
                    _rematchedScratch.Add(new KeyValuePair<ZDO, SkippedPiece>(zdo, new SkippedPiece(zdo, skipped.Identity)));
                    continue;
                }
                _changedScratch.Add(new KeyValuePair<ZDO, MeshBaker.PieceIdentity>(zdo, skipped.Identity));
            }
            for (int i = 0; i < _rematchedScratch.Count; i++) state.Skipped[_rematchedScratch[i].Key] = _rematchedScratch[i].Value;
            if (_changedScratch.Count == 0) return;

            bool needsRebake = false;
            for (int i = 0; i < _changedScratch.Count && !needsRebake; i++)
                needsRebake = !ReleaseChangedInstance(state, _changedScratch[i].Key, _changedScratch[i].Value);
            if (!needsRebake) return;

            state.Dirty = true;
            state.LastChangeUnscaledTime = Time.unscaledTime;
            s_handedBackForChanges += state.Skipped.Count;
            HoldRealPieces(state, 0f);
        }

        private static readonly List<ZoneState> _watchScratch = new List<ZoneState>();
        private static readonly List<KeyValuePair<ZDO, MeshBaker.PieceIdentity>> _changedScratch = new List<KeyValuePair<ZDO, MeshBaker.PieceIdentity>>();
        private static readonly List<KeyValuePair<ZDO, SkippedPiece>> _rematchedScratch = new List<KeyValuePair<ZDO, SkippedPiece>>();
        private static readonly System.Comparison<ZoneState> CompareByNextWatch = (a, b) => a.NextWatch.CompareTo(b.NextWatch);

        private static bool ReleaseChangedInstance(ZoneState state, ZDO zdo, MeshBaker.PieceIdentity identity)
        {
            var groups = state.Bake.InstanceGroups;
            bool removed = false;
            for (int i = 0; i < groups.Count && !removed; i++) removed = groups[i].TryRemove(identity);
            if (!removed) return false;

            state.Bake.ContributorIdentities?.Remove(identity);
            state.Bake.PieceTransforms.Remove(identity);
            DropStandIn(state, identity);
            state.Skipped.Remove(zdo);
            if (zdo != null) zdo.Created = false;
            state.CacheStale = true;
            s_handedBackForChanges++;
            return true;
        }

        // A build tool only ever snaps to, hits or removes what is close by, so only the pieces within the build radius
        // come back. In a dense base the surrounding zones hold tens of thousands of pieces, and recreating all of them
        // every time the hammer comes out is the most expensive thing skipping does. A scan hands back everything within
        // the radius plus BuildHoldRescanMeters of where it ran, and TrySkipCreation skips nothing inside that sphere, so
        // until the player has moved that far every piece in reach is already real and a frame only keeps the holds up.
        private static void HoldForBuildTools(Vector3 position, float now)
        {
            float radius = FiresEasyBakeMeshesPlugin.BatchingBuildRadius.Value + BuildHoldRescanMeters;
            bool moved = (position - s_buildHoldCenter).sqrMagnitude >= BuildHoldRescanMeters * BuildHoldRescanMeters;
            if (s_buildHoldScanned && !moved && Mathf.Approximately(radius, s_buildHoldRadius) && KeepBuildHolds(now)) return;

            s_buildHoldScanned = true;
            s_buildHoldCenter = position;
            s_buildHoldRadius = radius;
            var extent = new Vector3(radius, 0f, radius);
            s_buildHoldMinZone = ZoneSystem.GetZone(position - extent);
            s_buildHoldMaxZone = ZoneSystem.GetZone(position + extent);
            float radiusSqr = radius * radius;
            for (int x = s_buildHoldMinZone.x; x <= s_buildHoldMaxZone.x; x++)
            {
                for (int y = s_buildHoldMinZone.y; y <= s_buildHoldMaxZone.y; y++)
                {
                    if (!_zones.TryGetValue(new Vector2s(x, y), out var state)) continue;
                    HoldRealPiecesNear(state, position, radiusSqr, now + BuildHoldSeconds);
                }
            }
        }

        // False when a zone the last scan covered is not held (built since, or its hold was released), which needs a scan.
        private static bool KeepBuildHolds(float now)
        {
            float holdUntil = now + BuildHoldSeconds;
            for (int x = s_buildHoldMinZone.x; x <= s_buildHoldMaxZone.x; x++)
            {
                for (int y = s_buildHoldMinZone.y; y <= s_buildHoldMaxZone.y; y++)
                {
                    if (!_zones.TryGetValue(new Vector2s(x, y), out var state)) continue;
                    if (!state.HoldReal) return false;
                    state.HoldRealUntil = Mathf.Max(state.HoldRealUntil, holdUntil);
                    if (state.Skipped.Count == 0 && state.AwaitingRecreate == null) DestroyStandIns(state);
                }
            }
            return true;
        }

        private static bool InBuildHold(Vector3 position)
            => !s_buildHoldScanned || (position - s_buildHoldCenter).sqrMagnitude <= s_buildHoldRadius * s_buildHoldRadius;

        private static void HoldRealPiecesNear(ZoneState state, Vector3 position, float radiusSqr, float holdUntil)
        {
            state.HoldReal = true;
            state.HoldRealUntil = Mathf.Max(state.HoldRealUntil, holdUntil);

            _handBackScratch.Clear();
            foreach (var entry in state.Skipped)
            {
                if (entry.Key == null) continue;
                if ((entry.Key.GetPosition() - position).sqrMagnitude > radiusSqr) continue;
                _handBackScratch.Add(entry.Key);
            }

            if (_handBackScratch.Count == 0)
            {
                if (state.Skipped.Count == 0 && state.AwaitingRecreate == null) DestroyStandIns(state);
                return;
            }

            bool wholeZoneInFlight = state.AwaitingRecreate != null && state.AwaitingIdentities == null;
            if (state.AwaitingRecreate == null)
            {
                state.AwaitingRecreate = new List<ZDO>(_handBackScratch.Count);
                state.AwaitingIdentities = new List<MeshBaker.PieceIdentity>(_handBackScratch.Count);
                state.AwaitingSince = Time.unscaledTime;
            }

            for (int i = 0; i < _handBackScratch.Count; i++)
            {
                var zdo = _handBackScratch[i];
                var identity = state.Skipped[zdo].Identity;
                zdo.Created = false;
                state.AwaitingRecreate.Add(zdo);
                if (!wholeZoneInFlight) state.AwaitingIdentities.Add(identity);
                state.Skipped.Remove(zdo);
            }
            s_handedBackForBuilding += _handBackScratch.Count;
            state.LastChangeUnscaledTime = Time.unscaledTime;
        }

        private static void HoldRealPieces(ZoneState state, float holdUntil)
        {
            state.HoldReal = true;
            state.HoldWholeZone = true;
            state.HoldRealUntil = Mathf.Max(state.HoldRealUntil, holdUntil);
            if (state.Skipped.Count == 0)
            {
                // Nothing is waiting on the stand-ins, so the real pieces take over their colliders straight away.
                if (state.AwaitingRecreate == null) DestroyStandIns(state);
                return;
            }

            if (state.AwaitingRecreate == null)
            {
                state.AwaitingRecreate = new List<ZDO>(state.Skipped.Count);
                state.AwaitingSince = Time.unscaledTime;
            }
            // The whole zone is coming back, so every stand-in goes when it lands, not just the ones listed here.
            state.AwaitingIdentities = null;
            foreach (var entry in state.Skipped)
            {
                if (entry.Key == null) continue;
                entry.Key.Created = false;
                state.AwaitingRecreate.Add(entry.Key);
            }
            state.Skipped.Clear();
            state.StandInQueue.Clear();
            state.LastChangeUnscaledTime = Time.unscaledTime;
        }

        // Stand-ins go once vanilla has recreated every handed-back piece; a hold that has run out lets the zone skip
        // again, rebuilding its stand-ins first.
        private static void FinishHandBacks(float now)
        {
            var scene = ZNetScene.instance;
            var zdoMan = ZDOMan.instance;
            foreach (var state in _zones.Values)
            {
                if (state.AwaitingRecreate != null)
                {
                    bool done = now - state.AwaitingSince >= RecreateTimeoutSeconds;
                    if (!done && scene != null && zdoMan != null)
                    {
                        done = true;
                        for (int i = 0; i < state.AwaitingRecreate.Count; i++)
                        {
                            var zdo = state.AwaitingRecreate[i];
                            if (zdo == null || zdoMan.GetZDO(zdo.m_uid) != zdo) continue;
                            if (scene.FindInstance(zdo) == null) { done = false; break; }
                        }
                    }
                    if (done)
                    {
                        state.AwaitingRecreate = null;
                        if (state.AwaitingIdentities != null)
                        {
                            for (int i = 0; i < state.AwaitingIdentities.Count; i++) DropStandIn(state, state.AwaitingIdentities[i]);
                            state.AwaitingIdentities = null;
                        }
                        else DestroyStandIns(state);
                    }
                }

                if (state.HoldReal && !state.Dirty && state.AwaitingRecreate == null && now >= state.HoldRealUntil)
                {
                    state.HoldReal = false;
                    state.HoldWholeZone = false;
                    if (state.ZoneActive && state.Baked) QueueStandIns(state);
                }
            }
        }

        // Skipped ZDOs go back through creation on the next visit. A zone leaving range parks its stand-ins for that visit;
        // a teardown destroys them with the bake they matched.
        private static void ReleaseSkipped(ZoneState state, bool parkStandIns = false)
        {
            foreach (var entry in state.Skipped)
                if (entry.Key != null) entry.Key.Created = false;
            state.Skipped.Clear();
            state.AwaitingRecreate = null;
            state.AwaitingIdentities = null;
            if (parkStandIns && state.StandInRoot != null) state.StandInRoot.SetActive(false);
            else DestroyStandIns(state);
            state.StandInQueue.Clear();
            state.ConvertQueue.Clear();
            state.HoldReal = false;
            state.HoldWholeZone = false;
            state.HoldRealUntil = 0f;
        }

        private static void DestroyStandIns(ZoneState state)
        {
            if (state.StandInRoot != null) Object.Destroy(state.StandInRoot);
            state.StandInRoot = null;
            state.StandIns.Clear();
        }

        private static void DropStandIn(ZoneState state, MeshBaker.PieceIdentity identity)
        {
            if (!state.StandIns.TryGetValue(identity, out var standIn)) return;
            if (standIn != null) Object.Destroy(standIn);
            state.StandIns.Remove(identity);
        }

        private static void ReportSkipping(float now)
        {
            if (now < s_nextSkipReport) return;
            s_nextSkipReport = now + SkipReportSeconds;
            int total = s_skippedAtCreation + s_convertedLive + s_handedBackForBuilding + s_handedBackForChanges + s_standInZonesReused + s_handedBackLooks + s_joinedLate;
            if (total == s_lastReportedTotal) return;
            s_lastReportedTotal = total;

            int skippedNow = 0, zones = 0;
            foreach (var state in _zones.Values)
            {
                if (state.Skipped.Count == 0) continue;
                skippedNow += state.Skipped.Count;
                zones++;
            }
            double standInSeconds = (double)s_standInTicks / Stopwatch.Frequency;
            double microsPerCollider = s_standInColliders > 0 ? standInSeconds * 1000000.0 / s_standInColliders : 0.0;
            DeferCreation.Report();
            // THE REVISIT VERDICT. A zone EBM has never seen must instantiate its
            // pieces before it can bake them — MeshBaker works from live
            // WearNTear components, not ZDOs — so create-then-destroy on a FIRST
            // visit is the cost of learning the zone and cannot be scheduled
            // away. A zone that came back from the cache is the opposite case:
            // ConstructCachedZonesInRange builds its stand-ins when the terrain
            // loads, before vanilla reaches the pieces, so its converted count
            // SHOULD be ~0. If it is not, the pre-construct is losing the race
            // and that is a real bug rather than a cold cache.
            EasyBakeLog.Info(
                $"[Revisit] zones baked this period: {s_zonesFromCache} from cache, {s_zonesBakedFresh} fresh. " +
                $"Pieces built-then-unloaded: {s_convertedOnCached} on CACHED zones (want ~0 — the bake should have " +
                $"landed first), {s_convertedOnFresh} on FRESH zones (unavoidable; the baker needs the live pieces). " +
                (s_convertedOnCached > s_convertedOnFresh / 4 && s_zonesFromCache > 0
                    ? "CACHED zones are still paying the build-then-destroy cost — the pre-construct is not winning the race."
                    : "Cached zones are pre-constructing ahead of population as intended."));
            EasyBakeLog.Info(
                $"[Skip] {skippedNow} baked pieces are not created right now, in {zones} zones. So far: {s_skippedAtCreation} skipped at " +
                $"creation, {s_deferredForBake} held for a pending bake, {s_convertedLive} unloaded after creation, {s_standInColliders} stand-in colliders built in {standInSeconds:F1} s " +
                $"({microsPerCollider:F0} microseconds each; {s_standInsOnDemand} " +
                $"built as their pieces arrived, {s_standInZonesReused} zone revisits reused theirs); real pieces " +
                $"brought back: {s_handedBackForBuilding} for build tools, {s_handedBackForChanges} for removals or changes; " +
                $"{s_handedBackLooks} instanced pieces went back to drawing themselves after damage, a look change, burning or a hammer highlight; " +
                $"{s_joinedLate} pieces that arrived after their zone baked joined its instanced groups.");

            string data = PieceData.Report();
            if (data != null) EasyBakeLog.Info(data);
        }

        // ebm_census: every object this client created, grouped by the first reason it was not skipped, with the prefabs
        // behind each reason. One pass over all instances, so it only runs when asked.
        internal static string Census()
        {
            var scene = ZNetScene.instance;
            var instances = SceneInstances();
            if (scene == null || instances == null) return "[Census] No world is loaded.";

            int skipped = 0;
            foreach (var state in _zones.Values) skipped += state.Skipped.Count;
            s_mismatchExamples.Clear();

            var groups = new Dictionary<string, Dictionary<string, int>>();
            int created = 0;
            foreach (var entry in instances)
            {
                if (entry.Value == null || entry.Key == null) continue;
                var go = entry.Value.gameObject;
                var prefab = scene.GetPrefab(entry.Key.GetPrefab());
                string reason = WhyCreated(entry.Key, go);
                if (!groups.TryGetValue(reason, out var prefabs)) groups[reason] = prefabs = new Dictionary<string, int>();
                string name = prefab != null ? prefab.name : go.name;
                prefabs.TryGetValue(name, out int count);
                prefabs[name] = count + 1;
                created++;
            }

            var ordered = new List<KeyValuePair<string, int>>();
            foreach (var group in groups)
            {
                int total = 0;
                foreach (var n in group.Value.Values) total += n;
                ordered.Add(new KeyValuePair<string, int>(group.Key, total));
            }
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));

            var report = new StringBuilder();
            report.Append($"[Census] {created + skipped} objects around you: {skipped} baked pieces skipped, {created} created. Created, by why they were not skipped:");
            foreach (var group in ordered)
            {
                var top = new List<KeyValuePair<string, int>>(groups[group.Key]);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                report.Append($"\n  {group.Value,7}  {group.Key}. Most common: ");
                for (int i = 0; i < top.Count && i < 6; i++)
                    report.Append(i == 0 ? "" : ", ").Append(top[i].Key).Append(" x").Append(top[i].Value);
            }
            foreach (var example in s_mismatchExamples)
                report.Append($"\n  Example, {example.Key}: {example.Value}");
            return report.ToString();
        }

        private static string WhyCreated(ZDO zdo, GameObject go)
        {
            var wnt = go.GetComponent<WearNTear>();
            if (wnt == null)
            {
                if (go.GetComponent<Character>() != null) return "creatures and players";
                if (go.GetComponent<ItemDrop>() != null) return "items on the ground";
                if (go.GetComponent<Pickable>() != null) return "pickables";
                if (go.GetComponent<TreeBase>() != null || go.GetComponent<TreeLog>() != null || go.GetComponent<Destructible>() != null
                    || go.GetComponent<MineRock>() != null || go.GetComponent<MineRock5>() != null)
                    return "trees, rocks and bushes";
                return "other objects that are not building pieces";
            }
            bool invulnerable = InvulnerableClassifier.IsInvulnerable(wnt);
            if (!invulnerable && !FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value)
                return "building pieces that can take damage (DamageablePieces is off)";
            if (HasBakeUnsafeComponent(go)) return "doors, chests, item and armor stands, glass, shields, mine rocks and pieces excluded from baking (always left live)";

            Vector3 position = go.transform.position;
            if (!_zones.TryGetValue(ZoneSystem.GetZone(position), out var state) || !state.Baked || state.Bake == null)
                return "building pieces in a zone with no bake (too few pieces, or still settling)";
            var identity = MeshBaker.PieceIdentity.From(zdo);
            bool contributor = state.Bake.ContributorIdentities != null && state.Bake.ContributorIdentities.Contains(identity);
            if (!invulnerable)
                return contributor
                    ? "damageable pieces drawn by the bake (kept live on purpose, for wear, support and raids)"
                    : "damageable pieces the bake does not draw, " + WhyDamageableNotDrawn(state, zdo, wnt);
            if (!contributor) return "invulnerable pieces left out of their zone's bake, " + WhyLeftOutOfBake(state, zdo, go);

            var info = SkipEligibility.Get(zdo.GetPrefab());
            if (!info.Skippable) return "baked, but not skippable because the prefab has " + info.Blocker;
            if (state.HoldReal) return state.HoldWholeZone ? "baked, held real for a rebake" : "baked, held real for build tools";
            if (!state.Bake.PieceTransforms.TryGetValue(identity, out var cached)) return "baked, but no stand-in transform was recorded";
            string mismatch = SkipEligibility.DescribeMismatch(zdo, info, cached, out string detail);
            if (mismatch != null)
            {
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                string name = prefab != null ? prefab.name : go.name;
                if (s_mismatchExamples.Count < 8 && !s_mismatchExamples.ContainsKey(name)) s_mismatchExamples[name] = detail;
                return "baked, but " + mismatch;
            }
            return "baked and skippable, waiting to be unloaded";
        }

        private static readonly Dictionary<string, string> s_mismatchExamples = new Dictionary<string, string>();

        private static string WhyDamageableNotDrawn(ZoneState state, ZDO zdo, WearNTear wnt)
        {
            if (!MeshBaker.ShowsHealthyState(wnt)) return "worn or broken (a damageable piece draws its own look, since the next hit changes it)";
            if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) return "a field edit or Structure Tweaks key on it is not a look the bake draws";
            var healthy = new InstanceKey(zdo.GetPrefab(), HealthyLookOf(zdo.GetPrefab()));
            if (!InstanceDefinitionCache.TryGet(healthy, out _)) return "its prefab has several renderers or materials, which instancing cannot draw";
            return GroupFor(state, healthy.PrefabHash, healthy.Look) != null
                ? "handed back after a hammer highlight or burning, or damaged and since repaired"
                : "fewer copies of its prefab in the zone than MinInstancesPerPrefab when it baked";
        }

        // The combiner takes a piece only when every enabled near-tier renderer (LOD0, or in no LODGroup) has a readable mesh
        // and no per-object material values; instancing takes single-renderer prefabs with enough copies in the zone.
        private static string WhyLeftOutOfBake(ZoneState state, ZDO zdo, GameObject go)
        {
            int prefabHash = zdo.GetPrefab();
            if (PieceData.MustStayLive(zdo, prefabHash))
                return "a field edit or Structure Tweaks key on it is not a look the bake draws (" + PieceData.DescribeLiveEdits(zdo, prefabHash) + ")";
            var key = new InstanceKey(prefabHash, MeshBaker.LookOf(zdo));
            if (GroupFor(state, prefabHash, key.Look) != null)
                return "its prefab has an instanced group for its look here but it is not in it (handed back after a hammer highlight or a change)";

            string refusal = MeshBaker.WhyCombinerRefuses(prefabHash, go);
            if (refusal == null) return "too few copies for an instanced group and too few sharing its material for a combined batch";
            return InstanceDefinitionCache.TryGet(key, out _)
                ? refusal + ", and its zone has no instanced group for it yet"
                : refusal + ", and instancing cannot draw it either";
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
            // Item and armor stands show whatever is hung on them, added at runtime; a bake would freeze a stale item.
            if (HasComponentAnywhere<ItemStand>(go))       return true;
            if (HasComponentAnywhere<ArmorStand>(go))      return true;
            // Excluded: the piece's own mod opted it out through FiresCore's MeshBakeBridge, or the user listed it in
            // [Batching] ExcludedPrefabs.
            if (IsExcludedFromBaking(go))                  return true;
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

        // Never baked, instanced or skipped; these pieces always keep their own renderers.
        internal static bool IsExcludedFromBaking(GameObject go)
            => FiresCore.Bridge.MeshBakeBridge.IsExcludedFromBaking(go) || IsExcludedPrefab(go);

        private static HashSet<string> s_excludedPrefabs;

        internal static void ReloadExcludedPrefabs() => s_excludedPrefabs = null;

        private static bool IsExcludedPrefab(GameObject go)
        {
            if (s_excludedPrefabs == null)
            {
                s_excludedPrefabs = new HashSet<string>(System.StringComparer.Ordinal);
                foreach (var name in (FiresEasyBakeMeshesPlugin.BatchingExcludedPrefabs?.Value ?? string.Empty).Split(','))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0) s_excludedPrefabs.Add(trimmed);
                }
            }
            return s_excludedPrefabs.Count > 0 && s_excludedPrefabs.Contains(PrefabName(go));
        }

        private static string PrefabName(GameObject go)
        {
            string name = go.name;
            int paren = name.IndexOf('(');
            return paren > 0 ? name.Substring(0, paren).Trim() : name;
        }

        // Per-prefab-name verdict cache — the renderer/material walk runs once
        // per prefab kind, not per piece instance per zone scan.
        private static readonly Dictionary<string, bool> _transparentByName
            = new Dictionary<string, bool>(System.StringComparer.Ordinal);

        private static bool IsTransparentPiece(GameObject go)
        {
            string key = PrefabName(go);
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
