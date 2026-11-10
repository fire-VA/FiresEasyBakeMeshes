using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // E31 ([seasons] 11-09, STUDY_sections\round2_status_EBM.md row E31; DROP_NOTES_seasons_fx.md). EBM's own code.
    // Existing fixes, unchanged: RainShelter.cs (falling rain stops while the local player is InShelter: every path, player-based) and
    // WetOverride.cs (_Wet 0 on roofed / overhead pieces). Neither touches the sky-depth mask itself.
    // The mask: vanilla DepthCamera.RenderDepth, once a second, RenderWithShader(Unlit/DepthWrite, "RenderType") into _SkyAlphaTexture.
    //  - EBM combined zone meshes: ordinary MeshRenderers, drawn by DepthWrite correctly.
    //  - EBM INDIRECT instances: their shaders are tagged RenderType Opaque, so DepthWrite replaces them, but DepthWrite takes the transform
    //    from unity_ObjectToWorld and never runs EBM's procedural setup: each instance would land at its mesh's local position. They do not
    //    reach the mask where they stand (and may stamp a false roof near the world origin).
    // 1. CENSUS (always, Debug every 30 s; one async read of the mask every 10 s): sampled indirect instances and combined zone meshes inside the
    //    depth camera's view, mask value vs the value their top would write: present / missing per path.
    // 2. [Optimize] IndirectSkyDepth (key; default to be set ON once the census proves the miss): in the depth camera's pre-cull EBM still
    //    culls view 6 for it but draws NOTHING there (no misplaced replaced draws); straight after vanilla's RenderDepth, the same batches are
    //    drawn into the same texture with EBM/SkyDepth (DepthWrite's output, procedural transforms), the depth camera's own matrices.
    // 3. [Optimize] SkyDepthThrottle (key, off): RenderDepth is skipped while its rounded position is unchanged and less than 5 s passed.
    //    Visible: a roof built or removed changes the rain mask up to 5 s later (vanilla: up to 1 s).
    internal static partial class EbmIndirect
    {
        internal static ConfigEntry<bool> SkyDepthOn, SkyDepthThrottleOn;
        private static Shader s_skyShader; private static bool s_skyLooked;
        private static readonly Dictionary<Material, Material> s_skyMats = new Dictionary<Material, Material>();
        private static CommandBuffer s_skyCb;
        private static int s_skyFrame = -1, s_skyW; private static bool s_skyCpu;
        private static int w_skyRecords, w_skyDraws, w_skyNoView, w_skyThrottled;

        internal static void BindSkyDepth(ConfigFile config)
        {
            SkyDepthOn = config.Bind("Optimize", "IndirectSkyDepth", false,
                "Roofs EBM draws on the graphics card are added to the game's rain and wet mask (the top-down map that says where open sky is), so rain does not show through a baked house seen from outside and wet looks stay right under it. The player standing under a roof is already covered by EBM's rain shelter. Off = as before.");
            SkyDepthThrottleOn = config.Bind("Optimize", "SkyDepthThrottle", false,
                "The game re-draws its rain mask every second. With this on it is skipped while you have not moved (at most every 5 s). Visible: a roof just built or removed changes where rain falls up to 5 s later.");
        }

        private static bool SkyDepthTakes(Camera cam) => SkyDepthOn != null && SkyDepthOn.Value && cam != null && cam.GetComponent<DepthCamera>() != null && SkyShader() != null;

        private static Shader SkyShader()
        {
            if (!s_skyLooked) { s_skyLooked = true; s_skyShader = EbmIndirectAssets.BundleShader("EBM/SkyDepth"); if (s_skyShader == null) EasyBakeLog.Info("[Indirect] sky depth: EBM's bundle has no EBM/SkyDepth shader; the rain-mask pass is off"); }
            return s_skyShader;
        }

        private static Material SkyMat(Material source)
        {
            if (source == null) return null;
            if (s_skyMats.TryGetValue(source, out var m) && m != null) return m;
            m = new Material(SkyShader()) { name = "EBM sky depth " + source.name, enableInstancing = true, hideFlags = HideFlags.HideAndDontSave };
            if (source.IsKeywordEnabled("EBM_ALPHATEST"))
            {
                m.EnableKeyword("EBM_ALPHATEST");
                if (source.HasProperty("_MainTex")) m.SetTexture("_MainTex", source.GetTexture("_MainTex"));
                if (source.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
            }
            s_skyMats[source] = m;
            return m;
        }

        /// <summary>From OtherCameraPreCull, after view 6 was culled for this camera: the depth camera's draws are ours (after RenderDepth).</summary>
        private static bool SkyDepthKeep(Camera cam, bool cpu, int w)
        {
            if (!SkyDepthTakes(cam)) return false;
            s_skyFrame = Time.frameCount; s_skyCpu = cpu; s_skyW = w;
            return true;
        }

        /// <summary>Straight after vanilla's RenderDepth: EBM's batches into the mask, from this render's view 6.</summary>
        private static void RecordSkyDepth(Camera cam, RenderTexture tex)
        {
            if (!SkyDepthTakes(cam) || tex == null || !Active || s_argsBuf == null) return;
            if (s_skyFrame != Time.frameCount) { w_skyNoView++; return; }   // view 6 was not culled for this render (one-pass path): draw nothing rather than guess
            if (s_skyCb == null) s_skyCb = new CommandBuffer { name = "EBM sky depth (rain mask)" };
            var cb = s_skyCb; cb.Clear();
            cb.SetRenderTarget(tex);
            cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
            int pass = tex.depth > 0 ? 0 : 1, mask = cam.cullingMask, n = 0;
            for (int i = 0; i < s_batches.Count; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0 || !HasMesh(b) || b.OtherProps == null) continue;
                if (s_skyCpu && ((mask & (1 << b.Layer)) == 0 || (i < s_skyW && !s_otherVis[i]))) continue;
                if (!s_skyCpu && (mask & (1 << b.Layer)) == 0) continue;
                var mat = SkyMat(b.Material);
                if (mat == null) continue;
                cb.DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, mat, pass, s_argsBuf, (OtherView * s_batchCap + i) * ArgWords * 4, b.OtherProps);
                n++;
            }
            Graphics.ExecuteCommandBuffer(cb);
            w_skyRecords++; w_skyDraws += n;
        }

        // ── census: is a sampled roof's top in the mask? ──
        private struct SkySample { public Vector2 Uv; public float Expect; public bool Indirect; }
        private static readonly List<SkySample> s_samples = new List<SkySample>(96), s_pendingSamples = new List<SkySample>(96);
        private static bool s_skyReading; private static float s_nextSkyRead, s_nextSkySay;
        private static int w_indSampled, w_indPresent, w_indMissing, w_cmbSampled, w_cmbPresent, w_cmbMissing;

        private static void SkyCensus(Camera cam, RenderTexture tex)
        {
            float now = Time.unscaledTime;
            if (now >= s_nextSkySay) SaySky(now);
            if (s_skyReading || now < s_nextSkyRead || tex == null || !EasyBakeLog.DebugOn()) return;
            s_nextSkyRead = now + 10f;
            s_samples.Clear();
            var w2c = cam.worldToCameraMatrix;
            // indirect: up to 64 instances inside the view, evenly through the slots
            if (Active && s_high > 0)
            {
                int step = Mathf.Max(1, s_high / 512);
                for (int s = 0; s < s_high && s_samples.Count < 64; s += step)
                {
                    uint bi = s_instBatch[s];
                    if (bi == 0 || bi > s_batches.Count) continue;
                    var b = s_batches[(int)bi - 1];
                    if ((cam.cullingMask & (1 << b.Layer)) == 0) continue;
                    Vector3 top = s_instPos[s] + Vector3.up * Mathf.Max(0f, b.LocalBounds.max.y);
                    AddSample(cam, w2c, top, true);
                }
            }
            // combined zone meshes: up to 32 batch tops
            int c = 0;
            ZoneTracker.ForEachBakedZone((coord, bake, stale) =>
            {
                var list = bake.ShowingFarTier ? bake.FarBatches : bake.Batches;
                if (list == null) return;
                for (int i = 0; i < list.Count && c < 32; i++)
                {
                    var mr = list[i].Combined != null ? list[i].Combined.GetComponent<MeshRenderer>() : null;
                    if (mr == null || !mr.enabled || !list[i].Combined.activeInHierarchy) continue;
                    var bb = mr.bounds;
                    if (AddSample(cam, w2c, new Vector3(bb.center.x, bb.max.y, bb.center.z), false)) c++;
                }
            });
            if (s_samples.Count == 0) return;
            s_pendingSamples.Clear(); s_pendingSamples.AddRange(s_samples);
            s_skyReading = true;
            AsyncGPUReadback.Request(tex, 0, TextureFormat.RFloat, req =>
            {
                s_skyReading = false;
                if (req.hasError) return;
                var data = req.GetData<float>();
                int width = req.width, height = req.height;
                foreach (var smp in s_pendingSamples)
                {
                    int x = Mathf.Clamp((int)(smp.Uv.x * width), 0, width - 1), y = Mathf.Clamp((int)(smp.Uv.y * height), 0, height - 1);
                    float v = data[y * width + x];
                    bool present = v <= smp.Expect + 0.02f;   // 2 m: the mask holds something at or above this top
                    if (smp.Indirect) { w_indSampled++; if (present) w_indPresent++; else w_indMissing++; }
                    else { w_cmbSampled++; if (present) w_cmbPresent++; else w_cmbMissing++; }
                }
            });
        }

        private static bool AddSample(Camera cam, Matrix4x4 w2c, Vector3 top, bool indirect)
        {
            Vector3 vp = cam.WorldToViewportPoint(top);
            if (vp.x < 0.02f || vp.x > 0.98f || vp.y < 0.02f || vp.y > 0.98f || vp.z <= 0f) return false;
            float expect = w2c.MultiplyPoint(top).magnitude * 0.01f;   // DepthWrite: length(view position) * 0.01
            s_samples.Add(new SkySample { Uv = new Vector2(vp.x, vp.y), Expect = expect, Indirect = indirect });
            return true;
        }

        private static void SaySky(float now)
        {
            if (s_nextSkySay > 0f && EasyBakeLog.DebugOn() && (w_indSampled + w_cmbSampled + w_skyRecords + w_skyThrottled) > 0)
                EasyBakeLog.Debug($"[SkyDepth] 30 s: rain mask samples: EBM indirect {w_indSampled} ({w_indPresent} present, {w_indMissing} MISSING), combined zone meshes {w_cmbSampled} ({w_cmbPresent} present, {w_cmbMissing} missing); " +
                    $"pass {(SkyDepthOn != null && SkyDepthOn.Value ? "on" : "off")}: {w_skyRecords} mask render(s) extended, {w_skyDraws} draw(s), {w_skyNoView} without a view; throttle {(SkyDepthThrottleOn != null && SkyDepthThrottleOn.Value ? "on" : "off")}: {w_skyThrottled} render(s) skipped");
            s_nextSkySay = now + 30f;
            w_indSampled = w_indPresent = w_indMissing = w_cmbSampled = w_cmbPresent = w_cmbMissing = 0;
            w_skyRecords = w_skyDraws = w_skyNoView = w_skyThrottled = 0;
        }

        // ── hooks on vanilla DepthCamera ──
        private static readonly AccessTools.FieldRef<DepthCamera, Camera> F_DepthCam = AccessTools.FieldRefAccess<DepthCamera, Camera>("m_camera");
        private static Vector3 s_lastDepthPos = new Vector3(float.NaN, 0, 0); private static float s_lastDepthTime = -999f;

        [HarmonyPatch(typeof(DepthCamera), "RenderDepth")]
        private static class DepthCamera_RenderDepth
        {
            private static bool Prefix(DepthCamera __instance)
            {
                if (SkyDepthThrottleOn == null || !SkyDepthThrottleOn.Value) return true;
                var main = Utils.GetMainCamera();
                if (main == null) return true;
                Vector3 p = (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : main.transform.position) + Vector3.up * __instance.m_offset;
                p = new Vector3(Mathf.Round(p.x), Mathf.Round(p.y), Mathf.Round(p.z));   // vanilla's own rounding
                if (p == s_lastDepthPos && Time.time - s_lastDepthTime < 5f) { w_skyThrottled++; return false; }
                s_lastDepthPos = p; s_lastDepthTime = Time.time;
                return true;
            }

            private static void Postfix(DepthCamera __instance, bool __runOriginal)
            {
                if (!__runOriginal) return;
                try
                {
                    var cam = F_DepthCam(__instance);
                    RecordSkyDepth(cam, __instance.m_texture);
                    SkyCensus(cam, __instance.m_texture);
                }
                catch (System.Exception e) { EasyBakeLog.Debug("[Indirect] sky depth threw: " + e.Message); }
            }
        }
    }
}
