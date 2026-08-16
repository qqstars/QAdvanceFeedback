using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// ABSENT-VS-ZERO (telemetry-integrity pass, item 1 - highest priority). A wheel reporting 0 RPS is
    /// indistinguishable, by value alone, from a fully locked wheel; a substituted zero for a wheel that
    /// simply did not report this frame is therefore not harmless noise, it is the single strongest false
    /// signal this plugin can produce. These tests prove: (a) the mechanism that would otherwise read a
    /// literal 0 as "fully locked" (<see cref="WheelRotationLockFilter"/>) is never fed an ABSENT reading
    /// coalesced to that same 0 (<see cref="DispatchBranchFormulas.LockFromWheelRotation"/>'s own
    /// null-guard); (b) partial per-wheel aggregation combines only the wheels that actually reported
    /// (<see cref="Aggregator.ComputeAvailable"/>), never diluting a real reading with silent zeros for
    /// the rest; (c) a channel with nothing to report publishes ABSENT end to end
    /// (<see cref="LegacyWheelLockSlipResult.LockHasValue"/>/<c>SlipHasValue</c>), not a numeric 0; and
    /// (d) an absent per-wheel reading never reaches a cross-frame learner.
    /// </summary>
    public class AbsentTelemetryTests
    {
        // ------------------------------------------------------------------------------------
        // (a) THE MECHANISM: why a coalesced zero would be catastrophic, and why it cannot happen.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_REAL_zero_rotation_rate_at_real_speed_correctly_reads_as_fully_locked()
        {
            // This is CORRECT behaviour for a wheel that genuinely stopped rotating while the car keeps
            // moving - it is exactly why a coalesced ABSENT-to-zero would be catastrophic: the filter
            // cannot tell "genuinely stopped" from "never told us anything" once both look like 0.0.
            var filter = new WheelRotationLockFilter();
            double lock1 = 0.0;
            for (int i = 0; i < 20; i++) lock1 = filter.Compute(wheelRotationRateHz: 0.0, vehicleSpeedMps: 20.0);

            Assert.True(lock1 > 0.9, $"a real, sustained 0 RPS at real speed should read as (near) fully locked, got {lock1}");
        }

        [Fact]
        public void An_ABSENT_rotation_rate_never_reaches_the_filter_and_reads_as_no_lock_not_full_lock()
        {
            // The guard DispatchBranchFormulas.LockFromWheelRotation already has - proven directly here,
            // not merely inferred: a null reading returns 0 (no lock, i.e. "nothing to say") WITHOUT ever
            // calling into the filter above that would otherwise read a literal 0 as fully locked.
            var filter = new WheelRotationLockFilter();
            double result = DispatchBranchFormulas.LockFromWheelRotation(filter, wheelRotationRateHz: null, groundSpeedMps: 20.0, lockSensibility: 50.0);

            Assert.Equal(0.0, result, 9);
        }

        // ------------------------------------------------------------------------------------
        // (b) PARTIAL-SET AGGREGATION - Aggregator.ComputeAvailable combines only reporting wheels.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Two_reporting_wheels_combine_as_two_not_four_with_two_silent_zeros()
        {
            // Front axle (FL/FR) genuinely reports a strong 80; the rear axle did not report at all this
            // frame (absent, not a real 0). AggregationWeights.LockDefaults' own Front/Rear blend is
            // 0.90/0.10 - if the missing rear pair were silently coalesced to 0, "All" would read
            // 80*0.90 + 0*0.10 = 72, understating the real front-axle event by 8 points for no physical
            // reason. The fix: with no rear reading at all, "All" must equal the front axle's own value,
            // unmodified by an invented rear zero.
            WheelAggregate result = Aggregator.ComputeAvailable(
                frontLeft: 80.0, frontRight: 80.0, rearLeft: null, rearRight: null,
                AggregationWeights.LockDefaults, out bool[] hasValue);

            Assert.Equal(80.0, result.Front, 9);
            Assert.Equal(80.0, result.All, 9); // NOT 72.0 - the dilution this fix removes.
            Assert.True(hasValue[4], "Front should have a value"); // Targets order: ...,Front,Rear,Left,Right,All
            Assert.False(hasValue[5], "Rear should be absent - nothing reported for either rear wheel");
            Assert.True(hasValue[8], "All should still have a value - it is fully explained by the front axle alone");
        }

        [Fact]
        public void MutationGuard_coalescing_a_missing_wheel_to_zero_would_understate_a_real_event()
        {
            // Pins the WRONG number a "coalesce absent to 0" mutation would produce, so a future
            // regression that silently reintroduces that dilution is caught even without re-deriving it
            // by hand: Aggregator.Compute (the ORIGINAL, non-availability-aware method, still used
            // elsewhere for values that are already fully resolved) treats every wheel as reported by
            // construction - exactly the behaviour that must NOT apply once a wheel is genuinely absent.
            var asIfCoalescedToZero = new Corners(80.0, 80.0, 0.0, 0.0);
            WheelAggregate wrongIfCoalesced = Aggregator.Compute(asIfCoalescedToZero, AggregationWeights.LockDefaults);
            Assert.Equal(72.0, wrongIfCoalesced.All, 6); // the wrong answer a coalesce-to-zero regression would produce

            WheelAggregate correct = Aggregator.ComputeAvailable(80.0, 80.0, null, null, AggregationWeights.LockDefaults, out _);
            Assert.NotEqual(wrongIfCoalesced.All, correct.All, 3);
            Assert.Equal(80.0, correct.All, 9);
        }

        [Fact]
        public void Every_wheel_absent_leaves_every_aggregate_absent_not_a_fabricated_zero()
        {
            WheelAggregate result = Aggregator.ComputeAvailable(null, null, null, null, AggregationWeights.LockDefaults, out bool[] hasValue);

            Assert.All(hasValue, has => Assert.False(has));
            // The numeric fields are harmless placeholders only - never published as a real reading (see
            // PropertyPublisher's own nullable Raw-tier publish) - but pinned at 0 here so a caller that
            // ignores hasValue by mistake at least gets a safe, inert number rather than garbage.
            Assert.Equal(0.0, result.All, 9);
        }

        // ------------------------------------------------------------------------------------
        // (c) END-TO-END THROUGH RawCalculatorEngine - a channel with nothing supplied publishes
        // absent, not zero; a per-wheel dropout does not silently corrupt the whole channel's aggregate.
        // ------------------------------------------------------------------------------------

        private static TelemetrySample Sample(
            double? newRpm = 5000.0, double? oldRpm = 5000.0, double speedKmh = 80.0, double? oldSpeedKmh = null, double brakePercent = 90.0)
        {
            var newFrame = new TelemetryFrame(speedKmh: speedKmh, rpm: newRpm, gear: "3", brakePercent: brakePercent, throttlePercent: 0.0, clutchPercent: 0.0);
            var oldFrame = new TelemetryFrame(speedKmh: oldSpeedKmh ?? speedKmh + 5.0, rpm: oldRpm, gear: "3");
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow);
        }

        [Fact]
        public void A_title_reporting_no_usable_capability_at_all_publishes_absent_not_zero()
        {
            // No wheel telemetry, no speed/RPM capability either - WheelSlipBranchSelector resolves
            // NoBranchMatched for both channels. Previously documented (and published) as "a genuine
            // zero" - corrected here to ABSENT (nothing was ever measured, so there is nothing honest to
            // report as zero).
            var raw = new RawWheelTelemetrySnapshot(); // every field null/false by construction (Empty-shaped)
            var engine = new RawCalculatorEngine();

            var result = engine.Compute(Sample(), thresholds: null, lockAggregation: null, slipAggregation: null, rawTelemetry: raw);

            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, result.SelectedLockBranch);
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, result.SelectedSlipBranch);
            Assert.All(result.LockHasValue, has => Assert.False(has));
            Assert.All(result.SlipHasValue, has => Assert.False(has));
        }

        [Fact]
        public void A_per_wheel_RPS_dropout_excludes_only_the_affected_wheels_not_the_whole_channel()
        {
            // FL/FR genuinely report a rotation rate well below their own warmed-up cruise reference (a
            // real, non-zero, non-trivial lock reading); RL/RR simply did not report this particular
            // frame (null, despite the title's capability otherwise being RPS-driven) - a defensive edge
            // case RawWheelTelemetryBuilder's own capability gate does not need to handle (it gates the
            // whole array, not one slot), but which the aggregation layer must still not zero-dilute.
            var engine = new RawCalculatorEngine();
            var thresholds = LegacyThresholds.Defaults;

            var warmupRaw = new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: 9.0, wheelRpsFrontRight: 9.0, wheelRpsRearLeft: 9.0, wheelRpsRearRight: 9.0,
                capabilityWheelsRPS: true, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false);
            var cruiseSample = Sample(newRpm: 5000.0, oldRpm: 5000.0, speedKmh: 90.0, oldSpeedKmh: 90.0, brakePercent: 0.0);
            for (int i = 0; i < 600; i++) engine.Compute(cruiseSample, thresholds, null, null, warmupRaw);

            // Query frame: decelerating, FL/FR report a rotation rate consistent with real lock (well
            // below the ~9/90=0.1 cruise ratio just warmed up), RL/RR report NOTHING this frame.
            var dropoutRaw = new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: 1.0, wheelRpsFrontRight: 1.0, wheelRpsRearLeft: null, wheelRpsRearRight: null,
                capabilityWheelsRPS: true, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false);
            var newFrame = new TelemetryFrame(speedKmh: 60.0, rpm: 5000.0, gear: "3", brakePercent: 80.0, throttlePercent: 0.0, clutchPercent: 0.0);
            var oldFrame = new TelemetryFrame(speedKmh: 90.0, rpm: 5000.0, gear: "3");
            var decelerating = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow);

            var result = engine.Compute(decelerating, thresholds, null, null, dropoutRaw);

            Assert.Equal(WheelSlipBranchNames.Rps, result.SelectedLockBranch);

            // Targets order: FrontLeft, FrontRight, RearLeft, RearRight, Front, Rear, Left, Right, All.
            Assert.True(result.LockHasValue[0], "FrontLeft reported a real rotation rate");
            Assert.True(result.LockHasValue[1], "FrontRight reported a real rotation rate");
            Assert.False(result.LockHasValue[2], "RearLeft reported nothing this frame");
            Assert.False(result.LockHasValue[3], "RearRight reported nothing this frame");
            Assert.True(result.LockHasValue[4], "Front is fully explained by FL/FR, both present");
            Assert.False(result.LockHasValue[5], "Rear has nothing to report - neither RL nor RR reported");
            Assert.True(result.LockFront > 0.0, $"expected a real, nonzero lock reading from the reporting front axle, got {result.LockFront}");

            // "All" must equal the front axle's own reading alone - NOT diluted by an invented rear zero
            // (AggregationWeights.LockDefaults' own Rear weight is 0.10 - a coalesce-to-zero regression
            // would read All at only 90% of Front, not 100%).
            Assert.Equal(result.LockFront, result.LockAll, 9);
        }

        // ------------------------------------------------------------------------------------
        // (d) LEARNERS NEVER OBSERVE AN ABSENT VALUE.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Hundreds_of_frames_with_absent_slip_ratio_never_mature_the_learned_distribution()
        {
            // SlipData's own learner (StreamingPercentileLearner) requires 200 real observations before
            // it will answer a percentile at all. Feed 300 frames where WheelSlipRatio is ALWAYS absent
            // (null) despite the capability being on, then ONE frame with a real slip ratio - if the 300
            // absent frames had silently fed the learner a coalesced 0.0 each time, it would already be
            // mature (well past 200) by the time the real frame arrives. It must not be: this proves
            // absent values never reached the learner at all.
            var raw = new RawWheelTelemetrySnapshot(
                wheelSlipRatioFrontLeft: null, wheelSlipRatioFrontRight: null, wheelSlipRatioRearLeft: null, wheelSlipRatioRearRight: null,
                capabilityWheelsSlip: true, capabilityWheelsRPS: false, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false);

            Assert.Equal(WheelSlipBranchNames.SlipData, WheelSlipBranchSelector.Select(raw, isLock: false));

            var engine = new RawCalculatorEngine();
            for (int i = 0; i < 300; i++)
                engine.Compute(Sample(speedKmh: 60.0), thresholds: null, lockAggregation: null, slipAggregation: null, rawTelemetry: raw);

            // A single real observation now - if the learner were already mature from the absent frames,
            // this dispatch would report a real percentile-derived Slip reading instead of the
            // rotation-spread-only floor (0, since brake/rotation capability is off here too).
            var rawWithOneRealSample = new RawWheelTelemetrySnapshot(
                wheelSlipRatioFrontLeft: 0.02, wheelSlipRatioFrontRight: 0.02, wheelSlipRatioRearLeft: 0.02, wheelSlipRatioRearRight: 0.02,
                capabilityWheelsSlip: true, capabilityWheelsRPS: false, capabilityWheelSlipUseSimpleBraking: false,
                capabilityDetectLockFromWheelsSpeed: false, capabilityDetectLockFromWheelsRPSAndDummyRadius: false);
            var result = engine.Compute(Sample(speedKmh: 60.0), thresholds: null, lockAggregation: null, slipAggregation: null, rawTelemetry: rawWithOneRealSample);

            Assert.Equal(0.0, result.SlipAll, 6);
        }
    }
}
