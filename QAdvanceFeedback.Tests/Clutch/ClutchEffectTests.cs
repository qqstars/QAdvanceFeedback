using System.IO;
using QAdvanceFeedback.Core.ShakeItEffects;
using Xunit;

namespace QAdvanceFeedback.Tests.Clutch
{
    /// <summary>The native "QAdvanceFeedback Clutch Bite" ShakeIt effect (docs\clutch-bite-design.md).</summary>
    public class ClutchEffectTests
    {
        [Fact]
        public void Clutch_tone_defaults_are_40_low_15_high()
        {
            ToneDefault d = ToneDefaults.Clutch;
            Assert.Equal(40, d.LowHz);
            Assert.True(d.UseHigh);
            Assert.Equal(15, d.HighHz);
            Assert.False(d.UseNoise);
        }

        [Theory]
        [InlineData(TestKind.Clutch, TestKind.Clutch, true)]
        [InlineData(TestKind.Clutch, TestKind.Lock, false)]
        [InlineData(TestKind.Clutch, TestKind.GForce, false)]
        [InlineData(TestKind.Slip, TestKind.Clutch, false)]
        public void Clutch_kind_shares_outputs_only_with_itself(TestKind a, TestKind b, bool shared) =>
            Assert.Equal(shared, TestEffectSession.SharesOutputs(a, b));

        [Fact]
        public void Native_effect_plays_only_the_published_clutch_snapshot_behind_the_test_aware_gate()
        {
            string code = File.ReadAllText(PluginFile("Integration", "ShakeIt", "QAdvanceFeedbackClutchBiteContainer.cs"));
            Assert.Contains("public sealed class QAdvanceFeedbackClutchBiteContainer", code);
            Assert.Contains("LatestClutch", code);
            Assert.Contains("TestAwareOutputGate.Allows(", code);
            Assert.Contains("TestClutchActive", code);
            Assert.Contains("ToneDefaults.Clutch", code);
            Assert.Contains("public override bool IsAllowedOutOfGame => true;", code);
        }

        private static string PluginFile(params string[] parts)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "QAdvanceFeedback", "QAdvanceFeedback.cs"))) dir = dir.Parent;
            return Path.Combine(Path.Combine(dir.FullName, "QAdvanceFeedback"), Path.Combine(parts));
        }
    }
}
