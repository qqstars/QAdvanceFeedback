using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The page's own Apply button writes the Slip Angle tab's per-wheel telemetry source into the
    /// QAdvanceFeedback.SlipAngleSource.json in PluginsData\Common (owner, 2026-11-23) - end to end through
    /// <see cref="SettingsPageViewModel.ApplyCommand"/>, with the file checked on disk.
    /// </summary>
    public sealed class SlipAngleSourceApplyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "qaf-saa-" + Guid.NewGuid().ToString("N"));
        private readonly FakePluginPageHost _host = new FakePluginPageHost();
        private readonly SlipAngleSourceStore _store;

        public SlipAngleSourceApplyTests()
        {
            Directory.CreateDirectory(_dir);
            _store = new SlipAngleSourceStore(Path.Combine(_dir, SlipAngleSourceStore.FileName), use: _ => { });
            _store.Load();
            _host.NativeSources = _store;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private SettingsPageViewModel NewPage(string game)
        {
            _host.CurrentGameId = game;
            var page = new SettingsPageViewModel(_host, new ImmediateDispatcher(), new ManualTicker(), new FakeDialogService(), new FakeClipboard());
            page.SlipAngleTab.Activate();
            return page;
        }

        private JToken FileRow(string id) =>
            ((JArray)JObject.Parse(File.ReadAllText(_store.Path))["games"]).SingleOrDefault(g => (string)g["id"] == id);

        [Fact]
        public void Apply_writes_a_new_games_source_into_the_common_json()
        {
            SettingsPageViewModel page = NewPage("BrandNewSim");
            Assert.Null(FileRow("BrandNewSim"));
            page.SlipAngleTab.TelemetryType = SlipAngleTelemetryType.AngleOnly;
            string[] paths = { "DataCorePlugin.GameRawData.Sa.FL", "DataCorePlugin.GameRawData.Sa.FR", "DataCorePlugin.GameRawData.Sa.RL", "DataCorePlugin.GameRawData.Sa.RR" };
            for (int i = 0; i < 4; i++) page.SlipAngleTab.AngleRows[i].Text = paths[i];

            Assert.True(page.IsDirty);                         // a telemetry-only edit enables Apply
            Assert.True(page.ApplyCommand.CanExecute(null));
            page.ApplyCommand.Execute(null);

            Assert.Equal(paths, FileRow("BrandNewSim")["slipAngle"].ToObject<string[]>());
            Assert.Null(FileRow("BrandNewSim")["unit"]);       // no unit: Auto-detect
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Apply_updates_a_shipped_games_source_in_the_common_json_and_restore_puts_it_back()
        {
            SettingsPageViewModel page = NewPage("AssettoCorsaEVO");
            page.SlipAngleTab.AngleRows[2].Text = "DataCorePlugin.GameRawData.Mine.RL";
            page.ApplyCommand.Execute(null);
            Assert.Equal("DataCorePlugin.GameRawData.Mine.RL", (string)FileRow("AssettoCorsaEVO")["slipAngle"][2]);

            Assert.True(page.SlipAngleTab.ShowTelemetryRestore);
            Assert.True(page.SlipAngleTab.RestoreTelemetryCommand.CanExecute(null));
            page.SlipAngleTab.RestoreTelemetryCommand.Execute(null);     // written at once, no Apply (owner, 2026-11-24)
            Assert.False(page.IsDirty);
            Assert.False(page.SlipAngleTab.HasPendingTelemetry);
            Assert.Null(FileRow("AssettoCorsaEVO"));                     // merged back into the shared row
            Assert.Equal("DataCorePlugin.GameRawData.Physics.slipAngle03", (string)FileRow("ACC|AssettoCorsaCompetizione|AssettoCorsaRally|AssettoCorsaEVO")["slipAngle"][2]);
            Assert.False(_store.CanRestore("AssettoCorsaEVO"));
            Assert.False(page.SlipAngleTab.RestoreTelemetryCommand.CanExecute(null));
        }

        [Fact]
        public void Restore_drops_a_typed_edit_and_leaves_nothing_for_Apply()
        {
            SettingsPageViewModel page = NewPage("AssettoCorsaEVO");
            string text = File.ReadAllText(_store.Path);
            page.SlipAngleTab.AngleRows[2].Text = "DataCorePlugin.GameRawData.Mine.RL";
            Assert.True(page.IsDirty);
            page.SlipAngleTab.RestoreTelemetryCommand.Execute(null);
            Assert.False(page.SlipAngleTab.HasPendingTelemetry);
            Assert.False(page.IsDirty);
            Assert.Equal(text, File.ReadAllText(_store.Path));
        }

        [Fact]
        public void Apply_with_nothing_changed_leaves_the_file_alone()
        {
            SettingsPageViewModel page = NewPage("AssettoCorsaEVO");
            DateTime before = File.GetLastWriteTimeUtc(_store.Path);
            string text = File.ReadAllText(_store.Path);
            page.SlipAngleTab.WeightMax = 0.9;                 // some other edit
            page.ApplyCommand.Execute(null);
            Assert.Equal(text, File.ReadAllText(_store.Path));
            Assert.Equal(before, File.GetLastWriteTimeUtc(_store.Path));
        }
    }
}
