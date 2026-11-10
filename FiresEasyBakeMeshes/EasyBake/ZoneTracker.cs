using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;

using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    internal static partial class ZoneTracker   // partial: E26 / E27 census, coverage proof and change follow in ZoneTrackerBakeProof.cs ([worldgen] 11-09)
    {
        private class ZoneState
        {
            public Vector2s Coord;
            public HashSet<WearNTear> Pieces = new HashSet<WearNTear>();
            // 1.2.191 (BlueHills proof, lead 11-08): this zone's ZDO identities, rebuilt only when its sector's ZDO count moves (or after 2 s)
            public HashSet<MeshBaker.PieceIdentity> LiveIds; public Dictionary<MeshBaker.PieceIdentity, ZDO> LiveZdos; public int LiveStamp = -1; public float LiveAt, LiveCountChangedAt = -1f;
            // 1.2.193: FGN's zone signal (true until FGN says the zone is filling) and the cache double check
            public bool FgnSettled = true, SettledSinceCheck; public HashSet<MeshBaker.PieceIdentity> PendingMissing; public float RecheckAt, FgnFillingAt;
            // Trees, shrubs and bushes in this zone. Kept apart from Pieces because they have no WearNTear and
            // none of what the piece path reads from one: no wear look, no support, no invulnerability question.
            public readonly HashSet<ZNetView> Vegetation = new HashSet<ZNetView>();
            public float LastChangeUnscaledTime;
            // 1.2.88: when this zone's bake first waited on a pending material variation swap (-1 = not waiting).
            public float VariationWaitSince = -1f;
            public float ConstructedUnscaledTime;
            // 1.2.94: when the zone was soft-unloaded (hidden), for releasing old hidden bakes.
            public float HiddenSinceUnscaledTime;
            // 1.2.83: when this zone came due to bake, its pieces went to the front of the classify queue (-1 = not yet).
            public float ClassifyAskedAt = -1f;
            // 1.2.85: when its instance-group prep started (the bake waits for it up to ClassifyWaitSeconds from here).
            public float PrepAskedAt = -1f;
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
            public int SeasonEpochChecked;   // 1.2.180: the Vedr season its skipped pieces were last fully re-checked for
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
            public bool SaveRetried;   // 1.2.255 D8: a failed save is tried once more, never every frame
            public float LastSaveAt = -999f;   // 1.2.253 SaveOffThread: at most one save per SaveMinSeconds while the zone is active
            // 1.2.185: pieces ZNetScene still held when this zone unloaded; they got their renderers back then, and the reload re-adopts them.
            public readonly List<GameObject> UnloadSurvivors = new List<GameObject>();
            public readonly List<EasyBakeSuppressedVisuals> GiveBackQueue = new List<EasyBakeSuppressedVisuals>();   // [Optimize] ZoneTransitionBudgetMs
            public bool GiveBackQueued;

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

        // [seasons] 11-09 E25 (EbmBakeMemRelease): every baked zone with its bake and whether its cache file is behind it. Main thread.
        internal static void ForEachBakedZone(System.Action<Vector2s, MeshBaker.BakeResult, bool> visit)
        {
            foreach (var kv in _zones)
            {
                var s = kv.Value;
                if (s != null && s.Baked && s.Bake != null) visit(kv.Key, s.Bake, s.CacheStale);
            }
        }
        // 1.2.229 heap census (EbmHeapCensus): what the zone table holds, by element count x size
        internal static void HeapTotals(out int zones, out int groups, out long matrixBytes, out long idBytes, out int pieces)
        {
            zones = _zones.Count; groups = 0; matrixBytes = 0; idBytes = 0; pieces = 0;
            int idSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<MeshBaker.PieceIdentity>();
            foreach (var st in _zones.Values)
            {
                pieces += st.Pieces.Count;
                var gs = st.Bake != null ? st.Bake.InstanceGroups : null;
                if (gs == null) continue;
                groups += gs.Count;
                foreach (var g in gs) { if (g == null) continue; matrixBytes += (long)g.Matrices.Capacity * 64; idBytes += (long)g.Identities.Capacity * idSize; }
            }
        }

        // [Heap] by holder (Debug, inside the 60 s census): what the zone table holds beyond HeapTotals. Hash containers by Count (their real capacity is not readable), so a lower bound.
        internal static long HeapHolders(StringBuilder sb)
        {
            int idSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<MeshBaker.PieceIdentity>(), tfSize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<MeshBaker.PieceTransform>();
            int hidden = 0, liveHidden = 0, liveActive = 0, ids = 0, transforms = 0, suppressed = 0, skipped = 0, standIns = 0;
            long supB = 0;
            foreach (var st in _zones.Values)
            {
                int live = (st.LiveIds != null ? st.LiveIds.Count : 0) + (st.LiveZdos != null ? st.LiveZdos.Count : 0);
                if (st.ZoneActive) liveActive += live; else { hidden++; liveHidden += live; }
                skipped += st.Skipped.Count; standIns += st.StandIns.Count;
                if (st.Bake == null) continue;
                if (st.Bake.ContributorIdentities != null) ids += st.Bake.ContributorIdentities.Count;
                if (st.Bake.PieceTransforms != null) transforms += st.Bake.PieceTransforms.Count;
                var sup = st.Bake.SuppressedPieces;
                if (sup == null) continue;
                supB += sup.Capacity * 8L;
                foreach (var s in sup)
                {
                    if (s == null) continue;
                    suppressed++;
                    supB += 40L + BoolArrayBytes(s.RendererPriorEnabled) + BoolArrayBytes(s.LodGroupPriorEnabled);
                }
            }
            long slot = idSize + 16L;   // hash + next + bucket + padding
            long liveSlot = slot + 8L;   // between an id set slot and a ZDO map slot: close enough to name the holder
            long liveHiddenB = liveHidden * liveSlot, liveActiveB = liveActive * liveSlot, idsB = ids * slot, tfB = transforms * (idSize + tfSize + 16L), skipB = skipped * (idSize + 44L), standB = standIns * (idSize + 24L);
            double mb = 1.0 / (1024 * 1024);
            sb.Append($"zone LiveIds+LiveZdos {liveHiddenB * mb:0.0} MB in {hidden} hidden zone(s) ({liveHidden} entries) + {liveActiveB * mb:0.0} MB in active ({liveActive}); bake id sets {idsB * mb:0.0} MB ({ids}); bake transforms {tfB * mb:0.0} MB ({transforms}); suppressed visuals listed in bakes {supB * mb:0.0} MB ({suppressed}); skipped table {skipB * mb:0.0} MB ({skipped}); stand-in table {standB * mb:0.0} MB ({standIns}); ");
            return liveHiddenB + liveActiveB + idsB + tfB + supB + skipB + standB;
        }
        private static long BoolArrayBytes(bool[] a) => a == null ? 0L : 32L + ((a.Length + 7) & ~7);

        // 1.2.197 ([generator] piece-create audit, 11-08): does EBM's bake draw this piece instead of its own renderers? True when its
        // zone is baked, not held real, and the bake's contributors hold its identity (prefab + position + rotation, from its ZDO). Valid
        // from ZNetView.Awake on (the ZDO is bound there), so a visual-only Piece/WearNTear Awake hook can return early: whatever it sets
        // up on the renderers is silenced by the bake. One zone lookup + one hash lookup; main thread only.
        internal static bool DrawsPiece(GameObject piece)
        {
            if (piece == null) return false;
            var identity = MeshBaker.PieceIdentity.From(piece, piece.transform.position);
            if (identity.PrefabHash == 0) return false;   // no ZDO: not a networked piece EBM bakes
            if (!_zones.TryGetValue(EbmZones.Of(piece.transform.position), out var state)) return false;
            if (!state.Baked || state.Bake == null || state.HoldReal || state.Dirty) return false;
            // 1.2.198: every hand-back (damage, build hold, changes) fills AwaitingRecreate BEFORE the real piece is created, so a piece
            // being handed back is never reported as drawn by the bake: its Awake hooks run normally and need no PieceLeftBake.
            if (state.AwaitingRecreate != null) return false;
            var ids = state.Bake.ContributorIdentities;
            return ids != null && ids.Contains(identity);
        }

        // 1.2.133 ([visual] for [ghost]'s live map, overnight 10-27): every BAKED piece within radius of a point, skipped (no live
        // object) or not: prefab hash + world pose. Read-only; walks the baked zones' saved transforms and allocates nothing beyond
        // the caller's list. A map renderer draws these where the live scene has only stand-in colliders.
        internal static int BakedPiecesNear(Vector3 centre, float radius, List<FiresEasyBakeMeshes.EbmBakedPiece> into)
        {
            int before = into.Count; float r2 = radius * radius;
            foreach (var state in _zones.Values)
            {
                if (!state.Baked || state.Bake?.PieceTransforms == null) continue;
                foreach (var kv in state.Bake.PieceTransforms)
                {
                    var p = kv.Value.Position; float dx = p.x - centre.x, dz = p.z - centre.z;
                    if (dx * dx + dz * dz > r2) continue;
                    into.Add(new FiresEasyBakeMeshes.EbmBakedPiece { PrefabHash = kv.Key.PrefabHash, Position = p, Rotation = kv.Value.Rotation, Scale = kv.Value.Scale, Skipped = state.StandIns.ContainsKey(kv.Key) });
                }
            }
            return into.Count - before;
        }
        private static readonly List<Vector2s> _dropScratch = new List<Vector2s>();

        private const float BuildHoldSeconds = 30f;
        private const float BuildHoldRescanMeters = 2f;
        private const float RecreateTimeoutSeconds = 20f;
        private const float EmptyZoneGraceSeconds = 10f;
        private const float CacheValidationQuietSeconds = 10f;
        private const float ClassifyWaitSeconds = 3f;   // 1.2.83: the longest a due zone waits for its pieces' classification
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
        private static int s_handedBackForDamage;
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

        private static void OnVegetationCreated(GameObject go)
        {
            if (!FiresEasyBakeMeshesPlugin.SkipVegetation.Value) return;
            if (!VegetationInstancing.IsSkipCandidate(VegetationInstancing.KindOf(go))) return;

            var view = go.GetComponent<ZNetView>();
            if (view == null || view.GetZDO() == null) return;

            var coord = EbmZones.Of(go.transform.position);
            if (!_zones.TryGetValue(coord, out var state)) state = CreateZone(coord, "tree");   // [generator] defect 1: this path had no preload check, so the zone baked fresh and overwrote its fuller file
            if (!state.Vegetation.Add(view)) return;

            // Deliberately NOT marked dirty. A dirty baked zone is torn down and rebaked, and vegetation streams in
            // by the hundred, so dirtying here would rebake a forest zone once per tree. A zone that has not baked
            // yet does not need the flag - it bakes on its own once LastChangeUnscaledTime settles - and one that
            // already has keeps this tree real until something else rebakes it.
            state.LastChangeUnscaledTime = Time.unscaledTime;

            // 1.2.168: a skipped tree handed back (build hold, damage) arrives real while its group still draws it. It leaves the group and stays real.
            if (state.Baked && state.Bake?.ContributorIdentities != null)
            {
                var identity = MeshBaker.PieceIdentity.From(go, go.transform.position);
                if (state.Bake.ContributorIdentities.Contains(identity))
                {
                    // Where pieces are not skipped (a host without SkipOnHost), every tree of a cached zone arrives real. The group still draws it, so it is silenced as a piece is
                    // (OnInstanceCreated); leaving the group would drain the cached group tree by tree and rewrite the file each time.
                    if (!SkipCreationActive()) MeshBaker.DisableForCacheHit(go, state.Bake);
                    else
                    {
                        foreach (var group in state.Bake.InstanceGroups)
                            if (group.PrefabHash == identity.PrefabHash && group.TryRemove(identity)) break;
                        ForgetIdentityUnlessTwin(state, identity);
                        state.CacheStale = true;
                    }
                }
            }
        }

        public static void OnInstanceCreated(GameObject go)
        {
            if (go == null) return;
            var wnt = go.GetComponent<WearNTear>();
            if (wnt == null) { OnVegetationCreated(go); return; }
            bool invulnerable = InvulnerableClassifier.IsInvulnerable(wnt);
            if (!invulnerable && !FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value) return;
            if (HasBakeUnsafeComponent(go)) return;

            var coord = EbmZones.Of(wnt.transform.position);
            if (!_zones.TryGetValue(coord, out var state)) state = CreateZone(coord, "piece");
            if (!state.Pieces.Add(wnt)) return;
            MeshBaker.QueueClassify(wnt);   // 1.2.82: classified ahead of the bake under a frame budget

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
                    DropStandIn(state, identity);   // 1.2.168: the real piece has its own colliders; conversion builds the stand-in again
                    long shellStart = ShellPieces.Begin();   // [Optimize] ShellDamageablePieces: this silencing is what makes the piece a shell
                    MeshBaker.DisableForCacheHit(go, state.Bake);
                    ShellPieces.End(shellStart);
                    if ((invulnerable || SkipEligibility.DamageableSkipAllowed) && SkipCreationActive() && !ShellPieces.On) state.ConvertQueue.Enqueue(wnt);
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
            // Both of these were gated on invulnerability, and both have to widen together or a late-joining
            // damageable piece gets half the treatment: a transform with nothing queued to use it, or a queue
            // entry ZdoMatches must refuse for want of a transform.
            bool skippableHere = invulnerable || SkipEligibility.DamageableSkipAllowed;
            if (skippableHere) state.Bake.PieceTransforms[identity] = MeshBaker.PieceTransform.From(wnt.transform, look);
            long shellStart = ShellPieces.Begin();
            MeshBaker.DisableForCacheHit(wnt.gameObject, state.Bake);
            ShellPieces.End(shellStart);
            if (skippableHere && SkipCreationActive() && !ShellPieces.On) state.ConvertQueue.Enqueue(wnt);
            state.CacheStale = true;
            s_joinedLate++;
        }

        // A prefab the combiner cannot take has no other way into a bake, so the first of its pieces to arrive after the zone
        // baked opens the group the zone never built for it.
        private static ZoneInstanceGroup OpenInstanceGroup(ZoneState state, WearNTear wnt, int prefabHash, WearLook look, bool invulnerable)
        {
            if (!invulnerable || state.Bake?.InstanceGroups == null) return null;
            if (MeshBaker.WhyCombinerRefuses(prefabHash, wnt.gameObject) == null) return null;
            return NewInstanceGroup(state, prefabHash, look);
        }

        private static ZoneInstanceGroup NewInstanceGroup(ZoneState state, int prefabHash, WearLook look)
        {
            if (state.Bake?.InstanceGroups == null) return null;
            if (!InstanceDefinitionCache.TryGet(new InstanceKey(prefabHash, look), out var definition)) return null;

            var group = new ZoneInstanceGroup { PrefabHash = prefabHash, Look = look, Definition = definition };
            state.Bake.InstanceGroups.Add(group); Indirect.EbmIndirect.RequestFullWalk();
            return group;
        }

        // 1.2.253 [Optimize] LateJoinFromZdo: what TryJoinInstanceGroup does for a piece that was created, for one that need not be. The same tests, read from the
        // ZDO and prefab; a false takes today's create path, which then joins it the old way. On true the caller does a real skip's bookkeeping.
        private static readonly string[] LateJoinFallbackNames = { "instancing off", "zone busy", "already drawn by the bake", "no WearNTear", "bake-unsafe prefab", "damageable baking off", "not healthy", "ZDO refused", "no group", "combiner verdict not known yet" };
        private static readonly int[] s_lateJoinFallback = new int[10];
        private static int s_joinedFromZdo;
        private static bool s_saidLateJoin;

        private static bool LateJoinFail(int reason) { s_lateJoinFallback[reason]++; return false; }

        private static bool TryLateJoinFromZdo(ZoneState state, ZDO zdo, MeshBaker.PieceIdentity identity, SkipEligibility.PrefabInfo info, out MeshBaker.PieceTransform transform)
        {
            transform = default;
            if (FiresEasyBakeMeshesPlugin.LateJoinFromZdo == null || !FiresEasyBakeMeshesPlugin.LateJoinFromZdo.Value) return false;
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return LateJoinFail(0);
            // A dirty zone is waiting to rebake from real pieces. An unloaded one is torn down from another path.
            if (state.Dirty || !state.ZoneActive || state.Bake.ContributorIdentities == null) return LateJoinFail(1);
            // In the merged mesh with no saved transform: today's OnInstanceCreated silences it; a matrix here would draw it twice.
            if (state.Bake.ContributorIdentities.Contains(identity)) return LateJoinFail(2);
            if (!info.HasWear) return LateJoinFail(3);
            if (!info.BakeUnsafe.HasValue) info.BakeUnsafe = HasBakeUnsafeComponent(info.Prefab);
            if (info.BakeUnsafe.Value) return LateJoinFail(4);

            bool invulnerable = info.AllImmune || SkipEligibility.StoredHealthIsNegative(zdo, info);
            if (!invulnerable && !FiresEasyBakeMeshesPlugin.BatchingDamageablePieces.Value) return LateJoinFail(5);
            var look = WearLooks.Resolve(info.Prefab, zdo);
            if (!invulnerable && look != WearLooks.Healthy(info.Prefab)) return LateJoinFail(6);

            var candidate = new MeshBaker.PieceTransform
            {
                Position = zdo.GetPosition(),
                Rotation = zdo.GetRotation(),
                Scale = SkipEligibility.ExpectedScale(zdo, info),
                Look = look,
            };
            // The skip's own test: edits only a real piece carries, snow, damageable rules, a look switching on a blocker.
            if (!SkipEligibility.ZdoMatches(zdo, info, candidate)) return LateJoinFail(7);

            var group = GroupFor(state, identity.PrefabHash, look);
            if (group == null)
            {
                if (!invulnerable) return LateJoinFail(8);
                if (!MeshBaker.TryGetKnownCombinerRefusal(identity.PrefabHash, out string refusal)) return LateJoinFail(9);
                if (refusal == null) return LateJoinFail(8);   // the combiner takes it, so OpenInstanceGroup would not open one either
                group = NewInstanceGroup(state, identity.PrefabHash, look);
                if (group == null) return LateJoinFail(8);
            }

            group.AddLate(Matrix4x4.TRS(candidate.Position, candidate.Rotation, candidate.Scale), identity);
            state.Bake.ContributorIdentities.Add(identity);
            state.Bake.PieceTransforms[identity] = candidate;
            state.CacheStale = true;
            s_joinedFromZdo++;
            if (!s_saidLateJoin) { s_saidLateJoin = true; EasyBakeLog.Info("[Skip] LateJoinFromZdo on: pieces missing from a baked zone's cache join its instanced groups from their ZDOs, without creating an object"); }
            transform = candidate;
            return true;
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
            return WearLooks.Healthy(scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefabHash) : null);
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

        // The one way a zone starts being tracked. The disk read already happened off-thread (MeshCacheStore's preload), so TryGetPreloaded is a dictionary lookup; a zone the preload
        // has not reached yet misses its cache for this session.
        private static ZoneState CreateZone(Vector2s coord, string firstArrival)
        {
            var state = new ZoneState { Coord = coord, ConstructedUnscaledTime = Time.unscaledTime };
            _zones.Add(coord, state);
            if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value && MeshCacheStore.TryGetPreloaded(coord, out var cachedData))
            {
                ConstructCached(state, cachedData);
                if (state.Baked) EasyBakeLog.Debug($"[Cache] Zone ({coord.x},{coord.y}) built from its saved bake when its first {firstArrival} arrived");
            }
            else if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value) MeshCacheStore.NoteNoFile();
            return state;
        }

        // Identical stacked copies share one identity, one saved transform and one stand-in, but each has its own matrix. True while a matrix with this identity is still drawn.
        private static bool StillDrawn(ZoneState state, MeshBaker.PieceIdentity identity)
        {
            foreach (var group in state.Bake.InstanceGroups)
                if (group.PrefabHash == identity.PrefabHash && group.Identities.Contains(identity)) return true;
            return false;
        }

        // [generator] defects 3 + 4: after one matrix left, the identity goes only with the last copy, or the survivor keeps drawing with no transform or stand-in and, when it is
        // created, misses the contributor test and joins beside its own matrix (drawn twice).
        private static void ForgetIdentityUnlessTwin(ZoneState state, MeshBaker.PieceIdentity identity)
        {
            if (StillDrawn(state, identity))
            {
                EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): one of several stacked copies of prefab {identity.PrefabHash} at ({identity.X / 100f:0.##}, {identity.Y / 100f:0.##}, {identity.Z / 100f:0.##}) left its group; the other keeps the identity, transform and stand-in");
                return;
            }
            state.Bake.ContributorIdentities?.Remove(identity);
            state.Bake.PieceTransforms?.Remove(identity);
            DropStandIn(state, identity);
        }

        private static void ConstructCached(ZoneState state, MeshCacheStore.CachedZoneData cachedData)
        {
            var swCache = Stopwatch.StartNew();
            state.Bake = MeshCacheStore.ConstructFromCache(cachedData);
            swCache.Stop();
            if (state.Bake == null) return;

            state.Baked = true;
            state.FromCache = true; Indirect.EbmIndirect.RequestFullWalk();
            AnnounceWhenReady(state);   // 1.2.206 EbmApi.ZoneBaked
            s_zonesFromCache++;
            NoteCacheConstruct(state.Coord);
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
                EasyBakeLog.Debug(
                    $"[Cache] Zone ({state.Coord.x},{state.Coord.y}) constructed from preload: " +
                    $"{state.Bake.Batches.Count} batches, {state.Bake.ContributorIdentities?.Count ?? 0} cached identities.");
            QueueStandIns(state);
        }

        // A piece drawn as an instance stops matching it when it is worn, broken, burning or highlighted: its matrix leaves
        // the group and its own renderers draw again. Pieces merged into combined meshes would need a rebake and keep
        // their baked look.
        internal static void HandBackPiece(WearNTear wnt) => HandBackPiece(wnt, ShellPieces.Reason.LookOrDamage);

        internal static void HandBackPiece(WearNTear wnt, ShellPieces.Reason reason)
        {
            if (wnt == null || _zones.Count == 0) return;
            Vector3 position = wnt.transform.position;
            if (!_zones.TryGetValue(EbmZones.Of(position), out var state) || !state.Baked || state.Bake?.ContributorIdentities == null) return;
            var identity = MeshBaker.PieceIdentity.From(wnt.gameObject, position);
            if (state.Bake.ContributorIdentities.Contains(identity) && HandBack(state, wnt, identity)) ShellPieces.NoteHandBack(reason);
        }

        // Health changed on a piece that already exists. SetHealthVisual's first call, from WearNTear.Awake, is left to
        // OnInstanceCreated, which compares the look against the bake once the piece is registered.
        internal static void OnLookMayHaveChanged(WearNTear wnt)
        {
            if (wnt == null || _zones.Count == 0) return;
            Vector3 position = wnt.transform.position;
            if (!_zones.TryGetValue(EbmZones.Of(position), out var state) || !state.Baked || state.Bake?.ContributorIdentities == null) return;
            var identity = MeshBaker.PieceIdentity.From(wnt.gameObject, position);
            if (!state.Bake.ContributorIdentities.Contains(identity) || StillMatchesBake(state, wnt, identity)) return;
            if (HandBack(state, wnt, identity))
            {
                ShellPieces.NoteHandBack(ShellPieces.Reason.LookOrDamage);
                TryJoinInstanceGroup(state, wnt, identity, InvulnerableClassifier.IsInvulnerable(wnt));
            }
        }

        private static bool HandBack(ZoneState state, WearNTear wnt, MeshBaker.PieceIdentity identity)
        {
            var groups = state.Bake.InstanceGroups;
            bool removed = false;
            for (int i = 0; i < groups.Count && !removed; i++) removed = groups[i].TryRemove(identity);
            if (!removed) return false;

            ForgetIdentityUnlessTwin(state, identity);
            var suppression = wnt.GetComponent<EasyBakeSuppressedVisuals>();
            if (suppression != null)
            {
                suppression.RestoreVisuals();
                state.Bake.SuppressedPieces.Remove(suppression);
                Object.DestroyImmediate(suppression);   // 1.2.168: TryJoinInstanceGroup may suppress again this frame and would find the doomed one, leaving the piece drawn twice
            }
            state.CacheStale = true;
            s_handedBackLooks++;
            return true;
        }

        public static void OnPieceDestroyed(WearNTear wnt)
        {
            if (wnt == null) return;
            long ztDestroyed = EbmSelfCost.ZT();   // Debug: per piece, summed; outside every other timer
            InvulnerableClassifier.Forget(wnt);
            foreach (var state in _zones.Values)
            {
                if (state.Pieces.Remove(wnt) && MeshBaker.ForgetClassed(wnt))
                {
                    state.LastChangeUnscaledTime = Time.unscaledTime;
                    // Deliberately NOT marking dirty here — GameObject destruction
                    // fires for both real removes AND zone unloads (the pieces come
                    // back at the same positions next visit), and marking dirty on
                    // unload would force a rebake every zone-crossing. REAL removals
                    // are handled by OnZdoDestroyed below: a genuine remove destroys
                    // the piece's ZDO, a zone unload never does (persistent ZDOs
                    // survive) — that's the distinguisher this path lacks.
                    EbmSelfCost.ZAcc(EbmSelfCost.ZsPieceDestroyed, ztDestroyed);
                    return;
                }
            }
            EbmSelfCost.ZAcc(EbmSelfCost.ZsPieceDestroyed, ztDestroyed);
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
            if (!_zones.TryGetValue(EbmZones.Of(pos), out var state)) return;
            state.Skipped.Remove(zdo);
            // R85 (the lead: "line of sight blocked by [EasyBake/Zone_-14_-4/standins]" on test area ONE): a destroyed piece's
            // stand-in collider was only dropped when the zone had a current bake with contributors, so the drill courses'
            // removed walls left invisible colliders behind. The stand-in goes first, whatever the bake's state.
            var identity = MeshBaker.PieceIdentity.From(zdo);
            // [generator] defect 3: only a real contributor pays for the group walks (this runs for every ZDO destroyed in a tracked zone)
            if (state.Baked && state.Bake?.ContributorIdentities != null && state.Bake.ContributorIdentities.Contains(identity))
            {
                bool one = false;
                foreach (var group in state.Bake.InstanceGroups)
                    if (group.PrefabHash == identity.PrefabHash && group.TryRemove(identity)) { one = true; state.CacheStale = true; break; }
                if (one)
                {
                    if (SkipEligibility.Get(identity.PrefabHash).HasWear) ShellPieces.NoteHandBack(ShellPieces.Reason.Removed);
                    ForgetIdentityUnlessTwin(state, identity);
                    if (!StillDrawn(state, identity) && FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                        EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): instanced piece removed — dropped without rebake.");
                    return;
                }
            }
            DropStandIn(state, identity);
            if (!state.Baked || state.Bake == null) return;
            var ids = state.Bake.ContributorIdentities;
            if (ids == null || ids.Count == 0) return;

            state.Bake.PieceTransforms?.Remove(identity);
            if (!ids.Remove(identity))
            {
                // 1.2.167: identical stacked copies share one identity, so the second destroy lands here with its matrix still drawn
                foreach (var stacked in state.Bake.InstanceGroups)
                    if (stacked.PrefabHash == identity.PrefabHash && stacked.TryRemove(identity)) { state.CacheStale = true; break; }
                return;   // not part of the combined mesh
            }

            // Instanced pieces drop straight out of their group. No geometry was merged
            // for them, so there is nothing to rebuild — this is the invalidation win
            // instancing exists for.
            var instanceGroups = state.Bake.InstanceGroups;
            for (int i = 0; i < instanceGroups.Count; i++)
            {
                if (!instanceGroups[i].TryRemove(identity)) continue;
                state.CacheStale = true;
                if (FiresEasyBakeMeshesPlugin.VerboseZoneLogging.Value)
                    EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): instanced piece removed — dropped without rebake.");
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
                EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): batched piece removed for real — rebake queued.");
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

            float switchDistance = FiresEasyBakeMeshesPlugin.BatchingFarTierDistance.Value * PerformanceProfile.Distance;   // 1.2.135
            float enterFar = switchDistance;
            float leaveFar = Mathf.Max(0f, switchDistance - FarTierHysteresisMeters);
            float threshold = state.Bake.ShowingFarTier ? leaveFar : enterFar;

            // 1.2.90 (Fire 10-12, #8: stone floors with dirt levelled up to them vanished until the hammer came out): measured
            // to the zone's NEAREST point, not its centre. From the next zone a piece on the shared edge is ~20-30 m away while
            // the centre is past 64 m, so the zone drew LOD1, whose lower top sat under the levelled dirt. Now nothing within
            // the switch distance draws LOD1.
            // 1.2.122: through EbmZones (x,z box off the ring; 3D to the ring sector on the ring, where world y is not height)
            float sqrDistance = EbmZones.NearestSqr(coord, player.transform.position);

            MeshBaker.SetFarTierActive(state.Bake, sqrDistance > threshold * threshold);
        }

        // Instanced groups are immediate-mode draws, so they must be re-issued every
        // frame. Only zones that are loaded and baked draw, which reuses the same
        // lifecycle gate the combined meshes already follow, and each group carries its
        // own worldBounds so Unity culls a whole zone's instances in one test. Groups
        // InstancedDraw finds out of view never reach Unity at all.
        public static void DrawInstances()
        {
            DrawTestFrame();   // 1.2.104 ebm_drawtest (idle unless the command ran in the last 30 s)
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return;

            InstancedDraw.BeginFrame();
            bool indirect = Indirect.EbmIndirect.BeginFrame();   // 1.2.203 [Optimize] IndirectRenderer
            // 1.2.213 [Optimize] IndirectCachedAlive: each shared definition is checked once this frame (-1 = every group checks, as 1.2.212)
            int aliveFrame = FiresEasyBakeMeshesPlugin.IndirectCachedAlive == null || FiresEasyBakeMeshesPlugin.IndirectCachedAlive.Value ? Time.frameCount : -1;
            bool settled = indirect && Indirect.EbmIndirect.BeginSettledFrame();   // [Optimize] IndirectSettledWalk: nothing structural changed: only the work list + the legacy groups
            bool keepLists = indirect && !settled && Indirect.EbmIndirect.SettledOn;
            if (!settled) { s_settledZones.Clear(); s_settledLegacy.Clear(); }
            if (settled) DrawSettled(); else
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked) continue;
                var bake = state.Bake;
                if (bake == null || bake.InstanceGroups.Count == 0) continue;
                if (!bake.Ready) continue;   // its pieces aren't all silenced yet (time-sliced suppression): no double draw
                long sd0 = InstancedDraw.SubT();
                bool dead = HasDeadGroup(bake, aliveFrame);
                InstancedDraw.SubAcc(InstancedDraw.SubDead, sd0);
                if (dead) { s_deadBakes.Add(state); continue; }
                if (keepLists) s_settledZones.Add(state);

                InstancedDraw.NoteZone();
                for (int i = 0; i < bake.InstanceGroups.Count; i++)
                {
                    var group = bake.InstanceGroups[i];
                    long st0 = InstancedDraw.SubT();
                    bool took = indirect && Indirect.EbmIndirect.Take(group);
                    InstancedDraw.SubAcc(InstancedDraw.SubTake, st0);
                    if (took) continue;   // 1.2.203: drawn GPU-driven in EndFrame, every view, own cull + LOD
                    if (keepLists) s_settledLegacy.Add((group, bake));
                    long sl0 = InstancedDraw.SubT();
                    if (InstancedDraw.Submit(group)) group.Draw(bake.ShowingFarTier);
                    else InstancedDraw.NoteCulled(group, bake.ShowingFarTier);   // 1.2.114: other cameras may still see it
                    InstancedDraw.SubAcc(InstancedDraw.SubLegacy, sl0);
                }
            }
            if (indirect && !settled) Indirect.EbmIndirect.NoteFullWalkEnd();
            long se0 = InstancedDraw.SubT();
            if (indirect) Indirect.EbmIndirect.EndFrame();
            InstancedDraw.SubAcc(InstancedDraw.SubIndirect, se0);
            InstancedDraw.EndFrame();
            if (s_deadBakes.Count > 0) ReleaseDeadBakes();
        }

        // 1.2.101 (Fire 10-18: stone floors invisible until the hammer came out). When another mod releases a prefab's assets
        // (FiresAdminPrefabs' LazyBundles returns idle prefabs to stubs), the meshes an instanced group draws are destroyed and
        // the group drew nothing while its pieces stayed silenced. Such a zone is torn down, which gives every piece its own
        // renderers back (what the hammer's build hold did), and it bakes again later from the prefab as it is then.
        private static readonly List<ZoneState> s_deadBakes = new List<ZoneState>();

        // [Optimize] IndirectSettledWalk ([wishbone] 11-09): both lists are rebuilt by every full walk; every place that can add, remove or stop a group asks for one (EbmIndirect.RequestFullWalk).
        private static readonly List<ZoneState> s_settledZones = new List<ZoneState>();
        private static readonly List<(ZoneInstanceGroup group, MeshBaker.BakeResult bake)> s_settledLegacy = new List<(ZoneInstanceGroup, MeshBaker.BakeResult)>();
        private static int s_settledCursor;
        private const int SettledDeadCheckShare = 8;   // one zone in this many is checked for destroyed meshes each frame: no Unity event says another mod released one; every draw path skips a dead batch meanwhile

        private static void DrawSettled()
        {
            long st0 = InstancedDraw.SubT();
            Indirect.EbmIndirect.WalkWork();
            InstancedDraw.SubAcc(InstancedDraw.SubTake, st0);
            long sd0 = InstancedDraw.SubT();
            int zones = s_settledZones.Count, frame = Time.frameCount;
            for (int i = 0, n = Mathf.Max(1, zones / SettledDeadCheckShare); i < n && zones > 0; i++)
            {
                if (s_settledCursor >= zones) s_settledCursor = 0;
                var state = s_settledZones[s_settledCursor++];
                if (state.Bake != null && HasDeadGroup(state.Bake, frame)) s_deadBakes.Add(state);
            }
            InstancedDraw.SubAcc(InstancedDraw.SubDead, sd0);
            InstancedDraw.NoteZones(zones);
            long sl0 = InstancedDraw.SubT();
            for (int i = 0; i < s_settledLegacy.Count; i++)
            {
                var (group, bake) = s_settledLegacy[i];
                if (group.Definition != null && !group.Definition.IsAliveCached(frame)) continue;   // the full walk would have skipped this zone
                if (InstancedDraw.Submit(group)) group.Draw(bake.ShowingFarTier);
                else InstancedDraw.NoteCulled(group, bake.ShowingFarTier);
            }
            InstancedDraw.SubAcc(InstancedDraw.SubLegacy, sl0);
        }

        // 1.2.102 (Fire 10-18, stone_floor_2x2 invisible): `ebm_why` names what EBM does with the piece under the crosshair.
        // Read-only. Also written to the log at Info.
        internal static IEnumerable<string> Why()
        {
            var cam = global::Utils.GetMainCamera();
            if (cam == null) { yield return "[ebm_why] no camera"; yield break; }
            if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore))
            { yield return "[ebm_why] the view hits nothing within 60 m"; yield break; }
            var view = hit.collider.GetComponentInParent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo == null)
            {
                bool standIn = hit.collider.transform.root != null && hit.collider.transform.root.name.IndexOf("StandIn", System.StringComparison.OrdinalIgnoreCase) >= 0;
                yield return $"[ebm_why] hit '{hit.collider.name}' (root '{hit.collider.transform.root.name}'): no ZNetView{(standIn ? " - an EBM stand-in collider, so the piece is SKIPPED (not created) and drawn by its zone's bake" : "")}";
                // 1.2.103 (Fire's 1.2.102 ebm_why: the invisible stone floors are skipped pieces): find the piece in the bakes and
                // name exactly what is meant to draw it.
                foreach (var line in WhySkipped(hit.point)) yield return line;
                yield break;
            }
            foreach (var line in WhyFor(view)) yield return line;
        }

        // 1.2.117: SnowField.ZoneChanged -> every instanced group of that zone rebuilds its snow values at its next draw.
        internal static void MarkSnowDirty(Vector2s zone)
        {
            if (!_zones.TryGetValue(zone, out var state) || state.Bake?.InstanceGroups == null) return;
            foreach (var group in state.Bake.InstanceGroups) { if (group == null) continue; group.MarkSnowDirty(); s_areaMarked++; }
        }

        // 1.2.181: Vedr's VedrApi.PieceSnowRevision moved (season stage, weather, winter cover step, a piece placed / removed, a shovel clear, a fire ring):
        // every loaded zone's groups rebuild their snow values, through the same per-frame re-split budget as Core's events.
        internal static void MarkAllSnowDirty()
        {
            foreach (var state in _zones.Values)
            {
                if (state.Bake?.InstanceGroups == null) continue;
                foreach (var group in state.Bake.InstanceGroups) { if (group == null) continue; group.MarkSnowDirty(); s_areaMarked++; }
            }
        }

        // 1.2.158 ([visual], Fire 10-31 event/cache rule; Core 0.2.1074 SnowField.ZoneAreaChanged(zone, rect, wholeZone), raised just
        // before every ZoneChanged): a local change (wholeZone false: a piece placed / removed) dirties only the groups whose world
        // bounds overlap the changed XZ rect; a whole-zone change dirties every group of the zone, exactly as ZoneChanged did.
        // SnowFeed binds this instead of ZoneChanged when the event exists. Same values, fewer re-splits.
        private const float AreaMargin = 0.5f;   // metres: a rect edge that just touches a group still counts
        private static int s_areaEvents, s_areaLocalEvents, s_areaMarked, s_areaSpared;

        internal static void MarkSnowDirtyArea(Vector2s zone, Rect area, bool wholeZone)
        {
            s_areaEvents++;
            if (wholeZone) { MarkSnowDirty(zone); return; }
            s_areaLocalEvents++;
            if (!_zones.TryGetValue(zone, out var state) || state.Bake?.InstanceGroups == null) return;
            float minX = area.xMin - AreaMargin, maxX = area.xMax + AreaMargin, minZ = area.yMin - AreaMargin, maxZ = area.yMax + AreaMargin;
            foreach (var group in state.Bake.InstanceGroups)
            {
                if (group == null) continue;
                var b = group.WorldBounds;
                if (b.max.x < minX || b.min.x > maxX || b.max.z < minZ || b.min.z > maxZ) { s_areaSpared++; continue; }
                group.MarkSnowDirty();
                s_areaMarked++;
            }
        }

        // the 30 s [SnowBake] summary's area part; resets its counters
        internal static string SnowAreaSummary()
        {
            string s = SnowFeed.AreaEventBound
                ? $"snow change events {s_areaEvents} ({s_areaLocalEvents} local): {s_areaMarked} group(s) marked, {s_areaSpared} spared by the area test"
                : $"{s_areaMarked} group(s) marked by whole-zone ZoneChanged (Core has no ZoneAreaChanged)";
            s_areaEvents = s_areaLocalEvents = s_areaMarked = s_areaSpared = 0;
            return s;
        }

        // 1.2.115: the same report for a given piece (StationInteractProbe: the station that was used, not whatever the camera ray hits).
        internal static IEnumerable<string> WhyFor(ZNetView view)
        {
            var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            if (zdo == null) { yield return "[ebm_why] the piece has no ZDO"; yield break; }
            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(zdo.GetPrefab()) : null;
            var look = MeshBaker.LookOf(zdo);
            var coord = EbmZones.Of(zdo.GetPosition());
            bool suppressed = view.GetComponent<EasyBakeSuppressedVisuals>() != null;
            yield return $"[ebm_why] '{(prefab != null ? prefab.name : zdo.GetPrefab().ToString())}' look {look} zone {coord}: own renderers {(suppressed ? "SILENCED by EBM" : "drawing")}";
            // 1.2.104 (the lead 10-18): what each real renderer adds on top of its material (the instanced draw has none of it).
            foreach (var r in view.GetComponentsInChildren<MeshRenderer>(true))
                yield return "[ebm_why]   real renderer " + DescribeRenderer(r);

            if (!_zones.TryGetValue(coord, out var state)) { yield return "[ebm_why] zone not tracked by EBM"; }
            else
            {
                var bake = state.Bake;
                yield return $"[ebm_why] zone: active {state.ZoneActive}, baked {state.Baked}, ready {(bake != null && bake.Ready)}, far tier {(bake != null && bake.ShowingFarTier)}, hold real {state.HoldReal}, skipped {state.Skipped.Count}";
                if (bake != null)
                {
                    var id = MeshBaker.PieceIdentity.From(zdo);
                    bool found = false;
                    foreach (var group in bake.InstanceGroups)
                    {
                        if (!group.Identities.Contains(id)) continue;
                        found = true;
                        var def = group.Definition;
                        yield return $"[ebm_why] in an instanced group: {group.Matrices.Count} instances, look {group.Look}, alive {(def != null && def.IsAlive())}, bounds {group.WorldBounds}";
                        yield return "[ebm_why] parts: " + (def != null ? InstanceDefinition.Describe(def.Parts) : "(no definition)");
                    }
                    if (!found) yield return bake.PieceTransforms.ContainsKey(id) ? "[ebm_why] in the zone's combined mesh (not instanced)" : "[ebm_why] not in the zone's bake";
                }
            }
            if (prefab != null)
            {
                bool ok = InstanceDefinition.TryBuild(prefab, look, out var fresh, out string reason);
                yield return ok ? "[ebm_why] a fresh definition would draw: " + InstanceDefinition.Describe(fresh.Parts) : "[ebm_why] a fresh definition is refused: " + reason;
                foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                    yield return $"[ebm_why]   prefab renderer '{r.name}' {r.GetType().Name} enabled {r.enabled} active {r.gameObject.activeSelf} shown-for-look {WearLooks.IsShown(prefab, look, r.transform)} shadows {r.shadowCastingMode}";
            }
        }

        private static readonly string[] s_mpbFloats = { "_Cutoff", "_RippleDistance", "_RippleFreq", "_ValueNoise", "_ValueNoiseVertex", "_MoveableObject", "_TriplanarMap", "_TriplanarLocalPos", "_AddRain", "_Glossiness", "_Metallic" };
        private static readonly string[] s_mpbColors = { "_Color", "_EmissionColor", "_MetalColor" };
        private static readonly MaterialPropertyBlock s_mpb = new MaterialPropertyBlock();

        private static string DescribeRenderer(MeshRenderer r)
        {
            var sb = new StringBuilder();
            var mf = r.GetComponent<MeshFilter>();
            var m = r.sharedMaterial;
            sb.Append('\'').Append(r.name).Append("' enabled ").Append(r.enabled).Append(" active ").Append(r.gameObject.activeInHierarchy)
              .Append(" mesh '").Append(mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "null").Append("' mat '").Append(m != null ? m.name : "null")
              .Append("' shader '").Append(m != null && m.shader != null ? m.shader.name : "null").Append("' keywords [").Append(m != null ? string.Join(",", m.shaderKeywords) : "")
              .Append("] queue ").Append(m != null ? m.renderQueue : -1).Append(" inst ").Append(m != null && m.enableInstancing);
            if (m != null)
                foreach (var p in s_mpbFloats) if (m.HasProperty(p)) sb.Append(' ').Append(p).Append('=').Append(m.GetFloat(p).ToString("0.###"));
            if (m != null && m.HasProperty("_Color")) sb.Append(" _Color=").Append(m.GetColor("_Color"));
            if (!r.HasPropertyBlock()) { sb.Append(" | no property block"); return sb.ToString(); }
            r.GetPropertyBlock(s_mpb);
            sb.Append(" | PROPERTY BLOCK:");
            foreach (var p in s_mpbFloats) if (s_mpb.HasFloat(p)) sb.Append(' ').Append(p).Append('=').Append(s_mpb.GetFloat(p).ToString("0.###"));
            foreach (var p in s_mpbColors) if (s_mpb.HasColor(p) || s_mpb.HasVector(p)) sb.Append(' ').Append(p).Append('=').Append(s_mpb.GetColor(p));
            if (s_mpb.HasTexture("_MainTex")) sb.Append(" _MainTex=").Append(s_mpb.GetTexture("_MainTex") != null ? s_mpb.GetTexture("_MainTex").name : "null");
            if (s_mpb.isEmpty) sb.Append(" (empty)");
            return sb.ToString();
        }

        // 1.2.107 `ebm_drawtest` (Fire 10-18: Custom/FiresPiece draws nothing instanced; find which part of it breaks).
        // For 30 s draws the looked-at piece's mesh 2 m above it as a row of copies, 2.5 m apart, numbered LEFT to RIGHT:
        //  1 plain Graphics.DrawMesh (reference: must show)
        //  2 RenderMeshInstanced, its own material (what EBM does)
        //  3 legacy Graphics.DrawMeshInstanced, its own material
        //  4 RenderMeshInstanced, a copy with every shader keyword off
        //  5 RenderMeshInstanced, a copy with _Cutoff 0 (no alpha clip)
        //  6 RenderMeshInstanced, a copy with _NORMALMAP off only
        //  7 RenderMeshInstanced, own material, renderer's layer + light probes + no shadows
        private static Mesh s_testMesh;
        private static string s_testTarget;   // 1.2.112
        private static int s_testSub;
        private static Material s_testMat;
        private static Material[] s_testVariants;
        private static Matrix4x4 s_testAt;
        private static int s_testLayer;
        private static float s_testUntil;
        private static readonly Matrix4x4[] s_testOne = new Matrix4x4[1];

        // 1.2.112 ([seasons] 10-20: the test runner's crosshair kept landing on a rock): `ebm_drawtest <prefab>` targets the
        // nearest created piece of that prefab within 60 m instead of the crosshair. Without an argument it uses the crosshair.
        internal static string DrawTest(string prefabName = null)
        {
            var cam = global::Utils.GetMainCamera();
            if (cam == null) return "[ebm_drawtest] no camera";
            ZNetView view = null;
            string target;
            if (!string.IsNullOrEmpty(prefabName))
            {
                var player = Player.m_localPlayer;
                Vector3 from = player != null ? player.transform.position : cam.transform.position;
                float best = 60f * 60f;
                foreach (var v in UnityEngine.Object.FindObjectsOfType<ZNetView>())
                {
                    if (v == null || !v.IsValid()) continue;
                    if (!string.Equals(global::Utils.GetPrefabName(v.gameObject), prefabName, System.StringComparison.OrdinalIgnoreCase)) continue;
                    float d = (v.transform.position - from).sqrMagnitude;
                    if (d < best) { best = d; view = v; }
                }
                if (view == null) return $"[ebm_drawtest] no created '{prefabName}' within 60 m (a baked, skipped piece has no object: place one, or take out the hammer near it)";
                target = $"nearest '{prefabName}' {Mathf.Sqrt(best):0.0} m away";
            }
            else
            {
                if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, 60f, ~0, QueryTriggerInteraction.Ignore))
                    return "[ebm_drawtest] the view hits nothing within 60 m";
                view = hit.collider.GetComponentInParent<ZNetView>();
                target = $"crosshair hit '{(view != null ? global::Utils.GetPrefabName(view.gameObject) : hit.collider.name)}' {hit.distance:0.0} m away";
                if (view == null) return $"[ebm_drawtest] {target}: no ZNetView (aim at a real piece, not a stand-in, or use ebm_drawtest <prefab>)";
            }
            MeshRenderer r = null;
            foreach (var c in view.GetComponentsInChildren<MeshRenderer>(true))
                if (c.gameObject.activeInHierarchy && c.GetComponent<MeshFilter>() != null && c.sharedMaterial != null) { r = c; break; }
            if (r == null) return $"[ebm_drawtest] {target}: no active mesh renderer";
            s_testTarget = target;
            s_testMesh = r.GetComponent<MeshFilter>().sharedMesh;
            s_testSub = 0;
            s_testMat = r.sharedMaterial;
            s_testLayer = r.gameObject.layer;
            var inst = s_testMat.enableInstancing ? s_testMat : new Material(s_testMat) { enableInstancing = true };
            var noKw = new Material(s_testMat) { enableInstancing = true, shaderKeywords = new string[0] };
            var noClip = new Material(s_testMat) { enableInstancing = true };
            if (noClip.HasProperty("_Cutoff")) noClip.SetFloat("_Cutoff", 0f);
            var noNormal = new Material(s_testMat) { enableInstancing = true };
            noNormal.DisableKeyword("_NORMALMAP");
            // 1.2.108 controls: Standard, and the first loaded material still on vanilla Custom/Piece (double-sided pieces keep it).
            var stdShader = Shader.Find("Standard");
            var std = stdShader != null ? new Material(stdShader) { enableInstancing = true, color = Color.magenta } : null;
            Material vanilla = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (m != null && m.shader != null && m.shader.name == "Custom/Piece") { vanilla = new Material(m) { enableInstancing = true }; break; }
            s_testVariants = new[] { inst, noKw, noClip, noNormal, std, vanilla };
            s_testAt = Matrix4x4.TRS(r.transform.position + Vector3.up * 2f, r.transform.rotation, r.transform.lossyScale);
            s_testUntil = Time.unscaledTime + 30f;
            string line = $"[ebm_drawtest] {s_testTarget}: 30 s, 2 m above '{r.name}' (material '{s_testMat.name}', shader '{s_testMat.shader.name}', keywords [{string.Join(",", s_testMat.shaderKeywords)}]): 9 copies numbered LEFT to RIGHT: 1 plain, 2 instanced, 3 legacy instanced, 4 instanced no keywords, 5 instanced no alpha clip, 6 instanced no normal map, 7 instanced renderer layer+probes, 8 instanced Standard (magenta) control, 9 instanced vanilla Custom/Piece control ({(vanilla != null ? vanilla.name : "NONE FOUND")}). Which numbers show?";
            EasyBakeLog.Debug(line);
            // 1.2.109 (the lead 10-18): which Custom/FiresPiece objects are loaded, which one this material holds, which one
            // TossinShade loaded. Each loaded copy is also drawn instanced as copies 10, 11, ... further right.
            var sb = new StringBuilder("[ebm_drawtest] shaders: material's ").Append(DescribeShader(s_testMat.shader))
                .Append("; TossinShade's ").Append(DescribeShader(TossinShadePieceShader()))
                .Append("; globalMaximumLOD ").Append(Shader.globalMaximumLOD).Append("; loaded copies named ").Append(s_testMat.shader.name).Append(':');
            var extra = new List<Material>();
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (sh == null || sh.name != s_testMat.shader.name) continue;
                sb.Append(" [copy ").Append(10 + extra.Count).Append(": ").Append(DescribeShader(sh)).Append(']');
                var m = new Material(s_testMat) { enableInstancing = true };
                m.shader = sh;
                extra.Add(m);
            }
            s_testExtra = extra.ToArray();
            EasyBakeLog.Debug(sb.ToString());
            return line + "\n" + sb;
        }

        private static Material[] s_testExtra;

        private static string DescribeShader(Shader sh)
        {
            if (sh == null) return "(none)";
            return $"id {sh.GetInstanceID()} supported {sh.isSupported} passes {sh.passCount} subshaders {sh.subshaderCount} maxLOD {sh.maximumLOD} keywords {sh.keywordSpace.keywordCount}";
        }

        // The loaded "Custom/FiresPiece" shader for the drawtest copies, found by its shader name (1.2.210: never TossinShade's private field).
        private static Shader TossinShadePieceShader() => Shader.Find("Custom/FiresPiece");

        private static System.Type[] SafeTypes(System.Reflection.Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return System.Array.FindAll(ex.Types, x => x != null); }
            catch { return new System.Type[0]; }
        }

        private static void DrawTestFrame()
        {
            if (s_testMesh == null || s_testMat == null || s_testVariants == null || Time.unscaledTime > s_testUntil) return;
            var step = s_testAt.MultiplyVector(Vector3.right).normalized * 2.5f;
            Matrix4x4 At(int n) => Matrix4x4.Translate(step * (n - 4)) * s_testAt;   // copy 4 sits right above the piece
            Bounds B(int n) => new Bounds(At(n).GetColumn(3), Vector3.one * 50f);

            Graphics.DrawMesh(s_testMesh, At(1), s_testMat, s_testLayer, null, s_testSub);
            DrawOne(2, s_testVariants[0], At(2), B(2), 0, false);
            s_testOne[0] = At(3);
            Graphics.DrawMeshInstanced(s_testMesh, s_testSub, s_testVariants[0], s_testOne, 1);
            DrawOne(4, s_testVariants[1], At(4), B(4), 0, false);
            DrawOne(5, s_testVariants[2], At(5), B(5), 0, false);
            DrawOne(6, s_testVariants[3], At(6), B(6), 0, false);
            DrawOne(7, s_testVariants[0], At(7), B(7), s_testLayer, true);
            if (s_testVariants[4] != null) DrawOne(8, s_testVariants[4], At(8), B(8), 0, false);
            if (s_testVariants[5] != null) DrawOne(9, s_testVariants[5], At(9), B(9), 0, false);
            if (s_testExtra != null)
                for (int i = 0; i < s_testExtra.Length; i++) DrawOne(10 + i, s_testExtra[i], At(10 + i), B(10 + i), 0, false);
        }

        private static void DrawOne(int n, Material m, Matrix4x4 at, Bounds b, int layer, bool rendererLike)
        {
            var rp = new RenderParams(m) { worldBounds = b, layer = layer };
            if (rendererLike)
            {
                rp.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.BlendProbes;
                rp.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            s_testOne[0] = at;
            Graphics.RenderMeshInstanced(in rp, s_testMesh, s_testSub, s_testOne, 1, 0);
        }

        private static IEnumerable<string> WhySkipped(Vector3 point)
        {
            ZoneState best = null;
            MeshBaker.PieceIdentity bestId = default;
            MeshBaker.PieceTransform bestT = default;
            float bestD = 9f;   // 3 m
            foreach (var state in _zones.Values)
            {
                if (state.Bake == null) continue;
                foreach (var kv in state.Bake.PieceTransforms)
                {
                    float d = (kv.Value.Position - point).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = state; bestId = kv.Key; bestT = kv.Value; }
                }
            }
            if (best == null) { yield return "[ebm_why] no baked piece within 3 m of that point"; yield break; }
            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(bestId.PrefabHash) : null;
            var bake = best.Bake;
            yield return $"[ebm_why] nearest baked piece: '{(prefab != null ? prefab.name : bestId.PrefabHash.ToString())}' {Mathf.Sqrt(bestD):0.00} m away at {bestT.Position}, scale {bestT.Scale}, look {bestT.Look}, zone {best.Coord} (active {best.ZoneActive}, baked {best.Baked}, ready {bake.Ready}, far tier {bake.ShowingFarTier}, parent {(bake.Parent != null ? (bake.Parent.activeInHierarchy ? "active" : "INACTIVE") : "none")})";
            bool inGroup = false;
            foreach (var group in bake.InstanceGroups)
            {
                if (!group.Identities.Contains(bestId)) continue;
                inGroup = true;
                var def = group.Definition;
                int idx = group.Identities.IndexOf(bestId);
                yield return $"[ebm_why] drawn by an instanced group: {group.Matrices.Count} instances, look {group.Look}, alive {(def != null && def.IsAlive())}, submitted-in-view {InstancedDraw.Submit(group)}, bounds {group.WorldBounds}, its matrix {(idx >= 0 && idx < group.Matrices.Count ? group.Matrices[idx].GetColumn(3).ToString() : "?")}";
                yield return "[ebm_why] parts: " + (def != null ? InstanceDefinition.Describe(def.Parts) : "(no definition)");
            }
            bool contributor = bake.ContributorIdentities != null && bake.ContributorIdentities.Contains(bestId);
            yield return $"[ebm_why] in instanced group {inGroup}, combined-batch contributor {contributor}";
            var list = bake.ShowingFarTier ? bake.FarBatches : bake.Batches;
            int n = 0;
            foreach (var b in list)
            {
                if (b == null || b.Combined == null) continue;
                var r = b.Combined.GetComponent<MeshRenderer>();
                if (r == null || !r.bounds.Contains(point) && r.bounds.SqrDistance(point) > 4f) continue;
                var m = r.sharedMaterial;
                n++;
                yield return $"[ebm_why] batch '{b.Combined.name}' active {b.Combined.activeInHierarchy} enabled {r.enabled} mesh v{(b.Mesh != null ? b.Mesh.vertexCount : -1)} bounds {r.bounds} layer {LayerMask.LayerToName(b.Combined.layer)} mat '{(m != null ? m.name : "null")}' shader '{(m != null && m.shader != null ? m.shader.name : "null")}'{(m != null && m.shader != null && !m.shader.isSupported ? " UNSUPPORTED" : "")} queue {(m != null ? m.renderQueue : -1)} shadows {r.shadowCastingMode} forceOff {r.forceRenderingOff}";
            }
            yield return $"[ebm_why] {n} {(bake.ShowingFarTier ? "far" : "near")}-tier batch(es) cover that point (of {list.Count})";
        }

        // 1.2.102 (the lead 10-18): `ebm_rebake [all|here]` drops EBM's bakes (memory and disk) for every tracked zone, or the
        // player's zone, and the cached instance definitions; the zones' pieces draw themselves and bake again when they settle.
        internal static string Rebake(bool all)
        {
            var player = Player.m_localPlayer;
            if (!all && player == null) return "[ebm_rebake] no local player";
            long uid = MeshCacheStore.TryGetWorldUid();
            var here = player != null ? EbmZones.Of(player.transform.position) : default;
            int n = 0;
            float now = Time.unscaledTime;
            foreach (var state in _zones.Values)
            {
                if (!all && !state.Coord.Equals(here)) continue;
                if (uid != 0L) MeshCacheStore.Delete(uid, state.Coord);
                TearDown(state);
                state.LastChangeUnscaledTime = now;
                n++;
            }
            InstanceDefinitionCache.Clear();
            MeshCacheStore.ForgetPrefabPrints();
            if (all && uid != 0L) MeshCacheStore.DeleteWorld(uid);   // 1.2.255 D7: zones not tracked right now too, as this command's message says
            string line = $"[ebm_rebake] {(all ? "all" : "here")}: {n} zone bake(s) dropped from memory and disk, instance definitions cleared; zones bake again when they settle";
            EasyBakeLog.Debug(line);
            return line;
        }

        private static bool HasDeadGroup(MeshBaker.BakeResult bake) => HasDeadGroup(bake, -1);

        // aliveFrame >= 0: a definition shared by several groups is checked once that frame (1.2.213 IndirectCachedAlive)
        private static bool HasDeadGroup(MeshBaker.BakeResult bake, int aliveFrame)
        {
            for (int i = 0; i < bake.InstanceGroups.Count; i++)
            {
                var definition = bake.InstanceGroups[i].Definition;
                if (definition != null && !(aliveFrame >= 0 ? definition.IsAliveCached(aliveFrame) : definition.IsAlive())) return true;
            }
            return false;
        }

        private static void ReleaseDeadBakes()
        {
            float now = Time.unscaledTime;
            for (int i = 0; i < s_deadBakes.Count; i++)
            {
                var state = s_deadBakes[i];
                var bake = state.Bake;
                if (bake == null) continue;
                for (int g = 0; g < bake.InstanceGroups.Count; g++)
                {
                    var group = bake.InstanceGroups[g];
                    if (group.Definition != null && !group.Definition.IsAlive())
                        InstanceDefinitionCache.Forget(new InstanceKey(group.PrefabHash, group.Look));
                }
                EasyBakeLog.Debug($"[Instancing] zone {state.Coord}: a prefab's meshes were released by another mod; its pieces draw themselves again and the zone bakes again later.");
                TearDown(state);
                state.LastChangeUnscaledTime = now;
            }
            s_deadBakes.Clear();
        }

        // 1.2.184 (lead 11-07, Fire's stone_wall_2x1 at 1635,460 drew only its snow cap): the unload branch clears a zone's SuppressedPieces without
        // restoring them, on the assumption that ZNetScene has already destroyed those pieces. A suppression that lives on in no zone's list is a piece
        // whose renderers nothing will ever switch back on. Debug only, at most every 30 s, and only after a suppression was added or destroyed or an
        // unload cleared a list; nothing runs while Debug logging is off.
        private static int s_unloadClears, s_censusSeenChanges = -1, s_censusSeenClears = -1;
        private static float s_nextCensus;
        private static readonly HashSet<EasyBakeSuppressedVisuals> s_listed = new HashSet<EasyBakeSuppressedVisuals>();

        // 1.2.185 (lead 11-08, census on 0107r: "8 of 5059 silenced piece(s) are in no zone's list" - cauldron extensions and ashwood_bed in active
        // zone (26,7) that its bake does not draw, Beech1 in unloaded (23,6)): the census now heals what it finds, with Debug on or off. An orphan
        // the active zone's bake draws goes back on that bake's list (it stays silenced, drawn by the bake); every other orphan gets its renderers
        // back, and a piece in an active zone rejoins it the way a late piece does. One Info line the first time it heals anything, Debug after.
        private static readonly List<EasyBakeSuppressedVisuals> s_orphans = new List<EasyBakeSuppressedVisuals>();
        private static int s_unloadGivenBack, s_unloadReadopted;
        private static bool s_healSaid;

        // 1.2.185: the unload branch restores every silenced piece ZNetScene still holds before it drops the list (null-safe: a piece ZNetScene
        // already destroyed is Unity-null and skipped). Survivors are kept so the reload can silence them again where the bake still draws them.
        private static void GiveBackOnUnload(ZoneState state)
        {
            state.UnloadSurvivors.Clear();
            var list = state.Bake?.SuppressedPieces;
            if (list == null) return;
            if (GiveBackBudgetMs > 0f)
            {
                if (list.Count == 0) return;
                state.GiveBackQueue.AddRange(list);
                list.Clear();
                if (!state.GiveBackQueued) { state.GiveBackQueued = true; s_giveBackZones.Add(state); }
                return;
            }
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s == null) continue;
                s.RestoreVisuals();
                state.UnloadSurvivors.Add(s.gameObject);
                Object.DestroyImmediate(s);   // a reload this frame would otherwise find the doomed component (see HandBack, 1.2.168)
                s_unloadGivenBack++;
            }
            list.Clear();
        }

        // [Optimize] ZoneTransitionBudgetMs ([saddles] 11-09): a zone's unload still flips its state in the frame it happens, but giving each suppressed piece back (RestoreVisuals +
        // DestroyImmediate, thousands in a town zone) is worked off under this budget, at least one a frame. The whole transition is NOT deferred: a zone left active after it unloaded
        // would see its pieces vanish in the bake decision. A zone that loads again first finishes its own queue, so ReadoptSurvivors sees every survivor as before. 0 = in one frame.
        private static readonly List<ZoneState> s_giveBackZones = new List<ZoneState>();
        private static bool s_giveBackSaid;
        private static float GiveBackBudgetMs => FiresEasyBakeMeshesPlugin.ZoneTransitionBudgetMs != null ? FiresEasyBakeMeshesPlugin.ZoneTransitionBudgetMs.Value : 0f;

        private static void GiveBackLast(ZoneState state)
        {
            var q = state.GiveBackQueue;
            var s = q[q.Count - 1];
            q.RemoveAt(q.Count - 1);
            if (s == null) return;
            s.RestoreVisuals();
            state.UnloadSurvivors.Add(s.gameObject);
            Object.DestroyImmediate(s);
            s_unloadGivenBack++;
        }

        private static void FlushGiveBack(ZoneState state)
        {
            if (!state.GiveBackQueued) return;
            while (state.GiveBackQueue.Count > 0) GiveBackLast(state);
            state.GiveBackQueued = false;
            s_giveBackZones.Remove(state);
        }

        private static void PumpGiveBack()
        {
            if (s_giveBackZones.Count == 0) return;
            long start = Stopwatch.GetTimestamp(), budget = (long)(Mathf.Max(0.25f, GiveBackBudgetMs) * Stopwatch.Frequency / 1000f);
            int done = 0;
            while (s_giveBackZones.Count > 0)
            {
                var st = s_giveBackZones[s_giveBackZones.Count - 1];
                while (st.GiveBackQueue.Count > 0)
                {
                    if (done > 0 && Stopwatch.GetTimestamp() - start >= budget)
                    {
                        if (!s_giveBackSaid) { s_giveBackSaid = true; EasyBakeLog.Info($"[Zones] unload give-back spread over frames (ZoneTransitionBudgetMs {GiveBackBudgetMs:0.##} ms): first spread at zone ({st.Coord.x},{st.Coord.y}), {st.GiveBackQueue.Count} piece(s) left after this frame's {done}"); }
                        return;
                    }
                    GiveBackLast(st);
                    done++;
                }
                st.GiveBackQueued = false;
                s_giveBackZones.RemoveAt(s_giveBackZones.Count - 1);
            }
        }

        // 1.2.185: a zone that loads again re-adopts the pieces that lived through its unload. They were never destroyed, so OnInstanceCreated
        // never sees them: a piece goes through it now (cache hit -> silenced again, otherwise it joins like a late piece); a tree is silenced
        // again only when the bake still draws it.
        private static void ReadoptSurvivors(ZoneState state)
        {
            if (state.UnloadSurvivors.Count == 0) return;
            var scene = ZNetScene.instance;
            int back = 0;
            for (int i = 0; i < state.UnloadSurvivors.Count; i++)
            {
                var go = state.UnloadSurvivors[i];
                if (go == null || scene == null) continue;
                var view = go.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                if (zdo == null || scene.FindInstance(zdo) != view) continue;
                if (go.GetComponent<WearNTear>() != null) { OnInstanceCreated(go); back++; }
                else if (state.Baked && state.Bake?.ContributorIdentities != null
                         && state.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(go, go.transform.position)))
                { MeshBaker.DisableForCacheHit(go, state.Bake); back++; }
            }
            if (back > 0)
            {
                s_unloadReadopted += back;
                EasyBakeLog.Debug($"[Suppress] zone ({state.Coord.x},{state.Coord.y}) loaded again: {back} of {state.UnloadSurvivors.Count} piece(s) that lived through its unload re-adopted");
            }
            state.UnloadSurvivors.Clear();
        }

        private static void HealOrphans()
        {
            int adopted = 0, given = 0;
            var scene = ZNetScene.instance;
            for (int i = 0; i < s_orphans.Count; i++)
            {
                var s = s_orphans[i];
                if (s == null) continue;
                var go = s.gameObject;
                var view = go.GetComponent<ZNetView>();
                var zdo = view != null ? view.GetZDO() : null;
                bool held = zdo != null && scene != null && scene.FindInstance(zdo) == view;
                _zones.TryGetValue(EbmZones.Of(go.transform.position), out var st);
                bool active = held && st != null && st.ZoneActive;
                var wnt = go.GetComponent<WearNTear>();
                if (active && st.Baked && st.Bake?.ContributorIdentities != null
                    && st.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(go, go.transform.position)))
                {
                    st.Bake.SuppressedPieces.Add(s);
                    if (wnt != null) st.Pieces.Add(wnt); else st.Vegetation.Add(view);
                    adopted++;
                    continue;
                }
                s.RestoreVisuals();
                Object.DestroyImmediate(s);
                given++;
                if (active && wnt != null) OnInstanceCreated(go);
            }
            s_orphans.Clear();
            if (adopted + given == 0) return;
            string line = $"[Suppress] {given} piece(s) given back their renderers, {adopted} re-adopted by the zone bake that draws them (silenced but in no zone's list)";
            if (!s_healSaid) { s_healSaid = true; EasyBakeLog.Info("[EBM] " + line + "; later heals log at Debug"); }
            else EasyBakeLog.Debug(line);
        }

        // 1.2.257: allocations the create path no longer makes, per site, since the last line (Debug, every 30 s; counted where each one would have been made,
        // labelled estimates where a site makes more than one object). Nothing is written when nothing was avoided.
        private static float s_allocNext;
        private static long s_allocSeenNames, s_allocSeenSupp, s_allocSeenCover, s_allocSeenPacer, s_allocSeenItems;
        private static void CreateAllocsReport(float now)
        {
            if (now < s_allocNext) return;
            s_allocNext = now + 30f;
            long names = AllocsAvoidedNames - s_allocSeenNames, supp = EasyBakeSuppressedVisuals.AllocsAvoided - s_allocSeenSupp,
                 cover = Indirect.Coverage.CoveredObject.AllocsAvoided - s_allocSeenCover, pacer = StreamPacer.AllocsAvoided - s_allocSeenPacer,
                 items = RemoteItemZSyncSkip.AllocsAvoided - s_allocSeenItems;
            s_allocSeenNames = AllocsAvoidedNames; s_allocSeenSupp = EasyBakeSuppressedVisuals.AllocsAvoided; s_allocSeenCover = Indirect.Coverage.CoveredObject.AllocsAvoided;
            s_allocSeenPacer = StreamPacer.AllocsAvoided; s_allocSeenItems = RemoteItemZSyncSkip.AllocsAvoided;
            if (names + supp + cover + pacer + items == 0 || !EasyBakeLog.DebugOn()) return;
            EasyBakeLog.Debug($"[Allocs] last 30 s, create-path allocations avoided: {names + supp + cover + pacer + items} (prefab-name strings {names} est. 2 per read, bake suppression arrays {supp}, covered-object collections {cover} est. net of lazy creations, pacer stopwatches + wait lists {pacer}, dropped-item watch records {items})");
        }

        private static void OrphanSuppressionCensus(float now)
        {
            if (now < s_nextCensus) return;
            if (EasyBakeSuppressedVisuals.Changes == s_censusSeenChanges && s_unloadClears == s_censusSeenClears) return;
            s_nextCensus = now + 30f;
            s_censusSeenChanges = EasyBakeSuppressedVisuals.Changes;
            s_censusSeenClears = s_unloadClears;
            try
            {
                s_listed.Clear();
                foreach (var state in _zones.Values)
                {
                    var list = state.Bake?.SuppressedPieces;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++) if (list[i] != null) s_listed.Add(list[i]);
                }
                s_orphans.Clear();
                foreach (var z in s_giveBackZones) foreach (var q in z.GiveBackQueue) if (q != null) s_listed.Add(q);   // queued for give-back: not orphans
                foreach (var s in EasyBakeSuppressedVisuals.All) if (s != null && !s_listed.Contains(s)) s_orphans.Add(s);
                s_listed.Clear();
                if (EasyBakeLog.DebugOn()) SayCensus();
                if (s_orphans.Count > 0) HealOrphans();
            }
            catch (System.Exception ex) { s_orphans.Clear(); EasyBakeLog.Debug("[Suppress] orphan census failed: " + ex.Message); }
        }

        private static void SayCensus()
        {
            {
                int orphans = 0;
                var sb = new System.Text.StringBuilder();
                var scene = ZNetScene.instance;
                foreach (var s in s_orphans)
                {
                    if (s == null) continue;
                    orphans++;
                    if (orphans > 5) continue;
                    var view = s.GetComponent<ZNetView>();
                    var zdo = view != null ? view.GetZDO() : null;
                    bool held = zdo != null && scene != null && scene.FindInstance(zdo) == view;
                    Vector3 p = s.transform.position;
                    var key = EbmZones.Of(p);
                    string zone = _zones.TryGetValue(key, out var st)
                        ? $"EBM zone ({key.x},{key.y}) {(st.ZoneActive ? "active" : "marked unloaded")}, baked {st.Baked}, its bake {(st.Bake == null || zdo == null ? "-" : st.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(zdo)) ? "draws it" : "does not draw it")}"
                        : $"EBM zone ({key.x},{key.y}) not tracked";
                    sb.Append(orphans == 1 ? "" : "; ").Append(Utils.GetPrefabName(s.gameObject)).Append($" @{p.x:0.0},{p.y:0.0},{p.z:0.0}")
                      .Append(held ? " ZNetScene holds it" : zdo == null ? " no ZDO" : " NOT in ZNetScene").Append(", ").Append(zone);
                }
                string unloads = $"{s_unloadClears} unload clear(s) so far, {s_unloadGivenBack} piece(s) given back at unload, {s_unloadReadopted} re-adopted at reload";
                EasyBakeLog.Debug(orphans == 0
                    ? $"[Suppress] orphan census: 0 of {EasyBakeSuppressedVisuals.All.Count} silenced piece(s) outside every zone's list ({unloads})"
                    : $"[Suppress] orphan census: {orphans} of {EasyBakeSuppressedVisuals.All.Count} silenced piece(s) are in no zone's list; healing them now ({unloads}). First {Mathf.Min(orphans, 5)}: {sb}");
            }
        }

        // 1.2.208: zones whose stand-ins were removed / parked / unparked this frame; StandInsChanged(zone, stand-ins left) once each at the end of Update
        private static readonly HashSet<ZoneState> s_standInsTouched = new HashSet<ZoneState>();
        private static void NoteStandInsTouched(ZoneState state) { if (state != null) s_standInsTouched.Add(state); }
        private static void FlushStandInsTouched()
        {
            if (s_standInsTouched.Count == 0) return;
            foreach (var s in s_standInsTouched) EbmApi.RaiseStandInsChanged(new Vector2i(s.Coord.x, s.Coord.y), s.StandIns.Count);
            s_standInsTouched.Clear();
        }

        private static bool s_bakePassSpent, s_bakePassDeferred;   // [Optimize] BakePassBudgetMs: this pass did expensive work / it stopped early, so the next frame walks again
        private static float s_nextSlow;
        private static bool AnyZoneWorkQueued()
        {
            foreach (var s in _zones.Values)
                if (s.ZoneActive && (s.Dirty || s.AwaitingRecreate != null || s.StandInQueue.Count > 0 || s.ConvertQueue.Count > 0)) return true;
            return false;
        }

        public static void Update()
        {
            long pumpStart = InstancedDraw.Mark(InstancedDraw.Phase.Pumps);
            BakeFrameSplit.BeginFrame();
            MeshBaker.PumpSuppression();
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Classify);
            MeshBaker.PumpClassify();
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Other);
            ShellPieces.Tick();   // [Optimize] ShellDamageablePieces 30 s lines + support census
            SnowFeed.PollVedrRevision();   // 1.2.181: one int read a frame while Vedr is the snow source
            float now = Time.unscaledTime;
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Census);
            OrphanSuppressionCensus(now);
            CreateAllocsReport(now);
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Other);
            InstancedDraw.NotePhase(InstancedDraw.Phase.Pumps, pumpStart);
            float settleDelay = FiresEasyBakeMeshesPlugin.BatchingSettleDelaySeconds.Value;
            int minPerZone = FiresEasyBakeMeshesPlugin.BatchingMinPiecesPerZone.Value;

            var zoneSystem = ZoneSystem.instance;
            _dropScratch.Clear();

            bool skipping = SkipCreationActive();
            var localPlayer = Player.m_localPlayer;
            s_buildingNearby = skipping && localPlayer != null && localPlayer.InPlaceMode();
            long holdStart = InstancedDraw.Mark(InstancedDraw.Phase.BuildHold);
            if (s_buildingNearby) HoldForBuildTools(localPlayer.transform.position, now);
            else s_buildHoldScanned = false;
            InstancedDraw.NotePhase(InstancedDraw.Phase.BuildHold, holdStart);

            long walkStart = InstancedDraw.Mark(InstancedDraw.Phase.Zones);

            // Soft unload / reload pass. Zones that ZoneSystem stops tracking
            // get their combined-mesh parent hidden but kept in memory; their
            // disable lists are given back and dropped (1.2.185: pieces ZNetScene
            // still holds get their renderers back, the reload re-adopts them).
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
                BakeFrameSplit.Enter(BakeFrameSplit.Step.ZoneWalk, coord);
                bool zoneLoaded = EbmZones.Loaded(zoneSystem, coord);
                bool keptAlive = FiresEasyBakeMeshesPlugin.ZoneKeepaliveEnabled.Value
                    && ZoneKeepalive.IsKeptAlive(coord);

                if (!zoneLoaded && state.ZoneActive && !keptAlive)
                {
                    long ztUnload = EbmSelfCost.ZT();   // Debug step split (EbmSelfCost.Zs*)
                    if (state.Baked && state.Bake != null && state.Bake.Parent != null)
                        state.Bake.Parent.SetActive(false);
                    if (state.CacheStale && state.Baked && !state.Dirty) { long ztSave = EbmSelfCost.ZT(); SaveZone(state); EbmSelfCost.ZAcc(EbmSelfCost.ZsUnloadSave, ztSave); }
                    MeshBaker.ForgetPrep(state.Pieces);
                    state.Pieces.Clear();
                    BakeFrameSplit.Enter(BakeFrameSplit.Step.ZoneWalk);
                    GiveBackOnUnload(state);   // 1.2.185: restores before the list is dropped (was a bare Clear)
                    MeshBaker.DropPending(state.Bake, state.UnloadSurvivors);   // [generator] defect 5: pieces still queued for silencing are re-adopted at reload too, or the shown bake draws beside them
                    s_unloadClears++;   // 1.2.184: the census looks again after an unload drops a list
                    ReleaseSkipped(state, parkStandIns: true);
                    state.ZoneActive = false; Indirect.EbmIndirect.RequestFullWalk();
                    state.HiddenSinceUnscaledTime = now;
                    UnannounceZone(state);   // 1.2.206 EbmApi.ZoneUnloaded
                    EbmSelfCost.ZAcc(EbmSelfCost.ZsUnload, ztUnload);
                }
                else if (zoneLoaded && !state.ZoneActive)
                {
                    long ztReload = EbmSelfCost.ZT();
                    FlushGiveBack(state);
                    if (state.Baked && state.Bake != null && state.Bake.Parent != null)
                        state.Bake.Parent.SetActive(true);
                    long ztUnpark = EbmSelfCost.ZT(); UnparkStandIns(state); EbmSelfCost.ZAcc(EbmSelfCost.ZsUnpark, ztUnpark);
                    state.ZoneActive = true; Indirect.EbmIndirect.RequestFullWalk();
                    state.LastChangeUnscaledTime = now;
                    state.ConstructedUnscaledTime = now;
                    if (state.Baked) { long ztQueue = EbmSelfCost.ZT(); QueueStandIns(state); EbmSelfCost.ZAcc(EbmSelfCost.ZsQueueStandIns, ztQueue); }
                    if (state.Baked) AnnounceWhenReady(state);   // 1.2.206: back in range = announced again (its values went on unload)
                    ReadoptSurvivors(state);   // 1.2.185
                    EbmSelfCost.ZAcc(EbmSelfCost.ZsReload, ztReload);
                }

                if (state.ZoneActive && state.Baked) { long ztFar = EbmSelfCost.ZT(); UpdateFarTier(coord, state); EbmSelfCost.ZAcc(EbmSelfCost.ZsFarTier, ztFar); }
            }
            long ztGive = EbmSelfCost.ZT();
            PumpGiveBack();
            EbmSelfCost.ZAcc(EbmSelfCost.ZsGiveBack, ztGive, 0);
            BakeProofTick(now);   // [Optimize] BakeCoverageCensus / BakeVerifyCoverage / BakeFollowChanges (ZoneTrackerBakeProof.cs); three bool reads while all are off
            InstancedDraw.NotePhase(InstancedDraw.Phase.Zones, walkStart);

            // 1.2.207 (lead 11-08: zoneTrack 14.4 ms/s standing still): the checks and the bake decision run at 10 Hz while nothing is queued,
            // every frame while any zone has queued work (stand-ins, conversions, a hand-back, a dirty zone). Bakes already wait seconds to settle.
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Other);
            bool slowNow = now >= s_nextSlow || AnyZoneWorkQueued() || s_bakePassDeferred;   // s_bakePassDeferred: only ever set while [Optimize] BakePassBudgetMs is above 0
            if (slowNow) s_nextSlow = now + 0.1f;
            long checkStart = InstancedDraw.Mark(InstancedDraw.Phase.Checks);
            if (slowNow) FinishHandBacks(now);
            if (skipping && slowNow)
            {
                if (now >= s_nextRangeCheck)
                {
                    s_nextRangeCheck = now + 0.25f;
                    ConstructCachedZonesInRange(zoneSystem);
                }
                InstancedDraw.NotePhase(InstancedDraw.Phase.Checks, checkStart);

                long standInStart = InstancedDraw.Mark(InstancedDraw.Phase.StandIns);
                BuildStandIns();
                InstancedDraw.NotePhase(InstancedDraw.Phase.StandIns, standInStart);

                long unloadStart = InstancedDraw.Mark(InstancedDraw.Phase.Unloading);
                ConvertLivePieces();
                InstancedDraw.NotePhase(InstancedDraw.Phase.Unloading, unloadStart);

                checkStart = InstancedDraw.Mark(InstancedDraw.Phase.Checks);
                WatchSkipped(now);
                ReportSkipping(now);
            }
            InstancedDraw.NotePhase(InstancedDraw.Phase.Checks, checkStart);

            long bakeStart = InstancedDraw.Mark(InstancedDraw.Phase.Baking);
            float passBudgetMs = FiresEasyBakeMeshesPlugin.BakePassBudgetMs != null ? FiresEasyBakeMeshesPlugin.BakePassBudgetMs.Value * PerformanceProfile.Budget : 0f; long passBudget = (long)(passBudgetMs * Stopwatch.Frequency / 1000f), passStart = Stopwatch.GetTimestamp(); s_bakePassSpent = false; if (slowNow) s_bakePassDeferred = false;
            if (slowNow)   // 1.2.207: the whole bake-decision walk (see slowNow)

            // Now the per-frame bake decision. Only active zones with enough
            // settled invulnerable pieces qualify. A zone that's Baked AND not
            // Dirty is in steady state — skip; the combined mesh keeps drawing.
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive) continue;
                BakeFrameSplit.Enter(BakeFrameSplit.Step.Walk, state.Coord);
                if (passBudget > 0L && s_bakePassSpent && Stopwatch.GetTimestamp() - passStart >= passBudget) { s_bakePassDeferred = true; BakeFrameSplit.NotePassStop(passBudgetMs, state.Coord); break; }   // every expensive step changes its zone's state, so a stopped walk moves on next frame
                // Skipped pieces handed back to vanilla are still being created; a bake now would miss them.
                if (state.AwaitingRecreate != null) continue;

                // One-shot stale-cache reconciliation for reattached zones: any
                // cached contributor with no live piece was removed while this
                // client was offline (an admin, another player) and is
                // ghost-rendering out of the stale combined mesh — rebake now.
                // 1.2.113 (HOWTO limits fix 10-20): matched piece by piece (a count let unrelated skipped pieces
                // cover a missing one), and checked BEFORE the bake threshold, so a build cleared below
                // minPerZone goes Dirty and the teardown below drops its mesh and cache file.
                // 1.2.193: while FGN's signal is live, a zone still filling is neither judged nor baked (it is not all here yet)
                bool fgn = FgnLink.Active;
                if (fgn && !state.FgnSettled && now - state.FgnFillingAt < FgnFillingCeiling) continue;
                // 1.2.253 [Cache] RemoteZoneValidation: delivered-and-near decides, not quiet time. Waiting falls through (not continue): a far zone waits indefinitely and must still reach the save below.
                bool sectorRule = SectorArrivals.RuleActive;
                if (sectorRule && !s_saidSectorRule) { s_saidSectorRule = true; EasyBakeLog.Info($"[Cache] RemoteZoneValidation on: cached zones are judged only {CacheZdoQuietSeconds:0} s after their sector's last new ZDO and with the player within one zone"); }
                bool sectorWait = sectorRule && state.Baked && !state.Dirty && state.NeedsCacheValidation && !SectorJudgeable(state, now);
                if (sectorWait) s_sectorWaits++;
                if (state.Baked && !state.Dirty && state.NeedsCacheValidation && !sectorWait)
                {
                    // A busy server can deliver a zone's pieces in waves; judging too early rebakes half a base.
                    // With FGN's signal, "settled" is that answer; without it, EBM's own quiet windows.
                    if (!sectorRule && !fgn && now - state.LastChangeUnscaledTime < CacheValidationQuietSeconds) continue;
                    BakeFrameSplit.Enter(BakeFrameSplit.Step.CacheCheck);
                    var ids = state.Bake != null ? state.Bake.ContributorIdentities : null;
                    // 1.2.191: judged by the zone's ZDOs (what the server says exists), never by live objects EBM chose not to create
                    var zdoIds = LiveIdentities(state);
                    // 1.2.255: this wait stands. A zone with nothing here is judged by the empty-zone pass below ([Cache] EmptyZoneDropsCache), which can tell demolished from not delivered yet.
                    if (ids != null && ids.Count > 0 && zdoIds.Count == 0) { s_validationWaits++; continue; }
                    // 1.2.194 (Fire's 0108k city on 1.2.193: 25 cached zones rebaked, "(-16,10) 10078 contributor(s), 9249 ZDO(s) held, 1338 missing
                    // -> rebake (same contributors still missing)", then 27,605 pieces "arrived after their zone baked": the server was still sending
                    // them). A cached zone is judged only once it is really complete: FGN settled it (never on the filling ceiling) AND its ZDO count
                    // has not moved for 30 s (10 s without FGN). Until then its bake draws as cached; a removed piece draws a little longer, nothing worse.
                    if (fgn && !state.FgnSettled) { s_validationWaits++; continue; }
                    if (!sectorRule && now - state.LiveCountChangedAt < (fgn ? CacheZdoQuietSeconds : CacheValidationQuietSeconds)) { s_validationWaits++; continue; }
                    // 1.2.193 (Fire's BlueHills arrival: "(-19,12) 5,240 contributor(s), 5,110 ZDO(s) held, 595 missing -> rebake"; the zone
                    // was not all delivered): a check that finds missing contributors does not rebake at once. It looks again at the next
                    // settle (FGN) or 30 s later, and rebakes only if the SAME contributors are still missing: a removed piece stays gone,
                    // an undelivered one arrives. A rebake throws the cache away and makes every piece of the zone a live object.
                    if (state.PendingMissing != null && now < state.RecheckAt && !(fgn && state.SettledSinceCheck)) continue;
                    if (ids != null && ids.Count > 0)
                    {
                        s_bakePassSpent = true;
                        HashSet<MeshBaker.PieceIdentity> live;
                        if (MeshCacheStore.LowGarbageOn)   // 1.2.255: one reused set, dropped when a very large zone has grown it
                        {
                            if (s_liveCheckSet.Count > 32768) s_liveCheckSet = new HashSet<MeshBaker.PieceIdentity>();
                            s_liveCheckSet.Clear();
                            s_liveCheckSet.UnionWith(zdoIds);
                            live = s_liveCheckSet;
                        }
                        else live = new HashSet<MeshBaker.PieceIdentity>(zdoIds);
                        foreach (var skipped in state.Skipped.Values) live.Add(skipped.Identity);
                        foreach (var piece in state.Pieces)
                        {
                            if (piece == null) continue;
                            live.Add(MeshBaker.PieceIdentity.From(piece.gameObject, piece.transform.position));
                        }
                        // 1.2.168: trees are contributors too; a real one is not a removed one
                        foreach (var tree in state.Vegetation)
                            if (tree != null) live.Add(MeshBaker.PieceIdentity.From(tree.gameObject, tree.transform.position));
                        var missingNow = new HashSet<MeshBaker.PieceIdentity>();
                        var firstMissing = new System.Text.StringBuilder();
                        foreach (var id in ids) if (!live.Contains(id)) { if (missingNow.Count < 6) firstMissing.Append($" {id.PrefabHash}@{id.X},{id.Y},{id.Z}"); missingNow.Add(id); }
                        state.SettledSinceCheck = false;
                        if (missingNow.Count == 0)
                        {
                            state.NeedsCacheValidation = false;
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) cache check vs ZDOs: {ids.Count} contributor(s), {zdoIds.Count} ZDO(s) held, 0 missing -> cache kept{(state.PendingMissing != null ? $" (the {state.PendingMissing.Count} missing last time were delivered)" : "")}");
                            state.PendingMissing = null;
                            continue;
                        }
                        if (state.PendingMissing == null)
                        {
                            state.PendingMissing = missingNow; state.RecheckAt = now + CacheRecheckSeconds; s_cacheRechecks++;
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) cache check: {missingNow.Count} missing of {ids.Count} ({zdoIds.Count} ZDO(s) held) -> recheck at settle / in {CacheRecheckSeconds:0} s before any rebake");
                            continue;
                        }
                        int still = 0; foreach (var id in missingNow) if (state.PendingMissing.Contains(id)) still++;
                        if (still == 0)
                        {
                            // different pieces missing this time: still arriving; look again later
                            state.PendingMissing = missingNow; state.RecheckAt = now + CacheRecheckSeconds; s_cacheRechecks++;
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) cache check: {missingNow.Count} missing, none of them the same as last time -> recheck again");
                            continue;
                        }
                        state.NeedsCacheValidation = false;
                        // 1.2.253: same world UID, different content (a restored backup, an edited server): a zone this far from its cache rebakes once rather than trimming piece by piece. The Dirty path below holds its real pieces and bakes.
                        if (sectorRule && still >= StaleCacheMinMissing && still >= ids.Count * StaleCacheShare)
                        {
                            state.PendingMissing = null;
                            state.Dirty = true;
                            state.LastChangeUnscaledTime = now;
                            s_staleCacheZones++;
                            if (FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value)
                            {
                                long uidStale = MeshCacheStore.TryGetWorldUid();
                                if (uidStale != 0L) MeshCacheStore.Delete(uidStale, state.Coord);
                            }
                            if (!s_saidStaleCache) { s_saidStaleCache = true; EasyBakeLog.Info("[Cache] a cached zone no longer matches this world's content (restored or edited?): zones like it discard their cache and bake fresh once"); }
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) cache stale: {still} of {ids.Count} contributor(s) missing at both looks ({zdoIds.Count} ZDO(s) held) -> cache discarded, baking fresh");
                            continue;
                        }
                        // 1.2.194: the pieces missing at both looks are gone. An instanced one leaves its group the way a destroyed piece does
                        // (OnZdoDestroyed): no rebake, nothing made live. Only a piece merged into a combined mesh still needs the rebake.
                        BakeFrameSplit.Enter(BakeFrameSplit.Step.Trim);
                        int dropped = 0, merged = 0;
                        foreach (var id in missingNow)
                        {
                            if (!state.PendingMissing.Contains(id)) continue;   // missing only this time: still arriving
                            bool left = false;
                            foreach (var g in state.Bake.InstanceGroups)
                                if (g.PrefabHash == id.PrefabHash && g.TryRemove(id))
                                {
                                    left = true;
                                    // 1.2.238 ([ghost]: the zone that differs in broken runs): what left, and Sync logs the slots after it
                                    g.DropNote = "cache check";   // 1.2.253: no per-piece string or line; the zone's summary below counts them
                                    while (g.TryRemove(id)) { }   // [generator] defect 2: the identity is gone from the ZDO set, so every stacked copy is; a second matrix left here stayed drawn and saved
                                }
                            if (!left) { merged++; continue; }
                            ids.Remove(id); state.Bake.PieceTransforms?.Remove(id); DropStandIn(state, id);
                            dropped++;
                        }
                        state.PendingMissing = null;
                        s_missingDropped += dropped;
                        if (dropped > 0) state.CacheStale = true;
                        EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) cache check vs ZDOs: {ids.Count} contributor(s), {zdoIds.Count} ZDO(s) held, {missingNow.Count} missing (same as last look: {still}); {dropped} instanced dropped without rebake, {merged} in the combined mesh{(merged > 0 ? " -> rebake" : " -> cache kept, trimmed")}");
                        if (merged > 0)
                        {
                            s_missingRebakes++;
                            int missing = merged;
                            state.Dirty = true;
                            state.LastChangeUnscaledTime = now;
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): cached mesh has "
                                + $"{missing} piece(s) removed since it was saved — rebaking to drop them.");
                            EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) why: missing{firstMissing}; {ids.Count} contributors, {state.PresentPieces} present, {state.Skipped.Count} skipped, {state.Pieces.Count} real, built from cache {CacheConstructs(state.Coord)} time(s) this session");
                        }
                    }
                    else state.NeedsCacheValidation = false;
                }
                // A dirty zone that dropped below the bake threshold can never
                // rebake — tear it down (restores the remaining live renderers,
                // drops the removed pieces' ghost geometry) and delete its cache
                // file so next session doesn't resurrect the stale mesh.
                BakeFrameSplit.Enter(BakeFrameSplit.Step.Walk);
                if (state.Baked && state.Dirty && state.PresentPieces < minPerZone)
                {
                    if (now - state.LastChangeUnscaledTime < settleDelay) continue;
                    if (state.Skipped.Count > 0) { HoldRealPieces(state, 0f); continue; }
                    s_bakePassSpent = true; BakeFrameSplit.Enter(BakeFrameSplit.Step.Teardown);
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
                if ((!state.Baked || state.Dirty) && VariationSwapPending(state, now)) continue;

                if (state.Baked && !state.Dirty)
                {
                    if (state.CacheStale && (FiresEasyBakeMeshesPlugin.SaveOffThread == null || !FiresEasyBakeMeshesPlugin.SaveOffThread.Value || now - state.LastSaveAt >= SaveMinSeconds)) SaveZone(state);   // 1.2.253: throttled only with the key on; unload saves always
                    continue;
                }
                if (state.Skipped.Count > 0) { HoldRealPieces(state, 0f); continue; }

                // 1.2.83 ([fgn] BlueHills: "classified ahead 0, at bake 513" - the shared queue had not reached this zone): a
                // zone due to bake first puts its pieces at the front of the classify queue, and the bake waits for them under
                // the same frame budget, at most ClassifyWaitSeconds, so the classification isn't done inside the bake frame.
                if (FiresEasyBakeMeshesPlugin.ClassifyBudgetMs.Value > 0f)
                {
                    if (state.ClassifyAskedAt < 0f || state.LastChangeUnscaledTime > state.ClassifyAskedAt)   // new pieces since: ask again
                    {
                        state.ClassifyAskedAt = now;
                        MeshBaker.ForgetPrep(state.Pieces);   // 1.2.84: a prep from before the new pieces is stale
                        if (MeshBaker.ClassifyFirst(state.Pieces) > 0) continue;
                    }
                    if (MeshBaker.UrgentClassifyPending && now - state.ClassifyAskedAt < ClassifyWaitSeconds) continue;
                    // 1.2.84: then its instance groups are built ahead under the same budget, inside the same wait.
                    // 1.2.85 ([fgn] round 112023: six zones baked with "not finished (0/N walked)"): the prep's own wait counts from
                    // when it starts, not from the classify ask, so a prep that starts late still gets its time.
                    if (MeshBaker.PrepareGroups(state.Pieces)) state.PrepAskedAt = now;
                    if (!MeshBaker.GroupsReady(state.Pieces) && now - state.PrepAskedAt < ClassifyWaitSeconds) continue;
                }
                state.ClassifyAskedAt = -1f;

                if (state.Baked && state.Dirty) { s_bakePassSpent = true; BakeFrameSplit.Enter(BakeFrameSplit.Step.Teardown); TearDown(state); }
                s_bakePassSpent = true; BakeFrameSplit.Enter(BakeFrameSplit.Step.Bake);
                long tBake = Probe.Start();
                state.Bake = MeshBaker.Bake(state.Coord, state.Pieces, state.Vegetation);
                try { ZdoBakeVerifier.Verify(state.Coord, state.Bake); } catch { }
                Probe.Stop("EasyBake:bake", tBake);
                state.Baked = true;
                state.FromCache = false; Indirect.EbmIndirect.RequestFullWalk();
                AnnounceWhenReady(state);   // 1.2.206 EbmApi.ZoneBaked
                s_zonesBakedFresh++;
                state.Dirty = false;
                // Only pin via keepalive if the fresh bake produced batches.
                // Zones with sub-threshold materials or all-PropBlock /
                // all-non-LOD0 renderers bake to 0 batches and don't benefit
                // from being kept alive across zone-cross.
                if (state.Bake != null && state.Bake.HasRenderableContent)
                    ZoneKeepalive.MarkActive(state.Coord);
                BakeFrameSplit.Enter(BakeFrameSplit.Step.AfterBake);
                AfterFreshBake(state, now);
                EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}) baked fresh, from {state.Pieces.Count} real piece(s)");

                // Persist the freshly-baked result so the next session can
                // skip CombineMeshes entirely. Save is fail-soft (logs + moves
                // on) — disk I/O issues mustn't break gameplay. A rebake that
                // produced 0 batches must DELETE the old file instead (Save
                // early-outs on empty bakes and would leave the stale mesh to
                // resurrect ghost geometry next session).
                SaveZone(state);
            }

            InstancedDraw.NotePhase(InstancedDraw.Phase.Baking, bakeStart);
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Tail);
            long ztTail = EbmSelfCost.ZT();
            FlushStandInsTouched();   // 1.2.208
            EbmSelfCost.ZAcc(EbmSelfCost.ZsFlushStandIns, ztTail, 0);
            ztTail = EbmSelfCost.ZT();

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
                    bool demolished = EmptyZoneDropsCacheOn && Demolished(state, now);   // before the teardown, which forgets the bake's contributors
                    TearDown(state);
                    _dropScratch.Add(kv.Key);
                    if (demolished) DropDemolishedCache(state.Coord);
                }
            }
            for (int i = 0; i < _dropScratch.Count; i++) _zones.Remove(_dropScratch[i]);
            EbmSelfCost.ZAcc(EbmSelfCost.ZsEmptyTeardown, ztTail, _dropScratch.Count);
            ztTail = EbmSelfCost.ZT();
            // 1.2.206: zones whose instance groups gained / lost a piece since their EbmApi.ZoneBaked, at most once a second each
            BakeFrameSplit.Enter(BakeFrameSplit.Step.Announce);
            var due = EbmSnowStore.TakeDue(Time.unscaledTime);
            EbmSnowStore.PumpHolds(Time.unscaledTime);   // 1.2.260: zones whose wait for per-piece snow ended re-split
            for (int i = 0; i < due.Count; i++) if (_zones.TryGetValue(due[i], out var dz) && dz.Baked) AnnounceWhenReady(dz);
            EbmSelfCost.ZAcc(EbmSelfCost.ZsReannounce, ztTail, due.Count);
            BakeFrameSplit.Enter(BakeFrameSplit.Step.HiddenRelease);
            ztTail = EbmSelfCost.ZT();
            ReleaseOldHidden(now);
            EbmSelfCost.ZAcc(EbmSelfCost.ZsHiddenRelease, ztTail, 0);
            BakeFrameSplit.EndFrame();
        }

        // 1.2.94 (Fire 10-13 RAM audit): a soft-unloaded zone kept its baked meshes until logout, so EBM grew with
        // every zone visited and every teleport. A hidden zone is now released after HiddenReleaseSeconds, and beyond
        // HiddenKeepZones the oldest hidden go first. A released zone that comes back is built again like a first
        // visit (from the preloaded cache when it has one), so only a long-gone zone pays that.
        private const float HiddenReleaseSeconds = 600f;
        private const int HiddenKeepZones = 32;
        private static readonly List<ZoneState> s_hiddenScratch = new List<ZoneState>();
        private static float s_nextHiddenRelease;

        private static void ReleaseOldHidden(float now)
        {
            if (now < s_nextHiddenRelease) return;
            s_nextHiddenRelease = now + 5f;
            s_hiddenScratch.Clear();
            foreach (var state in _zones.Values)
                if (!state.ZoneActive) s_hiddenScratch.Add(state);
            if (s_hiddenScratch.Count == 0) return;
            s_hiddenScratch.Sort((a, b) => a.HiddenSinceUnscaledTime.CompareTo(b.HiddenSinceUnscaledTime));
            int released = 0;
            for (int i = 0; i < s_hiddenScratch.Count; i++)
            {
                var state = s_hiddenScratch[i];
                bool tooMany = s_hiddenScratch.Count - i > HiddenKeepZones;
                if (!tooMany && now - state.HiddenSinceUnscaledTime < HiddenReleaseSeconds) break;
                EbmTrace.Step("hidden release", 0, "zone " + state.Coord.x + "," + state.Coord.y);
                TearDown(state);
                _zones.Remove(state.Coord);
                released++;
            }
            if (released > 0)
                EasyBakeLog.Debug($"[Memory] released {released} hidden zone bake(s) (hidden > {HiddenReleaseSeconds:0} s or beyond {HiddenKeepZones} kept); {s_hiddenScratch.Count - released} hidden kept");
            s_hiddenScratch.Clear();
        }

        // 1.2.255 [Cache] EmptyZoneDropsCache. The teardown above used to leave the file and preload entry, so the range pass rebuilt the zone every ~10 s: a ghost base.
        // LiveIdentities holds EVERY ZDO of the sector, so this tells the cases apart:
        //  - any contributor still has a ZDO: not demolished;
        //  - a server, host or single player holds every ZDO: none left means gone;
        //  - a client must have had this sector delivered (at least one ZDO of any kind here), FGN must not call it still filling, the player must be within one zone,
        //    and no new ZDO may have arrived for 30 s (SectorArrivals). A sector with NO ZDO at all on a client looks the same as one not delivered, so it is never treated as demolished.
        // The zone is torn down and rebuilt about every 10 s while it waits, so the time it has looked empty is kept here and not on its ZoneState.
        private struct EmptySeen { public float First, Last; }
        private const float EmptySeenGapSeconds = 30f;
        private const int MaxEmptySeen = 1024;
        private static readonly Dictionary<Vector2s, EmptySeen> s_emptySeen = new Dictionary<Vector2s, EmptySeen>();
        private static int s_emptyZoneDrops;
        private static bool s_saidEmptyDrop;
        private static bool EmptyZoneDropsCacheOn => FiresEasyBakeMeshesPlugin.CacheEmptyZoneDropsCache != null && FiresEasyBakeMeshesPlugin.CacheEmptyZoneDropsCache.Value && FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value;

        private static bool Demolished(ZoneState state, float now)
        {
            var net = ZNet.instance;
            var ids = state.Bake != null ? state.Bake.ContributorIdentities : null;
            if (net == null || ids == null || ids.Count == 0) return false;
            var zdoIds = LiveIdentities(state);
            foreach (var id in ids)
                if (zdoIds.Contains(id)) { s_emptySeen.Remove(state.Coord); return false; }
            bool holdsEveryZdo = net.IsServer();
            if (!holdsEveryZdo)
            {
                if (zdoIds.Count == 0) return false;
                if (FgnLink.Active && !state.FgnSettled) return false;
                var delta = EbmZones.Delta(EbmZones.Of(net.GetReferencePosition()), state.Coord);
                if (Mathf.Abs(delta.x) > 1 || Mathf.Abs(delta.y) > 1) return false;
            }
            if (!s_emptySeen.TryGetValue(state.Coord, out var seen) || now - seen.Last > EmptySeenGapSeconds) seen = new EmptySeen { First = now };
            seen.Last = now;
            if (s_emptySeen.Count >= MaxEmptySeen && !s_emptySeen.ContainsKey(state.Coord)) s_emptySeen.Clear();
            s_emptySeen[state.Coord] = seen;
            float since = holdsEveryZdo ? seen.First : Mathf.Max(seen.First, SectorArrivals.LastArrival(state.Coord));
            return now - since >= CacheZdoQuietSeconds;
        }

        private static void DropDemolishedCache(Vector2s coord)
        {
            s_emptySeen.Remove(coord);
            long uid = MeshCacheStore.TryGetWorldUid();
            if (uid == 0L) return;
            MeshCacheStore.Delete(uid, coord);
            s_emptyZoneDrops++;
            if (!s_saidEmptyDrop) { s_saidEmptyDrop = true; EasyBakeLog.Info("[Cache] EmptyZoneDropsCache: a fully demolished zone's saved bake was removed, so it cannot come back"); }
            EasyBakeLog.Debug($"[Cache] Zone ({coord.x},{coord.y}): every piece it was baked from is gone; saved bake removed");
        }

        private static void SaveZone(ZoneState state)
        {
            state.CacheStale = false;
            s_bakePassSpent = true;
            state.LastSaveAt = Time.unscaledTime;
            if (!FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value) return;
            long uid = MeshCacheStore.TryGetWorldUid();
            if (uid == 0L) return;
            if (state.Bake != null && state.Bake.HasRenderableContent)
            {
                // 1.2.255 D8: one retry, so a locked file or a full disk can never mean a save attempt every frame
                if (MeshCacheStore.Save(uid, state.Coord, state.Bake)) state.SaveRetried = false;
                else if (!state.SaveRetried) { state.SaveRetried = true; state.CacheStale = true; }
            }
            else
                MeshCacheStore.Delete(uid, state.Coord);
        }

        // ── 1.2.206 EbmApi.ZoneBaked / ZoneUnloaded (EbmSnowStore). Once per bake becoming Ready, or a throttled re-announce; never per frame. ──
        private static void AnnounceWhenReady(ZoneState state)
        {
            var bake = state.Bake;
            if (bake == null || !state.ZoneActive) return;
            if (bake.Ready) { AnnounceZone(state, bake); return; }
            bake.OnReady = b => { if (state.Bake == b && state.ZoneActive) AnnounceZone(state, b); };   // MeshBaker calls it once, when the suppression queue ends
        }

        private static void AnnounceZone(ZoneState state, MeshBaker.BakeResult bake)
        {
            if (bake.InstanceGroups == null || bake.InstanceGroups.Count == 0) { UnannounceZone(state); return; }   // nothing EBM can take values for
            LiveIdentities(state);   // the zone's ZDO map, reused when its sector count hasn't moved
            var ids = EbmSnowStore.Announce(state.Coord, bake, state.LiveZdos);
            EbmApi.RaiseZoneBaked(state.Coord, ids);
        }

        private static void UnannounceZone(ZoneState state)
        {
            if (EbmSnowStore.Unload(state.Coord)) EbmApi.RaiseZoneUnloaded(state.Coord);
        }

        private static void TearDown(ZoneState state)
        {
            UnannounceZone(state);   // 1.2.206
            ReleaseSkipped(state);
            if (state.Bake != null)
            {
                MeshBaker.Restore(state.Bake);
                state.Bake = null;
            }
            state.Baked = false; Indirect.EbmIndirect.RequestFullWalk();
            state.Dirty = false;
        }

        public static void Reset()
        {
            Indirect.EbmIndirect.Shutdown();   // 1.2.203: the indirect buffers + clones go with the world
            s_giveBackZones.Clear();
            foreach (var s in _zones.Values) TearDown(s);
            _zones.Clear();
            EbmSnowStore.Clear();   // 1.2.206: nothing outlives the world
            _transparentByName.Clear(); s_transparentByHash.Clear();
            InvulnerableClassifier.Reset();
            ZoneKeepalive.Reset();
            DestroyTimeSlicer.Reset();
            SectorInstanceMirror.Reset();
            StaticPieceZSyncSkip.Reset();
            SectorArrivals.Clear();   // 1.2.253
            s_emptySeen.Clear(); s_emptyZoneDrops = 0; s_notInBakeKeptReal = 0; s_notInBakeMissing = 0;   // 1.2.255
            s_joinedFromZdo = 0; System.Array.Clear(s_lateJoinFallback, 0, s_lateJoinFallback.Length); s_sectorWaits = 0; s_staleCacheZones = 0;
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
            s_handedBackForBuilding = 0; s_handedBackForDamage = 0;
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
            if (FiresEasyBakeMeshesPlugin.SkipBakedPieceObjects == null
                || !FiresEasyBakeMeshesPlugin.SkipBakedPieceObjects.Value
                || !FiresEasyBakeMeshesPlugin.BatchingActive()
                || ZNet.instance == null) return false;

            // ── SINGLE PLAYER IS NOT ARCHITECTURALLY DIFFERENT, IT IS THE SAME PROCESS ───────────────────
            // A joined client skips; a host or single-player world did not, and the stated reason - "server" -
            // was never the real one. EBM draws and decides entirely client-side; what differs on a host is
            // only that the SAME process also owns the ZDOs, so vanilla's destroy-on-null error path is live
            // in the call we intercept. ConsumeJustSkipped + the DestroyZDO prefix close that, so the gate
            // becomes a question about SIMULATION, not about being a server.
            //
            // The remaining honest unknown is wear/support. On a dedicated server the SERVER owns WearNTear
            // and still holds every object; on a host the host owns it and would be missing the skipped ones.
            // Only invulnerable pieces are skipped by default, which do not take damage, but whether they are
            // load-bearing for their neighbours is UNTESTED. So hosts are opt-in until somebody measures it on
            // a throwaway world - not off because it cannot work.
            if (!ZNet.instance.IsServer()) return true;
            return FiresEasyBakeMeshesPlugin.SkipOnHost != null && FiresEasyBakeMeshesPlugin.SkipOnHost.Value;
        }

        // ═══ ONE SKIP DECISION, ASKED TWO WAYS ═══════════════════════════════════════════════════════════
        // TrySkipCreation is the only thing that decides whether a piece stays uncreated, and the candidate
        // census has to report that same decision. It used to ask its own cheaper question instead - "is this
        // prefab CLASS bake-eligible?" - and that answered something else entirely. Prefab eligibility is the
        // FIRST of eight tests, so every piece that passed it and failed a later one was filed as
        // "bake-skippable" when the truth was the opposite: TrySkipCreation had already looked at that piece
        // and declined it. That is precisely WHY it was still in the queue - a real skip sets zdo.Created, so
        // a skipped piece is never a candidate again. The bucket that claimed "EBM is holding these back" was
        // listing pieces EBM had waved through, and it was taking them out of the only bucket that mattered:
        // creatable, waiting on nothing but budget.
        //
        // So the decision lives here once and both callers ask it. WouldSkip touches nothing - no zone is
        // constructed, no defer timer starts, no counter moves - which is what makes it safe to call tens of
        // thousands of times from a census, and why TrySkipCreation keeps every side effect on its own side of
        // the call instead of passing a mode flag down.
        //
        // Declared in evaluation order. Prefab-hash and prefab-absent are deliberately NOT here: SkipEligibility
        // already answers both as PrefabNotSkippable, and re-asking ZNetScene would put a second dictionary
        // lookup on the CreateObject path to sharpen a label only the census reads. The census resolves those
        // two itself, before it asks.
        // 1.2.174: Debug tally for the leg compare: why pieces were or were not skipped at creation, how often a zone came back from its cache.
        private static readonly int[] s_skipTally = new int[(int)SkipReason.ZdoChanged + 1];
        private static readonly Dictionary<Vector2s, int> s_cacheConstructs = new Dictionary<Vector2s, int>();
        private static float s_nextTallyLog;

        private static void NoteSkipReason(SkipReason reason)
        {
            s_skipTally[(int)reason]++;
            if (Time.unscaledTime < s_nextTallyLog) return;
            s_nextTallyLog = Time.unscaledTime + 60f;
            var sb = new System.Text.StringBuilder("[Skip] reasons so far:");
            for (int i = 0; i < s_skipTally.Length; i++) if (s_skipTally[i] > 0) sb.Append($" {(SkipReason)i} {s_skipTally[i]}");
            string winter = SkipEligibility.WinterKeepSummary();   // 1.2.183: distinct pieces the winter rule holds real
            if (winter != null) sb.Append("; ").Append(winter);
            EasyBakeLog.Debug(sb.ToString());
        }

        private static void NoteCacheConstruct(Vector2s coord) { s_cacheConstructs.TryGetValue(coord, out int n); s_cacheConstructs[coord] = n + 1; }
        private static int CacheConstructs(Vector2s coord) { s_cacheConstructs.TryGetValue(coord, out int n); return n; }

        internal enum SkipReason
        {
            /// <summary>No reason: the bake draws this piece and a stand-in stands in for it.</summary>
            WouldBeSkipped = 0,
            NoZdo,
            /// <summary>Skipping is off this session: config, batching, or a host that has not opted in.</summary>
            SkipInactive,
            /// <summary>The prefab does something a combined mesh and a collider cannot stand in for.</summary>
            PrefabNotSkippable,
            /// <summary>A build tool is out within reach, so the piece is deliberately kept real.</summary>
            BuildHold,
            /// <summary>EBM knows the zone and its bake has not landed yet.</summary>
            ZoneBakePending,
            /// <summary>No zone state and no cached bake to build one from - normal on a first visit.</summary>
            ZoneUnknown,
            /// <summary>The zone is handing pieces back and the real ones do not exist yet.</summary>
            AwaitingRecreate,
            /// <summary>The zone is holding its real pieces: a rebake, a teardown, or a tool nearby.</summary>
            ZoneHoldingReal,
            /// <summary>The bake landed and does not contain this piece.</summary>
            NotInBake,
            /// <summary>The piece no longer matches what was baked - damaged, re-looked, rotated or rescaled.</summary>
            ZdoChanged,
        }

        /// <summary>
        /// The skip decision with no side effects. True means the bake draws this piece; on false
        /// <paramref name="reason"/> is the stage that said no.
        /// </summary>
        internal static bool WouldSkip(ZDO zdo, out SkipReason reason) => WouldSkip(zdo, out reason, out _, out _);

        // The same decision, also handing back what a caller that acts on a true needs, so the winning path does
        // not rebuild the identity and look the dictionary entry up a second time.
        private static bool WouldSkip(ZDO zdo, out SkipReason reason,
                                      out MeshBaker.PieceIdentity identity, out MeshBaker.PieceTransform cached)
        {
            identity = default;
            cached = default;

            if (zdo == null) { reason = SkipReason.NoZdo; return false; }
            if (!SkipCreationActive()) { reason = SkipReason.SkipInactive; return false; }

            var info = SkipEligibility.Get(zdo.GetPrefab());
            if (!info.Skippable) { reason = SkipReason.PrefabNotSkippable; return false; }
            // [Optimize] ShellDamageablePieces: a building piece is created by vanilla and silenced (a shell), never skipped to a stand-in.
            if (ShellPieces.KeepsReal(info)) { reason = SkipReason.SkipInactive; return false; }

            Vector3 position = zdo.GetPosition();
            if (s_buildingNearby && InBuildHold(position)) { reason = SkipReason.BuildHold; return false; }

            var zoneCoord = EbmZones.Of(position);
            if (!_zones.TryGetValue(zoneCoord, out var state))
            {
                // Peek, never construct. EnsureZoneFromCache materialises a cached bake as a side effect, and
                // this is asked about every queued ZDO in a single frame. Nothing is lost by peeking in the case
                // that matters: ConstructCachedZonesInRange builds every cached zone in range from Update, so a
                // zone that HAS a cache and is near enough to be creating pieces is already in _zones. A cache
                // that exists and was still not constructed is worth telling apart from a zone nobody has baked,
                // which is the whole difference between these two reasons.
                reason = FiresEasyBakeMeshesPlugin.CachePersistEnabled.Value
                         && MeshCacheStore.TryGetPreloaded(zoneCoord, out _)
                    ? SkipReason.ZoneBakePending
                    : SkipReason.ZoneUnknown;
                return false;
            }
            if (!state.Baked || state.Bake == null) { reason = SkipReason.ZoneBakePending; return false; }
            // A handed-back piece skipped again before it exists would have its stand-in dropped when the hand-back ends.
            if (state.AwaitingRecreate != null) { reason = SkipReason.AwaitingRecreate; return false; }
            if (state.HoldReal && (state.HoldWholeZone || !s_buildingNearby)) { reason = SkipReason.ZoneHoldingReal; return false; }

            identity = MeshBaker.PieceIdentity.From(zdo);
            if (!state.Bake.PieceTransforms.TryGetValue(identity, out cached)) { reason = SkipReason.NotInBake; return false; }
            if (!SkipEligibility.ZdoMatches(zdo, info, cached)) { reason = SkipReason.ZdoChanged; return false; }

            reason = SkipReason.WouldBeSkipped;
            return true;
        }

        // ZNetScene.CreateObject prefix. True means the piece stays uncreated: its cached bake draws it and its
        // stand-in collider already exists.
        //
        // The verdict is WouldSkip's. What stays here is everything WouldSkip must not do: building the zone from
        // its cache, running the defer timer, and the bookkeeping a real skip owes the rest of the mod. The cheap
        // tests ahead of that are a pre-filter and not a second opinion - they are what keeps EnsureZoneFromCache,
        // the one side effect on this path, off the many ZDOs that were never skip candidates to begin with.
        public static bool TrySkipCreation(ZDO zdo)
        {
            long tp = System.Diagnostics.Stopwatch.GetTimestamp();   // 1.2.142 sampler
            SkipFrameBegin();
            s_justSkippedForCreate = null;   // 1.2.167
            if (zdo == null || !SkipCreationActive()) return false;
            int prefabHash = zdo.GetPrefab();
            // 1.2.157 ([visual], Fire 10-31 pick (b), A/B toggle [Optimize] SpreadFirstSightChecks): once this frame's first-sight
            // checks pass FirstSightBudgetMs, a piece whose prefab isn't checked yet is created LIVE this frame (vanilla's own path;
            // no hold, which 1.2.150 proved costly) and its prefab is checked on a later frame; the live-piece conversion bakes it
            // then. Off = today's behaviour.
            if (FiresEasyBakeMeshesPlugin.SpreadFirstSightChecks != null && FiresEasyBakeMeshesPlugin.SpreadFirstSightChecks.Value
                && !SkipEligibility.Known(prefabHash) && s_skipGateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= FirstSightBudgetMs)
            {
                s_firstSightLive++;
                return false;
            }
            long gate0 = ModdedGateTicks;
            var info = SkipEligibility.Get(prefabHash);
            SkipPhase(0, ref tp);
            long gate = ModdedGateTicks - gate0;   // the modded-gate share of this call's eligibility
            s_skipPhaseTicks[0] -= gate;
            s_skipGateTicks += gate;
            if (!info.Skippable) return false;
            // [Optimize] ShellDamageablePieces: vanilla creates it untouched (every WearNTear registration, ZDO read and write, other mods'
            // patches); OnInstanceCreated silences it because the bake draws it. No stand-in, so no collider without a WearNTear.
            if (ShellPieces.KeepsReal(info)) { ShellPieces.NoteDeclinedSkip(); return false; }

            Vector3 position = zdo.GetPosition();
            if (s_buildingNearby && InBuildHold(position)) return false;
            SkipPhase(1, ref tp);
            var zoneCoord = EbmZones.Of(position);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var state = EnsureZoneFromCache(zoneCoord);
            NoteSlowSkipStep("building zone from cache", zoneCoord, t0);   // 1.2.140
            SkipPhase(2, ref tp);
            if (state == null || !state.Baked || state.Bake == null)
            {
                // The bake has not landed yet, so this piece cannot be skipped: it is created now.
                // 1.2.150 (Fire 10-30 "if that setting makes things worse then lets get rid of it all together"): the
                // DeferCreationWhileBaking hold is gone (measured 51.7 -> 20.1 fps; a held ZDO was retried on every pass).
                SkipPhase(3, ref tp);
                return false;
            }

            SkipPhase(3, ref tp);
            bool would = WouldSkip(zdo, out var skipReason, out var identity, out var cached);
            NoteSkipReason(skipReason);
            // 1.2.255: a contributor with no saved transform is a damageable piece the bake draws and the damageable rule keeps real; anything else is truly not in the bake.
            if (skipReason == SkipReason.NotInBake) { if (state.Bake.ContributorIdentities != null && state.Bake.ContributorIdentities.Contains(identity)) s_notInBakeKeptReal++; else s_notInBakeMissing++; }
            SkipPhase(4, ref tp);
            if (!would && !(skipReason == SkipReason.NotInBake && TryLateJoinFromZdo(state, zdo, identity, info, out cached))) return false;   // 1.2.253: joined = everything below, as for any skip

            PieceData.Sample(zdo, prefabHash);
            SkipPhase(5, ref tp);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureStandIn(state, identity, cached);
            NoteSlowSkipStep("making the stand-in", zoneCoord, t1);   // 1.2.140
            SkipPhase(6, ref tp);

            zdo.Created = true;
            state.Skipped[zdo] = new SkippedPiece(zdo, identity);
            BindDamageProxy(state, identity, zdo, info);
            SkipPhase(7, ref tp);
            state.LastChangeUnscaledTime = Time.unscaledTime;
            s_skippedAtCreation++;

            // ── WHY A SKIP HAS TO ANNOUNCE ITSELF TO THE DESTROY PATH ────────────────────────────────────
            // We signal a skip by returning null from ZNetScene.CreateObject. Vanilla's CreateObjectsSorted
            // reads that null as "invalid prefab" and, WHEN IsServer(), deletes the object from the world:
            //
            //     else if (ZNet.instance.IsServer())
            //     { zdo.SetOwner(...); ZLog.Log("Destroyed invalid prefab ZDO:" + zdo.m_uid);
            //       ZDOMan.instance.DestroyZDO(zdo); }
            //
            // That is the ONLY reason skipping was client-only: our signal collides with vanilla's error path,
            // and on a host the two are the same process. It is not a property of the optimisation.
            //
            // So the skip hands the ZDO forward as a ONE-SHOT, consumed by the DestroyZDO prefix that runs
            // immediately afterwards in the same loop iteration. One-shot rather than a set membership test on
            // purpose: a stale entry here would BLOCK A REAL DELETION, and a piece that refuses to die is a
            // worse bug than one that is drawn twice. Nothing else can reach it - the destroy is the very next
            // statement after the null return.
            // 1.2.167 ([generator] B7): only a server runs that destroy (ZNetScene.CreateObjectsSorted); on a client this stayed armed and blocked a later real removal.
            s_justSkippedForCreate = ZNet.instance != null && ZNet.instance.IsServer() ? zdo : null;
            return true;
        }

        private static ZDO s_justSkippedForCreate;

        // 1.2.142 ([visual], 10-28 ledger: the CreateObject skip prefix ran 895 ms over 9 stall frames, no single step >=10 ms): a
        // Debug-only sampler. Each call's time is split by step and summed per frame; a frame whose skip prefix took over
        // SkipFrameReportMs writes one line with the per-step totals and the call count. Costs a few timestamps a call.
        private const double SkipFrameReportMs = 20.0;
        // 1.2.143: "eligibility" is reported as "prefab scan" + "modded gate" (the gate's share is timed inside ModdedPrefabGate.Refuses
        // via ModdedGateTicks), so the log proves where a slow first sighting of a prefab goes.
        private static readonly string[] SkipPhaseNames = { "prefab scan", "build hold", "zone from cache", "defer check", "WouldSkip", "piece sample", "stand-in", "bind + bookkeeping" };
        internal static long ModdedGateTicks;
        private static readonly long[] s_skipPhaseTicks = new long[8];
        private static int s_skipFrame = -1, s_skipCalls;
        private static long s_skipGateTicks;
        // 1.2.157: the first-sight budget (SpreadFirstSightChecks) and its A/B proof
        private const double FirstSightBudgetMs = 4.0;
        private static int s_firstSightLive, s_firstSightLiveTotal, s_firstSightLiveFrames;
        private static double s_worstSkipFrameMs;

        private static void SkipPhase(int phase, ref long t)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            s_skipPhaseTicks[phase] += now - t;
            t = now;
        }

        private static void SkipFrameBegin()
        {
            int frame = Time.frameCount;
            if (frame != s_skipFrame)
            {
                if (s_skipFrame >= 0 && s_skipCalls > 0)
                {
                    long sum = s_skipGateTicks;
                    foreach (long ticks in s_skipPhaseTicks) sum += ticks;
                    double ms = sum * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms > s_worstSkipFrameMs) s_worstSkipFrameMs = ms;
                    if (s_firstSightLive > 0)
                    {
                        s_firstSightLiveTotal += s_firstSightLive;
                        s_firstSightLiveFrames++;
                        EasyBakeLog.Debug($"[Skip] frame {s_skipFrame}: first-sight budget {FirstSightBudgetMs:F0} ms reached, {s_firstSightLive} piece(s) created live to bake later (session: {s_firstSightLiveTotal} piece(s) over {s_firstSightLiveFrames} frame(s); worst skip-prefix frame {s_worstSkipFrameMs:F0} ms)");
                    }
                    if (ms >= SkipFrameReportMs)
                    {
                        var parts = new System.Text.StringBuilder();
                        if (s_skipGateTicks > 0) parts.Append("modded gate ").Append((s_skipGateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1")).Append(" ms");
                        for (int i = 0; i < s_skipPhaseTicks.Length; i++)
                            if (s_skipPhaseTicks[i] > 0) parts.Append(parts.Length > 0 ? ", " : "").Append(SkipPhaseNames[i]).Append(' ').Append((s_skipPhaseTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1")).Append(" ms");
                        EasyBakeLog.Debug($"[Skip] frame {s_skipFrame}: CreateObject skip prefix {ms:F0} ms over {s_skipCalls} call(s): {parts} (worst so far {s_worstSkipFrameMs:F0} ms; spread {(FiresEasyBakeMeshesPlugin.SpreadFirstSightChecks != null && FiresEasyBakeMeshesPlugin.SpreadFirstSightChecks.Value ? "on" : "off")})");
                    }
                }
                s_skipFrame = frame;
                s_skipCalls = 0;
                s_skipGateTicks = 0;
                s_firstSightLive = 0;
                System.Array.Clear(s_skipPhaseTicks, 0, s_skipPhaseTicks.Length);
            }
            s_skipCalls++;
        }

        // 1.2.140 ([visual], round 45 login stall: this prefix ran 659 ms in one frame, longest call 139 ms): one Debug line per step
        // that takes over SlowSkipStepMs, so the next round shows which step the time goes to.
        private const double SlowSkipStepMs = 10.0;
        private static void NoteSlowSkipStep(string step, Vector2s zone, long start)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms >= SlowSkipStepMs) EasyBakeLog.Debug($"[Skip] slow step inside CreateObject: {step} for zone ({zone.x},{zone.y}) took {ms:F0} ms (frame {Time.frameCount})");
        }

        /// <summary>Consumes the one-shot skip signal. Returns true only for the ZDO we just declined to create.</summary>
        internal static bool ConsumeJustSkipped(ZDO zdo)
        {
            if (!ReferenceEquals(zdo, s_justSkippedForCreate)) return false;
            s_justSkippedForCreate = null;
            return true;
        }

        internal static void LogSkipFailureOnce(System.Exception ex)
        {
            if (s_skipFailureLogged) return;
            s_skipFailureLogged = true;
            EasyBakeLog.Warn($"[Skip] Skipping piece creation failed and is off for that piece: {ex.GetType().Name}: {ex.Message}");
        }

        // ZNetScene.IsAreaReady waits for an instance of every object around a point, which a skipped piece never gets.
        // 1.2.192 (Fire 0108i, BlueHills teleport: "not 'ready' for 30 s"; lead GO): vanilla holds an arrival until EVERY valid ZDO in the
        // 3x3 around the point has an instance. EBM decides "skip" only when the create loop reaches a piece, so pieces of a baked zone that
        // EBM WILL skip still counted as missing until then, and the arrival waited on the create loop to walk thousands of them.
        //   [Optimize] ArrivalCountsBakedPieces (default ON, a fix): such a piece counts as ready once its stand-in collider exists (the
        //   bake draws it and its collider is already there, so nobody falls through a floor that is drawn but not yet solid).
        //   Proof line (Debug, at most every 5 s while an arrival waits): "missing N: S skipped, W would be skipped (stand-in ready),
        //   C would be skipped (no stand-in yet), K still to create -> ready / waiting".
        private static float s_nextAreaLine;

        public static bool IsAreaReadyCountingSkipped(Vector3 point)
        {
            bool countWould = FiresEasyBakeMeshesPlugin.ArrivalCountsBakedPieces != null && FiresEasyBakeMeshesPlugin.ArrivalCountsBakedPieces.Value;
            if (!countWould && !AnySkipped()) return false;
            var zoneSystem = ZoneSystem.instance;
            var scene = ZNetScene.instance;
            var zdoMan = ZDOMan.instance;
            if (zoneSystem == null || scene == null || zdoMan == null) return false;

            var zone = EbmZones.Of(point);
            if (!EbmZones.Loaded(zoneSystem, zone)) return false;
            _areaScratch.Clear();
            zdoMan.FindSectorObjects(zone, new SimulationDistance(1, 0), _areaScratch);
            float now = Time.unscaledTime;
            bool report = now >= s_nextAreaLine;
            int missing = 0, skipped = 0, would = 0, wouldNoCollider = 0, toCreate = 0;
            for (int i = 0; i < _areaScratch.Count; i++)
            {
                var zdo = _areaScratch[i];
                int prefab = zdo.GetPrefab();
                if (prefab == 0 || FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(prefab) == null) continue;
                if (scene.FindInstance(zdo) != null) continue;
                missing++;
                if (IsSkipped(zdo)) { skipped++; continue; }
                bool blocks;
                if (WouldSkip(zdo, out _, out var identity, out _)
                    && _zones.TryGetValue(EbmZones.Of(zdo.GetPosition()), out var st))
                {
                    if (st.StandIns.ContainsKey(identity)) { would++; blocks = !countWould; }
                    else { wouldNoCollider++; blocks = true; }
                }
                else { toCreate++; blocks = true; }
                if (blocks && !report) return false;   // not reporting: the first blocker answers
            }
            bool ready = toCreate == 0 && wouldNoCollider == 0 && (countWould || would == 0);
            if (report && missing > 0)
            {
                s_nextAreaLine = now + 5f;
                EasyBakeLog.Debug($"[Arrival] area at ({point.x:0}, {point.z:0}): missing {missing}: {skipped} skipped, {would} would be skipped (stand-in ready), {wouldNoCollider} would be skipped (no stand-in yet), {toCreate} still to create -> {(ready ? "ready" : "waiting")}{(would > 0 && !countWould ? " ([Optimize] ArrivalCountsBakedPieces is off)" : "")}");
            }
            return ready;
        }
        private static bool AnySkipped()
        {
            foreach (var state in _zones.Values)
                if (state.Skipped.Count > 0) return true;
            return false;
        }

        // ═══ ONLY A PIECE THAT CAN BE HURT GETS A DAMAGE PROXY ════════════════
        // An invulnerable stand-in is left exactly as it is, on purpose. Giving it an IDestructible would change
        // what a weapon swing finds on something that has always answered "nothing here" - a behaviour change
        // for the pieces this mod has been skipping all along, in service of a feature they take no part in.
        //
        // For the ones that CAN be hurt, the proxy goes on the object StandInColliders.Build returned, which is
        // the collider itself for a single-collider piece and the holder for a multi-collider one. Both resolve,
        // because Projectile.FindHitObject looks with GetComponentInParent<IDestructible>() and then
        // GetComponent<IDestructible>() on what that found.
        private static void BindDamageProxy(ZoneState state, MeshBaker.PieceIdentity identity, ZDO zdo, SkipEligibility.PrefabInfo info)
        {
            if (info == null || info.AllImmune || SkipEligibility.StoredHealthIsNegative(zdo, info)) return;   // 1.2.167 ([generator] B9): a hit there holds the zone real for nothing
            if (!state.StandIns.TryGetValue(identity, out var standIn) || standIn == null) return;
            StandInDamageBinding.Bind(standIn, zdo);
        }

        internal static bool IsSkipped(ZDO zdo)
        {
            return _zones.TryGetValue(EbmZones.Of(zdo.GetPosition()), out var state) && state.Skipped.ContainsKey(zdo);
        }

        // [worldgen] 11-09, [Optimize] FarHandoffProvenDrawn (round 2 E17): skipped AND drawn this frame by its group, i.e. DrawInstances' own gate for that
        // zone holds (instancing on, zone active, baked, bake ready, groups present). IsSkipped alone is also true while the zone's bake is not drawing yet.
        internal static bool SkippedAndDrawn(ZDO zdo)
        {
            return FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value
                && _zones.TryGetValue(EbmZones.Of(zdo.GetPosition()), out var state) && state.Skipped.ContainsKey(zdo)
                && state.ZoneActive && state.Baked && state.Bake != null && state.Bake.Ready && state.Bake.InstanceGroups.Count > 0;
        }

        // ═══ BRING BACK ONE PIECE, BECAUSE SOMETHING IS ABOUT TO DAMAGE IT ════
        // Damage is owner-authoritative: WearNTear.Damage only routes, and RPC_Damage returns unless
        // m_nview.IsOwner(). When THIS client owns a piece it never created, the owner-side handler does not
        // exist, and ZRoutedRpc.HandleRoutedRPC drops the call with no else branch and no log line. So the
        // instance has to exist before the hit can be replayed into it - see DamageReplay.
        //
        // ONE piece, not a neighbourhood. HoldRealPiecesNear(radius) is the build-tool path and would pull in
        // every piece within reach; a hit needs exactly its own target, and support propagation is the OWNER's
        // job for everything else - on a dedi the server already has the neighbours live.
        //
        // Returns false when the ZDO is not one EBM is holding back, which is the important half: a routed RPC
        // for a missing instance is a thing that happens in vanilla for its own reasons, and claiming those
        // would be masking somebody else's behaviour.
        internal static bool MaterialiseForDamage(ZDO zdo, float holdSeconds)
        {
            if (zdo == null) return false;
            if (!_zones.TryGetValue(EbmZones.Of(zdo.GetPosition()), out var state)) return false;
            if (!state.Skipped.TryGetValue(zdo, out var skipped)) return false;

            state.HoldReal = true;
            state.HoldRealUntil = Mathf.Max(state.HoldRealUntil, Time.unscaledTime + holdSeconds);

            // A whole-zone hand-back in flight drops EVERY stand-in when it lands, so it keeps no identity
            // list; adding one here would make it drop only this piece's stand-in instead of all of them.
            bool wholeZoneInFlight = state.AwaitingRecreate != null && state.AwaitingIdentities == null;
            if (state.AwaitingRecreate == null)
            {
                state.AwaitingRecreate = new List<ZDO>(1);
                state.AwaitingIdentities = new List<MeshBaker.PieceIdentity>(1);
                state.AwaitingSince = Time.unscaledTime;
            }

            zdo.Created = false; Patches.ZdoEventDirty.Mark();                 // vanilla's budgeted CreateDestroyObjects picks it up again
            state.AwaitingRecreate.Add(zdo);
            if (!wholeZoneInFlight) state.AwaitingIdentities.Add(skipped.Identity);
            state.Skipped.Remove(zdo);
            s_handedBackForDamage++;
            state.LastChangeUnscaledTime = Time.unscaledTime;
            return true;
        }

        // [Optimize] ShellDamageablePieces turned off: real (silenced) building pieces of baked zones go to the conversion queue again, the same
        // proposal AfterFreshBake makes; ConvertLivePieces' own tests still decide. Turning it on uses HandBackAllSkipped instead.
        internal static void RequeueConversions()
        {
            if (!SkipCreationActive()) return;
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked || state.Bake?.ContributorIdentities == null) continue;
                foreach (var piece in state.Pieces)
                {
                    if (piece == null) continue;
                    if (!InvulnerableClassifier.IsInvulnerable(piece) && !SkipEligibility.DamageableSkipAllowed) continue;
                    if (state.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(piece.gameObject, piece.transform.position)))
                        state.ConvertQueue.Enqueue(piece);
                }
                QueueStandIns(state);
            }
        }

        internal static int SkippedWearPieces()
        {
            int n = 0;
            foreach (var state in _zones.Values)
                foreach (var entry in state.Skipped)
                    if (entry.Key != null && SkipEligibility.Get(entry.Value.Identity.PrefabHash).HasWear) n++;
            return n;
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
            var center = EbmZones.Of(net.GetReferencePosition());
            int radius = net.GetSyncedSimulationDistance().NearSimulationDistance;
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    var coord = EbmZones.Norm(new Vector2s(center.x + dx, center.y + dy));   // 1.2.122: wraps at the ring seam
                    if (_zones.ContainsKey(coord) || !EbmZones.Loaded(zoneSystem, coord)) continue;
                    if (!MeshCacheStore.TryGetPreloaded(coord, out _)) continue;
                    EnsureZoneFromCache(coord);
                }
            }
            MeshCacheStore.PumpNearOnly(center);   // 1.2.255 [Cache] PreloadNearOnly; off = returns at once
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
            NoteStandInsTouched(state);   // 1.2.208: EbmApi.StandInsChanged after the frame (Core's shelter follows removals too)
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

        private static int s_standInsNoPiece;
        private static int s_notInBakeKeptReal, s_notInBakeMissing;   // 1.2.255
        private static readonly List<ZDO> _liveScratch = new List<ZDO>();
        private static HashSet<MeshBaker.PieceIdentity> s_liveCheckSet = new HashSet<MeshBaker.PieceIdentity>();   // 1.2.255 CacheLowGarbage

        // The identities of the pieces in this zone that this client holds a ZDO for right now.
        // 1.2.130: identities among the last LiveIdentities scan whose ZDO has a real object in the scene right now.
        // 1.2.191: RealIdentities() (a per-frame pass over every ZDO of the zone) is gone; IsReal() looks one identity up.
        private static int s_validationWaits, s_cacheRechecks;
        private const float FgnFillingCeiling = 60f;   // a zone FGN said is filling but never settled (it left the near area) is not held past this
        private const float CacheRecheckSeconds = 30f;
        private const float CacheZdoQuietSeconds = 30f;   // 1.2.194
        private static int s_missingDropped, s_missingRebakes;   // 1.2.194
        // 1.2.253: RemoteZoneValidation + SaveOffThread
        private const int StaleCacheMinMissing = 50;
        private const float StaleCacheShare = 0.25f;
        private const float SaveMinSeconds = 30f;
        private static int s_sectorWaits, s_staleCacheZones;
        private static bool s_saidSectorRule, s_saidStaleCache;

        private static bool SectorJudgeable(ZoneState state, float now)
        {
            var net = ZNet.instance;
            if (net == null) return false;
            var delta = EbmZones.Delta(EbmZones.Of(net.GetReferencePosition()), state.Coord);
            if (Mathf.Abs(delta.x) > 1 || Mathf.Abs(delta.y) > 1) return false;
            float since = Mathf.Max(state.ConstructedUnscaledTime, SectorArrivals.LastArrival(state.Coord));   // nothing arrived yet still waits out the wait from when the zone was built
            return now - since >= CacheZdoQuietSeconds;
        }

        private static string ExtraSkipCounters()
        {
            var sb = new System.Text.StringBuilder();
            int fellBack = 0;
            foreach (int n in s_lateJoinFallback) fellBack += n;
            if (s_joinedFromZdo > 0 || fellBack > 0)
            {
                sb.Append($" Joined from ZDOs: {s_joinedFromZdo}; fell back to creating: {fellBack}");
                for (int i = 0; i < s_lateJoinFallback.Length; i++)
                    if (s_lateJoinFallback[i] > 0) sb.Append($", {LateJoinFallbackNames[i]} {s_lateJoinFallback[i]}");
                sb.Append('.');
            }
            if (SectorArrivals.KeyOn) sb.Append($" Sector rule: {s_sectorWaits} check(s) waited, {s_staleCacheZones} stale zone cache(s) discarded.");
            string saves = MeshCacheStore.AsyncSaveSummary();
            if (saves != null) sb.Append(' ').Append(saves).Append('.');
            if (s_notInBakeKeptReal > 0 || s_notInBakeMissing > 0) sb.Append($" Not in the bake's saved transforms: {s_notInBakeKeptReal} damageable, kept real; {s_notInBakeMissing} truly missing.");
            string cache = MeshCacheStore.CacheSummary();
            if (cache != null) sb.Append(' ').Append(cache).Append('.');
            if (s_emptyZoneDrops > 0) sb.Append($" Demolished zones' saved bakes removed: {s_emptyZoneDrops}.");
            return sb.ToString();
        }

        // 1.2.193: FGN's zone signal (FgnLink). Filling = the zone is still receiving / creating; Settled = all of it is in.
        // 1.2.194 ([fgn] 11-08: ZoneFilling fires when a zone's first uncreated ZDO is queued, before any is created): a zone with a cache
        // is built from it right then, so its pieces meet a baked zone at creation and are skipped (FGN counts the skip as done), instead of
        // being created and unloaded again because the 0.25 s range pass had not reached the zone yet (1.2.193 city: 9,428 on CACHED zones).
        private static int s_cachedFromFilling;
        internal static void NoteFgnFilling(Vector2s zone)
        {
            if (!_zones.TryGetValue(zone, out var st) && SkipCreationActive() && !EbmZones.Ring
                && ZoneSystem.instance != null && EbmZones.Loaded(ZoneSystem.instance, zone))
            {
                st = EnsureZoneFromCache(zone);
                if (st != null) s_cachedFromFilling++;
            }
            if (st != null) { st.FgnSettled = false; st.FgnFillingAt = Time.unscaledTime; }
        }
        internal static void NoteFgnSettled(Vector2s zone)
        {
            if (!_zones.TryGetValue(zone, out var st)) return;
            st.FgnSettled = true; st.SettledSinceCheck = true;
            st.LiveStamp = -1;   // the zone's identity set is rebuilt once now that all of it is here
        }

        private static int s_liveWithheld, s_liveWithheldLogged, s_onDemandLogged;
        private static float s_nextStandInWhy;
        private static string s_lastWithheldName;

        private static void NoteLiveWithheld(MeshBaker.PieceIdentity identity)
        {
            s_liveWithheld++;
            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(identity.PrefabHash) : null;
            s_lastWithheldName = prefab != null ? prefab.name : identity.PrefabHash.ToString();
        }

        // Why-line (Fire 10-26 "logging to tell WHY"): at most every 10 s, only when the counts moved.
        internal static void LogStandInWhy()
        {
            if (Time.time < s_nextStandInWhy) return;
            s_nextStandInWhy = Time.time + 10f;
            // 1.2.191 proof (lead 11-08): identity sets rebuilt per zone change vs reused, and cached-zone checks waiting for their ZDOs
            if (s_idSetBuilds > 0 || s_idSetReuses > 0 || s_validationWaits > 0 || s_cacheRechecks > 0)
            {
                EasyBakeLog.Debug($"[StandIn] zone identity sets, last 10 s: {s_idSetBuilds} rebuilt (a zone's ZDOs changed or 2 s passed), {s_idSetReuses} reused; {s_validationWaits} cached-zone check(s) waited for the zone's ZDOs; {s_cacheRechecks} check(s) found pieces missing and will look again before any rebake; so far {s_missingDropped} gone piece(s) dropped from their instanced groups without a rebake, {s_missingRebakes} rebake(s) for pieces in a combined mesh; {s_cachedFromFilling} cached zone(s) built at FGN's ZoneFilling; FGN zone signal {(FgnLink.Active ? $"on ({FgnLink.FillingEvents} filling / {FgnLink.SettledEvents} settled so far)" : "off (EBM's own timing)")}");
                s_idSetBuilds = 0; s_idSetReuses = 0; s_validationWaits = 0; s_cacheRechecks = 0;
            }
            if (s_liveWithheld != s_liveWithheldLogged)
            {
                EasyBakeLog.Debug($"[StandIn] piece {s_lastWithheldName} live: no stand-in ({s_liveWithheld - s_liveWithheldLogged} more real piece(s) kept without one, {s_liveWithheld} in all)");
                s_liveWithheldLogged = s_liveWithheld;
            }
            if (s_standInsOnDemand < s_onDemandLogged) s_onDemandLogged = 0;   // the 5-min report resets the counter
            if (s_standInsOnDemand != s_onDemandLogged)
            {
                EasyBakeLog.Debug($"[StandIn] stand-in built at skip/convert: {s_standInsOnDemand - s_onDemandLogged} more ({s_standInsOnDemand} since the last report)");
                s_onDemandLogged = s_standInsOnDemand;
            }
        }

        // 1.2.191 (Fire's BlueHills city, 174k ZDOs: "tracking zones 0.70-0.85 ms a frame, stand-ins 0.56-0.65"): the zone's ZDO identity set
        // was rebuilt (FindSectorObjects + a new HashSet of every ZDO, ~3,000 in a city zone) on EVERY frame its stand-in queue was open.
        // Now it is kept on the zone and rebuilt only when the sector's ZDO count changes (a ZDO arrived / went) or it is 2 s old.
        private static readonly HarmonyLib.AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_bySector = HarmonyLib.AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");
        private static int s_idSetBuilds, s_idSetReuses;
        private const float IdSetMaxAge = 2f;

        private static int SectorCount(Vector2s coord)
        {
            var zm = ZDOMan.instance; if (zm == null) return -1;
            List<ZDO>[] bySector;
            try { bySector = s_bySector(zm); } catch { return -1; }
            if (bySector == null) return -1;
            uint i = ZoneSystem.SectorToIndex(coord).Sector;
            if (i >= (uint)bySector.Length) return -1;   // outside the sector grid: no cheap count, rebuild
            var list = bySector[i];
            return list != null ? list.Count : 0;
        }

        private static HashSet<MeshBaker.PieceIdentity> LiveIdentities(ZoneState state)
        {
            float now = Time.unscaledTime;
            int count = SectorCount(state.Coord);
            if (state.LiveIds != null && count >= 0 && count == state.LiveStamp && now - state.LiveAt < IdSetMaxAge) { s_idSetReuses++; return state.LiveIds; }
            var set = state.LiveIds ?? new HashSet<MeshBaker.PieceIdentity>();
            var map = state.LiveZdos ?? new Dictionary<MeshBaker.PieceIdentity, ZDO>();
            set.Clear(); map.Clear();
            var zdoMan = ZDOMan.instance;
            if (zdoMan != null)
            {
                _liveScratch.Clear();
                zdoMan.FindSectorObjects(state.Coord, new SimulationDistance(0, 0), _liveScratch);
                for (int i = 0; i < _liveScratch.Count; i++)
                {
                    var zdo = _liveScratch[i];
                    if (zdo == null || !zdo.IsValid()) continue;
                    var id = MeshBaker.PieceIdentity.From(zdo);
                    set.Add(id); map[id] = zdo;
                }
            }
            if (count != state.LiveStamp) state.LiveCountChangedAt = now;   // ZDOs still arriving (or leaving) in this zone
            state.LiveIds = set; state.LiveZdos = map; state.LiveStamp = count; state.LiveAt = now;
            s_idSetBuilds++;
            return set;
        }

        // 1.2.191: "is this identity a real (created) object right now" for one identity, from the zone's identity -> ZDO map; replaces the
        // per-frame RealIdentities() pass over every ZDO of the zone.
        private static bool IsReal(ZoneState state, MeshBaker.PieceIdentity identity, Dictionary<ZDO, ZNetView> instances)
        {
            return instances != null && state.LiveZdos != null && state.LiveZdos.TryGetValue(identity, out var zdo) && instances.ContainsKey(zdo);
        }

        private static void QueueStandIns(ZoneState state)
        {
            state.StandInQueue.Clear();
            if (!SkipCreationActive() || state.Bake?.PieceTransforms == null) return;
            foreach (var identity in state.Bake.PieceTransforms.Keys)
            {
                if (state.StandIns.ContainsKey(identity)) continue;
                if (!SkipEligibility.IsPrefabSkippable(identity.PrefabHash)) continue;
                if (ShellPieces.KeepsReal(identity.PrefabHash)) continue;   // its shell carries the real colliders
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
            // 1.2.127 ([visual], lead/Fire 10-26 "getting wet inside due to ebm"): a re-bake used to destroy EVERY stand-in of the
            // zone and rebuild them over the next frames from StandInQueue, so a skipped roof had no collider in between (vanilla
            // Cover / IsUnderRoof saw open sky: Wet, wind, fires). Stand-ins the new bake still lists stay up; only stale ones go.
            KeepCurrentStandIns(state);
            QueueStandIns(state);
            state.ConvertQueue.Clear();
            foreach (var piece in state.Pieces)
            {
                // THIS FILTER IS THE SECOND SKIP GATE, and missing it makes SkipDamageablePieces look like it
                // barely works. There are two ways into the skip: TrySkipAtCreation, for a piece whose zone was
                // already baked when it arrived, and this queue, for a piece that was ALREADY LIVE when its
                // zone baked. In a town the pieces stream in before their zone settles, so the second path
                // carries most of them - filtering it to invulnerable only would leave every damageable piece
                // that predates its bake live forever, whatever ZdoMatches says. The queue only proposes;
                // ZdoMatches still decides, so widening it cannot skip anything ineligible.
                if (piece == null) continue;
                if (!InvulnerableClassifier.IsInvulnerable(piece) && !SkipEligibility.DamageableSkipAllowed) continue;
                if (ShellPieces.On) continue;   // every piece here has a WearNTear: with shells it stays the real (silenced) piece
                if (state.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(piece.gameObject, piece.transform.position)))
                    state.ConvertQueue.Enqueue(piece);
            }
        }

        // 1.2.88: MaterialVariationWorld swaps on its first Update after its Location is found, MaterialVariation over its first
        // second; a bake before that keeps the prefab look. Hold the bake while a swap is due, at most MaterialVariants.WaitLimitSeconds
        // (a MaterialVariationWorld outside any location never swaps and never disables itself).
        private static bool VariationSwapPending(ZoneState state, float now)
        {
            bool pending = false;
            foreach (var piece in state.Pieces)
                if (MaterialVariants.SwapPending(piece)) { pending = true; break; }
            if (!pending) { state.VariationWaitSince = -1f; return false; }
            if (state.VariationWaitSince < 0f)
            {
                state.VariationWaitSince = now;
                EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): waiting for a material variation swap before baking.");
            }
            if (now - state.VariationWaitSince < MaterialVariants.WaitLimitSeconds) return true;
            if (state.VariationWaitSince > 0f)
            {
                EasyBakeLog.Debug($"[Bake] Zone ({state.Coord.x},{state.Coord.y}): a material variation swap still pending after " +
                                  $"{MaterialVariants.WaitLimitSeconds:0} s; baking without it.");
                state.VariationWaitSince = 0f;   // gave up: 0 keeps it below the limit check's reach until the swap clears
            }
            return false;
        }

        private static void BuildStandIns()
        {
            LogStandInWhy();
            _zoneScratch.Clear();
            foreach (var state in _zones.Values)
                if (state.ZoneActive && state.Baked && state.Bake != null && !state.HoldReal && state.StandInQueue.Count > 0)
                    _zoneScratch.Add(state);
            if (_zoneScratch.Count == 0) return;

            s_sortReference = ZNet.instance != null ? ZNet.instance.GetReferencePosition() : Vector3.zero;
            _zoneScratch.Sort(CompareByDistance);

            var player = Player.m_localPlayer;
            double budget = (player == null || player.IsTeleporting() ? LoadingStandInBudgetMs : StandInBudgetMs) * PerformanceProfile.Budget;   // 1.2.135
            var stopwatch = Stopwatch.StartNew();
            try
            {
                for (int z = 0; z < _zoneScratch.Count; z++)
                {
                    var state = _zoneScratch[z];
                    if (state.StandInRoot == null)
                        state.StandInRoot = new GameObject($"[EasyBake/Zone_{state.Coord.x}_{state.Coord.y}/standins]");
                    // R86 (the lead: bodies stalled 90 s "on StandInCollider" on test area ONE after 1.2.76): the queue is built
                    // from the zone's SAVED bake, which still lists pieces removed while the zone was not tracked (the drill
                    // courses' walls), so their colliders came back every session. A stand-in is only built for a piece whose
                    // ZDO this client holds; one that arrives later gets its stand-in on arrival (EnsureStandIn).
                    HashSet<MeshBaker.PieceIdentity> live = LiveIdentities(state);
                    // 1.2.130 ([visual]/[seasons] 10-26, Fire's hut 403 roofed vs 67 open): a piece that is still a REAL object gets no
                    // stand-in; it sat inside its own invisible copy, and every roof test from the piece (vanilla WearNTear m_haveRoof,
                    // FTS snow / wet) hit it. Its stand-in is built when it is skipped or converted (EnsureStandIn, before it goes).
                    var instances = SceneInstances();
                    while (state.StandInQueue.Count > 0)
                    {
                        if (stopwatch.Elapsed.TotalMilliseconds >= budget) return;
                        var identity = state.StandInQueue.Dequeue();
                        if (state.StandIns.ContainsKey(identity)) continue;
                        if (!live.Contains(identity)) { s_standInsNoPiece++; continue; }
                        if (IsReal(state, identity, instances)) { NoteLiveWithheld(identity); continue; }
                        if (!state.Bake.PieceTransforms.TryGetValue(identity, out var transform)) continue;
                        state.StandIns[identity] = StandInColliders.Build(state.StandInRoot.transform, identity, transform, out int colliders);
                        s_standInColliders += colliders;
                    }
                    // 1.2.208: the zone's stand-ins are all built (Core / Vedr follow this instead of patching EBM)
                    EbmApi.RaiseStandInsChanged(new Vector2i(state.Coord.x, state.Coord.y), state.StandIns.Count);
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
            var centre = EbmZones.Pos(coord);
            if (EbmZones.Ring) return (centre - s_sortReference).sqrMagnitude;   // 1.2.122: 3D on the ring
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
                    if (stopwatch.Elapsed.TotalMilliseconds >= ConvertBudgetMs * PerformanceProfile.Budget) return;   // 1.2.135
                    var wnt = state.ConvertQueue.Dequeue();
                    if (wnt == null) continue;
                    var view = wnt.GetComponent<ZNetView>();
                    var zdo = view != null ? view.GetZDO() : null;
                    if (zdo == null) continue;

                    int prefabHash = zdo.GetPrefab();
                    var info = SkipEligibility.Get(prefabHash);
                    if (!info.Skippable || ShellPieces.KeepsReal(info)) continue;   // a shell stays the real piece
                    var identity = MeshBaker.PieceIdentity.From(zdo);
                    if (!state.Bake.PieceTransforms.TryGetValue(identity, out var cached)) continue;
                    if (!SkipEligibility.ZdoMatches(zdo, info, cached)) continue;
                    EnsureStandIn(state, identity, cached);

                    state.Pieces.Remove(wnt); MeshBaker.ForgetClass(wnt); Patches.ZdoEventDirty.Mark();
                    instances.Remove(zdo);
                    SectorInstanceMirror.OnInstanceRemoved(zdo);
                    view.ResetZDO();
                    Object.Destroy(wnt.gameObject);
                    zdo.Created = true;
                    state.Skipped[zdo] = new SkippedPiece(zdo, identity);
                    BindDamageProxy(state, identity, zdo, info);
                    s_convertedLive++;
                    if (state.FromCache) s_convertedOnCached++; else s_convertedOnFresh++;
                }
            }
        }

        internal static Dictionary<ZDO, ZNetView> SceneInstances()
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
            bool seasonRecheck = state.SeasonEpochChecked != s_seasonEpoch;   // 1.2.180: after a Vedr season change every skipped piece is asked again once
            state.SeasonEpochChecked = s_seasonEpoch;
            foreach (var entry in state.Skipped)
            {
                var zdo = entry.Key;
                var skipped = entry.Value;
                if (!seasonRecheck && skipped.Unchanged(zdo)) continue;
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

        // 1.2.180: Vedr's season or snow masks changed (VedrApi.VisualsChanged). A skipped piece's ZDO does not change with the season, so the watch would never
        // ask it again; the epoch makes the next watch of each zone re-check all its skipped pieces once (winter snow keeps them real, SkipEligibility.VedrWinterSnowHere).
        private static int s_seasonEpoch;

        internal static void RecheckSkippedForSeason(int season, int stage)
        {
            s_seasonEpoch++;
            SkipEligibility.ForgetSnowLines();
            int zones = 0, skipped = 0;
            foreach (var state in _zones.Values)
            {
                if (state.Skipped.Count == 0) continue;
                zones++; skipped += state.Skipped.Count;
                state.NextWatch = 0f;
            }
            EasyBakeLog.Info($"[Skip] season {season} stage {stage}: {skipped} skipped piece(s) in {zones} zone(s) re-checked for winter snow (Vedr snow-building mask {VedrLink.SnowBuildingBiomeMask})");
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

            ForgetIdentityUnlessTwin(state, identity);
            state.Skipped.Remove(zdo);
            if (zdo != null) zdo.Created = false; Patches.ZdoEventDirty.Mark();
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
            s_buildHoldKeys.Clear();
            if (EbmZones.Ring)
            {
                // 1.2.122: on the ring the x,z rectangle is not the neighbourhood: every EBM zone whose sector is within reach
                float reachSqr = (radius + ZoneSystem.c_ZoneSize) * (radius + ZoneSystem.c_ZoneSize);
                foreach (var kv in _zones) if (EbmZones.NearestSqr(kv.Key, position) <= reachSqr) s_buildHoldKeys.Add(kv.Key);
            }
            else
                for (int x = s_buildHoldMinZone.x; x <= s_buildHoldMaxZone.x; x++)
                    for (int y = s_buildHoldMinZone.y; y <= s_buildHoldMaxZone.y; y++)
                        s_buildHoldKeys.Add(new Vector2s(x, y));
            foreach (var key in s_buildHoldKeys)
            {
                if (!_zones.TryGetValue(key, out var state)) continue;
                HoldRealPiecesNear(state, position, radiusSqr, now + BuildHoldSeconds);
            }
        }

        private static readonly System.Collections.Generic.List<Vector2s> s_buildHoldKeys = new System.Collections.Generic.List<Vector2s>();

        // False when a zone the last scan covered is not held (built since, or its hold was released), which needs a scan.
        private static bool KeepBuildHolds(float now)
        {
            float holdUntil = now + BuildHoldSeconds;
            foreach (var key in s_buildHoldKeys)
            {
                if (!_zones.TryGetValue(key, out var state)) continue;
                if (!state.HoldReal) return false;
                state.HoldRealUntil = Mathf.Max(state.HoldRealUntil, holdUntil);
                if (state.Skipped.Count == 0 && state.AwaitingRecreate == null) DestroyStandIns(state);
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
                zdo.Created = false; Patches.ZdoEventDirty.Mark();
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
                entry.Key.Created = false; Patches.ZdoEventDirty.Mark();
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
            NoteStandInsTouched(state);   // 1.2.208: EbmApi.StandInsChanged after the frame (Core's shelter follows removals too)
            foreach (var entry in state.Skipped)
                if (entry.Key != null) entry.Key.Created = false; Patches.ZdoEventDirty.Mark();
            state.Skipped.Clear();
            state.AwaitingRecreate = null;
            state.AwaitingIdentities = null;
            if (parkStandIns && state.StandInRoot != null) { long ztPark = EbmSelfCost.ZT(); state.StandInRoot.SetActive(false); EbmSelfCost.ZAcc(EbmSelfCost.ZsPark, ztPark); }
            else DestroyStandIns(state);
            state.StandInQueue.Clear();
            state.ConvertQueue.Clear();
            state.HoldReal = false;
            state.HoldWholeZone = false;
            state.HoldRealUntil = 0f;
        }

        // Disables every collider under go and clears MeshCollider meshes, so physics drops them THIS frame, not at the deferred Destroy.
        private static int DetachColliders(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>(true);
            foreach (var c in cols)
            {
                if (c is MeshCollider mc) mc.sharedMesh = null;
                c.enabled = false;
            }
            return cols.Length;
        }

        private static void DestroyStandIns(ZoneState state)
        {
            NoteStandInsTouched(state);   // 1.2.208: EbmApi.StandInsChanged after the frame (Core's shelter follows removals too)
            if (state.StandInRoot != null)
            {
                int n = DetachColliders(state.StandInRoot);   // 1.2.96: Destroy is deferred; detach first (MeshCollider rule)
                EbmTrace.Step("standin root destroy", n, "zone " + state.Coord.x + "," + state.Coord.y);
                Object.Destroy(state.StandInRoot);
            }
            state.StandInRoot = null;
            state.StandIns.Clear();
        }

        private static readonly List<MeshBaker.PieceIdentity> _staleScratch = new List<MeshBaker.PieceIdentity>();

        private static void KeepCurrentStandIns(ZoneState state)
        {
            if (state.Bake?.PieceTransforms == null) { DestroyStandIns(state); return; }
            _staleScratch.Clear();
            foreach (var identity in state.StandIns.Keys)
                if (!state.Bake.PieceTransforms.ContainsKey(identity)) _staleScratch.Add(identity);
            foreach (var identity in _staleScratch) DropStandIn(state, identity);
            if (_staleScratch.Count > 0 || state.StandIns.Count > 0)
                EasyBakeLog.Debug($"[StandIn] zone ({state.Coord.x},{state.Coord.y}) re-bake: kept {state.StandIns.Count} stand-in(s), dropped {_staleScratch.Count} stale");
        }

        private static void DropStandIn(ZoneState state, MeshBaker.PieceIdentity identity)
        {
            NoteStandInsTouched(state);   // 1.2.208: EbmApi.StandInsChanged after the frame (Core's shelter follows removals too)
            if (!state.StandIns.TryGetValue(identity, out var standIn)) return;
            if (standIn != null) { DetachColliders(standIn); EbmTrace.Step("standin destroy", standIn.GetInstanceID(), standIn.name); Object.Destroy(standIn); }
            state.StandIns.Remove(identity);
        }

        // 1.2.93 memory line (Fire 10-13 client RAM audit with [generator]): every 5 min at Debug, what EBM holds.
        // Hidden = baked zones soft-unloaded (Parent off) whose meshes stay resident until logout.
        private static float s_nextMemoryReport = 60f;

        private static void ReportMemory(float now)
        {
            if (now < s_nextMemoryReport) return;
            s_nextMemoryReport = now + 300f;
            try
            {
                int baked = 0, hidden = 0, meshes = 0;
                long verts = 0, meshBytes = 0, hiddenBytes = 0;
                foreach (var state in _zones.Values)
                {
                    if (state.Bake == null) continue;
                    baked++;
                    long zoneBytes = 0;
                    foreach (var list in new[] { state.Bake.Batches, state.Bake.FarBatches })
                        foreach (var b in list)
                        {
                            if (b == null || b.Mesh == null) continue;
                            meshes++;
                            long idx = 0;
                            for (int i = 0; i < b.Mesh.subMeshCount; i++) idx += (long)b.Mesh.GetIndexCount(i);
                            verts += b.Mesh.vertexCount;
                            zoneBytes += b.Mesh.vertexCount * 48L + idx * 4L;
                        }
                    meshBytes += zoneBytes;
                    if (!state.ZoneActive) { hidden++; hiddenBytes += zoneBytes; }
                }
                long cacheBytes = MeshCacheStore.PreloadedBytes(out int cacheZones);
                EasyBakeLog.Debug($"[Memory] zones={_zones.Count} baked={baked} hidden={hidden} meshes={meshes} verts={verts} " +
                    $"meshCpu~{meshBytes >> 20}MB (hidden {hiddenBytes >> 20}MB) preloadCache={cacheZones} zones {cacheBytes >> 20}MB " +
                    $"managedHeap={System.GC.GetTotalMemory(false) >> 20}MB");
            }
            catch (System.Exception e) { EasyBakeLog.Debug($"[Memory] line skipped: {e.Message}"); }
        }

        private static void ReportSkipping(float now)
        {
            ReportMemory(now);
            if (now < s_nextSkipReport) return;
            s_nextSkipReport = now + SkipReportSeconds;
            int total = s_skippedAtCreation + s_convertedLive + s_handedBackForBuilding + s_handedBackForDamage + s_handedBackForChanges + s_standInZonesReused + s_handedBackLooks + s_joinedLate + MeshCacheStore.AsyncSaveCount + MeshCacheStore.StatTotal + s_emptyZoneDrops + s_notInBakeKeptReal + s_notInBakeMissing;
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
            // THE REVISIT VERDICT. A zone EBM has never seen must instantiate its
            // pieces before it can bake them — MeshBaker works from live
            // WearNTear components, not ZDOs — so create-then-destroy on a FIRST
            // visit is the cost of learning the zone and cannot be scheduled
            // away. A zone that came back from the cache is the opposite case:
            // ConstructCachedZonesInRange builds its stand-ins when the terrain
            // loads, before vanilla reaches the pieces, so its converted count
            // SHOULD be ~0. If it is not, the pre-construct is losing the race
            // and that is a real bug rather than a cold cache.
            EasyBakeLog.Debug(
                $"[Revisit] zones baked this period: {s_zonesFromCache} from cache, {s_zonesBakedFresh} fresh. " +
                $"Pieces built-then-unloaded: {s_convertedOnCached} on CACHED zones (want ~0 — the bake should have " +
                $"landed first), {s_convertedOnFresh} on FRESH zones (unavoidable; the baker needs the live pieces). " +
                (s_convertedOnCached > s_convertedOnFresh / 4 && s_zonesFromCache > 0
                    ? "CACHED zones are still paying the build-then-destroy cost — the pre-construct is not winning the race."
                    : "Cached zones are pre-constructing ahead of population as intended."));
            EasyBakeLog.Debug(
                $"[Skip] {skippedNow} baked pieces are not created right now, in {zones} zones. So far: {s_skippedAtCreation} skipped at " +
                $"creation, {s_deferredForBake} held for a pending bake, {s_convertedLive} unloaded after creation, {s_standInColliders} stand-in colliders built in {standInSeconds:F1} s ({s_standInsNoPiece} skipped: no ZDO held yet, or removed) " +
                $"({microsPerCollider:F0} microseconds each; {s_standInsOnDemand} " +
                $"built as their pieces arrived, {s_standInZonesReused} zone revisits reused theirs); real pieces " +
                $"brought back: {s_handedBackForBuilding} for build tools, {s_handedBackForDamage} for incoming damage, {s_handedBackForChanges} for removals or changes; " +
                $"{s_handedBackLooks} instanced pieces went back to drawing themselves after damage, a look change, burning or a hammer highlight; " +
                $"{s_joinedLate} pieces that arrived after their zone baked joined its instanced groups." + ExtraSkipCounters());

            string data = PieceData.Report();
            if (data != null) EasyBakeLog.Debug(data);

            string damage = DamageReplay.Report();
            if (damage != null) EasyBakeLog.Debug(damage);
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
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(entry.Key.GetPrefab());
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
            if (!_zones.TryGetValue(EbmZones.Of(position), out var state) || !state.Baked || state.Bake == null)
                return "building pieces in a zone with no bake (too few pieces, or still settling)";
            var identity = MeshBaker.PieceIdentity.From(zdo);
            bool contributor = state.Bake.ContributorIdentities != null && state.Bake.ContributorIdentities.Contains(identity);
            // With SkipDamageablePieces on, "kept live on purpose" is a LIE for a damageable contributor: it is
            // no longer kept live on purpose, it is still live for some reason the mismatch analysis below can
            // name. Falling through is what makes arm B readable - otherwise every damageable piece that failed
            // to skip reports as an intentional decision and the reason it actually failed is invisible.
            if (!invulnerable && !(contributor && SkipEligibility.DamageableSkipAllowed))
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
                var prefab = FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(zdo.GetPrefab());
                string name = prefab != null ? prefab.name : go.name;
                if (s_mismatchExamples.Count < 8 && !s_mismatchExamples.ContainsKey(name)) s_mismatchExamples[name] = detail;
                return "baked, but " + mismatch;
            }
            return "baked and skippable, waiting to be unloaded";
        }

        private static readonly Dictionary<string, string> s_mismatchExamples = new Dictionary<string, string>();

        private static string WhyDamageableNotDrawn(ZoneState state, ZDO zdo, WearNTear wnt)
        {
            // Which half of the healthy test failed, and by what value. A piece that is not actually damaged can still
            // read as worn: WearNTear.Awake takes the stored health BEFORE it raises m_health for the world level, then
            // divides one by the other, so an untouched piece on a levelled world reports a fraction of full health.
            if (!MeshBaker.ShowsHealthyState(wnt))
                return wnt.GetHealthPercentage() <= WearLooks.NewModelAbove
                    ? $"worn or broken (health reads {wnt.GetHealthPercentage():0.00} of {wnt.m_health:0} - a damageable "
                      + "piece draws its own look, since the next hit changes it)"
                    : "worn or broken (its undamaged look is switched off in the prefab)";
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

        // Never baked, instanced or skipped; these pieces always keep their own renderers. 1.2.85: also another mod's prefab that
        // would not look the same baked (ModdedPrefabGate).
        internal static bool IsExcludedFromBaking(GameObject go)
            => CoreLink.IsExcludedFromBaking(go) || IsExcludedPrefab(go) || IsFeast(go) || IsFuelToggled(go) || ModdedPrefabGate.Refuses(go);

        // 1.2.152 (Fire 10-30 shot 403: ONE hanging brazier drew only its bowl, chains + hanger gone, "One of the lamps is low on fuel";
        // the other braziers were whole): a Fireplace switches its m_enabledObject / High / Low children by fuel (vanilla
        // Fireplace.UpdateState), so a bake captures whatever fuel state the piece had at that moment and keeps showing it after the
        // live piece is skipped. Any piece with a Fireplace that has such children is never baked (stays live, like doors / feasts).
        private static readonly Dictionary<string, bool> s_fuelByName = new Dictionary<string, bool>(System.StringComparer.Ordinal);
        private static int s_fuelKept;

        // 1.2.257 (per-object create allocations, READ_zone_create_allocs_20261109 item 2): a created piece's per-prefab checks key by its ZDO's
        // prefab hash, so no name string is made per object (go.name is a new string every read, PrefabName adds a Substring). The name is read
        // only on a hash miss (once per prefab: the name cache is asked first, so verdicts and the once-per-prefab log lines are unchanged) and
        // for objects with no ZDO (prefabs, ghosts), exactly as before. Main thread only, like the Unity reads already here.
        internal static long AllocsAvoidedNames;   // estimate: 2 per name read skipped (go.name + Substring)
        private static readonly Dictionary<int, bool> s_fuelByHash = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> s_feastByHash = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> s_excludedByHash = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> s_transparentByHash = new Dictionary<int, bool>();

        private static int ZdoPrefabHash(GameObject go)
        {
            var view = go.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            return zdo != null ? zdo.GetPrefab() : 0;
        }

        private static bool HashHit(Dictionary<int, bool> cache, int hash, out bool v)
        {
            if (hash != 0 && cache.TryGetValue(hash, out v)) { AllocsAvoidedNames += 2; return true; }
            v = false;
            return false;
        }

        private static bool IsFuelToggled(GameObject go)
        {
            int hash = ZdoPrefabHash(go);
            if (HashHit(s_fuelByHash, hash, out bool v)) return v;
            string key = PrefabName(go);
            if (s_fuelByName.TryGetValue(key, out v)) { if (hash != 0) s_fuelByHash[hash] = v; return v; }
            var fp = go.GetComponentInChildren<Fireplace>(true);
            v = fp != null && (fp.m_enabledObject != null || fp.m_enabledObjectHigh != null || fp.m_enabledObjectLow != null);
            s_fuelByName[key] = v;
            if (hash != 0) s_fuelByHash[hash] = v;
            if (v) EasyBakeLog.Debug($"[EBM] kept live: {key} (toggled visuals: a Fireplace switches its fire / fuel children), {++s_fuelKept} prefab(s) so far");
            return v;
        }

        // 1.2.148 (public user report via Fire: "Feasts are not properly skipping the bake"): a feast swaps its stack visuals
        // (Feast.UpdateVisual SetActive on child objects) as it is eaten; a baked batch keeps the old look until the hammer unbakes it.
        // Any piece carrying vanilla's Feast component (every Feast_* prefab, modded feasts too) is never baked. Cached per prefab name.
        private static readonly Dictionary<string, bool> s_feastByName = new Dictionary<string, bool>(System.StringComparer.Ordinal);

        private static bool IsFeast(GameObject go)
        {
            int hash = ZdoPrefabHash(go);
            if (HashHit(s_feastByHash, hash, out bool v)) return v;
            string key = PrefabName(go);
            if (s_feastByName.TryGetValue(key, out v)) { if (hash != 0) s_feastByHash[hash] = v; return v; }
            v = go.GetComponentInChildren<Feast>(true) != null;
            s_feastByName[key] = v;
            if (hash != 0) s_feastByHash[hash] = v;
            if (v) EasyBakeLog.Debug("[EBM] never baked: " + key + " (a feast; its eaten stages are live child objects)");
            return v;
        }

        private static HashSet<string> s_excludedPrefabs;

        internal static void ReloadExcludedPrefabs() { s_excludedPrefabs = null; s_excludedByHash.Clear(); }

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
            if (s_excludedPrefabs.Count == 0) return false;
            int hash = ZdoPrefabHash(go);
            if (HashHit(s_excludedByHash, hash, out bool v)) return v;
            v = s_excludedPrefabs.Contains(PrefabName(go));
            if (hash != 0) s_excludedByHash[hash] = v;
            return v;
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
            int hash = ZdoPrefabHash(go);
            if (HashHit(s_transparentByHash, hash, out bool hv)) return hv;
            string key = PrefabName(go);
            if (_transparentByName.TryGetValue(key, out bool cached)) { if (hash != 0) s_transparentByHash[hash] = cached; return cached; }

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
            if (hash != 0) s_transparentByHash[hash] = result;
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
