using System;
using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>
    /// The settings page's root view model (docs\full-mvvm-plan.md §3.1-3.2): it owns the draft and its
    /// hub subscription for its whole life, so a ShakeIt-side change can never be missed - the
    /// owner-reported defect was a page that listened only while Loaded and so missed the first change.
    /// </summary>
    public class SettingsPageViewModelTests
    {
        private readonly FakeSettingsPageHost _host = new FakeSettingsPageHost();
        private readonly FakeDialogService _dialogs = new FakeDialogService();
        private readonly ManualTicker _ticker = new ManualTicker();

        private SettingsPageViewModel NewPage(IUiDispatcher ui = null) =>
            new SettingsPageViewModel(_host, ui ?? new ImmediateDispatcher(), _ticker, _dialogs, new FakeClipboard());

        private static string Json(object o) => JsonConvert.SerializeObject(o);

        private sealed class HeldDispatcher : IUiDispatcher
        {
            public readonly Queue<Action> Pending = new Queue<Action>();
            public void Post(Action action) => Pending.Enqueue(action);
            public void PostLater(Action action) => Pending.Enqueue(action);
            public void Flush() { while (Pending.Count > 0) Pending.Dequeue()(); }
        }

        [Fact]
        public void A_ShakeIt_side_change_before_the_page_was_ever_shown_reaches_the_page()
        {
            // THE ORIGINAL DEFECT: the page is built eagerly but listened only while Loaded, and had no
            // catch-up before its first Loaded - the first ShakeIt-side source switch never reached it.
            SettingsPageViewModel vm = NewPage();
            Assert.NotEqual(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);

            Assert.True(_host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel));

            Assert.Equal(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);
            // The whole switch (archived / restored source rows) came along, not only the mode.
            Assert.Equal(Json(_host.Settings.Slip), Json(vm.Context.Settings.Slip));
            Assert.False(vm.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_change_keeps_an_unapplied_edit_of_another_field()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.BrakeThresholdPercent = 29;
            vm.Context.Draft.MarkEdited();

            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);

            Assert.Equal(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);
            Assert.Equal(29, vm.Context.Settings.Lock.BrakeThresholdPercent);
            Assert.True(vm.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_change_replaces_an_unapplied_edit_of_the_same_field()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.GForce.FixedAccelMaxG = 1.9;
            vm.Context.Draft.MarkEdited();
            Assert.True(vm.IsDirty);

            _host.Hub.Set(SettingField.FixedAccelMaxG, 2.4, SettingsOrigin.ShakeItPanel);

            Assert.Equal(2.4, vm.Context.Settings.GForce.FixedAccelMaxG);
            Assert.False(vm.IsDirty);
        }

        [Fact]
        public void A_page_origin_broadcast_does_not_clear_another_pending_edit()
        {
            // e.g. a key-data immediate save from another section, or the source-availability push.
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.BrakeThresholdPercent = 29;
            vm.Context.Draft.MarkEdited();

            _host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.PluginPage);

            Assert.Equal(29, vm.Context.Settings.Lock.BrakeThresholdPercent);
            Assert.True(vm.IsDirty);
        }

        [Fact]
        public void Hub_events_are_marshalled_to_the_ui_thread()
        {
            var ui = new HeldDispatcher();
            SettingsPageViewModel vm = NewPage(ui);

            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);
            Assert.NotEqual(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);   // not touched off-thread

            ui.Flush();
            Assert.Equal(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);
        }

        [Fact]
        public void Activate_catches_up_with_a_change_nobody_signalled()
        {
            SettingsPageViewModel vm = NewPage();
            _host.Settings.Lock.BrakeThresholdPercent = 17;   // e.g. a runtime seed, no hub event

            vm.Activate();

            Assert.Equal(17, vm.Context.Settings.Lock.BrakeThresholdPercent);
            Assert.False(vm.IsDirty);
            vm.Deactivate();
        }

        [Fact]
        public void Apply_after_a_ShakeIt_side_change_does_not_revert_it()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.BrakeThresholdPercent = 29;
            vm.Context.Draft.MarkEdited();
            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);
            _host.Settings.GForce.FixedLatMaxG = 2.9;   // and one nobody signalled

            vm.ApplyCommand.Execute(null);

            Assert.Equal(SourceMode.Custom, _host.Settings.Slip.SourceMode);
            Assert.Equal(2.9, _host.Settings.GForce.FixedLatMaxG);
            Assert.Equal(29, _host.Settings.Lock.BrakeThresholdPercent);
            Assert.Equal(Json(_host.Settings), Json(vm.Context.Settings));
        }

        [Fact]
        public void Apply_writes_the_draft_and_the_plugin_broadcasts_the_changed_fields_once()
        {
            SettingsPageViewModel vm = NewPage();
            var raised = new List<SettingsChangedEventArgs>();
            _host.Hub.Changed += (s, e) => raised.Add(e);
            var changedProps = new List<string>();
            vm.PropertyChanged += (s, e) => changedProps.Add(e.PropertyName);

            Assert.False(vm.ApplyCommand.CanExecute(null));
            vm.Context.Settings.GForce.FixedAccelMaxG = 2.2;
            vm.Context.Draft.MarkEdited();
            Assert.True(vm.IsDirty);
            Assert.True(vm.ApplyCommand.CanExecute(null));
            Assert.Contains(nameof(SettingsPageViewModel.IsDirty), changedProps);

            double liveDuringApply = double.NaN;
            _host.OnApply = s => liveDuringApply = s.GForce.FixedAccelMaxG;
            vm.ApplyCommand.Execute(null);

            Assert.Equal(1, _host.ApplyCalls);
            Assert.Equal(2.2, liveDuringApply);   // written before the plugin applies
            Assert.Single(raised);
            Assert.Equal(SettingsOrigin.PluginPage, raised[0].Origin);
            Assert.Equal(new[] { SettingField.FixedAccelMaxG }, raised[0].Fields);
            Assert.False(vm.IsDirty);
            Assert.False(vm.ApplyCommand.CanExecute(null));
        }

        [Fact]
        public void Apply_adopts_what_the_plugin_normalised_while_applying()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.BrakeThresholdPercent = 29;
            vm.Context.Draft.MarkEdited();
            _host.OnApply = s => s.Lock.BrakeThresholdPercent = 28;

            vm.ApplyCommand.Execute(null);

            Assert.Equal(28, vm.Context.Settings.Lock.BrakeThresholdPercent);
            Assert.False(vm.IsDirty);
        }

        [Fact]
        public void A_failing_apply_is_reported_not_thrown()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.BrakeThresholdPercent = 29;
            vm.Context.Draft.MarkEdited();
            _host.OnApply = s => throw new InvalidOperationException("boom");

            vm.ApplyCommand.Execute(null);   // must not throw into WPF
        }

        [Fact]
        public void Restore_defaults_asks_first_and_a_declined_confirm_changes_nothing()
        {
            _host.Settings.Lock.BrakeThresholdPercent = 3;
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.GForce.FixedAccelMaxG = 2.2;
            vm.Context.Draft.MarkEdited();
            string liveBefore = Json(_host.Settings), draftBefore = Json(vm.Context.Settings);
            _dialogs.Answer = false;

            vm.RestoreDefaultsCommand.Execute(null);

            Assert.Equal(new[] { Strings.Get("Common.RestoreAllConfirm") }, _dialogs.Confirms);
            Assert.Equal(liveBefore, Json(_host.Settings));
            Assert.Equal(draftBefore, Json(vm.Context.Settings));
            Assert.Equal(0, _host.ApplyCalls);
            Assert.True(vm.IsDirty);
        }

        [Fact]
        public void Restore_defaults_resets_live_immediately_and_carries_key_data_and_custom_sources_across()
        {
            QAdvanceFeedbackSettings s = _host.Settings;
            s.Lock.BrakeThresholdPercent = 3;
            s.GForce.FixedLatMaxG = 3.3;
            s.Lock.KeyDataPoints.Values["g|lock"] = new KeyDataPointEntry { SMax = 8, S90 = 6, S75 = 5 };
            s.Slip.KeyDataPoints.Values["g|slip"] = new KeyDataPointEntry { SMax = 7, S90 = 5, S75 = 4 };
            s.Lock.CustomSources = new WheelSourceSet { FrontLeft = "my.fl" };
            KeyDataPointSettings lockKeyData = s.Lock.KeyDataPoints;

            // Today's rule, computed the way the old page did it: RestoreDefaults on the live settings.
            QAdvanceFeedbackSettings expected = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(Json(s));
            expected.RestoreDefaults();

            SettingsPageViewModel vm = NewPage();
            vm.RestoreDefaultsCommand.Execute(null);

            Assert.Equal(Json(expected), Json(_host.Settings));
            Assert.Equal(8, _host.Settings.Lock.KeyDataPoints.Values["g|lock"].SMax);
            Assert.Equal("my.fl", _host.Settings.Lock.CustomSources.FrontLeft);
            Assert.Same(lockKeyData, _host.Settings.Lock.KeyDataPoints);   // carried data untouched in place
            // Immediate, like today: applied and saved, nothing left for Apply.
            Assert.Equal(1, _host.ApplyCalls);
            Assert.False(vm.IsDirty);
            Assert.Equal(Json(_host.Settings), Json(vm.Context.Settings));
        }

        [Fact]
        public void Restore_defaults_discards_unapplied_edits_like_today()
        {
            // Today the page reset the LIVE settings and reloaded every control from them, so an
            // unapplied edit - even of a carried-across value - was gone.
            _host.Settings.Lock.CustomSources = new WheelSourceSet { FrontLeft = "applied.fl" };
            SettingsPageViewModel vm = NewPage();
            vm.Context.Settings.Lock.CustomSources.FrontLeft = "unapplied.fl";
            vm.Context.Settings.General.ExportCsv = !vm.Context.Settings.General.ExportCsv;
            vm.Context.Draft.MarkEdited();

            vm.RestoreDefaultsCommand.Execute(null);

            Assert.Equal("applied.fl", _host.Settings.Lock.CustomSources.FrontLeft);
            Assert.Equal("applied.fl", vm.Context.Settings.Lock.CustomSources.FrontLeft);
            Assert.Equal(QAdvanceFeedbackSettings.CreateDefault().General.ExportCsv, _host.Settings.General.ExportCsv);
            Assert.False(vm.IsDirty);
        }

        [Fact]
        public void Dispose_unsubscribes_from_the_hub()
        {
            SettingsPageViewModel vm = NewPage();
            vm.Dispose();

            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);

            Assert.NotEqual(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);
            vm.Dispose();   // idempotent
        }

        [Fact]
        public void A_change_queued_before_Dispose_is_dropped_after_it()
        {
            var ui = new HeldDispatcher();
            SettingsPageViewModel vm = NewPage(ui);
            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);
            vm.Dispose();
            ui.Flush();
            Assert.NotEqual(SourceMode.Custom, vm.Context.Settings.Slip.SourceMode);
        }

        [Fact]
        public void The_page_exposes_its_tabs_and_a_context_over_the_same_draft()
        {
            SettingsPageViewModel vm = NewPage();
            Assert.NotNull(vm.LockTab);
            Assert.NotNull(vm.SlipTab);
            Assert.NotSame(vm.LockTab, vm.SlipTab);
            Assert.True(vm.LockTab.IsLock);
            Assert.False(vm.SlipTab.IsLock);
            Assert.NotNull(vm.GForceTab);
            Assert.NotNull(vm.GeneralTab);
            Assert.Same(_host, vm.Context.Host);
            Assert.Same(vm.Context.Draft.Settings, vm.Context.Settings);
            Assert.Same(_ticker, vm.Context.Ticker);
            Assert.Same(_dialogs, vm.Context.Dialogs);
        }

        [Fact]
        public void Section_view_models_refresh_all_properties_when_the_draft_is_reloaded()
        {
            SettingsPageViewModel vm = NewPage();
            var names = new List<string>();
            ((INotifyPropertyChanged)vm.GForceTab).PropertyChanged += (s, e) => names.Add(e.PropertyName);

            _host.Hub.Set(SettingField.FixedAccelMaxG, 2.4, SettingsOrigin.ShakeItPanel);

            Assert.Contains(null, names);
        }

        [Fact]
        public void A_page_without_a_running_plugin_is_tolerated()
        {
            var host = new FakeSettingsPageHost { Settings = null };
            var vm = new SettingsPageViewModel(host, new ImmediateDispatcher(), _ticker, _dialogs, new FakeClipboard());
            vm.Activate();
            vm.ApplyCommand.Execute(null);
            vm.RestoreDefaultsCommand.Execute(null);
            Assert.NotNull(vm.Context.Settings);
            vm.Dispose();
        }

        // ---- the section base class ----

        private sealed class ProbeSection : PageSectionViewModel
        {
            public ProbeSection(SettingsPageContext ctx) : base(ctx) { }
            public int Reloads;
            public DraftReloadedEventArgs Last;

            public double Brake
            {
                get => S.Lock.BrakeThresholdPercent;
                set => Edit(S.Lock.BrakeThresholdPercent, value, v => S.Lock.BrakeThresholdPercent = v);
            }

            public bool SetBrake(double v) => Edit(S.Lock.BrakeThresholdPercent, v, x => S.Lock.BrakeThresholdPercent = x, nameof(Brake));

            protected override void OnDraftReloaded(DraftReloadedEventArgs e)
            {
                Reloads++;
                Last = e;
                base.OnDraftReloaded(e);
            }
        }

        [Fact]
        public void Edit_writes_the_draft_raises_the_property_and_marks_the_draft_edited()
        {
            SettingsPageViewModel vm = NewPage();
            var section = new ProbeSection(vm.Context);
            var names = new List<string>();
            section.PropertyChanged += (s, e) => names.Add(e.PropertyName);
            double original = section.Brake;

            Assert.False(section.SetBrake(original));   // unchanged: nothing happens
            Assert.Empty(names);
            Assert.False(vm.IsDirty);

            section.Brake = original + 4;
            Assert.Equal(original + 4, vm.Context.Settings.Lock.BrakeThresholdPercent);
            Assert.Equal(new[] { "Brake" }, names);
            Assert.True(vm.IsDirty);
            Assert.Equal(original, _host.Settings.Lock.BrakeThresholdPercent);   // not applied
        }

        [Fact]
        public void A_section_hears_the_reload_with_the_changed_paths()
        {
            SettingsPageViewModel vm = NewPage();
            var section = new ProbeSection(vm.Context);

            _host.Hub.Set(SettingField.FixedAccelMaxG, 2.4, SettingsOrigin.ShakeItPanel);

            Assert.Equal(1, section.Reloads);
            Assert.True(section.Last.Touches("GForce.FixedAccelMaxG"));
        }
    }
}
