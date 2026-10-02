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

        private static string CodeBehind() => File.ReadAllText(CodeBehindPath());

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

        [Fact]
        public void The_apply_button_is_not_dirtied_by_anything_that_applies_immediately()
        {
            // OWNER, 2026-09-29, for both the key data controls and the supported-game button: "MAKE
            // SURE THE APPLY BUTTON WILL NOT ENABLED AND THE WARNING FOR THE APPLY BUTTON WILL NOT
            // SHOW". The button means "there is a change here that is not live yet" - these are live
            // before the driver's finger leaves the control, so lighting it would be false and would
            // invite a click that does nothing.
            string code = CodeBehind();

            foreach (string handler in new[] { "private void OnKeyDataValueChanged",
                                               "private void OnKeyDataToggleChanged",
                                               "private void ToggleCurrentGameSupport" })
            {
                int start = code.IndexOf(handler, System.StringComparison.Ordinal);
                Assert.True(start > 0, handler + " not found");
                int end = code.IndexOf("\n        private ", start + 1, System.StringComparison.Ordinal);
                string body = code.Substring(start, end - start);

                Assert.DoesNotContain("MarkDirty()", body);
            }
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
        public void Both_restore_buttons_save_at_once_and_the_source_reset_button_is_gone()
        {
            // "once click the GLOBAL Restore To default, or click the dedicated Restore to default for
            // KeyPoints, will save the changes IMMEDIATELY (As the same as clicking on the apply
            // button)" - ApplySettings is what calls ConfigStore.Save.
            string code = CodeBehind();

            foreach (string method in new[] { "private void RestoreAllDefaults", "private void ResetKeyDataPoints" })
            {
                int start = code.IndexOf(method, System.StringComparison.Ordinal);
                Assert.True(start > 0, method + " not found");
                int end = code.IndexOf("\n        private ", start + 1, System.StringComparison.Ordinal);
                if (end < 0) end = code.Length;
                Assert.Contains("_plugin.ApplySettings()", code.Substring(start, end - start));
            }

            // The per-source "Reset to default" is retired: editing a preset's source text now moves the
            // channel to Custom, so switching back on the dropdown restores it (owner, 2026-09-30).
            Assert.DoesNotContain("LockResetSources", code);
            Assert.DoesNotContain("SlipResetSources", code);
        }

        [Fact]
        public void Custom_is_hidden_by_the_MODE_not_only_by_an_unrecognised_identity()
        {
            // Found by driving the real control - every model test passed without this. A custom
            // configuration can CLASSIFY as one of the presets (most easily by being Raw's own
            // properties, which is where Custom starts from), so the identity check alone would offer it
            // a preset's defaults. Only the page knows which mode the dropdown says.
            string code = CodeBehind();
            int start = code.IndexOf("private bool CanRestoreKeyDataDefaults", System.StringComparison.Ordinal);
            Assert.True(start > 0, "CanRestoreKeyDataDefaults not found");
            int end = code.IndexOf("\n        /// <summary>", start, System.StringComparison.Ordinal);
            string body = code.Substring(start, end - start);

            Assert.Contains("SourceMode.Custom", body);
            Assert.Contains("MatchesDefaults", body);
        }

        [Fact]
        public void A_typed_value_latches_its_slot_SYNCHRONOUSLY_not_on_the_debounce()
        {
            // THE DEFECT THAT MADE KEY POINTS LOOK UNSAVEABLE (owner-reported, 2026-09-30: "Change Key
            // Points number will not be saved, everytime restarted will always go back to default"),
            // reproduced by driving the real control against a simulated frame loop.
            //
            // The 700 ms debounce used to be what wrote the slot - and therefore what latched Seeded.
            // For those 700 ms the slot still looked UNTOUCHED to the engine, and
            // AutoPersistSeededKeyDataPoints runs on the frame loop and fills exactly such a slot with
            // the LEARNED value, latches Seeded itself, and bumps _keyDataRevision. The page sees the
            // new revision, reloads the boxes from the slot, and the driver's number is gone from the
            // screen; the debounce then saved whatever was left in the box - the learned value.
            // Switching Auto off is always followed within a second by typing, so this fired on
            // essentially every attempt.
            //
            // The model write is now synchronous and only the DISK write is debounced.
            string code = CodeBehind();

            int handler = code.IndexOf("private void OnKeyDataValueChanged", System.StringComparison.Ordinal);
            Assert.True(handler > 0, "OnKeyDataValueChanged not found");
            int end = code.IndexOf("\n        /// <summary>", handler, System.StringComparison.Ordinal);
            string body = code.Substring(handler, end - handler);

            int commit = body.IndexOf("CommitKeyDataValues()", System.StringComparison.Ordinal);
            int schedule = body.IndexOf("ScheduleKeyDataPersist()", System.StringComparison.Ordinal);
            Assert.True(commit > 0, "the typed value must be committed to the model synchronously");
            Assert.True(schedule > commit, "the model write must happen BEFORE the debounce is armed");

            // And the debounced path must share that same commit rather than duplicating it, so the two
            // can never disagree about what was written.
            int now = code.IndexOf("private void PersistKeyDataPointsNow", System.StringComparison.Ordinal);
            int nowEnd = code.IndexOf("\n        /// <summary>", now, System.StringComparison.Ordinal);
            Assert.Contains("CommitKeyDataValues()", code.Substring(now, nowEnd - now));
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

        [Fact]
        public void The_game_id_reaches_the_page_even_while_paused_or_in_a_game_menu()
        {
            // OWNER, 2026-09-30: "gameInMenu or GamePaused ... is the very typical scenario when the
            // user adjust the parameters. We need to make sure the adjustment being made during
            // GameInMenu or Paused will be applied and saved."
            //
            // Saving never depended on DataUpdate - the page saves on the WPF dispatcher. What the gate
            // DID break is everything that needs to know WHICH GAME is running, because the page can
            // only learn that from the plugin:
            //   - Per-Game key data has no slot to write to without a game id, so edits were refused;
            //   - Viper support is per title, so a supported game read as unsupported.
            // The id is therefore read and pushed ABOVE the gate, while the gate itself stays exactly
            // as strict for LEARNING, which is all it was ever meant to guard.
            string plugin = File.ReadAllText(Path.Combine(RepoRootDir(), "QAdvanceFeedback", "QAdvanceFeedback.cs"));

            int update = plugin.IndexOf("public void DataUpdate", System.StringComparison.Ordinal);
            Assert.True(update > 0, "DataUpdate not found");
            int gate = plugin.IndexOf("!data.GameRunning || data.GamePaused || data.GameInMenu", update,
                                      System.StringComparison.Ordinal);
            Assert.True(gate > 0, "the learning gate is no longer recognisable - re-check this test");

            int push = plugin.IndexOf("PushCurrentGameToSettingsUi(", update, System.StringComparison.Ordinal);
            Assert.True(push > 0, "the game id is not pushed to the settings page at all");
            Assert.True(push < gate,
                "the game id must be pushed BEFORE the learning gate, or a paused game leaves the "
                + "settings page believing no game is running");

            // The gate must still stop the LEARNED values, which a paused game cannot supply.
            int learnedPush = plugin.IndexOf("PushLearnedKeyDataPointsToSettingsUi(", gate,
                                             System.StringComparison.Ordinal);
            Assert.True(learnedPush > gate, "the 1 Hz learned push must stay behind the gate");

            // A newly opened page must be told immediately rather than waiting for a game CHANGE.
            Assert.Contains("_lastGameIdPushedToUi = null", plugin);

            string code = CodeBehind();
            Assert.Contains("public void UpdateCurrentGame(string gameId)", code);
        }

        [Fact]
        public void Changing_the_game_re_resolves_the_slot_and_the_source_notes()
        {
            // A change of game is a change of Per-Game slot AND of Viper support, so the lightweight
            // push has to redo both - otherwise it would fix the id and leave the page describing the
            // previous title.
            string code = CodeBehind();
            int m = code.IndexOf("public void UpdateCurrentGame(string gameId)", System.StringComparison.Ordinal);
            Assert.True(m > 0);
            int end = code.IndexOf("\n        public void UpdateLearnedKeyDataPoints", m, System.StringComparison.Ordinal);
            string body = code.Substring(m, end - m);

            Assert.Contains("ReloadKeyDataForCurrentContext()", body);
            Assert.Contains("RefreshSourceModeUi(isLock: true)", body);
            Assert.Contains("RefreshSourceModeUi(isLock: false)", body);
        }

        private static string RepoRootDir()
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            return dir.FullName;
        }

        [Fact]
        public void A_shadowed_channels_source_boxes_are_never_saved_over_its_configuration()
        {
            string code = CodeBehind();

            Assert.Contains("bool shadowingRaw", code);
            Assert.Contains("if (!shadowingRaw)", code);

            // The four source assignments must sit INSIDE that guard, not before it.
            int guard = code.IndexOf("if (!shadowingRaw)", System.StringComparison.Ordinal);
            int firstAssign = code.IndexOf("channel.SourceFrontLeft = rows[0]", System.StringComparison.Ordinal);
            Assert.True(guard > 0 && firstAssign > guard,
                "the source assignments must be inside the !shadowingRaw guard");

            // And the REAL shadow state is what gets passed, not a constant.
            Assert.Contains("LockPulseMinValue, _lockShadowing)", code);
            Assert.Contains("SlipPulseMinValue, _slipShadowing)", code);
        }

        [Fact]
        public void Per_game_with_no_game_shows_blank_not_a_misleading_shipped_default()
        {
            string code = CodeBehind();

            int seed = code.IndexOf("private static void SeedChannel", System.StringComparison.Ordinal);
            Assert.True(seed > 0, "SeedChannel not found");

            // The flag is a parameter of SeedChannel itself.
            int param = code.IndexOf("bool perGameWithoutGame,", seed, System.StringComparison.Ordinal);
            Assert.True(param > seed && param - seed < 400,
                "SeedChannel must take the no-slot flag");

            // The bail-out must come BEFORE the shipped-default prefill inside that method.
            int bail = code.IndexOf("if (perGameWithoutGame)", seed, System.StringComparison.Ordinal);
            int prefill = code.IndexOf("TryResolveShippedDefaults", bail, System.StringComparison.Ordinal);
            Assert.True(bail > seed, "SeedChannel must bail out when no slot is selected");
            Assert.True(prefill > bail,
                "the no-slot bail-out must precede the shipped-default prefill");

            // Both channels pass their own real state.
            Assert.Contains("LockKeyDataPerGameToggle.IsChecked == true && string.IsNullOrEmpty(_currentGameId)", code);
            Assert.Contains("SlipKeyDataPerGameToggle.IsChecked == true && string.IsNullOrEmpty(_currentGameId)", code);
        }

        [Fact]
        public void BOTH_driver_write_paths_latch_the_slot_against_the_one_time_seed()
        {
            // Two paths write a driver's key data points: the debounced typing commit
            // (PersistChannelIfManual) and Apply (SaveChannel). The typing path always latched Seeded;
            // Apply wrote seeded:false, which left a slot first created from there UNLATCHED - so
            // AutoPersistSeededKeyDataPoints treated it as never configured, overwrote it with the
            // learned value and persisted that. Same symptom as the debounce race, different doorway:
            // Apply running before the commit had created the slot, or toggling Per-Game onto a
            // brand-new slot key.
            //
            // Only the engine's own one-time seed has any business writing an unlatched slot.
            string code = CodeBehind();

            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(code, @"SetManual\([^)]*seeded:\s*(\w+)\)"))
            {
                Assert.Equal("true", m.Groups[1].Value);
            }

            // And at least the two we know about are present, so the scan above is not vacuous.
            Assert.True(System.Text.RegularExpressions.Regex.Matches(code, @"seeded:\s*true").Count >= 2,
                "expected both driver write paths to latch the slot");
        }

        [Fact]
        public void Going_back_to_Auto_never_overwrites_the_stored_manual_numbers()
        {
            // OWNER, 2026-10-01: "confirm if switching from Manual into Auto again after changed the
            // key points, and the auto session updated the KeyPoint value from learning, it will NOT
            // override the manual KeyPoints settings."
            //
            // Under Auto, SeedChannel refills the three boxes with the LEARNED values every second, so
            // they are a readout, not a configuration. SaveChannel wrote them into the manual slot
            // regardless of Auto, so any Apply pressed while on Auto replaced the driver's stored
            // numbers with whatever had been learned by then. Every other writer already stood down
            // under Auto - PersistChannelIfManual, and the engine's own TrySeedKeyDataSlot - which made
            // this the single remaining way to lose them.
            string code = CodeBehind();

            int save = code.IndexOf("private void SaveChannel(KeyDataPointSettings k", System.StringComparison.Ordinal);
            Assert.True(save > 0, "the key-data SaveChannel overload was not found");
            int end = code.IndexOf("\n        /// <summary>", save, System.StringComparison.Ordinal);
            if (end < 0) end = code.IndexOf("\n        private ", save + 1, System.StringComparison.Ordinal);
            string body = code.Substring(save, end - save);

            int bail = body.IndexOf("if (auto.IsChecked == true) return;", System.StringComparison.Ordinal);
            int write = body.IndexOf("SetManual(", System.StringComparison.Ordinal);
            Assert.True(bail > 0, "SaveChannel must stand down under Auto");
            Assert.True(write > bail, "the Auto bail-out must precede the manual-slot write");

            // The Auto/Per-Game choice itself IS still saved - that is the setting being changed.
            int setAuto = body.IndexOf("SetAutoGenerate(", System.StringComparison.Ordinal);
            Assert.True(setAuto > 0 && setAuto < bail, "the Auto flag must still be persisted");
        }

        [Fact]
        public void No_writer_touches_the_manual_slot_while_Auto_is_on()
        {
            // The complete set, so the guarantee is checkable rather than remembered.
            string code = CodeBehind();
            foreach (string guard in new[]
                     {
                         "k.GetAutoGenerate(sourceIdentity)",   // PersistChannelIfManual
                         "if (auto.IsChecked == true) return;", // SaveChannel
                     })
                Assert.Contains(guard, code);

            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback.sln")))
                dir = dir.Parent;
            string plugin = File.ReadAllText(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"));
            Assert.Contains("keyData.GetAutoGenerate(sourceIdentity)) return false;", plugin);
        }

        [Fact]
        public void The_reflective_sweep_actually_excludes_the_key_data_controls()
        {
            // THE CHECK THAT CAUGHT THE REAL BUG. Removing MarkDirty from the two key data handlers
            // achieved NOTHING on its own: WireDirtyTracking sweeps every named field and hooks
            // MarkDirty to every NumericEditor and ToggleSwitch, so all ten key data controls were
            // re-armed behind the handlers' backs. Verified by driving the real control - the Apply
            // button lit up regardless - not by reading the handlers.
            //
            // The sweep must STAY exhaustive (52 controls were once silently unwired), so the escape is
            // a named exclusion beside the Test Effect one, and this pins it.
            string code = CodeBehind();

            Assert.Contains("IsImmediateApplyControl(field.Name)", code);
            Assert.Contains("private static bool IsImmediateApplyControl", code);

            // The prefix must actually match the controls, or the exclusion is decorative.
            int fn = code.IndexOf("private static bool IsImmediateApplyControl", System.StringComparison.Ordinal);
            int fnEnd = code.IndexOf("\n        private ", fn + 1, System.StringComparison.Ordinal);
            string body = code.Substring(fn, fnEnd - fn);
            Assert.Contains("\"LockKeyData\"", body);
            Assert.Contains("\"SlipKeyData\"", body);

            // And the exclusion must sit INSIDE the sweep loop, next to the Test Effect one.
            int sweep = code.IndexOf("private void WireDirtyTracking", System.StringComparison.Ordinal);
            int sweepEnd = code.IndexOf("\n        private ", sweep + 1, System.StringComparison.Ordinal);
            string sweepBody = code.Substring(sweep, sweepEnd - sweep);
            Assert.Contains("IsTestEffectControl(field.Name)) continue", sweepBody);
            Assert.Contains("IsImmediateApplyControl(field.Name)) continue", sweepBody);
        }

        [Fact]
        public void A_pending_edit_survives_the_settings_page_closing()
        {
            // The 700 ms debounce used to die with the page: type a number, close the tab inside that
            // window, and it was gone - silently, and now with no Apply button to catch it. Forced out
            // on Unloaded instead.
            string code = CodeBehind();
            Assert.Contains("Unloaded += (s, e) => SafeUiAction(FlushKeyDataPersistNow", code);
        }

        [Fact]
        public void Per_game_with_no_game_running_says_so_instead_of_swallowing_the_edit()
        {
            // The slot key is "game:<id>|src:<src>", so with no id there is nowhere real to write and
            // PersistChannelIfManual refuses. Refusing SILENTLY is the trap: with no Apply button there
            // would be no sign at all that the number went nowhere.
            string code = CodeBehind();
            Assert.Contains("perGameWithoutGame", code);
            Assert.Contains("KeyData.PerGame.NoGame", code);

            string dir = Path.Combine(
                Directory.GetParent(Path.GetDirectoryName(CodeBehindPath())).FullName, "Core", "Localization");
            foreach (string table in new[] { "StringTableEn.cs", "StringTableZhHans.cs" })
                Assert.Contains("\"KeyData.PerGame.NoGame\"", File.ReadAllText(Path.Combine(dir, table)));
        }

        [Fact]
        public void A_pending_edit_is_flushed_under_the_flags_it_was_typed_under()
        {
            // OWNER: "FROM MANUAL THEN SWITCH BACK TO AUTO, THE MANUAL NUMBER SHOULD BE SAVED PROPERLY."
            //
            // The toggle has ALREADY changed by the time its event fires, so a flush that reads the
            // toggle would see Auto, stand down, and drop the number. Reading the STORED flag instead
            // still sees Manual at that moment. Hence: flush first, commit the new choice second, and
            // gate the flush on the model rather than on the control.
            string code = CodeBehind();

            int persist = code.IndexOf("private bool PersistChannelIfManual", System.StringComparison.Ordinal);
            int persistEnd = code.IndexOf("\n        /// <summary>", persist, System.StringComparison.Ordinal);
            string persistBody = code.Substring(persist, persistEnd - persist);
            Assert.Contains("k.GetAutoGenerate(sourceIdentity)", persistBody);
            Assert.DoesNotContain("auto.IsChecked", persistBody);

            int toggle = code.IndexOf("private void OnKeyDataToggleChanged", System.StringComparison.Ordinal);
            int toggleEnd = code.IndexOf("\n        /// <summary>", toggle, System.StringComparison.Ordinal);
            string toggleBody = code.Substring(toggle, toggleEnd - toggle);
            int flush = toggleBody.IndexOf("FlushKeyDataPersistNow()", System.StringComparison.Ordinal);
            int commit = toggleBody.IndexOf("CommitKeyDataToggles()", System.StringComparison.Ordinal);
            Assert.True(flush > 0 && commit > flush,
                "the pending edit must be flushed BEFORE the new toggle state is committed");

            // The supported-game button moves both channels to another source, so it flushes too.
            int game = code.IndexOf("private void ToggleCurrentGameSupport", System.StringComparison.Ordinal);
            int gameEnd = code.IndexOf("\n        private ", game + 1, System.StringComparison.Ordinal);
            string gameBody = code.Substring(game, gameEnd - game);
            int gameFlush = gameBody.IndexOf("FlushKeyDataPersistNow()", System.StringComparison.Ordinal);
            int gameWrite = gameBody.IndexOf("SaveViperDocument()", System.StringComparison.Ordinal);
            Assert.True(gameFlush > 0 && gameWrite > gameFlush,
                "the supported-game button must flush the pending edit before switching source");
        }

        [Fact]
        public void Reloading_a_slot_is_strictly_model_to_UI()
        {
            // A source switch must restore THAT source's toggles, not carry the previous source's
            // across - and the reload must never write back, or it would overwrite the setting it is
            // supposed to be displaying. The opposite direction has its own method.
            string code = CodeBehind();

            int reload = code.IndexOf("private void ReloadChannel", System.StringComparison.Ordinal);
            int reloadEnd = code.IndexOf("\n        private ", reload + 1, System.StringComparison.Ordinal);
            string body = code.Substring(reload, reloadEnd - reload);

            Assert.Contains("auto.IsChecked = k.GetAutoGenerate(sourceIdentity)", body);
            Assert.Contains("perGame.IsChecked = k.GetPerGame(sourceIdentity)", body);
            Assert.DoesNotContain("k.SetPerGame", body);
            Assert.DoesNotContain("k.SetAutoGenerate", body);

            Assert.Contains("private void CommitKeyDataToggles", code);
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

        [Fact]
        public void Immediate_changes_reach_disk_immediately()
        {
            // "AND THE NUMBERS BEING SAVED INTO THE CONFIG FILE PROPERLY WITHOUT LOST THE CHANGES IF WE
            // RESTART THE SIMHUB." ApplySettings is what calls ConfigStore.Save, so both the debounced
            // number write and the toggle commit must end in one.
            string code = CodeBehind();

            foreach (string method in new[] { "private void PersistKeyDataPointsNow",
                                              "private void CommitKeyDataToggles" })
            {
                int start = code.IndexOf(method, System.StringComparison.Ordinal);
                Assert.True(start > 0, method + " not found");
                int end = code.IndexOf("\n        /// <summary>", start, System.StringComparison.Ordinal);
                if (end < 0) end = code.IndexOf("\n        private ", start + 1, System.StringComparison.Ordinal);
                Assert.Contains("_plugin.ApplySettings()", code.Substring(start, end - start));
            }
        }
    }
}
