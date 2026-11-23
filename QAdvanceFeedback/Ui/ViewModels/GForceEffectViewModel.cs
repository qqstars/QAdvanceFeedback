using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// One G-Force maximum (acceleration, braking or cornering), following the plugin page's pattern:
    /// <list type="bullet">
    /// <item>an Auto toggle;</item>
    /// <item>the max-G box, ALWAYS editable (under Auto it is the default Auto falls back to);</item>
    /// <item>the readout label (<see cref="GForceReadout"/>).</item>
    /// </list>
    /// </summary>
    public sealed class GForceAxisViewModel : ObservableObject
    {
        private readonly IPanelHost _host;
        private bool _auto;
        private double _max;
        private string _readout = string.Empty;

        public GForceAxisViewModel(IPanelHost host, string label, SettingField modeField, SettingField maxField)
        {
            _host = host;
            Label = label;
            ModeField = modeField;
            MaxField = maxField;
        }

        public string Label { get; }
        public SettingField ModeField { get; }
        public SettingField MaxField { get; }

        /// <summary>Auto toggle (two-way); applies at once.</summary>
        public bool Auto
        {
            get => _auto;
            set
            {
                if (!SetProperty(ref _auto, value)) return;
                _host.Hub?.Set(ModeField, value ? GMaxMode.Auto : GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);
                RefreshReadout();
            }
        }

        /// <summary>The max-G value (two-way, 2 decimals); applies at once.</summary>
        public double Max
        {
            get => _max;
            set
            {
                if (!SetProperty(ref _max, value)) return;
                _host.Hub?.Set(MaxField, value, SettingsOrigin.ShakeItPanel);
                RefreshReadout();
            }
        }

        public string Readout { get => _readout; private set => SetProperty(ref _readout, value); }

        /// <summary>Model -> view, without writing anything back.</summary>
        public void Load(QAdvanceFeedbackSettings s)
        {
            if (s?.GForce == null) return;
            SetProperty(ref _auto, (GMaxMode)SettingsHub.Read(s, ModeField) == GMaxMode.Auto, nameof(Auto));
            SetProperty(ref _max, Math.Round(Convert.ToDouble(SettingsHub.Read(s, MaxField)), 2), nameof(Max));
            RefreshReadout();
        }

        public void RefreshReadout() => Readout = GForceReadout.ForAxis(_host.Settings?.GForce, ModeField);
    }

    /// <summary>
    /// The G-Force ShakeIt panel, identical on all four G-Force effects. It holds:
    /// <list type="bullet">
    /// <item>the three maxima;</item>
    /// <item>Lateral feedback direction;</item>
    /// <item>Integrate Wheel Lock and Slip, and Integrate Slip Angle;</item>
    /// <item>the G-Force Test Effect (on the applied settings).</item>
    /// </list>
    /// Shared with the plugin page through the hub. The readouts refresh once a second, because the
    /// learned max moves while driving.
    /// </summary>
    public sealed class GForceEffectViewModel : PanelViewModelBase
    {
        private IDisposable _refresh;
        private bool _available;
        private LateralDirectionMode _lateralDirection;
        private bool _integrate;
        private bool _integrateSlipAngle;

        public GForceEffectViewModel(IPanelHost host, ITicker ticker, IUiDispatcher ui) : base(host, ticker, ui)
        {
            Axes = new[]
            {
                new GForceAxisViewModel(host, Strings.Get("GForce.AccelMax.Label"), SettingField.AccelMaxMode, SettingField.FixedAccelMaxG),
                new GForceAxisViewModel(host, Strings.Get("GForce.DecelMax.Label"), SettingField.DecelMaxMode, SettingField.FixedDecelMaxG),
                new GForceAxisViewModel(host, Strings.Get("GForce.LatMax.Label"), SettingField.LatMaxMode, SettingField.FixedLatMaxG),
            };
            LateralChoices = new[]
            {
                new Choice<LateralDirectionMode>(LateralDirectionMode.Normal, Strings.Get("GForce.LateralDirection.Normal")),
                new Choice<LateralDirectionMode>(LateralDirectionMode.Reversed, Strings.Get("GForce.LateralDirection.Reversed")),
            };
            Test = new GForceTestViewModel(host, () => host.Settings?.GForce, ticker, ui);
        }

        public IReadOnlyList<GForceAxisViewModel> Axes { get; }
        public IReadOnlyList<Choice<LateralDirectionMode>> LateralChoices { get; }
        public GForceTestViewModel Test { get; }

        public bool Available { get => _available; private set => SetProperty(ref _available, value); }

        public LateralDirectionMode LateralDirection
        {
            get => _lateralDirection;
            set
            {
                if (SetProperty(ref _lateralDirection, value))
                    Host.Hub?.Set(SettingField.LateralDirection, value, SettingsOrigin.ShakeItPanel);
            }
        }

        public bool Integrate
        {
            get => _integrate;
            set
            {
                if (SetProperty(ref _integrate, value))
                    Host.Hub?.Set(SettingField.IntegrateWheelLockAndSlip, value, SettingsOrigin.ShakeItPanel);
            }
        }

        /// <summary>"Integrate Slip Angle" (docs/slip-angle-design.md section 10) - the twin of
        /// <see cref="Integrate"/>, through the hub's <see cref="SettingField.IntegrateSlipAngle"/>.</summary>
        public bool IntegrateSlipAngle
        {
            get => _integrateSlipAngle;
            set
            {
                if (SetProperty(ref _integrateSlipAngle, value))
                    Host.Hub?.Set(SettingField.IntegrateSlipAngle, value, SettingsOrigin.ShakeItPanel);
            }
        }

        protected override void OnActivated()
        {
            Refresh();
            _refresh = Ticker.Every(TimeSpan.FromSeconds(1), () => { foreach (GForceAxisViewModel a in Axes) a.RefreshReadout(); });
        }

        protected override void OnDeactivated()
        {
            _refresh?.Dispose();
            _refresh = null;
            Test.Stop();
        }

        protected override void OnHubChanged(SettingsChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            QAdvanceFeedbackSettings s = Host.Settings;
            Available = s?.GForce != null && Host.Hub != null;
            if (s?.GForce == null) return;
            foreach (GForceAxisViewModel a in Axes) a.Load(s);
            SetProperty(ref _lateralDirection, s.GForce.LateralDirection, nameof(LateralDirection));
            SetProperty(ref _integrate, s.GForce.IntegrateWheelLockAndSlip, nameof(Integrate));
            SetProperty(ref _integrateSlipAngle, s.GForce.IntegrateSlipAngle, nameof(IntegrateSlipAngle));
        }
    }
}
