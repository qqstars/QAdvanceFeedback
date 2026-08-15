using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceMaxLearner"/> - AUTO-mode learning with outlier (single-frame-spike)
    /// rejection, keyed per (gameId, carId). This is the mutation-(c)-sensitive group: accepting a
    /// candidate on the first above-max frame instead of requiring
    /// <see cref="GForceMaxLearner.ConfirmFrames"/> consecutive confirming frames is exactly the
    /// mistake the brief warns cost the sibling project its signal (a 19.9g spike becoming "the"
    /// reference).
    /// </summary>
    public class GForceMaxLearnerTests
    {
        [Fact]
        public void Sustained_readings_are_learned_after_the_confirmation_threshold()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.0);
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9); // not confirmed yet (1 frame)

            learner.Observe("Game", "Car", 1.0);
            Assert.Equal(1.0, learner.GetLearnedMax("Game", "Car"), 9); // confirmed (2 consecutive frames)
        }

        [Fact]
        public void A_single_isolated_spike_is_never_learned()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.0);
            learner.Observe("Game", "Car", 1.0); // learnedMax = 1.0

            // The exact cautionary scenario from the brief: one freak frame.
            learner.Observe("Game", "Car", 19.9);
            Assert.Equal(1.0, learner.GetLearnedMax("Game", "Car"), 9);

            // Back to normal driving afterwards - the spike must not linger as a pending candidate
            // that later gets accidentally confirmed by an unrelated normal reading.
            learner.Observe("Game", "Car", 1.0);
            learner.Observe("Game", "Car", 1.0);
            learner.Observe("Game", "Car", 1.0);
            Assert.Equal(1.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_sustained_spike_across_two_consecutive_frames_IS_learned()
        {
            // Distinguishes "single isolated spike rejected" from "the learner is just broken and
            // never learns anything above the seed" - two consecutive similar high readings represent
            // a genuinely sustained event (e.g. real hard braking lasting more than one frame).
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 19.9);
            learner.Observe("Game", "Car", 19.5); // within tolerance of 19.9 -> confirms
            Assert.True(learner.GetLearnedMax("Game", "Car") >= 19.5);
        }

        [Fact]
        public void Non_positive_and_NaN_magnitudes_are_ignored()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 0.0);
            learner.Observe("Game", "Car", -5.0);
            learner.Observe("Game", "Car", double.NaN);
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void Different_cars_in_the_same_game_learn_independent_maxima()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("RaceGame", "RoadCar", 0.6);
            learner.Observe("RaceGame", "RoadCar", 0.6);

            learner.Observe("RaceGame", "GT3Car", 1.4);
            learner.Observe("RaceGame", "GT3Car", 1.4);

            Assert.Equal(0.6, learner.GetLearnedMax("RaceGame", "RoadCar"), 9);
            Assert.Equal(1.4, learner.GetLearnedMax("RaceGame", "GT3Car"), 9);
        }

        [Fact]
        public void Same_car_id_in_different_games_learns_independent_maxima()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("GameA", "SharedCarId", 0.5);
            learner.Observe("GameA", "SharedCarId", 0.5);

            learner.Observe("GameB", "SharedCarId", 1.9);
            learner.Observe("GameB", "SharedCarId", 1.9);

            Assert.Equal(0.5, learner.GetLearnedMax("GameA", "SharedCarId"), 9);
            Assert.Equal(1.9, learner.GetLearnedMax("GameB", "SharedCarId"), 9);
        }

        [Fact]
        public void Key_concatenation_does_not_let_different_game_car_pairs_collide()
        {
            // "Foo"+"Bar1" must not collide with "FooBar"+"1" via naive string concatenation.
            var learner = new GForceMaxLearner();
            learner.Observe("Foo", "Bar1", 0.7);
            learner.Observe("Foo", "Bar1", 0.7);

            learner.Observe("FooBar", "1", 1.7);
            learner.Observe("FooBar", "1", 1.7);

            Assert.Equal(0.7, learner.GetLearnedMax("Foo", "Bar1"), 9);
            Assert.Equal(1.7, learner.GetLearnedMax("FooBar", "1"), 9);
        }

        [Fact]
        public void Reset_clears_all_learned_state()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.0);
            learner.Observe("Game", "Car", 1.0);
            Assert.Equal(1.0, learner.GetLearnedMax("Game", "Car"), 9);

            learner.Reset();
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_genuine_upward_trend_keeps_growing_frame_by_frame()
        {
            // Not just spike-rejection - the learner must still track a real, gradually-rising
            // maximum across a session (e.g. the driver's braking gets progressively harder as they
            // learn the track).
            var learner = new GForceMaxLearner();
            double[] sequence = { 0.8, 0.8, 0.9, 0.9, 1.0, 1.0, 1.1, 1.1 };
            foreach (double m in sequence) learner.Observe("Game", "Car", m);

            Assert.Equal(1.1, learner.GetLearnedMax("Game", "Car"), 9);
        }

        // ---------------------------------------------------------------------------------------
        // Export/Import - the wiring task's addition so AUTO-learned maxima survive a SimHub
        // restart via RuntimeStore, matching how the Lock/Slip GripLearner states already do.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Export_round_trips_a_confirmed_maximum_through_import()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.4);
            learner.Observe("Game", "Car", 1.4);

            var exported = learner.ExportLearnedMaxima();

            var restored = new GForceMaxLearner();
            restored.ImportLearnedMaxima(exported);

            Assert.Equal(1.4, restored.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void Export_never_includes_an_unconfirmed_pending_candidate()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.0);
            learner.Observe("Game", "Car", 1.0); // confirmed max = 1.0
            learner.Observe("Game", "Car", 19.9); // single-frame spike - only a pending candidate

            var exported = learner.ExportLearnedMaxima();

            Assert.Equal(1.0, exported[GForceMaxLearner.MakeKey("Game", "Car")], 9);
            Assert.DoesNotContain(19.9, exported.Values);
        }

        [Fact]
        public void Import_ignores_null_non_finite_and_non_positive_entries()
        {
            var learner = new GForceMaxLearner();
            var data = new System.Collections.Generic.Dictionary<string, double>
            {
                ["Game|#|Car"] = 1.5,
                ["Bad|#|NaN"] = double.NaN,
                ["Bad|#|Zero"] = 0.0,
                ["Bad|#|Negative"] = -2.0,
            };

            learner.ImportLearnedMaxima(data);

            Assert.Equal(1.5, learner.GetLearnedMax("Game", "Car"), 9);
            Assert.Equal(0.0, learner.GetLearnedMax("Bad", "NaN"), 9);
            Assert.Equal(0.0, learner.GetLearnedMax("Bad", "Zero"), 9);
            Assert.Equal(0.0, learner.GetLearnedMax("Bad", "Negative"), 9);
        }

        [Fact]
        public void Import_of_null_leaves_the_learner_exactly_as_freshly_constructed()
        {
            var learner = new GForceMaxLearner();
            learner.ImportLearnedMaxima(null); // must not throw
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }
    }
}
