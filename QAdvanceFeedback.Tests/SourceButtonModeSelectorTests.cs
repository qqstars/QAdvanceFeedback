using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>All (scriptType x editor x picker x evaluator) combinations for the source-field
    /// button cascade - see <see cref="SourceButtonModeSelector"/>'s own remarks on the bug this
    /// fixes (Plain used to be able to show the script editor).</summary>
    public class SourceButtonModeSelectorTests
    {
        [Theory]
        [InlineData(true, true, true, SourceButtonMode.Editor)]
        [InlineData(true, true, false, SourceButtonMode.Picker)]
        [InlineData(true, false, true, SourceButtonMode.Editor)]
        [InlineData(true, false, false, SourceButtonMode.None)]
        [InlineData(false, true, true, SourceButtonMode.Picker)]
        [InlineData(false, true, false, SourceButtonMode.Picker)]
        [InlineData(false, false, true, SourceButtonMode.None)]
        [InlineData(false, false, false, SourceButtonMode.None)]
        public void JavaScript_type_resolves_every_availability_combination_correctly(
            bool editor, bool picker, bool evaluator, SourceButtonMode expected)
        {
            Assert.Equal(expected, SourceButtonModeSelector.Resolve(ScriptType.JavaScript, editor, picker, evaluator));
        }

        [Theory]
        [InlineData(true, true, true, SourceButtonMode.Editor)]
        [InlineData(true, true, false, SourceButtonMode.Picker)]
        [InlineData(true, false, true, SourceButtonMode.Editor)]
        [InlineData(true, false, false, SourceButtonMode.None)]
        [InlineData(false, true, true, SourceButtonMode.Picker)]
        [InlineData(false, true, false, SourceButtonMode.Picker)]
        [InlineData(false, false, true, SourceButtonMode.None)]
        [InlineData(false, false, false, SourceButtonMode.None)]
        public void NCalc_type_resolves_every_availability_combination_correctly(
            bool editor, bool picker, bool evaluator, SourceButtonMode expected)
        {
            Assert.Equal(expected, SourceButtonModeSelector.Resolve(ScriptType.NCalc, editor, picker, evaluator));
        }

        // ------------------------------------------------------------------------------------
        // THE BUG FIX: Plain must NEVER yield the script editor, regardless of availability -
        // covers all eight (editor x picker x evaluator) combinations for Plain specifically, since
        // this is exactly the case the mutation evidence targets (button mode ignoring script type).
        // ------------------------------------------------------------------------------------
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, true)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, false, false)]
        public void Plain_type_never_yields_the_script_editor(bool editor, bool picker, bool evaluator)
        {
            SourceButtonMode mode = SourceButtonModeSelector.Resolve(ScriptType.Plain, editor, picker, evaluator);
            Assert.NotEqual(SourceButtonMode.Editor, mode);
        }

        [Fact]
        public void Plain_type_shows_the_picker_when_available()
        {
            Assert.Equal(SourceButtonMode.Picker,
                SourceButtonModeSelector.Resolve(ScriptType.Plain, editorAvailable: true, pickerAvailable: true, evaluatorAvailable: true));
        }

        [Fact]
        public void Plain_type_shows_no_button_when_the_picker_is_unavailable()
        {
            Assert.Equal(SourceButtonMode.None,
                SourceButtonModeSelector.Resolve(ScriptType.Plain, editorAvailable: true, pickerAvailable: false, evaluatorAvailable: true));
        }

        [Theory]
        [InlineData((ScriptType)99)]
        public void Unknown_script_type_values_fall_back_to_the_scripted_cascade_not_a_crash(ScriptType weird)
        {
            // Defensive: an out-of-range enum value (e.g. from a hand-edited config file) must not
            // throw - it is simply treated as "not Plain", same as JavaScript/NCalc.
            SourceButtonMode mode = SourceButtonModeSelector.Resolve(weird, true, true, true);
            Assert.Equal(SourceButtonMode.Editor, mode);
        }
    }
}
