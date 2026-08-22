using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// One <see cref="GripLearner"/> per (gameId, carId, sourceIdentity), so the Lock/Slip channels'
    /// learned peak-g reference switches automatically when the game, the car, OR the configured
    /// SOURCE changes - see docs\branch-dispatch-and-source-keyed-learning-report.md for the
    /// source-keying extension (was (gameId, carId) only - see this class's own git history / that
    /// report's "Part 2" for the original (game,car)-only gap docs\layer45-report.md flagged: "The
    /// learned grip reference is a single global value per channel, not per-car/per-game ... switching
    /// cars mid-session shares one learned peak across both").
    /// <para/>
    /// WHY SOURCE TOO: a per-car learned reference is calibrated to the SCALE AND SHAPE of whatever
    /// signal fed it. Switching the configured Wheel Lock/Slip SOURCE (ShakeIt export, this plugin's
    /// own Raw, a third-party property, a custom expression) changes that signal - "60" from one
    /// provider is not "60" from another - so a learner keyed only by (game,car) would silently carry
    /// one source's calibration over to a completely different one the moment the driver switches.
    /// Adding <paramref name="sourceIdentity"/>/<see cref="SourceIdentity"/> as a third key dimension
    /// gives each source its own fully isolated learning session: switching sources loads that
    /// source's own previously-learned profile (or starts fresh if this is genuinely new), and
    /// switching back restores exactly what was learned before, per (game,car).
    /// <para/>
    /// KEY SHAPE, PER-CHANNEL NOT PER-WHEEL: this class is already one-instance-per-CHANNEL (Lock has
    /// its own <see cref="KeyedGripLearner"/>, Slip has its own - see
    /// <see cref="NormalizedWheelLockSlipEngine"/>), and the learner itself only ever observes ONE
    /// car-level G-force magnitude per frame (see <see cref="AchievedMotion"/>) - there is no per-wheel
    /// learned quantity to split further. <see cref="SourceIdentity"/> therefore combines all four of a
    /// channel's own per-wheel source configurations into ONE composite identity string for that
    /// channel (the four wheels COULD in principle point at four different providers - see that
    /// class's own remarks for why a single composite, rather than trying to track four independent
    /// per-wheel learners, is the right level for a channel that only ever learns one scalar).
    /// <para/>
    /// MIGRATION (see <see cref="GripLearnerKeyMigration"/>): a runtime file persisted before this
    /// change has keys with no source segment at all - <see cref="GripLearnerKeyMigration.MigrateLegacyKeys"/>
    /// upgrades every such key to this class's OWN <see cref="LegacySourcelessSourceIdentity"/> sentinel
    /// before <see cref="ImportAll"/> ever sees it, and <see cref="GetOrCreate"/> below seeds every
    /// BRAND NEW source (for a (game,car) pair that already has a sourceless-migrated profile) from
    /// that sentinel exactly once per new key - mirroring the EXACT SAME non-destructive adoption
    /// pattern this class already uses for the even-older flat <see cref="LegacyImportKey"/>.
    /// <para/>
    /// Key format and separator are deliberately identical to <see cref="GForce.GForceMaxLearner.MakeKey"/>
    /// (same reasoning: a separator that cannot appear in a SimHub GameName/CarId), so the two learner
    /// families read as one consistent convention rather than two subtly different ones.
    /// </summary>
    public sealed class KeyedGripLearner
    {
        private const string KeySeparator = "|#|";

        /// <summary>The key a pre-per-car runtime file's single global peak/sample count is imported
        /// under (see <see cref="SeedLegacy"/>) - deliberately not a valid (gameId, carId, sourceIdentity)
        /// tuple (all three segments would have to independently equal this exact literal), so it can
        /// never collide with a real game/car/source key.</summary>
        public const string LegacyImportKey = "__legacy_import__";

        /// <summary>The source-identity sentinel a pre-source-keying persisted (gameId, carId) entry is
        /// migrated to (see <see cref="GripLearnerKeyMigration"/>) - distinct from the empty string
        /// (which is a perfectly legitimate, real "no source configured differently" identity a caller
        /// might genuinely pass), so a migrated legacy profile is never confused with a driver's own,
        /// deliberately blank configuration.</summary>
        public const string LegacySourcelessSourceIdentity = "__source_unknown__";

        /// <summary>The surface-bucket sentinel a pre-surface-keying persisted (gameId, carId,
        /// sourceIdentity) entry is migrated to (see <see cref="GripLearnerKeyMigration"/>) - distinct
        /// from "Sealed"/"Loose" (the two REAL buckets <see cref="NormalizedWheelLockSlipEngine"/> now
        /// blends between), so a driver upgrading past surface-keying still has their prior combined
        /// data adopted as a cold-start seed for whichever real surface bucket is encountered first -
        /// same non-destructive pattern as <see cref="LegacySourcelessSourceIdentity"/>.</summary>
        public const string LegacyPreSurfaceSplitBucket = "__surface_unknown__";

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

        /// <param name="sourceIdentity">See <see cref="SourceIdentity"/>. Defaults to empty - every
        /// pre-existing caller/test that predates source-keying keeps compiling and behaving exactly as
        /// before (a stable, real key of its own, just as isolated per (game,car) as before - only a
        /// caller that ALSO varies this parameter observes the new per-source isolation).</param>
        /// <param name="surfaceBucket">SURFACE-KEYED LEARNING - "Sealed" or "Loose" (see
        /// <see cref="SurfaceLooseFraction"/>/<see cref="NormalizedWheelLockSlipEngine"/>'s own
        /// remarks). Defaults to empty for the SAME backward-compatibility reason as
        /// <paramref name="sourceIdentity"/> - every pre-existing caller/test keeps compiling and
        /// behaving exactly as before.</param>
        public static string MakeKey(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => (gameId ?? string.Empty) + KeySeparator + (carId ?? string.Empty) + KeySeparator
             + (sourceIdentity ?? string.Empty) + KeySeparator + (surfaceBucket ?? string.Empty);

        /// <summary>The learned peak for this (gameId, carId, sourceIdentity, surfaceBucket), or
        /// <see cref="GripLearner.SeedPeakG"/> if nothing has been observed for it yet - never creates
        /// an entry (a read must not have a side effect on what gets persisted).</summary>
        public double LearnedPeakG(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.LearnedPeakG ?? GripLearner.SeedPeakG;

        /// <summary>COLD/WARM (item 3): the actual BLENDED reference <see cref="Ratio"/> divides by for
        /// this key - see <see cref="GripLearner.PublishedPeakG"/>'s own remarks. Identical to
        /// <see cref="LearnedPeakG"/> for any key with no persisted cold reference.</summary>
        public double PublishedPeakG(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.PublishedPeakG ?? GripLearner.SeedPeakG;

        public int Samples(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.Samples ?? 0;

        public double Confidence(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.Confidence ?? 0.0;

        /// <summary>Read-only diagnostic passthrough to <see cref="GripLearner.PublishedGMech"/> for this
        /// key - <see cref="GripLearner.SeedPeakG"/> (the flat seed) for a key never observed yet. Added
        /// for the v1.0.6.9 rework's own Goal 3 reporting (docs\v1068-rework-report.md - the wet/dry and
        /// cross-car speed-G-mapping-vs-source-anchor stability comparison); never creates an entry.</summary>
        public double PublishedGMech(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.PublishedGMech ?? GripLearner.SeedPeakG;

        /// <summary>The Slip/Lock-shared equivalent for <see cref="GripLearner.PublishedK"/> - 0.0 (no
        /// aero term learned) for a key never observed yet.</summary>
        public double PublishedK(string gameId, string carId, string sourceIdentity = "", string surfaceBucket = "")
            => Find(gameId, carId, sourceIdentity, surfaceBucket)?.PublishedK ?? 0.0;

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY (docs\speed-aware-reference-fix-report.md) - passthrough to
        /// <see cref="GripLearner.SnapshotSpeedBucket"/> for this key. Never creates an entry - a key never
        /// observed yet reports every bucket as having no live estimate.</summary>
        public void SnapshotSpeedBucket(string gameId, string carId, string sourceIdentity, string surfaceBucket, int index,
            out bool hasLiveEstimate, out double peakG, out double meanSpeedKmh, out int liveSamples)
        {
            GripLearner learner = Find(gameId, carId, sourceIdentity, surfaceBucket);
            if (learner == null)
            {
                hasLiveEstimate = false; peakG = 0.0; meanSpeedKmh = 0.0; liveSamples = 0;
                return;
            }
            learner.SnapshotSpeedBucket(index, out hasLiveEstimate, out peakG, out meanSpeedKmh, out liveSamples);
        }

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY - the fixed number of speed buckets every
        /// <see cref="GripLearner"/> uses (not per-key).</summary>
        public static int SpeedBucketCount => GripLearner.SpeedBucketCount;

        /// <summary>DIAGNOSTIC/MEASUREMENT ONLY - the lower edge (km/h) of speed bucket <paramref name="index"/>.</summary>
        public static double SpeedBucketEdgeKmh(int index) => GripLearner.SpeedBucketEdgeKmh(index);

        /// <summary><paramref name="magnitudeG"/> as a fraction of this (gameId, carId, sourceIdentity,
        /// surfaceBucket)'s own learned peak - see <see cref="GripLearner.Ratio"/>. Creates a fresh (or
        /// legacy-seeded) learner for a key seen for the very first time, mirroring
        /// <see cref="Observe"/>.</summary>
        /// <param name="speedKmh">SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - see
        /// <see cref="GripLearner.Ratio"/>'s own remarks. Defaults to <c>null</c> so every pre-existing
        /// caller/test keeps its exact prior behaviour.</param>
        /// <param name="useStabilityGatedCeiling">See <see cref="GripLearner.Ratio"/>'s own remarks.</param>
        public double Ratio(string gameId, string carId, double magnitudeG, string sourceIdentity = "", string surfaceBucket = "", bool applyColdStartCeiling = true, double? speedKmh = null, bool useStabilityGatedCeiling = true)
            => GetOrCreate(gameId, carId, sourceIdentity, surfaceBucket).Ratio(magnitudeG, applyColdStartCeiling, speedKmh, useStabilityGatedCeiling);

        /// <summary>Folds one qualifying observation into this (gameId, carId, sourceIdentity,
        /// surfaceBucket)'s own learner - see <see cref="GripLearner.Observe"/>.</summary>
        /// <param name="speedKmh">See <see cref="Ratio"/>'s own remarks.</param>
        /// <param name="atLimitWeight">See <see cref="GripLearner.Observe"/>'s own remarks. Defaults to
        /// 1.0 (unconditional speed-bucket admission) so every pre-existing caller/test is unaffected.</param>
        public void Observe(string gameId, string carId, double magnitudeG, string sourceIdentity = "", string surfaceBucket = "", double? speedKmh = null, double atLimitWeight = 1.0)
            => GetOrCreate(gameId, carId, sourceIdentity, surfaceBucket).Observe(magnitudeG, speedKmh, atLimitWeight);

        /// <summary>Read-only lookup: the key's own learner if it has been created yet (via
        /// <see cref="Observe"/>/<see cref="Ratio"/>/<see cref="ImportAll"/>), otherwise this SAME
        /// (game,car,source)'s migrated-pre-surface-split profile (if any), otherwise this SAME
        /// (game,car)'s migrated-legacy-sourceless profile (if any), otherwise the even-older flat
        /// legacy import seed (if any) - so a brand-new car/source/surface's "learned peak" readout
        /// shows the best available previously-learned value immediately rather than the generic seed
        /// until its own first observation. Never creates or mutates an entry.</summary>
        private GripLearner Find(string gameId, string carId, string sourceIdentity, string surfaceBucket)
        {
            if (_learners.TryGetValue(MakeKey(gameId, carId, sourceIdentity, surfaceBucket), out GripLearner learner)) return learner;

            if (_learners.TryGetValue(MakeKey(gameId, carId, sourceIdentity, LegacyPreSurfaceSplitBucket), out GripLearner preSurfaceSplit)
                && preSurfaceSplit.Samples > 0)
                return preSurfaceSplit;

            if (_learners.TryGetValue(MakeKey(gameId, carId, LegacySourcelessSourceIdentity, LegacyPreSurfaceSplitBucket), out GripLearner legacySourceless)
                && legacySourceless.Samples > 0)
                return legacySourceless;

            return _learners.TryGetValue(LegacyImportKey, out GripLearner legacy) && legacy.Samples > 0 ? legacy : null;
        }

        private GripLearner GetOrCreate(string gameId, string carId, string sourceIdentity, string surfaceBucket)
        {
            string key = MakeKey(gameId, carId, sourceIdentity, surfaceBucket);
            if (_learners.TryGetValue(key, out GripLearner learner)) return learner;

            learner = new GripLearner(_learnCapG);

            // A brand-new (gameId, carId, sourceIdentity, surfaceBucket) starts from whatever was
            // already learned for the SAME (game,car,source) under a migrated, pre-surface-split
            // profile (if any), else the SAME (game,car) under a migrated, pre-source-keying profile
            // (if any), else whatever a pre-per-car runtime file had already learned globally (if
            // anything was imported - see SeedLegacy) - "don't lose learned data" applied to the
            // cold-start value a NEW source/surface combination gets, not just to data that already had
            // a car key.
            if (_learners.TryGetValue(MakeKey(gameId, carId, sourceIdentity, LegacyPreSurfaceSplitBucket), out GripLearner preSurfaceSplit)
                && preSurfaceSplit.Samples > 0)
            {
                learner.Load(preSurfaceSplit.LearnedPeakG, preSurfaceSplit.Samples);
            }
            else if (_learners.TryGetValue(MakeKey(gameId, carId, LegacySourcelessSourceIdentity, LegacyPreSurfaceSplitBucket), out GripLearner legacySourceless)
                && legacySourceless.Samples > 0)
            {
                learner.Load(legacySourceless.LearnedPeakG, legacySourceless.Samples);
            }
            else if (_learners.TryGetValue(LegacyImportKey, out GripLearner legacy) && legacy.Samples > 0)
            {
                learner.Load(legacy.LearnedPeakG, legacy.Samples);
            }

            _learners[key] = learner;
            return learner;
        }

        /// <summary>Clears every learned key - called on a full reset (e.g. plugin-level "forget
        /// everything"), NOT on an ordinary game/car/source switch (each key is already isolated, so a
        /// switch needs no clearing at all - that is the entire point of this class).</summary>
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
                    // COLD/WARM (item 3): PersistedPeakG, not the raw hot LearnedPeakG - protects an
                    // already-persisted, trustworthy COLD reference from a noisy session's own hot state
                    // (see GripLearner.PersistedPeakG's own remarks). Identical to LearnedPeakG for every
                    // key that was never Load-ed from a prior session (a brand-new key becoming persisted
                    // for the first time has no cold reference to protect yet). PersistedGMech/PersistedK
                    // (docs\speed-aware-grip-report.md) mirror PersistedPeakG's own "HOT must not corrupt
                    // COLD" gate exactly - same reasoning, same key.
                    //
                    // RaiseCandidateG/Hits/LowerCandidateG/Hits (docs\adaptive-peak-learner-report.md) -
                    // the evidence-weighted estimator's own in-progress corroboration, exported AS-IS
                    // (not cold/warm blended - see GripLearner.RaiseCandidateG's own remarks on why this
                    // discrete bookkeeping is not a value meaningful to interpolate) so a returning car
                    // resumes warm rather than relearning its confidence from scratch.
                    export[pair.Key] = new GripLearnerState
                    {
                        PeakG = pair.Value.PersistedPeakG,
                        Samples = pair.Value.Samples,
                        GMech = pair.Value.PersistedGMech,
                        K = pair.Value.PersistedK,
                        RaiseCandidateG = pair.Value.RaiseCandidateG,
                        RaiseCandidateHits = pair.Value.RaiseCandidateHits,
                        LowerCandidateG = pair.Value.LowerCandidateG,
                        LowerCandidateHits = pair.Value.LowerCandidateHits,
                        QuietStreak = pair.Value.QuietStreak
                    };
            }
            return export;
        }

        /// <summary>Restores every key from a previously persisted snapshot - called once at Init.
        /// A null/empty source leaves this instance exactly as freshly constructed. Callers should pass
        /// this through <see cref="GripLearnerKeyMigration.MigrateLegacyKeys"/> first so a
        /// pre-source-keying (gameId,carId)-only key is upgraded to a full (gameId,carId,sourceIdentity)
        /// key before it ever reaches this dictionary - see that class's own remarks.</summary>
        public void ImportAll(IDictionary<string, GripLearnerState> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, GripLearnerState> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                var learner = new GripLearner(_learnCapG);
                learner.Load(pair.Value.PeakG, pair.Value.Samples, pair.Value.GMech, pair.Value.K,
                    pair.Value.RaiseCandidateG, pair.Value.RaiseCandidateHits,
                    pair.Value.LowerCandidateG, pair.Value.LowerCandidateHits,
                    pair.Value.QuietStreak);
                _learners[pair.Key] = learner;
            }
        }

        /// <summary>
        /// Seeds <see cref="LegacyImportKey"/> from a pre-per-car runtime file's single global
        /// peak/sample count - called at most once, at Init, only when the new keyed runtime file did
        /// not exist yet and the old flat-shaped one did (see <c>RuntimeStore</c>'s remarks). Never
        /// overwrites an already-imported per-(game,car,source) entry (there cannot be one yet at this
        /// point in Init, but the check costs nothing and documents the intent). A non-finite, non-positive,
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

        /// <summary>SPEED-DEPENDENT GRIP MODEL (docs\speed-aware-grip-report.md) - the persisted
        /// mechanical (v=0) grip term, <see cref="GripLearner.PersistedGMech"/>. Defaults to 0.0 -
        /// a document written before this feature existed simply lacks this key in its JSON;
        /// Newtonsoft's construct-then-overwrite convention leaves it at this default, and
        /// <see cref="GripLearner.Load(double,int,double,double)"/> treats a non-positive value as
        /// "fall back to the flat <see cref="PeakG"/>" - the exact bit-identical behaviour this
        /// feature guarantees for every pre-existing persisted profile.</summary>
        public double GMech;

        /// <summary>The persisted aero/drag term, <see cref="GripLearner.PersistedK"/> - see
        /// <see cref="GMech"/>'s own remarks. Defaults to 0.0; any invalid or negative value loaded
        /// falls back to 0.0 (see <see cref="GripLearner.Load(double,int,double,double)"/>).</summary>
        public double K;

        /// <summary>EVIDENCE-WEIGHTED ADAPTIVE PEAK ESTIMATOR (docs\adaptive-peak-learner-report.md) -
        /// the flat scalar's own in-progress RAISE corroboration (<see cref="GripLearner.RaiseCandidateG"/>/
        /// <see cref="GripLearner.RaiseCandidateHits"/>), persisted so a returning car resumes warm
        /// rather than relearning its confidence from scratch. Defaults to 0.0/0 - a document written
        /// before this feature existed simply lacks these keys; Newtonsoft's construct-then-overwrite
        /// convention leaves them at these defaults, which <see cref="GripLearner.Load(double,int,double,double,double,int,double,int)"/>
        /// already treats as "no corroboration in progress yet" - bit-identical to a freshly-constructed
        /// learner's own starting state.</summary>
        public double RaiseCandidateG;
        public int RaiseCandidateHits;

        /// <summary>The flat scalar's own in-progress LOWER corroboration - see
        /// <see cref="RaiseCandidateG"/>'s own remarks.</summary>
        public double LowerCandidateG;
        public int LowerCandidateHits;

        /// <summary>STABILITY-GATED MATURITY (docs\stability-confidence-fix-report.md) - the flat
        /// scalar's own in-progress "settling streak" (<see cref="GripLearner.QuietStreak"/>), persisted
        /// so a RETURNING car's already-genuinely-stable reference is not treated as freshly unstable on
        /// every restart - see <see cref="GripLearner.Load(double,int,double,double,double,int,double,int,double)"/>'s
        /// own remarks. Defaults to 0.0 - a document written before this feature existed simply lacks
        /// this key; Newtonsoft's construct-then-overwrite convention leaves it at this default, which
        /// that <c>Load</c> overload already treats as "no settling evidence yet this session" - the
        /// exact starting state a freshly-constructed learner already has.</summary>
        public double QuietStreak;
    }
}
