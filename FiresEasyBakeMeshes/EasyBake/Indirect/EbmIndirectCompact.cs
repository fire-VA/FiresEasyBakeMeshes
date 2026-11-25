using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [visual] 11-15, EBM 1.2.392 ([lead]; Fire: never worse than vanilla, small gains add up). [Optimize] IndirectSlotCompact (live, default off until its leg).
    // Row (Plains census leg (rig path), Debug phases line before / after an IndirectRenderer off -> on):
    //   "Instances 100646 live in 227922 slots, ... 1027 batches" -> "Instances 99970 live in 99970 slots, ... 575 batches"; rows and stride unchanged.
    //   The same cycle made the frame 0.31 ms shorter at Plains and 0.26 ms at ocean (there with equal draws): after a world load the live instances are
    //   spread over 2.3 x the slots they need, and every cull dispatch, every per-view pass and the instance buffers cover s_high, not s_live.
    // Why it gets sparse: the free heap hands out the lowest free slot, but s_high only comes down when the TOP slots are free (RemoveInstance), so the
    // adds / removes of a load leave live instances scattered up to the load's peak.
    // What this does, once the load has settled (no slot handed out or freed for CompactSettleSeconds, nothing staged, enough holes): the live instances at
    // the top of the range move down into the free slots below until s_high is the live count. Each move patches its owner at once:
    //   - a group's GroupSlots.Slots entry and its Lane.Slots entry (found through a reverse index built first, verified before every write);
    //   - a covered object's / far rock's handle: since 1.2.392 a handle is an id into IndirectBatches' table, so only the table entry changes.
    // So the tables are consistent after every single move, and the work is SLICED ([lead]: hitches are Fire's priority): the reverse index and the moves
    // each run under CompactBudgetMs a frame, over as many frames as it takes; any slot hand-out, remove, group sync or staged sync in between ends the
    // pass where it is (what was moved stays moved). The moved slots upload through the normal changed-slot path (which is why a pass needs
    // [Optimize] IndirectSparseUpload); a vacated slot's look goes back to Plain on the CPU at once and is uploaded once, as one range, when the pass ends
    // (the looks buffer has a single dirty span, so marking both ends every frame would re-send everything between them).
    // Cells, batches, cascade / light records and the cull's CPU verdicts are keyed by batch and position, never by slot: untouched.
    // Nothing is dropped: the same instances draw, in fewer slots. Two kinds of slot are never moved and are counted in the line ([seasons], [2ic]):
    // ORPHANS, live slots no group list and no handle names (an instance whose owner is gone would be one; "no owner" and "an owner kind not indexed" look
    // the same, so they stay put), and slots the index finds claimed twice. s_high then stops at the highest such slot.
    // A pass that ends does not start again until the slot tables really change ([2ic]: pinned orphans would otherwise restart it at every settle).
    internal static partial class EbmIndirect
    {
        private static bool SlotCompactOn => FiresEasyBakeMeshesPlugin.IndirectSlotCompact != null && FiresEasyBakeMeshesPlugin.IndirectSlotCompact.Value;
        private const int CompactMinHoles = 4096;          // fewer free slots under s_high than this: not worth a pass
        private const float CompactSettleSeconds = 10f;
        private const double CompactBudgetMs = 0.5;        // main-thread time a frame, for the index and for the moves
        internal static int s_slotMutations;               // bumped by every slot hand-out (AllocSlot) and every RemoveInstance: the settle clock.
                                                           // NOT by MoveInstance (SetMatrix): the instance keeps its slot, only its matrix changes, so no index entry goes stale
        internal static int s_slotListRebuilds;            // bumped by every group Sync (its slot lists are rebuilt): ends a pass in flight, but does not restart the settle
                                                           // clock ([2ic]: a look / snow change re-syncs groups without moving a slot, and would keep the clock from ever running out)
        private static int s_compactSeenMut = -1, s_compactDoneMut = -1;
        private static float s_compactQuietSince;
        internal static int s_compactions; internal static long s_compactMoved;   // since the world loaded
        private static bool s_compactSaid, s_compactSaidDepth; private static int s_compactSaidPinned = -1;
        // [2ic]: a pass that a group re-sync stops makes no progress while it is still building its index, and re-syncs do not hold the settle clock, so such
        // stops wait longer each time: 20, 40, ... up to CompactBackoffMaxSeconds. A complete pass, the key going off, or a new world clears it. A stop by a
        // hand-out or a free is normal movement and never backs off (it already needs a fresh settle).
        private const float CompactBackoffMaxSeconds = 320f;
        private static float s_compactBackoff, s_compactNotBefore;
        internal static int s_compactMoveStamp;   // one per moved instance; a diagnostic read taken before a move knows it is stale (EbmIndirectVertexCheck)

        // the pass in flight
        private const int CpIdle = 0, CpIndex = 1, CpMove = 2;
        private const int CpNone = -1, CpTwice = int.MinValue;   // owner values besides a group ordinal (>= 0) and a handle id (-(id + 2))
        private static int s_cpState, s_cpMut, s_cpLists, s_cpGroupAt, s_cpLo, s_cpHi, s_cpHigh0, s_cpMovedNow, s_cpFrames, s_cpOrphans, s_cpTwice, s_cpMismatch, s_cpHandles;
        private static long s_cpTicks, s_cpWorstTicks;
        private static string s_cpOrphanNote = "";
        private static readonly List<GroupSlots> s_cpGroups = new List<GroupSlots>();
        // reverse index, one entry a slot
        private static int[] s_cpOwner = new int[0], s_cpK = new int[0], s_cpLane = new int[0], s_cpLk = new int[0];
        private static int[] s_cpPerBatch = new int[0];
        private static readonly int[] s_cpEmpty = new int[0];

        internal static bool CompactInFlight => s_cpState != CpIdle;   // the slot trim and two diagnostic checks stand back while a pass runs

        // from PresizeTick (EndFrame, before EnsureBuffers / Upload / Cull of this frame). Key off and nothing in flight = one int test + one bool test.
        private static void SlotCompactTick()
        {
            if (s_cpState != CpIdle) { CompactStep(); return; }
            if (!SlotCompactOn) { s_compactSeenMut = -1; s_compactBackoff = 0f; s_compactNotBefore = 0f; return; }
            int holes = s_high - s_live;
            if (holes < CompactMinHoles || holes < s_live / 8) return;
            if (s_slotMutations == s_compactDoneMut) return;   // [2ic]: nothing was handed out or freed since the last pass ended; what is left is pinned
            if (s_slotMutations != s_compactSeenMut) { s_compactSeenMut = s_slotMutations; s_compactQuietSince = Time.unscaledTime; return; }
            if (s_stagedCount > 0 || s_stageList.Count > 0) return;
            if (Time.unscaledTime - s_compactQuietSince < CompactSettleSeconds) return;
            if (Time.unscaledTime < s_compactNotBefore) return;   // [2ic]: backing off after passes a group re-sync stopped
            if (!SparseOn) return;   // [seasons] review: without [Optimize] IndirectSparseUpload each frame of a pass would re-send the whole span between the two pointers
            if (DepthCullWanted)
            {
                // the depth cull keeps a per-slot streak on the GPU (EbmIndirectDepthCull s_streak); moving slots under it would hand one instance another's streak
                if (!s_compactSaidDepth) { s_compactSaidDepth = true; EasyBakeLog.Info("[Indirect] IndirectSlotCompact: not run while IndirectDepthCull is on (its per-slot GPU state does not move with the slots)"); }
                return;
            }
            CompactBegin();
        }

        private static void CompactBegin()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // [seasons] review: the index arrays and their fill count as the pass's first frame
            int high = s_high;
            s_cpState = CpIndex; s_cpMut = s_slotMutations; s_cpLists = s_slotListRebuilds; s_cpGroupAt = 0; s_cpHigh0 = high;
            s_cpMovedNow = 0; s_cpOrphans = 0; s_cpTwice = 0; s_cpMismatch = 0; s_cpHandles = 0; s_cpOrphanNote = "";
            s_cpGroups.Clear();
            foreach (var kv in s_groups) s_cpGroups.Add(kv.Value);
            if (s_cpOwner.Length < high)
            {
                int n = Mathf.NextPowerOfTwo(high);
                s_cpOwner = new int[n]; s_cpK = new int[n]; s_cpLane = new int[n]; s_cpLk = new int[n];
            }
            for (int i = 0; i < high; i++) { s_cpOwner[i] = CpNone; s_cpLane[i] = -1; }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            s_cpTicks = dt; s_cpFrames = 1; s_cpWorstTicks = dt;
        }

        // one frame's share of the pass in flight
        private static void CompactStep()
        {
            string stop = !SlotCompactOn ? "the key went off"
                        : s_slotMutations != s_cpMut ? "a slot was handed out or freed"
                        : s_slotListRebuilds != s_cpLists ? "a group was re-synced"
                        : s_stagedCount > 0 || s_stageList.Count > 0 ? "a staged sync began"
                        : DepthCullWanted ? "IndirectDepthCull went on"
                        : !SparseOn ? "IndirectSparseUpload went off"
                        : s_high > s_cpHigh0 ? "the slot range grew" : null;
            if (stop != null) { CompactEnd(stop, SlotCompactOn && s_slotMutations == s_cpMut && s_slotListRebuilds != s_cpLists); return; }   // back off only when a re-sync alone stopped it
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = (long)(CompactBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            bool done = false;
            if (s_cpState == CpIndex)
            {
                int high = s_cpHigh0;
                while (s_cpGroupAt < s_cpGroups.Count)
                {
                    var reg = s_cpGroups[s_cpGroupAt];
                    var lanes = reg.Lanes;
                    for (int l = 0; l < lanes.Count; l++)
                    {
                        var ls = lanes[l].Slots;
                        for (int k = 0; k < ls.Count; k++) { int s = ls[k]; if (s >= 0 && s < high) { s_cpLane[s] = l; s_cpLk[s] = k; } }
                    }
                    var slots = reg.Slots;
                    for (int k = 0; k < slots.Count; k++)
                    {
                        int s = slots[k];
                        if (s < 0 || s >= high) continue;
                        if (s_cpOwner[s] != CpNone) { if (s_cpOwner[s] != CpTwice) s_cpTwice++; s_cpOwner[s] = CpTwice; continue; }   // [2ic]: a slot two owners name is never moved, and counted
                        s_cpOwner[s] = s_cpGroupAt; s_cpK[s] = k;
                    }
                    s_cpGroupAt++;
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= budget) break;
                }
                if (s_cpGroupAt >= s_cpGroups.Count)
                {
                    s_cpHandles = IndirectBatches.FillOwners(s_cpOwner, high, CpNone, CpTwice, ref s_cpTwice);
                    CountOrphans(high);
                    s_cpState = CpMove; s_cpLo = 0; s_cpHi = high - 1;
                }
            }
            else
            {
                int lo = s_cpLo, hi = s_cpHi, sinceCheck = 0;
                while (true)
                {
                    while (lo < hi && s_instBatch[lo] != 0) lo++;
                    while (hi > lo && (s_instBatch[hi] == 0 || s_cpOwner[hi] == CpNone || s_cpOwner[hi] == CpTwice)) hi--;   // [seasons] / [2ic]: an orphan or a twice-claimed slot stays where it is
                    if (lo >= hi) { done = true; break; }
                    // the owner first: found and verified before anything moves
                    int own = s_cpOwner[hi];
                    if (own >= 0)
                    {
                        var reg = s_cpGroups[own];
                        int k = s_cpK[hi], l = s_cpLane[hi], kl = s_cpLk[hi];
                        bool laneOk = l >= 0 && l < reg.Lanes.Count && kl < reg.Lanes[l].Slots.Count && reg.Lanes[l].Slots[kl] == hi;
                        if (k >= reg.Slots.Count || reg.Slots[k] != hi || !laneOk) { s_cpOwner[hi] = CpTwice; s_cpMismatch++; continue; }   // [2ic]: its list entry is not where the index says: the slot is pinned for this pass and counted, nothing moves
                        reg.Slots[k] = lo;
                        reg.Lanes[l].Slots[kl] = lo;
                        s_cpK[lo] = k; s_cpLane[lo] = l; s_cpLk[lo] = kl;
                    }
                    else
                    {
                        if (!IndirectBatches.MoveId(-(own + 2), hi, lo)) { s_cpOwner[hi] = CpTwice; s_cpMismatch++; continue; }   // [2ic]: the handle no longer names this slot: pinned and counted, nothing moves
                    }
                    s_cpOwner[lo] = own; s_cpOwner[hi] = CpNone; s_cpLane[hi] = -1;
                    s_inst[lo] = s_inst[hi]; s_instBatch[lo] = s_instBatch[hi]; s_instPos[lo] = s_instPos[hi]; s_instLod[lo] = s_instLod[hi];
                    s_inst[hi] = default; s_instBatch[hi] = 0; s_instPos[hi] = Vector3.zero; s_instLod[hi] = Vector4.zero;
                    IndirectLooks.MoveSlot(hi, lo);
                    MarkDirty(lo);   // the changed-slot upload of this frame sends it
                    MarkDirty(hi);   // [seasons] review: the vacated slot's ZERO batch id must reach the GPU too; a staged slot handed out there later rests on "its batch id is still 0 on both sides" (Upload sends freed top slots since 1.2.392)
                    s_free.Push(hi);   // [2ic]: a vacated slot below a pinned one must be reusable; one above the new s_high is dropped by PopBelow when it surfaces
                    s_cpMovedNow++; s_compactMoveStamp++;
                    if (++sinceCheck >= 128) { sinceCheck = 0; if (System.Diagnostics.Stopwatch.GetTimestamp() - t0 >= budget) break; }
                }
                s_cpLo = lo; s_cpHi = hi;
                while (s_high > 0 && s_instBatch[s_high - 1] == 0 && !IsStaged(s_high - 1)) s_high--;   // as RemoveInstance lowers it
            }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            s_cpTicks += dt; s_cpFrames++; if (dt > s_cpWorstTicks) s_cpWorstTicks = dt;
            if (done) CompactEnd(null, false);
        }

        // live slots that no group list and no handle names (after the index is complete)
        private static void CountOrphans(int high)
        {
            int nb = s_batches.Count, orphans = 0, orphanBatches = 0;
            if (s_cpPerBatch.Length < nb + 1) s_cpPerBatch = new int[Mathf.NextPowerOfTwo(nb + 1)];
            var per = s_cpPerBatch;
            Array.Clear(per, 0, nb + 1);
            for (int s = 0; s < high; s++)
            {
                uint b = s_instBatch[s];
                if (b == 0 || s_cpOwner[s] != CpNone) continue;
                orphans++;
                if (b <= nb && per[b]++ == 0) orphanBatches++;
            }
            s_cpOrphans = orphans;
            if (orphans == 0) return;
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            for (int b = 1; b <= nb && shown < 6; b++)
            {
                if (per[b] == 0) continue;
                var batch = s_batches[b - 1];
                sb.Append(shown == 0 ? "" : ", ").Append(batch.Mesh != null ? batch.Mesh.name : "(no mesh)").Append(" x").Append(per[b]);
                shown++;
            }
            s_cpOrphanNote = $" in {orphanBatches} batch(es): {sb}{(orphanBatches > shown ? ", ..." : "")}";
        }

        // why == null: the pass ran to its end. Otherwise it stopped early; what it moved stays moved (every move patched its owner).
        private static void CompactEnd(string why, bool backOff)
        {
            s_cpState = CpIdle;
            s_cpGroups.Clear();
            if (why == null) s_cpOwner = s_cpK = s_cpLane = s_cpLk = s_cpEmpty;   // the index (4 ints a slot of the old range) goes after a complete pass (the next one needs the slot tables to change and settle again); a stopped pass keeps it, so its retry allocates nothing ([2ic])
            if (why == null) { s_compactBackoff = 0f; s_compactNotBefore = 0f; }
            else if (backOff) { s_compactBackoff = s_compactBackoff <= 0f ? 2f * CompactSettleSeconds : Mathf.Min(s_compactBackoff * 2f, CompactBackoffMaxSeconds); s_compactNotBefore = Time.unscaledTime + s_compactBackoff; }
            IndirectLooks.FlushMovedTail();   // the vacated slots' Plain looks go up once, now
            s_trimSince = -1f;   // [seasons] / [2ic]: the slot trim's 60 s start again, so a trim never lands on the tick a pass ends (the looks tail must be uploaded first)
            if (s_high == s_live) s_free.Clear();   // no hole left below s_high: every entry of the free heap is stale
            s_compactSeenMut = -1;
            if (why == null) { s_compactions++; s_compactDoneMut = s_slotMutations; }   // a complete pass waits for a real change; one that was stopped tries again after the next settle
            s_compactMoved += s_cpMovedNow;
            double ms = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int pinned = s_cpOrphans + s_cpTwice + s_cpMismatch;
            string line = $"[Indirect] compacted{(why == null ? "" : " (stopped early: " + why + ")")}: slots in use {s_cpHigh0} -> {s_high} for {s_live} live instance(s) ({s_cpMovedNow} moved down, {s_cpHigh0 - s_high} slot(s) fewer for every cull pass); capacity {s_inst.Length} slots ({(long)s_inst.Length * 144 >> 20} MB of slot arrays), unchanged by this; "
                        + $"took {s_cpTicks * ms:0.00} ms of compaction work over {s_cpFrames} frame(s), worst frame {s_cpWorstTicks * ms:0.00} ms (the uploads of the moved slots are in the upload / looks upload phases); "
                        + $"{s_batches.Count} batch(es), unchanged; owners: {s_groups.Count} group(s), {s_cpHandles} handle(s); never moved: orphan instances (live, no owner) {s_cpOrphans}{s_cpOrphanNote}, claimed twice {s_cpTwice}, index mismatch {s_cpMismatch}{(why != null && backOff ? $"; next try not before {s_compactBackoff:0} s" : "")}";
            // Info: the first complete pass, and a complete pass whose pinned count differs from the last one said. Everything else Debug (a repeating line; [2ic]: a disk write a pass is a stall).
            bool tell = why == null && (!s_compactSaid || pinned != s_compactSaidPinned);
            if (tell) { s_compactSaid = true; s_compactSaidPinned = pinned; EasyBakeLog.Info(line); } else EasyBakeLog.Debug(line);
        }

        // from ReleaseAll: the next world starts clean
        private static void SlotCompactReset()
        {
            s_cpState = CpIdle; s_cpGroups.Clear();
            s_cpOwner = s_cpK = s_cpLane = s_cpLk = s_cpEmpty;
            s_compactSeenMut = -1; s_compactDoneMut = -1; s_compactions = 0; s_compactMoved = 0L; s_compactSaidPinned = -1;
            s_compactBackoff = 0f; s_compactNotBefore = 0f;
        }
    }
}
