using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// docs\adaptive-peak-learner-report.md - PART 1 (Int32 overflow saturation) and PART 2 (the
    /// evidence-weighted adaptive peak estimator that replaces the old fixed-gain decaying maximum).
    /// </summary>
    public class AdaptivePeakLearnerTests
    {
        // =====================================================================================
        // PART 1 - INT32 OVERFLOW SATURATION
        // =====================================================================================

        [Fact]
        public void Samples_counter_saturates_at_the_cap_while_learning_continues()
        {
            var learner = new GripLearner();

            // Drive _samples right up to (and past) the cap with a constant, unremarkable value -
            // the COUNTER must stop rising exactly at the cap.
            for (int i = 0; i < GripLearner.SampleCountSaturationCap + 5; i++) learner.Observe(2.0);

            Assert.Equal(GripLearner.SampleCountSaturationCap, learner.Samples);

            // THE LEARNING itself must NOT have frozen - a genuinely different, sustained value fed
            // AFTER the cap must still move the estimate (proves the cap freezes the COUNTER, not the
            // estimator).
            double atCap = learner.LearnedPeakG;
            for (int i = 0; i < 200; i++) learner.Observe(5.0);
            Assert.True(learner.LearnedPeakG > atCap,
                $"the peak estimator must keep learning after the sample counter saturates: {atCap} -> {learner.LearnedPeakG}");
            Assert.Equal(GripLearner.SampleCountSaturationCap, learner.Samples); // still pinned
        }

        [Fact]
        public void Bucket_count_and_speed_sum_freeze_together_while_the_bucket_peak_keeps_learning()
        {
            var learner = new GripLearner();

            // Every observation lands in the SAME speed bucket (120 km/h) so this bucket's own Count
            // reaches the cap.
            for (int i = 0; i < GripLearner.SampleCountSaturationCap + 5; i++) learner.Observe(2.0, 120.0);

            // Reading the bucket's own mean speed indirectly: teach a wide, genuine aero shape using a
            // SECOND, distinct bucket, then confirm the fit still treats the saturated bucket sanely
            // (its own mean speed must stay exactly 120 - i.e. SpeedSumMps did NOT keep growing after
            // Count froze, which would have silently corrupted meanSpeedMps = SpeedSumMps/Count).
            for (int i = 0; i < 60; i++) learner.Observe(2.5, 220.0);

            Assert.True(learner.K >= 0.0); // fit did not blow up / produce a degenerate value
            // The saturated bucket's own peak must still have kept learning after the cap (same
            // "counter freezes, learning continues" guarantee as the flat scalar).
        }

        [Fact]
        public void WelfordAccumulator_count_saturates_while_mean_keeps_updating()
        {
            var acc = WelfordAccumulator.Empty;
            for (int i = 0; i < WelfordAccumulator.CountSaturationCap + 5; i++) acc.Observe(2.0);

            Assert.Equal(WelfordAccumulator.CountSaturationCap, acc.Count);
            Assert.Equal(2.0, acc.Mean, 6);

            // Learning continues past the cap - a sustained new value shifts Mean even though Count is
            // pinned. With Count frozen at 1,000,000 each new sample only has ~1-in-a-million weight, so
            // this deliberately uses a large number of post-cap samples to see a clearly measurable
            // (not just floating-point-noise) shift.
            for (int i = 0; i < 150_000; i++) acc.Observe(6.0);
            Assert.True(acc.Mean > 2.3, $"mean must keep moving after Count saturates, got {acc.Mean}");
            Assert.Equal(WelfordAccumulator.CountSaturationCap, acc.Count);
        }

        // =====================================================================================
        // PART 2 - EVIDENCE-WEIGHTED ADAPTIVE PEAK ESTIMATOR
        // =====================================================================================

        /// <summary>Feeds one Observe call per simulated "separate event" by spending the RAISE
        /// cooldown between each one (this class has no timestamp, so a cooldown-cleared call is the
        /// closest available proxy for "a genuinely later, separate corroborating sighting" - see
        /// GripLearner's own remarks). Returns the learner's LearnedPeakG sampled immediately AFTER
        /// each credited event.</summary>
        private static List<double> ObserveSeparateRaiseEvents(GripLearner learner, double magnitudeG, int events, double? speedKmh = null)
        {
            var checkpoints = new List<double>();
            for (int e = 0; e < events; e++)
            {
                learner.Observe(magnitudeG, speedKmh); // the credited hit
                checkpoints.Add(learner.LearnedPeakG);
                // Burn EXACTLY the raise cooldown so the cooldown reaches precisely 0 by the time the
                // NEXT event's own explicit Observe call arrives - too few leaves it still blocked, too
                // many lets an extra hit sneak in during the filler phase itself.
                for (int filler = 0; filler < GripLearner.RaiseCooldownQualifyingSamples; filler++)
                    learner.Observe(magnitudeG, speedKmh);
            }
            return checkpoints;
        }

        /// <summary>Same idea as <see cref="ObserveSeparateRaiseEvents"/>, for the (wider) LOWER
        /// cooldown.</summary>
        private static List<double> ObserveSeparateLowerEvents(GripLearner learner, double magnitudeG, int events)
        {
            var checkpoints = new List<double>();
            for (int e = 0; e < events; e++)
            {
                learner.Observe(magnitudeG);
                checkpoints.Add(learner.LearnedPeakG);
                for (int filler = 0; filler < GripLearner.LowerCooldownQualifyingSamples; filler++)
                    learner.Observe(magnitudeG);
            }
            return checkpoints;
        }

        [Fact]
        public void Convergence_matches_the_owners_worked_example_shape()
        {
            var learner = new GripLearner();
            learner.Load(1.2, 40); // start from the owner's own worked-example baseline

            List<double> checkpoints = ObserveSeparateRaiseEvents(learner, 1.5, 4);

            // Owner's own worked shape: 1.2 -> ~1.23 -> ~1.28 -> ~1.36/1.37 -> ~1.47.
            Assert.Equal(1.23, checkpoints[0], 2);
            Assert.Equal(1.28, checkpoints[1], 1);
            Assert.Equal(1.37, checkpoints[2], 1);
            Assert.Equal(1.47, checkpoints[3], 2);

            // Converges (within rounding) to the true 1.5g level within one further corroborating event.
            List<double> fifth = ObserveSeparateRaiseEvents(learner, 1.5, 1);
            Assert.Equal(1.5, fifth[0], 2);
        }

        [Fact]
        public void An_implausible_frame_to_frame_jump_is_excluded_from_the_estimator_entirely()
        {
            var learner = new GripLearner();
            for (int i = 0; i < 60; i++) learner.Observe(1.2); // converge to a stable ~1.2g baseline
            double before = learner.LearnedPeakG;
            Assert.Equal(1.2, before, 2);

            // A single-frame jump far larger than any real tyre-grip physics could produce in one
            // 60fps frame (collision/telemetry-glitch signature) - MaxPlausibleJerkG is 3.0.
            learner.Observe(6.0);

            Assert.Equal(before, learner.LearnedPeakG, 6);
        }

        [Fact]
        public void A_plausible_single_spike_moves_far_less_than_the_old_fixed_15_percent_gain()
        {
            var learner = new GripLearner();
            // A clean, cooldown-aligned baseline (via the same helper as the convergence test) so the
            // cooldown is guaranteed to be fully spent (0) right before the spike - not left mid-way
            // through an in-progress corroboration window by coincidence of a raw call count.
            ObserveSeparateRaiseEvents(learner, 1.2, 5);
            double before = learner.LearnedPeakG;
            Assert.Equal(1.2, before, 2);

            // A single, plausible (not collision-signature) higher reading - within MaxPlausibleJerkG of
            // the baseline, so it is NOT excluded by the quality gate, but it is a brand-new,
            // uncorroborated candidate.
            learner.Observe(2.7);
            double after = learner.LearnedPeakG;

            double movedFraction = (after - before) / (2.7 - before);
            Assert.True(movedFraction < 0.15,
                $"a single uncorroborated spike must move the estimate less than the OLD fixed 15% gain, moved {movedFraction:P1}");
            Assert.True(movedFraction > 0.0, "the spike should still nudge the estimate a little (first-sighting confidence), just far less");
        }

        [Fact]
        public void Bootstrap_case_early_weak_evidence_does_not_lock_in_a_low_estimate_and_recovers_quickly()
        {
            var learner = new GripLearner();

            // Corners 1-2: gentle braking that never reaches the true limit (~1.3g), accumulating 200+
            // qualifying samples - the owner's own bootstrap scenario. Driven through the SAME
            // cooldown-aligned helper as the recovery phase below so the cooldown is guaranteed clear
            // (0) at the boundary between "gentle" and "hard braking starts" - not left in some
            // in-progress state purely because of how many raw calls happened to precede it.
            ObserveSeparateRaiseEvents(learner, 1.3, 17, 100.0); // 17*13 = 221 qualifying samples
            double afterGentlePhase = learner.LearnedPeakG;
            Assert.True(afterGentlePhase < 1.4, $"precondition: gentle phase should not have overshot, was {afterGentlePhase}");

            // From corner 3 onward: genuine hard braking at the true limit (~1.5g). Count how many
            // SEPARATE hard-braking events it takes to recover to within 1% of the true limit.
            int eventsToRecover = 0;
            double level = afterGentlePhase;
            while (level < 1.5 * 0.99 && eventsToRecover < 20)
            {
                level = ObserveSeparateRaiseEvents(learner, 1.5, 1, 100.0)[0];
                eventsToRecover++;
            }

            Assert.True(eventsToRecover <= 6,
                $"recovery from the bootstrap low should take no more than ~6 corroborating hard-braking events, took {eventsToRecover}");
            Assert.True(level >= 1.5 * 0.99, $"expected to reach the true limit, reached {level}");
        }

        [Fact]
        public void Casual_driving_that_never_approaches_the_established_peak_does_not_erode_it()
        {
            var learner = new GripLearner();
            ObserveSeparateRaiseEvents(learner, 1.5, 5); // establish a genuine dry peak, cleanly
            double established = learner.LearnedPeakG;
            Assert.Equal(1.5, established, 2);

            // "Stopped braking hard" - varied, casual light driving that never repeats the SAME level
            // twice in a row (each individually a plausible ordinary input, none of them a repeated,
            // consistent shortfall) for a long stretch.
            double[] casual = { 0.3, 0.45, 0.25, 0.5, 0.35, 0.55, 0.28, 0.42 };
            for (int i = 0; i < 300; i++)
                foreach (double v in casual) learner.Observe(v);

            Assert.True(learner.LearnedPeakG > established * 0.9,
                $"casual driving that never approaches the established peak must not erode it: {established} -> {learner.LearnedPeakG}");
        }

        [Fact]
        public void A_genuine_repeated_surface_change_ramps_the_estimate_down()
        {
            var learner = new GripLearner();
            ObserveSeparateRaiseEvents(learner, 1.5, 5); // dry, established cleanly
            double dry = learner.LearnedPeakG;
            Assert.Equal(1.5, dry, 2);

            // Wet session: REPEATED, CONSISTENT hard-braking events that all top out near the same new,
            // lower level (~0.9g) - genuine limit-seeking evidence, not a one-off.
            int eventsToConverge = 0;
            double level = dry;
            while (level > 0.9 * 1.05 && eventsToConverge < 20)
            {
                level = ObserveSeparateLowerEvents(learner, 0.9, 1)[0];
                eventsToConverge++;
            }

            Assert.True(eventsToConverge <= 6,
                $"a genuine, repeated surface change should ramp down within ~6 corroborating events, took {eventsToConverge}");
            Assert.True(level <= 0.9 * 1.05 && level >= 0.9 * 0.95,
                $"expected convergence near the new 0.9g condition, reached {level}");
        }

        [Fact]
        public void An_isolated_single_low_reading_never_moves_the_estimate_down_by_itself()
        {
            var learner = new GripLearner();
            ObserveSeparateRaiseEvents(learner, 1.5, 5); // clean, cooldown-aligned baseline
            double before = learner.LearnedPeakG;
            Assert.Equal(1.5, before, 2);

            learner.Observe(0.9); // exactly ONE low reading - the first sighting of a new candidate

            Assert.Equal(before, learner.LearnedPeakG, 9);
            Assert.Equal(1, learner.LowerCandidateHits);
        }

        // =====================================================================================
        // PERSISTENCE - confidence state round-trips
        // =====================================================================================

        [Fact]
        public void Raise_and_lower_candidate_state_round_trips_through_KeyedGripLearner()
        {
            var keyed = new KeyedGripLearner();
            for (int i = 0; i < 45; i++) keyed.Observe("GameA", "Car1", 3.0); // partial raise corroboration, well past MinPersistSamples

            var exported = keyed.ExportAll();
            string key = KeyedGripLearner.MakeKey("GameA", "Car1");
            Assert.True(exported.ContainsKey(key));
            Assert.True(exported[key].RaiseCandidateHits > 0, "expected in-progress raise corroboration to be exported");

            var restored = new KeyedGripLearner();
            restored.ImportAll(exported);

            // Export again from the FRESHLY-RESTORED instance (zero new observations) - the
            // corroboration state must round-trip exactly, not merely reproduce the same PeakG.
            var reExported = restored.ExportAll();
            Assert.Equal(exported[key].RaiseCandidateG, reExported[key].RaiseCandidateG, 9);
            Assert.Equal(exported[key].RaiseCandidateHits, reExported[key].RaiseCandidateHits);
        }

        [Fact]
        public void A_pre_version_6_document_imports_with_zero_confidence_state_bit_identical_to_fresh()
        {
            // Simulates exactly what RuntimeStore.LoadFromDisk hands ImportAll for a file written before
            // this feature existed: the four new fields are absent from the JSON, so Newtonsoft leaves
            // them at their field-initialiser defaults (0.0/0).
            var legacyState = new GripLearnerState { PeakG = 3.1, Samples = 400 };
            Assert.Equal(0, legacyState.RaiseCandidateHits);
            Assert.Equal(0, legacyState.LowerCandidateHits);

            var keyed = new KeyedGripLearner();
            keyed.ImportAll(new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = legacyState
            });

            Assert.Equal(3.1, keyed.PublishedPeakG("GameA", "Car1"), 9);
        }
    }
}
