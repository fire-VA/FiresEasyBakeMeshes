using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // What a piece carries on its ZDO besides its place in the world.
    //
    // Vanilla applies per-piece field overrides in ZNetView.Awake: LoadFields reads "HasFields", then for every
    // MonoBehaviour ON THE OBJECT whose "HasFields<Type>" flag is set it writes each "<Type>.<field>" value it finds
    // onto that component. Most of those edits survive skipping untouched - a piece that was never created cannot fall,
    // wear or take damage, and the ZDO keeps the values for whenever it is created. What does NOT survive is an edit a
    // player can see or touch on the piece itself: a renamed piece, its hover text, a swapped model or effect. Those
    // keep their piece live; the rest are left to the bake, because tools write edit markers across a whole build and
    // treating every one of them as a reason to keep 70,000 objects alive is what skipping exists to avoid.
    internal static class PieceData
    {
        private const int SamplesPerPrefab = 20;
        private const int ReportedKeys = 6;
        private const int ExamplePrefabs = 3;

        private static readonly int CustomFieldsKey = ZNetView.CustomFieldsStr.GetStableHashCode();
        private static readonly int RandomMaterialSeedKey = "RandMatSeed".GetStableHashCode();

        // Numbers and flags on a piece that is not there change nothing, but these turn it into a different piece.
        private static readonly HashSet<string> AlwaysLiveFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "Piece.m_comfort", "Piece.m_comfortGroup", "Piece.m_groundPiece", "Piece.m_groundOnly",
            "ZNetView.m_syncInitialScale",
        };

        private sealed class EditTarget
        {
            public int MarkerKey;
            public readonly Dictionary<int, string> LiveFields = new Dictionary<int, string>();
            public readonly Dictionary<int, string> BakeableFields = new Dictionary<int, string>();
        }

        private struct Verdict
        {
            public uint Revision;
            public bool StayLive;
        }

        private sealed class UnknownKey
        {
            public int Pieces;
            public readonly List<string> Prefabs = new List<string>(ExamplePrefabs);
        }

        private static readonly EditTarget[] NoTargets = new EditTarget[0];
        private static readonly Dictionary<int, EditTarget[]> s_targetsByPrefab = new Dictionary<int, EditTarget[]>();
        private static readonly Dictionary<Type, EditTarget> s_targetsByType = new Dictionary<Type, EditTarget>();
        private static readonly HashSet<int> s_keysOnPiece = new HashSet<int>();
        private static readonly Dictionary<string, int> s_liveEdits = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> s_bakedEdits = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<ZDOID, Verdict> s_verdicts = new Dictionary<ZDOID, Verdict>();

        private static HashSet<int> s_vanillaKeys;
        private static readonly Dictionary<int, int> s_sampledPerPrefab = new Dictionary<int, int>();
        private static readonly Dictionary<int, UnknownKey> s_unknownKeys = new Dictionary<int, UnknownKey>();
        private static int s_sampledPieces;
        private static int s_piecesWithUnknownKeys;

        // True only when an edit lands on this prefab and changes what the piece shows or offers, which no stand-in can.
        // The markers are read first because a tool writes them for components most prefabs do not have, and the watcher asks
        // this about every skipped piece every second; only a marker that matches a real component costs a read of the data,
        // and that answer is kept until the piece changes.
        public static bool MustStayLive(ZDO zdo, int prefabHash)
        {
            if (!zdo.GetBool(CustomFieldsKey)) return false;

            var targets = EditTargets(prefabHash);
            for (int i = 0; i < targets.Length; i++)
            {
                if (!zdo.GetBool(targets[i].MarkerKey)) continue;
                return EditedFieldsMatter(zdo, targets);
            }
            return false;
        }

        private static bool EditedFieldsMatter(ZDO zdo, EditTarget[] targets)
        {
            if (s_verdicts.TryGetValue(zdo.m_uid, out var known) && known.Revision == zdo.DataRevision) return known.StayLive;

            ReadKeys(zdo);
            bool stayLive = false;
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                if (!zdo.GetBool(target.MarkerKey)) continue;
                foreach (int key in s_keysOnPiece)
                {
                    if (target.LiveFields.TryGetValue(key, out string live)) { Count(s_liveEdits, live); stayLive = true; }
                    else if (target.BakeableFields.TryGetValue(key, out string baked)) Count(s_bakedEdits, baked);
                }
            }

            s_verdicts[zdo.m_uid] = new Verdict { Revision = zdo.DataRevision, StayLive = stayLive };
            return stayLive;
        }

        public static void Reset()
        {
            s_sampledPerPrefab.Clear();
            s_unknownKeys.Clear();
            s_liveEdits.Clear();
            s_bakedEdits.Clear();
            s_verdicts.Clear();
            s_sampledPieces = 0;
            s_piecesWithUnknownKeys = 0;
        }

        // Sampled per prefab: enough of each kind to see what it carries, without walking every piece in a base.
        public static void Sample(ZDO zdo, int prefabHash)
        {
            s_sampledPerPrefab.TryGetValue(prefabHash, out int sampled);
            if (sampled >= SamplesPerPrefab) return;
            s_sampledPerPrefab[prefabHash] = sampled + 1;
            s_sampledPieces++;

            ReadKeys(zdo);
            var targets = EditTargets(prefabHash);
            bool anyUnknown = false;
            foreach (int key in s_keysOnPiece)
            {
                if (VanillaKeys().Contains(key) || IsFieldEditKey(targets, key)) continue;
                anyUnknown = true;
                if (!s_unknownKeys.TryGetValue(key, out var unknown)) s_unknownKeys[key] = unknown = new UnknownKey();
                unknown.Pieces++;

                string prefabName = PrefabName(prefabHash);
                if (unknown.Prefabs.Count < ExamplePrefabs && !unknown.Prefabs.Contains(prefabName)) unknown.Prefabs.Add(prefabName);
            }
            if (anyUnknown) s_piecesWithUnknownKeys++;
        }

        public static string Report()
        {
            if (s_liveEdits.Count == 0 && s_bakedEdits.Count == 0 && s_piecesWithUnknownKeys == 0) return null;

            var line = new System.Text.StringBuilder("[Data] ");
            if (s_liveEdits.Count > 0) line.Append($"kept live by field edits: {Top(s_liveEdits)}. ");
            if (s_bakedEdits.Count > 0) line.Append($"field edits the bake keeps as they are: {Top(s_bakedEdits)}. ");
            if (s_piecesWithUnknownKeys > 0)
            {
                line.Append($"{s_piecesWithUnknownKeys} of {s_sampledPieces} sampled skipped pieces carry data the bake does not know:");
                foreach (var key in TopKeys())
                    line.Append($" key {key.Key} on {key.Value.Pieces} ({string.Join(", ", key.Value.Prefabs.ToArray())});");
            }
            return line.ToString().TrimEnd(';', ' ');
        }

        // The markers and values of a field edit are the bake's own business, not data it has never heard of.
        private static bool IsFieldEditKey(EditTarget[] targets, int key)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                if (key == target.MarkerKey || target.LiveFields.ContainsKey(key) || target.BakeableFields.ContainsKey(key)) return true;
            }
            return false;
        }

        private static void Count(Dictionary<string, int> tally, string field)
        {
            tally.TryGetValue(field, out int seen);
            tally[field] = seen + 1;
        }

        private static void ReadKeys(ZDO zdo)
        {
            ZDOExtraData.GetData(zdo.m_uid, out var floats, out var vectors, out var rotations, out var ints,
                out var longs, out var strings, out var byteArrays, out var connection);

            s_keysOnPiece.Clear();
            AddKeys(floats);
            AddKeys(vectors);
            AddKeys(rotations);
            AddKeys(ints);
            AddKeys(longs);
            AddKeys(strings);
            AddKeys(byteArrays);
        }

        private static void AddKeys<T>(List<KeyValuePair<int, T>> values)
        {
            for (int i = 0; i < values.Count; i++) s_keysOnPiece.Add(values[i].Key);
        }

        // Mirrors LoadFields' own walk: the MonoBehaviours of the prefab, and for each the public instance fields it
        // would read back out of the ZDO.
        private static EditTarget[] EditTargets(int prefabHash)
        {
            if (s_targetsByPrefab.TryGetValue(prefabHash, out var targets)) return targets;

            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            if (prefab == null) return NoTargets;

            var behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            var byType = new List<EditTarget>();
            var seen = new HashSet<Type>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;
                var type = behaviours[i].GetType();
                if (!seen.Add(type)) continue;
                byType.Add(TargetFor(type));
            }

            targets = byType.ToArray();
            s_targetsByPrefab[prefabHash] = targets;
            return targets;
        }

        // Text and prefab references are what a player reads or sees on the piece; numbers and flags are state the piece
        // would have applied to itself, and the ZDO still holds them for whenever it is created for real.
        private static EditTarget TargetFor(Type type)
        {
            if (s_targetsByType.TryGetValue(type, out var target)) return target;

            target = new EditTarget { MarkerKey = (ZNetView.CustomFieldsStr + type.Name).GetStableHashCode() };
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string name = type.Name + "." + field.Name;
                bool showsOnThePiece = field.FieldType == typeof(string)
                    || field.FieldType == typeof(GameObject)
                    || AlwaysLiveFields.Contains(name);
                if (showsOnThePiece) target.LiveFields[name.GetStableHashCode()] = name;
                else target.BakeableFields[name.GetStableHashCode()] = name;
            }

            s_targetsByType[type] = target;
            return target;
        }

        private static string Top(Dictionary<string, int> tally)
        {
            var ordered = new List<KeyValuePair<string, int>>(tally);
            ordered.Sort((left, right) => right.Value.CompareTo(left.Value));
            var named = new List<string>(ReportedKeys);
            for (int i = 0; i < ordered.Count && i < ReportedKeys; i++) named.Add(ordered[i].Key + " x" + ordered[i].Value);
            return string.Join(", ", named.ToArray());
        }

        private static List<KeyValuePair<int, UnknownKey>> TopKeys()
        {
            var ordered = new List<KeyValuePair<int, UnknownKey>>(s_unknownKeys);
            ordered.Sort((left, right) => right.Value.Pieces.CompareTo(left.Value.Pieces));
            if (ordered.Count > ReportedKeys) ordered.RemoveRange(ReportedKeys, ordered.Count - ReportedKeys);
            return ordered;
        }

        private static string PrefabName(int prefabHash)
        {
            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(prefabHash) : null;
            return prefab != null ? prefab.name : prefabHash.ToString();
        }

        // ZDOVars holds every key vanilla names, as hashes; RandomMaterialValues keeps its own outside that class, and
        // "HasFields" marks the overrides ZNetView applies.
        private static HashSet<int> VanillaKeys()
        {
            if (s_vanillaKeys != null) return s_vanillaKeys;

            s_vanillaKeys = new HashSet<int> { CustomFieldsKey, RandomMaterialSeedKey };
            foreach (var field in typeof(ZDOVars).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object value = field.GetValue(null);
                if (value is int hash) s_vanillaKeys.Add(hash);
                else if (value is KeyValuePair<int, int> pair) { s_vanillaKeys.Add(pair.Key); s_vanillaKeys.Add(pair.Value); }
                else if (value is List<int> hashes) s_vanillaKeys.UnionWith(hashes);
            }
            return s_vanillaKeys;
        }
    }
}
