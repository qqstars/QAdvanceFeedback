using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Faithful, minimal port of the subset of WoteverCommon.Extensions.MathExtensions (decompiled;
    /// see docs\reference\Wotever.MathExtensions.decompiled.cs) that
    /// WheelSlipEffect.GetRpmSpeedSlipLegacy (docs\reference\SimHub.WheelSlipEffect.decompiled.cs)
    /// actually calls: the 4-arg <see cref="Offset(double,double,double,bool)"/>, <see cref="Map"/>
    /// and <see cref="Clamp"/>. Ported verbatim rather than approximated, per the brief - the
    /// arithmetic here must match the decompiled source exactly, including its rounding/branch
    /// order, not just its intent.
    /// </summary>
    public static class MathHelpers
    {
        /// <summary>
        /// double Clamp(value, lowRange, highRange) -&gt; Math.Max(Math.Min(value, high), low),
        /// exactly as WoteverCommon.Extensions.MathExtensions.Clamp(double, double, double).
        /// </summary>
        public static double Clamp(double value, double lowRange, double highRange)
            => Math.Max(Math.Min(value, highRange), lowRange);

        /// <summary>
        /// MathExtensions.Map(x, in_min, in_max, out_min, out_max, constrain) ported verbatim -
        /// linear remap, optionally constrained (clamped) to the output range afterwards.
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
        /// The 4-arg Offset(min, max, value, clamp = true) overload - the ONLY Offset overload
        /// GetRpmSpeedSlipLegacy calls (its three call sites - the lateral floor, the brake ratio,
        /// and the throttle ratio - all pass a single bool). Returns 0 at/below <paramref name="min"/>
        /// and 1 at/above <paramref name="max"/> when <paramref name="clamp"/> is true (both ends
        /// clamped by the SAME bool); otherwise the raw, unclamped linear ratio.
        /// <para/>
        /// NOT the same semantics as the 5-arg overload below - see its remarks. Using the wrong one
        /// at a call site silently changes clamping behaviour at one end only.
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
        /// The 5-arg Offset(min, max, value, clampMin, clampMax) overload - independent bottom/top
        /// clamping. NOT used by the legacy lock/slip algorithm (all three of its Offset calls use
        /// the 4-arg overload above) - kept here for fidelity with the decompiled source and as a
        /// clean seam for later layers that port other WheelSlipEffect branches
        /// (GetDirectSlip/GetLockFromWheelSpeed/GetRpsLock all call THIS overload, typically with
        /// clampMax:false, which lets the ratio exceed 1.0 above <paramref name="max"/>).
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
    }
}
