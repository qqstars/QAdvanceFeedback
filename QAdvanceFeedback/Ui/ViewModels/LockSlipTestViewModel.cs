using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The WheelLock/WheelSlip Test Effect in the ShakeIt panel (docs\shakeit-test-effect-mvvm-design.md).
    /// <list type="bullet">
    /// <item>Four sliders give FL/FR/RL/RR source values.</item>
    /// <item>While on, each 60 Hz tick runs <see cref="LockSlipTestEngine"/> with the key data the graph
    /// shows and publishes the result to <c>Projected.*</c> through the host; the native effect plays
    /// those published values.</item>
    /// <item><see cref="Balls"/> shows one ball per output channel of the current routing.</item>
    /// </list>
    /// Turns itself off (toggle included) after 10 s without slider input, on <see cref="Stop"/>, or when
    /// another test begins, and then releases the outputs.
    /// </summary>
    public sealed class LockSlipTestViewModel : ObservableObject, IDisposable
    {
        public static readonly TimeSpan AutoOff = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / 60.0);
        private static readonly IReadOnlyList<TestBall> NoBalls = new TestBall[0];

        private readonly bool _isLock;
        private readonly TestKind _kind;
        private readonly ITestEffectHost _host;
        private readonly Func<WheelChannelSettings> _channel;
        private readonly Func<KeyDataView> _keyData;
        private readonly Func<EffectRouting> _routing;
        private readonly ITicker _ticker;
        private readonly IUiDispatcher _ui;

        private IDisposable _ticks;
        private LockSlipTestEngine _engine;
        private WheelChannelSettings _engineChannel;
        private string _engineKey;
        private ProjectedWheelLockSlipResult _last;
        private DateTime _lastInput;
        private DateTime? _lastTick;
        private static readonly TimeSpan MaxFrameTime = TimeSpan.FromMilliseconds(100);

        // What the group slider values were last computed from; a change re-raises them (never Touch).
        private bool _hasBasis, _basisMaxGrip;
        private double? _basisSMax, _basisS90, _basisS75;
        private AggregationWeights _basisWeights;

        private bool _isTestEnabled;
        private double _fl, _fr, _rl, _rr;
        private IReadOnlyList<TestBall> _balls = NoBalls;

        public LockSlipTestViewModel(bool isLock, ITestEffectHost host, Func<WheelChannelSettings> channel,
            Func<KeyDataView> keyData, Func<EffectRouting> routing, ITicker ticker, IUiDispatcher ui)
        {
            _isLock = isLock;
            _kind = isLock ? TestKind.Lock : TestKind.Slip;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _keyData = keyData ?? throw new ArgumentNullException(nameof(keyData));
            _routing = routing ?? throw new ArgumentNullException(nameof(routing));
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

        public double FrontLeft { get => _fl; set => SetWheel(ref _fl, value, nameof(FrontLeft)); }
        public double FrontRight { get => _fr; set => SetWheel(ref _fr, value, nameof(FrontRight)); }
        public double RearLeft { get => _rl; set => SetWheel(ref _rl, value, nameof(RearLeft)); }
        public double RearRight { get => _rr; set => SetWheel(ref _rr, value, nameof(RearRight)); }

        // Group sliders: each value is computed from the four wheels (never stored); dragging one writes
        // its member wheels. See TestGroupSliders and docs\slip-angle-design.md section 6.
        public double SingleChannel { get => GroupValue(TestGroup.All); set => SetGroup(TestGroup.All, value); }
        public double FrontChannel { get => GroupValue(TestGroup.Front); set => SetGroup(TestGroup.Front, value); }
        public double RearChannel { get => GroupValue(TestGroup.Rear); set => SetGroup(TestGroup.Rear, value); }
        public double LeftChannel { get => GroupValue(TestGroup.Left); set => SetGroup(TestGroup.Left, value); }
        public double RightChannel { get => GroupValue(TestGroup.Right); set => SetGroup(TestGroup.Right, value); }

        /// <summary>Which group sliders show, from the effect's routing (bound visibility).</summary>
        public bool ShowSingle => Shows(TestGroup.All);
        public bool ShowFrontRear => Shows(TestGroup.Front);
        public bool ShowLeftRight => Shows(TestGroup.Left);
        public bool ShowAnyGroup => ShowSingle || ShowFrontRear || ShowLeftRight;

        /// <summary>One ball per output channel while the test is on; empty while off.</summary>
        public IReadOnlyList<TestBall> Balls { get => _balls; private set => SetProperty(ref _balls, value); }

        /// <summary>The effect's routing changed: redraw the balls for it.</summary>
        public void RoutingChanged()
        {
            OnPropertyChanged(nameof(ShowSingle));
            OnPropertyChanged(nameof(ShowFrontRear));
            OnPropertyChanged(nameof(ShowLeftRight));
            OnPropertyChanged(nameof(ShowAnyGroup));
            RaiseGroupsChanged();
            UpdateBalls();
        }

        public void Stop() => TurnOff();

        public void Dispose() => TurnOff();

        private bool Shows(TestGroup group) => TestGroupSliders.GroupsFor(_routing()).Contains(group);

        private bool MaxGripOnly(WheelChannelSettings channel) => channel.NormalizePattern == NormalizePattern.MaxGripOnly;

        private double GroupValue(TestGroup group)
        {
            WheelChannelSettings channel = _channel();
            return channel == null
                ? 0.0
                : TestGroupSliders.SourceFor(group, Sources, channel.ToAggregationWeights(), _isLock,
                    MaxGripOnly(channel), _keyData());
        }

        private void SetWheel(ref double field, double value, string name)
        {
            if (!SetProperty(ref field, ClampMath.To0100(value), name)) return;
            Touch();
            RaiseGroupsChanged();
        }

        // Writes the member wheels through the backing fields, then raises the group notifications once
        // (not once per wheel).
        private void SetGroup(TestGroup group, double value)
        {
            Corners next = TestGroupSliders.Write(group, ClampMath.To0100(value), Sources);
            bool fl = SetProperty(ref _fl, next.FrontLeft, nameof(FrontLeft));
            bool fr = SetProperty(ref _fr, next.FrontRight, nameof(FrontRight));
            bool rl = SetProperty(ref _rl, next.RearLeft, nameof(RearLeft));
            bool rr = SetProperty(ref _rr, next.RearRight, nameof(RearRight));
            if (!(fl || fr || rl || rr)) return;
            Touch();
            RaiseGroupsChanged();
        }

        private void RaiseGroupsChanged()
        {
            OnPropertyChanged(nameof(SingleChannel));
            OnPropertyChanged(nameof(FrontChannel));
            OnPropertyChanged(nameof(RearChannel));
            OnPropertyChanged(nameof(LeftChannel));
            OnPropertyChanged(nameof(RightChannel));
        }

        // The group values depend on the key data and the weights: tell the view when either changes
        // while the test runs, so a slider never sits off its dot. Not per tick, and not an input.
        private void RefreshGroupsIfBasisChanged(WheelChannelSettings channel)
        {
            KeyDataView k = _keyData();
            bool grip = MaxGripOnly(channel);
            AggregationWeights w = channel.ToAggregationWeights();
            double? sMax = k?.SMax, s90 = k?.S90, s75 = k?.S75;
            bool changed = _hasBasis && (grip != _basisMaxGrip || sMax != _basisSMax || s90 != _basisS90
                || s75 != _basisS75 || !w.Equals(_basisWeights));
            _hasBasis = true;
            _basisMaxGrip = grip; _basisSMax = sMax; _basisS90 = s90; _basisS75 = s75; _basisWeights = w;
            if (changed) RaiseGroupsChanged();
        }

        private Corners Sources => new Corners(_fl, _fr, _rl, _rr);

        private void Start()
        {
            // Listen only while running, so an idle view model is never held by the session.
            _host.Session.Stopped += OnSessionStopped;
            _host.Session.Begin(this, _kind);
            _engine = null;
            _hasBasis = false;
            _lastInput = _ticker.UtcNow;
            _lastTick = null;
            SetProperty(ref _isTestEnabled, true, nameof(IsTestEnabled));
            _ticks?.Dispose();
            _ticks = _ticker.Every(Interval, Tick);
        }

        private void Touch() => _lastInput = _ticker.UtcNow;

        private void Tick()
        {
            if (!_isTestEnabled) return;
            if (_ticker.UtcNow - _lastInput >= AutoOff) { TurnOff(); return; }

            WheelChannelSettings channel = _channel();
            if (channel == null) return;
            EnsureEngine(channel);
            RefreshGroupsIfBasisChanged(channel);
            _last = _engine.Step(Sources, _keyData(), MeasuredFrameTime().TotalSeconds);
            _host.PublishLockSlipTest(_last);
            UpdateBalls();
        }

        // A projector or pulse change (or a reloaded settings object) builds a new engine; otherwise the
        // engine is reused so the pulse phase carries on smoothly.
        private void EnsureEngine(WheelChannelSettings channel)
        {
            string key = JsonConvert.SerializeObject(new { channel.Projector, channel.Pulse });
            if (_engine != null && ReferenceEquals(channel, _engineChannel) && key == _engineKey) return;
            _engine = new LockSlipTestEngine(_isLock, channel);
            _engineChannel = channel;
            _engineKey = key;
        }

        private void UpdateBalls()
        {
            WheelChannelSettings channel = _channel();
            Balls = _isTestEnabled && _last != null && channel != null
                ? TestBalls.For(_routing(), _isLock, Sources, channel.ToAggregationWeights(), _last,
                    MaxGripOnly(channel), _keyData())
                : NoBalls;
        }

        // The real time since the last frame, so the pulse runs at its configured rate even when the
        // DispatcherTimer drifts from 1/60 s; clamped so a stalled UI thread cannot jump the pulse.
        private TimeSpan MeasuredFrameTime()
        {
            DateTime now = _ticker.UtcNow;
            TimeSpan dt = _lastTick.HasValue ? now - _lastTick.Value : Interval;
            _lastTick = now;
            if (dt <= TimeSpan.Zero) dt = Interval;
            return dt > MaxFrameTime ? MaxFrameTime : dt;
        }

        private void TurnOff() => TurnOff(release: true);

        /// <param name="release">False when another Lock/Slip test took over: it now drives Projected.*,
        /// so releasing would blank its frames.</param>
        private void TurnOff(bool release)
        {
            _ticks?.Dispose();
            _ticks = null;
            _host.Session.Stopped -= OnSessionStopped;
            if (!_isTestEnabled) return;
            SetProperty(ref _isTestEnabled, false, nameof(IsTestEnabled));
            // Owner: switching off resets the sliders to 0.
            SetProperty(ref _fl, 0.0, nameof(FrontLeft));
            SetProperty(ref _fr, 0.0, nameof(FrontRight));
            SetProperty(ref _rl, 0.0, nameof(RearLeft));
            SetProperty(ref _rr, 0.0, nameof(RearRight));
            RaiseGroupsChanged();
            _last = null;
            Balls = NoBalls;
            if (release) _host.PublishLockSlipTest(null);
            _host.Session.End(this);
        }

        private void OnSessionStopped(TestStop stop)
        {
            if (ReferenceEquals(stop.Owner, this))
                _ui.Post(() => TurnOff(release: !TestEffectSession.SharesOutputs(_kind, stop.By)));
        }
    }
}
