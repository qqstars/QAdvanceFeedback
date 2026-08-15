using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// One <see cref="GripLearner"/> per (gameId, carId), so the Lock/Slip channels' learned peak-g
    /// reference switches automatically when either the game or the car changes, exactly like
    /// <see cref="GForce.GForceMaxLearner"/> already does for the G-force AUTO maxima. This is the fix
    /// for the gap docs\layer45-report.md flagged explicitly under "Concerns / seams for later work":
    /// "The learned grip reference is a single global value per channel, not per-car/per-game ...
    /// switching cars mid-session shares one learned peak across both."
    /// <para/>
    /// Key format and separator are deliberately identical to <see cref="GForce.GForceMaxLearner.MakeKey"/>
    /// (same reasoning: a separator that cannot appear in a SimHub GameName/CarId), so the two learner
    /// families read as one consistent convention rather than two subtly different ones.
    /// </summary>
    public sealed class KeyedGripLearner
    {
        private const string KeySeparator = "|#|";

        /// <summary>The key a pre-per-car runtime file's single global peak/sample count is imported
        /// under (see <see cref="SeedLegacy"/>) - deliberately not a valid (gameId, carId) pair (both
        /// halves would have to independently equal this exact literal), so it can never collide with
        /// a real game/car key.</summary>
        public const string LegacyImportKey = "__legacy_import__";

        private readonly Dictionary<string, GripLearner> _learners = new Dictionary<string, GripLearner>(StringComparer.Ordinal);
        private readonly double _learnCapG;

        /// <param name="learnCapG">The channel-specific learning-path reject ceiling (see
        /// <see cref="GripLearner.LearnCapG"/>) passed to every <see cref="GripLearner"/> THIS
        /// instance creates (fresh, legacy-seeded, or restored via <see cref="ImportAll"/>) - defaults
        /// to <see cref="GripLearner.MaxPlausibleG"/> so every pre-existing
        /// <c>new KeyedGripLearner()</c> call site is unaffected. See
        /// <see cref="NormalizedWheelLockSlipEngine"/>'s own Lock/Slip constants for the asymmetric
        /// values this plugin actually uses.</param>
        public KeyedGripLearner(double learnCapG = GripLearner.MaxPlausibleG)
        {
            _learnCapG = learnCapG;
        }

        public static string MakeKey(string gameId, string carId)
            => (gameId ?? string.Empty) + KeySeparator + (carId ?? string.Empty);

        /// <summary>The learned peak for this (gameId, carId), or <see cref="GripLearner.SeedPeakG"/>
        /// if nothing has been observed for it yet - never creates an entry (a read must not have a
        /// side effect on what gets persisted).</summary>
        public double LearnedPeakG(string gameId, string carId) => Find(gameId, carId)?.LearnedPeakG ?? GripLearner.SeedPeakG;

        public int Samples(string gameId, string carId) => Find(gameId, carId)?.Samples ?? 0;

        public double Confidence(string gameId, string carId) => Find(gameId, carId)?.Confidence ?? 0.0;

        /// <summary><paramref name="magnitudeG"/> as a fraction of this (gameId, carId)'s own learned
        /// peak - see <see cref="GripLearner.Ratio"/>. Creates a fresh (or legacy-seeded) learner for
        /// a key seen for the very first time, mirroring <see cref="Observe"/>.</summary>
        public double Ratio(string gameId, string carId, double magnitudeG) => GetOrCreate(gameId, carId).Ratio(magnitudeG);

        /// <summary>Folds one qualifying observation into this (gameId, carId)'s own learner - see
        /// <see cref="GripLearner.Observe"/>.</summary>
        public void Observe(string gameId, string carId, double magnitudeG) => GetOrCreate(gameId, carId).Observe(magnitudeG);

        /// <summary>Read-only lookup: the key's own learner if it has been created yet (via
        /// <see cref="Observe"/>/<see cref="Ratio"/>/<see cref="ImportAll"/>), otherwise the legacy
        /// import seed (if any) - so a brand-new car's "learned peak" readout shows the imported
        /// legacy value immediately rather than the generic seed until its own first observation.
        /// Never creates or mutates an entry.</summary>
        private GripLearner Find(string gameId, string carId)
        {
            if (_learners.TryGetValue(MakeKey(gameId, carId), out GripLearner learner)) return learner;
            return _learners.TryGetValue(LegacyImportKey, out GripLearner legacy) && legacy.Samples > 0 ? legacy : null;
        }

        private GripLearner GetOrCreate(string gameId, string carId)
        {
            string key = MakeKey(gameId, carId);
            if (_learners.TryGetValue(key, out GripLearner learner)) return learner;

            learner = new GripLearner(_learnCapG);
            // A brand-new (gameId, carId) starts from whatever a pre-per-car runtime file had already
            // learned (if anything was imported - see SeedLegacy), rather than the generic 1.0 seed -
            // "don't lose learned data" applied to the cold-start value a NEW car gets, not just to
            // data that already had a car key.
            if (_learners.TryGetValue(LegacyImportKey, out GripLearner legacy) && legacy.Samples > 0)
                learner.Load(legacy.LearnedPeakG, legacy.Samples);

            _learners[key] = learner;
            return learner;
        }

        /// <summary>Clears every learned key - called on a full reset (e.g. plugin-level "forget
        /// everything"), NOT on an ordinary game/car switch (each key is already isolated, so a switch
        /// needs no clearing at all - that is the entire point of this class).</summary>
        public void Reset() => _learners.Clear();

        /// <summary>Snapshots every key with at least <see cref="GripLearner.MinPersistSamples"/>
        /// qualifying observations - the legacy-import pseudo-key is included like any other (it is a
        /// genuine, valid learner state; only its key is special), so it keeps serving as the seed for
        /// the next brand-new car across restarts too, not just for the remainder of one session.
        /// <para/>
        /// A key with FEWER than <see cref="GripLearner.MinPersistSamples"/> observations is simply
        /// left OUT of the returned dictionary - since this is called every frame with the FULL
        /// current in-memory contents and <c>RuntimeStore</c> replaces its on-disk dictionary wholesale
        /// with whatever is passed in, an immature key never reaches disk at all until it matures
        /// (harmless: there was nothing of value there to lose), while an already-mature key's sample
        /// count only ever grows, so it is never at risk of being excluded once it has ever
        /// qualified - a short, later, low-quality session cannot make an already-persisted, mature
        /// profile disappear.</summary>
        public Dictionary<string, GripLearnerState> ExportAll()
        {
            var export = new Dictionary<string, GripLearnerState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, GripLearner> pair in _learners)
            {
                if (pair.Value.Samples >= GripLearner.MinPersistSamples)
                    export[pair.Key] = new GripLearnerState { PeakG = pair.Value.LearnedPeakG, Samples = pair.Value.Samples };
            }
            return export;
        }

        /// <summary>Restores every key from a previously persisted snapshot - called once at Init.
        /// A null/empty source leaves this instance exactly as freshly constructed.</summary>
        public void ImportAll(IDictionary<string, GripLearnerState> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, GripLearnerState> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                var learner = new GripLearner(_learnCapG);
                learner.Load(pair.Value.PeakG, pair.Value.Samples);
                _learners[pair.Key] = learner;
            }
        }

        /// <summary>
        /// Seeds <see cref="LegacyImportKey"/> from a pre-per-car runtime file's single global
        /// peak/sample count - called at most once, at Init, only when the new keyed runtime file did
        /// not exist yet and the old flat-shaped one did (see <c>RuntimeStore</c>'s remarks). Never
        /// overwrites an already-imported per-(game,car) entry (there cannot be one yet at this point
        /// in Init, but the check costs nothing and documents the intent). A non-finite, non-positive,
        /// or zero-sample pair is ignored - "nothing usable was stored".
        /// </summary>
        public void SeedLegacy(double peakG, int samples)
        {
            if (!ClampMath.IsFinite(peakG) || peakG <= 0.0 || samples <= 0) return;
            var learner = new GripLearner(_learnCapG);
            learner.Load(peakG, samples);
            _learners[LegacyImportKey] = learner;
        }
    }

    /// <summary>Plain, Newtonsoft-round-trippable snapshot of one <see cref="GripLearner"/>'s state -
    /// the per-key value <see cref="KeyedGripLearner.ExportAll"/>/<see cref="KeyedGripLearner.ImportAll"/>
    /// persist through <c>RuntimeStore</c>.</summary>
    public sealed class GripLearnerState
    {
        public double PeakG = GripLearner.SeedPeakG;
        public int Samples;
    }
}
