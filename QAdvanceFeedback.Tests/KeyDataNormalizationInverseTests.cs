using System;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class KeyDataNormalizationInverseTests
    {
        public static readonly object[][] Cases =
        {
            new object[] { true, false, 75.0, 60.0, 45.0 },
            new object[] { true, false, null, null, null },
            new object[] { true, true, 75.0, 60.0, 45.0 },
            new object[] { true, true, null, null, null },
            new object[] { false, false, 75.0, 60.0, 45.0 },
            new object[] { false, false, null, null, null },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void Round_trip_is_exact_within_1e_6_for_1000_random_sources(bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)
        {
            var rng = new Random(12345);
            for (int i = 0; i < 1000; i++)
            {
                double source = rng.NextDouble() * 100.0;
                double n = KeyDataNormalization.Normalize(source, isLock, maxGripOnly, sMax, s90, s75);
                double back = KeyDataNormalizationInverse.SourceFor(n, isLock, maxGripOnly, sMax, s90, s75);
                double again = KeyDataNormalization.Normalize(back, isLock, maxGripOnly, sMax, s90, s75);
                Assert.True(Math.Abs(again - n) < 1e-6, $"source={source} n={n} back={back} again={again}");
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Saturated_target_returns_smallest_source_reaching_it(bool isLock, bool maxGripOnly, double? sMax, double? s90, double? s75)
        {
            double top = KeyDataNormalization.Normalize(100.0, isLock, maxGripOnly, sMax, s90, s75);
            Assert.Equal(100.0, KeyDataNormalizationInverse.SourceFor(top, isLock, maxGripOnly, sMax, s90, s75), 6);
            Assert.Equal(100.0, KeyDataNormalizationInverse.SourceFor(top + 50.0, isLock, maxGripOnly, sMax, s90, s75), 6);

            double s = KeyDataNormalizationInverse.SourceFor(top, isLock, maxGripOnly, sMax, s90, s75);
            Assert.True(KeyDataNormalization.Normalize(s, isLock, maxGripOnly, sMax, s90, s75) >= top - 1e-9);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        public void Non_finite_target_returns_zero(double target)
        {
            if (double.IsPositiveInfinity(target))
            {
                // +Inf is "at or above the top", so it saturates at 100 per the contract.
                Assert.Equal(100.0, KeyDataNormalizationInverse.SourceFor(target, true, false, null, null, null), 6);
                return;
            }
            Assert.Equal(0.0, KeyDataNormalizationInverse.SourceFor(target, true, false, null, null, null));
        }
    }
}
