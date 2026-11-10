using System;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // [seasons] 11-09, design 4.2 (Tools\DESIGN_EBM_grass_20261109.md; DROP_NOTES_seasons_grass2.md round 2). Key [Optimize] IndirectGrassStepGate (on),
    // acting only while EBM grass is on. EBM's own code; relies on no other mod.
    //
    // Vanilla: every terrain op ends in ClutterSystem.ResetGrass (TerrainComp.DoOperation), and walking in deep snow places one every metre
    // (Character's m_deepSnowWalkObj). The grass in the touched patches is regenerated even when the op changed nothing grass reads.
    // What grass reads from the paint (ClutterSystem.GenerateVegPatch): IsCleared (r, g or b > 0.5) and the vegetation mask (a). A paint-only op
    // changes no height. So: snapshot those two per touched vertex before the paint, compare after; nothing changed = the ResetGrass that the
    // same op asks for next (same centre and radius) is skipped. Save and Poke still run, so the trail on the ground is exactly vanilla's.
    // Always falls through to vanilla when: the op changes height; its footprint reaches a tile edge (the paint spreads into the neighbour,
    // TerrainComp.PaintCleared's spread); a vertex crossed; the reset that follows is not this op's own (another mod's, CheckLoad, a batch).
    // Verify (IndirectGrassStepGateVerify, off): nothing is skipped; the rebuilt patches inside a "no change" footprint are compared blade for
    // blade (EbmIndirectGrass's upload hash), and any difference is counted: must stay 0.
    [HarmonyPatch]
    internal static class EbmGrassStepGate
    {
        private static bool KeyOn => FiresEasyBakeMeshesPlugin.IndirectGrassStepGate == null || FiresEasyBakeMeshesPlugin.IndirectGrassStepGate.Value;
        private static bool VerifyOn => FiresEasyBakeMeshesPlugin.IndirectGrassStepGateVerify != null && FiresEasyBakeMeshesPlugin.IndirectGrassStepGateVerify.Value;

        private static readonly AccessTools.FieldRef<TerrainComp, Heightmap> F_Hmap = AccessTools.FieldRefAccess<TerrainComp, Heightmap>("m_hmap");
        private static readonly AccessTools.FieldRef<TerrainComp, Color[]> F_Paint = AccessTools.FieldRefAccess<TerrainComp, Color[]>("m_paintMask");
        private static readonly AccessTools.FieldRef<TerrainComp, bool[]> F_Modified = AccessTools.FieldRefAccess<TerrainComp, bool[]>("m_modifiedPaint");
        private static readonly AccessTools.FieldRef<TerrainComp, int> F_Width = AccessTools.FieldRefAccess<TerrainComp, int>("m_width");

        private const int MaxSide = 33;   // a footprint wider than this (radius > 16 vertices) is never gated
        private static readonly byte[] s_cleared = new byte[MaxSide * MaxSide];
        private static readonly float[] s_alpha = new float[MaxSide * MaxSide];
        private static TerrainComp s_comp;
        private static int s_x0, s_y0, s_x1, s_y1, s_pitch;
        // armed by a "no change" op: the next ResetGrass with this centre and radius is skipped
        private static bool s_armed; private static Vector3 s_armPos; private static float s_armRadius; private static int s_armFrame;
        internal static int Gated, Crossed, Edge, Height, NotOurs, VerifyChecked, VerifyDiffered;

        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Before(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
        {
            s_comp = null; s_armed = false;
            if (!KeyOn || !EbmIndirectGrass.Active || __instance == null || modifier == null || !modifier.m_paintCleared) return;
            try
            {
                if (modifier.m_level || modifier.m_raise || modifier.m_smooth) { Height++; return; }
                var hm = F_Hmap(__instance);
                var paint = F_Paint(__instance); var mod = F_Modified(__instance);
                if (hm == null || paint == null || mod == null) return;
                int w = F_Width(__instance);
                Vector3 p = pos;
                if (modifier.m_halfOffset) { p.x -= 0.5f; p.z -= 0.5f; }   // PaintCleared's own offset
                hm.WorldToVertexMask(p, out int cx, out int cy);
                int r = Mathf.CeilToInt(modifier.m_paintRadius / hm.m_scale);
                int x0 = cx - r, x1 = cx + r, y0 = cy - r, y1 = cy + r;
                // a footprint on or past a tile edge spreads into the neighbour comp: vanilla
                if (x0 <= 0 || y0 <= 0 || x1 >= w || y1 >= w || x1 - x0 + 1 > MaxSide || y1 - y0 + 1 > MaxSide) { Edge++; return; }
                s_x0 = x0; s_y0 = y0; s_x1 = x1; s_y1 = y1; s_pitch = w + 1;
                int k = 0;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++, k++)
                    {
                        int i = y * s_pitch + x;
                        Color c = mod[i] ? paint[i] : hm.GetPaintMask(x, y);   // what the grass reads now: this comp's paint, else the tile's base
                        s_cleared[k] = (byte)(c.r > 0.5f || c.g > 0.5f || c.b > 0.5f ? 1 : 0);
                        s_alpha[k] = c.a;
                    }
                s_comp = __instance;
            }
            catch (Exception e) { s_comp = null; EasyBakeLog.Debug("[Indirect] grass step gate snapshot skipped: " + e.Message); }
        }

        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void After(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
        {
            if (s_comp == null || s_comp != __instance) return;
            s_comp = null;
            try
            {
                var hm = F_Hmap(__instance);
                var paint = F_Paint(__instance); var mod = F_Modified(__instance);
                int k = 0;
                for (int y = s_y0; y <= s_y1; y++)
                    for (int x = s_x0; x <= s_x1; x++, k++)
                    {
                        int i = y * s_pitch + x;
                        if (!mod[i]) continue;   // untouched and unmodified: still the tile's base, unchanged
                        Color c = paint[i];
                        byte cl = (byte)(c.r > 0.5f || c.g > 0.5f || c.b > 0.5f ? 1 : 0);
                        if (cl != s_cleared[k] || c.a != s_alpha[k]) { Crossed++; return; }
                    }
                s_armed = true; s_armPos = pos; s_armRadius = modifier.GetRadius(); s_armFrame = Time.frameCount;
            }
            catch (Exception e) { EasyBakeLog.Debug("[Indirect] grass step gate compare skipped: " + e.Message); }
        }

        // the op's own reset: vanilla DoOperation (and any batcher that calls ResetGrass(pos, radius) right after the op, in the same frame)
        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.ResetGrass)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool ResetGrassPrefix(Vector3 center, float radius)
        {
            if (!s_armed) return true;
            bool ours = s_armFrame == Time.frameCount && center == s_armPos && radius == s_armRadius;
            s_armed = false;
            if (!ours) { NotOurs++; return true; }
            if (VerifyOn) { EbmIndirectGrass.PredictSame(center, radius); return true; }   // verify: rebuild anyway, compare blades
            Gated++;
            return false;
        }

        internal static string Census() =>
            $"step gate {(KeyOn ? "on" : "off")}{(VerifyOn ? " (verify)" : "")}: skipped {Gated}, crossed {Crossed}, tile edge {Edge}, height ops {Height}, reset not the op's own {NotOurs}" +
            (VerifyOn ? $", verify: {VerifyChecked} rebuilt patch(es) in no-change footprints, {VerifyDiffered} differed (must be 0)" : "");

        internal static void ClearCounts() { Gated = Crossed = Edge = Height = NotOurs = VerifyChecked = VerifyDiffered = 0; }
    }
}
