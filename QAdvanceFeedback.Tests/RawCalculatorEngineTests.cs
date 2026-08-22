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

        [Fact]
        public void Lock_produces_a_strong_cue_for_a_genuine_low_speed_lockup_that_the_faithful_formula_alone_cannot()
        {
            var sample = Sample(newRpm: 900.0, oldRpm: 900.0, brake: 80.0, throttle: 0.0, clutch: 0.0,
                                 lateralLocalVelocity: 0.0, speedKmh: 8.0);

            var engine = new RawCalculatorEngine();
            var result = engine.Compute(sample);

            Assert.True(result.LockAll > 40.0,
                $"a genuine low-speed lockup must produce a strong cue, got {result.LockAll}");
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
        /// THE FIX, proven end to end through the real engine (not the private learner directly):
        /// FrontLeft's own native <c>WheelSlipRatio</c> reaches +/-1.0 at a hard lock while
        /// FrontRight's own native reading never exceeds +/-0.1 for the exact same car/session -
        /// both wheels are, physically, at their OWN full lock on the test frame below. Before the
        /// fix, Lock's FrontRight was judged against a learner POOLED with FrontLeft's much larger
        /// scale (see <c>_slipRatioFront</c>) and read far below 100 despite being fully locked on
        /// its own native scale; Slip used - and, being unchanged, still uses today - that exact
        /// same pooled reference and behaviour, which this test also pins down as a mutation guard.
        /// </summary>
        [Fact]
        public void Lock_SlipData_branch_judges_each_wheel_against_its_own_history_not_the_axle_pool()
        {
            var thresholds = LegacyThresholds.Defaults;
            var engine = new RawCalculatorEngine();

            // Train both wheels' references: FrontLeft alternates between a light reading and a hard
            // +/-1.0 lock; FrontRight alternates between a light reading and ITS OWN, much smaller,
            // +/-0.1 hard-lock ceiling - a realistic per-wheel-scale asymmetry, not a corner case.
            for (int i = 0; i < 300; i++)
            {
                bool hardLockFrame = i % 2 == 0;
                var raw = SlipDataOnlySnapshot(hardLockFrame ? -1.0 : -0.05, hardLockFrame ? -0.1 : -0.02);
                var sample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 0.0, clutch: 0.0,
                                     lateralLocalVelocity: 0.0, speedKmh: 90.0);
                engine.Compute(sample, thresholds, null, null, raw);
            }

            // Test frame: FrontRight is at its OWN full-lock reading (-0.1, its historical ceiling)
            // at the exact same instant FrontLeft is at ITS full-lock reading (-1.0).
            var testRaw = SlipDataOnlySnapshot(-1.0, -0.1);
            var testSample = Sample(newRpm: 5000.0, oldRpm: 5000.0, brake: 0.0, throttle: 0.0, clutch: 0.0,
                                     lateralLocalVelocity: 0.0, speedKmh: 90.0);
            var result = engine.Compute(testSample, thresholds, null, null, testRaw);

            Assert.Equal(WheelSlipBranchNames.SlipData, result.SelectedLockBranch);
            Assert.Equal(WheelSlipBranchNames.SlipData, result.SelectedSlipBranch);

            // THE FIX: judged against its own history, FrontRight's own full-lock reading publishes
            // near 100 for Lock.
            Assert.True(result.LockWheels.FrontRight > 90.0,
                $"Lock.FrontRight should read near its own full-lock ceiling once judged against its own history, got {result.LockWheels.FrontRight}");

            // MUTATION GUARD: Slip is untouched - still reading the OLD axle-pooled reference, so the
            // exact same native input (-0.1, dwarfed by FrontLeft's own -1.0 in the shared pool)
            // still reads far below 100 for Slip, unlike Lock now does.
            Assert.True(result.SlipWheels.FrontRight < 50.0,
                $"Slip.FrontRight must remain on the old axle-pooled reference (unchanged), got {result.SlipWheels.FrontRight}");
        }
    }
}
