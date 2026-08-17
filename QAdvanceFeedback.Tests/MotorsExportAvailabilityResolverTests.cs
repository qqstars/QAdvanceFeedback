using System.Collections.Generic;
using QAdvanceFeedback.Core.MotorsExport;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="MotorsExportAvailabilityResolver"/> - the pure, SimHub-free "is the ShakeIt
    /// Motors export toggle usable right now" decision.
    /// </summary>
    public class MotorsExportAvailabilityResolverTests
    {
        private static Dictionary<string, object> LockValues() => new Dictionary<string, object>
        {
            ["ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft"] = 12.0,
            ["ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight"] = 34.0,
            ["ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft"] = 56.0,
            ["ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight"] = 78.0,
        };

        private static object Get(Dictionary<string, object> values, string name)
            => values.TryGetValue(name, out object v) ? v : null;

        [Fact]
        public void All_four_wheels_present_and_numeric_is_available()
        {
            var values = LockValues();
            Assert.True(MotorsExportAvailabilityResolver.IsAvailable(name => Get(values, name), isLockChannel: true));
        }

        [Fact]
        public void All_four_wheels_must_resolve_or_the_channel_is_unavailable()
        {
            var values = LockValues();
            values.Remove("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight"); // three of four

            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => Get(values, name), isLockChannel: true));
        }

        [Fact]
        public void A_null_reader_never_reports_available()
        {
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(null, isLockChannel: true));
        }

        [Fact]
        public void Missing_property_reads_as_null_not_zero_and_is_treated_as_unavailable()
        {
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => null, isLockChannel: true));
        }

        [Fact]
        public void Non_numeric_value_is_treated_as_unavailable_not_a_crash()
        {
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => "not a number", isLockChannel: true));
        }

        [Fact]
        public void NaN_or_infinite_value_is_treated_as_unavailable()
        {
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => double.NaN, isLockChannel: true));
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => double.PositiveInfinity, isLockChannel: true));
        }

        [Fact]
        public void A_reader_that_throws_is_treated_as_unavailable_not_propagated()
        {
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(
                name => throw new System.InvalidOperationException("simulated PluginManager failure"),
                isLockChannel: true));
        }

        [Fact]
        public void Lock_and_slip_are_resolved_independently()
        {
            var values = LockValues(); // only Lock's four names present
            Assert.True(MotorsExportAvailabilityResolver.IsAvailable(name => Get(values, name), isLockChannel: true));
            Assert.False(MotorsExportAvailabilityResolver.IsAvailable(name => Get(values, name), isLockChannel: false));
        }
    }
}
