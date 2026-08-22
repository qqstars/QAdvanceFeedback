using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Unit-level coverage for the DELTA-G COLLAPSE BAND MAPPING (docs\delta-g-band-mapping-report.md) -
    /// the owner's final specification for what "Normalized" means: 30 = consistently achieving 75% of
    /// the maximum effort available, 60 = 90%, 80 = the maximum effort available, 100 = fully
    /// locked/spun, delivering LESS effort than the maximum. See
    /// <see cref="NormalizedWheelLockSlipEngine"/>'s own DELTA-G COLLAPSE BAND MAPPING history note for
    /// the formula (u/gate/collapse/b/R/F) and why the branch discriminator is ΔG itself, not the
    /// configured source.
    /// <para/>
    /// v1068 CORRECTION (docs\v1068-four-range-report.md, "A REAL DEFECT in how Feature C was wired") -
    /// this suite originally pinned Lock's OWN use of this formula for its live severity. Lock's live
    /// severity became the calibrated SOURCE end to end (the four-range curve when its own anchors are
    /// valid, else 1.0.6.3's own single-anchor Rescale) - restoring exact parity with 1.0.6.3 (reaching
    /// 80 whenever the calibrated source is ~80, and 100 whenever it is ~100, unconditionally of whether
    /// G is actively collapsing). Every test below that exercised LOCK specifically was retargeted to
    /// SLIP at that time, since Slip was (incorrectly) left on this formula for its own published
    /// severity too.
    /// <para/>
    /// 1.0.6.0 SLIP-REGRESSION FIX (docs\release-1060-report.md, Part 1) - THIS mechanism is now
    /// DIAGNOSTIC ONLY for BOTH channels. The owner reported, after directly comparing against 1.0.6.3,
    /// that Slip "shaked too early, and the output is high... totally different with 1.0.6.3" -
    /// confirmed by diff: 1.0.6.3 has NO ΔG-collapse/band-mapping concept for Slip (or Lock) at all; its
    /// Slip severity is exactly `calibratedMean`/`calibratedRawFallback` (the same calibrated-source
    /// formula Lock's own correction above already restored). Slip's published severity now uses that
    /// SAME formula - see `NormalizedWheelLockSlipEngine.ComputeChannel`'s own severity-assignment
    /// remarks. `ComputeDeltaGCollapseSeverity` is still called, unconditionally, for both channels
    /// purely to keep `lastG` coherent and to populate the `Utilization`/`BranchConfidence` diagnostics -
    /// genuinely correct, standalone readouts ("is this car at its own physical limit", "is achieved
    /// deceleration currently falling") that NEITHER channel's published severity is decided by any
    /// more. The tests below that assert on `LockUtilization`/`LockBranchConfidence`/`SlipUtilization`/
    /// `SlipBranchConfidence` (the diagnostics) remain exactly true and are unchanged; the ones that used
    /// to assert the published `SlipAll`/`LockAll` equalled this formula's own rising/falling branch value
    /// have been updated to reflect the new, source-driven severity instead (see each test's own remarks).
    /// </summary>
    public class DeltaGCollapseBandMappingTests
    {
        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        /// <summary>Slip's own equivalent of <see cref="BrakingSample"/> - used by the tests below that
        /// were retargeted from Lock to Slip (v1068 correction, see this class's own remarks).</summary>
        private static ITelemetrySample ThrottleSample(double gMagnitude, double throttlePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 100.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 101.0, longitudinalG: gMagnitude, throttlePercent: throttlePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        // ------------------------------------------------------------------------------------
        // RISING BRANCH - band correspondence at STEADY STATE (ΔG=0, so b=0 and Normalized = R(u)
        // exactly) - the owner's own literal specification, verified at all three anchors, identically
        // for both channels.
        // ------------------------------------------------------------------------------------

        // DIAGNOSTIC-ONLY as of the 1.0.6.0 Slip-regression fix (see this class's own remarks) - the
        // `SlipUtilization` readout still matches the owner's band correspondence exactly (the formula
        // itself is unchanged); only the assertion on the PUBLISHED `SlipAll` was removed, since Slip's
        // severity no longer comes from this formula at all (it is now `calibratedMean`, matching
        // 1.0.6.3 - see Slip_severity_now_tracks_the_calibrated_source_not_achieved_G_matching_1063
        // below for that coverage).
        [Theory]
        [InlineData(0.75, 30.0)]
        [InlineData(0.90, 60.0)]
        [InlineData(1.00, 80.0)]
        public void Rising_branch_diagnostic_matches_the_owners_band_correspondence_at_steady_state(double uTarget, double expectedNormalized)
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(ThrottleSample(peak), Corners.Zero, Corners.Uniform(50.0));

            double targetG = uTarget * peak;
            // A quiet frame (below the trigger threshold) resets the collapse detector's own "previous g"
            // to null (see _slipLastG's own remarks) WITHOUT feeding a second qualifying observation at
            // targetG into the learner - avoiding a real confound: two identical qualifying observations
            // at a value BELOW the established peak start corroborating each other on the SECOND one
            // (LowerConfidenceForHits(2) is already nonzero), which would nudge the learned peak itself
            // and perturb u away from the intended, exact uTarget. With lastG null, the very next
            // qualifying frame's collapse reads 0 unconditionally (a fresh "run"), isolating R(u) with a
            // SINGLE qualifying observation at targetG (an uncorroborated first sighting - moves nothing).
            engine.Compute(ThrottleSample(peak, throttlePercent: 0.0), Corners.Zero, Corners.Uniform(50.0));
            engine.Compute(ThrottleSample(targetG), Corners.Zero, Corners.Uniform(50.0));

            Assert.Equal(uTarget, engine.SlipUtilization, 2);
            Assert.Equal(0.0, engine.SlipBranchConfidence, 6);
            _ = expectedNormalized; // kept as documentation of R(u) at each anchor - no longer read from SlipAll.
        }

        // RE-EXPRESSED AGAIN (1.0.6.0 Slip-regression fix, docs\release-1060-report.md Part 1 - see this
        // class's own remarks): Lock and Slip no longer diverge on this formula at all - BOTH channels'
        // published severity is now the calibrated SOURCE end to end (1.0.6.3 parity), and the ΔG-collapse
        // formula this class covers is diagnostic-only for both. Re-expressed to confirm exactly that:
        // two engines, each driven to the SAME u=0.9 via its own channel's own G trace, but Lock's OWN raw
        // source is deliberately far below its own learned ceiling (a light reading) while Slip's stays at
        // the level its own ceiling was warmed from - both channels now track their OWN source, so Lock
        // (light source) reads far below Slip (source at its own ceiling), for the SAME reason on both
        // sides, not because the two channels use different formulas.
        [Fact]
        public void Lock_and_Slip_both_track_the_calibrated_source_matching_1063()
        {
            var lockEngine = new NormalizedWheelLockSlipEngine();
            var slipEngine = new NormalizedWheelLockSlipEngine();
            const double peak = 3.0;
            for (int i = 0; i < 300; i++)
            {
                lockEngine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);
                slipEngine.Compute(ThrottleSample(peak), Corners.Zero, Corners.Uniform(50.0));
            }

            // A quiet frame (below the trigger threshold) resets each channel's own collapse detector
            // "previous g" to null WITHOUT feeding a second qualifying observation into the learner (see
            // Rising_branch_diagnostic_matches_the_owners_band_correspondence_at_steady_state's own
            // remarks) - isolates a SINGLE, uncorroborated first sighting at 0.9*peak (moves the learned
            // peak nothing), so u lands at EXACTLY 0.9 rather than drifting from a second, corroborating
            // hit. u itself is no longer what decides severity for either channel any more, but keeping
            // the same fixture keeps this test's own history intact.
            var lightLockRaw = Corners.Uniform(5.0); // far below Lock's own ~50 learned ceiling
            lockEngine.Compute(BrakingSample(peak, brakePercent: 0.0), lightLockRaw, Corners.Zero);
            double lockResult = lockEngine.Compute(BrakingSample(0.9 * peak), lightLockRaw, Corners.Zero).LockAll;

            // Slip's own raw source (Corners.Uniform(50.0)) sits AT its own learned ceiling throughout -
            // its calibrated-source severity should therefore land near the max-grip anchor (80), exactly
            // like Lock would if ITS raw were also at its own ceiling instead of deliberately light.
            slipEngine.Compute(ThrottleSample(peak, throttlePercent: 0.0), Corners.Zero, Corners.Uniform(50.0));
            double slipResult = slipEngine.Compute(ThrottleSample(0.9 * peak), Corners.Zero, Corners.Uniform(50.0)).SlipAll;

            Assert.True(slipResult >= 79.9, $"Slip's source is at its own ceiling, so its calibrated severity should read the max-grip anchor, got {slipResult}");
            // Lock: still tracks its OWN (light) source, reading far below Slip's - both channels are
            // source-driven now, so this divergence comes entirely from the DIFFERENT raw readings fed to
            // each engine in this fixture, not from a different formula.
            Assert.True(lockResult < slipResult - 10.0,
                $"Lock (light raw) should read well below Slip (raw at its own ceiling), both being source-driven now, got Lock={lockResult} Slip={slipResult}");
        }

        // ------------------------------------------------------------------------------------
        // BRANCH GATE - the falling branch never engages below u=0.80, no matter how sharply G is
        // collapsing (the owner's own "60-80 is the ideal range... 80-100 means it is getting worse"
        // definition - below 80% of the physical max, this is not "getting worse from the limit", it is
        // simply "not hard enough yet").
        // ------------------------------------------------------------------------------------

        [Fact]
        public void The_falling_branch_never_engages_below_the_080_utilization_gate_even_with_a_sharp_collapse()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            // A sharp, genuine collapse (peak -> 40% of peak in one frame) while u itself stays well
            // below the 0.80 gate.
            engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);
            engine.Compute(BrakingSample(0.4 * peak), Corners.Uniform(50.0), Corners.Zero);

            Assert.True(engine.LockUtilization < 0.80, $"precondition: u should be below the gate, was {engine.LockUtilization}");
            Assert.Equal(0.0, engine.LockBranchConfidence, 6);
        }

        [Fact]
        public void Collapse_reads_zero_while_G_is_rising_or_flat_even_near_the_physical_limit()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            // Flat (ΔG=0) at the limit.
            engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);
            Assert.Equal(0.0, engine.LockBranchConfidence, 6);

            // Rising further (a genuinely harder moment) at the limit.
            engine.Compute(BrakingSample(peak * 1.05), Corners.Uniform(50.0), Corners.Zero);
            Assert.Equal(0.0, engine.LockBranchConfidence, 6);
        }

        // ------------------------------------------------------------------------------------
        // NO FLOOR - Normalized can fall all the way from a saturated high value back through
        // 60-80/30-60/0-30 as utilization eases, mirroring the offline sweep
        // (docs\two-signal-band-mapping-report.md, Part 6) that the two PRIOR Math.Max-floored attempts
        // (docs\grip-utilization-band-report.md, docs\stable-denominator-report.md) could not satisfy.
        // ------------------------------------------------------------------------------------

        // DIAGNOSTIC-ONLY as of the 1.0.6.0 Slip-regression fix (see this class's own remarks). This used
        // to assert the PUBLISHED `SlipAll` fell through every band as G eased - no longer meaningful,
        // since Slip's raw source is held CONSTANT (Corners.Uniform(50.0)) throughout this fixture and
        // severity is now source-driven, so `SlipAll` itself stays flat near the source's own calibrated
        // level regardless of G. The underlying ΔG-collapse mechanism this test actually exercises still
        // has no floor anywhere - re-expressed against `SlipUtilization` (which the formula's rising
        // branch still reads off directly), the still-genuinely-useful diagnostic.
        [Fact]
        public void Utilization_falls_through_every_band_as_g_eases_no_floor_anywhere()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(ThrottleSample(peak), Corners.Zero, Corners.Uniform(50.0));

            engine.Compute(ThrottleSample(peak), Corners.Zero, Corners.Uniform(50.0));
            engine.Compute(ThrottleSample(peak), Corners.Zero, Corners.Uniform(50.0)); // steady, isolates b=0
            double stillAtLimit = engine.SlipUtilization;
            engine.Compute(ThrottleSample(peak * 0.85), Corners.Zero, Corners.Uniform(50.0));
            double sixtyToEighty = engine.SlipUtilization;
            engine.Compute(ThrottleSample(peak * 0.60), Corners.Zero, Corners.Uniform(50.0));
            double thirtyToSixty = engine.SlipUtilization;
            engine.Compute(ThrottleSample(peak * 0.20), Corners.Zero, Corners.Uniform(50.0));
            double zeroToThirty = engine.SlipUtilization;
            engine.Compute(ThrottleSample(peak * 0.01), Corners.Zero, Corners.Uniform(50.0));
            double offBrakes = engine.SlipUtilization;

            Assert.True(stillAtLimit >= 0.99, $"at the limit u should read ~1.0, got {stillAtLimit}");
            Assert.True(sixtyToEighty < stillAtLimit, $"easing off the limit must fall, not stick: {stillAtLimit} -> {sixtyToEighty}");
            Assert.True(thirtyToSixty < sixtyToEighty, $"must keep falling: {sixtyToEighty} -> {thirtyToSixty}");
            Assert.True(zeroToThirty < thirtyToSixty, $"must keep falling: {thirtyToSixty} -> {zeroToThirty}");
            Assert.True(offBrakes < zeroToThirty, $"must keep falling toward zero: {zeroToThirty} -> {offBrakes}");
            Assert.True(offBrakes < 0.15, $"should end up close to zero once well below the limit, got {offBrakes}");
        }

        // ------------------------------------------------------------------------------------
        // COLD START - the ceiling is GripLearner's own existing ColdStartCeilingRatio/MaturitySamples
        // contract, continuous (no step at any sample count), applied to u BEFORE R/gate/b - never over-
        // reports while cold.
        // ------------------------------------------------------------------------------------

        // 1.0.6.0 SLIP-REGRESSION FIX (see this class's own remarks): Slip's own cold-start behaviour for
        // its PUBLISHED severity is now KeyedScaleLearner's OWN documented identity fallback (raw,
        // unscaled, until a ceiling exists) - matching 1.0.6.3 exactly, exactly like Lock's own cold start
        // - not this u-ceiling any more. The u-ceiling itself (the diagnostic `SlipUtilization`) is
        // unchanged and still verified below.
        [Fact]
        public void A_brand_new_key_ceilings_u_continuously_never_over_reporting_while_cold()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            // First-ever frame for this (game,car): a large g relative to the seed seed peak (1.0g) -
            // GripLearner.ColdStartCeilingRatio(0.75) caps u at ~0.75 (not the true raw ratio, which would
            // be far larger) - still exactly true as a diagnostic. The PUBLISHED severity, with no
            // calibration ceiling learned yet either, is KeyedScaleLearner.Rescale's own cold identity
            // fallback - the raw source (50.0) passed straight through, matching 1.0.6.3.
            NormalizedWheelLockSlipResult result = engine.Compute(ThrottleSample(3.0), Corners.Zero, Corners.Uniform(50.0));

            Assert.True(engine.SlipUtilization <= 0.76, $"a cold key must ceiling u near 0.75, got {engine.SlipUtilization}");
            Assert.Equal(50.0, result.SlipAll, 1);
        }

        [Fact]
        public void The_cold_start_ceiling_relaxes_continuously_with_no_step_at_any_sample_count()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            double previousU = -1.0;
            double maxJump = 0.0;

            for (int i = 0; i < 250; i++)
            {
                engine.Compute(BrakingSample(3.0), Corners.Uniform(50.0), Corners.Zero);
                if (previousU >= 0.0) maxJump = Math.Max(maxJump, Math.Abs(engine.LockUtilization - previousU));
                previousU = engine.LockUtilization;
            }

            Assert.True(maxJump < 0.05, $"u must never step as confidence matures sample-by-sample, max jump was {maxJump}");
        }

        // ------------------------------------------------------------------------------------
        // DGSCALE COLD FALLBACK - fewer than 5 ever-qualifying samples for this (game,car) key falls back
        // to a fixed 0.15g collapse scale rather than an undefined/zero one.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_genuinely_fresh_key_uses_the_fixed_015g_collapse_scale_not_an_undefined_one()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            // Frame 1: establishes a first reading (1 qualifying sample - well under the 5-sample bar).
            engine.Compute(BrakingSample(3.0), Corners.Uniform(50.0), Corners.Zero);
            // Frame 2: a small drop (0.1g) - if the scale were genuinely near-zero/undefined, even a tiny
            // drop would saturate collapse to 1.0; with the 0.15g fallback, a 0.1g drop should NOT fully
            // saturate collapse (0.1/0.15 = 0.667, not 1.0).
            engine.Compute(BrakingSample(2.9), Corners.Uniform(50.0), Corners.Zero);

            // b itself may still be 0 here if u has not reached the 0.80 gate - the assertion below checks
            // the COLD FALLBACK indirectly via a case where u DOES clear the gate.
            var freshEngine = new NormalizedWheelLockSlipEngine();
            // Warm ONLY 2 qualifying samples (well under CollapseDGScaleMinSamples=5) at the peak, so u
            // clears the gate on the very next frame, then apply a 0.1g drop.
            freshEngine.Compute(BrakingSample(3.0), Corners.Uniform(50.0), Corners.Zero);
            freshEngine.Compute(BrakingSample(3.0), Corners.Uniform(50.0), Corners.Zero);
            freshEngine.Compute(BrakingSample(2.9), Corners.Uniform(50.0), Corners.Zero);

            // Not fully saturated (would be 1.0/100-collapse if the scale were ~0) - the fixed 0.15g
            // fallback keeps a small 0.1g drop from reading as a full-blown collapse.
            Assert.True(freshEngine.LockBranchConfidence < 0.85,
                $"a small (0.1g) drop against the 0.15g cold fallback scale must not read as a near-total collapse, got b={freshEngine.LockBranchConfidence}");
        }

        // ------------------------------------------------------------------------------------
        // EDGE CASES - no hard sample gate, continuous, standstill/Unknown-direction/no-G-channel all
        // degrade gracefully (reusing the pre-existing degradation ladder - see
        // NormalizedWheelLockSlipEngineTests for the dedicated tests of each; these two are new,
        // DELTA-G-COLLAPSE-specific edge cases).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_quiet_gap_between_two_engaged_runs_does_not_carry_a_stale_G_into_the_next_runs_collapse_check()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            // Channel goes quiet (below trigger threshold) for a while.
            for (int i = 0; i < 10; i++) engine.Compute(BrakingSample(peak, brakePercent: 0.0), Corners.Uniform(50.0), Corners.Zero);

            // Re-engages at a MUCH lower g than the last quiet frame's own g - if the stale g leaked
            // through, this would read as a huge collapse; it must not (b=0, since there is no genuine
            // prior QUALIFYING frame in this new run to diff against).
            engine.Compute(BrakingSample(peak * 0.5), Corners.Uniform(50.0), Corners.Zero);

            Assert.Equal(0.0, engine.LockBranchConfidence, 6);
        }

        [Fact]
        public void A_car_switch_resets_the_collapse_detectors_previous_G_for_both_channels()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero, "GameA", "Car1");

            engine.ResetDirection();

            // A different car's first frame at a much lower g - must not be compared against Car1's own
            // last g (which would otherwise read as a large, spurious collapse).
            engine.Compute(BrakingSample(1.0), Corners.Uniform(50.0), Corners.Zero, "GameA", "Car2");

            Assert.Equal(0.0, engine.LockBranchConfidence, 6);
        }

        // ------------------------------------------------------------------------------------
        // PER-WHEEL DISCRIMINATION - G sets the car-level LEVEL, per-wheel SOURCE values still
        // distribute it: a single locking wheel must still show up distinctly on its own channel, even
        // when the car-level number is entirely G-derived.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_single_locking_wheel_still_reads_distinctly_higher_than_the_other_three_at_high_severity()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            const double peak = 4.0;
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(peak), Corners.Uniform(50.0), Corners.Zero);

            // Steady state at the physical limit (car-level severity ~80, per the rising-branch tests
            // above) - FrontLeft's own source reads far higher than the other three.
            engine.Compute(BrakingSample(peak), new Corners(100.0, 40.0, 40.0, 40.0), Corners.Zero);
            NormalizedWheelLockSlipResult result = engine.Compute(BrakingSample(peak), new Corners(100.0, 40.0, 40.0, 40.0), Corners.Zero);

            Assert.True(result.LockWheels.FrontLeft > result.LockWheels.FrontRight,
                $"the wheel the source flags as worse must still read higher, even though the car-level level is G-derived: FL={result.LockWheels.FrontLeft} FR={result.LockWheels.FrontRight}");
            Assert.Equal(result.LockWheels.FrontRight, result.LockWheels.RearLeft, 3);
            Assert.Equal(result.LockWheels.RearLeft, result.LockWheels.RearRight, 3);
            // The car-level aggregate itself should still be near the ~80 max-grip anchor - the per-wheel
            // distribution does not distort the level, only how it is shared across the four wheels.
            Assert.True(result.LockAll >= 70.0, $"the car-level aggregate should stay near the max-grip anchor, got {result.LockAll}");
        }
    }
}
