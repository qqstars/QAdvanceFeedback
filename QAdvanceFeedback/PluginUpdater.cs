using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.ViewModels;

namespace QAdvanceFeedback
{
    /// <summary>
    /// AUTO-UPDATE (owner, 2026-11-25, 1.3.0). Modelled on the AZOM plugin's updater.
    /// <list type="bullet">
    /// <item><see cref="CheckInBackground"/> (plugin start and the General tab's "Check For Update"): on a NEW background
    /// thread at below-normal priority, reads the repository's LATEST release from GitHub, downloads its
    /// <c>Release_x.x.x.zip</c>, and unpacks it into a fresh <c>%TEMP%\QAdvanceFeedbackLatest</c> (deleted, then created).
    /// Then it reads the file version of the <c>QAdvanceFeedback.dll</c> found there - any folder depth; the zip may hold
    /// other files - and sets <see cref="UpdateStatusViewModel.Current"/>. A version different from the running one, larger
    /// or smaller, is a new version. Nothing here touches a view: the views bind to that status one-way.</item>
    /// <item><see cref="Apply"/> (the "Update" button): the downloaded DLL is copied next to the running one as
    /// <c>QAdvanceFeedback.dll.new</c>; the running DLL is renamed to <c>.dll.old</c> (Windows allows renaming a loaded
    /// DLL, not overwriting it); the new one takes its name; SimHub restarts. Names that do not end in ".dll" are used for
    /// the staged and old files, so a leftover can never be loaded as a second plugin. The next start deletes both.</item>
    /// </list>
    /// </summary>
    public static class PluginUpdater
    {
        public const string Repository = "qqstars/QAdvanceFeedback";
        public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public const string DllName = "QAdvanceFeedback.dll";
        public const string NewSuffix = ".new";
        public const string OldSuffix = ".old";

        /// <summary>A release package: <c>Release_1.3.0.zip</c> (case ignored).</summary>
        private static readonly Regex PackageName = new Regex(@"^Release_[0-9][0-9A-Za-z.\-]*\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Whether a release asset is the plugin's package (<c>Release_1.3.0.zip</c>).</summary>
        public static bool IsReleasePackage(string assetName) => PackageName.IsMatch(assetName ?? string.Empty);

        private const int TimeoutMs = 30000;
        private static int _running;

        /// <summary>Where the latest release is unpacked.</summary>
        public static string LatestFolder => LatestFolderOverride ?? Path.Combine(Path.GetTempPath(), "QAdvanceFeedbackLatest");

        /// <summary>Tests only: another download folder and another "running" DLL (never the real ones).</summary>
        public static string LatestFolderOverride { get; set; }
        public static string PluginDllPathOverride { get; set; }

        /// <summary>The running plugin DLL.</summary>
        public static string PluginDllPath => PluginDllPathOverride ?? typeof(PluginUpdater).Assembly.Location;

        /// <summary>The running DLL's file version ("unknown" when it cannot be read).</summary>
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    string v = FileVersionInfo.GetVersionInfo(PluginDllPath).FileVersion;
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
                catch (Exception) { }
                return typeof(PluginUpdater).Assembly.GetName().Version?.ToString() ?? "unknown";
            }
        }

