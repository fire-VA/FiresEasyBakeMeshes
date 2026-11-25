using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.229 (lead / [fgn], 11-08: Mono's Boehm GC is non-generational, every collection a full sweep of the ~2.9 GB heap = 150-440 ms; the
    // levers are allocation rate and heap size): every 60 s at Debug, what EBM's own big managed holders keep, by element count x element size
    // (capacity, not count: what is really allocated). One walk of the zone table (thousands of groups, well under 1 ms) and a few counts.
    // Next to it: the whole Mono heap (used / reserved) so EBM's share is visible.
    internal static class EbmHeapCensus
    {
        private static float s_next;
        // 1.2.389 ([lead]: a normal leg must be able to prove the MB): the two [Heap] lines go out at Info the first time, whenever EBM's counted total moved by
        // InfoStepBytes since the last Info pair, and from ebm_heap; Debug otherwise (a repeating line stays Debug). At most one pair a minute.
        private static long s_infoCounted = -1L;
        private const long InfoStepBytes = 8L << 20;

        // ── 1.2.268 (lead 11-10, Tools\PLAN_ebm_memory_snapshot_20261110.md Path B): ebm_heap, on demand only. The managed line above at once, then EBM's
        // NATIVE objects by kind, from Profiler.GetRuntimeMemorySizeLong (what Unity says each holds, CPU + GPU copies): baked batch meshes (EasyBake_* /
        // EasyBakeCached_*: readable ones count their CPU copy too), far chunks, simplified rock meshes, EBM materials and textures. It scans every loaded
        // Mesh / Material / Texture once (a few hundred ms in a town), so it never runs on a timer. Used when FDT's fires_memsnap cannot take a snapshot.
        [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Register
        {
            private static bool s_registered;
            [HarmonyLib.HarmonyPostfix]
            private static void AfterInitTerminal()
            {
                if (s_registered) return;
                s_registered = true;
                new Terminal.ConsoleCommand("ebm_heap", "[EBM] Memory census now: EBM's managed holders and its native meshes / materials / textures by kind (on demand; scans every loaded object once).",
                    args => { try { Say(true); Native(); } catch (System.Exception e) { EasyBakeLog.Warn("[Heap] ebm_heap threw: " + e.GetType().Name + ": " + e.Message); } });
            }
        }

        private static string KindOf(string n)
        {
            if (string.IsNullOrEmpty(n)) return null;
            if (n.StartsWith("EasyBakeCached_", System.StringComparison.Ordinal)) return "baked batch (from cache)";
            if (n.StartsWith("EasyBake_", System.StringComparison.Ordinal)) return "baked batch";
            if (n.StartsWith("FarBuildings", System.StringComparison.Ordinal)) return "far chunk";
            if (n.EndsWith(" (simplified)", System.StringComparison.Ordinal)) return "simplified (rock LOD)";
            if (n.StartsWith("FiresEBM", System.StringComparison.Ordinal) || n.StartsWith("EBM ", System.StringComparison.Ordinal) || n.StartsWith("EBM_", System.StringComparison.Ordinal)) return "EBM other";
            return null;
        }

        private static bool s_estimated;
        // vertex buffers (each stream's stride x vertex count) + indices (2 or 4 bytes each), counted twice for a readable mesh (CPU copy beside the GPU one);
        // a texture as width x height x 4 bytes, x 4/3 with mips. Materials: 0 (small, property data only).
        private static long Estimate(Object o)
        {
            try
            {
                if (o is Mesh m)
                {
                    long vb = 0; for (int s = 0; s < m.vertexBufferCount; s++) vb += (long)m.GetVertexBufferStride(s) * m.vertexCount;
                    long ib = 0; for (int s = 0; s < m.subMeshCount; s++) ib += (long)m.GetIndexCount(s) * (m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 2 : 4);
                    return (vb + ib) * (m.isReadable ? 2 : 1);
                }
                if (o is Texture t) return (long)(t.width * (long)t.height * 4L * (t.mipmapCount > 1 ? 4.0 / 3.0 : 1.0));
            }
            catch { }
            return 0;
        }

        private static void Native()
        {
            double mb = 1.0 / (1024 * 1024);
            s_estimated = false;
            var sb = new System.Text.StringBuilder("[Heap] EBM native objects (Profiler.GetRuntimeMemorySizeLong): ");
            void Sum<T>(string label) where T : Object
            {
                var by = new System.Collections.Generic.Dictionary<string, (int n, long b, int readable)>();
                foreach (var o in Resources.FindObjectsOfTypeAll<T>())
                {
                    string k = KindOf(o != null ? o.name : null);
                    if (k == null) continue;
                    by.TryGetValue(k, out var v);
                    bool rd = o is Mesh m && m.isReadable;
                    long bytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o);
                    if (bytes <= 0) { bytes = Estimate(o); s_estimated = true; }   // [ghost] review: release players report 0; estimate from the object itself
                    by[k] = (v.n + 1, v.b + bytes, v.readable + (rd ? 1 : 0));
                }
                foreach (var kv in by) sb.Append($"{label} {kv.Key}: {kv.Value.n} ({kv.Value.b * mb:0.0} MB{(label == "mesh" ? $", {kv.Value.readable} still readable" : "")}); ");
            }
            Sum<Mesh>("mesh"); Sum<Material>("material"); Sum<Texture>("texture");
            if (s_estimated) sb.Append("(this player reports 0 through the profiler, so sizes are ESTIMATED from vertex counts and texture sizes) ");
            sb.Append($"GPU buffers counted in the indirect line above. Mono used {UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() * mb:0} MB.");
            EasyBakeLog.Info(sb.ToString());
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 60f;
            ZdoCombine.ReleaseIdle(Time.realtimeSinceStartup);   // 1.2.389 [Cache] ZdoCombineIdleRelease: on this 60 s tick (returns at once when off or nothing is held)
            EbmMeshCheck.TrimIdle(now);   // 1.2.392
            try { Say(false); } catch (System.Exception e) { EasyBakeLog.Debug("[Heap] census skipped: " + e.Message); }   // [generator]: a census never takes Update down
        }

        internal static void Say(bool force)
        {
            ZoneTracker.HeapTotals(out int zones, out int groups, out long matB, out long idB, out int pieces);
            long mirB = SectorInstanceMirror.HeapBytes(out int mirZdos);
            long indB = Indirect.EbmIndirect.HeapBytes();
            int covered = Indirect.Coverage.CoveredObject.Live.Count;
            long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            double mb = 1.0 / (1024 * 1024);
            System.Func<string> head = () => ($"[Heap] EBM managed holders: baked instance matrices {matB * mb:0.0} MB + piece ids {idB * mb:0.0} MB over {groups} group(s) in {zones} zone(s) ({pieces} live piece refs); " +
                              $"sector mirror ~{mirB * mb:0.0} MB ({mirZdos} ZDOs); indirect CPU arrays {indB * mb:0.0} MB; covered objects {covered}; meshes checked {EbmMeshCheck.Checked} ({EbmMeshCheck.Bad} bad); " +
                              $"total counted {(matB + idB + mirB + indB) * mb:0.0} MB of the Mono heap {used * mb:0} MB used / {heap * mb:0} MB reserved");
            // HeapByHolder: the holders the line above omits. An estimate from counts x layout sizes; only what EBM holds a reference to can be named here.
            var sb = new System.Text.StringBuilder("[Heap] by holder (EBM only, estimate): ");
            long posLod = Indirect.EbmIndirect.HeapBytesPosLod(), looks = Indirect.IndirectLooks.HeapBytes();
            sb.Append($"indirect s_instPos+s_instLod {posLod * mb:0.0} MB; s_looks {looks * mb:0.0} MB; ");
            long more = posLod + looks + Indirect.Coverage.CoveredObject.HeapHolders(sb) + ZoneTracker.HeapHolders(sb) + Far.FarBuildingDraw.HeapHolders(sb);
            long preArrays = MeshCacheStore.PreloadedBytes(out int preZones), preSets = MeshCacheStore.PreloadedSetBytes(out int preIds, out int preTf);
            sb.Append($"preload arrays {preArrays * mb:0.0} MB ({preZones} zones); preload id sets + transforms {preSets * mb:0.0} MB ({preIds} ids, {preTf} transforms); ");
            more += preArrays + preSets;
            long zdoB = ZdoCombine.HeapBytes(out int zdoParts);   // 1.2.389
            sb.Append(ZdoCombine.HeapSummary(zdoB, zdoParts));
            more += zdoB;
            int classN = MeshBaker.ClassCounts(out int classForgot, out int classSwept, out int classSweepSlots);   // 1.2.392
            long classB = classN * 220L + classSweepSlots * 8L;   // PieceClass ~190 B + the table slot ~28 B, + the sweep's kept key list; NOT the destroyed piece a dead key still holds
            sb.Append($"piece class table {classB * mb:0.0} MB ({classN} entries; forgotten at zone unload or reset {classForgot}, destroyed keys swept {classSwept} in {MeshBaker.ClassSweeps} sweep(s), worst sweep slice {MeshBaker.ClassSweepMaxMs:0.000} ms a frame, last sweep took {MeshBaker.ClassSweepLastFrames} frame(s)); ");
            more += classB;
            long checkB = EbmMeshCheck.HeapBytes(out int checkTrims);   // 1.2.392
            sb.Append($"mesh check scratch {checkB * mb:0.0} MB (trimmed {checkTrims} time(s)); ");
            more += checkB;
            long counted = matB + idB + mirB + indB + more;
            sb.Append($"added here {more * mb:0.0} MB, all EBM counted {counted * mb:0.0} MB; Mono used {used * mb:0} MB, so {System.Math.Max(0L, used - counted) * mb:0} MB is NOT named by this census (vanilla, other mods, uncollected garbage, heap fragmentation, the far merge worker's local lists)");
            sb.Append("; " + MeshCacheStore.SlimSummary());
            bool info = force || s_infoCounted < 0L || System.Math.Abs(counted - s_infoCounted) >= InfoStepBytes;
            if (info) { s_infoCounted = counted; EasyBakeLog.Info(head()); EasyBakeLog.Info(sb.ToString()); }
            else if (EasyBakeLog.DebugOn()) { EasyBakeLog.Debug(head()); EasyBakeLog.Debug(sb.ToString()); }   // [visual] review: the head line is only built when it is written
        }
    }
}
