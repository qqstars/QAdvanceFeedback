namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The inverse of <see cref="KeyDataNormalization.Normalize"/>: the source value that maps to a given
    /// normalized value. Normalize is monotone non-decreasing on [0,100], so this bisects it.
    /// </summary>
    public static class KeyDataNormalizationInverse
    {
        /// <summary>The smallest source in [0,100] whose normalized value reaches <paramref name="normalized0100"/>.
        /// Returns 100 when the target is at or above Normalize(100), and 0 for a target &lt;= 0 or NaN.</summary>
        public static double SourceFor(double normalized0100, bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)
        {
            if (double.IsNaN(normalized0100) || normalized0100 <= 0.0) return 0.0;
            if (normalized0100 >= KeyDataNormalization.Normalize(100.0, isLock, maxGripOnly, sMax, s90, s75)) return 100.0;

            double lo = 0.0, hi = 100.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (KeyDataNormalization.Normalize(mid, isLock, maxGripOnly, sMax, s90, s75) >= normalized0100) hi = mid;
                else lo = mid;
            }
            return hi;
        }
    }
}
