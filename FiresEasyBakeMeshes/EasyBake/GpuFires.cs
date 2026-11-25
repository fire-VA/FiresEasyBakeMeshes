using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // [seasons] 11-11 (lead for Fire: "get that done"; Tools\PLAN_ebm_gpu_fires_20261111.md, OK'd by [visual] and the lead). EBM's own code.
    // Torches and fires: many identical looping emitters (306 CastleKit_groundtorch_green at the swamp pose), each its own particle simulation.
    // One simulation per KIND (prefab x qualifying particle system) and PHASE (K copies started a 1/K loop apart, so neighbours do not flicker in step):
    // each frame, in the main camera's pre-cull, each phase template's particles are baked for that camera (ParticleSystemRenderer.BakeMesh: what
    // vanilla would build) and drawn ONCE, instanced, at every burning instance of that phase beyond the near band. The real systems there are
    // stopped and hidden; their light, heat area, fuel, sound and logic stay vanilla. Within the near band (GpuFiresNearM) a torch is fully vanilla.
    //  - Burning state is vanilla's: a system's GameObject activeInHierarchy (Fireplace.UpdateState's SetActive of m_enabledObject / Low / High).
    //  - Qualifies (census line per prefab says why not): looping, not Custom space, View-aligned billboards or meshes, upright and unscaled fires
    //    that do not move, default vertex streams, no sub-emitters / collision / lights / trails / external forces / trigger, not in a dungeon.
    //  - A material that cannot draw instanced: that kind draws one RenderMesh per instance (the simulations are still saved). One Debug line.
    //  - Key off / unload / fallback: every real system it stopped is restored (renderer on, Play).
    //
    // 1.2.295 ([visual] + lead queue2 11-11: spot 4 with nothing lit cost ~0.13 ms a frame, and a 35.8 ms / 32.2 ms frame pair while the pose was still
    // streaming in, no GC):
    //  - IDLE = ~0: the frame loop walks only BURNING systems. That list is kept by the UpdateState hook (vanilla's own lit / out / wet switch, which also
    //    runs on Awake and on every fire's 2 s tick), plus a 2 s safety sweep. With nothing burning, PreCull returns before any work, and only the kinds
    //    that drew last frame are cleared.
    //  - NO BUILD SPIKE: a kind's templates are built only when a fire beyond the near band first needs them (never for unlit or near fires), ONE PHASE
    //    PER FRAME (Instantiate + strip + Simulate pre-warm). Phases 2..K are cloned from the already stripped phase 1. A fire whose kind is not ready
    //    yet simply stays vanilla. The slowest single phase build of the session is told once at Info when over 8 ms (the proof line), and every 30 s
    //    at Debug.
    //  - LOADING: the per-system verdict (Disqualified) is cached per prefab + system, and the census text is built only once per prefab.
    internal static class GpuFires
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<float> NearM;
        internal static ConfigEntry<int> Phases;
        internal static ConfigEntry<string> Exclude;
        internal static void Bind(ConfigFile config)
        {
            On = config.Bind("Optimize", "GpuFires", /* on to test (lead 11-11) */ true,
                "Torch and fire flames and smoke beyond GpuFiresNearM share a few running copies drawn on the graphics card, instead of each fire running its own. Lights, heat, fuel and sound stay the game's. Off = as before.");
            NearM = config.Bind("Optimize", "GpuFiresNearM", 12f,
                new ConfigDescription("Fires closer than this many metres keep their own flames (the game's exact look up close).", new AcceptableValueRange<float>(0f, 100f)));
            Phases = config.Bind("Optimize", "GpuFiresPhases", 8,
                new ConfigDescription("How many differently-timed copies each kind of flame runs (more = less alike, a little more cost). Takes effect for fires loaded after the change.", new AcceptableValueRange<int>(1, 16)));
            Exclude = config.Bind("Optimize", "GpuFiresExclude", "",
                "Prefab names (comma separated) whose fires always stay the game's own.");
        }
        private static bool Wanted => On != null && On.Value && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;   // never on a dedicated server
        internal static bool ActiveNow => Wanted;   // 1.2.288: EbmApi.GpuFiresActive (FiresFixes reads it when it decides who owns fire particles); true from Bind on while the key is on
        private static float Near => NearM != null ? NearM.Value : 12f;

        // ── kinds, templates, instances ─────────────────────────────────────────────────────────────────────────────────────────────
        private sealed class Kind
        {
            public string Name; public int Phases;
            public ParticleSystem[] Templates; public ParticleSystemRenderer[] TemplateRenderers; public Mesh[] Baked;
            public Material[] Materials; public int Layer; public bool Instanced = true; public bool Broken;   // 1.2.289: one per submesh of the baked mesh
            public readonly List<Matrix4x4>[] ByPhase = new List<Matrix4x4>[16];
            public float LastNeeded; public bool Paused;   // 1.2.290 ([generator] review): templates pause when the kind drew nothing for IdlePauseSeconds
            // 1.2.295: built one phase per frame from the first fire that needs it
            public int Built; public bool Ready => Built >= Phases;
            public ParticleSystem Src; public Quaternion RelRot; public Vector3 Scale; public bool Queued, Drawn;
        }
        private sealed class Inst
        {
            public Fireplace Fire; public ParticleSystem Ps; public ParticleSystemRenderer Renderer; public Kind Kind; public int Phase;
            public bool Covered;     // true = the real system is stopped / hidden and this draws it
            public bool InBurning;   // 1.2.295: on s_burning
        }
        private static readonly Dictionary<string, Kind> s_kinds = new Dictionary<string, Kind>();
        private static readonly List<Inst> s_insts = new List<Inst>();
        private static readonly List<Inst> s_burning = new List<Inst>();     // 1.2.295: the frame loop walks only these
        private static readonly List<Kind> s_drawnKinds = new List<Kind>();  // 1.2.295: kinds with draws last frame (the only lists cleared)
        private static readonly List<Kind> s_buildQueue = new List<Kind>();
        private static readonly Dictionary<Fireplace, List<Inst>> s_byFire = new Dictionary<Fireplace, List<Inst>>();
        private static readonly Dictionary<string, string> s_verdicts = new Dictionary<string, string>();   // 1.2.295: prefab#root/index -> null (qualifies) or why not
        private static readonly HashSet<string> s_censusSaid = new HashSet<string>();
        private static GameObject s_templateRoot;
        private static readonly List<ParticleSystem> s_psScratch = new List<ParticleSystem>(16);
        private static readonly List<ParticleSystemVertexStream> s_streams = new List<ParticleSystemVertexStream>(8);
        private static readonly Matrix4x4[] s_chunk = new Matrix4x4[1023];
        private static readonly GameObject[] s_roots = new GameObject[3];
        private static int s_phaseSeed;

        [HarmonyPatch(typeof(Fireplace), "Awake")]
        private static class FireAwake { private static void Postfix(Fireplace __instance) { try { if (Wanted && __instance != null) Register(__instance); } catch (System.Exception e) { EasyBakeLog.Debug("[GpuFires] register skipped: " + e.Message); } } }

        // Fireplace has no OnDestroy (vanilla 1.0): a destroyed fire is dropped by the frame loop / the sweep (its Fire reads null)

        // vanilla just switched the burning look (fuel / rain; also on Awake and every 2 s): a covered system its SetActive woke must be stopped again
        // (playOnAwake restarts it), and 1.2.295 a system that lit up joins the frame loop
        [HarmonyPatch(typeof(Fireplace), "UpdateState")]
        private static class FireState
        {
            private static void Postfix(Fireplace __instance)
            {
                try
                {
                    if (__instance == null || !s_byFire.TryGetValue(__instance, out var list)) return;
                    foreach (var i in list) { if (i.Covered) Hide(i); if (!i.InBurning && i.Ps != null && i.Ps.gameObject.activeInHierarchy) AddBurning(i); }
                }
                catch { }
            }
        }

        private static void AddBurning(Inst i) { i.InBurning = true; s_burning.Add(i); }

        private static void Register(Fireplace fire)
        {
            if (s_byFire.ContainsKey(fire)) return;
            if (fire.GetComponentInParent<DungeonGenerator>() != null) return;   // DungeonInteriorCull pauses dungeon-room particles: one owner per system
            string prefab = Utils.GetPrefabName(fire.gameObject);
            if (Excluded(prefab)) { Say(prefab, "excluded by GpuFiresExclude"); return; }
            // 1.2.289: the templates carry the prefab's own tilt and scale (relative to the fire's root), and the instance draw adds only a translation,
            // so a fire must stand upright at scale 1 and must not move (a world-space system on a moving fire, a ship's brazier, trails behind it)
            var ft = fire.transform;
            if (Vector3.Dot(ft.up, Vector3.up) < 0.98f) { Say(prefab + " (tilted)", "this placed fire is tilted: stays vanilla"); return; }
            Vector3 ls = ft.lossyScale;
            if (Mathf.Abs(ls.x - 1f) > 0.05f || Mathf.Abs(ls.y - 1f) > 0.05f || Mathf.Abs(ls.z - 1f) > 0.05f) { Say(prefab + " (scaled)", "this placed fire is scaled: stays vanilla"); return; }
            var rb = fire.GetComponentInParent<Rigidbody>();
            if ((rb != null && !rb.isKinematic) || fire.GetComponentInParent<Ship>() != null || fire.GetComponentInParent<Vagon>() != null) { Say(prefab + " (moving)", "on something that moves: stays vanilla"); return; }
            List<Inst> list = null;
            bool first = !s_censusSaid.Contains(prefab);
            StringBuilder report = first ? new StringBuilder() : null;   // 1.2.295: the census text only for a prefab's first fire
            int index = 0;
            s_roots[0] = fire.m_enabledObject; s_roots[1] = fire.m_enabledObjectLow; s_roots[2] = fire.m_enabledObjectHigh;
            for (int ri = 0; ri < 3; ri++)
            {
                var root = s_roots[ri];
                if (root == null) continue;
                s_psScratch.Clear(); root.GetComponentsInChildren(true, s_psScratch);
                foreach (var ps in s_psScratch)
                {
                    int my = index++;
                    string key = prefab + "#" + root.name + "/" + my;
                    ParticleSystemRenderer r = ps != null ? ps.GetComponent<ParticleSystemRenderer>() : null;
                    if (!s_verdicts.TryGetValue(key, out string why)) { why = Disqualified(ps, r); s_verdicts[key] = why; }   // 1.2.295: once per prefab + system
                    if (report != null && report.Length < 900) report.Append(ps != null ? ps.name : "?").Append(why == null ? " yes" : " no (" + why + ")").Append("; ");
                    if (why != null) continue;
                    var kind = KindFor(key, ps, ft);   // 1.2.295: the kind's record only; its templates are built when a fire first needs them
                    if (kind == null || kind.Broken) continue;
                    if (list == null) list = new List<Inst>();
                    list.Add(new Inst { Fire = fire, Ps = ps, Renderer = r, Kind = kind, Phase = (s_phaseSeed++ & 0x7fffffff) % kind.Phases });
                }
            }
            s_roots[0] = s_roots[1] = s_roots[2] = null;
            if (report != null) Say(prefab, report.Length > 0 ? report.ToString() : "no particle systems under its burning objects");
            if (list == null) return;
            s_byFire[fire] = list;
            s_insts.AddRange(list);
            foreach (var i in list) if (i.Ps.gameObject.activeInHierarchy) AddBurning(i);
        }

        private static bool Excluded(string prefab)
        {
            string ex = Exclude != null ? Exclude.Value : "";
            if (string.IsNullOrEmpty(ex)) return false;
            foreach (var part in ex.Split(',')) if (part.Trim().Length > 0 && string.Equals(part.Trim(), prefab, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Say(string prefab, string text)
        {
            if (!s_censusSaid.Add(prefab)) return;
            EasyBakeLog.Debug($"[GpuFires] {prefab}: {text}");
        }

        // null = qualifies; else why not
        private static string Disqualified(ParticleSystem ps, ParticleSystemRenderer r)
        {
            if (ps == null || r == null) return "no renderer";
            var main = ps.main;
            if (!main.loop) return "not looping (a burst)";
            // 1.2.289: World space is fine now (fires are static and the template bakes in world space at the origin, so a static world-space emitter
            // looks as a local one); Custom space stays out
            if (main.simulationSpace == ParticleSystemSimulationSpace.Custom) return "simulates in a custom space";
            if (r.renderMode == ParticleSystemRenderMode.Billboard) { if (r.alignment != ParticleSystemRenderSpace.View) return "billboards aligned to " + r.alignment; }
            else if (r.renderMode != ParticleSystemRenderMode.Mesh) return "render mode " + r.renderMode;   // VerticalBillboard faces the camera POSITION: one bake is not right at every position
            if (r.sharedMaterials == null || r.sharedMaterials.Length == 0 || r.sharedMaterial == null) return "no material";
            if (ps.subEmitters.enabled) return "sub-emitters";
            if (ps.collision.enabled) return "collision";
            if (ps.lights.enabled) return "lights module";
            if (ps.trails.enabled) return "trails";   // with trails off, a renderer's second material (the trail's) is unused: several materials are fine
            if (ps.externalForces.enabled) return "external forces";
            if (ps.trigger.enabled) return "trigger";
            s_streams.Clear(); r.GetActiveVertexStreams(s_streams);
            foreach (var st in s_streams)
                if (st != ParticleSystemVertexStream.Position && st != ParticleSystemVertexStream.Normal && st != ParticleSystemVertexStream.Color && st != ParticleSystemVertexStream.UV) return "custom vertex stream " + st;   // a baked mesh carries no custom data: the shader would read nothing
            return null;   // 1.2.289: a tilted system (smoke emitters rotated in the prefab) is fine: its template carries the same tilt
        }

        // the kind's record (no templates yet): the source system, its tilt relative to its fire (the prefab's own) and its world scale
        private static Kind KindFor(string key, ParticleSystem src, Transform fireRoot)
        {
            if (s_kinds.TryGetValue(key, out var k)) return k;
            if (s_kinds.Count >= 96) return null;   // bounded
            int phases = Mathf.Clamp(Phases != null ? Phases.Value : 8, 1, 16);
            var r = src.GetComponent<ParticleSystemRenderer>();
            k = new Kind
            {
                Name = key, Phases = phases, Templates = new ParticleSystem[phases], TemplateRenderers = new ParticleSystemRenderer[phases], Baked = new Mesh[phases],
                Materials = r.sharedMaterials, Layer = src.gameObject.layer, Src = src,
                RelRot = Quaternion.Inverse(fireRoot.rotation) * src.transform.rotation, Scale = src.transform.lossyScale,
            };
            s_kinds[key] = k;
            return k;
        }

        // ── template builds: one phase per frame (Tick), only for kinds a fire needs ────────────────────────────────────────────────
        private static double s_buildWorstMs, w_buildWorstMs; private static string s_buildWorstKind; private static bool s_buildToldInfo; private static int w_phasesBuilt;
        private static void Need(Kind k, ParticleSystem from)
        {
            if (k.Queued || k.Ready || k.Broken) return;
            if (k.Src == null) k.Src = from;   // the first source unloaded before its build began: this fire's identical system stands in
            k.Queued = true; s_buildQueue.Add(k);
        }

        private static void BuildStep()
        {
            while (s_buildQueue.Count > 0)
            {
                var k = s_buildQueue[0];
                if (k.Ready || k.Broken) { k.Queued = false; s_buildQueue.RemoveAt(0); continue; }
                if (k.Built == 0 && k.Src == null) { k.Queued = false; s_buildQueue.RemoveAt(0); continue; }   // asked for again by the next fire that needs it
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                BuildPhase(k);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                w_phasesBuilt++;
                if (ms > w_buildWorstMs) w_buildWorstMs = ms;
                if (ms > s_buildWorstMs) { s_buildWorstMs = ms; s_buildWorstKind = k.Name; }
                if (!s_buildToldInfo && ms > 8.0) { s_buildToldInfo = true; EasyBakeLog.Info($"[GpuFires] one template phase of {k.Name} took {ms:0.0} ms to build (one phase per frame; the slowest is kept in the 30 s Debug line)"); }
                if (k.Ready || k.Broken) { k.Queued = false; s_buildQueue.RemoveAt(0); }
                return;   // one phase per frame
            }
        }

        private static void BuildPhase(Kind k)
        {
            if (s_templateRoot == null) { s_templateRoot = new GameObject("EBM GpuFires templates"); Object.DontDestroyOnLoad(s_templateRoot); }
            int p = k.Built;
            try
            {
                GameObject go;
                if (p == 0)
                {
                    go = Object.Instantiate(k.Src.gameObject, s_templateRoot.transform, false);
                    // keep only this system: no child systems (each is its own kind), no lights / sounds / scripts
                    foreach (var c in go.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null || c is Transform) continue;
                        if (c.gameObject == go && (c is ParticleSystem || c is ParticleSystemRenderer)) continue;
                        Object.DestroyImmediate(c is ParticleSystemRenderer || c is ParticleSystem ? (Object)c.gameObject : c);
                    }
                    for (int ch = go.transform.childCount - 1; ch >= 0; ch--) Object.DestroyImmediate(go.transform.GetChild(ch).gameObject);   // bare child transforms too
                }
                else go = Object.Instantiate(k.Templates[0].gameObject, s_templateRoot.transform, false);   // 1.2.295: phase 1 is already stripped
                go.name = "tpl " + k.Name + " p" + p;
                go.transform.localPosition = Vector3.zero; go.transform.localRotation = k.RelRot; go.transform.localScale = k.Scale;   // the root sits at the origin, unrotated, scale 1
                var tps = go.GetComponent<ParticleSystem>(); var tr = go.GetComponent<ParticleSystemRenderer>();
                if (tps == null || tr == null) { k.Broken = true; Object.Destroy(go); return; }
                var tm = tps.main; tm.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; tm.playOnAwake = false;
                tr.enabled = false;   // simulated, never drawn by Unity; we bake and draw it
                float loopTime = Mathf.Max(0.1f, tm.duration);
                tps.Simulate(loopTime * p / k.Phases, true, true); tps.Play(true);
                if (k.Paused) tps.Pause(true);
                k.Templates[p] = tps; k.TemplateRenderers[p] = tr;
                k.Baked[p] = new Mesh { name = "EBM GpuFires " + k.Name + " p" + p }; k.Baked[p].MarkDynamic();
                k.ByPhase[p] = new List<Matrix4x4>(64);
                k.Built = p + 1;
                k.LastNeeded = Time.unscaledTime;   // a kind being built is needed: the idle pause must not catch it half made
                if (k.Ready) k.Src = null;   // the source is not needed any more (no reference kept to a scene object)
            }
            catch (System.Exception e) { k.Broken = true; EasyBakeLog.Debug($"[GpuFires] {k.Name}: template not made ({e.Message}); stays vanilla"); }
        }

        // ── real systems: hide / show ────────────────────────────────────────────────────────────────────────────────────────────
        private static void Hide(Inst i)
        {
            if (i.Ps == null) return;
            if (i.Ps.isPlaying || i.Ps.particleCount > 0) i.Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (i.Renderer != null && i.Renderer.enabled) i.Renderer.enabled = false;
            i.Covered = true;
        }
        private static void Show(Inst i)
        {
            i.Covered = false;
            if (i.Ps == null) return;
            if (i.Renderer != null) i.Renderer.enabled = true;
            if (i.Ps.gameObject.activeInHierarchy && !i.Ps.isPlaying) i.Ps.Play(true);
        }

        // ── per frame: the main camera's pre-cull ────────────────────────────────────────────────────────────────────────────────
        private static bool s_hooked;
        internal static void Hook() { if (s_hooked) return; s_hooked = true; Camera.onPreCull += PreCull; }
        private static readonly Plane[] s_planes = new Plane[6];
        private static int w_frames, w_idleFrames, w_drawn, w_near, w_waiting, w_bakes, w_draws, w_fallbackDraws, w_burningSum; private static double w_ms;
        private static bool s_wasOn;

        private static void PreCull(Camera cam)
        {
            if (cam == null || cam != Utils.GetMainCamera()) return;
            if (!Wanted) s_missedFires = true;   // 1.2.381: fires that wake while the key is off are not registered (FireAwake)
            if (!Wanted) { if (s_wasOn) RestoreAll("GpuFires off"); return; }
            if (s_missedFires) { s_missedFires = false; RegisterLoaded(); }   // 1.2.381: a fire that woke while the key was off is taken now
            s_wasOn = true;
            w_frames++;
            if (s_burning.Count == 0 && s_drawnKinds.Count == 0) { w_idleFrames++; return; }   // 1.2.295: nothing burning, nothing drawn last frame: no work
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                for (int d = 0; d < s_drawnKinds.Count; d++) { var dk = s_drawnKinds[d]; dk.Drawn = false; for (int p = 0; p < dk.Phases; p++) dk.ByPhase[p]?.Clear(); }
                s_drawnKinds.Clear();
                w_burningSum += s_burning.Count;
                Vector3 cp = cam.transform.position; float near = Near, nearIn = near * near, nearOut = (near + 2f) * (near + 2f);
                GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
                for (int n = s_burning.Count - 1; n >= 0; n--)
                {
                    var i = s_burning[n];
                    if (i.Ps == null || i.Fire == null || !i.Ps.gameObject.activeInHierarchy)
                    {
                        // gone, out of fuel / wet, or its whole fire object inactive: off the frame loop (a covered one stays stopped and hidden: nothing to show)
                        i.InBurning = false; s_burning[n] = s_burning[s_burning.Count - 1]; s_burning.RemoveAt(s_burning.Count - 1);
                        continue;
                    }
                    var t = i.Ps.transform; Vector3 pos = t.position;
                    float d2 = (pos - cp).sqrMagnitude;
                    // the near band, with 2 m of hysteresis so a fire on the edge does not flip every frame
                    bool wantCover = i.Covered ? d2 > nearIn : d2 > nearOut;
                    if (!wantCover) { if (i.Covered) Show(i); w_near++; continue; }
                    var k = i.Kind;
                    if (k.Broken) { if (i.Covered) Show(i); continue; }
                    if (!k.Ready) { Need(k, i.Ps); if (i.Covered) Show(i); w_waiting++; continue; }   // 1.2.295: stays vanilla until its templates exist
                    if (!i.Covered) Hide(i);
                    else if (i.Ps.isPlaying) Hide(i);   // a SetActive woke it (playOnAwake): stopped again
                    float rad = Mathf.Max(1f, t.lossyScale.y * 2f);
                    if (!GeometryUtility.TestPlanesAABB(s_planes, new Bounds(pos, Vector3.one * rad * 2f))) continue;
                    k.ByPhase[i.Phase % k.Phases].Add(Matrix4x4.Translate(pos));   // 1.2.289: the bake is in world space at the origin (tilt + scale in it): only the move to this fire
                    if (!k.Drawn) { k.Drawn = true; s_drawnKinds.Add(k); }
                    w_drawn++;
                }
                for (int d = 0; d < s_drawnKinds.Count; d++)
                {
                    var k = s_drawnKinds[d];
                    k.LastNeeded = Time.unscaledTime; if (k.Paused) Resume(k);   // 1.2.290: a paused kind runs again the frame it is needed (looping: no catch-up needed)
                    for (int p = 0; p < k.Phases; p++)
                    {
                        var list = k.ByPhase[p];
                        if (list == null || list.Count == 0 || k.Templates[p] == null) continue;
                        k.TemplateRenderers[p].BakeMesh(k.Baked[p], cam, true); w_bakes++;   // 1.2.289: with the template's transform = world space at the origin, any simulation space
                        if (k.Baked[p].vertexCount == 0) continue;
                        Draw(k, k.Baked[p], list, cam);
                    }
                }
            }
            catch (System.Exception e) { EasyBakeLog.Debug("[GpuFires] frame skipped: " + e.Message); }
            w_ms += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void Draw(Kind k, Mesh mesh, List<Matrix4x4> list, Camera cam)
        {
            // explicit world bounds around every instance of this draw (the mesh's own bounds sit at the template's origin): right culling and sorting
            Bounds mb = mesh.bounds; Bounds wb = new Bounds(list[0].MultiplyPoint3x4(mb.center), Vector3.zero);
            float grow = mb.extents.magnitude * 2f;
            for (int n = 0; n < list.Count; n++) wb.Encapsulate(new Bounds(list[n].MultiplyPoint3x4(mb.center), Vector3.one * grow));
            // 1.2.289: one draw per submesh of the baked mesh with that slot's material (a renderer's extra slot is its unused trail material: skipped)
            int subs = Mathf.Min(mesh.subMeshCount, k.Materials.Length);
            for (int sm = 0; sm < subs; sm++)
            {
                if (k.Materials[sm] == null) continue;
                if (k.Instanced)
                {
                    try
                    {
                        if (!k.Materials[sm].enableInstancing) k.Materials[sm] = new Material(k.Materials[sm]) { enableInstancing = true, name = k.Materials[sm].name + " (EBM GpuFires)" };
                        var rpi = new RenderParams(k.Materials[sm]) { motionVectorMode = MotionVectorGenerationMode.Camera,  camera = cam, layer = k.Layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, worldBounds = wb };
                        for (int s = 0; s < list.Count; s += s_chunk.Length)
                        {
                            int n = Mathf.Min(s_chunk.Length, list.Count - s);
                            list.CopyTo(s, s_chunk, 0, n);
                            Graphics.RenderMeshInstanced(rpi, mesh, sm, s_chunk, n);
                            w_draws++;
                        }
                        continue;
                    }
                    catch (System.Exception e) { k.Instanced = false; EasyBakeLog.Debug($"[GpuFires] {k.Name}: its material cannot draw instanced ({e.Message}); one draw per fire from now on (the simulations are still shared)"); }
                }
                var rp = new RenderParams(k.Materials[sm]) { motionVectorMode = MotionVectorGenerationMode.Camera,  camera = cam, layer = k.Layer, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false, worldBounds = wb };
                foreach (var m in list) { Graphics.RenderMesh(rp, mesh, sm, m); w_fallbackDraws++; }
            }
        }

        // 1.2.381 ([visual], [lead] hot-swap audit 4c): fires register in Fireplace.Awake only while the key is on, so after a live OFF -> ON the fires loaded meanwhile
        // kept their own particle systems for good. A frame with the key off sets the flag (PreCull); the first frame the key is on again every loaded fire goes through
        // Register (it returns at once for one already registered). One FindObjectsOfType at that flip, never per frame.
        private static bool s_missedFires;
        private static void RegisterLoaded()
        {
            int before = s_byFire.Count, seen = 0;
            foreach (var fire in Object.FindObjectsOfType<Fireplace>())
            {
                seen++;
                try { Register(fire); } catch (System.Exception e) { EasyBakeLog.Debug("[GpuFires] register skipped: " + e.Message); }
            }
            // 1.2.383 ([lead]): walked / acted / total in one line, so a run can gate on "acted > 0 or all already shared"
            EasyBakeLog.Info($"[GpuFires] turned on mid-run: walked {seen} loaded fire(s), acted on {s_byFire.Count - before} (share their flames now), {s_byFire.Count} fire(s) with {s_insts.Count} particle system(s) shared in all{(s_byFire.Count == 0 ? "; no targets in this scene" : "")}");
        }

        private static void RestoreAll(string why)
        {
            int n = 0;
            foreach (var i in s_insts) if (i.Covered) { Show(i); n++; }
            s_wasOn = false;
            if (n > 0) EasyBakeLog.Debug($"[GpuFires] {why}: {n} fire system(s) given back to the game");
        }

        // 1.2.290 ([generator] review of 1.2.287, MEDIUM): a kind whose instances drew nothing for IdlePauseSeconds pauses its phase templates (they ran
        // AlwaysSimulate under DontDestroyOnLoad forever, so hundreds of simulations kept running after passing many fire kinds); the next frame that needs
        // the kind plays them again. Looping systems, so no catch-up. Checked once a second.
        private const float IdlePauseSeconds = 10f;
        private static float s_pauseCheckAt;
        private static int c_paused, c_resumed;
        private static void PauseIdleKinds(float now)
        {
            if (now < s_pauseCheckAt) return;
            s_pauseCheckAt = now + 1f;
            foreach (var k in s_kinds.Values)
            {
                if (k.Broken || k.Paused || k.Built == 0 || now - k.LastNeeded < IdlePauseSeconds) continue;
                for (int p = 0; p < k.Built; p++) if (k.Templates[p] != null) k.Templates[p].Pause(true);
                k.Paused = true; c_paused++;
            }
        }
        private static void Resume(Kind k)
        {
            for (int p = 0; p < k.Built; p++) if (k.Templates[p] != null) k.Templates[p].Play(true);
            k.Paused = false; c_resumed++;
        }

        // ── housekeeping + census (Plugin.LateUpdateInner) ───────────────────────────────────────────────────────────────────────
        private static float s_sayAt, s_sweepAt;
        private static readonly List<Fireplace> s_deadFires = new List<Fireplace>();
        private static int w_sweepOff, w_sweepOffHidden, w_sweeps;
        internal static void Tick()
        {
            Hook();
            float now = Time.unscaledTime;
            PauseIdleKinds(now);   // 1.2.290; also with the key off, so built templates stop simulating 10 s after the last draw
            if (!Wanted) return;
            if (s_buildQueue.Count > 0) BuildStep();   // 1.2.295: one template phase per frame
            if (now >= s_sweepAt) Sweep(now);
            if (now < s_sayAt) return;
            SayBurning();   // 1.2.296: the Info proof line (player-default logs carry it)
            // destroyed fires (no OnDestroy to hook): their entries leave the map here, every 30 s
            s_deadFires.Clear();
            foreach (var f in s_byFire.Keys) if (f == null) s_deadFires.Add(f);
            foreach (var f in s_deadFires) s_byFire.Remove(f);
            if (s_sayAt > 0f && EasyBakeLog.DebugOn() && (w_frames > 0 || s_insts.Count > 0))
            {
                int templates = 0, broken = 0, notInstanced = 0, built = 0;
                foreach (var k in s_kinds.Values) { if (k.Broken) broken++; else { templates += k.Built; if (k.Ready) built++; } if (!k.Instanced) notInstanced++; }
                float fr = w_frames > 0 ? w_frames : 1f, busy = w_frames - w_idleFrames > 0 ? w_frames - w_idleFrames : 1f, sw = w_sweeps > 0 ? w_sweeps : 1f;
                EasyBakeLog.Debug($"[GpuFires] 30 s: {s_kinds.Count} kind(s) known, {built} with templates ({templates} template phase(s) running; {broken} left vanilla, {notInstanced} drawn one per fire), {s_insts.Count} fire system(s) known, "
                    + $"{w_burningSum / busy:0} burning on the frame loop; idle frames (nothing burning) {w_idleFrames} of {w_frames}; per busy frame {w_drawn / busy:0} drawn shared, {w_near / busy:0} near (vanilla), {w_waiting / busy:0} waiting for templates; "
                    + $"not burning at the 2 s sweep {w_sweepOff / sw:0} (out of fuel / wet) + {w_sweepOffHidden / sw:0} (whole fire object inactive); template phases built {w_phasesBuilt} (slowest {w_buildWorstMs:0.0} ms; session slowest {s_buildWorstMs:0.0} ms, {s_buildWorstKind ?? "-"}); "
                    + $"paused {c_paused} / resumed {c_resumed}; {w_bakes / busy:0.0} bakes + {(w_draws + w_fallbackDraws) / busy:0.0} draws a busy frame, {w_ms / fr:0.000} ms a frame");
            }
            s_sayAt = now + 30f;
            w_frames = w_idleFrames = w_drawn = w_near = w_waiting = w_bakes = w_draws = w_fallbackDraws = w_burningSum = 0; w_ms = 0;
            w_sweepOff = w_sweepOffHidden = w_sweeps = w_phasesBuilt = 0; w_buildWorstMs = 0;
            c_paused = c_resumed = 0;
        }

        // 1.2.296 (lead 11-11: the fueled-circle leg could not show a fire was lit in its window, Debug being off): every 30 s at INFO, only when a count
        // changed: burning fires, how many GpuFires draws (beyond the near band) and how many stay vanilla near the camera. Counted by fire, not by system.
        private static readonly HashSet<Fireplace> s_countScratch = new HashSet<Fireplace>();
        private static int s_lastBurning = -1, s_lastShared = -1;
        private static void SayBurning()
        {
            s_countScratch.Clear();
            int shared = 0;
            for (int n = 0; n < s_burning.Count; n++)
            {
                var i = s_burning[n];
                if (i.Fire == null || i.Ps == null || !i.Ps.gameObject.activeInHierarchy) continue;
                if (s_countScratch.Add(i.Fire) && i.Covered) shared++;
            }
            int burning = s_countScratch.Count;
            s_countScratch.Clear();
            if (burning == s_lastBurning && shared == s_lastShared) return;
            s_lastBurning = burning; s_lastShared = shared;
            EasyBakeLog.Info($"[GpuFires] {burning} burning fire(s) known: {shared} drawn shared beyond {Near:0} m, {burning - shared} by the game (near the camera, or their kind's templates not built yet)");
        }

        // 1.2.295: every 2 s, a safety net for a burning change no UpdateState showed (a fire object reactivated, a mod's own SetActive): lit systems off the
        // frame loop join it; destroyed ones leave every list
        private static void Sweep(float now)
        {
            s_sweepAt = now + 2f; w_sweeps++;
            for (int n = s_insts.Count - 1; n >= 0; n--)
            {
                var i = s_insts[n];
                if (i.Ps == null || i.Fire == null) { s_insts[n] = s_insts[s_insts.Count - 1]; s_insts.RemoveAt(s_insts.Count - 1); if (i.InBurning) { i.InBurning = false; s_burning.Remove(i); } continue; }
                if (i.InBurning) continue;
                if (i.Ps.gameObject.activeInHierarchy) AddBurning(i);
                else if (i.Fire.gameObject.activeInHierarchy) w_sweepOff++; else w_sweepOffHidden++;
            }
        }
    }
}
