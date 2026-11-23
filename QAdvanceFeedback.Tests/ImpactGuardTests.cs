using System;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class ImpactGuardTests
    {
        private const double Dt = 1.0 / 60.0;

        private static ImpactGuard Slip() => new ImpactGuard(ImpactGuardParameters.SlipAngle);

        private static ImpactVerdict Calm(ImpactGuard g, double dt = Dt)
            => g.Observe(1.0, 0.0, 1.0, 30.0, null, null, dt);

        [Fact]
        public void SlipAngle_parameters_are_the_spec_values()
        {
            var p = ImpactGuardParameters.SlipAngle;
            Assert.True(p.Enabled);
            Assert.Equal(8.0, p.PlausibleCapG);
            Assert.Equal(3.0, p.MaxJumpG);
            Assert.Equal(5, p.RetroFrames);
            Assert.Equal(1.5, p.ExclusionSeconds);
            Assert.Equal(0.3, p.YawResidualG);
            Assert.Equal(1.0 / 60.0, p.NominalFrameSeconds);
            Assert.False(ImpactGuardParameters.Disabled.Enabled);
        }

        [Fact]
        public void Disabled_always_allows()
        {
            var g = new ImpactGuard(ImpactGuardParameters.Disabled);
            Calm(g);
            var v = g.Observe(50.0, -50.0, double.NaN, 30.0, 5.0, 40.0, Dt);
            Assert.True(v.LearnAllowed);
            Assert.False(v.ImpactThisFrame);
            Assert.False(v.RetroInvalidate);
            Assert.Equal(0.0, v.ExclusionRemaining);
        }

        [Fact]
        public void First_frame_cannot_be_a_jump()
        {
            var g = Slip();
            var v = g.Observe(7.0, 7.0, 7.0, 30.0, null, null, Dt);
            Assert.True(v.LearnAllowed);
            Assert.False(v.ImpactThisFrame);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Jump_over_3g_on_any_axis_opens_1_5s_window(int axis)
        {
            var g = Slip();
            Calm(g);
            double lat = 1.0, lon = 0.0, vert = 1.0;
            if (axis == 0) lat += 3.5; else if (axis == 1) lon += 3.5; else vert += 3.5;
            var v = g.Observe(lat, lon, vert, 30.0, null, null, Dt);
            Assert.True(v.ImpactThisFrame);
            Assert.True(v.RetroInvalidate);
            Assert.False(v.LearnAllowed);
            Assert.Equal(1.5, v.ExclusionRemaining, 6);

            for (int i = 0; i < 80; i++)
            {
                v = g.Observe(lat, lon, vert, 30.0, null, null, Dt);
                Assert.False(v.ImpactThisFrame);
                Assert.False(v.RetroInvalidate);
                Assert.False(v.LearnAllowed);
            }
            // Allowed again by 1.5 s + one frame (91 frames after the impact frame).
            for (int i = 0; i < 11; i++) v = g.Observe(lat, lon, vert, 30.0, null, null, Dt);
            Assert.True(v.LearnAllowed);
            Assert.Equal(0.0, v.ExclusionRemaining);
        }

        [Fact]
        public void Jump_exactly_at_threshold_is_not_a_jump()
        {
            var g = Slip();
            Calm(g);
            var v = g.Observe(4.0, 0.0, 1.0, 30.0, null, null, Dt);
            Assert.False(v.ImpactThisFrame);
            Assert.True(v.LearnAllowed);
        }

        [Fact]
        public void New_jump_inside_window_restarts_it()
        {
            var g = Slip();
            Calm(g);
            g.Observe(5.0, 0.0, 1.0, 30.0, null, null, Dt);              // impact 1
            for (int i = 0; i < 60; i++) g.Observe(5.0, 0.0, 1.0, 30.0, null, null, Dt);
            var v = g.Observe(1.0, 0.0, 1.0, 30.0, null, null, Dt);      // impact 2 (-4 g)
            Assert.True(v.ImpactThisFrame);
            Assert.True(v.RetroInvalidate);
            Assert.Equal(1.5, v.ExclusionRemaining, 6);
            for (int i = 0; i < 80; i++) v = g.Observe(1.0, 0.0, 1.0, 30.0, null, null, Dt);
            Assert.False(v.LearnAllowed);   // would have expired under the first window
            for (int i = 0; i < 11; i++) v = g.Observe(1.0, 0.0, 1.0, 30.0, null, null, Dt);
            Assert.True(v.LearnAllowed);
        }

        [Fact]
        public void Over_8g_rejected_without_window()
        {
            var g = Slip();
            Calm(g);
            var v = g.Observe(2.0, 8.5, 1.0, 30.0, null, null, Dt);
            Assert.False(v.LearnAllowed);
            Assert.False(v.ImpactThisFrame);
            Assert.False(v.RetroInvalidate);
            Assert.Equal(0.0, v.ExclusionRemaining);
            // No window, and previous values were not updated, so a calm frame is no jump.
            Assert.True(Calm(g).LearnAllowed);
        }

        [Fact]
        public void Non_finite_rejected()
        {
            var g = Slip();
            Calm(g);
            foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var v = g.Observe(bad, 0.0, 1.0, 30.0, null, null, Dt);
                Assert.False(v.LearnAllowed);
                Assert.False(v.ImpactThisFrame);
                Assert.Equal(0.0, v.ExclusionRemaining);
                v = g.Observe(1.0, bad, 1.0, 30.0, null, null, Dt);
                Assert.False(v.LearnAllowed);
                v = g.Observe(1.0, 0.0, bad, 30.0, null, null, Dt);
                Assert.False(v.LearnAllowed);
            }
            Assert.True(Calm(g).LearnAllowed);
        }

        [Fact]
        public void Zero_or_NaN_dt_ages_by_nominal_frame()
        {
            foreach (var badDt in new[] { 0.0, -1.0, double.NaN })
            {
                var g = Slip();
                Calm(g);
                g.Observe(5.0, 0.0, 1.0, 30.0, null, null, Dt);
                var v = g.Observe(5.0, 0.0, 1.0, 30.0, null, null, badDt);
                Assert.Equal(1.5 - Dt, v.ExclusionRemaining, 6);
                for (int i = 0; i < 88; i++) v = g.Observe(5.0, 0.0, 1.0, 30.0, null, null, badDt);
                Assert.False(v.LearnAllowed);
                for (int i = 0; i < 3; i++) v = g.Observe(5.0, 0.0, 1.0, 30.0, null, null, badDt);
                Assert.True(v.LearnAllowed);
            }
        }

        [Fact]
        public void Yaw_residual_over_0_3g_blocks_learning()
        {
            var g = Slip();
            // speed 30 m/s, yaw 0.3 rad/s, dVlat/dt 0 -> predicted 9/9.80665 g; latG 1.5 -> residual ~0.582
            var v = g.Observe(1.5, 0.0, 1.0, 30.0, 0.3, 0.0, Dt);
            Assert.False(v.LearnAllowed);
            Assert.False(v.ImpactThisFrame);
            Assert.Equal(1.5 - 9.0 / 9.80665, v.YawResidualG, 6);
            // Consistent frame next: allowed again at once (no window).
            v = g.Observe(0.9, 0.0, 1.0, 30.0, 0.3, 0.0, Dt);
            Assert.True(v.LearnAllowed);
        }

        [Fact]
        public void Missing_yaw_skips_residual_check()
        {
            var g = Slip();
            var v = g.Observe(1.5, 0.0, 1.0, 30.0, null, 0.0, Dt);
            Assert.True(v.LearnAllowed);
            Assert.Equal(0.0, v.YawResidualG);
            v = g.Observe(1.5, 0.0, 1.0, 30.0, 0.3, null, Dt);
            Assert.True(v.LearnAllowed);
            Assert.Equal(0.0, v.YawResidualG);
        }

        [Fact]
        public void Reset_clears_previous_values_and_window()
        {
            var g = Slip();
            Calm(g);
            g.Observe(5.0, 0.0, 1.0, 30.0, null, null, Dt);
            g.Reset();
            var v = g.Observe(5.0, 0.0, 1.0, 30.0, null, null, Dt);
            Assert.True(v.LearnAllowed);
            Assert.False(v.ImpactThisFrame);
        }
    }
}
