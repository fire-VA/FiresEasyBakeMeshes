using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// 1.2.272 [Optimize] CoverageRegistryAttach (lead 11-10 10 PM; FDT 1.1.1181: EBM's ZNetView.Awake postfix 5.91 ms/s during load).
    /// The old path added a CoveredObject component inside ZNetView.Awake to every tree / rock / pickable / destructible that loads, a Unity
    /// AddComponent per object on the load-critical path, including the many objects past their own last LOD level that nothing draws.
    /// With the key on, Awake only REGISTERS the object (ZDO id -> view, its per-prefab verdict, its reach) after the cached per-prefab
    /// refusal check; no component. A late-frame pass (EBM's LateUpdate, beside CoverageBatchedStart's flush) attaches the CoveredObject
    /// only to entries that are still alive and the camera can reach: within the object's last LOD end distance x 1.25 + 96 m (more than
    /// a 64 m zone cell of slack, since waiting entries are re-checked when the camera crosses a cell). Until it is attached, an object is
    /// drawn by Unity exactly as without EBM, so nothing is ever unseen; attaching only changes who draws it. Objects with no LOD end
    /// (no LODGroup, or a never-culled level) are attached on the first pass, as vanilla draws those at any distance.
    ///
    /// Registry = only objects NOT attached yet: an attached object leaves it (its CoveredObject owns it from then on). Bounded by live
    /// views: removed on ZNetScene.Destroy (ZNetScene_Destroy_Patch), dropped when Unity already destroyed it (zone unload uses
    /// Object.Destroy directly) at its next check and by the 30 s sweep, cleared at ZNetScene.Shutdown. Waiting entries are filed by the
    /// same 64 m XZ cell as CoverageReach, so a camera move re-checks only the cells within the largest waiting reach.
    /// Idle = free: nothing new and the camera still in the same cell is one compare and a return (no clock read).
    /// Objects born with prefab parts switched off (PrefabCoverage) keep the old immediate path: they must never reach a drawn frame uncovered.
    /// [Optimize] CoverageRegistryVerify (default off): for the first 2,000 attaches, the verdict is judged again from scratch and compared with
    /// what the old Awake path used; mismatches counted. Debug census every 30 s.
    /// </summary>
    internal static class CoverageRegistry
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.CoverageRegistryAttach != null && FiresEasyBakeMeshesPlugin.CoverageRegistryAttach.Value;
        private static bool VerifyOn => FiresEasyBakeMeshesPlugin.CoverageRegistryVerify != null && FiresEasyBakeMeshesPlugin.CoverageRegistryVerify.Value;

        private const float CellSize = 64f, ReachFactor = 1.25f, ReachSlack = 96f;
        private const int NewPerPassCap = 512, VerifyMax = 2000;
        private const double PassBudgetMs = 2.0;

        private struct Entry
        {
            public ZNetView View; public CoverageRules.Verdict Verdict; public int Prefab;
            public float Units;          // last LOD end in LOD units (x camera factor = metres); +inf = attach at once
            public Vector3 Pos; public long Cell; public int Frame; public bool Filed;
        }

        private static readonly Dictionary<ZDOID, Entry> s_entries = new Dictionary<ZDOID, Entry>();
        private static readonly List<ZDOID> s_new = new List<ZDOID>(256);
        private static readonly Dictionary<long, List<ZDOID>> s_cells = new Dictionary<long, List<ZDOID>>();
        private static readonly Dictionary<int, float> s_prefabUnits = new Dictionary<int, float>();   // per ZDO prefab hash: LOD size / end height, unscaled
        private static readonly List<long> s_scan = new List<long>(64);
        private static long s_camCell = long.MinValue;
        private static float s_maxWaitingUnits;
        private static float s_nextCensus, s_nextSweep;

        // census (30 s window) + totals
        private static int c_registered, c_attachedNow, c_attachedLate, c_dropped, c_removedByDestroy, c_cellChecks, c_passes, c_lateWorstFrames;
        private static double c_ms, c_worstMs;
        private static int s_verified, s_verifyMismatch;
        private static bool s_saidOn;

        internal static int Waiting => s_entries.Count;

        /// <summary>ZNetView.Awake (key on, not born-off): register instead of AddComponent. False = not handled here (caller uses the old path).</summary>
        internal static bool Register(ZNetView view)
        {
            var zdo = view != null ? view.GetZDO() : null;
            if (zdo == null) return true;                       // placement ghosts / no ZDO stay Unity's (as the old path)
            var go = view.gameObject;
            int prefab = zdo.GetPrefab();
            var v = CoverageRules.For(prefab, go);
            if (v.Refusal != null) return true;                 // refused per prefab: nothing to do, ever
            if (!s_saidOn) { s_saidOn = true; EasyBakeLog.Info("[CoverageRegistry] on: loading objects are registered, and handed to the indirect batches late in the frame once the camera can reach them (CoverageRegistryAttach)"); }
            var t = view.transform;
            var e = new Entry { View = view, Verdict = v, Prefab = prefab, Units = UnitsFor(prefab, go, t), Pos = t.position, Frame = Time.frameCount };
            s_entries[zdo.m_uid] = e;
            s_new.Add(zdo.m_uid);
            c_registered++;
            return true;
        }

        private static float UnitsFor(int prefab, GameObject go, Transform t)
        {
            if (!s_prefabUnits.TryGetValue(prefab, out float u))
            {
                u = float.PositiveInfinity;
                var lg = go.GetComponent<LODGroup>();
                if (lg != null)
                {
                    var lods = lg.GetLODs();
                    float end = lods.Length > 0 ? lods[lods.Length - 1].screenRelativeTransitionHeight : 0f;
                    if (end > 0f && lg.size > 0f) u = lg.size / end;   // LODGroup.size is local: per prefab unscaled, the instance's own scale applied below
                }
                s_prefabUnits[prefab] = u;
            }
            if (float.IsInfinity(u)) return u;
            Vector3 sc = t.lossyScale;
            return u * Mathf.Max(Mathf.Abs(sc.x), Mathf.Abs(sc.y), Mathf.Abs(sc.z));
        }

        /// <summary>ZNetScene.Destroy (from ZNetScene_Destroy_Patch): a waiting object being destroyed leaves the registry.</summary>
        internal static void OnDestroy(ZDO zdo)
        {
            if (s_entries.Count == 0 || zdo == null) return;
            if (!s_entries.TryGetValue(zdo.m_uid, out var e)) return;
            Unfile(zdo.m_uid, e);
            s_entries.Remove(zdo.m_uid);
            c_removedByDestroy++;
        }

        /// <summary>ZNetScene.Shutdown / scene start / key off: forget everything (key off attaches nothing: those objects stay Unity's, as before coverage engages).</summary>
        internal static void Clear()
        {
            s_entries.Clear(); s_new.Clear(); s_cells.Clear(); s_prefabUnits.Clear();
            s_camCell = long.MinValue; s_maxWaitingUnits = 0f;
        }

        private static long KeyOf(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / CellSize), z = Mathf.FloorToInt(p.z / CellSize);
            return (x << 32) ^ (z & 0xFFFFFFFFL);
        }

        private static void File(ZDOID id, ref Entry e)
        {
            e.Cell = KeyOf(e.Pos); e.Filed = true;
            if (!s_cells.TryGetValue(e.Cell, out var list)) { list = new List<ZDOID>(8); s_cells[e.Cell] = list; }
            list.Add(id);
            if (e.Units > s_maxWaitingUnits) s_maxWaitingUnits = e.Units;
        }

        private static void Unfile(ZDOID id, Entry e)
        {
            if (!e.Filed || !s_cells.TryGetValue(e.Cell, out var list)) return;
            int i = list.IndexOf(id);
            if (i >= 0) { list[i] = list[list.Count - 1]; list.RemoveAt(list.Count - 1); }
            if (list.Count == 0) s_cells.Remove(e.Cell);
        }

        // 1.2.274 (lead 11-10, 1.2.273 swamp leg: "1056 waiting out of reach", CloudberryBush 1,119 uncovered, covered meshes 465 -> 158): a waiting
        // object keeps its renderers ENABLED, so Unity culls it (LODGroup work) every frame, while the old path covered it and CoverageReach parked
        // it with its renderers out of Unity's culling. Holding far objects back is therefore not free. [Optimize] CoverageRegistryReachGate
        // (default OFF): off = every registered object is attached in the late-frame pass (budgeted), so the Awake saving stays and far objects
        // are covered + parked as on the old path; on = the 1.2.272 reach gate (attach only once the camera can reach them).
        private static bool ReachGateOn => FiresEasyBakeMeshesPlugin.CoverageRegistryReachGate != null && FiresEasyBakeMeshesPlugin.CoverageRegistryReachGate.Value;
        private static bool InReach(in Entry e, Vector3 cam, float factor)
            => !ReachGateOn || float.IsInfinity(e.Units) || Vector3.Distance(cam, e.Pos) <= e.Units * factor * ReachFactor + ReachSlack;

        /// <summary>EBM's LateUpdate. Free when idle: nothing new and the camera in the same cell = one compare.</summary>
        internal static void Tick()
        {
            Census();
            if (s_entries.Count == 0) { s_new.Clear(); return; }
            if (!On || !IndirectBatches.Active) return;          // waiting entries stay (vanilla draws them); attached when it comes back
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            Vector3 cp = cam.transform.position;
            long cell = KeyOf(cp);
            bool crossed = cell != s_camCell;
            float now = Time.unscaledTime;
            bool sweep = now >= s_nextSweep;
            if (s_new.Count == 0 && !crossed && !sweep) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long limit = t0 + (long)(PassBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            float factor = CoverageReach.Factor(cam);
            int done = 0;

            // 1) new entries: attach the ones in reach, file the rest by cell
            int i = 0;
            for (; i < s_new.Count && done < NewPerPassCap; i++)
            {
                if ((done & 15) == 15 && System.Diagnostics.Stopwatch.GetTimestamp() > limit) break;
                var id = s_new[i];
                if (!s_entries.TryGetValue(id, out var e) || e.Filed) continue;
                done++;
                if (e.View == null) { s_entries.Remove(id); c_dropped++; continue; }
                if (InReach(e, cp, factor)) { Attach(id, e, false); continue; }
                File(id, ref e);
                s_entries[id] = e;
            }
            if (i >= s_new.Count) s_new.Clear(); else s_new.RemoveRange(0, i);

            // 2) the camera crossed a cell: re-check only cells within the largest waiting reach
            if (crossed && s_cells.Count > 0)
            {
                s_camCell = cell;
                int r = Mathf.CeilToInt((s_maxWaitingUnits * factor * ReachFactor + ReachSlack) / CellSize) + 1;
                if (r > 64) r = 64;
                long cx = Mathf.FloorToInt(cp.x / CellSize), cz = Mathf.FloorToInt(cp.z / CellSize);
                s_scan.Clear();
                if ((2 * r + 1) * (2 * r + 1) < s_cells.Count) { for (long x = cx - r; x <= cx + r; x++) for (long z = cz - r; z <= cz + r; z++) { long k = (x << 32) ^ (z & 0xFFFFFFFFL); if (s_cells.ContainsKey(k)) s_scan.Add(k); } }
                else s_scan.AddRange(s_cells.Keys);
                foreach (var k in s_scan)
                {
                    if (!s_cells.TryGetValue(k, out var list)) continue;
                    c_cellChecks++;
                    for (int j = list.Count - 1; j >= 0; j--)
                    {
                        var id = list[j];
                        if (!s_entries.TryGetValue(id, out var e)) { list.RemoveAt(j); continue; }
                        if (e.View == null) { list.RemoveAt(j); s_entries.Remove(id); c_dropped++; continue; }
                        if (!InReach(e, cp, factor)) continue;
                        list.RemoveAt(j); e.Filed = false;
                        Attach(id, e, true);
                    }
                    if (list.Count == 0) s_cells.Remove(k);
                }
            }
            else if (crossed) s_camCell = cell;

            // 3) every 30 s: drop entries Unity destroyed without ZNetScene.Destroy (zone unload), recompute the largest waiting reach
            if (sweep)
            {
                s_nextSweep = now + 30f;
                s_maxWaitingUnits = 0f;
                s_scan.Clear(); s_scan.AddRange(s_cells.Keys);
                foreach (var k in s_scan)
                {
                    var list = s_cells[k];
                    for (int j = list.Count - 1; j >= 0; j--)
                    {
                        if (!s_entries.TryGetValue(list[j], out var e) || e.View == null) { if (s_entries.Remove(list[j])) c_dropped++; list.RemoveAt(j); continue; }
                        if (e.Units > s_maxWaitingUnits) s_maxWaitingUnits = e.Units;
                    }
                    if (list.Count == 0) s_cells.Remove(k);
                }
            }

            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            c_passes++; c_ms += ms; if (ms > c_worstMs) c_worstMs = ms;
        }

        private static void Attach(ZDOID id, Entry e, bool late)
        {
            s_entries.Remove(id);
            var view = e.View;
            if (view == null) { c_dropped++; return; }
            var go = view.gameObject;
            if (VerifyOn && s_verified < VerifyMax)
            {
                s_verified++;
                var again = CoverageRules.Judge(go);   // from scratch, not the per-prefab cache
                if ((again.Refusal == null) != (e.Verdict.Refusal == null) || again.Kind != e.Verdict.Kind)
                {
                    s_verifyMismatch++;
                    if (s_verifyMismatch <= 5) EasyBakeLog.Debug($"[CoverageRegistry] verify: {e.Verdict.Name} registered as {e.Verdict.Kind} / {(e.Verdict.Refusal ?? "covered")}, judged now {again.Kind} / {(again.Refusal ?? "covered")}");
                }
            }
            if (go.GetComponent<CoveredObject>() != null) return;   // already attached (old path, e.g. coverage re-engaged)
            var c = go.AddComponent<CoveredObject>();
            c.Verdict = e.Verdict;   // its Start covers it, as the old path
            // a piece EBM's bake took over while it waited is attached too: its Refresh sees EasyBakeSuppressedVisuals and marks it "baked by EBM"
            // (CoveredObject.cs Refresh), so it is covered again when the bake gives it back (CoverAfterBakeEnds), as on the old path.
            if (late)
            {
                c_attachedLate++;
                int frames = Time.frameCount - e.Frame;
                if (frames > c_lateWorstFrames) c_lateWorstFrames = frames;
            }
            else c_attachedNow++;
        }

        private static void Census()
        {
            float now = Time.unscaledTime;
            if (now < s_nextCensus) return;
            bool first = s_nextCensus <= 0f;
            s_nextCensus = now + 30f;
            if (!first && (c_registered > 0 || s_entries.Count > 0) && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[CoverageRegistry] 30 s: {c_registered} registered in Awake (no component), {c_attachedNow} attached at their first pass (in reach), "
                    + $"{c_attachedLate} attached late (camera came into reach; worst {c_lateWorstFrames} frame(s) after Awake), {s_entries.Count} waiting out of reach in {s_cells.Count} cell(s), "
                    + $"{c_removedByDestroy} removed by ZNetScene.Destroy, {c_dropped} dropped (destroyed without it); {c_passes} pass(es) {c_ms:0.00} ms, worst {c_worstMs:0.00} ms, {c_cellChecks} cell check(s)"
                    + (VerifyOn ? $"; verify {s_verified} of {VerifyMax} checked, {s_verifyMismatch} mismatch(es)" : ""));
            c_registered = c_attachedNow = c_attachedLate = c_dropped = c_removedByDestroy = c_cellChecks = c_passes = c_lateWorstFrames = 0;
            c_ms = c_worstMs = 0;
        }
    }
}
