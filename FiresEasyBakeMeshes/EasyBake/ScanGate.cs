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
        private int _checked, _wrong, _wrongFound;
        private int _lastFound = -1;

        private ScanGate(string name)
        {
            _name = name;
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

            // WORK CAN BE PENDING WITHOUT ANYTHING CHANGING. The four counters answer "did the world move",
            // not "is there anything left to do", and those are different questions whenever a budget caps how
            // much of the work a single pass may finish. ZNetScene creates at most ~40 objects a frame, so a
            // 47,898-object backlog sits across passes while near, distant, instances and zone all hold still.
            //
            // The audit caught exactly this on 2026-09-24: "1 of 1 recheck(s) WRONG, 35,153 object(s) missed",
            // during streaming, while the skip rate was 0% so nothing was actually lost. The destroy side reported
            // 0 wrong in the same window because it already required its queue to be empty - this is that same
            // condition, stated once for both: never skip until a real scan has come back with nothing.
            if (_lastFound != 0) return false;

            float now = Time.unscaledTime;
            bool unchanged = near == _near
                          && distant == _distant
                          && instances == _instances
                          && _haveZone && zone.x == _zone.x && zone.y == _zone.y;

            if (!unchanged)
            {
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
                _scanned++;
                return false;
            }

            if (now - _lastScan >= ForceScanSeconds)
            {
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
            string status = $"{_name} {_skipped:N0}/{total:N0} ({_skipped * 100.0 / total:N0}%), {audit}";
            _skipped = 0; _scanned = 0; _checked = 0; _wrong = 0; _wrongFound = 0;
            return status;
        }

        internal static string TakeStatus()
        {
            string create = Create.TakeOwnStatus();
            string destroy = Destroy.TakeOwnStatus();
            if (create == null && destroy == null) return null;
            if (create == null) return "near-scan skipped " + destroy;
            if (destroy == null) return "near-scan skipped " + create;
            return $"near-scan skipped {create} | {destroy}";
        }

        internal static void ResetAll()
        {
            Create.Reset();
            Destroy.Reset();
        }

        internal static void Report()
        {
            string status = TakeStatus();
            if (status != null) Log.Info("[ScanGate] " + status);
        }
    }
}
