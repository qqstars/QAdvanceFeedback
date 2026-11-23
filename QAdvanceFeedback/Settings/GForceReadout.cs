using QAdvanceFeedback.Core.Localization;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The readout label beside each G-Force max-G editor, shared by the plugin settings page and the
    /// native G-Force ShakeIt effects (docs\robust-auto-gforce-report.md, UI requirement). The editor is
    /// ALWAYS editable in both modes - under Auto it is the default Auto falls back to - so this label
    /// is what communicates Auto's state:
    /// <list type="bullet">
    /// <item>Fixed: "Fixed: 1.5G".</item>
    /// <item>Auto, no evidence yet: "Default: 1.5G. Auto: still using default (no data yet)".</item>
    /// <item>Auto, evidence exists: "Default: 1.5G. Auto detected: 2.3G".</item>
    /// </list>
    /// </summary>
    public static class GForceReadout
    {
        public static string Build(bool autoMode, double fixedValue, bool hasDetected, double detectedValue)
        {
            if (!autoMode) return string.Format(Strings.Get("GForce.Readout.Fixed"), fixedValue);
            return hasDetected
                ? string.Format(Strings.Get("GForce.Readout.Auto.Detected"), fixedValue, detectedValue)
                : string.Format(Strings.Get("GForce.Readout.Auto.NoDataYet"), fixedValue);
        }

        /// <summary>The readout for one axis from the APPLIED settings, keyed by the axis's mode field
        /// (<see cref="SettingField.AccelMaxMode"/>, <see cref="SettingField.DecelMaxMode"/> or
        /// <see cref="SettingField.LatMaxMode"/>).</summary>
        public static string ForAxis(GForceSettings g, SettingField modeField)
        {
            if (g == null) return string.Empty;
            double detected;
            switch (modeField)
            {
                case SettingField.AccelMaxMode:
                    return Build(g.AccelMaxMode == GMaxMode.Auto, g.FixedAccelMaxG, g.TryGetCurrentAccelAutoDetected(out detected), detected);
                case SettingField.DecelMaxMode:
                    return Build(g.DecelMaxMode == GMaxMode.Auto, g.FixedDecelMaxG, g.TryGetCurrentDecelAutoDetected(out detected), detected);
                case SettingField.LatMaxMode:
                    return Build(g.LatMaxMode == GMaxMode.Auto, g.FixedLatMaxG, g.TryGetCurrentLatAutoDetected(out detected), detected);
                default:
                    return string.Empty;
            }
        }
    }
}
