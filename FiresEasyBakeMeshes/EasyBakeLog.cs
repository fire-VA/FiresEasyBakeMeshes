using BepInEx.Logging;
using FiresCore.Logging;

namespace FiresEasyBakeMeshes
{
    internal static class EasyBakeLog
    {
        private static ManualLogSource _log;

        // BepInEx prints "[Message:FiresEasyBakeMeshes] " before every line here, which is 30 of the console's 100
        // columns. The summary lines are dense by design - [Skip] ran to 582 columns - so they are folded to fit
        // instead of being written short. Measured once on Bind rather than per line.
        private static int _prefixColumns = ConsoleWrap.ConsoleColumns;

        public static void Bind(ManualLogSource log)
        {
            _log = log;
            _prefixColumns = ConsoleWrap.PrefixColumnsFor(log != null ? log.SourceName : null);
        }

        public static ManualLogSource Source => _log;

        public static void Info(string msg)  { if (_log != null) _log.LogMessage(Fit(msg)); }
        // 1.2.85 (Fire: "we should not be adding more noisy info logs ... put them as debug logs and set our configs to see them";
        // the team, 19:05: BepInEx's own LogLevels stay as they are, a global Debug wakes every mod's spam): Debug lines follow
        // Core's [Logging] Log Level (FiresLogger.DebugEnabled, Core 0.2.279+, read by name). On (the rigs: Debug), they go out
        // at Info, a level BepInEx already shows; off (players), nothing is written. A Core without the flag counts as off.
        public static void Debug(string msg) { if (_log != null && DebugOn()) _log.LogInfo(Fit(msg)); }

        private static System.Func<bool> s_debugOn;

        private static bool DebugOn()
        {
            if (s_debugOn == null)
            {
                var getter = HarmonyLib.AccessTools.PropertyGetter(HarmonyLib.AccessTools.TypeByName("FiresCore.Logging.FiresLogger"), "DebugEnabled");
                s_debugOn = getter != null ? (System.Func<bool>)System.Delegate.CreateDelegate(typeof(System.Func<bool>), getter) : () => false;
            }
            try { return s_debugOn(); } catch { return false; }
        }
        public static void Warn(string msg)  { if (_log != null) _log.LogWarning(Fit(msg)); }
        public static void Error(string msg) { if (_log != null) _log.LogError(Fit(msg)); }

        private static string Fit(string msg) => ConsoleWrap.Fit(msg, _prefixColumns);
    }
}
