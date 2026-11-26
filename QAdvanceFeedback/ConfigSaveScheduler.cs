using System;
using System.Threading;

namespace QAdvanceFeedback
{
    /// <summary>
    /// THE CONFIG FILE IS NEVER WRITTEN ON THE DATA THREAD (owner, 2026-11-25). A save asked for while driving - a key
    /// data seed, a Slip Angle seed on a new surface, a new unit list - only marks the config changed and arms ONE
    /// one-shot <see cref="Timer"/>:
    /// <list type="bullet">
    /// <item><see cref="RequestSave"/>: due <see cref="CoalesceDelay"/> (100 ms), period infinite. Requests closer
    /// together than that re-arm it, so a burst ends in one write 100 ms after its last request.</item>
    /// <item>The tick writes on the timer's own thread at the lowest priority, inside try/finally, under one write lock
    /// - a tick that fires while the previous write is still running waits for it. The finally re-arms for
    /// <see cref="IdleDelay"/> (5 s, period infinite), or for 100 ms again when another request came in meanwhile.
    /// A tick with nothing requested writes nothing.</item>
    /// <item>A failed write (the settings changed under the serialiser, a busy disk) stays requested and is retried.</item>
    /// <item><see cref="SaveNow"/>: the same write, synchronously on the caller - for plugin Init, the settings page's
    /// Apply and End, never the data thread. <see cref="Dispose"/> stops the timer, waits for a running write and
    /// makes the last one.</item>
    /// </list>
    /// </summary>
    public sealed class ConfigSaveScheduler : IDisposable
    {
        public static readonly TimeSpan CoalesceDelay = TimeSpan.FromMilliseconds(100);
        public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(10);

        private readonly Action _write;
        private readonly Action<string> _logWarning;
        private readonly TimeSpan _coalesce;
        private readonly TimeSpan _idle;
        private readonly object _writeLock = new object();
        private readonly Timer _timer;
        private int _requested;
        private int _disposed;
        private string _loggedFault;

        /// <param name="write">Writes the config file (serialise + atomic write). Throwing means "not written".</param>
        /// <param name="coalesce">Tests only; defaults to <see cref="CoalesceDelay"/>.</param>
        /// <param name="idle">Tests only; defaults to <see cref="IdleDelay"/>.</param>
        public ConfigSaveScheduler(Action write, Action<string> logWarning = null, TimeSpan? coalesce = null, TimeSpan? idle = null)
        {
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _logWarning = logWarning;
            _coalesce = coalesce ?? CoalesceDelay;
            _idle = idle ?? IdleDelay;
            _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Writes completed (diagnostics and tests).</summary>
        public int WritesDone => Volatile.Read(ref _writes);
        private int _writes;

        /// <summary>A save is asked for and not written yet.</summary>
        public bool IsRequested => Volatile.Read(ref _requested) != 0;

        /// <summary>
        /// Asks for a save: marks the config changed and arms the timer for <see cref="CoalesceDelay"/>. Any thread -
        /// costs the caller one interlocked write and one timer change, never the write itself.
        /// </summary>
        public void RequestSave()
        {
            Volatile.Write(ref _requested, 1);
            Arm(_coalesce);
        }

        /// <summary>The same write, synchronously on the caller (Init / Apply / End - never the data thread). Also writes
        /// a request still waiting for its tick.</summary>
        public void SaveNow()
        {
            Volatile.Write(ref _requested, 1);
            WriteIfRequested();
        }

        private void Tick()
        {
            Thread thread = Thread.CurrentThread;
            ThreadPriority priority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.Lowest;
                WriteIfRequested();
            }
            catch (Exception e)
            {
                // A timer callback must never throw (it would take SimHub down) - logged once per distinct fault.
                string message = "QAdvanceFeedback: background config save failed - " + e.Message;
                if (!string.Equals(message, _loggedFault, StringComparison.Ordinal))
                {
                    _loggedFault = message;
                    _logWarning?.Invoke(message);
                }
            }
            finally
            {
                try { thread.Priority = priority; } catch (ThreadStateException) { }
                // Re-armed only after the write fully finished: 100 ms again when a request came in meanwhile, else 5 s.
                Arm(IsRequested ? _coalesce : _idle);
            }
        }

        private void WriteIfRequested()
        {
            lock (_writeLock)
            {
                if (Interlocked.Exchange(ref _requested, 0) == 0) return;
                bool written = false;
                try
                {
                    _write();
                    written = true;
                    Interlocked.Increment(ref _writes);
                }
                finally
                {
                    if (!written) Volatile.Write(ref _requested, 1);   // retried by the next tick
                }
            }
        }

        private void Arm(TimeSpan due)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _timer.Change(due, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Stops the timer, waits for a write in progress, then writes a pending request (End).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                var done = new ManualResetEvent(false);
                if (_timer.Dispose(done) && done.WaitOne(DisposeWait)) done.Dispose();
            }
            catch (ObjectDisposedException) { }

            try { WriteIfRequested(); }
            catch (Exception e) { _logWarning?.Invoke("QAdvanceFeedback: final config save failed - " + e.Message); }
        }
    }
}
