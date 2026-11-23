using System;

namespace QAdvanceFeedback.Ui.Mvvm
{
    /// <summary>
    /// Time for view models: repeating ticks (test frames, debounces, refreshes) and the current time
    /// (auto-off). Ticks run on the UI thread. The WPF implementation lives in Ui\Views; tests advance
    /// time by hand.
    /// </summary>
    public interface ITicker
    {
        DateTime UtcNow { get; }

        /// <summary>Calls <paramref name="tick"/> every <paramref name="interval"/> until disposed.</summary>
        IDisposable Every(TimeSpan interval, Action tick);
    }
}
