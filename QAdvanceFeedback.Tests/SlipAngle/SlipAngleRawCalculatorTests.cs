using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    public class SlipAngleRawCalculatorTests
    {
        private static SlipAngleInput Native(Corners w, bool[] kerbs = null)
            => new SlipAngleInput("g", true, w, true, SlipAngleUnit.Degrees, false, kerbs);

        private static SlipAngleRawResult Run(SlipAngleInput i, ITelemetryFrame f = null, bool isNative = true,
            double k = 1.0, double wMax = 0.85, double wMin = 0.15)
            => SlipAngleRawCalculator.Compute(i, f ?? new TelemetryFrame(), isNative, k, wMax, wMin);

        [Fact]
        public void Axle_is_mean_of_two_wheels()
        {
            var r = Run(Native(new Corners(-2, 4, 5, -7)));
            Assert.Equal(3.0, r.FrontDeg.Value, 9);
            Assert.Equal(6.0, r.RearDeg.Value, 9);
            Assert.Equal(SourceNativeness.Native, r.Tier);
            Assert.Equal(2.0, r.WheelsDeg.Value.FrontLeft);
        }

        [Fact]
        public void Kerb_wheel_dropped_other_used()
        {
            var r = Run(Native(new Corners(2, 10, 5, 7), new[] { false, true, false, false }));
            Assert.Equal(2.0, r.FrontDeg.Value, 9);
            Assert.Equal(6.0, r.RearDeg.Value, 9);
        }

        [Fact]
        public void Both_wheels_on_kerb_axle_null()
        {
            var r = Run(Native(new Corners(2, 10, 5, 7), new[] { true, true, false, false }));
            Assert.Null(r.FrontDeg);
            Assert.Equal(6.0, r.RearDeg.Value, 9);
            Assert.Equal(6.0, r.AllDeg.Value, 9);
        }

        [Fact]
        public void Equalisation_scales_front_by_k()
        {
            var r = Run(Native(new Corners(4, 4, 6, 6)), k: 1.5);
            Assert.Equal(6.0, r.AllDeg.Value, 9);
            var r2 = Run(Native(new Corners(4, 4, 2, 2)), k: 1.5);
            Assert.Equal(0.85 * 6 + 0.15 * 2, r2.AllDeg.Value, 9);
        }

        [Fact]
        public void All_is_max_min_blend()
        {
            var r = Run(Native(new Corners(4, 4, 8, 8)), k: 1.5);
            Assert.Equal(7.7, r.AllDeg.Value, 9);
        }

        [Fact]
        public void k_clamped_0_3_to_2_0()
        {
            // Owner, 2026-11-22: the floor is 0.3 (was 0.5) - the F1 log measures 0.29-0.46.
            var w = Native(new Corners(4, 4, 0, 0));
            Assert.Equal(8.0 * 0.85, Run(w, k: 10).AllDeg.Value, 9);
            Assert.Equal(4.0 * 0.3 * 0.85, Run(w, k: 0.01).AllDeg.Value, 9);
            Assert.Equal(4.0 * 0.85, Run(w, k: double.NaN).AllDeg.Value, 9);
        }

        [Fact]
        public void Fallback_uses_body_slip_atan2()
        {
            var f = new TelemetryFrame(lateralLocalVelocity: 2.0, forwardLocalVelocity: 20.0);
            var r = Run(SlipAngleInput.Empty, f, isNative: false);
            Assert.Equal(Math.Atan2(2, 20) * 180 / Math.PI, r.AllDeg.Value, 9);
            Assert.Equal(5.7106, r.AllDeg.Value, 3);
            Assert.Equal(r.AllDeg, r.RearDeg);
            Assert.Null(r.FrontDeg);
            Assert.Equal(SourceNativeness.Fallback, r.Tier);
            Assert.Null(r.WheelsDeg);
        }

        [Fact]
        public void Fallback_with_null_lateral_or_forward_velocity_is_null()
        {
            Assert.Null(Run(SlipAngleInput.Empty, new TelemetryFrame(forwardLocalVelocity: 20.0), false).AllDeg);
            Assert.Null(Run(SlipAngleInput.Empty, new TelemetryFrame(lateralLocalVelocity: 2.0), false).AllDeg);
            Assert.Null(Run(SlipAngleInput.Empty, new TelemetryFrame(), false).AllDeg);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(0.2)]
        public void Reverse_or_standstill_uses_1mps_floor_and_never_NaN(double vfwd)
        {
            var f = new TelemetryFrame(lateralLocalVelocity: 2.0, forwardLocalVelocity: vfwd);
            var r = Run(SlipAngleInput.Empty, f, false);
            Assert.True(ClampMath.IsFinite(r.AllDeg.Value));
            Assert.Equal(Math.Atan2(2, 1) * 180 / Math.PI, r.AllDeg.Value, 9);
        }

        [Fact]
        public void Native_null_wheels_falls_back()
        {
            var input = new SlipAngleInput("g", true, null, true);
            var f = new TelemetryFrame(lateralLocalVelocity: 2.0, forwardLocalVelocity: 20.0);
            var r = Run(input, f, isNative: true);
            Assert.Equal(SourceNativeness.Fallback, r.Tier);
            Assert.Equal(5.7106, r.AllDeg.Value, 3);
        }
    }
}
