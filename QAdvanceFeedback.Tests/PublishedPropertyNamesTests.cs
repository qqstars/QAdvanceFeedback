using System.Linq;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class PublishedPropertyNamesTests
    {
        [Fact]
        public void Produces_exactly_18_names_lock_then_slip()
        {
            string[] names = PublishedPropertyNames.AllNames().ToArray();
            Assert.Equal(18, names.Length);

            string[] expected =
            {
                "WheelLock.Raw.FrontLeft", "WheelLock.Raw.FrontRight",
                "WheelLock.Raw.RearLeft", "WheelLock.Raw.RearRight",
                "WheelLock.Raw.Front", "WheelLock.Raw.Rear",
                "WheelLock.Raw.Left", "WheelLock.Raw.Right", "WheelLock.Raw.All",
                "WheelSlip.Raw.FrontLeft", "WheelSlip.Raw.FrontRight",
                "WheelSlip.Raw.RearLeft", "WheelSlip.Raw.RearRight",
                "WheelSlip.Raw.Front", "WheelSlip.Raw.Rear",
                "WheelSlip.Raw.Left", "WheelSlip.Raw.Right", "WheelSlip.Raw.All",
            };

            Assert.Equal(expected, names);
        }

        [Fact]
        public void Full_registered_name_matches_brief_exactly_once_prefixed_by_class_name()
        {
            // PluginManager.GetName(name, pluginType) = pluginType.Name + "." + name (confirmed by
            // decompiling SimHub.Plugins.dll) - simulate that here since Core has no SimHub
            // reference to call it directly.
            string simulatedGetName(string name) => "QAdvanceFeedback" + "." + name;

            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.All", simulatedGetName(PublishedPropertyNames.LockPrefix + "All"));
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.FrontLeft", simulatedGetName(PublishedPropertyNames.SlipPrefix + "FrontLeft"));
        }
    }
}
