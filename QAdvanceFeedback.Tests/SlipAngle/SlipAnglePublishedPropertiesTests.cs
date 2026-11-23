using System;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests.SlipAngle
{
    /// <summary>docs\slip-angle-design.md 9.8 / 3: additive names, appended at the END, CSV aligned.</summary>
    public class SlipAnglePublishedPropertiesTests
    {
        private static readonly string[] ExpectedDiagnostics =
        {
            "Diag.SlipAngle.Wheel.FrontLeft", "Diag.SlipAngle.Wheel.FrontRight",
            "Diag.SlipAngle.Wheel.RearLeft", "Diag.SlipAngle.Wheel.RearRight",
            "Diag.SlipAngle.Front", "Diag.SlipAngle.Rear", "Diag.SlipAngle.K",
            "Diag.SlipAngle.Front.LearnedBest", "Diag.SlipAngle.Rear.LearnedBest",
            "Diag.SlipAngle.SourceTier", "Diag.SlipAngle.DriftState", "Diag.SlipAngle.DriftFactor",
            "Diag.SlipAngle.Window.Min", "Diag.SlipAngle.Window.Best", "Diag.SlipAngle.Window.Max",
            "Diag.SlipAngle.Maturity", "Diag.SlipAngle.WindowQuality", "Diag.SlipAngle.BorrowingTier",
            "Diag.SlipAngle.WobbleRemaining", "Diag.SlipAngle.PedalGateOpen", "Diag.SlipAngle.ImpactFlag",
            "Diag.SlipAngle.ExclusionRemaining", "Diag.SlipAngle.YawResidual", "Diag.SlipAngle.SurfaceBucket",
            "Diag.SlipAngle.SurfaceWeight.Sealed", "Diag.SlipAngle.SurfaceWeight.Loose", "Diag.SlipAngle.SurfaceWeight.LowGrip",
            "Diag.SlipAngle.SurfaceSource", "Diag.SlipAngle.Unit", "Diag.SlipAngle.UnitUndetermined",
            "Diag.SlipAngle.UnitVotes.Degrees", "Diag.SlipAngle.UnitVotes.Radians", "Diag.SlipAngle.NativeBlocked",
            "Diag.SlipAngle.SpeedScale",
            "Diag.SlipAngle.Crossings", "Diag.SlipAngle.PeakConfirmed", "Diag.SlipAngle.CurvePeakG", "Diag.SlipAngle.Identity",
            "Diag.SlipAngle.Sliding", "Diag.SlipAngle.EarlyWeight", "Diag.SlipAngle.EarlyGuarded",
            "Diag.SlipAngle.AppliedWindow.Min", "Diag.SlipAngle.AppliedWindow.Best", "Diag.SlipAngle.AppliedWindow.Max",
            "Diag.SlipAngle.ManualApplied",
        };

        [Fact]
        public void Product_list_gains_exactly_the_three_names_at_the_end()
        {
            string[] names = AllPublishedProperties.ProductNames().ToArray();
            Assert.Equal(66, names.Length);
            Assert.Equal("Clutch.Bite.Output", names[62]);
            Assert.Equal(new[]
            {
                "QAdvanceFeedback.SlipAngle.Raw.All",
                "QAdvanceFeedback.SlipAngle.Normalized.All",
                "QAdvanceFeedback.SlipAngle.Projected.All",
            }, names.Skip(63).Select(n => "QAdvanceFeedback." + n).ToArray());
        }

        [Fact]
        public void Diagnostic_list_gains_the_listed_names_at_the_end_after_clutch()
        {
            string[] diag = AllPublishedProperties.DiagnosticNames().ToArray();
            string[] tail = diag.Skip(diag.Length - ExpectedDiagnostics.Length).ToArray();
            Assert.Equal(ExpectedDiagnostics, tail);
            Assert.Equal("Diag.Clutch.BiteSamples", diag[diag.Length - ExpectedDiagnostics.Length - 1]);
        }

        [Fact]
        public void Every_name_is_distinct_and_diagnostics_stay_gated()
        {
            string[] all = AllPublishedProperties.AllNames(diagnosticsEnabled: true).ToArray();
            Assert.Equal(all.Length, all.Distinct().Count());
            string[] off = AllPublishedProperties.AllNames(diagnosticsEnabled: false).ToArray();
            foreach (string d in ExpectedDiagnostics) Assert.DoesNotContain(d, off);
            Assert.Contains("SlipAngle.Raw.All", off);
        }

        [Fact]
        public void Getter_tables_match_the_name_tables()
        {
            Assert.Equal(SlipAnglePublishedNames.ProductNames.Length, PropertyPublisher.SlipAngleProductGetters.Length);
            Assert.Equal(SlipAnglePublishedNames.DiagnosticNames.Length, PropertyPublisher.SlipAngleDiagnosticGetters.Length);
        }

        [Fact]
        public void Csv_row_count_matches_the_header_and_values_land_in_their_columns()
        {
            var header = AllPublishedProperties.AllNames(true).ToList();
            var publisher = new PropertyPublisher();
            publisher.UpdateSlipAngle(new SlipAngleFrameResult
            {
                RawAll = 7.5, NormalizedAll = 52.0, ProjectedAll = 21.0, K = 1.25,
                WheelRearRight = 8.25, Tier = SourceNativeness.Native, UnitVotesRadians = 2, NativeBlocked = true,
                SurfaceWeightLoose = 0.4,
            });

            object[] row = publisher.SnapshotAllValuesForCsv();

            Assert.Equal(header.Count, row.Length);
            Assert.Equal(7.5, row[header.IndexOf("SlipAngle.Raw.All")]);
            Assert.Equal(52.0, row[header.IndexOf("SlipAngle.Normalized.All")]);
            Assert.Equal(21.0, row[header.IndexOf("SlipAngle.Projected.All")]);
            Assert.Equal(1.25, row[header.IndexOf("Diag.SlipAngle.K")]);
            Assert.Equal(8.25, row[header.IndexOf("Diag.SlipAngle.Wheel.RearRight")]);
            Assert.Equal("Native", row[header.IndexOf("Diag.SlipAngle.SourceTier")]);
            Assert.Equal(2, row[header.IndexOf("Diag.SlipAngle.UnitVotes.Radians")]);
            Assert.Equal(true, row[header.IndexOf("Diag.SlipAngle.NativeBlocked")]);
            Assert.Equal(0.4, row[header.IndexOf("Diag.SlipAngle.SurfaceWeight.Loose")]);
        }

        [Fact]
        public void Empty_publishes_a_null_raw_and_zero_outputs()
        {
            var header = AllPublishedProperties.AllNames(true).ToList();
            var publisher = new PropertyPublisher();
            publisher.UpdateSlipAngle(new SlipAngleFrameResult { RawAll = 3.0, ProjectedAll = 40.0 });
            publisher.UpdateSlipAngle(SlipAngleFrameResult.Empty);

            object[] row = publisher.SnapshotAllValuesForCsv();

            Assert.Null(row[header.IndexOf("SlipAngle.Raw.All")]);
            Assert.Equal(0.0, row[header.IndexOf("SlipAngle.Normalized.All")]);
            Assert.Equal(0.0, row[header.IndexOf("SlipAngle.Projected.All")]);
            Assert.Null(row[header.IndexOf("Diag.SlipAngle.Wheel.FrontLeft")]);
            Assert.Equal("Unknown", row[header.IndexOf("Diag.SlipAngle.SourceTier")]);
        }

        [Fact]
        public void Csv_writer_writes_a_real_row_with_the_slip_angle_columns()
        {
            string path = Path.Combine(Path.GetTempPath(), "qaf-sa-csv-" + Guid.NewGuid() + ".csv");
            try
            {
                string[] header = AllPublishedProperties.AllNames(true).ToArray();
                var publisher = new PropertyPublisher();
                publisher.UpdateSlipAngle(new SlipAngleFrameResult { RawAll = 6.125 });
                using (var writer = new CsvExportWriter())
                {
                    writer.Start(path, header);
                    writer.WriteRow(publisher.SnapshotAllValuesForCsv());
                    writer.Stop();
                }
                string[] lines = File.ReadAllLines(path);
                Assert.Equal(2, lines.Length);
                Assert.Contains("SlipAngle.Raw.All", lines[0]);
                Assert.Contains("6.125", lines[1]);
            }
            finally
            {
                try { File.Delete(path); } catch { /* best effort */ }
            }
        }
    }
}
