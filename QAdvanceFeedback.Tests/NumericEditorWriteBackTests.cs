using System.IO;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE BUG THAT MADE EVERY SETTING UNSAVEABLE (owner-reported, 2026-10-01: "CHANGE NUMBER, EXIT,
    /// RESTART, KeyPoints RESTORED ... CONFIG FILE DOES NOT HAVE UPDATED VALUE").
    /// <para/>
    /// <c>NumericEditor</c> wraps MahApps' NumericUpDown and exposes a <c>Value</c> dependency
    /// property. The DP pushed DOWN into the inner control, which is the path a programmatic
    /// assignment takes - but a DRIVER typing, or clicking +/-, changes only the INNER control. The DP
    /// kept its old number. Every reader on the settings page reads the DP, so the page displayed the
    /// new value while saving the old one, on all 77 editors - key data points, thresholds, curve
    /// anchors, G-Force, everything.
    /// <para/>
    /// WHY NOTHING CAUGHT IT. The WPF control cannot be constructed from this project, so the coverage
    /// for it was a harness that set <c>Value</c> programmatically - the one direction that worked.
    /// Both directions are asserted here against the source, and the ORDER matters as much as the
    /// write: handlers read <c>Value</c>, so the DP has to be current before the event is raised.
    /// </summary>
    public class NumericEditorWriteBackTests
    {
        private readonly ITestOutputHelper _out;
        public NumericEditorWriteBackTests(ITestOutputHelper output) { _out = output; }

        private static string EditorSource()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings", "NumericEditor.xaml.cs"));
        }

        [Fact]
        public void A_user_edit_writes_back_into_the_Value_dependency_property()
        {
            string code = EditorSource();

            int handler = code.IndexOf("private void OnInnerValueChanged", System.StringComparison.Ordinal);
            Assert.True(handler > 0, "OnInnerValueChanged not found");
            int end = code.IndexOf("\n        /// <summary>", handler, System.StringComparison.Ordinal);
            if (end < 0) end = code.Length;
            string body = code.Substring(handler, end - handler);
            _out.WriteLine(body);

            // It must ASSIGN Value, not merely forward the event.
            Assert.Contains("Value = e.NewValue", body);

            // And the assignment must come BEFORE the event is raised, or every handler still reads
            // the stale number and the bug simply moves up one level.
            int assign = body.IndexOf("Value = e.NewValue", System.StringComparison.Ordinal);
            int raise = body.IndexOf("ValueChanged?.Invoke", System.StringComparison.Ordinal);
            Assert.True(assign > 0 && raise > assign,
                "the Value DP must be updated before ValueChanged is raised");
        }

        [Fact]
        public void Both_directions_guard_the_echo_so_they_cannot_loop()
        {
            // Down (DP -> inner) and up (inner -> DP) now both assign, so each needs an equality guard
            // or the pair ping-pongs forever on the first edit.
            string code = EditorSource();

            Assert.Contains("if (!NullableEquals(editor.Inner.Value, next)) editor.Inner.Value = next;", code);
            Assert.Contains("if (!NullableEquals(Value, e.NewValue)) Value = e.NewValue;", code);
            Assert.Contains("private static bool NullableEquals", code);
        }

        [Fact]
        public void A_preset_channel_with_mismatched_sources_repairs_itself()
        {
            // The other half of the same incident: a config already written as "SourceMode = Viper"
            // over four RAW property names stays broken forever, because the page faithfully restores
            // the text it was given. A preset's sources are this plugin's, not the driver's - editing
            // them is defined to move the channel to Custom - so a mismatch can only be damage.
            var channel = new WheelChannelSettings { SourceMode = SourceMode.Viper };
            channel.ResetSourcesForCurrentMode(isLockChannel: true);
            string viperFl = channel.SourceFrontLeft;

            // Simulate the damage: Raw's names stored while the mode still says Viper.
            var raw = new WheelChannelSettings();
            raw.ResetSourcesToDefault(isLockChannel: true);
            channel.SourceFrontLeft = raw.SourceFrontLeft;
            channel.SourceFrontRight = raw.SourceFrontRight;
            channel.SourceRearLeft = raw.SourceRearLeft;
            channel.SourceRearRight = raw.SourceRearRight;
            channel.SourceMode = SourceMode.Viper;

            Assert.True(channel.RepairPresetSourcesIfStale(isLockChannel: true), "the damage should be detected");
            Assert.Equal(viperFl, channel.SourceFrontLeft);
            Assert.Equal(SourceMode.Viper, channel.SourceMode);

            // Idempotent - a healthy channel is left alone, so the repair does not run every start.
            Assert.False(channel.RepairPresetSourcesIfStale(isLockChannel: true));
        }

        [Fact]
        public void A_custom_channel_is_never_repaired_because_its_text_IS_the_configuration()
        {
            // There is no canonical text for Custom to be compared against, and that text is the
            // driver's own work - rewriting it would be the very damage this method exists to undo.
            var channel = new WheelChannelSettings { SourceMode = SourceMode.Custom };
            channel.SourceFrontLeft = "my.own.expression";
            channel.SourceFrontRight = "my.own.expression.2";

            Assert.False(channel.RepairPresetSourcesIfStale(isLockChannel: true));
            Assert.Equal("my.own.expression", channel.SourceFrontLeft);
        }

        [Fact]
        public void The_repair_runs_at_startup_and_is_persisted_once()
        {
            // It has to reach the ENGINE, not just the page - the engine reads the stored sources every
            // frame - and it must be saved, or it would be redone on every start.
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            string plugin = File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"));

            int load = plugin.IndexOf("_settings = ConfigStore.Load(", System.StringComparison.Ordinal);
            int repair = plugin.IndexOf("RepairPresetSourcesIfStale(true)", load, System.StringComparison.Ordinal);
            Assert.True(repair > load, "the repair must run straight after the config is loaded");
            Assert.Contains("RepairPresetSourcesIfStale(false)", plugin);

            int save = plugin.IndexOf("ConfigStore.Save(_configPath", repair, System.StringComparison.Ordinal);
            Assert.True(save > repair, "a repaired config must be written back");
        }
    }
}
