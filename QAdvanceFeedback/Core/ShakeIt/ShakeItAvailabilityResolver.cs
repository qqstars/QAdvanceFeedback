using System;
using System.Globalization;

namespace QAdvanceFeedback.Core.ShakeIt
{
    /// <summary>
    /// Pure, SimHub-free resolution of whether SimHub's own ShakeIt Motors export
    /// (<see cref="ShakeItPropertyNames"/>, see <c>docs\shakeit-export-guide.md</c>) is currently
    /// FULLY available for one channel (Wheel Lock or Wheel Slip). Takes the property lookup as an
    /// injected delegate so this is unit-testable without a live SimHub/PluginManager - the
    /// plugin-facing <c>ShakeItSourceProvider</c> class (outside Core, since it talks to a real
    /// PluginManager) adapts <c>PluginManager.GetPropertyValue</c> to this shape, following the same
    /// resolve-once/degrade-silently discipline as <c>SimHubScriptEditor</c>/
    /// <c>PropertyPickerLauncher</c>/<c>SimHubExpressionEvaluator</c>, even though the underlying
    /// primitive here is SimHub's own PUBLIC API (no private-type reflection is needed at all - see
    /// that class's own remarks).
    /// <para/>
    /// "FULLY available" means ALL FOUR wheels' properties exist and return a non-null,
    /// finite-convertible value - a partial match (e.g. three wheels exported, one not) is treated as
    /// UNAVAILABLE, never a partially-working mode, per the brief's "never show a mode that cannot
    /// work". A missing property is never read as 0 - it simply fails this resolution, and the
    /// caller falls back to Manual/Raw (see <c>WheelSourceResolver</c>'s own fallback).
    /// </summary>
    public static class ShakeItAvailabilityResolver
    {
        public static bool IsAvailable(Func<string, object> propertyReader, bool isLockChannel)
        {
            if (propertyReader == null) return false;

            foreach (string wheel in ShakeItPropertyNames.WheelSuffixes)
            {
                string name = ShakeItPropertyNames.GetWheelPropertyName(isLockChannel, wheel);
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
