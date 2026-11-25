using System;
using System.IO;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [Optimize] IndirectPresize ([saddles] 11-09, Tools\AUDIT_shave_hitches_20261109.md): a fill grew the GPU buffers about ten times (19k to 162k+ instances), each growth
    // making new buffers and a full layout on a frame the player is moving. On: this world's largest sizes from its last session are remembered and used once, before the first
    // layout, so the arrays and buffers start at the size the session ends at anyway. A world with no record behaves as before. Bounded, one small text file per world.
    internal static partial class EbmIndirect
    {
        private const int PresizeMaxSlots = 1 << 21, PresizeMaxBatches = 1 << 13, PresizeMaxStride = 1 << 22;
        private const float PresizeSaveSeconds = 30f;
        private static bool s_presizeApplied, s_presizeDirty;
        private static string s_presizeFile;
        private static int s_presizeBatches, s_presizeStride, s_hwSlots, s_hwBatches, s_hwStride;
        private static float s_presizeNextSave;

        private static bool PresizeOn => FiresEasyBakeMeshesPlugin.IndirectPresize != null && FiresEasyBakeMeshesPlugin.IndirectPresize.Value;
        // 1.2.389 [Optimize] IndirectSlotTrim ([visual], [lead]; rig: 524,288 slots of capacity for 9-15 k in use = 75.5 MB of managed arrays at 144 B a slot).
        // (a) The record keeps this session's PEAK slots in use, marked "live", instead of the capacity (which only ever went up, world after world).
        // (b) SlotTrimTick: capacity under a quarter used for TrimHoldSeconds -> the slot arrays shrink to twice the slots in use. Safe by construction: every
        //     live or staged slot is below s_high (RemoveInstance / ReleaseStagedSlot only lower s_high past free, unstaged slots), and nothing is trimmed while a
        //     staged sync is in flight. The instance GPU buffers follow through EnsureBuffers' growth branch (same copy kernel, same re-bind), so no path sees a
        //     CPU array longer than its buffer for more than the rest of this call.
        private static bool SlotTrimOn => FiresEasyBakeMeshesPlugin.IndirectSlotTrim != null && FiresEasyBakeMeshesPlugin.IndirectSlotTrim.Value;
        private const int TrimMinSlots = 4096;
        private const float TrimHoldSeconds = 60f;
        private static float s_trimSince = -1f;
        private static bool s_trimRemake, s_trimSaid;     // s_trimRemake: EnsureBuffers re-makes the instance buffers at the smaller size once
        private static int s_peakHigh;                    // this session's most slots in use (the record's "live" value)
        private static int s_peakSaved;                   // 1.2.394: the value the record on disk holds from this session (0 = none yet)
        private static int s_peakLoaded;                  // 1.2.394 ([seasons], [lead]): the peak the record held when this world loaded (a live record: its value; an old capacity record: half of it; 0 = no record)
        private static int PeakToSave => Mathf.Max(s_peakHigh, s_peakLoaded / 2);   // 1.2.394: the record never drops below half of what it held at load, so one session away from the dense place (or one killed early in a load) cannot collapse it; the next load then needs one doubling at most, and a world that stays small still shrinks session by session
        internal static int s_trims; internal static long s_trimFreedBytes;   // since the world loaded

        private static void SlotTrimTick()
        {
            if (s_high > s_peakHigh) s_peakHigh = s_high;   // 1.2.394 ([visual], [lead]): counted with the key off too, so a key turned on in the middle of a session saves the real peak of the load, not the count after a compaction (rig 11-15: such a record made the next load grow its buffers once)
            if (!SlotTrimOn) { s_trimSince = -1f; return; }
            if (PeakToSave != s_peakSaved) s_presizeDirty = true;   // the live record is not what this session would save: saved at the next save slot. Was "s_presizeDirty = true" inside the peak test; unchanged in 1.2.392 ([lead], after [2ic]'s numbers): the record keeps the load's PEAK slots in use, so the next load does not grow the arrays midway; the compaction makes the cull cover s_high whatever the capacity is
            if (CompactInFlight) { s_trimSince = -1f; return; }   // 1.2.392 ([seasons] / [2ic]): no trim while a compaction pass runs (CompactEnd restarts this clock too); a trim would cut the looks array under the pass's held-back tail
            int cap = s_inst.Length;
            if (cap <= TrimMinSlots || (long)s_high * 4 >= cap || s_stagedCount > 0 || s_stageList.Count > 0) { s_trimSince = -1f; return; }
            float now = Time.unscaledTime;
            if (s_trimSince < 0f) { s_trimSince = now; return; }
            if (now - s_trimSince < TrimHoldSeconds) return;
            s_trimSince = -1f;
            int n = Mathf.Max(TrimMinSlots, Mathf.NextPowerOfTwo(Mathf.Max(1, s_high * 2)));
            if (n >= cap) return;
            Array.Resize(ref s_inst, n); Array.Resize(ref s_instBatch, n); Array.Resize(ref s_instPos, n); Array.Resize(ref s_instLod, n);
            IndirectLooks.TrimCapacity(n);
            if (s_dirtyMax >= n) s_dirtyMax = n - 1;   // a pending upload never reaches past the arrays (slots at or past s_high are free: nothing to send for them)
            s_trimRemake = true; s_bindingsDirty = true;
            s_trims++; s_trimFreedBytes += (long)(cap - n) * 144;
            string line = $"[Indirect] slot arrays trimmed: {cap} -> {n} slots of capacity ({s_high} in use, {s_live} live) after {TrimHoldSeconds:0} s under a quarter used; {(long)(cap - n) * 144 >> 20} MB of managed arrays freed, the instance buffers are re-made at {n}";
            if (!s_trimSaid) { s_trimSaid = true; EasyBakeLog.Info(line); } else EasyBakeLog.Debug(line);
        }

        // from EndFrame, before EnsureBuffers: once per world, then the high-water bookkeeping
        private static void PresizeTick()
        {
            SlotCompactTick();   // 1.2.392 [Optimize] IndirectSlotCompact (key off = one bool test); before the trim, so the trim sees the compacted range
            SlotTrimTick();   // 1.2.389 [Optimize] IndirectSlotTrim (key off = one bool test)
            if (!PresizeOn) return;
            if (!s_presizeApplied) ApplyPresize();
            if (s_inst.Length > s_hwSlots) { s_hwSlots = s_inst.Length; s_presizeDirty = true; }
            if (s_presizeDirty && Time.unscaledTime >= s_presizeNextSave) { s_presizeNextSave = Time.unscaledTime + PresizeSaveSeconds; SavePresize(); }
        }

        private static void ApplyPresize()
        {
            var world = ZNet.World;
            if (world == null) return;   // the first frames of a load: asked again next frame
            s_presizeApplied = true;
            try
            {
                string name = Far.FarCache.Safe(string.IsNullOrEmpty(world.m_name) ? "world" : world.m_name) + "_" + world.m_uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
                s_presizeFile = Path.Combine(Path.Combine(Far.FarCache.Root, "indirect-presize"), name + ".txt");
                if (!File.Exists(s_presizeFile)) { EasyBakeLog.Info($"[Indirect] presize: no record for this world yet; buffers grow as before and the sizes are remembered for next time"); return; }
                string[] v = File.ReadAllText(s_presizeFile).Trim().Split(' ');
                if ((v.Length != 3 && v.Length != 4) || !int.TryParse(v[0], out int slots) || !int.TryParse(v[1], out int batches) || !int.TryParse(v[2], out int stride) || slots <= 0 || batches <= 0 || stride <= 0)
                { EasyBakeLog.Debug("[Indirect] presize: the record is unreadable, ignored"); return; }
                bool liveRecord = v.Length == 4 && v[3] == "live";   // 1.2.389: written by a session with IndirectSlotTrim on
                s_peakLoaded = liveRecord ? slots : slots / 2;   // 1.2.394: before the headroom below is added; see PeakToSave
                if (liveRecord) slots = Mathf.Max(TrimMinSlots, slots + slots / 2);   // the last session's peak in use + half as headroom
                slots = Mathf.NextPowerOfTwo(Mathf.Min(slots, PresizeMaxSlots));
                // 1.2.393 ([lead], [seasons]): an old 3-token record's capacity is USED again (1.2.389-1.2.392 set slots to 0 here while IndirectSlotTrim was on, which made the first load of a world grow its arrays and GPU buffers by doubling). The trim shrinks an oversized capacity after the load settles; the next save writes the live record.
                s_presizeBatches = Mathf.Min(batches, PresizeMaxBatches);
                s_presizeStride = Mathf.Min(stride, PresizeMaxStride);
                s_hwSlots = slots; s_hwBatches = s_presizeBatches; s_hwStride = s_presizeStride;
                if (slots > s_inst.Length)
                {
                    Array.Resize(ref s_inst, slots); Array.Resize(ref s_instBatch, slots); Array.Resize(ref s_instPos, slots); Array.Resize(ref s_instLod, slots);
                    IndirectLooks.EnsureCapacity(slots);
                    s_bindingsDirty = true;
                }
                EasyBakeLog.Info($"[Indirect] presize: this world's last sizes used: {slots} instance slots, {s_presizeBatches} batch rows, visible stride {s_presizeStride}");
            }
            catch (Exception e) { s_presizeBatches = s_presizeStride = 0; EasyBakeLog.Debug("[Indirect] presize: not used: " + e.Message); }
        }

        // after a full layout: the sizes it settled on
        private static void NotePresizeLayout(int cap, int stride)
        {
            if (!PresizeOn) return;
            if (cap > s_hwBatches) { s_hwBatches = cap; s_presizeDirty = true; }
            if (stride > s_hwStride) { s_hwStride = stride; s_presizeDirty = true; }
        }

        private static void SavePresize()
        {
            if (s_presizeFile == null) return;
            s_presizeDirty = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_presizeFile));
                string tmp = s_presizeFile + ".tmp";
                if (SlotTrimOn) s_peakSaved = PeakToSave;   // 1.2.394: before the write, so a write that fails is not retried every frame (the next higher peak asks again)
                File.WriteAllText(tmp, SlotTrimOn ? Mathf.Max(1, PeakToSave) + " " + s_hwBatches + " " + s_hwStride + " live" : s_hwSlots + " " + s_hwBatches + " " + s_hwStride);   // 1.2.389: with IndirectSlotTrim the peak slots in use, not the capacity
                if (File.Exists(s_presizeFile)) File.Replace(tmp, s_presizeFile, null); else File.Move(tmp, s_presizeFile);
            }
            catch (Exception e) { EasyBakeLog.Debug("[Indirect] presize: not saved: " + e.Message); }
        }

        // from ReleaseAll: the next world reads its own record
        private static void PresizeReset()
        {
            if (s_presizeDirty) SavePresize();
            s_presizeApplied = false; s_presizeDirty = false; s_presizeFile = null;
            s_presizeBatches = s_presizeStride = s_hwSlots = s_hwBatches = s_hwStride = 0;
            s_peakHigh = 0; s_peakSaved = 0; s_peakLoaded = 0; s_trimSince = -1f; s_trimRemake = false; s_trims = 0; s_trimFreedBytes = 0L;   // 1.2.389
        }
    }
}
