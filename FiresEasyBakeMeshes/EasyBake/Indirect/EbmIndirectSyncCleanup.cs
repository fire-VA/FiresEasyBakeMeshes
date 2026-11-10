using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [Optimize] IndirectSyncCleanup ([worldgen] 11-09, Tools\READ_EBM_take_sync_20261109.md). Same result as SyncLane, less work. An instance whose matrix is unchanged at its own index keeps
    // its slot after one compare, and only the matrices that moved go through the hash table: a piece joining or leaving no longer hashes every matrix of the group twice per lane.
    // An empty lane (a group's first registration) never touches the table. The lists are sized once instead of doubling, which is garbage during a fill, and every GC here is a full pause.
    internal static partial class EbmIndirect
    {
        private static bool SyncCleanupOn => FiresEasyBakeMeshesPlugin.IndirectSyncCleanup != null && FiresEasyBakeMeshesPlugin.IndirectSyncCleanup.Value;
        private static bool[] s_syncSame = new bool[256];

        // exact, field by field (Matrix4x4 == is a fuzzy column compare; the hash table this stands beside compares exactly)
        private static bool ExactSame(in Matrix4x4 a, in Matrix4x4 b)
            => a.m03 == b.m03 && a.m13 == b.m13 && a.m23 == b.m23 && a.m00 == b.m00 && a.m10 == b.m10 && a.m20 == b.m20 && a.m30 == b.m30
            && a.m01 == b.m01 && a.m11 == b.m11 && a.m21 == b.m21 && a.m31 == b.m31 && a.m02 == b.m02 && a.m12 == b.m12 && a.m22 == b.m22 && a.m32 == b.m32 && a.m33 == b.m33;

        // the group's slot lists at their final size, before its lanes fill them
        private static void PresizeSlotLists(ZoneInstanceGroup group, GroupSlots reg)
        {
            int lanes = 0;
            var lodParts = group.Definition.LodParts;
            if (lodParts != null) lanes = lodParts.Length;
            else
                foreach (var part in group.Definition.Parts)
                    lanes += part.LodMeshes != null && part.LodMeshes.Length > 0 && part.LodHeights != null && part.LodSize > 0f ? part.LodMeshes.Length : 1;
            int need = group.Matrices.Count * lanes;
            if (reg.Slots.Capacity < need) reg.Slots.Capacity = need;
            if (reg.SlotInstance.Capacity < need) reg.SlotInstance.Capacity = need;
        }

        // SyncLane's matching, after its lane setup. Slots keep the instance order of src (looks need it).
        private static void SyncLaneClean(GroupSlots reg, Lane ln, Batch batch, List<Matrix4x4> src, LodPart lp, Vector4 bakedLod, float lodStart, float lodEnd)
        {
            int oldN = ln.Mats.Count, n = src.Count, common = Mathf.Min(oldN, n);
            var oldSlots = s_syncOldSlots; oldSlots.Clear(); oldSlots.AddRange(ln.Slots);
            s_syncHead.Clear(); s_syncNext.Clear();
            if (oldN > 0)
            {
                if (s_syncSame.Length < common) s_syncSame = new bool[Mathf.NextPowerOfTwo(common)];
                for (int i = 0; i < common; i++) s_syncSame[i] = ExactSame(ln.Mats[i], src[i]);
                for (int j = 0; j < oldN; j++)
                {
                    if (j < common && s_syncSame[j]) { s_syncNext.Add(-1); continue; }
                    var old = ln.Mats[j];
                    s_syncNext.Add(s_syncHead.TryGetValue(old, out int h) ? h : -1);
                    s_syncHead[old] = j;
                }
            }
            ln.Mats.Clear(); ln.Slots.Clear();
            if (ln.Mats.Capacity < n) ln.Mats.Capacity = n;
            if (ln.Slots.Capacity < n) ln.Slots.Capacity = n;
            bool lookup = s_syncHead.Count > 0;
            for (int i = 0; i < n; i++)
            {
                var m = src[i];
                int slot;
                if (i < common && s_syncSame[i]) { slot = oldSlots[i]; oldSlots[i] = -1; }
                else if (lookup && s_syncHead.TryGetValue(m, out int j))
                {
                    slot = oldSlots[j]; oldSlots[j] = -1;
                    int nx = s_syncNext[j];
                    if (nx < 0) s_syncHead.Remove(m); else s_syncHead[m] = nx;
                }
                else if (lp != null)
                {
                    Vector4 lw = Vector4.zero;
                    if (lp.Size > 0f) { var refW = m.MultiplyPoint3x4(lp.RefFirst); lw = new Vector4(refW.x, refW.y, refW.z, lp.Size); }
                    slot = AddInstance(batch, m * lp.FromFirst, lw, lodStart, lodEnd);
                }
                else slot = batch.LodPerInstance ? AddInstance(batch, m, LodWorld(bakedLod, m), lodStart, lodEnd) : AddInstance(batch, m, BakedLod(batch, m));
                ln.Mats.Add(m); ln.Slots.Add(slot);
                reg.Slots.Add(slot); reg.SlotInstance.Add(i);
            }
            foreach (int slot in oldSlots) if (slot >= 0) RemoveInstance(slot);   // matrices that left the group
        }
    }
}
