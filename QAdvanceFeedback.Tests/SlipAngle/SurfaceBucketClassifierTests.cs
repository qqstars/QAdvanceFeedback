using System;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Surface bucket classifier and game surface map (docs\slip-angle-design.md 9.5.8, ruling R13).</summary>
    public class SurfaceBucketClassifierTests
    {
        private const double Dt = 1.0 / 60.0;

        private static SurfaceBlend Run(SurfaceBucketClassifier c, double seconds, SurfaceBucket? game,
            Action<double, SurfaceBlend> each = null)
        {
            SurfaceBlend last = c.Current;
            int frames = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < frames; i++)
            {
                last = c.Step(game, Dt);
                each?.Invoke((i + 1) * Dt, last);
            }
            return last;
        }

        [Fact]
        public void Starts_sealed_with_full_sealed_weight()
        {
            var c = new SurfaceBucketClassifier();
            Assert.Equal(SurfaceBucket.Sealed, c.Current.Active);
            Assert.Equal(1.0, c.Current.WSealed);
            Assert.Equal(0.0, c.Current.WLoose);
            Assert.Equal(0.0, c.Current.WLowGrip);
            Assert.Equal(SurfaceBucketSource.Unknown, c.Current.Source);
        }

        /// <summary>NEVER GUESSED (owner, 2026-11-22): a game that reports no surface stays in its bucket for good -
        /// FH6, F1, AC - and the source reads Unknown.</summary>
        [Fact]
        public void A_game_that_reports_no_surface_never_switches()
        {
            var c = new SurfaceBucketClassifier();
            Run(c, 600.0, null, each: (t, r) =>
            {
                Assert.Equal(SurfaceBucket.Sealed, r.Active);
                Assert.Equal(1.0, r.WSealed);
                Assert.Equal(SurfaceBucketSource.Unknown, r.Source);
            });
        }

        [Fact]
        public void A_game_reported_surface_switches_after_the_hysteresis_and_blends_over_one_and_a_half_seconds()
        {
            var c = new SurfaceBucketClassifier();
            double switchedAt = double.NaN, fullAt = double.NaN;
            Run(c, 5.0, SurfaceBucket.Loose, each: (t, r) =>
            {
                Assert.Equal(1.0, r.WSealed + r.WLoose + r.WLowGrip, 9);
                Assert.Equal(SurfaceBucketSource.Game, r.Source);
                if (double.IsNaN(switchedAt) && r.Active == SurfaceBucket.Loose) switchedAt = t;
                if (double.IsNaN(fullAt) && r.WLoose >= 1.0) fullAt = t;
            });
            Assert.InRange(switchedAt, SurfaceBucketClassifier.HysteresisSeconds - Dt, SurfaceBucketClassifier.HysteresisSeconds + 2 * Dt);
            Assert.InRange(fullAt - switchedAt, SurfaceBucketClassifier.BlendSeconds - 2 * Dt, SurfaceBucketClassifier.BlendSeconds + 2 * Dt);
        }

        [Fact]
        public void A_short_reported_cut_does_not_flip()
        {
            var c = new SurfaceBucketClassifier();
            Run(c, 0.5, SurfaceBucket.Loose, each: (t, r) => Assert.Equal(SurfaceBucket.Sealed, r.Active));
            Run(c, 5.0, SurfaceBucket.Sealed, each: (t, r) => Assert.Equal(SurfaceBucket.Sealed, r.Active));
        }

        /// <summary>A frame the game leaves unmapped (rF2 Wet, a tie) is Unknown: the bucket is held, not guessed, and
        /// it restarts a pending switch.</summary>
        [Fact]
        public void An_unknown_frame_holds_the_bucket_and_restarts_a_pending_switch()
        {
            var c = new SurfaceBucketClassifier();
            Run(c, 3.0, SurfaceBucket.Loose);
            Assert.Equal(SurfaceBucket.Loose, c.Current.Active);
            SurfaceBlend r = Run(c, 10.0, null);
            Assert.Equal(SurfaceBucket.Loose, r.Active);
            Assert.Equal(SurfaceBucketSource.Unknown, r.Source);

            Run(c, 0.8, SurfaceBucket.LowGrip);
            c.Step(null, Dt);                                  // restarts the timer
            r = Run(c, 0.8, SurfaceBucket.LowGrip);
            Assert.Equal(SurfaceBucket.Loose, r.Active);
            r = Run(c, 0.5, SurfaceBucket.LowGrip);
            Assert.Equal(SurfaceBucket.LowGrip, r.Active);
        }

        // ---- the game's own surface, translated by the shipped source file (R13) ----------------------------------

        private static SurfaceBucket? Map(string game, string raw)
        {
            System.Collections.Generic.IReadOnlyDictionary<string, SurfaceBucket> values = SlipAngleSourceCompiled.Catalog.SurfaceValues(game);
            return values != null && raw != null && values.TryGetValue(raw, out SurfaceBucket b) ? b : (SurfaceBucket?)null;
        }

        [Theory]
        [InlineData("0", SurfaceBucket.Sealed)]   // Dry
        [InlineData("2", SurfaceBucket.Loose)]    // Grass
        [InlineData("3", SurfaceBucket.Loose)]    // Dirt
        [InlineData("4", SurfaceBucket.Loose)]    // Gravel
        [InlineData("5", SurfaceBucket.Sealed)]   // Kerb
        public void RFactor2_documented_surface_values_map(string raw, SurfaceBucket expected)
            => Assert.Equal(expected, Map("RFactor2", raw));

        [Theory]
        [InlineData("RFactor2", "1")]          // Wet: tarmac but not dry - unknown, never guessed
        [InlineData("RFactor2", "6")]          // Special
        [InlineData("RFactor2", "Dirt")]       // a byte field never arrives as a name
        [InlineData("LMU", "3")]               // meaning not documented for LMU
        [InlineData("F12022", "3")]            // F1 2019-22: meaning not documented
        [InlineData("F12025", "3")]
        [InlineData("Automobilista2", "1")]    // eTerrain values not documented
        [InlineData("GranTurismo7", "2")]      // GT7: only the names are documented
        [InlineData("RFactor2", null)]
        public void Undocumented_values_are_not_mapped(string game, string raw) => Assert.Null(Map(game, raw));

        [Theory]
        [InlineData("Tarmac", SurfaceBucket.Sealed)]
        [InlineData("kerb", SurfaceBucket.Sealed)]
        [InlineData("Dirt", SurfaceBucket.Loose)]
        [InlineData("Grass", SurfaceBucket.Loose)]
        [InlineData("SAND", SurfaceBucket.Loose)]
        [InlineData("Snow", SurfaceBucket.LowGrip)]
        public void GranTurismo7_documented_surface_names_map(string raw, SurfaceBucket expected)
            => Assert.Equal(expected, Map("GranTurismo7", raw));

        [Fact]
        public void Only_GT7_ships_a_low_grip_value()
        {
            Assert.True(SlipAngleSourceCompiled.Catalog.ReportsLowGrip("GranTurismo7"));
            Assert.False(SlipAngleSourceCompiled.Catalog.ReportsLowGrip("RFactor2"));
            Assert.False(SlipAngleSourceCompiled.Catalog.ReportsLowGrip("FH6"));
        }

        private static readonly SurfaceBucket? S = SurfaceBucket.Sealed, L = SurfaceBucket.Loose, G = SurfaceBucket.LowGrip, U = null;

        [Fact]
        public void Majority_vote_across_wheels_ignores_wheel_order()
        {
            Assert.Equal(SurfaceBucket.Loose, SurfaceBucketMap.Vote(new[] { L, S, L, L }));
            Assert.Equal(SurfaceBucket.Loose, SurfaceBucketMap.Vote(new[] { L, L, L, S }));
            Assert.Equal(SurfaceBucket.Sealed, SurfaceBucketMap.Vote(new[] { S, U, S, U }));
            Assert.Equal(SurfaceBucket.LowGrip, SurfaceBucketMap.Vote(new[] { G, G, S, U }));
        }

        [Fact]
        public void A_tie_or_nothing_mapped_is_unknown()
        {
            Assert.Null(SurfaceBucketMap.Vote(new[] { L, L, S, S }));
            Assert.Null(SurfaceBucketMap.Vote(new[] { U, U, U, U }));
            Assert.Null(SurfaceBucketMap.Vote(null));
        }

        // ---- SimHub's own grass/gravel flag (owner, 2026-11-23) -------------------------------------------------

        [Fact]
        public void SimHubs_flag_on_most_wheels_is_loose_even_before_any_evidence()
        {
            Assert.Equal(SurfaceBucket.Loose, SurfaceBucketMap.FromSimHubLoose(true, true, true, false, gameReportsLoose: false));
            Assert.Equal(SurfaceBucket.Loose, SurfaceBucketMap.FromSimHubLoose(true, true, true, true, gameReportsLoose: true));
        }

        [Fact]
        public void Not_loose_is_tarmac_only_once_the_game_was_seen_reporting_loose()
        {
            // A game that never fills the flag in reads all false: that must stay Unknown, not "tarmac".
            Assert.Null(SurfaceBucketMap.FromSimHubLoose(false, false, false, false, gameReportsLoose: false));
            Assert.Equal(SurfaceBucket.Sealed, SurfaceBucketMap.FromSimHubLoose(false, false, false, false, gameReportsLoose: true));
            Assert.Equal(SurfaceBucket.Sealed, SurfaceBucketMap.FromSimHubLoose(true, false, false, false, gameReportsLoose: true));
        }

        [Fact]
        public void Two_wheels_off_or_too_few_wheels_known_is_unknown()
        {
            Assert.Null(SurfaceBucketMap.FromSimHubLoose(true, true, false, false, gameReportsLoose: true));
            Assert.Null(SurfaceBucketMap.FromSimHubLoose(false, null, null, false, gameReportsLoose: true));
            Assert.Null(SurfaceBucketMap.FromSimHubLoose(null, null, null, null, gameReportsLoose: true));
        }

        [Fact]
        public void Saved_loose_buckets_are_kept_for_every_game_and_low_grip_only_for_a_game_that_can_report_it()
        {
            // (Shared static: every test that needs it sets the same compiled-file rule, so parallel tests never disagree.)
            SurfaceBucketMap.ReportsLowGrip = g => SlipAngleSourceCompiled.Catalog.ReportsLowGrip(g);
            Assert.False(SurfaceBucketMap.IsGuessedBucket("FH6", SurfaceBucket.Loose));      // SimHub's flag can report it
            Assert.True(SurfaceBucketMap.IsGuessedBucket("FH6", SurfaceBucket.LowGrip));
            Assert.False(SurfaceBucketMap.IsGuessedBucket("GranTurismo7", SurfaceBucket.LowGrip));
            Assert.False(SurfaceBucketMap.IsGuessedBucket("FH6", SurfaceBucket.Sealed));
        }

        [Fact]
        public void A_bad_surface_value_name_or_values_without_surface_make_the_file_invalid()
        {
            var bad = new SlipAngleSourceDocument();
            bad.Games.Add(new SlipAngleSourceGame
            {
                Id = "X", Surface = new System.Collections.Generic.List<string> { "a", "b", "c", "d" },
                SurfaceValues = new System.Collections.Generic.Dictionary<string, string> { ["1"] = "mud" },
            });
            Assert.Throws<FormatException>(() => SlipAngleSourceCatalog.FromDocument(bad));
            bad.Games[0].SurfaceValues["1"] = "loose";
            bad.Games[0].Surface = null;
            bad.Games[0].SlipAngle = new System.Collections.Generic.List<string> { "a", "b", "c", "d" };
            Assert.Throws<FormatException>(() => SlipAngleSourceCatalog.FromDocument(bad));
        }
    }
}
