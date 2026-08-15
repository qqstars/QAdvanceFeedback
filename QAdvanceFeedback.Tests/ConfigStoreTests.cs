using System;
using System.IO;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>ConfigStore.cs is SimHub-free (logging is an injectable Action&lt;string&gt; instead
    /// of a direct SimHub.Logging call) so it is link-compiled into this test project and exercised
    /// against real temporary files - same convention as the sibling ReliableWheelLockSlip project's
    /// ConfigStoreTests.</summary>
    public class ConfigStoreTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-config-" + Guid.NewGuid() + ".json");
        private readonly string _legacyPath = Path.Combine(Path.GetTempPath(), "qaf-legacy-config-" + Guid.NewGuid() + ".json");

        public void Dispose()
        {
            TryDelete(_path);
            TryDelete(_path + ".tmp");
            TryDelete(_legacyPath);
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }

        [Fact]
        public void Missing_file_yields_exactly_the_shipped_defaults()
        {
            Assert.False(File.Exists(_path));

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            // Owner-confirmed global default is ShakeIt Plugin Output Properties, not Manual/Raw.
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", loaded.Lock.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft", loaded.Slip.SourceFrontLeft);
            Assert.Equal(60.0, loaded.Lock.Projector.ModerateInput, 6);
            Assert.False(loaded.Lock.Pulse.Enabled);
        }

        [Fact]
        public void Save_then_Load_round_trips_edited_values()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.Lock.SourceFrontLeft = "MyCustom.Wheel.FL";
            settings.Lock.Projector.CriticalOutput = 42.0;
            settings.Slip.Pulse.Enabled = true;
            settings.Slip.Pulse.GapMs = 350.0;

            ConfigStore.Save(_path, settings);
            Assert.True(File.Exists(_path));

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal("MyCustom.Wheel.FL", loaded.Lock.SourceFrontLeft);
            Assert.Equal(42.0, loaded.Lock.Projector.CriticalOutput, 6);
            Assert.True(loaded.Slip.Pulse.Enabled);
            Assert.Equal(350.0, loaded.Slip.Pulse.GapMs, 6);
        }

        [Fact]
        public void Save_leaves_no_leftover_tmp_file_behind()
        {
            ConfigStore.Save(_path, new QAdvanceFeedbackSettings());
            Assert.False(File.Exists(_path + ".tmp"));
        }

        [Fact]
        public void Corrupt_json_degrades_to_defaults_without_throwing()
        {
            File.WriteAllText(_path, "{ this is not valid json at all ][");

            var warnings = 0;
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path, _ => warnings++);

            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", loaded.Lock.SourceFrontLeft);
            Assert.True(warnings > 0);
        }

        [Fact]
        public void Truncated_json_degrades_to_defaults_without_throwing()
        {
            File.WriteAllText(_path, "{\r\n  \"Version\": 1,\r\n  \"Lock\": { \"SourceFrontLeft\": \"Cal");

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", loaded.Lock.SourceFrontLeft);
        }

        [Fact]
        public void A_pulse_gap_persisted_below_200ms_by_a_hand_edited_file_still_loads_floored()
        {
            // Even a hand-edited config file cannot smuggle in a sub-200ms gap: Newtonsoft sets the
            // backing field directly during deserialisation... but PulseSettings.GapMs's setter is
            // exactly what Newtonsoft calls for a public auto-property, so the floor still applies.
            File.WriteAllText(_path,
                "{ \"Lock\": { \"Pulse\": { \"Enabled\": true, \"GapMs\": 50.0, \"MinValue\": 50.0 } } }");

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal(PulseSettings.MinGapMs, loaded.Lock.Pulse.GapMs, 6);
        }

        // ---------------------------------------------------------------------------------------
        // GForce/General round-tripping - the wiring task's addition, composing the previously
        // independent GForceSettings/GeneralSettings groups into the same root object/config file.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Missing_file_yields_GForce_and_General_defaults_too()
        {
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.NotNull(loaded.GForce);
            Assert.NotNull(loaded.General);
            Assert.Equal(GMaxMode.Fixed, loaded.GForce.AccelMaxMode);
            Assert.Equal(0.9, loaded.GForce.FixedAccelMaxG, 6);
            Assert.Equal(50.0, loaded.GForce.BrakeBottomRearSustainPercent, 6);
            Assert.Equal(LateralDirectionMode.Normal, loaded.GForce.LateralDirection);
            Assert.False(loaded.General.EnableDiagnostics);
            Assert.False(loaded.General.ExportCsv);
        }

        [Fact]
        public void Save_then_Load_round_trips_every_GForce_and_General_field()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.GForce.AccelMaxMode = GMaxMode.Auto;
            settings.GForce.DecelMaxMode = GMaxMode.Auto;
            settings.GForce.FixedAccelMaxG = 1.1;
            settings.GForce.FixedDecelMaxG = 2.5;
            settings.GForce.BrakeBottomRearSustainPercent = 30.0;
            settings.GForce.BrakeBackLowSustainPercent = 20.0;
            settings.GForce.AccelBottomRearSustainPercent = 40.0;
            settings.GForce.AccelBackLowSustainPercent = 60.0;
            settings.GForce.LateralDirection = LateralDirectionMode.Reversed;
            settings.GForce.RecommendedFromHz = 250.0;
            settings.GForce.RecommendedToHz = 30.0;
            settings.General.EnableDiagnostics = true;
            settings.General.ExportCsv = true;

            ConfigStore.Save(_path, settings);
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal(GMaxMode.Auto, loaded.GForce.AccelMaxMode);
            Assert.Equal(GMaxMode.Auto, loaded.GForce.DecelMaxMode);
            Assert.Equal(1.1, loaded.GForce.FixedAccelMaxG, 6);
            Assert.Equal(2.5, loaded.GForce.FixedDecelMaxG, 6);
            Assert.Equal(30.0, loaded.GForce.BrakeBottomRearSustainPercent, 6);
            Assert.Equal(20.0, loaded.GForce.BrakeBackLowSustainPercent, 6);
            Assert.Equal(40.0, loaded.GForce.AccelBottomRearSustainPercent, 6);
            Assert.Equal(60.0, loaded.GForce.AccelBackLowSustainPercent, 6);
            Assert.Equal(LateralDirectionMode.Reversed, loaded.GForce.LateralDirection);
            Assert.Equal(250.0, loaded.GForce.RecommendedFromHz, 6);
            Assert.Equal(30.0, loaded.GForce.RecommendedToHz, 6);
            Assert.True(loaded.General.EnableDiagnostics);
            Assert.True(loaded.General.ExportCsv);
        }

        [Fact]
        public void Missing_file_yields_the_shake_settings_own_defaults()
        {
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.False(loaded.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(5.0, loaded.GForce.ShakeFrequencyHz, 6);
            Assert.Equal(1.0, loaded.GForce.WheelLockShakeScale, 6);
            Assert.Equal(1.0, loaded.GForce.WheelSlipShakeScale, 6);
        }

        [Fact]
        public void Save_then_Load_round_trips_the_shake_settings()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.GForce.IntegrateWheelLockAndSlip = true;
            settings.GForce.ShakeFrequencyHz = 14.0;
            settings.GForce.WheelLockShakeScale = 2.5;
            settings.GForce.WheelSlipShakeScale = 0.5;

            ConfigStore.Save(_path, settings);
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.True(loaded.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(14.0, loaded.GForce.ShakeFrequencyHz, 6);
            Assert.Equal(2.5, loaded.GForce.WheelLockShakeScale, 6);
            Assert.Equal(0.5, loaded.GForce.WheelSlipShakeScale, 6);
        }

        // ---------------------------------------------------------------------------------------
        // Config file rename (plugin.QAdvanceFeedback.config.json -> QAdvanceFeedback.config.json) -
        // a file still present under the old name is imported exactly once.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_legacy_named_config_file_is_imported_once_when_the_new_file_does_not_exist()
        {
            var legacySettings = new QAdvanceFeedbackSettings();
            legacySettings.Lock.SourceFrontLeft = "MyLegacy.Wheel.FL";
            ConfigStore.Save(_legacyPath, legacySettings);

            Assert.False(File.Exists(_path));

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path, legacyPath: _legacyPath);

            Assert.Equal("MyLegacy.Wheel.FL", loaded.Lock.SourceFrontLeft);

            // Imported settings are saved straight back out under the NEW name, so the import only
            // ever happens once.
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void The_legacy_config_file_is_ignored_once_the_new_file_already_exists()
        {
            var currentSettings = new QAdvanceFeedbackSettings();
            currentSettings.Lock.SourceFrontLeft = "Current.Wheel.FL";
            ConfigStore.Save(_path, currentSettings);

            var legacySettings = new QAdvanceFeedbackSettings();
            legacySettings.Lock.SourceFrontLeft = "MyLegacy.Wheel.FL";
            ConfigStore.Save(_legacyPath, legacySettings);

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path, legacyPath: _legacyPath);

            Assert.Equal("Current.Wheel.FL", loaded.Lock.SourceFrontLeft);
        }

        [Fact]
        public void A_missing_legacy_config_file_still_degrades_to_defaults_without_throwing()
        {
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path, legacyPath: _legacyPath);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", loaded.Lock.SourceFrontLeft);
        }
    }
}
