using QAdvanceFeedback.Core.ShakeIt;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Verbatim property-name strings, confirmed by decompiling SimHub.Plugins.dll /
    /// GameReaderCommon.dll (see ShakeItPropertyNames' own remarks and docs\shakeit-export-guide.md) -
    /// this test pins the exact defaults this plugin ships against, so a typo here is caught
    /// immediately rather than only discovered against a live SimHub install.</summary>
    public class ShakeItPropertyNamesTests
    {
        [Fact]
        public void Lock_wheel_names_match_the_confirmed_ShakeIt_Motors_export_shape()
        {
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: true, "FrontLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: true, "FrontRight"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: true, "RearLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: true, "RearRight"));
        }

        [Fact]
        public void Slip_wheel_names_match_the_confirmed_ShakeIt_Motors_export_shape()
        {
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: false, "FrontLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight",
                ShakeItPropertyNames.GetWheelPropertyName(isLockChannel: false, "RearRight"));
        }

        [Fact]
        public void Wheel_suffixes_are_in_Corners_own_canonical_order()
        {
            Assert.Equal(new[] { "FrontLeft", "FrontRight", "RearLeft", "RearRight" }, ShakeItPropertyNames.WheelSuffixes);
        }
    }
}
