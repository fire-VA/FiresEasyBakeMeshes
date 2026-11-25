using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.301 [Optimize] IndirectVegetationPrepass (OFF until gate ABBA; design Tools\PLAN_ebm_vegetation_prepass_20261111.md). The swamp replay:
    // EBM/VegetationIndirect's alpha-cut leaf cards shade 10-27 layers per kept pixel (clip() in a depth-writing pass turns early-Z off).
    //  1. A camera CommandBuffer at BeforeGBuffer draws each visible card batch with the material's own ShadowCaster pass (depth only, alpha clip, the SAME
    //     vert(): wind / push / sway) into the camera depth, with unity_LightShadowBias zeroed (same clip position as the GBuffer pass) and _EbmVegPrepass = 1
    //     (the near-camera bush hide applies in that pass too).
    //  2. The batch's camera draw then uses a camera-only copy of its material with _EbmZWrite = 0 (ZTest LEqual): hidden card layers fail the depth test
    //     before the surface shader runs. Shadow casters keep the original material.
    // Cards = EBM/VegetationIndirect, _Cutoff > 0, at most IndirectVegetationPrepassMaxTris triangles in the submesh. Judged once per batch.
    internal static partial class EbmIndirect
    {
        private static readonly int IdEbmZWrite = Shader.PropertyToID("_EbmZWrite"), IdVegPrepass = Shader.PropertyToID("_EbmVegPrepass"), IdLightShadowBias = Shader.PropertyToID("unity_LightShadowBias");
        private static CommandBuffer s_vegCb; private static Camera s_vegCam;
        private static int s_vegDraws, w_vegDraws, w_vegFrames;
        private static readonly int IdEbmZTest = Shader.PropertyToID("_EbmZTest");
        private static Shader s_noClip; private static bool s_noClipLooked;
        private static bool NoClipOn => FiresEasyBakeMeshesPlugin.IndirectVegetationPrepassNoClip != null && FiresEasyBakeMeshesPlugin.IndirectVegetationPrepassNoClip.Value;
        private static Shader NoClipShader()
        {
            if (!s_noClipLooked)
            {
                s_noClipLooked = true;
                s_noClip = EbmIndirectAssets.BundleShader("EBM/VegetationIndirectNoClip");
                EasyBakeLog.InfoFirst("Indirect vegetation prepass", s_noClip != null ? "[Indirect] vegetation prepass: no-clip colour pass available (IndirectVegetationPrepassNoClip)" : "[Indirect] vegetation prepass: the bundle has no EBM/VegetationIndirectNoClip (an older bundle); the colour pass keeps the alpha cut");
            }
            return s_noClip;
        }
        private static bool VegPrepassOn => FiresEasyBakeMeshesPlugin.IndirectVegetationPrepass != null && FiresEasyBakeMeshesPlugin.IndirectVegetationPrepass.Value;

        private static bool IsPrepassCard(Batch b)
        {
            if (b.PrepassCard < 0)
            {
                bool card = false;
                try
                {
                    var m = b.Material; var mesh = b.Mesh;
                    int max = FiresEasyBakeMeshesPlugin.IndirectVegetationPrepassMaxTris != null ? FiresEasyBakeMeshesPlugin.IndirectVegetationPrepassMaxTris.Value : 64;
                    card = m != null && mesh != null && m.shader != null && m.shader.name == "EBM/VegetationIndirect" && m.HasProperty(IdEbmZWrite)
                        && m.HasProperty("_Cutoff") && m.GetFloat("_Cutoff") > 0f
                        && b.SubMesh >= 0 && b.SubMesh < mesh.subMeshCount && mesh.GetIndexCount(b.SubMesh) / 3 <= max;
                }
                catch { card = false; }
                b.PrepassCard = (sbyte)(card ? 1 : 0);
            }
            return b.PrepassCard == 1;
        }

        // the camera-only copy: the same values as the batch material every frame (Vedr / looks write the original), depth write off
        // 1.2.308 ([lead] 11-11, town gate +0.285 ms with the GPU 0.35 ms FASTER: the cost is CPU). Before, every prepassed batch copied EVERY property of
        // its material into its own camera copy EVERY frame (1.2.301-1.2.307 line: `else b.PrepassMat.CopyPropertiesFromMaterial(b.Material);`), 17
        // batches a frame in town. Now ONE camera copy per source material (batches sharing a material share it), refreshed only when it can have changed:
        //  - Vedr's VisualsChanged (season / stage / snow masks: the writers of vegetation material values) marks every copy dirty;
        //  - a 1 s backstop per copy (any other writer: lag <= 1 s; vegetation values ramp over minutes): 17 copies a SECOND in town, not 17 a frame;
        //  - the shader / NoClip choice is re-applied only when it differs.
        private sealed class CamCopy { public Material Mat; public bool Dirty = true; public float CopiedAt; public bool NoClip; }
        private static readonly Dictionary<Material, CamCopy> s_camCopies = new Dictionary<Material, CamCopy>();
        private static readonly List<CamCopy> s_camCopyList = new List<CamCopy>();
        private static bool s_camCopyHooked;
        private const float CamCopyBackstop = 1f;

        private static void CamCopiesDirty(int a, int b) { for (int i = 0; i < s_camCopyList.Count; i++) s_camCopyList[i].Dirty = true; }

        private static Material PrepassCameraMaterial(Batch b)
        {
            if (!s_camCopyHooked) { s_camCopyHooked = true; VedrLink.OnVisualsChanged(CamCopiesDirty); }
            var src = b.Material;
            if (!s_camCopies.TryGetValue(src, out var cc))
            {
                cc = new CamCopy { Mat = new Material(src) { name = src.name + " (EBM prepassed)" }, Dirty = false, CopiedAt = Time.unscaledTime };
                s_camCopies[src] = cc; s_camCopyList.Add(cc);
                cc.Mat.SetFloat(IdEbmZWrite, 0f); cc.NoClip = !NoClipOn; // forces the shader / ZTest step below once
            }
            else if (cc.Dirty || Time.unscaledTime - cc.CopiedAt > CamCopyBackstop)
            {
                // 1.2.305 NoClip: the no-clip copy of the shader, ZTest Equal against the prepass depth (same vert, same instances, same args), set below
                cc.Mat.CopyPropertiesFromMaterial(src);
                cc.Mat.SetFloat(IdEbmZWrite, 0f);
                cc.Dirty = false; cc.CopiedAt = Time.unscaledTime;
                cc.NoClip = !NoClipOn;   // re-apply the shader / ZTest below (CopyProperties does not change the shader, but _EbmZTest was overwritten)
            }
            b.PrepassMat = cc.Mat;
            bool noClipWanted = NoClipOn && NoClipShader() != null;
            if (cc.NoClip != noClipWanted)
            {
                cc.NoClip = noClipWanted;
                Shader want = noClipWanted ? NoClipShader() : src.shader;
                if (cc.Mat.shader != want) cc.Mat.shader = want;
                cc.Mat.SetFloat(IdEbmZTest, noClipWanted ? (float)CompareFunction.Equal : (float)CompareFunction.LessEqual);
            }
            return cc.Mat;
        }

        // 1.2.306 (Fire 11-11, gate-noclip2-spot4 leg 1: "the branches ... flicker a bit", then "maybe only when we first load"). Cause, from code:
        // s_vegCb is a camera command buffer that stays attached between frames and was cleared only in VegPrepassBegin, which runs only when Draw()
        // runs. Frames that return before Draw() (EbmIndirect: no main camera / nothing live yet, both normal while loading in, or an exception)
        // replayed LAST frame's depth draws: meshes and args offsets from a batch layout that may have changed, on an args buffer EnsureBuffers may
        // have released and re-created (it grows while zones load). Those draws wrote depth for cards the colour pass did not draw that frame, so
        // other foliage failed its depth test there for a frame. Now the buffer is emptied at the start of every frame; Begin records it again only
        // when this frame's draw loop runs.
        private static void VegPrepassFrameStart() { if (s_vegCb != null) s_vegCb.Clear(); }

        // 1.2.380 ([visual], [lead] hot-swap audit): [Optimize] IndirectRenderer turned off mid-run. EndFrame returns before VegPrepassFrameStart then, and
        // VegPrepassBegin (the only detach) runs from Draw, so the buffer stayed on the camera replaying its last draws against an args buffer ReleaseAll frees.
        private static void VegPrepassRelease()
        {
            if (s_vegCam != null && s_vegCb != null) { try { s_vegCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, s_vegCb); } catch { } }
            s_vegCam = null;
            if (s_vegCb != null) s_vegCb.Clear();
        }

        // called once a frame before the camera draw loop
        private static void VegPrepassBegin(Camera cam)
        {
            bool on = VegPrepassOn && cam != null;
            if (s_vegCam != null && (s_vegCam != cam || !on)) { s_vegCam.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, s_vegCb); s_vegCam = null; }
            if (!on) return;
            if (s_vegCb == null) s_vegCb = new CommandBuffer { name = "EBM vegetation depth prepass" };
            s_vegCb.Clear();
            if (s_vegCam == null) { cam.AddCommandBuffer(CameraEvent.BeforeGBuffer, s_vegCb); s_vegCam = cam; }
            s_vegCb.SetGlobalVector(IdLightShadowBias, Vector4.zero);
            s_vegCb.SetGlobalFloat(IdVegPrepass, 1f);
            s_vegDraws = 0;
        }

        // for a visible card batch about to be drawn by the camera: its depth draw into the prepass buffer, and the material its camera draw uses
        private static Material VegPrepassFor(Batch b, int i)
        {
            if (s_vegCam == null || !IsPrepassCard(b)) return null;
            // 1.2.308 (town replay: VineLeaves 22 indices x 1,199 instances lost 0.006 ms: vertex-bound, few pixels; every winner had <= 492): too many instances = skip
            var maxInst = FiresEasyBakeMeshesPlugin.IndirectVegetationPrepassMaxInstances; if (maxInst != null && maxInst.Value > 0 && b.Count > maxInst.Value) return null;
            if (b.CasterPass == -2) b.CasterPass = b.Material.FindPass("ShadowCaster");
            if (b.CasterPass < 0) return null;
            s_vegCb.DrawMeshInstancedIndirect(b.Mesh, b.SubMesh, b.Material, b.CasterPass, s_argsBuf, i * ArgWords * sizeof(uint), b.CameraProps);
            s_vegDraws++;
            return PrepassCameraMaterial(b);
        }

        private static void VegPrepassEnd()
        {
            if (s_vegCam == null) return;
            s_vegCb.SetGlobalFloat(IdVegPrepass, 0f);
            w_vegDraws += s_vegDraws; w_vegFrames++;
            // 1.2.303 ([generator]: the census line had no caller): one Info line per 30 s while the key is on, so an ON leg proves the prepass drew
            float now = Time.unscaledTime;
            if (s_vegSayNext <= 0f) s_vegSayNext = now + 30f;
            else if (now >= s_vegSayNext) { s_vegSayNext = now + 30f; string line = VegPrepassCensus(); if (line != null) EasyBakeLog.InfoFirst("Indirect vegetation prepass 30 s", line); }   // 1.2.378 ([lead]: 14 Info lines in leg 1341_2B): first Info, then Debug (repeating)
        }
        private static float s_vegSayNext;

        internal static string VegPrepassCensus()
        {
            string s = w_vegFrames > 0 ? $"[Indirect] vegetation prepass: {(double)w_vegDraws / w_vegFrames:0.0} card batch draw(s) a frame over {w_vegFrames} frame(s)" : null;
            w_vegDraws = 0; w_vegFrames = 0;
            return s;
        }
    }
}
