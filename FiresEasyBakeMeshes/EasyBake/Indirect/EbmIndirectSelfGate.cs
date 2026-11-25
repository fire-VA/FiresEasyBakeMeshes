using System.Collections.Generic;
using UnityEngine;
using FiresEasyBakeMeshes.EasyBake.Indirect.Coverage;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [visual] 11-15, EBM 1.2.401 ([lead]; Fire: the indirect path should stand down by itself where it does not pay, Deep North; never worse than vanilla).
    // Rows (frame ms, renderer on / off, live slots): Deep North ~3,985: 3.365 / 3.225; circN 14,575: 3.958 / 3.834; bf1 25,735: 4.509 / 6.640. The path
    // loses 0.1 ms where little is loaded and wins 2 ms where much is.
    // [Optimize] IndirectSelfGate (live, default OFF until its leg): the renderer stays ACTIVE (tables, buffers, hooks as they are) and is emptied through
    // the doors that already exist, a slice a frame, so there is no hitch in either direction and the settled group walk stays available:
    //   - baked groups: Take returns false for them, only during a FULL group walk (ZoneTracker.cs:820 then draws the group instanced in the same frame;
    //     on a settled frame a group Take refuses is not drawn until the next full walk, EbmIndirectCpuTrim.cs:150), each leaving through the existing Drop;
    //     they return through the normal Take (Sync / staged sync), a budget of instances a frame;
    //   - covered objects: their own Release (out) and Refresh (back), from a queue under 2 ms a frame, the budget CoveredObject.FlushPending uses;
    //     while stood down CoveredObject.Refresh leaves at its new gate exit, so new objects get their component (and are counted) but are not covered.
    // THE CONDITION reads the same in both states (live slots are 0 once stood down, and a count rebuilt from loose objects would not have distance
    // parking and LOD-level release taken out, so it would be biased and the gate would flip back and forth):
    //   load L = instances of every baked group the last full walk offered to Take + live covered objects + objects waiting in the coverage registry.
    //   Stand down when L <= IndirectSelfGateDownAt for 5 checks in a row (10 s; never while teleporting), come back at once when L >= IndirectSelfGateUpAt. UpAt must be above DownAt; else automatic never moves, only Force does.
    // [Optimize] IndirectSelfGateForce (acts only while IndirectSelfGate is on): 0 = automatic, 1 = hold stood down, 2 = hold on (for the leg that measures
    // the stood-down state and reads L).
    // Far rocks (their key is off by default) are drawn only by this path and are never moved out: with them on, "stood down" keeps their instances.
    // Checked every 2 s on values that already exist; nothing per frame while the state is steady except two compares.
    internal static partial class EbmIndirect
    {
        private const int GateIn = 0, GateLeaving = 1, GateOut = 2, GateReturning = 3;
        private const int GateOutBudget = 6000, GateInBudget = 3000;   // baked instances a frame leaving / returning (a Drop is one slot free each; a Sync fills slots)
        private const double GateQueueMs = 2.0;                        // covered objects a frame, as CoveredObject.FlushPending
        private const int GateUnderChecks = 5;                         // automatic stands down after this many checks in a row (10 s) at or under the mark
        private static int s_gateUnder;
        private static int s_gate, s_gateBudget, s_gateDeferred, s_gateMovedGroups, s_gateMovedObjects, s_gateQueueAt;
        private static long s_gateBakedWalk, s_gateBaked;
        private static float s_gateNextCheck, s_gateStartedAt, s_gateNextSay;
        private static bool s_gateZeroDetached, s_gateWalked;
        private static readonly List<CoveredObject> s_gateQueue = new List<CoveredObject>(256);

        private static bool GateKeyOn => FiresEasyBakeMeshesPlugin.IndirectSelfGate != null && FiresEasyBakeMeshesPlugin.IndirectSelfGate.Value;
        private static int GateForce => FiresEasyBakeMeshesPlugin.IndirectSelfGateForce != null ? FiresEasyBakeMeshesPlugin.IndirectSelfGateForce.Value : 0;
        private static float GateDownAt => FiresEasyBakeMeshesPlugin.IndirectSelfGateDownAt != null ? FiresEasyBakeMeshesPlugin.IndirectSelfGateDownAt.Value : 0f;
        private static float GateUpAt => FiresEasyBakeMeshesPlugin.IndirectSelfGateUpAt != null ? FiresEasyBakeMeshesPlugin.IndirectSelfGateUpAt.Value : 0f;

        /// <summary>CoveredObject.Refresh: true = do not cover now (leaving or stood down).</summary>
        internal static bool GateHoldsOut => s_gate == GateLeaving || s_gate == GateOut;

        /// <summary>The load the gate judges, the same in both states.</summary>
        private static long GateLoad() => s_gateBaked + CoveredObject.Live.Count + CoverageRegistry.Waiting;

        /// <summary>From Take, first thing. True = the gate decided (result is Take's answer); false = Take goes on as always.</summary>
        private static bool GateTake(ZoneInstanceGroup group, out bool result)
        {
            result = false;
            if (!s_settledFrame) s_gateBakedWalk += group.Matrices.Count;   // a full walk offers every drawn group once
            if (s_gate == GateIn) return false;
            bool registered = s_groups.TryGetValue(group, out var reg);
            if (s_gate == GateReturning)
            {
                if (registered || s_settledFrame || s_refused.ContainsKey(group)) return false;
                if (s_gateBudget <= 0) { s_gateDeferred++; RefusedGroups++; return true; }   // drawn instanced this frame; asked again on the next full walk
                s_gateBudget -= Mathf.Max(1, group.Matrices.Count);
                return false;
            }
            // leaving or stood down
            if (!registered) { RefusedGroups++; return true; }
            if (s_settledFrame || s_gateBudget <= 0) return false;   // it stays on the indirect path this frame; it leaves on a full walk with budget left
            s_gateBudget -= Mathf.Max(1, group.Matrices.Count);
            Drop(group, reg); s_groups.Remove(group); IndirectLooks.Forget(group);   // the same three steps as the snow / wet hand-back in Take
            RefusedGroups++; s_gateMovedGroups++;
            return true;
        }

        /// <summary>From NoteFullWalkEnd: the walk's sum becomes the load's baked part.</summary>
        private static void GateWalkEnd() { s_gateBaked = s_gateBakedWalk; s_gateBakedWalk = 0; s_gateWalked = true; }

        /// <summary>From EndFrame, once a frame while the renderer is active.</summary>
        private static void GateTick()
        {
            if (s_gate == GateIn && !GateKeyOn) return;   // key off and nothing to undo: two compares
            float now = Time.unscaledTime;
            if (s_gate == GateIn || s_gate == GateOut)
            {
                if (now < s_gateNextCheck) return;
                s_gateNextCheck = now + 2f;
                long load = GateLoad();
                int force = GateForce;
                bool marksOk = GateDownAt > 0f && GateUpAt > GateDownAt;   // both set with a gap between them ([seasons] review: equal marks and a steady load exactly on them would stand down and come back for ever); else automatic never moves
                // [seasons] review: right after a spawn or teleport the load is small for some seconds while the area streams in. Standing down then and coming back
                // seconds later would release and re-cover everything during the fill. So automatic stands down only after GateUnderChecks checks in a row (2 s
                // apart) at or under the mark, and never while the player is teleporting or there is no player. Coming back is immediate.
                var me = Player.m_localPlayer;
                bool under = marksOk && s_gateWalked && load <= (long)GateDownAt && me != null && !me.IsTeleporting();
                s_gateUnder = s_gate == GateIn && force == 0 && under ? s_gateUnder + 1 : 0;
                bool wantOut = GateKeyOn && (force == 1
                    || (force == 0 && marksOk && (s_gate == GateIn ? s_gateUnder >= GateUnderChecks : load < (long)GateUpAt)));   // key off, force 2 or unset marks while stood down = come back
                if (s_gate == GateIn && wantOut) GateBegin(GateLeaving, load, force == 1 ? "forced" : "load at or under the stand-down mark");
                else if (s_gate == GateOut && !wantOut) GateBegin(GateReturning, load, force == 2 ? "forced" : !GateKeyOn ? "key off" : "load at or over the come-back mark");
                else if (EasyBakeLog.DebugOn() && now >= s_gateNextSay)
                {
                    s_gateNextSay = now + 30f;
                    EasyBakeLog.Debug($"[Indirect] self-gate: {(s_gate == GateOut ? "stood down" : "on")}, load {load} (baked instances {s_gateBaked} + covered objects {CoveredObject.Live.Count} + waiting {CoverageRegistry.Waiting}), live slots {s_live}; marks down {GateDownAt:0} / up {GateUpAt:0}, force {force}");
                }
                return;
            }

            // moving: a slice of covered objects under the budget, and a full group walk next frame while groups are left to move
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long limit = t0 + (long)(GateQueueMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            bool leaving = s_gate == GateLeaving;
            for (; s_gateQueueAt < s_gateQueue.Count; s_gateQueueAt++)
            {
                if ((s_gateQueueAt & 7) == 7 && System.Diagnostics.Stopwatch.GetTimestamp() > limit) break;
                var c = s_gateQueue[s_gateQueueAt];
                if (c == null) continue;   // destroyed meanwhile
                try { if (leaving) c.GateRelease(); else c.Refresh(); }
                catch (System.Exception e) { EasyBakeLog.Debug($"[Indirect] self-gate: {(leaving ? "release" : "cover")} of {c.Verdict?.Name} threw: {e.Message}"); }
                s_gateMovedObjects++;
            }
            bool queueDone = s_gateQueueAt >= s_gateQueue.Count;
            bool groupsDone = leaving ? s_groups.Count == 0 : (s_gateWalked && s_gateDeferred == 0);
            if (queueDone && groupsDone)
            {
                s_gateQueue.Clear(); s_gateQueueAt = 0;
                s_gate = leaving ? GateOut : GateIn;
                s_gateNextCheck = now + 2f;
                EasyBakeLog.Info($"[Indirect] self-gate: {(leaving ? "stood down" : "back on")} in {now - s_gateStartedAt:0.0} s: {s_gateMovedGroups} baked group(s) and {s_gateMovedObjects} covered object(s) moved {(leaving ? "to the instanced path / to Unity" : "back to the indirect path")}; load {GateLoad()}, live slots now {s_live}");
                return;
            }
            s_gateBudget = leaving ? GateOutBudget : GateInBudget;
            s_gateDeferred = 0; s_gateWalked = false;
            RequestFullWalk();   // groups move only on a full walk (see the header)
        }

        private static void GateBegin(int state, long load, string why)
        {
            s_gate = state;
            s_gateStartedAt = Time.unscaledTime;
            s_gateMovedGroups = 0; s_gateMovedObjects = 0; s_gateDeferred = 0; s_gateWalked = false;
            s_gateBudget = state == GateLeaving ? GateOutBudget : GateInBudget;
            s_gateQueue.Clear(); s_gateQueueAt = 0;
            foreach (var c in CoveredObject.Live) if (c != null && (state == GateReturning || c.IsCovering)) s_gateQueue.Add(c);
            EasyBakeLog.Info($"[Indirect] self-gate: {(state == GateLeaving ? "standing down" : "coming back")} ({why}): load {load} (baked instances {s_gateBaked} + covered objects {CoveredObject.Live.Count} + waiting {CoverageRegistry.Waiting}), live slots {s_live}, {s_groups.Count} baked group(s) on the indirect path, {s_gateQueue.Count} covered object(s) queued; marks down {GateDownAt:0} / up {GateUpAt:0}");
            RequestFullWalk();
        }

        /// <summary>
        /// EndFrame's "no instance live" return (found 11-15 while reading for the gate, a fault with or without it): that return comes before Upload, Cull and Draw,
        /// and nothing on it took the sun's and the lights' caster command buffers off (DetachCascades is reached only from RecordCascades, Draw and ReleaseAll).
        /// They stayed attached and replayed every recorded caster draw each frame against an args buffer no cull refreshed. Detached once here; RecordCascades and
        /// RecordLocal attach them again by themselves on the next frame that draws.
        /// </summary>
        private static void GateZeroDetach()
        {
            if (s_gateZeroDetached) return;
            s_gateZeroDetached = true;
            bool had = s_cbLight != null || s_slotAttached > 0;
            DetachCascades(); DetachLocal();
            if (had) EasyBakeLog.Debug("[Indirect] no instance is live: the caster command buffers were taken off the sun and the lights (they come back with the first instance)");
        }

        /// <summary>From ReleaseAll: the next world, or the renderer key off, starts on with nothing queued.</summary>
        private static void GateReset()
        {
            s_gate = GateIn; s_gateUnder = 0; s_gateQueue.Clear(); s_gateQueueAt = 0; s_gateBaked = 0; s_gateBakedWalk = 0; s_gateWalked = false; s_gateDeferred = 0; s_gateZeroDetached = false; s_gateNextCheck = 0f;
        }
    }
}
