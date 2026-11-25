namespace QAdvanceFeedback.Core.Health
{
    /// <summary>
    /// Stable subsystem identifiers shared by every producer (the reflection wrappers, the
    /// persistence classes, the CSV/background-flush machinery, the plugin composition root) and the
    /// one consumer (the settings UI's health section) of <see cref="HealthRegistry"/>. Centralised as
    /// constants specifically so a producer's own string literal can never silently drift from what
    /// the settings UI looks up a display name/localization key for - the same failure mode the
    /// "QAdvanceFeedback." property-name prefix report already flagged for a different pair of
    /// stringly-typed call sites.
    /// </summary>
    public static class HealthSubsystems
    {
        /// <summary>SimHub's own built-in NCalc/JavaScript formula editor, opened by reflection from
        /// the settings UI's "Edit script..." button (<c>SimHubScriptEditor</c>).</summary>
        public const string ScriptEditor = "ScriptEditor";

        /// <summary>SimHub's own property picker dialog, opened by reflection from the settings UI's
        /// "Pick..." button (<c>PropertyPickerLauncher</c>).</summary>
        public const string PropertyPicker = "PropertyPicker";

        /// <summary>SimHub's own NCalc/JavaScript expression engine, used to evaluate a configured
        /// Source field that is not a plain property name (<c>SimHubExpressionEvaluator</c>).</summary>
        public const string ExpressionEvaluator = "ExpressionEvaluator";

        /// <summary>Reading viper4gh's CalcLngWheelSlip properties for the Viper source's "plugin not detected"
        /// note (<c>ViperAvailabilityProvider</c>). Formerly <c>ShakeItExport</c>, shared with the removed ShakeIt
        /// Motors export provider; renamed with its strings (docs\shakeit-source-design.md Task E) - the
        /// health registry is in-memory only, so no persisted data carries the old id.</summary>
        public const string ViperAvailability = "ViperAvailability";

        /// <summary>The native QAdvanceFeedback ShakeIt effects and the ShakeIt built-in lock/slip they
        /// read (docs\shakeit-native-effects-design.md).</summary>
        public const string ShakeItEffects = "ShakeItEffects";

        /// <summary>The 1 Hz inspection of the ShakeIt profiles for an enabled QAdvanceFeedback effect
        /// (docs\shakeit-source-design.md section 3.1, <c>ShakeItProfileInspection</c>). Cleared on recovery.</summary>
        public const string ShakeItProfile = "ShakeItProfile";

        /// <summary>Clutch Bite output and bite point learning (docs\clutch-bite-design.md).</summary>
        public const string ClutchBite = "ClutchBite";

        /// <summary>Reading SimHub's live <c>FeedbackCapabilities</c>/per-wheel raw telemetry for the
        /// <c>Diag.Capabilities.*</c>/<c>Diag.Telemetry.*</c> diagnostics (<c>SimHubTelemetryAdapter.
        /// CaptureRawTelemetry</c>).</summary>
        public const string CapabilityDetection = "CapabilityDetection";

        /// <summary>Loading/saving <c>QAdvanceFeedback.config.json</c> (<c>ConfigStore</c>).</summary>
        public const string ConfigPersistence = "ConfigPersistence";

        /// <summary>Loading/saving <c>QAdvanceFeedback.Parameters.json</c> - the learned Lock/Slip and
        /// G-force calibration (<c>RuntimeStore</c>, excluding the background timer callback itself -
        /// see <see cref="BackgroundFlush"/>).</summary>
        public const string RuntimePersistence = "RuntimePersistence";

        /// <summary>Writing the per-session diagnostics CSV file (<c>CsvExportWriter</c>).</summary>
        public const string CsvExport = "CsvExport";

        /// <summary>The <c>RuntimeStore</c> background flush <see cref="System.Threading.Timer"/>
        /// callback itself - the single most dangerous class here, since an unhandled exception on a
        /// raw ThreadPool thread can terminate the whole SimHub process in .NET Framework (see
        /// docs\pipeline-exception-safety-report.md).</summary>
        public const string BackgroundFlush = "BackgroundFlush";

        /// <summary>The per-frame telemetry pipeline (<c>QAdvanceFeedback.DataUpdate</c>'s own
        /// top-level guard) - covers any fault anywhere in one frame's processing, not one specific
        /// class, matching the brief's own "the telemetry adapter" framing for this catch-all.</summary>
        public const string TelemetryAdapter = "TelemetryAdapter";

        /// <summary>An individual published SimHub property (<c>AttachDelegate</c> value-provider
        /// lambda) throwing instead of returning a value (<c>PropertyPublisher</c>).</summary>
        public const string PropertyPublish = "PropertyPublish";

        /// <summary>The plugin's own <c>Init</c> failing (the late/manual-enable path SimHub does not
        /// guard - see docs\pipeline-exception-safety-report.md).</summary>
        public const string Init = "Init";

        /// <summary>The plugin's own <c>End</c> (shutdown) failing.</summary>
        public const string Shutdown = "Shutdown";

        /// <summary>The settings screen (<c>GetWPFSettingsControl</c>/<c>SettingsControl</c>'s own
        /// constructor) failing to build at all.</summary>
        public const string SettingsUi = "SettingsUi";

        /// <summary>Reading a game's native per-wheel slip angle from SimHub raw data
        /// (<c>SlipAngleRawSourceReader</c>).</summary>
        public const string SlipAngleSource = "SlipAngleSource";

        /// <summary>The per-wheel slip-angle source file (<c>QAdvanceFeedback.SlipAngleSource.json</c> in
        /// PluginsData\Common, <c>SlipAngleSourceStore</c>): not valid, or not readable/writable.</summary>
        public const string SlipAngleSourceFile = "SlipAngleSourceFile";

        /// <summary>The per-frame Slip Angle pipeline (<c>QAdvanceFeedback.UpdateSlipAngle</c> /
        /// <c>SlipAngleFrameProcessor</c>); on a fault its outputs read 0 / null and Lock/Slip/G-Force carry on.</summary>
        public const string SlipAngle = "SlipAngle";
    }
}
