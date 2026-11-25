using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The hand-off with the game's real objects (SPEC §5): an N x N R8 mask of zones centred on the player's zone, refreshed 4 times a
    /// second, read by the cull. 255 = everything stands there (an active-area zone loaded for 3 s, or an owner said so sooner with
    /// MarkZoneStanding); 128 = only m_distant objects stand (a loaded zone in the distant ring); 0 = nothing stands (far trees draw).
    /// Kinds flagged Distant hand off at 128, others at 255, so far trees go zone by zone exactly as the real ones appear, and stay until
    /// they have (no gap while a zone fills). The game's own loaded / active-area tests are used, so simulation distance is respected.
    /// 1.2.485 ([visual], design 3.3 / limit (ii), [lead] GO): N was a fixed 64 (= +-2048 m), so far trees past 2048 m had no ground gate
    /// (outside the mask = nothing stands = draw). N now covers the far reach (64-192, even), but ONLY when the loaded cull compute reads the
    /// mask's own size (marker kernel "MaskSizeAware", FiresCoreImpostorCull.compute 1.2.485); with an older bundle N stays 64, so a new DLL
    /// beside an old bundle never misreads the mask.
    /// </summary>
    internal static class ImpostorHandOff
    {
        internal const int BaseSize = 64, MaxSize = 192;
        private const float StandAfter = 3f;
        private static int s_size = BaseSize;
        internal static int Size => s_size;
        private static Texture2D s_tex;
        private static byte[] s_data = new byte[BaseSize * BaseSize];
        private static bool[] s_noGround = new bool[BaseSize * BaseSize];   // 1.2.485: the last ground pass (true = no ground drawn there), re-applied between passes
        private static bool[] s_tested = new bool[BaseSize * BaseSize];     // 1.2.485 ([2ic]): the cell was 0 at its last ground test (so s_noGround holds a real answer for it)
        private static int s_groundTick; private static Vector2Int s_groundOrigin = new Vector2Int(int.MinValue, 0);
        private static readonly Dictionary<Vector2s, float> s_loadedAt = new Dictionary<Vector2s, float>();
        private static readonly Dictionary<Vector2s, bool> s_marked = new Dictionary<Vector2s, bool>();
        private static readonly HashSet<Vector2s> s_standing = new HashSet<Vector2s>();
        private static float s_next;
        private static int s_sizeAware = -1;   // -1 not asked yet, 0 old bundle, 1 the cull reads the mask's size
        internal static Vector2Int Origin;
        internal static Texture Texture => s_tex;
        internal static int StandingZones => s_standing.Count;

        internal static void Mark(Vector2s zone, bool standing) { if (standing) s_marked[zone] = true; else s_marked.Remove(zone); }

        // 1.2.485: the mask size for the far reach (+2 zones of margin each side), when the cull can read it.
        private static int WantedSize()
        {
            if (s_sizeAware < 0)
            {
                var cs = ImpostorAssets.Cull;
                if (cs == null) return BaseSize;   // not loaded yet: ask again next tick
                s_sizeAware = cs.HasKernel("MaskSizeAware") ? 1 : 0;
                EasyBakeLog.Info(s_sizeAware == 1
                    ? "[FarImpostors] zone mask follows the far reach (the cull reads the mask size): far trees are ground-gated out to the reach"
                    : "[FarImpostors] zone mask stays 64 x 64 (+-2048 m): this impostor bundle predates 1.2.485, far trees past 2048 m are not ground-gated");
            }
            if (s_sizeAware != 1) return BaseSize;
            int n = 2 * Mathf.CeilToInt(ImpostorEngine.Distance / 64f) + 4;
            n += n & 1;
            return Mathf.Clamp(n, BaseSize, MaxSize);
        }

        internal static void Tick()
        {
            int want = WantedSize();
            if (s_tex == null || want != s_size)
            {
                if (s_tex != null) Object.Destroy(s_tex);
                s_size = want;
                s_data = new byte[s_size * s_size]; s_noGround = new bool[s_size * s_size]; s_tested = new bool[s_size * s_size]; s_groundOrigin = new Vector2Int(int.MinValue, 0);
                s_tex = new Texture2D(s_size, s_size, TextureFormat.R8, false, true) { name = "FiresEBM_FarZoneMask", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                s_tex.SetPixelData(s_data, 0); s_tex.Apply(false, false);
                s_next = 0f;   // fill it this tick
            }
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.25f;
            var zs = ZoneSystem.instance; var net = ZNet.instance;
            if (zs == null || net == null) return;
            int size = s_size;
            Vector3 refPos = net.GetReferencePosition();
            Vector2s pz = ZoneSystem.GetZone(refPos);
            Origin = new Vector2Int(pz.x - size / 2, pz.y - size / 2);
            System.Array.Clear(s_data, 0, s_data.Length);
            float now = Time.unscaledTime;
            const int reach = 12;   // loaded zones are never farther than the distant ring
            bool grid = FarZoneGrid.On;   // [Optimize] FarZoneGrid: the same IsZoneLoaded answers from this frame's grid
            for (int dy = -reach; dy <= reach; dy++)
                for (int dx = -reach; dx <= reach; dx++)
                {
                    var z = new Vector2s(pz.x + dx, pz.y + dy);
                    byte v = 0;
                    if (grid ? FarZoneGrid.Loaded(z) : zs.IsZoneLoaded(z))
                    {
                        if (!s_loadedAt.TryGetValue(z, out float t)) s_loadedAt[z] = t = now;
                        bool active = ZNetScene.InActiveArea(ZoneSystem.GetZonePos(z), pz);
                        if (active) v = (now - t >= StandAfter || s_marked.ContainsKey(z)) ? (byte)255 : (byte)0;
                        else v = 128;
                    }
                    else s_loadedAt.Remove(z);
                    if (v == 255 && s_standing.Add(z)) ImpostorEngine.RaiseHandedOff(z);
                    else if (v != 255) s_standing.Remove(z);
                    int tx = z.x - Origin.x, ty = z.y - Origin.y;
                    s_data[ty * size + tx] = v;
                }
            // 1.2.416 [Far] FarNeedsGround (FarGroundCover): a mask zone that would draw far trees (0) but has NO ground drawn under it yet (vanilla's TerrainLod
            // not built there: arrival, or its 256 m re-centre still waiting for every region) is masked 255, the same "hide" the cull already honours, so no
            // impostor floats in the sky. Not added to s_standing (no hand-off event). Re-checked 4 times a second, so they appear the moment the ground does.
            // 1.2.484: Covered also counts EBM's own shown far tiles. 1.2.485: over the whole (reach-sized) mask.
            // 1.2.485 (cost at up to 192 x 192): the ground test runs once a second (every 4th tick) or when the mask moved; between, the last answer is re-applied.
            if (FarGroundCover.On)
            {
                bool pass = (s_groundTick++ & 3) == 0 || Origin != s_groundOrigin;
                if (pass) s_groundOrigin = Origin;
                for (int ty = 0; ty < size; ty++)
                    for (int tx = 0; tx < size; tx++)
                    {
                        int i = ty * size + tx;
                        bool zero = s_data[i] == 0;
                        // [2ic]: a cell that is 0 now but was not at its last test (a zone just unloaded: far trees take over there) is tested THIS tick, never on a stale answer
                        if (pass || (zero && !s_tested[i])) { s_noGround[i] = zero && !FarGroundCover.Covered(new Vector2s(Origin.x + tx, Origin.y + ty)); s_tested[i] = zero; }
                        else if (!zero) s_tested[i] = false;
                        if (zero && s_noGround[i]) s_data[i] = 255;
                    }
            }
            s_tex.SetPixelData(s_data, 0); s_tex.Apply(false, false);
        }

        internal static void Clear()
        {
            if (s_tex != null) Object.Destroy(s_tex); s_tex = null;
            s_loadedAt.Clear(); s_marked.Clear(); s_standing.Clear();
        }
    }
}
