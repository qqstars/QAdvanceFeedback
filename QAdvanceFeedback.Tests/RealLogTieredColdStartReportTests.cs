using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// REAL-LOG REPLAY for the v1.0.7 tiered cold-start reference system
    /// (docs\v107-tiered-coldstart-report.md). Replays the real, shipped pipeline (Layer 4 +
    /// Layer 5 - <see cref="NormalizedWheelLockSlipEngine"/>/<see cref="ProjectedWheelLockSlipEngine"/>)
    /// against the owner's own captured session logs, feeding EACH frame's already-resolved
    /// Diag.Source.Lock/Slip.* (the configured source Layer 4 actually consumed) and WheelLock/Slip.Raw.*
    /// (Layer 3's own always-computed fallback) columns straight back in - causally, frame order, exactly
    /// once per frame, same as production.
    /// <para/>
    /// Compares the SAME replay run TWICE per log: once with the tiered resolver forced to Tier 1
    /// (<see cref="KeyedScaleLearner.ForceTier1ForTesting"/> - reproducing pre-1.0.7 behaviour exactly)
    /// and once with it enabled, reporting the first ~60 seconds' Projected.All peak and above-90
    /// fraction, before and after, plus which tier each replay actually resolved to.
    /// <para/>
    /// GRACEFUL WHEN LOGS ARE ABSENT (mirrors <c>BranchDispatchReplayTests</c>'s own convention) - the
    /// CSVs live outside this repository; if none can be found, this test passes trivially rather than
    /// failing a build/CI run that has no access to them. When run with access to the logs, it also
    /// writes a detailed report to a scratch file for the accompanying markdown report to quote from.
    /// </summary>
    public class RealLogTieredColdStartReportTests
    {
        private readonly ITestOutputHelper _output;
        public RealLogTieredColdStartReportTests(ITestOutputHelper output) { _output = output; }

        private static readonly string[] RootCandidates =
        {
            @"C:\Development\Repos\Samples\simhub\",
        };

        private sealed class LogSpec
        {
            public string RelativePath;
            public string GameId;
            public string CarId; // null = read from Diag.CarId per row
            public string SourceIdentity; // "ShakeIt" or "Raw"
            public int? TruncateAtRow; // 1-indexed data row (excl. header); keep rows [1..TruncateAtRow-1]
            public string Label;
        }

        private static readonly LogSpec[] Specs =
        {
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_5_e_d\QAdvanceFeedback.session-20260819-174052_1.0.6.5_ShakeIt_BestOverall_Stable.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "ShakeIt", Label = "I_1_6_5 ShakeIt (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_5_e_d\QAdvanceFeedback.session-20260819-174358_1.0.6.5_Raw_BestOverall_Stable_StartShakeTooStrong.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "Raw", Label = "I_1_6_5 Raw (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\l_1_6_6_e_d\QAdvanceFeedback.session-20260819-174849_1.0.6.6_Raw_GenerallyGood.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "Raw", Label = "l_1_6_6 Raw (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\l_1_6_6_e_d\QAdvanceFeedback.session-20260819-175151_1.0.6.6_ShakeIt_Solid_LittleEarly.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "ShakeIt", Label = "l_1_6_6 ShakeIt (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_7_e_d\QAdvanceFeedback.session-20260819-170515_1.0.6.7_Raw_little_Late.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "Raw", Label = "I_1_6_7 Raw (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_7_e_d\QAdvanceFeedback.session-20260819-170837_1.0.6.7_ShakeIt_StartStageShake_LockingNotShake.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "ShakeIt", Label = "I_1_6_7 ShakeIt (RedBull/Dry/Silverstone)" },
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_8_e_d\QAdvanceFeedback.session-20260819-171243_1.0.6.8_Raw_NoLockAtAll_NoGForce_Detected.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "Raw", TruncateAtRow = 3613, Label = "I_1_6_8 Raw (RedBull/Dry/Silverstone) [truncated at restart, row 3613]" },
            new LogSpec { RelativePath = @"1.0.6_logs\I_1_6_8_e_d\QAdvanceFeedback.session-20260819-171720_1.0.6.8_ShakeIt_NoLockAtAll_NoGForce.csv", GameId = "F12025", CarId = "Red Bull Racing", SourceIdentity = "ShakeIt", Label = "I_1_6_8 ShakeIt (RedBull/Dry/Silverstone)" },

            new LogSpec { RelativePath = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-122552-withRawWet.csv", GameId = "", CarId = "", SourceIdentity = "Raw", Label = "Common Raw Wet [inferred]" },
            new LogSpec { RelativePath = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-122825-WithShakeItWet.csv", GameId = "", CarId = "", SourceIdentity = "ShakeIt", Label = "Common ShakeIt Wet [inferred]" },
            new LogSpec { RelativePath = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-123120-WithRawDry.csv", GameId = "", CarId = "", SourceIdentity = "Raw", Label = "Common Raw Dry [inferred]" },
            new LogSpec { RelativePath = @"Common_1_5_e_d\QAdvanceFeedback.session-20260816-123345-WithShakeItDry.csv", GameId = "", CarId = "", SourceIdentity = "ShakeIt", TruncateAtRow = 3197, Label = "Common ShakeIt Dry [inferred] [truncated at restart, row 3197]" },

            new LogSpec { RelativePath = @"c_1_5_3_e_d\QAdvanceFeedback.session-20260816-211733_ShakeIt.csv", GameId = "F12025", CarId = null, SourceIdentity = "ShakeIt", Label = "c_1_5_3 ShakeIt (Sauber=Wet/Silverstone then F1 Generic=Dry/AbuDhabi)" },
            new LogSpec { RelativePath = @"c_1_5_3_e_d\QAdvanceFeedback.session-20260816-212439_Raw.csv", GameId = "F12025", CarId = null, SourceIdentity = "Raw", Label = "c_1_5_3 Raw (Sauber=Wet/Silverstone then F1 Generic=Dry/AbuDhabi)" },
        };

        [Fact]
        public void Replay_all_usable_logs_and_report_before_after_tiered_coldstart_behaviour()
        {
            string root = FindRoot();
            if (root == null) return; // graceful - see this class's own remarks.

            var report = new System.Text.StringBuilder();
            report.AppendLine("# v1.0.7 tiered cold-start real-log replay results");
            report.AppendLine();

            int totalLogsAttempted = 0, totalLogsFound = 0;

            foreach (LogSpec spec in Specs)
            {
                string path = Path.Combine(root, spec.RelativePath);
                totalLogsAttempted++;
                if (!File.Exists(path))
                {
                    report.AppendLine($"## {spec.Label} - FILE NOT FOUND ({path})");
                    continue;
                }
                totalLogsFound++;

                List<Dictionary<string, string>> rows = ReadCsv(path);
                int originalCount = rows.Count;
                if (spec.TruncateAtRow.HasValue)
                {
                    int keep = Math.Min(rows.Count, spec.TruncateAtRow.Value - 1);
                    int dropped = rows.Count - keep;
                    rows = rows.Take(keep).ToList();
                    report.AppendLine($"## {spec.Label}");
                    report.AppendLine($"- Rows: {originalCount} total, TRUNCATED at restart to {keep} rows ({dropped} dropped).");
                }
                else
                {
                    report.AppendLine($"## {spec.Label}");
                    report.AppendLine($"- Rows: {originalCount}.");
                }

                RunAndReport(report, rows, spec.GameId, spec.CarId, spec.SourceIdentity, "  ");
                report.AppendLine();
            }

            report.AppendLine($"Logs attempted: {totalLogsAttempted}, found: {totalLogsFound}.");

            string outPath = Path.Combine(Path.GetTempPath(), "qaf_v107_reallog_report.md");
            File.WriteAllText(outPath, report.ToString());
            _output.WriteLine("Report written to: " + outPath);
            _output.WriteLine(report.ToString());

            Assert.True(totalLogsFound > 0, "expected at least one real log to be found and replayed");
        }

        /// <summary>Runs ONE (game,car,source) segment TWICE (Tier-1-forced "before", tiered "after")
        /// through independent, freshly-constructed engines, and appends the comparison to
        /// <paramref name="report"/>.</summary>
        private static void RunAndReport(System.Text.StringBuilder report, List<Dictionary<string, string>> rows,
            string gameId, string carId, string sourceIdentity, string indent)
        {
            if (rows.Count == 0) { report.AppendLine(indent + "(no rows)"); return; }

            // ONE SHARED ENGINE PER PASS (before/after), fed the FULL row set in original order, with
            // carId read PER ROW when spec.CarId is null (c_1_5_3_e_d - two cars, one after another, in
            // the SAME session/engine, exactly as a real multi-car SimHub session would be) - this is
            // what lets the SECOND car's own key see the FIRST car's already-learned reference. Stats are
            // still tracked PER CONTIGUOUS CAR SEGMENT (each car's own "lap" gets its own first-60s clock).
            Dictionary<string, List<SegmentResult>> before = Replay(rows, gameId, carId, sourceIdentity, forceTier1: true);
            Dictionary<string, List<SegmentResult>> after = Replay(rows, gameId, carId, sourceIdentity, forceTier1: false);

            foreach (string segmentCar in after.Keys)
            {
                List<SegmentResult> beforeSegs = before[segmentCar];
                List<SegmentResult> afterSegs = after[segmentCar];
                string carLabel = carId ?? segmentCar;
                report.AppendLine($"{indent}Car segment: {carLabel}");
                for (int i = 0; i < afterSegs.Count; i++)
                {
                    SegmentResult b = beforeSegs[i];
                    SegmentResult a = afterSegs[i];
                    report.AppendLine($"{indent}- Resolved tier (Lock/Slip): {a.FinalLockTier} / {a.FinalSlipTier}");
                    report.AppendLine($"{indent}- First ~60s LOCK Projected.All peak: before={b.EarlyLockPeak:F1} after={a.EarlyLockPeak:F1}; >90 fraction: before={b.EarlyLockAbove90:P1} after={a.EarlyLockAbove90:P1}");
                    report.AppendLine($"{indent}- Rest-of-lap LOCK Projected.All peak: before={b.RestLockPeak:F1} after={a.RestLockPeak:F1}; >90 fraction: before={b.RestLockAbove90:P1} after={a.RestLockAbove90:P1}");
                    report.AppendLine($"{indent}- First ~60s SLIP Projected.All peak: before={b.EarlySlipPeak:F1} after={a.EarlySlipPeak:F1}; >90 fraction: before={b.EarlySlipAbove90:P1} after={a.EarlySlipAbove90:P1}");
                    report.AppendLine($"{indent}- Rest-of-lap SLIP Projected.All peak: before={b.RestSlipPeak:F1} after={a.RestSlipPeak:F1}; >90 fraction: before={b.RestSlipAbove90:P1} after={a.RestSlipAbove90:P1}");
                }
            }
        }

        private struct SegmentResult
        {
            public double EarlyLockPeak, RestLockPeak, EarlySlipPeak, RestSlipPeak;
            public double EarlyLockAbove90, RestLockAbove90, EarlySlipAbove90, RestSlipAbove90;
            public ColdStartTier FinalLockTier, FinalSlipTier;
        }

        /// <summary>Replays the FULL row set through ONE shared engine, returning per-contiguous-car-
        /// segment results keyed by carId (a single "" key when <paramref name="fixedCarId"/> is
        /// supplied). Segment boundaries are wherever Diag.CarId changes (or the whole set, for a fixed
        /// car) - each segment's own "first ~60 seconds" clock starts at that segment's own first frame,
        /// while the ENGINE (and its KeyedScaleLearner state) carries over continuously across segments,
        /// exactly like a real multi-car SimHub session.</summary>
        private static Dictionary<string, List<SegmentResult>> Replay(List<Dictionary<string, string>> rows, string gameId, string fixedCarId, string sourceIdentity, bool forceTier1)
        {
            bool previousFlag = KeyedScaleLearner.ForceTier1ForTesting;
            KeyedScaleLearner.ForceTier1ForTesting = forceTier1;
            try
            {
                var normalizedEngine = new NormalizedWheelLockSlipEngine();
                var lockProjector = new OutputProjector(IdentitySettings());
                var slipProjector = new OutputProjector(IdentitySettings());
                var projectedEngine = new ProjectedWheelLockSlipEngine(
                    lockProjector, new PulseSettings { Enabled = false }, slipProjector, new PulseSettings { Enabled = false });

                var resultsByCar = new Dictionary<string, List<SegmentResult>>(StringComparer.Ordinal);

                var earlyLock = new List<double>();
                var restLock = new List<double>();
                var earlySlip = new List<double>();
                var restSlip = new List<double>();
                string currentSegmentCar = null;
                DateTime? segmentFirstTimestamp = null;

                ITelemetryFrame previousFrame = null;
                DateTime? previousTimestamp = null;

                void FlushSegment()
                {
                    if (currentSegmentCar == null) return;
                    if (!resultsByCar.TryGetValue(currentSegmentCar, out List<SegmentResult> list))
                        resultsByCar[currentSegmentCar] = list = new List<SegmentResult>();
                    list.Add(new SegmentResult
                    {
                        EarlyLockPeak = earlyLock.Count > 0 ? earlyLock.Max() : 0.0,
                        RestLockPeak = restLock.Count > 0 ? restLock.Max() : 0.0,
                        EarlySlipPeak = earlySlip.Count > 0 ? earlySlip.Max() : 0.0,
                        RestSlipPeak = restSlip.Count > 0 ? restSlip.Max() : 0.0,
                        EarlyLockAbove90 = Fraction(earlyLock, 90.0),
                        RestLockAbove90 = Fraction(restLock, 90.0),
                        EarlySlipAbove90 = Fraction(earlySlip, 90.0),
                        RestSlipAbove90 = Fraction(restSlip, 90.0),
                        FinalLockTier = normalizedEngine.LockColdStartTier,
                        FinalSlipTier = normalizedEngine.SlipColdStartTier,
                    });
                    earlyLock.Clear(); restLock.Clear(); earlySlip.Clear(); restSlip.Clear();
                }

                foreach (Dictionary<string, string> row in rows)
                {
                    string rowCarId = fixedCarId ?? (row.TryGetValue("Diag.CarId", out string rc) ? rc : "");
                    if (rowCarId != currentSegmentCar)
                    {
                        FlushSegment();
                        currentSegmentCar = rowCarId;
                        segmentFirstTimestamp = null;
                    }

                    DateTime ts = ParseTimestamp(row) ?? (previousTimestamp ?? DateTime.UtcNow);
                    if (segmentFirstTimestamp == null) segmentFirstTimestamp = ts;
                    TimeSpan dt = previousTimestamp.HasValue ? (ts - previousTimestamp.Value) : TimeSpan.FromMilliseconds(16);
                    if (dt <= TimeSpan.Zero || dt > TimeSpan.FromSeconds(1)) dt = TimeSpan.FromMilliseconds(16);
                    previousTimestamp = ts;

                    ITelemetryFrame frame = BuildFrame(row);
                    var sample = new TelemetrySample(frame, previousFrame ?? frame, ts, dt);
                    previousFrame = frame;

                    Corners sourceLock = ReadCorners(row, "Diag.Source.Lock.");
                    Corners sourceSlip = ReadCorners(row, "Diag.Source.Slip.");
                    Corners rawLock = ReadCorners(row, "WheelLock.Raw.");
                    Corners rawSlip = ReadCorners(row, "WheelSlip.Raw.");

                    NormalizedWheelLockSlipResult normalized = normalizedEngine.Compute(
                        sample, sourceLock, sourceSlip, gameId, rowCarId,
                        lockSourceIdentity: sourceIdentity, slipSourceIdentity: sourceIdentity,
                        layer3RawLockWheels: rawLock, layer3RawSlipWheels: rawSlip);

                    ProjectedWheelLockSlipResult projected = projectedEngine.Compute(
                        normalized, dt.TotalSeconds,
                        normalizedEngine.LockColdStartConfidence, normalizedEngine.SlipColdStartConfidence,
                        normalizedEngine.LockColdStartFloor, normalizedEngine.SlipColdStartFloor);

                    double elapsed = (ts - segmentFirstTimestamp.Value).TotalSeconds;
                    (elapsed <= 60.0 ? earlyLock : restLock).Add(projected.LockAll);
                    (elapsed <= 60.0 ? earlySlip : restSlip).Add(projected.SlipAll);
                }
                FlushSegment();

                return resultsByCar;
            }
            finally
            {
                KeyedScaleLearner.ForceTier1ForTesting = previousFlag;
            }
        }

        private static double Fraction(List<double> values, double threshold)
            => values.Count == 0 ? 0.0 : values.Count(v => v > threshold) / (double)values.Count;

        private static ProjectorSettings IdentitySettings()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear);
            return s;
        }

        private static Corners ReadCorners(Dictionary<string, string> row, string prefix)
            => new Corners(
                ParseDouble(row, prefix + "FrontLeft") ?? 0.0,
                ParseDouble(row, prefix + "FrontRight") ?? 0.0,
                ParseDouble(row, prefix + "RearLeft") ?? 0.0,
                ParseDouble(row, prefix + "RearRight") ?? 0.0);

        private static ITelemetryFrame BuildFrame(Dictionary<string, string> row)
        {
            return new TelemetryFrame(
                groundSpeedKmh: ParseDouble(row, "Diag.Telemetry.GroundSpeedKmh"),
                speedKmh: ParseDouble(row, "Diag.Telemetry.SpeedKmh"),
                brakePercent: ParseDouble(row, "Diag.Telemetry.BrakePercent"),
                throttlePercent: ParseDouble(row, "Diag.Telemetry.ThrottlePercent"),
                longitudinalG: ParseDouble(row, "Diag.Telemetry.LongitudinalG"),
                wheelOnLooseSurfaceFrontLeft: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.FrontLeft"),
                wheelOnLooseSurfaceFrontRight: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.FrontRight"),
                wheelOnLooseSurfaceRearLeft: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.RearLeft"),
                wheelOnLooseSurfaceRearRight: ParseBool(row, "Diag.Telemetry.WheelOnLooseSurface.RearRight"));
        }

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

        private static string FindRoot()
        {
            foreach (string c in RootCandidates)
                if (Directory.Exists(c)) return c;
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
