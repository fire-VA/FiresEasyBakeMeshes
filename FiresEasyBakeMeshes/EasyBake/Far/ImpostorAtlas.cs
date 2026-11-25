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
        private static Pair s_building;   // 1.2.450: the first pair while it is being created over two frames
        private static readonly System.Diagnostics.Stopwatch s_buildSw = new System.Diagnostics.Stopwatch();
        private static double s_buildMs1;

        internal static int PxPerView(ImpostorEngine.Sharpness s) => s == ImpostorEngine.Sharpness.Low ? 64 : s == ImpostorEngine.Sharpness.High ? 192 : s == ImpostorEngine.Sharpness.Ultra ? 256 : 128;
        internal static int MipsFor(int size) { int m = 0; while (size >= 4 && (size & 3) == 0) { m++; size >>= 1; } return m; }   // 1.2.346 ([ghost], Fire run_8: 240 "Index was outside the bounds" bake failures): every mip must be a whole number of 4x4 DXT blocks. Sharpness High = 192 px x 8 views = 1536, whose 9th mip is 6x6: ImpostorDxt.Size(6,6) reserved one block while Encode wrote four, past the end of the chain. Stop at the last multiple of 4 (1536 -> 8 mips, down to 12x12).

        /// <summary>The pair bakes go into right now (Next while a Sharpness change is pending).</summary>
        internal static Pair Target => Next ?? Current;

        /// <summary>Sharpness at the moment: start a NEXT pair when it differs from the current one.</summary>
        internal static void CheckSize(int kinds)
        {
            int size = PxPerView(ImpostorEngine.SharpnessNow) * ImpostorKinds.Views;
            // 1.2.450 ([visual], 449 leg: "created + uploaded in 59.3 / 60.0 ms on the main thread", DontInitializePixels did not help): the first pair is
            // created over TWO frames (albedo array, then normal array), so no single frame pays both. Current stays null until both exist; the baker
            // waits for Target (ImpostorBaker.Begin) and nothing draws without a baked slice.
            if (Current == null)
            {
                if (s_building == null || s_building.Size != size)
                {
                    int cap = Math.Max(8, ((kinds + 7) / 8) * 8);
                    s_buildSw.Restart();
                    s_building = new Pair { Size = size, Mips = MipsFor(size), Capacity = cap, Has = new bool[cap] };
                    s_building.Albedo = NewArray(size, s_building.Mips, cap, false, "FarImpostorAlbedo");
                    s_buildMs1 = s_buildSw.Elapsed.TotalMilliseconds;
                    return;
                }
                s_buildSw.Restart();
                s_building.Normal = NewArray(size, s_building.Mips, s_building.Capacity, true, "FarImpostorNormal");
                Current = s_building; s_building = null;
                EasyBakeLog.Info(ImpostorEngine.Tag + $"atlas arrays {size} px, {Current.Mips} mips, {Current.Capacity} slices ({Current.Bytes / (1024 * 1024)} MB), created over 2 frames on the main thread: albedo {s_buildMs1:0.0} ms, normal {s_buildSw.Elapsed.TotalMilliseconds:0.0} ms");
                return;
            }
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
            var sw = System.Diagnostics.Stopwatch.StartNew();   // 1.2.448 ([visual], lead: 447 "FarImpostors worst 121811 us" on first use): time the array creation + its zero upload
            p.Albedo = NewArray(size, p.Mips, cap, false, "FarImpostorAlbedo");
            p.Normal = NewArray(size, p.Mips, cap, true, "FarImpostorNormal");
            EasyBakeLog.Info(ImpostorEngine.Tag + $"atlas arrays {size} px, {p.Mips} mips, {cap} slices ({p.Bytes / (1024 * 1024)} MB), created + uploaded in {sw.Elapsed.TotalMilliseconds:0.0} ms on the main thread");
            return p;
        }

        private static Texture2DArray NewArray(int size, int mips, int cap, bool linear, string name)
        {
            // 1.2.449 ([visual], 448 leg: "created + uploaded in 64.7 ms on the main thread"): DontInitializePixels skips filling the 63 MB CPU copy with zeros
            // before the one upload (every slice is overwritten by a bake or a disk read before it is drawn: the draw is gated on Kind.HasSlice, ImpostorDraw.cs:141, set only after a bake / disk read fills the slice). The timer
            // line in Make() proves the new cost.
            var gf = linear ? UnityEngine.Experimental.Rendering.GraphicsFormat.RGBA_DXT5_UNorm : UnityEngine.Experimental.Rendering.GraphicsFormat.RGBA_DXT5_SRGB;
            // 1.2.452 ([visual], 450 leg: albedo 33.1 ms / normal 21.1 ms): slices are only ever written on the GPU (Graphics.CopyTexture from the baker's per-view
            // textures, ImpostorBaker.cs:212-213, and from the disk-read temp, below), so the CPU copy is never used. DontUploadUponCreate: the constructor no longer
            // uploads the (uninitialised) CPU data; the single Apply(false, true) creates the GPU texture and frees the CPU copy. UNCONFIRMED that the constructor
            // uploaded before; the split timer below (ctor vs Apply) proves where the cost is.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var t = new Texture2DArray(size, size, cap, gf,
                                       UnityEngine.Experimental.Rendering.TextureCreationFlags.MipChain | UnityEngine.Experimental.Rendering.TextureCreationFlags.DontInitializePixels
                                       | UnityEngine.Experimental.Rendering.TextureCreationFlags.DontUploadUponCreate, mips)
            { name = name, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 1, hideFlags = HideFlags.HideAndDontSave };
            double ctor = sw.Elapsed.TotalMilliseconds;
            t.Apply(false, true);   // GPU only
            EasyBakeLog.Info(ImpostorEngine.Tag + $"atlas array '{name}' {size} px x {cap}: constructor {ctor:0.0} ms, upload (Apply) {sw.Elapsed.TotalMilliseconds - ctor:0.0} ms");
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
