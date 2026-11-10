using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // E25 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E25; Tools\READ_EBM_cache_20261109.md, READ_EBM_alloc_20261109.md;
    // DROP_NOTES_seasons_bakemem.md). Key [Cache] ReleaseBakedCpuCopies, OFF until the A/B. EBM's own code; relies on no other mod.
    //
    // Every baked zone mesh (MeshBaker CombineMeshes, MeshCacheStore cache rebuild) stays READABLE for the session: a full CPU copy of its
    // vertices, normals, tangents, uvs and indices sits in native memory beside the GPU copy that is drawn. Its only later reader is the next
    // cache save of that zone (MeshCacheStore.ExtractBatchDataFromLive). Nothing else reads it: EbmMeshCheck reads at build only, the far tier
    // reads the pieces' prefab meshes (never the baked batches), the GPU validator compares only readable meshes, and a changed combined batch
    // is a rebake (new meshes).
    // So: once a zone is baked, NOT CacheStale (its file holds exactly these meshes), has a file on disk, and was last saved more than
    // 'ReleaseIdleSeconds' ago (a queued background write has landed), each of its batch meshes gets UploadMeshData(markNoLongerReadable: true):
    // the CPU copy is freed and the GPU copy is untouched, so what is drawn does not change in any frame (no pop, from any camera cell).
    // Re-create on demand: the next save of that zone takes the arrays from the preload copy when it was built from the same mesh, else
    // re-reads them from the zone's own file (matched by position and material key). If they cannot be found the save is not written and the file
    // is removed, so the zone bakes fresh on its next visit; nothing on screen changes.
    // Census (Debug, 30 s): batch meshes still readable (MB), released (count, MB) this window and in total, re-reads from disk (count, ms),
    // failed re-reads.
    internal static class EbmBakeMemRelease
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> IdleSeconds;
        private static bool On => Enabled != null && Enabled.Value;

        private static readonly HashSet<int> s_released = new HashSet<int>();   // instance ids of meshes we released
        private static readonly Dictionary<Vector2s, float> s_eligibleSince = new Dictionary<Vector2s, float>();
        private static readonly HashSet<Vector2s> s_gone = new HashSet<Vector2s>();   // review 11-09: a set (was a list: O(n^2) per sweep)
        private static float s_nextSweep, s_nextSay;
        private static int s_lastFrame = -1;
        private static bool s_hooked;
        private static int w_released, w_rereads, w_failed; private static long w_bytes, t_bytes; private static double w_rereadMs;
        private static int t_released;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Cache", "ReleaseBakedCpuCopies", false,
                "Baked building meshes keep a second copy in RAM after they are sent to the graphics card. With this on, that copy is freed once the zone's cache file holds the same mesh and nothing changed for a while; if the zone is saved again later, the mesh is read back from that file. Nothing on screen changes. Saves RAM.");
            IdleSeconds = config.Bind("Cache", "ReleaseIdleSeconds", 30f, new ConfigDescription(
                "With ReleaseBakedCpuCopies on: seconds a zone must stay saved and unchanged before its meshes' RAM copies are freed.", new AcceptableValueRange<float>(5f, 600f)));
            Hook();
        }

        private static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += OnPreCull;   // main thread, once a frame (the main camera)
        }

        private static void OnPreCull(Camera cam)
        {
            if (Time.frameCount == s_lastFrame || cam == null || cam != Utils.GetMainCamera()) return;
            s_lastFrame = Time.frameCount;
            GpuReleaseFence.Pump();
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextSay) Say(now);
            if (!On || now < s_nextSweep) return;
            s_nextSweep = now + 5f;
            try { Sweep(now); }
            catch (System.Exception e) { EasyBakeLog.Debug("[BakeMem] release sweep threw (key turned off): " + e.Message); Enabled.Value = false; }
        }

        private static void Sweep(float now)
        {
            float idle = IdleSeconds != null ? IdleSeconds.Value : 30f;
            s_gone.Clear(); s_gone.UnionWith(s_eligibleSince.Keys);
            ZoneTracker.ForEachBakedZone((coord, bake, stale) =>
            {
                s_gone.Remove(coord);
                if (stale || !MeshCacheStore.HasDiskFile(coord) || MeshCacheStore.SavedAgo(coord) < idle) { s_eligibleSince.Remove(coord); return; }
                if (!s_eligibleSince.TryGetValue(coord, out float since)) { s_eligibleSince[coord] = now; return; }
                if (now - since < idle) return;
                ReleaseList(bake.Batches);
                ReleaseList(bake.FarBatches);
            });
            foreach (var c in s_gone) s_eligibleSince.Remove(c);   // zones no longer tracked
        }

        private static void ReleaseList(List<MeshBaker.BatchInstance> list)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var mesh = list[i].Mesh;
                if (mesh == null || !mesh.isReadable) continue;
                long bytes = CpuBytes(mesh);
                mesh.UploadMeshData(true);   // GPU copy stays; the CPU copy goes
                s_released.Add(mesh.GetInstanceID());
                w_released++; t_released++; w_bytes += bytes; t_bytes += bytes;
            }
        }

        // vertices 12 + normals 12 + tangents 16 + uv 8 bytes a vertex (what the baker writes), indices 2 or 4
        private static long CpuBytes(Mesh m)
        {
            long v = m.vertexCount, idx = 0;
            for (int s = 0; s < m.subMeshCount; s++) idx += (long)m.GetIndexCount(s);
            return v * 48L + idx * (m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32 ? 4L : 2L);
        }

        /// <summary>A save is about to run: does the bake hold a mesh whose CPU copy we released?</summary>
        internal static bool AnyReleased(MeshBaker.BakeResult bake)
        {
            if (s_released.Count == 0 || bake == null) return false;
            return Has(bake.Batches) || Has(bake.FarBatches);
        }
        private static bool Has(List<MeshBaker.BatchInstance> list)
        {
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++) { var m = list[i].Mesh; if (m != null && !m.isReadable && s_released.Contains(m.GetInstanceID())) return true; }
            return false;
        }

        internal static void NoteReread(double ms) { w_rereads++; w_rereadMs += ms; }
        internal static void NoteRereadFailed() { w_failed++; }

        private static void Say(float now)
        {
            s_nextSay = now + 30f;
            if (!EasyBakeLog.DebugOn()) { w_released = w_rereads = w_failed = 0; w_bytes = 0; w_rereadMs = 0; return; }
            long readable = 0; int readableN = 0;
            ZoneTracker.ForEachBakedZone((coord, bake, stale) => { Count(bake.Batches, ref readable, ref readableN); Count(bake.FarBatches, ref readable, ref readableN); });
            EasyBakeLog.Debug($"[BakeMem] 30 s: key {(On ? "on" : "off")}; baked meshes still holding a RAM copy {readableN} ({readable / 1048576f:0.0} MB); released this window {w_released} ({w_bytes / 1048576f:0.0} MB), " +
                $"session {t_released} ({t_bytes / 1048576f:0.0} MB); read back from zone files for a save {w_rereads} ({w_rereadMs:0.0} ms), failed {w_failed} (zone file removed, bakes fresh)" +
                $"; {GpuReleaseFence.Census()}");
            w_released = w_rereads = w_failed = 0; w_bytes = 0; w_rereadMs = 0;
        }
        private static void Count(List<MeshBaker.BatchInstance> list, ref long bytes, ref int n)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++) { var m = list[i].Mesh; if (m != null && m.isReadable) { bytes += CpuBytes(m); n++; } }
        }
    }

    // E23 ([seasons] 11-09, row E23). Key [Optimize] GpuReleaseFence, OFF. A GraphicsBuffer replaced in a grow or relayout is released 3 frames
    // later instead of the same frame, so no command already issued this frame (a copy dispatch that reads it, a draw bound to it) can meet a
    // freed buffer, whatever the graphics API defers on its own. Off = released at once, as today. Bounded: at most 64 buffers wait; past that the
    // oldest are released.
    internal static class GpuReleaseFence
    {
        internal static ConfigEntry<bool> Enabled;
        private const int Frames = 3, MaxWaiting = 64;
        private struct Waiting { public GraphicsBuffer B; public int Frame; }
        private static readonly Queue<Waiting> s_queue = new Queue<Waiting>();
        private static int w_deferred, w_released, w_overflow;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Optimize", "GpuReleaseFence", false,
                "Graphics buffers EBM replaces (when a batch grows) are freed 3 frames later instead of at once, so nothing already sent to the graphics card this frame can use a freed buffer. No visible change. Off = freed at once.");
        }

        internal static void Release(GraphicsBuffer b)
        {
            if (b == null) return;
            if (Enabled == null || !Enabled.Value) { b.Release(); return; }
            s_queue.Enqueue(new Waiting { B = b, Frame = Time.frameCount });
            w_deferred++;
            while (s_queue.Count > MaxWaiting) { s_queue.Dequeue().B?.Release(); w_overflow++; }
        }

        /// <summary>Once a frame (EbmBakeMemRelease's onPreCull).</summary>
        internal static void Pump()
        {
            int f = Time.frameCount;
            while (s_queue.Count > 0 && f - s_queue.Peek().Frame >= Frames) { s_queue.Dequeue().B?.Release(); w_released++; }
        }

        /// <summary>Shutdown / logout: every waiting buffer released now (review 11-09; called from EbmIndirect.ReleaseAll).</summary>
        internal static void FlushAll()
        {
            while (s_queue.Count > 0) { s_queue.Dequeue().B?.Release(); w_released++; }
        }

        internal static string Census()
        {
            string s = $"GPU release fence {(Enabled != null && Enabled.Value ? "on" : "off")}: deferred {w_deferred}, released {w_released}, waiting {s_queue.Count}, over the cap {w_overflow}";
            w_deferred = w_released = w_overflow = 0;
            return s;
        }
    }
}
