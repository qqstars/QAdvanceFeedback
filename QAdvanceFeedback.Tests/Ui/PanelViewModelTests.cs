using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>A plugin stand-in for the ShakeIt panel view models: real settings and a real hub.</summary>
    public sealed class FakePanelHost : IPanelHost, ILearningMaturityHost
    {
        public FakePanelHost()
        {
            Settings = new QAdvanceFeedbackSettings();
            Hub = new SettingsHub(() => Settings, _ => Saves++, () => CurrentGameId);
        }

        public int Saves { get; private set; }
        public QAdvanceFeedbackSettings Settings { get; set; }
        public SettingsHub Hub { get; }
        public string CurrentGameId { get; set; } = "TestGame";
        public QAdvanceFeedback.Core.SourceNativeness CurrentLockNativeness { get; set; }
        public QAdvanceFeedback.Core.SourceNativeness CurrentSlipNativeness { get; set; }
        public int KeyDataRevision { get; set; }
        public bool ViperAvailable { get; set; }
        public KeyDataTriple Learned { get; set; }
        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) => Learned;
        public KeyDataMaturity Maturity { get; set; }
        public KeyDataMaturity LearnedKeyDataMaturity(bool isLock, string sourceIdentity) => Maturity;
        public QAdvanceFeedback.Core.SlipAngle.SlipAngleWindowMaturity? SlipAngleMaturity { get; set; }
        public QAdvanceFeedback.Core.SlipAngle.SlipAngleWindowMaturity? LearnedSlipAngleMaturity(string sourceIdentity) => SlipAngleMaturity;

        public TestEffectSession Session { get; } = new TestEffectSession();
        public List<GForceOutput> GForce { get; } = new List<GForceOutput>();
        public List<ProjectedWheelLockSlipResult> LockSlip { get; } = new List<ProjectedWheelLockSlipResult>();
        public void PublishGForceTest(GForceOutput outputOrNull) => GForce.Add(outputOrNull);
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) => LockSlip.Add(resultOrNull);
        public List<SlipAngleProjectedResult> SlipAngle { get; } = new List<SlipAngleProjectedResult>();
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) => SlipAngle.Add(resultOrNull);
    }

    public sealed class FakeRouting : IRoutingTarget
    {
        public EffectRouting Routing { get; set; } = EffectRouting.Mono;
    }

    /// <summary>
    /// The ShakeIt panel view models (docs\shakeit-test-effect-mvvm-design.md, "ShakeIt panels rebuilt on
    /// MVVM"): the behaviour the code-behind panels had, now in testable view models.
    /// </summary>
    public class PanelViewModelTests
    {
        private readonly FakePanelHost _host = new FakePanelHost();
        private readonly ManualTicker _ticker = new ManualTicker();

        private KeyDataViewModel NewKeyData(bool isLock = true)
        {
            var vm = new KeyDataViewModel(isLock, _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        private static string Id(WheelChannelSettings ch) => KeyDataPanelModel.Identity(ch);

        [Fact]
        public void KeyData_auto_makes_values_read_only_and_smax_only_hides_lower()
        {
            WheelChannelSettings lockCh = _host.Settings.Lock;
            lockCh.KeyDataPoints.SetAutoGenerate(Id(lockCh), true);
            lockCh.NormalizePattern = NormalizePattern.MaxGripOnly;

            KeyDataViewModel vm = NewKeyData();

            Assert.True(vm.Auto);
            Assert.False(vm.ValuesEditable);
            Assert.False(vm.ShowLowerAnchors);
            Assert.Equal(NormalizePattern.MaxGripOnly, vm.Pattern);

            vm.Auto = false;
            Assert.True(vm.ValuesEditable);
            Assert.False(lockCh.KeyDataPoints.GetAutoGenerate(Id(lockCh)));
        }

        [Fact]
        public void KeyData_commits_after_700ms_through_hub_and_flushes_on_pattern_change()
        {
            WheelChannelSettings lockCh = _host.Settings.Lock;
            lockCh.KeyDataPoints.SetAutoGenerate(Id(lockCh), false);
            lockCh.NormalizePattern = NormalizePattern.Mapping;
            KeyDataViewModel vm = NewKeyData();

            vm.SMax = 90; vm.S90 = 70; vm.S75 = 50;
            _ticker.Advance(TimeSpan.FromMilliseconds(600));
            Assert.False(lockCh.KeyDataPoints.TryGetManual(_host.CurrentGameId, Id(lockCh), out double a, out _, out _) && a == 90);
            _ticker.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(lockCh.KeyDataPoints.TryGetManual(_host.CurrentGameId, Id(lockCh), out a, out double b, out double c));
            Assert.Equal((90.0, 70.0, 50.0), (a, b, c));

            vm.SMax = 95;
            vm.Pattern = NormalizePattern.MaxGripOnly;   // the pending edit is written first
            lockCh.KeyDataPoints.TryGetManual(_host.CurrentGameId, Id(lockCh), out a, out _, out _);
            Assert.Equal(95.0, a);
            Assert.Equal(NormalizePattern.MaxGripOnly, lockCh.NormalizePattern);
        }

        [Fact]
        public void KeyData_refresh_never_overwrites_a_pending_edit()
        {
            WheelChannelSettings lockCh = _host.Settings.Lock;
            lockCh.KeyDataPoints.SetAutoGenerate(Id(lockCh), false);
            KeyDataViewModel vm = NewKeyData();

            vm.SMax = 91;
            _host.KeyDataRevision++;
            _ticker.Advance(TimeSpan.FromMilliseconds(300));   // no refresh tick yet, but a hub event:
            _host.Hub.NotifyApplied(new[] { SettingField.LockKeyDataValues }, SettingsOrigin.PluginPage);
            Assert.Equal(91.0, vm.SMax);
        }

        [Fact]
        public void GForce_axis_editor_stays_editable_under_auto_and_readout_shows_default()
        {
            _host.Settings.GForce.AccelMaxMode = GMaxMode.Auto;
            _host.Settings.GForce.FixedAccelMaxG = 0.75;
            var vm = new GForceEffectViewModel(_host, _ticker, new ImmediateDispatcher());
            vm.Activate();

            GForceAxisViewModel accel = vm.Axes[0];
            Assert.True(accel.Auto);
            Assert.True(vm.Available);
            Assert.Equal(0.75, accel.Max);
            Assert.Equal(GForceReadout.ForAxis(_host.Settings.GForce, SettingField.AccelMaxMode), accel.Readout);

            accel.Max = 1.25;
            Assert.Equal(1.25, _host.Settings.GForce.FixedAccelMaxG);
            accel.Auto = false;
            Assert.Equal(GMaxMode.Fixed, _host.Settings.GForce.AccelMaxMode);
        }

        [Fact]
        public void GForce_lateral_and_integrate_write_through_hub()
        {
            var vm = new GForceEffectViewModel(_host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            bool integrate = _host.Settings.GForce.IntegrateWheelLockAndSlip;

            vm.LateralDirection = LateralDirectionMode.Reversed;
            vm.Integrate = !integrate;

            Assert.Equal(LateralDirectionMode.Reversed, _host.Settings.GForce.LateralDirection);
            Assert.Equal(!integrate, _host.Settings.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(2, _host.Saves);
        }

        [Fact]
        public void Hub_change_from_page_updates_viewmodel()
        {
            var vm = new GForceEffectViewModel(_host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            var changed = new List<string>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            _host.Settings.GForce.LateralDirection = LateralDirectionMode.Reversed;
            _host.Hub.NotifyApplied(new[] { SettingField.LateralDirection }, SettingsOrigin.PluginPage);

            Assert.Equal(LateralDirectionMode.Reversed, vm.LateralDirection);
            Assert.Contains(nameof(GForceEffectViewModel.LateralDirection), changed);
            Assert.Equal(0, _host.Saves);   // a refresh is never a write
        }

        [Fact]
        public void Wheel_routing_change_writes_target_and_updates_balls()
        {
            var routing = new FakeRouting();
            var vm = new WheelChannelEffectViewModel(true, routing, _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(1.0 / 60.0));
            Assert.Single(vm.Graph.Balls);

            vm.Routing = EffectRouting.Corners;

            Assert.Equal(EffectRouting.Corners, routing.Routing);
            Assert.Equal(4, vm.Graph.Balls.Count);
        }

        [Fact]
        public void Source_options_follow_availability()
        {
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt }, vm.Sources.Select(s => s.Value).ToArray());

            _host.ViperAvailable = true;
            _host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.Contains(SourceMode.Viper, vm.Sources.Select(s => s.Value));

            vm.Source = SourceMode.ShakeIt;
            Assert.Equal(SourceMode.ShakeIt, _host.Settings.Lock.SourceMode);
        }

        [Fact]
        public void Graph_curve_is_the_source_to_projected_graph()
        {
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            KeyDataView k = vm.KeyData.Current;
            var expected = SourceToProjectedGraph.Points(_host.Settings.Lock.Projector, true,
                _host.Settings.Lock.NormalizePattern == NormalizePattern.MaxGripOnly, k.SMax, k.S90, k.S75, 100, 100);

            Assert.Equal(expected.Count, vm.Graph.Curve.Count);
            Assert.Equal(100 - expected[10].Y, vm.Graph.Curve[10].Y, 9);
        }

        [Theory]
        [InlineData(SourceMode.Manual, SourceNativeness.Fallback, true)]
        [InlineData(SourceMode.ShakeIt, SourceNativeness.Fallback, false)]
        [InlineData(SourceMode.Viper, SourceNativeness.Fallback, false)]
        [InlineData(SourceMode.Custom, SourceNativeness.Fallback, false)]
        [InlineData(SourceMode.Manual, SourceNativeness.Native, false)]
        [InlineData(SourceMode.Manual, SourceNativeness.Unknown, false)]
        public void Warning_shown_only_for_plugin_internal_and_fallback(SourceMode mode, SourceNativeness n, bool expected)
        {
            foreach (bool isLock in new[] { true, false })
            {
                var host = new FakePanelHost();
                (isLock ? host.Settings.Lock : host.Settings.Slip).SourceMode = mode;
                if (isLock) host.CurrentLockNativeness = n; else host.CurrentSlipNativeness = n;
                var vm = new WheelChannelEffectViewModel(isLock, new FakeRouting(), host, _ticker, new ImmediateDispatcher());
                vm.Activate();

                Assert.Equal(expected, vm.ShowFallbackWarning);
                Assert.Equal(expected, vm.FallbackWarning.Length > 0);
            }
        }

        [Fact]
        public void Warning_follows_availability_notification_and_ignores_the_other_channel()
        {
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            Assert.False(vm.ShowFallbackWarning);

            _host.CurrentSlipNativeness = SourceNativeness.Fallback;
            _host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.False(vm.ShowFallbackWarning);

            _host.CurrentLockNativeness = SourceNativeness.Fallback;
            _host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.True(vm.ShowFallbackWarning);
            Assert.Equal("Not natively supported by this game — using the fallback estimate (speed/RPM).", vm.FallbackWarning);
        }

        [Fact]
        public void Deactivate_stops_running_test()
        {
            var vm = new WheelChannelEffectViewModel(false, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));

            vm.Deactivate();

            Assert.False(vm.Test.IsTestEnabled);
            Assert.Null(_host.LockSlip.Last());

            var g = new GForceEffectViewModel(_host, _ticker, new ImmediateDispatcher());
            g.Activate();
            g.Test.IsTestEnabled = true;
            g.Deactivate();
            Assert.False(g.Test.IsTestEnabled);
        }
    }
}
