using System;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The WheelLock/WheelSlip Test Effect in the ShakeIt panel
    /// (docs\shakeit-test-effect-mvvm-design.md).</summary>
    public class LockSlipTestViewModelTests
    {
        private readonly FakeTestHost _host = new FakeTestHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly WheelChannelSettings _channel = new WheelChannelSettings
        {
            NormalizePattern = NormalizePattern.Mapping,
            Projector = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock),
        };
        private EffectRouting _routing = EffectRouting.Corners;
        private readonly KeyDataView _keyData = new KeyDataView { SMax = 80, S90 = 60, S75 = 40, ShowLowerAnchors = true };

        private LockSlipTestViewModel NewVm() =>
            new LockSlipTestViewModel(true, _host, () => _channel, () => _keyData, () => _routing, _ticker, new ImmediateDispatcher());

        private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 60.0);

        [Fact]
        public void Enabling_publishes_the_slider_projection()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 80;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);

            ProjectedWheelLockSlipResult r = _host.LockSlip.Last();
            Assert.Equal(new OutputProjector(_channel.Projector).Project(80), r.LockWheels.FrontLeft, 6);
            Assert.Equal(TestKind.Lock, _host.Session.Active);
        }

        [Fact]
        public void Balls_track_sliders_and_routing()
        {
            LockSlipTestViewModel vm = NewVm();
            Assert.Empty(vm.Balls);
            vm.FrontLeft = 80; vm.FrontRight = 60;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);

            Assert.Equal(4, vm.Balls.Count);
            Assert.Equal(80, vm.Balls[0].Source0100, 9);
            Assert.Equal(BallColour.BrightRed, vm.Balls[0].Colour);

            _routing = EffectRouting.FrontRear;
            vm.RoutingChanged();
            Assert.Equal(new[] { BallColour.Red, BallColour.Blue }, vm.Balls.Select(b => b.Colour).ToArray());
        }

        [Fact]
        public void Idle_for_10s_turns_off_and_publishes_null()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(10.05));
            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.LockSlip.Last());
            Assert.Empty(vm.Balls);
            Assert.Equal(TestKind.None, _host.Session.Active);
        }

        [Fact]
        public void Slider_moves_reset_idle()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(9));
            vm.RearRight = 30;
            _ticker.Advance(TimeSpan.FromSeconds(6));
            Assert.True(vm.IsTestEnabled);
            _ticker.Advance(TimeSpan.FromSeconds(4.1));
            Assert.False(vm.IsTestEnabled);
        }

        [Fact]
        public void Settings_change_mid_test_is_used_next_tick()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 50;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            double before = _host.LockSlip.Last().LockWheels.FrontLeft;

            _channel.Projector = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock);
            _channel.Projector.ModerateOutput += 20;
            _ticker.Advance(Frame);

            double after = _host.LockSlip.Last().LockWheels.FrontLeft;
            Assert.Equal(new OutputProjector(_channel.Projector).Project(KeyDataNormalization.Normalize(50, true, false, 80, 60, 40)), after, 6);
            Assert.NotEqual(before, after);
        }

        [Fact]
        public void Stop_releases_and_clears_balls()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            vm.Stop();
            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.LockSlip.Last());
            Assert.Empty(vm.Balls);
            Assert.Equal(0, _ticker.ActiveCount);
        }

        [Fact]
        public void Another_test_starting_turns_this_one_off()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _host.Session.Begin(new object(), TestKind.GForce);
            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.LockSlip.Last());
        }

        [Fact]
        public void Dragging_single_sets_all_four_wheels()
        {
            _routing = EffectRouting.Mono;
            LockSlipTestViewModel vm = NewVm();
            vm.SingleChannel = 55;
            Assert.Equal(new[] { 55.0, 55, 55, 55 }, new[] { vm.FrontLeft, vm.FrontRight, vm.RearLeft, vm.RearRight });
        }

        [Fact]
        public void Dragging_front_sets_FL_FR_only()
        {
            _routing = EffectRouting.FrontRear;
            LockSlipTestViewModel vm = NewVm();
            vm.RearLeft = 10;
            vm.FrontChannel = 70;
            Assert.Equal(new[] { 70.0, 70, 10, 0 }, new[] { vm.FrontLeft, vm.FrontRight, vm.RearLeft, vm.RearRight });
        }

        [Fact]
        public void Dragging_left_and_right_set_their_wheel_pairs()
        {
            _routing = EffectRouting.LeftRight;
            LockSlipTestViewModel vm = NewVm();
            vm.LeftChannel = 30;
            vm.RightChannel = 90;
            Assert.Equal(new[] { 30.0, 90, 30, 90 }, new[] { vm.FrontLeft, vm.FrontRight, vm.RearLeft, vm.RearRight });
        }

        [Fact]
        public void Dragging_a_wheel_recomputes_group_values_once()
        {
            LockSlipTestViewModel vm = NewVm();
            int single = 0, front = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LockSlipTestViewModel.SingleChannel)) single++;
                if (e.PropertyName == nameof(LockSlipTestViewModel.FrontChannel)) front++;
            };
            vm.FrontLeft = 40;
            Assert.Equal(1, single);
            Assert.Equal(1, front);
            vm.RearRight = 20;
            Assert.Equal(2, single);
        }

        [Fact]
        public void Dragging_a_group_recomputes_group_values_once()
        {
            _routing = EffectRouting.Mono;
            LockSlipTestViewModel vm = NewVm();
            int single = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LockSlipTestViewModel.SingleChannel)) single++; };
            vm.SingleChannel = 60;
            Assert.Equal(1, single);
        }

        [Fact]
        public void Routing_change_switches_visible_slider_set()
        {
            LockSlipTestViewModel vm = NewVm();
            Assert.False(vm.ShowSingle); Assert.False(vm.ShowFrontRear); Assert.False(vm.ShowLeftRight);

            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            _routing = EffectRouting.Mono; vm.RoutingChanged();
            Assert.True(vm.ShowSingle); Assert.False(vm.ShowFrontRear); Assert.False(vm.ShowLeftRight);
            _routing = EffectRouting.FrontRear; vm.RoutingChanged();
            Assert.False(vm.ShowSingle); Assert.True(vm.ShowFrontRear); Assert.False(vm.ShowLeftRight);
            _routing = EffectRouting.LeftRight; vm.RoutingChanged();
            Assert.False(vm.ShowSingle); Assert.False(vm.ShowFrontRear); Assert.True(vm.ShowLeftRight);
            Assert.Contains(nameof(LockSlipTestViewModel.ShowLeftRight), changed);
        }

        [Fact]
        public void Ball_x_equals_group_slider_value()
        {
            _routing = EffectRouting.FrontRear;
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 80; vm.FrontRight = 20; vm.RearLeft = 50;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);

            Assert.Equal(vm.FrontChannel, vm.Balls[0].Source0100, 6);
            Assert.Equal(vm.RearChannel, vm.Balls[1].Source0100, 6);
        }

        [Fact]
        public void Ball_x_equals_single_slider_value_for_mono()
        {
            _routing = EffectRouting.Mono;
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 90; vm.RearRight = 30;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            Assert.Equal(vm.SingleChannel, vm.Balls[0].Source0100, 6);
        }

        [Fact]
        public void Turn_off_resets_group_sliders_to_zero()
        {
            _routing = EffectRouting.FrontRear;
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.FrontChannel = 70;
            Assert.True(vm.FrontChannel > 0);
            vm.IsTestEnabled = false;
            Assert.Equal(0, vm.FrontChannel);
            Assert.Equal(0, vm.RearChannel);
            Assert.Equal(0, vm.SingleChannel);
        }

        [Fact]
        public void Key_data_change_while_running_raises_group_once_and_matches_dot()
        {
            _routing = EffectRouting.FrontRear;
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 50; vm.FrontRight = 30;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            int front = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LockSlipTestViewModel.FrontChannel)) front++; };

            _ticker.Advance(Frame);
            Assert.Equal(0, front);

            _keyData.SMax = 60; _keyData.S90 = 45; _keyData.S75 = 30;
            _ticker.Advance(Frame);
            Assert.Equal(1, front);
            Assert.Equal(vm.FrontChannel, vm.Balls[0].Source0100, 6);

            _ticker.Advance(Frame);
            Assert.Equal(1, front);
        }

        [Fact]
        public void Weights_change_while_running_raises_group_once_and_matches_dot()
        {
            _routing = EffectRouting.FrontRear;
            LockSlipTestViewModel vm = NewVm();
            vm.FrontLeft = 90; vm.FrontRight = 10;
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            int front = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LockSlipTestViewModel.FrontChannel)) front++; };

            _channel.AggregationWMax = 0.9; _channel.AggregationWMin = 0.1;
            _ticker.Advance(Frame);
            Assert.Equal(1, front);
            Assert.Equal(vm.FrontChannel, vm.Balls[0].Source0100, 6);

            _ticker.Advance(Frame);
            Assert.Equal(1, front);
        }

        [Fact]
        public void Group_refresh_does_not_reset_the_idle_timer()
        {
            LockSlipTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(Frame);
            _ticker.Advance(TimeSpan.FromSeconds(9));
            _keyData.SMax = 70;
            _ticker.Advance(TimeSpan.FromSeconds(1.05));
            Assert.False(vm.IsTestEnabled);
        }
    }
}
