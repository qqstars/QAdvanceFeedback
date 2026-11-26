using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The General tab (docs\full-mvvm-plan.md §3.2): the two General switches (part of Apply), the ShakeIt
    /// reference-data import (immediate, not part of Apply), the plugin health list with its copy command,
    /// and the version line. Ported from Settings\SettingsControl.xaml(.cs); every rule names its old line.
    /// </summary>
    public class GeneralPageViewModelTests
    {
        private readonly FakeGeneralPageHost _host = new FakeGeneralPageHost();
        private readonly FakeClipboard _clipboard = new FakeClipboard();

        private SettingsPageViewModel NewPage(IClipboard clipboard = null) =>
            new SettingsPageViewModel(_host, new ImmediateDispatcher(), new ManualTicker(), new FakeDialogService(),
                clipboard ?? _clipboard);

        private static HealthEntry Entry(string subsystem, HealthSeverity severity = HealthSeverity.Degraded,
            string impactKey = "Health.Impact.SettingsUi", string detail = "boom", bool simHub = false) =>
            new HealthEntry(subsystem, severity, impactKey, detail, simHub, new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        // ------------------------------------------------------------ General switches

        [Fact]
        public void The_switches_show_the_draft_and_an_edit_arms_Apply_without_touching_live()
        {
            // Old: LoadFromSettings (SettingsControl.xaml.cs:3363-3364) / SaveToSettings (:3461-3462),
            // armed by the reflective dirty sweep (:1650-1660). Now: Edit() on the draft.
            _host.Settings.General.EnableDiagnostics = true;
            SettingsPageViewModel page = NewPage();
            GeneralViewModel vm = page.GeneralTab;

            Assert.True(vm.EnableDiagnostics);
            Assert.False(vm.ExportCsv);
            Assert.False(page.IsDirty);

            vm.ExportCsv = true;
            Assert.True(page.IsDirty);
            Assert.False(_host.Settings.General.ExportCsv);   // not live until Apply

            page.ApplyCommand.Execute(null);
            Assert.True(_host.Settings.General.ExportCsv);
            Assert.True(_host.Settings.General.EnableDiagnostics);
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Setting_a_switch_to_its_current_value_does_not_arm_Apply()
        {
            SettingsPageViewModel page = NewPage();
            page.GeneralTab.EnableDiagnostics = false;
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Turning_a_switch_back_clears_the_dirty_state()
        {
            SettingsPageViewModel page = NewPage();
            page.GeneralTab.EnableDiagnostics = true;
            page.GeneralTab.EnableDiagnostics = false;
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void A_live_change_reaches_the_switches()
        {
            SettingsPageViewModel page = NewPage();
            GeneralViewModel vm = page.GeneralTab;
            var changed = new List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            _host.Settings.General.ExportCsv = true;
            page.Activate();   // the catch-up adopts live changes nobody signalled

            Assert.True(vm.ExportCsv);
            Assert.True(changed.Contains(null) || changed.Contains(nameof(GeneralViewModel.ExportCsv)));
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Restore_all_defaults_resets_the_switches()
        {
            // Old: RestoreAllDefaults (SettingsControl.xaml.cs:2843) resets General with everything else.
            _host.Settings.General.EnableDiagnostics = true;
            _host.Settings.General.ExportCsv = true;
            SettingsPageViewModel page = NewPage();

            page.RestoreDefaultsCommand.Execute(null);

            Assert.False(page.GeneralTab.EnableDiagnostics);
            Assert.False(page.GeneralTab.ExportCsv);
            Assert.False(_host.Settings.General.EnableDiagnostics);
        }

        // ------------------------------------------------------------ version

        [Fact]
        public void The_version_line_shows_the_running_plugin_version()
        {
            // Old: LocalizeStaticText (SettingsControl.xaml.cs:2018) + GetRunningAssemblyFileVersion
            // (:3604) - read from the running DLL, never hardcoded.
            _host.PluginVersion = "1.2.0.7";
            Assert.Equal(string.Format(Strings.Get("General.Version.Label"), "1.2.0.7"), NewPage().GeneralTab.VersionText);
        }

        [Fact]
        public void Without_the_general_host_the_version_reads_unknown_and_import_is_disabled()
        {
            var plain = new FakeSettingsPageHost();
            var page = new SettingsPageViewModel(plain, new ImmediateDispatcher(), new ManualTicker(), new FakeDialogService(), _clipboard);
            Assert.Equal(string.Format(Strings.Get("General.Version.Label"), "unknown"), page.GeneralTab.VersionText);
            Assert.False(page.GeneralTab.ShakeItImportCommand.CanExecute(null));
        }

        // ------------------------------------------------------------ ShakeIt import

        [Fact]
        public void Import_passes_the_override_switch_and_shows_the_outcome_message()
        {
            // Old: RunShakeItImport (SettingsControl.xaml.cs:3661-3669).
            _host.OnImport = o => new global::QAdvanceFeedback.ShakeItConversionOutcome { Message = o ? "replaced" : "kept" };
            GeneralViewModel vm = NewPage().GeneralTab;
            Assert.False(vm.ShowShakeItImportResult);   // Collapsed in the XAML until the first import

            // Default ON (owner, 2026-11-25): the button takes SimHub's values for a game already held.
            Assert.True(vm.ShakeItImportOverride);
            vm.ShakeItImportCommand.Execute(null);
            Assert.Equal(new[] { true }, _host.Imports);
            Assert.Equal("replaced", vm.ShakeItImportResultText);
            Assert.True(vm.ShowShakeItImportResult);

            vm.ShakeItImportOverride = false;   // the value shown, applied or not
            vm.ShakeItImportCommand.Execute(null);
            Assert.Equal(new[] { true, false }, _host.Imports);
            Assert.Equal("kept", vm.ShakeItImportResultText);
        }

        [Fact]
        public void The_manual_sync_button_shows_only_while_the_override_switch_is_off()
        {
            GeneralViewModel vm = NewPage().GeneralTab;
            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);
            Assert.True(vm.ShakeItImportOverride);
            Assert.False(vm.ShowShakeItImportButton);   // on (default): every start syncs - no button

            vm.ShakeItImportOverride = false;
            Assert.True(vm.ShowShakeItImportButton);
            Assert.Contains(nameof(GeneralViewModel.ShowShakeItImportButton), changed);

            vm.ShakeItImportOverride = true;
            Assert.False(vm.ShowShakeItImportButton);
        }

        [Fact]
        public void An_outside_change_of_the_switch_updates_the_button_through_the_view_model()
        {
            SettingsPageViewModel page = NewPage();
            GeneralViewModel vm = page.GeneralTab;
            var changed = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            _host.Settings.General.ShakeItImportOverride = false;   // e.g. a hand edit picked up live
            page.Activate();                                        // the page adopts it into the draft

            Assert.False(vm.ShakeItImportOverride);
            Assert.True(vm.ShowShakeItImportButton);
            Assert.Contains(changed, n => n == null || n == nameof(GeneralViewModel.ShowShakeItImportButton));
        }

        [Fact]
        public void The_override_switch_is_a_setting_saved_with_Apply_and_reset_by_Restore_defaults()
        {
            SettingsPageViewModel page = NewPage();
            Assert.True(_host.Settings.General.ShakeItImportOverride);   // shipped default
            page.GeneralTab.ShakeItImportOverride = false;
            Assert.True(page.IsDirty);
            page.ApplyCommand.Execute(null);
            Assert.False(_host.Settings.General.ShakeItImportOverride);
            Assert.False(page.IsDirty);

            page.RestoreDefaultsCommand.Execute(null);
            Assert.True(page.GeneralTab.ShakeItImportOverride);
            Assert.True(_host.Settings.General.ShakeItImportOverride);
        }

        [Fact]
        public void An_empty_or_missing_outcome_hides_the_result_line()
        {
            GeneralViewModel vm = NewPage().GeneralTab;
            _host.OnImport = o => new global::QAdvanceFeedback.ShakeItConversionOutcome { Message = "x" };
            vm.ShakeItImportCommand.Execute(null);
            Assert.True(vm.ShowShakeItImportResult);

            _host.OnImport = o => null;   // outcome?.Message ?? string.Empty
            vm.ShakeItImportCommand.Execute(null);
            Assert.Equal(string.Empty, vm.ShakeItImportResultText);
            Assert.False(vm.ShowShakeItImportResult);

            _host.OnImport = o => new global::QAdvanceFeedback.ShakeItConversionOutcome { Message = "" };
            vm.ShakeItImportCommand.Execute(null);
            Assert.False(vm.ShowShakeItImportResult);
        }

        [Fact]
        public void A_failing_import_is_reported_on_the_health_list_instead_of_throwing()
        {
            _host.OnImport = o => throw new InvalidOperationException("disk gone");
            GeneralViewModel vm = NewPage().GeneralTab;
            try
            {
                vm.ShakeItImportCommand.Execute(null);   // must not throw into WPF
                Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.SettingsUi);
            }
            finally { HealthRegistry.Clear(HealthSubsystems.SettingsUi); }
        }

        // ------------------------------------------------------------ health

        [Fact]
        public void A_healthy_plugin_shows_one_all_good_line_and_no_copy_button()
        {
            // Old: RefreshHealthUi (SettingsControl.xaml.cs:1484-1496).
            HealthListViewModel health = NewPage().GeneralTab.Health;
            Assert.True(health.IsHealthy);
            Assert.Equal(Strings.Get("Health.AllGood"), health.AllGoodText);
            Assert.Empty(health.Issues);
            Assert.False(health.ShowCopyDetails);
            Assert.False(health.ShowCopyDone);
        }

        [Fact]
        public void Each_degraded_subsystem_gets_one_warning_block()
        {
            // Old: RefreshHealthUi (:1498-1520): "⚠ <subsystem>: <impact>", the SimHub-update sentence when
            // the entry is a compatibility issue (the case the owner named), Firebrick for Failed else
            // DarkOrange.
            _host.Health.Add(Entry(HealthSubsystems.SettingsUi));
            _host.Health.Add(Entry("PropertyPicker", HealthSeverity.Failed, "Health.Impact.PropertyPicker", simHub: true));
            HealthListViewModel health = NewPage().GeneralTab.Health;

            Assert.False(health.IsHealthy);
            Assert.True(health.ShowCopyDetails);
            Assert.Equal(2, health.Issues.Count);
            Assert.Equal("⚠ " + Strings.Get("Health.Subsystem.SettingsUi") + ": " + Strings.Get("Health.Impact.SettingsUi"),
                health.Issues[0].Text);
            Assert.False(health.Issues[0].IsFailed);
            Assert.Equal("⚠ " + Strings.Get("Health.Subsystem.PropertyPicker") + ": " + Strings.Get("Health.Impact.PropertyPicker")
                + " " + Strings.Get("Health.SimHubUpdateNeeded"), health.Issues[1].Text);
            Assert.True(health.Issues[1].IsFailed);
        }

        [Fact]
        public void Copy_details_puts_the_raw_english_report_on_the_clipboard_and_says_so()
        {
            // Old: CopyHealthDetailsToClipboard (SettingsControl.xaml.cs:1529-1549) - deliberately
            // unlocalized, read fresh from the registry at click time.
            _host.Health.Add(Entry("PropertyPicker", HealthSeverity.Failed, "Health.Impact.PropertyPicker", "trace 1", simHub: true));
            HealthListViewModel health = NewPage().GeneralTab.Health;
            _host.Health.Add(Entry("CsvExport", HealthSeverity.Degraded, "Health.Impact.CsvExport", "trace 2"));

            health.CopyDetailsCommand.Execute(null);

            string nl = Environment.NewLine;
            string expected =
                "[Failed] PropertyPicker (possible SimHub version mismatch) - first: 2026-03-04 05:06:07Z, last: 2026-03-04 05:06:07Z, occurrences: 1" + nl
                + "trace 1" + nl + nl
                + "[Degraded] CsvExport - first: 2026-03-04 05:06:07Z, last: 2026-03-04 05:06:07Z, occurrences: 1" + nl
                + "trace 2" + nl + nl;
            Assert.Equal(expected, _clipboard.Text);
            Assert.True(health.ShowCopyDone);
            Assert.Equal(Strings.Get("Health.CopyDetails.Done"), health.CopyDoneText);
        }

        [Fact]
        public void A_locked_clipboard_never_throws_and_still_says_copied()
        {
            // Old: the try/catch around Clipboard.SetText (:1544-1545); the done line is set after it.
            _host.Health.Add(Entry(HealthSubsystems.SettingsUi));
            var locked = new LockedClipboard();
            HealthListViewModel health = NewPage(locked).GeneralTab.Health;

            health.CopyDetailsCommand.Execute(null);

            Assert.Equal(1, locked.Attempts);
            Assert.True(health.ShowCopyDone);
        }

        [Fact]
        public void The_health_list_is_read_when_the_page_is_built()
        {
            // Old: RefreshHealthUi() is the constructor's LAST statement (SettingsControl.xaml.cs:238),
            // after every reflection wrapper has been resolved.
            NewPage();
            Assert.True(_host.HealthReads >= 1);
        }

        [Fact]
        public void Showing_the_General_tab_rereads_the_health_list()
        {
            // RULING: the old page read the registry once at construction and again only after one of its
            // own UI actions failed (SafeUiAction, :3633). A fault recorded later (a game started after the
            // page was built) stayed invisible. The General view now also re-reads it when it is shown.
            GeneralViewModel vm = NewPage().GeneralTab;
            Assert.True(vm.Health.IsHealthy);

            _host.Health.Add(Entry(HealthSubsystems.SettingsUi));
            vm.Activate();

            Assert.False(vm.Health.IsHealthy);
            Assert.Single(vm.Health.Issues);
        }

        [Fact]
        public void A_refresh_keeps_the_copied_line_while_issues_remain_and_drops_it_when_all_clear()
        {
            // Old: RefreshHealthUi only collapses HealthCopyDetailsDoneText in the all-good branch (:1494).
            _host.Health.Add(Entry(HealthSubsystems.SettingsUi));
            GeneralViewModel vm = NewPage().GeneralTab;
            vm.Health.CopyDetailsCommand.Execute(null);

            _host.Health.Add(Entry("CsvExport"));
            vm.Health.Refresh();
            Assert.True(vm.Health.ShowCopyDone);
            Assert.Equal(2, vm.Health.Issues.Count);

            _host.Health.Clear();
            vm.Health.Refresh();
            Assert.False(vm.Health.ShowCopyDone);
            Assert.False(vm.Health.ShowCopyDetails);
            Assert.True(vm.Health.IsHealthy);
        }

        [Fact]
        public void A_refresh_raises_change_notifications_for_the_bound_state()
        {
            GeneralViewModel vm = NewPage().GeneralTab;
            var changed = new List<string>();
            vm.Health.PropertyChanged += (s, e) => changed.Add(e.PropertyName);

            _host.Health.Add(Entry(HealthSubsystems.SettingsUi));
            vm.Health.Refresh();

            Assert.Contains(nameof(HealthListViewModel.IsHealthy), changed);
            Assert.Contains(nameof(HealthListViewModel.ShowCopyDetails), changed);
        }

        [Fact]
        public void Without_the_general_host_the_health_list_comes_from_the_registry()
        {
            const string probe = "GeneralPageViewModelTests.Probe";
            HealthRegistry.Report(probe, HealthSeverity.Degraded, "Health.Impact.SettingsUi", "x");
            try
            {
                var page = new SettingsPageViewModel(new FakeSettingsPageHost(), new ImmediateDispatcher(), new ManualTicker(),
                    new FakeDialogService(), _clipboard);
                Assert.Contains(page.GeneralTab.Health.Issues, i => i.Text.Contains(Strings.Get("Health.Subsystem." + probe)));
            }
            finally { HealthRegistry.Clear(probe); }
        }

        [Fact]
        public void The_general_view_model_runs_only_while_shown()
        {
            Assert.IsAssignableFrom<IActivatable>(NewPage().GeneralTab);
        }
    }
}
