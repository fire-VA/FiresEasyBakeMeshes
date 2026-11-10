using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.245 ([visual] 11-08, Fire: tests that read what they need, no guessing). [Diagnostics] IndirectGpuMeshValidator, off by default.
    //   Validator: each live batch mesh's GPU vertex + index data, as the GPU holds it, is read back and checked: every position finite and inside the mesh's own bounds, every
    //   index + base vertex inside the vertex count, the data unchanged since its first read. One mesh in flight at a time; a lap every 20 s. No shader, no UAV, no Raw flag.
    //   Raw probe: FarBuildings sets Raw on a non-readable shared mesh in ReadGpu. The GPU data is hashed before, the flag set, then hashed again 3 frames and 2 s later.
    // Machine-read lines: "[GpuValidator] LAP", "[GpuValidator] BAD", "[RawProbe] RESULT".
    internal static partial class EbmIndirect
    {
        private static bool ValidatorOn => FiresEasyBakeMeshesPlugin.IndirectGpuMeshValidator != null && FiresEasyBakeMeshesPlugin.IndirectGpuMeshValidator.Value;
        private const int MaxValidatorBytes = 32 * 1024 * 1024;
        private const float LapSeconds = 20f, StuckSeconds = 10f;

        private sealed class GpuRead { public byte[] Vertex, Index; public string Error; public int Stride, Offset, VertexCount; public IndexFormat Format; public ulong Hash; }

        private static readonly Queue<Mesh> s_gvQueue = new Queue<Mesh>();
        private static readonly Dictionary<int, ulong> s_gvHash = new Dictionary<int, ulong>();
        private static readonly HashSet<string> s_gvNamed = new HashSet<string>();
        private static readonly Dictionary<int, string> s_rawTouched = new Dictionary<int, string>();
        private static bool s_gvBusy, s_gvLapOpen; private static float s_gvNextLap, s_gvBusySince;
        private static int s_gvLap, s_gvChecked, s_gvBad, s_gvNull, s_gvChanged, s_gvOtherFormat, s_gvGeneration, s_gvCompared, s_gvRangeOnly;
        private static readonly HashSet<int> s_gvCpuDone = new HashSet<int>(), s_gvCpuFailed = new HashSet<int>();
        private static readonly List<Vector3> s_gvCpuVerts = new List<Vector3>();
        private static readonly List<int> s_gvCpuIndices = new List<int>();

        private static ulong Fnv(byte[] d, ulong x = 1469598103934665603UL) { for (int k = 0; k < d.Length; k++) x = (x ^ d[k]) * 1099511628211UL; return x; }

        // reads the position stream and index buffer as the GPU has them; done runs once, on the main thread
        private static void ReadGpuData(Mesh mesh, Action<GpuRead> done)
        {
            var read = new GpuRead();
            GraphicsBuffer vb = null, ib = null;
            try
            {
                if (!mesh.HasVertexAttribute(VertexAttribute.Position)) { read.Error = "no position attribute"; done(read); return; }
                int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                read.Offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
                read.Stride = mesh.GetVertexBufferStride(stream);
                read.VertexCount = mesh.vertexCount;
                read.Format = mesh.indexFormat;
                if (mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 || mesh.GetVertexAttributeDimension(VertexAttribute.Position) != 3) { read.Error = "position is not Float32x3"; done(read); return; }
                vb = mesh.GetVertexBuffer(stream);
                ib = mesh.GetIndexBuffer();
                if (vb == null || ib == null) { vb?.Dispose(); ib?.Dispose(); read.Error = vb == null ? "vertex buffer is null" : "index buffer is null"; done(read); return; }
                int vBytes = vb.count * vb.stride, iBytes = ib.count * ib.stride;
                if (vBytes > MaxValidatorBytes || iBytes > MaxValidatorBytes) { vb.Dispose(); ib.Dispose(); read.Error = "too large"; done(read); return; }
                var vbLocal = vb; var ibLocal = ib; int left = 2;
                void Landed()
                {
                    if (--left > 0) return;
                    vbLocal.Dispose(); ibLocal.Dispose();
                    if (read.Vertex != null && read.Index != null) read.Hash = Fnv(read.Index, Fnv(read.Vertex));
                    done(read);
                }
                AsyncGPUReadback.Request(vbLocal, r => { if (r.hasError) read.Error = "vertex readback failed"; else read.Vertex = r.GetData<byte>().ToArray(); Landed(); });
                AsyncGPUReadback.Request(ibLocal, r => { if (r.hasError) read.Error = "index readback failed"; else read.Index = r.GetData<byte>().ToArray(); Landed(); });
            }
            catch (Exception e)
            {
                vb?.Dispose(); ib?.Dispose();
                read.Error = e.Message;
                done(read);
            }
        }

        private static void GpuValidatorTick()
        {
            if (!ValidatorOn) return;
            float now = Time.unscaledTime;
            if (s_gvBusy)
            {
                if (now - s_gvBusySince < StuckSeconds) return;
                s_gvBusy = false; s_gvGeneration++; s_gvChecked++; s_gvNull++;   // its read never landed: it was not cleared
            }
            if (s_gvQueue.Count == 0)
            {
                if (s_gvLapOpen)
                {
                    s_gvLapOpen = false;
                    // a lap that could not read a mesh has not cleared it: only an all-read clean lap is PASS
                    int readOk = s_gvChecked - s_gvNull - s_gvOtherFormat;
                    // PASS-COMPARED = every mesh matched its CPU copy exactly; PASS-MIXED = some could only be range-checked, and zeroed data can pass a range check
                    string verdict = s_gvBad > 0 || s_gvChanged > 0 ? "FAIL" : readOk <= 0 ? "NODATA" : readOk < s_gvChecked ? "PARTIAL" : s_gvRangeOnly > 0 ? "PASS-MIXED" : "PASS-COMPARED";
                    EasyBakeLog.Debug($"[GpuValidator] LAP {s_gvLap} {verdict}: {s_gvChecked} meshes read, {s_gvCompared} compared to CPU, {s_gvRangeOnly} range-only, {s_gvArgsChecked} GPU arg rows matched to the live mesh, {s_gvBad} with bad data, {s_gvChanged} changed since first read, {s_gvNull} unreadable, {s_gvOtherFormat} not Float32 positions, {s_rawTouched.Count} touched by FarBuildings Raw, at frame {Time.frameCount}");
                }
                if (now < s_gvNextLap) return;
                s_gvNextLap = now + LapSeconds;
                var seen = new HashSet<int>();
                foreach (var b in s_batches) if (b.Count > 0 && b.Mesh != null && seen.Add(b.Mesh.GetInstanceID())) s_gvQueue.Enqueue(b.Mesh);
                if (s_gvQueue.Count == 0) return;
                s_gvLap++; s_gvLapOpen = true; s_gvChecked = s_gvBad = s_gvNull = s_gvChanged = s_gvOtherFormat = s_gvCompared = s_gvRangeOnly = s_gvArgsChecked = 0;
                CheckBatchLayout();
            }
            var mesh = s_gvQueue.Dequeue();
            if (mesh == null) { s_gvChecked++; s_gvNull++; return; }
            s_gvBusy = true; s_gvBusySince = now;
            int generation = s_gvGeneration;
            ReadGpuData(mesh, read => { if (generation != s_gvGeneration) return; s_gvBusy = false; if (mesh != null) Validate(mesh, read); else { s_gvChecked++; s_gvNull++; } });
        }

        private static void NameBad(Mesh mesh, string kind, string detail)
        {
            if (!s_gvNamed.Add(mesh.GetInstanceID() + "|" + kind)) { EasyBakeLog.Debug($"[GpuValidator] again: '{mesh.name}' id {mesh.GetInstanceID()}: {kind}"); return; }
            int batch = s_batches.FindIndex(b => b.Mesh == mesh);
            s_rawTouched.TryGetValue(mesh.GetInstanceID(), out string raw);
            EasyBakeLog.Error($"[GpuValidator] BAD {kind}: mesh '{mesh.name}' id {mesh.GetInstanceID()}, {detail}; FarBuildings Raw: {raw ?? "not touched"}; first batch: {(batch >= 0 ? DescribeBatch(batch) : "none")}");
        }

        private static void Validate(Mesh mesh, GpuRead read)
        {
            s_gvChecked++;
            if (read.Error != null || read.Vertex == null || read.Index == null)
            {
                if (read.Error == "position is not Float32x3") s_gvOtherFormat++; else s_gvNull++;
                string why = read.Error ?? "no data";
                if (s_gvNamed.Add(mesh.GetInstanceID() + "|unreadable|" + why)) EasyBakeLog.Debug($"[GpuValidator] unreadable: mesh '{mesh.name}' id {mesh.GetInstanceID()}, {mesh.vertexCount} verts, readable {mesh.isReadable}: {why}");
                return;
            }
            if (read.VertexCount != mesh.vertexCount) { s_gvNull++; return; }   // changed while reading: not cleared; the next lap sees it
            int id = mesh.GetInstanceID();
            bool newData = true;
            if (s_gvHash.TryGetValue(id, out ulong first))
            {
                newData = first != read.Hash;
                if (newData) { s_gvChanged++; NameBad(mesh, "CHANGED", $"its GPU data differs from the first read this session ({first:X16} then {read.Hash:X16})"); s_gvHash[id] = read.Hash; }
            }
            else s_gvHash[id] = read.Hash;

            // a readable mesh has the truth beside it: the GPU copy must match the CPU copy exactly. Re-run only for new data; a failure counts on every lap.
            bool compared = mesh.isReadable;
            if (compared)
            {
                if (s_gvCpuDone.Add(id) | newData)
                {
                    string diff = CompareToCpu(mesh, read);
                    if (diff != null) { s_gvCpuFailed.Add(id); NameBad(mesh, "CPUDIFF", diff); } else s_gvCpuFailed.Remove(id);
                }
                if (s_gvCpuFailed.Contains(id)) s_gvBad++;
                s_gvCompared++;
            }
            else s_gvRangeOnly++;
            bool matchesCpu = compared && !s_gvCpuFailed.Contains(id);

            var bounds = mesh.bounds;
            float slack = 0.01f + 0.01f * bounds.size.magnitude;
            Vector3 min = bounds.min - Vector3.one * slack, max = bounds.max + Vector3.one * slack;
            int bad = 0, sample = -1, zeroVerts = 0; Vector3 samplePos = Vector3.zero;
            int count = read.Stride > 0 ? Mathf.Min(read.VertexCount, read.Vertex.Length / read.Stride) : 0;
            for (int v = 0; v < count; v++)
            {
                int at = v * read.Stride + read.Offset;
                float x = BitConverter.ToSingle(read.Vertex, at), y = BitConverter.ToSingle(read.Vertex, at + 4), z = BitConverter.ToSingle(read.Vertex, at + 8);
                if (x == 0f && y == 0f && z == 0f) zeroVerts++;
                if (float.IsNaN(x + y + z) || float.IsInfinity(x + y + z) || x < min.x || y < min.y || z < min.z || x > max.x || y > max.y || z > max.z)
                {
                    bad++;
                    if (sample < 0) { sample = v; samplePos = new Vector3(x, y, z); }
                }
            }
            int badIndices = 0, firstBad = -1, zeroIndices = 0, allIndices = 0, cutSubmeshes = 0;
            int indexSize = read.Format == IndexFormat.UInt16 ? 2 : 4;
            int indexRoom = read.Index.Length / indexSize;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var sm = mesh.GetSubMesh(s);
                if (sm.indexStart + sm.indexCount > indexRoom) cutSubmeshes++;
                int end = Mathf.Min(sm.indexStart + sm.indexCount, indexRoom);
                for (int i = sm.indexStart; i < end; i++)
                {
                    int raw = indexSize == 2 ? BitConverter.ToUInt16(read.Index, i * 2) : (int)BitConverter.ToUInt32(read.Index, i * 4);
                    allIndices++; if (raw == 0) zeroIndices++;
                    int value = raw + sm.baseVertex;
                    if (value < 0 || value >= read.VertexCount) { badIndices++; if (firstBad < 0) firstBad = value; }
                }
            }
            if (count < read.VertexCount) { s_gvBad++; NameBad(mesh, "SHORT", $"its GPU vertex buffer holds {count} of {read.VertexCount} vertices"); return; }
            if (cutSubmeshes > 0) { s_gvBad++; NameBad(mesh, "SHORTINDEX", $"{cutSubmeshes} submesh(es) run past its GPU index buffer of {indexRoom} indices; readable {mesh.isReadable}"); }
            // a range check cannot see zeroed data (0,0,0 is inside most bounds; index 0 is in range), so with no CPU copy a large zero share is called out
            // a real triangle holds index 0 at most once (over a third needs degenerate triangles); a cone with its tip on the pivot can reach half its vertices at the origin
            if (!compared && (count >= 8 && zeroVerts * 2 > count || allIndices >= 12 && zeroIndices * 3 > allIndices)) { s_gvBad++; NameBad(mesh, "ZEROED", $"no CPU copy to compare; {zeroVerts} of {count} positions are exactly zero, {zeroIndices} of {allIndices} indices are 0"); }
            else if (!compared && count >= 8 && zeroVerts * 4 >= count && s_gvNamed.Add(id + "|manyzero")) EasyBakeLog.Debug($"[GpuValidator] '{mesh.name}': {zeroVerts} of {count} positions are exactly zero (no CPU copy; legal for a cone on its pivot)");
            if (bad > 0 && matchesCpu) { if (s_gvNamed.Add(id + "|authored")) EasyBakeLog.Debug($"[GpuValidator] '{mesh.name}': {bad} of {read.VertexCount} vertices outside its bounds, but the GPU matches the CPU copy exactly, so the bounds are as authored"); }
            else if (bad > 0) { s_gvBad++; NameBad(mesh, "POSITION", $"{bad} of {read.VertexCount} vertices outside bounds {bounds.center:F2} size {bounds.size:F2}; first #{sample} at {samplePos:F2}; readable {mesh.isReadable}"); }
            if (badIndices > 0) { s_gvBad++; NameBad(mesh, "INDEX", $"{badIndices} indices past its {read.VertexCount} vertices, first value {firstBad}; readable {mesh.isReadable}"); }
        }

        // Once a lap, for the camera view's rows: a non-triangle submesh, a combined static-batch mesh, and the drawn index range on the GPU against the live mesh.
        // Only batches holding the same mesh as the lap before are compared, so a row that is new this frame and not uploaded yet is never called bad.
        private static int s_gvArgsChecked, s_gvStaticCovered;
        private static int[] s_gvPrevMeshIds = new int[0];
        private static readonly HashSet<string> s_gvStaticNamed = new HashSet<string>();

        private static void CheckBatchLayout()
        {
            s_gvBad += s_gvStaticCovered;
            int cap = s_batchCap, epoch = Epoch, n = Mathf.Min(s_batches.Count, cap);
            var ids = new int[n];
            for (int i = 0; i < n; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0 || b.Mesh == null) continue;
                ids[i] = b.Mesh.GetInstanceID();
                int sub = Mathf.Clamp(b.SubMesh, 0, Mathf.Max(0, b.Mesh.subMeshCount - 1));
                var topology = b.Mesh.GetTopology(sub);
                if (topology != MeshTopology.Triangles) { s_gvBad++; NameBad(b.Mesh, "TOPOLOGY", $"submesh {sub} is {topology}; batches draw an index range as triangles"); }
                if (b.Mesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal)) { s_gvBad++; NameBad(b.Mesh, "STATICBATCH", $"submesh {sub} is a static-batch combined mesh"); }
            }
            var prev = s_gvPrevMeshIds;
            s_gvPrevMeshIds = ids;
            if (s_argsBuf == null || n <= 0) return;
            int bytes = n * ArgWords * sizeof(uint);
            if (bytes > s_argsBuf.count * s_argsBuf.stride) return;
            AsyncGPUReadback.Request(s_argsBuf, bytes, 0, r =>
            {
                if (r.hasError || epoch != Epoch || cap != s_batchCap) return;
                var args = r.GetData<uint>();
                for (int i = 0; i < n && i < prev.Length && i < s_batches.Count; i++)
                {
                    var b = s_batches[i];
                    if (ids[i] == 0 || prev[i] != ids[i] || b.Count <= 0 || b.Mesh == null || b.Mesh.GetInstanceID() != ids[i]) continue;
                    var sm = b.Mesh.GetSubMesh(Mathf.Clamp(b.SubMesh, 0, Mathf.Max(0, b.Mesh.subMeshCount - 1)));
                    int a = i * ArgWords;
                    if (args[a] == (uint)sm.indexCount && args[a + 2] == (uint)sm.indexStart && (int)args[a + 3] == sm.baseVertex && b.VertCount == b.Mesh.vertexCount) { s_gvArgsChecked++; continue; }
                    if (s_gvLapOpen) s_gvBad++;
                    NameBad(b.Mesh, "ARGS", $"batch {i} draws index count {args[a]}, start {args[a + 2]}, base {(int)args[a + 3]}, written for {b.VertCount} vertices; the live mesh has {sm.indexCount}, {sm.indexStart}, {sm.baseVertex}, {b.Mesh.vertexCount}");
                }
            });
        }

        // Called while a renderer is covered: Unity's static batching swaps the MeshFilter's mesh for a combined one already in world space, so the object's matrix would apply twice
        internal static void NoteCoveredRenderer(MeshRenderer r, Mesh mesh)
        {
            if (!ValidatorOn || r == null || mesh == null) return;
            if (!r.isPartOfStaticBatch && !mesh.name.StartsWith("Combined Mesh", StringComparison.Ordinal)) return;
            string name = r.transform.root.name + "/" + r.gameObject.name;
            if (!s_gvStaticNamed.Add(name)) return;
            s_gvStaticCovered++;
            EasyBakeLog.Error($"[GpuValidator] BAD STATICBATCH: covered renderer '{name}', mesh '{mesh.name}' id {mesh.GetInstanceID()}, isPartOfStaticBatch {r.isPartOfStaticBatch}");
        }

        // null = the GPU's positions and indices are exactly the CPU copy's; otherwise what differs and the first place it does
        private static string CompareToCpu(Mesh mesh, GpuRead read)
        {
            s_gvCpuVerts.Clear();
            mesh.GetVertices(s_gvCpuVerts);
            if (s_gvCpuVerts.Count != read.VertexCount) return $"CPU has {s_gvCpuVerts.Count} vertices, GPU layout says {read.VertexCount}";
            int count = read.Stride > 0 ? Mathf.Min(read.VertexCount, read.Vertex.Length / read.Stride) : 0;
            int differ = 0, firstVertex = -1, zeroed = 0; Vector3 gpuAt = Vector3.zero;
            for (int v = 0; v < count; v++)
            {
                int at = v * read.Stride + read.Offset;
                var gpu = new Vector3(BitConverter.ToSingle(read.Vertex, at), BitConverter.ToSingle(read.Vertex, at + 4), BitConverter.ToSingle(read.Vertex, at + 8));
                var cpu = s_gvCpuVerts[v];
                if (gpu.x == cpu.x && gpu.y == cpu.y && gpu.z == cpu.z) continue;
                differ++;
                if (gpu.x == 0f && gpu.y == 0f && gpu.z == 0f) zeroed++;
                if (firstVertex < 0) { firstVertex = v; gpuAt = gpu; }
            }
            int indexSize = read.Format == IndexFormat.UInt16 ? 2 : 4;
            int indexRoom = read.Index.Length / indexSize;
            int badIndex = 0, firstIndex = -1, firstGpu = 0, firstCpu = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                s_gvCpuIndices.Clear();
                mesh.GetIndices(s_gvCpuIndices, s, false);
                int start = mesh.GetSubMesh(s).indexStart;
                for (int k = 0; k < s_gvCpuIndices.Count && start + k < indexRoom; k++)
                {
                    int at = start + k;
                    int gpu = indexSize == 2 ? BitConverter.ToUInt16(read.Index, at * 2) : (int)BitConverter.ToUInt32(read.Index, at * 4);
                    if (gpu == s_gvCpuIndices[k]) continue;
                    badIndex++;
                    if (firstIndex < 0) { firstIndex = at; firstGpu = gpu; firstCpu = s_gvCpuIndices[k]; }
                }
            }
            if (differ == 0 && badIndex == 0) return null;
            string text = "";
            if (differ > 0) text += $"{differ} of {count} positions differ from the CPU copy ({zeroed} of them zero on the GPU); first #{firstVertex}: GPU {gpuAt:F3}, CPU {s_gvCpuVerts[firstVertex]:F3}. ";
            if (badIndex > 0) text += $"{badIndex} indices differ; first at {firstIndex}: GPU {firstGpu}, CPU {firstCpu}.";
            return text;
        }

        // ---- the Raw flag test ----
        internal static bool RawProbeWanted(Mesh mesh) => ValidatorOn && mesh != null && !s_rawTouched.ContainsKey(mesh.GetInstanceID());

        // the before read lands first, then `next` (which sets the flags) runs, then RawProbeAfter compares
        private static readonly Dictionary<int, GpuRead> s_rawBefore = new Dictionary<int, GpuRead>();

        // the flags are noted here, before anything sets them: a second part sharing this mesh may set them while the before read is in flight
        internal static void RawProbeBefore(Mesh mesh, Action next)
        {
            int id = mesh.GetInstanceID();
            s_rawTouched[id] = "pending";
            bool hadVertexRaw = (mesh.vertexBufferTarget & GraphicsBuffer.Target.Raw) != 0, hadIndexRaw = (mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) != 0;
            ReadGpuData(mesh, read =>
            {
                s_rawBefore[id] = read;
                next();
                if (!s_rawBefore.Remove(id)) return;
                var host = FiresEasyBakeMeshesPlugin.Instance;
                if (host != null && mesh != null) host.StartCoroutine(RawProbeCompare(mesh, id, read, hadVertexRaw, hadIndexRaw));
            });
        }

        private static System.Collections.IEnumerator RawProbeCompare(Mesh mesh, int id, GpuRead before, bool hadVertexRaw, bool hadIndexRaw)
        {
            for (int f = 0; f < 3; f++) yield return null;
            string name = mesh != null ? mesh.name : "?";
            int verts = mesh != null ? mesh.vertexCount : -1;
            GpuRead soon = null, later = null;
            if (mesh != null) ReadGpuData(mesh, r => soon = r);
            float wait = Time.unscaledTime + StuckSeconds;
            while (soon == null && Time.unscaledTime < wait) yield return null;
            yield return new WaitForSecondsRealtime(2f);
            if (mesh != null) ReadGpuData(mesh, r => later = r);
            wait = Time.unscaledTime + StuckSeconds;
            while (later == null && Time.unscaledTime < wait) yield return null;
            string Show(GpuRead r) => r == null ? "no answer" : r.Error != null ? r.Error : $"{r.Hash:X16} ({r.Vertex.Length} + {r.Index.Length} bytes)";
            bool bothRead = before.Error == null && soon?.Error == null && later?.Error == null && soon != null && later != null;
            string verdict = !bothRead ? "UNKNOWN" : before.Hash == soon.Hash && before.Hash == later.Hash ? "SAME" : "CHANGED";
            string text = $"{verdict}; before {Show(before)}; 3 frames after {Show(soon)}; 2 s after {Show(later)}; Raw already set: vertex {hadVertexRaw}, index {hadIndexRaw}";
            s_rawTouched[id] = text;
            string line = $"[RawProbe] RESULT {text}; mesh '{name}' id {id}, {verts} verts";
            if (verdict == "CHANGED") EasyBakeLog.Error(line); else EasyBakeLog.Debug(line);
        }
    }
}
