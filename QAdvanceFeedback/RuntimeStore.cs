using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
    /// <item>A single background <see cref="Timer"/> flushes the cache to
    /// <c>QAdvanceFeedback.Parameters.json</c> every <see cref="DefaultFlushInterval"/>, and only
    /// when the cache is actually dirty - an unchanged cache is never rewritten. The write itself runs
    /// via <see cref="Task.Run(Action)"/>, off whatever thread the timer callback lands on.</item>
    /// <item>The runtime file is read once, at construction (plugin Init). A missing or corrupt file
    /// degrades to a fresh, empty document rather than throwing out of the constructor.</item>
    /// <item>Writes are atomic: a temp file is written and swapped in with <see cref="File.Move"/>
    /// only once complete, under a lock so the timer's periodic flush and an explicit
    /// <see cref="Flush"/> (from End()) can never interleave their temp-file dance.</item>
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
        private readonly object _fileLock = new object();
        private readonly Timer _timer;
        private bool _disposed;
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
                out _legacySlipPeakG, out _legacySlipSamples);

            _cache.Load(document);
            if (importedFromLegacy) _cache.MarkDirty();

            TimeSpan interval = flushInterval ?? DefaultFlushInterval;
            _timer = new Timer(_ => FlushTick(), null, interval, interval);
        }

        public void LoadLockLearners(out Dictionary<string, GripLearnerState> data) => _cache.LoadLockLearners(out data);

        public void SaveLockLearners(Dictionary<string, GripLearnerState> data) => _cache.SaveLockLearners(data);

        public void LoadSlipLearners(out Dictionary<string, GripLearnerState> data) => _cache.LoadSlipLearners(out data);

        /// <summary>Version 3 (items 2/3) - see <see cref="RuntimeCache"/>'s own remarks.</summary>
        public void LoadLockScaleLearners(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadLockScaleLearners(out data);

        public void SaveLockScaleLearners(Dictionary<string, ScaleLearnerState> data) => _cache.SaveLockScaleLearners(data);

        public void LoadSlipScaleLearners(out Dictionary<string, ScaleLearnerState> data) => _cache.LoadSlipScaleLearners(out data);

        public void SaveSlipScaleLearners(Dictionary<string, ScaleLearnerState> data) => _cache.SaveSlipScaleLearners(data);

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

        /// <summary>Saves the G-force channels' current AUTO-learned maxima - called once per frame
        /// from <c>QAdvanceFeedback.DataUpdate</c> (in-memory only, see this class's own remarks; the
        /// background timer/Flush is what actually reaches disk).</summary>
        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel)
            => _cache.SaveGForceLearners(accel, decel);

        /// <summary>Synchronous write-if-dirty - every real caller (<c>Plugin.End</c>) runs off the
        /// game/data thread already, so blocking here is both safe and exactly what gives shutdown
        /// its "flush once more before exit" guarantee.</summary>
        public void Flush()
        {
            RuntimeDocument snapshot = _cache.SnapshotIfDirty();
            if (snapshot == null) return;
            WriteAtomic(snapshot);
        }

        /// <summary>Discards all learned data, in memory and on disk.</summary>
        public void Clear()
        {
            _cache.Clear();
            Flush();

            lock (_fileLock)
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
        /// ANY exception, not just the ones <see cref="WriteAtomic"/> already anticipates from disk
        /// I/O. Logged once (not per-tick) via the same distinct-message dedup <c>QAdvanceFeedback.
        /// DataUpdate</c> uses, so a persistent fault does not spam the log every
        /// <see cref="DefaultFlushInterval"/>.
        /// </summary>
        private void FlushTick()
        {
            try
            {
                RuntimeDocument snapshot = _cache.SnapshotIfDirty();
                if (snapshot == null) return;
                Task.Run(() => WriteAtomic(snapshot));
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
        }

        private void WriteAtomic(RuntimeDocument snapshot)
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                // The ShakeIt calibration types get short JSON names and drop their live-only
                // members - see ShakeItCalibrationContractResolver. The reader below MUST use the
                // same settings or every calibration reads back empty.
                string json = JsonConvert.SerializeObject(
                    snapshot, ShakeItCalibrationContractResolver.Settings(Formatting.Indented));
                lock (_fileLock)
                {
                    string directory = Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    string temporary = _path + ".tmp";
                    File.WriteAllText(temporary, json);
                    if (File.Exists(_path)) File.Delete(_path);
                    File.Move(temporary, _path);
                }
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
            // Belt-and-suspenders (pipeline-exception-safety report): this method now also runs
            // detached inside Task.Run, off FlushTick's own guarded call on the Timer thread (see
            // FlushTick's remarks) - a JsonException from SerializeObject, or an ArgumentException/
            // PathTooLongException/NotSupportedException from CreateDirectory/WriteAllText/Move (none
            // of which derive from IOException or UnauthorizedAccessException) must not escape onto
            // that detached Task either, so it is caught and logged here too rather than left to
            // become an unobserved task exception.
            catch (Exception e)
            {
                _logWarning?.Invoke("QAdvanceFeedback: parameters save failed unexpectedly - " + e.Message);
                HealthRegistry.Report(HealthSubsystems.RuntimePersistence, HealthSeverity.Degraded,
                    "Health.Impact.RuntimePersistence", e.ToString());
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
            out double legacySlipPeakG, out int legacySlipSamples)
        {
            importedFromLegacy = false;
            legacyLockPeakG = 0.0; legacyLockSamples = 0;
            legacySlipPeakG = 0.0; legacySlipSamples = 0;

            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var loaded = JsonConvert.DeserializeObject<RuntimeDocument>(
                        File.ReadAllText(path), ShakeItCalibrationContractResolver.Settings(Formatting.None));
                    if (loaded != null) return Normalise(loaded);
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

        /// <summary>A hand-edited or partially-written new-shape file can deserialise successfully but
        /// leave either dictionary null (Newtonsoft does not run field initialisers for a property
        /// present-but-null in the source JSON) - normalised back to empty here so every downstream
        /// caller can rely on "never null", same guarantee <see cref="RuntimeCache"/> already gives.</summary>
        private static RuntimeDocument Normalise(RuntimeDocument document)
        {
            document.LockLearners ??= new Dictionary<string, GripLearnerState>();
            document.SlipLearners ??= new Dictionary<string, GripLearnerState>();
            document.GForceAccelLearnedMaxima ??= new Dictionary<string, double>();
            document.GForceDecelLearnedMaxima ??= new Dictionary<string, double>();
            document.LockScaleLearners ??= new Dictionary<string, ScaleLearnerState>();
            document.SlipScaleLearners ??= new Dictionary<string, ScaleLearnerState>();
            document.SurfaceSupportByGame ??= new Dictionary<string, bool>();
            document.LockPhysicalReference ??= new Dictionary<string, GripLearnerState>();
            document.SlipPhysicalReference ??= new Dictionary<string, GripLearnerState>();
            document.LockScaleCrossCarSeed ??= new Dictionary<string, ScaleLearnerState>();
            document.SlipScaleCrossCarSeed ??= new Dictionary<string, ScaleLearnerState>();
            return document;
        }

        /// <summary>Stops the background timer. Does NOT flush - callers that want a guaranteed
        /// final write must call <see cref="Flush"/> first (see <c>Plugin.End</c>).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
        }
    }
}
