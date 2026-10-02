namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Per-channel (Wheel Lock / Wheel Slip) toggle between the plugin's own configurable per-wheel
    /// sources and SimHub's own ShakeIt Motors export - see
    /// <c>Core.MotorsExport.MotorsExportPropertyNames</c> and <c>docs\shakeit-export-guide.md</c>.
    /// <para/>
    /// The enum member name <see cref="ShakeIt"/> is kept exactly as shipped (not renamed alongside
    /// the internal <c>Core.MotorsExport</c> types it used to share a name with) because it is
    /// serialised by name into persisted settings JSON - renaming it would silently reset every
    /// existing installation's source mode back to its default on upgrade.
    /// </summary>
    public enum SourceMode
    {
        /// <summary>The current behaviour: four independently-configurable source fields, each with
        /// its own <see cref="ScriptType"/> (plain property / JavaScript / NCalc), editor/picker
        /// buttons.</summary>
        Manual = 0,

        /// <summary>Reads the four wheels straight from SimHub's own ShakeIt Motors "Wheels lock"/
        /// "Wheels slip" effect export (with "Use legacy IRacing algorythm" enabled, per the guide),
        /// via plain property references to the fixed names in
        /// <c>Core.MotorsExport.MotorsExportPropertyNames</c> - no per-wheel script configuration is
        /// shown while this mode is selected.</summary>
        ShakeIt = 1,

        /// <summary>
        /// Reads the four wheels from viper4gh's CalcLngWheelSlip plugin via fixed NCalc expressions -
        /// see <c>Core.Viper.ViperPropertyNames</c>. The ORIGINAL viper4gh plugin only; the community
        /// fork registers its properties under a different plugin name and is out of scope.
        /// <para/>
        /// Unlike <see cref="ShakeIt"/>, this mode's four fields are NCalc rather than
        /// <see cref="ScriptType.Plain"/>: Viper publishes a signed value where positive is lock and
        /// negative is slip, so each channel needs an expression to select and rescale its own half.
        /// A driver who leaves the script type on Plain would have SimHub look up a property literally
        /// named "min(1.0, max(0, ...", find nothing, and silently fall back to Raw - which is why this
        /// mode sets the type itself rather than only filling in the text.
        /// <para/>
        /// Appended as 2 and never reordered: <see cref="SourceMode"/> is serialised BY NAME, but the
        /// numeric values are what a hand-edited or third-party config may carry.
        /// </summary>
        Viper = 2,

        /// <summary>
        /// A source configuration the driver wrote themselves, rather than one of the presets above.
        /// <para/>
        /// REACHED BY EDITING, NOT BY PICKING (owner, 2026-09-28): changing any source text while Raw,
        /// ShakeIt or Viper is selected moves the channel here immediately - before Apply - so the
        /// preset's own configuration is never altered by an edit. Each mode keeps its own archive (see
        /// <see cref="WheelSourceSet"/>), so switching away and back returns exactly what was there.
        /// <para/>
        /// EXCLUDED FROM "RESTORE ALL DEFAULTS", which resets only the three known presets - a driver's
        /// hand-written configuration is not something this plugin has a default for.
        /// </summary>
        Custom = 3
    }
}
