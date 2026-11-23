using System.Globalization;
using QAdvanceFeedback.Core.Localization;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// The read-only Start line shown under Min/Best/Max (docs\slip-angle-design.md 9.4):
    /// "Start: 2° (50% of Min)", or "Start: — (Min below 2°)" when the derived Start is absent. Shared by
    /// the Slip Angle tab and the ShakeIt panel. Empty when Min is not a number (nothing to derive from).
    /// </summary>
    public static class SlipAngleStartText
    {
        public static string For(double? min)
        {
            if (!min.HasValue || !ClampMath.IsFinite(min.Value)) return string.Empty;
            double? start = new SlipAngleWindow(min.Value, 0.0, 0.0).Start;
            return start.HasValue
                ? string.Format(CultureInfo.InvariantCulture, Strings.Get("SlipAngle.Start.Line"), start.Value.ToString("0", CultureInfo.InvariantCulture))
                : Strings.Get("SlipAngle.Start.Absent");
        }
    }
}
