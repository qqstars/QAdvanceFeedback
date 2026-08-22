using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// SPEED-DEPENDENT GRIP MODEL, END-TO-END (speed-aware-grip-report.md) - THE BUG, confirmed on the
    /// real captured logs: an aero car's genuine low-speed lockup could never trip
    /// <see cref="NormalizedWheelLockSlipEngine.PhysicalLimitRatioThreshold"/> (0.85), because
    /// <see cref="NormalizedWheelLockSlipEngine.LockPhysicalReference"/>'s single learned peak was
    /// dominated by high-speed braking evidence. This exercises the fix through the PUBLIC
    /// <see cref="NormalizedWheelLockSlipEngine.Compute"/> API (not just <see cref="GripLearner"/> in
    /// isolation), confirming the speed dimension is actually wired through the real engine.
    /// </summary>
    public class NormalizedEngineSpeedModelTests
    {
        private static ITelemetrySample BrakingSampleAtSpeed(double speedKmh, double gMagnitude)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: speedKmh + 1.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: speedKmh,
                longitudinalG: -gMagnitude,
                brakePercent: 80.0);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        [Fact]
        public void A_genuine_low_speed_lockup_now_teaches_the_physical_reference_and_is_recognised_as_at_limit()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var rnd = new Random(11);

            // Teach a realistic aero shape: peak_decel(v) = 1.5 + 0.0006 * v_mps^2, across a wide speed
            // range - mirrors the real captured-log magnitude (see the report). Every frame brakes AT
            // that speed's own true limit (with mild noise) so the physical reference actually learns the
            // speed-dependent ceiling, not just a single flat scalar.
            foreach (double speedKmh in new[] { 20.0, 70.0, 120.0, 170.0, 220.0, 280.0 })
            {
                double vMps = speedKmh / 3.6;
                double truePeak = 1.5 + 0.0006 * vMps * vMps;
                for (int i = 0; i < 60; i++)
                {
                    double noisy = truePeak * (0.97 + 0.06 * rnd.NextDouble());
                    engine.Compute(BrakingSampleAtSpeed(speedKmh, noisy), Corners.Zero, Corners.Zero, "GameA", "Car1");
                }
            }

            // THE FIX: at 20 km/h, a lockup near this car's OWN low-speed limit (~1.5g) must now be
            // recognised as physically at the limit (ratio >= PhysicalLimitRatioThreshold) - the exact
            // scenario the bug made permanently impossible when only high-speed evidence set the ceiling.
            double lowSpeedRatio = engine.LockPhysicalReference.Ratio(
                "GameA", "Car1", 1.48, sourceIdentity: "", surfaceBucket: NormalizedWheelLockSlipEngine.SealedSurfaceBucket,
                applyColdStartCeiling: false, speedKmh: 20.0);
            Assert.True(lowSpeedRatio >= NormalizedWheelLockSlipEngine.PhysicalLimitRatioThreshold,
                $"a genuine low-speed lockup should now cross the physical-limit threshold, got ratio={lowSpeedRatio}");

            // And the SAME 1.48g reading at high speed must NOT look anywhere near the limit (the car's
            // high-speed ceiling is much higher) - proving the reference is genuinely speed-appropriate.
            double highSpeedRatio = engine.LockPhysicalReference.Ratio(
                "GameA", "Car1", 1.48, sourceIdentity: "", surfaceBucket: NormalizedWheelLockSlipEngine.SealedSurfaceBucket,
                applyColdStartCeiling: false, speedKmh: 280.0);
            Assert.True(highSpeedRatio < 0.5,
                $"the same 1.48g reading should look far from the limit at high speed, got ratio={highSpeedRatio}");
        }

        [Fact]
        public void Without_speed_evidence_the_engine_behaves_exactly_as_before_this_feature()
        {
            // Every existing caller/test of Compute() never varies groundSpeedKmh across a wide-enough
            // range to teach a real (g_mech,k) fit within the SAME narrow band this fixture uses (a single
            // repeated speed) - the physical reference must degrade to the flat, speed-blind behaviour
            // this engine always had.
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSampleAtSpeed(150.0, 2.0 + 0.01 * (i % 5)), Corners.Zero, Corners.Zero, "GameA", "Car1");

            double atTaughtSpeed = engine.LockPhysicalReference.Ratio(
                "GameA", "Car1", 2.0, sourceIdentity: "", surfaceBucket: NormalizedWheelLockSlipEngine.SealedSurfaceBucket,
                applyColdStartCeiling: false, speedKmh: 150.0);
            double atADifferentSpeed = engine.LockPhysicalReference.Ratio(
                "GameA", "Car1", 2.0, sourceIdentity: "", surfaceBucket: NormalizedWheelLockSlipEngine.SealedSurfaceBucket,
                applyColdStartCeiling: false, speedKmh: 250.0);

            // A narrow speed band (all evidence at ~150 km/h) must never fit a real aero curve - the
            // reference must be identical regardless of what speed is queried.
            Assert.Equal(atTaughtSpeed, atADifferentSpeed, 9);
        }
    }
}
