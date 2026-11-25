using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// 1.2.286 [Optimize] IndirectStaticClusters (lead 11-11, Fire "get that done"; plan approved). Coverage reached only live ZNetView objects, so dungeon
    /// interiors (DungeonGenerator copies its rooms with their ZNetView children switched off) and most location geometry (no ZNetView) stayed thousands of
    /// Unity renderers: FDT's census put DG_SunkenCrypt's 7,390 in the "Location, no ZNetView" bucket. A static cluster = one dungeon Room, or one child tree of
    /// a Location with no ZNetView on it; a CoveredObject (Cluster = true, no per-prefab verdict) goes on its root and its normal per-renderer rules decide
    /// every MeshRenderer (procedural variant, LOD window, per-object looks); a renderer under an active ZNetView, an Animator or a moving Rigidbody is refused.
    /// Outside a dungeon its rooms are 5,000 m up, far past their LOD reach, so CoverageReach parks them like any far tree (slots released, still hidden);
    /// DungeonInteriorCull keeps holding what coverage refused, and waits two frames after listing so coverage sees the renderers enabled.
    /// Verify ([Optimize] StaticClustersVerify): for the first 50 clusters, 2 s after covering, every renderer that was enabled at attach must be exactly one of
    /// drawn by Unity or covered by a batch (no hole, no double draw). Census every 30 s.
    /// </summary>
    [HarmonyPatch]
    internal static class StaticClusters
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.IndirectStaticClusters != null && FiresEasyBakeMeshesPlugin.IndirectStaticClusters.Value && IndirectBatches.Active;
        private static bool VerifyOn => FiresEasyBakeMeshesPlugin.StaticClustersVerify != null && FiresEasyBakeMeshesPlugin.StaticClustersVerify.Value;

        private static readonly List<CoveredObject> s_clusters = new List<CoveredObject>();
        private static int c_rooms, c_locParts, c_verified, c_holes, c_doubles;
        private static float s_nextCensus;
        private static readonly List<MeshRenderer> s_scratch = new List<MeshRenderer>(256);

        private static readonly System.Type s_animatorType = System.Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule");
        // ---- the static test, per renderer, up to its cluster root
        internal static bool StaticPart(Transform t, Transform root)
        {
            for (var p = t; p != null && p != root; p = p.parent)
            {
                var z = p.GetComponent<ZNetView>();
                if (z != null && z.enabled && z.gameObject.activeInHierarchy) return false;
                if (s_animatorType != null && p.GetComponent(s_animatorType) != null) return false;   // Animator, looked up by name (AnimationModule is not referenced)
                var b = p.GetComponent<Rigidbody>();
                if (b != null && !b.isKinematic) return false;
            }
            return true;
        }

        private static bool HasActiveZNetView(Transform t)
        {
            var z = t.GetComponent<ZNetView>();
            return z != null && z.enabled && z.gameObject.activeInHierarchy;
        }

        // ---- dungeon rooms (called by DungeonInteriorCull when it lists a dungeon)
        internal static void CoverDungeon(DungeonGenerator gen)
        {
            if (!On || gen == null) return;
            var root = gen.transform;
            for (int c = 0; c < root.childCount; c++)
            {
                var room = root.GetChild(c);
                if (room.GetComponent<Room>() == null || HasActiveZNetView(room)) continue;
                if (Attach(room.gameObject)) c_rooms++;
            }
        }

        // ---- location parts: each child tree of a Location with no ZNetView on its root (the generator and networked props are their own objects)
        [HarmonyPatch(typeof(Location), "Awake"), HarmonyPostfix]
        private static void AfterLocationAwake(Location __instance)
        {
            using (EbmPatchMarkers.LocationAwake.Auto()) AfterLocationAwakeBody(__instance);   // 1.2.360: profiler marker only
        }
        private static void AfterLocationAwakeBody(Location __instance)
        {
            try
            {
                if (!On || __instance == null || ZNet.instance == null || ZNet.instance.IsDedicated()) return;
                var root = __instance.transform;
                for (int c = 0; c < root.childCount; c++)
                {
                    var part = root.GetChild(c);
                    if (HasActiveZNetView(part) || part.GetComponent<DungeonGenerator>() != null || part.GetComponentInChildren<DungeonGenerator>(true) != null) continue;
                    if (part.GetComponentInChildren<MeshRenderer>(false) == null) continue;
                    if (Attach(part.gameObject)) c_locParts++;
                }
            }
            catch (System.Exception e) { EasyBakeLog.Debug("[StaticClusters] location skipped: " + e.Message); }
        }

        private static bool Attach(GameObject go)
        {
            if (go.GetComponent<CoveredObject>() != null) return false;
            var co = go.AddComponent<CoveredObject>();
            co.Cluster = true;   // its Start covers it, through CoverageBatchedStart's budgeted pass when that key is on
            s_clusters.Add(co);
            if (VerifyOn && c_verified + s_pendingVerify.Count < 50)
            {
                var snap = new List<MeshRenderer>();
                go.GetComponentsInChildren(false, s_scratch);
                foreach (var r in s_scratch) if (r != null && r.enabled && !r.forceRenderingOff) snap.Add(r);
                s_pendingVerify.Add(new KeyValuePair<CoveredObject, List<MeshRenderer>>(co, snap)); s_verifyAt.Add(Time.unscaledTime + 2f);
            }
            return true;
        }

        // ---- verify: covered XOR drawn by Unity, for every renderer enabled at attach
        private static readonly List<KeyValuePair<CoveredObject, List<MeshRenderer>>> s_pendingVerify = new List<KeyValuePair<CoveredObject, List<MeshRenderer>>>();
        private static readonly List<float> s_verifyAt = new List<float>();

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            for (int i = s_pendingVerify.Count - 1; i >= 0; i--)
            {
                if (now < s_verifyAt[i]) continue;
                var co = s_pendingVerify[i].Key; var snap = s_pendingVerify[i].Value;
                s_pendingVerify.RemoveAt(i); s_verifyAt.RemoveAt(i);
                if (co == null) continue;
                c_verified++;
                foreach (var r in snap)
                {
                    if (r == null || !r.gameObject.activeInHierarchy) continue;
                    bool unity = r.enabled && !r.forceRenderingOff;
                    bool covered = co.Covers(r);
                    bool heldByDungeon = !r.enabled && !covered && !CoveredObject.HiddenByCoverage(r);   // DungeonInteriorCull holding a refused part while outside
                    if (!unity && !covered && !heldByDungeon) { c_holes++; if (c_holes == 1) EasyBakeLog.Warn($"[StaticClusters] verify: {co.name}/{r.name} is neither drawn by Unity nor covered (a hole)"); }
                    if (unity && covered) { c_doubles++; if (c_doubles == 1) EasyBakeLog.Warn($"[StaticClusters] verify: {co.name}/{r.name} is drawn by Unity AND covered (drawn twice)"); }
                }
            }
            if (now < s_nextCensus) return;
            s_nextCensus = now + 30f;
            if (!(VerifyOn || EasyBakeLog.DebugOn()) || s_clusters.Count == 0) return;   // 1.2.296: Verify on = the verdict at Info
            int live = 0, covering = 0, parked = 0, inst = 0;
            for (int i = s_clusters.Count - 1; i >= 0; i--)
            {
                var co = s_clusters[i];
                if (co == null) { s_clusters.RemoveAt(i); continue; }
                live++;
                if (co.IsCovering) covering++;
                if (co.IsParked) parked++;
                inst += co.HeldInstances(out _);
            }
            EasyBakeLog.Say(VerifyOn, $"[StaticClusters] 30 s: {live} cluster(s) live ({c_rooms} dungeon room(s), {c_locParts} location part(s) attached so far), {covering} covering, {parked} parked (out of reach / outside), "
                + $"{inst} instance(s) drawn by the batches; refusals by reason: see ebm_coverage{(VerifyOn ? $"; verify {c_verified}/50 cluster(s): {c_holes} hole(s), {c_doubles} double draw(s)" : "")}");
        }

        internal static void Clear() { s_clusters.Clear(); s_pendingVerify.Clear(); s_verifyAt.Clear(); }
    }
}
