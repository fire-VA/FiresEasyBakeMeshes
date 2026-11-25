using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Bakes one kind at a time (SPEC §3). A hidden DEFERRED camera, no HDR / MSAA, culling one free layer, looks at the kind's LOD0
    /// meshes (staged as plain MeshRenderers with the LIVE materials, so a season mod's look is what gets baked; no scripts, no LODGroup,
    /// no shadows) from 64 hemi-octahedral directions. A command buffer right after the G-buffer packs the UNLIT material data (albedo +
    /// coverage, kind-space normal + smoothness, premultiplied) into the two atlases: one view per frame at most, inside [Far Impostors]
    /// Bake budget ms. Then: GPU mips, async readback (no stall), and on a worker the per-mip coverage cutoffs, the per-view silhouette
    /// areas and our DXT5 encode; back on the main thread the slice is uploaded and the result cached on disk (a relaunch reads it back).
    /// </summary>
    internal static class ImpostorBaker
    {
        private enum Phase { Idle, Render, Readback, Work }

        private static Phase s_phase;
        private static ImpostorKinds.Kind s_kind;
        private static ImpostorAtlas.Pair s_pair;
        private static int s_view, s_px, s_size, s_mips;
        private static GameObject s_stage, s_camGo;
        private static Camera s_cam;
        private static CommandBuffer s_cb;
        private static Material s_pack;
        private static RenderTexture s_camRT, s_tA, s_tB, s_atlasA, s_atlasB;
        private static byte[][] s_readA, s_readB; private static int s_readsLeft; private static bool s_readFailed;
        // 1.2.356 ([2ic], lead 11-13 profiler shore move: "<RenderView>b__0 / b__1" 24.0 + 24.0 MB/min, 16 allocations of 1.5 MB): every readback copied its
        // mip into a NEW array (GetData().ToArray()). The bake is strictly one kind at a time (Idle -> Render -> Readback -> Work -> Finish -> Idle),
        // so one buffer per atlas per mip is reused: written by the readback, read by the worker, free again before the next kind's readback.
        // s_readSerial: a callback from a bake that ended (Done / Abort / Release) is ignored, so it can never write into the next kind's buffers.
        private static byte[][] s_poolA, s_poolB; private static int s_readSerial;
        private static byte[] PoolBuf(ref byte[][] pool, int mip, int len)
        {
            if (pool == null || pool.Length <= mip) { var n = new byte[Math.Max(mip + 1, 12)][]; if (pool != null) Array.Copy(pool, n, pool.Length); pool = n; }
            var b = pool[mip];
            if (b == null || b.Length != len) pool[mip] = b = new byte[len];   // a new atlas size: once
            return b;
        }
        private static Task<Result> s_task;
        private static int s_layer = -1;
        private static readonly Vector3 StageOrigin = new Vector3(0f, 40f, 0f);   // meadow height: no altitude snow in the bake
        private static readonly Stopwatch s_sw = new Stopwatch();
        private const float BakeFovDeg = 1f;
        private const float LoadingBudgetMs = 40f;   // 1.2.345: under the loading screen (a longer load beats post-load hitches)
        private static string s_pathUsed;             // Camera.actualRenderingPath of the kind's first view
        private static double s_lastViewMs;           // cost of the last view (the next one is assumed to cost the same)
        private static int s_skipFrames;              // after load: frames to wait after an over-budget view
        internal static int OverBudgetViews, DeferredFrames;   // census: views that cost more than BakeBudgetMs after load; frames skipped for it
        internal static double SpentMs, WorstViewMs; internal static int KindsBaked, FromCache;
        private static readonly int GB0 = Shader.PropertyToID("_ImpGB0"), GB1 = Shader.PropertyToID("_ImpGB1"), GB2 = Shader.PropertyToID("_ImpGB2"), Depth = Shader.PropertyToID("_ImpDepth");

        private sealed class Result { public byte[] A, B; public float[] Cutoffs; public float[] Areas; public float Coverage; public string Why; }

        internal static bool Busy => s_phase != Phase.Idle;
        internal static string BakingName => s_kind != null ? s_kind.Name : null;

        /// <summary>Main thread, from the engine's late hook.</summary>
        internal static void Step()
        {
            switch (s_phase)
            {
                case Phase.Idle: Begin(); break;
                case Phase.Render: RenderOne(); break;
                case Phase.Readback: if (s_readsLeft == 0) StartWork(); break;
                case Phase.Work: if (s_task != null && s_task.IsCompleted) Finish(); break;
            }
        }

        private static void Begin()
        {
            ImpostorAtlas.CheckSize(ImpostorKinds.Count);
            if (ImpostorAtlas.Target == null) return;   // 1.2.450: the first atlas pair is still being created (over two frames)
            if (ImpostorAtlas.TrySwap()) ImpostorDraw.KindsChanged();
            var k = ImpostorKinds.NextToBake();
            if (k == null) { SayQueueEmpty(); FreeWhenIdle(); return; }
            s_idleSince = -1f;
            s_pair = ImpostorAtlas.Target;   // 1.2.478: size first, the disk key holds it (Begin runs only while idle, so nothing reads these mid-bake)
            s_size = s_pair.Size; s_px = s_size / ImpostorKinds.Views; s_mips = s_pair.Mips;
            // 1.2.478: the disk key first, BEFORE the kind is taken: a runtime-made texture's content hash may still be on its way (GPU readback, a few
            // frames); the kind waits for it up to KeyWaitFrames, then bakes without the disk cache this time.
            string key = null; bool keyPending = false;
            if (ImpostorEngine.CacheOnDisk) key = CacheKey(k, out keyPending);   // disk cache off: no key, no content readbacks
            if (keyPending)
            {
                if (s_keyWaitFrames == 0) ImpostorKinds.RequestQueuedRuntimeHashes();   // [2ic] SHOULD: the first frame of a wait asks for every queued kind's hashes at once
                if (++s_keyWaitFrames < KeyWaitFrames) return;
                s_keyWaits++; key = null;
            }
            s_keyWaitFrames = 0;
            // 1.2.345: baking now runs under the loading screen too (it used to wait for it to close and bake while playing)
            s_kind = k;
            k.State = ImpostorKinds.State.Baking;

            // the disk cache first
            s_bakeKey = key;   // 1.2.478 ([2ic] S1): Finish saves under THIS key (was re-derived from the live materials at Finish)
            if (key == null && ImpostorEngine.CacheOnDisk) s_noDiskKinds++;
            if (ImpostorEngine.CacheOnDisk && key != null && ImpostorDiskCache.TryRead("impostor-atlas", key, out var bytes) && Decode(bytes, out var cached))
            {
                Apply(cached, true);
                return;
            }
            if (ImpostorEngine.CacheOnDisk) EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {k.Name}: " + (key == null ? "a runtime-made texture without a content hash (readback failed or timed out), not disk-cached (1.2.478): baking" : $"not in the disk cache (1.2.478, key {key}): baking"));   // [2ic] S2
            if (!Setup(k)) { k.State = ImpostorKinds.State.Refused; k.Why = "bake setup failed"; s_kind = null; return; }
            s_view = 0; s_phase = Phase.Render; s_pathUsed = null;
        }

        // 1.2.464 ([visual], lead 11-19 RAM census G8 (rig path) "FiresEBM_ImpostorBake 12.0 MB (1536x1536
        // ARGB32 ... mips)" x2 for the whole session): the bake targets (two atlas-size render textures, the view textures) and the pooled readback buffers
        // (one per atlas per mip, RGBA32: ~12.6 MB each at 1536 px, ~25 MB of managed heap) were kept until the world unloaded, though every kind is baked
        // (or read from the disk cache) within the first minute and new kinds are rare. After IdleFreeSeconds with nothing to bake they are freed; the next
        // bake makes them again in Setup (Alloc handles null; the pack command buffer is cleared and re-recorded there; the disabled camera never renders
        // without RenderOne). The atlases themselves (what draws) are untouched.
        private const float IdleFreeSeconds = 10f;
        private static float s_idleSince = -1f;
        private static void FreeWhenIdle()
        {
            if (s_atlasA == null && s_poolA == null && s_poolB == null) return;
            float now = Time.unscaledTime;
            if (s_idleSince < 0f) { s_idleSince = now; return; }
            if (now - s_idleSince < IdleFreeSeconds) return;
            long rtBytes = 0, poolBytes = 0;
            foreach (var rt in new[] { s_camRT, s_tA, s_tB, s_atlasA, s_atlasB }) if (rt != null) rtBytes += (long)rt.width * rt.height * 4 * (rt.useMipMap ? 4 : 3) / 3;
            foreach (var pool in new[] { s_poolA, s_poolB }) if (pool != null) foreach (var b in pool) if (b != null) poolBytes += b.Length;
            if (s_cam != null) s_cam.targetTexture = null;
            foreach (var rt in new[] { s_camRT, s_tA, s_tB, s_atlasA, s_atlasB }) if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            s_camRT = s_tA = s_tB = s_atlasA = s_atlasB = null;
            s_poolA = s_poolB = null; s_readA = s_readB = null;
            s_idleSince = -1f;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"bake buffers freed after {IdleFreeSeconds:0} s with nothing to bake: render targets ~{rtBytes / (1024 * 1024)} MB, readback buffers {poolBytes / (1024 * 1024)} MB (made again by the next bake)");
        }

        private static bool Loading() => Hud.instance != null && Hud.instance.m_loadingScreen != null && Hud.instance.m_loadingScreen.gameObject.activeInHierarchy && Hud.instance.m_loadingScreen.alpha > 0f;

        private static int FreeLayer()
        {
            if (s_layer >= 0) return s_layer;
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { s_layer = i; break; }
            if (s_layer < 0) s_layer = 31;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"bake layer {s_layer}");
            return s_layer;
        }

        private static bool Setup(ImpostorKinds.Kind k)
        {
            int layer = FreeLayer();
            if (s_pack == null) s_pack = new Material(ImpostorAssets.Pack) { hideFlags = HideFlags.HideAndDontSave };
            s_stage = new GameObject("FiresEBM_ImpostorStage") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
            s_stage.transform.position = StageOrigin;
            foreach (var p in k.Parts)
            {
                var go = new GameObject("part") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                go.transform.SetParent(s_stage.transform, false);
                var m = p.ToRoot;
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = p.Mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = p.Materials;
                r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off; r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }
            if (s_camGo == null)
            {
                s_camGo = new GameObject("FiresEBM_ImpostorBakeCamera") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(s_camGo);
                s_cam = s_camGo.AddComponent<Camera>();
                // 1.2.345 ([ghost], Fire's run_7: all 30 kinds refused "nothing reached the G-buffer", 0 baked): Unity's built-in pipeline does not
                // run deferred on an ORTHOGRAPHIC camera (it falls back to forward), so the AfterGBuffer pack below never ran. A perspective camera
                // with a 1 degree field of view, placed r / tan(0.5 deg) away, is deferred and its rays are parallel within half a degree, so the
                // 64 views keep the hemi-octahedral layout the draw shader expects. Each kind's line names the path Unity actually used.
                s_cam.enabled = false; s_cam.orthographic = false; s_cam.fieldOfView = BakeFovDeg; s_cam.aspect = 1f;
                s_cam.renderingPath = RenderingPath.DeferredShading; s_cam.allowHDR = false; s_cam.allowMSAA = false;
                s_cam.clearFlags = CameraClearFlags.SolidColor; s_cam.backgroundColor = Color.clear;
                s_cam.useOcclusionCulling = false;
            }
            s_cam.cullingMask = 1 << layer;
            Alloc(ref s_camRT, s_px, 24, RenderTextureReadWrite.sRGB, false);
            Alloc(ref s_tA, s_px, 0, RenderTextureReadWrite.sRGB, false);
            Alloc(ref s_tB, s_px, 0, RenderTextureReadWrite.Linear, false);
            Alloc(ref s_atlasA, s_size, 0, RenderTextureReadWrite.sRGB, true);
            Alloc(ref s_atlasB, s_size, 0, RenderTextureReadWrite.Linear, true);
            s_cam.targetTexture = s_camRT;
            if (s_cb == null)
            {
                s_cb = new CommandBuffer { name = "FiresEBM impostor pack" };
                s_cam.AddCommandBuffer(CameraEvent.AfterGBuffer, s_cb);
            }
            s_cb.Clear();
            s_cb.SetGlobalTexture(GB0, BuiltinRenderTextureType.GBuffer0);
            s_cb.SetGlobalTexture(GB1, BuiltinRenderTextureType.GBuffer1);
            s_cb.SetGlobalTexture(GB2, BuiltinRenderTextureType.GBuffer2);
            // 1.2.346: no ResolvedDepth (run_8 :17647 "built-in render texture type 5 not found": a non-MSAA deferred camera has none). The pack
            // shader (FiresCoreImpostorPack, firescoreimpostor bundle 1.2.346+) takes coverage from the G-buffer normals instead.
            s_cb.Blit(BuiltinRenderTextureType.GBuffer0, s_tA, s_pack, 0);
            s_cb.Blit(BuiltinRenderTextureType.GBuffer0, s_tB, s_pack, 1);
            // clear the atlases (views outside the kind stay empty)
            var prev = RenderTexture.active;
            RenderTexture.active = s_atlasA; GL.Clear(false, true, Color.clear);
            RenderTexture.active = s_atlasB; GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            s_stage.SetActive(true);
            return true;
        }

        private static void Alloc(ref RenderTexture rt, int size, int depth, RenderTextureReadWrite rw, bool mips)
        {
            if (rt != null && rt.width == size && rt.useMipMap == mips && rt.sRGB == (rw == RenderTextureReadWrite.sRGB)) return;
            if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            rt = new RenderTexture(size, size, depth, RenderTextureFormat.ARGB32, rw) { useMipMap = mips, autoGenerateMips = false, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave, name = "FiresEBM_ImpostorBake" };
            rt.Create();
        }

        // 1.2.345: views per frame inside a budget: LoadingBudgetMs under the loading screen, [Far Impostors] Bake budget ms after it. After
        // load a view whose last cost was over the budget still runs (one view is the smallest step), but the next frame is skipped so the
        // average stays near the budget; every such view is counted (census OverBudgetViews).
        private static void RenderOne()
        {
            if (s_kind.State != ImpostorKinds.State.Baking) { Abort("the kind went away"); return; }
            if (s_skipFrames > 0) { s_skipFrames--; DeferredFrames++; return; }
            bool loading = Loading();
            double budget = loading ? LoadingBudgetMs : ImpostorEngine.BakeBudgetMs;
            var frame = Stopwatch.StartNew();
            do
            {
                if (!RenderView()) return;
                if (!loading && s_lastViewMs > budget) { OverBudgetViews++; s_skipFrames = 1; return; }
            }
            while (s_phase == Phase.Render && frame.Elapsed.TotalMilliseconds + s_lastViewMs <= budget);
        }

        // one view; false when the kind is done with views (or stopped)
        private static bool RenderView()
        {
            s_sw.Restart();
            int i = s_view % ImpostorKinds.Views, j = s_view / ImpostorKinds.Views;
            Vector3 d = HemiOctDecode(new Vector2((i + 0.5f) / ImpostorKinds.Views, (j + 0.5f) / ImpostorKinds.Views));
            float r = s_kind.Radius;
            Vector3 centre = StageOrigin + s_kind.Bounds.center;
            Vector3 f = -d;
            Vector3 up = Vector3.Cross(Vector3.up, f).sqrMagnitude < 1e-8f ? Vector3.forward : Vector3.up;
            float dist = r / Mathf.Tan(BakeFovDeg * 0.5f * Mathf.Deg2Rad);   // the sphere of radius r just fills the 1 degree view
            s_cam.transform.SetPositionAndRotation(centre + d * dist, Quaternion.LookRotation(f, up));
            s_cam.nearClipPlane = Mathf.Max(0.1f, dist - r * 1.5f); s_cam.farClipPlane = dist + r * 1.5f;
            s_cam.Render();
            if (s_pathUsed == null) s_pathUsed = s_cam.actualRenderingPath.ToString();
            Graphics.CopyTexture(s_tA, 0, 0, 0, 0, s_px, s_px, s_atlasA, 0, 0, i * s_px, j * s_px);
            Graphics.CopyTexture(s_tB, 0, 0, 0, 0, s_px, s_px, s_atlasB, 0, 0, i * s_px, j * s_px);
            double ms = s_sw.Elapsed.TotalMilliseconds; SpentMs += ms; if (ms > WorstViewMs) WorstViewMs = ms; s_lastViewMs = ms;
            if (++s_view < ImpostorKinds.Views * ImpostorKinds.Views) return true;

            // all views: mips on the GPU, then read every mip back without a stall
            s_stage.SetActive(false); UnityEngine.Object.Destroy(s_stage); s_stage = null;
            s_atlasA.GenerateMips(); s_atlasB.GenerateMips();
            s_readA = new byte[s_mips][]; s_readB = new byte[s_mips][]; s_readsLeft = s_mips * 2; s_readFailed = false;
            int serial = ++s_readSerial;   // 1.2.356
            for (int m = 0; m < s_mips; m++)
            {
                int mip = m;
                // 1.2.356: copied into the pooled buffer for this atlas + mip (was a new array per readback)
                AsyncGPUReadback.Request(s_atlasA, mip, TextureFormat.RGBA32, req => { if (serial != s_readSerial) return; if (req.hasError) s_readFailed = true; else { var d = req.GetData<byte>(); var buf = PoolBuf(ref s_poolA, mip, d.Length); d.CopyTo(buf); s_readA[mip] = buf; } s_readsLeft--; });
                AsyncGPUReadback.Request(s_atlasB, mip, TextureFormat.RGBA32, req => { if (serial != s_readSerial) return; if (req.hasError) s_readFailed = true; else { var d = req.GetData<byte>(); var buf = PoolBuf(ref s_poolB, mip, d.Length); d.CopyTo(buf); s_readB[mip] = buf; } s_readsLeft--; });
            }
            s_phase = Phase.Readback;
            return false;
        }

        private static void StartWork()
        {
            if (s_readFailed) { Abort("the GPU readback failed"); return; }
            var a = s_readA; var b = s_readB; int size = s_size, mips = s_mips; float r = s_kind.Radius;
            s_readA = s_readB = null;
            s_task = Task.Run(() => Work(a, b, size, mips, r));
            s_phase = Phase.Work;
        }

        // worker: cutoffs per mip (keep mip 0's covered share), silhouette area per view, DXT5 chains
        private static Result Work(byte[][] a, byte[][] b, int size, int mips, float radius)
        {
            var res = new Result { Cutoffs = new float[8], Areas = new float[ImpostorKinds.Views * ImpostorKinds.Views] };
            var hist0 = Hist(a[0]); int n0 = a[0].Length / 4;
            int covered0 = 0; for (int t = 128; t < 256; t++) covered0 += hist0[t];
            float share = n0 > 0 ? covered0 / (float)n0 : 0f;
            res.Coverage = share;
            if (covered0 == 0) { res.Why = "nothing reached the G-buffer (its shader draws no deferred pass)"; return res; }
            for (int m = 0; m < 8; m++)
            {
                if (m == 0 || m >= mips) { res.Cutoffs[m] = m == 0 ? 0.5f : res.Cutoffs[m - 1]; continue; }
                var h = Hist(a[m]); int n = a[m].Length / 4, want = (int)(share * n), acc = 0, t2 = 255;
                for (; t2 > 0; t2--) { acc += h[t2]; if (acc >= want) break; }
                res.Cutoffs[m] = Mathf.Clamp(t2 / 255f, 0.05f, 0.5f);
            }
            int px = size / ImpostorKinds.Views; float texel = 2f * radius / px;
            for (int v = 0; v < res.Areas.Length; v++)
            {
                int vi = v % ImpostorKinds.Views, vj = v / ImpostorKinds.Views, c = 0;
                for (int y = vj * px; y < (vj + 1) * px; y++) for (int x = vi * px; x < (vi + 1) * px; x++) if (a[0][(y * size + x) * 4 + 3] >= 128) c++;
                res.Areas[v] = c * texel * texel;
            }
            res.A = Chain(a, size, mips); res.B = Chain(b, size, mips);
            return res;
        }

        private static int[] Hist(byte[] rgba) { var h = new int[256]; for (int i = 3; i < rgba.Length; i += 4) h[rgba[i]]++; return h; }

        private static byte[] Chain(byte[][] mipsRgba, int size, int mips)
        {
            int total = 0; for (int m = 0, s = size; m < mips; m++, s >>= 1) total += ImpostorDxt.Size(s, s);
            var o = new byte[total]; int off = 0;
            for (int m = 0, s = size; m < mips; m++, s >>= 1) { ImpostorDxt.Encode(mipsRgba[m], s, s, o, off); off += ImpostorDxt.Size(s, s); }
            return o;
        }

        private static void Finish()
        {
            var task = s_task; s_task = null;
            if (task.IsFaulted || task.Result == null) { var ex = task.Exception?.GetBaseException(); string at = ex?.StackTrace != null ? ex.StackTrace.Split('\n')[0].Trim() : "?"; Abort("the bake work failed: " + ex?.Message + " at " + at + $" (size {s_size}, px {s_px}, mips {s_mips}, views {ImpostorKinds.Views})"); return; }   // 1.2.346: run_8 cycled 240 times on "Index was outside the bounds of the array" with no frame named
            var r = task.Result;
            if (r.Why != null) { s_kind.State = ImpostorKinds.State.Refused; s_kind.Why = r.Why; EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {s_kind.Name} refused: {r.Why} (bake camera path {s_pathUsed ?? "?"})"); Done(); return; }
            Apply(r, false);
            if (ImpostorEngine.CacheOnDisk && s_bakeKey != null)   // 1.2.478: null = a runtime-made texture, no disk copy
            {
                string key = s_bakeKey; var bytes = Encode(r, s_kindLast);
                Task.Run(() => ImpostorDiskCache.Write("impostor-atlas", key, bytes));
            }
        }

        private static ImpostorKinds.Kind s_kindLast;
        private const int MaxFails = 3;   // 1.2.346
        private static readonly System.Collections.Generic.Dictionary<string, int> s_fails = new System.Collections.Generic.Dictionary<string, int>();

        private static void Apply(Result r, bool fromCache)
        {
            var k = s_kind; s_kindLast = k;
            s_sw.Restart();
            ImpostorAtlas.Upload(s_pair, k.Id, r.A, r.B);
            double ms = s_sw.Elapsed.TotalMilliseconds; SpentMs += ms;
            k.Cutoffs = r.Cutoffs; k.Areas = r.Areas; k.Dirty = false;
            k.State = ImpostorKinds.State.Ready;
            if (s_pair == ImpostorAtlas.Current) { bool was = k.HasSlice; k.HasSlice = true; if (was) ImpostorKinds.LookRevision++; }
            KindsBaked++; if (fromCache) FromCache++;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {k.Name} {(fromCache ? "read from the disk cache" : "baked (bake camera path " + (s_pathUsed ?? "?") + ")")}: {s_size} px, coverage {r.Coverage:P0}, upload {ms:0.0} ms, cutoffs {string.Join("/", Array.ConvertAll(r.Cutoffs, c => c.ToString("0.00")))}");
            if (ImpostorAtlas.TrySwap()) foreach (var kk in ImpostorKinds.All) if (kk.State == ImpostorKinds.State.Ready) kk.HasSlice = true;
            ImpostorDraw.KindsChanged();
            Done();
        }

        private static void Abort(string why)
        {
            if (s_kind != null)
            {
                // 1.2.346 ([lead], run_8: 240 throws in 2 minutes, kinds re-queued forever): a kind that fails MaxFails times is refused with the
                // reason (Info, once per kind) instead of going back to the queue; "the kind went away" is not a failure of the bake.
                int fails = 0;
                if (why != "the kind went away") { s_fails.TryGetValue(s_kind.Name, out fails); s_fails[s_kind.Name] = ++fails; }
                if (fails >= MaxFails)
                {
                    s_kind.State = ImpostorKinds.State.Refused; s_kind.Why = $"bake failed {fails} times: {why}";
                    EasyBakeLog.Info(ImpostorEngine.Tag + $"kind {s_kind.Name} refused after {fails} failed bakes: {why}");
                }
                else
                {
                    EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {s_kind.Name} bake stopped ({fails} of {MaxFails}): {why}");
                    if (s_kind.State == ImpostorKinds.State.Baking) s_kind.State = ImpostorKinds.State.Queued; s_kind.NeedDistance += 1e6f;
                }
            }
            Done();
        }

        private static void Done()
        {
            if (s_stage != null) { UnityEngine.Object.Destroy(s_stage); s_stage = null; }
            s_kind = null; s_pair = null; s_phase = Phase.Idle;
            s_readSerial++;   // 1.2.356: a readback still in flight for this bake writes nothing
        }

        internal static void Release()
        {
            Done(); s_task = null; s_readA = s_readB = null; s_poolA = s_poolB = null;   // 1.2.356: the pooled readback buffers go with the bake
            foreach (var rt in new[] { s_camRT, s_tA, s_tB, s_atlasA, s_atlasB }) if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            s_camRT = s_tA = s_tB = s_atlasA = s_atlasB = null;
            if (s_cam != null && s_cb != null) s_cam.RemoveCommandBuffer(CameraEvent.AfterGBuffer, s_cb);
            s_cb?.Release(); s_cb = null;
            if (s_camGo != null) UnityEngine.Object.Destroy(s_camGo); s_camGo = null; s_cam = null;
            if (s_pack != null) UnityEngine.Object.Destroy(s_pack); s_pack = null;
        }

        // ── shared maths (same as FiresCoreImpostorCommon.cginc) ──
        internal static Vector3 HemiOctDecode(Vector2 e)
        {
            e = e * 2f - Vector2.one;
            var p = new Vector2((e.x + e.y) * 0.5f, (e.x - e.y) * 0.5f);
            float y = 1f - Mathf.Abs(p.x) - Mathf.Abs(p.y);
            return new Vector3(p.x, y, p.y).normalized;
        }

        // ── disk cache ──
        // 1.2.478: one Info each time the bake queue empties after new work: the proof that a warm launch reads every kind from the disk cache
        private static int s_saidAtKinds = -1;
        private static string s_bakeKey;      // the key read in Begin, written in Finish (null = not disk-cached)
        private static int s_noDiskKinds;     // bakes of kinds with a runtime-made texture that has no content hash (no disk copy)
        private const int KeyWaitFrames = 120;   // ~2 s at 60 fps: the longest a kind waits for its textures' content hashes
        private static int s_keyWaitFrames, s_keyWaits;
        private static void SayQueueEmpty()
        {
            if (KindsBaked == s_saidAtKinds) return;
            s_saidAtKinds = KindsBaked;
            EasyBakeLog.Info(ImpostorEngine.Tag + $"bake queue empty (1.2.478): {KindsBaked} kind(s) this session, {FromCache} read from the disk cache, {KindsBaked - FromCache} baked ({s_noDiskKinds} of them not disk-cached: a runtime-made texture without a content hash, {s_keyWaits} of those timed out waiting); disk key: {ImpostorKinds.DiskTextures} texture identity(ies), {ImpostorKinds.DiskCollisions} shared by two different textures; runtime-made textures: {ImpostorKinds.RtHashed} content-hashed, {ImpostorKinds.RtFailed} failed, {ImpostorKinds.RtStartMs:0.00} ms main thread in all, slowest readback {ImpostorKinds.RtWorstFrames:0} frame(s)");
        }

        // 1.2.478: launch-stable texture identity (was k.Signature with instance IDs: a new key, a re-bake and an orphan file on some launches), imp-v1 -> imp-v2;
        // + the mod-set tag (game version + every plugin's GUID@version, FarBuildingSources.ModTag), so a mod update that changes a texture under the same name re-bakes.
        // null = no disk copy (a runtime-made texture without a content hash; pending = its hash is still on its way).
        private static string CacheKey(ImpostorKinds.Kind k, out bool pending)
        {
            string sig = ImpostorKinds.DiskSignatureOf(k, out pending);
            return sig == null ? null : ImpostorDiskCache.Key("imp-v2", sig, s_size, Application.version, Version.Build, FarBuildingSources.ModTag);
        }

        private static byte[] Encode(Result r, ImpostorKinds.Kind k)
        {
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(0x504D4946); w.Write(1); w.Write(s_size); w.Write(s_mips);
                for (int i = 0; i < 8; i++) w.Write(r.Cutoffs[i]);
                w.Write(r.Areas.Length); foreach (var a in r.Areas) w.Write(a);
                w.Write(r.Coverage);
                w.Write(r.A.Length); w.Write(r.A); w.Write(r.B.Length); w.Write(r.B);
                return ms.ToArray();
            }
        }

        private static bool Decode(byte[] bytes, out Result r)
        {
            r = null;
            try
            {
                using (var rd = new BinaryReader(new MemoryStream(bytes)))
                {
                    if (rd.ReadInt32() != 0x504D4946 || rd.ReadInt32() != 1) return false;
                    if (rd.ReadInt32() != s_size || rd.ReadInt32() != s_mips) return false;
                    var res = new Result { Cutoffs = new float[8] };
                    for (int i = 0; i < 8; i++) res.Cutoffs[i] = rd.ReadSingle();
                    int na = rd.ReadInt32(); if (na != ImpostorKinds.Views * ImpostorKinds.Views) return false;
                    res.Areas = new float[na]; for (int i = 0; i < na; i++) res.Areas[i] = rd.ReadSingle();
                    res.Coverage = rd.ReadSingle();
                    res.A = rd.ReadBytes(rd.ReadInt32()); res.B = rd.ReadBytes(rd.ReadInt32());
                    int want = 0; for (int m = 0, s = s_size; m < s_mips; m++, s >>= 1) want += ImpostorDxt.Size(s, s);
                    if (res.A.Length != want || res.B.Length != want) return false;
                    r = res; return true;
                }
            }
            catch { return false; }
        }

        private static class Version { internal const int Build = 3; }   // 3: 1.2.346 mip chain stops at the last multiple of 4 (ImpostorAtlas.MipsFor) + G-buffer coverage   // bump when the bake output changes (2: 1.2.345 perspective 1 degree bake camera)
    }
}
