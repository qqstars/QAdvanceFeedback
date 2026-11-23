using System;
using System.Collections.Generic;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.ShakeItEffects;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;
using QAdvanceFeedback.Ui.ViewModels;
using Xunit;

namespace QAdvanceFeedback.Tests.Ui
{
    /// <summary>The Slip Angle snapshot side of the plugin, for the native Slip Angle panel.</summary>
    public sealed class FakeSlipAngleEffectHost : ISlipAngleEffectHost
    {
        public SlipAngleStatus SlipAngleStatus { get; set; } = SlipAngleStatus.None;
        public SourceNativeness SlipAngleIdentityTier { get; set; } = SourceNativeness.Unknown;
        /// <summary>What the plugin's learner holds per identity for the current context (the display rule's
        /// result); an identity absent here is an unknown key.</summary>
        public Dictionary<string, SlipAngleWindow> Learned { get; } = new Dictionary<string, SlipAngleWindow>(StringComparer.Ordinal);
        public SlipAngleWindow? LearnedSlipAngleWindow(string sourceIdentity) =>
            sourceIdentity != null && Learned.TryGetValue(sourceIdentity, out SlipAngleWindow w) ? w : (SlipAngleWindow?)null;
    }

    /// <summary>
    /// The native ShakeIt "Slip Angle" effect's panel (docs\slip-angle-design.md 9.10): Trigger By and
    /// its thresholds, Auto / Manual, Min/Best/Max (read-only under Auto), the orange fallback warning and
    /// the status - two-way through the settings hub with <see cref="SettingsOrigin.ShakeItPanel"/>.
    /// </summary>
    public class SlipAngleEffectViewModelTests
    {
        private readonly FakePanelHost _host = new FakePanelHost();
        private readonly FakeSlipAngleEffectHost _slip = new FakeSlipAngleEffectHost();
        private readonly ManualTicker _ticker = new ManualTicker();
        private readonly List<SettingsChangedEventArgs> _events = new List<SettingsChangedEventArgs>();

        public SlipAngleEffectViewModelTests()
        {
            _host.Hub.Changed += (s, e) => _events.Add(e);
        }

        private SlipAngleChannelSettings Live => _host.Settings.SlipAngle;
        private static string Id => SlipAngleFrameProcessor.NativeIdentity;

        private SlipAngleEffectViewModel NewVm()
        {
            var vm = new SlipAngleEffectViewModel(_host, _slip, _ticker, new ImmediateDispatcher());
            vm.Activate();
            return vm;
        }

        private static SlipAngleStatus Status(SourceNativeness tier, bool hasData, string game = "TestGame",
            bool pluginInternal = true, SlipAngleWindow? applied = null) =>
            new SlipAngleStatus(tier, pluginInternal, hasData, SlipAngleWindow.ShippedDefault,
                applied ?? SlipAngleWindow.ShippedDefault, 0.5, ColdStartTier.Tier1, true, true, game,
                SettingsHub.SlipAngleIdentity(null, tier), windowQuality: 0.4);

        [Fact]
        public void Panel_values_read_only_under_auto()
        {
            Live.KeyData.SetAutoGenerate(Id, true);
            _slip.SlipAngleStatus = Status(SourceNativeness.Native, true, applied: new SlipAngleWindow(4.0, 7.0, 9.0));
            _slip.Learned[Id] = new SlipAngleWindow(4.0, 7.0, 9.0);   // the exact key's window
            SlipAngleEffectViewModel vm = NewVm();

            Assert.True(vm.Auto);
            Assert.False(vm.ValuesEditable);
            Assert.Equal((4.0, 7.0, 9.0), (vm.Min, vm.Best, vm.Max));   // the applied (learned) window

            // A write while read-only never reaches the settings.
            vm.Max = 12.0;
            _ticker.Advance(TimeSpan.FromSeconds(2));
            Assert.False(Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out _));
            Assert.DoesNotContain(_events, e => e.Fields.Contains(SettingField.SlipAngleKeyDataValues));

            vm.Auto = false;
            Assert.True(vm.ValuesEditable);
            Assert.False(Live.KeyData.GetAutoGenerate(Id));
        }

