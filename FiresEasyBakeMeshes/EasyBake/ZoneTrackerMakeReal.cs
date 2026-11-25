using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.482 ([visual], [lead] 11-21: "a real integration bug (Vedr's shovel-make-real silently does nothing today)"): the provider side of the make-real
    // contract that Vedr's BakedPieceSnow has bound by reflection since 0.3.x (Vedr BakedPieceSnow.cs:57-73: FiresEasyBakeMeshes.EbmStandIns.MakeReal(ZDOID,
    // string) and StandsInFor(Collider, out ZDOID)). Neither existed in EBM, so s_makeReal stayed null and every "shovel hit", "snow shovel within 4 m" and
    // "full snow load" make-real returned false. It was masked only because EBM's ZDO-snow rule (SkipEligibility.SnowKeepsReal) made snowy pieces real anyway.
    //
    // What MakeReal does: a skipped (baked) piece is handed back the one-piece way damage already uses (MaterialiseForDamage: zdo.Created = false, its
    // stand-in stays until the real piece exists and has its support, ZoneTrackerSupportSafe), and the piece is KEPT real for the rest of the session:
    // SkipEligibility.ZdoMatches refuses it first, so no path skips it again (creation TrySkipCreation, conversion ConvertLivePieces, the skipped-piece
    // watch, late join). A piece that is already real is only kept real. Session-long on purpose: Vedr asks once per piece (its s_askedReal answers "true"
    // to every later ask without calling), so a released keep would leave Vedr believing a re-baked piece is real. Bounded: at KeepCap pieces new asks are
    // refused (false, so Vedr sees it), the set is cleared with the world (ZoneTracker.Reset). Main thread only (Vedr calls from Update).
    internal static partial class ZoneTracker
    {
        private const int KeepCap = 20000;
        private const float MakeRealHoldSeconds = 10f;   // the zone hold MaterialiseForDamage sets, as DamageReplay's HoldSecondsAfterHit
        private static readonly HashSet<ZDOID> s_keptReal = new HashSet<ZDOID>();
        private static int c_madeReal, c_keptAlreadyReal, c_refusedNotOurs, c_refusedCap;
        // [2ic] 11-21: handed back BY REASON (the caller's why: "Vedr: shovel hit", "Vedr: snow shovel within 4 m", "Vedr: full snow load"), and the
        // SkippedAt lookup cost (calls, matches, ms) so a profile / census row shows it
        private static readonly Dictionary<string, int> c_madeRealWhy = new Dictionary<string, int>();
        private static int c_skippedAtCalls, c_skippedAtHits; private static long c_skippedAtTicks;

        /// <summary>True when the snow owner (or another caller of EbmStandIns.MakeReal) asked this piece to stay real this session.</summary>
        internal static bool KeptReal(ZDO zdo) => s_keptReal.Count > 0 && zdo != null && s_keptReal.Contains(zdo.m_uid);

        /// <summary>The skipped piece an EBM stand-in at (prefab, position) stands in for, or null (the same 5 cm match as EbmStandIns.StandsInFor).</summary>
        internal static ZDO SkippedAt(int prefabHash, Vector3 position)
        {
            c_skippedAtCalls++;
            if (!_zones.TryGetValue(EbmZones.Of(position), out var state)) return null;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ZDO found = null;
            foreach (var kv in state.Skipped)
            {
                var z = kv.Key;
                if (z != null && z.GetPrefab() == prefabHash && (z.GetPosition() - position).sqrMagnitude < 0.0025f) { found = z; break; }
            }
            c_skippedAtTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (found != null) c_skippedAtHits++;
            return found;
        }

        /// <summary>EbmStandIns.MakeReal. True = the piece is real now or is being created and will stay real; false = EBM does not hold that piece
        /// (unknown ZDO, a zone EBM does not track, a piece it neither skips nor holds live) or the keep cap is reached.</summary>
        internal static bool MakeRealFor(ZDOID id, string why)
        {
            if (id == ZDOID.None || ZDOMan.instance == null) { c_refusedNotOurs++; return false; }
            var zdo = ZDOMan.instance.GetZDO(id);
            if (zdo == null || !_zones.TryGetValue(EbmZones.Of(zdo.GetPosition()), out var state)) { c_refusedNotOurs++; return false; }
            if (s_keptReal.Contains(id)) return true;
            if (s_keptReal.Count >= KeepCap) { c_refusedCap++; EasyBakeLog.InfoFirst("MakeReal cap", $"[Skip] make-real keep list full ({KeepCap} pieces this session): further make-real asks are refused until the world reloads"); return false; }
            bool skipped = state.Skipped.ContainsKey(zdo);
            if (skipped)
            {
                if (!MaterialiseForDamage(zdo, MakeRealHoldSeconds)) { c_refusedNotOurs++; return false; }
                c_madeReal++;
                string key = string.IsNullOrEmpty(why) ? "(no reason given)" : why;
                c_madeRealWhy.TryGetValue(key, out int n); c_madeRealWhy[key] = n + 1;
            }
            else c_keptAlreadyReal++;   // real (or being created): keep it from being converted back
            s_keptReal.Add(id);
            EasyBakeLog.InfoFirst("MakeReal first", $"[Skip] make-real: a piece was made real on request and is kept real this session ('{why}'; EbmStandIns.MakeReal; repeating line: the next ones are Debug)");
            EasyBakeLog.Debug($"[Skip] make-real ({why}): {zdo.GetPrefab()} at {zdo.GetPosition()} {(skipped ? "handed back" : "already real, kept real")}");
            return true;
        }

        /// <summary>For the [Skip] session summary.</summary>
        internal static string MakeRealSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(" Make-real asks (EbmStandIns.MakeReal): ").Append(c_madeReal).Append(" handed back");
            if (c_madeRealWhy.Count > 0)
            {
                sb.Append(" (");
                int i = 0; foreach (var kv in c_madeRealWhy) { if (i++ > 0) sb.Append(", "); sb.Append(kv.Value).Append(" '").Append(kv.Key).Append('\''); }
                sb.Append(')');
            }
            sb.Append(", ").Append(c_keptAlreadyReal).Append(" already real and kept, ").Append(c_refusedNotOurs).Append(" refused (not a piece EBM holds), ")
              .Append(c_refusedCap).Append(" refused at the cap; ").Append(s_keptReal.Count).Append(" kept real now; stand-in lookups (StandsInFor) ")
              .Append(c_skippedAtCalls).Append(", ").Append(c_skippedAtHits).Append(" matched, ")
              .Append((c_skippedAtTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.00")).Append(" ms total.");
            return sb.ToString();
        }

        /// <summary>1.2.482 ([2ic] proof MUST): the make-real counts every 30 s on the [Churn] line (Info when ChurnLine is on), so the A/B reads them without Debug.</summary>
        internal static string MakeRealChurn() => $"make-real this session: handed back {c_madeReal} ({c_madeRealWhy.Count} reason(s)), kept real {s_keptReal.Count}, stand-in lookups {c_skippedAtCalls}";

        private static void MakeRealReset()
        {
            s_keptReal.Clear(); c_madeRealWhy.Clear();
            c_madeReal = c_keptAlreadyReal = c_refusedNotOurs = c_refusedCap = 0;
            c_skippedAtCalls = c_skippedAtHits = 0; c_skippedAtTicks = 0;
        }
    }
}
