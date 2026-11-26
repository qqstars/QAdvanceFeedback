using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Core.Viper;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui.Page
{
    /// <summary>
    /// The plugin as the whole page sees it in Wave 2 (Integration\ShakeIt\PluginPageHost implements
    /// <see cref="IPluginSettingsPageHost"/>): <see cref="FakeSettingsPageHost"/> - real settings, real hub,
    /// the plugin's own Apply broadcast - plus the Slip Angle, Clutch and wheel-source hosts, so the page view
    /// model builds every tab.
    /// </summary>
    public sealed class FakePluginPageHost : FakeSettingsPageHost, ISlipAngleTabHost, IClutchHost, IWheelChannelPageHost
    {
        public FakePluginPageHost(QAdvanceFeedbackSettings settings = null) : base(settings ?? QAdvanceFeedbackSettings.CreateDefault()) { }

        /// <summary>Order of the plugin calls the page made ("apply", "viper-save").</summary>
        public List<string> Calls { get; } = new List<string>();

        // ---- ISlipAngleTabHost ----
        public SlipAngleStatus Status { get; set; } = SlipAngleStatus.None;
        public SourceNativeness IdentityTier { get; set; } = SourceNativeness.Native;
        public SlipAngleWindow? LearnedWindow(string sourceIdentity) => null;
        public void SaveLiveSettings() => ApplySettings();
        public SlipAngleSourceStore NativeSources { get; set; }

        // ---- IWheelChannelPageHost (+ the Slip Angle tab's picker / editor) ----
        public bool EvaluatorAvailable { get; set; } = true;
        public SourceButtonMode ResolveSourceButton(ScriptType scriptType) => SourceButtonMode.Picker;
        public Task<SourceEdit> EditSourceAsync(SourceButtonMode mode, string text, ScriptType scriptType) => Task.FromResult<SourceEdit>(null);
        public bool ShakeItSourceBlocked(WheelChannelSettings channel, bool isLockChannel) => false;
        public bool ViperSourceBlocked(WheelChannelSettings channel, string gameId)
            => channel != null && channel.SourceMode == SourceMode.Viper && !string.IsNullOrWhiteSpace(gameId)
               && !ViperSupportedGames.IsSupported(gameId, ViperDocument.SupportedGames);
        private ViperDocument _viper = ViperSupportedGames.CreateShippedDocument();
        public ViperDocument ViperDocument { get => _viper; set { if (value != null) _viper = value; } }
        public bool SaveViperDocument() { Calls.Add("viper-save:" + ApplyCalls); return true; }

        // ---- IClutchHost ----
        public ClutchHostStatus ClutchStatus { get; } = new ClutchHostStatus { Final = 50 };
        public IReadOnlyList<string> ControllerAxes() => new string[0];
        public double? ReadAxis01(string name) => null;
        public void ForgetClutchBite() { }
        public void PublishClutchTest(double? output0100) { }
        public double FinalBiteForTest => 50;
        public string FinalBiteForTestKey => null;
    }

    /// <summary>
    /// Wave 2 (docs\full-mvvm-plan.md §4 Wave 2, §5 Acceptance): the page view model wired the way the plugin
    /// builds it, end to end - the acceptance tests of the plan, the Slip Angle / Clutch tabs under the page,
    /// the cross-tab rules, and the page views' structure that the old source-text tests pinned on the old
    /// SettingsControl.xaml.
    /// </summary>
    public class SettingsPageIntegrationTests
    {
        private readonly FakePluginPageHost _host = new FakePluginPageHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly FakeDialogService _dialogs = new FakeDialogService();

        private SettingsPageViewModel NewPage() =>
            new SettingsPageViewModel(_host, new ImmediateDispatcher(), _ticker, _dialogs, new FakeClipboard());

        private WheelChannelEffectViewModel SlipPanel()
        {
            var vm = new WheelChannelEffectViewModel(false, new FakeRouting(), _host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        private static string Json(object o) => JsonConvert.SerializeObject(o);

        // ================================================================ §5 acceptance

        [Fact]
        public void Acceptance_a_Slip_source_changed_on_the_ShakeIt_panel_before_the_page_was_ever_shown_is_shown()
        {
            // THE ORIGINAL DEFECT, through the real ShakeIt panel view model and the real Slip tab.
            SettingsPageViewModel page = NewPage();
            Assert.NotEqual(SourceMode.Custom, page.SlipTab.Sources.Mode);

            SlipPanel().Source = SourceMode.Custom;

            Assert.Equal(SourceMode.Custom, page.SlipTab.Sources.Mode);
            Assert.Equal(Json(_host.Settings.Slip), Json(page.Context.Settings.Slip));
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Acceptance_the_same_with_the_page_shown_and_with_the_page_hidden()
        {
            SettingsPageViewModel page = NewPage();
            WheelChannelEffectViewModel panel = SlipPanel();

            page.Activate();
            page.SlipTab.Activate();
            panel.Source = SourceMode.Viper;
            Assert.Equal(SourceMode.Viper, page.SlipTab.Sources.Mode);

            page.SlipTab.Deactivate();
            page.Deactivate();
            panel.Source = SourceMode.Custom;
            Assert.Equal(SourceMode.Custom, page.SlipTab.Sources.Mode);

            page.Activate();   // shown again: nothing more to catch up, nothing dirty
            Assert.Equal(SourceMode.Custom, page.SlipTab.Sources.Mode);
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Acceptance_an_unapplied_edit_of_another_field_is_kept_and_of_the_same_field_is_replaced()
        {
            SettingsPageViewModel page = NewPage();
            page.LockTab.Trigger.BrakeThreshold = 33;                 // another field: kept
            page.SlipTab.Sources.Mode = SourceMode.Viper;             // the same field: replaced
            Assert.True(page.IsDirty);

            SlipPanel().Source = SourceMode.Custom;

            Assert.Equal(SourceMode.Custom, page.SlipTab.Sources.Mode);
            Assert.Equal(33, page.LockTab.Trigger.BrakeThreshold);
            Assert.True(page.IsDirty);

            page.ApplyCommand.Execute(null);
            Assert.Equal(SourceMode.Custom, _host.Settings.Slip.SourceMode);   // Apply never reverts it
            Assert.Equal(33, _host.Settings.Lock.BrakeThresholdPercent);
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Acceptance_the_owners_example_source_switched_on_ShakeIt_keeps_the_pending_accel_mode_edit()
        {
            // Ported from PageFieldRefreshPlanTests.Unapplied_edit_survives_an_external_source_change: the page
            // has UNAPPLIED edits to the Lock source and to Accel max mode; ShakeIt changes the source. The page
            // takes the new source, keeps its Accel edit, and Apply then changes Accel only.
            SettingsPageViewModel page = NewPage();
            page.GForceTab.AccelMax.Auto = !page.GForceTab.AccelMax.Auto;
            page.LockTab.Sources.Mode = SourceMode.Viper;

            _host.Hub.Set(SettingField.LockSourceMode, SourceMode.ShakeIt, SettingsOrigin.ShakeItPanel);
            QAdvanceFeedbackSettings beforeApply = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(Json(_host.Settings));
            Assert.Equal(SourceMode.ShakeIt, page.LockTab.Sources.Mode);
            Assert.Equal(_host.Settings.Lock.SourceFrontLeft, page.Context.Settings.Lock.SourceFrontLeft);

            var raised = new List<SettingsChangedEventArgs>();
            _host.Hub.Changed += (s, e) => raised.Add(e);
            page.ApplyCommand.Execute(null);

            Assert.Equal(new[] { SettingField.AccelMaxMode }, SettingsHub.Diff(beforeApply, _host.Settings).ToArray());
            Assert.Equal(SourceMode.ShakeIt, _host.Settings.Lock.SourceMode);
            // One broadcast - the plugin's own (ruling: the page no longer broadcasts a second time).
            Assert.Single(raised);
            Assert.Equal(SettingsOrigin.PluginPage, raised[0].Origin);
            Assert.Equal(new[] { SettingField.AccelMaxMode }, raised[0].Fields);
        }

        [Fact]
        public void Acceptance_a_page_switch_and_a_panel_switch_produce_identical_settings()
        {
            foreach (SourceMode mode in new[] { SourceMode.Custom, SourceMode.Viper, SourceMode.ShakeIt, SourceMode.Manual })
            {
                var viaPanel = new FakePluginPageHost();
                var panel = new WheelChannelEffectViewModel(false, new FakeRouting(), viaPanel, _ticker, new ImmediateDispatcher());
                panel.Activate();
                panel.Source = SourceMode.Viper;
                panel.Source = mode;

                var viaPage = new FakePluginPageHost();
                var page = new SettingsPageViewModel(viaPage, new ImmediateDispatcher(), new ManualTicker(), new FakeDialogService(), new FakeClipboard());
                page.SlipTab.Sources.Mode = SourceMode.Viper;
                page.SlipTab.Sources.Mode = mode;
                page.ApplyCommand.Execute(null);

                Assert.Equal(Json(viaPanel.Settings), Json(viaPage.Settings));
            }
        }

        // ================================================================ the tabs the page now owns

        [Fact]
        public void The_page_builds_the_Slip_Angle_and_Clutch_tabs_from_the_plugin_host_and_the_clutch_shows_learning()
        {
            SettingsPageViewModel page = NewPage();
            Assert.NotNull(page.SlipAngleTab);
            Assert.NotNull(page.ClutchTab);
            // Ported from ClutchLearningParametersTests.Only_the_page_creates_the_view_model_with_learning_parameters:
            // the learning parameters exist on the plugin page only.
            Assert.True(page.ClutchTab.ShowLearning);
            Assert.NotNull(page.ClutchTab.LearningSection);

            // A host that is neither: the tabs are absent, never a crash.
            var bare = new SettingsPageViewModel(new FakeSettingsPageHost(), new ImmediateDispatcher(), _ticker, _dialogs, new FakeClipboard());
            Assert.Null(bare.SlipAngleTab);
            Assert.Null(bare.ClutchTab);
        }

        [Fact]
        public void A_Slip_Angle_edit_goes_through_the_draft_dirties_the_page_and_applies_on_Apply()
        {
            SettingsPageViewModel page = NewPage();
            page.SlipAngleTab.BrakeThreshold = 37;

            Assert.True(page.IsDirty);
            Assert.Equal(37, page.Context.Settings.SlipAngle.Gate.BrakeThreshold);
            Assert.NotEqual(37, _host.Settings.SlipAngle.Gate.BrakeThreshold);   // not applied yet

            page.ApplyCommand.Execute(null);
            Assert.Equal(37, _host.Settings.SlipAngle.Gate.BrakeThreshold);
            Assert.False(page.IsDirty);

            // Back to the applied value: clean again (the draft compares leaves, not "was touched").
            page.SlipAngleTab.BrakeThreshold = 40;
            page.SlipAngleTab.BrakeThreshold = 37;
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void A_ShakeIt_side_Slip_Angle_change_reaches_the_tab_and_keeps_its_other_unapplied_edit()
        {
            SettingsPageViewModel page = NewPage();
            page.SlipAngleTab.ThrottleThreshold = 44;

            _host.Hub.Set(SettingField.SlipAngleTriggerBy, TriggerBy.Brake, SettingsOrigin.ShakeItPanel);

            Assert.Equal(TriggerBy.Brake, page.SlipAngleTab.TriggerBy);
            Assert.Equal(44, page.SlipAngleTab.ThrottleThreshold);
            Assert.True(page.IsDirty);
            page.ApplyCommand.Execute(null);
            Assert.Equal(TriggerBy.Brake, _host.Settings.SlipAngle.Gate.TriggerBy);
            Assert.Equal(44, _host.Settings.SlipAngle.Gate.ThrottleThreshold);
        }

        [Fact]
        public void Restore_defaults_reloads_the_Slip_Angle_tab()
        {
            _host.Settings.SlipAngle.Gate.BrakeThreshold = 3;
            SettingsPageViewModel page = NewPage();
            Assert.Equal(3, page.SlipAngleTab.BrakeThreshold);

            page.RestoreDefaultsCommand.Execute(null);

            double shipped = QAdvanceFeedbackSettings.CreateDefault().SlipAngle.Gate.BrakeThreshold;
            Assert.Equal(shipped, page.SlipAngleTab.BrakeThreshold);
            Assert.Equal(shipped, _host.Settings.SlipAngle.Gate.BrakeThreshold);
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void Restore_defaults_puts_the_global_source_type_mode_back_to_per_wheel()
        {
            SettingsPageViewModel page = NewPage();
            page.SlipAngleTab.AngleSource = SlipAngleAngleSource.CarAngle;
            page.ApplyCommand.Execute(null);
            Assert.Equal(SlipAngleAngleSource.CarAngle, _host.Settings.SlipAngle.AngleSource);   // applied (and saved)

            page.RestoreDefaultsCommand.Execute(null);

            Assert.Equal(SlipAngleAngleSource.PerWheelWhenAvailable, page.SlipAngleTab.AngleSource);
            Assert.Equal(SlipAngleAngleSource.PerWheelWhenAvailable, _host.Settings.SlipAngle.AngleSource);
            Assert.False(page.IsDirty);
        }

        [Fact]
        public void The_Slip_Angle_key_data_applies_at_once_and_never_dirties_the_page()
        {
            SettingsPageViewModel page = NewPage();
            page.SlipAngleTab.Activate();
            int applies = _host.ApplyCalls;

            page.SlipAngleTab.Auto = !page.SlipAngleTab.Auto;

            Assert.True(_host.ApplyCalls > applies);
            Assert.False(page.IsDirty);
        }

        // ================================================================ cross-tab rules

        [Fact]
        public void The_Viper_game_button_on_either_tab_writes_both_tabs_pending_key_data_first()
        {
            // Old page: FlushKeyDataPersistNow before the supported-game list changed - a pending key data
            // edit belongs to the source being left. The list is not per channel, so BOTH tabs flush.
            _host.CurrentGameId = "SomeUnshippedTitle";
            SettingsPageViewModel page = NewPage();
            page.LockTab.KeyData.Auto = false;
            page.LockTab.KeyData.SMax = 90.0;          // a pending (700 ms) save on the Lock tab
            page.LockTab.KeyData.S90 = 80.0;
            page.LockTab.KeyData.S75 = 70.0;
            page.SlipTab.Sources.Mode = SourceMode.Viper;
            page.ApplyCommand.Execute(null);
            page.LockTab.KeyData.SMax = 91.0;          // pending again, after the Apply
            Assert.Equal(string.Empty, page.LockTab.KeyData.Status);   // a valid triple, written to the slot
            Assert.True(page.SlipTab.Sources.ShowViperGameButton);
            int before = _host.ApplyCalls;

            page.SlipTab.Sources.ToggleViperGameCommand.Execute(null);

            Assert.Equal("viper-save:" + (before + 1), _host.Calls.Last());   // flushed BEFORE the list was saved
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(before + 1, _host.ApplyCalls);                        // and not saved a second time
            Assert.False(page.IsDirty);                                        // the button never dirties Apply
        }

        [Fact]
        public void The_Wheel_tab_builds_A2s_sections_and_the_curve_follows_its_key_data()
        {
            SettingsPageViewModel page = NewPage();
            WheelChannelPageViewModel lockTab = page.LockTab;
            lockTab.KeyData.Auto = false;
            IReadOnlyList<GraphPoint> before = lockTab.Curve.SourceGraphCurve;

            lockTab.KeyData.SMax = 30.0;

            Assert.NotEqual(Json(before), Json(lockTab.Curve.SourceGraphCurve));   // Curve.Attach(KeyData)
        }

        [Fact]
        public void A_draft_reload_raises_SourceChanged()
        {
            SettingsPageViewModel page = NewPage();
            int changed = 0;
            page.SlipTab.Sources.SourceChanged += (s, e) => changed++;
            _host.Hub.Set(SettingField.SlipSourceMode, SourceMode.Custom, SettingsOrigin.ShakeItPanel);
            Assert.True(changed > 0);
        }

        [Fact]
        public void Viper_availability_is_re_sampled_on_the_plugin_push_not_once_at_construction()
        {
            // Ported from ViperSourceTests.Plugin_availability_is_re_sampled_while_driving_not_once_at_construction:
            // viper4gh publishes nothing until a supported game runs, and the page is normally opened before.
            _host.CurrentGameId = null;   // at the SimHub menu: nothing to shadow
            SettingsPageViewModel page = NewPage();
            page.SlipTab.Sources.Mode = SourceMode.Viper;
            page.SlipTab.Activate();
            Assert.True(page.SlipTab.Sources.ShowViperUnavailableNote);

            _host.ViperAvailable = true;
            _host.RaisePluginStateChanged();   // the plugin's 1 Hz push while driving

            Assert.False(page.SlipTab.Sources.ShowViperUnavailableNote);
        }

        // ================================================================ health

        [Fact]
        public void A_failed_page_action_is_logged_recorded_and_shown_on_the_General_tab_at_once()
        {
            SettingsPageViewModel page = NewPage();
            page.LockTab.Trigger.BrakeThreshold = 31;
            _host.OnApply = s => throw new InvalidOperationException("boom");
            int reads = 0;
            page.GeneralTab.Health.PropertyChanged += (s, e) => reads++;

            page.ApplyCommand.Execute(null);   // must not throw into WPF

            Assert.Contains(_host.Errors, m => m.StartsWith("QAdvanceFeedback: settings UI action failed - ", StringComparison.Ordinal) && m.Contains("boom"));
            Assert.Contains(HealthRegistry.Snapshot(), h => h.Subsystem == HealthSubsystems.SettingsUi && h.Detail.Contains("boom"));
            Assert.True(reads > 0 || page.GeneralTab.Health.Issues.Count > 0);
        }

        // ================================================================ page views (XAML as text)

        private static string ViewsDir([CallerFilePath] string thisFile = "") => Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)))), "QAdvanceFeedback", "Ui", "Views");

        private static string View(string name) => File.ReadAllText(Path.Combine(ViewsDir(), name));

        [Fact]
        public void Every_grid_of_a_wheel_tab_shares_one_label_and_one_editor_column_scoped_once_at_tab_level()
        {
            // Wave 2 ruling: one name pair for every grid of the tab (trigger, sources, key data, Custom
            // reference, aggregation, curve, pulse) - the old LockAlign / LockEditor - so the columns line up
            // like the old tab. The scope is set ONCE, on the hosted view inside the tab's one ScrollViewer.
            foreach (string view in new[] { "WheelChannelPageView.xaml", "WheelChannelKeyDataView.xaml", "WheelChannelCurveView.xaml", "WheelChannelPulseView.xaml" })
            {
                string xaml = View(view);
                foreach (Match m in Regex.Matches(xaml, "SharedSizeGroup=\"([^\"]*)\""))
                    Assert.True(m.Groups[1].Value == "ChannelAlign" || m.Groups[1].Value == "ChannelEditor", view + ": " + m.Value);
                Assert.DoesNotContain("<ScrollViewer", xaml);
                Assert.DoesNotContain("Grid.IsSharedSizeScope=\"", xaml);
            }
            Assert.Contains("SharedSizeGroup=\"ChannelEditor\"", View("WheelChannelKeyDataView.xaml"));

            string shell = View("SettingsPageView.xaml");
            Assert.Equal(2, Regex.Matches(shell, "<ScrollViewer VerticalScrollBarVisibility=\"Auto\">\\s*<v:WheelChannelPageView DataContext=\"\\{Binding (Lock|Slip)Tab\\}\" Grid.IsSharedSizeScope=\"True\"/>").Count);
            Assert.DoesNotContain("Grid.IsSharedSizeScope=\"", View("GForcePageView.xaml"));
        }

        [Fact]
        public void The_cold_start_reference_sits_under_the_key_data_and_lines_up_with_it()
        {
            // Ported from CustomSourceRestoreTests (owner, 2026-09-29): "move custom source Cold-start reference
            // UNDER the Key Data Points Settings ... vertically aligned with key data points settings ones."
            string xaml = View("WheelChannelPageView.xaml");
            int rows = xaml.IndexOf("DataContext=\"{Binding Rows[0]}\"", StringComparison.Ordinal);
            int keyData = xaml.IndexOf("<v:WheelChannelKeyDataView", StringComparison.Ordinal);
            int customRef = xaml.IndexOf("x:Name=\"CustomRefPanel\"", StringComparison.Ordinal);
            Assert.True(rows > 0 && keyData > rows && customRef > keyData);
            string panel = xaml.Substring(customRef, xaml.IndexOf("</StackPanel>", customRef, StringComparison.Ordinal) - customRef);
            Assert.Contains("SharedSizeGroup=\"ChannelAlign\"", panel);
            Assert.Contains("SharedSizeGroup=\"ChannelEditor\"", panel);
            // The reference can never read dashes (no watermark); the key data editors keep theirs.
            Assert.DoesNotContain("Watermark", panel);
            Assert.Equal(3, Regex.Matches(View("WheelChannelKeyDataView.xaml"), "Watermark=\"---\"").Count);
        }

        [Fact]
        public void The_Viper_game_button_spans_the_source_label_and_the_stretched_dropdown()
        {
            // Ported from CustomSourceRestoreTests (owner, 2026-09-29): "left aligned with 'Source:' text left,
            // and right aligned with the dropdown control right" - a Grid, the button spanning both columns.
            string xaml = View("WheelChannelPageView.xaml");
            int grid = xaml.IndexOf("<Grid x:Name=\"SourceModePanel\"", StringComparison.Ordinal);
            Assert.True(grid > 0);
            string body = xaml.Substring(grid, xaml.IndexOf("</Grid>", grid, StringComparison.Ordinal) - grid);
            Assert.Contains("x:Name=\"SourceModeCombo\" HorizontalAlignment=\"Stretch\"", body);
            Assert.Matches("Grid.ColumnSpan=\"2\"\\s+Visibility=\"\\{Binding ShowViperGameButton", body);
            Assert.Contains("Command=\"{Binding ToggleViperGameCommand}\"", body);
        }

        [Fact]
        public void Both_channels_share_one_view_so_no_notice_can_exist_for_one_channel_only()
        {
            // Ported from CustomSourceRestoreTests.The_page_declares_every_new_element_for_both_channels: the
            // rule is now structural - both tabs host the same view, and it binds every notice.
            string shell = View("SettingsPageView.xaml");
            Assert.Contains("<v:WheelChannelPageView DataContext=\"{Binding LockTab}\"", shell);
            Assert.Contains("<v:WheelChannelPageView DataContext=\"{Binding SlipTab}\"", shell);
            Assert.Contains("x:Name=\"UnappliedChangesBanner\"", shell);
            string xaml = View("WheelChannelPageView.xaml") + View("WheelChannelKeyDataView.xaml");
            foreach (string binding in new[] { "CustomNote", "ViperUnsupportedNote", "ShowViperGameButton", "ShowShadowNote", "CustomLabel" })
                Assert.Contains("{Binding " + binding, xaml);
        }

        [Fact]
        public void Every_numeric_input_of_the_page_is_the_wrapped_NumericEditor()
        {
            // Ported from SettingsControlDirtyTrackingTests (owner, 2026-09-28): the box and its +/- disable
            // together because NumericEditor wraps both - so no raw NumericUpDown, and no half-disabling
            // IsReadOnly, anywhere on the page.
            foreach (string view in new[] { "SettingsPageView.xaml", "WheelChannelPageView.xaml", "WheelChannelKeyDataView.xaml",
                                            "WheelChannelCurveView.xaml", "WheelChannelPulseView.xaml", "GForcePageView.xaml",
                                            "GeneralPageView.xaml", "SlipAngleTabView.xaml", "ClutchBiteView.xaml" })
            {
                string xaml = View(view);
                Assert.DoesNotContain("<mah:NumericUpDown", xaml);
                Assert.DoesNotContain("IsReadOnly", xaml);
            }
            Assert.True(Regex.Matches(View("WheelChannelPageView.xaml") + View("WheelChannelCurveView.xaml") + View("GForcePageView.xaml"),
                "NumericEditor ").Count > 20);
        }

        // ================================================================ the plugin side (not instantiable: read as text)

        // Read with LF line endings whatever the file uses (the repo's files may be CRLF or LF - owner, 2026-11-25).
        private static string PluginFile(string name, [CallerFilePath] string thisFile = "") => File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)))), "QAdvanceFeedback", name)).Replace("\r\n", "\n");

        [Fact]
        public void The_game_reaches_the_page_even_while_paused_or_in_a_game_menu()
        {
            // Ported from PerSourceKeyDataFlagsTests (owner, 2026-09-30: "gameInMenu or GamePaused ... is the
            // very typical scenario when the user adjust the parameters"): the game notification runs ABOVE
            // DataUpdate's learning gate, after CurrentGameId is set, and it now raises PluginStateChanged.
            string plugin = PluginFile("QAdvanceFeedback.cs");
            int update = plugin.IndexOf("public void DataUpdate", StringComparison.Ordinal);
            int set = plugin.IndexOf("_currentGameId = data.GameName ?? string.Empty;", update, StringComparison.Ordinal);
            int push = plugin.IndexOf("PushCurrentGameToSettingsUi(data.GameName ?? string.Empty);", update, StringComparison.Ordinal);
            int gate = plugin.IndexOf("GamePaused", push, StringComparison.Ordinal);
            Assert.True(update > 0 && set > update && push > set && gate > push);
            string method = plugin.Substring(plugin.IndexOf("private void PushCurrentGameToSettingsUi", StringComparison.Ordinal));
            method = method.Substring(0, method.IndexOf("\n        }", StringComparison.Ordinal));
            Assert.Contains("RaiseSettingsPageStateChanged();", method);
        }

        [Fact]
        public void The_plugin_tells_the_page_on_every_state_change_it_used_to_push()
        {
            string plugin = PluginFile("QAdvanceFeedback.cs");
            Assert.DoesNotContain("_settingsControl", plugin);
            Assert.DoesNotContain("UpdateLearnedKeyDataPoints", plugin);
            Assert.DoesNotContain("UpdateCurrentGame", plugin);
            // The 1 Hz learned readout, the seed revision bump, ShakeIt / nativeness changes.
            Assert.Contains("_keyDataRevision++;\n            RaiseSettingsPageStateChanged();", plugin);
            string nativeness = plugin.Substring(plugin.IndexOf("private void NotifyNativenessChanged()", StringComparison.Ordinal), 300);
            Assert.Contains("SettingField.SourceAvailability", nativeness);   // the hub broadcast is kept
            Assert.Contains("RaiseSettingsPageStateChanged();", nativeness);
            string learned = plugin.Substring(plugin.IndexOf("private void PushLearnedKeyDataPointsToSettingsUi", StringComparison.Ordinal));
            learned = learned.Substring(0, learned.IndexOf("\n        }", StringComparison.Ordinal));
            Assert.Contains("RaiseSettingsPageStateChanged();", learned);

            string page = PluginFile("QAdvanceFeedback.SettingsPage.cs");
            Assert.Contains("new SettingsPageViewModel(_pageHost", page);
            Assert.Contains("if (_settingsPage == null)", page);   // ONE page view model, created with the first page
            Assert.Contains("DisposeSettingsPage();", plugin);    // ... disposed with the plugin (End)
        }
    }
}
