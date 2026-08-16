using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="WheelSlipBranchSelector"/> - the pure, SimHub-free capability-priority
    /// dispatch this plugin's Raw layer uses to pick which signal shape a title supports. Every case
    /// here is drawn directly from SimHub's own real dispatch priority order - see each test's own
    /// comment for which condition it covers.
    /// </summary>
    public class WheelSlipBranchSelectorTests
    {
        private static RawWheelTelemetrySnapshot Caps(
            bool? wheelsSlip = null, bool? wheelsRps = null, bool? wheelsSpeed = null,
            bool? directMode = null, bool? simpleBraking = null,
            bool? detectFromSpeed = null, bool? detectFromRpsDummy = null,
            bool? speed = null, bool? rpm = null, double? slipScale = null)
            => new RawWheelTelemetrySnapshot(
                capabilityWheelsSlip: wheelsSlip, capabilityWheelsRPS: wheelsRps, capabilityWheelsSpeed: wheelsSpeed,
                capabilityWheelsSlipDirectMode: directMode, capabilityWheelSlipUseSimpleBraking: simpleBraking,
                capabilityDetectLockFromWheelsSpeed: detectFromSpeed,
                capabilityDetectLockFromWheelsRPSAndDummyRadius: detectFromRpsDummy,
                capabilitySpeed: speed, capabilityRpm: rpm,
                capabilityWheelSlipCalibrationProviderSlipScale: slipScale);

        [Fact]
        public void Null_snapshot_matches_no_branch_for_either_channel()
        {
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, WheelSlipBranchSelector.Select(null, isLock: true));
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, WheelSlipBranchSelector.Select(null, isLock: false));
        }

        /// <summary>THIS SESSION'S OWN CAPTURE (docs\telemetry-diagnostics-report.md /
        /// docs\branch-dispatch-and-source-keyed-learning-report.md): WheelsRPS=1, WheelsSlip=1,
        /// Speed=1, Rpm=1, everything else false/empty - Lock dispatches to RPS, Slip dispatches to
        /// Slip data. THE central fact this whole feature exists to act on.</summary>
        [Fact]
        public void This_sessions_capabilities_dispatch_Lock_to_RPS_and_Slip_to_SlipData()
        {
            RawWheelTelemetrySnapshot caps = Caps(wheelsSlip: true, wheelsRps: true, speed: true, rpm: true,
                wheelsSpeed: false, directMode: false, simpleBraking: false, detectFromSpeed: false, detectFromRpsDummy: false);

            Assert.Equal(WheelSlipBranchNames.Rps, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.SlipData, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch1_PrecalibratedSlip_is_Slip_only_and_takes_priority_over_everything_else()
        {
            RawWheelTelemetrySnapshot caps = Caps(slipScale: 5.0, wheelsRps: true, simpleBraking: true, wheelsSlip: true);

            Assert.Equal(WheelSlipBranchNames.PrecalibratedSlip, WheelSlipBranchSelector.Select(caps, isLock: false));
            // Lock never reaches branch 1 (its own condition requires !IsLock) - falls through to the
            // next branch whose OWN condition is satisfied (here, WheelSlipUseSimpleBraking).
            Assert.Equal(WheelSlipBranchNames.BrakingVsSpeed, WheelSlipBranchSelector.Select(caps, isLock: true));
        }

        [Fact]
        public void Branch2_RpsAndDummyRadius_is_Lock_only()
        {
            RawWheelTelemetrySnapshot caps = Caps(detectFromRpsDummy: true, wheelsRps: true, wheelsSlip: true);

            Assert.Equal(WheelSlipBranchNames.RpsAndDummyRadius, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.NotEqual(WheelSlipBranchNames.RpsAndDummyRadius, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch3_WheelSpeedLock_is_Lock_only_and_beaten_by_branch2()
        {
            RawWheelTelemetrySnapshot capsAlone = Caps(detectFromSpeed: true);
            Assert.Equal(WheelSlipBranchNames.WheelSpeedLock, WheelSlipBranchSelector.Select(capsAlone, isLock: true));

            RawWheelTelemetrySnapshot capsWithBothLockBranches = Caps(detectFromSpeed: true, detectFromRpsDummy: true);
            Assert.Equal(WheelSlipBranchNames.RpsAndDummyRadius, WheelSlipBranchSelector.Select(capsWithBothLockBranches, isLock: true));
        }

        [Fact]
        public void Branch4_BrakingVsSpeed_is_shared_between_Lock_and_Slip()
        {
            RawWheelTelemetrySnapshot caps = Caps(simpleBraking: true);
            Assert.Equal(WheelSlipBranchNames.BrakingVsSpeed, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.BrakingVsSpeed, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch5_Rps_is_Lock_only()
        {
            RawWheelTelemetrySnapshot caps = Caps(wheelsRps: true);
            Assert.Equal(WheelSlipBranchNames.Rps, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.NotEqual(WheelSlipBranchNames.Rps, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch6_DirectSlipFeedback_is_shared_and_always_zero_by_design()
        {
            RawWheelTelemetrySnapshot caps = Caps(directMode: true);
            Assert.Equal(WheelSlipBranchNames.DirectSlipFeedback, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.DirectSlipFeedback, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch7_SlipData_is_shared()
        {
            RawWheelTelemetrySnapshot caps = Caps(wheelsSlip: true);
            Assert.Equal(WheelSlipBranchNames.SlipData, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.SlipData, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch8_WheelsSpeed_is_shared()
        {
            RawWheelTelemetrySnapshot caps = Caps(wheelsSpeed: true);
            Assert.Equal(WheelSlipBranchNames.WheelsSpeed, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.WheelsSpeed, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Branch9_assumed_legacy_needs_both_Speed_and_Rpm_and_is_shared()
        {
            RawWheelTelemetrySnapshot bothPresent = Caps(speed: true, rpm: true);
            Assert.Equal(WheelSlipBranchNames.RpmVsSpeedAssumedLegacy, WheelSlipBranchSelector.Select(bothPresent, isLock: true));
            Assert.Equal(WheelSlipBranchNames.RpmVsSpeedAssumedLegacy, WheelSlipBranchSelector.Select(bothPresent, isLock: false));

            RawWheelTelemetrySnapshot onlySpeed = Caps(speed: true, rpm: false);
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, WheelSlipBranchSelector.Select(onlySpeed, isLock: true));
        }

        [Fact]
        public void No_capability_at_all_matches_no_branch_faithfully_matching_SimHubs_own_zero_result()
        {
            RawWheelTelemetrySnapshot caps = new RawWheelTelemetrySnapshot();
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, WheelSlipBranchSelector.Select(caps, isLock: true));
            Assert.Equal(WheelSlipBranchNames.NoBranchMatched, WheelSlipBranchSelector.Select(caps, isLock: false));
        }

        [Fact]
        public void Dispatch_order_matches_the_decompiled_source_exactly_branch1_beats_branch4_for_Slip()
        {
            // Precalibrated slip (branch 1) must win over Braking vs speed (branch 4) for the Slip
            // channel when both conditions are satisfied - exactly the decompiled if/else order.
            RawWheelTelemetrySnapshot caps = Caps(slipScale: 3.0, simpleBraking: true);
            Assert.Equal(WheelSlipBranchNames.PrecalibratedSlip, WheelSlipBranchSelector.Select(caps, isLock: false));
        }
    }
}
