using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using QAdvanceFeedback.Core.Health;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Runtime;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Owns Layer 4's learned parameters (the two keyed <see cref="KeyedGripLearner"/> families) and
    /// the G-force channels' AUTO maxima across restarts, backed by an in-memory <see cref="RuntimeCache"/>
    /// plus the file/timer plumbing the brief requires, persisted to <c>QAdvanceFeedback.Parameters.json</c>
    /// under SimHub's <c>PluginsData\Common</c> folder (resolved by the caller via
    /// <c>PluginManager.GetCommonStoragePath</c> - never hard-coded here).
    /// <para/>
    /// Renamed from <c>plugin.QAdvanceFeedback.runtime.json</c> - a file still present under that OLD
    /// name is imported exactly ONCE (only when the NEW file does not exist yet - see
    /// <see cref="LoadFromDisk"/>), so upgrading past this change does not discard whatever the plugin
    /// had already learned. The old file's single GLOBAL Lock/Slip peak+sample-count (predating
    /// per-game/per-car keying entirely) becomes the COLD-START SEED for the first brand-new
    /// (game, car) key each channel encounters after the upgrade, rather than being discarded outright
    /// or arbitrarily assigned to one specific game/car it was never actually recorded against - see
    /// <see cref="KeyedGripLearner.SeedLegacy"/>'s own remarks for why this is the least-destructive
    /// reading available.
    /// <list type="bullet">
    /// <item><see cref="SaveLockLearners"/>/<see cref="SaveSlipLearners"/>/<see cref="LoadLockLearners"/>/
    /// <see cref="LoadSlipLearners"/> touch ONLY the in-memory cache - never the filesystem - so
    /// the hot per-frame path (the learner updates every qualifying frame) never stalls on disk I/O.</item>
    /// <item>THE WRITER (owner, 2026-11-21; docs\parameters-file-format-report.md): one ONE-SHOT
    /// <see cref="Timer"/> (due <see cref="DefaultFlushInterval"/>, period infinite). Its tick writes synchronously
    /// inside try/finally, and the finally re-arms it for another 5 seconds - so the next write is counted from the
    /// END of the previous one, two writes can never overlap and nothing ever queues. Only changed sections are
    /// regenerated (<see cref="RuntimeDocumentWriter"/>), and a write whose sections all came out as before is
    /// skipped.</item>
    /// <item>Shutdown (<see cref="Dispose"/>, AppDomain unload, process exit - whichever comes first, once): the timer
    /// is stopped (due infinite), disposed - waiting for a tick in progress - and the very last write is made.</item>
    /// <item>Startup recovery: a leftover temporary file next to a present parameters file is a write that never
    /// finished - deleted; with the parameters file missing, a temporary file that parses is renamed into its place.</item>
    /// <item>The runtime file is read once, at construction (plugin Init). A missing or corrupt file
    /// degrades to a fresh, empty document rather than throwing out of the constructor.</item>
    /// <item>Writes are atomic and durable (<see cref="AtomicFile"/>: the temporary file is flushed to the disk, then
    /// swapped in with one replace), under one write lock shared by the timer, <see cref="Flush"/> and shutdown.</item>
    /// <item><see cref="Flush"/> performs a SYNCHRONOUS write-if-dirty, so <c>Plugin.End</c>'s call
    /// chain is guaranteed to have written the last few seconds of learning before the process is
    /// allowed to exit.</item>
    /// </list>
    /// <paramref name="logInfo"/>/<paramref name="logWarning"/>-style optional logging delegates keep
    /// this class's ONLY dependencies at System.IO/System.Threading/Newtonsoft.Json - no SimHub
    /// reference - so it links directly into the SimHub-free test project for real file-based tests.
    /// </summary>
    public sealed class RuntimeStore : IDisposable
    {
        public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

        private readonly string _path;
        private readonly Action<string> _logWarning;
        private readonly RuntimeCache _cache = new RuntimeCache();
        /// <summary>One write at a time - the timer's tick, <see cref="Flush"/>, <see cref="Clear"/> and the final
        /// write at shutdown all take it, so the newest snapshot is always the last one on disk.</summary>
        private readonly object _writeLock = new object();
        private readonly Timer _timer;
        private readonly TimeSpan _interval;
        private readonly RuntimeDocumentWriter _writer = new RuntimeDocumentWriter();

        /// <summary>A write failed: the next one regenerates every section and writes even if nothing changed since.
        /// Under <see cref="_writeLock"/>.</summary>
        private bool _fileStale;

        /// <summary>0 = running, 1 = shut down (the timer stopped and the last write made).</summary>
        private int _shutDown;

        /// <summary>How long shutdown waits for a tick already writing. Generous enough for a multi-megabyte
        /// write to a busy disk, short enough that a wedged one cannot hang the host's shutdown.</summary>
        private static readonly TimeSpan TickWaitTimeout = TimeSpan.FromSeconds(10);
        private string _loggedFlushTickFault;

        private readonly double _legacyLockPeakG;
        private readonly int _legacyLockSamples;
        private readonly double _legacySlipPeakG;
        private readonly int _legacySlipSamples;

        /// <param name="path">Where the runtime file lives - resolved by the caller from
        /// <c>PluginManager.GetCommonStoragePath</c>.</param>
        /// <param name="legacyPath">The old <c>plugin.QAdvanceFeedback.runtime.json</c> location (same
        /// caller-resolved way) - imported once, in memory, if <paramref name="path"/> does not exist
        /// yet. See <see cref="LoadFromDisk"/>.</param>
        /// <param name="flushInterval">Defaults to <see cref="DefaultFlushInterval"/>; overridable
        /// only so tests can use a short interval instead of waiting multiple seconds.</param>
        public RuntimeStore(string path, string legacyPath = null, TimeSpan? flushInterval = null,
            Action<string> logInfo = null, Action<string> logWarning = null)
        {
            _path = path;
            _logWarning = logWarning;

            RuntimeDocument document = LoadFromDisk(path, legacyPath, logInfo, logWarning,
                out bool importedFromLegacy, out _legacyLockPeakG, out _legacyLockSamples,
                out _legacySlipPeakG, out _legacySlipSamples, out int loadedVersion);

            _cache.Load(document);
            // An older file is rewritten in the current format by the first tick (5 s after start), not left as it
            // was until enough new driving happens to save every section again (owner, 2026-11-22: "the parameter
            // file, were not updated, when restart the simhub").
            LoadedOlderVersion = loadedVersion > 0 && loadedVersion < document.Version;
            if (importedFromLegacy || LoadedOlderVersion) _cache.MarkDirty();

            _interval = flushInterval ?? DefaultFlushInterval;
            // ONE-SHOT: due after the interval, never periodic - FlushTick re-arms it once its write is done.
            _timer = new Timer(_ => FlushTick(), null, _interval, Timeout.InfiniteTimeSpan);
            AppDomain.CurrentDomain.DomainUnload += OnHostUnloading;
            AppDomain.CurrentDomain.ProcessExit += OnHostUnloading;
        }

        /// <summary>The file read at startup was written by an older version: every section is written back in the
        /// current format by the first tick. Learners whose saved shape changed (the Slip Angle learners, v15) are
        /// re-exported by the plugin right after their import, so that write carries the new shape.</summary>
        public bool LoadedOlderVersion { get; }

        public void LoadLockLearners(out Dictionary<string, GripLearnerState> data) => _cache.LoadLockLearners(out data);

        public void SaveLockLearners(Dictionary<string, GripLearnerState> data) => _cache.SaveLockLearners(data);

        public void LoadSlipLearners(out Dictionary<string, GripLearnerState> data) => _cache.LoadSlipLearners(out data);

        /// <summary>Version 3 (items 2/3) - see <see cref="RuntimeCache"/>'s own remarks.</summary>
        public void LoadLockScaleLearners(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadLockScaleLearners(out data);

        public void SaveLockScaleLearners(Dictionary<string, ScaleLearnerState> data) => _cache.SaveLockScaleLearners(data);

        public void LoadSlipScaleLearners(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadSlipScaleLearners(out data);

        public void SaveSlipScaleLearners(Dictionary<string, ScaleLearnerState> data) => _cache.SaveSlipScaleLearners(data);

        // Clutch Bite (docs\clutch-bite-design.md).
        public void LoadClutchBiteLearners(out Dictionary<string, Core.Clutch.ClutchBiteLearnerState> data) => _cache.LoadClutchBiteLearners(out data);
        public void SaveClutchBiteLearners(Dictionary<string, Core.Clutch.ClutchBiteLearnerState> data) => _cache.SaveClutchBiteLearners(data);
        public string LoadLastClutchKey() => _cache.LoadLastClutchKey();
        public void SaveLastClutchKey(string key) => _cache.SaveLastClutchKey(key);

        // Restart display (docs\architecture.md): the last (game, car) the key data was shown for.
        public bool TryLoadLastKeyDataContext(out string gameId, out string carId) => _cache.TryLoadLastKeyDataContext(out gameId, out carId);
        public void SaveLastKeyDataContext(string gameId, string carId) => _cache.SaveLastKeyDataContext(gameId, carId);

        // Slip Angle restart display (docs\full-mvvm-plan.md 3.5): the last (game, car, identity tier).
        public bool TryLoadLastSlipAngleContext(out string gameId, out string carId, out Core.SourceNativeness tier) =>
            _cache.TryLoadLastSlipAngleContext(out gameId, out carId, out tier);
        public void SaveLastSlipAngleContext(string gameId, string carId, Core.SourceNativeness tier) =>
            _cache.SaveLastSlipAngleContext(gameId, carId, tier);

        public void LoadSurfaceSupport(out Dictionary<string, bool> data) => _cache.LoadSurfaceSupport(out data);

        public void SaveSurfaceSupport(Dictionary<string, bool> data) => _cache.SaveSurfaceSupport(data);

        /// <summary>Version 4 (docs\cold-start-and-timing-fix-report.md) - see
        /// <see cref="Core.Normalized.NormalizedWheelLockSlipEngine.LockPhysicalReference"/>.</summary>
        public void LoadLockPhysicalReference(out Dictionary<string, GripLearnerState> data) => _cache.LoadLockPhysicalReference(out data);

        public void SaveLockPhysicalReference(Dictionary<string, GripLearnerState> data) => _cache.SaveLockPhysicalReference(data);

        public void LoadSlipPhysicalReference(out Dictionary<string, GripLearnerState> data) => _cache.LoadSlipPhysicalReference(out data);

        public void SaveSlipPhysicalReference(Dictionary<string, GripLearnerState> data) => _cache.SaveSlipPhysicalReference(data);

        /// <summary>Version 4 - see <see cref="Core.Normalized.KeyedScaleLearner.ExportCrossCarSeeds"/>.</summary>
        public void LoadLockScaleCrossCarSeed(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadLockScaleCrossCarSeed(out data);

        public void SaveLockScaleCrossCarSeed(Dictionary<string, ScaleLearnerState> data) => _cache.SaveLockScaleCrossCarSeed(data);

        public void LoadSlipScaleCrossCarSeed(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadSlipScaleCrossCarSeed(out data);

        public void SaveSlipScaleCrossCarSeed(Dictionary<string, ScaleLearnerState> data) => _cache.SaveSlipScaleCrossCarSeed(data);

        public void SaveSlipLearners(Dictionary<string, GripLearnerState> data) => _cache.SaveSlipLearners(data);

        /// <summary>Version 8 (docs\v1068-four-range-report.md, Feature C) - WheelLock's own learned
        /// S75/S90 anchors. WHEELLOCK ONLY, no Slip equivalent.</summary>
        public void LoadLockAnchors(out Dictionary<string, LockAnchorState> data) => _cache.LoadLockAnchors(out data);

        public void SaveLockAnchors(Dictionary<string, LockAnchorState> data) => _cache.SaveLockAnchors(data);

        /// <summary>Version 11 (1.0.7.1) - Layer 3's ShakeIt calibration and the converted shipped
        /// presets. See <c>Core.RawCalculator.Calibration.CalibrationDataProvider</c>.</summary>
        public void LoadShakeItCalibration(out Dictionary<string, Core.RawCalculator.Calibration.CalibrationData> data)
            => _cache.LoadShakeItCalibration(out data);

        public void SaveShakeItCalibration(Dictionary<string, Core.RawCalculator.Calibration.CalibrationData> data)
            => _cache.SaveShakeItCalibration(data);

        public void LoadShakeItPrecalibration(out Dictionary<string, Dictionary<string, Core.RawCalculator.Calibration.PreloadedCalibrationData>> data)
            => _cache.LoadShakeItPrecalibration(out data);

        public void SaveShakeItPrecalibration(Dictionary<string, Dictionary<string, Core.RawCalculator.Calibration.PreloadedCalibrationData>> data)
            => _cache.SaveShakeItPrecalibration(data);

        public void LoadShakeItGameBounds(out Dictionary<string, Core.RawCalculator.Calibration.GameCalibrationBounds> data)
            => _cache.LoadShakeItGameBounds(out data);

        public void SaveShakeItGameBounds(Dictionary<string, Core.RawCalculator.Calibration.GameCalibrationBounds> data)
            => _cache.SaveShakeItGameBounds(data);

        /// <summary>Version 12 (docs\slip-angle-design.md 9.5.9) - the Slip Angle learners. Like every
        /// Save here this touches only the in-memory cache: the DATA thread exports
        /// (<c>KeyedSlipAngleLearner.SaveIfDue</c>) and saves; the timer thread only snapshots the cache.</summary>
        public void LoadSlipAngleLearners(out Dictionary<string, Core.SlipAngle.SlipAngleKeyState> data)
            => _cache.LoadSlipAngleLearners(out data);

        public void SaveSlipAngleLearners(Dictionary<string, Core.SlipAngle.SlipAngleKeyState> data)
            => _cache.SaveSlipAngleLearners(data);

        /// <summary>Version 13 - the Slip Angle speed-aware lateral G bands (same threading as the learners).</summary>
        public void LoadSlipAngleSpeedBands(out Dictionary<string, Core.SlipAngle.SlipAngleSpeedBandSetState> data)
            => _cache.LoadSlipAngleSpeedBands(out data);

        public void SaveSlipAngleSpeedBands(Dictionary<string, Core.SlipAngle.SlipAngleSpeedBandSetState> data)
            => _cache.SaveSlipAngleSpeedBands(data);

        /// <summary>Version 14 - the evidence behind each key's SMax (Lock or Slip), saved throttled.</summary>
        public void LoadScaleEvidence(bool isLock, out Dictionary<string, ScaleEvidenceState> data)
            => _cache.LoadScaleEvidence(isLock, out data);

        public void SaveScaleEvidence(bool isLock, Dictionary<string, ScaleEvidenceState> data)
            => _cache.SaveScaleEvidence(isLock, data);

        public void LoadShakeItSourceTimestamps(out Dictionary<string, long> data)
            => _cache.LoadShakeItSourceTimestamps(out data);

        public void SaveShakeItSourceTimestamps(Dictionary<string, long> data)
            => _cache.SaveShakeItSourceTimestamps(data);

        /// <summary>The pre-per-car global Lock peak/samples imported from a legacy-named runtime
        /// file at construction, if any - false when nothing usable was found (fresh install, or the
        /// new file already existed so no legacy import was attempted at all). See this class's own
        /// remarks and <see cref="KeyedGripLearner.SeedLegacy"/>.</summary>
        public bool TryGetLegacyLockSeed(out double peakG, out int samples)
        {
            peakG = _legacyLockPeakG; samples = _legacyLockSamples;
            return samples > 0;
        }

        /// <summary>The Slip channel's equivalent of <see cref="TryGetLegacyLockSeed"/>.</summary>
        public bool TryGetLegacySlipSeed(out double peakG, out int samples)
        {
            peakG = _legacySlipPeakG; samples = _legacySlipSamples;
            return samples > 0;
        }

        /// <summary>Loads the G-force channels' persisted AUTO-learned maxima (accel, decel), keyed
        /// exactly as <c>GForceMaxLearner.MakeKey</c> produces - both dictionaries are empty (never
        /// null) on a fresh/corrupt runtime file.</summary>
        public void LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel)
            => _cache.LoadGForceLearners(out accel, out decel);

        public void LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel,
            out Dictionary<string, double> lateral)
            => _cache.LoadGForceLearners(out accel, out decel, out lateral);

        /// <summary>Saves the G-force channels' current AUTO-learned maxima - called once per frame
        /// from <c>QAdvanceFeedback.DataUpdate</c> (in-memory only, see this class's own remarks; the
        /// background timer/Flush is what actually reaches disk).</summary>
        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel)
            => _cache.SaveGForceLearners(accel, decel);

        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel,
            Dictionary<string, double> lateral)
            => _cache.SaveGForceLearners(accel, decel, lateral);

        /// <summary>Synchronous write-if-dirty - every real caller (<c>Plugin.End</c>) runs off the
        /// game/data thread already, so blocking here is both safe and exactly what gives shutdown
        /// its "flush once more before exit" guarantee.</summary>
        public void Flush()
        {
            // The write lock makes this unambiguously last: a tick already writing finishes first, and this
            // snapshot - taken inside the lock - is the newest. (The old design queued writes on the thread pool,
            // where an older snapshot could land on top of the final one.)
            WriteIfDirty();
        }

        /// <summary>Discards all learned data, in memory and on disk.</summary>
        public void Clear()
        {
            _cache.Clear();
            Flush();

            lock (_writeLock)
            {
                if (!string.IsNullOrEmpty(_path) && File.Exists(_path)) File.Delete(_path);
            }
        }

        /// <summary>
        /// The <see cref="Timer"/> callback itself - runs on a raw ThreadPool thread with NOTHING
        /// upstream of it to catch a throw (unlike <c>Plugin.DataUpdate</c>, which SimHub calls from
        /// its own guarded dispatch loop - see the pipeline-exception-safety report). In .NET
        /// Framework, an unhandled exception on a ThreadPool thread is NOT contained to that one
        /// callback: it takes down the entire host process. <see cref="_cache"/>.SnapshotIfDirty is
        /// ordinary in-memory dictionary/state work today and not expected to throw, but "not expected
        /// to" is exactly the gap this project's own report calls out - this handler must survive
        /// ANY exception, not just the ones <see cref="WriteIfDirty"/> already anticipates from disk
        /// I/O. Logged once (not per-tick) via the same distinct-message dedup <c>QAdvanceFeedback.
        /// DataUpdate</c> uses, so a persistent fault does not spam the log every
        /// <see cref="DefaultFlushInterval"/>.
        /// </summary>
        private void FlushTick()
        {
            try
            {
                WriteIfDirty();
            }
            catch (Exception e)
            {
                string message = "QAdvanceFeedback: background flush tick failed - " + e;
                if (!string.Equals(_loggedFlushTickFault, message, StringComparison.Ordinal))
                {
                    _loggedFlushTickFault = message;
                    _logWarning?.Invoke(message);
                }
                HealthRegistry.Report(HealthSubsystems.BackgroundFlush, HealthSeverity.Degraded,
                    "Health.Impact.BackgroundFlush", e.ToString());
            }
            finally
            {
                // RE-ARM: the next write is due one interval after THIS one finished (owner's pattern) - never
                // while shutting down, and a timer disposed meanwhile is simply left alone.
                if (Volatile.Read(ref _shutDown) == 0)
                {
                    try { _timer?.Change(_interval, Timeout.InfiniteTimeSpan); }
                    catch (ObjectDisposedException) { }
                }
            }
        }

        /// <summary>
        /// The one write path: under <see cref="_writeLock"/>, snapshot the cache if anything was saved since, regenerate
        /// only the changed sections, and write the file - unless every regenerated section came out exactly as already
        /// written. A failed write marks everything dirty again and the file stale, so the next tick retries in full.
        /// </summary>
        private void WriteIfDirty()
        {
            lock (_writeLock)
            {
                RuntimeDocument snapshot = _cache.SnapshotIfDirty(out HashSet<string> sections);
                if (snapshot == null || string.IsNullOrEmpty(_path)) return;
                bool written = false;
                try
                {
                    // The ShakeIt calibration types get short JSON names and drop their live-only members - see
                    // ShakeItCalibrationContractResolver; the writer uses the same settings as the reader.
                    string json = _writer.Compose(snapshot, _fileStale ? null : sections, out bool changed);
                    if (!changed && !_fileStale) { written = true; return; }   // the file already holds exactly this

                    // ATOMIC AND DURABLE - see Core.Runtime.AtomicFile: flushed to the disk, then swapped in with one
                    // replace, so the file is never missing (the owner-reported 2026-10-02 defect).
                    AtomicFile.WriteAllText(_path, json);
                    written = true;
                    _fileStale = false;
                }
                catch (IOException e)
                {
                    _logWarning?.Invoke("QAdvanceFeedback: parameters save failed - " + e.Message);
                    HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                        "Health.Impact.RuntimePersistence", e.ToString());
                }
                catch (UnauthorizedAccessException e)
                {
                    _logWarning?.Invoke("QAdvanceFeedback: parameters save denied - " + e.Message);
                    HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                        "Health.Impact.RuntimePersistence", e.ToString());
                }
                // Belt-and-suspenders (pipeline-exception-safety report): a JsonException from serialising, or an
                // ArgumentException/PathTooLongException/NotSupportedException from the file calls, must not escape
                // onto the timer thread either.
                catch (Exception e)
                {
                    _logWarning?.Invoke("QAdvanceFeedback: parameters save failed unexpectedly - " + e.Message);
                    HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                        "Health.Impact.RuntimePersistence", e.ToString());
                }
                finally
                {
                    if (!written)
                    {
                        _fileStale = true;
                        _cache.MarkDirty();   // nothing is lost: the next tick writes it all
                    }
                }
            }
        }

        /// <summary>
        /// Reads the new-named runtime file if present; otherwise, as a ONE-TIME import, reads the
        /// OLD-named file (<paramref name="legacyPath"/>) if THAT is present, converting its flat
        /// global Lock/Slip peak+sample-count out through <paramref name="legacyLockPeakG"/> etc.
        /// (consumed once by the caller via <see cref="KeyedGripLearner.SeedLegacy"/>) and carrying
        /// its G-force dictionaries straight across unchanged (their shape did not change). Degrades
        /// to a fresh, empty document on any read/parse failure of EITHER file rather than throwing.
        /// </summary>
        private static RuntimeDocument LoadFromDisk(
            string path, string legacyPath, Action<string> logInfo, Action<string> logWarning,
            out bool importedFromLegacy,
            out double legacyLockPeakG, out int legacyLockSamples,
            out double legacySlipPeakG, out int legacySlipSamples, out int loadedVersion)
        {
            importedFromLegacy = false;
            loadedVersion = 0;
            legacyLockPeakG = 0.0; legacyLockSamples = 0;
            legacySlipPeakG = 0.0; legacySlipSamples = 0;

            RecoverTemporaryFile(path, logInfo, logWarning);

            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<RuntimeDocument>(
                        File.ReadAllText(path), ShakeItCalibrationContractResolver.Settings(Formatting.None));
                    if (loaded != null)
                    {
                        loadedVersion = loaded.Version;
                        return Normalise(loaded);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: parameters load failed, starting fresh - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                    "Health.Impact.RuntimePersistence", e.ToString());
            }

            try
            {
                if (!string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath))
                {
                    var legacy = JsonConvert.DeserializeObject<LegacyRuntimeDocument>(File.ReadAllText(legacyPath));
                    if (legacy != null)
                    {
                        importedFromLegacy = true;
                        legacyLockPeakG = legacy.LockLearnedPeakG; legacyLockSamples = legacy.LockSamples;
                        legacySlipPeakG = legacy.SlipLearnedPeakG; legacySlipSamples = legacy.SlipSamples;

                        logInfo?.Invoke("QAdvanceFeedback: imported learned parameters from the legacy runtime file (" + legacyPath + ")");

                        return new RuntimeDocument
                        {
                            GForceAccelLearnedMaxima = legacy.GForceAccelLearnedMaxima ?? new Dictionary<string, double>(),
                            GForceDecelLearnedMaxima = legacy.GForceDecelLearnedMaxima ?? new Dictionary<string, double>(),
                        };
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: legacy parameters import failed, starting fresh - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                    "Health.Impact.RuntimePersistence", e.ToString());
            }

            return new RuntimeDocument();
        }

        /// <summary>
        /// STARTUP RECOVERY (owner, 2026-11-21). The temporary file is the write in progress
        /// (<see cref="AtomicFile.TemporarySuffix"/>). Both present: the swap never happened, so the parameters file is
        /// the last complete write - the temporary one is deleted. Parameters file missing but a temporary file present:
        /// if the temporary file parses as a parameters document it is renamed into place and used; if it does not, it is
        /// left alone (the next write overwrites it) and the plugin starts fresh. Never throws.
        /// </summary>
        private static void RecoverTemporaryFile(string path, Action<string> logInfo, Action<string> logWarning)
        {
            if (string.IsNullOrEmpty(path)) return;
            string temporary = path + AtomicFile.TemporarySuffix;
            try
            {
                AtomicFile.Recovery result = AtomicFile.RecoverTemporary(path, text =>
                {
                    try
                    {
                        return JsonConvert.DeserializeObject<RuntimeDocument>(
                            text, ShakeItCalibrationContractResolver.Settings(Formatting.None)) != null;
                    }
                    catch (JsonException) { return false; }
                });
                if (result == AtomicFile.Recovery.Restored)
                    logInfo?.Invoke("QAdvanceFeedback: parameters file recovered from its temporary file (" + temporary + ")");
                else if (result == AtomicFile.Recovery.Unusable)
                    logWarning?.Invoke("QAdvanceFeedback: an unreadable temporary parameters file was ignored (" + temporary + ")");
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: parameters temporary-file recovery failed - " + e.Message);
            }
        }

        /// <summary>A hand-edited or partially-written new-shape file can deserialise successfully but
        /// leave either dictionary null (Newtonsoft does not run field initialisers for a property
        /// present-but-null in the source JSON) - normalised back to empty here so every downstream
        /// caller can rely on "never null", same guarantee <see cref="RuntimeCache"/> already gives.</summary>
        private static RuntimeDocument Normalise(RuntimeDocument document)
        {
            document.Version = new RuntimeDocument().Version;   // it is written back in the current format
            document.LockLearners ??= new Dictionary<string, GripLearnerState>();
            document.SlipLearners ??= new Dictionary<string, GripLearnerState>();
            document.GForceAccelLearnedMaxima ??= new Dictionary<string, double>();
            document.GForceDecelLearnedMaxima ??= new Dictionary<string, double>();
            document.GForceLatLearnedMaxima ??= new Dictionary<string, double>();
            document.LockScaleLearners ??= new Dictionary<string, ScaleLearnerState>();
            document.SlipScaleLearners ??= new Dictionary<string, ScaleLearnerState>();
            document.SurfaceSupportByGame ??= new Dictionary<string, bool>();
            document.LockPhysicalReference ??= new Dictionary<string, GripLearnerState>();
            document.SlipPhysicalReference ??= new Dictionary<string, GripLearnerState>();
            document.LockScaleCrossCarSeed ??= new Dictionary<string, ScaleLearnerState>();
            document.SlipScaleCrossCarSeed ??= new Dictionary<string, ScaleLearnerState>();
            document.ClutchBiteLearners ??= new Dictionary<string, Core.Clutch.ClutchBiteLearnerState>();
            document.SlipAngleLearners ??= new Dictionary<string, Core.SlipAngle.SlipAngleKeyState>();
            document.SlipAngleSpeedBands ??= new Dictionary<string, Core.SlipAngle.SlipAngleSpeedBandSetState>();
            document.LockScaleEvidence ??= new Dictionary<string, ScaleEvidenceState>();
            document.SlipScaleEvidence ??= new Dictionary<string, ScaleEvidenceState>();
            return document;
        }

        /// <summary>Shuts down (see <see cref="Shutdown"/>): stops the timer and makes the very last write.</summary>
        public void Dispose() => Shutdown();

        private void OnHostUnloading(object sender, EventArgs e) => Shutdown();

        /// <summary>
        /// SHUTDOWN, exactly once, whichever comes first - <see cref="Dispose"/> (Plugin.End), AppDomain unload or
        /// process exit (owner, 2026-11-21): the timer is stopped (due infinite, period infinite), disposed - waiting
        /// up to <see cref="TickWaitTimeout"/> for a tick that is writing - and the very last write is made. Saves
        /// after this reach the cache only.
        /// </summary>
        private void Shutdown()
        {
            if (Interlocked.Exchange(ref _shutDown, 1) != 0) return;
            AppDomain.CurrentDomain.DomainUnload -= OnHostUnloading;
            AppDomain.CurrentDomain.ProcessExit -= OnHostUnloading;
            try
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                var done = new ManualResetEvent(false);
                // Disposed only when the wait completed: a tick still running past the timeout signals it later.
                if (_timer.Dispose(done) && done.WaitOne(TickWaitTimeout)) done.Dispose();
            }
            catch (ObjectDisposedException) { }

            try { WriteIfDirty(); }
            catch (Exception e)
            {
                _logWarning?.Invoke("QAdvanceFeedback: final parameters save failed - " + e.Message);
            }
        }
    }
}
