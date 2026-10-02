using Newtonsoft.Json;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// EACH SOURCE KEEPS ITS OWN CONFIGURATION (owner, 2026-09-28: "the settings should be dedicated to
    /// each source, switching between the sources will NOT derive the settings from each other").
    /// <para/>
    /// The active four wheels stay where they always were - the one thing the engine reads - and each
    /// non-selected mode keeps an archive beside them. See <see cref="WheelSourceSet"/> for why that
    /// shape was chosen over replacing the active fields.
    /// </summary>
    public class PerSourceSettingsTests
    {
        private readonly ITestOutputHelper _out;
        public PerSourceSettingsTests(ITestOutputHelper output) { _out = output; }

        private static WheelChannelSettings LockChannel()
        {
            var c = new WheelChannelSettings();
            c.ResetSourcesToDefault(true);
            return c;
        }

        [Fact]
        public void Switching_away_and_back_returns_the_drivers_own_text_not_a_regenerated_default()
        {
            // THE CORE REQUIREMENT. A hand-edited Raw configuration must survive a trip through Viper.
            var c = LockChannel();
            c.SourceFrontLeft = "MyPlugin.Something.FL";
            c.ScriptTypeFrontLeft = ScriptType.NCalc;

            c.SwitchSourceMode(SourceMode.Viper, true);
            Assert.Equal(SourceMode.Viper, c.SourceMode);
            Assert.Contains(ViperPropertyNames.ComputedPrefix, c.SourceFrontLeft);

            c.SwitchSourceMode(SourceMode.Manual, true);
            Assert.Equal("MyPlugin.Something.FL", c.SourceFrontLeft);
            Assert.Equal(ScriptType.NCalc, c.ScriptTypeFrontLeft);
        }

        [Fact]
        public void Editing_one_mode_never_reaches_another()
        {
            var c = LockChannel();
            c.SwitchSourceMode(SourceMode.Viper, true);
            string viperFl = c.SourceFrontLeft;

            c.SwitchSourceMode(SourceMode.Manual, true);
            c.SourceFrontLeft = "edited.raw.only";

            c.SwitchSourceMode(SourceMode.Viper, true);
            _out.WriteLine($"viper after editing raw: {c.SourceFrontLeft}");
            Assert.Equal(viperFl, c.SourceFrontLeft);
            Assert.DoesNotContain("edited.raw.only", c.SourceFrontLeft);
        }

        [Fact]
        public void A_first_ever_switch_lands_on_that_modes_preset()
        {
            // No archive yet, so the mode's shipped defaults apply - a driver switching to Viper for the
            // first time must get a working configuration, not four blank boxes.
            var c = LockChannel();
            c.SwitchSourceMode(SourceMode.ShakeIt, true);

            Assert.Equal(SourceMode.ShakeIt, c.SourceMode);
            Assert.All(new[] { c.SourceFrontLeft, c.SourceFrontRight, c.SourceRearLeft, c.SourceRearRight },
                v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }

        [Fact]
        public void Switching_to_Custom_keeps_the_edit_in_progress()
        {
            // Custom is REACHED BY EDITING, so it must start from what the driver just typed rather than
            // wiping it - the one mode where an empty archive does not mean "apply a preset".
            var c = LockChannel();
            c.SourceFrontLeft = "half.typed.expression";

            c.SwitchSourceMode(SourceMode.Custom, true);

            Assert.Equal(SourceMode.Custom, c.SourceMode);
            Assert.Equal("half.typed.expression", c.SourceFrontLeft);
        }

        [Fact]
        public void Custom_keeps_its_own_configuration_across_switches()
        {
            var c = LockChannel();
            c.SourceFrontLeft = "custom.fl";
            c.SwitchSourceMode(SourceMode.Custom, true);

            c.SwitchSourceMode(SourceMode.Viper, true);
            c.SwitchSourceMode(SourceMode.Custom, true);

            Assert.Equal("custom.fl", c.SourceFrontLeft);
        }

        [Fact]
        public void Switching_to_the_mode_already_selected_changes_nothing()
        {
            var c = LockChannel();
            c.SourceFrontLeft = "untouched";
            c.SwitchSourceMode(SourceMode.Manual, true);
            Assert.Equal("untouched", c.SourceFrontLeft);
        }

        [Fact]
        public void All_four_modes_coexist_independently()
        {
            // The owner can have every mode configured at once and move between them freely.
            var c = LockChannel();
            c.SourceFrontLeft = "raw.fl";

            c.SwitchSourceMode(SourceMode.ShakeIt, true);
            c.SourceFrontLeft = "shakeit.fl";

            c.SwitchSourceMode(SourceMode.Viper, true);
            c.SourceFrontLeft = "viper.fl";

            c.SwitchSourceMode(SourceMode.Custom, true);
            c.SourceFrontLeft = "custom.fl";

            foreach (var expected in new[]
                     {
                         (SourceMode.Manual, "raw.fl"), (SourceMode.ShakeIt, "shakeit.fl"),
                         (SourceMode.Viper, "viper.fl"), (SourceMode.Custom, "custom.fl"),
                     })
            {
                c.SwitchSourceMode(expected.Item1, true);
                Assert.Equal(expected.Item2, c.SourceFrontLeft);
            }
        }

        [Fact]
        public void Lock_and_Slip_are_entirely_separate()
        {
            // "Lock and Slip MUST be separated" - they are different objects, but pin it so a future
            // shared/static archive cannot creep in.
            var lockCh = LockChannel();
            var slipCh = new WheelChannelSettings();
            slipCh.ResetSourcesToDefault(false);

            lockCh.SwitchSourceMode(SourceMode.Viper, true);

            Assert.Equal(SourceMode.Viper, lockCh.SourceMode);
            Assert.Equal(SourceMode.Manual, slipCh.SourceMode);
            Assert.DoesNotContain(ViperPropertyNames.ComputedPrefix, slipCh.SourceFrontLeft ?? string.Empty);
        }

        [Fact]
        public void An_existing_config_with_no_archives_migrates_without_losing_anything()
        {
            // A file written before the archives existed has only the active fields. It must load
            // unchanged, and its current configuration must become that mode's archive on the first
            // switch rather than being discarded.
            const string legacy = @"{
                ""SourceMode"": 0,
                ""SourceFrontLeft"": ""legacy.fl"",
                ""SourceFrontRight"": ""legacy.fr"",
                ""SourceRearLeft"": ""legacy.rl"",
                ""SourceRearRight"": ""legacy.rr""
            }";

            var c = JsonConvert.DeserializeObject<WheelChannelSettings>(legacy);
            Assert.Equal("legacy.fl", c.SourceFrontLeft);
            Assert.Null(c.RawSources);

            c.SwitchSourceMode(SourceMode.Viper, true);
            c.SwitchSourceMode(SourceMode.Manual, true);

            Assert.Equal("legacy.fl", c.SourceFrontLeft);
            Assert.Equal("legacy.rr", c.SourceRearRight);
        }

        [Fact]
        public void The_archives_round_trip_through_json()
        {
            var c = LockChannel();
            c.SourceFrontLeft = "raw.fl";
            c.SwitchSourceMode(SourceMode.Viper, true);
            c.ArchiveCurrentSources();

            var reloaded = JsonConvert.DeserializeObject<WheelChannelSettings>(JsonConvert.SerializeObject(c));
            reloaded.SwitchSourceMode(SourceMode.Manual, true);

            Assert.Equal("raw.fl", reloaded.SourceFrontLeft);
        }
    }
}
