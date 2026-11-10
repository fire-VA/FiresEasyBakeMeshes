using System.Runtime.InteropServices;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>One far tree (or any registered kind) as the GPU reads it: 32 bytes. Ground point in world space, the prefab root's
    /// scale, yaw and tilt (radians). Build arrays of these anywhere; hand them to <see cref="ImpostorEngine.SetCell"/> on the main thread.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ImpostorInstance
    {
        public Vector3 Position;
        public float Scale;
        public float Yaw;
        public int Kind;
        public float TiltX;
        public float TiltZ;

        public ImpostorInstance(Vector3 position, float scale, float yaw, int kind, float tiltX = 0f, float tiltZ = 0f)
        { Position = position; Scale = scale; Yaw = yaw; Kind = kind; TiltX = tiltX; TiltZ = tiltZ; }

        public const int Stride = 32;
    }

    /// <summary>Where a kind's mid-range mesh comes from (MidLodTrees).</summary>
    public enum MidLodSource { AuthoredFarLod, LastLod, None }

    /// <summary>Options per registered kind (SPEC_Core_ImpostorEngine §2).</summary>
    public struct KindOptions
    {
        /// <summary>Metres; 0 = the engine cap ([Far Impostors] Impostor distance). An owner may ask for less, never more.</summary>
        public float MaxDistance;
        /// <summary>How much reach grows with instance scale (1 = proportional, 0 = not at all).</summary>
        public float ReachScale;
        /// <summary>Half the bake resolution (small understorey kinds).</summary>
        public bool Understorey;
        public MidLodSource MidLodSource;
        /// <summary>The prefab's ZNetView has m_distant: the game keeps it in the distant ring, so it hands off at the ring, not the active area.</summary>
        public bool Distant;

        public static KindOptions Default => new KindOptions { ReachScale = 1f, MidLodSource = MidLodSource.AuthoredFarLod };
    }

    /// <summary>What the engine knows about a kind (read-only, after its bake).</summary>
    public struct KindInfo
    {
        public bool Baked;
        public string Prefab;
        /// <summary>Bounds of the baked renderers in the prefab root's space (unscaled).</summary>
        public Bounds Bounds;
        public float Reach;
        /// <summary>Covered area per view in m² (64 views, hemi-octahedral order), from the bake readback; null before the bake.</summary>
        public float[] SilhouetteArea;
    }
}
