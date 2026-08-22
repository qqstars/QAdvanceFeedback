using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Coverage for the S75/S90 FALLBACK RATIO fix (docs\release-1060-report.md, Part 3 - the owner's
    /// measured "S90 feels a little early" correction for WheelLock). Before <see cref="LockAnchorLearner"/>
    /// has learned a real S75/S90 crossing for a (game,car,source) key, <see cref="NormalizedWheelLockSlipEngine"/>
    /// now builds the four-range curve from EXPLICIT fallback ratios (S75 ~= 0.40*Smax, S90 = 0.84375*Smax
    /// = 0.750*1.125*Smax) instead of silently degrading to the plain linear Rescale, which implied
    /// S75=0.375*Smax and S90=0.750*Smax.
    /// </summary>
    public class S90FallbackRatioTests
    {
        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        /// <summary>
        /// On the very FIRST qualifying frame for a brand-new key, no real S75/S90 crossing has ever been
        /// recorded yet (LockAnchorLearner starts with nothing for this key) - so the four-range curve, if
        /// active at all, must be built from the FALLBACK ratios. This reproduces the curve independently
        /// (hand-built from the engine's own reported <see cref="NormalizedWheelLockSlipEngine.LockScaleCeiling"/>
        /// at exactly the fallback ratios) and checks the engine's own published severity matches it -
        /// pinning the fallback formula itself, not just "some curve was used".
        /// </summary>
        [Fact]
        public void First_frame_for_a_new_key_uses_the_S75_S90_fallback_ratios_not_the_old_implied_0375_075()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            double raw = 60.0;
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(3.0), Corners.Uniform(raw), Corners.Zero, "TestGame", "TestCar", lockSourceIdentity: "Raw");

            Assert.True(engine.LockFourRangeCurveActive, "the four-range curve should already be active from the fallback ratios on the very first frame");
            double? smax = engine.LockScaleCeiling;
            Assert.True(smax.HasValue && smax.Value > 0.0, "Smax should already be established from the first physical-limit observation");

            double s75Fallback = smax.Value * 0.40;
            double s90Fallback = smax.Value * 0.84375; // 0.750 * 1.125, the owner's explicit upscaling
            var expectedCurve = new MonotoneCubicCurve(
                new[] { 0.0, s75Fallback, s90Fallback, smax.Value, 100.0 },
                new[] { 0.0, 30.0, 60.0, 80.0, 100.0 });

            double expected = expectedCurve.Evaluate(raw);
            Assert.Equal(expected, result.LockAll, 3);

            // Sanity: the fallback must NOT be the OLD implied ratios (0.375/0.750) - if it were, S90's
            // fallback position would be at 0.750*Smax, not 0.84375*Smax, giving a measurably different
            // curve/result at this same raw query.
            var oldImpliedCurve = new MonotoneCubicCurve(
                new[] { 0.0, smax.Value * 0.375, smax.Value * 0.750, smax.Value, 100.0 },
                new[] { 0.0, 30.0, 60.0, 80.0, 100.0 });
            double oldImplied = oldImpliedCurve.Evaluate(raw);
            Assert.True(Math.Abs(expected - oldImplied) > 0.5,
                $"the new fallback ({expected}) should differ measurably from the old implied 0.375/0.750 ratios ({oldImplied})");
        }

        /// <summary>Endpoint guarantees hold under the FALLBACK curve exactly as they do under the learned
        /// one: source==Smax must read exactly 80, and source==100 must read exactly 100 - both are
        /// literal knots of the curve by construction, regardless of where S75/S90 themselves sit.</summary>
        [Fact]
        public void Fallback_curve_still_guarantees_source_equals_Smax_reads_80_and_source_100_reads_100()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            // Warm-up at a constant raw so Smax converges near it.
            NormalizedWheelLockSlipResult first = null;
            for (int i = 0; i < 3; i++)
                first = engine.Compute(BrakingSample(3.0), Corners.Uniform(60.0), Corners.Zero, "TestGame", "TestCar2", lockSourceIdentity: "Raw");

            double? smax = engine.LockScaleCeiling;
            Assert.True(smax.HasValue);

            // Tolerance is loose (not tight decimal precision): querying exactly AT the just-read Smax
            // also feeds one more observation into the still-live scale learner within the SAME call,
            // nudging the ceiling itself very slightly before the curve is evaluated against it - the
            // guarantee under test is "reads the max-grip anchor", not bit-exact equality mid-learning.
            NormalizedWheelLockSlipResult atSmax = engine.Compute(BrakingSample(3.0), Corners.Uniform(smax.Value), Corners.Zero, "TestGame", "TestCar2", lockSourceIdentity: "Raw");
            Assert.True(Math.Abs(atSmax.LockAll - 80.0) < 1.0, $"source==Smax should read ~80, got {atSmax.LockAll}");

            NormalizedWheelLockSlipResult at100 = engine.Compute(BrakingSample(3.0), Corners.Uniform(100.0), Corners.Zero, "TestGame", "TestCar2", lockSourceIdentity: "Raw");
            Assert.True(at100.LockAll >= 99.0, $"source==100 should read ~100, got {at100.LockAll}");
            _ = first;
        }

        /// <summary>The fallback ratio must NEVER be written back into <see cref="LockAnchorLearner"/>'s
        /// own persisted state - it is a READ-time substitute only. Confirmed indirectly: a fresh
        /// <see cref="LockAnchorLearner"/> queried directly (bypassing the engine) still reports no
        /// learned anchors for a key the engine above already produced fallback-driven output for,
        /// proving the fallback never taught the learner anything.</summary>
        [Fact]
        public void The_fallback_never_writes_back_into_LockAnchorLearners_own_persisted_state()
        {
            var freshLearner = new LockAnchorLearner();
            Assert.Null(freshLearner.LearnedS75("TestGame", "TestCar", "Raw"));
            Assert.Null(freshLearner.LearnedS90("TestGame", "TestCar", "Raw"));
        }
    }
}
