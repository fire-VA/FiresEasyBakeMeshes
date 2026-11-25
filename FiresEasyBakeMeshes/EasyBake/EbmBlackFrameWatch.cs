// 1.2.365 (Fire 11-14: commented out, re-enable to debug): the 1.2.359 black-world detector ([visual]). Compiled only with EBM_BLACKFRAME defined, so nothing
// of it is bound, installed or run for anyone; its [Diagnostics] BlackFrameWatch keys stay orphaned in existing cfgs (no cfg rewrite).
#if EBM_BLACKFRAME
using BepInEx.Configuration;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.359 ([visual] 11-13, [lead] order after the black-Mistlands stand: (rig path) OABRV_1_O_spot3_1.png, everything EBM draws
    // black under a bright sky, 1 launch in 7, not reproduced with RenderDoc in the process): a detector that works on a player's machine without any
    // tool. At most once every 10 s the finished frame (post effects, what the player sees) is copied into a screen-size texture at the end of the
    // frame, downscaled to 64x36 the next frame and read back asynchronously (no stall; one screen copy + one small blit per sample). Centre dark
    // while the top band is bright = the scene is black under a lit sky: one Warning per episode with EBM's state (indirect batches, skipped pieces,
    // cache summary, place, environment), Debug while it lasts, one Info when it clears. Night, caves and the loading screen never trigger it: the
    // sky band must be bright. [Diagnostics] BlackFrameWatch, default on. Thresholds are sRGB 0-255 of the readback: the black shot measured centre
    // 8.6 with its top band 145; a normal Mistlands stand 128-136 / 186; a Swamp rain stand 20.8 / 59 (dark biome, must NOT fire): defaults 15 / 110.
    internal static class EbmBlackFrameWatch
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> EverySeconds, DarkBelow, SkyAbove;
        private const int W = 64, H = 36;
        private static float s_next = -1f;
        private static RenderTexture s_full, s_small;
        private static int s_phase;          // 0 idle, 1 captured this frame (blit next frame), 2 readback pending
        private static bool s_inEpisode, s_said;
        private static int s_episodes, s_samples;
        private static float s_lastCentre = -1f, s_lastSky = -1f;

        internal static void Bind(ConfigFile cfg)
        {
            Enabled = cfg.Bind("Diagnostics", "BlackFrameWatch", true, "1.2.359: every 10 s the finished frame is sampled (a 64x36 copy, read back without a stall). If the screen centre is dark while the top band is bright, one Warning names EBM's state (indirect batches, skipped pieces, cache), so a black-world episode on any machine leaves a log line. Off = no sampling.");
            EverySeconds = cfg.Bind("Diagnostics", "BlackFrameWatchEverySeconds", 10f, new ConfigDescription("Seconds between samples.", new AcceptableValueRange<float>(2f, 120f)));
            DarkBelow = cfg.Bind("Diagnostics", "BlackFrameWatchDarkBelow", 15f, new ConfigDescription("Screen-centre mean (sRGB 0-255) below which the frame counts as dark.", new AcceptableValueRange<float>(1f, 128f)));
            SkyAbove = cfg.Bind("Diagnostics", "BlackFrameWatchSkyAbove", 110f, new ConfigDescription("Top-band mean (sRGB 0-255) above which the sky counts as bright, so night and caves never trigger.", new AcceptableValueRange<float>(10f, 250f)));
        }

        // Called from the plugin's LateUpdate before the batching gate: the detector must run while EBM is switched off mid-game too.
        internal static void Tick()
        {
            if (Enabled == null || !Enabled.Value) return;
            if (s_phase == 1) { Downscale(); return; }
            if (s_phase == 2) return;
            float now = Time.unscaledTime;
            if (s_next < 0f) { s_next = now + 15f; return; }
            if (now < s_next) return;
            s_next = now + Mathf.Max(2f, EverySeconds.Value);
            if (Player.m_localPlayer == null || (Game.instance != null && Game.instance.WaitingForRespawn()) || Player.m_localPlayer.IsTeleporting()) return;
            if (!SystemInfo.supportsAsyncGPUReadback || Screen.width < W || Screen.height < H) return;
            try
            {
                if (s_full != null && (s_full.width != Screen.width || s_full.height != Screen.height)) { s_full.Release(); Object.Destroy(s_full); s_full = null; }
                if (s_full == null) { s_full = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32) { name = "EBM BlackFrameWatch full" }; s_full.Create(); }
                if (s_small == null) { s_small = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32) { name = "EBM BlackFrameWatch 64x36", filterMode = FilterMode.Bilinear }; s_small.Create(); }
                ScreenCapture.CaptureScreenshotIntoRenderTexture(s_full);   // lands at the end of this frame
                s_phase = 1;
            }
            catch (System.Exception e) { Off("sampling failed", e); }
        }

        private static void Downscale()
        {
            try
            {
                Graphics.Blit(s_full, s_small);
                s_phase = 2;
                AsyncGPUReadback.Request(s_small, 0, TextureFormat.RGBA32, OnRead);
            }
            catch (System.Exception e) { s_phase = 0; Off("readback failed", e); }
        }

        private static void Off(string what, System.Exception e)
        {
            if (!s_said) { s_said = true; EasyBakeLog.Warn("[BlackFrame] " + what + ", watch off for this session: " + e.GetType().Name + ": " + e.Message); }
            if (Enabled != null) Enabled.Value = false;
        }

        private static void OnRead(AsyncGPUReadbackRequest req)
        {
            s_phase = 0;
            if (req.hasError) return;
            NativeArray<Color32> px = req.GetData<Color32>();
            if (px.Length < W * H) return;
            // a readback's rows run bottom-up: the sky band is the top 6 rows (y >= H - 6); the centre is the middle 18 rows x 32 columns (the rig shot's crop)
            double sky = 0, centre = 0; int ns = 0, nc = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    Color32 c = px[y * W + x]; float l = (c.r + c.g + c.b) / 3f;
                    if (y >= H - 6) { sky += l; ns++; }
                    else if (y >= 9 && y < 27 && x >= 16 && x < 48) { centre += l; nc++; }
                }
            if (ns == 0 || nc == 0) return;
            s_lastSky = (float)(sky / ns); s_lastCentre = (float)(centre / nc); s_samples++;
            bool dark = s_lastCentre < DarkBelow.Value && s_lastSky > SkyAbove.Value;
            if (dark)
            {
                if (!s_inEpisode)
                {
                    s_inEpisode = true; s_episodes++;
                    EasyBakeLog.Warn($"[BlackFrame] screen centre mean {s_lastCentre:0.0} while the top band reads {s_lastSky:0.0} (sRGB 0-255): the scene is dark under a bright sky (episode {s_episodes}). {State()}");
                }
                else EasyBakeLog.Debug($"[BlackFrame] still dark: centre {s_lastCentre:0.0}, sky {s_lastSky:0.0}");
            }
            else if (s_inEpisode)
            {
                s_inEpisode = false;
                EasyBakeLog.Info($"[BlackFrame] cleared: centre {s_lastCentre:0.0}, sky {s_lastSky:0.0}. {State()}");
            }
        }

        private static string State()
        {
            int batches = 0; try { Indirect.EbmIndirect.ForEachBatch(b => batches++); } catch { }
            int zones = 0, skipped = 0; try { skipped = ZoneTracker.SkippedTotal(out zones); } catch { }
            string cache = null; try { cache = MeshCacheStore.CacheSummary(); } catch { }
            var p = Player.m_localPlayer; Vector3 at = p != null ? p.transform.position : Vector3.zero;
            string biome = "?"; try { biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(at).ToString() : "?"; } catch { }
            string env = "?"; try { var es = EnvMan.instance != null ? EnvMan.instance.GetCurrentEnvironment() : null; env = es != null ? es.m_name : "?"; } catch { }
            return $"EBM state: indirect batches {batches}, skipped pieces {skipped} in {zones} zone(s), batching {(FiresEasyBakeMeshesPlugin.BatchingActive() ? "active" : "off")}, at ({at.x:0},{at.y:0},{at.z:0}) {biome}, env {env}, cache {(cache ?? "n/a")}, samples {s_samples}";
        }

        internal static string Status() => Enabled != null && Enabled.Value ? $"black-frame watch: last centre {s_lastCentre:0.0} / sky {s_lastSky:0.0}, {s_samples} sample(s), {s_episodes} episode(s)" : "black-frame watch off";
    }
}
#endif   // EBM_BLACKFRAME (1.2.365, Fire 11-14)
