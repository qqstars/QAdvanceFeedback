using System;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class AggregatorTests
    {
        private static readonly Aggregator PNorm2 = new Aggregator(GroupMode.PNorm, 2.0, Corners.Uniform(1.0));

        [Fact]
        public void Pair_of_equal_values_returns_that_value()
        {
            var wheels = new Corners(40.0, 40.0, 40.0, 40.0);
            Assert.Equal(40.0, PNorm2.Pair(Corners.FL, Corners.FR, wheels), 9);
        }

        [Fact]
        public void Pair_pnorm_matches_hand_computed_rms()
        {
            var wheels = new Corners(30.0, 40.0, 0.0, 0.0);
            // sqrt((30^2 + 40^2) / 2) = sqrt((900+1600)/2) = sqrt(1250) = 35.35533905932738
            double expected = Math.Sqrt((30.0 * 30.0 + 40.0 * 40.0) / 2.0);
            Assert.Equal(expected, PNorm2.Pair(Corners.FL, Corners.FR, wheels), 9);
        }

        [Fact]
        public void Quad_of_two_equal_pairs_matches_pair_of_one_pair()
        {
            // When FL=RL and FR=RR (two equal pairs - a shape any per-wheel engine can produce),
            // Quad's p-norm of the four values collapses to the Pair p-norm of one representative
            // pair. This is a general property of the p-norm aggregator, independent of any
            // particular algorithm's output.
            var wheels = new Corners(20.0, 60.0, 20.0, 60.0);
            double pair = PNorm2.Pair(Corners.FL, Corners.FR, wheels);
            double quad = PNorm2.Quad(wheels);
            Assert.Equal(pair, quad, 9);
        }

        [Fact]
        public void Max_mode_ignores_p_and_weights()
        {
            var max = new Aggregator(GroupMode.Max, 2.0, Corners.Uniform(1.0));
            var wheels = new Corners(10.0, 90.0, 5.0, 5.0);
            Assert.Equal(90.0, max.Quad(wheels), 9);
        }

        [Fact]
        public void Values_are_clamped_to_0_100_before_combining()
        {
            var wheels = new Corners(150.0, -20.0, 0.0, 0.0);
            // Clamped first: 100 and 0 -> pair pnorm = sqrt((100^2+0^2)/2) = sqrt(5000)
            double expected = Math.Sqrt(5000.0);
            Assert.Equal(expected, PNorm2.Pair(Corners.FL, Corners.FR, wheels), 9);
        }
    }
}
