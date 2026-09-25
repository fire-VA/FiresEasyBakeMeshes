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
        public static void Debug(string msg) { if (_log != null) _log.LogInfo(Fit(msg)); }
        public static void Warn(string msg)  { if (_log != null) _log.LogWarning(Fit(msg)); }
        public static void Error(string msg) { if (_log != null) _log.LogError(Fit(msg)); }

        private static string Fit(string msg) => ConsoleWrap.Fit(msg, _prefixColumns);
    }
}
