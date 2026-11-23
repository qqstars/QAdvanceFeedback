using System.IO;
using QAdvanceFeedback;
using QAdvanceFeedback.Settings;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// AUTO/MANUAL AND GLOBAL/PER-GAME BELONG TO THE SOURCE, NOT THE CHANNEL (owner, 2026-09-29:
    /// "using Raw might using AutoGenerate, but Viper using manual, which is an exactly valid
    /// scenario").
    /// <para/>
    /// Raw is a signal this plugin derives and can learn well; a plugin-supplied source on a known
    /// 0-1 scale may be better pinned by hand. One flag for the whole channel forced the two to agree,
    /// and switching source silently re-applied the other source's choice.
    /// <para/>
    /// Also covers the two "applies immediately, so it must not pretend otherwise" rules for the same
    /// controls - see <see cref="The_apply_button_is_not_dirtied_by_anything_that_applies_immediately"/>.
    /// </summary>
    public class PerSourceKeyDataFlagsTests
    {
        private readonly ITestOutputHelper _out;
        public PerSourceKeyDataFlagsTests(ITestOutputHelper output) { _out = output; }

        private const string Raw = "QAdvanceFeedback.WheelLock.Raw.FrontLeft~...";
        private const string Viper = "NCalc:deadbeef~NCalc:cafef00d";

        private static string CodeBehindPath()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings")))
                dir = dir.Parent;
            return Path.Combine(dir.FullName, "QAdvanceFeedback", "Settings", "SettingsControl.xaml.cs");
        }


        /// <summary>The key data rules shared by the settings page and the ShakeIt panels.</summary>
        private static string SharedKeyDataModel() =>
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(CodeBehindPath()), "KeyDataPanelModel.cs"));

        [Fact]
        public void Raw_can_stay_on_Auto_while_Viper_is_on_Manual()
        {
            // The owner's own scenario, end to end.
            var k = new KeyDataPointSettings();
            k.SetAutoGenerate(Raw, true);
            k.SetAutoGenerate(Viper, false);

            Assert.True(k.GetAutoGenerate(Raw));
            Assert.False(k.GetAutoGenerate(Viper));
        }

        [Fact]
        public void Per_game_is_per_source_too_and_it_moves_the_slot_key()
        {
            // Per-Game is part of the slot KEY, so making it per-source is what lets one source keep a
            // set per title while another keeps one shared set - and the numbers must follow.
            var k = new KeyDataPointSettings();
            k.SetPerGame(Raw, false);
            k.SetPerGame(Viper, true);

            k.SetManual("F1 25", Raw, 85.0, 75.0, 60.0, seeded: false);
            k.SetManual("F1 25", Viper, 15.0, 13.5, 10.5, seeded: false);

            // Raw is global, so the same numbers come back under a DIFFERENT game.
            Assert.True(k.TryGetManual("AssettoCorsa", Raw, out double rawSMax, out _, out _));
            Assert.Equal(85.0, rawSMax, 6);

            // Viper is per-game, so another title has nothing stored.
            Assert.False(k.TryGetManual("AssettoCorsa", Viper, out _, out _, out _));
            Assert.True(k.TryGetManual("F1 25", Viper, out double viperSMax, out _, out _));
            Assert.Equal(15.0, viperSMax, 6);
        }

        [Fact]
        public void An_old_settings_file_keeps_behaving_exactly_as_it_did()
        {
            // THE MIGRATION, and the reason the two channel-wide fields were kept rather than deleted.
            // A file written before this change has only AutoGenerate/PerGame; every source must inherit
            // them, so nobody's calibration changes underneath them on upgrade.
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = true };

            Assert.False(k.GetAutoGenerate(Raw));
            Assert.False(k.GetAutoGenerate(Viper));
            Assert.True(k.GetPerGame(Raw));
            Assert.True(k.GetPerGame(Viper));

            // And the first source the driver touches inherits the old channel-wide choice rather than
            // snapping back to the type's own defaults (which would be Auto=true, PerGame=false).
            k.SetPerGame(Viper, false);
            Assert.False(k.GetAutoGenerate(Viper));      // still the inherited Manual, not reset to Auto
            Assert.False(k.GetPerGame(Viper));
            Assert.True(k.GetPerGame(Raw));              // untouched source still inherits
        }

        [Fact]
        public void A_channel_with_no_source_yet_does_not_create_a_shared_phantom_entry()
        {
            // Before the page resolves an identity the source is "", and every unnamed source would
            // otherwise share one entry - so a write with no identity goes nowhere rather than into a
            // bucket that later leaks into a real source.
            var k = new KeyDataPointSettings { AutoGenerate = true };
            k.SetAutoGenerate(null, false);
            k.SetAutoGenerate(string.Empty, false);

            Assert.Empty(k.SourceFlags);
            Assert.True(k.GetAutoGenerate(Raw));
        }

        [Fact]
        public void The_flags_survive_a_clone()
        {
            var k = new KeyDataPointSettings();
            k.SetAutoGenerate(Viper, false);
            k.SetPerGame(Viper, true);

            KeyDataPointSettings copy = k.Clone();
            Assert.False(copy.GetAutoGenerate(Viper));
            Assert.True(copy.GetPerGame(Viper));

            // A DEEP copy - mutating the clone must not reach back into the original.
            copy.SetAutoGenerate(Viper, true);
            Assert.False(k.GetAutoGenerate(Viper));
        }

        [Fact]
        public void The_engine_reads_the_flags_per_source_not_per_channel()
        {
            // The half that actually changes the output: ResolveManualAnchors and TrySeedKeyDataSlot
            // both have the source identity in hand and must use it, or a channel whose Raw is on Auto
            // would publish learned values while reading Viper on Manual.
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs")))
                dir = dir.Parent;
            string plugin = File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"));

            Assert.DoesNotContain("keyData.AutoGenerate", plugin);
            Assert.DoesNotContain("keyData.PerGame", plugin);
            Assert.Contains("keyData.GetAutoGenerate(sourceIdentity)", plugin);
            Assert.Contains("keyData.GetPerGame(sourceIdentity)", plugin);
        }

        // ---- the dedicated "restore key points to default" button (owner, 2026-09-30) ----

        private static KeyDataPointDefaults Shipped() => KeyDataPointDefaults.CreateShipped();

        private static string RawIdentity(bool isLock) => RawSourceFallback.RawIdentity(isLock);

        [Fact]
        public void A_shipped_source_with_nothing_configured_matches_the_defaults()
        {
            // The button is hidden in this state - there is nothing to undo.
            var k = new KeyDataPointSettings();
            Assert.True(k.MatchesDefaults(RawIdentity(true), true, Shipped()));
        }

        [Fact]
        public void Each_of_the_three_settings_on_its_own_is_enough_to_show_the_button()
        {
            // "ONLY if the key points settings (include the auto/manual, per games, and the numbers)
            // are different with the default, then will show the button" - so all three count, and any
            // one of them alone is a difference.
            string raw = RawIdentity(true);

            var byAuto = new KeyDataPointSettings();
            byAuto.SetAutoGenerate(raw, false);
            Assert.False(byAuto.MatchesDefaults(raw, true, Shipped()));

            var byPerGame = new KeyDataPointSettings();
            byPerGame.SetPerGame(raw, true);
            Assert.False(byPerGame.MatchesDefaults(raw, true, Shipped()));

            var byNumbers = new KeyDataPointSettings();
            byNumbers.SetManual("F12025", raw, 42.0, 37.0, 31.0, seeded: true);
            Assert.False(byNumbers.MatchesDefaults(raw, true, Shipped()));
        }

        [Fact]
        public void Numbers_that_happen_to_equal_the_shipped_triple_still_count_as_default()
        {
            // Otherwise the button would appear for a driver who typed the shipped figures by hand, and
            // clicking it would visibly change nothing.
            string raw = RawIdentity(true);
            var k = new KeyDataPointSettings();
            k.SetManual(null, raw,
                KeyDataPointSettings.LockDefaultSMax,
                KeyDataPointSettings.LockDefaultS90,
                KeyDataPointSettings.LockDefaultS75, seeded: true);

            Assert.True(k.MatchesDefaults(raw, true, Shipped()));
        }

        [Fact]
        public void The_custom_source_never_offers_the_button_because_it_has_no_default()
        {
            // The owner's own rule: "For the custom source, as there is no 'default', so it will always
            // not show the button." It falls out of MatchesDefaults rather than being special-cased at
            // the call site.
            const string custom = "NCalc:aaaaaaaa~NCalc:bbbbbbbb~NCalc:cccccccc~NCalc:dddddddd";
            var k = new KeyDataPointSettings();
            k.SetAutoGenerate(custom, false);
            k.SetPerGame(custom, true);
            k.SetManual("F12025", custom, 42.0, 37.0, 31.0, seeded: true);

            Assert.True(k.MatchesDefaults(custom, true, Shipped()));
        }

        [Fact]
        public void Restoring_clears_the_flags_and_every_stored_number_for_that_source_only()
        {
            string raw = RawIdentity(true);
            const string viper = "NCalc:1111~NCalc:2222~NCalc:3333~NCalc:4444";

            var k = new KeyDataPointSettings();
            k.SetAutoGenerate(raw, false);
            k.SetPerGame(raw, true);
            k.SetManual("F12025", raw, 42.0, 37.0, 31.0, seeded: true);
            k.SetManual("EAWRC23", raw, 44.0, 39.0, 33.0, seeded: true);
            k.SetAutoGenerate(viper, false);
            k.SetManual("F12025", viper, 15.0, 13.5, 10.5, seeded: true);

            k.RestoreSourceDefaults(raw);

            Assert.True(k.GetAutoGenerate(raw));
            Assert.False(k.GetPerGame(raw));
            Assert.False(k.TryGetManual("F12025", raw, out _, out _, out _));
            Assert.False(k.TryGetManual("EAWRC23", raw, out _, out _, out _));
            Assert.True(k.MatchesDefaults(raw, true, Shipped()));

            // THE OTHER SOURCE IS UNTOUCHED - the button lives under one channel's key points and
            // resets the source being looked at, not the channel's whole history.
            Assert.False(k.GetAutoGenerate(viper));
            Assert.True(k.TryGetManual("F12025", viper, out double viperSMax, out _, out _));
            Assert.Equal(15.0, viperSMax, 6);
        }

        [Fact]
        public void The_global_restore_still_keeps_key_points_and_the_dedicated_button_is_why()
        {
            // The owner confirmed the global Restore must NOT wipe key points (their v1.0.7.2 rule) and
            // asked for a dedicated control instead - so both halves are pinned together here. If
            // someone later makes the global button wipe them, this fails and points at the decision.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            string raw = RawIdentity(true);
            settings.Lock.KeyDataPoints.SetAutoGenerate(raw, false);
            settings.Lock.KeyDataPoints.SetManual(null, raw, 42.0, 37.0, 31.0, seeded: true);

            settings.RestoreDefaults();

            Assert.False(settings.Lock.KeyDataPoints.GetAutoGenerate(raw));
            Assert.True(settings.Lock.KeyDataPoints.TryGetManual(null, raw, out double sMax, out _, out _));
            Assert.Equal(42.0, sMax, 6);
        }

        [Fact]
        public void A_driver_written_slot_stops_the_engines_one_time_seed()
        {
            // The mechanism the fix above relies on: the seed stands down on a slot that reports Seeded,
            // and the settings page writes with seeded:true. TrySeedKeyDataSlot's own guard is
            // `if (keyData.IsSeeded(gameId, sourceIdentity)) return false;`.
            var k = new KeyDataPointSettings();
            string raw = RawSourceFallback.RawIdentity(true);
            k.SetAutoGenerate(raw, false);

            Assert.False(k.IsSeeded("F12025", raw));
            k.SetManual("F12025", raw, 15.0, 13.5, 10.5, seeded: true);
            Assert.True(k.IsSeeded("F12025", raw));

            // A plain value write must NOT clear an existing latch either.
            k.SetManual("F12025", raw, 16.0, 14.4, 11.2, seeded: false);
            Assert.True(k.IsSeeded("F12025", raw));
        }

        private static string RepoRootDir()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            return dir.FullName;
        }

        [Fact]
        public void The_per_source_flags_survive_the_config_file()
        {
            // "WITHOUT LOST THE CHANGES IF WE RESTART THE SIMHUB" - saving immediately is only half of
            // it; the new dictionary has to come back off disk too. ConfigStore uses Newtonsoft with no
            // attributes, so this is the same round trip the real file takes.
            var original = new KeyDataPointSettings();
            original.SetAutoGenerate(Raw, true);
            original.SetAutoGenerate(Viper, false);
            original.SetPerGame(Viper, true);
            original.SetManual("F1 25", Viper, 15.0, 13.5, 10.5, seeded: true);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(original, Newtonsoft.Json.Formatting.Indented);
            _out.WriteLine(json);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<KeyDataPointSettings>(json);

            Assert.True(restored.GetAutoGenerate(Raw));
            Assert.False(restored.GetAutoGenerate(Viper));
            Assert.True(restored.GetPerGame(Viper));
            Assert.True(restored.TryGetManual("F1 25", Viper, out double sMax, out _, out _));
            Assert.Equal(15.0, sMax, 6);
            Assert.True(restored.IsSeeded("F1 25", Viper));
        }

        [Fact]
        public void A_file_written_by_this_build_still_loads_on_the_previous_one()
        {
            // The two channel-wide fields are still written, so a downgrade reads a coherent file
            // rather than losing both settings to a property it does not know.
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = true };
            k.SetAutoGenerate(Viper, true);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(k);
            Assert.Contains("\"AutoGenerate\":false", json);
            Assert.Contains("\"PerGame\":true", json);
            Assert.Contains("SourceFlags", json);
        }

    }
}
