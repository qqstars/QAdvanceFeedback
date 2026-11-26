using System;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Wheel Lock or Wheel Slip tab (one class, <see cref="IsLock"/> picks the channel; one view,
    /// <c>WheelChannelPageView</c>, used twice - every Lock / Slip difference is a bound property):
    /// Trigger threshold, Sources (with the key data and the Custom reference inside), Wheel combination,
    /// Output data and shaping, Pulse (docs\full-mvvm-plan.md §3.2).
    /// <para/>
    /// <see cref="SourcesViewModel.SourceChanged"/> drives everything keyed by the source: the Custom reference,
    /// the key data and the curve. While the tab is on screen (<see cref="Activate"/>) it re-samples the plugin's
    /// state on <see cref="ISettingsPageHost.PluginStateChanged"/> and on the hub's SourceAvailability broadcast
    /// (ShakeIt state, Viper support, game), and refreshes the fallback warning once a second - the old page's
    /// 1 s timer; showing the tab catches up with anything that changed while it was hidden.
    /// </summary>
    public sealed class WheelChannelPageViewModel : PageSectionViewModel, IActivatable
    {
        private IDisposable _tick;
        private SettingsHub _hub;
        private bool _active;

        public WheelChannelPageViewModel(SettingsPageContext ctx, bool isLock) : base(ctx)
        {
            IsLock = isLock;
            Trigger = new TriggerThresholdViewModel(ctx, isLock);
            Sources = new SourcesViewModel(ctx, isLock);
            CustomReference = new CustomReferenceViewModel(ctx, isLock, Sources);
            Aggregation = new AggregationViewModel(ctx, isLock);
            KeyData = new KeyDataPageViewModel(ctx, isLock);
            Curve = new CurveEditorViewModel(ctx, isLock);
            Pulse = new PulseViewModel(ctx, isLock);
            // The key data the page shows drives the source graph; a pattern edit reaches the key data.
            Curve.Attach(KeyData);
            CalibrationReset = new CalibrationResetViewModel(() => Ctx.Host);

            Sources.SourceChanged += (s, e) =>
            {
                CustomReference.Refresh();
                KeyData.OnSourceChanged();
                Curve.OnSourceChanged();
            };
        }

        public bool IsLock { get; }
        public bool IsSlip => !IsLock;

        public TriggerThresholdViewModel Trigger { get; }
        public SourcesViewModel Sources { get; }
        public CustomReferenceViewModel CustomReference { get; }
        public AggregationViewModel Aggregation { get; }
        public KeyDataPageViewModel KeyData { get; }
        public CurveEditorViewModel Curve { get; }
        public PulseViewModel Pulse { get; }

        /// <summary>Reset Calibration for the current track and car (shared with the other channel's tab).</summary>
        public CalibrationResetViewModel CalibrationReset { get; }

        private CalibrationPersistViewModel _calibrationPersist;

        /// <summary>"Persist Calibration data?" - the page gives both tabs the SAME instance
        /// (<see cref="SettingsPageViewModel"/>); a tab built alone gets its own.</summary>
        public CalibrationPersistViewModel CalibrationPersist
        {
            get => _calibrationPersist ?? (CalibrationPersist = new CalibrationPersistViewModel(() => Ctx.Host));
            set
            {
                if (_calibrationPersist != null) _calibrationPersist.Changed -= CalibrationReset.Refresh;
                _calibrationPersist = value;
                if (_calibrationPersist != null) _calibrationPersist.Changed += CalibrationReset.Refresh;
            }
        }

        public void Activate()
        {
            if (_active) return;
            _active = true;
            Ctx.Host.PluginStateChanged += OnPluginStateChanged;
            _hub = Ctx.Host.Hub;
            if (_hub != null) _hub.Changed += OnHubChanged;
            _tick = Ctx.Ticker.Every(TimeSpan.FromSeconds(1), () =>
            {
                Sources.RefreshFallbackWarning();
                CalibrationReset.Refresh();
                CalibrationPersist.Refresh();
            });
            Sources.Refresh();
            CalibrationReset.Refresh();
            CalibrationPersist.Refresh();
        }

        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            Ctx.Host.PluginStateChanged -= OnPluginStateChanged;
            if (_hub != null) _hub.Changed -= OnHubChanged;
            _hub = null;
            _tick?.Dispose();
            _tick = null;
        }

        // Either may arrive on any thread; the view models are touched on the UI thread only.
        private void OnPluginStateChanged(object sender, EventArgs e) => Ctx.Ui.Post(RefreshIfActive);

        private void OnHubChanged(object sender, SettingsChangedEventArgs e)
        {
            // Any origin, the page's own included: the Viper game button on the OTHER tab broadcasts it.
            if (e?.Fields == null) return;
            foreach (SettingField f in e.Fields)
                if (f == SettingField.SourceAvailability) { Ctx.Ui.Post(RefreshIfActive); return; }
        }

        private void RefreshIfActive()
        {
            if (_active) Sources.Refresh();
        }
    }
}
