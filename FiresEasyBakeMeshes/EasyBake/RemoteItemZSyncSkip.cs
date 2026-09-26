using System.Collections.Generic;
using UnityEngine;

namespace FiresEasyBakeMeshes.EasyBake
{
    // ── A REMOTE ITEM AT REST IS LERPED TOWARD WHERE IT ALREADY IS, FOREVER ───────────────────────────────
    //
    // MEASURED on the Iso rig 2026-09-26, va_updaters census against the frame ledger:
    //
    //   ZSyncTransform  412 instances - 308 owned (ClientSync exits immediately), 104 REMOTE
    //   MonoUpdaters.FixedUpdate.ZSyncTransform   103-109 ms/s
    //
    // The owned 308 cost almost nothing, so those ~103 ms/s are carried by the 104 remote ones, and the census
    // says exactly what they are:
    //
    //   52 MeadStrength, 26 QueensJam, 18 Fish1, 18 Pukeberries, 17 Blueberries, 15 Seagal
    //
    // Dropped items and fish. Not building pieces - which is why StaticPieceZSyncSkip never sees them: its
    // eligibility requires a Piece component. A dropped mead on the floor is remote, motionless, and runs a
    // full distance check plus lerp every FixedUpdate for the entire session.
    //
    // WHY THIS CANNOT BE PREFAB-CACHED like the piece skip. Eligibility is per-INSTANCE and changes over time:
    // the same MeadStrength prefab is ineligible while it is still tumbling and eligible once it settles. So
    // this keeps a watch list and re-checks on a slow sweep instead of deciding once at creation.
    //
    // AND WHY IT MUST BE REVERSIBLE. For a remote object ZSyncTransform is what brings the owner's position to
    // us. Disable it on something that later MOVES and our copy is stranded. So the sweep also watches the
    // disabled ones: the moment the ZDO position diverges from where our transform sits, sync goes back on and
    // vanilla lerps it home. Worst case is one sweep interval of staleness on an object that was not moving.
    internal static class RemoteItemZSyncSkip
    {
        private const float SweepSeconds = 0.25f;
        private const float MovedEpsilonSqr = 0.04f;   // 0.2 m - below vanilla's own sync threshold
        private const float RestSpeedSqr = 0.01f;      // 0.1 m/s

        private sealed class Watched
        {
            public ZSyncTransform Sync;
            public ZNetView View;
            public Rigidbody Body;
            public bool Disabled;
            public Vector3 DisabledAt;
        }

        private static readonly List<Watched> s_watched = new List<Watched>();
        private static float s_nextSweep;
        private static int s_disabledNow;
        private static int s_reenabled;
        private static float s_nextReport;

        internal static void Reset()
        {
            s_watched.Clear();
            s_disabledNow = 0;
            s_reenabled = 0;
            s_nextSweep = 0f;
            s_nextReport = 0f;
        }

        /// <summary>Called for every object ZNetScene creates. Cheap rejects first.</summary>
        internal static void Consider(GameObject go)
        {
            if (!FiresEasyBakeMeshesPlugin.RemoteItemZSyncSkipEnabled.Value) return;
            if (go == null) return;

            var sync = go.GetComponent<ZSyncTransform>();
            if (sync == null) return;

            // Pieces are StaticPieceZSyncSkip's job and it caches per prefab; do not fight over them.
            if (go.GetComponent<Piece>() != null) return;
            if (go.GetComponent<ItemDrop>() == null) return;

            // Parented objects sync relative to their parent (cart bed, ship deck) - never touch those.
            if (go.transform.parent != null) return;

            var view = go.GetComponent<ZNetView>();
            if (view == null || !view.IsValid()) return;

            s_watched.Add(new Watched
            {
                Sync = sync,
                View = view,
                Body = go.GetComponent<Rigidbody>(),
                Disabled = false,
            });
        }

        internal static void Tick()
        {
            if (!FiresEasyBakeMeshesPlugin.RemoteItemZSyncSkipEnabled.Value) return;
            float now = Time.unscaledTime;
            if (now < s_nextSweep) return;
            s_nextSweep = now + SweepSeconds;

            for (int i = s_watched.Count - 1; i >= 0; i--)
            {
                var w = s_watched[i];
                if (w.Sync == null || w.View == null || !w.View.IsValid())
                {
                    s_watched.RemoveAt(i);
                    if (w.Disabled) s_disabledNow--;
                    continue;
                }

                var zdo = w.View.GetZDO();
                if (zdo == null) continue;

                // Ownership can change under us; an owned object must sync normally again.
                if (zdo.IsOwner())
                {
                    if (w.Disabled) Reenable(w);
                    continue;
                }

                if (w.Disabled)
                {
                    // The owner moved it - hand control back and let vanilla lerp it home.
                    if ((zdo.GetPosition() - w.DisabledAt).sqrMagnitude > MovedEpsilonSqr) Reenable(w);
                    continue;
                }

                if (!AtRest(w)) continue;

                w.Sync.enabled = false;
                w.Disabled = true;
                w.DisabledAt = zdo.GetPosition();
                s_disabledNow++;
            }

            Report(now);
        }

        private static void Reenable(Watched w)
        {
            w.Sync.enabled = true;
            w.Disabled = false;
            s_disabledNow--;
            s_reenabled++;
        }

        // Settled where the owner says it is, and not physically moving.
        private static bool AtRest(Watched w)
        {
            if (w.Body != null && !w.Body.isKinematic)
            {
                if (!w.Body.IsSleeping() && w.Body.linearVelocity.sqrMagnitude > RestSpeedSqr) return false;
            }
            var zdo = w.View.GetZDO();
            return (zdo.GetPosition() - w.Sync.transform.position).sqrMagnitude <= MovedEpsilonSqr;
        }

        private static void Report(float now)
        {
            if (now < s_nextReport) return;
            s_nextReport = now + 30f;
            if (s_disabledNow == 0 && s_reenabled == 0) return;
            EasyBakeLog.Info(
                $"Remote item ZSync skip: {s_disabledNow} settled remote item(s) not lerping, {s_watched.Count} watched, "
                + $"{s_reenabled} handed back after the owner moved them.");
        }
    }
}
