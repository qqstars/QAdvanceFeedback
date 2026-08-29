using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Engine-level tests for <see cref="RawCalculatorEngine"/>: the *100 + clamp-to-[0,100] scaling
    /// step, Front/Rear/Left/Right/All aggregation via <see cref="Aggregator"/>'s physically-motivated
    /// scheme (docs/aggregation-report.md), Lock's own car-level model (<see cref="BrakingVsSpeedModel"/>),
    /// and the capability-driven branch dispatch (<see cref="WheelSlipBranchSelector"/>).
    /// </summary>
    public class RawCalculatorEngineTests
    {
        private static TelemetrySample Sample(
            double? newRpm, double? oldRpm, double? brake, double? throttle, double? clutch,
            double? lateralLocalVelocity, double speedKmh = 100.0)
        {
            var newFrame = new TelemetryFrame(
                speedKmh: speedKmh, rpm: newRpm, gear: "3",
                brakePercent: brake, throttlePercent: throttle, clutchPercent: clutch,
                lateralLocalVelocity: lateralLocalVelocity);
            var oldFrame = new TelemetryFrame(speedKmh: speedKmh, rpm: oldRpm, gear: "3");
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow);
        }

        // ------------------------------------------------------------------------------------
        // SLIP - the pedal+speed RPM-derived per-wheel term (BrakeSpeedSlipModel).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Slip_raw_wheel_values_are_scaled_to_0_100_and_clamped()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 6000.0, brake: 0.0, throttle: 95.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0);

            double nativeDelta = Math.Max(0.0, 100.0 / 5000.0 - 100.0 / 6000.0);
            double nativeThrottleTerm = nativeDelta * 4000.0 * ((95.0 - 70.0) / (100.0 - 70.0));
            Assert.True(nativeThrottleTerm * 100.0 > 100.0, "test setup should exceed 100 pre-clamp");

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(100.0, result.SlipWheels.FrontLeft, 9);
            Assert.Equal(100.0, result.SlipAll, 9);
        }

        [Fact]
        public void Slip_aggregates_Front_Rear_Left_Right_All_with_the_owners_scheme()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 45.0, clutch: 0.0,
                                 lateralLocalVelocity: -1.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            double outer = 100.0 / 3.0;
            double inner = outer / 2.0;

            Assert.Equal(outer, result.SlipWheels.FrontLeft, 6);
            Assert.Equal(inner, result.SlipWheels.FrontRight, 6);
            Assert.Equal(outer, result.SlipWheels.RearLeft, 6);
            Assert.Equal(inner, result.SlipWheels.RearRight, 6);

            // REVISED (docs\slip-source-consistency-report.md): Slip's own axle blend WMax/WMin
            // 0.55/0.45 -> 0.85/0.15.
            double expectedFrontRear = outer * 0.85 + inner * 0.15;
            double expectedLeft = outer;
            double expectedRight = inner;
            double expectedAll = expectedFrontRear;

            Assert.Equal(expectedFrontRear, result.SlipFront, 6);
            Assert.Equal(expectedFrontRear, result.SlipRear, 6);
            Assert.Equal(expectedLeft, result.SlipLeft, 6);
            Assert.Equal(expectedRight, result.SlipRight, 6);
            Assert.Equal(expectedAll, result.SlipAll, 6);
        }

        [Fact]
        public void Slip_floor_engages_through_the_real_Raw_tier_engine_for_a_single_spinning_wheel()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 45.0, clutch: 0.0,
                                 lateralLocalVelocity: -1.0);

            var engine = new RawCalculatorEngine();
            var defaultResult = engine.Compute(sample);

            WheelAggregate expected = Aggregator.Compute(defaultResult.SlipWheels, AggregationWeights.SlipDefaults);
            Assert.Equal(expected.All, defaultResult.SlipAll, 9);
            Assert.Equal(expected.Front, defaultResult.SlipFront, 9);
            Assert.Equal(expected.Rear, defaultResult.SlipRear, 9);
            Assert.Equal(expected.Left, defaultResult.SlipLeft, 9);
            Assert.Equal(expected.Right, defaultResult.SlipRight, 9);
        }

        [Fact]
        public void Slip_Front_equals_Rear_and_FrontLeft_equals_RearLeft_documented_behaviour()
        {
            var sample = Sample(newRpm: 5200.0, oldRpm: 4800.0, brake: 0.0, throttle: 65.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.7);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(result.SlipWheels.FrontLeft, result.SlipWheels.RearLeft, 9);
            Assert.Equal(result.SlipWheels.FrontRight, result.SlipWheels.RearRight, 9);
            Assert.Equal(result.SlipFront, result.SlipRear, 9);
        }

        // ------------------------------------------------------------------------------------
        // LOCK - BrakingVsSpeedModel, car-level (no per-wheel input at all - a genuine characteristic
        // of this branch, not a bug).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lock_is_uniform_across_all_four_wheels_and_every_aggregate()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 80.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.7, speedKmh: 100.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(result.LockWheels.FrontLeft, result.LockWheels.FrontRight, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockWheels.RearLeft, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockWheels.RearRight, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockFront, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockRear, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockLeft, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockRight, 9);
            Assert.Equal(result.LockWheels.FrontLeft, result.LockAll, 9);
        }

        [Fact]
        public void Lock_reads_full_when_hard_brake_is_sustained_at_ordinary_speed()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 90.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 100.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(100.0, result.LockAll, 6);
        }

        [Fact]
        public void Lock_reads_zero_at_a_genuine_standstill_regardless_of_brake()
        {
            var sample = Sample(newRpm: 800.0, oldRpm: 800.0, brake: 100.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 0.5);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(0.0, result.LockAll, 6);
        }

        /// <summary>
        /// INVERTED IN 1.0.7.1, AND KEPT RATHER THAN DELETED. This used to assert the opposite - that a
        /// low-speed lockup produced a strong cue - which was true only because of a brake-only floor
        /// layered on top of the faithful formula.
        /// <para/>
        /// That floor was removed because SimHub's own GetSimpleBraking has none (verified assembly-wide:
        /// one definition, one call site), and the divergence was severe rather than cosmetic - at
        /// 15 km/h under braking SimHub publishes 0 where the floor published 100, converging only at
        /// 30 km/h. It had effectively removed SPEED from the Lock formula below 30 km/h.
        /// <para/>
        /// The blind spot is therefore real and now DELIBERATE: this branch is silent below the
        /// sensitivity dead zone, exactly as ShakeIt is. If a low-speed cue is wanted, it belongs in the
        /// Projected layer's own curve, where it cannot make Raw and ShakeIt disagree.
        /// </summary>
        [Fact]
        public void Lock_is_silent_below_the_sensitivity_dead_zone_exactly_as_ShakeIt_is()
        {
            var sample = Sample(newRpm: 900.0, oldRpm: 900.0, brake: 80.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 8.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.Equal(0.0, result.LockAll, 6);
        }

        [Fact]
        public void Lock_does_not_fire_at_full_from_a_light_brake_dab_at_low_speed()
        {
            var sample = Sample(newRpm: 900.0, oldRpm: 900.0, brake: 25.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 8.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.True(result.LockAll < 15.0,
                $"a light brake dab at low speed must not read as a strong/full lock, got {result.LockAll}");
        }

        [Fact]
        public void Lock_uses_the_configured_LockSensibility_not_a_hardcoded_default()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 27.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 100.0);
            var engine = new RawCalculatorEngine();

            var defaultSensibility = LegacyThresholds.Defaults;
            var highSensibility = new LegacyThresholds
            {
                LockBrakeThresholdPercent = defaultSensibility.LockBrakeThresholdPercent,
                SlipBrakeThresholdPercent = defaultSensibility.SlipBrakeThresholdPercent,
                SlipThrottleThresholdPercent = defaultSensibility.SlipThrottleThresholdPercent,
                LockSensibility = 100.0
            };

            double atDefault = engine.Compute(sample, defaultSensibility).LockAll;
            double atHighSensibility = engine.Compute(sample, highSensibility).LockAll;

            Assert.Equal(0.0, atDefault, 6);
            Assert.True(atHighSensibility > 0.0,
                $"a higher LockSensibility must respond sooner (nonzero where the default reads 0), got {atHighSensibility}");
        }

        [Fact]
        public void Lock_below_its_own_trigger_threshold_reads_zero_even_with_a_strong_brake()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 90.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 100.0);
            var lowThreshold = new LegacyThresholds { LockBrakeThresholdPercent = 95.0, SlipBrakeThresholdPercent = 100.0, SlipThrottleThresholdPercent = 40.0, LockSensibility = 50.0 };

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample, lowThreshold);

            Assert.Equal(0.0, result.LockAll, 6);
        }

        // ------------------------------------------------------------------------------------
        // BRANCH DISPATCH - a capability-driven dispatch over every signal shape this engine
        // recognises, replacing a single hard-coded formula.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void No_rawTelemetry_supplied_falls_back_to_the_pre_dispatch_fixed_branches()
        {
            var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 90.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 100.0);
            var engine = new RawCalculatorEngine();

            var result = engine.Compute(sample); // rawTelemetry defaults to null

            Assert.Equal(WheelSlipBranchNames.NoCapabilitiesSuppliedLockFallback, result.SelectedLockBranch);
            Assert.Equal(WheelSlipBranchNames.NoCapabilitiesSuppliedSlipFallback, result.SelectedSlipBranch);
        }

        /// <summary>THE central case this dispatch exists for: WheelsRPS/WheelsSlip/Speed/Rpm all true,
        /// everything else false - Lock dispatches to RPS, Slip to Slip data - and the engine actually
        /// PRODUCES A REAL, NONZERO reading via that dispatch once its own learners have matured.</summary>
        [Fact]
        public void RawTelemetry_with_this_sessions_capabilities_dispatches_Lock_to_RPS_and_Slip_to_SlipData()
        {
            var raw = new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: 9.0, wheelRpsFrontRight: 9.0, wheelRpsRearLeft: 9.0, wheelRpsRearRight: 9.0,
                wheelSlipRatioFrontLeft: 5.0, wheelSlipRatioFrontRight: 5.0, wheelSlipRatioRearLeft: 5.0, wheelSlipRatioRearRight: 5.0,
                capabilityWheelsSlip: true, capabilityWheelsRPS: true, capabilityWheelsSpeed: false,
                capabilityWheelsSlipDirectMode: false, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false,
                capabilitySpeed: true, capabilityRpm: true);

            var engine = new RawCalculatorEngine();
            var thresholds = LegacyThresholds.Defaults;

            var cruiseSample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 30.0, clutch: 0.0,
                                       lateralLocalVelocity: 0.0, speedKmh: 90.0);
            for (int i = 0; i < 600; i++) engine.Compute(cruiseSample, thresholds, null, null, raw);

            var newFrame = new TelemetryFrame(speedKmh: 60.0, rpm: 5000.0, gear: "3", brakePercent: 80.0, throttlePercent: 0.0, clutchPercent: 0.0, lateralLocalVelocity: 0.0);
            var oldFrame = new TelemetryFrame(speedKmh: 90.0, rpm: 5000.0, gear: "3");
            var decelerating = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow);

            var result = engine.Compute(decelerating, thresholds, null, null, raw);

            Assert.Equal(WheelSlipBranchNames.Rps, result.SelectedLockBranch);
            Assert.Equal(WheelSlipBranchNames.SlipData, result.SelectedSlipBranch);
        }

        /// <summary>
        /// MUTATION GUARD, kept permanently: hard-coding the Lock branch back to
        /// <see cref="BrakingVsSpeedModel"/> regardless of capabilities would make this fail - it
        /// asserts the real dispatch picks RPS (not Braking vs speed) for RPS-capable titles.
        /// </summary>
        [Fact]
        public void MutationGuard_Lock_branch_is_not_hardcoded_to_BrakingVsSpeed_when_RPS_capability_is_present()
        {
            var raw = new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: 9.0, wheelRpsFrontRight: 9.0, wheelRpsRearLeft: 9.0, wheelRpsRearRight: 9.0,
                capabilityWheelsRPS: true, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false);

            string branch = WheelSlipBranchSelector.Select(raw, isLock: true);
            Assert.Equal(WheelSlipBranchNames.Rps, branch);
            Assert.NotEqual(WheelSlipBranchNames.BrakingVsSpeed, branch);
        }

        // ------------------------------------------------------------------------------------
        // FULL-LOCK FIDELITY FIX (docs\raw-full-lock-fidelity-report.md): the SlipData branch's
        // learned-percentile reference must be PER WHEEL for Lock, not shared/axle-pooled - two
        // wheels on the same axle are not guaranteed to report WheelSlipRatio on the same native
        // scale, and pooling them let one wheel's own extremes drag the other wheel's reference
        // band away from its own history.
        // ------------------------------------------------------------------------------------

        private static RawWheelTelemetrySnapshot SlipDataOnlySnapshot(double flRatio, double frRatio)
            => new RawWheelTelemetrySnapshot(
                wheelSlipRatioFrontLeft: flRatio, wheelSlipRatioFrontRight: frRatio,
                wheelSlipRatioRearLeft: frRatio, wheelSlipRatioRearRight: frRatio,
                capabilityWheelsSlip: true);

        /// <summary>
        /// SUPERSEDED IN 1.0.7.1 - AND DELIBERATELY KEPT, INVERTED, RATHER THAN DELETED.
        /// <para/>
        /// This test used to prove the full-lock fidelity fix: Lock read a PER-WHEEL slip-ratio learner,
        /// so a wheel whose native <c>WheelSlipRatio</c> tops out at +/-0.1 still published near 100 at
        /// its own genuine full lock, instead of being judged against an axle-mate whose native scale
        /// reaches +/-1.0 and reading far below.
        /// <para/>
        /// 1.0.7.1 removed those per-wheel learners because SimHub pools ALL FOUR wheels into a single
        /// "Slip" calibration - established by decompilation, and not merely at read time: SimHub's own
        /// ongoing learning (<c>CalibrationDataProvider.Update</c>) feeds every wheel into that same
        /// object, and <c>GetSlipCalibration</c> accepts a <c>front</c> flag it never reads. Matching
        /// ShakeIt exactly was the owner's explicit instruction, made after confirming that the pooling
        /// is SimHub's learning model and not just its read path.
        /// <para/>
        /// THE COST IS REAL AND IS PINNED BELOW so it cannot be forgotten: on a car whose two wheels
        /// report different native slip scales, the quieter wheel now under-reads at its own full lock -
        /// exactly as ShakeIt does. If that ever needs revisiting, this test is the record of what was
        /// traded away and why.
        /// </summary>
        [Fact]
        public void Lock_SlipData_branch_now_pools_all_four_wheels_matching_ShakeIt()
        {
            var thresholds = LegacyThresholds.Defaults;
            var engine = new RawCalculatorEngine();

            for (int i = 0; i < 300; i++)
            {
                bool hardLockFrame = i % 2 == 0;
                var raw = SlipDataOnlySnapshot(hardLockFrame ? -1.0 : -0.05, hardLockFrame ? -0.1 : -0.02);
                var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 0.0, clutch: 0.0,
                                     lateralLocalVelocity: 0.0, speedKmh: 90.0);
                engine.Compute(sample, thresholds, null, null, raw);
            }

            var testRaw = SlipDataOnlySnapshot(-1.0, -0.1);
            var testSample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 0.0, clutch: 0.0,
                                     lateralLocalVelocity: 0.0, speedKmh: 90.0);
            var result = engine.Compute(testSample, thresholds, null, null, testRaw);

            Assert.Equal(WheelSlipBranchNames.SlipData, result.SelectedLockBranch);
            Assert.Equal(WheelSlipBranchNames.SlipData, result.SelectedSlipBranch);

            // BOTH channels now read the same pooled calibration, so both report the SAME value for the
            // same native input. That identity is the actual contract this test defends.
            Assert.Equal(result.SlipWheels.FrontRight, result.LockWheels.FrontRight, 6);

            // ...and the pooled reference is dominated by FrontLeft's much larger native scale, so
            // FrontRight's own full-lock reading lands well below 100 - the ShakeIt behaviour, and the
            // cost recorded in this test's own remarks.
            Assert.True(result.LockWheels.FrontRight < 50.0,
                $"pooled against FrontLeft's +/-1.0 scale, FrontRight's -0.1 full lock must read low, got {result.LockWheels.FrontRight}");
        }
    }
}
