using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Direct unit tests for <see cref="BrakingVsSpeedModel"/> - the Raw-layer, car-level
    /// pedal+speed Lock model, including the low-speed fix.</summary>
    public class BrakingVsSpeedModelTests
    {
        private const double DefaultSensibility = BrakingVsSpeedModel.DefaultLockSensibility;

        // ------------------------------------------------------------------------------------
        // THE FAITHFUL MODEL.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Faithful_compute_is_zero_when_brake_is_not_pressed()
        {
            Assert.Equal(0.0, BrakingVsSpeedModel.Compute(0.0, 100.0, DefaultSensibility), 6);
        }

        [Fact]
        public void Faithful_compute_reaches_full_scale_when_both_brake_and_speed_are_fully_saturated()
        {
            double result = BrakingVsSpeedModel.Compute(90.0, 100.0, DefaultSensibility);
            Assert.Equal(1.0, result, 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(5.0)]
        [InlineData(10.0)]
        [InlineData(15.0)]
        public void Faithful_compute_is_mathematically_zero_at_or_below_15kmh_regardless_of_brake_at_default_sensibility(double speedKmh)
        {
            // At the default sensitivity (50, threshold exactly 0.5), the combined brake*speed factor
            // can reach at most 1.0 * (speedKmh/30) at this speed - for speedKmh <= 15, that is at most
            // 0.5, which the reshaping maps to exactly 0, even at maximum brake.
            double result = BrakingVsSpeedModel.Compute(100.0, speedKmh, DefaultSensibility);
            Assert.Equal(0.0, result, 9);
        }

        [Fact]
        public void Faithful_compute_becomes_nonzero_just_above_the_15kmh_dead_zone_at_full_brake()
        {
            double result = BrakingVsSpeedModel.Compute(100.0, 16.0, DefaultSensibility);
            Assert.True(result > 0.0, $"expected a nonzero reading just past the dead zone, got {result}");
        }

        [Fact]
        public void Faithful_compute_ignores_a_light_brake_dab_regardless_of_speed()
        {
            double result = BrakingVsSpeedModel.Compute(25.0, 100.0, DefaultSensibility);
            Assert.Equal(0.0, result, 6);
        }

        [Fact]
        public void Sensitivity_above_50_caps_the_achievable_ceiling_below_full_scale()
        {
            double atFullSaturation = BrakingVsSpeedModel.Compute(100.0, 100.0, lockSensibility: 100.0);
            Assert.True(atFullSaturation < 1.0, $"expected a capped ceiling above default sensitivity, got {atFullSaturation}");
            Assert.True(atFullSaturation > 0.0);
        }

        // ------------------------------------------------------------------------------------
        // THE LOW-SPEED FIX WAS REMOVED IN 1.0.7.1 - these tests are its inverse, kept rather than
        // deleted so the trade-off stays on the record.
        //
        // An earlier revision took Math.Max of the faithful brake x speed reading and a brake-only
        // floor ramped across the sensitivity dead zone, because the faithful model is mathematically
        // zero at or below 15 km/h at default sensitivity. That looked like a blind spot worth
        // covering. It was removed because SimHub's own GetSimpleBraking has no such floor - verified
        // assembly-wide, one definition and one call site - and the divergence it caused was severe:
        // at 15 km/h under braking SimHub publishes 0 where the floor published 100, and the two only
        // reconverge at 30 km/h. In effect it removed SPEED from the Lock formula below 30 km/h.
        //
        // Layer 3's contract is to reproduce ShakeIt exactly. A perceived improvement that makes Raw
        // and ShakeIt disagree by up to 100 points belongs in the Projected layer's curve, not here.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void The_faithful_model_reads_zero_below_the_sensitivity_dead_zone_exactly_as_ShakeIt_does()
        {
            // THE KNOWN BLIND SPOT, now deliberate rather than patched over. Default sensitivity puts
            // the dead zone at threshold(0.5) x SpeedFullKmh(30) = 15 km/h.
            Assert.Equal(0.0, BrakingVsSpeedModel.Compute(90.0, 8.0, DefaultSensibility), 6);
            Assert.Equal(0.0, BrakingVsSpeedModel.Compute(100.0, 15.0, DefaultSensibility), 6);
        }

        [Theory]
        // Pinned against SimHub's own GetSimpleBraking, computed directly from its formula:
        //   Reshape(Clamp(brake,0,60)/60 * Clamp(speed,0,30)/30, threshold)
        [InlineData(5.0, 0.0)]
        [InlineData(10.0, 0.0)]
        [InlineData(15.0, 0.0)]
        [InlineData(20.0, 1.0 / 3.0)]
        [InlineData(25.0, 2.0 / 3.0)]
        [InlineData(30.0, 1.0)]
        [InlineData(120.0, 1.0)]
        public void MatchesShakeItAcrossTheSpeedRange(double speedKmh, double expected)
        {
            Assert.Equal(expected, BrakingVsSpeedModel.Compute(60.0, speedKmh, DefaultSensibility), 6);
        }

        [Fact]
        public void ReadsZeroAtAStandstillEvenWithFullBrake()
        {
            Assert.Equal(0.0, BrakingVsSpeedModel.Compute(100.0, 0.0, DefaultSensibility), 6);
        }

        [Fact]
        public void RisesMonotonicallyWithSpeedAndNeverSteps()
        {
            double previous = 0.0;
            for (double speed = 0.0; speed <= 40.0; speed += 0.5)
            {
                double value = BrakingVsSpeedModel.Compute(90.0, speed, DefaultSensibility);
                Assert.True(value >= previous - 1e-9, $"dropped at speed={speed}");
                Assert.True(value - previous < 0.15, $"discontinuity near speed={speed}: {previous} -> {value}");
                previous = value;
            }
        }

        [Fact]
        public void ALightBrakeDabNeverProducesAReadingAtAnySpeed()
        {
            for (double speed = 0.0; speed <= 40.0; speed += 2.0)
                Assert.Equal(0.0, BrakingVsSpeedModel.Compute(20.0, speed, DefaultSensibility), 6);
        }

    }
}
