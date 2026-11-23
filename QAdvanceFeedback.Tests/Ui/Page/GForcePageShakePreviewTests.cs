using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>The G-Force tab's preview data (old <c>RefreshShakePreview</c>, SettingsControl.GForceTest.cs):
    /// one second, 401 samples, from the engine's own <see cref="GForceShake.FeelingPair"/>.</summary>
    public class GForcePageShakePreviewTests
    {
        [Theory]
        [InlineData(ShakeFeeling.OppositePhase)]
        [InlineData(ShakeFeeling.SamePhase)]
        [InlineData(ShakeFeeling.Blending)]
        public void Traces_are_one_second_of_the_engine_feeling_pair(ShakeFeeling feeling)
        {
            GForcePageShakePreview p = GForcePageShakePreview.Build(7, 30, feeling);

            Assert.Equal(GForcePageShakePreview.Samples + 1, p.Left.Count);
            Assert.Equal(p.Left.Count, p.Right.Count);
            Assert.Equal(0.0, p.Left[0].X);
            Assert.Equal(1.0, p.Left[p.Left.Count - 1].X);
            double hold = GForceShake.EffectiveHold(0.30, feeling);
            for (int i = 0; i < p.Left.Count; i += 37)
            {
                GForceShake.FeelingPair(7, p.Left[i].X, hold, feeling, out double l, out double r);
                Assert.Equal(l, p.Left[i].Y);
                Assert.Equal(r, p.Right[i].Y);
            }
        }

        [Fact]
        public void Empty_boxes_draw_at_5_Hz_with_no_hold()
        {
            GForcePageShakePreview empty = GForcePageShakePreview.Build(null, null, ShakeFeeling.OppositePhase);
            GForcePageShakePreview explicitValues = GForcePageShakePreview.Build(5, 0, ShakeFeeling.OppositePhase);

            Assert.Equal(explicitValues.Left, empty.Left);
            Assert.Equal(explicitValues.Right, empty.Right);
        }
    }
}
