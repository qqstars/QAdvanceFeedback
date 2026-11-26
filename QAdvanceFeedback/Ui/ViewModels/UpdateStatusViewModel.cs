using System;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.Mvvm;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// THE UPDATE STATUS (owner, 2026-11-25, 1.3.0) - ONE instance (<see cref="Current"/>) every view binds to ONE-WAY:
    /// the green "new version" notice on top of each settings tab (except General) and each ShakeIt panel, and the
    /// General tab's VERSION UPDATE section. The update check runs on its own thread and only calls <see cref="Set"/>;
    /// WPF delivers the property changes to the bound views (no view or dispatcher is touched by the check).
    /// </summary>
    public sealed class UpdateStatusViewModel : ObservableObject
    {
        public static UpdateStatusViewModel Current { get; } = new UpdateStatusViewModel();

        private string _currentVersion = string.Empty;
        private string _latestVersion;
        private bool _isChecking;
        private string _message = string.Empty;
        private string _packageName;

        /// <summary>The release file the downloaded DLL came from (e.g. <c>QAdvanceFeedback_1.3.0.zip</c>); null when unknown.</summary>
        public string PackageName => _packageName;

        /// <summary>The running plugin DLL's file version.</summary>
        public string CurrentVersion => _currentVersion;

        /// <summary>The file version of the QAdvanceFeedback.dll in %TEMP%\QAdvanceFeedbackLatest; null when there is none.</summary>
        public string LatestVersion => _latestVersion;

        /// <summary>A check is running (the Check button is disabled meanwhile).</summary>
        public bool IsChecking => _isChecking;

        public bool IsNotChecking => !_isChecking;

        /// <summary>The last check's problem (no network, no package...), empty when it went fine.</summary>
        public string Message => _message;

        public bool HasMessage => !string.IsNullOrEmpty(_message);

        /// <summary>A downloaded DLL exists and its version differs from the running one (larger or smaller alike).</summary>
        public bool HasNewVersion => !string.IsNullOrEmpty(_latestVersion) && !SameVersion(_latestVersion, _currentVersion);

        /// <summary>A downloaded DLL exists and its version is the running one.</summary>
        public bool IsLatest => !string.IsNullOrEmpty(_latestVersion) && SameVersion(_latestVersion, _currentVersion);

        /// <summary>"QAdvanceFeedback version: x.x.x.x" plus " (latest)" when the downloaded DLL is this version.</summary>
        public string VersionLine =>
            string.Format(Strings.Get("General.Version.Label"), string.IsNullOrEmpty(_currentVersion) ? "unknown" : _currentVersion)
            + (IsLatest ? Strings.Get("Update.Latest") : string.Empty);

        /// <summary>" (new version: x.x.x.x)" - shown in green beside <see cref="VersionLine"/> while <see cref="HasNewVersion"/>.</summary>
        public string NewVersionText => HasNewVersion ? string.Format(Strings.Get("Update.NewVersion"), _latestVersion) : string.Empty;

        /// <summary>The notice's first line: "New version x.x.x.x detected (QAdvanceFeedback_x.x.x.zip)." - the package name
        /// only when it is known (owner, 2026-11-25).</summary>
        public string NoticeLine1 => !HasNewVersion ? string.Empty
            : string.IsNullOrEmpty(_packageName) ? string.Format(Strings.Get("Update.Notice.Line1"), _latestVersion)
            : string.Format(Strings.Get("Update.Notice.Line1.Package"), _latestVersion, _packageName);

        /// <summary>The notice's second (smaller) line: where to update.</summary>
        public string NoticeLine2 => Strings.Get("Update.Notice.Line2");

        /// <summary>
        /// Sets the state (any thread) and raises every derived property. <paramref name="latestVersion"/> null: no
        /// downloaded DLL.
        /// </summary>
        public void Set(string currentVersion, string latestVersion, bool isChecking, string message, string packageName = null)
        {
            _packageName = string.IsNullOrWhiteSpace(packageName) ? null : packageName.Trim();
            _currentVersion = currentVersion ?? string.Empty;
            _latestVersion = string.IsNullOrEmpty(latestVersion) ? null : latestVersion;
            _isChecking = isChecking;
            _message = message ?? string.Empty;
            foreach (string name in new[]
            {
                nameof(CurrentVersion), nameof(LatestVersion), nameof(PackageName), nameof(IsChecking), nameof(IsNotChecking), nameof(Message),
                nameof(HasMessage), nameof(HasNewVersion), nameof(IsLatest), nameof(VersionLine), nameof(NewVersionText),
                nameof(NoticeLine1), nameof(NoticeLine2),
            })
                OnPropertyChanged(name);
        }

        /// <summary>Re-raises the texts (the language changed).</summary>
        public void RefreshTexts() => Set(_currentVersion, _latestVersion, _isChecking, _message, _packageName);

        /// <summary>Two versions are the same when they parse to the same <see cref="Version"/> (1.3.0 = 1.3.0.0), or are
        /// the same text otherwise.</summary>
        public static bool SameVersion(string a, string b)
        {
            if (Version.TryParse(Normalise(a), out Version va) && Version.TryParse(Normalise(b), out Version vb))
                return va.Equals(vb);
            return string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalise(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return v;
            string t = v.Trim();
            int cut = t.IndexOfAny(new[] { ' ', '+', '-' });
            if (cut > 0) t = t.Substring(0, cut);
            string[] parts = t.Split('.');
            if (parts.Length == 1) t += ".0.0.0";
            else if (parts.Length == 2) t += ".0.0";
            else if (parts.Length == 3) t += ".0";
            return t;
        }
    }
}
