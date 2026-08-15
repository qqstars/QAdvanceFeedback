using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class WheelChannelSettingsTests
    {
        [Fact]
        public void Lock_defaults_point_at_the_confirmed_ShakeIt_Motors_export_names()
        {
            // Owner-confirmed global default: "ShakeIt Plugin Output Properties" (SourceMode.ShakeIt),
            // NOT Manual/Raw - see WheelChannelSettings.SourceMode's own remarks.
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();

            Assert.Equal(SourceMode.ShakeIt, s.SourceMode);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", s.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight", s.SourceFrontRight);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft", s.SourceRearLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight", s.SourceRearRight);

            Assert.Equal(ScriptType.Plain, s.ScriptTypeFrontLeft);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeFrontRight);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeRearLeft);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeRearRight);
        }

        [Fact]
        public void Slip_defaults_point_at_the_confirmed_ShakeIt_Motors_export_names()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            Assert.Equal(SourceMode.ShakeIt, s.SourceMode);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft", s.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight", s.SourceRearRight);
        }

        [Fact]
        public void Manual_modes_own_default_is_this_channels_Raw_property_not_ShakeIt()
        {
            // ResetSourcesToDefault (forces Manual) is what Manual mode's own default actually is -
            // confirmed Raw.*, NOT Projected.* (Projected is Layer 5's OUTPUT and these fields are
            // Layer 4's INPUT - defaulting to Projected would be circular: Layer 5 -> Layer 4 -> Layer 5).
            WheelChannelSettings lock_ = new WheelChannelSettings();
            lock_.ResetSourcesToDefault(isLockChannel: true);
            Assert.Equal(SourceMode.Manual, lock_.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", lock_.SourceFrontLeft);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontRight", lock_.SourceFrontRight);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearLeft", lock_.SourceRearLeft);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearRight", lock_.SourceRearRight);

            WheelChannelSettings slip = new WheelChannelSettings();
            slip.ResetSourcesToDefault(isLockChannel: false);
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.FrontLeft", slip.SourceFrontLeft);
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.RearRight", slip.SourceRearRight);
        }

        [Fact]
        public void Lock_defaults_ship_the_briefs_lock_curve_numbers()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();

            Assert.Equal(20.0, s.Projector.StartInput, 6);
            Assert.Equal(100.0, s.Projector.EndInput, 6);
            Assert.Equal(30.0, s.Projector.SlightlyInput, 6);
            Assert.Equal(10.0, s.Projector.SlightlyOutput, 6);
            Assert.Equal(60.0, s.Projector.ModerateInput, 6);
            Assert.Equal(30.0, s.Projector.ModerateOutput, 6);
            Assert.Equal(80.0, s.Projector.CriticalInput, 6);
            Assert.Equal(80.0, s.Projector.CriticalOutput, 6);
        }

        [Fact]
        public void Slip_defaults_ship_the_owners_shared_band_boundaries_with_a_gentler_curve()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            // Same anchor INPUT positions as Lock (30/60/80/100 - the owner's stated band
            // boundaries apply to both channels, see docs\refinements-report.md); only the OUTPUT
            // values differ, kept gentler than Lock's own (10/30/80).
            Assert.Equal(20.0, s.Projector.StartInput, 6);
            Assert.Equal(100.0, s.Projector.EndInput, 6);
            Assert.Equal(30.0, s.Projector.SlightlyInput, 6);
            Assert.Equal(8.0, s.Projector.SlightlyOutput, 6);
            Assert.Equal(60.0, s.Projector.ModerateInput, 6);
            Assert.Equal(20.0, s.Projector.ModerateOutput, 6);
            Assert.Equal(80.0, s.Projector.CriticalInput, 6);
            Assert.Equal(75.0, s.Projector.CriticalOutput, 6);
        }

        [Fact]
        public void Pulse_ships_disabled_with_a_200ms_floor_enforced()
        {
            WheelChannelSettings lockDefaults = WheelChannelSettings.CreateLockDefaults();
            Assert.False(lockDefaults.Pulse.Enabled);
            Assert.Equal(50.0, lockDefaults.Pulse.MinValue, 6);

            lockDefaults.Pulse.GapMs = 10.0;
            Assert.Equal(PulseSettings.MinGapMs, lockDefaults.Pulse.GapMs, 6);
        }

        [Fact]
        public void ResetSourcesToDefault_restores_plain_layer3_references_after_being_customised()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();
            s.SourceFrontLeft = "$prop(\"Something.Custom\")";
            s.ScriptTypeFrontLeft = ScriptType.JavaScript;

            s.ResetSourcesToDefault(isLockChannel: true);

            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", s.SourceFrontLeft);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeFrontLeft);
        }

        [Fact]
        public void Lock_and_slip_defaults_ship_ShakeIt_mode_and_the_owners_revised_pedal_thresholds()
        {
            WheelChannelSettings lockDefaults = WheelChannelSettings.CreateLockDefaults();
            WheelChannelSettings slipDefaults = WheelChannelSettings.CreateSlipDefaults();

            Assert.Equal(SourceMode.ShakeIt, lockDefaults.SourceMode);
            Assert.Equal(SourceMode.ShakeIt, slipDefaults.SourceMode);

            Assert.Equal(20.0, lockDefaults.BrakeThresholdPercent, 6);
            Assert.Equal(100.0, slipDefaults.BrakeThresholdPercent, 6);
            Assert.Equal(40.0, slipDefaults.ThrottleThresholdPercent, 6);
        }

        [Fact]
        public void ApplyShakeItDefaults_points_all_four_sources_at_the_confirmed_ShakeIt_Motors_names()
        {
            WheelChannelSettings lockSettings = WheelChannelSettings.CreateLockDefaults();
            lockSettings.ApplyShakeItDefaults(isLockChannel: true);

            Assert.Equal(SourceMode.ShakeIt, lockSettings.SourceMode);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", lockSettings.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight", lockSettings.SourceFrontRight);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft", lockSettings.SourceRearLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight", lockSettings.SourceRearRight);
            Assert.Equal(ScriptType.Plain, lockSettings.ScriptTypeFrontLeft);

            WheelChannelSettings slipSettings = WheelChannelSettings.CreateSlipDefaults();
            slipSettings.ApplyShakeItDefaults(isLockChannel: false);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft", slipSettings.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight", slipSettings.SourceRearRight);
        }

        [Fact]
        public void ResetSourcesToDefault_switches_back_to_Manual_even_after_ShakeIt_was_selected()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();
            s.ApplyShakeItDefaults(isLockChannel: true);
            Assert.Equal(SourceMode.ShakeIt, s.SourceMode);

            s.ResetSourcesToDefault(isLockChannel: true);

            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", s.SourceFrontLeft);
        }

        // ---------------------------------------------------------------------------------------
        // THE PER-SOURCE "Reset to default" button's actual model logic - follows the CURRENT mode,
        // never forces a switch (unlike ResetSourcesToDefault/ApplyShakeItDefaults, which each force
        // their own specific mode - those are still used INSIDE this method, see its own remarks).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ResetSourcesForCurrentMode_in_ShakeIt_mode_restores_the_four_ShakeIt_names_and_stays_in_ShakeIt_mode()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults(); // already ShakeIt by default
            s.SourceFrontLeft = "SomethingTypedOverTheShakeItName";

            s.ResetSourcesForCurrentMode(isLockChannel: true);

            Assert.Equal(SourceMode.ShakeIt, s.SourceMode);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", s.SourceFrontLeft);
        }

        [Fact]
        public void ResetSourcesForCurrentMode_in_Manual_mode_restores_this_channels_Raw_property_and_stays_in_Manual_mode()
        {
            WheelChannelSettings s = new WheelChannelSettings();
            s.ResetSourcesToDefault(isLockChannel: true); // switch to Manual first
            s.SourceFrontLeft = "SomethingTypedOverTheRawName";

            s.ResetSourcesForCurrentMode(isLockChannel: true);

            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", s.SourceFrontLeft);
        }

        [Fact]
        public void ResetSourcesForCurrentMode_never_switches_the_mode_itself_either_way()
        {
            // MUTATION (c) target: if this method ignored SourceMode and always reset to one fixed
            // mode, one of these two assertions would fail.
            WheelChannelSettings shakeIt = WheelChannelSettings.CreateLockDefaults();
            shakeIt.ResetSourcesForCurrentMode(isLockChannel: true);
            Assert.Equal(SourceMode.ShakeIt, shakeIt.SourceMode);

            WheelChannelSettings manual = new WheelChannelSettings();
            manual.ResetSourcesToDefault(isLockChannel: true);
            manual.ResetSourcesForCurrentMode(isLockChannel: true);
            Assert.Equal(SourceMode.Manual, manual.SourceMode);
        }

        [Fact]
        public void BrakeThresholdPercent_and_ThrottleThresholdPercent_are_clamped_to_0_100()
        {
            var s = new WheelChannelSettings { BrakeThresholdPercent = -5.0, ThrottleThresholdPercent = 250.0 };
            Assert.Equal(0.0, s.BrakeThresholdPercent, 6);
            Assert.Equal(100.0, s.ThrottleThresholdPercent, 6);
        }

        [Fact]
        public void Lock_and_slip_defaults_are_independent_instances()
        {
            WheelChannelSettings lockSettings = WheelChannelSettings.CreateLockDefaults();
            WheelChannelSettings slipSettings = WheelChannelSettings.CreateSlipDefaults();

            lockSettings.Projector.CriticalOutput = 5.0;

            Assert.NotEqual(lockSettings.Projector.CriticalOutput, slipSettings.Projector.CriticalOutput);
        }
    }
}
