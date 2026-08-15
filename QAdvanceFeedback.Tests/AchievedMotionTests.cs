using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class AchievedMotionTests
    {
        private static TelemetrySample Sample(ITelemetryFrame newFrame, ITelemetryFrame oldFrame, TimeSpan? dt)
            => new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, dt);

        [Fact]
        public void Direct_longitudinal_g_is_used_when_present()
        {
            var newFrame = new TelemetryFrame(longitudinalG: -1.4);
            var sample = Sample(newFrame, TelemetryFrame.Empty, TimeSpan.FromMilliseconds(16));

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Direct, result.Level);
            Assert.Equal(1.4, result.MagnitudeG, 6); // magnitude, sign-agnostic - see this class's remarks
        }

        [Fact]
        public void Falls_back_to_differentiated_ground_speed_when_g_is_absent()
        {
            // 100 km/h -> 80 km/h over 0.5s: delta = -20 km/h = -5.5556 m/s over 0.5s = -11.111 m/s^2
            // = -1.1327g magnitude.
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 80.0);
            var sample = Sample(newFrame, oldFrame, TimeSpan.FromSeconds(0.5));

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Derived, result.Level);
            Assert.Equal(1.13, result.MagnitudeG, 2);
        }

        [Fact]
        public void Falls_back_to_plain_SpeedKmh_when_GroundSpeedKmh_is_absent()
        {
            var oldFrame = new TelemetryFrame(speedKmh: 60.0);
            var newFrame = new TelemetryFrame(speedKmh: 40.0);
            var sample = Sample(newFrame, oldFrame, TimeSpan.FromSeconds(1.0));

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Derived, result.Level);
            Assert.True(result.MagnitudeG > 0.0);
        }

        [Fact]
        public void Unavailable_when_neither_g_nor_speed_nor_dt_exist()
        {
            var sample = Sample(TelemetryFrame.Empty, TelemetryFrame.Empty, null);

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Unavailable, result.Level);
            Assert.Equal(0.0, result.MagnitudeG);
        }

        [Fact]
        public void Unavailable_when_speed_exists_but_dt_does_not_the_very_first_frame_of_a_session()
        {
            var newFrame = new TelemetryFrame(groundSpeedKmh: 80.0);
            var sample = Sample(newFrame, TelemetryFrame.Empty, null);

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Unavailable, result.Level);
        }

        [Fact]
        public void Unavailable_when_dt_is_zero_or_negative()
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 80.0);
            var sample = Sample(newFrame, oldFrame, TimeSpan.Zero);

            Assert.Equal(AchievedMotion.SignalLevel.Unavailable, AchievedMotion.Resolve(sample).Level);
        }

        [Fact]
        public void Non_finite_longitudinal_g_falls_back_to_derived_speed_instead_of_being_used_directly()
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 80.0, longitudinalG: double.NaN);
            var sample = Sample(newFrame, oldFrame, TimeSpan.FromSeconds(0.5));

            AchievedMotion.Result result = AchievedMotion.Resolve(sample);

            Assert.Equal(AchievedMotion.SignalLevel.Derived, result.Level);
        }
    }
}
