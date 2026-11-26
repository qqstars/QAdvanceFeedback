using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.RawCalculator.Calibration;

namespace QAdvanceFeedback
{
    /// <summary>What one conversion run did - reported back to the settings UI verbatim.</summary>
    public sealed class ShakeItConversionOutcome
    {
        public bool SourceFound;
        public int FilesRead;
        public int GamesImported;
        public int MetricsImported;
        public int BoundsImported;
        /// <summary>Entries kept because the plugin already held them and "Override current data if exists?" is off.</summary>
        public int MetricsSkippedBecauseOursExists;

        /// <summary>Metrics present in SimHub's file that nothing in this plugin consumes - see
        /// <see cref="ShakeItPrecalibrationConverter.ConsumedMetrics"/>.</summary>
        public int MetricsIgnoredAsUnused;
        public string Message = string.Empty;
    }

    /// <summary>
    /// Everything imported from SimHub's shipped ShakeIt reference data. Passed in and mutated in place (UPSERT: an
    /// entry SimHub no longer has is kept - owner, 2026-11-25).
    /// </summary>
    public sealed class ShakeItImportState
    {
        public Dictionary<string, Dictionary<string, PreloadedCalibrationData>> Presets
            = new Dictionary<string, Dictionary<string, PreloadedCalibrationData>>(StringComparer.Ordinal);

        public Dictionary<string, GameCalibrationBounds> Bounds
            = new Dictionary<string, GameCalibrationBounds>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Converts SimHub's own shipped ShakeIt reference data into this plugin's persisted form.
    /// <para/>
    /// WHY THIS EXISTS. Layer 3 reproduces ShakeIt's arithmetic exactly (see
    /// <c>Core.RawCalculator.Calibration</c>), so with the same calibration inputs it produces the same
    /// output. The ONE input this plugin cannot ship is SimHub's own
    /// <c>ShakeIt\ShakeItPrecalibration\GameData.json</c> - that file is theirs to distribute. Importing
    /// it from the local installation closes the last gap, after which our Raw values and ShakeIt's agree
    /// by construction rather than by approximation.
    /// <para/>
    /// TWO KINDS OF DATA COME OUT OF THAT FILE, and both are imported: the per-metric
    /// <c>PrecalibrationData</c> presets, and the three per-game wheel-speed-delta bounds that sit
    /// alongside them on the same object.
    /// <para/>
    /// SimHub reads three files and layers them (base, Custom, External); all three are read here in the
    /// same order, later ones overriding earlier ones for the same game.
    /// </summary>
    public static class ShakeItPrecalibrationConverter
    {
        /// <summary>SimHub's own precalibration folder, relative to the SimHub installation root. The
        /// plugin runs INSIDE SimHub, so <see cref="AppDomain.BaseDirectory"/> is that root.</summary>
        public static readonly string[] RelativeSourceFiles =
        {
            @"ShakeIt\ShakeItPrecalibration\GameData.json",
            @"ShakeIt\ShakeItPrecalibration\GameData_Custom.json",
            @"ShakeIt\ShakeItPrecalibration\GameData_External.json",
        };

        // ---- The shape of SimHub's own file, mirrored just closely enough to read it. ----

        private sealed class GamesCalibrationDto
        {
            public List<GameCalibrationDto> Games { get; set; }
        }

        private sealed class GameCalibrationDto
        {
            public string GameCodes { get; set; }
            public Dictionary<string, PreloadedCalibrationDto> PrecalibrationData { get; set; }

            // SimHub omits these when they equal its own defaults (ShouldSerialize* guards), so a missing
            // value must mean "the default", not zero - hence nullable with a default applied on import.
            public double? WheelSpeedDeltaLowbound { get; set; }
            public double? WheelSpeedDeltaHighbound { get; set; }
            public double? WheelSpeedDeltaLowLockbound { get; set; }
        }

        private sealed class PreloadedCalibrationDto
        {
            public double MeasuredMaximum { get; set; }
            public double CorrectionFactor { get; set; } = 1.0;
        }

