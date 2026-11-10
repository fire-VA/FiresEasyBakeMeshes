using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.213 ([saddles], lead / [visual] split of the [Draw] cost study, Tools\STUDY_sections\saddles_EbmDrawCost_20261108.md #2 / #3 / #4).
    // Fire's O log: the indirect frame's main-thread time grew with the batch count (7-9 us per camera batch, flat), not with instances.
    // Three of the per-frame costs that scale with batches / cells, each behind its own [Optimize] key (read live, off = as 1.2.212):
    //   #2 IndirectFusedCells: one walk over the CPU cells for every view kind (IndirectCells.CullFused), and Compact() finally has a caller.
    //   #3 IndirectCachedAlive: Unity's native "== null" on a batch's Mesh / Material once a frame per batch, not in every loop.
    //   #4 IndirectLocalSteady: point / spot light caster buffers are not torn down on a one-frame drop of the light, and a buffer re-records
    //      only for its own reasons (a gain, a quarter stale, one of its batches moved), not whenever any batch got its first instance.
    internal static partial class EbmIndirect
    {
        // ---- #2 ----
        private static bool s_cellsFused;   // this frame's Cull used the fused walk (CullLocal / PrepareOnePass then skip their own)
        private static bool FusedCellsOn => FiresEasyBakeMeshesPlugin.IndirectFusedCells == null || FiresEasyBakeMeshesPlugin.IndirectFusedCells.Value;
        private static bool s_compactDue;
        private static float s_compactNext;
        private const float CompactEverySeconds = 5f;
        internal static int s_compacts;   // Debug

        // from EndFrame after the dropped groups were removed: empty cells (unloaded zones) leave the walk, at most every few seconds
        private static void MaybeCompactCells(bool dropped)
        {
            if (dropped) s_compactDue = true;
            if (!s_compactDue || !FusedCellsOn) return;
            float now = Time.unscaledTime;
            if (now < s_compactNext) return;
            s_compactNext = now + CompactEverySeconds;
            s_compactDue = false;
            int before = s_cells.CellCount;
            s_cells.Compact();
            s_compacts++;
            if (EasyBakeLog.DebugOn()) EasyBakeLog.Debug($"[Indirect] cells compacted: {before} -> {s_cells.CellCount} cell(s), batch width {s_cells.BatchCount}");
        }

        // ---- #3 ----
        private static bool s_aliveValid;   // RefreshAlive ran this frame (BeginFrame clears it)
        private static bool CachedAliveOn => FiresEasyBakeMeshesPlugin.IndirectCachedAlive == null || FiresEasyBakeMeshesPlugin.IndirectCachedAlive.Value;

        // from EndFrame, before anything reads a batch: one native check per Mesh / Material for the whole frame (other cameras' pre-cull included)
        private static void RefreshAlive()
        {
            s_aliveValid = false;
            if (!CachedAliveOn) return;
            for (int i = 0; i < s_batches.Count; i++)
            {
                var b = s_batches[i];
                b.MeshOk = b.Mesh != null;
                b.MatOk = b.Material != null;
            }
            s_aliveValid = true;
        }

        private static bool HasMesh(Batch b) => !BisectHides(b) && (s_aliveValid ? b.MeshOk : b.Mesh != null);   // 1.2.238: every draw path asks this
        private static bool HasMaterial(Batch b) => s_aliveValid ? b.MatOk : b.Material != null;

        // ---- [Optimize] IndirectSettledWalk ([wishbone] 11-09, Tools\AUDIT_shave_draws_groups_20261109.md) ----
        // Every frame ZoneTracker.DrawInstances walked every group of every zone only to find the few that changed. With the key on, a frame with nothing structural changed walks only
        // the work list (groups marked dirty, or still in a sliced snow pass); any zone, bake, group-list or global looks change asks for one full walk, the old code unchanged.
        internal static bool SettledOn => FiresEasyBakeMeshesPlugin.IndirectSettledWalk != null && FiresEasyBakeMeshesPlugin.IndirectSettledWalk.Value && LooksChangeDrivenOn;
        private static bool s_walkAll = true, s_settledFrame, s_takeSettled;
        private static readonly List<ZoneInstanceGroup> s_work = new List<ZoneInstanceGroup>();
        private static int s_lastTaken, s_lastRefused;
        private static bool s_gSnow, s_gField, s_gWet, s_gRoof; private static int s_gInside;

        internal static void RequestFullWalk() { s_walkAll = true; }

        // a group of a hidden zone is not registered, and must not be: only the full walk knows which zones draw
        internal static void MarkGroupDirty(ZoneInstanceGroup group)
        {
            if (!SettledOn || group == null || group.IndirectWork || !s_groups.ContainsKey(group)) return;
            group.IndirectWork = true; s_work.Add(group);
        }

        private static void EnqueueWork(ZoneInstanceGroup group)
        {
            if (!SettledOn || group.IndirectWork) return;
            group.IndirectWork = true; s_work.Add(group);
        }

        private static void ClearWork()
        {
            for (int i = 0; i < s_work.Count; i++) if (s_work[i] != null) s_work[i].IndirectWork = false;
            s_work.Clear();
        }

        private static bool GlobalsChanged()
        {
            bool snow = SnowFeed.Active, field = SnowFeed.FieldActive, wet = WetOverride.BakedDries, roof = InstancedDraw.RoofHidingActive;
            int inside = WetOverride.InsideGeneration;
            bool changed = snow != s_gSnow || field != s_gField || wet != s_gWet || roof != s_gRoof || inside != s_gInside;
            s_gSnow = snow; s_gField = field; s_gWet = wet; s_gRoof = roof; s_gInside = inside;
            return changed;
        }

        // From DrawInstances after BeginFrame: true = walk only the work list this frame (EndFrame then leaves its Seen loops alone)
        internal static bool BeginSettledFrame()
        {
            s_settledFrame = false;
            if (!SettledOn) { s_walkAll = true; return false; }
            bool globals = GlobalsChanged();   // first, so the stored values stay current on a full walk
            if (s_walkAll || globals) { s_walkAll = false; ClearWork(); return false; }
            s_settledFrame = true;
            return true;
        }

        internal static void NoteFullWalkEnd() { s_lastTaken = TakenGroups; s_lastRefused = RefusedGroups; }

        internal static void WalkWork()
        {
            int n = s_work.Count, w = 0;
            for (int i = 0; i < n; i++)
            {
                var g = s_work[i];
                if (!s_groups.ContainsKey(g)) { g.IndirectWork = false; continue; }
                s_takeSettled = false;
                bool took = Take(g);
                if (!took) s_walkAll = true;   // it left the indirect path: the legacy list has to learn it
                if (took && s_takeSettled) { g.IndirectWork = false; continue; }
                s_work[w++] = g;
            }
            for (int i = n; i < s_work.Count; i++) s_work[w++] = s_work[i];   // marked while the list was walked
            s_work.RemoveRange(w, s_work.Count - w);
            TakenGroups = s_lastTaken; RefusedGroups = s_lastRefused;
        }

        // ---- #4 ----
        private static bool LocalSteadyOn => FiresEasyBakeMeshesPlugin.IndirectLocalSteady == null || FiresEasyBakeMeshesPlugin.IndirectLocalSteady.Value;
        private const int LocalGraceFrames = 30;   // ~0.5 s: a light that blinks out of the list (LightLod shadow toggle, frustum edge) keeps its buffers
        private static int s_localOffFrames;
        private static readonly List<int> s_localMoved = new List<int>();   // batches that moved (PlaceQueued) since the last RecordLocal
        internal static int s_localGraceHolds, s_localMovedRedos;   // Debug

        // AddInstance's "a batch got its first instance": with the key on, each slot's own gain check (RecordLocal) already re-records the
        // slots that want it, so the all-slot force is not raised for the lights (the cascades keep theirs)
        private static void NoteFirstInstance()
        {
            s_cascadesDirty = true;
            if (!LocalSteadyOn) s_localDirty = true;
        }

        // PlaceQueued moved a shadow-casting batch (new blocks): with the key on, only the slots that recorded it re-record
        private static void NoteCasterMoved(Batch b)
        {
            s_cascadesDirty = true;
            if (LocalSteadyOn) s_localMoved.Add(b.Index); else s_localDirty = true;
        }

        private static bool SlotHoldsMoved(CasterSlot s)
        {
            for (int m = 0; m < s_localMoved.Count; m++) { int i = s_localMoved[m]; if (i < s.Rec.Length && s.Rec[i]) return true; }
            return false;
        }
    }
}
