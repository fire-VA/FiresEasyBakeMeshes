using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// The hand-off with the game's real objects (SPEC §5): a 64 x 64 R8 mask of zones centred on the player's zone, refreshed 4 times a
    /// second, read by the cull. 255 = everything stands there (an active-area zone loaded for 3 s, or an owner said so sooner with
    /// MarkZoneStanding); 128 = only m_distant objects stand (a loaded zone in the distant ring); 0 = nothing stands (far trees draw).
    /// Kinds flagged Distant hand off at 128, others at 255, so far trees go zone by zone exactly as the real ones appear, and stay until
    /// they have (no gap while a zone fills). The game's own loaded / active-area tests are used, so simulation distance is respected.
    /// </summary>
    internal static class ImpostorHandOff
    {
        internal const int Size = 64;
        private const float StandAfter = 3f;
        private static Texture2D s_tex;
        private static readonly byte[] s_data = new byte[Size * Size];
        private static readonly Dictionary<Vector2s, float> s_loadedAt = new Dictionary<Vector2s, float>();
        private static readonly Dictionary<Vector2s, bool> s_marked = new Dictionary<Vector2s, bool>();
        private static readonly HashSet<Vector2s> s_standing = new HashSet<Vector2s>();
        private static float s_next;
        internal static Vector2Int Origin;
        internal static Texture Texture => s_tex;
        internal static int StandingZones => s_standing.Count;

        internal static void Mark(Vector2s zone, bool standing) { if (standing) s_marked[zone] = true; else s_marked.Remove(zone); }

        internal static void Tick()
        {
            if (s_tex == null)
            {
                s_tex = new Texture2D(Size, Size, TextureFormat.R8, false, true) { name = "FiresEBM_FarZoneMask", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                s_tex.SetPixelData(s_data, 0); s_tex.Apply(false, false);
            }
            if (Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 0.25f;
            var zs = ZoneSystem.instance; var net = ZNet.instance;
            if (zs == null || net == null) return;
            Vector3 refPos = net.GetReferencePosition();
            Vector2s pz = ZoneSystem.GetZone(refPos);
            Origin = new Vector2Int(pz.x - Size / 2, pz.y - Size / 2);
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
                    s_data[ty * Size + tx] = v;
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
