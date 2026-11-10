using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>One mesh of a prefab's far look, in the prefab root's space, with its materials (one per submesh).</summary>
    internal sealed class FarPart
    {
        public Matrix4x4 Local; public Material[] Mats;
        public Vector3[] Pos; public Vector3[] Nrm; public Vector4[] Tan; public Vector2[] Uv; public int[][] Sub;
        public bool Ready, Failed;
        public bool IsCap;   // 1.2.262: part of the healthy snow cap (WearNTear.m_snow); merged only for a copy that has snow

    }

    /// <summary>A prefab's far look: the parts the far tier merges for every placed copy.</summary>
    internal sealed class FarSource
    {
        public int Prefab; public string Name; public List<FarPart> Parts = new List<FarPart>();
        public bool Ready, Failed; public int Vertices;
        public bool Closed;   // far fix: TryFinish ran its end once
    }

    /// <summary>
    /// One far mesh per prefab, read ONCE. Which renderers: the LODGroup's last LOD (for VA prefabs that is FAP's authored far stage),
    /// else the prefab's own mesh renderers, minus WearNTear's worn/broken/wet states. Readable meshes are copied directly; the rest
    /// come from one AsyncGPUReadback each (no stall, unlike a synchronous GPU read on the merge path). The arrays are kept in the
    /// per-install far cache (#4), so a later session reads no mesh at all. Opaque and cutout materials only.
    /// </summary>
    internal static class FarBuildingSources
    {
        private const int CacheVersion = 3;   // 1.2.261: caps out of the body (2); 1.2.262: caps back as their own IsCap parts after the body (3)
        private static readonly Dictionary<int, FarSource> s_sources = new Dictionary<int, FarSource>();
        private static readonly Queue<int> s_pending = new Queue<int>();
        private static int s_inFlight;
        private static string s_cacheTag;
        private static bool s_loggedGpu, s_loggedGpuOk;
        // far fix: on (default) the read leaves the shared prefab mesh's buffer targets alone; AsyncGPUReadback does not need Raw
        private static bool NoRawFlags => FiresEasyBakeMeshesPlugin.FarMeshReadNoRawFlags == null || FiresEasyBakeMeshesPlugin.FarMeshReadNoRawFlags.Value;

        /// <summary>The source for a prefab; starts its read the first time (null result = not ready yet).</summary>
        internal static FarSource Get(int prefab)
        {
            if (s_sources.TryGetValue(prefab, out var s)) return s.Ready ? s : null;
            s = new FarSource { Prefab = prefab };
            s_sources[prefab] = s;
            s_pending.Enqueue(prefab);
            return null;
        }

        internal static bool IsFailed(int prefab) => s_sources.TryGetValue(prefab, out var s) && s.Failed;

        /// <summary>Starts at most a few reads per frame and caps readbacks in flight.</summary>
        // [Optimize] FarSourceAsync ([saddles] 11-09): the cached far mesh is read, inflated and parsed on the far cache's worker; starts also stop at the frame's far budget
        private static bool AsyncOn => FiresEasyBakeMeshesPlugin.FarSourceAsync != null && FiresEasyBakeMeshesPlugin.FarSourceAsync.Value;
        private static bool s_saidAsync;

        internal static void Step(long deadline = long.MaxValue)
        {
            int started = 0;
            while (s_pending.Count > 0 && started < 4 && s_inFlight < 16 && (started == 0 || !AsyncOn || System.Diagnostics.Stopwatch.GetTimestamp() < deadline))
            {
                int prefab = s_pending.Dequeue();
                if (s_sources.TryGetValue(prefab, out var s)) { Begin(s); started++; }
            }
        }

        private static void Begin(FarSource s)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(s.Prefab) : null;
            if (prefab == null) { s.Failed = true; return; }
            s.Name = prefab.name;
            var picked = PickRenderers(prefab);
            if (picked.Count == 0) { s.Failed = true; return; }
            int body = picked.Count;
            AddCapRenderers(prefab, picked);   // 1.2.262: the healthy cap, after the body (same order in ReadLive and the cache)

            Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
            for (int i = 0; i < picked.Count; i++)
            {
                var mr = picked[i];
                var mats = mr.sharedMaterials;
                var part = new FarPart { Local = rootInv * mr.transform.localToWorldMatrix, Mats = mats, IsCap = i >= body };
                s.Parts.Add(part);
            }
            if (picked.Count > body) CapSources++;

            // Cached arrays for this prefab (per install: game version + mod set + layout version in the key).
            if (AsyncOn && FarCache.Enabled)
            {
                if (!s_saidAsync) { s_saidAsync = true; EasyBakeLog.Info($"{FarBuildings.Log} cached far meshes are read and parsed on the worker thread (FarSourceAsync)."); }
                string key = CacheKey(s);   // Unity and BepInEx reads stay on the main thread
                s_inFlight++;
                FarCache.Queue(() =>
                {
                    bool ok = false;
                    try { ok = FarCache.TryRead(FarCacheScope.Global, "far-meshes", key, out var bytes) && bytes != null && ParseCached(s, bytes); }
                    catch (Exception) { ok = false; }
                    if (!ok) ResetParts(s);
                    return () => { s_inFlight--; if (ok) FinishCacheLoad(s); else ReadLive(s, picked); };
                });
                return;
            }
            if (TryLoadCached(s)) { FinishCacheLoad(s); return; }

            ReadLive(s, picked);
        }

        private static void FinishCacheLoad(FarSource s)
        {
            s.Closed = true;
            s.Parts.RemoveAll(p => p.Failed);
            if (s.Parts.Count == 0) { s.Failed = true; EasyBakeLog.Debug($"{FarBuildings.Log} {s.Name}: recorded as unreadable for this mod set; its meshes are left alone and it is not drawn far away."); return; }
            FarBuildings.S.MeshCached++; Finish(s);
        }

        private static void ReadLive(FarSource s, List<MeshRenderer> picked)
        {
            for (int i = 0; i < picked.Count; i++)
            {
                var part = s.Parts[i];
                var filter = picked[i] != null ? picked[i].GetComponent<MeshFilter>() : null;   // the async path comes back frames later
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) { part.Failed = true; continue; }
                if (mesh.isReadable) { ReadCpu(mesh, part); part.Ready = true; FarBuildings.S.MeshCpu++; }
                else
                {
                    // Only meshes we actually bake (a drawn far piece's picked renderers) are ever read back ([lead] 11-08).
                    FarBuildings.S.MeshGpu++;
                    if (!s_loggedGpu) { s_loggedGpu = true; EasyBakeLog.Info($"{FarBuildings.Log} reading non-readable far meshes once from the GPU (first: {s.Name}); counts in the Timing log line, each is cached. Raw flags on shared meshes: {(NoRawFlags ? "left alone" : "set (FarMeshReadNoRawFlags off)")}."); }
                    ReadGpu(mesh, part, s);
                }
            }
            TryFinish(s);
        }

        private static void TryFinish(FarSource s)
        {
            if (s.Closed) return;   // far fix: a source ends once, so a shortened part list is never saved
            foreach (var p in s.Parts) if (!p.Ready && !p.Failed) return;
            s.Closed = true;
            // 1.2.248 ([generator]): only a source with every part read is saved; a failed part fails for this session and is tried again next launch.
            bool anyFailed = false;
            foreach (var p in s.Parts) if (p.Failed) anyFailed = true;
            if (!anyFailed) SaveCached(s);
            s.Parts.RemoveAll(p => p.Failed);
            if (s.Parts.Count == 0)
            {
                s.Failed = true;
                FarBuildings.S.ReadbackFails++;
                EasyBakeLog.Debug($"{FarBuildings.Log} no far mesh could be read for {s.Name}; it is not drawn far away.");   // [visual] merge: players see warnings; a skipped far piece is not one
                return;
            }
            Finish(s);
        }

        private static void Finish(FarSource s)
        {
            int v = 0; foreach (var p in s.Parts) v += p.Pos.Length;
            s.Vertices = v; s.Ready = true;
            FarBuildingDraw.OnSourceReady(s.Prefab);
        }

        // 1.2.261 ([lead] 11-10; Fire's shot: far copies of buildings white in Summer while "[FarBuildings] started: ... snow off" and "far chunks built
        // with snow 0"): the far look took every renderer ACTIVE IN THE PREFAB, and vanilla's snow cap objects (WearNTear.m_snow / m_snowWorn /
        // m_snowBroken) are active in the prefab (WearNTear.Awake switches them off, UpdateSnowVisual shows them above 0.25 build-up), so every far
        // copy was merged WITH its white cap mesh, whatever the season or the piece's own snow. The near bake already leaves the caps to vanilla
        // (EasyBakeSuppressedVisuals.UnderSnowCap); the far source now skips them the same way, in both branches. Far snow stays the season
        // owner's push (FarSnow): a value on the far shader, never cap geometry.
        internal static int CapsSkipped, CapSources;   // body renderers under a cap (left out of the body); prefabs with a healthy cap kept as IsCap parts
        internal static List<MeshRenderer> PickRenderers(GameObject prefab)
        {
            var result = new List<MeshRenderer>();
            var wnt = prefab.GetComponent<WearNTear>();
            int caps = 0;
            var lodGroup = prefab.GetComponentInChildren<LODGroup>(true);
            if (lodGroup != null)
            {
                var lods = lodGroup.GetLODs();
                if (lods.Length > 0)
                    foreach (var r in lods[lods.Length - 1].renderers)
                    {
                        if (!(r is MeshRenderer mr) || !Usable(mr)) continue;
                        if (UnderCap(mr.transform, wnt)) { caps++; continue; }
                        result.Add(mr);
                    }
                if (result.Count > 0) { NoteCaps(prefab, caps); return result; }
            }
            foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!Usable(mr) || !ActiveInPrefab(mr.transform, prefab.transform)) continue;
                if (wnt != null && (Under(mr.transform, wnt.m_worn) || Under(mr.transform, wnt.m_broken) || Under(mr.transform, wnt.m_wet))) continue;
                if (UnderCap(mr.transform, wnt)) { caps++; continue; }
                result.Add(mr);
            }
            NoteCaps(prefab, caps);
            return result;
        }

        internal static bool UnderCap(Transform t, WearNTear wnt)   // 1.2.262: also ImpostorKinds.Stage
        {
            if (wnt == null) return false;
            return (wnt.m_snow != null && Under(t, wnt.m_snow.gameObject)) || (wnt.m_snowWorn != null && Under(t, wnt.m_snowWorn.gameObject)) || (wnt.m_snowBroken != null && Under(t, wnt.m_snowBroken.gameObject));
        }

        // 1.2.262 (Fire 11-10 2:35 AM: "far building snow cap renders shouldnt be left out, they should be fixed to only show when theres actually snow
        // there"): the body still skips every cap (PickRenderers, as 1.2.261), and the HEALTHY cap (WearNTear.m_snow; far copies always draw the healthy
        // look, so m_snowWorn / m_snowBroken never apply) comes back as its own IsCap parts, appended after the body so ReadLive / the cache keep the
        // picked-to-part order. FarBuildingDraw.Prep includes a copy's cap parts only when that copy has snow (FarPiece.Cap, CapShown).
        private static void NoteCaps(GameObject prefab, int caps)
        {
            if (caps <= 0) return;
            CapsSkipped += caps;
        }

        /// <summary>The healthy snow cap's renderers (vanilla WearNTear.m_snow subtree), usable ones only; empty for a piece without one.</summary>
        internal static void AddCapRenderers(GameObject prefab, List<MeshRenderer> into)
        {
            var wnt = prefab.GetComponent<WearNTear>();
            if (wnt == null || wnt.m_snow == null) return;
            foreach (var mr in wnt.m_snow.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) continue;
                bool ok = true; foreach (var m in mr.sharedMaterials) if (m == null || m.renderQueue > 2500) { ok = false; break; }
                if (ok) into.Add(mr);
            }
        }

        private static bool Usable(MeshRenderer mr)
        {
            if (mr == null || !(mr.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(mr))) return false;
            var mf = mr.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) return false;
            foreach (var m in mr.sharedMaterials) if (m == null || m.renderQueue > 2500) return false; // opaque + cutout only
            return true;
        }

        private static bool ActiveInPrefab(Transform t, Transform root)
        {
            for (; t != null; t = t.parent) { if (!t.gameObject.activeSelf && t != root) return false; if (t == root) return true; }
            return true;
        }

        private static bool Under(Transform t, GameObject state)
        {
            if (state == null) return false;
            for (var s = state.transform; t != null; t = t.parent) if (t == s) return true;
            return false;
        }

        /// <summary>Bounds of the prefab's far renderers in root space (for classification and snow tops).</summary>
        internal static Bounds LocalBounds(GameObject prefab)
        {
            Matrix4x4 rootInv = prefab.transform.worldToLocalMatrix;
            bool any = false; var b = new Bounds();
            foreach (var mr in PickRenderers(prefab))
            {
                var mb = mr.GetComponent<MeshFilter>().sharedMesh.bounds;
                var m = rootInv * mr.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(c, Vector3.zero); any = true; } else b.Encapsulate(c);
                }
            }
            return b;
        }

        // â”€â”€ CPU read (readable meshes) â”€â”€
        private static void ReadCpu(Mesh mesh, FarPart part)
        {
            part.Pos = mesh.vertices;
            part.Nrm = mesh.normals; if (part.Nrm == null || part.Nrm.Length != part.Pos.Length) part.Nrm = new Vector3[part.Pos.Length];
            part.Tan = mesh.tangents; if (part.Tan == null || part.Tan.Length != part.Pos.Length) part.Tan = new Vector4[part.Pos.Length];
            part.Uv = mesh.uv; if (part.Uv == null || part.Uv.Length != part.Pos.Length) part.Uv = new Vector2[part.Pos.Length];
            part.Sub = new int[mesh.subMeshCount][];
            for (int i = 0; i < mesh.subMeshCount; i++)
                part.Sub[i] = mesh.GetTopology(i) == MeshTopology.Triangles ? mesh.GetTriangles(i) : new int[0];
        }

        // â”€â”€ GPU read (non-readable meshes): one async readback of the vertex streams and the index buffer â”€â”€
        private static void ReadGpu(Mesh mesh, FarPart part, FarSource s)
        {
            // 1.2.245: with the validator on, the first read of a mesh waits for a GPU read taken before the Raw flags are set. A body that fails here has no caller left to finish the source.
            if (!NoRawFlags && Indirect.EbmIndirect.RawProbeWanted(mesh)) { Indirect.EbmIndirect.RawProbeBefore(mesh, () => { ReadGpuBody(mesh, part, s); if (part.Failed && s.Parts.Contains(part)) TryFinish(s); }); return; }
            ReadGpuBody(mesh, part, s);
        }

        private static void ReadGpuBody(Mesh mesh, FarPart part, FarSource s)
        {
            // far fix: every buffer is taken BEFORE a readback is counted, so a mesh with a null index buffer leaks no in-flight slot (16 leaks stopped all far reads for the session).
            GraphicsBuffer[] vbs = null; GraphicsBuffer ib = null;
            bool counted = false;
            try
            {
                int vcount = mesh.vertexCount;
                if (!mesh.HasVertexAttribute(VertexAttribute.Position)) { part.Failed = true; return; }
                if (!NoRawFlags)
                {
                    mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                }

                int streams = mesh.vertexBufferCount;
                vbs = new GraphicsBuffer[streams];
                for (int st = 0; st < streams; st++) vbs[st] = mesh.GetVertexBuffer(st);
                ib = mesh.GetIndexBuffer();
                bool have = ib != null;
                foreach (var v in vbs) if (v == null) have = false;
                if (!have) { Release(vbs, ib); part.Failed = true; return; }

                var data = new byte[streams][];
                int left = streams + 1;
                bool failed = false;
                s_inFlight++; counted = true;

                void Done()
                {
                    if (--left > 0) return;
                    s_inFlight--;
                    if (failed) { part.Failed = true; TryFinish(s); return; }
                    try
                    {
                        Decode(mesh, vcount, data, part); part.Ready = true;
                        if (!s_loggedGpuOk) { s_loggedGpuOk = true; EasyBakeLog.Info($"{FarBuildings.Log} first far mesh read from the GPU landed ({s.Name}, {vcount} vertices; Raw flags {(NoRawFlags ? "left alone" : "set")})."); }
                    }
                    catch (Exception e) { part.Failed = true; EasyBakeLog.Debug($"{FarBuildings.Log} {s.Name}: decode failed: {e.Message}"); }
                    TryFinish(s);
                }

                for (int st = 0; st < streams; st++)
                {
                    int stream = st;
                    var gb = vbs[st]; vbs[st] = null;
                    AsyncGPUReadback.Request(gb, req =>
                    {
                        try { if (req.hasError) failed = true; else data[stream] = req.GetData<byte>().ToArray(); }
                        catch (Exception e) { failed = true; EasyBakeLog.Debug($"{FarBuildings.Log} {s.Name}: vertex read failed: {e.Message}"); }
                        gb.Dispose(); Done();
                    });
                }
                var ibLocal = ib; ib = null;
                AsyncGPUReadback.Request(ibLocal, req =>
                {
                    try
                    {
                        if (req.hasError) failed = true;
                        else
                        {
                            var raw = req.GetData<byte>().ToArray();
                            bool is16 = mesh.indexFormat == IndexFormat.UInt16;
                            part.Sub = new int[mesh.subMeshCount][];
                            for (int i = 0; i < mesh.subMeshCount; i++)
                            {
                                var sd = mesh.GetSubMesh(i);
                                if (sd.topology != MeshTopology.Triangles) { part.Sub[i] = new int[0]; continue; }
                                var tri = new int[sd.indexCount];
                                for (int k = 0; k < sd.indexCount; k++)
                                {
                                    int at = sd.indexStart + k;
                                    tri[k] = (is16 ? BitConverter.ToUInt16(raw, at * 2) : (int)BitConverter.ToUInt32(raw, at * 4)) + sd.baseVertex;
                                }
                                part.Sub[i] = tri;
                            }
                        }
                    }
                    catch (Exception e) { failed = true; EasyBakeLog.Debug($"{FarBuildings.Log} {s.Name}: index read failed: {e.Message}"); }   // e.g. the mesh was destroyed while the read was out
                    ibLocal.Dispose(); Done();
                });
            }
            catch (Exception e)
            {
                Release(vbs, ib);
                part.Failed = true;
                if (counted) s_inFlight--;   // a Request threw: left can never reach 0, so Done never gives this back
                EasyBakeLog.Debug($"{FarBuildings.Log} {s.Name}: GPU read not possible: {e.Message}");
            }
        }

        private static void Release(GraphicsBuffer[] vbs, GraphicsBuffer ib)
        {
            if (vbs != null) foreach (var v in vbs) v?.Dispose();
            ib?.Dispose();
        }

        private static void Decode(Mesh mesh, int n, byte[][] data, FarPart part)
        {
            part.Pos = new Vector3[n]; part.Nrm = new Vector3[n]; part.Tan = new Vector4[n]; part.Uv = new Vector2[n];
            ReadAttr(mesh, data, VertexAttribute.Position, n, (i, v) => part.Pos[i] = v);
            if (mesh.HasVertexAttribute(VertexAttribute.Normal)) ReadAttr(mesh, data, VertexAttribute.Normal, n, (i, v) => part.Nrm[i] = v);
            if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) ReadAttr(mesh, data, VertexAttribute.Tangent, n, (i, v) => part.Tan[i] = v);
            if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) ReadAttr(mesh, data, VertexAttribute.TexCoord0, n, (i, v) => part.Uv[i] = v);
        }

        private static void ReadAttr(Mesh mesh, byte[][] data, VertexAttribute a, int n, Action<int, Vector4> set)
        {
            int stream = mesh.GetVertexAttributeStream(a), offset = mesh.GetVertexAttributeOffset(a);
            int stride = mesh.GetVertexBufferStride(stream), dim = mesh.GetVertexAttributeDimension(a);
            var fmt = mesh.GetVertexAttributeFormat(a);
            var buf = data[stream];
            int size = FormatSize(fmt);
            for (int i = 0; i < n; i++)
            {
                int at = i * stride + offset;
                var v = new Vector4();
                for (int c = 0; c < dim && c < 4; c++) v[c] = ReadComponent(buf, at + c * size, fmt);
                set(i, v);
            }
        }

        private static int FormatSize(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: case VertexAttributeFormat.UInt32: case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16: case VertexAttributeFormat.UNorm16: case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16: case VertexAttributeFormat.SInt16: return 2;
                default: return 1;
            }
        }

        private static float ReadComponent(byte[] b, int at, VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return BitConverter.ToSingle(b, at);
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat(BitConverter.ToUInt16(b, at));
                case VertexAttributeFormat.UNorm16: return BitConverter.ToUInt16(b, at) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max(BitConverter.ToInt16(b, at) / 32767f, -1f);
                case VertexAttributeFormat.UNorm8: return b[at] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)b[at] / 127f, -1f);
                case VertexAttributeFormat.UInt16: return BitConverter.ToUInt16(b, at);
                case VertexAttributeFormat.SInt16: return BitConverter.ToInt16(b, at);
                case VertexAttributeFormat.UInt8: return b[at];
                case VertexAttributeFormat.SInt8: return (sbyte)b[at];
                case VertexAttributeFormat.UInt32: return BitConverter.ToUInt32(b, at);
                case VertexAttributeFormat.SInt32: return BitConverter.ToInt32(b, at);
                default: return 0f;
            }
        }

        // â”€â”€ Per-install cache (#4, Global): arrays only; renderers and materials are re-picked (cheap) and matched by count â”€â”€
        private static string CacheKey(FarSource s)
        {
            if (s_cacheTag == null)
            {
                ulong h = 1469598103934665603UL;
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    foreach (char ch in kv.Key + "@" + kv.Value?.Metadata?.Version) { h ^= ch; h *= 1099511628211UL; }
                s_cacheTag = Version.GetVersionString() + "|" + h.ToString("X16");
            }
            return FarCache.Key(s.Name, s.Prefab, s_cacheTag, CacheVersion);
        }

        // far fix: a load that gives up part way leaves no half-filled part behind for the live read
        private static bool TryLoadCached(FarSource s)
        {
            if (LoadCachedParts(s)) return true;
            ResetParts(s);
            return false;
        }

        private static void ResetParts(FarSource s)
        {
            foreach (var p in s.Parts) { p.Ready = p.Failed = false; p.Pos = null; p.Nrm = null; p.Tan = null; p.Uv = null; p.Sub = null; }
        }

        private static bool LoadCachedParts(FarSource s)
        {
            return FarCache.TryRead(FarCacheScope.Global, "far-meshes", CacheKey(s), out var bytes) && bytes != null && ParseCached(s, bytes);
        }

        // plain arrays only: also runs on the far cache's worker (FarSourceAsync), while the source is not Ready and nothing else reads its parts
        private static bool ParseCached(FarSource s, byte[] bytes)
        {
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (r.ReadInt32() != CacheVersion || r.ReadInt32() != s.Parts.Count) return false;
                    foreach (var p in s.Parts)
                    {
                        int n = r.ReadInt32(); if (n <= 0 || n > 1 << 20) return false;
                        p.Pos = new Vector3[n]; p.Nrm = new Vector3[n]; p.Tan = new Vector4[n]; p.Uv = new Vector2[n];
                        for (int i = 0; i < n; i++)
                        {
                            p.Pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            p.Nrm[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            p.Tan[i] = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                            p.Uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                        }
                        int subs = r.ReadInt32(); if (subs < 0 || subs > 64) return false;
                        p.Sub = new int[subs][];
                        for (int k = 0; k < subs; k++)
                        {
                            int c = r.ReadInt32(); if (c < 0 || c > 3 << 20) return false;
                            p.Sub[k] = new int[c];
                            for (int i = 0; i < c; i++) { int ix = r.ReadInt32(); if ((uint)ix >= (uint)n) return false; p.Sub[k][i] = ix; }
                        }
                        p.Ready = true;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static void SaveCached(FarSource s)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CacheVersion); w.Write(s.Parts.Count);
                foreach (var p in s.Parts)
                {
                    w.Write(p.Pos.Length);
                    for (int i = 0; i < p.Pos.Length; i++)
                    {
                        w.Write(p.Pos[i].x); w.Write(p.Pos[i].y); w.Write(p.Pos[i].z);
                        w.Write(p.Nrm[i].x); w.Write(p.Nrm[i].y); w.Write(p.Nrm[i].z);
                        w.Write(p.Tan[i].x); w.Write(p.Tan[i].y); w.Write(p.Tan[i].z); w.Write(p.Tan[i].w);
                        w.Write(p.Uv[i].x); w.Write(p.Uv[i].y);
                    }
                    w.Write(p.Sub.Length);
                    foreach (var sub in p.Sub) { w.Write(sub.Length); foreach (int ix in sub) w.Write(ix); }
                }
                FarCache.Write(FarCacheScope.Global, "far-meshes", CacheKey(s), ms.ToArray());
            }
        }

        internal static void Clear() { s_sources.Clear(); s_pending.Clear(); }
    }
}

