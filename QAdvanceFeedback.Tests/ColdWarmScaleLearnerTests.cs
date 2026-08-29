using System;
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

        /// <summary>
        /// SAFETY-RELEVANT FIX (docs\anchor-rescale-report.md): before this fix,
        /// <see cref="KeyedScaleLearner"/>'s own primary-tier weight
        /// (<see cref="Core.ColdWarmBlend.ConcaveHotWeight"/>) was a PRODUCT of a count term that DOES
        /// reach exactly 1.0 at <see cref="KeyedScaleLearner.CalibrationConfidenceScaleSamples"/> and a
        /// dispersion term that is STRICTLY LESS than 1.0 for any nonzero coefficient of variation -
        /// i.e. every real driving session, which never repeats the exact same reading at every
        /// physically-at-the-limit moment - so the product never actually reached full trust, no matter
        /// how much MORE evidence accumulated afterward (measured: a permanent plateau, not a slow
        /// approach - see docs\anchor-rescale-report.md's own before/after numbers). That silently left
        /// a genuinely-at-the-limit reading meaningfully off <see cref="KeyedScaleLearner.CanonicalAtLimitAnchor"/>
        /// FOREVER for any realistically-noisy source, defeating the point of the anchor for a
        /// safety-relevant "release the pedal now" cue. This test pins the fix: realistic (10%) jitter
        /// around a native ceiling, once genuinely abundant evidence exists, must still converge close
        /// to the anchor - not plateau several points away from it.
        /// </summary>
        [Fact]
        public void Primary_tier_reaches_full_trust_at_the_documented_sample_scale_even_with_realistic_dispersion()
        {
            var learner = new KeyedScaleLearner();
            var rng = new Random(42);
            double highest = 0.0;
            for (int i = 0; i < 250; i++)
            {
                double noisy = 90.0 * (1.0 + (rng.NextDouble() * 2 - 1) * 0.10); // realistic 10% jitter
                highest = Math.Max(highest, noisy);
                // Both observers, as the engine always does - see the anti-correlation fix.
                learner.ObserveAtPhysicalLimit(Game, Car, Source, noisy);
                learner.ObserveGeneral(Game, Car, Source, noisy);
            }

            // RE-SPECIFIED BY THE ANTI-CORRELATION FIX, not merely re-tuned. The ceiling is now a HIGH
            // PERCENTILE of the source's own distribution rather than the mean of its at-limit readings,
            // so with a jittered series it lands near the TOP of the spread, not its middle. The property
            // worth defending is unchanged - a genuinely-at-the-limit reading maps to the anchor - but
            // "genuinely at the limit" now means the top of the observed range, not its centre.
            double rescaledAtTrueCeiling = learner.Rescale(Game, Car, Source, highest);

            Assert.True(Math.Abs(rescaledAtTrueCeiling - KeyedScaleLearner.CanonicalAtLimitAnchor) < 4.0,
                $"a genuinely-at-the-limit reading with realistic (10%) dispersion must converge close to " +
                $"the anchor ({KeyedScaleLearner.CanonicalAtLimitAnchor}) once evidence is abundant " +
                $"(>= {KeyedScaleLearner.CalibrationConfidenceScaleSamples} samples) - got {rescaledAtTrueCeiling}");
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
