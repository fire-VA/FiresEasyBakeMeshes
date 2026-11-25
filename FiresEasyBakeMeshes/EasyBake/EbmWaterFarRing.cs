using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.469 ([visual], lead GO 11-19; Fire: "yes, lets do that, but we will need to make sure it does not hurt performance"; his shots: the loaded water is a
    /// hard-edged teal disc, the far water a pale mirror past it). [44 - Water - Surface &amp; Reflection] WaterFarRing (default OFF, needs [40 - Shaders] WaterShader):
    /// the water past the loaded zones is drawn by EBM/Water itself, on ONE player-centred mesh out to the ring radius, instead of the game's distant plane.
    /// Our own method, ported from FiresTossinShade (FiresWaterExtendedRing.cs:7-42, :340-403, :413-437; FiresClipmapOcean.cs:496-497):
    ///  - verts on a fixed 64 m lattice (the zone grid); the object snaps to the reference point's zone, so every vertex always sits on the same world lattice and the
    ///    world-position waves never jump on a recenter (one transform write per zone crossing, no rebuild, no garbage);
    ///  - a square hole over the loaded zones (the synced near simulation distance), so the ring abuts the zone water on the zone grid and never draws over it;
    ///  - the material is a copy of the zone water's own EBM/Water material (re-copied once a second, so every look key reaches it), with distance tessellation
    ///    (_IsLod 1: full _Tess out to the detail range, falling to the far floor past it; EbmWater.shader FiresTessFromDist :1205-1210) from live keys;
    ///  - reflection: BlendProbes with the ring itself as the probe anchor (it follows the player), so it samples the same ReflectionUpdate probe as the zone tiles;
    ///  - the game's distant plane (_GameMain/_Environment/WaterPlane) is deactivated while the ring is on (renderer-off does not stick: FTS :405-411) and
    ///    re-activated when it goes off; WaterEdgeBlend / WaterEdgeCrossFade (which ease the near water into that plane) stand down while it is on.
    /// Depth look: the per-pixel bed (_CameraDepthTexture) works wherever far terrain writes depth; the shore field's depth colour covers its baked rect only and reads
    /// "deep" past it (EbmWater.shader :921-925). No CPU far depth field (lead's first-build rule).
    /// </summary>
    internal static class EbmWaterFarRing
    {
        internal static ConfigEntry<bool> On;
        internal static ConfigEntry<float> Radius, TessMax, TessMin, DetailRange;
        private const string Sec = "44 - Water - Surface & Reflection";
        private const int TileSize = 64;
        private const float WaveBoundsY = 60f;
        private const int RingQueue = 2950;   // after the zone water (2901): FTS DiskRenderQueue, the same reason
        private static readonly Unity.Profiling.ProfilerMarker s_mark = new Unity.Profiling.ProfilerMarker("EBM water: far ring");

        private static Material s_mat; private static Material s_template;
        private static Mesh s_mesh; private static GameObject s_go; private static MeshRenderer s_r;
        private static float s_builtRadius = -1f; private static int s_builtHole = -1;
        private static Vector2s s_cell = new Vector2s(short.MinValue, short.MinValue);
        private static GameObject s_plane; private static bool s_weHid; private static float s_planeScanAt, s_copyAt;
        private static int p_recenters, p_frames; private static long p_ticks, p_worst; private static float s_logAt;
        private static bool s_saidOn;

        internal static void Bind(ConfigFile config)
        {
            On = EbmConfigMove.Bind(config, Sec, "WaterFarRing", false /* 1.2.469: OFF until Fire's look check and the lead's perf legs */,
                "Needs [40 - Shaders] WaterShader = true. The water past the loaded zones is drawn by EBM's own water, on one mesh around you out to the ring radius, instead of the game's flat distant plane: the same colours, depth look and sky reflection as the near water, so there is no hard circle where they meet. The game's distant plane is hidden while this is on (and WaterEdgeBlend / WaterEdgeCrossFade stand down). Off = the game's distant plane as before. Applies at once.");
            Radius = EbmConfigMove.Bind(config, Sec, "WaterFarRingRadius", 3000f, new ConfigDescription("With WaterFarRing: how far the far water reaches, metres. Range 1000 to 6000. Default 3000.", new AcceptableValueRange<float>(1000f, 6000f)));
            TessMax = EbmConfigMove.Bind(config, Sec, "WaterFarRingTess", 16f, new ConfigDescription("With WaterFarRing: wave detail of the far water inside the detail range (tessellation factor per 64 m cell). Higher = finer distant waves, more GPU. Range 1 to 64. Default 16. Live.", new AcceptableValueRange<float>(1f, 64f)));
            TessMin = EbmConfigMove.Bind(config, Sec, "WaterFarRingMinTess", 2f, new ConfigDescription("With WaterFarRing: the far water's lowest wave detail, out past the detail range (where fog hides it). Higher = wavier horizon, more GPU. Range 1 to 32. Default 2. Live.", new AcceptableValueRange<float>(1f, 32f)));
            DetailRange = EbmConfigMove.Bind(config, Sec, "WaterFarRingDetailRange", 300f, new ConfigDescription("With WaterFarRing: metres from the camera over which the far water keeps its full wave detail before falling to the lowest. Range 64 to 2000. Default 300. Live.", new AcceptableValueRange<float>(64f, 2000f)));
            On.SettingChanged += (_, __) => s_saidOn = false;
            System.EventHandler tune = (_, __) => Tune();
            TessMax.SettingChanged += tune; TessMin.SettingChanged += tune; DetailRange.SettingChanged += tune;
        }

        /// <summary>True while the ring draws (EdgeBlend / CrossFade read it to stand down).</summary>
        internal static bool Drawing => s_go != null && s_go.activeSelf;

        /// <summary>EbmWater.Tick, every frame (before its key gate, so a key off restores the game's plane).</summary>
        internal static void Tick(bool waterShader)
        {
            if (On == null) return;
            bool want = On.Value && waterShader && ZNet.instance != null && ZoneSystem.instance != null;
            if (!want) { if (s_go != null || s_weHid) Stop(On.Value ? "WaterShader off or no world" : "key off"); return; }
            long t0 = Stopwatch.GetTimestamp();
            using (s_mark.Auto())
            {
                if (!EnsureMaterial()) return;   // no zone water on EBM/Water yet: retry next frame (a reference compare)
                EnsureMesh();
                HidePlane();
                Vector2s z = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());   // the centre the game's plane hole and the loaded zones use
                bool moved = z.x != s_cell.x || z.y != s_cell.y;
                if (moved)
                {
                    s_cell = z;
                    s_go.transform.position = new Vector3(z.x * TileSize, ZoneSystem.c_WaterLevel, z.y * TileSize);
                    p_recenters++;
                }
                // 1.2.471 (Fire 11-19: "when we move the far water disappears before the near water fills in completely"): the hole is the zones whose game
                // water tile is LIVE right now, not the near-distance square. A zone's water comes with its zone root (ZoneSystem.SpawnZone instantiates
                // m_zonePrefab, REF ZoneSystem.cs:684-699) and leaves with it, so a cell is open only while its own water draws: never a frame with neither.
                // Per frame: one pass over WaterVolume.Instances (tens) for a signature; the index list is rebuilt only when that set or the centre changed.
                if (LiveSignature(out int count, out long sig) || moved || s_holeDirty) RebuildHole(count);
            }
            long dt = Stopwatch.GetTimestamp() - t0; p_ticks += dt; if (dt > p_worst) p_worst = dt; p_frames++;
            Report();
        }

        private static bool EnsureMaterial()
        {
            var tpl = EbmWater.ZoneTemplate();
            if (tpl == null) return s_mat != null;
            if (s_mat == null || s_template != tpl) { if (s_mat != null) Object.Destroy(s_mat); s_mat = new Material(tpl) { name = "EBM water far ring" }; s_template = tpl; s_copyAt = 0f; if (s_r != null) s_r.sharedMaterial = s_mat; }
            float now = Time.unscaledTime;
            if (now >= s_copyAt) { s_copyAt = now + 1f; s_mat.CopyPropertiesFromMaterial(tpl); Tune(); }   // every look key on the zone water reaches the ring within a second
            return true;
        }

        private static void Tune()
        {
            if (s_mat == null) return;
            s_mat.enableInstancing = false;
            s_mat.renderQueue = RingQueue;
            Set("_IsLod", 1f); Set("_Tess", TessMax.Value); Set("_IsLodMinTess", Mathf.Min(TessMin.Value, TessMax.Value)); Set("_VisibleMaxDistance", DetailRange.Value);
            Set("_FiresClipmapMorph", 0f);
        }
        private static void Set(string p, float v) { if (s_mat.HasProperty(p)) s_mat.SetFloat(p, v); }

        private static void EnsureMesh()
        {
            if (s_go != null && Mathf.Approximately(s_builtRadius, Radius.Value)) return;
            int hole = ZNet.instance.GetSyncedSimulationDistance().NearSimulationDistance;   // for the Info line only (1.2.471: the hole follows the live zone water)
            var mesh = BuildGrid(Radius.Value, out int verts, out int tris);
            s_builtRadius = Radius.Value; s_builtHole = hole; s_holeDirty = true;
            if (s_go == null)
            {
                s_go = new GameObject("EBM water far ring") { hideFlags = HideFlags.HideAndDontSave };
                s_go.layer = WaterLayer();
                s_go.AddComponent<MeshFilter>();
                s_r = s_go.AddComponent<MeshRenderer>();
                s_r.sharedMaterial = s_mat;
                s_r.shadowCastingMode = ShadowCastingMode.Off; s_r.receiveShadows = false;
                s_r.lightProbeUsage = LightProbeUsage.Off;
                s_r.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;   // FTS 0.3.391 (FiresClipmapOcean.cs:496): the zone tiles' probe, not the default skybox cube
                s_r.probeAnchor = s_go.transform;                               // centred on the player's zone: inside both ReflectionUpdate boxes, steady while moving
                s_r.allowOcclusionWhenDynamic = false;
                s_r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            }
            if (s_mesh != null) Object.Destroy(s_mesh);
            s_mesh = mesh; s_go.GetComponent<MeshFilter>().sharedMesh = mesh;
            s_cell = new Vector2s(short.MinValue, short.MinValue);   // place it this frame
            if (!s_saidOn)
            {
                s_saidOn = true;
                EasyBakeLog.Info($"[Water] WaterFarRing on: EBM/Water draws the far water to {Radius.Value:0} m on one mesh ({verts:N0} vertices, {tris:N0} triangles, 64 m cells, its hole follows the zones whose own water tile is live (1.2.471), near distance {hole} zone(s)); tessellation {TessMax.Value:0.#} out to {DetailRange.Value:0} m, then down to {Mathf.Min(TessMin.Value, TessMax.Value):0.#}; reflection: the zone tiles' probe (BlendProbes, anchored on the player); the game's distant plane {(FindPlane() != null ? "hidden" : "not found yet (hidden when it appears)")}; WaterEdgeBlend / WaterEdgeCrossFade stand down while it draws");
            }
        }

        // FTS FiresWaterExtendedRing.BuildGridMesh (:340-403): quads centred on zone centres (half-cell shift), kept inside the radius.
        // 1.2.471: every quad inside the radius is kept in s_quads (its zone offset + first vertex); the triangle list leaves out the live water zones (RebuildHole).
        private static Mesh BuildGrid(float radius, out int vcount, out int tcount)
        {
            int rc = Mathf.Max(1, Mathf.CeilToInt(radius / TileSize));
            int side = 2 * rc + 2;
            vcount = side * side;
            var verts = new Vector3[vcount]; var uvs = new Vector2[vcount]; var normals = new Vector3[vcount]; var colors = new Color32[vcount];
            for (int zi = 0; zi < side; zi++)
                for (int xi = 0; xi < side; xi++)
                {
                    int i = zi * side + xi;
                    float lx = (xi - rc) * TileSize - TileSize * 0.5f, lz = (zi - rc) * TileSize - TileSize * 0.5f;
                    verts[i] = new Vector3(lx, 0f, lz); uvs[i] = new Vector2(lx / TileSize, lz / TileSize); normals[i] = Vector3.up; colors[i] = new Color32(255, 255, 255, 255);
                }
            float rSqr = radius * radius;
            s_quads.Clear(); s_side = side;
            for (int zi = 0; zi < side - 1; zi++)
                for (int xi = 0; xi < side - 1; xi++)
                {
                    float cx = (xi - rc) * TileSize, cz = (zi - rc) * TileSize;
                    if (cx * cx + cz * cz > rSqr) continue;
                    s_quads.Add(new Quad { Dx = (short)(xi - rc), Dz = (short)(zi - rc), I0 = zi * side + xi });
                }
            s_tris.Clear(); s_tris.Capacity = Mathf.Max(s_tris.Capacity, s_quads.Count * 6);
            foreach (var q in s_quads) AddQuad(q);   // no hole yet: RebuildHole cuts it this frame
            tcount = s_tris.Count / 3;
            var m = new Mesh { name = "EBM water far ring", indexFormat = vcount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.vertices = verts; m.uv = uvs; m.normals = normals; m.colors32 = colors;
            m.SetTriangles(s_tris, 0, false);
            float half = rc * TileSize;
            m.bounds = new Bounds(Vector3.zero, new Vector3(half * 2f, WaveBoundsY, half * 2f));
            m.UploadMeshData(false);   // 1.2.471: kept readable (~0.4 MB): the triangle list changes with the live water zones (SetTriangles needs a readable mesh)
            return m;
        }

        // ── 1.2.471 live-water hole ──
        private struct Quad { public short Dx, Dz; public int I0; }
        private static readonly List<Quad> s_quads = new List<Quad>();
        private static readonly List<int> s_tris = new List<int>();
        private static readonly HashSet<long> s_live = new HashSet<long>();
        private static readonly Dictionary<WaterVolume, bool> s_zoneWater = new Dictionary<WaterVolume, bool>();
        private static int s_side, s_liveCount = -1, p_holeRebuilds, p_holeCells; private static long s_liveSig, p_holeTicks, p_holeWorst; private static bool s_holeDirty;
        private static long ZoneKey(int x, int z) => ((long)x << 32) ^ (uint)z;
        private static void AddQuad(Quad q) { int i0 = q.I0, i1 = i0 + 1, i2 = i0 + s_side, i3 = i2 + 1; s_tris.Add(i0); s_tris.Add(i2); s_tris.Add(i1); s_tris.Add(i1); s_tris.Add(i2); s_tris.Add(i3); }
        // the game's zone water only (a WaterVolume under a ZNetView is a built piece: the hot tub), cached per volume as EbmWater does
        private static bool IsZoneWater(WaterVolume v)
        {
            if (v == null || v.m_waterSurface == null) return false;
            if (!s_zoneWater.TryGetValue(v, out bool z)) { z = v.GetComponentInParent<ZNetView>(true) == null; if (s_zoneWater.Count > 4096) s_zoneWater.Clear(); s_zoneWater[v] = z; }
            return z && v.m_waterSurface.enabled && v.gameObject.activeInHierarchy;
        }
        // true = the set of live water zones changed since the last rebuild (order-free signature: sum of keys + count; no allocation)
        private static bool LiveSignature(out int count, out long sig)
        {
            count = 0; sig = 0L;
            var list = WaterVolume.Instances;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    Vector2s z = ZoneSystem.GetZone(v.transform.position);
                    sig += ZoneKey(z.x, z.y) * -7046029254386353131L; count++;
                }
            return count != s_liveCount || sig != s_liveSig;
        }
        private static void RebuildHole(int count)
        {
            long t0 = Stopwatch.GetTimestamp();
            s_live.Clear();
            long sig = 0L;
            var list = WaterVolume.Instances;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var v = list[i];
                    if (!IsZoneWater(v)) continue;
                    Vector2s z = ZoneSystem.GetZone(v.transform.position);
                    long k = ZoneKey(z.x, z.y);
                    s_live.Add(k); sig += k * -7046029254386353131L;
                }
            s_liveCount = count; s_liveSig = sig; s_holeDirty = false;
            s_tris.Clear(); int cut = 0;
            foreach (var q in s_quads)
            {
                if (s_live.Contains(ZoneKey(s_cell.x + q.Dx, s_cell.y + q.Dz))) { cut++; continue; }   // this zone's own water tile draws it
                AddQuad(q);
            }
            if (s_mesh != null) s_mesh.SetTriangles(s_tris, 0, false);   // index buffer only; vertices and bounds unchanged
            p_holeCells = cut; p_holeRebuilds++;
            long dt = Stopwatch.GetTimestamp() - t0; p_holeTicks += dt; if (dt > p_holeWorst) p_holeWorst = dt;
        }

        private static int WaterLayer()
        {
            var list = WaterVolume.Instances;
            if (list != null) foreach (var v in list) if (v != null && v.m_waterSurface != null) return v.m_waterSurface.gameObject.layer;
            int l = LayerMask.NameToLayer("Water"); return l >= 0 ? l : 4;
        }

        // the game's distant plane: GameObject.Find sees active objects only, so the reference is kept once found (FTS :418-422)
        private static GameObject FindPlane()
        {
            if (s_plane == null && Time.unscaledTime >= s_planeScanAt) { s_planeScanAt = Time.unscaledTime + 1f; s_plane = GameObject.Find("_GameMain/_Environment/WaterPlane"); }
            return s_plane;
        }
        private static void HidePlane()
        {
            var p = FindPlane();
            if (p != null && p.activeSelf) { p.SetActive(false); s_weHid = true; }
        }

        private static void Stop(string why)
        {
            if (s_weHid && s_plane != null && !s_plane.activeSelf) s_plane.SetActive(true);   // only what we hid
            bool had = s_go != null || s_weHid;
            s_weHid = false;
            if (s_go != null) { Object.Destroy(s_go); s_go = null; s_r = null; }
            if (s_mesh != null) { Object.Destroy(s_mesh); s_mesh = null; }
            if (s_mat != null) { Object.Destroy(s_mat); s_mat = null; s_template = null; }
            s_builtRadius = -1f; s_builtHole = -1; s_saidOn = false;
            s_liveCount = -1; s_holeDirty = true; s_quads.Clear(); s_tris.Clear(); s_live.Clear(); s_zoneWater.Clear();   // 1.2.471
            if (had) EasyBakeLog.Info($"[Water] WaterFarRing off ({why}): the game's distant plane is back");
        }

        private static void Report()
        {
            float now = Time.unscaledTime;
            if (now < s_logAt) return;
            bool first = s_logAt <= 0f;
            s_logAt = now + 30f;
            if (first) { p_recenters = p_frames = 0; p_ticks = p_worst = 0L; return; }
            double ms = 1000.0 / Stopwatch.Frequency;
            EasyBakeLog.InfoFirst("water-far-ring", $"[Water] WaterFarRing 30 s: {p_recenters} recenter(s) (one transform write each), tick avg {(p_frames > 0 ? p_ticks * ms / p_frames : 0.0):0.0000} ms/frame, worst {p_worst * ms:0.000} ms over {p_frames} frame(s); plane {(s_plane == null ? "not found" : s_plane.activeSelf ? "SHOWING" : "hidden")}; hole (1.2.471, live zone water): {p_holeCells} cell(s) now, {p_holeRebuilds} index rebuild(s), avg {(p_holeRebuilds > 0 ? p_holeTicks * ms / p_holeRebuilds : 0.0):0.000} ms, worst {p_holeWorst * ms:0.000} ms; tess {TessMax.Value:0.#} / {Mathf.Min(TessMin.Value, TessMax.Value):0.#} past {DetailRange.Value:0} m");
            p_recenters = p_frames = 0; p_ticks = p_worst = 0L; p_holeRebuilds = 0; p_holeTicks = p_holeWorst = 0L;
        }
    }
}
