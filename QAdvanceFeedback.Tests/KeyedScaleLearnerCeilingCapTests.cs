using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>The hard learned-SMax limit (owner, 2026-11-19): every ceiling the scale learner answers
    /// is at most <see cref="KeyedScaleLearner.MaxLearnedCeiling"/> (95), whatever it was fed or loaded.</summary>
    public class KeyedScaleLearnerCeilingCapTests
    {
        private const string Game = "fh6", Car = "car1", Source = "Raw";

        [Fact]
        public void The_limit_is_95()
        {
            Assert.Equal(95.0, KeyedScaleLearner.MaxLearnedCeiling);
        }

        [Fact]
        public void A_parameters_file_saved_at_100_heals_to_95_on_import()
        {
            var learner = new KeyedScaleLearner();
            string key = KeyedGripLearner.MakeKey(Game, Car, Source);
            learner.ImportAll(new Dictionary<string, ScaleLearnerState>
            {
                [key] = new ScaleLearnerState { ColdCeiling = 100.0, ColdIsPrimaryTier = true },
            });

            Assert.Equal(95.0, learner.PublishedCeiling(Game, Car, Source, out _).Value, 6);
            Assert.Equal(95.0, learner.PersistedCeiling(Game, Car, Source, out _).Value, 6);
            Assert.Equal(95.0, learner.ExportAll()[key].ColdCeiling, 6);
        }

        [Fact]
        public void LoadCold_clamps_too()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 100.0);
            Assert.Equal(95.0, learner.PublishedCeiling(Game, Car, Source, out _).Value, 6);
        }

        [Fact]
        public void Saturated_live_evidence_never_learns_above_95()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 400; i++)
            {
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 100.0);
                learner.ObserveGeneral(Game, Car, Source, 100.0);
            }

            Assert.True(learner.LearnedCeiling(Game, Car, Source, out _).Value <= 95.0);
            Assert.True(learner.PublishedCeiling(Game, Car, Source, out _).Value <= 95.0);
            Assert.True(learner.PersistedCeiling(Game, Car, Source, out _).Value <= 95.0);
            foreach (ScaleLearnerState s in learner.ExportAll().Values) Assert.True(s.ColdCeiling <= 95.0);
        }

        [Fact]
        public void Values_below_the_limit_are_untouched()
        {
            var learner = new KeyedScaleLearner();
            learner.LoadCold(Game, Car, Source, 78.9);
            Assert.Equal(78.9, learner.PublishedCeiling(Game, Car, Source, out _).Value, 9);
        }
    }
}
