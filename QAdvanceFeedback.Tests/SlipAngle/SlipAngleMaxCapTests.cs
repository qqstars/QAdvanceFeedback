using System.Collections.Generic;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>The Slip Angle learned-Max cap (owner, 2026-11-19): 20 degrees when the game reports tyre
    /// slip angle natively, 30 degrees for the car-angle fallback. Only Max is capped - the window keeps
    /// its shape (Min and Best scale with it) and End stays 1.5 x Max.</summary>
    public class SlipAngleMaxCapTests
    {
        [Fact]
        public void Caps_are_20_native_and_30_car_angle()
        {
            Assert.Equal(20.0, SlipAngleWindowLearner.MaxCapNativeDeg);
            Assert.Equal(30.0, SlipAngleWindowLearner.MaxCapCarAngleDeg);
            Assert.Equal(20.0, SlipAngleWindowLearner.MaxCapDegFor(SlipAngleFrameProcessor.NativeIdentity));
            Assert.Equal(30.0, SlipAngleWindowLearner.MaxCapDegFor(SlipAngleFrameProcessor.FallbackIdentity));
            // Custom: the driver's own per-wheel tyre slip angles - the native rule.
            Assert.Equal(20.0, SlipAngleWindowLearner.MaxCapDegFor("SlipAngle.Custom"));
        }

        [Fact]
        public void CappedAt_scales_the_whole_window_only_when_Max_is_above_the_cap()
        {
            SlipAngleWindow capped = new SlipAngleWindow(10.0, 16.0, 40.0).CappedAt(20.0);
            Assert.Equal(5.0, capped.Min, 9);
            Assert.Equal(8.0, capped.Best, 9);
            Assert.Equal(20.0, capped.Max, 9);
            Assert.Equal(30.0, capped.End, 9);

            SlipAngleWindow under = SlipAngleWindow.ShippedDefault.CappedAt(20.0);
            Assert.Equal(SlipAngleWindow.ShippedDefault.Max, under.Max);
            Assert.Equal(SlipAngleWindow.ShippedDefault.Best, under.Best);
        }

        [Fact]
        public void The_window_learner_caps_its_own_published_Max()
        {
            Assert.Equal(SlipAngleWindowLearner.MaxCeilingDeg, new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault).MaxCapDeg);

            var learner = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault, 20.0);
            // A state saved before the cap, with the prior raised 4x (Max 40).
            learner.Import(new SlipAngleLearnerState { PriorMin = 5.0, PriorBest = 8.0, PriorMax = 10.0, PriorScale = 4.0 });
            Assert.Equal(20.0, learner.Published.Max, 9);
            Assert.True(learner.Published.IsValid);
        }

        private static QAdvanceFeedback.Settings.SlipAngleKeyDataSettings ManualFor(string identity)
        {
            var k = new QAdvanceFeedback.Settings.SlipAngleKeyDataSettings();
            k.SetAutoGenerate(identity, false);
            return k;
        }

        [Theory]
        [InlineData(SlipAngleFrameProcessor.NativeIdentity, 25.0, 20.0)]
        [InlineData(SlipAngleFrameProcessor.FallbackIdentity, 35.0, 30.0)]
        [InlineData(SlipAngleFrameProcessor.FallbackIdentity, 28.0, 28.0)]
        public void A_typed_Max_is_clamped_to_the_source_cap(string identity, double typed, double stored)
        {
            var k = ManualFor(identity);
            Assert.True(k.TryCommitManual("g", identity, 5.0, 8.0, typed));
            Assert.True(k.TryGetSlot("g", identity, out SlipAngleWindow w));
            Assert.Equal(stored, w.Max, 6);
            Assert.Equal(8.0, w.Best, 6);
            Assert.Equal(5.0, w.Min, 6);
        }

        [Fact]
        public void A_typed_window_that_no_longer_fits_under_the_cap_is_refused()
        {
            var k = ManualFor(SlipAngleFrameProcessor.NativeIdentity);
            // Max clamps to 20, leaving Best 19.9 only 0.1 below it (the gap rule needs 0.2).
            Assert.False(k.TryCommitManual("g", SlipAngleFrameProcessor.NativeIdentity, 5.0, 19.9, 25.0));
        }

        [Fact]
        public void A_stored_window_above_the_cap_heals_on_read()
        {
            var k = ManualFor(SlipAngleFrameProcessor.NativeIdentity);
            Assert.True(k.TryCommitManual("g", SlipAngleFrameProcessor.NativeIdentity, 5.0, 8.0, 10.0));
            foreach (var e in k.Values.Values) { e.Min = 10.0; e.Best = 16.0; e.Max = 40.0; }   // a file from before the cap

            Assert.True(k.TryGetSlot("g", SlipAngleFrameProcessor.NativeIdentity, out SlipAngleWindow w));
            Assert.Equal(20.0, w.Max, 9);
            Assert.Equal(8.0, w.Best, 9);
        }

        [Theory]
        [InlineData(SlipAngleFrameProcessor.NativeIdentity, 20.0)]
        [InlineData(SlipAngleFrameProcessor.FallbackIdentity, 30.0)]
        public void A_published_window_never_exceeds_the_source_cap(string identity, double cap)
        {
            var learner = new KeyedSlipAngleLearner();
            string key = KeyedSlipAngleLearner.MakeKey("g", "c", identity, SlipAngleTarget.All, SurfaceBucket.Sealed);
            learner.Import(new Dictionary<string, SlipAngleKeyState>
            {
                [key] = new SlipAngleKeyState
                {
                    Learner = new SlipAngleLearnerState { PriorMin = 20.0, PriorBest = 30.0, PriorMax = 40.0 },
                    PublishedMin = 20.0, PublishedBest = 30.0, PublishedMax = 40.0, ColdTier = 1,
                },
            });

            SlipAngleWindow w = learner.BucketWindow("g", "c", identity, SlipAngleTarget.All, SurfaceBucket.Sealed);
            Assert.Equal(cap, w.Max, 9);
            Assert.True(w.IsValid);
        }
    }
}
