using System;
using System.Globalization;

namespace QAdvanceFeedback.Core.Viper
{
    /// <summary>
    /// Pure, SimHub-free resolution of whether viper4gh's CalcLngWheelSlip plugin
    /// (<see cref="ViperPropertyNames"/>) is currently publishing all four wheels. The twin of
    /// <see cref="MotorsExport.MotorsExportAvailabilityResolver"/>, and deliberately a separate class:
    /// the two probe different plugins and their notions of "available" are free to diverge.
    /// <para/>
    /// WHY THIS CANNOT SIMPLY READ THE VALUE AND CHECK IT IS NONZERO. Viper's own output is legitimately
    /// 0 for most of a lap - a wheel that is neither locking nor spinning reads exactly 0 - so "is it
    /// nonzero" would report the plugin as missing through every straight. Availability here therefore
    /// means the property EXISTS and converts to a finite number, nothing more, exactly as the ShakeIt
    /// resolver defines it.
    /// <para/>
    /// AND WHY THAT IS STILL WORTH PROBING. SimHub's NCalc resolves an unknown property to 0 without
    /// erroring, so a driver pointing this mode at a plugin that is not installed gets a clean,
    /// permanent 0 and no diagnostic at all - the exact failure that cost the owner a whole session.
    /// A direct <c>GetPropertyValue</c> probe distinguishes "not installed" (null) from "installed and
    /// currently reading 0" (a real number), which the expression itself cannot.
    /// <para/>
    /// ALL FOUR WHEELS MUST RESOLVE. A partial match is treated as unavailable rather than as a
    /// partially-working mode - same rule as the ShakeIt resolver.
    /// </summary>
    public static class ViperAvailabilityResolver
    {
        /// <summary>Viper's own wheel suffixes, in this project's canonical corner order.</summary>
        private static readonly string[] ViperWheels = { "FL", "FR", "RL", "RR" };

        /// <summary>
        /// Whether all four of Viper's computed wheel properties currently resolve to a finite number.
        /// <para/>
        /// NOT PER-CHANNEL, unlike the ShakeIt resolver: Lock and Slip read the SAME four properties and
        /// merely differ in sign, so there is exactly one availability answer and both channels share
        /// it. Taking a channel parameter here would imply a distinction that does not exist.
        /// </summary>
        public static bool IsAvailable(Func<string, object> propertyReader)
        {
            if (propertyReader == null) return false;

            foreach (string wheel in ViperWheels)
            {
                object value;
                try { value = propertyReader(ViperPropertyNames.GetRawPropertyName(wheel)); }
                catch { value = null; }

                if (!IsUsableNumber(value)) return false;
            }

            return true;
        }

        private static bool IsUsableNumber(object value)
        {
            if (value == null) return false;

            try
            {
                double converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return ClampMath.IsFinite(converted);
            }
            catch (Exception e) when (e is InvalidCastException || e is FormatException || e is OverflowException)
            {
                return false;
            }
        }
    }
}
