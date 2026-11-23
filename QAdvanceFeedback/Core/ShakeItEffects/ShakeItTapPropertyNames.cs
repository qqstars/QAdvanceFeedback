namespace QAdvanceFeedback.Core.ShakeItEffects
{
    /// <summary>
    /// Diagnostic properties carrying ShakeIt's own built-in lock/slip values, as read through a
    /// native QAdvanceFeedback ShakeIt effect (docs\shakeit-native-effects-design.md). Additive and
    /// diagnostics-only: every existing published name is unchanged, and the ShakeIt source mode keeps
    /// its existing source identity (the legacy export property names), so learned data carries over.
    /// </summary>
    public static class ShakeItTapPropertyNames
    {
        public const string PluginPrefix = "QAdvanceFeedback.";

        /// <summary>True while a native QAdvanceFeedback effect is feeding fresh ShakeIt values.</summary>
        public const string TapActiveName = "Diag.ShakeIt.TapActive";

        /// <summary>The name as published by this plugin (SimHub adds the plugin prefix), e.g.
        /// <c>Diag.ShakeIt.WheelLock.FrontLeft</c>.</summary>
        public static string GetPublishedName(bool isLockChannel, string wheelSuffix) =>
            (isLockChannel ? "Diag.ShakeIt.WheelLock." : "Diag.ShakeIt.WheelSlip.") + wheelSuffix;

        /// <summary>The full SimHub property name, e.g. <c>QAdvanceFeedback.Diag.ShakeIt.WheelLock.FrontLeft</c>.</summary>
        public static string GetWheelPropertyName(bool isLockChannel, string wheelSuffix) =>
            PluginPrefix + GetPublishedName(isLockChannel, wheelSuffix);
    }
}
