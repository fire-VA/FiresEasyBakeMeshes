using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.480 ([visual], lead 11-21 GO; Fire: "~60 FPS in town during vedr winter ... preventing rebuild churn from ebm and all our mods interacting";
    // Tools\_visual_EBM_WINTER_CHURN_20261121.md). MEASUREMENT ONLY: nothing here changes what EBM does.
    //  - "[Churn] EBM last 30 s": every way EBM redoes work, split by trigger (rebakes by site, hand-backs, the season re-check, snow re-reads by
    //    cause, per-piece pushes, snow holds, re-splits, zone re-announces by cause, looks uploads, pieces kept real by the winter rule).
    //  - "[Settle] EBM near work empty at T+x s": once per arrival (spawn / teleport, FillTracker's own start), the first moment EBM's near work has been
    //    empty for 3 s: no dirty zone, no recreate / stand-in / convert queue, an empty create pacer, no snow hold, no re-split for 3 s. 600 s cap.
    // Cost: integer increments at the existing sites; one Info line every 30 s while in a world, one per arrival. Key [90 - Debugging] ChurnLine.
    internal static class EbmChurn
    {
        internal static ConfigEntry<bool> Line;
        internal static bool On => Line != null && Line.Value;   // [2ic] 11-21: default OFF (a disk write per 30 s in every player's log is not needed there); the legs pin it on. Counters always run.

        internal static void Bind(ConfigFile config)
        {
            Line = EbmConfigMove.Bind(config, "90 - Debugging", "ChurnLine", false,
                "Info: one [Churn] line every 30 s counting why EBM redid work (rebakes, hand-backs, snow re-reads, pushes, re-announces, uploads) by trigger, and one [Settle] line per arrival when EBM's near work is empty. Measurement only.");
        }

        // ── rebakes: the six sites that set ZoneState.Dirty (ZoneTracker) ──
        internal enum Rebake { NewPiece = 0, MergedLook, MergedRemoved, CacheContent, CacheMissing, ChangedInstance }
        private static readonly string[] RebakeNames = { "new piece", "merged look change", "merged piece removed", "cache content mismatch", "cache pieces missing", "changed instance" };
        private static readonly int[] s_rebake = new int[6];
        internal static void NoteRebake(Rebake r) { s_rebake[(int)r]++; s_arrivalRebakes++; }

        // ── hand-backs (ShellPieces.Reason) ──
        private static readonly int[] s_handBack = new int[6];
        internal const int WatchHandBack = 5;   // ZoneTracker.ReleaseChangedInstance: a changed skipped piece, or one the winter keep radius wants real
        private static readonly string[] HandBackNames = { "look change / damage / repair / fire", "hammer highlight", "Ashlands burning", "removed", "stand-ins on the key", "watch (changed piece / winter radius)" };
        internal static void NoteHandBack(int reason, int n) { if (reason >= 0 && reason < s_handBack.Length) s_handBack[reason] += n; }

        // ── season re-check (Vedr VisualsChanged) ──
        private static int s_seasonEvents, s_seasonZones, s_seasonSkipped;
        internal static void NoteSeasonRecheck(int zones, int skipped) { s_seasonEvents++; s_seasonZones += zones; s_seasonSkipped += skipped; }

        // ── snow re-reads (SnowFeed.Refresh / Register), by cause ──
        internal enum Reread { Entry = 0, Raise, Invalidate, Revision, Register, Other }
        private static readonly string[] RereadNames = { "world entry", "raise", "invalidate", "revision poll", "source (un)registered", "other" };
        private static readonly int[] s_reread = new int[6];
        internal static void NoteReread(Reread r) => s_reread[(int)r]++;
        internal static Reread RereadOf(string why)
        {
            if (string.IsNullOrEmpty(why)) return Reread.Other;
            if (why.StartsWith("entered") || why.StartsWith("10 s after")) return Reread.Entry;
            if (why.StartsWith("InvalidateSnow")) return Reread.Invalidate;
            if (why.StartsWith("the snow source's revision")) return Reread.Revision;
            if (why.StartsWith("the snow source announced")) return Reread.Raise;
            return Reread.Other;
        }
        private static int s_groupsMarkedAll, s_areaEvents, s_areaGroups;
        internal static void NoteMarkAll(int groups) => s_groupsMarkedAll += groups;
        internal static void NoteArea(int groups) { s_areaEvents++; s_areaGroups += groups; }

        // ── per-piece values from the snow owner ──
        private static int s_pushChanged, s_shapeChanged, s_holdsReleased;
        internal static void NotePush() => s_pushChanged++;
        internal static void NoteShape() => s_shapeChanged++;
        internal static void NoteHoldReleased() => s_holdsReleased++;

        // ── re-splits (ZoneInstanceGroup re-split steps) ──
        private static long s_rsInstances, s_rsTicks, s_rsWorstFrameTicks, s_rsFrameTicks;
        private static int s_rsFrames, s_rsFrame = -1, s_rsSteps;
        private static float s_lastResplitAt = -1f;
        internal static void NoteResplit(int instances, long ticks)
        {
            int f = Time.frameCount;
            if (f != s_rsFrame) { s_rsFrame = f; s_rsFrames++; s_rsFrameTicks = 0; }
            s_rsFrameTicks += ticks;
            if (s_rsFrameTicks > s_rsWorstFrameTicks) s_rsWorstFrameTicks = s_rsFrameTicks;
            s_rsInstances += instances; s_rsTicks += ticks; s_rsSteps++;
            s_lastResplitAt = Time.unscaledTime;
        }

        // ── zone (re)announces to the snow owner (EbmApi.ZoneBaked), by cause ──
        internal enum Announce { CacheBuilt = 0, BackInRange, Baked, Invalidate }
        private static readonly string[] AnnounceNames = { "cache built", "back in range", "baked", "after invalidate" };
        private static readonly int[] s_announce = new int[4];
        internal static void NoteAnnounce(Announce a) => s_announce[(int)a]++;

        // ── looks buffer uploads (IndirectLooks.Upload) ──
        private static int s_upFrames; private static long s_upSlots;
        internal static void NoteLooksUpload(int slots) { s_upFrames++; s_upSlots += slots; }

        // ── far building copies (FarBuildingDraw gather start): first builds vs rebuilds of a target that already drew ──
        private static int s_farFirst, s_farRebuild;
        internal static void NoteFarBuild(bool rebuild) { if (rebuild) s_farRebuild++; else s_farFirst++; }

        // ── once a frame from ZoneTracker.Update ──
        private static float s_nextLine;
        internal static void Tick(float now)
        {
            if (!On) return;
            SettleTick(now);
            if (Player.m_localPlayer == null) { s_nextLine = now + 30f; return; }
            if (s_nextLine <= 0f) { s_nextLine = now + 30f; return; }
            if (now < s_nextLine) return;
            s_nextLine = now + 30f;
            EasyBakeLog.Info(BuildLine());
            Reset();
        }

        private static string BuildLine()
        {
            double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var sb = new StringBuilder(512);
            int rb = 0; foreach (int v in s_rebake) rb += v;
            // 1.2.482 ([lead] 11-21: no live-ref count exists in any fps window; [Heap] logs at Info only on an 8 MB change, EbmHeapCensus.cs:15 / :133):
            // the counts NOW, after the first field so they land on the line's first physical line; the head and "rebakes queued N" stay byte-identical
            // ([2ic]: 22 landed tests parse "EBM last 30 s: rebakes queued (?<n>\d+)")
            sb.Append("[Churn] EBM last 30 s: rebakes queued ").Append(rb);
            if (rb > 0) { sb.Append(" ("); Parts(sb, RebakeNames, s_rebake); sb.Append(')'); }
            ZoneTracker.ChurnPieceCounts(out int realNow, out int skippedNow, out int groupsNow, out int zonesNow);
            sb.Append(" (now: real pieces ").Append(realNow).Append(", skipped ").Append(skippedNow).Append(", baked groups ").Append(groupsNow).Append(" in ").Append(zonesNow).Append(" zone(s))");
            sb.Append("; zones baked fresh ").Append(ZoneTracker.ChurnBakedFresh(true)).Append(", from cache ").Append(ZoneTracker.ChurnFromCache(true));
            int hb = 0; foreach (int v in s_handBack) hb += v;
            sb.Append("; hand-backs ").Append(hb);
            if (hb > 0) { sb.Append(" ("); Parts(sb, HandBackNames, s_handBack); sb.Append(')'); }
            sb.Append("; season re-checks ").Append(s_seasonEvents).Append(" (").Append(s_seasonZones).Append(" zone(s), ").Append(s_seasonSkipped).Append(" skipped piece(s))");
            sb.Append("; winter-kept pieces (season so far) ").Append(SkipEligibility.WinterKeptCount);
            int rr = 0; foreach (int v in s_reread) rr += v;
            sb.Append("; snow re-reads ").Append(rr);
            if (rr > 0) { sb.Append(" ("); Parts(sb, RereadNames, s_reread); sb.Append(')'); }
            sb.Append(" -> ").Append(s_groupsMarkedAll).Append(" group(s) marked; area events ").Append(s_areaEvents).Append(" (").Append(s_areaGroups).Append(" group(s))");
            sb.Append("; per-piece pushes changed ").Append(s_pushChanged).Append(", shape changes ").Append(s_shapeChanged).Append(", snow holds released ").Append(s_holdsReleased);
            sb.Append("; re-split ").Append(s_rsInstances).Append(" instance(s) in ").Append(s_rsSteps).Append(" group step(s) over ").Append(s_rsFrames).Append(" frame(s), ").Append(ms(s_rsTicks).ToString("0.0")).Append(" ms total, worst frame ").Append(ms(s_rsWorstFrameTicks).ToString("0.0")).Append(" ms");
            int an = 0; foreach (int v in s_announce) an += v;
            sb.Append("; re-announces ").Append(an);
            if (an > 0) { sb.Append(" ("); Parts(sb, AnnounceNames, s_announce); sb.Append(')'); }
            sb.Append("; far copies built ").Append(s_farFirst).Append(", rebuilt ").Append(s_farRebuild);
            sb.Append("; empty-zone teardowns ").Append(s_emptyTeardowns).Append(" (").Append(s_emptyUndelivered).Append(" undelivered), held for delivery ").Append(s_emptyHeld.Count).Append(" zone(s), wait cap reached ").Append(s_emptyCap);   // 1.2.481 F1
            sb.Append("; storm cap decisions left baked beyond the keep radius ").Append(s_stormCapSkipped);   // 1.2.481 F2
            sb.Append("; ").Append(ZoneTracker.MakeRealChurn());   // 1.2.482 ([2ic] proof MUST: make-real counts readable at Info)
            sb.Append("; ").Append(ZoneTracker.GraceChurn());   // 1.2.482 support grace (piece-loss proof)
            sb.Append("; looks uploads ").Append(s_upFrames).Append(" frame(s), ").Append(s_upSlots).Append(" slot(s) = ").Append((s_upSlots * 16 / 1024.0).ToString("0")).Append(" KB");
            return sb.ToString();
        }

        private static void Parts(StringBuilder sb, string[] names, int[] counts)
        {
            bool first = true;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == 0) continue;
                if (!first) sb.Append(", ");
                sb.Append(names[i]).Append(' ').Append(counts[i]);
                first = false;
            }
        }

        private static void Reset()
        {
            System.Array.Clear(s_rebake, 0, s_rebake.Length); System.Array.Clear(s_handBack, 0, s_handBack.Length);
            System.Array.Clear(s_reread, 0, s_reread.Length); System.Array.Clear(s_announce, 0, s_announce.Length);
            s_seasonEvents = s_seasonZones = s_seasonSkipped = 0;
            s_groupsMarkedAll = s_areaEvents = s_areaGroups = 0;
            s_pushChanged = s_shapeChanged = s_holdsReleased = 0;
            s_rsInstances = s_rsTicks = s_rsWorstFrameTicks = 0; s_rsFrames = 0; s_rsSteps = 0; s_farFirst = s_farRebuild = 0;
            s_upFrames = 0; s_upSlots = 0;
            s_emptyTeardowns = s_emptyUndelivered = s_emptyCap = s_stormCapSkipped = 0; s_emptyHeld.Clear();   // 1.2.481
        }

        // ── per-zone build histogram for the current arrival ([2ic] / Fire: "everything builds once correctly"): a fresh bake or a cache build counts as one build ──
        // lead 11-21 (Fire: "a chunk of buildings rebuilt 2-3 times from the time we load in until we settle fully"): every build of a zone since the
        // arrival, in order, with what caused it: "fresh" (first bake from live pieces), "cache", "zdo" (ZDO-file first build), "zdo swap" (this-visit swap
        // adds the file's batches), or "rebake: <A1-A6 site>". The cause of a rebake is the site that set Dirty (ZoneTracker passes it at the bake).
        private static readonly System.Collections.Generic.Dictionary<Vector2s, System.Collections.Generic.List<string>> s_builds = new System.Collections.Generic.Dictionary<Vector2s, System.Collections.Generic.List<string>>();
        internal static void NoteZoneBuilt(Vector2s zone, string how)
        {
            if (!On || !s_watching) return;
            if (!s_builds.TryGetValue(zone, out var seq)) { seq = new System.Collections.Generic.List<string>(2); s_builds[zone] = seq; }
            if (seq.Count < 12) seq.Add(how); else if (seq.Count == 12) seq.Add("...");
            if (how == "zdo swap") s_arrivalSwaps++;   // 1.2.481 ([2ic]): per-arrival ZDO this-visit swaps in [Settle]
        }
        internal static string RebakeName(Rebake r) => "rebake: " + RebakeNames[(int)r];
        private static string BuildHistogram()
        {
            int once = 0, twice = 0, more = 0;   // 1.2.481: + "zdo swaps N" after the three counts
            foreach (var kv in s_builds) { int n = kv.Value.Count; if (n <= 1) once++; else if (n == 2) twice++; else more++; }
            var sb = new StringBuilder();
            sb.Append("zones built once ").Append(once).Append(", twice ").Append(twice).Append(", 3+ times ").Append(more).Append(", zdo swaps ").Append(s_arrivalSwaps);
            if (twice + more > 0)
            {
                var top = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Vector2s, System.Collections.Generic.List<string>>>(s_builds);
                top.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
                sb.Append("; most: ");
                for (int i = 0; i < top.Count && i < 5 && top[i].Value.Count > 1; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append('(').Append(top[i].Key.x).Append(',').Append(top[i].Key.y).Append(") ").Append(top[i].Value.Count).Append("x: ").Append(string.Join(" > ", top[i].Value.ToArray()));
                }
            }
            return sb.ToString();
        }

        // ── 1.2.480 [08 - Snow And Seasons] WinterKeepRadius (lead 11-21 with [seasons]: Vedr hides snow caps beyond 60 m, SnowLayer.cs:286) ──
        internal static ConfigEntry<float> WinterKeepRadius;
        internal static void BindWinterKeep(ConfigFile config)
        {
            WinterKeepRadius = EbmConfigMove.Bind(config, "08 - Snow And Seasons", "WinterKeepRadius", 0f, new ConfigDescription(
                "Metres. 0 = off (as before): in Vedr's winter snow biomes every piece that can carry a snow cap stays real (not baked). Above 0: only within this distance of the player; farther pieces bake as in summer and keep Vedr's snow colour (Vedr draws no caps past 60 m). A/B lever, default off.",
                new AcceptableValueRange<float>(0f, 500f)));
            WinterKeepRadius.SettingChanged += (_, __) => SayWinterKeep();
            SayWinterKeep();
        }
        private static void SayWinterKeep()
        {
            float r = WinterKeepRadius != null ? WinterKeepRadius.Value : 0f;
            EasyBakeLog.Info(r > 0f
                ? $"[Skip] winter keep radius {r:0} m: cap-capable pieces in Vedr winter snow biomes stay real only within {r:0} m of the player; farther ones skip and bake"
                : "[Skip] winter keep radius off (0): cap-capable pieces in Vedr winter snow biomes stay real everywhere (as before 1.2.480)");
        }

        // ── 1.2.481 F1 [09 - Disk Cache] EmptyZoneWaitsForDelivery + EmptyZoneWaitMaxSeconds, F2 [08 - Snow And Seasons] WinterKeepRadiusStormCaps (all default OFF) ──
        internal static ConfigEntry<bool> EmptyWait, StormCaps;
        internal static ConfigEntry<float> EmptyWaitMax;
        internal static bool EmptyWaitOn => EmptyWait != null && EmptyWait.Value;
        internal static float EmptyWaitMaxSeconds => EmptyWaitMax != null ? EmptyWaitMax.Value : 60f;
        internal static bool StormCapsOn => StormCaps != null && StormCaps.Value;
        internal static void BindLevers481(ConfigFile config)
        {
            EmptyWait = EbmConfigMove.Bind(config, "09 - Disk Cache", "EmptyZoneWaitsForDelivery", false,
                "A zone built from its saved bake waits for the server to send its pieces instead of being torn down and rebuilt every 10 s while it is empty (at most EmptyZoneWaitMaxSeconds; a zone the server says is empty is still torn down). A/B lever, default off.");
            EmptyWaitMax = EbmConfigMove.Bind(config, "09 - Disk Cache", "EmptyZoneWaitMaxSeconds", 60f, new ConfigDescription(
                "Seconds. The longest a zone built from its saved bake waits for its pieces (EmptyZoneWaitsForDelivery) before today's empty-zone teardown runs.",
                new AcceptableValueRange<float>(10f, 600f)));
            StormCaps = EbmConfigMove.Bind(config, "08 - Snow And Seasons", "WinterKeepRadiusStormCaps", false,
                "With WinterKeepRadius above 0: a piece with snow on it (a storm) stays baked beyond that radius instead of becoming real for its snow cap (caps are not shown that far). A/B lever, default off.");
            EmptyWait.SettingChanged += (_, __) => SayLevers481(); EmptyWaitMax.SettingChanged += (_, __) => SayLevers481(); StormCaps.SettingChanged += (_, __) => SayLevers481();
            SayLevers481();
        }
        private static void SayLevers481()
        {
            EasyBakeLog.Info(EmptyWaitOn
                ? $"[Cache] empty zone waits for delivery on (max {EmptyWaitMaxSeconds:0} s): a zone built from its saved bake is kept while the server has not sent its sector"
                : "[Cache] empty zone waits for delivery off: empty zones built from their saved bake are torn down after 10 s (as before 1.2.481)");
            EasyBakeLog.Info(StormCapsOn
                ? "[Skip] storm caps honour the winter keep radius on: beyond WinterKeepRadius a piece with snow on it stays baked"
                : "[Skip] storm caps honour the winter keep radius off: a piece with snow on it becomes real everywhere (as before 1.2.481)");
        }
        // F1 / F2 counters ([Churn] 30 s): teardowns (of them undelivered = the cap fired or the key is off), distinct zones held, cap reached; storm caps left baked
        private static int s_emptyTeardowns, s_emptyUndelivered, s_emptyCap, s_stormCapSkipped;
        private static readonly System.Collections.Generic.HashSet<Vector2s> s_emptyHeld = new System.Collections.Generic.HashSet<Vector2s>();
        internal static void NoteEmptyTeardown(bool undelivered) { s_emptyTeardowns++; if (undelivered) s_emptyUndelivered++; }
        internal static void NoteEmptyHeld(Vector2s zone) => s_emptyHeld.Add(zone);
        internal static void NoteEmptyCapReached() => s_emptyCap++;
        internal static void NoteStormCapSkipped() => s_stormCapSkipped++;

        // ── [Settle]: one line per arrival ──
        private const float QuietSeconds = 3f, CapSeconds = 600f;
        private static bool s_watching;
        private static string s_why;
        private static float s_start, s_quietSince = -1f;
        private static int s_arrivalRebakes, s_freshAtStart, s_cacheAtStart, s_arrivalSwaps;

        /// <summary>FillTracker's arrival start (after the spawn / after a teleport).</summary>
        internal static void Arrival(string why)
        {
            if (!On) return;
            s_watching = true; s_why = why; s_start = Time.unscaledTime; s_quietSince = -1f; s_arrivalRebakes = 0; s_arrivalSwaps = 0; s_builds.Clear();
            s_freshAtStart = ZoneTracker.ChurnBakedFresh(false); s_cacheAtStart = ZoneTracker.ChurnFromCache(false);
        }

        private static void SettleTick(float now)
        {
            if (!s_watching) return;
            bool resplitQuiet = s_lastResplitAt < 0f || now - s_lastResplitAt >= QuietSeconds;
            // the arrival's own fill (FillTracker: the instance count settled) must have ended first, or an empty first second would "settle" before anything streamed in
            bool empty = !FillTracker.Watching && !ZoneTracker.ChurnAnyWorkQueued() && StreamPacer.QueueCount == 0 && EbmSnowStore.HoldCount == 0 && resplitQuiet;
            if (empty) { if (s_quietSince < 0f) s_quietSince = now; }
            else s_quietSince = -1f;
            float t = now - s_start;
            bool settled = s_quietSince >= 0f && now - s_quietSince >= QuietSeconds;
            if (!settled && t < CapSeconds) return;
            s_watching = false;
            int fresh = ZoneTracker.ChurnBakedFresh(false) - s_freshAtStart, cache = ZoneTracker.ChurnFromCache(false) - s_cacheAtStart;
            EasyBakeLog.Info(settled
                ? $"[Settle] EBM near work empty at T+{s_quietSince - s_start:0.0} s {s_why} (quiet {QuietSeconds:0} s): zones baked fresh {fresh}, from cache {cache}, rebakes queued {s_arrivalRebakes}; {BuildHistogram()}; winter-kept pieces {SkipEligibility.WinterKeptCount}; last re-split at {(s_lastResplitAt < s_start ? "none since arrival" : "T+" + (s_lastResplitAt - s_start).ToString("0.0") + " s")}"
                : $"[Settle] EBM near work NOT empty after {CapSeconds:0} s {s_why}: {ZoneTracker.ChurnQueuesSummary()}, create pacer {StreamPacer.QueueCount}, snow holds {EbmSnowStore.HoldCount}, last re-split {(s_lastResplitAt < 0f ? "never" : (now - s_lastResplitAt).ToString("0.0") + " s ago")}; zones baked fresh {fresh}, from cache {cache}, rebakes queued {s_arrivalRebakes}; {BuildHistogram()}");
        }
    }
}
