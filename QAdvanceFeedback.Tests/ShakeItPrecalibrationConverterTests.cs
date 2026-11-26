using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using QAdvanceFeedback.Core.RawCalculator.Calibration;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE SHAKEIT REFERENCE-DATA IMPORTER (1.0.7.1). Layer 3 reproduces ShakeIt's arithmetic exactly;
    /// the one input this plugin cannot ship is SimHub's own per-game reference file. These tests use a
    /// real temporary folder laid out the way SimHub lays its own out, so the importer runs against
    /// genuine files rather than a stub.
    /// </summary>
    public class ShakeItPrecalibrationConverterTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "qaf-shakeit-" + Guid.NewGuid());

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private void WriteSourceFile(string relative, string json)
        {
            string full = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, json);
        }

        private const string OneGameJson = @"{
            ""Games"": [
                { ""GameCodes"": ""F12025"",
                  ""WheelSpeedDeltaLowbound"": 0.03,
                  ""WheelSpeedDeltaHighbound"": 0.25,
                  ""WheelSpeedDeltaLowLockbound"": 0.12,
                  ""PrecalibrationData"": {
                      ""Slip"": { ""MeasuredMaximum"": 2.5, ""CorrectionFactor"": 1.2 }
                  } }
            ]
        }";

        private ShakeItConversionOutcome Import(ShakeItImportState state, bool overrideExisting = true)
            => ShakeItPrecalibrationConverter.Convert(_root, state, overrideExisting);

        [Fact]
        public void ReportsClearlyWhenSimHubIsNotInstalled()
        {
            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.False(outcome.SourceFound);
            Assert.Empty(state.Presets);
            Assert.Contains("No SimHub ShakeIt reference files were found", outcome.Message);
        }

        [Fact]
        public void ImportsPresetsAndGameBoundsFromTheSameFile()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.True(outcome.SourceFound);
            Assert.Equal(1, outcome.MetricsImported);
            Assert.Equal(1, outcome.BoundsImported);

            PreloadedCalibrationData slip = state.Presets["F12025"]["Slip"];
            Assert.Equal(2.5, slip.MeasuredMaximum, 9);
            Assert.Equal(1.2, slip.CorrectionFactor, 9);

            // The bounds live on the SAME GameCalibration object in SimHub's file - that is exactly why
            // the same conversion pass can pick them up.
            GameCalibrationBounds bounds = state.Bounds["F12025"];
            Assert.Equal(0.03, bounds.WheelSpeedDeltaLowbound, 9);
            Assert.Equal(0.25, bounds.WheelSpeedDeltaHighbound, 9);
            Assert.Equal(0.12, bounds.WheelSpeedDeltaLowLockbound, 9);
        }

        [Fact]
        public void OmittedBoundsFallBackToSimHubsOwnDefaults()
        {
            // SimHub omits a bound when it equals its default (ShouldSerialize* guards), so a missing
            // value must mean "the default", never zero.
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""F12025"", ""WheelSpeedDeltaHighbound"": 0.4,
                    ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 1.0 } } } ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            GameCalibrationBounds bounds = state.Bounds["F12025"];
            Assert.Equal(0.4, bounds.WheelSpeedDeltaHighbound, 9);
            Assert.Equal(GameCalibrationBounds.Defaults.WheelSpeedDeltaLowbound, bounds.WheelSpeedDeltaLowbound, 9);
            Assert.Equal(GameCalibrationBounds.Defaults.WheelSpeedDeltaLowLockbound, bounds.WheelSpeedDeltaLowLockbound, 9);
        }

        // ------------------------------------------------------------------------------------
        // SYNC ON EVERY START (owner, 2026-11-25): all three files, UPSERT, no timestamp check.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Every_run_takes_SimHubs_current_values_for_a_game_already_held()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            state.Presets["F12025"] = new Dictionary<string, PreloadedCalibrationData>
            {
                ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 99.0 },
            };
            state.Bounds["F12025"] = new GameCalibrationBounds { WheelSpeedDeltaHighbound = 9.0 };

            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(1, outcome.MetricsImported);
            Assert.Equal(2.5, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
            Assert.Equal(0.25, state.Bounds["F12025"].WheelSpeedDeltaHighbound, 9);
        }

        [Fact]
        public void A_SimHub_update_lands_on_the_next_run_new_games_and_changed_values_alike()
        {
            string file = Path.Combine(_root, ShakeItPrecalibrationConverter.RelativeSourceFiles[0]);
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);
            var state = new ShakeItImportState();
            Import(state);
            DateTime stamp = File.GetLastWriteTimeUtc(file);

            // SimHub updates: F12025's preset changes and a new game arrives - even with the same file time.
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [
                    { ""GameCodes"": ""F12025"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.0 } } },
                    { ""GameCodes"": ""WRC"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.5 } } }
                ]
            }");
            File.SetLastWriteTimeUtc(file, stamp);

            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(1, outcome.FilesRead);
            Assert.Equal(3.0, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
            Assert.Equal(3.5, state.Presets["WRC"]["Slip"].MeasuredMaximum, 9);
        }

        [Fact]
        public void With_override_off_a_game_already_held_is_kept_and_only_new_ones_are_added()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [
                    { ""GameCodes"": ""F12025"", ""WheelSpeedDeltaHighbound"": 0.25,
                      ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 2.5 }, ""RPSToSpeedFront"": { ""MeasuredMaximum"": 1.1 } } },
                    { ""GameCodes"": ""WRC"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.5 } } }
                ]
            }");
            var state = new ShakeItImportState();
            state.Presets["F12025"] = new Dictionary<string, PreloadedCalibrationData>
            {
                ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 99.0 },
            };
            state.Bounds["F12025"] = new GameCalibrationBounds { WheelSpeedDeltaHighbound = 9.0 };

            ShakeItConversionOutcome outcome = Import(state, overrideExisting: false);

            Assert.Equal(99.0, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);       // kept
            Assert.Equal(9.0, state.Bounds["F12025"].WheelSpeedDeltaHighbound, 9);         // kept
            Assert.True(state.Presets["F12025"].ContainsKey("RPSToSpeedFront"));               // a new metric is added
            Assert.Equal(3.5, state.Presets["WRC"]["Slip"].MeasuredMaximum, 9);           // a new game is added
            Assert.Equal(2, outcome.MetricsSkippedBecauseOursExists);
            Assert.Contains("Override current data if exists?", outcome.Message);
        }

        [Fact]
        public void A_game_SimHub_no_longer_has_is_kept()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);
            var state = new ShakeItImportState();
            Import(state);

            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""WRC"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.5 } } } ]
            }");
            Import(state);

            Assert.Equal(2.5, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);   // upsert: never deleted
            Assert.True(state.Bounds.ContainsKey("F12025"));
            Assert.True(state.Presets.ContainsKey("WRC"));
        }

        [Fact]
        public void A_later_file_wins_for_the_same_game_like_SimHubs_own_layering()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[1], @"{
                ""Games"": [ { ""GameCodes"": ""F12025"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 4.0 } } } ]
            }");

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(2, outcome.FilesRead);
            Assert.Equal(4.0, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
            // ...and every run gives the same answer (all files are read each time).
            Import(state);
            Assert.Equal(4.0, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
        }

        [Fact]
        public void A_malformed_file_does_not_abort_the_others()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], "{ this is not json");
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[1], OneGameJson);

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(1, outcome.FilesRead);
            Assert.Equal(1, outcome.MetricsImported);
            Assert.Contains("Skipped GameData.json", outcome.Message);
        }

        [Fact]
        public void SemicolonSeparatedGameCodesEachBecomeTheirOwnEntry()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""F12024;F12025"",
                    ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 1.0 } } } ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            Assert.True(state.Presets.ContainsKey("F12024"));
            Assert.True(state.Presets.ContainsKey("F12025"));
        }

        [Fact]
        public void RealWorldMultiCodeEntriesSplitAndResolveForEveryTitleInThem()
        {
            // The two shapes the owner found in a real GameData.json, whitespace and all - a five-way
            // F1 entry and a two-way Trackmania entry. Every title listed must resolve to that entry's
            // data, and the bounds must come across for each of them too.
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [
                    { ""GameCodes"": ""F12017;F12016;F12014;F12013;F12012"",
                      ""WheelSpeedDeltaHighbound"": 0.42,
                      ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 2.5, ""CorrectionFactor"": 1.1 } } },
                    { ""GameCodes"": ""TrackmaniaTurbo; Trackmania"",
                      ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.5 } } }
                ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            foreach (string code in new[] { "F12017", "F12016", "F12014", "F12013", "F12012" })
            {
                Assert.True(state.Presets.ContainsKey(code), $"missing preset for {code}");
                Assert.Equal(2.5, ShakeItPrecalibrationConverter.ResolveForGame(state.Presets, code)["Slip"].MeasuredMaximum, 9);
                Assert.Equal(0.42, ShakeItPrecalibrationConverter.ResolveBoundsForGame(state.Bounds, code).WheelSpeedDeltaHighbound, 9);
            }

            // " Trackmania" - the leading space after the separator must be trimmed, or the key would
            // never match the running title.
            Assert.True(state.Presets.ContainsKey("Trackmania"));
            Assert.Equal(3.5, ShakeItPrecalibrationConverter.ResolveForGame(state.Presets, "Trackmania")["Slip"].MeasuredMaximum, 9);
            Assert.Equal(3.5, ShakeItPrecalibrationConverter.ResolveForGame(state.Presets, "TrackmaniaTurbo")["Slip"].MeasuredMaximum, 9);
        }

        [Fact]
        public void EmptyAndWhitespaceOnlyGameCodeSegmentsAreSkipped()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": "";;F12025;  ;"",
                    ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 1.0 } } } ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            Assert.Single(state.Presets);
            Assert.True(state.Presets.ContainsKey("F12025"));
        }

        // ------------------------------------------------------------------------------------
        // METRIC FILTERING. SimHub's PrecalibrationData carries RollDelta and Suspension alongside
        // Slip; those drive ShakeIt's chassis-roll and suspension MOTION effects, and the legacy
        // iRacing wheel-lock/slip algorithm never reads them.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void UnusedMetricsAreIgnoredRatherThanStoredUnread()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""F12025"", ""PrecalibrationData"": {
                    ""Slip"": { ""MeasuredMaximum"": 2.5 },
                    ""RollDelta"": { ""MeasuredMaximum"": 9.9 },
                    ""Suspension"": { ""MeasuredMaximum"": 8.8 }
                } } ]
            }");

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(new[] { "Slip" }, state.Presets["F12025"].Keys);
            Assert.Equal(1, outcome.MetricsImported);
            Assert.Equal(2, outcome.MetricsIgnoredAsUnused);
            Assert.Contains("RollDelta", outcome.Message);
        }

        [Fact]
        public void RpsToSpeedMetricsAreConsumedAndKept()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""F12025"", ""PrecalibrationData"": {
                    ""RPSToSpeedFront"": { ""MeasuredMaximum"": 1.5 },
                    ""RPSToSpeedRear"": { ""MeasuredMaximum"": 1.6 }
                } } ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            Assert.Equal(1.5, state.Presets["F12025"]["RPSToSpeedFront"].MeasuredMaximum, 9);
            Assert.Equal(1.6, state.Presets["F12025"]["RPSToSpeedRear"].MeasuredMaximum, 9);
        }

        [Fact]
        public void MetricNamesAreCanonicalisedSoTheOrdinalLookupCanFindThem()
        {
            // CalibrationDataProvider reads PrecalibrationData with StringComparer.Ordinal, so a file
            // spelling the key "slip" would import happily and then never be found at runtime.
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""F12025"", ""PrecalibrationData"": {
                    ""slip"": { ""MeasuredMaximum"": 2.5 }
                } } ]
            }");

            var state = new ShakeItImportState();
            Import(state);

            Assert.True(state.Presets["F12025"].ContainsKey("Slip"));
        }

        [Fact]
        public void AGameWithNoConsumedMetricsLeavesNoEntryBehind()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [ { ""GameCodes"": ""SomeGame"", ""PrecalibrationData"": {
                    ""RollDelta"": { ""MeasuredMaximum"": 9.9 }
                } } ]
            }");

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state);

            Assert.False(state.Presets.ContainsKey("SomeGame"));
            Assert.Equal(0, outcome.GamesImported);
            Assert.Equal(0, outcome.MetricsImported);
        }

        [Fact]
        public void CanonicalMetricNameRejectsWhatNothingConsumes()
        {
            Assert.Equal("Slip", ShakeItPrecalibrationConverter.CanonicalMetricName("  SLIP  "));
            Assert.Equal("RPSToSpeedRear", ShakeItPrecalibrationConverter.CanonicalMetricName("rpstospeedrear"));
            Assert.Null(ShakeItPrecalibrationConverter.CanonicalMetricName("RollDelta"));
            Assert.Null(ShakeItPrecalibrationConverter.CanonicalMetricName("Suspension"));
            Assert.Null(ShakeItPrecalibrationConverter.CanonicalMetricName(null));
            Assert.Null(ShakeItPrecalibrationConverter.CanonicalMetricName("   "));
        }

        // ------------------------------------------------------------------------------------
        // Game-code matching - SimHub matches a VB `Like` pattern, not string equality.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData("F12025", "F12025", true)]
        [InlineData("f12025", "F12025", true)]
        [InlineData("F12025", "F1*", true)]
        [InlineData("F12025", "F1202#", true)]
        [InlineData("F12025", "F1202?", true)]
        [InlineData("AssettoCorsa", "F1*", false)]
        [InlineData("F1", "F12025", false)]
        public void GameCodePatternsMatchLikeSimHubDoes(string gameCode, string pattern, bool expected)
        {
            Assert.Equal(expected, ShakeItPrecalibrationConverter.Matches(gameCode, pattern));
        }

        [Fact]
        public void ResolversPreferAnExactKeyThenFallBackToAPattern()
        {
            var presets = new Dictionary<string, Dictionary<string, PreloadedCalibrationData>>
            {
                ["F1*"] = new Dictionary<string, PreloadedCalibrationData> { ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 1.0 } },
                ["F12025"] = new Dictionary<string, PreloadedCalibrationData> { ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 2.0 } },
            };
            Assert.Equal(2.0, ShakeItPrecalibrationConverter.ResolveForGame(presets, "F12025")["Slip"].MeasuredMaximum, 9);
            Assert.Equal(1.0, ShakeItPrecalibrationConverter.ResolveForGame(presets, "F12019")["Slip"].MeasuredMaximum, 9);
            Assert.Null(ShakeItPrecalibrationConverter.ResolveForGame(presets, "AssettoCorsa"));

            var bounds = new Dictionary<string, GameCalibrationBounds>
            {
                ["F1*"] = new GameCalibrationBounds { WheelSpeedDeltaHighbound = 0.9 },
            };
            Assert.Equal(0.9, ShakeItPrecalibrationConverter.ResolveBoundsForGame(bounds, "F12025").WheelSpeedDeltaHighbound, 9);
            Assert.Null(ShakeItPrecalibrationConverter.ResolveBoundsForGame(bounds, "AssettoCorsa"));
        }
    }
}
