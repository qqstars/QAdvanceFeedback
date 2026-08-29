using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// COLD-START / REFERENCE-HANDOVER SIMULATION (docs\cross-channel-smax-report.md).
    /// <para/>
    /// Replays the real pipeline across a SEQUENCE of sessions - switching source, surface and car -
    /// carrying learned state between them through the same <c>ExportAll</c>/<c>ImportAll</c> round trip
    /// a real SimHub restart uses (held in memory here rather than written to disk, which exercises the
    /// identical code path). The point is to see the tiered reference actually kick in: session 1 of each
    /// scenario is a genuine full cold start, and every session after it should reach a usable ceiling
    /// faster because something comparable has already been learned.
    /// <para/>
    /// "COLD START DONE" is defined as the moment the hand-over confidence reaches
    /// <see cref="HandoverDoneThreshold"/> - i.e. the published value has essentially stopped being the
    /// reference and become this key's own learned evidence. For SMax that is
    /// <see cref="KeyedScaleLearner.CeilingHandoverConfidence"/>; for S75/S90 it is
    /// <see cref="LockAnchorLearner.PhysicalRatioConfidence"/>. Seconds are accumulated from the
    /// session's own first frame using the capture's own timestamps.
    /// <para/>
    /// GRACEFUL WHEN LOGS ARE ABSENT - see <see cref="RealLogSMaxConvergenceReportTests"/>.
    /// </summary>
    public class ColdStartSimulationReportTests
    {
        private readonly ITestOutputHelper _output;
        public ColdStartSimulationReportTests(ITestOutputHelper output) { _output = output; }

        private const string Root = @"C:\Development\Repos\Samples\simhub";
        private const double HandoverDoneThreshold = 0.95;
        private const double MinSpeedKmh = 3.0;

        /// <summary>S75/S90 seed ratios - must match NormalizedWheelLockSlipEngine's own
        /// PhysicalS75SeedRatio/PhysicalS90SeedRatio, which are private.</summary>
        private const double S75Seed = 0.50, S90Seed = 0.75;

        private sealed class Step
        {
            public string Label;
            public string File;
            public string GameId;
            public string CarId;
            public string Source;
            /// <summary>Which contiguous car-segment of the file to use, or null for the whole file.
            /// The 1.7.1 capture holds four stints in one file, separated by gaps.</summary>
            public int? Segment;
        }

        private const string Wet = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-122552-withRawWet.csv";
        private const string WetShake = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-122825-WithShakeItWet.csv";
        private const string Dry = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-123120-WithRawDry.csv";
        private const string DryShake = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-123345-WithShakeItDry.csv";
        private const string Capture171 = @"c_1_7_1_e_d\QAdvanceFeedback.session-20260825-201217.csv";

        // NOTE ON IDENTITY. The 1.5.x wet/dry captures record NO Diag.GameId or Diag.CarId at all, so the
        // "two cars" in them are not recoverable from the data - they are assigned here explicitly, which
        // is what makes the car switch in each scenario a real key change for the learner. The 1.7.1
        // capture does carry real ids (Haas, Red Bull) and those are used as-is.
        //
        // NOTE ON SURFACE. The plugin keys surface as Sealed/Loose (from WheelOnLooseSurface), not
        // wet/dry - wet tarmac and dry tarmac are the SAME bucket. So a wet->dry switch here exercises a
        // genuine change in the DATA (grip, and therefore the source's own range) without being a
        // different surface key; that is the honest reading of what these scenarios test.
        private static readonly Step[] ScenarioA =
        {
            new Step { Label = "1 wet, source A (COLD)",   File = Wet,        GameId = "F12025", CarId = "CarA", Source = "Raw" },
            new Step { Label = "2 wet, source B",          File = WetShake,   GameId = "F12025", CarId = "CarA", Source = "ShakeIt" },
            new Step { Label = "3 dry, source A",          File = Dry,        GameId = "F12025", CarId = "CarA", Source = "Raw" },
            new Step { Label = "4 dry, source A, car 2",   File = Capture171, GameId = "F12025", CarId = null,   Source = "Raw",     Segment = 1 },
            new Step { Label = "5 dry, source B",          File = DryShake,   GameId = "F12025", CarId = "CarA", Source = "ShakeIt" },
            new Step { Label = "6 dry, source B, car 3",   File = Capture171, GameId = "F12025", CarId = null,   Source = "ShakeIt", Segment = 2 },
        };

        private static readonly Step[] ScenarioB =
        {
            new Step { Label = "1 dry, source A (COLD)",   File = Dry,        GameId = "F12025", CarId = "CarA", Source = "Raw" },
            new Step { Label = "2 dry, source B",          File = DryShake,   GameId = "F12025", CarId = "CarA", Source = "ShakeIt" },
            new Step { Label = "3 dry, source A, car 2",   File = Capture171, GameId = "F12025", CarId = null,   Source = "Raw",     Segment = 1 },
            new Step { Label = "4 wet, source A",          File = Wet,        GameId = "F12025", CarId = "CarA", Source = "Raw" },
            new Step { Label = "5 dry, source B, car 3",   File = Capture171, GameId = "F12025", CarId = null,   Source = "ShakeIt", Segment = 2 },
            new Step { Label = "6 wet, source B",          File = WetShake,   GameId = "F12025", CarId = "CarA", Source = "ShakeIt" },
        };

        [Fact]
        public void Simulate_cold_start_and_reference_handover_across_source_surface_and_car_switches()
        {
            if (!Directory.Exists(Root)) return;

            var report = new StringBuilder();
            report.AppendLine("# Cold-start and reference hand-over simulation");
            report.AppendLine();
            report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "\"Cold-start done\" = hand-over confidence reaches {0:P0}: the published value has stopped", HandoverDoneThreshold));
            report.AppendLine("being the reference and become this key's own learned evidence.");
            report.AppendLine("`-` in a seconds column means cold start did not complete within that session.");
            report.AppendLine("Learned state carries between sessions via ExportAll/ImportAll, as a real restart does.");
            report.AppendLine();

            RunScenario("Scenario A - cold start on WET", ScenarioA, report);
            RunScenario("Scenario B - cold start on DRY", ScenarioB, report);

            string text = report.ToString();
            _output.WriteLine(text);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "qaf-coldstart-simulation.md"), text); }
            catch (IOException) { }

            Assert.Contains("Scenario A", text);
        }

        private static void RunScenario(string title, Step[] steps, StringBuilder report)
        {
            report.AppendLine("## " + title);
            report.AppendLine();
            report.AppendLine("| session | SMax cold s | S75/S90 cold s | SMax cold mean | SMax cold P95 | S75 cold mean | S75 cold P95 | S90 cold mean | S90 cold P95 | SMax warm mean | SMax warm P95 | S75 warm mean | S75 warm P95 | S90 warm mean | S90 warm P95 |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

            Dictionary<string, ScaleLearnerState> lockState = null, slipState = null;
            Dictionary<string, LockAnchorState> anchorState = null;

            foreach (Step step in steps)
            {
                string path = Path.Combine(Root, step.File);
                if (!File.Exists(path)) { report.AppendLine($"| {step.Label} | (log missing) |||||||||||||"); continue; }

                List<Dictionary<string, string>> rows = LoadRows(path, step.Segment);
                if (rows.Count == 0) { report.AppendLine($"| {step.Label} | (no usable rows) |||||||||||||"); continue; }

                var engine = new NormalizedWheelLockSlipEngine();
                if (lockState != null) engine.LockScaleLearner.ImportAll(lockState);
                if (slipState != null) engine.SlipScaleLearner.ImportAll(slipState);
                if (anchorState != null) engine.LockAnchors.ImportAll(anchorState);

                var smaxCold = new List<double>(); var smaxWarm = new List<double>();
                var s75Cold = new List<double>(); var s75Warm = new List<double>();
                var s90Cold = new List<double>(); var s90Warm = new List<double>();
                double? smaxDoneAt = null, ratioDoneAt = null;
                double elapsed = 0.0;

                ITelemetryFrame previous = null;
                DateTime? previousTs = null;
                string game = step.GameId, car = step.CarId;

                foreach (Dictionary<string, string> row in rows)
                {
                    string rowCar = step.CarId ?? (row.TryGetValue("Diag.CarId", out string rc) ? rc : "");
                    car = rowCar;

                    DateTime ts = ParseTimestamp(row) ?? (previousTs ?? DateTime.UtcNow);
                    TimeSpan dt = previousTs.HasValue ? (ts - previousTs.Value) : TimeSpan.FromMilliseconds(16);
                    if (dt <= TimeSpan.Zero || dt > TimeSpan.FromSeconds(1)) dt = TimeSpan.FromMilliseconds(16);
                    previousTs = ts;
                    elapsed += dt.TotalSeconds;

                    ITelemetryFrame frame = BuildFrame(row);
                    var sample = new TelemetrySample(frame, previous ?? frame, ts, dt);
                    previous = frame;

                    engine.Compute(sample,
                        ReadCorners(row, "Diag.Source.Lock."), ReadCorners(row, "Diag.Source.Slip."),
                        game, rowCar,
                        lockSourceIdentity: step.Source, slipSourceIdentity: step.Source,
                        layer3RawLockWheels: ReadCorners(row, "WheelLock.Raw."),
                        layer3RawSlipWheels: ReadCorners(row, "WheelSlip.Raw."));

                    double? smax = engine.LockScaleLearner.LearnedCeiling(game, rowCar, step.Source, out _);
                    if (!smax.HasValue) continue;

                    double smaxConfidence = engine.LockScaleLearner.CeilingHandoverConfidence(game, rowCar, step.Source);
                    double ratioConfidence = engine.LockAnchors.PhysicalRatioConfidence(game, rowCar, step.Source);
                    if (smaxDoneAt == null && smaxConfidence >= HandoverDoneThreshold) smaxDoneAt = elapsed;
                    if (ratioDoneAt == null && ratioConfidence >= HandoverDoneThreshold) ratioDoneAt = elapsed;

                    double s75 = smax.Value * engine.LockAnchors.PhysicalS75Ratio(game, rowCar, step.Source, S75Seed);
                    double s90 = smax.Value * engine.LockAnchors.PhysicalS90Ratio(game, rowCar, step.Source, S90Seed);

                    bool warm = smaxDoneAt != null;
                    (warm ? smaxWarm : smaxCold).Add(smax.Value);
                    (warm ? s75Warm : s75Cold).Add(s75);
                    (warm ? s90Warm : s90Cold).Add(s90);
                }

                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |",
                    step.Label, Secs(smaxDoneAt), Secs(ratioDoneAt),
                    F(Mean(smaxCold)), F(Pct(smaxCold, 95)),
                    F(Mean(s75Cold)), F(Pct(s75Cold, 95)),
                    F(Mean(s90Cold)), F(Pct(s90Cold, 95)),
                    F(Mean(smaxWarm)), F(Pct(smaxWarm, 95)),
                    F(Mean(s75Warm)), F(Pct(s75Warm, 95)),
                    F(Mean(s90Warm)), F(Pct(s90Warm, 95))));

                lockState = engine.LockScaleLearner.ExportAll();
                slipState = engine.SlipScaleLearner.ExportAll();
                anchorState = engine.LockAnchors.ExportAll();
            }
            report.AppendLine();
        }

        /// <summary>Rows above the stationary threshold; when <paramref name="segment"/> is given, only
        /// that contiguous stint (stints are separated by gaps in the capture's own timestamps).</summary>
        private static List<Dictionary<string, string>> LoadRows(string path, int? segment)
        {
            List<Dictionary<string, string>> rows = ReadCsv(path);
            var moving = new List<Dictionary<string, string>>();
            var segments = new List<List<Dictionary<string, string>>>();
            DateTime? last = null;

            foreach (Dictionary<string, string> row in rows)
            {
                DateTime? ts = ParseTimestamp(row);
                if (last.HasValue && ts.HasValue && (ts.Value - last.Value) > TimeSpan.FromSeconds(5) && moving.Count > 0)
                {
                    segments.Add(moving);
                    moving = new List<Dictionary<string, string>>();
                }
                if ((ParseDouble(row, "Diag.Telemetry.SpeedKmh") ?? 0.0) >= MinSpeedKmh)
                {
                    moving.Add(row);
                    if (ts.HasValue) last = ts;
                }
            }
            if (moving.Count > 0) segments.Add(moving);
            segments = segments.Where(s => s.Count > 300).ToList();

            if (segment == null) return segments.SelectMany(s => s).ToList();
            return segment.Value < segments.Count ? segments[segment.Value] : new List<Dictionary<string, string>>();
        }

        private static string Secs(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";
        private static string F(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";
        private static double? Mean(List<double> v) => v.Count == 0 ? (double?)null : v.Average();

        private static double? Pct(List<double> values, double percentile)
        {
            if (values.Count == 0) return null;
            List<double> s = values.OrderBy(x => x).ToList();
            double pos = (s.Count - 1) * percentile / 100.0;
            int lo = (int)pos, hi = Math.Min(lo + 1, s.Count - 1);
            return lo == hi ? s[lo] : s[lo] + (s[hi] - s[lo]) * (pos - lo);
        }

        private static Corners ReadCorners(Dictionary<string, string> row, string prefix)
            => new Corners(
                ParseDouble(row, prefix + "FrontLeft") ?? 0.0,
                ParseDouble(row, prefix + "FrontRight") ?? 0.0,
                ParseDouble(row, prefix + "RearLeft") ?? 0.0,
                ParseDouble(row, prefix + "RearRight") ?? 0.0);

        private static ITelemetryFrame BuildFrame(Dictionary<string, string> row)
            => new TelemetryFrame(
                groundSpeedKmh: ParseDouble(row, "Diag.Telemetry.GroundSpeedKmh"),
                speedKmh: ParseDouble(row, "Diag.Telemetry.SpeedKmh"),
                brakePercent: ParseDouble(row, "Diag.Telemetry.BrakePercent"),
                throttlePercent: ParseDouble(row, "Diag.Telemetry.ThrottlePercent"),
                longitudinalG: ParseDouble(row, "Diag.Telemetry.LongitudinalG"),
                wheelOnLooseSurfaceFrontLeft: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.FrontLeft"),
                wheelOnLooseSurfaceFrontRight: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.FrontRight"),
                wheelOnLooseSurfaceRearLeft: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.RearLeft"),
                wheelOnLooseSurfaceRearRight: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.RearRight"));

        private static DateTime? ParseTimestamp(Dictionary<string, string> row)
        {
            if (!row.TryGetValue("TimestampUtc", out string s) || string.IsNullOrEmpty(s)) return null;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t) ? t : (DateTime?)null;
        }

        private static double? ParseDouble(Dictionary<string, string> row, string field)
        {
            if (!row.TryGetValue(field, out string s) || string.IsNullOrEmpty(s)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        private static bool? ParseBool(Dictionary<string, string> row, string field)
        {
            if (!row.TryGetValue(field, out string s) || string.IsNullOrEmpty(s)) return null;
            if (s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return null;
        }

        private static List<Dictionary<string, string>> ReadCsv(string path)
        {
            var result = new List<Dictionary<string, string>>();
            using (var reader = new StreamReader(path))
            {
                string headerLine = reader.ReadLine();
                if (headerLine == null) return result;
                string[] headers = headerLine.Split(',');
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    string[] cells = line.Split(',');
                    var row = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < headers.Length && i < cells.Length; i++) row[headers[i]] = cells[i];
                    result.Add(row);
                }
            }
            return result;
        }
    }
}
