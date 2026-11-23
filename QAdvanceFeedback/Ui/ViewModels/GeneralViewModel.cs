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
        private bool _importOverride;
        private string _importResult = string.Empty;

        public GeneralViewModel(SettingsPageContext ctx) : base(ctx)
        {
            _host = ctx.Host as IGeneralPageHost;
            Health = new HealthListViewModel(ReadHealth, ctx.Clipboard);
            _import = new RelayCommand(RunImport, () => _host != null);
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

        // ---------------------------------------------------------------- ShakeIt reference data import

        /// <summary>"Override current data if exists?" - passed to the import; not saved, never dirty.</summary>
        public bool ShakeItImportOverride
        {
            get => _importOverride;
            set => SetProperty(ref _importOverride, value);
        }

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
                global::QAdvanceFeedback.ShakeItConversionOutcome outcome = _host.ImportShakeItPrecalibration(_importOverride);
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
