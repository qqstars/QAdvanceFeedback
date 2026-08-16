using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class StreamingPercentileLearnerTests
    {
        [Fact]
        public void Fresh_learner_has_no_average_and_is_not_mature()
        {
            var learner = new StreamingPercentileLearner();
            Assert.Null(learner.Average());
            Assert.False(learner.IsMature);
            Assert.Equal(0, learner.Count);
        }

        [Fact]
        public void Average_is_the_plain_mean_of_absolute_values()
        {
            var learner = new StreamingPercentileLearner();
            learner.Observe(2.0);
            learner.Observe(-4.0);
            learner.Observe(6.0);

            Assert.Equal(4.0, learner.Average().Value, 9);
        }

        [Fact]
        public void Percentile_is_null_before_the_maturity_threshold()
        {
            var learner = new StreamingPercentileLearner();
            for (int i = 0; i < StreamingPercentileLearner.MinimumSamplesForMaturity - 1; i++) learner.Observe(1.0);

            Assert.Null(learner.Percentile(50.0));
            Assert.False(learner.IsMature);
        }

        [Fact]
        public void Percentile_becomes_available_at_the_maturity_threshold()
        {
            var learner = new StreamingPercentileLearner();
            for (int i = 0; i < StreamingPercentileLearner.MinimumSamplesForMaturity; i++) learner.Observe(1.0);

            Assert.NotNull(learner.Percentile(50.0));
            Assert.True(learner.IsMature);
        }

        [Fact]
        public void Percentile_zero_values_are_excluded_from_the_ranking()
        {
            var learner = new StreamingPercentileLearner();
            for (int i = 0; i < 1000; i++) learner.Observe(0.0);
            for (int i = 0; i < 300; i++) learner.Observe(10.0);

            Assert.Equal(10.0, learner.Percentile(50.0).Value, 9);
        }

        [Fact]
        public void Non_finite_observations_are_ignored()
        {
            var learner = new StreamingPercentileLearner();
            learner.Observe(double.NaN);
            learner.Observe(double.PositiveInfinity);
            learner.Observe(double.NegativeInfinity);

            Assert.Null(learner.Average());
            Assert.Equal(0, learner.Count);
        }

        [Fact]
        public void Percentile_at_the_extremes_returns_the_min_and_max()
        {
            var learner = new StreamingPercentileLearner();
            for (int i = 1; i <= 1000; i++) learner.Observe(i);

            Assert.Equal(1.0, learner.Percentile(0.01).Value, 1);
            Assert.True(learner.Percentile(99.9).Value >= 990.0);
        }
    }
}
