using System.Collections.Generic;
using System.Text;
using FiresCore.Pieces;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// Trees, shrubs, bushes and rocks are the largest bucket of created objects that the bake never looks at, because
    /// every entry point into the bake is typed on WearNTear. This reports how much of that bucket the existing
    /// instancing path could actually draw, per prefab, before any of it is wired in.
    /// </summary>
    internal static class VegetationInstancing
    {
        private const int TopPrefabsShown = 12;

        // MUST be default(WearLook). Vegetation has no wear models, so WearLooks.IsShown answers node.activeSelf
        // whatever look it is handed - but SkipEligibility.ZdoMatches compares the live look against the recorded one
        // with WearLooks.Resolve(prefab, zdo), and for a prefab with no WearNTear that returns AsShipped(null) =
        // default(WearLook), Bits 0. Recording anything else here makes that comparison fail for every vegetation
        // object, so the whole feature would refuse silently. This value also keys the instance definition and the
        // stand-in collider templates, so all four agree on one InstanceKey per object.
        internal static readonly WearLook AsShipped = default(WearLook);

        internal enum VegetationKind
        {
            None,
            Tree,
            Destructible,
            FelledLog,
            MineRock,
        }

        private static readonly Dictionary<int, VegetationKind> _kindByPrefab = new Dictionary<int, VegetationKind>();

        internal static void ClearPrefabCache() => _kindByPrefab.Clear();

        internal static VegetationKind KindOf(GameObject go)
        {
            if (go == null) return VegetationKind.None;
            if (go.GetComponent<Pickable>() != null) return VegetationKind.None;
            if (go.GetComponent<TreeBase>() != null) return VegetationKind.Tree;
            if (go.GetComponent<TreeLog>() != null) return VegetationKind.FelledLog;
            if (go.GetComponent<MineRock5>() != null || go.GetComponent<MineRock>() != null) return VegetationKind.MineRock;
            if (go.GetComponent<Destructible>() != null) return VegetationKind.Destructible;
            return VegetationKind.None;
        }

        // The skip path decides before the object exists, so it has only the ZDO's prefab hash to go on.
        internal static VegetationKind KindOfPrefab(int prefabHash)
        {
            if (_kindByPrefab.TryGetValue(prefabHash, out var cached)) return cached;

            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            var kind = KindOf(prefab);

            _kindByPrefab[prefabHash] = kind;
            return kind;
        }

        private sealed class PrefabTally
        {
            public string Name;
            public VegetationKind Kind;
            public int Instances;
            public bool Instanceable;
            public string RejectReason;
            public int VisibleRenderers;
            public int MostInAnyZone;
            public string SkipBlocker;
        }

        internal static string Audit()
        {
            var scene = ZNetScene.instance;
            var instances = ZoneTracker.SceneInstances();
            if (scene == null || instances == null) return "[Vegetation] No world is loaded.";

            var tallies = new Dictionary<int, PrefabTally>();
            var perZone = new Dictionary<int, Dictionary<Vector2s, int>>();

            foreach (var entry in instances)
            {
                if (entry.Value == null || entry.Key == null) continue;
                var kind = KindOf(entry.Value.gameObject);
                if (kind == VegetationKind.None) continue;

                int prefabHash = entry.Key.GetPrefab();
                if (!tallies.TryGetValue(prefabHash, out var tally))
                {
                    var prefab = scene.GetPrefab(prefabHash);
                    tally = new PrefabTally
                    {
                        Name = prefab != null ? prefab.name : entry.Value.gameObject.name,
                        Kind = kind,
                    };
                    tally.Instanceable = CanInstance(prefab, out string reject, out int visible);
                    tally.RejectReason = reject;
                    tally.VisibleRenderers = visible;
                    var eligibility = SkipEligibility.Get(prefabHash);
                    tally.SkipBlocker = eligibility.Skippable ? null : eligibility.Blocker;
                    tallies.Add(prefabHash, tally);
                    perZone.Add(prefabHash, new Dictionary<Vector2s, int>());
                }
                tally.Instances++;

                var zone = ZoneSystem.GetZone(entry.Key.GetPosition());
                var zones = perZone[prefabHash];
                zones.TryGetValue(zone, out int inZone);
                zones[zone] = inZone + 1;
            }

            foreach (var pair in tallies)
            {
                int most = 0;
                foreach (int inZone in perZone[pair.Key].Values)
                    if (inZone > most) most = inZone;
                pair.Value.MostInAnyZone = most;
            }

            return Report(tallies);
        }

        private static bool CanInstance(GameObject prefab, out string rejectReason, out int visibleRenderers)
        {
            rejectReason = null;
            visibleRenderers = 0;
            if (prefab == null) { rejectReason = "prefab not in ZNetScene"; return false; }

            visibleRenderers = CountVisibleRenderers(prefab);
            return InstanceDefinition.TryBuild(prefab, AsShipped, out _, out rejectReason);
        }

        internal static bool IsSkipCandidate(VegetationKind kind)
        {
            return kind == VegetationKind.Tree || kind == VegetationKind.Destructible;
        }

        // TreeBase.Awake and Destructible.RPC_Damage both treat health <= 0 as already dead and destroy the object,
        // so a corpse must never be stood in for.
        private static bool IsAlive(ZDO zdo, GameObject go)
        {
            float shipped = ShippedHealth(go);
            return zdo.GetFloat(ZDOVars.s_health, shipped) > 0f;
        }

        private static float ShippedHealth(GameObject go)
        {
            var tree = go.GetComponent<TreeBase>();
            if (tree != null) return tree.m_health;
            var destructible = go.GetComponent<Destructible>();
            return destructible != null ? destructible.m_health : 1f;
        }

        /// <summary>
        /// Builds the per-zone instanced groups for this zone's vegetation and records a PieceTransform for each
        /// member, which is what skipping and stand-in colliders key on. Returns the views whose prefab is now
        /// drawn by a group, so the caller can suppress their own renderers.
        /// </summary>
        internal static HashSet<ZNetView> BuildInstanceGroups(
            HashSet<ZNetView> vegetation,
            List<ZoneInstanceGroup> into,
            Dictionary<MeshBaker.PieceIdentity, MeshBaker.PieceTransform> transforms)
        {
            var instanced = new HashSet<ZNetView>();
            if (vegetation == null || vegetation.Count == 0) return instanced;
            if (!FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value) return instanced;
            if (!FiresEasyBakeMeshesPlugin.SkipVegetation.Value) return instanced;

            var candidatesByKey = new Dictionary<InstanceKey, List<ZNetView>>();
            foreach (var view in vegetation)
            {
                if (view == null || view.gameObject == null) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (!IsSkipCandidate(KindOf(view.gameObject))) continue;
                if (!IsAlive(zdo, view.gameObject)) continue;
                if (PieceData.MustStayLive(zdo, zdo.GetPrefab())) continue;

                var key = new InstanceKey(zdo.GetPrefab(), AsShipped);
                if (!InstanceDefinitionCache.TryGet(key, out _)) continue;

                if (!candidatesByKey.TryGetValue(key, out var list))
                {
                    list = new List<ZNetView>();
                    candidatesByKey.Add(key, list);
                }
                list.Add(view);
            }

            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;
            foreach (var entry in candidatesByKey)
            {
                if (entry.Value.Count < minInstances) continue;
                if (!InstanceDefinitionCache.TryGet(entry.Key, out var definition)) continue;

                var group = new ZoneInstanceGroup { PrefabHash = entry.Key.PrefabHash, Look = entry.Key.Look, Definition = definition };
                for (int i = 0; i < entry.Value.Count; i++)
                {
                    var view = entry.Value[i];
                    var identity = MeshBaker.PieceIdentity.From(view.gameObject, view.transform.position);
                    group.Add(view.transform.localToWorldMatrix, identity);
                    transforms[identity] = MeshBaker.PieceTransform.From(view.transform, AsShipped);
                    instanced.Add(view);
                }
                group.RecomputeBounds();
                into.Add(group);
            }
            return instanced;
        }

        private static int CountVisibleRenderers(GameObject prefab)
        {
            var renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            int visible = 0;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null && renderers[i].enabled) visible++;
            return visible;
        }

        private static string Report(Dictionary<int, PrefabTally> tallies)
        {
            int minInstances = FiresEasyBakeMeshesPlugin.BatchingMinInstancesPerPrefab.Value;

            var ordered = new List<PrefabTally>(tallies.Values);
            ordered.Sort((a, b) => b.Instances.CompareTo(a.Instances));

            int total = 0, instanceable = 0, clearsMinimum = 0;
            var byKind = new Dictionary<VegetationKind, int>();
            foreach (var tally in ordered)
            {
                total += tally.Instances;
                byKind.TryGetValue(tally.Kind, out int kindTotal);
                byKind[tally.Kind] = kindTotal + tally.Instances;
                if (!tally.Instanceable) continue;
                instanceable += tally.Instances;
                if (tally.MostInAnyZone >= minInstances) clearsMinimum += tally.Instances;
            }

            var report = new StringBuilder();
            report.Append($"[Vegetation] {total} live trees, shrubs, bushes and rocks across {ordered.Count} prefab(s). ");
            report.Append($"The instancing path could draw {instanceable} of them ({Percent(instanceable, total)}%); ");
            report.Append($"{clearsMinimum} ({Percent(clearsMinimum, total)}%) also reach MinInstancesPerPrefab = {minInstances} in at least one zone.");

            report.Append("\n  By kind: ");
            bool first = true;
            foreach (var kind in byKind)
            {
                report.Append(first ? "" : ", ").Append(kind.Key).Append(' ').Append(kind.Value);
                first = false;
            }

            report.Append($"\n  Top {TopPrefabsShown} prefabs:");
            for (int i = 0; i < ordered.Count && i < TopPrefabsShown; i++)
            {
                var tally = ordered[i];
                string verdict = tally.Instanceable
                    ? $"INSTANCEABLE, {tally.VisibleRenderers} renderer(s), most in one zone {tally.MostInAnyZone}"
                    : $"refused: {tally.RejectReason}";
                report.Append($"\n    {tally.Instances,6}  {tally.Name} [{tally.Kind}] - {verdict}");
            }

            var refusals = new Dictionary<string, int>();
            foreach (var tally in ordered)
            {
                if (tally.Instanceable) continue;
                refusals.TryGetValue(tally.RejectReason ?? "unknown", out int count);
                refusals[tally.RejectReason ?? "unknown"] = count + tally.Instances;
            }
            var skipBlockers = new Dictionary<string, int>();
            foreach (var tally in ordered)
            {
                if (tally.SkipBlocker == null) continue;
                skipBlockers.TryGetValue(tally.SkipBlocker, out int count);
                skipBlockers[tally.SkipBlocker] = count + tally.Instances;
            }
            if (skipBlockers.Count > 0)
            {
                report.Append("\n  What SkipEligibility refuses them for (this is what has to be widened to skip them):");
                var orderedBlockers = new List<KeyValuePair<string, int>>(skipBlockers);
                orderedBlockers.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var blocker in orderedBlockers)
                    report.Append($"\n    {blocker.Value,6}  {blocker.Key}");
            }

            if (refusals.Count > 0)
            {
                report.Append("\n  Refusals, by objects lost:");
                var orderedRefusals = new List<KeyValuePair<string, int>>(refusals);
                orderedRefusals.Sort((a, b) => b.Value.CompareTo(a.Value));
                foreach (var refusal in orderedRefusals)
                    report.Append($"\n    {refusal.Value,6}  {refusal.Key}");
            }

            return report.ToString();
        }

        private static int Percent(int part, int whole) => whole <= 0 ? 0 : Mathf.RoundToInt(100f * part / whole);
    }
}
