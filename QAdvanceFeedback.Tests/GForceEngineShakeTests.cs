using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceEngine"/>'s owner-requested "Integrate Wheel Lock and Slip" shake
    /// wiring - the ENGINE-LEVEL integration of <see cref="GForceShake"/> (whose own pure math is
    /// covered by <c>GForceShakeTests</c>): the checkbox/scale/frequency properties, the Lock-vs-Slip
    /// combination rule, and the "checkbox off is byte-identical to before this feature existed"
    /// guarantee.
    /// </summary>
    public class GForceEngineShakeTests
    {
        private const double AccelMax = 0.9;
        private const double DecelMax = 2.0;

        /// <summary>DIRECTION FIX (docs\gforce-direction-fix-report.md): every call site in this file
        /// used a negative longG (braking) - ground speed FALLING (old 101 -&gt; new 100 km/h)
        /// resolves Slowing from the first frame, exactly like GForceEngineTests' own identically-named
        /// helper. Magnitude/sign convention at the fixture level only - see that class's own remarks.</summary>
        private static TelemetrySample Sample(double? longG, double dtSeconds, double? latG = null)
        {
            if (!longG.HasValue)
            {
                var bare = new TelemetryFrame(longitudinalG: null, lateralG: latG);
                return new TelemetrySample(bare, TelemetryFrame.Empty, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
            }

            var oldFrame = new TelemetryFrame(groundSpeedKmh: longG.Value <= 0.0 ? 101.0 : 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: longG.Value <= 0.0 ? 100.0 : 101.0,
                longitudinalG: longG,
                lateralG: latG);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        // ---------------------------------------------------------------------------------------
        // Settings-level clamps (1-20 Hz floor/ceiling, default 3 Hz off the floor; non-negative
        // scales, default 1.5) - enforced in the SETTER, not only a UI spinner range. Floor/default
        // LOWERED/RAISED respectively (docs\shake-tuning-report.md) per driver feedback that 5 Hz was
        // not obvious enough and the shake was not pronounced enough by default - this is a legitimate
        // default/floor change, not a weakened assertion.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ShakeFrequencyHz_defaults_to_3_and_is_clamped_to_1_20_in_the_setter()
        {
            var engine = new GForceEngine();
            Assert.Equal(3.0, engine.ShakeFrequencyHz, 6);

            engine.ShakeFrequencyHz = 0.1; // below floor
            Assert.Equal(1.0, engine.ShakeFrequencyHz, 6);

            engine.ShakeFrequencyHz = 100.0; // above ceiling
            Assert.Equal(20.0, engine.ShakeFrequencyHz, 6);

            engine.ShakeFrequencyHz = 12.0; // within range
            Assert.Equal(12.0, engine.ShakeFrequencyHz, 6);
        }

        /// <summary>MUTATION (a) target: if the setter allowed a frequency below 1 Hz, this test would
        /// fail (the assertion below would instead pass with an unclamped 0.001).</summary>
        [Fact]
        public void MUTATION_a_a_frequency_below_1Hz_must_never_be_readable_back()
        {
            var engine = new GForceEngine { ShakeFrequencyHz = 0.001 };
            Assert.True(engine.ShakeFrequencyHz >= GForceShake.MinFrequencyHz);
            Assert.Equal(1.0, engine.ShakeFrequencyHz, 6);
        }

        /// <summary>The Layer 5 pulse's own, separate, UNCHANGED 200 ms (5 Hz) gap floor must not be
        /// the one that moved - see <c>PulseSettingsTests</c> for the dedicated pulse-side assertion;
        /// this is the shake-side half of that same distinction.</summary>
        [Fact]
        public void The_pulse_gap_floor_is_a_different_setting_and_is_unaffected_by_the_shake_floor_change()
        {
            Assert.Equal(200.0, QAdvanceFeedback.Core.Projection.PulseSettings.MinGapMs, 6);
        }

        [Fact]
        public void WheelLockShakeScale_and_WheelSlipShakeScale_default_to_1_5_and_reject_negative_values()
        {
            var engine = new GForceEngine();
            Assert.Equal(1.5, engine.WheelLockShakeScale, 6);
            Assert.Equal(1.5, engine.WheelSlipShakeScale, 6);

            engine.WheelLockShakeScale = -5.0;
            engine.WheelSlipShakeScale = -5.0;
            Assert.Equal(0.0, engine.WheelLockShakeScale, 6);
            Assert.Equal(0.0, engine.WheelSlipShakeScale, 6);
        }

        /// <summary>
        /// This is the RAW ENGINE's own bare-constructor default, which deliberately stays OFF
        /// (docs\integrate-default-report.md) as a library-level "inert unless configured" baseline for
        /// any caller constructing <see cref="GForceEngine"/> directly - it is NOT the same thing as what
        /// a real, fully-wired install experiences. The SETTINGS-layer default
        /// (<see cref="Settings.GForceSettings.IntegrateWheelLockAndSlip"/>) is now ON, and
        /// <c>GForceSettings.ApplyTo</c> pushes that value onto this property at Init and on every
        /// settings Apply - so the two defaults disagreeing here is intentional, not drift. See
        /// <c>GForceSettingsTests.Shake_settings_now_default_to_on_3Hz_and_scale_1_5</c> for the
        /// settings-layer counterpart.
        /// </summary>
        [Fact]
        public void IntegrateWheelLockAndSlip_defaults_to_off_on_the_bare_engine_itself()
        {
            Assert.False(new GForceEngine().IntegrateWheelLockAndSlip);
        }

        // ---------------------------------------------------------------------------------------
        // "Checkbox off produces byte-identical output to today" - the mandatory evidence item.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Checkbox_off_produces_byte_identical_output_regardless_of_wheel_values_passed_in()
        {
            var baseline = new GForceEngine { IntegrateWheelLockAndSlip = false };
            var withWheelArgsButDisabled = new GForceEngine { IntegrateWheelLockAndSlip = false }; // explicit, not relying on the bare-constructor default

            for (int i = 0; i < 50; i++)
            {
                var sample = Sample(-1.0 - i * 0.01, 0.02);
                var r1 = baseline.Compute(sample, AccelMax, DecelMax);
                var r2 = withWheelArgsButDisabled.Compute(sample, AccelMax, DecelMax, wheelLockAll0100: 100.0, wheelSlipAll0100: 100.0);

                Assert.Equal(r1.BottomFrontLeft.Value, r2.BottomFrontLeft.Value, 12);
                Assert.Equal(r1.BottomRearLeft.Value, r2.BottomRearLeft.Value, 12);
                Assert.Equal(r1.BackLowLeft.Value, r2.BackLowLeft.Value, 12);
                Assert.Equal(r1.BackTopLeft.Value, r2.BackTopLeft.Value, 12);
            }
        }

        [Fact]
        public void Wheel_value_of_zero_produces_no_shake_even_when_enabled()
        {
            var withZeroWheel = new GForceEngine { IntegrateWheelLockAndSlip = true };
            var disabled = new GForceEngine { IntegrateWheelLockAndSlip = false }; // explicit, not relying on the bare-constructor default

            for (int i = 0; i < 50; i++)
            {
                var sample = Sample(-1.0 - i * 0.01, 0.02);
                var r1 = disabled.Compute(sample, AccelMax, DecelMax);
                var r2 = withZeroWheel.Compute(sample, AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 0.0);

                Assert.Equal(r1.BottomRearLeft.Value, r2.BottomRearLeft.Value, 9);
            }
        }

        [Fact]
        public void Scale_of_zero_produces_no_shake_even_with_a_full_wheel_value()
        {
            var engine = new GForceEngine
            {
                IntegrateWheelLockAndSlip = true,
                WheelLockShakeScale = 0.0,
                WheelSlipShakeScale = 0.0,
            };
            var disabled = new GForceEngine { IntegrateWheelLockAndSlip = false }; // explicit, not relying on the bare-constructor default

            for (int i = 0; i < 50; i++)
            {
                var sample = Sample(-1.0 - i * 0.01, 0.02);
                var r1 = disabled.Compute(sample, AccelMax, DecelMax);
                var r2 = engine.Compute(sample, AccelMax, DecelMax, wheelLockAll0100: 100.0, wheelSlipAll0100: 100.0);

                Assert.Equal(r1.BottomRearLeft.Value, r2.BottomRearLeft.Value, 9);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Lock vs Slip combination: the LARGER scaled contribution wins, not the sum - owner-confirmed.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void When_both_lock_and_slip_are_non_zero_the_larger_scaled_contribution_drives_the_shake_not_the_sum()
        {
            // Settle a steady braking level first so BottomRearLeft has a genuine non-zero centre.
            var withBoth = new GForceEngine { IntegrateWheelLockAndSlip = true, WheelLockShakeScale = 1.0, WheelSlipShakeScale = 1.0 };
            var withLockOnly = new GForceEngine { IntegrateWheelLockAndSlip = true, WheelLockShakeScale = 1.0, WheelSlipShakeScale = 1.0 };

            for (int i = 0; i < 400; i++)
            {
                withBoth.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 30.0);
                withLockOnly.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 0.0);
            }

            // Lock and Slip contribute EQUALLY here (30*1.0 both) - Max(30,30) == Sum-is-NOT-60. If the
            // combination were a SUM, "withBoth" would show a visibly wider oscillation range than
            // "withLockOnly" (which only has the Lock contribution) even though both channels report
            // the SAME 30 value; under Max, feeding an identical second channel at the same value must
            // produce IDENTICAL amplitude to the single-channel case.
            double bandBoth = 0.0, bandLockOnly = 0.0;
            double? minBoth = null, maxBoth = null, minLockOnly = null, maxLockOnly = null;
            for (int i = 0; i < 200; i++)
            {
                var rBoth = withBoth.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 30.0);
                var rLockOnly = withLockOnly.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 30.0, wheelSlipAll0100: 0.0);

                double vBoth = rBoth.BottomRearLeft.Value;
                double vLockOnly = rLockOnly.BottomRearLeft.Value;
                minBoth = minBoth.HasValue ? Math.Min(minBoth.Value, vBoth) : vBoth;
                maxBoth = maxBoth.HasValue ? Math.Max(maxBoth.Value, vBoth) : vBoth;
                minLockOnly = minLockOnly.HasValue ? Math.Min(minLockOnly.Value, vLockOnly) : vLockOnly;
                maxLockOnly = maxLockOnly.HasValue ? Math.Max(maxLockOnly.Value, vLockOnly) : vLockOnly;
            }

            bandBoth = maxBoth.Value - minBoth.Value;
            bandLockOnly = maxLockOnly.Value - minLockOnly.Value;

            Assert.Equal(bandLockOnly, bandBoth, 6);
        }

        [Fact]
        public void Slip_alone_can_also_drive_the_shake()
        {
            var engine = new GForceEngine { IntegrateWheelLockAndSlip = true, WheelSlipShakeScale = 1.0, WheelLockShakeScale = 1.0 };
            var disabled = new GForceEngine { IntegrateWheelLockAndSlip = false }; // explicit, not relying on the bare-constructor default

            double? maxGap = null;
            for (int i = 0; i < 400; i++)
            {
                var r = engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 0.0, wheelSlipAll0100: 80.0);
                var rBase = disabled.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax);
                double gap = Math.Abs(r.BottomRearLeft.Value - rBase.BottomRearLeft.Value);
                maxGap = maxGap.HasValue ? Math.Max(maxGap.Value, gap) : gap;
            }

            Assert.True(maxGap.Value > 1.0, "Slip alone (Lock=0) should still visibly perturb the pad, got max gap " + maxGap);
        }

        // ---------------------------------------------------------------------------------------
        // Left/right are exact mirrors around whatever the shaken pair's own centre currently is.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Left_and_right_stay_exact_mirrors_of_each_other_around_their_own_centre_when_shaking()
        {
            var engine = new GForceEngine { IntegrateWheelLockAndSlip = true };

            for (int i = 0; i < 100; i++)
            {
                var r = engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 40.0, wheelSlipAll0100: 0.0);
                double centre = (r.BottomRearLeft.Value + r.BottomRearRight.Value) / 2.0;
                Assert.InRange(centre, 0.0, 100.0);
                // Exact mirror: left - centre == -(right - centre).
                Assert.Equal(r.BottomRearLeft.Value - centre, -(r.BottomRearRight.Value - centre), 6);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Existing 0-100 bound and Reset() behaviour must still hold with shake active.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Shaking_never_breaks_the_0_100_bound_across_a_wide_sweep()
        {
            var engine = new GForceEngine { IntegrateWheelLockAndSlip = true, WheelLockShakeScale = 3.0, WheelSlipShakeScale = 3.0 };

            for (double longG = -6.0; longG <= 6.0; longG += 0.5)
            {
                var r = engine.Compute(Sample(longG, 0.02), AccelMax, DecelMax, wheelLockAll0100: 90.0, wheelSlipAll0100: 90.0);
                Assert.InRange(r.BottomFrontLeft.Value, 0.0, 100.0);
                Assert.InRange(r.BottomRearLeft.Value, 0.0, 100.0);
                Assert.InRange(r.BackLowLeft.Value, 0.0, 100.0);
                Assert.InRange(r.BackTopLeft.Value, 0.0, 100.0);
            }
        }

        [Fact]
        public void Reset_clears_the_shake_phase_so_a_fresh_session_restarts_at_phase_zero()
        {
            var engine = new GForceEngine { IntegrateWheelLockAndSlip = true };
            for (int i = 0; i < 30; i++)
                engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 50.0, wheelSlipAll0100: 0.0);

            engine.Reset();

            // Immediately after Reset, the very first frame's shake phase is back at t=0 (sin(0)=0) -
            // left and right must be equal (both == the frame's own centre) on that first frame.
            var r = engine.Compute(Sample(-1.0, 0.02), AccelMax, DecelMax, wheelLockAll0100: 50.0, wheelSlipAll0100: 0.0);
            Assert.Equal(r.BottomRearLeft.Value, r.BottomRearRight.Value, 6);
        }
    }
}
