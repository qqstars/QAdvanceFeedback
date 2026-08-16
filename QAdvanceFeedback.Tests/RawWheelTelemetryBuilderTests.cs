using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="RawWheelTelemetryBuilder"/> - the SimHub-free gating logic behind
    /// <see cref="RawWheelTelemetrySnapshot"/>. THIS is the part of docs\telemetry-diagnostics-report.md
    /// the brief calls out as mattering "more than anything else": SimHub's own
    /// <c>FeedbackData.WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> arrays default to a real
    /// <c>double[4]</c> of zeros even on titles that never populate them at all, so the array's own
    /// contents alone can never distinguish "genuinely stationary" from "not supplied" - only the
    /// matching <c>FeedbackCapabilities</c> flag can. Exercised here entirely without SimHub, using
    /// plain arrays/bools that stand in for what a real capture would hand over.
    /// </summary>
    public class RawWheelTelemetryBuilderTests
    {
        private static readonly double[] RealReadings = { 12.5, 12.7, 11.9, 12.1 };
        private static readonly double[] GenuineZeros = { 0.0, 0.0, 0.0, 0.0 };

        [Fact]
        public void Capability_true_with_real_readings_publishes_the_real_numbers()
        {
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: RealReadings,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Equal(12.5, snapshot.WheelRpsFrontLeft);
            Assert.Equal(12.7, snapshot.WheelRpsFrontRight);
            Assert.Equal(11.9, snapshot.WheelRpsRearLeft);
            Assert.Equal(12.1, snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void Capability_true_with_genuinely_zero_readings_publishes_real_zeros_not_null()
        {
            // The car IS genuinely stationary/wheels genuinely not rotating THIS frame, and the title
            // DOES actively support this channel - 0.0 here is a real, meaningful reading, not a
            // "missing" placeholder, and must come through as exactly 0.0, not null.
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: GenuineZeros,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.True(snapshot.WheelRpsFrontLeft.HasValue);
            Assert.Equal(0.0, snapshot.WheelRpsFrontLeft.Value);
            Assert.True(snapshot.WheelRpsRearRight.HasValue);
            Assert.Equal(0.0, snapshot.WheelRpsRearRight.Value);
        }

        [Fact]
        public void Capability_false_publishes_null_even_though_the_array_holds_real_looking_numbers()
        {
            // THE core scenario this whole builder exists for: SimHub's own array defaults to zeros
            // (or, here, even non-zero leftovers) regardless of whether the title supports the
            // channel - a known-false capability must still suppress trusting it.
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: false, wheelRps: RealReadings,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void Capability_false_with_all_zero_array_publishes_null_not_a_fabricated_zero()
        {
            // The exact confusion the brief warns about: a title that does not support the channel
            // ALSO reports all zeros (SimHub's own default), and this must NOT be mistaken for a real
            // zero reading - it must come through as absent (null).
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: false, wheelRps: GenuineZeros,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void Capability_unknown_null_is_treated_as_conservatively_as_a_known_false()
        {
            // FeedbackCapabilities itself could not be reached this frame - "unknown" must not be
            // treated as "assume yes"; it degrades exactly like a known-false capability.
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: null, wheelRps: RealReadings,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void Capability_true_but_array_null_defends_by_publishing_null_rather_than_throwing()
        {
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: null,
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void Capability_true_but_array_too_short_defends_by_publishing_null_rather_than_throwing()
        {
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: new[] { 1.0, 2.0 },
                capabilityWheelsSpeed: null, wheelSpeed: null,
                capabilityWheelsSlip: null, wheelSlipRatio: null);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
        }

        [Fact]
        public void The_three_channels_gate_independently()
        {
            // WheelsRPS available but WheelsSpeed/WheelsSlip are not - each channel's gate must be
            // evaluated on its OWN capability flag, not a single combined switch.
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: RealReadings,
                capabilityWheelsSpeed: false, wheelSpeed: RealReadings,
                capabilityWheelsSlip: null, wheelSlipRatio: RealReadings);

            Assert.NotNull(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelSpeedFrontLeft);
            Assert.Null(snapshot.WheelSlipRatioFrontLeft);
        }

        [Fact]
        public void Capability_flags_and_metadata_pass_through_verbatim()
        {
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: true, wheelRps: RealReadings,
                capabilityWheelsSpeed: false, wheelSpeed: RealReadings,
                capabilityWheelsSlip: true, wheelSlipRatio: RealReadings,
                capabilityWheelsSlipDirectMode: false,
                capabilityWheelSlipUseSimpleBraking: true,
                capabilityDetectLockFromWheelsSpeed: false,
                capabilityDetectLockFromWheelsRPSAndDummyRadius: true,
                capabilitySpeed: true,
                capabilityRpm: true,
                capabilityGameFamily: "Road",
                capabilityWheelSlipCalibrationProviderSlipScale: 42.0);

            Assert.Equal(true, snapshot.CapabilityWheelsRPS);
            Assert.Equal(false, snapshot.CapabilityWheelsSpeed);
            Assert.Equal(true, snapshot.CapabilityWheelsSlip);
            Assert.Equal(false, snapshot.CapabilityWheelsSlipDirectMode);
            Assert.Equal(true, snapshot.CapabilityWheelSlipUseSimpleBraking);
            Assert.Equal(false, snapshot.CapabilityDetectLockFromWheelsSpeed);
            Assert.Equal(true, snapshot.CapabilityDetectLockFromWheelsRPSAndDummyRadius);
            Assert.Equal(true, snapshot.CapabilitySpeed);
            Assert.Equal(true, snapshot.CapabilityRpm);
            Assert.Equal("Road", snapshot.CapabilityGameFamily);
            Assert.Equal(42.0, snapshot.CapabilityWheelSlipCalibrationProviderSlipScale);
        }

        [Fact]
        public void Every_capability_entirely_unreachable_leaves_every_capability_field_null()
        {
            // Mirrors exactly what SimHubTelemetryAdapter.CaptureRawTelemetry passes when
            // PluginManager.GameManager.GetFeedbackCapabilities() itself returns null this frame -
            // every capability argument comes in as null, not just the three gating ones.
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetryBuilder.Build(
                capabilityWheelsRPS: null, wheelRps: RealReadings,
                capabilityWheelsSpeed: null, wheelSpeed: RealReadings,
                capabilityWheelsSlip: null, wheelSlipRatio: RealReadings);

            Assert.Null(snapshot.CapabilityWheelsRPS);
            Assert.Null(snapshot.CapabilityWheelsSpeed);
            Assert.Null(snapshot.CapabilityWheelsSlip);
            Assert.Null(snapshot.CapabilityWheelsSlipDirectMode);
            Assert.Null(snapshot.CapabilityWheelSlipUseSimpleBraking);
            Assert.Null(snapshot.CapabilityDetectLockFromWheelsSpeed);
            Assert.Null(snapshot.CapabilityDetectLockFromWheelsRPSAndDummyRadius);
            Assert.Null(snapshot.CapabilitySpeed);
            Assert.Null(snapshot.CapabilityRpm);
            Assert.Null(snapshot.CapabilityGameFamily);
            Assert.Null(snapshot.CapabilityWheelSlipCalibrationProviderSlipScale);

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelSpeedFrontLeft);
            Assert.Null(snapshot.WheelSlipRatioFrontLeft);
        }

        [Fact]
        public void Empty_snapshot_has_every_reading_absent()
        {
            RawWheelTelemetrySnapshot snapshot = RawWheelTelemetrySnapshot.Empty;

            Assert.Null(snapshot.WheelRpsFrontLeft);
            Assert.Null(snapshot.WheelRpsFrontRight);
            Assert.Null(snapshot.WheelRpsRearLeft);
            Assert.Null(snapshot.WheelRpsRearRight);
            Assert.Null(snapshot.WheelSpeedFrontLeft);
            Assert.Null(snapshot.WheelSlipRatioFrontLeft);
            Assert.Null(snapshot.CapabilityWheelsRPS);
            Assert.Null(snapshot.CapabilityGameFamily);
        }
    }
}
