using System.Linq;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>Tests for <see cref="GForcePublishedNames"/> - the exact, verbatim published-name
    /// list (correctly spelled "Bottom", not the "Buttom" of the requirement text),
    /// analogous to PublishedPropertyNamesTests for Layers 1-3.</summary>
    public class GForcePublishedNamesTests
    {
        [Fact]
        public void Produces_exactly_8_names_Bottom_first_then_Back()
        {
            string[] names = GForcePublishedNames.AllNames().ToArray();
            Assert.Equal(8, names.Length);

            string[] expected =
            {
                "GForce.Bottom.FrontLeft", "GForce.Bottom.FrontRight",
                "GForce.Bottom.RearLeft", "GForce.Bottom.RearRight",
                "GForce.Back.LowLeft", "GForce.Back.LowRight",
                "GForce.Back.TopLeft", "GForce.Back.TopRight",
            };

            Assert.Equal(expected, names);
        }

        [Fact]
        public void Full_registered_name_matches_the_brief_exactly_once_prefixed_by_class_name()
        {
            // PluginManager.GetName(name, pluginType) = pluginType.Name + "." + name (decompiled and
            // confirmed for Layers 1-3 - see docs\layer123-report.md).
            string simulatedGetName(string name) => "QAdvanceFeedback" + "." + name;

            Assert.Equal("QAdvanceFeedback.GForce.Bottom.FrontLeft",
                simulatedGetName(GForcePublishedNames.BottomPrefix + "FrontLeft"));
            Assert.Equal("QAdvanceFeedback.GForce.Back.TopRight",
                simulatedGetName(GForcePublishedNames.BackPrefix + "TopRight"));
        }

        [Fact]
        public void Uses_the_correctly_spelled_Bottom_not_Buttom()
        {
            Assert.Contains("Bottom", GForcePublishedNames.BottomPrefix);
            Assert.DoesNotContain("Buttom", GForcePublishedNames.BottomPrefix);
        }
    }
}
