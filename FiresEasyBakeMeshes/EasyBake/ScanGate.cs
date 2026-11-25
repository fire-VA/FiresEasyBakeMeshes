using System.Collections.Generic;   // 1.2.492: List<ZDO>[] (the sector grid check)
using UnityEngine;
using Log = FiresEasyBakeMeshes.EasyBakeLog;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ═══ THE SCAN THAT FINDS NOTHING, THIRTY TIMES A SECOND ═══════════════════
    // ZNetScene rebuilds its near/distant lists every pass and then walks them
    // whole, twice: CreateObjectsSorted looks for ZDOs with no instance, and
    // RemoveObjects stamps every one of them and then walks every live ZNetView
    // looking for a stamp that is missing. Standing still, both find nothing.
    //
    // Measured 2026-09-24, settled in a built settlement, by splitting the method:
    //
    //     1,273 pass(es): walk 5,217 ms of up to 113,014 near,
    //                     sort+create 6 ms of up to 0 candidate(s), 0 created
    //
    // 5,217 ms of walking against 6 ms of doing. EBM is why the list is that
    // long: 66,667 of those are pieces its bake draws, marked Created so they are
    // correctly ignored, and then visited again next pass, forever.
    //
    // With the create side gated, measured the same evening: skip rate reached
    // 77%, the walk fell 5,217 ms -> 505 ms for a comparable number of passes,
    // and 32 rechecks across four windows all came back "0 wrong".
    //
    // ═══ WHAT MAKES WORK APPEAR ═══════════════════════════════════════════════
    // Only four things, and all four are one integer compare:
    //   * the near list changes size      (a ZDO entered or left the active area)
    //   * the distant list changes size
    //   * the instance count changes      (something was created or destroyed)
    //   * the reference zone changes      (the player crossed a zone boundary)
    //
    // Membership can churn without the count moving - one ZDO leaves as another
    // arrives in the same pass. That is the only miss this can have, and it costs
    // one deadline. So ForceScanSeconds is not a safety net bolted on, it is what
    // makes the cheap test legitimate.
    //
    // ═══ AND IT CHECKS ITSELF ═════════════════════════════════════════════════
    // Every ValidateEverySeconds the gate lets a scan run that it WOULD have
    // skipped, then reads what that scan actually found. Anything found is work
    // the skip would have missed, and is reported as wrong. A skip that cannot be
    // audited is a guess; this one carries its own falsification.
    //
    // ═══ NEVER SKIP WHILE WORK IS OWED ═══════════════════════════════════════
    // The four counters answer "did the world move", which is NOT the same as "is
    // there anything left to do" - see CanSkip. Both sides therefore require a
    // real scan to have come back empty before any skipping starts.
    //
    // The two sides are not equally forgiving about getting that wrong:
    //
    // CREATE: skipping leaves the candidate list empty, so the create loop finds
    //   nothing. It cannot destroy or strand anything; a new piece appears one
    //   deadline late at worst. Shipped first, on its own, for exactly that reason.
    //
    // DESTROY: RemoveObjects treats a ZDO with no fresh earmark as gone and
    //   DESTROYS it. So this side carries a SECOND condition on top: the destroy
    //   queue must be EMPTY, which is about drain progress rather than discovery.
    //   The scan can come back empty while items enqueued on an earlier pass are
    //   still waiting on the time budget, and skipping then would leave them
    //   queued and undrained with no stamps written.
    internal sealed class ScanGate
    {
        private const float ForceScanSeconds = 1f;
        private const float ForceScanSecondsHashed = 3f;   // 1.2.268: with the event counter the count test is exact, the force scan is a safety lap
        private long _events = -1;
        private const float ValidateEverySeconds = 5f;

        internal static readonly ScanGate Create = new ScanGate("create");
        internal static readonly ScanGate Destroy = new ScanGate("destroy");

        private readonly string _name;

        private int _near = -1, _distant = -1, _instances = -1;
        private Vector2s _zone;
        private bool _haveZone;
        private float _lastScan = -999f, _lastValidate = -999f;
        private bool _validating;

        private long _skipped, _scanned;
        private long _rOwed, _rNear, _rDistant, _rInstances, _rZone, _rEvents, _rAudit, _rForce;   // 1.2.492: passes that ran, by reason
        private int _checked, _wrong, _wrongFound;
        private int _lastFound = -1;

        private readonly Unity.Profiling.ProfilerMarker _marker;   // next EBM ([lead] 11-22): one profiler row per side

        private ScanGate(string name)
        {
            _name = name;
            _marker = name == "create" ? EbmPatchMarkers.ScanGateCreate : EbmPatchMarkers.ScanGateDestroy;
        }

        // -- 1.2.268 ScanGateEventHash: sector membership events inside the active area --
        internal static long s_areaEvents;
        private static bool EventHashOn => FiresEasyBakeMeshesPlugin.ScanGateEventHash != null && FiresEasyBakeMeshesPlugin.ScanGateEventHash.Value;
        internal static void NoteSectorEvent(ZoneSystem.SectorIndex s)   // 1.2.271: called from SectorHooks (one postfix per vanilla method)
        {
            if (!EventHashOn) return;
            var net = ZNet.instance;
            if (net == null) return;
            Vector2s here = ZoneSystem.GetZone(net.GetReferencePosition());
            int x = (int)(s.Sector % 512u) - 256, y = (int)(s.Sector / 512u) - 256;   // vanilla's own decode (ZoneSystem.cs:3463-3464)
            int r = net.GetSyncedSimulationDistance().TotalSimulationDistance;
            if (Mathf.Abs(x - here.x) <= r && Mathf.Abs(y - here.y) <= r) s_areaEvents++;
        }

        // -- 1.2.492 ScanGateIgnoreInAreaMoves ([visual], Fire 11-22 via [lead]: "not allowing fish and creature movement to cause churn but be ideal") --
        // A ZDO changing sector is ZDO.SetSector: RemoveFromSector(old) then AddToSector(new), back to back (ZDO.cs:212-214). A fish, creature, ship
        // or drop moving between two sectors that are BOTH in vanilla's near / distant lists changes nothing CreateDestroyObjects builds (the lists are
        // per sector: ZDOMan.FindSectorObjects ZDOMan.cs:945-1000), so it is not counted. A move that changes membership (into or out of either list),
        // a removal with no add (a destroyed ZDO) and an add with no remove (a new ZDO) are counted exactly as before. Pairing needs the same object
        // AND the same uid: ZDOs are pooled, so a destroyed ZDO's object can come back as a new one.
        private static bool IgnoreMovesOn => FiresEasyBakeMeshesPlugin.ScanGateIgnoreInAreaMoves != null && FiresEasyBakeMeshesPlugin.ScanGateIgnoreInAreaMoves.Value;
        private static ZDO s_pendZdo; private static ZDOID s_pendUid; private static ZoneSystem.SectorIndex s_pendFrom;
        private static long s_movesIgnored, s_movesCounted;
        internal static void NoteSectorRemove(ZDO zdo, ZoneSystem.SectorIndex s)   // SectorHooks.Removed
        {
            if (!EventHashOn) return;
            using (EbmPatchMarkers.ScanGateSector.Auto()) NoteSectorRemoveBody(zdo, s);   // next EBM: profiler marker only
        }
        private static void NoteSectorRemoveBody(ZDO zdo, ZoneSystem.SectorIndex s)
        {
            FlushPending();   // an earlier remove that no add followed was a real removal
            if (IgnoreMovesOn && zdo != null) { s_pendZdo = zdo; s_pendUid = zdo.m_uid; s_pendFrom = s; return; }
            NoteSectorEvent(s);
        }
        internal static void NoteSectorAdd(ZDO zdo, ZoneSystem.SectorIndex s)   // SectorHooks.Added
        {
            if (!EventHashOn) return;
            using (EbmPatchMarkers.ScanGateSector.Auto()) NoteSectorAddBody(zdo, s);   // next EBM: profiler marker only
        }
        private static void NoteSectorAddBody(ZDO zdo, ZoneSystem.SectorIndex s)
        {
            if (s_pendZdo != null && ReferenceEquals(s_pendZdo, zdo) && s_pendUid == zdo.m_uid)
            {
                var from = s_pendFrom; s_pendZdo = null;
                if (SameMembership(zdo, from, s)) { s_movesIgnored++; return; }
                s_movesCounted++;
                NoteSectorEvent(from); NoteSectorEvent(s);
                return;
            }
            FlushPending();
            NoteSectorEvent(s);
        }
        // CanSkip calls this first, so a removal is always counted before the gate decides
        internal static void FlushPending()
        {
            if (s_pendZdo == null) return;
            var from = s_pendFrom; s_pendZdo = null;
            NoteSectorEvent(from);
        }
        // in CreateDestroyObjects' lists before and after the move? Vanilla's own rule (ZDOMan.FindSectorObjects, ZoneSystem.ZonesWithinRadius ZoneSystem.cs:637),
        // around the same reference zone and simulation distance ZNetScene.CreateDestroyObjects uses (ZNetScene.cs:290-293). Anything unsure counts as a change.
        private static bool SameMembership(ZDO zdo, ZoneSystem.SectorIndex a, ZoneSystem.SectorIndex b)
        {
            var net = ZNet.instance; var zs = ZoneSystem.instance;
            if (net == null || zs == null || a.Sector == 0u || b.Sector == 0u) return false;   // SectorZero = outside the zones: always counted
            if (!VanillaGrid()) return false;   // [2ic] MUST: Decode assumes vanilla's 512 x 512 grid; a widened grid counts every move (as before)
            Vector2s here = ZoneSystem.GetZone(net.GetReferencePosition());
            var sim = net.GetSyncedSimulationDistance();
            return ListOf(zdo, Decode(a), here, sim, zs) == ListOf(zdo, Decode(b), here, sim, zs);
        }
        // [2ic] MUST: the live sector grid (ZDOMan.m_objectsBySector, read through FarBuildingDigest's field ref); anything but vanilla's 512 x 512 = 262,144
        // (ZoneSystem.SectorToIndex, ZoneSystem.cs:2178-2184) turns the move filter off for the session, said once
        private static int s_grid = -1;
        private static ZDOMan s_gridFor;   // [2ic]: per ZDOMan instance = per world (world size is per world), so a second world in the same session is asked again
        private static bool VanillaGrid()
        {
            var zm = ZDOMan.instance;
            if (zm == null) return false;
            if (!ReferenceEquals(s_gridFor, zm)) { s_gridFor = zm; s_grid = -1; }
            if (s_grid < 0)
            {
                var f = Far.FarBuildingDigest.s_objectsBySector;
                List<ZDO>[] arr = null; try { arr = f != null ? f(zm) : null; } catch { arr = null; }
                s_grid = arr != null ? arr.Length : 0;
                if (s_grid != 262144) Log.Info($"[ScanGate] sector grid {s_grid} (vanilla 262144): the in-area move filter is off for this world, every sector move counts as before (1.2.492)");
            }
            return s_grid == 262144;
        }
        private static Vector2s Decode(ZoneSystem.SectorIndex s) => new Vector2s((int)(s.Sector % 512u) - 256, (int)(s.Sector / 512u) - 256);   // as NoteSectorEvent
        // [2ic] should: WHICH list, as vanilla builds them (0 none, 1 near = m_tempCurrentObjects, 2 distant = m_tempCurrentDistantObjects): CreateObjects
        // paces the two differently, so a Distant ZDO moving from a near sector to a distant-ring sector is a change too
        private static int ListOf(ZDO zdo, Vector2s z, Vector2s here, SimulationDistance sim, ZoneSystem zs)
        {
            int ring = Mathf.Max(Mathf.Abs(z.x - here.x), Mathf.Abs(z.y - here.y));
            if (ring == 0) return 1;   // FindObjects(sector) first
            if (ring <= sim.NearSimulationDistance && (sim.IsClassic || zs.ZonesWithinRadius(here, z, sim.NearSimulationDistance))) return 1;
            if (!zdo.Distant) return 0;
            int first = sim.IsClassic ? 1 + sim.NearSimulationDistance : 1;
            return ring >= first && ring <= sim.TotalSimulationDistance && (sim.IsClassic || zs.ZonesWithinRadius(here, z, sim.TotalSimulationDistance, true)) ? 2 : 0;
        }

        internal static bool Enabled =>
            FiresEasyBakeMeshesPlugin.SkipUnchangedScans != null
            && FiresEasyBakeMeshesPlugin.SkipUnchangedScans.Value;

        /// <summary>
        /// True when nothing that could produce work has changed since the last scan. False means scan, and the caller
        /// must report what that scan found through <see cref="NoteScanFound"/> so the gate can audit itself.
        /// </summary>
        internal bool CanSkip(int near, int distant, int instances, Vector2s zone)
        {
            if (!Enabled) return false;
            using (_marker.Auto()) return CanSkipBody(near, distant, instances, zone);   // next EBM: profiler marker only
        }
        private bool CanSkipBody(int near, int distant, int instances, Vector2s zone)
        {
            FlushPending();   // 1.2.492: a removal no add followed counts before this pass decides

            // WORK CAN BE PENDING WITHOUT ANYTHING CHANGING. The four counters answer "did the world move",
            // not "is there anything left to do", and those are different questions whenever a budget caps how
            // much of the work a single pass may finish. ZNetScene creates at most ~40 objects a frame, so a
            // 47,898-object backlog sits across passes while near, distant, instances and zone all hold still.
            //
            // The audit caught exactly this on 2026-09-24: "1 of 1 recheck(s) WRONG, 35,153 object(s) missed",
            // during streaming, while the skip rate was 0% so nothing was actually lost. The destroy side reported
            // 0 wrong in the same window because it already required its queue to be empty - this is that same
            // condition, stated once for both: never skip until a real scan has come back with nothing.
            if (_lastFound != 0) { _rOwed++; return false; }

            float now = Time.unscaledTime;
            // 1.2.268 [Optimize] ScanGateEventHash (TESTPLAN row 33): every sector add / remove inside the active area bumps s_areaEvents (vanilla
            // ZDOMan.AddToSector / RemoveFromSector postfixes below), so a ZDO leaving while another arrives (the counts' one blind spot) is seen too.
            // With that, the forced scan relaxes from every 1 s to every 3 s; the 5 s audit is unchanged and still counts anything a skip would miss.
            bool hashOn = EventHashOn;
            bool unchanged = near == _near
                          && distant == _distant
                          && instances == _instances
                          && _haveZone && zone.x == _zone.x && zone.y == _zone.y
                          && (!hashOn || s_areaEvents == _events);
            _events = s_areaEvents;

            if (!unchanged)
            {
                // 1.2.492 proof: why this pass ran (first reason that applies)
                if (near != _near) _rNear++;
                else if (distant != _distant) _rDistant++;
                else if (instances != _instances) _rInstances++;
                else if (!_haveZone || zone.x != _zone.x || zone.y != _zone.y) _rZone++;
                else _rEvents++;
                _near = near;
                _distant = distant;
                _instances = instances;
                _zone = zone;
                _haveZone = true;
                _lastScan = now;
                _scanned++;
                _validating = false;
                return false;
            }

            // Deliberately let a skippable pass through so the scan can be read back. This is the audit, not a
            // fallback: it is the only way to learn that the cheap test agrees with the expensive one.
            if (now - _lastValidate >= ValidateEverySeconds)
            {
                _lastValidate = now;
                _lastScan = now;
                _validating = true;
                _scanned++; _rAudit++;
                return false;
            }

            if (now - _lastScan >= (hashOn ? ForceScanSecondsHashed : ForceScanSeconds))
            {
                _rForce++;
                _lastScan = now;
                _scanned++;
                _validating = false;
                return false;
            }

            _skipped++;
            return true;
        }

        /// <summary>
        /// What the scan the gate just allowed actually found. Non-zero on a validation pass means the skip would have
        /// been wrong. Safe to call on every scan; it only counts during an audit.
        /// </summary>
        internal void NoteScanFound(int found)
        {
            _lastFound = found;
            if (!_validating) return;
            _validating = false;
            _checked++;
            if (found <= 0) return;
            _wrong++;
            _wrongFound += found;
        }

        internal void Reset()
        {
            _near = _distant = _instances = -1;
            _haveZone = false;
            _lastScan = _lastValidate = -999f;
            _validating = false;
            _lastFound = -1;
        }

        private string TakeOwnStatus()
        {
            long total = _skipped + _scanned;
            if (total == 0) return null;
            string audit = _checked == 0
                ? "no recheck yet"
                : (_wrong == 0
                    ? $"{_checked:N0} recheck(s) confirmed, 0 wrong"
                    : $"{_wrong:N0} of {_checked:N0} recheck(s) WRONG, {_wrongFound:N0} object(s) missed");
            string status = $"{_name} {_skipped:N0}/{total:N0} ({_skipped * 100.0 / total:N0}%), {audit}"
                + $"; ran because: work owed {_rOwed:N0}, near count {_rNear:N0}, distant count {_rDistant:N0}, instances {_rInstances:N0}, zone {_rZone:N0}, sector events {_rEvents:N0}, audit {_rAudit:N0}, safety lap {_rForce:N0}";   // 1.2.492
            _skipped = 0; _scanned = 0; _checked = 0; _wrong = 0; _wrongFound = 0;
            _rOwed = _rNear = _rDistant = _rInstances = _rZone = _rEvents = _rAudit = _rForce = 0;
            return status;
        }

        internal static string TakeStatus()
        {
            string create = Create.TakeOwnStatus();
            string destroy = Destroy.TakeOwnStatus();
            if (create == null && destroy == null) return null;
            // 1.2.492: sector moves inside the lists (fish, creatures, ships, drops) not counted vs moves that changed membership
            string moves = IgnoreMovesOn ? $" | in-area moves ignored {s_movesIgnored:N0}, membership moves counted {s_movesCounted:N0}" : " | in-area moves: key off";
            s_movesIgnored = 0; s_movesCounted = 0;
            if (create == null) return "near-scan skipped " + destroy + moves;
            if (destroy == null) return "near-scan skipped " + create + moves;
            return $"near-scan skipped {create} | {destroy}{moves}";
        }

        internal static void ResetAll()
        {
            Create.Reset();
            Destroy.Reset();
        }

        internal static void Report()
        {
            string status = TakeStatus();
            if (status != null) Log.Debug("[ScanGate] " + status);
        }
    }
}
