using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="OnlineDistributionLearner"/> - QAdvanceFeedback's own substitute for
    /// SimHub's unreachable internal <c>ICalibrationData</c> state (see that class's own remarks for
    /// why it exists and the replay evidence behind it,
    /// docs\branch-dispatch-and-source-keyed-learning-report.md).
    /// </summary>
    public class OnlineDistributionLearnerTests
    {
        [Fact]
        public void GetAverage_is_null_before_any_observation()
        {
            var learner = new OnlineDistributionLearner();
            Assert.Null(learner.GetAverage());
        }

        /// <summary>
        /// RE-EXPRESSED, not weakened (docs\regression-fix-report.md - the sample-threshold follow-up):
        /// <see cref="OnlineDistributionLearner.GetAverage"/> used to be a PLAIN, equally-weighted mean of
        /// every |value| ever folded in. It is now a DECAYING WEIGHTED mean (see that class's own
        /// remarks) so a caller's estimate keeps tracking genuine change instead of becoming permanently
        /// diluted by an ever-growing history - deliberately requested so <see cref="Normalized.KeyedScaleLearner"/>'s
        /// primary tier "keeps refining indefinitely" rather than locking in place. For a CONSTANT input
        /// series this is mathematically IDENTICAL to the old plain mean (verified by
        /// <see cref="A_constant_input_series_gives_the_exact_same_average_regardless_of_decay"/>); for a
        /// series whose values genuinely differ - as here - the most RECENT value is weighted very
        /// slightly more than older ones (decay 0.997/sample - the same rate
        /// <see cref="Normalized.GripLearner"/>'s own decaying peak uses), so the result is close to, but
        /// not exactly, the old plain mean of 4.0. The exact value below was captured directly from this
        /// implementation, not hand-derived, and is pinned so a future accidental change to the decay
        /// rate or formula is caught.
        /// </summary>
        [Fact]
        public void GetAverage_is_a_decaying_weighted_mean_slightly_favouring_recent_values()
        {
            var learner = new OnlineDistributionLearner();
            learner.AddValue(2.0);
            learner.AddValue(-4.0);
            learner.AddValue(6.0);

            Assert.Equal(4.004006006, learner.GetAverage().Value, 6);
            // Still close to the old plain mean (4.0) - decay only nudges it, never wildly.
            Assert.True(System.Math.Abs(learner.GetAverage().Value - 4.0) < 0.1);
        }

        /// <summary>The property that actually matters for every EXISTING caller/test that feeds a
        /// constant value repeatedly (every calibration test in this project's own test suite does
        /// exactly this): decay changes NOTHING when the fed value never changes - both the weighted sum
        /// and the weighted-total-weight carry the identical geometric decay factor, which cancels out
        /// exactly, at every sample count, not just in the limit.</summary>
        [Fact]
        public void A_constant_input_series_gives_the_exact_same_average_regardless_of_decay()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < 500; i++)
            {
                learner.AddValue(42.0);
                Assert.Equal(42.0, learner.GetAverage().Value, 9);
            }
        }

        /// <summary>
        /// INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - mirrors
        /// <c>AdaptivePeakLearnerTests.Samples_counter_saturates_at_the_cap_while_learning_continues</c>'s
        /// own precedent exactly: <see cref="OnlineDistributionLearner.Count"/> must freeze at
        /// <see cref="OnlineDistributionLearner.SampleCountSaturationCap"/> while
        /// <see cref="OnlineDistributionLearner.GetAverage"/> (backed by an entirely separate,
        /// uncapped decaying-weighted-sum pair) keeps moving.
        /// </summary>
        [Fact]
        public void Count_saturates_at_the_cap_while_the_decaying_average_keeps_learning()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < OnlineDistributionLearner.SampleCountSaturationCap + 5; i++) learner.AddValue(2.0);

            Assert.Equal(OnlineDistributionLearner.SampleCountSaturationCap, learner.Count);

            double atCap = learner.GetAverage() ?? 0.0;
            for (int i = 0; i < 500; i++) learner.AddValue(6.0);
            double afterMore = learner.GetAverage() ?? 0.0;

            Assert.True(afterMore > atCap, $"the decaying average must keep moving after Count saturates: {atCap} -> {afterMore}");
            Assert.Equal(OnlineDistributionLearner.SampleCountSaturationCap, learner.Count); // still pinned
        }

        /// <summary>A WEIGHTED fold-in contributes proportionally less/more than a fully-trusted one -
        /// the mechanism <see cref="Normalized.NormalizedWheelLockSlipEngine"/>'s own continuous
        /// physical-limit trust weighting depends on.</summary>
        [Fact]
        public void AddValue_with_a_low_weight_contributes_proportionally_less_than_full_weight()
        {
            var learner = new OnlineDistributionLearner();
            learner.AddValue(100.0, weight: 1.0);
            learner.AddValue(0.0001, weight: 1.0); // a near-zero, but not exactly zero, second value

            // A near-fully-trusted first sample followed by ANY positive-weight low value should still
            // pull the average down substantially - sanity-checking the plumbing works at all before the
            // more targeted low-weight comparison below.
            Assert.True(learner.GetAverage().Value < 100.0);

            var lowWeightLearner = new OnlineDistributionLearner();
            lowWeightLearner.AddValue(100.0, weight: 1.0);
            lowWeightLearner.AddValue(0.0001, weight: 0.01); // SAME low value, but heavily DISCOUNTED

            // The heavily-discounted low value must pull the average down LESS than a fully-weighted one
            // would - proving the weight parameter genuinely scales each fold-in's own influence.
            Assert.True(lowWeightLearner.GetAverage().Value > learner.GetAverage().Value);
        }

        [Fact]
        public void AddValue_ignores_non_finite_values()
        {
            var learner = new OnlineDistributionLearner();
            learner.AddValue(double.NaN);
            learner.AddValue(double.PositiveInfinity);
            learner.AddValue(double.NegativeInfinity);

            Assert.Null(learner.GetAverage());
            Assert.Equal(0, learner.Count);
        }

        [Fact]
        public void GetPercentile_is_null_before_MinSamplesForPercentile_qualifying_samples()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < OnlineDistributionLearner.MinSamplesForPercentile - 1; i++)
                learner.AddValue(1.0);

            Assert.Null(learner.GetPercentile(50.0));
        }

        [Fact]
        public void GetPercentile_becomes_available_once_the_minimum_is_reached()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < OnlineDistributionLearner.MinSamplesForPercentile; i++)
                learner.AddValue(1.0);

            Assert.NotNull(learner.GetPercentile(50.0));
        }

        [Fact]
        public void GetPercentile_zero_values_are_excluded_from_the_ranking()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < 1000; i++) learner.AddValue(0.0);
            for (int i = 0; i < 600; i++) learner.AddValue(10.0);

            // Only the 600 nonzero samples count toward the 500-minimum and the ranking - the 1000
            // zeros must not dilute or delay readiness (matches SimHub's own includeZero:false default).
            Assert.Equal(10.0, learner.GetPercentile(50.0).Value, 9);
        }

        [Fact]
        public void GetPercentile_at_the_extremes_returns_the_min_and_max()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 1; i <= 1000; i++) learner.AddValue(i);

            Assert.Equal(1.0, learner.GetPercentile(0.01).Value, 1);
            Assert.True(learner.GetPercentile(99.9).Value >= 990.0);
        }

        [Fact]
        public void Percentile_zero_is_always_zero_regardless_of_readiness()
        {
            var learner = new OnlineDistributionLearner();
            Assert.Equal(0.0, learner.GetPercentile(0.0));
        }

        [Fact]
        public void Reset_clears_average_and_percentile_state()
        {
            var learner = new OnlineDistributionLearner();
            for (int i = 0; i < 600; i++) learner.AddValue(5.0);
            Assert.NotNull(learner.GetAverage());

            learner.Reset();

            Assert.Null(learner.GetAverage());
            Assert.Null(learner.GetPercentile(50.0));
            Assert.Equal(0, learner.Count);
        }

        [Fact]
        public void Count_tracks_every_folded_in_observation_including_zero()
        {
            var learner = new OnlineDistributionLearner();
            learner.AddValue(0.0);
            learner.AddValue(3.0);
            learner.AddValue(-3.0);

            Assert.Equal(3, learner.Count);
        }
    }
}
