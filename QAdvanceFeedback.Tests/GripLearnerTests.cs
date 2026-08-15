using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class GripLearnerTests
    {
        [Fact]
        public void Fresh_learner_has_zero_confidence_and_the_seed_peak()
        {
            var learner = new GripLearner();
            Assert.Equal(0.0, learner.Confidence, 9);
            Assert.Equal(GripLearner.SeedPeakG, learner.LearnedPeakG, 9);
            Assert.Equal(0, learner.Samples);
        }

        [Fact]
        public void Cold_start_ceilings_the_ratio_even_when_the_raw_ratio_would_read_100_or_more()
        {
            var learner = new GripLearner();

            // Very first observation: raw ratio would be exactly 1.0 (magnitude == seed peak), but
            // confidence is 0, so the ceiling (0.75) must win.
            double ratio = learner.Ratio(GripLearner.SeedPeakG);
            Assert.Equal(GripLearner.ColdStartCeilingRatio, ratio, 6);
        }

        [Fact]
        public void Ceiling_relaxes_as_confidence_matures_and_disappears_at_full_confidence()
        {
            var learner = new GripLearner();

            // Feed exactly MaturitySamples qualifying observations, all at a magnitude far below the
            // (also-rising) learned peak, so the ratio computed for a FRESH large magnitude at the
            // end is governed by the ceiling formula, not by the peak having chased the magnitude.
            for (int i = 0; i < GripLearner.MaturitySamples; i++)
                learner.Observe(0.5);

            Assert.Equal(1.0, learner.Confidence, 6);

            // Once mature, a magnitude equal to the learned peak reads ratio 1.0 - no ceiling at all.
            double ratio = learner.Ratio(learner.LearnedPeakG);
            Assert.Equal(1.0, ratio, 3);
        }

        [Fact]
        public void Ceiling_is_strictly_between_cold_start_and_one_at_half_maturity()
        {
            var learner = new GripLearner();
            for (int i = 0; i < GripLearner.MaturitySamples / 2; i++)
                learner.Observe(0.5);

            Assert.InRange(learner.Confidence, 0.45, 0.55);

            // A magnitude far above the learned peak (raw ratio >> 1) still gets ceilinged, and the
            // ceiling at ~50% confidence must sit strictly between the cold-start floor and 1.0.
            double ratio = learner.Ratio(100.0);
            Assert.True(ratio > GripLearner.ColdStartCeilingRatio, "ceiling did not relax at all");
            Assert.True(ratio < 1.0, "ceiling had already fully disappeared at half maturity");
        }

        [Fact]
        public void Learned_peak_rises_toward_a_new_sustained_magnitude()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 500; i++) learner.Observe(3.0);

            Assert.True(learner.LearnedPeakG > 2.5, $"peak only reached {learner.LearnedPeakG}, expected to rise toward 3.0");
        }

        [Fact]
        public void Learned_peak_is_a_decaying_maximum_not_a_ratchet()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 500; i++) learner.Observe(4.0); // establish a high peak
            double highPeak = learner.LearnedPeakG;

            for (int i = 0; i < 5000; i++) learner.Observe(1.0); // sustained lower driving afterward

            Assert.True(learner.LearnedPeakG < highPeak, "a single early spike should not permanently pin the peak");
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(-1.0)]
        [InlineData(0.0)]
        [InlineData(GripLearner.MaxPlausibleG + 0.01)]
        public void Outlier_and_non_finite_observations_are_rejected_and_do_not_move_the_peak(double outlier)
        {
            var learner = new GripLearner();
            double before = learner.LearnedPeakG;
            int samplesBefore = learner.Samples;

            learner.Observe(outlier);

            Assert.Equal(before, learner.LearnedPeakG, 9);
            Assert.Equal(samplesBefore, learner.Samples);
        }

        [Fact]
        public void A_plausible_observation_at_the_ceiling_boundary_is_accepted()
        {
            var learner = new GripLearner();
            learner.Observe(GripLearner.MaxPlausibleG);
            Assert.Equal(1, learner.Samples);
        }

        [Fact]
        public void Load_adopts_a_usable_persisted_state()
        {
            var learner = new GripLearner();
            learner.Load(2.5, 400);

            Assert.Equal(2.5, learner.LearnedPeakG, 9);
            Assert.Equal(400, learner.Samples);
            Assert.Equal(1.0, learner.Confidence, 6);
        }

        [Theory]
        [InlineData(0.0, 10)]
        [InlineData(-1.0, 10)]
        [InlineData(double.NaN, 10)]
        public void Load_ignores_a_non_positive_or_non_finite_persisted_peak(double badPeak, int samples)
        {
            var learner = new GripLearner();
            double before = learner.LearnedPeakG;

            learner.Load(badPeak, samples);

            Assert.Equal(before, learner.LearnedPeakG, 9);
        }
    }
}
