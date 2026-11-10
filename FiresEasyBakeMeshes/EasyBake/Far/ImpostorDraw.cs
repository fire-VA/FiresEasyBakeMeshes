using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The engine's frame (SPEC §4): from FarRunner's late hook (after EBM's draws), on a client, while [Far Impostors] Enabled:
    /// bake step, pool upload, zone mask, then on the GPU: clear args, cull every pooled instance, fill the mid-LOD args; then ONE
    /// indirect draw for all impostors (all kinds share the atlas arrays) and one per mid-LOD mesh part. Main camera only. Stands down
    /// with one Info WHY line when the main camera is not deferred, compute / indirect is missing, or the shaders are not in this EBM.
    /// Timed per phase for the 30 s Debug line; everything it made is destroyed when it turns off or the world unloads.
    /// </summary>
    internal static class ImpostorDraw
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct KindGpu { public Vector4 CentreRadius, Params, CutA, CutB; }   // 64 bytes, = ImpKind

        internal static bool Running;
        private static bool s_registered, s_saidOn, s_kindsDirty = true, s_poolDirty = true;
        private static string s_downWhy; private static float s_nextDownCheck;
        private static ComputeBuffer s_kindsBuf, s_impVisible, s_impArgs, s_midVisible, s_midCount, s_midBase, s_midCap, s_midParts, s_midArgs;
        private static readonly List<(ImpostorKinds.Kind kind, ImpostorKinds.MidPart part)> s_midList = new List<(ImpostorKinds.Kind, ImpostorKinds.MidPart)>();
        private static Material s_impMat; private static MaterialPropertyBlock s_impMpb; private static readonly List<MaterialPropertyBlock> s_midMpbs = new List<MaterialPropertyBlock>();
        private static Mesh s_quad;
        private static int kClear, kCull, kFill;
        private static readonly Plane[] s_planes = new Plane[6]; private static readonly Vector4[] s_planeVec = new Vector4[6];
        private static readonly Stopwatch s_sw = new Stopwatch();
        private static double s_cullMs, s_drawMs; private static int s_frames; private static float s_nextLog;
        private static float s_snow; private static bool s_vedrSnow; private static float s_nextSnow;

        internal static void Ensure()
        {
            if (s_registered) return;
            s_registered = true;
            FarRunner.Register("FarImpostors", null, Late);
        }

        internal static void KindsChanged() { s_kindsDirty = true; }
        internal static void PoolResized() { s_poolDirty = true; }

        private static void Late()
        {
            if (!ImpostorEngine.Live() || !ImpostorEngine.Enabled || ZNet.instance == null || ZNetScene.instance == null)
            {
                if (Running || ZNet.instance == null) Stop(ZNet.instance == null ? "world unloaded" : "turned off");
                return;
            }
            var cam = Camera.main;
            if (cam == null) return;
            if (!CanRun(cam)) { if (Running) Stop("stood down: " + s_downWhy); return; }
            if (!Running) { Running = true; ImpostorEngine.RaiseReady(); }

            ImpostorKinds.PollSignatures();
            ImpostorBaker.Step();
            ImpostorPool.Upload();
            ImpostorHandOff.Tick();
            if (ImpostorPool.Buffer == null || ImpostorAtlas.Current == null) return;
            if (!s_saidOn && ImpostorKinds.Count > 0) { s_saidOn = true; SayOn(); }

            s_sw.Restart();
            Buffers();
            Snow();
            Cull(cam);
            s_cullMs += s_sw.Elapsed.TotalMilliseconds; s_sw.Restart();
            Draw(cam);
            s_drawMs += s_sw.Elapsed.TotalMilliseconds; s_frames++;
            if (Time.unscaledTime >= s_nextLog) { s_nextLog = Time.unscaledTime + 30f; Report(); }
        }

        private static bool CanRun(Camera cam)
        {
            if (s_downWhy == null && Time.unscaledTime < s_nextDownCheck) return true;
            if (Time.unscaledTime < s_nextDownCheck) return false;
            s_nextDownCheck = Time.unscaledTime + 5f;
            string why = null;
            if (!SystemInfo.supportsComputeShaders) why = "this GPU has no compute shaders";
            else if (!SystemInfo.supportsInstancing) why = "this GPU has no instancing";
            else if (!ImpostorAssets.Ready()) why = ImpostorAssets.Why;
            else if (cam.actualRenderingPath != RenderingPath.DeferredShading) why = $"the main camera renders {cam.actualRenderingPath}, not deferred";
            if (why != s_downWhy)
            {
                if (why != null) EasyBakeLog.Info(ImpostorEngine.Tag + "stands down: " + why);
                else if (s_downWhy != null) EasyBakeLog.Info(ImpostorEngine.Tag + "can draw again (" + s_downWhy + " no longer applies)");
                s_downWhy = why;
            }
            return why == null;
        }

        private static void SayOn()
        {
            int fromCache = ImpostorBaker.FromCache;
            EasyBakeLog.Info(ImpostorEngine.Tag + $"on: {ImpostorKinds.Count} kind(s), sharpness {ImpostorEngine.SharpnessNow} ({ImpostorAtlas.PxPerView(ImpostorEngine.SharpnessNow)} px a view), mid-LOD trees {(ImpostorEngine.MidLodOn ? "on" : "off")}, reach {ImpostorEngine.Distance:0} m; atlases bake in the background ({fromCache} read from the disk cache so far)");
        }

        // ── buffers ──
        private static void Buffers()
        {
            int kinds = Math.Max(1, ImpostorKinds.Count);
            if (s_impMat == null)
            {
                s_impMat = new Material(ImpostorAssets.Impostor) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
                s_impMpb = new MaterialPropertyBlock();
                s_quad = MakeQuad();
                var cs = ImpostorAssets.Cull; kClear = cs.FindKernel("ClearArgs"); kCull = cs.FindKernel("Cull"); kFill = cs.FindKernel("FillMidArgs");
                s_impArgs = new ComputeBuffer(5, 4, ComputeBufferType.IndirectArguments) { name = "FiresEBM_FarImpArgs" };
            }
            if (s_kindsBuf == null || s_kindsBuf.count < kinds) { s_kindsBuf?.Release(); s_kindsBuf = new ComputeBuffer(Math.Max(16, kinds), 64) { name = "FiresEBM_FarKinds" }; s_kindsDirty = true; }
            if (s_kindsDirty) { UploadKinds(kinds); s_kindsDirty = false; ImpostorPool.CountsChanged = true; }
            if (s_poolDirty || s_impVisible == null || s_impVisible.count < ImpostorPool.Capacity)
            {
                s_impVisible?.Release();
                s_impVisible = new ComputeBuffer(Math.Max(1, ImpostorPool.Capacity), 4) { name = "FiresEBM_FarImpVisible" };
                s_poolDirty = false;
            }
            if (ImpostorPool.CountsChanged) { MidSegments(kinds); ImpostorPool.CountsChanged = false; }
        }

        private static void UploadKinds(int kinds)
        {
            var data = new KindGpu[s_kindsBuf.count];
            foreach (var k in ImpostorKinds.All)
            {
                bool draw = k.HasSlice && k.State != ImpostorKinds.State.Refused;
                var c = k.Cutoffs;
                data[k.Id] = new KindGpu
                {
                    CentreRadius = new Vector4(k.Bounds.center.x, k.Bounds.center.y, k.Bounds.center.z, k.Radius),
                    Params = new Vector4(draw ? ImpostorKinds.ReachOf(k) : 0f, k.Options.Distant ? 1f : 0f, k.Options.ReachScale, ImpostorAtlas.Current != null ? ImpostorAtlas.Current.Size : 1024),
                    CutA = new Vector4(c[0], c[1], c[2], c[3]), CutB = new Vector4(c[4], c[5], c[6], c[7]),
                };
            }
            s_kindsBuf.SetData(data);
        }

        // mid-LOD: one segment per kind (its live count), one args entry per mesh part
        private static void MidSegments(int kinds)
        {
            s_midList.Clear();
            var bases = new uint[kinds]; var caps = new uint[kinds]; uint total = 0;
            bool mid = ImpostorEngine.MidLodOn && ImpostorAssets.Mid != null;
            foreach (var k in ImpostorKinds.All)
            {
                if (!mid || k.Mid.Count == 0 || k.Id >= ImpostorPool.KindCounts.Length) continue;
                uint n = (uint)Math.Max(0, ImpostorPool.KindCounts[k.Id]);
                if (n == 0) continue;
                bases[k.Id] = total; caps[k.Id] = n; total += n;
                foreach (var p in k.Mid) s_midList.Add((k, p));
            }
            Realloc(ref s_midVisible, (int)Math.Max(1, total), 4, ComputeBufferType.Default, "FiresEBM_FarMidVisible");
            Realloc(ref s_midCount, kinds, 4, ComputeBufferType.Default, "FiresEBM_FarMidCount");
            Realloc(ref s_midBase, kinds, 4, ComputeBufferType.Default, "FiresEBM_FarMidBase"); s_midBase.SetData(bases);
            Realloc(ref s_midCap, kinds, 4, ComputeBufferType.Default, "FiresEBM_FarMidCap"); s_midCap.SetData(caps);
            int parts = Math.Max(1, s_midList.Count);
            Realloc(ref s_midParts, parts, 16, ComputeBufferType.Default, "FiresEBM_FarMidParts");
            var pd = new uint[parts * 4];
            for (int i = 0; i < s_midList.Count; i++)
            {
                var (k, p) = s_midList[i];
                pd[i * 4] = (uint)k.Id; pd[i * 4 + 1] = p.Mesh.GetIndexCount(p.SubMesh); pd[i * 4 + 2] = p.Mesh.GetIndexStart(p.SubMesh); pd[i * 4 + 3] = (uint)p.Mesh.GetBaseVertex(p.SubMesh);
            }
            s_midParts.SetData(pd);
            Realloc(ref s_midArgs, parts * 5, 4, ComputeBufferType.IndirectArguments, "FiresEBM_FarMidArgs");
            while (s_midMpbs.Count < s_midList.Count) s_midMpbs.Add(new MaterialPropertyBlock());
        }

        private static void Realloc(ref ComputeBuffer b, int count, int stride, ComputeBufferType type, string name)
        {
            if (b != null && b.count >= count) return;
            b?.Release(); b = new ComputeBuffer(Math.Max(1, count), stride, type) { name = name };
        }

        private static void Snow()
        {
            if (Time.unscaledTime < s_nextSnow) return;
            s_nextSnow = Time.unscaledTime + 2f;
            s_vedrSnow = VedrSnow(out s_snow);
        }

        // 1.2.210: the season snow amount PUSHED through EbmApi.RegisterFarSnow (was a type-name lookup of Vedr), else altitude snow on
        // mountain heights. s_vedrSnow keeps its name: true = a season amount is live.
        private static bool VedrSnow(out float amount) => FarSnow.WorldAmount(out amount);

        // ── GPU cull ──
        private static void Cull(Camera cam)
        {
            var cs = ImpostorAssets.Cull;
            GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
            for (int i = 0; i < 6; i++) s_planeVec[i] = new Vector4(s_planes[i].normal.x, s_planes[i].normal.y, s_planes[i].normal.z, s_planes[i].distance);
            int kinds = Math.Max(1, ImpostorKinds.Count);
            cs.SetVector("_WorldSpaceCameraPos", cam.transform.position);
            cs.SetFloat("_ImpDistance", ImpostorEngine.Distance);
            cs.SetVectorArray("_Planes", s_planeVec);
            cs.SetFloat("_Margin", 15f);
            cs.SetFloat("_MidDistance", ImpostorEngine.MidLodOn && s_midList.Count > 0 ? ImpostorEngine.MidDistance : 0f);
            cs.SetInt("_Count", ImpostorPool.HighWater);
            cs.SetInt("_ImpCap", s_impVisible.count);
            cs.SetInt("_KindCount", ImpostorKinds.Count);
            cs.SetInt("_PartCount", s_midList.Count);
            cs.SetInts("_MaskOrigin", ImpostorHandOff.Origin.x, ImpostorHandOff.Origin.y);

            cs.SetBuffer(kClear, "_ImpArgs", s_impArgs); cs.SetBuffer(kClear, "_MidCount", s_midCount);
            cs.Dispatch(kClear, Math.Max(1, (kinds + 63) / 64), 1, 1);

            cs.SetBuffer(kCull, "_ImpInstances", ImpostorPool.Buffer); cs.SetBuffer(kCull, "_ImpKinds", s_kindsBuf);
            cs.SetBuffer(kCull, "_ImpVisibleRW", s_impVisible); cs.SetBuffer(kCull, "_MidVisibleRW", s_midVisible);
            cs.SetBuffer(kCull, "_ImpArgs", s_impArgs); cs.SetBuffer(kCull, "_MidCount", s_midCount);
            cs.SetBuffer(kCull, "_MidBase", s_midBase); cs.SetBuffer(kCull, "_MidCap", s_midCap);
            cs.SetTexture(kCull, "_ZoneMask", ImpostorHandOff.Texture);
            if (ImpostorPool.HighWater > 0) cs.Dispatch(kCull, (ImpostorPool.HighWater + 63) / 64, 1, 1);

            cs.SetBuffer(kFill, "_ImpArgs", s_impArgs); cs.SetBuffer(kFill, "_MidCount", s_midCount); cs.SetBuffer(kFill, "_MidCap", s_midCap);
            cs.SetBuffer(kFill, "_MidParts", s_midParts); cs.SetBuffer(kFill, "_MidArgs", s_midArgs);
            cs.Dispatch(kFill, Math.Max(1, (s_midList.Count + 63) / 64), 1, 1);
        }

        // ── draw ──
        private static void Draw(Camera cam)
        {
            var bounds = new Bounds(cam.transform.position, Vector3.one * (ImpostorEngine.Distance * 2f + 1000f));
            var atlas = ImpostorAtlas.Current;
            float altSnow = s_vedrSnow ? 0f : 1f;
            if (ImpostorEngine.SmoothViews) s_impMat.EnableKeyword("FIRES_IMP_SMOOTH"); else s_impMat.DisableKeyword("FIRES_IMP_SMOOTH");
            s_impMpb.SetBuffer("_ImpInstances", ImpostorPool.Buffer);
            s_impMpb.SetBuffer("_ImpKinds", s_kindsBuf);
            s_impMpb.SetBuffer("_ImpVisible", s_impVisible);
            s_impMpb.SetTexture("_ImpAlbedo", atlas.Albedo);
            s_impMpb.SetTexture("_ImpNormal", atlas.Normal);
            s_impMpb.SetFloat("_ImpViews", ImpostorKinds.Views);
            s_impMpb.SetFloat("_ImpDistance", ImpostorEngine.Distance);
            s_impMpb.SetFloat("_FarSnow", s_snow); s_impMpb.SetFloat("_FarAltSnow", altSnow); s_impMpb.SetFloat("_FarSnowLine", 110f);
            Graphics.DrawMeshInstancedIndirect(s_quad, 0, s_impMat, bounds, s_impArgs, 0, s_impMpb, ShadowCastingMode.Off, true, 0, cam, LightProbeUsage.Off);
            if (FiresEasyBakeMeshes.EasyBake.Indirect.IndirectStats.SplitOn) FiresEasyBakeMeshes.EasyBake.Indirect.IndirectStats.NoteSplitImpostors(1 + s_midList.Count);   // 1.2.254

            for (int i = 0; i < s_midList.Count; i++)
            {
                var (k, p) = s_midList[i]; var mpb = s_midMpbs[i];
                mpb.SetBuffer("_ImpInstances", ImpostorPool.Buffer);
                mpb.SetBuffer("_ImpKinds", s_kindsBuf);
                mpb.SetBuffer("_MidVisible", s_midVisible);
                mpb.SetInt("_MidBaseK", (int)MidBase(k.Id));
                mpb.SetMatrix("_PartMatrix", p.ToRoot);
                mpb.SetFloat("_ImpDistance", ImpostorEngine.Distance);
                mpb.SetFloat("_FarSnow", s_snow); mpb.SetFloat("_FarAltSnow", altSnow); mpb.SetFloat("_FarSnowLine", 110f);
                Graphics.DrawMeshInstancedIndirect(p.Mesh, p.SubMesh, p.Material, bounds, s_midArgs, i * 20, mpb, ShadowCastingMode.Off, true, 0, cam, LightProbeUsage.Off);
            }
        }

        private static readonly uint[] s_baseRead = new uint[1];
        private static uint MidBase(int kind)
        {
            // segment starts are computed on the CPU (MidSegments); recompute the same prefix here instead of reading the GPU
            uint total = 0;
            foreach (var k in ImpostorKinds.All)
            {
                if (k.Id == kind) return total;
                if (k.Mid.Count == 0 || k.Id >= ImpostorPool.KindCounts.Length) continue;
                total += (uint)Math.Max(0, ImpostorPool.KindCounts[k.Id]);
            }
            return total;
        }

        private static Mesh MakeQuad()
        {
            var m = new Mesh { name = "FiresEBM_FarQuad", hideFlags = HideFlags.HideAndDontSave };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            m.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            return m;
        }

        private static void Report()
        {
            if (s_frames == 0) return;
            long vram = ImpostorAtlas.Current != null ? ImpostorAtlas.Current.Bytes : 0;
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"last 30 s: cull dispatch {s_cullMs / s_frames * 1000:0} us, draw submit {s_drawMs / s_frames * 1000:0} us a frame; bake {ImpostorBaker.SpentMs:0} ms total (worst view {ImpostorBaker.WorstViewMs:0.0} ms, {ImpostorBaker.KindsBaked} kind(s) done{(ImpostorBaker.Busy ? ", baking " + ImpostorBaker.BakingName : "")}); upload {ImpostorPool.SpentMs:0} ms, {ImpostorPool.Uploaded} instance(s); {ImpostorPool.StatusText()}; atlas {vram / (1024 * 1024)} MB; zones handed off {ImpostorHandOff.StandingZones}; snow {(s_vedrSnow ? "Vedr " + s_snow.ToString("0.00") : "by altitude")}");
            s_cullMs = s_drawMs = 0; s_frames = 0; ImpostorBaker.SpentMs = 0; ImpostorBaker.WorstViewMs = 0; ImpostorPool.SpentMs = 0; ImpostorPool.Uploaded = 0;
        }

        internal static string StatusText() => Running ? $"drawing (main camera, {ImpostorAtlas.Current?.Size ?? 0} px atlases)" : s_downWhy != null ? "stood down: " + s_downWhy : "idle";

        private static void Stop(string why)
        {
            bool was = Running || ImpostorPool.Buffer != null || ImpostorKinds.Count > 0;
            Running = false; s_saidOn = false;
            ImpostorBaker.Release();
            ImpostorPool.Clear(); ImpostorHandOff.Clear();
            if (why == "world unloaded") { ImpostorKinds.Clear(); ImpostorAtlas.Clear(); }
            foreach (var b in new[] { s_kindsBuf, s_impVisible, s_impArgs, s_midVisible, s_midCount, s_midBase, s_midCap, s_midParts, s_midArgs }) b?.Release();
            s_kindsBuf = s_impVisible = s_impArgs = s_midVisible = s_midCount = s_midBase = s_midCap = s_midParts = s_midArgs = null;
            if (s_impMat != null) UnityEngine.Object.Destroy(s_impMat); s_impMat = null;
            if (s_quad != null) UnityEngine.Object.Destroy(s_quad); s_quad = null;
            s_midList.Clear(); s_kindsDirty = true; s_poolDirty = true;
            if (was) EasyBakeLog.Debug(ImpostorEngine.Tag + "off (" + why + "): every buffer, texture and camera released");
        }
    }
}
