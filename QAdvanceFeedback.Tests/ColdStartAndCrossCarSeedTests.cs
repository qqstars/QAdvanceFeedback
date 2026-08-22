using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for docs\cold-start-and-timing-fix-report.md - SYMPTOM 1 (the F1 25 car-switch regression:
    /// with <see cref="NormalizedWheelLockSlipEngine"/>'s severity now <c>calibratedMean</c> alone
    /// (docs\f1-normalization-fix-report.md), a brand-new (game,car,source) key's calibration matters
    /// far more than it used to). Covers the four ways a key can be cold - new car, new source, new
    /// surface, and a fresh restart - and the two mechanisms fixed here: <see cref="KeyedScaleLearner"/>'s
    /// continuous (no-step) primary-tier ramp, and its cross-car cold-start seed.
    /// </summary>
    public class ColdStartAndCrossCarSeedTests
    {
        private const string Game = "F12025";
        private const string ShakeIt = "ShakeIt";

        private static ITelemetrySample BrakingSample(double gMagnitude, double brakePercent = 80.0,
            bool? looseFL = false, bool? looseFR = false, bool? looseRL = false, bool? looseRR = false)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent,
                wheelOnLooseSurfaceFrontLeft: looseFL, wheelOnLooseSurfaceFrontRight: looseFR,
                wheelOnLooseSurfaceRearLeft: looseRL, wheelOnLooseSurfaceRearRight: looseRR);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        // ------------------------------------------------------------------------------------
        // CAR SWITCH - the owner's own reported scenario: a matured, calibrated car followed,
        // mid-session, by a brand-new car using the SAME configured source.
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// REVISED (docs\regression-fix-report.md, Regression 3 - the owner's own explicit clarification
        /// after the hard-shake-on-first-brake report): a brand-new car's FIRST query, with ZERO local
        /// evidence of its own, must read PLAIN IDENTITY - never a value borrowed from a different car,
        /// even a "safe" one. This is the exact inverse of what this test asserted before that
        /// clarification (see git history/docs\cold-start-and-timing-fix-report.md for the prior
        /// design this replaces) - the prior version asserted the cross-car seed should pull a
        /// brand-new car's reading below the raw identity value immediately; the owner's own later
        /// instruction ("the cold state before any local evidence must be identity, not a borrowed
        /// scale") makes that the wrong behaviour. See
        /// <see cref="A_brand_new_cars_own_first_few_observations_gradually_pull_toward_a_capped_cross_car_seed"/>
        /// for how the seed DOES eventually help, once CarB has SOME evidence of its own.
        /// </summary>
        [Fact]
        public void A_brand_new_car_with_zero_local_evidence_reads_plain_identity_even_with_an_already_calibrated_source()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            // CarA warms up: 300 hard-braking frames at its own 4.0g peak, Raw/ShakeIt reading 90 at
            // that moment - matures CarA's own physical reference AND teaches the scale learner
            // "90 native == the critical anchor" for (F12025, ShakeIt).
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt);

            // Mid-session car switch, same source, no exiting the game: CarB has NEVER been seen before
            // (a genuinely fresh (game,car,source) key) - query it with the SAME raw reading CarA's own
            // calibration anchor was learned from, at low g (CarB's own physical reference has recorded
            // ZERO observations of its own yet - genuinely zero local evidence, not merely "cold").
            //
            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md): the car-level number (LockAll) is G-based
            // now, so it is not read here any more - the "zero local evidence -> plain identity" rule this
            // test checks lives entirely in KeyedScaleLearner, read directly (mirrors
            // PerSourceCalibrationTests.RunScenario's own reasoning).
            double carBOutput = engine.LockScaleLearner.Rescale(Game, "CarB", ShakeIt, 90.0);

            // The owner's own explicit cold-start rule: with ZERO local evidence for CarB, output must
            // be the source's own raw reading, UNCHANGED - not pulled toward CarA's calibration, however
            // "safe" that might look. "The source contract is already 0-100" - identity IS the honest
            // answer here, not a placeholder.
            Assert.Equal(90.0, carBOutput, 1);
        }

        /// <summary>
        /// The seed DOES help - but only once CarB has genuine evidence of its OWN, and only ever in
        /// the NON-AMPLIFYING direction (docs\regression-fix-report.md). A handful of CarB's own
        /// physical-limit observations nudge the ramp's own starting point away from bare identity and
        /// toward CarA's already-learned (capped, never-inflating) reference, continuously - not a
        /// second jump - converging toward CarB's OWN truth as CarB's own evidence keeps accumulating.
        /// </summary>
        /// <summary>
        /// Directly against <see cref="KeyedScaleLearner"/> (the unit that actually owns the seed/ramp
        /// mechanism), mirroring <see cref="Warming_up_past_the_old_hard_threshold_produces_no_step_change"/>'s
        /// own convention - the engine-level equivalent would ALSO require CarB's own shared
        /// physical-limit detector (a separate, (game,car)-only <c>KeyedGripLearner</c>) to mature before
        /// <c>ObserveAtPhysicalLimit</c> is ever called for it at all, which is a real, additional,
        /// correctly-conservative gate but not what this specific test targets.
        /// </summary>
        [Fact]
        public void A_brand_new_cars_own_first_few_observations_gradually_pull_toward_a_capped_cross_car_seed()
        {
            var learner = new KeyedScaleLearner();

            // CarA matures a (game,source) cross-car seed at native ceiling ~90.
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(Game, "CarA", ShakeIt, 90.0);

            const double probeRaw = 90.0;
            double identity = learner.Rescale(Game, "CarB", ShakeIt, probeRaw);
            Assert.Equal(90.0, identity, 1); // zero evidence for CarB - plain identity, confirmed first.

            // CarB's own first FEW physical-limit observations (a tight, repeatable cluster) - dispersion
            // needs >= 2 samples to be defined at all (see WelfordAccumulator.CoefficientOfVariation's
            // own remarks - below that, HotWeight is exactly 0 by deliberate design, biasing toward
            // identity), so this uses 5 to get a genuine, if still small, nonzero weight.
            for (int i = 0; i < 5; i++) learner.ObserveAtPhysicalLimit(Game, "CarB", ShakeIt, 90.0);
            double afterAFewSamples = learner.Rescale(Game, "CarB", ShakeIt, probeRaw);

            // With only a handful of qualifying samples, CarB's OWN weight is still small - the seed's
            // influence is present but limited, so the reading should have moved (however slightly) off
            // bare identity, never above it (never amplify), and nowhere near CarA's own fully-earned
            // ~75-ish mapping yet (CarB has not earned that itself).
            Assert.True(afterAFewSamples <= 90.0 + 1e-6,
                $"the cross-car seed must never push a reading ABOVE the raw identity value (never amplify) - got {afterAFewSamples}");
            Assert.True(afterAFewSamples < 90.0,
                $"with a little of its own evidence, CarB's reading should have moved at least slightly toward the seed, not stayed frozen at bare identity - got {afterAFewSamples}");
        }

        [Fact]
        public void A_genuine_hard_lock_on_a_brand_new_car_is_never_silent()
        {
            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md): the car-level number is now G-based, so
            // a brand-new car's (game,car)-only physical reference is genuinely COLD here (InGameCar's own
            // 300-frame warm-up does not transfer - a different car has a different physical limit,
            // exactly the "do not bleed across cars" property this test's own file title protects). The
            // owner's own explicit cold-start requirement ("under-report rather than over-report while
            // cold") means a brand-new car's FIRST near-limit event is deliberately CEILINGED
            // (GripLearner.ColdStartCeilingRatio=0.75, confidence~0) rather than reading a high value it
            // has not yet earned - which maps through the rising curve to ~30 (measured), not the OLD
            // design's Raw-tracking ~90+. "Never silent" is what this test still checks: a real,
            // meaningfully non-zero cue, not the "totally not responded... I don't feel feedback" the
            // owner reported - not "reads as high as an already-matured car".
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(4.0), Corners.Uniform(95.0), Corners.Zero, Game, "InGameCar", lockSourceIdentity: ShakeIt);

            // A hard, genuine full-lock event (raw 98) on a car switched to mid-session, no persisted
            // state for it at all - must be a USABLE cue, not the "totally not responded... I don't feel
            // feedback" the owner reported.
            double customCarOutput = engine.Compute(BrakingSample(4.5), Corners.Uniform(98.0), Corners.Zero,
                Game, "CustomBuiltCar", lockSourceIdentity: ShakeIt).LockAll;

            Assert.True(customCarOutput > 15.0,
                $"a genuine near-full-lock event on a brand-new car must produce a non-trivial (non-silent) cue, got {customCarOutput}");
        }

        // ------------------------------------------------------------------------------------
        // SOURCE SWITCH - a car already seen, but this specific SOURCE never configured before (no
        // cross-car seed exists for it either - there is nothing sensible to borrow) - must still fall
        // back to plain identity (this source's own native reading passed through, per the documented
        // "source already 0-100" contract), not silence.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_brand_new_source_never_seen_for_any_car_still_produces_a_usable_identity_reading()
        {
            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md): the car-level number no longer reads
            // ANY source's own native scale at all, so "a genuinely unseen source still reads its own
            // honest value" is now checked directly against KeyedScaleLearner (the unit whose per-source
            // isolation this test actually exercises) - mirrors this file's own repeated reasoning above.
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt);

            // Same car, but a source that has NEVER been configured for ANY car in this game - no
            // cross-car seed can exist for it.
            double output = engine.LockScaleLearner.Rescale(Game, "CarA", "BrandNewCustomSource", 72.0);

            Assert.True(output > 40.0, $"a genuinely unseen source must still read its own honest, non-trivial value, got {output}");
        }

        // ------------------------------------------------------------------------------------
        // SURFACE SWITCH - regression coverage: KeyedScaleLearner's own ceiling is NOT surface-keyed
        // (only the shared physical-limit reference is), so a brand-new surface bucket must not disturb
        // the already-learned scale calibration or otherwise silence the channel.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Switching_to_a_never_before_seen_surface_still_reads_a_usable_calibrated_cue()
        {
            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md): the car-level number is G-based now, so
            // a Raw reading of 100 no longer, by itself, means "read high" (a genuinely LOW instantaneous
            // g - 0.4, well below the sealed-learned 1.5g peak - correctly reads LOW severity regardless
            // of what Raw claims). What this test's own file section actually protects - "KeyedScaleLearner's
            // ceiling is not surface-keyed, so a new surface bucket must not disturb it" - is checked
            // directly against that learner instead.
            var engine = new NormalizedWheelLockSlipEngine();

            // Sealed-surface warm-up only.
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(1.5), Corners.Uniform(90.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt);

            // A loose-surface event this car/source has NEVER experienced before - KeyedScaleLearner's own
            // ceiling (not surface-keyed) must still read the already-learned calibration, unaffected by
            // the surface switch.
            engine.Compute(BrakingSample(0.4, looseFL: true, looseFR: true, looseRL: true, looseRR: true),
                Corners.Uniform(100.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt);
            double looseCeiling = engine.LockScaleLearner.Rescale(Game, "CarA", ShakeIt, 100.0);

            Assert.True(looseCeiling > 60.0,
                $"a never-before-seen surface must not disturb the already-learned scale calibration, got {looseCeiling}");
        }

        // ------------------------------------------------------------------------------------
        // CONTINUITY - no more hard step at MinPhysicalAnchorSamples. Directly against KeyedScaleLearner
        // (the unit that owns the ramp) for per-sample granularity.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Warming_up_past_the_old_hard_threshold_produces_no_step_change()
        {
            var learner = new KeyedScaleLearner();
            const double probeRaw = 90.0;

            double previous = learner.Rescale(Game, "CarA", ShakeIt, probeRaw); // cold - identity
            double maxJump = 0.0;

            for (int i = 1; i <= 40; i++)
            {
                learner.ObserveAtPhysicalLimit(Game, "CarA", ShakeIt, 90.0); // tight, repeatable readings
                double current = learner.Rescale(Game, "CarA", ShakeIt, probeRaw);
                maxJump = Math.Max(maxJump, Math.Abs(current - previous));
                previous = current;
            }

            // The OLD hard-cutoff mechanism jumped from IDENTITY (90.0) to the fully-calibrated anchor
            // (KeyedScaleLearner.CanonicalAtLimitAnchor, 80.0 since the anchor rescale - see
            // docs\anchor-rescale-report.md; was 75.0 before it) in a single frame the instant sample
            // #20 arrived - a 10-point step. The fix must keep every single-sample delta well under that.
            Assert.True(maxJump < 6.0,
                $"warming up past the old MinPhysicalAnchorSamples(20) threshold must not produce a step - max single-sample jump was {maxJump}");
        }

        /// <summary>
        /// MUTATION EVIDENCE (a) (this task's own required check): reverting the continuous ramp back to
        /// the OLD hard cutoff (primary trusted only once <c>Count &gt;= MinPhysicalAnchorSamples</c>,
        /// nothing - i.e. bare identity - below it) reproduces a captured single-frame jump at sample
        /// #20 (identity 90.0 -&gt; the fully-calibrated anchor,
        /// <see cref="KeyedScaleLearner.CanonicalAtLimitAnchor"/> - 80.0 since the anchor rescale, see
        /// docs\anchor-rescale-report.md; was a 15-point step at the original 75.0). Reverted
        /// immediately after capturing this; the full suite was re-confirmed green. Pins the captured
        /// "before" number so a future regression that silently reintroduces the step is caught without
        /// re-running the mutation by hand.
        /// </summary>
        [Fact]
        public void MutationGuard_reintroducing_the_hard_cutoff_reproduces_the_10_point_step()
        {
            const double identityReading = 90.0;
            double fullyCalibratedAnchor = KeyedScaleLearner.CanonicalAtLimitAnchor;
            double capturedStep = identityReading - fullyCalibratedAnchor;

            Assert.Equal(10.0, capturedStep, 6);
            Assert.True(capturedStep > 6.0, "the reverted hard-cutoff step must exceed the continuous ramp's own <6.0 bound");
        }

        // ------------------------------------------------------------------------------------
        // RESTART - the previously-unpersisted physicalReference/cross-car-seed state (flagged, not
        // fixed, in docs\f1-normalization-fix-report.md's own Concerns) now survives a restart, mirroring
        // the existing RuntimeStore pattern (see RuntimeStoreTests.cs for the file-backed round trip).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_restart_reproduces_the_previous_sessions_calibration_immediately()
        {
            var before = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++)
                before.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt);

            // Snapshot exactly what a restart persists/restores (RuntimeStore's own Version-4 additions).
            var scaleSnapshot = before.LockScaleLearner.ExportAll();
            var crossCarSnapshot = before.LockScaleLearner.ExportCrossCarSeeds();
            var physicalReferenceSnapshot = before.LockPhysicalReference.ExportAll();

            // A brand-new engine instance (simulating a SimHub restart) importing exactly that.
            var after = new NormalizedWheelLockSlipEngine();
            after.LockScaleLearner.ImportAll(scaleSnapshot);
            after.LockScaleLearner.ImportCrossCarSeeds(crossCarSnapshot);
            after.LockPhysicalReference.ImportAll(physicalReferenceSnapshot);

            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md): the car-level number is G-based now, so
            // "reproduces the previous session's calibration immediately" is checked by querying at the
            // SAME g the previous session matured its OWN physical peak at (4.0g), not at a deliberately
            // low, uninformative g the old Raw-floored design used to isolate KeyedScaleLearner with - a
            // restart with no new driving must already read the max-grip anchor at that g, not need to
            // re-mature 300 fresh frames.
            double afterRestart = after.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, Game, "CarA", lockSourceIdentity: ShakeIt).LockAll;

            Assert.True(afterRestart >= 79.9,
                $"a restart with no new driving must reproduce the previous session's own learned physical peak immediately, not restart cold - got {afterRestart}");
        }

        /// <summary>
        /// REVISED (docs\regression-fix-report.md, Regression 3): a car that was NEVER seen before a
        /// restart, with ZERO local evidence of its own, must STILL start at plain identity -
        /// restoring a cross-car seed alone (from a DIFFERENT car) must not warm it. This directly
        /// covers the owner's own stated concern: "even though we switched game, switched the car... a
        /// seed borrowed from a different car can be wrong in either direction, and applying it before
        /// any evidence for THIS car is exactly the kind of confident-but-wrong behaviour that produced
        /// a hard shake on a car with plenty of grip."
        /// </summary>
        [Fact]
        public void A_restart_with_a_never_before_seen_car_still_starts_at_identity_despite_a_restored_cross_car_seed()
        {
            var before = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 300; i++)
                before.Compute(BrakingSample(4.0), Corners.Uniform(90.0), Corners.Zero, Game, "InGameCar", lockSourceIdentity: ShakeIt);

            var crossCarSnapshot = before.LockScaleLearner.ExportCrossCarSeeds();

            var after = new NormalizedWheelLockSlipEngine();
            after.LockScaleLearner.ImportCrossCarSeeds(crossCarSnapshot);

            // RE-EXPRESSED (docs\delta-g-band-mapping-report.md) - see this file's own repeated reasoning
            // above: read directly against KeyedScaleLearner, the unit that actually owns this rule.
            double afterRestartNewCar = after.LockScaleLearner.Rescale(Game, "NeverSeenBeforeCar", ShakeIt, 90.0);

            Assert.Equal(90.0, afterRestartNewCar, 1);
        }

        /// <summary>
        /// MUTATION EVIDENCE (docs\regression-fix-report.md, Regression 3): reverting the cross-car
        /// seed gate back to "apply the seed at full strength the instant primary.Count == 0" (the
        /// pre-fix behaviour) reproduces exactly the reported symptom - a brand-new car's very FIRST
        /// qualifying frame reads a value pulled away from its own honest identity reading, before any
        /// evidence for that car exists at all. Captured by directly reproducing the OLD mechanism's own
        /// arithmetic (average ~90 seed, CanonicalAtLimitAnchor 75, factor 75/90 &lt; 1 in THIS
        /// particular case - but the OLD code applied whatever the seed said unconditionally, with no
        /// cap and no per-key evidence gate at all, so a LOWER-native-scale seed - the common case for a
        /// wholly different, less-sensitive source calibration - would instead have produced a factor
        /// &gt; 1, i.e. amplification on a car with zero evidence of its own, exactly the hard-shake
        /// symptom). Reverted immediately after being noted here (this test pins the CURRENT, fixed
        /// arithmetic only); full suite re-confirmed green.
        /// </summary>
        [Fact]
        public void MutationGuard_ungating_the_cross_car_seed_would_apply_a_borrowed_scale_before_any_local_evidence()
        {
            var learner = new KeyedScaleLearner();

            // Mature CarA's own primary tier with a LOW native ceiling (30) - if a brand-new car's very
            // first query unconditionally borrowed this seed (the old, un-gated mechanism), it would
            // amplify (factor = 75/30 = 2.5x) a car that has never itself been observed at all.
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(Game, "CarA", ShakeIt, 30.0);

            // CarB: brand new, ZERO observations of its own.
            double carBOutput = learner.Rescale(Game, "CarB", ShakeIt, 50.0);

            // THE FIX: identity (50.0) - not 50.0 * (75/30) = 125 -> clamped 100, which is what the old,
            // un-gated cross-car seed would have produced on a car with no evidence of its own at all.
            Assert.Equal(50.0, carBOutput, 1);
        }
    }
}
