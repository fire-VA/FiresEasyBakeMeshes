using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.505 ([visual], piece snow caps v1; shape agreed by [seasons], approved by [2ic] and [lead] 11-23, Fire's "yes" to the piece snow redesign):
    // the season mod (Vedr) owns one snow cap mesh + material per piece prefab (EbmApi.SetPieceCapMesh); EBM only DRAWS it, once per baked piece of that
    // prefab, with the piece root's own matrix x the cap's capToRoot. EBM never edits, destroys or reads back the mesh or material.
    //  - Instanced path only ([2ic] MUST 1): EBM's indirect path draws only materials with an EBM procedural variant, and EBM never ships another mod's
    //    shader. Caps draw with Graphics.RenderMeshInstanced from their own per-zone lists (ZoneTracker.DrawCaps, once per frame after the group walk),
    //    every frame, whether the piece groups went indirect or not; same gate as the groups (ZoneActive, Baked, Bake.Ready).
    //  - Per instance: _SnowLevel = the piece's snow shape (EbmSnowStore.ShapeLookZ - 1; unset = NO cap); _VedrCapEdges / _VedrCapNbr = the join values
    //    the season mod pushes through EbmApi.SetCapSnow (never set = 0 / 0). Shadows as registered (Vedr passes off).
    //  - Hand-over ([2ic] MUST A + B): a live piece whose cap EBM draws gets its own cap renderers switched off (EasyBakeSuppressedVisuals.ClaimCaps) in
    //    the same rebuild that adds its cap instance; the list is checked against a signature before every submit and rebuilt there (same LateUpdate),
    //    so a hand-back (group.TryRemove bumps Version, SuppressedPieces.Remove changes the count) never submits a stale cap. RestoreVisuals unclaims in
    //    the same call (before PieceLeftBake). Combined-mesh pieces are never in a group, so never claimed.
    //  - Key [08 - Snow And Seasons] "Draw Snow Caps On Baked Pieces", default OFF (new keys default off). Off = nothing drawn, every claim given back.
    //    Nothing registered = one count check per frame.
    internal static class PieceCaps
    {
        internal static ConfigEntry<bool> Key;
        internal static bool Active => Key != null && Key.Value && !EbmApi.Inert && FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled != null && FiresEasyBakeMeshesPlugin.BatchingInstancingEnabled.Value;

        private const int MaxPerDraw = 1023;
        internal static readonly int SnowLevelId = Shader.PropertyToID("_SnowLevel");
        internal static readonly int EdgesId = Shader.PropertyToID("_VedrCapEdges");
        internal static readonly int NbrId = Shader.PropertyToID("_VedrCapNbr");

        internal static void Bind(ConfigFile config)
        {
            Key = EbmConfigMove.Bind(config, "08 - Snow And Seasons", "Draw Snow Caps On Baked Pieces", false,
                "Baked buildings draw the snow caps your season mod (Vedr) gives them, the same caps the live pieces show. Off = baked buildings show no season-mod caps (as before).");
            Key.SettingChanged += (_, __) =>
            {
                Revision++; RebuildGen++;
                if (!Key.Value) { UnclaimWhere(-1, "key off"); SkipEligibility.CapsCoverChanged("Draw Snow Caps On Baked Pieces off"); }   // 1.2.514
                EasyBakeLog.Info($"[Caps] Draw Snow Caps On Baked Pieces {(Key.Value ? "on" : "off")} ({FiresEasyBakeMeshesPlugin.PluginVersion})");
            };
        }

        // ── registrations (EbmApi.SetPieceCapMesh / ClearPieceCaps) ──
        internal sealed class Cap
        {
            public string Owner, Prefab; public int Hash; public Mesh Mesh; public Matrix4x4 CapToRoot; public Material Material; public bool Shadows;
            public Bounds MeshBounds;
            // 1.2.508 (.41 tilted caps): the prefab's variants 1..32 (same material / shadows); VariantGen bumps on any variant change (that prefab's lists rebuild)
            public Dictionary<int, Variant> Variants; public int VariantGen;
        }
        internal sealed class Variant { public Mesh Mesh; public Matrix4x4 CapToRoot; public Bounds MeshBounds; }
        private static readonly Dictionary<int, Cap> s_caps = new Dictionary<int, Cap>();
        internal static int Revision;   // any registration change, key flip: every zone re-checks WHICH lists it has (cheap); a list rebuilds only if its own cap changed
        // 1.2.507 ([lead] proof34: 65,071 rebuilds / 60 s): the one global "rebuild every list" count, bumped only by the key turning on and CapsInactive (re-claim
        // after a give-back-all). Everything else that changes is per group (Version / Count / CapAnnounceGen = rebuild; ShapeGen / CapJoinGen = values refresh).
        internal static int RebuildGen;
        private const float ValuesRefreshSeconds = 0.5f;   // a list's snow level / join values follow at most twice a second (structural changes: the same frame)
        internal static bool AnyRegistered => s_caps.Count > 0;
        private static bool s_saidSet, s_saidClear, s_saidActive, s_saidDrawn, s_saidDraws;
        internal static int DrawnLastFrame;
        private static int s_drawnFrame;

        internal static bool Set(string owner, string prefab, Mesh mesh, Matrix4x4 capToRoot, Material material, bool castShadows)
        {
            if (EbmApi.Inert || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(prefab)) return false;
            int hash = prefab.GetStableHashCode();   // [2ic] SHOULD a: the same hash as ZoneInstanceGroup.PrefabHash / ZDO.GetPrefab
            if (s_caps.TryGetValue(hash, out var held) && held.Owner != owner)   // [2ic] SHOULD b: no silent steal
            {
                EasyBakeLog.Warn($"[Caps] '{owner}' asked for the cap of '{prefab}', which '{held.Owner}' holds: refused (1.2.505)");
                return false;
            }
            if (mesh == null)   // remove
            {
                if (held == null) return true;
                s_caps.Remove(hash); Revision++;
                UnclaimWhere(hash, "cap removed");
                SkipEligibility.CapsCoverChanged($"cap of '{prefab}' removed");   // 1.2.514
                return true;
            }
            if (material == null) return false;
            if (!RigidTransform(capToRoot))   // [2ic] SHOULD: a bad matrix never draws
            {
                EasyBakeLog.Warn($"[Caps] '{owner}' cap for '{prefab}': capToRoot is not a rigid transform (scale, shear, mirror or NaN), refused (1.2.511)");
                return false;
            }
            if (!s_saidSet) { s_saidSet = true; EasyBakeLog.Info($"[Caps] first SetPieceCapMesh from '{owner}' ('{prefab}'); key Draw Snow Caps On Baked Pieces is {(Key != null && Key.Value ? "on" : "off")} (1.2.505)"); }
            // 1.2.507: the same registration again changes nothing (no regroup, no rebuild)
            if (held != null && ReferenceEquals(held.Mesh, mesh) && ReferenceEquals(held.Material, material) && held.CapToRoot == capToRoot && held.Shadows == castShadows) { c_sameSets++; return true; }
            if (held == null) s_newKinds.Add(hash);   // 1.2.514 ([2ic] SHOULD): a NEW kind: its live pieces kept real for winter snow may skip now (ZoneTracker.RetryNewCapKinds)
            s_caps[hash] = new Cap { Owner = owner, Prefab = prefab, Hash = hash, Mesh = mesh, CapToRoot = capToRoot, Material = material, Shadows = castShadows, MeshBounds = mesh.bounds,
                Variants = held != null ? held.Variants : null, VariantGen = held != null ? held.VariantGen + 1 : 0 };   // 1.2.508: a replaced base keeps its variants
            Revision++;
            return true;
        }

        // 1.2.508 (EbmApi.SetPieceCapVariant, [seasons] .41): variant 1..32 of a prefab whose base cap this owner registered; mesh null removes it.
        private static bool s_saidVariant;
        internal static bool SetVariant(string owner, string prefab, int variant, Mesh mesh, Matrix4x4 capToRoot)
        {
            if (EbmApi.Inert || string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(prefab) || variant < 1 || variant > 32) return false;
            if (!s_caps.TryGetValue(prefab.GetStableHashCode(), out var cap) || cap.Owner != owner) return false;   // base first, same owner
            if (mesh == null)
            {
                if (cap.Variants == null || !cap.Variants.Remove(variant)) return true;
                cap.VariantGen++;
                return true;
            }
            if (!RigidTransform(capToRoot)) { EasyBakeLog.Warn($"[Caps] '{owner}' cap variant {variant} for '{prefab}': capToRoot is not a rigid transform (scale, shear, mirror or NaN), refused (1.2.511)"); return false; }
            if (cap.Variants == null) cap.Variants = new Dictionary<int, Variant>();
            if (cap.Variants.TryGetValue(variant, out var had) && ReferenceEquals(had.Mesh, mesh) && had.CapToRoot == capToRoot) { c_sameSets++; return true; }
            cap.Variants[variant] = new Variant { Mesh = mesh, CapToRoot = capToRoot, MeshBounds = mesh.bounds };
            cap.VariantGen++;
            if (!s_saidVariant) { s_saidVariant = true; EasyBakeLog.Info($"[Caps] first SetPieceCapVariant from '{owner}' ('{prefab}' variant {variant}) (1.2.508)"); }
            return true;
        }

        internal static void Clear(string owner)
        {
            if (string.IsNullOrEmpty(owner) || s_caps.Count == 0) return;
            if (!s_saidClear) { s_saidClear = true; EasyBakeLog.Info($"[Caps] first ClearPieceCaps from '{owner}' (1.2.505)"); }
            s_dropScratch.Clear();
            foreach (var kv in s_caps) if (kv.Value.Owner == owner) s_dropScratch.Add(kv.Key);
            if (s_dropScratch.Count == 0) return;
            for (int i = 0; i < s_dropScratch.Count; i++) { s_caps.Remove(s_dropScratch[i]); UnclaimWhere(s_dropScratch[i], "owner cleared"); }
            s_dropScratch.Clear();
            Revision++;
            SkipEligibility.CapsCoverChanged($"'{owner}' cleared its caps");   // 1.2.514
        }

        /// <summary>1.2.514: EBM draws this kind's snow cap (caps active, a cap registered for the prefab hash), so a baked piece of it needs no live object
        /// for its winter snow cap (SkipEligibility.CapsCoverWinter).</summary>
        internal static bool Registered(int prefabHash) => s_caps.ContainsKey(prefabHash);   // 1.2.514 v4: the "declared, no mesh" tally (SkipEligibility)
        internal static bool CoversWinter(int prefabHash) => s_caps.Count > 0 && Active && s_caps.ContainsKey(prefabHash);
        // 1.2.514: kinds registered since the last retry pass (drained by ZoneTracker.RetryNewCapKinds at most twice a second)
        internal static readonly HashSet<int> s_newKinds = new HashSet<int>();
        internal static int RetriedPieces, RetriedPasses;
        private static readonly List<int> s_dropScratch = new List<int>();

        internal static bool PieceCapsActive
        {
            get
            {
                bool on = Active;
                if (!s_saidActive) { s_saidActive = true; EasyBakeLog.Info($"[Caps] first PieceCapsActive read: {on} (1.2.505)"); }
                return on;
            }
        }

        internal static int PieceCapsDrawn
        {
            get
            {
                if (!s_saidDrawn) { s_saidDrawn = true; EasyBakeLog.Info("[Caps] first PieceCapsDrawn read (1.2.505)"); }
                return s_drawnFrame == Time.frameCount - 1 || s_drawnFrame == Time.frameCount ? DrawnLastFrame : 0;
            }
        }

        /// <summary>EbmApi.DrawsPieceCap: EBM has claimed this live piece's own cap renderers (it draws its cap). Main thread.</summary>
        internal static bool DrawsCap(GameObject piece)
        {
            if (!s_saidDraws) { s_saidDraws = true; EasyBakeLog.Info("[Caps] first DrawsPieceCap call (1.2.505)"); }
            if (piece == null) return false;
            var s = piece.GetComponent<EasyBakeSuppressedVisuals>();
            return s != null && s.CapClaimed && !s.Restored;
        }

        // 1.2.511 ([seasons] .45/.46: a baked roof cap must keep the game cap's own slope frame, so its joined rims take the slope as their normal, as live
        // caps do): capToRoot may now carry ANY rotation (pitch / roll too), still no scale, shear, mirror or projection, and every entry finite. The
        // check was never load-bearing for the draw: the instance matrix is root x capToRoot (any rigid matrix composes), and each run's bounds carry the
        // cap mesh box through the full matrix (FillRun's |m| reach), so a rotated cap is culled right. Was TranslationYawOnly (1.2.505).
        private static bool RigidTransform(Matrix4x4 m)
        {
            const float eps = 1e-3f;
            for (int i = 0; i < 16; i++) if (float.IsNaN(m[i]) || float.IsInfinity(m[i])) return false;   // NaN fails every comparison below: refuse it first
            Vector3 x = m.GetColumn(0), y = m.GetColumn(1), z = m.GetColumn(2);
            // unit axes (no scale), mutually perpendicular (no shear), right-handed (det +1: no mirror)
            if (Mathf.Abs(x.magnitude - 1f) > eps || Mathf.Abs(y.magnitude - 1f) > eps || Mathf.Abs(z.magnitude - 1f) > eps) return false;
            if (Mathf.Abs(Vector3.Dot(x, y)) > eps || Mathf.Abs(Vector3.Dot(y, z)) > eps || Mathf.Abs(Vector3.Dot(z, x)) > eps) return false;
            if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) return false;
            return Mathf.Abs(m.m30) <= eps && Mathf.Abs(m.m31) <= eps && Mathf.Abs(m.m32) <= eps && Mathf.Abs(m.m33 - 1f) <= eps;
        }

        // Gives back every claim on prefab hash (-1 = all). Walks the live suppressions (EasyBakeSuppressedVisuals.All); runs only on a registration
        // removal / owner clear / key off, never per frame.
        private static readonly List<EasyBakeSuppressedVisuals> s_unclaimScratch = new List<EasyBakeSuppressedVisuals>();
        internal static void UnclaimWhere(int prefabHash, string why)
        {
            s_unclaimScratch.Clear();
            foreach (var s in EasyBakeSuppressedVisuals.All)
                if (s != null && s.CapClaimed && (prefabHash == -1 || (s.CapList is CapList l && l.Hash == prefabHash))) s_unclaimScratch.Add(s);
            for (int i = 0; i < s_unclaimScratch.Count; i++) s_unclaimScratch[i].UnclaimCaps();
            if (s_unclaimScratch.Count > 0) EasyBakeLog.Debug($"[Caps] {why}: {s_unclaimScratch.Count} live piece cap(s) given back");
            s_unclaimScratch.Clear();
        }

        // ── per zone: one CapList per capped prefab, kept on the bake (MeshBaker.BakeResult.Caps), so it goes with the bake ──
        internal sealed class ZoneCaps
        {
            public int Revision = -1, GroupCount = -1;
            public readonly List<CapList> Lists = new List<CapList>();
            public readonly Dictionary<MeshBaker.PieceIdentity, EasyBakeSuppressedVisuals> Live = new Dictionary<MeshBaker.PieceIdentity, EasyBakeSuppressedVisuals>();
            public int LiveCount = -1, LiveChanges = -1;
            public bool Roof;
        }

        internal sealed class Run { public int Start, N; public Mesh Mesh; public Bounds Bounds; public MaterialPropertyBlock Block; public float[] Level; public Vector4[] Edges, Nbr; }

        internal sealed class CapList
        {
            public int Hash; public Cap Cap; public bool Roof;
            public readonly List<ZoneInstanceGroup> Groups = new List<ZoneInstanceGroup>();
            // signature ([2ic] MUST A): STRUCTURAL parts rebuilt before submit when any differs (1.2.507: all per group / per list, plus RebuildGen + ClearGen);
            // VALUES parts (ShapeGen, CapJoinGen) refresh the per-instance values at most every ValuesRefreshSeconds (a piece whose cap appears / goes = a rebuild)
            public int SigRev = -1, SigVer, SigGroupIds, SigSupp = -1, SigClear = -1, SigGroups = -1, SigShape, SigJoin; public float SigCell = -1f;
            public int SigSuppRem = -1;   // 1.2.509 ([2ic] MUST): the bake's SuppressRemovals at the last look; any removal = a full rebuild, never an in-place claim
            public int SigAnn, SigVar;   // 1.2.508: the announce and variant parts apart from the pieces part (rebuild reasons in the [Caps] line)
            public readonly HashSet<MeshBaker.PieceIdentity> IdSet = new HashSet<MeshBaker.PieceIdentity>();   // 1.2.509: the pieces this list draws a cap for (in-place claims)
            public bool ValuesPending; public float NextValuesAt; public int ShapeCountAtBuild = -1;
            public int Build;
            public readonly List<Matrix4x4> M = new List<Matrix4x4>();
            public readonly List<MeshBaker.PieceIdentity> Ids = new List<MeshBaker.PieceIdentity>();   // 1.2.507: per instance, for the values refresh
            public readonly List<ZoneInstanceGroup> Src = new List<ZoneInstanceGroup>();
            public readonly List<Vector3> Piv = new List<Vector3>();
            public readonly List<int> V = new List<int>();   // 1.2.508: per instance, 0 = base cap, 1..32 = variant
            public readonly List<float> Level = new List<float>();
            public readonly List<Vector4> Edges = new List<Vector4>(), Nbr = new List<Vector4>();
            public readonly List<Run> Runs = new List<Run>();
            public readonly List<EasyBakeSuppressedVisuals> Claimed = new List<EasyBakeSuppressedVisuals>();
            public float LastDrawnAt;
        }

        // counters (60 s Debug line)
        private static int c_frames, c_instances, c_calls, c_rebuilds, c_rebuiltInstances, c_refreshes, c_refreshInstances, c_sameSets;
        private static long c_refreshTicks;
        private static int c_whyForced, c_whyPieces, c_whyAnnounce, c_whyVariant, c_whySupp, c_whyOther, c_whyEscalated;   // 1.2.508: rebuild reasons
        private static int c_incAppends, c_incClaims;   // 1.2.509: appended suppressions handled in place, live caps claimed by them
        // 1.2.509 ([lead]: split the next stall between Vedr and EBM by numbers): the cap API entry points, calls / total / worst frame per 60 s (Debug line)
        internal const int ApiVariant = 0, ApiOverride = 1, ApiCapSnow = 2, ApiDraws = 3, ApiCapLevel = 4, ApiN = 5;   // 1.2.510: + SetPieceCapLevel
        private static readonly string[] s_apiName = { "SetPieceCapVariant", "SetPieceCapOverride", "SetCapSnow", "DrawsPieceCap", "SetPieceCapLevel" };
        private static readonly int[] s_apiCalls = new int[ApiN]; private static readonly long[] s_apiTicks = new long[ApiN];
        private static long s_apiFrameTicks, s_apiWorstFrameTicks; private static int s_apiFrame = -1, s_apiWorstFrameCalls, s_apiFrameCalls;
        internal static void NoteApi(int which, long t0)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            s_apiCalls[which]++; s_apiTicks[which] += dt;
            int f = Time.frameCount;
            if (f != s_apiFrame) { s_apiFrame = f; s_apiFrameTicks = 0; s_apiFrameCalls = 0; }
            s_apiFrameTicks += dt; s_apiFrameCalls++;
            if (s_apiFrameTicks > s_apiWorstFrameTicks) { s_apiWorstFrameTicks = s_apiFrameTicks; s_apiWorstFrameCalls = s_apiFrameCalls; }
        }
        private static string ApiLine(double tick)
        {
            var sb = new System.Text.StringBuilder("cap API (all callers): ");
            for (int i = 0; i < ApiN; i++) sb.Append(s_apiName[i]).Append(' ').Append(s_apiCalls[i]).Append(" call(s) ").Append((s_apiTicks[i] * tick).ToString("0.0")).Append(" ms; ");
            sb.Append("worst frame ").Append((s_apiWorstFrameTicks * tick).ToString("0.0")).Append(" ms in ").Append(s_apiWorstFrameCalls).Append(" call(s)");
            return sb.ToString();
        }
        private static void ResetApi() { for (int i = 0; i < ApiN; i++) { s_apiCalls[i] = 0; s_apiTicks[i] = 0; } s_apiWorstFrameTicks = 0; s_apiWorstFrameCalls = 0; }
        private static long c_rebuildTicks;
        private static float s_nextLine;

        internal static void BeginFrame() { DrawnLastFrame = 0; s_drawnFrame = Time.frameCount; c_frames++; s_deferredRoofs.Clear(); }

        /// <summary>ZoneTracker.DrawCaps, once per frame per drawing zone: refresh this bake's cap lists if anything changed, then submit them.</summary>
        internal static void DrawBake(MeshBaker.BakeResult bake, bool far)
        {
            var zc = bake.Caps as ZoneCaps;
            if (zc == null) { zc = new ZoneCaps(); bake.Caps = zc; }
            var groups = bake.InstanceGroups;
            if (zc.Revision != Revision || zc.GroupCount != groups.Count) RegroupZone(zc, groups);
            if (zc.Lists.Count == 0) return;
            for (int i = 0; i < zc.Lists.Count; i++)
            {
                var list = zc.Lists[i];
                if (StructureChanged(list, bake, out bool suppOnly)) { if (!(suppOnly && ClaimAppended(list, bake, zc))) Rebuild(list, bake, zc); }   // 1.2.509: appended suppressions = an in-place claim
                else if (ValuesChanged(list) && !RefreshValues(list)) { c_whyEscalated++; Rebuild(list, bake, zc); }   // 1.2.507: values only; a cap that appears / goes escalates
                if (list.M.Count == 0) continue;
                list.LastDrawnAt = Time.unscaledTime;
                if (list.Roof && InstancedDraw.RoofHidingActive) { s_deferredRoofs.Add(list); continue; }   // per camera, InstancedDraw.DrawRoofsFor
                Submit(list, null);
            }
        }

        private static void RegroupZone(ZoneCaps zc, List<ZoneInstanceGroup> groups)
        {
            // which capped prefabs this bake holds, and their groups (a prefab can bake into several groups: one per wear look)
            var old = s_regroupOld; old.Clear();
            for (int i = 0; i < zc.Lists.Count; i++) old[zc.Lists[i].Hash] = zc.Lists[i];
            zc.Lists.Clear();
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group == null || !s_caps.TryGetValue(group.PrefabHash, out var cap)) continue;
                CapList list = null;
                for (int i = 0; i < zc.Lists.Count; i++) if (zc.Lists[i].Hash == group.PrefabHash) { list = zc.Lists[i]; break; }
                if (list == null)
                {
                    if (!old.TryGetValue(group.PrefabHash, out list)) list = new CapList { Hash = group.PrefabHash };
                    old.Remove(group.PrefabHash);
                    list.Groups.Clear();
                    if (!ReferenceEquals(list.Cap, cap)) { list.Cap = cap; list.Roof = EbmRoofs.IsRoofPrefab(group.PrefabHash); list.SigRev = -1; }   // 1.2.507: rebuild only a new / changed cap (was every list at every regroup)
                    zc.Lists.Add(list);
                }
                list.Groups.Add(group);
            }
            // a prefab no longer capped (or gone from this bake): its claims go back now
            foreach (var kv in old) GiveBack(kv.Value);
            old.Clear();
            zc.Revision = Revision; zc.GroupCount = groups.Count;
        }
        private static readonly Dictionary<int, CapList> s_regroupOld = new Dictionary<int, CapList>();

        private static void GiveBack(CapList list)
        {
            for (int i = 0; i < list.Claimed.Count; i++) { var s = list.Claimed[i]; if (s != null && s.CapList == list) s.UnclaimCaps(); }
            list.Claimed.Clear(); list.M.Clear(); list.Runs.Clear();
            list.Ids.Clear(); list.Src.Clear(); list.Piv.Clear(); list.V.Clear(); list.SigRev = -1;   // 1.2.507: a reused list rebuilds
        }

        // 1.2.507: STRUCTURE = which pieces get a cap, where, and which live caps are claimed. Per group: Version + Count (pieces joined / left / reordered,
        // HandBack's TryRemove), CapAnnounceGen (this zone announced / unloaded / values dropped); the group set itself; the bake's suppressions; the cell
        // size; RebuildGen / ClearGen (rare globals). SigRev = -1 forces one (a new / changed cap). No per-frame global (proof34: one was bumped every frame).
        private static bool StructureChanged(CapList list, MeshBaker.BakeResult bake, out bool suppAppendOnly)
        {
            suppAppendOnly = false;
            int ver = 0, ids = 0, ann = 0, vg = 0;
            unchecked
            {
                for (int i = 0; i < list.Groups.Count; i++)
                {
                    var g = list.Groups[i];
                    ver = ver * 31 + g.Version * 7 + g.Count * 3;
                    ann = ann * 31 + g.CapAnnounceGen;
                    vg = vg * 31 + g.CapVariantGen;   // 1.2.508: variant overrides
                    ids = ids * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g);
                }
                ver = ver * 31 + RebuildGen;
                vg = vg * 31 + (list.Cap != null ? list.Cap.VariantGen : 0);   // 1.2.508: a variant registered / removed
            }
            float cell = ZoneInstanceGroup.CellSize;
            int supp = bake.SuppressedPieces != null ? bake.SuppressedPieces.Count : 0;
            int rem = bake.SuppressRemovals;
            if (list.SigRev == 0 && list.SigVer == ver && list.SigAnn == ann && list.SigVar == vg && list.SigGroupIds == ids && list.SigSupp == supp && list.SigSuppRem == rem && list.SigClear == EbmSnowStore.ClearGen
                && list.SigGroups == list.Groups.Count && list.SigCell == cell) return false;
            // 1.2.509 ([lead] full41s: rebuild reason "suppressions 2315"): ONLY the bake's suppression list grew, with NO removal since the last look (SuppressRemovals unchanged; [2ic] MUST: the BakeCensus fix removes without a Version bump) (pieces re-created and silenced while arriving,
            // MeshBaker.DisableForCacheHit appends) = the caller claims the new ones in place, no rebuild. Any removal bumps the bake's SuppressRemovals (every Remove site) and takes the full rebuild.
            if (list.SigRev == 0 && list.SigVer == ver && list.SigAnn == ann && list.SigVar == vg && list.SigGroupIds == ids && list.SigClear == EbmSnowStore.ClearGen
                && list.SigGroups == list.Groups.Count && list.SigCell == cell && list.SigSuppRem == rem && supp > list.SigSupp && list.SigSupp >= 0) { suppAppendOnly = true; return true; }
            // 1.2.508: count the first reason that applies ([lead]: which change drives the rebuilds)
            if (list.SigRev != 0) c_whyForced++; else if (list.SigVer != ver) c_whyPieces++; else if (list.SigAnn != ann) c_whyAnnounce++; else if (list.SigVar != vg) c_whyVariant++;
            else if (list.SigSupp != supp || list.SigSuppRem != rem) c_whySupp++; else c_whyOther++;
            list.SigSuppRem = rem;
            list.SigAnn = ann; list.SigVar = vg;
            list.SigRev = 0; list.SigVer = ver; list.SigGroupIds = ids; list.SigSupp = supp; list.SigClear = EbmSnowStore.ClearGen;
            list.SigGroups = list.Groups.Count; list.SigCell = cell;
            return true;
        }

        // 1.2.509: the suppressions appended since this list's last look: a live piece this list already draws a cap for gets its own cap claimed now (the same claim a
        // rebuild makes, so the hand-over rule holds); the zone's identity map takes the new entries. False = something does not fit an append (a null / restored entry,
        // the list shrank) -> the caller rebuilds as before. Cost: the new entries only.
        private static bool ClaimAppended(CapList list, MeshBaker.BakeResult bake, ZoneCaps zc)
        {
            var supp = bake.SuppressedPieces;
            int from = list.SigSupp, to = supp != null ? supp.Count : 0;
            list.SigSupp = to;   // taken either way: on false the caller's rebuild reads every suppression fresh (no retry loop)
            if (supp == null || from < 0 || to < from) return false;
            for (int i = from; i < to; i++)
            {
                var s = supp[i];
                if (s == null || s.Restored) return false;
                var id = MeshBaker.PieceIdentity.From(s.gameObject, s.transform.position);
                if (zc.LiveCount == from) zc.Live[id] = s;
                if (!list.IdSet.Contains(id)) continue;
                s.ClaimCaps(list, list.Build);
                list.Claimed.Add(s);
                c_incClaims++;
            }
            if (zc.LiveCount == from) { zc.LiveCount = to; zc.LiveChanges = EasyBakeSuppressedVisuals.Changes; }
            c_incAppends++;
            return true;
        }

        private static void ValuesSig(CapList list, out int shape, out int join)
        {
            shape = 0; join = 0;
            // ShapeCount too: a value filled from the shape source (ZoneInstanceGroup.LookOf -> ShapeLookZ) bumps no ShapeGen, but may add a cap
            unchecked { for (int i = 0; i < list.Groups.Count; i++) { var g = list.Groups[i]; shape = shape * 31 + g.ShapeGen + g.ShapeCount * 17; join = join * 31 + g.CapJoinGen; } }
        }

        // VALUES = the per-instance snow level (ShapeGen) and join (CapJoinGen) of the list's groups. True once a change is DUE (at most every ValuesRefreshSeconds
        // per list); a change seen early waits (the list keeps drawing its last values) and is taken at the next due frame.
        private static bool ValuesChanged(CapList list)
        {
            ValuesSig(list, out int shape, out int join);
            if (shape != list.SigShape || join != list.SigJoin) { list.SigShape = shape; list.SigJoin = join; list.ValuesPending = true; }
            if (!list.ValuesPending) return false;
            float now = Time.unscaledTime;
            if (now < list.NextValuesAt) return false;
            list.NextValuesAt = now + ValuesRefreshSeconds;
            list.ValuesPending = false;
            return true;
        }

        // The per-instance values re-read in place (no claims, no sort, no matrices): level from the shape store, edges / neighbours from the join store, every
        // run's arrays refilled. False = a piece's cap would appear or go (its shape became unset) -> the caller rebuilds instead.
        private static bool RefreshValues(CapList list)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (ShapeCountOf(list) != list.ShapeCountAtBuild) return false;   // a piece got its first shape value since the build: its cap appears = a rebuild
            int n = list.M.Count;
            for (int i = 0; i < n; i++)
            {
                float z = EbmSnowStore.ShapeLookZ(list.Src[i], list.Ids[i], list.Piv[i]);
                if (z <= 0f) return false;
                list.Level[i] = EbmSnowStore.TryGetCapLevel(list.Ids[i], out var cl) ? cl : z - 1f;   // 1.2.510: the cap's own level when the owner sent one
                EbmSnowStore.TryGetCapJoin(list.Ids[i], out var e, out var nb);
                list.Edges[i] = e; list.Nbr[i] = nb;
            }
            for (int r = 0; r < list.Runs.Count; r++) RefillValues(list, list.Runs[r]);
            c_refreshes++; c_refreshInstances += n;
            c_refreshTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return true;
        }

        private static int ShapeCountOf(CapList list) { int c = 0; for (int i = 0; i < list.Groups.Count; i++) c += list.Groups[i].ShapeCount; return c; }

        private static void RefillValues(CapList list, Run run)
        {
            for (int i = 0; i < run.N; i++) { run.Level[i] = list.Level[run.Start + i]; run.Edges[i] = list.Edges[run.Start + i]; run.Nbr[i] = list.Nbr[run.Start + i]; }
            run.Block.SetFloatArray(SnowLevelId, run.Level);
            run.Block.SetVectorArray(EdgesId, run.Edges);
            run.Block.SetVectorArray(NbrId, run.Nbr);
        }

        private static readonly HashSet<MeshBaker.PieceIdentity> s_seen = new HashSet<MeshBaker.PieceIdentity>();
        private static readonly List<EasyBakeSuppressedVisuals> s_claimedBefore = new List<EasyBakeSuppressedVisuals>();

        private static void Rebuild(CapList list, MeshBaker.BakeResult bake, ZoneCaps zc)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            list.Build++;
            // the bake's live suppressed pieces by identity, refreshed when the bake's list changed size or any suppression was added / destroyed
            // (a HandBack + re-join in one frame keeps the count but swaps the component: EasyBakeSuppressedVisuals.Changes moves on both)
            var supp = bake.SuppressedPieces;
            int suppCount = supp != null ? supp.Count : 0;
            if (zc.LiveCount != suppCount || zc.LiveChanges != EasyBakeSuppressedVisuals.Changes)
            {
                zc.LiveChanges = EasyBakeSuppressedVisuals.Changes;
                zc.Live.Clear();
                for (int i = 0; i < suppCount; i++)
                {
                    var s = supp[i];
                    if (s == null || s.Restored) continue;
                    zc.Live[MeshBaker.PieceIdentity.From(s.gameObject, s.transform.position)] = s;
                }
                zc.LiveCount = suppCount;
            }

            s_claimedBefore.Clear(); s_claimedBefore.AddRange(list.Claimed);
            list.Claimed.Clear(); list.M.Clear(); list.Level.Clear(); list.Edges.Clear(); list.Nbr.Clear();
            list.Ids.Clear(); list.Src.Clear(); list.Piv.Clear(); list.V.Clear();
            s_seen.Clear();
            var cap = list.Cap;
            for (int g = 0; g < list.Groups.Count; g++)
            {
                var group = list.Groups[g];
                if (group.Definition == null) continue;
                var toRoot = group.Definition.LocalOffset.inverse;   // group matrices are pieceLocalToWorld x LocalOffset (ZoneInstanceGroup.Add)
                var ms = group.Matrices; var ids = group.Identities;
                int n = Mathf.Min(ms.Count, ids.Count);
                for (int i = 0; i < n; i++)
                {
                    var id = ids[i];
                    if (!s_seen.Add(id)) continue;   // exactly once per piece, whatever groups it is in
                    var m = ms[i];
                    var piv = new Vector3(m.m03, m.m13, m.m23);
                    float z = EbmSnowStore.ShapeLookZ(group, id, piv);   // the pivot, as ZoneInstanceGroup.LookOf
                    if (z <= 0f) continue;   // no shape value yet: no cap (its own cap, if live, stays on)
                    // 1.2.508: the piece's variant (-1 = no cap; a variant not registered yet = no cap): no instance, no claim (its live cap stays the owner's)
                    int v = EbmSnowStore.CapVariantOf(id);
                    if (v < 0) continue;
                    Matrix4x4 c2r = cap.CapToRoot;
                    if (v > 0) { if (cap.Variants == null || !cap.Variants.TryGetValue(v, out var vr) || vr.Mesh == null) continue; c2r = vr.CapToRoot; }
                    if (zc.Live.TryGetValue(id, out var s) && s != null && !s.Restored)
                    {
                        s.ClaimCaps(list, list.Build);
                        list.Claimed.Add(s);
                    }
                    list.M.Add(m * toRoot * c2r);
                    list.Level.Add(EbmSnowStore.TryGetCapLevel(id, out var cl) ? cl : z - 1f);   // 1.2.510: the cap's own level when the owner sent one
                    EbmSnowStore.TryGetCapJoin(id, out var e, out var nb);
                    list.Edges.Add(e); list.Nbr.Add(nb);
                    list.Ids.Add(id); list.Src.Add(group); list.Piv.Add(piv); list.V.Add(v);
                }
            }
            // claims this build did not confirm go back now (shape dropped, piece left the group)
            for (int i = 0; i < s_claimedBefore.Count; i++)
            {
                var s = s_claimedBefore[i];
                if (s != null && s.CapClaimed && s.CapList == list && s.CapListBuild != list.Build) s.UnclaimCaps();
            }
            s_claimedBefore.Clear();
            list.IdSet.Clear(); for (int i = 0; i < list.Ids.Count; i++) list.IdSet.Add(list.Ids[i]);   // 1.2.509
            list.ShapeCountAtBuild = ShapeCountOf(list);   // 1.2.507: after the loop (ShapeLookZ may have filled values from the shape source)
            ValuesSig(list, out list.SigShape, out list.SigJoin); list.ValuesPending = false;   // every value was just read fresh
            BuildRuns(list);
            c_rebuilds++; c_rebuiltInstances += list.M.Count;
            c_rebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        // cells as the groups ([Optimize] InstancedCellSizeMeters > 0: ordered by x/z cell, one run per cell), else 1023-instance chunks; each run's
        // bounds = the cap mesh bounds carried through every instance matrix ([2ic] SHOULD d)
        private static long[] s_keys = new long[0]; private static int[] s_order = new int[0];
        private static readonly List<Matrix4x4> s_sM = new List<Matrix4x4>(); private static readonly List<float> s_sL = new List<float>();
        private static readonly List<Vector4> s_sE = new List<Vector4>(), s_sN = new List<Vector4>();
        private static readonly List<MeshBaker.PieceIdentity> s_sI = new List<MeshBaker.PieceIdentity>(); private static readonly List<ZoneInstanceGroup> s_sS = new List<ZoneInstanceGroup>();
        private static readonly List<Vector3> s_sP = new List<Vector3>();
        private sealed class KeyOrder : IComparer<int> { internal long[] Keys; internal int[] Var; public int Compare(int a, int b) { int c = Var[a].CompareTo(Var[b]); if (c != 0) return c; c = Keys[a].CompareTo(Keys[b]); return c != 0 ? c : a.CompareTo(b); } }   // 1.2.508: variant first (one mesh per run)
        private static int[] s_vkeys = new int[0];
        private static readonly List<int> s_sV = new List<int>();
        private static readonly KeyOrder s_keyOrder = new KeyOrder();

        private static void BuildRuns(CapList list)
        {
            int n = list.M.Count;
            float cell = ZoneInstanceGroup.CellSize;
            bool anyVar = false; for (int i = 0; i < n && !anyVar; i++) anyVar = list.V[i] != 0;   // 1.2.508
            if ((cell > 0f || anyVar) && n > 1)
            {
                if (s_keys.Length < n) { s_keys = new long[Mathf.NextPowerOfTwo(n)]; s_order = new int[s_keys.Length]; s_vkeys = new int[s_keys.Length]; }
                for (int i = 0; i < n; i++) { var m = list.M[i]; s_keys[i] = cell > 0f ? Key2(m.m03, m.m23, cell) : 0L; s_vkeys[i] = list.V[i]; s_order[i] = i; }
                s_keyOrder.Keys = s_keys; s_keyOrder.Var = s_vkeys; System.Array.Sort(s_order, 0, n, s_keyOrder); s_keyOrder.Keys = null; s_keyOrder.Var = null;
                s_sM.Clear(); s_sL.Clear(); s_sE.Clear(); s_sN.Clear(); s_sI.Clear(); s_sS.Clear(); s_sP.Clear(); s_sV.Clear();
                for (int i = 0; i < n; i++)
                {
                    int j = s_order[i];
                    s_sM.Add(list.M[j]); s_sL.Add(list.Level[j]); s_sE.Add(list.Edges[j]); s_sN.Add(list.Nbr[j]);
                    s_sI.Add(list.Ids[j]); s_sS.Add(list.Src[j]); s_sP.Add(list.Piv[j]);   // 1.2.507: carried with the instance (values refresh)
                    s_sV.Add(list.V[j]);   // 1.2.508
                }
                list.M.Clear(); list.M.AddRange(s_sM); list.Level.Clear(); list.Level.AddRange(s_sL);
                list.Edges.Clear(); list.Edges.AddRange(s_sE); list.Nbr.Clear(); list.Nbr.AddRange(s_sN);
                list.Ids.Clear(); list.Ids.AddRange(s_sI); list.Src.Clear(); list.Src.AddRange(s_sS); list.Piv.Clear(); list.Piv.AddRange(s_sP);
                list.V.Clear(); list.V.AddRange(s_sV);
                s_sM.Clear(); s_sL.Clear(); s_sE.Clear(); s_sN.Clear(); s_sI.Clear(); s_sS.Clear(); s_sP.Clear(); s_sV.Clear();
            }
            int r = 0, start = 0;
            while (start < n)
            {
                int end = start + 1;
                long key = cell > 0f ? Key2(list.M[start].m03, list.M[start].m23, cell) : 0L;
                while (end < n && end - start < MaxPerDraw && list.V[end] == list.V[start] && (cell <= 0f || Key2(list.M[end].m03, list.M[end].m23, cell) == key)) end++;   // 1.2.508: a run never mixes variants
                if (r >= list.Runs.Count) list.Runs.Add(new Run());
                FillRun(list, list.Runs[r], start, end - start);
                r++; start = end;
            }
            if (list.Runs.Count > r) list.Runs.RemoveRange(r, list.Runs.Count - r);
        }

        private static long Key2(float x, float z, float cell)
        {
            long cx = (long)Mathf.Floor(x / cell), cz = (long)Mathf.Floor(z / cell);
            return (cx << 32) ^ (cz & 0xFFFFFFFFL);
        }

        private static int SizeClass(int n) => n <= 8 ? 8 : Mathf.Min(MaxPerDraw, Mathf.NextPowerOfTwo(n));   // as ZoneInstanceGroup.SizeClass

        private static void FillRun(CapList list, Run run, int start, int n)
        {
            run.Start = start; run.N = n;
            int rv = list.V[start];   // 1.2.508: the run's one variant: its mesh and bounds
            Variant rvar = null;
            if (rv > 0 && list.Cap.Variants != null) list.Cap.Variants.TryGetValue(rv, out rvar);
            run.Mesh = rvar != null ? rvar.Mesh : list.Cap.Mesh;
            int size = SizeClass(n);
            if (run.Block == null || run.Level == null || run.Level.Length != size)   // Unity sizes an MPB array once: a new block per size class
            {
                run.Block = new MaterialPropertyBlock();
                run.Level = new float[size]; run.Edges = new Vector4[size]; run.Nbr = new Vector4[size];
            }
            var lb = rvar != null ? rvar.MeshBounds : list.Cap.MeshBounds; var e = lb.extents;
            Bounds b = default;
            for (int i = 0; i < n; i++)
            {
                var m = list.M[start + i];
                run.Level[i] = list.Level[start + i]; run.Edges[i] = list.Edges[start + i]; run.Nbr[i] = list.Nbr[start + i];
                var c = m.MultiplyPoint3x4(lb.center);
                var reach = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                if (i == 0) b = new Bounds(c, reach * 2f); else { b.Encapsulate(c + reach); b.Encapsulate(c - reach); }
            }
            for (int i = n; i < size; i++) { run.Level[i] = 0f; run.Edges[i] = Vector4.zero; run.Nbr[i] = Vector4.zero; }
            run.Bounds = b;
            var block = run.Block;
            block.Clear();
            block.SetFloatArray(SnowLevelId, run.Level);
            block.SetVectorArray(EdgesId, run.Edges);
            block.SetVectorArray(NbrId, run.Nbr);
        }

        private static void Submit(CapList list, Camera camera)
        {
            var cap = list.Cap;
            if (cap.Material == null) return;   // the owner destroyed it without telling EBM: draw nothing
            var rp = new RenderParams(cap.Material)
            {
                shadowCastingMode = cap.Shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off,
                receiveShadows = true,
                layer = list.Groups.Count > 0 && list.Groups[0].Definition != null && list.Groups[0].Definition.Parts != null && list.Groups[0].Definition.Parts.Length > 0
                    ? list.Groups[0].Definition.Parts[0].RenderParams.layer : 0,   // the piece's own layer
            };
            if (camera != null) rp.camera = camera;
            for (int r = 0; r < list.Runs.Count; r++)
            {
                var run = list.Runs[r];
                rp.worldBounds = run.Bounds;
                rp.matProps = run.Block;
                if (run.Mesh == null) continue;   // 1.2.508: the run's mesh (base or variant); destroyed by its owner = that run draws nothing
                Graphics.RenderMeshInstanced(in rp, run.Mesh, 0, list.M, run.N, run.Start);
                c_calls++;
            }
            if (camera == null) { c_instances += list.M.Count; DrawnLastFrame += list.M.Count; }
        }

        // ── baked roofs while a camera hides them (the live map, InstancedDraw.HideRoofsFor): the caps of roof prefabs go per camera too ──
        private static readonly List<CapList> s_deferredRoofs = new List<CapList>();
        internal static void DrawRoofCapsFor(Camera cam)
        {
            for (int i = 0; i < s_deferredRoofs.Count; i++) Submit(s_deferredRoofs[i], cam);
        }

        /// <summary>Once a frame from ZoneTracker.DrawCaps: the 60 s Debug proof / cost line, with the [2ic] claim census.</summary>
        internal static void Summary()
        {
            float now = Time.unscaledTime;
            if (now < s_nextLine) return;
            s_nextLine = now + 60f;
            // [2ic] SHOULD a: the census also HEALS - a claim with no cap instance (its list is gone, e.g. a replaced BakeResult whose ZoneCaps went without
            // GiveBack; its build no longer confirms it; or it did not draw for 2 s) means no cap at all on that piece, so it is given back here. Runs every
            // 60 s whatever the log level; only the line needs Debug.
            int claimed = 0, orphan = 0;
            if (EasyBakeSuppressedVisuals.ClaimedNow > 0)
            {
                s_unclaimScratch.Clear();
                foreach (var s in EasyBakeSuppressedVisuals.All)
                {
                    if (s == null || !s.CapClaimed) continue;
                    claimed++;
                    if (!(s.CapList is CapList l) || s.CapListBuild != l.Build || now - l.LastDrawnAt > 2f) s_unclaimScratch.Add(s);
                }
                orphan = s_unclaimScratch.Count;
                for (int i = 0; i < s_unclaimScratch.Count; i++)
                {
                    // a list that still exists rebuilds at its next draw, so it re-claims what it draws then (never an EBM cap over a given-back live cap)
                    if (s_unclaimScratch[i].CapList is CapList ol) ol.SigRev = -1;
                    s_unclaimScratch[i].UnclaimCaps();
                }
                s_unclaimScratch.Clear();
            }
            if (!EasyBakeLog.DebugOn() || (c_frames == 0 && claimed == 0)) { ResetCounters(); return; }
            double f = c_frames > 0 ? c_frames : 1, tick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // 1.2.507: the running version (was a fixed "(1.2.505)"), + the values refreshes and the same-registration no-ops
            EasyBakeLog.Debug($"[Caps] last 60 s ({FiresEasyBakeMeshesPlugin.PluginVersion}): prefabs with caps {s_caps.Count}, key {(Key != null && Key.Value ? "on" : "off")}; cap instances drawn {c_instances / f:0} / frame, " +
                $"cap draws (instanced only) {c_calls / f:0.0} / frame added; cap list rebuilds {c_rebuilds} ({c_rebuiltInstances} instances, {c_rebuildTicks * tick:0.0} ms); " +
                $"values refreshes {c_refreshes} ({c_refreshInstances} instances, {c_refreshTicks * tick:0.0} ms); same registration again {c_sameSets}; " +
                $"rebuild reasons: new / changed cap or heal {c_whyForced}, pieces joined / left / reordered {c_whyPieces}, zone announce {c_whyAnnounce}, variant {c_whyVariant}, suppressions {c_whySupp}, other {c_whyOther}, a cap appearing / going on a values refresh {c_whyEscalated}; " +
                $"appended suppressions claimed in place {c_incAppends} ({c_incClaims} live cap(s) claimed); {ApiLine(tick)}; " +
                $"new cap kinds: live pieces re-offered to the live-to-skip conversion {RetriedPieces} in {RetriedPasses} pass(es) (1.2.514); " +
                $"cap values kept across a zone reload {EbmSnowStore.KeptPieces} piece(s) in {EbmSnowStore.KeptZones} zone(s) (parked now {EbmSnowStore.ParkedNow}, dropped as stale {EbmSnowStore.ParkDropped}); " +
                $"claimed caps {claimed}, claimed with no cap instance {orphan} (given back)");
            ResetCounters();
        }
        private static void ResetCounters() { c_frames = c_instances = c_calls = c_rebuilds = c_rebuiltInstances = c_refreshes = c_refreshInstances = c_sameSets = 0; c_rebuildTicks = 0; c_refreshTicks = 0; c_whyForced = c_whyPieces = c_whyAnnounce = c_whyVariant = c_whySupp = c_whyOther = c_whyEscalated = 0; c_incAppends = c_incClaims = 0; ResetApi(); EbmSnowStore.KeptPieces = EbmSnowStore.KeptZones = EbmSnowStore.ParkDropped = 0; RetriedPieces = RetriedPasses = 0; }
    }
}
