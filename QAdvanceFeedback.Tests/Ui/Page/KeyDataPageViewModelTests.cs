using System;
using System.Collections.Generic;
using System.ComponentModel;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The Wheel Lock / Wheel Slip key data block of the settings page (old SettingsControl.xaml.cs
    /// "KEY DATA POINTS", ~247-1551, and its part of SettingsControl.Sync.cs). It shares the ShakeIt
    /// panel's rules (<see cref="KeyDataPanelModel.Resolve"/> / <see cref="KeyDataPanelModel.TryCommitManual"/>),
    /// keys everything by the page's DRAFT source, and applies / saves immediately - never through Apply.
    /// </summary>
    public class KeyDataPageViewModelTests
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(700);

        private static KeyDataPageViewModel New(PageRig rig, bool isLock = true) => new KeyDataPageViewModel(rig.Ctx, isLock);

        private static KeyDataPointSettings LiveStore(PageRig rig, bool isLock = true) =>
            isLock ? rig.Host.Settings.Lock.KeyDataPoints : rig.Host.Settings.Slip.KeyDataPoints;

        private static void Shipped(PageRig rig, bool isLock, out double a, out double b, out double c) =>
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                KeyDataPanelModel.Identity(isLock ? rig.Host.Settings.Lock : rig.Host.Settings.Slip, isLock),
                isLock, rig.Host.Settings.KeyDataPointDefaults, out a, out b, out c));

        /// <summary>Makes the channel's draft source an unrecognised (Custom) one, as the Sources section would.</summary>
        private static void CustomSource(PageRig rig, bool isLock = true)
        {
            WheelChannelSettings ch = isLock ? rig.Ctx.Settings.Lock : rig.Ctx.Settings.Slip;
            ch.SourceMode = SourceMode.Custom;
            ch.SourceFrontLeft = ch.SourceFrontRight = ch.SourceRearLeft = ch.SourceRearRight = "MyPlugin.MyValue";
        }

        private static void Manual(KeyDataPageViewModel vm) => vm.Auto = false;

        // ---------------------------------------------------------------- display rule (shared with the panel)

        [Fact]
        public void Auto_shows_the_learned_triple_of_the_one_display_rule_for_the_drafts_source()
        {
            var rig = new PageRig();
            string id = KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);
            // Lock's lower anchors come from the learned ratios - shown as learned, NOT re-derived from SMax
            // (the old page's 0.90/0.70 re-derivation; the panel and the restart rule show the triple).
            rig.Host.Learned[(true, id)] = new KeyDataTriple(50.0, 47.0, 40.0);

            var vm = New(rig);

            Assert.True(vm.Auto);
            Assert.Equal(50.0, vm.SMax);
            Assert.Equal(47.0, vm.S90);
            Assert.Equal(40.0, vm.S75);
            Assert.Contains((true, id), rig.Host.LearnedAsked);
            Assert.False(vm.ValuesEditable);
        }

        [Fact]
        public void Auto_with_nothing_learned_shows_the_shipped_reference_and_an_unknown_source_shows_nothing()
        {
            var rig = new PageRig();
            Shipped(rig, true, out double a, out double b, out double c);
            var vm = New(rig);
            Assert.Equal(a, vm.SMax); Assert.Equal(b, vm.S90); Assert.Equal(c, vm.S75);

            CustomSource(rig);
            vm.OnSourceChanged();
            Assert.Null(vm.SMax); Assert.Null(vm.S90); Assert.Null(vm.S75);
        }

        [Fact]
        public void Per_game_row_is_hidden_under_Auto_and_shown_in_Manual()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Assert.False(vm.ShowPerGame);
            Manual(vm);
            Assert.True(vm.ShowPerGame);
            Assert.True(vm.ValuesEditable);
        }

        [Fact]
        public void Manual_with_nothing_stored_prefills_the_shipped_defaults_without_storing_them()
        {
            var rig = new PageRig();
            Shipped(rig, true, out double a, out double b, out double c);
            var vm = New(rig);
            Manual(vm);

            Assert.Equal(a, vm.SMax); Assert.Equal(b, vm.S90); Assert.Equal(c, vm.S75);
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.False(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out _, out _, out _));
        }

        // ---------------------------------------------------------------- immediate apply / save

        [Fact]
        public void A_typed_value_latches_its_slot_synchronously_and_saves_700ms_later_never_dirtying_Apply()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            int appliesAfterToggle = rig.Host.ApplyCalls;

            vm.SMax = 88.0;

            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            KeyDataPointSettings k = LiveStore(rig);
            Assert.True(k.TryGetManual(rig.Host.CurrentGameId, id, out double s, out _, out _));
            Assert.Equal(88.0, s);
            Assert.True(k.IsSeeded(rig.Host.CurrentGameId, id));      // the one-time seed stands down at once
            Assert.Equal(appliesAfterToggle, rig.Host.ApplyCalls);      // disk write debounced

            rig.Ticker.Advance(Debounce);
            Assert.Equal(appliesAfterToggle + 1, rig.Host.ApplyCalls);
            rig.Ticker.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(appliesAfterToggle + 1, rig.Host.ApplyCalls);  // once, not every 700 ms
            Assert.False(rig.Draft.IsDirty);
        }

        [Fact]
        public void Edits_inside_the_debounce_save_once()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            int before = rig.Host.ApplyCalls;

            vm.SMax = 88.0;
            rig.Ticker.Advance(TimeSpan.FromMilliseconds(400));
            vm.SMax = 89.0;
            rig.Ticker.Advance(TimeSpan.FromMilliseconds(400));
            Assert.Equal(before, rig.Host.ApplyCalls);
            rig.Ticker.Advance(TimeSpan.FromMilliseconds(400));
            Assert.Equal(before + 1, rig.Host.ApplyCalls);
        }

        [Fact]
        public void A_pending_save_is_written_when_the_view_goes_away()
        {
            var rig = new PageRig();
            var vm = New(rig);
            vm.Activate();
            Manual(vm);
            int before = rig.Host.ApplyCalls;
            vm.SMax = 88.0;

            vm.Deactivate();

            Assert.Equal(before + 1, rig.Host.ApplyCalls);
            rig.Ticker.Advance(Debounce);
            Assert.Equal(before + 1, rig.Host.ApplyCalls);
        }

        [Fact]
        public void An_invalid_triple_is_not_stored_it_stays_on_screen_with_the_channels_message()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.S90 = 99.0;   // above SMax

            Assert.Equal(99.0, vm.S90);
            Assert.Equal(Strings.Get("KeyData.Invalid"), vm.Status);
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.False(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out _, out _, out _));

            var slip = New(new PageRig(), isLock: false);
            Manual(slip);
            slip.S90 = 99.0;
            Assert.Equal(Strings.Get("KeyData.Invalid.Slip"), slip.Status);
        }

        [Fact]
        public void A_typed_SMax_above_95_is_stored_and_shown_as_95()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.SMax = 98.0;

            Assert.Equal(KeyDataPointSettings.MaxSMax, vm.SMax);
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.True(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out double s, out _, out _));
            Assert.Equal(KeyDataPointSettings.MaxSMax, s);
        }

        // ---------------------------------------------------------------- toggles

        [Fact]
        public void The_toggles_write_the_drafts_source_flags_into_the_live_settings_and_save_at_once()
        {
            var rig = new PageRig();
            CustomSource(rig);
            var vm = New(rig);
            string draftId = KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);
            string appliedId = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            int before = rig.Host.ApplyCalls;

            vm.Auto = false;

            Assert.False(LiveStore(rig).GetAutoGenerate(draftId));
            Assert.True(LiveStore(rig).GetAutoGenerate(appliedId));     // the applied source is untouched
            Assert.Equal(before + 1, rig.Host.ApplyCalls);
            Assert.False(rig.Draft.IsDirty);

            vm.PerGame = true;
            Assert.True(LiveStore(rig).GetPerGame(draftId));
            Assert.Equal(before + 2, rig.Host.ApplyCalls);
        }

        [Fact]
        public void Going_back_to_Auto_keeps_the_typed_manual_numbers_in_their_slot()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.SMax = 86.0;   // pending save

            vm.Auto = true;

            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            KeyDataPointSettings k = LiveStore(rig);
            Assert.True(k.GetAutoGenerate(id));
            k.SetAutoGenerate(id, false);
            Assert.True(k.TryGetManual(rig.Host.CurrentGameId, id, out double s, out _, out _));
            Assert.Equal(86.0, s);
        }

        [Fact]
        public void Under_Auto_typing_writes_nothing()
        {
            var rig = new PageRig();
            var vm = New(rig);
            vm.SMax = 33.0;
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.False(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out _, out _, out _));
        }

        [Fact]
        public void Per_game_with_no_game_shows_blank_says_so_and_refuses_the_edit()
        {
            var rig = new PageRig();
            rig.Host.CurrentGameId = null;
            var vm = New(rig);
            Manual(vm);
            vm.PerGame = true;

            Assert.Null(vm.SMax);
            Assert.Equal(Strings.Get("KeyData.PerGame.NoGame"), vm.Status);

            vm.SMax = 60.0;
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.False(LiveStore(rig).TryGetManual(null, id, out _, out _, out _));
        }

        [Fact]
        public void A_game_arriving_re_resolves_the_per_game_slot()
        {
            var rig = new PageRig();
            rig.Host.CurrentGameId = null;
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            KeyDataPointSettings k = LiveStore(rig);
            k.SetAutoGenerate(id, false);
            k.SetPerGame(id, true);
            k.SetManual("Game2", id, 44.0, 40.0, 30.0, seeded: true);
            var vm = New(rig);
            vm.Activate();   // the key data listens to the plugin while shown
            Assert.Null(vm.SMax);

            rig.Host.CurrentGameId = "Game2";
            rig.Host.RaisePluginStateChanged();

            Assert.Equal(44.0, vm.SMax);
            Assert.Equal(string.Empty, vm.Status);
        }

        [Fact]
        public void A_seed_by_the_plugin_bumps_the_revision_and_the_page_reloads_the_slot()
        {
            var rig = new PageRig();
            var vm = New(rig);
            vm.Activate();   // the key data listens to the plugin while shown
            Manual(vm);
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            LiveStore(rig).SetManual(rig.Host.CurrentGameId, id, 52.0, 48.0, 40.0, seeded: true);

            rig.Host.KeyDataRevision++;
            rig.Host.RaisePluginStateChanged();

            Assert.Equal(52.0, vm.SMax);
        }

        // ---------------------------------------------------------------- draft source / shadow

        [Fact]
        public void The_source_follows_the_draft_selection_not_the_applied_one()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            Assert.NotNull(vm.SMax);   // Raw: shipped

            CustomSource(rig);         // unapplied
            vm.OnSourceChanged();

            Assert.Equal(KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true), vm.SourceIdentity);
            Assert.True(vm.Auto);      // the Custom source's own (default) flags, not Raw's Manual
            Assert.Null(vm.SMax);
        }

        [Fact]
        public void Learned_values_belong_to_the_source_they_were_learned_for_only()
        {
            // Owner-reported, 2026-10-02: Viper learns 13.5, switch to Raw without Apply, and the page showed
            // Raw's ~50 labelled as Viper's. The learned triple is looked up for the page's source alone.
            var rig = new PageRig();
            string appliedId = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            rig.Host.Learned[(true, appliedId)] = new KeyDataTriple(50.0, 45.0, 35.0);
            var vm = New(rig);
            CustomSource(rig);
            vm.OnSourceChanged();
            vm.Auto = false;

            Assert.Equal(Strings.Get("KeyData.LearnedPending"), vm.SMaxLearnedText);
            vm.Auto = true;
            Assert.Null(vm.SMax);
        }

        [Fact]
        public void A_shadowed_channel_keys_as_Raw_and_shows_the_note()
        {
            var rig = new PageRig();
            CustomSource(rig);
            rig.Ctx.Settings.Lock.SourceMode = SourceMode.Viper;
            rig.Host.Shadowing = true;
            var vm = New(rig);
            vm.Activate();   // the key data listens to the plugin while shown

            Assert.Equal(RawSourceFallback.RawIdentity(true), vm.SourceIdentity);
            Assert.True(vm.ShowShadowNote);

            rig.Host.Shadowing = false;
            rig.Host.RaisePluginStateChanged();
            Assert.False(vm.ShowShadowNote);
            Assert.Equal(KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true), vm.SourceIdentity);
        }

        [Fact]
        public void A_typed_value_lands_in_the_drafts_source_slot()
        {
            var rig = new PageRig();
            CustomSource(rig);
            var vm = New(rig);
            Manual(vm);
            vm.SMax = 12.0; vm.S90 = 10.0; vm.S75 = 8.0;

            string draftId = KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);
            Assert.True(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, draftId, out double s, out _, out double c));
            Assert.Equal(12.0, s);
            Assert.Equal(8.0, c);
        }

        // ---------------------------------------------------------------- learned hints, status

        [Fact]
        public void Learned_hints_show_only_in_Manual_and_for_an_unknown_source_only_once_the_gate_opened()
        {
            var rig = new PageRig();
            string rawId = KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);
            rig.Host.Learned[(true, rawId)] = new KeyDataTriple(9.3, 8.4, 6.5);
            var vm = New(rig);
            vm.Activate();   // the key data listens to the plugin while shown
            Assert.False(vm.ShowLearnedHints);

            Manual(vm);
            Assert.True(vm.ShowLearnedHints);   // Raw is a shipped source
            Assert.Equal(string.Format(Strings.Get("KeyData.LearnedPrefix"), "  9.3"), vm.SMaxLearnedText);

            CustomSource(rig);
            vm.OnSourceChanged();
            vm.Auto = false;
            Assert.False(vm.ShowLearnedHints);
            Assert.Equal(Strings.Get("KeyData.LearnedPending"), vm.SMaxLearnedText);
            Assert.Equal(Strings.Get("KeyData.Waiting"), vm.Status);

            rig.Host.GateReady = true;
            rig.Host.RaisePluginStateChanged();
            Assert.True(vm.ShowLearnedHints);
            Assert.Equal(string.Empty, vm.Status);
        }

        [Fact]
        public void Max_grip_only_hides_the_lower_rows_and_derives_them_on_commit()
        {
            var rig = new PageRig();
            rig.Ctx.Settings.Lock.NormalizePattern = NormalizePattern.MaxGripOnly;   // draft, unapplied
            var vm = New(rig);
            Manual(vm);
            Assert.False(vm.ShowLowerAnchors);
            Assert.False(vm.ShowLowerLearnedHints);

            vm.SMax = 50.0;

            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.True(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out _, out double b, out double c));
            Assert.Equal(45.0, b, 6);
            Assert.Equal(35.0, c, 6);
        }

        [Fact]
        public void Switching_to_the_mapping_fills_missing_lower_anchors()
        {
            // Shipped source, SMax still the shipped number: the shipped companions.
            Assert.True(KeyDataPointSettings.TryResolveShippedDefaults(
                KeyDataPanelModel.Identity(QAdvanceFeedbackSettings.CreateDefault().Lock, true), true,
                KeyDataPointDefaults.CreateShipped(), out double a, out double b, out double c));
            string raw = KeyDataPanelModel.Identity(QAdvanceFeedbackSettings.CreateDefault().Lock, true);
            KeyDataTriple t = KeyDataPageRules.FillLowerAnchors(a, null, 0.0, raw, true, KeyDataPointDefaults.CreateShipped());
            Assert.Equal(new KeyDataTriple(a, b, c), t);

            // Nothing in SMax on a shipped source: the whole shipped triple.
            t = KeyDataPageRules.FillLowerAnchors(null, null, null, raw, true, KeyDataPointDefaults.CreateShipped());
            Assert.Equal(new KeyDataTriple(a, b, c), t);

            // Another SMax: derived, rounded to one decimal; a value already there is kept.
            t = KeyDataPageRules.FillLowerAnchors(33.3, 31.0, null, "unknown-source", true, KeyDataPointDefaults.CreateShipped());
            Assert.Equal(new KeyDataTriple(33.3, 31.0, 23.3), t);

            // Both there: nothing to do.
            Assert.Null(KeyDataPageRules.FillLowerAnchors(50, 40, 30, raw, true, KeyDataPointDefaults.CreateShipped()));
        }

        [Fact]
        public void A_pattern_change_to_the_mapping_fills_and_stores_the_lower_anchors()
        {
            var rig = new PageRig();
            rig.Ctx.Settings.Lock.NormalizePattern = NormalizePattern.MaxGripOnly;
            var vm = New(rig);
            Manual(vm);
            vm.S90 = null; vm.S75 = null;   // what an old single-point configuration leaves
            vm.SMax = 40.0;

            rig.Ctx.Settings.Lock.NormalizePattern = NormalizePattern.Mapping;
            vm.OnPatternChanged();

            Assert.True(vm.ShowLowerAnchors);
            Assert.Equal(36.0, vm.S90);
            Assert.Equal(28.0, vm.S75);
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.True(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out _, out double b, out _));
            Assert.Equal(36.0, b, 6);
        }

        // ---------------------------------------------------------------- reset

        [Fact]
        public void Reset_shows_only_when_the_sources_configuration_differs_and_never_for_Custom()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Assert.False(vm.ShowReset);
            Manual(vm);
            Assert.True(vm.ShowReset);

            rig.Ctx.Settings.Lock.SourceMode = SourceMode.Custom;   // Raw's own rows, mode Custom
            vm.OnSourceChanged();
            Assert.False(vm.ShowReset);
        }

        [Fact]
        public void Reset_restores_the_sources_defaults_saves_at_once_and_drops_a_pending_save()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.PerGame = true;
            vm.SMax = 87.0;
            int before = rig.Host.ApplyCalls;

            vm.ResetCommand.Execute(null);

            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            KeyDataPointSettings k = LiveStore(rig);
            Assert.True(k.GetAutoGenerate(id));
            Assert.False(k.GetPerGame(id));
            Assert.False(k.TryGetManual(rig.Host.CurrentGameId, id, out _, out _, out _));
            Assert.Equal(before + 1, rig.Host.ApplyCalls);
            Assert.True(vm.Auto);
            Assert.False(vm.ShowReset);

            rig.Ticker.Advance(Debounce);
            Assert.Equal(before + 1, rig.Host.ApplyCalls);   // the cancelled debounce does not fire
            Assert.False(rig.Draft.IsDirty);
        }

        // ---------------------------------------------------------------- ShakeIt-side changes

        [Fact]
        public void A_ShakeIt_side_key_data_change_reaches_the_page_and_wins_over_a_pending_save()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.SMax = 88.0;   // pending save
            int before = rig.Host.ApplyCalls;

            Assert.True(rig.Host.Hub.Set(SettingField.LockKeyDataValues, new KeyDataTriple(30.0, 27.0, 21.0), SettingsOrigin.ShakeItPanel));
            rig.Adopt();

            Assert.Equal(30.0, vm.SMax);
            rig.Ticker.Advance(Debounce);
            Assert.Equal(before, rig.Host.ApplyCalls);   // the hub saved it; our stale value is not written
            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            Assert.True(LiveStore(rig).TryGetManual(rig.Host.CurrentGameId, id, out double s, out _, out _));
            Assert.Equal(30.0, s);
        }

        [Fact]
        public void The_adoption_of_our_own_write_does_not_cancel_its_save()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.SMax = 88.0;
            int before = rig.Host.ApplyCalls;

            rig.Adopt();   // e.g. another section's hub event: the draft adopts our live write

            Assert.Equal(88.0, vm.SMax);
            rig.Ticker.Advance(Debounce);
            Assert.Equal(before + 1, rig.Host.ApplyCalls);
        }

        [Fact]
        public void A_ShakeIt_side_Auto_change_reaches_the_page()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Assert.True(rig.Host.Hub.Set(SettingField.LockKeyDataAuto, false, SettingsOrigin.ShakeItPanel));
            rig.Adopt();
            Assert.False(vm.Auto);
            Assert.True(vm.ShowPerGame);
        }

        [Fact]
        public void The_two_channels_are_independent()
        {
            var rig = new PageRig();
            var lockVm = New(rig, true);
            var slipVm = New(rig, false);
            lockVm.Auto = false;
            Assert.True(slipVm.Auto);
            Assert.Equal(KeyDataPanelModel.Identity(rig.Ctx.Settings.Slip, false), slipVm.SourceIdentity);
        }

        [Fact]
        public void Labels_follow_the_channel()
        {
            var rig = new PageRig();
            var l = New(rig, true);
            var s = New(rig, false);
            Assert.Equal(Strings.Get("KeyData.Section.Lock"), l.Header);
            Assert.Equal(Strings.Get("KeyData.Section.Slip"), s.Header);
            Assert.Equal(Strings.Get("KeyData.Lock.SMax.Label"), l.SMaxLabel);
            Assert.Equal(Strings.Get("KeyData.Slip.S75.Desc"), s.S75Desc);
        }

        [Fact]
        public void Nothing_here_ever_dirties_the_page()
        {
            var rig = new PageRig();
            var vm = New(rig);
            vm.Auto = false; vm.PerGame = true; vm.PerGame = false;
            vm.SMax = 60.0; vm.S90 = 50.0; vm.S75 = 40.0;
            rig.Ticker.Advance(Debounce);
            vm.ResetCommand.Execute(null);
            Assert.False(rig.Draft.IsDirty);
        }

        [Fact]
        public void While_shown_under_Auto_the_readout_follows_the_learned_values_every_second()
        {
            var rig = new PageRig();
            string id = KeyDataPanelModel.Identity(rig.Ctx.Settings.Lock, true);
            var vm = New(rig);
            vm.Activate();
            rig.Host.Learned[(true, id)] = new KeyDataTriple(44.0, 40.0, 31.0);

            rig.Ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(44.0, vm.SMax);

            vm.Deactivate();
            rig.Host.Learned[(true, id)] = new KeyDataTriple(55.0, 50.0, 40.0);
            rig.Ticker.Advance(TimeSpan.FromSeconds(3));
            Assert.Equal(44.0, vm.SMax);   // no timer while not shown
        }

        [Fact]
        public void A_ShakeIt_side_pattern_change_re_resolves_the_lower_rows()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Assert.True(vm.ShowLowerAnchors);
            Assert.True(rig.Host.Hub.Set(SettingField.LockNormalizePattern, NormalizePattern.MaxGripOnly, SettingsOrigin.ShakeItPanel));
            rig.Adopt();
            Assert.False(vm.ShowLowerAnchors);
        }

        [Fact]
        public void An_unrelated_reload_leaves_a_typed_value_on_screen()
        {
            var rig = new PageRig();
            var vm = New(rig);
            Manual(vm);
            vm.S90 = 99.0;   // invalid, so only on screen
            rig.Host.Settings.GForce.FixedLatMaxG = 2.7;

            rig.Adopt();

            Assert.Equal(99.0, vm.S90);
            Assert.Equal(Strings.Get("KeyData.Invalid"), vm.Status);
        }

        [Fact]
        public void A_plugin_state_change_without_a_new_context_keeps_typed_values_in_Manual()
        {
            var rig = new PageRig();
            var vm = New(rig);
            vm.Activate();   // the key data listens to the plugin while shown
            Manual(vm);
            vm.S90 = 99.0;
            rig.Host.RaisePluginStateChanged();
            Assert.Equal(99.0, vm.S90);
        }

        [Fact]
        public void While_hidden_it_does_not_listen_and_showing_it_catches_up_with_a_seed()
        {
            // Wave 2 ruling: the page view model lives as long as the plugin, so a hidden key data block
            // does no work; Activate catches up with whatever the plugin changed meanwhile.
            var rig = new PageRig();
            var vm = New(rig);
            vm.Activate();
            Manual(vm);
            vm.Deactivate();
            Assert.False(rig.Host.HasPluginStateListeners);

            string id = KeyDataPanelModel.Identity(rig.Host.Settings.Lock, true);
            LiveStore(rig).SetManual(rig.Host.CurrentGameId, id, 52.0, 48.0, 40.0, seeded: true);
            rig.Host.KeyDataRevision++;
            rig.Host.RaisePluginStateChanged();
            Assert.NotEqual(52.0, vm.SMax);

            vm.Activate();
            Assert.Equal(52.0, vm.SMax);
            Assert.True(rig.Host.HasPluginStateListeners);
        }
    }
}
