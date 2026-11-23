using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>Speed-aware lateral G for the Slip Angle learner (owner, 2026-11-21): 40 km/h bands, the
    /// owner's staged 45/30/15% acceptance with a challenger, the 85% band-peak crossing bar, the matured-only
    /// scale, and persistence in the runtime file.</summary>
    public class SlipAngleSpeedBandsTests
    {
        private const double Dt = 1.0 / 60.0;

        private static SlipAngleSpeedBandState Band() => new SlipAngleSpeedBandState { LowKmh = 80 };

        // ---------------- bands ----------------

        [Theory]
        [InlineData(29.9, -1)]
        [InlineData(30.0, 0)]
        [InlineData(79.9, 1)]
        [InlineData(80.0, 2)]
        [InlineData(319.9, 7)]
        [InlineData(400.0, 8)]
        [InlineData(double.NaN, -1)]
        public void Speeds_fall_in_40_kmh_bands_from_30_kmh(double kmh, int band)
        {
            Assert.Equal(band, SlipAngleSpeedBands.BandIndex(kmh));
        }

        // ---------------- the owner's staged acceptance ----------------

        [Fact]
        public void The_first_crossing_is_always_accepted_but_not_counted()
        {
            SlipAngleSpeedBandState b = Band();
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 2.0));
            Assert.Equal(2.0, b.MeanG);
            Assert.Equal(1L, b.SampleCount);
            Assert.Equal(0L, b.CountedCount);
        }

        [Fact]
        public void Within_45_percent_is_accepted_and_only_within_15_percent_is_counted()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 2.0);
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 1.2));      // -40%: accepted, not counted
            Assert.Equal(0L, b.CountedCount);
            Assert.Equal(1.6, b.MeanG, 9);                                  // mean of the two
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 1.7));      // within 15% of 1.6: counted
            Assert.Equal(1L, b.CountedCount);
            Assert.Equal(3L, b.SampleCount);
        }

        [Fact]
        public void After_the_first_counted_crossing_the_tolerance_is_30_percent()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 2.0);
            SlipAngleSpeedBands.ObserveCrossing(b, 2.0);                    // counted 1
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 1.3));     // -35%: below the band, dropped
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 1.5));      // -25%: accepted, not counted
            Assert.Equal(1L, b.CountedCount);
        }

        [Fact]
        public void The_third_counted_crossing_matures_the_band_and_then_only_15_percent_is_accepted()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 3.0);
            for (int i = 0; i < 3; i++) SlipAngleSpeedBands.ObserveCrossing(b, 3.0);
            Assert.Equal(3L, b.CountedCount);
            Assert.True(SlipAngleSpeedBands.IsMatured(b));
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 2.4));     // -20%: dropped once matured
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 2.7));      // -10%: accepted and counted
            Assert.Equal(4L, b.CountedCount);
        }

        [Fact]
        public void A_band_seeded_by_a_light_corner_is_replaced_by_two_agreeing_higher_crossings()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 1.87);                   // a light corner, not at the limit
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 3.53));    // +89%: a challenger only
            Assert.Equal(1.87, b.MeanG);
            Assert.Equal(1L, b.ChallengerCount);
            Assert.True(SlipAngleSpeedBands.ObserveCrossing(b, 3.83));     // within 15% of 3.53: replaces
            Assert.Equal((3.53 + 3.83) / 2, b.MeanG, 9);
            Assert.Equal(2L, b.SampleCount);
            Assert.Equal(1L, b.CountedCount);
            Assert.Equal(0L, b.ChallengerCount);
        }

        [Fact]
        public void Two_disagreeing_higher_crossings_do_not_replace_the_band()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 2.0);
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 3.0));
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 4.0));     // 33% off the challenger: restarts it
            Assert.Equal(2.0, b.MeanG);
            Assert.Equal(4.0, b.ChallengerMeanG);
            Assert.Equal(1L, b.ChallengerCount);
        }

        [Fact]
        public void A_lower_crossing_outside_the_band_is_dropped_without_touching_the_challenger()
        {
            SlipAngleSpeedBandState b = Band();
            SlipAngleSpeedBands.ObserveCrossing(b, 3.0);
            SlipAngleSpeedBands.ObserveCrossing(b, 4.4);                    // challenger
            Assert.False(SlipAngleSpeedBands.ObserveCrossing(b, 1.0));
            Assert.Equal(3.0, b.MeanG);
            Assert.Equal(1L, b.SampleCount);
            Assert.Equal(1L, b.ChallengerCount);
        }

        [Fact]
        public void The_mean_is_the_plain_average_for_20_crossings_then_follows_newer_ones_at_one_twentieth()
        {
            SlipAngleSpeedBandState b = Band();
            for (int i = 0; i < 20; i++) SlipAngleSpeedBands.ObserveCrossing(b, 3.0);
            Assert.Equal(3.0, b.MeanG, 9);
            SlipAngleSpeedBands.ObserveCrossing(b, 3.3);
            Assert.Equal(3.0 + 0.3 / 20, b.MeanG, 9);
            Assert.Equal(21L, b.SampleCount);
        }

        // ---------------- scale ----------------

        private static SlipAngleSpeedBands Learned(params (int low, double mean, long counted)[] bands)
        {
            var set = new SlipAngleSpeedBandSetState();
            foreach (var (low, mean, counted) in bands)
                set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = low, MeanG = mean, SampleCount = counted + 1, CountedCount = counted, PeakG = mean });
            var sb = new SlipAngleSpeedBands();
            sb.Import(new Dictionary<string, SlipAngleSpeedBandSetState> { [SlipAngleSpeedBands.MakeKey("G", "C", "S", SurfaceBucket.Sealed)] = set });
            return sb;
        }

        [Fact]
        public void The_scale_is_1_with_nothing_learned_below_30_kmh_and_for_an_unknown_car()
        {
            var sb = new SlipAngleSpeedBands();
            Assert.Equal(1.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 150));
            SlipAngleSpeedBands learned = Learned((40, 2.0, 3), (200, 4.0, 3));
            Assert.Equal(1.0, learned.Scale("G", "C", "S", SurfaceBucket.Sealed, 20));
            Assert.Equal(1.0, learned.Scale("G", "Other", "S", SurfaceBucket.Sealed, 60));
        }

        [Fact]
        public void The_scale_lifts_a_slow_corner_to_the_highest_band_and_interpolates_between_band_centres()
        {
            SlipAngleSpeedBands sb = Learned((40, 2.0, 3), (200, 4.0, 3));
            Assert.Equal(2.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 60), 9);     // band 40-80 centre: 4.0 / 2.0
            Assert.Equal(1.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 220), 9);    // the highest band itself
            Assert.Equal(4.0 / 3.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 140), 9);   // halfway: reference 3.0
            Assert.Equal(2.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 35), 9);     // flat outside the learned bands
            Assert.Equal(1.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 330), 9);
        }

        [Fact]
        public void Only_matured_bands_set_the_scale()
        {
            SlipAngleSpeedBands sb = Learned((40, 1.6, 2), (200, 4.0, 3));   // a provisional light seed
            Assert.Equal(1.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 60), 9);
        }

        [Fact]
        public void The_scale_is_capped_at_2_5()
        {
            SlipAngleSpeedBands sb = Learned((40, 1.0, 3), (200, 4.0, 3));
            Assert.Equal(SlipAngleSpeedBands.MaxScale, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 60), 9);
        }

        // ---------------- crossings from frames ----------------

        private static void Straight(SlipAngleSpeedBands sb, double kmh, int frames = 80)
        {
            for (int i = 0; i < frames; i++) sb.Observe("G", "C", "S", SurfaceBucket.Sealed, kmh, 0.5, 0.3, true, false, Dt);
        }

        /// <summary>A corner at the limit: 20 frames held at <paramref name="g"/> while the angle builds, then the
        /// angle keeps growing while lateral G falls 1% a frame - the crossing shape. <paramref name="cut"/>: a
        /// frame (counted from the fall) that is not learnable.</summary>
        private static void Corner(SlipAngleSpeedBands sb, double kmh, double g, int cut = -1, int retroAt = -1)
        {
            double a = 1.0;
            for (int i = 0; i < 20; i++) { a += 0.1; sb.Observe("G", "C", "S", SurfaceBucket.Sealed, kmh, a, g, true, false, Dt); }
            for (int i = 0; i < 14; i++)
            {
                a += 0.2;
                sb.Observe("G", "C", "S", SurfaceBucket.Sealed, kmh, a, g * (1.0 - 0.01 * (i + 1)), i != cut, i == retroAt, Dt);
            }
            Straight(sb, kmh);
        }

        [Fact]
        public void A_corner_at_the_limit_teaches_its_band_the_lateral_g_of_the_crossing()
        {
            var sb = new SlipAngleSpeedBands();
            Corner(sb, 100, 2.5);
            SlipAngleSpeedBandState b = sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2);
            Assert.Equal(1L, b.SampleCount);
            Assert.Equal(2.5, b.MeanG, 1);
            Assert.Equal(0L, sb.Band("G", "C", "S", SurfaceBucket.Sealed, 3).SampleCount);
            Assert.True(sb.Changed);
        }

        [Fact]
        public void A_frame_that_is_not_learnable_right_after_the_crossing_drops_it()
        {
            // The corner ends at the gap: a corner that kept going would be a new, legitimate crossing.
            var sb = new SlipAngleSpeedBands();
            double a = 1.0;
            for (int i = 0; i < 20; i++) { a += 0.1; sb.Observe("G", "C", "S", SurfaceBucket.Sealed, 100, a, 2.5, true, false, Dt); }
            for (int i = 0; i < 4; i++) { a += 0.2; sb.Observe("G", "C", "S", SurfaceBucket.Sealed, 100, a, 2.5 * (1.0 - 0.01 * (i + 1)), true, false, Dt); }
            sb.Observe("G", "C", "S", SurfaceBucket.Sealed, 100, a, 2.3, false, false, Dt);
            Straight(sb, 100);
            Assert.Equal(0L, sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2).SampleCount);
        }

        [Fact]
        public void An_impact_flagged_right_after_the_crossing_drops_it()
        {
            var sb = new SlipAngleSpeedBands();
            Corner(sb, 100, 2.5, retroAt: 7);
            Assert.Equal(0L, sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2).SampleCount);
        }

        [Fact]
        public void A_corner_below_85_percent_of_the_band_peak_is_no_crossing()
        {
            var sb = new SlipAngleSpeedBands();
            Corner(sb, 100, 3.0);
            Corner(sb, 100, 2.4);                                    // 80% of the 3.0 peak
            Assert.Equal(1L, sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2).SampleCount);
        }

        [Fact]
        public void A_short_spike_never_raises_the_band_peak()
        {
            var sb = new SlipAngleSpeedBands();
            Straight(sb, 100, 30);
            for (int i = 0; i < SlipAngleSpeedBands.SustainFrames - 1; i++) sb.Observe("G", "C", "S", SurfaceBucket.Sealed, 100, 0.5, 6.0, true, false, Dt);
            Straight(sb, 100, 30);
            Assert.True(sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2).PeakG < 0.31);
        }

        // ---------------- persistence ----------------

        [Fact]
        public void Bands_round_trip_through_export_import_and_json_with_long_counts()
        {
            SlipAngleSpeedBands sb = Learned((80, 2.36, 9), (200, 3.9, 18));
            var big = new SlipAngleSpeedBandSetState();
            big.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 120, MeanG = 2.9, SampleCount = 5_000_000_000L, CountedCount = 4_000_000_000L, ChallengerMeanG = 3.5, ChallengerCount = 3_000_000_000L, PeakG = 3.1 });
            Dictionary<string, SlipAngleSpeedBandSetState> exported = sb.Export();
            exported[SlipAngleSpeedBands.MakeKey("G", "Big", "S", SurfaceBucket.Sealed)] = big;

            var doc = new RuntimeDocument { SlipAngleSpeedBands = exported };
            RuntimeDocument back = JsonConvert.DeserializeObject<RuntimeDocument>(JsonConvert.SerializeObject(doc));
            var restored = new SlipAngleSpeedBands();
            restored.Import(back.SlipAngleSpeedBands);

            Assert.True(back.Version >= 13);   // v14 added the SMax evidence alongside
            Assert.Equal(JsonConvert.SerializeObject(exported), JsonConvert.SerializeObject(restored.Export()));
            SlipAngleSpeedBandState b = restored.Band("G", "Big", "S", SurfaceBucket.Sealed, 3);
            Assert.Equal(5_000_000_000L, b.SampleCount);
            Assert.Equal(4_000_000_000L, b.CountedCount);
            Assert.Equal(3_000_000_000L, b.ChallengerCount);
            Assert.Equal(sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 100), restored.Scale("G", "C", "S", SurfaceBucket.Sealed, 100), 12);
        }

        [Fact]
        public void An_older_runtime_file_without_the_section_reads_as_empty()
        {
            RuntimeDocument doc = JsonConvert.DeserializeObject<RuntimeDocument>("{\"Version\":12}");
            Assert.NotNull(doc.SlipAngleSpeedBands);
            Assert.Empty(doc.SlipAngleSpeedBands);
        }

        [Fact]
        public void Import_skips_bands_with_a_bad_edge_or_value()
        {
            var set = new SlipAngleSpeedBandSetState();
            set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 50, MeanG = 2.0, SampleCount = 1 });
            set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 400, MeanG = 2.0, SampleCount = 1 });
            set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 80, MeanG = double.NaN, SampleCount = 1 });
            set.Bands.Add(new SlipAngleSpeedBandState { LowKmh = 120, MeanG = 2.0, SampleCount = -1 });
            set.Bands.Add(null);
            var sb = new SlipAngleSpeedBands();
            sb.Import(new Dictionary<string, SlipAngleSpeedBandSetState> { [SlipAngleSpeedBands.MakeKey("G", "C", "S", SurfaceBucket.Sealed)] = set, ["x"] = null });
            Assert.Empty(sb.Export());
        }

        [Fact]
        public void The_keyed_learner_saves_the_bands_with_its_windows()
        {
            var learner = new KeyedSlipAngleLearner();
            Dictionary<string, SlipAngleSpeedBandSetState> saved = null;
            learner.SpeedBandSave = d => saved = d;
            learner.SpeedBands.Import(Learned((40, 2.0, 3)).Export());
            learner.Save(_ => { });
            Assert.NotNull(saved);
            Assert.Equal(2.0, saved[SlipAngleSpeedBands.MakeKey("G", "C", "S", SurfaceBucket.Sealed)].Bands[0].MeanG);
        }

        /// <summary>Bands saved per (game, car) before 2026-11-22 become the car's Sealed bands for the Plugin Internal
        /// source the first time it is driven (migration, not borrowing); another car starts at scale 1.</summary>
        [Fact]
        public void The_processor_scales_lateral_g_by_the_learned_bands_and_reports_it()
        {
            var learner = new KeyedSlipAngleLearner();
            var old = new Dictionary<string, SlipAngleSpeedBandSetState>
            {
                [SlipAngleSpeedBands.LegacyKey("G", "C")] = Learned((40, 2.0, 3), (200, 4.0, 3)).Export().Values.Single(),
            };
            learner.SpeedBands.Import(old);
            var p = new SlipAngleFrameProcessor(learner, null);
            var f = new TelemetryFrame(groundSpeedKmh: 60, speedKmh: 60, brakePercent: 50, throttlePercent: 0,
                lateralLocalVelocity: 2.0, forwardLocalVelocity: 16.7, lateralG: 1.0, yawRateRadPerSec: 0.3);
            SlipAngleFrameResult r = p.Process(new SlipAngleFrameRequest { GameId = "G", CarId = "C", Frame = f, Dt = Dt, Input = new SlipAngleInput("G") });
            Assert.Equal(2.0, r.SpeedScale, 9);
            r = p.Process(new SlipAngleFrameRequest { GameId = "G", CarId = "Cold", Frame = f, Dt = Dt, Input = new SlipAngleInput("G") });
            Assert.Equal(1.0, r.SpeedScale, 9);
            Dictionary<string, SlipAngleSpeedBandSetState> saved = learner.SpeedBands.Export();
            Assert.True(saved.ContainsKey(SlipAngleSpeedBands.MakeKey("G", "C", SlipAngleFrameProcessor.FallbackIdentity, SurfaceBucket.Sealed)));
            Assert.False(saved.ContainsKey(SlipAngleSpeedBands.LegacyKey("G", "C")));   // moved, not copied
        }

        /// <summary>PER SURFACE, NEVER BORROWED (owner, 2026-11-22): a surface's bands are its own; another surface, source
        /// or car starts empty (scale 1) - and every surface learns, not only sealed.</summary>
        [Fact]
        public void Bands_are_per_game_car_source_and_surface_and_never_borrowed()
        {
            SlipAngleSpeedBands sb = Learned((40, 2.0, 3), (200, 4.0, 3));
            Assert.Equal(2.0, sb.Scale("G", "C", "S", SurfaceBucket.Sealed, 60), 9);
            Assert.Equal(1.0, sb.Scale("G", "C", "S", SurfaceBucket.Loose, 60), 9);
            Assert.Equal(1.0, sb.Scale("G", "C", "Other", SurfaceBucket.Sealed, 60), 9);
            Assert.Equal(1.0, sb.Scale("G", "C2", "S", SurfaceBucket.Sealed, 60), 9);
            Assert.Equal(SlipAngleBorrowScope.Never, SlipAngleBorrowRules.SpeedBandScope);

            for (int i = 0; i < 200; i++) sb.Observe("G", "C", "S", SurfaceBucket.Loose, 100, 2.0, 1.5, true, false, Dt);
            Assert.True(sb.Band("G", "C", "S", SurfaceBucket.Loose, 2).PeakG > 0.0);   // loose learns its own
            Assert.Equal(0.0, sb.Band("G", "C", "S", SurfaceBucket.Sealed, 2).PeakG);   // sealed untouched
        }
    }
}
