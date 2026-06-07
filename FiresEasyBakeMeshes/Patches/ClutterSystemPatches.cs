using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresEasyBakeMeshes.Patches
{
    // Skip-when-static throttle for ClutterSystem.LateUpdate.
    //
    // ClutterSystem.LateUpdate runs EVERY frame and, even when the player is
    // standing still, walks the full ring of grass patches around the camera
    // (~120 patches at the default 40m distance / 8m patch size) twice: once in
    // GeneratePatches (reset each in-range patch's timeout) and once in
    // TimeoutPatches (age every patch, destroy the ones that fell out of range).
    // FiresDebugginTools profiling of a populated world shows this at ~3 ms
    // EVERY frame — the single largest steady-state cost on a natural (non-
    // megabase) world, and nothing else in EasyBake touches it.
    //
    // The patch maintenance only has real work when the player MOVES:
    //   - GeneratePatches generates at most ONE new patch per frame regardless,
    //     and only when the camera reaches fresh ground.
    //   - Patches time out only when the player leaves them behind.
    //   - The grass-push shader globals (_PlayerPosition / _PlayerOldPosition)
    //     converge to a single point once the player stops, so they need no
    //     further updates while stationary.
    // Parked, re-walking both rings every frame rebuilds an identical patch set —
    // pure wasted work.
    //
    // We skip the whole LateUpdate while the reference position hasn't moved
    // beyond a small threshold, with two escape hatches so nothing goes stale:
    //   - A short TTL (default 0.33s) forces a vanilla pass a few times a second
    //     even while parked, so the grass-push trail finishes decaying and an
    //     async heightmap-ready transition is picked up promptly.
    //   - m_forceRebuild (set by ClearAll / ResetGrass on terrain edits, quality
    //     changes, biome paints) forces a vanilla pass immediately so a requested
    //     rebuild is never swallowed.
    //
    // Net: moving → vanilla every frame (smooth grass-in + push); parked →
    // LateUpdate drops from ~60 Hz to ~3 Hz, reclaiming the ~3 ms/frame with no
    // visible change to the static grass field.
    //
    // Fail-open: if reflection on m_forceRebuild fails, or there's no parked
    // local player, never skip — correctness beats the optimization.
    [HarmonyPatch(typeof(ClutterSystem), "LateUpdate")]
    public static class ClutterSystem_LateUpdate_Patch
    {
        private static Vector3 _lastRunPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        private static float _lastRunTime = -1000f;

        private static FieldInfo s_forceRebuildField;
        private static bool s_forceRebuildChecked;

        // Diagnostic tally — buckets every call by why it skipped or passed
        // through. Emitted as a one-line summary every 5s when ClutterLodVerbose
        // is on; near-zero overhead when off (six int increments per call).
        private static int _ctSkipped, _ctPassMoved, _ctPassTTL, _ctPassForceRebuild,
                           _ctPassNoPlayer, _ctPassFreefly, _ctPassDisabled;
        private static float _lastReportTime;

        [HarmonyPrefix]
        public static bool Prefix(ClutterSystem __instance)
        {
            if (!FiresEasyBakeMeshesPlugin.PluginEnabled.Value)    { _ctPassDisabled++; MaybeReport(); return true; }
            if (!FiresEasyBakeMeshesPlugin.ClutterLodEnabled.Value){ _ctPassDisabled++; MaybeReport(); return true; }

            // Only throttle around a real, on-foot local player. Menu / loading /
            // freefly all defer to vanilla's own camera-vs-player center logic.
            var player = Player.m_localPlayer;
            if (player == null) { _ctPassNoPlayer++; MaybeReport(); return true; }
            if (GameCamera.InFreeFly()) { _ctPassFreefly++; MaybeReport(); return true; }

            // A pending rebuild must run now — don't swallow ClearAll / ResetGrass.
            // This is the single biggest reason the throttle would no-op: terrain
            // edits / biome paint queue heightmap rebuilds, which keeps
            // m_forceRebuild stuck true until Heightmap.HaveQueuedRebuild clears.
            if (GetForceRebuild(__instance)) { _ctPassForceRebuild++; MaybeReport(); return true; }

            Vector3 pos = player.transform.position;
            float now = Time.unscaledTime;
            float threshold = FiresEasyBakeMeshesPlugin.ClutterLodMoveThreshold.Value;
            float maxSeconds = FiresEasyBakeMeshesPlugin.ClutterLodMaxSeconds.Value;

            float dx = pos.x - _lastRunPos.x;
            float dy = pos.y - _lastRunPos.y;
            float dz = pos.z - _lastRunPos.z;
            bool moved = (dx * dx + dy * dy + dz * dz) >= threshold * threshold;
            bool ttlExpired = (now - _lastRunTime) >= maxSeconds;

            if (!moved && !ttlExpired)
            {
                _ctSkipped++;
                MaybeReport();
                return false; // parked — identical patch set, skip the double ring-walk
            }

            if (moved)      _ctPassMoved++;
            else if (ttlExpired) _ctPassTTL++;
            _lastRunPos = pos;
            _lastRunTime = now;
            MaybeReport();
            return true;
        }

        // One-line periodic summary. Off by default; flip ClutterLodVerbose
        // when investigating why the throttle isn't biting (e.g. m_forceRebuild
        // perpetually true, player slowly drifting past threshold, etc.).
        private static void MaybeReport()
        {
            if (!FiresEasyBakeMeshesPlugin.ClutterLodVerbose.Value) return;
            float now = Time.unscaledTime;
            if (now - _lastReportTime < 5f) return;
            _lastReportTime = now;
            int total = _ctSkipped + _ctPassMoved + _ctPassTTL + _ctPassForceRebuild
                      + _ctPassNoPlayer + _ctPassFreefly + _ctPassDisabled;
            if (total == 0) return;
            int skipPct = (_ctSkipped * 100) / total;
            EasyBakeLog.Info(
                $"[ClutterLod] {total} calls in last ~5s — skipped {_ctSkipped} ({skipPct}%), " +
                $"passed: moved={_ctPassMoved} ttl={_ctPassTTL} forceRebuild={_ctPassForceRebuild} " +
                $"noPlayer={_ctPassNoPlayer} freefly={_ctPassFreefly} disabled={_ctPassDisabled}");
            _ctSkipped = _ctPassMoved = _ctPassTTL = _ctPassForceRebuild
                      = _ctPassNoPlayer = _ctPassFreefly = _ctPassDisabled = 0;
        }

        private static bool GetForceRebuild(ClutterSystem cs)
        {
            if (!s_forceRebuildChecked)
            {
                s_forceRebuildField = typeof(ClutterSystem).GetField("m_forceRebuild",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                s_forceRebuildChecked = true;
                if (s_forceRebuildField == null)
                    EasyBakeLog.Warn("[ClutterLod] ClutterSystem.m_forceRebuild not found via reflection — " +
                        "throttle disabled (fail-open: never skips).");
            }
            // Field missing → behave as if a rebuild is always pending so the
            // prefix never skips. Lose the optimization, keep correctness.
            if (s_forceRebuildField == null) return true;
            return (bool) s_forceRebuildField.GetValue(cs);
        }

        public static void ResetState()
        {
            _lastRunPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            _lastRunTime = -1000f;
        }
    }
}
