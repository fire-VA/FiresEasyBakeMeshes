using System;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The impostor atlases: two DXT5 Texture2DArrays (albedo + coverage in sRGB, normal + smoothness linear), one slice per kind, all
    /// at the Sharpness resolution (8 x 8 views). Grows by copying slices on the GPU. A Sharpness change builds a NEXT pair at the new
    /// size; kinds re-bake into it and the pair swaps when every kind is in, so the old look draws until then (no gap).
    /// </summary>
    internal static class ImpostorAtlas
    {
        internal sealed class Pair
        {
            public Texture2DArray Albedo, Normal; public int Size, Mips, Capacity; public bool[] Has;
            public void Destroy() { if (Albedo != null) UnityEngine.Object.Destroy(Albedo); if (Normal != null) UnityEngine.Object.Destroy(Normal); Albedo = Normal = null; }
            public long Bytes => Albedo == null ? 0 : (long)ImpostorDxt.Size(Size, Size) * 4 / 3 * 2 * Capacity;
        }

        internal static Pair Current, Next;

        internal static int PxPerView(ImpostorEngine.Sharpness s) => s == ImpostorEngine.Sharpness.Low ? 64 : s == ImpostorEngine.Sharpness.High ? 192 : s == ImpostorEngine.Sharpness.Ultra ? 256 : 128;
        internal static int MipsFor(int size) { int m = 0; while (size >= 4) { m++; size >>= 1; } return m; }   // down to 4 x 4 (DXT blocks)

        /// <summary>The pair bakes go into right now (Next while a Sharpness change is pending).</summary>
        internal static Pair Target => Next ?? Current;

        /// <summary>Sharpness at the moment: start a NEXT pair when it differs from the current one.</summary>
        internal static void CheckSize(int kinds)
        {
            int size = PxPerView(ImpostorEngine.SharpnessNow) * ImpostorKinds.Views;
            if (Current == null) { Current = Make(size, kinds); return; }
            if (Current.Size == size) { if (Next != null) { Next.Destroy(); Next = null; } return; }
            if (Next != null && Next.Size == size) return;
            Next?.Destroy();
            Next = Make(size, kinds);
            ImpostorKinds.MarkAllDirty($"sharpness {ImpostorEngine.SharpnessNow} ({size} px atlases)");
        }

        private static Pair Make(int size, int kinds)
        {
            int cap = Math.Max(8, ((kinds + 7) / 8) * 8);
            var p = new Pair { Size = size, Mips = MipsFor(size), Capacity = cap, Has = new bool[cap] };
            p.Albedo = NewArray(size, p.Mips, cap, false, "FarImpostorAlbedo");
            p.Normal = NewArray(size, p.Mips, cap, true, "FarImpostorNormal");
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"atlas arrays {size} px, {p.Mips} mips, {cap} slices ({p.Bytes / (1024 * 1024)} MB)");
            return p;
        }

        private static Texture2DArray NewArray(int size, int mips, int cap, bool linear, string name)
        {
            var t = new Texture2DArray(size, size, cap, TextureFormat.DXT5, mips, linear)
            { name = name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
            t.Apply(false, true);   // GPU only
            return t;
        }

        internal static void Grow(Pair p, int kinds)
        {
            if (kinds <= p.Capacity) return;
            int cap = ((kinds + 7) / 8) * 8;
            var a = NewArray(p.Size, p.Mips, cap, false, "FarImpostorAlbedo");
            var n = NewArray(p.Size, p.Mips, cap, true, "FarImpostorNormal");
            for (int s = 0; s < p.Capacity; s++) if (p.Has[s]) { Graphics.CopyTexture(p.Albedo, s, a, s); Graphics.CopyTexture(p.Normal, s, n, s); }
            p.Destroy(); p.Albedo = a; p.Normal = n;
            var has = new bool[cap]; Array.Copy(p.Has, has, p.Has.Length); p.Has = has; p.Capacity = cap;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"atlas arrays grew to {cap} slices ({p.Bytes / (1024 * 1024)} MB)");
        }

        /// <summary>Puts a baked kind (DXT5 mip chains) into slice <paramref name="slice"/> of the target pair. Main thread.</summary>
        internal static void Upload(Pair p, int slice, byte[] albedo, byte[] normal)
        {
            Grow(p, slice + 1);
            CopyIn(p.Albedo, slice, p.Size, p.Mips, albedo, false);
            CopyIn(p.Normal, slice, p.Size, p.Mips, normal, true);
            p.Has[slice] = true;
        }

        private static void CopyIn(Texture2DArray dst, int slice, int size, int mips, byte[] data, bool linear)
        {
            var t = new Texture2D(size, size, TextureFormat.DXT5, mips, linear) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                t.LoadRawTextureData(data);
                t.Apply(false, true);
                for (int m = 0; m < mips; m++) Graphics.CopyTexture(t, 0, m, dst, slice, m);
            }
            finally { UnityEngine.Object.Destroy(t); }
        }

        /// <summary>A pending Sharpness change is complete when every live kind is in NEXT: swap.</summary>
        internal static bool TrySwap()
        {
            if (Next == null) return false;
            foreach (var k in ImpostorKinds.All)
                if (k.State != ImpostorKinds.State.Refused && (k.Id >= Next.Capacity || !Next.Has[k.Id])) return false;
            Current.Destroy(); Current = Next; Next = null;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"atlas arrays swapped to {Current.Size} px");
            return true;
        }

        internal static void Clear() { Current?.Destroy(); Next?.Destroy(); Current = Next = null; }
    }
}
