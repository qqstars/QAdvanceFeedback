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
        /// docs\shakeit-silence-diagnosis-report.md - the resolved (gameId, carId) every learner/scale
        /// key is keyed under, added because no prior capture could carry this at all, making the
        /// car-id-fragmentation question (the owner's own Parameters.json shows the same physical F1 25
        /// car as BOTH "Sauber" and "F1 Generic") impossible to investigate directly from a session log.
        /// </summary>
        [Fact]
        public void Diagnostic_names_include_the_resolved_game_and_car_id()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            Assert.Contains("Diag.GameId", diag);
            Assert.Contains("Diag.CarId", diag);
        }

        [Fact]
        public void Diagnostics_off_never_publishes_the_resolved_game_and_car_id()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            Assert.DoesNotContain("Diag.GameId", names);
            Assert.DoesNotContain("Diag.CarId", names);
        }

        /// <summary>
        /// SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - the degraded-state
        /// diagnostic that makes "the configured source went quiet, we substituted Raw" distinguishable
        /// from "genuinely no lockup", per this task's own explicit requirement.
        /// </summary>
        [Fact]
        public void Diagnostic_names_include_the_source_fallback_active_flags()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            Assert.Contains("Diag.Lock.SourceFallbackActive", diag);
            Assert.Contains("Diag.Slip.SourceFallbackActive", diag);
        }

        [Fact]
        public void Diagnostics_off_never_publishes_the_source_fallback_active_flags()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            Assert.DoesNotContain("Diag.Lock.SourceFallbackActive", names);
            Assert.DoesNotContain("Diag.Slip.SourceFallbackActive", names);
        }

        /// <summary>docs\branch-dispatch-and-source-keyed-learning-report.md - which of SimHub's own
        /// nine decompiled branches Layer 3 ran this frame, per channel, gated behind diagnostics exactly
        /// like every other internal-state name in this group.</summary>
        [Fact]
        public void Diagnostic_names_include_the_selected_branch_per_channel()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();
            Assert.Contains("Diag.SelectedBranch.Lock", diag);
            Assert.Contains("Diag.SelectedBranch.Slip", diag);
        }

        [Fact]
        public void Diagnostics_off_never_publishes_the_selected_branch()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            Assert.DoesNotContain("Diag.SelectedBranch.Lock", names);
            Assert.DoesNotContain("Diag.SelectedBranch.Slip", names);
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

        /// <summary>
        /// Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) - the raw
        /// per-wheel WheelRPS/WheelSpeed/WheelSlipRatio telemetry plus the FeedbackCapabilities flags
        /// added so a future capture can fit SimHub's own three candidate Lock branches directly
        /// against real numbers. Only four wheels (no Front/Rear/Left/Right/All aggregate - see
        /// AllPublishedProperties' own remarks on why).
        /// </summary>
        [Fact]
        public void Diagnostic_names_include_the_raw_wheel_telemetry_and_capabilities_diagnostics()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();

            Assert.Contains("Diag.Telemetry.GroundSpeedKmh", diag);
            Assert.Contains("Diag.Telemetry.SpeedKmh", diag);
            Assert.Contains("Diag.Telemetry.GroundSpeedMps", diag);
            Assert.Contains("Diag.Telemetry.Rpm", diag);
            Assert.Contains("Diag.Telemetry.Gear", diag);
            Assert.Contains("Diag.Telemetry.BrakePercent", diag);
            Assert.Contains("Diag.Telemetry.ThrottlePercent", diag);
            Assert.Contains("Diag.Telemetry.ClutchPercent", diag);
            Assert.Contains("Diag.Telemetry.LateralLocalVelocity", diag);
            Assert.Contains("Diag.Telemetry.LongitudinalG", diag);
            Assert.Contains("Diag.Telemetry.LateralG", diag);

            foreach (string wheel in new[] { "FrontLeft", "FrontRight", "RearLeft", "RearRight" })
            {
                Assert.Contains("Diag.Telemetry.WheelRPS." + wheel, diag);
                Assert.Contains("Diag.Telemetry.WheelSpeed." + wheel, diag);
                Assert.Contains("Diag.Telemetry.WheelSlipRatio." + wheel, diag);
            }

            Assert.Contains("Diag.Capabilities.WheelsSlip", diag);
            Assert.Contains("Diag.Capabilities.WheelsRPS", diag);
            Assert.Contains("Diag.Capabilities.WheelsSpeed", diag);
            Assert.Contains("Diag.Capabilities.WheelsSlipDirectMode", diag);
            Assert.Contains("Diag.Capabilities.WheelSlipUseSimpleBraking", diag);
            Assert.Contains("Diag.Capabilities.DetectLockFromWheelsSpeed", diag);
            Assert.Contains("Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius", diag);
            Assert.Contains("Diag.Capabilities.Speed", diag);
            Assert.Contains("Diag.Capabilities.Rpm", diag);
            Assert.Contains("Diag.Capabilities.GameFamily", diag);
            Assert.Contains("Diag.Capabilities.WheelSlipCalibrationProviderSlipScale", diag);
        }

        /// <summary>
        /// THE test whose failure is this task's own required mutation evidence: publishing the new
        /// raw-telemetry/capabilities diagnostics unconditionally (i.e. moving any of the
        /// <c>DiagnosticNames</c> lines added for this task above the
        /// <c>if (!diagnosticsEnabled) yield break;</c> gate in <c>AllNames</c>, or into
        /// <c>ProductNames</c>) makes this test fail - see docs\telemetry-diagnostics-report.md's own
        /// mutation section for the exact revert-and-rerun this was checked against.
        /// </summary>
        [Fact]
        public void Diagnostics_off_never_publishes_the_raw_wheel_telemetry_or_capabilities_diagnostics()
        {
            string[] names = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();

            Assert.DoesNotContain("Diag.Telemetry.GroundSpeedKmh", names);
            Assert.DoesNotContain("Diag.Telemetry.WheelRPS.FrontLeft", names);
            Assert.DoesNotContain("Diag.Telemetry.WheelSpeed.FrontLeft", names);
            Assert.DoesNotContain("Diag.Telemetry.WheelSlipRatio.FrontLeft", names);
            Assert.DoesNotContain("Diag.Capabilities.WheelsRPS", names);
            Assert.DoesNotContain("Diag.Capabilities.GameFamily", names);

            // The default published set (the 62 product names) must be completely unaffected by this
            // task - re-asserted here, alongside the new names' absence, as the single most direct
            // "default set unchanged" check this task's own brief asks for.
            Assert.Equal(62, AllPublishedProperties.ProductNames().Count());
            Assert.Equal(62, names.Length);
        }
    }
}
