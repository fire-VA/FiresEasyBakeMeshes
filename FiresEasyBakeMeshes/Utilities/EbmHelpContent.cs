using UnityEngine;

namespace FiresEasyBakeMeshes.Utilities
{
    // EBM's sections for the shared FiresCore help panel (registration only).
    internal static class EbmHelpContent
    {
        private const string ModId = "Fires Easy Bake Meshes";
        private static bool _registered;

        public static void Register()
        {
            if (_registered || Application.isBatchMode) return;
            _registered = true;

            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "What Easy Bake Does", 0, BuildOverview);
            FiresCore.Help.HelpRegistry.RegisterSection(ModId, "", "Tuning & Troubleshooting", 1, BuildTuning, adminOnly: true);
        }

        private static void BuildOverview(FiresCore.Help.HelpContentWriter w)
        {
            w.Header("What Easy Bake Does");
            w.Paragraph("Fires Easy Bake Meshes is a silent performance mod — no UI, no commands. You " +
                "experience it as smoother frames, especially around large builds and when crossing zone " +
                "borders.");
            w.Divider();

            w.SubHeader("What It's Doing");
            w.Bullet("Mesh batching — invulnerable building pieces sharing a material are combined into " +
                "one mesh per zone, collapsing hundreds of draw calls into a handful. Colliders and " +
                "interactions are untouched.");
            w.Bullet("Prefab prewarm — every prefab is instantiated once during loading, paying the " +
                "hidden first-spawn shader-compile cost up front instead of hitching at zone borders.");
            w.Bullet("GPU instancing — prefabs that reduce to a single mesh and material (palisades, " +
                "floor tiles, stakewalls) are drawn as instances rather than merged, which duplicates " +
                "no vertices and lets a removed piece drop out without rebaking the zone " +
                "([Batching] InstancingEnabled, MinInstancesPerPrefab).");
            w.Bullet("Distance LOD — a baked zone keeps a second mesh built from each piece's LOD1 and " +
                "swaps to it past [Batching] FarTierDistanceMeters (default 64m, which is far enough " +
                "that the swap never happens while you are standing in that zone).");
            w.Bullet("Disk cache — baked meshes persist per world and reload ~4x faster next session.");
            w.Bullet("Zone keepalive — recently-visited baked zones stay pinned briefly instead of being " +
                "destroyed and rebuilt when you cross back.");
            w.Bullet("A set of steady-state CPU trims: time-sliced object destruction, wear-and-tear " +
                "short-circuits for invulnerable pieces, distance-LOD on light flicker/sound/clutter " +
                "updates, and static-piece sync skips.");
            w.Divider();

            w.Paragraph("Everything is local to your machine and can be switched off live — " +
                "[General] PluginEnabled in F8 applies without a restart.");
        }

        private static void BuildTuning(FiresCore.Help.HelpContentWriter w)
        {
            w.AdminHeader("Tuning & Troubleshooting");
            w.SubHeader("If Something Looks Wrong");
            w.Bullet("A piece renders oddly after building/destroying nearby — the zone rebakes a moment " +
                "after changes settle ([Batching] SettleDelaySeconds).");
            w.Bullet("Transparent pieces (glass) are excluded from batching by design " +
                "(ExcludeTransparentPieces) — merged transparency would draw through walls.");
            w.Bullet("Suspect the mod? Flip [General] PluginEnabled off live and compare.");
            w.Divider();

            w.SubHeader("Prewarm Skips");
            w.Paragraph("Prefabs that take pathologically long to instantiate (or have enormous " +
                "hierarchies) are skipped and REMEMBERED:");
            w.Bullet("SlowInstantiateThresholdMs — a prefab slower than this is recorded to " +
                "config/FiresEasyBakeMeshes/prewarm_slow_skips.txt and skipped in future sessions. " +
                "Delete a line to retry it.");
            w.Bullet("MaxTransformCount — skip prefabs with more than N hierarchy nodes (default 800).");
            w.Bullet("SkipNameContains — manual comma-separated skip list.");
            w.Divider();

            w.SubHeader("Key Levers");
            w.Bullet("[Batching] MinPiecesPerZone / MinPiecesPerBatch — when batching engages");
            w.Bullet("[Batching] InstancingEnabled / MinInstancesPerPrefab — instancing vs combining");
            w.Bullet("[Batching] FarTierEnabled / FarTierDistanceMeters — distance LOD for baked zones");
            w.Bullet("[Prewarm] PrefabsPerFrame / FrameBudgetMs — loading-screen pacing");
            w.Bullet("[Cache] PersistToDisk — the per-world mesh cache");
            w.Bullet("[Optimize] ZoneKeepaliveRadius/Seconds — how long zones stay pinned");
            w.Bullet("RunOnServer (Batching + Prewarm) — default OFF on dedicated servers; rendering " +
                "work is wasted headless");
            w.Bullet("Verbose flags per subsystem log exactly what was baked/skipped/pinned");
        }
    }
}
