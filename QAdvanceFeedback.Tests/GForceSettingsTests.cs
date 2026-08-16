using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceSettings"/> - FIXED vs AUTO mode resolution, per-game/per-car
    /// learned-value exposure, and the recommended shaker frequency range. Model/algorithm only, no
    /// UI, no persistence (see GForceSettings' own remarks for what a later task still needs to wire).
    /// </summary>
    public class GForceSettingsTests
    {
        [Fact]
        public void Fixed_mode_ignores_learned_values_even_when_a_higher_max_was_confirmed()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Fixed, FixedAccelMaxG = 0.9 };

            // Confirm (two consecutive similar frames) a learned value well above the fixed one.
            settings.ObserveAccelG("Game", "Car", 5.0);
            settings.ObserveAccelG("Game", "Car", 5.0);
            Assert.Equal(5.0, settings.GetLearnedAccelMaxG("Game", "Car"), 6); // learner itself did learn it...

            // ...but FIXED mode must not use it.
            Assert.Equal(0.9, settings.EffectiveAccelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Fixed_mode_ignores_learned_values_for_deceleration_too()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Fixed, FixedDecelMaxG = 2.0 };
            settings.ObserveDecelG("Game", "Car", 19.9);
            settings.ObserveDecelG("Game", "Car", 19.9);

            Assert.Equal(2.0, settings.EffectiveDecelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Auto_mode_uses_the_learned_value_once_confirmed()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            settings.ObserveAccelG("Game", "Car", 1.3);
            settings.ObserveAccelG("Game", "Car", 1.3);

            Assert.Equal(1.3, settings.EffectiveAccelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Auto_mode_falls_back_to_the_fixed_value_as_a_seed_before_anything_is_learned()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            // No observations yet for this key.
            Assert.Equal(0.9, settings.EffectiveAccelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Auto_mode_rejects_a_single_spike_exactly_like_the_underlying_learner()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 2.0 };
            settings.ObserveDecelG("Game", "Car", 1.5);
            settings.ObserveDecelG("Game", "Car", 1.5);
            settings.ObserveDecelG("Game", "Car", 19.9); // single collision-spike frame
            settings.ObserveDecelG("Game", "Car", 1.5);

            Assert.Equal(1.5, settings.EffectiveDecelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Learned_maxima_do_not_bleed_between_different_cars_in_the_same_game()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            settings.ObserveAccelG("RaceGame", "RoadCar", 0.6);
            settings.ObserveAccelG("RaceGame", "RoadCar", 0.6);
            settings.ObserveAccelG("RaceGame", "GT3Car", 1.4);
            settings.ObserveAccelG("RaceGame", "GT3Car", 1.4);

            Assert.Equal(0.6, settings.EffectiveAccelMaxG("RaceGame", "RoadCar"), 9);
            Assert.Equal(1.4, settings.EffectiveAccelMaxG("RaceGame", "GT3Car"), 9);
        }

        [Fact]
        public void Current_game_and_car_context_drives_the_no_arg_learned_properties()
        {
            var settings = new GForceSettings();
            settings.ObserveAccelG("GameX", "CarX", 1.1);
            settings.ObserveAccelG("GameX", "CarX", 1.1);
            settings.ObserveDecelG("GameX", "CarX", 1.8);
            settings.ObserveDecelG("GameX", "CarX", 1.8);

            settings.SetCurrentGameAndCar("GameX", "CarX");
            Assert.Equal(1.1, settings.CurrentLearnedAccelMaxG, 9);
            Assert.Equal(1.8, settings.CurrentLearnedDecelMaxG, 9);

            settings.SetCurrentGameAndCar("GameY", "CarY");
            Assert.Equal(0.0, settings.CurrentLearnedAccelMaxG, 9); // different key -> nothing learned yet
        }

        [Fact]
        public void Fixed_max_setters_reject_non_positive_values()
        {
            var settings = new GForceSettings { FixedAccelMaxG = -1.0, FixedDecelMaxG = 0.0 };
            Assert.True(settings.FixedAccelMaxG > 0.0);
            Assert.True(settings.FixedDecelMaxG > 0.0);
        }

        [Fact]
        public void Recommended_frequency_range_defaults_to_the_owners_own_example_and_stays_in_device_bounds()
        {
            var settings = new GForceSettings();

            Assert.Equal(100.0, settings.RecommendedFromHz, 6);
            Assert.Equal(50.0, settings.RecommendedToHz, 6);
            Assert.InRange(settings.RecommendedFromHz, GForceSettings.DeviceMinHz, GForceSettings.DeviceMaxHz);
            Assert.InRange(settings.RecommendedToHz, GForceSettings.DeviceMinHz, GForceSettings.DeviceMaxHz);
            Assert.True(settings.RecommendedFromHz > settings.RecommendedToHz);
        }

        [Fact]
        public void Recommended_frequency_range_cannot_be_pushed_outside_device_capability()
        {
            var settings = new GForceSettings { RecommendedFromHz = 500.0, RecommendedToHz = 1.0 };

            Assert.Equal(GForceSettings.DeviceMaxHz, settings.RecommendedFromHz, 6);
            Assert.Equal(GForceSettings.DeviceMinHz, settings.RecommendedToHz, 6);
        }

        [Fact]
        public void Recommended_to_hz_cannot_be_configured_above_from_hz()
        {
            var settings = new GForceSettings { RecommendedFromHz = 100.0, RecommendedToHz = 250.0 };
            Assert.True(settings.RecommendedToHz <= settings.RecommendedFromHz);
        }

        // ---------------------------------------------------------------------------------------
        // Owner's requirement 4 (sustain floors) and requirement 3 (lateral direction toggle) - the
        // settings-level surface over GForceEngine's own model logic.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Sustain_percent_settings_default_to_the_re_derived_25_50_shape()
        {
            var settings = new GForceSettings();

            // Re-derived from the model's own chain topology (halving per hop from each chain's
            // terminal zone - see GForceEngine.BrakeBottomRearSustainFraction's remarks): the MIDDLE
            // zone of each chain sits at 50%, the FAR zone at 25% - a genuine spatial gradient,
            // replacing the old, flawed flat-50%-everywhere defaults.
            Assert.Equal(50.0, settings.BrakeBottomRearSustainPercent, 6);  // braking's middle zone
            Assert.Equal(25.0, settings.BrakeBackLowSustainPercent, 6);     // braking's far zone
            Assert.Equal(25.0, settings.AccelBottomRearSustainPercent, 6);  // acceleration's far zone
            Assert.Equal(50.0, settings.AccelBackLowSustainPercent, 6);     // acceleration's middle zone
        }

        [Fact]
        public void Sustain_percent_settings_are_clamped_to_0_100()
        {
            var settings = new GForceSettings
            {
                BrakeBottomRearSustainPercent = -10.0,
                BrakeBackLowSustainPercent = 150.0,
            };

            Assert.Equal(0.0, settings.BrakeBottomRearSustainPercent, 6);
            Assert.Equal(100.0, settings.BrakeBackLowSustainPercent, 6);
        }

        [Fact]
        public void Lateral_direction_defaults_to_Normal()
        {
            var settings = new GForceSettings();
            Assert.Equal(LateralDirectionMode.Normal, settings.LateralDirection);
        }

        [Fact]
        public void ApplyTo_maps_every_sustain_and_lateral_setting_onto_the_engine()
        {
            var settings = new GForceSettings
            {
                BrakeBottomRearSustainPercent = 30.0,
                BrakeBackLowSustainPercent = 20.0,
                AccelBottomRearSustainPercent = 40.0,
                AccelBackLowSustainPercent = 60.0,
                LateralDirection = LateralDirectionMode.Reversed,
                SustainTimeConstantSeconds = 0.5,
                TransientTimeConstantSeconds = 0.2,
                TransientGain = 2.0,
            };

            var engine = new GForceEngine();
            settings.ApplyTo(engine);

            Assert.Equal(0.30, engine.BrakeBottomRearSustainFraction, 6);
            Assert.Equal(0.20, engine.BrakeBackLowSustainFraction, 6);
            Assert.Equal(0.40, engine.AccelBottomRearSustainFraction, 6);
            Assert.Equal(0.60, engine.AccelBackLowSustainFraction, 6);
            Assert.Equal(LateralDirectionMode.Reversed, engine.LateralDirection);
            Assert.Equal(0.5, engine.SustainTimeConstantSeconds, 6);
            Assert.Equal(0.2, engine.TransientTimeConstantSeconds, 6);
            Assert.Equal(2.0, engine.TransientGain, 6);
        }

        [Fact]
        public void Washout_filter_settings_default_to_the_reasoned_values()
        {
            var settings = new GForceSettings();
            Assert.Equal(0.15, settings.SustainTimeConstantSeconds, 6);
            Assert.Equal(0.08, settings.TransientTimeConstantSeconds, 6);
            Assert.Equal(1.5, settings.TransientGain, 6);
        }

        [Fact]
        public void Washout_filter_settings_are_clamped_to_sane_ranges()
        {
            var settings = new GForceSettings
            {
                SustainTimeConstantSeconds = -1.0,
                TransientTimeConstantSeconds = 0.0,
                TransientGain = -5.0,
            };

            Assert.True(settings.SustainTimeConstantSeconds > 0.0);
            Assert.True(settings.TransientTimeConstantSeconds > 0.0);
            Assert.Equal(0.0, settings.TransientGain, 6);
        }

        [Fact]
        public void ApplyTo_does_nothing_and_does_not_throw_for_a_null_engine()
        {
            var settings = new GForceSettings();
            settings.ApplyTo(null); // must not throw
        }

        // ---------------------------------------------------------------------------------------
        // Owner-requested "Integrate Wheel Lock and Slip" shake settings.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Shake_settings_now_default_to_on_3Hz_and_scale_1_5()
        {
            // Floor 5->1 Hz, default 5->3 Hz, and both scale defaults 1.0->1.5 (docs\shake-tuning-report.md)
            // are legitimate default/floor CHANGES per driver feedback, not weakened assertions.
            // IntegrateWheelLockAndSlip itself later flipped OFF->ON (docs\integrate-default-report.md) -
            // also a legitimate, deliberate default change: the owner wants a fresh install to feel this
            // without hunting for the toggle. It stays behaviourally inert with no lock/slip signal wired
            // up (amplitude is gForceValue * (wheelValue/100) * scale, so wheelValue=0 gives a zero-width
            // band) - see GForceEngineShakeTests' "Wheel_value_of_zero..." coverage for that guarantee.
            var settings = new GForceSettings();

            Assert.True(settings.IntegrateWheelLockAndSlip);
            Assert.Equal(3.0, settings.ShakeFrequencyHz, 6);
            Assert.Equal(1.5, settings.WheelLockShakeScale, 6);
            Assert.Equal(1.5, settings.WheelSlipShakeScale, 6);
        }

        [Fact]
        public void ShakeFrequencyHz_is_clamped_to_1_20_in_the_setter_itself()
        {
            var settings = new GForceSettings { ShakeFrequencyHz = 0.1 };
            Assert.Equal(1.0, settings.ShakeFrequencyHz, 6);

            settings.ShakeFrequencyHz = 999.0;
            Assert.Equal(20.0, settings.ShakeFrequencyHz, 6);

            settings.ShakeFrequencyHz = 15.0;
            Assert.Equal(15.0, settings.ShakeFrequencyHz, 6);
        }

        /// <summary>MUTATION (a) evidence: a hand-edited config file (or any caller) must never be
        /// able to smuggle in a shake frequency below the new 1 Hz floor.</summary>
        [Fact]
        public void MUTATION_a_a_shake_frequency_below_1Hz_must_never_be_readable_back()
        {
            var settings = new GForceSettings { ShakeFrequencyHz = 0.0001 };
            Assert.Equal(1.0, settings.ShakeFrequencyHz, 6);
        }

        /// <summary>MUTATION (b) evidence: the G-Force shake's own frequency floor is a DIFFERENT
        /// setting from the Layer 5 pulse's own 200 ms (5 Hz) gap floor on the Wheel Lock/Slip tabs -
        /// this pins the pulse floor's own value so a change that accidentally touched the pulse
        /// instead of the shake would be caught here (see <c>PulseSettingsTests</c> for the pulse's own
        /// dedicated, more thorough coverage of that floor).</summary>
        [Fact]
        public void MUTATION_b_the_pulse_gap_floor_is_a_different_200ms_setting_unaffected_by_this_change()
        {
            Assert.Equal(200.0, QAdvanceFeedback.Core.Projection.PulseSettings.MinGapMs, 6);
        }

        [Fact]
        public void Shake_scales_reject_negative_values()
        {
            var settings = new GForceSettings { WheelLockShakeScale = -2.0, WheelSlipShakeScale = -3.0 };
            Assert.Equal(0.0, settings.WheelLockShakeScale, 6);
            Assert.Equal(0.0, settings.WheelSlipShakeScale, 6);
        }

        [Fact]
        public void ApplyTo_maps_every_shake_setting_onto_the_engine()
        {
            var settings = new GForceSettings
            {
                IntegrateWheelLockAndSlip = true,
                ShakeFrequencyHz = 11.0,
                WheelLockShakeScale = 2.0,
                WheelSlipShakeScale = 3.0,
            };

            var engine = new GForceEngine();
            settings.ApplyTo(engine);

            Assert.True(engine.IntegrateWheelLockAndSlip);
            Assert.Equal(11.0, engine.ShakeFrequencyHz, 6);
            Assert.Equal(2.0, engine.WheelLockShakeScale, 6);
            Assert.Equal(3.0, engine.WheelSlipShakeScale, 6);
        }

        [Fact]
        public void Learned_maxima_export_import_round_trips_through_a_plain_dictionary()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            settings.ObserveAccelG("Game", "Car", 1.2);
            settings.ObserveAccelG("Game", "Car", 1.2);
            settings.ObserveDecelG("Game", "Car", 2.4);
            settings.ObserveDecelG("Game", "Car", 2.4);

            settings.ExportLearnedMaxima(out Dictionary<string, double> accel, out Dictionary<string, double> decel);
            Assert.True(accel.Count > 0);
            Assert.True(decel.Count > 0);

            var restored = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            restored.ImportLearnedMaxima(accel, decel);

            Assert.Equal(1.2, restored.GetLearnedAccelMaxG("Game", "Car"), 6);
            Assert.Equal(2.4, restored.GetLearnedDecelMaxG("Game", "Car"), 6);
        }

        // ---------------------------------------------------------------------------------------
        // Owner's learning-validity-gate ask (docs\gforce-direction-fix-report.md) - IsFrameValidForLearning
        // wraps TelemetryLearningGate (see TelemetryLearningGateTests for the gate's own unit tests);
        // these tests confirm GForceSettings actually exposes and is wired to it.
        // ---------------------------------------------------------------------------------------

        private static ITelemetrySample Sample(double? newSpeedKmh, double? oldSpeedKmh, double dtSeconds = 0.02)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: oldSpeedKmh);
            var newFrame = new TelemetryFrame(groundSpeedKmh: newSpeedKmh);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromSeconds(dtSeconds));
        }

        [Fact]
        public void IsFrameValidForLearning_accepts_an_ordinary_driving_frame()
        {
            var settings = new GForceSettings();
            Assert.True(settings.IsFrameValidForLearning(Sample(100.0, 100.5)));
        }

        [Fact]
        public void IsFrameValidForLearning_rejects_a_teleport_sized_speed_jump()
        {
            var settings = new GForceSettings();
            settings.IsFrameValidForLearning(Sample(100.0, 100.0)); // establishes a baseline
            Assert.False(settings.IsFrameValidForLearning(Sample(300.0, 300.0)));
        }

        [Fact]
        public void ResetLearning_clears_the_gates_remembered_baseline_too()
        {
            var settings = new GForceSettings();
            settings.IsFrameValidForLearning(Sample(100.0, 100.0));

            settings.ResetLearning();

            // Without the reset, jumping straight to 300 km/h would read as a teleport against the
            // pre-reset 100 km/h baseline.
            Assert.True(settings.IsFrameValidForLearning(Sample(300.0, 300.0)));
        }

        // ---------------------------------------------------------------------------------------
        // Owner's plausibility-limit refinement: asymmetric, DERIVED learning caps (6g accel / 8g
        // decel - see GForceSettings.AccelLearnMaxPlausibleG/DecelLearnMaxPlausibleG's own remarks for
        // the real-world-peak derivation), tighter than the owner's own rougher 10g/20g proposal.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Accel_and_decel_learning_caps_are_asymmetric()
        {
            Assert.Equal(6.0, GForceSettings.AccelLearnMaxPlausibleG, 6);
            Assert.Equal(8.0, GForceSettings.DecelLearnMaxPlausibleG, 6);
            Assert.True(GForceSettings.DecelLearnMaxPlausibleG > GForceSettings.AccelLearnMaxPlausibleG,
                "braking is consistently harder than accelerating for road/GT/F1 content - decel's cap must be the looser of the two");
        }

        [Fact]
        public void A_legitimate_F1_magnitude_braking_event_is_still_learned_not_rejected()
        {
            // F1 braking peaks around 5-6g - comfortably under the 8g decel cap. A cap set too tight
            // would silently learn nothing and look identical to the plugin doing nothing at all.
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto };
            settings.ObserveDecelG("Game", "Car", 5.0);
            settings.ObserveDecelG("Game", "Car", 5.0);

            Assert.Equal(5.0, settings.EffectiveDecelMaxG("Game", "Car"), 6);
        }

        [Fact]
        public void A_legitimate_drag_launch_magnitude_acceleration_event_is_still_learned_not_rejected()
        {
            // A top-fuel drag launch peaks around 4-5g - comfortably under the 6g accel cap.
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto };
            settings.ObserveAccelG("Game", "Car", 4.5);
            settings.ObserveAccelG("Game", "Car", 4.5);

            Assert.Equal(4.5, settings.EffectiveAccelMaxG("Game", "Car"), 6);
        }

        [Fact]
        public void An_impact_magnitude_reading_is_rejected_by_both_learners()
        {
            // An 18g reading (wall-tap scale) exceeds BOTH the 6g accel and 8g decel caps - neither
            // learner should ever confirm it, however many times it repeats.
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            for (int i = 0; i < 5; i++)
            {
                settings.ObserveAccelG("Game", "Car", 18.0);
                settings.ObserveDecelG("Game", "Car", 18.0);
            }

            Assert.Equal(0.0, settings.GetLearnedAccelMaxG("Game", "Car"), 6);
            Assert.Equal(0.0, settings.GetLearnedDecelMaxG("Game", "Car"), 6);
        }

        [Fact]
        public void A_magnitude_between_the_two_caps_is_learned_for_decel_but_rejected_for_accel()
        {
            // 7.0g sits ABOVE the 6g accel cap but BELOW the 8g decel cap - the asymmetry made concrete.
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            settings.ObserveAccelG("Game", "Car", 7.0);
            settings.ObserveAccelG("Game", "Car", 7.0);
            settings.ObserveDecelG("Game", "Car", 7.0);
            settings.ObserveDecelG("Game", "Car", 7.0);

            Assert.Equal(0.0, settings.GetLearnedAccelMaxG("Game", "Car"), 6);
            Assert.Equal(7.0, settings.GetLearnedDecelMaxG("Game", "Car"), 6);
        }

        // ---------------------------------------------------------------------------------------
        // MUTATION EVIDENCE (b) in the report: "set the learning cap symmetric and loose (20g both
        // ways)" - reproduced directly below by constructing the loose-capped learner the mutation
        // would produce, contrasted with the CORRECT, tight-capped learner GForceSettings actually
        // builds (see the two tests immediately above this one). The real source mutation (loosening
        // GForceSettings.AccelLearnMaxPlausibleG/DecelLearnMaxPlausibleG to 20.0/20.0) was additionally
        // performed manually and reverted - see docs\gforce-direction-fix-report.md for that run.
        // ---------------------------------------------------------------------------------------
        [Fact]
        public void MUTATION_evidence_a_loose_symmetric_20g_cap_would_have_learned_the_impact_reading()
        {
            var looseLearner = new QAdvanceFeedback.Core.GForce.GForceMaxLearner(learnCapG: 20.0);
            looseLearner.Observe("Game", "Car", 18.0);
            looseLearner.Observe("Game", "Car", 18.0);

            // This IS the implausible-maximum failure mode the tight, derived cap (used by the real
            // GForceSettings, see An_impact_magnitude_reading_is_rejected_by_both_learners above) exists
            // to prevent.
            Assert.Equal(18.0, looseLearner.GetLearnedMax("Game", "Car"), 6);
        }
    }
}
