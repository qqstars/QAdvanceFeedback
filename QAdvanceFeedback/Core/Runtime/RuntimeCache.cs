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

        /// <summary>Seeds the cache from a freshly loaded (or freshly defaulted) document - called
        /// once, at plugin initialisation. Never marks the cache dirty: loading is not itself a
        /// change that needs writing straight back out.</summary>
        public void Load(RuntimeDocument document)
        {
            lock (_gate)
            {
                _document = document ?? new RuntimeDocument();
                _dirty = false;
            }
        }

        /// <summary>Marks the cache dirty without changing its contents - used once, right after
        /// <see cref="Load"/>, when the loaded document was actually imported from the legacy-named
        /// runtime file rather than read from the new one, so the next flush writes it into the new
        /// location for real instead of leaving the import memory-only until the next unrelated
        /// change.</summary>
        public void MarkDirty()
        {
            lock (_gate) { _dirty = true; }
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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
            }
        }

        private static readonly Dictionary<string, ScaleLearnerState> EmptyScaleLearners = new Dictionary<string, ScaleLearnerState>();

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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
            }
        }

        private static readonly Dictionary<string, LockAnchorState> EmptyLockAnchors = new Dictionary<string, LockAnchorState>();

        // ---- Version 11 (1.0.7.1): Layer 3's ShakeIt calibration, and the converted shipped presets.
        // NOTE both are stored BY REFERENCE, unlike every other section here. RawCalculatorEngine holds
        // the live provider and mutates its dictionary every frame; deep-copying on save would cost a
        // histogram clone at 60fps for no benefit, and the flush path already snapshots under the lock.

        public void LoadShakeItCalibration(out Dictionary<string, RawCalculator.Calibration.CalibrationData> data)
        {
            lock (_gate) { data = new Dictionary<string, RawCalculator.Calibration.CalibrationData>(_document.ShakeItCalibration, StringComparer.Ordinal); }
        }

        public void SaveShakeItCalibration(Dictionary<string, RawCalculator.Calibration.CalibrationData> data)
        {
            lock (_gate)
            {
                _document.ShakeItCalibration = data ?? new Dictionary<string, RawCalculator.Calibration.CalibrationData>(StringComparer.Ordinal);
                _dirty = true;
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
                _dirty = true;
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
                _dirty = true;
            }
        }

        public void LoadShakeItSourceTimestamps(out Dictionary<string, long> data)
        {
            lock (_gate) { data = new Dictionary<string, long>(_document.ShakeItSourceTimestamps, StringComparer.Ordinal); }
        }

        public void SaveShakeItSourceTimestamps(Dictionary<string, long> data)
        {
            lock (_gate)
            {
                _document.ShakeItSourceTimestamps = data ?? new Dictionary<string, long>(StringComparer.Ordinal);
                _dirty = true;
            }
        }


        public void LoadGForceLearners(out Dictionary<string, double> accel, out Dictionary<string, double> decel)
        {
            lock (_gate)
            {
                accel = new Dictionary<string, double>(_document.GForceAccelLearnedMaxima, StringComparer.Ordinal);
                decel = new Dictionary<string, double>(_document.GForceDecelLearnedMaxima, StringComparer.Ordinal);
            }
        }

        public void SaveGForceLearners(Dictionary<string, double> accel, Dictionary<string, double> decel)
        {
            lock (_gate)
            {
                _document.GForceAccelLearnedMaxima = new Dictionary<string, double>(accel ?? EmptyMap, StringComparer.Ordinal);
                _document.GForceDecelLearnedMaxima = new Dictionary<string, double>(decel ?? EmptyMap, StringComparer.Ordinal);
                _dirty = true;
            }
        }

        private static readonly Dictionary<string, double> EmptyMap = new Dictionary<string, double>();

        private static readonly Dictionary<string, RawCalculator.Calibration.CalibrationData> EmptyShakeItCalibration
            = new Dictionary<string, RawCalculator.Calibration.CalibrationData>();

        private static readonly Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>> EmptyShakeItPrecalibration
            = new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>();

        private static readonly Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds> EmptyShakeItBounds
            = new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>();

        private static readonly Dictionary<string, long> EmptyShakeItStamps = new Dictionary<string, long>();

        public void Clear()
        {
            lock (_gate)
            {
                _document = new RuntimeDocument();
                _dirty = true;
            }
        }

        /// <summary>
        /// Returns a deep, independent copy of the current document if (and only if) it has changed
        /// since the last snapshot, atomically clearing the dirty flag as it does - see the sibling
        /// project's identically-named method for the full reasoning (safe outside the lock, and two
        /// concurrent callers can never both think they own writing it). Returns null when nothing
        /// has changed - the caller must treat null as "nothing to write".
        /// </summary>
        public RuntimeDocument SnapshotIfDirty()
        {
            lock (_gate)
            {
                if (!_dirty) return null;
                _dirty = false;
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
            LockScaleLearners = CloneScaleLearners(source.LockScaleLearners),
            SlipScaleLearners = CloneScaleLearners(source.SlipScaleLearners),
            SurfaceSupportByGame = new Dictionary<string, bool>(source.SurfaceSupportByGame, StringComparer.Ordinal),
            LockPhysicalReference = CloneLearners(source.LockPhysicalReference),
            SlipPhysicalReference = CloneLearners(source.SlipPhysicalReference),
            LockScaleCrossCarSeed = CloneScaleLearners(source.LockScaleCrossCarSeed),
            SlipScaleCrossCarSeed = CloneScaleLearners(source.SlipScaleCrossCarSeed),
            LockAnchors = CloneLockAnchors(source.LockAnchors),
            ShakeItCalibration = new Dictionary<string, RawCalculator.Calibration.CalibrationData>(source.ShakeItCalibration ?? EmptyShakeItCalibration, StringComparer.Ordinal),
            ShakeItPrecalibration = new Dictionary<string, Dictionary<string, RawCalculator.Calibration.PreloadedCalibrationData>>(source.ShakeItPrecalibration ?? EmptyShakeItPrecalibration, StringComparer.Ordinal),
            ShakeItGameBounds = new Dictionary<string, RawCalculator.Calibration.GameCalibrationBounds>(source.ShakeItGameBounds ?? EmptyShakeItBounds, StringComparer.Ordinal),
            ShakeItSourceTimestamps = new Dictionary<string, long>(source.ShakeItSourceTimestamps ?? EmptyShakeItStamps, StringComparer.Ordinal),
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
