using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Fidelity tests for <see cref="BrakeSpeedSlipModel"/> - one test per branch/guard, with every
    /// expected value computed independently in-line (not by calling the production Offset/Map
    /// helpers), so a broken formula cannot "agree with itself".
    /// </summary>
    public class BrakeSpeedSlipModelTests
    {
        private const int FL = 0, FR = 1, RL = 2, RR = 3;

        private static TelemetrySample Sample(
            double? newSpeedKmh = 100.0, double? oldSpeedKmh = 100.0,
            double? newRpm = 5000.0, double? oldRpm = 5000.0,
            string newGear = "3", string oldGear = "3",
            double? brake = 0.0, double? throttle = 0.0, double? clutch = 0.0,
            double? lateralLocalVelocity = 0.0,
            double? newGroundSpeedKmh = null, double? oldGroundSpeedKmh = null)
        {
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: newGroundSpeedKmh, speedKmh: newSpeedKmh, rpm: newRpm, gear: newGear,
                brakePercent: brake, throttlePercent: throttle, clutchPercent: clutch,
                lateralLocalVelocity: lateralLocalVelocity);
            var oldFrame = new TelemetryFrame(
                groundSpeedKmh: oldGroundSpeedKmh, speedKmh: oldSpeedKmh, rpm: oldRpm, gear: oldGear);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow);
        }

        [Fact]
        public void LateralFloorOnly_no_brake_no_throttle_halves_on_inside_wheel()
        {
            // lat = -(-1.0) = 1.0; floor = Offset(0.5,2.0,1.0,true) = (1.0-0.5)/1.5 = 1/3
            var sample = Sample(lateralLocalVelocity: -1.0, brake: 0.0, throttle: 0.0);

            double expectedOuter = 1.0 / 3.0;
            double expectedInner = expectedOuter / 2.0;

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            var fr = BrakeSpeedSlipModel.Compute(sample, FR);
            var rl = BrakeSpeedSlipModel.Compute(sample, RL);
            var rr = BrakeSpeedSlipModel.Compute(sample, RR);

            Assert.Equal(expectedOuter, fl.Lock, 9);
            Assert.Equal(expectedOuter, fl.Slip, 9);
            Assert.Equal(expectedInner, fr.Lock, 9);
            Assert.Equal(expectedOuter, rl.Lock, 9);
            Assert.Equal(expectedInner, rr.Lock, 9);
        }

        [Fact]
        public void LateralFloor_halves_the_other_side_when_lat_is_negative()
        {
            var sample = Sample(lateralLocalVelocity: 1.0);

            double full = 1.0 / 3.0;
            double halved = full / 2.0;

            Assert.Equal(halved, BrakeSpeedSlipModel.Compute(sample, FL).Lock, 9);
            Assert.Equal(full, BrakeSpeedSlipModel.Compute(sample, FR).Lock, 9);
            Assert.Equal(halved, BrakeSpeedSlipModel.Compute(sample, RL).Lock, 9);
            Assert.Equal(full, BrakeSpeedSlipModel.Compute(sample, RR).Lock, 9);
        }

        [Fact]
        public void Braking_term_pairs_each_frames_own_ground_speed_with_that_frames_own_rpm()
        {
            // Both frames share the same ground speed (100) here, so this scenario alone cannot tell
            // the corrected pairing apart from a same-speed-for-both approximation - see
            // Braking_term_differs_from_a_single_current_speed_approximation_when_speed_actually_changed
            // below for the case that actually distinguishes them. This test just pins the ordinary,
            // constant-speed case.
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, lateralLocalVelocity: 0.0);

            double delta = Math.Abs(100.0 / 6000.0 - 100.0 / 5000.0);
            double brakeFactor = (70.0 - 55.0) / (90.0 - 55.0);
            double expectedLock = delta * 4000.0 * brakeFactor;

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(expectedLock, fl.Lock, 9);
            // Throttle is 0 and brake=70 does not exceed Slip's own default threshold (100 - Slip is
            // throttle-only by default) -> Slip stays at the lateral floor here (0), unaffected by
            // braking, even though Lock (its own, separate 20% threshold) is fully engaged.
            Assert.Equal(0.0, fl.Slip, 9);
        }

        /// <summary>
        /// THE DEFECT FIX, verified directly. An earlier draft of this model divided a single
        /// CURRENT-frame ground speed by both the old and new RPM (i.e. read as "how has RPM changed at
        /// today's speed" rather than "what did each frame's own RPM imply about speed at the time"),
        /// which understates a braking event that spans a genuine speed change. This test uses a sample
        /// where the old and new ground speed genuinely differ, which is exactly the case that
        /// distinguishes the corrected pairing from that approximation - see
        /// <see cref="Braking_term_would_differ_under_the_old_same_speed_approximation"/> for the
        /// mutation check proving the two are NOT the same value here.
        /// </summary>
        [Fact]
        public void Braking_term_uses_the_old_frames_own_speed_with_old_rpm_and_the_new_frames_own_speed_with_new_rpm()
        {
            var sample = Sample(
                newSpeedKmh: 90.0, oldSpeedKmh: 110.0,
                newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, lateralLocalVelocity: 0.0);

            double expectedDelta = Math.Abs(110.0 / 6000.0 - 90.0 / 5000.0);
            double brakeFactor = (70.0 - 55.0) / (90.0 - 55.0);
            double expectedLock = expectedDelta * 4000.0 * brakeFactor;

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(expectedLock, fl.Lock, 9);
        }

        /// <summary>
        /// MUTATION EVIDENCE: reproduces what the PREVIOUS (defective) formula would have produced for
        /// the same scenario as the test above (both RPM terms divided by a single current-frame speed
        /// of 90) and confirms it is a materially different number from the corrected reading - proving
        /// the fix actually changes behaviour here, not merely re-derives the same value differently.
        /// </summary>
        [Fact]
        public void Braking_term_would_differ_under_the_old_same_speed_approximation()
        {
            var sample = Sample(
                newSpeedKmh: 90.0, oldSpeedKmh: 110.0,
                newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, lateralLocalVelocity: 0.0);

            double correctedDelta = Math.Abs(110.0 / 6000.0 - 90.0 / 5000.0);
            double oldApproximationDelta = Math.Abs(90.0 / 6000.0 - 90.0 / 5000.0); // both terms use new speed only
            double brakeFactor = (70.0 - 55.0) / (90.0 - 55.0);

            double correctedLock = correctedDelta * 4000.0 * brakeFactor;
            double oldApproximationLock = oldApproximationDelta * 4000.0 * brakeFactor;

            Assert.NotEqual(correctedLock, oldApproximationLock, 6);

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(correctedLock, fl.Lock, 9);
            Assert.NotEqual(oldApproximationLock, fl.Lock, 6);
        }

        [Fact]
        public void Throttle_branch_requires_clutch_below_5()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5100.0, throttle: 80.0, clutch: 0.0, lateralLocalVelocity: 0.0);

            double delta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 5100.0);
            double throttleFactor = (80.0 - 70.0) / (100.0 - 70.0);
            double expectedSlip = delta * 4000.0 * throttleFactor;

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Lock, 9);
            Assert.Equal(expectedSlip, fl.Slip, 9);
        }

        [Fact]
        public void Throttle_branch_suppressed_when_clutch_is_5_or_above()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5100.0, throttle: 80.0, clutch: 5.0, lateralLocalVelocity: 0.0);
            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Slip, 9);
        }

        [Fact]
        public void GearChange_suppresses_the_wheel_entirely()
        {
            var sample = Sample(oldGear: "2", newGear: "3", brake: 99.0, throttle: 99.0, clutch: 0.0,
                                 newRpm: 3000.0, oldRpm: 6000.0, lateralLocalVelocity: -1.0);

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Lock, 9);
            Assert.Equal(0.0, fl.Slip, 9);
        }

        [Fact]
        public void Neutral_gear_suppresses_brake_and_throttle_but_not_the_lateral_floor()
        {
            var sample = Sample(oldGear: "N", newGear: "N", brake: 99.0, throttle: 99.0, clutch: 0.0,
                                 lateralLocalVelocity: -1.0);

            double expectedFloor = 1.0 / 3.0;

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(expectedFloor, fl.Lock, 9);
            Assert.Equal(expectedFloor, fl.Slip, 9);
        }

        [Fact]
        public void BelowSpeedGate_outer_gate_zeroes_everything()
        {
            var sample = Sample(newGroundSpeedKmh: 0.5, brake: 99.0, lateralLocalVelocity: -1.0);

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Lock, 9);
            Assert.Equal(0.0, fl.Slip, 9);
        }

        [Fact]
        public void MissingNewRpm_zeroes_everything()
        {
            var sample = Sample(newRpm: null, brake: 99.0, lateralLocalVelocity: -1.0);
            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Lock, 9);
            Assert.Equal(0.0, fl.Slip, 9);
        }

        [Fact]
        public void MissingOldRpm_zeroes_everything()
        {
            var sample = Sample(oldRpm: null, brake: 99.0, lateralLocalVelocity: -1.0);
            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            Assert.Equal(0.0, fl.Lock, 9);
            Assert.Equal(0.0, fl.Slip, 9);
        }

        [Fact]
        public void FrontLeft_equals_RearLeft_and_FrontRight_equals_RearRight()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, lateralLocalVelocity: -1.0);

            var fl = BrakeSpeedSlipModel.Compute(sample, FL);
            var rl = BrakeSpeedSlipModel.Compute(sample, RL);
            var fr = BrakeSpeedSlipModel.Compute(sample, FR);
            var rr = BrakeSpeedSlipModel.Compute(sample, RR);

            Assert.Equal(fl.Lock, rl.Lock, 12);
            Assert.Equal(fl.Slip, rl.Slip, 12);
            Assert.Equal(fr.Lock, rr.Lock, 12);
            Assert.Equal(fr.Slip, rr.Slip, 12);
        }

        // ------------------------------------------------------------------------------------
        // Driver-configurable pedal thresholds (LegacyThresholds). Default (null) thresholds equal the
        // shipped defaults, so every pre-existing 2-arg call above keeps behaving identically.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Default_thresholds_gate_at_20_and_40()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, throttle: 0.0, lateralLocalVelocity: 0.0);
            var withNullThresholds = BrakeSpeedSlipModel.Compute(sample, FL, thresholds: null);
            var withExplicitDefaults = BrakeSpeedSlipModel.Compute(sample, FL, LegacyThresholds.Defaults);

            Assert.Equal(withExplicitDefaults.Lock, withNullThresholds.Lock, 12);
            Assert.True(withNullThresholds.Lock > 0.0, "brake at 70% (>20% default) should trigger Lock");
        }

        [Fact]
        public void Configurable_Lock_brake_threshold_gates_at_the_owner_configured_value_not_20()
        {
            var lowBrakeSample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 15.0, lateralLocalVelocity: 0.0);
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 30.0, SlipBrakeThresholdPercent = 100.0, SlipThrottleThresholdPercent = 40.0 };

            var lowResult = BrakeSpeedSlipModel.Compute(lowBrakeSample, FL, thresholds);
            Assert.Equal(0.0, lowResult.Lock, 9);

            var midBrakeSample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 60.0, lateralLocalVelocity: 0.0);
            var midResult = BrakeSpeedSlipModel.Compute(midBrakeSample, FL, thresholds);
            Assert.True(midResult.Lock > 0.0, "brake at 60% should trigger Lock once above the configured 30% threshold");

            var raisedThresholds = new LegacyThresholds { LockBrakeThresholdPercent = 65.0, SlipBrakeThresholdPercent = 100.0, SlipThrottleThresholdPercent = 40.0 };
            var raisedResult = BrakeSpeedSlipModel.Compute(midBrakeSample, FL, raisedThresholds);
            Assert.Equal(0.0, raisedResult.Lock, 9);
        }

        [Fact]
        public void Slip_brake_threshold_at_its_default_100_disables_the_brake_path_so_only_throttle_triggers_slip()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5100.0, brake: 100.0, throttle: 80.0, clutch: 0.0, lateralLocalVelocity: 0.0);
            var result = BrakeSpeedSlipModel.Compute(sample, FL, LegacyThresholds.Defaults);

            double throttleDelta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 5100.0);
            double throttleFactor = (80.0 - 70.0) / (100.0 - 70.0);
            double expectedThrottleTerm = throttleDelta * 4000.0 * throttleFactor;

            double brakeDelta = Math.Abs(100.0 / 5100.0 - 100.0 / 5000.0);
            double wouldBeBrakeTerm = brakeDelta * 4000.0 * 1.0;

            Assert.NotEqual(expectedThrottleTerm, wouldBeBrakeTerm, 6);
            Assert.Equal(expectedThrottleTerm, result.Slip, 9);
            Assert.NotEqual(wouldBeBrakeTerm, result.Slip, 6);
        }

        [Fact]
        public void Slip_brake_threshold_can_be_lowered_to_20_to_match_Lock_when_a_driver_wants_it()
        {
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 20.0, SlipBrakeThresholdPercent = 20.0, SlipThrottleThresholdPercent = 40.0 };
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, throttle: 80.0, clutch: 0.0, lateralLocalVelocity: 0.0);
            var result = BrakeSpeedSlipModel.Compute(sample, FL, thresholds);

            double brakeDelta = Math.Abs(100.0 / 6000.0 - 100.0 / 5000.0);
            double brakeFactor = (70.0 - 55.0) / (90.0 - 55.0);
            double expectedBrakeTerm = brakeDelta * 4000.0 * brakeFactor;

            double throttleDelta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 6000.0);
            double throttleFactor = (80.0 - 70.0) / (100.0 - 70.0);
            double wouldBeThrottleTerm = throttleDelta * 4000.0 * throttleFactor;

            Assert.NotEqual(expectedBrakeTerm, wouldBeThrottleTerm, 6);
            Assert.Equal(expectedBrakeTerm, result.Slip, 9);
            Assert.NotEqual(wouldBeThrottleTerm, result.Slip, 6);
        }

        [Fact]
        public void Slip_brake_priority_wins_over_throttle_when_both_thresholds_are_exceeded()
        {
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 20.0, SlipBrakeThresholdPercent = 50.0, SlipThrottleThresholdPercent = 40.0 };
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 70.0, throttle: 80.0, clutch: 0.0, lateralLocalVelocity: 0.0);

            var result = BrakeSpeedSlipModel.Compute(sample, FL, thresholds);

            double brakeDelta = Math.Abs(100.0 / 6000.0 - 100.0 / 5000.0);
            double brakeFactor = (70.0 - 55.0) / (90.0 - 55.0);
            double expectedBrakeTerm = brakeDelta * 4000.0 * brakeFactor;

            double throttleDelta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 6000.0);
            double throttleFactor = (80.0 - 70.0) / (100.0 - 70.0);
            double wouldBeThrottleTerm = throttleDelta * 4000.0 * throttleFactor;

            Assert.NotEqual(expectedBrakeTerm, wouldBeThrottleTerm, 6);
            Assert.Equal(expectedBrakeTerm, result.Slip, 9);
            Assert.NotEqual(wouldBeThrottleTerm, result.Slip, 6);
        }

        [Fact]
        public void Slip_falls_through_to_throttle_when_brake_threshold_is_not_exceeded()
        {
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 20.0, SlipBrakeThresholdPercent = 50.0, SlipThrottleThresholdPercent = 40.0 };
            var sample = Sample(newRpm: 5000.0, oldRpm: 5100.0, brake: 30.0, throttle: 80.0, clutch: 0.0, lateralLocalVelocity: 0.0);

            var result = BrakeSpeedSlipModel.Compute(sample, FL, thresholds);

            double throttleDelta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 5100.0);
            double throttleFactor = (80.0 - 70.0) / (100.0 - 70.0);
            double expectedThrottleTerm = throttleDelta * 4000.0 * throttleFactor;

            Assert.Equal(expectedThrottleTerm, result.Slip, 9);
        }

        [Fact]
        public void Lock_channel_never_reads_the_Slip_specific_thresholds()
        {
            var thresholds = new LegacyThresholds { LockBrakeThresholdPercent = 60.0, SlipBrakeThresholdPercent = 10.0, SlipThrottleThresholdPercent = 10.0 };
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 40.0, lateralLocalVelocity: 0.0);

            var result = BrakeSpeedSlipModel.Compute(sample, FL, thresholds);
            Assert.Equal(0.0, result.Lock, 9);
        }
    }
}
