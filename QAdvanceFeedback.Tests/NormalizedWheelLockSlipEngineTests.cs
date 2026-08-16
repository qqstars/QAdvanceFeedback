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
        // two cars must read comparably for a comparable FRACTION of their own peak. This is the
        // test that fails under mutation (c) - replacing the learned reference with a fixed
        // constant reproduces the sibling project's exact saturation bug (see GripLearner's
        // remarks).
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Arcade_and_sim_magnitude_traces_both_span_a_useful_range_instead_of_one_saturating()
        {
            // FIELD FIXES NOTE (docs\field-fixes-report.md, defects B/D): this fixture used to hold
            // Raw at a constant 70.0 throughout, specifically so Raw's own absolute level could never
            // influence the result - the whole point being to isolate GripUtilization's calibration.
            // That assumption is what defect B disproved: the brief's own acceptance criteria now
            // require Raw's own level to act as a FLOOR on the published severity (so a wheel Layer 3
            // already measured as fully locked/spinning can never read near-zero - see
            // NormalizedWheelLockSlipEngine's own remarks), which a constant 70 floor would swamp
            // "light" braking's expected sub-50 reading. Lowered to RawActiveThreshold itself (1.0) -
            // still comfortably "active" (never triggers the release envelope's decay, so this test
            // still exercises GripUtilization's OWN calibration exactly as before) while contributing
            // a negligible floor. No assertion below was weakened - only this one fixture value.
            var raw = Corners.Uniform(1.0);

            var arcadeEngine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++) arcadeEngine.Compute(BrakingSample(4.0), raw, Corners.Zero);
            double arcadeLight = arcadeEngine.Compute(BrakingSample(1.0), raw, Corners.Zero).LockAll;
            double arcadeHard = arcadeEngine.Compute(BrakingSample(4.0), raw, Corners.Zero).LockAll;

            var simEngine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++) simEngine.Compute(BrakingSample(1.2), raw, Corners.Zero);
            double simLight = simEngine.Compute(BrakingSample(0.3), raw, Corners.Zero).LockAll;
            double simHard = simEngine.Compute(BrakingSample(1.2), raw, Corners.Zero).LockAll;

            Assert.True(arcadeHard > 85.0, $"arcade hard (at its own 4g peak) read {arcadeHard}, expected near-max");
            Assert.True(simHard > 85.0, $"sim hard (at its own 1.2g peak) read {simHard}, expected near-max");

            // The money assertion: a FIXED ~1g reference would read arcade's ordinary 1g braking as
            // ratio 1.0 -> ~100 (saturated) - exactly the sibling project's bug. Learned-and-relative
            // calibration must instead read it as roughly a quarter of THIS car's own peak.
            Assert.True(arcadeLight < 50.0,
                $"arcade light braking (1g, 25% of its own 4g peak) read {arcadeLight} - expected well below max; " +
                "a fixed reference would saturate this exactly like the sibling project's bug");
            Assert.True(simLight < 50.0, $"sim light braking read {simLight}, expected well below max");

            // Same fraction of each car's own peak must read comparably - proof of car-relative,
            // not absolute-g, calibration.
            Assert.True(Math.Abs(arcadeLight - simLight) < 25.0,
                $"arcade light ({arcadeLight}) and sim light ({simLight}) should read comparably for the same ~25% fraction of each car's own peak");
            Assert.True(Math.Abs(arcadeHard - simHard) < 15.0,
                $"arcade hard ({arcadeHard}) and sim hard ({simHard}) should both read near-max at each car's own peak");
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
        // threshold, BOTH Raw (Layer 3 - see LegacyWheelLockSlipEngineTests, Private\) AND Normalized
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

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty)); // excluded from learning
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
        // artefact - see NormalizedWheelLockSlipEngine's own remarks). Fixed via the fast-release
        // envelope gating gripUtilization once Raw itself drops below RawActiveThreshold.
        // ------------------------------------------------------------------------------------
        [Fact]
        public void Lock_severity_releases_quickly_once_Raw_drops_even_though_the_car_keeps_decelerating()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var activeRaw = Corners.Uniform(100.0);

            // Settle into a fully-locked, matured state - severity saturated near 100.
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(2.0), activeRaw, Corners.Zero);
            double beforeRelease = engine.Compute(BrakingSample(2.0), activeRaw, Corners.Zero).LockAll;
            Assert.True(beforeRelease > 90.0, $"precondition: should be saturated before release, was {beforeRelease}");

            // Raw drops to 0 (the wheel itself is objectively no longer locked) but the car keeps
            // measurably decelerating (still "Slowing", still a nonzero G magnitude) - exactly the
            // real session's own traced release event. 16ms/frame (~60fps, matching BrakingSample's
            // own fixed dt) - advance ~0.15s (about 9 frames) and require the severity to have
            // released, not merely started to.
            double lastLockAll = beforeRelease;
            for (int i = 0; i < 9; i++)
                lastLockAll = engine.Compute(BrakingSample(1.5), Corners.Zero, Corners.Zero).LockAll;

            Assert.True(lastLockAll < 10.0,
                $"severity should have released to near-zero within ~0.15s of Raw dropping to 0, still reading {lastLockAll}");
        }

        [Fact]
        public void Lock_severity_does_not_lag_while_Raw_stays_continuously_active()
        {
            // Guards against an overly-broad release mechanism: as long as Raw keeps indicating
            // engagement, a magnitude/severity change must still be reflected INSTANTLY (matching
            // every pre-existing calibration test's own expectation) - the release envelope must only
            // engage when Raw itself drops, never merely because gripUtilization drops. Raw held at a
            // small, merely-"active" level (not a large constant) so its own floor does not itself
            // mask the magnitude-driven drop this test is about (see the Arcade/sim test's own
            // remarks on the same consideration).
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
        // tests) now live in Private\QAdvanceFeedback.Tests\ against SimpleBrakingLockAlgorithm/
        // LegacyWheelLockSlipEngine, where the actual Layer 3 branch mismatch was found.

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
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(70.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1");
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.0), raw, Corners.Zero, "GameA", "Car2");

            double car1Hard = engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1").LockAll;
            double car2Hard = engine.Compute(BrakingSample(1.0), raw, Corners.Zero, "GameA", "Car2").LockAll;

            // Each car reads near-max at ITS OWN learned peak - proof the two did not share one
            // learned reference (a shared reference would have Car2's gentle 1g either barely
            // register against Car1's 4g peak, or Car1's 4g saturate against Car2's 1g peak).
            Assert.True(car1Hard > 85.0, $"Car1 at its own 4g peak read {car1Hard}");
            Assert.True(car2Hard > 85.0, $"Car2 at its own 1g peak read {car2Hard}");
        }

        [Fact]
        public void Learned_parameters_for_one_game_do_not_bleed_into_the_same_car_id_in_a_different_game()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var raw = Corners.Uniform(70.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1");
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.0), raw, Corners.Zero, "GameB", "Car1");

            double gameAHard = engine.Compute(BrakingSample(4.0), raw, Corners.Zero, "GameA", "Car1").LockAll;
            double gameBHard = engine.Compute(BrakingSample(1.0), raw, Corners.Zero, "GameB", "Car1").LockAll;

            Assert.True(gameAHard > 85.0, $"GameA/Car1 at its own 4g peak read {gameAHard}");
            Assert.True(gameBHard > 85.0, $"GameB/Car1 at its own 1g peak read {gameBHard}");
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

            Assert.Equal(car1HardBeforeSwitch, car1HardAfterSwitchBack, 3);
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

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty));
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

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty));
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

            Assert.Equal(0, engine.SlipLearners.Samples(string.Empty, string.Empty));
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

            int samplesBefore = engine.LockLearners.Samples(string.Empty, string.Empty);
            engine.Compute(teleportSample, raw, Corners.Zero);

            Assert.Equal(samplesBefore, engine.LockLearners.Samples(string.Empty, string.Empty));
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

            Assert.Equal(0, engine.LockLearners.Samples(string.Empty, string.Empty));
        }
    }
}
