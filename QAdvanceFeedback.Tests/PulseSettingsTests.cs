using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class PulseSettingsTests
    {
        [Fact]
        public void Fresh_settings_default_to_disabled_500ms_gap_and_min_50()
        {
            var s = new PulseSettings();
            Assert.False(s.Enabled);
            Assert.Equal(500.0, s.GapMs, 6);
            Assert.Equal(50.0, s.MinValue, 6);
        }

        // ------------------------------------------------------------------------------------
        // Mutation-evidence target (a): "allow a pulse gap below 200 ms" must fail a test - these
        // are that test. The floor is enforced in the MODEL (this setter), not only a UI spinner's
        // Minimum, per the brief's explicit instruction.
        // ------------------------------------------------------------------------------------
        [Theory]
        [InlineData(0.0)]
        [InlineData(1.0)]
        [InlineData(199.999)]
        [InlineData(-500.0)]
        public void Gap_below_200ms_is_floored_to_200ms_by_the_model_itself(double attempted)
        {
            var s = new PulseSettings { GapMs = attempted };
            Assert.Equal(PulseSettings.MinGapMs, s.GapMs, 9);
        }

        [Fact]
        public void Gap_at_or_above_200ms_is_accepted_unchanged()
        {
            var s = new PulseSettings { GapMs = 200.0 };
            Assert.Equal(200.0, s.GapMs, 9);

            s.GapMs = 750.0;
            Assert.Equal(750.0, s.GapMs, 9);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Non_finite_gap_is_floored_to_200ms_rather_than_accepted(double attempted)
        {
            var s = new PulseSettings { GapMs = attempted };
            Assert.Equal(PulseSettings.MinGapMs, s.GapMs, 9);
        }

        [Theory]
        [InlineData(-10.0, 0.0)]
        [InlineData(150.0, 100.0)]
        [InlineData(double.NaN, 0.0)]
        [InlineData(42.0, 42.0)]
        public void MinValue_is_clamped_to_0_100(double attempted, double expected)
        {
            var s = new PulseSettings { MinValue = attempted };
            Assert.Equal(expected, s.MinValue, 9);
        }
    }
}
