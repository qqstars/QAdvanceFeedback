using System.IO;
using System.Text.RegularExpressions;
using QAdvanceFeedback;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// "RESTORE ALL DEFAULTS" LEAVES THE CUSTOM SOURCE ALONE (owner, 2026-09-28: "Restore to Default
    /// will ONLY override the settings for the known sources, say, Raw, ShakeIt and Viper. The Custom
    /// source settings will NOT be overriden").
    /// <para/>
    /// Raw, ShakeIt and Viper are this plugin's own presets, and restoring them to shipped values is
    /// exactly what the button promises. A hand-written source configuration is the driver's work and
    /// there is no default to restore it TO.
    /// <para/>
    /// Also covers the settings page's own new invariants, which can only be checked by reading the
    /// source - the WPF control cannot be instantiated from this project.
    /// </summary>
    public class CustomSourceRestoreTests
    {
        private readonly ITestOutputHelper _out;
        public CustomSourceRestoreTests(ITestOutputHelper output) { _out = output; }

        private static string SettingsDirectory()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            return Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings");
        }

        [Fact]
        public void A_restore_keeps_the_custom_source_configuration()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.SourceFrontLeft = "my.custom.lock.fl";
            settings.Lock.SwitchSourceMode(SourceMode.Custom, true);
            settings.Slip.SourceFrontLeft = "my.custom.slip.fl";
            settings.Slip.SwitchSourceMode(SourceMode.Custom, false);
            settings.Lock.ArchiveCurrentSources();
            settings.Slip.ArchiveCurrentSources();

            settings.RestoreDefaults();

            Assert.Equal("my.custom.lock.fl", settings.Lock.CustomSources.FrontLeft);
            Assert.Equal("my.custom.slip.fl", settings.Slip.CustomSources.FrontLeft);
        }

        [Fact]
        public void A_restore_keeps_the_custom_cold_start_reference_too()
        {
            // Half a configuration would be worse than either outcome: the source text and its
            // reference have to survive together or not at all.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.KeyDataPointDefaults.LockCustom = new KeyDataPointDefaultSet(42.0, 37.8, 29.4);

            settings.RestoreDefaults();

            Assert.NotNull(settings.KeyDataPointDefaults.LockCustom);
            Assert.Equal(42.0, settings.KeyDataPointDefaults.LockCustom.SMax, 6);
        }

        [Fact]
        public void A_restore_DOES_reset_the_three_presets()
        {
            // The other half of the promise - the button must actually restore what it says it does.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.SourceFrontLeft = "vandalised";
            settings.Lock.ArchiveCurrentSources();

            settings.RestoreDefaults();

            Assert.DoesNotContain("vandalised", settings.Lock.SourceFrontLeft ?? string.Empty);
            Assert.Equal(SourceMode.Manual, settings.Lock.SourceMode);
        }

        [Fact]
        public void The_viper_references_the_panel_should_have_shown_are_the_small_ones()
        {
            // The values the screenshot should have displayed, pinned so the numbers behind the bug
            // above cannot drift: Viper reads a 0-1 slip ratio scaled to 0-100, so its grip limit sits
            // far below Raw's. Raw's 75 / 85 appearing on a Viper channel is the visible symptom.
            KeyDataPointDefaults shipped = KeyDataPointDefaults.CreateShipped();

            Assert.True(shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, false,
                out double slipSMax, out double slipS90, out double slipS75));
            Assert.Equal(10.0, slipSMax, 6);
            Assert.Equal(9.0, slipS90, 6);
            Assert.Equal(7.0, slipS75, 6);

            Assert.True(shipped.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, true,
                out double lockSMax, out double lockS90, out double lockS75));
            Assert.Equal(15.0, lockSMax, 6);
            Assert.Equal(13.5, lockS90, 6);
            Assert.Equal(10.5, lockS75, 6);

            // And what the screenshot actually showed, so the diagnosis stays legible.
            Assert.Equal(75.0, KeyDataPointSettings.SlipDefaultSMax, 6);
            Assert.Equal(67.5, KeyDataPointSettings.SlipDefaultSMax * KeyDataPointSettings.DerivedS90Fraction, 6);
            Assert.Equal(52.5, KeyDataPointSettings.SlipDefaultSMax * KeyDataPointSettings.DerivedS75Fraction, 6);
        }

        /// <summary>The single XAML line declaring <paramref name="name"/>, for asserting on its
        /// attributes without matching some other element's.</summary>
        private static string LineDeclaring(string xaml, string name)
        {
            foreach (string line in xaml.Split('\n'))
                if (line.Contains("x:Name=\"" + name + "\"")) return line;
            throw new Xunit.Sdk.XunitException("no element named " + name);
        }

        [Fact]
        public void Every_new_string_key_exists_in_both_languages()
        {
            string dir = Path.Combine(Directory.GetParent(SettingsDirectory()).FullName, "Core", "Localization");
            string en = File.ReadAllText(Path.Combine(dir, "StringTableEn.cs"));
            string zh = File.ReadAllText(Path.Combine(dir, "StringTableZhHans.cs"));

            foreach (string key in new[]
                     {
                         "Sources.Mode.Custom", "Sources.Mode.CustomNote", "Sources.ViperUnsupported.Note",
                         "Sources.ViperGame.Add", "Sources.ViperGame.Remove",
                         "KeyData.ShadowingRaw.Note", "Apply.Unapplied.Banner",
                     })
            {
                Assert.Contains("\"" + key + "\"", en);
                Assert.Contains("\"" + key + "\"", zh);
            }
        }
    }
}
