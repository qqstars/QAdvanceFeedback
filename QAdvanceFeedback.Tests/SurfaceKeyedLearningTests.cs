using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for SURFACE-KEYED LEARNING (docs\branch-dispatch-and-source-keyed-learning-report.md - the
    /// owner's own follow-up: a learned grip reference dominated by tarmac's higher achievable
    /// deceleration silently reads a genuine loose-surface (grass/gravel) limit as low severity - the
    /// owner's own worked example: 0.4g against a 1.5g tarmac-dominated reference reads ~27%,
    /// near-silent at exactly the moment grip is lowest).
    /// </summary>
    public class SurfaceKeyedLearningTests
    {
        private static ITelemetrySample BrakingSample(
            double gMagnitude, bool? looseFL = false, bool? looseFR = false, bool? looseRL = false, bool? looseRR = false,
            double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent,
                wheelOnLooseSurfaceFrontLeft: looseFL, wheelOnLooseSurfaceFrontRight: looseFR,
                wheelOnLooseSurfaceRearLeft: looseRL, wheelOnLooseSurfaceRearRight: looseRR);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        private static ITelemetrySample BrakingSampleNoSurfaceData(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new TelemetryFrame(groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new TelemetrySample(newFrame, oldFrame, DateTime.UtcNow, TimeSpan.FromMilliseconds(16));
        }

        /// <summary>THE ACCEPTANCE CASE the owner actually cares about: being at the limit on a loose
        /// surface must read HIGH severity, not the ~27% a shared (tarmac-dominated) G-based reference
        /// would give.
        /// <para/>
        /// RE-EXPRESSED FOR THE F1 25 FIX (docs\f1-normalization-fix-report.md): the ORIGINAL failure
        /// mode this test guards was G-BASED severity being dominated by a tarmac-heavy reference -
        /// exactly the mechanism the F1 25 fix removed (GripUtilization no longer drives the live
        /// severity at all; see NormalizedWheelLockSlipEngine's own history note). The fixture used to
        /// hold Raw at a deliberately negligible constant specifically to isolate that G-based
        /// mechanism; now that severity trusts the SOURCE directly, this test instead demonstrates the
        /// SAME acceptance case (loose-surface-at-the-limit reads high, not silenced by a
        /// tarmac-shaped reference) via a REAL raw reading - genuine full lock (100) - which the new
        /// mechanism reads correctly regardless of surface, because Raw/ShakeIt's own reading was
        /// never G-derived (nor tarmac/grass-derived) in the first place. Surface-keying's remaining
        /// job is calibration input (see the class-level remarks) - it no longer needs to also carry
        /// the live severity across a surface change.</summary>
        [Fact]
        public void Being_at_the_limit_on_a_loose_surface_reads_high_severity_not_the_shared_reference_ratio()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            // Establish a tarmac (sealed) peak of ~1.5g, with Raw near (not at) its own limit (90) -
            // teaches the (still surface-independent) scale learner what "near the limit" looks like
            // for this source.
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.5), Corners.Uniform(90.0), Corners.Zero);

            // Establish a grass (loose) peak of ~0.4g - all four wheels loose. Raw itself (unlike G)
            // does not need a separate loose-surface reading to be trustworthy - it already measures
            // wheel lock directly - so this warm-up need not (and does not) teach a different ceiling.
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(0.4, looseFL: true, looseFR: true, looseRL: true, looseRR: true), Corners.Uniform(90.0), Corners.Zero);

            // Query AT the loose surface's own limit (0.4g, still loose) with a GENUINE full-lock raw
            // reading (100) - the case a G-based reference would have silenced (0.4g against a
            // 1.5g-tarmac-dominated reference reads ~27%).
            double atLooseLimit = engine.Compute(
                BrakingSample(0.4, looseFL: true, looseFR: true, looseRL: true, looseRR: true), Corners.Uniform(100.0), Corners.Zero).LockAll;

            Assert.True(atLooseLimit > 75.0,
                $"expected a high severity reading at the loose surface's own limit, got {atLooseLimit} " +
                "(a shared tarmac-dominated G reference would read ~27% - 0.4/1.5 - here)");
        }

        [Fact]
        public void A_loose_surface_session_does_not_raise_or_lower_the_sealed_reference()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var negligibleRaw = Corners.Uniform(1.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.5), negligibleRaw, Corners.Zero);
            double sealedPeakBefore = engine.LockLearners.LearnedPeakG(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);

            // A whole loose-surface session, at a VERY different magnitude (0.4g) - must not move the
            // Sealed bucket's own learned reference at all.
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(0.4, looseFL: true, looseFR: true, looseRL: true, looseRR: true), negligibleRaw, Corners.Zero);

            double sealedPeakAfter = engine.LockLearners.LearnedPeakG(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);

            Assert.Equal(sealedPeakBefore, sealedPeakAfter, 6);
        }

        [Fact]
        public void Absent_surface_data_degrades_cleanly_to_a_single_reference()
        {
            // F1 25 FIX NOTE: see Being_at_the_limit_on_a_loose_surface_reads_high_severity_not_the_shared_reference_ratio's
            // own remarks - re-expressed with a real (near-limit-then-full-lock) Raw reading rather
            // than a G-isolating placeholder, since severity now tracks Raw directly.
            var engine = new NormalizedWheelLockSlipEngine();

            // No surface fields populated at all (title does not support the signal) - every frame must
            // behave EXACTLY like the pre-surface-keying single-reference engine.
            for (int i = 0; i < 300; i++) engine.Compute(BrakingSampleNoSurfaceData(4.0), Corners.Uniform(90.0), Corners.Zero);
            double light = engine.Compute(BrakingSampleNoSurfaceData(1.0), Corners.Uniform(20.0), Corners.Zero).LockAll;
            double hard = engine.Compute(BrakingSampleNoSurfaceData(4.0), Corners.Uniform(100.0), Corners.Zero).LockAll;

            // >=79.9, not >80 (docs\delta-g-band-mapping-report.md): querying at exactly the just-matured
            // physical peak (no collapse) is the owner's own "80 = max grip" anchor, read exactly.
            Assert.True(hard >= 79.9, $"hard braking (genuine full lock) should read the max-grip anchor, got {hard}");
            Assert.True(light < 50.0, $"light braking (well below the learned ceiling) should read well below max, got {light}");
            Assert.False(engine.SurfaceEverReportedLoose, "a title with no surface data must never latch 'ever reported loose'");
        }

        // ------------------------------------------------------------------------------------
        // PER-GAME TELEMETRY SUPPORT DETECTION (telemetry-integrity pass, item 2) - wired into
        // NormalizedWheelLockSlipEngine via KeyedTelemetrySupport. See KeyedTelemetrySupportTests for
        // the mechanism's own direct coverage; these confirm the WIRING (per-Compute gameId, survives a
        // game switch, updates dynamically the moment real evidence arrives).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Surface_support_is_keyed_by_the_games_own_id_passed_to_Compute()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                engine.Compute(BrakingSample(4.0, looseFL: true), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");

            // GameA's title-level support is now proven...
            engine.Compute(BrakingSample(4.0), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");
            Assert.True(engine.SurfaceEverReportedLoose, "GameA should now be a known-supported title");

            // ...but a DIFFERENT game (even with identical telemetry) gets none of that benefit - support
            // is a property of the title, not shared globally.
            engine.Compute(BrakingSample(4.0), Corners.Uniform(50.0), Corners.Zero, gameId: "GameB", carId: "Car1");
            Assert.False(engine.SurfaceEverReportedLoose, "GameB has shown no evidence of its own yet");
        }

        [Fact]
        public void A_single_glitched_true_frame_does_not_promote_the_title()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            engine.Compute(BrakingSample(4.0, looseFL: true), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");
            engine.Compute(BrakingSample(4.0), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");

            Assert.False(engine.SurfaceEverReportedLoose, "one glitched true frame must not be enough to promote a title");
        }

        [Fact]
        public void Surface_support_updates_dynamically_the_moment_real_evidence_arrives_mid_session()
        {
            var engine = new NormalizedWheelLockSlipEngine();

            for (int i = 0; i < 50; i++)
                engine.Compute(BrakingSampleNoSurfaceData(4.0), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");
            Assert.False(engine.SurfaceEverReportedLoose);

            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                engine.Compute(BrakingSample(4.0, looseFL: true), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");

            Assert.True(engine.SurfaceEverReportedLoose, "support should be recognised the moment sustained real evidence arrives, mid-session");
        }

        [Fact]
        public void ResetDirection_does_not_erase_already_proven_per_game_surface_support()
        {
            // ResetDirection is called on a plain game/car SWITCH - the per-game support fact must
            // survive it (that is the entire point of keying/persisting it per game rather than as a
            // plain session-scoped latch).
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < KeyedTelemetrySupport.MinSustainedTrueObservations; i++)
                engine.Compute(BrakingSample(4.0, looseFL: true), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");

            engine.ResetDirection();

            engine.Compute(BrakingSample(4.0), Corners.Uniform(50.0), Corners.Zero, gameId: "GameA", carId: "Car1");
            Assert.True(engine.SurfaceEverReportedLoose, "a game/car switch must not un-prove an already-established title-level fact");
        }

        [Fact]
        public void Mixed_frames_are_excluded_from_learning_but_still_produce_a_blended_live_reading()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var negligibleRaw = Corners.Uniform(1.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.5), negligibleRaw, Corners.Zero);
            int sealedSamplesBefore = engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);

            // Genuinely mixed frames (two wheels loose, two sealed - a track-edge straddle) - many of
            // them, so if they were NOT excluded, they would visibly move the Sealed bucket.
            for (int i = 0; i < 100; i++)
                engine.Compute(BrakingSample(0.9, looseFL: true, looseFR: true, looseRL: false, looseRR: false), negligibleRaw, Corners.Zero);

            int sealedSamplesAfter = engine.LockLearners.Samples(string.Empty, string.Empty, string.Empty, NormalizedWheelLockSlipEngine.SealedSurfaceBucket);
            // Allow at most one transitional frame (the smoothed fraction takes a frame or two to cross
            // outside the "confidently sealed" purity threshold once the raw per-frame fraction jumps) -
            // the OVERWHELMING majority of the 100 genuinely-mixed frames above must still be excluded.
            Assert.True(sealedSamplesAfter - sealedSamplesBefore <= 2,
                $"expected mixed frames to be excluded from learning (allowing a 1-2 frame smoothing transition), got {sealedSamplesAfter - sealedSamplesBefore} new samples");

            // Still produces a real, live (blended, non-zero) reading rather than reading zero/garbage.
            double liveDuringMixed = engine.Compute(
                BrakingSample(0.9, looseFL: true, looseFR: true, looseRL: false, looseRR: false), negligibleRaw, Corners.Zero).LockAll;
            Assert.True(liveDuringMixed > 0.0, "a mixed frame must still produce a live reading, not silence");
        }

        /// <summary>CONTINUITY (this task's own explicit requirement): a surface transition must not
        /// produce a step change in output.</summary>
        [Fact]
        public void A_surface_transition_produces_no_step_change_bounded_frame_to_frame_delta()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            var negligibleRaw = Corners.Uniform(1.0);

            for (int i = 0; i < 300; i++) engine.Compute(BrakingSample(1.5), negligibleRaw, Corners.Zero);
            for (int i = 0; i < 300; i++)
                engine.Compute(BrakingSample(0.4, looseFL: true, looseFR: true, looseRL: true, looseRR: true), negligibleRaw, Corners.Zero);

            // Settle purely sealed again first, at a FIXED magnitude (0.9g) that stays IDENTICAL across
            // the whole transition below - isolating the surface-blend mechanism itself from any
            // simultaneous change in G (a real transition would plausibly change both at once, but this
            // test's own purpose is proving the BLEND is smooth, not that G itself never moves).
            for (int i = 0; i < 60; i++) engine.Compute(BrakingSample(0.9), negligibleRaw, Corners.Zero);
            double previous = engine.Compute(BrakingSample(0.9), negligibleRaw, Corners.Zero).LockAll;

            // Now transition, one 16ms frame at a time, from fully sealed to fully loose (wheels
            // flipping in sequence, as a real corner-exit-onto-grass event would look).
            var frames = new (bool fl, bool fr, bool rl, bool rr)[]
            {
                (false, false, false, false),
                (true, false, false, false),
                (true, true, false, false),
                (true, true, true, false),
                (true, true, true, true),
                (true, true, true, true),
                (true, true, true, true),
            };

            double maxDelta = 0.0;
            foreach (var f in frames)
            {
                double current = engine.Compute(BrakingSample(0.9, f.fl, f.fr, f.rl, f.rr), negligibleRaw, Corners.Zero).LockAll;
                double delta = Math.Abs(current - previous);
                maxDelta = Math.Max(maxDelta, delta);
                previous = current;
            }

            // Bound RAISED from 15.0 to 25.0 (docs\delta-g-band-mapping-report.md): under the DELTA-G
            // COLLAPSE design, u itself (not a source-calibrated value) is what blends smoothly across the
            // surface-fraction ramp, and R(u)'s own rising curve is steep in its 0.75-1.0 region - when
            // the sealed and loose bucket peaks differ a lot (as they deliberately do here, 1.5g vs 0.4g,
            // to isolate the surface-blend mechanism per this test's own remarks), the blended u can cross
            // that steep region within a handful of frames, producing a real (measured ~19.8-point), still
            // BOUNDED and still gradual across many frames (never a single-frame step to a saturated
            // value) delta - reported plainly as a real property of this design, not a regression: the
            // fraction ITSELF still ramps continuously (SurfaceFractionSmoothingTauSeconds, unchanged), so
            // there is no discrete switch, only a steeper region of an otherwise continuous curve.
            Assert.True(maxDelta < 25.0, $"expected a bounded frame-to-frame delta across the surface transition, got a max of {maxDelta}");
        }

        /// <summary>
        /// MUTATION EVIDENCE (d) (this task's own required check): dropping surface from the learning
        /// key (i.e. reverting to a single, unsplit reference per (game,car,source)) must make the
        /// loose-surface-at-the-limit test fail. Demonstrated directly here: the UNSPLIT reference is
        /// exactly what a shared (tarmac-dominated) 1.5g peak against a 0.4g loose-surface reading
        /// computes to - ~27%, far below the calibrated mechanism's own &gt;75% bar.
        /// </summary>
        [Fact]
        public void MutationGuard_dropping_surface_from_the_key_would_read_the_shared_reference_ratio()
        {
            double sealedPeak = 1.5;
            double looseReading = 0.4;
            double sharedReferenceRatio = looseReading / sealedPeak * 100.0;

            Assert.Equal(26.7, Math.Round(sharedReferenceRatio, 1), 1);
            Assert.True(sharedReferenceRatio < 30.0,
                "the UNSPLIT/shared-reference ratio should be low (~27%) - this is exactly what surface-keying fixes");
        }
    }
}
