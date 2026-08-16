using System;
using System.Globalization;

namespace QAdvanceFeedback.Core.MotorsExport
{
    /// <summary>
    /// Pure, SimHub-free resolution of whether SimHub's own ShakeIt Motors export
    /// (<see cref="MotorsExportPropertyNames"/>, see <c>docs\shakeit-export-guide.md</c>) is currently
    /// FULLY available for one channel (Wheel Lock or Wheel Slip). Takes the property lookup as an
    /// injected delegate so this is unit-testable without a live SimHub <c>PluginManager</c> - the
    /// plugin-facing provider adapts <c>PluginManager.GetPropertyValue</c> to this shape.
    /// <para/>
    /// "FULLY available" means ALL FOUR wheels' properties exist and return a non-null,
    /// finite-convertible value - a partial match (e.g. three wheels exported, one not) is treated as
    /// UNAVAILABLE, never a partially-working mode. A missing property is never read as 0 - it simply
    /// fails this resolution, and the caller falls back to Manual/Raw.
    /// </summary>
    public static class MotorsExportAvailabilityResolver
    {
        public static bool IsAvailable(Func<string, object> propertyReader, bool isLockChannel)
        {
            if (propertyReader == null) return false;

            foreach (string wheel in MotorsExportPropertyNames.WheelSuffixes)
            {
                string name = MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, wheel);
                object value;
                try { value = propertyReader(name); }
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
