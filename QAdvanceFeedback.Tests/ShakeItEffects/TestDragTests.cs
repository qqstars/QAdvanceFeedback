using System;
using System.IO;
using System.Text.RegularExpressions;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.ShakeItEffects
{
    /// <summary>The Test Effect drag targets (owner, 2026-11-20): held only while the button is down, let go
    /// at once on release, clamped at the edge outside the control, snapped to rest near it.</summary>
    public class TestDragTests
    {
        [Fact]
        public void Ball_is_held_only_between_press_and_release()
        {
            var hold = new TestDragHold();
            Assert.False(hold.IsHeld);
            Assert.False(hold.Move(leftButtonDown: true));   // hovering with the button down from elsewhere

            hold.Press();
            Assert.True(hold.IsHeld);
            Assert.True(hold.Move(leftButtonDown: true));

            Assert.True(hold.Release());
            Assert.False(hold.IsHeld);
            Assert.False(hold.Move(leftButtonDown: true));
        }

        [Fact]
        public void A_move_with_the_button_already_up_does_not_drag()
        {
            // The button-up went to another element: the next move must not carry the ball.
            var hold = new TestDragHold();
            hold.Press();
            Assert.False(hold.Move(leftButtonDown: false));
            Assert.True(hold.Release());
            Assert.False(hold.IsHeld);
        }

        [Fact]
        public void Each_let_go_reports_once()
        {
            // Button-up, then the lost capture it causes: only the first one snaps.
            var hold = new TestDragHold();
            hold.Press();
            Assert.True(hold.Release());
            Assert.False(hold.Release());
        }

        [Theory]
        [InlineData(0, 100, 0)]
        [InlineData(50, 100, 0.5)]
        [InlineData(-50, 100, -0.5)]
        [InlineData(100, 100, 1)]
        [InlineData(400, 100, 1)]     // outside the pad: stays at the edge
        [InlineData(-400, 100, -1)]
        [InlineData(double.NaN, 100, 0)]
        [InlineData(10, 0, 0)]
        public void Pad_axis_is_clamped_at_the_edge(double offset, double travel, double expected) =>
            Assert.Equal(expected, TestDragMath.PadAxis(offset, travel), 9);

        [Theory]
        [InlineData(0.0, 0.0, true)]
        [InlineData(0.10, -0.10, true)]    // within one ball radius (11 / 100) on both axes
        [InlineData(0.12, 0.0, false)]
        [InlineData(0.0, -0.5, false)]
        public void Pad_release_near_the_centre_snaps(double x, double y, bool snaps) =>
            Assert.Equal(snaps, TestDragMath.PadSnapsToCentre(x, y, 11, 100));

        [Theory]
        [InlineData(29, 0)]
        [InlineData(179, 0.5)]
        [InlineData(329, 1)]
        [InlineData(-200, 0)]    // past either end: stays at that end
        [InlineData(900, 1)]
        public void Bar_value_is_clamped_at_the_ends(double px, double expected) =>
            Assert.Equal(expected, TestDragMath.Bar01(px, 29, 329), 9);

        [Theory]
        [InlineData(0.02, 0)]
        [InlineData(0.05, 0.05)]
        [InlineData(0.8, 0.8)]
        public void Bar_release_near_the_left_end_snaps_to_zero(double value, double expected) =>
            Assert.Equal(expected, TestDragMath.BarSnap(value, 11, 300), 9);

        [Fact]
        public void Both_controls_end_the_drag_on_button_up_on_a_move_with_the_button_up_and_on_lost_capture()
        {
            string code = File.ReadAllText(PluginFile("Ui", "Views", "TestControls.cs"));
            Assert.Equal(2, Regex.Matches(code, @"new TestDragHold\(\)").Count);
            Assert.Equal(2, Regex.Matches(code, @"protected override void OnLostMouseCapture").Count);
            Assert.Equal(2, Regex.Matches(code, @"e\.LeftButton == MouseButtonState\.Pressed").Count);
            // The press and the release are handled, so the surrounding ShakeIt list cannot take them.
            Assert.Equal(4, Regex.Matches(code, @"e\.Handled = true;").Count);
            Assert.DoesNotContain("_dragging", code);
        }

        [Fact]
        public void A_release_outside_the_control_still_ends_the_drag()
        {
            // The press captures the mouse (the button-up comes back wherever the cursor is); the window's
            // own button-up and its losing focus are the backstop, watched only while the drag is held.
            string code = File.ReadAllText(PluginFile("Ui", "Views", "TestControls.cs"));
            Assert.Equal(2, Regex.Matches(code, @"CaptureMouse\(\);\s*_release\.Start\(this, EndDrag\);").Count);
            Assert.Equal(2, Regex.Matches(code, @"if \(!_hold\.Release\(\)\) return;\s*_release\.Stop\(\);").Count);
            Assert.Contains("_window.PreviewMouseLeftButtonUp += OnButtonUp;", code);
            Assert.Contains("_window.Deactivated += OnDeactivated;", code);
            Assert.Contains("_window.PreviewMouseLeftButtonUp -= OnButtonUp;", code);
        }

        [Fact]
        public void Both_controls_opt_out_of_the_shakeit_lists_row_drag()
        {
            // SimHub's effect list is a GongSolutions drag source: without DragSourceIgnore a press on the
            // pad or bar starts a row drag after a few pixels and takes the mouse away (owner, 2026-11-20).
            string code = File.ReadAllText(PluginFile("Ui", "Views", "TestControls.cs"));
            Assert.Equal(2, Regex.Matches(code, @"TestLook\.IgnoreListDrag\(this\);").Count);
            Assert.Contains("GetType(\"GongSolutions.Wpf.DragDrop.DragDrop\", false)", code);
            Assert.Contains("GetField(\"DragSourceIgnoreProperty\")", code);
        }

        [Fact]
        public void Legacy_iracing_label_is_short_in_both_languages()
        {
            Assert.Equal("Legacy iRacing", QAdvanceFeedback.Core.Localization.Strings.TableFor("en")["ShakeItAlgorithm.LegacyIracing"]);
            Assert.Equal("旧版iRacing", QAdvanceFeedback.Core.Localization.Strings.TableFor("zh-Hans")["ShakeItAlgorithm.LegacyIracing"]);
        }

        [Fact]
        public void Bars_sit_under_the_pad_in_one_column()
        {
            string xaml = File.ReadAllText(PluginFile("Ui", "Views", "GForceTestView.xaml"));
            Assert.DoesNotContain("<WrapPanel", xaml);
            int pad = xaml.IndexOf("x:Name=\"GForceTestPad\"", StringComparison.Ordinal);
            int padCaption = xaml.IndexOf("x:Name=\"GForceTestPadCaption\"", StringComparison.Ordinal);
            int bar = xaml.IndexOf("x:Name=\"GForceTestBar\"", StringComparison.Ordinal);
            int angle = xaml.IndexOf("x:Name=\"GForceTestAngleBar\"", StringComparison.Ordinal);
            Assert.True(pad > 0 && pad < padCaption && padCaption < bar && bar < angle);
            string column = xaml.Substring(xaml.IndexOf("x:Name=\"GForceTestControls\"", StringComparison.Ordinal));
            column = column.Substring(0, column.IndexOf("</StackPanel>", StringComparison.Ordinal));
            Assert.Contains("GForceTestAngleBarCaption", column);   // all six in the one column
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            var all = new string[parts.Length + 2];
            all[0] = dir.FullName; all[1] = "QAdvanceFeedback";
            Array.Copy(parts, 0, all, 2, parts.Length);
            return Path.Combine(all);
        }
    }
}
