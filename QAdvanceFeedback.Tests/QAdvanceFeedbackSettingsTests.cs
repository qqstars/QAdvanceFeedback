using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class QAdvanceFeedbackSettingsTests
    {
        [Fact]
        public void Fresh_settings_have_independent_lock_and_slip_channels_with_their_own_defaults()
        {
            var settings = new QAdvanceFeedbackSettings();

            Assert.NotNull(settings.Lock);
            Assert.NotNull(settings.Slip);
            Assert.NotSame(settings.Lock, settings.Slip);

            // Global default is now Manual/Raw (docs\relative-fallback-and-raw-default-report.md -
            // FLIPPED from the earlier ShakeIt Plugin Output Properties default) - see
            // WheelChannelSettings.SourceMode's own remarks for the evidence.
            Assert.Equal(SourceMode.Manual, settings.Lock.SourceMode);
            Assert.Equal(SourceMode.Manual, settings.Slip.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", settings.Lock.SourceFrontLeft);
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.FrontLeft", settings.Slip.SourceFrontLeft);

            // Pre-release Change 2c: threshold moved 60 -> 62 (paired with a flatten range of 2) so the
            // Ideal plateau's own edge still lands exactly on the shared 60 band boundary.
            Assert.Equal(62.0, settings.Lock.Projector.ModerateInput, 6);
            Assert.Equal(62.0, settings.Slip.Projector.ModerateInput, 6); // shared band boundary
            Assert.NotEqual(settings.Lock.Projector.ModerateOutput, settings.Slip.Projector.ModerateOutput);
        }

        [Fact]
        public void Version_defaults_to_one()
        {
            Assert.Equal(1, new QAdvanceFeedbackSettings().Version);
        }

        // ---------------------------------------------------------------------------------------
        // "Restore all default settings" - a single internally-defined default object derives both
        // the shipped defaults and the settings UI's restore button, so they cannot drift.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void RestoreDefaults_resets_every_customised_field_across_all_four_tabs()
        {
            var settings = new QAdvanceFeedbackSettings();

            // Touch at least one field per tab/group.
            settings.Lock.SourceFrontLeft = "Custom.Lock.FL";
            settings.Lock.Projector.CriticalOutput = 42.0;
            settings.Slip.SourceFrontLeft = "Custom.Slip.FL";
            settings.Slip.Pulse.Enabled = true;
            settings.GForce.FixedAccelMaxG = 5.0;
            settings.GForce.AccelMaxMode = GMaxMode.Auto;
            // Touch it to the NON-default value (false) - the default is now true, so setting it to
            // true here would not actually exercise RestoreDefaults for this field.
            settings.GForce.IntegrateWheelLockAndSlip = false;
            settings.GForce.ShakeFrequencyHz = 18.0;
            settings.GForce.WheelLockShakeScale = 4.0;
            settings.GForce.WheelSlipShakeScale = 4.0;
            settings.General.EnableDiagnostics = true;
            settings.General.ExportCsv = true;

            settings.RestoreDefaults();

            QAdvanceFeedbackSettings expected = QAdvanceFeedbackSettings.CreateDefault();

            Assert.Equal(expected.Lock.SourceFrontLeft, settings.Lock.SourceFrontLeft);
            Assert.Equal(expected.Lock.Projector.CriticalOutput, settings.Lock.Projector.CriticalOutput, 6);
            Assert.Equal(expected.Slip.SourceFrontLeft, settings.Slip.SourceFrontLeft);
            Assert.Equal(expected.Slip.Pulse.Enabled, settings.Slip.Pulse.Enabled);
            Assert.Equal(expected.GForce.FixedAccelMaxG, settings.GForce.FixedAccelMaxG, 6);
            Assert.Equal(expected.GForce.AccelMaxMode, settings.GForce.AccelMaxMode);
            Assert.Equal(expected.GForce.IntegrateWheelLockAndSlip, settings.GForce.IntegrateWheelLockAndSlip);
            Assert.Equal(expected.GForce.ShakeFrequencyHz, settings.GForce.ShakeFrequencyHz, 6);
            Assert.Equal(expected.GForce.WheelLockShakeScale, settings.GForce.WheelLockShakeScale, 6);
            Assert.Equal(expected.GForce.WheelSlipShakeScale, settings.GForce.WheelSlipShakeScale, 6);
            Assert.Equal(expected.General.EnableDiagnostics, settings.General.EnableDiagnostics);
            Assert.Equal(expected.General.ExportCsv, settings.General.ExportCsv);
        }

        [Fact]
        public void RestoreDefaults_keeps_Lock_and_Slip_as_independent_instances_afterwards()
        {
            var settings = new QAdvanceFeedbackSettings();
            settings.RestoreDefaults();

            Assert.NotSame(settings.Lock, settings.Slip);
            settings.Lock.Projector.CriticalOutput = 5.0;
            Assert.NotEqual(settings.Lock.Projector.CriticalOutput, settings.Slip.Projector.CriticalOutput);
        }

        [Fact]
        public void CreateDefault_matches_a_plain_new_instances_shipped_defaults()
        {
            QAdvanceFeedbackSettings shipped = new QAdvanceFeedbackSettings();
            QAdvanceFeedbackSettings viaFactory = QAdvanceFeedbackSettings.CreateDefault();

            Assert.Equal(shipped.Lock.SourceFrontLeft, viaFactory.Lock.SourceFrontLeft);
            Assert.Equal(shipped.Slip.Projector.ModerateInput, viaFactory.Slip.Projector.ModerateInput, 6);
            Assert.Equal(shipped.GForce.FixedAccelMaxG, viaFactory.GForce.FixedAccelMaxG, 6);
            Assert.Equal(shipped.General.EnableDiagnostics, viaFactory.General.EnableDiagnostics);
        }
    }
}
