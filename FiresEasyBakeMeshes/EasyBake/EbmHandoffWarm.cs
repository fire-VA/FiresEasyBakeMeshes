using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // E28 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E28; DROP_NOTES_seasons_handoff.md): MEASUREMENT FIRST (lead: fix only on non-zero).
    // Key [Debugging] HandoffCensus, off.
    // Where the two sides change:
    //  - real piece hidden: EasyBakeSuppressedVisuals.SuppressPiece (renderers off, that moment)
    //  - real piece shown again: EasyBakeSuppressedVisuals.RestoreVisuals (renderers on, that moment)
    //  - drawn side: a combined zone mesh appears / goes in the same call as the hide / show (MeshBaker builds the batch object, then suppresses;
    //    ZoneTracker restores, then destroys the batch, and Unity destroys before rendering), so it cannot leave a gap or a double;
    //    an INDIRECT instance reaches the GPU only at EbmIndirect.Upload, inside EBM's LateUpdate (ZoneTracker.DrawInstances -> EndFrame).
    // So a gap / double can only come from an indirect add / remove that is still waiting for Upload when the frame renders. Checked at the main
    // camera's onPreCull (after every Update / LateUpdate, before the frame renders):
    //  - GAP frame: a piece was hidden since the last rendered frame AND an indirect add is still not uploaded
    //  - DOUBLE frame: a piece was shown since the last rendered frame AND an indirect remove is still not uploaded
    // Upper bound: the waiting add / remove may belong to another piece, so a 0 here proves 0. Census (Debug, 30 s): hides, shows, gap frames,
    // double frames, frames rendered, and how many adds / removes were waiting on those frames.
    internal static class EbmHandoffCensus
    {
        internal static ConfigEntry<bool> Enabled;
        private static bool On => Enabled != null && Enabled.Value;
        private static int s_hidesSinceRender, s_showsSinceRender, s_lastFrame = -1;
        private static int w_hides, w_shows, w_gapFrames, w_doubleFrames, w_frames, w_waitingAdds, w_waitingRemoves;
        private static float s_nextSay;
        private static bool s_hooked;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Debugging", "HandoffCensus", false,
                "Testing aid: counts frames where a piece EBM took over was hidden before its replacement reached the graphics card (gap) or shown again while its replacement was still drawn (double). Debug line every 30 s; both should read 0.");
            if (!s_hooked) { s_hooked = true; Camera.onPreCull += OnPreCull; }
        }

        internal static void NoteHide() { if (On) { s_hidesSinceRender++; w_hides++; } }
        internal static void NoteShow() { if (On) { s_showsSinceRender++; w_shows++; } }

        private static void OnPreCull(Camera cam)
        {
            if (Time.frameCount == s_lastFrame || cam == null || cam != Utils.GetMainCamera()) return;
            s_lastFrame = Time.frameCount;
            EbmShaderWarm.Frame(cam);   // shares the once-a-frame hook
            if (!On) return;
            w_frames++;
            int adds = Indirect.EbmIndirect.AddsWaiting, removes = Indirect.EbmIndirect.RemovesWaiting;
            if (s_hidesSinceRender > 0 && adds > 0) { w_gapFrames++; w_waitingAdds += adds; }
            if (s_showsSinceRender > 0 && removes > 0) { w_doubleFrames++; w_waitingRemoves += removes; }
            s_hidesSinceRender = s_showsSinceRender = 0;
            float now = Time.unscaledTime;
            if (now < s_nextSay) return;
            if (s_nextSay > 0f) EasyBakeLog.Debug($"[Handoff] 30 s: {w_frames} frame(s), pieces hidden for EBM {w_hides}, given back {w_shows}; gap frames {w_gapFrames} (an add still waiting: {w_waitingAdds}), double frames {w_doubleFrames} (a remove still waiting: {w_waitingRemoves}); both must be 0 (upper bound)");
            s_nextSay = now + 30f;
            w_hides = w_shows = w_gapFrames = w_doubleFrames = w_frames = w_waitingAdds = w_waitingRemoves = 0;
        }
    }

    // E30 ([seasons] 11-09, row E30; READ_EBM_round2_gaps section 6). Key [Optimize] WarmEbmShaders, off.
    // Vanilla / EBM today: PrewarmCoroutine instantiates prefabs under an inactive holder, so no shader pass is ever compiled ahead; EBM's
    // own indirect shaders (its bundle) compile on the first frame a batch using that shader + keyword set is drawn.
    // Warm-up: the first in-world frames, ONCE: for every shader in EBM's own bundle (EbmIndirectAssets.OwnShaders, never another mod's) and each
    // material keyword set EBM uses on it (none, EBM_ALPHATEST where the shader has it), one Graphics.RenderMeshIndirect with an instance count
    // of 0 in its arguments, casting shadows, for 3 frames. The count lives on the GPU, so Unity sets the pass (and compiles it with the frame's
    // real global keywords: deferred, caster) but no vertex runs and nothing is drawn. Then the materials and the buffer are released.
    // First-use census (always counted, Debug every 30 s): each new shader + keyword set an EBM batch draws with, whether it was warmed, and the
    // frame time after its first draw over the running average (a first-draw spike). With the key on, "not warmed" and spikes must read 0.
    internal static class EbmShaderWarm
    {
        internal static ConfigEntry<bool> Enabled;
        private static bool On => Enabled != null && Enabled.Value;
        private const int WarmFrames = 3;
        private static int s_warmLeft = -1;   // -1 = not started
        private static readonly List<Material> s_mats = new List<Material>();
        private static readonly HashSet<long> s_warmed = new HashSet<long>(), s_seen = new HashSet<long>();
        private static GraphicsBuffer s_args; private static Mesh s_tri;
        private static double s_warmMs;
        // first-use census
        private static int s_firstUseFrame = -1; private static float s_avgDt = 0.016f;
        private static int w_first, w_firstWarmed, w_spikes; private static float w_worst; private static float s_nextSay;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Optimize", "WarmEbmShaders", false,
                "When you enter the world, EBM's own graphics shaders are prepared once, before the first building that needs them appears, so that first appearance does not stutter. Nothing is drawn. Slightly longer first second in the world.");
        }

        private static long Key(Material m)
        {
            if (m == null || m.shader == null) return 0;
            // review 1.2.257 ([generator]): the whole enabled keyword set, not just EBM_ALPHATEST, so a variant with other keywords never counts as warmed
            int h = 17;
            var kws = m.enabledKeywords;
            for (int i = 0; i < kws.Length; i++) h ^= kws[i].name.GetHashCode();   // order-independent
            return ((long)m.shader.GetInstanceID() << 32) | (uint)h;
        }

        /// <summary>From EbmIndirect.BatchOf, when a new batch is made: a shader + keyword set drawn for the first time this session?</summary>
        internal static void NoteBatch(Material m)
        {
            long k = Key(m);
            if (k == 0 || !s_seen.Add(k)) return;
            w_first++;
            if (s_warmed.Contains(k)) w_firstWarmed++;
            s_firstUseFrame = Time.frameCount;
        }

        internal static void Frame(Camera cam)
        {
            // first-use spike: the frame after a first use, its time over the running average
            float dt = Time.unscaledDeltaTime;
            if (s_firstUseFrame >= 0 && Time.frameCount == s_firstUseFrame + 1)
            {
                float over = (dt - s_avgDt) * 1000f;
                if (over > 5f) w_spikes++;
                if (over > w_worst) w_worst = over;
                s_firstUseFrame = -1;
            }
            else s_avgDt = Mathf.Lerp(s_avgDt, dt, 0.05f);
            Say();

            if (!On || s_warmLeft == 0) return;
            if (s_warmLeft < 0)
            {
                if (Player.m_localPlayer == null) return;   // in the world
                Begin();
                if (s_warmLeft <= 0) return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var bounds = new Bounds(cam.transform.position + cam.transform.forward * 2f, Vector3.one);
            foreach (var mat in s_mats)
            {
                var rp = new RenderParams(mat) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.On, receiveShadows = true, matProps = s_props };
                Graphics.RenderMeshIndirect(in rp, s_tri, s_args, 1, 0);
            }
            s_warmMs += sw.Elapsed.TotalMilliseconds;
            if (--s_warmLeft == 0) End(true);
        }

        // review 11-09 ([visual]): a draw with nothing bound may compile nothing. Every buffer EBM's shaders read is bound, one element each,
        // at the exact stride the shader declares (a wrong stride gets the draw skipped), all zeros, and ONE instance is drawn: its matrix is zero,
        // so every vertex lands on one point and no pixel is written, while the full vertex + fragment pipeline runs.
        //   _EbmInstances (EbmInstance: 6 x float4 = 96 B), _EbmVisible (uint), _EbmVisibleBase, _EbmInstLooks (float4, EbmPiece),
        //   _EbmGrassO2W (float4, 3 a slot), _EbmGrassVisible (uint), _EbmGrassBase
        private static MaterialPropertyBlock s_props;
        private static readonly List<GraphicsBuffer> s_bufs = new List<GraphicsBuffer>();
        private static int s_rewarms, s_aborted;

        private static GraphicsBuffer Zeros(int count, int stride)
        {
            var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
            b.SetData(new byte[count * stride]);
            s_bufs.Add(b);
            return b;
        }

        private static void Begin()
        {
            s_warmLeft = 0;
            var own = Indirect.EbmIndirectAssets.OwnShaders;
            if (own == null) return;
            foreach (var sh in own)
            {
                if (sh == null || !sh.isSupported || sh.name.IndexOf("Capture", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;   // a UAV writer, not a drawn shader
                AddMat(sh, false);
                if (sh.keywordSpace.FindKeyword("EBM_ALPHATEST").isValid) AddMat(sh, true);
            }
            if (s_mats.Count == 0) return;
            s_tri = new Mesh { name = "EBM warm-up", hideFlags = HideFlags.HideAndDontSave };
            s_tri.SetVertices(new List<Vector3> { Vector3.zero, Vector3.right * 0.001f, Vector3.up * 0.001f });
            s_tri.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up });
            s_tri.SetTangents(new List<Vector4> { new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f) });   // review 1.2.257: same layout as baked meshes (position, normal, tangent, uv0)
            s_tri.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.up });
            s_tri.SetTriangles(new[] { 0, 1, 2 }, 0);
            s_args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 5, 4);
            s_args.SetData(new uint[] { 3u, 1u, 0u, 0u, 0u });   // 3 indices, ONE instance (zero matrix: nothing rasterised)
            s_props = new MaterialPropertyBlock();
            s_props.SetBuffer("_EbmInstances", Zeros(1, 96));
            s_props.SetBuffer("_EbmVisible", Zeros(1, 4));
            s_props.SetInteger("_EbmVisibleBase", 0);
            s_props.SetBuffer("_EbmInstLooks", Zeros(4, 16));
            s_props.SetBuffer("_EbmGrassO2W", Zeros(3, 16));
            s_props.SetBuffer("_EbmGrassVisible", Zeros(1, 4));
            s_props.SetInteger("_EbmGrassBase", 0);
            if (s_warmCount++ > 0) s_rewarms++;
            s_warmLeft = WarmFrames;
        }
        private static int s_warmCount;

        private static void AddMat(Shader sh, bool alphaTest)
        {
            var m = new Material(sh) { name = "EBM warm-up " + sh.name, enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
            if (alphaTest) m.EnableKeyword("EBM_ALPHATEST");
            s_mats.Add(m);
        }

        // completed = all warm frames ran: only then do the sets count as warmed for the first-use census
        private static void End(bool completed)
        {
            if (completed)
            {
                foreach (var m in s_mats) s_warmed.Add(Key(m));
                EasyBakeLog.Info($"[Warm] EBM's own shaders prepared: {s_mats.Count} shader + keyword set(s) over {WarmFrames} frames with real buffers bound, {s_warmMs:0.0} ms on the main thread (nothing visible); warm-ups this process {s_warmCount} (re-warms after a world change {s_rewarms}, cut short by a logout {s_aborted})");
            }
            foreach (var m in s_mats) if (m != null) Object.Destroy(m);
            s_mats.Clear();
            if (s_args != null) { s_args.Release(); s_args = null; }
            if (s_tri != null) { Object.Destroy(s_tri); s_tri = null; }
            foreach (var b in s_bufs) b?.Release();
            s_bufs.Clear();
            s_props = null;
        }

        /// <summary>review 11-09: the world session ended (EbmIndirect.ReleaseAll at logout / world unload). A warm-up in progress is freed and
        /// counted as cut short; the next world warms again (its global keywords may differ).</summary>
        internal static void ResetForWorld()
        {
            if (s_warmLeft > 0) { s_aborted++; End(false); }
            else if (s_mats.Count > 0 || s_bufs.Count > 0) End(false);
            s_warmLeft = -1; s_warmMs = 0;
            s_seen.Clear();   // review 11-09 ([visual]): the first-use census counts again in the next world
        }

        private static void Say()
        {
            float now = Time.unscaledTime;
            if (now < s_nextSay) return;
            if (s_nextSay > 0f && EasyBakeLog.DebugOn() && w_first > 0)
                EasyBakeLog.Debug($"[Warm] 30 s: EBM shader + keyword sets drawn for the first time {w_first} ({w_firstWarmed} warmed beforehand, {w_first - w_firstWarmed} not); first-draw spikes over 5 ms {w_spikes}, worst {w_worst:0.0} ms over the running frame time; warm-up {(On ? "on" : "off")}");
            s_nextSay = now + 30f;
            w_first = w_firstWarmed = w_spikes = 0; w_worst = 0f;
        }
    }
}
