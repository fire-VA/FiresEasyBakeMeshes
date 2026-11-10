using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    // 1.2.238 ([visual] 11-08, Fire: "we just need to narrow down what mesh caused problems and why"). Three tools for the moment the stretch is on screen:
    //   ebm_bisect   hides half of the suspect batches; 'stays' or 'gone' halves again, so a few steps name the batch by eye. Lists go to the log.
    //   ebm_batches  writes every live batch to BepInEx\EbmBatches_<time>.txt, each compared with Unity's mesh as it is now.
    //   ebm_meshhash re-hashes each batch mesh's whole GPU vertex + index data against the baseline taken when the batch was made.
    internal static partial class EbmIndirect
    {
        // ── bisect ──
        private static bool[] s_bisectHidden;
        private static List<int> s_bisectSuspects, s_bisectShown, s_bisectHiddenList;
        private static readonly List<int> s_bisectFound = new List<int>();   // 1.2.239: batches named so far; they stay hidden through ebm_bisect next
        private static bool s_bisectAtFound;

        private static bool BisectHides(Batch b) => s_bisectHidden != null && b.Index < s_bisectHidden.Length && s_bisectHidden[b.Index];

        private static string NameOf(int index) { var b = s_batches[index]; return (b.Mesh != null ? b.Mesh.name : "?") + "#" + b.SubMesh + " x" + b.Count; }

        private static void SplitSuspects(List<string> say)
        {
            int keep = s_bisectSuspects.Count / 2;
            s_bisectShown = s_bisectSuspects.Take(keep).ToList();
            s_bisectHiddenList = s_bisectSuspects.Skip(keep).ToList();
            s_bisectHidden = new bool[s_batches.Count];
            foreach (int i in s_bisectHiddenList) s_bisectHidden[i] = true;
            foreach (int i in s_bisectFound) s_bisectHidden[i] = true;
            s_bisectAtFound = false;
            s_bindingsDirty = true;
            say.Add($"[Bisect] {s_bisectSuspects.Count} suspect batches: {s_bisectHiddenList.Count} hidden, {s_bisectShown.Count} still drawn. Look, then 'ebm_bisect stays' or 'ebm_bisect gone'.");
            EasyBakeLog.Info("[Bisect] hidden: " + string.Join(", ", s_bisectHiddenList.Take(400).Select(NameOf).ToArray()));
            EasyBakeLog.Info("[Bisect] still drawn: " + string.Join(", ", s_bisectShown.Take(400).Select(NameOf).ToArray()));
        }

        internal static IEnumerable<string> BisectCommand(string word)
        {
            var say = new List<string>();
            if (word == "off")
            {
                s_bisectHidden = null; s_bisectSuspects = s_bisectShown = s_bisectHiddenList = null; s_bisectFound.Clear(); s_bisectAtFound = false; s_bindingsDirty = true;
                say.Add("[Bisect] off: everything is drawn again.");
                return say;
            }
            if (word == "start" || word == "next")
            {
                if (word == "start") s_bisectFound.Clear();
                s_bisectSuspects = new List<int>();
                for (int i = 0; i < s_batches.Count; i++) if (s_batches[i].Count > 0 && s_batches[i].Mesh != null && !s_bisectFound.Contains(i)) s_bisectSuspects.Add(i);
                if (s_bisectSuspects.Count == 0) { say.Add("[Bisect] no live batches left: is IndirectRenderer on?"); return say; }
                if (s_bisectFound.Count > 0) say.Add($"[Bisect] {s_bisectFound.Count} found batch(es) stay hidden.");
                SplitSuspects(say);
                return say;
            }
            if (s_bisectSuspects == null) { say.Add("Usage: ebm_bisect start | stays | gone | next | off"); return say; }
            if (s_bisectAtFound && (word == "stays" || word == "gone"))
            {
                say.Add(word == "gone" ? $"[Bisect] Stretch gone with {s_bisectFound.Count} batch(es) hidden: those are all of them. 'ebm_bisect off' draws everything." : "[Bisect] It stays, so more batches are bad: 'ebm_bisect next' searches the rest with the found ones hidden.");
                return say;
            }
            if (word == "stays") s_bisectSuspects = s_bisectShown;
            else if (word == "gone") s_bisectSuspects = s_bisectHiddenList;
            else { say.Add("Usage: ebm_bisect start | stays | gone | next | off"); return say; }
            if (s_bisectSuspects.Count <= 1)
            {
                say.Add(s_bisectSuspects.Count == 1 ? "[Bisect] FOUND: " + DescribeBatch(s_bisectSuspects[0]) : "[Bisect] no suspects left: the answers did not fit one batch (is it more than one?). 'ebm_bisect off' draws everything.");
                if (s_bisectSuspects.Count == 1) s_bisectFound.Add(s_bisectSuspects[0]);
                s_bisectHidden = new bool[s_batches.Count];
                foreach (int i in s_bisectFound) s_bisectHidden[i] = true;
                s_bindingsDirty = true;
                s_bisectAtFound = true;
                say.Add($"[Bisect] {s_bisectFound.Count} found batch(es) hidden now. Look: 'ebm_bisect gone' if the stretch went, 'ebm_bisect stays' if not; 'ebm_bisect next' searches the rest.");
                s_bisectShown = s_bisectHiddenList = new List<int>();
                return say;
            }
            SplitSuspects(say);
            return say;
        }

        private static string DescribeBatch(int i)
        {
            var b = s_batches[i];
            string name = b.Mesh != null ? b.Mesh.name : "(destroyed)";
            return $"'{name}' submesh {b.SubMesh}, material '{(b.Material != null ? b.Material.name : "?")}', {b.Count} instance(s), world bounds centre {b.World.center:F0} size {b.World.size:F0}, largest scale {b.MaxScale:0.###}; {HowBuilt(i)}";
        }

        // ── dump ──
        // 1.2.239: how a batch is made, so a stretched one can be set beside a clean one of the same mesh
        private static readonly string[] s_shaderValues = { "_RippleDistance", "_RippleFreq", "_ValueNoiseVertex", "_Height", "_UV2Height", "_Cutoff", "_MoveableObject" };

        private static string HowBuilt(int i)
        {
            var b = s_batches[i];
            var sb = new StringBuilder();
            int saved = 0, fresh = 0;
            var prefabs = new HashSet<string>();
            foreach (var kv in s_groups)
                foreach (var lane in kv.Value.Lanes)
                {
                    if (lane.Batch != b) continue;
                    if (kv.Key.FromCache) saved += lane.Slots.Count; else fresh += lane.Slots.Count;
                    if (prefabs.Count < 6)
                    {
                        var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(kv.Key.PrefabHash) : null;
                        prefabs.Add((prefab != null ? prefab.name : "prefab " + kv.Key.PrefabHash) + " look " + kv.Key.Look.Bits);
                    }
                }
            sb.Append($"saved {saved}, fresh {fresh}, covered {Mathf.Max(0, b.Count - saved - fresh)}");
            sb.Append("; prefabs ").Append(prefabs.Count > 0 ? string.Join(", ", prefabs.ToArray()) : "(covered: see the object names)");
            sb.Append($"; LOD window {b.StartHeight:0.###} to {b.EndHeight:0.###}, per instance {b.LodPerInstance}; shadows {b.Shadows}, layer {b.Layer}");
            if (b.Mesh != null)
            {
                var sm = b.Mesh.GetSubMesh(Mathf.Clamp(b.SubMesh, 0, Mathf.Max(0, b.Mesh.subMeshCount - 1)));
                sb.Append($"; mesh id {b.Mesh.GetInstanceID()}, {b.Mesh.vertexCount} verts, {sm.indexCount} indices, readable {b.Mesh.isReadable}");
                if (s_mfUnreadable.Contains(b.Mesh.GetInstanceID())) sb.Append(", GPU DATA UNREADABLE");
                sb.Append("; streams");
                foreach (var a in b.Mesh.GetVertexAttributes()) sb.Append($" {a.attribute}:{a.format}x{a.dimension}@{a.stream}");
            }
            if (b.Material != null)
            {
                sb.Append($"; shader {(b.Material.shader != null ? b.Material.shader.name : "?")}, material id {b.Material.GetInstanceID()}");
                foreach (var p in s_shaderValues) if (b.Material.HasProperty(p)) sb.Append($" {p}={b.Material.GetFloat(p):0.####}");
                sb.Append(" keywords ").Append(string.Join(" ", b.Material.shaderKeywords));
            }
            return sb.ToString();
        }

        internal static string DumpBatches()
        {
            var lanes = new Dictionary<int, (int cached, int fresh, HashSet<string> zones)>();
            foreach (var kv in s_groups)
                foreach (var lane in kv.Value.Lanes)
                {
                    if (lane.Batch == null) continue;
                    lanes.TryGetValue(lane.Batch.Index, out var v);
                    if (v.zones == null) v.zones = new HashSet<string>();
                    if (kv.Key.FromCache) v.cached += lane.Slots.Count; else v.fresh += lane.Slots.Count;
                    foreach (var m in lane.Mats) { if (v.zones.Count >= 8) break; var z = EbmZones.Of((Vector3)m.GetColumn(3)); v.zones.Add(z.x + "," + z.y); }
                    lanes[lane.Batch.Index] = v;
                }
            var rows = new List<(float size, bool diff, string text)>();
            int live = 0, differ = 0;
            for (int i = 0; i < s_batches.Count; i++)
            {
                var b = s_batches[i];
                if (b.Count <= 0) continue;
                live++;
                var sb = new StringBuilder();
                sb.Append(b.Mesh != null ? b.Mesh.name : "(destroyed)").Append(" | submesh ").Append(b.SubMesh).Append(" | material ").Append(b.Material != null ? b.Material.name : "?");
                sb.Append(" | instances ").Append(b.Count);
                lanes.TryGetValue(i, out var from);
                sb.Append(" | saved ").Append(from.cached).Append(", fresh ").Append(from.fresh).Append(", covered ").Append(Mathf.Max(0, b.Count - from.cached - from.fresh));
                sb.Append(" | zones ").Append(from.zones != null ? string.Join(" ", from.zones.ToArray()) : "(covered)");
                sb.Append(" | largest scale ").Append(b.MaxScale.ToString("0.###")).Append(" | world centre ").Append(b.World.center.ToString("F0")).Append(" size ").Append(b.World.size.ToString("F0"));
                var why = new List<string>();
                if (b.Mesh != null)
                {
                    int a = i * ArgWords;
                    var sm = b.Mesh.GetSubMesh(Mathf.Clamp(b.SubMesh, 0, Mathf.Max(0, b.Mesh.subMeshCount - 1)));
                    sb.Append(" | verts ").Append(b.Mesh.vertexCount).Append(", indices ").Append(sm.indexCount).Append(", start ").Append(sm.indexStart).Append(", base ").Append(sm.baseVertex).Append(", ").Append(b.Mesh.indexFormat).Append(", readable ").Append(b.Mesh.isReadable);
                    if (b.SubMesh >= b.Mesh.subMeshCount) why.Add("submesh past the mesh's " + b.Mesh.subMeshCount);
                    if (b.VertCount >= 0 && b.VertCount != b.Mesh.vertexCount) why.Add($"verts at layout {b.VertCount}");
                    if (a + 3 < s_args.Length)
                    {
                        if (s_args[a] != (uint)sm.indexCount) why.Add("drawn index count " + s_args[a]);
                        if (s_args[a + 2] != (uint)sm.indexStart) why.Add("drawn index start " + s_args[a + 2]);
                        if (s_args[a + 3] != (uint)sm.baseVertex) why.Add("drawn base vertex " + (int)s_args[a + 3]);
                    }
                    var nowBounds = sm.bounds.size == Vector3.zero ? b.Mesh.bounds : sm.bounds;
                    if ((nowBounds.center - b.LocalBounds.center).sqrMagnitude > 1e-4f || (nowBounds.size - b.LocalBounds.size).sqrMagnitude > 1e-4f) why.Add($"bounds at batch creation {b.LocalBounds.center:F2} size {b.LocalBounds.size:F2}, now {nowBounds.center:F2} size {nowBounds.size:F2}");
                }
                else why.Add("mesh destroyed");
                sb.Append(" | ").Append(HowBuilt(i));
                if (why.Count > 0) { differ++; sb.Append(" | DIFFERS FROM UNITY: ").Append(string.Join("; ", why.ToArray())); }
                rows.Add((b.World.size.magnitude, why.Count > 0, sb.ToString()));
            }
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "EbmBatches_" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllLines(path, rows.OrderByDescending(r => r.diff).ThenByDescending(r => r.size).Select(r => r.text).ToArray());
            return $"[Batches] {live} live batches written to {path}; {differ} differ from Unity's mesh now (listed first), then largest world bounds first.";
        }

        // ── whole-mesh hash ──
        private const int MaxHashBytes = 32 * 1024 * 1024;
        private static readonly Dictionary<int, ulong> s_mfBase = new Dictionary<int, ulong>();
        private static readonly Queue<Mesh> s_mfQueue = new Queue<Mesh>();
        private static readonly HashSet<int> s_mfQueued = new HashSet<int>(), s_mfUnreadable = new HashSet<int>();
        private static readonly List<string> s_mfChanged = new List<string>();
        private static bool s_mfBusy, s_mfReport;
        private static float s_mfSince;
        private static int s_mfCompared, s_mfBaselined, s_mfGeneration;

        private static void MeshFullQueue(Mesh mesh)
        {
            if (mesh != null && MeshContentOn && s_mfQueued.Add(mesh.GetInstanceID())) s_mfQueue.Enqueue(mesh);
        }

        internal static string MeshHashCommand(bool rebase)
        {
            if (!MeshContentOn) return "[MeshHash] [Diagnostics] IndirectMeshContentCheck is off.";
            if (rebase) s_mfBase.Clear();
            s_mfChanged.Clear(); s_mfCompared = s_mfBaselined = 0; s_mfUnreadable.Clear(); s_mfReport = true;
            foreach (var b in s_batches) if (b.Count > 0) MeshFullQueue(b.Mesh);
            return $"[MeshHash] {s_mfQueue.Count} mesh(es) queued, one a frame; the result is one [MeshHash] line in the log.";
        }

        private static void MeshFullTick()
        {
            if (!MeshContentOn) return;
            if (s_mfBusy)
            {
                if (Time.unscaledTime - s_mfSince < 10f) return;
                s_mfBusy = false; s_mfGeneration++;   // the buffers go with their own callbacks, if those ever land
            }
            if (s_mfQueue.Count == 0)
            {
                if (s_mfReport)
                {
                    s_mfReport = false;
                    EasyBakeLog.Info($"[MeshHash] done: {s_mfCompared} compared with their baseline, {s_mfBaselined} first seen now, {s_mfChanged.Count} CHANGED, {s_mfUnreadable.Count} could not be read"
                        + (s_mfChanged.Count > 0 ? ": " + string.Join(", ", s_mfChanged.Take(60).ToArray()) : ""));
                }
                return;
            }
            var mesh = s_mfQueue.Dequeue();
            if (mesh == null) return;
            int id = mesh.GetInstanceID();
            s_mfQueued.Remove(id);
            GraphicsBuffer v = null, ix = null;
            try
            {
                v = mesh.GetVertexBuffer(0); ix = mesh.GetIndexBuffer();
                int vBytes = v != null ? (v.count * v.stride) & ~3 : 0, iBytes = ix != null ? (ix.count * ix.stride) & ~3 : 0;
                if (vBytes <= 0 || iBytes <= 0 || vBytes > MaxHashBytes || iBytes > MaxHashBytes) { v?.Dispose(); ix?.Dispose(); s_mfUnreadable.Add(id); return; }
                var vBuf = v; var iBuf = ix; string name = mesh.name; int generation = s_mfGeneration;
                ulong hV = 0, hI = 0; int left = 2; bool failed = false;
                void Landed()
                {
                    if (--left > 0) return;
                    vBuf.Dispose(); iBuf.Dispose();
                    if (generation != s_mfGeneration) return;
                    s_mfBusy = false;
                    if (failed) { s_mfUnreadable.Add(id); return; }
                    ulong h = (hV * 1099511628211UL) ^ hI;
                    if (s_mfBase.TryGetValue(id, out ulong first))
                    {
                        s_mfCompared++;
                        if (first != h)
                        {
                            if (s_mfChanged.Count < 200) s_mfChanged.Add(name);
                            EasyBakeLog.Error($"[MeshHash] '{name}': its whole GPU vertex / index data differs from the baseline taken when its batch was made");
                        }
                    }
                    else { s_mfBase[id] = h; s_mfBaselined++; }
                }
                ulong Hash(NativeArray<uint> d) { ulong x = 1469598103934665603UL; for (int k = 0; k < d.Length; k++) x = (x ^ d[k]) * 1099511628211UL; return x; }
                s_mfBusy = true; s_mfSince = Time.unscaledTime;
                AsyncGPUReadback.Request(vBuf, vBytes, 0, r => { if (r.hasError) failed = true; else hV = Hash(r.GetData<uint>()); Landed(); });
                AsyncGPUReadback.Request(iBuf, iBytes, 0, r => { if (r.hasError) failed = true; else hI = Hash(r.GetData<uint>()); Landed(); });
            }
            catch (Exception e)
            {
                v?.Dispose(); ix?.Dispose(); s_mfBusy = false; s_mfUnreadable.Add(id);
                EasyBakeLog.Debug($"[MeshHash] skipped '{mesh.name}' id {id}, {mesh.vertexCount} verts, {mesh.GetIndexCount(0)} indices, {mesh.subMeshCount} submesh(es), readable {mesh.isReadable}: {e.Message}");
            }
        }
    }
}
