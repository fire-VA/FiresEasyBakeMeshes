using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.243 ([visual] 11-08, Fire: it is the camera draw, and a capture hides it): two ways to see the stretch from inside the game.
    //   [Optimize] IndirectVertexDebug: EBM/Piece and EBM/StaticRockIndirect paint bad vertices a solid colour (see EbmIndirectInstancing.cginc).
    //   ebm_batchread [batch]: one read of what the GPU holds for a batch (the last FOUND by default): its visible list and instance data against EBM's own.
    internal static partial class EbmIndirect
    {
        private const string VertexDebugKeyword = "EBM_VERTDEBUG";
        private static readonly int s_dbgRadiusId = Shader.PropertyToID("_EbmDbgRadius");
        private static bool s_vertexDebugSet;

        // 1.2.251: global keyword, follows its key live; said once per change
        private static bool s_pixelShaveSet;
        private static void ApplyPixelShave()
        {
            bool want = FiresEasyBakeMeshesPlugin.IndirectPiecePixelShave != null && FiresEasyBakeMeshesPlugin.IndirectPiecePixelShave.Value;
            if (want == s_pixelShaveSet) return;
            s_pixelShaveSet = want;
            if (want) Shader.EnableKeyword("EBM_PIXEL_SHAVE"); else Shader.DisableKeyword("EBM_PIXEL_SHAVE");
            EasyBakeLog.Info("[Indirect] piece pixel shave " + (want ? "on" : "off"));
        }

        private static void ApplyVertexDebug()
        {
            bool want = FiresEasyBakeMeshesPlugin.IndirectVertexDebug != null && FiresEasyBakeMeshesPlugin.IndirectVertexDebug.Value;
            if (want == s_vertexDebugSet) return;
            s_vertexDebugSet = want;
            if (want) Shader.EnableKeyword(VertexDebugKeyword); else Shader.DisableKeyword(VertexDebugKeyword);
            s_bindingsDirty = true;
            EasyBakeLog.Info("[VertexCheck] colour marks " + (want ? "ON: orange index, red input, magenta matrix, yellow world position, white clip, cyan w" : "off"));
        }

        private static void BindDebugRadius(Batch b) => b.CameraProps.SetFloat(s_dbgRadiusId, b.LocalBounds.center.magnitude + b.LocalBounds.extents.magnitude);

        internal static string BatchRead(string arg)
        {
            int index = -1;
            if (!string.IsNullOrEmpty(arg)) { if (!int.TryParse(arg, out index)) return "Usage: ebm_batchread [batch index]; no number = the last FOUND batch"; }
            else if (s_bisectFound.Count > 0) index = s_bisectFound[s_bisectFound.Count - 1];
            if (index < 0 || index >= s_batches.Count) return "[BatchRead] no such batch; use ebm_bisect to find one, or give an index";
            if (s_instBuf == null || s_visibleBuf == null || s_argsBuf == null) return "[BatchRead] the indirect buffers are not there; is IndirectRenderer on?";
            var b = s_batches[index];
            int high = Mathf.Min(s_high, s_instBuf.count);
            int capacity = Mathf.Min(b.Capacity, s_visibleBuf.count - b.VisibleOffset);
            if (capacity <= 0 || high <= 0) return "[BatchRead] this batch has no range";
            int rowBytes = ArgWords * sizeof(uint);
            uint[] args = null, visible = null; GpuInstance[] gpu = null; bool failed = false; int left = 3;
            void Landed()
            {
                if (--left > 0) return;
                if (failed || args == null || visible == null || gpu == null) { EasyBakeLog.Info("[BatchRead] a GPU read failed"); return; }
                Compare(b, index, args, 0, visible, 0, visible.Length, gpu, false);
            }
            AsyncGPUReadback.Request(s_argsBuf, rowBytes, index * rowBytes, r => { if (r.hasError) failed = true; else args = r.GetData<uint>().ToArray(); Landed(); });
            AsyncGPUReadback.Request(s_visibleBuf, capacity * sizeof(uint), b.VisibleOffset * sizeof(uint), r => { if (r.hasError) failed = true; else visible = r.GetData<uint>().ToArray(); Landed(); });
            AsyncGPUReadback.Request(s_instBuf, r => { if (r.hasError) failed = true; else gpu = r.GetData<GpuInstance>().ToArray(); Landed(); });
            return $"[BatchRead] reading batch {index}: {DescribeBatch(index)}";
        }

        // 1.2.243: with the colour marks on, every batch's list and instance rows are checked every 2 s; each mesh + failed test is named once
        private static readonly HashSet<string> s_sweepNamed = new HashSet<string>();
        private static float s_sweepNext; private static bool s_sweepPending;

        private static void VertexSweepTick()
        {
            if (!s_vertexDebugSet || s_sweepPending || s_instBuf == null || s_visibleBuf == null || s_argsBuf == null || Time.unscaledTime < s_sweepNext) return;
            s_sweepNext = Time.unscaledTime + 2f;
            s_sweepPending = true;
            int epoch = Epoch, cap = s_batchCap, stride = s_visibleStride, cellsVersion = s_cells.Version;
            uint[] args = null, visible = null; GpuInstance[] gpu = null; bool failed = false; int left = 3;
            void Landed()
            {
                if (--left > 0) return;
                s_sweepPending = false;
                // 1.2.244 ([generator]): anything added, removed or moved since the read would look like bad data
                if (failed || epoch != Epoch || cap != s_batchCap || stride != s_visibleStride || cellsVersion != s_cells.Version) return;
                int batches = s_batches.Count;
                for (int i = 0; i < batches && i < cap; i++)
                {
                    var b = s_batches[i];
                    if (b.Count <= 0 || b.Mesh == null) continue;
                    Compare(b, i, args, i * ArgWords, visible, b.VisibleOffset, Mathf.Min(b.Capacity, visible.Length - b.VisibleOffset), gpu, true);
                }
            }
            int rowBytes = ArgWords * sizeof(uint);
            AsyncGPUReadback.Request(s_argsBuf, cap * rowBytes, 0, r => { if (r.hasError) failed = true; else args = r.GetData<uint>().ToArray(); Landed(); });
            AsyncGPUReadback.Request(s_visibleBuf, stride * sizeof(uint), 0, r => { if (r.hasError) failed = true; else visible = r.GetData<uint>().ToArray(); Landed(); });
            AsyncGPUReadback.Request(s_instBuf, r => { if (r.hasError) failed = true; else gpu = r.GetData<GpuInstance>().ToArray(); Landed(); });
        }

        private static bool RowOk(Vector4 v) => !float.IsNaN(v.x + v.y + v.z + v.w) && !float.IsInfinity(v.x + v.y + v.z + v.w);

        private static void Compare(Batch b, int index, uint[] args, int argAt, uint[] visible, int visibleAt, int room, GpuInstance[] gpu, bool sweep)
        {
            uint drawn = args[argAt + 1];
            int outOfRange = 0, foreign = 0, differ = 0, notFinite = 0;
            float maxScale = 0f;
            var examples = new List<string>();
            int n = Mathf.Min((int)drawn, room);
            for (int j = 0; j < n; j++)
            {
                uint slot = visible[visibleAt + j];
                if (slot >= gpu.Length || slot >= s_inst.Length) { outOfRange++; if (examples.Count < 8) examples.Add($"list[{j}] = {slot}, past the {gpu.Length} slots"); continue; }
                if (s_instBatch[slot] != (uint)(index + 1)) { foreign++; if (examples.Count < 8) examples.Add($"list[{j}] = {slot}, owned by batch {(int)s_instBatch[slot] - 1}"); }
                var g = gpu[slot]; var c = s_inst[slot];
                if (!RowOk(g.O0) || !RowOk(g.O1) || !RowOk(g.O2)) { notFinite++; if (examples.Count < 8) examples.Add($"slot {slot}: not finite"); continue; }
                if ((g.O0 - c.O0).sqrMagnitude > 1e-4f || (g.O1 - c.O1).sqrMagnitude > 1e-4f || (g.O2 - c.O2).sqrMagnitude > 1e-4f) { differ++; if (examples.Count < 8) examples.Add($"slot {slot}: GPU {g.O0} {g.O1} {g.O2}, EBM {c.O0} {c.O1} {c.O2}"); }
                maxScale = Mathf.Max(maxScale, new Vector3(g.O0.x, g.O1.x, g.O2.x).magnitude, new Vector3(g.O0.y, g.O1.y, g.O2.y).magnitude, new Vector3(g.O0.z, g.O1.z, g.O2.z).magnitude);
            }
            var sb = new StringBuilder($"[BatchRead] batch {index}: args index count {args[argAt]}, drawn {drawn}, start {args[argAt + 2]}, base {(int)args[argAt + 3]}, first instance {args[argAt + 4]}; list room {room}; of {n} read: {outOfRange} past the end, {foreign} from another batch, {notFinite} not finite, {differ} differ from EBM; largest scale {maxScale:0.###}");
            if (drawn > room) sb.Append($"; DRAWN COUNT OVERRUNS ITS RANGE by {drawn - room}");
            foreach (var e in examples) sb.Append(" | ").Append(e);
            if (sweep)
            {
                string failed = outOfRange > 0 ? "visible index past the instance buffer" : foreign > 0 ? "visible index owned by another batch" : notFinite > 0 ? "instance row not finite" : differ > 0 ? "instance row differs from EBM" : drawn > room ? "drawn count overruns its range" : null;
                if (failed == null || b.Mesh == null || !s_sweepNamed.Add(b.Mesh.GetInstanceID() + "|" + failed)) return;
                EasyBakeLog.Error($"[VertexCheck] INSTANCE DATA BAD at frame {Time.frameCount}: {failed}; {DescribeBatch(index)}. {sb}");
                return;
            }
            EasyBakeLog.Info(sb.ToString());
        }
    }
}
