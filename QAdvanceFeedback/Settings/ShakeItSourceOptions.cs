using System.Collections.Generic;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Which sources the ShakeIt-side WheelLock/WheelSlip panel offers (it has no text boxes, so it
    /// can only offer sources that work as-is): Plugin Internal always; ShakeIt when its source is set up
    /// (docs\shakeit-source-design.md §4); Viper only when its plugin is available; Custom only when the
    /// channel has a configured Custom source. The current selection is always listed, so the dropdown
    /// never shows blank and a driver's saved source is never rewritten.
    /// </summary>
    public static class ShakeItSourceOptions
    {
        public static IReadOnlyList<SourceMode> For(bool viperAvailable, bool customConfigured, SourceMode current) =>
            For(viperAvailable, customConfigured, current, shakeItSetUp: true);

        /// <param name="shakeItSetUp">The channel's ShakeIt source state is not NotSetUp.</param>
        public static IReadOnlyList<SourceMode> For(bool viperAvailable, bool customConfigured, SourceMode current, bool shakeItSetUp)
        {
            var list = new List<SourceMode> { SourceMode.Manual };
            if (shakeItSetUp || current == SourceMode.ShakeIt) list.Add(SourceMode.ShakeIt);
            if (viperAvailable || current == SourceMode.Viper) list.Add(SourceMode.Viper);
            if (customConfigured || current == SourceMode.Custom) list.Add(SourceMode.Custom);
            return list;
        }
    }
}
