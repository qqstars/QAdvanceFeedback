using System;
using QAdvanceFeedback.Settings;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for the FLOOR under every learned G-force maximum (v1.0.8) - 0.5 g on the acceleration,
    /// deceleration and lateral axes alike.
    /// <para/>
    /// A minimum-observation threshold was also tried on the lateral axis and REMOVED: sweeping it on a
    /// real log was monotonic with no knee, so it was a feel knob rather than a noise filter, and the
    /// floor covers the case that actually mattered. See <c>GForceSettings.ObserveLatG</c>'s remarks.
    /// </summary>
    public class GForceLateralLearningTests
    {
        private static GForceSettings Settings()
        {
            var s = new GForceSettings();
            s.SetCurrentGameAndCar("G", "C");
            return s;
        }

        /// <summary>MaxRamp starts on its FIRST call and weights in over 2 seconds, so a single call
        /// right after observing still returns the fixed default. Arm it, then read once it has
        /// settled.</summary>
        private static double Settled(Func<DateTime, double> effective, DateTime t)
        {
            effective(t);
            return effective(t.AddSeconds(5));
        }

        // ---------------- The floor, on each axis ----------------

        [Fact]
        public void A_session_with_almost_no_cornering_is_floored_rather_than_left_absurd()
        {
            // The pit-lap / straight-line-testing case. Without the floor this learns ~0.2g and the
            // cornering split then saturates on the slightest steering input.
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 400; i++)
                s.ObserveLatG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            Assert.True(s.GetLearnedLatMaxG("G", "C") < GForceSettings.MinLearnedLatMaxG,
                "precondition: the raw learned value should be below the floor");

            Assert.Equal(GForceSettings.MinLearnedLatMaxG,
                Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60)), 3);
        }

        [Fact]
        public void A_session_with_almost_no_braking_is_floored_too()
        {
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 400; i++)
                s.ObserveDecelG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            Assert.Equal(GForceSettings.MinLearnedDecelMaxG,
                Settled(x => s.EffectiveDecelMaxG("G", "C", x), t.AddSeconds(60)), 3);
        }

        [Fact]
        public void A_session_with_almost_no_acceleration_is_floored_too()
        {
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 400; i++)
                s.ObserveAccelG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            Assert.Equal(GForceSettings.MinLearnedAccelMaxG,
                Settled(x => s.EffectiveAccelMaxG("G", "C", x), t.AddSeconds(60)), 3);
        }

        [Fact]
        public void The_floor_never_holds_back_a_genuinely_higher_learned_value()
        {
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 2000; i++)
            {
                s.ObserveLatG("G", "C", 2.60, t.AddMilliseconds(i * 16));
                s.ObserveDecelG("G", "C", 3.40, t.AddMilliseconds(i * 16));
                s.ObserveAccelG("G", "C", 1.20, t.AddMilliseconds(i * 16));
            }

            DateTime at = t.AddSeconds(60);
            Assert.True(Settled(x => s.EffectiveLatMaxG("G", "C", x), at) > 2.0);
            Assert.True(Settled(x => s.EffectiveDecelMaxG("G", "C", x), at) > 2.5);
            Assert.True(Settled(x => s.EffectiveAccelMaxG("G", "C", x), at) > 1.0);
        }

        [Fact]
        public void The_floor_does_not_touch_a_hand_typed_fixed_value_on_any_axis()
        {
            // A driver who typed a fixed number meant it - even a deliberately tiny one.
            var s = new GForceSettings
            {
                LatMaxMode = GMaxMode.Fixed, FixedLatMaxG = 0.2,
                DecelMaxMode = GMaxMode.Fixed, FixedDecelMaxG = 0.2,
                AccelMaxMode = GMaxMode.Fixed, FixedAccelMaxG = 0.2,
            };

            Assert.Equal(0.2, s.EffectiveLatMaxG("G", "C"), 6);
            Assert.Equal(0.2, s.EffectiveDecelMaxG("G", "C"), 6);
            Assert.Equal(0.2, s.EffectiveAccelMaxG("G", "C"), 6);
        }

        [Fact]
        public void Auto_mode_with_no_evidence_yet_uses_the_typed_value_unfloored()
        {
            // THE SUBTLE PATH. In AUTO the floor lives inside the "has learned evidence" branch, so a key
            // with nothing learned yet falls back to Fixed*MaxG - and that fallback must NOT be floored
            // either. A driver who typed 0.2 and left the axis on Auto sees 0.2 until real evidence
            // arrives, not 0.5.
            var s = new GForceSettings
            {
                LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 0.2,
                DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 0.2,
                AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 0.2,
            };
            s.SetCurrentGameAndCar("G", "C");
            DateTime t = DateTime.UtcNow;

            Assert.Equal(0.2, Settled(x => s.EffectiveLatMaxG("G", "C", x), t), 6);
            Assert.Equal(0.2, Settled(x => s.EffectiveDecelMaxG("G", "C", x), t), 6);
            Assert.Equal(0.2, Settled(x => s.EffectiveAccelMaxG("G", "C", x), t), 6);
        }

        [Fact]
        public void Switching_an_axis_to_Fixed_drops_the_floor_immediately()
        {
            // Learn something below the floor first, so AUTO is actively flooring it, then switch to
            // Fixed: the typed value must win outright rather than the floor persisting.
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 400; i++)
                s.ObserveLatG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            Assert.Equal(GForceSettings.MinLearnedLatMaxG,
                Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60)), 3);

            s.LatMaxMode = GMaxMode.Fixed;
            s.FixedLatMaxG = 0.25;
            Assert.Equal(0.25, s.EffectiveLatMaxG("G", "C", t.AddSeconds(120)), 6);
        }

        [Fact]
        public void The_floor_is_never_written_back_into_the_typed_value_or_the_learner()
        {
            // The floor is applied at READ time only. Neither the setting the driver typed nor the
            // learner's own stored estimate may be mutated by it - otherwise a session that briefly
            // learned something low would permanently inherit 0.5 even after real evidence arrived.
            var s = Settings();
            s.FixedLatMaxG = 0.2;
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 400; i++)
                s.ObserveLatG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60));

            Assert.Equal(0.2, s.FixedLatMaxG, 6);
            Assert.True(s.GetLearnedLatMaxG("G", "C") < GForceSettings.MinLearnedLatMaxG,
                $"the learner's own value must stay un-floored, got {s.GetLearnedLatMaxG("G", "C")}");
        }

        [Fact]
        public void A_genuinely_low_G_vehicle_reads_proportionally_low_and_that_is_intended()
        {
            // THE DESIGN DECISION THIS PINS (owner's own reasoning). A truck that only pulls 0.2g is
            // normalised against the 0.5g floor, so its hardest effort reads about 40% rather than full
            // scale. That is CORRECT, not a defect: a real truck does not produce a strong G-force
            // transition, and the pad should not pretend it does. Above the floor the cue still means
            // "at this car's limit"; below it, it means "not much force here".
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 800; i++)
                s.ObserveDecelG("G", "C", 0.20, t.AddMilliseconds(i * 16));

            double max = Settled(x => s.EffectiveDecelMaxG("G", "C", x), t.AddSeconds(60));
            double readsAs = 0.20 / max;

            Assert.Equal(0.5, max, 3);
            Assert.InRange(readsAs, 0.35, 0.45);
        }

        // ---------------- The floor defers to a lower typed value ----------------

        [Fact]
        public void A_typed_value_below_the_floor_lowers_the_floor_to_it()
        {
            // OWNER'S RULE: the effective floor is Min(0.5, typed). Typing something under 0.5 is an
            // explicit statement that low readings are wanted on this axis, so the floor steps aside.
            // Their worked case: lateral typed at 0.2, learner converging on 0.3 -> 0.3 is allowed.
            var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 0.2 };
            s.SetCurrentGameAndCar("G", "C");

            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 2000; i++)
                s.ObserveLatG("G", "C", 0.30, t.AddMilliseconds(i * 16));

            double settled = Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60));
            Assert.InRange(settled, 0.25, 0.35);
        }

        [Fact]
        public void A_typed_value_above_the_floor_leaves_the_floor_at_half_a_g()
        {
            // The other half of the same rule, and the owner's other worked case: lateral typed at 1.5,
            // real maximum only 0.3 - the learner is allowed down to 0.5 and no further.
            var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5 };
            s.SetCurrentGameAndCar("G", "C");

            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 2000; i++)
                s.ObserveLatG("G", "C", 0.30, t.AddMilliseconds(i * 16));

            Assert.Equal(0.5, Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60)), 3);
        }

        // ---------------- Learning starts at the typed value and steps both ways ----------------

        [Fact]
        public void Learning_starts_at_the_typed_value_and_steps_DOWN_to_the_real_maximum()
        {
            // The owner's own scenario: typed 1.0 / 1.5 / 1.5, real maxima 0.7 / 1.2 / 1.0. Each axis
            // must OPEN at the typed number and converge downward onto what was actually measured -
            // all three well above their floors, so nothing is clamped here.
            var s = new GForceSettings
            {
                AccelMaxMode = GMaxMode.Auto, FixedAccelMaxG = 1.0,
                DecelMaxMode = GMaxMode.Auto, FixedDecelMaxG = 1.5,
                LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 1.5,
            };
            s.SetCurrentGameAndCar("G", "C");
            DateTime t = DateTime.UtcNow;

            // Before any evidence: exactly the typed values.
            Assert.Equal(1.0, s.EffectiveAccelMaxG("G", "C", t), 6);
            Assert.Equal(1.5, s.EffectiveDecelMaxG("G", "C", t), 6);
            Assert.Equal(1.5, s.EffectiveLatMaxG("G", "C", t), 6);

            for (int i = 0; i < 2000; i++)
            {
                DateTime at = t.AddMilliseconds(i * 16);
                s.ObserveAccelG("G", "C", 0.70, at);
                s.ObserveDecelG("G", "C", 1.20, at);
                s.ObserveLatG("G", "C", 1.00, at);
            }

            DateTime later = t.AddSeconds(60);
            Assert.InRange(Settled(x => s.EffectiveAccelMaxG("G", "C", x), later), 0.6, 0.8);
            Assert.InRange(Settled(x => s.EffectiveDecelMaxG("G", "C", x), later), 1.1, 1.3);
            Assert.InRange(Settled(x => s.EffectiveLatMaxG("G", "C", x), later), 0.9, 1.1);
        }

        [Fact]
        public void Learning_also_steps_UP_when_the_car_turns_out_to_be_stronger()
        {
            // The ramp is direction-agnostic by construction (it compares |target - lastPublished|), but
            // the owner asked explicitly for both directions, so both are pinned.
            var s = new GForceSettings { LatMaxMode = GMaxMode.Auto, FixedLatMaxG = 0.8 };
            s.SetCurrentGameAndCar("G", "C");
            DateTime t = DateTime.UtcNow;

            Assert.Equal(0.8, s.EffectiveLatMaxG("G", "C", t), 6);

            for (int i = 0; i < 2000; i++)
                s.ObserveLatG("G", "C", 2.40, t.AddMilliseconds(i * 16));

            Assert.InRange(Settled(x => s.EffectiveLatMaxG("G", "C", x), t.AddSeconds(60)), 2.2, 2.6);
        }

        [Fact]
        public void All_three_floors_are_half_a_g_and_independently_adjustable()
        {
            // Equal today, but kept as three constants: the axes have different physics (braking and
            // cornering are grip-limited, acceleration is power-limited), so one shared value would have
            // to be re-reasoned for all three at once if any of them ever moved.
            Assert.Equal(0.5, GForceSettings.MinLearnedLatMaxG, 6);
            Assert.Equal(0.5, GForceSettings.MinLearnedDecelMaxG, 6);
            Assert.Equal(0.5, GForceSettings.MinLearnedAccelMaxG, 6);
        }

        // ---------------- Lateral's own remaining properties ----------------

        [Fact]
        public void Lateral_takes_the_highest_reading_regardless_of_direction_or_sign()
        {
            // One axis, one maximum: whether the car was accelerating or braking is irrelevant, and so
            // is which way it was turning (the caller passes an absolute magnitude).
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 2000; i++)
                s.ObserveLatG("G", "C", i % 2 == 0 ? 1.80 : 0.90, t.AddMilliseconds(i * 16));

            Assert.True(s.GetLearnedLatMaxG("G", "C") > 1.5,
                $"the harder corner should dominate, got {s.GetLearnedLatMaxG("G", "C")}");
        }

        [Fact]
        public void A_NaN_or_non_positive_magnitude_never_reaches_the_pool()
        {
            // ObserveLatG no longer gates by magnitude, so this is the learner's own guard doing the work.
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 100; i++)
            {
                s.ObserveLatG("G", "C", double.NaN, t.AddMilliseconds(i * 16));
                s.ObserveLatG("G", "C", 0.0, t.AddMilliseconds(i * 16));
                s.ObserveLatG("G", "C", -1.0, t.AddMilliseconds(i * 16));
            }

            Assert.Equal(0.0, s.GetLearnedLatMaxG("G", "C"), 6);
        }

        [Fact]
        public void Small_but_real_cornering_now_reaches_the_learner_unfiltered()
        {
            // With the observation threshold gone, low-grip cornering is learned as-is - and then the
            // floor decides whether it is treated as a meaningful force at all.
            var s = Settings();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 2000; i++)
                s.ObserveLatG("G", "C", 0.40, t.AddMilliseconds(i * 16));

            Assert.True(s.GetLearnedLatMaxG("G", "C") > 0.3,
                $"0.4g cornering must be learnable, got {s.GetLearnedLatMaxG("G", "C")}");
        }
    }
}
