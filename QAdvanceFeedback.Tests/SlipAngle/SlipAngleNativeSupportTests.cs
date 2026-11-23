using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Native promotion and per-game unit auto-detection (docs\slip-angle-design.md §9.1,
    /// §9.2 "Unverified units"; rulings R6).</summary>
    public class SlipAngleNativeSupportTests
    {
        private const double Dt = 0.1;
        private const string Game = "AssettoCorsaCompetizione";

        /// <summary>A plausible, varying four-wheel reading in degrees (2..6°).</summary>
        private static Corners Varying(int frame)
        {
            double v = 2.0 + (frame % 5);
            return new Corners(v, v + 0.5, v - 0.5, v + 0.25);
        }

        private static void Feed(SlipAngleNativeSupport s, string game, double seconds, System.Func<int, Corners?> wheels,
            double speedKmh = 100.0, SlipAngleUnit unit = SlipAngleUnit.Degrees, double? latG = null)
        {
            int frames = (int)System.Math.Round(seconds / Dt);
            for (int i = 0; i < frames; i++) s.Observe(game, wheels(i), unit, speedKmh, Dt, latG);
        }

        [Fact]
        public void Spec_values_are_pinned()
        {
            Assert.Equal(5.0, SlipAngleNativeSupport.PromotionSeconds);
            Assert.Equal(30.0, SlipAngleNativeSupport.MinMovingSpeedKmh);
            Assert.Equal(45.0, SlipAngleNativeSupport.PlausibleMaxDeg);
            Assert.Equal(1.0, SlipAngleNativeSupport.ImplausibleBlockSeconds);
            Assert.Equal(0.8, SlipAngleNativeSupport.UnitDetectionLateralG);
            Assert.Equal(1.0, SlipAngleNativeSupport.RadiansCeiling);
        }

        [Fact]
        public void Unknown_game_is_not_native()
        {
            var s = new SlipAngleNativeSupport();
            Assert.False(s.IsNative(Game));
            Assert.False(s.IsNative(null));
            Assert.False(s.IsNative(""));
        }

        [Fact]
        public void Promotes_after_5_seconds_of_moving_plausible_varying_frames()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 4.8, i => Varying(i));
            Assert.False(s.IsNative(Game));
            Feed(s, Game, 0.3, i => Varying(i));
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Convenience_overload_treats_values_as_degrees()
        {
            var s = new SlipAngleNativeSupport();
            for (int i = 0; i < 52; i++) s.Observe(Game, Varying(i), 100.0, Dt);
            Assert.True(s.IsNative(Game));
            Assert.True(s.TryGetUnit(Game, out SlipAngleUnit unit));
            Assert.Equal(SlipAngleUnit.Degrees, unit);
        }

        [Fact]
        public void Constant_zero_never_promotes()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 60.0, i => Corners.Zero);
            Assert.False(s.IsNative(Game));
        }

        [Fact]
        public void Constant_non_zero_never_promotes()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 60.0, i => Corners.Uniform(3.0));
            Assert.False(s.IsNative(Game));
        }

        [Fact]
        public void NaN_and_null_frames_do_not_count()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 30.0, i => i % 2 == 0 ? (Corners?)new Corners(double.NaN, 1, 2, 3) : null);
            Assert.False(s.IsNative(Game));
            Feed(s, Game, 30.0, i => new Corners(1, double.PositiveInfinity, 2, 3));
            Assert.False(s.IsNative(Game));
            Feed(s, Game, 5.1, i => Varying(i));
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Slow_frames_do_not_count()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 30.0, i => Varying(i), speedKmh: 30.0);
            Assert.False(s.IsNative(Game));
            Feed(s, Game, 30.0, i => Varying(i), speedKmh: double.NaN);
            Assert.False(s.IsNative(Game));
        }

        [Fact]
        public void Invalid_dt_frames_do_not_count()
        {
            var s = new SlipAngleNativeSupport();
            for (int i = 0; i < 500; i++) s.Observe(Game, Varying(i), SlipAngleUnit.Degrees, 100.0, 0.0, null);
            for (int i = 0; i < 500; i++) s.Observe(Game, Varying(i), SlipAngleUnit.Degrees, 100.0, double.NaN, null);
            for (int i = 0; i < 500; i++) s.Observe(Game, Varying(i), SlipAngleUnit.Degrees, 100.0, -1.0, null);
            Assert.False(s.IsNative(Game));
        }

        [Fact]
        public void A_huge_dt_frame_is_capped_and_cannot_promote_alone()
        {
            var s = new SlipAngleNativeSupport();
            s.Observe(Game, Varying(0), SlipAngleUnit.Degrees, 100.0, 10.0, null);
            s.Observe(Game, Varying(1), SlipAngleUnit.Degrees, 100.0, 10.0, null);
            Assert.False(s.IsNative(Game));
        }

        // ---- helpers for unverified-unit rows (episode voting, ruling R16) ----

        private static void FeedU(SlipAngleNativeSupport s, string game, double seconds, System.Func<int, Corners?> wheels,
            double? latG, double? forward = null)
        {
            int frames = (int)System.Math.Round(seconds / Dt);
            for (int i = 0; i < frames; i++) s.Observe(game, wheels(i), SlipAngleUnit.Unverified, 100.0, Dt, latG, forward);
        }

        /// <summary>One cornering episode: <paramref name="seconds"/> at <paramref name="g"/>, then 0.2 s at
        /// 0.2 g (which ends the episode) with <paramref name="between"/> wheels.</summary>
        private static void Episode(SlipAngleNativeSupport s, string game, double seconds, System.Func<int, Corners?> wheels,
            System.Func<int, Corners?> between, double g = 1.0)
        {
            FeedU(s, game, seconds, wheels, g);
            FeedU(s, game, 0.2, between, 0.2);
        }

        private static void DegreeEpisodes(SlipAngleNativeSupport s, string game, int count, double g = 1.0)
        {
            for (int k = 0; k < count; k++) Episode(s, game, 0.5, i => Varying(i), i => Varying(i), g);
        }

        private static void RadianEpisodes(SlipAngleNativeSupport s, string game, int count, double g = 1.0)
        {
            for (int k = 0; k < count; k++) Episode(s, game, 0.5, i => VaryingRad(i), i => VaryingRad(i), g);
        }

        /// <summary>Plausible slip in radians: 0.03..0.11 rad (about 2..6°).</summary>
        private static Corners VaryingRad(int frame)
        {
            double v = 0.03 + 0.02 * (frame % 5);
            return new Corners(v, v + 0.01, v - 0.01, v + 0.005);
        }

        /// <summary>A radian-valued spin: 1.1..1.5 rad (63..86°), all above the 1.0 ceiling.</summary>
        private static Corners SpinRad(int frame) => Corners.Uniform(1.1 + 0.1 * (frame % 5));

        private static void AssertUnit(SlipAngleNativeSupport s, string game, SlipAngleUnit expected)
        {
            Assert.True(s.TryGetUnit(game, out SlipAngleUnit unit));
            Assert.Equal(expected, unit);
        }

        // ---- the 45° block ----

        [Fact]
        public void Unverified_over_45_without_lateral_load_sustained_for_more_than_1s_blocks_promotion()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 1.5, i => new Corners(3, 3, 50 + i, 3), 0.1);
            DegreeEpisodes(s, Game, 10);
            Assert.False(s.IsNative(Game));
            Assert.True(s.IsBlocked(Game));
        }

        [Fact]
        public void Unverified_over_45_at_1g_does_not_block()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 3.0, i => new Corners(3, 3, 50 + i, 3), 1.0);
            DegreeEpisodes(s, Game, 8);
            Assert.False(s.IsBlocked(Game));
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Degrees);
        }

        [Fact]
        public void Unverified_over_45_with_unknown_lateral_g_is_neutral()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 3.0, i => Corners.Uniform(60), null);
            DegreeEpisodes(s, Game, 8);
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Unverified_over_45_while_reversing_never_counts_toward_the_block()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 1.5, i => Corners.Uniform(60), 0.1, forward: -10.0);
            DegreeEpisodes(s, Game, 8);
            Assert.False(s.IsBlocked(Game));
            Assert.True(s.IsNative(Game));

            // the same frames driving forward do block
            var t = new SlipAngleNativeSupport();
            FeedU(t, Game, 1.5, i => Corners.Uniform(60), 0.1, forward: 10.0);
            DegreeEpisodes(t, Game, 8);
            Assert.True(t.IsBlocked(Game));
            Assert.False(t.IsNative(Game));
        }

        [Fact]
        public void Unverified_garbage_after_the_unit_is_determined_is_neutral()
        {
            var s = new SlipAngleNativeSupport();
            DegreeEpisodes(s, Game, 3); // unit determined: 3 agreeing votes
            FeedU(s, Game, 3.0, i => Corners.Uniform(60), 0.1);
            FeedU(s, Game, 4.0, i => Varying(i), 0.5);
            Assert.False(s.IsBlocked(Game));
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Pinned_unit_over_45_frames_are_neutral_and_never_block()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 3.0, i => Corners.Uniform(80), latG: 0.0);
            Assert.False(s.IsBlocked(Game));
            Feed(s, Game, 4.9, i => Varying(i));
            Assert.False(s.IsNative(Game)); // the 3 s did not count
            Feed(s, Game, 0.2, i => Varying(i));
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Pinned_stuck_over_45_then_stuck_zero_never_promotes()
        {
            // Re-review minor (i): neutral > 45° frames must not make a stuck field look "varying".
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 3.0, i => Corners.Uniform(90));
            Feed(s, Game, 30.0, i => Corners.Zero);
            Assert.False(s.IsNative(Game));
        }

        [Fact]
        public void Drift21_long_drift_over_45_inside_the_first_5s_still_promotes()
        {
            // Drift21 is pinned radians; a 3 s drift at 1.0 rad (57°) is real, not garbage.
            var s = new SlipAngleNativeSupport();
            Feed(s, "Drift21", 1.0, i => VaryingRad(i), unit: SlipAngleUnit.Radians, latG: 0.9);
            Feed(s, "Drift21", 3.0, i => new Corners(0.2, 0.2, 1.0 + 0.01 * (i % 3), 1.0), unit: SlipAngleUnit.Radians, latG: 0.9);
            Feed(s, "Drift21", 4.1, i => VaryingRad(i), unit: SlipAngleUnit.Radians, latG: 0.9);
            Assert.False(s.IsBlocked("Drift21"));
            Assert.True(s.IsNative("Drift21"));
        }

        [Fact]
        public void Brief_over_45_excursions_do_not_block()
        {
            var s = new SlipAngleNativeSupport();
            for (int k = 0; k < 20; k++)
            {
                FeedU(s, Game, 0.5, i => new Corners(3, 3, 60, 3), 0.1); // 0.5 s garbage (also ends the episode)
                FeedU(s, Game, 0.3, i => Varying(i), 1.0);
            }
            Assert.False(s.IsBlocked(Game));
            Assert.True(s.IsNative(Game)); // 20 x 0.3 s = 6 s of plausible frames
        }

        [Fact]
        public void Over_45_frames_do_not_count_toward_the_5_seconds()
        {
            var s = new SlipAngleNativeSupport();
            for (int k = 0; k < 10; k++)
            {
                Feed(s, Game, 0.5, i => new Corners(3, 3, 60 + i, 3));
                Feed(s, Game, 0.4, i => Varying(i));
            }
            Assert.False(s.IsNative(Game)); // only 4 s of plausible frames
        }

        [Fact]
        public void Never_demotes_within_a_session()
        {
            var s = new SlipAngleNativeSupport();
            Feed(s, Game, 5.1, i => Varying(i));
            Assert.True(s.IsNative(Game));
            Feed(s, Game, 10.0, i => new Corners(90, 90, 90, 90));
            Feed(s, Game, 10.0, i => null);
            Feed(s, Game, 10.0, i => Corners.Zero);
            Assert.True(s.IsNative(Game));
        }

        [Fact]
        public void Games_are_isolated()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, "A", 1.5, i => new Corners(60, 60, 60, 60), 0.0); // A blocked
            Feed(s, "B", 3.0, i => Varying(i));
            Feed(s, "C", 5.1, i => Varying(i));
            DegreeEpisodes(s, "A", 10);
            Assert.False(s.IsNative("A"));
            Assert.True(s.IsBlocked("A"));
            Assert.False(s.IsNative("B"));
            Assert.True(s.IsNative("C"));
            Assert.False(s.IsBlocked("C"));
        }

        [Fact]
        public void Pinned_radians_are_converted_before_the_45_degree_check()
        {
            var s = new SlipAngleNativeSupport();
            // 0.9 rad = 51.6 deg: above 45° once converted, so those frames do not count (neutral).
            Feed(s, Game, 6.0, i => new Corners(0.9 + 0.01 * (i % 3), 0.9, 0.9, 0.9), unit: SlipAngleUnit.Radians);
            Assert.False(s.IsNative(Game));
            Assert.False(s.IsBlocked(Game));

            var t = new SlipAngleNativeSupport();
            Feed(t, Game, 5.1, i => new Corners(0.05 + 0.01 * (i % 3), 0.06, 0.04, 0.05), unit: SlipAngleUnit.Radians);
            Assert.True(t.IsNative(Game));
            AssertUnit(t, Game, SlipAngleUnit.Radians);
            Assert.False(t.IsUnitUndetermined(Game)); // pinned
        }

        [Fact]
        public void ToDegrees_converts_by_unit()
        {
            Assert.Equal(180.0, SlipAngleNativeSupport.ToDegrees(System.Math.PI, SlipAngleUnit.Radians), 9);
            Assert.Equal(12.5, SlipAngleNativeSupport.ToDegrees(12.5, SlipAngleUnit.Degrees), 9);
        }

        // ---- Unit auto-detection by episode voting (rulings R6, R16) ----

        [Fact]
        public void Episode_voting_values_are_pinned()
        {
            Assert.Equal(0.5, SlipAngleNativeSupport.EpisodeEndLateralG);
            Assert.Equal(0.3, SlipAngleNativeSupport.MinEpisodeSeconds);
            Assert.Equal(0.7, SlipAngleNativeSupport.DegreesFraction);
            Assert.Equal(3, SlipAngleNativeSupport.MinEpisodeVotes);
            Assert.Equal(2.0 / 3.0, SlipAngleNativeSupport.VoteAgreement);
            Assert.Equal(3.0, SlipAngleNativeSupport.ImpactJumpG);
        }

        [Fact]
        public void Radian_cornering_episodes_are_detected_as_radians()
        {
            var s = new SlipAngleNativeSupport();
            RadianEpisodes(s, Game, 8, g: 1.1);
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void Degree_cornering_episodes_are_detected_as_degrees()
        {
            var s = new SlipAngleNativeSupport();
            DegreeEpisodes(s, Game, 8, g: -1.0);
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Degrees);
        }

        [Fact]
        public void Without_high_lateral_g_episodes_the_unit_stays_undetermined()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 30.0, i => VaryingRad(i), 0.5);
            FeedU(s, Game, 30.0, i => VaryingRad(i), null);
            Assert.False(s.IsNative(Game));
            Assert.False(s.TryGetUnit(Game, out _));
            Assert.True(s.IsUnitUndetermined(Game));
        }

        [Fact]
        public void A_long_unbroken_high_g_run_is_one_open_episode_and_does_not_vote()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 20.0, i => VaryingRad(i), 1.0);
            Assert.False(s.IsNative(Game));
            Assert.True(s.TryGetUnitVotes(Game, out int deg, out int rad));
            Assert.Equal(0, deg + rad);
        }

        [Fact]
        public void Episodes_shorter_than_0_3s_do_not_vote()
        {
            var s = new SlipAngleNativeSupport();
            for (int k = 0; k < 10; k++) Episode(s, Game, 0.2, i => VaryingRad(i), i => VaryingRad(i));
            Assert.True(s.TryGetUnitVotes(Game, out int deg, out int rad));
            Assert.Equal(0, deg + rad);
            Assert.False(s.IsNative(Game));
            RadianEpisodes(s, Game, 3);
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void Frames_between_0_5_and_0_8_g_keep_an_episode_open_without_adding_to_it()
        {
            // 0.2 s at 1.0 g, 1 s at 0.6 g, 0.2 s at 1.0 g = one 0.4 s episode: one vote.
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 0.2, i => VaryingRad(i), 1.0);
            FeedU(s, Game, 1.0, i => VaryingRad(i), 0.6);
            FeedU(s, Game, 0.2, i => VaryingRad(i), 1.0);
            FeedU(s, Game, 0.1, i => VaryingRad(i), 0.2);
            Assert.True(s.TryGetUnitVotes(Game, out int deg, out int rad));
            Assert.Equal(0, deg);
            Assert.Equal(1, rad);
        }

        [Fact]
        public void A_single_spike_above_1_inside_an_episode_does_not_flip_its_vote()
        {
            var s = new SlipAngleNativeSupport();
            for (int k = 0; k < 10; k++)
                Episode(s, Game, 0.5, i => i == 2 ? Corners.Uniform(1.2) : VaryingRad(i), i => VaryingRad(i));
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void Unverified_unit_radians_reading_over_45_degrees_blocks()
        {
            var s = new SlipAngleNativeSupport();
            // 0.9 "rad" values with no lateral load: the unit will be radians, and 0.9 rad = 51.6° held
            // for 1.5 s with no load is garbage, so it must block.
            FeedU(s, Game, 1.5, i => Corners.Uniform(0.9), 0.1);
            RadianEpisodes(s, Game, 10);
            Assert.False(s.IsNative(Game));
            Assert.True(s.IsBlocked(Game));
        }

        [Fact]
        public void R16a_early_spin_as_first_episode_then_three_radian_episodes_is_radians()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => VaryingRad(i), 0.2);              // banked low-G moving time
            Episode(s, Game, 1.0, i => SpinRad(i), i => VaryingRad(i), g: 0.9); // first high-G episode: spin
            Assert.False(s.IsNative(Game));
            Assert.True(s.IsUnitUndetermined(Game));
            RadianEpisodes(s, Game, 3);
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Radians);
            Assert.False(s.IsUnitUndetermined(Game));
        }

        [Fact]
        public void R16b_only_the_spin_episode_leaves_the_unit_undetermined_and_not_promoted()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => VaryingRad(i), 0.2);
            Episode(s, Game, 1.0, i => SpinRad(i), i => VaryingRad(i), g: 0.9);
            FeedU(s, Game, 20.0, i => VaryingRad(i), 0.2);
            Assert.False(s.IsNative(Game));
            Assert.False(s.TryGetUnit(Game, out _));
            Assert.True(s.IsUnitUndetermined(Game));
            Assert.True(s.TryGetUnitVotes(Game, out int deg, out int rad));
            Assert.Equal(1, deg);
            Assert.Equal(0, rad);
        }

        [Fact]
        public void R16c_degree_game_three_cornering_episodes_at_2_to_8_is_degrees()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => Varying(i), 0.2);
            for (int k = 0; k < 3; k++)
                Episode(s, Game, 0.5, i => Corners.Uniform(2.0 + (i % 7)), i => Varying(i));
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Degrees);
        }

        [Fact]
        public void R16d_two_radian_episodes_and_one_spin_episode_is_radians()
        {
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => VaryingRad(i), 0.2);
            RadianEpisodes(s, Game, 2);
            Episode(s, Game, 0.6, i => SpinRad(i), i => VaryingRad(i), g: 0.9);
            Assert.True(s.IsNative(Game));
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void An_episode_needs_70_percent_at_or_above_1_to_vote_degrees()
        {
            // Each episode is 60 % degree-looking: votes radians.
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => VaryingRad(i), 0.2);
            for (int k = 0; k < 3; k++)
            {
                FeedU(s, Game, 0.3, i => Varying(i), 1.0);
                FeedU(s, Game, 0.2, i => VaryingRad(i), 1.0);
                FeedU(s, Game, 0.2, i => VaryingRad(i), 0.2);
            }
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void Lateral_g_jumps_over_3g_are_excluded_from_episode_time()
        {
            // Each episode: one ordinary 1.3 frame, 4 collision frames (latG jumping 4 g each frame) at 1.3,
            // then 0.2 s of radian cornering. Counted, the collision frames make it 71 % "degrees";
            // excluded, it is 33 %, so every episode votes radians.
            var s = new SlipAngleNativeSupport();
            FeedU(s, Game, 5.0, i => VaryingRad(i), 0.2);
            for (int k = 0; k < 3; k++)
            {
                for (int i = 0; i < 5; i++)
                    s.Observe(Game, Corners.Uniform(1.3), SlipAngleUnit.Unverified, 100.0, Dt, i % 2 == 0 ? 2.0 : -2.0);
                FeedU(s, Game, 0.2, i => VaryingRad(i), 1.0);
                FeedU(s, Game, 0.2, i => VaryingRad(i), 0.2);
            }
            AssertUnit(s, Game, SlipAngleUnit.Radians);
        }

        [Fact]
        public void Unit_is_unknown_before_any_evidence()
        {
            var s = new SlipAngleNativeSupport();
            Assert.False(s.TryGetUnit(Game, out SlipAngleUnit unit));
            Assert.Equal(SlipAngleUnit.Unverified, unit);
            Assert.False(s.IsUnitUndetermined(Game)); // never seen
            Assert.False(s.TryGetUnitVotes(Game, out _, out _));
        }
    }
}
