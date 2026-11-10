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
        internal sealed class MidPart { public Mesh Mesh; public int SubMesh; public Material Material; public Matrix4x4 ToRoot; }

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
                        if (src.mainTexture != null) { m.mainTexture = src.mainTexture; m.mainTextureScale = src.mainTextureScale; m.mainTextureOffset = src.mainTextureOffset; }
                        if (src.HasProperty("_Color")) m.SetColor("_Color", src.GetColor("_Color"));
                        if (src.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", src.GetFloat("_Cutoff"));
                        k.Mid.Add(new MidPart { Mesh = mf.sharedMesh, SubMesh = s, Material = m, ToRoot = toRoot * r.transform.localToWorldMatrix });
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

        /// <summary>Every 2 s: a kind whose look changed is queued for a background re-bake (Vedr's season, another mod).</summary>
        internal static void PollSignatures()
        {
            if (Time.unscaledTime < s_nextSig) return;
            s_nextSig = Time.unscaledTime + 2f;
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
