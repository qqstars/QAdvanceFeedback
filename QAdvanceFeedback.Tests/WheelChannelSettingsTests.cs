using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class WheelChannelSettingsTests
    {
        [Fact]
        public void Lock_defaults_point_at_this_channels_own_Raw_property_not_ShakeIt()
        {
            // FLIPPED (docs\relative-fallback-and-raw-default-report.md): the shipped global default is
            // now Manual/Raw, NOT "ShakeIt Plugin Output Properties" - see
            // WheelChannelSettings.SourceMode's own remarks for the evidence (Raw needs no SimHub-side
            // setup and reads consistently across cars/conditions; ShakeIt needs both export
            // configuration and its own per-car calibration to mature).
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();

            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", s.SourceFrontLeft);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontRight", s.SourceFrontRight);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearLeft", s.SourceRearLeft);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.RearRight", s.SourceRearRight);

            Assert.Equal(ScriptType.Plain, s.ScriptTypeFrontLeft);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeFrontRight);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeRearLeft);
            Assert.Equal(ScriptType.Plain, s.ScriptTypeRearRight);
        }

        [Fact]
        public void Slip_defaults_point_at_this_channels_own_Raw_property_not_ShakeIt()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.FrontLeft", s.SourceFrontLeft);
            Assert.Equal("QAdvanceFeedback.WheelSlip.Raw.RearRight", s.SourceRearRight);
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
            // Pre-release Change 2c: thresholds moved 60 -> 62 and 80 -> 78 (paired with flatten ranges
            // of 2 each) so the Ideal/Max Grip plateaus' own edges still land exactly on the shared band
            // boundaries 60 and 80.
            Assert.Equal(62.0, s.Projector.ModerateInput, 6);
            Assert.Equal(30.0, s.Projector.ModerateOutput, 6);
            Assert.Equal(78.0, s.Projector.CriticalInput, 6);
            // 1.0.6.0 (docs\release-1060-report.md, Part 4d): Critical/"Max Grip" OUTPUT moved 80 -> 60,
            // matching the sibling 1.0.6.2 pre-release branch - the owner's explicit request, since 80
            // shook too strong once braking reached the best-braking-force point.
            Assert.Equal(60.0, s.Projector.CriticalOutput, 6);
            Assert.Equal(3.0, s.Projector.SlightlyFlattenRange, 6);
            Assert.Equal(2.0, s.Projector.ModerateFlattenRange, 6);
            Assert.Equal(2.0, s.Projector.CriticalFlattenRange, 6);
        }

        [Fact]
        public void Slip_defaults_ship_the_owners_shared_band_boundaries_with_a_gentler_curve()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            // Same anchor INPUT positions as Lock (30/62/78/100 - the owner's stated band
            // boundaries apply to both channels, see docs\refinements-report.md; thresholds moved
            // 60/80 -> 62/78 by pre-release Change 2c so the flatten-range plateau edges land on the
            // shared 60/80 boundaries); only the OUTPUT values differ. REVISED
            // (docs\slip-source-consistency-report.md - a second round of owner seat-testing):
            // 8/20/75 -> 10/35/75, no longer dramatically gentler than Lock's own (10/30/80) in the
            // working range. FURTHER REVISED (this build, owner-confirmed): Critical (Max Grip) output
            // softened 75 -> 70, direct response to the owner's in-game report that ShakeIt "shakes
            // much harder than using Raw" on WheelSlip. WheelLock's own Critical output is unchanged
            // (stays 60) - only Slip's max-grip output moves.
            Assert.Equal(20.0, s.Projector.StartInput, 6);
            Assert.Equal(100.0, s.Projector.EndInput, 6);
            Assert.Equal(30.0, s.Projector.SlightlyInput, 6);
            Assert.Equal(10.0, s.Projector.SlightlyOutput, 6);
            Assert.Equal(62.0, s.Projector.ModerateInput, 6);
            Assert.Equal(35.0, s.Projector.ModerateOutput, 6);
            Assert.Equal(78.0, s.Projector.CriticalInput, 6);
            Assert.Equal(70.0, s.Projector.CriticalOutput, 6);
        }

        [Fact]
        public void CanonicalAtLimitAnchor_is_80()
        {
            // Rescaled from 75.0 (docs\anchor-rescale-report.md) so the grip limit sits at 80 on the
            // canonical Normalized scale.
            Assert.Equal(80.0, KeyedScaleLearner.CanonicalAtLimitAnchor, 6);
        }

        [Fact]
        public void Lock_and_slip_top_curve_plateau_edge_coincides_with_the_canonical_at_limit_anchor()
        {
            // THE ESSENTIAL COUPLING (docs\anchor-rescale-report.md), REVISED for pre-release Change 2c:
            // the top ("Max Grip") curve anchor's own THRESHOLD moved to 78 (from 80) specifically so
            // that, PAIRED WITH its own flatten range (2), the plateau's own UPPER EDGE
            // (threshold + range = 78 + 2 = 80) still coincides exactly with
            // KeyedScaleLearner.CanonicalAtLimitAnchor - the owner's own explicit intent ("we will get
            // the smooth range between 60 to 80, with flatten on the edge of ... 80"). The coincidence
            // that used to be against the raw threshold directly is now against the plateau edge -
            // still catches either number drifting without the other. Independent of Lock's own
            // Critical OUTPUT (80 on this branch, unlike the sibling 1.0.6.2 branch's 60) - this
            // coupling is purely about INPUT/plateau-edge positioning.
            WheelChannelSettings lockDefaults = WheelChannelSettings.CreateLockDefaults();
            WheelChannelSettings slipDefaults = WheelChannelSettings.CreateSlipDefaults();

            double lockPlateauEdge = lockDefaults.Projector.CriticalInput + lockDefaults.Projector.CriticalFlattenRange;
            double slipPlateauEdge = slipDefaults.Projector.CriticalInput + slipDefaults.Projector.CriticalFlattenRange;

            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, lockPlateauEdge, 6);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, slipPlateauEdge, 6);
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
        public void Lock_and_slip_defaults_ship_Manual_Raw_mode_and_the_owners_revised_pedal_thresholds()
        {
            WheelChannelSettings lockDefaults = WheelChannelSettings.CreateLockDefaults();
            WheelChannelSettings slipDefaults = WheelChannelSettings.CreateSlipDefaults();

            // FLIPPED (docs\relative-fallback-and-raw-default-report.md) - see this class's other tests'
            // own remarks for the evidence.
            Assert.Equal(SourceMode.Manual, lockDefaults.SourceMode);
            Assert.Equal(SourceMode.Manual, slipDefaults.SourceMode);

            Assert.Equal(20.0, lockDefaults.BrakeThresholdPercent, 6);
            // 100.0 - the owner's own explicit, deliberate instruction ("by default, set the break
            // pedal presses as 100%, which means only throttle pedal pressed will trigger wheelSlip"),
            // confirmed after driving it. A prior pass changed this to 20.0 (reasoning, correctly as a
            // standalone fact, that SimHub's own decompiled GetRpmSpeedSlipLegacy applies an identical
            // Brake>20 gate to both Lock and Slip - docs\raw-match-rootcause-report.md) but that
            // overrode the owner's own tested decision; it was reverted back to 100.0. A driver who
            // wants SimHub's brake-responsive Slip can still lower this to 20 themselves.
            Assert.Equal(100.0, slipDefaults.BrakeThresholdPercent, 6);
            Assert.Equal(40.0, slipDefaults.ThrottleThresholdPercent, 6);
        }

        [Fact]
        public void ApplyMotorsExportDefaults_points_all_four_sources_at_the_confirmed_ShakeIt_Motors_names()
        {
            WheelChannelSettings lockSettings = WheelChannelSettings.CreateLockDefaults();
            lockSettings.ApplyMotorsExportDefaults(isLockChannel: true);

            Assert.Equal(SourceMode.ShakeIt, lockSettings.SourceMode);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontLeft", lockSettings.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.FrontRight", lockSettings.SourceFrontRight);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearLeft", lockSettings.SourceRearLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelLock.IRacing.RearRight", lockSettings.SourceRearRight);
            Assert.Equal(ScriptType.Plain, lockSettings.ScriptTypeFrontLeft);

            WheelChannelSettings slipSettings = WheelChannelSettings.CreateSlipDefaults();
            slipSettings.ApplyMotorsExportDefaults(isLockChannel: false);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.FrontLeft", slipSettings.SourceFrontLeft);
            Assert.Equal("ShakeITMotorsV3Plugin.Export.WheelSlip.IRacing.RearRight", slipSettings.SourceRearRight);
        }

        [Fact]
        public void ResetSourcesToDefault_switches_back_to_Manual_even_after_ShakeIt_was_selected()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();
            s.ApplyMotorsExportDefaults(isLockChannel: true);
            Assert.Equal(SourceMode.ShakeIt, s.SourceMode);

            s.ResetSourcesToDefault(isLockChannel: true);

            Assert.Equal(SourceMode.Manual, s.SourceMode);
            Assert.Equal("QAdvanceFeedback.WheelLock.Raw.FrontLeft", s.SourceFrontLeft);
        }

        // ---------------------------------------------------------------------------------------
        // THE PER-SOURCE "Reset to default" button's actual model logic - follows the CURRENT mode,
        // never forces a switch (unlike ResetSourcesToDefault/ApplyMotorsExportDefaults, which each force
        // their own specific mode - those are still used INSIDE this method, see its own remarks).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ResetSourcesForCurrentMode_in_ShakeIt_mode_restores_the_four_ShakeIt_names_and_stays_in_ShakeIt_mode()
        {
            // Defaults are now Manual/Raw (docs\relative-fallback-and-raw-default-report.md), so ShakeIt
            // mode must be explicitly selected first to set up this test's own precondition.
            WheelChannelSettings s = WheelChannelSettings.CreateLockDefaults();
            s.ApplyMotorsExportDefaults(isLockChannel: true);
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
            // mode, one of these two assertions would fail. Defaults are Manual/Raw now (docs\
            // relative-fallback-and-raw-default-report.md), so ShakeIt mode is set up explicitly here.
            WheelChannelSettings shakeIt = WheelChannelSettings.CreateLockDefaults();
            shakeIt.ApplyMotorsExportDefaults(isLockChannel: true);
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

            // REVISED (docs\slip-source-consistency-report.md): WMax/WMin 0.45/0.55 -> 0.75/0.25.
            Assert.Equal(0.75, s.AggregationWMax, 9);
            Assert.Equal(0.25, s.AggregationWMin, 9);
            Assert.Equal(0.90, s.AggregationWFront, 9);
            Assert.Equal(0.10, s.AggregationWRear, 9);
            Assert.Equal(0.0, s.SlipFloorFactor, 9);
        }

        [Fact]
        public void Slip_defaults_ship_the_owners_tested_aggregation_weights_exactly()
        {
            WheelChannelSettings s = WheelChannelSettings.CreateSlipDefaults();

            // REVISED (docs\slip-source-consistency-report.md): WMax/WMin 0.55/0.45 -> 0.85/0.15;
            // WFront/WRear FLIPPED 0.65/0.35 -> 0.45/0.55; floor 0.4 -> 0.70.
            Assert.Equal(0.85, s.AggregationWMax, 9);
            Assert.Equal(0.15, s.AggregationWMin, 9);
            Assert.Equal(0.45, s.AggregationWFront, 9);
            Assert.Equal(0.55, s.AggregationWRear, 9);
            Assert.Equal(0.70, s.SlipFloorFactor, 9);
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

            // REVISED (docs\slip-source-consistency-report.md): Lock WMax/WMin 0.45/0.55 -> 0.75/0.25;
            // Slip floor 0.4 -> 0.70.
            Assert.Equal(0.75, settings.Lock.AggregationWMax, 9);
            Assert.Equal(0.25, settings.Lock.AggregationWMin, 9);
            Assert.Equal(0.70, settings.Slip.SlipFloorFactor, 9);
        }

        // ------------------------------------------------------------------------------------
        // NormalizePattern (1.0.6.0, docs\release-1060-report.md, Part 2's UI half) - Lock-only
        // selector between the four-range Mapping formula and the simpler MaxGripOnly one. The
        // property lives on the shared WheelChannelSettings class (same convention as LockSensibility,
        // which is likewise "only meaningful for Lock") so it round-trips through Newtonsoft alongside
        // every other setting, without needing a bespoke JSON path.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void NormalizePattern_ships_three_point_on_Lock_and_single_point_on_Slip()
        {
            // THE TWO CHANNELS SHIP DIFFERENT PATTERNS, and that asymmetry is the point of this test
            // (owner's call, re-confirmed 2026-08-28 after a build had wrongly shipped single-point on
            // both). Lock measures all three of its anchors physically - LockAnchorLearner reads the
            // source at 90% and 75% of the corner's own g-limit - so the full mapping is what a fresh
            // install should feel. Slip has no such 90%/75% measurement: its lower anchors are DERIVED
            // from the Perfect point by fixed percentages, which is not a good enough reason to curve a
            // fresh install. Either channel can still be switched to either pattern by hand.
            Assert.Equal(NormalizePattern.Mapping, WheelChannelSettings.CreateLockDefaults().NormalizePattern);
            Assert.Equal(NormalizePattern.MaxGripOnly, WheelChannelSettings.CreateSlipDefaults().NormalizePattern);

            // A bare instance keeps the property initialiser's Mapping - only the channel factories
            // express the shipped choice.
            Assert.Equal(NormalizePattern.Mapping, new WheelChannelSettings().NormalizePattern);
        }

        [Fact]
        public void NormalizePattern_round_trips_through_JSON_serialisation()
        {
            var settings = WheelChannelSettings.CreateLockDefaults();
            settings.NormalizePattern = NormalizePattern.MaxGripOnly;

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(settings);
            var restored = Newtonsoft.Json.JsonConvert.DeserializeObject<WheelChannelSettings>(json);

            Assert.Equal(NormalizePattern.MaxGripOnly, restored.NormalizePattern);
        }

        [Fact]
        public void RestoreDefaults_resets_each_channel_to_its_own_shipped_pattern()
        {
            // Both channels are set to the pattern the OTHER one ships, so a restore that ignored the
            // per-channel distinction - or that reset both to one shared value - fails here rather than
            // passing trivially.
            var settings = QAdvanceFeedbackSettings.CreateDefault();
            settings.Lock.NormalizePattern = NormalizePattern.MaxGripOnly;
            settings.Slip.NormalizePattern = NormalizePattern.Mapping;

            settings.RestoreDefaults();

            Assert.Equal(NormalizePattern.Mapping, settings.Lock.NormalizePattern);
            Assert.Equal(NormalizePattern.MaxGripOnly, settings.Slip.NormalizePattern);
        }
    }
}
