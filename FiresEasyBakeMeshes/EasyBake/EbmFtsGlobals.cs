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
    internal static class EbmFtsGlobals
    {
        internal static ConfigEntry<bool> ShoreOn;   // 1.2.461: StampOn (Footprint Trails) removed, archived in _archive_footprint_trails
        internal static void Bind(ConfigFile c)
        {
            ShoreOn = EbmConfigMove.Bind(c, "43 - Water - Foam & Shore", "Shore Field", true, "Needs [40 - Shaders] WaterShader = true. The water knows where the shore is: waves ease off in shallow water (as the game's own swim surface does), shallow water takes its shallow colour, and the sea fog stays over the sea. Without it the waves stay full right up to the beach. EBM's own copy of what FiresTossinShade did; while FiresTossinShade is installed it keeps doing it.");
        }

        private static bool? s_fts;
        private static bool FtsLoaded { get { if (s_fts == null) s_fts = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.Fire.FiresTossinShade") || Type.GetType("VerdantsAscent.Water.Core.FiresShoreField, FiresTossinShade") != null; return s_fts.Value; } }
        private static bool s_said;

        internal static void Tick()
        {
            if (FtsLoaded) { if (!s_said) { s_said = true; EasyBakeLog.Info("[FtsGlobals] FiresTossinShade is installed: it keeps writing the shore field, caustic day mask, terrain season globals; EBM writes none of them"); } return; }
            if (!s_said) { s_said = true; EasyBakeLog.Info("[FtsGlobals] FiresTossinShade is not installed: EBM writes the shore field, caustic day mask, its shaders read (1.2.461: no footprint stamp, Vedr owns trails)"); }
            try { DayMask(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] day mask: " + e.Message); }
            // 1.2.460: Season() removed (pushed _FiresSeasonGold/Fresh/MudGate/MudTexOn for the old FTS-copy terrain shader; EBM provides no terrain look of its own, Vedr owns these)
            try { Shore.Tick(); } catch (Exception e) { EasyBakeLog.Debug("[FtsGlobals] shore field: " + e.Message); }
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
            private static float[] s_liveWave; private static Vector2 s_twinC; private static bool s_liveOn, s_shoreSaid;
            // 1.2.462: Vedr's tide/river shaping field (R = tide influence, G = river raise in metres: half floats, FTS FiresShoreField.cs:258 RGBAHalf linear)
            private static readonly int P_FieldTex = Shader.PropertyToID("_FiresTideRiverField");
            private static Texture2D s_fieldTex; private static Color[] s_fieldPx; private static bool s_bakeFieldOk, s_liveFieldOk; private static int s_bakeRev = int.MinValue, s_liveRev = int.MinValue;

            internal static void Tick()
            {
                var player = Player.m_localPlayer;
                if (ShoreOn == null || !ShoreOn.Value || !EbmWater.Active || WorldGenerator.instance == null || player == null)   // 1.2.444: needs [Optimize] WaterShader = true (Fire rule 11-18)
                {
                    if (s_liveOn) { Shader.SetGlobalFloat(P_On, 0f); s_liveOn = false; }
                    if (ShoreOn != null && ShoreOn.Value && !EbmWater.Active && !s_shoreSaid) { s_shoreSaid = true; EasyBakeLog.Info("[Water] Shore Field stands down: it needs [40 - Shaders] WaterShader = true (only EBM's water shader reads it)"); }
                    return;
                }
                Vector3 pp = player.transform.position; var p = new Vector2(pp.x, pp.z);
                float tideNow = Shader.GetGlobalFloat(P_Tide);
                bool tideMoved = s_hasLive && Mathf.Abs(tideNow - s_liveTide) > TideRebake && Time.time - s_lastTideBake > TideMinSec;
                // 1.2.462: Vedr's tide shaping / river keys move VedrApi.SurfaceRevision (VedrApiSea.cs:74-92): re-bake the field when it differs from the live bake's
                if (s_hasLive && VedrSurfaceRevision() != s_liveRev) tideMoved = true;
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
                    // 1.2.462 ([seasons], lead-approved; Fire 11-18: swim / boats / fish / lily pads didn't follow Vedr's tide on inland water): the per-texel tide/river
                    // shaping field, READ from Vedr (VedrApi.TryTideFieldAt, API 36: the same sampler its own WaterVolumeTidePatch / floaters / fish use), baked with
                    // the shore field, as FTS did (FiresShoreField.cs:166-168, :297-303). Without Vedr (or Vedr's sea off): 0, the uniform tide exactly as before.
                    if (s_liveFieldOk) Shader.SetGlobalTexture(P_FieldTex, s_fieldTex);
                    Shader.SetGlobalFloat(P_FieldOn, s_liveFieldOk ? 1f : 0f);
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
                if (s_fieldTex == null)
                {
                    s_fieldTex = new Texture2D(N, N, TextureFormat.RGBAHalf, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "EbmTideRiverField" };
                    s_fieldPx = new Color[N * N];
                }
                s_baking = true; s_bakeC = c; s_row = 0; s_x = 0; s_bakeTide = tide;
                s_bakeFieldOk = false; s_bakeRev = VedrSurfaceRevision();
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
                    // 1.2.462: Vedr's per-texel tide influence + river raise (FTS FiresShoreField.cs:303: (1,0) where there's no answer)
                    if (VedrTideField(wx, wz, out float infl, out float raise)) { s_fieldPx[i] = new Color(infl, raise, 0f, 1f); s_bakeFieldOk = true; }
                    else s_fieldPx[i] = new Color(1f, 0f, 0f, 1f);
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
                if (s_bakeFieldOk) { s_fieldTex.SetPixels(s_fieldPx); s_fieldTex.Apply(false, false); }   // 1.2.462
                if (s_bakeFieldOk != s_liveFieldOk) EasyBakeLog.Info($"[FtsGlobals] tide/river field {(s_bakeFieldOk ? "on: inland water is drawn at Vedr's shaped tide (VedrApi.TryTideFieldAt), as swimming, boats and fish use it" : "off: Vedr's tide field isn't answering (no Vedr, or its sea is off); the drawn tide is uniform")}");
                s_liveFieldOk = s_bakeFieldOk; s_liveRev = s_bakeRev;
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

            // 1.2.462: Vedr's tide/river field sampler, by name (VedrApi.TryTideFieldAt(float, float, out float influence, out float raise), Vedr 0.8.283 API 36), and its
            // SurfaceRevision (moves on the tide shaping / river keys). Main thread (the bake's Step runs in Tick). Absent / older Vedr = false, revision 0.
            private static bool s_tideLooked; private static MethodInfo s_tideGet; private static PropertyInfo s_revProp; private static readonly object[] s_tideArgs = new object[4];
            private static void LookTide()
            {
                if (s_tideLooked) return;
                s_tideLooked = true;
                var t = Type.GetType("Vedr.VedrApi, Norger.Vedr");
                s_tideGet = t?.GetMethod("TryTideFieldAt", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(float), typeof(float), typeof(float).MakeByRefType(), typeof(float).MakeByRefType() }, null);
                s_revProp = t?.GetProperty("SurfaceRevision", BindingFlags.Public | BindingFlags.Static);
            }
            private static bool VedrTideField(float x, float z, out float influence, out float raise)
            {
                influence = 1f; raise = 0f;
                LookTide();
                if (s_tideGet == null) return false;
                try { s_tideArgs[0] = x; s_tideArgs[1] = z; s_tideArgs[2] = 1f; s_tideArgs[3] = 0f; bool ok = (bool)s_tideGet.Invoke(null, s_tideArgs); influence = (float)s_tideArgs[2]; raise = (float)s_tideArgs[3]; return ok; }
                catch { s_tideGet = null; return false; }
            }
            private static int VedrSurfaceRevision()
            {
                LookTide();
                if (s_revProp == null) return 0;
                try { return Convert.ToInt32(s_revProp.GetValue(null)); } catch { s_revProp = null; return 0; }
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

    }
}