        /// <summary>Resolves SimHub's precalibration files. <paramref name="rootDirectory"/> is injectable
        /// purely so this is testable without a SimHub installation present.</summary>
        public static List<string> FindSourceFiles(string rootDirectory)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(rootDirectory)) return found;

            foreach (string relative in RelativeSourceFiles)
            {
                string full = Path.Combine(rootDirectory, relative);
                try { if (File.Exists(full)) found.Add(full); }
                catch { /* an unreadable path is simply not a source */ }
            }
            return found;
        }

        public static string DefaultRootDirectory()
        {
            try { return AppDomain.CurrentDomain.BaseDirectory; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// SYNC WITH SIMHUB'S FILES (owner, 2026-11-25): reads ALL THREE files, in SimHub's order (base, Custom, External),
        /// on every plugin start (and from the settings button) - no timestamp check. A new game or metric is always added;
        /// one SimHub no longer has is always kept. <paramref name="overrideExisting"/> ("Override current data if
        /// exists?", default on) decides one already held: TRUE takes SimHub's current values (a full upsert - a later file
        /// then wins for the same game), FALSE keeps it (the first file to bring it wins). These sections hold only
        /// SimHub's data - nothing in the plugin learns into them (what it learns is the slip / lock calibration).
        /// </summary>
        public static ShakeItConversionOutcome Convert(string rootDirectory, ShakeItImportState state, bool overrideExisting = true)
        {
            var outcome = new ShakeItConversionOutcome();
            if (state == null) state = new ShakeItImportState();

            List<string> files = FindSourceFiles(rootDirectory);
            outcome.SourceFound = files.Count > 0;
            if (!outcome.SourceFound)
            {
                outcome.Message = "No SimHub ShakeIt reference files were found. Expected "
                                  + RelativeSourceFiles[0] + " under the SimHub installation folder.";
                return outcome;
            }

            var report = new StringBuilder();
            foreach (string file in files)
            {
                string fileKey = Path.GetFileName(file);
                GamesCalibrationDto parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<GamesCalibrationDto>(File.ReadAllText(file));
                }
                catch (Exception e)
                {
                    // One malformed file must not abort the import of the others (it is read again next start).
                    report.AppendLine("Skipped " + fileKey + ": " + e.Message);
                    continue;
                }

                outcome.FilesRead++;
                if (parsed?.Games == null) continue;

                foreach (GameCalibrationDto game in parsed.Games)
                {
                    if (game == null || string.IsNullOrWhiteSpace(game.GameCodes)) continue;

                    // GameCodes is a ';'-separated list of patterns; each becomes its own entry so a
                    // lookup can match the running title against any of them.
                    foreach (string pattern in game.GameCodes.Split(';'))
                    {
                        string code = pattern?.Trim();
                        if (string.IsNullOrEmpty(code)) continue;

                        ImportBounds(state, code, game, overrideExisting, outcome);
                        ImportPresets(state, code, game, overrideExisting, outcome);
                    }
                }
            }

            report.Insert(0, BuildSummary(outcome) + Environment.NewLine);
            outcome.Message = report.ToString().TrimEnd();
            return outcome;
        }

        private static string BuildSummary(ShakeItConversionOutcome outcome)
        {
            return "Synced " + outcome.MetricsImported + " calibration metric(s) and " + outcome.BoundsImported
                   + " game bound(s) from " + outcome.FilesRead + " file(s) (" + outcome.GamesImported + " new game entr(y/ies))."
                   + (outcome.MetricsSkippedBecauseOursExists > 0
                       ? " Kept " + outcome.MetricsSkippedBecauseOursExists
                         + " existing entr(y/ies) - turn on \"Override current data if exists?\" to take SimHub's values."
                       : string.Empty)
                   + (outcome.MetricsIgnoredAsUnused > 0
                       ? " Ignored " + outcome.MetricsIgnoredAsUnused
                         + " metric(s) ShakeIt uses for other effects (RollDelta, Suspension) - the wheel"
                         + " lock/slip algorithm does not read them."
                       : string.Empty);
        }

        private static void ImportBounds(
            ShakeItImportState state, string code, GameCalibrationDto game, bool overrideExisting, ShakeItConversionOutcome outcome)
        {
            bool hasAny = game.WheelSpeedDeltaLowbound.HasValue
                          || game.WheelSpeedDeltaHighbound.HasValue
                          || game.WheelSpeedDeltaLowLockbound.HasValue;
            if (!hasAny) return;

            if (state.Bounds.ContainsKey(code) && !overrideExisting)
            {
                outcome.MetricsSkippedBecauseOursExists++;
                return;
            }

            var defaults = GameCalibrationBounds.Defaults;
            state.Bounds[code] = new GameCalibrationBounds
            {
                WheelSpeedDeltaLowbound = game.WheelSpeedDeltaLowbound ?? defaults.WheelSpeedDeltaLowbound,
                WheelSpeedDeltaHighbound = game.WheelSpeedDeltaHighbound ?? defaults.WheelSpeedDeltaHighbound,
                WheelSpeedDeltaLowLockbound = game.WheelSpeedDeltaLowLockbound ?? defaults.WheelSpeedDeltaLowLockbound,
            };
            outcome.BoundsImported++;
        }

        /// <summary>
        /// The ONLY metrics anything in this plugin ever reads back out.
        /// <para/>
        /// SimHub's <c>PrecalibrationData</c> carries more than these - real files hold <c>RollDelta</c>
        /// and <c>Suspension</c> alongside <c>Slip</c>. Those belong to ShakeIt's chassis-roll and
        /// suspension MOTION effects; the legacy iRacing wheel-lock/slip algorithm this plugin mirrors
        /// does not use them. Verified against our own consumers rather than assumed: the only lookups
        /// that ever reach <c>CalibrationDataProvider.PrecalibrationData</c> are
        /// <c>GetSlipCalibration</c> (metric <c>Slip</c>) and <c>GetRpsToSpeedCalibration</c> (metric
        /// <c>RPSToSpeed</c> + <c>Front</c>/<c>Rear</c>) - and that dictionary is keyed by METRIC NAME
        /// ALONE, so an unconsumed key can never be reached by any other route.
        /// <para/>
        /// Filtered at import rather than left to sit unread, for three reasons: the "imported N
        /// metric(s)" summary the owner sees stays honest, the persisted runtime document does not carry
        /// data nothing will ever read, and an ALLOW-list means a metric SimHub adds later is skipped
        /// visibly (counted in <see cref="ShakeItConversionOutcome.MetricsIgnoredAsUnused"/>) instead of
        /// being silently stored against a consumer that does not exist.
        /// </summary>
        public static readonly string[] ConsumedMetrics =
        {
            CalibrationDataProvider.SlipMetric,
            CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.FrontSuffix,
            CalibrationDataProvider.RpsToSpeedMetric + CalibrationDataProvider.RearSuffix,
        };

        /// <summary>
        /// Maps a metric name from SimHub's file onto the EXACT spelling our consumers look up, or null
        /// when nothing consumes it.
        /// <para/>
        /// The canonicalisation matters as much as the filtering: <c>PrecalibrationData</c> is read with
        /// <see cref="StringComparer.Ordinal"/>, so a file spelling the key <c>"slip"</c> would import
        /// happily and then never be found. Comparing case-insensitively but STORING the canonical name
        /// removes that whole failure mode.
        /// </summary>
        public static string CanonicalMetricName(string metricName)
        {
            if (string.IsNullOrWhiteSpace(metricName)) return null;

            string trimmed = metricName.Trim();
            foreach (string consumed in ConsumedMetrics)
                if (string.Equals(trimmed, consumed, StringComparison.OrdinalIgnoreCase)) return consumed;

            return null;
        }

        private static void ImportPresets(
            ShakeItImportState state, string code, GameCalibrationDto game, bool overrideExisting, ShakeItConversionOutcome outcome)
        {
            if (game.PrecalibrationData == null) return;

            // The per-code entry is created LAZILY, on the first metric we actually consume - a game
            // whose only entries are RollDelta/Suspension must not be counted as an imported game, nor
            // leave an empty dictionary behind in the persisted document.
            Dictionary<string, PreloadedCalibrationData> perMetric;
            bool haveEntry = state.Presets.TryGetValue(code, out perMetric);

            foreach (KeyValuePair<string, PreloadedCalibrationDto> metric in game.PrecalibrationData)
            {
                if (metric.Value == null) continue;

                string metricName = CanonicalMetricName(metric.Key);
                if (metricName == null)
                {
                    outcome.MetricsIgnoredAsUnused++;
                    continue;
                }

                if (haveEntry && perMetric.ContainsKey(metricName) && !overrideExisting)
                {
                    outcome.MetricsSkippedBecauseOursExists++;
                    continue;
                }

                if (!haveEntry)
                {
                    perMetric = new Dictionary<string, PreloadedCalibrationData>(StringComparer.Ordinal);
                    state.Presets[code] = perMetric;
                    haveEntry = true;
                    outcome.GamesImported++;
                }

                perMetric[metricName] = new PreloadedCalibrationData
                {
                    MeasuredMaximum = metric.Value.MeasuredMaximum,
                    CorrectionFactor = metric.Value.CorrectionFactor,
                };
                outcome.MetricsImported++;
            }
        }

        /// <summary>
        /// Selects the preset set for the running title, reproducing SimHub's own matching: each stored
        /// key is a VB <c>Like</c> pattern matched case-insensitively against the game code.
        /// </summary>
        public static Dictionary<string, PreloadedCalibrationData> ResolveForGame(
            Dictionary<string, Dictionary<string, PreloadedCalibrationData>> presets, string gameCode)
            => ResolveByPattern(presets, gameCode);

        /// <summary>The running title's shipped wheel-speed-delta bounds, or null for SimHub's own
        /// defaults.</summary>
        public static GameCalibrationBounds ResolveBoundsForGame(
            Dictionary<string, GameCalibrationBounds> bounds, string gameCode)
            => ResolveByPattern(bounds, gameCode);

        private static TValue ResolveByPattern<TValue>(Dictionary<string, TValue> source, string gameCode)
            where TValue : class
        {
            if (source == null || string.IsNullOrEmpty(gameCode)) return null;
            if (source.TryGetValue(gameCode, out TValue exact)) return exact;

            foreach (KeyValuePair<string, TValue> entry in source)
                if (Matches(gameCode, entry.Key)) return entry.Value;

            return null;
        }

        /// <summary>
        /// A minimal VB <c>Like</c>: <c>*</c> any run, <c>?</c> any single character, <c>#</c> any digit,
        /// everything else literal, all case-insensitive.
        /// <para/>
        /// HONEST LIMITATION: VB's <c>Like</c> also supports <c>[charlist]</c>, which is not implemented -
        /// such a pattern simply fails to match rather than matching wrongly. No shipped game code
        /// observed so far uses a character list.
        /// </summary>
        public static bool Matches(string text, string pattern)
        {
            if (text == null || pattern == null) return false;
            return MatchFrom(text.ToLowerInvariant(), 0, pattern.ToLowerInvariant(), 0);
        }

        private static bool MatchFrom(string text, int t, string pattern, int p)
        {
            while (p < pattern.Length)
            {
                char pc = pattern[p];
                if (pc == '*')
                {
                    while (p < pattern.Length && pattern[p] == '*') p++;
                    if (p == pattern.Length) return true;
                    for (int skip = t; skip <= text.Length; skip++)
                        if (MatchFrom(text, skip, pattern, p)) return true;
                    return false;
                }

                if (t >= text.Length) return false;
                if (pc == '?') { t++; p++; continue; }
                if (pc == '#') { if (!char.IsDigit(text[t])) return false; t++; p++; continue; }
                if (text[t] != pc) return false;
                t++; p++;
            }

            return t == text.Length;
        }
    }
}
