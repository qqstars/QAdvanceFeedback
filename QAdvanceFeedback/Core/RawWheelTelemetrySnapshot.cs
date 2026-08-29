namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// DIAGNOSTICS ONLY (docs\telemetry-diagnostics-report.md) - never read by this plugin's own
    /// Lock/Slip algorithm (Layer 3), never affects any product-tier published property. Snapshot of
    /// the raw per-wheel telemetry SimHub's own <c>GameReaderCommon.Feedback.FeedbackData</c> holds
    /// this frame (<c>WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> - all three are ordinary public
    /// <c>double[]</c> properties marked only with the no-op <c>[DoNotExpose]</c> marker, which hides
    /// them from SimHub's OWN property picker but has no effect whatsoever on a plugin reading them
    /// directly - see <c>docs\raw-match-rootcause-report.md</c> §2c for the retraction of the earlier,
    /// wrong "architecturally impossible" claim this snapshot exists to settle with real data), plus
    /// the <c>FeedbackCapabilities</c> flags that determine which Raw-layer signal shape is actually
    /// available for the currently running title - this snapshot's capability properties are named
    /// after those conditions, in the same priority order <see cref="WheelSlipBranchSelector"/> checks
    /// them.
    /// <para/>
    /// NULL VS 0 IS LOAD-BEARING HERE, exactly like <see cref="ITelemetryFrame"/>: SimHub's own
    /// <c>FeedbackData.WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> arrays default to a real
    /// <c>double[4]</c> of ZEROS even on titles that never populate them at all (confirmed by
    /// decompiling <c>GameReaderCommon.FeedbackData</c> - the field initialisers are
    /// <c>= new double[4]</c>, not <c>null</c>) - so the array's own contents can never, by
    /// themselves, distinguish "genuinely stationary/zero" from "this title does not supply this
    /// channel at all". The only way to tell those apart is the matching <c>FeedbackCapabilities</c>
    /// flag (<c>WheelsRPS</c>/<c>WheelsSpeed</c>/<c>WheelsSlip</c>), which is exactly what
    /// <see cref="RawWheelTelemetryBuilder.Build"/> gates on before it will ever populate one of this
    /// class's per-wheel properties with a real number - every property here is null until that gate
    /// says the title genuinely supplies the channel. Getting this distinction wrong (treating a
    /// title's unsupported-channel zero as if it were a real reading) is exactly the kind of
    /// confusion a previous pass's "all zero => branch cannot be live" argument could have fallen
    /// into had it had this telemetry without this safeguard - see that report's own §2c/§2d.
    /// </summary>
    public sealed class RawWheelTelemetrySnapshot
    {
        public double? WheelRpsFrontLeft { get; }
        public double? WheelRpsFrontRight { get; }
        public double? WheelRpsRearLeft { get; }
        public double? WheelRpsRearRight { get; }

        public double? WheelSpeedFrontLeft { get; }
        public double? WheelSpeedFrontRight { get; }
        public double? WheelSpeedRearLeft { get; }
        public double? WheelSpeedRearRight { get; }

        /// <summary>The raw per-wheel slip RATIO from <c>FeedbackData.WheelSlip</c> - deliberately
        /// named "WheelSlipRatio" here (not "WheelSlip") so it is never confused with this plugin's
        /// OWN <c>WheelSlip.*</c> published channel, which is a completely different, already-computed
        /// value.</summary>
        public double? WheelSlipRatioFrontLeft { get; }
        public double? WheelSlipRatioFrontRight { get; }
        public double? WheelSlipRatioRearLeft { get; }
        public double? WheelSlipRatioRearRight { get; }

        /// <summary>Null when <c>FeedbackCapabilities</c> itself could not be reached this frame
        /// (see <see cref="RawWheelTelemetryBuilder"/>'s remarks) - distinct from a real, known
        /// <c>false</c>.</summary>
        /// <summary>SimHub's own <c>FeedbackData.IsFlying</c>. Non-zero suppresses the wheel-speed-delta
        /// branch entirely - airborne wheels spin freely, so wheelSpeed/groundSpeed diverges wildly and
        /// would otherwise read as maximum slip. Most relevant on rally titles, which are also the ones
        /// most likely to select that branch. Zero when the title does not report it.</summary>
        public int IsFlying { get; }

        /// <summary>SimHub's own <c>FeedbackData.OrientationYawChangePerSecond</c>. Used only to EXCLUDE
        /// cornering frames from the per-gear wheel-speed-delta reference - in a corner the inner and
        /// outer wheels genuinely travel different distances, so a delta measured there describes the
        /// corner rather than the car. Null when the title does not report it.</summary>
        public double? OrientationYawChangePerSecond { get; }

        public bool? CapabilityWheelsSlip { get; }
        public bool? CapabilityWheelsRPS { get; }
        public bool? CapabilityWheelsSpeed { get; }
        public bool? CapabilityWheelsSlipDirectMode { get; }
        public bool? CapabilityWheelSlipUseSimpleBraking { get; }
        public bool? CapabilityDetectLockFromWheelsSpeed { get; }
        public bool? CapabilityDetectLockFromWheelsRPSAndDummyRadius { get; }
        public bool? CapabilitySpeed { get; }
        public bool? CapabilityRpm { get; }
        public string CapabilityGameFamily { get; }
        public double? CapabilityWheelSlipCalibrationProviderSlipScale { get; }

        /// <summary>Every reading absent - the honest "could not capture this frame" default, mirroring
        /// <see cref="TelemetryFrame.Empty"/>'s own philosophy.</summary>
        public static readonly RawWheelTelemetrySnapshot Empty = new RawWheelTelemetrySnapshot();

        public RawWheelTelemetrySnapshot(
            double? wheelRpsFrontLeft = null, double? wheelRpsFrontRight = null,
            double? wheelRpsRearLeft = null, double? wheelRpsRearRight = null,
            double? wheelSpeedFrontLeft = null, double? wheelSpeedFrontRight = null,
            double? wheelSpeedRearLeft = null, double? wheelSpeedRearRight = null,
            double? wheelSlipRatioFrontLeft = null, double? wheelSlipRatioFrontRight = null,
            double? wheelSlipRatioRearLeft = null, double? wheelSlipRatioRearRight = null,
            bool? capabilityWheelsSlip = null, bool? capabilityWheelsRPS = null, bool? capabilityWheelsSpeed = null,
            bool? capabilityWheelsSlipDirectMode = null, bool? capabilityWheelSlipUseSimpleBraking = null,
            bool? capabilityDetectLockFromWheelsSpeed = null, bool? capabilityDetectLockFromWheelsRPSAndDummyRadius = null,
            bool? capabilitySpeed = null, bool? capabilityRpm = null, string capabilityGameFamily = null,
            double? capabilityWheelSlipCalibrationProviderSlipScale = null,
            int isFlying = 0,
            double? orientationYawChangePerSecond = null)
        {
            WheelRpsFrontLeft = wheelRpsFrontLeft;
            WheelRpsFrontRight = wheelRpsFrontRight;
            WheelRpsRearLeft = wheelRpsRearLeft;
            WheelRpsRearRight = wheelRpsRearRight;

            WheelSpeedFrontLeft = wheelSpeedFrontLeft;
            WheelSpeedFrontRight = wheelSpeedFrontRight;
            WheelSpeedRearLeft = wheelSpeedRearLeft;
            WheelSpeedRearRight = wheelSpeedRearRight;

            WheelSlipRatioFrontLeft = wheelSlipRatioFrontLeft;
            WheelSlipRatioFrontRight = wheelSlipRatioFrontRight;
            WheelSlipRatioRearLeft = wheelSlipRatioRearLeft;
            WheelSlipRatioRearRight = wheelSlipRatioRearRight;

            CapabilityWheelsSlip = capabilityWheelsSlip;
            CapabilityWheelsRPS = capabilityWheelsRPS;
            CapabilityWheelsSpeed = capabilityWheelsSpeed;
            CapabilityWheelsSlipDirectMode = capabilityWheelsSlipDirectMode;
            CapabilityWheelSlipUseSimpleBraking = capabilityWheelSlipUseSimpleBraking;
            CapabilityDetectLockFromWheelsSpeed = capabilityDetectLockFromWheelsSpeed;
            CapabilityDetectLockFromWheelsRPSAndDummyRadius = capabilityDetectLockFromWheelsRPSAndDummyRadius;
            CapabilitySpeed = capabilitySpeed;
            CapabilityRpm = capabilityRpm;
            CapabilityGameFamily = capabilityGameFamily;
            CapabilityWheelSlipCalibrationProviderSlipScale = capabilityWheelSlipCalibrationProviderSlipScale;
            IsFlying = isFlying;
            OrientationYawChangePerSecond = orientationYawChangePerSecond;
        }
    }
}
