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
        private static DateTime T0 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Feeds <paramref name="count"/> qualifying acceleration observations 5ms apart,
        /// starting at <paramref name="start"/> - the volume the NEW estimator (200-sample minimum,
        /// 2-minute window) actually needs to mature, replacing the old 2-observation "confirm"
        /// idiom.</summary>
        private static void ObserveManyAccel(GForceSettings s, string game, string car, double value, int count, DateTime start, double stepMs = 5.0)
        {
            for (int i = 0; i < count; i++) s.ObserveAccelG(game, car, value, start.AddMilliseconds(i * stepMs));
        }

        private static void ObserveManyDecel(GForceSettings s, string game, string car, double value, int count, DateTime start, double stepMs = 5.0)
        {
            for (int i = 0; i < count; i++) s.ObserveDecelG(game, car, value, start.AddMilliseconds(i * stepMs));
        }

        /// <summary>A timestamp comfortably past BOTH the 200-sample crossing and the subsequent 2-second
        /// ramp, for tests that want the fully-settled effective value rather than a mid-ramp one.</summary>
        private static DateTime FullyRampedAt(DateTime lastObservationTime) => lastObservationTime.AddSeconds(5.0);

        [Fact]
        public void Fixed_mode_ignores_learned_values_even_when_a_higher_max_was_learned()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Fixed, FixedAccelMaxG = 0.9 };

            ObserveManyAccel(settings, "Game", "Car", 5.0, 200, T0);
            Assert.Equal(5.0, settings.GetLearnedAccelMaxG("Game", "Car"), 2); // learner itself did learn it...

            // ...but FIXED mode must not use it.
            Assert.Equal(0.9, settings.EffectiveAccelMaxG("Game", "Car", FullyRampedAt(T0)), 9);
        }

        [Fact]
        public void Fixed_mode_ignores_learned_values_for_deceleration_too()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Fixed, FixedDecelMaxG = 2.0 };
            ObserveManyDecel(settings, "Game", "Car", 5.0, 200, T0);

            Assert.Equal(2.0, settings.EffectiveDecelMaxG("Game", "Car", FullyRampedAt(T0)), 9);
        }

        [Fact]
        public void Auto_mode_uses_the_learned_value_once_matured_and_fully_ramped_in()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            ObserveManyAccel(settings, "Game", "Car", 1.3, 200, T0);
            DateTime lastObservation = T0.AddMilliseconds(199 * 5);

            // The ramp's own clock only advances when this is actually queried (mirroring the real,
            // once-per-frame call site) - query once right at maturity to start the ramp, then again
            // once it has had time to complete.
            settings.EffectiveAccelMaxG("Game", "Car", lastObservation);
            Assert.Equal(1.3, settings.EffectiveAccelMaxG("Game", "Car", FullyRampedAt(lastObservation)), 2);
        }

        [Fact]
        public void Auto_mode_falls_back_to_the_fixed_value_as_a_seed_before_anything_is_learned()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            // No observations yet for this key.
            Assert.Equal(0.9, settings.EffectiveAccelMaxG("Game", "Car"), 9);
        }

        [Fact]
        public void Auto_mode_stays_close_to_a_representative_band_despite_an_occasional_high_reading()
        {
            // The robust estimator's own outlier-resistance (see GForceMaxLearnerTests for the dedicated
            // unit coverage) wired through GForceSettings: a consistent band of real braking plus one
            // occasional high-but-plausible reading must not drag the effective value toward the spike.
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 2.0 };
            DateTime t = T0;
            int i = 0;
            for (; i < 195; i++) { settings.ObserveDecelG("Game", "Car", 1.5, t.AddMilliseconds(i * 5)); }
            for (; i < 200; i++) { settings.ObserveDecelG("Game", "Car", 6.5, t.AddMilliseconds(i * 5)); } // rare, high
            DateTime lastObservation = t.AddMilliseconds(199 * 5);

            settings.EffectiveDecelMaxG("Game", "Car", lastObservation); // starts the ramp
            double effective = settings.EffectiveDecelMaxG("Game", "Car", FullyRampedAt(lastObservation));
            Assert.True(effective < 2.5, $"effective {effective} should stay close to the representative 1.5g band, not the rare 6.5g reading");
        }

        [Fact]
        public void Learned_maxima_do_not_bleed_between_different_cars_in_the_same_game()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.9 };
            settings.ImportLearnedMaxima(
                new Dictionary<string, double>
                {
                    [GForceMaxLearner.MakeKey("RaceGame", "RoadCar")] = 0.6,
                    [GForceMaxLearner.MakeKey("RaceGame", "GT3Car")] = 1.4,
                },
                new Dictionary<string, double>());

            // The RAW learned value is available immediately (GetLearnedAccelMaxG, used by the UI
            // readout) - the EFFECTIVE (engine-fed) value may still ramp toward it if the jump from the
            // fixed default exceeds the step-trigger fraction (see EffectiveAccelMaxG's own remarks), so
            // query well past the 2-second ramp for a settled comparison here.
            Assert.Equal(0.6, settings.GetLearnedAccelMaxG("RaceGame", "RoadCar"), 9);
            Assert.Equal(1.4, settings.GetLearnedAccelMaxG("RaceGame", "GT3Car"), 9);

            DateTime t0 = T0;
            settings.EffectiveAccelMaxG("RaceGame", "RoadCar", t0); // starts the ramp, if any
            settings.EffectiveAccelMaxG("RaceGame", "GT3Car", t0);
            Assert.Equal(0.6, settings.EffectiveAccelMaxG("RaceGame", "RoadCar", FullyRampedAt(t0)), 9);
            Assert.Equal(1.4, settings.EffectiveAccelMaxG("RaceGame", "GT3Car", FullyRampedAt(t0)), 9);
        }

        [Fact]
        public void Current_game_and_car_context_drives_the_no_arg_learned_properties()
        {
            var settings = new GForceSettings();
            settings.ImportLearnedMaxima(
                new Dictionary<string, double> { [GForceMaxLearner.MakeKey("GameX", "CarX")] = 1.1 },
                new Dictionary<string, double> { [GForceMaxLearner.MakeKey("GameX", "CarX")] = 1.8 });

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
            Assert.Equal(1.2, settings.TransientGain, 6);
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
        public void Shake_settings_now_default_to_on_10Hz_and_scale_1_5()
        {
            // Floor 5->1 Hz, default 5->3 Hz (docs\shake-tuning-report.md), then 3->10 Hz
            // (docs\shake-frequency-default-report.md - the owner tried 3 Hz on real hardware and
            // reports 10 Hz feels much better; the 1-20 Hz bounds themselves are unchanged), and both
            // scale defaults 1.0->1.5 (docs\shake-tuning-report.md) are legitimate default/floor
            // CHANGES per driver feedback, not weakened assertions. IntegrateWheelLockAndSlip itself
            // later flipped OFF->ON (docs\integrate-default-report.md) - also a legitimate, deliberate
            // default change: the owner wants a fresh install to feel this without hunting for the
            // toggle. It stays behaviourally inert with no lock/slip signal wired up (amplitude is
            // gForceValue * (wheelValue/100) * scale, so wheelValue=0 gives a zero-width band) - see
            // GForceEngineShakeTests' "Wheel_value_of_zero..." coverage for that guarantee.
            var settings = new GForceSettings();

            Assert.True(settings.IntegrateWheelLockAndSlip);
            Assert.Equal(10.0, settings.ShakeFrequencyHz, 6);
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

        // ---------------------------------------------------------------------------------------
        // MODE-DEPENDENT TRANSITION SCALING (docs\robust-auto-gforce-report.md) - REPLACES the single
        // TransitionAnimationScale setting with two, blended continuously (never stepped) by the SAME
        // ramp weight EffectiveAccelMaxG/EffectiveDecelMaxG themselves use.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Transition_scale_defaults_are_owner_specified_1_2_auto_and_1_5_fixed()
        {
            var settings = new GForceSettings();
            Assert.Equal(1.2, settings.AutoTransitionAnimationScale, 6);
            Assert.Equal(1.5, settings.FixedTransitionAnimationScale, 6);
        }

        [Fact]
        public void Both_transition_scales_are_clamped_to_0_and_the_engines_configured_upper_bound()
        {
            var settings = new GForceSettings { AutoTransitionAnimationScale = -1.0, FixedTransitionAnimationScale = 999.0 };
            Assert.Equal(0.0, settings.AutoTransitionAnimationScale, 6);
            Assert.Equal(GForceEngine.MaxTransitionAnimationScale, settings.FixedTransitionAnimationScale, 6);

            settings.AutoTransitionAnimationScale = 2.5;
            Assert.Equal(2.5, settings.AutoTransitionAnimationScale, 6);
        }

        [Fact]
        public void Fixed_mode_transition_scale_is_always_the_fixed_setting()
        {
            var settings = new GForceSettings
            {
                AccelMaxMode = GMaxMode.Fixed, DecelMaxMode = GMaxMode.Fixed,
                AutoTransitionAnimationScale = 1.2, FixedTransitionAnimationScale = 1.5,
            };
            Assert.Equal(1.5, settings.EffectiveAccelTransitionScale("Game", "Car"), 9);
            Assert.Equal(1.5, settings.EffectiveDecelTransitionScale("Game", "Car"), 9);
        }

        [Fact]
        public void Auto_mode_below_the_sample_threshold_uses_the_fixed_transition_scale_not_the_auto_one()
        {
            // The EDGE CASE the brief calls out explicitly: below 200 samples the effective max IS the
            // fixed default, so the transition scale must ALSO read as the fixed one (1.5, not 1.2) -
            // otherwise the two settings would disagree about what "using the default" means.
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto };
            Assert.Equal(settings.FixedTransitionAnimationScale, settings.EffectiveAccelTransitionScale("Game", "Car"), 9);
        }

        [Fact]
        public void The_transition_scale_ramps_continuously_alongside_the_max_no_step_at_the_threshold()
        {
            var settings = new GForceSettings
            {
                AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.75,
                AutoTransitionAnimationScale = 1.2, FixedTransitionAnimationScale = 1.5,
            };
            ObserveManyAccel(settings, "Game", "Car", 2.0, 200, T0);
            DateTime lastObservation = T0.AddMilliseconds(199 * 5);

            double justBefore = settings.EffectiveAccelTransitionScale("Game", "Car", lastObservation.AddMilliseconds(-1));
            double atCrossing = settings.EffectiveAccelTransitionScale("Game", "Car", lastObservation);
            Assert.True(Math.Abs(atCrossing - justBefore) < 0.01,
                $"transition scale must not step at the sample threshold: {justBefore} -> {atCrossing}");

            double fullyRamped = settings.EffectiveAccelTransitionScale("Game", "Car", FullyRampedAt(lastObservation));
            Assert.Equal(1.2, fullyRamped, 2);
        }

        [Fact]
        public void Fixed_maxima_default_to_the_revised_075_and_15_values()
        {
            // REVISED defaults (docs\gforce-transition-scale-report.md): 0.9->0.75 (accel), 2.0->1.5
            // (decel) - a legitimate default change, not a weakened assertion.
            var settings = new GForceSettings();
            Assert.Equal(0.75, settings.FixedAccelMaxG, 6);
            Assert.Equal(1.5, settings.FixedDecelMaxG, 6);
        }

        [Fact]
        public void Learned_maxima_export_import_round_trips_through_a_plain_dictionary()
        {
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            ObserveManyAccel(settings, "Game", "Car", 1.2, 200, T0);
            ObserveManyDecel(settings, "Game", "Car", 2.4, 200, T0);

            settings.ExportLearnedMaxima(out Dictionary<string, double> accel, out Dictionary<string, double> decel);
            Assert.True(accel.Count > 0);
            Assert.True(decel.Count > 0);

            var restored = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto };
            restored.ImportLearnedMaxima(accel, decel);

            Assert.Equal(1.2, restored.GetLearnedAccelMaxG("Game", "Car"), 2);
            Assert.Equal(2.4, restored.GetLearnedDecelMaxG("Game", "Car"), 2);
            // The RAW value round-trips immediately (the owner's explicit "switching cars mid-session/
            // restart picks up that car's own value" requirement, satisfied by GetLearned*MaxG above) -
            // the EFFECTIVE (engine-fed) value settles to it too, once past any ramp the jump from the
            // fixed default might trigger.
            restored.EffectiveAccelMaxG("Game", "Car", T0);
            restored.EffectiveDecelMaxG("Game", "Car", T0);
            Assert.Equal(1.2, restored.EffectiveAccelMaxG("Game", "Car", FullyRampedAt(T0)), 2);
            Assert.Equal(2.4, restored.EffectiveDecelMaxG("Game", "Car", FullyRampedAt(T0)), 2);
        }

        // ---------------------------------------------------------------------------------------
        // DEFAULT MODE -> AUTO (docs\robust-auto-gforce-report.md, verification item 6) - AUTO's worst
        // case (no evidence) must be bit-for-bit identical to FIXED.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Accel_and_decel_max_mode_default_to_Auto()
        {
            var settings = new GForceSettings();
            Assert.Equal(GMaxMode.Auto, settings.AccelMaxMode);
            Assert.Equal(GMaxMode.Auto, settings.DecelMaxMode);
        }

        [Fact]
        public void Autos_worst_case_with_no_evidence_is_bit_for_bit_identical_to_fixed()
        {
            var auto = new GForceSettings { AccelMaxMode = GMaxMode.Auto, DecelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.75, FixedDecelMaxG = 1.5 };
            var fixedOnly = new GForceSettings { AccelMaxMode = GMaxMode.Fixed, DecelMaxMode = GMaxMode.Fixed, FixedAccelMaxG = 0.75, FixedDecelMaxG = 1.5 };

            Assert.Equal(fixedOnly.EffectiveAccelMaxG("Game", "Car"), auto.EffectiveAccelMaxG("Game", "Car"), 9);
            Assert.Equal(fixedOnly.EffectiveDecelMaxG("Game", "Car"), auto.EffectiveDecelMaxG("Game", "Car"), 9);
            Assert.Equal(fixedOnly.EffectiveAccelTransitionScale("Game", "Car"), auto.EffectiveAccelTransitionScale("Game", "Car"), 9);
            Assert.Equal(fixedOnly.EffectiveDecelTransitionScale("Game", "Car"), auto.EffectiveDecelTransitionScale("Game", "Car"), 9);
        }

        // ---------------------------------------------------------------------------------------
        // RAMP-IN WHEN AUTO ENGAGES (owner's explicit spec) - the worked examples: fixed=1.5, current
        // rises to 5.5g then 6.0g. 0.5s -> 2.5, 1.5s -> 4.5, 1.0s(current=6.0) -> 3.75.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Ramp_matches_the_owners_own_worked_examples()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 1.5 };
            ObserveManyDecel(settings, "Game", "Car", 5.5, 200, T0);
            DateTime crossing = T0.AddMilliseconds(199 * 5);

            settings.EffectiveDecelMaxG("Game", "Car", crossing); // starts the ramp at `crossing`

            Assert.Equal(2.5, settings.EffectiveDecelMaxG("Game", "Car", crossing.AddSeconds(0.5)), 2);
            Assert.Equal(4.5, settings.EffectiveDecelMaxG("Game", "Car", crossing.AddSeconds(1.5)), 2);

            // Before the 1.0s checkpoint, the underlying auto value rises to 6.0g (re-read LIVE each
            // query, not frozen at ramp start - the owner's own explicit requirement) - the trimmed
            // band's own top decile is homogeneous 6.0 once enough new readings arrive, regardless of
            // the earlier 5.5g cluster still sitting lower in the (still-unaged) window.
            ObserveManyDecel(settings, "Game", "Car", 6.0, 200, crossing.AddMilliseconds(1), stepMs: 3.0);

            // At exactly 1.0s elapsed since the ramp started, weight = 0.5 - applied to the NOW-live 6.0g
            // current value: 1.5 + 0.5*(6.0-1.5) = 3.75.
            Assert.Equal(3.75, settings.EffectiveDecelMaxG("Game", "Car", crossing.AddSeconds(1.0)), 1);
        }

        [Fact]
        public void Ramp_is_continuous_across_a_sweep_of_elapsed_time_no_step_anywhere()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 1.5 };
            ObserveManyDecel(settings, "Game", "Car", 5.0, 200, T0);
            DateTime crossing = T0.AddMilliseconds(199 * 5);
            settings.EffectiveDecelMaxG("Game", "Car", crossing);

            double previous = settings.EffectiveDecelMaxG("Game", "Car", crossing);
            for (double t = 0.05; t <= 2.5; t += 0.05)
            {
                double current = settings.EffectiveDecelMaxG("Game", "Car", crossing.AddSeconds(t));
                Assert.True(Math.Abs(current - previous) < 0.2,
                    $"ramp stepped between t={t - 0.05:0.00}s and t={t:0.00}s: {previous} -> {current}");
                previous = current;
            }
        }

        [Fact]
        public void Ramp_restarts_for_a_brand_new_car_key()
        {
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 1.5 };
            ObserveManyDecel(settings, "Game", "CarA", 4.0, 200, T0);
            DateTime crossing = T0.AddMilliseconds(199 * 5);
            settings.EffectiveDecelMaxG("Game", "CarA", crossing);
            Assert.Equal(4.0, settings.EffectiveDecelMaxG("Game", "CarA", FullyRampedAt(crossing)), 2);

            // A brand-new key (different car) has never had a ramp started - its effective value is
            // exactly the fixed default, immediately, regardless of what CarA's own ramp is doing.
            Assert.Equal(1.5, settings.EffectiveDecelMaxG("Game", "CarB", crossing), 9);
        }

        [Fact]
        public void A_large_drop_in_the_learned_value_is_ramped_not_stepped()
        {
            // No sample-count threshold to lose any more (the owner's revised, no-gate design) - what
            // now triggers a ramp in EITHER direction is simply the SIZE of the change in the learner's
            // own current estimate, e.g. a genuine surface/condition change once the old cluster ages out
            // of the 2-minute window and is replaced by a much lower one.
            var settings = new GForceSettings { DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 1.5 };
            ObserveManyDecel(settings, "Game", "Car", 4.0, 200, T0, stepMs: 500.0); // spans 0..99.5s
            DateTime crossing = T0.AddSeconds(99.5);
            settings.EffectiveDecelMaxG("Game", "Car", crossing);
            Assert.Equal(4.0, settings.EffectiveDecelMaxG("Game", "Car", FullyRampedAt(crossing)), 2);

            // Condition change: feed a new, much lower cluster far enough past the 2-minute window that
            // the old 4.0g cluster fully ages out first.
            DateTime t1 = crossing.AddMinutes(2).AddSeconds(1);
            ObserveManyDecel(settings, "Game", "Car", 1.6, 200, t1, stepMs: 5.0);
            DateTime lastNew = t1.AddMilliseconds(199 * 5);

            // The INSTANT the new, much-lower value is detected, the published effective value must not
            // step straight down to it.
            double justAfter = settings.EffectiveDecelMaxG("Game", "Car", lastNew);
            Assert.Equal(4.0, justAfter, 1);

            double fullySettled = settings.EffectiveDecelMaxG("Game", "Car", FullyRampedAt(lastNew));
            Assert.Equal(1.6, fullySettled, 1);
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
            ObserveManyDecel(settings, "Game", "Car", 5.0, 200, T0);

            Assert.Equal(5.0, settings.GetLearnedDecelMaxG("Game", "Car"), 2);
        }

        [Fact]
        public void A_legitimate_drag_launch_magnitude_acceleration_event_is_still_learned_not_rejected()
        {
            // A top-fuel drag launch peaks around 4-5g - comfortably under the 6g accel cap.
            var settings = new GForceSettings { AccelMaxMode = GMaxMode.Auto };
            ObserveManyAccel(settings, "Game", "Car", 4.5, 200, T0);

            Assert.Equal(4.5, settings.GetLearnedAccelMaxG("Game", "Car"), 2);
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
            ObserveManyAccel(settings, "Game", "Car", 7.0, 200, T0);
            ObserveManyDecel(settings, "Game", "Car", 7.0, 200, T0);

            Assert.Equal(0.0, settings.GetLearnedAccelMaxG("Game", "Car"), 6);
            Assert.Equal(7.0, settings.GetLearnedDecelMaxG("Game", "Car"), 2);
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
            looseLearner.Observe("Game", "Car", 18.0, T0);

            // This IS the implausible-maximum failure mode the tight, derived cap (used by the real
            // GForceSettings, see An_impact_magnitude_reading_is_rejected_by_both_learners above) exists
            // to prevent.
            Assert.Equal(18.0, looseLearner.GetLearnedMax("Game", "Car"), 2);
        }
    }
}
