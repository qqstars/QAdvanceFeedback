using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="SourceIdentity"/> - the third key dimension
    /// <see cref="KeyedGripLearner"/> now learns by (docs\branch-dispatch-and-source-keyed-learning-report.md,
    /// "Part 2").
    /// </summary>
    public class SourceIdentityTests
    {
        private static string Identity(string flSrc, string flType, string frSrc = "X", string frType = "Plain",
            string rlSrc = "X", string rlType = "Plain", string rrSrc = "X", string rrType = "Plain")
            => SourceIdentity.Compute(flSrc, flType, frSrc, frType, rlSrc, rlType, rrSrc, rrType);

        [Fact]
        public void Same_configuration_produces_the_same_identity_every_time()
        {
            string a = Identity("QAdvanceFeedback.WheelLock.Raw.FrontLeft", "Plain");
            string b = Identity("QAdvanceFeedback.WheelLock.Raw.FrontLeft", "Plain");
            Assert.Equal(a, b);
        }

        [Fact]
        public void Different_plain_property_names_produce_different_identities()
        {
            string a = Identity("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", "Plain");
            string b = Identity("QAdvanceFeedback.WheelLock.Raw.FrontLeft", "Plain");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Plain_source_is_kept_human_readable_verbatim()
        {
            string identity = SourceIdentity.Compute(
                "MyProperty", "Plain", "X", "Plain", "X", "Plain", "X", "Plain");
            Assert.Contains("MyProperty", identity);
        }

        [Fact]
        public void Scripted_source_is_hashed_not_embedded_verbatim()
        {
            string longExpression = "$prop(\"Some.Very.Long.Expression.Name.That.Should.Not.Appear.Verbatim\") * 2";
            string identity = SourceIdentity.Compute(
                longExpression, "JavaScript", "X", "Plain", "X", "Plain", "X", "Plain");
            Assert.DoesNotContain(longExpression, identity);
        }

        [Fact]
        public void Same_expression_text_hashes_to_the_same_value_every_time()
        {
            string expr = "[SomeProperty] * 1.5";
            string a = SourceIdentity.Compute(expr, "NCalc", "X", "Plain", "X", "Plain", "X", "Plain");
            string b = SourceIdentity.Compute(expr, "NCalc", "X", "Plain", "X", "Plain", "X", "Plain");
            Assert.Equal(a, b);
        }

        [Fact]
        public void Different_expression_text_hashes_to_different_values()
        {
            string a = SourceIdentity.Compute("[A]", "NCalc", "X", "Plain", "X", "Plain", "X", "Plain");
            string b = SourceIdentity.Compute("[B]", "NCalc", "X", "Plain", "X", "Plain", "X", "Plain");
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void Changing_any_one_of_the_four_wheels_changes_the_composite_identity()
        {
            string baseline = SourceIdentity.Compute("A", "Plain", "B", "Plain", "C", "Plain", "D", "Plain");
            string changedFrontLeft = SourceIdentity.Compute("A2", "Plain", "B", "Plain", "C", "Plain", "D", "Plain");
            string changedRearRight = SourceIdentity.Compute("A", "Plain", "B", "Plain", "C", "Plain", "D2", "Plain");

            Assert.NotEqual(baseline, changedFrontLeft);
            Assert.NotEqual(baseline, changedRearRight);
            Assert.NotEqual(changedFrontLeft, changedRearRight);
        }

        [Fact]
        public void Null_or_empty_source_is_handled_without_throwing()
        {
            string identity = SourceIdentity.Compute(null, "Plain", "", "Plain", "  ", "Plain", "D", "Plain");
            Assert.NotNull(identity);
        }

        [Fact]
        public void Null_scripttype_name_defaults_to_plain_handling()
        {
            string identity = SourceIdentity.Compute("Foo", null, "X", "Plain", "X", "Plain", "X", "Plain");
            Assert.Contains("Foo", identity);
        }
    }
}
