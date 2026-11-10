using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.20x (DESIGN_IndirectEBM §4b Step 3). One per covered LIVE object: hands its static renderers to IndirectBatches and
    /// hides them with forceRenderingOff, or (1.2.21x, [Optimize] IndirectCoverageDisableRenderers) with renderer.enabled = false so Unity's culling skips them; never shadowCastingMode. Only what we changed is undone.
    /// Everything is event-driven: Start / OnEnable / OnDisable / OnDestroy, plus Refresh() from CoveragePatches when vanilla changes what
    /// the object shows (picked, regrown, a rock area broken, settled after a fall). There is no per-frame work.
    /// Hand-back rules:
    ///  - "Refresh" re-reads the object and re-adds only what still qualifies (inactive children drop out by themselves);
    ///  - a timed pause (tree shake 1 s, regrow 0.5 s) or an open pause (StaticPhysics falling) hands everything back until it ends;
    ///  - "sticky" (a MaterialMan property its materials read, so per-object looks we cannot carry) hands back for the object's life.
    /// A renderer that someone else already hid (forceRenderingOff set before us, or disabled) is never touched.
    /// </summary>
    internal sealed class CoveredObject : MonoBehaviour
    {
        internal static readonly HashSet<CoveredObject> Live = new HashSet<CoveredObject>();
        internal static int CoveredObjects, CoveredRenderers, Handles;   // what is handed over right now
        internal static readonly Dictionary<string, int> RendererRefusals = new Dictionary<string, int>();   // reason -> renderers, since load

        internal CoverageRules.Verdict Verdict;
        private bool m_started, m_paused, m_open;
        private string m_sticky;
        private Vector3 m_pos;

        // 1.2.21x ([ghost], [visual]: 35,222 enabled MeshRenderers, 1,225 visible; Unity's culling walks every ENABLED renderer each frame):
        // Disabled = we hid it with renderer.enabled = false (out of Unity's culling, [Optimize] IndirectCoverageDisableRenderers), else with
        // forceRenderingOff. Bounds = its world bounds when covered (a disabled renderer's own bounds are not used: the parts never move while covered).
        // 1.2.214 ([ghost] P2): what Add was called with, so a parked entry can be added back exactly (the parts never move while covered)
        private struct Entry { public MeshRenderer Renderer; public int[] Handles; public IndirectLodRange Range; public bool Disabled; public Bounds Bounds;
                               public Mesh Mesh; public Material[] Mats; public Matrix4x4 Matrix; public ShadowCastingMode Shadows; public int Layer;
                               public bool LevelOff; }   // 1.2.218 P3: this LOD level's slots released (camera outside its window)

        // ---- 1.2.214 ([ghost] P2, CoverageReach): reach and parking. ReachUnits = the largest WorldSize / EndHeight over this object's entries
        // (camera distance = ReachUnits x CoverageReach.Factor at which its last LOD level ends); +inf when any entry is never distance-culled.
        internal float ReachUnits;
        internal Vector3 ReachPoint;
        internal long FiledKey;
        internal bool Filed;
        private bool m_parked;
        private int m_parkedSlots;
        internal bool IsParked => m_parked;

        // ---- 1.2.218 ([ghost] P3, [Optimize] IndirectCoverageLodLevels): only the LOD level(s) the camera is near hold batch slots. Each entry is
        // one renderer = one LOD level with its own window (RangeFor). Visible window = camera distance [dStart, dEnd] (WorldSize x factor /
        // StartHeight .. / EndHeight); a level is HELD inside [dStart / 1.15 - 8 m, dEnd x 1.15 + 8 m] and released outside [dStart / 1.25 - 8 m,
        // dEnd x 1.25 + 8 m]. Passes run every 8 m of camera movement, so a level is always held before its window can be reached. Renderers stay
        // hidden either way; only GPU slots come and go. A level with no LOD window (WorldSize 0) is never released.
        internal static int LevelsOff, LevelSlotsOff, LevelOffs, LevelOns;

        internal void ApplyLevels(Vector3 cam, float factor)
        {
            if (m_parked || m_entries.Count == 0) return;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                var r = e.Range;
                if (r.WorldSize <= 0f) continue;
                float dS = (float.IsInfinity(r.StartHeight) || r.StartHeight >= float.MaxValue * 0.5f || r.StartHeight <= 0f) ? 0f : r.WorldSize * factor / r.StartHeight;
                float dE = r.EndHeight <= 0f ? float.PositiveInfinity : r.WorldSize * factor / r.EndHeight;
                float d = Vector3.Distance(cam, r.ReferencePointWorld);
                if (!e.LevelOff)
                {
                    if (d < dS / 1.25f - 8f || d > dE * 1.25f + 8f) LevelRelease(i);
                }
                else if (d >= dS / 1.15f - 8f && d <= dE * 1.15f + 8f)
                {
                    if (!LevelHold(i)) return;   // refused: the object went back to Unity whole
                }
            }
        }

        /// <summary>P3 key off: every released level of this object comes back.</summary>
        internal void HoldAllLevels()
        {
            for (int i = 0; i < m_entries.Count; i++) if (m_entries[i].LevelOff && !LevelHold(i)) return;
        }

        private void LevelRelease(int i)
        {
            var e = m_entries[i];
            if (e.LevelOff) return;
            if (IndirectBatches.Active) for (int k = 0; k < e.Handles.Length; k++) { try { IndirectBatches.Remove(e.Handles[k]); } catch { } }
            Handles -= e.Handles.Length; LevelSlotsOff += e.Mats.Length; LevelsOff++; LevelOffs++;
            e.Handles = s_noHandles; e.LevelOff = true;
            m_entries[i] = e;
        }

        private bool LevelHold(int i)
        {
            var e = m_entries[i];
            if (!e.LevelOff) return true;
            var added = new int[e.Mats.Length];
            for (int sub = 0; sub < e.Mats.Length; sub++)
            {
                EbmIndirect.s_addContext = "covered '" + transform.gameObject.name + "' (level back on) at " + transform.position.ToString("F0");   // 1.2.235
                int h = IndirectBatches.Add(e.Mesh, sub, e.Mats[sub], in e.Matrix, e.Shadows, e.Layer, in e.Range);
                if (h < 0)
                {
                    for (int k = 0; k < sub; k++) IndirectBatches.Remove(added[k]);
                    EasyBakeLog.Debug($"[CoverageReach] could not add a LOD level of {Verdict?.Name} back: handed to Unity");
                    Release();
                    return false;
                }
                added[sub] = h;
            }
            e.Handles = added; e.LevelOff = false; Handles += added.Length;
            LevelSlotsOff -= e.Mats.Length; LevelsOff--; LevelOns++;
            m_entries[i] = e;
            return true;
        }

        private static readonly int[] s_noHandles = new int[0];

        private void ComputeReach()
        {
            ReachUnits = 0f; ReachPoint = m_pos;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var r = m_entries[i].Range;
                if (r.WorldSize <= 0f || r.EndHeight <= 0f) { ReachUnits = float.PositiveInfinity; ReachPoint = r.ReferencePointWorld; return; }
                float u = r.WorldSize / r.EndHeight;
                if (u > ReachUnits) { ReachUnits = u; ReachPoint = r.ReferencePointWorld; }
            }
        }

        /// <summary>P2: release this object's batch slots; its renderers stay hidden (it is past its last LOD level, so nothing was drawn).</summary>
        internal void Park()
        {
            if (m_parked || m_entries.Count == 0) return;
            bool tablesLive = IndirectBatches.Active;
            int slots = 0;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                if (tablesLive) for (int k = 0; k < e.Handles.Length; k++) { try { IndirectBatches.Remove(e.Handles[k]); } catch { } }
                slots += e.Handles.Length; Handles -= e.Handles.Length;
                if (e.LevelOff) { e.LevelOff = false; LevelsOff--; LevelSlotsOff -= e.Mats.Length; }   // P3: now part of the whole-object park
                e.Handles = s_noHandles;
                m_entries[i] = e;
            }
            m_parked = true; m_parkedSlots = slots;
            CoverageReach.NoteParked(this, true, slots);
        }

        /// <summary>P2: add the slots back exactly as covered. False = refused: the object is handed back to Unity whole (never left unseen).</summary>
        internal bool Unpark()
        {
            if (!m_parked) return true;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                var added = new int[e.Mats.Length];
                for (int sub = 0; sub < e.Mats.Length; sub++)
                {
                    int h = IndirectBatches.Add(e.Mesh, sub, e.Mats[sub], in e.Matrix, e.Shadows, e.Layer, in e.Range);
                    if (h < 0)
                    {
                        for (int k = 0; k < sub; k++) IndirectBatches.Remove(added[k]);
                        EasyBakeLog.Debug($"[CoverageReach] could not add {Verdict?.Name} back to the batches: handed to Unity");
                        Release();   // clears the parked state too, renderers shown again
                        return false;
                    }
                    added[sub] = h;
                }
                e.Handles = added; Handles += added.Length;
                m_entries[i] = e;
            }
            m_parked = false;
            CoverageReach.NoteParked(this, false, m_parkedSlots);
            m_parkedSlots = 0;
            return true;
        }

        /// <summary>P1 census: instances this object holds (or would hold when parked), and how many of them can never be distance-culled.</summary>
        internal int HeldInstances(out int neverCulled)
        {
            int n = 0; neverCulled = 0;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                int c = e.Mats != null ? e.Mats.Length : e.Handles.Length;
                n += c;
                if (e.Range.WorldSize <= 0f || e.Range.EndHeight <= 0f) neverCulled += c;
            }
            return n;
        }
        internal static bool DisableMode => s_hideOverride ?? (FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers != null && FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers.Value);
        private static bool? s_hideOverride;   // ebm_coverhide on|off: this session only, the cfg is never written

        /// <summary>ebm_coverhide: switch the hiding method for this session and re-cover every object the new way (both directions).</summary>
        internal static void SetHideOverride(bool disable)
        {
            s_hideOverride = disable;
            EasyBakeLog.Info($"[Coverage] renderer hide mode -> {(disable ? "disabled" : "forceRenderingOff")} by command (ebm_coverhide, this session only; the cfg still says {((FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers != null && FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers.Value) ? "disabled" : "forceRenderingOff")})");
            RefreshEverything();
        }

        internal static string HideStatus()
            => $"[Coverage] renderer hide mode {(DisableMode ? "disabled (renderer.enabled = false)" : "forceRenderingOff")}{(s_hideOverride.HasValue ? " (session override)" : " (cfg)")}: covered {CoveredObjects} object(s) / {CoveredRenderers} renderer(s), disabled now {DisabledNow}, contested {ContestedObjects}; {PrefabCoverage.Status()}, born-off waiting {s_bornPending.Count}";
        internal static int DisabledNow, ContestedObjects;
        private static readonly HashSet<Renderer> s_disabledByUs = new HashSet<Renderer>();

        /// <summary>
        /// True for a renderer coverage switched off (renderer.enabled = false) while the batches draw it. Code that reads a LIVE renderer's
        /// enabled to mean "vanilla shows this" (EBM's bake tiers, the bake suppressor's prior state) must read "enabled || HiddenByCoverage(r)".
        /// One hash lookup.
        /// </summary>
        internal static bool HiddenByCoverage(Renderer r)
            => r != null && ((s_disabledByUs.Count > 0 && s_disabledByUs.Contains(r))
                          || (s_bornPending.Count > 0 && s_bornPending.Contains(r))   // step 2: a copy's part born off, not covered yet
                          || PrefabCoverage.IsOffOnPrefab(r));                        // step 2: a PREFAB part we switched off (EBM's prefab reads)

        // ---- step 2 ([Optimize] IndirectCoverageDisablePrefabs): parts this copy was born with switched off (PrefabCoverage). Adopted on the
        // first cover: covered = stays off (disable mode) or comes on under forceRenderingOff; anything not covered is switched back on, so a
        // copy never keeps a part off that coverage does not draw.
        private static readonly HashSet<Renderer> s_bornPending = new HashSet<Renderer>();
        // 1.2.257 (per-object create allocations, item 3): m_bornOff, m_entries, m_lodsOff and m_seenMaterials start as one shared EMPTY instance
        // each and get their own collection on the first Add (the only writers that add are AdoptBornOff, TryCoverPart, Refresh's LODGroup pass and
        // TryCoverPart's material note). Readers see Count 0 exactly as before; Clear / Remove on the shared empty change nothing. Never Add to the
        // shared ones: every Add goes through Own*(). Main thread only (MonoBehaviour), as before.
        private static readonly List<MeshRenderer> s_noBornOff = new List<MeshRenderer>(0);
        private static readonly List<Entry> s_noEntries = new List<Entry>(0);
        private static readonly List<LODGroup> s_noLodsOff = new List<LODGroup>(0);
        private static readonly HashSet<Material> s_noSeen = new HashSet<Material>();
        // estimate per CoveredObject made: List(0) 1 + List(4) 2 + List(1) 2 + HashSet 1 = 6 allocations; each one created later on first use is taken back off
        internal static long AllocsAvoided;
        internal static void CountShellsAvoided() => AllocsAvoided += 6;

        private List<MeshRenderer> m_bornOff = s_noBornOff;
        private List<MeshRenderer> OwnBornOff() { if (ReferenceEquals(m_bornOff, s_noBornOff)) { m_bornOff = new List<MeshRenderer>(0); AllocsAvoided -= 1; } return m_bornOff; }

        private void Awake() => CountShellsAvoided();

        internal void AdoptBornOff(List<MeshRenderer> born)
        {
            for (int i = 0; i < born.Count; i++) if (born[i] != null) { OwnBornOff().Add(born[i]); s_bornPending.Add(born[i]); PrefabCoverage.BornAdopted++; }
            born.Clear();
            if (m_started) Refresh();
        }

        internal void RestoreBornOff()
        {
            if (m_bornOff.Count == 0) return;
            for (int i = 0; i < m_bornOff.Count; i++)
            {
                var r = m_bornOff[i];
                if (r == null) { s_bornPending.RemoveWhere(x => x == null); continue; }
                s_bornPending.Remove(r);
                if (!r.enabled) { r.enabled = true; PrefabCoverage.BornRestored++; }
            }
            m_bornOff.Clear();
        }
        private bool m_refreshQueued;
        private List<Entry> m_entries = s_noEntries;
        private List<Entry> OwnEntries() { if (ReferenceEquals(m_entries, s_noEntries)) { m_entries = new List<Entry>(4); AllocsAvoided -= 2; } return m_entries; }

        // [Heap] by holder (Debug, inside the 60 s census): layout sizes x counts; the component's own shell is an estimate (160 B). Managed side only.
        internal static long HeapHolders(System.Text.StringBuilder sb)
        {
            int entrySize = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Entry>(), entries = 0, capacity = 0;
            long b = 0;
            foreach (var o in Live)
            {
                if (o == null) continue;
                entries += o.m_entries.Count; capacity += o.m_entries.Capacity;
                b += 160L + 72L;   // shell + the List object and its array header
                for (int i = 0; i < o.m_entries.Count; i++)
                {
                    var e = o.m_entries[i];
                    if (e.Handles != null) b += 32L + e.Handles.Length * 4L;
                    if (e.Mats != null) b += 32L + e.Mats.Length * 8L;
                }
            }
            b += capacity * (long)entrySize;
            sb.Append($"covered objects {b / (1024.0 * 1024.0):0.0} MB ({Live.Count} objects, {entries} entries, {entrySize} B each, capacity {capacity}); ");
            return b;
        }
        private List<LODGroup> m_lodsOff = s_noLodsOff;
        private List<LODGroup> OwnLodsOff() { if (ReferenceEquals(m_lodsOff, s_noLodsOff)) { m_lodsOff = new List<LODGroup>(1); AllocsAvoided -= 2; } return m_lodsOff; }

        private static readonly List<MeshRenderer> s_renderers = new List<MeshRenderer>(16);
        private static readonly List<LODGroup> s_groups = new List<LODGroup>(4);
        private static readonly List<Material> s_materials = new List<Material>(4);
        private static readonly List<int> s_added = new List<int>(8);
        private static readonly HashSet<Renderer> s_covered = new HashSet<Renderer>();

        internal bool IsCovering => m_entries.Count > 0;
        internal string StickyReason => m_sticky;

        private void Start()
        {
            m_started = true; Live.Add(this);
            // CoverageBatchedStart: queue the first cover for the one late-frame pass. A copy holding born-off parts covers now as before
            // (its parts are off until covered, so it must never reach a drawn frame uncovered).
            if (Batched && m_bornOff.Count == 0) { Enqueue(ref m_pendStart); return; }
            Refresh();
        }
        private void OnEnable() { if (m_started) { Live.Add(this); Count("object re-enabled"); Refresh(); } }
        private void OnDisable() { CancelInvoke(nameof(QueuedRefresh)); CancelInvoke(nameof(QueuedAfterBake)); m_refreshQueued = false; m_pendPart = false; Release(); }   // a queued Start cover stays queued: it runs Refresh, which hands an inactive object back exactly as Start did
        private void OnDestroy() { Live.Remove(this); m_pendStart = false; m_pendPart = false; Release(); RestoreBornOff(); }

        // ---- CoverageBatchedStart ([Optimize], default off): Start and the part on/off re-cover only queue; FlushPending (EBM's LateUpdate,
        // after every Start of the frame's spawns) runs the same Refresh for all of them. A destroyed object drops out (Unity null); a disabled
        // one runs the same Refresh it would have at Start (which hands it back). Bounded: one slot per live object at most; 2 ms per pass,
        // the rest next frame (they stay drawn by Unity meanwhile, never unseen).
        private static bool Batched => FiresEasyBakeMeshesPlugin.CoverageBatchedStart != null && FiresEasyBakeMeshesPlugin.CoverageBatchedStart.Value;
        private bool m_pendStart, m_pendPart, m_inPending;
        private static readonly List<CoveredObject> s_pending = new List<CoveredObject>(256);
        private const double PassBudgetMs = 2.0;
        private static int s_batchObjects, s_batchPasses; private static double s_batchMs, s_batchWorst; private static float s_batchNextReport;

        private void Enqueue(ref bool flag)
        {
            flag = true;
            if (m_inPending) return;
            m_inPending = true; s_pending.Add(this);
        }

        internal static void FlushPending()
        {
            if (s_pending.Count > 0)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long limit = t0 + (long)(PassBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
                int i = 0;
                for (; i < s_pending.Count; i++)
                {
                    if (i > 0 && System.Diagnostics.Stopwatch.GetTimestamp() > limit) break;
                    var c = s_pending[i];
                    if (c == null) continue;   // destroyed before the pass
                    c.m_inPending = false;
                    bool start = c.m_pendStart, part = c.m_pendPart;
                    c.m_pendStart = false; c.m_pendPart = false;
                    try
                    {
                        if (start) c.Refresh();
                        if (part) c.QueuedRefresh();
                    }
                    catch (System.Exception e) { EasyBakeLog.Debug($"[Coverage] batched cover of {c.Verdict?.Name} threw: {e.Message}"); }
                    s_batchObjects++;
                }
                if (i >= s_pending.Count) s_pending.Clear(); else s_pending.RemoveRange(0, i);
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s_batchPasses++; s_batchMs += ms; if (ms > s_batchWorst) s_batchWorst = ms;
            }
            float now = Time.realtimeSinceStartup;
            if (now < s_batchNextReport) return;
            s_batchNextReport = now + 30f;
            if (s_batchPasses > 0 && EasyBakeLog.DebugOn())
                EasyBakeLog.Debug($"[Coverage] batched start last 30 s: {s_batchObjects} object(s) in {s_batchPasses} pass(es), {s_batchMs:0.00} ms total, worst pass {s_batchWorst:0.00} ms, {s_pending.Count} waiting");
            s_batchObjects = 0; s_batchPasses = 0; s_batchMs = 0; s_batchWorst = 0;
        }

        /// <summary>Hand everything back for good (this object's life): a per-object look we cannot carry.</summary>
        internal void HandBackForGood(string reason)
        {
            if (m_sticky != null) return;
            m_sticky = reason;
            bool was = IsCovering;
            Release();
            if (was) EasyBakeLog.Debug($"[Coverage] {Verdict?.Name} handed back to vanilla for good: {reason}");
        }

        /// <summary>Hand back for a fixed time (vanilla animates the object), then cover again.</summary>
        internal void HandBackFor(float seconds)
        {
            m_paused = true;
            Release();
            CancelInvoke(nameof(Resume));
            Invoke(nameof(Resume), seconds);
        }

        /// <summary>Hand back until Resume() is called by the event that ends it (StaticPhysics stopped falling).</summary>
        internal void HandBackUntilResumed() { m_open = true; Release(); }

        internal void Resume() { m_paused = false; m_open = false; Count("pause ended (tree shake / regrow / settled)"); Refresh(); }
        internal bool WaitingForSettle => m_open;
        internal static readonly Dictionary<int, int> HandBacksByProperty = new Dictionary<int, int>();   // shader property id -> look holds

        /// <summary>The object moved without an event of its own (StaticPhysics.PushUp): re-cover only if it really did.</summary>
        internal void RefreshIfMoved() { if (transform.position != m_pos) { Count("moved (StaticPhysics push-up)"); Refresh(); } }

        // ---- per-object looks (MaterialMan): a hover highlight, a damage / burn tint. Held only WHILE a value a covered material reads
        // is set, then covered again when MaterialMan clears it (WearNTear.ResetHighlight 0.2 s later), never "for good": a piece the
        // player once looked at must not stay off the batches for the rest of the session.
        private bool m_lookHeld;
        internal bool LookHeld => m_lookHeld;
        internal void HoldForLook() { if (m_lookHeld) return; m_lookHeld = true; Count("look hold: MaterialMan value set (handed back)"); Release(); }
        internal void ReleaseLookHold() { if (!m_lookHeld) return; m_lookHeld = false; Count("look hold ended (re-covered)"); Refresh(); }

        // the materials this object's candidate renderers use (filled by the last Refresh, kept while released) - what "reads" is asked about
        private HashSet<Material> m_seenMaterials = s_noSeen;
        private HashSet<Material> OwnSeen() { if (ReferenceEquals(m_seenMaterials, s_noSeen)) { m_seenMaterials = new HashSet<Material>(); AllocsAvoided -= 1; } return m_seenMaterials; }
        internal bool AnySeenMaterialReads(int nameId)
        {
            foreach (var m in m_seenMaterials) if (m != null && m.HasProperty(nameId)) return true;
            return false;
        }

        // ---- dropped items: drawn by the batches only while their Rigidbody sleeps. Checked on vanilla's own 10 s ItemDrop.SlowUpdate and
        // on any collision (CoveredItemBump); a woken item goes back to Unity at once and is covered again, where it now rests, once asleep.
        internal void SleepCheck()
        {
            if (Verdict == null || Verdict.Kind != CoverageRules.Kind.Item) return;
            bool asleep = m_body != null && m_body.IsSleeping();
            if (asleep && !IsCovering) { Count("item came to rest (re-covered)"); Refresh(); }
            else if (!asleep && IsCovering) { Count("item moving (handed back)"); Release(); }
        }
        internal void Woken() { if (IsCovering) { Count("item bumped (handed back)"); Release(); } }
        private Rigidbody m_body;

        /// <summary>EBM's own bake took this piece over (EasyBakeSuppressedVisuals): hand everything back now, so it is never drawn twice.</summary>
        internal static void ReleaseFor(GameObject go)
        {
            var co = go != null ? go.GetComponent<CoveredObject>() : null;
            if (co != null) { co.Release(); co.RestoreBornOff(); co.m_sticky = BakedByEbm; }
        }

        // 1.2.255 [Optimize] CoverAfterBakeEnds: "baked by EBM" lasts only as long as the bake does. Once the bake gives the piece back it can be covered again, next frame, coalesced.
        private const string BakedByEbm = "baked by EBM";
        private static bool s_saidBakeEnded;
        private static bool CoverAfterBakeEnds => FiresEasyBakeMeshesPlugin.IndirectCoverAfterBakeEnds == null || FiresEasyBakeMeshesPlugin.IndirectCoverAfterBakeEnds.Value;

        internal static void BakeEnded(GameObject go)
        {
            if (!CoverAfterBakeEnds || go == null) return;
            var co = go.GetComponent<CoveredObject>();
            if (co == null || co.m_sticky != BakedByEbm) return;
            co.m_sticky = null;
            Count("the bake gave the piece back (re-covered)");
            if (!s_saidBakeEnded) { s_saidBakeEnded = true; EasyBakeLog.Info("[Coverage] CoverAfterBakeEnds: pieces a bake gives back are covered again"); }
            if (co.m_refreshQueued || !co.m_started) return;
            co.m_refreshQueued = true;
            co.Invoke(nameof(QueuedAfterBake), 0f);
        }

        private void QueuedAfterBake() { m_refreshQueued = false; Refresh(); }

        // ---- 1.2.215 ([ghost], [visual]'s standing-still churn): every re-cover counted by reason (census line), and vanilla's timer hooks
        // re-cover only on a real change. Signature = the active MeshRenderers under the object and their meshes (MineRock5 swaps meshes).
        internal static readonly Dictionary<string, int> RefreshReasons = new Dictionary<string, int>();
        internal static int RefreshesSkipped;
        private int m_signature, m_signatureCount = -1;

        internal static void Count(string reason)
        {
            RefreshReasons.TryGetValue(reason, out int n);
            if (n == 0 && RefreshReasons.Count >= 32) return;
            RefreshReasons[reason] = n + 1;
        }

        private int Signature(out int count)
        {
            GetComponentsInChildren(false, s_sigRenderers);
            int h = 17; count = 0;
            for (int i = 0; i < s_sigRenderers.Count; i++)
            {
                var r = s_sigRenderers[i];
                if (r == null) continue;
                var mf = r.GetComponent<MeshFilter>();
                int mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.GetInstanceID() : 0;
                h = unchecked(h * 31 + r.GetInstanceID()); h = unchecked(h * 31 + mesh);
                count++;
            }
            return h;
        }
        private static readonly List<MeshRenderer> s_sigRenderers = new List<MeshRenderer>(16);

        /// <summary>From vanilla's own hooks (some on timers): re-cover only when the active renderers / meshes changed since the last cover.</summary>
        internal void RefreshIfChanged(string reason)
        {
            // 1.2.227 ([ghost], Fire 1226 log: ItemStand.SetVisualItem still ~730 re-covers per 30 s standing still): the skip also holds for an object
            // whose last full cover took nothing (an empty stand, or one whose item is not coverable). Before, only a covering object stored a
            // signature, so vanilla's 4 s UpdateVisual re-ran a full Release + cover on every such stand.
            if (m_started && m_signatureCount >= 0)
            {
                int sig = Signature(out int count);
                if (sig == m_signature && count == m_signatureCount) { RefreshesSkipped++; return; }
            }
            Count(reason);
            Refresh();
        }

        internal void Refresh()
        {
            m_signatureCount = -1;
            Release();
            if (!m_started) return;   // Start covers it (born-off parts wait for that, before the first frame is drawn)
            if (!isActiveAndEnabled || m_sticky != null || m_paused || m_open || m_lookHeld) { RestoreBornOff(); return; }
            if (!IndirectBatches.Active) { RestoreBornOff(); return; }
            if (TryGetComponent<EasyBakeSuppressedVisuals>(out var suppressed) && !(CoverAfterBakeEnds && suppressed.Restored)) { RestoreBornOff(); m_sticky = BakedByEbm; return; }   // EBM's bake draws it
            if (Verdict != null && Verdict.Kind == CoverageRules.Kind.Item)
            {
                if (m_body == null) m_body = GetComponent<Rigidbody>();
                if (m_body == null || !m_body.IsSleeping()) { RestoreBornOff(); return; }   // moving: Unity draws it until it rests
                if (!TryGetComponent<CoveredItemBump>(out _)) gameObject.AddComponent<CoveredItemBump>();
            }
            m_pos = transform.position;
            m_seenMaterials.Clear();

            s_covered.Clear();
            GetComponentsInChildren(false, s_renderers);   // active only: picked / broken / hidden parts drop out by themselves
            for (int i = 0; i < s_renderers.Count; i++)
            {
                var r = s_renderers[i];
                string why = TryCover(r);
                if (why == null) s_covered.Add(r);
                else if (why.Length > 0) { Refused(why); RefusedByPrefab(Verdict?.Name, r, why); }
            }
            RestoreBornOff();   // step 2: born-off parts not covered now (inactive, refused) are vanilla again

            // A LODGroup whose every renderer is now drawn by the batches has nothing left to pick: turn it off (its CPU cost goes too).
            // One with any vanilla renderer left stays on, or those would draw at every level at once.
            if (s_covered.Count > 0)
            {
                GetComponentsInChildren(false, s_groups);
                for (int g = 0; g < s_groups.Count; g++)
                {
                    var lg = s_groups[g];
                    if (lg == null || !lg.enabled) continue;
                    bool all = true, any = false;
                    foreach (var lod in lg.GetLODs())
                        if (lod.renderers != null)
                            foreach (var lr in lod.renderers) { if (lr == null) continue; any = true; if (!s_covered.Contains(lr)) { all = false; break; } }
                    if (any && all) { lg.enabled = false; OwnLodsOff().Add(lg); }
                }
            }
            if (m_entries.Count > 0)
            {
                CoveredObjects++; CoveragePatches.SayEngagedOnce(Verdict);
                ComputeReach();
                CoverageReach.File(this);   // 1.2.214 P2: filed by cell; judged at once while the gate is on (a far object is parked before it is drawn)
            }
            m_signature = Signature(out m_signatureCount);   // 1.2.215: what this cover saw; 1.2.227: stored covered or not (only a full attempt gets here)
        }

        // null = covered; "" = silently not ours (someone else hid it); otherwise the refusal reason (counted for ebm_coverage)
        private string TryCover(MeshRenderer r)
        {
            bool born = r != null && m_bornOff.Count > 0 && m_bornOff.Remove(r);   // step 2: switched off on its prefab, judged there already
            if (born) s_bornPending.Remove(r);
            string why = TryCoverPart(r, born);
            if (why != null && born && r != null && !r.enabled) { r.enabled = true; PrefabCoverage.BornRestored++; }   // not drawn by us: vanilla again
            return why;
        }

        private string TryCoverPart(MeshRenderer r, bool born)
        {
            if (r == null || (!r.enabled && !born) || r.forceRenderingOff) return "";
            var root = transform;
            if (Verdict != null && Verdict.ExcludedPaths.Count > 0 && Verdict.ExcludedPaths.Contains(CoverageRules.PathOf(root, r.transform))) return "changes at runtime (material variation / animator / fader)";
            if (CoverageRules.IsLowTwin(r)) return "";
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) return "no mesh";
            // A fractured rock's own mesh is a private per-rock copy (one batch per rock = nothing saved); only the shared intact mesh and
            // its LOD1 from IntactRockLod ([Optimize] BakeIntactRocks) are batchable.
            if (Verdict != null && Verdict.Kind == CoverageRules.Kind.MineRock5 && !mesh.name.StartsWith("EBM intact rock", System.StringComparison.Ordinal))
                return "fractured rock with its own mesh (damaged, or BakeIntactRocks off)";
            r.GetSharedMaterials(s_materials);
            for (int m = 0; m < s_materials.Count; m++) if (s_materials[m] != null) OwnSeen().Add(s_materials[m]);
            if (s_materials.Count == 0 || s_materials.Count > mesh.subMeshCount) return "material count does not match the mesh's submeshes";
            // A per-object MaterialMan value set before we got here (RandomMaterialValues at Start, a damage tint) that these materials read:
            // a look the batches cannot carry (v1). Held, not refused: PerObjectLookWatch covers it again when the value is cleared.
            if (CoveragePatches.LookSetOn(transform.gameObject, s_materials) || CoveragePatches.LookSetOn(r.gameObject, s_materials))
            { HoldForLook(); return "per-object MaterialMan look (held until cleared)"; }
            for (int m = 0; m < s_materials.Count; m++)
            {
                if (s_materials[m] == null) return "missing material";
                if (!IndirectBatches.Accepts(s_materials[m])) return "no indirect variant for shader " + (s_materials[m].shader != null ? s_materials[m].shader.name : "?");
            }
            if (!CoverageRules.RangeFor(r, out var range)) return "";   // under a LODGroup but in none of its levels: Unity never draws it

            var matrix = r.transform.localToWorldMatrix;
            EbmIndirect.NoteCoveredRenderer(r, mesh);   // 1.2.246: logs only, with the validator on
            EbmIndirect.s_addContext = "covered '" + transform.gameObject.name + "/" + r.gameObject.name + "' at " + transform.position.ToString("F0");   // 1.2.235
            s_added.Clear();
            for (int sub = 0; sub < s_materials.Count; sub++)
            {
                int h = IndirectBatches.Add(mesh, sub, s_materials[sub], in matrix, r.shadowCastingMode, r.gameObject.layer, in range);
                if (h < 0) { for (int k = 0; k < s_added.Count; k++) IndirectBatches.Remove(s_added[k]); return "refused by IndirectBatches.Add"; }
                s_added.Add(h);
            }
            // taken while Unity still treats it as a live renderer; a born-off part has no live renderer bounds, so its mesh bounds are moved into the world
            var bounds = born ? WorldBounds(mesh.bounds, in matrix) : r.bounds;
            // Every kind, pieces included: EBM's own bake and suppressor read a live renderer we disabled as ENABLED (HiddenByCoverage).
            // Step 2 prefab mode always hides by enabled = false (the part is already off: it never enters Unity's renderer scene).
            bool disable = DisableMode || born;
            if (disable) { r.enabled = false; DisabledNow++; s_disabledByUs.Add(r); } else r.forceRenderingOff = true;
            OwnEntries().Add(new Entry { Renderer = r, Handles = s_added.ToArray(), Range = range, Disabled = disable, Bounds = bounds,
                                      Mesh = mesh, Mats = s_materials.ToArray(), Matrix = matrix, Shadows = r.shadowCastingMode, Layer = r.gameObject.layer });
            CoveredRenderers++; Handles += s_added.Count;
            if (!r.TryGetComponent<CoveredPartWatch>(out _)) r.gameObject.AddComponent<CoveredPartWatch>();   // once per part, kept for its life
            return null;
        }

        /// <summary>
        /// Gives everything back to Unity. Order matters ([generator] review 11-08): the looks come back FIRST (LODGroups on, renderers
        /// shown) for every entry, and only then are the batch handles removed, each on its own, so a failing Remove can never leave a part
        /// hidden with nothing drawing it. While the batches are off their tables are already cleared (and their slots may be handed out
        /// again on the next start), so stale handles are dropped without a Remove.
        /// </summary>
        private void Release() => ReleaseEntries();

        private static Bounds WorldBounds(Bounds local, in Matrix4x4 m)
        {
            var c = m.MultiplyPoint3x4(local.center);
            var e = local.extents;
            var x = new Vector3(Mathf.Abs(m.m00), Mathf.Abs(m.m10), Mathf.Abs(m.m20)) * e.x;
            var y = new Vector3(Mathf.Abs(m.m01), Mathf.Abs(m.m11), Mathf.Abs(m.m21)) * e.y;
            var z = new Vector3(Mathf.Abs(m.m02), Mathf.Abs(m.m12), Mathf.Abs(m.m22)) * e.z;
            return new Bounds(c, 2f * (x + y + z));
        }

        private void ReleaseEntries()
        {
            if (m_entries.Count == 0 && m_lodsOff.Count == 0) return;
            CoverageReach.Unfile(this);   // 1.2.214 P2
            if (m_parked) { m_parked = false; CoverageReach.NoteParked(this, false, m_parkedSlots); m_parkedSlots = 0; }   // its handles are already empty
            for (int g = 0; g < m_lodsOff.Count; g++) if (m_lodsOff[g] != null) m_lodsOff[g].enabled = true;
            m_lodsOff.Clear();
            for (int i = 0; i < m_entries.Count; i++)
                if (m_entries[i].LevelOff) { LevelsOff--; LevelSlotsOff -= m_entries[i].Mats.Length; }   // 1.2.218 P3: its slots are already gone
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                if (e.Disabled) { DisabledNow--; if (e.Renderer != null) s_disabledByUs.Remove(e.Renderer); else s_disabledByUs.RemoveWhere(x => x == null); }   // a destroyed part: drop dead keys
                if (e.Renderer == null) continue;
                if (e.Disabled) e.Renderer.enabled = true; else e.Renderer.forceRenderingOff = false;   // undo exactly what we did
            }

            bool tablesLive = IndirectBatches.Active;
            for (int i = 0; i < m_entries.Count; i++)
            {
                var e = m_entries[i];
                if (tablesLive)
                    for (int k = 0; k < e.Handles.Length; k++)
                    {
                        try { IndirectBatches.Remove(e.Handles[k]); }
                        catch (System.Exception ex) { EasyBakeLog.Debug($"[Coverage] remove of handle {e.Handles[k]} for {Verdict?.Name} failed: {ex.Message}"); }
                    }
                CoveredRenderers--; Handles -= e.Handles.Length;
            }
            if (m_entries.Count > 0) CoveredObjects--;
            m_entries.Clear();
        }

        /// <summary>A part of this object was switched on or off by a path we do not hook (CoveredPartWatch): look again, once.</summary>
        internal void PartActivityChanged()
        {
            if (m_refreshQueued || !m_started) return;
            m_refreshQueued = true;
            if (Batched) { Enqueue(ref m_pendPart); return; }   // CoverageBatchedStart: same coalescing, run by the late-frame pass
            Invoke(nameof(QueuedRefresh), 0f);   // coalesced: a whole subtree switching fires once; an inactive object refreshes in OnEnable
        }

        private void QueuedRefresh() { m_refreshQueued = false; Count("a covered part switched on/off (CoveredPartWatch)"); CountPartToggle(Verdict?.Name); Refresh(); }

        // 1.2.216 ([visual] / Fire's 1.2.215 O run: 6,197 part on/off re-covers per 30 s standing still): which prefabs' parts are switched
        internal static readonly Dictionary<string, int> PartToggleByPrefab = new Dictionary<string, int>();
        private static void CountPartToggle(string name)
        {
            name = name ?? "?";
            PartToggleByPrefab.TryGetValue(name, out int n);
            if (n > 0 || PartToggleByPrefab.Count < 64) PartToggleByPrefab[name] = n + 1;
        }

        internal void Contested()
        {
            ContestedObjects++;
            HandBackForGood("a covered renderer was drawn by Unity again (another path turned it back on)");
        }

        /// <summary>True when this renderer is hidden by us (the debris check and the per-object look watch).</summary>
        internal bool Covers(Renderer r)
        {
            for (int i = 0; i < m_entries.Count; i++) if (m_entries[i].Renderer == r) return true;
            return false;
        }

        /// <summary>
        /// Would Unity have drawn this covered renderer for the main camera this frame? The question vanilla's debris asks with isVisible
        /// (which Unity never sets under forceRenderingOff): inside the frustum AND inside its own LOD window, so only the level actually
        /// shown breaks into fragments, never LOD0 + LOD1 + LOD2 stacked.
        /// </summary>
        internal bool WouldBeDrawn(Renderer r, Camera cam, Plane[] planes)
        {
            for (int i = 0; i < m_entries.Count; i++)
            {
                if (m_entries[i].Renderer != r) continue;
                if (!GeometryUtility.TestPlanesAABB(planes, m_entries[i].Bounds)) return false;
                var range = m_entries[i].Range;
                if (range.WorldSize <= 0f) return true;
                float d = Mathf.Max(0.01f, Vector3.Distance(cam.transform.position, range.ReferencePointWorld));
                float h = range.WorldSize / (2f * d * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)) * QualitySettings.lodBias;
                return h >= range.EndHeight && h < range.StartHeight;
            }
            return false;
        }

        /// <summary>Does any material of a renderer we draw read this shader property?</summary>
        internal bool ReadsProperty(int nameId)
        {
            for (int i = 0; i < m_entries.Count; i++)
            {
                var r = m_entries[i].Renderer;
                if (r == null) continue;
                r.GetSharedMaterials(s_materials);
                for (int m = 0; m < s_materials.Count; m++) if (s_materials[m] != null && s_materials[m].HasProperty(nameId)) return true;
            }
            return false;
        }

        // 1.2.218 ([ghost], [lead]: why do item stands / rugs stay vanilla?): refused renderers by prefab, part and reason, since the last census line
        internal static readonly Dictionary<string, int> RefusalsByPrefab = new Dictionary<string, int>();
        private static void RefusedByPrefab(string prefab, MeshRenderer r, string why)
        {
            if (!EasyBakeLog.DebugOn()) return;
            string key = (prefab ?? "?") + "/" + (r != null ? r.name : "?") + ": " + why;
            RefusalsByPrefab.TryGetValue(key, out int n);
            if (n > 0 || RefusalsByPrefab.Count < 128) RefusalsByPrefab[key] = n + 1;
        }

        private static void Refused(string why)
        {
            RendererRefusals.TryGetValue(why, out int n);
            if (n == 0 && RendererRefusals.Count >= 48) return;   // bounded: the reasons are a small fixed set plus shader names
            RendererRefusals[why] = n + 1;
        }

        internal static void ReleaseEverything()
        {
            foreach (var c in new List<CoveredObject>(Live)) if (c != null) { c.Release(); c.RestoreBornOff(); }
        }

        internal static void RefreshEverything()
        {
            foreach (var c in new List<CoveredObject>(Live)) if (c != null) c.Refresh();
        }
    }

    /// <summary>
    /// On a covered dropped item only (never on trees or rocks: a player walking into one would hand it back): any collision means the
    /// item may move, so it goes back to Unity at once; the next ItemDrop.SlowUpdate covers it again once its body sleeps.
    /// </summary>
    internal sealed class CoveredItemBump : MonoBehaviour
    {
        private CoveredObject m_owner;
        private void OnCollisionEnter(Collision _)
        {
            if (m_owner == null) m_owner = GetComponent<CoveredObject>();
            if (m_owner != null) m_owner.Woken();
        }
    }

    /// <summary>
    /// On each covered part's GameObject ([generator] review 11-08): Unity calls OnDisable / OnEnable whenever the part or ANY parent is
    /// switched, whatever did it (Pickable.UpdateRespawn hiding m_hideWhenPicked directly, a regrown trunk coming back, a MineRock area
    /// or base model, another mod). The owner then looks again once. A copy made by Instantiate (TreeBase's regrow trunk) finds an owner
    /// that does not cover it and changes nothing.
    /// </summary>
    internal sealed class CoveredPartWatch : MonoBehaviour
    {
        private void OnEnable() => Tell();
        private void OnDisable() => Tell();

        // A covered part never draws through Unity (enabled = false or forceRenderingOff). If Unity says it became visible, something turned it
        // back on (another mod, a vanilla path coverage does not hook): the object goes back to Unity for its life, so it is never drawn twice.
        private void OnBecameVisible()
        {
            var owner = GetComponentInParent<CoveredObject>(true);
            if (owner == null) return;
            var r = GetComponent<Renderer>();
            if (r != null && owner.Covers(r)) owner.Contested();
        }

        private void Tell()
        {
            var owner = GetComponentInParent<CoveredObject>(true);
            if (owner == null || owner.gameObject == gameObject && !owner.isActiveAndEnabled) return;
            var r = GetComponent<Renderer>();
            // going away: only matters if we draw it; coming back: only matters if the owner is live and could draw it again
            if (!isActiveAndEnabled ? owner.Covers(r) : owner.isActiveAndEnabled) owner.PartActivityChanged();
        }
    }
}
