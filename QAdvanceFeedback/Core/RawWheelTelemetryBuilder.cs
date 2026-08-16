namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Pure, SimHub-free gating logic behind <see cref="RawWheelTelemetrySnapshot"/> - see that
    /// class's own remarks for the full "why" (SimHub's raw per-wheel arrays default to real zeros
    /// even when a title never supplies them at all, so the array's own contents can never
    /// distinguish "genuinely zero" from "not supplied"; only the matching
    /// <c>FeedbackCapabilities</c> flag can). Kept entirely free of SimHub types (only
    /// <c>double</c>/<c>double?</c>/<c>bool?</c>/<c>string</c> in and out) so THIS is the one place
    /// that exact distinction - the thing a previous pass's now-retracted "all zero, so
    /// architecturally impossible" reasoning could have gotten wrong the same way - is unit-tested
    /// directly, not only ever exercised via a live SimHub session (docs\telemetry-diagnostics-report.md).
    /// <para/>
    /// The SimHub-facing side (<c>SimHubTelemetryAdapter.cs</c>) does nothing
    /// more than read <c>FeedbackData.WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> and
    /// <c>FeedbackCapabilities</c>' own bool properties and hand them straight to <see cref="Build"/> -
    /// exactly the same "Layer 2 is a thin mapping, Layer 1 holds the shape/logic" split this project
    /// already uses for <see cref="TelemetryFrame"/>.
    /// </summary>
    public static class RawWheelTelemetryBuilder
    {
        public static RawWheelTelemetrySnapshot Build(
            bool? capabilityWheelsRPS, double[] wheelRps,
            bool? capabilityWheelsSpeed, double[] wheelSpeed,
            bool? capabilityWheelsSlip, double[] wheelSlipRatio,
            bool? capabilityWheelsSlipDirectMode = null,
            bool? capabilityWheelSlipUseSimpleBraking = null,
            bool? capabilityDetectLockFromWheelsSpeed = null,
            bool? capabilityDetectLockFromWheelsRPSAndDummyRadius = null,
            bool? capabilitySpeed = null,
            bool? capabilityRpm = null,
            string capabilityGameFamily = null,
            double? capabilityWheelSlipCalibrationProviderSlipScale = null)
        {
            GateWheelArray(capabilityWheelsRPS, wheelRps,
                out double? rpsFrontLeft, out double? rpsFrontRight, out double? rpsRearLeft, out double? rpsRearRight);
            GateWheelArray(capabilityWheelsSpeed, wheelSpeed,
                out double? speedFrontLeft, out double? speedFrontRight, out double? speedRearLeft, out double? speedRearRight);
            GateWheelArray(capabilityWheelsSlip, wheelSlipRatio,
                out double? slipFrontLeft, out double? slipFrontRight, out double? slipRearLeft, out double? slipRearRight);

            return new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: rpsFrontLeft, wheelRpsFrontRight: rpsFrontRight,
                wheelRpsRearLeft: rpsRearLeft, wheelRpsRearRight: rpsRearRight,
                wheelSpeedFrontLeft: speedFrontLeft, wheelSpeedFrontRight: speedFrontRight,
                wheelSpeedRearLeft: speedRearLeft, wheelSpeedRearRight: speedRearRight,
                wheelSlipRatioFrontLeft: slipFrontLeft, wheelSlipRatioFrontRight: slipFrontRight,
                wheelSlipRatioRearLeft: slipRearLeft, wheelSlipRatioRearRight: slipRearRight,
                capabilityWheelsSlip: capabilityWheelsSlip,
                capabilityWheelsRPS: capabilityWheelsRPS,
                capabilityWheelsSpeed: capabilityWheelsSpeed,
                capabilityWheelsSlipDirectMode: capabilityWheelsSlipDirectMode,
                capabilityWheelSlipUseSimpleBraking: capabilityWheelSlipUseSimpleBraking,
                capabilityDetectLockFromWheelsSpeed: capabilityDetectLockFromWheelsSpeed,
                capabilityDetectLockFromWheelsRPSAndDummyRadius: capabilityDetectLockFromWheelsRPSAndDummyRadius,
                capabilitySpeed: capabilitySpeed,
                capabilityRpm: capabilityRpm,
                capabilityGameFamily: capabilityGameFamily,
                capabilityWheelSlipCalibrationProviderSlipScale: capabilityWheelSlipCalibrationProviderSlipScale);
        }

        /// <summary>
        /// THE gate: only ever copies real numbers out of <paramref name="source"/> when
        /// <paramref name="capabilityAvailable"/> is a KNOWN <c>true</c> - both a known <c>false</c>
        /// (title does not supply this channel) and an unknown <c>null</c> (capability itself could
        /// not be reached this frame) leave every output null, the same conservative "cannot tell,
        /// so don't claim a zero" default <see cref="ITelemetryFrame"/> already uses elsewhere. Also
        /// defends against a null/short array even when the capability says yes (should not happen on
        /// a real SimHub session, but this is diagnostics code - it must never throw).
        /// </summary>
        private static void GateWheelArray(
            bool? capabilityAvailable, double[] source,
            out double? frontLeft, out double? frontRight, out double? rearLeft, out double? rearRight)
        {
            frontLeft = null;
            frontRight = null;
            rearLeft = null;
            rearRight = null;

            if (capabilityAvailable != true) return;
            if (source == null || source.Length < 4) return;

            frontLeft = source[0];
            frontRight = source[1];
            rearLeft = source[2];
            rearRight = source[3];
        }
    }
}
