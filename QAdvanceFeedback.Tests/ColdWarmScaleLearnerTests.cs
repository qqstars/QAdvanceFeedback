using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3) for <see cref="KeyedScaleLearner"/> - the
    /// Raw-side per-source calibration learner, previously session-scoped only (no persistence at all).
    /// Mirrors <see cref="ColdWarmGripLearnerTests"/>' own coverage shape for the sibling learner.
    /// </summary>
    public class ColdWarmScaleLearnerTests
    {
        private const string Game = "GameA";
        private const string Car = "Car1";
        private const string Source = "ShakeIt";

        [Fact]
        public void Cold_start_with_no_persisted_ceiling_behaves_exactly_as_before_this_feature()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 25; i++) learner.ObserveAtPhysicalLimit(Game, Car, Source, 80.0);

            double? hot = learner.LearnedCeiling(Game, Car, Source, out bool hotPrimary);
            double? published = learner.PublishedCeiling(Game, Car, Source, out bool publishedPrimary);

            Assert.Equal(hot, published);
            Assert.Equal(hotPrimary, publishedPrimary);
        }

        [Fact]
        public void A_restart_with_no_new_driving_reproduces_the_previous_ceiling_exactly()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 62.0);

            double? published = learner.PublishedCeiling(Game, Car, Source, out _);
            Assert.True(published.HasValue);
            Assert.Equal(62.0, published.Value, 9);

            double rescaled = learner.Rescale(Game, Car, Source, 62.0);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, rescaled, 6);
        }

        [Fact]
        public void A_clean_tight_session_shifts_the_published_ceiling_toward_hot()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 40.0); // a cold reference far from what this session will show

            for (int i = 0; i < 25; i++) learner.ObserveAtPhysicalLimit(Game, Car, Source, 90.0); // tight, repeatable

            double? published = learner.PublishedCeiling(Game, Car, Source, out _);
            Assert.True(published.HasValue && published.Value > 60.0,
                $"a clean, tight session should shift the published ceiling well toward the new ~90 condition, got {published}");
        }

        [Fact]
        public void A_noisy_scattered_session_stays_near_the_persisted_cold_ceiling()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 40.0);

            double[] scattered = { 20.0, 95.0, 25.0, 90.0, 15.0, 100.0 };
            for (int i = 0; i < 10; i++)
                foreach (double v in scattered) learner.ObserveAtPhysicalLimit(Game, Car, Source, v);

            double? published = learner.PublishedCeiling(Game, Car, Source, out _);
            Assert.True(published.HasValue && published.Value < 55.0,
                $"a noisy, scattered session must stay close to the persisted cold ceiling, got {published}");
        }

        [Fact]
        public void A_noisy_session_does_not_overwrite_an_already_persisted_ceiling()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 55.0);

            double[] scattered = { 10.0, 95.0, 15.0, 90.0, 5.0, 100.0 };
            for (int i = 0; i < 10; i++)
                foreach (double v in scattered) learner.ObserveAtPhysicalLimit(Game, Car, Source, v);

            var exported = learner.ExportAll();
            string key = KeyedGripLearner.MakeKey(Game, Car, Source);
            Assert.True(exported.ContainsKey(key));
            Assert.Equal(55.0, exported[key].ColdCeiling, 6);
        }

        [Fact]
        public void A_clean_session_does_get_persisted_over_an_old_cold_ceiling()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 40.0);

            for (int i = 0; i < 35; i++) learner.ObserveAtPhysicalLimit(Game, Car, Source, 90.0); // tight, repeatable, past MinSamplesForPersist

            var exported = learner.ExportAll();
            string key = KeyedGripLearner.MakeKey(Game, Car, Source);
            Assert.True(exported[key].ColdCeiling > 60.0, $"a clean session should eventually be trusted enough to persist, got {exported[key].ColdCeiling}");
        }

        [Fact]
        public void ExportAll_then_ImportAll_round_trips_the_cold_ceiling()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 55.0);

            var exported = learner.ExportAll();

            var restored = new KeyedScaleLearner();
            restored.ImportAll(exported);

            double? restoredCeiling = restored.PublishedCeiling(Game, Car, Source, out _);
            Assert.True(restoredCeiling.HasValue);
            Assert.Equal(55.0, restoredCeiling.Value, 6);
        }

        [Fact]
        public void Reset_clears_the_persisted_cold_ceiling_too()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 55.0);

            learner.Reset();

            Assert.Null(learner.PublishedCeiling(Game, Car, Source, out _));
        }
    }
}
