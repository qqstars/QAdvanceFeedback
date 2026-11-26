using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A FINISHED calibration (more than 7000 points, never fed again) keeps only what the plugin reads (owner,
    /// 2026-11-25; parameters Version 20): the point counter, Sum and Count, plus - for Slip - its 15th and 99th
    /// percentiles. Every value the plugin reads comes back exactly the same.
    /// </summary>
    public sealed class ShakeItCalibrationCompactTests
    {
        private static readonly JsonSerializerSettings Settings = ShakeItCalibrationContractResolver.Settings(Formatting.None);

        private const string SlipKey = "track;car;" + CalibrationDataProvider.SlipMetric;
        private const string RpsKey = "track;car;" + CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.FrontSuffix;

        /// <summary>A slip-like distribution: mostly small ratios, a tail, and some zeros (zeros are not "points").</summary>
        private static CalibrationData Fed(int samples, int seed = 7)
        {
            var random = new Random(seed);
            var c = new CalibrationData();
            for (int i = 0; i < samples; i++)
            {
                double v = i % 9 == 0 ? 0.0 : Math.Pow(random.NextDouble(), 3) * (random.NextDouble() < 0.05 ? 60.0 : 1.5);
                c.AddValue(v);
            }
            return c;
        }

        /// <summary>Fed as the provider does: never past the cap (CalibrationDataProvider.UpdateData).</summary>
        private static CalibrationData Finished(int seed = 11)
        {
            var random = new Random(seed);
            var c = new CalibrationData();
            while (c.CalibrationPointsAdded <= CalibrationData.ReadySamples)
                c.AddValue(Math.Pow(random.NextDouble(), 3) * 1.5);
            return c;
        }

        private static CalibrationData RoundTrip(CalibrationData c) =>
            JsonConvert.DeserializeObject<CalibrationData>(JsonConvert.SerializeObject(c, Settings), Settings);

        private static JObject Json(CalibrationData c) => JObject.Parse(JsonConvert.SerializeObject(c, Settings));

        /// <summary>Loaded through the provider, as the plugin does: each entry learns its metric from its key.</summary>
        private static CalibrationDataProvider Imported(string key, CalibrationData entry)
        {
            var provider = new CalibrationDataProvider();
            provider.ImportCalibrations(new Dictionary<string, CalibrationData>(StringComparer.Ordinal) { [key] = entry });
            return provider;
        }

        [Fact]
        public void A_finished_slip_calibration_keeps_only_its_two_percentiles_and_reads_back_the_same()
        {
            CalibrationData full = Finished();
            double p15 = full.GetPercentile(15.0), p99 = full.GetPercentile(99.0);
            double? average = full.GetAverage();

            full.Compact();
            Assert.True(full.IsCompact);
            Assert.Empty(full.Values);
            Assert.Equal(p15, full.GetPercentile(15.0));   // the published value does not move
            Assert.Equal(p99, full.GetPercentile(99.0));

            JObject json = Json(full);
            Assert.Equal(new[] { "s", "c", "p", "p15", "p99" }, Names(json));

            CalibrationData back = RoundTrip(full);
            Assert.True(back.IsCompact);
            Assert.Equal(p15, back.GetPercentile(15.0));
            Assert.Equal(p99, back.GetPercentile(99.0));
            Assert.Equal(average, back.GetAverage());
            Assert.Equal(0.0, back.GetPercentile(0.0));
        }

        [Fact]
        public void A_finished_rps_calibration_keeps_only_its_average()
        {
            CalibrationData full = Finished();
            double? average = full.GetAverage();
            CalibrationData entry = Imported(RpsKey, full).CalibrationData[RpsKey];
            Assert.True(entry.IsCompact);

            JObject json = Json(entry);
            Assert.Equal(new[] { "s", "c", "p" }, Names(json));
            Assert.Equal(average, RoundTrip(entry).GetAverage());
        }

        [Fact]
        public void The_provider_compacts_an_entry_the_frame_it_stops_feeding_it()
        {
            var provider = new CalibrationDataProvider();
            var random = new Random(3);
            CalibrationData slip = null;
            for (int frame = 0; frame < 3000 && (slip == null || !slip.IsCompact); frame++)
            {
                double s = Math.Pow(random.NextDouble(), 3) * 1.5;
                provider.Update("track", "car", false, true, 80.0, 0.0, null, new double?[] { s, s, s, s });
                slip = provider.CalibrationData[SlipKey];
            }
            Assert.True(slip.IsCompact);
            Assert.NotNull(slip.Percentile15);
            Assert.NotNull(slip.Percentile99);
        }

        [Fact]
        public void A_compact_calibration_is_never_fed_and_saves_compact_again()
        {
            CalibrationData compact = Finished();
            compact.Compact();
            int count = compact.Count;
            double p99 = compact.GetPercentile(99.0);
            compact.AddValue(50.0);
            Assert.Equal(count, compact.Count);
            Assert.Equal(p99, compact.GetPercentile(99.0));
            Assert.Null(Json(RoundTrip(compact))["v"]);
        }

        [Fact]
        public void A_percentile_a_compact_entry_did_not_keep_restarts_its_learning_rather_than_answering_wrong()
        {
            CalibrationData compact = Finished();
            compact.Compact();
            compact.GetPercentile(50.0);
            Assert.False(compact.IsFinished);
            Assert.Equal(0, compact.Count);
            compact.AddValue(0.5);   // fed again
            Assert.Equal(1, compact.Count);
        }

        [Fact]
        public void An_unfinished_calibration_keeps_its_histogram()
        {
            CalibrationData learning = Fed(3000);
            Assert.False(learning.IsFinished);
            JObject json = Json(learning);
            Assert.NotNull(json["v"]);
            Assert.NotNull(json["mx"]);
            Assert.Null(json["p15"]);

            CalibrationData back = RoundTrip(learning);
            Assert.Equal(learning.Values.Count, back.Values.Count);
            Assert.Equal(learning.GetPercentile(99.0), back.GetPercentile(99.0));
            back.AddValue(0.5);   // still fed
            Assert.Equal(learning.Count + 1, back.Count);
        }

        [Fact]
        public void An_older_files_histogram_and_a_version_17_table_both_load_and_are_written_in_the_new_form()
        {
            CalibrationData full = Finished();
            double p15 = full.GetPercentile(15.0), p99 = full.GetPercentile(99.0);

            // Version 16: the whole histogram.
            CalibrationData fromHistogram = Imported(SlipKey, RoundTrip(full)).CalibrationData[SlipKey];
            Assert.True(fromHistogram.IsCompact);
            Assert.Equal(p15, fromHistogram.GetPercentile(15.0));
            Assert.Equal(p99, fromHistogram.GetPercentile(99.0));

            // Version 17-19: the 100-value table "pt" (index 14 = 15th, 98 = 99th).
            var table = new JArray();
            for (int i = 1; i <= 100; i++) table.Add(i == 15 ? p15 : i == 99 ? p99 : i / 100.0);
            var old = new JObject { ["s"] = full.Sum, ["c"] = full.Count, ["p"] = full.CalibrationPointsAdded, ["mx"] = 1.4, ["pt"] = table };
            CalibrationData fromTable = Imported(SlipKey, JsonConvert.DeserializeObject<CalibrationData>(old.ToString(), Settings)).CalibrationData[SlipKey];
            Assert.Equal(p15, fromTable.GetPercentile(15.0));
            Assert.Equal(p99, fromTable.GetPercentile(99.0));
            Assert.Equal(new[] { "s", "c", "p", "p15", "p99" }, Names(Json(fromTable)));
        }

        [Fact]
        public void The_export_is_a_copy_and_reports_a_change_only_when_an_entry_changed()
        {
            var provider = new CalibrationDataProvider();
            provider.Update("track", "car", false, true, 80.0, 0.0, null, new double?[] { 0.2, 0.3, 0.4, 0.5 });
            Dictionary<string, CalibrationData> first = provider.ExportCalibrations(out bool changed);
            Assert.True(changed);
            Assert.NotSame(provider.CalibrationData[SlipKey], first[SlipKey]);

            Dictionary<string, CalibrationData> again = provider.ExportCalibrations(out changed);
            Assert.False(changed);                                // nothing fed since
            Assert.Same(first[SlipKey], again[SlipKey]);          // the unchanged copy is reused

            provider.Update("track", "car", false, true, 80.0, 0.0, null, new double?[] { 0.6, null, null, null });
            Dictionary<string, CalibrationData> third = provider.ExportCalibrations(out changed);
            Assert.True(changed);
            Assert.Equal(4, first[SlipKey].Count);                // an earlier copy never moves
            Assert.Equal(5, third[SlipKey].Count);
        }

        [Fact]
        public void The_compact_form_is_a_small_fraction_of_the_histogram()
        {
            CalibrationData full = Finished();
            int histogram = JsonConvert.SerializeObject(full, Settings).Length;
            full.Compact();
            int compact = JsonConvert.SerializeObject(full, Settings).Length;
            Assert.True(compact < 100, compact + " chars");
            Assert.True(compact * 20 < histogram, compact + " vs " + histogram);
        }

        private static string[] Names(JObject json)
        {
            var names = new List<string>();
            foreach (JProperty p in json.Properties()) names.Add(p.Name);
            return names.ToArray();
        }
    }
}
