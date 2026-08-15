using System;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class PulseGeneratorTests
    {
        private static PulseSettings Settings(double gapMs, double minValue, bool enabled = true)
            => new PulseSettings { Enabled = enabled, GapMs = gapMs, MinValue = minValue };

        [Fact]
        public void Disabled_always_returns_the_plain_projected_value_even_at_100()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0, enabled: false));

            Assert.Equal(100.0, gen.Advance(0.0, 100.0), 9);
            Assert.Equal(100.0, gen.Advance(1.0, 100.0), 9); // still flat, no pulsing even a second later
        }

        [Fact]
        public void Below_100_always_returns_the_plain_projected_value()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            Assert.Equal(0.0, gen.Advance(0.016, 0.0), 9);
            Assert.Equal(62.5, gen.Advance(0.016, 62.5), 9);
            Assert.Equal(99.9, gen.Advance(0.016, 99.9), 9);
        }

        [Fact]
        public void The_first_frame_reaching_100_always_reads_exactly_100()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            Assert.Equal(100.0, gen.Advance(0.5, 100.0), 9); // even with a large dt on the very first frame
        }

        [Fact]
        public void One_full_move_100_to_min_takes_exactly_one_gap()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);                  // t=0    -> 100
            double atGap = gen.Advance(0.4, 100.0);   // t=400  -> min (one full "move")
            Assert.Equal(0.0, atGap, 6);
        }

        [Fact]
        public void A_full_cycle_is_twice_the_gap_100_to_min_to_100()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);                   // t=0
            gen.Advance(0.4, 100.0);                   // t=400 -> min
            double atFullCycle = gen.Advance(0.4, 100.0); // t=800 -> back to 100
            Assert.Equal(100.0, atFullCycle, 6);
        }

        [Fact]
        public void Waveform_holds_min_as_the_configured_MinValue_not_always_zero()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 35.0));
            gen.Advance(0.0, 100.0);
            double atGap = gen.Advance(0.4, 100.0);
            Assert.Equal(35.0, atGap, 6);
        }

        // ------------------------------------------------------------------------------------
        // Mutation-evidence target (b): "make the pulse transition linear instead of sinusoidal"
        // must fail a test - this is that test. At t = gap/4 a raised cosine and a linear ramp
        // disagree substantially (cosine: ~85.36% of the way from min to 100; linear: exactly 75%),
        // even though both agree at the endpoints (t=0, t=gap) and at the exact midpoint (t=gap/2,
        // where both happen to read the arithmetic mean) - so the assertion point is deliberately
        // NOT the midpoint.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Waveform_at_one_quarter_gap_matches_a_raised_cosine_not_a_linear_ramp()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);              // t=0
            double atQuarterGap = gen.Advance(0.1, 100.0); // t=100 = gap/4

            double cosineExpected = 50.0 * (1.0 + Math.Cos(Math.PI * 0.25)); // ~85.3553
            double linearWouldBe = 75.0;

            Assert.Equal(cosineExpected, atQuarterGap, 3);
            Assert.True(Math.Abs(atQuarterGap - linearWouldBe) > 5.0,
                $"value {atQuarterGap} was too close to the linear prediction {linearWouldBe} - waveform may not be sinusoidal");
        }

        [Fact]
        public void Dropping_below_100_mid_pulse_reverts_immediately_to_the_plain_value()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);          // t=0 -> 100
            gen.Advance(0.2, 100.0);          // t=200 -> mid-descent (not yet at min)
            Assert.True(gen.IsActive);

            double reverted = gen.Advance(0.016, 62.5); // input itself drops below 100 mid-pulse
            Assert.Equal(62.5, reverted, 9);
            Assert.False(gen.IsActive);
        }

        [Fact]
        public void Re_reaching_100_after_dropping_off_restarts_the_cycle_at_phase_zero()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);   // t=0
            gen.Advance(0.2, 100.0);   // t=200, mid-descent
            gen.Advance(0.016, 50.0);  // drops off - pulse stops

            double restarted = gen.Advance(0.016, 100.0); // reaches 100 again
            Assert.Equal(100.0, restarted, 9); // fresh phase 0, not wherever the old cycle left off
        }

        [Fact]
        public void Reset_clears_an_in_progress_pulse()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);
            gen.Advance(0.2, 100.0);
            Assert.True(gen.IsActive);

            gen.Reset();
            Assert.False(gen.IsActive);

            double restarted = gen.Advance(0.016, 100.0);
            Assert.Equal(100.0, restarted, 9);
        }

        [Fact]
        public void Constructor_rejects_null_settings()
        {
            Assert.Throws<ArgumentNullException>(() => new PulseGenerator(null));
        }

        [Fact]
        public void Non_positive_or_non_finite_dt_does_not_advance_the_phase()
        {
            var gen = new PulseGenerator(Settings(gapMs: 400, minValue: 0));
            gen.Advance(0.0, 100.0);
            double stillAtStart = gen.Advance(0.0, 100.0); // dt=0 -> no progress
            Assert.Equal(100.0, stillAtStart, 9);

            double stillAtStart2 = gen.Advance(double.NaN, 100.0);
            Assert.Equal(100.0, stillAtStart2, 9);
        }
    }
}
