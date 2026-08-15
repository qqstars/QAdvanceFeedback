namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Per-channel (Wheel Lock / Wheel Slip) toggle between the plugin's own configurable per-wheel
    /// sources and SimHub's own ShakeIt Motors export - see <c>Core.ShakeIt.ShakeItPropertyNames</c>
    /// and <c>docs\shakeit-export-guide.md</c>.
    /// </summary>
    public enum SourceMode
    {
        /// <summary>The current behaviour: four independently-configurable source fields, each with
        /// its own <see cref="ScriptType"/> (plain property / JavaScript / NCalc), editor/picker
        /// buttons. Default - unaffected by anything ShakeIt-related.</summary>
        Manual = 0,

        /// <summary>Reads the four wheels straight from SimHub's own ShakeIt Motors "Wheels lock"/
        /// "Wheels slip" effect export (with "Use legacy IRacing algorythm" enabled, per the guide),
        /// via plain property references to the fixed names in
        /// <c>Core.ShakeIt.ShakeItPropertyNames</c> - no per-wheel script configuration is shown while
        /// this mode is selected.</summary>
        ShakeIt = 1
    }
}
