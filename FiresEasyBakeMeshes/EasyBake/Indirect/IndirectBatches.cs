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
    }

    // Per-renderer API over EbmIndirect. Handles carry the release epoch, so a handle from before a key toggle never removes a reused slot.
    internal static class IndirectBatches
    {
        private const int SlotBits = 22, SlotMask = (1 << SlotBits) - 1;

        public static bool Active => EbmIndirect.Active && (FiresEasyBakeMeshesPlugin.IndirectCoverage == null || FiresEasyBakeMeshesPlugin.IndirectCoverage.Value);

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
            var batch = EbmIndirect.BatchOf(mesh, submesh, variant, shadows, true, layer, uint.MaxValue, start, end);
            var lodWorld = lod.WorldSize > 0f ? new Vector4(lod.ReferencePointWorld.x, lod.ReferencePointWorld.y, lod.ReferencePointWorld.z, lod.WorldSize) : Vector4.zero;
            int slot = EbmIndirect.AddInstance(batch, objectToWorld, lodWorld, start, end);   // rank 6: its own window (per instance when the batch is)
            return (EbmIndirect.Epoch << SlotBits) | (slot & SlotMask);
        }

        public static void Remove(int handle)
        {
            if (handle < 0 || (handle >> SlotBits) != EbmIndirect.Epoch) return;   // stale: from before a release
            EbmIndirect.RemoveInstance(handle & SlotMask);
        }

        public static void SetMatrix(int handle, in Matrix4x4 objectToWorld)
        {
            if (handle < 0 || (handle >> SlotBits) != EbmIndirect.Epoch) return;
            EbmIndirect.MoveInstance(handle & SlotMask, objectToWorld);
        }
    }
}
