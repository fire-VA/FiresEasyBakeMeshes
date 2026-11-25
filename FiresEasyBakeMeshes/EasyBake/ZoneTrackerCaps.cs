namespace FiresEasyBakeMeshes.EasyBake
{
    internal static partial class ZoneTracker
    {
        // 1.2.505 ([visual], piece snow caps v1): once per frame from DrawInstances, after the group walk (full or settled) and the indirect EndFrame.
        // Every zone whose groups draw this frame (the full walk's gate: ZoneActive, Baked, a Ready bake with instance groups) draws its snow caps on
        // the instanced path, whether its groups went indirect or not ([2ic] MUST 1). Nothing registered or the key off = one check, no walk.
        private static void DrawCaps()
        {
            if (!PieceCaps.AnyRegistered || !PieceCaps.Active) { CapsInactive(); return; }
            PieceCaps.BeginFrame();
            RetryNewCapKinds();   // 1.2.514
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked) continue;
                var bake = state.Bake;
                if (bake == null || !bake.Ready || bake.InstanceGroups.Count == 0) continue;
                PieceCaps.DrawBake(bake, bake.ShowingFarTier);
            }
            PieceCaps.Summary();
        }

        // 1.2.514 ([2ic] SHOULD on the arrival order): the skip is decided when a piece is created, but the season mod registers a kind's cap only after it
        // built it (from a live piece's Awake or EBM's announce), so on a session's first arrival most snowed pieces were decided real before their kind
        // had a cap. When kinds are newly registered, their live baked pieces go to the zone's ConvertQueue - the same live-to-skip path a fresh bake
        // proposes (RequeueConversions); ConvertLivePieces' own tests (ZdoMatches, now with CapsCoverWinter) decide, under its frame budget. At most one
        // pass per 0.5 s, one walk over the baked zones' real pieces for all kinds registered meanwhile.
        private static float s_nextCapRetry;
        private static readonly System.Collections.Generic.HashSet<int> s_retryKinds = new System.Collections.Generic.HashSet<int>();   // v4: qualified (this file has no usings; v3 build CS0246)
        private static void RetryNewCapKinds()
        {
            if (PieceCaps.s_newKinds.Count == 0) return;
            float now = UnityEngine.Time.unscaledTime;
            if (now < s_nextCapRetry) return;
            s_nextCapRetry = now + 0.5f;
            if (!SkipCreationActive() || ShellPieces.On) return;   // RequeueConversions' own gate, BEFORE the drain ([2ic] nit a): kinds wait until it opens
            s_retryKinds.Clear(); foreach (int h in PieceCaps.s_newKinds) s_retryKinds.Add(h); PieceCaps.s_newKinds.Clear();
            int queued = 0;
            foreach (var state in _zones.Values)
            {
                if (!state.ZoneActive || !state.Baked || state.Bake?.ContributorIdentities == null) continue;
                foreach (var piece in state.Pieces)
                {
                    if (piece == null) continue;
                    var view = piece.GetComponent<ZNetView>(); var zdo = view != null ? view.GetZDO() : null;
                    if (zdo == null || !s_retryKinds.Contains(zdo.GetPrefab())) continue;
                    if (!InvulnerableClassifier.IsInvulnerable(piece) && !SkipEligibility.DamageableSkipAllowed) continue;
                    if (!state.Bake.ContributorIdentities.Contains(MeshBaker.PieceIdentity.From(zdo))) continue;
                    state.ConvertQueue.Enqueue(piece); queued++;
                }
            }
            PieceCaps.RetriedPieces += queued; PieceCaps.RetriedPasses++;
        }

        // [2ic] MUST 1 (1.2.505 review): caps not drawn this frame (nothing registered, key off, EBM inert, or instancing off - DrawInstances returns
        // before the walk then and calls this itself): every claimed live cap is given back at once. ClaimedNow is 0 after one pass: not per-frame work.
        private static void CapsInactive()
        {
            if (EasyBakeSuppressedVisuals.ClaimedNow == 0) return;
            PieceCaps.UnclaimWhere(-1, "caps inactive");
            SkipEligibility.CapsCoverChanged("caps inactive");   // 1.2.514
            PieceCaps.Revision++; PieceCaps.RebuildGen++;   // every list rebuilds (and re-claims) when caps draw again (1.2.507: RebuildGen; Revision alone only regroups now)
        }
    }
}
