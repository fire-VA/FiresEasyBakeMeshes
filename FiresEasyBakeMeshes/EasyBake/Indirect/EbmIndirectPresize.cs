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

        // from EndFrame, before EnsureBuffers: once per world, then the high-water bookkeeping
        private static void PresizeTick()
        {
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
                if (v.Length != 3 || !int.TryParse(v[0], out int slots) || !int.TryParse(v[1], out int batches) || !int.TryParse(v[2], out int stride) || slots <= 0 || batches <= 0 || stride <= 0)
                { EasyBakeLog.Debug("[Indirect] presize: the record is unreadable, ignored"); return; }
                slots = Mathf.NextPowerOfTwo(Mathf.Min(slots, PresizeMaxSlots));
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
                File.WriteAllText(tmp, s_hwSlots + " " + s_hwBatches + " " + s_hwStride);
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
        }
    }
}
