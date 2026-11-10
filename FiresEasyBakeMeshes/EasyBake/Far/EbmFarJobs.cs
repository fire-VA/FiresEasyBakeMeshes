using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace FiresEasyBakeMeshes.EasyBake.Far
{
    /// <summary>Lane order for <see cref="EbmFarJobs"/> (same four lanes as Core's WorkPriority, drained Critical first).</summary>
    internal enum WorkPriority { Critical = 0, High = 1, Normal = 2, Low = 3 }

    /// <summary>
    /// EBM port (Core 0.2.1216 Far\ moved into EBM; Fire 11-08: EBM relies on no other mod): EBM's own small main-thread queue for the
    /// far view, replacing Core's FrameBudgetScheduler.Schedule / ScheduleCoroutine. Drained from <see cref="FarRunner"/>'s Update
    /// (every frame, client and server) within about 1 ms; at least one item runs each frame so nothing starves. Lanes drain
    /// Critical -> High -> Normal -> Low. Each coroutine gets one MoveNext a frame. Schedule / ScheduleCoroutine may be called from any
    /// thread (they only enqueue); the work always runs on the main thread. A job that throws is logged at Warning and dropped.
    /// The queue only runs once FarRunner exists, which every far system's Initialize (EBM's Awake) creates by registering a part.
    /// </summary>
    internal static class EbmFarJobs
    {
        internal const double BudgetMs = 1.0;
        private const int Lanes = 4;
        private const string LogPrefix = "[Far] jobs:";

        private static readonly object s_lock = new object();
        private static readonly Queue<Action>[] s_actions = { new Queue<Action>(), new Queue<Action>(), new Queue<Action>(), new Queue<Action>() };
        private static readonly List<IEnumerator>[] s_coroutines = { new List<IEnumerator>(), new List<IEnumerator>(), new List<IEnumerator>(), new List<IEnumerator>() };
        private static readonly List<IEnumerator> s_step = new List<IEnumerator>();
        private static readonly List<IEnumerator> s_finished = new List<IEnumerator>();

        /// <summary>Any thread. Runs <paramref name="action"/> once on the main thread, inside the far budget.</summary>
        internal static void Schedule(WorkPriority priority, Action action)
        {
            if (action == null) return;
            lock (s_lock) s_actions[Lane(priority)].Enqueue(action);
        }

        /// <summary>Any thread. Steps <paramref name="steps"/> on the main thread, one MoveNext a frame, until it ends.</summary>
        internal static void ScheduleCoroutine(WorkPriority priority, IEnumerator steps)
        {
            if (steps == null) return;
            lock (s_lock) s_coroutines[Lane(priority)].Add(steps);
        }

        /// <summary>Main thread, from FarRunner.Update.</summary>
        internal static void Drain()
        {
            long start = Stopwatch.GetTimestamp();
            long budget = (long)(BudgetMs * Stopwatch.Frequency / 1000.0);
            bool ranOne = false;
            for (int lane = 0; lane < Lanes; lane++)
            {
                // coroutines: one step each this frame
                s_step.Clear();
                lock (s_lock) { if (s_coroutines[lane].Count > 0) s_step.AddRange(s_coroutines[lane]); }
                if (s_step.Count > 0)
                {
                    s_finished.Clear();
                    foreach (var co in s_step)
                    {
                        if (ranOne && Stopwatch.GetTimestamp() - start >= budget) break;
                        ranOne = true;
                        bool more = false;
                        try { more = co.MoveNext(); }
                        catch (Exception e) { EasyBakeLog.Warn($"{LogPrefix} a coroutine step failed: {e.Message}"); }
                        if (!more) s_finished.Add(co);
                    }
                    if (s_finished.Count > 0) lock (s_lock) foreach (var co in s_finished) s_coroutines[lane].Remove(co);
                    s_step.Clear(); s_finished.Clear();
                }
                // actions: FIFO while the budget lasts
                while (true)
                {
                    if (ranOne && Stopwatch.GetTimestamp() - start >= budget) return;
                    Action a;
                    lock (s_lock) { if (s_actions[lane].Count == 0) break; a = s_actions[lane].Dequeue(); }
                    ranOne = true;
                    try { a(); }
                    catch (Exception e) { EasyBakeLog.Warn($"{LogPrefix} an action failed: {e.Message}"); }
                }
            }
        }

        /// <summary>Items still queued (actions + coroutines), for status lines.</summary>
        internal static int Pending
        {
            get { int n = 0; lock (s_lock) for (int i = 0; i < Lanes; i++) n += s_actions[i].Count + s_coroutines[i].Count; return n; }
        }

        private static int Lane(WorkPriority p) { int l = (int)p; return l < 0 || l >= Lanes ? (int)WorkPriority.Normal : l; }
    }

    /// <summary>
    /// EBM port: EBM's own single-thread worker, replacing Core's Async BackgroundWorker for FarCache. The process function
    /// runs on one below-normal background thread (never touch Unity there); the callback runs on the main thread through
    /// <see cref="EbmFarJobs"/> at High priority (as Core's did through its scheduler). A job or callback that throws is logged
    /// at Warning. The thread is a background thread, so it never holds the game open at quit.
    /// </summary>
    internal sealed class EbmFarWorker<TInput, TOutput>
        where TInput : class
        where TOutput : class, new()
    {
        private readonly string _name;
        private readonly Action<TInput, TOutput> _process;
        private readonly Queue<KeyValuePair<TInput, Action<TOutput>>> _jobs = new Queue<KeyValuePair<TInput, Action<TOutput>>>();
        private readonly object _lock = new object();
        private Thread _thread;

        internal EbmFarWorker(string name, Action<TInput, TOutput> process)
        {
            _name = name ?? "EbmFarWorker";
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        internal void QueueJob(TInput input, Action<TOutput> callback)
        {
            if (input == null || callback == null) return;
            lock (_lock)
            {
                if (_thread == null || !_thread.IsAlive)
                {
                    _thread = new Thread(Loop) { Name = "FiresEBM_" + _name, IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }
                _jobs.Enqueue(new KeyValuePair<TInput, Action<TOutput>>(input, callback));
                Monitor.Pulse(_lock);
            }
        }

        private void Loop()
        {
            while (true)
            {
                KeyValuePair<TInput, Action<TOutput>> job;
                lock (_lock)
                {
                    while (_jobs.Count == 0) Monitor.Wait(_lock);
                    job = _jobs.Dequeue();
                }
                try
                {
                    var output = new TOutput();
                    _process(job.Key, output);
                    var cb = job.Value;
                    EbmFarJobs.Schedule(WorkPriority.High, () =>
                    {
                        try { cb(output); }
                        catch (Exception e) { EasyBakeLog.Warn($"[{_name}] Callback failed: {e.Message}"); }
                    });
                }
                catch (Exception e) { EasyBakeLog.Warn($"[{_name}] Job failed: {e.Message}\n{e.StackTrace}"); }
            }
        }
    }
}
