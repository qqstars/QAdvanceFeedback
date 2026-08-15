using System;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceShake"/> - the pure per-pad-pair oscillation math behind the
    /// owner-requested "Integrate Wheel Lock and Slip" G-force shake. The three worked examples are the
    /// owner's own, verbatim from docs\shake-and-toggle-report.md.
    /// <para/>
    /// NOTE on the owner's third worked example message (G=40, wheel=90, scale=1.5, "40 * 0.9 * 2.5 =
    /// 90"): that arithmetic uses 2.5 where the stated scale is 1.5 - a genuine inconsistency in the
    /// owner's own message, flagged rather than silently "corrected" to match. This suite implements
    /// the formula consistently as <c>G * (wheel/100) * scale</c> and tests the OWNER'S SEPARATELY
    /// CONFIRMED example 3 (G=60, wheel=90, scale=3.0 -&gt; band 162, capped 0..100, centre 50) instead.
    /// </summary>
    public class GForceShakeTests
    {
        private const double TwoPi = 2.0 * Math.PI;

        // At phase*frequency chosen so sin(2*pi*f*t) == 1.0 exactly (t = 1/(4f)), the wave sits at its
        // positive peak - i.e. output_L = centre + half, output_R = centre - half exactly. Using this
        // throughout makes the worked examples' "range" assertions exact rather than approximate.
        private static double PhaseAtPositivePeak(double frequencyHz) => 1.0 / (4.0 * frequencyHz);

        [Fact]
        public void Worked_example_1_G40_wheel30_scale1_band12_range34to46_centre40()
        {
            const double g = 40.0, wheel = 30.0, scale = 1.0, freq = 5.0;
            double contribution = (wheel / 100.0) * scale;

            GForceShake.Apply(g, contribution, freq, PhaseAtPositivePeak(freq), out double left, out double right);

            Assert.Equal(46.0, left, 6);   // centre(40) + half(6)
            Assert.Equal(34.0, right, 6);  // centre(40) - half(6)
            Assert.Equal(12.0, left - right, 6); // band width preserved
            Assert.Equal(40.0, (left + right) / 2.0, 6); // centre unchanged (no clamp needed)
        }

        [Fact]
        public void Worked_example_2_G80_wheel60_scale1_band48_shifted_to_52to100_centre76()
        {
            const double g = 80.0, wheel = 60.0, scale = 1.0, freq = 5.0;
            double contribution = (wheel / 100.0) * scale;

            GForceShake.Apply(g, contribution, freq, PhaseAtPositivePeak(freq), out double left, out double right);

            // Raw range would have been 56..104 (centre 80, half 24) - shifted DOWN so the top sits
            // exactly at 100, not squashed: shifted centre 76, range 52..100.
            Assert.Equal(100.0, left, 6);
            Assert.Equal(52.0, right, 6);
            Assert.Equal(48.0, left - right, 6); // band width PRESERVED, not squashed to <48
            Assert.Equal(76.0, (left + right) / 2.0, 6);
        }

        [Fact]
        public void Worked_example_3_G60_wheel90_scale3_band162_both_ends_out_capped_0to100_centre50()
        {
            const double g = 60.0, wheel = 90.0, scale = 3.0, freq = 5.0;
            double contribution = (wheel / 100.0) * scale; // 2.7 -> band 162, half 81 > 50

            GForceShake.Apply(g, contribution, freq, PhaseAtPositivePeak(freq), out double left, out double right);

            // Band (162) is wider than the whole 0-100 range - cannot be preserved by any shift, so the
            // owner's own exception applies: effective centre fixed at 50, output squashed to [0,100].
            Assert.Equal(100.0, left, 6);   // 50 + 81 clamped down to 100
            Assert.Equal(0.0, right, 6);    // 50 - 81 clamped up to 0
        }

        [Fact]
        public void Band_width_is_preserved_under_shifting_across_a_sweep_of_centres()
        {
            const double freq = 5.0;
            for (double centre = 0.0; centre <= 100.0; centre += 2.5)
            {
                const double contribution = 0.4; // band = centre*0.4, half = centre*0.2 <= 20 - never triggers the squash branch
                double phase = PhaseAtPositivePeak(freq);
                GForceShake.Apply(centre, contribution, freq, phase, out double left, out double right);

                double expectedBand = centre * contribution;
                Assert.Equal(expectedBand, left - right, 6);
                Assert.InRange(left, 0.0, 100.0);
                Assert.InRange(right, 0.0, 100.0);
            }
        }

        [Fact]
        public void MUTATION_a_clamp_by_squashing_instead_of_shifting_would_fail_example_2()
        {
            // Demonstrates what "squashing" (simple Clamp of the raw, unshifted output) would have
            // produced for example 2 - and that it is NOT what GForceShake.Apply actually returns,
            // proving the shift-based implementation is what is under test, not a squash.
            const double g = 80.0, wheel = 60.0, scale = 1.0, freq = 5.0;
            double contribution = (wheel / 100.0) * scale;
            double half = (g * contribution) / 2.0;

            double squashedLeft = Math.Min(100.0, g + half);   // 100 (squashed from 104)
            double squashedRight = Math.Max(0.0, g - half);    // 56 (unchanged, in range)
            double squashedBand = squashedLeft - squashedRight; // 44 - NARROWER than the true 48 band

            GForceShake.Apply(g, contribution, freq, PhaseAtPositivePeak(freq), out double left, out double right);

            Assert.Equal(48.0, left - right, 6); // the REAL implementation preserves the full 48 band...
            Assert.NotEqual(squashedBand, left - right, 6); // ...which a squash would NOT have (44 != 48)
        }

        [Fact]
        public void Zero_contribution_is_inert_output_equals_the_plain_gforce_value()
        {
            GForceShake.Apply(63.4, 0.0, 10.0, 12.345, out double left, out double right);
            Assert.Equal(63.4, left, 9);
            Assert.Equal(63.4, right, 9);
        }

        [Fact]
        public void Negative_or_non_finite_contribution_is_treated_as_zero_never_produces_NaN()
        {
            GForceShake.Apply(50.0, -1.0, 5.0, 1.0, out double leftNeg, out double rightNeg);
            Assert.Equal(50.0, leftNeg, 9);
            Assert.Equal(50.0, rightNeg, 9);

            GForceShake.Apply(50.0, double.NaN, 5.0, 1.0, out double leftNaN, out double rightNaN);
            Assert.Equal(50.0, leftNaN, 9);
            Assert.Equal(50.0, rightNaN, 9);
        }

        [Fact]
        public void Left_and_right_are_exact_mirrors_around_the_effective_centre_at_every_phase()
        {
            const double g = 55.0, contribution = 0.5, freq = 7.0;
            for (double t = 0.0; t < 1.0; t += 0.05)
            {
                GForceShake.Apply(g, contribution, freq, t, out double left, out double right);
                double half = (g * contribution) / 2.0;
                double effectiveCentre = (left + right) / 2.0;
                Assert.Equal(effectiveCentre + half * Math.Sin(TwoPi * freq * t), left, 6);
                Assert.Equal(effectiveCentre - half * Math.Sin(TwoPi * freq * t), right, 6);
            }
        }

        [Fact]
        public void Output_never_leaves_0_to_100_across_a_wide_sweep()
        {
            for (double g = 0.0; g <= 100.0; g += 10.0)
            for (double contribution = 0.0; contribution <= 4.0; contribution += 0.5)
            for (double t = 0.0; t < 1.0; t += 0.1)
            {
                GForceShake.Apply(g, contribution, 8.0, t, out double left, out double right);
                Assert.InRange(left, 0.0, 100.0);
                Assert.InRange(right, 0.0, 100.0);
            }
        }
    }
}
