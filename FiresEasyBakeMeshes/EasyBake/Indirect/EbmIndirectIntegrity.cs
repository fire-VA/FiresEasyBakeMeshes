using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.226 ([visual], the stretch, lead: "evidence before any more theory"): the GPU copies of the two buffers whose corruption can stretch
    // pieces are read back and compared with what EBM's CPU last uploaded:
    //  - the indirect args: every row's index count / start index / base vertex / start instance (words 0, 2, 3, 4; word 1 is the culled
    //    count the GPU writes) - a wrong start index or base vertex draws one mesh's indices against another range = stretched triangles;
    //  - the instance matrices (object-to-world rows) of every slot outside the range still waiting to upload.
    // Args once a second, instances 8,192 slots a second (rolling), one readback in flight each. A mismatch logs ONE Error with the frame, the buffer, the first bad
    // row / slot, expected vs found, how many differ, and what EBM last did to that buffer (layout / grow / upload frames). Then it keeps
    // counting quietly and says the totals every 30 s. [Diagnostics] IndirectIntegrityCheck (default on while the stretch is open).
    internal static partial class EbmIndirect
    {
        private static bool IntegrityOn => FiresEasyBakeMeshesPlugin.IndirectIntegrityCheck == null || FiresEasyBakeMeshesPlugin.IndirectIntegrityCheck.Value;
        private static float s_icNextArgs, s_icNextInst, s_icNextSay, s_icNextLooks;
        private static bool s_icArgsPending, s_icInstPending, s_icArgsSaid, s_icInstSaid, s_icSaidOn;
        private static uint[] s_icArgsExpect = new uint[0]; private static int s_icArgsRows, s_icArgsCap;
        private static GpuInstance[] s_icInstExpect = new GpuInstance[0]; private static int s_icInstHigh, s_icInstBase, s_icInstStart, s_icSkipMin, s_icSkipMax;
        private const int InstWindow = 8192;
        private static int s_icArgsChecks, s_icArgsBad, s_icInstChecks, s_icInstBad;
        internal static int s_lastLayoutFrame = -1, s_lastGrowFrame = -1;

        // 1.2.234 ([visual], the near stretch): the args rows are written from each batch mesh's submesh ONCE (layout). If anything later changes
        // that shared mesh (another mod, a vanilla private-mesh swap, a reimport), the GPU args still match EBM's CPU copy (so the args check
        // passes) but no longer match the mesh: one mesh's index range drawn against changed vertices = stretched triangles. Rolling 64 batches a
        // frame: the stored index count / start / base vertex vs the mesh's submesh NOW. A mismatch logs an Error (mesh, old vs new) and the
        // batch's rows are rewritten from the mesh at once (self-healing).
        private static int s_msCursor, s_msHealed; private static bool s_msSaid;
        private static void MeshDriftTick(int nb)
        {
            if (nb == 0 || s_argsBuf == null) return;
            for (int n = 0; n < 64 && n < nb; n++)
            {
                if (s_msCursor >= nb) s_msCursor = 0;
                int i = s_msCursor++;
                var b = s_batches[i];
                if (b.Mesh == null || b.Count <= 0) continue;   // destroyed (Unity-null) or empty: nothing drawn
                int a = i * ArgWords;
                if (a + 3 >= s_args.Length) continue;
                try
                {
                int subs = b.Mesh.subMeshCount;
                if (subs <= 0) continue;
                bool subGone = b.SubMesh >= subs;
                var sm = b.Mesh.GetSubMesh(Mathf.Clamp(b.SubMesh, 0, subs - 1));
                if (!subGone && b.Mesh.vertexCount == b.VertCount && s_args[a] == (uint)sm.indexCount && s_args[a + 2] == (uint)sm.indexStart && s_args[a + 3] == (uint)sm.baseVertex) continue;
                s_msHealed++;
                if (!s_msSaid) { s_msSaid = true; EasyBakeLog.Error($"[Integrity] MESH CHANGED UNDER A BATCH at frame {Time.frameCount}: '{b.Mesh.name}' submesh {b.SubMesh}: args had count {s_args[a]} start {s_args[a + 2]} base {s_args[a + 3]}, the mesh now has count {sm.indexCount} start {sm.indexStart} base {sm.baseVertex}, {b.Mesh.vertexCount} vertices (was {b.VertCount}){(subGone ? ", submesh gone" : "")}; rows + bounds rewritten (CPU cells refresh at the next layout). Later ones are counted on the [Integrity] line."); }
                b.LocalBounds = sm.bounds;   // the new submesh bounds (cells refresh at the next layout)
                WriteBatchRows(i);
                for (int v = 0; v < Views; v++) { int z = (v * s_batchCap + i) * ArgWords; s_argsBuf.SetData(s_args, z, z, ArgWords); }
                if (s_batchBuf != null && i < s_gpuBatches.Length) s_batchBuf.SetData(s_gpuBatches, i, i, 1);   // [generator]: CPU and GPU batch rows stay equal
                }
                catch (System.Exception e) { EasyBakeLog.Debug("[Integrity] mesh drift check skipped a batch: " + e.Message); }
            }
        }

        private static void IntegrityTick(int nb)
        {
            if (!Active || s_argsBuf == null || s_instBuf == null) return;
            MeshDriftTick(nb);   // 1.2.234: always on (64 cheap compares a frame), not only with the integrity key
            MeshContentTick(nb);   // 1.2.235: its own Diagnostics key
            MeshFullTick();   // 1.2.238
            if (!IntegrityOn) return;
            if (!s_icSaidOn) { s_icSaidOn = true; EasyBakeLog.Info("[Integrity] on: EBM's indirect args (every 1 s) and instance matrices (8,192 slots a second, rolling) are read back from the GPU and compared with what EBM uploaded; a mismatch logs an Error with the frame"); }
            float now = Time.unscaledTime;
            if (!s_icArgsPending && now >= s_icNextArgs && nb > 0)
            {
                s_icNextArgs = now + 1f;
                int rows = s_batchCap * Views, words = rows * ArgWords;
                if (words <= s_args.Length && words <= s_argsBuf.count)
                {
                    if (s_icArgsExpect.Length < words) s_icArgsExpect = new uint[words];
                    System.Array.Copy(s_args, s_icArgsExpect, words);
                    s_icArgsRows = rows; s_icArgsCap = s_batchCap; s_icArgsPending = true;
                    var buf = s_argsBuf;
                    AsyncGPUReadback.Request(buf, words * sizeof(uint), 0, r => OnArgs(r, buf));
                }
            }
            if (now >= s_icNextLooks) { s_icNextLooks = now + 1f; IndirectLooks.IntegrityTick(s_high); }   // 1.2.232
            if (!s_icInstPending && now >= s_icNextInst && s_high > 0 && s_high <= s_inst.Length && s_high <= s_instBuf.count)
            {
                // [generator]: a rolling window of InstWindow slots a second (no multi-ms full copy / readback every 10 s)
                s_icNextInst = now + 1f;
                if (s_icInstStart >= s_high) s_icInstStart = 0;
                int n = Mathf.Min(InstWindow, s_high - s_icInstStart);
                if (s_icInstExpect.Length < n) s_icInstExpect = new GpuInstance[InstWindow];
                System.Array.Copy(s_inst, s_icInstStart, s_icInstExpect, 0, n);
                s_icInstHigh = n; s_icInstBase = s_icInstStart; s_icSkipMin = s_dirtyMin; s_icSkipMax = s_dirtyMax; s_icInstPending = true;
                s_icInstStart += n;
                int stride = System.Runtime.InteropServices.Marshal.SizeOf<GpuInstance>();
                var buf = s_instBuf;
                AsyncGPUReadback.Request(buf, n * stride, s_icInstBase * stride, r => OnInst(r, buf));
            }
            if (now >= s_icNextSay)
            {
                if (s_icNextSay > 0f && EasyBakeLog.DebugOn())
                    EasyBakeLog.Debug($"[Integrity] 30 s: args checks {s_icArgsChecks} ({s_icArgsBad} with a mismatch), instance checks {s_icInstChecks} ({s_icInstBad} with a mismatch), looks checks {IndirectLooks.s_icChecks} ({IndirectLooks.s_icBad} with a mismatch); last layout frame {s_lastLayoutFrame}, last grow frame {s_lastGrowFrame}; mesh-changed batches healed {s_msHealed}; {MeshContentLine()}; bad instances not drawn {s_badInst}");
                s_icNextSay = now + 30f; s_icArgsChecks = s_icArgsBad = s_icInstChecks = s_icInstBad = 0;
            }
        }

        private static void OnArgs(AsyncGPUReadbackRequest r, GraphicsBuffer buf)
        {
            s_icArgsPending = false;
            if (r.hasError || buf != s_argsBuf || s_icArgsCap != s_batchCap) return;   // re-laid out since: the snapshot is stale, skip
            var d = r.GetData<uint>();
            int bad = 0, first = -1, fw = -1; uint fe = 0, fg = 0;
            int words = Mathf.Min(d.Length, s_icArgsRows * ArgWords);
            for (int i = 0; i < words; i++)
            {
                if (i % ArgWords == 1) continue;   // the culled count: GPU-written
                if (d[i] != s_icArgsExpect[i]) { if (first < 0) { first = i / ArgWords; fw = i % ArgWords; fe = s_icArgsExpect[i]; fg = d[i]; } bad++; }
            }
            s_icArgsChecks++;
            if (bad == 0) return;
            s_icArgsBad++;
            if (!s_icArgsSaid)
            {
                s_icArgsSaid = true;
                int view = first / Mathf.Max(1, s_icArgsCap), batch = first % Mathf.Max(1, s_icArgsCap);
                string name = batch < s_batches.Count && s_batches[batch].Mesh != null ? s_batches[batch].Mesh.name : "?";
                EasyBakeLog.Error($"[Integrity] ARGS MISMATCH at frame {Time.frameCount}: {bad} word(s) differ; first row {first} (view {view}, batch {batch} '{name}') word {fw} ({(fw == 0 ? "index count" : fw == 2 ? "start index" : fw == 3 ? "base vertex" : "start instance")}): expected {fe}, GPU has {fg}. Last layout frame {s_lastLayoutFrame}, last grow frame {s_lastGrowFrame}, same-frame cascades {(s_cascSeparate ? "on" : "off")}");
            }
        }

        private static void OnInst(AsyncGPUReadbackRequest r, GraphicsBuffer buf)
        {
            s_icInstPending = false;
            if (r.hasError || buf != s_instBuf) return;
            var d = r.GetData<GpuInstance>();
            int n = Mathf.Min(d.Length, s_icInstHigh), bad = 0, first = -1; GpuInstance fe = default, fg = default;
            for (int j = 0; j < n; j++)
            {
                int i = s_icInstBase + j;
                if (i >= s_icSkipMin && i <= s_icSkipMax) continue;   // still waiting to upload at request time
                if (i >= s_instBatch.Length || s_instBatch[i] == 0) continue;   // a free slot: never drawn
                var a = s_icInstExpect[j]; var g = d[j];
                if (a.O0 != g.O0 || a.O1 != g.O1 || a.O2 != g.O2 || a.W0 != g.W0 || a.W1 != g.W1 || a.W2 != g.W2) { if (first < 0) { first = i; fe = a; fg = g; } bad++; }   // 1.2.232: + the world-to-object rows
            }
            s_icInstChecks++;
            if (bad == 0) return;
            s_icInstBad++;
            if (!s_icInstSaid)
            {
                s_icInstSaid = true;
                EasyBakeLog.Error($"[Integrity] INSTANCE MISMATCH at frame {Time.frameCount}: {bad} slot(s) of {n} differ; first slot {first}: expected O {fe.O0} {fe.O1} {fe.O2} W {fe.W0} {fe.W1} {fe.W2}, GPU has O {fg.O0} {fg.O1} {fg.O2} W {fg.W0} {fg.W1} {fg.W2}. Last layout frame {s_lastLayoutFrame}, last grow frame {s_lastGrowFrame}, same-frame cascades {(s_cascSeparate ? "on" : "off")}");
            }
        }
    }
}
