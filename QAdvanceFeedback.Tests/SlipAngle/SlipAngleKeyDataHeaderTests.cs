using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Tests.Ui;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The unit-aware Slip Angle key data heading (owner, 2026-11-21), the new diagnostic columns' values, and the
    /// indicator rule while the window is still the starting one.
    /// </summary>
    public class SlipAngleKeyDataHeaderTests
    {
        private const string Head = "SlipAngle.KeyData.Header";

        private static string Key(string game, SourceMode mode = SourceMode.Manual, SourceNativeness tier = SourceNativeness.Native,
            SlipAngleSourceUnits units = null) => SettingsHub.SlipAngleKeyDataHeaderKey(mode, tier, game, units ?? SlipAngleSourceUnits.CreateDefault());

        [Theory]
        [InlineData("FH6", Head + ".Ratio1")]            // the Forza list: a 0-1 grip ratio
        [InlineData("FM8", Head + ".Ratio1")]
        [InlineData("KartKraft", Head)]                  // the degrees list
        [InlineData("LFS", Head + ".Radians")]           // the table states radians
        [InlineData("F12025", Head + ".AssumedRadians")] // nothing states it: read as radians
        [InlineData("ACC", Head + ".AssumedRadians")]
        [InlineData("", Head)]                           // no game yet
        [InlineData("NoSuchGame", Head)]                 // no native field
        public void Each_games_unit_names_what_the_key_points_are(string game, string key)
        {
            Assert.Equal(key, Key(game));
        }

        [Fact]
        public void A_game_moved_to_the_0_100_list_says_so()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            Assert.True(units.Assign("ACC", SlipAngleUnit.Ratio100));
            Assert.Equal(Head + ".Ratio100", Key("ACC", units: units));
        }

        [Fact]
        public void The_car_angle_fallback_and_a_custom_source_name_themselves_whatever_the_game()
        {
            Assert.Equal(Head + ".CarAngle", Key("FH6", tier: SourceNativeness.Fallback));
            Assert.Equal(Head + ".Custom", Key("FH6", mode: SourceMode.Custom));
            Assert.Equal(Head + ".Custom", Key("F12025", mode: SourceMode.Custom, tier: SourceNativeness.Fallback));
        }

        [Fact]
        public void Every_heading_has_English_and_Chinese_text_without_spaces_in_the_Chinese()
        {
            Dictionary<string, string> zh = StringTableZhHans.Create();
            foreach (string suffix in new[] { "", ".Radians", ".AssumedRadians", ".Ratio1", ".Ratio100", ".CarAngle", ".Custom" })
            {
                Assert.False(string.IsNullOrWhiteSpace(Strings.Get(Head + suffix)));
                Assert.DoesNotContain(" ", zh[Head + suffix]);
            }
            Assert.DoesNotContain(" ", zh["Learning.Indicator.SlipAngle.Progress"]);
        }

        [Fact]
        public void The_tab_and_the_panel_show_the_ratio_heading_for_FH6()
        {
            var tabHost = new FakeSlipAngleHost { GameId = "FH6" };
            var tab = new SlipAngleTabViewModel(tabHost, new ManualTicker());
            tab.Load(tabHost.Settings.SlipAngle);
            tab.Activate();
            Assert.Equal(Strings.Get(Head + ".Ratio1"), tab.KeyDataHeader);

            var panelHost = new FakePanelHost { CurrentGameId = "FH6" };
            var panel = new SlipAngleEffectViewModel(panelHost, new FakeSlipAngleEffectHost { SlipAngleIdentityTier = SourceNativeness.Native },
                new ManualTicker(), new ImmediateDispatcher());
            panel.Activate();
            Assert.Equal(Strings.Get(Head + ".Ratio1"), panel.KeyDataHeader);
        }

        [Fact]
        public void The_tab_heading_follows_the_game()
        {
            var host = new FakeSlipAngleHost { GameId = "LFS" };
            var tab = new SlipAngleTabViewModel(host, new ManualTicker());
            tab.Load(host.Settings.SlipAngle);
            tab.Activate();
            Assert.Equal(Strings.Get(Head + ".Radians"), tab.KeyDataHeader);

            host.GameId = "KartKraft";
            tab.RefreshKeyData();
            Assert.Equal(Strings.Get(Head), tab.KeyDataHeader);
        }

        // ---------------- indicators while the window is still the starting one ----------------

        [Fact]
        public void Before_confirmation_all_three_indicators_show_the_same_progress_and_say_the_peak_is_not_confirmed()
        {
            var host = new FakeSlipAngleHost { Maturity = new SlipAngleWindowMaturity(1.0 / 3.0, 1.0 / 3.0, 1.0 / 3.0) };
            var tab = new SlipAngleTabViewModel(host, new ManualTicker());
            tab.Load(host.Settings.SlipAngle);
            tab.Activate();

            string tip = LearningIndicatorRules.SlipAngleProgressToolTip(33);
            Assert.Equal(33, tab.MaxIndicator.Percent);
            Assert.Equal(33, tab.BestIndicator.Percent);
            Assert.Equal(33, tab.MinIndicator.Percent);
            Assert.Equal(tip, tab.MaxIndicator.ToolTip);
            Assert.Equal(tip, tab.MinIndicator.ToolTip);
            Assert.Contains("33%", tip);
        }

        [Fact]
        public void After_confirmation_max_is_fully_learned_and_best_min_show_their_own_coverage()
        {
            var host = new FakeSlipAngleHost { Maturity = new SlipAngleWindowMaturity(1.0, 0.6, 0.6) };
            var tab = new SlipAngleTabViewModel(host, new ManualTicker());
            tab.Load(host.Settings.SlipAngle);
            tab.Activate();

            Assert.Equal(Strings.Get("Learning.Indicator.Full"), tab.MaxIndicator.ToolTip);
            Assert.Equal(60, tab.BestIndicator.Percent);
            Assert.Equal(string.Format(Strings.Get("Learning.Indicator.Partial"), 60), tab.BestIndicator.ToolTip);
        }
    }
}
