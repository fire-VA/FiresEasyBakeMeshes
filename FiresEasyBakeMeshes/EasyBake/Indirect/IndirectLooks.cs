using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    /// <summary>
    /// Step A2 ([wishbone] 11-08 for [visual]'s indirect path; our own code): per-instance looks, so groups with snow or roofed-dry
    /// ranges can draw indirect too instead of going back to the instanced path (ZoneInstanceGroup.IndirectNeedsLegacy).
    /// One float4 per instance slot, parallel to EbmIndirect's slot arrays:
    ///   x = snow cover (the group's _FiresSnowBiome / _VedrSnowCover value for that instance),
    ///   y = wet scale (0 = in a roofed range drawn dry, 1 = open), zw = 0.
    /// One GraphicsBuffer, bound under _VedrPieceInst (Vedr/Piece's VedrPieceInst {snowCover, wetScale, pad2}, 16 bytes; contract v1)
    /// and _EbmInstLooks (EBM's own procedural FiresPiece variant). Both are indexed by the original instance slot.
    /// Only changed slots are written (dirty range, uploaded once a frame). A group's values are rewritten only when its version,
    /// snow generation or look state changes, never per frame. 0 alloc a frame; main thread only; grows by doubling.
    /// </summary>
    internal static class IndirectLooks
    {
        internal static readonly Vector4 Plain = new Vector4(0f, 1f, 0f, 0f);   // no snow, open (vanilla wetness)
        internal static readonly int IdVedrPieceInst = Shader.PropertyToID("_VedrPieceInst");
        internal static readonly int IdEbmInstLooks = Shader.PropertyToID("_EbmInstLooks");
        private const int Stride = 16;

        private static Vector4[] s_looks = new Vector4[0];
        internal static long HeapBytes() => s_looks.Length * 16L;   // [Heap] by holder
        private static GraphicsBuffer s_buf;
        private static int s_dirtyMin = int.MaxValue, s_dirtyMax = -1;

        private sealed class Seen { public int Version = -1, SnowGen = -1, ExtGen = -1, Slots = -1; public bool Active; }
        private static readonly Dictionary<ZoneInstanceGroup, Seen> s_seen = new Dictionary<ZoneInstanceGroup, Seen>();
        private static int s_mismatchLogs;

        /// <summary>Call where EbmIndirect grows its slot arrays (same new length). Doubling; new slots start Plain.</summary>
        internal static void EnsureCapacity(int slots)
        {
            if (slots <= s_looks.Length) return;
            int n = Mathf.Max(1024, s_looks.Length);
            while (n < slots) n *= 2;
            int old = s_looks.Length;
            Array.Resize(ref s_looks, n);
            for (int i = old; i < n; i++) s_looks[i] = Plain;
        }

        /// <summary>One slot's look; a no-op when unchanged (so a rewrite of an unchanged group uploads nothing).</summary>
        internal static void Set(int slot, Vector4 look)
        {
            if (slot < 0) return;
            if (slot >= s_looks.Length) EnsureCapacity(slot + 1);
            if (s_looks[slot] == look) return;
            s_looks[slot] = look;
            if (slot < s_dirtyMin) s_dirtyMin = slot;
            if (slot > s_dirtyMax) s_dirtyMax = slot;
        }

        /// <summary>EbmIndirect.RemoveInstance: the freed slot goes back to Plain (a reused slot never shows the last owner's snow).</summary>
        internal static void Clear(int slot) => Set(slot, Plain);

        // 1.2.232 ([visual], the stretch hunt): [Integrity] for this buffer too. 8,192 slots a second, rolling; skips the not-yet-uploaded range.
        private static int s_icStart; private static bool s_icPending, s_icSaid; internal static int s_icChecks, s_icBad;
        private static Vector4[] s_icExpect = new Vector4[8192]; private static int s_icBase, s_icN, s_icSkipMin, s_icSkipMax;
        internal static void IntegrityTick(int high)
        {
            if (s_icPending || s_buf == null || high <= 0) return;
            int top = Mathf.Min(high, Mathf.Min(s_looks.Length, s_buf.count));
            if (top <= 0) return;
            if (s_icStart >= top) s_icStart = 0;
            int n = Mathf.Min(8192, top - s_icStart);
            Array.Copy(s_looks, s_icStart, s_icExpect, 0, n);
            s_icBase = s_icStart; s_icN = n; s_icSkipMin = s_dirtyMin; s_icSkipMax = s_dirtyMax; s_icStart += n; s_icPending = true;
            var buf = s_buf;
            UnityEngine.Rendering.AsyncGPUReadback.Request(buf, n * Stride, s_icBase * Stride, r =>
            {
                s_icPending = false;
                if (r.hasError || buf != s_buf) return;
                var d = r.GetData<Vector4>();
                int bad = 0, first = -1; Vector4 fe = default, fg = default;
                for (int j = 0; j < s_icN && j < d.Length; j++)
                {
                    int i = s_icBase + j;
                    if (i >= s_icSkipMin && i <= s_icSkipMax) continue;
                    if (d[j] != s_icExpect[j]) { if (first < 0) { first = i; fe = s_icExpect[j]; fg = d[j]; } bad++; }
                }
                s_icChecks++;
                if (bad == 0) return;
                s_icBad++;
                if (!s_icSaid) { s_icSaid = true; EasyBakeLog.Error($"[Integrity] LOOKS MISMATCH at frame {Time.frameCount}: {bad} slot(s) of {s_icN} differ; first slot {first}: expected {fe}, GPU has {fg}"); }
            });
        }

        /// <summary>
        /// A taken group's values, in EbmIndirect.Sync's slot order (parts x LOD levels x instances, the same skips).
        /// Call after Sync each frame the group is taken; it returns at once unless the version, snow generation or look state moved.
        /// </summary>
        // 1.2.208: each slot carries the group instance it draws (slotInstance), so any registration layout (parts x levels, or per-renderer LOD parts) maps
        internal static void WriteGroup(ZoneInstanceGroup group, List<int> slots, List<int> slotInstance)
        {
            if (group == null || slots == null || slotInstance == null) return;
            if (!s_seen.TryGetValue(group, out var seen)) { seen = new Seen(); s_seen[group] = seen; }   // once per group
            bool active = group.LooksActive;
            int gen = group.SnowGeneration;
            int ext = group.ExtGen;   // 1.2.206: a pushed EbmApi.SetPieceSnow value changed
            if (seen.Version == group.Version && seen.SnowGen == gen && seen.ExtGen == ext && seen.Active == active && seen.Slots == slots.Count) return;
            seen.Version = group.Version; seen.SnowGen = gen; seen.ExtGen = ext; seen.Active = active; seen.Slots = slots.Count;
            int n = Mathf.Min(slots.Count, slotInstance.Count);
            for (int k = 0; k < n; k++) Set(slots[k], active ? group.LookOf(slotInstance[k]) : Plain);
        }

        /// <summary>A group left the indirect path (dropped / unloaded): forget its state. Its slots are cleared by RemoveInstance.</summary>
        internal static void Forget(ZoneInstanceGroup group) { if (group != null) s_seen.Remove(group); }

        /// <summary>Once a frame, before the draws. True = the GPU buffer was (re)made: every batch's property blocks must Bind again.</summary>
        internal static bool Upload()
        {
            if (s_looks.Length == 0) return false;
            bool remade = false;
            if (s_buf == null || s_buf.count < s_looks.Length)
            {
                if (s_buf != null) s_buf.Release();
                s_buf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, s_looks.Length, Stride);
                s_dirtyMin = 0; s_dirtyMax = s_looks.Length - 1;   // a new buffer holds nothing yet
                remade = true;
            }
            if (s_dirtyMax >= s_dirtyMin)
            {
                s_buf.SetData(s_looks, s_dirtyMin, s_dirtyMin, s_dirtyMax - s_dirtyMin + 1);
                s_dirtyMin = int.MaxValue; s_dirtyMax = -1;
            }
            return remade;
        }

        /// <summary>Into a batch's property block (camera and shadow both), next to EbmIndirect.Bind.</summary>
        internal static void Bind(MaterialPropertyBlock mpb)
        {
            if (mpb == null || s_buf == null) return;
            mpb.SetBuffer(IdVedrPieceInst, s_buf);
            mpb.SetBuffer(IdEbmInstLooks, s_buf);
        }

        /// <summary>EbmIndirect.ReleaseAll: the buffer and every group's state go; slots start Plain again.</summary>
        internal static void Release()
        {
            if (s_buf != null) { s_buf.Release(); s_buf = null; }
            s_seen.Clear();
            for (int i = 0; i < s_looks.Length; i++) s_looks[i] = Plain;
            s_dirtyMin = int.MaxValue; s_dirtyMax = -1;
        }
    }
}
