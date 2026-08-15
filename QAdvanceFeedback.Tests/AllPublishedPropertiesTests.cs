using System.Linq;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="AllPublishedProperties"/> - the exact published-name set, and the
    /// diagnostics gate, asserted without any SimHub/AttachDelegate involvement. Verbatim strings are
    /// checked directly (per the brief's explicit instruction), including the full
    /// "QAdvanceFeedback." prefix via the same simulated GetName every other published-name test in
    /// this project uses.
    /// </summary>
    public class AllPublishedPropertiesTests
    {
        private static string SimulatedGetName(string name) => "QAdvanceFeedback." + name;

        [Fact]
        public void Product_names_total_exactly_62_and_are_all_distinct()
        {
            string[] names = AllPublishedProperties.ProductNames().ToArray();
            Assert.Equal(62, names.Length);
            Assert.Equal(names.Length, names.Distinct().Count());
        }

        [Fact]
        public void Verbatim_names_match_the_brief_exactly()
        {
            string[] names = AllPublishedProperties.ProductNames().Select(SimulatedGetName).ToArray();

            Assert.Contains("QAdvanceFeedback.WheelLock.Raw.All", names);
            Assert.Contains("QAdvanceFeedback.WheelSlip.Raw.All", names);
            Assert.Contains("QAdvanceFeedback.WheelLock.Normalized.All", names);
            Assert.Contains("QAdvanceFeedback.WheelSlip.Normalized.All", names);
            Assert.Contains("QAdvanceFeedback.WheelLock.Projected.All", names);
            Assert.Contains("QAdvanceFeedback.WheelSlip.Projected.All", names);
            Assert.Contains("QAdvanceFeedback.WheelLock.Raw.FrontLeft", names);
            Assert.Contains("QAdvanceFeedback.GForce.Bottom.FrontLeft", names);
            Assert.Contains("QAdvanceFeedback.GForce.Back.TopRight", names);
        }

        [Fact]
        public void Diagnostics_off_publishes_only_the_62_product_names()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();

            Assert.Equal(62, names.Length);
            foreach (string name in names) Assert.DoesNotContain("Diag.", name);
        }

        [Fact]
        public void Diagnostics_on_adds_the_diagnostic_names_on_top_of_the_62_product_names()
        {
            string[] namesOff = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            string[] namesOn = AllPublishedProperties.AllNames(diagnosticsEnabled: true).ToArray();

            Assert.True(namesOn.Length > namesOff.Length);
            foreach (string name in namesOff) Assert.Contains(name, namesOn);

            string[] diagOnly = namesOn.Except(namesOff).ToArray();
            Assert.All(diagOnly, n => Assert.Contains("Diag.", n));
            Assert.True(diagOnly.Length > 0);
        }

        [Fact]
        public void Diagnostic_names_cover_direction_motion_learner_and_gforce_state()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            Assert.Contains("Diag.Direction", diag);
            Assert.Contains("Diag.MotionLevel", diag);
            Assert.Contains("Diag.MotionMagnitudeG", diag);
            Assert.Contains("Diag.Lock.LearnedPeakG", diag);
            Assert.Contains("Diag.Lock.LearnerConfidence", diag);
            Assert.Contains("Diag.Slip.LearnedPeakG", diag);
            Assert.Contains("Diag.Slip.LearnerConfidence", diag);
            Assert.Contains("Diag.GForce.LearnedAccelMaxG", diag);
            Assert.Contains("Diag.GForce.LearnedDecelMaxG", diag);
        }
    }
}
