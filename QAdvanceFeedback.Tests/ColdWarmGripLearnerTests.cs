using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3) for <see cref="GripLearner"/>/
    /// <see cref="KeyedGripLearner"/>. See <see cref="ColdWarmBlend"/>'s own remarks for the mechanism
    /// (dispersion-weighted, not sample-count-weighted) and its honestly-disclosed limitations.
    /// </summary>
    public class ColdWarmGripLearnerTests
    {
        [Fact]
        public void Cold_start_with_no_persisted_value_behaves_exactly_as_before_this_feature()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 50; i++) learner.Observe(3.0);

            // No Load ever called - PublishedPeakG must be identical to the raw hot LearnedPeakG, byte
            // for byte, not merely "close".
            Assert.Equal(learner.LearnedPeakG, learner.PublishedPeakG, 12);
            Assert.Equal(learner.LearnedPeakG, learner.PersistedPeakG, 12);
        }

        [Fact]
        public void A_restart_with_no_new_driving_reproduces_the_previous_mapping_exactly()
        {
            var learner = new GripLearner();
            learner.Load(2.4, 500);

            // Zero new observations this session - the published (blended) reference must equal the
            // persisted cold value EXACTLY, not merely approximately.
            Assert.Equal(2.4, learner.PublishedPeakG, 12);
            Assert.Equal(1.0, learner.Ratio(2.4), 6); // matches Ratio's own mature, no-ceiling behaviour
        }

        [Fact]
        public void A_clean_tight_session_shifts_the_published_reference_toward_hot_within_a_few_braking_zones()
        {
            var learner = new GripLearner();
            learner.Load(1.0, 500); // a cold reference far from what this session will show

            // ~2 "braking zones" worth of TIGHT, repeatable observations (identical value - CV exactly 0).
            for (int i = 0; i < 45; i++) learner.Observe(3.0);

            Assert.True(learner.PublishedPeakG > 2.0,
                $"a clean, tight session should shift the published reference well toward the new 3.0g condition within ~2 braking zones, got {learner.PublishedPeakG}");
        }

        [Fact]
        public void A_noisy_scattered_session_stays_near_cold_even_with_many_samples()
        {
            var learner = new GripLearner();
            learner.Load(1.0, 500);

            // Wildly scattered observations (alternating low/high) - many samples, but never
            // repeatable/tight. Mean is deliberately similar to the clean-session test above (~3.0-ish)
            // so this test isolates DISPERSION as the distinguishing factor, not magnitude.
            double[] scattered = { 0.3, 5.5, 0.5, 5.0, 0.2, 6.0, 0.4, 5.8, 0.3, 5.5 };
            for (int i = 0; i < 20; i++)
                foreach (double v in scattered) learner.Observe(v);

            // A count-only weighting (see the MutationGuard test below) would have pushed this well
            // above 4.5 (near the scattered session's own mean) by 200 samples - staying under 2.0 (well
            // under half the scattered session's own ~3g mean, close to the 1.0 cold value) demonstrates
            // the dispersion term is genuinely suppressing this noisy session's own influence.
            Assert.True(learner.PublishedPeakG < 2.0,
                $"a noisy, scattered session must stay close to the persisted cold value even after 200 samples, got {learner.PublishedPeakG}");
        }

        /// <summary>MUTATION EVIDENCE: if HOT were weighted by sample count ALONE (dropping the
        /// dispersion factor), the noisy session above (200 samples, comfortably past
        /// <see cref="ColdWarmBlend.SampleSaturationK"/>) would already carry near-full weight and the
        /// published reference would sit near the scattered session's own (much higher) mean - this test
        /// pins the actual dispersion-aware behaviour so a regression to count-only weighting is
        /// caught.</summary>
        [Fact]
        public void MutationGuard_count_only_weighting_would_have_trusted_the_noisy_session_far_more()
        {
            var learner = new GripLearner();
            learner.Load(1.0, 500);

            double[] scattered = { 0.3, 5.5, 0.5, 5.0, 0.2, 6.0, 0.4, 5.8, 0.3, 5.5 };
            for (int i = 0; i < 20; i++)
                foreach (double v in scattered) learner.Observe(v);

            // A count-only weight at 200 samples (SampleSaturationK=5) would be 200/205 = 0.9756 -
            // overwhelmingly HOT. The dispersion-aware weight actually used must be far below that.
            double countOnlyWeight = 200.0 / (200.0 + ColdWarmBlend.SampleSaturationK);
            double actualWeight = ColdWarmBlend.HotWeight(200, StdDevOverMean(scattered));

            Assert.True(actualWeight < countOnlyWeight * 0.5,
                $"dispersion-aware weight ({actualWeight}) should be far below the count-only weight ({countOnlyWeight})");
        }

        private static double StdDevOverMean(double[] values)
        {
            double mean = 0; foreach (double v in values) mean += v; mean /= values.Length;
            double variance = 0; foreach (double v in values) variance += (v - mean) * (v - mean); variance /= values.Length;
            return System.Math.Sqrt(variance) / mean;
        }

        [Fact]
        public void The_live_blend_is_continuous_across_the_persistence_sample_threshold()
        {
            // ColdWarmBlend.MinSamplesForPersist (30) is a deliberate discrete decision point for
            // PERSISTENCE only - the LIVE published blend itself must not jump there.
            var atTwentyNine = new GripLearner();
            atTwentyNine.Load(1.0, 500);
            for (int i = 0; i < 29; i++) atTwentyNine.Observe(3.0);

            var atThirty = new GripLearner();
            atThirty.Load(1.0, 500);
            for (int i = 0; i < 30; i++) atThirty.Observe(3.0);

            double delta = System.Math.Abs(atThirty.PublishedPeakG - atTwentyNine.PublishedPeakG);
            Assert.True(delta < 0.1, $"the live blend must not jump across the persistence threshold, delta={delta}");

            // Meanwhile ShouldPersist itself DOES flip exactly there - the one deliberate discrete point.
            Assert.False(ColdWarmBlend.ShouldPersist(29, 0.0));
            Assert.True(ColdWarmBlend.ShouldPersist(30, 0.0));
        }

        [Fact]
        public void A_noisy_session_does_not_overwrite_an_already_persisted_profile()
        {
            var learner = new KeyedGripLearner();
            learner.ImportAll(new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = new GripLearnerState { PeakG = 3.5, Samples = 5000 }
            });

            double[] scattered = { 0.2, 6.5, 0.3, 6.0, 0.1, 7.0 };
            for (int i = 0; i < 40; i++)
                foreach (double v in scattered) learner.Observe("GameA", "Car1", v);

            var exported = learner.ExportAll();
            Assert.True(exported.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car1")));
            Assert.Equal(3.5, exported[KeyedGripLearner.MakeKey("GameA", "Car1")].PeakG, 6);
        }

        [Fact]
        public void A_clean_session_DOES_eventually_get_persisted_over_the_old_cold_value()
        {
            var learner = new KeyedGripLearner();
            learner.ImportAll(new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = new GripLearnerState { PeakG = 1.0, Samples = 5000 }
            });

            for (int i = 0; i < 45; i++) learner.Observe("GameA", "Car1", 3.0); // tight, repeatable

            var exported = learner.ExportAll();
            double persisted = exported[KeyedGripLearner.MakeKey("GameA", "Car1")].PeakG;
            Assert.True(persisted > 1.5, $"a clean session should eventually be trusted enough to persist, got {persisted}");
        }
    }
}
