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
    /// <summary>Fixes from the final review of the Test Effects (docs\shakeit-test-effect-mvvm-report.md).</summary>
    public class ReviewFixTestEffectTests
    {
        // I1 - ShakeIt builds a new edit view each time; they all share the container's view model.

        [Fact]
        public void Panel_stays_active_while_any_of_its_views_is_loaded()
        {
            var host = new FakePanelHost();
            var ticker = new ManualTicker();
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), host, ticker, new ImmediateDispatcher());
            vm.Activate();                 // view A loaded
            vm.Activate();                 // view B loaded before A unloads
            vm.Test.IsTestEnabled = true;

            vm.Deactivate();               // A unloads - B is still showing
            Assert.True(vm.IsActive);
            Assert.True(vm.Test.IsTestEnabled);

            vm.Deactivate();               // B unloads - nothing left on screen
            Assert.False(vm.IsActive);
            Assert.False(vm.Test.IsTestEnabled);
            Assert.Null(host.LockSlip.Last());
        }

        [Fact]
        public void Reshowing_the_panel_does_not_kill_a_running_test()
        {
            var host = new FakePanelHost();
            var ui = new QueuedDispatcher();
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), host, new ManualTicker(), ui);
            vm.Activate();
            vm.Test.IsTestEnabled = true;

            vm.Deactivate();               // old view unloads...
            vm.Activate();                 // ...and the new one loads in the same layout pass
            ui.Flush();

            Assert.True(vm.IsActive);
            Assert.True(vm.Test.IsTestEnabled);
        }

        // I2 - the pulse must run on the real elapsed time, not the nominal 1/60 s.

        [Fact]
        public void Lock_slip_test_steps_with_the_measured_frame_time()
        {
            var host = new FakeTestHost();
            var ticker = new ManualTicker { ActualInterval = TimeSpan.FromSeconds(1.0 / 30.0) };   // a slow timer
            var channel = new WheelChannelSettings
            {
                NormalizePattern = NormalizePattern.Mapping,
                Projector = ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock),
            };
            channel.Pulse.Enabled = true;
            var keyData = new KeyDataView { SMax = 80, S90 = 60, S75 = 40, ShowLowerAnchors = true };
            var vm = new LockSlipTestViewModel(true, host, () => channel, () => keyData, () => EffectRouting.Mono, ticker, new ImmediateDispatcher());
            vm.FrontLeft = vm.FrontRight = vm.RearLeft = vm.RearRight = 100;
            vm.IsTestEnabled = true;

            ticker.Advance(TimeSpan.FromSeconds(1));

            var reference = new LockSlipTestEngine(true, channel);
            var expected = Enumerable.Range(0, 30)
                .Select(_ => reference.Step(new Corners(100, 100, 100, 100), keyData, 1.0 / 30.0).LockAll).ToArray();
            // DateTime ticks round 1/30 s to 0.0333333 s, so allow 0.01 (the nominal 1/60 s would be off
            // by whole percent within a few frames).
            double[] actual = host.LockSlip.Select(r => r.LockAll).ToArray();
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++) Assert.True(System.Math.Abs(expected[i] - actual[i]) < 0.01, $"frame {i}: {expected[i]} vs {actual[i]}");
        }

        // I3 + M2 - one test at a time, between owners of the SAME kind too.

        [Fact]
        public void A_second_test_of_the_same_kind_stops_the_first_and_keeps_its_claim()
        {
            var host = new FakeTestHost();
            var ticker = new ManualTicker();
            var channel = new WheelChannelSettings();
            Func<KeyDataView> keyData = () => new KeyDataView { SMax = 80, S90 = 60, S75 = 40 };
            var a = new LockSlipTestViewModel(true, host, () => channel, keyData, () => EffectRouting.Mono, ticker, new ImmediateDispatcher());
            var b = new LockSlipTestViewModel(true, host, () => channel, keyData, () => EffectRouting.Mono, ticker, new ImmediateDispatcher());

            a.IsTestEnabled = true;
            ticker.Advance(TimeSpan.FromSeconds(0.1));
            b.IsTestEnabled = true;

            Assert.False(a.IsTestEnabled);
            Assert.True(b.IsTestEnabled);
            Assert.Same(b, host.Session.ActiveOwner);
            // A handed over to B on the same outputs: it published no release over B's claim.
            Assert.DoesNotContain(null, host.LockSlip);

            a.Stop();                       // stopping A again must not end B's claim
            Assert.Same(b, host.Session.ActiveOwner);
        }

        [Fact]
        public void A_second_gforce_test_stops_the_first()
        {
            var host = new FakeTestHost();
            var ticker = new ManualTicker();
            var page = new GForceTestViewModel(host, () => new GForceSettings(), ticker, new ImmediateDispatcher());
            var panel = new GForceTestViewModel(host, () => new GForceSettings(), ticker, new ImmediateDispatcher());

            page.IsTestEnabled = true;
            panel.IsTestEnabled = true;

            Assert.False(page.IsTestEnabled);
            Assert.Same(panel, host.Session.ActiveOwner);
        }

        [Fact]
        public void A_test_listens_to_the_session_only_while_it_runs()
        {
            var host = new FakeTestHost();
            var vm = new GForceTestViewModel(host, () => new GForceSettings(), new ManualTicker(), new ImmediateDispatcher());
            Assert.Equal(0, host.Session.ListenerCount);
            vm.IsTestEnabled = true;
            Assert.Equal(1, host.Session.ListenerCount);
            vm.Stop();
            Assert.Equal(0, host.Session.ListenerCount);
        }
    }
}
