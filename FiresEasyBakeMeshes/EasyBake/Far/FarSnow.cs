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
            if (!Layer) FarBuildingDraw.MarkSnowDirty();   // 1.2.468: caps only (a layer copy has none). 1.2.266 ([ghost] review): registering (or withdrawing) far snow changes SnowNeedsSnowMod's answer: far caps rebuild
            EasyBakeLog.Info(Bound
                ? $"[Far] season snow registered (cover + exposure{(revision != null ? " + revision" : "")}{(worldSnow01 != null ? " + world amount" : "")}): far buildings and impostors follow it"
                : (worldSnow01 != null ? "[Far] season snow registered for impostors only (world amount)" : "[Far] season snow withdrawn: far buildings plain, impostors use altitude snow"));
        }

        // ── 1.2.468 far snow LAYER (EbmApi.RegisterFarPieceSnowShapeSource) ──
        private static Func<Vector3, float> s_farShape;
        internal static void RegisterFarShape(Func<Vector3, float> classAt)
        {
            if (s_farShape == classAt) return;
            bool was = s_farShape != null;
            s_farShape = classAt;
            FarBuildingDraw.MarkSnowDirty();   // the copies rebuild once: layer codes in, vanilla caps out (or back)
            EasyBakeLog.Info(classAt != null
                ? "[Far] snow layer source registered: far buildings draw snow as a layer (static class x shelter baked per copy, the snow owner's live amount on top); vanilla snow cap meshes are no longer merged into far copies"
                : (was ? "[Far] snow layer source withdrawn: far copies merge the vanilla snow caps again" : "[Far] snow layer source: none"));
        }
        /// <summary>1.2.468: far snow is drawn as a layer (a source is registered and [03 - Far View] FarSnowLayer is on).</summary>
        internal static bool Layer => s_farShape != null && (FiresEasyBakeMeshesPlugin.FarSnowLayer == null || FiresEasyBakeMeshesPlugin.FarSnowLayer.Value);
        // 1.2.468: whether the owner's piece shader decodes _VedrSnowCover as class x shelter right now ([seasons] 11-19: read VedrApi.PieceShapeActive,
        // ApiVersion 37, = PieceSnowDriver.PieceShapeMode, live; never inferred). By name, cached once a second (LiveSnow reads it once a second anyway).
        // Absent / older Vedr = false: layer chunks then carry the 0..1 cover x their openness, as a live piece does in that mode.
        private static bool s_shapeLooked; private static System.Reflection.PropertyInfo s_shapeProp; private static bool s_shapeVal; private static float s_shapeAt = -1f;
        internal static bool OwnerShapeActive
        {
            get
            {
                if (!s_shapeLooked) { s_shapeLooked = true; s_shapeProp = Type.GetType("Vedr.VedrApi, Norger.Vedr")?.GetProperty("PieceShapeActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static); }
                if (s_shapeProp == null) return false;
                float now = Time.unscaledTime;
                if (now - s_shapeAt < 1f && s_shapeAt >= 0f) return s_shapeVal;
                s_shapeAt = now;
                try { s_shapeVal = (bool)s_shapeProp.GetValue(null); } catch { s_shapeProp = null; s_shapeVal = false; }
                return s_shapeVal;
            }
        }

        /// <summary>1.2.468: the owner's class at pos (0, 1 or 3 in the near shape encoding at full openness); -1 = no answer.</summary>
        internal static float ClassAt(Vector3 pos)
        {
            var f = s_farShape; if (f == null) return -1f;
            float v;
            try { v = f(pos); } catch (Exception e) { EasyBakeLog.Warn("[Far] the snow layer source threw, far snow layer off until it registers again: " + e.Message); s_farShape = null; FarBuildingDraw.MarkSnowDirty(); return -1f; }
            return float.IsNaN(v) || float.IsInfinity(v) || v < 0f ? -1f : Mathf.Min(v, 3f);
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
