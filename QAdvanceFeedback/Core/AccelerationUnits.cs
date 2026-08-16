namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// THE ONE PLACE the m/s^2-vs-G unit boundary is converted (see docs\field-fixes-report.md,
    /// defect A). SimHub's own <c>StatusDataBase.AccelerationSurge</c>/<c>AccelerationSway</c> are
    /// reported in metres/second^2 (confirmed against this session's own captured log: the raw
    /// magnitude runs p50~8.4, p90~19.8, max~194.5 - values that are plainly implausible as G
    /// (194.5 G would be lethal) but entirely ordinary as m/s^2, matching the sibling
    /// ReliableWheelLockSlip project's own <c>AccelerationScaleDetector</c> finding for the same
    /// title), while every consumer in this assembly (<c>ITelemetryFrame.LongitudinalG</c>/
    /// <c>LateralG</c>, <see cref="Normalized.AchievedMotion"/>, <see cref="GForce.GForceEngine"/>,
    /// <see cref="GForce.GForceMaxLearner"/>, <see cref="Normalized.GripLearner"/>'s
    /// <c>MaxPlausibleG</c>, and the G-force settings' FIXED maxima) is calibrated in G, by name and
    /// by every existing test's own fixture values.
    /// <para/>
    /// THE FIX: convert exactly once, at the Layer 1/2 boundary (<c>SimHubTelemetryAdapter.ToFrame</c>
    /// - the only caller), rather than teaching every downstream consumer to know SimHub's own
    /// native unit. Kept here (Core, no SimHub dependency) rather than inline in the SimHub-facing
    /// adapter so the conversion FACTOR itself is unit-testable without a SimHub reference (see
    /// <c>AccelerationUnitsTests</c>) even though the adapter call site that uses it is not (that
    /// class needs GameReaderCommon/SimHub types this net8.0 test project deliberately does not
    /// reference - see docs\architecture.md's own remarks on the SimHub-dependent/pure boundary).
    /// </summary>
    public static class AccelerationUnits
    {
        /// <summary>Standard gravity, m/s^2 per g - the conventional constant (not the local/measured
        /// value, which varies negligibly for this purpose).</summary>
        public const double MetersPerSecondSquaredPerG = 9.80665;

        /// <summary>Converts a metres/second^2 reading (SimHub's native <c>AccelerationSurge</c>/
        /// <c>AccelerationSway</c> scale) to G (this assembly's canonical internal unit for every
        /// longitudinal/lateral acceleration field from <see cref="ITelemetryFrame"/> downward).
        /// Sign-preserving; NaN/Infinity pass through unchanged (callers already guard for
        /// non-finite readings - see <see cref="ClampMath.IsFinite"/>).</summary>
        public static double Ms2ToG(double ms2) => ms2 / MetersPerSecondSquaredPerG;

        /// <summary>The inverse of <see cref="Ms2ToG"/> - provided for test fixtures/tools that need
        /// to construct a "raw SimHub-scale" reading from a known G value (e.g. reproducing this
        /// session's own captured m/s^2 magnitudes from a G-denominated test expectation).</summary>
        public static double GToMs2(double g) => g * MetersPerSecondSquaredPerG;
    }
}
