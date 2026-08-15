using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Builds the shipped default source text for one of <see cref="WheelChannelSettings"/>'s four
    /// per-wheel source fields: the FULL registered SimHub property name of that wheel's Layer 3 Raw
    /// value (e.g. <c>QAdvanceFeedback.WheelLock.Raw.FrontLeft</c>), formatted for
    /// <see cref="ScriptType.Plain"/> (the shipped default type - see that enum's remarks).
    /// <see cref="ScriptSyntax.Format"/> is still the right place to re-wrap this same name if a
    /// later "reset to default under a different script type" action ever needs one.
    /// </summary>
    /// <summary>
    /// KEPT AS <c>Raw.*</c> DELIBERATELY, not merely unfinished: SimHub's own ShakeIt effect can also
    /// export the Wheels lock/Wheels slip effect's per-wheel value as a property (confirmed by
    /// decompiling SimHub.Plugins.dll - see docs\shakeit-export-guide.md for the full, decompilation-
    /// verified walkthrough), and the exported value IS genuinely per-wheel and on this same 0-100
    /// scale - but its FULL registered name is not something this plugin can predict: it is
    /// <c>&lt;ShakeITMotorsV3Plugin-or-ShakeITBSV3Plugin&gt;.Export.&lt;whatever the driver typed as
    /// the property name&gt;.&lt;Placement&gt;</c>, i.e. it depends on which of ShakeIt's two plugin
    /// variants (Motors vs. Bass Shakers) the driver enabled AND on free-text they chose themselves -
    /// there is no fixed default SimHub itself assigns. Hard-coding a guess here would silently do
    /// nothing for anyone who made a different choice (or has not set up the export at all), so this
    /// stays a manual, documented, four-field-per-channel edit (docs\shakeit-export-guide.md, step 7)
    /// rather than an automatic default - <see cref="WheelSourceResolver"/>'s existing fallback
    /// already makes pointing a Source field at a not-yet-exported ShakeIt property safe (falls back
    /// to this same Raw property, silently, never reading the miss as 0) once a driver does so.
    /// </summary>
    public static class DefaultWheelSources
    {
        /// <summary>
        /// SimHub.Plugins.PluginManager.GetName(name, pluginType) is hard-coded as
        /// <c>pluginType.Name + "." + name</c> (decompiled and documented in
        /// QAdvanceFeedback.cs's own remarks, and again in docs\layer123-report.md) and this
        /// plugin's class is deliberately named <c>QAdvanceFeedback</c> so that prefix comes out
        /// right. Duplicated here as a literal (rather than this Settings-owned file reaching into
        /// the plugin class to read it) so this class stays a plain, SimHub-free model - but that
        /// also means: if the plugin class is ever renamed, THIS literal must be updated too. Same
        /// caveat layer123-report.md already flags for the plugin class name itself.
        /// </summary>
        public const string PluginPublishedPrefix = "QAdvanceFeedback.";

        /// <summary>The full registered property name of one wheel's Layer 3 Raw value, e.g.
        /// <c>QAdvanceFeedback.WheelLock.Raw.FrontLeft</c>.</summary>
        public static string RawPropertyName(bool isLockChannel, string wheelSuffix)
        {
            string channelPrefix = isLockChannel ? PublishedPropertyNames.LockPrefix : PublishedPropertyNames.SlipPrefix;
            return PluginPublishedPrefix + channelPrefix + wheelSuffix;
        }
    }
}
