namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Plain, immutable implementation of <see cref="ITelemetryFrame"/>. Used by both
    /// SimHubTelemetryAdapter (Layer 2, real games) and this project's own tests (Layer 3, hand-built
    /// frames) - kept here in Core rather than duplicated, since the type itself has zero SimHub
    /// dependency (only the code that POPULATES it from GameData does).
    /// </summary>
    public sealed class TelemetryFrame : ITelemetryFrame
    {
        public double? GroundSpeedKmh { get; }
        public double? SpeedKmh { get; }
        public double? GroundSpeedMps { get; }
        public double? Rpm { get; }
        public string Gear { get; }
        public double? BrakePercent { get; }
        public double? ThrottlePercent { get; }
        public double? ClutchPercent { get; }
        public double? LateralLocalVelocity { get; }
        public double? LongitudinalG { get; }
        public double? LateralG { get; }
        public bool? IsInPit { get; }
        public bool? IsReplay { get; }
        public bool? IsSessionRestart { get; }

        public bool? WheelOnLooseSurfaceFrontLeft { get; }
        public bool? WheelOnLooseSurfaceFrontRight { get; }
        public bool? WheelOnLooseSurfaceRearLeft { get; }
        public bool? WheelOnLooseSurfaceRearRight { get; }

        /// <summary>A frame with every reading absent - the honest "no data" default, not a frame of
        /// zeros pretending to be real readings.</summary>
        public static readonly TelemetryFrame Empty = new TelemetryFrame();

        public TelemetryFrame(
            double? groundSpeedKmh = null,
            double? speedKmh = null,
            double? groundSpeedMps = null,
            double? rpm = null,
            string gear = null,
            double? brakePercent = null,
            double? throttlePercent = null,
            double? clutchPercent = null,
            double? lateralLocalVelocity = null,
            double? longitudinalG = null,
            double? lateralG = null,
            bool? isInPit = null,
            bool? isReplay = null,
            bool? isSessionRestart = null,
            bool? wheelOnLooseSurfaceFrontLeft = null,
            bool? wheelOnLooseSurfaceFrontRight = null,
            bool? wheelOnLooseSurfaceRearLeft = null,
            bool? wheelOnLooseSurfaceRearRight = null)
        {
            GroundSpeedKmh = groundSpeedKmh;
            SpeedKmh = speedKmh;
            GroundSpeedMps = groundSpeedMps;
            Rpm = rpm;
            Gear = gear;
            BrakePercent = brakePercent;
            ThrottlePercent = throttlePercent;
            ClutchPercent = clutchPercent;
            LateralLocalVelocity = lateralLocalVelocity;
            LongitudinalG = longitudinalG;
            LateralG = lateralG;
            IsInPit = isInPit;
            IsReplay = isReplay;
            IsSessionRestart = isSessionRestart;
            WheelOnLooseSurfaceFrontLeft = wheelOnLooseSurfaceFrontLeft;
            WheelOnLooseSurfaceFrontRight = wheelOnLooseSurfaceFrontRight;
            WheelOnLooseSurfaceRearLeft = wheelOnLooseSurfaceRearLeft;
            WheelOnLooseSurfaceRearRight = wheelOnLooseSurfaceRearRight;
        }
    }
}
