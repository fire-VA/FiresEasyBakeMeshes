using BepInEx.Logging;

namespace FiresEasyBakeMeshes
{
    internal static class EasyBakeLog
    {
        private static ManualLogSource _log;

        public static void Bind(ManualLogSource log) => _log = log;

        public static ManualLogSource Source => _log;

        public static void Info(string msg)  { if (_log != null) _log.LogMessage(msg); }
        public static void Debug(string msg) { if (_log != null) _log.LogInfo(msg); }
        public static void Warn(string msg)  { if (_log != null) _log.LogWarning(msg); }
        public static void Error(string msg) { if (_log != null) _log.LogError(msg); }
    }
}
