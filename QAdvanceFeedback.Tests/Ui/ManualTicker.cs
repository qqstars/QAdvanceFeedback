using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>Deterministic <see cref="ITicker"/>: time only moves on <see cref="Advance"/>, which fires
    /// every due tick in time order.</summary>
    public sealed class ManualTicker : ITicker
    {
        private sealed class Entry : IDisposable
        {
            public TimeSpan Interval;
            public Action Tick;
            public DateTime Next;
            public bool Disposed;
            public void Dispose() => Disposed = true;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        public ManualTicker(DateTime? start = null) { UtcNow = start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }

        public DateTime UtcNow { get; private set; }

        public int ActiveCount => _entries.Count(e => !e.Disposed);

        /// <summary>When set, timers really fire at this interval whatever they asked for - the way a
        /// DispatcherTimer drifts from its nominal interval under load.</summary>
        public TimeSpan? ActualInterval { get; set; }

        public IDisposable Every(TimeSpan interval, Action tick)
        {
            interval = ActualInterval ?? interval;
            var e = new Entry { Interval = interval, Tick = tick, Next = UtcNow + interval };
            _entries.Add(e);
            return e;
        }

        public void Advance(TimeSpan by)
        {
            DateTime end = UtcNow + by;
            while (true)
            {
                Entry due = _entries.Where(e => !e.Disposed && e.Next <= end).OrderBy(e => e.Next).FirstOrDefault();
                if (due == null) break;
                UtcNow = due.Next;
                due.Next += due.Interval;
                due.Tick();
            }
            _entries.RemoveAll(e => e.Disposed);
            UtcNow = end;
        }
    }

    /// <summary>Runs posted work inline (view-model tests have no UI thread).</summary>
    public sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
        public void PostLater(Action action) => action();
    }

    /// <summary>Runs <see cref="Post"/> inline but holds <see cref="PostLater"/> work until
    /// <see cref="Flush"/> - like WPF finishing a layout pass before running deferred work.</summary>
    public sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _later = new Queue<Action>();
        public void Post(Action action) => action();
        public void PostLater(Action action) => _later.Enqueue(action);
        public void Flush() { while (_later.Count > 0) _later.Dequeue()(); }
    }
}
