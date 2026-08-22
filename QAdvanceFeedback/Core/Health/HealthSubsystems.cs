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

        /// <summary>Reading ShakeIt Motors' own exported wheel properties for the source-mode
        /// availability note (<c>MotorsExportAvailabilityProvider</c>).</summary>
        public const string ShakeItExport = "ShakeItExport";

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
    }
}
