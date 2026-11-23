using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>Records what a Test Effect view model publishes.</summary>
    public sealed class FakeTestHost : ITestEffectHost
    {
        public TestEffectSession Session { get; } = new TestEffectSession();
        public List<GForceOutput> GForce { get; } = new List<GForceOutput>();
        public List<ProjectedWheelLockSlipResult> LockSlip { get; } = new List<ProjectedWheelLockSlipResult>();
        public void PublishGForceTest(GForceOutput outputOrNull) => GForce.Add(outputOrNull);
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) => LockSlip.Add(resultOrNull);
        public List<SlipAngleProjectedResult> SlipAngle { get; } = new List<SlipAngleProjectedResult>();
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) => SlipAngle.Add(resultOrNull);
    }

    /// <summary>The G-Force Test Effect, shared by the plugin page and the G-Force ShakeIt panels
    /// (docs\shakeit-test-effect-mvvm-design.md).</summary>
    public class GForceTestViewModelTests
    {
        private readonly FakeTestHost _host = new FakeTestHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly GForceSettings _settings = new GForceSettings();

        private GForceTestViewModel NewVm() => new GForceTestViewModel(_host, () => _settings, _ticker, new ImmediateDispatcher());

        [Fact]
        public void Enabling_publishes_frames_at_the_rate()
        {
            GForceTestViewModel vm = NewVm();
            vm.RateHz = 50;
            vm.IsTestEnabled = true;

            _ticker.Advance(TimeSpan.FromSeconds(1));

            Assert.Equal(50, _host.GForce.Count);
            Assert.All(_host.GForce, o => Assert.NotNull(o));
            Assert.Equal(TestKind.GForce, _host.Session.Active);
        }

        [Fact]
        public void Frame_matches_the_shared_frame_formula()
        {
            GForceTestViewModel vm = NewVm();
            vm.PadX = 0.0;
            vm.PadY = 1.0;     // full braking
            vm.Wheel = 0.4;
            vm.RateHz = 60;
            vm.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(1.0 / 60.0));

            GForceTestFrame.Result expected = GForceTestFrame.Compute(0.0, 1.0, 0.4, new GForceSettings(), new GForceEngine(), TimeSpan.FromSeconds(1.0 / 60.0));
            Assert.Equal(JsonConvert.SerializeObject(expected.Output), JsonConvert.SerializeObject(_host.GForce[0]));
            Assert.Equal(GForceTestFrame.FormatG(expected.LongitudinalG, expected.LongMax), vm.LongText);
        }

        [Fact]
        public void Idle_for_10s_turns_the_toggle_off_and_releases()
        {
            GForceTestViewModel vm = NewVm();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.IsTestEnabled = true;

            _ticker.Advance(TimeSpan.FromSeconds(10.05));

            Assert.False(vm.IsTestEnabled);
            Assert.Contains(nameof(GForceTestViewModel.IsTestEnabled), changed);
            Assert.Null(_host.GForce[_host.GForce.Count - 1]);
            Assert.Equal(TestKind.None, _host.Session.Active);
            int count = _host.GForce.Count;
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(count, _host.GForce.Count);   // nothing more is published
        }

        [Fact]
        public void Input_resets_the_idle_timer()
        {
            GForceTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(9));
            vm.PadX = 0.5;
            _ticker.Advance(TimeSpan.FromSeconds(6));
            Assert.True(vm.IsTestEnabled);
            _ticker.Advance(TimeSpan.FromSeconds(4.1));
            Assert.False(vm.IsTestEnabled);
        }

        [Fact]
        public void Another_test_starting_turns_this_one_off()
        {
            GForceTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _host.Session.Begin(new object(), TestKind.Lock);
            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.GForce[_host.GForce.Count - 1]);
            Assert.Equal(TestKind.Lock, _host.Session.Active);
        }

        [Fact]
        public void Stop_releases_and_turns_off()
        {
            GForceTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));
            vm.Stop();
            Assert.False(vm.IsTestEnabled);
            Assert.Null(_host.GForce[_host.GForce.Count - 1]);
            Assert.Equal(0, _ticker.ActiveCount);
        }

        [Fact]
        public void Never_writes_the_settings_object()
        {
            string before = JsonConvert.SerializeObject(_settings);
            GForceTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.PadX = -0.7; vm.PadY = -0.5; vm.Wheel = 0.9;
            _ticker.Advance(TimeSpan.FromSeconds(2));
            vm.Stop();
            Assert.Equal(before, JsonConvert.SerializeObject(_settings));
        }

        [Fact]
        public void Angle_bar_feeds_test_engine()
        {
            // Integrate Slip Angle ON, lock/slip OFF: only the Angle bar can shake. The published frame
            // must equal the shared formula fed that angle, and differ from the same frame at angle 0.
            _settings.IntegrateWheelLockAndSlip = false;
            _settings.IntegrateSlipAngle = true;
            _settings.ShakeTriggerThresholdPercent = 10.0;
            GForceTestViewModel vm = NewVm();
            vm.PadY = 1.0;
            vm.Angle = 0.7;
            vm.RateHz = 60;
            vm.IsTestEnabled = true;
            TimeSpan dt = TimeSpan.FromSeconds(1.0 / 60.0);
            _ticker.Advance(dt);

            string published = JsonConvert.SerializeObject(_host.GForce[0]);
            string withAngle = JsonConvert.SerializeObject(
                GForceTestFrame.Compute(0.0, 1.0, 0.0, _settings, new GForceEngine(), dt, 0.7).Output);
            string noAngle = JsonConvert.SerializeObject(
                GForceTestFrame.Compute(0.0, 1.0, 0.0, _settings, new GForceEngine(), dt, 0.0).Output);
            Assert.Equal(withAngle, published);
            Assert.NotEqual(noAngle, published);
            Assert.Equal(GForceTestFrame.FormatWheel(70.0), vm.AngleText);
        }

        [Fact]
        public void Switching_off_resets_the_angle_bar()
        {
            GForceTestViewModel vm = NewVm();
            vm.IsTestEnabled = true;
            vm.Angle = 0.5;
            vm.IsTestEnabled = false;
            Assert.Equal(0.0, vm.Angle);
        }
    }
}
