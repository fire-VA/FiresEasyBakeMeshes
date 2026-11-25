using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>
    /// Registered kinds: what to bake for each prefab (its LOD0 renderers, read from the prefab asset WITHOUT instantiating it, so no
    /// Awake / OnEnable of any mod ever runs: the EBM 1.2.189 prewarm lesson), its bounds, its mid-LOD parts, its material signature (a
    /// change = a background re-bake, the old look keeps drawing), and the bake queue (nearest-needed first, one kind at a time).
    /// </summary>
    internal static class ImpostorKinds
    {
        internal const int Views = 8;   // per side: 64 views

        internal enum State { Queued, Baking, Ready, Refused }

        internal sealed class Part { public Mesh Mesh; public Material[] Materials; public Matrix4x4 ToRoot; }
        internal sealed class MidPart
        {
            public Mesh Mesh; public int SubMesh; public Material Material; public Matrix4x4 ToRoot;
            // 1.2.488 ([visual], Fire 11-21 white "egg" blobs; [lead] / [2ic]): the far-LOD renderer's own material the copy follows, and what was copied from it.
            // Off = its source has no live main texture: the part is not drawn and its kind takes no mid segment, so that band falls back to the impostor card.
            public Material Source; public Texture CopiedTex; public Color CopiedColor; public float CopiedCutoff = -1f; public bool Off;
        }

        internal sealed class Kind
        {
            public int Id; public string Owner, Name; public GameObject Prefab; public KindOptions Options;
            public List<Part> Parts = new List<Part>(); public List<MidPart> Mid = new List<MidPart>();
            public Bounds Bounds; public float Radius;
            public string Signature; public State State; public string Why;
            public bool HasSlice;                 // a baked look is in the drawing atlas slice Id
            public bool Dirty;                    // re-bake wanted (signature / sharpness)
            public float[] Cutoffs = DefaultCutoffs(); public float[] Areas;
            public float NeedDistance = float.MaxValue;   // nearest instance seen (bake order)
        }

        private static readonly List<Kind> s_kinds = new List<Kind>();
        private static readonly Dictionary<GameObject, int> s_byPrefab = new Dictionary<GameObject, int>();
        internal static int LookRevision;
        private static float s_nextSig;

        internal static int Count => s_kinds.Count;
        internal static Kind Get(int id) => id >= 0 && id < s_kinds.Count ? s_kinds[id] : null;
        internal static IReadOnlyList<Kind> All => s_kinds;

        internal static float[] DefaultCutoffs() => new[] { 0.5f, 0.5f, 0.45f, 0.4f, 0.35f, 0.3f, 0.3f, 0.3f };

        internal static int Register(string owner, GameObject prefab, KindOptions options)
        {
            if (prefab == null) { EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind refused for {owner}: no prefab"); return -1; }
            if (s_byPrefab.TryGetValue(prefab, out int existing)) return existing;
            if (prefab.GetComponentInChildren<Character>(true) != null) { EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {prefab.name} refused: a character"); return -1; }
            if (s_kinds.Count >= 255) { EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {prefab.name} refused: 255 kinds already"); return -1; }

            var k = new Kind { Id = s_kinds.Count, Owner = owner ?? "?", Name = prefab.name, Prefab = prefab, Options = options };
            if (k.Options.ReachScale < 0f) k.Options.ReachScale = 1f;   // 0 = reach does not grow with scale
            string why = Stage(k);
            if (why != null) { EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {prefab.name} refused: {why}"); return -1; }
            k.Signature = SignatureOf(k);
            k.State = State.Queued;
            s_kinds.Add(k); s_byPrefab[prefab] = k.Id;
            ImpostorDraw.KindsChanged();
            EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {k.Id} {k.Name} from {k.Owner}: {k.Parts.Count} part(s), radius {k.Radius:0.0} m, mid parts {k.Mid.Count}, queued to bake");
            return k.Id;
        }

        internal static void Forget(int id)
        {
            var k = Get(id); if (k == null) return;
            k.State = State.Refused; k.Why = "forgotten"; k.HasSlice = false;
            ImpostorDraw.KindsChanged();
        }

        internal static KindInfo Info(int id)
        {
            var k = Get(id);
            if (k == null) return default;
            return new KindInfo { Baked = k.HasSlice, Prefab = k.Name, Bounds = k.Bounds, Reach = ReachOf(k), SilhouetteArea = k.Areas };
        }

        internal static float ReachOf(Kind k) => k.Options.MaxDistance > 0f ? Mathf.Min(k.Options.MaxDistance, ImpostorEngine.Distance) : ImpostorEngine.Distance;

        // what the bake draws: LOD0 renderers of the prefab asset, as (mesh, live materials, renderer -> root)
        private static string Stage(Kind k)
        {
            var root = k.Prefab.transform; var toRoot = root.worldToLocalMatrix;
            var lod = k.Prefab.GetComponentInChildren<LODGroup>(true);
            Renderer[] rs = null; Renderer[] last = null;
            if (lod != null)
            {
                var lods = lod.GetLODs();
                if (lods.Length > 0) rs = lods[0].renderers;
                if (lods.Length > 1) last = lods[lods.Length - 1].renderers;
            }
            if (rs == null || rs.Length == 0) rs = k.Prefab.GetComponentsInChildren<MeshRenderer>(false);
            // 1.2.262 ([lead] 11-10, after 1.2.261 far caps): a kind that carries a WearNTear (VA prefabs, FAP props) has vanilla's snow caps ACTIVE in
            // the prefab (WearNTear.Awake hides them on instances); one baked into the impostor card is a white top for the whole season. Skipped here
            // and in the mid-LOD parts with the far source's own test. One Info line the first time, so we learn whether a building kind gets here.
            var capWnt = k.Prefab.GetComponent<WearNTear>();
            bool any = false; var b = new Bounds();
            foreach (var r in rs)
            {
                if (r == null || !(r is MeshRenderer) || !(r.enabled || FiresEasyBakeMeshes.EasyBake.Indirect.Coverage.CoveredObject.HiddenByCoverage(r))) continue;
                if (capWnt != null && FarBuildingSources.UnderCap(r.transform, capWnt)) { NoteCapSkip(k); continue; }
                var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue;
                var mats = r.sharedMaterials;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    if (m.renderQueue >= 2500) return $"material {m.name} is transparent (queue {m.renderQueue})";
                }
                var part = new Part { Mesh = mf.sharedMesh, Materials = mats, ToRoot = toRoot * r.transform.localToWorldMatrix };
                k.Parts.Add(part);
                var mb = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var p = part.ToRoot.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)));
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            if (!any) return "no mesh renderers in its first LOD";
            if (b.size.y < 0.5f) return $"too flat ({b.size.y:0.00} m)";
            k.Bounds = b; k.Radius = b.extents.magnitude;

            // mid-LOD parts: the authored far LOD (the LODGroup's last), else none
            if (k.Options.MidLodSource != MidLodSource.None && last != null && ImpostorAssets.Mid != null)
            {
                foreach (var r in last)
                {
                    if (r == null || !(r is MeshRenderer)) continue;
                    if (capWnt != null && FarBuildingSources.UnderCap(r.transform, capWnt)) { NoteCapSkip(k); continue; }
                    var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue;
                    var mats = r.sharedMaterials;
                    for (int s = 0; s < mf.sharedMesh.subMeshCount && s < mats.Length; s++)
                    {
                        var src = mats[s]; if (src == null) continue;
                        var m = new Material(ImpostorAssets.Mid) { name = "FarMid_" + src.name, hideFlags = HideFlags.HideAndDontSave };
                        var part = new MidPart { Mesh = mf.sharedMesh, SubMesh = s, Material = m, ToRoot = toRoot * r.transform.localToWorldMatrix, Source = src };
                        CopyMid(k, part, "registered");   // 1.2.488: one copy method, also used when the source changes later
                        k.Mid.Add(part);
                    }
                }
            }
            return null;
        }

        internal static int CapsSkipped;
        private static bool s_saidCap;
        private static void NoteCapSkip(Kind k)
        {
            CapsSkipped++;
            if (s_saidCap) return;
            s_saidCap = true;
            EasyBakeLog.Info($"[Impostors] a snow cap renderer of {k.Name} was left out of its impostor card (a building-type kind reached the impostor bake; 1.2.262)");
        }

        // what the bake depends on: meshes, shaders, textures, colours (a season mod swapping a texture or colour changes it)
        internal static string SignatureOf(Kind k)
        {
            var sb = new StringBuilder(k.Name);
            foreach (var p in k.Parts)
            {
                sb.Append('|').Append(p.Mesh.name).Append(':').Append(p.Mesh.vertexCount);
                foreach (var m in p.Materials)
                {
                    if (m == null) { sb.Append("|-"); continue; }
                    sb.Append('|').Append(m.shader != null ? m.shader.name : "?");
                    var t = m.mainTexture; sb.Append(':').Append(t != null ? t.name + "#" + t.GetInstanceID() : "-");
                    if (m.HasProperty("_Color")) { var c = m.GetColor("_Color"); sb.Append(':').Append(Mathf.RoundToInt(c.r * 255)).Append(',').Append(Mathf.RoundToInt(c.g * 255)).Append(',').Append(Mathf.RoundToInt(c.b * 255)); }
                }
            }
            return sb.ToString();
        }

        // 1.2.478 ([visual], [lead] 11-20; rig profiles prof_pfbfixed77_*: 11 impostor atlases / 8.6 MB re-baked under new names on some warm launches):
        // the DISK cache key reads a texture by a launch-stable identity (name, size, format, mip count). SignatureOf above keeps the instance ID: it only
        // compares within one session (PollSignatures). An identity that two different textures share is counted and named (they would share a cache file).
        // 1.2.478 ([2ic] review M1, [lead] "winter re-baked once, then cached"): a texture MADE AT RUNTIME (a season mod's variant: Vedr names every season
        // "<source>_vedr", TextureFactory.cs:330, sets _Color white, and changes a season only by SetTexture(_MainTex) + _Color, SeasonalFoliage.cs:12/:337/:381,
        // so autumn / winter / spring share name + size + format) is keyed by its CONTENT: a 64x64 GPU downsample read back once per texture per session
        // (RuntimeHash). Until that hash is back the kind waits (pending = true); a failed or unsupported readback = no disk cache for that kind (null), it
        // bakes as before. Runtime objects are told by a negative instance ID, CHECKED once against a texture EBM makes itself; if this Unity build does not
        // give runtime objects negative IDs, the 1.2.477 key (SignatureOf, instance IDs) is used for every kind.
        internal static string DiskSignatureOf(Kind k, out bool pending)
        {
            pending = false;
            if (!RuntimeIdsNegative) return SignatureOf(k);
            bool failed = false;
            foreach (var p in k.Parts) foreach (var m in p.Materials)
            {
                var tt = m != null ? m.mainTexture : null;
                if (tt == null || tt.GetInstanceID() >= 0) continue;
                int st = RuntimeHashState(tt);
                if (st == 0) pending = true; else if (st < 0) failed = true;
            }
            if (failed) return null;
            if (pending) return null;
            var sb = new StringBuilder(k.Name);
            foreach (var p in k.Parts)
            {
                sb.Append('|').Append(p.Mesh.name).Append(':').Append(p.Mesh.vertexCount);
                foreach (var m in p.Materials)
                {
                    if (m == null) { sb.Append("|-"); continue; }
                    sb.Append('|').Append(m.shader != null ? m.shader.name : "?");
                    var t = m.mainTexture; sb.Append(':').Append(t == null ? "-" : t.GetInstanceID() < 0 ? "rt:" + t.name + "#" + t.width + "x" + t.height + ":" + s_rtHash[t.GetInstanceID()] : TextureIdentity(t, k.Name));
                    if (m.HasProperty("_Color")) { var c = m.GetColor("_Color"); sb.Append(':').Append(Mathf.RoundToInt(c.r * 255)).Append(',').Append(Mathf.RoundToInt(c.g * 255)).Append(',').Append(Mathf.RoundToInt(c.b * 255)); }
                }
            }
            return sb.ToString();
        }

        // ---- 1.2.478: content hash of a runtime-made texture (once per texture per session, main thread start, GPU readback callback)
        private const int RtHashSize = 64;
        private const int RtGiveUpFrames = 300;

        /// <summary>[2ic] SHOULD: while the nearest kind waits for a content hash, start the readbacks of every other queued kind's runtime-made textures
        /// too, so after a season flip they come back together instead of one kind after another.</summary>
        internal static void RequestQueuedRuntimeHashes()
        {
            if (!RuntimeIdsNegative) return;
            foreach (var k in s_kinds)
            {
                if (k.State != State.Queued) continue;
                foreach (var p in k.Parts) foreach (var m in p.Materials)
                {
                    var tt = m != null ? m.mainTexture : null;
                    if (tt != null && tt.GetInstanceID() < 0) RuntimeHashState(tt);
                }
            }
        }
        private static readonly Dictionary<int, string> s_rtHash = new Dictionary<int, string>();   // instance ID -> FNV-1a 64 hex of the 64x64 RGBA32 downsample; null = failed
        private static readonly HashSet<int> s_rtPending = new HashSet<int>();
        internal static int RtHashed, RtFailed; internal static double RtStartMs, RtWorstFrames; private static readonly Dictionary<int, int> s_rtAskedAt = new Dictionary<int, int>();
        /// <summary>1 = hashed, 0 = waiting (asked now or earlier), -1 = cannot hash (no readback support / readback error).</summary>
        private static int RuntimeHashState(Texture t)
        {
            int id = t.GetInstanceID();
            if (s_rtHash.TryGetValue(id, out var h)) return h != null ? 1 : -1;
            if (s_rtPending.Contains(id))
            {
                // [2ic] NOTE (a): a readback whose callback never comes (device loss) gives up after RtGiveUpFrames: no disk for its kinds, no endless waits
                if (s_rtAskedAt.TryGetValue(id, out int asked) && Time.frameCount - asked > RtGiveUpFrames) { s_rtPending.Remove(id); s_rtAskedAt.Remove(id); s_rtHash[id] = null; RtFailed++; return -1; }
                return 0;
            }
            if (!SystemInfo.supportsAsyncGPUReadback) { s_rtHash[id] = null; RtFailed++; return -1; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var rt = RenderTexture.GetTemporary(RtHashSize, RtHashSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var prevActive = RenderTexture.active;
                Graphics.Blit(t, rt);
                RenderTexture.active = prevActive;   // Blit leaves its target active
                s_rtPending.Add(id); s_rtAskedAt[id] = Time.frameCount;
                UnityEngine.Rendering.AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, req =>
                {
                    s_rtPending.Remove(id);
                    if (req.hasError) { s_rtHash[id] = null; RtFailed++; }
                    else
                    {
                        var d = req.GetData<byte>(); ulong fh = 14695981039346656037UL;
                        for (int i = 0; i < d.Length; i++) { fh ^= d[i]; fh *= 1099511628211UL; }
                        s_rtHash[id] = fh.ToString("X16"); RtHashed++;
                    }
                    if (s_rtAskedAt.TryGetValue(id, out int at)) { RtWorstFrames = Math.Max(RtWorstFrames, Time.frameCount - at); s_rtAskedAt.Remove(id); }
                    RenderTexture.ReleaseTemporary(rt);
                });
            }
            catch (Exception e) { s_rtPending.Remove(id); s_rtHash[id] = null; RtFailed++; RenderTexture.ReleaseTemporary(rt); EasyBakeLog.Debug(ImpostorEngine.Tag + $"runtime texture {t.name}: content hash failed ({e.GetType().Name}: {e.Message}); its kinds are not disk-cached"); return -1; }
            RtStartMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return 0;
        }

        private static int s_runtimeIdSign;   // 0 = not checked, 1 = a runtime-made texture has a negative instance ID, -1 = it does not
        internal static bool RuntimeIdsNegative
        {
            get
            {
                if (s_runtimeIdSign == 0)
                {
                    var probe = new Texture2D(1, 1); int id = probe.GetInstanceID(); UnityEngine.Object.Destroy(probe);
                    s_runtimeIdSign = id < 0 ? 1 : -1;
                    EasyBakeLog.Info(ImpostorEngine.Tag + (id < 0 ? "disk key (1.2.478): runtime-made textures have negative instance IDs here; they are keyed by a content hash (64x64 GPU readback, once per texture), every other texture by name / size / format / mips"
                                                                    : $"disk key (1.2.478): a runtime-made texture got instance ID {id} (not negative): every kind keeps the 1.2.477 key (instance IDs)"));
                }
                return s_runtimeIdSign == 1;
            }
        }

        private static readonly Dictionary<string, int> s_texIdentity = new Dictionary<string, int>();   // identity -> the instance ID first seen (int.MinValue = shared)
        internal static int DiskTextures => s_texIdentity.Count;
        internal static int DiskCollisions;
        private static string TextureIdentity(Texture t, string kind)
        {
            string id = t.name + "#" + t.width + "x" + t.height + ":" + t.graphicsFormat + ":" + t.mipmapCount;
            int iid = t.GetInstanceID();
            if (!s_texIdentity.TryGetValue(id, out int seen)) s_texIdentity[id] = iid;
            else if (seen != iid && seen != int.MinValue)
            {
                s_texIdentity[id] = int.MinValue; DiskCollisions++;
                if (DiskCollisions <= 10) EasyBakeLog.Info(ImpostorEngine.Tag + $"disk key: two different textures share the identity {id} (kind {kind}); their kinds share one cache file (1.2.478)");
            }
            return id;
        }

        // ---- 1.2.488 mid-LOD follows its source ([visual]; Fire 11-21 white blobs; [2ic]: re-copy on the source's own change, texture + colour + cutoff together) ----
        // The mid material is a copy on the impostor-mid shader of the far-LOD renderer's own material. 1.2.487 and before copied it ONCE at registration, so a
        // season mod's later repaint (Vedr SeasonalFoliage swaps _MainTex at runtime and sets _Color white) left the copy behind; a copied texture that was then
        // destroyed samples as WHITE. Now each part keeps its Source and what it copied; a part whose source's texture / colour / cutoff reference changed is copied
        // again (the same 2 s pass that already watches the cards' look, no new poll), so the mid matches the live tree in every season.
        internal static int MidRecopies, MidOffEver; private static bool s_midOffSaid;
        internal static int MidOffNow { get { int n = 0; foreach (var k in s_kinds) foreach (var p in k.Mid) if (p.Off) n++; return n; } }
        private static void CopyMid(Kind k, MidPart p, string why)
        {
            var src = p.Source; var m = p.Material;
            Texture tex = src != null ? src.mainTexture : null;
            bool wasOff = p.Off;
            Texture before = p.CopiedTex;
            bool beforeDestroyed = !ReferenceEquals(before, null) && before == null;   // Unity: a destroyed object compares equal to null while the reference is kept
            p.Off = src == null || m == null || tex == null;
            // [2ic]: colour / cutoff are RECORDED from the source even while Off (only applied when drawn), so an unchanged dark part is not re-copied every 2 s
            if (src != null && src.HasProperty("_Color")) p.CopiedColor = src.GetColor("_Color");
            if (src != null && src.HasProperty("_Cutoff")) p.CopiedCutoff = src.GetFloat("_Cutoff");
            if (!p.Off)
            {
                m.mainTexture = tex; m.mainTextureScale = src.mainTextureScale; m.mainTextureOffset = src.mainTextureOffset;
                if (src.HasProperty("_Color")) m.SetColor("_Color", p.CopiedColor);
                if (src.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", p.CopiedCutoff);
            }
            p.CopiedTex = tex;
            if (p.Off != wasOff || why == "registered")
            {
                ImpostorPool.CountsChanged = true;   // ImpostorDraw.MidSegments rebuilds: a kind with no live mid part takes no mid segment (its band draws the card)
                if (p.Off && !wasOff)
                {
                    MidOffEver++;
                    // [2ic]: a safety net, not a hiding place: in steady state this is a function loss (no mid LOD for that part), so it is said once at Info
                    if (!s_midOffSaid) { s_midOffSaid = true; EasyBakeLog.Info(ImpostorEngine.Tag + $"mid-LOD part {k.Name}/{(src != null ? src.name : "?")} has no live texture, so it is drawn as the impostor card instead of white (once per session; please report)"); }
                }
            }
            // (c) one Debug line per copy: the texture copied (name / id), whether the previous copy was a DESTROYED texture, the source's current texture id, the colour
            if (EasyBakeLog.DebugOn())
                EasyBakeLog.Debug(ImpostorEngine.Tag + $"mid part {k.Name}/{(src != null ? src.name : "?")} {why}: texture {(tex != null ? tex.name + " #" + tex.GetInstanceID() : "NONE")}"
                    + $" (previous copy {(ReferenceEquals(before, null) ? "none" : beforeDestroyed ? "a DESTROYED texture (drew white)" : before.name + " #" + before.GetInstanceID())}),"
                    + $" colour {p.CopiedColor}, cutoff {p.CopiedCutoff:0.##}{(p.Off ? "; NOT DRAWN (no live source texture): this band shows the impostor card" : "")}");
        }

        // true = the source's look moved since the copy (a texture swapped or destroyed, a colour or cutoff changed)
        private static bool MidStale(MidPart p)
        {
            var src = p.Source;
            if (src == null) return !p.Off;
            Texture tex = src.mainTexture;
            if (tex != p.CopiedTex || (tex == null) != p.Off) return true;   // [2ic]: Unity's != = native identity (no managed-wrapper risk)
            if (!ReferenceEquals(p.CopiedTex, null) && p.CopiedTex == null) return true;   // the copied texture was destroyed
            if (src.HasProperty("_Color") && src.GetColor("_Color") != p.CopiedColor) return true;
            return src.HasProperty("_Cutoff") && !Mathf.Approximately(src.GetFloat("_Cutoff"), p.CopiedCutoff);
        }

        /// <summary>Every 2 s: a kind whose look changed is queued for a background re-bake (Vedr's season, another mod).</summary>
        internal static void PollSignatures()
        {
            if (Time.unscaledTime < s_nextSig) return;
            s_nextSig = Time.unscaledTime + 2f;
            foreach (var k in s_kinds)
                foreach (var p in k.Mid)
                    if (MidStale(p)) { CopyMid(k, p, "source changed"); MidRecopies++; }   // 1.2.488: before the card check (independent of the card's bake state)
            foreach (var k in s_kinds)
            {
                if (k.State != State.Ready) continue;
                string sig = SignatureOf(k);
                if (sig == k.Signature) continue;
                k.Signature = sig; k.Dirty = true; k.State = State.Queued;
                EasyBakeLog.Debug(ImpostorEngine.Tag + $"kind {k.Name}: its look changed (material signature), re-baking in the background; the old look draws until then");
            }
        }

        /// <summary>The next kind to bake: queued, nearest-needed first.</summary>
        internal static Kind NextToBake()
        {
            Kind best = null;
            foreach (var k in s_kinds) if (k.State == State.Queued && (best == null || k.NeedDistance < best.NeedDistance)) best = k;
            return best;
        }

        internal static void MarkAllDirty(string why)
        {
            int n = 0;
            foreach (var k in s_kinds) if (k.State == State.Ready || k.State == State.Baking) { k.Dirty = true; k.State = State.Queued; n++; }
            if (n > 0) EasyBakeLog.Debug(ImpostorEngine.Tag + $"{n} kind(s) re-bake: {why}");
        }

        internal static void Clear()
        {
            foreach (var k in s_kinds) foreach (var m in k.Mid) if (m.Material != null) UnityEngine.Object.Destroy(m.Material);
            s_kinds.Clear(); s_byPrefab.Clear();
        }

        internal static string StatusText()
        {
            int ready = 0, queued = 0, refused = 0;
            foreach (var k in s_kinds) { if (k.HasSlice) ready++; if (k.State == State.Queued || k.State == State.Baking) queued++; if (k.State == State.Refused) refused++; }
            return $"kinds {s_kinds.Count} ({ready} baked, {queued} to bake{(refused > 0 ? ", " + refused + " off" : "")})";
        }
    }
}
