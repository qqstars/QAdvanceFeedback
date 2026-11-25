using System;
using System.Collections.Generic;
using System.Globalization;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>What the Slip Angle ShakeIt panel needs from the plugin beyond <see cref="IPanelHost"/>:
    /// the data-thread snapshots only, never the learner.</summary>
    public interface ISlipAngleEffectHost
    {
        /// <summary>The latest Slip Angle status (warning, no-data, learned / applied window).</summary>
        SlipAngleStatus SlipAngleStatus { get; }
        /// <summary>The tier that selects the Plugin Internal key data identity: the last in-session
        /// confirmed tier for the current game - kept across pause / menu, reset on a game switch
        /// (<see cref="SlipAngleIdentityTier"/>). Not the warning's tier.</summary>
        SourceNativeness SlipAngleIdentityTier { get; }
        /// <summary>The learned window for a source identity by the one display rule
        /// (<see cref="SlipAngleKeyDataModel"/>) - the same values the Slip Angle tab shows: the exact
        /// (game, car, identity) key's published window (live context, or the persisted last one on the menu
        /// after a restart), capped for the source; null when that exact key is unknown (the panel then
        /// shows the source's default).</summary>
        SlipAngleWindow? LearnedSlipAngleWindow(string sourceIdentity);
    }

    /// <summary>
    /// The read-only "Source -> Projected" graph in the Slip Angle effect's curve slot: Raw.All in
    /// degrees (0 to End) through the normalization and the applied projector - the same samples as the
    /// Slip Angle tab's right-hand graph (<see cref="SlipAngleSourceToProjectedGraph"/>). <see cref="Curve"/>
    /// has X as percent of End for the shared 0-100 graph control; the caption names End in degrees.
    /// </summary>
    public sealed class SlipAngleGraphViewModel : ObservableObject
    {
        private IReadOnlyList<GraphPoint> _curve = new GraphPoint[0];
        private string _caption = string.Empty;

        public SlipAngleGraphViewModel()
        {
            Note = Strings.Get("ShakeItPanel.SlipAngle.Curve.ReadOnlyNote");
        }

        public IReadOnlyList<GraphPoint> Curve { get => _curve; private set => SetProperty(ref _curve, value); }
        public string Caption { get => _caption; private set => SetProperty(ref _caption, value); }
        public string Note { get; }

        internal void Draw(SlipAngleWindow window, ProjectorSettings projector)
        {
            SlipAngleWindow w = window.IsValid ? window : SlipAngleWindow.ShippedDefault;
            if (projector == null) { Curve = new GraphPoint[0]; Caption = string.Empty; return; }
            IReadOnlyList<GraphPoint> samples = SlipAngleSourceToProjectedGraph.Sample(w, projector);
            var scaled = new List<GraphPoint>(samples.Count);
            foreach (GraphPoint p in samples) scaled.Add(new GraphPoint(p.X / w.End * 100.0, p.Y));
            Curve = scaled;
            Caption = string.Format(Strings.Get("SlipAngle.Curve.Graph.Source"), w.End.ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The native ShakeIt "Slip Angle" effect's panel (docs\slip-angle-design.md 9.10), the WheelLock
    /// panel's pattern (<see cref="WheelChannelEffectViewModel"/> + <see cref="KeyDataViewModel"/>):
    /// <list type="bullet">
    /// <item>Trigger By and the threshold(s) it uses - applied at once through the hub;</item>
    /// <item>Auto / Manual and Min/Best/Max - read-only under Auto (<see cref="ValuesEditable"/>), showing the
    /// learned window; a manual edit applies after 700 ms without typing, and a pending edit is written
    /// before an Auto change and when the panel closes. The hub resolves the key data identity itself
    /// (current Native / Fallback tier, ruling R32);</item>
    /// <item>the orange fallback warning, the "no slip-angle data" line and the learning status, read once
    /// a second from the data-thread snapshot (nothing after a fault: the Health panel says it);</item>
    /// <item>the curve-slot <see cref="Graph"/>, redrawn on projector / key data changes and once a second
    /// under Auto.</item>
    /// </list>
    /// Every edit is written with <see cref="SettingsOrigin.ShakeItPanel"/>.
    /// </summary>
    public sealed class SlipAngleEffectViewModel : PanelViewModelBase
    {
        private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private readonly ISlipAngleEffectHost _slip;
        private IDisposable _commit, _refresh;
        private int _lastRevision = -1;
        private string _shownIdentity;
        private bool _loading;

        private TriggerBy _triggerBy = TriggerBy.Any;
        private double? _brake, _throttle;
        private bool _available, _auto = true, _perGame, _valuesEditable;
        private double? _min, _best, _max;
        private LearningIndicator _maxIndicator = LearningIndicator.Hidden, _bestIndicator = LearningIndicator.Hidden, _minIndicator = LearningIndicator.Hidden;
        private SlipAngleWindow _shown = SlipAngleWindow.ShippedDefault;
        private string _status = string.Empty, _keyDataStatus = string.Empty;
        private bool _showWarning, _showNoData;
        private string _warningText = string.Empty, _noDataText = string.Empty, _learningStatus = string.Empty;

        public SlipAngleEffectViewModel(IPanelHost host, ISlipAngleEffectHost slipAngle, ITicker ticker, IUiDispatcher ui)
            : base(host, ticker, ui)
        {
            _slip = slipAngle ?? throw new ArgumentNullException(nameof(slipAngle));
            TriggerByChoices = new[]
            {
                new Choice<TriggerBy>(TriggerBy.Brake, Strings.Get("SlipAngle.TriggerBy.Brake")),
                new Choice<TriggerBy>(TriggerBy.Throttle, Strings.Get("SlipAngle.TriggerBy.Throttle")),
                new Choice<TriggerBy>(TriggerBy.Any, Strings.Get("SlipAngle.TriggerBy.Any")),
            };
            Graph = new SlipAngleGraphViewModel();
            Test = new SlipAngleTestViewModel(host, () => Live, () => _graphWindow, ticker, ui);
        }

        public SlipAngleGraphViewModel Graph { get; }

        /// <summary>The Slip Angle Test Effect (docs\slip-angle-design.md 9.11), shown below the graph. Its
        /// slider spans the window the graph shows; the panel closing stops it.</summary>
        public SlipAngleTestViewModel Test { get; }

        // The window the graph was last drawn from (the typed window while valid, else the shown one).
        private SlipAngleWindow _graphWindow = SlipAngleWindow.ShippedDefault;

        // ---------------- trigger ----------------

        public IReadOnlyList<Choice<TriggerBy>> TriggerByChoices { get; }

        public TriggerBy TriggerBy
        {
            get => _triggerBy;
            set
            {
                if (!SetProperty(ref _triggerBy, value)) return;
                NotifyThresholdVisibility();
                if (!_loading) Host.Hub?.Set(SettingField.SlipAngleTriggerBy, value, SettingsOrigin.ShakeItPanel);
            }
        }

        public bool ShowBrakeThreshold => _triggerBy != TriggerBy.Throttle;
        public bool ShowThrottleThreshold => _triggerBy != TriggerBy.Brake;

        public double? BrakeThreshold
        {
            get => _brake;
            set { if (SetProperty(ref _brake, value)) WriteThreshold(SettingField.SlipAngleBrakeThreshold, value); }
        }

        public double? ThrottleThreshold
        {
            get => _throttle;
            set { if (SetProperty(ref _throttle, value)) WriteThreshold(SettingField.SlipAngleThrottleThreshold, value); }
        }

        // ---------------- key data ----------------

        public bool Available { get => _available; private set => SetProperty(ref _available, value); }

        /// <summary>Auto Generate (two-way, applies at once): a pending manual edit is written first.</summary>
        public bool Auto
        {
            get => _auto;
            set
            {
                if (value == _auto) return;
                if (_commit != null) CommitNow();
                SetProperty(ref _auto, value);
                Host.Hub?.Set(SettingField.SlipAngleKeyDataAuto, value, SettingsOrigin.ShakeItPanel);
                RefreshKeyData();
            }
        }

        /// <summary>Min/Best/Max editable (text box and +/- together) only under Manual (owner, 9.4).</summary>
        public bool ValuesEditable { get => _valuesEditable; private set => SetProperty(ref _valuesEditable, value); }

        public double? Min { get => _min; set => SetValue(ref _min, value); }
        /// <summary>Read-only "Start: 2° (50% of Min)" line (derived from Min; docs\slip-angle-design.md 8).</summary>
        public string StartText => SlipAngleStartText.For(_min);

        /// <summary>The unit-aware key data heading (<see cref="SettingsHub.SlipAngleKeyDataHeaderKey"/>) for the
        /// applied source, re-read with the key data.</summary>
        public string KeyDataHeader { get => _keyDataHeader; private set => SetProperty(ref _keyDataHeader, value); }
        private string _keyDataHeader = Strings.Get("SlipAngle.KeyData.Header");

        // ---- the unit dropdown (owner, 2026-11-22): always shown for Plugin Internal with a game. A hard-coded game
        // offers its own unit and the car angle; any other game Auto-detect (top), the four units and the car angle. A
        // pick moves the game to that list (saved at once); Restore default puts the game back (SettingsHub).

        /// <summary>The current game's entries (<see cref="SlipAngleUnitChoices.For"/>).</summary>
        public IReadOnlyList<Choice<SlipAngleUnitChoice>> UnitChoices { get => _unitChoices; private set => SetProperty(ref _unitChoices, value); }
        private IReadOnlyList<Choice<SlipAngleUnitChoice>> _unitChoices = new Choice<SlipAngleUnitChoice>[0];

        /// <summary>The dropdown shows (one-way; collapsed otherwise).</summary>
        public bool ShowUnitChoice { get => _showUnitChoice; private set => SetProperty(ref _showUnitChoice, value); }
        private bool _showUnitChoice;

        /// <summary>Restore default shows: the game's unit is not its default (<see cref="SettingsHub.SlipAngleUnitIsDefault"/>).</summary>
        public bool ShowUnitReset { get => _showUnitReset; private set => SetProperty(ref _showUnitReset, value); }
        private bool _showUnitReset;

        /// <summary>The current game's list (two-way): a pick writes through the settings hub, which saves and
        /// broadcasts; the key data re-reads from the hub event.</summary>
        public SlipAngleUnitChoice SelectedUnitChoice
        {
            get => _unitChoice;
            set
            {
                if (value == _unitChoice) return;
                SetProperty(ref _unitChoice, value);
                if (!_unitLoading) Host.Hub?.Set(SettingField.SlipAngleSourceUnit, (SlipAngleUnitChoice?)value, SettingsOrigin.ShakeItPanel);
            }
        }
        private SlipAngleUnitChoice _unitChoice;
        private bool _unitLoading;

        /// <summary>Restore default: a hard-coded game back to its own unit, any other game to Auto-detect.</summary>
        public RelayCommand ResetUnitCommand => _resetUnitCommand ?? (_resetUnitCommand =
            new RelayCommand(() => Host.Hub?.Set(SettingField.SlipAngleSourceUnit, null, SettingsOrigin.ShakeItPanel)));
        private RelayCommand _resetUnitCommand;

        private void RefreshUnitChoice(string gameId, SlipAngleSourceUnits units, SlipAngleAngleSource angleSource)
        {
            // Hidden under "Always car angle" (owner, 2026-11-23); the angle source itself is the plugin page's only.
            ShowUnitChoice = SettingsHub.ShowSlipAngleUnitChoice(gameId, units, angleSource);
            ShowUnitReset = ShowUnitChoice && !SettingsHub.SlipAngleUnitIsDefault(gameId, units);
            IReadOnlyList<Choice<SlipAngleUnitChoice>> options = SlipAngleUnitChoices.For(gameId, units);
            SlipAngleUnitChoice? selected = SettingsHub.SlipAngleSelectedUnit(gameId, units);
            _unitLoading = true;   // a new item list must not write its interim selection back
            try
            {
                bool newList = !SlipAngleUnitChoices.Same(UnitChoices, options);
                if (newList) UnitChoices = options;
                if (selected.HasValue) SelectedUnitChoice = selected.Value;
                if (newList) OnPropertyChanged(nameof(SelectedUnitChoice));   // re-select in the new list
            }
            finally { _unitLoading = false; }
        }

        public double? Best { get => _best; set => SetValue(ref _best, value); }
        public double? Max { get => _max; set => SetValue(ref _max, value); }

        /// <summary>The learning indicators (docs\learning-maturity-indicator-report.md): Auto only - collapsed
        /// (no space) under Manual. Max from the peak confirmation, Best / Min from the curve under it. Re-read
        /// with the key data (the Auto toggle, a source change, once a second under Auto).</summary>
        public LearningIndicator MaxIndicator { get => _maxIndicator; private set => SetProperty(ref _maxIndicator, value); }
        public LearningIndicator BestIndicator { get => _bestIndicator; private set => SetProperty(ref _bestIndicator, value); }
        public LearningIndicator MinIndicator { get => _minIndicator; private set => SetProperty(ref _minIndicator, value); }

        private void UpdateIndicators(bool auto, SlipAngleWindowMaturity? m)
        {
            // Peak not confirmed yet: one progress for all three (the fast estimate), and a tooltip that says so. A borrowed
            // window shows half-and-half; a borrowed k only in the tooltip (LearningIndicatorRules.SlipAngle).
            MaxIndicator = LearningIndicatorRules.SlipAngle(m?.Max, auto, m);
            BestIndicator = LearningIndicatorRules.SlipAngle(m?.Best, auto, m);
            MinIndicator = LearningIndicatorRules.SlipAngle(m?.Min, auto, m);
        }

        /// <summary>The window the channel uses now (learned under Auto, the manual slot otherwise).</summary>
        public SlipAngleWindow Current => _shown;

        /// <summary>Red key data line: invalid typed window, or Per-Game with no game running.</summary>
        public string KeyDataStatus { get => _keyDataStatus; private set => SetProperty(ref _keyDataStatus, value); }

        // ---------------- live status ----------------

        /// <summary>"Plugin not running" (empty otherwise).</summary>
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        public bool ShowFallbackWarning { get => _showWarning; private set => SetProperty(ref _showWarning, value); }
        public string FallbackWarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
        public bool ShowNoData { get => _showNoData; private set => SetProperty(ref _showNoData, value); }
        public string NoDataText { get => _noDataText; private set => SetProperty(ref _noDataText, value); }

        /// <summary>The surface whose learning key is in use, and where it came from (as on the tab).</summary>
        public string SurfaceText { get => _surfaceText; private set => SetProperty(ref _surfaceText, value); }
        private string _surfaceText = string.Empty;

        /// <summary>What this surface's key starts from while it has no confirmed window (as on the tab).</summary>
        public string SurfaceBorrowText { get => _surfaceBorrowText; private set => SetProperty(ref _surfaceBorrowText, value); }
        private string _surfaceBorrowText = string.Empty;

        /// <summary>Handover confidence / window quality (and the unconfirmed-peak lines), as on the tab.</summary>
        public string LearningStatus { get => _learningStatus; private set => SetProperty(ref _learningStatus, value); }

        // ================= lifecycle =================

        protected override void OnActivated()
        {
            RefreshGate();
            RefreshLive();
            RefreshKeyData();
            _refresh = Ticker.Every(RefreshInterval, () =>
            {
                RefreshLive();
                if (!_available) RefreshGate();   // the plugin may have started since
                if (_auto || Host.KeyDataRevision != _lastRevision || !string.Equals(Identity(), _shownIdentity, StringComparison.Ordinal))
                    RefreshKeyData();
            });
        }

        protected override void OnDeactivated()
        {
            _refresh?.Dispose();
            _refresh = null;
            if (_commit != null) CommitNow();   // a pending edit must not be lost when the panel closes
            Test.Stop();                         // release the output when the panel closes
        }

        protected override void OnHubChanged(SettingsChangedEventArgs e)
        {
            foreach (SettingField f in e.Fields)
            {
                switch (f)
                {
                    case SettingField.SlipAngleTriggerBy:
                    case SettingField.SlipAngleBrakeThreshold:
                    case SettingField.SlipAngleThrottleThreshold:
                        RefreshGate();
                        break;
                    case SettingField.SourceAvailability:
                        RefreshLive();
                        RefreshKeyData();
                        break;
                    case SettingField.SlipAngleKeyDataAuto:
                    case SettingField.SlipAngleKeyDataValues:
                    case SettingField.SlipAngleSourceUnit:
                    case SettingField.SlipAngleAngleSource:
                        RefreshKeyData();
                        break;
                    case SettingField.SlipAngleProjector:
                        RefreshGraph();
                        break;
                }
            }
        }

        // ================= refresh =================

        private SlipAngleChannelSettings Live => Host.Settings?.SlipAngle;

        /// <summary>The APPLIED source's key data identity (the panel has no draft source).</summary>
        private string Identity() => SettingsHub.SlipAngleIdentity(Live, _slip.SlipAngleIdentityTier, Host.CurrentGameId);

        private void RefreshGate()
        {
            SlipAngleGateSettings g = Live?.Gate;
            if (g == null) return;
            _loading = true;
            try
            {
                SetProperty(ref _triggerBy, g.TriggerBy, nameof(TriggerBy));
                SetProperty(ref _brake, g.BrakeThreshold, nameof(BrakeThreshold));
                SetProperty(ref _throttle, g.ThrottleThreshold, nameof(ThrottleThreshold));
            }
            finally { _loading = false; }
            NotifyThresholdVisibility();
        }

        /// <summary>Re-reads the applied key data. Never over an edit that is still pending.</summary>
        public void RefreshKeyData()
        {
            if (_commit != null) return;
            SlipAngleChannelSettings live = Live;
            Available = live?.KeyData != null && live.Gate != null && Host.Hub != null;
            Status = live == null ? Strings.Get("ShakeItPanel.PluginNotRunning") : string.Empty;
            _lastRevision = Host.KeyDataRevision;
            if (!Available)
            {
                ValuesEditable = false;
                UpdateIndicators(false, null);
                return;
            }

            string identity = Identity();
            _shownIdentity = identity;
            KeyDataHeader = Strings.Get(SettingsHub.SlipAngleKeyDataHeaderKey(_slip.SlipAngleIdentityTier, Host.CurrentGameId, live.SourceUnits, live.AngleSource));
            RefreshUnitChoice(Host.CurrentGameId, live.SourceUnits, live.AngleSource);
            bool auto = live.KeyData.GetAutoGenerate(identity);
            _perGame = live.KeyData.GetPerGame(identity);

            // THE ONE DISPLAY RULE (owner, 2026-11-19; docs\full-mvvm-plan.md 3.5), the tab's: Auto = the
            // exact key's learned window else the source's default; Manual = the stored slot else the default.
            SlipAngleWindow? learned = _slip.LearnedSlipAngleWindow(identity);
            SlipAngleWindow? slot = live.KeyData.TryGetSlot(Host.CurrentGameId, identity, out SlipAngleWindow manual) ? manual : (SlipAngleWindow?)null;
            _shown = SlipAngleKeyDataModel.Shown(auto, slot, learned, identity);

            _loading = true;
            try
            {
                SetProperty(ref _auto, auto, nameof(Auto));
                SetProperty(ref _min, Round(_shown.Min), nameof(Min));
                SetProperty(ref _best, Round(_shown.Best), nameof(Best));
                SetProperty(ref _max, Round(_shown.Max), nameof(Max));
            }
            finally { _loading = false; }
            OnPropertyChanged(nameof(StartText));
            OnPropertyChanged(nameof(Current));
            ValuesEditable = !auto;
            UpdateIndicators(auto, auto ? (Host as ILearningMaturityHost)?.LearnedSlipAngleMaturity(identity) : null);
            UpdateKeyDataStatus();
            RefreshGraph();
        }

        private void RefreshLive()
        {
            var t = new SlipAngleLiveText(_slip.SlipAngleStatus);
            ShowFallbackWarning = t.ShowFallbackWarning;
            FallbackWarningText = t.FallbackWarningText;
            ShowNoData = t.ShowNoData;
            NoDataText = t.NoDataText;
            LearningStatus = t.LearningStatus;
            SurfaceText = t.SurfaceText;
            SurfaceBorrowText = t.SurfaceBorrowText;
        }

        /// <summary>The graph view calls this when shown (it is rebuilt per request and never activates the
        /// panel): with the panel closed the key data is re-read first, so the graph is never stale.</summary>
        private RelayCommand _redrawGraphCommand;
        /// <summary><see cref="RedrawGraph"/> as a command, for <c>v:EventCommand.Loaded</c>.</summary>
        public RelayCommand RedrawGraphCommand =>
            _redrawGraphCommand ?? (_redrawGraphCommand = new RelayCommand(RedrawGraph));

        public void RedrawGraph()
        {
            if (IsActive) RefreshGraph();
            else RefreshKeyData();
        }

        /// <summary>Redraws the curve-slot graph from the shown (or valid typed) window and the applied projector.</summary>
        private void RefreshGraph()
        {
            SlipAngleWindow w = TypedWindow(out SlipAngleWindow typed) ? typed : _shown;
            _graphWindow = w.IsValid ? w : SlipAngleWindow.ShippedDefault;
            Graph.Draw(w, Live?.Projector);
            Test.WindowChanged();
        }

        // ================= edits =================

        private void WriteThreshold(SettingField field, double? value)
        {
            if (_loading || !value.HasValue) return;
            // A real change is broadcast and OnHubChanged re-reads the gate; only a refused write (unchanged
            // after clamping, or no hub) needs the re-read here to put the applied value back.
            if (Host.Hub?.Set(field, value.Value, SettingsOrigin.ShakeItPanel) != true) RefreshGate();
        }

        private void NotifyThresholdVisibility()
        {
            OnPropertyChanged(nameof(ShowBrakeThreshold));
            OnPropertyChanged(nameof(ShowThrottleThreshold));
        }

        private void SetValue(ref double? field, double? value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (!SetProperty(ref field, value, name)) return;
            OnPropertyChanged(nameof(StartText));
            if (_loading) return;
            UpdateKeyDataStatus();
            RefreshGraph();
            if (!_valuesEditable) return;   // Auto: read-only, nothing to commit
            _commit?.Dispose();
            _commit = TypedWindow(out _) ? Ticker.Every(CommitDelay, CommitNow) : null;
        }

        private bool TypedWindow(out SlipAngleWindow window)
        {
            window = default(SlipAngleWindow);
            if (!_min.HasValue || !_best.HasValue || !_max.HasValue) return false;
            double min = Round(_min.Value), best = Round(_best.Value), max = Round(_max.Value);
            if (!SlipAngleKeyDataSettings.IsValid(min, best, max)) return false;
            window = new SlipAngleWindow(min, best, max);
            return true;
        }

        private void UpdateKeyDataStatus()
        {
            if (_auto) { KeyDataStatus = string.Empty; return; }
            if (_perGame && string.IsNullOrEmpty(Host.CurrentGameId)) { KeyDataStatus = Strings.Get("KeyData.PerGame.NoGame"); return; }
            KeyDataStatus = TypedWindow(out _) ? string.Empty : Strings.Get("SlipAngle.KeyData.Invalid");
        }

        private void CommitNow()
        {
            _commit?.Dispose();
            _commit = null;
            // The hub picks the identity (current Native / Fallback tier) and the Per-Game slot itself.
            if (!_auto && TypedWindow(out SlipAngleWindow w))
                Host.Hub?.Set(SettingField.SlipAngleKeyDataValues, w, SettingsOrigin.ShakeItPanel);
            RefreshKeyData();
        }

        private static double Round(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    }
}
