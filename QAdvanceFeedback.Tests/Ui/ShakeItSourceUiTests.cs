using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>A panel host that also publishes the ShakeIt source state, and records the identities the
    /// view models ask learned key data for.</summary>
    public sealed class ShakeItStateHost : IPanelHost, IShakeItSourceStateHost
    {
        private readonly FakePanelHost _inner = new FakePanelHost();

        public ShakeItSourceStates ShakeItSourceState { get; set; } = ShakeItSourceStates.Initial;
        public List<string> LearnedAskedFor { get; } = new List<string>();

        public QAdvanceFeedbackSettings Settings => _inner.Settings;
        public SettingsHub Hub => _inner.Hub;
        public string CurrentGameId => _inner.CurrentGameId;
        public SourceNativeness CurrentLockNativeness => SourceNativeness.Unknown;
        public SourceNativeness CurrentSlipNativeness => SourceNativeness.Unknown;
        public int KeyDataRevision => 0;
        public bool ViperAvailable => false;
        public KeyDataTriple LearnedKeyData(bool isLock, string sourceIdentity) { LearnedAskedFor.Add(sourceIdentity); return null; }
        public TestEffectSession Session => _inner.Session;
        public void PublishGForceTest(GForceOutput outputOrNull) { }
        public void PublishLockSlipTest(ProjectedWheelLockSlipResult resultOrNull) { }
        public void PublishSlipAngleTest(SlipAngleProjectedResult resultOrNull) { }

        public void Set(bool isLock, ShakeItSourceState state) => ShakeItSourceState = isLock
            ? new ShakeItSourceStates(state, ShakeItSourceState.Slip)
            : new ShakeItSourceStates(ShakeItSourceState.Lock, state);
    }

    /// <summary>
    /// Task E of docs\shakeit-source-design.md: the ShakeIt Built-in source's list visibility, state notes
    /// and algorithm block on the settings page (tab view models) and the WheelLock / WheelSlip ShakeIt
    /// panels; the menu-time refresh; the fingerprint-aware identity in every key data caller.
    /// </summary>
    public class ShakeItSourceUiTests
    {
        private static readonly ShakeItSourceState[] AllStates =
        {
            ShakeItSourceState.NotSetUp, ShakeItSourceState.SetUpIdle, ShakeItSourceState.Supported, ShakeItSourceState.NotSupported
        };

        private readonly ManualTicker _ticker = new ManualTicker();

        private static string NotSetUpNote => "Not set up — add a QAdvanceFeedback effect to the active ShakeIt profile. Using Raw.";
        private static string NotSupportedNote => "Not supported in this game — using Raw.";

        private WheelChannelEffectViewModel Panel(ShakeItStateHost host, bool isLock)
        {
            var vm = new WheelChannelEffectViewModel(isLock, new FakeRouting(), host, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        private static WheelChannelSettings Channel(IPanelHost host, bool isLock) => isLock ? host.Settings.Lock : host.Settings.Slip;

        // ---------------- list visibility ----------------

        [Fact]
        public void Option_is_hidden_only_when_not_set_up_and_not_selected()
        {
            foreach (ShakeItSourceState state in AllStates)
                foreach (bool selected in new[] { false, true })
                    Assert.Equal(state != ShakeItSourceState.NotSetUp || selected, ShakeItSourceUi.ShowOption(state, selected));
        }

        [Fact]
        public void Tab_visibility_matrix_both_channels()
        {
            foreach (bool isLock in new[] { true, false })
                foreach (ShakeItSourceState state in AllStates)
                    foreach (bool selected in new[] { false, true })
                    {
                        var vm = new ShakeItSourceStatusViewModel();
                        vm.Refresh(selected ? SourceMode.ShakeIt : SourceMode.Manual, SourceMode.Manual, state);
                        Assert.Equal(state != ShakeItSourceState.NotSetUp || selected, vm.ShowShakeItOption);
                        Assert.Equal(state == ShakeItSourceState.NotSetUp ? "ShakeIt Built-in (not set up)" : "ShakeIt Built-in",
                            vm.ShakeItOptionLabel);
                    }
        }

        [Fact]
        public void Tab_keeps_the_saved_ShakeIt_source_listed_and_marked_while_the_draft_differs()
        {
            var vm = new ShakeItSourceStatusViewModel();
            vm.Refresh(SourceMode.Manual, SourceMode.ShakeIt, ShakeItSourceState.NotSetUp);
            Assert.True(vm.ShowShakeItOption);
            Assert.Equal("ShakeIt Built-in (not set up)", vm.ShakeItOptionLabel);
            Assert.False(vm.ShowNote);   // the note is for the shown source only

            vm.Refresh(SourceMode.Manual, SourceMode.Manual, ShakeItSourceState.NotSetUp);
            Assert.False(vm.ShowShakeItOption);
        }

        [Fact]
        public void Panel_visibility_matrix_both_channels()
        {
            foreach (bool isLock in new[] { true, false })
                foreach (ShakeItSourceState state in AllStates)
                    foreach (bool selected in new[] { false, true })
                    {
                        var host = new ShakeItStateHost();
                        host.Set(isLock, state);
                        Channel(host, isLock).SourceMode = selected ? SourceMode.ShakeIt : SourceMode.Manual;
                        WheelChannelEffectViewModel vm = Panel(host, isLock);

                        Choice<SourceMode> shakeIt = vm.Sources.FirstOrDefault(c => c.Value == SourceMode.ShakeIt);
                        bool expected = state != ShakeItSourceState.NotSetUp || selected;
                        Assert.Equal(expected, shakeIt != null);
                        if (shakeIt != null)
                            Assert.Equal(state == ShakeItSourceState.NotSetUp ? "ShakeIt Built-in (not set up)" : "ShakeIt Built-in", shakeIt.Label);
                        Assert.Equal(Channel(host, isLock).SourceMode, vm.Source);   // never rewritten
                    }
        }

        [Fact]
        public void Panel_selected_but_not_set_up_is_listed_marked_and_the_source_is_not_rewritten()
        {
            var host = new ShakeItStateHost();
            host.Settings.Slip.SourceMode = SourceMode.ShakeIt;
            WheelChannelEffectViewModel vm = Panel(host, isLock: false);

            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt }, vm.Sources.Select(c => c.Value).ToArray());
            Assert.Equal("ShakeIt Built-in (not set up)", vm.Sources[1].Label);
            Assert.Equal(SourceMode.ShakeIt, vm.Source);
            Assert.Equal(SourceMode.ShakeIt, host.Settings.Slip.SourceMode);
        }

        [Fact]
        public void A_host_without_a_published_state_lists_ShakeIt_as_before()
        {
            Assert.Equal(ShakeItSourceState.SetUpIdle, ShakeItSourceUi.StateOf(new FakePanelHost(), true));
            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt }, ShakeItSourceOptions.For(false, false, SourceMode.Manual).ToArray());
            Assert.Equal(new[] { SourceMode.Manual }, ShakeItSourceOptions.For(false, false, SourceMode.Manual, shakeItSetUp: false).ToArray());
            Assert.Equal(new[] { SourceMode.Manual, SourceMode.ShakeIt }, ShakeItSourceOptions.For(false, false, SourceMode.ShakeIt, shakeItSetUp: false).ToArray());
        }

        // ---------------- notes ----------------

        [Fact]
        public void Notes_per_state_on_tab_and_panel()
        {
            var expected = new Dictionary<ShakeItSourceState, string>
            {
                [ShakeItSourceState.NotSetUp] = NotSetUpNote,
                [ShakeItSourceState.SetUpIdle] = string.Empty,
                [ShakeItSourceState.Supported] = string.Empty,
                [ShakeItSourceState.NotSupported] = NotSupportedNote,
            };
            foreach (bool isLock in new[] { true, false })
                foreach (ShakeItSourceState state in AllStates)
                {
                    var tab = new ShakeItSourceStatusViewModel();
                    tab.Refresh(SourceMode.ShakeIt, SourceMode.ShakeIt, state);
                    Assert.Equal(expected[state], tab.Note);
                    Assert.Equal(expected[state].Length > 0, tab.ShowNote);

                    var host = new ShakeItStateHost();
                    host.Set(isLock, state);
                    Channel(host, isLock).SourceMode = SourceMode.ShakeIt;
                    WheelChannelEffectViewModel panel = Panel(host, isLock);
                    Assert.Equal(expected[state], panel.ShakeItNote);
                    Assert.Equal(expected[state].Length > 0, panel.ShowShakeItNote);
                    Assert.False(panel.ShowFallbackWarning);   // the orange warning stays Plugin-Internal-only
                }
        }

        [Fact]
        public void No_note_for_any_other_source()
        {
            foreach (SourceMode mode in new[] { SourceMode.Manual, SourceMode.Viper, SourceMode.Custom })
                foreach (ShakeItSourceState state in AllStates)
                    Assert.Equal(string.Empty, ShakeItSourceUi.Note(mode, state));
        }

        [Fact]
        public void Notes_have_both_languages()
        {
            var zh = StringTableZhHans.Create();
            foreach (string key in new[]
                     {
                         "Sources.Mode.ShakeIt.NotSetUp", "Sources.ShakeIt.NotSetUp.Note", "Sources.ShakeIt.NotSupported.Note",
                         "ShakeItAlgorithm.Header", "ShakeItAlgorithm.Note", "ShakeItAlgorithm.LegacyIracing",
                         "ShakeItAlgorithm.LockSensibility", "ShakeItAlgorithm.LockOnly", "ShakeItAlgorithm.MuteWhileLockActive",
                         "Health.Subsystem.ViperAvailability", "Health.Impact.ViperAvailability"
                     })
                Assert.True(zh.ContainsKey(key), key);
            Assert.Equal(NotSetUpNote, StringTableEn.Create()["Sources.ShakeIt.NotSetUp.Note"]);
            Assert.Equal(NotSupportedNote, StringTableEn.Create()["Sources.ShakeIt.NotSupported.Note"]);
        }

        // ---------------- live refresh at the menu ----------------

        [Fact]
        public void Panel_follows_the_state_broadcast_at_the_menu()
        {
            var host = new ShakeItStateHost();
            host.Settings.Lock.SourceMode = SourceMode.Manual;
            WheelChannelEffectViewModel vm = Panel(host, isLock: true);
            Assert.DoesNotContain(SourceMode.ShakeIt, vm.Sources.Select(c => c.Value));

            // The driver adds our effect at the menu: the plugin publishes SetUpIdle and broadcasts.
            host.Set(true, ShakeItSourceState.SetUpIdle);
            host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.Contains(SourceMode.ShakeIt, vm.Sources.Select(c => c.Value));

            host.Hub.Set(SettingField.LockSourceMode, SourceMode.ShakeIt, SettingsOrigin.PluginPage);
            Assert.False(vm.ShowShakeItNote);

            // ...and removes it again: listed (selected), marked, noted.
            host.Set(true, ShakeItSourceState.NotSetUp);
            host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);
            Assert.Equal("ShakeIt Built-in (not set up)", vm.Sources.Single(c => c.Value == SourceMode.ShakeIt).Label);
            Assert.Equal(NotSetUpNote, vm.ShakeItNote);
        }

        [Fact]
        public void Panel_source_survives_a_label_flip_of_the_selected_ShakeIt_item()
        {
            var host = new ShakeItStateHost();
            host.Set(true, ShakeItSourceState.SetUpIdle);
            host.Settings.Lock.SourceMode = SourceMode.ShakeIt;
            WheelChannelEffectViewModel vm = Panel(host, isLock: true);
            var raised = new List<string>();
            vm.PropertyChanged += (s, e) => raised.Add(e.PropertyName);

            foreach (ShakeItSourceState state in new[] { ShakeItSourceState.NotSetUp, ShakeItSourceState.SetUpIdle })
            {
                raised.Clear();
                IReadOnlyList<Choice<SourceMode>> before = vm.Sources;
                host.Set(true, state);
                host.Hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime);

                Assert.NotSame(before, vm.Sources);   // only the label changed, yet the list was replaced
                // ...so the selection is re-asserted after it, for a combo whose ItemsSource reset cleared it.
                Assert.Contains(nameof(WheelChannelEffectViewModel.Source), raised);
                Assert.True(raised.LastIndexOf(nameof(WheelChannelEffectViewModel.Source)) > raised.LastIndexOf(nameof(WheelChannelEffectViewModel.Sources)));
                Assert.Equal(SourceMode.ShakeIt, vm.Source);
                Assert.Equal(SourceMode.ShakeIt, host.Settings.Lock.SourceMode);
            }
        }

        [Fact]
        public void Page_hidden_ShakeIt_item_is_also_disabled_and_the_block_label_is_distinct()
        {
            // One view for both tabs (Wave 2: the old page's Lock / Slip copies are gone).
            string xaml = File.ReadAllText(Path.Combine(ViewsDir(), "WheelChannelPageView.xaml"));
            Assert.Matches("<ComboBoxItem Tag=\"ShakeIt\"[^>]*IsEnabled=\"\\{Binding ShowShakeItOption\\}\"[^>]*Visibility=\"\\{Binding ShowShakeItOption", xaml);
            Assert.Equal("ShakeIt lock sensibility", StringTableEn.Create()["ShakeItAlgorithm.LockSensibility"]);
            Assert.NotEqual(StringTableEn.Create()["ShakeItAlgorithm.LockSensibility"], StringTableEn.Create()["Sources.Threshold.LockSensibility"]);
            Assert.StartsWith("ShakeIt", StringTableZhHans.Create()["ShakeItAlgorithm.LockSensibility"]);
        }

        // ---------------- algorithm block ----------------

        [Fact]
        public void Block_visible_only_for_ShakeIt_and_values_kept_across_a_source_switch()
        {
            var vm = new ShakeItAlgorithmViewModel(isLock: true);
            var s = new ShakeItAlgorithmSettings { UseLegacyIracingAlgorithm = false, LockSensibility = 70, LockOnly = false };
            vm.Load(s);

            foreach (SourceMode mode in new[] { SourceMode.Manual, SourceMode.Viper, SourceMode.Custom })
            {
                vm.SetSource(mode);
                Assert.False(vm.IsVisible);
            }
            vm.SetSource(SourceMode.ShakeIt);
            Assert.True(vm.IsVisible);
            Assert.False(vm.UseLegacyIracingAlgorithm);
            Assert.Equal(70.0, vm.LockSensibility);
            Assert.False(vm.LockOnly);
        }

        [Fact]
        public void Panel_block_follows_the_channel_source_and_keeps_values()
        {
            var host = new ShakeItStateHost();
            host.Set(false, ShakeItSourceState.Supported);
            host.Settings.Slip.SourceMode = SourceMode.Manual;
            host.Settings.Slip.ShakeItAlgorithm.MuteWhileLockActive = true;
            WheelChannelEffectViewModel vm = Panel(host, isLock: false);
            Assert.False(vm.Algorithm.IsVisible);
            Assert.True(vm.Algorithm.MuteWhileLockActive);
            Assert.True(vm.Algorithm.IsSlip);

            vm.Source = SourceMode.ShakeIt;
            Assert.True(vm.Algorithm.IsVisible);
            vm.Source = SourceMode.Manual;
            Assert.False(vm.Algorithm.IsVisible);
            Assert.True(host.Settings.Slip.ShakeItAlgorithm.MuteWhileLockActive);
            Assert.True(vm.Algorithm.MuteWhileLockActive);
        }

        [Theory]
        [InlineData(52.0, 50.0)]
        [InlineData(53.0, 55.0)]
        [InlineData(52.5, 55.0)]
        [InlineData(120.0, 100.0)]
        [InlineData(-5.0, 0.0)]
        [InlineData(65.0, 65.0)]
        public void Sensibility_snaps_to_steps_of_5(double typed, double expected)
        {
            var vm = new ShakeItAlgorithmViewModel(isLock: true);
            vm.LockSensibility = typed;
            Assert.Equal(expected, vm.LockSensibility);
            var target = new ShakeItAlgorithmSettings();
            vm.SaveTo(target);
            Assert.Equal(expected, target.LockSensibility);
        }

        [Fact]
        public void A_cleared_sensibility_box_keeps_the_value()
        {
            var vm = new ShakeItAlgorithmViewModel(isLock: true);
            int edits = 0;
            vm.Edited += (f, v) => edits++;
            vm.LockSensibility = null;
            Assert.Equal(50.0, vm.LockSensibility);
            Assert.Equal(0, edits);
        }

        [Fact]
        public void Edited_fires_for_driver_edits_only_with_the_hub_field()
        {
            var edits = new List<KeyValuePair<SettingField, object>>();
            var l = new ShakeItAlgorithmViewModel(isLock: true);
            var sl = new ShakeItAlgorithmViewModel(isLock: false);
            l.Edited += (f, v) => edits.Add(new KeyValuePair<SettingField, object>(f, v));
            sl.Edited += (f, v) => edits.Add(new KeyValuePair<SettingField, object>(f, v));

            var loaded = new ShakeItAlgorithmSettings { UseLegacyIracingAlgorithm = false, LockSensibility = 30, LockOnly = false, MuteWhileLockActive = true };
            l.Load(loaded);
            sl.Load(loaded);
            Assert.True(l.LoadField(SettingField.LockShakeItSensibility, new ShakeItAlgorithmSettings { LockSensibility = 80 }));
            Assert.False(l.LoadField(SettingField.SlipShakeItMuteWhileLockActive, loaded));
            Assert.Empty(edits);   // a load is never an edit (the page's dirty tracking relies on it)
            Assert.Equal(80.0, l.LockSensibility);
            Assert.False(l.UseLegacyIracingAlgorithm);   // only the named field was reloaded

            l.UseLegacyIracingAlgorithm = true;
            l.LockSensibility = 41;
            l.LockOnly = true;
            sl.UseLegacyIracingAlgorithm = true;
            sl.MuteWhileLockActive = false;

            Assert.Equal(new[]
            {
                SettingField.LockShakeItLegacyAlgorithm, SettingField.LockShakeItSensibility, SettingField.LockShakeItLockOnly,
                SettingField.SlipShakeItLegacyAlgorithm, SettingField.SlipShakeItMuteWhileLockActive
            }, edits.Select(e => e.Key).ToArray());
            Assert.Equal(40.0, edits[1].Value);
        }

        [Fact]
        public void SaveTo_writes_only_the_channels_own_part()
        {
            var target = new ShakeItAlgorithmSettings { LockSensibility = 25, LockOnly = false, MuteWhileLockActive = true };
            var slip = new ShakeItAlgorithmViewModel(isLock: false);
            slip.UseLegacyIracingAlgorithm = false;
            slip.MuteWhileLockActive = false;
            slip.SaveTo(target);
            Assert.False(target.UseLegacyIracingAlgorithm);
            Assert.False(target.MuteWhileLockActive);
            Assert.Equal(25.0, target.LockSensibility);
            Assert.False(target.LockOnly);
        }

        // ---------------- page view (WPF is not instantiable in xunit: the XAML read as text) ----------------

        private static string ViewsDir([CallerFilePath] string thisFile = "")
            => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))), "QAdvanceFeedback", "Ui", "Views");

        // ---------------- identity: the engine's (fingerprinted) key in all three callers ----------------

        private static void ShakeItWithNonDefaultAlgorithm(WheelChannelSettings ch, bool isLock)
        {
            ch.SwitchSourceMode(SourceMode.ShakeIt, isLock);
            if (isLock) ch.ShakeItAlgorithm.LockSensibility = 70;
            else ch.ShakeItAlgorithm.MuteWhileLockActive = true;
        }

        [Fact]
        public void Panel_key_data_and_graph_ask_for_the_fingerprinted_key()
        {
            foreach (bool isLock in new[] { true, false })
            {
                var host = new ShakeItStateHost();
                WheelChannelSettings ch = Channel(host, isLock);
                ShakeItWithNonDefaultAlgorithm(ch, isLock);
                string engineKey = KeyDataPanelModel.Identity(ch, isLock);
                Assert.NotEqual(KeyDataPanelModel.Identity(ch), engineKey);

                var keyData = new KeyDataViewModel(isLock, host, _ticker, new ImmediateDispatcher());
                keyData.Activate();
                Assert.Equal(engineKey, host.LearnedAskedFor.Last());

                host.LearnedAskedFor.Clear();
                new GraphViewModel(isLock, host).Refresh();
                Assert.NotEmpty(host.LearnedAskedFor);
                Assert.All(host.LearnedAskedFor, id => Assert.Equal(engineKey, id));
            }
        }

        [Fact]
        public void Panel_key_data_follows_an_algorithm_change()
        {
            var host = new ShakeItStateHost();
            host.Settings.Lock.SwitchSourceMode(SourceMode.ShakeIt, true);
            WheelChannelEffectViewModel vm = Panel(host, isLock: true);
            host.LearnedAskedFor.Clear();

            vm.Algorithm.LockSensibility = 80;

            Assert.Contains(KeyDataPanelModel.Identity(host.Settings.Lock, true), host.LearnedAskedFor);
            Assert.NotEqual(KeyDataPanelModel.Identity(host.Settings.Lock), KeyDataPanelModel.Identity(host.Settings.Lock, true));
        }

        [Fact]
        public void At_defaults_the_identity_is_unchanged()
        {
            var host = new ShakeItStateHost();
            host.Settings.Lock.SwitchSourceMode(SourceMode.ShakeIt, true);
            Assert.Equal(KeyDataPanelModel.Identity(host.Settings.Lock), KeyDataPanelModel.Identity(host.Settings.Lock, true));
        }

        [Fact]
        public void Page_draft_identity_equals_the_engine_key_for_the_same_channel()
        {
            // The page builds its draft channel from the controls exactly like this; the result must be the
            // key the engine and the hub use for the applied channel with the same values.
            var applied = new WheelChannelSettings();
            ShakeItWithNonDefaultAlgorithm(applied, isLock: true);
            var block = new ShakeItAlgorithmViewModel(isLock: true);
            block.Load(applied.ShakeItAlgorithm);
            var draft = new WheelChannelSettings
            {
                SourceMode = SourceMode.ShakeIt,
                SourceFrontLeft = applied.SourceFrontLeft, ScriptTypeFrontLeft = applied.ScriptTypeFrontLeft,
                SourceFrontRight = applied.SourceFrontRight, ScriptTypeFrontRight = applied.ScriptTypeFrontRight,
                SourceRearLeft = applied.SourceRearLeft, ScriptTypeRearLeft = applied.ScriptTypeRearLeft,
                SourceRearRight = applied.SourceRearRight, ScriptTypeRearRight = applied.ScriptTypeRearRight,
                ShakeItAlgorithm = block.ToSettings()
            };
            Assert.Equal(KeyDataPanelModel.Identity(applied, true), KeyDataPanelModel.Identity(draft, true));
        }
    }
}
