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
        };

        private static Dictionary<string, GripLearnerState> CloneLearners(Dictionary<string, GripLearnerState> source)
        {
            var copy = new Dictionary<string, GripLearnerState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, GripLearnerState> pair in source)
                copy[pair.Key] = new GripLearnerState { PeakG = pair.Value.PeakG, Samples = pair.Value.Samples };
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
