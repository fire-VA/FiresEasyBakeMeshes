using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.206 ([ghost]'s coverage, contract of 11-08): one LIVE renderer's window into EBM's indirect batches. A covered object stays a
    // real GameObject; its renderer is hidden (forceRenderingOff) by the coverage code and drawn here instead, with its own LOD window.
    internal struct IndirectLodRange
    {
        public Vector3 ReferencePointWorld;   // LODGroup reference point, world space
        public float WorldSize;               // LODGroup.size x the group's largest lossy scale (0 = no LOD)
        public float StartHeight;             // drawn while EndHeight <= h < StartHeight (float.MaxValue / +inf for LOD0)
        public float EndHeight;               // this level's own transition height (0 = never culled)
        public bool Last;                     // E8 (1.2.292): the renderer is in its LODGroup's last level
    }

    // Per-renderer API over EbmIndirect. Handles carry the release epoch, so a handle from before a key toggle never removes a reused slot.
    internal static class IndirectBatches
    {
        private const int SlotBits = 22, SlotMask = (1 << SlotBits) - 1;

        // 1.2.392 ([visual], [lead]: IndirectSlotCompact moves instances to lower slots): the low bits of a handle are an ID into this table, not the slot itself,
        // so a holder's handle (a covered object, a far rock) stays valid when its instance moves; only s_idSlot[id] changes. One array read more per Remove /
        // SetMatrix. The table belongs to one release epoch: a new epoch starts it empty (handles of older epochs are refused by their epoch, as before).
        private static int[] s_idSlot = new int[0], s_idFree = new int[0];
        private static int s_idHigh, s_idFreeN, s_idEpoch = -1;

        private static int IdNew(int slot)
        {
            if (s_idEpoch != EbmIndirect.Epoch) { s_idEpoch = EbmIndirect.Epoch; s_idHigh = 0; s_idFreeN = 0; }
            int id;
            if (s_idFreeN > 0) id = s_idFree[--s_idFreeN];
            else
            {
                if (s_idHigh > SlotMask) return -1;   // more ids than a handle can carry
                id = s_idHigh++;
                if (id >= s_idSlot.Length) Array.Resize(ref s_idSlot, Mathf.Max(1024, s_idSlot.Length * 2));
            }
            s_idSlot[id] = slot;
            return id;
        }

        // the slot a handle names now, or -1 (another epoch, an id that was freed)
        private static int IdSlot(int handle)
        {
            if (handle < 0 || (handle >> SlotBits) != EbmIndirect.Epoch || s_idEpoch != EbmIndirect.Epoch) return -1;
            int id = handle & SlotMask;
            return id < s_idHigh ? s_idSlot[id] : -1;
        }

        private static void IdFree(int id)
        {
            s_idSlot[id] = -1;
            if (s_idFreeN >= s_idFree.Length) Array.Resize(ref s_idFree, Mathf.Max(1024, s_idFree.Length * 2));
            s_idFree[s_idFreeN++] = id;
        }

        /// <summary>EbmIndirect's compaction index: owner[slot] = -(id + 2) for every slot a live handle names; returns how many handles are live.</summary>
        /// A slot that already has an owner in the index (`none` = no owner yet) is marked `twice` and counted: two owners for one slot is never moved.
        internal static int FillOwners(int[] owner, int high, int none, int twice, ref int twiceCount)
        {
            if (s_idEpoch != EbmIndirect.Epoch) return 0;
            int n = 0;
            for (int id = 0; id < s_idHigh; id++)
            {
                int s = s_idSlot[id];
                if (s < 0) continue;
                n++;
                if (s >= high) continue;
                if (owner[s] != none) { if (owner[s] != twice) twiceCount++; owner[s] = twice; }
                else owner[s] = -(id + 2);
            }
            return n;
        }

        /// <summary>EbmIndirect's compaction: the instance of this id moved from one slot to another. False = the id does not name `from` (nothing is changed).</summary>
        internal static bool MoveId(int id, int from, int to)
        {
            if (s_idEpoch != EbmIndirect.Epoch || id < 0 || id >= s_idHigh || s_idSlot[id] != from) return false;
            s_idSlot[id] = to;
            return true;
        }

        public static bool Active => EbmIndirect.Active && (FiresEasyBakeMeshesPlugin.IndirectCoverage == null || FiresEasyBakeMeshesPlugin.IndirectCoverage.Value);
        // 1.2.381 ([visual], [lead] hot-swap audit 4c): the instance tables themselves are live whenever the indirect path is, whatever [Optimize] IndirectCoverage says.
        // A release that asks "Active" while only the coverage key is off skipped Remove, so the object's instances stayed in the tables (still drawn) while its own
        // renderers were switched back on. Remove is safe to call any time (a handle from before a release is refused by its epoch).
        public static bool TablesLive => EbmIndirect.Active;

        public static event Action ActiveChanged
        {
            add { EbmIndirect.ActiveChanged += value; }
            remove { EbmIndirect.ActiveChanged -= value; }
        }

        public static bool Accepts(Material m) => m != null && EbmIndirectAssets.VariantFor(m) != null;

        // handle >= 0, or -1 when refused (path off, no variant, no mesh)
        public static int Add(Mesh mesh, int submesh, Material material, in Matrix4x4 objectToWorld, ShadowCastingMode shadows, int layer, in IndirectLodRange lod)
        {
            if (!Active || mesh == null) return -1;
            var variant = EbmIndirectAssets.VariantFor(material);
            if (variant == null) return -1;
            if (EbmIndirect.RefuseCovered(mesh, submesh, in objectToWorld)) return -1;   // 1.2.238: broken for us; the caller rolls back and Unity draws it
            float start = lod.StartHeight <= 0f || float.IsInfinity(lod.StartHeight) ? float.MaxValue : lod.StartHeight;
            float end = Mathf.Max(0f, lod.EndHeight);
            var batch = EbmIndirect.BatchOf(mesh, submesh, variant, shadows, true, layer, uint.MaxValue, start, end, lod.Last);
            var lodWorld = lod.WorldSize > 0f ? new Vector4(lod.ReferencePointWorld.x, lod.ReferencePointWorld.y, lod.ReferencePointWorld.z, lod.WorldSize) : Vector4.zero;
            int slot = EbmIndirect.AddInstance(batch, objectToWorld, lodWorld, start, end);   // rank 6: its own window (per instance when the batch is)
            int id = IdNew(slot);   // 1.2.392: the handle carries an id; the table carries the slot
            if (id < 0) { EbmIndirect.RemoveInstance(slot); return -1; }
            return (EbmIndirect.Epoch << SlotBits) | (id & SlotMask);
        }

        public static void Remove(int handle)
        {
            int slot = IdSlot(handle);   // 1.2.392: -1 = stale (from before a release, or already removed)
            if (slot < 0) return;
            IdFree(handle & SlotMask);
            EbmIndirect.RemoveInstance(slot);
        }

        public static void SetMatrix(int handle, in Matrix4x4 objectToWorld)
        {
            int slot = IdSlot(handle);   // 1.2.392
            if (slot < 0) return;
            EbmIndirect.MoveInstance(slot, objectToWorld);
        }
    }
}
