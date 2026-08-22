using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="GForceMaxLearner"/> - AUTO-mode learning via the shared
    /// <see cref="QAdvanceFeedback.Core.RobustBandEstimator"/> (docs\robust-auto-gforce-report.md),
    /// keyed per (gameId, carId). NO MINIMUM-SAMPLE GATE anywhere (owner's hard constraint) - a single
    /// observation already produces a usable value; the pool-selection algorithm inside
    /// <see cref="RobustBandEstimator"/> is what stays robust across the whole range of n instead.
    /// </summary>
    public class GForceMaxLearnerTests
    {
        private static DateTime T0 => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static void ObserveMany(GForceMaxLearner learner, string game, string car, double value, int count, DateTime start, double stepMs = 5.0)
        {
            for (int i = 0; i < count; i++) learner.Observe(game, car, value, start.AddMilliseconds(i * stepMs));
        }

        [Fact]
        public void A_single_observation_immediately_produces_a_usable_value_no_gate()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 5.0, T0);
            Assert.Equal(5.0, learner.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void Nothing_observed_yet_reports_zero()
        {
            var learner = new GForceMaxLearner();
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_small_cluster_of_high_but_plausible_outliers_does_not_become_the_learned_maximum()
        {
            // The exact cautionary scenario the brief describes: clipping an NPC car's rear bumper (or
            // braking down a slope) produces a real, plausibility-cap-passing but non-representative
            // high reading. It must not dominate the learned reference the way a blind maximum would.
            var learner = new GForceMaxLearner();
            DateTime t = T0;
            int i = 0;
            for (; i < 6; i++) learner.Observe("Game", "Car", 8.0, t.AddMilliseconds(i * 5));
            for (; i < 200; i++) learner.Observe("Game", "Car", 5.0, t.AddMilliseconds(i * 5));

            double learned = learner.GetLearnedMax("Game", "Car");
            Assert.True(learned < 6.0, $"learned {learned} must stay close to the representative cluster");
            Assert.True(learned >= 4.5, $"learned {learned} must still be representative of the real driving");
        }

        [Fact]
        public void MUTATION_a_a_blind_maximum_would_report_the_outlier_but_this_learner_does_not()
        {
            var learner = new GForceMaxLearner();
            DateTime t = T0;
            int i = 0;
            for (; i < 6; i++) learner.Observe("Game", "Car", 8.0, t.AddMilliseconds(i * 5));
            for (; i < 200; i++) learner.Observe("Game", "Car", 5.0, t.AddMilliseconds(i * 5));

            Assert.NotEqual(8.0, learner.GetLearnedMax("Game", "Car"), 3);
        }

        [Fact]
        public void A_genuine_upward_trend_is_still_reflected_once_it_dominates_the_window()
        {
            var learner = new GForceMaxLearner();
            DateTime t = T0;
            for (int i = 0; i < 50; i++) learner.Observe("Game", "Car", 0.8 + i * 0.01, t.AddMilliseconds(i * 5));

            double learned = learner.GetLearnedMax("Game", "Car");
            Assert.True(learned > 0.8, "a real, gradually-rising set of readings should be reflected in the pool, not stuck at the earliest value");
        }

        // ---------------------------------------------------------------------------------------
        // MaxPlausibleG hard ceiling - a magnitude above this is rejected OUTRIGHT before it ever
        // reaches the estimator, regardless of how many times it repeats.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void An_implausible_spike_never_reaches_the_estimator_even_when_repeated_many_times()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 1.0, T0);
            double before = learner.GetLearnedMax("Game", "Car");

            DateTime t = T0.AddSeconds(1);
            for (int i = 0; i < 30; i++) learner.Observe("Game", "Car", 19.9, t.AddMilliseconds(i * 5));

            Assert.Equal(before, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_value_exactly_at_MaxPlausibleG_is_still_learnable()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", GForceMaxLearner.MaxPlausibleG, T0);
            Assert.Equal(GForceMaxLearner.MaxPlausibleG, learner.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void Non_positive_and_NaN_magnitudes_are_ignored()
        {
            var learner = new GForceMaxLearner();
            learner.Observe("Game", "Car", 0.0, T0);
            learner.Observe("Game", "Car", -5.0, T0.AddMilliseconds(5));
            learner.Observe("Game", "Car", double.NaN, T0.AddMilliseconds(10));
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void Different_cars_in_the_same_game_learn_independent_maxima()
        {
            var learner = new GForceMaxLearner();
            ObserveMany(learner, "RaceGame", "RoadCar", 0.6, 10, T0);
            ObserveMany(learner, "RaceGame", "GT3Car", 1.4, 10, T0);

            Assert.Equal(0.6, learner.GetLearnedMax("RaceGame", "RoadCar"), 2);
            Assert.Equal(1.4, learner.GetLearnedMax("RaceGame", "GT3Car"), 2);
        }

        [Fact]
        public void Same_car_id_in_different_games_learns_independent_maxima()
        {
            var learner = new GForceMaxLearner();
            ObserveMany(learner, "GameA", "SharedCarId", 0.5, 10, T0);
            ObserveMany(learner, "GameB", "SharedCarId", 1.9, 10, T0);

            Assert.Equal(0.5, learner.GetLearnedMax("GameA", "SharedCarId"), 2);
            Assert.Equal(1.9, learner.GetLearnedMax("GameB", "SharedCarId"), 2);
        }

        [Fact]
        public void Key_concatenation_does_not_let_different_game_car_pairs_collide()
        {
            var learner = new GForceMaxLearner();
            ObserveMany(learner, "Foo", "Bar1", 0.7, 10, T0);
            ObserveMany(learner, "FooBar", "1", 1.7, 10, T0);

            Assert.Equal(0.7, learner.GetLearnedMax("Foo", "Bar1"), 2);
            Assert.Equal(1.7, learner.GetLearnedMax("FooBar", "1"), 2);
        }

        [Fact]
        public void Reset_clears_all_learned_state()
        {
            var learner = new GForceMaxLearner();
            ObserveMany(learner, "Game", "Car", 1.0, 10, T0);
            Assert.True(learner.GetLearnedMax("Game", "Car") > 0.0);

            learner.Reset();
            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        // ---------------------------------------------------------------------------------------
        // Sliding time window (2 minutes in production) - ageing by TIMESTAMP, not by sample count.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void MUTATION_c_samples_age_out_of_the_window_by_timestamp_not_by_count()
        {
            var learner = new GForceMaxLearner(window: TimeSpan.FromMinutes(2));
            ObserveMany(learner, "Game", "Car", 5.0, 50, T0, stepMs: 1000.0); // spans 0..49s
            Assert.True(learner.GetLearnedMax("Game", "Car") > 0.0);

            // Advance well past 2 minutes since the LAST of those 50 samples - all must age out. One
            // more, single fresh sample is enough to still answer (no gate) - it just reflects only
            // itself now.
            DateTime farLater = T0.AddSeconds(49).AddMinutes(2).AddSeconds(1);
            learner.Observe("Game", "Car", 2.0, farLater);
            Assert.Equal(2.0, learner.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void A_surface_condition_change_is_tracked_within_roughly_one_window()
        {
            var learner = new GForceMaxLearner(window: TimeSpan.FromMinutes(2));
            ObserveMany(learner, "Game", "Car", 8.0, 50, T0, stepMs: 1000.0); // old, high-grip condition

            DateTime t1 = T0.AddSeconds(49).AddMinutes(2).AddSeconds(1);
            ObserveMany(learner, "Game", "Car", 3.0, 50, t1, stepMs: 1000.0); // new, low-grip condition

            double learned = learner.GetLearnedMax("Game", "Car");
            Assert.True(learned < 4.0, $"learned {learned} should reflect only the new condition after roughly one window");
        }

        [Fact]
        public void Production_default_window_is_2_minutes()
        {
            var learner = new GForceMaxLearner();
            Assert.Equal(TimeSpan.FromMinutes(2), learner.Window);
        }

        // ---------------------------------------------------------------------------------------
        // Export/Import - the wiring task's addition so AUTO-learned references survive a SimHub
        // restart via RuntimeStore, and so switching cars mid-session immediately picks up that car's
        // own previously-learned value (the owner's explicit requirement).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Export_round_trips_a_learned_value_through_import()
        {
            var learner = new GForceMaxLearner();
            ObserveMany(learner, "Game", "Car", 1.4, 20, T0);

            var exported = learner.ExportLearnedMaxima();

            var restored = new GForceMaxLearner();
            restored.ImportLearnedMaxima(exported);

            Assert.Equal(1.4, restored.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void An_imported_value_is_used_immediately_before_any_new_evidence_this_session()
        {
            // The owner's explicit requirement: switching cars mid-session (or restarting) immediately
            // reflects that car's own previously-learned value, rather than waiting for fresh evidence.
            var restored = new GForceMaxLearner();
            restored.ImportLearnedMaxima(new Dictionary<string, double> { [GForceMaxLearner.MakeKey("Game", "Car")] = 2.2 });

            Assert.Equal(2.2, restored.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_live_estimate_supersedes_an_older_imported_seed()
        {
            var learner = new GForceMaxLearner();
            learner.ImportLearnedMaxima(new Dictionary<string, double> { [GForceMaxLearner.MakeKey("Game", "Car")] = 2.2 });
            Assert.Equal(2.2, learner.GetLearnedMax("Game", "Car"), 9);

            ObserveMany(learner, "Game", "Car", 1.5, 20, T0);
            Assert.Equal(1.5, learner.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void Import_ignores_null_non_finite_and_non_positive_entries()
        {
            var learner = new GForceMaxLearner();
            var data = new Dictionary<string, double>
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

        // ---------------------------------------------------------------------------------------
        // Owner's asymmetric-cap/plausibility-limit ask (docs\gforce-direction-fix-report.md) -
        // LearnCapG is per-instance and constructor-overridable; GForceSettings constructs its
        // accel/decel instances with tighter, ASYMMETRIC values (see GForceSettingsTests for that
        // integration) rather than the shared MaxPlausibleG default.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_plain_constructor_defaults_LearnCapG_to_the_shared_MaxPlausibleG()
        {
            var learner = new GForceMaxLearner();
            Assert.Equal(GForceMaxLearner.MaxPlausibleG, learner.LearnCapG, 6);
        }

        [Fact]
        public void A_custom_learn_cap_rejects_above_its_own_ceiling_even_though_it_is_below_the_shared_default()
        {
            var learner = new GForceMaxLearner(learnCapG: 6.0);
            learner.Observe("Game", "Car", 7.0, T0);

            Assert.Equal(0.0, learner.GetLearnedMax("Game", "Car"), 9);
        }

        [Fact]
        public void A_custom_learn_cap_still_accepts_a_magnitude_at_or_below_its_own_ceiling()
        {
            var learner = new GForceMaxLearner(learnCapG: 6.0);
            learner.Observe("Game", "Car", 5.5, T0);

            Assert.Equal(5.5, learner.GetLearnedMax("Game", "Car"), 2);
        }

        [Fact]
        public void A_non_positive_or_non_finite_custom_learn_cap_falls_back_to_the_default()
        {
            Assert.Equal(GForceMaxLearner.MaxPlausibleG, new GForceMaxLearner(learnCapG: 0.0).LearnCapG, 6);
            Assert.Equal(GForceMaxLearner.MaxPlausibleG, new GForceMaxLearner(learnCapG: -1.0).LearnCapG, 6);
            Assert.Equal(GForceMaxLearner.MaxPlausibleG, new GForceMaxLearner(learnCapG: double.NaN).LearnCapG, 6);
        }
    }
}
