using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// A source value normalized by a channel's key data points (SMax/S90/S75) - the same piecewise
    /// construction the engine uses: the four-range curve under Mapping (S75->30, S90->60, SMax->80), or
    /// the two-range SMax curve under Max-Grip-Only. A missing value falls back to the channel default
    /// SMax, and S90/S75 to the fractions derived from SMax.
    /// <para/>
    /// Shared by the "Source value -> Projected value" graph and the WheelLock/WheelSlip Test Effect, so
    /// the test plays exactly what the graph shows.
    /// </summary>
    public static class KeyDataNormalization
    {
        /// <summary>The normalized value (0..100, before the projection curve; not yet clamped).</summary>
        public static double Normalize(double source0100, bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)
        {
            MonotoneCubicCurve curve = Curve(isLock, maxGripOnly, sMax, s90, s75, out double top);
            return curve != null
                ? curve.Evaluate(source0100)
                : ClampMath.To0100(top > 0.0 ? source0100 * KeyedScaleLearner.CanonicalAtLimitAnchor / top : source0100);
        }

        /// <summary>The range curve for these key data points, or null when they cannot form one (the
        /// plain SMax rescale applies then). <paramref name="top"/> is the SMax actually used.</summary>
        public static MonotoneCubicCurve Curve(bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75, out double top)
        {
            top = sMax ?? (isLock ? KeyDataPointSettings.LockDefaultSMax : KeyDataPointSettings.SlipDefaultSMax);
            double mid = s90 ?? top * KeyDataPointSettings.DerivedS90Fraction;
            double low = s75 ?? top * KeyDataPointSettings.DerivedS75Fraction;

            if (!(top > 0.0 && top < 100.0)) return null;
            if (maxGripOnly) return new MonotoneCubicCurve(new[] { 0.0, top, 100.0 }, new[] { 0.0, 80.0, 100.0 });
            return KeyDataPointSettings.IsValid(top, mid, low) && low > 0.0
                ? new MonotoneCubicCurve(new[] { 0.0, low, mid, top, 100.0 }, new[] { 0.0, 30.0, 60.0, 80.0, 100.0 })
                : null;
        }
    }
}
