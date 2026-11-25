using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E9 census ([seasons] 11-09, Tools\DESIGN_EBM_shadows_20261109.md). Census FIRST, before any merge code: how many caster draws a merged
    // caster would save. Runs only when a cascade or light slot is re-recorded (never per frame), Debug only.
    //
    // The merge key (identical shadows): a caster draw needs only the mesh, the submesh and each instance's transform UNLESS
    //  - the material is alpha-tested (EBM_ALPHATEST, or EBM/VegetationIndirect: alphatest:_Cutoff): the cut-out comes from its texture;
    //  - the shader moves vertices from material values in its caster (addshadow runs vert): EBM/Piece and EBM/StaticRock add
    //    _RippleDistance x a sine of the world position (exactly 0 when _RippleDistance is 0, the default); EBM/VegetationIndirect bends in the wind.
    // Opaque EBM/Piece, EBM/StaticRock (ripple 0), EBM/StandardIndirect, EBM/StandardSpecularIndirect: key = (mesh, submesh) -> one caster draw
    // for all their materials. Everything else: key = (mesh, submesh, material) -> unchanged. EBM/RugIndirect has no caster pass (never counted).
    internal static partial class EbmIndirect
    {
        // review 11-09: an exact key (no hash collisions): (mesh, submesh, alpha-tested, shader, material or 0 when merged)
        private struct CasterKeyT : System.IEquatable<CasterKeyT>
        {
            public int Mesh, SubMesh, Shader, Material, Cull; public bool AlphaTest;   // E9 merge code: + Cull (EBM/Piece has Cull [_Cull] per material; a two-sided caster is a different shadow)
            public bool Equals(CasterKeyT o) => Mesh == o.Mesh && SubMesh == o.SubMesh && Shader == o.Shader && Material == o.Material && Cull == o.Cull && AlphaTest == o.AlphaTest;
            public override bool Equals(object o) => o is CasterKeyT k && Equals(k);
            public override int GetHashCode() { unchecked { int h = Mesh; h = h * 397 ^ SubMesh; h = h * 397 ^ Shader; h = h * 397 ^ Material; h = h * 397 ^ Cull; return h * 2 + (AlphaTest ? 1 : 0); } }
        }
        private static readonly Dictionary<Batch, CasterKeyT> s_casterKey = new Dictionary<Batch, CasterKeyT>();   // cleared at every re-record (review 11-09)
        private static readonly HashSet<CasterKeyT> s_keyScratch = new HashSet<CasterKeyT>();
        private static float s_nextShadowCensus;
        private static int c_casterDraws, c_casterKeys, c_slotDraws, c_slotKeys, c_mergeable, c_perMaterial;
        private static readonly int[] c_cascDraws = new int[4], c_cascKeys = new int[4], c_cascBefore = new int[4];
        private static int c_casterBefore, c_slotBefore;   // E9 merge code: per-material draws the recorded groups replace

        private static CasterKeyT CasterKey(Batch b)
        {
            if (s_casterKey.TryGetValue(b, out var k)) return k;
            var m = b.Material;
            bool merge = m != null && MergeableCaster(m);
            k = new CasterKeyT
            {
                Mesh = b.Mesh != null ? b.Mesh.GetInstanceID() : 0, SubMesh = b.SubMesh,
                AlphaTest = m != null && (m.IsKeywordEnabled("EBM_ALPHATEST") || (m.shader != null && m.shader.name == "EBM/VegetationIndirect")),
                // merged: one shared caster, so shader 0 too (any of the four mergeable shaders cast the same depth); else this material exactly
                Shader = merge ? 0 : (m != null && m.shader != null ? m.shader.GetInstanceID() : 0),
                Material = merge ? 0 : (m != null ? m.GetInstanceID() : 0),
                Cull = merge ? CullOf(m) : 0,   // E9: the shared caster draws with one cull mode per group (EbmIndirectMergedCasters.SharedCaster)
            };
            s_casterKey[b] = k;
            if (merge) c_mergeable++; else c_perMaterial++;
            return k;
        }

        private static bool MergeableCaster(Material m)
        {
            var sh = m.shader;
            if (sh == null) return false;
            string n = sh.name;
            if (n != "EBM/Piece" && n != "EBM/StaticRockIndirect" && n != "EBM/StandardIndirect" && n != "EBM/StandardSpecularIndirect") return false;
            if (m.IsKeywordEnabled("EBM_ALPHATEST")) return false;
            if (m.HasProperty("_RippleDistance") && m.GetFloat("_RippleDistance") != 0f) return false;
            return true;
        }

        /// <summary>After RecordCascades re-recorded: per cascade, recorded caster draws vs the unique merge keys among them.</summary>
        private static void CensusCascades(int nb)
        {
            if (!EasyBakeLog.DebugOn()) return;
            ForgetCasterKeys();   // review 11-09: no batch refs held past this recording
            int draws = 0, keys = 0, before = 0;
            for (int k = 0; k < s_cascadeCount; k++)
            {
                s_keyScratch.Clear();
                int d = 0, bf = 0;
                for (int i = 0; i < nb; i++) if (s_recCascade[k * s_recCap + i]) { d++; bf += GroupCascadeBefore(k, i); s_keyScratch.Add(CasterKey(s_batches[i])); }   // E9: a recorded leader counts its wanted members as "before"
                c_cascDraws[k] = d; c_cascKeys[k] = s_keyScratch.Count; c_cascBefore[k] = bf;
                draws += d; keys += s_keyScratch.Count; before += bf;
            }
            c_casterDraws = draws; c_casterKeys = keys; c_casterBefore = before;
            SayShadowCensus();
        }

        /// <summary>After RecordLocal: point / spot light slots, the same count.</summary>
        private static void CensusSlots(int nb)
        {
            if (!EasyBakeLog.DebugOn()) return;
            ForgetCasterKeys();   // review 11-09: no batch refs held past this recording
            int draws = 0, keys = 0, before = 0;
            for (int j = 0; j < s_slotCount; j++)
            {
                var s = s_slots[j];
                if (s == null || s.Rec == null) continue;
                s_keyScratch.Clear();
                for (int i = 0; i < nb && i < s.Rec.Length; i++) if (s.Rec[i]) { draws++; before += GroupSlotBefore(s, i); s_keyScratch.Add(CasterKey(s_batches[i])); }
                keys += s_keyScratch.Count;
            }
            c_slotDraws = draws; c_slotKeys = keys; c_slotBefore = before;
        }

        private static void SayShadowCensus()
        {
            if (Time.unscaledTime < s_nextShadowCensus) return;
            s_nextShadowCensus = Time.unscaledTime + 30f;
            if (s_mergeActive)   // E9 IndirectMergedCasters on: the recorded draws ARE merged; "before" = what one caster per material would have recorded
            {
                var mb = new System.Text.StringBuilder("[ShadowCensus] E9 merged casters ON (last recording): ");
                mb.Append(c_mergeGroups).Append(" caster group(s) of 2+ holding ").Append(c_mergeMembers).Append(" batch(es); cascade caster draws before -> after ")
                  .Append(c_casterBefore).Append(" -> ").Append(c_casterDraws).Append(" (");
                for (int k = 0; k < s_cascadeCount; k++) mb.Append(k > 0 ? ", " : "").Append("c").Append(k + 1).Append(' ').Append(c_cascBefore[k]).Append("->").Append(c_cascDraws[k]);
                mb.Append("); light slot draws ").Append(c_slotBefore).Append(" -> ").Append(c_slotDraws).Append("; visible space used ").Append(s_visibleUsed).Append(" of stride ").Append(s_visibleStride);
                EasyBakeLog.Debug(mb.ToString());
                return;
            }
            var sb = new System.Text.StringBuilder("[ShadowCensus] E9 merge (last recording): cascade caster draws ");
            sb.Append(c_casterDraws).Append(" -> ").Append(c_casterKeys).Append(" merged (");
            for (int k = 0; k < s_cascadeCount; k++) sb.Append(k > 0 ? ", " : "").Append("c").Append(k + 1).Append(' ').Append(c_cascDraws[k]).Append("->").Append(c_cascKeys[k]);
            sb.Append("); light slot draws ").Append(c_slotDraws).Append(" -> ").Append(c_slotKeys)
              .Append("; batches by caster kind: mergeable (opaque, no vertex motion) ").Append(c_mergeable).Append(", per material (alpha-tested / ripple / wind) ").Append(c_perMaterial)
              .Append(". A merged draw = one caster material for all materials of that mesh; nothing merged yet (census only)");
            EasyBakeLog.Debug(sb.ToString());
        }

        private static void ForgetCasterKeys() { s_casterKey.Clear(); c_mergeable = c_perMaterial = 0; }
    }
}
