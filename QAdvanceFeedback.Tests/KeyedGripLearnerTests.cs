using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// KeyedGripLearner.cs is the fix for the gap docs\layer45-report.md flagged explicitly: "the
    /// learned grip reference is a single global value per channel, not per-car/per-game ... switching
    /// cars mid-session shares one learned peak across both." These tests prove the isolation and
    /// restore behaviour the owner asked for directly, independently of RuntimeStore/persistence.
    /// </summary>
    public class KeyedGripLearnerTests
    {
        [Fact]
        public void Game_A_car_1_and_game_A_car_2_learn_independent_peaks()
        {
            var learner = new KeyedGripLearner();

            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car1", 4.0);
            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car2", 1.0);

            Assert.True(learner.LearnedPeakG("GameA", "Car1") > 3.0);
            Assert.True(learner.LearnedPeakG("GameA", "Car2") < 1.5);
        }

        [Fact]
        public void Game_A_car_1_and_game_B_car_1_learn_independent_peaks_despite_sharing_a_car_id()
        {
            var learner = new KeyedGripLearner();

            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car1", 4.0);
            for (int i = 0; i < 300; i++) learner.Observe("GameB", "Car1", 1.0);

            Assert.True(learner.LearnedPeakG("GameA", "Car1") > 3.0);
            Assert.True(learner.LearnedPeakG("GameB", "Car1") < 1.5);
        }

        [Fact]
        public void Switching_back_to_a_previously_seen_key_restores_its_own_learned_value()
        {
            var learner = new KeyedGripLearner();

            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car1", 4.0);
            double car1Peak = learner.LearnedPeakG("GameA", "Car1");

            // Switch to a different car (as a session-long game/car change would)...
            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car2", 1.0);
            Assert.True(learner.LearnedPeakG("GameA", "Car2") < 1.5);

            // ...and switching back must restore exactly what Car1 had already learned - not reset to
            // the generic seed, and not still holding Car2's value.
            Assert.Equal(car1Peak, learner.LearnedPeakG("GameA", "Car1"), 6);
        }

        [Fact]
        public void A_key_never_observed_reads_the_generic_seed_without_creating_an_entry()
        {
            var learner = new KeyedGripLearner();
            Assert.Equal(GripLearner.SeedPeakG, learner.LearnedPeakG("Unseen", "Car"), 6);
            Assert.Equal(0, learner.Samples("Unseen", "Car"));

            // A read-only query must not itself create/export an entry - only Observe/Ratio should.
            Assert.Empty(learner.ExportAll());
        }

        [Fact]
        public void ExportAll_then_ImportAll_round_trips_every_key_independently()
        {
            var learner = new KeyedGripLearner();
            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car1", 4.0);
            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car2", 1.0);

            var exported = learner.ExportAll();
            Assert.True(exported.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car1")));
            Assert.True(exported.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car2")));

            var restored = new KeyedGripLearner();
            restored.ImportAll(exported);

            Assert.Equal(learner.LearnedPeakG("GameA", "Car1"), restored.LearnedPeakG("GameA", "Car1"), 6);
            Assert.Equal(learner.LearnedPeakG("GameA", "Car2"), restored.LearnedPeakG("GameA", "Car2"), 6);
            Assert.Equal(learner.Samples("GameA", "Car1"), restored.Samples("GameA", "Car1"));
        }

        [Fact]
        public void SeedLegacy_seeds_only_a_brand_new_key_never_an_already_imported_one()
        {
            var learner = new KeyedGripLearner();
            learner.SeedLegacy(3.0, 250);

            // A brand-new key (never observed, never imported) picks up the legacy seed as its own
            // cold-start value instead of the generic 1.0 seed.
            Assert.Equal(3.0, learner.LearnedPeakG("NewGame", "NewCar"), 6);

            // A key that was already restored from a real per-car snapshot must NOT be overwritten by
            // the legacy seed.
            var withRealData = new KeyedGripLearner();
            withRealData.ImportAll(new System.Collections.Generic.Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = new GripLearnerState { PeakG = 1.2, Samples = 300 }
            });
            withRealData.SeedLegacy(3.0, 250);
            Assert.Equal(1.2, withRealData.LearnedPeakG("GameA", "Car1"), 6);
        }

        [Fact]
        public void Reset_clears_every_key()
        {
            var learner = new KeyedGripLearner();
            for (int i = 0; i < 300; i++) learner.Observe("GameA", "Car1", 4.0);

            learner.Reset();

            Assert.Equal(GripLearner.SeedPeakG, learner.LearnedPeakG("GameA", "Car1"), 6);
            Assert.Equal(0, learner.Samples("GameA", "Car1"));
        }

        // ---------------------------------------------------------------------------------------
        // Owner's "do not persist a learned parameter until it has a minimum sample count/confidence"
        // ask (docs\gforce-direction-fix-report.md) - a short, low-sample session must not seed a
        // persisted entry for a brand-new key at all.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ExportAll_excludes_a_key_below_MinPersistSamples()
        {
            var learner = new KeyedGripLearner();
            for (int i = 0; i < GripLearner.MinPersistSamples - 1; i++)
                learner.Observe("GameA", "Car1", 3.0);

            Assert.DoesNotContain(KeyedGripLearner.MakeKey("GameA", "Car1"), learner.ExportAll().Keys);
        }

        [Fact]
        public void ExportAll_includes_a_key_once_it_reaches_MinPersistSamples()
        {
            var learner = new KeyedGripLearner();
            for (int i = 0; i < GripLearner.MinPersistSamples; i++)
                learner.Observe("GameA", "Car1", 3.0);

            Assert.Contains(KeyedGripLearner.MakeKey("GameA", "Car1"), learner.ExportAll().Keys);
        }

        [Fact]
        public void A_key_that_was_already_mature_before_a_short_low_quality_session_is_still_exported()
        {
            // Simulates "a five-second session in a menu cannot overwrite a good profile that took a
            // full stint to build": a key restored from a previous, mature persisted snapshot (well
            // above MinPersistSamples) must still be exported even if THIS session barely touches it.
            var learner = new KeyedGripLearner();
            learner.ImportAll(new Dictionary<string, GripLearnerState>
            {
                [KeyedGripLearner.MakeKey("GameA", "Car1")] = new GripLearnerState { PeakG = 3.5, Samples = 5000 }
            });

            var exported = learner.ExportAll();

            Assert.True(exported.ContainsKey(KeyedGripLearner.MakeKey("GameA", "Car1")));
            Assert.Equal(3.5, exported[KeyedGripLearner.MakeKey("GameA", "Car1")].PeakG, 6);
        }

        // ---------------------------------------------------------------------------------------
        // Owner's asymmetric-cap ask - a channel-specific learn cap is passed through to every
        // GripLearner this instance creates (fresh, legacy-seeded, and restored alike).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_custom_learn_cap_is_used_for_every_freshly_created_learner()
        {
            var learner = new KeyedGripLearner(learnCapG: 6.0);

            learner.Observe("GameA", "Car1", 7.0); // above the 6g cap - must be rejected
            Assert.Equal(0, learner.Samples("GameA", "Car1"));

            learner.Observe("GameA", "Car1", 5.5); // below the 6g cap - must be accepted
            Assert.Equal(1, learner.Samples("GameA", "Car1"));
        }
    }
}
