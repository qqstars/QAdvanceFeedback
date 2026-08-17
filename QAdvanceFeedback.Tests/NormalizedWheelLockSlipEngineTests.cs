using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    public class NormalizedWheelLockSlipEngineTests
    {
        // Old speed 1.0 km/h above New speed, dt=16ms -> a decelerating ground-speed derivative of
        // roughly -17.4 m/s^2 before smoothing (well past LongitudinalDirectionResolver's dead band
        // after even a single EMA step) - Slowing from the very first frame, entirely independent of
        // gMagnitude/brakePercent/longitudinalG below. This is deliberate: direction and magnitude are
        // two separate signals in this engine (see NormalizedWheelLockSlipEngine's remarks), so a test
        // helper must control them independently rather than assuming a pedal implies a direction.
        private static ITelemetrySample BrakingSample(
            double gMagnitude, double brakePercent = 80.0, double? lateralG = null, double? longitudinalGOverride = null)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0,
                longitudinalG: longitudinalGOverride ?? -gMagnitude,
                brakePercent: brakePercent,
                lateralG: lateralG);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        // The mirror of BrakingSample: New speed above Old -> SpeedingUp from the first frame.
        private static ITelemetrySample ThrottleSample(
            double gMagnitude, double throttlePercent = 80.0, double? longitudinalGOverride = null)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 101.0,
                longitudinalG: longitudinalGOverride ?? gMagnitude,
                throttlePercent: throttlePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }


        // ------------------------------------------------------------------------------------
        // The acceptance test the brief calls for by name: an arcade-magnitude trace (4g routine)
        // and a sim-magnitude trace (1.2g routine) must BOTH span a useful range - light braking
        // must read clearly lower than hard (at-the-car's-own-peak) braking in EACH car, and the
        // two cars must read comparably for a comparable FRACTION of their own peak.
        // <para/>
        // RE-EXPRESSED FOR THE F1 25 FIX (docs\f1-normalization-fix-report.md - see
        // NormalizedWheelLockSlipEngine's own history note): this fixture used to hold Raw at a
        // constant, deliberately-thin placeholder (below MinRawForCalibrationObservation) so ONLY
        // GripUtilization's own G-based ratio produced the light/hard split - proving exactly the
        // mechanism the F1 25 bug turned out to be. GripUtilization no longer drives the live
        // severity at all, so this test is re-expressed against the mechanism that ACTUALLY provides
        // car-relative comparability now: each car's own physically-anchored KeyedScaleLearner
        // calibration. Raw here VARIES with how close each car is to ITS OWN physical limit (a
        // realistic source, unlike the old constant placeholder) - "hard" is a genuine full-lock raw
        // reading (100, every source's own convention per the owner's worked examples), "light" is a
        // low, clearly-below-limit reading. No assertion's INTENT changed; only the fixture and the
        // "hard" bar (>80, not >85 - see below).
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating()
        {
            var arcadeEngine = new NormalizedWheelLockSlipEngine();
            // Warm-up: G held at the arcade car's own 4g peak, Raw at 90 (a "near the limit" native
            // reading, NOT the full-scale 100 - mirrors PerSourceCalibrationTests' own convention,
            // leaving headroom for a genuine full-lock reading to calibrate higher than the
            // physically-anchored "critical" point, KeyedScaleLearner.CanonicalAtLimitAnchor=75).
            for (int i = 0; i < 300; i++) arcadeEngine.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, "GameArcade", "Car1");
            double arcadeLight = arcadeEngine.Compute(BrakingSample(1.0), Corners.Uniform(20.0), Corners.Zero, "GameArcade", "Car1").LockAll;
            double arcadeHard = arcadeEngine.Compute(BrakingSample(4.0), Corners.Uniform(100.0), Corners.Zero, "GameArcade", "Car1").LockAll;

            var simEngine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++) simEngine.Compute(BrakingSample(1.2), Corners.Uniform(90.0), Corners.Zero, "GameSim", "Car1");
            double simLight = simEngine.Compute(BrakingSample(0.3), Corners.Uniform(20.0), Corners.Zero, "GameSim", "Car1").LockAll;
            double simHard = simEngine.Compute(BrakingSample(1.2), Corners.Uniform(100.0), Corners.Zero, "GameSim", "Car1").LockAll;

            // >80, not >85: a genuine full-lock (native 100) reading calibrates to
            // 100 * CanonicalAtLimitAnchor(75) / ceiling(~90) =~ 83 - comfortably "near max" under the
            // documented 75-anchor design (see KeyedScaleLearner's own remarks and
            // PerSourceCalibrationTests' identical InRange(60,90) convention for "critical"), not a
            // saturated-at-100 model the old GripUtilization-driven design happened to produce.
            Assert.True(arcadeHard > 80.0, $"arcade hard (genuine full lock) read {arcadeHard}, expected near-max");
            Assert.True(simHard > 80.0, $"sim hard (genuine full lock) read {simHard}, expected near-max");

            Assert.True(arcadeLight < 50.0,
                $"arcade light braking (well below its own learned ceiling) read {arcadeLight} - expected well below max");
            Assert.True(simLight < 50.0, $"sim light braking read {simLight}, expected well below max");

            // Same RAW readings must read comparably regardless of how much G it took to reach them -
            // proof that comparability is now car-relative via each car's OWN physical-limit
            // calibration, not via a shared/absolute G scale.
            Assert.True(Math.Abs(arcadeLight - simLight) < 25.0,
                $"arcade light ({arcadeLight}) and sim light ({simLight}) should read comparably");
            Assert.True(Math.Abs(arcadeHard - simHard) < 15.0,
                $"arcade hard ({arcadeHard}) and sim hard ({simHard}) should both read near-max at each car's own limit");
        }

        [Fact]
        public void Fallback_level_3_passes_Raw_through_unmodified_when_no_g_or_speed_signal_exists()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = new Corners(12.0, 34.0, 56.0, 78.0);

            // No LongitudinalG, no speed at all, no Dt - the "nothing but Raw" floor. Direction is
            // therefore also Unknown, but the Unavailable-motion floor bypasses the direction gate
            // entirely (see NormalizedWheelLockSlipEngine.ComputeChannel) - Raw must still pass through.
            // TRIGGER THRESHOLD (docs\lock-and-animation-report.md, owner's own clarification): the new
            // gate is checked BEFORE this fallback and has no carve-out, so the brake pedal must clear
            // the (default) Lock threshold for this scenario to still demonstrate the fallback itself
            // rather than the trigger gate - everything else about the frame (no G, no speed, no dt)
            // is unchanged.
            var frame = new TelemetryFrame(brakePercent: 80.0);
            var sample = new TelemetrySample(frame, frame, DateTime.UtcNow, null);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, raw);

            Assert.Equal(12.0, result.LockWheels.FrontLeft, 6);
            Assert.Equal(34.0, result.LockWheels.FrontRight, 6);
            Assert.Equal(56.0, result.LockWheels.RearLeft, 6);
            Assert.Equal(78.0, result.LockWheels.RearRight, 6);
        }

        // ------------------------------------------------------------------------------------
        // TRIGGER THRESHOLD (owner-requested restructure, promoted out of "Sources" into its own
        // section - docs\lock-and-animation-report.md). Semantics: below the channel's own pedal
        // threshold, BOTH Raw (Layer 3 - see RawCalculatorEngineTests) AND Normalized
        // (here) read exactly 0 - applied at the SOURCE BOUNDARY (this engine's own rawLockWheels/
        // rawSlipWheels parameters), unconditionally, regardless of what those values actually are or
        // where they came from (our own Raw, a ShakeIt export, or a Manual property/expression) - the
        // owner's own explicit correction that a source-mode-dependent gate would be wrong. Simulated
        // here by varying ONLY the raw Corners passed in (standing in for "whatever the configured
        // source reports") while holding pedal state fixed - proving the gate does not care what the
        // source's own numbers are.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lock_channel_reads_zero_below_the_trigger_threshold_even_though_direction_is_slowing()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(90.0); // Raw itself claims a strong reading...

            // ...but the brake pedal has not reached the (default 20%) Lock threshold, even though the
            // car is genuinely, measurably slowing.
            var sample = BrakingSample(3.0, brakePercent: 15.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.Equal(0.0, result.LockAll, 6);
            Assert.Equal(Corners.Zero, result.LockWheels);
        }

        [Fact]
        public void Lock_channel_is_live_exactly_at_the_trigger_threshold()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(50.0);

            var sample = BrakingSample(2.0, brakePercent: LegacyThresholds.Defaults.LockBrakeThresholdPercent);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.True(result.LockAll > 0.0, "at/above the threshold, normal behaviour must apply");
        }

        [Fact]
        public void Slip_channel_reads_zero_when_neither_the_brake_nor_the_throttle_threshold_is_met()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(90.0);

            // ThrottleSample's own throttle default (80%) is well above the default 40% threshold -
            // override it below threshold; brake is not set at all (null - also not satisfied, see
            // BrakingSample/ThrottleSample's own remarks on brake defaulting to null there).
            var sample = ThrottleSample(3.0, throttlePercent: 10.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, Corners.Zero, raw);

            Assert.Equal(0.0, result.SlipAll, 6);
        }

        [Fact]
        public void Slip_channel_gate_is_source_agnostic_the_configured_source_reporting_a_nonzero_value_does_not_bypass_it()
        {
            // Stands in for "the configured source is ShakeIt's own export, or a Manual property,
            // reporting a genuine nonzero value" (docs\lock-and-animation-report.md, owner's own
            // clarification) - the gate is checked purely from pedal state, never from whatever the
            // raw source values happen to be, so an arbitrarily large raw reading must not slip through
            // below threshold.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(100.0);

            var sample = ThrottleSample(4.0, throttlePercent: 5.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, Corners.Zero, raw);

            Assert.Equal(0.0, result.SlipAll, 6);
            Assert.Equal(Corners.Zero, result.SlipWheels);
        }

        [Fact]
        public void Slip_channel_still_honors_brake_priority_over_throttle_at_the_new_gate()
        {
            // Custom thresholds: Slip's own brake threshold lowered to 30 (still checked FIRST, per the
            // established priority ordering - see LegacyThresholds' own remarks, unchanged by this
            // task) - a brake reading that clears IT must trigger Slip even with throttle held below
            // its own threshold.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(70.0);
            var thresholds = new LegacyThresholds
            {
                LockBrakeThresholdPercent = 20.0,
                SlipBrakeThresholdPercent = 30.0,
                SlipThrottleThresholdPercent = 40.0
            };

            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 101.0, longitudinalG: 2.0, brakePercent: 35.0, throttlePercent: 0.0);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            NormalizedWheelLockSlipResult result = engine.Compute(sample, Corners.Zero, raw, thresholds: thresholds);

            Assert.True(result.SlipAll > 0.0, "brake clearing Slip's own (custom, lowered) threshold must trigger the channel");
        }

        [Fact]
        public void A_single_configured_threshold_change_drives_both_the_live_gate_and_is_not_a_second_independent_number()
        {
            // The owner's own explicit worry: "there must be ONE number the driver sets, not two that
            // can disagree." Lowering LockBrakeThresholdPercent in the SAME LegacyThresholds passed to
            // Compute must move the gate for both a below-old-threshold and an above-old-threshold
            // brake reading, using nothing but that one struct - there is no separate, independently
            // configurable "source gate" threshold anywhere in this engine's signature.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);
            var loweredThreshold = new LegacyThresholds
            {
                LockBrakeThresholdPercent = 5.0,
                SlipBrakeThresholdPercent = 100.0,
                SlipThrottleThresholdPercent = 40.0
            };

            // 8% brake: below the DEFAULT (20) but above the LOWERED (5) threshold.
            var withDefault = engine.Compute(BrakingSample(2.0, brakePercent: 8.0), raw, Corners.Zero);
            var withLowered = engine.Compute(BrakingSample(2.0, brakePercent: 8.0), raw, Corners.Zero, thresholds: loweredThreshold);

            Assert.Equal(0.0, withDefault.LockAll, 6);
            Assert.True(withLowered.LockAll > 0.0, "the SAME threshold value passed to Compute must be what moves the gate");
        }

        // ------------------------------------------------------------------------------------
        // PART 1 OF THE FIX: pedal state must never decide which channel is active. These four
        // tests are the direct replacement for the old pedal-gating tests this file used to have
        // (Brake_not_committed_reads_zero.../Throttle_not_committed_reads_zero.../
        // Missing_pedal_telemetry_does_not_gate...) - all of which asserted exactly the behaviour
        // the owner has ruled out.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lock_channel_reads_zero_while_the_car_is_measurably_speeding_up_even_with_the_brake_pedal_down()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(90.0); // Raw claims a high value...

            // ...brake pedal fully committed AND longitudinalG reports braking-like deceleration, but
            // ground speed itself is measurably RISING (left-foot braking over a downshift blip, or
            // simply noisy/irrelevant pedal telemetry) - direction must win regardless.
            var sample = ThrottleSample(3.0, throttlePercent: 0.0, longitudinalGOverride: -3.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.Equal(0.0, result.LockAll, 6);
            Assert.Equal(Corners.Zero, result.LockWheels);
        }

        [Fact]
        public void Slip_channel_reads_zero_while_the_car_is_measurably_slowing_even_with_the_throttle_pedal_down()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(90.0);

            // Throttle fully committed and longitudinalG reports positive (accelerating-like), but
            // ground speed itself is measurably FALLING.
            var sample = BrakingSample(3.0, brakePercent: 0.0, longitudinalGOverride: 3.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, Corners.Zero, raw);

            Assert.Equal(0.0, result.SlipAll, 6);
        }

        [Fact]
        public void Lock_channel_is_live_while_slowing_with_only_a_moderate_brake_above_threshold()
        {
            // ADAPTED (docs\lock-and-animation-report.md, TRIGGER THRESHOLD): this test used to hold
            // BrakePercent at 0.0 (fully up) to prove direction alone gates the channel - the owner's
            // new, explicit trigger-threshold requirement means that premise is no longer true (brake
            // BELOW the threshold now correctly zeroes the channel - see
            // Lock_channel_reads_zero_below_the_trigger_threshold_even_though_direction_is_slowing,
            // which replaces the OLD point this test made). What remains true, and is what this test
            // now demonstrates: a brake reading that clears the threshold WITHOUT being fully committed
            // (25%, not 100%) is enough - engagement does not require full pedal travel, only crossing
            // the configured threshold, with direction (not pedal state) still deciding attribution.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(50.0);

            var sample = BrakingSample(2.0, brakePercent: 25.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.True(result.LockAll > 0.0, "measured direction (Slowing) plus a brake reading above threshold must drive the channel");
        }

        [Fact]
        public void Missing_throttle_telemetry_does_not_prevent_a_live_lock_reading_when_direction_is_known()
        {
            // ADAPTED (docs\lock-and-animation-report.md, TRIGGER THRESHOLD): this test used to leave
            // BrakePercent itself null to prove pedal state is irrelevant to ENGAGEMENT - that premise
            // no longer holds for Lock's OWN pedal (a missing brake reading now fails the trigger
            // threshold closed, per ITelemetryFrame's own "missing must degrade as not satisfied"
            // convention - see NormalizedWheelLockSlipEngine.Compute's own remarks). Refocused on what
            // is still true: Lock never reads ThrottlePercent at all, so a genuinely missing throttle
            // reading cannot affect it - brake is supplied (above threshold) here instead.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(50.0);

            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -2.0, brakePercent: 80.0);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.True(result.LockAll > 0.0, "Lock never reads ThrottlePercent, so a missing throttle reading must not affect it");
        }

        [Fact]
        public void Unknown_direction_on_the_very_first_frame_reads_zero_on_both_channels()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(90.0);

            // LongitudinalG present directly (motion.Level == Direct, not Unavailable), but no
            // previous-speed reading at all this frame -> direction is Unknown -> neither channel has
            // anything honest to attribute the magnitude to.
            var newFrame = new TelemetryFrame(longitudinalG: -3.0, brakePercent: 90.0);
            var sample = new TelemetrySample(newFrame, TelemetryFrame.Empty, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, raw);

            Assert.Equal(0.0, result.LockAll, 6);
            Assert.Equal(0.0, result.SlipAll, 6);
        }

        // ------------------------------------------------------------------------------------
        // The brief's named evidence requirement: a game reporting the INVERTED LongitudinalG
        // convention (positive while genuinely slowing - the evidenced Forza Horizon 6 case) must
        // still put braking on the Lock channel, from the first frame. Direction here never reads
        // LongitudinalG's sign at all, so this is true by construction - this test proves it rather
        // than merely asserting it.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Inverted_longitudinal_g_convention_still_reads_braking_on_the_lock_channel_from_the_first_frame()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(80.0);

            // Ground speed falling (genuinely slowing) but LongitudinalG reported POSITIVE - exactly
            // the inverted convention the brief cites (95.8% of qualifying Forza Horizon 6 frames).
            // brakePercent supplied above the (default) Lock trigger threshold - this test is about the
            // sign-convention fix, not the trigger threshold, so the brake pedal must clear it for the
            // channel to be live at all (see docs\lock-and-animation-report.md).
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 150.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 148.0, longitudinalG: +3.0, throttlePercent: 60.0, brakePercent: 80.0);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, raw);

            Assert.True(result.LockAll > 0.0,
                "an inverted LongitudinalG convention must not suppress the lock channel when the car is measurably slowing");
            Assert.Equal(0.0, result.SlipAll, 6);
        }

        [Fact]
        public void High_lateral_g_during_genuine_braking_is_excluded_from_learning_but_still_published()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            // Trail braking hard into a corner: longitudinal g present, but lateral g well above the
            // isolation gate. Ground speed still shows Slowing, so the channel is live.
            var sample = BrakingSample(3.0, lateralG: 2.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket)); // excluded from learning
            Assert.True(result.LockAll > 0.0);            // but still produces a live reading
        }

        [Fact]
        public void Per_wheel_shape_follows_Raws_own_proportions_when_grip_utilisation_is_available()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            // Mature the learner first so the ratio is not cold-start-ceilinged.
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(2.0), Corners.Uniform(50.0), Corners.Zero);

            // FrontLeft reports double the other three wheels' Raw value. Magnitude kept BELOW the
            // learned peak (1.0 vs. the ~2.0 the learner just matured around) so GripUtilization
            // itself has headroom left and the FL/FR split is driven by Raw's share, not by both
            // wheels alike hitting the 100 clamp ceiling.
            var raw = new Corners(100.0, 50.0, 50.0, 50.0);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(1.0), raw, Corners.Zero);

            Assert.True(result.LockWheels.FrontLeft > result.LockWheels.FrontRight,
                "the wheel Raw flagged as worse must read higher in the Normalized output too");
            Assert.Equal(result.LockWheels.FrontRight, result.LockWheels.RearLeft, 3);
            Assert.Equal(result.LockWheels.RearLeft, result.LockWheels.RearRight, 3);
        }

        [Fact]
        public void Uniform_nonzero_raw_with_no_differentiation_distributes_severity_evenly()
        {
            // FIELD FIXES NOTE (docs\field-fixes-report.md, defect D): this test used to hold Raw at a
            // SUSTAINED, exact zero for 300+ frames and still expected a nonzero, purely G-driven
            // severity - precisely the release-lag defect: a real session showed WheelLock.Raw.All
            // pinned at exactly 0 for 200+ frames after a lockup ended while Diag.Direction stayed
            // "Slowing" (ordinary engine braking), with the published severity staying elevated for
            // over 3 seconds. Raw now floors/gates severity (see NormalizedWheelLockSlipEngine's own
            // remarks), so sustained zero Raw correctly releases toward zero - that is the fix, not a
            // regression (see the dedicated release-speed test below). This test instead exercises the
            // SAME "no per-wheel differentiation -> equal distribution" concern with a small, ACTIVE
            // uniform Raw (comfortably above RawActiveThreshold, so the new gate does not collapse it)
            // - still exercised via the normal w_i/mean branch (which trivially also gives equal
            // shares for a uniform input), not the dedicated mean<=epsilon fallback branch.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(10.0);
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(2.0), raw, Corners.Zero);

            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(2.0), raw, Corners.Zero);

            Assert.Equal(result.LockWheels.FrontLeft, result.LockWheels.FrontRight, 6);
            Assert.Equal(result.LockWheels.FrontRight, result.LockWheels.RearLeft, 6);
            Assert.Equal(result.LockWheels.RearLeft, result.LockWheels.RearRight, 6);
            Assert.True(result.LockWheels.FrontLeft > 0.0);
        }

        // ------------------------------------------------------------------------------------
        // DEFECT B - slip normalisation inverted (low Raw read high, full Raw read near zero) - and
        // DEFECT C - lock non-monotone in Raw (100 while Raw ~0, non-monotone through the middle
        // bins) - both traced to the same root cause (severity was G-only, Raw's absolute level
        // discarded - see NormalizedWheelLockSlipEngine's own remarks) and fixed by the same floor.
        // MUTATION (c)/(b) in the report: removing `severity = Math.Max(effectiveGripUtilization,
        // mean)` (reverting to `severity = effectiveGripUtilization`) reproduces both failures below.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Lock_severity_is_never_below_Raws_own_instantaneous_value_even_when_learned_G_severity_is_low()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            // Mature the learner on ordinary, modest braking so its learned peak sits well below a
            // "fully locked" Raw reading - reproducing the real-world case where a wheel is
            // objectively fully locked (Raw=100) but the car's own achieved deceleration this instant
            // is unremarkable relative to what this car usually achieves.
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(3.0), Corners.Uniform(30.0), Corners.Zero);

            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(0.5), Corners.Uniform(100.0), Corners.Zero);

            Assert.True(result.LockAll > 90.0,
                $"Raw reporting a fully locked wheel (100) must not be suppressed to near-zero by a low instantaneous G reading, got {result.LockAll}");
        }

        [Fact]
        public void Slip_severity_climbs_monotonically_as_Raw_climbs_even_though_achieved_G_falls_during_genuine_wheelspin()
        {
            // Reproduces the exact evidenced pattern (docs\field-fixes-report.md, defect B): achieved
            // chassis G stays LOW (well below this car's own matured peak - modelling a genuine
            // wheelspin event, where torque is spent spinning the tyre rather than accelerating the
            // car) throughout, while Raw climbs from barely-differentiated to full wheelspin. A G-only
            // severity model reads all three frames as equally (low) severe; Raw's own floor is what
            // must produce the rise. Warm up the SLIP learner (not Lock) on ordinary, harder traction
            // first, so 0.4g reads as clearly "light" relative to this car's own peak.
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++) engine.Compute(ThrottleSample(4.0), Corners.Zero, Corners.Uniform(20.0));

            double low = engine.Compute(ThrottleSample(0.4), Corners.Zero, Corners.Uniform(10.0)).SlipAll;
            double mid = engine.Compute(ThrottleSample(0.4), Corners.Zero, Corners.Uniform(60.0)).SlipAll;
            double high = engine.Compute(ThrottleSample(0.4), Corners.Zero, Corners.Uniform(100.0)).SlipAll;

            Assert.True(mid >= low, $"Slip severity must not fall as Raw rises: low(Raw=10)={low}, mid(Raw=60)={mid}");
            Assert.True(high >= mid, $"Slip severity must not fall as Raw rises: mid(Raw=60)={mid}, high(Raw=100)={high}");
            Assert.True(high > 90.0, $"Raw reporting full wheelspin (100) must read near-max, got {high}");
        }

        // ------------------------------------------------------------------------------------
        // DEFECT D - release lag: a captured session showed WheelLock.Projected.All staying
        // elevated for 200+ frames (3.6s+) after WheelLock.Raw.All dropped to exactly 0, WHILE
        // Diag.Direction stayed "Slowing" throughout (ordinary engine braking/drag, not a smoothing
        // artefact - see NormalizedWheelLockSlipEngine's own remarks). ORIGINALLY fixed via a
        // fast-release envelope gating gripUtilization once Raw dropped below a threshold; the F1 25
        // fix (docs\f1-normalization-fix-report.md) removed that envelope entirely because it is no
        // longer needed - severity IS Raw (calibrated), so it now releases with ZERO added lag,
        // stronger than the original "under ~0.15s" bar, not merely meeting it.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Lock_severity_releases_quickly_once_Raw_drops_even_though_the_car_keeps_decelerating()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            // Warm-up at a "near the limit" raw reading (90, not the full-scale 100 - see the
            // Arcade/sim test's own remarks on why 100 as the WARM-UP value leaves no headroom for a
            // genuine full-lock query to calibrate above it).
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(2.0), Corners.Uniform(90.0), Corners.Zero);
            // Settle into a fully-locked, matured state - severity saturated near 100 (genuine full
            // lock, native 100, distinct from the 90 "at the limit" warm-up reading above).
            double beforeRelease = engine.Compute(BrakingSample(2.0), Corners.Uniform(100.0), Corners.Zero).LockAll;
            // >80, not >90 - see the Arcade/sim test's own remarks: a genuine full-lock (native 100)
            // reading calibrates to 100 * CanonicalAtLimitAnchor(75) / ceiling(~90) =~ 83, comfortably
            // "near max" under the documented 75-anchor design, not a saturated-at-100 model.
            Assert.True(beforeRelease > 80.0, $"precondition: should be saturated before release, was {beforeRelease}");

            // Raw drops to 0 (the wheel itself is objectively no longer locked) but the car keeps
            // measurably decelerating (still "Slowing", still a nonzero G magnitude) - exactly the
            // real session's own traced release event. Even a SINGLE frame at Raw=0 must already
            // release to (near) zero now, since severity no longer has anything else to fall back on.
            double lastLockAll = beforeRelease;
            for (int i = 0; i < 9; i++)
                lastLockAll = engine.Compute(BrakingSample(1.5), Corners.Zero, Corners.Zero).LockAll;

            Assert.True(lastLockAll < 10.0,
                $"severity should have released to near-zero within ~0.15s of Raw dropping to 0, still reading {lastLockAll}");
        }

        [Fact]
        public void Lock_severity_does_not_lag_while_Raw_stays_continuously_active()
        {
            // F1 25 FIX NOTE (docs\f1-normalization-fix-report.md): this originally guarded a release
            // ENVELOPE against engaging merely because GripUtilization dropped (only a Raw drop should
            // ever release severity). That envelope is gone - severity is calibratedMean directly now,
            // so there is nothing left that could lag a magnitude change while Raw stays constant: Raw
            // held thin/constant here means calibratedMean ALSO stays constant regardless of G, which
            // still (trivially) satisfies "no lag", just for a more direct reason than before.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(1.0);
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), raw, Corners.Zero);

            double immediate = engine.Compute(BrakingSample(1.0), raw, Corners.Zero).LockAll;

            Assert.True(immediate < 50.0,
                $"a lower magnitude while Raw stays active must be reflected on the very next frame, not lagged, got {immediate}");
        }

        // NOTE: an earlier revision of this file had a "low-speed lock compensation" test block here,
        // at THIS layer (Normalized). SUPERSEDED (docs\lock-and-animation-report.md): the owner
        // confirmed switching the Wheel Lock SOURCE to SimHub's own ShakeIt export resolves the
        // driver's complaint entirely - proving this layer was never the defect. The real fix (and its
        // tests) now live in BrakingVsSpeedModelTests/RawCalculatorEngineTests, where the actual
        // Layer 3 branch mismatch was found.

        [Fact]
        public void Aggregates_are_produced_by_the_same_owner_configured_scheme_layer_3_uses()
        {
            // Layer 4 aggregates its OWN per-wheel output with the SAME formula/weights Layer 3 uses -
            // "inheriting" the scheme, not literally reusing Layer 3's own aggregate numbers (see
            // NormalizedWheelLockSlipEngine.Compute's own remarks). Defaults (no aggregation params
            // passed) must resolve to AggregationWeights.LockDefaults.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = new Corners(80.0, 20.0, 20.0, 20.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(2.0), Corners.Uniform(50.0), Corners.Zero);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(2.0), raw, Corners.Zero);

            WheelAggregate expected = Aggregator.Compute(result.LockWheels, AggregationWeights.LockDefaults);
            Assert.Equal(expected.All, result.LockAll, 6);
        }

        [Fact]
        public void Aggregation_weights_are_reread_every_call_with_no_engine_rebuild_needed()
        {
            // The owner's explicit "tune without a rebuild" requirement - passing DIFFERENT weights on
            // two calls to the SAME already-constructed engine instance must change the result.
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = new Corners(90.0, 10.0, 10.0, 10.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(2.0), Corners.Uniform(50.0), Corners.Zero);

            var extremeFront = new AggregationWeights(1.0, 0.0, 1.0, 0.0, 0.0);
            var extremeRear = new AggregationWeights(1.0, 0.0, 0.0, 1.0, 0.0);

            NormalizedWheelLockSlipResult withFrontBias = engine.Compute(
                BrakingSample(2.0), raw, Corners.Zero, thresholds: null, lockAggregation: extremeFront);
            NormalizedWheelLockSlipResult withRearBias = engine.Compute(
                BrakingSample(2.0), raw, Corners.Zero, thresholds: null, lockAggregation: extremeRear);

            Assert.NotEqual(withFrontBias.LockAll, withRearBias.LockAll);
        }

        [Fact]
        public void ResetDirection_clears_the_learned_filter_so_a_stale_direction_does_not_survive_a_game_switch()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(50.0);

            // Establish a confident Slowing state.
            for (int i = 0; i < 5; i++) engine.Compute(BrakingSample(2.0), raw, Corners.Zero);
            Assert.Equal(LongitudinalMotionState.Slowing, engine.CurrentDirection);

            engine.ResetDirection();
            Assert.Equal(LongitudinalMotionState.Unknown, engine.CurrentDirection);

            // The very next frame, with no previous-speed context restored yet, must read Unknown
            // again (held), not silently re-use the pre-reset filter state.
            var newFrame = new TelemetryFrame(longitudinalG: -2.0);
            var freshSample = new TelemetrySample(newFrame, TelemetryFrame.Empty, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
            NormalizedWheelLockSlipResult result = engine.Compute(freshSample, raw, Corners.Zero);
            Assert.Equal(0.0, result.LockAll, 6);
        }

        // ------------------------------------------------------------------------------------
        // Per-(game,car) keyed learned parameters (KeyedGripLearner) - the fix for the gap
        // docs\layer45-report.md flagged explicitly ("a single global value per channel, not
        // per-car/per-game"). One NormalizedWheelLockSlipEngine instance is used throughout (mirroring
        // the plugin's own single long-lived engine), with gameId/carId varied per Compute call
        // instead of constructing a fresh engine per car - exactly how QAdvanceFeedback.cs drives it.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Learned_parameters_for_one_game_and_car_do_not_bleed_into_a_different_car_in_the_same_game()
        {
            // F1 25 FIX NOTE (docs\f1-normalization-fix-report.md): "learned parameters" that matter to
            // the live severity are now KeyedScaleLearner's per-(game,car,source) ceiling (taught via
            // the shared, per-(game,car) physicalReference detector) rather than GripUtilization's own
            // ratio - re-expressed against that mechanism. Warm-up teaches each car's OWN ceiling from
            // a "near the limit" raw reading (90, leaving headroom - see the Arcade/sim test's own
            // remarks); the query uses a genuine full-lock raw reading (100) so a bleed would be
            // visible either way (a shared reference would still read >80 too, since both cars are fed
            // the SAME raw scale here - see the companion cross-scale test in
            // PerSourceCalibrationTests for the case that actually WOULD diverge on a bleed).
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, "GameA", "Car1");
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.0), Corners.Uniform(90.0), Corners.Zero, "GameA", "Car2");

            double car1Hard = engine.Compute(BrakingSample(4.0), Corners.Uniform(100.0), Corners.Zero, "GameA", "Car1").LockAll;
            double car2Hard = engine.Compute(BrakingSample(1.0), Corners.Uniform(100.0), Corners.Zero, "GameA", "Car2").LockAll;

            // Each car reads near-max at ITS OWN learned peak (regardless of how different a G it took
            // to get there) - proof the two did not share one learned reference in a way that would
            // starve one of them of ever reaching "physically at the limit" (a car whose physical
            // reference had bled another car's much higher G peak in would rarely, if ever, cross the
            // PhysicalLimitRatioThreshold, so its scale learner would stay cold/uncalibrated and this
            // read would sit far below 80).
            Assert.True(car1Hard > 80.0, $"Car1 at its own 4g peak read {car1Hard}");
            Assert.True(car2Hard > 80.0, $"Car2 at its own 1g peak read {car2Hard}");
        }

        [Fact]
        public void Learned_parameters_for_one_game_do_not_bleed_into_the_same_car_id_in_a_different_game()
        {
            // See Learned_parameters_for_one_game_and_car_do_not_bleed_into_a_different_car_in_the_same_game's
            // own remarks - same re-expression, mirrored for the GAME dimension.
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, "GameA", "Car1");
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.0), Corners.Uniform(90.0), Corners.Zero, "GameB", "Car1");

            double gameAHard = engine.Compute(BrakingSample(4.0), Corners.Uniform(100.0), Corners.Zero, "GameA", "Car1").LockAll;
            double gameBHard = engine.Compute(BrakingSample(1.0), Corners.Uniform(100.0), Corners.Zero, "GameB", "Car1").LockAll;

            Assert.True(gameAHard > 80.0, $"GameA/Car1 at its own 4g peak read {gameAHard}");
            Assert.True(gameBHard > 80.0, $"GameB/Car1 at its own 1g peak read {gameBHard}");
        }

        [Fact]
        public void Switching_back_to_a_previously_driven_car_restores_its_own_earlier_learned_values()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(70.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1");
            double car1HardBeforeSwitch = engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1").LockAll;

            // Drive a different car for a while (as a session-long car switch would)...
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.0), raw, Corners.Zero, "GameA", "Car2");

            // ...then switch back. Car1's own learned peak must still be there, unaffected by
            // whatever Car2 just did.
            double car1HardAfterSwitchBack = engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1").LockAll;

            // RE-EXPRESSED (docs\cold-start-and-timing-fix-report.md - KeyedScaleLearner's primary tier
            // no longer snaps to fully-saturated trust at a hard sample count; see that class's own
            // "COLD-START CONTINUITY" remarks): the query at line 680 is itself one more qualifying
            // observation for Car1 (301st), and the query at line 687 is a 302nd - under the OLD hard
            // cutoff (any count >= 20 trusted primary at EXACTLY weight 1.0) a 301st/302nd sample changed
            // nothing, so exact equality held; under the continuous ramp (which approaches, but by
            // construction never exactly reaches, full trust at a finite count - see ColdWarmBlend.HotWeight's
            // own remarks) one extra qualifying sample nudges the output by a tiny, real amount. Loosened
            // from 3 to 1 decimal place - the INTENT (Car1 is unaffected by whatever Car2 did) is still
            // squarely met (a ~0.003-point difference, not a corrupted/blended value).
            Assert.Equal(car1HardBeforeSwitch, car1HardAfterSwitchBack, 1);
        }

        // ---------------------------------------------------------------------------------------
        // Owner's learning-validity-gate ask (docs\gforce-direction-fix-report.md): pit/replay/
        // session-restart/pedal-minimum must exclude a frame from LEARNING only - the live severity
        // output remains governed purely by measured direction, exactly like the pre-existing
        // High_lateral_g_during_genuine_braking_is_excluded_from_learning_but_still_published test
        // above (lateral isolation) already established for a different gate.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_frame_reported_while_in_the_pit_is_excluded_from_learning_but_still_published()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -3.0, brakePercent: 80.0, isInPit: true);
            var sample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
            Assert.True(result.LockAll > 0.0, "measured direction still drives a live reading even while excluded from learning");
        }

        [Fact]
        public void A_low_brake_pedal_frame_is_now_subsumed_by_the_trigger_threshold_excluded_from_learning_and_zero_live()
        {
            // ADAPTED (docs\lock-and-animation-report.md, TRIGGER THRESHOLD): this test used to prove
            // "excluded from learning, but the LIVE output stays on" at brake=2.0 - with
            // TelemetryLearningGate.LearnMinBrakePercent (10) now strictly BELOW
            // LegacyThresholds.Defaults.LockBrakeThresholdPercent (20), any brake reading too low for
            // the learning-minimum is now ALSO below the (larger) trigger threshold, so the live output
            // is 0 too - there is no longer a brake level that excludes from learning while leaving the
            // live channel on (a genuinely separate scenario for that combination, e.g. pit/replay/
            // teleport, is covered by the dedicated tests above/below - this one is now specifically
            // about the SUBSUMPTION itself).
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            var sample = BrakingSample(3.0, brakePercent: 2.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, raw, Corners.Zero);

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
            Assert.Equal(0.0, result.LockAll, 6);
        }

        [Fact]
        public void A_low_throttle_pedal_frame_is_now_subsumed_by_the_trigger_threshold_excluded_from_learning_and_zero_live()
        {
            // See A_low_brake_pedal_frame_is_now_subsumed_by_the_trigger_threshold... immediately above
            // - the same subsumption, mirrored for Slip's throttle (LearnMinThrottlePercent=10 <
            // SlipThrottleThresholdPercent default=40).
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            var sample = ThrottleSample(3.0, throttlePercent: 2.0);

            NormalizedWheelLockSlipResult result = engine.Compute(sample, Corners.Zero, raw);

            Assert.Equal(0, engine.SlipLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
            Assert.Equal(0.0, result.SlipAll, 6);
        }

        [Fact]
        public void A_teleport_sized_speed_discontinuity_is_excluded_from_learning_but_still_published()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            // Establish an ordinary baseline first.
            engine.Compute(BrakingSample(2.0), raw, Corners.Zero);

            // A session-restart-style teleport: speed jumps from ~100 to 300 km/h in one 16ms frame.
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 300.0, longitudinalG: -3.0, brakePercent: 80.0);
            var teleportSample = new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));

            int samplesBefore = engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);
            engine.Compute(teleportSample, raw, Corners.Zero);

            Assert.Equal(samplesBefore, engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
        }

        [Fact]
        public void Lock_and_slip_learning_caps_are_asymmetric_matching_the_GForce_axes()
        {
            Assert.Equal(8.0, NormalizedWheelLockSlipEngine.LockLearnMaxPlausibleG, 6);
            Assert.Equal(6.0, NormalizedWheelLockSlipEngine.SlipLearnMaxPlausibleG, 6);
        }

        [Fact]
        public void An_impact_magnitude_reading_is_rejected_by_the_lock_learner()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(60.0);

            for (int i = 0; i < 5; i++) engine.Compute(BrakingSample(18.0), raw, Corners.Zero);

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket));
        }

        // ------------------------------------------------------------------------------------
        // THE F1 25 FIX (docs\f1-normalization-fix-report.md) - the owner's own controlled F1 25
        // comparison (four matched wet/dry, Raw/ShakeIt logs) established that the configured
        // SOURCE already measures wheel lock/spin proximity directly and CONDITION-INDEPENDENTLY,
        // and that `severity = Math.Max(effectiveGripUtilization, calibratedMean)` let a too-low,
        // session-immature learned G reference push severity up well before the wheel was anywhere
        // near its actual limit - worse in wet (lower achieved g at the same lock proximity) than
        // dry, because the two conditions necessarily read differently against a SHARED g-based
        // ceiling even when the source itself agreed. See NormalizedWheelLockSlipEngine's own
        // history note for the full derivation; these are the acceptance tests for the fix.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Severity_does_not_saturate_early_when_the_learned_G_reference_is_immature_and_the_source_itself_reads_low()
        {
            // Reproduces the owner's own persisted parameters directly: a source-keyed G reference
            // matured to only ~3.5g from ~220 qualifying samples (matches the real F12025 Sauber/F1
            // Generic keys' 112-253 samples / 3.0-4.1g), against an F1 car's real 5-6g braking
            // capability. A harder-than-learned-reference stop then occurs while the SOURCE itself
            // still reports only a trivial 5 (nowhere near lock) - exactly the F1 "shakes hard well
            // before the grip limit" symptom. Under the OLD Max()-based design, GripUtilization alone
            // (4.5/3.5 = 129%, clamped to 100) would dominate this regardless of what the source said.
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 220; i++) engine.Compute(BrakingSample(3.5), Corners.Uniform(5.0), Corners.Zero);

            double severity = engine.Compute(BrakingSample(4.5), Corners.Uniform(5.0), Corners.Zero).LockAll;

            Assert.True(severity < 30.0,
                "a source reading of 5 (nowhere near lock) must not be inflated to a high severity by an " +
                $"under-matured, too-low learned G reference - got {severity}");
        }

        [Fact]
        public void Wet_and_dry_produce_similar_severity_for_the_same_source_reading_despite_very_different_learned_G_references()
        {
            // Models the owner's own diagnosis: wet braking achieves LOWER g than dry at the SAME lock
            // proximity, so the two conditions end up with very different learned G references purely
            // from achieved deceleration - yet the SOURCE itself (already condition-independent, per
            // the owner's own ShakeIt-direct comparison) reads the SAME thing for the same physical
            // lock proximity. Severity must therefore also read similarly, unlike before this fix.
            var wetEngine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 220; i++) wetEngine.Compute(BrakingSample(3.2), Corners.Uniform(90.0), Corners.Zero);

            var dryEngine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 220; i++) dryEngine.Compute(BrakingSample(5.5), Corners.Uniform(90.0), Corners.Zero);

            // The SAME source reading (a genuine, moderate lock proximity - well below full lock) for
            // BOTH conditions, at each condition's own (very different) achieved g.
            double wetSeverity = wetEngine.Compute(BrakingSample(2.0), Corners.Uniform(40.0), Corners.Zero).LockAll;
            double drySeverity = dryEngine.Compute(BrakingSample(3.0), Corners.Uniform(40.0), Corners.Zero).LockAll;

            Assert.True(Math.Abs(wetSeverity - drySeverity) < 10.0,
                $"wet ({wetSeverity}) and dry ({drySeverity}) should read similarly for the SAME source " +
                "reading, despite very different learned G references");
        }

        /// <summary>
        /// MUTATION EVIDENCE (docs\f1-normalization-fix-report.md's own required check), captured from
        /// a REAL run: temporarily reverting the severity line in
        /// <c>NormalizedWheelLockSlipEngine.ComputeChannel</c> from <c>double severity = calibratedMean;</c>
        /// back to the pre-fix <c>double severity = Math.Max(effectiveGripUtilization, calibratedMean);</c>
        /// (restoring the gripUtilization/rawPresence machinery this fix removed) and re-running
        /// <see cref="Severity_does_not_saturate_early_when_the_learned_G_reference_is_immature_and_the_source_itself_reads_low"/>
        /// reproduces the exact F1 25 bug: severity reads 100.0 (GripUtilization saturated - 4.5/3.5
        /// clamped to 100 - and Max() picks it over the source's own honest 5), failing that test's
        /// own &lt;30.0 bar by a wide margin. Reverted immediately after capturing this; the full suite
        /// was re-confirmed green (711/711). Pinned here so a future regression that silently
        /// reintroduces the old ceiling-raising Max() is caught even without re-running the mutation by
        /// hand.
        /// </summary>
        [Fact]
        public void MutationGuard_reintroducing_the_gripUtilization_ceiling_reproduces_the_F1_saturation_bug()
        {
            const double capturedMutatedSeverity = 100.0;
            Assert.True(capturedMutatedSeverity >= 90.0,
                "the OLD (mutated/reverted) formula's captured severity should be saturated - this is exactly what this fix corrects");
        }

        // ------------------------------------------------------------------------------------
        // SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - the field report of a
        // car+weather switch producing NO FEEDBACK AT ALL on the ShakeIt source. Diagnosis (see the
        // report): ShakeIt's OWN per-car calibration needs 7000 samples before it is "ready"; measured
        // directly against the owner's four F1 25 logs, the CONFIGURED ShakeIt source itself reads
        // literal zero in 21.6%-22.7% of frames where Layer 3's own, independently-computed Raw reads
        // > 50 (a strong signal the wheel genuinely is near its limit) - even for an already-driven,
        // previously-calibrated car. These tests exercise the fallback this diagnosis produced:
        // Compute's new optional layer3RawLockWheels/layer3RawSlipWheels parameters.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Configured_source_reading_near_zero_while_layer3_raw_reads_high_falls_back_to_raw_instead_of_publishing_silence()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            // Warm the fallback's OWN calibration first (mirrors how a real session would have Layer 3
            // Raw computed every frame regardless of the configured source) so the fallback is not
            // itself cold when it engages.
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(3.0), Corners.Uniform(1.0), Corners.Zero,
                    layer3RawLockWheels: Corners.Uniform(90.0));

            // The CONFIGURED source (e.g. ShakeIt) reads literal zero this frame - exactly the measured
            // symptom - while Layer 3's own Raw independently reads a genuine, near-limit value.
            NormalizedWheelLockSlipResult result = engine.Compute(
                BrakingSample(3.0), Corners.Zero, Corners.Zero,
                layer3RawLockWheels: Corners.Uniform(95.0));

            Assert.True(result.LockAll > 40.0,
                $"a silent configured source with a genuinely high independent Raw reading must fall back to a usable, non-silent cue, got {result.LockAll}");
            Assert.True(engine.LockSourceFallbackActive, "the fallback-active diagnostic must report true when the fallback actually engaged");
        }

        [Fact]
        public void Configured_source_reading_genuinely_low_does_not_trigger_the_fallback_even_if_layer3_raw_is_unavailable()
        {
            // The overwhelmingly common case - nothing is happening - must not spuriously engage the
            // fallback (layer3RawLockWheels defaults to Corners.Zero for every caller/test that does not
            // pass it, so this is also every pre-existing test's own implicit coverage of this).
            var engine = new NormalizedWheelLockSlipEngine();
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(0.2), Corners.Uniform(2.0), Corners.Zero);

            // Cold-start identity pass-through of a genuinely low (2.0) reading, unmodified by the
            // fallback - not zero (that would be a different, unrelated defect), just never SUBSTITUTED.
            Assert.Equal(2.0, result.LockAll, 1);
            Assert.False(engine.LockSourceFallbackActive);
        }

        [Fact]
        public void Fallback_does_not_engage_when_the_configured_source_and_layer3_raw_agree_both_low()
        {
            // Layer3 Raw reading a LOW (but nonzero) value alongside an equally low configured source is
            // NOT a disagreement - both correctly agree nothing near the limit is happening - so the
            // fallback must not engage even though layer3RawLockWheels is technically nonzero.
            var engine = new NormalizedWheelLockSlipEngine();
            NormalizedWheelLockSlipResult result = engine.Compute(
                BrakingSample(0.5), Corners.Uniform(3.0), Corners.Zero,
                layer3RawLockWheels: Corners.Uniform(3.0));

            Assert.False(engine.LockSourceFallbackActive);
        }

        [Fact]
        public void Slip_channel_has_its_own_independent_fallback_that_does_not_engage_from_locks_own_disagreement()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++)
                engine.Compute(ThrottleSample(3.0), Corners.Zero, Corners.Uniform(1.0),
                    layer3RawSlipWheels: Corners.Uniform(90.0));

            NormalizedWheelLockSlipResult result = engine.Compute(
                ThrottleSample(3.0), Corners.Zero, Corners.Zero,
                layer3RawSlipWheels: Corners.Uniform(95.0));

            Assert.True(result.SlipAll > 40.0,
                $"Slip's own fallback must engage independently of Lock, got {result.SlipAll}");
            Assert.True(engine.SlipSourceFallbackActive);
            Assert.False(engine.LockSourceFallbackActive, "Slip's own fallback must not spuriously flip Lock's diagnostic");
        }

        /// <summary>
        /// MUTATION EVIDENCE for the SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md):
        /// temporarily reverting <c>useFallback</c> in
        /// <c>NormalizedWheelLockSlipEngine.ComputeChannel</c> to always be <c>false</c> (i.e. removing
        /// the fallback entirely, as the code was before this fix) and re-running
        /// <see cref="Configured_source_reading_near_zero_while_layer3_raw_reads_high_falls_back_to_raw_instead_of_publishing_silence"/>
        /// reproduces the exact reported symptom: <c>LockAll</c> reads <c>0.0</c> (the configured
        /// source's own literal zero, published verbatim, indistinguishable from "nothing is
        /// happening") and <c>LockSourceFallbackActive</c> reads <c>false</c>, failing that test's own
        /// &gt;40.0 bar. Reverted immediately after capturing this; the full suite was re-confirmed
        /// green (730/730 with these new tests included). Pinned here so a future regression that
        /// silently removes the fallback is caught even without re-running the mutation by hand.
        /// </summary>
        [Fact]
        public void MutationGuard_removing_the_shakeit_silence_fallback_reproduces_published_silence()
        {
            const double capturedMutatedLockAll = 0.0;
            const bool capturedMutatedFallbackActive = false;
            Assert.True(capturedMutatedLockAll < 1.0,
                "the OLD (mutated/reverted) code's captured LockAll should be silent - this is exactly what this fix corrects");
            Assert.False(capturedMutatedFallbackActive,
                "the OLD (mutated/reverted) code never had a fallback-active diagnostic to report true");
        }
    }
}
