using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The ONE Slip Angle key-data display rule (owner, 2026-11-19: "make sure the Slip Angle also following
    /// the same rule similar to the Lock/Slip for the setting/displaying default value";
    /// docs\full-mvvm-plan.md 3.5): the exact persist key's published window when the learner (parameters
    /// file) holds that exact key, capped for its source; otherwise nothing, and the UI shows the source's
    /// default. Never another key's window.
    /// </summary>
    public class SlipAngleKeyDataDisplayRuleTests
    {
        private const string Native = SlipAngleFrameProcessor.NativeIdentity;
        private const string Fallback = SlipAngleFrameProcessor.FallbackIdentity;

        private static SlipAngleKeyState State(double min, double best, double max) => new SlipAngleKeyState
        {
            Learner = new SlipAngleLearnerState { PriorMin = min, PriorBest = best, PriorMax = max },
            PublishedMin = min, PublishedBest = best, PublishedMax = max, ColdTier = 1,
        };

        /// <summary>A learner restored from a parameters file holding exactly these All-target keys.</summary>
        private static KeyedSlipAngleLearner Restored(params (string game, string car, string id, SurfaceBucket bucket, SlipAngleWindow w)[] keys)
        {
            var states = new Dictionary<string, SlipAngleKeyState>();
            foreach (var k in keys)
                states[KeyedSlipAngleLearner.MakeKey(k.game, k.car, k.id, SlipAngleTarget.All, k.bucket)] = State(k.w.Min, k.w.Best, k.w.Max);
            var learner = new KeyedSlipAngleLearner();
            learner.Import(states);
            return learner;
        }

        private static void AssertWindow(SlipAngleWindow expected, SlipAngleWindow? actual)
        {
            Assert.True(actual.HasValue);
            Assert.Equal(expected.Min, actual.Value.Min, 9);
            Assert.Equal(expected.Best, actual.Value.Best, 9);
            Assert.Equal(expected.Max, actual.Value.Max, 9);
        }

        [Fact]
        public void Restart_with_the_exact_key_saved_shows_its_window()
        {
            var saved = new SlipAngleWindow(4.0, 6.5, 9.0);
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, saved));

            AssertWindow(saved, SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", Native));
        }

        [Fact]
        public void Another_car_or_game_is_unknown_and_shows_the_default()
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, new SlipAngleWindow(4.0, 6.5, 9.0)));

            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car2", Native));
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "ams2", "car1", Native));
            SlipAngleWindow shown = SlipAngleKeyDataModel.Shown(auto: true, manualSlot: null,
                SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car2", Native), Native);
            AssertWindow(SlipAngleWindow.ShippedDefault, shown);
        }

        [Fact]
        public void No_context_or_no_learner_is_unknown()
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, new SlipAngleWindow(4.0, 6.5, 9.0)));
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, null, "car1", Native));
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", null));
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(null, "acc", "car1", Native));
        }

        [Fact]
        public void Native_and_fallback_keys_are_never_cross_read()
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Fallback, SurfaceBucket.Sealed, new SlipAngleWindow(6.0, 9.0, 14.0)));

            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", Native));
            AssertWindow(new SlipAngleWindow(6.0, 9.0, 14.0), SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", Fallback));
        }

        [Theory]
        [InlineData(Native, 20.0)]
        [InlineData(Fallback, 30.0)]
        public void The_source_cap_is_applied(string identity, double cap)
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", identity, SurfaceBucket.Sealed, new SlipAngleWindow(20.0, 30.0, 40.0)));

            SlipAngleWindow? w = SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", identity);
            Assert.True(w.HasValue);
            Assert.Equal(cap, w.Value.Max, 9);
            Assert.True(w.Value.IsValid);
        }

        [Fact]
        public void The_default_is_capped_for_the_source()
        {
            AssertWindow(SlipAngleWindow.ShippedDefault, SlipAngleKeyDataModel.DefaultWindow(Native));
            AssertWindow(SlipAngleWindow.ShippedDefault, SlipAngleKeyDataModel.DefaultWindow(Fallback));
        }

        [Fact]
        public void A_surface_blend_uses_only_the_exact_keys_that_exist()
        {
            var sealedW = new SlipAngleWindow(4.0, 6.0, 8.0);
            var looseW = new SlipAngleWindow(6.0, 10.0, 14.0);
            KeyedSlipAngleLearner learner = Restored(
                ("RFactor2", "car1", Native, SurfaceBucket.Sealed, sealedW),
                ("RFactor2", "car1", Native, SurfaceBucket.Loose, looseW));
            var half = new SurfaceBlend(SurfaceBucket.Sealed, 0.5, 0.5, 0.0, SurfaceBucketSource.Unknown);

            // Both exact keys exist: the learner's own blend (what the engine applies).
            AssertWindow(new SlipAngleWindow(5.0, 8.0, 11.0), SlipAngleKeyDataModel.LearnedWindow(learner, "RFactor2", "car1", Native, half));

            // Only Sealed exists: the missing Low-grip bucket never contributes a borrowed or default window.
            var sealedAndLowGrip = new SurfaceBlend(SurfaceBucket.Sealed, 0.5, 0.0, 0.5, SurfaceBucketSource.Unknown);
            AssertWindow(sealedW, SlipAngleKeyDataModel.LearnedWindow(learner, "RFactor2", "car1", Native, sealedAndLowGrip));

            // No weighted bucket exists: unknown.
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "RFactor2", "car1", Native, SurfaceBlend.Only(SurfaceBucket.LowGrip)));
        }

        [Fact]
        public void The_menu_context_reads_the_sealed_key()
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Loose, new SlipAngleWindow(6.0, 10.0, 14.0)));
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", Native));
        }

        [Fact]
        public void A_key_known_only_by_borrowing_is_not_shown()
        {
            // car1 is learned and confirmed in session; car2 has no key of its own. The learner itself would
            // publish car1's window for car2 (Tier 2 borrowing) - the display rule never does.
            var learner = new KeyedSlipAngleLearner();
            SlipAngleWindow borrowed = learner.BucketWindow("acc", "car2", Native, SlipAngleTarget.All, SurfaceBucket.Sealed);
            Assert.Null(SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car2", Native));
            Assert.True(borrowed.IsValid);
        }

        // ---------------- what the key data boxes show (KeyDataPanelModel.Resolve's rule) ----------------

        [Fact]
        public void Auto_shows_the_learned_window_else_the_default()
        {
            var learned = new SlipAngleWindow(4.0, 6.5, 9.0);
            AssertWindow(learned, SlipAngleKeyDataModel.Shown(true, null, learned, Native));
            AssertWindow(learned, SlipAngleKeyDataModel.Shown(true, new SlipAngleWindow(3.0, 5.0, 7.0), learned, Native));
            AssertWindow(SlipAngleWindow.ShippedDefault, SlipAngleKeyDataModel.Shown(true, null, null, Native));
        }

        [Fact]
        public void Manual_shows_the_stored_slot_else_the_default_never_the_learned_window()
        {
            var learned = new SlipAngleWindow(4.0, 6.5, 9.0);
            var slot = new SlipAngleWindow(3.0, 5.0, 7.0);
            AssertWindow(slot, SlipAngleKeyDataModel.Shown(false, slot, learned, Native));
            AssertWindow(SlipAngleWindow.ShippedDefault, SlipAngleKeyDataModel.Shown(false, null, learned, Native));
        }

        // ---------------- the seed writes what was shown ----------------

        [Fact]
        public void The_seed_writes_exactly_the_shown_window()
        {
            var saved = new SlipAngleWindow(4.04, 6.46, 9.0);
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, saved));
            var keyData = new Settings.SlipAngleKeyDataSettings();
            keyData.SetAutoGenerate(Native, false);

            SlipAngleWindow? shown = SlipAngleKeyDataModel.LearnedWindow(learner, "acc", "car1", Native);
            Assert.True(SlipAngleKeyDataModel.TrySeed(keyData, "acc", Native, shown));

            Assert.True(keyData.TryGetSlot("acc", Native, out SlipAngleWindow seeded));
            Assert.Equal(System.Math.Round(shown.Value.Min, 1, System.MidpointRounding.AwayFromZero), seeded.Min, 9);
            Assert.Equal(System.Math.Round(shown.Value.Best, 1, System.MidpointRounding.AwayFromZero), seeded.Best, 9);
            Assert.Equal(shown.Value.Max, seeded.Max, 9);
        }

        [Fact]
        public void The_processor_seed_goes_through_the_display_rule_not_the_borrowed_window()
        {
            var saved = new SlipAngleWindow(4.0, 6.5, 9.0);
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, saved));
            var keyData = new Settings.SlipAngleKeyDataSettings();
            keyData.SetAutoGenerate(Native, false);
            var wrapped = new SlipAngleDisplayRuleKeyData(learner).Configure(keyData, "car1", SlipAngleKeyDataModel.MenuBlend);

            // The processor passes its own window (here a different one); the exact key's window is seeded.
            Assert.True(wrapped.TrySeed("acc", Native, new SlipAngleWindow(3.0, 5.0, 7.0)));
            Assert.True(keyData.TryGetSlot("acc", Native, out SlipAngleWindow seeded));
            AssertWindow(saved, seeded);
            Assert.False(wrapped.GetAutoGenerate(Native));
        }

        [Fact]
        public void The_processor_seed_is_refused_while_the_exact_key_is_unknown()
        {
            KeyedSlipAngleLearner learner = Restored(("acc", "car1", Native, SurfaceBucket.Sealed, new SlipAngleWindow(4.0, 6.5, 9.0)));
            var keyData = new Settings.SlipAngleKeyDataSettings();
            keyData.SetAutoGenerate(Native, false);
            var wrapped = new SlipAngleDisplayRuleKeyData(learner).Configure(keyData, "car2", SlipAngleKeyDataModel.MenuBlend);

            Assert.False(wrapped.TrySeed("acc", Native, SlipAngleWindow.ShippedDefault));
            Assert.False(keyData.IsSeeded("acc", Native));
        }

        [Fact]
        public void Nothing_known_seeds_nothing()
        {
            var keyData = new Settings.SlipAngleKeyDataSettings();
            keyData.SetAutoGenerate(Native, false);
            Assert.False(SlipAngleKeyDataModel.TrySeed(keyData, "acc", Native, null));
            Assert.False(keyData.TryGetSlot("acc", Native, out _));
        }
    }
}
