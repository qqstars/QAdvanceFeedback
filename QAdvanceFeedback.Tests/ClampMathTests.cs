using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class ClampMathTests
    {
        [Fact]
        public void To0100_clamps_both_ends()
        {
            Assert.Equal(0.0, ClampMath.To0100(-5.0));
            Assert.Equal(100.0, ClampMath.To0100(150.0));
            Assert.Equal(42.0, ClampMath.To0100(42.0));
        }

        [Fact]
        public void To0100_NaN_becomes_min()
        {
            Assert.Equal(0.0, ClampMath.To0100(double.NaN));
        }

        [Fact]
        public void To01_clamps_both_ends()
        {
            Assert.Equal(0.0, ClampMath.To01(-0.5));
            Assert.Equal(1.0, ClampMath.To01(1.5));
            Assert.Equal(0.25, ClampMath.To01(0.25));
        }

        // ------------------------------------------------------------------------------------
        // CoerceOrFallback - the pure core of WheelSourceResolver.Resolve's Plain-source path
        // (property present -> used; missing/unusable -> fallback; missing NEVER reads as zero).
        // This is what a Layer 4 wheel source field falls back to when a driver points it at a
        // SimHub property (e.g. a ShakeIt-exported one) that does not exist yet.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void CoerceOrFallback_null_raw_returns_fallback_not_zero()
        {
            // "null" is exactly what PluginManager.GetPropertyValue returns for a property that has
            // never been registered - e.g. the driver has not yet ticked ShakeIt's "Export output
            // value as a property" checkbox for this effect. Using a non-zero, distinctive fallback
            // proves this is never silently coerced to 0.
            Assert.Equal(37.5, ClampMath.CoerceOrFallback(null, 37.5));
        }

        [Fact]
        public void CoerceOrFallback_a_present_boxed_double_is_used_verbatim()
        {
            Assert.Equal(64.0, ClampMath.CoerceOrFallback((object)64.0, 37.5));
        }

        [Fact]
        public void CoerceOrFallback_a_present_boxed_int_converts_to_double()
        {
            Assert.Equal(12.0, ClampMath.CoerceOrFallback((object)12, 37.5));
        }

        [Fact]
        public void CoerceOrFallback_NaN_or_infinite_falls_back()
        {
            Assert.Equal(37.5, ClampMath.CoerceOrFallback((object)double.NaN, 37.5));
            Assert.Equal(37.5, ClampMath.CoerceOrFallback((object)double.PositiveInfinity, 37.5));
        }

        [Fact]
        public void CoerceOrFallback_an_unconvertible_type_falls_back_rather_than_throwing()
        {
            Assert.Equal(37.5, ClampMath.CoerceOrFallback(new object(), 37.5));
        }

        [Fact]
        public void CoerceOrFallback_a_convertible_string_still_works()
        {
            Assert.Equal(55.0, ClampMath.CoerceOrFallback((object)"55.0", 37.5));
        }
    }
}
