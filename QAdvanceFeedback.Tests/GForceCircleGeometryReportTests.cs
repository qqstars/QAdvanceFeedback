using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// A REPORT, not a pass/fail gate: replays a real session log through the live
    /// <see cref="GForceEngine"/> and measures where the braking sensation actually sits on the seat,
    /// against the owner's stated "G-force circle" expectation.
    /// <para/>
    /// THE OWNER'S MODEL. The three braking pads are points on a forward axis through the seat:
    /// Back Low at the CENTRE of the circle (radius 0), Bottom Front at ~85% of the radius forward, and
    /// Bottom Rear about halfway between (~42.5%). If the feel is a genuine circle, then as braking
    /// effort grows the sensation should TRAVEL FORWARD along that axis - quiet braking felt near the
    /// centre, hard braking felt out at 85%.
    /// <para/>
    /// The measurement is the intensity-weighted radial centroid:
    /// <c>sum(level_i x radius_i) / sum(level_i)</c>. A circle predicts it tracks the braking ratio; a
    /// fixed front-weighted shape predicts it barely moves.
    /// </summary>
    public class GForceCircleGeometryReportTests
    {
        private readonly ITestOutputHelper _out;
        public GForceCircleGeometryReportTests(ITestOutputHelper output) => _out = output;

        private const string LogPath = @"C:\Development\Repos\Samples\simhub\c_1_8_1_e_d\QAdvanceFeedback.session-20260831-195555.csv";

        // The owner's own geometry, as fractions of the circle's radius.
        private const double BackLowRadius = 0.00;
        private const double BottomRearRadius = 0.425;
        private const double BottomFrontRadius = 0.85;

        private const double AccelMax = 0.75;
        private const double DecelMax = 2.0;
        private const double LatMax = 1.5;

        private sealed class Frame
        {
            public DateTime T;
            public double Dt, Speed, LongG, LatG, Brake, Throttle;
        }

        private static List<Frame> Load()
        {
            var frames = new List<Frame>();
            // FileShare.ReadWrite deliberately: SimHub may still hold this log open for writing, and a
            // plain StreamReader(path) takes an exclusive-enough share mode to fail with IOException.
            using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                string[] header = reader.ReadLine().Split(',');
                int Col(string name) => Array.IndexOf(header, name);
                int iT = Col("TimestampUtc"), iSpeed = Col("Diag.Telemetry.GroundSpeedKmh");
                int iLong = Col("Diag.Telemetry.LongitudinalG"), iLat = Col("Diag.Telemetry.LateralG");
                int iBrake = Col("Diag.Telemetry.BrakePercent"), iThrottle = Col("Diag.Telemetry.ThrottlePercent");

                DateTime? prev = null;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] f = line.Split(',');
                    if (f.Length <= iThrottle) continue;

                    if (!DateTime.TryParse(f[iT], CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)) continue;

                    double D(int i) => double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0.0;

                    frames.Add(new Frame
                    {
                        T = t,
                        Dt = prev.HasValue ? (t - prev.Value).TotalSeconds : 0.016,
                        Speed = D(iSpeed), LongG = D(iLong), LatG = D(iLat),
                        Brake = D(iBrake), Throttle = D(iThrottle),
                    });
                    prev = t;
                }
            }
            return frames;
        }

        [Fact]
        public void Report_where_the_braking_sensation_actually_sits_on_the_seat()
        {
            if (!File.Exists(LogPath)) { _out.WriteLine("log not present - skipping"); return; }

            List<Frame> frames = Load();
            var engine = new GForceEngine();

            // Bucket by braking ratio, and record the radial centroid plus the raw pad levels.
            var buckets = new SortedDictionary<int, List<(double centroid, double bl, double br, double bf)>>();

            Frame prev = null;
            foreach (Frame f in frames)
            {
                var oldFrame = prev == null
                    ? new TelemetryFrame(groundSpeedKmh: f.Speed)
                    : new TelemetryFrame(groundSpeedKmh: prev.Speed);
                var newFrame = new TelemetryFrame(
                    groundSpeedKmh: f.Speed, longitudinalG: f.LongG, lateralG: f.LatG,
                    brakePercent: f.Brake, throttlePercent: f.Throttle);
                var sample = new TelemetrySample(newFrame, oldFrame, f.T,
                    TimeSpan.FromSeconds(f.Dt > 0 && f.Dt < 0.5 ? f.Dt : 0.016));

                GForceOutput r = engine.Compute(sample, AccelMax, DecelMax, latMaxG: LatMax);
                prev = f;

                // Braking frames only, and only where the chain is genuinely lit.
                if (f.Brake < 5.0) continue;
                double brakeRatio = Math.Min(1.0, Math.Abs(f.LongG) / DecelMax);
                if (engine.CurrentDirection != LongitudinalMotionState.Slowing) continue;

                double bl = (r.BackLowLeft.Value + r.BackLowRight.Value) / 2.0;
                double br = (r.BottomRearLeft.Value + r.BottomRearRight.Value) / 2.0;
                double bf = (r.BottomFrontLeft.Value + r.BottomFrontRight.Value) / 2.0;
                double total = bl + br + bf;
                if (total < 1.0) continue;

                double centroid = (bl * BackLowRadius + br * BottomRearRadius + bf * BottomFrontRadius) / total;

                int bucket = (int)Math.Min(9, brakeRatio * 10.0);
                if (!buckets.TryGetValue(bucket, out var list)) buckets[bucket] = list = new List<(double, double, double, double)>();
                list.Add((centroid, bl, br, bf));
            }

            _out.WriteLine("REPLAY OF THE REAL SESSION LOG THROUGH THE LIVE ENGINE");
            _out.WriteLine($"  frames {frames.Count}, decelMax {DecelMax}g, latMax {LatMax}g");
            _out.WriteLine("");
            _out.WriteLine("Owner's expected geometry:  BackLow r=0.00   BottomRear r=0.425   BottomFront r=0.85");
            _out.WriteLine("A real 'circle' means the CENTROID tracks the braking ratio (0.0 -> 0.85).");
            _out.WriteLine("");
            _out.WriteLine("brakeRatio |    n | centroid |  BackLow BottomRear BottomFront | loudest pad");
            _out.WriteLine("-----------+------+----------+---------------------------------+------------");

            foreach (var kv in buckets)
            {
                var list = kv.Value;
                double c = list.Average(x => x.centroid);
                double bl = list.Average(x => x.bl), br = list.Average(x => x.br), bf = list.Average(x => x.bf);
                string loudest = bf >= br && bf >= bl ? "BottomFront" : (br >= bl ? "BottomRear" : "BackLow");
                _out.WriteLine($"  {kv.Key / 10.0:F1}-{(kv.Key + 1) / 10.0:F1}  | {list.Count,4} |  {c,6:F3}  | {bl,8:F1} {br,10:F1} {bf,11:F1} | {loudest}");
            }

            _out.WriteLine("");
            if (buckets.Count >= 3)
            {
                double lowC = buckets.First().Value.Average(x => x.centroid);
                double highC = buckets.Last().Value.Average(x => x.centroid);
                _out.WriteLine($"CENTROID TRAVEL across the observed braking range: {lowC:F3} -> {highC:F3}  (span {highC - lowC:F3})");
                _out.WriteLine("Owner's model wants a span approaching 0.85. A span near 0 means the shape is");
                _out.WriteLine("fixed and only its VOLUME changes - i.e. not a circle.");
            }
        }
    }
}
