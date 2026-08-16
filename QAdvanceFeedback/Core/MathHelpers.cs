using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// General-purpose clamping/remapping helpers shared by the Raw-layer formulas
    /// (<c>QAdvanceFeedback.Core.RawCalculator</c>): turning a value on one numeric range into the
    /// equivalent position on another, optionally clamped at either end.
    /// </summary>
    public static class MathHelpers
    {
        /// <summary>Clamps <paramref name="value"/> to <c>[lowRange, highRange]</c>.</summary>
        public static double Clamp(double value, double lowRange, double highRange)
            => Math.Max(Math.Min(value, highRange), lowRange);

        /// <summary>
        /// Linear remap of <paramref name="x"/> from <c>[inMin, inMax]</c> onto <c>[outMin, outMax]</c>,
        /// optionally constrained (clamped) to the output range afterwards.
        /// </summary>
        public static double Map(double x, double inMin, double inMax, double outMin, double outMax, bool constrain = false)
        {
            double result = (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
            if (constrain)
            {
                result = Math.Max(Math.Min(outMin, outMax), result);
                result = Math.Min(Math.Max(outMin, outMax), result);
            }
            return result;
        }

        /// <summary>
        /// Where <paramref name="value"/> sits between <paramref name="min"/> and <paramref name="max"/>
        /// as a 0-1 ratio: 0 at/below <paramref name="min"/>, 1 at/above <paramref name="max"/> when
        /// <paramref name="clamp"/> is true (both ends clamped together); otherwise the raw, unclamped
        /// linear ratio (which can go below 0 or above 1).
        /// </summary>
        public static double Offset(double min, double max, double value, bool clamp = true)
        {
            if (min == max) return 0.0;

            bool inverted = false;
            if (max < min)
            {
                double t = max;
                max = min;
                min = t;
                inverted = true;
            }

            double ratio = (clamp && value < min)
                ? 0.0
                : ((!clamp || !(value > max)) ? (value - min) / (max - min) : 1.0);

            return inverted ? 1.0 - ratio : ratio;
        }

        /// <summary>
        /// Same 0-1 ratio as <see cref="Offset(double,double,double,bool)"/>, but with the bottom and
        /// top clamps controlled independently - useful when a caller wants a floor at 0 but deliberately
        /// wants the ratio free to exceed 1 above <paramref name="max"/> (or vice versa).
        /// </summary>
        public static double Offset(double min, double max, double value, bool clampMin, bool clampMax)
        {
            if (min == max) return 0.0;

            bool inverted = false;
            if (max < min)
            {
                double t = max;
                max = min;
                min = t;
                inverted = true;
            }

            double ratio = (clampMin && value < min)
                ? 0.0
                : ((!clampMax || !(value > max)) ? (value - min) / (max - min) : 1.0);

            return inverted ? 1.0 - ratio : ratio;
        }

        /// <summary>
        /// A two-segment version of <see cref="Map(double,double,double,double,double,bool)"/>: below
        /// <paramref name="inMiddle"/>, remaps <c>[inMin, inMiddle]</c> onto <c>[outMin, outMiddle]</c>;
        /// at or above it, remaps <c>[inMiddle, inMax]</c> onto <c>[outMiddle, outMax]</c>. Both segments
        /// are clamped to their own output range. Used where a response curve needs a different slope on
        /// either side of a chosen midpoint rather than one straight line across the whole input range.
        /// </summary>
        public static double MapPiecewise(double x, double inMin, double inMiddle, double inMax, double outMin, double outMiddle, double outMax)
        {
            if (x >= Math.Min(inMin, inMiddle) && x <= Math.Max(inMin, inMiddle))
                return Map(x, inMin, inMiddle, outMin, outMiddle, true);
            return Map(x, inMiddle, inMax, outMiddle, outMax, true);
        }
    }
}
