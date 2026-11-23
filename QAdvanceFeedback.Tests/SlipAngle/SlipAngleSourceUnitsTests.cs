using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Slip Angle source units (owner, 2026-11-20): radians by default, the ByAngle / ratio lists in
    /// the config, the body-angle check that moves a game to its list, the 20% ratio scale with its 30-degree
    /// cap, the drift start for ratio sources and the two warnings.</summary>
    public class SlipAngleSourceUnitsTests
    {
        private const double Dt = 1.0 / 60.0;

        // ---------------- scale ----------------

        [Theory]
        [InlineData(SlipAngleUnit.Radians, 0.1, 5.729578)]
        [InlineData(SlipAngleUnit.Degrees, 7.5, 7.5)]
        // Owner, 2026-11-22: 10 ratio points per degree (was 5) - full slip is 10 degrees; reading cap 1.5 x 22.5 = 33.75.
        [InlineData(SlipAngleUnit.Ratio1, 0.5, 5.0)]      // 50% -> 5 degrees
        [InlineData(SlipAngleUnit.Ratio1, 1.0, 10.0)]     // full slip -> 10
        [InlineData(SlipAngleUnit.Ratio1, 2.25, 22.5)]    // the Max cap
        [InlineData(SlipAngleUnit.Ratio1, 4.0, 33.75)]    // capped at 337.5% = 33.75
        [InlineData(SlipAngleUnit.Ratio1, -4.0, -33.75)]  // sign kept
        [InlineData(SlipAngleUnit.Ratio100, 50.0, 5.0)]
        [InlineData(SlipAngleUnit.Ratio100, 150.0, 15.0)]
        [InlineData(SlipAngleUnit.Ratio100, 400.0, 33.75)]
        public void Each_unit_converts_to_degrees(SlipAngleUnit unit, double value, double degrees) =>
            Assert.Equal(degrees, SlipAngleSourceScale.ToDegrees(value, unit), 5);

        [Fact]
        public void Full_slip_is_10_degrees_the_max_cap_22_5_and_the_guard_13_5()
        {
            Assert.Equal(10.0, SlipAngleSourceScale.FullScaleDeg, 9);
            Assert.Equal(22.5, SlipAngleSourceScale.MaxCapDeg, 9);
            Assert.Equal(22.5, SlipAngleWindowLearner.MaxCapDegFor(SlipAngleFrameProcessor.NativeRatioIdentity), 9);
            Assert.Equal(20.0, SlipAngleWindowLearner.MaxCapDegFor(SlipAngleFrameProcessor.NativeIdentity), 9);
            Assert.Equal(33.75, SlipAngleSourceScale.CapDeg, 9);
            Assert.Equal(13.5, SlipAngleEarlyEstimator.RatioGuardDeg, 9);
            Assert.Equal(SlipAngleSourceScale.ToDegrees(1.0, SlipAngleUnit.Ratio1), SlipAngleNativeSupport.ToDegrees(1.0, SlipAngleUnit.Ratio1), 9);
        }

        // ---------------- lists ----------------

        [Fact]
        public void Defaults_list_forza_as_a_0_to_1_ratio_that_goes_over_and_kartkraft_as_degrees()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            foreach (string forza in new[] { "FM7", "FM8", "FH4", "FH5", "FH6" })
            {
                Assert.Equal(SlipAngleUnit.Ratio1, u.Resolve(forza, SlipAngleUnit.Unverified));
                Assert.Equal(true, u.ExceedsFullScale(forza));
            }
            Assert.Equal(SlipAngleUnit.Degrees, u.Resolve("KartKraft", SlipAngleUnit.Unverified));
            Assert.Empty(u.AngleUsingRatio100);
        }

        [Fact]
        public void An_unlisted_game_reads_radians_and_a_listed_game_reads_its_list_even_over_the_table()
        {
            // The lists are authoritative (owner, 2026-11-21): an unlisted game keeps the table's unit (else radians);
            // a game in a list is read as the list says.
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.Equal(SlipAngleUnit.Radians, u.Resolve("F12025", SlipAngleUnit.Unverified));
            Assert.Equal(SlipAngleUnit.Radians, u.Resolve("LFS", SlipAngleUnit.Radians));
            Assert.True(u.Assign("LFS", SlipAngleUnit.Ratio1));
            Assert.Equal(SlipAngleUnit.Ratio1, u.Resolve("LFS", SlipAngleUnit.Radians));
        }

        [Fact]
        public void Missing_lists_read_as_the_defaults_until_seeded()
        {
            var u = new SlipAngleSourceUnits();
            Assert.Equal(SlipAngleUnit.Ratio1, u.Resolve("FH6", SlipAngleUnit.Unverified));
            Assert.Equal(SlipAngleUnit.Degrees, u.Resolve("kartkraft", SlipAngleUnit.Unverified));
            Assert.True(u.Normalise());
            Assert.False(u.Normalise());
        }

        [Fact]
        public void Assign_moves_a_game_between_lists_including_the_checked_radians_list()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.True(u.Assign("Rennsport", SlipAngleUnit.Ratio100));
            Assert.Equal(SlipAngleUnit.Ratio100, u.Resolve("Rennsport", SlipAngleUnit.Unverified));
            Assert.False(u.Assign("Rennsport", SlipAngleUnit.Ratio100));
            Assert.True(u.Assign("Rennsport", SlipAngleUnit.Degrees));
            Assert.Equal(SlipAngleUnit.Degrees, u.Resolve("Rennsport", SlipAngleUnit.Unverified));
            Assert.DoesNotContain(u.AngleUsingRatio100, e => e.Game == "Rennsport");
            // Radians is a list of its own now (owner, 2026-11-21): the check is finished for that game.
            Assert.True(u.Assign("Rennsport", SlipAngleUnit.Radians));
            Assert.True(u.IsListed("Rennsport"));
            Assert.Contains("Rennsport", u.AngleUsingRadians);
            Assert.DoesNotContain("Rennsport", u.ByAngle);
            Assert.Equal(SlipAngleUnit.Radians, u.Resolve("Rennsport", SlipAngleUnit.Unverified));
            // Unverified takes it out of every list: it is checked again.
            Assert.True(u.Assign("Rennsport", SlipAngleUnit.Unverified));
            Assert.False(u.IsListed("Rennsport"));
        }

        [Fact]
        public void The_over_100_flag_is_recorded_on_the_ratio_entry()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            u.Assign("WRC", SlipAngleUnit.Ratio100);
            Assert.Null(u.ExceedsFullScale("WRC"));
            Assert.True(u.SetExceedsFullScale("WRC", false));
            Assert.Equal(false, u.ExceedsFullScale("WRC"));
            Assert.False(u.SetExceedsFullScale("WRC", false));
            Assert.False(u.SetExceedsFullScale("F12025", true));   // not a ratio game
        }

        [Fact]
        public void A_config_without_the_lists_is_seeded_once_and_a_saved_config_never_duplicates_them()
        {
            var fresh = JsonConvert.DeserializeObject<SlipAngleChannelSettings>("{}");
            Assert.True(fresh.EnsureSourceUnits());
            Assert.False(fresh.EnsureSourceUnits());

            string json = JsonConvert.SerializeObject(fresh);
            var back = JsonConvert.DeserializeObject<SlipAngleChannelSettings>(json);
            Assert.False(back.EnsureSourceUnits());
            Assert.Equal(5, back.SourceUnits.AngleUsingRatio1.Count);
            Assert.Single(back.SourceUnits.ByAngle);
            Assert.Contains("AngleUsingRatio100", json);
        }

        [Fact]
        public void Restore_defaults_keeps_the_learned_unit_lists()
        {
            QAdvanceFeedbackSettings s = QAdvanceFeedbackSettings.CreateDefault();
            s.SlipAngle.EnsureSourceUnits();
            s.SlipAngle.SourceUnits.Assign("Rennsport", SlipAngleUnit.Ratio100);
            s.RestoreDefaults();
            Assert.Equal(SlipAngleUnit.Ratio100, s.SlipAngle.SourceUnits.Resolve("Rennsport", SlipAngleUnit.Unverified));
        }

        // ---------------- body-angle check ----------------

        // Body angle 2 degrees (30 m/s forward, 1.0477 m/s sideways); 3 episodes of 2 s at 1 g.
        private static IEnumerable<(double latG, double vlat)> Corners3()
        {
            for (int e = 0; e < 3; e++)
            {
                for (int i = 0; i < 120; i++) yield return (1.0, 1.0477);
                for (int i = 0; i < 20; i++) yield return (0.2, 0.0);
            }
        }

        [Theory]
        [InlineData(0.0663, SlipAngleUnit.Radians)]    // r ~ 1.9 (F1 25)
        [InlineData(0.279, SlipAngleUnit.Ratio1)]      // r ~ 8 (FH6)
        [InlineData(3.80, SlipAngleUnit.Degrees)]      // r ~ 109
        [InlineData(27.9, SlipAngleUnit.Ratio100)]     // r ~ 800
        public void The_reading_against_the_body_angle_tells_the_four_units_apart(double raw, SlipAngleUnit expected)
        {
            var c = new SlipAngleUnitClassifier();
            foreach (var (latG, vlat) in Corners3())
                c.Observe("G", new Corners(raw, raw, raw, raw), latG, vlat, 30.0, 108.0, Dt);
            Assert.True(c.TryGetVerdict("G", out SlipAngleUnit unit));
            Assert.Equal(expected, unit);
        }

        [Fact]
        public void One_long_corner_is_not_enough_evidence()
        {
            var c = new SlipAngleUnitClassifier();
            for (int i = 0; i < 600; i++) c.Observe("G", new Corners(0.279, 0.279, 0.279, 0.279), 1.0, 1.0477, 30.0, 108.0, Dt);
            Assert.False(c.TryGetVerdict("G", out _));
        }

        [Fact]
        public void Slides_straights_and_impacts_do_not_count()
        {
            var c = new SlipAngleUnitClassifier();
            for (int e = 0; e < 6; e++)
            {
                for (int i = 0; i < 120; i++) c.Observe("G", new Corners(3, 3, 3, 3), 1.0, 15.0, 30.0, 108.0, Dt);   // 26.6 deg body: a slide
                for (int i = 0; i < 20; i++) c.Observe("G", new Corners(3, 3, 3, 3), 0.2, 0.0, 30.0, 108.0, Dt);
            }
            Assert.True(c.TryGetEvidence("G", out double seconds, out _, out _));
            Assert.Equal(0.0, seconds, 9);
        }

        [Theory]
        [InlineData(3.9, SlipAngleUnit.Radians)]
        [InlineData(4.1, SlipAngleUnit.Ratio1)]
        [InlineData(28.5, SlipAngleUnit.Degrees)]
        [InlineData(230.0, SlipAngleUnit.Ratio100)]
        public void Band_edges(double r, SlipAngleUnit expected) => Assert.Equal(expected, SlipAngleUnitClassifier.Classify(r));

        // ---------------- over 100% ----------------

        [Fact]
        public void A_ratio_seen_above_100_goes_over()
        {
            var t = new SlipAngleRatioRangeTracker();
            t.Observe("G", new Corners(0.5, 1.2, 0.3, 0.3), SlipAngleUnit.Ratio1, 100);
            Assert.Equal(true, t.Exceeds("G"));
        }

        [Fact]
        public void Three_separate_reaches_of_100_without_going_over_mean_it_stops_at_100()
        {
            var t = new SlipAngleRatioRangeTracker();
            for (int e = 0; e < 3; e++)
            {
                Assert.NotEqual(false, t.Exceeds("G"));
                t.Observe("G", new Corners(100, 100, 40, 40), SlipAngleUnit.Ratio100, 100);
                t.Observe("G", new Corners(50, 50, 40, 40), SlipAngleUnit.Ratio100, 100);
            }
            Assert.Equal(false, t.Exceeds("G"));
            t.Observe("G", new Corners(101, 50, 40, 40), SlipAngleUnit.Ratio100, 100);
            Assert.Equal(true, t.Exceeds("G"));
        }

        // ---------------- drift start ----------------

        private static readonly SlipAngleWindow Max20 = new SlipAngleWindow(10, 16, 20);

        [Fact]
        public void A_ratio_that_goes_over_keeps_its_normal_drift_start_under_the_reading_cap()
        {
            // Owner, 2026-11-22: the reading cap is 33.75 degrees (1.5 x the 22.5 Max cap), so even Max 20's start (30) is reachable.
            double nu = SlipAngleDriftStart.RatioNu(Max20, true, out bool saturating);
            Assert.False(saturating);
            Assert.Equal(SlipAngleDriftStart.Nu(Max20), nu, 6);
            Assert.True(SlipAngleDriftStart.AngleDeg(Max20.Max) < SlipAngleSourceScale.CapDeg);
            // A smaller Max keeps the normal start (1.5 x 10 = 15 degrees).
            var max10 = new SlipAngleWindow(5, 8, 10);
            Assert.Equal(SlipAngleDriftStart.Nu(max10), SlipAngleDriftStart.RatioNu(max10, null, out _), 6);
        }

        [Fact]
        public void A_ratio_that_stops_at_100_starts_drift_at_100_with_the_longer_hold()
        {
            double nu = SlipAngleDriftStart.RatioNu(Max20, false, out bool saturating);
            Assert.True(saturating);
            Assert.Equal(Math.Max(DriftAttenuator.MinDriftStart, SlipAngleNormalizer.NormalizeUnclamped(9.8, Max20)), nu, 6);   // 98% of full slip (10 degrees), never under the minimum start
            Assert.True(nu >= DriftAttenuator.MinDriftStart && nu < 80.0);
            // Max 6: 1.5 x 6 = 9 degrees is under full slip, so the normal start and hold stay.
            SlipAngleDriftStart.RatioNu(new SlipAngleWindow(3, 4.8, 6), false, out bool small);
            Assert.False(small);
            Assert.Equal(1.5, SlipAngleDriftStart.SaturatingHoldSeconds(0.25), 9);
            Assert.Equal(2.0, SlipAngleDriftStart.SaturatingHoldSeconds(0.5), 9);
        }

        private static DriftState Hold(DriftAttenuator d, DriftSettings s, double nU, double start, double seconds)
        {
            for (double t = 0; t < seconds; t += Dt) d.Step(nU, 100, Dt, s, start);
            return d.State;
        }

        [Fact]
        public void A_value_held_at_the_cap_drifts_with_max_20()
        {
            double start = SlipAngleDriftStart.RatioNu(Max20, true, out _);
            double atCap = SlipAngleNormalizer.NormalizeUnclamped(SlipAngleSourceScale.CapDeg, Max20);
            Assert.Equal(DriftState.Drifting, Hold(new DriftAttenuator(), new DriftSettings(), atCap, start, 1.0));
        }

        [Fact]
        public void A_value_held_at_full_slip_drifts_after_the_longer_hold_with_max_20()
        {
            double start = SlipAngleDriftStart.RatioNu(Max20, false, out _);
            double atFull = SlipAngleNormalizer.NormalizeUnclamped(20.0, Max20);   // 80: the Max mark
            DriftSettings s = new DriftSettings().WithHoldSeconds(SlipAngleDriftStart.SaturatingHoldSeconds(0.25));
            var d = new DriftAttenuator();
            Assert.Equal(DriftState.Normal, Hold(d, s, atFull, start, 1.3));
            Assert.Equal(DriftState.Drifting, Hold(d, s, atFull, start, 0.7));
        }

        // ---------------- reader ----------------

        private static string P(string relative) => SlipAngleRawSourceTable.FullPropertyName(relative);

        private static SlipAngleInput Feed(SlipAngleRawSourceReader reader, string game, double raw)
        {
            var values = new Dictionary<string, object>();
            for (int i = 0; i < 4; i++) values[P($"Physics.slipAngle{i + 1:00}")] = (float)raw;
            SlipAngleInput input = null;
            foreach (var (latG, vlat) in Corners3())
                input = reader.Read(game, n => values.TryGetValue(n, out object v) ? v : null,
                    new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: latG, lateralLocalVelocity: vlat, forwardLocalVelocity: 30.0), Dt);
            return input;
        }

        [Fact]
        public void An_unlisted_game_whose_values_are_a_ratio_moves_to_its_list_and_is_saved()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { Units = () => units };
            int saves = 0;
            reader.UnitsChanged += () => saves++;
            SlipAngleInput input = Feed(reader, "ACC", 0.279);
            Assert.Equal(SlipAngleUnit.Ratio1, units.Resolve("ACC", SlipAngleUnit.Unverified));
            Assert.Equal(SlipAngleUnit.Ratio1, input.Unit);
            Assert.Equal(0.279f * 100 * 0.1, input.NativeWheelDeg.Value.FrontLeft, 3);
            Assert.True(saves >= 1);
        }

        [Fact]
        public void An_unlisted_game_checked_as_radians_moves_to_the_radians_list_and_is_saved()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { Units = () => units };
            int saves = 0;
            reader.UnitsChanged += () => saves++;
            SlipAngleInput input = Feed(reader, "ACC", 0.0663);
            Assert.True(units.IsListed("ACC"));
            Assert.Contains("ACC", units.AngleUsingRadians);
            Assert.Equal(SlipAngleUnit.Radians, input.Unit);
            Assert.Equal(1, saves);
            // The heading now names it, and the check is over.
            Assert.Equal("SlipAngle.KeyData.Header.Radians",
                QAdvanceFeedback.Settings.SettingsHub.SlipAngleKeyDataHeaderKey(QAdvanceFeedback.Settings.SourceMode.Manual, SourceNativeness.Native, "ACC", units));
        }

        [Fact]
        public void A_game_the_user_lists_as_radians_is_never_checked()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            units.AngleUsingRadians.Add("ACC");
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { Units = () => units };
            int saves = 0;
            reader.UnitsChanged += () => saves++;
            SlipAngleInput input = Feed(reader, "ACC", 0.0663 * 57.29578);   // degree-sized values: still read as listed
            Assert.Equal(SlipAngleUnit.Radians, input.Unit);
            Assert.Equal(0, saves);
            Assert.False(reader.UnitClassifier.TryGetVerdict("ACC", out _));
        }

        [Fact]
        public void A_unit_the_table_states_is_respected_and_never_checked()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { Units = () => units };
            Assert.Equal(SlipAngleUnit.Radians, units.Resolve("LFS", SlipAngleUnit.Radians));
            Assert.False(units.IsListed("LFS"));
            Assert.False(reader.UnitClassifier.TryGetVerdict("LFS", out _));   // never checked
            Assert.True(QAdvanceFeedback.Settings.SettingsHub.SlipAngleUnitIsHardCoded("LFS"));   // no dropdown either
        }

        [Fact]
        public void The_radians_list_is_in_the_config_file_and_round_trips()
        {
            var settings = new QAdvanceFeedback.Settings.QAdvanceFeedbackSettings();
            settings.SlipAngle.EnsureSourceUnits();
            settings.SlipAngle.SourceUnits.AngleUsingRadians.Add("MyGame");
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            Assert.Contains("\"AngleUsingRadians\"", json);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedback.Settings.QAdvanceFeedbackSettings>(json);
            back.SlipAngle.EnsureSourceUnits();
            Assert.Equal(new[] { "MyGame" }, back.SlipAngle.SourceUnits.AngleUsingRadians);
        }

        [Fact]
        public void An_older_config_without_the_radians_list_gets_it_seeded_empty()
        {
            var units = Newtonsoft.Json.JsonConvert.DeserializeObject<SlipAngleSourceUnits>("{\"ByAngle\":[\"KartKraft\"]}");
            Assert.Null(units.AngleUsingRadians);
            Assert.True(units.Normalise());
            Assert.Empty(units.AngleUsingRadians);
        }

        [Fact]
        public void A_forza_game_reads_its_ratio_from_the_first_frame()
        {
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport());
            var values = new Dictionary<string, object>
            {
                [P("TireSlipAngleFrontLeft")] = 0.5f, [P("TireSlipAngleFrontRight")] = 0.5f,
                [P("TireSlipAngleRearLeft")] = 0.5f, [P("TireSlipAngleRearRight")] = 0.5f,
            };
            SlipAngleInput input = reader.Read("FH6", n => values.TryGetValue(n, out object v) ? v : null,
                new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: 0.5), Dt);
            Assert.Equal(SlipAngleUnit.Ratio1, input.Unit);
            Assert.Equal(5.0, input.NativeWheelDeg.Value.FrontLeft, 4);   // ratio 0.5 = 5 degrees (10 points per degree)
            Assert.Equal(true, input.RatioExceedsFullScale);
        }

        // ---------------- learning key and warnings ----------------

        [Fact]
        public void A_ratio_game_learns_under_its_own_key_everywhere()
        {
            Assert.Equal(SlipAngleFrameProcessor.NativeRatioIdentity, SlipAngleFrameProcessor.NativeIdentityFor(SlipAngleUnit.Ratio1));
            Assert.Equal(SlipAngleFrameProcessor.NativeIdentity, SlipAngleFrameProcessor.NativeIdentityFor(SlipAngleUnit.Radians));
            var ch = new SlipAngleChannelSettings();
            Assert.Equal(SlipAngleFrameProcessor.NativeRatioIdentity, SettingsHub.SlipAngleIdentity(ch, SourceNativeness.Native, "FH6"));
            Assert.Equal(SlipAngleFrameProcessor.NativeIdentity, SettingsHub.SlipAngleIdentity(ch, SourceNativeness.Native, "F12025"));
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, SettingsHub.SlipAngleIdentity(ch, SourceNativeness.Fallback, "FH6"));
        }

        private static SlipAngleStatus Status(SourceNativeness tier, SlipAngleUnit unit) =>
            new SlipAngleStatus(tier, true, true, Max20, Max20, 1.0, ColdStartTier.Tier1, false, false, "G", "id", unit: unit);

        [Fact]
        public void The_ratio_and_car_angle_warnings_have_their_own_short_texts()
        {
            var ratio = new SlipAngleLiveText(Status(SourceNativeness.Native, SlipAngleUnit.Ratio1));
            Assert.True(ratio.ShowFallbackWarning);
            Assert.Equal("Source is a slip ratio (0 → 100 = grip → full slip). Angle = value ÷ 10 (full slip = 10°).", ratio.FallbackWarningText);

            var car = new SlipAngleLiveText(Status(SourceNativeness.Fallback, SlipAngleUnit.Radians));
            Assert.True(car.ShowFallbackWarning);
            Assert.Equal("This game has no tyre slip angle. Using the car's body angle.", car.FallbackWarningText);

            Assert.False(new SlipAngleLiveText(Status(SourceNativeness.Native, SlipAngleUnit.Radians)).ShowFallbackWarning);
        }
    }
}
