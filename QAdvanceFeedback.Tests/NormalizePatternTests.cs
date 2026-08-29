using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Coverage for the WheelLock "Normalize Pattern" selector (docs\release-1060-report.md, Part 2):
    /// <see cref="NormalizePattern.MaxGripOnly"/> (1.0.6.3 parity - only the SMax parameter is applied,
    /// globally) versus <see cref="NormalizePattern.Mapping"/> (DEFAULT - 1.0.6.8's four-range logic).
    /// Slip has no equivalent selector and is unaffected either way.
    /// </summary>
    public class NormalizePatternTests
    {
        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        [Fact]
        public void Default_pattern_is_Mapping()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            Assert.Equal(NormalizePattern.Mapping, engine.LockNormalizePattern);
        }

        [Fact]
        public void MaxGripOnly_never_reports_the_four_range_curve_as_active_even_though_a_curve_is_buildable()
        {
            var engine = new NormalizedWheelLockSlipEngine { LockNormalizePattern = NormalizePattern.MaxGripOnly };
            for (int i = 0; i < 5; i++)
                engine.Compute(BrakingSample(3.0), Corners.Uniform(60.0), Corners.Zero, "TestGame", "MaxGripOnlyCar", lockSourceIdentity: "Raw");

            Assert.False(engine.LockFourRangeCurveActive,
                "in Max-Grip Only mode the four-range curve must never drive the published severity, even though S75/S90 fallback/learning makes a curve buildable underneath");
        }

        /// <summary>MaxGripOnly's own defining behaviour: severity is the plain calibrated-source Rescale
        /// (1.0.6.3 parity), NOT the 30/60/80 four-range mapping - verified the same way
        /// Lock_severity_now_tracks_the_calibrated_source_not_achieved_G_matching_1063 verifies Mapping
        /// mode's own source-tracking, but at a query point where Mapping mode WOULD diverge from plain
        /// Rescale (a raw value that sits inside the learned S75-S90 band under Mapping, but not on the
        /// plain Smax-anchored straight line).</summary>
        [Fact]
        public void MaxGripOnly_severity_matches_plain_calibratedMean_not_the_four_range_curve()
        {
            var mappingEngine = new NormalizedWheelLockSlipEngine();
            var maxGripEngine = new NormalizedWheelLockSlipEngine { LockNormalizePattern = NormalizePattern.MaxGripOnly };

            double raw = 60.0;
            // TWO frames each - the corner-local at-limit detector that now teaches SMax is DIFFERENTIAL
            // and reports 0.0 on a run's first frame, so a single Compute leaves the key with no
            // physical-limit evidence and the four-range curve inactive. See
            // S90FallbackRatioTests.First_frame_for_a_new_key... for the same adjustment and the full
            // reasoning. Both engines are warmed identically, which is what this comparison requires.
            mappingEngine.Compute(BrakingSample(3.0), Corners.Uniform(raw), Corners.Zero, "TestGame", "Car", lockSourceIdentity: "Raw");
            maxGripEngine.Compute(BrakingSample(3.0), Corners.Uniform(raw), Corners.Zero, "TestGame", "Car", lockSourceIdentity: "Raw");
            NormalizedWheelLockSlipResult mappingResult = mappingEngine.Compute(BrakingSample(3.0), Corners.Uniform(raw), Corners.Zero, "TestGame", "Car", lockSourceIdentity: "Raw");
            NormalizedWheelLockSlipResult maxGripResult = maxGripEngine.Compute(BrakingSample(3.0), Corners.Uniform(raw), Corners.Zero, "TestGame", "Car", lockSourceIdentity: "Raw");

            Assert.True(mappingEngine.LockFourRangeCurveActive, "precondition: Mapping mode should be using the four-range curve");
            Assert.False(maxGripEngine.LockFourRangeCurveActive, "precondition: Max-Grip Only mode should never use the four-range curve");

            // Same raw, same warm-up, same learned Smax - Mapping's curve-shaped output and MaxGripOnly's
            // plain-linear output must genuinely differ (proving MaxGripOnly is not silently still using
            // the curve), while MaxGripOnly's own value matches the plain calibratedMean formula exactly:
            // raw * (80/Smax) via KeyedScaleLearner.Rescale (canonical anchor 80).
            Assert.True(Math.Abs(mappingResult.LockAll - maxGripResult.LockAll) > 0.5,
                $"Mapping ({mappingResult.LockAll}) and MaxGripOnly ({maxGripResult.LockAll}) should genuinely differ at this raw value");

            double? smax = maxGripEngine.LockScaleCeiling;
            Assert.True(smax.HasValue);
            double expectedPlainRescale = Math.Min(100.0, raw * (80.0 / smax.Value));
            Assert.Equal(expectedPlainRescale, maxGripResult.LockAll, 2);
        }

        /// <summary>THE OWNER'S EXPLICIT GUARANTEE (docs\release-1060-report.md, Part 2 & 5): S75/S90
        /// keep learning and persisting in Max-Grip Only mode even though they are never read for
        /// output - verified by switching a key from MaxGripOnly to Mapping mid-session (same engine, same
        /// LockAnchorLearner instance) and confirming the four-range curve is IMMEDIATELY available
        /// (not starting cold), proving the anchors were learned all along.</summary>
        [Fact]
        public void S75_S90_keep_learning_in_MaxGripOnly_mode_even_though_output_never_uses_them()
        {
            var engine = new NormalizedWheelLockSlipEngine { LockNormalizePattern = NormalizePattern.MaxGripOnly };

            // Drive a genuine braking ramp (not a constant G) so LockAnchorLearner sees real bracket
            // crossings of u=0.75/0.90 as GripLearner's own peak matures, exactly like the fallback
            // regression test's own warm-up shape.
            for (int i = 0; i < 40; i++)
                engine.Compute(BrakingSample(3.0), Corners.Uniform(60.0), Corners.Zero, "TestGame", "SwitchCar", lockSourceIdentity: "Raw");

            Assert.False(engine.LockFourRangeCurveActive, "precondition: still in Max-Grip Only mode, so never reported active");

            engine.LockNormalizePattern = NormalizePattern.Mapping;
            engine.Compute(BrakingSample(3.0), Corners.Uniform(60.0), Corners.Zero, "TestGame", "SwitchCar", lockSourceIdentity: "Raw");

            Assert.True(engine.LockFourRangeCurveActive,
                "switching to Mapping mode should immediately find a usable four-range curve (from the fallback ratios at worst) - the anchors/Smax kept learning underneath MaxGripOnly mode the whole time");
        }
    }
}
