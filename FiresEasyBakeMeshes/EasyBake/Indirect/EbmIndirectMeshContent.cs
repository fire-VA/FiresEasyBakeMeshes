using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.235 ([visual], the near stretch, Fire's live test on 1234: IndirectRenderer off = gone, on = back = persistent source data): the CONTENT
    // of each batch mesh's GPU vertex / index buffers, rewritten in place with the same counts, is the one source no earlier check sees. Prefab
    // meshes are mostly not CPU-readable, so this reads the GPU buffers: one batch mesh every 0.25 s, a 4 KB window of vertex stream 0 and of
    // the index buffer through AsyncGPUReadback. The window walks the buffer over the laps (offset stored with each hash, so like is compared with
    // like); each buffer is hashed on its own and the two are combined in a fixed order ([generator]: readbacks may land in either order).
    // A changed hash = one Error naming the mesh (later ones Debug). A mesh that cannot be read back is skipped for the session.
    // [Diagnostics] IndirectMeshContentCheck (default on, ~32 KB read a second).
    internal static partial class EbmIndirect
    {
        private static bool MeshContentOn => FiresEasyBakeMeshesPlugin.IndirectMeshContentCheck == null || FiresEasyBakeMeshesPlugin.IndirectMeshContentCheck.Value;
        private const int SampleBytes = 4096;
        private const int Windows = 8;   // each buffer is covered by 8 windows spread over it; one hash per (mesh, window)
        private static readonly Dictionary<long, ulong> s_mcHash = new Dictionary<long, ulong>();   // (mesh instance id, window) -> hash
        private static readonly Dictionary<int, int> s_mcLap = new Dictionary<int, int>();          // mesh instance id -> next window index
        private static readonly HashSet<int> s_mcUnreadable = new HashSet<int>();
        private static int s_mcCursor, s_mcChecks, s_mcChanged, s_mcFailed; private static float s_mcNext, s_mcPendingSince; private static bool s_mcPending, s_mcSaid, s_mcSaidOn;
        private static GraphicsBuffer s_mcV, s_mcI;

        private static void MeshContentTick(int nb)
        {
            if (!MeshContentOn || nb == 0) return;
            float now = Time.unscaledTime;
            if (s_mcPending)
            {
                if (now - s_mcPendingSince < 5f) return;
                DisposePending(); s_mcFailed++;   // [generator]: a readback that never called back (device reset) must not leak the wrappers
            }
            if (now < s_mcNext) return;
            s_mcNext = now + 0.25f;
            if (!s_mcSaidOn) { s_mcSaidOn = true; EasyBakeLog.Info("[Integrity] mesh content check on: each batch mesh's GPU vertex + index buffers are sampled in 4 KB windows that walk the buffer over time, and hashed; a change under EBM's draws logs an Error with the mesh name"); }
            for (int tries = 0; tries < nb; tries++)
            {
                if (s_mcCursor >= nb) s_mcCursor = 0;
                var b = s_batches[s_mcCursor++];
                if (b.Mesh == null || b.Count <= 0 || s_mcUnreadable.Contains(b.Mesh.GetInstanceID())) continue;
                Sample(b.Mesh);
                return;
            }
        }

        private static void DisposePending() { s_mcV?.Dispose(); s_mcI?.Dispose(); s_mcV = s_mcI = null; s_mcPending = false; }

        private static void Sample(Mesh mesh)
        {
            int id = mesh.GetInstanceID();
            try
            {
                s_mcV = mesh.GetVertexBuffer(0);
                s_mcI = mesh.GetIndexBuffer();
                if (s_mcV == null || s_mcI == null) { DisposePending(); s_mcUnreadable.Add(id); s_mcFailed++; return; }
                int vTotal = s_mcV.count * s_mcV.stride, iTotal = s_mcI.count * s_mcI.stride;
                int vLen = Mathf.Min(SampleBytes, vTotal) & ~3, iLen = Mathf.Min(SampleBytes, iTotal) & ~3;
                if (vLen <= 0 || iLen <= 0) { DisposePending(); s_mcUnreadable.Add(id); s_mcFailed++; return; }
                // 8 windows spread over each buffer, one per lap; a window is compared only with an earlier sample of the same window
                s_mcLap.TryGetValue(id, out int lap);
                int win = lap % Windows;
                int vOff = (int)((long)(vTotal - vLen) * win / (Windows - 1)) & ~3, iOff = (int)((long)(iTotal - iLen) * win / (Windows - 1)) & ~3;
                long key = ((long)id << 8) | (uint)win;
                if (vOff + vLen > vTotal) vOff = (vTotal - vLen) & ~3;
                if (iOff + iLen > iTotal) iOff = (iTotal - iLen) & ~3;
                s_mcLap[id] = lap + 1;
                s_mcPending = true; s_mcPendingSince = Time.unscaledTime;
                var m = mesh; ulong hV = 0, hI = 0; int left = 2; bool failed = false;
                void Done()
                {
                    if (--left > 0) return;
                    DisposePending(); s_mcChecks++;
                    if (failed) { s_mcUnreadable.Add(id); s_mcFailed++; return; }
                    ulong h = (hV * 1099511628211UL) ^ hI;   // fixed order, whatever order the readbacks landed in
                    if (s_mcHash.TryGetValue(key, out ulong first) && first != h)
                    {
                        s_mcChanged++;
                        string name = m != null ? m.name : "?";
                        if (!s_mcSaid) { s_mcSaid = true; EasyBakeLog.Error($"[Integrity] MESH CONTENT CHANGED under EBM's draws at frame {Time.frameCount}: '{name}' ({(m != null ? m.vertexCount : 0)} vertices): its GPU vertex / index data in window {win} (bytes {vOff} / {iOff}) differs from an earlier sample of the same window. Something rewrote this prefab mesh in place."); }
                        else EasyBakeLog.Debug($"[Integrity] mesh content changed: '{name}' (#{s_mcChanged})");
                    }
                    s_mcHash[key] = h;
                }
                ulong Fnv(NativeArray<uint> d) { ulong x = 1469598103934665603UL; for (int k = 0; k < d.Length; k++) x = (x ^ d[k]) * 1099511628211UL; return x; }
                AsyncGPUReadback.Request(s_mcV, vLen, vOff, r => { if (r.hasError) failed = true; else hV = Fnv(r.GetData<uint>()); Done(); });
                AsyncGPUReadback.Request(s_mcI, iLen, iOff, r => { if (r.hasError) failed = true; else hI = Fnv(r.GetData<uint>()); Done(); });
            }
            catch (System.Exception e)
            {
                DisposePending(); s_mcUnreadable.Add(id); s_mcFailed++;
                EasyBakeLog.Debug("[Integrity] mesh content sample skipped (" + mesh.name + "): " + e.Message);
            }
        }

        internal static string MeshContentLine() => $"mesh content checks {s_mcChecks} (4 KB windows walking each buffer; {s_mcChanged} changed, {s_mcUnreadable.Count} mesh(es) unreadable), {s_mcHash.Count} mesh(es) sampled";
    }
}
