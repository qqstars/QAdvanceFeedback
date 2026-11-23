using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Runtime;
using QAdvanceFeedback.Core.SlipAngle;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>The parameters file writer (owner, 2026-11-21; docs\parameters-file-format-report.md): only changed
    /// sections are regenerated, an unchanged write is skipped, the text is what the whole-document serialiser would
    /// write, and a leftover temporary file is recovered at startup.</summary>
    public class ParametersFileWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "qaf-writer-" + Guid.NewGuid().ToString("N"));
        private string Main => Path.Combine(_dir, "QAdvanceFeedback.Parameters.json");
        private string Temp => Main + AtomicFile.TemporarySuffix;
        private static readonly TimeSpan NoAutoFlush = TimeSpan.FromMinutes(10);

        public ParametersFileWriterTests() { Directory.CreateDirectory(_dir); }

        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

        private static RuntimeDocument Populated()
        {
            var d = new RuntimeDocument();
            d.LockLearners["G|#|C"] = new GripLearnerState { PeakG = 1.234567890123, Samples = 17 };
            d.GForceLatLearnedMaxima["G|#|C"] = 2.5;
            d.LastKeyDataGame = "G";
            d.LastKeyDataCar = "C \"quoted\"";
            var l = new SlipAngleWindowLearner(SlipAngleWindow.ShippedDefault);
            d.SlipAngleLearners["G|#|C|#|Native/all|#|sealed"] = new SlipAngleKeyState { Learner = l.Export(), PublishedMax = 9.0 };
            return d;
        }

        [Fact]
        public void The_composed_text_is_exactly_what_the_whole_document_serialiser_writes()
        {
            RuntimeDocument d = Populated();
            string expected = JsonConvert.SerializeObject(d, ShakeItCalibrationContractResolver.Settings(Formatting.Indented));
            Assert.Equal(expected, new RuntimeDocumentWriter().Compose(d, null, out bool changed));
            Assert.True(changed);
        }

        [Fact]
        public void Only_changed_sections_are_regenerated_and_unchanged_ones_reuse_their_text()
        {
            var w = new RuntimeDocumentWriter();
            RuntimeDocument d = Populated();
            w.Compose(d, null, out _);
            int all = w.SectionsSerialized;

            d.LockLearners["G|#|C"].PeakG = 3.0;
            string text = w.Compose(d, new HashSet<string> { nameof(RuntimeDocument.LockLearners) }, out bool changed);

            Assert.True(changed);
            Assert.Equal(all + 1, w.SectionsSerialized);
            Assert.Equal(JsonConvert.SerializeObject(d, ShakeItCalibrationContractResolver.Settings(Formatting.Indented)), text);
        }

        [Fact]
        public void A_section_saved_again_with_the_same_content_is_not_a_change()
        {
            var w = new RuntimeDocumentWriter();
            RuntimeDocument d = Populated();
            w.Compose(d, null, out _);
            w.Compose(d, new HashSet<string> { nameof(RuntimeDocument.LockLearners) }, out bool changed);
            Assert.False(changed);
        }

        [Fact]
        public void The_store_skips_the_write_when_a_save_changed_nothing()
        {
            var learners = new Dictionary<string, GripLearnerState> { ["G|#|C"] = new GripLearnerState { PeakG = 1.5, Samples = 9 } };
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.SaveLockLearners(learners);
                store.Flush();
                File.Delete(Main);
                store.SaveLockLearners(learners);   // same content
                store.Flush();
                Assert.False(File.Exists(Main), "an unchanged save must not rewrite the file");
                store.SaveLockLearners(new Dictionary<string, GripLearnerState> { ["G|#|C"] = new GripLearnerState { PeakG = 1.6, Samples = 9 } });
                store.Flush();
                Assert.True(File.Exists(Main));
            }
        }

        [Fact]
        public void The_lateral_g_maxima_reach_the_file()
        {
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.SaveGForceLearners(new Dictionary<string, double>(), new Dictionary<string, double>(),
                    new Dictionary<string, double> { ["G|#|C"] = 2.75 });
                store.Flush();
            }
            using (var reopened = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                reopened.LoadGForceLearners(out _, out _, out Dictionary<string, double> lateral);
                Assert.Equal(2.75, lateral["G|#|C"]);
            }
        }

        [Fact]
        public void Startup_renames_a_valid_temporary_file_when_the_parameters_file_is_missing()
        {
            File.WriteAllText(Temp, JsonConvert.SerializeObject(Populated()));
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.LoadLockLearners(out var lockLearners);
                Assert.Equal(17, lockLearners["G|#|C"].Samples);
            }
            Assert.True(File.Exists(Main));
            Assert.False(File.Exists(Temp));
        }

        [Fact]
        public void Startup_deletes_a_temporary_file_left_next_to_the_parameters_file()
        {
            File.WriteAllText(Main, JsonConvert.SerializeObject(Populated()));
            File.WriteAllText(Temp, "{ half written");
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.LoadLockLearners(out var lockLearners);
                Assert.Equal(17, lockLearners["G|#|C"].Samples);
            }
            Assert.False(File.Exists(Temp));
        }

        [Fact]
        public void Startup_ignores_an_unreadable_temporary_file_and_starts_fresh()
        {
            File.WriteAllText(Temp, "{ half written");
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.LoadLockLearners(out var lockLearners);
                Assert.Empty(lockLearners);
            }
            Assert.False(File.Exists(Main));
        }

        [Fact]
        public void A_loaded_file_is_written_back_with_the_current_version()
        {
            File.WriteAllText(Main, "{ \"Version\": 12 }");
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                store.SaveLastKeyDataContext("G", "C");
                store.Flush();
            }
            Assert.Equal(new RuntimeDocument().Version, JsonConvert.DeserializeObject<RuntimeDocument>(File.ReadAllText(Main)).Version);
        }

        /// <summary>OWNER, 2026-11-22: "the parameter file, were not updated, when restart the simhub". An older file is
        /// rewritten in the current format with no save at all - the first write after the restart carries it.</summary>
        [Fact]
        public void An_older_file_is_rewritten_after_a_restart_without_any_save()
        {
            File.WriteAllText(Main, "{ \"Version\": 14, \"LastKeyDataGame\": \"G\" }");
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                Assert.True(store.LoadedOlderVersion);
                store.Flush();
            }
            RuntimeDocument written = JsonConvert.DeserializeObject<RuntimeDocument>(File.ReadAllText(Main));
            Assert.Equal(new RuntimeDocument().Version, written.Version);
            Assert.Equal("G", written.LastKeyDataGame);

            // A current file is not rewritten for nothing.
            DateTime stamp = File.GetLastWriteTimeUtc(Main);
            using (var store = new RuntimeStore(Main, flushInterval: NoAutoFlush))
            {
                Assert.False(store.LoadedOlderVersion);
                store.Flush();
            }
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Main));
        }
    }
}
