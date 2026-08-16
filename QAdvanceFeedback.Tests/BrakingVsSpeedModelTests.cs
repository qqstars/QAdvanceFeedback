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
        // THE LOW-SPEED FIX.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Low_speed_fix_produces_a_strong_reading_where_the_faithful_model_is_mathematically_zero()
        {
            double faithful = BrakingVsSpeedModel.Compute(90.0, 8.0, DefaultSensibility);
            double fixedValue = BrakingVsSpeedModel.ComputeWithLowSpeedFix(90.0, 8.0, DefaultSensibility);

            Assert.Equal(0.0, faithful, 6);
            Assert.True(fixedValue > 0.4, $"expected a strong low-speed reading, got {fixedValue}");
        }

        [Fact]
        public void Low_speed_fix_never_reduces_the_faithful_reading()
        {
            for (double speed = 0.0; speed <= 40.0; speed += 2.0)
            {
                double faithful = BrakingVsSpeedModel.Compute(85.0, speed, DefaultSensibility);
                double fixedValue = BrakingVsSpeedModel.ComputeWithLowSpeedFix(85.0, speed, DefaultSensibility);
                Assert.True(fixedValue >= faithful - 1e-9,
                    $"at speed={speed}, fixed ({fixedValue}) must never be below faithful ({faithful})");
            }
        }

        [Fact]
        public void Low_speed_fix_never_manufactures_a_reading_from_a_light_brake_dab()
        {
            for (double speed = 0.0; speed <= 30.0; speed += 3.0)
            {
                double fixedValue = BrakingVsSpeedModel.ComputeWithLowSpeedFix(20.0, speed, DefaultSensibility);
                Assert.Equal(0.0, fixedValue, 6);
            }
        }

        [Fact]
        public void Low_speed_fix_reads_zero_at_a_genuine_standstill_even_with_full_brake()
        {
            double result = BrakingVsSpeedModel.ComputeWithLowSpeedFix(100.0, 0.0, DefaultSensibility);
            Assert.Equal(0.0, result, 6);
        }

        [Fact]
        public void Low_speed_fix_ramps_up_from_standstill_rather_than_stepping()
        {
            double prev = 0.0;
            for (double speed = 0.0; speed <= 15.0; speed += 1.0)
            {
                double value = BrakingVsSpeedModel.ComputeWithLowSpeedFix(90.0, speed, DefaultSensibility);
                Assert.True(value >= prev - 1e-9, $"expected a monotonic ramp from standstill, dropped at speed={speed}");
                prev = value;
            }
        }

        [Fact]
        public void Low_speed_fix_has_no_large_discontinuity_anywhere_across_the_full_speed_range()
        {
            double? prev = null;
            for (double speed = 0.0; speed <= 40.0; speed += 0.5)
            {
                double value = BrakingVsSpeedModel.ComputeWithLowSpeedFix(90.0, speed, DefaultSensibility);
                if (prev.HasValue)
                {
                    Assert.True(System.Math.Abs(value - prev.Value) < 0.15,
                        $"discontinuity near speed={speed}: {prev.Value} -> {value}");
                }
                prev = value;
            }
        }

        [Fact]
        public void Low_speed_fix_converges_with_the_faithful_model_at_and_above_SpeedFullKmh()
        {
            double faithful = BrakingVsSpeedModel.Compute(90.0, BrakingVsSpeedModel.SpeedFullKmh, DefaultSensibility);
            double fixedValue = BrakingVsSpeedModel.ComputeWithLowSpeedFix(90.0, BrakingVsSpeedModel.SpeedFullKmh, DefaultSensibility);

            Assert.Equal(faithful, fixedValue, 6);
        }
    }
}
