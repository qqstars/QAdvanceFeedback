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

            // IntegrateWheelLockAndSlip now defaults ON (docs\integrate-default-report.md) - a legitimate
            // default change, not a weakened assertion: the owner wants a fresh install to feel this
            // without hunting for the toggle.
            Assert.True(loaded.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(3.0, loaded.GForce.ShakeFrequencyHz, 6);
            Assert.Equal(1.5, loaded.GForce.WheelLockShakeScale, 6);
            Assert.Equal(1.5, loaded.GForce.WheelSlipShakeScale, 6);
        }

        [Fact]
        public void Save_then_Load_round_trips_the_shake_settings_when_explicitly_disabled()
        {
            var settings = new QAdvanceFeedbackSettings();
            // Explicitly OFF here (rather than relying on the now-ON default) so this test still proves
            // the round trip carries a non-default value, not just echoes the shipped default back.
            settings.GForce.IntegrateWheelLockAndSlip = false;
            settings.GForce.ShakeFrequencyHz = 14.0;
            settings.GForce.WheelLockShakeScale = 2.5;
            settings.GForce.WheelSlipShakeScale = 0.5;

            ConfigStore.Save(_path, settings);
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.False(loaded.GForce.IntegrateWheelLockAndSlip);
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

        // ---------------------------------------------------------------------------------------
        // Aggregation (docs\aggregation-report.md) round-tripping through the real JSON file.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Missing_file_yields_the_owners_aggregation_defaults_for_both_channels()
        {
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal(0.45, loaded.Lock.AggregationWMax, 9);
            Assert.Equal(0.55, loaded.Lock.AggregationWMin, 9);
            Assert.Equal(0.90, loaded.Lock.AggregationWFront, 9);
            Assert.Equal(0.10, loaded.Lock.AggregationWRear, 9);
            Assert.Equal(0.0, loaded.Lock.SlipFloorFactor, 9);

            Assert.Equal(0.55, loaded.Slip.AggregationWMax, 9);
            Assert.Equal(0.45, loaded.Slip.AggregationWMin, 9);
            Assert.Equal(0.65, loaded.Slip.AggregationWFront, 9);
            Assert.Equal(0.35, loaded.Slip.AggregationWRear, 9);
            Assert.Equal(0.4, loaded.Slip.SlipFloorFactor, 9);
        }

        [Fact]
        public void Save_then_Load_round_trips_edited_aggregation_weights_for_both_channels()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.Lock.AggregationWMax = 0.3;
            settings.Lock.AggregationWMin = 0.7;
            settings.Lock.AggregationWFront = 0.8;
            settings.Lock.AggregationWRear = 0.2;
            settings.Lock.SlipFloorFactor = 0.15;

            settings.Slip.AggregationWMax = 0.2;
            settings.Slip.AggregationWMin = 0.8;
            settings.Slip.AggregationWFront = 0.1;
            settings.Slip.AggregationWRear = 0.9;
            settings.Slip.SlipFloorFactor = 0.6;

            ConfigStore.Save(_path, settings);
            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal(0.3, loaded.Lock.AggregationWMax, 9);
            Assert.Equal(0.7, loaded.Lock.AggregationWMin, 9);
            Assert.Equal(0.8, loaded.Lock.AggregationWFront, 9);
            Assert.Equal(0.2, loaded.Lock.AggregationWRear, 9);
            Assert.Equal(0.15, loaded.Lock.SlipFloorFactor, 9);

            Assert.Equal(0.2, loaded.Slip.AggregationWMax, 9);
            Assert.Equal(0.8, loaded.Slip.AggregationWMin, 9);
            Assert.Equal(0.1, loaded.Slip.AggregationWFront, 9);
            Assert.Equal(0.9, loaded.Slip.AggregationWRear, 9);
            Assert.Equal(0.6, loaded.Slip.SlipFloorFactor, 9);
        }

        [Fact]
        public void RestoreDefaults_restores_the_owners_aggregation_weights_through_the_full_settings_object()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.Lock.AggregationWMax = 0.0;
            settings.Slip.SlipFloorFactor = 0.0;

            settings.RestoreDefaults();

            Assert.Equal(0.45, settings.Lock.AggregationWMax, 9);
            Assert.Equal(0.4, settings.Slip.SlipFloorFactor, 9);
        }

        [Fact]
        public void A_config_file_saved_before_this_feature_existed_still_loads_this_channels_real_aggregation_defaults()
        {
            // Simulates a settings file from before the aggregation feature existed - no
            // AggregationW*/SlipFloorFactor keys at all for Lock. Newtonsoft's default object-population
            // behaviour (ObjectCreationHandling.Auto) REUSES the existing Lock instance - which
            // QAdvanceFeedbackSettings' own field initialiser already set to CreateLockDefaults(), not a
            // bare WheelChannelSettings() - and only overwrites the JSON-present properties
            // (SourceFrontLeft here), so every absent property (including the five new aggregation ones)
            // keeps CreateLockDefaults()' REAL Lock numbers automatically. No explicit migration is
            // needed for a property added to WheelChannelSettings, exactly like every other field added
            // to this class after BrakeThresholdPercent/ThrottleThresholdPercent first shipped.
            File.WriteAllText(_path, "{ \"Lock\": { \"SourceFrontLeft\": \"Custom.Wheel.FL\" } }");

            QAdvanceFeedbackSettings loaded = ConfigStore.Load(_path);

            Assert.Equal("Custom.Wheel.FL", loaded.Lock.SourceFrontLeft);
            Assert.Equal(0.45, loaded.Lock.AggregationWMax, 9);
            Assert.Equal(0.55, loaded.Lock.AggregationWMin, 9);
            Assert.Equal(0.90, loaded.Lock.AggregationWFront, 9);
            Assert.Equal(0.10, loaded.Lock.AggregationWRear, 9);
            Assert.Equal(0.0, loaded.Lock.SlipFloorFactor, 9);
        }
    }
}
