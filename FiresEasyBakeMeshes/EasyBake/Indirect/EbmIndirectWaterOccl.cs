using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.305 A2 [Optimize] WaterHideOccludedTiles (OFF until its gate; Tools\PLAN_ebm_water_tile_cull_20261111.md section A2; lead-approved (b) 11-11).
    // Zone water tiles that stand wholly behind what the camera already drew (a hill, a building) are not drawn.
    //  - Test: each tile's WAVE SLAB (WaterTightBounds.SlabWorld, the same slab A3 uses) against E2's depth pyramid with the SAME HiZTest
    //    (EbmHiZ.compute HiZTestBoxes), recorded in E2's command buffer right after the pyramid is built: THIS frame's depth and pose. The water is
    //    forward / transparent, so the pyramid holds only opaques: exactly what can hide a tile. The lake bed under the water is FARTHER than the
    //    slab's top, so visible water is never judged hidden by its own bed.
    //  - The pyramid is built for A2 even while E2's instance cull (IndirectDepthCull) is OFF (approved (b): ~0.07 ms build cost at spot4 vs an
    //    upper bound of ~0.18 ms live gain). E2's motion skip, orientation self-test and near band apply unchanged.
    //  - Readback: CommandBuffer.RequestAsyncReadback of <= 1 KB, 1-3 frames late; each read carries the frame + camera pose it was made from.
    //  - Apply (the pop risk): hidden (renderer.forceRenderingOff, renderer only, prior value kept, as A1) only after 3 CONSECUTIVE occluded reads,
    //    each made within the motion guard of the current camera; shown at once on the first visible read, on a camera move / turn past the guard
    //    since the read, when reads go stale (no pyramid: moving, paused, key off), or on key off. A1-buried tiles are skipped (A1 owns them).
    //  - Verify (WaterHideOccludedTilesVerify): hides nothing; counts would-hides and WOULD-POPS (a tile the 3-read rule would have hidden that reads
    //    visible within its next 3 reads = the latency risk, measured).
    // Gate (lead): E2's breakdown legs must read "frames in a row 3+" false culls = 0 at town AND spot4 before this key goes ON anywhere.
    internal static partial class EbmIndirect
    {
        internal static ConfigEntry<bool> WaterOcclOn, WaterOcclVerifyOn;
        internal static void BindWaterOccl(ConfigFile config)
        {
            WaterOcclOn = config.Bind("Optimize", "WaterHideOccludedTiles", false /* OFF until gate: E2 breakdown 3+ streaks = 0 at town + spot4, then ABBA */,
                "Water tiles that are completely hidden behind hills or buildings (from where you stand) are not drawn. They come back the moment they could be seen. Swimming, boats and everything else about the water are unchanged. Off = as before.");
            WaterOcclVerifyOn = config.Bind("Optimize", "WaterHideOccludedTilesVerify", false,
                "Test twin of WaterHideOccludedTiles: nothing is hidden; the log counts the tiles it would hide and how many of those would have popped back within 3 reads (WOULD-POPS should be 0). Off = no test.");
        }

        private static bool WaterOcclWanted => Active && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null
            && ((WaterOcclOn != null && WaterOcclOn.Value) || (WaterOcclVerifyOn != null && WaterOcclVerifyOn.Value));

        private sealed class WoTile
        {
            public WaterVolume Volume; public MeshRenderer Renderer; public Bounds Slab; public float SlabAt = -1f;
            public int Occluded, Seen; public bool Hidden, PriorForceOff, WouldHide; public int WouldHideReads;
        }
        private static readonly Dictionary<WaterVolume, WoTile> s_woTiles = new Dictionary<WaterVolume, WoTile>();
        private static readonly List<WoTile> s_woOrder = new List<WoTile>();
        private static readonly List<WaterVolume> s_woGone = new List<WaterVolume>();
        private const int WoMax = 256, WoHideAfter = 3, WoStaleFrames = 6;
        private static int s_kBoxes = -2;
        private static GraphicsBuffer s_woBoxes, s_woOut;
        private static readonly Vector4[] s_woBoxData = new Vector4[WoMax * 2];
        private static readonly int IdHiZBoxes = Shader.PropertyToID("_HiZBoxes"), IdHiZBoxOut = Shader.PropertyToID("_HiZBoxOut"), IdHiZBoxCount = Shader.PropertyToID("_HiZBoxCount");
        // what each in-flight read was made from: the tile order snapshot, frame and pose (up to 4 reads in flight)
        private sealed class WoRead { public WoTile[] Tiles = new WoTile[WoMax]; public int Count, Frame; public Vector3 Pos; public Quaternion Rot; public bool Busy; }
        private static readonly WoRead[] s_woReads = { new WoRead(), new WoRead(), new WoRead(), new WoRead() };
        private static int s_woLastRead = -100, s_woStamp; private static float s_woNextScan; private static bool s_woWas, s_woNoKernelSaid; private static string s_woState = "";
        private static int w_woReads, w_woHides, w_woShows, w_woWouldHides, w_woWouldPops, w_woGuardShows, w_woStaleShows; private static float s_woNextCensus;

        // from RecordPyramid: append the box test to E2's command buffer (this frame's pyramid + pose)
        private static void WaterOcclRecord(CommandBuffer cb, Matrix4x4 vp, Camera cam)
        {
            if (!WaterOcclWanted || !s_orientOk || s_woOrder.Count == 0) return;
            if (s_kBoxes == -2) s_kBoxes = s_hiz != null && s_hiz.HasKernel("HiZTestBoxes") ? s_hiz.FindKernel("HiZTestBoxes") : -1;
            if (s_kBoxes < 0) { if (!s_woNoKernelSaid) { s_woNoKernelSaid = true; EasyBakeLog.Info("[WaterOccl] off: EBM's bundle has no HiZTestBoxes kernel (an older bundle)"); } return; }
            WoRead slot = null;
            for (int i = 0; i < s_woReads.Length; i++) if (!s_woReads[i].Busy) { slot = s_woReads[i]; break; }
            if (slot == null) return;   // 4 reads in flight: skip this frame
            int n = 0;
            for (int i = 0; i < s_woOrder.Count && n < WoMax; i++)
            {
                var t = s_woOrder[i];
                if (t.Renderer == null || WaterHideBuried.IsHidden(t.Volume)) continue;   // A1 owns buried tiles
                s_woBoxData[n * 2] = t.Slab.center; s_woBoxData[n * 2 + 1] = t.Slab.extents;
                slot.Tiles[n++] = t;
            }
            if (n == 0) return;
            if (s_woBoxes == null) s_woBoxes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WoMax * 2, 16);
            if (s_woOut == null) s_woOut = new GraphicsBuffer(GraphicsBuffer.Target.Structured, WoMax, sizeof(uint));
            cb.SetBufferData(s_woBoxes, s_woBoxData, 0, 0, n * 2);
            cb.SetComputeTextureParam(s_hiz, s_kBoxes, IdHiZ, s_pyramid);
            cb.SetComputeMatrixParam(s_hiz, IdHiZVP, vp);
            cb.SetComputeVectorParam(s_hiz, IdHiZSize, new Vector4(s_pyrW, s_pyrH, s_pyrLevels, 1f));
            cb.SetComputeVectorParam(s_hiz, IdHiZOpts, new Vector4(s_flipV ? 1f : 0f, DepthBias, 0f, 0f));
            var cp = cam.transform.position;
            cb.SetComputeVectorParam(s_hiz, IdHiZCam, new Vector4(cp.x, cp.y, cp.z, DepthCullNearM != null ? DepthCullNearM.Value : 8f));
            cb.SetComputeBufferParam(s_hiz, s_kBoxes, IdHiZBoxes, s_woBoxes);
            cb.SetComputeBufferParam(s_hiz, s_kBoxes, IdHiZBoxOut, s_woOut);
            cb.SetComputeIntParam(s_hiz, IdHiZBoxCount, n);
            cb.DispatchCompute(s_hiz, s_kBoxes, (n + 63) / 64, 1, 1);
            slot.Count = n; slot.Frame = Time.frameCount; slot.Pos = cp; slot.Rot = cam.transform.rotation; slot.Busy = true;
            var captured = slot;
            cb.RequestAsyncReadback(s_woOut, n * sizeof(uint), 0, r => WaterOcclApply(captured, r));
        }

        private static void WaterOcclApply(WoRead slot, AsyncGPUReadbackRequest r)
        {
            slot.Busy = false;
            if (r.hasError || !WaterOcclWanted) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            bool on = WaterOcclOn != null && WaterOcclOn.Value, verify = WaterOcclVerifyOn != null && WaterOcclVerifyOn.Value, hideReal = on && !verify;
            // the read's pose vs now: past the guard = this read says nothing about now (and every hidden tile is shown)
            bool poseOk = (cam.transform.position - slot.Pos).sqrMagnitude <= MoveGuardM * MoveGuardM && Quaternion.Angle(cam.transform.rotation, slot.Rot) <= TurnGuardDeg;
            var d = r.GetData<uint>();
            s_woLastRead = Time.frameCount; w_woReads++;
            for (int k = 0; k < slot.Count && k < d.Length; k++)
            {
                var t = slot.Tiles[k]; slot.Tiles[k] = null;
                if (t == null || t.Renderer == null) continue;
                bool occl = d[k] != 0u && poseOk;
                if (occl) t.Occluded++; else t.Occluded = 0;
                // Verify: would-hide at 3 in a row; a would-hidden tile reading visible within its next 3 reads = a would-pop
                if (t.WouldHide)
                {
                    t.WouldHideReads++;
                    if (!occl) { if (t.WouldHideReads <= WoHideAfter) w_woWouldPops++; t.WouldHide = false; t.WouldHideReads = 0; }
                }
                else if (t.Occluded >= WoHideAfter) { t.WouldHide = true; t.WouldHideReads = 0; w_woWouldHides++; }
                if (hideReal && t.Occluded >= WoHideAfter && !t.Hidden) { t.PriorForceOff = t.Renderer.forceRenderingOff; t.Renderer.forceRenderingOff = true; t.Hidden = true; w_woHides++; }
                else if (t.Hidden && (!occl || !hideReal)) { WoShow(t); if (!poseOk) w_woGuardShows++; }
            }
        }

        private static void WoShow(WoTile t) { if (t.Hidden && t.Renderer != null) t.Renderer.forceRenderingOff = t.PriorForceOff; t.Hidden = false; w_woShows++; }

        /// <summary>From the plugin's slow tick (every frame, cheap): tile list upkeep, staleness and the census.</summary>
        internal static void WaterOcclTick()
        {
            bool wanted = WaterOcclWanted;
            if (!wanted)
            {
                if (s_woWas) { for (int i = 0; i < s_woOrder.Count; i++) if (s_woOrder[i].Hidden) WoShow(s_woOrder[i]); s_woOrder.Clear(); s_woTiles.Clear(); }
                s_woWas = false;
                WoSay(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null ? "off (no graphics: dedicated server)" : "off (WaterHideOccludedTiles and its Verify are off)");
                return;
            }
            s_woWas = true;
            bool verify = WaterOcclVerifyOn != null && WaterOcclVerifyOn.Value;
            WoSay(!s_orientOk ? "waiting for the depth pyramid's orientation self-test (E2)" : verify ? "VERIFY (nothing hidden; would-hides and would-pops counted)" : "on (water tiles behind hills / buildings are not drawn)");
            float now = Time.unscaledTime;
            if (now >= s_woNextScan) { s_woNextScan = now + 0.5f; WoScan(now); }
            // no fresh read (no pyramid: moving, paused, orientation unknown): show everything at once
            if (Time.frameCount - s_woLastRead > WoStaleFrames)
                for (int i = 0; i < s_woOrder.Count; i++) { var t = s_woOrder[i]; t.Occluded = 0; if (t.Hidden) { WoShow(t); w_woStaleShows++; } }
            if (now >= s_woNextCensus) WoCensus(verify);
        }

        private static void WoScan(float now)
        {
            var list = WaterVolume.Instances; int stamp = ++s_woStamp;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (v == null) continue;
                    if (!s_woTiles.TryGetValue(v, out var t))
                    {
                        if (!WaterHideBuried.IsZoneTile(v)) { s_woTiles[v] = null; continue; }
                        t = new WoTile { Volume = v, Renderer = v.m_waterSurface };
                        s_woTiles[v] = t; s_woOrder.Add(t);
                    }
                    if (t == null) continue;
                    t.Seen = stamp;
                    if (t.Renderer != null) t.Slab = WaterTightBounds.SlabWorld(v, t.Renderer);   // the depth (and so the slab) follows the heightmap
                }
            s_woGone.Clear();
            foreach (var kv in s_woTiles) if (kv.Key == null || (kv.Value != null && (kv.Value.Renderer == null || kv.Value.Seen != stamp))) s_woGone.Add(kv.Key);
            for (int i = 0; i < s_woGone.Count; i++)
            {
                if (s_woTiles.TryGetValue(s_woGone[i], out var t) && t != null) { if (t.Hidden) WoShow(t); s_woOrder.Remove(t); }
                s_woTiles.Remove(s_woGone[i]);
            }
        }

        private static void WoSay(string state) { if (state == s_woState) return; s_woState = state; EasyBakeLog.Info("[WaterOccl] " + state); }

        private static void WoCensus(bool verify)
        {
            s_woNextCensus = Time.unscaledTime + 30f;
            if (verify || EasyBakeLog.DebugOn())
            {
                int hidden = 0, would = 0;
                for (int i = 0; i < s_woOrder.Count; i++) { if (s_woOrder[i].Hidden) hidden++; if (s_woOrder[i].WouldHide) would++; }
                EasyBakeLog.Say(verify, $"[WaterOccl] 30 s: {s_woOrder.Count} zone water tile(s), {hidden} hidden now, {would} would-hidden now; reads {w_woReads}; hides {w_woHides}, shows {w_woShows} (pose guard {w_woGuardShows}, stale {w_woStaleShows})"
                    + (verify ? $"; VERIFY: would-hides {w_woWouldHides}, WOULD-POPS {w_woWouldPops}" : ""));
            }
            w_woReads = w_woHides = w_woShows = w_woWouldHides = w_woWouldPops = w_woGuardShows = w_woStaleShows = 0;
        }
    }
}
