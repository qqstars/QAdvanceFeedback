using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>A source field edit coming back from the property picker or the script editor.</summary>
    public sealed class SourceEdit
    {
        public SourceEdit(string text, ScriptType scriptType) { Text = text; ScriptType = scriptType; }
        public string Text { get; }
        public ScriptType ScriptType { get; }
    }

    /// <summary>What the Slip Angle tab needs from the running plugin. The settings page implements it;
    /// tests use a stand-in with real settings and a real hub.</summary>
    public interface ISlipAngleTabHost
    {
        /// <summary>The live (applied) settings; null while the plugin is not running.</summary>
        QAdvanceFeedbackSettings Settings { get; }
        SettingsHub Hub { get; }
        string CurrentGameId { get; }
        /// <summary>The latest data-thread snapshot (never the learner itself).</summary>
        SlipAngleStatus Status { get; }
        /// <summary>The tier that selects the Plugin Internal key data identity: the last in-session
        /// confirmed tier for the current game - kept across pause / menu, reset on a game switch
        /// (<see cref="SlipAngleIdentityTier"/>). Not the warning's tier.</summary>
        SourceNativeness IdentityTier { get; }
        /// <summary>The learned window for a source identity by the one display rule
        /// (<see cref="SlipAngleKeyDataModel"/>): the exact (game, car, identity) key's published window -
        /// from the live context, or the persisted last one on the menu after a restart - capped for the
        /// source; null when that exact key is unknown (the tab then shows the source's default).</summary>
        SlipAngleWindow? LearnedWindow(string sourceIdentity);
        int KeyDataRevision { get; }
        /// <summary>Apply and save the live settings now (the key data controls' immediate path).</summary>
        void SaveLiveSettings();
        bool EvaluatorAvailable { get; }
        SourceButtonMode ResolveSourceButton(ScriptType scriptType);
        /// <summary>Picker or script editor; null when cancelled.</summary>
        Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType);
    }

    /// <summary>One Custom wheel source row: text, script type toggle (P/J/N) and the Pick/Edit button.</summary>
    public sealed class SlipAngleSourceRowViewModel : ObservableObject
    {
        private readonly ISlipAngleTabHost _host;
        private readonly Action _edited;
        private string _text = string.Empty;
        private ScriptType _scriptType;
        private bool _loading;

        internal SlipAngleSourceRowViewModel(ISlipAngleTabHost host, string label, Action edited)
        {
            _host = host;
            _edited = edited;
            Label = label;
            CycleScriptTypeCommand = new RelayCommand(() => ScriptType = ScriptSyntax.Cycle(_scriptType));
            ActionCommand = new RelayCommand(() => { _ = RunActionAsync(); });
        }

        public string Label { get; }
        public RelayCommand CycleScriptTypeCommand { get; }
        public RelayCommand ActionCommand { get; }

        public string Text
        {
            get => _text;
            set { if (SetProperty(ref _text, value ?? string.Empty) && !_loading) _edited(); }
        }

        public ScriptType ScriptType
        {
            get => _scriptType;
            set
            {
                if (!SetProperty(ref _scriptType, value)) return;
                NotifyScriptType();
                if (!_loading) _edited();
            }
        }

        public string ScriptLetter => _scriptType == ScriptType.JavaScript ? "J" : _scriptType == ScriptType.NCalc ? "N" : "P";

        public string ScriptTooltip => Strings.Get(_scriptType == ScriptType.JavaScript ? "Sources.ScriptType.TooltipJavaScript"
            : _scriptType == ScriptType.NCalc ? "Sources.ScriptType.TooltipNCalc" : "Sources.ScriptType.TooltipPlain");

        /// <summary>Hidden without the expression evaluator (a plain property is then the only syntax).</summary>
        public bool ShowScriptType => _host.EvaluatorAvailable;

        private SourceButtonMode Mode => _host.ResolveSourceButton(_scriptType);
        public bool ShowAction => Mode != SourceButtonMode.None;
        public string ActionLabel => Strings.Get(Mode == SourceButtonMode.Editor ? "Sources.EditScriptButton" : "Curve.PickButton");

        internal void Load(string text, ScriptType scriptType)
        {
            _loading = true;
            try
            {
                Text = text;
                ScriptType = _host.EvaluatorAvailable ? scriptType : ScriptType.Plain;
            }
            finally { _loading = false; }
            NotifyScriptType();
        }

        private void NotifyScriptType()
        {
            OnPropertyChanged(nameof(ScriptLetter));
            OnPropertyChanged(nameof(ScriptTooltip));
            OnPropertyChanged(nameof(ShowAction));
            OnPropertyChanged(nameof(ActionLabel));
        }

        private async Task RunActionAsync()
        {
            try
            {
                SourceButtonMode mode = Mode;
                if (mode == SourceButtonMode.None) return;
                SourceEdit result = await _host.EditSourceAsync(mode, _text, _scriptType);
                if (result == null) return;
                if (mode == SourceButtonMode.Picker && string.IsNullOrEmpty(result.Text)) return;
                Text = result.Text;
                if (mode == SourceButtonMode.Editor) ScriptType = result.ScriptType;
            }
            catch (Exception e)
            {
                Core.Health.HealthRegistry.Report(Core.Health.HealthSubsystems.SettingsUi, Core.Health.HealthSeverity.Degraded,
                    "Health.Impact.SettingsUi", e.ToString());
            }
        }
    }

    /// <summary>
    /// The plugin page's Slip Angle tab (docs\slip-angle-design.md 9.9), laid out exactly like the Wheel
    /// Lock tab. Two kinds of state, as on the Lock tab:
    /// <list type="bullet">
    /// <item><b>Page settings</b> (source, trigger, weights, curve, pulse, drift / landing / noise): a draft.
    /// <see cref="Load"/> fills it (never dirty), a driver edit raises <see cref="Edited"/> (the page marks
    /// Apply dirty), and the page's Apply calls <see cref="SaveTo"/>. <see cref="RefreshFields"/> adopts
    /// an outside change (ShakeIt panel, config file) of only the named hub fields.</item>
    /// <item><b>Key data</b> (Auto, Per-Game, Min/Best/Max, reset): applies and saves at once, like the Lock
    /// tab's key data - written to the live settings and saved/broadcast through the host (values 700 ms
    /// after typing stops). Keyed by the DRAFT source like the Lock tab (ruling R35, see DraftIdentity).
    /// Under Auto the editors are read-only (<see cref="ValuesEditable"/>) and show the learned window.</item>
    /// </list>
    /// The live status (warning, no-data line, learning status, learned hints) is read from the host's
    /// data-thread snapshot once a second while the view is on screen (<see cref="Activate"/>).
    /// </summary>
    public sealed class SlipAngleTabViewModel : ObservableObject, IActivatable
    {
        private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

        private readonly ISlipAngleTabHost _host;
        private readonly ITicker _ticker;
        private IDisposable _commit, _refresh;
        private string _shownIdentity, _pendingIdentity;
        private int _lastRevision = -1;
        private bool _loading;

        // Draft (page Apply).
        private SourceMode _sourceMode = SourceMode.Manual;
        private TriggerBy _triggerBy = TriggerBy.Any;
        private double? _brake, _throttle, _wMax, _wMin;
        private bool _pulseEnabled;
        private double? _pulseGap, _pulseMin;
        private double? _hold, _stability, _toPercent, _duration, _gamma, _smoothing, _swing, _confirm, _maxFlight, _maxWobble;
        private double? _throttleRise, _throttleLevel;

        // Key data (immediate).
        private bool _auto = true, _perGame, _valuesEditable, _available, _showReset, _showLearned;
        private double? _min, _best, _max;
        private string _keyDataStatus = string.Empty, _minLearned = string.Empty, _bestLearned = string.Empty, _maxLearned = string.Empty;
        private LearningIndicator _maxIndicator = LearningIndicator.Hidden, _bestIndicator = LearningIndicator.Hidden, _minIndicator = LearningIndicator.Hidden;

        // Live status.
        private bool _showWarning, _showNoData;
        private string _warningText = string.Empty, _noDataText = string.Empty, _learningStatus = string.Empty;
        private IReadOnlyList<GraphPoint> _samples = new GraphPoint[0], _sourceCurve = new GraphPoint[0];
        private string _sourceCaption = string.Empty;

        /// <remarks>No UI dispatcher: the page marshals hub changes to the UI thread before calling
        /// <see cref="RefreshFields"/>, and the ticker already ticks on the UI thread.</remarks>
        public SlipAngleTabViewModel(ISlipAngleTabHost host, ITicker ticker)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));

            SourceModeChoices = new[]
            {
                new Choice<SourceMode>(SourceMode.Manual, Strings.Get("Sources.Mode.Manual")),
                new Choice<SourceMode>(SourceMode.Custom, Strings.Get("Sources.Mode.Custom")),
            };
            TriggerByChoices = new[]
            {
                new Choice<TriggerBy>(TriggerBy.Brake, Strings.Get("SlipAngle.TriggerBy.Brake")),
                new Choice<TriggerBy>(TriggerBy.Throttle, Strings.Get("SlipAngle.TriggerBy.Throttle")),
                new Choice<TriggerBy>(TriggerBy.Any, Strings.Get("SlipAngle.TriggerBy.Any")),
            };
            SourceRows = new[]
            {
                new SlipAngleSourceRowViewModel(host, Strings.Get("Sources.FrontLeft"), OnSourceRowEdited),
                new SlipAngleSourceRowViewModel(host, Strings.Get("Sources.FrontRight"), OnSourceRowEdited),
                new SlipAngleSourceRowViewModel(host, Strings.Get("Sources.RearLeft"), OnSourceRowEdited),
                new SlipAngleSourceRowViewModel(host, Strings.Get("Sources.RearRight"), OnSourceRowEdited),
            };

            ProjectorSettings d = ProjectorSettings.CreateShippedDefault(ProjectionChannel.SlipAngle);
            string Fmt(string key, double v) => string.Format(Strings.Get(key), v.ToString("0.#", CultureInfo.InvariantCulture));
            Projector = new ProjectorEditorViewModel(ProjectionChannel.SlipAngle,
                Strings.Get("SlipAngle.Curve.Min") + ":", Strings.Get("SlipAngle.Curve.Best") + ":", Strings.Get("SlipAngle.Curve.Max") + ":",
                Fmt("SlipAngle.Curve.Start.Desc", d.StartInput), Strings.Get("SlipAngle.Curve.Min.Desc"),
                Strings.Get("SlipAngle.Curve.Best.Desc"), Strings.Get("SlipAngle.Curve.Max.Desc"), Fmt("SlipAngle.Curve.End.Desc", d.EndInput));
            Projector.Edited += () => { RefreshGraph(); RaiseEdited(); };
            _markersWithoutStart = new[]
            {
                new GraphMarker(SlipAngleNormalizer.MinPoint, Strings.Get("SlipAngle.Marker.Min")),
                new GraphMarker(SlipAngleNormalizer.BestPoint, Strings.Get("SlipAngle.Marker.Best")),
                new GraphMarker(SlipAngleNormalizer.MaxPoint, Strings.Get("SlipAngle.Marker.Max")),
            };
            _markersWithStart = new[]
            {
                new GraphMarker(SlipAngleNormalizer.StartPoint, Strings.Get("SlipAngle.Marker.Start")),
                _markersWithoutStart[0], _markersWithoutStart[1], _markersWithoutStart[2],
            };
            _keyMarkers = _markersWithStart;
            ResetCommand = new RelayCommand(ResetKeyData);
            Load(host.Settings?.SlipAngle);
        }

        /// <summary>Raised after a driver edit of a page setting (the page marks Apply dirty). Never for a
        /// load, an outside change, or the key data (which applies itself).</summary>
        public event Action Edited;

        // ---------------- 1. live warning / status ----------------

        public bool ShowFallbackWarning { get => _showWarning; private set => SetProperty(ref _showWarning, value); }
        public string FallbackWarningText { get => _warningText; private set => SetProperty(ref _warningText, value); }
        public bool ShowNoData { get => _showNoData; private set => SetProperty(ref _showNoData, value); }
        public string NoDataText { get => _noDataText; private set => SetProperty(ref _noDataText, value); }

        // ---------------- 2. sources ----------------

        public IReadOnlyList<Choice<SourceMode>> SourceModeChoices { get; }
        public IReadOnlyList<SlipAngleSourceRowViewModel> SourceRows { get; }

        public SourceMode SourceMode
        {
            get => _sourceMode;
            set
            {
                if (value != SourceMode.Custom) value = SourceMode.Manual;
                if (!SetDraft(ref _sourceMode, value)) return;
                OnPropertyChanged(nameof(IsCustom));
                OnPropertyChanged(nameof(IsPluginInternal));
                OnDraftSourceChanged();
            }
        }

        public bool IsCustom => _sourceMode == SourceMode.Custom;
        public bool IsPluginInternal => !IsCustom;

        // ---------------- 3. trigger ----------------

        public IReadOnlyList<Choice<TriggerBy>> TriggerByChoices { get; }

        public TriggerBy TriggerBy
        {
            get => _triggerBy;
            set
            {
                if (!SetDraft(ref _triggerBy, value)) return;
                OnPropertyChanged(nameof(ShowBrakeThreshold));
                OnPropertyChanged(nameof(ShowThrottleThreshold));
            }
        }

        public bool ShowBrakeThreshold => _triggerBy != TriggerBy.Throttle;
        public bool ShowThrottleThreshold => _triggerBy != TriggerBy.Brake;
        public double? BrakeThreshold { get => _brake; set => SetDraft(ref _brake, value); }
        public double? ThrottleThreshold { get => _throttle; set => SetDraft(ref _throttle, value); }

        // ---------------- 4. wheel combination ----------------

        public double? WeightMax { get => _wMax; set => SetDraft(ref _wMax, value); }
        public double? WeightMin { get => _wMin; set => SetDraft(ref _wMin, value); }

        // ---------------- 5. key data (immediate) ----------------

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
                SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
                if (!_loading && live?.KeyData != null)
                {
                    // Against the DRAFT source, then saved and broadcast - the Lock tab's CommitKeyDataToggles.
                    live.KeyData.SetAutoGenerate(DraftIdentity(), value);
                    _host.SaveLiveSettings();
                }
                RefreshKeyData();
            }
        }

        /// <summary>Per-Game (two-way, Manual only, saved at once). A pending edit belongs to the slot it was
        /// typed for, so it is written first.</summary>
        public bool PerGame
        {
            get => _perGame;
            set
            {
                if (value == _perGame) return;
                if (_commit != null) CommitNow();
                SetProperty(ref _perGame, value);
                SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
                if (!_loading && live?.KeyData != null)
                {
                    live.KeyData.SetPerGame(DraftIdentity(), value);
                    _host.SaveLiveSettings();
                }
                RefreshKeyData();
            }
        }

        /// <summary>Min/Best/Max editable (text box and +/- together) only under Manual (owner, 9.4).</summary>
        public bool ValuesEditable { get => _valuesEditable; private set => SetProperty(ref _valuesEditable, value); }
        public bool ShowPerGame => !_auto;

        public double? Min { get => _min; set => SetValue(ref _min, value); }
        /// <summary>Read-only "Start: 2° (50% of Min)" line (derived from Min; docs\slip-angle-design.md 8).</summary>
        public string StartText => SlipAngleStartText.For(_min);
        public double? Best { get => _best; set => SetValue(ref _best, value); }
        public double? Max { get => _max; set => SetValue(ref _max, value); }

        public bool ShowLearnedHints { get => _showLearned; private set => SetProperty(ref _showLearned, value); }
        public string MinLearnedText { get => _minLearned; private set => SetProperty(ref _minLearned, value); }
        public string BestLearnedText { get => _bestLearned; private set => SetProperty(ref _bestLearned, value); }
        public string MaxLearnedText { get => _maxLearned; private set => SetProperty(ref _maxLearned, value); }

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

        /// <summary>Red key data line: invalid window, or Per-Game with no game running.</summary>
        public string KeyDataStatus { get => _keyDataStatus; private set => SetProperty(ref _keyDataStatus, value); }

        /// <summary>Handover confidence / window quality, plus the unconfirmed-peak lines (9.5).</summary>
        public string LearningStatus { get => _learningStatus; private set => SetProperty(ref _learningStatus, value); }

        /// <summary>The unit-aware key data heading (<see cref="SettingsHub.SlipAngleKeyDataHeaderKey"/>) for the
        /// draft source, re-read with the key data.</summary>
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
                if (!_unitLoading) _host.Hub?.Set(SettingField.SlipAngleSourceUnit, (SlipAngleUnitChoice?)value, SettingsOrigin.PluginPage);
            }
        }
        private SlipAngleUnitChoice _unitChoice;
        private bool _unitLoading;

        /// <summary>Restore default: a hard-coded game back to its own unit, any other game to Auto-detect.</summary>
        public RelayCommand ResetUnitCommand => _resetUnitCommand ?? (_resetUnitCommand =
            new RelayCommand(() => _host.Hub?.Set(SettingField.SlipAngleSourceUnit, null, SettingsOrigin.PluginPage)));
        private RelayCommand _resetUnitCommand;

        private void RefreshUnitChoice(SourceMode mode, string gameId, SlipAngleSourceUnits units)
        {
            ShowUnitChoice = SettingsHub.ShowSlipAngleUnitChoice(mode, gameId, units);
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


        public bool ShowReset { get => _showReset; private set => SetProperty(ref _showReset, value); }
        public RelayCommand ResetCommand { get; }

        // ---------------- 6. graphs ----------------

        public ProjectorEditorViewModel Projector { get; }
        /// <summary>Dashed markers at Normalized 15 (Start, only when Min >= 2) / 30 / 60 / 80 on the left graph.</summary>
        public IReadOnlyList<GraphMarker> KeyMarkers { get => _keyMarkers; private set => SetProperty(ref _keyMarkers, value); }
        private readonly GraphMarker[] _markersWithStart, _markersWithoutStart;
        private IReadOnlyList<GraphMarker> _keyMarkers;

        /// <summary>The 15 marker is only drawn while the window has a Start (Min of 2 degrees or more).</summary>
        private void UpdateStartDisplay()
        {
            OnPropertyChanged(nameof(StartText));
            KeyMarkers = _min.HasValue && ClampMath.IsFinite(_min.Value) && new SlipAngleWindow(_min.Value, 0.0, 0.0).Start != null
                ? _markersWithStart : _markersWithoutStart;
        }

        /// <summary>Right graph samples, value coordinates: X = Raw.All degrees (0 to End), Y = projected.</summary>
        public IReadOnlyList<GraphPoint> SourceGraphSamples { get => _samples; private set => SetProperty(ref _samples, value); }
        /// <summary>The same samples with X as percent of End, for the shared 0-100 graph control.</summary>
        public IReadOnlyList<GraphPoint> SourceGraphCurve { get => _sourceCurve; private set => SetProperty(ref _sourceCurve, value); }
        public string SourceGraphCaption { get => _sourceCaption; private set => SetProperty(ref _sourceCaption, value); }

        // ---------------- 7. pulse ----------------

        public bool PulseEnabled { get => _pulseEnabled; set => SetDraft(ref _pulseEnabled, value); }
        public double? PulseGapMs { get => _pulseGap; set => SetDraft(ref _pulseGap, value); }
        public double? PulseMinValue { get => _pulseMin; set => SetDraft(ref _pulseMin, value); }

        // ---------------- 8. drift / landing / noise (9.7) ----------------

        public double? HoldMs { get => _hold; set => SetDraft(ref _hold, value); }
        public double? StabilityRange { get => _stability; set => SetDraft(ref _stability, value); }
        public double? ToPercent { get => _toPercent; set => SetDraft(ref _toPercent, value); }
        public double? DurationMs { get => _duration; set => SetDraft(ref _duration, value); }
        public double? Gamma { get => _gamma; set => SetDraft(ref _gamma, value); }
        public double? ThrottleRiseClear { get => _throttleRise; set => SetDraft(ref _throttleRise, value); }
        public double? ThrottleLevelClear { get => _throttleLevel; set => SetDraft(ref _throttleLevel, value); }
        public double? MaxFlightMs { get => _maxFlight; set => SetDraft(ref _maxFlight, value); }
        public double? MaxWobbleMs { get => _maxWobble; set => SetDraft(ref _maxWobble, value); }
        public double? SmoothingMs { get => _smoothing; set => SetDraft(ref _smoothing, value); }
        public double? SwingMargin { get => _swing; set => SetDraft(ref _swing, value); }
        public double? TrendConfirmMs { get => _confirm; set => SetDraft(ref _confirm, value); }

        public const double MinFlightMs = 50, MaxFlightMsLimit = 5000, MinWobbleMs = 0, MaxWobbleMsLimit = 5000;

        // ================= lifecycle =================

        /// <summary>The view is on screen: refresh now and once a second.</summary>
        public void Activate()
        {
            _refresh?.Dispose();
            RefreshLive();
            RefreshKeyData();
            _refresh = _ticker.Every(RefreshInterval, () =>
            {
                RefreshLive();
                if (_auto || _host.KeyDataRevision != _lastRevision) RefreshKeyData();
            });
        }

        /// <summary>The view left the screen: stop refreshing; a pending key data edit is written, not lost.</summary>
        public void Deactivate()
        {
            _refresh?.Dispose();
            _refresh = null;
            if (_commit != null) CommitNow();
        }

        // ================= page Apply path =================

        /// <summary>Fill the draft from <paramref name="s"/> (a load: never <see cref="Edited"/>).</summary>
        public void Load(SlipAngleChannelSettings s)
        {
            s = s ?? new SlipAngleChannelSettings();
            _loading = true;
            try
            {
                SourceMode = s.SourceMode;
                WheelSourceSet c = s.CustomSources ?? new WheelSourceSet();
                SourceRows[0].Load(c.FrontLeft, c.ScriptTypeFrontLeft);
                SourceRows[1].Load(c.FrontRight, c.ScriptTypeFrontRight);
                SourceRows[2].Load(c.RearLeft, c.ScriptTypeRearLeft);
                SourceRows[3].Load(c.RearRight, c.ScriptTypeRearRight);
                SlipAngleGateSettings g = s.Gate ?? new SlipAngleGateSettings();
                LoadGate(g);
                WeightMax = s.WeightMax;
                WeightMin = s.WeightMin;
                PulseSettings p = s.Pulse ?? new PulseSettings();
                PulseEnabled = p.Enabled;
                PulseGapMs = p.GapMs;
                PulseMinValue = p.MinValue;
                DriftSettings d = s.Drift ?? new DriftSettings();
                HoldMs = Ms(d.HoldSeconds);
                StabilityRange = d.StabilityRange;
                ToPercent = d.ToPercent;
                DurationMs = Ms(d.DurationSeconds);
                Gamma = d.Gamma;
                ThrottleRiseClear = d.ThrottleRiseClear;
                ThrottleLevelClear = d.ThrottleLevelClear;
                SmoothingMs = Ms(d.SmoothingSeconds);
                SwingMargin = d.SwingMargin;
                TrendConfirmMs = Ms(d.TrendConfirmSeconds);
                MaxFlightMs = Ms(g.MaxFlightSeconds);
                MaxWobbleMs = Ms(g.MaxWobbleSeconds);
                Projector.Load(s.Projector);
            }
            finally { _loading = false; }
            RefreshKeyData();   // the draft source may have moved
            RefreshGraph();
        }

        /// <summary>Write the draft into <paramref name="target"/> (the page's Apply). Key data is not
        /// touched - it applied itself already. Every value is clamped to its range.</summary>
        public void SaveTo(SlipAngleChannelSettings target)
        {
            if (target == null) return;
            target.SourceMode = _sourceMode;
            var c = target.CustomSources ?? new WheelSourceSet();
            c.FrontLeft = SourceRows[0].Text; c.ScriptTypeFrontLeft = SourceRows[0].ScriptType;
            c.FrontRight = SourceRows[1].Text; c.ScriptTypeFrontRight = SourceRows[1].ScriptType;
            c.RearLeft = SourceRows[2].Text; c.ScriptTypeRearLeft = SourceRows[2].ScriptType;
            c.RearRight = SourceRows[3].Text; c.ScriptTypeRearRight = SourceRows[3].ScriptType;
            target.CustomSources = c;

            SlipAngleGateSettings g = target.Gate ?? (target.Gate = new SlipAngleGateSettings());
            g.TriggerBy = _triggerBy;
            g.BrakeThreshold = Percent(_brake, g.BrakeThreshold);
            g.ThrottleThreshold = Percent(_throttle, g.ThrottleThreshold);
            g.MaxFlightSeconds = Seconds(_maxFlight, g.MaxFlightSeconds, MinFlightMs, MaxFlightMsLimit);
            g.MaxWobbleSeconds = Seconds(_maxWobble, g.MaxWobbleSeconds, MinWobbleMs, MaxWobbleMsLimit);

            target.WeightMax = _wMax ?? target.WeightMax;
            target.WeightMin = _wMin ?? target.WeightMin;

            PulseSettings p = target.Pulse ?? (target.Pulse = new PulseSettings());
            p.Enabled = _pulseEnabled;
            p.GapMs = _pulseGap ?? p.GapMs;
            p.MinValue = _pulseMin ?? p.MinValue;

            DriftSettings d = target.Drift ?? (target.Drift = new DriftSettings());
            d.HoldSeconds = _hold.HasValue ? _hold.Value / 1000.0 : d.HoldSeconds;
            d.StabilityRange = _stability ?? d.StabilityRange;
            d.ToPercent = _toPercent ?? d.ToPercent;
            d.DurationSeconds = _duration.HasValue ? _duration.Value / 1000.0 : d.DurationSeconds;
            d.Gamma = _gamma ?? d.Gamma;
            d.ThrottleRiseClear = _throttleRise ?? d.ThrottleRiseClear;
            d.ThrottleLevelClear = _throttleLevel ?? d.ThrottleLevelClear;
            d.SmoothingSeconds = _smoothing.HasValue ? _smoothing.Value / 1000.0 : d.SmoothingSeconds;
            d.SwingMargin = _swing ?? d.SwingMargin;
            d.TrendConfirmSeconds = _confirm.HasValue ? _confirm.Value / 1000.0 : d.TrendConfirmSeconds;
            d.Sanitise();

            ProjectorSettings proj = target.Projector ?? (target.Projector = new ProjectorSettings());
            Projector.CopyTo(proj);
        }

        /// <summary>Adopt an outside change (ShakeIt panel, config file) of exactly these hub fields from the
        /// live settings; every other unapplied edit stays. Never <see cref="Edited"/>.</summary>
        public void RefreshFields(IEnumerable<SettingField> fields)
        {
            SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
            if (fields == null || live == null) return;
            bool keyData = false;
            _loading = true;
            try
            {
                foreach (SettingField f in fields)
                {
                    switch (f)
                    {
                        case SettingField.SlipAngleTriggerBy: if (live.Gate != null) TriggerBy = live.Gate.TriggerBy; break;
                        case SettingField.SlipAngleBrakeThreshold: if (live.Gate != null) BrakeThreshold = live.Gate.BrakeThreshold; break;
                        case SettingField.SlipAngleThrottleThreshold: if (live.Gate != null) ThrottleThreshold = live.Gate.ThrottleThreshold; break;
                        case SettingField.SlipAngleKeyDataAuto:
                        case SettingField.SlipAngleKeyDataValues:
                        case SettingField.SourceAvailability:
                        case SettingField.SlipAngleSourceUnit:
                            keyData = true;
                            break;
                    }
                }
            }
            finally { _loading = false; }
            if (keyData)
            {
                _commit?.Dispose();   // the newer outside write wins
                _commit = null;
                RefreshKeyData();
            }
        }

        // ================= key data =================

        /// <summary>
        /// The source identity the key data is read and written for: the DRAFT source, exactly like the Lock
        /// tab's <c>_currentLockSource</c> (ruling R35) - switching the Source combo, or editing a Custom
        /// row, shows that source's key data at once, before Apply. Plugin Internal keys by the identity
        /// tier (Native / Fallback identity; the last in-session tier for the current game, kept across
        /// pause / menu - <see cref="SlipAngleIdentityTier"/>), Custom by its composite identity (<see cref="SettingsHub.SlipAngleIdentity"/>).
        /// </summary>
        private string DraftIdentity()
        {
            if (_sourceMode != SourceMode.Custom)
                return SettingsHub.SlipAngleIdentity(null, _host.IdentityTier, _host.CurrentGameId, _host.Settings?.SlipAngle?.SourceUnits);
            var draft = new SlipAngleChannelSettings { SourceMode = SourceMode.Custom, CustomSources = DraftSources() };
            return SettingsHub.SlipAngleIdentity(draft, _host.IdentityTier);
        }

        private WheelSourceSet DraftSources() => new WheelSourceSet
        {
            FrontLeft = SourceRows[0].Text, ScriptTypeFrontLeft = SourceRows[0].ScriptType,
            FrontRight = SourceRows[1].Text, ScriptTypeFrontRight = SourceRows[1].ScriptType,
            RearLeft = SourceRows[2].Text, ScriptTypeRearLeft = SourceRows[2].ScriptType,
            RearRight = SourceRows[3].Text, ScriptTypeRearRight = SourceRows[3].ScriptType,
        };

        private void OnSourceRowEdited()
        {
            RaiseEdited();
            OnDraftSourceChanged();
        }

        /// <summary>The draft source moved (the Lock tab's OnSourceConfigurationChanged): a pending edit
        /// lands in the slot it was typed for, then the new source's key data is shown.</summary>
        private void OnDraftSourceChanged()
        {
            if (_loading || string.Equals(DraftIdentity(), _shownIdentity, StringComparison.Ordinal)) return;
            if (_commit != null) CommitNow();
            else RefreshKeyData();
        }

        /// <summary>Re-read the applied key data and the live status. Never over a pending edit.</summary>
        public void RefreshKeyData()
        {
            if (_commit != null) return;
            SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
            Available = live?.KeyData != null && _host.Hub != null;
            _lastRevision = _host.KeyDataRevision;
            SlipAngleStatus status = _host.Status ?? SlipAngleStatus.None;
            if (!Available)
            {
                ValuesEditable = false;
                ShowReset = false;
                UpdateIndicators(false, null);
                return;
            }

            string identity = DraftIdentity();
            _shownIdentity = identity;
            KeyDataHeader = Strings.Get(SettingsHub.SlipAngleKeyDataHeaderKey(_sourceMode, _host.IdentityTier, _host.CurrentGameId, live.SourceUnits));
            RefreshUnitChoice(_sourceMode, _host.CurrentGameId, live.SourceUnits);
            string game = _host.CurrentGameId;
            bool auto = live.KeyData.GetAutoGenerate(identity);
            bool perGame = live.KeyData.GetPerGame(identity);

            // THE ONE DISPLAY RULE (owner, 2026-11-19; docs\full-mvvm-plan.md 3.5) - Lock/Slip's: the exact
            // key's learned window (live or, after a restart, the persisted context's), per DRAFT identity so
            // another source never shows this one's evidence; Auto shows it else the source's default; Manual
            // shows the stored slot else the source's default (the one-time seed then fills the slot).
            SlipAngleWindow? learned = _host.LearnedWindow(identity);
            SlipAngleWindow? slot = live.KeyData.TryGetSlot(game, identity, out SlipAngleWindow manual) ? manual : (SlipAngleWindow?)null;
            SlipAngleWindow shown = SlipAngleKeyDataModel.Shown(auto, slot, learned, identity);

            _loading = true;
            try
            {
                SetProperty(ref _auto, auto, nameof(Auto));
                SetProperty(ref _perGame, perGame, nameof(PerGame));
                SetProperty(ref _min, Round(shown.Min), nameof(Min));
                SetProperty(ref _best, Round(shown.Best), nameof(Best));
                SetProperty(ref _max, Round(shown.Max), nameof(Max));
            }
            finally { _loading = false; }
            OnPropertyChanged(nameof(ShowPerGame));
            UpdateStartDisplay();
            ValuesEditable = !auto;
            // Custom has no shipped default to restore to (the Lock tab's CanRestoreKeyDataDefaults, draft mode).
            ShowReset = _sourceMode != SourceMode.Custom && !live.KeyData.IsDefault(identity);

            // The Lock tab's hint rule (RefreshKeyDataChannel: knownSource || manualLive): never under Auto,
            // and for a source with no shipped default (Custom) only once the manual warm-up gate is ready
            // for THIS source - before that the learned value is still the cold-start blend. Plugin
            // Internal is a shipped source (5/8/10).
            // The gate is live state of the APPLIED source: it counts for this one only when they match.
            bool knownSource = _sourceMode != SourceMode.Custom;
            bool matches = string.Equals(status.SourceIdentity, identity, StringComparison.Ordinal);
            bool manualLive = matches && status.ManualGateReady;
            ShowLearnedHints = !auto && (knownSource || manualLive);
            MinLearnedText = Learned(learned.HasValue, learned?.Min ?? 0.0);
            BestLearnedText = Learned(learned.HasValue, learned?.Best ?? 0.0);
            MaxLearnedText = Learned(learned.HasValue, learned?.Max ?? 0.0);
            UpdateIndicators(auto, auto ? (_host as ILearningMaturityHost)?.LearnedSlipAngleMaturity(identity) : null);
            UpdateKeyDataStatus();
            RefreshGraph();
        }

        private void SetValue(ref double? field, double? value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (!SetProperty(ref field, value, name)) return;
            UpdateStartDisplay();
            if (_loading) return;
            UpdateKeyDataStatus();
            RefreshGraph();
            if (!_valuesEditable) return;   // Auto: read-only, nothing to commit
            _commit?.Dispose();
            _commit = IsTypedWindowValid() ? _ticker.Every(CommitDelay, CommitNow) : null;
            _pendingIdentity = _shownIdentity;   // the edit belongs to the source it was typed for
        }

        private bool IsTypedWindowValid() =>
            _min.HasValue && _best.HasValue && _max.HasValue
            && SlipAngleKeyDataSettings.IsValid(Round(_min.Value), Round(_best.Value), Round(_max.Value));

        private void UpdateKeyDataStatus()
        {
            if (_auto) { KeyDataStatus = string.Empty; return; }
            if (_perGame && string.IsNullOrEmpty(_host.CurrentGameId)) { KeyDataStatus = Strings.Get("KeyData.PerGame.NoGame"); return; }
            KeyDataStatus = IsTypedWindowValid() ? string.Empty : Strings.Get("SlipAngle.KeyData.Invalid");
        }

        private void CommitNow()
        {
            _commit?.Dispose();
            _commit = null;
            SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
            if (!_auto && IsTypedWindowValid() && live?.KeyData != null
                && live.KeyData.TryCommitManual(_host.CurrentGameId, _pendingIdentity ?? DraftIdentity(),
                    Round(_min.Value), Round(_best.Value), Round(_max.Value)))
                _host.SaveLiveSettings();   // saved and broadcast at once, like the Lock tab's key data
            _pendingIdentity = null;
            RefreshKeyData();
        }

        private void ResetKeyData()
        {
            _commit?.Dispose();   // a pending edit would otherwise come back after the reset
            _commit = null;
            SlipAngleChannelSettings live = _host.Settings?.SlipAngle;
            if (live?.KeyData == null) return;
            live.KeyData.ResetSource(DraftIdentity());
            _host.SaveLiveSettings();
            RefreshKeyData();
        }

        private static string Learned(bool hasGame, double value) =>
            hasGame && ClampMath.IsFinite(value) && value > 0.0
                ? string.Format(Strings.Get("KeyData.LearnedPrefix"), value.ToString("0.0", CultureInfo.InvariantCulture).PadLeft(5))
                : Strings.Get("KeyData.LearnedPending");

        // ================= live status =================

        private void RefreshLive()
        {
            var t = new SlipAngleLiveText(_host.Status);
            ShowFallbackWarning = t.ShowFallbackWarning;
            FallbackWarningText = t.FallbackWarningText;
            ShowNoData = t.ShowNoData;
            NoDataText = t.NoDataText;
            LearningStatus = t.LearningStatus;
        }

        /// <summary>Shown only while a game runs and the pipeline is healthy (a fault is the Health panel's).</summary>
        internal static string StatusText(SlipAngleStatus s)
        {
            if (s.Faulted || string.IsNullOrEmpty(s.GameId) || s.NoSlipAngleData) return string.Empty;
            string text = string.Format(Strings.Get("SlipAngle.Status.Confidence"),
                Pct(s.Maturity), Pct(s.WindowQuality));
            if (!s.PeakConfirmed)
                text += " - " + Strings.Get("SlipAngle.Status.MaxNotConfirmed") + Environment.NewLine + Strings.Get("SlipAngle.Status.PeakNotReached");
            return text;
        }

        private static string Pct(double v) =>
            (ClampMath.IsFinite(v) ? Math.Round(ClampMath.Clamp(v, 0.0, 1.0) * 100.0) : 0.0).ToString("0", CultureInfo.InvariantCulture);

        // ================= graphs =================

        private void RefreshGraph()
        {
            SlipAngleWindow w = IsTypedWindowValid()
                ? new SlipAngleWindow(Round(_min.Value), Round(_best.Value), Round(_max.Value))
                : (_host.Status ?? SlipAngleStatus.None).AppliedWindow;
            if (!w.IsValid) w = SlipAngleWindow.ShippedDefault;
            IReadOnlyList<GraphPoint> samples = SlipAngleSourceToProjectedGraph.Sample(w, Projector.Working);
            SourceGraphSamples = samples;
            var scaled = new List<GraphPoint>(samples.Count);
            foreach (GraphPoint p in samples) scaled.Add(new GraphPoint(p.X / w.End * 100.0, p.Y));
            SourceGraphCurve = scaled;
            SourceGraphCaption = string.Format(Strings.Get("SlipAngle.Curve.Graph.Source"), w.End.ToString("0.#", CultureInfo.InvariantCulture));
        }

        // ================= helpers =================

        private void LoadGate(SlipAngleGateSettings g)
        {
            TriggerBy = g.TriggerBy;
            BrakeThreshold = g.BrakeThreshold;
            ThrottleThreshold = g.ThrottleThreshold;
        }

        private bool SetDraft<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (!SetProperty(ref field, value, name)) return false;
            if (!_loading) RaiseEdited();
            return true;
        }

        private void RaiseEdited()
        {
            if (!_loading) Edited?.Invoke();
        }

        private static double Ms(double seconds) => Math.Round(seconds * 1000.0, 3);

        private static double Round(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);

        private static double Percent(double? v, double fallback) =>
            v.HasValue && ClampMath.IsFinite(v.Value) ? ClampMath.Clamp(v.Value, 0.0, 100.0) : fallback;

        private static double Seconds(double? ms, double fallbackSeconds, double minMs, double maxMs) =>
            ms.HasValue && ClampMath.IsFinite(ms.Value) ? ClampMath.Clamp(ms.Value, minMs, maxMs) / 1000.0 : fallbackSeconds;
    }

    /// <summary>The live Slip Angle lines shared by the Slip Angle tab and the ShakeIt Slip Angle panel,
    /// from one data-thread status snapshot (R19 + Faulted): the orange fallback warning, the "no slip-angle
    /// data" line and the learning status - each empty when not shown (all of them after a fault).</summary>
    internal sealed class SlipAngleLiveText
    {
        public SlipAngleLiveText(SlipAngleStatus status)
        {
            SlipAngleStatus s = status ?? SlipAngleStatus.None;
            ShowFallbackWarning = s.ShowFallbackWarning;
            FallbackWarningText = s.IsRatioSource ? Strings.Get("Source.RatioWarning.SlipAngle")
                : s.IsCarAngleFallback ? Strings.Get("Source.FallbackWarning.SlipAngle") : string.Empty;
            ShowNoData = s.NoSlipAngleData;
            NoDataText = s.NoSlipAngleData ? Strings.Get("SlipAngle.Status.NoData") : string.Empty;
            LearningStatus = SlipAngleTabViewModel.StatusText(s);
        }

        public bool ShowFallbackWarning { get; }
        public string FallbackWarningText { get; }
        public bool ShowNoData { get; }
        public string NoDataText { get; }
        public string LearningStatus { get; }
    }

    /// <summary>A dashed vertical marker on a graph at <see cref="At"/> (0-100) with a short label.</summary>
    public sealed class GraphMarker
    {
        public GraphMarker(double at, string label) { At = at; Label = label; }
        public double At { get; }
        public string Label { get; }
    }
}
