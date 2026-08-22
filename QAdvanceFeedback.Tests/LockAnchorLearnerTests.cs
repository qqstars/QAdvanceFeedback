using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// FEATURE C (docs\v1068-four-range-report.md) - WHEELLOCK ONLY. Direct unit tests of
    /// <see cref="LockAnchorLearner"/>'s own bracket-interpolation, branch filter, and dynamic update
    /// rule, independent of the full engine (which is covered separately in
    /// <see cref="NormalizedWheelLockSlipEngineTests"/>).
    /// </summary>
    public class LockAnchorLearnerTests
    {
        private const string Game = "TestGame";
        private const string Car = "TestCar";
        private const string Source = "Raw";

        [Fact]
        public void A_fresh_learner_has_no_anchors_and_nothing_is_taught_until_Smax_exists()
        {
            var learner = new LockAnchorLearner();
            Assert.Null(learner.LearnedS75(Game, Car, Source));
            Assert.Null(learner.LearnedS90(Game, Car, Source));

            // A clean bracket crossing of 0.75 (u: 0.70 -> 0.80, source: 20 -> 24) with NO Smax supplied
            // (null) must be held, not accepted - there is nothing yet to evaluate the branch filter
            // against.
            learner.Observe(Game, Car, Source, 0.70, 20.0, smaxRaw: null);
            learner.Observe(Game, Car, Source, 0.80, 24.0, smaxRaw: null);

            Assert.Null(learner.LearnedS75(Game, Car, Source));
            Assert.Equal(0, learner.AcceptedObservationCount);
            Assert.Equal(0, learner.RejectedByBranchFilterCount);
        }

        [Fact]
        public void A_bracket_crossing_below_Smax_is_accepted_and_interpolated_linearly()
        {
            var learner = new LockAnchorLearner();
            double smax = 75.0;

            // u rises from 0.70 (source 18) to 0.80 (source 22) across one frame - 0.75 falls exactly
            // halfway, so the interpolated source value should be exactly halfway too (18 + 0.5*4 = 20).
            learner.Observe(Game, Car, Source, 0.70, 18.0, smax);
            learner.Observe(Game, Car, Source, 0.80, 22.0, smax);

            Assert.Equal(1, learner.AcceptedObservationCount);
            Assert.Equal(0, learner.RejectedByBranchFilterCount);
            Assert.NotNull(learner.LearnedS75(Game, Car, Source));
            Assert.Equal(20.0, learner.LearnedS75(Game, Car, Source).Value, 3);
        }

        [Fact]
        public void THE_BRANCH_FILTER_rejects_a_crossing_whose_interpolated_source_is_at_or_above_Smax()
        {
            // THE OWNER'S KEY IDEA: a frame reaching 75%/90% of the corner's own max-grip G is only a
            // valid rising-branch observation if the source is BELOW Smax - at/above it, the car is
            // already PAST the limit (G falling because the wheel is locking), contaminating a rising-
            // branch anchor with a post-limit reading.
            var learner = new LockAnchorLearner();
            double smax = 30.0;

            // u crosses 0.75 while the interpolated source (halfway between 32 and 36 = 34) is ABOVE
            // Smax(30) - the car is past the limit on this "corner", so this candidate must be discarded.
            learner.Observe(Game, Car, Source, 0.70, 32.0, smax);
            learner.Observe(Game, Car, Source, 0.80, 36.0, smax);

            Assert.Null(learner.LearnedS75(Game, Car, Source));
            Assert.Equal(0, learner.AcceptedObservationCount);
            Assert.Equal(1, learner.RejectedByBranchFilterCount);
        }

        [Fact]
        public void ResetRun_prevents_a_bracket_being_detected_across_a_gap()
        {
            var learner = new LockAnchorLearner();
            double smax = 75.0;

            // u=0.70 at source=18, then the run breaks (a gap - not triggered/not engaged/no G signal).
            learner.Observe(Game, Car, Source, 0.70, 18.0, smax);
            learner.ResetRun(Game, Car, Source);

            // A LATER, unrelated run starts at u=0.80 - without the reset, this would look like a
            // (spurious) bracket crossing of 0.75 against the stale u=0.70/source=18 pair from before
            // the gap.
            learner.Observe(Game, Car, Source, 0.80, 50.0, smax);

            Assert.Equal(0, learner.AcceptedObservationCount);
            Assert.Null(learner.LearnedS75(Game, Car, Source));
        }

        [Fact]
        public void A_single_outlier_corner_barely_moves_an_established_anchor_but_repeated_corroboration_converges_it()
        {
            // DYNAMIC UPDATE (the owner's own impact-weighted rule: anchor += (observed-anchor)*impactRate,
            // impact growing with corroborating sample count). Establish S75 at ~20 across several
            // corners, then feed one wildly different corner (a one-off outlier) and confirm it barely
            // moves the anchor, then feed several corroborating corners near a NEW level and confirm the
            // anchor converges toward THAT new level instead.
            var learner = new LockAnchorLearner();
            double smax = 100.0;

            void Cross(double source75)
            {
                // A tight bracket straddling exactly u=0.75 whose linear interpolation lands at source75.
                learner.Observe(Game, Car, Source, 0.749, source75 - 0.05, smax);
                learner.Observe(Game, Car, Source, 0.751, source75 + 0.05, smax);
                learner.ResetRun(Game, Car, Source); // each call is its own "corner"
            }

            Cross(20.0);
            Cross(20.0);
            Cross(20.0);
            double established = learner.LearnedS75(Game, Car, Source).Value;
            Assert.True(established > 19.0 && established < 21.0, $"expected convergence near 20, got {established}");

            // ONE outlier corner (a single, uncorroborated one-off far from the established value).
            Cross(80.0);
            double afterOutlier = learner.LearnedS75(Game, Car, Source).Value;
            Assert.True(afterOutlier < established + 15.0,
                $"a single outlier corner must not swing the established anchor drastically, was {established} -> {afterOutlier}");

            // Several corroborating corners near a genuinely NEW level (55) should converge the anchor
            // toward it, not leave it stuck near the old value forever (non-stopping adaptation).
            Cross(55.0);
            Cross(55.0);
            Cross(55.0);
            Cross(55.0);
            double afterCorroboration = learner.LearnedS75(Game, Car, Source).Value;
            Assert.True(afterCorroboration > 45.0, $"repeated corroboration at 55 should pull the anchor up substantially, got {afterCorroboration}");
        }

        [Fact]
        public void ExportAll_and_ImportAll_round_trip_learned_anchors()
        {
            var learner = new LockAnchorLearner();
            double smax = 100.0;
            learner.Observe(Game, Car, Source, 0.70, 18.0, smax);
            learner.Observe(Game, Car, Source, 0.80, 22.0, smax);
            learner.Observe(Game, Car, Source, 0.85, 45.0, smax);
            learner.Observe(Game, Car, Source, 0.95, 55.0, smax);

            double s75 = learner.LearnedS75(Game, Car, Source).Value;
            double s90 = learner.LearnedS90(Game, Car, Source).Value;

            var exported = learner.ExportAll();
            Assert.NotEmpty(exported);

            var restored = new LockAnchorLearner();
            restored.ImportAll(exported);

            Assert.Equal(s75, restored.LearnedS75(Game, Car, Source).Value, 6);
            Assert.Equal(s90, restored.LearnedS90(Game, Car, Source).Value, 6);
        }

        [Fact]
        public void Reset_clears_every_learned_key_and_counters()
        {
            var learner = new LockAnchorLearner();
            learner.Observe(Game, Car, Source, 0.70, 18.0, 100.0);
            learner.Observe(Game, Car, Source, 0.80, 22.0, 100.0);
            Assert.NotNull(learner.LearnedS75(Game, Car, Source));

            learner.Reset();

            Assert.Null(learner.LearnedS75(Game, Car, Source));
            Assert.Equal(0, learner.AcceptedObservationCount);
            Assert.Equal(0, learner.RejectedByBranchFilterCount);
        }
    }
}
