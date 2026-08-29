using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.RawCalculator.Calibration
{
    /// <summary>
    /// A faithful port of SimHub's own
    /// <c>SimHub.Plugins.DataPlugins.ShakeItV3.Calibration.CalibrationDataProvider</c> - the object that
    /// owns every ShakeIt calibration, decides which one a given metric resolves to, and is the thing
    /// SimHub persists between runs.
    /// <para/>
    /// KEPT SIMHUB-FREE. SimHub's own version takes a <c>GameData</c>; this one takes the same values as
    /// plain parameters so <c>Core\</c> keeps its no-SimHub-dependency rule. Nothing else differs.
    /// <para/>
    /// TWO THINGS HERE ARE EASY TO GET WRONG, so they are called out:
    /// <list type="bullet">
    /// <item>THE SLIP CALIBRATION IS POOLED ACROSS ALL FOUR WHEELS. <see cref="GetSlipCalibration"/>
    /// accepts a <c>front</c> flag and deliberately ignores it, exactly as SimHub does - its key carries
    /// no wheel or axle segment. <see cref="GetRpsToSpeedCalibration"/> DOES split, appending
    /// "Front"/"Rear". SimHub splits where it intends to and pools where it intends to, and both are
    /// reproduced. This supersedes the earlier per-wheel Lock learners.</item>
    /// <item>LEARNING ALWAYS FEEDS THE LIVE CALIBRATION, never a preset. The preset resolution below
    /// happens only on the read path (<c>store: false</c>), matching SimHub's own placement of that
    /// block - so a shipped preset shapes what is PUBLISHED without ever absorbing observations.</item>
    /// </list>
    /// </summary>
    public sealed class CalibrationDataProvider
    {
        public const string SlipMetric = "Slip";
        public const string RpsToSpeedMetric = "RPSToSpeed";
        public const string FrontSuffix = "Front";
        public const string RearSuffix = "Rear";

        /// <summary>Minimum ground speed before ANY calibration is fed, from SimHub's own
        /// <c>Update</c>.</summary>
        public const double MinSpeedKmhToFeed = 2.0;

        /// <summary>The RPS-to-speed calibration only learns from light-throttle cruising, per SimHub's
        /// own gate (<c>Brake &lt; 5 &amp;&amp; SpeedKmh &gt; 50</c>).</summary>
        public const double RpsFeedMaxBrakePercent = 5.0;
        public const double RpsFeedMinSpeedKmh = 50.0;

        /// <summary>SimHub's own weight on live autocalibration wherever a shipped preset exists.</summary>
        public double AutocalibrationDataRatio { get; set; } = PreloadedCalibrationData.DefaultAutocalibrationDataRatio;

        /// <summary>Live, accumulating calibrations, keyed <c>track;car;metric</c> - this is the
        /// dictionary that persists between runs (SimHub serialises its equivalent as
        /// <c>CalibrationDataV5</c>).</summary>
        public Dictionary<string, CalibrationData> CalibrationData { get; private set; }
            = new Dictionary<string, CalibrationData>(StringComparer.Ordinal);

        /// <summary>Shipped presets for the CURRENT game, keyed by metric name only (never by track or
        /// car) - exactly SimHub's own scoping.</summary>
        public Dictionary<string, PreloadedCalibrationData> PrecalibrationData { get; private set; }
            = new Dictionary<string, PreloadedCalibrationData>(StringComparer.Ordinal);

        /// <summary>The running title's shipped wheel-speed-delta bounds, or null for SimHub's own
        /// defaults. Swapped per game exactly like <see cref="PrecalibrationData"/>.</summary>
        public GameCalibrationBounds GameBounds { get; private set; }

        public void SetGameBounds(GameCalibrationBounds bounds) => GameBounds = bounds;

        public void ImportCalibrations(Dictionary<string, CalibrationData> calibrations)
            => CalibrationData = calibrations ?? new Dictionary<string, CalibrationData>(StringComparer.Ordinal);

        public Dictionary<string, CalibrationData> ExportCalibrations() => CalibrationData;

        /// <summary>Installs the shipped presets for the game now being played. Clearing and re-setting
        /// per game matters: SimHub's presets are per-game, so a preset from one title must never leak
        /// into another.</summary>
        public void SetPrecalibration(Dictionary<string, PreloadedCalibrationData> presets)
            => PrecalibrationData = presets ?? new Dictionary<string, PreloadedCalibrationData>(StringComparer.Ordinal);

        /// <summary>SimHub's own key: track and car joined by ';', with EMPTY SEGMENTS REMOVED, then the
        /// metric appended. The empty-segment removal is not cosmetic - a title that reports no track id
        /// produces "car;metric", not ";car;metric", and those are different dictionary entries.</summary>
        public static string BuildKey(string trackIdWithConfig, string carModel, string metricName)
        {
            string composed = (trackIdWithConfig ?? string.Empty) + ";" + (carModel ?? string.Empty) + ";" + (metricName ?? string.Empty);
            return string.Join(";", composed.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <param name="front">Accepted and DELIBERATELY IGNORED - see this class's own remarks. Kept in
        /// the signature so the call sites read the same as SimHub's.</param>
        public ICalibrationData GetSlipCalibration(string trackIdWithConfig, string carModel, bool front, bool store = false)
            => Resolve(BuildKey(trackIdWithConfig, carModel, SlipMetric), SlipMetric, store);

        public ICalibrationData GetRpsToSpeedCalibration(string trackIdWithConfig, string carModel, bool front, bool store = false)
        {
            string metric = RpsToSpeedMetric + (front ? FrontSuffix : RearSuffix);
            return Resolve(BuildKey(trackIdWithConfig, carModel, metric), metric, store);
        }

        private ICalibrationData Resolve(string key, string metricName, bool store)
        {
            if (!CalibrationData.TryGetValue(key, out CalibrationData live))
            {
                live = new CalibrationData();
                // Only a STORING (learning) call creates a persistent entry. A read for a key never seen
                // returns a throwaway empty calibration rather than populating the dictionary - SimHub's
                // GetValueOrBuildDefault vs GetOrAdd distinction, which keeps the persisted file free of
                // entries for combinations that were only ever read.
                if (store) CalibrationData[key] = live;
            }

            // Learning always targets the live calibration - the preset never absorbs observations.
            if (store) return live;

            if (PrecalibrationData.TryGetValue(metricName, out PreloadedCalibrationData preset) && preset != null)
            {
                preset.AutoCalibrationData = live;
                preset.AutocalibrationDataRatio = AutocalibrationDataRatio;
                return preset;
            }

            return live;
        }

        /// <summary>
        /// Feeds every calibration this frame, mirroring SimHub's own <c>Update</c> - same order, same
        /// gates, same values, same 7000-positive-point feed cap.
        /// </summary>
        public void Update(
            string trackIdWithConfig, string carModel,
            bool capabilityWheelsRps, bool capabilityWheelsSlip,
            double? speedKmh, double? brakePercent,
            double?[] wheelRps, double?[] wheelSlip)
        {
            if (!(speedKmh > MinSpeedKmhToFeed)) return;

            if (capabilityWheelsRps && brakePercent < RpsFeedMaxBrakePercent && speedKmh > RpsFeedMinSpeedKmh && wheelRps != null)
            {
                for (int wheel = 0; wheel < 4; wheel++)
                {
                    if (!wheelRps[wheel].HasValue) continue;
                    double value = Math.Abs(wheelRps[wheel].Value / speedKmh.Value);
                    Feed(GetRpsToSpeedCalibration(trackIdWithConfig, carModel, wheel < 2, store: true), value);
                }
            }

            if (capabilityWheelsSlip && wheelSlip != null)
            {
                for (int wheel = 0; wheel < 4; wheel++)
                {
                    if (!wheelSlip[wheel].HasValue) continue;
                    // All four wheels into the SAME pooled calibration - see this class's own remarks.
                    Feed(GetSlipCalibration(trackIdWithConfig, carModel, wheel < 2, store: true), wheelSlip[wheel].Value);
                }
            }
        }

        /// <summary>SimHub's <c>UpdateData</c>: stop folding in new samples once the POSITIVE-point
        /// counter passes the ready threshold. Note the cap is on
        /// <see cref="Calibration.CalibrationData.CalibrationPointsAdded"/>, not on <c>Count</c>.</summary>
        private static void Feed(ICalibrationData calibration, double value)
        {
            if (!(calibration is CalibrationData live)) return;
            if (live.CalibrationPointsAdded <= Calibration.CalibrationData.ReadySamples) live.AddValue(value);
        }
    }
}
