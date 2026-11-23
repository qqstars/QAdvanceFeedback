using System;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Runtime;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// SMax EVIDENCE PERSISTENCE (owner, 2026-11-21; docs\learning-maturity-indicator-report.md): a restart -
    /// even after switching game, car or surface - continues each key's learning where it stopped instead of
    /// starting again, so the ceiling and its maturity (the indicator) come back as they were. Never an
    /// overflow; never a load / save cost that grows with driving time.
    /// </summary>
    public class ScaleEvidencePersistenceTests
    {
        private const string Game = "G";
        private const string Car = "C";
        private const string Source = "S";

        private static void Drive(KeyedScaleLearner learner, int samples, int seed, string car = Car, string surface = "")
        {
            var rng = new Random(seed);
            for (int i = 0; i < samples; i++)
            {
                double v = 70.0 + (rng.NextDouble() - 0.5) * 14.0;
                learner.ObserveAtPhysicalLimit(Game, car, Source, v, 1.0, surface);
                learner.ObserveGeneral(Game, car, Source, v * 0.8);
            }
        }

        /// <summary>A restart: a fresh learner fed what the runtime file holds (cold ceilings, then evidence).</summary>
        private static KeyedScaleLearner Restart(KeyedScaleLearner before)
        {
            var after = new KeyedScaleLearner();
            after.ImportAll(before.ExportAll());
            after.ImportEvidence(before.ExportEvidence());
            return after;
        }

        // ---------------- continuing, not restarting ----------------

        [Fact]
        public void A_restart_mid_learning_comes_back_with_the_same_ceiling_and_maturity()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 80, 1);   // part-way: not matured
            double ceiling = learner.PublishedCeiling(Game, Car, Source, out _).Value;
            double maturity = learner.CeilingHandoverConfidence(Game, Car, Source);
            Assert.InRange(maturity, 0.05, 0.95);

            KeyedScaleLearner restarted = Restart(learner);

            Assert.Equal(ceiling, restarted.PublishedCeiling(Game, Car, Source, out _).Value, 1);
            Assert.Equal(maturity, restarted.CeilingHandoverConfidence(Game, Car, Source), 6);
        }

        [Fact]
        public void Learning_after_a_restart_adds_to_the_saved_evidence_instead_of_starting_again()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 100, 2);
            KeyedScaleLearner restarted = Restart(learner);
            Drive(restarted, 100, 3);

            var fresh = new KeyedScaleLearner();
            Drive(fresh, 100, 3);   // the same new driving with nothing saved: the old behaviour

            Assert.Equal(1.0, restarted.CeilingHandoverConfidence(Game, Car, Source), 6);   // 200 samples in all
            Assert.True(fresh.CeilingHandoverConfidence(Game, Car, Source) < 1.0);
        }

        [Fact]
        public void Every_car_and_surface_continues_on_its_own_after_a_restart()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 60, 4, car: "C1");
            Drive(learner, 120, 5, car: "C2");
            Drive(learner, 40, 6, car: "C1", surface: "loose");
            Dictionary<string, ScaleEvidenceState> saved = learner.ExportEvidence();
            Assert.Contains(KeyedGripLearner.MakeKey(Game, "C1", Source, "loose"), saved.Keys);

            KeyedScaleLearner restarted = Restart(learner);

            Assert.Equal(learner.CeilingHandoverConfidence(Game, "C1", Source), restarted.CeilingHandoverConfidence(Game, "C1", Source), 6);
            Assert.Equal(learner.CeilingHandoverConfidence(Game, "C2", Source), restarted.CeilingHandoverConfidence(Game, "C2", Source), 6);
        }

        [Fact]
        public void A_file_without_evidence_starts_from_its_saved_ceiling_at_zero_maturity()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 200, 7);
            var restarted = new KeyedScaleLearner();
            restarted.ImportAll(learner.ExportAll());   // a v13 file: ceilings only

            Assert.Equal(0.0, restarted.CeilingHandoverConfidence(Game, Car, Source));
            Assert.NotNull(restarted.PublishedCeiling(Game, Car, Source, out _));
        }

        // ---------------- restart = continue ----------------

        [Fact]
        public void A_key_whose_evidence_was_restored_publishes_its_learning_without_the_cold_blend()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 120, 21);
            KeyedScaleLearner restarted = Restart(learner);
            Drive(restarted, 200, 22);   // new driving moves the learning

            double learned = restarted.LearnedCeiling(Game, Car, Source, out _).Value;
            Assert.Equal(learned, restarted.PublishedCeiling(Game, Car, Source, out _).Value, 9);
        }

        [Fact]
        public void A_key_from_an_older_file_without_evidence_keeps_the_cold_blend()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 200, 23);
            var restarted = new KeyedScaleLearner();
            restarted.ImportAll(learner.ExportAll());   // ceilings only
            var rng = new Random(24);
            for (int i = 0; i < 200; i++) restarted.ObserveAtPhysicalLimit(Game, Car, Source, 40.0 + rng.NextDouble() * 4.0);

            double learned = restarted.LearnedCeiling(Game, Car, Source, out _).Value;
            double published = restarted.PublishedCeiling(Game, Car, Source, out _).Value;
            Assert.True(published > learned + 1.0, $"published {published} should still lean on the saved ceiling, learned {learned}");
        }

        [Fact]
        public void Plugin_internal_slip_finds_its_lock_pairing_before_the_first_frame()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            string lockId = engine.LockInternalSourceIdentity, slipId = engine.SlipInternalSourceIdentity;
            var rng = new Random(25);
            for (int i = 0; i < 200; i++) engine.LockScaleLearner.ObserveAtPhysicalLimit(Game, Car, lockId, 70.0 + rng.NextDouble() * 4.0);

            Assert.True(engine.SlipScaleLearner.CrossChannelLockConfidence(Game, Car, slipId) > 0.0);   // no Compute has run
        }

        // ---------------- save cost ----------------

        [Fact]
        public void Saving_again_without_new_driving_reuses_every_snapshot()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 50, 8, car: "C1");
            Drive(learner, 50, 9, car: "C2");
            Dictionary<string, ScaleEvidenceState> first = learner.ExportEvidence();
            Drive(learner, 10, 10, car: "C1");
            Dictionary<string, ScaleEvidenceState> second = learner.ExportEvidence();

            string c1 = KeyedGripLearner.MakeKey(Game, "C1", Source, string.Empty);
            string c2 = KeyedGripLearner.MakeKey(Game, "C2", Source, string.Empty);
            Assert.NotSame(first[c1], second[c1]);   // driven: re-snapshotted
            Assert.Same(first[c2], second[c2]);      // untouched: the same object, no work
        }

        [Theory]
        [InlineData(50000, 0.0, 1000.0)]   // wide
        [InlineData(5000, 63.0, 77.0)]     // narrow
        [InlineData(25, 60.0, 80.0)]       // thin: just past the percentile bar
        public void The_summary_is_single_values_and_reads_back_every_saved_percentile_exactly(int samples, double lo, double hi)
        {
            var d = new OnlineDistributionLearner();
            var rng = new Random(11);
            for (int i = 0; i < samples; i++) d.AddValue(lo + rng.NextDouble() * (hi - lo));

            OnlineDistributionState state = d.ExportState();
            Assert.Null(state.Values);   // no bucket lists any more
            Assert.Null(state.Weights);
            string json = JsonConvert.SerializeObject(state);
            Assert.DoesNotContain("[", json);

            var back = new OnlineDistributionLearner();
            Assert.True(back.ImportState(state));
            foreach (double p in OnlineDistributionLearner.SummaryPercentiles)
                Assert.Equal(d.GetPercentile(p, 1).Value, back.GetPercentile(p, 1).Value, 3);
            Assert.InRange(back.PositiveSampleCount, d.PositiveSampleCount - 1, d.PositiveSampleCount + 1);
            Assert.Equal(d.Count, back.Count);
        }

        [Fact]
        public void A_restored_percentile_keeps_moving_with_new_samples_instead_of_sticking()
        {
            // Eight lumps alone pinned a restored P90 until new samples outweighed a whole lump.
            var d = new OnlineDistributionLearner();
            var rng = new Random(5);
            for (int i = 0; i < 400; i++) d.AddValue(60.0 + rng.NextDouble() * 10.0);
            var back = new OnlineDistributionLearner();
            Assert.True(back.ImportState(d.ExportState()));
            double before = back.GetPercentile(90.0, 1).Value;
            for (int i = 0; i < 40; i++) { back.AddValue(50.0); d.AddValue(50.0); }   // 10% more, all lower
            double after = back.GetPercentile(90.0, 1).Value;
            Assert.True(after < before, $"P90 {before} -> {after}");
            Assert.InRange(after, d.GetPercentile(90.0, 1).Value - 0.6, d.GetPercentile(90.0, 1).Value + 0.6);
        }

        [Fact]
        public void An_old_bucket_list_from_the_previous_version_still_loads()
        {
            var old = new OnlineDistributionState
            {
                Count = 300, DecayedWeightedSum = 70.0, DecayedWeight = 1.0,
                Values = new List<double> { 60.0, 70.0, 80.0 }, Weights = new List<double> { 100.0, 100.0, 100.0 },
            };
            var d = new OnlineDistributionLearner();
            Assert.True(d.ImportState(old));
            Assert.Equal(70.0, d.GetPercentile(50.0, 1).Value);
            Assert.Null(d.ExportState().Values);   // ...and is saved as the summary from then on
        }

        [Fact]
        public void Exporting_many_keys_stays_fast()
        {
            var learner = new KeyedScaleLearner();
            for (int c = 0; c < 40; c++) Drive(learner, 500, 100 + c, car: "Car" + c);
            learner.ExportEvidence();   // first full snapshot

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++)
            {
                Drive(learner, 1, 200 + i, car: "Car0");   // one key driven between saves, as in play
                learner.ExportEvidence();
            }
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2000, $"100 throttled saves took {sw.ElapsedMilliseconds} ms");
        }

        // ---------------- overflow and bad data ----------------

        [Fact]
        public void Saved_counts_beyond_int_are_clamped_on_load_never_wrapped()
        {
            var state = new OnlineDistributionState
            {
                Count = 5_000_000_000L,
                DecayedWeightedSum = 70.0,
                DecayedWeight = 1.0,
                Values = new List<double> { 70.0 },
                Weights = new List<double> { 20000.0 },
            };
            var d = new OnlineDistributionLearner();
            Assert.True(d.ImportState(state));
            Assert.Equal(OnlineDistributionLearner.SampleCountSaturationCap, d.Count);

            WelfordAccumulator w = WelfordAccumulator.FromState(5_000_000_000L, 70.0, 1000.0);
            Assert.Equal(WelfordAccumulator.CountSaturationCap, w.Count);
            Assert.Equal(70.0, w.Mean);
        }

        [Theory]
        [InlineData(new[] { 1.0, 2.0 }, new[] { 1.0 })]          // mismatched lists
        [InlineData(new[] { 1.0 }, new[] { -1.0 })]              // negative weight
        [InlineData(new[] { double.NaN }, new[] { 1.0 })]        // NaN value
        [InlineData(new[] { -5.0 }, new[] { 1.0 })]              // negative value
        public void A_malformed_distribution_is_rejected_and_leaves_the_learner_untouched(double[] values, double[] weights)
        {
            var d = new OnlineDistributionLearner();
            d.AddValue(42.0);
            bool ok = d.ImportState(new OnlineDistributionState
            {
                Count = 3, Values = new List<double>(values), Weights = new List<double>(weights),
            });
            Assert.False(ok);
            Assert.Equal(1, d.Count);
        }

        [Fact]
        public void An_oversized_distribution_is_rejected_without_loading_it()
        {
            var values = new List<double>();
            var weights = new List<double>();
            for (int i = 0; i <= OnlineDistributionLearner.MaxLoadedBuckets; i++) { values.Add(i); weights.Add(1.0); }
            Assert.False(new OnlineDistributionLearner().ImportState(new OnlineDistributionState { Values = values, Weights = weights }));
        }

        [Fact]
        public void A_bad_spread_loads_as_empty()
        {
            Assert.Equal(0, WelfordAccumulator.FromState(10, double.NaN, 1.0).Count);
            Assert.Equal(0, WelfordAccumulator.FromState(10, 1.0, -1.0).Count);
            Assert.Equal(0, WelfordAccumulator.FromState(-3, 1.0, 1.0).Count);
        }

        // ---------------- the runtime file ----------------

        [Fact]
        public void The_runtime_file_round_trips_the_evidence_at_version_14()
        {
            var learner = new KeyedScaleLearner();
            Drive(learner, 90, 13);
            var doc = new RuntimeDocument { LockScaleLearners = learner.ExportAll(), LockScaleEvidence = learner.ExportEvidence() };

            string json = JsonConvert.SerializeObject(doc);
            RuntimeDocument back = JsonConvert.DeserializeObject<RuntimeDocument>(json);

            Assert.Equal(16, back.Version);
            var restarted = new KeyedScaleLearner();
            restarted.ImportAll(back.LockScaleLearners);
            restarted.ImportEvidence(back.LockScaleEvidence);
            Assert.Equal(learner.CeilingHandoverConfidence(Game, Car, Source), restarted.CeilingHandoverConfidence(Game, Car, Source), 6);
            Assert.Empty(back.SlipScaleEvidence);
        }

        [Fact]
        public void An_older_runtime_file_has_no_evidence_sections()
        {
            RuntimeDocument back = JsonConvert.DeserializeObject<RuntimeDocument>("{\"Version\":13}");
            Assert.Empty(back.LockScaleEvidence);
            Assert.Empty(back.SlipScaleEvidence);
        }
    }
}
