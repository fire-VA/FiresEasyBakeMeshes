using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.322 (SkipOnHost-safe, [lead] GO 11-11; design in my 11-11 answer): what "support" is, from vanilla WearNTear.UpdateSupport (the loop after
    // Physics.OverlapBoxNonAlloc): a collider with NO WearNTear in s_rayMask gives its neighbour maxSupport. EBM's stand-ins keep the piece's own layer
    // (StandInColliders.cs:87), so while a stand-in stands, its live neighbours are fully supported. The loss path is the TRANSITION: a stand-in removed
    // before its real piece exists and has computed its support, so a neighbour's next UpdateSupport finds nothing under it. So:
    //  1. FinishHandBacks drops a stand-in only after the real piece exists AND its WearNTear ran UpdateSupport once (SupportReady below);
    //  2. the real piece is created by vanilla's create pass BEFORE the stand-in goes (hand-back = Created = false, the stand-in stays meanwhile);
    //  3. a 30 s Info counter: pieces handed back, frames / seconds the stand-in was kept, hard-cap drops, and any WearNTear destroyed within 8 m and
    //     10 s of a stand-in drop (any cause: a hammer counts too; it is the alarm, not the proof).
    // SkipOnHost stays OFF by default; Fire decides after the throwaway-world test and the 1 h host soak.
    internal static partial class ZoneTracker
    {
        private const float RecreateHardCapSeconds = 60f;

        // WearNTears whose first UpdateSupport we are waiting for; the postfix only looks while this is non-empty (one Count check otherwise)
        private static readonly HashSet<int> s_awaitSupport = new HashSet<int>();
        private static readonly HashSet<int> s_supportDone = new HashSet<int>();

        // true when the instance has no WearNTear (nothing to support) or its WearNTear has run UpdateSupport since we started waiting
        private static bool SupportReady(ZNetView inst)
        {
            var wnt = inst.GetComponent<WearNTear>();
            if (wnt == null || !inst.IsOwner()) return true;   // not ours: its owner runs UpdateSupport (vanilla runs it only on the owner)
            int id = wnt.GetInstanceID();
            if (s_supportDone.Contains(id)) return true;
            s_awaitSupport.Add(id);
            return false;
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
        private static class WearNTear_UpdateSupport_SeenOnce
        {
            [HarmonyPostfix]
            private static void AfterUpdateSupport(WearNTear __instance)
            {
                if (s_awaitSupport.Count == 0 || __instance == null) return;
                int id = __instance.GetInstanceID();
                if (s_awaitSupport.Remove(id)) s_supportDone.Add(id);
            }
        }

        // ── the 30 s transition counters ──
        private static readonly Dictionary<ZoneState, int> s_awaitFrame = new Dictionary<ZoneState, int>();
        private static int c_handedBack, c_landings, c_hardCap, c_framesMax, c_brokeNear; private static long c_framesSum; private static float c_secsMax;
        private static int c_capKept, c_capKeptZones;   // 1.2.345: stand-ins kept past the cap for pieces the pacer had not created yet
        private static void NoteCapKept(int pieces) { c_capKept += pieces; c_capKeptZones++; }
        private static float s_nextTransitionReport;
        private struct Drop { public Vector3 Pos; public float Time; }
        private static readonly List<Drop> s_recentDrops = new List<Drop>();

        // ── 1.2.348 ([visual], [lead] 11-13 12:2x AM, player report on the PUBLISHED 1.2.293: half a base lost when the skip setting went off mid-base;
        // "all the glass windows exploded and a lot of beds, furniture, firepits") ──
        // Who breaks a piece: vanilla WearNTear.UpdateWear runs on the OWNER only (WearNTear.cs:617 "if (m_nview.IsOwner() && ShouldUpdate(time))"), calls
        // UpdateSupport and, when HaveSupport() is false, applies 100 % damage (:654-660, :776 ApplyDamage -> Destroy). With FGN's server-side simulation the
        // owner of every build piece is still a player's client ([fgn] 11-13), so this hold runs exactly there: a Harmony prefix on WearNTear.HaveSupport
        // answers "supported" for a piece standing in, or next to, a zone whose hand-back is in flight or landed / dropped its stand-ins less than
        // SupportHoldSeconds ago. 1.2.293 dropped a zone's stand-ins 20 s after the hand-back whether or not its pieces had come back
        // (ZoneTracker.cs:203 RecreateTimeoutSeconds = 20 in that tree), so the real pieces that had never been skipped (glass, beds, fire pits: damageable
        // or transparent, kept live) found nothing under them and broke; 1.2.322+ waits for the real piece and its first UpdateSupport, 1.2.345 keeps the
        // colliders of pieces still missing at the cap, and this closes what is left: the frames between a dropped stand-in and the neighbour's next
        // support read, and any drop path that is not the landing. Cost: one Count check per HaveSupport call while nothing is held.
        private const float SupportHoldSeconds = 10f;
        // 1.2.349 ([visual]; Fire's 1:0x AM run on 1.2.348: "support reads held 692 in 44 held zone(s) now" -> 53 while he stood still): a zone
        // whose stand-ins were dropped as it LEFT range got its 10 s hold and its ZoneState was removed from _zones right after, so the sweep that
        // walked _zones never cleared it: a permanent hold on every zone that ever left range. The hold now carries its own expiry (zone -> until),
        // the prefix checks the time itself, and a 30 s sweep drops expired entries whether or not the zone state still exists. A zone with a
        // hand-back in flight is refreshed every tick by FinishHandBacks, so it stays held exactly as long as the landing takes.
        private static readonly Dictionary<Vector2s, float> s_heldZones = new Dictionary<Vector2s, float>();
        private static readonly List<Vector2s> s_heldExpired = new List<Vector2s>();
        private static int c_supportHeld; private static float s_nextHoldSaid;
        // 1.2.351 ([visual]; Fire's 1:4x-2:0x AM runs on 1.2.349/350: "support reads held 1305 in 15 held zone(s) now" every 30 s while he STOOD STILL, i.e.
        // stand-ins dropped and zones re-held continuously with nothing moving = the hand-back churn Fire asked about): holds are counted per reason and the
        // zone held most often in the 30 s is named, so the next run says WHICH path churns (landed / landed at the cap / whole-zone stand-ins dropped /
        // one stand-in dropped) and where.
        private static readonly Dictionary<string, int> c_holdWhy = new Dictionary<string, int>();
        private static readonly Dictionary<Vector2s, int> c_holdZone = new Dictionary<Vector2s, int>();
        private static void NoteSupportHold(ZoneState state, string why)
        {
            state.SupportHoldUntil = Time.unscaledTime + SupportHoldSeconds;
            s_heldZones[state.Coord] = state.SupportHoldUntil;
            c_holdWhy.TryGetValue(why, out int w); c_holdWhy[why] = w + 1;
            c_holdZone.TryGetValue(state.Coord, out int z); c_holdZone[state.Coord] = z + 1;
        }
        private static string HoldCensus()
        {
            if (c_holdWhy.Count == 0) return "no new holds";
            var sb = new System.Text.StringBuilder("holds by reason: ");
            int n = 0; foreach (var kv in c_holdWhy) { if (n++ > 0) sb.Append(", "); sb.Append(kv.Value).Append(" x ").Append(kv.Key); }
            Vector2s top = default; int best = 0; foreach (var kv in c_holdZone) if (kv.Value > best) { best = kv.Value; top = kv.Key; }
            if (best > 0) sb.Append("; most held zone (").Append(top.x).Append(',').Append(top.y).Append(") x ").Append(best).Append(_zones.TryGetValue(top, out var ts) ? $" (baked {ts.Baked}, dirty {ts.Dirty}, skipped {ts.Skipped.Count}, stand-ins {ts.StandIns.Count}, hold-real {ts.HoldReal}, awaiting {(ts.AwaitingRecreate != null ? ts.AwaitingRecreate.Count : 0)})" : " (zone state gone)");
            c_holdWhy.Clear(); c_holdZone.Clear();
            return sb.ToString();
        }
        private static void RefreshSupportHold(ZoneState state) => s_heldZones[state.Coord] = Time.unscaledTime + SupportHoldSeconds;   // 1.2.349: in flight
        private static void SweepSupportHolds(float now)
        {
            if (s_heldZones.Count == 0) return;
            s_heldExpired.Clear();
            foreach (var kv in s_heldZones) if (kv.Value <= now) s_heldExpired.Add(kv.Key);
            for (int i = 0; i < s_heldExpired.Count; i++) s_heldZones.Remove(s_heldExpired[i]);
        }

        [HarmonyPatch(typeof(WearNTear), "HaveSupport")]
        private static class WearNTear_HaveSupport_HoldDuringHandBack
        {
            [HarmonyPrefix]
            private static bool BeforeHaveSupport(WearNTear __instance, ref bool __result)
            {
                if (s_heldZones.Count == 0 || __instance == null) return true;
                Vector3 p = __instance.transform.position;
                Vector2s z = ZoneSystem.GetZone(p);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        var zz = new Vector2s(z.x + dx, z.y + dy);
                        if (!s_heldZones.TryGetValue(zz, out float until) || until <= Time.unscaledTime) continue;   // 1.2.349: expired holds do not hold
                        c_supportHeld++;
                        __result = true;
                        if (Time.unscaledTime >= s_nextHoldSaid)
                        {
                            s_nextHoldSaid = Time.unscaledTime + 30f;
                            string st = _zones.TryGetValue(zz, out var hs) ? (hs.AwaitingRecreate != null ? $"hand-back in flight, {hs.AwaitingRecreate.Count} piece(s) awaited for {Time.unscaledTime - hs.AwaitingSince:0.0} s" : $"hold ends in {hs.SupportHoldUntil - Time.unscaledTime:0.0} s") : "zone state gone";
                            EasyBakeLog.InfoFirst("Transition support held", $"[Transition] support damage held for '{__instance.name.Replace("(Clone)", "")}' at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) (zone ({z.x},{z.y}), held zone ({zz.x},{zz.y}): {st}); its real support is read again once the hold ends ({c_supportHeld} hold(s) this session)");
                        }
                        return false;
                    }
                return true;
            }
        }

        // FinishHandBacks runs every tick; the first tick that sees a zone waiting stamps its frame
        private static void StampAwaiting(ZoneState state) { if (!s_awaitFrame.ContainsKey(state)) s_awaitFrame[state] = Time.frameCount; }

        private static void NoteHandBackLanded(ZoneState state, float now, bool hardCap)
        {
            int frames = s_awaitFrame.TryGetValue(state, out int f0) ? Time.frameCount - f0 : 0;
            s_awaitFrame.Remove(state);
            int n = state.AwaitingRecreate != null ? state.AwaitingRecreate.Count : 0;
            c_handedBack += n; c_landings++; c_framesSum += frames; if (frames > c_framesMax) c_framesMax = frames;
            float secs = now - state.AwaitingSince; if (secs > c_secsMax) c_secsMax = secs;
            if (hardCap) c_hardCap++;
            if (state.AwaitingRecreate != null)
                foreach (var zdo in state.AwaitingRecreate)
                    if (zdo != null && zdo.IsValid()) s_recentDrops.Add(new Drop { Pos = zdo.GetPosition(), Time = now });
            if (s_recentDrops.Count > 4096) s_recentDrops.RemoveRange(0, s_recentDrops.Count - 4096);
            // the waits of pieces that landed are over: nothing stays in the sets (a destroyed WearNTear's id is never reused while it lives)
            if (s_awaitSupport.Count == 0) s_supportDone.Clear();
        }

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static class WearNTear_Destroy_NearTransition
        {
            [HarmonyPrefix]
            private static void BeforeDestroy(WearNTear __instance)
            {
                if (s_recentDrops.Count == 0 || __instance == null) return;
                float now = Time.unscaledTime; Vector3 p = __instance.transform.position;
                for (int i = s_recentDrops.Count - 1; i >= 0; i--)
                {
                    var d = s_recentDrops[i];
                    if (now - d.Time > 10f) break;
                    if ((d.Pos - p).sqrMagnitude <= 64f)
                    {
                        c_brokeNear++;
                        EasyBakeLog.Warn($"[Transition] a piece ({__instance.name.Replace("(Clone)", "")}) at ({p.x:0.0},{p.y:0.0},{p.z:0.0}) was destroyed within {now - d.Time:0.0} s and 8 m of a stand-in hand-back (any cause; a hammer counts too)");
                        return;
                    }
                }
            }
        }

        internal static void TransitionReport(float now)
        {
            if (now < s_nextTransitionReport) return;
            s_nextTransitionReport = now + 30f;
            while (s_recentDrops.Count > 0 && now - s_recentDrops[0].Time > 10f) s_recentDrops.RemoveAt(0);
            SweepSupportHolds(now);   // 1.2.349
            if (c_landings == 0 && c_brokeNear == 0 && c_supportHeld == 0 && c_holdWhy.Count == 0) return;
            EasyBakeLog.InfoFirst("Transition 30 s", $"[Transition] last 30 s: {c_handedBack} piece(s) handed back in {c_landings} landing(s); stand-ins kept until the real piece existed and had its support: avg {(c_landings > 0 ? c_framesSum / c_landings : 0)} frame(s), max {c_framesMax} frame(s) / {c_secsMax:0.0} s; "
                + $"dropped at the {RecreateHardCapSeconds:0} s cap {c_hardCap}; kept past the cap {c_capKept} piece(s) in {c_capKeptZones} zone-period(s) (1.2.345: still waiting for the pacer); pieces destroyed near a hand-back {c_brokeNear}; support reads held {c_supportHeld} in {s_heldZones.Count} held zone(s) now; {HoldCensus()}");
            c_handedBack = c_landings = c_hardCap = c_framesMax = c_brokeNear = c_supportHeld = 0; c_framesSum = 0; c_secsMax = 0f; c_capKept = c_capKeptZones = 0;
        }
    }
}
