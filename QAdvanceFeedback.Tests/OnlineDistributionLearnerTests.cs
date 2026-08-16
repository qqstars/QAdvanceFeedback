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

        [Fact]
        public void GetAverage_is_the_plain_mean_of_absolute_values()
        {
            var learner = new OnlineDistributionLearner();
            learner.AddValue(2.0);
            learner.AddValue(-4.0);
            learner.AddValue(6.0);

            Assert.Equal(4.0, learner.GetAverage().Value, 9);
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
