using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.242 ([visual] 11-08, lead: the ~1,100 cascade draws are the biggest lever left). The cascades are culled inside the shadow pass, so at the start of the next
    // frame's cull the args buffer still holds what each cascade row really drew. Twice a second that is read back: the [Cascades] line says recorded rows against rows
    // with instances. With [Optimize] IndirectCascadeEmptySkip a row empty for 4 samples in a row (2 s) is not recorded; it comes back the moment the GPU count for it is
    // above zero (the compute still fills skipped rows), or when the CPU cell verdict is recomputed (the camera moved), so nothing is skipped while moving.
    internal static partial class EbmIndirect
    {
        private const int EmptySamples = 4;
        private const float EmptySampleSeconds = 0.5f;
        private static byte[] s_emptyStreak = new byte[0];
        private static int s_emptyCap;
        private static bool s_emptyAny, s_emptyPending, s_emptyLast;
        private static float s_emptyNext, s_emptySayNext;
        private static readonly int[] s_csRows = new int[4], s_csFull = new int[4];
        private static readonly long[] s_csInst = new long[4];
        private static int s_csSamples, s_csSkipped;

        private static bool EmptySkipOn => FiresEasyBakeMeshesPlugin.IndirectCascadeEmptySkip != null && FiresEasyBakeMeshesPlugin.IndirectCascadeEmptySkip.Value && s_cellsFused && CellReuseOn;

        private static bool EmptySkip(int k, int i) => s_emptyAny && s_emptyLast && s_emptyCap == s_batchCap && i < s_emptyCap && s_emptyStreak[k * s_emptyCap + i] >= EmptySamples;

        // the cells were judged afresh: what was empty may not be any more
        private static void CascadeVerdictChanged()
        {
            if (!s_emptyAny) return;
            System.Array.Clear(s_emptyStreak, 0, s_emptyStreak.Length);
            s_emptyAny = false;
        }

        private static void CascadeEmptyTick()
        {
            bool on = EmptySkipOn;
            if (on != s_emptyLast) { s_emptyLast = on; s_cascadesDirty = true; CascadeVerdictChanged(); }
            bool counts = FiresEasyBakeMeshesPlugin.IndirectCascadeCounts != null && FiresEasyBakeMeshesPlugin.IndirectCascadeCounts.Value;
            if (!(on || counts) || !CascadesOn || s_argsBuf == null || s_emptyPending || s_batchCap <= 0) return;
            float now = Time.unscaledTime;
            if (now < s_emptyNext) return;
            s_emptyNext = now + EmptySampleSeconds;
            int cap = s_batchCap, cascades = Mathf.Min(s_cascadeCount, 4);
            int size = 4 * cap * ArgWords * sizeof(uint), offset = 2 * cap * ArgWords * sizeof(uint);
            if (offset + size > s_argsBuf.count * s_argsBuf.stride) return;
            s_emptyPending = true;
            int epoch = Epoch, batches = s_batches.Count;
            AsyncGPUReadback.Request(s_argsBuf, size, offset, r =>
            {
                s_emptyPending = false;
                if (r.hasError || epoch != Epoch || cap != s_batchCap) return;
                CascadeSample(r.GetData<uint>(), cap, cascades, Mathf.Min(batches, s_batches.Count));
            });
        }

        // [Optimize] IndirectCameraEmptySkip ([wishbone] 11-09): the camera's own rows, by the same rules as the cascades above. A batch the GPU drew nothing of for 4 samples (2 s) is not
        // submitted to the camera while the CPU verdict is being reused; the compute keeps filling its row, so a count above zero brings it back at the next sample. Any re-judged verdict
        // (the camera moved or turned, a piece was added or removed: IndirectCells.Version) clears every streak, so nothing is skipped while moving. Row 0 of the args buffer only.
        private static byte[] s_camEmpty = new byte[0];
        private static int s_camEmptyCap, s_camVerdictGen;
        private static bool s_camEmptyAny, s_camEmptyPending;
        private static float s_camEmptyNext;
        private static bool CameraEmptyOn => FiresEasyBakeMeshesPlugin.IndirectCameraEmptySkip != null && FiresEasyBakeMeshesPlugin.IndirectCameraEmptySkip.Value && s_cellsFused && CellReuseOn;
        private static bool CameraEmptySkip(int i) => s_camEmptyAny && s_camEmptyCap == s_batchCap && i < s_camEmptyCap && s_camEmpty[i] >= EmptySamples && CameraEmptyOn;

        private static void CameraVerdictChanged()
        {
            s_camVerdictGen++;
            if (!s_camEmptyAny) return;
            System.Array.Clear(s_camEmpty, 0, s_camEmpty.Length);
            s_camEmptyAny = false;
        }

        private static void CameraEmptyTick()
        {
            if (!CameraEmptyOn) { if (s_camEmptyAny) CameraVerdictChanged(); return; }
            if (s_argsBuf == null || s_camEmptyPending || s_batchCap <= 0) return;
            float now = Time.unscaledTime;
            if (now < s_camEmptyNext) return;
            s_camEmptyNext = now + EmptySampleSeconds;
            int cap = s_batchCap, size = cap * ArgWords * sizeof(uint);
            if (size > s_argsBuf.count * s_argsBuf.stride) return;
            s_camEmptyPending = true;
            int epoch = Epoch, gen = s_camVerdictGen, batches = s_batches.Count;
            AsyncGPUReadback.Request(s_argsBuf, size, 0, r =>
            {
                s_camEmptyPending = false;
                if (r.hasError || epoch != Epoch || cap != s_batchCap || gen != s_camVerdictGen) return;   // a read from before the verdict changed says nothing about now
                CameraSample(r.GetData<uint>(), cap, Mathf.Min(batches, s_batches.Count));
            });
        }

        private static void CameraSample(NativeArray<uint> args, int cap, int batches)
        {
            if (s_camEmptyCap != cap || s_camEmpty.Length < cap) { s_camEmpty = new byte[cap]; s_camEmptyCap = cap; s_camEmptyAny = false; }
            bool anyDrawn = false;
            for (int i = 0; i < batches && !anyDrawn; i++) anyDrawn = args[i * ArgWords + 1] > 0;
            if (!anyDrawn) return;   // every row zero is a bad read, not an empty world
            bool any = false;
            for (int i = 0; i < batches; i++)
            {
                if (args[i * ArgWords + 1] > 0) s_camEmpty[i] = 0;
                else if (s_camEmpty[i] < 255) s_camEmpty[i]++;
                if (s_camEmpty[i] >= EmptySamples) any = true;
            }
            s_camEmptyAny = any;
        }

        private static void CascadeSample(NativeArray<uint> args, int cap, int cascades, int batches)
        {
            if (s_emptyCap != cap || s_emptyStreak.Length < 4 * cap) { s_emptyStreak = new byte[4 * cap]; s_emptyCap = cap; s_emptyAny = false; }
            // 1.2.244 ([generator]): rows all reading zero is a bad read, not an empty world; it must not count toward leaving rows out
            bool anyDrawn = false;
            for (int k = 0; k < cascades && !anyDrawn; k++)
                for (int i = 0; i < batches && !anyDrawn; i++) anyDrawn = args[(k * cap + i) * ArgWords + 1] > 0;
            if (!anyDrawn) return;
            bool any = false;
            int skipped = 0;
            for (int k = 0; k < cascades; k++)
                for (int i = 0; i < batches; i++)
                {
                    uint instances = args[(k * cap + i) * ArgWords + 1];
                    int slot = k * cap + i;
                    bool recorded = s_recCap >= batches && s_recCascade != null && s_recCascade[k * s_recCap + i];
                    if (recorded) { s_csRows[k]++; if (instances > 0) s_csFull[k]++; }
                    s_csInst[k] += instances;
                    if (instances > 0) s_emptyStreak[slot] = 0;
                    else if (s_emptyStreak[slot] < 255) s_emptyStreak[slot]++;
                    if (s_emptyStreak[slot] >= EmptySamples) { any = true; if (!recorded) skipped++; }
                }
            s_emptyAny = any;
            s_csSkipped = skipped;
            s_csSamples++;
            float now = Time.unscaledTime;
            if (now < s_emptySayNext || !EasyBakeLog.DebugOn()) return;
            s_emptySayNext = now + 30f;
            var sb = new System.Text.StringBuilder("[Cascades] over ").Append(s_csSamples).Append(" samples, a sample:");
            for (int k = 0; k < cascades; k++)
            {
                sb.Append($" cascade {k + 1}: {(double)s_csRows[k] / s_csSamples:0} rows recorded, {(double)s_csFull[k] / s_csSamples:0} with instances, {(double)s_csInst[k] / s_csSamples:0} instances;");
                s_csRows[k] = s_csFull[k] = 0; s_csInst[k] = 0;
            }
            sb.Append($" skip {(s_emptyLast ? "on" : "off")}, {s_csSkipped} rows left out now");
            s_csSamples = 0;
            EasyBakeLog.Debug(sb.ToString());
        }
    }
}
