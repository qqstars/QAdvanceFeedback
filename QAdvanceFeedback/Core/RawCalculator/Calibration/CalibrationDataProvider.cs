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

        /// <summary>Takes the loaded calibrations; each entry learns which metric it is (from its key) and a finished one
        /// is compacted (see <see cref="Calibration.CalibrationData.Compact"/>).</summary>
        /// <param name="fromOlderVersion">The file is older than Version 21, whose keys have no game (owner, 2026-11-25):
        /// an entry keyed by a SimHub-generated <c>DT_</c> track or <c>DC_</c> car is dropped (it can never be met
        /// again), every other one is kept as a LEGACY entry and moved under its game the first time that track and car
        /// are driven or read (see <see cref="Resolve"/>).</param>
        public void ImportCalibrations(Dictionary<string, CalibrationData> calibrations, bool fromOlderVersion = false)
        {
            CalibrationData = calibrations ?? new Dictionary<string, CalibrationData>(StringComparer.Ordinal);
            if (fromOlderVersion) CalibrationData = MigrateLegacyKeys(CalibrationData);
            _exported.Clear();   // new objects: a revision of an old one says nothing about them
            foreach (KeyValuePair<string, CalibrationData> kv in CalibrationData)
            {
                if (kv.Value == null) continue;
                kv.Value.SetKeepsPercentiles(ReadsPercentiles(kv.Key));
                kv.Value.Compact();
            }
        }

        /// <summary>
        /// A pre-Version-21 calibration section in the Version 21 form: an entry keyed by a SimHub-generated <c>DT_</c>
        /// track or <c>DC_</c> car is dropped, every other one is kept under <see cref="LegacyPrefix"/> until its game is
        /// known. Idempotent (an entry already under the prefix stays as it is). Null in, null out.
        /// <paramref name="knownGames"/>: games the file already holds learners for - an entry whose first part is one of
        /// them is ALREADY in the game-first form (written by a build that stamped Version 21 but left other keys old) and is
        /// kept as it is; an old key never starts with a game.
        /// </summary>
        public static Dictionary<string, CalibrationData> MigrateLegacyKeys(Dictionary<string, CalibrationData> calibrations,
            ICollection<string> knownGames = null)
        {
            if (calibrations == null) return null;
            var moved = new Dictionary<string, CalibrationData>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, CalibrationData> kv in calibrations)
            {
                if (kv.Key == null || kv.Value == null) continue;
                if (kv.Key.StartsWith(LegacyPrefix, StringComparison.Ordinal)) { moved[kv.Key] = kv.Value; continue; }
                int cut = kv.Key.IndexOf(';');
                if (knownGames != null && cut > 0 && kv.Key.Split(';').Length >= 3 && knownGames.Contains(kv.Key.Substring(0, cut)))
                {
                    moved[kv.Key] = kv.Value;   // already game;track;car;metric
                    continue;
                }
                if (HasGeneratedId(kv.Key)) continue;
                moved[LegacyPrefix + kv.Key] = kv.Value;
            }
            return moved;
        }

        /// <summary>The game the current frames belong to - the first part of every calibration key (Version 21). Set by
        /// the engine with the track and car; empty (no game part) when unknown.</summary>
        public string GameId { get; set; } = string.Empty;

        /// <summary>The prefix a pre-Version-21 entry is kept under until its game is known (see
        /// <see cref="ImportCalibrations"/>).</summary>
        public const string LegacyPrefix = "~legacy;";

        /// <summary>SimHub's own prefixes for a track / car id it GENERATED because the game reported none (Forza
        /// Horizon's open world: <c>DT__&lt;date-time&gt;</c>, new every session). SimHub itself never saves a
        /// calibration under them (<c>CalibrationDataProvider.ShouldSerializeCalibrationData</c>).</summary>
        public const string GeneratedTrackPrefix = "DT_";
        public const string GeneratedCarPrefix = "DC_";

        private static bool IsGenerated(string id, string prefix) =>
            !string.IsNullOrEmpty(id) && id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static bool HasGeneratedId(string key)
        {
            foreach (string part in key.Split(';'))
                if (IsGenerated(part, GeneratedTrackPrefix) || IsGenerated(part, GeneratedCarPrefix)) return true;
            return false;
        }

        /// <summary>Only the Slip metric is read by percentile; the RPS-to-speed metrics are only averaged.</summary>
        private static bool ReadsPercentiles(string keyOrMetric)
        {
            if (keyOrMetric == null) return true;
            int split = keyOrMetric.LastIndexOf(';');
            string metric = split >= 0 ? keyOrMetric.Substring(split + 1) : keyOrMetric;
            return !metric.StartsWith(RpsToSpeedMetric, StringComparison.Ordinal);
        }

        /// <summary>The last copy handed out per key, with the entry's revision when it was copied.</summary>
        private readonly Dictionary<string, KeyValuePair<int, CalibrationData>> _exported =
            new Dictionary<string, KeyValuePair<int, CalibrationData>>(StringComparer.Ordinal);

        /// <summary>See <see cref="ExportCalibrations(out bool)"/>.</summary>
        public Dictionary<string, CalibrationData> ExportCalibrations() => ExportCalibrations(out _);

        /// <summary>
        /// A SNAPSHOT for the parameters writer (owner, 2026-11-25): a new dictionary of independent copies, so the writer
        /// thread never reads an entry this (data) thread is still feeding or compacting. Only entries whose
        /// <see cref="Calibration.CalibrationData.Revision"/> moved since the last export are copied again - the others
        /// reuse their previous copy, which nothing mutates. <paramref name="changed"/> is false when no entry was added,
        /// removed or changed since the last export (the caller then saves nothing). Data thread only.
        /// </summary>
        public Dictionary<string, CalibrationData> ExportCalibrations(out bool changed)
        {
            changed = _exported.Count != CalibrationData.Count;
            var snapshot = new Dictionary<string, CalibrationData>(CalibrationData.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, CalibrationData> kv in CalibrationData)
            {
                if (kv.Value == null) continue;
                if (!_exported.TryGetValue(kv.Key, out KeyValuePair<int, CalibrationData> last) || last.Key != kv.Value.Revision)
                {
                    last = new KeyValuePair<int, CalibrationData>(kv.Value.Revision, kv.Value.Copy());
                    _exported[kv.Key] = last;
                    changed = true;
                }
                snapshot[kv.Key] = last.Value;
            }
            if (_exported.Count != snapshot.Count)
            {
                changed = true;
                foreach (string gone in new List<string>(_exported.Keys))
                    if (!snapshot.ContainsKey(gone)) _exported.Remove(gone);
            }
            return snapshot;
        }

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

        /// <summary>
        /// THE KEY (Version 21, owner, 2026-11-25): <c>game;track;car;metric</c>. SimHub keeps one calibration store PER
        /// GAME, so its key needs no game; this plugin keeps one for all games, so the game comes first (two games
        /// reporting the same track and car names must not share one). A SimHub-generated track (<c>DT_</c>) or car
        /// (<c>DC_</c>) - new every session - is left out, so e.g. Forza Horizon keeps one calibration per car:
        /// <c>FH6;Car_3304;Slip</c>. Empty parts are left out, as in SimHub's own key.
        /// </summary>
        public static string BuildKey(string gameId, string trackIdWithConfig, string carModel, string metricName)
        {
            string track = IsGenerated(trackIdWithConfig, GeneratedTrackPrefix) ? string.Empty : trackIdWithConfig ?? string.Empty;
            string car = IsGenerated(carModel, GeneratedCarPrefix) ? string.Empty : carModel ?? string.Empty;
            string composed = (gameId ?? string.Empty) + ";" + track + ";" + car + ";" + (metricName ?? string.Empty);
            return string.Join(";", composed.Split(new[] { ";" }, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The pre-Version-21 key of the same calibration, as kept by <see cref="ImportCalibrations"/>.</summary>
        private static string LegacyKey(string trackIdWithConfig, string carModel, string metricName) =>
            LegacyPrefix + BuildKey(trackIdWithConfig, carModel, metricName);

        private string Key(string trackIdWithConfig, string carModel, string metricName) =>
            BuildKey(GameId, trackIdWithConfig, carModel, metricName);

        /// <summary>Every metric one (track, car) key can hold - Slip (shared by all four wheels) and RPS-to-speed per axle.</summary>
        private static readonly string[] AllMetrics = { SlipMetric, RpsToSpeedMetric + FrontSuffix, RpsToSpeedMetric + RearSuffix };

        /// <summary>Whether any metric of this (game, track, car) has a calibration entry - a legacy one included.</summary>
        public bool HasCalibration(string trackIdWithConfig, string carModel)
        {
            foreach (string metric in AllMetrics)
                if (CalibrationData.ContainsKey(Key(trackIdWithConfig, carModel, metric))
                    || CalibrationData.ContainsKey(LegacyKey(trackIdWithConfig, carModel, metric))) return true;
            return false;
        }

        /// <summary>
        /// RESET (owner, 2026-11-25): deletes every metric's entry of this (game, track, car) - Wheel Slip and Wheel Lock
        /// share them, so either channel's Reset Calibration clears both (a legacy entry too, so it is not adopted
        /// again). Learning starts again from nothing on the next driven frame. Returns how many entries were deleted.
        /// </summary>
        public int ResetCalibration(string trackIdWithConfig, string carModel)
        {
            int removed = 0;
            foreach (string metric in AllMetrics)
            {
                if (CalibrationData.Remove(Key(trackIdWithConfig, carModel, metric))) removed++;
                if (CalibrationData.Remove(LegacyKey(trackIdWithConfig, carModel, metric))) removed++;
            }
            return removed;
        }

        /// <summary>Deletes every calibration (the "Persist Calibration data?" switch turned off). Returns how many.</summary>
        public int ClearAll()
        {
            int count = CalibrationData.Count;
            CalibrationData.Clear();
            return count;
        }

        /// <param name="front">Accepted and DELIBERATELY IGNORED - see this class's own remarks. Kept in
        /// the signature so the call sites read the same as SimHub's.</param>
        public ICalibrationData GetSlipCalibration(string trackIdWithConfig, string carModel, bool front, bool store = false)
            => Resolve(Key(trackIdWithConfig, carModel, SlipMetric), LegacyKey(trackIdWithConfig, carModel, SlipMetric), SlipMetric, store);

        public ICalibrationData GetRpsToSpeedCalibration(string trackIdWithConfig, string carModel, bool front, bool store = false)
        {
            string metric = RpsToSpeedMetric + (front ? FrontSuffix : RearSuffix);
            return Resolve(Key(trackIdWithConfig, carModel, metric), LegacyKey(trackIdWithConfig, carModel, metric), metric, store);
        }

        private ICalibrationData Resolve(string key, string legacyKey, string metricName, bool store)
        {
            // A pre-Version-21 entry of this track and car moves under the game the first time it is met.
            if (!CalibrationData.ContainsKey(key) && CalibrationData.TryGetValue(legacyKey, out CalibrationData legacy) && legacy != null)
            {
                CalibrationData.Remove(legacyKey);
                CalibrationData[key] = legacy;
            }

            if (!CalibrationData.TryGetValue(key, out CalibrationData live))
            {
                live = new CalibrationData();
                live.SetKeepsPercentiles(ReadsPercentiles(metricName));
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
            else live.Compact();   // finished: keep only what is read (a no-op once compact)
        }
    }
}
