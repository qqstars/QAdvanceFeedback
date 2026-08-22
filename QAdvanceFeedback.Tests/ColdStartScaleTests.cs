using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="ColdStartScale"/> - v1.0.6.9 rework, Goal 2 (docs\v1068-rework-report.md).
    /// Ported (with adjusted framing) from <c>QAdvanceFeedback_1.0.6.2_prerelease\QAdvanceFeedback.Tests\ColdStartScaleTests.cs</c> -
    /// the Layer 5 device-feel scale that replaces the 1.0.6.8 stability-confidence gate's Layer-4 clamp
    /// approach (measured on this rework's own real logs to almost never lift within a realistic session)
    /// with a smooth multiplicative scale (see that class's own remarks for the full mechanism and why a
    /// clamp was rejected).
    /// </summary>
    public class ColdStartScaleTests
    {
        // =====================================================================================
        // PART 1 - THE CURVE SHAPE
        // =====================================================================================

        [Fact]
        public void Floor_is_exactly_the_minimum_safe_floor_at_zero_confidence()
        {
            Assert.Equal(ColdStartScale.MinSafeFloor, ColdStartScale.Compute(0.0), 9);
        }

        [Fact]
        public void Scale_genuinely_reaches_exactly_one_at_full_confidence_not_an_asymptote()
        {
            // NOT an asymptote (a real, shipped bug in this project before - docs\anchor-rescale-report.md):
            // this must be an EXACT equality, not "close to".
            Assert.Equal(1.0, ColdStartScale.Compute(1.0), 9);
        }

        /// <summary>Pinned regression at the owner's own seven anchor points - a Hermite interpolant
        /// reproduces its own control points exactly, so these must match to the percent point.</summary>
        [Theory]
        [InlineData(0.00, 0.500)]
        [InlineData(0.10, 0.525)]
        [InlineData(0.50, 0.575)]
        [InlineData(0.75, 0.750)]
        [InlineData(0.80, 0.875)]
        [InlineData(0.90, 0.935)]
        [InlineData(1.00, 1.000)]
        public void Scale_hits_every_owner_anchor_point_exactly(double confidence, double expectedScale)
        {
            Assert.Equal(expectedScale, ColdStartScale.Compute(confidence), 9);
        }

        [Fact]
        public void Scale_is_monotonically_non_decreasing_across_a_fine_sweep_with_no_step()
        {
            double previous = ColdStartScale.Compute(0.0);
            for (double x = 0.001; x <= 1.0; x += 0.001)
            {
                double y = ColdStartScale.Compute(x);
                Assert.True(y >= previous - 1e-9, $"decreased at confidence={x}: {previous} -> {y}");
                Assert.True(y - previous < 0.03, $"jumped more than 3 scale-points in a 0.001 confidence step at {x}: {previous} -> {y}");
                previous = y;
            }
        }

        [Fact]
        public void Never_exceeds_one_or_drops_below_the_floor_across_the_whole_domain()
        {
            for (double x = 0.0; x <= 1.0; x += 0.01)
                Assert.InRange(ColdStartScale.Compute(x), ColdStartScale.MinSafeFloor - 1e-9, 1.0 + 1e-9);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(-5.0)]
        [InlineData(5.0)]
        public void Non_finite_or_out_of_range_confidence_is_clamped_defensively_never_throws(double weirdConfidence)
        {
            Assert.InRange(ColdStartScale.Compute(weirdConfidence), ColdStartScale.MinSafeFloor - 1e-9, 1.0 + 1e-9);
        }

        /// <summary>A RETURNING, already-warm (game,car) key must NOT be scaled down - GripLearner.Load
        /// restores the persisted sample count directly, so Confidence (and therefore this scale) is
        /// already at/near 1.0 from the very first frame of a new session, never re-derived from a fresh
        /// zero.</summary>
        [Fact]
        public void A_warm_persisted_learner_starts_this_session_at_full_scale_not_the_floor()
        {
            var learner = new GripLearner();
            learner.Load(learnedPeakG: 3.0, samples: GripLearner.MaturitySamples); // a fully-matured, persisted profile

            Assert.Equal(1.0, ColdStartScale.Compute(learner.Confidence), 9);
        }

        /// <summary>The genuinely cold case, for contrast with the warm test above.</summary>
        [Fact]
        public void A_genuinely_cold_learner_starts_at_the_floor()
        {
            var learner = new GripLearner();
            Assert.Equal(ColdStartScale.MinSafeFloor, ColdStartScale.Compute(learner.Confidence), 9);
        }

        [Fact]
        public void Scale_rises_continuously_as_a_cold_learner_accumulates_real_observations()
        {
            var learner = new GripLearner();
            double previousScale = ColdStartScale.Compute(learner.Confidence);

            for (int i = 0; i < GripLearner.MaturitySamples; i++)
            {
                learner.Observe(3.0);
                double scale = ColdStartScale.Compute(learner.Confidence);
                Assert.True(scale >= previousScale - 1e-9, $"scale decreased at sample {i}");
                previousScale = scale;
            }

            Assert.Equal(1.0, previousScale, 6);
        }

        // =====================================================================================
        // PART 2 - AMPLITUDE-DEPENDENT SCALING
        // =====================================================================================

        /// <summary>The owner's own two worked examples, verified exactly: a full-amplitude shake gets
        /// the full attenuation; a half-amplitude shake gets only HALF the attenuation.</summary>
        [Theory]
        [InlineData(100.0, 0.6, 60.0)]
        [InlineData(50.0, 0.6, 40.0)]
        [InlineData(100.0, 0.75, 75.0)]
        [InlineData(80.0, 0.75, 64.0)]
        public void Amplitude_scale_matches_the_owners_own_worked_examples(double projected, double scale, double expectedOutput)
        {
            Assert.Equal(expectedOutput, ColdStartScale.ApplyAmplitudeScale(projected, scale), 9);
        }

        [Fact]
        public void A_small_shake_survives_attenuation_far_better_than_a_large_one()
        {
            double smallOutput = ColdStartScale.ApplyAmplitudeScale(10.0, ColdStartScale.MinSafeFloor);
            double largeOutput = ColdStartScale.ApplyAmplitudeScale(100.0, ColdStartScale.MinSafeFloor);

            double smallFractionRetained = smallOutput / 10.0;
            double largeFractionRetained = largeOutput / 100.0;
            Assert.True(smallFractionRetained > largeFractionRetained,
                $"small-shake retention ({smallFractionRetained}) should exceed large-shake retention ({largeFractionRetained})");
        }

        /// <summary>THE SAFETY-CRITICAL PROPERTY: at the floor scale (0.5, the mathematical boundary),
        /// the amplitude transform must be monotone non-decreasing across the ENTIRE 0-100 projected
        /// range, with the derivative only ever reaching (never going below) exactly zero, at p=100.</summary>
        [Fact]
        public void Amplitude_transform_is_monotone_non_decreasing_in_projected_output_at_the_floor_scale()
        {
            double previous = double.NegativeInfinity;
            for (double p = 0.0; p <= 100.0; p += 0.1)
            {
                double output = ColdStartScale.ApplyAmplitudeScale(p, ColdStartScale.MinSafeFloor);
                Assert.True(output >= previous - 1e-9, $"a harder brake (p={p}) produced a WEAKER shake - cue inverted at the floor scale");
                previous = output;
            }
        }

        /// <summary>A caller-supplied scale BELOW the mathematical floor must be clamped up to the floor
        /// rather than silently inverting the cue.</summary>
        [Fact]
        public void A_scale_below_the_safety_floor_is_clamped_rather_than_inverting_the_cue()
        {
            double dangerousScale = 0.2; // well below MinSafeFloor - would invert the cue if used raw

            double previous = double.NegativeInfinity;
            for (double p = 0.0; p <= 100.0; p += 0.5)
            {
                double output = ColdStartScale.ApplyAmplitudeScale(p, dangerousScale);
                Assert.True(output >= previous - 1e-9, $"cue inverted at p={p} despite the floor clamp");
                previous = output;
            }
        }

        [Fact]
        public void Amplitude_transform_never_exceeds_the_projected_input_and_never_goes_negative()
        {
            foreach (double scale in new[] { ColdStartScale.MinSafeFloor, 0.6, 0.75, 0.9, 1.0 })
            {
                for (double p = 0.0; p <= 100.0; p += 5.0)
                {
                    double output = ColdStartScale.ApplyAmplitudeScale(p, scale);
                    Assert.InRange(output, 0.0, p + 1e-9);
                }
            }
        }

        [Fact]
        public void Amplitude_transform_at_full_scale_is_the_identity()
        {
            for (double p = 0.0; p <= 100.0; p += 5.0)
                Assert.Equal(p, ColdStartScale.ApplyAmplitudeScale(p, 1.0), 9);
        }

        // =====================================================================================
        // PART 3 - WIRED INTO ProjectedWheelLockSlipEngine (v1.0.6.9 rework, this task's own addition -
        // Parts 1/2 above are the ported, unmodified prerelease class; this section proves the NEW
        // wiring into this repo's own Layer 5 orchestrator).
        // =====================================================================================

        [Fact]
        public void Default_confidence_parameters_reproduce_the_pre_existing_2_arg_Compute_call_bit_identically()
        {
            var lockProjector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock));
            var slipProjector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Slip));
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, new PulseSettings(), slipProjector, new PulseSettings());
            var normalized = new NormalizedWheelLockSlipResult(
                new QAdvanceFeedback.Core.Corners(70, 70, 70, 70), 70, 70, 70, 70, 70,
                new QAdvanceFeedback.Core.Corners(40, 40, 40, 40), 40, 40, 40, 40, 40);

            var twoArg = engine.Compute(normalized, 0.016);
            var explicitFull = engine.Compute(normalized, 0.016, 1.0, 1.0);

            Assert.Equal(twoArg.LockAll, explicitFull.LockAll, 9);
            Assert.Equal(twoArg.SlipAll, explicitFull.SlipAll, 9);
        }

        [Fact]
        public void A_cold_confidence_measurably_damps_the_projected_output_versus_full_confidence()
        {
            var lockProjector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Lock));
            var slipProjector = new OutputProjector(ProjectorSettings.CreateShippedDefault(ProjectionChannel.Slip));
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, new PulseSettings(), slipProjector, new PulseSettings());
            var normalized = new NormalizedWheelLockSlipResult(
                new QAdvanceFeedback.Core.Corners(100, 100, 100, 100), 100, 100, 100, 100, 100,
                new QAdvanceFeedback.Core.Corners(100, 100, 100, 100), 100, 100, 100, 100, 100);

            var cold = engine.Compute(normalized, 0.016, 0.0, 0.0);
            var warm = engine.Compute(normalized, 0.016, 1.0, 1.0);

            Assert.True(cold.LockAll < warm.LockAll, $"cold={cold.LockAll} should be less than warm={warm.LockAll}");
            Assert.True(cold.SlipAll < warm.SlipAll, $"cold={cold.SlipAll} should be less than warm={warm.SlipAll}");
        }
    }
}
