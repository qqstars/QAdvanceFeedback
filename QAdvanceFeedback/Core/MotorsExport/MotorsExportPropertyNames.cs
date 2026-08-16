namespace QAdvanceFeedback.Core.MotorsExport
{
    /// <summary>
    /// The exact shape of the SimHub property names the "ShakeIt Motors" plugin registers when a
    /// "Wheels lock"/"Wheels slip" effect has "Export output value as a property" ticked (see
    /// <c>docs\shakeit-export-guide.md</c> for the full walkthrough).
    /// <para/>
    /// <see cref="PluginTypeName"/> and the full property-name shape built by
    /// <see cref="GetWheelPropertyName"/> below MUST match SimHub's own real, registered names exactly
    /// - these are not this project's own naming choice, they are SimHub's public property-lookup API
    /// surface for a plugin that wants to read a ShakeIt Motors export.
    /// </summary>
    public static class MotorsExportPropertyNames
    {
        /// <summary>SimHub's own "ShakeIt Motors" plugin's class name, as it appears in every property
        /// name that plugin registers. Only this ShakeIt variant is supported here (per the settings
        /// UI's own "Use ShakeIt Motors output" wording) - ShakeIt Bass Shakers is not wired up (a
        /// driver using that variant instead can still point a Manual-mode Source field at their own
        /// exported property by hand - see the guide).</summary>
        public const string PluginTypeName = "ShakeITMotorsV3Plugin";

        /// <summary>The recommended "Property name" text for the Wheel Lock ("Wheels lock") effect -
        /// this plugin's own convention (SimHub has no default of its own), chosen to make the
        /// resulting export self-documenting.</summary>
        public const string LockExportedPropertyName = "WheelLock.IRacing";

        /// <summary>The recommended "Property name" text for the Wheel Slip ("Wheels slip") effect -
        /// see <see cref="LockExportedPropertyName"/>'s remarks.</summary>
        public const string SlipExportedPropertyName = "WheelSlip.IRacing";

        public const string FrontLeft = "FrontLeft";
        public const string FrontRight = "FrontRight";
        public const string RearLeft = "RearLeft";
        public const string RearRight = "RearRight";

        /// <summary>All four wheel suffixes, in <see cref="Corners"/>'s own canonical order.</summary>
        public static readonly string[] WheelSuffixes = { FrontLeft, FrontRight, RearLeft, RearRight };

        /// <summary>
        /// Builds the exact, full SimHub property name for one wheel of one channel, e.g.
        /// <c>ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft</c>.
        /// </summary>
        public static string GetWheelPropertyName(bool isLockChannel, string wheelSuffix)
        {
            string exportedPropertyName = isLockChannel ? LockExportedPropertyName : SlipExportedPropertyName;
            return PluginTypeName + ".Export." + exportedPropertyName + "." + wheelSuffix;
        }
    }
}
