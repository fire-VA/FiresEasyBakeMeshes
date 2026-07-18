using FiresCore.Logging;
using UnityEngine;

namespace FiresEasyBakeMeshes
{
    // Debounced zone-bake tally. ZoneTracker / MeshBaker record each cache
    // construct or fresh bake; once no new event has landed for DebounceSeconds
    // the accumulated counts emit as a single LoadSummary mini-box instead of
    // one console line per zone (~25-30 at login on a megabase).
    internal static class BakeSummary
    {
        private const float DebounceSeconds = 5f;

        private static readonly LoadSummary.TaggedEmitter Emitter = LoadSummary.For("FiresEasyBakeMeshes");

        private static int _zonesFromCache;
        private static int _zonesBakedFresh;
        private static int _batches;
        private static long _totalMs;
        private static float _lastEventUnscaledTime = -1f;

        public static void RecordCacheConstruct(int batches, long elapsedMs)
        {
            _zonesFromCache++;
            _batches += batches;
            _totalMs += elapsedMs;
            _lastEventUnscaledTime = Time.unscaledTime;
        }

        public static void RecordFreshBake(int batches, long elapsedMs)
        {
            _zonesBakedFresh++;
            _batches += batches;
            _totalMs += elapsedMs;
            _lastEventUnscaledTime = Time.unscaledTime;
        }

        public static void Update()
        {
            if (_lastEventUnscaledTime < 0f) return;
            if (Time.unscaledTime - _lastEventUnscaledTime < DebounceSeconds) return;

            Emitter.EmitMiniBox("🧱 ZONES BAKED", new[]
            {
                $"from cache : {_zonesFromCache}",
                $"baked fresh: {_zonesBakedFresh}",
                $"batches    : {_batches}",
                $"total time : {_totalMs}ms",
            });

            _zonesFromCache = 0;
            _zonesBakedFresh = 0;
            _batches = 0;
            _totalMs = 0;
            _lastEventUnscaledTime = -1f;
        }
    }
}
