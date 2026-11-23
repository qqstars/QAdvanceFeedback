using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The G-Force Test Effect (docs\shakeit-test-effect-mvvm-design.md), shared by the plugin settings
    /// page and the four G-Force ShakeIt panels. While on, it runs the real <see cref="GForceEngine"/>
    /// from the pad, bar and the provided settings at <see cref="RateHz"/>, and publishes each frame
    /// through the host - the published <c>GForce.*</c> values are what the native effects play.
    /// <para/>
    /// Turns itself off (and its toggle with it) after <see cref="AutoOff"/> without input, when
    /// <see cref="Stop"/> is called, or when another test begins; it then releases the outputs. It never
    /// writes the settings it reads.
    /// </summary>
    public sealed class GForceTestViewModel : ObservableObject, IDisposable
    {
        public static readonly TimeSpan AutoOff = TimeSpan.FromSeconds(10);

        private readonly ITestEffectHost _host;
        private readonly Func<GForceSettings> _settings;
        private readonly ITicker _ticker;
        private readonly IUiDispatcher _ui;
        private IDisposable _ticks;
        private GForceEngine _engine;
        private DateTime _lastInput;
        private DateTime? _lastTick;

        private bool _isTestEnabled;
        private double _padX, _padY, _wheel, _angle, _rateHz = 60.0;
        private string _longLabel = Strings.Get("GForce.Test.Readout.Accel");
        private string _longText = string.Empty, _latText = string.Empty, _wheelText = string.Empty, _angleText = string.Empty;
        private string _frontText = string.Empty, _rearText = string.Empty, _lowText = string.Empty, _topText = string.Empty;

        public GForceTestViewModel(ITestEffectHost host, Func<GForceSettings> settings, ITicker ticker, IUiDispatcher ui)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <summary>The Test toggle (two-way). Switching on starts publishing; off releases.</summary>
        public bool IsTestEnabled
        {
            get => _isTestEnabled;
            set
            {
                if (value == _isTestEnabled) return;
                if (value) Start(); else TurnOff();
            }
        }

        /// <summary>-1..+1, +1 at the right of the pad (two-way). Moving it counts as input.</summary>
        public double PadX { get => _padX; set { if (SetProperty(ref _padX, ClampMath.Clamp(value, -1, 1))) Touch(); } }

        /// <summary>-1..+1, +1 at the top of the pad = braking (two-way).</summary>
        public double PadY { get => _padY; set { if (SetProperty(ref _padY, ClampMath.Clamp(value, -1, 1))) Touch(); } }

        /// <summary>0..1 along the Lock/Slip bar (two-way).</summary>
        public double Wheel { get => _wheel; set { if (SetProperty(ref _wheel, ClampMath.Clamp(value, 0, 1))) Touch(); } }

        /// <summary>0..1 along the Slip Angle bar (two-way) - the projected Slip Angle the engine
        /// integrates while Integrate Slip Angle is on (docs\slip-angle-design.md section 10).</summary>
        public double Angle { get => _angle; set { if (SetProperty(ref _angle, ClampMath.Clamp(value, 0, 1))) Touch(); } }

        /// <summary>Frames per second, 1..240 (two-way). Not persisted: every test opens at 60 Hz.</summary>
        public double RateHz
        {
            get => _rateHz;
            set
            {
                if (!SetProperty(ref _rateHz, ClampMath.Clamp(value, 1, 240))) return;
                if (_isTestEnabled) StartTicking();
            }
        }

        public string LongLabel { get => _longLabel; private set => SetProperty(ref _longLabel, value); }
        public string LongText { get => _longText; private set => SetProperty(ref _longText, value); }
        public string LatText { get => _latText; private set => SetProperty(ref _latText, value); }
        public string WheelText { get => _wheelText; private set => SetProperty(ref _wheelText, value); }
        public string AngleText { get => _angleText; private set => SetProperty(ref _angleText, value); }
        public string FrontText { get => _frontText; private set => SetProperty(ref _frontText, value); }
        public string RearText { get => _rearText; private set => SetProperty(ref _rearText, value); }
        public string LowText { get => _lowText; private set => SetProperty(ref _lowText, value); }
        public string TopText { get => _topText; private set => SetProperty(ref _topText, value); }

        /// <summary>Turns the test off and releases the outputs (the view left the screen).</summary>
        public void Stop() => TurnOff();

        public void Dispose() => TurnOff();

        private void Start()
        {
            // Listen only while running, so an idle view model is never held by the session.
            _host.Session.Stopped += OnSessionStopped;
            _host.Session.Begin(this, TestKind.GForce);
            _engine = new GForceEngine();
            _lastInput = _ticker.UtcNow;
            _lastTick = null;
            SetProperty(ref _isTestEnabled, true, nameof(IsTestEnabled));
            StartTicking();
        }

        private void StartTicking()
        {
            _ticks?.Dispose();
            _ticks = _ticker.Every(Interval, Tick);
        }

        private TimeSpan Interval => TimeSpan.FromSeconds(1.0 / _rateHz);

        private void Touch() => _lastInput = _ticker.UtcNow;

        private void Tick()
        {
            if (!_isTestEnabled) return;
            if (_ticker.UtcNow - _lastInput >= AutoOff) { TurnOff(); return; }

            GForceSettings settings = _settings();
            if (settings == null) return;
            GForceTestFrame.Result frame = GForceTestFrame.Compute(_padX, _padY, _wheel, settings, _engine, MeasuredFrameTime(), _angle);
            _host.PublishGForceTest(frame.Output);

            LongLabel = Strings.Get(frame.Accelerating ? "GForce.Test.Readout.Accel" : "GForce.Test.Readout.Decel");
            LongText = GForceTestFrame.FormatG(frame.LongitudinalG, frame.LongMax);
            LatText = GForceTestFrame.FormatG(frame.LateralG, frame.LatMax);
            WheelText = GForceTestFrame.FormatWheel(frame.Wheel0100);
            AngleText = GForceTestFrame.FormatWheel(frame.Angle0100);
            FrontText = GForceTestFrame.FormatPair(frame.Output.BottomFrontLeft, frame.Output.BottomFrontRight);
            RearText = GForceTestFrame.FormatPair(frame.Output.BottomRearLeft, frame.Output.BottomRearRight);
            LowText = GForceTestFrame.FormatPair(frame.Output.BackLowLeft, frame.Output.BackLowRight);
            TopText = GForceTestFrame.FormatPair(frame.Output.BackTopLeft, frame.Output.BackTopRight);
        }

        // The real time since the last frame (a DispatcherTimer drifts from its nominal interval),
        // clamped so a stalled UI thread cannot jump the engine.
        private TimeSpan MeasuredFrameTime()
        {
            DateTime now = _ticker.UtcNow;
            TimeSpan dt = _lastTick.HasValue ? now - _lastTick.Value : Interval;
            _lastTick = now;
            if (dt <= TimeSpan.Zero) dt = Interval;
            return dt > MaxFrameTime ? MaxFrameTime : dt;
        }

        private static readonly TimeSpan MaxFrameTime = TimeSpan.FromMilliseconds(100);

        private void TurnOff() => TurnOff(release: true);

        /// <param name="release">False when another G-Force test took over: it now drives the outputs,
        /// so releasing them would blank its frames.</param>
        private void TurnOff(bool release)
        {
            _ticks?.Dispose();
            _ticks = null;
            _host.Session.Stopped -= OnSessionStopped;
            if (!_isTestEnabled) return;
            SetProperty(ref _isTestEnabled, false, nameof(IsTestEnabled));
            // Owner: switching off resets the inputs - the G-ball centred, the bars at 0.
            SetProperty(ref _padX, 0.0, nameof(PadX));
            SetProperty(ref _padY, 0.0, nameof(PadY));
            SetProperty(ref _wheel, 0.0, nameof(Wheel));
            SetProperty(ref _angle, 0.0, nameof(Angle));
            if (release) _host.PublishGForceTest(null);
            _host.Session.End(this);
        }

        private void OnSessionStopped(TestStop stop)
        {
            if (ReferenceEquals(stop.Owner, this))
                _ui.Post(() => TurnOff(release: !TestEffectSession.SharesOutputs(TestKind.GForce, stop.By)));
        }
    }
}
