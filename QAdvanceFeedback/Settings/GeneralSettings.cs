namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The two plugin-wide toggles the brief's General tab owns, both default OFF:
    /// <list type="bullet">
    /// <item><see cref="EnableDiagnostics"/> - when off (default), the plugin publishes ONLY the
    /// product properties (Raw/Normalized/Projected x Wheel Lock/Wheel Slip, and the 8 G-force channels)
    /// and writes no log output beyond Init/End/fault lines. When on, every internal/diagnostic
    /// property is ALSO published and per-frame diagnostic logging is enabled. SimHub registers
    /// properties once at <c>Init</c>, so toggling this only takes effect after a SimHub restart -
    /// the settings UI must say so next to the checkbox (mirroring the sibling project's own
    /// <c>PublishDiagnosticProperties</c> note).</item>
    /// <item><see cref="ExportCsv"/> - when on, every property (product AND diagnostic) is written to
    /// a session CSV file under SimHub's <c>PluginsData\Common</c> folder for troubleshooting. When
    /// off (default), nothing is written. Unlike <see cref="EnableDiagnostics"/>, this takes effect
    /// immediately - no restart needed, since it only gates whether <c>CsvExportWriter</c> is asked to
    /// write, not what SimHub registers at Init.</item>
    /// </list>
    /// Public settable properties with a parameterless constructor - Newtonsoft convention, matching
    /// every other settings POCO in this plugin family.
    /// </summary>
    public sealed class GeneralSettings
    {
        public bool EnableDiagnostics { get; set; } = false;

        public bool ExportCsv { get; set; } = false;
    }
}
