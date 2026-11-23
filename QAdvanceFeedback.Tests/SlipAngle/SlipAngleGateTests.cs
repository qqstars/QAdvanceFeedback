using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Slip Angle gating (docs\slip-angle-design.md 9.2; rulings R5, R7).</summary>
    public class SlipAngleGateTests
    {
        private const double Dt = 0.05;

        private static ITelemetryFrame F(double? brake = null, double? throttle = null, double? speed = 100.0,
            bool air = false, double? vg = null, double? groundSpeed = null) =>
            new TelemetryFrame(groundSpeedKmh: groundSpeed, speedKmh: speed, brakePercent: brake,
                throttlePercent: throttle, verticalG: vg, isAirborne: air);

        private static SlipAngleGateSettings S(TriggerBy t = TriggerBy.Any) => new SlipAngleGateSettings { TriggerBy = t };

        private static void Fly(SlipAngleGate g, double seconds, SlipAngleGateSettings s)
        {
            int n = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < n; i++) g.Step(F(air: true), Dt, s);
        }

        /// <summary>Seconds (after the touchdown frame) until InWobble ends.</summary>
        private static double WobbleSeconds(double flight)
        {
            var g = new SlipAngleGate(); var s = S();
            Fly(g, flight, s);
            var v = g.Step(F(), Dt, s); // touchdown
            if (!v.InWobble) return 0;
            double t = 0;
            while (v.InWobble && t < 5) { v = g.Step(F(), Dt, s); t += Dt; }
            return t;
        }

        [Fact]
        public void Defaults_are_pinned()
        {
            var s = new SlipAngleGateSettings();
            Assert.Equal(TriggerBy.Any, s.TriggerBy);
            Assert.Equal(5.0, s.BrakeThreshold);
            Assert.Equal(10.0, s.ThrottleThreshold);
            Assert.Equal(1.0, s.MaxFlightSeconds);   // owner swap 2026-11-19 (was 0.5)
            Assert.Equal(0.5, s.MaxWobbleSeconds);   // owner swap 2026-11-19 (was 1.0)
        }

        [Fact]
        public void Any_is_or_of_brake_and_throttle()
        {
            var g = new SlipAngleGate(); var s = S(TriggerBy.Any);
            Assert.True(g.Step(F(brake: 5), Dt, s).PedalOpen);
            Assert.True(g.Step(F(throttle: 10), Dt, s).PedalOpen);
            Assert.False(g.Step(F(brake: 4.9, throttle: 9.9), Dt, s).PedalOpen);
            Assert.False(g.Step(F(), Dt, s).PedalOpen); // null pedals read as 0
        }

        [Fact]
        public void Brake_only_ignores_throttle()
        {
            var g = new SlipAngleGate(); var s = S(TriggerBy.Brake);
            Assert.False(g.Step(F(throttle: 100), Dt, s).PedalOpen);
            Assert.True(g.Step(F(brake: 20), Dt, s).PedalOpen);
        }

        [Fact]
        public void Throttle_only_ignores_brake()
        {
            var g = new SlipAngleGate(); var s = S(TriggerBy.Throttle);
            Assert.False(g.Step(F(brake: 100), Dt, s).PedalOpen);
            Assert.True(g.Step(F(throttle: 20), Dt, s).PedalOpen);
        }

        [Fact]
        public void Speed_fade_10_to_20()
        {
            var g = new SlipAngleGate(); var s = S();
            Assert.Equal(0.0, g.Step(F(speed: 5), Dt, s).SpeedFade);
            Assert.Equal(0.0, g.Step(F(speed: 10), Dt, s).SpeedFade);
            Assert.Equal(0.5, g.Step(F(speed: 15), Dt, s).SpeedFade, 9);
            Assert.Equal(1.0, g.Step(F(speed: 20), Dt, s).SpeedFade);
            Assert.Equal(1.0, g.Step(F(speed: 80), Dt, s).SpeedFade);
        }

        [Fact]
        public void Speed_prefers_ground_speed_and_falls_back_and_learn_threshold_is_30()
        {
            var g = new SlipAngleGate(); var s = S();
            Assert.Equal(0.5, g.Step(F(speed: 100, groundSpeed: 15), Dt, s).SpeedFade, 9);
            Assert.False(g.Step(F(speed: 29.9), Dt, s).LearnSpeedOk);
            Assert.True(g.Step(F(speed: 30), Dt, s).LearnSpeedOk);
            var none = g.Step(F(speed: null), Dt, s);
            Assert.Equal(0.0, none.SpeedFade);
            Assert.False(none.LearnSpeedOk);
        }

        [Fact]
        public void Airborne_reports_fade_zero_and_no_wobble()
        {
            var g = new SlipAngleGate(); var s = S();
            var v = g.Step(F(air: true), Dt, s);
            Assert.True(v.Airborne);
            Assert.Equal(0.0, v.LandingFade);
            Assert.False(v.InWobble);
            Assert.False(v.LearningAllowed);
        }

        [Fact]
        public void Grounded_from_the_start_has_full_landing_fade()
        {
            var v = new SlipAngleGate().Step(F(), Dt, S());
            Assert.False(v.Airborne);
            Assert.Equal(1.0, v.LandingFade);
            Assert.False(v.InWobble);
            Assert.True(v.LearningAllowed);
        }

        [Fact]
        public void Wobble_scales_with_flight()
        {
            Assert.Equal(0.1, WobbleSeconds(0.2), 6);   // 0.2/1.0 * 0.5: 200 ms airborne -> 100 ms wobble
            Assert.Equal(0.5, WobbleSeconds(1.5), 6);   // capped at MaxWobble
            Assert.Equal(0.5, WobbleSeconds(1.0), 6);
        }

        [Fact]
        public void Landing_fade_is_s_curve_0_to_1()
        {
            var g = new SlipAngleGate(); var s = S();
            Fly(g, 1.0, s); // wobble = 0.5 s
            var v = g.Step(F(), Dt, s);
            Assert.True(v.InWobble);
            Assert.Equal(0.0, v.LandingFade, 9);
            Assert.False(v.LearningAllowed);
            double prev = v.LandingFade; double mid = double.NaN;
            for (int i = 1; i <= 10; i++)
            {
                v = g.Step(F(), Dt, s);
                Assert.True(v.LandingFade >= prev - 1e-12);
                prev = v.LandingFade;
                if (i == 5) mid = v.LandingFade;
                if (i < 10) { Assert.True(v.InWobble); Assert.False(v.LearningAllowed); }
            }
            Assert.Equal(0.5, mid, 9);  // smoothstep midpoint
            Assert.Equal(1.0, v.LandingFade, 9);
            Assert.False(v.InWobble);
            Assert.True(v.LearningAllowed);
            // smoothstep is flatter than linear near the start
            var g2 = new SlipAngleGate(); Fly(g2, 1.0, s); g2.Step(F(), Dt, s);
            Assert.True(g2.Step(F(), Dt, s).LandingFade < 0.05); // t=0.05 of 0.5 -> 0.028
        }

        [Fact]
        public void Takeoff_during_wobble_cancels_it_and_flight_restarts()
        {
            var g = new SlipAngleGate(); var s = S();
            Fly(g, 1.0, s);
            g.Step(F(), Dt, s); g.Step(F(), Dt, s);
            var v = g.Step(F(air: true), Dt, s); // new flight starts
            Assert.False(v.InWobble);
            Assert.Equal(0.0, v.LandingFade);
            for (int i = 0; i < 3; i++) g.Step(F(air: true), Dt, s);   // 0.2 s new flight in all
            v = g.Step(F(), Dt, s);              // 0.2/1.0 * 0.5 = 0.1 s wobble, not the old long one
            Assert.True(v.InWobble);
            int steps = 0;
            while (v.InWobble && steps < 100) { v = g.Step(F(), Dt, s); steps++; }
            Assert.Equal(2, steps); // 0.1 s at 50 ms
        }

        [Fact]
        public void Hop_over_0_5g_excludes_learning_200ms()
        {
            var g = new SlipAngleGate(); var s = S();
            Assert.False(g.Step(F(vg: 1.4), Dt, s).HopExcluded);     // within 0.5 g of 1 g
            var v = g.Step(F(vg: 1.6), Dt, s);
            Assert.True(v.HopExcluded);
            Assert.False(v.LearningAllowed);
            for (int i = 0; i < 3; i++) Assert.True(g.Step(F(vg: 1.0), Dt, s).HopExcluded); // t=50,100,150 ms
            Assert.False(g.Step(F(vg: 1.0), Dt, s).HopExcluded);                            // t=200 ms
            Assert.True(g.Step(F(vg: 0.4), Dt, s).HopExcluded);      // low side counts too
        }

        [Fact]
        public void Null_vertical_g_never_hop_excludes()
        {
            var g = new SlipAngleGate(); var s = S();
            for (int i = 0; i < 5; i++) Assert.False(g.Step(F(vg: null), Dt, s).HopExcluded);
        }

        [Fact]
        public void Airborne_resets_state_of_a_previous_landing()
        {
            var g = new SlipAngleGate(); var s = S();
            Fly(g, 0.8, s);
            Assert.True(g.Step(F(), Dt, s).InWobble);
            var v = g.Step(F(air: true), Dt, s);
            Assert.True(v.Airborne);
            Assert.Equal(0.0, v.LandingFade);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void Bad_dt_ages_by_nominal_sixtieth_and_never_throws(double dt)
        {
            var g = new SlipAngleGate(); var s = S();
            for (int i = 0; i < 6; i++) g.Step(F(air: true), dt, s);
            var v = g.Step(F(), dt, s);
            Assert.True(v.InWobble); // ~0.1 s flight -> wobble
            for (int i = 0; i < 200 && v.InWobble; i++) v = g.Step(F(), dt, s);
            Assert.False(v.InWobble);
        }

        [Fact]
        public void Null_settings_and_garbage_inputs_never_throw()
        {
            var g = new SlipAngleGate();
            g.Step(F(brake: double.NaN, throttle: double.NaN, speed: double.NaN, vg: double.NaN), Dt, null);
            g.Step(TelemetryFrame.Empty, Dt, new SlipAngleGateSettings { MaxFlightSeconds = 0, MaxWobbleSeconds = -1 });
            g.Step(null, Dt, null);
        }
    }
}
