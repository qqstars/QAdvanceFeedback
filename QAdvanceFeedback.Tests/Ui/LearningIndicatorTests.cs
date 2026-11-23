using System.Collections.Generic;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Tests.Ui.Page;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The learning maturity indicators (owner, 2026-11-21; docs\learning-maturity-indicator-report.md): the
    /// rate -> colour / percent / tooltip rule, and the four key-data view models showing them under Auto only,
    /// collapsed under Manual and on hidden rows, re-read at once on an Auto toggle or a game change.
    /// </summary>
    public class LearningIndicatorTests
    {
        // ---------------- the rule ----------------

        [Theory]
        [InlineData(null, 0)]
        [InlineData(double.NaN, 0)]
        [InlineData(-0.5, 0)]
        [InlineData(0.0, 0)]
        [InlineData(0.001, 1)]
        [InlineData(0.2499, 24)]
        [InlineData(0.25, 25)]
        [InlineData(0.29, 29)]
        [InlineData(0.5, 50)]
        [InlineData(0.75, 75)]
        [InlineData(0.996, 99)]
        [InlineData(1.0, 100)]
        [InlineData(1.7, 100)]
        public void A_rate_shows_its_whole_percent_and_a_partial_rate_never_reads_0_or_100(double? rate, int percent)
        {
            Assert.Equal(percent, LearningIndicatorRules.Percent(rate));
        }

        [Theory]
        [InlineData(0, LearningLevel.Red)]
        [InlineData(1, LearningLevel.Orange)]
        [InlineData(24, LearningLevel.Orange)]
        [InlineData(25, LearningLevel.Yellow)]
        [InlineData(49, LearningLevel.Yellow)]
        [InlineData(50, LearningLevel.Azure)]
        [InlineData(74, LearningLevel.Azure)]
        [InlineData(75, LearningLevel.Blue)]
        [InlineData(99, LearningLevel.Blue)]
        [InlineData(100, LearningLevel.Green)]
        public void Colours_follow_the_owners_bands_red_only_at_zero_and_each_boundary_in_the_higher_colour(int percent, LearningLevel level)
        {
            Assert.Equal(level, LearningIndicatorRules.LevelFor(percent));
        }

        [Fact]
        public void Not_learned_partial_and_fully_learned_have_their_own_tooltips()
        {
            Assert.Equal(Strings.Get("Learning.Indicator.None"), LearningIndicatorRules.From(0.0, true).ToolTip);
            Assert.Equal(string.Format(Strings.Get("Learning.Indicator.Partial"), 63), LearningIndicatorRules.From(0.634, true).ToolTip);
            Assert.Equal(Strings.Get("Learning.Indicator.Full"), LearningIndicatorRules.From(1.0, true).ToolTip);
            Assert.Contains("63%", LearningIndicatorRules.From(0.634, true).ToolTip);
        }

        [Fact]
        public void Every_tooltip_has_a_Chinese_text_without_spaces_between_Chinese_and_Latin()
        {
            Dictionary<string, string> table = StringTableZhHans.Create();
            foreach (string key in new[] { "Learning.Indicator.None", "Learning.Indicator.Partial", "Learning.Indicator.Full" })
            {
                string zh = table[key];
                Assert.False(string.IsNullOrWhiteSpace(zh));
                Assert.DoesNotContain(" ", zh);
            }
        }

        [Fact]
        public void A_hidden_indicator_is_the_shared_hidden_value_whatever_the_rate()
        {
            Assert.Same(LearningIndicator.Hidden, LearningIndicatorRules.From(0.8, false));
            Assert.False(LearningIndicator.Hidden.IsVisible);
            LearningIndicator shown = LearningIndicatorRules.From(0.8, true);
            Assert.True(shown.IsVisible);
            Assert.Equal(LearningLevel.Blue, shown.Level);
            Assert.Equal(80, shown.Percent);
        }

        // ---------------- settings page, Lock / Slip ----------------

        private static string LockId(PageRig rig) => KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);

        [Fact]
        public void Settings_page_shows_each_values_maturity_under_Auto()
        {
            var rig = new PageRig();
            rig.Host.Maturity[(true, LockId(rig))] = new KeyDataMaturity(1.0, 0.6, 0.3);
            var vm = new KeyDataPageViewModel(rig.Ctx, true);

            Assert.True(vm.Auto);
            Assert.Equal(LearningLevel.Green, vm.SMaxIndicator.Level);
            Assert.Equal(LearningLevel.Azure, vm.S90Indicator.Level);
            Assert.Equal(LearningLevel.Yellow, vm.S75Indicator.Level);
            Assert.True(vm.SMaxIndicator.IsVisible && vm.S90Indicator.IsVisible && vm.S75Indicator.IsVisible);
        }

        [Fact]
        public void Settings_page_hides_every_indicator_under_Manual_and_brings_them_back_at_once_with_Auto()
        {
            var rig = new PageRig();
            rig.Host.Maturity[(true, LockId(rig))] = new KeyDataMaturity(0.5, 0.5, 0.5);
            var vm = new KeyDataPageViewModel(rig.Ctx, true);
            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            vm.Auto = false;
            Assert.False(vm.SMaxIndicator.IsVisible);
            Assert.False(vm.S90Indicator.IsVisible);
            Assert.False(vm.S75Indicator.IsVisible);
            Assert.Contains(nameof(KeyDataPageViewModel.SMaxIndicator), raised);

            vm.Auto = true;
            Assert.True(vm.SMaxIndicator.IsVisible);
            Assert.Equal(50, vm.SMaxIndicator.Percent);
        }

        [Fact]
        public void Settings_page_hides_the_lower_indicators_with_their_rows_under_max_grip_only()
        {
            var rig = new PageRig();
            rig.Ctx.Settings.Lock.NormalizePattern = NormalizePattern.MaxGripOnly;
            var vm = new KeyDataPageViewModel(rig.Ctx, true);

            Assert.True(vm.SMaxIndicator.IsVisible);
            Assert.False(vm.S90Indicator.IsVisible);
            Assert.False(vm.S75Indicator.IsVisible);
        }

        [Fact]
        public void Settings_page_rereads_the_maturity_when_the_game_changes()
        {
            var rig = new PageRig();
            string id = LockId(rig);
            rig.Host.Maturity[(true, id)] = new KeyDataMaturity(0.1, 0.1, 0.1);
            var vm = new KeyDataPageViewModel(rig.Ctx, true);
            vm.Activate();
            Assert.Equal(LearningLevel.Orange, vm.SMaxIndicator.Level);

            rig.Host.Maturity[(true, id)] = new KeyDataMaturity(0.8, 0.8, 0.8);
            rig.Host.CurrentGameId = "AnotherGame";
            rig.Host.RaisePluginStateChanged();

            Assert.Equal(LearningLevel.Blue, vm.SMaxIndicator.Level);
            vm.Deactivate();
        }

        [Fact]
        public void Settings_page_with_nothing_known_reads_not_learned_yet()
        {
            var rig = new PageRig();
            var vm = new KeyDataPageViewModel(rig.Ctx, false);

            Assert.True(vm.SMaxIndicator.IsVisible);
            Assert.Equal(0, vm.SMaxIndicator.Percent);
            Assert.Equal(LearningLevel.Red, vm.SMaxIndicator.Level);
            Assert.Equal(Strings.Get("Learning.Indicator.None"), vm.SMaxIndicator.ToolTip);
        }

        // ---------------- ShakeIt panel, Lock / Slip ----------------

        [Fact]
        public void Panel_shows_the_indicators_under_Auto_and_hides_them_under_Manual()
        {
            var host = new FakePanelHost { Maturity = new KeyDataMaturity(0.9, 0.4, 0.2) };
            var vm = new KeyDataViewModel(true, host, new ManualTicker(), new ImmediateDispatcher());
            vm.Refresh();

            Assert.True(vm.Auto);
            Assert.Equal(LearningLevel.Blue, vm.SMaxIndicator.Level);
            Assert.Equal(LearningLevel.Yellow, vm.S90Indicator.Level);
            Assert.Equal(LearningLevel.Orange, vm.S75Indicator.Level);

            vm.Auto = false;
            Assert.False(vm.SMaxIndicator.IsVisible);
            Assert.False(vm.S90Indicator.IsVisible);
            Assert.False(vm.S75Indicator.IsVisible);
        }

        // ---------------- Slip Angle tab and panel ----------------

        [Fact]
        public void Slip_angle_tab_shows_max_best_min_maturity_under_Auto_only()
        {
            var host = new FakeSlipAngleHost { Maturity = new SlipAngleWindowMaturity(1.0, 0.7, 0.7) };
            var vm = new SlipAngleTabViewModel(host, new ManualTicker());
            vm.Load(host.Settings.SlipAngle);
            vm.Activate();

            Assert.True(vm.Auto);
            Assert.Equal(LearningLevel.Green, vm.MaxIndicator.Level);
            Assert.Equal(LearningLevel.Azure, vm.BestIndicator.Level);
            Assert.Equal(LearningLevel.Azure, vm.MinIndicator.Level);

            vm.Auto = false;
            Assert.False(vm.MaxIndicator.IsVisible);
            Assert.False(vm.BestIndicator.IsVisible);
            Assert.False(vm.MinIndicator.IsVisible);
        }

        /// <summary>BORROWING (owner, 2026-11-22): only a borrowed WINDOW colours the Slip Angle LED (half-and-half, the left
        /// half this key's own confidence); a borrowed k is a tooltip line only.</summary>
        [Fact]
        public void Slip_angle_led_shows_a_borrowed_window_in_its_colour_and_a_borrowed_k_only_in_the_tooltip()
        {
            var window = new SlipAngleWindowMaturity(1.0, 1.0, 1.0, ColdStartTier.Tier4, 0.0, kBorrowed: false);
            LearningIndicator led = LearningIndicatorRules.SlipAngle(window.Max, true, window);
            Assert.True(led.IsBorrowed);
            Assert.Equal(LearningLevel.Green, led.Level);
            Assert.Equal(LearningLevel.Red, led.OwnLevel);
            Assert.Equal(Strings.Get("Learning.Indicator.Origin.BorrowedSurface"), led.ToolTip);

            var car = new SlipAngleWindowMaturity(1.0, 1.0, 1.0, ColdStartTier.Tier3, 0.0, kBorrowed: false);
            Assert.Equal(Strings.Get("Learning.Indicator.Origin.BorrowedCar"), LearningIndicatorRules.SlipAngle(car.Max, true, car).ToolTip);

            var kOnly = new SlipAngleWindowMaturity(0.4, 0.4, 0.4, ColdStartTier.Tier1, 0.4, kBorrowed: true);
            LearningIndicator k = LearningIndicatorRules.SlipAngle(kOnly.Max, true, kOnly);
            Assert.False(k.IsBorrowed);                                         // the colour is the key's own
            Assert.Equal(LearningLevel.Yellow, k.Level);
            Assert.Contains(Strings.Get("Learning.Indicator.SlipAngle.KBorrowed"), k.ToolTip);

            var own = new SlipAngleWindowMaturity(0.4, 0.4, 0.4);
            Assert.DoesNotContain(Strings.Get("Learning.Indicator.SlipAngle.KBorrowed"), LearningIndicatorRules.SlipAngle(own.Max, true, own).ToolTip);
            Assert.False(LearningIndicatorRules.SlipAngle(window.Max, false, window).IsVisible);   // Manual: hidden
        }

        [Fact]
        public void Slip_angle_panel_shows_max_best_min_maturity_under_Auto_only()
        {
            var host = new FakePanelHost { SlipAngleMaturity = new SlipAngleWindowMaturity(0.66, 0.4, 0.4) };
            var vm = new SlipAngleEffectViewModel(host, new FakeSlipAngleEffectHost(), new ManualTicker(), new ImmediateDispatcher());
            vm.Activate();

            Assert.True(vm.Auto);
            Assert.Equal(66, vm.MaxIndicator.Percent);
            Assert.Equal(LearningLevel.Azure, vm.MaxIndicator.Level);
            Assert.Equal(LearningLevel.Yellow, vm.BestIndicator.Level);

            vm.Auto = false;
            Assert.False(vm.MaxIndicator.IsVisible);
            Assert.False(vm.MinIndicator.IsVisible);
        }
    }
}
