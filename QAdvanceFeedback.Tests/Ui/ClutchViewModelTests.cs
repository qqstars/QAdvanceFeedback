using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core.Clutch;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>A plugin stand-in for the Clutch view models.</summary>
    public sealed class FakeClutchHost : IClutchHost
    {
        public ClutchHostStatus Status { get; set; } = new ClutchHostStatus { Final = 50 };
        public List<string> Axes { get; } = new List<string> { "JoystickPlugin.Pedals_Z" };
        public Dictionary<string, double?> AxisValues { get; } = new Dictionary<string, double?>();
        public List<double?> Published { get; } = new List<double?>();
        public int Forgets { get; private set; }
        public double FinalBite { get; set; } = 50;
        public string FinalBiteKey { get; set; }

        public ClutchHostStatus ClutchStatus => Status;
        public IReadOnlyList<string> ControllerAxes() => Axes;
        public double? ReadAxis01(string name) => name != null && AxisValues.TryGetValue(name, out double? v) ? v : null;
        public void ForgetClutchBite() => Forgets++;
        public void PublishClutchTest(double? output0100) => Published.Add(output0100);
        public double FinalBiteForTest => FinalBite;
        public string FinalBiteForTestKey => FinalBiteKey;
    }

    /// <summary>The Clutch tab / ShakeIt panel view models (docs\clutch-bite-design.md).</summary>
    public class ClutchViewModelTests
    {
        private readonly FakePanelHost _host = new FakePanelHost();
        private readonly FakeClutchHost _clutch = new FakeClutchHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private const string Axis = "JoystickPlugin.Pedals_Z";

        private ClutchBiteViewModel NewVm()
        {
            var vm = new ClutchBiteViewModel(_host, _clutch, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        [Fact]
        public void Settings_write_through_hub_and_refresh_from_hub()
        {
            ClutchBiteViewModel vm = NewVm();
            vm.BitePoint = 42;
            vm.Apply = ClutchApplyDirection.ReleaseOnly;
            vm.FallbackAxis = Axis;
            Assert.Equal(42.0, _host.Settings.Clutch.BitePoint);
            Assert.Equal(ClutchApplyDirection.ReleaseOnly, _host.Settings.Clutch.Apply);
            Assert.Equal(Axis, _host.Settings.Clutch.FallbackAxis);

            _host.Settings.Clutch.Gamma = 3.5;
            _host.Hub.NotifyApplied(new[] { SettingField.ClutchGamma }, SettingsOrigin.PluginPage);
            Assert.Equal(3.5, vm.Gamma);
        }

        [Fact]
        public void Axis_choices_start_with_none_and_list_the_controller_axes()
        {
            ClutchBiteViewModel vm = NewVm();
            Assert.Null(vm.AxisChoices[0].Value);
            Assert.Contains(vm.AxisChoices, c => c.Value == Axis);
        }

        [Fact]
        public void Readout_shows_learning_then_matured()
        {
            _clutch.Status = new ClutchHostStatus { Kind = ClutchSourceKind.Game, Final = 47, Learned = 45, Count = 3, Key = "game|#|car|#|GameClutch|#|" };
            ClutchBiteViewModel vm = NewVm();
            Assert.Contains("3", vm.SamplesText);
            Assert.Contains("45.0", vm.LearnedText);
            Assert.Contains("car", vm.CarText);
            string learning = vm.SamplesText;

            _clutch.Status = new ClutchHostStatus { Kind = ClutchSourceKind.Game, Final = 45, Learned = 45, Count = 9, IsMatured = true, Key = "game|#|car|#|GameClutch|#|" };
            _ticker.Advance(TimeSpan.FromSeconds(1.1));
            Assert.NotEqual(learning, vm.SamplesText);
            Assert.Contains("9", vm.SamplesText);
        }

        [Fact]
        public void Forget_command_calls_host()
        {
            ClutchBiteViewModel vm = NewVm();
            vm.ForgetCommand.Execute(null);
            Assert.Equal(1, _clutch.Forgets);
        }

        [Fact]
        public void Graph_curve_is_the_shared_curve_and_moves_with_bite()
        {
            ClutchBiteViewModel vm = NewVm();
            _clutch.FinalBite = 60;
            vm.Refresh();
            Assert.Equal(ClutchBiteCurve.Points(60, 30, 2.5).Select(p => p.Y), vm.Graph.Curve.Select(p => p.Y));
            Assert.Equal(60.0, vm.Graph.BiteMarker);
            Assert.Equal(50.0, vm.Graph.ConfiguredMarker);   // Auto: the configured value is marked too

            vm.Mode = ClutchBiteMode.Manual;
            Assert.Null(vm.Graph.ConfiguredMarker);
        }

        [Fact]
        public void Test_publishes_curve_output_from_axis()
        {
            _host.Settings.Clutch.FallbackAxis = Axis;
            _clutch.AxisValues[Axis] = 0.5;
            ClutchBiteViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(1.0 / 60.0));

            Assert.False(vm.Test.UseSlider);
            Assert.Equal(100.0, _clutch.Published.Last().Value, 6);       // 50 = the bite
            Assert.Equal(50.0, vm.Graph.TestBall.Value.X, 6);
            Assert.Equal(100.0, vm.Graph.TestBall.Value.Y, 6);
            Assert.Equal(TestKind.Clutch, _host.Session.Active);
        }

        [Fact]
        public void Test_uses_slider_without_axis()
        {
            ClutchBiteViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            Assert.True(vm.Test.UseSlider);
            vm.Test.Slider = 35;
            _ticker.Advance(TimeSpan.FromSeconds(1.0 / 60.0));
            Assert.Equal(ClutchBiteCurve.Output(35, 50, 30, 2.5), _clutch.Published.Last().Value, 6);
        }

        [Fact]
        public void Test_auto_offs_after_10s_without_pedal_movement()
        {
            _host.Settings.Clutch.FallbackAxis = Axis;
            _clutch.AxisValues[Axis] = 0.5;
            ClutchBiteViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            _ticker.Advance(TimeSpan.FromSeconds(8));
            _clutch.AxisValues[Axis] = 0.6;                  // the pedal moved: input
            _ticker.Advance(TimeSpan.FromSeconds(8));
            Assert.True(vm.Test.IsTestEnabled);
            _ticker.Advance(TimeSpan.FromSeconds(2.2));
            Assert.False(vm.Test.IsTestEnabled);
            Assert.Null(_clutch.Published.Last());
        }

        [Fact]
        public void Test_stops_on_deactivate()
        {
            ClutchBiteViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            vm.Deactivate();
            Assert.False(vm.Test.IsTestEnabled);
            Assert.Null(_clutch.Published.Last());
            Assert.Null(vm.Graph.TestBall);
        }

        [Fact]
        public void Another_test_stops_clutch_test()
        {
            ClutchBiteViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            _host.Session.Begin(new object(), TestKind.GForce);
            Assert.False(vm.Test.IsTestEnabled);
        }
    }
}
