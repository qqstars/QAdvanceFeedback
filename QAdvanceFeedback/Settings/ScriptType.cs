namespace QAdvanceFeedback.Settings
{
    /// <summary>
    /// The three ways a Layer 4 wheel source field can be interpreted, per the brief: "a configurable
    /// source with a script type (JavaScript / NCalc / plain property) ... The syntax must match the
    /// type." <see cref="Plain"/> is the enum's zero value AND the shipped default (unlike the sibling
    /// ReliableWheelLockSlip project, whose only two script types were both expression dialects and
    /// therefore defaulted to JavaScript) - a plain SimHub property reference is the simplest, most
    /// robust default: it needs no expression engine at all to resolve, and it is exactly what "default
    /// to the Layer 3 property" means literally (a bare property name, not a wrapped expression that
    /// merely reads one).
    /// </summary>
    public enum ScriptType
    {
        Plain = 0,
        JavaScript = 1,
        NCalc = 2
    }

    /// <summary>
    /// The one place that knows how to WRITE a source field's text so its syntax matches its
    /// <see cref="ScriptType"/> - centralised so the three dialects can never drift apart between
    /// wherever a default is shipped and wherever a "reset to default" action (owned by the later
    /// settings-UI task) would regenerate one.
    /// </summary>
    public static class ScriptSyntax
    {
        /// <summary>
        /// JavaScript: <c>$prop("Name")</c> - SimHub's own JavaScript property-read syntax.
        /// NCalc: <c>[Name]</c> - SimHub's own NCalc property-read syntax.
        /// Plain: the bare name, unwrapped - read directly as a SimHub property, no expression
        /// evaluation involved at all.
        /// </summary>
        public static string Format(ScriptType scriptType, string propertyName)
        {
            switch (scriptType)
            {
                case ScriptType.JavaScript: return "$prop(\"" + propertyName + "\")";
                case ScriptType.NCalc: return "[" + propertyName + "]";
                default: return propertyName;
            }
        }

        /// <summary>
        /// The settings UI's script-type toggle button cycles through all three states in this
        /// order: Plain -&gt; JavaScript -&gt; NCalc -&gt; Plain. Extracted here (rather than left
        /// inline in the WPF-only SettingsControl.xaml.cs) specifically so it is unit-testable.
        /// <para/>
        /// FIXES A REAL BUG (read before changing this again): this used to only flip between
        /// JavaScript and NCalc (<c>current == NCalc ? JavaScript : NCalc</c>) - since
        /// <see cref="ScriptType.Plain"/> is the shipped default every field starts on, and was never
        /// one of the two states the click cycled between, Plain was UNREACHABLE once a driver left
        /// it - there was no way back to a plain property reference through the UI at all. The cycle
        /// now includes Plain as a genuine third state.
        /// </summary>
        public static ScriptType Cycle(ScriptType current)
        {
            switch (current)
            {
                case ScriptType.Plain: return ScriptType.JavaScript;
                case ScriptType.JavaScript: return ScriptType.NCalc;
                default: return ScriptType.Plain; // NCalc, or any unrecognised value, wraps to Plain
            }
        }
    }
}
