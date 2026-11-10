using BepInEx.Logging;


namespace FiresEasyBakeMeshes
{
    internal static class EasyBakeLog
    {
        private static ManualLogSource _log;

        // BepInEx prints "[Message:FiresEasyBakeMeshes] " before every line here, which is 30 of the console's 100
        // columns. The summary lines are dense by design - [Skip] ran to 582 columns - so they are folded to fit
        // instead of being written short. Measured once on Bind rather than per line.
        private static int _prefixColumns = CoreLink.ConsoleColumns;

        public static void Bind(ManualLogSource log)
        {
            _log = log;
            _prefixColumns = CoreLink.PrefixColumnsFor(log != null ? log.SourceName : null);
        }

        public static ManualLogSource Source => _log;

        public static void Info(string msg)  { if (_log != null) _log.LogMessage(Fit(msg)); }
        // 1.2.85 (Fire: "we should not be adding more noisy info logs ... put them as debug logs and set our configs to see them";
        // the team, 19:05: BepInEx's own LogLevels stay as they are, a global Debug wakes every mod's spam): Debug lines follow
        // Core's [Logging] Log Level (FiresLogger.DebugEnabled, Core 0.2.279+, read by name). On (the rigs: Debug), they go out
        // at Info, a level BepInEx already shows; off (players), nothing is written. A Core without the flag counts as off.
        // 1.2.143 (Fire 10-28, the team rule: proof / why lines at Debug, never Info): written with LogDebug (the rigs record Debug).
        public static void Debug(string msg) { if (_log != null && DebugOn()) _log.LogDebug(Fit(msg)); }

        private static System.Func<bool> s_debugOn;
        internal static System.Func<bool> Registered;
        internal static void SetDebugSwitch(System.Func<bool> on) { Registered = on; s_debugOn = null; }

        internal static bool DebugOn()
        {
            if (s_debugOn == null)
            {
                // 1.2.208: EBM never looks Core up. A logging system pushes its Debug switch through EbmApi.RegisterDebugSwitch; none = BepInEx's own levels decide
                s_debugOn = Registered ?? (() => true);
            }
            try { return s_debugOn(); } catch { return false; }
        }
        public static void Warn(string msg)  { if (_log != null) _log.LogWarning(Fit(msg)); }
        public static void Error(string msg) { if (_log != null) _log.LogError(Fit(msg)); }

        private static string Fit(string msg) => CoreLink.Fit(msg, _prefixColumns);
    }
}
