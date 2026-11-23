using System;
using System.IO;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// Owner's Test Effect UI rules (follow-up to docs\clutch-bite-design.md and
    /// docs\shakeit-test-effect-mvvm-design.md):
    /// <list type="bullet">
    /// <item>a test's inputs and description show only while it is on;</item>
    /// <item>switching it off resets its inputs (sliders to 0, the G-ball centred);</item>
    /// <item>the Clutch test's slider follows a readable pedal axis;</item>
    /// <item>the clutch readout shows only under Auto;</item>
    /// <item>a Detect button finds the clutch axis by pedalling.</item>
    /// </list>
    /// </summary>
    public class TestEffectUiRulesTests
    {
        private readonly FakePanelHost _host = new FakePanelHost();
        private readonly FakeClutchHost _clutch = new FakeClutchHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1.0 / 60.0);

        private ClutchBiteViewModel NewClutch()
        {
            var vm = new ClutchBiteViewModel(_host, _clutch, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        // ---- Clutch ---------------------------------------------------------------------------------

        [Fact]
        public void Clutch_test_off_resets_the_slider()
        {
            ClutchBiteViewModel vm = NewClutch();
            vm.Test.IsTestEnabled = true;
            vm.Test.Slider = 63;
            vm.Test.IsTestEnabled = false;
            Assert.Equal(0.0, vm.Test.Slider);
        }

        [Fact]
        public void Clutch_test_slider_follows_a_readable_pedal_axis()
        {
            _host.Settings.Clutch.FallbackAxis = "JoystickPlugin.Pedals_Z";
            _clutch.AxisValues["JoystickPlugin.Pedals_Z"] = 0.37;
            ClutchBiteViewModel vm = NewClutch();
            vm.Test.IsTestEnabled = true;
            _ticker.Advance(Frame);
            Assert.Equal(37.0, vm.Test.Slider, 6);
            Assert.False(vm.Test.UseSlider);   // the slider shows the pedal; it is not dragged
        }

        [Fact]
        public void Clutch_readout_shows_only_under_auto()
        {
            ClutchBiteViewModel vm = NewClutch();
            Assert.True(vm.IsAuto);
            vm.Mode = ClutchBiteMode.Manual;
            Assert.False(vm.IsAuto);
        }

        [Fact]
        public void Detect_picks_the_axis_that_moves_and_sets_invert()
        {
            _clutch.Axes.Clear();
            _clutch.Axes.AddRange(new[] { "JoystickPlugin.Wheel_X", "JoystickPlugin.Pedals_Z", "JoystickPlugin.Pedals_Y" });
            _clutch.AxisValues["JoystickPlugin.Wheel_X"] = 0.5;
            _clutch.AxisValues["JoystickPlugin.Pedals_Y"] = 0.0;
            _clutch.AxisValues["JoystickPlugin.Pedals_Z"] = 1.0;       // reads 100 % when released
            ClutchBiteViewModel vm = NewClutch();

            vm.DetectAxisCommand.Execute(null);
            Assert.True(vm.IsDetecting);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            _clutch.AxisValues["JoystickPlugin.Pedals_Z"] = 0.05;      // pressed
            _clutch.AxisValues["JoystickPlugin.Wheel_X"] = 0.52;       // a little steering noise
            _ticker.Advance(TimeSpan.FromSeconds(1));
            _clutch.AxisValues["JoystickPlugin.Pedals_Z"] = 1.0;       // released
            _ticker.Advance(TimeSpan.FromSeconds(4));

            Assert.False(vm.IsDetecting);
            Assert.Equal("JoystickPlugin.Pedals_Z", _host.Settings.Clutch.FallbackAxis);
            Assert.True(_host.Settings.Clutch.InvertAxis);
        }

        [Fact]
        public void Detect_without_pedal_movement_changes_nothing()
        {
            _host.Settings.Clutch.FallbackAxis = "JoystickPlugin.Old";
            ClutchBiteViewModel vm = NewClutch();
            vm.DetectAxisCommand.Execute(null);
            _ticker.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal("JoystickPlugin.Old", _host.Settings.Clutch.FallbackAxis);
            Assert.False(string.IsNullOrEmpty(vm.DetectText));
        }

        // ---- G-Force ----------------------------------------------------------------------------------

        [Fact]
        public void GForce_test_off_centres_the_ball_and_resets_the_bar()
        {
            var host = new FakeTestHost();
            var vm = new GForceTestViewModel(host, () => new GForceSettings(), _ticker, new ImmediateDispatcher());
            vm.IsTestEnabled = true;
            vm.PadX = 0.6; vm.PadY = -0.4; vm.Wheel = 0.8;
            vm.IsTestEnabled = false;
            Assert.Equal((0.0, 0.0, 0.0), (vm.PadX, vm.PadY, vm.Wheel));
        }

        // ---- Lock/Slip --------------------------------------------------------------------------------

        [Fact]
        public void Lock_slip_test_off_resets_the_sliders_and_the_test_sits_with_the_graph()
        {
            var vm = new WheelChannelEffectViewModel(true, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            vm.Test.IsTestEnabled = true;
            vm.Test.FrontLeft = 70; vm.Test.RearRight = 30;
            vm.Test.IsTestEnabled = false;
            Assert.Equal((0.0, 0.0, 0.0, 0.0), (vm.Test.FrontLeft, vm.Test.FrontRight, vm.Test.RearLeft, vm.Test.RearRight));
            Assert.Same(vm.Test, vm.Graph.Test);
        }

        // ---- Views ------------------------------------------------------------------------------------

        [Fact]
        public void Views_show_test_inputs_and_notes_only_while_enabled_and_lock_slip_test_follows_the_graph()
        {
            string wheel = View("WheelChannelEffectView.xaml"), graph = View("GraphView.xaml");
            Assert.DoesNotContain("ShakeItPanel.Test.Header", wheel);
            Assert.True(graph.IndexOf("ShakeItPanel.Test.Header", StringComparison.Ordinal) > graph.IndexOf("SourceProjectedGraph", StringComparison.Ordinal));

            string gforce = View("GForceTestView.xaml");
            Assert.Contains("x:Name=\"GForceTestNote\" Text=\"{v:Loc GForce.Test.Note}\" TextWrapping=\"Wrap\" FontStyle=\"Italic\"\n                 Visibility=\"{Binding IsTestEnabled", gforce.Replace("\r\n", "\n"));

            string clutch = View("ClutchBiteView.xaml");
            Assert.Contains("DetectAxisCommand", clutch);
            Assert.Contains("IsAuto", clutch);
        }

        private static string View(string name)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "Ui", "Views", name));
        }
    }
}
