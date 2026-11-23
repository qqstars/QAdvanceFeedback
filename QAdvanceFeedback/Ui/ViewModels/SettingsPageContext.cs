using System;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>Everything a settings page section view model needs, created once by
    /// <see cref="SettingsPageViewModel"/> and shared by every section.</summary>
    public sealed class SettingsPageContext
    {
        public SettingsPageContext(SettingsDraft draft, ISettingsPageHost host, IUiDispatcher ui, ITicker ticker,
            IDialogService dialogs, IClipboard clipboard)
        {
            Draft = draft ?? throw new ArgumentNullException(nameof(draft));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Ui = ui ?? throw new ArgumentNullException(nameof(ui));
            Ticker = ticker ?? throw new ArgumentNullException(nameof(ticker));
            Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            Clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        }

        public SettingsDraft Draft { get; }
        public ISettingsPageHost Host { get; }
        public IUiDispatcher Ui { get; }
        public ITicker Ticker { get; }
        public IDialogService Dialogs { get; }
        public IClipboard Clipboard { get; }

        /// <summary>The draft the page edits (never the live settings). Read it each time.</summary>
        public QAdvanceFeedbackSettings Settings => Draft.Settings;

        /// <summary>A page action failed (already logged and recorded): the General tab re-reads its health
        /// list, so the failure is visible at once (the old page's SafeUiAction -> RefreshHealthUi).</summary>
        public event EventHandler ActionFailed;

        /// <summary>Runs a page action; a fault never reaches WPF / SimHub (see <see cref="ReportFailure"/>).</summary>
        public void Guard(Action action)
        {
            if (action == null) return;
            try { action(); }
            catch (Exception e) { ReportFailure(e); }
        }

        /// <summary>
        /// THE OLD PAGE'S SafeUiAction, kept whole: the SimHub log line
        /// (<c>"QAdvanceFeedback: settings UI action failed - " + e</c>), a Degraded SettingsUi health entry,
        /// then <see cref="ActionFailed"/> (the health list refreshes). Never throws.
        /// </summary>
        public void ReportFailure(Exception e)
        {
            if (e == null) return;
            try { Host.LogError("QAdvanceFeedback: settings UI action failed - " + e); }
            catch (Exception) { /* logging must never turn one failure into two */ }
            HealthRegistry.Report(HealthSubsystems.SettingsUi, HealthSeverity.Degraded, "Health.Impact.SettingsUi", e.ToString());
            try { ActionFailed?.Invoke(this, EventArgs.Empty); }
            catch (Exception) { /* the health refresh is best-effort */ }
        }
    }
}