        [Fact]
        public void Auto_readout_follows_the_learner_once_a_second()
        {
            Live.KeyData.SetAutoGenerate(Id, true);
            _slip.SlipAngleStatus = Status(SourceNativeness.Native, true, applied: new SlipAngleWindow(4.0, 7.0, 9.0));
            _slip.Learned[Id] = new SlipAngleWindow(4.0, 7.0, 9.0);   // the exact key's window
            SlipAngleEffectViewModel vm = NewVm();

            Assert.Contains("13.5", vm.Graph.Caption);   // End = 1.5 x Max

            _slip.SlipAngleStatus = Status(SourceNativeness.Native, true, applied: new SlipAngleWindow(6.0, 9.0, 12.0));
            _slip.Learned[Id] = new SlipAngleWindow(6.0, 9.0, 12.0);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(12.0, vm.Max);
            Assert.Contains("18", vm.Graph.Caption);      // the graph follows the new End
            Assert.Equal(100.0, vm.Graph.Curve.Last().X, 6);   // x = percent of End on the shared control
        }

        [Fact]
        public void Panel_edit_reaches_hub_with_ShakeItPanel_origin()
        {
            Live.KeyData.SetAutoGenerate(Id, false);
            SlipAngleEffectViewModel vm = NewVm();

            vm.TriggerBy = TriggerBy.Brake;
            Assert.Equal(TriggerBy.Brake, Live.Gate.TriggerBy);
            Assert.Contains(_events, e => e.Origin == SettingsOrigin.ShakeItPanel && e.Fields.Contains(SettingField.SlipAngleTriggerBy));

            vm.BrakeThreshold = 33;
            Assert.Equal(33.0, Live.Gate.BrakeThreshold);
            Assert.Contains(_events, e => e.Origin == SettingsOrigin.ShakeItPanel && e.Fields.Contains(SettingField.SlipAngleBrakeThreshold));

            vm.Min = 4.0; vm.Best = 6.0; vm.Max = 9.0;
            _ticker.Advance(TimeSpan.FromMilliseconds(600));
            Assert.False(Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out _));   // still debouncing
            _ticker.Advance(TimeSpan.FromMilliseconds(200));
            Assert.True(Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out SlipAngleWindow w));
            Assert.Equal((4.0, 6.0, 9.0), (w.Min, w.Best, w.Max));
            Assert.Contains(_events, e => e.Origin == SettingsOrigin.ShakeItPanel && e.Fields.Contains(SettingField.SlipAngleKeyDataValues));
        }

        [Fact]
        public void Pending_edit_is_written_before_auto_and_on_close()
        {
            Live.KeyData.SetAutoGenerate(Id, false);
            SlipAngleEffectViewModel vm = NewVm();

            vm.Min = 4.0; vm.Best = 6.0; vm.Max = 9.0;
            vm.Auto = true;   // the pending window lands first
            Assert.True(Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out SlipAngleWindow w));
            Assert.Equal(9.0, w.Max);
            Assert.True(Live.KeyData.GetAutoGenerate(Id));

            vm.Auto = false;
            vm.Max = 11.0;
            vm.Deactivate();
            Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out w);
            Assert.Equal(11.0, w.Max);
        }

        [Fact]
        public void Threshold_shows_the_clamped_value_even_when_the_write_changes_nothing()
        {
            SlipAngleEffectViewModel vm = NewVm();
            vm.BrakeThreshold = 150;   // a change: clamped to 100, broadcast, re-read
            Assert.Equal(100.0, vm.BrakeThreshold);
            vm.BrakeThreshold = 120;   // clamps back to the same 100: refused, still re-read
            Assert.Equal(100.0, vm.BrakeThreshold);
            Assert.Equal(100.0, Live.Gate.BrakeThreshold);
        }

        [Fact]
        public void Invalid_typed_window_is_not_committed_and_shows_the_rule()
        {
            Live.KeyData.SetAutoGenerate(Id, false);
            SlipAngleEffectViewModel vm = NewVm();

            vm.Min = 8.0; vm.Best = 6.0; vm.Max = 9.0;
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(Live.KeyData.TryGetSlot(_host.CurrentGameId, Id, out _));
            Assert.False(string.IsNullOrEmpty(vm.KeyDataStatus));
        }

        [Fact]
        public void Outside_change_refreshes_the_panel()
        {
            SlipAngleEffectViewModel vm = NewVm();
            Live.Gate.TriggerBy = TriggerBy.Throttle;
            Live.Gate.ThrottleThreshold = 44;
            _host.Hub.NotifyApplied(new[] { SettingField.SlipAngleTriggerBy, SettingField.SlipAngleThrottleThreshold }, SettingsOrigin.PluginPage);
            Assert.Equal(TriggerBy.Throttle, vm.TriggerBy);
            Assert.Equal(44.0, vm.ThrottleThreshold);
        }

        [Theory]
        [InlineData(TriggerBy.Brake, true, false)]
        [InlineData(TriggerBy.Throttle, false, true)]
        [InlineData(TriggerBy.Any, true, true)]
        public void Trigger_by_shows_only_the_thresholds_it_uses(TriggerBy by, bool brake, bool throttle)
        {
            SlipAngleEffectViewModel vm = NewVm();
            vm.TriggerBy = by;
            Assert.Equal(brake, vm.ShowBrakeThreshold);
            Assert.Equal(throttle, vm.ShowThrottleThreshold);
        }

        [Fact]
        public void Fallback_shows_the_orange_warning()
        {
            _slip.SlipAngleStatus = Status(SourceNativeness.Fallback, true);
            SlipAngleEffectViewModel vm = NewVm();
            Assert.True(vm.ShowFallbackWarning);
            Assert.False(string.IsNullOrEmpty(vm.FallbackWarningText));
            Assert.False(vm.ShowNoData);
        }

        [Fact]
        public void No_data_shows_the_status_line_and_the_warning_goes_away()
        {
            _slip.SlipAngleStatus = Status(SourceNativeness.Fallback, true);
            SlipAngleEffectViewModel vm = NewVm();

            _slip.SlipAngleStatus = Status(SourceNativeness.Unknown, false);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.False(vm.ShowFallbackWarning);
            Assert.True(vm.ShowNoData);
            Assert.False(string.IsNullOrEmpty(vm.NoDataText));
        }

        [Fact]
        public void Faulted_shows_neither_warning_nor_status()
        {
            _slip.SlipAngleStatus = SlipAngleStatus.Fault("TestGame", true);
            SlipAngleEffectViewModel vm = NewVm();
            Assert.False(vm.ShowFallbackWarning);
            Assert.False(vm.ShowNoData);
            Assert.Equal(string.Empty, vm.LearningStatus);
        }

        [Fact]
        public void Without_the_plugin_the_panel_is_unavailable()
        {
            _host.Settings = null;
            SlipAngleEffectViewModel vm = NewVm();
            Assert.False(vm.Available);
            Assert.False(vm.ValuesEditable);
            Assert.False(string.IsNullOrEmpty(vm.Status));
        }

        [Fact]
        public void Test_slot_holds_the_slip_angle_test_effect()
        {
            SlipAngleEffectViewModel vm = NewVm();
            Assert.NotNull(vm.Test);
            Assert.False(vm.Test.IsTestEnabled);
            // The slider spans 0 to 300% of the Max the graph shows.
            Assert.Equal(3.0 * vm.Current.Max, vm.Test.SliderMaximum, 9);
        }

        [Fact]
        public void Closing_the_panel_stops_the_test_and_releases_the_output()
        {
            SlipAngleEffectViewModel vm = NewVm();
            vm.Test.IsTestEnabled = true;
            vm.Test.SourceDeg = 5.0;
            _ticker.Advance(TimeSpan.FromSeconds(0.1));
            Assert.NotNull(_host.SlipAngle.Last());

            vm.Deactivate();
            Assert.False(vm.Test.IsTestEnabled);
            Assert.Null(_host.SlipAngle.Last());
            Assert.Equal(TestKind.None, _host.Session.Active);
        }

        [Fact]
        public void Stale_frame_outputs_zero()
        {
            long now = 10_000 * TimeSpan.TicksPerMillisecond;
            Assert.Equal(0.0, SlipAngleEffectOutput.Value0100(null, now));
            Assert.Equal(42.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(42.0, now - 100 * TimeSpan.TicksPerMillisecond), now));
            Assert.Equal(42.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(42.0, now - FrameSnapshot.StaleTicks), now));
            Assert.Equal(0.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(42.0, now - FrameSnapshot.StaleTicks - 1), now));
            Assert.Equal(0.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(42.0, now + 1), now));   // clock went back
            Assert.Equal(0.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(double.NaN, now), now));
            Assert.Equal(100.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(130.0, now), now));
            Assert.Equal(0.0, SlipAngleEffectOutput.Value0100(new FrameSnapshot<double>(-5.0, now), now));
        }

        [Fact]
        public void Panel_start_line_follows_min()
        {
            SlipAngleEffectViewModel vm = NewVm();
            vm.Min = 5.0;
            Assert.Equal("Start: 2° (50% of Min)", vm.StartText);
            vm.Min = 1.5;
            Assert.Equal("Start: — (Min below 2°)", vm.StartText);
        }

        // ---------------- the one display rule (owner, 2026-11-19; docs\full-mvvm-plan.md 3.5) ----------------

        [Fact]
        public void Restart_on_the_menu_shows_the_saved_exact_key()
        {
            // SimHub restarted, no game running: no frame-loop status at all, but the plugin's learner holds
            // the persisted context's exact key.
            _host.CurrentGameId = null;
            _slip.SlipAngleStatus = SlipAngleStatus.None;
            _slip.Learned[Id] = new SlipAngleWindow(4.0, 6.5, 9.0);
            SlipAngleEffectViewModel vm = NewVm();
            Assert.Equal((4.0, 6.5, 9.0), (vm.Min, vm.Best, vm.Max));
        }

        [Fact]
        public void An_unknown_key_shows_the_default_not_the_frame_loop_window()
        {
            _slip.SlipAngleStatus = Status(SourceNativeness.Native, true, applied: new SlipAngleWindow(4.0, 7.0, 9.0));
            SlipAngleEffectViewModel vm = NewVm();
            Assert.Equal((5.0, 8.0, 10.0), (vm.Min, vm.Best, vm.Max));
        }

        [Fact]
        public void The_identity_tier_picks_the_key_native_and_fallback_never_cross_read()
        {
            _slip.Learned[SlipAngleFrameProcessor.NativeIdentity] = new SlipAngleWindow(4.0, 7.0, 9.0);
            _slip.SlipAngleIdentityTier = SourceNativeness.Fallback;
            SlipAngleEffectViewModel vm = NewVm();
            Assert.Equal((5.0, 8.0, 10.0), (vm.Min, vm.Best, vm.Max));

            _slip.Learned[SlipAngleFrameProcessor.FallbackIdentity] = new SlipAngleWindow(6.0, 9.0, 14.0);
            _ticker.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal((6.0, 9.0, 14.0), (vm.Min, vm.Best, vm.Max));
        }

        [Fact]
        public void Manual_with_nothing_stored_prefills_the_default()
        {
            Live.KeyData.SetAutoGenerate(Id, false);
            _slip.Learned[Id] = new SlipAngleWindow(4.0, 7.0, 9.0);
            SlipAngleEffectViewModel vm = NewVm();
            Assert.Equal((5.0, 8.0, 10.0), (vm.Min, vm.Best, vm.Max));

            Assert.True(Live.KeyData.TryCommitManual(_host.CurrentGameId, Id, 3.0, 6.0, 8.0));
            vm.RefreshKeyData();
            Assert.Equal((3.0, 6.0, 8.0), (vm.Min, vm.Best, vm.Max));
        }

        [Fact]
        public void The_panel_shows_what_the_tab_shows()
        {
            var window = new SlipAngleWindow(4.2, 6.9, 9.4);
            _slip.Learned[Id] = window;
            SlipAngleEffectViewModel panel = NewVm();

            var tabHost = new FakeSlipAngleHost { Settings = _host.Settings, IdentityTier = SourceNativeness.Unknown };
            tabHost.Learned[Id] = window;
            var tab = new SlipAngleTabViewModel(tabHost, _ticker);
            tab.Activate();

            Assert.Equal((tab.Min, tab.Best, tab.Max), (panel.Min, panel.Best, panel.Max));
            Assert.Equal((4.2, 6.9, 9.4), (panel.Min, panel.Best, panel.Max));
        }
    }
}
