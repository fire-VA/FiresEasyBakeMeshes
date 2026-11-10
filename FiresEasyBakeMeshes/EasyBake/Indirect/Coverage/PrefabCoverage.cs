using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.21x step 2 ([Optimize] IndirectCoverageDisablePrefabs, default off; [lead] 11-08: compare none / per-instance / prefab).
    /// While coverage is on, the PREFAB parts coverage would draw anyway are switched off once (renderer.enabled = false), so every copy is
    /// born out of Unity's renderer scene. Method learned from VPO by ontrigger (MIT); our own code and our own rules:
    ///  - only what CoveredObject.TryCover would accept from the prefab alone: active parts, not runtime-driven, not a low twin, a mesh,
    ///    matching material count, every material with an indirect variant, inside a LOD level; never items (their prefabs are also the
    ///    source of hand / stand / rack copies) and never MineRock5 (its mesh is built per rock);
    ///  - a copy is never left with a part off unless coverage draws it: ZNetView.Awake hands the born-off parts to the copy's
    ///    CoveredObject (adopted on its first cover, everything after is the per-instance path) or switches them back on at once
    ///    (placement ghosts and other copies without a ZDO, coverage off, baked by EBM, refused);
    ///  - EBM's own prefab reads see these parts as shown (CoveredObject.HiddenByCoverage covers prefab parts too);
    ///  - coverage off, or the key off: every prefab part is switched back on (exactly the ones we switched off).
    /// Not closed from here (why the key is default off): another mod that copies a patched prefab without a ZNetView.Awake (an icon
    /// render, a preview) or reads a prefab renderer's enabled sees those parts off.
    /// </summary>
    internal static class PrefabCoverage
    {
        private static readonly string[] s_none = new string[0];
        private static readonly Dictionary<int, string[]> s_paths = new Dictionary<int, string[]>();   // prefab hash -> part paths switched off (empty = judged, none)
        private static readonly HashSet<Renderer> s_off = new HashSet<Renderer>();                     // the PREFAB renderers we switched off
        private static readonly HashSet<string> s_offKeys = new HashSet<string>();                     // "meshId|path" of those: finds them in clones made later
        private static readonly List<MeshRenderer> s_scratch = new List<MeshRenderer>(16);
        private static readonly List<Material> s_mats = new List<Material>(4);
        private static readonly HashSet<string> s_seenPaths = new HashSet<string>();
        private static readonly HashSet<string> s_dupPaths = new HashSet<string>();
        internal static int BornAdopted, BornRestored, ClonePartsRestored;

        internal static bool Wanted => FiresEasyBakeMeshesPlugin.IndirectCoverageDisablePrefabs != null && FiresEasyBakeMeshesPlugin.IndirectCoverageDisablePrefabs.Value;
        internal static bool Engaged => s_off.Count > 0;
        internal static bool IsOffOnPrefab(Renderer r) => s_off.Count > 0 && r != null && s_off.Contains(r);

        internal static string Status()
            => Wanted ? $"prefab mode ON: {s_off.Count} prefab part(s) off on {CountPatched()} prefab(s); copies adopted {BornAdopted}, switched back on {BornRestored}, clone parts restored {ClonePartsRestored}"
                      : (Engaged ? $"prefab mode OFF but {s_off.Count} part(s) still off (restore pending)" : "prefab mode off");

        private static int CountPatched() { int n = 0; foreach (var kv in s_paths) if (kv.Value.Length > 0) n++; return n; }

        /// <summary>Coverage is on (ActiveChanged, a new ZNetScene, the key turned on): judge every prefab not judged yet.</summary>
        internal static void Apply()
        {
            if (!Wanted || !IndirectBatches.Active || ZNetScene.instance == null) return;
            int parts0 = s_off.Count, judged = 0;
            var prefabs = ZNetScene.instance.m_prefabs;
            if (prefabs == null) return;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var p = prefabs[i];
                if (p == null) continue;
                int hash = p.name.GetStableHashCode();
                if (s_paths.ContainsKey(hash)) continue;
                try { Judge(hash, p); judged++; }
                catch (System.Exception e) { s_paths[hash] = s_none; EasyBakeLog.Debug($"[Coverage] prefab mode skipped {p.name}: {e.Message}"); }
            }
            if (s_off.Count != parts0 || judged > 0)
                EasyBakeLog.Info($"[Coverage] prefab mode: {s_off.Count - parts0} prefab part(s) switched off on {judged} newly judged prefab(s) (total {s_off.Count} on {CountPatched()}); copies are born out of Unity's culling");
        }

        /// <summary>Coverage off or the key off: switch back on exactly the prefab parts we switched off.</summary>
        internal static void RestoreAll(string why)
        {
            if (s_off.Count == 0 && s_paths.Count == 0) return;
            int n = 0;
            foreach (var r in s_off) if (r != null) { r.enabled = true; n++; }
            // prefabs another mod cloned from a patched one while we were on carry our parts too: back to vanilla before the keys go
            var prefabs = ZNetScene.instance != null ? ZNetScene.instance.m_prefabs : null;
            if (prefabs != null) for (int i = 0; i < prefabs.Count; i++) if (prefabs[i] != null) { try { RestoreCloneParts(prefabs[i]); } catch { } }
            s_off.Clear(); s_paths.Clear(); s_offKeys.Clear();
            if (n > 0) EasyBakeLog.Info($"[Coverage] prefab mode: {n} prefab part(s) switched back on ({why})");
        }

        private static void Judge(int hash, GameObject prefab)
        {
            var root = prefab.transform;
            prefab.GetComponentsInChildren(true, s_scratch);

            // A prefab cloned (by another mod) from one we patched carries our switched-off parts: put them back to vanilla first.
            if (s_offKeys.Count > 0)
                for (int i = 0; i < s_scratch.Count; i++)
                {
                    var r = s_scratch[i];
                    if (r == null || r.enabled || s_off.Contains(r)) continue;
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && s_offKeys.Contains(Key(mf.sharedMesh, CoverageRules.PathOf(root, r.transform)))) { r.enabled = true; ClonePartsRestored++; }
                }

            s_paths[hash] = s_none;
            if (prefab.GetComponent<ZNetView>() == null) return;
            var v = CoverageRules.For(hash, prefab);
            if (v.Refusal != null || v.Kind == CoverageRules.Kind.Item || v.Kind == CoverageRules.Kind.MineRock5 || v.Kind == CoverageRules.Kind.None) return;
            if (prefab.GetComponent<EasyBakeSuppressedVisuals>() != null) return;

            // Transform.Find needs unique paths: two parts at the same path are left alone.
            s_seenPaths.Clear(); s_dupPaths.Clear();
            for (int i = 0; i < s_scratch.Count; i++)
            {
                if (s_scratch[i] == null) continue;
                string p = CoverageRules.PathOf(root, s_scratch[i].transform);
                if (!s_seenPaths.Add(p)) s_dupPaths.Add(p);
            }

            List<string> paths = null;
            for (int i = 0; i < s_scratch.Count; i++)
            {
                var r = s_scratch[i];
                if (r == null || !r.enabled || r.forceRenderingOff || !ActiveUnder(root, r.transform)) continue;
                string path = CoverageRules.PathOf(root, r.transform);
                if (s_dupPaths.Contains(path) || v.ExcludedPaths.Contains(path) || CoverageRules.IsLowTwin(r)) continue;
                var mf = r.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                r.GetSharedMaterials(s_mats);
                if (s_mats.Count == 0 || s_mats.Count > mesh.subMeshCount) continue;
                bool ok = true;
                for (int m = 0; m < s_mats.Count && ok; m++) ok = s_mats[m] != null && IndirectBatches.Accepts(s_mats[m]);
                if (!ok || !CoverageRules.RangeFor(r, out _)) continue;
                r.enabled = false;
                s_off.Add(r);
                s_offKeys.Add(Key(mesh, path));
                (paths ?? (paths = new List<string>(4))).Add(path);
            }
            if (paths != null) s_paths[hash] = paths.ToArray();
        }

        private static string Key(Mesh mesh, string path) => mesh.GetInstanceID().ToString() + "|" + path;

        // active below the root (the root's own activeSelf is the prefab's storage state, not what a copy shows)
        private static bool ActiveUnder(Transform root, Transform t)
        {
            for (var x = t; x != null && x != root; x = x.parent) if (!x.gameObject.activeSelf) return false;
            return true;
        }

        /// <summary>
        /// ZNetView.Awake of a copy: the parts this copy was born with switched off (from its prefab). An unknown prefab hash with our parts in
        /// it (a clone made after Apply) is judged now, once.
        /// </summary>
        internal static bool TakeBornOff(ZNetView view, List<MeshRenderer> into)
        {
            into.Clear();
            if (s_off.Count == 0 || view == null) return false;
            var go = view.gameObject;
            var zdo = view.GetZDO();
            int hash = zdo != null ? zdo.GetPrefab() : Utils.GetPrefabName(go).GetStableHashCode();   // no ZDO: placement ghosts and other copies (rare)
            if (!s_paths.TryGetValue(hash, out var paths))
            {
                var proto = SafePrefab.Get(hash);
                if (proto != null) { try { Judge(hash, proto); } catch { s_paths[hash] = s_none; } }
                else s_paths[hash] = s_none;   // judged once: an unregistered copy is not looked at again
                RestoreCloneParts(go);   // this copy was made before its prefab was judged: whatever it carries of ours goes back to vanilla
                return false;
            }
            if (paths.Length == 0) return false;
            var root = go.transform;
            for (int i = 0; i < paths.Length; i++)
            {
                var t = paths[i] == "." ? root : root.Find(paths[i]);
                var r = t != null ? t.GetComponent<MeshRenderer>() : null;
                if (r != null && !r.enabled) into.Add(r);
            }
            return into.Count > 0;
        }

        private static void RestoreCloneParts(GameObject go)
        {
            if (s_offKeys.Count == 0) return;
            var root = go.transform;
            go.GetComponentsInChildren(true, s_scratch);
            for (int i = 0; i < s_scratch.Count; i++)
            {
                var r = s_scratch[i];
                if (r == null || r.enabled) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && s_offKeys.Contains(Key(mf.sharedMesh, CoverageRules.PathOf(root, r.transform)))) { r.enabled = true; ClonePartsRestored++; }
            }
        }

        /// <summary>A copy coverage will not draw: switch its born-off parts back on now (vanilla look).</summary>
        internal static void Restore(List<MeshRenderer> born)
        {
            for (int i = 0; i < born.Count; i++) if (born[i] != null && !born[i].enabled) { born[i].enabled = true; BornRestored++; }
            born.Clear();
        }
    }
}
