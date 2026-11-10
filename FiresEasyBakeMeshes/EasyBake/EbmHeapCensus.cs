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

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now < s_next) return;
            s_next = now + 60f;
            if (!EasyBakeLog.DebugOn()) return;
            try { Say(); } catch (System.Exception e) { EasyBakeLog.Debug("[Heap] census skipped: " + e.Message); }   // [generator]: a census never takes Update down
        }

        private static void Say()
        {
            ZoneTracker.HeapTotals(out int zones, out int groups, out long matB, out long idB, out int pieces);
            long mirB = SectorInstanceMirror.HeapBytes(out int mirZdos);
            long indB = Indirect.EbmIndirect.HeapBytes();
            int covered = Indirect.Coverage.CoveredObject.Live.Count;
            long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong(), heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            double mb = 1.0 / (1024 * 1024);
            EasyBakeLog.Debug($"[Heap] EBM managed holders: baked instance matrices {matB * mb:0.0} MB + piece ids {idB * mb:0.0} MB over {groups} group(s) in {zones} zone(s) ({pieces} live piece refs); " +
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
            long counted = matB + idB + mirB + indB + more;
            sb.Append($"added here {more * mb:0.0} MB, all EBM counted {counted * mb:0.0} MB; Mono used {used * mb:0} MB, so {System.Math.Max(0L, used - counted) * mb:0} MB is NOT named by this census (vanilla, other mods, uncollected garbage, heap fragmentation, the far merge worker's local lists)");
            EasyBakeLog.Debug(sb.ToString());
        }
    }
}
