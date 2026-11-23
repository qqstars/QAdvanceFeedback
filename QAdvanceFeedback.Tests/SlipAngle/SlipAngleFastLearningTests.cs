using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// FAST SLIP ANGLE LEARNING (owner, 2026-11-21; docs\slip-angle-fast-learning-report.md): before the peak is
    /// confirmed every crossing teaches a weighted Max from the first corner on - the owner's tiers against the speed
    /// band's peak, 10% for a young band and for the key's first crossing, matured bands only at 85% of their mean -
    /// with the slide guard (body angle 10 degrees, or 90% of full slip on a ratio source).
    /// </summary>
    public class SlipAngleFastLearningTests
    {
        private const double Dt = 1.0 / 60.0;
        private const double Peak = 3.0;   // the band's reference lateral G in these tests

        private static SlipAngleEarlyContext Ctx(double g, bool young = false, bool matured = false, double mean = 0.0,
            bool sliding = false, double axle = 0.0, bool ratio = false, double car = 6.0) =>
            new SlipAngleEarlyContext(g, Peak, mean > 0.0 ? mean : Peak, matured, young, sliding, axle, ratio, car);

        /// <summary>Alpha rises 0.12 deg/frame; lateral G holds at <paramref name="gHold"/> until alpha reaches
        /// <paramref name="target"/>, then falls - a crossing whose snapshot is <paramref name="target"/> - then a
        /// 1.2 s exit at low alpha and G.</summary>
        private static void Crossing(Action<double, double> frame, double target, double gHold)
        {
            for (int i = 0; i <= 20; i++)
            {
                double a = target - 1.2 + 0.12 * i;
                double g = i <= 10 ? gHold : gHold * (1.0 - 0.01 * (i - 10));
                frame(a, g);
            }
            for (int i = 0; i < 72; i++) frame(1.0, 0.2);
        }

        private static void Teach(SlipAngleEarlyEstimator e, double target, double gHold, double startMax = 10.0,
            bool young = false, bool matured = false, double mean = 0.0, bool sliding = false, double axle = 0.0, bool ratio = false,
            double car = 6.0) =>
            Crossing((a, g) => e.Observe(a, Ctx(g, young, matured, mean, sliding, axle, ratio, car), startMax, Dt), target, gHold);

        // ---------------- the weights ----------------

        [Theory]
        [InlineData(1.20, 1.0)]
        [InlineData(0.85, 1.0)]
        [InlineData(0.80, 0.8)]
        [InlineData(0.70, 0.6)]
        [InlineData(0.60, 0.3)]
        [InlineData(0.50, 0.1)]
        [InlineData(double.NaN, 0.1)]
        public void The_owners_tiers_weigh_a_crossing_by_its_share_of_the_band_peak(double share, double weight)
        {
            Assert.Equal(weight, SlipAngleEarlyEstimator.TierWeight(share));
        }

        [Fact]
        public void The_first_crossing_counts_10_percent_and_moves_max_a_tenth_of_the_way()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 4.0, Peak);   // a full-weight corner by its share, but the key's first
            Assert.Equal(1, e.Crossings);
            Assert.Equal(0.1, e.Weight, 6);
            Assert.InRange(e.Max, (10.0 + 0.1 * 4.0) / 1.1 - 0.05, (10.0 + 0.1 * 4.0) / 1.1 + 0.05);
        }

        [Fact]
        public void Later_full_share_corners_count_fully_and_max_is_the_weighted_mean_with_the_start()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 6.0, Peak);
            Teach(e, 6.0, Peak);
            Teach(e, 6.0, Peak);
            Assert.Equal(2.1, e.Weight, 6);   // 0.1 + 1 + 1
            Assert.InRange(e.Max, (10.0 + 2.1 * 6.0) / 3.1 - 0.1, (10.0 + 2.1 * 6.0) / 3.1 + 0.1);
            Assert.Equal(0.21, e.Evidence, 6);   // full at 10 corners' worth
            Assert.Equal(0.0, e.Stability);       // Max still moving fast (9.6 -> 7.9 -> 7.3): not stable...
            Assert.Equal(0.21, e.Confidence, 6);  // ...so the last steady value is used (owner, 2026-11-22), never a drop to 0
        }

        // ---------------- the indicator's confidence: evidence x stability ----------------

        [Fact]
        public void Confidence_needs_ten_corners_worth_and_a_steady_max_then_latches_while_learning_continues()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            for (int i = 0; i < 9; i++) Teach(e, 6.0, Peak);
            Assert.True(e.Evidence < 1.0);   // 0.1 + 8 = 8.1 corners' worth: not full yet
            Assert.True(e.Confidence < 0.99);
            for (int i = 0; i < 30 && !e.Matured; i++) Teach(e, 6.0, Peak);
            Assert.True(e.Matured);
            Assert.Equal(1.0, e.Confidence);

            double before = e.Max;
            Teach(e, 9.0, Peak);              // still learning after maturity, a small step
            Assert.True(e.Max > before && e.Max - before < 0.1 * (9.0 - before));
            Assert.Equal(1.0, e.Confidence); // the latch holds
        }

        [Fact]
        public void A_moving_max_lowers_confidence_even_with_plenty_of_evidence()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 6.0, Peak);
            for (int i = 0; i < 4; i++) { Teach(e, 3.0, Peak); Teach(e, 14.0, Peak); }   // corners far apart
            Assert.True(e.Evidence < 1.0 || e.Stability < 1.0);
            Assert.True(e.Confidence < 0.99);
            Assert.False(e.Matured);
        }

        [Fact]
        public void The_maturity_latch_and_recent_max_survive_a_restart()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            for (int i = 0; i < 40 && !e.Matured; i++) Teach(e, 6.0, Peak);
            Assert.True(e.Matured);
            var state = new SlipAngleLearnerState();
            e.Export(state);
            var back = new SlipAngleEarlyEstimator(20.0);
            back.Import(state);
            Assert.True(back.Matured);
            Assert.Equal(1.0, back.Confidence);
            Assert.Equal(e.Stability, back.Stability, 9);
        }

        [Fact]
        public void A_young_band_weighs_10_percent_and_a_light_corner_less_than_a_limit_one()
        {
            var young = new SlipAngleEarlyEstimator(20.0);
            Teach(young, 6.0, Peak);
            Teach(young, 6.0, Peak, young: true);
            Assert.Equal(0.2, young.Weight, 6);

            var light = new SlipAngleEarlyEstimator(20.0);
            Teach(light, 6.0, Peak);
            Teach(light, 6.0, 0.62 * Peak);   // 62% of the band peak: 30%
            Assert.Equal(0.4, light.Weight, 6);
        }

        [Fact]
        public void Once_the_band_is_matured_only_corners_at_85_percent_of_its_mean_count()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 6.0, 2.0, matured: true, mean: 2.0);
            Teach(e, 6.0, 2.0, matured: true, mean: 2.0);   // at the mean: full weight
            Teach(e, 6.0, 1.4, matured: true, mean: 2.0);   // 70%: ignored
            Assert.Equal(1.1, e.Weight, 6);
        }

        // ---------------- the slide guard ----------------

        [Fact]
        public void A_crossing_while_sliding_is_ignored()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 15.0, Peak, sliding: true);
            Assert.Equal(0, e.Crossings);
            Assert.Equal(1, e.Guarded);
            Assert.False(e.HasEstimate);
        }

        /// <summary>THE CAR-ANGLE MINIMUM (owner, 2026-11-22): a slip-ratio crossing teaches only with the car angle at 5 degrees
        /// or more - FH6's flat-out sweepers (car barely rotating, G peaking from the road, not the tyres) are dropped; no
        /// upper car-angle limit for a slip ratio (the 13.5-degree ratio guard caps it); an angle source is unchanged.</summary>
        [Fact]
        public void A_slip_ratio_crossing_teaches_only_with_the_car_angle_at_5_degrees_or_more()
        {
            Assert.Equal(5.0, SlipAngleEarlyEstimator.RatioMinCarAngleDeg);

            var sweeper = new SlipAngleEarlyEstimator(22.5);
            Teach(sweeper, 4.0, Peak, ratio: true, car: 2.5);
            Assert.Equal(0, sweeper.Crossings);
            Assert.Equal(1, sweeper.Guarded);

            var atLimit = new SlipAngleEarlyEstimator(22.5);
            Teach(atLimit, 10.5, Peak, ratio: true, car: 5.0);
            Assert.Equal(1, atLimit.Crossings);

            var bigCarAngle = new SlipAngleEarlyEstimator(22.5);
            Teach(bigCarAngle, 11.0, Peak, ratio: true, car: 14.0, sliding: true);   // no slide guard for a slip ratio
            Assert.Equal(1, bigCarAngle.Crossings);

            var angleSource = new SlipAngleEarlyEstimator(20.0);
            Teach(angleSource, 6.0, Peak, ratio: false, car: 1.0);                    // an angle source: no minimum
            Assert.Equal(1, angleSource.Crossings);
            var angleSlide = new SlipAngleEarlyEstimator(20.0);
            Teach(angleSlide, 6.0, Peak, ratio: false, sliding: true);                // ...and its slide guard stays
            Assert.Equal(1, angleSlide.Guarded);
        }

        [Fact]
        public void On_a_ratio_source_90_percent_of_15_degrees_on_either_axle_is_a_slide()
        {
            // Owner, 2026-11-22: 90% of 15 degrees = 13.5 (ratio 1.35) - was 90% of full slip, which threw away FH6's limit corners.
            Assert.Equal(13.5, SlipAngleEarlyEstimator.RatioGuardDeg, 6);

            var byAngle = new SlipAngleEarlyEstimator(22.5);
            Teach(byAngle, 14.0, Peak, ratio: true);             // the crossing itself past 13.5
            Assert.Equal(1, byAngle.Guarded);

            var underIt = new SlipAngleEarlyEstimator(22.5);
            Teach(underIt, 11.0, Peak, ratio: true);             // FH6's limit (ratio 1.1) is taught
            Assert.Equal(1, underIt.Crossings);

            var byAxle = new SlipAngleEarlyEstimator(20.0);
            Teach(byAxle, 9.0, Peak, axle: 25.0, ratio: true);   // the other axle far past full slip
            Assert.Equal(1, byAxle.Guarded);

            var angleSource = new SlipAngleEarlyEstimator(20.0);
            Teach(angleSource, 9.0, Peak, axle: 25.0, ratio: false);   // degrees / radians: no full-slip rule
            Assert.Equal(1, angleSource.Crossings);
        }

        // ---------------- bounds and persistence ----------------

        [Fact]
        public void The_weight_is_renormalised_so_nothing_overflows_and_new_corners_keep_counting()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            for (int i = 0; i < 230; i++) Teach(e, 6.0, Peak);
            Assert.InRange(e.Weight, 1.0, SlipAngleEarlyEstimator.WeightWindow);
            for (int i = 0; i < 300; i++) Teach(e, 8.0, Peak);
            Assert.InRange(e.Max, 7.2, 8.0);   // later corners pulled it most of the way
        }

        [Fact]
        public void The_estimate_survives_a_restart_and_bad_saved_values_start_fresh()
        {
            var e = new SlipAngleEarlyEstimator(20.0);
            Teach(e, 6.0, Peak);
            Teach(e, 6.0, Peak);
            var state = new SlipAngleLearnerState();
            e.Export(state);

            var back = new SlipAngleEarlyEstimator(20.0);
            back.Import(state);
            Assert.Equal(e.Weight, back.Weight);
            Assert.Equal(e.Max, back.Max, 9);
            Assert.Equal(2, back.Crossings);

            state.EarlyWeightedAngle = double.NaN;
            var bad = new SlipAngleEarlyEstimator(20.0);
            bad.Import(state);
            Assert.False(bad.HasEstimate);
        }

        // ---------------- in the window learner ----------------

        private static SlipAngleWindowLearner Learner() => new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault, SlipAngleWindowLearner.MaxCapNativeDeg);

        private static readonly ImpactVerdict Ok = new ImpactVerdict(true, false, false, 0, 0);

        private static void TeachLearner(SlipAngleWindowLearner l, double target, double gHold, bool sliding = false) =>
            Crossing((a, g) => l.Observe(a, g, Ok, true, Dt, Ctx(g, sliding: sliding)), target, gHold);

        [Fact]
        public void Before_confirmation_the_published_window_moves_from_the_first_corner_and_keeps_its_shape()
        {
            SlipAngleWindowLearner l = Learner();
            Assert.Equal(10.0, l.Published.Max, 6);
            TeachLearner(l, 6.0, Peak);
            TeachLearner(l, 6.0, Peak);

            Assert.False(l.PeakConfirmed);
            SlipAngleWindow w = l.Published;
            Assert.True(w.Max < 10.0 && w.Max > 6.0, $"Max {w.Max}");
            Assert.Equal(SlipAngleWindow.ShippedDefault.Best / SlipAngleWindow.ShippedDefault.Max, w.Best / w.Max, 6);
            Assert.Equal(SlipAngleWindow.ShippedDefault.Min / SlipAngleWindow.ShippedDefault.Max, w.Min / w.Max, 6);
            Assert.InRange(l.MaxMaturity, 0.0, 0.99);   // two corners: far from settled
        }

        [Fact]
        public void Without_the_context_the_learner_behaves_exactly_as_before()
        {
            SlipAngleWindowLearner l = Learner();
            Crossing((a, g) => l.Observe(a, g, Ok, true, Dt), 6.0, Peak);
            Assert.Equal(0, l.Early.Crossings);
            Assert.Equal(SlipAngleWindow.ShippedDefault.Max, l.Published.Max, 6);
        }

        [Fact]
        public void The_learner_saves_and_restores_the_fast_estimate()
        {
            SlipAngleWindowLearner l = Learner();
            TeachLearner(l, 6.0, Peak);
            TeachLearner(l, 6.0, Peak);
            var back = Learner();
            back.Import(l.Export());
            Assert.Equal(l.Published.Max, back.Published.Max, 9);
            Assert.Equal(l.MaxMaturity, back.MaxMaturity, 9);
        }

        [Fact]
        public void A_slide_teaches_neither_the_fast_estimate_nor_the_confirmed_path()
        {
            SlipAngleWindowLearner l = Learner();
            TeachLearner(l, 15.0, Peak, sliding: true);
            Assert.Equal(0, l.Early.Crossings);
            Assert.Equal(0, l.CrossingCount);
            Assert.Equal(10.0, l.Published.Max, 6);
        }

        // ---------------- the speed band reference ----------------

        [Fact]
        public void A_band_is_young_until_2_seconds_of_driving_and_the_age_is_saved()
        {
            var bands = new SlipAngleSpeedBands();
            for (int i = 0; i < 60; i++) bands.Observe("g", "c", "s", SurfaceBucket.Sealed, 150.0, 2.0, 2.5, true, false, Dt);   // 1 s
            Assert.True(bands.TryReference("g", "c", "s", SurfaceBucket.Sealed, 150.0, out double peak, out _, out bool matured, out bool young));
            Assert.True(peak > 0.0);
            Assert.False(matured);
            Assert.True(young);

            for (int i = 0; i < 90; i++) bands.Observe("g", "c", "s", SurfaceBucket.Sealed, 150.0, 2.0, 2.5, true, false, Dt);   // 2.5 s in all
            bands.TryReference("g", "c", "s", SurfaceBucket.Sealed, 150.0, out _, out _, out _, out young);
            Assert.False(young);

            var back = new SlipAngleSpeedBands();
            back.Import(bands.Export());
            back.TryReference("g", "c", "s", SurfaceBucket.Sealed, 150.0, out _, out _, out _, out bool youngAfterRestart);
            Assert.False(youngAfterRestart);
        }

        [Fact]
        public void A_band_with_no_data_has_no_reference_and_counts_as_young()
        {
            var bands = new SlipAngleSpeedBands();
            Assert.True(bands.TryReference("g", "c", "s", SurfaceBucket.Sealed, 150.0, out double peak, out _, out _, out bool young));
            Assert.Equal(0.0, peak);
            Assert.True(young);
            Assert.False(bands.TryReference("g", "c", "s", SurfaceBucket.Sealed, 10.0, out _, out _, out _, out _));   // below the bands
        }
    }
}
