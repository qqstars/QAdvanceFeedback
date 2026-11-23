using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>One warning block of the health list: the driver-readable sentence and whether the
    /// subsystem failed outright (Firebrick) rather than degraded (DarkOrange).</summary>
    public sealed class HealthIssueViewModel
    {
        public HealthIssueViewModel(string text, bool isFailed) { Text = text; IsFailed = isFailed; }
        public string Text { get; }
        public bool IsFailed { get; }
    }

    /// <summary>
    /// The General tab's Plugin health group (Core.Health.HealthRegistry). One all-good line when nothing
    /// is wrong (no clutter in the normal case); otherwise one warning block per degraded subsystem and a
    /// "copy details for a bug report" command. Ported from Settings\SettingsControl.xaml.cs
    /// RefreshHealthUi / CopyHealthDetailsToClipboard.
    /// </summary>
    public sealed class HealthListViewModel : ObservableObject
    {
        private readonly Func<IReadOnlyList<HealthEntry>> _snapshot;
        private readonly IClipboard _clipboard;
        private bool _isHealthy = true;
        private bool _showCopyDone;

        public HealthListViewModel(Func<IReadOnlyList<HealthEntry>> snapshot, IClipboard clipboard)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
            CopyDetailsCommand = new RelayCommand(CopyDetails);
        }

        /// <summary>One block per recorded entry, in the registry's order.</summary>
        public ObservableCollection<HealthIssueViewModel> Issues { get; } = new ObservableCollection<HealthIssueViewModel>();

        /// <summary>Nothing recorded: the all-good line shows, the copy button does not.</summary>
        public bool IsHealthy
        {
            get => _isHealthy;
            private set
            {
                if (SetProperty(ref _isHealthy, value)) OnPropertyChanged(nameof(ShowCopyDetails));
            }
        }

        public bool ShowCopyDetails => !_isHealthy;

        public string AllGoodText => Strings.Get("Health.AllGood");

        /// <summary>"Copied to clipboard." - shown after a copy, until the list is all clear again.</summary>
        public bool ShowCopyDone
        {
            get => _showCopyDone;
            private set => SetProperty(ref _showCopyDone, value);
        }

        public string CopyDoneText => Strings.Get("Health.CopyDetails.Done");

        public ICommand CopyDetailsCommand { get; }

        /// <summary>Re-reads the registry. The all-clear state also hides the "copied" line (and only it
        /// does - a refresh while issues remain keeps it, as the old page did).</summary>
        public void Refresh()
        {
            IReadOnlyList<HealthEntry> entries = _snapshot() ?? Array.Empty<HealthEntry>();
            Issues.Clear();
            if (entries.Count == 0)
            {
                IsHealthy = true;
                ShowCopyDone = false;
                return;
            }

            IsHealthy = false;
            foreach (HealthEntry entry in entries)
            {
                string text = Strings.Get("Health.Subsystem." + entry.Subsystem) + ": " + Strings.Get(entry.ImpactKey);
                // THE CASE THE OWNER NAMED EXPLICITLY: a reflection target moved / a SimHub dependency
                // changed - say so plainly rather than showing an opaque failure.
                if (entry.IsSimHubCompatibilityIssue)
                    text += " " + Strings.Get("Health.SimHubUpdateNeeded");
                Issues.Add(new HealthIssueViewModel("⚠ " + text, entry.Severity == HealthSeverity.Failed));
            }
        }

        /// <summary>Every current entry's raw technical detail, deliberately English / unlocalized (it is
        /// pasted into a bug report, not read by the driver), read fresh at click time. A locked clipboard
        /// (another app mid-copy) must never throw out of the click.</summary>
        private void CopyDetails()
        {
            var sb = new StringBuilder();
            foreach (HealthEntry entry in _snapshot() ?? Array.Empty<HealthEntry>())
            {
                sb.Append('[').Append(entry.Severity).Append("] ").Append(entry.Subsystem);
                if (entry.IsSimHubCompatibilityIssue) sb.Append(" (possible SimHub version mismatch)");
                sb.Append(" - first: ").Append(entry.FirstOccurredUtc.ToString("u", CultureInfo.InvariantCulture));
                sb.Append(", last: ").Append(entry.LastOccurredUtc.ToString("u", CultureInfo.InvariantCulture));
                sb.Append(", occurrences: ").Append(entry.OccurrenceCount);
                sb.AppendLine();
                sb.AppendLine(entry.Detail);
                sb.AppendLine();
            }

            try { _clipboard.SetText(sb.ToString()); }
            catch (Exception) { /* clipboard can be locked by another process - never let this throw */ }

            ShowCopyDone = true;
        }
    }
}
