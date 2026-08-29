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
        public int FilesSkippedUnchanged;
        public int GamesImported;
        public int MetricsImported;
        public int BoundsImported;
        public int MetricsSkippedBecauseOursExists;
        public string Message = string.Empty;
    }

    /// <summary>
    /// Everything imported from SimHub's shipped ShakeIt reference data, plus the timestamps that make a
    /// re-import cheap. Passed in and mutated in place so all three live in one persisted document.
    /// </summary>
    public sealed class ShakeItImportState
    {
        public Dictionary<string, Dictionary<string, PreloadedCalibrationData>> Presets
            = new Dictionary<string, Dictionary<string, PreloadedCalibrationData>>(StringComparer.Ordinal);

        public Dictionary<string, GameCalibrationBounds> Bounds
            = new Dictionary<string, GameCalibrationBounds>(StringComparer.Ordinal);

        /// <summary>Source file name to its last-write time in UTC ticks, so a start-up import can skip
        /// files that have not changed since the last one.</summary>
        public Dictionary<string, long> SourceTimestamps = new Dictionary<string, long>(StringComparer.Ordinal);
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
        /// Reads SimHub's files and merges them into <paramref name="state"/>.
        /// </summary>
        /// <param name="overrideExisting">The settings checkbox. FALSE (and always so on the automatic
        /// start-up import) leaves alone any entry this plugin already holds, so a driver's accumulated
        /// data is never silently replaced. TRUE replaces ours for every matching key.</param>
        /// <param name="onlyChangedFiles">TRUE on the automatic start-up import: a source file whose
        /// last-write time matches the one recorded from the previous import is not even opened. This is
        /// what makes running the import on EVERY launch cheap - a SimHub update that ships new games is
        /// picked up automatically, while an unchanged installation costs three timestamp reads.</param>
        public static ShakeItConversionOutcome Convert(
            string rootDirectory, ShakeItImportState state, bool overrideExisting, bool onlyChangedFiles)
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
                long stamp;
                try { stamp = File.GetLastWriteTimeUtc(file).Ticks; }
                catch { stamp = 0L; }

                if (onlyChangedFiles && stamp != 0L
                    && state.SourceTimestamps.TryGetValue(fileKey, out long previous) && previous == stamp)
                {
                    outcome.FilesSkippedUnchanged++;
                    continue;
                }

                GamesCalibrationDto parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<GamesCalibrationDto>(File.ReadAllText(file));
                }
                catch (Exception e)
                {
                    // One malformed file must not abort the import of the others, and its timestamp is
                    // deliberately NOT recorded - so a later fix to that file is picked up.
                    report.AppendLine("Skipped " + fileKey + ": " + e.Message);
                    continue;
                }

                outcome.FilesRead++;
                state.SourceTimestamps[fileKey] = stamp;
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
            if (outcome.FilesRead == 0 && outcome.FilesSkippedUnchanged > 0)
                return "SimHub reference data is already up to date (" + outcome.FilesSkippedUnchanged + " file(s) unchanged).";

            return "Imported " + outcome.MetricsImported + " calibration metric(s) and " + outcome.BoundsImported
                   + " game bound(s) across " + outcome.GamesImported + " game entr(y/ies) from "
                   + outcome.FilesRead + " file(s)."
                   + (outcome.FilesSkippedUnchanged > 0 ? " " + outcome.FilesSkippedUnchanged + " file(s) unchanged." : string.Empty)
                   + (outcome.MetricsSkippedBecauseOursExists > 0
                       ? " Kept " + outcome.MetricsSkippedBecauseOursExists
                         + " existing entr(y/ies) - tick \"Override current data if exists?\" to replace them."
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

        private static void ImportPresets(
            ShakeItImportState state, string code, GameCalibrationDto game, bool overrideExisting, ShakeItConversionOutcome outcome)
        {
            if (game.PrecalibrationData == null) return;

            if (!state.Presets.TryGetValue(code, out Dictionary<string, PreloadedCalibrationData> perMetric))
            {
                perMetric = new Dictionary<string, PreloadedCalibrationData>(StringComparer.Ordinal);
                state.Presets[code] = perMetric;
                outcome.GamesImported++;
            }

            foreach (KeyValuePair<string, PreloadedCalibrationDto> metric in game.PrecalibrationData)
            {
                if (metric.Value == null) continue;

                if (perMetric.ContainsKey(metric.Key) && !overrideExisting)
                {
                    outcome.MetricsSkippedBecauseOursExists++;
                    continue;
                }

                perMetric[metric.Key] = new PreloadedCalibrationData
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
