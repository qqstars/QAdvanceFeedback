using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Slip Angle normalizer (docs\slip-angle-design.md 8).</summary>
    public class SlipAngleNormalizerTests
    {
        private static readonly SlipAngleWindow W = SlipAngleWindow.ShippedDefault;

        [Fact]
        public void Anchors_map_to_30_60_80_100()
        {
            Assert.Equal(30.0, SlipAngleNormalizer.Normalize(5.0, W), 6);
            Assert.Equal(60.0, SlipAngleNormalizer.Normalize(8.0, W), 6);
            Assert.Equal(80.0, SlipAngleNormalizer.Normalize(10.0, W), 6);
            Assert.Equal(100.0, SlipAngleNormalizer.Normalize(15.0, W), 6);
            Assert.Equal(100.0, SlipAngleNormalizer.Normalize(40.0, W), 6);
            Assert.Equal(0.0, SlipAngleNormalizer.Normalize(0.0, W), 6);
            Assert.Equal(0.0, SlipAngleNormalizer.Normalize(-3.0, W), 6);
        }

        [Fact]
        public void Normalize_is_monotone()
        {
            double prev = -1;
            for (double a = 0; a <= 20; a += 0.05)
            {
                double n = SlipAngleNormalizer.Normalize(a, W);
                Assert.True(n >= prev - 1e-9);
                prev = n;
            }
        }

        [Fact]
        public void Unclamped_extends_linearly()
        {
            Assert.Equal(120.0, SlipAngleNormalizer.NormalizeUnclamped(20.0, W), 6);
            Assert.Equal(100.0, SlipAngleNormalizer.NormalizeUnclamped(15.0, W), 6);
            Assert.Equal(60.0, SlipAngleNormalizer.NormalizeUnclamped(8.0, W), 6);
        }

        [Fact]
        public void Inverse_round_trip()
        {
            foreach (double a in new[] { 0.5, 2.0, 5.0, 6.5, 8.0, 9.0, 10.0, 12.0, 14.5 })
            {
                double n = SlipAngleNormalizer.Normalize(a, W);
                Assert.Equal(a, SlipAngleNormalizer.SourceFor(n, W), 3);
            }
            Assert.Equal(15.0, SlipAngleNormalizer.SourceFor(100.0, W), 6);
            Assert.Equal(15.0, SlipAngleNormalizer.SourceFor(130.0, W), 6);
            Assert.Equal(0.0, SlipAngleNormalizer.SourceFor(0.0, W), 6);
            Assert.Equal(0.0, SlipAngleNormalizer.SourceFor(double.NaN, W), 6);
            Assert.Equal(0.0, SlipAngleNormalizer.SourceFor(-5.0, W), 6);
        }
    
        [Theory]
        [InlineData(5.0, 2.0)]
        [InlineData(9.0, 4.0)]
        [InlineData(2.0, 1.0)]
        [InlineData(1.9, null)]
        [InlineData(1.5, null)]
        public void Start_is_floor_of_half_min_and_absent_below_2_degrees(double min, double? expected)
        {
            Assert.Equal(expected, new SlipAngleWindow(min, min + 3.0, min + 5.0).Start);
        }

        [Theory]
        [InlineData(5.0, 8.0, 10.0, 2.0)]
        [InlineData(9.0, 12.0, 15.0, 4.0)]
        public void N_at_Start_is_15(double min, double best, double max, double start)
        {
            var w = new SlipAngleWindow(min, best, max);
            Assert.Equal(15.0, SlipAngleNormalizer.Normalize(start, w), 6);
            Assert.Equal(15.0, SlipAngleNormalizer.NormalizeUnclamped(start, w), 6);
            Assert.Equal(30.0, SlipAngleNormalizer.Normalize(min, w), 6);
            Assert.Equal(start, SlipAngleNormalizer.SourceFor(15.0, w), 6);   // inverse through the new knot
        }

        [Fact]
        public void Min_below_2_omits_the_knot_and_stays_monotone()
        {
            var w = new SlipAngleWindow(1.5, 3.0, 5.0);
            Assert.Null(w.Start);
            Assert.Equal(30.0, SlipAngleNormalizer.Normalize(1.5, w), 6);
            double prev = -1;
            for (double a = 0; a <= 9; a += 0.02)
            {
                double n = SlipAngleNormalizer.Normalize(a, w);
                Assert.True(n >= prev - 1e-9);
                prev = n;
            }
        }

        [Fact]
        public void Inverse_round_trips_through_every_knot_and_between()
        {
            var w = new SlipAngleWindow(5.0, 8.0, 10.0);
            foreach (double a in new[] { 0.5, 1.0, 2.0, 3.5, 5.0, 6.5, 8.0, 9.0, 10.0, 12.0, 14.5 })
                Assert.Equal(a, SlipAngleNormalizer.SourceFor(SlipAngleNormalizer.Normalize(a, w), w), 4);
        }

        [Fact]
        public void Unclamped_extension_beyond_End_is_unchanged_with_the_start_knot()
        {
            Assert.Equal(120.0, SlipAngleNormalizer.NormalizeUnclamped(20.0, new SlipAngleWindow(5.0, 8.0, 10.0)), 6);
        }
}
}