        /// <summary>The QAdvanceFeedback.dll in <see cref="LatestFolder"/> (any depth), or null.</summary>
        public static string FindDownloadedDll()
        {
            try
            {
                if (!Directory.Exists(LatestFolder)) return null;
                return Directory.EnumerateFiles(LatestFolder, DllName, SearchOption.AllDirectories).FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        /// <summary>The downloaded DLL's file version, or null.</summary>
        public static string DownloadedVersion()
        {
            string dll = FindDownloadedDll();
            if (dll == null) return null;
            try
            {
                string v = FileVersionInfo.GetVersionInfo(dll).FileVersion;
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }
            catch (Exception) { return null; }
        }

        /// <summary>Shows what is already downloaded (no network) - e.g. at start, before the check finishes.</summary>
        public static void ShowDownloaded() =>
            UpdateStatusViewModel.Current.Set(CurrentVersion, DownloadedVersion(), Volatile.Read(ref _running) != 0, string.Empty);

        /// <summary>
        /// Runs the check on a new background thread (below-normal priority) and returns at once. A check already running
        /// is not started twice.
        /// </summary>
        public static void CheckInBackground(Action<string> logWarning = null)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0) return;
            UpdateStatusViewModel.Current.Set(CurrentVersion, DownloadedVersion(), true, string.Empty);
            var thread = new Thread(() => Run(logWarning))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "QAdvanceFeedback update check",
            };
            thread.Start();
        }

        private static void Run(Action<string> logWarning)
        {
            string message = string.Empty;
            try
            {
                message = DownloadLatest();
            }
            catch (Exception e)
            {
                message = string.Format(Strings.Get("Update.Error.Check"), e.Message);
                logWarning?.Invoke("QAdvanceFeedback: " + message);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                UpdateStatusViewModel.Current.Set(CurrentVersion, DownloadedVersion(), false, message);
            }
        }

        /// <summary>The download itself (the check thread). Returns a message for the page, empty when it went fine.</summary>
        private static string DownloadLatest()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch (Exception) { }

            string json;
            using (var web = NewClient())
            {
                web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = web.DownloadString(LatestReleaseApi);
            }

            JObject release = JObject.Parse(json);
            JToken asset = (release["assets"] as JArray)?
                .FirstOrDefault(a => IsReleasePackage((string)a["name"]));
            string url = (string)asset?["browser_download_url"];
            if (string.IsNullOrEmpty(url) || !IsTrustedUrl(url)) return Strings.Get("Update.Error.NoPackage");

            string zip = Path.Combine(Path.GetTempPath(), "QAdvanceFeedbackLatest.zip");
            try
            {
                using (var web = NewClient()) web.DownloadFile(url, zip);

                // %TEMP%\QAdvanceFeedbackLatest is deleted, then created, then filled.
                if (Directory.Exists(LatestFolder)) Directory.Delete(LatestFolder, recursive: true);
                Directory.CreateDirectory(LatestFolder);
                ExtractSafely(zip, LatestFolder);
            }
            finally
            {
                try { if (File.Exists(zip)) File.Delete(zip); } catch (Exception) { }
            }

            return FindDownloadedDll() == null ? Strings.Get("Update.Error.NoDll") : string.Empty;
        }

        private static WebClient NewClient()
        {
            var web = new TimeoutWebClient(TimeoutMs);
            // GitHub refuses a request without a User-Agent. A generic one: no user or machine detail is sent.
            web.Headers[HttpRequestHeader.UserAgent] = "QAdvanceFeedback-Updater/" + CurrentVersion;
            return web;
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;
            public TimeoutWebClient(int timeoutMs) { _timeoutMs = timeoutMs; }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null) request.Timeout = _timeoutMs;
                return request;
            }
        }

        /// <summary>Only GitHub's own hosts over https.</summary>
        public static bool IsTrustedUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal)
                || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
        }

        /// <summary>Unpacks every entry, refusing one that would land outside <paramref name="folder"/>.</summary>
        public static void ExtractSafely(string zipPath, string folder)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("the package holds an entry outside its folder: " + entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
        }

        // ---------------------------------------------------------------- applying

        /// <summary>
        /// The "Update" button (UI thread): puts the downloaded DLL in place of the running one, then restarts SimHub.
        /// Returns an error message, or null when SimHub is restarting. The running DLL is left as it was on any failure.
        /// </summary>
        /// <param name="restart">Restarts SimHub after the swap (<see cref="SimHubRestarter.Restart"/> in the plugin);
        /// returns an error message, or null when restarting.</param>
        public static string Apply(Func<string> restart, Action<string> logInfo = null)
        {
            string source = FindDownloadedDll();
            if (source == null) return Strings.Get("Update.Error.NoDll");

            string dll = PluginDllPath;
            string staged = dll + NewSuffix;
            string old = dll + OldSuffix;
            bool renamed = false;
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
                File.Copy(source, staged);
                if (File.Exists(old)) File.Delete(old);   // a previous update's leftover (not loaded any more)
                File.Move(dll, old);
                renamed = true;
                File.Move(staged, dll);
            }
            catch (Exception e)
            {
                // Put the running DLL back under its name if the swap stopped half-way.
                if (renamed && !File.Exists(dll))
                    try { File.Move(old, dll); } catch (Exception) { }
                try { if (File.Exists(staged)) File.Delete(staged); } catch (Exception) { }
                return string.Format(Strings.Get("Update.Error.Apply"), e.Message);
            }

            logInfo?.Invoke("QAdvanceFeedback: updated to " + DownloadedVersion() + " - restarting SimHub");
            return restart?.Invoke();
        }

        /// <summary>At start: deletes a previous update's <c>.dll.old</c> / <c>.dll.new</c> (no longer loaded).</summary>
        public static void DeleteLeftovers()
        {
            foreach (string suffix in new[] { OldSuffix, NewSuffix })
            {
                try
                {
                    string path = PluginDllPath + suffix;
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception) { }
            }
        }
    }
}
