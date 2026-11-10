using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    /// <summary>
    /// 1.2.197 ([ghost], DESIGN_OcclusionLOD §5, lead 11-08): the READ that decides the fractured-rock lever. Vanilla MineRock5.UpdateMesh
    /// already merges every intact hit area into one per-rock mesh (CombineMeshes, 2 submeshes) and points its LODGroup's LOD0 at it, so
    /// the open questions are: how far are the rocks, which LOD do they draw, how many tris per LOD, do they cast shadows, and is every
    /// rock's mesh its own (unbatchable) copy. Console "ebm_rocks [radius m, default 300]" (read-only, also written to the log at Debug):
    /// one line per MineRock5 in range (distance, intact, LOD now / count, screen height, transition heights, tris per LOD, shadow mode,
    /// per-instance mesh) and one summary per prefab (count, intact, tris drawn now, tris if every one drew its last LOD).
    /// LOD "now" is computed the way Unity picks it: screen-relative height = lodGroup.size x the largest lossy scale / (2 x distance x
    /// tan(fov/2)) x QualitySettings.lodBias, the first LOD whose transition height is below it (past the last = culled).
    /// MineRock5's private fields are read by reflection (never a publicized reference).
    /// </summary>
    internal static class RockReport
    {
        private static FieldInfo s_hitAreas, s_meshFilter, s_health, s_areaHealth;

        internal static string Run(float radius)
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return "ebm_rocks: no camera";
            if (s_hitAreas == null)
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                s_hitAreas = typeof(MineRock5).GetField("m_hitAreas", F);
                s_meshFilter = typeof(MineRock5).GetField("m_meshFilter", F);
                s_health = typeof(MineRock5).GetField("m_health", F);
                var hit = typeof(MineRock5).GetNestedType("HitArea", BindingFlags.NonPublic | BindingFlags.Public);
                s_areaHealth = hit?.GetField("m_health", F);
            }
            Vector3 cp = cam.transform.position;
            float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var sb = new StringBuilder();
            var perPrefab = new Dictionary<string, int[]>();   // count, intact, trisNow, trisLast
            int n = 0;
            foreach (var rock in UnityEngine.Object.FindObjectsByType<MineRock5>(FindObjectsSortMode.None))
            {
                if (rock == null) continue;
                float dist = Vector3.Distance(cp, rock.transform.position);
                if (dist > radius) continue;
                n++;
                string name = Utils.GetPrefabName(rock.gameObject);
                bool intact = true; int areas = 0;
                try
                {
                    float full = s_health != null ? (float)s_health.GetValue(rock) : 0f;
                    if (s_hitAreas?.GetValue(rock) is System.Collections.IList list)
                        foreach (var a in list) { areas++; if (s_areaHealth != null && (float)s_areaHealth.GetValue(a) < full) intact = false; }
                }
                catch { intact = false; }

                var lg = rock.GetComponentInChildren<LODGroup>();
                var lods = lg != null ? lg.GetLODs() : new LOD[0];
                float rel = 0f; int now = 0;
                if (lg != null)
                {
                    Vector3 s = lg.transform.lossyScale;
                    float size = lg.size * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
                    rel = size / (2f * Mathf.Max(0.01f, dist) * tanHalf) * QualitySettings.lodBias;
                    now = lods.Length;   // culled unless a LOD matches
                    for (int i = 0; i < lods.Length; i++) if (rel >= lods[i].screenRelativeTransitionHeight) { now = i; break; }
                }
                var trisPer = new int[lods.Length];
                string shadow = "?";
                for (int i = 0; i < lods.Length; i++)
                    foreach (var r in lods[i].renderers)
                    {
                        if (r == null) continue;
                        if (i == 0 && shadow == "?") shadow = r.shadowCastingMode.ToString();
                        var mf = r.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) trisPer[i] += Tris(mf.sharedMesh);
                    }
                bool ownMesh = false;
                try { var mf = s_meshFilter?.GetValue(rock) as MeshFilter; ownMesh = mf != null && mf.sharedMesh != null && mf.sharedMesh.name.StartsWith("___MineRock5", StringComparison.Ordinal); } catch { }

                int trisNow = now < lods.Length ? trisPer[now] : 0;
                int trisLast = lods.Length > 0 ? trisPer[lods.Length - 1] : 0;
                if (!perPrefab.TryGetValue(name, out var agg)) perPrefab[name] = agg = new int[4];
                agg[0]++; if (intact) agg[1]++; agg[2] += trisNow; agg[3] += trisLast;

                var heights = new string[lods.Length];
                for (int i = 0; i < lods.Length; i++) heights[i] = lods[i].screenRelativeTransitionHeight.ToString("0.000");
                sb.Append($"[Rocks] {name} at {dist:0} m: {(intact ? "intact" : "damaged")} ({areas} areas), LOD {(now < lods.Length ? now.ToString() : "culled")}/{lods.Length}, screen {rel:0.000} vs [{string.Join(", ", heights)}], tris per LOD [{string.Join(", ", Array.ConvertAll(trisPer, t => t.ToString()))}], shadows {shadow}, per-rock mesh {ownMesh}\n");
            }
            sb.Insert(0, $"[Rocks] {n} MineRock5 within {radius:0} m of the camera (lodBias {QualitySettings.lodBias:0.##}, fov {cam.fieldOfView:0})\n");
            foreach (var kv in perPrefab)
                sb.Append($"[Rocks] summary {kv.Key}: {kv.Value[0]} rock(s), {kv.Value[1]} intact; tris drawn now {kv.Value[2]}, if all at their last LOD {kv.Value[3]}\n");
            sb.Append(IntactRockLod.Summary()).Append('\n');   // 1.2.201
            return sb.ToString().TrimEnd('\n');
        }

        private static int Tris(Mesh m)
        {
            int t = 0;
            for (int i = 0; i < m.subMeshCount; i++) t += (int)(m.GetIndexCount(i) / 3);
            return t;
        }
    }
}
