using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// The 1.3.0 auto-update (owner, 2026-11-25): the latest GitHub release's Release_x.x.x.zip unpacked into
    /// %TEMP%\QAdvanceFeedbackLatest; a different QAdvanceFeedback.dll version shows the green notice (one-way bound to
    /// one shared status) and the General tab's Update button, which swaps the DLL by rename-aside and restarts SimHub.
    /// </summary>
    [Collection("PluginUpdater")]   // the overrides are static
    public sealed class AutoUpdateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "qaf-update-" + Guid.NewGuid().ToString("N"));

        public AutoUpdateTests()
        {
            Directory.CreateDirectory(_root);
            PluginUpdater.LatestFolderOverride = Path.Combine(_root, "latest");
            PluginUpdater.PluginDllPathOverride = Path.Combine(_root, "simhub", "QAdvanceFeedback.dll");
        }

        public void Dispose()
        {
            PluginUpdater.LatestFolderOverride = null;
            PluginUpdater.PluginDllPathOverride = null;
            UpdateStatusViewModel.Current.Set(string.Empty, null, false, string.Empty);
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ---------------------------------------------------------------- versions and the status

        [Fact]
        public void A_different_version_larger_or_smaller_is_new()
        {
            Assert.True(UpdateStatusViewModel.SameVersion("1.3.0", "1.3.0.0"));
            Assert.True(UpdateStatusViewModel.SameVersion("1.3.0.0", " 1.3.0.0 "));
            Assert.False(UpdateStatusViewModel.SameVersion("1.3.0.1", "1.3.0.0"));
            Assert.False(UpdateStatusViewModel.SameVersion("1.2.2.0", "1.3.0.0"));   // older counts too (no ordering)
        }

        [Fact]
        public void The_status_shows_latest_or_new_only_once_a_dll_was_downloaded()
        {
            UpdateStatusViewModel s = UpdateStatusViewModel.Current;
            s.Set("1.3.0.0", null, false, string.Empty);
            Assert.False(s.HasNewVersion);
            Assert.False(s.IsLatest);
            Assert.Equal(string.Format(Strings.Get("General.Version.Label"), "1.3.0.0"), s.VersionLine);   // no addition
            Assert.Equal(string.Empty, s.NewVersionText);

            s.Set("1.3.0.0", "1.3.0.0", false, string.Empty);
            Assert.True(s.IsLatest);
            Assert.EndsWith(Strings.Get("Update.Latest"), s.VersionLine);
            Assert.False(s.HasNewVersion);

            s.Set("1.3.0.0", "1.3.1.0", false, string.Empty);
            Assert.True(s.HasNewVersion);
            Assert.False(s.IsLatest);
            Assert.Equal(string.Format(Strings.Get("Update.NewVersion"), "1.3.1.0"), s.NewVersionText);
            Assert.Equal(string.Format(Strings.Get("Update.Notice.Line1"), "1.3.1.0"), s.NoticeLine1);   // package unknown
            s.Set("1.3.0.0", "1.3.1.0", false, string.Empty, "QAdvanceFeedback_1.3.1.zip");
            Assert.Equal("QAdvanceFeedback_1.3.1.zip", s.PackageName);
            Assert.Equal(string.Format(Strings.Get("Update.Notice.Line1.Package"), "1.3.1.0", "QAdvanceFeedback_1.3.1.zip"), s.NoticeLine1);
            Assert.Equal("New version 1.3.1.0 detected (QAdvanceFeedback_1.3.1.zip).",
                string.Format(StringTableEn.Create()["Update.Notice.Line1.Package"], "1.3.1.0", "QAdvanceFeedback_1.3.1.zip"));
            Assert.Equal("New version 1.3.1.0 detected.", string.Format(StringTableEn.Create()["Update.Notice.Line1"], "1.3.1.0"));
        }

        [Fact]
        public void The_check_thread_only_sets_the_view_model_and_the_bindings_hear_it()
        {
            UpdateStatusViewModel s = UpdateStatusViewModel.Current;
            var heard = new List<string>();
            void Handler(object o, System.ComponentModel.PropertyChangedEventArgs e) { lock (heard) heard.Add(e.PropertyName); }
            s.PropertyChanged += Handler;
            try
            {
                Task.Run(() => s.Set("1.3.0.0", "1.4.0.0", false, string.Empty)).Wait();
            }
            finally { s.PropertyChanged -= Handler; }
            Assert.Contains(nameof(UpdateStatusViewModel.HasNewVersion), heard);
            Assert.Contains(nameof(UpdateStatusViewModel.NoticeLine1), heard);
            Assert.Contains(nameof(UpdateStatusViewModel.VersionLine), heard);
        }

        // ---------------------------------------------------------------- the package

        [Fact]
        public void Only_a_Release_zip_from_GitHub_over_https_is_used()
        {
            foreach (string ok in new[]
            {
                "Release_1.3.0.zip", "release_1.3.0.1.ZIP", "Release 1.3.0.zip", "Release.1.3.0.zip",
                "QAdvanceFeedback_1.2.1.zip", "qadvancefeedback 1.3.0.0.zip", "QAdvanceFeedback.1.3.0.zip",
            })
                Assert.True(PluginUpdater.IsReleasePackage(ok), ok);
            foreach (string no in new[]
            {
                "Source code.zip", "Release_notes.txt", "Release_1.3.zip", "Release_1.3.0.0.0.zip", "Release-1.3.0.zip",
                "QAdvanceFeedback_1.3.0.rar", "QAdvanceFeedback.Tests.dll", null,
                // Never the bare DLL (owner: Windows Defender reports a downloaded DLL as a virus).
                "QAdvanceFeedback.dll", "qadvancefeedback.DLL",
            })
                Assert.False(PluginUpdater.IsReleasePackage(no), no ?? "null");

            // The first zip in the release's order; a release with only the bare DLL has no package.
            var assets = new Newtonsoft.Json.Linq.JArray(
                new Newtonsoft.Json.Linq.JObject { ["name"] = "QAdvanceFeedback.dll" },
                new Newtonsoft.Json.Linq.JObject { ["name"] = "Release 1.3.0.zip" },
                new Newtonsoft.Json.Linq.JObject { ["name"] = "QAdvanceFeedback_1.3.0.zip" });
            Assert.Equal("Release 1.3.0.zip", (string)PluginUpdater.PickPackage(assets)["name"]);
            Assert.Null(PluginUpdater.PickPackage(new Newtonsoft.Json.Linq.JArray(assets[0])));
            Assert.Null(PluginUpdater.PickPackage(new Newtonsoft.Json.Linq.JArray()));

            Assert.True(PluginUpdater.IsTrustedUrl("https://github.com/qqstars/QAdvanceFeedback/releases/download/v1.3.0/Release_1.3.0.zip"));
            Assert.True(PluginUpdater.IsTrustedUrl("https://objects.githubusercontent.com/x"));
            Assert.False(PluginUpdater.IsTrustedUrl("http://github.com/x"));
            Assert.False(PluginUpdater.IsTrustedUrl("https://github.com.evil.example/x"));
            Assert.False(PluginUpdater.IsTrustedUrl("not a url"));
        }

        private string Zip(params (string name, byte[] data)[] entries)
        {
            string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach ((string name, byte[] data) in entries)
                    using (Stream s = zip.CreateEntry(name).Open()) s.Write(data, 0, data.Length);
            return path;
        }

        [Fact]
        public void The_package_is_unpacked_with_its_other_files_and_the_dll_is_found_at_any_depth()
        {
            byte[] dll = File.ReadAllBytes(typeof(AutoUpdateTests).Assembly.Location);   // a real file with a version
            string zip = Zip(("readme.txt", new byte[] { 1 }), ("Release_1.3.0/QAdvanceFeedback.dll", dll));
            Directory.CreateDirectory(PluginUpdater.LatestFolder);
            PluginUpdater.ExtractSafely(zip, PluginUpdater.LatestFolder);
            Assert.True(File.Exists(Path.Combine(PluginUpdater.LatestFolder, "readme.txt")));
            Assert.EndsWith(Path.Combine("Release_1.3.0", "QAdvanceFeedback.dll"), PluginUpdater.FindDownloadedDll());
            Assert.False(string.IsNullOrEmpty(PluginUpdater.DownloadedVersion()));
        }

        [Fact]
        public void An_entry_outside_the_folder_is_refused()
        {
            string zip = Zip(("../escape.txt", new byte[] { 1 }));
            Directory.CreateDirectory(PluginUpdater.LatestFolder);
            Assert.Throws<IOException>(() => PluginUpdater.ExtractSafely(zip, PluginUpdater.LatestFolder));
            Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        }

        // ---------------------------------------------------------------- applying

        [Fact]
        public void Update_swaps_the_dll_by_rename_aside_then_restarts()
        {
            string running = PluginUpdater.PluginDllPath;
            Directory.CreateDirectory(Path.GetDirectoryName(running));
            File.WriteAllBytes(running, new byte[] { 1, 1, 1 });
            Directory.CreateDirectory(PluginUpdater.LatestFolder);
            File.WriteAllBytes(Path.Combine(PluginUpdater.LatestFolder, "QAdvanceFeedback.dll"), new byte[] { 2, 2, 2, 2 });

            int restarts = 0;
            string error = PluginUpdater.Apply(() => { restarts++; return null; });
            Assert.Null(error);
            Assert.Equal(1, restarts);
            Assert.Equal(new byte[] { 2, 2, 2, 2 }, File.ReadAllBytes(running));                      // the new one in place
            Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(running + PluginUpdater.OldSuffix)); // the old one aside
            Assert.False(File.Exists(running + PluginUpdater.NewSuffix));

            PluginUpdater.DeleteLeftovers();   // the next start
            Assert.False(File.Exists(running + PluginUpdater.OldSuffix));
            Assert.True(File.Exists(running));
        }

        [Fact]
        public void With_nothing_downloaded_Update_changes_nothing_and_says_why()
        {
            string running = PluginUpdater.PluginDllPath;
            Directory.CreateDirectory(Path.GetDirectoryName(running));
            File.WriteAllBytes(running, new byte[] { 1 });
            int restarts = 0;
            string error = PluginUpdater.Apply(() => { restarts++; return null; });
            Assert.Equal(Strings.Get("Update.Error.NoDll"), error);
            Assert.Equal(0, restarts);
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(running));
        }

        // ---------------------------------------------------------------- the views (MVVM, one-way)

        private static string View(string name) =>
            File.ReadAllText(Path.Combine(RepoRoot(), "QAdvanceFeedback", "Ui", "Views", name + ".xaml"));

        [Fact]
        public void The_notice_binds_one_way_to_the_shared_status_and_is_on_every_tab_but_General_and_every_panel()
        {
            string bar = View("UpdateNoticeBar");
            Assert.Contains("DataContext=\"{x:Static vm:UpdateStatusViewModel.Current}\"", bar);
            Assert.Contains("Visibility=\"{Binding HasNewVersion, Mode=OneWay, Converter={StaticResource VisibleWhen}}\"", bar);
            Assert.Contains("Text=\"{Binding NoticeLine1, Mode=OneWay}\"", bar);
            Assert.Contains("Text=\"{Binding NoticeLine2, Mode=OneWay}\"", bar);
            Assert.Contains("FontSize=\"11\"", bar);   // the second row is smaller

            string page = View("SettingsPageView");
            foreach (string tab in new[] { "WheelLockTab", "WheelSlipTab", "SlipAngleTab", "GForceTab" })
            {
                int at = page.IndexOf("x:Name=\"" + tab + "\"", StringComparison.Ordinal);
                int end = page.IndexOf("</TabItem>", at, StringComparison.Ordinal);
                Assert.Contains("<v:UpdateNoticeBar", page.Substring(at, end - at));
            }
            int general = page.IndexOf("x:Name=\"GeneralTab\"", StringComparison.Ordinal);
            Assert.DoesNotContain("UpdateNoticeBar", page.Substring(general, page.IndexOf("</TabItem>", general, StringComparison.Ordinal) - general));
            foreach (string panel in new[] { "WheelChannelEffectView", "GForceEffectView", "SlipAngleEffectView", "ClutchBiteView" })
                Assert.Contains("<v:UpdateNoticeBar/>", View(panel));   // ShakeIt panels (Clutch's also on its tab)
        }

        [Fact]
        public void The_General_tab_has_VERSION_UPDATE_on_top_with_one_way_bindings()
        {
            string general = View("GeneralPageView");
            Assert.True(general.IndexOf("VersionUpdateGroup", StringComparison.Ordinal) < general.IndexOf("GeneralGroup", StringComparison.Ordinal));
            Assert.Contains("Text=\"{Binding Update.VersionLine, Mode=OneWay}\"", general);
            Assert.Contains("Text=\"{Binding Update.NewVersionText, Mode=OneWay}\"", general);
            Assert.Contains("Command=\"{Binding CheckForUpdateCommand}\"", general);
            Assert.Contains("Command=\"{Binding UpdateCommand}\"", general);
            Assert.Contains("Visibility=\"{Binding Update.HasNewVersion, Mode=OneWay, Converter={StaticResource VisibleWhen}}\"", general);
            Assert.Equal(1, Count(general, "x:Name=\"GeneralVersionText\""));   // moved, not duplicated
        }

        private static int Count(string text, string part)
        {
            int n = 0;
            for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
            return n;
        }

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "QAdvanceFeedback.sln"))) dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("repo root not found");
        }
    }
}
