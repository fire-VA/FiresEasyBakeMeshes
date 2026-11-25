using System;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // EBM 1.2.428 ([visual], lead approved; Fire 11-16: "we were meant to remove fts"; Tools\FTS_REMOVAL_AUDIT_20261116.md §1): the globals EBM's own shaders read that only
    // FiresTossinShade wrote, now EBM's own. Our code, after FTS's (our mod): FiresShoreField.cs, FiresCausticsPass.cs (ComputeLightDrive), FiresPieceStamp.cs,
    // FiresHeightmapShaderLoader.cs (PushSeason). While FTS is loaded it keeps writing them and EBM writes none of them (never two writers); without FTS EBM writes them.
    //  - Shore field ([Water] Shore Field, ON): _FiresShoreField RGBA (R wave strength welded to vanilla = depth / 10 m, G depth / 8 m, B water-ness over 96 m from the shore),
    //    96 x 96 over +-1024 m around the player, the centre snapped to the texel grid, baked at most 0.5 ms a frame; depth from the shaped sea (VedrApi.TrySurfaceOffsetAt when
    //    Vedr has it, else the flat level + _FiresTideOffset). CPU twin: EbmApi.TrySampleShoreWave (Vedr's wave physics reads it).
    //  - Caustic day mask: _FiresCausticDayMask = sun elevation ramp x sun luminance (FTS's exact drive); the water surface caustics multiply it.
    //  - Terrain season globals: _FiresSeasonGold 0, _FiresSeasonFresh 0 (as FTS since 0.3.317), _FiresMudGate 1 (mud allowed; FTS's winter gate needs Core's snow field and
    //    stays 1 without it), _FiresMudTexOn 0 (no mud texture set in EBM's bundle: plain mud colours).
    //  - Footprint stamp ([Water] Footprint Trails, ON): _FiresPieceStamp / _FiresPieceStampRect where the local player walks; stands down while Vedr's piece snow is active.
    internal static class EbmFtsGlobals
    {
        internal static ConfigEntry<bool> ShoreOn, StampOn;
        internal static void Bind(ConfigFile c)
        {
            ShoreOn = c.Bind("Water", "Shore Field", true, "The water knows where the shore is: waves ease off in shallow water (as the game's own swim surface does), shallow water takes its shallow colour, and the sea fog stays over the sea. Without it the waves stay full right up to the beach. EBM's own copy of what FiresTossinShade did; while FiresTossinShade is installed it keeps doing it.");
            StampOn = c.Bind("Water", "Footprint Trails", true, "Your footsteps leave a trail EBM's ground and pieces can show (snow knocked off, worn paths). Stands aside while Vedr's own piece snow trails are active.");
        }

        private static bool? s_fts;
        private static bool FtsLoaded { get { if (s_fts == null) s_fts = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Fire.FiresTossinShade") || Type.GetType("VerdantsAscent.Water.Core.FiresShoreField, FiresTossinShade") != null; return s_fts.Value; } }
        private static bool s_said;

        internal static void Tick()
        {
            if (FtsLoaded) { if (!s_said) { s_said = true; EasyBakeLog.Info("[FtsGlobals] FiresTossinShade is installed: it keeps writing the shore field, caustic day mask, terrain season globals and footprint stamp; EBM writes none of them"); } return; }
            if (!s_said) { s_said = true; EasyBakeLog.Info("[FtsGlobals] FiresTossinShade is not installed: EBM writes the shore field, caustic day mask, terrain season globals and footprint stamp its shaders read"); }
            try { DayMask(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] day mask: " + e.Message); }
            try { Season(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] season: " + e.Message); }
            try { Shore.Tick(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] shore field: " + e.Message); }
            try { Stamp.Tick(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] stamp: " + e.Message); }
        }

        // ---- caustic day mask (FTS FiresCausticsPass.ComputeLightDrive, MoonScale 1)
        private static readonly int P_Day = Shader.PropertyToID("_FiresCausticDayMask"), P_Sun = Shader.PropertyToID("_SunColor");
        private static void DayMask()
        {
            var em = EnvMan.instance;
            float drive = 1f;
            if (em != null)
            {
                float elev = Mathf.Clamp01(-em.GetSunDirection().y);
                float elevRamp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.06f, 0.35f, elev));
                Color sun = Shader.GetGlobalColor(P_Sun);
                drive = elevRamp * Mathf.Clamp01((sun.r * 0.299f + sun.g * 0.587f + sun.b * 0.114f) / 0.8f);
            }
            Shader.SetGlobalFloat(P_Day, drive);
        }

        // ---- terrain season globals (FTS PushSeason without Core's winter gate)
        private static bool s_seasonPushed;
        private static void Season()
        {
            if (s_seasonPushed) return;
            s_seasonPushed = true;
            Shader.SetGlobalFloat("_FiresSeasonGold", 0f);
            Shader.SetGlobalFloat("_FiresSeasonFresh", 0f);
            Shader.SetGlobalFloat("_FiresMudGate", 1f);
            Shader.SetGlobalFloat("_FiresMudTexOn", 0f);
        }

        // ================================================================== shore field (FTS FiresShoreField, our port)
        internal static class Shore
        {
            private const int N = 96; private const float Extent = 1024f, WaveFullDepth = 10f, DepthForDark = 8f, FogBlend = 96f, BudgetMs = 0.5f, TideRebake = 0.25f, TideMinSec = 30f;
            private static readonly int P_Tex = Shader.PropertyToID("_FiresShoreField"), P_Center = Shader.PropertyToID("_FiresShoreFieldCenter"), P_Ext = Shader.PropertyToID("_FiresShoreFieldExtent"),
                P_On = Shader.PropertyToID("_FiresShoreFieldOn"), P_Tide = Shader.PropertyToID("_FiresTideOffset"), P_FieldOn = Shader.PropertyToID("_FiresTideRiverFieldOn");
            private static Texture2D s_tex; private static Color[] s_px; private static float[] s_depth, s_dist; private static bool[] s_water;
            private static bool s_baking, s_hasLive; private static Vector2 s_bakeC, s_liveC; private static int s_row, s_x; private static float s_bakeTide, s_liveTide, s_lastTideBake = -999f;
            private static readonly System.Diagnostics.Stopwatch s_sw = new System.Diagnostics.Stopwatch();
            private static int s_bakes;
            // CPU twin, swapped in with the texture
            private static float[] s_liveWave; private static Vector2 s_twinC; private static bool s_liveOn;

            internal static void Tick()
            {
                var player = Player.m_localPlayer;
                if (ShoreOn == null || !ShoreOn.Value || WorldGenerator.instance == null || player == null)
                {
                    if (s_liveOn) { Shader.SetGlobalFloat(P_On, 0f); s_liveOn = false; }
                    return;
                }
                Vector3 pp = player.transform.position; var p = new Vector2(pp.x, pp.z);
                float tideNow = Shader.GetGlobalFloat(P_Tide);
                bool tideMoved = s_hasLive && Mathf.Abs(tideNow - s_liveTide) > TideRebake && Time.time - s_lastTideBake > TideMinSec;
                float reTrig = Extent * 0.35f;
                if (!s_baking && (!s_hasLive || tideMoved || (p - s_liveC).sqrMagnitude > reTrig * reTrig))
                {
                    if (tideMoved) s_lastTideBake = Time.time;
                    float texelM = 2f * Extent / N;
                    Begin(new Vector2(Mathf.Round(p.x / texelM) * texelM, Mathf.Round(p.y / texelM) * texelM), tideNow);
                }
                if (s_baking) Step();
                if (s_hasLive)
                {
                    Shader.SetGlobalTexture(P_Tex, s_tex);
                    Shader.SetGlobalVector(P_Center, new Vector4(s_liveC.x, 0f, s_liveC.y, 0f));
                    Shader.SetGlobalFloat(P_Ext, Extent);
                    Shader.SetGlobalFloat(P_On, 1f);
                    Shader.SetGlobalFloat(P_FieldOn, 0f);   // no per-texel tide/river shaping field without its source (the drawn tide is uniform, as the swim surface without one)
                    s_liveOn = true;
                }
            }

            private static void Begin(Vector2 c, float tide)
            {
                if (s_tex == null)
                {
                    s_tex = new Texture2D(N, N, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "EbmShoreField" };
                    s_px = new Color[N * N]; s_depth = new float[N * N]; s_dist = new float[N * N]; s_water = new bool[N * N];
                }
                s_baking = true; s_bakeC = c; s_row = 0; s_x = 0; s_bakeTide = tide;
            }

            private static void Step()
            {
                float texelM = 2f * Extent / N, origin = -Extent + texelM * 0.5f;
                var wg = WorldGenerator.instance;
                s_sw.Restart(); bool first = true;
                while (s_row < N && (first || s_sw.Elapsed.TotalMilliseconds < BudgetMs))
                {
                    first = false;
                    int z = s_row, x = s_x;
                    if (++s_x >= N) { s_x = 0; s_row++; }
                    float wx = s_bakeC.x + origin + x * texelM, wz = s_bakeC.y + origin + z * texelM;
                    float gh = wg.GetHeight(wx, wz);
                    float sea = ZoneSystem.c_WaterLevel + (VedrOffset(wx, wz, out float off) ? off : s_bakeTide);
                    float d = Mathf.Max(0f, sea - gh);
                    int i = z * N + x; s_depth[i] = d; s_water[i] = d > 0.05f;
                }
                if (s_row < N) return;
                Complete(texelM);
            }

            private static void Complete(float texelM)
            {
                // chamfer distance from each water texel to the nearest land (FTS ComputeDistanceToShore)
                const float BIG = 1e9f; float d1 = texelM, d2 = texelM * 1.4142136f;
                for (int i = 0; i < N * N; i++) s_dist[i] = s_water[i] ? BIG : 0f;
                for (int z = 0; z < N; z++) for (int x = 0; x < N; x++)
                {
                    int i = z * N + x; float v = s_dist[i];
                    if (x > 0) v = Mathf.Min(v, s_dist[i - 1] + d1); if (z > 0) v = Mathf.Min(v, s_dist[i - N] + d1);
                    if (x > 0 && z > 0) v = Mathf.Min(v, s_dist[i - N - 1] + d2); if (x < N - 1 && z > 0) v = Mathf.Min(v, s_dist[i - N + 1] + d2);
                    s_dist[i] = v;
                }
                for (int z = N - 1; z >= 0; z--) for (int x = N - 1; x >= 0; x--)
                {
                    int i = z * N + x; float v = s_dist[i];
                    if (x < N - 1) v = Mathf.Min(v, s_dist[i + 1] + d1); if (z < N - 1) v = Mathf.Min(v, s_dist[i + N] + d1);
                    if (x < N - 1 && z < N - 1) v = Mathf.Min(v, s_dist[i + N + 1] + d2); if (x > 0 && z < N - 1) v = Mathf.Min(v, s_dist[i + N - 1] + d2);
                    s_dist[i] = v;
                }
                var wave = new float[N * N];
                for (int i = 0; i < N * N; i++)
                {
                    float w = s_water[i] ? Mathf.Clamp01(s_depth[i] / WaveFullDepth) : 0f;
                    s_px[i] = new Color(w, Mathf.Clamp01(s_depth[i] / DepthForDark), s_water[i] ? Mathf.Clamp01(s_dist[i] / FogBlend) : 0f, 1f);
                    wave[i] = Mathf.Round(w * 255f) / 255f;   // as RGBA32 stores it (the GPU reads the 8-bit value)
                }
                s_tex.SetPixels(s_px); s_tex.Apply(false, false);
                s_liveWave = wave; s_twinC = s_bakeC;
                s_liveC = s_bakeC; s_liveTide = s_bakeTide; s_hasLive = true; s_baking = false; s_bakes++;
                if (s_bakes == 1) EasyBakeLog.Info($"[FtsGlobals] shore field baked ({N}x{N} over {2 * Extent:0} m around ({s_liveC.x:0},{s_liveC.y:0}), depth from {(s_vedrGet != null ? "Vedr's shaped sea" : "the flat sea level + tide")}); EBM's water and Vedr's wave physics read it");
            }

            // the shader's SampleShoreField().r on the CPU: same texture values, rect, bilinear over texel centres, 5 % edge fade to 1
            internal static bool TrySampleWave(float x, float z, out float wave)
            {
                wave = 1f;
                var r = s_liveWave;
                if (!s_liveOn || r == null) return false;
                float size = 2f * Extent, u = (x - s_twinC.x) / size + 0.5f, v = (z - s_twinC.y) / size + 0.5f;
                if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
                float inside = Mathf.Clamp01((0.5f - Mathf.Abs(u - 0.5f)) / 0.05f) * Mathf.Clamp01((0.5f - Mathf.Abs(v - 0.5f)) / 0.05f);
                float fx = u * N - 0.5f, fz = v * N - 0.5f; int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz); float tx = fx - x0, tz = fz - z0;
                int xa = Mathf.Clamp(x0, 0, N - 1), xb = Mathf.Clamp(x0 + 1, 0, N - 1), za = Mathf.Clamp(z0, 0, N - 1), zb = Mathf.Clamp(z0 + 1, 0, N - 1);
                float s = Mathf.Lerp(Mathf.Lerp(r[za * N + xa], r[za * N + xb], tx), Mathf.Lerp(r[zb * N + xa], r[zb * N + xb], tx), tz);
                wave = Mathf.Lerp(1f, s, inside);
                return true;
            }

            // Vedr's shaped sea, by name (VedrApi.TrySurfaceOffsetAt(float, float, out float)), when it exists; null = flat level + tide
            private static bool s_vedrLooked; private static MethodInfo s_vedrGet; private static readonly object[] s_args = new object[3];
            private static bool VedrOffset(float x, float z, out float off)
            {
                off = 0f;
                if (!s_vedrLooked)
                {
                    s_vedrLooked = true;
                    var t = Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    s_vedrGet = t?.GetMethod("TrySurfaceOffsetAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float), typeof(float).MakeByRefType() }, null);
                }
                if (s_vedrGet == null) return false;
                try { s_args[0] = x; s_args[1] = z; s_args[2] = 0f; bool ok = (bool)s_vedrGet.Invoke(null, s_args); off = (float)s_args[2]; return ok; }
                catch { s_vedrGet = null; return false; }
            }
        }

        // ================================================================== footprint stamp (FTS FiresPieceStamp, our port)
        internal static class Stamp
        {
            private const int Res = 256; private const float Size = 64f, Cell = Size / Res, Radius = 0.9f, StepM = 0.4f, UploadS = 0.5f, RefillS = 600f, FadeS = 10f, RecentreM = 16f;
            private static readonly int P_Tex = Shader.PropertyToID("_FiresPieceStamp"), P_Rect = Shader.PropertyToID("_FiresPieceStampRect");
            private static Texture2D s_tex; private static Color[] s_px, s_spare; private static Vector2 s_origin; private static Vector3 s_last;
            private static bool s_dirty, s_any, s_down; private static float s_nextUpload, s_nextFade; private static int s_mask;

            internal static void Tick()
            {
                var player = Player.m_localPlayer;
                if (player == null || StampOn == null || !StampOn.Value) return;
                if (VedrPieceSnow())
                {
                    if (!s_down) { s_down = true; Shader.SetGlobalVector(P_Rect, Vector4.zero); }
                    return;
                }
                if (s_down) { s_down = false; if (s_tex != null) Shader.SetGlobalVector(P_Rect, new Vector4(s_origin.x, s_origin.y, Size, 1f)); }
                if (s_tex == null)
                {
                    s_tex = new Texture2D(Res, Res, TextureFormat.RGFloat, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "EbmFootprintStamp" };
                    s_px = new Color[Res * Res]; s_mask = LayerMask.GetMask("piece", "piece_nonsolid");
                    Recentre(player.transform.position);
                    Shader.SetGlobalTexture(P_Tex, s_tex);
                }
                Vector3 at = player.transform.position;
                float cx = s_origin.x + Size * 0.5f, cz = s_origin.y + Size * 0.5f;
                if (Mathf.Abs(at.x - cx) > RecentreM || Mathf.Abs(at.z - cz) > RecentreM) Recentre(at);
                if ((at - s_last).sqrMagnitude >= StepM * StepM)
                {
                    s_last = at;
                    if (player.IsOnGround() || Physics.CheckSphere(at, 0.3f, s_mask, QueryTriggerInteraction.Ignore)) Put(at);
                }
                if (s_any && Time.time >= s_nextFade) Fade();
                if (s_dirty && Time.time >= s_nextUpload) { s_nextUpload = Time.time + UploadS; s_dirty = false; s_tex.SetPixels(s_px); s_tex.Apply(false); }
            }

            private static void Recentre(Vector3 at)
            {
                var o = new Vector2(Mathf.Round((at.x - Size * 0.5f) / Cell) * Cell, Mathf.Round((at.z - Size * 0.5f) / Cell) * Cell);
                int dx = Mathf.RoundToInt((o.x - s_origin.x) / Cell), dz = Mathf.RoundToInt((o.y - s_origin.y) / Cell);
                if (!s_any) { s_origin = o; Shader.SetGlobalVector(P_Rect, new Vector4(o.x, o.y, Size, 1f)); return; }
                if (s_spare == null) s_spare = new Color[s_px.Length];
                var next = s_spare; Array.Clear(next, 0, next.Length);
                for (int z = 0; z < Res; z++) for (int x = 0; x < Res; x++) { int sx = x + dx, sz = z + dz; if (sx >= 0 && sz >= 0 && sx < Res && sz < Res) next[z * Res + x] = s_px[sz * Res + sx]; }
                s_spare = s_px; s_px = next; s_origin = o; s_dirty = false;
                s_tex.SetPixels(s_px); s_tex.Apply(false); s_nextUpload = Time.time + UploadS;
                Shader.SetGlobalVector(P_Rect, new Vector4(o.x, o.y, Size, 1f));
            }

            private static void Put(Vector3 at)
            {
                int r = Mathf.CeilToInt(Radius / Cell), px = Mathf.FloorToInt((at.x - s_origin.x) / Cell), pz = Mathf.FloorToInt((at.z - s_origin.y) / Cell);
                for (int z = Mathf.Max(0, pz - r); z <= Mathf.Min(Res - 1, pz + r); z++)
                    for (int x = Mathf.Max(0, px - r); x <= Mathf.Min(Res - 1, px + r); x++)
                    {
                        float dx = (x + 0.5f) * Cell + s_origin.x - at.x, dz = (z + 0.5f) * Cell + s_origin.y - at.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > Radius) continue;
                        float t = Mathf.Clamp01((Radius - d) / (Radius * 0.75f)), s = t * t * (3f - 2f * t);
                        int i = z * Res + x; float g = Mathf.Max(s_px[i].g, s);
                        s_px[i] = new Color((at.y + 1f) * g, g, 0f, 1f);
                    }
                s_any = true; s_dirty = true;
            }

            private static void Fade()
            {
                s_nextFade = Time.time + FadeS;
                float step = FadeS / RefillS; bool any = false;
                for (int i = 0; i < s_px.Length; i++)
                {
                    Color c = s_px[i]; if (c.g <= 0f) continue;
                    float h = c.r / c.g; c.g -= step;
                    if (c.g <= 0f) c = default; else { c.r = h * c.g; any = true; }
                    s_px[i] = c;
                }
                s_any = any; s_dirty = true;
            }

            // Vedr's piece snow draws its own trails (VedrApi.PieceShaderActive, by name); re-read every 2 s
            private static float s_vedrAt = -1f; private static bool s_vedrOn; private static PropertyInfo s_vedrProp; private static bool s_vedrLooked;
            private static bool VedrPieceSnow()
            {
                if (Time.unscaledTime < s_vedrAt) return s_vedrOn;
                s_vedrAt = Time.unscaledTime + 2f;
                if (!s_vedrLooked) { s_vedrLooked = true; s_vedrProp = Type.GetType("Vedr.VedrApi, Norger.Vedr")?.GetProperty("PieceShaderActive", BindingFlags.Public | BindingFlags.Static); }
                try { s_vedrOn = s_vedrProp != null && (bool)s_vedrProp.GetValue(null, null); } catch { s_vedrOn = false; }
                return s_vedrOn;
            }
        }
    }
}
