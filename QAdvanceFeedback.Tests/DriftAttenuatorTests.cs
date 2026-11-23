using System;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="DriftAttenuator"/> / <see cref="DriftSettings"/> - spec
    /// docs\slip-angle-design.md §9.7 (drift state machine) and §8 (Nᵤ, the unclamped normalized value).
    /// </summary>
    public class DriftAttenuatorTests
    {
        private const double Dt = 0.01;

        private static double S(double t, double gamma)
        {
            double a = Math.Pow(t, gamma), b = Math.Pow(1.0 - t, gamma);
            return a / (a + b);
        }

        /// <summary>Feeds <paramref name="frames"/> identical frames; returns the last output.</summary>
        private static double Feed(DriftAttenuator d, DriftSettings s, double nU, double calc, int frames, double dt = Dt)
        {
            double o = double.NaN;
            for (int i = 0; i < frames; i++) o = d.Step(nU, calc, dt, s);
            return o;
        }

        /// <summary>Settings without smoothing/trend confirmation, so transitions land on exact frames.</summary>
        private static DriftSettings Crisp() => new DriftSettings { SmoothingSeconds = 0, TrendConfirmSeconds = 0 };

        [Fact]
        public void Defaults_match_spec_table()
        {
            var s = new DriftSettings();
            Assert.Equal(0.25, s.HoldSeconds);
            Assert.Equal(20, s.StabilityRange);
            Assert.Equal(40, s.ToPercent);
            Assert.Equal(0.12, s.DurationSeconds);
            Assert.Equal(2.0, s.Gamma);
            Assert.Equal(0.05, s.SmoothingSeconds);
            Assert.Equal(8, s.SwingMargin);
            Assert.Equal(0.08, s.TrendConfirmSeconds);
        }

        [Fact]
        public void Sanitise_enforces_ranges_and_repairs_non_finite()
        {
            var s = new DriftSettings
            {
                HoldSeconds = 0.0, StabilityRange = 100, ToPercent = -5, DurationSeconds = 9,
                Gamma = double.NaN, SmoothingSeconds = -1, SwingMargin = 0, TrendConfirmSeconds = double.PositiveInfinity,
            };
            s.Sanitise();
            Assert.Equal(0.05, s.HoldSeconds);
            Assert.Equal(60, s.StabilityRange);
            Assert.Equal(0, s.ToPercent);
            Assert.Equal(2, s.DurationSeconds);
            Assert.Equal(2.0, s.Gamma);          // NaN -> default
            Assert.Equal(0, s.SmoothingSeconds);
            Assert.Equal(1, s.SwingMargin);
            Assert.Equal(0.08, s.TrendConfirmSeconds); // non-finite -> default
        }

        [Fact]
        public void Normal_passthrough_below_80()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            for (int i = 0; i < 300; i++)
            {
                double n = 50 + 29 * Math.Sin(i * 0.1); // 21..79, never > 80
                double calc = 10 + i % 90;
                Assert.Equal(calc, d.Step(n, calc, Dt, s));
                Assert.Equal(DriftState.Normal, d.State);
                Assert.Equal(1.0, d.Factor);
            }
        }

        [Fact]
        public void Hold_then_s_ramp_to_40()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            // 0.25 s hold at 10 ms frames = 24 frames still Normal, 25th enters Drifting.
            for (int i = 0; i < 24; i++)
            {
                Assert.Equal(80, d.Step(90, 80, Dt, s), 9);
                Assert.Equal(DriftState.Normal, d.State);
            }
            Assert.Equal(80, d.Step(90, 80, Dt, s), 9);
            Assert.Equal(DriftState.Drifting, d.State);
            Assert.Equal(1.0, d.Factor, 9);

            for (int k = 1; k <= 12; k++)
            {
                double o = d.Step(90, 80, Dt, s);
                double t = Math.Min(1.0, k * Dt / 0.12);
                double expected = 1.0 - 0.6 * S(t, 2.0);
                Assert.Equal(expected, d.Factor, 6);
                Assert.Equal(expected * 80, o, 6);
            }
            Assert.Equal(0.40, d.Factor, 9);
            Assert.Equal(DriftState.Drifting, d.State);
            Assert.Equal(0.40 * 80, Feed(d, s, 90, 80, 100), 9); // stays at 40 %
        }

        [Fact]
        public void Owner_example_80_to_32()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            double o = Feed(d, s, 90, 80, 60); // hold 0.25 s + ramp 0.12 s, then some
            Assert.Equal(DriftState.Drifting, d.State);
            Assert.Equal(32, o, 9);
        }

        [Fact]
        public void Owner_example_release_at_factor_0_8_gives_64()
        {
            var d = new DriftAttenuator();
            // Duration chosen so that 100 ms into the default γ=2 S-ramp the factor is exactly 0.8:
            // 1 - 0.6·S(t) = 0.8  ->  S(t) = 1/3  ->  t = √2 - 1.
            var s = Crisp();
            s.DurationSeconds = 0.1 / (Math.Sqrt(2) - 1);
            Feed(d, s, 90, 80, 25);
            Assert.Equal(DriftState.Drifting, d.State);
            Feed(d, s, 90, 80, 9);              // 90 ms into the ramp
            double o = d.Step(70, 80, Dt, s);   // 100 ms: confirmed decrease (90 -> 70 ≥ 8 margin)
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(0.8, d.Factor, 6);
            Assert.Equal(64, o, 6);
            Assert.Equal(64, Feed(d, s, 70, 80, 20), 6); // frozen, not ramping on
        }

        private static void AssertSpinStaysFull(DriftAttenuator d, DriftSettings s, double start, double rate, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                double o = d.Step(start + rate * i * Dt, 70, Dt, s);
                Assert.Equal(DriftState.Normal, d.State);
                Assert.Equal(70, o, 9);
            }
        }

        [Theory]
        [InlineData(90)]
        [InlineData(100)]
        public void Escalation_over_stability_range_stays_full(double rate)
        {
            // A spin starting right at 81 on a FRESH instance (EMA just seeded): the raw rise
            // (0.24·rate per hold window > 20) must still count, even though the smoothed rise lags.
            AssertSpinStaysFull(new DriftAttenuator(), new DriftSettings(), 81, rate, 150);
        }

        [Theory]
        [InlineData(90)]
        [InlineData(100)]
        public void Escalation_right_after_reset_stays_full(double rate)
        {
            // Landing / airborne: the caller Resets, which re-seeds the EMA - a spin straight after must stay full.
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            Feed(d, s, 95, 80, 60);
            Assert.Equal(DriftState.Drifting, d.State);
            d.Reset();
            AssertSpinStaysFull(d, s, 81, rate, 150);
        }

        [Fact]
        public void Escalation_through_80_from_below_stays_full()
        {
            // Warmed EMA: Nᵤ climbing 100 pts/s straight through 80 (from 60).
            AssertSpinStaysFull(new DriftAttenuator(), new DriftSettings(), 60, 100, 200);
        }

        [Fact]
        public void Slow_stabilising_slide_detected_later()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            for (int i = 0; i < 70; i++) // 0.7 s escalating 60 -> 130 (0.5 s of it above 80)
            {
                d.Step(60 + 100 * i * Dt, 70, Dt, s);
                Assert.Equal(DriftState.Normal, d.State);
            }
            int framesToDetect = -1;
            for (int i = 0; i < 100; i++)
            {
                d.Step(130, 70, Dt, s);
                if (d.State == DriftState.Drifting) { framesToDetect = i + 1; break; }
            }
            // The window slides: once its rise fits in StabilityRange it's a drift - well under a second.
            Assert.InRange(framesToDetect, 1, 30);
        }

        [Fact]
        public void Decrease_freezes_factor_and_holds_the_locked_output()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            Feed(d, s, 95, 80, 60);
            Assert.Equal(0.4, d.Factor, 9);
            // Small wobbles inside SwingMargin never flip state.
            for (int i = 0; i < 60; i++) d.Step(95 + (i % 2 == 0 ? 3 : -3), 80, Dt, s);
            Assert.Equal(DriftState.Drifting, d.State);

            // Fall to 60: confirmed decrease after smoothing + 80 ms confirmation.
            Feed(d, s, 60, 80, 30);
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(0.4, d.Factor, 9);
            // Owner, 2026-11-20: the eased value (0.4 × 80 = 32) is locked; calc is never scaled again.
            foreach (double calc in new[] { 0.0, 10, 50, 100 })
            {
                double o = d.Step(60, calc, Dt, s);
                Assert.Equal(Math.Min(32, calc), o, 9);
                Assert.True(o <= calc);
            }

            // Release mid-ramp with ToPercent = 100 (factor 1): Min rule keeps output at calc, never above.
            var d2 = new DriftAttenuator();
            var s2 = Crisp();
            s2.ToPercent = 100;
            Feed(d2, s2, 90, 80, 40);
            Assert.Equal(DriftState.Drifting, d2.State);
            Assert.Equal(55, d2.Step(70, 55, Dt, s2), 9);
            Assert.Equal(DriftState.Recovering, d2.State);
        }

        [Fact]
        public void Increase_in_recovering_returns_calc_directly()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            Feed(d, s, 95, 80, 60);
            Feed(d, s, 60, 80, 40);
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(32, d.Step(62, 80, Dt, s), 9); // +2 above trough: still Recovering

            double o = Feed(d, s, 75, 80, 30);          // ≥ trough + 8, held 80 ms
            Assert.Equal(DriftState.Normal, d.State);
            Assert.Equal(80, o, 9);
            Assert.Equal(1.0, d.Factor);

            // Drift counting may restart.
            Feed(d, s, 95, 80, 60);
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Fact]
        public void Recovering_ends_below_30()
        {
            var d = new DriftAttenuator();
            var s = Crisp();
            Feed(d, s, 95, 80, 60);
            d.Step(50, 80, Dt, s);
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(32, d.Step(35, 40, Dt, s), 9);  // locked 32, calc 40 above it
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(20, d.Step(29, 20, Dt, s), 9);
            Assert.Equal(DriftState.Normal, d.State);
        }

        [Fact]
        public void Gravel_noise_does_not_chatter() => AssertGravelDoesNotChatter(12345);

        [Fact]
        public void Gravel_noise_does_not_chatter_across_20_seeds()
        {
            for (int seed = 1; seed <= 20; seed++) AssertGravelDoesNotChatter(seed);
        }

        private static void AssertGravelDoesNotChatter(int seed)
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            var rng = new Random(seed);
            var prev = d.State;
            int transitions = 0;
            for (int i = 0; i < 300; i++) // 5 s at 60 Hz
            {
                double n = 95 + (rng.NextDouble() * 12 - 6);
                d.Step(n, 80, 1.0 / 60, s);
                if (d.State != prev) { transitions++; prev = d.State; }
            }
            Assert.InRange(transitions, 0, 5);
            Assert.Equal(DriftState.Drifting, d.State); // the held gravel drift was recognised
        }

        [Fact]
        public void Zero_dt_safe()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            // dt 0 / negative / NaN / ∞ age by 1/60: 15 nominal frames = 0.25 s hold.
            double[] dts = { 0, -1, double.NaN, double.PositiveInfinity };
            for (int i = 0; i < 14; i++) d.Step(90, 80, dts[i % dts.Length], s);
            Assert.Equal(DriftState.Normal, d.State);
            d.Step(90, 80, 0, s);
            Assert.Equal(DriftState.Drifting, d.State);

            // Non-finite calc: output 0, no state change, never throw.
            Feed(d, s, 90, 80, 30, 0);
            var state = d.State; double factor = d.Factor;
            Assert.Equal(0, d.Step(90, double.PositiveInfinity, Dt, s));
            Assert.Equal(0, d.Step(double.NegativeInfinity, double.NaN, Dt, s));
            Assert.Equal(state, d.State);
            Assert.Equal(factor, d.Factor);

            // Null settings fall back to defaults.
            Assert.Equal(32, Feed(new DriftAttenuator(), null, 90, 80, 60), 9);
        }

        [Fact]
        public void Non_finite_nu_returns_current_state_output_without_state_change()
        {
            var d = new DriftAttenuator();
            var s = Crisp();
            Assert.Equal(55, d.Step(double.NaN, 55, Dt, s));            // Normal: calc
            Assert.Equal(DriftState.Normal, d.State);

            Feed(d, s, 90, 80, 24);
            Assert.Equal(55, d.Step(double.NaN, 55, Dt, s));            // timers did not advance...
            Assert.Equal(DriftState.Normal, d.State);
            d.Step(90, 80, Dt, s);                                       // ...so the 25th real frame enters
            Assert.Equal(DriftState.Drifting, d.State);

            Feed(d, s, 90, 80, 20);
            Assert.Equal(0.4 * 80, d.Step(double.PositiveInfinity, 50, Dt, s), 9); // Drifting: Min(factor × peak calc, calc)
            Assert.Equal(DriftState.Drifting, d.State);
            Assert.Equal(0.4, d.Factor, 9);

            d.Step(70, 80, Dt, s);
            Assert.Equal(DriftState.Recovering, d.State);
            Assert.Equal(0.4 * 80, d.Step(double.NaN, 50, Dt, s), 9);   // Recovering: Min(locked, calc)
            Assert.Equal(DriftState.Recovering, d.State);
        }

        [Fact]
        public void Stall_frame_dt_is_capped_and_cannot_satisfy_hold()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            d.Step(90, 80, 5.0, s);                 // a 5 s stall counts as 0.1 s
            Assert.Equal(DriftState.Normal, d.State);
            d.Step(90, 80, 5.0, s);                 // 0.2 s
            Assert.Equal(DriftState.Normal, d.State);
            d.Step(90, 80, 5.0, s);                 // 0.3 s ≥ 0.25 s hold
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Theory]
        [InlineData(12345)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Noisy_held_drift_still_attenuates_to_ToPercent(int seed)
        {
            // Washboard / rumble strip: a HELD drift (mean 100) with ±12 pts raw uniform noise every
            // 60 Hz frame. The raw frame-to-frame rise is ≥ 20 nearly every hold window; once the EMA
            // is warm (> 3τ since it was seeded) only the smoothed rise counts, so the drift is
            // recognised and the output eases to ToPercent.
            const double dt60 = 1.0 / 60.0;
            var rng = new Random(seed);
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            double Noise() => (rng.NextDouble() * 2 - 1) * 12;

            // Warm the EMA (0.5 s ≫ 3τ = 0.15 s) on the same rough surface below the drift threshold.
            for (int i = 0; i < 30; i++)
            {
                d.Step(55 + Noise(), 80, dt60, s);
                Assert.Equal(DriftState.Normal, d.State);
            }

            int framesToDrift = -1;
            for (int i = 0; i < 120 && framesToDrift < 0; i++) // 2 s budget
            {
                d.Step(100 + Noise(), 80, dt60, s);
                if (d.State == DriftState.Drifting) framesToDrift = i + 1;
            }
            Assert.True(framesToDrift > 0, "noisy held drift never entered Drifting");
            Assert.InRange(framesToDrift * dt60, 0.25, 0.5); // the hold plus the climb to the plateau

            double o = double.NaN;
            for (int i = 0; i < 12; i++) o = d.Step(100 + Noise(), 80, dt60, s); // 0.2 s > 0.12 s ramp
            Assert.NotEqual(DriftState.Normal, d.State);
            Assert.Equal(0.40, d.Factor, 9);
            Assert.Equal(0.40 * 80, o, 9);
        }

        [Fact]
        public void Warm_ema_ignores_raw_rise_but_fresh_ema_counts_it()
        {
            // A raw sawtooth whose smoothed rise is small: warm EMA → Drifting; with τ = 0 the raw
            // series IS the smoothed series, so the same rise is an escalation.
            var warm = new DriftAttenuator();
            var crisp = new DriftAttenuator();
            var s = new DriftSettings();
            var s0 = new DriftSettings { SmoothingSeconds = 0 };
            bool warmDrifted = false;
            for (int i = 0; i < 100; i++)
            {
                double n = 100 + (i % 2 == 0 ? -11 : 11);
                warm.Step(n, 80, Dt, s);
                crisp.Step(n, 80, Dt, s0);
                warmDrifted |= warm.State == DriftState.Drifting;
                Assert.Equal(DriftState.Normal, crisp.State);
            }
            Assert.True(warmDrifted);
        }

        [Fact]
        public void Short_hold_caps_stall_frame_to_half_the_hold()
        {
            // MinHoldSeconds 0.05 < MaxDt 0.1: dt is capped at min(MaxDt, Hold/2) = 0.025 s, so one
            // stall frame can never satisfy the hold on its own.
            var d = new DriftAttenuator();
            var s = new DriftSettings { HoldSeconds = DriftSettings.MinHoldSeconds };
            d.Step(90, 80, 5.0, s);                 // 0.025 s
            Assert.Equal(DriftState.Normal, d.State);
            d.Step(90, 80, 5.0, s);                 // 0.05 s ≥ hold
            Assert.Equal(DriftState.Drifting, d.State);
        }

        [Fact]
        public void Reset_returns_normal()
        {
            var d = new DriftAttenuator();
            var s = new DriftSettings();
            Feed(d, s, 95, 80, 60);
            Assert.Equal(DriftState.Drifting, d.State);
            d.Reset();
            Assert.Equal(DriftState.Normal, d.State);
            Assert.Equal(1.0, d.Factor);
            Assert.Equal(80, d.Step(95, 80, Dt, s));
            // Hold counting starts afresh after Reset (24 more frames still Normal).
            Feed(d, s, 95, 80, 23);
            Assert.Equal(DriftState.Normal, d.State);
            Feed(d, s, 95, 80, 1);
            Assert.Equal(DriftState.Drifting, d.State);
        }
    }
}
