using System;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Resolves "how hard is the car changing speed right now, in g" from ONLY the fields Layer 1's
    /// <see cref="ITelemetryFrame"/> exposes - the brief's hard restriction for Layer 4 ("do NOT use
    /// third-party or derived data ... only values reachable through the Layer 1 interface").
    /// <para/>
    /// THE DEGRADATION LADDER (state clearly per the brief):
    /// <list type="number">
    /// <item><b>Direct</b> - <see cref="ITelemetryFrame.LongitudinalG"/> is present this frame: its
    /// magnitude is used directly.</item>
    /// <item><b>Derived</b> - LongitudinalG is absent, but two consecutive frames' ground speed
    /// (<see cref="ITelemetryFrame.GroundSpeedKmh"/>, falling back to
    /// <see cref="ITelemetryFrame.SpeedKmh"/> exactly like Layer 3's own fallback) and a valid,
    /// positive <see cref="ITelemetrySample.Dt"/> are available: differentiated speed
    /// (delta-v / delta-t) stands in for g.</item>
    /// <item><b>Unavailable</b> - neither is possible (no G channel, and either no speed channel or
    /// no valid Dt yet - e.g. the very first frame of a session). The caller
    /// (<see cref="NormalizedWheelLockSlipEngine"/>) is REQUIRED to still produce a sane result in this
    /// case (the brief: "must still produce something sane rather than zero or garbage") by falling
    /// all the way back to Layer 3's own Raw value, unmodified - see that class's remarks.</item>
    /// </list>
    /// <para/>
    /// DELIBERATELY SIGN-AGNOSTIC: this returns a MAGNITUDE, never a signed value, and callers gate
    /// "is this frame relevant to braking vs. throttle" purely by which PEDAL is committed
    /// (<see cref="ITelemetryFrame.BrakePercent"/> / <see cref="ITelemetryFrame.ThrottlePercent"/>),
    /// never by the sign of <see cref="ITelemetryFrame.LongitudinalG"/>. This is a deliberate design
    /// choice, not an oversight: Layer 1's own doc comment for LongitudinalG names only the SimHub
    /// source field (<c>AccelerationSurge</c>) with no documented sign convention, and Layer 2's own
    /// remarks already flag one other field's unit as an unverified inference rather than a proven
    /// fact - adding a SECOND unverifiable assumption (which sign means "slowing down") on top of an
    /// already-uncertain field would compound risk for no benefit, since the magnitude alone, gated by
    /// pedal state, is sufficient for this layer's purpose (how close to the car's own limit is this
    /// frame), regardless of which sign convention any given game happens to use.
    /// </summary>
    public static class AchievedMotion
    {
        private const double MetersPerSecondSquaredPerG = 9.80665;

        public enum SignalLevel { Direct, Derived, Unavailable }

        public readonly struct Result
        {
            public readonly double MagnitudeG;
            public readonly SignalLevel Level;

            public Result(double magnitudeG, SignalLevel level)
            {
                MagnitudeG = magnitudeG;
                Level = level;
            }

            public static readonly Result Unavailable = new Result(0.0, SignalLevel.Unavailable);
        }

        public static Result Resolve(ITelemetrySample sample)
        {
            if (sample == null) return Result.Unavailable;

            double? g = sample.New?.LongitudinalG;
            if (g.HasValue && ClampMath.IsFinite(g.Value))
                return new Result(Math.Abs(g.Value), SignalLevel.Direct);

            double? newSpeed = sample.New?.GroundSpeedKmh ?? sample.New?.SpeedKmh;
            double? oldSpeed = sample.Old?.GroundSpeedKmh ?? sample.Old?.SpeedKmh;
            double? dtSeconds = sample.Dt?.TotalSeconds;

            if (newSpeed.HasValue && oldSpeed.HasValue && dtSeconds.HasValue && dtSeconds.Value > 0.0)
            {
                double deltaKmh = newSpeed.Value - oldSpeed.Value;
                double deltaMs = deltaKmh / 3.6;
                double accelMs2 = deltaMs / dtSeconds.Value;
                double accelG = accelMs2 / MetersPerSecondSquaredPerG;

                if (ClampMath.IsFinite(accelG))
                    return new Result(Math.Abs(accelG), SignalLevel.Derived);
            }

            return Result.Unavailable;
        }
    }
}
