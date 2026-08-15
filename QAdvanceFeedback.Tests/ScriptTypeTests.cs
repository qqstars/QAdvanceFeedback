using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class ScriptTypeTests
    {
        [Fact]
        public void Plain_is_the_enums_zero_value()
        {
            Assert.Equal(ScriptType.Plain, default(ScriptType));
        }

        [Fact]
        public void Plain_syntax_is_the_bare_property_name()
        {
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft",
                ScriptSyntax.Format(ScriptType.Plain, "QAdvanceFeedback.WheelLock.Raw.FrontLeft"));
        }

        [Fact]
        public void JavaScript_syntax_wraps_in_prop_call()
        {
            Assert.Equal("$prop(\"QAdvanceFeedback.WheelLock.Raw.FrontLeft\")",
                ScriptSyntax.Format(ScriptType.JavaScript, "QAdvanceFeedback.WheelLock.Raw.FrontLeft"));
        }

        [Fact]
        public void NCalc_syntax_wraps_in_square_brackets()
        {
            Assert.Equal("[QAdvanceFeedback.WheelLock.Raw.FrontLeft]",
                ScriptSyntax.Format(ScriptType.NCalc, "QAdvanceFeedback.WheelLock.Raw.FrontLeft"));
        }

        // ------------------------------------------------------------------------------------
        // BUG 1's fix: the settings UI's script-type toggle must cycle through all THREE states,
        // Plain included - see ScriptSyntax.Cycle's own remarks on why Plain was unreachable before.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Cycle_goes_Plain_to_JavaScript_to_NCalc_and_back_to_Plain()
        {
            Assert.Equal(ScriptType.JavaScript, ScriptSyntax.Cycle(ScriptType.Plain));
            Assert.Equal(ScriptType.NCalc, ScriptSyntax.Cycle(ScriptType.JavaScript));
            Assert.Equal(ScriptType.Plain, ScriptSyntax.Cycle(ScriptType.NCalc));
        }

        [Fact]
        public void Three_consecutive_cycles_from_any_starting_state_return_to_the_start()
        {
            foreach (ScriptType start in new[] { ScriptType.Plain, ScriptType.JavaScript, ScriptType.NCalc })
            {
                ScriptType current = start;
                current = ScriptSyntax.Cycle(current);
                current = ScriptSyntax.Cycle(current);
                current = ScriptSyntax.Cycle(current);
                Assert.Equal(start, current);
            }
        }

        [Fact]
        public void Plain_is_reachable_from_both_other_states_not_just_the_initial_default()
        {
            // The exact regression this fixes: previously, once a field left Plain, clicking could
            // only ever flip between JavaScript and NCalc - Plain was gone for good.
            Assert.Equal(ScriptType.Plain, ScriptSyntax.Cycle(ScriptType.NCalc));
        }
    }

    public class DefaultWheelSourcesTests
    {
        [Theory]
        [InlineData(true, "FrontLeft", "QAdvanceFeedback.WheelLock.Raw.FrontLeft")]
        [InlineData(true, "RearRight", "QAdvanceFeedback.WheelLock.Raw.RearRight")]
        [InlineData(false, "FrontRight", "QAdvanceFeedback.WheelSlip.Raw.FrontRight")]
        [InlineData(false, "RearLeft", "QAdvanceFeedback.WheelSlip.Raw.RearLeft")]
        public void RawPropertyName_matches_the_layer3_published_name_exactly(
            bool isLockChannel, string wheelSuffix, string expected)
        {
            Assert.Equal(expected, DefaultWheelSources.RawPropertyName(isLockChannel, wheelSuffix));
        }
    }
}
