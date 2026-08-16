using QAdvanceFeedback.Core;
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
        public void LockSensibility_is_clamped_to_0_100_and_defaults_to_50()
        {
            var fresh = new WheelChannelSettings();
            Assert.Equal(50.0, fresh.LockSensibility, 6);

            var s = new WheelChannelSettings { LockSensibility = -5.0 };
            Assert.Equal(0.0, s.LockSensibility, 6);
            s.LockSensibility = 250.0;
            Assert.Equal(100.0, s.LockSensibility, 6);
        }

        [Fact]
        public void Lock_and_slip_defaults_are_independent_instances()
        {
            WheelChannelSettings lockSettings = WheelChannelSettings.CreateLockDefaults();
            WheelChannelSettings slipSettings = WheelChannelSettings.CreateSlipDefaults();

            lockSettings.Projector.CriticalOutput = 5.0;

            Assert.NotEqual(lockSettings.Projector.CriticalOutput, slipSettings.Projector.CriticalOutput);
        }

        // ---------------------------------------------------------------------------------------
        // Aggregation (docs\aggregation-report.md) - the owner's Max/Min axle blend + Front/Rear
        // weight-transfer scheme, replacing the retired p-norm/GroupMode aggregation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Lock_defaults_ship_the_owners_tested_aggregation_weights_exactly()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();

            Assert.Equal(0.45, s.AggregationWMax, 9);
            Assert.Equal(0.55, s.AggregationWMin, 9);
            Assert.Equal(0.90, s.AggregationWFront, 9);
            Assert.Equal(0.10, s.AggregationWRear, 9);
            Assert.Equal(0.0, s.SlipFloorFactor, 9);
        }

        [Fact]
        public void Slip_defaults_ship_the_owners_tested_aggregation_weights_exactly()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            Assert.Equal(0.55, s.AggregationWMax, 9);
            Assert.Equal(0.45, s.AggregationWMin, 9);
            Assert.Equal(0.65, s.AggregationWFront, 9);
            Assert.Equal(0.35, s.AggregationWRear, 9);
            Assert.Equal(0.4, s.SlipFloorFactor, 9);
        }

        [Fact]
        public void A_bare_new_instance_ships_the_neutral_no_floor_fallback_not_either_channels_real_numbers()
        {
            // A settings file saved before this feature existed (missing these JSON keys) deserialises
            // via the bare constructor, landing on the neutral fallback - NOT silently on one channel's
            // real numbers for the other channel too (see this class's own remarks on why a shared POCO
            // cannot give two different field-initialiser defaults).
            var fresh = new WheelChannelSettings();

            Assert.Equal(AggregationWeights.Neutral.WMax, fresh.AggregationWMax, 9);
            Assert.Equal(AggregationWeights.Neutral.WMin, fresh.AggregationWMin, 9);
            Assert.Equal(AggregationWeights.Neutral.WFront, fresh.AggregationWFront, 9);
            Assert.Equal(AggregationWeights.Neutral.WRear, fresh.AggregationWRear, 9);
            Assert.Equal(AggregationWeights.Neutral.SlipFloorFactor, fresh.SlipFloorFactor, 9);
        }

        [Fact]
        public void Aggregation_weight_setters_clamp_negative_values_to_zero()
        {
            var s = new WheelChannelSettings
            {
                AggregationWMax = -1.0,
                AggregationWMin = -2.0,
                AggregationWFront = -3.0,
                AggregationWRear = -4.0
            };

            Assert.Equal(0.0, s.AggregationWMax, 9);
            Assert.Equal(0.0, s.AggregationWMin, 9);
            Assert.Equal(0.0, s.AggregationWFront, 9);
            Assert.Equal(0.0, s.AggregationWRear, 9);
        }

        [Fact]
        public void Aggregation_weight_setters_accept_values_above_one_unclamped()
        {
            // Only >= 0 is enforced - NOT a sum-to-1 constraint (see AggregationWeights' own remarks on
            // why: a driver's typed weights are never silently rescaled).
            var s = new WheelChannelSettings { AggregationWMax = 3.5 };
            Assert.Equal(3.5, s.AggregationWMax, 9);
        }

        [Fact]
        public void SlipFloorFactor_setter_clamps_to_0_1()
        {
            var s = new WheelChannelSettings { SlipFloorFactor = -0.5 };
            Assert.Equal(0.0, s.SlipFloorFactor, 9);

            s.SlipFloorFactor = 2.5;
            Assert.Equal(1.0, s.SlipFloorFactor, 9);

            s.SlipFloorFactor = 0.4;
            Assert.Equal(0.4, s.SlipFloorFactor, 9);
        }

        [Fact]
        public void ToAggregationWeights_round_trips_all_five_numbers()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();
            AggregationWeights weights = s.ToAggregationWeights();

            Assert.Equal(s.AggregationWMax, weights.WMax, 9);
            Assert.Equal(s.AggregationWMin, weights.WMin, 9);
            Assert.Equal(s.AggregationWFront, weights.WFront, 9);
            Assert.Equal(s.AggregationWRear, weights.WRear, 9);
            Assert.Equal(s.SlipFloorFactor, weights.SlipFloorFactor, 9);
        }

        [Fact]
        public void RestoreDefaults_restores_the_owners_aggregation_weights_after_being_customised()
        {
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.AggregationWMax = 0.0;
            settings.Lock.AggregationWMin = 0.0;
            settings.Slip.SlipFloorFactor = 0.0;

            settings.RestoreDefaults();

            Assert.Equal(0.45, settings.Lock.AggregationWMax, 9);
            Assert.Equal(0.55, settings.Lock.AggregationWMin, 9);
            Assert.Equal(0.4, settings.Slip.SlipFloorFactor, 9);
        }
    }
}
