using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

        public void SaveSlipLearners(Dictionary<string, GripLearnerState> data) => _cache.SaveSlipLearners(data);

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

        private void FlushTick()
        {
            RuntimeDocument snapshot = _cache.SnapshotIfDirty();
            if (snapshot == null) return;
            Task.Run(() => WriteAtomic(snapshot));
        }

        private void WriteAtomic(RuntimeDocument snapshot)
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                string json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
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
            catch (IOException e) { _logWarning?.Invoke("QAdvanceFeedback: parameters save failed - " + e.Message); }
            catch (UnauthorizedAccessException e) { _logWarning?.Invoke("QAdvanceFeedback: parameters save denied - " + e.Message); }
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
                    var loaded = JsonConvert.DeserializeObject<RuntimeDocument>(File.ReadAllText(path));
                    if (loaded != null) return Normalise(loaded);
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                logWarning?.Invoke("QAdvanceFeedback: parameters load failed, starting fresh - " + e.Message);
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
