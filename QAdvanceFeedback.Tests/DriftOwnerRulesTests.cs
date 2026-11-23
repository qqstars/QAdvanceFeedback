using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Owner rules of 2026-11-20 for the Slip Angle drift (docs\slip-angle-design.md §9.7): the locked
    /// eased output, the later drift start, and the throttle re-press clear.
    /// </summary>
    public class DriftOwnerRulesTests
    {
        private const double Dt = 0.01;

        private static DriftSettings Crisp() => new DriftSettings { SmoothingSeconds = 0, TrendConfirmSeconds = 0 };

        private static double Feed(DriftAttenuator d, DriftSettings s, double nU, double calc, int frames,
            double start = DriftAttenuator.DriftThreshold, double? throttle = null)
        {
            double o = double.NaN;
            for (int i = 0; i < frames; i++) o = d.Step(nU, calc, Dt, s, start, throttle);
            return o;
        }

        /// <summary>A crisp attenuator already in a held drift at calc 100 (eased to 40).</summary>
        private static DriftAttenuator Drifting(DriftSettings s)
        {
            var d = new DriftAttenuator();
            Assert.Equal(40, Feed(d, s, 95, 100, 60), 9);
            Assert.Equal(DriftState.Drifting, d.State);
            return d;
        }

        // ---------------- 1. locked output ----------------

        [Fact]
        public void Owner_example_100_eased_to_40_holds_40_while_calc_falls_then_follows_calc()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            Assert.Equal(40, d.Step(70, 90, Dt, s), 9);   // falling: Recovering, 40 locked (not 0.4 × 90 = 36)
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(40, d.Step(65, 60, Dt, s), 9);
            Assert.Equal(40, d.Step(60, 40, Dt, s), 9);
            Assert.Equal(30, d.Step(55, 30, Dt, s), 9);   // calc below 40: calc itself
            Assert.Equal(40, d.Step(55, 70, Dt, s), 9);   // calc up again inside Recovering: never above 40
        }

        [Fact]
        public void A_small_fall_inside_Drifting_is_not_scaled_again()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            Assert.Equal(40, d.Step(92, 70, Dt, s), 9);    // 3 points down: still Drifting, Min(0.4 × 100, 70)
            Assert.Equal(DriftState.Drifting, d.State);
            Assert.Equal(30, d.Step(92, 30, Dt, s), 9);    // calc below the cap: calc
        }

        [Fact]
        public void A_rise_inside_Drifting_raises_the_cap()
        {
            var d = new DriftAttenuator();
            var s = Crisp();
            Assert.Equal(32, Feed(d, s, 95, 80, 60), 9);
            Assert.Equal(36, d.Step(96, 90, Dt, s), 9);    // 0.4 × 90
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Fact]
        public void Release_mid_ramp_locks_the_ramp_value()
        {
            // Owner example kept: factor 0.8 at release with calc 80 -> 64, held while calc falls.
            var d = new DriftAttenuator();
            var s = Crisp();
            double t = 0;
            Feed(d, s, 90, 80, 25);
            while (d.Factor > 0.8 + 1e-3) { d.Step(90, 80, Dt, s); t += Dt; Assert.True(t < 1); }
            d.Step(70, 80, Dt, s);
            Assert.Equal(DriftState.Recovering, d.State);
            double locked = d.Factor * 80;                 // the ramp value frozen at the release frame
            Assert.InRange(locked, 0.4 * 80, 0.8 * 80 + 0.1);
            Assert.Equal(locked, d.Step(70, 70, Dt, s), 9);
            Assert.Equal(locked, d.Step(65, 50 + locked, Dt, s), 9);
        }

        // ---------------- 3. drift start ----------------

        [Theory]
        [InlineData(10, 15)]
        [InlineData(15, 22.5)]
        [InlineData(20, 30)]
        [InlineData(22, 30)]
        [InlineData(25, 30)]
        [InlineData(35, 42)]
        public void Drift_start_angle_is_max_of_120pct_and_min_of_30_and_150pct(double max, double expected) =>
            Assert.Equal(expected, SlipAngleDriftStart.AngleDeg(max), 9);

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        [InlineData(double.NaN)]
        public void Drift_start_angle_of_an_unusable_max_is_zero(double max) =>
            Assert.Equal(0, SlipAngleDriftStart.AngleDeg(max));

        [Fact]
        public void Drift_start_nu_is_End_for_a_small_max_and_between_80_and_100_for_a_large_one()
        {
            Assert.Equal(100, SlipAngleDriftStart.Nu(new SlipAngleWindow(8, 12, 15)), 9);   // 22.5° = End
            double large = SlipAngleDriftStart.Nu(new SlipAngleWindow(20, 30, 35));        // 42° < End 52.5°
            Assert.InRange(large, 80.0 + 1e-6, 100.0 - 1e-6);
            Assert.Equal(DriftAttenuator.DriftThreshold, SlipAngleDriftStart.Nu(new SlipAngleWindow(double.NaN, 1, 2)));
        }

        [Fact]
        public void Drift_counting_starts_only_above_the_given_start()
        {
            var s = Crisp();
            var d = new DriftAttenuator();
            Assert.Equal(80, Feed(d, s, 95, 80, 200, start: 100), 9);   // past Max, below the start: full
            Assert.Equal(DriftState.Normal, d.State);
            Feed(d, s, 101, 80, 40, start: 100);
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Fact]
        public void A_start_below_the_floor_means_the_floor_75()
        {
            // owner, 2026-11-20: a grip-ratio source may start just under Max, never below 75.
            var s = Crisp();
            var d = new DriftAttenuator();
            Feed(d, s, 74, 80, 100, 50);
            Assert.Equal(DriftState.Normal, d.State);
            Feed(d, s, 79, 80, 40, 50);
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.NegativeInfinity)]
        public void A_non_finite_start_means_80(double start)
        {
            var s = Crisp();
            var d = new DriftAttenuator();
            Feed(d, s, 79, 80, 100, start);
            Assert.Equal(DriftState.Normal, d.State);
            Feed(d, s, 85, 80, 40, start);
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Fact]
        public void Test_engine_holds_full_output_between_max_and_the_drift_start()
        {
            var ch = new SlipAngleChannelSettings();
            var w = new SlipAngleWindow(4, 7, 10);   // start 15° (End)
            var engine = new SlipAngleTestEngine(ch);
            SlipAngleProjectedResult r = null;
            for (int i = 0; i < 180; i++) r = engine.Step(13.0, w, 1.0 / 60);   // 130% of Max held 3 s
            Assert.Equal(DriftState.Normal, r.DriftState);
            Assert.Equal(r.PreDriftAll, r.PulseFreeAll, 9);
            for (int i = 0; i < 60; i++) r = engine.Step(16.0, w, 1.0 / 60);
            Assert.Equal(DriftState.Drifting, r.DriftState);
        }

        // ---------------- 2. throttle clear ----------------

        [Fact]
        public void Throttle_rise_over_the_reference_clears_the_flag()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            d.Step(70, 60, Dt, s, throttlePercent: 30);           // at/below Max: ref 30
            Assert.Equal(DriftState.Recovering, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 10);           // lift: ref 10
            Assert.Equal(40, d.Step(70, 60, Dt, s, throttlePercent: 30), 9);   // rise 20: not over 20
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(60, d.Step(70, 60, Dt, s, throttlePercent: 31), 9);   // over: full output at once
            Assert.Equal(DriftState.Normal, d.State);
            Assert.Equal(1.0, d.Factor);
        }

        [Fact]
        public void Throttle_at_the_level_clears_whatever_the_reference()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            d.Step(70, 60, Dt, s, throttlePercent: 85);           // ref 85
            Assert.Equal(DriftState.Recovering, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 90);           // +5 only, but at 90
            Assert.Equal(DriftState.Normal, d.State);
        }

        [Fact]
        public void Throttle_above_max_neither_sets_the_reference_nor_clears()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            Feed(d, s, 95, 100, 10, throttle: 100);               // still past Max: no clear
            Assert.Equal(DriftState.Drifting, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 50);           // first frame at/below Max: ref 50
            Assert.Equal(DriftState.Recovering, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 65);           // +15
            Assert.Equal(DriftState.Recovering, d.State);
        }

        [Fact]
        public void No_throttle_never_clears()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            Feed(d, s, 70, 60, 50, throttle: null);
            Assert.Equal(DriftState.Recovering, d.State);
            Feed(d, s, 70, 60, 5, throttle: double.NaN);
            Assert.Equal(DriftState.Recovering, d.State);
        }

        [Fact]
        public void The_reference_starts_fresh_for_the_next_drift()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            d.Step(70, 60, Dt, s, throttlePercent: 0);            // ref 0
            d.Step(70, 60, Dt, s, throttlePercent: 25);           // clears
            Assert.Equal(DriftState.Normal, d.State);
            Feed(d, s, 95, 100, 60, throttle: 60);                // a new drift (past Max: throttle ignored)
            Assert.Equal(DriftState.Drifting, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 50);           // new ref 50, not 0
            Assert.Equal(DriftState.Recovering, d.State);
        }

        [Fact]
        public void Configured_throttle_values_are_used()
        {
            var s = Crisp();
            s.ThrottleRiseClear = 40;
            s.ThrottleLevelClear = 100;
            DriftAttenuator d = Drifting(s);
            d.Step(70, 60, Dt, s, throttlePercent: 10);
            d.Step(70, 60, Dt, s, throttlePercent: 50);           // +40: not over 40
            Assert.Equal(DriftState.Recovering, d.State);
            d.Step(70, 60, Dt, s, throttlePercent: 95);           // +85, below level 100
            Assert.Equal(DriftState.Normal, d.State);
        }

        [Fact]
        public void Reset_drops_the_reference()
        {
            var s = Crisp();
            DriftAttenuator d = Drifting(s);
            d.Step(70, 60, Dt, s, throttlePercent: 0);            // ref 0
            d.Reset();
            Feed(d, s, 95, 100, 60);
            d.Step(70, 60, Dt, s, throttlePercent: 50);           // new ref 50
            d.Step(70, 60, Dt, s, throttlePercent: 60);
            Assert.Equal(DriftState.Recovering, d.State);
        }

        // ---------------- settings ----------------

        [Fact]
        public void Throttle_clear_defaults_and_ranges()
        {
            var s = new DriftSettings();
            Assert.Equal(20, s.ThrottleRiseClear);
            Assert.Equal(90, s.ThrottleLevelClear);
            s.ThrottleRiseClear = 1;
            s.ThrottleLevelClear = double.NaN;
            s.Sanitise();
            Assert.Equal(5, s.ThrottleRiseClear);
            Assert.Equal(90, s.ThrottleLevelClear);
            s.ThrottleRiseClear = 500;
            s.ThrottleLevelClear = 10;
            s.Sanitise();
            Assert.Equal(100, s.ThrottleRiseClear);
            Assert.Equal(50, s.ThrottleLevelClear);
        }
    }
}
