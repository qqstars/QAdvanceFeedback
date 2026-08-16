namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Clamping used at the publish boundary. Every value this plugin actually publishes as a
    /// "Raw.*" property passes through <see cref="To0100"/> - this is what turns SimHub's own
    /// native-scale effect arithmetic (unbounded above in places, see
    /// <c>BrakeSpeedSlipModel</c>'s remarks on scaling) into the strictly 0-100 range the
    /// brief requires.
    /// </summary>
    public static class ClampMath
    {
        public static double Clamp(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            return value < min ? min : (value > max ? max : value);
        }

        public static double To0100(double value) => Clamp(value, 0.0, 100.0);

        public static double To01(double value) => Clamp(value, 0.0, 1.0);

        /// <summary>
        /// Added for Layers 4/5 (Normalized projection, monotone-cubic curve): ported verbatim from
        /// the sibling ReliableWheelLockSlip project's identically-named methods so both projects'
        /// curve/normalisation math stay textually identical. Purely additive - <see cref="Clamp"/>/
        /// <see cref="To0100"/>/<see cref="To01"/> above (Layers 1-3) are unchanged.
        /// </summary>
        public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Divides, returning <paramref name="fallback"/> when the divisor is zero or the
        /// result would not be finite - see <see cref="IsFinite"/>.</summary>
        public static double SafeDiv(double numerator, double denominator, double fallback)
        {
            if (denominator == 0.0) return fallback;
            double result = numerator / denominator;
            return IsFinite(result) ? result : fallback;
        }

        /// <summary>
        /// Converts a boxed value - typically whatever <c>PluginManager.GetPropertyValue</c> returned
        /// for a Layer 4 source field - to a <see cref="double"/>, returning <paramref name="fallback"/>
        /// for every failure mode: <paramref name="raw"/> is null (the named property does not exist,
        /// e.g. a ShakeIt "export output value as a property" checkbox the driver has not ticked yet,
        /// or ticked but under a different name/plugin than the one configured here), a type that
        /// cannot convert to double, or a conversion that produces a non-finite result.
        /// <para/>
        /// This is the exact SimHub-independent core of <c>WheelSourceResolver.Resolve</c>'s Plain-
        /// source path, pulled out here so it is unit-testable without a live SimHub
        /// <c>PluginManager</c> (which cannot be constructed or queried outside a running SimHub
        /// process). The fallback is silent and automatic BY CONSTRUCTION: there is no branch here
        /// that turns "missing" into <c>0.0</c> rather than <paramref name="fallback"/> - the only way
        /// to get <c>0.0</c> out of this method is for <paramref name="fallback"/> itself to be 0, or
        /// for <paramref name="raw"/> to genuinely convert to 0.
        /// </summary>
        public static double CoerceOrFallback(object raw, double fallback)
        {
            if (raw == null) return fallback;

            try
            {
                double converted = System.Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
                return IsFinite(converted) ? converted : fallback;
            }
            catch (System.Exception e) when (e is System.InvalidCastException || e is System.FormatException || e is System.OverflowException)
            {
                return fallback;
            }
        }
    }
}
