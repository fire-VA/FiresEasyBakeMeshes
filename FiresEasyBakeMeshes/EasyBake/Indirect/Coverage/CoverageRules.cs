using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.20x (DESIGN_IndirectEBM §4b Step 3): which LIVE objects in loaded zones the coverage hands to EBM's indirect batches.
    /// Trees and Destructibles that SkipVegetation leaves uncreated are already EBM ZoneInstanceGroups and never reach this; this is the
    /// remainder that stays a GameObject: Pickables (not items), MineRock5 / MineRock, and the trees / Destructibles SkipEligibility
    /// refused. 1.2.209 (VPO parity, Fire): also the LIVE pieces EBM's bake leaves as objects (chests, doors, item stands, signs, beds,
    /// stations, anything still waiting for its zone's bake) and dropped items lying still on the ground. Pieces a bake suppresses are
    /// EBM's own draw and are skipped (EasyBakeSuppressedVisuals). Items are covered per live dropped object only (never the prefab, so the
    /// copies stands, hands and racks make are untouched) and only while their Rigidbody sleeps. Never anything else that moves under
    /// physics (a non-kinematic Rigidbody: ships, carts, felled logs).
    /// </summary>
    internal static class CoverageRules
    {
        internal enum Kind { None, Pickable, Tree, Destructible, MineRock5, MineRock, Piece, Item, LocationProp }

        // 1.2.216 ([ghost], [lead]: at the dvergr town 2,474 vanilla renderers stay visible + 1,184 shadow casters, mostly LOCATION props:
        // dvergrprops_*, goblin_roof / fence / pole, Ashlands pillars, shipwreck parts, statues: network objects with no Piece). With
        // [Optimize] IndirectCoverageLocationProps a ZNetView object with none of coverage's other kinds is covered as a LocationProp only when
        // nothing can change its look at runtime: no Character / AI / Animator (anywhere under it), no Light / LightFlicker / Fireplace
        // (lanterns and torches switch their own parts), no Door / Container / TeleportWorld / Ship / Vagon / ItemStand / spawner, and no
        // non-kinematic Rigidbody. WearNTear props keep the piece rules (snow overlays / fragments excluded, wear states followed).
        internal static string WhyNotStaticProp(GameObject go)
        {
            // the zone root (terrain heightmap), terrain edits and location roots are never props
            if (go.GetComponentInChildren<Heightmap>(true) != null || go.GetComponentInChildren<TerrainComp>(true) != null || go.GetComponentInChildren<TerrainModifier>(true) != null
                || go.GetComponentInChildren<LocationProxy>(true) != null || go.GetComponentInChildren<Location>(true) != null) return "terrain or a location root";
            if (go.GetComponentInChildren<Character>(true) != null || go.GetComponentInChildren<BaseAI>(true) != null) return "a creature";
            if (s_animator != null && go.GetComponentInChildren(s_animator, true) != null) return "animated (Animator)";
            if (go.GetComponentInChildren<Light>(true) != null || go.GetComponentInChildren<LightFlicker>(true) != null || go.GetComponentInChildren<Fireplace>(true) != null)
                return "has a light or fire that switches its own parts";
            if (go.GetComponentInChildren<Door>(true) != null || go.GetComponentInChildren<Container>(true) != null || go.GetComponentInChildren<TeleportWorld>(true) != null
                || go.GetComponentInChildren<Ship>(true) != null || go.GetComponentInChildren<Vagon>(true) != null || go.GetComponentInChildren<ItemStand>(true) != null)
                return "interactive (door / container / portal / ship / cart / stand)";
            if (go.GetComponentInChildren<SpawnArea>(true) != null || go.GetComponentInChildren<CreatureSpawner>(true) != null) return "a spawner";
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null && !rb.isKinematic) return "moves under physics";
            return null;
        }

        internal static bool LocationPropsOn => FiresEasyBakeMeshesPlugin.IndirectCoverageLocationProps != null && FiresEasyBakeMeshesPlugin.IndirectCoverageLocationProps.Value;

        internal sealed class Verdict
        {
            public Kind Kind;
            public string Refusal;                 // null = eligible
            public HashSet<string> ExcludedPaths;  // renderer paths (relative to the root) that change at runtime: always vanilla
            public string Name;
        }

        private static readonly System.Type s_animator = System.Type.GetType("UnityEngine.Animator, UnityEngine.AnimationModule", false);
        private static readonly Dictionary<int, Verdict> s_byPrefab = new Dictionary<int, Verdict>();
        internal static void Clear() => s_byPrefab.Clear();
        internal static IEnumerable<KeyValuePair<int, Verdict>> All => s_byPrefab;

        internal static Verdict For(int prefabHash, GameObject live)
        {
            if (s_byPrefab.TryGetValue(prefabHash, out var v)) return v;
            var proto = SafePrefab.Get(prefabHash);
            v = Judge(proto != null ? proto : live);
            v.Name = proto != null ? proto.name : prefabHash.ToString();
            s_byPrefab[prefabHash] = v;
            if (v.Refusal != null) EasyBakeLog.Debug($"[Coverage] {v.Name} stays vanilla: {v.Refusal}");
            return v;
        }

        internal static Kind KindOf(GameObject go)
        {
            if (go == null) return Kind.None;
            if (go.GetComponent<TreeLog>() != null) return Kind.None;
            if (go.GetComponent<ItemDrop>() != null) return Kind.Item;
            if (go.GetComponent<Pickable>() != null) return Kind.Pickable;   // before Piece: a planted crop keeps its pick / respawn hooks
            if (go.GetComponent<TreeBase>() != null) return Kind.Tree;
            if (go.GetComponent<MineRock5>() != null) return Kind.MineRock5;
            if (go.GetComponent<MineRock>() != null) return Kind.MineRock;
            if (go.GetComponent<Piece>() != null) return Kind.Piece;
            if (go.GetComponent<Destructible>() != null) return Kind.Destructible;
            if (LocationPropsOn && go.GetComponent<ZNetView>() != null) return Kind.LocationProp;   // 1.2.216: judged strictly in Judge
            return Kind.None;
        }

        private static Verdict Judge(GameObject go)
        {
            var v = new Verdict { Kind = KindOf(go), ExcludedPaths = new HashSet<string>() };
            if (go == null) { v.Refusal = "prefab not found"; return v; }
            if (v.Kind == Kind.None) { v.Refusal = "not a pickable, tree, destructible, rock, piece or dropped item (or a felled log)"; return v; }
            var body = go.GetComponent<Rigidbody>();
            // a dropped item is covered only while its body sleeps (CoveredObject.SleepCheck); anything else that can move under physics never
            if (v.Kind != Kind.Item && body != null && !body.isKinematic) { v.Refusal = "moves under physics (non-kinematic Rigidbody)"; return v; }
            if (v.Kind == Kind.Item && body == null) { v.Refusal = "a dropped item without a Rigidbody"; return v; }
            if (v.Kind == Kind.LocationProp)
            {
                string why = WhyNotStaticProp(go);
                if (why != null) { v.Refusal = "location prop kept vanilla: " + why; return v; }
            }

            MarkRuntimeDriven(v, go.transform);

            int usable = 0;
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
                if ((r.enabled || CoveredObject.HiddenByCoverage(r)) && !v.ExcludedPaths.Contains(PathOf(go.transform, r.transform)) && !IsLowTwin(r)) usable++;
            if (usable == 0 && v.Kind != Kind.MineRock5) v.Refusal = "no static MeshRenderer";   // MineRock5 builds its renderer's mesh at runtime
            return v;
        }

        /// <summary>
        /// Renderers whose look or pose a vanilla component drives per object stay Unity's for good:
        /// a MaterialVariation swaps that renderer's material, a MaterialFader animates its listed renderers (or all under it), an
        /// Animator moves everything under it.
        /// </summary>
        private static void MarkRuntimeDriven(Verdict v, Transform root)
        {
            foreach (var variation in root.GetComponentsInChildren<MaterialVariation>(true))
                v.ExcludedPaths.Add(PathOf(root, variation.transform));
            foreach (var fader in root.GetComponentsInChildren<MaterialFader>(true))
            {
                bool listed = fader.m_renderers != null && fader.m_renderers.Count > 0;
                if (!listed) { MarkSubtree(v, root, fader.transform); continue; }
                foreach (var r in fader.m_renderers) if (r != null) v.ExcludedPaths.Add(PathOf(root, r.transform));
            }
            // Animator by type name: EBM has no compile reference to UnityEngine.AnimationModule (and needs none for one type test).
            if (s_animator != null)
                foreach (var animator in root.GetComponentsInChildren(s_animator, true)) MarkSubtree(v, root, animator.transform);

            // Pieces: the parts a piece drives itself.
            //  - WearNTear's snow overlays read _SnowLevel per piece; its fragment roots only show while the piece breaks apart;
            //  - a portal's model and a ward's model animate their emission per object; a shield generator's bubble has its own blocks.
            // Wear states (m_new / m_worn / m_broken) and the wet look (m_wet) are NOT excluded: they switch whole children on and off,
            // which CoveredPartWatch follows, so each state is covered while it shows.
            foreach (var wnt in root.GetComponentsInChildren<WearNTear>(true))
            {
                MarkRenderer(v, root, wnt.m_snow); MarkRenderer(v, root, wnt.m_snowWorn); MarkRenderer(v, root, wnt.m_snowBroken);
                if (wnt.m_fragmentRoots != null) foreach (var fr in wnt.m_fragmentRoots) if (fr != null) MarkSubtree(v, root, fr.transform);
            }
            foreach (var portal in root.GetComponentsInChildren<TeleportWorld>(true)) MarkRenderer(v, root, portal.m_model);
            foreach (var ward in root.GetComponentsInChildren<PrivateArea>(true)) MarkRenderer(v, root, ward.m_model);
            foreach (var shield in root.GetComponentsInChildren<ShieldGenerator>(true))
                if (shield.m_enabledObject != null) MarkSubtree(v, root, shield.m_enabledObject.transform);
        }

        private static void MarkRenderer(Verdict v, Transform root, Renderer r)
        {
            if (r != null) MarkSubtree(v, root, r.transform);   // the renderer and anything under it
        }

        private static void MarkSubtree(Verdict v, Transform root, Transform under)
        {
            foreach (var r in under.GetComponentsInChildren<MeshRenderer>(true)) v.ExcludedPaths.Add(PathOf(root, r.transform));
        }

        /// <summary>
        /// A "_Combined Mesh [low]" renderer with a "[high]" sibling: some prefabs ship both combined copies with no LODGroup entry for the
        /// low one, so vanilla never shows it. Drawing it as well would put two copies on top of each other.
        /// </summary>
        internal static bool IsLowTwin(MeshRenderer renderer)
        {
            if (!renderer.name.Contains("_Combined Mesh [low]")) return false;
            var parent = renderer.transform.parent;
            if (parent == null) return false;
            for (int i = 0; i < parent.childCount; i++)
                if (parent.GetChild(i).name.Contains("_Combined Mesh [high]")) return true;
            return false;
        }

        internal static string PathOf(Transform root, Transform t)
        {
            if (t == root) return ".";
            string path = t.name;
            for (var p = t.parent; p != null && p != root; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>
        /// The screen-height window Unity's LODGroup would draw this renderer in, read from the LIVE object (MineRock5 and IntactRockLod
        /// rewrite their LODs at runtime). Level i draws while size / (2 d tan(fov/2)) x lodBias is in [its own transition, the previous
        /// level's transition); LOD0 starts at +inf. A renderer listed in several levels spans them all, from its first to its last. Not under a
        /// LODGroup = always drawn (Start +inf, End 0). Returns false when the renderer is under a LODGroup but in none of its levels
        /// (Unity never draws those, so neither do we).
        /// </summary>
        internal static bool RangeFor(Renderer r, out IndirectLodRange range)
        {
            range = new IndirectLodRange { ReferencePointWorld = r.bounds.center, WorldSize = 0f, StartHeight = float.PositiveInfinity, EndHeight = 0f };
            var lg = r.GetComponentInParent<LODGroup>();
            if (lg == null || !lg.enabled) return true;
            var lods = lg.GetLODs();
            float start = float.PositiveInfinity; bool found = false; float first = 0f, last = 0f;
            for (int i = 0; i < lods.Length; i++)
            {
                bool inLevel = false;
                var rs = lods[i].renderers;
                if (rs != null) for (int k = 0; k < rs.Length; k++) if (rs[k] == r) { inLevel = true; break; }
                if (inLevel) { if (!found) first = start; found = true; last = lods[i].screenRelativeTransitionHeight; }
                start = lods[i].screenRelativeTransitionHeight;
            }
            if (!found) return false;
            var gt = lg.transform;
            var s = gt.lossyScale;
            range.ReferencePointWorld = gt.TransformPoint(LocalReferencePoint(lg));
            range.WorldSize = lg.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
            range.StartHeight = first;
            range.EndHeight = last;
            return true;
        }

        // Vanilla's LodFadeInOut parks localReferencePoint at 999999 for 0.1-0.3 s after Awake (a fade-in trick) and keeps the real one in
        // a private field; read that one, so a range taken in that window is not "never drawn".
        private static readonly System.Reflection.FieldInfo f_fadeOriginal =
            typeof(LodFadeInOut).GetField("m_originalLocalRef", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private static Vector3 LocalReferencePoint(LODGroup lg)
        {
            var p = lg.localReferencePoint;
            if (p.x < 99999f) return p;
            var fade = lg.GetComponent<LodFadeInOut>();
            if (fade != null && f_fadeOriginal != null) { try { return (Vector3)f_fadeOriginal.GetValue(fade); } catch { } }
            return Vector3.zero;
        }
    }
}
