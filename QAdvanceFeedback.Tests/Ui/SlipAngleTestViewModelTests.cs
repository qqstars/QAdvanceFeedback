using System;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The Slip Angle Test Effect in the ShakeIt panel (docs\slip-angle-design.md 9.11): one Raw.All
    /// slider (0 to 300% of Max, the "100%" tick at End), the dot at x = Min(slider, End), y = the actual
    /// pulse-free output after drift attenuation; session / auto-off as the WheelLock/WheelSlip test.</summary>
    public class SlipAngleTestViewModelTests
    {
        private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 60.0);

        private readonly FakeTestHost _host = new FakeTestHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly SlipAngleChannelSettings _channel = new SlipAngleChannelSettings();
        private SlipAngleWindow _window = new SlipAngleWindow(4.0, 7.0, 10.0);   // End 15, slider 0..30

        private SlipAngleTestViewModel NewVm() =>
            new SlipAngleTestViewModel(_host, () => _channel, () => _window, _ticker, new ImmediateDispatcher());

        private double Curve(double deg) =>
            new OutputProjector(_channel.Projector).Project(ClampMath.To0100(SlipAngleNormalizer.Normalize(deg, _window)));

        [Fact]
        public void Slider_spans_300_percent_of_max_with_end_in_the_middle()
        {
            SlipAngleTestViewModel vm = NewVm();
            Assert.Equal(30.0, vm.SliderMaximum, 9);
            Assert.Equal(15.0, vm.End, 9);
            Assert.Equal(vm.SliderMaximum / 2.0, vm.End, 9);

            vm.SourceDeg = 99;
            Assert.Equal(30.0, vm.SourceDeg, 9);

            // A narrower window re-ranges the slider and clamps the value into it.
            vm.IsTestEnabled = true;
            _window = new SlipAngleWindow(3.0, 5.0, 8.0);
            vm.WindowChanged();
            Assert.Equal(24.0, vm.SliderMaximum, 9);
            Assert.Equal(24.0, vm.SourceDeg, 9);
        }

        [Fact]
        public void Below_tick_dot_on_curve()
        {
            SlipAngleTestViewModel vm = NewVm();
            for (double x = 0.25; x < _window.End; x += 0.25)
            {
                vm.IsTestEnabled = true;
                vm.SourceDeg = x;
                _ticker.Advance(Frame);

                TestBall dot = Assert.Single(vm.Balls);
                Assert.Equal(BallColour.Orange, dot.Colour);
                Assert.Equal(x / _window.End * 100.0, dot.Source0100, 9);
                Assert.Equal(Curve(x), dot.Projected0100, 9);
                Assert.Equal(dot.Projected0100, _host.SlipAngle.Last().PulseFreeAll, 9);
                vm.IsTestEnabled = false;
            }
        }

        [Fact]
        public void Below_max_the_dot_stays_on_the_curve_while_held()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 9.5;   // below Max: never a drift
            _ticker.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(Curve(9.5), vm.Balls.Single().Projected0100, 9);
        }

        [Fact]
        public void Past_tick_dot_top_right()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 2.0 * _window.Max;   // past End = 1.5 x Max
            _ticker.Advance(Frame);

            TestBall dot = vm.Balls.Single();
            Assert.Equal(100.0, dot.Source0100, 9);
            Assert.Equal(Curve(_window.End), dot.Projected0100, 9);
        }

        [Fact]
        public void Holding_past_max_drops_dot_to_40_percent_of_curve()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 2.5 * _window.Max;

            // Hold 0.25 s (drift detected), then the 0.12 s ease to ToPercent (40 %); a few frames' margin.
            _ticker.Advance(TimeSpan.FromSeconds(0.25) + TimeSpan.FromSeconds(0.12) + Frame + Frame + Frame);

            TestBall dot = vm.Balls.Single();
            Assert.Equal(100.0, dot.Source0100, 9);   // keeps x
            Assert.Equal(0.40 * Curve(_window.End), dot.Projected0100, 9);
            Assert.Equal(DriftState.Drifting, _host.SlipAngle.Last().DriftState);
        }

        [Fact]
        public void Auto_off_after_10s()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 5;
            Assert.Equal(TestKind.SlipAngle, _host.Session.Active);
            _ticker.Advance(TimeSpan.FromSeconds(9));
            Assert.True(vm.IsTestEnabled);
            vm.SourceDeg = 6;   // input resets the idle timer
            _ticker.Advance(TimeSpan.FromSeconds(9));
            Assert.True(vm.IsTestEnabled);

            _ticker.Advance(TimeSpan.FromSeconds(1.05));
            Assert.False(vm.IsTestEnabled);
            Assert.Equal(0.0, vm.SourceDeg);   // off resets the slider
            Assert.Empty(vm.Balls);
            Assert.Null(_host.SlipAngle.Last());   // output released
            Assert.Equal(TestKind.None, _host.Session.Active);
            Assert.Equal(0, _ticker.ActiveCount);
            Assert.Equal(0, _host.Session.ListenerCount);
        }

        [Fact]
        public void Other_test_starting_stops_this_one()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 5;
            _ticker.Advance(Frame);

            var lockTest = new LockSlipTestViewModel(true, _host, () => new WheelChannelSettings(), () => null,
                () => EffectRouting.Mono, _ticker, new ImmediateDispatcher());
            lockTest.IsTestEnabled = true;

            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.SlipAngle.Last());   // another kind's outputs: this one releases its own
            Assert.Equal(TestKind.Lock, _host.Session.Active);
            lockTest.Stop();
        }

        [Fact]
        public void Another_slip_angle_test_taking_over_does_not_release_its_output()
        {
            SlipAngleTestViewModel first = NewVm(), second = NewVm();
            first.IsTestEnabled = true;
            _ticker.Advance(Frame);
            int published = _host.SlipAngle.Count;

            second.IsTestEnabled = true;
            Assert.False(first.IsTestEnabled);
            Assert.Equal(published, _host.SlipAngle.Count);   // no null from the stopped one
            Assert.Same(second, _host.Session.ActiveOwner);
            second.Stop();
            Assert.Null(_host.SlipAngle.Last());
        }

        [Fact]
        public void Turning_off_releases_the_output()
        {
            SlipAngleTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.SourceDeg = 20;
            _ticker.Advance(Frame);
            Assert.NotNull(_host.SlipAngle.Last());

            vm.IsTestEnabled = false;
            Assert.Null(_host.SlipAngle.Last());
            Assert.Equal(0.0, vm.SourceDeg);
            Assert.Empty(vm.Balls);
        }
    }
}
