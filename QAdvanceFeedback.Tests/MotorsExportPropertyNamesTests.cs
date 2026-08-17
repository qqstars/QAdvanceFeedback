using QAdvanceFeedback.Core.MotorsExport;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Pins the exact ShakeIt Motors export property names this plugin's source-mode toggle
    /// checks for, so a typo here is caught immediately rather than only discovered against a live
    /// SimHub install (see docs\shakeit-export-guide.md for the full walkthrough).</summary>
    public class MotorsExportPropertyNamesTests
    {
        [Fact]
        public void Lock_wheel_names_match_the_confirmed_Motors_export_shape()
        {
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: true, "FrontLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: true, "FrontRight"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: true, "RearLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: true, "RearRight"));
        }

        [Fact]
        public void Slip_wheel_names_match_the_confirmed_Motors_export_shape()
        {
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: false, "FrontLeft"));
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight",
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel: false, "RearRight"));
        }

        [Fact]
        public void Wheel_suffixes_are_in_Corners_own_canonical_order()
        {
            Assert.Equal(new[] { "FrontLeft", "FrontRight", "RearLeft", "RearRight" }, MotorsExportPropertyNames.WheelSuffixes);
        }
    }
}
