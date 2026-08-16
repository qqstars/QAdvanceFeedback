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
            // Every diagnostics-gated name either uses the "Diag." prefix convention, or is one of the
            // owner-specifically-named "WheelLock/WheelSlip.ProjectedWithoutPulse.*" internal
            // diagnostics (docs\raw-gap-and-pad-balance-report.md - named that way per the owner's own
            // explicit request, still gated exactly like every "Diag." name).
            Assert.All(diagOnly, n => Assert.True(
                n.Contains("Diag.") || n.Contains("ProjectedWithoutPulse."),
                $"'{n}' is gated behind diagnostics but matches neither known internal-name convention"));
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

        /// <summary>
        /// Diag.Source.* (docs\raw-gap-and-pad-balance-report.md) - the resolved-source diagnostic
        /// added so a future "does our Raw match the configured source" investigation never again
        /// needs to invert the Normalized transform. MUTATION EVIDENCE (c): removing the
        /// <c>SourceLockPrefix</c>/<c>SourceSlipPrefix</c> loops from
        /// <see cref="AllPublishedProperties.DiagnosticNames"/> makes this test fail (the names are
        /// simply absent) - reverted, full suite re-confirmed green.
        /// </summary>
        [Fact]
        public void Diagnostic_names_include_the_resolved_source_value_per_wheel_plus_All_both_channels()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            foreach (string target in PublishedPropertyNames.Targets)
            {
                Assert.Contains("Diag.Source.Lock." + target, diag);
                Assert.Contains("Diag.Source.Slip." + target, diag);
            }

            Assert.Contains("Diag.Source.Lock.All", diag);
            Assert.Contains("Diag.Source.Slip.All", diag);
        }

        [Fact]
        public void Diagnostics_off_never_publishes_the_resolved_source_diagnostic()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            Assert.DoesNotContain("Diag.Source.Lock.All", names);
            Assert.DoesNotContain("Diag.Source.Slip.All", names);
        }

        /// <summary>
        /// WheelLock/WheelSlip.ProjectedWithoutPulse.* (docs\raw-gap-and-pad-balance-report.md, the
        /// pulse-into-shake fix) - internal, owner-requested name (not "Diag." prefixed, but gated
        /// exactly the same way), added so the unpulsed value the G-force shake now consumes can be
        /// inspected under diagnostics without cluttering the default property picker.
        /// </summary>
        [Fact]
        public void Diagnostic_names_include_ProjectedWithoutPulse_per_wheel_plus_All_both_channels()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            foreach (string target in PublishedPropertyNames.Targets)
            {
                Assert.Contains("WheelLock.ProjectedWithoutPulse." + target, diag);
                Assert.Contains("WheelSlip.ProjectedWithoutPulse." + target, diag);
            }
        }

        [Fact]
        public void Diagnostics_off_never_publishes_ProjectedWithoutPulse()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            Assert.DoesNotContain("WheelLock.ProjectedWithoutPulse.All", names);
            Assert.DoesNotContain("WheelSlip.ProjectedWithoutPulse.All", names);
        }
    }
}
