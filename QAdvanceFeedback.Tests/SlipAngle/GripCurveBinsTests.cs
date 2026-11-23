using System;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Grip curve bins (docs\slip-angle-design.md 9.5 steps 2-4, 9.5.5).</summary>
    public class GripCurveBinsTests
    {
        private const double PeakGLevel = 1.6;

        /// <summary>Magic-formula shape (B 0.14, C 1.7, E -1): smooth peak at ~7.7 deg, the 97% plateau
        /// spans ~6.0..9.9 deg (midpoint ~8.0) and 90% of peak is first reached at ~4.9 deg.</summary>
        private static double Pacejka(double alphaDeg)
        {
            const double b = 0.14, c = 1.7, e = -1.0;
            double x = b * alphaDeg;
            return PeakGLevel * Math.Sin(c * Math.Atan(x - e * (x - Math.Atan(x))));
        }

        /// <summary>Feeds a noisy curve over [fromDeg, toDeg): every 0.25 deg bin gets
        /// <paramref name="perBin"/> frames; 30% are sub-limit (60-95% of the curve), the rest sit on the
        /// curve with ~2% Gaussian noise. Fixed seed, so deterministic. <paramref name="transform"/>
        /// (bin index, pass k, g) -> g can poison or drop (NaN, ignored by Observe) frames without
        /// changing the random stream, so a variant is comparable frame-for-frame with the clean run.</summary>
        private static void FeedCurve(GripCurveBins bins, double fromDeg, double toDeg, int perBin = 200, int seed = 1234,
            Func<int, int, double, double> transform = null)
        {
            var rng = new Random(seed);
            for (int k = 0; k < perBin; k++)
                for (double a = fromDeg; a < toDeg - 1e-9; a += GripCurveBins.BinWidthDeg)
                {
                    double alpha = a + rng.NextDouble() * GripCurveBins.BinWidthDeg;
                    double g = Pacejka(alpha);
                    if (rng.NextDouble() < 0.3) g *= 0.6 + 0.35 * rng.NextDouble();
                    else g *= 1.0 + 0.02 * Gaussian(rng);
                    if (transform != null) g = transform(Bin(alpha), k, g);
                    bins.Observe(alpha, g);
                }
        }

        private static int Bin(double alphaDeg) => Math.Min((int)(alphaDeg / GripCurveBins.BinWidthDeg), GripCurveBins.BinCount - 1);

        private static (double best, double min) Window(GripCurveBins bins, double max = 10.0)
        {
            Assert.True(bins.TryBestAndMin(max, out double best, out double min));
            return (best, min);
        }

        private static GripCurveBins Curve(int perBin, Func<int, int, double, double> transform = null)
        {
            var bins = new GripCurveBins();
            FeedCurve(bins, 0.0, 15.0, perBin, transform: transform);
            return bins;
        }

        [Theory]
        [InlineData(20)]
        [InlineData(35)]
        [InlineData(50)]
        public void Best_and_min_are_accurate_at_20_to_50_samples_per_bin(int perBin)
        {
            (double best, double min) = Window(Curve(perBin));
            Assert.InRange(best, 7.5, 8.5);
            Assert.InRange(min, 4.5, 5.5);
        }

        [Theory]
        [InlineData(20, 2.0)]
        [InlineData(50, 2.0)]
        [InlineData(20, -8.0)]
        [InlineData(50, -8.0)]
        public void First_sample_outlier_in_every_bin_does_not_bias_the_window(int perBin, double poison)
        {
            // poison > 0: the first sample of every bin is that multiple of its true value; < 0: it is |poison| g.
            (double best0, double min0) = Window(Curve(perBin));
            (double best1, double min1) = Window(Curve(perBin,
                (bin, k, g) => k == 0 ? (poison > 0 ? g * poison : -poison) : g));
            Assert.InRange(best1, best0 - 0.3, best0 + 0.3);
            Assert.InRange(min1, min0 - 0.3, min0 + 0.3);
        }

        [Theory]
        [InlineData(20)]
        [InlineData(50)]
        public void Two_adjacent_poisoned_bins_near_the_peak_do_not_move_best(int perBin)
        {
            int b1 = Bin(9.1), b2 = Bin(9.35);
            (double best0, _) = Window(Curve(perBin));
            (double best1, _) = Window(Curve(perBin, (bin, k, g) => k == 0 && (bin == b1 || bin == b2) ? 8.0 : g));
            Assert.InRange(best1, best0 - 0.5, best0 + 0.5);
        }

        [Fact]
        public void A_single_under_populated_bin_inside_the_plateau_is_bridged()
        {
            int hole = Bin(8.1);
            (double best0, double min0) = Window(Curve(200));
            GripCurveBins holed = Curve(200, (bin, k, g) => bin == hole && k >= 10 ? double.NaN : g);
            Assert.Equal(10, holed.SampleCount(hole));
            (double best1, double min1) = Window(holed);
            Assert.InRange(best1, best0 - 0.3, best0 + 0.3);
            Assert.InRange(min1, min0 - 0.3, min0 + 0.3);
        }

        [Fact]
        public void Two_consecutive_under_populated_bins_end_the_plateau()
        {
            // Documents the rule: a 0.5 deg hole splits the plateau, so Best comes from one side only.
            int h1 = Bin(7.6), h2 = Bin(7.85);
            (double best0, _) = Window(Curve(200));
            (double best1, _) = Window(Curve(200, (bin, k, g) => (bin == h1 || bin == h2) && k >= 10 ? double.NaN : g));
            Assert.True(Math.Abs(best1 - best0) > 0.3, $"best0 {best0}, best1 {best1}");
        }

        private static double Gaussian(Random rng)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static GripCurveBins FullCurve()
        {
            var bins = new GripCurveBins();
            FeedCurve(bins, 0.0, 15.0);
            return bins;
        }

        [Fact]
        public void Pacejka_curve_peaking_at_8_gives_best_8_and_min_5_with_max_10()
        {
            GripCurveBins bins = FullCurve();
            Assert.True(bins.TryBestAndMin(10.0, out double best, out double min));
            Assert.InRange(best, 7.5, 8.5);
            Assert.InRange(min, 4.5, 5.5);
            Assert.InRange(bins.PeakG, PeakGLevel * 0.98, PeakGLevel * 1.06);
        }

        [Fact]
        public void Empty_bins_have_no_window()
        {
            var bins = new GripCurveBins();
            Assert.False(bins.TryBestAndMin(10.0, out _, out _));
            Assert.Equal(0.0, bins.PeakG);
            Assert.True(double.IsNaN(bins.ExpectedLatG(5.0)));
        }

        [Fact]
        public void Bins_need_20_samples_to_count()
        {
            var bins = new GripCurveBins();
            for (int i = 0; i < 19; i++) bins.Observe(8.1, 1.5);
            Assert.Equal(0.0, bins.PeakG);
            bins.Observe(8.1, 1.5);
            Assert.Equal(1.5, bins.PeakG, 6);
        }

        [Fact]
        public void Bins_ignore_alpha_above_max()
        {
            GripCurveBins bins = FullCurve();
            Assert.True(bins.TryBestAndMin(10.0, out double best0, out double min0));
            // Very high G past Max (12-14 deg): PeakG moves, but Best/Min (restricted to alpha <= Max) do not.
            for (int k = 0; k < 500; k++)
                for (double a = 12.0; a < 14.0; a += 0.25) bins.Observe(a + 0.1, 2.5);
            Assert.True(bins.PeakG > 2.0);
            Assert.True(bins.TryBestAndMin(10.0, out double best1, out double min1));
            Assert.Equal(best0, best1);
            Assert.Equal(min0, min1);
        }

        [Fact]
        public void Alpha_outside_0_to_30_and_bad_values_are_ignored()
        {
            var bins = new GripCurveBins();
            for (int i = 0; i < 100; i++)
            {
                bins.Observe(31.0, 2.0);
                bins.Observe(-1.0, 2.0);
                bins.Observe(double.NaN, 2.0);
                bins.Observe(5.0, double.PositiveInfinity);
                bins.Observe(5.0, -1.0);
                bins.Observe(double.PositiveInfinity, 1.0);
            }
            Assert.Equal(0.0, bins.PeakG);
            for (int b = 0; b < GripCurveBins.BinCount; b++) Assert.Equal(0, bins.SampleCount(b));
        }

        [Fact]
        public void Alpha_exactly_30_lands_in_the_top_bin()
        {
            var bins = new GripCurveBins();
            bins.Observe(30.0, 1.0);
            Assert.Equal(1, bins.SampleCount(GripCurveBins.BinCount - 1));
        }

        [Fact]
        public void Unvisited_bins_do_not_decay()
        {
            GripCurveBins bins = FullCurve();
            Assert.True(bins.TryBestAndMin(10.0, out double best0, out double min0));
            double peak0 = bins.PeakG;
            for (int i = 0; i < 10_000; i++) bins.Observe(2.1, 0.3);
            Assert.True(bins.TryBestAndMin(10.0, out double best1, out double min1));
            Assert.Equal(best0, best1);
            Assert.Equal(min0, min1);
            Assert.Equal(peak0, bins.PeakG);
        }

        [Fact]
        public void Drift_volume_does_not_outvote_limit()
        {
            GripCurveBins bins = FullCurve();
            (double best0, double min0) = Window(bins);
            // A long session of mostly sub-limit driving through the ALREADY-POPULATED bins on and
            // around the plateau (3-12 deg): 3x as many sub-limit frames (50-85% of the curve) as
            // at-limit frames, 2000 frames per bin (4x the estimator's ~500-observation memory).
            var rng = new Random(99);
            for (int k = 0; k < 2000; k++)
                for (double a = 3.0; a < 12.0 - 1e-9; a += GripCurveBins.BinWidthDeg)
                {
                    double alpha = a + rng.NextDouble() * GripCurveBins.BinWidthDeg;
                    double g = Pacejka(alpha);
                    g *= rng.NextDouble() < 0.75 ? 0.5 + 0.35 * rng.NextDouble() : 1.0 + 0.02 * Gaussian(rng);
                    bins.Observe(alpha, g);
                }
            (double best1, double min1) = Window(bins);
            Assert.InRange(best1, best0 - 0.3, best0 + 0.3);
            Assert.InRange(min1, min0 - 0.3, min0 + 0.3);
        }

        [Fact]
        public void Bin_p90_follows_its_own_observations_over_time()
        {
            var bins = new GripCurveBins();
            for (int i = 0; i < 2000; i++) bins.Observe(8.1, 1.0);
            Assert.Equal(1.0, bins.PeakG, 2);
            for (int i = 0; i < 5000; i++) bins.Observe(8.1, 1.5);
            Assert.InRange(bins.PeakG, 1.47, 1.53);
        }

        [Fact]
        public void Bin_p90_is_a_high_percentile_not_a_mean()
        {
            var bins = new GripCurveBins();
            var rng = new Random(7);
            // 80% drift-like low-G frames, 20% at the limit: P90 sits at the limit, not the mean.
            for (int i = 0; i < 5000; i++) bins.Observe(8.1, rng.NextDouble() < 0.8 ? 0.5 : 1.5);
            Assert.InRange(bins.PeakG, 1.3, 1.55);
        }

        [Fact]
        public void Rising_at_top_detected()
        {
            var rising = new GripCurveBins();
            FeedCurve(rising, 0.0, 5.0);
            Assert.True(rising.IsStillRisingAtTop);

            Assert.False(FullCurve().IsStillRisingAtTop);
            Assert.True(new GripCurveBins().IsStillRisingAtTop);
        }

        [Fact]
        public void Min_falls_back_to_five_eighths_of_best_without_a_rising_side()
        {
            var bins = new GripCurveBins();
            FeedCurve(bins, 6.0, 15.0);
            Assert.True(bins.TryBestAndMin(10.0, out double best, out double min));
            Assert.Equal(best * 5.0 / 8.0, min, 9);
        }

        [Fact]
        public void Gaps_of_at_least_0_2_are_enforced()
        {
            var bins = new GripCurveBins();
            // Flat curve 0..12: the plateau below Max 3 spans everything, Best would be ~1.5 deg.
            for (int k = 0; k < 50; k++)
                for (double a = 0.0; a < 12.0; a += 0.25) bins.Observe(a + 0.1, 1.2);
            Assert.True(bins.TryBestAndMin(3.0, out double best, out double min));
            Assert.True(best <= 3.0 - 0.2 + 1e-9);
            Assert.True(min <= best - 0.2 + 1e-9);
            Assert.True(min > 0);
            Assert.True(new SlipAngleWindow(min, best, 3.0).IsValid);

            Assert.False(bins.TryBestAndMin(0.3, out _, out _));
            Assert.False(bins.TryBestAndMin(double.NaN, out _, out _));
        }

        [Fact]
        public void Expected_lat_g_follows_the_curve()
        {
            GripCurveBins bins = FullCurve();
            Assert.InRange(bins.ExpectedLatG(8.0), Pacejka(8.0) * 0.97, Pacejka(8.0) * 1.07);
            Assert.InRange(bins.ExpectedLatG(4.0), Pacejka(4.0) * 0.95, Pacejka(4.0) * 1.08);
            // Beyond the populated range the last value holds.
            Assert.Equal(bins.ExpectedLatG(14.9), bins.ExpectedLatG(25.0), 9);
            Assert.True(double.IsNaN(bins.ExpectedLatG(double.NaN)));
            Assert.True(double.IsNaN(bins.ExpectedLatG(-1.0)));
        }

        [Fact]
        public void Sample_counts_saturate_at_the_cap()
        {
            var bins = new GripCurveBins();
            var state = bins.Export();
            state.Counts[10] = GripLearner.SampleCountSaturationCap;
            state.P90[10] = 1.0;
            bins.Import(state);
            bins.Observe(10 * GripCurveBins.BinWidthDeg + 0.1, 1.0);
            Assert.Equal(GripLearner.SampleCountSaturationCap, bins.SampleCount(10));
            Assert.Equal(1.0, bins.PeakG, 6);
        }

        [Fact]
        public void Export_import_round_trip_is_exact()
        {
            GripCurveBins a = FullCurve();
            for (int i = 0; i < 7; i++) a.Observe(20.1, 1.0 + 0.1 * i); // a bin still seeding
            GripCurveBinsState s = a.Export();
            var b = new GripCurveBins();
            b.Import(s);
            GripCurveBinsState s2 = b.Export();
            Assert.Equal(s.Counts, s2.Counts);
            Assert.Equal(s.P90, s2.P90);
            Assert.Equal(s.Spread, s2.Spread);
            Assert.Equal(s.Seed, s2.Seed);
            Assert.Equal(a.PeakG, b.PeakG);
            Assert.True(b.TryBestAndMin(10.0, out double best, out double min));
            a.TryBestAndMin(10.0, out double bestA, out double minA);
            Assert.Equal(bestA, best);
            Assert.Equal(minA, min);
            // Continuing identically after the round trip.
            a.Observe(7.1, 1.4); b.Observe(7.1, 1.4);
            Assert.Equal(a.Export().P90, b.Export().P90);
            // The seeding bin completes identically too.
            for (int i = 0; i < 13; i++) { a.Observe(20.1, 1.2); b.Observe(20.1, 1.2); }
            Assert.Equal(GripCurveBins.MinSamplesPerBin, b.SampleCount(Bin(20.1)));
            Assert.Equal(a.Export().P90, b.Export().P90);
            Assert.Equal(a.Export().Spread, b.Export().Spread);
            // Export is a copy.
            s.P90[30] = 99.0;
            Assert.NotEqual(99.0, a.Export().P90[30]);
        }

        [Fact]
        public void Import_of_malformed_state_resets_to_empty()
        {
            GripCurveBins bins = FullCurve();
            bins.Import(null);
            Assert.Equal(0.0, bins.PeakG);

            bins = FullCurve();
            bins.Import(new GripCurveBinsState { Counts = new int[3], P90 = new double[3], Spread = new double[3] });
            Assert.Equal(0.0, bins.PeakG);

            var s = new GripCurveBins().Export();
            s.Counts[5] = 50; s.P90[5] = double.NaN;
            s.Counts[6] = -4; s.P90[6] = 1.0;
            bins.Import(s);
            Assert.Equal(0, bins.SampleCount(5));
            Assert.Equal(0, bins.SampleCount(6));
            Assert.Equal(0.0, bins.PeakG);
        }
    }

    public class SlipAngleWindowTests
    {
        [Fact]
        public void Shipped_default_is_5_8_10_end_15()
        {
            SlipAngleWindow w = SlipAngleWindow.ShippedDefault;
            Assert.Equal(5.0, w.Min);
            Assert.Equal(8.0, w.Best);
            Assert.Equal(10.0, w.Max);
            Assert.Equal(15.0, w.End);
            Assert.True(w.IsValid);
        }

        [Theory]
        [InlineData(5.0, 8.0, 10.0, true)]
        [InlineData(1.0, 1.2, 1.4, true)]
        [InlineData(1.0, 1.1, 1.4, false)]
        [InlineData(1.0, 1.2, 1.3, false)]
        [InlineData(0.0, 1.2, 1.4, false)]
        [InlineData(-1.0, 1.2, 1.4, false)]
        [InlineData(8.0, 5.0, 10.0, false)]
        [InlineData(double.NaN, 8.0, 10.0, false)]
        [InlineData(5.0, 8.0, double.PositiveInfinity, false)]
        public void IsValid_requires_order_and_gaps(double min, double best, double max, bool valid)
        {
            Assert.Equal(valid, new SlipAngleWindow(min, best, max).IsValid);
        }

        [Fact]
        public void Default_struct_is_invalid()
        {
            Assert.False(default(SlipAngleWindow).IsValid);
        }
    }
}
