using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class MathHelpersTests
    {
        [Fact]
        public void Clamp_ports_WoteverMathExtensions_exactly()
        {
            Assert.Equal(5.0, MathHelpers.Clamp(5.0, 0.0, 10.0));
            Assert.Equal(0.0, MathHelpers.Clamp(-5.0, 0.0, 10.0));
            Assert.Equal(10.0, MathHelpers.Clamp(15.0, 0.0, 10.0));
        }

        [Fact]
        public void Map_unconstrained_extrapolates_beyond_output_range()
        {
            // x=15 is beyond in_max=10, unconstrained Map extrapolates past out_max.
            double result = MathHelpers.Map(15.0, 0.0, 10.0, 0.0, 100.0, constrain: false);
            Assert.Equal(150.0, result, 9);
        }

        [Fact]
        public void Map_constrained_clamps_to_output_range()
        {
            double result = MathHelpers.Map(15.0, 0.0, 10.0, 0.0, 100.0, constrain: true);
            Assert.Equal(100.0, result, 9);
        }

        [Fact]
        public void Offset4_below_min_is_zero_at_or_above_max_is_one()
        {
            Assert.Equal(0.0, MathHelpers.Offset(0.5, 2.0, 0.5, true), 9);
            Assert.Equal(0.0, MathHelpers.Offset(0.5, 2.0, 0.0, true), 9);
            Assert.Equal(1.0, MathHelpers.Offset(0.5, 2.0, 2.0, true), 9);
            Assert.Equal(1.0, MathHelpers.Offset(0.5, 2.0, 5.0, true), 9);
        }

        [Fact]
        public void Offset4_midpoint_is_linear_ratio()
        {
            // (1.0 - 0.5) / (2.0 - 0.5) = 0.5 / 1.5
            Assert.Equal(0.5 / 1.5, MathHelpers.Offset(0.5, 2.0, 1.0, true), 9);
        }

        [Fact]
        public void Offset5_can_exceed_one_when_clampMax_false()
        {
            // GetLockFromWheelSpeed/GetDirectSlip call THIS overload with clampMax:false - values
            // above max are NOT capped at 1, unlike the 4-arg overload.
            double result = MathHelpers.Offset(0.0, 1.0, 3.0, clampMin: true, clampMax: false);
            Assert.Equal(3.0, result, 9);
        }

        [Fact]
        public void Offset5_clampMin_still_floors_at_zero()
        {
            double result = MathHelpers.Offset(0.0, 1.0, -3.0, clampMin: true, clampMax: false);
            Assert.Equal(0.0, result, 9);
        }

        [Fact]
        public void Offset4_and_Offset5_agree_when_both_bools_match()
        {
            // Sanity: the two overloads must produce identical results for any (clamp, clamp) pair,
            // since the 4-arg overload is just "use the same bool for both ends" - if a call site ever
            // used the wrong overload with mismatched semantics this test would still pass, but at
            // least confirms the two implementations aren't accidentally divergent where they should
            // agree.
            Assert.Equal(
                MathHelpers.Offset(10.0, 20.0, 15.0, true),
                MathHelpers.Offset(10.0, 20.0, 15.0, true, true), 9);
            Assert.Equal(
                MathHelpers.Offset(10.0, 20.0, 25.0, false),
                MathHelpers.Offset(10.0, 20.0, 25.0, false, false), 9);
        }
    }
}
