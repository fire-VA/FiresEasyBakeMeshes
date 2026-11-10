namespace FiresEasyBakeMeshes.EasyBake
{
    // 1.2.135 ([visual], overnight 10-27, lead GO: lower-end PCs): [General] PerformanceProfile = Default | LowEnd, read ONCE at
    // load. Default changes nothing. LowEnd halves every per-frame bake / convert / stand-in / destroy / material budget (the worst
    // EBM frame drops from ~14 ms to ~7 ms; zones finish baking about twice as slowly), halves the per-frame create budget, moves
    // the far tier in from its configured distance by a quarter, and shortens the light-flicker / SFX LOD distances by a quarter.
    internal static class PerformanceProfile
    {
        internal static bool LowEnd { get; private set; }
        internal static float Budget => LowEnd ? 0.5f : 1f;      // every per-frame ms budget and the create budget
        internal static float Distance => LowEnd ? 0.75f : 1f;   // far tier + light / SFX LOD distances

        internal static void Load(string value)
        {
            LowEnd = string.Equals(value, "LowEnd", System.StringComparison.OrdinalIgnoreCase);
            EasyBakeLog.Debug(LowEnd
                ? "[EBM] performance profile executing: LowEnd (per-frame budgets x0.5, create budget x0.5, far tier + light/SFX LOD distances x0.75)"
                : "[EBM] performance profile executing: Default (configured budgets as they are)");
        }
    }
}
