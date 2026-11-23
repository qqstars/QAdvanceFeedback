using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The read-only "Source value -> Projected value" graph of a WheelLock/WheelSlip ShakeIt effect:
    /// <list type="bullet">
    /// <item><see cref="Curve"/>, in value coordinates (x = source 0..100, y = projected 0..100);</item>
    /// <item>the Test Effect's <see cref="Balls"/>.</item>
    /// </list>
    /// The curve comes from the applied projector and pattern and the key data the panel shows.
    /// </summary>
    public sealed class GraphViewModel : ObservableObject
    {
        private static readonly IReadOnlyList<TestBall> NoBalls = new TestBall[0];

        private readonly bool _isLock;
        private readonly IPanelHost _host;
        private readonly Func<KeyDataView> _keyData;
        private IReadOnlyList<GraphPoint> _curve = new GraphPoint[0];
        private IReadOnlyList<TestBall> _balls = NoBalls;

        /// <param name="keyData">The key data the panel shows; null resolves it here (a graph without a
        /// panel).</param>
        public GraphViewModel(bool isLock, IPanelHost host, Func<KeyDataView> keyData = null)
        {
            _isLock = isLock;
            _host = host;
            _keyData = keyData;
            Caption = Strings.Get("Curve.Graph.Source");
            Note = Strings.Get("ShakeItPanel.Curve.ReadOnlyNote");
            Refresh();
        }

        public string Caption { get; }
        public string Note { get; }

        public IReadOnlyList<GraphPoint> Curve { get => _curve; private set => SetProperty(ref _curve, value); }
        public IReadOnlyList<TestBall> Balls { get => _balls; set => SetProperty(ref _balls, value ?? NoBalls); }

        /// <summary>The effect's Test Effect, shown below the graph (null for a graph without a panel).</summary>
        public LockSlipTestViewModel Test { get; set; }

        private RelayCommand _refreshCommand;
        /// <summary><see cref="Refresh"/> as a command, for <c>v:EventCommand.Loaded</c>.</summary>
        public RelayCommand RefreshCommand => _refreshCommand ?? (_refreshCommand = new RelayCommand(Refresh));

        public void Refresh()
        {
            WheelChannelSettings channel = _isLock ? _host?.Settings?.Lock : _host?.Settings?.Slip;
            if (channel?.Projector == null) { Curve = new GraphPoint[0]; return; }

            KeyDataView k = _keyData?.Invoke() ?? KeyDataPanelModel.Resolve(channel, _isLock, _host.CurrentGameId,
                _host.LearnedKeyData(_isLock, KeyDataPanelModel.Identity(channel, _isLock)), _host.Settings.KeyDataPointDefaults);

            // Value coordinates: SourceToProjectedGraph draws on a 100x100 canvas (y down), flipped here.
            Curve = SourceToProjectedGraph.Points(channel.Projector, _isLock, channel.NormalizePattern == NormalizePattern.MaxGripOnly,
                    k.SMax, k.S90, k.S75, 100, 100)
                .Select(p => new GraphPoint(p.X, 100 - p.Y)).ToList();
        }
    }

    /// <summary>
    /// A WheelLock/WheelSlip ShakeIt effect: Source, output routing, key data, the graph and the Test
    /// Effect. The container owns one, so its edit panel and its curve-slot graph share it. The edit
    /// panel's lifetime alone drives <see cref="PanelViewModelBase.Activate"/> /
    /// <see cref="PanelViewModelBase.Deactivate"/>; deactivating stops a running test and writes a
    /// pending key data edit.
    /// </summary>
    public sealed class WheelChannelEffectViewModel : PanelViewModelBase
    {
        private readonly bool _isLock;
        private readonly IRoutingTarget _target;
        private IDisposable _refresh;
        private IReadOnlyList<Choice<SourceMode>> _sources = new Choice<SourceMode>[0];
        private SourceMode _source;
        private bool _sourceEnabled;
        private string _status = string.Empty;
        private bool _showFallbackWarning;
        private string _fallbackWarning = string.Empty;
        private string _shakeItNote = string.Empty;
        private bool _showShakeItNote;

        public WheelChannelEffectViewModel(bool isLock, IRoutingTarget target, IPanelHost host, ITicker ticker, IUiDispatcher ui)
            : base(host, ticker, ui)
        {
            _isLock = isLock;
            _target = target ?? throw new ArgumentNullException(nameof(target));
            RoutingChoices = new[] { EffectRouting.Mono, EffectRouting.FrontRear, EffectRouting.LeftRight, EffectRouting.Corners }
                .Select(r => new Choice<EffectRouting>(r, Strings.Get("ShakeItPanel.Routing." + r))).ToArray();
            KeyData = new KeyDataViewModel(isLock, host, ticker, ui);
            Graph = new GraphViewModel(isLock, host, () => KeyData.Current);
            Test = new LockSlipTestViewModel(isLock, host, () => Channel, () => KeyData.Current, () => _target.Routing, ticker, ui);
            Graph.Test = Test;
            Test.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LockSlipTestViewModel.Balls)) Graph.Balls = Test.Balls;
            };
            KeyData.Refreshed += Graph.Refresh;
            Algorithm = new ShakeItAlgorithmViewModel(isLock);
            Algorithm.Edited += (field, value) => Host.Hub?.Set(field, value, SettingsOrigin.ShakeItPanel);
        }

        /// <summary>The ShakeIt algorithm block (docs\shakeit-source-design.md §6): shown while the channel's
        /// source is ShakeIt Built-in; every edit applies through the hub at once, like the rest of the panel.</summary>
        public ShakeItAlgorithmViewModel Algorithm { get; }

        /// <summary>The ShakeIt source state note (docs\shakeit-source-design.md §4): NotSetUp / NotSupported
        /// while the channel's source is ShakeIt Built-in; empty otherwise.</summary>
        public string ShakeItNote { get => _shakeItNote; private set => SetProperty(ref _shakeItNote, value); }
        public bool ShowShakeItNote { get => _showShakeItNote; private set => SetProperty(ref _showShakeItNote, value); }

        public KeyDataViewModel KeyData { get; }
        public GraphViewModel Graph { get; }
        public LockSlipTestViewModel Test { get; }
        public IReadOnlyList<Choice<EffectRouting>> RoutingChoices { get; }

        public IReadOnlyList<Choice<SourceMode>> Sources { get => _sources; private set => SetProperty(ref _sources, value); }
        public bool SourceEnabled { get => _sourceEnabled; private set => SetProperty(ref _sourceEnabled, value); }
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        /// <summary>The orange "fallback estimate" warning: Plugin Internal source on a game whose selected
        /// branch is a speed/RPM estimate.</summary>
        public bool ShowFallbackWarning { get => _showFallbackWarning; private set => SetProperty(ref _showFallbackWarning, value); }
        public string FallbackWarning { get => _fallbackWarning; private set => SetProperty(ref _fallbackWarning, value); }

        private void RefreshFallbackWarning()
        {
            bool show = FallbackWarningViewModel.Applies(Channel?.SourceMode,
                _isLock ? Host.CurrentLockNativeness : Host.CurrentSlipNativeness);
            FallbackWarning = show ? FallbackWarningViewModel.Message : string.Empty;
            ShowFallbackWarning = show;
        }

        /// <summary>Source dropdown (two-way): switches the channel's source through the hub.</summary>
        public SourceMode Source
        {
            get => _source;
            set
            {
                if (SetProperty(ref _source, value))
                    Host.Hub?.Set(_isLock ? SettingField.LockSourceMode : SettingField.SlipSourceMode, value, SettingsOrigin.ShakeItPanel);
            }
        }

        /// <summary>Output channels (two-way): written to the container, which tells ShakeIt.</summary>
        public EffectRouting Routing
        {
            get => _target.Routing;
            set
            {
                if (value == _target.Routing) return;
                _target.Routing = value;
                OnPropertyChanged(nameof(Routing));
                Test.RoutingChanged();
            }
        }

        private WheelChannelSettings Channel => _isLock ? Host.Settings?.Lock : Host.Settings?.Slip;

        private RelayCommand _refreshSourcesCommand;
        /// <summary><see cref="RefreshSources"/> as a command, for <c>v:EventCommand.DropDownOpened</c>.</summary>
        public RelayCommand RefreshSourcesCommand =>
            _refreshSourcesCommand ?? (_refreshSourcesCommand = new RelayCommand(RefreshSources));

        /// <summary>Called by the view when the source dropdown opens: availability may have changed.</summary>
        public void RefreshSources()
        {
            WheelChannelSettings channel = Channel;
            RefreshFallbackWarning();
            SourceEnabled = channel != null && Host.Hub != null;
            Status = channel == null ? Strings.Get("ShakeItPanel.PluginNotRunning") : string.Empty;
            ShakeItSourceState state = ShakeItSourceUi.StateOf(Host, _isLock);
            ShakeItNote = ShakeItSourceUi.Note(channel?.SourceMode, state);
            ShowShakeItNote = ShakeItNote.Length > 0;
            Algorithm.SetSource(channel?.SourceMode);
            if (channel == null) { Sources = new Choice<SourceMode>[0]; return; }

            // ShakeIt is listed while set up, or while it is the channel's source (then marked "not set up").
            IReadOnlyList<SourceMode> options = ShakeItSourceOptions.For(Host.ViperAvailable, channel.CustomSources?.HasContent() == true,
                channel.SourceMode, state != ShakeItSourceState.NotSetUp);
            Choice<SourceMode>[] choices = options.Select(m => new Choice<SourceMode>(m,
                m == SourceMode.ShakeIt ? ShakeItSourceUi.OptionLabel(state) : Strings.Get("Sources.Mode." + m))).ToArray();
            // Replace the list only when it changed, so an open dropdown is not pulled out from under the driver.
            if (!choices.Select(c => c.Value).SequenceEqual(_sources.Select(c => c.Value))
                || !choices.Select(c => c.Label).SequenceEqual(_sources.Select(c => c.Label)))
            {
                Sources = choices;
                // A new list can clear the combo's selection (Choice has reference equality); re-assert it
                // even when the value itself did not change (e.g. the selected ShakeIt item's label flipped).
                _source = channel.SourceMode;
                OnPropertyChanged(nameof(Source));
                return;
            }
            SetProperty(ref _source, channel.SourceMode, nameof(Source));
        }

        protected override void OnActivated()
        {
            Algorithm.Load(Channel?.ShakeItAlgorithm);
            RefreshSources();
            OnPropertyChanged(nameof(Routing));
            KeyData.Activate();
            Graph.Refresh();
            _refresh = Ticker.Every(TimeSpan.FromSeconds(1), () => { RefreshFallbackWarning(); if (KeyData.Auto) Graph.Refresh(); });
        }

        protected override void OnDeactivated()
        {
            _refresh?.Dispose();
            _refresh = null;
            Test.Stop();
            KeyData.Deactivate();
        }

        protected override void OnHubChanged(SettingsChangedEventArgs e)
        {
            foreach (SettingField f in e.Fields)
            {
                if (f == (_isLock ? SettingField.LockSourceMode : SettingField.SlipSourceMode) || f == SettingField.SourceAvailability) RefreshSources();
                if (f == (_isLock ? SettingField.LockProjector : SettingField.SlipProjector)) Graph.Refresh();
                Algorithm.LoadField(f, Channel?.ShakeItAlgorithm);
            }
        }
    }
}
