namespace QAdvanceFeedback.Core.GForce
{
    /// <summary>
    /// The 8 pad outputs this model computes, one per rumble-seat shaker channel (see the brief's
    /// PUBLISH list and <see cref="GForcePublishedNames"/>). Every value is nullable and that is
    /// load-bearing, exactly like <see cref="ITelemetryFrame"/>'s own contract: null means "the
    /// game/session did not supply G-force telemetry this frame", NOT "zero output". A car standing
    /// still with a real <see cref="ITelemetryFrame.LongitudinalG"/> reading of 0.0 gets a real 0.0 on
    /// every channel; a game that doesn't expose G-force at all gets null on every channel. See
    /// <see cref="GForceEngine"/> for the one and only place that decides which of the two applies.
    /// <para/>
    /// Named "Bottom" (the seat-cushion pads), not the "Buttom" spelling used in the
    /// requirement text - per the standing correct-spelling rule, published names and identifiers are
    /// corrected even when guidance text spells them wrong.
    /// </summary>
    public sealed class GForceOutput
    {
        public double? BottomFrontLeft { get; }
        public double? BottomFrontRight { get; }
        public double? BottomRearLeft { get; }
        public double? BottomRearRight { get; }
        public double? BackLowLeft { get; }
        public double? BackLowRight { get; }
        public double? BackTopLeft { get; }
        public double? BackTopRight { get; }

        /// <summary>All eight channels null - the honest "no G-force data this frame" default.</summary>
        public static readonly GForceOutput Empty =
            new GForceOutput(null, null, null, null, null, null, null, null);

        public GForceOutput(
            double? bottomFrontLeft, double? bottomFrontRight, double? bottomRearLeft, double? bottomRearRight,
            double? backLowLeft, double? backLowRight, double? backTopLeft, double? backTopRight)
        {
            BottomFrontLeft = bottomFrontLeft;
            BottomFrontRight = bottomFrontRight;
            BottomRearLeft = bottomRearLeft;
            BottomRearRight = bottomRearRight;
            BackLowLeft = backLowLeft;
            BackLowRight = backLowRight;
            BackTopLeft = backTopLeft;
            BackTopRight = backTopRight;
        }

        /// <summary>True when every channel is null (see <see cref="Empty"/>).</summary>
        public bool IsEmpty =>
            !BottomFrontLeft.HasValue && !BottomFrontRight.HasValue &&
            !BottomRearLeft.HasValue && !BottomRearRight.HasValue &&
            !BackLowLeft.HasValue && !BackLowRight.HasValue &&
            !BackTopLeft.HasValue && !BackTopRight.HasValue;
    }
}
