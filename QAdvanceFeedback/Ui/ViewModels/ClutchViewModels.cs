using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>The clutch state the plugin reports for the panel and tab.</summary>
    public sealed class ClutchHostStatus
    {
        public ClutchSourceKind Kind { get; set; }
        public double? Input { get; set; }
        /// <summary>Learning key (game |#| car |#| source |#|), or null.</summary>
        public string Key { get; set; }
        public double? Learned { get; set; }
        public int Count { get; set; }
        public bool IsMatured { get; set; }
        public double Final { get; set; } = 50;
    }

    /// <summary>What the Clutch view models need from the plugin beyond <see cref="IPanelHost"/>.</summary>
    public interface IClutchHost
    {
        ClutchHostStatus ClutchStatus { get; }
        IReadOnlyList<string> ControllerAxes();
        /// <summary>A controller axis, 0..1, or null when absent.</summary>
        double? ReadAxis01(string name);
        void ForgetClutchBite();
        /// <summary>Publishes a Test Effect frame to Clutch.Bite.Output; null releases it.</summary>
        void PublishClutchTest(double? output0100);
        /// <summary>The last in-game car's final bite, else the configured value.</summary>
        double FinalBiteForTest { get; }
        string FinalBiteForTestKey { get; }
    }

    /// <summary>The read-only clutch input → output graph, in value coordinates (0..100 each).</summary>
    public sealed class ClutchGraphViewModel : ObservableObject
    {
        private IReadOnlyList<GraphPoint> _curve = new GraphPoint[0];
        private double _biteMarker = 50;
        private double? _configuredMarker;
        private GraphPoint? _testBall;
        private double _lo = 20, _hi = 80;

        public IReadOnlyList<GraphPoint> Curve { get => _curve; set => SetProperty(ref _curve, value); }
        public double BiteMarker { get => _biteMarker; set => SetProperty(ref _biteMarker, value); }
        /// <summary>The configured bite, shown faintly under Auto; null under Manual.</summary>
        public double? ConfiguredMarker { get => _configuredMarker; set => SetProperty(ref _configuredMarker, value); }
        /// <summary>The Test Effect ball (input, output) while the test runs.</summary>
        public GraphPoint? TestBall { get => _testBall; set => SetProperty(ref _testBall, value); }
        public double Lo { get => _lo; set => SetProperty(ref _lo, value); }
        public double Hi { get => _hi; set => SetProperty(ref _hi, value); }
    }

    /// <summary>
    /// The Clutch Test Effect (docs\clutch-bite-design.md, "Test Effect").
    /// <list type="bullet">
    /// <item>Reads the fallback pedal axis at 60 Hz, or a slider when no axis is chosen or connected.</item>
    /// <item>Computes the real output (settings, the last in-game car's bite, last direction) and
    /// publishes it to Clutch.Bite.Output, which the native effect plays.</item>
    /// <item>Turns itself off after 10 s without input (pedal movement or slider), on
    /// <see cref="Stop"/>, or when another test begins.</item>
    /// <item>Never feeds the learning.</item>
    /// </list>
    /// </summary>
    public sealed class ClutchTestViewModel : ObservableObject
    {
        public static readonly TimeSpan AutoOff = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / 60.0);
        private const double InputDelta = 0.5;

        private readonly IPanelHost _host;
        private readonly IClutchHost _clutch;
        private readonly ClutchGraphViewModel _graph;
        private readonly ITicker _ticker;
        private readonly IUiDispatcher _ui;
        private readonly ClutchDirectionTracker _tracker = new ClutchDirectionTracker();
        private IDisposable _ticks;
        private DateTime _lastInput;
        private double? _lastAxis;

        private bool _isTestEnabled, _useSlider = true;
        private double _slider;
        private string _inputText = string.Empty, _outputText = string.Empty;

        public ClutchTestViewModel(IPanelHost host, IClutchHost clutch, ClutchGraphViewModel graph, ITicker ticker, IUiDispatcher ui)
        {
            _host = host; _clutch = clutch; _graph = graph; _ticker = ticker; _ui = ui;
        }

        public bool IsTestEnabled
        {
            get => _isTestEnabled;
            set
            {
                if (value == _isTestEnabled) return;
                if (value) Start(); else TurnOff(release: true);
            }
        }

        /// <summary>True when no fallback axis is chosen or connected: the slider drives the test.</summary>
        public bool UseSlider { get => _useSlider; private set => SetProperty(ref _useSlider, value); }

        /// <summary>Clutch position for the slider, 0..100 (two-way). Moving it counts as input.</summary>
        public double Slider { get => _slider; set { if (SetProperty(ref _slider, ClampMath.To0100(value))) _lastInput = _ticker.UtcNow; } }

        public string InputText { get => _inputText; private set => SetProperty(ref _inputText, value); }
        public string OutputText { get => _outputText; private set => SetProperty(ref _outputText, value); }

        public void Stop() => TurnOff(release: true);

        /// <summary>Re-checks whether a pedal axis is available (settings changed).</summary>
        public void RefreshInput() => UseSlider = AxisValue() == null;

        private ClutchBiteSettings Settings => _host.Settings?.Clutch;

        private double? AxisValue()
        {
            string axis = Settings?.FallbackAxis;
            double? v = string.IsNullOrEmpty(axis) ? null : _clutch.ReadAxis01(axis);
            if (!v.HasValue) return null;
            return (Settings.InvertAxis ? 1.0 - v.Value : v.Value) * 100.0;
        }

        private void Start()
        {
            _host.Session.Stopped += OnSessionStopped;
            _host.Session.Begin(this, TestKind.Clutch);
            _tracker.Reset();
            _lastInput = _ticker.UtcNow;
            _lastAxis = null;
            SetProperty(ref _isTestEnabled, true, nameof(IsTestEnabled));
            _ticks?.Dispose();
            _ticks = _ticker.Every(Interval, Tick);
            RefreshInput();
        }

        private void Tick()
        {
            if (!_isTestEnabled) return;
            ClutchBiteSettings s = Settings;
            if (s == null) return;

            double? axis = AxisValue();
            UseSlider = axis == null;
            if (axis.HasValue)
            {
                if (!_lastAxis.HasValue || Math.Abs(axis.Value - _lastAxis.Value) > InputDelta) { _lastInput = _ticker.UtcNow; _lastAxis = axis; }
            }
            if (_ticker.UtcNow - _lastInput >= AutoOff) { TurnOff(release: true); return; }

            // A readable pedal drives the slider, so it always shows where the clutch is.
            if (axis.HasValue) SetProperty(ref _slider, ClampMath.To0100(axis.Value), nameof(Slider));
            double input = axis ?? _slider;
            double output = ClutchBiteEngine.Output(input, s.ToParameters(), _clutch.FinalBiteForTest, _tracker);
            _clutch.PublishClutchTest(output);
            _graph.TestBall = new GraphPoint(input, output);
            InputText = string.Format(CultureInfo.InvariantCulture, Strings.Get("Clutch.Test.Input"), input);
            OutputText = string.Format(CultureInfo.InvariantCulture, Strings.Get("Clutch.Test.Output"), output);
        }

        private void TurnOff(bool release)
        {
            _ticks?.Dispose();
            _ticks = null;
            _host.Session.Stopped -= OnSessionStopped;
            if (!_isTestEnabled) return;
            SetProperty(ref _isTestEnabled, false, nameof(IsTestEnabled));
            SetProperty(ref _slider, 0.0, nameof(Slider));      // owner: switching off resets the slider
            _tracker.Reset();
            _graph.TestBall = null;
            if (release) _clutch.PublishClutchTest(null);
            _host.Session.End(this);
        }

        private void OnSessionStopped(TestStop stop)
        {
            if (ReferenceEquals(stop.Owner, this))
                _ui.Post(() => TurnOff(release: !TestEffectSession.SharesOutputs(TestKind.Clutch, stop.By)));
        }
    }

    /// <summary>
    /// The bite point learning parameters (plugin page only). Each edit writes the whole set through the
    /// settings hub as one field, so it applies at once and syncs like the other Clutch settings.
    /// </summary>
    public sealed class ClutchLearningViewModel : ObservableObject
    {
        private readonly IPanelHost _host;
        private readonly Action _afterWrite;
        private ClutchLearningParameters _p = ClutchLearningParameters.Default;

        public ClutchLearningViewModel(IPanelHost host, Action afterWrite)
        {
            _host = host;
            _afterWrite = afterWrite;
        }

        public double ArmClutchPercent { get => _p.ArmClutchPercent; set => Write(_p.With(armClutchPercent: value)); }
        public double RpmDipPercent { get => _p.RpmDipPercent; set => Write(_p.With(rpmDipPercent: value)); }
        public double ConfirmSpeedKmh { get => _p.ConfirmSpeedKmh; set => Write(_p.With(confirmSpeedKmh: value)); }
        public double MaxReleaseRatePerSec { get => _p.MaxReleaseRatePerSec; set => Write(_p.With(maxReleaseRatePerSec: value)); }
        public double MaxThrottleSwing { get => _p.MaxThrottleSwing; set => Write(_p.With(maxThrottleSwing: value)); }
        public double SteadyIdlePercent { get => _p.SteadyIdlePercent; set => Write(_p.With(steadyIdlePercent: value)); }
        public double MatureSamples { get => _p.MatureSamples; set => Write(_p.With(matureSamples: (int)Math.Round(value))); }
        public double MatureSpreadPoints { get => _p.MatureSpreadPoints; set => Write(_p.With(matureSpreadPoints: value)); }

        /// <summary>Model → view, without writing back.</summary>
        public void Load(ClutchLearningParameters p)
        {
            if (p.Equals(_p)) return;
            _p = p;
            OnPropertyChanged(string.Empty);   // every property
        }

        private void Write(ClutchLearningParameters p)
        {
            if (p.Equals(_p)) return;
            _p = p;
            OnPropertyChanged(string.Empty);
            _host.Hub?.Set(SettingField.ClutchLearning, p, SettingsOrigin.PluginPage);
            _afterWrite?.Invoke();
        }
    }

    /// <summary>
    /// The Clutch Bite settings, readout, graph and Test Effect, shared by the plugin page's Clutch tab
    /// and the native ShakeIt effect's panel. Settings write through the settings hub at once (like the
    /// key data) and refresh from it, so both places always agree.
    /// </summary>
    public sealed class ClutchBiteViewModel : PanelViewModelBase
    {
        private readonly IClutchHost _clutch;
        private IDisposable _refresh;

        private ClutchApplyDirection _apply;
        private ClutchBiteMode _mode;
        private double _bitePoint = 50, _fadedRange = 30, _gamma = 2.5;
        private string _fallbackAxis;
        private bool _invertAxis, _available;
        private IReadOnlyList<Choice<string>> _axisChoices = new Choice<string>[0];
        private string _learnedText = string.Empty, _samplesText = string.Empty,
            _finalText = string.Empty, _sourceText = string.Empty, _carText = string.Empty;

        /// <param name="showLearning">True only on the plugin page: the learning parameters are never
        /// shown in the ShakeIt panel (owner's rule).</param>
        public ClutchBiteViewModel(IPanelHost host, IClutchHost clutch, ITicker ticker, IUiDispatcher ui, bool showLearning = false) : base(host, ticker, ui)
        {
            _clutch = clutch ?? throw new ArgumentNullException(nameof(clutch));
            ShowLearning = showLearning;
            Learning = new ClutchLearningViewModel(host, Refresh);
            ApplyChoices = new[]
            {
                new Choice<ClutchApplyDirection>(ClutchApplyDirection.PressAndRelease, Strings.Get("Clutch.Apply.PressAndRelease")),
                new Choice<ClutchApplyDirection>(ClutchApplyDirection.PressOnly, Strings.Get("Clutch.Apply.PressOnly")),
                new Choice<ClutchApplyDirection>(ClutchApplyDirection.ReleaseOnly, Strings.Get("Clutch.Apply.ReleaseOnly")),
            };
            ModeChoices = new[]
            {
                new Choice<ClutchBiteMode>(ClutchBiteMode.Auto, Strings.Get("Clutch.Mode.Auto")),
                new Choice<ClutchBiteMode>(ClutchBiteMode.Manual, Strings.Get("Clutch.Mode.Manual")),
            };
            Graph = new ClutchGraphViewModel();
            Test = new ClutchTestViewModel(host, clutch, Graph, ticker, ui);
            ForgetCommand = new RelayCommand(() => { _clutch.ForgetClutchBite(); Refresh(); });
            DetectAxisCommand = new RelayCommand(StartDetect);
        }

        public IReadOnlyList<Choice<ClutchApplyDirection>> ApplyChoices { get; }
        public IReadOnlyList<Choice<ClutchBiteMode>> ModeChoices { get; }
        public IReadOnlyList<Choice<string>> AxisChoices { get => _axisChoices; private set => SetProperty(ref _axisChoices, value); }
        public ClutchGraphViewModel Graph { get; }
        public ClutchTestViewModel Test { get; }
        public ICommand ForgetCommand { get; }
        public bool Available { get => _available; private set => SetProperty(ref _available, value); }

        /// <summary>Whether this view shows the learning parameters (plugin page only).</summary>
        public bool ShowLearning { get; }
        public ClutchLearningViewModel Learning { get; }

        /// <summary>The learning parameters to SHOW: the plugin page only, null in the ShakeIt panel, so the
        /// panel never even creates their rows (hidden rows would still widen its shared label column).</summary>
        public ClutchLearningViewModel LearningSection => ShowLearning ? Learning : null;

        /// <summary>The plugin page groups the settings in categories (GroupBoxes); the ShakeIt panel
        /// stacks them like the other QAdvanceFeedback panels.</summary>
        public bool IsCategorized => ShowLearning;

        public ClutchApplyDirection Apply { get => _apply; set { if (SetProperty(ref _apply, value)) Write(SettingField.ClutchApply, value); } }
        public ClutchBiteMode Mode
        {
            get => _mode;
            set
            {
                if (!SetProperty(ref _mode, value)) return;
                OnPropertyChanged(nameof(IsAuto));
                Write(SettingField.ClutchBiteMode, value);
            }
        }

        /// <summary>The learning readout (and the axis note) show only under Auto.</summary>
        public bool IsAuto => _mode == ClutchBiteMode.Auto;

        // ---- Detect the clutch axis by pedalling ----
        public static readonly TimeSpan DetectDuration = TimeSpan.FromSeconds(5);
        public const double DetectMinTravel = 0.3;
        private IDisposable _detectTicks;
        private DateTime _detectEnd;
        private Dictionary<string, double[]> _detect;     // axis -> { rest, min, max }
        private bool _isDetecting;
        private string _detectText = string.Empty;

        /// <summary>Press and release the clutch within 5 s: the axis that moved most becomes the fallback
        /// axis, with Invert set when the pedal reads high while released.</summary>
        public ICommand DetectAxisCommand { get; }
        public bool IsDetecting { get => _isDetecting; private set => SetProperty(ref _isDetecting, value); }
        public string DetectText { get => _detectText; private set => SetProperty(ref _detectText, value); }

        private void StartDetect()
        {
            if (_isDetecting) return;
            _detect = new Dictionary<string, double[]>(StringComparer.Ordinal);
            foreach (string axis in _clutch.ControllerAxes() ?? new string[0])
            {
                double? v = _clutch.ReadAxis01(axis);
                if (v.HasValue) _detect[axis] = new[] { v.Value, v.Value, v.Value };
            }
            _detectEnd = Ticker.UtcNow + DetectDuration;
            IsDetecting = true;
            DetectText = Strings.Get("Clutch.Detect.Pedal");
            _detectTicks?.Dispose();
            _detectTicks = Ticker.Every(TimeSpan.FromSeconds(1.0 / 30.0), DetectTick);
        }

        private void DetectTick()
        {
            foreach (KeyValuePair<string, double[]> kv in _detect)
            {
                double? v = _clutch.ReadAxis01(kv.Key);
                if (!v.HasValue) continue;
                kv.Value[1] = Math.Min(kv.Value[1], v.Value);
                kv.Value[2] = Math.Max(kv.Value[2], v.Value);
            }
            if (Ticker.UtcNow >= _detectEnd) FinishDetect();
        }

        private void FinishDetect()
        {
            _detectTicks?.Dispose();
            _detectTicks = null;
            IsDetecting = false;
            string best = null;
            double bestRange = 0, rest = 0, mid = 0;
            foreach (KeyValuePair<string, double[]> kv in _detect ?? new Dictionary<string, double[]>())
            {
                double range = kv.Value[2] - kv.Value[1];
                if (range > bestRange) { bestRange = range; best = kv.Key; rest = kv.Value[0]; mid = (kv.Value[1] + kv.Value[2]) / 2.0; }
            }
            if (best == null || bestRange < DetectMinTravel)
            {
                DetectText = Strings.Get("Clutch.Detect.None");
                return;
            }
            AxisChoices = BuildAxisChoices(best);
            FallbackAxis = best;
            InvertAxis = rest > mid;      // released reads high: pressing lowers it
            DetectText = string.Format(CultureInfo.InvariantCulture, Strings.Get("Clutch.Detect.Found"),
                best.StartsWith("JoystickPlugin.", StringComparison.Ordinal) ? best.Substring(15) : best);
        }
        public double BitePoint { get => _bitePoint; set { if (SetProperty(ref _bitePoint, value)) Write(SettingField.ClutchBitePoint, value); } }
        public double FadedRange { get => _fadedRange; set { if (SetProperty(ref _fadedRange, value)) Write(SettingField.ClutchFadedRange, value); } }
        public double Gamma { get => _gamma; set { if (SetProperty(ref _gamma, value)) Write(SettingField.ClutchGamma, value); } }
        public string FallbackAxis { get => _fallbackAxis; set { if (SetProperty(ref _fallbackAxis, value)) Write(SettingField.ClutchAxis, value); } }
        public bool InvertAxis { get => _invertAxis; set { if (SetProperty(ref _invertAxis, value)) Write(SettingField.ClutchAxisInvert, value); } }

        public string LearnedText { get => _learnedText; private set => SetProperty(ref _learnedText, value); }
        public string SamplesText { get => _samplesText; private set => SetProperty(ref _samplesText, value); }
        public string FinalText { get => _finalText; private set => SetProperty(ref _finalText, value); }
        public string SourceText { get => _sourceText; private set => SetProperty(ref _sourceText, value); }
        public string CarText { get => _carText; private set => SetProperty(ref _carText, value); }

        private void Write(SettingField field, object value)
        {
            Host.Hub?.Set(field, value, SettingsOrigin.ShakeItPanel);
            Refresh();
            Test.RefreshInput();
        }

        protected override void OnActivated()
        {
            AxisChoices = BuildAxisChoices();
            Refresh();
            _refresh = Ticker.Every(TimeSpan.FromSeconds(1), Refresh);
        }

        protected override void OnDeactivated()
        {
            _refresh?.Dispose();
            _refresh = null;
            _detectTicks?.Dispose();
            _detectTicks = null;
            IsDetecting = false;
            Test.Stop();
        }

        protected override void OnHubChanged(SettingsChangedEventArgs e) => Refresh();

        /// <summary>Re-reads settings and the plugin's clutch state (model → view only).</summary>
        public void Refresh()
        {
            ClutchBiteSettings s = Host.Settings?.Clutch;
            Available = s != null && Host.Hub != null;
            if (s == null) return;

            SetProperty(ref _apply, s.Apply, nameof(Apply));
            if (SetProperty(ref _mode, s.Mode, nameof(Mode))) OnPropertyChanged(nameof(IsAuto));
            SetProperty(ref _bitePoint, s.BitePoint, nameof(BitePoint));
            SetProperty(ref _fadedRange, s.FadedRange, nameof(FadedRange));
            SetProperty(ref _gamma, s.Gamma, nameof(Gamma));
            SetProperty(ref _fallbackAxis, s.FallbackAxis, nameof(FallbackAxis));
            SetProperty(ref _invertAxis, s.InvertAxis, nameof(InvertAxis));
            Learning.Load(s.Learning?.ToParameters() ?? ClutchLearningParameters.Default);

            ClutchHostStatus st = _clutch.ClutchStatus ?? new ClutchHostStatus();
            double bite = _clutch.FinalBiteForTest;
            LearnedText = st.Learned.HasValue ? Format("Clutch.Readout.Learned", st.Learned.Value) : Strings.Get("Clutch.Readout.LearnedNone");
            SamplesText = string.Format(CultureInfo.InvariantCulture,
                Strings.Get(st.IsMatured ? "Clutch.Readout.Samples.Matured" : "Clutch.Readout.Samples.Learning"), st.Count);
            FinalText = Format("Clutch.Readout.Final", bite);
            SourceText = Strings.Get("Clutch.Readout.Source." + st.Kind);
            string car = CarOf(st.Key ?? _clutch.FinalBiteForTestKey);
            CarText = car == null ? Strings.Get("Clutch.Readout.CarNone") : string.Format(CultureInfo.InvariantCulture, Strings.Get("Clutch.Readout.Car"), car);

            (double lo, double hi) = ClutchBiteCurve.Edges(bite, s.FadedRange);
            Graph.Curve = ClutchBiteCurve.Points(bite, s.FadedRange, s.Gamma);
            Graph.BiteMarker = ClutchBiteCurve.ClampBite(bite);
            Graph.ConfiguredMarker = s.Mode == ClutchBiteMode.Auto ? s.BitePoint : (double?)null;
            Graph.Lo = lo;
            Graph.Hi = hi;
        }

        private IReadOnlyList<Choice<string>> BuildAxisChoices(string include = null)
        {
            var list = new List<Choice<string>> { new Choice<string>(null, Strings.Get("Clutch.Axis.None")) };
            IEnumerable<string> axes = _clutch.ControllerAxes() ?? new string[0];
            string current = include ?? Host.Settings?.Clutch?.FallbackAxis;
            if (!string.IsNullOrEmpty(current) && !axes.Contains(current)) axes = axes.Concat(new[] { current });
            list.AddRange(axes.Select(a => new Choice<string>(a, a.StartsWith("JoystickPlugin.", StringComparison.Ordinal) ? a.Substring(15) : a)));
            return list;
        }

        /// <summary>"game / car" from a learning key (game |#| car |#| source |#|), or null.</summary>
        private static string CarOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string[] parts = key.Split(new[] { "|#|" }, StringSplitOptions.None);
            return parts.Length >= 2 && (parts[0].Length > 0 || parts[1].Length > 0) ? parts[0] + " / " + parts[1] : null;
        }

        private static string Format(string key, double value) =>
            string.Format(CultureInfo.InvariantCulture, Strings.Get(key), value);
    }
}
