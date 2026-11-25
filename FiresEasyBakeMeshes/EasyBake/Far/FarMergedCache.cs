using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// 1.2.425 ([visual], lead approved; Fire 11-16 "back off a couple zones from the town and the builds pop out"): the MERGED far mesh of a target (zone or
    /// cell) kept in the per-world far cache, so a target whose pieces, snow and settings are unchanged shows at once: no main-thread gather, no merge.
    ///  - Key: target key, tier (cell / coarse / cluster / small-piece size), every member zone's piece fingerprint, the copies' cap levels, the far snow
    ///    revision, the mod set (FarBuildingSources.ModTag) and this format's version. Any change = a new key = a normal build, saved after its merge.
    ///  - Materials are stored as (prefab, part, submesh) references and resolved from the ready far sources; a reference that cannot resolve = a miss.
    ///  - Read and parsed on the far cache's worker; the main thread only resolves materials and hands the job to the upload path.
    ///  - Off while [Far Cache] Enabled is off, or while VA distant reach is registered (that skip depends on where the player stands).
    /// </summary>
    internal static class FarMergedCache
    {
        private const int Version = 1;
        internal const string Bucket = "far-merged";
        internal static int Hits, Misses, Writes, Unresolved;

        internal static bool On => FarCache.Enabled && FarCache.Ready && FarBuildingDraw.VaDistantReach.Count == 0;

        internal static string KeyFor(long targetKey, bool isCell, bool coarse, List<Vector2s> zones, float clusterM)
        {
            ulong prints = 0, caps = 0;
            foreach (var z in zones)
            {
                if (!FarBuildingDigest.Zones.TryGetValue(z, out var fz)) continue;
                prints += fz.Print ^ ((ulong)(uint)z.x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)z.y << 32);
                for (int i = 0; i < fz.Pieces.Length; i++) if (fz.Pieces[i].Cap != 0) caps += (ulong)(i + 1) * fz.Pieces[i].Cap * 0x100000001B3UL + (ulong)(uint)z.x;
            }
            // 1.2.428: no snow revision in the key (far snow is a live material value now; the merged mesh carries no snow level)
            return FarCache.Key("merged", Version + 3, clusterM, targetKey, isCell, coarse, coarse ? FarBuildings.CoarseCluster : 1f, coarse ? FarBuildings.CoarseSmall : 0f,
                                prints, caps, FarBuildings.InteriorOn, FarBuildings.SizeCutOn, FarBuildings.SizeCutMax, FarBuildingSources.ModTag);   // 1.2.431: the cuts change the content
        }

        // ---- format: [ver][mats n][(prefab, part, sub) x n][chunks n] then per chunk [mat][snow][bounds 6f][vcount][verts 24 B each][icount][idx u16] ----
        internal static byte[] Serialize(FarJob job)
        {
            if (job.MatRefs == null || job.Result == null || job.MatRefs.Length != job.Mats.Length * 3) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Version);
                w.Write(job.Mats.Length);
                foreach (int r in job.MatRefs) w.Write(r);
                w.Write(job.Result.Count);
                foreach (var c in job.Result)
                {
                    w.Write(c.MatIdx); w.Write(c.Snow);
                    w.Write(c.Bounds.min.x); w.Write(c.Bounds.min.y); w.Write(c.Bounds.min.z);
                    w.Write(c.Bounds.max.x); w.Write(c.Bounds.max.y); w.Write(c.Bounds.max.z);
                    w.Write(c.VertCount);
                    for (int i = 0; i < c.VertCount; i++) { var v = c.Verts[i]; w.Write(v.P.x); w.Write(v.P.y); w.Write(v.P.z); w.Write(v.N); w.Write(v.T); w.Write(v.UV); }
                    w.Write(c.IdxCount);
                    for (int i = 0; i < c.IdxCount; i++) w.Write(c.Idx[i]);
                }
                return ms.ToArray();
            }
        }

        // worker thread: plain arrays only. null = bad / old data (a miss)
        internal sealed class Parsed { public int[] MatRefs; public List<FarChunk> Chunks; }
        internal static Parsed Parse(byte[] bytes)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (r.ReadInt32() != Version) return null;
                    int mats = r.ReadInt32(); if (mats <= 0 || mats > 4096) return null;
                    var refs = new int[mats * 3]; for (int i = 0; i < refs.Length; i++) refs[i] = r.ReadInt32();
                    int n = r.ReadInt32(); if (n < 0 || n > 100000) return null;
                    var chunks = new List<FarChunk>(n);
                    for (int k = 0; k < n; k++)
                    {
                        int mi = r.ReadInt32(); byte snow = r.ReadByte();
                        var min = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); var max = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                        int vc = r.ReadInt32(); if (vc <= 0 || vc > 65536 || mi < 0 || mi >= mats) return null;
                        var verts = new FarVert[vc];
                        for (int i = 0; i < vc; i++) verts[i] = new FarVert { P = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), N = r.ReadUInt32(), T = r.ReadUInt32(), UV = r.ReadUInt32() };
                        int ic = r.ReadInt32(); if (ic <= 0 || ic > 3 << 20 || ic % 3 != 0) return null;
                        var idx = new ushort[ic];
                        for (int i = 0; i < ic; i++) { idx[i] = r.ReadUInt16(); if (idx[i] >= vc) return null; }
                        var b = new Bounds(); b.SetMinMax(min, max);
                        chunks.Add(new FarChunk { MatIdx = mi, Snow = snow, Verts = verts, VertCount = vc, Idx = idx, IdxCount = ic, Bounds = b });
                    }
                    return new Parsed { MatRefs = refs, Chunks = chunks };
                }
            }
            catch { return null; }
        }

        // main thread: the stored references back to the far sources' own shared materials; null = a source not ready (or changed): a miss
        internal static Material[] Resolve(int[] refs)
        {
            var mats = new Material[refs.Length / 3];
            for (int m = 0; m < mats.Length; m++)
            {
                var src = FarBuildingSources.Get(refs[m * 3]);
                if (src == null) return null;
                int part = refs[m * 3 + 1], sub = refs[m * 3 + 2];
                if (part < 0 || part >= src.Parts.Count) return null;
                var pm = src.Parts[part].Mats;
                if (pm == null || sub < 0 || sub >= pm.Length || pm[sub] == null) return null;
                mats[m] = pm[sub];
            }
            return mats;
        }

        private static float s_nextLog;
        internal static void MaybeLog()
        {
            if (Time.unscaledTime < s_nextLog) return;
            s_nextLog = Time.unscaledTime + 30f;
            if (Hits + Misses + Writes + Unresolved > 0 && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"{FarBuildings.Log} merged cache 30 s ({(On ? "on" : FarCache.Enabled ? "waiting / VA reach" : "off: [Far Cache] Enabled is off")}): {Hits} target(s) shown from disk, {Misses} built (miss), {Unresolved} hit(s) whose sources were not ready yet, {Writes} saved");
            Hits = Misses = Writes = Unresolved = 0;
        }
    }
}
