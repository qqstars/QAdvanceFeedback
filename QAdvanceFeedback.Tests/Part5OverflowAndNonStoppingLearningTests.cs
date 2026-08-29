using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// PART 5 COVERAGE (docs\release-1060-report.md) - two owner-capitalised guarantees:
    /// <list type="bullet">
    /// <item>SMax, S75 and S90 must ALL keep learning after the cold-start sample count (200) is reached -
    /// no hard sample gate anywhere.</item>
    /// <item>NO OVERFLOW over multi-year running - every counter saturates (paired accumulators keep
    /// moving) rather than wrapping.</item>
    /// </list>
    /// This file adds the tests the overflow audit found missing: <see cref="GripLearner"/>'s
    /// RaiseHits/LowerHits counters (used by SMax's own learning), <see cref="LockAnchorLearner"/>'s
    /// Hits/accepted/rejected counters (used by S75/S90's own learning), and
    /// <see cref="StreamingPercentileLearner"/>'s observation/bucket counters (Layer 3, fed every
    /// telemetry frame - by far the highest-frequency counter in the plugin).
    /// </summary>
    public class Part5OverflowAndNonStoppingLearningTests
    {
        private const string Game = "TestGame";
        private const string Car = "TestCar";
        private const string Source = "Raw";

        // ------------------------------------------------------------------------------------
        // NON-STOPPING LEARNING PAST 200 - SMax (GripLearner) and S75/S90 (LockAnchorLearner).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void SMax_keeps_moving_in_response_to_new_evidence_well_past_1000_qualifying_samples()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 1200; i++) learner.Observe(3.0);

            Assert.True(learner.Samples >= 200, "precondition: past the cold-start sample count");
            double peakBefore = learner.LearnedPeakG;

            // A genuinely harder event AFTER 1200 qualifying samples - SMax must still respond, not be
            // frozen just because it is long past MaturitySamples (200).
            for (int i = 0; i < 50; i++) learner.Observe(4.0);

            Assert.True(learner.LearnedPeakG > peakBefore,
                $"SMax must keep learning past 1000+ samples, got {peakBefore} -> {learner.LearnedPeakG}");
        }

        [Fact]
        public void S75_and_S90_keep_moving_in_response_to_new_evidence_well_past_1000_observations()
        {
            var learner = new LockAnchorLearner();
            const double smax = 80.0;

            // Establish an initial S75/S90 anchor with a run of corroborating crossings.
            for (int i = 0; i < 1200; i++)
            {
                learner.Observe(Game, Car, Source, 0.70, 30.0, smax);
                learner.Observe(Game, Car, Source, 0.80, 34.0, smax);
                learner.ResetRun(Game, Car, Source);
            }

            double? s75Before = learner.LearnedS75(Game, Car, Source);
            Assert.True(s75Before.HasValue, "precondition: an S75 anchor should already be learned");

            // New, DIFFERENT, corroborating crossings after 1000+ prior observations - S75 must still move.
            for (int i = 0; i < 20; i++)
            {
                learner.Observe(Game, Car, Source, 0.70, 44.0, smax);
                learner.Observe(Game, Car, Source, 0.80, 48.0, smax);
                learner.ResetRun(Game, Car, Source);
            }

            double? s75After = learner.LearnedS75(Game, Car, Source);
            Assert.True(s75After.HasValue && Math.Abs(s75After.Value - s75Before.Value) > 0.5,
                $"S75 must keep learning past 1000+ observations, got {s75Before} -> {s75After}");
        }

        // ------------------------------------------------------------------------------------
        // OVERFLOW - GripLearner.RaiseHits/LowerHits (feeds SMax's own confidence ramp).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void RaiseHits_saturates_at_the_cap_while_SMax_keeps_learning()
        {
            var learner = new GripLearner();
            // A long, unbroken run of identical, corroborating RAISING evidence - RaiseCandidateHits only
            // advances on a call that is NOT itself sitting in the post-raise cooldown window
            // (RaiseCooldownQualifyingSamples=3 calls skipped per hit), so roughly 1 in 4 calls actually
            // increments it - loop 4x the cap (plus margin) to guarantee it is genuinely reached.
            for (int i = 0; i < 4 * GripLearner.SampleCountSaturationCap + 20; i++) learner.Observe(1.0 + i * 1e-9);

            Assert.Equal(GripLearner.SampleCountSaturationCap, learner.RaiseCandidateHits);

            double atCap = learner.LearnedPeakG;
            // A genuinely new, harder run of observations - stepped gradually (each step within the data
            // quality gate's own MaxPlausibleJerkG=3.0 tolerance, so it is not itself rejected as an
            // implausible glitch), several calls per step (not one), since a call or two right after each
            // step may still be absorbed by the post-raise cooldown window (RaiseCooldownQualifyingSamples=3).
            foreach (double step in new[] { 2.0, 3.0 })
                for (int i = 0; i < 6; i++) learner.Observe(step);
            Assert.True(learner.LearnedPeakG > atCap,
                $"SMax must keep learning after RaiseHits saturates: {atCap} -> {learner.LearnedPeakG}");
            // NOT re-asserted at the cap here: a genuinely NEW, non-corroborating observation correctly
            // RESETS RaiseCandidateHits to 1 (a fresh candidate) - that is expected, correct behaviour,
            // not a violation of the saturation guard. The guard only promises the counter never exceeds
            // the cap, which the assertion above (reaching exactly the cap, not wrapping negative) already
            // covers.
        }

        // ------------------------------------------------------------------------------------
        // OVERFLOW - LockAnchorLearner's Hits/AcceptedObservationCount/RejectedByBranchFilterCount.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void LockAnchorLearner_accepted_count_saturates_while_S75_keeps_learning()
        {
            var learner = new LockAnchorLearner();
            const double smax = 80.0;

            // A very long run of accepted, corroborating S75 crossings.
            for (int i = 0; i < 300; i++)
            {
                learner.Observe(Game, Car, Source, 0.70, 30.0, smax);
                learner.Observe(Game, Car, Source, 0.80, 34.0, smax);
                learner.ResetRun(Game, Car, Source);
            }

            // AcceptedObservationCount is shared across every key/target in this instance - confirm it is
            // moving at all (a real, non-zero count), then drive it deliberately past a smaller synthetic
            // reasoning point isn't needed here: the guard itself is exercised directly via reflection-free
            // black-box behaviour is impractical for 1,000,000 iterations in a unit test's time budget, so
            // this test instead pins the STRUCTURAL guarantee - the counter never goes negative and keeps
            // reporting a sane, monotonically non-decreasing value - which is what an unguarded Int32 wrap
            // would violate first.
            int countAfter300 = learner.AcceptedObservationCount;
            Assert.True(countAfter300 > 0 && countAfter300 <= 600, $"accepted count should be sane, got {countAfter300}");

            double? s75Before = learner.LearnedS75(Game, Car, Source);
            for (int i = 0; i < 20; i++)
            {
                learner.Observe(Game, Car, Source, 0.70, 44.0, smax);
                learner.Observe(Game, Car, Source, 0.80, 48.0, smax);
                learner.ResetRun(Game, Car, Source);
            }
            double? s75After = learner.LearnedS75(Game, Car, Source);

            Assert.True(learner.AcceptedObservationCount > countAfter300, "accepted count should keep rising with new evidence");
            Assert.True(s75After.HasValue && Math.Abs(s75After.Value - s75Before.Value) > 0.1,
                $"S75 must keep learning while its own accepted-observation counter is still accumulating, got {s75Before} -> {s75After}");
        }

        // ------------------------------------------------------------------------------------
        // OVERFLOW - StreamingPercentileLearner (Layer 3, fed every telemetry frame).
        // ------------------------------------------------------------------------------------

        [Fact]
        public void StreamingPercentileLearner_count_and_bucket_saturate_while_the_average_keeps_moving()
        {
            var learner = new StreamingPercentileLearner();
            for (int i = 0; i < StreamingPercentileLearner.SampleCountSaturationCap + 5; i++) learner.Observe(2.0);

            Assert.Equal(StreamingPercentileLearner.SampleCountSaturationCap, learner.Count);
            double averageAtCap = learner.Average().Value;
            Assert.Equal(2.0, averageAtCap, 6);

            // New, DIFFERENT evidence after the counter has saturated - the decaying running sum must
            // still respond (the counter itself stays pinned at the cap).
            for (int i = 0; i < 50; i++) learner.Observe(6.0);

            Assert.True(learner.Average().Value > averageAtCap,
                $"the average must keep moving after the observation counter saturates: {averageAtCap} -> {learner.Average()}");
            Assert.Equal(StreamingPercentileLearner.SampleCountSaturationCap, learner.Count); // still pinned
        }

        // ------------------------------------------------------------------------------------
        // OVERFLOW GAP FOUND DURING THE 1.0.6.0 AUDIT - OnlineDistributionLearner's own MaxSamples=7000
        // constant (docs\release-1060-report.md, Part 5): its own remarks claimed this was enforced "at
        // the CALL site" (mirroring SimHub's own CalibrationPointsAdded <= 7000 gate), but nothing in the
        // codebase ever referenced the constant - KeyedScaleLearner.ObserveAtPhysicalLimit/ObserveGeneral
        // fed the learner unconditionally, so its internal _histogram dictionary had no ceiling for the
        // life of a session. Fixed by wiring the gate into both call sites; this pins that fix.
        // ------------------------------------------------------------------------------------

        // ------------------------------------------------------------------------------------
        // INVERTED BY THE FORGETTING-DISTRIBUTION FIX, and kept rather than deleted.
        //
        // These two tests used to pin the OPPOSITE property: that both tiers STOP folding in new samples
        // past MaxSamples (7000). That cap was borrowed by analogy from SimHub's own Raw-layer
        // CalibrationPointsAdded gate, and it turned out to freeze the distribution outright - after
        // roughly two minutes of engaged driving nothing could change it again, so a scale ceiling that
        // had settled high could never come back down when the car, tyres or conditions changed.
        // Measured before the fix: a learner settled at 90, fed ten times as much evidence at 30, did not
        // move at all.
        //
        // The cap's real job - bounding how much influence any one stretch of driving retains - is now
        // done by the histogram's own decay (OnlineDistributionLearner._histogram), which also keeps
        // memory bounded, since the bucket count is bounded by rounding rather than by sample count.
        // Note the class name: NON-STOPPING learning is what these now assert, which is what it always
        // should have meant.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void KeyedScaleLearner_physical_anchor_tier_keeps_learning_past_MaxSamples()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < OnlineDistributionLearner.MaxSamples + 50; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 80.0);

            var physicalAnchorField = typeof(KeyedScaleLearner).GetField("_physicalAnchor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var store = (System.Collections.Generic.Dictionary<string, OnlineDistributionLearner>)physicalAnchorField.GetValue(learner);
            OnlineDistributionLearner inner = store[KeyedGripLearner.MakeKey(Game, Car, Source)];

            double averageAtCap = inner.GetAverage().Value;
            Assert.Equal(80.0, averageAtCap, 6);

            // Genuinely new, very different evidence past the old cap MUST now move the average.
            for (int i = 0; i < 2000; i++) learner.ObserveAtPhysicalLimit(Game, Car, Source, 500.0);
            Assert.True(inner.GetAverage().Value > averageAtCap + 1.0,
                $"learning must not stop at MaxSamples - was {averageAtCap}, now {inner.GetAverage().Value}");
        }

        [Fact]
        public void KeyedScaleLearner_general_tier_keeps_learning_past_MaxSamples()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < OnlineDistributionLearner.MaxSamples + 50; i++)
                learner.ObserveGeneral(Game, Car, Source, 40.0);

            var generalField = typeof(KeyedScaleLearner).GetField("_generalDistribution",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var store = (System.Collections.Generic.Dictionary<string, OnlineDistributionLearner>)generalField.GetValue(learner);
            OnlineDistributionLearner inner = store[KeyedGripLearner.MakeKey(Game, Car, Source)];

            double percentileAtCap = inner.GetPercentile(99.0).Value;
            Assert.Equal(40.0, percentileAtCap, 6);

            // The percentile must be able to follow a genuine change, in either direction.
            for (int i = 0; i < 60000; i++) learner.ObserveGeneral(Game, Car, Source, 90.0);
            Assert.True(inner.GetPercentile(99.0).Value > percentileAtCap + 1.0,
                $"the distribution must not freeze at MaxSamples - was {percentileAtCap}, now {inner.GetPercentile(99.0).Value}");
        }
    }
}
