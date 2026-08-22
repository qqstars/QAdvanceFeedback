using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for the cold-vs-converged over-reporting safety fix (docs\stability-confidence-fix-report.md):
    /// <see cref="GripLearner.Ratio"/>'s cold-start ceiling now gates on
    /// <see cref="GripLearner.MaturityConfidence"/> (sample count AND stability of the reference itself -
    /// see <see cref="GripLearner.QuietStreak"/>), not sample count alone. Measured directly against all
    /// nine real logs (docs\band-accuracy-simulation-report.md): 13,708 of ~73,000 qualifying frames
    /// published a severity the SAME session's own later evidence contradicted, once the OLD sample-count-
    /// only ceiling had fully lifted (200 samples) while the reference was still visibly climbing -
    /// including 3,858 frames reaching the literal maximum (100) later contradicted down to as low as 3.0.
    /// </summary>
    public class StabilityConfidenceFixTests
    {
        // ------------------------------------------------------------------------------------
        // THE CORE FIX: sample count alone is NOT sufficient any more.
        // ------------------------------------------------------------------------------------

        /// <summary>
        /// THE HEADLINE, DIRECTLY: a reference that has accumulated well past
        /// <see cref="GripLearner.MaturitySamples"/> (200) qualifying samples but has JUST been revised
        /// upward by a large amount must still be ceilinged - the exact real-world failure mode the
        /// safety report measured (confidence reaches 1.0 by sample count, but the peak keeps climbing).
        /// </summary>
        [Fact]
        public void A_reference_still_climbing_well_past_200_samples_stays_ceilinged_not_fully_trusted()
        {
            var learner = new GripLearner();

            // Establish a settled 2.0g reference, then let it climb via repeated, corroborated raises to
            // a new, higher level RIGHT UP TO the 200-sample mark and beyond, so sample count alone would
            // have declared "fully mature" under the OLD design.
            for (int i = 0; i < 20; i++) learner.Observe(2.0);
            for (int i = 0; i < 200; i++) learner.Observe(2.0 + i * 0.01); // keeps nudging the peak upward, never settling

            Assert.Equal(1.0, learner.Confidence, 6); // sample count alone says "fully mature"

            // A fresh reading right at the CURRENT (still-recently-revised) peak must NOT read a raw,
            // unceilinged 1.0 - the reference has not genuinely settled yet.
            double ratio = learner.Ratio(learner.LearnedPeakG);
            Assert.True(ratio < 0.999,
                $"a still-climbing reference must stay ceilinged even with 200+ samples, got ratio {ratio}");
        }

        /// <summary>
        /// THE OTHER HALF: once the reference genuinely stops moving (even though it took a while to get
        /// there), full trust IS eventually reached - "conservative for longer, not conservative forever"
        /// (acceptance item 6).
        /// </summary>
        [Fact]
        public void The_same_reference_reaches_full_trust_once_it_genuinely_stops_moving()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 20; i++) learner.Observe(2.0);
            for (int i = 0; i < 200; i++) learner.Observe(2.0 + i * 0.01); // climbing
            double settledPeak = learner.LearnedPeakG;

            // Now hold at the settled peak for a further settling window (comfortably more than
            // StabilityScaleSamples of quiet room).
            int settlingWindow = (int)GripLearner.StabilityScaleSamples + 50;
            for (int i = 0; i < settlingWindow; i++) learner.Observe(settledPeak);

            double ratio = learner.Ratio(learner.LearnedPeakG);
            Assert.Equal(1.0, ratio, 3);
        }

        /// <summary>
        /// UNDER-REPORT, NEVER OVER-REPORT, DIRECTLY: while a reference is still visibly moving, the
        /// published ratio must never exceed what the SAME physical reading would settle to once the
        /// reference has stopped moving - the project's own hard safety rule, checked here as a direct
        /// unit-level invariant (not merely inferred from a real-log measurement).
        /// </summary>
        [Fact]
        public void While_still_climbing_the_ceilinged_ratio_never_exceeds_the_eventual_settled_ratio()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 20; i++) learner.Observe(2.0);

            double midClimbRatio = -1.0;
            for (int i = 0; i < 200; i++)
            {
                learner.Observe(2.0 + i * 0.01);
                if (i == 150) midClimbRatio = learner.Ratio(learner.LearnedPeakG);
            }

            // Let it fully settle at whatever it reached.
            double finalPeak = learner.LearnedPeakG;
            int settlingWindow = (int)GripLearner.StabilityScaleSamples + 50;
            for (int i = 0; i < settlingWindow; i++) learner.Observe(finalPeak);
            double settledRatioAtFinalPeak = learner.Ratio(finalPeak);

            Assert.True(midClimbRatio <= settledRatioAtFinalPeak + 1e-9,
                $"cold/unstable ratio ({midClimbRatio}) must never exceed the eventual settled ratio ({settledRatioAtFinalPeak})");
        }

        // ------------------------------------------------------------------------------------
        // NO HARD SAMPLE GATE / CONTINUOUS - the owner's own repeated, emphatic requirement.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void MaturityConfidence_is_continuous_no_step_at_any_sample_count_while_holding_steady()
        {
            var learner = new GripLearner();
            double previous = -1.0;
            double maxJump = 0.0;

            for (int i = 0; i < 400; i++)
            {
                learner.Observe(2.0); // constant - exercises the stability ramp settling smoothly
                double ratio = learner.Ratio(2.0);
                if (previous >= 0.0) maxJump = System.Math.Max(maxJump, System.Math.Abs(ratio - previous));
                previous = ratio;
            }

            Assert.True(maxJump < 0.05, $"ratio must never step sample-by-sample, max jump was {maxJump}");
        }

        [Fact]
        public void QuietStreak_starts_at_zero_for_a_brand_new_learner_identity_as_the_true_cold_state()
        {
            var learner = new GripLearner();
            Assert.Equal(0.0, learner.QuietStreak, 9);
            Assert.Equal(0.0, learner.MaturityConfidence, 9);
        }

        // ------------------------------------------------------------------------------------
        // PERSISTENCE - QuietStreak round-trips through KeyedGripLearner exactly like the existing
        // Raise/Lower candidate state (docs\adaptive-peak-learner-report.md's own precedent).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_settled_quiet_streak_round_trips_through_KeyedGripLearner_reproducing_full_trust_immediately()
        {
            var keyed = new KeyedGripLearner();
            // Establish and fully settle a reference within one session.
            for (int i = 0; i < 20; i++) keyed.Observe("GameA", "Car1", 2.0);
            int settlingWindow = (int)GripLearner.StabilityScaleSamples + 100;
            for (int i = 0; i < settlingWindow; i++) keyed.Observe("GameA", "Car1", 2.0); // holds steady - settles

            double ratioBeforeRestart = keyed.Ratio("GameA", "Car1", 2.0);
            Assert.Equal(1.0, ratioBeforeRestart, 3); // genuinely settled within this same session

            var exported = keyed.ExportAll();
            string key = KeyedGripLearner.MakeKey("GameA", "Car1");
            Assert.True(exported.ContainsKey(key));
            Assert.True(exported[key].QuietStreak > 0.0, "expected a settled quiet streak to be exported");

            var restored = new KeyedGripLearner();
            restored.ImportAll(exported);

            // Zero new observations after restore - a genuinely already-settled car must read with full
            // trust IMMEDIATELY, not restart its settling clock from scratch (the same "restart with no
            // new driving reproduces the previous mapping" guarantee the cold/warm blend already
            // provides for the peak value itself, now extended to stability).
            double ratioAfterRestart = restored.Ratio("GameA", "Car1", 2.0);
            Assert.Equal(1.0, ratioAfterRestart, 3);
        }

        [Fact]
        public void A_pre_version_7_document_imports_with_zero_quiet_streak_bit_identical_to_fresh()
        {
            // Simulates exactly what RuntimeStore.LoadFromDisk hands ImportAll for a file written before
            // this feature existed: QuietStreak is absent from the JSON, so Newtonsoft leaves it at its
            // field-initialiser default (0.0).
            var legacyState = new GripLearnerState { PeakG = 3.1, Samples = 400 };
            Assert.Equal(0.0, legacyState.QuietStreak, 9);

            var keyed = new KeyedGripLearner();
            keyed.ImportAll(new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = legacyState
            });

            // A pre-existing, high-sample-count persisted profile with no settledness signal at all must
            // still behave conservatively (ceilinged) immediately after import - not instantly fully
            // trusted purely because Samples is large.
            double ratio = keyed.Ratio("GameA", "Car1", 3.1);
            Assert.Equal(GripLearner.ColdStartCeilingRatio, ratio, 6);
        }

        // ------------------------------------------------------------------------------------
        // ANCHOR REACHABILITY - the anchor (full trust) must be genuinely reachable, mirroring why
        // KeyedScaleLearner needed its own full-trust floor (docs\anchor-rescale-report.md) - but here
        // reachability falls out of the mechanism itself (the estimate's own trajectory genuinely stops
        // moving), with NO artificial "regardless of X" override needed.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Full_trust_is_genuinely_reachable_not_an_asymptote_that_never_quite_arrives()
        {
            var learner = new GripLearner();
            // A single constant value throughout, fed for comfortably more than BOTH MaturitySamples
            // (sample-count term) and StabilityScaleSamples (settling term), so both factors of
            // MaturityConfidence reach exactly 1.0.
            int total = GripLearner.MaturitySamples + (int)GripLearner.StabilityScaleSamples * 3;
            for (int i = 0; i < total; i++) learner.Observe(2.0);

            // EXACTLY 1.0, not merely "close" - confirms the concave stability ramp truly reaches its
            // ceiling rather than asymptotically approaching it forever.
            Assert.Equal(1.0, learner.MaturityConfidence, 12);
        }
    }
}
