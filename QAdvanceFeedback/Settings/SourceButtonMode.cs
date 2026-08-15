namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// Which single button (if any) a wheel-source field's picker/editor slot shows. Every one of the
    /// eight source fields (Wheel Lock/Wheel Slip x FrontLeft/FrontRight/RearLeft/RearRight) shows
    /// ONE button, resolved by <see cref="SourceButtonModeSelector.Resolve"/> - ported from the sibling
    /// ReliableWheelLockSlip project's identically-named type (its own Task 33, Change 1), which
    /// already solved this cascade and is hard-won.
    /// </summary>
    public enum SourceButtonMode
    {
        /// <summary>Neither the script editor nor the property picker could be resolved - no button at all.</summary>
        None,

        /// <summary>SimHub's own script editor - offered first when it, AND the expression
        /// evaluator, are both available (see <see cref="SourceButtonModeSelector.Resolve"/>).</summary>
        Editor,

        /// <summary>SimHub's own property picker - the fallback when the editor is not offered.</summary>
        Picker
    }

    /// <summary>
    /// The pure cascade decision behind <see cref="SourceButtonMode"/> - deliberately a tiny,
    /// SimHub-free function so all combinations are unit-tested directly, rather than only reachable
    /// by driving the real reflection-based SimHubScriptEditor/PropertyPickerLauncher/
    /// SimHubExpressionEvaluator classes (none of which can be unit-tested - see their own remarks).
    /// <para/>
    /// FIXED BUG (read before changing this again): this used to be resolved ONCE from availability
    /// alone, the same for all eight source rows - so a row whose type was
    /// <see cref="ScriptType.Plain"/> still showed "Edit script...", which is wrong (a plain property
    /// reference has no script to edit). The button now also depends on the ROW'S OWN
    /// <see cref="ScriptType"/>, not just the three availability flags.
    /// </summary>
    public static class SourceButtonModeSelector
    {
        /// <summary>
        /// Chooses the single button a source field shows, given its OWN <paramref name="scriptType"/>:
        /// <list type="number">
        /// <item><see cref="ScriptType.Plain"/> - the property picker, if
        /// <paramref name="pickerAvailable"/>; otherwise no button. NEVER the script editor - a plain
        /// property reference has nothing to script-edit, regardless of whether the editor itself
        /// resolved fine.</item>
        /// <item><see cref="ScriptType.JavaScript"/>/<see cref="ScriptType.NCalc"/> - the script
        /// editor, if <paramref name="editorAvailable"/> AND <paramref name="evaluatorAvailable"/> (an
        /// editor is pointless, and misleading, to offer when the evaluator it would hand an
        /// expression to can never actually run it); otherwise the property picker, if
        /// <paramref name="pickerAvailable"/>; otherwise no button.</item>
        /// </list>
        /// </summary>
        public static SourceButtonMode Resolve(
            ScriptType scriptType, bool editorAvailable, bool pickerAvailable, bool evaluatorAvailable)
        {
            if (scriptType == ScriptType.Plain)
                return pickerAvailable ? SourceButtonMode.Picker : SourceButtonMode.None;

            if (editorAvailable && evaluatorAvailable) return SourceButtonMode.Editor;
            if (pickerAvailable) return SourceButtonMode.Picker;
            return SourceButtonMode.None;
        }
    }
}
