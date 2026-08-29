using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Fidelity tests for <see cref="DispatchBranchFormulas"/> and
    /// <see cref="WheelRotationLockFilter"/>.</summary>
    public class DispatchBranchFormulasTests
    {
        // ---------------- LockFromWheelRotation / WheelRotationLockFilter ----------------

        [Fact]
        public void LockFromWheelRotation_returns_zero_below_MinimumMeaningfulSpeed()
        {
            var filter = new WheelRotationLockFilter();
            double result = DispatchBranchFormulas.LockFromWheelRotation(filter, wheelRotationRateHz: 10.0, groundSpeedMps: 0.5, lockSensibility: 50.0);
            Assert.Equal(0.0, result);
        }

        [Fact]
        public void LockFromWheelRotation_null_inputs_return_zero()
        {
            var filter = new WheelRotationLockFilter();
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromWheelRotation(filter, null, 20.0, 50.0));
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromWheelRotation(filter, 5.0, null, 50.0));
        }

        [Fact]
        public void LockFromWheelRotation_ramps_up_over_several_frames()
        {
            // A wheel rotating far slower than ground speed implies (rotation-implied ratio near 0) - a
            // hard, sustained lockup - should ramp the smoothed estimate up toward 1.0 over successive
            // frames, not jump there in one frame.
            var filter = new WheelRotationLockFilter();
            double v1 = filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 30.0, unlockedAtRatio: 0.6, lockSensitivity: 50.0);
            double v2 = filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 30.0, unlockedAtRatio: 0.6, lockSensitivity: 50.0);
            double v3 = filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 30.0, unlockedAtRatio: 0.6, lockSensitivity: 50.0);

            Assert.True(v1 > 0.0);
            Assert.True(v2 > v1, "the smoothed estimate should keep rising while the wheel stays fully locked");
            Assert.True(v3 > v2);
            Assert.True(v3 <= 1.0);
        }

        [Fact]
        public void WheelRotationLockFilter_Reset_returns_to_cold_start()
        {
            var filter = new WheelRotationLockFilter();
            filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 30.0);
            filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 30.0);
            filter.Reset();

            double afterReset = filter.Compute(wheelRotationRateHz: 30.0, vehicleSpeedMps: 30.0); // rotating at ground speed - not locked
            Assert.Equal(0.0, afterReset, 6);
        }

        // ---------------- LockFromWheelSpeed ----------------

        [Fact]
        public void LockFromWheelSpeed_wheel_speed_matching_ground_speed_reads_low()
        {
            double gs = 100.0;
            double wheelSpeed = gs / 1.2;
            double result = DispatchBranchFormulas.LockFromWheelSpeed(wheelSpeed, gs);
            Assert.True(result < 0.05, $"expected near-zero, got {result}");
        }

        [Fact]
        public void LockFromWheelSpeed_wheel_stopped_while_car_moving_reads_high()
        {
            double result = DispatchBranchFormulas.LockFromWheelSpeed(wheelSpeed: 0.0, groundSpeedOrFallback: 100.0);
            Assert.True(result > 0.5, $"expected a strong lock reading, got {result}");
        }

        [Fact]
        public void LockFromWheelSpeed_returns_zero_at_or_below_1kmh()
        {
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromWheelSpeed(0.0, 1.0));
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromWheelSpeed(0.0, 0.0));
        }

        // ---------------- DirectSlip ----------------

        [Fact]
        public void DirectSlip_scales_by_SlipScale()
        {
            double result = DispatchBranchFormulas.DirectSlip(wheelSlipRatio: 5.0, slipScale: 10.0);
            Assert.Equal(0.5, result, 9);
        }

        [Fact]
        public void DirectSlip_uses_absolute_value()
        {
            double result = DispatchBranchFormulas.DirectSlip(wheelSlipRatio: -5.0, slipScale: 10.0);
            Assert.Equal(0.5, result, 9);
        }

        [Fact]
        public void DirectSlip_null_inputs_return_zero()
        {
            Assert.Equal(0.0, DispatchBranchFormulas.DirectSlip(null, 10.0));
            Assert.Equal(0.0, DispatchBranchFormulas.DirectSlip(5.0, null));
        }

        // ---------------- LockFromLearnedRotationRatio ----------------

        [Fact]
        public void LockFromLearnedRotationRatio_returns_zero_when_not_decelerating()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            learner.AddValue(1.0);
            double result = DispatchBranchFormulas.LockFromLearnedRotationRatio(wheelRotationRateHz: 50.0, speedKmh: 100.0, previousSpeedKmh: 90.0, learner, lockSensibility: 50.0);
            Assert.Equal(0.0, result); // speedKmh (100) is NOT less than previousSpeedKmh (90) - accelerating, not decelerating
        }

        [Fact]
        public void LockFromLearnedRotationRatio_returns_zero_without_a_learned_average()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData(); // no observations yet -> Average() is null
            double result = DispatchBranchFormulas.LockFromLearnedRotationRatio(wheelRotationRateHz: 50.0, speedKmh: 90.0, previousSpeedKmh: 100.0, learner, lockSensibility: 50.0);
            Assert.Equal(0.0, result);
        }

        [Fact]
        public void LockFromLearnedRotationRatio_reads_nonzero_when_rotation_falls_well_below_the_learned_average_ratio()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            for (int i = 0; i < 600; i++) learner.AddValue(1.0); // learned average ratio ~1.0

            double result = DispatchBranchFormulas.LockFromLearnedRotationRatio(wheelRotationRateHz: 9.0, speedKmh: 90.0, previousSpeedKmh: 100.0, learner, lockSensibility: 50.0);
            Assert.True(result > 0.0, $"expected a nonzero Lock reading, got {result}");
        }

        [Fact]
        public void LockFromLearnedRotationRatio_null_inputs_return_zero()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromLearnedRotationRatio(null, 90.0, 100.0, learner, 50.0));
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromLearnedRotationRatio(9.0, null, 100.0, learner, 50.0));
            Assert.Equal(0.0, DispatchBranchFormulas.LockFromLearnedRotationRatio(9.0, 90.0, null, learner, 50.0));
        }

        // ---------------- SlipFromLearnedDistribution ----------------

        [Fact]
        public void SlipFromLearnedDistribution_still_answers_before_the_distribution_is_ready()
        {
            // BEHAVIOUR DELIBERATELY REVERSED IN 1.0.7.1. This test previously asserted 0.0 here, which
            // was a faithful description of what we did and an UNfaithful description of ShakeIt: SimHub's
            // CalibrationData.GetPercentile returns a plain double and falls back to a band synthesised
            // from its running maximum, so the ShakeIt effect responds from the very first sample. Our
            // returning 0 for the first 200 positive samples is what made WheelSlip arrive late, and -
            // because BranchHasSignalForWheel still reported "signal present" - published that 0 as a
            // genuine measured zero rather than as absent.
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            learner.AddValue(5.0);

            double result = DispatchBranchFormulas.SlipFromLearnedDistribution(
                5.0, learner, brakePercent: 50.0, wheelRotationCapability: true, 10.0, 10.0, 10.0, 10.0);

            Assert.True(result > 0.0, $"the pre-maturity fallback must produce a reading, got {result}");
        }

        [Fact]
        public void SlipFromLearnedDistribution_uses_the_learned_percentile_band_once_ready()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            for (int i = 0; i < 600; i++) learner.AddValue(i % 10 + 1); // spread 1..10, nonzero, p15..p99 distinct
            // 1.0.7.1: GetPercentile returns a plain double and NEVER null - that is the whole point of
            // the ShakeIt calibration port (see Calibration.CalibrationData.GetPercentile).
            double p15 = learner.GetPercentile(15.0);
            double p99 = learner.GetPercentile(99.0);
            Assert.NotEqual(p15, p99);

            double result = DispatchBranchFormulas.SlipFromLearnedDistribution(p99, learner, brakePercent: 0.0, wheelRotationCapability: false, null, null, null, null);
            Assert.True(result >= 1.0 - 1e-6, $"a slip reading at p99 should read ~1.0, got {result}");
        }

        [Fact]
        public void SlipFromLearnedDistribution_rotation_spread_term_engages_above_20_percent_brake()
        {
            var learner = new QAdvanceFeedback.Core.RawCalculator.Calibration.CalibrationData();
            for (int i = 0; i < 600; i++) learner.AddValue(i % 10 + 1);

            double withoutBrake = DispatchBranchFormulas.SlipFromLearnedDistribution(0.0, learner, brakePercent: 0.0, wheelRotationCapability: true, 0.0, 30.0, 0.0, 0.0);
            double withBrake = DispatchBranchFormulas.SlipFromLearnedDistribution(0.0, learner, brakePercent: 50.0, wheelRotationCapability: true, 0.0, 30.0, 0.0, 0.0);

            Assert.Equal(0.0, withoutBrake, 9);
            Assert.True(withBrake > 0.0, "a 30-unit rotation-rate spread under braking should engage the spread term");
        }

        // ---------------- WheelSpeedVsGroundSpeedSlip ----------------

        [Fact]
        public void WheelSpeedVsGroundSpeedSlip_Lock_returns_zero_in_the_low_brake_positive_delta_regime()
        {
            double result = DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: true, wheelSpeedThisWheel: 105.0, wheelSpeedOppositeWheel: 105.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.02, gearAverageSampleCount: 0);

            Assert.Equal(0.0, result);
        }

        [Fact]
        public void WheelSpeedVsGroundSpeedSlip_Slip_reads_nonzero_when_wheel_speed_exceeds_ground_speed_under_power()
        {
            double result = DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(
                isLockChannel: false, wheelSpeedThisWheel: 130.0, wheelSpeedOppositeWheel: 130.0,
                groundSpeedOrFallback: 100.0, brakePercent: 0.0, throttlePercent: 50.0, clutchPercent: 0.0,
                gearAverageDelta: 0.02, gearAverageSampleCount: 0);

            Assert.True(result > 0.0, $"expected a nonzero Slip reading for a spinning driven wheel, got {result}");
        }

        [Fact]
        public void WheelSpeedVsGroundSpeedSlip_null_inputs_return_zero()
        {
            Assert.Equal(0.0, DispatchBranchFormulas.WheelSpeedVsGroundSpeedSlip(false, null, 100.0, 100.0, 0.0, 0.0, 0.0, 0.0, 0));
        }
    }
}
