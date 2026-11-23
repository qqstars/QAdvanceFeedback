using System;

namespace QAdvanceFeedback.Core.Clutch
{
    /// <summary>
    /// The tunable clutch-bite learning parameters (docs\clutch-bite-design.md, "Learning parameters"),
    /// editable on the plugin page's Clutch tab only. A value type, compared by value, so the settings
    /// hub can carry the whole set as one field. Values are clamped by <see cref="Clamped"/>.
    /// </summary>
    public readonly struct ClutchLearningParameters : IEquatable<ClutchLearningParameters>
    {
        public static readonly ClutchLearningParameters Default = new ClutchLearningParameters(
            armClutchPercent: 90, rpmDipPercent: 4, confirmSpeedKmh: 2, maxReleaseRatePerSec: 30,
            maxThrottleSwing: 15, steadyIdlePercent: 2, matureSamples: 9, matureSpreadPoints: 3);

        public ClutchLearningParameters(double armClutchPercent, double rpmDipPercent, double confirmSpeedKmh,
            double maxReleaseRatePerSec, double maxThrottleSwing, double steadyIdlePercent, int matureSamples, double matureSpreadPoints)
        {
            ArmClutchPercent = armClutchPercent; RpmDipPercent = rpmDipPercent; ConfirmSpeedKmh = confirmSpeedKmh;
            MaxReleaseRatePerSec = maxReleaseRatePerSec; MaxThrottleSwing = maxThrottleSwing; SteadyIdlePercent = steadyIdlePercent;
            MatureSamples = matureSamples; MatureSpreadPoints = matureSpreadPoints;
        }

        /// <summary>Clutch pressed at least this far (0..100) while stopped arms a standing start.</summary>
        public double ArmClutchPercent { get; }
        /// <summary>RPM drop below the idle baseline that detects the bite, percent.</summary>
        public double RpmDipPercent { get; }
        /// <summary>Speed above standstill that confirms the car really pulled away, km/h.</summary>
        public double ConfirmSpeedKmh { get; }
        /// <summary>Fastest pedal movement at the bite that can still be measured, percent of travel per second.</summary>
        public double MaxReleaseRatePerSec { get; }
        /// <summary>Largest throttle change during the release, points.</summary>
        public double MaxThrottleSwing { get; }
        /// <summary>How steady the idle must be before a release counts, percent band.</summary>
        public double SteadyIdlePercent { get; }
        /// <summary>Samples needed to mature (also the blend ramp's full-trust count).</summary>
        public int MatureSamples { get; }
        /// <summary>Largest spread (standard deviation, pedal points) that matures.</summary>
        public double MatureSpreadPoints { get; }

        public static ClutchLearningParameters Clamped(double arm, double dip, double confirm, double rate, double throttle,
            double steady, int mature, double spread) => new ClutchLearningParameters(
            C(arm, 70, 99, 90), C(dip, 1, 15, 4), C(confirm, 0.5, 10, 2), C(rate, 5, 200, 30),
            C(throttle, 0, 100, 15), C(steady, 0.5, 10, 2), Math.Max(3, Math.Min(30, mature)), C(spread, 0.5, 10, 3));

        public ClutchLearningParameters With(double? armClutchPercent = null, double? rpmDipPercent = null, double? confirmSpeedKmh = null,
            double? maxReleaseRatePerSec = null, double? maxThrottleSwing = null, double? steadyIdlePercent = null,
            int? matureSamples = null, double? matureSpreadPoints = null) => Clamped(
            armClutchPercent ?? ArmClutchPercent, rpmDipPercent ?? RpmDipPercent, confirmSpeedKmh ?? ConfirmSpeedKmh,
            maxReleaseRatePerSec ?? MaxReleaseRatePerSec, maxThrottleSwing ?? MaxThrottleSwing, steadyIdlePercent ?? SteadyIdlePercent,
            matureSamples ?? MatureSamples, matureSpreadPoints ?? MatureSpreadPoints);

        public bool Equals(ClutchLearningParameters o) =>
            ArmClutchPercent.Equals(o.ArmClutchPercent) && RpmDipPercent.Equals(o.RpmDipPercent) && ConfirmSpeedKmh.Equals(o.ConfirmSpeedKmh)
            && MaxReleaseRatePerSec.Equals(o.MaxReleaseRatePerSec) && MaxThrottleSwing.Equals(o.MaxThrottleSwing)
            && SteadyIdlePercent.Equals(o.SteadyIdlePercent) && MatureSamples == o.MatureSamples && MatureSpreadPoints.Equals(o.MatureSpreadPoints);

        public override bool Equals(object obj) => obj is ClutchLearningParameters o && Equals(o);

        public override int GetHashCode() =>
            ArmClutchPercent.GetHashCode() ^ (RpmDipPercent.GetHashCode() * 3) ^ (MaxReleaseRatePerSec.GetHashCode() * 7) ^ (MatureSamples * 31);

        private static double C(double v, double min, double max, double fallback) =>
            ClampMath.Clamp(ClampMath.IsFinite(v) ? v : fallback, min, max);
    }
}
