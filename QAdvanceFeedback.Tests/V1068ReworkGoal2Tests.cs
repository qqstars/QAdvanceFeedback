using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// v1.0.6.9 rework (docs\v1068-rework-report.md), Goal 2 - "the Lock motor not shaking at all" - and
    /// the owner's own NON-STOPPING MAX-GRIP LEARNING constraint (reaching any sample count must never
    /// freeze or end max-grip adaptation).
    /// </summary>
    public class V1068ReworkGoal2Tests
    {
        // ------------------------------------------------------------------------------------
        // GripLearner.Ratio's new useStabilityGatedCeiling parameter
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// THE CORE GOAL 2 FIX, exercised directly on <see cref="GripLearner"/> (no engine involved): the
        /// old default (<c>useStabilityGatedCeiling: true</c>, unchanged, still what every pre-existing
        /// caller/test uses) requires the reference to settle for
        /// <see cref="GripLearner.StabilityScaleSamples"/> qualifying observations after its LAST
        /// meaningful move before granting full trust - which, fed a continuous stream of small,
        /// realistic upward nudges (never quite "quiet" for 120 samples straight), never actually
        /// reaches confidence 1.0. <c>useStabilityGatedCeiling: false</c> reaches full trust at exactly
        /// <see cref="GripLearner.MaturitySamples"/> (200) regardless - the SAME plain sample-count
        /// contract the pre-existing <see cref="GripLearner.Confidence"/> property has always documented.
        /// </summary>
        [Fact]
        public void Bypassing_the_stability_gate_reaches_full_trust_at_200_samples_even_under_continuous_small_upward_nudges()
        {
            var learner = new GripLearner();
            double g = 2.0;
            for (int i = 0; i < 250; i++)
            {
                // A continuous, ever-so-slightly-rising sequence - never twice the same value, so a
                // stability gate keyed on "has the reference stopped moving" never sees 120 consecutive
                // quiet observations, exactly the real-log failure mode this task fixes.
                g += 0.01;
                learner.Observe(g);
            }

            double stabilityGated = learner.Ratio(g, applyColdStartCeiling: true, useStabilityGatedCeiling: true);
            double plainGated = learner.Ratio(g, applyColdStartCeiling: true, useStabilityGatedCeiling: false);

            Assert.True(learner.MaturityConfidence < 0.9,
                $"this fixture must reproduce the real-log failure mode (MaturityConfidence stuck well below 1.0) - got {learner.MaturityConfidence}");
            Assert.True(stabilityGated < plainGated - 0.05,
                $"the stability-gated ceiling must still under-report relative to the plain one for this never-quiet fixture - " +
                $"stabilityGated={stabilityGated}, plainGated={plainGated}");
            // Plain Confidence reaches 1.0 at 250 samples (past MaturitySamples=200), so the ceiling is
            // fully lifted and Ratio returns the raw, unceilinged ratio - close to 1.0 (a small excess is
            // expected/correct: RaiseCooldownQualifyingSamples briefly freezes Level between corroborating
            // hits while g keeps rising, so the raw ratio can legitimately sit fractionally above 1.0 -
            // GripLearner.Ratio's own doc: "a genuine full lock/spin can exceed the learned peak").
            Assert.True(plainGated > 0.95, $"expected the unceilinged ratio to sit near 1.0, got {plainGated}");
        }

        /// <summary>
        /// The Lock channel's live severity (car-level, via the real engine) must reach near the max-grip
        /// anchor within one realistic session's worth of qualifying samples (~200, a handful of braking
        /// zones) even when every individual observation is a slightly-different, never-repeating value -
        /// the direct, end-to-end regression test for "the Lock motor not shaking at all".
        /// </summary>
        [Fact]
        public void Lock_severity_reaches_near_the_max_grip_anchor_within_one_realistic_session_under_continuously_varying_G()
        {
            // The point of this test is that Lock matures under CONTINUOUSLY VARYING, never-quiet
            // driving rather than only under a repeating fixture. It used to vary G while holding the
            // source at a constant 50, which the Lock crossing gate correctly refuses to learn from
            // (a constant source is not a wheel going past its friction peak). The variation now lives
            // in the source too - the same never-settling character, arranged so it can be learned.
            var engine = new NormalizedWheelLockSlipEngine();
            double g = 2.0;
            double lastAll = 0.0;
            for (int cycle = 0; cycle < 12; cycle++)
            {
                for (int i = 0; i < 6; i++)
                {
                    g += 0.01;   // still rising overall, never repeating exactly
                    lastAll = engine.Compute(
                        TestFrames.BrakingSampleFor(g - i * 0.02),
                        QAdvanceFeedback.Core.Corners.Uniform(45.0 + i),
                        TestFrames.Corners0).LockAll;
                }
                for (int i = 0; i < 66; i++)
                {
                    g += 0.01;
                    lastAll = engine.Compute(
                        TestFrames.BrakingSampleFor(g), TestFrames.Corners50, TestFrames.Corners0).LockAll;
                }
            }

            Assert.True(lastAll >= 60.0,
                $"after ~260 qualifying, continuously-varying-but-never-quiet observations, Lock severity " +
                $"must be well past the low, permanently-ceilinged band the pre-fix stability gate produced - got {lastAll}");
        }

        // ------------------------------------------------------------------------------------
        // NON-STOPPING MAX-GRIP LEARNING (owner's explicit constraint) - the learned value must keep
        // moving well past 1,000+ samples, and reaching any of MaturitySamples (200),
        // CalibrationConfidenceScaleSamples (200), or the 1,000,000 saturation cap must never freeze the
        // LEARNED VALUE itself (only counters may saturate).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void GripLearners_learned_peak_still_moves_after_1000_plus_qualifying_samples()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 1200; i++) learner.Observe(3.0); // establish + fully mature a peak well past 1,000 samples

            double levelAt1200 = learner.LearnedPeakG;
            Assert.True(learner.Samples >= 1000);

            // A genuinely harder, repeated (corroborated) event after 1,000+ samples must still move the
            // learned value - "reaching 200 (or any other) sample count must never freeze or end max-grip
            // adaptation" (the owner's own explicit requirement).
            for (int i = 0; i < 10; i++) learner.Observe(5.0);

            Assert.True(learner.LearnedPeakG > levelAt1200 + 0.1,
                $"the learned peak must still move after 1,000+ samples - was {levelAt1200}, now {learner.LearnedPeakG}");
        }

        [Fact]
        public void KeyedScaleLearners_primary_tier_average_still_moves_after_1000_plus_observations_even_once_the_full_trust_floor_is_active()
        {
            var scaleLearner = new KeyedScaleLearner();
            // Both observers, as the engine always does. Since the anti-correlation fix the ceiling is
            // derived from the source's own distribution rather than from the mean of its at-limit
            // readings, so a learner shown only at-limit observations has nothing to take a percentile
            // of - a state the engine never produces. See KeyedScaleLearner.LearnedCeilingForKey.
            for (int i = 0; i < 1200; i++)
            {
                scaleLearner.ObserveAtPhysicalLimit("g", "c", "s", 60.0);
                scaleLearner.ObserveGeneral("g", "c", "s", 60.0);
            }

            double? ceilingAt1200 = scaleLearner.LearnedCeiling("g", "c", "s", out bool primary1);
            Assert.True(primary1);
            Assert.True(scaleLearner.LearnedCeiling("g", "c", "s", out _).HasValue);

            // Past CalibrationConfidenceScaleSamples (200), the "full-trust floor" (weight=1.0 regardless
            // of dispersion) is active - confirm the underlying AVERAGE this floor trusts fully still
            // moves when the incoming readings genuinely change, rather than the ceiling being frozen at
            // whatever value existed at sample 200.
            for (int i = 0; i < 400; i++)
            {
                scaleLearner.ObserveAtPhysicalLimit("g", "c", "s", 90.0);
                scaleLearner.ObserveGeneral("g", "c", "s", 90.0);
            }
            double? ceilingAfter = scaleLearner.LearnedCeiling("g", "c", "s", out bool primary2);

            Assert.True(primary2);
            Assert.True(ceilingAfter.HasValue && ceilingAt1200.HasValue && ceilingAfter.Value > ceilingAt1200.Value + 1.0,
                $"the primary tier's own average must keep moving toward new evidence past 1,000+ samples - was {ceilingAt1200}, now {ceilingAfter}");
        }

        [Fact]
        public void Saturating_GripLearners_sample_counter_at_the_cap_does_not_freeze_the_learned_peak()
        {
            // Drive the persisted sample counter to its saturation cap without materially changing G, then
            // confirm the learned VALUE still moves afterward - the counter freezes, learning does not (the
            // same contract docs\adaptive-peak-learner-report.md's own Part 1 tests already pin; re-asserted
            // here as part of this task's own explicit non-stopping-learning audit).
            var learner = new GripLearner();
            learner.Load(learnedPeakG: 3.0, samples: GripLearner.SampleCountSaturationCap);
            Assert.Equal(GripLearner.SampleCountSaturationCap, learner.Samples);

            double before = learner.LearnedPeakG;
            for (int i = 0; i < 10; i++) learner.Observe(4.5);

            Assert.Equal(GripLearner.SampleCountSaturationCap, learner.Samples); // counter stays capped
            Assert.True(learner.LearnedPeakG > before + 0.1,
                $"the learned peak must keep moving even once the sample counter itself is saturated - was {before}, now {learner.LearnedPeakG}");
        }
    }

    /// <summary>Small, shared fixture helper for this file - mirrors
    /// <c>NormalizedWheelLockSlipEngineTests.BrakingSample</c>'s own construction exactly (a
    /// decelerating ground-speed derivative, so <c>LongitudinalDirectionResolver</c> reads Slowing from
    /// the first frame, independent of the G magnitude itself).</summary>
    internal static class TestFrames
    {
        public static QAdvanceFeedback.Core.Corners Corners50 { get; } = QAdvanceFeedback.Core.Corners.Uniform(50.0);
        public static QAdvanceFeedback.Core.Corners Corners0 { get; } = QAdvanceFeedback.Core.Corners.Zero;

        public static QAdvanceFeedback.Core.ITelemetrySample BrakingSampleFor(double gMagnitude, double brakePercent = 80.0)
        {
            var oldFrame = new QAdvanceFeedback.Core.TelemetryFrame(groundSpeedKmh: 101.0);
            var newFrame = new QAdvanceFeedback.Core.TelemetryFrame(
                groundSpeedKmh: 100.0, longitudinalG: -gMagnitude, brakePercent: brakePercent);
            return new QAdvanceFeedback.Core.TelemetrySample(newFrame, oldFrame, System.DateTime.UtcNow, System.TimeSpan.FromMilliseconds(16));
        }

        /// <summary>
        /// Drives a Lock channel's learned SMax toward <paramref name="atLimitRaw"/> through BRAKING
        /// TRACTION CROSSINGS - the lock source climbing while achieved G falls away from this car's own
        /// peak, which is the only evidence <c>LockCrossingGate</c> accepts.
        /// <para/>
        /// SHARED BY EVERY TEST THAT USED TO WARM LOCK WITH A CONSTANT TRACE. Holding a constant source
        /// at a constant G for 300 frames stopped teaching anything when the Lock crossing gate was
        /// enabled (2026-09-05), and correctly so: a wheel at an unchanging lock level under unchanging
        /// deceleration is not a wheel going past its friction peak. Nothing those tests ASSERT has
        /// changed - only the way they arrange the precondition, since "Lock has learned a ceiling" now
        /// means something narrower. Kept in one place so the shape stays consistent across the five
        /// files that need it.
        /// <para/>
        /// The G fall is shallow (0.5% of peak per frame, so 2.5% over the gate's 5-frame trend against
        /// its -0.05 g bar) - enough to qualify while staying inside the corner-local detector's own
        /// notion of "at the limit", which is what arms Lock's gate.
        /// </summary>
        /// <summary>How much of its OWN at-limit value a source gives up per frame on the approach - see
        /// the ramp's own remarks. 2% per frame over the 5 approach frames means every source starts the
        /// rise at 90% of its at-limit reading, so the rise is always comfortably more than
        /// <c>LockRise</c> (1.0 in source units) even for the smallest-scale source these tests use.</summary>
        private const double RampFractionPerFrame = 0.02;

        public static void WarmLockWithCrossings(
            QAdvanceFeedback.Core.Normalized.NormalizedWheelLockSlipEngine engine,
            double peak, double atLimitRaw, int cycles = 40,
            string gameId = "", string carId = "", string lockSourceIdentity = "")
        {
            const int rise = 6;        // TrendFrames + 1: the crossing fires on the last of these
            const int recover = 66;    // longer than the 1 s hold, so the next cycle can arm again
            double step = peak * 0.005;

            // Establish this car's own peak G first, so the at-limit detector has a reference.
            for (int i = 0; i < 30; i++)
                engine.Compute(BrakingSampleFor(peak), QAdvanceFeedback.Core.Corners.Uniform(atLimitRaw),
                    QAdvanceFeedback.Core.Corners.Zero, gameId, carId, lockSourceIdentity: lockSourceIdentity);

            for (int c = 0; c < cycles; c++)
            {
                // Past the friction peak: more lock buying less braking. The ramp is arranged to LAND
                // EXACTLY ON atLimitRaw at the detection frame, so the onset snapshot - and therefore the
                // learned ceiling - is atLimitRaw itself. That is what lets the tests that used to warm
                // with a constant source at atLimitRaw keep their original expected numbers.
                //
                // THE RAMP IS PROPORTIONAL, NOT ABSOLUTE (v1.1.0 - a latent bug in this helper, exposed by
                // the crossing gates' G-banded walk-back, which was the first code ever to READ these
                // ramp frames rather than only the last one). It used to step `atLimitRaw - 5 + i`: a
                // fixed 1.0 per frame for EVERY source. That is self-contradictory for the one thing this
                // helper exists to model - several sources reading DIFFERENT native numbers at the SAME
                // physical moment - because a 5-unit approach is 5.6% of a source that reads 90 at the
                // limit but 17% of one that reads 30. The three synthetic sources in
                // PerSourceCalibrationTests therefore arrived at the limit along three physically
                // different trajectories, and anything sampling before the final frame saw them diverge.
                // Real captures do not behave that way: on the four corpus sessions that carry a
                // configured source AND the Raw fallback on the SAME frames, the configured/fallback
                // ratio at crossings is unchanged by the walk-back to three decimal places (1.045 ->
                // 1.043, 0.684 -> 0.684, 0.629 -> 0.623, 1.000 -> 1.000).
                //
                // Scaling by a FRACTION makes every source approach the limit along the same physical
                // curve, which is what this helper always meant. The final frame is still exactly
                // atLimitRaw, so every expectation that reads only the detection frame is untouched.
                for (int i = 0; i < rise; i++)
                    engine.Compute(BrakingSampleFor(peak - i * step),
                        QAdvanceFeedback.Core.Corners.Uniform(
                            System.Math.Max(1.0, atLimitRaw * (1.0 - (rise - 1 - i) * RampFractionPerFrame))),
                        QAdvanceFeedback.Core.Corners.Zero, gameId, carId, lockSourceIdentity: lockSourceIdentity);

                // Grip recovered, source held AT atLimitRaw. Holding it at the top rather than at the
                // bottom of the ramp matters: the only dip in the series is then the ramp itself, so the
                // trend can only turn positive once the source is back at atLimitRaw - which pins every
                // crossing, and therefore every taught value, to exactly that number.
                for (int i = 0; i < recover; i++)
                    engine.Compute(BrakingSampleFor(peak),
                        QAdvanceFeedback.Core.Corners.Uniform(atLimitRaw),
                        QAdvanceFeedback.Core.Corners.Zero, gameId, carId, lockSourceIdentity: lockSourceIdentity);
            }
        }
    }
}
