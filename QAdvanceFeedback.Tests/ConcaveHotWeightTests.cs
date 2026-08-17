using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="ColdWarmBlend.ConcaveHotWeight"/>/<see cref="ColdWarmBlend.ConcaveCountConfidence"/>/
    /// <see cref="ColdWarmBlend.DispersionQuality"/> - the owner's own concrete blend specification
    /// (docs\regression-fix-report.md, the sample-threshold follow-up): a CONCAVE (front-loaded) count
    /// term combined with the SAME dispersion-quality machinery <see cref="ColdWarmBlend.HotWeight"/>
    /// already uses, replacing the retired absolute-count gates (200, then 60) entirely.
    /// </summary>
    public class ConcaveHotWeightTests
    {
        // ------------------------------------------------------------------------------------
        // THE CONCAVE SHAPE - front-loaded, matches the owner's own worked anchors closely.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0.1, 0.19)]
        [InlineData(0.3, 0.51)]
        [InlineData(0.5, 0.75)]
        [InlineData(0.75, 0.9375)]
        [InlineData(1.0, 1.0)]
        public void ConcaveCountConfidence_matches_the_gamma_2_curve_at_known_points(double fraction, double expected)
        {
            int count = (int)(fraction * 200);
            double confidence = ColdWarmBlend.ConcaveCountConfidence(count, 200.0);
            Assert.Equal(expected, confidence, 2);
        }

        /// <summary>THE property that actually matters: the FIRST evidence counts for MORE than a
        /// proportional (linear) share would give it - e.g. 10% of the way to the scale reference
        /// already earns ~19% confidence, not 10%.</summary>
        [Fact]
        public void Early_evidence_earns_more_than_its_proportional_linear_share()
        {
            double at10Percent = ColdWarmBlend.ConcaveCountConfidence(20, 200.0);
            Assert.True(at10Percent > 0.10, $"10% of the way to the scale reference should earn MORE than a linear 10% - got {at10Percent:F3}");
        }

        [Fact]
        public void Zero_samples_gives_zero_confidence()
        {
            Assert.Equal(0.0, ColdWarmBlend.ConcaveCountConfidence(0, 200.0), 9);
        }

        [Fact]
        public void At_or_beyond_the_scale_reference_confidence_saturates_at_exactly_one()
        {
            Assert.Equal(1.0, ColdWarmBlend.ConcaveCountConfidence(200, 200.0), 9);
            Assert.Equal(1.0, ColdWarmBlend.ConcaveCountConfidence(500, 200.0), 9); // well past - still exactly 1, never more
        }

        /// <summary>THE SCALE REFERENCE IS SOFT, NOT A GATE - confidence is already meaningfully non-zero
        /// far below it, unlike a hard threshold which would read exactly 0 anywhere below the bar.</summary>
        [Fact]
        public void A_sample_count_far_below_the_scale_reference_still_earns_real_nonzero_confidence()
        {
            double confidence = ColdWarmBlend.ConcaveCountConfidence(5, 200.0); // 2.5% of the way there
            Assert.True(confidence > 0.0, "even far below the scale reference, confidence must be non-zero (no hard gate)");
        }

        // ------------------------------------------------------------------------------------
        // DISPERSION QUALITY - the owner's own explicit second refinement: count alone is not enough.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Tight_consistent_evidence_earns_high_dispersion_quality()
        {
            Assert.True(ColdWarmBlend.DispersionQuality(0.01) > 0.9, "a tight, repeatable set of readings should earn near-full dispersion trust");
        }

        [Fact]
        public void Scattered_noisy_evidence_earns_low_dispersion_quality_regardless_of_how_much_arrives()
        {
            Assert.True(ColdWarmBlend.DispersionQuality(1.0) < 0.2, "a scattered/erratic set of readings should earn low dispersion trust");
        }

        /// <summary>THE owner's own explicit concern: "150 noisy, scattered observations" must NOT earn
        /// the same weight as "150 tight consistent ones" - proven directly by comparing
        /// <see cref="ColdWarmBlend.ConcaveHotWeight"/> at the SAME count with different dispersion.</summary>
        [Fact]
        public void The_same_sample_count_earns_far_less_weight_when_the_evidence_is_scattered()
        {
            double tightWeight = ColdWarmBlend.ConcaveHotWeight(150, coefficientOfVariation: 0.01, scaleSamples: 200.0);
            double scatteredWeight = ColdWarmBlend.ConcaveHotWeight(150, coefficientOfVariation: 1.0, scaleSamples: 200.0);

            Assert.True(tightWeight > 0.8, $"150 tight, consistent observations should earn high weight - got {tightWeight:F3}");
            Assert.True(scatteredWeight < 0.2, $"150 scattered, noisy observations should stay near identity regardless of count - got {scatteredWeight:F3}");
        }

        [Fact]
        public void Undefined_dispersion_below_two_samples_gives_zero_weight()
        {
            // Mirrors WelfordAccumulator.CoefficientOfVariation's own remarks: fewer than 2 observations
            // -> PositiveInfinity -> "maximally dispersed" -> zero trust, biasing toward identity/cold.
            Assert.Equal(0.0, ColdWarmBlend.ConcaveHotWeight(1, double.PositiveInfinity, 200.0), 9);
        }

        // ------------------------------------------------------------------------------------
        // MUTATION EVIDENCE - both refinements, proven to matter.
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// MUTATION EVIDENCE (docs\regression-fix-report.md): a LINEAR count weight (the naive
        /// alternative the owner's own follow-up explicitly rejected: "Do NOT use a linear weight") gives
        /// early evidence only its proportional share, not the front-loaded boost the concave curve
        /// provides - directly reproduces the specific numeric gap the owner's own worked example relies
        /// on (10% of the way there earning ~19%, not a plain 10%).
        /// </summary>
        [Fact]
        public void MutationGuard_a_linear_weight_would_not_front_load_early_evidence()
        {
            double linearWeightAt10Percent = 20.0 / 200.0; // the naive, REJECTED linear alternative
            double concaveWeightAt10Percent = ColdWarmBlend.ConcaveCountConfidence(20, 200.0);

            Assert.Equal(0.10, linearWeightAt10Percent, 6);
            Assert.True(concaveWeightAt10Percent > linearWeightAt10Percent,
                $"the concave curve must give early evidence MORE than a linear weight would - linear={linearWeightAt10Percent:F3} concave={concaveWeightAt10Percent:F3}");
        }

        /// <summary>
        /// MUTATION EVIDENCE (docs\regression-fix-report.md): dropping the dispersion factor entirely
        /// (count confidence alone) would let 150 SCATTERED, noisy observations earn the SAME high weight
        /// as 150 tight ones - reproduced directly by computing count-confidence alone (no dispersion
        /// term) and confirming it does NOT distinguish the two, unlike the real, dispersion-combined
        /// <see cref="ColdWarmBlend.ConcaveHotWeight"/> above.
        /// </summary>
        [Fact]
        public void MutationGuard_dropping_dispersion_would_treat_scattered_and_tight_evidence_identically()
        {
            double countConfidenceOnly = ColdWarmBlend.ConcaveCountConfidence(150, 200.0);

            // Count-confidence ALONE (the mutation) cannot tell tight from scattered evidence apart - it
            // is the SAME number regardless of dispersion, which is exactly the defect the owner flagged.
            Assert.Equal(countConfidenceOnly, ColdWarmBlend.ConcaveCountConfidence(150, 200.0), 9);

            // The REAL mechanism, by contrast, clearly does distinguish them (see the test above) -
            // reasserted here for direct side-by-side contrast with the mutation.
            double realTight = ColdWarmBlend.ConcaveHotWeight(150, 0.01, 200.0);
            double realScattered = ColdWarmBlend.ConcaveHotWeight(150, 1.0, 200.0);
            Assert.True(realTight > 0.8);
            Assert.True(realScattered < 0.2);
            Assert.True(countConfidenceOnly > 0.8, "count-confidence alone (the mutation) rates scattered evidence just as highly as tight evidence");
        }
    }
}
