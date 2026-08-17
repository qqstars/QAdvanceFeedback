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
        ShakeIt = 1
    }
}
