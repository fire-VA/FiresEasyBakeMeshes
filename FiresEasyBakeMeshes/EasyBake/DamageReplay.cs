using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ═══ THE HIT THAT ARRIVES FOR A PIECE THAT DOES NOT EXIST ═════════════════
    // Damage in Valheim is owner-authoritative and routed. Nobody applies damage locally:
    //
    //     WearNTear.Damage(hit)   =>  m_nview.InvokeRPC("RPC_Damage", hit)          // fire and forget
    //     WearNTear.RPC_Damage    =>  returns unless m_nview.IsOwner()              // only the owner acts
    //
    // So a piece this client has not created is still damageable - as long as somebody else owns it. When THIS
    // client owns it, the call comes home and vanilla throws it away without a word:
    //
    //     // ZRoutedRpc.HandleRoutedRPC
    //     ZDO zDO = ZDOMan.instance.GetZDO(data.m_targetZDO);
    //     if (zDO != null) {
    //         ZNetView zNetView = ZNetScene.instance.FindInstance(zDO);
    //         if (zNetView != null) zNetView.HandleRoutedRPC(data);   // NO else. No log. Gone.
    //     }
    //
    // That silence is the most dangerous failure this whole feature can have - "damage sometimes does not
    // register", with nothing in any log to find it by. Refusing to skip what we own does not fix it either:
    // measured 2026-09-25 in the BlueHills town, 20,777 of 38,154 building pieces (54.5%) were client-owned,
    // and ZDOMan REASSIGNS ownership by active area
    //
    //     if ((!zdo.HasOwner() || !IsInPeerActiveArea(position, zdo.GetOwner())) && ZNetScene.InActiveArea(...))
    //         zdo.SetOwner(uid);
    //
    // so pieces flip to ours continuously as the character walks. Every flip would force a materialisation
    // while moving, which is the exact cost the feature exists to remove.
    //
    // So: catch the hit, bring that one piece back, and replay the original call into it once it exists.
    //
    // WHY ORDER AND MULTIPLICITY MATTER. Creation is budgeted - vanilla's CreateDestroyObjects spreads it over
    // frames - so the gap between "hit arrived" and "instance exists" is tens of milliseconds, and a mob
    // swinging at a wall lands more than one blow in that window. A single slot per ZDOID would drop all but
    // one, and replaying out of order would apply the second blow's HitData to the first blow's health. Hence a
    // LIST per ZDOID, drained in arrival order.
    //
    // NOTHING HERE FIRES YET. It only triggers for a ZDO that EBM is holding back AND that takes damage, and no
    // damageable piece is held back until the phase 3 config exists. A routed RPC for a missing instance that
    // is NOT one of ours is left strictly alone - that happens in vanilla for its own reasons, and claiming it
    // would be masking somebody else's behaviour.
    internal static class DamageReplay
    {
        // Held real this long after a hit, so the piece cannot be re-skipped between materialising and the
        // replay landing. It does not need to cover the piece's whole damaged life: applying damage writes
        // health into the ZDO, which bumps DataRevision, and a piece whose stored health no longer matches its
        // bake stops being eligible on its own.
        private const float HoldSecondsAfterHit = 10f;

        // ═══ TWO DEADLINES, BECAUSE "SLOW" AND "LOST" ARE DIFFERENT FACTS ══════
        // The first version had one 5-second deadline and called everything past it lost. Measured 2026-09-25,
        // swinging a toolTier-10 sword through the BlueHills town: 6 hits caught, 5 replayed, 1 reported LOST -
        // and the deadline was what failed, not the interception. Creation is budgeted
        // (CreateBudgetPerFrame 40) and vanilla chooses WHICH objects to create by distance, so a piece stops
        // being a near candidate the moment the character walks on - which an auto-walk route does at 5 m/s
        // immediately after the swing.
        //
        // So: warn when it is slow, and only claim loss after long enough that no budget or GC spike explains
        // it. A hit that lands late still lands - health goes into the ZDO and the look follows - and for a
        // building late is enormously better than lost.
        private const float SlowReplayWarnSeconds = 5f;
        private const float AbandonSeconds = 60f;

        private static readonly int DamageMethodHash = "RPC_Damage".GetStableHashCode();

        private sealed class Pending
        {
            public readonly List<ZRoutedRpc.RoutedRPCData> Hits = new List<ZRoutedRpc.RoutedRPCData>(2);
            public float FirstQueuedAt;
            public bool Warned;
        }

        private static readonly Dictionary<ZDOID, Pending> _pending = new Dictionary<ZDOID, Pending>();
        private static readonly List<ZDOID> _drained = new List<ZDOID>();

        private static int s_captured, s_replayed, s_lostToTimeout, s_multiHit, s_targetGone, s_slow;
        private static float s_worstWaitSeconds;

        internal static bool Idle => _pending.Count == 0;

        // Called from a postfix on ZRoutedRpc.HandleRoutedRPC - AFTER vanilla has had its turn, so this can
        // only ever see a call vanilla already declined to deliver. A prefix would have to decide whether to
        // suppress, and suppressing is both unnecessary and a way to break unrelated RPCs.
        internal static void OnRoutedRpc(ZRoutedRpc.RoutedRPCData data)
        {
            if (data == null || data.m_methodHash != DamageMethodHash) return;   // the hot path, one int compare
            if (data.m_targetZDO.IsNone()) return;

            var zdoMan = ZDOMan.instance;
            var scene = ZNetScene.instance;
            if (zdoMan == null || scene == null) return;

            var zdo = zdoMan.GetZDO(data.m_targetZDO);
            if (zdo == null) return;
            if (scene.FindInstance(zdo) != null) return;   // vanilla delivered it; nothing to do

            // Only ours. MaterialiseForDamage answers false for anything EBM is not holding back, and that
            // answer is the gate - not a guess about what the ZDO looks like.
            if (!_pending.ContainsKey(data.m_targetZDO) && !ZoneTracker.MaterialiseForDamage(zdo, HoldSecondsAfterHit))
                return;

            if (!_pending.TryGetValue(data.m_targetZDO, out var pending))
            {
                pending = new Pending { FirstQueuedAt = Time.unscaledTime };
                _pending[data.m_targetZDO] = pending;
            }
            else s_multiHit++;

            pending.Hits.Add(data);
            s_captured++;
        }

        internal static void Update()
        {
            if (_pending.Count == 0) return;

            var scene = ZNetScene.instance;
            var zdoMan = ZDOMan.instance;
            if (scene == null || zdoMan == null) { Abandon("the scene went away"); return; }

            float now = Time.unscaledTime;
            _drained.Clear();

            foreach (var entry in _pending)
            {
                var pending = entry.Value;
                var zdo = zdoMan.GetZDO(entry.Key);
                var view = zdo != null ? scene.FindInstance(zdo) : null;

                if (view != null)
                {
                    float landedAfter = now - pending.FirstQueuedAt;
                    if (landedAfter > s_worstWaitSeconds) s_worstWaitSeconds = landedAfter;

                    for (int i = 0; i < pending.Hits.Count; i++)
                    {
                        var hit = pending.Hits[i];
                        // InvokeRoutedRPC rewinds the parameter package once before dispatch and the read
                        // advanced it, so a replay has to rewind it again or the HitData deserialises as
                        // garbage. The RoutedRPCData itself is safe to hold: vanilla news one up per call and
                        // never pools it.
                        hit.m_parameters.SetPos(0);
                        view.HandleRoutedRPC(hit);
                        s_replayed++;
                    }
                    _drained.Add(entry.Key);
                    continue;
                }

                // The ZDO can vanish outright - destroyed by its owner, or the zone unloaded under us. That is
                // not a lost hit, there is simply nothing left to hit.
                if (zdo == null) { s_targetGone++; _drained.Add(entry.Key); continue; }

                // ═══ WAITING BEATS DROPPING WHILE THE TARGET STILL EXISTS ═════
                // The first version dropped after 5s and called it lost. Measured 2026-09-25, swinging a
                // toolTier-10 sword through a town: 6 caught, 5 replayed, 1 "LOST" - and the deadline, not the
                // interception, was what failed. Creation is budgeted (CreateBudgetPerFrame 40) and vanilla
                // picks WHICH objects by distance, so a piece stops being a near candidate as soon as the
                // character walks on, which an auto-walk route does at 5 m/s immediately after the swing.
                //
                // For a building, LATE damage is vastly better than LOST damage: the health lands in the ZDO
                // whenever the piece arrives and the look follows. There is also no leak risk in waiting - a
                // destroyed or unloaded ZDO is already drained by the branch above - so the only thing a short
                // deadline bought was a false loss report.
                float waited = now - pending.FirstQueuedAt;
                if (waited >= SlowReplayWarnSeconds && !pending.Warned)
                {
                    pending.Warned = true;
                    s_slow++;
                    EasyBakeLog.Warn(
                        $"[Damage] {pending.Hits.Count} hit(s) on {entry.Key} still waiting after {waited:F0}s: "
                        + "the piece has been asked for but budgeted creation has not produced it yet. The hit "
                        + "is NOT lost - it replays when the piece lands. Common when the character moves on "
                        + "straight after the hit, since vanilla creates by distance.");
                }
                if (waited >= AbandonSeconds)
                {
                    // Only here is damage genuinely lost: the ZDO is still alive, so something should have
                    // created it, and it has had far longer than any budget needs.
                    s_lostToTimeout += pending.Hits.Count;
                    EasyBakeLog.Warn(
                        $"[Damage] {pending.Hits.Count} hit(s) on {entry.Key} ABANDONED after {AbandonSeconds:0}s. "
                        + "The ZDO is still alive and was never created, so THIS DAMAGE IS LOST. That is a real "
                        + "defect, not a slow frame - vanilla loses it silently; this line exists so it is not "
                        + "silent.");
                    _drained.Add(entry.Key);
                }
            }

            for (int i = 0; i < _drained.Count; i++) _pending.Remove(_drained[i]);
            _drained.Clear();
        }

        private static void Abandon(string why)
        {
            int hits = 0;
            foreach (var entry in _pending) hits += entry.Value.Hits.Count;
            if (hits > 0)
            {
                s_lostToTimeout += hits;
                EasyBakeLog.Warn($"[Damage] dropped {hits} queued hit(s) because {why}.");
            }
            _pending.Clear();
        }

        internal static void Reset()
        {
            _pending.Clear();
            _drained.Clear();
            s_captured = s_replayed = s_lostToTimeout = s_multiHit = s_targetGone = s_slow = 0;
            s_worstWaitSeconds = 0f;
        }

        // One line, and only when something actually happened - a counter block that prints zeroes every period
        // trains people to skip it, which is how the interesting non-zero gets missed.
        internal static string Report()
        {
            if (s_captured == 0) return null;
            string line =
                $"[Damage] {s_captured} hit(s) caught for pieces not created, {s_replayed} replayed after "
                + $"materialising (worst wait {s_worstWaitSeconds * 1000f:0} ms), {_pending.Count} in flight";
            if (s_multiHit > 0) line += $", {s_multiHit} arrived while an earlier hit was still waiting";
            // Reported separately because they mean opposite things. "target gone" is the system working - the
            // piece was destroyed or unloaded, so there was nothing left to damage. "slow" is the budget, not a
            // defect. Only LOST is a problem, and conflating the three is what made one reported loss look like
            // a data-loss bug when the deadline was the thing at fault.
            if (s_targetGone > 0) line += $", {s_targetGone} whose target was already gone (destroyed or unloaded - not a loss)";
            if (s_slow > 0) line += $", {s_slow} slower than {SlowReplayWarnSeconds:0}s";
            if (s_lostToTimeout > 0) line += $", {s_lostToTimeout} LOST";
            return line + ".";
        }
    }
}
