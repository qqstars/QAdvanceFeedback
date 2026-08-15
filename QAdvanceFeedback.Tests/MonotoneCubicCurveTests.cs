using System;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Ported verbatim from the sibling ReliableWheelLockSlip project's
    /// ReliableWheelLockSlip.Tests/MonotoneCubicCurveTests.cs, per the brief ("port the tests too").
    /// Only the namespace changed.</summary>
    public class MonotoneCubicCurveTests
    {
        private static readonly double[] DefaultXs = { 0.0, 30.0, 60.0, 80.0, 100.0 };
        private static readonly double[] DefaultYs = { 0.0, 10.0, 30.0, 80.0, 100.0 };

        private static MonotoneCubicCurve Default() => new MonotoneCubicCurve(DefaultXs, DefaultYs);

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(30.0, 10.0)]
        [InlineData(60.0, 30.0)]
        [InlineData(80.0, 80.0)]
        [InlineData(100.0, 100.0)]
        public void Evaluate_interpolates_exactly_through_every_knot(double x, double expected)
            => Assert.Equal(expected, Default().Evaluate(x), 9);

        [Fact]
        public void Evaluate_clamps_below_first_and_above_last_knot()
        {
            var c = Default();
            Assert.Equal(0.0, c.Evaluate(-10.0));
            Assert.Equal(100.0, c.Evaluate(150.0));
        }

        [Fact]
        public void Evaluate_never_decreases_even_across_a_flat_segment()
        {
            var c = new MonotoneCubicCurve(
                new[] { 0.0, 30.0, 70.0, 80.0, 100.0 },
                new[] { 0.0, 30.0, 30.0, 80.0, 100.0 });
            AssertNeverDecreases(c);
        }

        [Fact]
        public void Evaluate_never_decreases_across_the_default_anchor_curve()
            => AssertNeverDecreases(Default());

        private static void AssertNeverDecreases(MonotoneCubicCurve c)
        {
            double previous = double.NegativeInfinity;
            for (double x = 0.0; x <= 100.0; x += 0.5)
            {
                double y = c.Evaluate(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}: {y} < {previous}");
                previous = y;
            }
        }

        [Fact]
        public void Evaluate_does_not_resurrect_a_zeroed_tangent_via_the_rescale()
        {
            var c = new MonotoneCubicCurve(
                new[] { 0.76, 9.443, 10.85, 12.674 },
                new[] { 42.452, 82.685, 12.38, 22.324 });

            double previous = double.NegativeInfinity;
            for (double x = 0.76; x <= 9.443; x += 0.01)
            {
                double y = c.Evaluate(x);
                Assert.True(y >= previous - 1e-9, $"decreased at x={x}: {y} < {previous}");
                previous = y;
            }
        }

        [Fact]
        public void Evaluate_is_smoother_than_the_equivalent_polyline()
        {
            var curve = Default();
            var polyline = new PiecewiseCurve(DefaultXs, DefaultYs);

            double maxCurveSecondDiff = MaxSecondDifference(x => curve.Evaluate(x));

            double h = 0.5;
            double maxPolylineKnotSecondDiff = 0.0;
            foreach (double knot in new[] { 30.0, 60.0, 80.0 })
            {
                double before = polyline.Evaluate(knot - h);
                double at = polyline.Evaluate(knot);
                double after = polyline.Evaluate(knot + h);
                double secondDiff = Math.Abs(after - 2 * at + before);
                if (secondDiff > maxPolylineKnotSecondDiff) maxPolylineKnotSecondDiff = secondDiff;
            }

            Assert.True(maxCurveSecondDiff < maxPolylineKnotSecondDiff,
                $"curve second-diff {maxCurveSecondDiff} was not smaller than polyline's {maxPolylineKnotSecondDiff}");
        }

        private static double MaxSecondDifference(Func<double, double> f)
        {
            double h = 0.5;
            double max = 0.0;
            for (double x = h; x <= 100.0 - h; x += h)
            {
                double secondDiff = Math.Abs(f(x + h) - 2 * f(x) + f(x - h));
                if (secondDiff > max) max = secondDiff;
            }
            return max;
        }

        [Fact]
        public void Evaluate_returns_first_y_for_nan()
            => Assert.Equal(0.0, Default().Evaluate(double.NaN));

        [Fact]
        public void Two_point_curve_is_a_straight_line()
        {
            var c = new MonotoneCubicCurve(new[] { 0.0, 10.0 }, new[] { 0.0, 20.0 });
            Assert.Equal(0.0, c.Evaluate(0.0), 9);
            Assert.Equal(10.0, c.Evaluate(5.0), 9);
            Assert.Equal(20.0, c.Evaluate(10.0), 9);
        }

        [Fact]
        public void Single_segment_curve_with_three_points_works()
        {
            var c = new MonotoneCubicCurve(new[] { 0.0, 5.0, 10.0 }, new[] { 0.0, 8.0, 10.0 });
            Assert.Equal(0.0, c.Evaluate(0.0), 9);
            Assert.Equal(8.0, c.Evaluate(5.0), 9);
            Assert.Equal(10.0, c.Evaluate(10.0), 9);
        }

        [Fact]
        public void Constructor_rejects_null_arrays()
        {
            Assert.Throws<ArgumentNullException>(() => new MonotoneCubicCurve(null, new[] { 0.0, 1.0 }));
            Assert.Throws<ArgumentNullException>(() => new MonotoneCubicCurve(new[] { 0.0, 1.0 }, null));
        }

        [Fact]
        public void Constructor_rejects_mismatched_lengths()
            => Assert.Throws<ArgumentException>(() =>
                new MonotoneCubicCurve(new[] { 0.0, 1.0, 2.0 }, new[] { 0.0, 1.0 }));

        [Fact]
        public void Constructor_rejects_fewer_than_two_points()
            => Assert.Throws<ArgumentException>(() =>
                new MonotoneCubicCurve(new[] { 0.0 }, new[] { 0.0 }));

        [Fact]
        public void Constructor_rejects_non_increasing_xs()
        {
            Assert.Throws<ArgumentException>(() =>
                new MonotoneCubicCurve(new[] { 0.0, 0.0 }, new[] { 0.0, 1.0 }));
            Assert.Throws<ArgumentException>(() =>
                new MonotoneCubicCurve(new[] { 0.0, 1.0, 0.5 }, new[] { 0.0, 1.0, 2.0 }));
        }

        [Fact]
        public void Evaluate_allocates_nothing()
        {
            var c = Default();
            c.Evaluate(45.0); // warm up JIT / any one-time setup

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10_000; i++)
                c.Evaluate(i % 100);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }
    }
}
