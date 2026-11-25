using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E9 merged shadow casters ([seasons], Tools\DESIGN_EBM_shadows_20261109.md "1. E9" + "E9 in the compute"; [Optimize] IndirectMergedCasters, off).
    // A caster draw of an opaque, motionless EBM material writes depth from the mesh and the instance transform only, so batches that differ
    // only in material (the census merge key, EbmShadowCensus.CasterKey) cast the same shadow. At each FULL layout those batches form a caster
    // group; its first batch is the LEADER:
    //  - the leader's visible range is the sum of its members' capacities, so every member's caster appends fit (the compute clamp still guards);
    //  - GpuBatch.Pad = the leader index (own index = not merged), and the cull appends caster views 1, 2..5 and 7..10 to the leader's row and range;
    //    camera views stay per batch, so the camera draws, looks and materials are untouched;
    //  - the cascades and light slots record ONE draw per leader with EBM/SharedCaster (one material per cull mode), members never.
    // Membership changes only at a full layout: a grouped batch that outgrows its own capacity, or is queued to move alone, forces one, so the
    // sum rule can never be broken by IndirectStableLayout's one-batch placement. Key off, or the bundle without the shader: Pad = own index
    // everywhere, which is exactly today's path.
    internal static partial class EbmIndirect
    {
        private static int[] s_casterLeader = new int[0];   // per batch: its caster leader (own index = not merged)
        private static int[] s_casterNext = new int[0];     // per batch: the next member of its group (-1 = last); the leader is the head
        private static int[] s_casterTail = new int[0];     // layout scratch: the last member linked to each leader
        private static readonly Dictionary<CasterKeyT, int> s_groupHead = new Dictionary<CasterKeyT, int>();   // layout scratch, cleared after use
        private static bool s_mergeActive;                  // the last full layout grouped casters (the key + the shader)
        private static bool s_mergeShaderSaid;
        private static readonly Material[] s_sharedCasterMat = new Material[3];   // per UnityEngine.Rendering.CullMode (Off, Front, Back)
        private static readonly int[] s_sharedCasterPass = { -2, -2, -2 };
        private static readonly int IdCull = Shader.PropertyToID("_Cull");
        internal static int c_mergeGroups, c_mergeMembers;  // [ShadowCensus]: groups of 2+ and the batches inside them, at the last layout

        private static bool MergedCastersKey => FiresEasyBakeMeshesPlugin.IndirectMergedCasters != null && FiresEasyBakeMeshesPlugin.IndirectMergedCasters.Value;

        // the key AND the shader: an older bundle has no EBM/SharedCaster, so the key falls back to today's path and says so once (Warning)
        private static bool MergedCastersReady()
        {
            if (!MergedCastersKey) return false;
            if (EbmIndirectAssets.BundleShader("EBM/SharedCaster") != null) return true;
            if (!s_mergeShaderSaid)
            {
                s_mergeShaderSaid = true;
                EasyBakeLog.Warn("[Indirect] IndirectMergedCasters is on, but the indirect bundle has no EBM/SharedCaster shader: shadows are drawn one caster per material, as before");
            }
            return false;
        }

        // the shared caster material for a cull mode (EBM/Piece carries Cull [_Cull] per material; the census key keeps cull modes apart)
        private static Material SharedCaster(int cull, out int pass)
        {
            cull = Mathf.Clamp(cull, 0, 2);
            var m = s_sharedCasterMat[cull];
            if (m == null)
            {
                var sh = EbmIndirectAssets.BundleShader("EBM/SharedCaster");
                m = new Material(sh) { name = "EBM shared caster (cull " + (CullMode)cull + ")", enableInstancing = true };
                m.SetFloat(IdCull, cull);
                s_sharedCasterMat[cull] = m;
                s_sharedCasterPass[cull] = m.FindPass("ShadowCaster");
            }
            pass = s_sharedCasterPass[cull];
            return m;
        }

        private static int CullOf(Material m) => m != null && m.HasProperty(IdCull) ? Mathf.Clamp(Mathf.RoundToInt(m.GetFloat(IdCull)), 0, 2) : (int)CullMode.Back;

        private static int CasterLeaderOf(int i) => s_mergeActive && i < s_casterLeader.Length ? s_casterLeader[i] : i;
        // in a group of 2+ (as leader or member): such a batch never moves alone
        private static bool InCasterGroup(int i) => s_mergeActive && i < s_casterLeader.Length && (s_casterLeader[i] != i || s_casterNext[i] >= 0);
        private static bool IsGroupLeader(int i) => s_mergeActive && i < s_casterLeader.Length && s_casterLeader[i] == i && s_casterNext[i] >= 0;

        /// <summary>Full layout, before the ranges: links the caster groups. Returns whether merging is on for this layout.</summary>
        private static bool BuildCasterGroups(int nb)
        {
            s_mergeActive = false;
            c_mergeGroups = c_mergeMembers = 0;
            bool on = MergedCastersReady();
            int len = Mathf.NextPowerOfTwo(Mathf.Max(16, nb));
            if (s_casterLeader.Length < len) { s_casterLeader = new int[len]; s_casterNext = new int[len]; s_casterTail = new int[len]; }
            for (int i = 0; i < s_casterLeader.Length; i++) { s_casterLeader[i] = i; s_casterNext[i] = -1; s_casterTail[i] = i; }   // later batches start alone
            if (!on) return false;
            ForgetCasterKeys();
            s_groupHead.Clear();
            for (int i = 0; i < nb; i++)
            {
                var b = s_batches[i];
                // only casters with a pass join: a non-caster's instances never append to a caster view, so its capacity must not be counted
                if (b.Shadows == ShadowCastingMode.Off || b.Mesh == null || b.Material == null || !HasCasterPass(b.Material)) continue;
                var key = CasterKey(b);
                if (key.Material != 0) continue;   // per-material key (alpha-tested, ripple, wind): keeps its own caster
                if (!s_groupHead.TryGetValue(key, out int head)) { s_groupHead[key] = i; continue; }
                s_casterLeader[i] = head;
                s_casterNext[s_casterTail[head]] = i;
                s_casterTail[head] = i;
            }
            foreach (var kv in s_groupHead) if (s_casterNext[kv.Value] >= 0) { c_mergeGroups++; for (int m = kv.Value; m >= 0; m = s_casterNext[m]) c_mergeMembers++; }
            s_groupHead.Clear();
            ForgetCasterKeys();   // no batch refs held past the layout (the census rebuilds its own)
            s_mergeActive = true;
            return true;
        }

        // the visible range a batch reserves: its own capacity, or for a leader the sum of its group's (each member's own headroom included)
        private static int RangeOf(Batch b)
        {
            if (!IsGroupLeader(b.Index)) return b.Capacity;
            int sum = 0;
            for (int m = b.Index; m >= 0; m = s_casterNext[m]) sum += s_batches[m].Capacity;
            return sum;
        }

        // IndirectStableLayout places a batch alone only when it is in no group (moving a member would break its leader's sum)
        private static bool PlaceNeedsFullLayout()
        {
            if (!s_mergeActive) return false;
            foreach (var b in s_place) if (InCasterGroup(b.Index)) return true;
            return false;
        }

        // ── record helpers: a member is never recorded in a caster view; a leader stands for its whole group ──
        private static bool CasterRowCasts(int i)
        {
            if (!s_mergeActive || i >= s_casterLeader.Length) return CastsShadows(s_batches[i]);
            if (s_casterLeader[i] != i) return false;
            for (int m = i; m >= 0; m = s_casterNext[m]) if (CastsShadows(s_batches[m])) return true;
            return false;
        }

        // cascade k wants row i: its group when i leads one (any member the CPU cells place in the sun volume and the cascade, not size-skipped)
        private static bool CasterRowWantsCascade(int k, int i)
        {
            if (!IsGroupLeader(i)) return (!s_mergeActive || i >= s_casterLeader.Length || s_casterLeader[i] == i) && CascadeWants(k, i);
            if (EmptySkip(k, i)) return false;   // the leader's row holds the group's GPU count
            for (int m = i; m >= 0; m = s_casterNext[m])
                if (CastsShadows(s_batches[m]) && (!CasterCullOn || (VisRow(1, m) && VisRow(2 + k, m))) && !SizeSkip(k, m)) return true;
            return false;
        }

        private static bool CasterRowWantsSlot(CasterSlot s, int i)
        {
            if (!IsGroupLeader(i)) return (!s_mergeActive || i >= s_casterLeader.Length || s_casterLeader[i] == i) && SlotWants(s, i);
            for (int m = i; m >= 0; m = s_casterNext[m]) if (CastsShadows(s_batches[m]) && SlotWants(s, m)) return true;
            return false;
        }

        // the material + pass a caster row draws with: EBM/SharedCaster for a group, the batch's own otherwise
        private static Material CasterMaterialOf(Batch b, out int pass)
        {
            if (IsGroupLeader(b.Index)) return SharedCaster(CullOf(b.Material), out pass);
            pass = b.CasterPass;
            return b.Material;
        }

        // members' before-merge draws in a recorded group (census only, Debug): what one-caster-per-material would have recorded there
        private static int GroupCascadeBefore(int k, int i)
        {
            if (!IsGroupLeader(i)) return 1;
            int n = 0;
            for (int m = i; m >= 0; m = s_casterNext[m]) if (CastsShadows(s_batches[m]) && (!CasterCullOn || (VisRow(1, m) && VisRow(2 + k, m)))) n++;
            return Mathf.Max(1, n);
        }

        private static int GroupSlotBefore(CasterSlot s, int i)
        {
            if (!IsGroupLeader(i)) return 1;
            int n = 0;
            for (int m = i; m >= 0; m = s_casterNext[m]) if (CastsShadows(s_batches[m]) && SlotWants(s, m)) n++;
            return Mathf.Max(1, n);
        }

        // ── the sun-volume caster draws (view 1, only while per-cascade shadows are off): one ShadowsOnly draw per group ──
        private static int DrawMergedSunOnly(ref long shTris)
        {
            if (!s_mergeActive || CascadesOn) return 0;
            int nb = s_batches.Count, draws = 0;
            for (int i = 0; i < nb && i < s_casterLeader.Length; i++)
            {
                if (!IsGroupLeader(i)) continue;
                var b = s_batches[i];
                if (!HasMesh(b)) continue;
                bool vis = false, any = false; Bounds world = default; long tris = 0;
                for (int m = i; m >= 0; m = s_casterNext[m])
                {
                    var mb = s_batches[m];
                    if (mb.Count <= 0 || mb.Shadows == ShadowCastingMode.Off || !mb.WorldSet) continue;
                    if (!any) { world = mb.World; any = true; } else world.Encapsulate(mb.World);
                    tris += mb.Tris * mb.Count;
                    if (m >= s_visWidth || s_vis[s_visWidth + m]) vis = true;
                }
                if (!any || !vis) continue;
                var mat = SharedCaster(CullOf(b.Material), out _);
                var rp = new RenderParams(mat)
                { motionVectorMode = MotionVectorGenerationMode.Camera, 
                    worldBounds = world, layer = b.Layer, renderingLayerMask = b.RenderingLayerMask, receiveShadows = false,
                    shadowCastingMode = ShadowCastingMode.ShadowsOnly, matProps = b.ShadowProps, camera = s_drawCamera,
                };
                Graphics.RenderMeshIndirect(in rp, b.Mesh, s_argsBuf, 1, s_batchCap + i);
                draws++; shTris += tris;
            }
            return draws;
        }

        private static void ReleaseMergedCasters()
        {
            s_mergeActive = false;
            for (int c = 0; c < s_sharedCasterMat.Length; c++) { if (s_sharedCasterMat[c] != null) Object.Destroy(s_sharedCasterMat[c]); s_sharedCasterMat[c] = null; s_sharedCasterPass[c] = -2; }
        }
    }
}
