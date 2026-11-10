using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect
{
    /// <summary>
    /// [ghost] 1.2.222 ([lead]: fixed step 3.00 ms vs VPO 2.54; what is physics simulating?). Census only, no behaviour change,
    /// Debug + Diagnostics.PhysicsCensus (default off) only for the automatic ONE scene search, 60 s after the local player spawns, never repeated on a timer; a further scan only when the ebm_physcensus command asks (a harness mark, Debug on)
    /// ([lead]: no per-window FindObjectsByType; Rigidbody is an engine component with no enable hook to register from), and the line
    /// logs that frame's own cost. Walks the loaded Rigidbodies (awake / sleeping / kinematic, by owning prefab), ZSyncTransform count
    /// (and how many are on an awake body), and enabled colliders (static vs on a body, mesh vs primitive, triggers).
    /// </summary>
    internal static class PhysicsCensus
    {
        private static float s_due = -1f;
        private static bool s_done;
        private static readonly Dictionary<string, int[]> s_byPrefab = new Dictionary<string, int[]>();

        internal static void Tick()
        {
            if (s_done) return;
            if (Player.m_localPlayer == null) { s_due = -1f; return; }
            float now = Time.unscaledTime;
            if (s_due < 0f) { s_due = now + 60f; return; }
            if (now < s_due) return;
            s_done = true;   // one shot per session, whatever the gates say
            if (!EasyBakeLog.DebugOn() || FiresEasyBakeMeshesPlugin.PhysicsCensus == null || !FiresEasyBakeMeshesPlugin.PhysicsCensus.Value) return;
            Scan("60 s after spawn");
        }

        // PhysicsCensusOnMark: one scan now, for ebm_physcensus [label]. Never called on a timer.
        internal static string RunNow(string label)
        {
            if (!EasyBakeLog.DebugOn()) return "[PhysicsCensus] Debug logging is off: nothing scanned";
            string when = string.IsNullOrEmpty(label) ? "command" : "command, mark " + label;
            Scan(when);
            return "[PhysicsCensus] scanned (" + when + "): see the log";
        }

        private static void Scan(string when)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            s_byPrefab.Clear();
            int bodies = 0, awake = 0, kinematic = 0, awakeKin = 0, interp = 0, continuous = 0;
            foreach (var rb in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (rb == null || !rb.gameObject.activeInHierarchy) continue;
                bodies++;
                bool a = !rb.IsSleeping();
                if (a) awake++;
                if (rb.isKinematic) { kinematic++; if (a) awakeKin++; }
                if (rb.interpolation != RigidbodyInterpolation.None) interp++;
                if (rb.collisionDetectionMode != CollisionDetectionMode.Discrete) continuous++;
                var view = rb.GetComponentInParent<ZNetView>();
                string name = view != null ? Utils.GetPrefabName(view.gameObject) : "(no ZNetView) " + rb.transform.root.name;
                if (!s_byPrefab.TryGetValue(name, out var c)) { if (s_byPrefab.Count >= 512) continue; c = new int[3]; s_byPrefab[name] = c; }
                c[0]++; if (a) c[1]++; if (rb.isKinematic) c[2]++;
            }
            int zst = 0, zstAwake = 0;
            foreach (var z in Object.FindObjectsByType<ZSyncTransform>(FindObjectsSortMode.None))
            {
                if (z == null || !z.isActiveAndEnabled) continue;
                zst++;
                var rb = z.GetComponent<Rigidbody>();
                if (rb != null && !rb.IsSleeping()) zstAwake++;
            }
            int cols = 0, colsOnBody = 0, meshCols = 0, convex = 0, triggers = 0, onPieces = 0, standIns = 0;
            var perLayer = new int[32];
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                cols++;
                perLayer[col.gameObject.layer]++;
                if (col.GetComponentInParent<WearNTear>() != null) onPieces++;
                else if (col.GetComponentInParent<EbmStandIn>() != null) standIns++;
                if (col.attachedRigidbody != null) colsOnBody++;
                if (col.isTrigger) triggers++;
                if (col is MeshCollider mc) { meshCols++; if (mc.convex) convex++; }
            }
            double scanMs = sw.Elapsed.TotalMilliseconds;
            var list = new List<KeyValuePair<string, int[]>>(s_byPrefab);
            list.Sort((x, y) => y.Value[1] != x.Value[1] ? y.Value[1].CompareTo(x.Value[1]) : y.Value[0].CompareTo(x.Value[0]));
            var sb = new StringBuilder($"[PhysicsCensus] rigidbodies {bodies}: awake {awake}, kinematic {kinematic} (awake+kinematic {awakeKin}), interpolated {interp}, continuous {continuous}; ZSyncTransform {zst} ({zstAwake} on an awake body); colliders {cols} (on a body {colsOnBody}, static {cols - colsOnBody}, mesh {meshCols} of which convex {convex}, triggers {triggers}, on a piece {onPieces}, EBM stand-ins {standIns}); fixedDeltaTime {Time.fixedDeltaTime * 1000f:0.0} ms; when: {when}; scan cost {scanMs:0.0} ms (this frame only; no timer repeats it)");
            sb.Append("\n  colliders per layer:");
            for (int i = 0; i < perLayer.Length; i++) if (perLayer[i] > 0) sb.Append(' ').Append(LayerMask.LayerToName(i)).Append('(').Append(i).Append(") ").Append(perLayer[i]).Append(';');
            sb.Append("\n  top prefabs (bodies / awake / kinematic):");
            for (int i = 0; i < list.Count && i < 25; i++)
                sb.Append($"\n    {list[i].Key}: {list[i].Value[0]} / {list[i].Value[1]} / {list[i].Value[2]}");
            EasyBakeLog.Debug(sb.ToString());
        }
    }
}
