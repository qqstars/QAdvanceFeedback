using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceEngine"/> - the washout (sustained low-pass + transient high-pass)
    /// model. The six acceptance scenarios (S1-S6) are the owner's own, verbatim from the wiring
    /// brief; each is a dedicated test. Steady-state (fully-converged, transient washed out to ~0)
    /// values are computed independently in-line and, not coincidentally, reproduce the PRE-washout
    /// model's own steady-state numbers exactly (since at steady state sustainedRatio == the raw
    /// ratio, matching the old "travel == intensity, no rate boost" case) - this cross-checks the new
    /// model's sustained path against the old one's already-validated arithmetic.
    /// <para/>
    /// A key technique used throughout: comparing the real engine (default TransientGain) against an
    /// otherwise-identical "twin" engine constructed with <c>TransientGain = 0</c> fed the EXACT same
    /// input sequence. The twin's output is, by construction, the pure sustained-path result with no
    /// transient at all - so the GAP between the two engines' outputs at any moment is precisely the
    /// transient's own contribution, without needing to hand-compute the washout filter cascade.
    /// <para/>
    /// Defaults used throughout unless a test overrides them: SustainTimeConstantSeconds=0.30,
    /// TransientTimeConstantSeconds=0.15, TransientGain=1.5, all four sustain fractions=0.5,
    /// LateralReferenceG=1.6, LateralBiasGain=0.5, LateralDirection=Normal. Default maxima:
    /// accelMaxG=0.9, decelMaxG=2.0.
    /// </summary>
    public class GForceEngineTests
    {
        private const double AccelMax = 0.9;
        private const double DecelMax = 2.0;

        private static TelemetrySample Sample(double? longG, double dtSeconds, double? latG = null)
        {
            var newFrame = new TelemetryFrame(longitudinalG: longG, lateralG: latG);
            return new TelemetrySample(newFrame, TelemetryFrame.Empty, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        private static TelemetrySample SampleNoDt(double? longG, double? latG = null)
        {
            var newFrame = new TelemetryFrame(longitudinalG: longG, lateralG: latG);
            return new TelemetrySample(newFrame, TelemetryFrame.Empty, DateTime.UtcNow, null);
        }

        /// <summary>Feeds a constant longG at a constant dt for enough steps to fully converge both
        /// washout filters (tens of time constants) and returns the final output.</summary>
        private static GForceOutput RunToSteadyState(GForceEngine engine, double longG, double dtSeconds = 0.05, int steps = 400)
        {
            GForceOutput last = null;
            for (int i = 0; i < steps; i++) last = engine.Compute(Sample(longG, dtSeconds), AccelMax, DecelMax);
            return last;
        }

        private static void AssertInBounds(double? value)
        {
            Assert.True(value.HasValue);
            Assert.InRange(value.Value, 0.0, 100.0);
        }

        // ---------------------------------------------------------------------------------------
        // Null / missing-data handling (0-not-null is mandatory), and the lateral-only fallback -
        // both unaffected by the washout redesign (neither touches the longitudinal filter state).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Missing_both_longitudinal_and_lateral_G_produces_null_on_every_channel()
        {
            var result = new GForceEngine().Compute(Sample(null, 0.05, null), AccelMax, DecelMax);
            Assert.True(result.IsEmpty);
        }

        [Fact]
        public void Null_sample_produces_null_on_every_channel()
        {
            Assert.True(new GForceEngine().Compute(null, AccelMax, DecelMax).IsEmpty);
        }

        [Fact]
        public void Longitudinal_missing_but_lateral_present_still_produces_output_not_null()
        {
            var result = new GForceEngine().Compute(Sample(null, 0.05, 0.8), AccelMax, DecelMax);
            Assert.False(result.IsEmpty);
            Assert.True(result.BottomFrontLeft.HasValue);
        }

        [Fact]
        public void Lateral_only_fallback_produces_a_genuine_left_right_difference()
        {
            var result = new GForceEngine().Compute(SampleNoDt(null, 1.6), AccelMax, DecelMax);

            Assert.True(result.BottomFrontRight.Value > result.BottomFrontLeft.Value);
            Assert.True(result.BackTopRight.Value > result.BackTopLeft.Value);
            Assert.Equal(result.BottomFrontLeft, result.BottomRearLeft);
            Assert.Equal(result.BottomRearLeft, result.BackLowLeft);
            Assert.Equal(result.BackLowLeft, result.BackTopLeft);
        }

        [Fact]
        public void Lateral_only_fallback_stays_within_0_to_100_and_is_continuous_through_zero()
        {
            var engine = new GForceEngine();
            double? prevLeft = null;

            for (double latG = -3.0; latG <= 3.0; latG += 0.05)
            {
                var r = engine.Compute(SampleNoDt(null, latG), AccelMax, DecelMax);
                AssertInBounds(r.BottomFrontLeft);
                if (prevLeft.HasValue) Assert.True(Math.Abs(r.BottomFrontLeft.Value - prevLeft.Value) <= 8.0);
                prevLeft = r.BottomFrontLeft;
            }
        }

        [Fact]
        public void Missing_lateral_G_still_produces_real_output_with_neutral_symmetric_split()
        {
            var engine = new GForceEngine();
            var result = RunToSteadyState(engine, -1.0);

            Assert.False(result.IsEmpty);
            Assert.Equal(result.BottomFrontLeft, result.BottomFrontRight);
            Assert.Equal(result.BackLowLeft, result.BackLowRight);
        }

        [Fact]
        public void Missing_dt_holds_the_filters_state_rather_than_advancing_incorrectly()
        {
            var engine = new GForceEngine();
            RunToSteadyState(engine, -2.0);
            var before = engine.Compute(Sample(-2.0, 0.05), AccelMax, DecelMax);

            var afterNoDt = engine.Compute(SampleNoDt(-2.0), AccelMax, DecelMax);

            Assert.Equal(before.BottomFrontLeft.Value, afterNoDt.BottomFrontLeft.Value, 6);
        }

        [Fact]
        public void Zero_G_is_a_real_reading_and_produces_zero_not_null()
        {
            var result = RunToSteadyState(new GForceEngine(), 0.0);
            Assert.False(result.IsEmpty);
            Assert.Equal(0.0, result.BottomFrontLeft.Value, 6);
            Assert.Equal(0.0, result.BottomRearLeft.Value, 6);
            Assert.Equal(0.0, result.BackLowLeft.Value, 6);
            Assert.Equal(0.0, result.BackTopLeft.Value, 6);
        }

        [Fact]
        public void Every_channel_stays_within_0_to_100_across_a_wide_sweep_of_inputs()
        {
            var engine = new GForceEngine();
            for (double longG = -6.0; longG <= 6.0; longG += 0.25)
            {
                for (double latG = -20.0; latG <= 20.0; latG += 5.0)
                {
                    var r = engine.Compute(Sample(longG, 0.02, latG), AccelMax, DecelMax);
                    AssertInBounds(r.BottomFrontLeft); AssertInBounds(r.BottomFrontRight);
                    AssertInBounds(r.BottomRearLeft); AssertInBounds(r.BottomRearRight);
                    AssertInBounds(r.BackLowLeft); AssertInBounds(r.BackLowRight);
                    AssertInBounds(r.BackTopLeft); AssertInBounds(r.BackTopRight);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Steady-state sustained distribution reproduces the pre-washout model's own arithmetic
        // (sustainedRatio == raw ratio once fully converged, exactly the old "no rate boost" case).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Steady_mid_level_braking_settles_on_Bottom_Rear()
        {
            var r = RunToSteadyState(new GForceEngine(), -1.0); // r = 1.0/2.0 = 0.5
            Assert.Equal(0.0, r.BackLowLeft.Value, 3);
            Assert.Equal(50.0, r.BottomRearLeft.Value, 1);
            Assert.Equal(0.0, r.BottomFrontLeft.Value, 3);
        }

        [Fact]
        public void Steady_max_braking_settles_at_the_derived_sustain_defaults()
        {
            // Re-derived defaults (see GForceEngine.BrakeBottomRearSustainFraction's remarks): the
            // terminal zone sits at 100% (not a setting), the middle zone (Bottom Rear) at 50%, the
            // far zone (Back Low) at 25% - a genuine spatial gradient, not the old flat 50%.
            var r = RunToSteadyState(new GForceEngine(), -2.0); // r = 1.0 (fully saturated)
            Assert.Equal(100.0, r.BottomFrontLeft.Value, 1);
            Assert.Equal(50.0, r.BottomRearLeft.Value, 1);
            Assert.Equal(25.0, r.BackLowLeft.Value, 1);
        }

        [Fact]
        public void Steady_max_acceleration_settles_at_the_derived_sustain_defaults()
        {
            // Acceleration's own terminal (Back Top, 100%), middle (Back Low, 50%), far (Bottom Rear,
            // 25%) - see the braking test's own remarks for the shared derivation.
            var r = RunToSteadyState(new GForceEngine(), 0.9); // r = 1.0 (fully saturated)
            Assert.Equal(100.0, r.BackTopLeft.Value, 1);
            Assert.Equal(50.0, r.BackLowLeft.Value, 1);
            Assert.Equal(25.0, r.BottomRearLeft.Value, 1);
        }

        [Fact]
        public void Zero_sustain_fraction_reproduces_fade_to_nothing_at_steady_state()
        {
            var engine = new GForceEngine { BrakeBottomRearSustainFraction = 0.0, BrakeBackLowSustainFraction = 0.0 };
            var r = RunToSteadyState(engine, -2.0);

            Assert.Equal(100.0, r.BottomFrontLeft.Value, 1);
            Assert.Equal(0.0, r.BottomRearLeft.Value, 1);
            Assert.Equal(0.0, r.BackLowLeft.Value, 1);
        }

        // ---------------------------------------------------------------------------------------
        // Owner's requirement 3: driver-facing lateral direction toggle - unaffected by the washout
        // redesign, re-verified at steady state.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Reversed_lateral_direction_mirrors_the_settled_steady_state_bias()
        {
            var e1 = new GForceEngine();
            var e2 = new GForceEngine { LateralDirection = LateralDirectionMode.Reversed };
            GForceOutput r1 = null, r2 = null;
            for (int i = 0; i < 400; i++)
            {
                r1 = e1.Compute(Sample(-1.0, 0.05, 1.6), AccelMax, DecelMax);
                r2 = e2.Compute(Sample(-1.0, 0.05, 1.6), AccelMax, DecelMax);
            }

            Assert.Equal(r1.BottomRearLeft.Value, r2.BottomRearRight.Value, 3);
            Assert.Equal(r1.BottomRearRight.Value, r2.BottomRearLeft.Value, 3);
        }

        [Fact]
        public void Full_scale_lateral_G_biases_toward_the_right_channels_at_steady_state()
        {
            var engine = new GForceEngine();
            GForceOutput r = null;
            for (int i = 0; i < 400; i++) r = engine.Compute(Sample(-1.0, 0.05, 1.6), AccelMax, DecelMax);

            Assert.True(r.BottomRearRight.Value > r.BottomRearLeft.Value);
        }

        [Fact]
        public void Negative_lateral_G_mirrors_the_bias_to_the_left_channels_at_steady_state()
        {
            var engine = new GForceEngine();
            GForceOutput r = null;
            for (int i = 0; i < 400; i++) r = engine.Compute(Sample(-1.0, 0.05, -1.6), AccelMax, DecelMax);

            Assert.True(r.BottomRearLeft.Value > r.BottomRearRight.Value);
        }

        [Fact]
        public void Reversed_lateral_direction_also_mirrors_the_lateral_only_fallback()
        {
            var normal = new GForceEngine();
            var reversed = new GForceEngine { LateralDirection = LateralDirectionMode.Reversed };
            var sample = SampleNoDt(null, 1.6);

            var rNormal = normal.Compute(sample, AccelMax, DecelMax);
            var rReversed = reversed.Compute(sample, AccelMax, DecelMax);

            Assert.Equal(rNormal.BottomFrontLeft.Value, rReversed.BottomFrontRight.Value, 6);
            Assert.Equal(rNormal.BottomFrontRight.Value, rReversed.BottomFrontLeft.Value, 6);
        }

        // ---------------------------------------------------------------------------------------
        // Continuity - no step jumps as longitudinal G sweeps continuously.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// NOTE ON METHOD: the engine is pre-settled at the sweep's own starting value before the
        /// sweep begins measuring deltas. Without this, the FIRST frame of a freshly-constructed
        /// engine is itself a "step" away from the implicit (0,0) filter rest state - a real, and
        /// arguably correct, one-off transient (a telemetry stream that starts mid-corner-exit with no
        /// prior history legitimately has nothing to interpolate from), but not what this test means
        /// by "continuity of the response to a changing input" - that is only meaningful once the
        /// filters already reflect the starting conditions.
        /// </summary>
        [Fact]
        public void Output_never_jumps_as_longitudinal_G_sweeps_continuously()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 500; i++) engine.Compute(Sample(-2.2, 0.02), AccelMax, DecelMax); // pre-settle

            double? prevFL = null, prevRL = null, prevLL = null, prevTL = null;

            for (double longG = -2.2; longG <= 1.0; longG += 0.01)
            {
                var r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                if (prevFL.HasValue)
                {
                    AssertSmallStep(prevFL.Value, r.BottomFrontLeft.Value);
                    AssertSmallStep(prevRL.Value, r.BottomRearLeft.Value);
                    AssertSmallStep(prevLL.Value, r.BackLowLeft.Value);
                    AssertSmallStep(prevTL.Value, r.BackTopLeft.Value);
                }
                prevFL = r.BottomFrontLeft; prevRL = r.BottomRearLeft;
                prevLL = r.BackLowLeft; prevTL = r.BackTopLeft;
            }
        }

        [Fact]
        public void No_step_change_as_braking_settles_past_saturation_with_default_sustain()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 500; i++) engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax); // pre-settle

            double? prev = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = -1.0 - i * 0.003; // slowly ramps from -1.0 past -2.0 (saturation) and beyond
                var r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                if (prev.HasValue) AssertSmallStep(prev.Value, r.BottomRearLeft.Value);
                prev = r.BottomRearLeft;
            }
        }

        /// <summary>
        /// CLOSES A COVERAGE GAP the re-derivation surfaced: the pre-existing continuity test above
        /// only ever swept BOTTOM REAR under BRAKING - it never independently exercised BACK LOW
        /// (braking) or EITHER pad under the ACCELERATION chain, so it could not by itself have
        /// distinguished "continuity holds because Max-of-continuous-functions is structurally
        /// guaranteed for ANY floor value" from "continuity only happened to hold because every floor
        /// was coincidentally 50%". These three tests close that gap directly, at the NEW,
        /// asymmetric 25%/50% defaults - continuity is unaffected by the specific floor value (it
        /// follows from Max(hat, floor*terminal) being a Max of two continuous functions, structurally,
        /// regardless of what the floor constant is), and these tests now prove that for real rather
        /// than leaving it an untested coincidence.
        /// </summary>
        [Fact]
        public void No_step_change_in_Back_Low_as_braking_settles_past_saturation_with_the_new_25_percent_floor()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 500; i++) engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax);

            double? prev = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = -1.0 - i * 0.003;
                var r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                if (prev.HasValue) AssertSmallStep(prev.Value, r.BackLowLeft.Value);
                prev = r.BackLowLeft;
            }
        }

        [Fact]
        public void No_step_change_in_Back_Low_or_Bottom_Rear_as_acceleration_settles_past_saturation()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 500; i++) engine.Compute(Sample(0.5, 0.02), AccelMax, DecelMax);

            double? prevBackLow = null, prevBottomRear = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = 0.5 + i * 0.003; // ramps past saturation (accelMaxG = 0.9) and beyond
                var r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                if (prevBackLow.HasValue)
                {
                    AssertSmallStep(prevBackLow.Value, r.BackLowLeft.Value);
                    AssertSmallStep(prevBottomRear.Value, r.BottomRearLeft.Value);
                }
                prevBackLow = r.BackLowLeft;
                prevBottomRear = r.BottomRearLeft;
            }
        }

        private static void AssertSmallStep(double previous, double current)
        {
            Assert.True(Math.Abs(current - previous) <= 8.0,
                $"Discontinuity detected: {previous} -> {current} (delta {current - previous})");
        }

        // ---------------------------------------------------------------------------------------
        // THE SIX ACCEPTANCE SCENARIOS (the owner's own, verbatim from the wiring brief).
        // ---------------------------------------------------------------------------------------

        /// <summary>S1: Gentle onset to 1g - transient travels Back Low -> Bottom Rear -> Bottom
        /// Front, amplitude appropriate to the modest rate. Verified via the TransientGain=0 twin
        /// technique (see class remarks): the gap between the real engine and its zero-gain twin,
        /// fed an identical GENTLE ramp, stays small throughout.</summary>
        [Fact]
        public void S1_gentle_onset_produces_a_small_transient_appropriate_to_the_modest_rate()
        {
            double maxGap = MeasureGentleRampPeakGap(rampSeconds: 3.0);

            Assert.True(maxGap > 0.0, "a gentle onset should still produce SOME measurable transient");
            Assert.True(maxGap < 10.0, $"a gentle onset's transient should stay modest, got a peak gap of {maxGap}");
        }

        /// <summary>Ramps longG from 0 to -1.0 (0g to 1g braking) over <paramref name="rampSeconds"/>,
        /// returning the peak gap between the real engine and its TransientGain=0 twin (see class
        /// remarks) - the transient's own peak contribution during the ramp.</summary>
        private static double MeasureGentleRampPeakGap(double rampSeconds)
        {
            var real = new GForceEngine();
            var twin = new GForceEngine { TransientGain = 0.0 };

            const double dt = 0.02;
            int steps = (int)(rampSeconds / dt);
            double maxGap = 0.0;
            for (int i = 1; i <= steps; i++)
            {
                double longG = -1.0 * i / steps;
                var rReal = real.Compute(Sample(longG, dt), AccelMax, DecelMax);
                var rTwin = twin.Compute(Sample(longG, dt), AccelMax, DecelMax);
                maxGap = Math.Max(maxGap, Math.Abs(rReal.BottomFrontLeft.Value - rTwin.BottomFrontLeft.Value));
            }
            return maxGap;
        }

        /// <summary>S2: Hold ~1g for 0.5s - the transient decays toward zero and the output settles to
        /// the sustained distribution.</summary>
        [Fact]
        public void S2_holding_steady_G_washes_the_transient_out_to_the_sustained_distribution()
        {
            var real = new GForceEngine();
            var twin = new GForceEngine { TransientGain = 0.0 };

            // A quick-ish rise to 1g (to create an initial transient)...
            for (int i = 1; i <= 20; i++)
            {
                double longG = -1.0 * i / 20.0;
                real.Compute(Sample(longG, 0.01), AccelMax, DecelMax);
                twin.Compute(Sample(longG, 0.01), AccelMax, DecelMax);
            }

            // ...then hold flat at -1.0 for 0.5s (50 steps @ dt=0.01).
            GForceOutput rReal = null, rTwin = null;
            for (int i = 0; i < 50; i++)
            {
                rReal = real.Compute(Sample(-1.0, 0.01), AccelMax, DecelMax);
                rTwin = twin.Compute(Sample(-1.0, 0.01), AccelMax, DecelMax);
            }

            double gapAfterHold = Math.Abs(rReal.BottomFrontLeft.Value - rTwin.BottomFrontLeft.Value);
            // Per the scenario's own wording ("small residual changes still produce small, gentle
            // transitions - we can feel it, but not that obvious") a fully-settled hold is not required
            // to reach a mathematically perfect zero - only small. 5.0 (out of 100) is comfortably
            // "not obvious" while still meaningfully tighter than the transient's own peak during the
            // preceding onset (verified elsewhere - see S1/S3's much larger peak gaps).
            Assert.True(gapAfterHold < 5.0, $"the transient should have washed out to something small after a 0.5s hold, gap was {gapAfterHold}");
        }

        /// <summary>S3: Step 1g -> 2g quickly - a large, obvious front-ward transition even though the
        /// system had already settled, then it settles into the NEW, higher sustained distribution.</summary>
        [Fact]
        public void S3_a_fast_step_produces_a_large_transient_that_then_settles_into_the_new_level()
        {
            var real = new GForceEngine();
            var twin = new GForceEngine { TransientGain = 0.0 };

            // Settle at 1g first.
            for (int i = 0; i < 300; i++)
            {
                real.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax);
                twin.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax);
            }

            // Fast step to 2g (a single large step, one frame).
            var rRealStep = real.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
            var rTwinStep = twin.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
            double gapAtStep = Math.Abs(rRealStep.BottomFrontLeft.Value - rTwinStep.BottomFrontLeft.Value);

            Assert.True(gapAtStep > 10.0, $"a fast 1g step should produce an obvious transient, got a gap of {gapAtStep}");

            // Compare directly against the SAME 1g change delivered gently over 3 seconds (S1) - the
            // step's gap must be clearly larger, proving "large, obvious" vs "modest" for the identical
            // underlying magnitude change.
            double gentleGap = MeasureGentleRampPeakGap(rampSeconds: 3.0);
            Assert.True(gapAtStep > gentleGap,
                $"a fast step (gap {gapAtStep}) should produce a clearly larger transient than the same " +
                $"change spread gently over 3s (gap {gentleGap})");

            // Then let it settle at the new level for a long time - the gap should collapse again.
            GForceOutput rRealSettled = null, rTwinSettled = null;
            for (int i = 0; i < 400; i++)
            {
                rRealSettled = real.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
                rTwinSettled = twin.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
            }
            double gapSettled = Math.Abs(rRealSettled.BottomFrontLeft.Value - rTwinSettled.BottomFrontLeft.Value);
            Assert.True(gapSettled < 1.0, $"should have settled into the new sustained level, gap was {gapSettled}");
            Assert.Equal(100.0, rTwinSettled.BottomFrontLeft.Value, 1); // the new (saturated) sustained level
        }

        /// <summary>S4: Max G configured 1.5g, actual 2g - all three pads sit at their configured
        /// maxima (sustained path saturated).</summary>
        [Fact]
        public void S4_exceeding_the_configured_maximum_saturates_all_three_pads_at_their_maxima()
        {
            // Configured max 1.5g, actual (steady) 2g -> r = 2/1.5 = 1.333, clamped to fully saturated.
            var engine = new GForceEngine();
            GForceOutput result = null;
            for (int i = 0; i < 400; i++) result = engine.Compute(Sample(-2.0, 0.05), AccelMax, decelMaxG: 1.5);

            Assert.Equal(100.0, result.BottomFrontLeft.Value, 1);
            Assert.Equal(50.0, result.BottomRearLeft.Value, 1);
            Assert.Equal(25.0, result.BackLowLeft.Value, 1);
        }

        /// <summary>S5: Already saturated, 2g -> 3g - a clearly visible transient STILL occurs,
        /// spending the headroom above the sustain floors (Back Low/Bottom Rear, both well below
        /// 100%, briefly rise well above their sustained floor; Bottom Front, already at 100%, has no
        /// headroom left and cannot rise further).</summary>
        [Fact]
        public void S5_a_transient_while_already_saturated_spends_the_headroom_above_the_sustain_floors()
        {
            const double decelMaxG = 1.5;
            var engine = new GForceEngine();

            // Settle fully saturated at 2g (r = 2/1.5 = 1.333, clamped to 1.0 sustained).
            for (int i = 0; i < 400; i++) engine.Compute(Sample(-2.0, 0.02), AccelMax, decelMaxG);
            var beforeStep = engine.Compute(Sample(-2.0, 0.02), AccelMax, decelMaxG);
            Assert.Equal(50.0, beforeStep.BottomRearLeft.Value, 1);
            Assert.Equal(25.0, beforeStep.BackLowLeft.Value, 1);
            Assert.Equal(100.0, beforeStep.BottomFrontLeft.Value, 1);

            // Fast step to 3g (r = 3/1.5 = 2.0) - well beyond saturation, but the UNCLAMPED gap still
            // drives a real transient.
            var afterStep = engine.Compute(Sample(-3.0, 0.02), AccelMax, decelMaxG);

            Assert.True(afterStep.BottomRearLeft.Value > beforeStep.BottomRearLeft.Value + 5.0,
                $"Bottom Rear should rise above its saturated sustain floor using its headroom, was {beforeStep.BottomRearLeft.Value} now {afterStep.BottomRearLeft.Value}");
            Assert.True(afterStep.BackLowLeft.Value > beforeStep.BackLowLeft.Value + 5.0,
                $"Back Low should rise above its saturated sustain floor using its headroom, was {beforeStep.BackLowLeft.Value} now {afterStep.BackLowLeft.Value}");
            // Bottom Front has (almost) no headroom left - it cannot meaningfully rise above 100 (clamped).
            Assert.True(afterStep.BottomFrontLeft.Value <= 100.0);
            Assert.True(afterStep.BottomFrontLeft.Value >= beforeStep.BottomFrontLeft.Value - 0.5);

            // TIGHT, headroom-specific check - this is what actually catches mutation (b) (headroom
            // scaling removed): independently computed (see docs\wiring-ui-report.md's simulation),
            // Bottom Rear's headroom-scaled value after this exact step is ~59.7 (50% sustain + 50%
            // headroom * ~19.4% drive). Without headroom scaling the SAME drive would be added
            // unscaled, landing around ~69.4 instead - well outside this tight range.
            Assert.InRange(afterStep.BottomRearLeft.Value, 57.0, 62.0);
        }

        /// <summary>S6: F1 aerodynamic bleed-off - braking G decreasing slowly and continuously - only
        /// a SMALL transition, but a CONTINUOUS one (not a single discrete event): the gap between the
        /// real engine and its zero-gain twin stays small but consistently NONZERO across the whole
        /// slow decrease, not just at one moment.</summary>
        [Fact]
        public void S6_a_slow_continuous_bleed_off_produces_a_small_but_sustained_continuous_transient()
        {
            var real = new GForceEngine();
            var twin = new GForceEngine { TransientGain = 0.0 };

            // Settle at 2g first.
            for (int i = 0; i < 300; i++)
            {
                real.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
                twin.Compute(Sample(-2.0, 0.02), AccelMax, DecelMax);
            }

            // Slowly, continuously bleed off from 2g toward 1g over 5 seconds (250 steps @ dt=0.02).
            int nonTrivialGapCount = 0;
            const int bleedSteps = 250;
            for (int i = 1; i <= bleedSteps; i++)
            {
                double longG = -2.0 + (1.0 * i / bleedSteps); // -2.0 -> -1.0
                var rReal = real.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                var rTwin = twin.Compute(Sample(longG, 0.02), AccelMax, DecelMax);
                double gap = Math.Abs(rReal.BottomFrontLeft.Value - rTwin.BottomFrontLeft.Value);

                Assert.True(gap < 15.0, $"a slow bleed-off must stay a SMALL transient, got {gap} at step {i}");
                // Sampled well after start (filters need a moment to reflect the new, slower rate).
                if (i > 20 && gap > 0.05) nonTrivialGapCount++;
            }

            Assert.True(nonTrivialGapCount > bleedSteps / 2,
                "the transient should be present continuously through most of the slow bleed-off, " +
                $"not just briefly - only {nonTrivialGapCount}/{bleedSteps} steps showed a non-trivial gap");
        }

        // ---------------------------------------------------------------------------------------
        // MUTATION EVIDENCE (see docs\wiring-ui-report.md for the actual mutation runs) - these three
        // tests are the ones that must fail under mutations (a)/(b)/(c) respectively.
        // (a) removing the high-pass/transient path (magnitude-only) -> S1, S3 and S6 fail.
        // (b) removing headroom scaling -> S5 fails.
        // (c) removing washout (transient never decays) -> S2 fails.
        // ---------------------------------------------------------------------------------------
    }
}
