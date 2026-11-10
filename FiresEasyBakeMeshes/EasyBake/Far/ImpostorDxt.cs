using System;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Our own DXT5 (BC3) block encoder for the impostor atlases ([visual] 11-08). Runs on a worker, so a bake never pays Unity's main-
    /// thread Texture2D.Compress hitch. Colour: bounding-box endpoints inset by 1/16 (the usual cheap range fit), 4-colour palette,
    /// nearest index. Alpha: min / max with the 8-value ramp, nearest index. Good enough for far trees (a few metres on screen); about
    /// 20-40 ms for a 1024² mip chain on one worker. Input RGBA32, width and height multiples of 4.
    /// </summary>
    internal static class ImpostorDxt
    {
        internal static int Size(int w, int h) => (w / 4) * (h / 4) * 16;

        internal static void Encode(byte[] rgba, int w, int h, byte[] dst, int dstOffset)
        {
            var block = new byte[64];
            int o = dstOffset;
            for (int by = 0; by < h; by += 4)
                for (int bx = 0; bx < w; bx += 4)
                {
                    for (int y = 0; y < 4; y++) Buffer.BlockCopy(rgba, ((by + y) * w + bx) * 4, block, y * 16, 16);
                    EncodeAlpha(block, dst, o);
                    EncodeColour(block, dst, o + 8);
                    o += 16;
                }
        }

        private static void EncodeAlpha(byte[] b, byte[] dst, int o)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; i++) { int a = b[i * 4 + 3]; if (a < min) min = a; if (a > max) max = a; }
            dst[o] = (byte)max; dst[o + 1] = (byte)min;
            ulong bits = 0;
            if (max > min)
            {
                // a0 > a1: index 0 = a0, 1 = a1, 2..7 = (6a0 + 1a1)/7 .. (1a0 + 6a1)/7
                for (int i = 0; i < 16; i++)
                {
                    int a = b[i * 4 + 3];
                    int t = (int)Math.Round((max - a) * 7.0 / (max - min));   // 0 = max .. 7 = min
                    int idx = t == 0 ? 0 : t == 7 ? 1 : t + 1;
                    bits |= (ulong)idx << (3 * i);
                }
            }
            for (int i = 0; i < 6; i++) dst[o + 2 + i] = (byte)(bits >> (8 * i));
        }

        private static void EncodeColour(byte[] b, byte[] dst, int o)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 16; i++)
            {
                int r = b[i * 4], g = b[i * 4 + 1], bl = b[i * 4 + 2];
                if (r < minR) minR = r; if (g < minG) minG = g; if (bl < minB) minB = bl;
                if (r > maxR) maxR = r; if (g > maxG) maxG = g; if (bl > maxB) maxB = bl;
            }
            int ir = (maxR - minR) >> 4, ig = (maxG - minG) >> 4, ib = (maxB - minB) >> 4;
            minR = Math.Min(255, minR + ir); minG = Math.Min(255, minG + ig); minB = Math.Min(255, minB + ib);
            maxR = Math.Max(0, maxR - ir); maxG = Math.Max(0, maxG - ig); maxB = Math.Max(0, maxB - ib);
            ushort c0 = To565(maxR, maxG, maxB), c1 = To565(minR, minG, minB);
            if (c0 < c1) { var t = c0; c0 = c1; c1 = t; }
            dst[o] = (byte)c0; dst[o + 1] = (byte)(c0 >> 8); dst[o + 2] = (byte)c1; dst[o + 3] = (byte)(c1 >> 8);
            uint idxBits = 0;
            if (c0 != c1)
            {
                From565(c0, out int r0, out int g0, out int b0); From565(c1, out int r1, out int g1, out int b1);
                int[] pr = { r0, r1, (2 * r0 + r1) / 3, (r0 + 2 * r1) / 3 };
                int[] pg = { g0, g1, (2 * g0 + g1) / 3, (g0 + 2 * g1) / 3 };
                int[] pb = { b0, b1, (2 * b0 + b1) / 3, (b0 + 2 * b1) / 3 };
                for (int i = 0; i < 16; i++)
                {
                    int r = b[i * 4], g = b[i * 4 + 1], bl = b[i * 4 + 2], best = 0, bestD = int.MaxValue;
                    for (int p = 0; p < 4; p++)
                    {
                        int dr = r - pr[p], dg = g - pg[p], db = bl - pb[p];
                        int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                        if (d < bestD) { bestD = d; best = p; }
                    }
                    idxBits |= (uint)best << (2 * i);
                }
            }
            dst[o + 4] = (byte)idxBits; dst[o + 5] = (byte)(idxBits >> 8); dst[o + 6] = (byte)(idxBits >> 16); dst[o + 7] = (byte)(idxBits >> 24);
        }

        private static ushort To565(int r, int g, int b) => (ushort)(((r * 31 + 127) / 255) << 11 | ((g * 63 + 127) / 255) << 5 | ((b * 31 + 127) / 255));
        private static void From565(ushort c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 31, g6 = (c >> 5) & 63, b5 = c & 31;
            r = (r5 << 3) | (r5 >> 2); g = (g6 << 2) | (g6 >> 4); b = (b5 << 3) | (b5 >> 2);
        }
    }
}
