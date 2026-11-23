using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>The Slip Angle inputs appended to <see cref="ITelemetryFrame"/> (Task 10).</summary>
    public class TelemetryFrameSlipAngleFieldsTests
    {
        [Fact]
        public void Empty_frame_reports_every_new_reading_absent()
        {
            ITelemetryFrame f = TelemetryFrame.Empty;
            Assert.Null(f.ForwardLocalVelocity);
            Assert.Null(f.YawRateRadPerSec);
            Assert.Null(f.VerticalG);
            Assert.False(f.IsAirborne);
            Assert.Null(f.WheelsOnKerbs);
            Assert.Null(f.SurfaceGripHint);
        }

        [Fact]
        public void New_readings_round_trip_through_the_constructor()
        {
            var kerbs = new[] { false, true, false, false };
            ITelemetryFrame f = new TelemetryFrame(
                lateralLocalVelocity: 1.5,
                forwardLocalVelocity: 30.0,
                yawRateRadPerSec: -0.25,
                verticalG: 1.02,
                isAirborne: true,
                wheelsOnKerbs: kerbs);
            Assert.Equal(1.5, f.LateralLocalVelocity);
            Assert.Equal(30.0, f.ForwardLocalVelocity);
            Assert.Equal(-0.25, f.YawRateRadPerSec);
            Assert.Equal(1.02, f.VerticalG);
            Assert.True(f.IsAirborne);
            Assert.Equal(kerbs, f.WheelsOnKerbs);
            Assert.Null(f.SurfaceGripHint); // reserved
        }

        [Fact]
        public void Kerb_array_of_the_wrong_length_is_reported_absent()
        {
            Assert.Null(new TelemetryFrame(wheelsOnKerbs: new[] { true, true }).WheelsOnKerbs);
        }

        [Fact]
        public void Yaw_rate_conversion_from_degrees_per_second_keeps_the_raw_sign()
        {
            Assert.Equal(System.Math.PI, AccelerationUnits.DegToRad(180.0), 12);
            Assert.Equal(-System.Math.PI / 2, AccelerationUnits.DegToRad(-90.0), 12);
        }
    }
}
