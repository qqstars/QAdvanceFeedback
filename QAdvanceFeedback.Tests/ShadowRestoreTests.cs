using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// LEAVING A SHADOW REBUILDS FROM THE MODE THE DROPDOWN SHOWS, NOT FROM THE STORED SETTINGS.
    /// <para/>
    /// THE BUG THIS CLOSES, which survived three earlier attempts (owner, 2026-10-02: "launch SimHub
    /// WITHOUT any setting files, and with unsupported game, click 'Add to supported list', source text
    /// box can be edit, but it still shows Raw ... STILL, AGAIN AND AGAIN"):
    /// <list type="number">
    /// <item>Fresh config - the stored SourceMode is Manual and the stored sources are Raw.</item>
    /// <item>Pick Viper - only the COMBO and the boxes change; nothing is applied. The title is
    /// unsupported, so the boxes are immediately shadowed back to Raw.</item>
    /// <item>Add to supported list - the shadow ends, and restoring "the channel's own configured
    /// source" restored RAW, because with no Apply that is genuinely what is stored.</item>
    /// </list>
    /// The earlier fixes all addressed how the settings got CORRUPTED. This case needs no corruption at
    /// all: the settings were simply never written, and the restore trusted them anyway.
    /// <para/>
    /// <c>ChannelShadowsRaw</c> already reads the combo rather than the persisted mode, for exactly this
    /// reason - "the driver may have switched without applying, and the page must describe what they are
    /// looking at". The restore now follows the same rule.
    /// </summary>
    public class ShadowRestoreTests
    {
        private readonly ITestOutputHelper _out;
        public ShadowRestoreTests(ITestOutputHelper output) { _out = output; }

        private static string CodeBehind()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(
                dir.FullName, "QAdvanceFeedback", "Settings", "SettingsControl.xaml.cs"));
        }

        [Fact]
        public void The_restore_regenerates_the_preset_the_combo_names()
        {
            string code = CodeBehind();

            int method = code.IndexOf("private void ApplyRawShadowToSourceRows", System.StringComparison.Ordinal);
            Assert.True(method > 0, "ApplyRawShadowToSourceRows not found");
            int end = code.IndexOf("\n        /// <summary>", method, System.StringComparison.Ordinal);
            if (end < 0) end = code.IndexOf("\n        private ", method + 1, System.StringComparison.Ordinal);
            string body = code.Substring(method, end - method);

            // It must build the preset from the SHOWN mode...
            Assert.Contains("new WheelChannelSettings { SourceMode = shown }", body);
            Assert.Contains("preset.ResetSourcesForCurrentMode(isLock)", body);
            Assert.Contains("rows[0].SourceBox.Text = preset.SourceFrontLeft", body);

            // ...and must NOT read the stored channel sources, which is what made it restore Raw.
            Assert.DoesNotContain("channel.SourceFrontLeft", body);
        }

        [Fact]
        public void Custom_is_excluded_because_its_text_IS_the_configuration()
        {
            // There is no preset to regenerate for Custom, and the boxes already hold the driver's own
            // expressions - rewriting them would destroy exactly what the restore is meant to preserve.
            string code = CodeBehind();

            int method = code.IndexOf("private void ApplyRawShadowToSourceRows", System.StringComparison.Ordinal);
            int end = code.IndexOf("\n        /// <summary>", method, System.StringComparison.Ordinal);
            if (end < 0) end = code.IndexOf("\n        private ", method + 1, System.StringComparison.Ordinal);
            string body = code.Substring(method, end - method);

            int guard = body.IndexOf("if (shown != SourceMode.Custom)", System.StringComparison.Ordinal);
            int write = body.IndexOf("rows[0].SourceBox.Text = preset.SourceFrontLeft", System.StringComparison.Ordinal);
            Assert.True(guard > 0 && write > guard,
                "the preset rewrite must be inside the non-Custom guard");
        }

        [Fact]
        public void The_shadow_decision_and_the_restore_read_the_same_source_of_truth()
        {
            // The invariant behind the fix: both halves consult the COMBO. If one reads the combo and
            // the other the persisted settings, they disagree on every unapplied change - which is the
            // whole bug.
            string code = CodeBehind();

            int shadows = code.IndexOf("private bool ChannelShadowsRaw", System.StringComparison.Ordinal);
            int shadowsEnd = code.IndexOf("\n        private ", shadows + 1, System.StringComparison.Ordinal);
            Assert.Contains("GetSelectedTag(isLock ? LockSourceModeCombo : SlipSourceModeCombo",
                code.Substring(shadows, shadowsEnd - shadows));

            int restore = code.IndexOf("private void ApplyRawShadowToSourceRows", System.StringComparison.Ordinal);
            int restoreEnd = code.IndexOf("\n        /// <summary>", restore, System.StringComparison.Ordinal);
            if (restoreEnd < 0) restoreEnd = code.IndexOf("\n        private ", restore + 1, System.StringComparison.Ordinal);
            Assert.Contains("GetSelectedTag(isLock ? LockSourceModeCombo : SlipSourceModeCombo",
                code.Substring(restore, restoreEnd - restore));
        }
    }
}
