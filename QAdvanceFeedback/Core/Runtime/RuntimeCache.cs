using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.Runtime
{
    /// <summary>
    /// The in-memory cache standing between the game/data thread's learner-save calls and the
    /// <c>QAdvanceFeedback.Parameters.json</c> file on disk - ported (shape, not exact contents) from
    /// the sibling ReliableWheelLockSlip project's <c>Core/Runtime/RuntimeCache.cs</c>. Lock/Slip are
    /// now keyed dictionaries (per game+car), matching how the G-force maxima were already stored.
    /// Everything here is synchronous and touches no file - only a background flush timer or an
    /// explicit final flush at shutdown (both owned OUTSIDE Core, in <c>RuntimeStore</c>) ever touches
    /// the filesystem, exclusively through <see cref="SnapshotIfDirty"/>.
    /// <para/>
    /// Thread safety: one <c>lock</c> around every access to the backing document and the dirty
    /// flag - the two real callers (the game/data thread, saving the learners every frame they
    /// update, and the background flush timer's <see cref="SnapshotIfDirty"/>) are expected to
    /// overlap in practice.
    /// </summary>
    public sealed class RuntimeCache
    {
        private readonly object _gate = new object();
        private RuntimeDocument _document = new RuntimeDocument();
        private bool _dirty;

        /// <summary>The top-level <see cref="RuntimeDocument"/> fields changed since the last snapshot (by field name), so
        /// the writer regenerates only those sections (docs\parameters-file-format-report.md). Under <see cref="_gate"/>.</summary>
        private readonly HashSet<string> _dirtySections = new HashSet<string>(StringComparer.Ordinal);

        private void MarkSection(params string[] sections)
        {
            foreach (string s in sections) _dirtySections.Add(s);
            _dirty = true;
        }

        private void MarkAllSections()
        {
            foreach (System.Reflection.FieldInfo f in typeof(RuntimeDocument).GetFields()) _dirtySections.Add(f.Name);
            _dirty = true;
        }

        /// <summary>Seeds the cache from a freshly loaded (or freshly defaulted) document - called
        /// once, at plugin initialisation. Never marks the cache dirty: loading is not itself a
        /// change that needs writing straight back out.</summary>
        public void Load(RuntimeDocument document)
        {
            lock (_gate)
            {
                _document = document ?? new RuntimeDocument();
                _dirty = false;
                _dirtySections.Clear();
            }
        }

        /// <summary>Marks the cache dirty without changing its contents - used once, right after
        /// <see cref="Load"/>, when the loaded document was actually imported from the legacy-named
        /// runtime file rather than read from the new one, so the next flush writes it into the new
        /// location for real instead of leaving the import memory-only until the next unrelated
        /// change.</summary>
        public void MarkDirty()
        {
            lock (_gate) { MarkAllSections(); }
        }

        public void LoadLockLearners(out Dictionary<string, GripLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, GripLearnerState>(_document.LockLearners, StringComparer.Ordinal); }
        }

        public void SaveLockLearners(Dictionary<string, GripLearnerState> data)
        {
            lock (_gate)
            {
                _document.LockLearners = new Dictionary<string, GripLearnerState>(data ?? EmptyLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.LockLearners));
            }
        }

        public void LoadSlipLearners(out Dictionary<string, GripLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, GripLearnerState>(_document.SlipLearners, StringComparer.Ordinal); }
        }

        public void SaveSlipLearners(Dictionary<string, GripLearnerState> data)
        {
            lock (_gate)
            {
                _document.SlipLearners = new Dictionary<string, GripLearnerState>(data ?? EmptyLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipLearners));
            }
        }

        private static readonly Dictionary<string, GripLearnerState> EmptyLearners = new Dictionary<string, GripLearnerState>();

        /// <summary>Version 3 (items 2/3): the Raw-side per-source calibration learner's persisted cold
        /// ceilings - see <c>KeyedScaleLearner</c>. Same in-memory-only convention as
        /// <see cref="LoadLockLearners"/>/<see cref="SaveLockLearners"/> above.</summary>
        public void LoadLockScaleLearners(out Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, ScaleLearnerState>(_document.LockScaleLearners, StringComparer.Ordinal); }
        }

        public void SaveLockScaleLearners(Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate)
            {
                _document.LockScaleLearners = new Dictionary<string, ScaleLearnerState>(data ?? EmptyScaleLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.LockScaleLearners));
            }
        }

        public void LoadSlipScaleLearners(out Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, ScaleLearnerState>(_document.SlipScaleLearners, StringComparer.Ordinal); }
        }

        public void SaveSlipScaleLearners(Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate)
            {
                _document.SlipScaleLearners = new Dictionary<string, ScaleLearnerState>(data ?? EmptyScaleLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipScaleLearners));
            }
        }

        private static readonly Dictionary<string, ScaleLearnerState> EmptyScaleLearners = new Dictionary<string, ScaleLearnerState>();

        public void LoadClutchBiteLearners(out Dictionary<string, Clutch.ClutchBiteLearnerState> data)
        {
            lock (_gate) { data = CloneClutch(_document.ClutchBiteLearners); }
        }

        public void SaveClutchBiteLearners(Dictionary<string, Clutch.ClutchBiteLearnerState> data)
        {
            lock (_gate)
            {
                _document.ClutchBiteLearners = CloneClutch(data);
                MarkSection(nameof(RuntimeDocument.ClutchBiteLearners));
            }
        }

        public string LoadLastClutchKey()
        {
            lock (_gate) { return _document.LastClutchKey; }
        }

        /// <summary>The last key-data context (see <see cref="RuntimeDocument.LastKeyDataGame"/>); false
        /// when none was ever saved.</summary>
        public bool TryLoadLastKeyDataContext(out string gameId, out string carId)
        {
            lock (_gate)
            {
                gameId = _document.LastKeyDataGame;
                carId = _document.LastKeyDataCar ?? string.Empty;
                return gameId != null;
            }
        }

        public void SaveLastKeyDataContext(string gameId, string carId)
        {
            if (gameId == null) return;
            carId = carId ?? string.Empty;
            lock (_gate)
            {
                if (string.Equals(_document.LastKeyDataGame, gameId, StringComparison.Ordinal)
                    && string.Equals(_document.LastKeyDataCar, carId, StringComparison.Ordinal)) return;
                _document.LastKeyDataGame = gameId;
                _document.LastKeyDataCar = carId;
                MarkSection(nameof(RuntimeDocument.LastKeyDataGame), nameof(RuntimeDocument.LastKeyDataCar));
            }
        }

        /// <summary>The last Slip Angle key-data context (see <see cref="RuntimeDocument.LastSlipAngleGame"/>);
        /// false when none was ever saved. An unreadable tier reads as Unknown.</summary>
        public bool TryLoadLastSlipAngleContext(out string gameId, out string carId, out SourceNativeness tier)
        {
            lock (_gate)
            {
                gameId = _document.LastSlipAngleGame;
                carId = _document.LastSlipAngleCar ?? string.Empty;
                string t = _document.LastSlipAngleTier;
                tier = !string.IsNullOrEmpty(t) && Enum.TryParse(t, false, out SourceNativeness parsed)
                    && Enum.IsDefined(typeof(SourceNativeness), parsed) ? parsed : SourceNativeness.Unknown;
                return !string.IsNullOrEmpty(gameId);
            }
        }

        /// <summary>Saves the last Slip Angle context; a no-op (not dirty) unless it changed, or for no game.</summary>
        public void SaveLastSlipAngleContext(string gameId, string carId, SourceNativeness tier)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            carId = carId ?? string.Empty;
            string t = tier.ToString();
            lock (_gate)
            {
                if (string.Equals(_document.LastSlipAngleGame, gameId, StringComparison.Ordinal)
                    && string.Equals(_document.LastSlipAngleCar, carId, StringComparison.Ordinal)
                    && string.Equals(_document.LastSlipAngleTier, t, StringComparison.Ordinal)) return;
                _document.LastSlipAngleGame = gameId;
                _document.LastSlipAngleCar = carId;
                _document.LastSlipAngleTier = t;
                MarkSection(nameof(RuntimeDocument.LastSlipAngleGame), nameof(RuntimeDocument.LastSlipAngleCar), nameof(RuntimeDocument.LastSlipAngleTier));
            }
        }

        public void SaveLastClutchKey(string key)
        {
            lock (_gate)
            {
                if (string.Equals(_document.LastClutchKey, key, StringComparison.Ordinal)) return;
                _document.LastClutchKey = key;
                MarkSection(nameof(RuntimeDocument.LastClutchKey));
            }
        }

        private static Dictionary<string, Clutch.ClutchBiteLearnerState> CloneClutch(Dictionary<string, Clutch.ClutchBiteLearnerState> source)
        {
            var copy = new Dictionary<string, Clutch.ClutchBiteLearnerState>(StringComparer.Ordinal);
            if (source == null) return copy;
            foreach (KeyValuePair<string, Clutch.ClutchBiteLearnerState> kv in source)
            {
                if (kv.Key == null || kv.Value == null) continue;
                copy[kv.Key] = new Clutch.ClutchBiteLearnerState
                {
                    Samples = new List<double>(kv.Value.Samples ?? new List<double>()),
                    TotalCount = kv.Value.TotalCount,
                    Matured = kv.Value.Matured,
                };
            }
            return copy;
        }

        /// <summary>Version 3 (item 2): per-GAME telemetry support detection - see
        /// <c>KeyedTelemetrySupport</c>.</summary>
        public void LoadSurfaceSupport(out Dictionary<string, bool> data)
        {
            lock (_gate) { data = new Dictionary<string, bool>(_document.SurfaceSupportByGame, StringComparer.Ordinal); }
        }

        public void SaveSurfaceSupport(Dictionary<string, bool> data)
        {
            lock (_gate)
            {
                _document.SurfaceSupportByGame = new Dictionary<string, bool>(data ?? EmptySurfaceSupport, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SurfaceSupportByGame));
            }
        }

        private static readonly Dictionary<string, bool> EmptySurfaceSupport = new Dictionary<string, bool>();

        /// <summary>Version 4 (docs\cold-start-and-timing-fix-report.md): the shared, (game,car)-only
        /// physical-limit detector - see <c>NormalizedWheelLockSlipEngine.LockPhysicalReference</c>. Same
        /// in-memory-only convention as <see cref="LoadLockLearners"/>/<see cref="SaveLockLearners"/>.</summary>
        public void LoadLockPhysicalReference(out Dictionary<string, GripLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, GripLearnerState>(_document.LockPhysicalReference, StringComparer.Ordinal); }
        }

        public void SaveLockPhysicalReference(Dictionary<string, GripLearnerState> data)
        {
            lock (_gate)
            {
                _document.LockPhysicalReference = new Dictionary<string, GripLearnerState>(data ?? EmptyLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.LockPhysicalReference));
            }
        }

        public void LoadSlipPhysicalReference(out Dictionary<string, GripLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, GripLearnerState>(_document.SlipPhysicalReference, StringComparer.Ordinal); }
        }

        public void SaveSlipPhysicalReference(Dictionary<string, GripLearnerState> data)
        {
            lock (_gate)
            {
                _document.SlipPhysicalReference = new Dictionary<string, GripLearnerState>(data ?? EmptyLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipPhysicalReference));
            }
        }

        /// <summary>Version 4: the per-(gameId,sourceIdentity) cross-car cold-start seed - see
        /// <c>KeyedScaleLearner.ExportCrossCarSeeds</c>/<c>ImportCrossCarSeeds</c>.</summary>
        public void LoadLockScaleCrossCarSeed(out Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, ScaleLearnerState>(_document.LockScaleCrossCarSeed, StringComparer.Ordinal); }
        }

        public void SaveLockScaleCrossCarSeed(Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate)
            {
                _document.LockScaleCrossCarSeed = new Dictionary<string, ScaleLearnerState>(data ?? EmptyScaleLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.LockScaleCrossCarSeed));
            }
        }

        public void LoadSlipScaleCrossCarSeed(out Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate) { data = new Dictionary<string, ScaleLearnerState>(_document.SlipScaleCrossCarSeed, StringComparer.Ordinal); }
        }

        public void SaveSlipScaleCrossCarSeed(Dictionary<string, ScaleLearnerState> data)
        {
            lock (_gate)
            {
                _document.SlipScaleCrossCarSeed = new Dictionary<string, ScaleLearnerState>(data ?? EmptyScaleLearners, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipScaleCrossCarSeed));
            }
        }

        /// <summary>Version 8 (docs\v1068-four-range-report.md, Feature C): WheelLock's own learned
        /// S75/S90 anchors - see <c>LockAnchorLearner</c>. WHEELLOCK ONLY, same in-memory-only convention
        /// as <see cref="LoadLockLearners"/>/<see cref="SaveLockLearners"/> above.</summary>
        public void LoadLockAnchors(out Dictionary<string, LockAnchorState> data)
        {
            lock (_gate) { data = new Dictionary<string, LockAnchorState>(_document.LockAnchors, StringComparer.Ordinal); }
        }

        public void SaveLockAnchors(Dictionary<string, LockAnchorState> data)
        {
            lock (_gate)
            {
                _document.LockAnchors = new Dictionary<string, LockAnchorState>(data ?? EmptyLockAnchors, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.LockAnchors));
            }
        }

        private static readonly Dictionary<string, LockAnchorState> EmptyLockAnchors = new Dictionary<string, LockAnchorState>();

        // ---- Version 11 (1.0.7.1): Layer 3's slip / lock calibration (SlipLockCalibration, Version 18 name), and the
        // presets converted from ShakeIt's own files.
        // NOTE both are stored BY REFERENCE, unlike every other section here. RawCalculatorEngine holds
        // the live provider and mutates its dictionary every frame; deep-copying on save would cost a
        // histogram clone at 60fps for no benefit, and the flush path already snapshots under the lock.

        public void LoadSlipLockCalibration(out Dictionary<string, RawCalculator.Calibration.CalibrationData> data)
        {
            lock (_gate) { data = new Dictionary<string, RawCalculator.Calibration.CalibrationData>(_document.SlipLockCalibration, StringComparer.Ordinal); }
        }

        public void SaveSlipLockCalibration(Dictionary<string, RawCalculator.Calibration.CalibrationData> data)
        {
            lock (_gate)
            {
                _document.SlipLockCalibration = data ?? new Dictionary<string, RawCalculator.Calibration.CalibrationData>(StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipLockCalibration));
            }
        }

        public void LoadShakeItPrecalibration(out Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>> data)
        {
            lock (_gate) { data = new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>(_document.ShakeItPrecalibration, StringComparer.Ordinal); }
        }

        public void SaveShakeItPrecalibration(Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>> data)
        {
            lock (_gate)
            {
                _document.ShakeItPrecalibration = data ?? new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>(StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.ShakeItPrecalibration));
            }
        }

        public void LoadShakeItGameBounds(out Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds> data)
        {
            lock (_gate) { data = new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>(_document.ShakeItGameBounds, StringComparer.Ordinal); }
        }

        public void SaveShakeItGameBounds(Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds> data)
        {
            lock (_gate)
            {
                _document.ShakeItGameBounds = data ?? new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>(StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.ShakeItGameBounds));
            }
        }



        // ---- Version 12 (slip-angle plan, Task 15): the Slip Angle learners. Stored BY REFERENCE (a
        // shallow dictionary copy): KeyedSlipAngleLearner.Export builds fresh state objects on the data
        // thread and nothing mutates them afterwards, so the flush snapshot can share them safely - and a
        // deep clone of ~1.5k doubles per learner on every save would buy nothing.

        public void LoadSlipAngleLearners(out Dictionary<string, SlipAngle.SlipAngleKeyState> data)
        {
            lock (_gate) { data = new Dictionary<string, SlipAngle.SlipAngleKeyState>(_document.SlipAngleLearners ?? EmptySlipAngle, StringComparer.Ordinal); }
        }

        public void SaveSlipAngleLearners(Dictionary<string, SlipAngle.SlipAngleKeyState> data)
        {
            lock (_gate)
            {
                _document.SlipAngleLearners = new Dictionary<string, SlipAngle.SlipAngleKeyState>(data ?? EmptySlipAngle, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipAngleLearners));
            }
        }

        // ---- Version 13: the Slip Angle speed bands - by reference, like the learners (Export builds fresh
        // objects on the data thread and nothing mutates them afterwards).

        public void LoadSlipAngleSpeedBands(out Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState> data)
        {
            lock (_gate) { data = new Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState>(_document.SlipAngleSpeedBands ?? EmptySpeedBands, StringComparer.Ordinal); }
        }

        public void SaveSlipAngleSpeedBands(Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState> data)
        {
            lock (_gate)
            {
                _document.SlipAngleSpeedBands = new Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState>(data ?? EmptySpeedBands, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.SlipAngleSpeedBands));
            }
        }

        private static readonly Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState> EmptySpeedBands =
            new Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState>();

        // ---- Version 14: the SMax evidence - by reference (ExportEvidence hands out snapshots it never
        // mutates again), so a save copies only the dictionary, never the distributions.

        public void LoadScaleEvidence(bool isLock, out Dictionary<string, ScaleEvidenceState> data)
        {
            lock (_gate)
            {
                Dictionary<string, ScaleEvidenceState> source = isLock ? _document.LockScaleEvidence : _document.SlipScaleEvidence;
                data = new Dictionary<string, ScaleEvidenceState>(source ?? EmptyScaleEvidence, StringComparer.Ordinal);
            }
        }

        public void SaveScaleEvidence(bool isLock, Dictionary<string, ScaleEvidenceState> data)
        {
            lock (_gate)
            {
                var copy = new Dictionary<string, ScaleEvidenceState>(data ?? EmptyScaleEvidence, StringComparer.Ordinal);
                if (isLock) _document.LockScaleEvidence = copy;
                else _document.SlipScaleEvidence = copy;
                MarkSection(isLock ? nameof(RuntimeDocument.LockScaleEvidence) : nameof(RuntimeDocument.SlipScaleEvidence));
            }
        }

        private static readonly Dictionary<string, ScaleEvidenceState> EmptyScaleEvidence =
            new Dictionary<string, ScaleEvidenceState>();

        private static readonly Dictionary<string, SlipAngle.SlipAngleKeyState> EmptySlipAngle =
            new Dictionary<string, SlipAngle.SlipAngleKeyState>();

        public void LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel)
            => LoadGForceLearners(out accel, out decel, out _);

        public void LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel,
            out Dictionary<string, double> lateral)
        {
            lock (_gate)
            {
                accel = new Dictionary<string, double>(_document.GForceAccelLearnedMaxima, StringComparer.Ordinal);
                decel = new Dictionary<string, double>(_document.GForceDecelLearnedMaxima, StringComparer.Ordinal);
                lateral = new Dictionary<string, double>(_document.GForceLatLearnedMaxima ?? EmptyMap, StringComparer.Ordinal);
            }
        }

        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel)
            => SaveGForceLearners(accel, decel, null);

        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel,
            Dictionary<string, double> lateral)
        {
            lock (_gate)
            {
                _document.GForceAccelLearnedMaxima = new Dictionary<string, double>(accel ?? EmptyMap, StringComparer.Ordinal);
                _document.GForceDecelLearnedMaxima = new Dictionary<string, double>(decel ?? EmptyMap, StringComparer.Ordinal);
                _document.GForceLatLearnedMaxima = new Dictionary<string, double>(lateral ?? EmptyMap, StringComparer.Ordinal);
                MarkSection(nameof(RuntimeDocument.GForceAccelLearnedMaxima), nameof(RuntimeDocument.GForceDecelLearnedMaxima), nameof(RuntimeDocument.GForceLatLearnedMaxima));
            }
        }

        private static readonly Dictionary<string, double> EmptyMap = new Dictionary<string, double>();

        private static readonly Dictionary<string, RawCalculator.Calibration.CalibrationData> EmptySlipLockCalibration
            = new Dictionary<string, RawCalculator.Calibration.CalibrationData>();

        private static readonly Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>> EmptyShakeItPrecalibration
            = new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>();

        private static readonly Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds> EmptyShakeItBounds
            = new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>();


        public void Clear()
        {
            lock (_gate)
            {
                _document = new RuntimeDocument();
                MarkAllSections();
            }
        }

        /// <summary>
        /// Returns a deep, independent copy of the current document if (and only if) it has changed
        /// since the last snapshot, atomically clearing the dirty flag as it does - see the sibling
        /// project's identically-named method for the full reasoning (safe outside the lock, and two
        /// concurrent callers can never both think they own writing it). Returns null when nothing
        /// has changed - the caller must treat null as "nothing to write".
        /// </summary>
        public RuntimeDocument SnapshotIfDirty() => SnapshotIfDirty(out _);

        /// <summary>As <see cref="SnapshotIfDirty()"/>, also handing over (and clearing) the names of the sections
        /// changed since the last snapshot.</summary>
        public RuntimeDocument SnapshotIfDirty(out HashSet<string> changedSections)
        {
            lock (_gate)
            {
                changedSections = null;
                if (!_dirty) return null;
                _dirty = false;
                changedSections = new HashSet<string>(_dirtySections, StringComparer.Ordinal);
                _dirtySections.Clear();
                return Clone(_document);
            }
        }

        private static RuntimeDocument Clone(RuntimeDocument source) => new RuntimeDocument
        {
            Version = source.Version,
            LockLearners = CloneLearners(source.LockLearners),
            SlipLearners = CloneLearners(source.SlipLearners),
            GForceAccelLearnedMaxima = new Dictionary<string, double>(source.GForceAccelLearnedMaxima, StringComparer.Ordinal),
            GForceDecelLearnedMaxima = new Dictionary<string, double>(source.GForceDecelLearnedMaxima, StringComparer.Ordinal),
            // Was missing: the learned lateral maxima never reached the file (found writing the section writer).
            GForceLatLearnedMaxima = new Dictionary<string, double>(source.GForceLatLearnedMaxima ?? EmptyMap, StringComparer.Ordinal),
            LockScaleLearners = CloneScaleLearners(source.LockScaleLearners),
            SlipScaleLearners = CloneScaleLearners(source.SlipScaleLearners),
            SurfaceSupportByGame = new Dictionary<string, bool>(source.SurfaceSupportByGame, StringComparer.Ordinal),
            LockPhysicalReference = CloneLearners(source.LockPhysicalReference),
            SlipPhysicalReference = CloneLearners(source.SlipPhysicalReference),
            LockScaleCrossCarSeed = CloneScaleLearners(source.LockScaleCrossCarSeed),
            SlipScaleCrossCarSeed = CloneScaleLearners(source.SlipScaleCrossCarSeed),
            LockAnchors = CloneLockAnchors(source.LockAnchors),
            SlipLockCalibration = new Dictionary<string, RawCalculator.Calibration.CalibrationData>(source.SlipLockCalibration ?? EmptySlipLockCalibration, StringComparer.Ordinal),
            ShakeItPrecalibration = new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>(source.ShakeItPrecalibration ?? EmptyShakeItPrecalibration, StringComparer.Ordinal),
            ShakeItGameBounds = new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>(source.ShakeItGameBounds ?? EmptyShakeItBounds, StringComparer.Ordinal),
            ClutchBiteLearners = CloneClutch(source.ClutchBiteLearners),
            LastClutchKey = source.LastClutchKey,
            LastKeyDataGame = source.LastKeyDataGame,
            LastKeyDataCar = source.LastKeyDataCar,
            LastSlipAngleGame = source.LastSlipAngleGame,
            LastSlipAngleCar = source.LastSlipAngleCar,
            LastSlipAngleTier = source.LastSlipAngleTier,
            SlipAngleLearners = new Dictionary<string, SlipAngle.SlipAngleKeyState>(source.SlipAngleLearners ?? EmptySlipAngle, StringComparer.Ordinal),
            SlipAngleSpeedBands = new Dictionary<string, SlipAngle.SlipAngleSpeedBandSetState>(source.SlipAngleSpeedBands ?? EmptySpeedBands, StringComparer.Ordinal),
            LockScaleEvidence = new Dictionary<string, ScaleEvidenceState>(source.LockScaleEvidence ?? EmptyScaleEvidence, StringComparer.Ordinal),
            SlipScaleEvidence = new Dictionary<string, ScaleEvidenceState>(source.SlipScaleEvidence ?? EmptyScaleEvidence, StringComparer.Ordinal),
        };

        private static Dictionary<string, LockAnchorState> CloneLockAnchors(Dictionary<string, LockAnchorState> source)
        {
            var copy = new Dictionary<string, LockAnchorState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, LockAnchorState> pair in source)
                copy[pair.Key] = new LockAnchorState
                {
                    S75 = pair.Value.S75,
                    Hits75 = pair.Value.Hits75,
                    Candidate75 = pair.Value.Candidate75,
                    S90 = pair.Value.S90,
                    Hits90 = pair.Value.Hits90,
                    Candidate90 = pair.Value.Candidate90,
                    // The ratio fields too - without them the learned S90/S75 ratios never reached the
                    // file and every restart fell back to the 0.90/0.70 seeds (LockAnchorPersistenceTests).
                    RatioLevel75 = pair.Value.RatioLevel75,
                    RatioHits75 = pair.Value.RatioHits75,
                    RatioCandidate75 = pair.Value.RatioCandidate75,
                    RatioLevel90 = pair.Value.RatioLevel90,
                    RatioHits90 = pair.Value.RatioHits90,
                    RatioCandidate90 = pair.Value.RatioCandidate90,
                    PhysicalRatioLevel75 = pair.Value.PhysicalRatioLevel75,
                    PhysicalRatioHits75 = pair.Value.PhysicalRatioHits75,
                    PhysicalRatioCandidate75 = pair.Value.PhysicalRatioCandidate75,
                    PhysicalRatioLevel90 = pair.Value.PhysicalRatioLevel90,
                    PhysicalRatioHits90 = pair.Value.PhysicalRatioHits90,
                    PhysicalRatioCandidate90 = pair.Value.PhysicalRatioCandidate90,
                };
            return copy;
        }

        private static Dictionary<string, GripLearnerState> CloneLearners(Dictionary<string, GripLearnerState> source)
        {
            var copy = new Dictionary<string, GripLearnerState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, GripLearnerState> pair in source)
                copy[pair.Key] = new GripLearnerState
                {
                    PeakG = pair.Value.PeakG,
                    Samples = pair.Value.Samples,
                    GMech = pair.Value.GMech,
                    K = pair.Value.K,
                    // Persisted per GripLearnerState's own contract ("so a returning car resumes warm") -
                    // this copy used to drop them, so they never reached the file (found by the Task G
                    // save/load round-trip test, the same gap the anchors copy had).
                    RaiseCandidateG = pair.Value.RaiseCandidateG,
                    RaiseCandidateHits = pair.Value.RaiseCandidateHits,
                    LowerCandidateG = pair.Value.LowerCandidateG,
                    LowerCandidateHits = pair.Value.LowerCandidateHits,
                    QuietStreak = pair.Value.QuietStreak,
                };
            return copy;
        }

        private static Dictionary<string, ScaleLearnerState> CloneScaleLearners(Dictionary<string, ScaleLearnerState> source)
        {
            var copy = new Dictionary<string, ScaleLearnerState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, ScaleLearnerState> pair in source)
                copy[pair.Key] = new ScaleLearnerState { ColdCeiling = pair.Value.ColdCeiling, ColdIsPrimaryTier = pair.Value.ColdIsPrimaryTier };
            return copy;
        }
    }
}
