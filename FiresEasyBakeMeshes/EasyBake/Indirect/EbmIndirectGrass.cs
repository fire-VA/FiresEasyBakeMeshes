using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.212 ([seasons], Tools\DESIGN_EbmGrass_20261108.md; [Optimize] IndirectGrass, default off): vanilla's grass drawn by EBM on the GPU.
    // Vanilla: every grass patch is one InstanceRenderer per clutter kind, and every frame MonoUpdaters calls each one's CustomUpdate: a sphere test,
    // the count fade, and a DrawMeshInstanced that re-sends up to 1023 matrices (64 KB) for grass that almost never changes. Here every renderer on
    // Custom/Grass is taken OFF vanilla's list (InstanceRenderer.Instances), its matrices go to GPU memory once per change (object-to-world rows
    // only, 48 B an instance), vanilla's own sphere test and count fade run per patch in EbmGrass.compute, and each grass kind is ONE indirect draw
    // through EBM/GrassIndirect (vanilla's look rebuilt). EBM's own code; it relies on no other mod.
    //
    // E12 / E13 round ([seasons] 11-09, Tools\DESIGN_EBM_grass_20261109.md 4.1, 4.6, 4.7; DROP_NOTES_seasons_grass2.md):
    //  - FADE CARRY (Deep North flicker, Tools\READ_grass_flicker_20261109.md): a rebuilt patch's new renderer takes its predecessor's GPU row
    //    (same patch centre, same kind, alive, not born this frame). The row keeps drawing the old blades until the new ones upload (no blank
    //    frame), and the fade is now a SHARE of the patch (0..1) on the GPU, so a changed blade count keeps what was on show. Vanilla's first-frame
    //    near snap is kept through a generation number per row (a fresh patch = a new generation; a carried one keeps its predecessor's).
    //    Key IndirectGrassFadeCarry (on): off = every new renderer starts at 0, as vanilla.
    //  - SHADOWS: the kind key includes the renderer's m_shadowCasting, drawn with it (was forced Off). A casting renderer whose clone has no
    //    ShadowCaster pass (an older bundle) stays on vanilla's path, logged once.
    //  - SELF-CHECK (Debug, 30 s): patches and blades the GPU drew vs vanilla's own answer for the same frame on the CPU (sphere test + settled
    //    fade target for every taken renderer, plus vanilla's renderer count). Standing still both must be 100 %.
    //  - INVALIDATION, every way vanilla changes a taken renderer: OnEnable / OnDisable (create, destroy, ClearAll, timeout), Clear / SetInstance /
    //    SetInstances / SetInstancesBillboard (postfixes), AddInstance during generation (the upload runs at onPreCull, after LateUpdate's
    //    generation, so the blades are there), a count, flag or LOD-distance change outside those (1 s sweep), a material look or shader change
    //    (1 s signature, now with the six floats the shader reads; a shader that is no longer Custom/Grass hands the renderer back), render queue
    //    copied from the source. Not hooked on purpose: AddInstance (VCP 0.45's takeover list holds it; a late AddInstance by another mod is
    //    caught by the sweep within 1 s).
    //  - Off, a fault, EBM's bundle without the grass assets, no compute support, or another mod patching InstanceRenderer's CustomUpdate / OnEnable:
    //    every renderer goes back on vanilla's list with its fade set to the share it was drawing (no fade-in), buffers released. One Info line per change.
    // Bounded: 4096 renderers, 256 kinds (past either, a renderer stays vanilla), instance slots in power-of-two classes 16..1024 grown by a GPU copy.
    internal static class EbmIndirectGrass
    {
        private const int MaxPatches = 4096, MaxKinds = 256, ArgWords = 5;
        private static readonly int[] ClassSize = { 16, 32, 64, 128, 256, 512, 1024 };

        private static bool KeyOn => FiresEasyBakeMeshesPlugin.IndirectGrass != null && FiresEasyBakeMeshesPlugin.IndirectGrass.Value;
        internal static bool Active => s_on;   // EbmGrassStepGate acts only while EBM draws the grass
        private static bool RecycleOn => FiresEasyBakeMeshesPlugin.IndirectGrassArrayRecycle != null && FiresEasyBakeMeshesPlugin.IndirectGrassArrayRecycle.Value;
        private static bool CarryOn => FiresEasyBakeMeshesPlugin.IndirectGrassFadeCarry == null || FiresEasyBakeMeshesPlugin.IndirectGrassFadeCarry.Value;
        private static float UploadMs => FiresEasyBakeMeshesPlugin.IndirectGrassUploadMs != null ? Mathf.Clamp(FiresEasyBakeMeshesPlugin.IndirectGrassUploadMs.Value, 0.1f, 4f) : 0.5f;

        // ── vanilla fields (InstanceRenderer is a vanilla type; private fields by FieldRef) ──
        private static readonly AccessTools.FieldRef<InstanceRenderer, Matrix4x4[]> F_Instances = AccessTools.FieldRefAccess<InstanceRenderer, Matrix4x4[]>("m_instances");
        private static readonly AccessTools.FieldRef<InstanceRenderer, int> F_Count = AccessTools.FieldRefAccess<InstanceRenderer, int>("m_instanceCount");
        private static readonly AccessTools.FieldRef<InstanceRenderer, float> F_LodCount = AccessTools.FieldRefAccess<InstanceRenderer, float>("m_lodCount");
        private static readonly AccessTools.FieldRef<InstanceRenderer, bool> F_FirstFrame = AccessTools.FieldRefAccess<InstanceRenderer, bool>("m_firstFrame");

        private sealed class Kind
        {
            public int Index; public Mesh Mesh; public Material Source, Clone; public MaterialPropertyBlock Props; public ShadowCastingMode Shadow;
            public int Members, Capacity, Base; public Bounds World; public bool BoundsDirty = true, BoundsSet; public int Signature;
        }
        private sealed class Patch
        {
            public InstanceRenderer R; public int Row = -1, Slot = -1, Class = -1, Count; public Kind Kind; public bool Pending; public Vector4 Sphere;
            public int Born; public bool Retired; public long MapKey; public float Gen;
            public ulong Hash; public bool HasHash;      // round 2: the blades the row holds (same-blades skip, gate verify)
            public bool Recycled; public int StableSweeps; // round 2: the 64 KB array went to EBM's pool, m_instances points at the sink
            public uint Flags; public float LodMin, LodMax;   // what the row was written with (the sweep compares)
        }

        private static readonly Dictionary<InstanceRenderer, Patch> s_taken = new Dictionary<InstanceRenderer, Patch>();
        private static readonly List<Patch> s_pending = new List<Patch>();
        private static readonly Dictionary<(Mesh, Material, ShadowCastingMode), Kind> s_kinds = new Dictionary<(Mesh, Material, ShadowCastingMode), Kind>();
        private static readonly List<Kind> s_kindList = new List<Kind>();
        private static readonly Patch[] s_rows = new Patch[MaxPatches];
        private static readonly Stack<int> s_freeRows = new Stack<int>();
        private static int s_rowHigh;
        private static readonly Stack<int>[] s_freeSlots = { new Stack<int>(), new Stack<int>(), new Stack<int>(), new Stack<int>(), new Stack<int>(), new Stack<int>(), new Stack<int>() };
        private static int s_slotTop, s_slotCap;
        private static readonly Dictionary<Material, bool> s_isGrass = new Dictionary<Material, bool>();
        // live patches by (patch centre, kind), chained in take order: a rebuild's new renderer finds the one it replaces
        private static readonly Dictionary<long, List<Patch>> s_byPlace = new Dictionary<long, List<Patch>>();
        private static float s_gen;   // generation counter (float: exact to 2^24, wraps well before)

        private static ComputeShader s_cs; private static Shader s_shader;
        private static int k_Clear = -1, k_Cull = -1, k_Expand = -1, k_Copy = -1;
        private static GraphicsBuffer s_o2w, s_patch, s_state, s_draw, s_args, s_kindBase, s_visible, s_stats;
        private static bool s_on, s_looked, s_layoutDirty = true, s_hooked, s_fault;
        private static string s_why = "";
        private static int s_layer = -1;

        private static readonly int IdO2W = Shader.PropertyToID("_EbmGrassO2W"), IdVisible = Shader.PropertyToID("_EbmGrassVisible"), IdBase = Shader.PropertyToID("_EbmGrassBase");
        private static readonly int IdPatch = Shader.PropertyToID("_EbmGrassPatch"), IdState = Shader.PropertyToID("_EbmGrassState"), IdDraw = Shader.PropertyToID("_EbmGrassDraw");
        private static readonly int IdArgs = Shader.PropertyToID("_EbmGrassArgs"), IdKindBase = Shader.PropertyToID("_EbmGrassKindBase"), IdVisibleOut = Shader.PropertyToID("_EbmGrassVisibleOut");
        private static readonly int IdCopySrc = Shader.PropertyToID("_EbmGrassCopySrc"), IdCopyDst = Shader.PropertyToID("_EbmGrassCopyDst"), IdCopyCount = Shader.PropertyToID("_EbmGrassCopyCount");
        private static readonly int IdPlanes = Shader.PropertyToID("_EbmGrassPlanes"), IdCam = Shader.PropertyToID("_EbmGrassCam");
        private static readonly int IdPatchCount = Shader.PropertyToID("_EbmGrassPatchCount"), IdKindCount = Shader.PropertyToID("_EbmGrassKindCount");
        private static readonly int IdStats = Shader.PropertyToID("_EbmGrassStats");

        // 64 B, the compute's GrassPatch. LodMax.y = generation (11-09)
        private struct PatchGpu { public Vector4 Sphere, PosLodMin, LodMax; public uint Flags, Count, Slot, Kind; }
        private static readonly PatchGpu[] s_rowScratch = new PatchGpu[1];
        private static readonly Vector2[] s_stateScratch = new Vector2[1];
        private static readonly Vector4[] s_rowsScratch = new Vector4[1023 * 3];
        private static readonly uint[] s_argScratch = new uint[ArgWords];
        private static readonly uint[] s_statsZero = new uint[2];
        private static readonly Plane[] s_planes = new Plane[6];
        private static readonly Vector4[] s_planes4 = new Vector4[6];

        // ── switching on / off ─────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static float s_nextTry;
        // from MonoUpdaters' InstanceRenderer pass (once a frame), at most every 5 s while it cannot start
        private static bool TryStart()
        {
            if (s_on) return true;
            if (s_fault || !KeyOn || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || Time.unscaledTime < s_nextTry) return false;
            s_nextTry = Time.unscaledTime + 5f;
            if (!SystemInfo.supportsComputeShaders) { Say("off: this GPU has no compute shaders (vanilla grass)"); return false; }
            if (!s_looked)
            {
                s_looked = true;
                s_cs = EbmIndirectAssets.BundleCompute("EbmGrass");
                s_shader = EbmIndirectAssets.BundleShader("EBM/GrassIndirect");
                if (s_cs != null)
                {
                    try { k_Clear = s_cs.FindKernel("EbmGrassClear"); k_Cull = s_cs.FindKernel("EbmGrassCull"); k_Expand = s_cs.FindKernel("EbmGrassExpand"); k_Copy = s_cs.FindKernel("EbmGrassCopy"); }
                    catch (Exception e) { s_cs = null; EasyBakeLog.Info("[Indirect] grass: the EbmGrass kernels did not load (" + e.Message + ")"); }
                }
            }
            if (s_cs == null || s_shader == null || !s_shader.isSupported) { Say("off: EBM's bundle has no EbmGrass compute / EBM/GrassIndirect shader (vanilla grass)"); return false; }
            string foreign = ForeignOwner();
            if (foreign != null) { Say("off: " + foreign + " also patches InstanceRenderer (vanilla grass, left to it)"); return false; }
            if (s_layer < 0) s_layer = LayerMask.NameToLayer("InstanceRenderer");
            s_on = true;
            HookCamera();
            // renderers that exist already (the key turned on in play): take them now, seeded from the fade they were drawing
            var list = InstanceRenderer.Instances;
            for (int i = list.Count - 1; i >= 0; i--) if (list[i] is InstanceRenderer r && r != null && r.isActiveAndEnabled) Take(r);
            return true;
        }

        private static void Stop(string why)
        {
            if (!s_on) return;
            s_on = false;
            int n = 0;
            var cam = Utils.GetMainCamera();
            foreach (var p in s_taken.Values)
            {
                var r = p.R;
                if (r == null) continue;
                if (r.isActiveAndEnabled && !InstanceRenderer.Instances.Contains(r)) InstanceRenderer.Instances.Add(r);
                // a recycled renderer's blades live only on the GPU: give it an empty array (draws nothing) until the rebuild below refills it
                if (p.Recycled) { F_Instances(r) = new Matrix4x4[1024]; p.Recycled = false; }
                // vanilla resumes from its own m_lodCount: give it the target for where the patch is now, so it does not fade in again
                try { if (r.m_useLod && cam != null) { F_LodCount(r) = VanillaTarget(r, cam.transform.position); F_FirstFrame(r) = false; } } catch { }
                n++;
            }
            ReleaseAll();
            if (s_anyRecycled)
            {
                s_anyRecycled = false;
                // vanilla's own full rebuild (the settings-change path): every patch rebuilt with real arrays; it fades in once
                try { if (ClutterSystem.instance != null) ClutterSystem.instance.ClearAll(); } catch { }
                Say("recycled grass arrays handed back: grass rebuilt once");
            }
            s_pool.Clear();
            Say($"off ({why}): {n} grass renderer(s) handed back to the game");
        }

        private static void ReleaseAll()
        {
            s_taken.Clear(); s_pending.Clear(); s_freeRows.Clear(); s_rowHigh = 0; Array.Clear(s_rows, 0, s_rows.Length); s_byPlace.Clear();
            foreach (var st in s_freeSlots) st.Clear();
            s_slotTop = 0; s_slotCap = 0;
            foreach (var k in s_kindList) if (k.Clone != null) UnityEngine.Object.Destroy(k.Clone);
            s_kinds.Clear(); s_kindList.Clear();
            Release(ref s_o2w); Release(ref s_patch); Release(ref s_state); Release(ref s_draw); Release(ref s_args); Release(ref s_kindBase); Release(ref s_visible); Release(ref s_stats);
            s_layoutDirty = true;
        }

        private static void Release(ref GraphicsBuffer b) { if (b != null) { b.Release(); b = null; } }

        private static string s_lastSay = "";
        private static void Say(string what) { if (what == s_lastSay) return; s_lastSay = what; EasyBakeLog.Info("[Indirect] grass: " + what); }

        // another mod patching what we replace = vanilla's path, left to it (checked at start and every 30 s)
        private static string ForeignOwner()
        {
            foreach (var m in new[] { AccessTools.Method(typeof(InstanceRenderer), "CustomUpdate"), AccessTools.Method(typeof(InstanceRenderer), "OnEnable") })
            {
                if (m == null) continue;
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var o in info.Owners) if (o != FiresEasyBakeMeshesPlugin.PluginGUID) return o;
            }
            return null;
        }

        // ── feed ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static bool IsGrass(Material m)
        {
            if (m == null) return false;
            if (s_isGrass.TryGetValue(m, out bool g)) return g;
            g = m.shader != null && m.shader.name == "Custom/Grass";
            if (s_isGrass.Count > 2048) s_isGrass.Clear();
            s_isGrass[m] = g;
            return g;
        }

        private static long PlaceKey(Vector3 pos, Kind k) =>
            ((long)(Mathf.RoundToInt(pos.x * 4f) & 0xFFFFFF) << 32) ^ ((long)(Mathf.RoundToInt(pos.z * 4f) & 0xFFFFFF) << 8) ^ (long)k.Index;

        private static bool s_saidNoCaster;
        private static void Take(InstanceRenderer r)
        {
            if (r == null || s_taken.ContainsKey(r) || r.m_mesh == null || !IsGrass(r.m_material)) return;
            if (s_taken.Count >= MaxPatches) { s_overLimit++; return; }
            var kind = KindFor(r);
            if (kind == null) { s_overLimit++; return; }
            if (kind.Shadow != ShadowCastingMode.Off && kind.Clone.FindPass("ShadowCaster") < 0)
            {
                s_stayedNoCaster++;
                if (!s_saidNoCaster) { s_saidNoCaster = true; EasyBakeLog.Info($"[Indirect] grass: '{r.name}' casts shadows but EBM's grass shader in this bundle has no caster pass; renderers that cast stay with the game"); }
                return;
            }
            InstanceRenderer.Instances.Remove(r);   // vanilla's per-frame CustomUpdate no longer runs for it
            var p = new Patch { R = r, Kind = kind, Born = Time.frameCount };
            p.MapKey = PlaceKey(r.transform.position, kind);   // vanilla instantiates the renderer at the patch centre (ClutterSystem GenerateVegPatch)
            s_taken[r] = p;
            kind.Members++;
            if (!(CarryOn && Inherit(p))) { p.Gen = NextGen(); s_fresh++; }
            if (!s_byPlace.TryGetValue(p.MapKey, out var chain)) s_byPlace[p.MapKey] = chain = new List<Patch>(2);
            chain.Add(p);
            MarkPending(p);
        }

        private static float NextGen() { s_gen = s_gen >= 8000000f ? 1f : s_gen + 1f; return s_gen; }

        // a rebuild: the new renderer is created while the old one is alive (ClutterSystem.GeneratePatch builds, then destroys the old patch's objects)
        private static bool Inherit(Patch p)
        {
            if (!s_byPlace.TryGetValue(p.MapKey, out var chain)) return false;
            for (int i = 0; i < chain.Count; i++)
            {
                var old = chain[i];
                if (old.Retired || old.Born >= p.Born || old.R == null) continue;
                chain.RemoveAt(i);
                old.Retired = true;
                if (old.Pending) { old.Pending = false; s_pending.Remove(old); }
                // the row, slots, count, sphere and generation move over; the old patch frees nothing when Unity destroys it
                p.Row = old.Row; p.Slot = old.Slot; p.Class = old.Class; p.Count = old.Count; p.Sphere = old.Sphere; p.Gen = old.Gen;
                p.Flags = old.Flags; p.LodMin = old.LodMin; p.LodMax = old.LodMax; p.Hash = old.Hash; p.HasHash = old.HasHash;
                if (p.Row >= 0) s_rows[p.Row] = p;
                old.Row = -1; old.Slot = -1; old.Class = -1;
                s_carried++;
                return true;
            }
            return false;
        }

        private static void MarkPending(Patch p) { if (!p.Pending && !p.Retired) { p.Pending = true; s_pending.Add(p); } }

        private static Kind KindFor(InstanceRenderer r)
        {
            var key = (r.m_mesh, r.m_material, r.m_shadowCasting);
            if (s_kinds.TryGetValue(key, out var k)) return k;
            if (s_kindList.Count >= MaxKinds) return null;
            k = new Kind { Index = s_kindList.Count, Mesh = r.m_mesh, Source = r.m_material, Shadow = r.m_shadowCasting, Props = new MaterialPropertyBlock() };
            k.Clone = new Material(r.m_material) { name = r.m_material.name + " (EBM grass)", shader = s_shader, enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
            k.Clone.renderQueue = r.m_material.renderQueue;   // 11-09: setting .shader can reset the queue; FF moves clutter materials' queue
            k.Signature = Signature(r.m_material);
            s_kinds[key] = k; s_kindList.Add(k);
            s_layoutDirty = true;
            if (k.Index == 0 || k.Clone.renderQueue != r.m_material.renderQueue)
                EasyBakeLog.Debug($"[Indirect] grass: kind '{r.m_material.name}' queue {r.m_material.renderQueue} -> clone {k.Clone.renderQueue}, shadows {k.Shadow}");
            return k;
        }

        // the source material's look, cheaply: re-copied to the clone when it changes (Vedr's season colours, a mod editing the grass material).
        // 11-09: + the six floats EBM/GrassIndirect reads, and the shader (a swap away from Custom/Grass hands the kind's renderers back).
        private static int Signature(Material m)
        {
            unchecked
            {
                int h = 17;
                var t = m.GetTexture("_MainTex"); h = h * 31 + (t != null ? t.GetInstanceID() : 0);
                var tc = m.HasProperty("_TerrainColorTex") ? m.GetTexture("_TerrainColorTex") : null; h = h * 31 + (tc != null ? tc.GetInstanceID() : 0);
                h = h * 31 + m.color.GetHashCode();
                if (m.HasProperty("_EmissionColor")) h = h * 31 + m.GetColor("_EmissionColor").GetHashCode();
                foreach (var id in s_sigFloats) if (m.HasProperty(id)) h = h * 31 + m.GetFloat(id).GetHashCode();
                h = h * 31 + (m.shader != null ? m.shader.GetInstanceID() : 0);
                h = h * 31 + m.renderQueue;
                return h;
            }
        }
        private static readonly int[] s_sigFloats =
        {
            Shader.PropertyToID("_Height"), Shader.PropertyToID("_SwaySpeed"), Shader.PropertyToID("_SwayDistance"),
            Shader.PropertyToID("_PushDistance"), Shader.PropertyToID("_Cutoff"), Shader.PropertyToID("_TerrainColorScale"),
        };

        private static void Forget(InstanceRenderer r)
        {
            if (r == null || !s_taken.TryGetValue(r, out var p)) return;
            s_taken.Remove(r);
            if (p.Pending) s_pending.Remove(p);
            if (!p.Retired && s_byPlace.TryGetValue(p.MapKey, out var chain)) { chain.Remove(p); if (chain.Count == 0) s_byPlace.Remove(p.MapKey); }
            FreeRow(p); FreeSlots(p);   // a retired patch holds neither (its successor took them)
            if (p.Kind != null) { p.Kind.Members--; p.Kind.BoundsDirty = true; }
            s_layoutDirty = true;
        }

        // hand one renderer back to vanilla (its material stopped being Custom/Grass), keeping the share it was drawing
        private static void GiveBack(Patch p)
        {
            var r = p.R;
            bool recycled = p.Recycled;
            Forget(r);
            if (r == null) return;
            if (recycled)
            {
                F_Instances(r) = new Matrix4x4[1024];   // empty until the patch is rebuilt
                if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(r.transform.position, 0.5f);
            }
            if (r.isActiveAndEnabled && !InstanceRenderer.Instances.Contains(r)) InstanceRenderer.Instances.Add(r);
            var cam = Utils.GetMainCamera();
            try { if (r.m_useLod && cam != null) { F_LodCount(r) = VanillaTarget(r, cam.transform.position); F_FirstFrame(r) = false; } } catch { }
            s_givenBack++;
        }

        // ── slots and rows ──────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static int ClassFor(int count) { for (int c = 0; c < ClassSize.Length; c++) if (count <= ClassSize[c]) return c; return ClassSize.Length - 1; }

        private static bool AllocSlots(Patch p, int count)
        {
            int c = ClassFor(Mathf.Max(1, count));
            if (p.Slot >= 0 && p.Class == c) return true;
            FreeSlots(p);
            int slot;
            if (s_freeSlots[c].Count > 0) slot = s_freeSlots[c].Pop();
            else
            {
                int need = s_slotTop + ClassSize[c];
                if (need > s_slotCap && !Grow(need)) return false;
                slot = s_slotTop; s_slotTop = need;
            }
            p.Slot = slot; p.Class = c;
            s_layoutDirty = true;
            return true;
        }

        private static void FreeSlots(Patch p) { if (p.Slot >= 0) { s_freeSlots[p.Class].Push(p.Slot); p.Slot = -1; p.Class = -1; } }

        private static int AllocRow(Patch p)
        {
            if (p.Row >= 0) return p.Row;   // a carried row keeps its fade share and generation
            int row = s_freeRows.Count > 0 ? s_freeRows.Pop() : s_rowHigh < MaxPatches ? s_rowHigh++ : -1;
            if (row < 0) return -1;
            p.Row = row; s_rows[row] = p;
            // fresh: share 0, last generation seen -1 (so the compute treats the next on-screen frame as vanilla's first frame).
            // A renderer vanilla already drew (key turned on in play) starts at the share it was drawing.
            float share = 0f;
            var r = p.R;
            try { if (r != null && r.m_useLod && !F_FirstFrame(r)) { int n = F_Count(r); share = n > 0 ? Mathf.Clamp01(F_LodCount(r) / n) : 0f; } } catch { }
            s_stateScratch[0] = new Vector2(share, -1f);
            s_state.SetData(s_stateScratch, 0, row, 1);
            return row;
        }

        private static void FreeRow(Patch p)
        {
            if (p.Row < 0) return;
            s_rowScratch[0] = default;   // Flags 0 = not live
            s_patch.SetData(s_rowScratch, 0, p.Row, 1);
            s_rows[p.Row] = null; s_freeRows.Push(p.Row); p.Row = -1;
        }

        // the instance pool: doubled on demand, the old rows kept by a GPU copy (EbmGrassCopy)
        private static bool Grow(int need)
        {
            int cap = Mathf.Max(16384, s_slotCap);
            while (cap < need) cap *= 2;
            if (cap == s_slotCap) return true;
            var o2w = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap * 3, 16);
            if (s_o2w != null && s_slotTop > 0)
            {
                s_cs.SetBuffer(k_Copy, IdCopySrc, s_o2w);
                s_cs.SetBuffer(k_Copy, IdCopyDst, o2w);
                s_cs.SetInt(IdCopyCount, s_slotTop * 3);
                s_cs.Dispatch(k_Copy, Mathf.Clamp(Mathf.CeilToInt(s_slotTop * 3 / 64f), 1, 65535), 1, 1);
            }
            Release(ref s_o2w); s_o2w = o2w;
            Release(ref s_visible); s_visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, 4);
            s_slotCap = cap;
            s_layoutDirty = true;
            EasyBakeLog.Debug($"[Indirect] grass: instance pool {cap} slot(s) ({cap * 52 / 1048576f:0.0} MB with the visible list)");
            return true;
        }

        private static void EnsureFixedBuffers()
        {
            if (s_patch != null) return;
            s_patch = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxPatches, 64);
            s_patch.SetData(new PatchGpu[MaxPatches]);
            s_state = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxPatches, 8);
            s_draw = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxPatches, 8);
            s_args = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments, MaxKinds * ArgWords, 4);
            s_args.SetData(new uint[MaxKinds * ArgWords]);
            s_kindBase = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxKinds, 4);
            s_stats = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);   // 11-09 self-check: patches drawn, blades drawn
            s_stats.SetData(s_statsZero);
            if (s_o2w == null) Grow(16384);
        }

        // ── upload: one renderer's matrices and row (vanilla's UpdateBounds for the sphere) ─────────────────────────────────────────────
        private static bool Upload(Patch p)
        {
            var r = p.R;
            if (r == null || p.Retired) return true;
            if (p.Recycled) return true;   // its blades are on the GPU already; a real change gives it an array back first (prefixes below)
            int count = Mathf.Min(F_Count(r), 1023);
            var m = F_Instances(r);
            // round 2 (design 4.4): the blades' hash first. A rebuild that produced the same blades as the row already holds (the inherited row
            // of a carried patch, or a re-upload with nothing changed) writes nothing: no SetData, no sphere, no relayout.
            ulong hash = HashBlades(m, count);
            bool sameRow = p.HasHash && hash == p.Hash && count == p.Count && p.Row >= 0 && (count == 0 || (p.Slot >= 0 && p.Class == ClassFor(count)));
            if (p.HasHash && InPredicted(r.transform.position)) { EbmGrassStepGate.VerifyChecked++; if (hash != p.Hash || count != p.Count) EbmGrassStepGate.VerifyDiffered++; }
            if (sameRow && FlagsOf(r) == p.Flags && r.m_lodMinDistance == p.LodMin && r.m_lodMaxDistance == p.LodMax) { s_sameBlades++; return true; }
            p.Count = count; p.Hash = hash; p.HasHash = true;
            if (AllocRow(p) < 0) { s_overLimit++; return true; }
            if (count == 0)
            {
                WriteRow(p, default, 0);
                return true;
            }
            if (!AllocSlots(p, count)) return false;
            float ext = r.m_mesh.bounds.extents.magnitude;
            Vector3 lo = new Vector3(9999999f, 9999999f, 9999999f), hi = -lo;
            for (int i = 0; i < count; i++)
            {
                var x = m[i];
                s_rowsScratch[i * 3] = new Vector4(x.m00, x.m01, x.m02, x.m03);
                s_rowsScratch[i * 3 + 1] = new Vector4(x.m10, x.m11, x.m12, x.m13);
                s_rowsScratch[i * 3 + 2] = new Vector4(x.m20, x.m21, x.m22, x.m23);
                Vector3 pos = new Vector3(x.m03, x.m13, x.m23), sc = x.lossyScale;
                float e = Mathf.Max(Mathf.Max(sc.x, sc.y), sc.z) * ext;
                var ev = new Vector3(e, e, e);
                hi = Vector3.Max(hi, pos + ev); lo = Vector3.Min(lo, pos - ev);
            }
            s_o2w.SetData(s_rowsScratch, 0, p.Slot * 3, count * 3);
            Vector3 c = (hi + lo) * 0.5f;
            WriteRow(p, new Vector4(c.x, c.y, c.z, Vector3.Distance(hi, c)), count);
            s_uploads++; s_uploadedInst += count;
            return true;
        }

        // FNV-1a over the 12 floats that reach the GPU per blade (bit patterns, so -0 / NaN are exact)
        private static ulong HashBlades(Matrix4x4[] m, int count)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL ^ (ulong)count;
                for (int i = 0; i < count; i++)
                {
                    var x = m[i];
                    h = Mix(h, x.m00); h = Mix(h, x.m01); h = Mix(h, x.m02); h = Mix(h, x.m03);
                    h = Mix(h, x.m10); h = Mix(h, x.m11); h = Mix(h, x.m12); h = Mix(h, x.m13);
                    h = Mix(h, x.m20); h = Mix(h, x.m21); h = Mix(h, x.m22); h = Mix(h, x.m23);
                }
                return h;
            }
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatBits { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public uint U; }
        private static ulong Mix(ulong h, float f) { unchecked { var b = new FloatBits { F = f }; return (h ^ b.U) * 1099511628211UL; } }

        // the step gate's verify: footprints it judged "no change" (rebuilt anyway in verify mode), held 10 s
        private static readonly List<Vector4> s_predicted = new List<Vector4>();
        internal static void PredictSame(Vector3 c, float r)
        {
            if (s_predicted.Count > 64) s_predicted.RemoveAt(0);
            s_predicted.Add(new Vector4(c.x, c.z, r + 4f, Time.unscaledTime + 10f));   // + half a patch: the patch centre may sit outside the op's circle
        }
        private static bool InPredicted(Vector3 p)
        {
            if (s_predicted.Count == 0) return false;
            float now = Time.unscaledTime;
            for (int i = s_predicted.Count - 1; i >= 0; i--)
            {
                var q = s_predicted[i];
                if (q.w < now) { s_predicted.RemoveAt(i); continue; }
                if (Mathf.Abs(p.x - q.x) <= q.z && Mathf.Abs(p.z - q.y) <= q.z) return true;
            }
            return false;
        }

        private static uint FlagsOf(InstanceRenderer r) => 8u | (r.m_frustumCull ? 1u : 0u) | (r.m_useLod ? 2u : 0u) | (r.m_useXZLodDistance ? 4u : 0u);

        private static void WriteRow(Patch p, Vector4 sphere, int count)
        {
            var r = p.R;
            var pos = r.transform.position;
            uint flags = FlagsOf(r);
            p.Flags = flags; p.LodMin = r.m_lodMinDistance; p.LodMax = r.m_lodMaxDistance;
            s_rowScratch[0] = new PatchGpu
            {
                Sphere = sphere, PosLodMin = new Vector4(pos.x, pos.y, pos.z, r.m_lodMinDistance), LodMax = new Vector4(r.m_lodMaxDistance, p.Gen, 0f, 0f),
                Flags = count > 0 ? flags : 0u, Count = (uint)count, Slot = (uint)Mathf.Max(0, p.Slot), Kind = (uint)p.Kind.Index,
            };
            s_patch.SetData(s_rowScratch, 0, p.Row, 1);
            p.Sphere = sphere;
            p.Kind.BoundsDirty = true;
            s_layoutDirty = true;
        }

        // kind bases (sum of member capacities), static args words, bindings: only when the layout changed
        private static readonly uint[] s_baseScratch = new uint[MaxKinds];
        private static void Relayout()
        {
            // capacities and bounds from the members (bounds: the union of the members' spheres, rebuilt for a kind that changed)
            foreach (var k in s_kindList) { k.Capacity = 0; k.BoundsSet = false; }
            foreach (var p in s_taken.Values)
            {
                var k = p.Kind;
                if (k == null || p.Count <= 0 || p.Retired) continue;
                k.Capacity += p.Count;
                if (!k.BoundsDirty) continue;
                var b = new Bounds(new Vector3(p.Sphere.x, p.Sphere.y, p.Sphere.z), Vector3.one * (p.Sphere.w * 2f));
                if (!k.BoundsSet) { k.World = b; k.BoundsSet = true; } else k.World.Encapsulate(b);
            }
            foreach (var k in s_kindList) k.BoundsDirty = false;
            int at = 0;
            foreach (var k in s_kindList)
            {
                k.Base = at; s_baseScratch[k.Index] = (uint)at; at += k.Capacity;
                var mesh = k.Mesh;
                if (mesh != null)
                {
                    s_argScratch[0] = mesh.GetIndexCount(0); s_argScratch[1] = 0u; s_argScratch[2] = mesh.GetIndexStart(0); s_argScratch[3] = mesh.GetBaseVertex(0); s_argScratch[4] = 0u;
                    s_args.SetData(s_argScratch, 0, k.Index * ArgWords, ArgWords);
                }
                k.Props.SetBuffer(IdO2W, s_o2w);
                k.Props.SetBuffer(IdVisible, s_visible);
                k.Props.SetInteger(IdBase, k.Base);
            }
            // the visible list (s_slotCap entries) holds every instance once at most: sum of counts <= slots in use <= s_slotCap.
            // A carried row keeps its old (possibly larger) count until its upload: the old count was already inside the old capacity sum,
            // and the successor replaces it in the same sum, so the bound holds.
            s_kindBase.SetData(s_baseScratch, 0, 0, s_kindList.Count);
            s_layoutDirty = false;
        }

        // ── the frame ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static void HookCamera()
        {
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += OnPreCull;
        }

        private static int s_lastFrame = -1;
        private static float s_nextSweep, s_nextForeign;
        private static void OnPreCull(Camera cam)
        {
            if (!s_on || cam == null || Time.frameCount == s_lastFrame || cam != Utils.GetMainCamera()) return;
            s_lastFrame = Time.frameCount;
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                if (!KeyOn) { Stop("key off"); return; }
                float now = Time.unscaledTime;
                if (now >= s_nextForeign) { s_nextForeign = now + 30f; string f = ForeignOwner(); if (f != null) { Stop(f + " also patches InstanceRenderer"); return; } }
                EnsureFixedBuffers();
                if (now >= s_nextSweep) Sweep(now);

                // pending uploads, within the budget (checked inside the loop)
                long end = t0 + (long)(UploadMs * Stopwatch.Frequency / 1000.0);
                int done = 0;
                for (; done < s_pending.Count; done++)
                {
                    var p = s_pending[done];
                    p.Pending = false;
                    if (!Upload(p)) { p.Pending = true; break; }
                    if (Stopwatch.GetTimestamp() >= end) { done++; break; }
                }
                if (done > 0) s_pending.RemoveRange(0, done);
                if (s_layoutDirty) Relayout();
                if (s_kindList.Count == 0 || s_rowHigh == 0) return;

                // GPU: clear the kinds' counts, cull + fade every patch, expand the visible ranges
                GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
                for (int i = 0; i < 6; i++) s_planes4[i] = new Vector4(s_planes[i].normal.x, s_planes[i].normal.y, s_planes[i].normal.z, s_planes[i].distance);
                var cp = cam.transform.position;
                s_cs.SetVectorArray(IdPlanes, s_planes4);
                s_cs.SetVector(IdCam, new Vector4(cp.x, cp.y, cp.z, Time.deltaTime));
                s_cs.SetInt(IdPatchCount, s_rowHigh);
                s_cs.SetInt(IdKindCount, s_kindList.Count);
                s_cs.SetBuffer(k_Clear, IdArgs, s_args); s_cs.SetBuffer(k_Clear, IdStats, s_stats);
                s_cs.Dispatch(k_Clear, Mathf.Max(1, Mathf.CeilToInt(s_kindList.Count / 64f)), 1, 1);
                s_cs.SetBuffer(k_Cull, IdPatch, s_patch); s_cs.SetBuffer(k_Cull, IdState, s_state); s_cs.SetBuffer(k_Cull, IdDraw, s_draw); s_cs.SetBuffer(k_Cull, IdArgs, s_args); s_cs.SetBuffer(k_Cull, IdStats, s_stats);
                s_cs.Dispatch(k_Cull, Mathf.CeilToInt(s_rowHigh / 64f), 1, 1);
                s_cs.SetBuffer(k_Expand, IdPatch, s_patch); s_cs.SetBuffer(k_Expand, IdDraw, s_draw); s_cs.SetBuffer(k_Expand, IdKindBase, s_kindBase); s_cs.SetBuffer(k_Expand, IdVisibleOut, s_visible);
                s_cs.Dispatch(k_Expand, s_rowHigh, 1, 1);

                // one draw per kind, with the kind's own shadow mode (vanilla passes m_shadowCasting)
                foreach (var k in s_kindList)
                {
                    if (k.Members <= 0 || k.Mesh == null || k.Clone == null || k.Source == null) continue;
                    var rp = new RenderParams(k.Clone)
                    {
                        worldBounds = k.World, layer = s_layer, shadowCastingMode = k.Shadow, receiveShadows = true,
                        matProps = k.Props, lightProbeUsage = LightProbeUsage.BlendProbes, reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
                    };
                    Graphics.RenderMeshIndirect(in rp, k.Mesh, s_args, 1, k.Index);
                    s_draws++;
                    if (IndirectStats.SplitOn) IndirectStats.NoteSplitGrass(1);   // 1.2.254
                }
                MaybeProve(cam);
            }
            catch (Exception e) { s_fault = true; Stop("threw: " + e.GetType().Name + ": " + e.Message); }
            finally { s_oursTicks += Stopwatch.GetTimestamp() - t0; s_oursFrames++; MaybeSay(); }
        }

        // once a second: a count, flag or LOD distance changed outside generation; the source material's look or shader changed
        private static readonly List<Patch> s_giveBack = new List<Patch>();
        private static void Sweep(float now)
        {
            s_nextSweep = now + 1f;
            foreach (var p in s_taken.Values)
            {
                var r = p.R;
                if (r == null || p.Pending || p.Retired) continue;
                if (F_Count(r) != p.Count)
                {
                    s_sweepCount++;
                    // a recycled renderer's blades were written into the sink (an AddInstance outside generation): rebuild that patch the vanilla way
                    if (p.Recycled) { if (ClutterSystem.instance != null) ClutterSystem.instance.ResetGrass(r.transform.position, 0.5f); s_recycleRebuilds++; }
                    else MarkPending(p);
                    p.StableSweeps = 0;
                    continue;
                }
                if (p.Row >= 0 && (FlagsOf(r) != p.Flags || r.m_lodMinDistance != p.LodMin || r.m_lodMaxDistance != p.LodMax)) { MarkPending(p); s_sweepFlags++; p.StableSweeps = 0; continue; }
                // round 2 (design 4.5): stable through a whole sweep = its 64 KB array goes to EBM's pool; m_instances points at the 1023-long sink
                // (AddInstance stops at 1023, so a stray write stays inside it; FF's pool only takes 1024-long arrays, so it is never returned twice)
                if (RecycleOn && !p.Recycled && p.Row >= 0 && p.HasHash && ++p.StableSweeps >= 1)
                {
                    var a = F_Instances(r);
                    if (a != null && a.Length == 1024)
                    {
                        if (s_pool.Count < PoolCap) { s_pool.Push(a); }
                        F_Instances(r) = s_sink;
                        p.Recycled = true; s_anyRecycled = true; s_recycled++;
                    }
                }
            }
            TrimPool(now);
            s_giveBack.Clear();
            foreach (var k in s_kindList)
            {
                if (k.Source == null || k.Clone == null) continue;
                int sig = Signature(k.Source);
                if (sig == k.Signature) continue;
                k.Signature = sig;
                s_isGrass.Remove(k.Source);
                if (!IsGrass(k.Source))
                {
                    foreach (var p in s_taken.Values) if (p.Kind == k) s_giveBack.Add(p);
                    continue;
                }
                k.Clone.CopyPropertiesFromMaterial(k.Source); k.Clone.shader = s_shader; k.Clone.renderQueue = k.Source.renderQueue;
            }
            foreach (var p in s_giveBack) GiveBack(p);
            if (s_giveBack.Count > 0) EasyBakeLog.Info($"[Indirect] grass: {s_giveBack.Count} renderer(s) handed back to the game: their material is no longer Custom/Grass");
        }

        private static float VanillaTarget(InstanceRenderer r, Vector3 cam)
        {
            var pos = r.transform.position;
            float v = r.m_useXZLodDistance ? Utils.DistanceXZ(cam, pos) : Vector3.Distance(cam, pos);
            int count = F_Count(r);
            return Mathf.Min((int)((1f - Utils.LerpStep(r.m_lodMinDistance, r.m_lodMaxDistance, v)) * count), count);
        }

        // ── census + proof (Debug, 30 s) ─────────────────────────────────────────────────────────────────────────────────────────────────
        private static long s_vanillaTicks, s_oursTicks; private static int s_vanillaFrames, s_oursFrames, s_vanillaRenderers, s_draws, s_uploads, s_overLimit; private static long s_uploadedInst;
        private static int s_carried, s_fresh, s_stayedNoCaster, s_givenBack, s_sweepCount, s_sweepFlags;
        private static float s_nextSay;
        private static long s_passStart;

        [HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomUpdate))]
        private static class MonoUpdaters_InstanceRendererPass
        {
            private static void Prefix(List<IMonoUpdater> source) { if (ReferenceEquals(source, InstanceRenderer.Instances)) s_passStart = Stopwatch.GetTimestamp(); }
            private static void Postfix(List<IMonoUpdater> source)
            {
                if (!ReferenceEquals(source, InstanceRenderer.Instances) || s_passStart == 0) return;
                s_vanillaTicks += Stopwatch.GetTimestamp() - s_passStart; s_vanillaFrames++; s_vanillaRenderers += source.Count; s_passStart = 0;
                if (!s_on) { MaybeSay(); if (KeyOn && !s_fault) TryStart(); }
            }
        }

        private static void MaybeSay()
        {
            float now = Time.unscaledTime;
            if (now < s_nextSay) return;
            if (s_nextSay > 0f && EasyBakeLog.DebugOn())
            {
                double f = 1000.0 / Stopwatch.Frequency;
                double van = s_vanillaFrames > 0 ? s_vanillaTicks * f / s_vanillaFrames : 0, ours = s_oursFrames > 0 ? s_oursTicks * f / s_oursFrames : 0;
                int vanR = s_vanillaFrames > 0 ? s_vanillaRenderers / s_vanillaFrames : 0;
                long inst = 0; foreach (var p in s_taken.Values) if (!p.Retired) inst += p.Count;
                EasyBakeLog.Debug($"[Indirect] grass census 30 s: key {(s_on ? "ON" : "off")}; vanilla InstanceRenderer pass {van:0.000} ms/frame over {vanR} renderer(s) on its list" +
                    (s_on ? $"; EBM: {s_taken.Count} grass renderer(s) taken in {s_kindList.Count} kind(s) = {(s_oursFrames > 0 ? s_draws / s_oursFrames : 0)} draw(s)/frame, {inst} instance(s), uploads {s_uploads} ({s_uploadedInst} inst), {ours:0.000} ms/frame (uploads + dispatch + draws), pending {s_pending.Count}, over limit {s_overLimit}, pool {s_slotCap} slot(s) {s_slotCap * 52 / 1048576f:0.0} MB" +
                            $"; rebuilds carried {s_carried} (fade + row kept), fresh {s_fresh}, carry {(CarryOn ? "on" : "off")}; re-uploads from the sweep: count {s_sweepCount}, flags/LOD {s_sweepFlags}; given back (not Custom/Grass) {s_givenBack}; casting renderers left to the game (no caster pass) {s_stayedNoCaster}" +
                            $"; same-blades uploads skipped {s_sameBlades}; {EbmGrassStepGate.Census()}" +
                            $"; array recycle {(RecycleOn ? "on" : "off")}{(s_ctorPatched ? "" : " (rent standing aside)")}: recycled {s_recycled}, rented {s_rented}, restored for a write {s_restored}, patches rebuilt after a recycled write {s_recycleRebuilds}, pool {s_pool.Count} ({s_pool.Count * 64} KB)"
                          : ""));
            }
            s_nextSay = now + 30f;
            s_vanillaTicks = s_oursTicks = 0; s_vanillaFrames = s_oursFrames = s_vanillaRenderers = s_draws = s_uploads = 0; s_uploadedInst = 0;
            s_carried = s_fresh = s_givenBack = s_sweepCount = s_sweepFlags = 0;
            s_sameBlades = s_recycled = s_rented = s_restored = s_recycleRebuilds = 0; EbmGrassStepGate.ClearCounts();
        }

        // the same grass: what the GPU drew (patches + blades, async readback) vs vanilla's own answer for the same frame on the CPU
        private static float s_nextProve; private static bool s_proving;
        private static void MaybeProve(Camera cam)
        {
            if (s_proving || Time.unscaledTime < s_nextProve || !EasyBakeLog.DebugOn()) return;
            s_nextProve = Time.unscaledTime + 30f;
            s_proving = true;
            // vanilla's sphere test + settled fade target per taken renderer (equal to the GPU's once the fade has settled, i.e. standing still;
            // while moving the GPU lags toward it at vanilla's own rate). A patch counts when vanilla would issue its draw (target > 0).
            long cpuBlades = 0; int cpuPatches = 0, onScreen = 0;
            var cp = cam.transform.position;
            foreach (var p in s_taken.Values)
            {
                var r = p.R;
                if (r == null || p.Retired || p.Count <= 0) continue;
                if (r.m_frustumCull)
                {
                    bool inside = true;
                    for (int i = 0; i < 6 && inside; i++) if (s_planes[i].GetDistanceToPoint(new Vector3(p.Sphere.x, p.Sphere.y, p.Sphere.z)) < -p.Sphere.w) inside = false;
                    if (!inside) continue;
                }
                onScreen++;
                long n = r.m_useLod ? (long)VanillaTarget(r, cp) : p.Count;
                if (n > 0) { cpuPatches++; cpuBlades += n; }
            }
            int vanillaList = InstanceRenderer.Instances.Count;
            UnityEngine.Rendering.AsyncGPUReadback.Request(s_stats, 8, 0, req =>
            {
                s_proving = false;
                if (req.hasError || !s_on) return;
                var d = req.GetData<uint>();
                if (d.Length < 2) return;
                long gpuPatches = d[0], gpuBlades = d[1];
                EasyBakeLog.Debug($"[Indirect] grass self-check: EBM drew {gpuPatches} patch(es) / {gpuBlades} blade(s); vanilla for the same frame {cpuPatches} patch(es) / {cpuBlades} blade(s) " +
                    $"over {onScreen} on-screen renderer(s) ({(cpuPatches > 0 ? 100.0 * gpuPatches / cpuPatches : 100.0):0.0} % patches, {(cpuBlades > 0 ? 100.0 * gpuBlades / cpuBlades : 100.0):0.0} % blades; standing still = 100 %); " +
                    $"grass renderers still on vanilla's list {vanillaList}");
            });
        }

        // ── hooks on vanilla InstanceRenderer ──────────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(InstanceRenderer), "OnEnable")]
        private static class InstanceRenderer_OnEnable { private static void Postfix(InstanceRenderer __instance) { try { if (s_on) Take(__instance); } catch (Exception e) { EasyBakeLog.Debug("[Indirect] grass take threw: " + e.Message); } } }

        [HarmonyPatch(typeof(InstanceRenderer), "OnDisable")]
        private static class InstanceRenderer_OnDisable { private static void Postfix(InstanceRenderer __instance) { try { if (s_on) Forget(__instance); } catch (Exception e) { EasyBakeLog.Debug("[Indirect] grass forget threw: " + e.Message); } } }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.Clear))]
        private static class InstanceRenderer_Clear { private static void Postfix(InstanceRenderer __instance) => Changed(__instance); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstance))]
        private static class InstanceRenderer_SetInstance { private static void Postfix(InstanceRenderer __instance) => Changed(__instance); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstances))]
        private static class InstanceRenderer_SetInstances { private static void Postfix(InstanceRenderer __instance) => Changed(__instance); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstancesBillboard))]
        private static class InstanceRenderer_SetInstancesBillboard { private static void Postfix(InstanceRenderer __instance) => Changed(__instance); }

        private static void Changed(InstanceRenderer r) { if (s_on && r != null && s_taken.TryGetValue(r, out var p)) MarkPending(p); }

        // ── round 2: array recycle (design 4.5; key IndirectGrassArrayRecycle, off until the A/B) ─────────────────────────────────────────
        private const int PoolCap = 128;   // 128 x 64 KB = 8 MB at most
        private static readonly Stack<Matrix4x4[]> s_pool = new Stack<Matrix4x4[]>();
        private static readonly Matrix4x4[] s_sink = new Matrix4x4[1023];
        private static int s_mainThread = -1, s_poolLow = int.MaxValue;
        private static float s_nextTrim;
        private static bool s_anyRecycled, s_ctorPatched;
        private static int s_recycled, s_rented, s_restored, s_recycleRebuilds, s_sameBlades;

        /// <summary>Replaces InstanceRenderer's `newarr Matrix4x4` (the length is on the stack). Main thread only, only while EBM grass and the key are on.</summary>
        private static Matrix4x4[] Rent(int length)
        {
            if (length == 1024 && s_on && s_pool.Count > 0 && System.Threading.Thread.CurrentThread.ManagedThreadId == s_mainThread && RecycleOn)
            {
                s_rented++;
                var a = s_pool.Pop();
                if (s_pool.Count < s_poolLow) s_poolLow = s_pool.Count;
                return a;   // a reused array keeps old matrices past m_instanceCount; vanilla reads only below it (AddInstance writes first)
            }
            return new Matrix4x4[length];
        }

        private static void TrimPool(float now)
        {
            if (s_mainThread < 0) s_mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (now < s_nextTrim) return;
            s_nextTrim = now + 10f;
            int drop = !RecycleOn ? s_pool.Count : (s_poolLow == int.MaxValue ? s_pool.Count : s_poolLow) / 2;   // half of what stayed unused all window
            for (int i = 0; i < drop && s_pool.Count > 0; i++) s_pool.Pop();
            s_poolLow = s_pool.Count;
        }

        /// <summary>A recycled renderer about to be written gets a real array first (zeros: the writer fills what it needs).</summary>
        private static void Restore(InstanceRenderer r, bool keepsOldBlades)
        {
            if (r == null || !s_taken.TryGetValue(r, out var p) || !p.Recycled) return;
            F_Instances(r) = Rent(1024);
            p.Recycled = false; p.StableSweeps = 0; p.HasHash = false; s_restored++;
            // SetInstance writes one blade into an array whose others are gone: rebuild the patch the vanilla way
            if (keepsOldBlades && ClutterSystem.instance != null) { ClutterSystem.instance.ResetGrass(r.transform.position, 0.5f); s_recycleRebuilds++; }
        }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.Clear))]
        private static class InstanceRenderer_Clear_Restore { private static void Prefix(InstanceRenderer __instance) => Restore(__instance, false); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstance))]
        private static class InstanceRenderer_SetInstance_Restore { private static void Prefix(InstanceRenderer __instance) => Restore(__instance, true); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstances))]
        private static class InstanceRenderer_SetInstances_Restore { private static void Prefix(InstanceRenderer __instance) => Restore(__instance, false); }

        [HarmonyPatch(typeof(InstanceRenderer), nameof(InstanceRenderer.SetInstancesBillboard))]
        private static class InstanceRenderer_SetInstancesBillboard_Restore { private static void Prefix(InstanceRenderer __instance) => Restore(__instance, false); }

        // The rent: InstanceRenderer's field initializer. Priority.Last, so another mod that already replaced the allocation (FF's GrassInstancePool,
        // VCP's GrassInstanceMemory) is seen: no `newarr Matrix4x4` left = ours stands aside (one Info line); VCP's smaller first size reaches Rent
        // with a length other than 1024 and is allocated as asked.
        [HarmonyPatch(typeof(InstanceRenderer), MethodType.Constructor)]
        private static class InstanceRenderer_Ctor
        {
            [HarmonyTranspiler, HarmonyPriority(Priority.Last)]
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
            {
                var original = new List<CodeInstruction>(code);   // review 11-09: the unchanged copy, returned when we stand aside (no second enumeration, no half edit)
                var list = new List<CodeInstruction>(original.Count);
                foreach (var ci in original) list.Add(ci.Clone());
                var rent = AccessTools.Method(typeof(EbmIndirectGrass), nameof(Rent));
                int hits = 0;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].opcode == System.Reflection.Emit.OpCodes.Newarr && list[i].operand is Type t && t == typeof(Matrix4x4))
                    {
                        list[i] = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, rent) { labels = list[i].labels, blocks = list[i].blocks };
                        hits++;
                    }
                s_ctorPatched = hits == 1;
                if (hits != 1)
                {
                    EasyBakeLog.Info($"[Indirect] grass array recycle: InstanceRenderer's constructor has {hits} Matrix4x4 allocation(s) left (another mod already rents them, or a game change); EBM's rent stands aside, arrays given back to the pool are only released");
                    return original;
                }
                return list;
            }
        }

        /// <summary>World unload / quit.</summary>
        internal static void Shutdown() { try { if (s_on) Stop("world unloaded"); else ReleaseAll(); } catch { } }
    }
}
