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
    /// TransientTimeConstantSeconds=0.15, TransientGain=1.2 (changed from 1.5 - owner's own hardware
    /// testing, see <see cref="GForceEngine.TransientGain"/>'s remarks), all four sustain fractions=0.5,
    /// LateralReferenceG=1.6, LateralBiasGain=0.5, LateralDirection=Normal. Default maxima:
    /// accelMaxG=0.9, decelMaxG=2.0.
    /// <para/>
    /// DIRECTION FIX (docs\gforce-direction-fix-report.md - read this before touching the sample
    /// helpers below): <see cref="GForceEngine"/> no longer takes which chain is active from
    /// LongitudinalG's own sign - it comes ONLY from measured ground-speed direction (see
    /// <see cref="BrakingSample"/>/<see cref="ThrottleSample"/> below), exactly like
    /// <c>NormalizedWheelLockSlipEngineTests</c>' own <c>BrakingSample</c>/<c>ThrottleSample</c>
    /// helpers already do for that sibling engine. Every test below that needs a specific chain
    /// active now supplies real Old/New ground speed via those two helpers instead of relying on
    /// LongitudinalG's sign alone - a faithful adaptation of the SAME test intent (magnitude and
    /// expected chain are unchanged from before), not a change to what any test asserts. None of
    /// these pre-existing assertions themselves encoded the old, wrong sign-based assumption (they
    /// are calibration/shape/continuity checks that remain equally valid under either direction
    /// source) - the NEW, dedicated inverted-convention test below is what specifically proves the
    /// fix, and a genuine-standstill test proves the "no chain active when Unknown" gate.
    /// </summary>
    public class GForceEngineTests
    {
        private const double AccelMax = 0.9;
        private const double DecelMax = 2.0;

        /// <summary>Bare frame, no ground speed at all - used only where LongitudinalG itself is
        /// null/irrelevant to direction (the missing-data/lateral-only-fallback tests) or where the
        /// magnitude is exactly zero (direction cannot matter). Kept for tests that rely on the
        /// resolver HOLDING whatever direction a preceding <see cref="BrakingSample"/>/
        /// <see cref="ThrottleSample"/> ramp already established (Dt/speed both absent here -&gt;
        /// <c>LongitudinalDirectionResolver.Resolve</c> holds its previous state rather than
        /// guessing - see that class's own remarks).</summary>
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

        /// <summary>Ground speed FALLING (old 101 -&gt; new 100 km/h) - resolves
        /// <c>LongitudinalMotionState.Slowing</c> from the very first frame regardless of
        /// <paramref name="dtSeconds"/> (the fixed 1 km/h delta clears
        /// <c>LongitudinalDirectionResolver</c>'s own dead band after a single EMA step for every dt
        /// used in this file). <paramref name="longitudinalGMagnitude"/> is fed as LongitudinalG's
        /// MAGNITUDE (sign no longer matters to the engine at all - kept negative here purely so a
        /// reader can still eyeball "braking" from the fixture, matching this file's old convention);
        /// see <see cref="Inverted_convention_title_still_drives_braking_on_bottom_front_when_the_car_is_measurably_slowing"/>
        /// for the dedicated proof that the engine is correct even when a title's sign is the OPPOSITE
        /// of what is used here.</summary>
        /// <summary>ANIMATION DIRECTION SELECTION (docs\lock-and-animation-report.md): the owner's own
        /// rules require the brake pedal actually applied for the deceleration animation, and
        /// SpeedingUp direction PLUS the throttle pedal applied for the acceleration one - a bare
        /// ground-speed ramp with no pedal reading is now "coasting", not "braking"/"accelerating".
        /// <paramref name="brakePercent"/> defaults to a firmly-committed 80%, mirroring
        /// <c>NormalizedWheelLockSlipEngineTests</c>' own identically-named helper's own default, so
        /// every pre-existing call site represents genuine, sustained braking (not coasting) unless a
        /// test explicitly overrides it.</summary>
        private static TelemetrySample BrakingSample(double longitudinalGMagnitude, double dtSeconds, double? latG = null, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -longitudinalGMagnitude, lateralG: latG, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        /// <summary>The mirror of <see cref="BrakingSample"/>: ground speed RISING (old 100 -&gt; new
        /// 101 km/h) - resolves <c>LongitudinalMotionState.SpeedingUp</c> from the first frame. Throttle
        /// defaults to a firmly-committed 80% - see <see cref="BrakingSample"/>'s own remarks.</summary>
        private static TelemetrySample ThrottleSample(double longitudinalGMagnitude, double dtSeconds, double? latG = null, double throttlePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 101.0, longitudinalG: longitudinalGMagnitude, lateralG: latG, throttlePercent: throttlePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        /// <summary>Preserves this file's old "sign of longG implies direction, magnitude is the
        /// absolute value" convention at the TEST-FIXTURE level only (never inside the engine itself
        /// any more - see this class's own remarks): negative -&gt; <see cref="BrakingSample"/>,
        /// positive -&gt; <see cref="ThrottleSample"/>, exactly zero -&gt; direction cannot matter
        /// (magnitude is 0 either way), so the bare <see cref="Sample"/> is used instead.</summary>
        private static TelemetrySample SampleForLongG(double longG, double dtSeconds, double? latG = null)
        {
            if (longG < 0.0) return BrakingSample(-longG, dtSeconds, latG);
            if (longG > 0.0) return ThrottleSample(longG, dtSeconds, latG);
            return Sample(0.0, dtSeconds, latG);
        }

        /// <summary>Feeds a constant longG at a constant dt for enough steps to fully converge both
        /// washout filters (tens of time constants) and returns the final output.</summary>
        private static GForceOutput RunToSteadyState(GForceEngine engine, double longG, double dtSeconds = 0.05, int steps = 400)
        {
            GForceOutput last = null;
            for (int i = 0; i < steps; i++) last = engine.Compute(SampleForLongG(longG, dtSeconds), AccelMax, DecelMax);
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
            var before = engine.Compute(SampleForLongG(-2.0, 0.05), AccelMax, DecelMax);

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
                    var r = engine.Compute(SampleForLongG(longG, 0.02, latG), AccelMax, DecelMax);
                    AssertInBounds(r.BottomFrontLeft); AssertInBounds(r.BottomFrontRight);
                    AssertInBounds(r.BottomRearLeft); AssertInBounds(r.BottomRearRight);
                    AssertInBounds(r.BackLowLeft); AssertInBounds(r.BackLowRight);
                    AssertInBounds(r.BackTopLeft); AssertInBounds(r.BackTopRight);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Steady-state sustained distribution (docs\lock-and-animation-report.md - STAGED TRAVEL
        // MODEL). SUPERSEDES the previous pass's "continuous hat position" test below: the owner's own
        // specification is that the SUSTAIN state is always the FULL three-pad stage-3 shape
        // (terminal=100%/mid=configured MID%/far=configured LOW%) once the sweep has settled, with ONLY
        // the overall magnitude (never the shape) scaled by how hard the car is braking/accelerating -
        // "sustain the final distribution while acceleration continues", not "the wave stops partway
        // through the chain at partial G". A mid-level, steady brake therefore settles on ALL THREE
        // pads (scaled by 50%), not a single pad in isolation - the old assertion (BottomRear alone at
        // its peak, the other two at exactly 0) was a direct consequence of the now-retired continuous
        // hat-position mechanism and no longer holds.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Steady_mid_level_braking_settles_on_the_full_stage3_shape_scaled_by_the_sustain_level()
        {
            var r = RunToSteadyState(new GForceEngine(), -1.0); // r = 1.0/2.0 = 0.5 (sustain level)
            // Stage-3 shape (LOW=25%, MID=50%, HIGH=100%) scaled by the 0.5 sustain level.
            Assert.Equal(12.5, r.BackLowLeft.Value, 1);
            Assert.Equal(25.0, r.BottomRearLeft.Value, 1);
            Assert.Equal(50.0, r.BottomFrontLeft.Value, 1);
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
                r1 = e1.Compute(SampleForLongG(-1.0, 0.05, 1.6), AccelMax, DecelMax);
                r2 = e2.Compute(SampleForLongG(-1.0, 0.05, 1.6), AccelMax, DecelMax);
            }

            Assert.Equal(r1.BottomRearLeft.Value, r2.BottomRearRight.Value, 3);
            Assert.Equal(r1.BottomRearRight.Value, r2.BottomRearLeft.Value, 3);
        }

        [Fact]
        public void Full_scale_lateral_G_biases_toward_the_right_channels_at_steady_state()
        {
            var engine = new GForceEngine();
            GForceOutput r = null;
            for (int i = 0; i < 400; i++) r = engine.Compute(SampleForLongG(-1.0, 0.05, 1.6), AccelMax, DecelMax);

            Assert.True(r.BottomRearRight.Value > r.BottomRearLeft.Value);
        }

        [Fact]
        public void Negative_lateral_G_mirrors_the_bias_to_the_left_channels_at_steady_state()
        {
            var engine = new GForceEngine();
            GForceOutput r = null;
            for (int i = 0; i < 400; i++) r = engine.Compute(SampleForLongG(-1.0, 0.05, -1.6), AccelMax, DecelMax);

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
            for (int i = 0; i < 500; i++) engine.Compute(SampleForLongG(-2.2, 0.02), AccelMax, DecelMax); // pre-settle

            double? prevFL = null, prevRL = null, prevLL = null, prevTL = null;

            for (double longG = -2.2; longG <= 1.0; longG += 0.01)
            {
                var r = engine.Compute(SampleForLongG(longG, 0.02), AccelMax, DecelMax);
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
            for (int i = 0; i < 500; i++) engine.Compute(SampleForLongG(-1.0, 0.02), AccelMax, DecelMax); // pre-settle

            double? prev = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = -1.0 - i * 0.003; // slowly ramps from -1.0 past -2.0 (saturation) and beyond
                var r = engine.Compute(SampleForLongG(longG, 0.02), AccelMax, DecelMax);
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
            for (int i = 0; i < 500; i++) engine.Compute(SampleForLongG(-1.0, 0.02), AccelMax, DecelMax);

            double? prev = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = -1.0 - i * 0.003;
                var r = engine.Compute(SampleForLongG(longG, 0.02), AccelMax, DecelMax);
                if (prev.HasValue) AssertSmallStep(prev.Value, r.BackLowLeft.Value);
                prev = r.BackLowLeft;
            }
        }

        [Fact]
        public void No_step_change_in_Back_Low_or_Bottom_Rear_as_acceleration_settles_past_saturation()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 500; i++) engine.Compute(SampleForLongG(0.5, 0.02), AccelMax, DecelMax);

            double? prevBackLow = null, prevBottomRear = null;
            for (int i = 0; i < 400; i++)
            {
                double longG = 0.5 + i * 0.003; // ramps past saturation (accelMaxG = 0.9) and beyond
                var r = engine.Compute(SampleForLongG(longG, 0.02), AccelMax, DecelMax);
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
        // THE STAGED TRAVEL MODEL (docs\lock-and-animation-report.md) - the driver's own explicit
        // restructure ("the chains are now correct but the driver does not FEEL the travel"). S2 and S4
        // (below) are RE-VERIFIED, still valid, under the new model. S1/S3/S6 are RE-WRITTEN using
        // direct sweep-speed/shape measurements instead of the old "gap against a TransientGain=0 twin"
        // technique (the twin isolated an ADDITIVE transient bump that no longer exists - the entire
        // output IS the staged sweep now). S5 ("a transient while already saturated spends the headroom
        // above the sustain floors") is EXPLICITLY SUPERSEDED and removed: once the stage progress has
        // fully swept AND the sustain level is itself already at 1.0, there is nothing further to show -
        // the owner's own specification calls only for DELTA-driven travel and G-driven sustain scaling,
        // neither of which describes a residual bump while both are already at their own ceiling.
        // ---------------------------------------------------------------------------------------

        /// <summary>Feeds a constant longG (a "cold start already at this level") for enough frames to
        /// fully converge, returning the number of frames it took the terminal pad (Bottom Front) to
        /// first reach within 1% of its own final value - a direct measurement of sweep SPEED.</summary>
        /// <summary>Measures how many frames it takes the braking chain's own SHAPE (the terminal
        /// pad's share of the three-pad total, <c>BottomFront / (BottomFront+BottomRear+BackLow)</c>) to
        /// settle within 1% of its final value - deliberately a RATIO, not a raw pad magnitude, since
        /// the ratio cancels out the (SAME-tau-regardless-of-delta-size) sustain LEVEL entirely and
        /// isolates the STAGE PROGRESS's own convergence speed, which is what this test is actually
        /// about (see <see cref="A_large_delta_produces_a_faster_sweep_than_a_small_delta"/>'s own
        /// remarks - a raw-magnitude measurement would be dominated by the sustain level's own
        /// convergence, which is identical for both cases and would swamp the very difference being
        /// measured).</summary>
        private static int MeasureFramesToSettle(GForceEngine engine, double longG, double dtSeconds = 0.02, int maxSteps = 2000)
        {
            // Stage-3/sustain shares: terminal=HIGH(1.0), mid=MID(0.5), far=LOW(0.25) -> terminal's own
            // share of the three-pad total = 1.0/(1.0+0.5+0.25) = 0.5714.
            double finalShare = 1.0 / 1.75;

            double sustainSum(GForceOutput r) => r.BottomFrontLeft.Value + r.BottomRearLeft.Value + r.BackLowLeft.Value;
            double shareOf(GForceOutput r) => sustainSum(r) > 1e-9 ? r.BottomFrontLeft.Value / sustainSum(r) : 0.0;

            for (int i = 0; i < maxSteps; i++)
            {
                var r = engine.Compute(SampleForLongG(longG, dtSeconds), AccelMax, DecelMax);
                if (Math.Abs(shareOf(r) - finalShare) < 0.01) return i + 1;
            }
            return maxSteps;
        }

        /// <summary>S1/S3 (rewritten): a large, sudden delta (a fast step onset) must sweep through the
        /// three stages FASTER (fewer frames to settle) than a small, gentle delta (the same magnitude
        /// change, but arriving one small step at a time). Both are "cold start" (constant longG fed
        /// from frame 1, so BOTH represent an instantaneous onset in TERMS OF DELTA SIZE per frame - a
        /// large one-frame delta vs. a small one-frame delta - matching the owner's own wording
        /// ("stamping the throttle from rest is a large delta... a gentle change is a small delta").
        /// <para/>
        /// MUTATION (a) target (see GForceEngine.AdvanceStageProgress's own remarks): driving the sweep
        /// from magnitude instead of delta collapses this distinction entirely (both would settle at
        /// the same, magnitude-only-driven speed) - this test is what catches that.</summary>
        [Fact]
        public void A_large_delta_produces_a_faster_sweep_than_a_small_delta()
        {
            int framesForLargeDelta = MeasureFramesToSettle(new GForceEngine(), -2.0); // 0->1.0 ratio in one frame
            int framesForSmallDelta = MeasureFramesToSettle(new GForceEngine(), -0.2); // 0->0.1 ratio in one frame

            Assert.True(framesForLargeDelta < framesForSmallDelta,
                $"a large delta should sweep faster than a small one: large took {framesForLargeDelta} frames, small took {framesForSmallDelta}");
        }

        /// <summary>S2 (re-verified, still valid under the new model): holding steady G settles the
        /// output onto the sustained (stage-3) distribution and stays there.</summary>
        [Fact]
        public void S2_holding_steady_G_settles_onto_the_sustained_distribution_and_holds()
        {
            var engine = new GForceEngine();
            GForceOutput settled = null;
            for (int i = 0; i < 300; i++) settled = engine.Compute(SampleForLongG(-1.0, 0.02), AccelMax, DecelMax);

            // Hold for another 0.5s (25 steps @ dt=0.02) - output must stay essentially unchanged.
            for (int i = 0; i < 25; i++)
            {
                var r = engine.Compute(SampleForLongG(-1.0, 0.02), AccelMax, DecelMax);
                Assert.True(Math.Abs(r.BottomFrontLeft.Value - settled.BottomFrontLeft.Value) < 1.0,
                    $"output should stay settled while holding steady G, drifted to {r.BottomFrontLeft.Value} from {settled.BottomFrontLeft.Value}");
            }
        }

        /// <summary>S4 (unchanged, still valid): Max G configured 1.5g, actual 2g - all three pads sit
        /// at their configured (stage-3, fully saturated) maxima.</summary>
        [Fact]
        public void S4_exceeding_the_configured_maximum_saturates_all_three_pads_at_their_maxima()
        {
            var engine = new GForceEngine();
            GForceOutput result = null;
            for (int i = 0; i < 400; i++) result = engine.Compute(SampleForLongG(-2.0, 0.05), AccelMax, decelMaxG: 1.5);

            Assert.Equal(100.0, result.BottomFrontLeft.Value, 1);
            Assert.Equal(50.0, result.BottomRearLeft.Value, 1);
            Assert.Equal(25.0, result.BackLowLeft.Value, 1);
        }

        /// <summary>S6 (rewritten): a slow, continuous bleed-off produces a smooth, continuous,
        /// proportional reduction of all three pads (never jumping) - the driver's own "G falling while
        /// still in the same direction scales the whole distribution proportionally" rule, exercised
        /// continuously rather than at a single instant (see the dedicated verbatim-example test below
        /// for the exact numeric proof of the proportional scaling itself).</summary>
        [Fact]
        public void S6_a_slow_continuous_bleed_off_produces_a_smooth_continuous_reduction_with_no_jumps()
        {
            var engine = new GForceEngine();
            for (int i = 0; i < 300; i++) engine.Compute(SampleForLongG(-2.0, 0.02), AccelMax, DecelMax);

            double? prev = null;
            const int bleedSteps = 250;
            for (int i = 1; i <= bleedSteps; i++)
            {
                double longG = -2.0 + (1.0 * i / bleedSteps); // -2.0 -> -1.0, gently
                var r = engine.Compute(SampleForLongG(longG, 0.02), AccelMax, DecelMax);
                if (prev.HasValue) AssertSmallStep(prev.Value, r.BottomFrontLeft.Value);
                prev = r.BottomFrontLeft.Value;
            }

            Assert.True(prev.Value < 100.0, "the bleed-off should have reduced the terminal pad below full saturation");
        }

        // ---------------------------------------------------------------------------------------
        // THE THREE STAGES - explicit ordering, both directions (docs\lock-and-animation-report.md).
        // MUTATION (b) target: collapsing StagedShape to a single stage (always returning the stage-3
        // shape) would fail these ordering checks.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Braking_chain_sweeps_far_to_mid_to_terminal_in_order()
        {
            // Very gentle onset (small delta every frame) so the sweep advances slowly enough to
            // sample distinct stages.
            var engine = new GForceEngine();
            const double dt = 0.02;
            double? farAtStage0 = null, midAtStage0 = null, terminalAtStage0 = null;
            bool sawMidLeading = false;

            for (int i = 1; i <= 300; i++)
            {
                double longG = -2.0 * Math.Min(1.0, i / 250.0);
                var r = engine.Compute(SampleForLongG(longG, dt), AccelMax, DecelMax);

                if (i == 2)
                {
                    farAtStage0 = r.BackLowLeft.Value;
                    midAtStage0 = r.BottomRearLeft.Value;
                    terminalAtStage0 = r.BottomFrontLeft.Value;
                }
                if (r.BottomRearLeft.Value > r.BackLowLeft.Value && r.BottomRearLeft.Value > r.BottomFrontLeft.Value)
                    sawMidLeading = true;
            }

            // Stage 1 (far leads): Back Low (far) reads highest of the three, early in the sweep.
            Assert.True(farAtStage0.Value >= midAtStage0.Value && farAtStage0.Value >= terminalAtStage0.Value,
                $"far pad should lead at the very start of the sweep: far={farAtStage0} mid={midAtStage0} terminal={terminalAtStage0}");
            // Stage 2 (mid leads): Bottom Rear reads highest of the three at some point during the sweep.
            Assert.True(sawMidLeading, "the middle pad should lead at some point during the sweep");
        }

        [Fact]
        public void Acceleration_chain_sweeps_far_to_mid_to_terminal_in_order()
        {
            var engine = new GForceEngine();
            const double dt = 0.02;
            double? farAtStage0 = null, midAtStage0 = null, terminalAtStage0 = null;
            bool sawMidLeading = false;

            for (int i = 1; i <= 300; i++)
            {
                double longG = 0.9 * Math.Min(1.0, i / 250.0);
                var r = engine.Compute(SampleForLongG(longG, dt), AccelMax, DecelMax);

                if (i == 2)
                {
                    farAtStage0 = r.BottomRearLeft.Value;
                    midAtStage0 = r.BackLowLeft.Value;
                    terminalAtStage0 = r.BackTopLeft.Value;
                }
                if (r.BackLowLeft.Value > r.BottomRearLeft.Value && r.BackLowLeft.Value > r.BackTopLeft.Value)
                    sawMidLeading = true;
            }

            Assert.True(farAtStage0.Value >= midAtStage0.Value && farAtStage0.Value >= terminalAtStage0.Value,
                $"far pad (Bottom Rear) should lead at the very start of the sweep: far={farAtStage0} mid={midAtStage0} terminal={terminalAtStage0}");
            Assert.True(sawMidLeading, "the middle pad (Back Low) should lead at some point during the sweep");
        }

        [Fact]
        public void The_sweep_never_steps_discontinuously()
        {
            var engine = new GForceEngine();
            const double dt = 0.02;
            double? prevFar = null, prevMid = null, prevTerminal = null;

            for (int i = 1; i <= 300; i++)
            {
                double longG = -2.0 * Math.Min(1.0, i / 200.0);
                var r = engine.Compute(SampleForLongG(longG, dt), AccelMax, DecelMax);
                if (prevFar.HasValue)
                {
                    AssertSmallStep(prevFar.Value, r.BackLowLeft.Value);
                    AssertSmallStep(prevMid.Value, r.BottomRearLeft.Value);
                    AssertSmallStep(prevTerminal.Value, r.BottomFrontLeft.Value);
                }
                prevFar = r.BackLowLeft.Value;
                prevMid = r.BottomRearLeft.Value;
                prevTerminal = r.BottomFrontLeft.Value;
            }
        }

        // ---------------------------------------------------------------------------------------
        // FALLING G SCALES THE WHOLE DISTRIBUTION PROPORTIONALLY - the owner's own verbatim worked
        // example (TopBack 90->60 = 100% of the change, LowBack 45->30 = 50%, BottomRear 22.5->15 =
        // 25%), reproduced exactly using the default sustain fractions (MID=50%, LOW=25%).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_falling_G_in_the_same_direction_scales_the_whole_distribution_by_the_sustain_ratios()
        {
            var engine = new GForceEngine();

            // Settle fully staged and sustained at 0.9g (accelMaxG=0.9 -> ratio 1.0, terminal=100%=90%
            // of... wait, use a max that makes the terminal read exactly 90 at full sustain: accelMax
            // such that ratio=0.9 gives terminal=90.
            const double accelMax = 1.0;
            for (int i = 0; i < 400; i++) engine.Compute(ThrottleSample(0.9, 0.02), accelMax, DecelMax);
            var before = engine.Compute(ThrottleSample(0.9, 0.02), accelMax, DecelMax);

            Assert.Equal(90.0, before.BackTopLeft.Value, 0);
            Assert.Equal(45.0, before.BackLowLeft.Value, 0);
            Assert.Equal(22.5, before.BottomRearLeft.Value, 1);

            // G falls to 0.6 (still SpeedingUp/accelerating, same direction) - hold long enough for the
            // (low-pass) sustain level to fully settle at the new, lower value; the stage progress is
            // already at 1.0 (fully swept) and stays there (still engaged).
            GForceOutput after = null;
            for (int i = 0; i < 400; i++) after = engine.Compute(ThrottleSample(0.6, 0.02), accelMax, DecelMax);

            Assert.Equal(60.0, after.BackTopLeft.Value, 0);
            Assert.Equal(30.0, after.BackLowLeft.Value, 0);
            Assert.Equal(15.0, after.BottomRearLeft.Value, 1);
        }

        // ---------------------------------------------------------------------------------------
        // COASTING (docs\lock-and-animation-report.md) - neither pedal pressed: a large
        // deceleration-direction delta (engine braking / a forced downshift) still runs the
        // deceleration animation; a small, steady one (ordinary rolling resistance) produces NO cue.
        // MUTATION (c) target: removing the coasting dead-band check must fail the "no cue" test.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Coasting_with_a_small_steady_deceleration_produces_no_cue_at_all()
        {
            var engine = new GForceEngine();
            // Slowing, genuinely (ground speed falling), but NEITHER pedal pressed, and the magnitude
            // itself is small and held CONSTANT (no meaningful delta at all after the first frame) -
            // ordinary rolling/aero drag.
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -0.05);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(20));

            GForceOutput result = null;
            for (int i = 0; i < 200; i++) result = engine.Compute(sample, AccelMax, DecelMax);

            Assert.True(result.BottomFrontLeft.Value < 1.0, $"BottomFrontLeft={result.BottomFrontLeft.Value}");
            Assert.True(result.BottomRearLeft.Value < 1.0, $"BottomRearLeft={result.BottomRearLeft.Value}");
            Assert.True(result.BackLowLeft.Value < 1.0, $"BackLowLeft={result.BackLowLeft.Value}");
        }

        [Fact]
        public void Coasting_with_a_large_deceleration_delta_runs_the_deceleration_animation()
        {
            var engine = new GForceEngine();
            // Neither pedal pressed, but a SUDDEN, large jump in deceleration-direction G - a forced
            // downshift / engine braking kicking in hard while coasting.
            var oldFrame1 = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame1 = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -0.05);
            engine.Compute(new TelemetrySample(newFrame1, oldFrame1, DateTime.UtcNow, TimeSpan.FromMilliseconds(20)), AccelMax, DecelMax);

            var oldFrame2 = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame2 = new TelemetryFrame(groundSpeedKmh: 96.0, longitudinalG: -1.8);
            GForceOutput result = null;
            for (int i = 0; i < 20; i++)
                result = engine.Compute(new TelemetrySample(newFrame2, oldFrame2, DateTime.UtcNow, TimeSpan.FromMilliseconds(20)), AccelMax, DecelMax);

            Assert.True(result.BottomFrontLeft.Value > 5.0,
                $"a large coasting deceleration delta should run the deceleration animation, got {result.BottomFrontLeft.Value}");
        }

        // ---------------------------------------------------------------------------------------
        // DIRECTION FIX (docs\gforce-direction-fix-report.md) - the driver's own complaint: braking
        // and accelerating chains were swapped, and pads shook slightly at a genuine standstill. Both
        // traced to GForceEngine deriving direction from LongitudinalG's own (unverified) sign instead
        // of measured ground-speed direction - fixed by routing through the same
        // LongitudinalDirectionResolver NormalizedWheelLockSlipEngine already uses.
        //
        // MUTATION (a): in Compute, replace the direction-based brakeG/accelG split with the OLD
        // `Math.Max(0.0, -longG.Value)` / `Math.Max(0.0, longG.Value)` sign-based one - the two
        // "Inverted_convention" tests below must fail (this was verified manually: reverting to the
        // sign-based split made both tests fail with the chains swapped, then reverted back to green -
        // see the report for the exact run).
        // ---------------------------------------------------------------------------------------

        /// <summary>ACCEPTANCE: "on a title reporting the INVERTED longitudinal convention the chains
        /// are still the right way round." Ground speed genuinely FALLS (braking) but LongitudinalG is
        /// reported POSITIVE - the exact inverted convention this codebase's own
        /// NormalizedWheelLockSlipEngine already documents for Forza Horizon 6 (positive while
        /// genuinely slowing, 95.8% of qualifying frames). The braking chain (Bottom Front) must still
        /// light up, and the accelerating chain (Back Top) must stay at zero.</summary>
        [Fact]
        public void Inverted_convention_title_still_drives_braking_on_bottom_front_when_the_car_is_measurably_slowing()
        {
            var engine = new GForceEngine();
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 150.0);
            // brakePercent supplied (80%) - ANIMATION direction selection now requires the brake pedal
            // actually applied (docs\lock-and-animation-report.md); this test is about the sign
            // convention, not that new gate, so the pedal must be committed for the chain to be live.
            var newFrame = new TelemetryFrame(groundSpeedKmh: 148.0, longitudinalG: +2.0, brakePercent: 80.0); // inverted sign
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            GForceOutput result = null;
            for (int i = 0; i < 400; i++) result = engine.Compute(sample, AccelMax, DecelMax);

            Assert.True(result.BottomFrontLeft.Value > 50.0,
                $"an inverted LongitudinalG convention must not suppress the braking chain when the car is measurably slowing, got {result.BottomFrontLeft.Value}");
            Assert.Equal(0.0, result.BackTopLeft.Value, 3);
        }

        /// <summary>The mirror of the test above: ground speed genuinely RISES (accelerating) but
        /// LongitudinalG is reported NEGATIVE. The accelerating chain (Back Top) must still light up,
        /// and the braking chain (Bottom Front) must stay at zero.</summary>
        [Fact]
        public void Inverted_convention_title_still_drives_acceleration_on_back_top_when_the_car_is_measurably_speeding_up()
        {
            var engine = new GForceEngine();
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            // throttlePercent supplied (80%) - see the braking mirror test's own remarks on the new
            // pedal-applied gate.
            var newFrame = new TelemetryFrame(groundSpeedKmh: 102.0, longitudinalG: -0.8, throttlePercent: 80.0); // inverted sign
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            GForceOutput result = null;
            for (int i = 0; i < 400; i++) result = engine.Compute(sample, AccelMax, DecelMax);

            Assert.True(result.BackTopLeft.Value > 50.0,
                $"an inverted LongitudinalG convention must not suppress the accelerating chain when the car is measurably speeding up, got {result.BackTopLeft.Value}");
            Assert.Equal(0.0, result.BottomFrontLeft.Value, 3);
        }

        /// <summary>
        /// MUTATION (b) target: the driver's second complaint ("Bottom Rear shaking slightly while
        /// stopped"). A genuine standstill: ground speed constant (well inside
        /// LongitudinalDirectionResolver's own dead band -&gt; Unknown), but LongitudinalG still
        /// carries small, real sensor noise (a nonzero reading with nothing to attribute it to).
        /// EVERY pad must settle at (or very near) zero - not just the terminal ones, the shared
        /// Bottom Rear/Back Low zones too, which is exactly where the driver reported feeling it.
        /// </summary>
        [Fact]
        public void Genuine_standstill_produces_near_zero_output_on_every_pad_despite_small_longitudinalG_noise()
        {
            var engine = new GForceEngine();
            // Ground speed essentially constant (a 0.01 km/h drift is well inside the resolver's own
            // dead band) - direction settles to Unknown. LongitudinalG carries small sensor noise
            // (0.03g) - plausible standstill noise, not a real driving event.
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 0.02);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 0.01, longitudinalG: -0.03);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            GForceOutput result = null;
            for (int i = 0; i < 100; i++) result = engine.Compute(sample, AccelMax, DecelMax);

            Assert.True(result.BottomFrontLeft.Value < 1.0, $"BottomFrontLeft={result.BottomFrontLeft.Value}");
            Assert.True(result.BottomRearLeft.Value < 1.0, $"BottomRearLeft={result.BottomRearLeft.Value}");
            Assert.True(result.BackLowLeft.Value < 1.0, $"BackLowLeft={result.BackLowLeft.Value}");
            Assert.True(result.BackTopLeft.Value < 1.0, $"BackTopLeft={result.BackTopLeft.Value}");
        }

        // ---------------------------------------------------------------------------------------
        // REJECT vs CLAMP (docs\gforce-direction-fix-report.md, the owner's plausibility-limit ask):
        // the LEARNING path (GForceMaxLearner/GripLearner) REJECTS an impact-magnitude reading outright
        // (see those classes' own tests); this LIVE path must instead CLAMP - a real, in-range,
        // finite, saturated cue for the impact frame itself, recovering immediately once ordinary
        // readings resume, rather than dropping the frame (which would look like the plugin hanging).
        //
        // MUTATION (a): change the live clamp to a reject/hold instead (e.g. return the previous
        // frame's own output unchanged when magnitude exceeds LiveMagnitudeClampG) - the "produces a
        // different, saturated reading" assertion below must fail (verified manually - see the report).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void An_impact_magnitude_frame_clamps_to_a_finite_saturated_in_range_reading_instead_of_freezing()
        {
            var engine = new GForceEngine();

            // Settle at a modest, ordinary braking level first.
            GForceOutput before = null;
            for (int i = 0; i < 400; i++) before = engine.Compute(BrakingSample(0.5, 0.02), AccelMax, DecelMax);

            // One impact-magnitude frame (a wall-tap-scale ~19.8g reading - this session's own captured
            // spike, converted to G) while still measurably Slowing.
            GForceOutput impact = engine.Compute(BrakingSample(19.8, 0.02), AccelMax, DecelMax);

            AssertInBounds(impact.BottomFrontLeft);
            AssertInBounds(impact.BottomRearLeft);
            AssertInBounds(impact.BackLowLeft);
            Assert.True(double.IsFinite(impact.BottomFrontLeft.Value));
            Assert.True(impact.BottomFrontLeft.Value > before.BottomFrontLeft.Value,
                "an impact-magnitude frame must produce a real, different (saturated) reading, not a frozen/unchanged one");
        }

        [Fact]
        public void Output_recovers_to_normal_on_the_frame_after_an_impact_magnitude_spike()
        {
            var engine = new GForceEngine();

            GForceOutput before = null;
            for (int i = 0; i < 400; i++) before = engine.Compute(BrakingSample(0.5, 0.02), AccelMax, DecelMax);

            engine.Compute(BrakingSample(19.8, 0.02), AccelMax, DecelMax); // the impact frame

            // Back to the SAME ordinary level immediately after - recovers close to the pre-impact
            // reading within a handful of frames (a few sustain time constants), not stuck saturated.
            GForceOutput after = null;
            for (int i = 0; i < 30; i++) after = engine.Compute(BrakingSample(0.5, 0.02), AccelMax, DecelMax);

            Assert.True(Math.Abs(after.BottomFrontLeft.Value - before.BottomFrontLeft.Value) < 15.0,
                $"output should recover close to the pre-impact level, before={before.BottomFrontLeft.Value} after={after.BottomFrontLeft.Value}");
        }
    }
}
