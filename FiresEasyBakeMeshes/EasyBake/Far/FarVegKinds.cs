using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    // [worldgen] 11-18, ported from FAT's FarForest.PickKinds (0.2.1412) for the EXACT replay (FarVegReplay), no FAT references.
    // The far KINDS: vanilla ZoneSystem.m_vegetation entries whose prefab is a tree / big rock (TreeBase, MineRock5, MineRock, Destructible) at
    // least MinHeight tall (renderer bounds, particles ignored), one kind per prefab (several entries add their weight and biomes), ranked by
    // coverage (per-zone count x average group size x biome count), the top MaxKinds kept.
    // Also the ENTRY SNAPSHOT the worker replays: every enabled m_vegetation entry whose prefab is a kind, all numbers copied on the main
    // thread (Unity object calls such as .name / GetComponent are main-thread only). Build() can be called again; it replaces everything whole.
    internal static class FarVegKinds
    {
        internal const int DefaultMaxKinds = 24;
        internal const float DefaultMinHeight = 4f;

        // One vegetation entry, copied from ZoneSystem.ZoneVegetation (only the fields PlaceVegetation reads). Never mutated after Build.
        internal sealed class Entry
        {
            public int Index;            // position in m_vegetation (vanilla's loop order)
            public int Kind;             // index into the kind list
            public int PrefabHash;       // m_prefab.name.GetStableHashCode() (vanilla's seed term)
            public string Name;          // m_name (alt biome blockVegetationNames)
            public string AltBiomeParent;
            public bool HasZNetView;
            public Heightmap.Biome Biome;
            public Heightmap.BiomeArea BiomeArea;
            public float Min, Max;
            public bool ForcePlacement;
            public float ScaleMin, ScaleMax, RandTilt, ChanceToUseGroundTilt;
            public bool BlockCheck, SnapToStaticSolid;
            public float MinAltitude, MaxAltitude, MinVegetation, MaxVegetation, MinOceanDepth, MaxOceanDepth;
            public float MinTilt, MaxTilt;
            public float TerrainDeltaRadius, MinTerrainDelta, MaxTerrainDelta;
            public float MinDistanceFromCenter, MaxDistanceFromCenter;
            public bool InForest; public float ForestMin, ForestMax;
            public bool SurroundCheckVegetation; public int SurroundCheckLayers; public float SurroundCheckDistance, SurroundBetterThanAverage;
            public bool SnapToWater; public float GroundOffset;
            // [worldgen] 1.2.449: Random words the prefab's LodFadeInOut.Awake draws when a CAMERA exists and is > 20 m away (LodFadeInOut.cs:15-26:
            // one Range per active LodFadeInOut with a LODGroup on the same object). 0 on a dedicated server (no camera). Main-thread count at Build.
            public int LodFadeWords;
            // 1.2.454: Awake words that do NOT depend on a camera, drawn inside the real Instantiate during generation (a ZDO exists then):
            // MineRock5.Awake :86 (1), Pickable.Awake :66 when m_respawnTimeMinutes > 0 (1), ResourceRoot.Awake :34 (1). Vine (Mistlands, 2+) not counted.
            public int FixedWords;
            // 1.2.451: the colliders vanilla's IsBlocked ray can hit (layers Default / static_solid / Default_small / piece), in the prefab ROOT's
            // local space (an instance places the root at TRS(p, rotation, uniform scale)); FootR = their XZ reach from the root, for a quick reject.
            public Shape[] Shapes = new Shape[0];
            public float FootR;
            public int GroupSizeMin, GroupSizeMax; public float GroupRadius;   // [visual] 1.2.451 build fix: this line had landed inside Shape below
        }

        // 1.2.451: one blocking collider. Type 0 sphere (A, R), 1 capsule segment (A..B, R), 2 box (centre A, half-axis vectors U, V, W).
        internal sealed class Shape
        {
            public byte Type; public Vector3 A, B, U, V, W; public float R;
            // 1.2.455: Type 3 = a non-convex MeshCollider's triangles (3 root-space vertices each); Type 4 = a convex MeshCollider's vertices (PhysX
            // uses their hull). The 454 leg showed the bounds-box stand-in over-blocking (Rock_4 blocked a spot vanilla placed a tree on).
            public Vector3[] Pts;
            public string Owner;   // the prefab, for the trace
        }

        private sealed class KindInfo { public GameObject Prefab; public string Name; public int Hash; public bool Distant; public float Height, Weight; public Heightmap.Biome Biome; public string What; }

        private static List<KindInfo> s_kinds = new List<KindInfo>();
        private static Dictionary<int, int> s_byHash = new Dictionary<int, int>();
        private static Entry[] s_entries = new Entry[0];

        internal static int Count => s_kinds.Count;
        internal static uint ListHash { get; private set; }
        internal static GameObject Prefab(int k) { var l = s_kinds; return k >= 0 && k < l.Count ? l[k].Prefab : null; }
        internal static bool Distant(int k) { var l = s_kinds; return k >= 0 && k < l.Count && l[k].Distant; }
        internal static string Name(int k) { var l = s_kinds; return k >= 0 && k < l.Count ? l[k].Name : "?"; }
        internal static int KindOfPrefabHash(int hash) => s_byHash.TryGetValue(hash, out int k) ? k : -1;
        internal static float Height(int k) { var l = s_kinds; return k >= 0 && k < l.Count ? l[k].Height : 0f; }
        // Worker-safe: the array is replaced whole by Build and never mutated.
        internal static Entry[] Entries => s_entries;

        // 1.2.465 ([visual] + [worldgen], lead 11-19 G5e): FAT's "Vegetation density scale" multiplies m_min / m_max by k inside its PlaceVegetation
        // prefix and restores them after, so the list read here holds the UNscaled counts (G5e trace C: Pinetree_01 count 60 = 60 attempts; the real
        // generation stopped at 42 = 60 x 0.7, and attempts a0..a41 hold exactly the zone's 32 real Pinetree_01). FatApi v4 FarVegetationCounts answers
        // the counts the GENERATING peer's PlaceVegetation sees. Set by FarVegSource.Pick before Build; null = raw counts (no FAT = vanilla; FAT v3 = 1.2.464).
        internal delegate bool FarCountsFn(ZoneSystem.ZoneVegetation v, out float min, out float max);
        internal static FarCountsFn SrcCounts;
        internal static string CountsSay { get; private set; } = "raw list counts";

        // Main thread, after ZoneSystem.Start.
        internal static bool Build() => Build(DefaultMaxKinds, DefaultMinHeight);

        internal static bool Build(int maxKinds, float minHeight)
        {
            var zs = ZoneSystem.instance;
            if (zs == null || zs.m_vegetation == null || zs.m_vegetation.Count == 0) return false;
            var kinds = new List<KindInfo>();
            int considered = 0, tooSmall = 0, notTree = 0;
            foreach (var v in zs.m_vegetation)
            {
                if (v == null || !v.m_enable || v.m_prefab == null) continue;
                considered++;
                // 1.2.448 ([visual], lead 11-18 446 kind list had rock4_coast / rock1_mountain…): FAR TREES ONLY. A kind is a TreeBase prefab, or an
                // uncuttable big plant (Destructible, not a rock: no MineRock / MineRock5, no "rock" in the name, no StaticRock material). Rocks are
                // EBM FarRocks' job; drawing them here doubled them and put rocks in the far-tree list.
                string what = v.m_prefab.GetComponent<TreeBase>() != null ? "tree"
                    : (v.m_prefab.GetComponent<MineRock5>() != null || v.m_prefab.GetComponent<MineRock>() != null) ? null
                    : (v.m_prefab.GetComponent<Destructible>() != null && !IsRock(v.m_prefab)) ? "big plant" : null;
                if (what == null) { notTree++; continue; }
                float h = PrefabHeight(v.m_prefab);
                if (h < minHeight) { tooSmall++; continue; }
                int hash = v.m_prefab.name.GetStableHashCode();
                float group = (v.m_groupSizeMin + v.m_groupSizeMax) * 0.5f;
                float count = v.m_max < 1f ? v.m_max : (v.m_min + v.m_max) * 0.5f;
                float w = count * group * Mathf.Max(1, CountBits((int)v.m_biome));
                var existing = kinds.Find(k => k.Hash == hash);
                if (existing != null) { existing.Weight += w; existing.Biome |= v.m_biome; continue; }   // one kind per prefab, several entries
                var nv = v.m_prefab.GetComponent<ZNetView>();
                kinds.Add(new KindInfo { Prefab = v.m_prefab, Name = v.m_prefab.name, Hash = hash, Distant = nv != null && nv.m_distant, Height = h, Weight = w, Biome = v.m_biome, What = what });
            }
            kinds.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            int dropped = Math.Max(0, kinds.Count - maxKinds);
            if (dropped > 0) kinds.RemoveRange(maxKinds, dropped);
            var byHash = new Dictionary<int, int>();
            for (int i = 0; i < kinds.Count; i++) byHash[kinds[i].Hash] = i;

            // Entry snapshot: every enabled entry whose prefab is a kind (all of that prefab's entries replay, in m_vegetation order).
            // 1.2.451: PLUS every enabled entry BEFORE the last kind entry whose prefab has a collider vanilla's IsBlocked ray hits (Kind = -1):
            // its instances block later candidates (ZoneSystem.cs:795 / :1929), so the replay places them (not drawn) when blocking is emulated.
            int lastKindEntry = -1;
            for (int i = 0; i < zs.m_vegetation.Count; i++) { var lv = zs.m_vegetation[i]; if (lv != null && lv.m_enable && lv.m_prefab != null && byHash.ContainsKey(lv.m_prefab.name.GetStableHashCode())) lastKindEntry = i; }
            int blockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece");   // ZoneSystem.cs:152
            bool hitTriggers = Physics.queriesHitTriggers;
            int blockers = 0, shapesTotal = 0, meshBoxes = 0;
            var entries = new List<Entry>();
            uint lh = 2166136261u;
            var counts = SrcCounts;
            int cScaled = 0, cUnanswered = 0, cAsked = 0; string cExample = null;
            for (int i = 0; i < zs.m_vegetation.Count; i++)
            {
                var v = zs.m_vegetation[i];
                if (v == null || !v.m_enable || v.m_prefab == null) continue;
                int hash = v.m_prefab.name.GetStableHashCode();
                var shapes = BlockShapes(v.m_prefab, blockMask, hitTriggers, ref meshBoxes, out float footR);
                if (!byHash.TryGetValue(hash, out int kind)) { if (i > lastKindEntry || shapes.Length == 0) continue; kind = -1; blockers++; }
                shapesTotal += shapes.Length;
                var e = new Entry
                {
                    Index = i, Kind = kind, PrefabHash = hash, Name = v.m_name, AltBiomeParent = v.AltBiomeParent,
                    HasZNetView = v.m_prefab.GetComponent<ZNetView>() != null,
                    LodFadeWords = LodFadeWordsOf(v.m_prefab),
                    FixedWords = FixedWordsOf(v.m_prefab),
                    Biome = v.m_biome, BiomeArea = v.m_biomeArea, Min = v.m_min, Max = v.m_max, ForcePlacement = v.m_forcePlacement,
                    ScaleMin = v.m_scaleMin, ScaleMax = v.m_scaleMax, RandTilt = v.m_randTilt, ChanceToUseGroundTilt = v.m_chanceToUseGroundTilt,
                    BlockCheck = v.m_blockCheck, SnapToStaticSolid = v.m_snapToStaticSolid,
                    MinAltitude = v.m_minAltitude, MaxAltitude = v.m_maxAltitude, MinVegetation = v.m_minVegetation, MaxVegetation = v.m_maxVegetation,
                    MinOceanDepth = v.m_minOceanDepth, MaxOceanDepth = v.m_maxOceanDepth, MinTilt = v.m_minTilt, MaxTilt = v.m_maxTilt,
                    TerrainDeltaRadius = v.m_terrainDeltaRadius, MinTerrainDelta = v.m_minTerrainDelta, MaxTerrainDelta = v.m_maxTerrainDelta,
                    MinDistanceFromCenter = v.m_minDistanceFromCenter, MaxDistanceFromCenter = v.m_maxDistanceFromCenter,
                    InForest = v.m_inForest, ForestMin = v.m_forestTresholdMin, ForestMax = v.m_forestTresholdMax,
                    SurroundCheckVegetation = v.m_surroundCheckVegetation, SurroundCheckLayers = v.m_surroundCheckLayers,
                    SurroundCheckDistance = v.m_surroundCheckDistance, SurroundBetterThanAverage = v.m_surroundBetterThanAverage,
                    SnapToWater = v.m_snapToWater, GroundOffset = v.m_groundOffset,
                    GroupSizeMin = v.m_groupSizeMin, GroupSizeMax = v.m_groupSizeMax, GroupRadius = v.m_groupRadius,
                    Shapes = shapes, FootR = footR,
                };
                if (counts != null)
                {
                    cAsked++;
                    if (counts(v, out float cMin, out float cMax))
                    {
                        if (cMin != e.Min || cMax != e.Max) { if (cExample == null) cExample = $"{v.m_prefab.name} {e.Min:0.##}..{e.Max:0.##} -> {cMin:0.##}..{cMax:0.##}"; cScaled++; }
                        e.Min = cMin; e.Max = cMax;   // hashed below: a cache built with other counts goes stale by itself
                    }
                    else cUnanswered++;
                }
                entries.Add(e);
                // ListHash: kind order + every rule number, so a mod that retunes or adds vegetation invalidates a cache.
                lh = Mix(lh, i); lh = Mix(lh, hash); lh = Mix(lh, kind); lh = Mix(lh, (int)e.Biome); lh = Mix(lh, (int)e.BiomeArea);
                foreach (float f in new[] { e.Min, e.Max, e.ScaleMin, e.ScaleMax, e.RandTilt, e.ChanceToUseGroundTilt, e.MinAltitude, e.MaxAltitude,
                                            e.MinVegetation, e.MaxVegetation, e.MinOceanDepth, e.MaxOceanDepth, e.MinTilt, e.MaxTilt, e.TerrainDeltaRadius,
                                            e.MinTerrainDelta, e.MaxTerrainDelta, e.MinDistanceFromCenter, e.MaxDistanceFromCenter, e.ForestMin, e.ForestMax,
                                            e.SurroundCheckDistance, e.SurroundBetterThanAverage, e.GroundOffset, e.GroupRadius })
                    lh = Mix(lh, BitConverter.ToInt32(BitConverter.GetBytes(f), 0));
                lh = Mix(lh, (e.ForcePlacement ? 1 : 0) | (e.BlockCheck ? 2 : 0) | (e.SnapToStaticSolid ? 4 : 0) | (e.InForest ? 8 : 0) |
                             (e.SurroundCheckVegetation ? 16 : 0) | (e.SnapToWater ? 32 : 0) | (e.HasZNetView ? 64 : 0));
                lh = Mix(lh, e.SurroundCheckLayers); lh = Mix(lh, e.GroupSizeMin); lh = Mix(lh, e.GroupSizeMax);
                lh = Mix(lh, (e.Name ?? "").GetStableHashCode()); lh = Mix(lh, (e.AltBiomeParent ?? "").GetStableHashCode());
            }

            s_kinds = kinds;
            s_byHash = byHash;
            s_entries = entries.ToArray();
            ListHash = lh;
            EasyBakeLog.Info($"[FarVeg] {kinds.Count} far kind(s), {s_entries.Length - blockers} vegetation entr(ies) replayed + {blockers} blocker entr(ies) ({shapesTotal} block collider(s), {meshBoxes} mesh collider(s) as their bounds box; triggers {(hitTriggers ? "counted" : "ignored")}), from {considered} enabled entries " +
                             $"({notTree} not a tree, {tooSmall} under {minHeight:0.#} m, {dropped} over {maxKinds}); list hash {ListHash:X8}.");
            CountsSay = counts == null ? "raw list counts" : $"FAT's FarVegetationCounts: {cScaled} of {cAsked} entr(ies) scaled{(cExample != null ? " (e.g. " + cExample + ")" : "")}{(cUnanswered > 0 ? $", {cUnanswered} unanswered (raw counts kept)" : "")}";
            EasyBakeLog.Info("[FarVeg] entry counts: " + CountsSay + ".");
            EasyBakeLog.Info("[FarVeg] far tree kinds kept (trees only, no rocks): " + string.Join(", ", kinds.ConvertAll(k => $"{k.Name} ({k.What})")));   // 1.2.448 (lead)
            EasyBakeLog.Debug("[FarVeg] kinds: " + string.Join(", ", kinds.ConvertAll(k => $"{k.Name} ({k.What}, {k.Height:0.#} m, {k.Biome}, w {k.Weight:0.#}{(k.Distant ? ", distant" : "")})")));
            return true;
        }

        // 1.2.448: a rock by name or by material (vanilla rocks use Custom/StaticRock). Main thread (Unity object calls).
        private static bool IsRock(GameObject prefab)
        {
            if (prefab.name.IndexOf("rock", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (prefab.name.IndexOf("bush", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (prefab.name.IndexOf("stump", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // 1.2.450 (lead, 449 log: StumpHole / StumpLog / StumpHut registered as far kinds): stumps are not far trees   // 1.2.449 (lead: Bush02_en, a 6 m bush, is not a far TREE); "not a far tree" either way
            foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.shader != null && m.shader.name.IndexOf("StaticRock", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static uint Mix(uint h, int v) { unchecked { return (h ^ (uint)v) * 16777619u; } }
        private static int CountBits(int v) { int n = 0; while (v != 0) { n += v & 1; v >>= 1; } return n; }

        // Render height (renderer bounds, particles ignored), as FAT's FarTreePlacement.PrefabHeight.
        // 1.2.449: active LodFadeInOut components that have a LODGroup on the same object (Awake runs only on objects active in the hierarchy).
        // 1.2.451: the prefab's colliders on the block layers, enabled and active in the prefab, as shapes in the root's local space
        // (root.worldToLocal * collider.localToWorld). A MeshCollider becomes its mesh bounds box (an over-estimate; counted in the Build line).
        private static Shape[] BlockShapes(GameObject prefab, int mask, bool hitTriggers, ref int meshBoxes, out float footR)
        {
            footR = 0f;
            var list = new List<Shape>();
            var root = prefab.transform;
            Matrix4x4 toRoot = root.worldToLocalMatrix;
            foreach (var col in prefab.GetComponentsInChildren<Collider>(true))
            {
                if (col == null || !col.enabled || (mask & (1 << col.gameObject.layer)) == 0) continue;
                if (col.isTrigger && !hitTriggers) continue;
                bool active = true;
                for (var t = col.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) { active = false; break; }
                if (!active) continue;
                Matrix4x4 m = toRoot * col.transform.localToWorldMatrix;
                float sx = m.GetColumn(0).magnitude, sy = m.GetColumn(1).magnitude, sz = m.GetColumn(2).magnitude;
                Shape s = null;
                switch (col)
                {
                    case SphereCollider sc:
                        s = new Shape { Type = 0, A = m.MultiplyPoint3x4(sc.center), R = sc.radius * Mathf.Max(sx, Mathf.Max(sy, sz)) };
                        break;
                    case CapsuleCollider cc:
                    {
                        Vector3 dir = cc.direction == 0 ? Vector3.right : cc.direction == 1 ? Vector3.up : Vector3.forward;
                        float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
                        float rs = cc.direction == 0 ? Mathf.Max(sy, sz) : cc.direction == 1 ? Mathf.Max(sx, sz) : Mathf.Max(sx, sy);
                        s = new Shape { Type = 1, A = m.MultiplyPoint3x4(cc.center - dir * half), B = m.MultiplyPoint3x4(cc.center + dir * half), R = cc.radius * rs };
                        break;
                    }
                    case BoxCollider bc:
                        s = new Shape { Type = 2, A = m.MultiplyPoint3x4(bc.center), U = m.MultiplyVector(new Vector3(bc.size.x * 0.5f, 0, 0)),
                                        V = m.MultiplyVector(new Vector3(0, bc.size.y * 0.5f, 0)), W = m.MultiplyVector(new Vector3(0, 0, bc.size.z * 0.5f)) };
                        break;
                    case MeshCollider mc when mc.sharedMesh != null && mc.sharedMesh.isReadable:
                    {
                        // 1.2.455: the real mesh (the ray PhysX casts hits these triangles, or their hull when convex)
                        var verts = mc.sharedMesh.vertices;
                        if (mc.convex)
                        {
                            var pts = new Vector3[verts.Length];
                            for (int vi = 0; vi < verts.Length; vi++) pts[vi] = m.MultiplyPoint3x4(verts[vi]);
                            s = new Shape { Type = 4, Pts = pts };
                        }
                        else
                        {
                            var tris = mc.sharedMesh.triangles;
                            var pts = new Vector3[tris.Length];
                            for (int ti = 0; ti < tris.Length; ti++) pts[ti] = m.MultiplyPoint3x4(verts[tris[ti]]);
                            s = new Shape { Type = 3, Pts = pts };
                        }
                        break;
                    }
                    case MeshCollider mc when mc.sharedMesh != null:
                    {
                        var b = mc.sharedMesh.bounds;   // not readable on the CPU: the bounds box (an over-estimate, counted)
                        s = new Shape { Type = 2, A = m.MultiplyPoint3x4(b.center), U = m.MultiplyVector(new Vector3(b.extents.x, 0, 0)),
                                        V = m.MultiplyVector(new Vector3(0, b.extents.y, 0)), W = m.MultiplyVector(new Vector3(0, 0, b.extents.z)) };
                        meshBoxes++;
                        break;
                    }
                }
                if (s == null) continue;
                s.Owner = prefab.name;
                list.Add(s);
                // XZ reach from the root (any rotation): centre distance + extent
                float ext; Vector3 c;
                if (s.Pts != null) { ext = 0f; c = Vector3.zero; foreach (var pt in s.Pts) ext = Mathf.Max(ext, pt.magnitude); }   // 1.2.455: mesh reach = farthest vertex from the root
                else { ext = s.Type == 2 ? s.U.magnitude + s.V.magnitude + s.W.magnitude : s.R + (s.Type == 1 ? (s.B - s.A).magnitude * 0.5f : 0f); c = s.Type == 1 ? (s.A + s.B) * 0.5f : s.A; }
                footR = Mathf.Max(footR, c.magnitude + ext);
            }
            return list.ToArray();
        }

        private static bool ActiveInPrefab(Component c)
        {
            for (var t = c.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }

        // 1.2.454: see Entry.FixedWords.
        private static int FixedWordsOf(GameObject prefab)
        {
            int n = 0;
            foreach (var c in prefab.GetComponentsInChildren<MineRock5>(true)) if (c != null && ActiveInPrefab(c)) n++;
            foreach (var c in prefab.GetComponentsInChildren<Pickable>(true)) if (c != null && ActiveInPrefab(c) && c.m_respawnTimeMinutes > 0f) n++;
            foreach (var c in prefab.GetComponentsInChildren<ResourceRoot>(true)) if (c != null && ActiveInPrefab(c)) n++;
            return n;
        }

        private static int LodFadeWordsOf(GameObject prefab)
        {
            int n = 0;
            foreach (var c in prefab.GetComponentsInChildren<LodFadeInOut>(true))
            {
                if (c == null || c.GetComponent<LODGroup>() == null) continue;
                bool active = true;
                for (var t = c.transform; t != null; t = t.parent) if (!t.gameObject.activeSelf) { active = false; break; }
                if (active) n++;
            }
            return n;
        }

        internal static float PrefabHeight(GameObject prefab)
        {
            float low = float.MaxValue, high = float.MinValue;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer) continue;
                var bounds = renderer.bounds;
                low = Mathf.Min(low, bounds.min.y);
                high = Mathf.Max(high, bounds.max.y);
            }
            return high > low ? high - low : 0f;
        }
    }
}
