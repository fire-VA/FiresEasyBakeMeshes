using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // [worldgen] 11-09, round 2 rows E26 "baked data follows changes" and E27 "bake refuses ambiguity, proves coverage" (both PARTLY). Our own methods.
    // Three keys, all default off, read live:
    //
    //  [Optimize] BakeCoverageCensus (read only). Every 30 s, every piece ZDO (a prefab with WearNTear) in every active baked zone is put in exactly one class,
    //   from the same state the draw uses:
    //     bake    = its identity is a contributor AND the bake is drawing it now (a group: instancing on and Bake.Ready; merged: Bake.Parent shown),
    //               and it has no instance drawing itself (no instance, or its renderers suppressed by the bake)
    //     real    = an instance draws itself (own renderers on, or coverage draws them) and the bake does not
    //     both    = the bake draws it AND its instance draws itself          (a double draw)
    //     neither = nobody draws it                                           (a gap), split by why and by whether it is in the camera's view
    //   One Debug block: a total line, then one row per zone "N bake, M real, B both, X neither (V in view)", then up to 5 examples per zone of both / neither.
    //   Sliced: at most CensusBudgetMs a frame; a pass that is not done in 30 s carries on and reports when it is.
    //
    //  [Optimize] BakeVerifyCoverage (E27): a both or neither the census finds is looked at again 2 s later; if it is still the same, it is fixed through the paths
    //   EBM already uses: both (steady zone) = the bake's own suppression completed (DisableForCacheHit), or the piece handed back if its look no longer matches;
    //   neither, hidden by the bake but not a contributor = its renderers restored; neither, skipped but not a contributor = handed back to vanilla with its stand-in
    //   kept until the real piece exists (MaterialiseForDamage's path, without its hold). Everything else (still being created, bake not ready yet, zone rebaking)
    //   is counted, never touched. Every fix is logged (Debug) with its reason; the census counts them.
    //
    //  [Optimize] BakeFollowChanges (E26): skipped pieces already have a revision watch (WatchSkipped). A LIVE piece the bake draws (renderers suppressed) was
    //   re-checked only at create, on a health-look change and on the hand-back hooks, so a later ZDO edit (a mod's paint or edit data that PieceData.MustStayLive
    //   reads, a move) kept the bake drawing its old look. Now each such piece's ZDO revision is watched about once a second per zone, inside WatchBudgetMs; a
    //   change re-runs the same test OnInstanceCreated uses (StillMatchesBake) and, on a mismatch or a move, hands the piece back the same way.
    internal static partial class ZoneTracker
    {
        private static bool CensusOn => FiresEasyBakeMeshesPlugin.BakeCoverageCensus != null && FiresEasyBakeMeshesPlugin.BakeCoverageCensus.Value;
        private static bool VerifyOn => FiresEasyBakeMeshesPlugin.BakeVerifyCoverage != null && FiresEasyBakeMeshesPlugin.BakeVerifyCoverage.Value;
        private static bool FollowOn => FiresEasyBakeMeshesPlugin.BakeFollowChanges != null && FiresEasyBakeMeshesPlugin.BakeFollowChanges.Value;
        private const double CensusBudgetMs = 0.5;
        private const float CensusSeconds = 30f, ConfirmSeconds = 2f;

        private enum Cls { Bake, Real, Both, Neither }
        // why a piece is neither / both (counted per zone)
        private static readonly string[] NeitherWhy = { "not created yet", "skipped but not in the bake", "skipped, bake not drawing yet", "hidden by the bake but not in it", "hidden by the bake, bake not drawing yet", "its own renderers off (not EBM)" };
        private static readonly string[] BothWhy = { "zone rebaking / held real (expected until it settles)", "steady zone" };

        private sealed class ZoneCount
        {
            public Vector2s Coord; public int Bake, Real, Both, Neither, InView;
            public readonly int[] NeitherBy = new int[6]; public readonly int[] BothBy = new int[2];
            public readonly List<string> Examples = new List<string>(5);
            public void Clear() { Bake = Real = Both = Neither = InView = 0; System.Array.Clear(NeitherBy, 0, 6); System.Array.Clear(BothBy, 0, 2); Examples.Clear(); }
        }

        private static readonly List<ZoneState> s_censusZones = new List<ZoneState>();
        private static readonly Dictionary<Vector2s, ZoneCount> s_censusCounts = new Dictionary<Vector2s, ZoneCount>();
        private static readonly List<ZDO> s_censusBuf = new List<ZDO>();
        private static readonly HashSet<MeshBaker.PieceIdentity> s_groupIds = new HashSet<MeshBaker.PieceIdentity>();
        private static readonly List<MeshRenderer> s_censusRenderers = new List<MeshRenderer>(16);
        private static readonly Plane[] s_censusPlanes = new Plane[6];
        private static readonly SimulationDistance s_censusSector = new SimulationDistance(0, 0, false);
        private static int s_censusZone = -1, s_censusPiece;
        private static float s_censusNext, s_censusStarted;
        private static bool s_planesOk;
        private static int s_fixBoth, s_fixHandBackLook, s_fixRestored, s_fixSkippedBack, s_fixSkippedNotSame;

        // from Update, every frame (cheap while all three keys are off)
        private static void BakeProofTick(float now)
        {
            if (FollowOn) WatchLivePieces(now);
            if (VerifyOn) ConfirmSuspects(now);
            if (!CensusOn) { s_censusZone = -1; return; }
            if (s_censusZone < 0)
            {
                if (now < s_censusNext) return;
                StartCensus(now);
            }
            StepCensus();
        }

        private static void StartCensus(float now)
        {
            s_censusZones.Clear();
            foreach (var st in _zones.Values) if (st.ZoneActive && st.Baked && st.Bake != null) s_censusZones.Add(st);
            foreach (var c in s_censusCounts.Values) c.Clear();
            s_censusZone = 0; s_censusPiece = -1; s_censusStarted = now; s_censusNext = now + CensusSeconds;
            var cam = Utils.GetMainCamera();
            s_planesOk = cam != null;
            if (s_planesOk) GeometryUtility.CalculateFrustumPlanes(cam, s_censusPlanes);
        }

        private static void StepCensus()
        {
            long end = Stopwatch.GetTimestamp() + (long)(CensusBudgetMs * Stopwatch.Frequency / 1000.0);
            var scene = ZNetScene.instance; var zdoMan = ZDOMan.instance;
            if (scene == null || zdoMan == null) return;
            bool progressed = false;
            while (s_censusZone < s_censusZones.Count)
            {
                var st = s_censusZones[s_censusZone];
                if (s_censusPiece < 0)
                {
                    // 1258 review (b): a zone's setup (its sector list + group ids) starts only while the slice has time left, after at least one step this frame
                    if (progressed && Stopwatch.GetTimestamp() > end) return;
                    progressed = true;
                    // a zone that unloaded or lost its bake since the pass started is left out
                    if (!st.ZoneActive || !st.Baked || st.Bake == null) { s_censusZone++; continue; }
                    s_censusBuf.Clear();
                    zdoMan.FindSectorObjects(st.Coord, s_censusSector, s_censusBuf, null);
                    s_groupIds.Clear();
                    foreach (var g in st.Bake.InstanceGroups) foreach (var id in g.Identities) s_groupIds.Add(id);
                    if (!s_censusCounts.TryGetValue(st.Coord, out var zc)) s_censusCounts[st.Coord] = zc = new ZoneCount { Coord = st.Coord };
                    s_censusPiece = 0;
                }
                var count = s_censusCounts[st.Coord];
                while (s_censusPiece < s_censusBuf.Count)
                {
                    if ((s_censusPiece & 3) == 0 && Stopwatch.GetTimestamp() > end) return;   // 1258 review (b): checked every 4 pieces (was 16)
                    var zdo = s_censusBuf[s_censusPiece++];
                    if (zdo == null || !zdo.IsValid() || EbmZones.Of(zdo.GetPosition()) != st.Coord) continue;
                    var info = SkipEligibility.Get(zdo.GetPrefab());
                    if (info == null || !info.HasWear) continue;
                    var cls = Classify(st, zdo, scene, out int why, out bool inView);
                    switch (cls)
                    {
                        case Cls.Bake: count.Bake++; break;
                        case Cls.Real: count.Real++; break;
                        case Cls.Both:
                            count.Both++; count.BothBy[why]++;
                            Example(count, zdo, "both: " + BothWhy[why]);
                            if (why == 1) Suspect(st, zdo, cls, why);
                            break;
                        default:
                            count.Neither++; count.NeitherBy[why]++; if (inView) count.InView++;
                            Example(count, zdo, "neither: " + NeitherWhy[why] + (inView ? " (IN VIEW)" : ""));
                            if (why == 1 || why == 3) Suspect(st, zdo, cls, why);
                            break;
                    }
                }
                s_censusZone++; s_censusPiece = -1;
            }
            Report();
            s_censusZone = -1;
        }

        // the one verdict, shared by the census and the 2 s confirmation
        private static Cls Classify(ZoneState st, ZDO zdo, ZNetScene scene, out int why, out bool inView)
        {
            why = 0; inView = false;
            var id = MeshBaker.PieceIdentity.From(zdo);
            bool contributor = st.Bake.ContributorIdentities != null && st.Bake.ContributorIdentities.Contains(id);
            bool inGroup = contributor && GroupHas(st, id);
            bool bakeDraws = contributor && st.ZoneActive && st.Baked
                             && (inGroup ? FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value && st.Bake.Ready
                                         : st.Bake.Parent != null && st.Bake.Parent.activeInHierarchy);
            var view = scene.FindInstance(zdo);
            bool suppressed = false, drawsSelf = false;
            if (view != null)
            {
                var sup = view.GetComponent<EasyBakeSuppressedVisuals>();
                suppressed = sup != null && !sup.Restored;
                drawsSelf = !suppressed && DrawsItself(view.gameObject);
            }
            if (bakeDraws && drawsSelf) { why = st.Dirty || st.HoldReal || st.AwaitingRecreate != null ? 0 : 1; return Cls.Both; }
            if (bakeDraws) return Cls.Bake;
            if (drawsSelf) return Cls.Real;
            bool skipped = st.Skipped.ContainsKey(zdo);
            if (view == null) why = skipped ? (contributor ? 2 : 1) : 0;
            else if (suppressed) why = contributor ? 4 : 3;
            else why = 5;
            inView = InView(zdo.GetPosition());
            return Cls.Neither;
        }

        // the census fills s_groupIds per zone; the 2 s confirmation asks the groups directly (one piece)
        private static bool GroupHas(ZoneState st, MeshBaker.PieceIdentity id)
        {
            if (s_censusZone >= 0 && s_censusZone < s_censusZones.Count && s_censusZones[s_censusZone] == st) return s_groupIds.Contains(id);
            return StillDrawn(st, id);
        }

        private static bool DrawsItself(GameObject go)
        {
            go.GetComponentsInChildren(false, s_censusRenderers);
            if (s_censusRenderers.Count == 0) return true;   // nothing to judge (no MeshRenderer): counted as drawing itself
            for (int i = 0; i < s_censusRenderers.Count; i++)
            {
                var r = s_censusRenderers[i];
                if (r != null && (r.enabled || Indirect.Coverage.CoveredObject.HiddenByCoverage(r))) return true;
            }
            return false;
        }

        private static bool InView(Vector3 p) => s_planesOk && GeometryUtility.TestPlanesAABB(s_censusPlanes, new Bounds(p, new Vector3(4f, 4f, 4f)));

        private static void Example(ZoneCount c, ZDO zdo, string what)
        {
            if (c.Examples.Count >= 5) return;
            var prefab = SafePrefab.Get(zdo.GetPrefab());
            var p = zdo.GetPosition();
            c.Examples.Add($"{(prefab != null ? prefab.name : zdo.GetPrefab().ToString())} at ({p.x:F1}, {p.y:F1}, {p.z:F1}) {what}");
        }

        private static void Report()
        {
            if (!EasyBakeLog.DebugOn()) return;
            int bake = 0, real = 0, both = 0, neither = 0, inView = 0, zones = 0;
            foreach (var c in s_censusCounts.Values) { if (c.Bake + c.Real + c.Both + c.Neither == 0) continue; zones++; bake += c.Bake; real += c.Real; both += c.Both; neither += c.Neither; inView += c.InView; }
            var sb = new StringBuilder(1024);
            sb.Append($"[BakeCensus] {zones} active baked zone(s), pass took {Time.unscaledTime - s_censusStarted:0.0} s: {bake} piece(s) drawn by the bake, {real} real, {both} both, {neither} neither ({inView} of them in the camera's view)");
            if (VerifyOn) sb.Append($"; BakeVerifyCoverage fixes since the last line: {s_fixBoth} suppression(s) completed, {s_fixHandBackLook} handed back (look changed), {s_fixRestored} renderer set(s) restored, {s_fixSkippedBack} skipped piece(s) handed back, {s_fixSkippedNotSame} suspect(s) cleared by themselves, {s_fixZoneBusy} left alone while their zone rebaked / was held real / was mid hand-back");
            foreach (var c in s_censusCounts.Values)
            {
                if (c.Bake + c.Real + c.Both + c.Neither == 0) continue;
                sb.Append($"\n  zone ({c.Coord.x},{c.Coord.y}): {c.Bake} bake, {c.Real} real, {c.Both} both, {c.Neither} neither ({c.InView} in view)");
                if (c.Both > 0) { sb.Append("; both by why:"); for (int i = 0; i < 2; i++) if (c.BothBy[i] > 0) sb.Append(' ').Append(BothWhy[i]).Append(' ').Append(c.BothBy[i]).Append(';'); }
                if (c.Neither > 0) { sb.Append("; neither by why:"); for (int i = 0; i < 6; i++) if (c.NeitherBy[i] > 0) sb.Append(' ').Append(NeitherWhy[i]).Append(' ').Append(c.NeitherBy[i]).Append(';'); }
                foreach (var e in c.Examples) sb.Append("\n    ").Append(e);
            }
            EasyBakeLog.Debug(sb.ToString());
            s_fixBoth = s_fixHandBackLook = s_fixRestored = s_fixSkippedBack = s_fixSkippedNotSame = s_fixZoneBusy = 0;
        }

        // ---- E27: confirm after 2 s, then fix through EBM's own paths ----
        private struct SuspectRec { public ZDO Zdo; public Vector2s Coord; public Cls Cls; public int Why; public float At; }
        private static readonly List<SuspectRec> s_suspects = new List<SuspectRec>();
        private const int MaxSuspects = 256, MaxConfirmsPerFrame = 8;
        private static int s_fixZoneBusy;

        private static void Suspect(ZoneState st, ZDO zdo, Cls cls, int why)
        {
            if (!VerifyOn || s_suspects.Count >= MaxSuspects) return;
            s_suspects.Add(new SuspectRec { Zdo = zdo, Coord = st.Coord, Cls = cls, Why = why, At = Time.unscaledTime });
        }

        private static void ConfirmSuspects(float now)
        {
            if (s_suspects.Count == 0) return;
            var scene = ZNetScene.instance;
            if (scene == null) return;
            int done = 0;
            for (int i = s_suspects.Count - 1; i >= 0; i--)
            {
                var s = s_suspects[i];
                if (now - s.At < ConfirmSeconds) continue;
                if (done++ >= MaxConfirmsPerFrame) break;   // 1258 review (d): at most this many re-checks (and so fixes) a frame; the rest wait for the next frames
                s_suspects.RemoveAt(i);
                if (s.Zdo == null || !s.Zdo.IsValid() || !_zones.TryGetValue(s.Coord, out var st) || !st.ZoneActive || !st.Baked || st.Bake == null) continue;
                if (Classify(st, s.Zdo, scene, out int why, out _) != s.Cls || why != s.Why) { s_fixSkippedNotSame++; continue; }
                Fix(st, s.Zdo, scene, s.Cls, why);
            }
        }

        private static void Fix(ZoneState st, ZDO zdo, ZNetScene scene, Cls cls, int why)
        {
            // 1258 review (a): no fix while the zone is rebaking, held real or mid hand-back (the Both class's own guard, applied to every fix): restoring a piece's
            // renderers then would draw it twice beside the old bake. It is counted and found again by a later census once the zone settles.
            if (st.Dirty || st.HoldReal || st.AwaitingRecreate != null) { s_fixZoneBusy++; return; }
            var id = MeshBaker.PieceIdentity.From(zdo);
            var view = scene.FindInstance(zdo);
            string name = SafePrefab.Get(zdo.GetPrefab())?.name ?? zdo.GetPrefab().ToString();
            if (cls == Cls.Both && why == 1 && view != null)
            {
                var wnt = view.GetComponent<WearNTear>();
                if (wnt != null && !StillMatchesBake(st, wnt, id)) { HandBackChanged(st, wnt, id, InvulnerableClassifier.IsInvulnerable(wnt)); s_fixHandBackLook++; EasyBakeLog.Debug($"[BakeCensus] fix: {name} at {zdo.GetPosition()} was drawn twice and its look no longer matches the bake: handed back"); return; }
                MeshBaker.DisableForCacheHit(view.gameObject, st.Bake);   // the suppression a contributor gets at create (OnInstanceCreated)
                s_fixBoth++;
                EasyBakeLog.Debug($"[BakeCensus] fix: {name} at {zdo.GetPosition()} was drawn by the bake AND itself for 2 s+: its renderers are now suppressed like any contributor's");
                return;
            }
            if (cls == Cls.Neither && why == 3 && view != null)
            {
                var sup = view.GetComponent<EasyBakeSuppressedVisuals>();
                if (sup == null || sup.Restored) return;
                sup.RestoreVisuals();
                st.Bake.SuppressedPieces.Remove(sup);
                Object.DestroyImmediate(sup);
                s_fixRestored++;
                EasyBakeLog.Debug($"[BakeCensus] fix: {name} at {zdo.GetPosition()} was hidden by the bake but is not in it (drawn by nobody for 2 s+): its renderers are back");
                return;
            }
            if (cls == Cls.Neither && why == 1 && st.Skipped.TryGetValue(zdo, out var skipped))
            {
                // MaterialiseForDamage's hand-back without its hold: the stand-in stays until the real piece exists (FinishHandBacks)
                bool wholeZoneInFlight = st.AwaitingRecreate != null && st.AwaitingIdentities == null;
                if (st.AwaitingRecreate == null) { st.AwaitingRecreate = new List<ZDO>(1); st.AwaitingIdentities = new List<MeshBaker.PieceIdentity>(1); st.AwaitingSince = Time.unscaledTime; }
                zdo.Created = false; Patches.ZdoEventDirty.Mark();
                st.AwaitingRecreate.Add(zdo);
                if (!wholeZoneInFlight) st.AwaitingIdentities.Add(skipped.Identity);
                st.Skipped.Remove(zdo);
                st.LastChangeUnscaledTime = Time.unscaledTime;
                s_fixSkippedBack++;
                EasyBakeLog.Debug($"[BakeCensus] fix: {name} at {zdo.GetPosition()} was skipped but the bake does not draw it (drawn by nobody for 2 s+): handed back to vanilla, its stand-in stays until it exists");
            }
        }

        // ---- E26: live pieces the bake draws follow their ZDO ----
        private struct LiveRec { public uint Rev; public MeshBaker.PieceIdentity Id; public int Stamp; }
        private static readonly Dictionary<EasyBakeSuppressedVisuals, LiveRec> s_live = new Dictionary<EasyBakeSuppressedVisuals, LiveRec>();
        private static readonly List<EasyBakeSuppressedVisuals> s_liveScratch = new List<EasyBakeSuppressedVisuals>();
        private static readonly List<ZoneState> s_liveZones = new List<ZoneState>();
        private static int s_liveStamp, s_liveZoneCursor;
        private static float s_liveNextPass, s_liveNextLog, s_liveNextPurge;
        private static int s_followChecked, s_followLook, s_followMoved, s_followRematched;

        // 1258 review (c): sliced per piece under FollowBudgetMs with a resume point (zone cursor + piece cursor), not a whole zone per step
        private const double FollowBudgetMs = 0.5;
        private static readonly List<EasyBakeSuppressedVisuals> s_livePurge = new List<EasyBakeSuppressedVisuals>();
        private static ZoneState s_liveZoneNow;
        private static int s_livePieceCursor;

        private static void WatchLivePieces(float now)
        {
            if (s_liveZoneCursor >= s_liveZones.Count && s_livePieceCursor >= s_liveScratch.Count)
            {
                if (now < s_liveNextPass) { LogFollow(now); return; }
                s_liveNextPass = now + WatchSeconds;
                s_liveZones.Clear();
                foreach (var st in _zones.Values) if (st.ZoneActive && st.Baked && st.Bake?.SuppressedPieces != null && st.Bake.SuppressedPieces.Count > 0) s_liveZones.Add(st);
                s_liveZoneCursor = 0; s_liveStamp++;
                s_liveScratch.Clear(); s_livePieceCursor = 0; s_liveZoneNow = null;
            }
            long end = Stopwatch.GetTimestamp() + (long)(FollowBudgetMs * Stopwatch.Frequency / 1000.0);
            while (true)
            {
                if (s_livePieceCursor >= s_liveScratch.Count)
                {
                    if (s_liveZoneCursor >= s_liveZones.Count) break;
                    var st = s_liveZones[s_liveZoneCursor++];
                    s_liveScratch.Clear(); s_livePieceCursor = 0; s_liveZoneNow = st;
                    if (!st.ZoneActive || !st.Baked || st.Bake == null || st.Dirty) continue;   // a dirty zone rebakes from its real pieces anyway
                    s_liveScratch.AddRange(st.Bake.SuppressedPieces);   // the zone's list, copied once; walked over as many frames as it takes
                    continue;
                }
                var z = s_liveZoneNow;
                if (z == null || !z.ZoneActive || !z.Baked || z.Bake == null || z.Dirty) { s_liveScratch.Clear(); s_livePieceCursor = 0; continue; }
                FollowOne(z, s_liveScratch[s_livePieceCursor++]);
                if ((s_livePieceCursor & 3) == 0 && Stopwatch.GetTimestamp() > end) break;
            }
            if (now >= s_liveNextPurge)
            {
                s_liveNextPurge = now + 30f;
                s_livePurge.Clear();
                foreach (var kv in s_live) if (kv.Key == null || kv.Key.Restored || s_liveStamp - kv.Value.Stamp > 2) s_livePurge.Add(kv.Key);
                foreach (var k in s_livePurge) s_live.Remove(k);
            }
            LogFollow(now);
        }

        private static void FollowOne(ZoneState st, EasyBakeSuppressedVisuals sup)
        {
            if (sup == null || sup.Restored) return;
            var view = sup.GetComponent<ZNetView>();
            var zdo = view != null ? view.GetZDO() : null;
            var wnt = sup.GetComponent<WearNTear>();
            if (zdo == null || wnt == null) return;
            if (!s_live.TryGetValue(sup, out var rec))
            {
                s_live[sup] = new LiveRec { Rev = zdo.DataRevision, Id = MeshBaker.PieceIdentity.From(sup.gameObject, sup.transform.position), Stamp = s_liveStamp };
                return;
            }
            if (rec.Rev == zdo.DataRevision) { rec.Stamp = s_liveStamp; s_live[sup] = rec; return; }
            s_followChecked++;
            var id = MeshBaker.PieceIdentity.From(sup.gameObject, sup.transform.position);
            bool inv = InvulnerableClassifier.IsInvulnerable(wnt);
            if (!id.Equals(rec.Id) && st.Bake.ContributorIdentities != null && st.Bake.ContributorIdentities.Contains(rec.Id))
            {
                // it moved: the bake still draws it where it was. Its old identity leaves the bake; merged geometry rebakes. It stays real where it is now.
                s_live.Remove(sup);
                if (!HandBack(st, wnt, rec.Id)) { st.Dirty = true; st.LastChangeUnscaledTime = Time.unscaledTime; }
                s_followMoved++;
                EasyBakeLog.Debug($"[BakeFollow] {wnt.name} moved while the bake drew it: handed back{(st.Dirty ? " (merged: the zone rebakes)" : "")}");
                return;
            }
            if (st.Bake.ContributorIdentities != null && st.Bake.ContributorIdentities.Contains(id) && !StillMatchesBake(st, wnt, id))
            {
                s_live.Remove(sup);
                HandBackChanged(st, wnt, id, inv);   // OnInstanceCreated's own mismatch path: a group piece rejoins with its new look, merged geometry rebakes
                s_followLook++;
                EasyBakeLog.Debug($"[BakeFollow] {wnt.name}: its ZDO changed and its look or edits no longer match the bake: handed back");
                return;
            }
            rec.Rev = zdo.DataRevision; rec.Id = id; rec.Stamp = s_liveStamp; s_live[sup] = rec;
            s_followRematched++;
        }

        private static void LogFollow(float now)
        {
            if (now < s_liveNextLog) return;
            s_liveNextLog = now + 30f;
            if (s_followChecked > 0 && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[BakeFollow] 30 s: {s_followChecked} live baked piece(s) whose ZDO changed (each seen within about {WatchSeconds:0} s of the change): {s_followLook} handed back (look / edits), {s_followMoved} handed back (moved), {s_followRematched} still match the bake; watching {s_live.Count}");
            s_followChecked = s_followLook = s_followMoved = s_followRematched = 0;
        }
    }
}
