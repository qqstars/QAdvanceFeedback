namespace QAdvanceFeedback.Core.ShakeIt
{
    /// <summary>
    /// The exact, decompilation-confirmed shape of the SimHub property names SimHub's own ShakeIt
    /// Motors plugin registers when a "Wheels lock"/"Wheels slip" effect has "Export output value as a
    /// property" ticked (see <c>docs\shakeit-export-guide.md</c> for the full walkthrough).
    /// <para/>
    /// CONFIRMED BY DECOMPILING <c>SimHub.Plugins.dll</c>/<c>GameReaderCommon.dll</c> (not guessed):
    /// <list type="bullet">
    /// <item><see cref="PluginTypeName"/> = <c>"ShakeITMotorsV3Plugin"</c> - the concrete class name of
    /// the "ShakeIt Motors" plugin (<c>[PluginName("ShakeIt Motors")]</c>); SimHub's own
    /// <c>PluginManager.GetName(name, pluginType)</c> is hard-coded as
    /// <c>pluginType.Name + "." + name</c>, so this is the first segment of every property this
    /// specific plugin registers.</item>
    /// <item>The full name is built as <c>pluginType.Name + ".Export." + Output.PropertyName + "." +
    /// Placement</c> (<c>ShakeITV3PluginBase.ExportProperties</c>) - <c>Output.PropertyName</c> is
    /// whatever free text the driver typed into ShakeIt's own "Property name" field. This plugin
    /// recommends (and defaults its own Source fields to) <see cref="LockExportedPropertyName"/>/
    /// <see cref="SlipExportedPropertyName"/> as that text, so the resulting full names are
    /// predictable - see <see cref="GetWheelPropertyName"/>.</item>
    /// <item><c>Placement</c> is <c>GameReaderCommon.Enums.FFBPlacement</c>'s own <c>ToString()</c> -
    /// confirmed by decompiling that enum directly: its members are declared exactly
    /// <c>FrontLeft</c>/<c>FrontRight</c>/<c>RearLeft</c>/<c>RearRight</c> (among others not used by
    /// the wheel lock/slip effects), with no <c>[EnumMember]</c>/custom-name attribute overriding the
    /// default enum <c>ToString()</c> - so the literal wheel-suffix strings below are exactly what
    /// SimHub publishes, not an assumption.</item>
    /// </list>
    /// </summary>
    public static class ShakeItPropertyNames
    {
        /// <summary>The "ShakeIt Motors" plugin's own class name - confirmed by decompilation (see
        /// this class's own remarks). Only this ONE ShakeIt variant is supported by the toggle (per
        /// the owner's explicit "Use ShakeIt Motors output" wording) - ShakeIt Bass Shakers is not
        /// wired up here (a driver using that variant instead can still point the Manual-mode Source
        /// fields at their own exported property by hand - see the guide).</summary>
        public const string PluginTypeName = "ShakeITMotorsV3Plugin";

        /// <summary>The recommended "Property name" text for the Wheel Lock ("Wheels lock") effect -
        /// this plugin's own convention (SimHub has no default of its own), chosen to make the
        /// resulting export self-documenting (it names both which channel and which algorithm branch
        /// - "IRacing" - the driver should have enabled).</summary>
        public const string LockExportedPropertyName = "WheelLock.IRacing";

        /// <summary>The recommended "Property name" text for the Wheel Slip ("Wheels slip") effect -
        /// see <see cref="LockExportedPropertyName"/>'s remarks.</summary>
        public const string SlipExportedPropertyName = "WheelSlip.IRacing";

        public const string FrontLeft = "FrontLeft";
        public const string FrontRight = "FrontRight";
        public const string RearLeft = "RearLeft";
        public const string RearRight = "RearRight";

        /// <summary>All four wheel suffixes, in <see cref="Corners"/>' own canonical order
        /// (FrontLeft, FrontRight, RearLeft, RearRight).</summary>
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
