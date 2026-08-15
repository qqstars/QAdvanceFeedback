using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>RuntimeStore.cs is SimHub-free and link-compiled into this test project, so these
    /// tests exercise the real class - real temporary files, a real background Timer - not a
    /// re-implementation. Ported (shape) from the sibling ReliableWheelLockSlip project's
    /// RuntimeStoreTests. Persists to <c>QAdvanceFeedback.Parameters.json</c> (renamed from
    /// <c>plugin.QAdvanceFeedback.runtime.json</c>); Lock/Slip learners are now keyed per (game,car)
    /// - see <see cref="KeyedGripLearner"/> and docs\layer45-report.md's own flagged gap.</summary>
    public class RuntimeStoreTests : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "qaf-parameters-" + Guid.NewGuid() + ".json");
        private readonly string _legacyPath = Path.Combine(Path.GetTempPath(), "qaf-legacy-runtime-" + Guid.NewGuid() + ".json");
        private RuntimeStore _store;

        private static readonly TimeSpan NoAutoFlush = TimeSpan.FromMinutes(10);

        public void Dispose()
        {
            _store?.Dispose();
            TryDelete(_path);
            TryDelete(_path + ".tmp");
            TryDelete(_legacyPath);
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }

        private static Dictionary<string, GripLearnerState> OneLearner(string key, double peakG, int samples)
            => new Dictionary<string, GripLearnerState> { [key] = new GripLearnerState { PeakG = peakG, Samples = samples } };

        [Fact]
        public void Save_never_touches_disk_until_Flush_is_called()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.5, 300));

            Assert.False(File.Exists(_path));
            _store.Flush();
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void LoadLockLearners_reads_back_a_saved_value_before_any_flush()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 3.3, 250));

            _store.LoadLockLearners(out var data);
            Assert.Equal(3.3, data["GameA|#|Car1"].PeakG, 6);
            Assert.Equal(250, data["GameA|#|Car1"].Samples);
        }

        [Fact]
        public void Multiple_game_car_keys_are_persisted_independently_within_one_channel()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(new Dictionary<string, GripLearnerState>
            {
                ["GameA|#|Car1"] = new GripLearnerState { PeakG = 4.0, Samples = 300 },
                ["GameA|#|Car2"] = new GripLearnerState { PeakG = 1.2, Samples = 250 },
            });
            _store.Flush();

            using (var reopened = new RuntimeStore(_path, flushInterval: NoAutoFlush))
            {
                reopened.LoadLockLearners(out var data);
                Assert.Equal(4.0, data["GameA|#|Car1"].PeakG, 6);
                Assert.Equal(1.2, data["GameA|#|Car2"].PeakG, 6);
            }
        }

        [Fact]
        public void Lock_and_slip_learners_are_persisted_independently()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.0, 100));
            _store.SaveSlipLearners(OneLearner("GameA|#|Car1", 0.8, 60));
            _store.Flush();

            using (var reopened = new RuntimeStore(_path, flushInterval: NoAutoFlush))
            {
                reopened.LoadLockLearners(out var lockData);
                Assert.Equal(2.0, lockData["GameA|#|Car1"].PeakG, 6);
                Assert.Equal(100, lockData["GameA|#|Car1"].Samples);

                reopened.LoadSlipLearners(out var slipData);
                Assert.Equal(0.8, slipData["GameA|#|Car1"].PeakG, 6);
                Assert.Equal(60, slipData["GameA|#|Car1"].Samples);
            }
        }

        [Fact]
        public void A_flush_with_nothing_dirty_does_not_recreate_a_deleted_file()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.5, 50));
            _store.Flush();
            Assert.True(File.Exists(_path));

            File.Delete(_path);
            _store.Flush();

            Assert.False(File.Exists(_path));
        }

        [Fact]
        public void Save_leaves_no_leftover_tmp_file_behind()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));
            _store.Flush();

            Assert.False(File.Exists(_path + ".tmp"));
        }

        [Fact]
        public void A_missing_file_yields_an_empty_store_without_throwing()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.LoadLockLearners(out var data);
            Assert.Empty(data);
        }

        [Fact]
        public void A_corrupt_parameters_file_degrades_to_empty_without_throwing()
        {
            File.WriteAllText(_path, "{ not json at all ][");

            var exception = Record.Exception(() => _store = new RuntimeStore(_path, flushInterval: NoAutoFlush));

            Assert.Null(exception);
            _store.LoadLockLearners(out var data);
            Assert.Empty(data);
        }

        [Fact]
        public void Clear_removes_the_file_and_leaves_the_store_usable_afterwards()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.2, 40));
            _store.Flush();
            Assert.True(File.Exists(_path));

            _store.Clear();
            Assert.False(File.Exists(_path));
            _store.LoadLockLearners(out var afterClear);
            Assert.Empty(afterClear);

            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.2, 80));
            _store.Flush();
            _store.LoadLockLearners(out var afterResave);
            Assert.NotEmpty(afterResave);
        }

        [Fact]
        public void The_background_timer_flushes_dirty_data_without_an_explicit_Flush_call()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMilliseconds(50));
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));

            bool written = SpinWaitForFile(_path, TimeSpan.FromSeconds(3));
            Assert.True(written, "expected the background timer to flush the dirty cache to disk within 3s");
        }

        [Fact]
        public void An_unchanged_cache_is_never_rewritten_by_the_background_timer()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMilliseconds(50));
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));
            Assert.True(SpinWaitForFile(_path, TimeSpan.FromSeconds(3)));

            DateTime firstWriteUtc = File.GetLastWriteTimeUtc(_path);
            Thread.Sleep(250); // several more timer ticks with nothing new saved

            Assert.Equal(firstWriteUtc, File.GetLastWriteTimeUtc(_path));
        }

        [Fact]
        public void Dispose_stops_the_timer_so_it_never_fires_again()
        {
            _store = new RuntimeStore(_path, flushInterval: TimeSpan.FromMilliseconds(50));
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));
            Assert.True(SpinWaitForFile(_path, TimeSpan.FromSeconds(3)));

            _store.Dispose();
            File.Delete(_path);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.0, 20));

            Thread.Sleep(300);
            Assert.False(File.Exists(_path));
        }

        // ---------------------------------------------------------------------------------------
        // Legacy (old file name, pre-per-car global shape) import - exactly once, only when the new
        // file does not exist yet.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void A_legacy_named_runtime_file_is_imported_once_when_the_new_file_does_not_exist()
        {
            File.WriteAllText(_legacyPath,
                "{ \"LockLearnedPeakG\": 3.5, \"LockSamples\": 300, \"SlipLearnedPeakG\": 0.9, \"SlipSamples\": 150 }");

            _store = new RuntimeStore(_path, legacyPath: _legacyPath, flushInterval: NoAutoFlush);

            Assert.True(_store.TryGetLegacyLockSeed(out double lockPeak, out int lockSamples));
            Assert.Equal(3.5, lockPeak, 6);
            Assert.Equal(300, lockSamples);

            Assert.True(_store.TryGetLegacySlipSeed(out double slipPeak, out int slipSamples));
            Assert.Equal(0.9, slipPeak, 6);
            Assert.Equal(150, slipSamples);

            // The import is written straight into the NEW file (cache marked dirty) so a second
            // start finds the new file first and never looks at the legacy one again.
            _store.Flush();
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void The_legacy_file_is_ignored_once_the_new_file_already_exists()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 5.0, 300));
            _store.Flush();
            _store.Dispose();

            File.WriteAllText(_legacyPath, "{ \"LockLearnedPeakG\": 1.0, \"LockSamples\": 999 }");

            _store = new RuntimeStore(_path, legacyPath: _legacyPath, flushInterval: NoAutoFlush);

            _store.LoadLockLearners(out var data);
            Assert.Equal(5.0, data["GameA|#|Car1"].PeakG, 6);
            Assert.False(_store.TryGetLegacyLockSeed(out _, out _));
        }

        [Fact]
        public void A_missing_legacy_file_yields_no_seed_without_throwing()
        {
            _store = new RuntimeStore(_path, legacyPath: _legacyPath, flushInterval: NoAutoFlush);
            Assert.False(_store.TryGetLegacyLockSeed(out _, out _));
            Assert.False(_store.TryGetLegacySlipSeed(out _, out _));
        }

        // ---------------------------------------------------------------------------------------
        // GForce learner persistence - unaffected by the Lock/Slip keying change, unchanged shape.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void GForce_learners_are_persisted_independently_of_and_alongside_Lock_Slip()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.0, 100));

            var accel = new Dictionary<string, double> { ["Game|#|Car"] = 1.4 };
            var decel = new Dictionary<string, double> { ["Game|#|Car"] = 2.6 };
            _store.SaveGForceLearners(accel, decel);
            _store.Flush();

            using (var reopened = new RuntimeStore(_path, flushInterval: NoAutoFlush))
            {
                reopened.LoadLockLearners(out var lockData);
                Assert.Equal(2.0, lockData["GameA|#|Car1"].PeakG, 6);
                Assert.Equal(100, lockData["GameA|#|Car1"].Samples);

                reopened.LoadGForceLearners(out Dictionary<string, double> loadedAccel, out Dictionary<string, double> loadedDecel);
                Assert.Equal(1.4, loadedAccel["Game|#|Car"], 6);
                Assert.Equal(2.6, loadedDecel["Game|#|Car"], 6);
            }
        }

        [Fact]
        public void A_missing_runtime_file_yields_empty_GForce_learner_dictionaries()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel);

            Assert.NotNull(accel);
            Assert.NotNull(decel);
            Assert.Empty(accel);
            Assert.Empty(decel);
        }

        private static bool SpinWaitForFile(string path, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path)) return true;
                Thread.Sleep(25);
            }
            return File.Exists(path);
        }
    }
}
