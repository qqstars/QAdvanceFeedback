using System;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The v1.0.7.2 manual key data points. Values are keyed by SLOT = (mode, game, source), so most of
    /// what matters here is which slot a given context selects and what survives a reset. The UI cannot
    /// be exercised headlessly, so these pin the logic behind it.
    /// </summary>
    public class KeyDataPointSettingsTests
    {
        private const string RawLock = "Plain:QAdvanceFeedback.WheelLock.Raw.FrontLeft~Plain:x~Plain:y~Plain:z";
        private const string ShakeItLock = "Plain:ShakeITMotorsV3Plugin.Export.Lock.FrontLeft~Plain:x~Plain:y~Plain:z";

        // ---------------- Which slot a save lands in ----------------

        [Fact]
        public void Global_mode_saves_one_set_per_source_shared_by_every_game()
        {
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = false };
            k.SetManual("F12025", RawLock, 84.0, 74.0, 58.0, seeded: false);

            // Global means the game is not part of the key - a different title reads the same numbers.
            Assert.True(k.TryGetManual("AC", RawLock, out double sMax, out _, out _));
            Assert.Equal(84.0, sMax, 3);
            Assert.True(k.TryGetManual(null, RawLock, out _, out _, out _));
        }

        [Fact]
        public void PerGame_mode_saves_only_the_current_game()
        {
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = true };
            k.SetManual("F12025", RawLock, 80.0, 70.0, 55.0, seeded: false);

            Assert.True(k.TryGetManual("F12025", RawLock, out double sMax, out _, out _));
            Assert.Equal(80.0, sMax, 3);
            Assert.False(k.TryGetManual("AC", RawLock, out _, out _, out _));
        }

        [Fact]
        public void A_different_source_is_a_different_slot()
        {
            // A number that is right for a ShakeIt export is not right for our own Raw - switching the
            // source must never silently reuse the other signal's scale.
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = false };
            k.SetManual("F12025", RawLock, 84.0, 74.0, 58.0, seeded: true);

            Assert.False(k.TryGetManual("F12025", ShakeItLock, out _, out _, out _));
            Assert.False(k.IsSeeded("F12025", ShakeItLock));
            Assert.True(k.IsSeeded("F12025", RawLock));
        }

        [Fact]
        public void Switching_global_to_per_game_and_back_finds_the_global_numbers_untouched()
        {
            // The owner's worked example: manual + global + a source, saved, played; then switch to
            // manual + per-game + the SAME source. The per-game slot is empty (so it seeds on its own),
            // and switching back must return the original global numbers exactly.
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = false };
            k.SetManual("F12025", RawLock, 84.0, 74.0, 58.0, seeded: true);

            k.PerGame = true;
            Assert.False(k.TryGetManual("F12025", RawLock, out _, out _, out _));
            Assert.False(k.IsSeeded("F12025", RawLock));
            k.SetManual("F12025", RawLock, 70.0, 62.0, 49.0, seeded: true);

            k.PerGame = false;
            Assert.True(k.TryGetManual("F12025", RawLock, out double globalSMax, out _, out _));
            Assert.Equal(84.0, globalSMax, 3);

            k.PerGame = true;
            Assert.True(k.TryGetManual("F12025", RawLock, out double perGameSMax, out _, out _));
            Assert.Equal(70.0, perGameSMax, 3);
        }

        // ---------------- The one-time seed, per slot ----------------

        [Fact]
        public void Global_seeding_happens_once_for_a_source_regardless_of_game()
        {
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = false };
            Assert.False(k.IsSeeded("F12025", RawLock));

            k.SetManual("F12025", RawLock, 80.0, 70.0, 55.0, seeded: true);

            Assert.True(k.IsSeeded("F12025", RawLock));
            Assert.True(k.IsSeeded("AC", RawLock));   // global - a new title must NOT re-seed
        }

        [Fact]
        public void PerGame_seeding_happens_again_for_a_game_never_played()
        {
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = true };
            k.SetManual("F12025", RawLock, 80.0, 70.0, 55.0, seeded: true);

            Assert.True(k.IsSeeded("F12025", RawLock));
            Assert.False(k.IsSeeded("AC", RawLock));   // never played - seeds once on first play

            k.SetManual("AC", RawLock, 60.0, 52.0, 40.0, seeded: true);
            Assert.True(k.IsSeeded("AC", RawLock));
            Assert.Equal(2, k.Values.Count);
        }

        [Fact]
        public void Saving_a_selection_without_a_value_leaves_the_slot_unseeded()
        {
            // The unknown-source case: the driver saves manual+global, but there is no default and
            // nothing learned yet. The SELECTION persists; the numbers arrive later via the one-time
            // seed, which must therefore still be pending.
            var k = new KeyDataPointSettings { AutoGenerate = false, PerGame = false };

            Assert.False(k.TryGetManual("F12025", RawLock, out _, out _, out _));
            Assert.False(k.IsSeeded("F12025", RawLock));
        }

        // ---------------- Nothing may discard stored values ----------------

        [Fact]
        public void RestoreDefaults_keeps_every_stored_slot_and_the_mode_switches()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            KeyDataPointSettings k = settings.Lock.KeyDataPoints;
            k.AutoGenerate = false;
            k.PerGame = false;
            k.SetManual(null, RawLock, 88.0, 77.0, 61.0, seeded: true);
            k.PerGame = true;
            k.SetManual("F12025", ShakeItLock, 70.0, 62.0, 49.0, seeded: true);

            settings.RestoreDefaults();
            KeyDataPointSettings after = settings.Lock.KeyDataPoints;

            Assert.False(after.AutoGenerate);
            Assert.True(after.PerGame);
            Assert.Equal(2, after.Values.Count);

            Assert.True(after.TryGetManual("F12025", ShakeItLock, out double perGame, out _, out _));
            Assert.Equal(70.0, perGame, 3);

            after.PerGame = false;
            Assert.True(after.TryGetManual(null, RawLock, out double global, out _, out _));
            Assert.Equal(88.0, global, 3);
        }

        [Fact]
        public void RestoreDefaults_still_resets_everything_it_always_did()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.BrakeThresholdPercent = 42.0;
            settings.Lock.KeyDataPoints.SetManual(null, RawLock, 84.0, 70.0, 55.0, seeded: true);

            settings.RestoreDefaults();

            Assert.NotEqual(42.0, settings.Lock.BrakeThresholdPercent);
            Assert.True(settings.Lock.KeyDataPoints.TryGetManual(null, RawLock, out _, out _, out _));
        }

        [Fact]
        public void No_source_reset_path_touches_the_key_data_points()
        {
            // "any place" - every reset entry point a channel exposes, not just the all-settings Restore.
            foreach (bool isLock in new[] { true, false })
            {
                var channel = isLock
                    ? WheelChannelSettings.CreateLockDefaults()
                    : WheelChannelSettings.CreateSlipDefaults();

                channel.KeyDataPoints.AutoGenerate = false;
                channel.KeyDataPoints.PerGame = true;
                channel.KeyDataPoints.SetManual("F12025", RawLock, 81.0, 71.0, 56.0, seeded: true);

                channel.ResetSourcesToDefault(isLock);
                channel.ApplyMotorsExportDefaults(isLock);
                channel.ResetSourcesForCurrentMode(isLock);

                Assert.False(channel.KeyDataPoints.AutoGenerate);
                Assert.True(channel.KeyDataPoints.PerGame);
                Assert.True(channel.KeyDataPoints.IsSeeded("F12025", RawLock));
                Assert.True(channel.KeyDataPoints.TryGetManual("F12025", RawLock, out double sMax, out _, out _));
                Assert.Equal(81.0, sMax, 3);
            }
        }

        // ---------------- Validation and derivation ----------------

        [Theory]
        [InlineData(80.0, 70.0, 55.0, true)]
        [InlineData(80.0, 80.0, 80.0, true)]
        [InlineData(70.0, 80.0, 55.0, false)]
        [InlineData(80.0, 55.0, 70.0, false)]
        [InlineData(0.0, 0.0, 0.0, false)]
        [InlineData(101.0, 70.0, 55.0, false)]
        [InlineData(-5.0, -6.0, -7.0, false)]
        public void IsValid_requires_ordering_and_range(double sMax, double s90, double s75, bool expected)
            => Assert.Equal(expected, KeyDataPointSettings.IsValid(sMax, s90, s75));

        [Fact]
        public void Derived_lower_anchors_stay_ordered_so_switching_pattern_cannot_corrupt_them()
        {
            KeyDataPointSettings.DeriveLowerAnchors(80.0, out double s90, out double s75);

            Assert.Equal(72.0, s90, 3);
            Assert.Equal(56.0, s75, 3);
            Assert.True(KeyDataPointSettings.IsValid(80.0, s90, s75));
        }

        [Fact]
        public void An_unknown_source_gets_no_shipped_default()
        {
            Assert.False(KeyDataPointSettings.TryResolveDefaults(
                KnownFeedbackSource.Unknown, isLockChannel: true, out _, out _, out _));

            Assert.True(KeyDataPointSettings.TryResolveDefaults(
                KnownFeedbackSource.QAdvanceFeedbackRaw, isLockChannel: true,
                out double sMax, out double s90, out double s75));
            Assert.Equal(85.0, sMax, 3);
            Assert.Equal(75.0, s90, 3);
            Assert.Equal(60.0, s75, 3);

            Assert.True(KeyDataPointSettings.TryResolveDefaults(
                KnownFeedbackSource.ShakeItMotorsExport, isLockChannel: false, out double slipSMax, out _, out _));
            Assert.Equal(75.0, slipSMax, 3);
        }

        // ---------------- Which sources count as "shipped" ----------------

        [Fact]
        public void Only_the_two_exact_shipped_configurations_count_as_known()
        {
            foreach (bool isLock in new[] { true, false })
            {
                string channel = isLock ? "WheelLock" : "WheelSlip";

                string raw = SourceIdentity.Compute(
                    "QAdvanceFeedback." + channel + ".Raw.FrontLeft", "Plain",
                    "QAdvanceFeedback." + channel + ".Raw.FrontRight", "Plain",
                    "QAdvanceFeedback." + channel + ".Raw.RearLeft", "Plain",
                    "QAdvanceFeedback." + channel + ".Raw.RearRight", "Plain");
                Assert.True(KeyDataPointSettings.IsExactShippedSource(raw, isLock));

                string shakeIt = SourceIdentity.Compute(
                    "ShakeITMotorsV3Plugin.Export." + channel + ".IRacing.FrontLeft", "Plain",
                    "ShakeITMotorsV3Plugin.Export." + channel + ".IRacing.FrontRight", "Plain",
                    "ShakeITMotorsV3Plugin.Export." + channel + ".IRacing.RearLeft", "Plain",
                    "ShakeITMotorsV3Plugin.Export." + channel + ".IRacing.RearRight", "Plain");
                Assert.True(KeyDataPointSettings.IsExactShippedSource(shakeIt, isLock));
            }
        }

        [Fact]
        public void Both_channels_resolve_their_own_shipped_defaults_and_an_empty_identity_resolves_nothing()
        {
            // WHY BOTH CHANNELS TOGETHER: a settings page bug had Wheel Slip showing "---" where its 75
            // belonged while Wheel Lock showed 85/75/60 from the same shipped Raw configuration. The cause
            // was upstream (Slip's identity was computed before its source boxes were filled, so it hashed
            // four EMPTY strings), but the asymmetry is worth pinning here so this layer can be ruled out
            // immediately next time - and so is the empty-identity case that the caller was accidentally
            // passing, which must resolve to nothing rather than to some channel's defaults.
            var defaults = KeyDataPointDefaults.CreateShipped();

            double sMax, s90, s75;
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                ShippedRawFor(isLock: true), true, defaults, out sMax, out s90, out s75));
            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, sMax, 3);

            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                ShippedRawFor(isLock: false), false, defaults, out sMax, out s90, out s75));
            Assert.Equal(KeyDataPointSettings.SlipDefaultSMax, sMax, 3);

            // Four blank source boxes - what the page hashed before the ordering fix.
            string blank = SourceIdentity.Compute("", "Plain", "", "Plain", "", "Plain", "", "Plain");
            Assert.False(KeyDataPointSettings.TryResolveShippedDefaults(blank, true, defaults, out _, out _, out _));
            Assert.False(KeyDataPointSettings.TryResolveShippedDefaults(blank, false, defaults, out _, out _, out _));
        }

        private static string ShippedRawFor(bool isLock)
        {
            string channel = isLock ? "WheelLock" : "WheelSlip";
            return SourceIdentity.Compute(
                "QAdvanceFeedback." + channel + ".Raw.FrontLeft", "Plain",
                "QAdvanceFeedback." + channel + ".Raw.FrontRight", "Plain",
                "QAdvanceFeedback." + channel + ".Raw.RearLeft", "Plain",
                "QAdvanceFeedback." + channel + ".Raw.RearRight", "Plain");
        }

        [Fact]
        public void A_driver_named_ShakeIt_export_is_NOT_a_shipped_source()
        {
            // The looser Classify() would call this ShakeIt, because the plugin name is in the string.
            // For shipped DEFAULTS that would be a guess: nobody has measured this effect s range.
            string mine = SourceIdentity.Compute(
                "ShakeITMotorsV3Plugin.Export.WheelLock.MyOwn.FrontLeft", "Plain",
                "ShakeITMotorsV3Plugin.Export.WheelLock.MyOwn.FrontRight", "Plain",
                "ShakeITMotorsV3Plugin.Export.WheelLock.MyOwn.RearLeft", "Plain",
                "ShakeITMotorsV3Plugin.Export.WheelLock.MyOwn.RearRight", "Plain");

            Assert.False(KeyDataPointSettings.IsExactShippedSource(mine, isLockChannel: true));
            Assert.False(KeyDataPointSettings.TryResolveShippedDefaults(mine, true, KeyDataPointDefaults.CreateShipped(), out _, out _, out _));
        }

        [Fact]
        public void A_scripted_source_is_NOT_a_shipped_source()
        {
            // NCalc/JavaScript sources are hashed by SourceIdentity, so they can never match either
            // shipped identity - which is exactly the intent.
            string scripted = SourceIdentity.Compute(
                "QAdvanceFeedback.WheelLock.Raw.FrontLeft * 1.1", "NCalc",
                "QAdvanceFeedback.WheelLock.Raw.FrontRight * 1.1", "NCalc",
                "QAdvanceFeedback.WheelLock.Raw.RearLeft * 1.1", "NCalc",
                "QAdvanceFeedback.WheelLock.Raw.RearRight * 1.1", "NCalc");

            Assert.False(KeyDataPointSettings.IsExactShippedSource(scripted, isLockChannel: true));
        }

        [Fact]
        public void A_partly_rewired_source_is_NOT_a_shipped_source()
        {
            // Three shipped wheels and one pointed elsewhere is not the shipped configuration.
            string mixed = SourceIdentity.Compute(
                "QAdvanceFeedback.WheelLock.Raw.FrontLeft", "Plain",
                "QAdvanceFeedback.WheelLock.Raw.FrontRight", "Plain",
                "QAdvanceFeedback.WheelLock.Raw.RearLeft", "Plain",
                "SomeOtherPlugin.Whatever.RearRight", "Plain");

            Assert.False(KeyDataPointSettings.IsExactShippedSource(mixed, isLockChannel: true));
        }

        // ---------------- Persisted learned values, with no game running ----------------

        [Fact]
        public void A_persisted_ceiling_is_found_by_source_without_knowing_the_game_or_car()
        {
            // The settings page has no (game, car) key while SimHub sits on the menu, but the parameters
            // file still holds everything learned last time. Without this lookup the page would show a
            // shipped default over its own evidence.
            var learner = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 400; i++)
                learner.ObserveAtPhysicalLimit("F12025", "Haas", "SourceA", 72.0);

            double? found = learner.PersistedCeilingForSource("SourceA");

            Assert.True(found.HasValue);
            Assert.InRange(found.Value, 65.0, 80.0);
        }

        [Fact]
        public void A_source_with_nothing_learned_reports_nothing()
        {
            var learner = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 400; i++)
                learner.ObserveAtPhysicalLimit("F12025", "Haas", "SourceA", 72.0);

            // A different source must not inherit SourceA's evidence - that is the whole point of keying
            // by source.
            Assert.False(learner.PersistedCeilingForSource("SourceB").HasValue);
            Assert.False(learner.PersistedCeilingForSource(null).HasValue);
            Assert.False(learner.PersistedCeilingForSource("").HasValue);
        }

        [Fact]
        public void The_highest_ceiling_wins_across_cars_on_the_same_source()
        {
            // Ceilings, not averages: a car that has seen a harder lock has simply seen more of the
            // source's range than one that has not.
            var learner = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 400; i++)
            {
                learner.ObserveAtPhysicalLimit("F12025", "Haas", "SourceA", 60.0);
                learner.ObserveAtPhysicalLimit("F12025", "RedBull", "SourceA", 80.0);
            }

            double? found = learner.PersistedCeilingForSource("SourceA");

            Assert.True(found.HasValue);
            Assert.True(found.Value > 70.0);
        }

        [Fact]
        public void A_persisted_ceiling_survives_a_restart_and_is_still_found_by_source()
        {
            // THE CASE THE FEATURE EXISTS FOR, and the one a first version silently failed: SimHub has
            // just started, no game is running, and everything the plugin knows came out of the
            // parameters file. The at-limit DISTRIBUTIONS are session-scoped - ImportAll restores only
            // the resulting ceiling - so a lookup that searched the distributions alone found nothing
            // exactly here, while passing happily in the same session where the value was learned.
            var learned = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 400; i++)
                learned.ObserveAtPhysicalLimit("F12025", "Haas", "SourceA", 72.0);

            var afterRestart = new KeyedScaleLearner(isLockChannel: false);
            afterRestart.ImportAll(learned.ExportAll());

            double? found = afterRestart.PersistedCeilingForSource("SourceA");

            Assert.True(found.HasValue, "a restored ceiling must still be findable by source alone");
            Assert.InRange(found.Value, 50.0, 100.0);
            Assert.False(afterRestart.PersistedCeilingForSource("SourceB").HasValue);
        }

        // ---------------- Configured per-source defaults ----------------

        [Fact]
        public void The_defaults_section_round_trips_through_the_config_file()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.KeyDataPointDefaults.LockShakeIt = new KeyDataPointDefaultSet(78.0, 70.0, 55.0);

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(json);

            Assert.Equal(78.0, restored.KeyDataPointDefaults.LockShakeIt.SMax, 3);
            Assert.Equal(70.0, restored.KeyDataPointDefaults.LockShakeIt.S90, 3);
            Assert.Equal(55.0, restored.KeyDataPointDefaults.LockShakeIt.S75, 3);

            // Retuning ShakeIt must not disturb Raw - that is the point of separate entries.
            Assert.Equal(KeyDataPointSettings.LockDefaultSMax, restored.KeyDataPointDefaults.LockRaw.SMax, 3);
        }

        [Fact]
        public void The_defaults_section_is_written_near_the_top_of_the_file()
        {
            // It is the block a driver is most likely to want to find and edit, so it must not be buried
            // under two channels' worth of settings.
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(QAdvanceFeedbackSettings.CreateDefault());

            int defaults = json.IndexOf("KeyDataPointDefaults", StringComparison.Ordinal);
            int lockChannel = json.IndexOf("\"Lock\"", StringComparison.Ordinal);

            Assert.True(defaults >= 0 && lockChannel >= 0);
            Assert.True(defaults < lockChannel, "the defaults block should precede the channel settings");
        }

        [Fact]
        public void A_configured_default_is_used_instead_of_the_built_in_one()
        {
            var defaults = KeyDataPointDefaults.CreateShipped();
            defaults.LockRaw = new KeyDataPointDefaultSet(70.0, 62.0, 48.0);

            Assert.True(defaults.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, isLockChannel: true,
                out double sMax, out double s90, out double s75));
            Assert.Equal(70.0, sMax, 3);
            Assert.Equal(62.0, s90, 3);
            Assert.Equal(48.0, s75, 3);
        }

        [Fact]
        public void An_unusable_configured_default_falls_back_to_the_built_in_one()
        {
            // Hand-edited nonsense must never reach the output - out of order, out of range, or blank.
            foreach (KeyDataPointDefaultSet bad in new[]
            {
                new KeyDataPointDefaultSet(50.0, 60.0, 70.0),   // out of order
                new KeyDataPointDefaultSet(0.0, 0.0, 0.0),      // blank
                new KeyDataPointDefaultSet(500.0, 60.0, 40.0),  // out of range
            })
            {
                var defaults = KeyDataPointDefaults.CreateShipped();
                defaults.LockRaw = bad;

                Assert.True(defaults.TryResolve(KnownFeedbackSource.QAdvanceFeedbackRaw, isLockChannel: true,
                    out double sMax, out _, out _));
                Assert.Equal(KeyDataPointSettings.LockDefaultSMax, sMax, 3);
            }
        }

        [Fact]
        public void An_unrecognised_source_still_has_no_default_however_the_section_is_edited()
        {
            var defaults = KeyDataPointDefaults.CreateShipped();

            Assert.False(defaults.TryResolve(KnownFeedbackSource.Unknown, isLockChannel: true,
                out _, out _, out _));
        }

        [Fact]
        public void RestoreDefaults_keeps_a_retuned_defaults_section()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.KeyDataPointDefaults.SlipRaw = new KeyDataPointDefaultSet(68.0, 61.0, 47.0);

            settings.RestoreDefaults();

            Assert.Equal(68.0, settings.KeyDataPointDefaults.SlipRaw.SMax, 3);
        }

        // ---------------- The gate ----------------

        [Fact]
        public void Gate_requires_BOTH_driving_time_and_cold_start_completion()
        {
            var gate = new ManualOverrideGate();
            const string key = "game|car|source";

            Assert.False(gate.IsReady(key, 1.0));

            for (int i = 0; i < 40 * 60; i++) gate.Observe(key, 1.0 / 60.0, advancing: true);
            Assert.True(gate.ElapsedSeconds(key) >= ManualOverrideGate.MinimumInGameSeconds);

            Assert.False(gate.IsReady(key, 0.10));
            Assert.True(gate.IsReady(key, 1.0));
        }

        [Fact]
        public void A_paused_game_accumulates_nothing()
        {
            var gate = new ManualOverrideGate();
            const string key = "game|car|source";

            for (int i = 0; i < 60 * 60; i++) gate.Observe(key, 1.0 / 60.0, advancing: false);

            Assert.Equal(0.0, gate.ElapsedSeconds(key), 6);
            Assert.False(gate.IsReady(key, 1.0));
        }

        [Fact]
        public void One_enormous_frame_cannot_satisfy_the_gate()
        {
            var gate = new ManualOverrideGate();
            const string key = "game|car|source";

            gate.Observe(key, 3600.0, advancing: true);

            Assert.True(gate.ElapsedSeconds(key) < ManualOverrideGate.MinimumInGameSeconds);
            Assert.False(gate.IsReady(key, 1.0));
        }

        [Fact]
        public void Each_context_earns_its_own_readiness()
        {
            var gate = new ManualOverrideGate();
            for (int i = 0; i < 40 * 60; i++) gate.Observe("f1|haas|raw", 1.0 / 60.0, advancing: true);

            Assert.True(gate.IsReady("f1|haas|raw", 1.0));
            Assert.False(gate.IsReady("f1|redbull|raw", 1.0));
        }
    }
}
