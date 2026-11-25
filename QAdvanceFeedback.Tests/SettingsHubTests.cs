using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Per-field two-way sync between the plugin settings page and the ShakeIt effect panels
    /// (docs\shakeit-native-effects-design.md, "Two-way settings sync").
    /// </summary>
    public class SettingsHubTests
    {
        private static SettingsHub NewHub(out QAdvanceFeedbackSettings settings, Action onSave = null)
        {
            var s = new QAdvanceFeedbackSettings();
            settings = s;
            return new SettingsHub(() => s, _ => onSave?.Invoke());
        }

        private static QAdvanceFeedbackSettings Clone(QAdvanceFeedbackSettings s) =>
            JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));

        [Fact]
        public void Set_writes_and_raises_only_that_field()
        {
            var hub = NewHub(out var s);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            Assert.True(hub.Set(SettingField.AccelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel));

            Assert.Equal(GMaxMode.Fixed, s.GForce.AccelMaxMode);
            Assert.Equal(new[] { SettingField.AccelMaxMode }, got.Fields);
            Assert.Equal(SettingsOrigin.ShakeItPanel, got.Origin);
        }

        [Fact]
        public void Set_same_value_is_a_noop()
        {
            int saves = 0, events = 0;
            var hub = NewHub(out _, () => saves++);
            hub.Changed += (_, __) => events++;

            Assert.False(hub.Set(SettingField.AccelMaxMode, GMaxMode.Auto, SettingsOrigin.ShakeItPanel));

            Assert.Equal(0, saves);
            Assert.Equal(0, events);
        }

        [Fact]
        public void Set_applies_and_saves_once()
        {
            int saves = 0;
            var hub = NewHub(out var s, () => saves++);

            hub.Set(SettingField.FixedLatMaxG, 2.5, SettingsOrigin.ShakeItPanel);

            Assert.Equal(1, saves);
            Assert.Equal(2.5, s.GForce.FixedLatMaxG, 9);
        }

        [Fact]
        public void Set_fixed_g_reads_back_every_field()
        {
            var hub = NewHub(out var s);
            hub.Set(SettingField.FixedAccelMaxG, 1.25, SettingsOrigin.ShakeItPanel);
            hub.Set(SettingField.FixedDecelMaxG, 3.5, SettingsOrigin.ShakeItPanel);
            hub.Set(SettingField.DecelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);
            hub.Set(SettingField.LatMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);

            Assert.Equal(1.25, (double)SettingsHub.Read(s, SettingField.FixedAccelMaxG), 9);
            Assert.Equal(3.5, (double)SettingsHub.Read(s, SettingField.FixedDecelMaxG), 9);
            Assert.Equal(GMaxMode.Fixed, SettingsHub.Read(s, SettingField.DecelMaxMode));
            Assert.Equal(GMaxMode.Fixed, SettingsHub.Read(s, SettingField.LatMaxMode));
        }

        [Fact]
        public void SourceMode_switch_goes_through_SwitchSourceMode()
        {
            var hub = NewHub(out var s);
            var expected = WheelChannelSettings.CreateLockDefaults();
            expected.SwitchSourceMode(SourceMode.ShakeIt, isLockChannel: true);

            hub.Set(SettingField.LockSourceMode, SourceMode.ShakeIt, SettingsOrigin.ShakeItPanel);

            Assert.Equal(SourceMode.ShakeIt, s.Lock.SourceMode);
            Assert.Equal(expected.SourceFrontLeft, s.Lock.SourceFrontLeft);
            Assert.Equal(expected.SourceRearRight, s.Lock.SourceRearRight);
            Assert.Equal(SourceMode.Manual, s.Slip.SourceMode);
        }

        [Fact]
        public void SourceMode_round_trip_restores_the_drivers_raw_text()
        {
            var hub = NewHub(out var s);
            s.Slip.SourceFrontLeft = "MyPlugin.SlipFL";
            hub.Set(SettingField.SlipSourceMode, SourceMode.ShakeIt, SettingsOrigin.ShakeItPanel);
            hub.Set(SettingField.SlipSourceMode, SourceMode.Manual, SettingsOrigin.ShakeItPanel);

            Assert.Equal("MyPlugin.SlipFL", s.Slip.SourceFrontLeft);
        }

        [Fact]
        public void Broadcast_only_fields_cannot_be_set()
        {
            var hub = NewHub(out _);
            Assert.Throws<InvalidOperationException>(() => hub.Set(SettingField.LockProjector, null, SettingsOrigin.ShakeItPanel));
        }

        [Fact]
        public void NotifyApplied_raises_without_saving()
        {
            int saves = 0;
            var hub = NewHub(out _, () => saves++);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;

            hub.NotifyApplied(new[] { SettingField.LockProjector }, SettingsOrigin.PluginPage);

            Assert.Equal(0, saves);
            Assert.Equal(SettingsOrigin.PluginPage, got.Origin);
            Assert.Equal(new[] { SettingField.LockProjector }, got.Fields);
        }

        [Fact]
        public void NotifyApplied_with_no_fields_raises_nothing()
        {
            var hub = NewHub(out _);
            int events = 0;
            hub.Changed += (_, __) => events++;
            hub.NotifyApplied(new SettingField[0], SettingsOrigin.PluginPage);
            Assert.Equal(0, events);
        }

        [Fact]
        public void Diff_reports_changed_shared_fields_only()
        {
            var a = new QAdvanceFeedbackSettings();
            var b = Clone(a);
            b.GForce.FixedDecelMaxG = a.GForce.FixedDecelMaxG + 1;
            b.Lock.SlipFloorFactor += 1;   // not a shared field

            Assert.Equal(new[] { SettingField.FixedDecelMaxG }, SettingsHub.Diff(a, b));
        }

        [Fact]
        public void Diff_reports_projector_change()
        {
            var a = new QAdvanceFeedbackSettings();
            var b = Clone(a);
            b.Slip.Projector.ModerateOutput += 5;

            Assert.Equal(new[] { SettingField.SlipProjector }, SettingsHub.Diff(a, b));
        }

        [Fact]
        public void FileChanged_applies_differing_fields_with_File_origin()
        {
            var hub = NewHub(out var s);
            SettingsChangedEventArgs got = null;
            hub.Changed += (_, e) => got = e;
            var disk = Clone(s);
            disk.GForce.LatMaxMode = GMaxMode.Fixed;

            hub.NotifyFileChanged(disk);

            Assert.Equal(GMaxMode.Fixed, s.GForce.LatMaxMode);
            Assert.Equal(SettingsOrigin.File, got.Origin);
            Assert.Equal(new[] { SettingField.LatMaxMode }, got.Fields);
        }

        [Fact]
        public void FileChanged_identical_raises_nothing()
        {
            var hub = NewHub(out var s);
            int events = 0;
            hub.Changed += (_, __) => events++;
            hub.NotifyFileChanged(Clone(s));
            Assert.Equal(0, events);
        }

        [Fact]
        public void A_throwing_listener_does_not_stop_the_others()
        {
            var hub = NewHub(out _);
            int reached = 0;
            hub.Changed += (_, __) => throw new InvalidOperationException("boom");
            hub.Changed += (_, __) => reached++;
            hub.Set(SettingField.AccelMaxMode, GMaxMode.Fixed, SettingsOrigin.ShakeItPanel);
            Assert.Equal(1, reached);
        }

        [Fact]
        public void Hub_broadcasts_nativeness_change_once()
        {
            var hub = NewHub(out _);
            var fields = new List<SettingField>();
            hub.Changed += (_, e) => fields.AddRange(e.Fields);
            var tracker = new QAdvanceFeedback.Core.NativenessBroadcaster(
                () => hub.NotifyApplied(new[] { SettingField.SourceAvailability }, SettingsOrigin.Runtime));

            var F = QAdvanceFeedback.Core.SourceNativeness.Fallback;
            var N = QAdvanceFeedback.Core.SourceNativeness.Native;
            for (int i = 0; i < 60; i++) tracker.Update(F, N, 1.0 / 60);   // 1 s stable

            Assert.Equal(new[] { SettingField.SourceAvailability }, fields);
            Assert.Equal(F, tracker.Lock);
            Assert.Equal(N, tracker.Slip);

            tracker.Clear();
            Assert.Equal(2, fields.Count);
        }
    }

    public class ConfigFileWatcherTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public ConfigFileWatcherTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "qaf-watch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _path = Path.Combine(_dir, "QAdvanceFeedback.json");
            File.WriteAllText(_path, JsonConvert.SerializeObject(new QAdvanceFeedbackSettings()));
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private void WriteSettings(Action<QAdvanceFeedbackSettings> edit)
        {
            var s = new QAdvanceFeedbackSettings();
            edit(s);
            File.WriteAllText(_path, JsonConvert.SerializeObject(s));
        }

        [Fact]
        public void External_writes_fire_once_after_debounce()
        {
            int calls = 0;
            QAdvanceFeedbackSettings last = null;
            using (new ConfigFileWatcher(_path, () => null, s => { last = s; Interlocked.Increment(ref calls); }, debounceMs: 150))
            {
                WriteSettings(s => s.GForce.AccelMaxMode = GMaxMode.Fixed);
                Thread.Sleep(30);
                WriteSettings(s => s.GForce.LatMaxMode = GMaxMode.Fixed);
                Thread.Sleep(1200);
            }
            Assert.Equal(1, calls);
            Assert.Equal(GMaxMode.Fixed, last.GForce.LatMaxMode);
        }

        [Fact]
        public void Own_save_is_ignored()
        {
            int calls = 0;
            string json = JsonConvert.SerializeObject(new QAdvanceFeedbackSettings { Version = 7 });
            string hash = ConfigFileWatcher.HashOf(System.Text.Encoding.UTF8.GetBytes(json));
            using (new ConfigFileWatcher(_path, () => hash, _ => Interlocked.Increment(ref calls), debounceMs: 100))
            {
                File.WriteAllText(_path, json);
                Thread.Sleep(1000);
            }
            Assert.Equal(0, calls);
        }

        [Fact]
        public void Corrupt_file_is_ignored()
        {
            int calls = 0;
            using (new ConfigFileWatcher(_path, () => null, _ => Interlocked.Increment(ref calls), debounceMs: 100))
            {
                File.WriteAllText(_path, "{ not json");
                Thread.Sleep(1000);
            }
            Assert.Equal(0, calls);
        }
    }

    /// <summary>Slip Angle and IntegrateSlipAngle shared fields (docs\slip-angle-design.md 9.10).</summary>
    public class SlipAngleSettingsHubTests
    {
        private const string Game = "AssettoCorsa";

        private static SettingsHub NewHub(out QAdvanceFeedbackSettings settings, int[] saves = null, string game = Game)
        {
            var s = new QAdvanceFeedbackSettings();
            settings = s;
            return new SettingsHub(() => s, _ => { if (saves != null) saves[0]++; }, () => game);
        }

        private static QAdvanceFeedbackSettings Clone(QAdvanceFeedbackSettings s) =>
            JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>(JsonConvert.SerializeObject(s));

        private static Core.SlipAngle.SlipAngleWindow W(double a, double b, double c) => new Core.SlipAngle.SlipAngleWindow(a, b, c);

        [Fact]
        public void Existing_enum_values_keep_their_ordinals_and_new_ones_are_appended()
        {
            string[] expected =
            {
                "LockSourceMode", "SlipSourceMode", "AccelMaxMode", "DecelMaxMode", "LatMaxMode",
                "FixedAccelMaxG", "FixedDecelMaxG", "FixedLatMaxG", "LockProjector", "SlipProjector",
                "SourceAvailability", "LockKeyDataAuto", "SlipKeyDataAuto", "LockNormalizePattern",
                "SlipNormalizePattern", "LockKeyDataValues", "SlipKeyDataValues", "LateralDirection",
                "IntegrateWheelLockAndSlip", "ClutchApply", "ClutchBiteMode", "ClutchBitePoint",
                "ClutchFadedRange", "ClutchGamma", "ClutchAxis", "ClutchAxisInvert", "ClutchLearning",
                "SlipAngleTriggerBy", "SlipAngleBrakeThreshold", "SlipAngleThrottleThreshold",
                "SlipAngleKeyDataAuto", "SlipAngleKeyDataValues", "SlipAngleProjector", "IntegrateSlipAngle",
                "LockShakeItLegacyAlgorithm", "LockShakeItSensibility", "LockShakeItLockOnly",
                "SlipShakeItLegacyAlgorithm", "SlipShakeItMuteWhileLockActive",
                "SlipAngleSourceUnit",  // the unit dropdown (owner, 2026-11-21)
                "SlipAngleAngleSource"  // the angle source dropdown (owner, 2026-11-23)
            };
            for (int i = 0; i < expected.Length; i++)
                Assert.Equal(i, (int)Enum.Parse(typeof(SettingField), expected[i]));
            Assert.Equal(expected.Length, Enum.GetValues(typeof(SettingField)).Length);
        }

        [Fact]
        public void IntegrateSlipAngle_defaults_true_and_a_missing_field_loads_true()
        {
            Assert.True(new GForceSettings().IntegrateSlipAngle);
            var s = JsonConvert.DeserializeObject<QAdvanceFeedbackSettings>("{\"GForce\":{\"IntegrateWheelLockAndSlip\":false}}");
            Assert.True(s.GForce.IntegrateSlipAngle);
            Assert.False(s.GForce.IntegrateWheelLockAndSlip);
        }

        [Fact]
        public void Each_editable_slip_angle_field_round_trips_with_origin()
        {
            var saves = new[] { 0 };
            var hub = NewHub(out var s, saves);
            var events = new List<SettingsChangedEventArgs>();
            hub.Changed += (_, e) => events.Add(e);

            Assert.True(hub.Set(SettingField.SlipAngleTriggerBy, Core.SlipAngle.TriggerBy.Brake, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleBrakeThreshold, 12.0, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleThrottleThreshold, 33.0, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.IntegrateSlipAngle, false, SettingsOrigin.PluginPage));

            Assert.Equal(Core.SlipAngle.TriggerBy.Brake, s.SlipAngle.Gate.TriggerBy);
            Assert.Equal(12.0, s.SlipAngle.Gate.BrakeThreshold, 9);
            Assert.Equal(33.0, s.SlipAngle.Gate.ThrottleThreshold, 9);
            Assert.False((bool)SettingsHub.Read(s, SettingField.SlipAngleKeyDataAuto));
            Assert.Equal(W(4, 8, 12), SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, Game));
            Assert.False(s.GForce.IntegrateSlipAngle);
            Assert.Equal(6, saves[0]);
            Assert.Equal(6, events.Count);
            for (int i = 0; i < 5; i++) Assert.Equal(SettingsOrigin.ShakeItPanel, events[i].Origin);
            Assert.Equal(SettingsOrigin.PluginPage, events[5].Origin);
            Assert.Equal(new[] { SettingField.IntegrateSlipAngle }, events[5].Fields);
        }

        [Fact]
        public void Set_unchanged_slip_angle_value_does_nothing()
        {
            var saves = new[] { 0 };
            var hub = NewHub(out var s, saves);
            Assert.False(hub.Set(SettingField.SlipAngleBrakeThreshold, s.SlipAngle.Gate.BrakeThreshold, SettingsOrigin.ShakeItPanel));
            Assert.False(hub.Set(SettingField.IntegrateSlipAngle, true, SettingsOrigin.ShakeItPanel));
            Assert.Equal(0, saves[0]);
        }

        [Fact]
        public void Thresholds_clamp_to_0_100_and_a_clamped_back_write_is_refused()
        {
            var hub = NewHub(out var s);
            Assert.True(hub.Set(SettingField.SlipAngleBrakeThreshold, 250.0, SettingsOrigin.ShakeItPanel));
            Assert.Equal(100.0, s.SlipAngle.Gate.BrakeThreshold, 9);
            Assert.False(hub.Set(SettingField.SlipAngleBrakeThreshold, 300.0, SettingsOrigin.ShakeItPanel));
        }

        [Fact]
        public void Projector_is_broadcast_only()
        {
            Assert.False(SettingsHub.IsEditable(SettingField.SlipAngleProjector));
            var hub = NewHub(out _);
            Assert.Throws<InvalidOperationException>(() => hub.Set(SettingField.SlipAngleProjector, null, SettingsOrigin.ShakeItPanel));
        }

        [Fact]
        public void Key_data_values_are_refused_under_auto_and_invalid_windows_are_refused()
        {
            var hub = NewHub(out _);
            Assert.False(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));
            hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel);
            Assert.False(hub.Set(SettingField.SlipAngleKeyDataValues, W(8, 8.1, 12), SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));
        }

        [Fact]
        public void Key_data_follows_the_per_game_slot_of_the_current_game()
        {
            string game = "A";
            var s = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => s, _ => { }, () => game);
            hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel);
            s.SlipAngle.KeyData.SetPerGame(SettingsHub.SlipAngleIdentity(s.SlipAngle), true);

            hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel);
            game = "B";
            Assert.Null(SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, game));
            hub.Set(SettingField.SlipAngleKeyDataValues, W(5, 9, 13), SettingsOrigin.ShakeItPanel);
            Assert.Equal(W(5, 9, 13), SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, "B"));
            Assert.Equal(W(4, 8, 12), SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, "A"));
        }

        [Fact]
        public void Plugin_internal_writes_touch_only_the_current_tier_identity()
        {
            var tier = Core.SourceNativeness.Fallback;
            var s = new QAdvanceFeedbackSettings();
            var hub = new SettingsHub(() => s, _ => { }, () => Game, () => tier);
            string native = Core.SlipAngle.SlipAngleFrameProcessor.NativeIdentity;
            string fallback = Core.SlipAngle.SlipAngleFrameProcessor.FallbackIdentity;

            Assert.True(hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));
            Assert.False(s.SlipAngle.KeyData.GetAutoGenerate(fallback));
            Assert.True(s.SlipAngle.KeyData.GetAutoGenerate(native));
            Assert.True(s.SlipAngle.KeyData.TryGetSlot(Game, fallback, out _));
            Assert.False(s.SlipAngle.KeyData.TryGetSlot(Game, native, out _));
            Assert.Equal(W(4, 8, 12), SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, Game, tier));
            Assert.Null(SettingsHub.Read(s, SettingField.SlipAngleKeyDataValues, Game, Core.SourceNativeness.Native));

            tier = Core.SourceNativeness.Native;   // reverse: a Native-tier write leaves Fallback alone
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel));
            Assert.True(hub.Set(SettingField.SlipAngleKeyDataValues, W(5, 9, 13), SettingsOrigin.ShakeItPanel));
            Assert.True(s.SlipAngle.KeyData.TryGetSlot(Game, native, out var n));
            Assert.Equal(W(5, 9, 13), n);
            Assert.True(s.SlipAngle.KeyData.TryGetSlot(Game, fallback, out var f));
            Assert.Equal(W(4, 8, 12), f);
        }

        [Fact]
        public void Unknown_tier_uses_the_native_identity()
        {
            var hub = NewHub(out var s);
            hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel);
            Assert.False(s.SlipAngle.KeyData.GetAutoGenerate(Core.SlipAngle.SlipAngleFrameProcessor.NativeIdentity));
            Assert.True(s.SlipAngle.KeyData.GetAutoGenerate(Core.SlipAngle.SlipAngleFrameProcessor.FallbackIdentity));
        }

        [Fact]
        public void Refused_key_data_commit_neither_saves_nor_broadcasts()
        {
            var saves = new[] { 0 };
            var hub = NewHub(out _, saves);
            int events = 0;
            hub.Changed += (_, __) => events++;
            Assert.False(hub.Set(SettingField.SlipAngleKeyDataValues, W(4, 8, 12), SettingsOrigin.ShakeItPanel));   // Auto
            hub.Set(SettingField.SlipAngleKeyDataAuto, false, SettingsOrigin.ShakeItPanel);
            saves[0] = 0; events = 0;
            Assert.False(hub.Set(SettingField.SlipAngleKeyDataValues, W(8, 8.1, 12), SettingsOrigin.ShakeItPanel)); // invalid
            Assert.Equal(0, saves[0]);
            Assert.Equal(0, events);
        }

        [Fact]
        public void Diff_reports_only_the_changed_slip_angle_fields()
        {
            var a = new QAdvanceFeedbackSettings();
            var b = Clone(a);
            b.SlipAngle.Gate.BrakeThreshold += 3;
            b.SlipAngle.Pulse.Enabled = !a.SlipAngle.Pulse.Enabled;   // not a shared field
            Assert.Equal(new[] { SettingField.SlipAngleBrakeThreshold }, SettingsHub.Diff(a, b, Game));

            var c = Clone(a);
            c.SlipAngle.Projector.ModerateOutput += 5;
            c.GForce.IntegrateSlipAngle = false;
            c.SlipAngle.Gate.TriggerBy = Core.SlipAngle.TriggerBy.Throttle;
            c.SlipAngle.Gate.ThrottleThreshold += 1;
            var d = SettingsHub.Diff(a, c, Game);
            Assert.Equal(4, d.Count);
            Assert.Contains(SettingField.SlipAngleProjector, d);
            Assert.Contains(SettingField.IntegrateSlipAngle, d);
            Assert.Contains(SettingField.SlipAngleTriggerBy, d);
            Assert.Contains(SettingField.SlipAngleThrottleThreshold, d);
        }

        [Fact]
        public void NotifyFileChanged_adopts_differing_slip_angle_fields_with_file_origin()
        {
            var saves = new[] { 0 };
            var hub = NewHub(out var s, saves);
            var events = new List<SettingsChangedEventArgs>();
            hub.Changed += (_, e) => events.Add(e);

            var disk = Clone(s);
            disk.SlipAngle.Gate.TriggerBy = Core.SlipAngle.TriggerBy.Throttle;
            disk.SlipAngle.Gate.BrakeThreshold = 20;
            disk.GForce.IntegrateSlipAngle = false;
            hub.NotifyFileChanged(disk);

            Assert.Equal(Core.SlipAngle.TriggerBy.Throttle, s.SlipAngle.Gate.TriggerBy);
            Assert.Equal(20.0, s.SlipAngle.Gate.BrakeThreshold, 9);
            Assert.False(s.GForce.IntegrateSlipAngle);
            Assert.Equal(1, saves[0]);
            Assert.Single(events);
            Assert.Equal(SettingsOrigin.File, events[0].Origin);
            Assert.Equal(3, events[0].Fields.Count);
        }
    }
}
