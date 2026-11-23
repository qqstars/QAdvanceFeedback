using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The Slip Angle Test Effect in the ShakeIt panel (docs\slip-angle-design.md 9.11).
    /// <list type="bullet">
    /// <item>One slider gives Raw.All in degrees, 0 to <see cref="SliderMaximum"/> (3 x Max of the window
    /// the graph shows); its middle is End (1.5 x Max, normalized 100), marked by the "100%" tick.</item>
    /// <item>While on, each 60 Hz tick runs <see cref="SlipAngleTestEngine"/> (real normalization,
    /// projector and drift attenuation) and publishes the result to <c>SlipAngle.Projected.All</c> through
    /// the host; the native effect plays it.</item>
    /// <item><see cref="Balls"/> holds the one orange dot: x = Min(slider, End), y = the actual pulse-free
    /// output after drift attenuation - on the curve left of the tick, top-right past it, and dropping
    /// along the ramp once a held drift is detected.</item>
    /// </list>
    /// Session and auto-off exactly as <see cref="LockSlipTestViewModel"/>: turns itself off after 10 s
    /// without slider input, on <see cref="Stop"/>, or when another test begins, and then releases the output.
    /// </summary>
    public sealed class SlipAngleTestViewModel : ObservableObject, IDisposable
    {
        public static readonly TimeSpan AutoOff = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / 60.0);
        private static readonly TimeSpan MaxFrameTime = TimeSpan.FromMilliseconds(100);
        private static readonly IReadOnlyList<TestBall> NoBalls = new TestBall[0];

        /// <summary>The slider spans 0 to this multiple of Max (End = 1.5 x Max sits in the middle).</summary>
        public const double SliderToMaxRatio = 2.0 * SlipAngleWindow.EndToMaxRatio;

        private readonly ITestEffectHost _host;
        private readonly Func<SlipAngleChannelSettings> _channel;
        private readonly Func<SlipAngleWindow> _window;
        private readonly ITicker _ticker;
        private readonly IUiDispatcher _ui;

        private IDisposable _ticks;
        private SlipAngleTestEngine _engine;
        private SlipAngleChannelSettings _engineChannel;
        private string _engineKey;
        private SlipAngleProjectedResult _last;
        private DateTime _lastInput;
        private DateTime? _lastTick;
        private double _shownMaximum = double.NaN;

        private bool _isTestEnabled;
        private double _sourceDeg;
        private IReadOnlyList<TestBall> _balls = NoBalls;

        /// <param name="window">The window the panel's graph shows (Max sets the slider range).</param>
        public SlipAngleTestViewModel(ITestEffectHost host, Func<SlipAngleChannelSettings> channel,
            Func<SlipAngleWindow> window, ITicker ticker, IUiDispatcher ui)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _window = window ?? throw new ArgumentNullException(nameof(window));
            _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <summary>The Test toggle (two-way).</summary>
        public bool IsTestEnabled
        {
            get => _isTestEnabled;
            set
            {
                if (value == _isTestEnabled) return;
                if (value) Start(); else TurnOff();
            }
        }

        /// <summary>Raw.All in degrees (two-way), 0 to <see cref="SliderMaximum"/>. Moving it counts as input.</summary>
        public double SourceDeg
        {
            get => _sourceDeg;
            set
            {
                double v = ClampMath.IsFinite(value) ? ClampMath.Clamp(value, 0.0, SliderMaximum) : 0.0;
                if (SetProperty(ref _sourceDeg, v)) Touch();
            }
        }

        /// <summary>The slider's right end: 3 x Max (300% of Max).</summary>
        public double SliderMaximum => SliderToMaxRatio * Window.Max;

        /// <summary>End (1.5 x Max, normalized 100) - the slider's middle, where the "100%" tick sits.</summary>
        public double End => Window.End;

        /// <summary>The one orange dot while the test is on; empty while off.</summary>
        public IReadOnlyList<TestBall> Balls { get => _balls; private set => SetProperty(ref _balls, value); }

        /// <summary>The graph's window may have changed (key data edit, learner): re-range the slider.</summary>
        public void WindowChanged() => RefreshRange();

        public void Stop() => TurnOff();

        public void Dispose() => TurnOff();

        private SlipAngleWindow Window
        {
            get
            {
                SlipAngleWindow w = _window();
                return w.IsValid ? w : SlipAngleWindow.ShippedDefault;
            }
        }

        private void RefreshRange()
        {
            double max = SliderMaximum;
            if (max == _shownMaximum) return;
            _shownMaximum = max;
            OnPropertyChanged(nameof(SliderMaximum));
            OnPropertyChanged(nameof(End));
            // A narrower window clamps the slider into its new range (not an input).
            if (_sourceDeg > max) SetProperty(ref _sourceDeg, max, nameof(SourceDeg));
        }

        private void Start()
        {
            // Listen only while running, so an idle view model is never held by the session.
            _host.Session.Stopped += OnSessionStopped;
            _host.Session.Begin(this, TestKind.SlipAngle);
            _engine = null;
            _lastInput = _ticker.UtcNow;
            _lastTick = null;
            RefreshRange();
            SetProperty(ref _isTestEnabled, true, nameof(IsTestEnabled));
            _ticks?.Dispose();
            _ticks = _ticker.Every(Interval, Tick);
        }

        private void Touch() => _lastInput = _ticker.UtcNow;

        private void Tick()
        {
            if (!_isTestEnabled) return;
            if (_ticker.UtcNow - _lastInput >= AutoOff) { TurnOff(); return; }

            SlipAngleChannelSettings channel = _channel();
            if (channel == null) return;
            RefreshRange();
            EnsureEngine(channel);
            SlipAngleWindow w = Window;
            _last = _engine.Step(_sourceDeg, w, MeasuredFrameTime().TotalSeconds);
            _host.PublishSlipAngleTest(_last);
            UpdateBalls(w);
        }

        // A projector, pulse or drift change (or a reloaded settings object) builds a new engine; otherwise
        // the engine is reused so the pulse phase and the drift state carry on.
        private void EnsureEngine(SlipAngleChannelSettings channel)
        {
            string key = JsonConvert.SerializeObject(new { channel.Projector, channel.Pulse, channel.Drift });
            if (_engine != null && ReferenceEquals(channel, _engineChannel) && key == _engineKey) return;
            _engine = new SlipAngleTestEngine(channel);
            _engineChannel = channel;
            _engineKey = key;
        }

        private void UpdateBalls(SlipAngleWindow w)
        {
            if (!_isTestEnabled || _last == null) { Balls = NoBalls; return; }
            double x0100 = Math.Min(_sourceDeg, w.End) / w.End * 100.0;
            Balls = new[] { new TestBall(EffectSlot.All, x0100, _last.PulseFreeAll, BallColour.Orange) };
        }

        // The real time since the last frame, so the pulse and the drift timers run at their configured
        // rate even when the DispatcherTimer drifts from 1/60 s; clamped so a stalled UI thread cannot jump them.
        private TimeSpan MeasuredFrameTime()
        {
            DateTime now = _ticker.UtcNow;
            TimeSpan dt = _lastTick.HasValue ? now - _lastTick.Value : Interval;
            _lastTick = now;
            if (dt <= TimeSpan.Zero) dt = Interval;
            return dt > MaxFrameTime ? MaxFrameTime : dt;
        }

        private void TurnOff() => TurnOff(release: true);

        /// <param name="release">False when another Slip Angle test took over: it now drives the output,
        /// so releasing would blank its frames.</param>
        private void TurnOff(bool release)
        {
            _ticks?.Dispose();
            _ticks = null;
            _host.Session.Stopped -= OnSessionStopped;
            if (!_isTestEnabled) return;
            SetProperty(ref _isTestEnabled, false, nameof(IsTestEnabled));
            // Owner: switching off resets the slider to 0.
            SetProperty(ref _sourceDeg, 0.0, nameof(SourceDeg));
            _last = null;
            _engine = null;
            Balls = NoBalls;
            if (release) _host.PublishSlipAngleTest(null);
            _host.Session.End(this);
        }

        private void OnSessionStopped(TestStop stop)
        {
            if (ReferenceEquals(stop.Owner, this))
                _ui.Post(() => TurnOff(release: !TestEffectSession.SharesOutputs(TestKind.SlipAngle, stop.By)));
        }
    }
}
