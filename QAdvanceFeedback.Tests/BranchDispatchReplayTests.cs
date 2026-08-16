using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.RawCalculator;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Replays the real, shipped <see cref="RawCalculatorEngine"/> - not a re-implementation, not an
    /// approximation - against a captured session log
    /// (<c>QAdvanceFeedback.session-20260815-230140.csv</c>), causally (frame order, online learners fed
    /// exactly once per frame, same as production), and compares this engine's own
    /// <c>WheelLock.Raw.All</c>/<c>WheelSlip.Raw.All</c> against the same log's
    /// <c>Diag.Source.Lock.All</c>/<c>Diag.Source.Slip.All</c> (SimHub's own ShakeIt Motors effect
    /// output for the same frames, captured independently in the same session).
    /// <para/>
    /// GRACEFUL WHEN THE LOG IS ABSENT: the CSV lives outside this repository (a large, environment-
    /// specific capture, not something to commit) - if it cannot be found at any of the candidate paths
    /// below, this test passes trivially rather than failing a build/CI run that has no access to it.
    /// </summary>
    public class BranchDispatchReplayTests
    {
        private const string LogFileName = "QAdvanceFeedback.session-20260815-230140.csv";

        [Fact]
        public void Replay_of_the_captured_session_matches_ShakeIts_real_Lock_and_Slip_closely()
        {
            string path = FindLogFile();
            if (path == null) return; // see this class's own remarks - graceful, not a failure.

            List<Dictionary<string, string>> rows = ReadCsv(path);
            Assert.True(rows.Count > 1000, $"expected a substantial capture, got {rows.Count} rows");

            var engine = new RawCalculatorEngine();

            var lockPairsBraking = new List<(double ours, double source)>();
            var slipPairsBraking = new List<(double ours, double source)>();

            ITelemetryFrame previous = null;
            DateTime frameTime = DateTime.UtcNow;

            foreach (Dictionary<string, string> row in rows)
            {
                ITelemetryFrame current = BuildFrame(row);
                RawWheelTelemetrySnapshot snapshot = BuildSnapshot(row);
                var sample = new TelemetrySample(current, previous ?? current, frameTime, TimeSpan.FromMilliseconds(16));
                frameTime = frameTime.AddMilliseconds(16);

                LegacyWheelLockSlipResult result = engine.Compute(sample, null, null, null, snapshot);

                double? sourceLockAll = ParseDouble(row, "Diag.Source.Lock.All");
                double? sourceSlipAll = ParseDouble(row, "Diag.Source.Slip.All");
                string direction = row.TryGetValue("Diag.Direction", out string d) ? d : null;

                if (sourceLockAll.HasValue && direction == "Slowing")
                    lockPairsBraking.Add((result.LockAll, sourceLockAll.Value));
                if (sourceSlipAll.HasValue && direction == "Slowing")
                    slipPairsBraking.Add((result.SlipAll, sourceSlipAll.Value));

                previous = current;
            }

            ErrorStats lockBraking = ComputeStats(lockPairsBraking);
            ErrorStats slipBraking = ComputeStats(slipPairsBraking);

            Assert.True(lockBraking.MeanError < 12.0, $"Lock braking mean error too high: {lockBraking}");
            Assert.True(lockBraking.Correlation > 0.85, $"Lock braking correlation too low: {lockBraking}");
            Assert.True(slipBraking.MeanError < 16.0, $"Slip braking mean error too high: {slipBraking}");
            Assert.True(slipBraking.Correlation > 0.75, $"Slip braking correlation too low: {slipBraking}");

            RawWheelTelemetrySnapshot lastSnapshot = BuildSnapshot(rows[rows.Count - 1]);
            Assert.Equal(WheelSlipBranchNames.Rps, WheelSlipBranchSelector.Select(lastSnapshot, isLock: true));
            Assert.Equal(WheelSlipBranchNames.SlipData, WheelSlipBranchSelector.Select(lastSnapshot, isLock: false));
        }

        /// <summary>
        /// On this session's own capability shape, Slip moves from the pedal+speed RPM-derived formula
        /// (throttle-only by default) to the learned-distribution branch. Measured directly, comparing
        /// the CURRENTLY-SHIPPED <c>WheelSlip.Raw.All</c> column (this log's own recorded value) against
        /// the dispatch's output on the identical frames - a real behaviour change on titles with this
        /// capability shape, kept because it tracks SimHub's own real output far more closely (see the
        /// replay test above), reported explicitly rather than silently absorbed.
        /// </summary>
        [Fact]
        public void Slip_branch_dispatch_changes_the_previous_default_formulas_output_on_this_capability_shape()
        {
            string path = FindLogFile();
            if (path == null) return;

            List<Dictionary<string, string>> rows = ReadCsv(path);
            var engine = new RawCalculatorEngine();

            var speedingUp = new List<(double oldShipped, double newDispatch)>();
            var braking = new List<(double oldShipped, double newDispatch)>();

            ITelemetryFrame previous = null;
            DateTime frameTime = DateTime.UtcNow;
            foreach (Dictionary<string, string> row in rows)
            {
                ITelemetryFrame current = BuildFrame(row);
                RawWheelTelemetrySnapshot snapshot = BuildSnapshot(row);
                var sample = new TelemetrySample(current, previous ?? current, frameTime, TimeSpan.FromMilliseconds(16));
                frameTime = frameTime.AddMilliseconds(16);

                LegacyWheelLockSlipResult result = engine.Compute(sample, null, null, null, snapshot);
                double? oldShipped = ParseDouble(row, "WheelSlip.Raw.All");
                string direction = row.TryGetValue("Diag.Direction", out string d) ? d : null;

                if (oldShipped.HasValue)
                {
                    if (direction == "SpeedingUp") speedingUp.Add((oldShipped.Value, result.SlipAll));
                    else if (direction == "Slowing") braking.Add((oldShipped.Value, result.SlipAll));
                }
                previous = current;
            }

            Assert.True(speedingUp.Count > 100 && braking.Count > 100, "expected both regimes represented");

            double speedingUpMeanDiff = speedingUp.Average(p => Math.Abs(p.oldShipped - p.newDispatch));
            double brakingMeanDiff = braking.Average(p => Math.Abs(p.oldShipped - p.newDispatch));

            Assert.True(speedingUpMeanDiff > 0.5, $"expected a measurable SpeedingUp-regime change, got {speedingUpMeanDiff:F3}");
            Assert.True(brakingMeanDiff > 5.0, $"expected a measurable braking-regime change, got {brakingMeanDiff:F3}");
        }

        private readonly struct ErrorStats
        {
            public readonly double MeanError, MedianError, P90Error, Correlation;
            public readonly int N;
            public ErrorStats(double mean, double median, double p90, double corr, int n)
            {
                MeanError = mean; MedianError = median; P90Error = p90; Correlation = corr; N = n;
            }
            public override string ToString() => $"n={N} mean={MeanError:F2} median={MedianError:F2} p90={P90Error:F2} corr={Correlation:F3}";
        }

        private static ErrorStats ComputeStats(List<(double ours, double source)> pairs)
        {
            if (pairs.Count == 0) return new ErrorStats(0, 0, 0, double.NaN, 0);

            List<double> errors = pairs.Select(p => Math.Abs(p.ours - p.source)).OrderBy(e => e).ToList();
            double mean = errors.Average();
            double median = errors[errors.Count / 2];
            double p90 = errors[Math.Min(errors.Count - 1, (int)Math.Ceiling(0.9 * errors.Count) - 1)];

            double meanX = pairs.Average(p => p.ours);
            double meanY = pairs.Average(p => p.source);
            double cov = pairs.Sum(p => (p.ours - meanX) * (p.source - meanY));
            double sx = Math.Sqrt(pairs.Sum(p => (p.ours - meanX) * (p.ours - meanX)));
            double sy = Math.Sqrt(pairs.Sum(p => (p.source - meanY) * (p.source - meanY)));
            double corr = (sx > 1e-9 && sy > 1e-9) ? cov / (sx * sy) : double.NaN;

            return new ErrorStats(mean, median, p90, corr, pairs.Count);
        }

        private static string FindLogFile()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", LogFileName),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", LogFileName),
                @"C:\Development\Repos\Samples\simhub\" + LogFileName,
            };
            foreach (string c in candidates)
            {
                string full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
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

        private static string ParseString(Dictionary<string, string> row, string key)
        {
            return row.TryGetValue(key, out string s) && !string.IsNullOrEmpty(s) ? s : null;
        }

        private static ITelemetryFrame BuildFrame(Dictionary<string, string> row)
        {
            return new TelemetryFrame(
                groundSpeedKmh: ParseDouble(row, "Diag.Telemetry.GroundSpeedKmh"),
                speedKmh: ParseDouble(row, "Diag.Telemetry.SpeedKmh"),
                groundSpeedMps: ParseDouble(row, "Diag.Telemetry.GroundSpeedMps"),
                rpm: ParseDouble(row, "Diag.Telemetry.Rpm"),
                gear: ParseString(row, "Diag.Telemetry.Gear"),
                brakePercent: ParseDouble(row, "Diag.Telemetry.BrakePercent"),
                throttlePercent: ParseDouble(row, "Diag.Telemetry.ThrottlePercent"),
                clutchPercent: ParseDouble(row, "Diag.Telemetry.ClutchPercent"),
                lateralLocalVelocity: ParseDouble(row, "Diag.Telemetry.LateralLocalVelocity"),
                longitudinalG: ParseDouble(row, "Diag.Telemetry.LongitudinalG"),
                lateralG: ParseDouble(row, "Diag.Telemetry.LateralG"));
        }

        private static RawWheelTelemetrySnapshot BuildSnapshot(Dictionary<string, string> row)
        {
            return new RawWheelTelemetrySnapshot(
                wheelRpsFrontLeft: ParseDouble(row, "Diag.Telemetry.WheelRPS.FrontLeft"),
                wheelRpsFrontRight: ParseDouble(row, "Diag.Telemetry.WheelRPS.FrontRight"),
                wheelRpsRearLeft: ParseDouble(row, "Diag.Telemetry.WheelRPS.RearLeft"),
                wheelRpsRearRight: ParseDouble(row, "Diag.Telemetry.WheelRPS.RearRight"),
                wheelSpeedFrontLeft: ParseDouble(row, "Diag.Telemetry.WheelSpeed.FrontLeft"),
                wheelSpeedFrontRight: ParseDouble(row, "Diag.Telemetry.WheelSpeed.FrontRight"),
                wheelSpeedRearLeft: ParseDouble(row, "Diag.Telemetry.WheelSpeed.RearLeft"),
                wheelSpeedRearRight: ParseDouble(row, "Diag.Telemetry.WheelSpeed.RearRight"),
                wheelSlipRatioFrontLeft: ParseDouble(row, "Diag.Telemetry.WheelSlipRatio.FrontLeft"),
                wheelSlipRatioFrontRight: ParseDouble(row, "Diag.Telemetry.WheelSlipRatio.FrontRight"),
                wheelSlipRatioRearLeft: ParseDouble(row, "Diag.Telemetry.WheelSlipRatio.RearLeft"),
                wheelSlipRatioRearRight: ParseDouble(row, "Diag.Telemetry.WheelSlipRatio.RearRight"),
                capabilityWheelsSlip: ParseBool(row, "Diag.Capabilities.WheelsSlip"),
                capabilityWheelsRPS: ParseBool(row, "Diag.Capabilities.WheelsRPS"),
                capabilityWheelsSpeed: ParseBool(row, "Diag.Capabilities.WheelsSpeed"),
                capabilityWheelsSlipDirectMode: ParseBool(row, "Diag.Capabilities.WheelsSlipDirectMode"),
                capabilityWheelSlipUseSimpleBraking: ParseBool(row, "Diag.Capabilities.WheelSlipUseSimpleBraking"),
                capabilityDetectLockFromWheelsSpeed: ParseBool(row, "Diag.Capabilities.DetectLockFromWheelsSpeed"),
                capabilityDetectLockFromWheelsRPSAndDummyRadius: ParseBool(row, "Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius"),
                capabilitySpeed: ParseBool(row, "Diag.Capabilities.Speed"),
                capabilityRpm: ParseBool(row, "Diag.Capabilities.Rpm"),
                capabilityGameFamily: ParseString(row, "Diag.Capabilities.GameFamily"),
                capabilityWheelSlipCalibrationProviderSlipScale: ParseDouble(row, "Diag.Capabilities.WheelSlipCalibrationProviderSlipScale"));
        }
    }
}
