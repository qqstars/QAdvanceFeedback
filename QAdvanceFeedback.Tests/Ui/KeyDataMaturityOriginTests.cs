using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// Where a learned SMax comes from, for the indicator (owner, 2026-11-21): the highest of this car's own
    /// evidence, a value saved in an earlier session (50%), a borrowed reference (50% same car other surface, 30%
    /// another car in the game, 10% another game) and, for Slip, Wheel Lock's learning (30%) - with its tooltip.
    /// </summary>
    public class KeyDataMaturityOriginTests
    {
        private const string Game = "G";
        private const string Source = "S";

        private static void Learn(KeyedScaleLearner learner, string car, int samples = 200)
        {
            var rng = new Random(7);
            for (int i = 0; i < samples; i++) learner.ObserveAtPhysicalLimit(Game, car, Source, 70.0 + (rng.NextDouble() - 0.5) * 6.0);
        }

        private static KeyDataMaturity Maturity(NormalizedWheelLockSlipEngine engine, bool isLock, string car) =>
            KeyDataPanelModel.LearnedKeyDataMaturity(engine, isLock, Game, car, Source);

        [Fact]
        public void Nothing_learned_anywhere_is_zero_with_no_origin()
        {
            KeyDataMaturity m = Maturity(new NormalizedWheelLockSlipEngine(), true, "C1");
            Assert.Equal(0.0, m.SMax);
            Assert.Equal(KeyDataOrigin.None, m.Origin);
        }

        [Fact]
        public void A_car_with_its_own_evidence_reports_its_own_hand_over()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            Learn(engine.LockScaleLearner, "C1");
            KeyDataMaturity m = Maturity(engine, true, "C1");
            Assert.Equal(1.0, m.SMax, 6);
            Assert.Equal(KeyDataOrigin.Own, m.Origin);
        }

        [Fact]
        public void Another_car_in_the_same_game_borrows_at_30_percent()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            Learn(engine.LockScaleLearner, "C1");
            KeyDataMaturity m = Maturity(engine, true, "C2");
            Assert.Equal(KeyDataPanelModel.BorrowedCarShare, m.SMax, 6);
            Assert.Equal(KeyDataOrigin.BorrowedCar, m.Origin);
            Assert.True(m.Borrowed);       // nothing of its own yet: the LED's left half Red
            Assert.Equal(0.0, m.OwnSMax);
            Assert.Equal(0.0, m.S90);      // its own anchor ratios are still the seeds

            LearningIndicator cold = LearningIndicatorRules.From(m.SMax, true, null, m.Borrowed, m.OwnSMax);
            Assert.True(cold.IsBorrowed);                       // half: Red | Yellow
            Assert.Equal(LearningLevel.Red, cold.OwnLevel);
            Assert.Equal(LearningLevel.Yellow, cold.Level);

            // OWNER, 2026-11-22: borrowing is ONLY a cold start. Its first real data - own confidence still 0% - showed
            // a full Yellow LED labelled "borrowed"; from that data on it is its own learning: normal colour and label.
            engine.LockScaleLearner.ObserveAtPhysicalLimit(Game, "C2", Source, 70.0);
            KeyDataMaturity first = Maturity(engine, true, "C2");
            Assert.False(first.Borrowed);
            Assert.Equal(KeyDataOrigin.Own, first.Origin);
            LearningIndicator led = LearningIndicatorRules.From(first.SMax, true, LearningIndicatorRules.OriginToolTip(first.Origin), first.Borrowed, first.OwnSMax);
            Assert.False(led.IsBorrowed);
            Assert.Equal(LearningLevel.Orange, led.Level);   // learning has started: never "not learned yet"
            Assert.Equal(string.Format(Strings.Get("Learning.Indicator.Partial"), led.Percent), led.ToolTip);
        }

        [Fact]
        public void Learning_reloaded_after_a_restart_is_its_own_and_keeps_going_never_borrowed()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            Learn(engine.LockScaleLearner, "C1");               // another car fully learned: C2 could borrow from it
            Learn(engine.LockScaleLearner, "C2", samples: 40);  // C2 half-way
            KeyDataMaturity before = Maturity(engine, true, "C2");
            Assert.False(before.Borrowed);
            Assert.True(before.SMax < 1.0);

            var restarted = new NormalizedWheelLockSlipEngine();
            restarted.LockScaleLearner.ImportAll(engine.LockScaleLearner.ExportAll());
            restarted.LockScaleLearner.ImportEvidence(engine.LockScaleLearner.ExportEvidence());
            KeyDataMaturity after = Maturity(restarted, true, "C2");
            Assert.False(after.Borrowed);
            Assert.Equal(KeyDataOrigin.Own, after.Origin);
            Assert.Equal(before.SMax, after.SMax, 6);           // the same maturity as before the restart
        }

        [Fact]
        public void A_value_saved_in_an_earlier_session_without_evidence_reports_50_percent()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            engine.LockScaleLearner.LoadCold(Game, "C1", Source, 70.0);
            KeyDataMaturity m = Maturity(engine, true, "C1");
            Assert.Equal(KeyDataPanelModel.EarlierSessionShare, m.SMax, 6);
            Assert.Equal(KeyDataOrigin.EarlierSession, m.Origin);
            Assert.False(m.Borrowed);   // its own, from an earlier session - not borrowed
        }

        [Fact]
        public void Slip_leaning_on_wheel_lock_reports_30_percent_until_it_learns_itself()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            Learn(engine.LockScaleLearner, "C1");
            KeyDataMaturity slip = Maturity(engine, false, "C1");
            Assert.Equal(KeyDataPanelModel.FromLockShare, slip.SMax, 6);
            Assert.Equal(KeyDataOrigin.FromLock, slip.Origin);
            Assert.Equal(slip.SMax, slip.S90);   // Slip's S90 / S75 derive from SMax

            Learn(engine.SlipScaleLearner, "C1");
            Assert.Equal(KeyDataOrigin.Own, Maturity(engine, false, "C1").Origin);
        }

        [Fact]
        public void The_indicator_says_where_a_borrowed_value_comes_from()
        {
            var host = new FakePanelHost { Maturity = new KeyDataMaturity(0.3, 0.0, 0.0, KeyDataOrigin.BorrowedCar, borrowed: true) };
            var vm = new KeyDataViewModel(true, host, new ManualTicker(), new ImmediateDispatcher());
            vm.Refresh();
            Assert.Equal(LearningLevel.Yellow, vm.SMaxIndicator.Level);
            Assert.True(vm.SMaxIndicator.IsBorrowed);
            Assert.Equal(LearningLevel.Red, vm.SMaxIndicator.OwnLevel);   // left half Red: nothing of its own
            Assert.False(vm.S90Indicator.IsBorrowed);   // Lock's S90 is 0% and not borrowed
            Assert.Equal(Strings.Get("Learning.Indicator.Origin.BorrowedCar"), vm.SMaxIndicator.ToolTip);
            Assert.Equal(Strings.Get("Learning.Indicator.None"), vm.S90Indicator.ToolTip);   // Lock's S90 is not borrowed

            host.Maturity = new KeyDataMaturity(0.3, 0.3, 0.3, KeyDataOrigin.FromLock, borrowed: true);
            var slip = new KeyDataViewModel(false, host, new ManualTicker(), new ImmediateDispatcher());
            slip.Refresh();
            Assert.Equal(Strings.Get("Learning.Indicator.Origin.FromLock"), slip.S90Indicator.ToolTip);
            Assert.True(slip.S90Indicator.IsBorrowed);   // Slip's S90 / S75 follow SMax
            Assert.Equal(LearningLevel.Red, slip.SMaxIndicator.OwnLevel);      // no real data of its own
            Assert.Equal(LearningLevel.Yellow, slip.SMaxIndicator.Level);      // the borrowed 30%

            host.Maturity = new KeyDataMaturity(0.6, 0.6, 0.6, KeyDataOrigin.Own, borrowed: false, ownSMax: 0.6);   // its own wins
            slip.Refresh();
            Assert.False(slip.SMaxIndicator.IsBorrowed);
            Assert.Equal(slip.SMaxIndicator.Level, slip.SMaxIndicator.OwnLevel);
        }

        [Fact]
        public void A_zero_percent_indicator_is_never_drawn_half()
        {
            Assert.False(LearningIndicatorRules.From(0.0, true, null, true).IsBorrowed);
            Assert.True(LearningIndicatorRules.From(0.3, true, null, true).IsBorrowed);
            Assert.Equal(LearningLevel.Red, LearningIndicatorRules.From(0.3, true, null, true).OwnLevel);
            Assert.False(LearningIndicatorRules.From(0.3, false, null, true).IsBorrowed);   // hidden
        }

        [Fact]
        public void Every_origin_has_English_and_Chinese_text_without_spaces_in_the_Chinese()
        {
            var zh = StringTableZhHans.Create();
            foreach (string o in new[] { "EarlierSession", "BorrowedSurface", "BorrowedCar", "BorrowedGame", "FromLock" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Strings.Get("Learning.Indicator.Origin." + o)));
                Assert.DoesNotContain(" ", zh["Learning.Indicator.Origin." + o]);
            }
        }
    }
}
