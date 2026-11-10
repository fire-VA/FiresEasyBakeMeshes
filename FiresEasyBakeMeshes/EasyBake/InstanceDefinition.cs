using System;
using System.Collections.Generic;

using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // A prefab as one wear look shows it: the unit instanced groups, instance definitions and stand-in colliders share.
    internal readonly struct InstanceKey : IEquatable<InstanceKey>
    {
        public readonly int PrefabHash;
        public readonly WearLook Look;

        public InstanceKey(int prefabHash, WearLook look)
        {
            PrefabHash = prefabHash;
            Look = look;
        }

        public bool Equals(InstanceKey other) => PrefabHash == other.PrefabHash && Look == other.Look;
        public override bool Equals(object obj) => obj is InstanceKey other && Equals(other);
        public override int GetHashCode() => unchecked(PrefabHash * 397 ^ Look.GetHashCode());
    }

    // One drawable piece of a prefab: a mesh, one of its submeshes with the material for it,
    // and where that sits relative to the first part, so a whole prefab can be redrawn from
    // the instance matrices the group already holds.
    // 1.2.208: one renderer + submesh of a LOD level, with its own screen-height window (indirect path only)
    internal sealed class LodPart
    {
        public Mesh Mesh; public int SubMesh; public Matrix4x4 FromFirst; public RenderParams RenderParams; public Material SourceMaterial;
        public float Start, End; public Vector3 RefFirst; public float Size;   // Size 0 = not under a LODGroup: always drawn
    }

    internal sealed class InstancePart
    {
        public Mesh NearMesh;
        public Mesh FarMesh;
        // 1.2.196 (DESIGN_OcclusionLOD 2b): the prefab's own LOD chain for this part, [0] = the near mesh, and vanilla's transition
        // heights (LOD.screenRelativeTransitionHeight per level; below the last = vanilla culls the piece). LodSize = the LODGroup's
        // world size at prefab scale, the number vanilla divides by the distance. No LODGroup: one level, never culled by LOD.
        public Mesh[] LodMeshes;
        public float[] LodHeights;
        public float LodSize;
        public int SubMesh;

        // The LOD level for a run whose nearest point is `distance` from the camera (scale = the run's instance scale), or -1 = culled,
        // exactly as vanilla's LODGroup picks it: relative height = size / (2 d tan(fov/2)) x lodBias against each level's transition.
        internal int LodFor(float distance, float scale, float halfFovTan)
        {
            if (LodMeshes == null || LodMeshes.Length == 0 || LodHeights == null || LodSize <= 0f) return 0;
            float rel = LodSize * scale / (2f * Mathf.Max(distance, 0.01f) * halfFovTan) * QualitySettings.lodBias;
            for (int k = 0; k < LodHeights.Length; k++)
                if (rel >= LodHeights[k]) return Mathf.Min(k, LodMeshes.Length - 1);
            return LodHeights.Length > LodMeshes.Length ? LodMeshes.Length - 1 : -1;   // past a level we could not resolve: keep the last we have
        }
        public Matrix4x4 FromFirst;
        public RenderParams RenderParams;
        // 1.2.155: where the material came from, so a mod that puts a NEW material on the prefab's renderer (seasons) is followed too
        public Renderer SourceRenderer;
        public int SourceIndex;
        public Material SourceMaterial;
        private float _nextCheck;

        internal void FollowRenderer()
        {
            if (SourceRenderer == null) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextCheck) return;
            _nextCheck = now + 1f;
            var mats = SourceRenderer.sharedMaterials;
            var live = SourceIndex < mats.Length ? mats[SourceIndex] : null;
            if (live == null || ReferenceEquals(live, SourceMaterial)) return;
            string was = SourceMaterial != null ? SourceMaterial.name : "null";
            SourceMaterial = live;
            RenderParams.material = InstanceDefinition.InstancingMaterialPublic(live);
            InstanceDefinition.AnnounceFollow();
            EasyBakeLog.Debug($"[Instancing] part of '{SourceRenderer.name}' follows a new material on its renderer: '{was}' -> '{live.name}'");
        }
    }

    // How one prefab is drawn as GPU instances: every renderer it actually shows, lifted off
    // the prefab as parts, plus the offset mapping a piece's root transform onto the first of
    // them (a prefab's mesh usually hangs off a child).
    //
    // Instancing accepts a prefab when everything it would draw is a MeshRenderer with a mesh,
    // one part per material. That preserves the hide-set == draw-set invariant, so suppressing
    // the piece hides nothing that is not redrawn, and it covers both the prefabs a megabase is
    // built from in bulk - palisades, floor tiles, stakewalls - and the two-renderer ones like
    // vines and log poles that a single-renderer rule turned away.
    //
    // Unlike the combine path, instancing does NOT need a CPU-readable mesh, so it can also take
    // prefabs Mesh.CombineMeshes has to reject.
    internal class InstanceDefinition
    {
        // Past a handful of parts a prefab is left to the combine path: each part is its own
        // draw call per zone, and merging geometry is the cheaper trade by then.
        private const int MaxParts = 8;

        public InstancePart[] Parts;
        // 1.2.208 ([generator] review item 3; Fire's head-to-head): the prefab's LODGroup levels as PER-RENDERER parts, for the indirect path only.
        // Every renderer of every level gets its own [start, end) screen-height window from its LODGroup's reference point (a renderer in
        // several levels spans them), exactly the way vanilla's LODGroup switches it, with no level pairing. ResolveLodChain's pairing stops at
        // any multi-renderer / offset level, so those prefabs drew LOD0 at every distance. Null = no LODGroup (the parts path is used).
        // The method (per-renderer LOD ranges) was learned from VPO's renderer (ontrigger, MIT, read for reference); this code is ours.
        public LodPart[] LodParts;
        public Matrix4x4 LocalOffset;
        public bool CastsShadows;
        // Covers every part and both tiers in the first part's own space; a mesh is rarely centred on its pivot.
        public Bounds MeshBounds;

        private static readonly HashSet<string> s_farOffsetLogged = new HashSet<string>();

        // 1.2.85 (an external report via Fire: another mod's textures broke with EBM installed): EBM no longer sets
        // enableInstancing on a prefab's own shared material, which changed that material for every renderer in the game
        // (another mod's, or vanilla's through a mod's mock slot). A material that doesn't already instance is drawn through
        // EBM's own copy of it, made once per material.
        private static readonly Dictionary<Material, Material> s_instancingCopies = new Dictionary<Material, Material>();

        // 1.2.116 ([visual], Fire 10-24 hand-over 57: every baked piece invisible after TossinShade's linear Custom/FiresPiece shipped
        // without its instancing variant): a shader with no INSTANCING_ON keyword draws NOTHING through RenderMeshInstanced, so warn
        // once per shader the moment a group would draw with one. Read-only: the draw is unchanged.
        private static readonly HashSet<Shader> s_instancingChecked = new HashSet<Shader>();

        private static void WarnIfNoInstancingVariant(Material material)
        {
            SelfCheckOnce();
            var shader = material != null ? material.shader : null;
            if (shader == null || !s_instancingChecked.Add(shader)) return;
            try
            {
                bool hasVariant = shader.keywordSpace.FindKeyword("INSTANCING_ON").isValid;
                if (!hasVariant)
                    EasyBakeLog.Warn($"[Instancing] shader '{shader.name}' (material '{material.name}') has no INSTANCING_ON variant: pieces EBM draws instanced with it will be INVISIBLE. Rebuild the shader with #pragma multi_compile_instancing, or list the prefab in [Batching] ExcludedPrefabs.");
            }
            catch (System.Exception ex) { EasyBakeLog.Debug($"[Instancing] could not check '{shader.name}' for an instancing variant: {ex.Message}"); }
        }

        // The guard's own proof, once a session (1.2.118: the lead 10-24, "Standard" is not loaded in Valheim, so it proved nothing): vanilla
        // "Custom/Piece" IS loaded and EBM draws it instanced every round, so the check must say YES for it. yes = the check sees instancing
        // variants and the no-variant warning can be trusted; NO = it can't, ignore those warnings (and the guard comes back out).
        private static bool s_selfChecked;

        private static void SelfCheckOnce()
        {
            if (s_selfChecked) return;
            s_selfChecked = true;
            try
            {
                var control = Shader.Find("Custom/Piece");
                string verdict = control == null ? "not loaded (no proof this session)" : control.keywordSpace.FindKeyword("INSTANCING_ON").isValid ? "yes (the no-variant warning can be trusted)" : "NO (the check can't see instancing variants: ignore the no-variant warnings)";
                EasyBakeLog.Debug("[Instancing] self-check: vanilla 'Custom/Piece' INSTANCING_ON " + verdict);
            }
            catch (System.Exception ex) { EasyBakeLog.Debug("[Instancing] self-check failed: " + ex.Message); }
        }

        internal static Material InstancingMaterialPublic(Material material) => InstancingMaterial(material);
        internal static void AnnounceFollow() { if (!s_followAnnounced) { s_followAnnounced = true; EasyBakeLog.Debug("[Instancing] baked copies follow their source material's changes (another mod changed one; no re-bake needed)"); } }

        private static Material InstancingMaterial(Material material)
        {
            WarnIfNoInstancingVariant(material);
            if (material.enableInstancing) return material;
            if (s_instancingCopies.TryGetValue(material, out Material copy) && copy != null) return copy;
            copy = new Material(material) { name = material.name + " (EBM instancing)", enableInstancing = true };
            s_instancingCopies[material] = copy;
            s_sourceOf[copy] = material;
            s_sigOf[copy] = Signature(material);   // 1.2.155: the state the copy was made from
            return copy;
        }

        // 1.2.125 ([visual]/[seasons], Fire 10-26 shots 459/460: straw roof white far, bare near): FTS swaps the shader of the
        // SHARED material in place (FiresPieceShaderLoader.TrySwap, on Piece.Awake + a 4 s rescan). A copy made before that kept the
        // old shader, which FTS never sees. Each draw the copy follows its source's shader (and properties) again.
        private static readonly Dictionary<Material, Material> s_sourceOf = new Dictionary<Material, Material>();

        // 1.2.155 ([visual], public report via Fire 10-30: with Seasonality, autumn groves drew in the frosted winter look until EBM was
        // toggled): a mod that keeps the SHADER but swaps the shared material's textures / colours (seasons) never reached the copy, which
        // only re-synced on a shader change. Each copy now also compares a cheap signature of its source's texture ids + colour / vector /
        // float values, at most once a second per copy; on a change it copies the properties again (no re-bake).
        private static readonly Dictionary<Material, int> s_sigOf = new Dictionary<Material, int>();
        private static readonly Dictionary<Material, float> s_nextCheck = new Dictionary<Material, float>();
        private static readonly List<int> s_texIds = new List<int>();
        private static bool s_followAnnounced;

        private static int Signature(Material m)
        {
            unchecked
            {
                int h = 17;
                s_texIds.Clear(); m.GetTexturePropertyNameIDs(s_texIds);
                foreach (int id in s_texIds) { var t = m.GetTexture(id); h = h * 31 + id; h = h * 31 + (t != null ? t.GetInstanceID() : 0); }
                var sh = m.shader;
                if (sh != null)
                    for (int i = 0, n = sh.GetPropertyCount(); i < n; i++)
                    {
                        int id = sh.GetPropertyNameId(i);
                        switch (sh.GetPropertyType(i))
                        {
                            case UnityEngine.Rendering.ShaderPropertyType.Color: h = h * 31 + m.GetColor(id).GetHashCode(); break;
                            case UnityEngine.Rendering.ShaderPropertyType.Vector: h = h * 31 + m.GetVector(id).GetHashCode(); break;
                            case UnityEngine.Rendering.ShaderPropertyType.Float:
                            case UnityEngine.Rendering.ShaderPropertyType.Range: h = h * 31 + m.GetFloat(id).GetHashCode(); break;
                        }
                    }
                return h;
            }
        }

        internal static void FollowSource(Material copy)
        {
            if (copy == null || !s_sourceOf.TryGetValue(copy, out Material source) || source == null) return;
            if (source.shader == copy.shader)
            {
                float now = Time.realtimeSinceStartup;
                if (s_nextCheck.TryGetValue(copy, out float next) && now < next) return;
                s_nextCheck[copy] = now + 1f;
                int sig = Signature(source);
                if (!s_sigOf.TryGetValue(copy, out int had)) { s_sigOf[copy] = sig; return; }   // first look: the copy was made from this state
                if (sig == had) return;
                s_sigOf[copy] = sig;
                copy.CopyPropertiesFromMaterial(source);
                copy.enableInstancing = true;
                AnnounceFollow();
                EasyBakeLog.Debug($"[Instancing] copy '{copy.name}' re-copied: its source material's textures / colours changed (same shader)");
                return;
            }
            var old = copy.shader;
            copy.shader = source.shader;
            copy.CopyPropertiesFromMaterial(source); s_sigOf[copy] = Signature(source);   // 1.2.155
            copy.enableInstancing = true;
            EasyBakeLog.Debug($"[Snow] instancing copy '{copy.name}' follows its source to shader '{(source.shader != null ? source.shader.name : "null")}' #{(source.shader != null ? source.shader.GetInstanceID() : 0)} (was '{(old != null ? old.name : "null")}' #{(old != null ? old.GetInstanceID() : 0)})");
        }

        public static bool TryBuild(GameObject prefab, WearLook look, out InstanceDefinition definition, out string rejectReason)
        {
            definition = null;
            rejectReason = null;
            if (prefab == null) { rejectReason = "null prefab"; return false; }
            if (ModdedPrefabGate.Refuses(prefab, out string modded)) { rejectReason = "modded prefab: " + modded; return false; }

            SnowFeed.SwapPrefabMaterials(prefab);   // 1.2.164

            // Only MeshRenderers are silenced when a piece is suppressed, so only they have to be
            // redrawn here; particles and anything else keep drawing themselves as they always did.
            var allRenderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            if (allRenderers.Length == 0) { rejectReason = "no MeshRenderers"; return false; }

            HashSet<Renderer> lod0Set, lodControlled;
            IndexLodMembership(prefab, out lod0Set, out lodControlled);

            var visible = new List<MeshRenderer>();
            for (int i = 0; i < allRenderers.Length; i++)
            {
                var renderer = allRenderers[i];
                if (renderer == null || !(renderer.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(renderer))) continue;   // step 2: a prefab part coverage switched off still counts
                if (!WearLooks.IsShown(prefab, look, renderer.transform)) continue;
                bool isLodControlled = lodControlled != null && lodControlled.Contains(renderer);
                if (isLodControlled && (lod0Set == null || !lod0Set.Contains(renderer))) continue;
                if (!isLodControlled && Indirect.Coverage.CoverageRules.IsLowTwin(renderer)) continue;   // 1.2.208 ([generator]): a "[low]" combined twin of a "[high]" vanilla never shows: never drawn next to it

                visible.Add(renderer);
            }

            if (visible.Count == 0) { rejectReason = "no visible renderer"; return false; }
            if (visible.Count > 1 && !FiresEasyBakeMeshesPlugin.BatchingMultiPartPieces.Value)
            {
                rejectReason = "more than one visible renderer";
                return false;
            }

            var parts = new List<InstancePart>(visible.Count);
            Matrix4x4 firstOffset = Matrix4x4.identity, firstInverse = Matrix4x4.identity;
            for (int i = 0; i < visible.Count; i++)
            {
                var renderer = visible[i];
                var meshFilter = renderer.GetComponent<MeshFilter>();
                if (meshFilter == null || meshFilter.sharedMesh == null) { rejectReason = "no MeshFilter/mesh"; return false; }

                var nearMesh = meshFilter.sharedMesh;
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0) { rejectReason = "no material"; return false; }
                if (materials.Length > nearMesh.subMeshCount) { rejectReason = "more materials than submeshes"; return false; }

                var offset = prefab.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                if (i == 0)
                {
                    firstOffset = offset;
                    firstInverse = offset.inverse;
                }

                for (int m = 0; m < materials.Length; m++)
                {
                    var material = materials[m];
                    if (material == null) { rejectReason = "null material"; return false; }
                    if (parts.Count == MaxParts) { rejectReason = "more than " + MaxParts + " parts"; return false; }

                    parts.Add(new InstancePart
                    {
                        NearMesh = nearMesh,
                        FarMesh = ResolveFarMesh(prefab, look, renderer, nearMesh, material, m),
                        LodMeshes = ResolveLodChain(prefab, look, renderer, nearMesh, material, m, out var lodHeights, out var lodSize),   // 1.2.196
                        LodHeights = lodHeights, LodSize = lodSize,
                        SubMesh = m,
                        FromFirst = i == 0 ? Matrix4x4.identity : firstInverse * offset,
                        SourceRenderer = renderer, SourceIndex = m, SourceMaterial = material,   // 1.2.155
                        RenderParams = new RenderParams(InstancingMaterial(material))
                        {
                            shadowCastingMode = renderer.shadowCastingMode,
                            receiveShadows = renderer.receiveShadows,
                            layer = renderer.gameObject.layer,
                            lightProbeUsage = LightProbeUsage.Off,
                            reflectionProbeUsage = renderer.reflectionProbeUsage,
                        },
                    });
                }
            }

            // 1.2.102 (Fire 10-18: stone_floor_2x2 invisible until hovered with the hammer): a definition whose parts would
            // put nothing on screen (shadow-only, an empty mesh or submesh, a shader the GPU can't run) silenced the piece
            // and drew nothing in its place. Such a prefab stays out of instancing and keeps drawing itself.
            string undrawable = Undrawable(parts);
            if (undrawable != null)
            {
                rejectReason = "nothing drawable (" + undrawable + ")";
                if (s_undrawableLogged.Add(prefab.name))
                    EasyBakeLog.Debug($"[Instancing] '{prefab.name}' ({look}) kept out of instancing: {undrawable}. Parts: {Describe(parts)}");
                return false;
            }

            var lodParts = BuildLodParts(prefab, look, firstInverse);
            // 1.2.208 ([generator] / lead): what each definition's LOD looks like on both paths, once per prefab + look (definitions are cached)
            EasyBakeLog.Debug($"[Instancing] LOD '{prefab.name}': indirect {(lodParts == null ? "no per-renderer parts (no LODGroup, or a renderer it cannot draw)" : lodParts.Length + " per-renderer part(s)")}; legacy chain {(parts.Count > 0 && parts[0].LodMeshes != null ? parts[0].LodMeshes.Length : 1)} level(s) of {(parts.Count > 0 && parts[0].LodHeights != null ? parts[0].LodHeights.Length : 0)} transition(s)");
            definition = new InstanceDefinition
            {
                LodParts = lodParts,
                Parts = parts.ToArray(),
                LocalOffset = firstOffset,
                CastsShadows = AnyCastsShadows(parts),
                MeshBounds = MergeBounds(parts),
            };
            return true;
        }

        // 1.2.101 (Fire 10-18: stone floors invisible until the hammer came out): FiresAdminPrefabs' LazyBundles returns idle
        // prefabs to stubs and releases their assets, which destroys the meshes and materials a definition holds. A dead part
        // drew nothing while its pieces stayed silenced. False once any part's mesh or material is gone.
        // 1.2.213 ([saddles], [Optimize] IndirectCachedAlive): definitions are shared (by key) across zones' groups, and ZoneTracker asks every
        // group's definition every frame; the native null checks run once per definition per frame instead.
        private int _aliveFrame = -1; private bool _alive;
        public bool IsAliveCached(int frame)
        {
            if (_aliveFrame != frame) { _aliveFrame = frame; _alive = IsAlive(); }
            return _alive;
        }

        public bool IsAlive()
        {
            if (Parts == null) return false;
            for (int i = 0; i < Parts.Length; i++)
            {
                var part = Parts[i];
                if (part == null || part.NearMesh == null || part.FarMesh == null || part.RenderParams.material == null) return false;
            }
            return true;
        }

        private static readonly HashSet<string> s_undrawableLogged = new HashSet<string>();

        // Null when at least one part puts geometry on screen; otherwise why none does.
        private static string Undrawable(List<InstancePart> parts)
        {
            string why = "no parts";
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var material = part.RenderParams.material;
                if (part.RenderParams.shadowCastingMode == ShadowCastingMode.ShadowsOnly) { why = "every part is shadow-only"; continue; }
                if (part.NearMesh == null || part.NearMesh.vertexCount == 0 || part.NearMesh.GetIndexCount(part.SubMesh) == 0) { why = "empty mesh or submesh"; continue; }
                if (material == null || material.shader == null || !material.shader.isSupported) { why = "unsupported shader"; continue; }
                return null;
            }
            return why;
        }

        // One line per part: mesh, vertices, submesh indices, material, shader, instancing, shadows, layer.
        internal static string Describe(IList<InstancePart> parts)
        {
            if (parts == null) return "(none)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var mat = p.RenderParams.material;
                var mesh = p.NearMesh;
                sb.Append(i == 0 ? "" : "; ").Append('#').Append(i).Append(" mesh '").Append(mesh != null ? mesh.name : "null").Append('\'');
                if (mesh != null) sb.Append(" v").Append(mesh.vertexCount).Append(" sub").Append(p.SubMesh).Append(" idx").Append(p.SubMesh < mesh.subMeshCount ? mesh.GetIndexCount(p.SubMesh) : 0);
                sb.Append(p.FarMesh != mesh ? " far '" + (p.FarMesh != null ? p.FarMesh.name : "null") + "'" : "");
                sb.Append(" mat '").Append(mat != null ? mat.name : "null").Append("' shader '").Append(mat != null && mat.shader != null ? mat.shader.name : "null").Append('\'');
                if (mat != null && mat.shader != null) sb.Append(mat.shader.isSupported ? "" : " UNSUPPORTED").Append(mat.enableInstancing ? " inst" : " NO-INST");
                // 1.2.183 (Fire 11-07, stone_wall_2x1 body invisible, cap only): keywords, queue, source material + shader and the source renderer's state
                if (mat != null) sb.Append(" kw[").Append(string.Join(" ", mat.shaderKeywords)).Append("] q").Append(mat.renderQueue);
                var src = p.SourceMaterial;
                sb.Append(" src '").Append(src != null ? src.name : "null").Append("'/'").Append(src != null && src.shader != null ? src.shader.name : "null").Append('\'');
                var sr = p.SourceRenderer;
                if (sr != null) sb.Append(" rend '").Append(sr.name).Append("' enabled ").Append(sr.enabled).Append(" active ").Append(sr.gameObject.activeSelf);
                sb.Append(' ').Append(p.RenderParams.shadowCastingMode).Append(" layer ").Append(LayerMask.LayerToName(p.RenderParams.layer));
            }
            return sb.ToString();
        }

        private static bool AnyCastsShadows(List<InstancePart> parts)
        {
            for (int i = 0; i < parts.Count; i++)
                if (parts[i].RenderParams.shadowCastingMode != ShadowCastingMode.Off) return true;
            return false;
        }

        // Each part's box is carried through its offset from the first part, because that is the
        // space the group's instance matrices are in.
        private static Bounds MergeBounds(List<InstancePart> parts)
        {
            var merged = new Bounds();
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var box = part.NearMesh.bounds;
                if (part.FarMesh != null && part.FarMesh != part.NearMesh) box.Encapsulate(part.FarMesh.bounds);

                var m = part.FromFirst;
                var e = box.extents;
                var centre = m.MultiplyPoint3x4(box.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == 0)
                {
                    merged = new Bounds(centre, reach * 2f);
                    continue;
                }
                merged.Encapsulate(centre + reach);
                merged.Encapsulate(centre - reach);
            }
            return merged;
        }

        // Instances draw the far mesh with LOD0's offset, so LOD1 only stands in when it is the single renderer in the
        // LOD1 slot of the group that switches this LOD0 renderer, carries this part's material on the same submesh and
        // sits exactly where LOD0 does. Anything else keeps the near mesh, so distance never moves or drops part of a piece.
        private static Mesh ResolveFarMesh(GameObject prefab, WearLook look, MeshRenderer nearRenderer, Mesh nearMesh, Material material,
            int subMesh)
        {
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lods = lodGroups[g] != null ? lodGroups[g].GetLODs() : null;
                if (lods == null || lods.Length < 2 || !Lists(lods[0].renderers, nearRenderer)) continue;

                MeshRenderer farRenderer = null;
                var slot = lods[1].renderers;
                for (int i = 0; slot != null && i < slot.Length; i++)
                {
                    if (slot[i] == null) continue;
                    if (farRenderer != null) return nearMesh;
                    farRenderer = slot[i] as MeshRenderer;
                    if (farRenderer == null) return nearMesh;
                }
                if (farRenderer == null || farRenderer == nearRenderer || !(farRenderer.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(farRenderer))) return nearMesh;
                if (!WearLooks.IsShown(prefab, look, farRenderer.transform)) return nearMesh;
                if (!Draws(farRenderer.sharedMaterials, material, subMesh)) return nearMesh;
                var farFilter = farRenderer.GetComponent<MeshFilter>();
                if (farFilter == null || farFilter.sharedMesh == null) return nearMesh;
                if (farFilter.sharedMesh.subMeshCount <= subMesh) return nearMesh;
                if (!SamePlacement(farRenderer.transform.localToWorldMatrix, nearRenderer.transform.localToWorldMatrix))
                {
                    if (s_farOffsetLogged.Add(prefab.name))
                        EasyBakeLog.Debug($"[Instancing] '{prefab.name}': LOD1 is placed differently from LOD0, so its instances keep the LOD0 mesh at distance.");
                    return nearMesh;
                }
                return farFilter.sharedMesh;
            }
            return nearMesh;
        }

        // 1.2.196: the whole chain, by the same rule ResolveFarMesh uses for LOD1 (the single renderer in that level's slot, same material on
        // the same submesh, same placement as LOD0). The chain stops at the first level that does not qualify; LodHeights keeps every level's
        // transition so LodFor still culls where vanilla culls (it keeps the last resolved mesh instead when a deeper level was unresolvable).
        private static Mesh[] ResolveLodChain(GameObject prefab, WearLook look, MeshRenderer nearRenderer, Mesh nearMesh, Material material,
            int subMesh, out float[] heights, out float size)
        {
            heights = null; size = 0f;
            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lodGroup = lodGroups[g];
                var lods = lodGroup != null ? lodGroup.GetLODs() : null;
                if (lods == null || lods.Length == 0 || !Lists(lods[0].renderers, nearRenderer)) continue;
                heights = new float[lods.Length];
                for (int k = 0; k < lods.Length; k++) heights[k] = lods[k].screenRelativeTransitionHeight;
                Vector3 s = lodGroup.transform.lossyScale;
                size = lodGroup.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                var chain = new List<Mesh> { nearMesh };
                for (int k = 1; k < lods.Length; k++)
                {
                    MeshRenderer levelRenderer = null; bool ok = true;
                    var slot = lods[k].renderers;
                    for (int i = 0; slot != null && i < slot.Length; i++)
                    {
                        if (slot[i] == null) continue;
                        if (levelRenderer != null) { ok = false; break; }
                        levelRenderer = slot[i] as MeshRenderer;
                        if (levelRenderer == null) { ok = false; break; }
                    }
                    if (!ok || levelRenderer == null || !(levelRenderer.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(levelRenderer))) break;
                    if (!WearLooks.IsShown(prefab, look, levelRenderer.transform)) break;
                    if (!Draws(levelRenderer.sharedMaterials, material, subMesh)) break;
                    var filter = levelRenderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null || filter.sharedMesh.subMeshCount <= subMesh) break;
                    if (!SamePlacement(levelRenderer.transform.localToWorldMatrix, nearRenderer.transform.localToWorldMatrix)) break;
                    chain.Add(filter.sharedMesh);
                }
                return chain.ToArray();
            }
            return new[] { nearMesh };
        }

        private static bool Draws(Material[] materials, Material material, int subMesh)
            => materials != null && subMesh < materials.Length && materials[subMesh] == material;

        private static bool Lists(Renderer[] renderers, Renderer renderer)
        {
            if (renderers == null) return false;
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] == renderer) return true;
            return false;
        }

        private static bool SamePlacement(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
                if (Mathf.Abs(a[i] - b[i]) > 0.001f) return false;
            return true;
        }

        // 1.2.208: one LodPart per (renderer, submesh) of every LOD level; null when the prefab has no LODGroup or any renderer cannot be drawn
        private static LodPart[] BuildLodParts(GameObject prefab, WearLook look, Matrix4x4 firstInverse)
        {
            var groups = prefab.GetComponentsInChildren<LODGroup>(true);
            if (groups.Length == 0) return null;
            var windows = new Dictionary<Renderer, (float start, float end, LODGroup group)>();
            foreach (var g in groups)
            {
                if (g == null || !g.enabled) continue;
                var lods = g.GetLODs();
                for (int k = 0; k < lods.Length; k++)
                {
                    float start = k == 0 ? float.MaxValue : lods[k - 1].screenRelativeTransitionHeight;
                    float end = lods[k].screenRelativeTransitionHeight;
                    if (lods[k].renderers == null) continue;
                    foreach (var r in lods[k].renderers)
                    {
                        if (r == null) continue;
                        if (windows.TryGetValue(r, out var w)) windows[r] = (Mathf.Max(w.start, start), Mathf.Min(w.end, end), w.group);   // in several levels: spans them
                        else windows[r] = (start, end, g);
                    }
                }
            }
            if (windows.Count == 0) return null;
            var list = new List<LodPart>();
            foreach (var r in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r == null || !(r.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(r))) continue;
                if (!WearLooks.IsShown(prefab, look, r.transform)) continue;
                if (!windows.ContainsKey(r) && Indirect.Coverage.CoverageRules.IsLowTwin(r)) continue;   // [generator]: a "[low]" combined twin vanilla never shows
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return null;
                var mats = r.sharedMaterials;
                if (mats.Length == 0 || mats.Length > mf.sharedMesh.subMeshCount) return null;
                var offset = prefab.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                float start = float.MaxValue, end = 0f, size = 0f; Vector3 refFirst = Vector3.zero;
                if (windows.TryGetValue(r, out var w))
                {
                    start = w.start; end = w.end;
                    var gt = w.group.transform;
                    var refPrefab = prefab.transform.InverseTransformPoint(gt.TransformPoint(w.group.localReferencePoint));
                    refFirst = firstInverse.MultiplyPoint3x4(refPrefab);
                    var ls = gt.lossyScale; var ps = prefab.transform.lossyScale;
                    size = w.group.size * Mathf.Max(Mathf.Abs(ls.x / ps.x), Mathf.Max(Mathf.Abs(ls.y / ps.y), Mathf.Abs(ls.z / ps.z)));   // at prefab scale, as LodSize
                }
                for (int m = 0; m < mats.Length; m++)
                {
                    if (mats[m] == null) return null;
                    list.Add(new LodPart
                    {
                        Mesh = mf.sharedMesh, SubMesh = m, FromFirst = firstInverse * offset, SourceMaterial = mats[m],
                        RenderParams = new RenderParams(InstancingMaterial(mats[m])) { shadowCastingMode = r.shadowCastingMode, receiveShadows = r.receiveShadows, layer = r.gameObject.layer, lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = r.reflectionProbeUsage },
                        Start = start, End = end, RefFirst = refFirst, Size = size,
                    });
                }
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        private static void IndexLodMembership(GameObject prefab,
            out HashSet<Renderer> lod0Set, out HashSet<Renderer> lodControlled)
        {
            lod0Set = null;
            lodControlled = null;

            var lodGroups = prefab.GetComponentsInChildren<LODGroup>(true);
            for (int g = 0; g < lodGroups.Length; g++)
            {
                var lodGroup = lodGroups[g];
                if (lodGroup == null) continue;
                var lods = lodGroup.GetLODs();
                if (lods == null) continue;

                for (int li = 0; li < lods.Length; li++)
                {
                    var renderers = lods[li].renderers;
                    if (renderers == null) continue;
                    for (int ri = 0; ri < renderers.Length; ri++)
                    {
                        var renderer = renderers[ri];
                        if (renderer == null) continue;
                        if (lodControlled == null) lodControlled = new HashSet<Renderer>();
                        lodControlled.Add(renderer);
                        if (li == 0)
                        {
                            if (lod0Set == null) lod0Set = new HashSet<Renderer>();
                            lod0Set.Add(renderer);
                        }
                    }
                }
            }
        }
    }

    // Definitions are derived from prefab assets, so they are world-independent and
    // built once per prefab and look. A null entry is a cached rejection, which keeps repeat
    // lookups for non-instanceable prefabs off the component-walk path.
    internal static class InstanceDefinitionCache
    {
        private static readonly Dictionary<InstanceKey, InstanceDefinition> _byKey
            = new Dictionary<InstanceKey, InstanceDefinition>();
        private static readonly Dictionary<string, int> _rejectionsByReason
            = new Dictionary<string, int>();

        // 1.2.101: drops a definition whose assets were released, so the next bake rebuilds it.
        public static void Forget(InstanceKey key) => _byKey.Remove(key);

        public static void Clear()
        {
            _byKey.Clear();
            _rejectionsByReason.Clear();
            _reasonByKey.Clear();
        }

        // 1.2.182: why a key has no definition, for the cache's "no longer resolves" line
        private static readonly Dictionary<InstanceKey, string> _reasonByKey = new Dictionary<InstanceKey, string>();
        public static string WhyNot(InstanceKey key) => _reasonByKey.TryGetValue(key, out var why) && why != null ? why : "no reason recorded";

        // 1.2.182: cached rejections are built again on next use (ModdedPrefabGate learned a snow swap shader)
        public static int ForgetRejections()
        {
            var gone = new List<InstanceKey>();
            foreach (var entry in _byKey) if (entry.Value == null) gone.Add(entry.Key);
            foreach (var key in gone) { _byKey.Remove(key); _reasonByKey.Remove(key); }
            return gone.Count;
        }

        public static bool TryGet(InstanceKey key, out InstanceDefinition definition)
        {
            if (_byKey.TryGetValue(key, out definition))
            {
                if (definition == null) return false;
                if (definition.IsAlive()) return true;
                _byKey.Remove(key);   // 1.2.101: its assets were released; rebuild from the prefab as it is now
            }

            var scene = ZNetScene.instance;
            var prefab = scene != null ? FiresEasyBakeMeshes.EasyBake.SafePrefab.Get(key.PrefabHash) : null;
            InstanceDefinition built;
            string rejectReason;
            InstanceDefinition.TryBuild(prefab, key.Look, out built, out rejectReason);
            // 1.2.183 (Fire 11-07, stone_wall_2x1 drew only its snow cap): what each instanced kind draws, once per prefab and look
            if (built != null && prefab != null && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[Instancing] definition '{prefab.name}' ({key.Look}): {built.Parts.Length} part(s), bounds {built.MeshBounds.center} size {built.MeshBounds.size}, offset pos {(Vector3)built.LocalOffset.GetColumn(3)}, shadows {built.CastsShadows}. Parts: {InstanceDefinition.Describe(built.Parts)}");
            _byKey[key] = built;
            if (built == null) _reasonByKey[key] = rejectReason; else _reasonByKey.Remove(key);
            definition = built;

            if (built == null && rejectReason != null)
            {
                int seen;
                _rejectionsByReason.TryGetValue(rejectReason, out seen);
                _rejectionsByReason[rejectReason] = seen + 1;
                if (FiresEasyBakeMeshesPlugin.BatchingVerbose.Value)
                {
                    string prefabName = prefab != null ? prefab.name : key.PrefabHash.ToString();
                    EasyBakeLog.Debug($"[Instancing] '{prefabName}' ({key.Look}) not instanceable: {rejectReason}.");
                }
            }
            return definition != null;
        }

        // One line per bake summarising why prefabs were turned away, so a zone reporting
        // zero instanced groups says what stopped it rather than leaving it to guesswork.
        public static string DescribeRejections()
        {
            if (_rejectionsByReason.Count == 0) return "none";
            var parts = new List<string>(_rejectionsByReason.Count);
            foreach (var entry in _rejectionsByReason)
                parts.Add($"{entry.Key} x{entry.Value}");
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts.ToArray());
        }
    }
}
