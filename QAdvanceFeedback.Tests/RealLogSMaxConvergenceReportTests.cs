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
    /// REAL-LOG SMax/S75/S90 CONVERGENCE REPORT (docs\cross-channel-smax-report.md).
    /// <para/>
    /// Answers a question raw percentile statistics over a log CANNOT answer: what does the shipped
    /// learner ACTUALLY converge to? A percentile of the source column is what a perfect estimator
    /// would eventually see; the learned ceiling is what the real
    /// <see cref="KeyedScaleLearner"/> arrives at after its cold-start blend, its readiness ramp, its
    /// P95/P99 confidence blend, its forgetting histogram and its tier resolution have all had their
    /// say. The two are not the same number, and only the second one drives what the driver feels.
    /// <para/>
    /// SESSION BY SESSION, THROUGH PERSISTENCE. The owner's capture holds several driving sessions in
    /// one file (separated by gaps in the timestamp column). Each is replayed into a FRESH engine whose
    /// state is seeded from the previous session's <see cref="KeyedScaleLearner.ExportAll"/> /
    /// <see cref="LockAnchorLearner.ExportAll"/> - i.e. through the same export/import round trip that
    /// carries learning across a real SimHub restart, not merely by holding one engine object alive.
    /// A session's own source (our Raw, or a ShakeIt export) is detected from the data rather than
    /// assumed, because the two are keyed separately by the learner and must not be pooled.
    /// <para/>
    /// GRACEFUL WHEN LOGS ARE ABSENT - the CSVs live outside this repository (mirrors
    /// <see cref="RealLogTieredColdStartReportTests"/>'s own convention): with no access to them this
    /// passes trivially rather than failing a build that never had the inputs.
    /// </summary>
    public class RealLogSMaxConvergenceReportTests
    {
        private readonly ITestOutputHelper _output;
        public RealLogSMaxConvergenceReportTests(ITestOutputHelper output) { _output = output; }

        private const string LogPath =
            @"C:\Development\Repos\Samples\simhub\c_1_7_1_e_d\QAdvanceFeedback.session-20260825-201217.csv";

        /// <summary>A gap larger than this in the capture's own timestamp column starts a new session.</summary>
        private static readonly TimeSpan SessionGap = TimeSpan.FromSeconds(5);

        /// <summary>Below this speed the car is stationary/in the pits and teaches nothing worth reporting.</summary>
        private const double MinSpeedKmh = 3.0;

        [Fact]
        public void Report_learned_SMax_S75_S90_session_by_session_through_persistence()
        {
            if (!File.Exists(LogPath)) return; // graceful - see this class's own remarks.

            List<Dictionary<string, string>> rows = ReadCsv(LogPath);
            List<List<Dictionary<string, string>>> sessions = SplitSessions(rows);

            var report = new StringBuilder();
            report.AppendLine("# Learned SMax / S75 / S90 - full-pipeline replay, session by session");
            report.AppendLine();
            report.AppendLine("SMax rows are percentiles OF THE PER-FRAME LEARNED CEILING over the session");
            report.AppendLine("(the ceiling moves as the learner learns), not percentiles of the source column.");
            report.AppendLine();
            report.AppendLine("\"settled\" = median over the session's SECOND HALF, i.e. what the ceiling reads");
            report.AppendLine("most of the time once the cold-start ramp is behind it.");
            report.AppendLine();
            report.AppendLine("| # | source | frames | Lock settled | Lock P50 | Lock P95 | Lock P99 | Lock final | Slip settled | Slip P50 | Slip P95 | Slip P99 | Slip final | S75 | S90 | learned-in |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

            // Carried across sessions through the SAME export/import round trip a real restart uses.
            Dictionary<string, ScaleLearnerState> lockState = null, slipState = null;
            Dictionary<string, LockAnchorState> anchorState = null;

            int index = 0;
            foreach (List<Dictionary<string, string>> session in sessions)
            {
                index++;
                string sourceIdentity = DetectSourceIdentity(session);

                var engine = new NormalizedWheelLockSlipEngine();
                if (lockState != null) engine.LockScaleLearner.ImportAll(lockState);
                if (slipState != null) engine.SlipScaleLearner.ImportAll(slipState);
                if (anchorState != null) engine.LockAnchors.ImportAll(anchorState);

                var lockCeilings = new List<double>();
                var slipCeilings = new List<double>();
                string lastGame = "", lastCar = "";

                ITelemetryFrame previousFrame = null;
                DateTime? previousTimestamp = null;

                foreach (Dictionary<string, string> row in session)
                {
                    string gameId = Get(row, "Diag.GameId");
                    string carId = Get(row, "Diag.CarId");
                    lastGame = gameId; lastCar = carId;

                    DateTime ts = ParseTimestamp(row) ?? (previousTimestamp ?? DateTime.UtcNow);
                    TimeSpan dt = previousTimestamp.HasValue ? (ts - previousTimestamp.Value) : TimeSpan.FromMilliseconds(16);
                    if (dt <= TimeSpan.Zero || dt > TimeSpan.FromSeconds(1)) dt = TimeSpan.FromMilliseconds(16);
                    previousTimestamp = ts;

                    ITelemetryFrame frame = BuildFrame(row);
                    var sample = new TelemetrySample(frame, previousFrame ?? frame, ts, dt);
                    previousFrame = frame;

                    engine.Compute(
                        sample,
                        ReadCorners(row, "Diag.Source.Lock."),
                        ReadCorners(row, "Diag.Source.Slip."),
                        gameId, carId,
                        lockSourceIdentity: sourceIdentity, slipSourceIdentity: sourceIdentity,
                        layer3RawLockWheels: ReadCorners(row, "WheelLock.Raw."),
                        layer3RawSlipWheels: ReadCorners(row, "WheelSlip.Raw."));

                    double? lockCeiling = engine.LockScaleLearner.LearnedCeiling(gameId, carId, sourceIdentity, out _);
                    double? slipCeiling = engine.SlipScaleLearner.LearnedCeiling(gameId, carId, sourceIdentity, out _);
                    if (lockCeiling.HasValue) lockCeilings.Add(lockCeiling.Value);
                    if (slipCeiling.HasValue) slipCeilings.Add(slipCeiling.Value);
                }

                double? finalLock = engine.LockScaleLearner.LearnedCeiling(lastGame, lastCar, sourceIdentity, out _);
                double? finalSlip = engine.SlipScaleLearner.LearnedCeiling(lastGame, lastCar, sourceIdentity, out _);
                // The anchors that ACTUALLY drive the four-range curve - both off the same
                // confidence-weighted at-limit distribution SMax comes from (see
                // NormalizedWheelLockSlipEngine.TryBuildLockRangeCurveWithFallback). The legacy
                // LockAnchors.LearnedS75/S90 still learn and persist, but they measure a rising-branch
                // crossing and no longer place the curve's knots, so reporting them here would describe
                // something the driver never feels.
                bool anchorsReady = engine.LockScaleLearner
                    .PhysicalAnchorLevel(lastGame, lastCar, sourceIdentity, 50.0).HasValue;
                double s75Ratio = engine.LockAnchors.PhysicalS75Ratio(lastGame, lastCar, sourceIdentity, 0.50);
                double s90Ratio = engine.LockAnchors.PhysicalS90Ratio(lastGame, lastCar, sourceIdentity, 0.75);
                double ratioConfidence = engine.LockAnchors.PhysicalRatioConfidence(lastGame, lastCar, sourceIdentity);
                double? s75 = anchorsReady && finalLock.HasValue ? finalLock.Value * s75Ratio : (double?)null;
                double? s90 = anchorsReady && finalLock.HasValue ? finalLock.Value * s90Ratio : (double?)null;

                report.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} | {15} |",
                    index, sourceIdentity, session.Count,
                    F(Percentile(SecondHalf(lockCeilings), 50)),
                    F(Percentile(lockCeilings, 50)), F(Percentile(lockCeilings, 95)),
                    F(Percentile(lockCeilings, 99)), F(finalLock),
                    F(Percentile(SecondHalf(slipCeilings), 50)),
                    F(Percentile(slipCeilings, 50)), F(Percentile(slipCeilings, 95)),
                    F(Percentile(slipCeilings, 99)), F(finalSlip),
                    F(s75), F(s90), ratioConfidence.ToString("P0", CultureInfo.InvariantCulture)));

                lockState = engine.LockScaleLearner.ExportAll();
                slipState = engine.SlipScaleLearner.ExportAll();
                anchorState = engine.LockAnchors.ExportAll();
            }

            string text = report.ToString();
            _output.WriteLine(text);
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "qaf-smax-convergence-report.md"), text);
            }
            catch (IOException) { /* report is already on the test output - a scratch write is a convenience */ }

            Assert.True(sessions.Count > 0, "the capture should split into at least one session");
        }

        private static List<List<Dictionary<string, string>>> SplitSessions(List<Dictionary<string, string>> rows)
        {
            var sessions = new List<List<Dictionary<string, string>>>();
            var current = new List<Dictionary<string, string>>();
            DateTime? last = null;

            foreach (Dictionary<string, string> row in rows)
            {
                DateTime? ts = ParseTimestamp(row);
                if (last.HasValue && ts.HasValue && (ts.Value - last.Value) > SessionGap && current.Count > 0)
                {
                    sessions.Add(current);
                    current = new List<Dictionary<string, string>>();
                }
                double speed = ParseDouble(row, "Diag.Telemetry.SpeedKmh") ?? 0.0;
                if (speed >= MinSpeedKmh)
                {
                    current.Add(row);
                    if (ts.HasValue) last = ts;
                }
            }
            if (current.Count > 0) sessions.Add(current);

            // Sub-300-frame fragments are pit-lane noise between real stints, not sessions.
            return sessions.Where(s => s.Count > 300).ToList();
        }

        /// <summary>
        /// Which source this session was actually driving on. When the configured source IS our own Raw,
        /// the Diag.Source.* columns are a copy of the WheelLock.Raw.* columns; a ShakeIt export is an
        /// independent signal and diverges. Decided on the whole session rather than any single frame,
        /// since both agree trivially while every wheel reads zero.
        /// </summary>
        private static string DetectSourceIdentity(List<Dictionary<string, string>> session)
        {
            int compared = 0, identical = 0;
            foreach (Dictionary<string, string> row in session)
            {
                Corners source = ReadCorners(row, "Diag.Source.Lock.");
                Corners raw = ReadCorners(row, "WheelLock.Raw.");
                if (Math.Max(source.FrontLeft, Math.Max(source.FrontRight, Math.Max(source.RearLeft, source.RearRight))) <= 0.0) continue;
                compared++;
                if (Math.Abs(source.FrontLeft - raw.FrontLeft) < 1e-9 &&
                    Math.Abs(source.FrontRight - raw.FrontRight) < 1e-9 &&
                    Math.Abs(source.RearLeft - raw.RearLeft) < 1e-9 &&
                    Math.Abs(source.RearRight - raw.RearRight) < 1e-9) identical++;
            }
            return compared > 0 && identical > compared * 0.9 ? "Raw" : "ShakeIt";
        }

        /// <summary>The trailing half of a session's ceiling trace - the cold-start ramp lives in the
        /// leading half and would otherwise drag every whole-session statistic down toward it.</summary>
        private static List<double> SecondHalf(List<double> values)
            => values.Count < 2 ? values : values.Skip(values.Count / 2).ToList();

        private static double? Percentile(List<double> values, double percentile)
        {
            if (values.Count == 0) return null;
            List<double> sorted = values.OrderBy(v => v).ToList();
            double position = (sorted.Count - 1) * percentile / 100.0;
            int low = (int)position, high = Math.Min(low + 1, sorted.Count - 1);
            return low == high ? sorted[low] : sorted[low] + (sorted[high] - sorted[low]) * (position - low);
        }

        private static string F(double? v) => v.HasValue ? v.Value.ToString("F1", CultureInfo.InvariantCulture) : "-";

        private static string Get(Dictionary<string, string> row, string key)
            => row.TryGetValue(key, out string s) ? s : "";

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

        private static double? ParseDouble(Dictionary<string, string> row, string key)
        {
            if (!row.TryGetValue(key, out string s) || string.IsNullOrEmpty(s)) return null;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        private static bool? ParseBool(Dictionary<string, string> row, string key)
        {
            if (!row.TryGetValue(key, out string s) || string.IsNullOrEmpty(s)) return null;
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
                    for (int i = 0; i < headers.Length && i < cells.Length; i++)
                        row[headers[i]] = cells[i];
                    result.Add(row);
                }
            }
            return result;
        }
    }
}
