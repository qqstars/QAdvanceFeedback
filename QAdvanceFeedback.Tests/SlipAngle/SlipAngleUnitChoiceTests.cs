using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Tests.Ui;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>
    /// The unit dropdown (owner, 2026-11-21 / 2026-11-22): always shown. A hard-coded game offers its own unit and the car
    /// angle; any other game Auto-detect, degrees, radians, a 0-1 / 0-100 slip ratio and the car angle. Restore default
    /// puts a hard-coded game back to its unit and any other game back to Auto-detect; the lists are authoritative.
    /// </summary>
    public class SlipAngleUnitChoiceTests
    {
        // ---------------- the lists ----------------

        [Fact]
        public void Each_choice_is_one_list_and_reset_takes_the_game_out_of_every_list()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            foreach (SlipAngleUnitChoice c in new[] { SlipAngleUnitChoice.Degrees, SlipAngleUnitChoice.Radians, SlipAngleUnitChoice.Ratio1,
                         SlipAngleUnitChoice.Ratio100, SlipAngleUnitChoice.CarAngle })
            {
                u.SetChoice("ACC", c);
                Assert.Equal(c, u.Choice("ACC"));
                Assert.Equal(c == SlipAngleUnitChoice.CarAngle, u.ForcesCarAngle("ACC"));
            }
            Assert.True(u.SetChoice("ACC", null));
            Assert.Null(u.Choice("ACC"));
            Assert.False(u.IsListed("ACC"));
        }

        [Fact]
        public void A_listed_game_is_read_as_its_list_says_even_when_the_table_states_a_unit()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.Equal(SlipAngleUnit.Radians, u.Resolve("LFS", SlipAngleUnit.Radians));
            u.SetChoice("LFS", SlipAngleUnitChoice.Degrees);   // a hand edit
            Assert.Equal(SlipAngleUnit.Degrees, u.Resolve("LFS", SlipAngleUnit.Radians));
        }

        [Fact]
        public void The_car_angle_list_is_in_the_config_file()
        {
            var s = new QAdvanceFeedbackSettings();
            s.SlipAngle.EnsureSourceUnits();
            s.SlipAngle.SourceUnits.UseCarAngle.Add("MyGame");
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(s);
            Assert.Contains("\"UseCarAngle\"", json);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(json);
            back.SlipAngle.EnsureSourceUnits();
            Assert.True(back.SlipAngle.SourceUnits.ForcesCarAngle("MyGame"));
        }

        [Fact]
        public void A_game_set_to_car_angle_reads_no_tyre_value_even_though_the_game_provides_one()
        {
            SlipAngleSourceUnits units = SlipAngleSourceUnits.CreateDefault();
            units.SetChoice("ACC", SlipAngleUnitChoice.CarAngle);
            var reader = new SlipAngleRawSourceReader(new SlipAngleNativeSupport()) { Units = () => units };
            var values = new Dictionary<string, object>();
            for (int i = 0; i < 4; i++) values[SlipAngleRawSourceTable.FullPropertyName("Physics.slipAngle" + (i + 1).ToString("00"))] = 0.05f;
            SlipAngleInput input = reader.Read("ACC", n => values.TryGetValue(n, out object v) ? v : null,
                new TelemetryFrame(groundSpeedKmh: 108, speedKmh: 108, lateralG: 1.0), 1.0 / 60.0);
            Assert.False(input.IsNative);
            Assert.Null(input.NativeWheelDeg);

            // ... and the key data follows the car-angle key and heading.
            var ch = new SlipAngleChannelSettings { SourceUnits = units };
            Assert.Equal(SlipAngleFrameProcessor.FallbackIdentity, SettingsHub.SlipAngleIdentity(ch, SourceNativeness.Native, "ACC"));
            Assert.Equal("SlipAngle.KeyData.Header.CarAngle", SettingsHub.SlipAngleKeyDataHeaderKey(SourceMode.Manual, SourceNativeness.Native, "ACC", units));
        }

        // ---------------- what is hard-coded ----------------

        [Theory]
        [InlineData("LFS", true)]        // the table states the unit
        [InlineData("RFactor2", true)]   // computed by the plugin
        [InlineData("FH6", true)]        // shipped in the lists
        [InlineData("KartKraft", true)]
        [InlineData("NoSuchGame", true)] // no tyre field: nothing to choose
        [InlineData("ACC", false)]
        [InlineData("F12025", false)]
        public void Only_a_game_whose_unit_is_not_known_for_certain_can_be_changed(string game, bool hardCoded)
        {
            Assert.Equal(hardCoded, SettingsHub.SlipAngleUnitIsHardCoded(game));
        }

        // ---------------- the entries (owner, 2026-11-22) ----------------

        private static readonly SlipAngleUnitChoice[] AllUnits =
        {
            SlipAngleUnitChoice.AutoDetect, SlipAngleUnitChoice.Degrees, SlipAngleUnitChoice.Radians,
            SlipAngleUnitChoice.Ratio1, SlipAngleUnitChoice.Ratio100, SlipAngleUnitChoice.CarAngle,
        };

        [Fact]
        public void A_hard_coded_game_offers_its_own_unit_and_the_car_angle_any_other_game_auto_detect_and_everything()
        {
            Assert.Equal(new[] { SlipAngleUnitChoice.Ratio1, SlipAngleUnitChoice.CarAngle }, SettingsHub.SlipAngleUnitOptions("FH6"));
            Assert.Equal(new[] { SlipAngleUnitChoice.Degrees, SlipAngleUnitChoice.CarAngle }, SettingsHub.SlipAngleUnitOptions("KartKraft"));
            Assert.Equal(new[] { SlipAngleUnitChoice.Radians, SlipAngleUnitChoice.CarAngle }, SettingsHub.SlipAngleUnitOptions("LFS"));   // the table states it
            Assert.Equal(AllUnits, SettingsHub.SlipAngleUnitOptions("ACC"));
            Assert.Equal(new[] { SlipAngleUnitChoice.CarAngle }, SettingsHub.SlipAngleUnitOptions("NoSuchGame"));   // no tyre field
            Assert.Empty(SettingsHub.SlipAngleUnitOptions(null));
        }

        [Fact]
        public void The_dropdown_always_shows_for_plugin_internal_with_a_game_and_never_for_custom()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.True(SettingsHub.ShowSlipAngleUnitChoice(SourceMode.Manual, "ACC", u));   // still being checked
            Assert.True(SettingsHub.ShowSlipAngleUnitChoice(SourceMode.Manual, "FH6", u));   // hard-coded
            Assert.False(SettingsHub.ShowSlipAngleUnitChoice(SourceMode.Custom, "ACC", u));
            Assert.False(SettingsHub.ShowSlipAngleUnitChoice(SourceMode.Manual, null, u));
        }

        [Fact]
        public void A_hard_coded_game_can_use_the_car_angle_and_restore_default_puts_its_own_unit_back()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.Equal(SlipAngleUnitChoice.Ratio1, SettingsHub.SlipAngleSelectedUnit("FH6", u));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("FH6", u));

            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "FH6", SlipAngleUnitChoice.CarAngle));
            Assert.True(u.ForcesCarAngle("FH6"));
            Assert.Contains("FH6", u.UseCarAngle);   // the car-angle list in the config
            Assert.Equal(SlipAngleUnitChoice.CarAngle, SettingsHub.SlipAngleSelectedUnit("FH6", u));
            Assert.False(SettingsHub.SlipAngleUnitIsDefault("FH6", u));
            Assert.False(SettingsHub.ApplySlipAngleUnitChoice(u, "FH6", SlipAngleUnitChoice.Degrees));   // not offered for FH6

            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "FH6", null));   // Restore default
            Assert.False(u.ForcesCarAngle("FH6"));
            Assert.Equal(SlipAngleUnit.Ratio1, u.Resolve("FH6", SlipAngleUnit.Unverified));
            Assert.True(u.ExceedsFullScale("FH6"));
            Assert.Equal(SlipAngleUnitChoice.Ratio1, SettingsHub.SlipAngleSelectedUnit("FH6", u));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("FH6", u));

            // Picking the game's own unit is the same as Restore default.
            SettingsHub.ApplySlipAngleUnitChoice(u, "KartKraft", SlipAngleUnitChoice.CarAngle);
            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "KartKraft", SlipAngleUnitChoice.Degrees));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("KartKraft", u));
            Assert.Equal(SlipAngleUnit.Degrees, u.Resolve("KartKraft", SlipAngleUnit.Unverified));

            // A table-stated game: restore clears any list entry, so the table's unit applies again.
            u.SetChoice("LFS", SlipAngleUnitChoice.Degrees);   // a hand edit
            Assert.False(SettingsHub.SlipAngleUnitIsDefault("LFS", u));
            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "LFS", null));
            Assert.Equal(SlipAngleUnit.Radians, u.Resolve("LFS", SlipAngleUnit.Radians));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("LFS", u));
        }

        [Fact]
        public void Any_other_game_stays_on_auto_detect_until_the_driver_picks_and_restore_default_goes_back_to_it()
        {
            SlipAngleSourceUnits u = SlipAngleSourceUnits.CreateDefault();
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, SettingsHub.SlipAngleSelectedUnit("ACC", u));
            Assert.Null(SettingsHub.SlipAngleDetectedUnit("ACC", u));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("ACC", u));

            u.Assign("ACC", SlipAngleUnit.Radians);   // the check decided: still Auto-detect, naming what it found
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, SettingsHub.SlipAngleSelectedUnit("ACC", u));
            Assert.Equal(SlipAngleUnitChoice.Radians, SettingsHub.SlipAngleDetectedUnit("ACC", u));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("ACC", u));

            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "ACC", SlipAngleUnitChoice.Ratio100));
            Assert.Equal(SlipAngleUnitChoice.Ratio100, SettingsHub.SlipAngleSelectedUnit("ACC", u));
            Assert.Equal(SlipAngleUnit.Ratio100, u.Resolve("ACC", SlipAngleUnit.Unverified));
            Assert.False(SettingsHub.SlipAngleUnitIsDefault("ACC", u));

            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "ACC", SlipAngleUnitChoice.CarAngle));
            Assert.True(u.ForcesCarAngle("ACC"));

            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "ACC", null));   // Restore default
            Assert.False(u.IsListed("ACC"));                                     // checked again while driving
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, SettingsHub.SlipAngleSelectedUnit("ACC", u));
            Assert.True(SettingsHub.SlipAngleUnitIsDefault("ACC", u));

            SettingsHub.ApplySlipAngleUnitChoice(u, "ACC", SlipAngleUnitChoice.Degrees);
            Assert.True(SettingsHub.ApplySlipAngleUnitChoice(u, "ACC", SlipAngleUnitChoice.AutoDetect));   // = Restore default
            Assert.False(u.IsListed("ACC"));
        }

        [Fact]
        public void The_drivers_picks_are_in_the_config_file()
        {
            var s = new QAdvanceFeedbackSettings();
            s.SlipAngle.EnsureSourceUnits();
            SettingsHub.ApplySlipAngleUnitChoice(s.SlipAngle.SourceUnits, "ACC", SlipAngleUnitChoice.Degrees);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(s);
            Assert.Contains("\"ChosenByUser\"", json);
            var back = Newtonsoft.Json.JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(json);
            back.SlipAngle.EnsureSourceUnits();
            Assert.True(back.SlipAngle.SourceUnits.IsChosenByUser("ACC"));
            Assert.Equal(SlipAngleUnitChoice.Degrees, SettingsHub.SlipAngleSelectedUnit("ACC", back.SlipAngle.SourceUnits));
        }

        // ---------------- the hub field ----------------

        [Fact]
        public void The_hub_writes_the_choice_saves_and_broadcasts_and_restores_a_hard_coded_game()
        {
            var host = new FakePanelHost { CurrentGameId = "ACC" };
            var events = new List<SettingsChangedEventArgs>();
            host.Hub.Changed += (s, e) => events.Add(e);
            host.Settings.SlipAngle.EnsureSourceUnits();
            SlipAngleSourceUnits u = host.Settings.SlipAngle.SourceUnits;
            int saves = host.Saves;

            Assert.True(host.Hub.Set(SettingField.SlipAngleSourceUnit, (SlipAngleUnitChoice?)SlipAngleUnitChoice.Ratio100, SettingsOrigin.ShakeItPanel));
            Assert.Equal(SlipAngleUnitChoice.Ratio100, u.Choice("ACC"));
            Assert.True(host.Saves > saves);
            Assert.Contains(events, e => e.Fields.Contains(SettingField.SlipAngleSourceUnit));

            Assert.True(host.Hub.Set(SettingField.SlipAngleSourceUnit, null, SettingsOrigin.ShakeItPanel));
            Assert.False(u.IsListed("ACC"));

            host.CurrentGameId = "FH6";
            Assert.False(host.Hub.Set(SettingField.SlipAngleSourceUnit, (SlipAngleUnitChoice?)SlipAngleUnitChoice.Degrees, SettingsOrigin.ShakeItPanel));
            Assert.True(host.Hub.Set(SettingField.SlipAngleSourceUnit, (SlipAngleUnitChoice?)SlipAngleUnitChoice.CarAngle, SettingsOrigin.ShakeItPanel));
            Assert.True(u.ForcesCarAngle("FH6"));
            Assert.True(host.Hub.Set(SettingField.SlipAngleSourceUnit, null, SettingsOrigin.ShakeItPanel));
            Assert.False(u.ForcesCarAngle("FH6"));
            Assert.Equal(SlipAngleUnitChoice.Ratio1, u.Choice("FH6"));
        }

        // ---------------- the view models ----------------

        [Fact]
        public void The_tab_always_shows_the_dropdown_and_restore_default_only_after_a_change()
        {
            var host = new FakeSlipAngleHost { GameId = "ACC" };
            host.Settings.SlipAngle.EnsureSourceUnits();
            var tab = new SlipAngleTabViewModel(host, new ManualTicker());
            tab.Load(host.Settings.SlipAngle);
            tab.Activate();
            Assert.True(tab.ShowUnitChoice);
            Assert.False(tab.ShowUnitReset);
            Assert.Equal(AllUnits, tab.UnitChoices.Select(c => c.Value));
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, tab.SelectedUnitChoice);

            host.Settings.SlipAngle.SourceUnits.Assign("ACC", SlipAngleUnit.Radians);   // the check decided
            tab.RefreshKeyData();
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, tab.SelectedUnitChoice);
            Assert.False(tab.ShowUnitReset);
            Assert.Contains(Strings.Get("SlipAngle.Unit.Radians"), tab.UnitChoices[0].Label);

            tab.SelectedUnitChoice = SlipAngleUnitChoice.CarAngle;
            tab.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.True(host.Settings.SlipAngle.SourceUnits.ForcesCarAngle("ACC"));
            Assert.True(tab.ShowUnitReset);

            tab.ResetUnitCommand.Execute(null);
            tab.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.False(host.Settings.SlipAngle.SourceUnits.IsListed("ACC"));
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, tab.SelectedUnitChoice);
            Assert.True(tab.ShowUnitChoice);
            Assert.False(tab.ShowUnitReset);
        }

        [Fact]
        public void The_tab_offers_a_hard_coded_game_only_its_unit_and_the_car_angle()
        {
            var host = new FakeSlipAngleHost { GameId = "FH6" };
            host.Settings.SlipAngle.EnsureSourceUnits();
            var tab = new SlipAngleTabViewModel(host, new ManualTicker());
            tab.Load(host.Settings.SlipAngle);
            tab.Activate();
            Assert.True(tab.ShowUnitChoice);
            Assert.Equal(new[] { SlipAngleUnitChoice.Ratio1, SlipAngleUnitChoice.CarAngle }, tab.UnitChoices.Select(c => c.Value));
            Assert.Equal(SlipAngleUnitChoice.Ratio1, tab.SelectedUnitChoice);
            Assert.False(tab.ShowUnitReset);

            tab.SelectedUnitChoice = SlipAngleUnitChoice.CarAngle;
            tab.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.True(tab.ShowUnitReset);

            tab.ResetUnitCommand.Execute(null);
            tab.RefreshFields(new[] { SettingField.SlipAngleSourceUnit });
            Assert.Equal(SlipAngleUnitChoice.Ratio1, tab.SelectedUnitChoice);
            Assert.False(tab.ShowUnitReset);
            Assert.True(tab.ShowUnitChoice);
        }

        [Fact]
        public void The_panel_shows_the_dropdown_and_follows_the_hub()
        {
            var host = new FakePanelHost { CurrentGameId = "ACC" };
            host.Settings.SlipAngle.EnsureSourceUnits();
            var panel = new SlipAngleEffectViewModel(host, new FakeSlipAngleEffectHost { SlipAngleIdentityTier = SourceNativeness.Native },
                new ManualTicker(), new ImmediateDispatcher());
            panel.Activate();
            Assert.True(panel.ShowUnitChoice);
            Assert.False(panel.ShowUnitReset);
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, panel.SelectedUnitChoice);

            panel.SelectedUnitChoice = SlipAngleUnitChoice.Ratio1;   // the hub event refreshes the panel
            Assert.Equal(SlipAngleUnitChoice.Ratio1, host.Settings.SlipAngle.SourceUnits.Choice("ACC"));
            Assert.True(panel.ShowUnitReset);

            panel.ResetUnitCommand.Execute(null);
            Assert.True(panel.ShowUnitChoice);
            Assert.False(panel.ShowUnitReset);
            Assert.Equal(SlipAngleUnitChoice.AutoDetect, panel.SelectedUnitChoice);
        }
    }
}
