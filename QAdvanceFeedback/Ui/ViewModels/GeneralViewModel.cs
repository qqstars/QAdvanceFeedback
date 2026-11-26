using System;
using System.Collections.Generic;
using System.Windows.Input;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The General tab (docs\full-mvvm-plan.md §3.2), ported from Settings\SettingsControl.xaml(.cs):
    /// <list type="bullet">
    /// <item>the diagnostics / CSV switches - settings, so they go through the draft and Apply;</item>
    /// <item>the ShakeIt reference-data import - runs and persists at once (never part of Apply); its
    /// "override" switch is the import's argument, not a setting;</item>
    /// <item>the plugin health list (<see cref="Health"/>), read when the page is built (the old page's
    /// last constructor step), each time the General tab is shown, and after a page action failed;</item>
    /// <item>the version line, read from the running DLL through the host.</item>
    /// </list>
    /// </summary>
    public sealed class GeneralViewModel : PageSectionViewModel, IActivatable
    {
        private readonly IGeneralPageHost _host;
        private readonly RelayCommand _import;
        private string _importResult = string.Empty;

        public GeneralViewModel(SettingsPageContext ctx) : base(ctx)
        {
            _host = ctx.Host as IGeneralPageHost;
            Health = new HealthListViewModel(ReadHealth, ctx.Clipboard);
            _import = new RelayCommand(RunImport, () => _host != null);
            _updateHost = ctx.Host as IUpdateHost;
            _check = new RelayCommand(() => _updateHost?.CheckForUpdate(), () => _updateHost != null && Update.IsNotChecking);
            _apply = new RelayCommand(ApplyUpdate, () => _updateHost != null && Update.HasNewVersion && Update.IsNotChecking);
            // The check runs on its own thread and sets the shared status; the buttons follow it (WPF re-queries a
            // RelayCommand's CanExecute on the UI thread).
            Update.PropertyChanged += (s, e) => ctx.Ui.Post(() => { _check.RaiseCanExecuteChanged(); _apply.RaiseCanExecuteChanged(); });
            // Any page action that failed (any tab) shows on the health list at once. Same lifetime as
            // the context, so no unsubscribe.
            ctx.ActionFailed += (s, e) => Health.Refresh();
            Health.Refresh();
        }

        // ---------------------------------------------------------------- General group

        /// <summary>Publishes the diagnostic properties (needs a SimHub restart - the note says so).</summary>
        public bool EnableDiagnostics
        {
            get => S.General.EnableDiagnostics;
            set => Edit(S.General.EnableDiagnostics, value, v => S.General.EnableDiagnostics = v);
        }

        /// <summary>Writes every property to a session CSV.</summary>
        public bool ExportCsv
        {
            get => S.General.ExportCsv;
            set => Edit(S.General.ExportCsv, value, v => S.General.ExportCsv = v);
        }

        public string VersionText => string.Format(Strings.Get("General.Version.Label"), _host?.PluginVersion ?? "unknown");

        // ---------------------------------------------------------------- VERSION UPDATE (owner, 2026-11-25, 1.3.0)

        private readonly IUpdateHost _updateHost;
        private readonly RelayCommand _check;
        private readonly RelayCommand _apply;
        private string _updateError = string.Empty;

        /// <summary>The shared update status - the view binds to it ONE-WAY (version line, " (latest)", the green
        /// " (new version: ...)", the Update button's visibility).</summary>
        public UpdateStatusViewModel Update => UpdateStatusViewModel.Current;

        /// <summary>"Check For Update": the same check as at start, again (its own thread).</summary>
        public ICommand CheckForUpdateCommand => _check;

        /// <summary>"Update": shown only while a different version was downloaded.</summary>
        public ICommand UpdateCommand => _apply;

        /// <summary>A failed update's message (the check's own message is <see cref="UpdateStatusViewModel.Message"/>).</summary>
        public string UpdateError
        {
            get => _updateError;
            private set { if (SetProperty(ref _updateError, value ?? string.Empty)) OnPropertyChanged(nameof(HasUpdateError)); }
        }

        public bool HasUpdateError => !string.IsNullOrEmpty(_updateError);

        private void ApplyUpdate()
        {
            UpdateError = string.Empty;
            string error = _updateHost?.ApplyUpdate();
            if (!string.IsNullOrEmpty(error)) UpdateError = error;
        }

        // ---------------------------------------------------------------- ShakeIt reference data import

        /// <summary>"Override current data if exists?" - a setting (owner, 2026-11-25, default on): the start-up sync and
        /// the button take SimHub's values for a game already held (on) or keep ours and only add new ones (off). Saved
        /// with Apply; the button uses the value shown.</summary>
        public bool ShakeItImportOverride
        {
            get => S.General.ShakeItImportOverride;
            set
            {
                Edit(S.General.ShakeItImportOverride, value, v => S.General.ShakeItImportOverride = v);
                OnPropertyChanged(nameof(ShowShakeItImportButton));
            }
        }

        /// <summary>The manual sync button shows only while the switch is OFF (owner, 2026-11-25): with it on, every start
        /// already takes SimHub's current values. Follows the switch as shown, applied or not.</summary>
        public bool ShowShakeItImportButton => !ShakeItImportOverride;

        public ICommand ShakeItImportCommand => _import;

        /// <summary>The last import's outcome message - success and "SimHub not found" alike are reported
        /// here, so the button is never silent.</summary>
        public string ShakeItImportResultText
        {
            get => _importResult;
            private set
            {
                if (SetProperty(ref _importResult, value ?? string.Empty)) OnPropertyChanged(nameof(ShowShakeItImportResult));
            }
        }

        public bool ShowShakeItImportResult => !string.IsNullOrEmpty(_importResult);

        // ---------------------------------------------------------------- health

        public HealthListViewModel Health { get; }

        /// <summary>Re-reads the health list (the page calls this after one of its actions failed, so the
        /// fault it just recorded shows at once - the old SafeUiAction did the same).</summary>
        public void RefreshHealth() => Health.Refresh();

        /// <summary>The General tab is shown: show the health entries recorded since it was last read.</summary>
        public void Activate() => Health.Refresh();

        public void Deactivate() { }

        private IReadOnlyList<HealthEntry> ReadHealth() =>
            _host != null ? _host.HealthSnapshot() : HealthRegistry.Snapshot();

        private void RunImport()
        {
            try
            {
                global::QAdvanceFeedback.ShakeItConversionOutcome outcome = _host.ImportShakeItPrecalibration(ShakeItImportOverride);
                ShakeItImportResultText = outcome?.Message ?? string.Empty;
            }
            catch (Exception e)
            {
                // The plugin's import catches its own faults; this guards the click itself (logged,
                // recorded, and the health list refreshed through ActionFailed).
                Ctx.ReportFailure(e);
            }
        }
    }
}
