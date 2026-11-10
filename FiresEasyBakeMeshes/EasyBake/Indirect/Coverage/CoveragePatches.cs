using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.20x (DESIGN_IndirectEBM §4b Step 3): the events that attach, refresh and hand back CoveredObject. Gate: IndirectBatches.Active
    /// ([Optimize] IndirectRenderer + IndirectCoverage, [visual]'s keys). Turning it on walks the loaded objects once; off hands everything back.
    /// Proof: console "ebm_coverage" (class x shader census + what is handed over), Info once when coverage first engages.
    /// Components go on live objects only, and only while Active (never on prefabs, so EBM's own prefab reads and SkipEligibility's
    /// component allow-list keep seeing vanilla prefabs); the object's kind is keyed on its ZDO prefab hash; private vanilla members are
    /// reached by TargetMethod / reflection (never a publicized reference).
    /// </summary>
    internal static class CoveragePatches
    {
        private static bool s_registered, s_engagedSaid, s_wasActive;

        internal static void SayEngagedOnce(CoverageRules.Verdict v)
        {
            if (s_engagedSaid) return;
            s_engagedSaid = true;
            EasyBakeLog.Info($"[Coverage] live trees / rocks / pickables now draw through EBM's indirect batches (first: {v?.Name}); see ebm_coverage");
        }

        private static CoveredObject Attach(ZNetView view)
        {
            if (view == null) return null;
            var zdo = view.GetZDO();
            if (zdo == null) return null;   // placement ghosts and views without a ZDO stay Unity's
            var go = view.gameObject;
            var v = CoverageRules.For(zdo.GetPrefab(), go);
            if (v.Refusal != null) return null;
            var c = go.GetComponent<CoveredObject>();
            if (c != null) { c.Refresh(); return c; }
            if (go.GetComponent<EasyBakeSuppressedVisuals>() != null) return null;   // EBM's bake already owns its looks
            c = go.AddComponent<CoveredObject>();
            c.Verdict = v;   // its Start covers it (after the object's own Awake/Start set it up)
            return c;
        }

        private static void OnActiveChanged()
        {
            bool now = IndirectBatches.Active;
            if (now == s_wasActive) return;
            s_wasActive = now;
            if (!now) { CoveredObject.ReleaseEverything(); PrefabCoverage.RestoreAll("indirect batches off"); EasyBakeLog.Info("[Coverage] indirect batches off: every covered object is drawn by Unity again"); return; }
            try { PrefabCoverage.Apply(); }   // step 2 (its key off = nothing): prefab parts off before the loaded copies are walked
            catch (Exception e) { EasyBakeLog.Debug("[Coverage] prefab mode apply skipped: " + e.Message); }
            var instances = ZoneTracker.SceneInstances();
            int n = 0;
            if (instances != null) foreach (var kv in instances) { if (kv.Value != null) { Attach(kv.Value); n++; } }
            EasyBakeLog.Debug($"[Coverage] indirect batches on: {n} loaded object(s) checked");
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class SceneAwake
        {
            // Last, after Jotunn and other mods registered and edited their prefabs (step 2 judges prefabs here when coverage is on)
            [HarmonyPriority(Priority.Last)]
            [HarmonyAfter("com.jotunn.jotunn")]
            private static void Postfix()
            {
                CoverageRules.Clear();   // prefab verdicts per scene: mods register different prefabs each session
                CoverageReach.Clear();   // 1.2.214: cells hold this scene's objects only
                CoveredObject.RendererRefusals.Clear();
                s_wasActive = IndirectBatches.Active;
                if (s_wasActive) { try { PrefabCoverage.Apply(); } catch (Exception e) { EasyBakeLog.Debug("[Coverage] prefab mode apply skipped: " + e.Message); } }
                if (s_registered) return;
                s_registered = true;
                IndirectBatches.ActiveChanged += OnActiveChanged;
                if (FiresEasyBakeMeshesPlugin.IndirectCoverageLocationProps != null)   // 1.2.216, live: verdicts depend on it
                    FiresEasyBakeMeshesPlugin.IndirectCoverageLocationProps.SettingChanged += (_, __) =>
                    {
                        CoverageRules.Clear();
                        int n = 0;
                        foreach (var co in new List<CoveredObject>(CoveredObject.Live))
                            if (co != null && co.Verdict != null && co.Verdict.Kind == CoverageRules.Kind.LocationProp) { co.HandBackForGood("location props key off"); UnityEngine.Object.Destroy(co); n++; }
                        if (CoverageRules.LocationPropsOn && IndirectBatches.Active)
                        {
                            var all = ZoneTracker.SceneInstances();
                            if (all != null) foreach (var kv in all) if (kv.Value != null) Attach(kv.Value);
                        }
                        EasyBakeLog.Info($"[Coverage] location props {(CoverageRules.LocationPropsOn ? "on: loaded objects checked again" : $"off: {n} prop(s) drawn by Unity again")}");
                    };
                if (FiresEasyBakeMeshesPlugin.IndirectCoverageDisablePrefabs != null)   // step 2, live: off = every prefab part back on; on = judged now
                    FiresEasyBakeMeshesPlugin.IndirectCoverageDisablePrefabs.SettingChanged += (_, __) =>
                    {
                        if (PrefabCoverage.Wanted) PrefabCoverage.Apply();
                        else PrefabCoverage.RestoreAll("IndirectCoverageDisablePrefabs off");
                    };
                if (FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers != null)   // the hiding method changed: every covered object re-covers the new way
                    FiresEasyBakeMeshesPlugin.IndirectCoverageDisableRenderers.SettingChanged += (_, __) => CoveredObject.RefreshEverything();
                new Terminal.ConsoleCommand("ebm_coverhide",
                    "on|off|status FiresEasyBakeMeshes: hide covered renderers with renderer.enabled = false (on: out of Unity's culling) or forceRenderingOff (off), this session only; status = counts.",
                    new Terminal.ConsoleEvent(args =>
                    {
                        string a = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                        if (a == "on" || a == "off") CoveredObject.SetHideOverride(a == "on");
                        else if (a != "status") { args.Context?.AddString("ebm_coverhide on|off|status"); return; }
                        string line = CoveredObject.HideStatus();
                        EasyBakeLog.Debug(line);
                        args.Context?.AddString(line);
                    }));
                new Terminal.ConsoleCommand("ebm_coverage",
                    "[radius m] FiresEasyBakeMeshes: live trees, rocks, destructibles and pickables near the camera by class and shader - renderers, materials, LODs, which the indirect batches draw and why the rest stay vanilla (read-only, also written to the log).",
                    new Terminal.ConsoleEvent(args =>
                    {
                        float radius = 150f;
                        if (args.Length > 1) float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out radius);
                        string report = CoverageCensus.Run(radius > 0f ? radius : 150f) + "\n" + CoverageReach.CensusText();   // 1.2.214 P1
                        EasyBakeLog.Debug(report);
                        foreach (var line in report.Split('\n')) args.Context?.AddString(line);
                    }));
            }
        }

        [HarmonyPatch(typeof(ZNetView), "Awake")]
        private static class ViewAwake
        {
            private static readonly List<MeshRenderer> s_born = new List<MeshRenderer>(8);

            private static void Postfix(ZNetView __instance)
            {
                // step 2: parts born off from a patched prefab go to this copy's CoveredObject, or are switched back on now
                bool born = false;
                if (PrefabCoverage.Engaged)
                {
                    try { born = PrefabCoverage.TakeBornOff(__instance, s_born); }
                    catch (Exception e) { born = s_born.Count > 0; EasyBakeLog.Debug($"[Coverage] prefab mode copy check failed for {__instance?.name}: {e.Message}"); }
                }
                if (!IndirectBatches.Active) { if (born) PrefabCoverage.Restore(s_born); return; }
                try
                {
                    var co = Attach(__instance);
                    if (born) { if (co != null) co.AdoptBornOff(s_born); else PrefabCoverage.Restore(s_born); }
                }
                catch (Exception e)
                {
                    if (born) PrefabCoverage.Restore(s_born);
                    EasyBakeLog.Debug($"[Coverage] attach skipped for {__instance?.name}: {e.Message}");
                }
            }
        }

        // 1.2.215 ([ghost], [visual]: ~10-14k indirect adds == removes per 30 s standing still): vanilla calls several of these hooks on a timer
        // (MineRock.UpdateVisability every 10 s on every rock), and each one was a full release + re-cover. Now a vanilla hook re-covers only when
        // what the object shows really changed (CoveredObject.RefreshIfChanged: the set of its active renderers); every call is counted by reason.
        private static void RefreshOf(Component c, string reason) { var co = c != null ? c.GetComponent<CoveredObject>() : null; if (co != null) co.RefreshIfChanged(reason); }
        private static void RefreshOf(Component c) => RefreshOf(c, "vanilla hook");

        internal static bool LookSetOn(GameObject go, List<Material> materials) => PerObjectLookWatch.LookSetOn(go, materials);

        // ---- Dropped items: vanilla's own 10 s ItemDrop.SlowUpdate (on every client) asks whether the item rests (cover) or moves (hand back).
        [HarmonyPatch]
        private static class ItemRest
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(ItemDrop), "SlowUpdate");
            private static void Postfix(ItemDrop __instance) { var co = __instance != null ? __instance.GetComponent<CoveredObject>() : null; if (co != null) co.SleepCheck(); }
        }

        // ---- Item stands: the shown item is a new child made here (its removal is seen by CoveredPartWatch); look again so it is covered too.
        [HarmonyPatch]
        private static class StandItemShown
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(ItemStand), "SetVisualItem");
            // 1.2.222 ([ghost], 1.2.220 O census: itemstand 3,510 + itemstandh 2,712 re-covers per 30 s standing still): vanilla's UpdateVisual calls
            // SetVisualItem on every stand every 4 s (InvokeRepeating, ItemStand.cs:103), and SetVisualItem returns at once when the item is
            // unchanged (:419). Re-cover only when the stand really shows something else (its active renderers / meshes changed).
            private static void Postfix(ItemStand __instance) { var co = __instance != null ? __instance.GetComponentInParent<CoveredObject>() : null; if (co != null) co.RefreshIfChanged("ItemStand.SetVisualItem"); }
        }

        // ---- Pickables: m_hideWhenPicked is set active / inactive by these; the refresh drops or re-adds it.
        [HarmonyPatch(typeof(Pickable), nameof(Pickable.SetPicked))]
        private static class PickableSetPicked { private static void Postfix(Pickable __instance) => RefreshOf(__instance, "Pickable.SetPicked"); }

        [HarmonyPatch]
        private static class PickableSetEnabled
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Pickable), nameof(Pickable.SetEnabled), new[] { typeof(int) });
            private static void Postfix(Pickable __instance) => RefreshOf(__instance, "Pickable.SetEnabled");
        }

        // ---- Trees: shake rotates m_trunk for 1 s, regrow swaps it for a scaled copy for 0.3 s. Vanilla draws those; cover again after.
        [HarmonyPatch]
        private static class TreeShake
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(TreeBase), "RPC_Shake");
            private static void Prefix(TreeBase __instance) { var co = __instance != null ? __instance.GetComponent<CoveredObject>() : null; if (co != null) co.HandBackFor(1.1f); }
        }

        [HarmonyPatch]
        private static class TreeGrow
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(TreeBase), "RPC_Grow");
            private static void Prefix(TreeBase __instance) { var co = __instance != null ? __instance.GetComponent<CoveredObject>() : null; if (co != null) co.HandBackFor(0.5f); }
        }

        // ---- Fractured rocks: every UpdateMesh (load, each hit) may change the mesh; runs after IntactRockLod's postfix (Priority.Low).
        [HarmonyPatch]
        private static class Rock5UpdateMesh
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MineRock5), "UpdateMesh");
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(MineRock5 __instance) => RefreshOf(__instance, "MineRock5.UpdateMesh");
        }

        // ---- Plain MineRock: broken areas are set inactive here.
        [HarmonyPatch]
        private static class RockHide
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MineRock), "RPC_Hide");
            private static void Postfix(MineRock __instance) => RefreshOf(__instance, "MineRock.RPC_Hide");
        }

        [HarmonyPatch]
        private static class RockVisibility
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(MineRock), "UpdateVisability");
            private static void Postfix(MineRock __instance) => RefreshOf(__instance, "MineRock.UpdateVisability (every 10 s)");
        }

        // ---- StaticPhysics: falling moves the object every 0.05 s (vanilla draws it); PushUp moves it once when it sinks.
        [HarmonyPatch]
        private static class PhysicsFall
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(StaticPhysics), "Fall");
            private static void Postfix(StaticPhysics __instance) { var co = __instance != null ? __instance.GetComponent<CoveredObject>() : null; if (co != null) co.HandBackUntilResumed(); }
        }

        // Settled: vanilla's FallUpdate calls StopFalling() BEFORE it writes the final ground position ([generator] review), so we resume
        // after the whole FallUpdate, once IsFalling is false: the batch copy is taken at the resting spot, not 0.2 m above it.
        [HarmonyPatch]
        private static class PhysicsSettled
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(StaticPhysics), "FallUpdate");
            private static void Postfix(StaticPhysics __instance)
            {
                if (__instance == null || __instance.IsFalling) return;
                var co = __instance.GetComponent<CoveredObject>();
                if (co != null && co.WaitingForSettle) co.Resume();
            }
        }

        [HarmonyPatch]
        private static class PhysicsPushUp
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(StaticPhysics), "PushUp");
            private static void Postfix(StaticPhysics __instance) { var co = __instance != null ? __instance.GetComponent<CoveredObject>() : null; if (co != null) co.RefreshIfMoved(); }
        }

        // ---- Debris: vanilla's CreateFragments(root, visibleOnly) copies each active renderer whose isVisible is set (or every active one,
        // LOD1/LOD2 included, when visibleOnly is false). Unity never sets isVisible under forceRenderingOff, so for an object we cover we
        // build the fragments ourselves with the question vanilla meant: a renderer we do not cover answers with its own isVisible; one we
        // cover counts when it would be drawn now (in the frustum AND inside its own LOD window, so one level breaks, not three stacked).
        // Everything after the choice is vanilla's own fragment: 0.9 x lossy scale on the "effect" layer, the same materials with
        // _RippleDistance and _ValueNoise zeroed through MaterialMan, a Rigidbody + BoxCollider, gone after 2-4 s, pushed out from the
        // centre of mass at 4 m/s plus a random 1 m/s.
        [HarmonyPatch(typeof(Destructible), nameof(Destructible.CreateFragments))]
        private static class DebrisForCovered
        {
            private static readonly int s_ripple = Shader.PropertyToID("_RippleDistance");
            private static readonly int s_valueNoise = Shader.PropertyToID("_ValueNoise");
            private static readonly Plane[] s_planes = new Plane[6];
            private static readonly List<Rigidbody> s_bodies = new List<Rigidbody>(8);

            private static bool Prefix(GameObject rootObject, bool visibleOnly)
            {
                if (rootObject == null) return true;
                var co = rootObject.GetComponent<CoveredObject>();
                if (co == null || !co.IsCovering) return true;   // not ours: vanilla as is
                try { Build(rootObject, co, visibleOnly); }
                catch (Exception e) { EasyBakeLog.Debug($"[Coverage] debris for {rootObject.name} failed: {e.Message}"); }
                return false;
            }

            private static void Build(GameObject root, CoveredObject co, bool visibleOnly)
            {
                var cam = Utils.GetMainCamera();
                if (cam != null) GeometryUtility.CalculateFrustumPlanes(cam, s_planes);
                int effectLayer = LayerMask.NameToLayer("effect");
                s_bodies.Clear();
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!mr.gameObject.activeInHierarchy) continue;
                    if (visibleOnly)
                    {
                        bool shown = co.Covers(mr) ? cam != null && co.WouldBeDrawn(mr, cam, s_planes) : mr.isVisible;
                        if (!shown) continue;
                    }
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null) continue;
                    if (mf.sharedMesh == null) { ZLog.Log("Meshfilter missing mesh " + mf.gameObject.name); continue; }

                    var piece = new GameObject { layer = effectLayer };
                    var src = mf.transform;
                    piece.transform.SetPositionAndRotation(src.position, src.rotation);
                    piece.transform.localScale = src.lossyScale * 0.9f;
                    piece.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                    piece.AddComponent<MeshRenderer>().sharedMaterials = mr.sharedMaterials;
                    MaterialMan.instance.SetValue(piece, s_ripple, 0f);
                    MaterialMan.instance.SetValue(piece, s_valueNoise, 0f);
                    s_bodies.Add(piece.AddComponent<Rigidbody>());
                    piece.AddComponent<BoxCollider>();
                    piece.AddComponent<TimedDestruction>().Trigger(UnityEngine.Random.Range(2, 4));
                }
                if (s_bodies.Count == 0) return;
                Vector3 centre = Vector3.zero;
                foreach (var b in s_bodies) centre += b.worldCenterOfMass;
                centre /= s_bodies.Count;
                foreach (var b in s_bodies)
                    b.AddForce((b.worldCenterOfMass - centre).normalized * 4f + UnityEngine.Random.onUnitSphere, ForceMode.VelocityChange);
                s_bodies.Clear();
            }
        }
        // ---- Per-object looks: MaterialMan gives an object its own property block (RandomMaterialValues, damage tints, ...). The batches
        // share one material per draw and v1 carries no per-instance values, so when a block sets a property that a material we draw
        // reads, that object goes back to Unity for its life. Runs only when a block changes (MaterialMan.Update), never per frame.
        [HarmonyPatch]
        private static class PerObjectLookWatch
        {
            private static FieldInfo f_renderers, f_properties;

            private static MethodBase TargetMethod()
            {
                var container = AccessTools.Inner(typeof(MaterialMan), "PropertyContainer");
                f_renderers = AccessTools.Field(container, "m_assignedRenderers");
                f_properties = AccessTools.Field(container, "m_shaderProperties");
                return AccessTools.Method(container, "UpdateBlock");
            }

            private static void Postfix(object __instance)
            {
                if (CoveredObject.Live.Count == 0) return;
                try
                {
                    var owner = OwnerOf(f_renderers?.GetValue(__instance) as IList);
                    if (owner == null || (!owner.IsCovering && !owner.LookHeld)) return;
                    if (!(f_properties?.GetValue(__instance) is IDictionary set)) return;
                    int readId = 0; bool reads = false;
                    foreach (var key in set.Keys)
                        if (key is int id && owner.AnySeenMaterialReads(id)) { reads = true; readId = id; break; }
                    if (reads && owner.IsCovering)
                    {
                        CoveredObject.HandBacksByProperty.TryGetValue(readId, out int n); CoveredObject.HandBacksByProperty[readId] = n + 1;   // shown by ebm_coverage
                        owner.HoldForLook();          // Unity draws the highlight / tint ...
                    }
                    else if (!reads && owner.LookHeld)
                        owner.ReleaseLookHold();      // ... and the batches take it back once MaterialMan cleared it
                }
                catch (Exception e) { EasyBakeLog.Debug($"[Coverage] per-object look check skipped: {e.Message}"); }
            }

            private static FieldInfo f_blocks;

            /// <summary>Does MaterialMan hold a value for this GameObject that one of these materials reads? (cover-time check)</summary>
            internal static bool LookSetOn(GameObject go, List<Material> materials)
            {
                if (go == null || MaterialMan.instance == null) return false;
                if (f_blocks == null) f_blocks = AccessTools.Field(typeof(MaterialMan), "m_blocks");
                if (!(f_blocks?.GetValue(MaterialMan.instance) is IDictionary blocks)) return false;
                int key = go.GetInstanceID();
                if (!blocks.Contains(key)) return false;
                if (!(f_properties?.GetValue(blocks[key]) is IDictionary set) || set.Count == 0) return false;
                foreach (var k in set.Keys)
                {
                    if (!(k is int id)) continue;
                    for (int m = 0; m < materials.Count; m++) if (materials[m] != null && materials[m].HasProperty(id)) return true;
                }
                return false;
            }

            private static CoveredObject OwnerOf(IList renderers)
            {
                if (renderers == null) return null;
                // a container belongs to ONE GameObject tree: the first live renderer decides ([generator] review: never walk them all)
                for (int i = 0; i < renderers.Count; i++)
                {
                    var r = renderers[i] as Renderer;
                    if (r != null) return r.GetComponentInParent<CoveredObject>();
                }
                return null;
            }
        }
    }
}
