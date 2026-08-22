using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class GripLearnerTests
    {
        /// <summary>
        /// NO MINIMUM-SAMPLE GATE (docs\robust-auto-gforce-report.md, owner's hard constraint) - this
        /// class has never had one: <see cref="GripLearner.Observe"/> updates <see cref="GripLearner.LearnedPeakG"/>
        /// UNCONDITIONALLY on every valid call, immediately, with no "wait for N observations before this
        /// counts" gate anywhere. <see cref="GripLearner.Confidence"/> is a smooth 0..1 ramp that only
        /// ever CEILINGS <see cref="GripLearner.Ratio"/>'s result while low - it never blocks
        /// <see cref="GripLearner.Observe"/> from updating the peak itself. MUTATION EVIDENCE: if a
        /// minimum-sample gate were introduced (e.g. "do not update LearnedPeakG below N observations"),
        /// this test fails immediately, since a single observation would incorrectly still read
        /// <see cref="GripLearner.SeedPeakG"/> instead of having moved toward the real observation.
        /// </summary>
        [Fact]
        public void A_single_observation_immediately_moves_the_learned_peak_no_minimum_sample_gate()
        {
            var learner = new GripLearner();
            learner.Observe(5.0);

            Assert.NotEqual(GripLearner.SeedPeakG, learner.LearnedPeakG, 6);
            Assert.Equal(1, learner.Samples);
        }

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

        /// <summary>
        /// RE-EXPRESSED, not weakened (docs\stability-confidence-fix-report.md - the cold-vs-converged
        /// over-reporting safety fix): reaching <see cref="GripLearner.MaturitySamples"/> qualifying
        /// observations no longer, by itself, removes the ceiling - <see cref="GripLearner.Ratio"/> now
        /// gates on <see cref="GripLearner.MaturityConfidence"/> (sample count AND stability), per that
        /// property's own remarks. This fixture's own peak settles to a fixed 0.5 well before
        /// MaturitySamples (200) is reached, so feeding a comfortable margin PAST 200 (still one
        /// ordinary session, nowhere near forbidden-forever-conservative) lets BOTH terms reach 1.0,
        /// reproducing the original "no ceiling once genuinely mature" guarantee - now honestly
        /// requiring the reference to have actually settled, not merely to have been observed enough
        /// times.
        /// </summary>
        [Fact]
        public void Ceiling_relaxes_as_confidence_matures_and_disappears_at_full_confidence()
        {
            var learner = new GripLearner();

            // Feed comfortably more than MaturitySamples qualifying observations, all at a magnitude far
            // below the (also-rising) learned peak, so the ratio computed for a FRESH large magnitude at
            // the end is governed by the ceiling formula, not by the peak having chased the magnitude -
            // and comfortably more than StabilityScaleSamples of quiet room AFTER the peak settles, so
            // the reference has had time to actually settle (QuietStreak), not merely accumulate samples.
            int total = GripLearner.MaturitySamples + (int)GripLearner.StabilityScaleSamples + 100;
            for (int i = 0; i < total; i++)
                learner.Observe(0.5);

            Assert.Equal(1.0, learner.Confidence, 6);

            // Once genuinely mature (enough samples AND settled), a magnitude equal to the learned peak
            // reads ratio 1.0 - no ceiling at all.
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

        /// <summary>
        /// SETTLING SPEED (docs\branch-dispatch-and-source-keyed-learning-report.md - the owner's own
        /// "wet after dry" concern): a decaying maximum's own asymmetry means a carried-over dry-session
        /// peak stays too high (under-reporting severity) until it decays down to the new, genuinely
        /// lower condition. The owner's own bar: settle within "the first few braking zones" of a
        /// session, not tens of them. Pinned here so a future change to the decay rate cannot silently
        /// regress back to the old, measured-too-slow (~861-sample) behaviour.
        /// </summary>
        [Fact]
        public void Learned_peak_settles_to_a_lower_condition_within_a_few_braking_zones()
        {
            var learner = new GripLearner();

            // Establish a dry-session peak (~1.5g).
            for (int i = 0; i < 300; i++) learner.Observe(1.5);
            double dryPeak = learner.LearnedPeakG;
            Assert.True(dryPeak > 1.3, $"precondition: dry peak should have converged near 1.5, was {dryPeak}");

            // Carry that reference into a wet session (~0.9g - a 40% reduction) - feed roughly FIVE
            // ordinary braking zones' worth of qualifying samples (30 each = 150 total), not tens of
            // zones, and require the reference to have settled close to the new condition by then.
            for (int i = 0; i < 150; i++) learner.Observe(0.9);

            Assert.True(learner.LearnedPeakG < 1.1,
                $"expected the reference to have settled close to the new ~0.9g condition within ~5 braking zones (150 qualifying samples), still reading {learner.LearnedPeakG}");
        }

        /// <summary>Guards the OTHER side of the same trade-off: an ordinarily-softer single braking
        /// zone (not a real condition change) must not collapse the peak dramatically - it should still
        /// read comfortably above the OLD test's own "settled" bar after just one such zone, self-
        /// correcting on the very next hard zone via the instant-rise mechanism.</summary>
        [Fact]
        public void A_single_ordinarily_softer_braking_zone_does_not_make_the_peak_wander_far()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 300; i++) learner.Observe(1.5);
            double dryPeak = learner.LearnedPeakG;

            // One ordinary zone's worth of samples (~40) at a modestly softer 1.3g - NOT a condition
            // change, just normal variance.
            for (int i = 0; i < 40; i++) learner.Observe(1.3);

            Assert.True(learner.LearnedPeakG > dryPeak * 0.85,
                $"a single ordinary zone should not make the peak wander far: {dryPeak} -> {learner.LearnedPeakG}");
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

        // ---------------------------------------------------------------------------------------
        // REJECT vs CLAMP (docs\gforce-direction-fix-report.md, the owner's plausibility-limit ask):
        // Observe (learning) REJECTS above LearnCapG; Ratio (live) CLAMPS against the separate, higher
        // LiveClampG - different bounds for different needs.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_plain_constructor_defaults_LearnCapG_to_the_shared_MaxPlausibleG()
        {
            var learner = new GripLearner();
            Assert.Equal(GripLearner.MaxPlausibleG, learner.LearnCapG, 6);
        }

        [Fact]
        public void A_custom_learn_cap_rejects_above_its_own_ceiling_even_though_it_is_below_the_shared_default()
        {
            // A channel-specific cap (e.g. Slip's 6g) must reject a magnitude that the SHARED default
            // (8g) would have accepted.
            var learner = new GripLearner(learnCapG: 6.0);
            learner.Observe(7.0);

            Assert.Equal(0, learner.Samples);
            Assert.Equal(GripLearner.SeedPeakG, learner.LearnedPeakG, 9);
        }

        [Fact]
        public void A_custom_learn_cap_still_accepts_a_magnitude_at_or_below_its_own_ceiling()
        {
            var learner = new GripLearner(learnCapG: 6.0);
            learner.Observe(5.5);

            Assert.Equal(1, learner.Samples);
        }

        [Fact]
        public void A_non_positive_or_non_finite_custom_learn_cap_falls_back_to_the_default()
        {
            Assert.Equal(GripLearner.MaxPlausibleG, new GripLearner(learnCapG: 0.0).LearnCapG, 6);
            Assert.Equal(GripLearner.MaxPlausibleG, new GripLearner(learnCapG: -1.0).LearnCapG, 6);
            Assert.Equal(GripLearner.MaxPlausibleG, new GripLearner(learnCapG: double.NaN).LearnCapG, 6);
        }

        [Fact]
        public void Ratio_clamps_an_impact_magnitude_reading_against_the_higher_LiveClampG_instead_of_rejecting_it()
        {
            var learner = new GripLearner(learnCapG: 6.0); // e.g. the Slip channel
            // Comfortably more than MaturitySamples AND StabilityScaleSamples (docs\stability-
            // confidence-fix-report.md) so the reference has both enough evidence and enough settled,
            // quiet time - mature, no cold-start ceiling.
            int total = GripLearner.MaturitySamples + (int)GripLearner.StabilityScaleSamples + 50;
            for (int i = 0; i < total; i++) learner.Observe(2.0);

            // 18g exceeds BOTH LearnCapG (6.0) and would have exceeded the old, single MaxPlausibleG
            // (8.0) too - but Ratio must still produce a real, finite, clamped-not-rejected number
            // (clamped at LiveClampG = 15.0), not zero/garbage/an exception.
            double ratio = learner.Ratio(18.0);

            Assert.True(double.IsFinite(ratio));
            Assert.True(ratio > 0.0);
            Assert.Equal(GripLearner.LiveClampG / learner.LearnedPeakG, ratio, 3);
        }

        [Fact]
        public void LiveClampG_is_higher_than_the_default_learning_cap()
        {
            Assert.True(GripLearner.LiveClampG > GripLearner.MaxPlausibleG,
                "the live-path clamp must be a HIGHER bound than the learning-path reject ceiling - different needs, see both constants' own remarks");
        }
    }
}
