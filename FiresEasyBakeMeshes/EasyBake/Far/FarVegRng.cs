using System;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [worldgen] 11-18, ported from FAT's FarRng (0.2.1417), no FAT references. Our own Xorshift128, written from the published algorithm
    // (Marsaglia 2003) and UnityEngine.Random's documented seeding, so vanilla's vegetation placement can be replayed on a worker thread.
    // TRUSTED ONLY after FarVegRngSelfTest has compared it with UnityEngine.Random in this process; the float / circle formulas are each
    // picked by that test from the candidates below (0 = not proven).
    internal struct FarVegRng
    {
        public uint X, Y, Z, W;

        internal static int ValueMode, RangeFloatMode, CircleMode;   // set by FarVegRngSelfTest (one answer per process)

        public static FarVegRng FromSeed(int seed)
        {
            var r = new FarVegRng();
            unchecked
            {
                r.X = (uint)seed;
                r.Y = r.X * 1812433253u + 1u;
                r.Z = r.Y * 1812433253u + 1u;
                r.W = r.Z * 1812433253u + 1u;
            }
            return r;
        }

        public static FarVegRng FromState(int s0, int s1, int s2, int s3)
            => new FarVegRng { X = unchecked((uint)s0), Y = unchecked((uint)s1), Z = unchecked((uint)s2), W = unchecked((uint)s3) };

        public bool SameState(int s0, int s1, int s2, int s3)
            => X == unchecked((uint)s0) && Y == unchecked((uint)s1) && Z == unchecked((uint)s2) && W == unchecked((uint)s3);

        public uint NextU32()
        {
            unchecked
            {
                uint t = X ^ (X << 11);
                X = Y; Y = Z; Z = W;
                W = W ^ (W >> 19) ^ t ^ (t >> 8);
                return W;
            }
        }

        // ---- candidate formulas (the self-test names the one that matches). Every step narrowed to float32 with an explicit cast: Mono may
        // keep float intermediates wider, Unity's native Random rounds each step (FAT 0.2.1417 found Range(float) off by the last bit without it).
        private const float Inv23 = (float)(1.0 / 8388607.0);

        public float Value01(int mode)
        {
            float f = (float)((float)(NextU32() & 0x7FFFFFu) * Inv23);
            return mode == 2 ? (float)(1f - f) : f;
        }

        public float RangeF(float min, float max, int mode)
        {
            float t = Value01(1);
            if (mode == 2) return (float)(min + (float)((float)(max - min) * t));
            float a = (float)(min * t);
            float b = (float)((float)(1f - t) * max);
            return (float)(a + b);
        }

        public int RangeI(int min, int max)
        {
            unchecked
            {
                if (min < max) return min + (int)(NextU32() % (uint)(max - min));
                if (min > max) return min - (int)(NextU32() % (uint)(min - max));
                return min;
            }
        }

        public Vector2 InsideCircle(int mode)
        {
            int rm = RangeFloatMode == 0 ? 1 : RangeFloatMode;
            if (mode == 2)
            {
                float r2 = (float)Math.Sqrt(RangeF(0f, 1f, rm));
                float th2 = RangeF(0f, Mathf.PI * 2f, rm);
                return new Vector2(r2 * (float)Math.Cos(th2), r2 * (float)Math.Sin(th2));
            }
            float th = RangeF(0f, Mathf.PI * 2f, rm);
            float r = (float)Math.Sqrt(RangeF(0f, 1f, rm));
            return new Vector2((float)(r * (float)Math.Cos(th)), (float)(r * (float)Math.Sin(th)));
        }

        // ---- the proven API the replay uses (valid only once FarVegRngSelfTest passed) ----
        public float value => Value01(ValueMode);
        public float Range(float min, float max) => RangeF(min, max, RangeFloatMode);
        public int Range(int min, int max) => RangeI(min, max);
        public Vector2 insideUnitCircle => InsideCircle(CircleMode);
    }
}
