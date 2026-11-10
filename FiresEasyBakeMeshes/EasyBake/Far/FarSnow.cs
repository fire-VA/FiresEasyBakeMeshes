using System;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.210 ([ghost]; was Core's read-only Vedr link, a type-name lookup of Vedr.VedrApi). EBM relies on no other mod, so the far view's
    /// season snow is PUSHED in: the snow owner calls EbmApi.RegisterFarSnow once with its functions. Nothing registered = no season snow
    /// (far buildings plain, impostors use altitude snow), exactly what the old link did when Vedr was absent.
    /// The amount a far chunk gets is the old rule: PieceSnowCover(pos) x PieceExposureAt(top), quantised to quarters.
    /// </summary>
    internal static class FarSnow
    {
        // EBM/Piece reads _EbmSnowCover; the far chunks also set _VedrSnowCover, which the season mod's own piece shader reads while EBM's
        // piece shader is not the one drawing (writing a property name is no dependency; nothing of Vedr is called).
        internal static readonly int SnowCoverId = Shader.PropertyToID("_EbmSnowCover");
        internal static readonly int LegacySnowCoverId = Shader.PropertyToID("_VedrSnowCover");

        private static Func<bool> s_active;
        private static Func<Vector3, float> s_cover, s_exposure;
        private static Func<int> s_revision;
        private static Func<float> s_world;

        internal static void Register(Func<bool> active, Func<Vector3, float> coverAt, Func<Vector3, float> exposureAt, Func<int> revision, Func<float> worldSnow01)
        {
            s_active = active; s_cover = coverAt; s_exposure = exposureAt; s_revision = revision; s_world = worldSnow01;
            EasyBakeLog.Info(Bound
                ? $"[Far] season snow registered (cover + exposure{(revision != null ? " + revision" : "")}{(worldSnow01 != null ? " + world amount" : "")}): far buildings and impostors follow it"
                : (worldSnow01 != null ? "[Far] season snow registered for impostors only (world amount)" : "[Far] season snow withdrawn: far buildings plain, impostors use altitude snow"));
        }

        /// <summary>Far-building snow is possible (cover + exposure + active registered).</summary>
        internal static bool Bound => s_active != null && s_cover != null && s_exposure != null;

        internal static bool Active { get { try { return Bound && s_active(); } catch { return false; } } }
        internal static int Revision { get { try { return s_revision != null ? s_revision() : 0; } catch { return 0; } } }

        /// <summary>0..4 (quarters of full cover). 0 when no season snow is live.</summary>
        /// <summary>1.2.262: the registered cover alone at a position (0 when nothing is registered or it throws). Far snow caps read it.</summary>
        internal static float CoverAt(Vector3 pos)
        {
            if (s_cover == null) return 0f;
            try { return s_cover(pos); } catch { return 0f; }
        }

        internal static byte Level(Vector3 pos, Vector3 top)
        {
            try
            {
                float c = s_cover(pos);
                if (c <= 0.01f) return 0;
                float e = s_exposure(top);
                return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(c * e) * 4f), 0, 4);
            }
            catch { return 0; }
        }

        /// <summary>The impostors' one snow amount; false = none registered or not live (they use altitude snow).</summary>
        internal static bool WorldAmount(out float amount)
        {
            amount = 0f;
            if (s_world == null) return false;
            try
            {
                float v = s_world();   // the owner returns a negative value while its snow is not ready (the old link's IsReady gate)
                if (v < 0f) return false;
                amount = Mathf.Clamp01(v);
                return true;
            }
            catch { return false; }
        }
    }
}
