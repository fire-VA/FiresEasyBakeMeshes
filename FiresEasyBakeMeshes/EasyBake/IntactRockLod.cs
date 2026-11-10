using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.201 ([ghost], DESIGN_OcclusionLOD §5, lead approved 11-08). Fractured rocks (MineRock5) at the TOWN50 615 pose: 201 rocks in
    /// 300 m, rock3_mountain_frac 147 of them drawing 898k tris (ebm_rocks, Tools\_port_rocks_ghost\rocks_before.txt). Two facts from that
    /// read decide the method:
    ///  - vanilla MineRock5.UpdateMesh ALREADY merges a rock's intact hit areas into one mesh, but a PRIVATE copy per rock (101 of ~10k
    ///    tris for rock3 alone), so identical intact rocks cannot share or batch, and every copy sits in memory;
    ///  - every one of those prefabs has ONE LOD plus a cull (transition 0.148-0.214): at lodBias 5 the rocks 150-300 m away still draw
    ///    their full mesh (screen 0.11-0.72) and cast full shadows.
    /// So, with [Optimize] BakeIntactRocks on, after vanilla's own UpdateMesh, an INTACT rock (every hit area at full health):
    ///  1. draws the per-prefab SHARED copy of vanilla's combined mesh (built once from vanilla's own result; same bytes), and its private
    ///     copy is destroyed;
    ///  2. gets a LOD1: one shared, simplified copy of that mesh per prefab, made by EBM's own grid-cluster simplifier
    ///     (EbmMeshSimplify, 1.2.206: EBM's copy of the team's Core one, so no other mod is needed),
    ///     drawn below "IntactRockLod1ScreenHeight" with the same materials and shadow mode (so shadows get cheaper too).
    /// On the first UpdateMesh that finds a damaged area, vanilla's own m_meshFilter.mesh getter already gives the rock a private mesh
    /// again (a clone it combines into); this puts the rock's ORIGINAL LODGroup back and drops our LOD1. Colliders are never touched.
    /// Turning the key off restores every rock the same way. Proof: ebm_rocks (tris drawn now) + the counters in its "[Rocks] bake" line;
    /// Info once when it first engages; Debug per new shared prefab.
    /// </summary>
    internal static class IntactRockLod
    {
        internal static bool Enabled => FiresEasyBakeMeshesPlugin.BakeIntactRocks != null && FiresEasyBakeMeshesPlugin.BakeIntactRocks.Value;
        private static float Lod1Height => FiresEasyBakeMeshesPlugin.IntactRockLod1ScreenHeight != null ? Mathf.Clamp(FiresEasyBakeMeshesPlugin.IntactRockLod1ScreenHeight.Value, 0.05f, 0.99f) : 0.8f;
        // 1.2.202: 48 cells gave rock4_coast 77% of its tris (cell 0.58 m) = too little saved; key renamed so the new default reaches cfgs
        private static int Detail => FiresEasyBakeMeshesPlugin.IntactRockLod1CellsAcross != null ? Mathf.Clamp(FiresEasyBakeMeshesPlugin.IntactRockLod1CellsAcross.Value, 4, 256) : 16;

        private sealed class Shared { public Mesh Full; public Mesh Lod1; public int FullVerts; public int TrisFull, TrisLod1; public bool Lod1Tried; }
        private static readonly Dictionary<string, Shared> s_shared = new Dictionary<string, Shared>();
        private static readonly HashSet<RockState> s_states = new HashSet<RockState>();
        internal static int PrivateFreed; internal static long BytesFreed;
        private static bool s_engagedSaid;

        // MineRock5's private fields, by reflection (never a publicized reference: a JIT-time throw would skip the method).
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly FieldInfo f_hitAreas = typeof(MineRock5).GetField("m_hitAreas", F);
        private static readonly FieldInfo f_meshFilter = typeof(MineRock5).GetField("m_meshFilter", F);
        private static readonly FieldInfo f_meshRenderer = typeof(MineRock5).GetField("m_meshRenderer", F);
        private static readonly FieldInfo f_health = typeof(MineRock5).GetField("m_health", F);
        private static readonly FieldInfo f_areaHealth = typeof(MineRock5).GetNestedType("HitArea", BindingFlags.NonPublic | BindingFlags.Public)?.GetField("m_health", F);


        /// <summary>Per rock: what we changed, so it can be put back exactly.</summary>
        private sealed class RockState : MonoBehaviour
        {
            public LODGroup Group; public LOD[] Original; public GameObject Lod1; public string Prefab;
            private void OnDestroy() { s_states.Remove(this); }
        }

        [HarmonyPatch]
        private static class UpdateMeshPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MineRock5), "UpdateMesh");

            private static void Postfix(MineRock5 __instance)
            {
                try { After(__instance); }
                catch (Exception e) { EasyBakeLog.Debug($"[Rocks] intact-rock pass skipped for {__instance?.name}: {e.Message}"); }
            }
        }

        private static void After(MineRock5 rock)
        {
            if (rock == null) return;
            var state = rock.GetComponent<RockState>();
            if (!Enabled || !Intact(rock)) { if (state != null) Restore(state); return; }

            var mf = f_meshFilter?.GetValue(rock) as MeshFilter;
            var mr = f_meshRenderer?.GetValue(rock) as MeshRenderer;
            if (mf == null || mr == null || mf.sharedMesh == null) return;
            // 1.2.202 (log-proven on 1.2.201's ON leg: "[Rocks] shared intact mesh for ___MineRock5", ONE shared mesh for every kind, so
            // rock3/rock1 failed the vertex-count check and stayed vanilla): the kind comes from the rock's ZDO prefab hash, never a
            // GameObject name. The vertex count is part of the key too, so a prefab variant that combines differently gets its own entry.
            int hash = rock.GetComponent<ZNetView>()?.GetZDO()?.GetPrefab() ?? 0;
            if (hash == 0) return;
            var proto = ZNetScene.instance != null ? SafePrefab.Get(hash) : null;   // 1.2.202 [visual]: the guarded lookup (1.2.189), as everywhere in EBM
            string prefab = (proto != null ? proto.name : hash.ToString()) + "#" + mf.sharedMesh.vertexCount;
            if (!s_shared.TryGetValue(prefab, out var sh))
            {
                sh = new Shared { Full = UnityEngine.Object.Instantiate(mf.sharedMesh) };
                sh.Full.name = "EBM intact rock " + prefab;
                sh.FullVerts = sh.Full.vertexCount; sh.TrisFull = Tris(sh.Full);
                s_shared[prefab] = sh;
                EasyBakeLog.Debug($"[Rocks] shared intact mesh for {prefab}: {sh.FullVerts} vertices, {sh.TrisFull} tris (built once from vanilla's own combine)");
            }
            // 1. the shared copy instead of this rock's private one (same prefab, same intact areas = the same mesh; checked by size)
            var priv = mf.sharedMesh;
            if (priv != sh.Full)
            {
                if (priv.vertexCount != sh.FullVerts) return;   // a prefab variant that combines differently: leave it vanilla
                mf.sharedMesh = sh.Full;
                if (priv.name.StartsWith("___MineRock5", StringComparison.Ordinal) || priv.name.EndsWith("(Clone)", StringComparison.Ordinal) || priv.name.Contains("Instance"))
                {
                    BytesFreed += (long)priv.vertexCount * 48 + (long)Tris(priv) * 12;
                    UnityEngine.Object.Destroy(priv);
                    PrivateFreed++;
                }
            }
            if (!s_engagedSaid) { s_engagedSaid = true; EasyBakeLog.Info($"[Rocks] intact rocks share one mesh per prefab (first: {prefab}); damaged rocks keep vanilla's own mesh"); }

            // 2. LOD1 from EBM's own simplifier
            if (!sh.Lod1Tried)
            {
                sh.Lod1Tried = true;
                // 1.2.206: EBM's own simplifier (EbmMeshSimplify, a copy of the team's Core one), so the rock LOD1 never needs Core
                {
                    var b = sh.Full.bounds;
                    float cell = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) / Detail;
                    sh.Lod1 = EbmMeshSimplify.Simplify(sh.Full, cell, out int _, out int after);
                    if (sh.Lod1 != null) { sh.Lod1.name = "EBM intact rock LOD1 " + prefab; sh.TrisLod1 = after; sh.Lod1.UploadMeshData(true); }
                    EasyBakeLog.Debug($"[Rocks] LOD1 for {prefab}: {(sh.Lod1 != null ? $"{sh.TrisLod1} tris of {sh.TrisFull} ({100f * sh.TrisLod1 / Mathf.Max(1, sh.TrisFull):0}%), cell {cell:0.00} m" : "simplifier returned nothing: no LOD1")}");
                }
            }
            if (sh.Lod1 == null) return;
            var lg = rock.GetComponentInChildren<LODGroup>();
            if (lg == null) return;

            if (state == null)
            {
                state = rock.gameObject.AddComponent<RockState>();
                state.Group = lg; state.Original = lg.GetLODs(); state.Prefab = prefab;
                var go = new GameObject("EBM_IntactRockLod1") { layer = mr.gameObject.layer };
                go.transform.SetParent(mf.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = sh.Lod1;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
                r.shadowCastingMode = mr.shadowCastingMode; r.receiveShadows = mr.receiveShadows;
                r.lightProbeUsage = mr.lightProbeUsage; r.reflectionProbeUsage = mr.reflectionProbeUsage;
                state.Lod1 = go;
                s_states.Add(state);
            }
            ApplyLods(state, lg);
        }

        // [LOD0 = vanilla's current LOD0 renderers, down to Lod1Height] [LOD1 = ours, down to vanilla's own cull height]
        private static void ApplyLods(RockState state, LODGroup lg)
        {
            var now = lg.GetLODs();
            if (now.Length == 0 || state.Original == null || state.Original.Length == 0) return;
            var lod0Renderers = now[0].renderers;   // vanilla's UpdateMesh just set these
            float cull = state.Original[0].screenRelativeTransitionHeight;
            float h0 = Mathf.Max(Lod1Height, cull + 0.01f);
            var r1 = state.Lod1.GetComponent<MeshRenderer>();
            lg.SetLODs(new[] { new LOD(h0, lod0Renderers), new LOD(cull, new Renderer[] { r1 }) });
        }

        private static void Restore(RockState state)
        {
            try
            {
                if (state.Group != null && state.Original != null && state.Original.Length > 0)
                {
                    var now = state.Group.GetLODs();
                    var orig = (LOD[])state.Original.Clone();
                    if (now.Length > 0) orig[0].renderers = now[0].renderers;   // keep vanilla's current LOD0 (its own mesh)
                    state.Group.SetLODs(orig);
                }
                if (state.Lod1 != null) UnityEngine.Object.Destroy(state.Lod1);
            }
            finally { s_states.Remove(state); UnityEngine.Object.Destroy(state); }
        }

        /// <summary>The key was turned off: every rock goes back to vanilla on its next UpdateMesh; the LODs come back now.</summary>
        internal static void RestoreAll()
        {
            foreach (var s in new List<RockState>(s_states)) if (s != null) Restore(s);
        }

        private static bool Intact(MineRock5 rock)
        {
            if (f_hitAreas == null || f_health == null || f_areaHealth == null) return false;
            float full = (float)f_health.GetValue(rock);
            if (!(f_hitAreas.GetValue(rock) is System.Collections.IList areas) || areas.Count == 0) return false;
            foreach (var a in areas) if ((float)f_areaHealth.GetValue(a) < full) return false;
            return true;
        }

        private static int Tris(Mesh m)
        {
            int t = 0;
            for (int i = 0; i < m.subMeshCount; i++) t += (int)(m.GetIndexCount(i) / 3);
            return t;
        }

        /// <summary>The "[Rocks] bake" line for ebm_rocks.</summary>
        internal static string Summary()
        {
            int lod1 = 0; foreach (var s in s_states) if (s != null && s.Lod1 != null) lod1++;
            var parts = new List<string>();
            foreach (var kv in s_shared) parts.Add($"{kv.Key} {kv.Value.TrisFull}{(kv.Value.Lod1 != null ? "->" + kv.Value.TrisLod1 : "")}");
            return $"[Rocks] bake {(Enabled ? "ON" : "off")}: {s_shared.Count} shared prefab mesh(es) [{string.Join(", ", parts.ToArray())}], {lod1} rock(s) with our LOD1, {PrivateFreed} private mesh(es) freed (~{BytesFreed / 1048576.0:0.0} MB); LOD1 below screen {Lod1Height:0.00}, detail {Detail} cells";
        }
    }
}
