using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.Mvvm;

// ROOT NAMESPACE OUTSIDE QAdvanceFeedback.* (forced, see QAdvanceFeedbackControls.NumericEditor): the
// plugin class QAdvanceFeedback.QAdvanceFeedback shadows that namespace in WPF-generated code.
namespace QAdvanceFeedbackViews
{
    /// <summary>The WPF <see cref="IUiDispatcher"/>: runs work on the dispatcher it was created on.</summary>
    public sealed class WpfUiDispatcher : IUiDispatcher
    {
        private readonly Dispatcher _dispatcher;

        public WpfUiDispatcher(Dispatcher dispatcher = null) { _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher; }

        public void Post(Action action)
        {
            Action guarded = () => ViewGuard.Run(action);
            if (_dispatcher.CheckAccess()) guarded();
            else _dispatcher.BeginInvoke(guarded);
        }

        // After the current layout pass (Loaded priority), when a replacement view has already loaded.
        public void PostLater(Action action) =>
            _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ViewGuard.Run(action)));
    }

    /// <summary>The WPF <see cref="ITicker"/>: a <see cref="DispatcherTimer"/> per subscription.</summary>
    public sealed class DispatcherTicker : ITicker
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherTicker(Dispatcher dispatcher = null) { _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher; }

        public DateTime UtcNow => DateTime.UtcNow;

        public IDisposable Every(TimeSpan interval, Action tick)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher) { Interval = interval };
            timer.Tick += (s, e) => ViewGuard.Run(tick);
            timer.Start();
            return new Subscription(timer);
        }

        private sealed class Subscription : IDisposable
        {
            private DispatcherTimer _timer;
            public Subscription(DispatcherTimer timer) { _timer = timer; }
            public void Dispose() { _timer?.Stop(); _timer = null; }
        }
    }

    /// <summary>A failure in a panel must never reach SimHub's UI; it is reported to the health panel.</summary>
    public static class ViewGuard
    {
        public static void Run(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                HealthRegistry.Report(HealthSubsystems.ShakeItEffects, HealthSeverity.Degraded, "Health.Impact.ShakeItEffects", e.ToString());
            }
        }
    }

    /// <summary><c>{v:Loc Some.Key}</c> - a localized string from the plugin's string tables.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension() { }
        public LocExtension(string key) { Key = key; }
        public string Key { get; set; }
        public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
    }

    /// <summary>The learning LED's converters (docs\learning-maturity-indicator-report.md) - here, not in its
    /// code-behind, which only wires the view (the MVVM rule).</summary>
    public static class LearningLedConverters
    {
        /// <summary>Share of the row height (the text box beside it) the LED takes - within the owner's 1/3 to 1/2.</summary>
        public const double HeightShare = 0.42;

        /// <summary>Collapsed when the indicator is hidden (no space taken).</summary>
        public static readonly VisibleWhenConverter VisibleWhen = new VisibleWhenConverter();

        /// <summary>Row height -> LED diameter.</summary>
        public static readonly System.Windows.Data.IValueConverter Size = new ShareOfConverter(HeightShare);
    }

    /// <summary>A positive, finite length times a fixed share (rounded to 0.1); anything else gives 0.</summary>
    public sealed class ShareOfConverter : System.Windows.Data.IValueConverter
    {
        private readonly double _share;
        public ShareOfConverter(double share) { _share = share; }

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            double h = value is double d && d > 0.0 && !double.IsInfinity(d) ? d : 0.0;
            return Math.Round(h * _share, 1);
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Collapsed when the bound value is false (or null/empty for strings).</summary>
    public sealed class VisibleWhenConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool visible = value is bool b ? b : value is string s ? !string.IsNullOrEmpty(s) : value != null;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
