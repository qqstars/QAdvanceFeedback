using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// <see cref="GForceShake.SineHoldWave"/> - the v1.0.8 shape, replacing the trapezoid this file used
    /// to specify.
    /// <para/>
    /// ONE CYCLE, for a channel starting at its MAXIMUM, as a fraction of the period:
    /// <code>
    ///   [0,        h/4)             held at MAX
    ///   [h/4,      h/4 + (1-h)/2)   half a cosine, MAX -> MIN
    ///   [...,      ... + h/2)       held at MIN
    ///   [...,      ... + (1-h)/2)   half a cosine, MIN -> MAX
    ///   [1 - h/4,  1)               held at MAX
    /// </code>
    /// THE PERIOD IS ALWAYS 1/f. The holds are a share OF the cycle, never an addition to it, so the
    /// frequency setting keeps meaning what the owner pinned it to mean: one full Max-Min-Max travel of
    /// a single pad per 1/f, at every hold value.
    /// </summary>
    public class GForceShakeWaveTests
    {
        private const double Hz = 10.0;
        private const double Period = 1.0 / Hz;

        private static double W(double ms, double hold) => GForceShake.SineHoldWave(Hz, ms / 1000.0, hold);

        // ---------------- hold = 0 : a pure cosine ----------------

        [Fact]
        public void With_no_hold_the_wave_is_exactly_a_raised_cosine()
        {
            // The owner's own acceptance line: "If set Hold at min/max as 0, the two line will be
            // exactly the sin curve."
            for (int ms = 0; ms <= 100; ms++)
            {
                double expected = (1.0 + Math.Cos(2.0 * Math.PI * ms / 100.0)) / 2.0;
                Assert.Equal(expected, W(ms, 0.0), 9);
            }
        }

        [Fact]
        public void With_no_hold_there_are_no_flat_sections_at_all()
        {
            // Every consecutive pair differs except across the two turning points.
            int flat = 0;
            for (int ms = 0; ms < 100; ms++)
                if (Math.Abs(W(ms + 1, 0.0) - W(ms, 0.0)) < 1e-9) flat++;

            Assert.True(flat <= 2, $"a pure cosine should have no plateau, found {flat} flat steps");
        }

        // ---------------- hold > 0 : the owner's worked example ----------------

        [Theory]
        // 10 Hz, hold 30% -> max-hold 0..7.5ms, ramp to 42.5, min-hold to 57.5, ramp to 92.5, max-hold.
        [InlineData(0.0, 1.0)]
        [InlineData(5.0, 1.0)]      // still inside the opening MAX hold
        [InlineData(7.5, 1.0)]      // its last instant
        [InlineData(25.0, 0.5)]     // exactly half way down the first cosine
        [InlineData(42.5, 0.0)]     // bottom reached
        [InlineData(50.0, 0.0)]     // inside the MIN hold
        [InlineData(57.4, 0.0)]     // its last instant
        [InlineData(75.0, 0.5)]     // half way back up
        [InlineData(92.5, 1.0)]     // top reached
        [InlineData(99.0, 1.0)]     // closing MAX hold
        public void The_30_percent_hold_lands_on_the_specified_boundaries(double ms, double expected)
            => Assert.Equal(expected, W(ms, 0.30), 6);

        [Fact]
        public void The_hold_is_a_share_of_the_cycle_not_an_addition_to_it()
        {
            // THE RULE THAT SETTLED THE SPEC. Total time at an extreme must be exactly h of the period -
            // h/4 + h/4 at the maximum and h/2 at the minimum - so the period stays 1/f.
            const double hold = 0.30;
            const int samples = 100000;

            int atMax = 0, atMin = 0;
            for (int i = 0; i < samples; i++)
            {
                double v = GForceShake.SineHoldWave(Hz, Period * i / samples, hold);
                if (v >= 1.0 - 1e-9) atMax++;
                else if (v <= 1e-9) atMin++;
            }

            Assert.Equal(hold / 2.0, atMax / (double)samples, 3);
            Assert.Equal(hold / 2.0, atMin / (double)samples, 3);
        }

        [Fact]
        public void The_sine_portion_shrinks_by_exactly_the_hold()
        {
            // "if the hold on min/max is 30%, then it will be the sin curve shaped as 70ms loop".
            const double hold = 0.30;
            const int samples = 100000;

            int moving = 0;
            for (int i = 0; i < samples; i++)
            {
                double v = GForceShake.SineHoldWave(Hz, Period * i / samples, hold);
                if (v > 1e-9 && v < 1.0 - 1e-9) moving++;
            }

            Assert.Equal(1.0 - hold, moving / (double)samples, 3);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.15)]
        [InlineData(0.30)]
        [InlineData(0.50)]
        [InlineData(0.90)]
        public void Every_hold_keeps_one_full_travel_per_period(double hold)
        {
            // The frequency contract, across the whole hold range.
            var series = new List<double>();
            const int samples = 8000;                    // two seconds at 4 kHz
            for (int i = 0; i < samples; i++)
                series.Add(GForceShake.SineHoldWave(Hz, i / 4000.0, hold));

            double mean = 0.0;
            foreach (double v in series) mean += v;
            mean /= series.Count;

            int upward = 0;
            for (int i = 1; i < series.Count; i++)
                if (series[i - 1] <= mean && series[i] > mean) upward++;

            Assert.Equal(20, upward);                    // 10 Hz x 2 s
        }

        [Fact]
        public void The_wave_always_starts_at_its_maximum()
        {
            foreach (double hold in new[] { 0.0, 0.2, 0.5, 0.9 })
                Assert.Equal(1.0, GForceShake.SineHoldWave(Hz, 0.0, hold), 9);
        }

        [Fact]
        public void The_wave_stays_inside_0_and_1_for_every_hold()
        {
            foreach (double hold in new[] { 0.0, 0.3, 0.6, 0.9 })
                for (int i = 0; i < 2000; i++)
                {
                    double v = GForceShake.SineHoldWave(Hz, Period * i / 2000.0, hold);
                    Assert.InRange(v, 0.0, 1.0);
                }
        }

        [Fact]
        public void The_shape_is_symmetric_about_the_minimum()
        {
            // Down and up are mirror images, which is what makes the hold split h/4 + h/4 at the top
            // and h/2 at the bottom the only symmetric arrangement.
            const double hold = 0.30;
            for (int i = 1; i < 500; i++)
            {
                double offset = Period * i / 1000.0;
                double before = GForceShake.SineHoldWave(Hz, Period / 2.0 - offset, hold);
                double after = GForceShake.SineHoldWave(Hz, Period / 2.0 + offset, hold);
                Assert.Equal(before, after, 9);
            }
        }

        // ---------------- guards ----------------

        [Fact]
        public void The_hold_is_capped_so_the_ramps_never_vanish()
        {
            // At a hold of 1.0 the ramps would have zero duration and the wave would be an instantaneous
            // square - a discontinuity the hardware reads as a click. MaxSustainFraction bounds it.
            Assert.Equal(GForceShake.SineHoldWave(Hz, 0.037, GForceShake.MaxSustainFraction),
                         GForceShake.SineHoldWave(Hz, 0.037, 5.0), 9);
        }

        [Fact]
        public void Nonsense_inputs_do_not_throw_or_escape_the_range()
        {
            foreach (double f in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
                Assert.InRange(GForceShake.SineHoldWave(f, 0.01, 0.3), 0.0, 1.0);

            foreach (double phase in new[] { double.NaN, double.PositiveInfinity, -5.0 })
                Assert.InRange(GForceShake.SineHoldWave(Hz, phase, 0.3), 0.0, 1.0);

            foreach (double hold in new[] { double.NaN, -1.0, 99.0 })
                Assert.InRange(GForceShake.SineHoldWave(Hz, 0.01, hold), 0.0, 1.0);
        }
    }
}
