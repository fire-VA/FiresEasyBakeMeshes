using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake.Indirect.Coverage
{
    /// <summary>
    /// [ghost] 1.2.214 ([lead] 11-08, coverage audit P1 + P2; Tools\STUDY_sections\ghost_CoverageAudit.md). Our own code.
    ///
    /// P2, [Optimize] IndirectCoverageReach (default off; live through ebm_opt): coverage holds every object vanilla creates, the whole synced
    /// simulation area (about +-288 m), with every LOD level and submesh as its own instance, so objects past their own last LOD level stay
    /// in the GPU tables and are rejected again in every view every frame. With this key on, a covered object the camera is beyond the
    /// reach of (its own last LOD level's end distance, from the same LOD window the GPU uses, + 15 % + the move slack) is PARKED: its batch
    /// slots are released, its renderers STAY hidden (still out of Unity's culling), nothing changes on screen (it was LOD-culled already).
    /// It is unparked (slots added back) before the camera can reach its draw distance: unpark at reach x 1.05 + 8 m, park at reach x 1.15
    /// + 8 m, and a pass runs whenever the camera moved 8 m since the last one (or the LOD bias / fov changed), so an object can never be
    /// inside its draw distance while parked. Objects without a LOD end (no LODGroup, or a never-culled level: [visual]'s "LodEnd 0") are
    /// never parked: vanilla draws those at any distance too.
    /// Filing (study E2): covered objects are filed by the same 64 m XZ cell key as IndirectCells (= ZoneSystem zones); each cell keeps the
    /// smallest and largest reach of its objects, so a pass skips whole cells that are clearly all in or all out and are already in that
    /// state. Passes are sliced (MaxChecksPerFrame object checks a frame); standing still costs one distance compare a frame.
    ///
    /// P1, the census (Debug, every 30 s, also in ebm_coverage): covered objects and held instances by kind x distance band, how many are
    /// beyond their own last LOD level right now, instances per object, instances that can never be distance-culled, and what is parked.
    /// </summary>
    internal static class CoverageReach
    {
        internal static bool On => FiresEasyBakeMeshesPlugin.IndirectCoverageReach != null && FiresEasyBakeMeshesPlugin.IndirectCoverageReach.Value;
        // 1.2.218 P3: needs IndirectCoverageReach on (its passes drive it)
        internal static bool LevelsOn => FiresEasyBakeMeshesPlugin.IndirectCoverageLodLevels != null && FiresEasyBakeMeshesPlugin.IndirectCoverageLodLevels.Value;

        private const float CellSize = 64f, MoveSlack = 8f, ParkFactor = 1.15f, UnparkFactor = 1.05f, FactorChange = 0.02f;
        private const int MaxChecksPerFrame = 3000;

        private sealed class Cell { public readonly HashSet<CoveredObject> Objects = new HashSet<CoveredObject>(); public float MinUnits = float.MaxValue, MaxUnits; public bool Dirty; public int Parked; public float CX, CZ; }
        private static readonly Dictionary<long, Cell> s_cells = new Dictionary<long, Cell>();
        private static readonly List<Cell> s_passCells = new List<Cell>();
        private static readonly List<CoveredObject> s_passObjects = new List<CoveredObject>();
        private static int s_passCell = -1, s_passObject;
        private static Vector3 s_passCam, s_lastPassCam = new Vector3(float.MaxValue, 0f, 0f);
        private static float s_passFactor, s_lastFactor = -1f;
        private static bool s_wasOn, s_dirty, s_saidFirst, s_wasLevels;
        internal static int Parked, ParkedSlots, ParksDone, UnparksDone, UnparkFailed, Passes, TimeCapped;   // 1.2.223 TimeCapped: frames the 1 ms cap stopped the pass
        private static float s_nextCensus;

        internal static long KeyOf(Vector3 p)
        {
            long x = Mathf.FloorToInt(p.x / CellSize), z = Mathf.FloorToInt(p.z / CellSize);
            return (x << 32) ^ (z & 0xFFFFFFFFL);
        }

        /// <summary>The camera's LOD factor: distance at which a size-1 object reaches screen height h is factor / h.</summary>
        internal static float Factor(Camera cam)
        {
            float half = Mathf.Max(1f, cam.fieldOfView) * 0.5f * Mathf.Deg2Rad;
            return QualitySettings.lodBias / (2f * Mathf.Tan(half));
        }

        // ---- filing (CoveredObject calls these when it covers / releases)
        internal static void File(CoveredObject co)
        {
            if (co.ReachUnits <= 0f || float.IsInfinity(co.ReachUnits)) return;   // never parked: not filed
            long key = KeyOf(co.ReachPoint);
            if (!s_cells.TryGetValue(key, out var cell))
            {
                cell = new Cell { CX = (Mathf.Floor(co.ReachPoint.x / CellSize) + 0.5f) * CellSize, CZ = (Mathf.Floor(co.ReachPoint.z / CellSize) + 0.5f) * CellSize };
                s_cells[key] = cell;
            }
            if (!cell.Objects.Add(co)) return;
            co.FiledKey = key; co.Filed = true;
            if (co.ReachUnits < cell.MinUnits) cell.MinUnits = co.ReachUnits;
            if (co.ReachUnits > cell.MaxUnits) cell.MaxUnits = co.ReachUnits;
            // a new object is judged at once (a far tree spawning in is parked before it is ever drawn) when the gate is on
            if (!On) return;
            var cam = Utils.GetMainCamera();
            if (cam != null) Judge(co, cam, Factor(cam), cell);
        }

        internal static void Unfile(CoveredObject co)
        {
            if (!co.Filed) return;
            co.Filed = false;
            if (!s_cells.TryGetValue(co.FiledKey, out var cell)) return;
            cell.Objects.Remove(co);
            cell.Dirty = true;   // min / max recomputed at the next pass
            if (cell.Objects.Count == 0) s_cells.Remove(co.FiledKey);
        }

        internal static void NoteParked(CoveredObject co, bool parked, int slots)
        {
            if (parked) { Parked++; ParkedSlots += slots; ParksDone++; } else { Parked--; ParkedSlots -= slots; UnparksDone++; }
            if (co.Filed && s_cells.TryGetValue(co.FiledKey, out var cell)) cell.Parked += parked ? 1 : -1;
        }

        // ---- per frame (Plugin.Update): one compare unless a pass runs
        internal static void Tick()
        {
            bool on = On;
            if (on != s_wasOn)
            {
                s_wasOn = on;
                if (!on) UnparkAll("IndirectCoverageReach off");
                else { s_dirty = true; EasyBakeLog.Info("[CoverageReach] on: covered objects the camera is past the last LOD level of are parked (slots released, still hidden); see ebm_coverage"); }
            }
            bool levels = on && LevelsOn;   // 1.2.218 P3
            if (levels != s_wasLevels)
            {
                s_wasLevels = levels;
                if (!levels)
                {
                    int n = 0;
                    foreach (var co in new List<CoveredObject>(CoveredObject.Live)) if (co != null && co.IsCovering) { co.HoldAllLevels(); n++; }
                    EasyBakeLog.Info($"[CoverageReach] LOD levels only off: every released level held again ({n} object(s) checked)");
                }
                else { s_dirty = true; EasyBakeLog.Info("[CoverageReach] LOD levels only on: levels the camera is not near give their slots back; see the census"); }
            }
            Census();
            if (!on || s_cells.Count == 0) return;
            var cam = Utils.GetMainCamera();
            if (cam == null) return;
            if (s_passCell < 0)
            {
                Vector3 p = cam.transform.position;
                float f = Factor(cam);
                bool moved = (p - s_lastPassCam).sqrMagnitude > MoveSlack * MoveSlack;
                bool lens = s_lastFactor <= 0f || Mathf.Abs(f - s_lastFactor) > s_lastFactor * FactorChange;
                if (!moved && !lens && !s_dirty) return;
                StartPass(p, f);
            }
            RunPass(cam);
        }

        private static void StartPass(Vector3 cam, float factor)
        {
            s_passCam = cam; s_passFactor = factor; s_dirty = false;
            s_passCells.Clear();
            foreach (var kv in s_cells) s_passCells.Add(kv.Value);
            s_passCell = 0; s_passObject = 0; s_passObjects.Clear();
            Passes++;
        }

        private static void RunPass(Camera cam)
        {
            int budget = MaxChecksPerFrame;
            long tEnd = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency / 1000;   // 1.2.223: also at most ~1 ms a frame (a park / unpark adds or frees slots: not all checks cost the same)
            int sinceClock = 0;
            while (s_passCell >= 0 && budget > 0)
            {
                if (s_passObjects.Count == 0 || s_passObject >= s_passObjects.Count)
                {
                    if (s_passCell >= s_passCells.Count) { EndPass(); return; }
                    var cell = s_passCells[s_passCell++];
                    budget--;
                    if (cell.Objects.Count == 0) continue;
                    if (cell.Dirty) Recompute(cell);
                    // whole-cell answers: nearest / farthest point of the cell's XZ square from the camera (height is not known per cell)
                    float dx = Mathf.Max(0f, Mathf.Abs(s_passCam.x - cell.CX) - CellSize * 0.5f), dz = Mathf.Max(0f, Mathf.Abs(s_passCam.z - cell.CZ) - CellSize * 0.5f);
                    float near = Mathf.Sqrt(dx * dx + dz * dz);
                    float fx = Mathf.Abs(s_passCam.x - cell.CX) + CellSize * 0.5f, fz = Mathf.Abs(s_passCam.z - cell.CZ) + CellSize * 0.5f;
                    float far = Mathf.Sqrt(fx * fx + fz * fz) + 64f;   // + height slack
                    bool allOut = near > cell.MaxUnits * s_passFactor * ParkFactor + MoveSlack;
                    bool allIn = far < cell.MinUnits * s_passFactor * UnparkFactor + MoveSlack;
                    if (allOut && cell.Parked == cell.Objects.Count) continue;   // all parked already and stay parked
                    if (allIn && cell.Parked == 0 && !LevelsOn) continue;       // all held and stay held (P3 still swaps levels inside reach)
                    s_passObjects.Clear(); s_passObjects.AddRange(cell.Objects); s_passObject = 0;
                    continue;
                }
                if (++sinceClock >= 16) { sinceClock = 0; if (System.Diagnostics.Stopwatch.GetTimestamp() > tEnd) { TimeCapped++; return; } }
                var co = s_passObjects[s_passObject++];
                budget--;
                if (co != null) Judge(co, cam, s_passFactor, null);
            }
        }

        private static void EndPass()
        {
            s_passCell = -1; s_passObjects.Clear();
            s_lastPassCam = s_passCam; s_lastFactor = s_passFactor;
        }

        private static void Judge(CoveredObject co, Camera cam, float factor, Cell cell)
        {
            if (!co.IsCovering) return;
            float d = Vector3.Distance(cam.transform.position, co.ReachPoint);
            float reach = co.ReachUnits * factor;
            if (!co.IsParked && d > reach * ParkFactor + MoveSlack) co.Park();
            else if (co.IsParked && d < reach * UnparkFactor + MoveSlack)
            {
                if (!co.Unpark()) UnparkFailed++;
            }
            if (LevelsOn && !co.IsParked && co.IsCovering) co.ApplyLevels(cam.transform.position, factor);   // 1.2.218 P3
            if (!s_saidFirst && co.IsParked) { s_saidFirst = true; EasyBakeLog.Info($"[CoverageReach] first park: {co.Verdict?.Name} at {d:F0} m (its last LOD level ends at {reach:F0} m)"); }
        }

        private static void Recompute(Cell cell)
        {
            cell.MinUnits = float.MaxValue; cell.MaxUnits = 0f; cell.Parked = 0;
            foreach (var co in cell.Objects)
            {
                if (co == null) continue;
                if (co.ReachUnits < cell.MinUnits) cell.MinUnits = co.ReachUnits;
                if (co.ReachUnits > cell.MaxUnits) cell.MaxUnits = co.ReachUnits;
                if (co.IsParked) cell.Parked++;
            }
            cell.Dirty = false;
        }

        internal static void UnparkAll(string why)
        {
            int n = 0;
            foreach (var co in new List<CoveredObject>(CoveredObject.Live)) if (co != null && co.IsParked) { if (!co.Unpark()) UnparkFailed++; n++; }
            s_passCell = -1; s_passObjects.Clear(); s_lastFactor = -1f;
            if (n > 0) EasyBakeLog.Info($"[CoverageReach] {n} parked object(s) back in the batches ({why})");
        }

        internal static void Clear() { s_cells.Clear(); s_passCell = -1; s_passObjects.Clear(); Parked = ParkedSlots = 0; s_lastFactor = -1f; }

        // ---- P1 census
        private static readonly string[] s_kinds = { "None", "Pickable", "Tree", "Destructible", "MineRock5", "MineRock", "Piece", "Item", "LocationProp" };
        private static readonly float[] s_bands = { 64f, 128f, 192f };
        private static readonly Dictionary<string, int> s_propInst = new Dictionary<string, int>();

        private static void Census()
        {
            float now = Time.unscaledTime;
            if (now < s_nextCensus) return;
            s_nextCensus = now + 30f;
            // 1.2.227 (lead, 1226 log: "coverage reach 29-31 ms" once per 30 s = this walk of every covered object): opt-in only
            if (!EasyBakeLog.DebugOn() || CoveredObject.Live.Count == 0 || FiresEasyBakeMeshesPlugin.CoverageCensus == null || !FiresEasyBakeMeshesPlugin.CoverageCensus.Value) return;
            EasyBakeLog.Debug(CensusText());
        }

        internal static string CensusText()
        {
            var cam = Utils.GetMainCamera();
            if (cam == null) return "[CoverageReach] census: no camera";
            float factor = Factor(cam);
            Vector3 cp = cam.transform.position;
            int k = s_kinds.Length;
            var objs = new int[k]; var inst = new int[k]; var never = new int[k]; var beyond = new int[k]; var beyondInst = new int[k]; var parked = new int[k];
            var band = new int[k, 4];
            foreach (var co in CoveredObject.Live)
            {
                if (co == null || !co.IsCovering) continue;
                int kind = co.Verdict != null ? (int)co.Verdict.Kind : 0;
                if (kind < 0 || kind >= k) kind = 0;
                int held = co.HeldInstances(out int neverCulled);   // a parked object's slots are counted as what it would hold
                objs[kind]++; inst[kind] += held; never[kind] += neverCulled;
                float d = Vector3.Distance(cp, co.ReachPoint);
                int b = d < s_bands[0] ? 0 : d < s_bands[1] ? 1 : d < s_bands[2] ? 2 : 3;
                band[kind, b]++;
                if (!float.IsInfinity(co.ReachUnits) && co.ReachUnits > 0f && d > co.ReachUnits * factor) { beyond[kind]++; beyondInst[kind] += held; }
                if (co.IsParked) parked[kind]++;
            }
            var sb = new StringBuilder("[CoverageReach] census (camera, lodBias ").Append(QualitySettings.lodBias.ToString("F2")).Append(", gate ").Append(On ? "on" : "off")
                .Append($", {Parked} parked = {ParkedSlots} slots released, passes {Passes}, parks {ParksDone}, unparks {UnparksDone}, unpark failures {UnparkFailed}, 1 ms caps {TimeCapped}; LOD levels only {(LevelsOn ? "on" : "off")}: {CoveredObject.LevelsOff} level(s) off = {CoveredObject.LevelSlotsOff} slots released, level offs {CoveredObject.LevelOffs}, ons {CoveredObject.LevelOns}):");
            int to = 0, ti = 0, tn = 0, tb = 0, tbi = 0;
            for (int i = 0; i < k; i++)
            {
                if (objs[i] == 0) continue;
                to += objs[i]; ti += inst[i]; tn += never[i]; tb += beyond[i]; tbi += beyondInst[i];
                sb.Append('\n').Append("  ").Append(s_kinds[i]).Append(": ").Append(objs[i]).Append(" obj, ").Append(inst[i]).Append(" inst (")
                  .Append((inst[i] / (float)objs[i]).ToString("F1")).Append("/obj), never distance-culled ").Append(never[i])
                  .Append(" inst; by distance <64 ").Append(band[i, 0]).Append(", 64-128 ").Append(band[i, 1]).Append(", 128-192 ").Append(band[i, 2]).Append(", 192+ ").Append(band[i, 3])
                  .Append("; past their last LOD now ").Append(beyond[i]).Append(" obj / ").Append(beyondInst[i]).Append(" inst; parked ").Append(parked[i]);
            }
            // 1.2.216: by prefab, the location props covered (top 10 by instances) and what the vanilla-left props are, by reason
            s_propInst.Clear();
            foreach (var co in CoveredObject.Live)
            {
                if (co == null || !co.IsCovering || co.Verdict == null || co.Verdict.Kind != CoverageRules.Kind.LocationProp) continue;
                string n = co.Verdict.Name ?? "?";
                s_propInst.TryGetValue(n, out int c); s_propInst[n] = c + co.HeldInstances(out _);
            }
            if (s_propInst.Count > 0)
            {
                var list = new List<KeyValuePair<string, int>>(s_propInst);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append('\n').Append("  location props covered, top by instances:");
                for (int i = 0; i < list.Count && i < 10; i++) sb.Append(' ').Append(list[i].Key).Append(' ').Append(list[i].Value).Append(';');
            }
            int refused = 0; var reasons = new Dictionary<string, int>();
            foreach (var kv in CoverageRules.All)
            {
                var v = kv.Value;
                if (v == null || v.Refusal == null || !v.Refusal.StartsWith("location prop kept vanilla", System.StringComparison.Ordinal)) continue;
                refused++; reasons.TryGetValue(v.Refusal, out int c); reasons[v.Refusal] = c + 1;
            }
            if (refused > 0) { sb.Append('\n').Append("  location prop prefabs kept vanilla:"); foreach (var kv in reasons) sb.Append(' ').Append(kv.Key.Substring(28)).Append(' ').Append(kv.Value).Append(';'); }

            // 1.2.215: why objects were re-covered / handed back since the last census line (standing-still churn), and timer hooks that changed nothing
            sb.Append('\n').Append("  re-covers / hand-backs since last line by reason:");
            if (CoveredObject.RefreshReasons.Count == 0) sb.Append(" none");
            foreach (var kv in CoveredObject.RefreshReasons) sb.Append(' ').Append(kv.Key).Append(' ').Append(kv.Value).Append(';');
            sb.Append(" vanilla-hook calls skipped (nothing changed) ").Append(CoveredObject.RefreshesSkipped);
            if (CoveredObject.RefusalsByPrefab.Count > 0)
            {
                var rf = new List<KeyValuePair<string, int>>(CoveredObject.RefusalsByPrefab);
                rf.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append('\n').Append("  renderers refused since last line, by prefab/part: reason:");
                for (int i = 0; i < rf.Count && i < 15; i++) sb.Append(' ').Append(rf[i].Key).Append(" x").Append(rf[i].Value).Append(';');
                CoveredObject.RefusalsByPrefab.Clear();
            }
            if (CoveredObject.PartToggleByPrefab.Count > 0)
            {
                var pt = new List<KeyValuePair<string, int>>(CoveredObject.PartToggleByPrefab);
                pt.Sort((a, b) => b.Value.CompareTo(a.Value));
                sb.Append('\n').Append("  part on/off re-covers by prefab:");
                for (int i = 0; i < pt.Count && i < 12; i++) sb.Append(' ').Append(pt[i].Key).Append(' ').Append(pt[i].Value).Append(';');
                CoveredObject.PartToggleByPrefab.Clear();
            }
            CoveredObject.RefreshReasons.Clear(); CoveredObject.RefreshesSkipped = 0;
            sb.Append('\n').Append($"  total (inst = what each object holds when drawn; parked objects counted at that size, see the header for slots released): {to} obj, {ti} inst, never distance-culled {tn} inst, past their last LOD {tb} obj / {tbi} inst ({(ti > 0 ? 100f * tbi / ti : 0f):F0} % of held: what the gate releases)");
            return sb.ToString();
        }
    }
}
