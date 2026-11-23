using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The Wheel Lock / Wheel Slip tabs' Sources section (SourcesViewModel + SourceRowViewModel): the mode
    /// switch through the hub's path, the four rows with the P/J/N toggle and the Pick / Edit cascade, the Raw
    /// shadow, the notes, the ShakeIt option / note / algorithm block and the Viper game button. Every rule
    /// here was a comment (often an owner report) in the old Settings\SettingsControl.xaml.cs.
    /// </summary>
    public class WheelChannelPageSourcesTests
    {
        internal readonly WheelChannelPageTestHost Host = new WheelChannelPageTestHost();
        private SettingsPageViewModel _page;

        internal SettingsPageViewModel Page => _page ?? (_page = new SettingsPageViewModel(Host, new ImmediateDispatcher(),
            _ticker, new FakeDialogService(), new FakeClipboard()));

        internal WheelChannelPageViewModel Tab(bool isLock) => isLock ? Page.LockTab : Page.SlipTab;
        internal SourcesViewModel Sources(bool isLock) => Tab(isLock).Sources;
        internal QAdvanceFeedbackSettings Draft => Page.Context.Settings;
        internal static WheelChannelSettings Ch(QAdvanceFeedbackSettings s, bool isLock) => isLock ? s.Lock : s.Slip;
        private static string Json(object o) => JsonConvert.SerializeObject(o);

        // ------------------------------------------------------------------ mode switch (plan §1 item 3)

        public static IEnumerable<object[]> Switches()
        {
            SourceMode[] modes = { SourceMode.Manual, SourceMode.ShakeIt, SourceMode.Viper, SourceMode.Custom };
            foreach (bool isLock in new[] { true, false })
                foreach (SourceMode from in modes)
                    foreach (SourceMode to in modes)
                        if (from != to) yield return new object[] { isLock, from, to };
        }

        [Theory]
        [MemberData(nameof(Switches))]
        public void A_page_switch_and_a_hub_switch_produce_identical_settings(bool isLock, SourceMode from, SourceMode to)
        {
            // ONE SOURCE-SWITCH PATH (docs\full-mvvm-plan.md §1 item 3, §5): the page used to switch without the
            // per-mode archives; it now takes the hub's steps (Custom seeds its reference, then SwitchSourceMode).
            QAdvanceFeedbackSettings start = QAdvanceFeedbackSettings.CreateDefault();
            Ch(start, isLock).SwitchSourceMode(from, isLock);
            // Something in every archive, so a restore (not a preset) is exercised too.
            Ch(start, isLock).SwitchSourceMode(SourceMode.Custom, isLock);
            Ch(start, isLock).SourceFrontLeft = "custom.fl";
            Ch(start, isLock).SwitchSourceMode(from, isLock);

            var viaHub = new WheelChannelPageTestHost(SettingsJsonCopy(start));
            Assert.True(viaHub.Hub.Set(isLock ? SettingField.LockSourceMode : SettingField.SlipSourceMode, to, SettingsOrigin.ShakeItPanel));

            var page = new WheelChannelPageSourcesTests();
            page.Host.Settings = SettingsJsonCopy(start);
            page.Sources(isLock).Mode = to;
            page.Page.ApplyCommand.Execute(null);

            Assert.Equal(Json(viaHub.Settings.Lock), Json(page.Host.Settings.Lock));
            Assert.Equal(Json(viaHub.Settings.Slip), Json(page.Host.Settings.Slip));
            Assert.Equal(Json(viaHub.Settings.KeyDataPointDefaults), Json(page.Host.Settings.KeyDataPointDefaults));
        }

        internal static QAdvanceFeedbackSettings SettingsJsonCopy(QAdvanceFeedbackSettings s) =>
            JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_switch_is_a_draft_edit_applied_on_Apply(bool isLock)
        {
            Sources(isLock).Mode = SourceMode.Viper;

            Assert.True(Page.IsDirty);
            Assert.Equal(SourceMode.Manual, Ch(Host.Settings, isLock).SourceMode);   // live untouched until Apply
            Page.ApplyCommand.Execute(null);
            Assert.Equal(SourceMode.Viper, Ch(Host.Settings, isLock).SourceMode);
            Assert.Equal(ScriptType.NCalc, Ch(Host.Settings, isLock).ScriptTypeFrontLeft);
        }

        [Fact]
        public void Raw_to_Viper_to_Raw_returns_the_drivers_own_archive_not_a_regenerated_preset()
        {
            // Owner, 2026-09-28: "switching between the sources will NOT derive the settings from each other".
            Sources(true).Mode = SourceMode.Custom;
            Sources(true).Rows[0].Text = "my.custom.fl";
            Sources(true).Mode = SourceMode.Viper;
            Sources(true).Mode = SourceMode.Custom;
            Assert.Equal("my.custom.fl", Sources(true).Rows[0].Text);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Picking_Custom_seeds_its_reference_from_the_source_being_left_in_the_draft(bool isLock)
        {
            Sources(isLock).Mode = SourceMode.Viper;
            Sources(isLock).Mode = SourceMode.Custom;

            KeyDataPointDefaultSet seeded = isLock ? Draft.KeyDataPointDefaults.LockCustom : Draft.KeyDataPointDefaults.SlipCustom;
            Assert.NotNull(seeded);
            Assert.True(Draft.KeyDataPointDefaults.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, isLock, out double sMax, out _, out _));
            Assert.Equal(sMax, seeded.SMax);
            // The live settings are not touched before Apply (the old page seeded the LIVE defaults).
            Assert.Null(isLock ? Host.Settings.KeyDataPointDefaults.LockCustom : Host.Settings.KeyDataPointDefaults.SlipCustom);
        }

        [Fact]
        public void A_ShakeIt_side_switch_reaches_the_tab_and_raises_SourceChanged()
        {
            SourcesViewModel s = Sources(false);
            int changed = 0;
            s.SourceChanged += (o, e) => changed++;

            Host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Viper, SettingsOrigin.ShakeItPanel);

            Assert.Equal(SourceMode.Viper, s.Mode);
            Assert.Equal("Viper", s.ModeTag);
            Assert.Equal(Ch(Host.Settings, false).SourceFrontLeft, s.Rows[0].Text);
            Assert.True(changed > 0);
            Assert.False(Page.IsDirty);
        }

        [Fact]
        public void The_mode_tag_is_the_combo_items_tag()
        {
            SourcesViewModel s = Sources(true);
            s.ModeTag = "Custom";
            Assert.Equal(SourceMode.Custom, s.Mode);
            s.ModeTag = null;   // a combo clearing its selection is not an edit
            Assert.Equal(SourceMode.Custom, s.Mode);
            Assert.Equal("Custom", s.ModeTag);
        }

        // ------------------------------------------------------------------ editing a preset moves to Custom

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Editing_a_preset_sources_text_moves_the_channel_to_Custom_and_keeps_the_preset(bool isLock)
        {
            // Owner: "when the source string being changed, even though the apply button not being clicked, the
            // dropdown need to be switched to the Custom" - and the preset itself must stay as shipped.
            SourcesViewModel s = Sources(isLock);
            s.Mode = SourceMode.Viper;
            string viperFl = Ch(Draft, isLock).SourceFrontLeft;

            s.Rows[0].Text = "my.own.property";

            Assert.Equal(SourceMode.Custom, s.Mode);
            Assert.Equal("my.own.property", Ch(Draft, isLock).SourceFrontLeft);
            Assert.Equal(viperFl, Ch(Draft, isLock).ViperSources.FrontLeft);   // the preset's archive is untouched
            // Custom's reference was seeded from the preset just left.
            Draft.KeyDataPointDefaults.TryResolve(KnownFeedbackSource.ViperLngWheelSlip, isLock, out double viperSMax, out _, out _);
            Assert.Equal(viperSMax, (isLock ? Draft.KeyDataPointDefaults.LockCustom : Draft.KeyDataPointDefaults.SlipCustom).SMax);

            s.Mode = SourceMode.Viper;   // back to the preset: as shipped
            Assert.Equal(viperFl, s.Rows[0].Text);
        }

        [Fact]
        public void Editing_while_already_Custom_never_reseeds_the_reference()
        {
            SourcesViewModel s = Sources(true);
            s.Mode = SourceMode.Custom;
            Tab(true).CustomReference.SMax = 90;
            s.Rows[1].Text = "another.property";
            Assert.Equal(90, Draft.KeyDataPointDefaults.LockCustom.SMax);
        }

        [Fact]
        public void The_script_type_toggle_alone_does_not_move_to_Custom()
        {
            // As the old page: only a TEXT change (TextChanged) moved a preset to Custom.
            SourcesViewModel s = Sources(true);
            s.Rows[2].CycleScriptTypeCommand.Execute(null);
            Assert.Equal(SourceMode.Manual, s.Mode);
            Assert.Equal(ScriptType.JavaScript, Draft.Lock.ScriptTypeRearLeft);
        }

        [Fact]
        public void Typed_text_keeps_its_spaces_on_screen_and_is_stored_trimmed()
        {
            SourceRowViewModel row = Sources(true).Rows[0];
            row.Text = "a ";
            Assert.Equal("a ", row.Text);
            Assert.Equal("a", Draft.Lock.SourceFrontLeft);
            row.Text = "a b";
            Assert.Equal("a b", Draft.Lock.SourceFrontLeft);
        }

        // ------------------------------------------------------------------ script type and the button cascade

        [Fact]
        public void The_script_type_toggle_cycles_plain_javascript_ncalc_and_back()
        {
            // BUG 1 FIX: Plain used to be unreachable once left.
            SourceRowViewModel row = Sources(false).Rows[3];
            Assert.Equal("P", row.ScriptLetter);
            row.CycleScriptTypeCommand.Execute(null);
            Assert.Equal("J", row.ScriptLetter);
            Assert.Equal(Strings.Get("Sources.ScriptType.TooltipJavaScript"), row.ScriptTooltip);
            row.CycleScriptTypeCommand.Execute(null);
            Assert.Equal("N", row.ScriptLetter);
            row.CycleScriptTypeCommand.Execute(null);
            Assert.Equal("P", row.ScriptLetter);
            Assert.Equal(Strings.Get("Sources.ScriptType.TooltipPlain"), row.ScriptTooltip);
        }

        [Fact]
        public void A_script_type_change_changes_the_identity_and_raises_SourceChanged()
        {
            // "A wheel switching between Plain and a script changes the identity (scripts are hashed)."
            SourcesViewModel s = Sources(true);
            string before = s.CurrentSourceIdentity;
            int changed = 0;
            s.SourceChanged += (o, e) => changed++;
            s.Rows[0].CycleScriptTypeCommand.Execute(null);
            Assert.NotEqual(before, s.CurrentSourceIdentity);
            Assert.True(changed > 0);
        }

        [Fact]
        public void The_button_depends_on_the_rows_own_script_type()
        {
            // BUG 2's fix (SourceButtonModeSelector): a Plain row never offers "Edit script".
            SourceRowViewModel row = Sources(true).Rows[0];
            Assert.True(row.ShowAction);
            Assert.Equal(Strings.Get("Curve.PickButton"), row.ActionLabel);
            row.CycleScriptTypeCommand.Execute(null);
            Assert.Equal(Strings.Get("Sources.EditScriptButton"), row.ActionLabel);

            Host.EditorAvailable = false;
            Host.PickerAvailable = false;
            Sources(true).Refresh();
            Assert.False(row.ShowAction);
        }

        [Fact]
        public void The_picker_fills_the_box_and_a_cancel_or_empty_pick_changes_nothing()
        {
            SourceRowViewModel row = Sources(true).Rows[1];
            string before = row.Text;
            Host.NextEdit = null;
            row.ActionCommand.Execute(null);
            Assert.Equal(before, row.Text);
            Host.NextEdit = new SourceEdit("", ScriptType.Plain);
            row.ActionCommand.Execute(null);
            Assert.Equal(before, row.Text);
            Assert.False(Page.IsDirty);

            Host.NextEdit = new SourceEdit("Picked.Property", ScriptType.Plain);
            row.ActionCommand.Execute(null);
            Assert.Equal("Picked.Property", row.Text);
            Assert.Equal(SourceButtonMode.Picker, Host.EditCalls.Last().Mode);
            Assert.Equal(before, Host.EditCalls.Last().Text);
        }

        [Fact]
        public void The_editor_result_sets_the_text_and_the_dialect_it_produced()
        {
            // BUG 3: after a completed edit the row takes whatever dialect the editor produced, unconditionally.
            SourceRowViewModel row = Sources(false).Rows[0];
            row.CycleScriptTypeCommand.Execute(null);   // JavaScript -> the Edit button
            Host.NextEdit = new SourceEdit("return 1;", ScriptType.NCalc);
            row.ActionCommand.Execute(null);
            Assert.Equal(SourceButtonMode.Editor, Host.EditCalls.Last().Mode);
            Assert.Equal(ScriptType.JavaScript, Host.EditCalls.Last().Type);
            Assert.Equal("return 1;", row.Text);
            Assert.Equal(ScriptType.NCalc, row.ScriptType);
            Assert.Equal(ScriptType.NCalc, Draft.Slip.ScriptTypeFrontLeft);
        }

        [Fact]
        public void Without_the_evaluator_Plain_is_the_only_shown_type_and_the_toggle_is_hidden()
        {
            // BUG 1's other half: a stored JavaScript/NCalc row shows (and keys) as Plain when nothing can run it.
            Host.Settings.Slip.ScriptTypeRearRight = ScriptType.JavaScript;
            Host.EvaluatorAvailable = false;
            SourcesViewModel s = Sources(false);
            Assert.False(s.Rows[3].ShowScriptType);
            Assert.Equal(ScriptType.Plain, s.Rows[3].ScriptType);
            Assert.Equal(Strings.Get("Sources.ContractNote.NoEvaluator"), s.ContractNote);
            var plain = SettingsJsonCopy(Host.Settings).Slip;
            plain.ScriptTypeRearRight = ScriptType.Plain;
            Assert.Equal(KeyDataPanelModel.Identity(plain, false), s.CurrentSourceIdentity);
        }

        [Fact]
        public void With_the_evaluator_the_toggle_shows_and_the_contract_note_says_so()
        {
            Assert.True(Sources(true).Rows[0].ShowScriptType);
            Assert.Equal(Strings.Get("Sources.ContractNote"), Sources(true).ContractNote);
            Assert.Equal(Strings.Get("Sources.Note"), Sources(true).SourcesNote);
            Assert.Equal(Strings.Get("Sources.FrontLeft"), Sources(true).Rows[0].Label);
            Assert.Equal(Strings.Get("Sources.RearRight"), Sources(true).Rows[3].Label);
        }

        // ------------------------------------------------------------------ the Raw shadow

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_Viper_channel_on_an_unsupported_game_shows_Raw_disables_the_rows_and_keys_as_Raw(bool isLock)
        {
            Host.CurrentGameId = "SomeUnsupportedTitle";
            SourcesViewModel s = Sources(isLock);
            s.Mode = SourceMode.Viper;

            Assert.True(s.IsShadowing);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(RawSourceFallback.PropertyName(isLock, i), s.Rows[i].Text);
                Assert.Equal(ScriptType.Plain, s.Rows[i].ScriptType);
                Assert.False(s.Rows[i].IsEnabled);
            }
            Assert.Equal(RawSourceFallback.RawIdentity(isLock), s.CurrentSourceIdentity);
            Assert.True(s.ShowViperUnsupportedNote);
            Assert.False(s.ShowViperSetupNote);
            Assert.False(s.ShowViperUnavailableNote);
        }

        [Fact]
        public void A_shadowed_channels_readout_is_never_saved_over_its_configuration()
        {
            // DATA-DESTROYING BUG (owner, 2026-10-01): the shadowed Raw names were saved over the Viper scripts.
            Host.CurrentGameId = "SomeUnsupportedTitle";
            Sources(true).Mode = SourceMode.Viper;
            Sources(true).Rows[0].Text = "typed while disabled";   // ignored: the rows are dead
            Page.ApplyCommand.Execute(null);

            var viper = new WheelChannelSettings();
            viper.ApplyViperDefaults(true);
            Assert.Equal(SourceMode.Viper, Host.Settings.Lock.SourceMode);
            Assert.Equal(viper.SourceFrontLeft, Host.Settings.Lock.SourceFrontLeft);
            Assert.Equal(ScriptType.NCalc, Host.Settings.Lock.ScriptTypeFrontLeft);
        }

        [Fact]
        public void The_shadow_ends_when_the_game_becomes_supported_and_the_configuration_comes_back()
        {
            // Owner, 2026-10-02: after "Add to supported list" the rows must show the Viper configuration again.
            Host.CurrentGameId = "SomeUnsupportedTitle";
            SourcesViewModel s = Sources(true);
            s.Mode = SourceMode.Viper;
            Assert.True(s.IsShadowing);

            s.ToggleViperGameCommand.Execute(null);

            Assert.False(s.IsShadowing);
            Assert.Equal(Draft.Lock.SourceFrontLeft, s.Rows[0].Text);
            Assert.True(s.Rows[0].IsEnabled);
            Assert.Equal(KeyDataPanelModel.Identity(Draft.Lock, true), s.CurrentSourceIdentity);
        }

        [Fact]
        public void No_game_running_is_not_an_unsupported_game()
        {
            Host.CurrentGameId = null;
            Sources(true).Mode = SourceMode.Viper;
            Assert.False(Sources(true).IsShadowing);
            Assert.False(Sources(true).ShowViperGameButton);
        }

        [Theory]
        [InlineData(ShakeItSourceState.NotSetUp, true)]
        [InlineData(ShakeItSourceState.NotSupported, true)]
        [InlineData(ShakeItSourceState.SetUpIdle, false)]
        [InlineData(ShakeItSourceState.Supported, false)]
        public void A_ShakeIt_channel_shadows_only_on_a_verdict(ShakeItSourceState state, bool shadows)
        {
            Host.SetShakeIt(false, state);
            Sources(false).Mode = SourceMode.ShakeIt;
            Assert.Equal(shadows, Sources(false).IsShadowing);
        }

        [Fact]
        public void A_plugin_state_change_re_samples_the_shadow()
        {
            SourcesViewModel s = Sources(true);
            s.Mode = SourceMode.Viper;
            Tab(true).Activate();
            Assert.False(s.IsShadowing);
            int changed = 0;
            s.SourceChanged += (o, e) => changed++;

            Host.CurrentGameId = "SomeUnsupportedTitle";
            Host.RaisePluginStateChanged();

            Assert.True(s.IsShadowing);
            Assert.True(changed > 0);   // the identity moved to Raw's
            Tab(true).Deactivate();
            Assert.False(Host.HasPluginStateListeners);
        }

        [Fact]
        public void A_source_availability_broadcast_re_samples_while_shown()
        {
            SourcesViewModel s = Sources(true);
            s.Mode = SourceMode.ShakeIt;
            Tab(true).Activate();
            Host.SetShakeIt(true, ShakeItSourceState.NotSetUp);
            Host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.True(s.IsShadowing);
            Assert.Equal(Strings.Get("Sources.ShakeIt.NotSetUp.Note"), s.ShakeItStatus.Note);
        }

        // ------------------------------------------------------------------ notes

        [Theory]
        [InlineData(true, SourceMode.Manual)]
        [InlineData(true, SourceMode.ShakeIt)]
        [InlineData(true, SourceMode.Viper)]
        [InlineData(true, SourceMode.Custom)]
        [InlineData(false, SourceMode.Manual)]
        [InlineData(false, SourceMode.ShakeIt)]
        [InlineData(false, SourceMode.Viper)]
        [InlineData(false, SourceMode.Custom)]
        public void The_mode_notes_follow_the_shown_source(bool isLock, SourceMode mode)
        {
            Host.ViperAvailable = false;
            SourcesViewModel s = Sources(isLock);
            s.Mode = mode;

            Assert.Equal(mode == SourceMode.Manual, s.ShowManualNote);
            Assert.Equal(mode == SourceMode.Custom, s.ShowCustomNote);
            Assert.Equal(mode == SourceMode.Viper, s.ShowViperSetupNote);
            Assert.Equal(mode == SourceMode.Viper, s.ShowViperUnavailableNote);   // Viper not publishing
            Assert.False(s.ShowViperUnsupportedNote);
            Assert.Equal(mode == SourceMode.ShakeIt, s.Algorithm.IsVisible);
            Assert.Equal(Strings.Get(isLock ? "Sources.Mode.ManualNote.Lock" : "Sources.Mode.ManualNote.Slip"), s.ManualNote);
            Assert.Equal(Strings.Get(isLock ? "Sources.ViperSetup.Lock" : "Sources.ViperSetup.Slip"), s.ViperSetupNote);
            Assert.Equal(Strings.Get("Sources.Mode.CustomNote"), s.CustomNote);
            Assert.Equal(Strings.Get("Sources.ViperUnavailable.Note"), s.ViperUnavailableNote);
            Assert.Equal(Strings.Get("Sources.ViperUnsupported.Note"), s.ViperUnsupportedNote);
        }

        [Fact]
        public void The_Viper_unavailable_note_is_hidden_while_Viper_publishes()
        {
            Host.ViperAvailable = true;
            Sources(true).Mode = SourceMode.Viper;
            Assert.False(Sources(true).ShowViperUnavailableNote);
        }

        // ------------------------------------------------------------------ the Viper game button

        [Fact]
        public void The_game_button_shows_only_on_Viper_with_a_game_not_shipped_and_adds_or_removes_it_at_once()
        {
            SourcesViewModel s = Sources(true);
            Host.CurrentGameId = "SomeUnsupportedTitle";
            Assert.False(s.ShowViperGameButton);   // not Viper
            s.Mode = SourceMode.Viper;
            Assert.True(s.ShowViperGameButton);
            Assert.Equal(Strings.Get("Sources.ViperGame.Add"), s.ViperGameButtonLabel);
            Page.ApplyCommand.Execute(null);
            Assert.False(Page.IsDirty);

            s.ToggleViperGameCommand.Execute(null);

            Assert.Equal(1, Host.ViperSaves);
            Assert.True(ViperSupportedGamesIsSupported("SomeUnsupportedTitle"));
            Assert.Equal(Strings.Get("Sources.ViperGame.Remove"), s.ViperGameButtonLabel);
            // Saved at once (its own file), so it must not light Apply.
            Assert.False(Page.IsDirty);

            s.ToggleViperGameCommand.Execute(null);
            Assert.False(ViperSupportedGamesIsSupported("SomeUnsupportedTitle"));
            Assert.Equal(2, Host.ViperSaves);
        }

        private bool ViperSupportedGamesIsSupported(string game) =>
            QAdvanceFeedback.Core.Viper.ViperSupportedGames.IsSupported(game, Host.ViperDocument.SupportedGames);

        [Fact]
        public void The_game_button_is_hidden_for_a_shipped_title()
        {
            // Owner, 2026-10-01: "ONLY the game not in the internal support lists will display the button".
            Host.CurrentGameId = QAdvanceFeedback.Core.Viper.ViperSupportedGames.Shipped[0];
            Sources(false).Mode = SourceMode.Viper;
            Assert.False(Sources(false).ShowViperGameButton);
        }

        [Fact]
        public void The_game_button_announces_the_switch_first_and_the_other_tab_follows_the_broadcast()
        {
            Host.CurrentGameId = "SomeUnsupportedTitle";
            Sources(true).Mode = SourceMode.Viper;
            Sources(false).Mode = SourceMode.Viper;
            Tab(false).Activate();
            Assert.True(Sources(false).IsShadowing);
            int announced = 0;
            Sources(true).BeforeViperGameToggle += (o, e) => { announced++; Assert.Equal(0, Host.ViperSaves); };

            Sources(true).ToggleViperGameCommand.Execute(null);

            Assert.Equal(1, announced);
            Assert.False(Sources(false).IsShadowing);   // not per channel: both follow the list
        }

        // ------------------------------------------------------------------ ShakeIt option, note, block

        [Fact]
        public void The_ShakeIt_option_is_hidden_when_not_set_up_unless_shown_or_saved()
        {
            Host.SetShakeIt(true, ShakeItSourceState.NotSetUp);
            SourcesViewModel s = Sources(true);
            Assert.False(s.ShakeItStatus.ShowShakeItOption);

            s.Mode = SourceMode.ShakeIt;
            Assert.True(s.ShakeItStatus.ShowShakeItOption);
            Assert.Equal(Strings.Get("Sources.Mode.ShakeIt.NotSetUp"), s.ShakeItStatus.ShakeItOptionLabel);

            Page.ApplyCommand.Execute(null);   // saved as ShakeIt
            s.Mode = SourceMode.Manual;        // a different draft: still listed until Apply
            Assert.True(s.ShakeItStatus.ShowShakeItOption);
        }

        [Fact]
        public void The_algorithm_block_edits_the_draft_moves_the_key_and_applies_on_Apply()
        {
            SourcesViewModel s = Sources(true);
            s.Mode = SourceMode.ShakeIt;
            Assert.True(s.Algorithm.IsVisible);
            string before = s.CurrentSourceIdentity;
            int changed = 0;
            s.SourceChanged += (o, e) => changed++;

            s.Algorithm.LockSensibility = 70;

            Assert.Equal(70, Draft.Lock.ShakeItAlgorithm.LockSensibility);
            Assert.NotEqual(before, s.CurrentSourceIdentity);   // the fingerprint
            Assert.True(changed > 0);
            Assert.True(Page.IsDirty);
            Page.ApplyCommand.Execute(null);
            Assert.Equal(70, Host.Settings.Lock.ShakeItAlgorithm.LockSensibility);
        }

        [Fact]
        public void A_panel_side_algorithm_edit_reaches_the_block()
        {
            Host.Hub.Set(SettingField.SlipShakeItMuteWhileLockActive, true, SettingsOrigin.ShakeItPanel);
            Assert.True(Sources(false).Algorithm.MuteWhileLockActive);
            Assert.False(Page.IsDirty);
        }

        // ------------------------------------------------------------------ fallback warning

        [Fact]
        public void The_fallback_warning_follows_the_applied_source_and_the_branch_nativeness()
        {
            Host.CurrentLockNativeness = QAdvanceFeedback.Core.SourceNativeness.Fallback;
            Tab(true).Activate();
            Assert.True(Sources(true).FallbackWarning.ShowWarning);

            Sources(true).Mode = SourceMode.Viper;   // not applied: the engine still reads Plugin Internal
            Assert.True(Sources(true).FallbackWarning.ShowWarning);

            Host.CurrentLockNativeness = QAdvanceFeedback.Core.SourceNativeness.Native;
            Assert.True(Sources(true).FallbackWarning.ShowWarning);
            _ticker.Advance(System.TimeSpan.FromSeconds(1));   // the 1 s refresh
            Assert.False(Sources(true).FallbackWarning.ShowWarning);
        }

        private readonly ManualTicker _ticker = new ManualTicker();

        [Fact]
        public void The_fallback_warning_ticks_only_while_shown()
        {
            var page = new SettingsPageViewModel(Host, new ImmediateDispatcher(), _ticker, new FakeDialogService(), new FakeClipboard());
            Assert.Equal(0, _ticker.ActiveCount);
            page.LockTab.Activate();
            Assert.Equal(1, _ticker.ActiveCount);
            page.LockTab.Deactivate();
            Assert.Equal(0, _ticker.ActiveCount);
        }
    }
}
