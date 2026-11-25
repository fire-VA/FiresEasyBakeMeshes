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

        // 1.2.363 ([seasons]): several open stall rows each hold an Info line ("EBM LateUpdate: CacheSummary" ~50 ms every 30 s is counters + ONE Info line;
        // BakeSettle's once-per-zone Info sits inside the 350.5 ms gate call; SaveOutcome's Info inside the sliced-save pump). Profiler markers on the fold vs the
        // BepInEx write, so one capture says whether a log line itself costs tens of ms on the rig. Markers only.
        private static readonly Unity.Profiling.ProfilerMarker s_mFit = new Unity.Profiling.ProfilerMarker("EBM log: fold to console width");
        private static readonly Unity.Profiling.ProfilerMarker s_mWrite = new Unity.Profiling.ProfilerMarker("EBM log: BepInEx write");
        private static string FitM(string msg) { using (s_mFit.Auto()) return Fit(msg); }
        public static void Info(string msg)  { if (_log == null) return; Emit(LogLevel.Message, FitM(msg)); }   // was LogMessage: the same level

        // 1.2.364 ([lead] 11-14, the Info-vs-Debug rule after the 1.2.363 diagnostic leg proved each disk log line a synchronous write, up to 433 ms):
        // a line that repeats while playing (per zone / fill / drain / 30 s summary) is said once per session at Info, every later one at Debug.
        private static readonly System.Collections.Generic.HashSet<string> s_saidFirst = new System.Collections.Generic.HashSet<string>();
        public static void InfoFirst(string key, string msg)
        {
            bool first; lock (s_saidFirst) first = s_saidFirst.Add(key);
            if (first) Info(msg + " (repeating line: the next ones are Debug)"); else Debug(msg);
        }
        // 1.2.85 (Fire: "we should not be adding more noisy info logs ... put them as debug logs and set our configs to see them";
        // the team, 19:05: BepInEx's own LogLevels stay as they are, a global Debug wakes every mod's spam): Debug lines follow
        // Core's [Logging] Log Level (FiresLogger.DebugEnabled, Core 0.2.279+, read by name). On (the rigs: Debug), they go out
        // at Info, a level BepInEx already shows; off (players), nothing is written. A Core without the flag counts as off.
        // 1.2.143 (Fire 10-28, the team rule: proof / why lines at Debug, never Info): written with LogDebug (the rigs record Debug).
        public static void Debug(string msg) { if (_log == null || !DebugOn()) return; Emit(LogLevel.Debug, FitM(msg)); }

        private static System.Func<bool> s_debugOn;
        internal static System.Func<bool> Registered;
        internal static void SetDebugSwitch(System.Func<bool> on) { Registered = on; s_debugOn = null; }

        // 1.2.292 ([lead]): [Debugging] PerfCensus gates everything DebugOn() guards (timers, census, Debug lines), cached as a bool (hot path).
        // A registered logging switch (EbmApi.RegisterDebugSwitch) can only narrow it further, never turn it on.
        private static bool s_census;
        internal static void SetCensusKey(BepInEx.Configuration.ConfigEntry<bool> key)
        {
            if (key == null) return;
            s_census = key.Value;
            key.SettingChanged += (_, __) => s_census = key.Value;
        }

        internal static bool DebugOn()
        {
            if (!s_census) return false;
            if (s_debugOn == null)
            {
                // 1.2.208: EBM never looks Core up. A logging system pushes its Debug switch through EbmApi.RegisterDebugSwitch; none = the census key alone
                s_debugOn = Registered ?? (() => true);
            }
            try { return s_debugOn(); } catch { return false; }
        }
        // 1.2.296 ([lead]): a Verify key's verdict line: Info while that Verify key is on (its leg needs the line whatever PerfCensus says), Debug otherwise
        public static void Say(bool verify, string msg) { if (verify) Info(msg); else Debug(msg); }
        public static void Warn(string msg)  { if (_log == null) return; Emit(LogLevel.Warning, FitM(msg)); }
        public static void Error(string msg) { if (_log == null) return; string m = Fit(msg); if (s_worker != null) { lock (s_writeLock) { DrainLocked(); _log.LogError(m); } } else _log.LogError(m); }   // async: everything queued before it is written first, then the error, at once

        private static string Fit(string msg) => CoreLink.Fit(msg, _prefixColumns);

        // ---- [Debugging] AsyncLog ([seasons] 11-16; FIRE: build it. Plan: Tools\ASYNC_LOG_PLAN_20261115.md). A log line handed to BepInEx runs every log
        // listener on the calling thread (BepInEx 5.4.23.5 Logger.InternalLogEvent: the disk log, Unity's Player.log, the console, other mods' listeners); in
        // capture legs one such line took up to 516 ms on the main thread. With the key on, Info / Debug / Warn lines are folded here as before and queued; the
        // "EBM log" thread hands them to BepInEx in order. BepInEx's listeners are safe from a second thread (the disk writer is TextWriter.Synchronized and
        // already flushed by a timer thread; Unity's WriteStringToUnityLog is marked thread safe; the console can at worst swap a line's colour, as it can today).
        // Error stays immediate. Read once at startup (StartAsync from the config bind); off = the direct write above, unchanged.
        private struct QLine { public LogLevel Level; public string Text; }
        private const int QueueCap = 8192;   // past it only Debug lines are dropped (counted); Info / Warn always queue
        private static readonly System.Collections.Generic.Queue<QLine> s_q = new System.Collections.Generic.Queue<QLine>(256);
        private static readonly System.Collections.Generic.List<QLine> s_batch = new System.Collections.Generic.List<QLine>(256);   // used under s_writeLock only
        private static readonly object s_qLock = new object(), s_writeLock = new object();
        private static readonly System.Threading.AutoResetEvent s_signal = new System.Threading.AutoResetEvent(false);
        private static System.Threading.Thread s_worker;
        private static volatile bool s_async, s_stop;
        private static int s_dropped;
        private static readonly Unity.Profiling.ProfilerMarker s_mQueue = new Unity.Profiling.ProfilerMarker("EBM log: queue a line");
        private static readonly Unity.Profiling.ProfilerMarker s_mWorker = new Unity.Profiling.ProfilerMarker("EBM log: worker write");

        private static void Emit(LogLevel level, string m)
        {
            if (s_async)
            {
                bool queued = false;
                using (s_mQueue.Auto())
                {
                    lock (s_qLock)
                    {
                        if (s_async)   // read again under the lock: StopAsync flips it under the same lock, so a line is either queued before the final drain or written directly ([visual] review)
                        {
                            if (s_q.Count >= QueueCap && level == LogLevel.Debug) { s_dropped++; return; }
                            s_q.Enqueue(new QLine { Level = level, Text = m });
                            queued = true;
                        }
                    }
                    if (queued) s_signal.Set();
                }
                if (queued) return;
            }
            using (s_mWrite.Auto()) _log.Log(level, m);
        }

        // caller holds s_writeLock: everything queued so far goes to BepInEx, in order
        private static bool s_draining;   // under s_writeLock: a listener that reports an exception while we write (Flush from OnUnityLog, same thread) must not start a second drain over s_batch
        private static void DrainLocked()
        {
            if (s_draining) return;
            s_draining = true;
            try
            {
                int dropped;
                lock (s_qLock) { while (s_q.Count > 0) s_batch.Add(s_q.Dequeue()); dropped = s_dropped; s_dropped = 0; }
                for (int i = 0; i < s_batch.Count; i++) { try { _log.Log(s_batch[i].Level, s_batch[i].Text); } catch { } }
                s_batch.Clear();
                if (dropped > 0) { try { _log.LogWarning($"[Log] {dropped} Debug line(s) were not written: EBM's log queue was full ({QueueCap} lines waiting)"); } catch { } }
            }
            finally { s_batch.Clear(); s_draining = false; }
        }

        private static void WorkerRun()
        {
            while (true)
            {
                s_signal.WaitOne(500);
                bool stop = s_stop;
                using (s_mWorker.Auto()) { lock (s_writeLock) DrainLocked(); }
                if (stop) return;
            }
        }

        /// <summary>From the config bind, once: starts the "EBM log" thread when [Debugging] AsyncLog is on.</summary>
        internal static void StartAsync(bool on)
        {
            if (!on || s_worker != null || _log == null) return;
            s_stop = false;
            s_worker = new System.Threading.Thread(WorkerRun) { IsBackground = true, Name = "EBM log" };
            s_worker.Start();
            s_async = true;
            UnityEngine.Application.logMessageReceived += OnUnityLog;
            Info("[Log] AsyncLog on: EBM's log lines are written by a background thread (the game never waits on the log file); errors are still written at once");
        }

        // an exception anywhere: write what is queued now, so the lines before it are in the file if the game goes down
        private static void OnUnityLog(string condition, string stack, UnityEngine.LogType type) { if (type == UnityEngine.LogType.Exception) Flush(); }

        /// <summary>Writes everything queued, on the calling thread. No-op when the async path is off.</summary>
        internal static void Flush() { if (s_worker == null || _log == null) return; lock (s_writeLock) DrainLocked(); }

        /// <summary>At quit and at plugin unload: from here on lines are written directly; the queue is emptied and the thread ends (at most 2 s).</summary>
        internal static void StopAsync()
        {
            var w = s_worker;
            if (w == null) return;
            UnityEngine.Application.logMessageReceived -= OnUnityLog;
            lock (s_qLock) s_async = false;   // under the queue lock: no line can be queued after the final drain below
            s_stop = true; s_signal.Set();
            try { w.Join(2000); } catch { }
            if (_log != null) { lock (s_writeLock) DrainLocked(); }   // a line a thread queued while the switch flipped
            s_worker = null;
        }
    }
}
