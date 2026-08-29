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

        private ShakeItConversionOutcome Import(ShakeItImportState state, bool overrideExisting = false, bool onlyChanged = false)
            => ShakeItPrecalibrationConverter.Convert(_root, state, overrideExisting, onlyChanged);

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

        [Fact]
        public void WithoutOverride_OurOwnDataIsKept()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            state.Presets["F12025"] = new Dictionary<string, PreloadedCalibrationData>
            {
                ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 99.0 },
            };

            ShakeItConversionOutcome outcome = Import(state);

            Assert.Equal(0, outcome.MetricsImported);
            Assert.Equal(99.0, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
            Assert.Contains("Override current data if exists?", outcome.Message);
        }

        [Fact]
        public void WithOverride_SimHubsDataReplacesOurs()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            state.Presets["F12025"] = new Dictionary<string, PreloadedCalibrationData>
            {
                ["Slip"] = new PreloadedCalibrationData { MeasuredMaximum = 99.0 },
            };

            ShakeItConversionOutcome outcome = Import(state, overrideExisting: true);

            Assert.Equal(1, outcome.MetricsImported);
            Assert.Equal(2.5, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
        }

        // ------------------------------------------------------------------------------------
        // Timestamp gating - what makes running this on EVERY launch cheap.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void AnUnchangedFileIsNotReopenedOnASubsequentStartupImport()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            ShakeItConversionOutcome first = Import(state, onlyChanged: true);
            Assert.Equal(1, first.FilesRead);
            Assert.Single(state.SourceTimestamps);

            ShakeItConversionOutcome second = Import(state, onlyChanged: true);
            Assert.Equal(0, second.FilesRead);
            Assert.Equal(1, second.FilesSkippedUnchanged);
            Assert.Contains("already up to date", second.Message);
        }

        [Fact]
        public void ANewerFileIsPickedUpAutomatically_SoASimHubUpdateLandsWithoutAskingOnce()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            Import(state, onlyChanged: true);

            // SimHub updates and ships a new game.
            Thread.Sleep(20);
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], @"{
                ""Games"": [
                    { ""GameCodes"": ""F12025"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 2.5 } } },
                    { ""GameCodes"": ""WRC"", ""PrecalibrationData"": { ""Slip"": { ""MeasuredMaximum"": 3.5 } } }
                ]
            }");
            File.SetLastWriteTimeUtc(Path.Combine(_root, ShakeItPrecalibrationConverter.RelativeSourceFiles[0]),
                DateTime.UtcNow.AddMinutes(1));

            ShakeItConversionOutcome outcome = Import(state, onlyChanged: true);

            Assert.Equal(1, outcome.FilesRead);
            Assert.True(state.Presets.ContainsKey("WRC"), "a newly shipped game must arrive without a button press");
            // ...and the existing entry is still ours, because the automatic import never overrides.
            Assert.Equal(2.5, state.Presets["F12025"]["Slip"].MeasuredMaximum, 9);
        }

        [Fact]
        public void AnExplicitButtonPressRereadsEverythingRegardlessOfTimestamps()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], OneGameJson);

            var state = new ShakeItImportState();
            Import(state, onlyChanged: true);

            ShakeItConversionOutcome forced = Import(state, overrideExisting: true, onlyChanged: false);
            Assert.Equal(1, forced.FilesRead);
            Assert.Equal(0, forced.FilesSkippedUnchanged);
        }

        [Fact]
        public void AMalformedFileDoesNotAbortTheOthers_AndIsRetriedNextTime()
        {
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[0], "{ this is not json");
            WriteSourceFile(ShakeItPrecalibrationConverter.RelativeSourceFiles[1], OneGameJson);

            var state = new ShakeItImportState();
            ShakeItConversionOutcome outcome = Import(state, onlyChanged: true);

            Assert.Equal(1, outcome.MetricsImported);
            Assert.Contains("Skipped GameData.json", outcome.Message);

            // Its timestamp was deliberately not recorded, so a later fix to that file is picked up.
            Assert.False(state.SourceTimestamps.ContainsKey("GameData.json"));
            Assert.True(state.SourceTimestamps.ContainsKey("GameData_Custom.json"));
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
