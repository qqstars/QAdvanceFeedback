using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using QAdvanceFeedback.Core.Health;
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

        // ---------------------------------------------------------------------------------------
        // RuntimeDocument Version 4 (docs\cold-start-and-timing-fix-report.md) - the shared,
        // (game,car)-only physical-limit detector (NormalizedWheelLockSlipEngine.LockPhysicalReference/
        // SlipPhysicalReference) and the per-(game,source) cross-car cold-start seed
        // (KeyedScaleLearner.ExportCrossCarSeeds/ImportCrossCarSeeds) - both previously session-scoped
        // only, so a restart re-cold-started calibration every time (flagged, not fixed, in
        // docs\f1-normalization-fix-report.md's own Concerns).
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Physical_reference_learners_survive_a_restart_independently_of_the_source_keyed_learners()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockPhysicalReference(OneLearner("F12025|#|Sauber|#||#|Sealed", 3.4, 220));
            _store.SaveSlipPhysicalReference(OneLearner("F12025|#|Sauber|#||#|Sealed", 2.1, 180));
            _store.Flush();

            using (var reopened = new RuntimeStore(_path, flushInterval: NoAutoFlush))
            {
                reopened.LoadLockPhysicalReference(out var lockData);
                Assert.Equal(3.4, lockData["F12025|#|Sauber|#||#|Sealed"].PeakG, 6);
                Assert.Equal(220, lockData["F12025|#|Sauber|#||#|Sealed"].Samples);

                reopened.LoadSlipPhysicalReference(out var slipData);
                Assert.Equal(2.1, slipData["F12025|#|Sauber|#||#|Sealed"].PeakG, 6);
                Assert.Equal(180, slipData["F12025|#|Sauber|#||#|Sealed"].Samples);
            }
        }

        [Fact]
        public void A_missing_runtime_file_yields_empty_physical_reference_dictionaries()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.LoadLockPhysicalReference(out var lockData);
            _store.LoadSlipPhysicalReference(out var slipData);

            Assert.Empty(lockData);
            Assert.Empty(slipData);
        }

        [Fact]
        public void A_version_3_file_with_no_physical_reference_key_at_all_still_loads_cleanly()
        {
            // A file written before this task's Version-4 bump - simulates the "upgrading past this
            // change" path (docs\cold-start-and-timing-fix-report.md, mirroring the 2->3 bump's own
            // precedent: no explicit migration code needed).
            File.WriteAllText(_path, "{ \"Version\": 3, \"LockLearners\": {}, \"SlipLearners\": {} }");

            var exception = Record.Exception(() => _store = new RuntimeStore(_path, flushInterval: NoAutoFlush));
            Assert.Null(exception);

            _store.LoadLockPhysicalReference(out var lockData);
            _store.LoadLockScaleCrossCarSeed(out var crossCarData);
            Assert.Empty(lockData);
            Assert.Empty(crossCarData);
        }

        [Fact]
        public void Cross_car_scale_seeds_are_persisted_independently_per_channel()
        {
            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.SaveLockScaleCrossCarSeed(new Dictionary<string, ScaleLearnerState>
            {
                ["F12025|#|ShakeIt"] = new ScaleLearnerState { ColdCeiling = 88.0, ColdIsPrimaryTier = true },
            });
            _store.SaveSlipScaleCrossCarSeed(new Dictionary<string, ScaleLearnerState>
            {
                ["F12025|#|ShakeIt"] = new ScaleLearnerState { ColdCeiling = 62.0, ColdIsPrimaryTier = false },
            });
            _store.Flush();

            using (var reopened = new RuntimeStore(_path, flushInterval: NoAutoFlush))
            {
                reopened.LoadLockScaleCrossCarSeed(out var lockData);
                Assert.Equal(88.0, lockData["F12025|#|ShakeIt"].ColdCeiling, 6);
                Assert.True(lockData["F12025|#|ShakeIt"].ColdIsPrimaryTier);

                reopened.LoadSlipScaleCrossCarSeed(out var slipData);
                Assert.Equal(62.0, slipData["F12025|#|ShakeIt"].ColdCeiling, 6);
                Assert.False(slipData["F12025|#|ShakeIt"].ColdIsPrimaryTier);
            }
        }

        // ---------------------------------------------------------------------------------------
        // PIPELINE-EXCEPTION-SAFETY (docs\pipeline-exception-safety-report.md): the background flush
        // Timer's callback runs on a raw ThreadPool thread with nothing upstream of it to catch a
        // throw (unlike Plugin.DataUpdate, which SimHub's own dispatch loop already guards). An
        // unhandled exception directly on that thread would crash the whole host process - see
        // FlushTick's own remarks. These tests force a write failure a plain IOException/
        // UnauthorizedAccessException catch would NOT have caught (an invalid path segment raises
        // ArgumentException from Directory.CreateDirectory/File.WriteAllText, not either of those
        // types) and confirm the store - and the test process itself - survive it.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void An_invalid_flush_path_does_not_crash_the_background_timer_or_the_process()
        {
            // "|" is not a legal character within a Windows path segment - Directory.CreateDirectory/
            // File.WriteAllText raise ArgumentException for it, a type neither WriteAtomic's nor (pre-
            // fix) FlushTick's own catch clauses covered.
            string invalidPath = Path.Combine(Path.GetTempPath(), "qaf-bad-path-" + Guid.NewGuid(), "sub|dir", "params.json");
            _store = new RuntimeStore(invalidPath, flushInterval: TimeSpan.FromMilliseconds(50));
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));

            // Several timer ticks' worth of time: if FlushTick's guard were missing, the raw
            // ThreadPool timer callback throwing here would take the whole process down with it -
            // simply reaching the next line is itself evidence the fix holds.
            Thread.Sleep(400);

            // The store must still be usable afterwards, not left poisoned by the failed tick.
            var exception = Record.Exception(() => _store.SaveLockLearners(OneLearner("GameA|#|Car1", 2.0, 20)));
            Assert.Null(exception);

            // HEALTH REGISTRY (resilience-hardening task) - the fault must not be silently swallowed:
            // WriteAtomic's own catch(Exception) records it under RuntimePersistence (the same fault
            // reaches HealthRegistry regardless of which of WriteAtomic's several catch clauses
            // actually matches the ArgumentException this invalid path segment raises).
            Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.RuntimePersistence);
        }

        [Fact]
        public void Flush_with_an_invalid_path_does_not_throw_synchronously_either()
        {
            string invalidPath = Path.Combine(Path.GetTempPath(), "qaf-bad-path-" + Guid.NewGuid(), "sub|dir", "params.json");
            _store = new RuntimeStore(invalidPath, flushInterval: NoAutoFlush);
            _store.SaveLockLearners(OneLearner("GameA|#|Car1", 1.0, 10));

            // Mirrors Plugin.End's own call chain (a synchronous Flush guaranteed not to throw out of
            // shutdown) - WriteAtomic's broadened catch(Exception) is what protects this path now.
            var exception = Record.Exception(() => _store.Flush());
            Assert.Null(exception);
            Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.RuntimePersistence);
        }

        [Fact]
        public void A_corrupt_runtime_file_is_recorded_in_the_health_registry_and_still_degrades_to_a_fresh_document()
        {
            File.WriteAllText(_path, "{ not valid json ][");

            _store = new RuntimeStore(_path, flushInterval: NoAutoFlush);
            _store.LoadLockLearners(out var data);

            Assert.Empty(data);
            Assert.Contains(HealthRegistry.Snapshot(), e => e.Subsystem == HealthSubsystems.RuntimePersistence);
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
