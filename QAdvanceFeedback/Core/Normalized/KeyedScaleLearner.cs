using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// PER-SOURCE INPUT CALIBRATION (docs\branch-dispatch-and-source-keyed-learning-report.md - the
    /// owner's own follow-up after source-KEYED learning shipped: keying stops one source's learned
    /// reference leaking onto another's signal, but does nothing to make the sources' own NATIVE SCALES
    /// agree - a source reading 60/80/90 at slightly/ideal/critical grip loss and one reading 5/15/30
    /// for the SAME three moments both still publish their own raw number, unrescaled, once
    /// <see cref="NormalizedWheelLockSlipEngine"/>'s own Raw FLOOR
    /// (<c>severity = Max(effectiveGripUtilization, mean(rawWheels))</c> - see that class's own remarks,
    /// defects B/D) is applied. This class rescales that floor so "75" means roughly the same thing
    /// ("at the limit") regardless of which source produced it.
    /// <para/>
    /// THE ANCHOR IS PHYSICAL, NOT STATISTICAL (the owner's own refinement, PREFERRED over this class's
    /// first design - a plain rolling percentile of each source's own distribution): within the SAME
    /// (game, car), the physics - grip limit, achieved deceleration, speed, load - is IDENTICAL no
    /// matter which source is configured; only the source's own NUMBER at that physical moment differs.
    /// A percentile envelope of a source's own history depends on driving style/session content (a
    /// gentle stint never reaches the top of the envelope, mis-learning the scale and drifting the
    /// bands) - anchoring to the SAME physically-detected event for every source is what actually makes
    /// them converge. <see cref="NormalizedWheelLockSlipEngine"/> therefore detects "are we physically
    /// at this car's own learned grip limit RIGHT NOW" using a DEDICATED, (game,car)-only
    /// <see cref="KeyedGripLearner"/> reference (shared across every source - see that class's own
    /// remarks on why the source-keyed one used for <c>gripUtilization</c> is NOT reused for this
    /// purpose) and, whenever that is true, calls <see cref="ObserveAtPhysicalLimit"/> here with THIS
    /// source's own raw reading at that exact moment - what this class actually learns.
    /// <para/>
    /// TWO TIERS, PRIMARY THEN FALLBACK (the owner's own explicit allowance - "if a percentile envelope
    /// still helps as a secondary signal where the physical anchor is unavailable... you may use it as a
    /// fallback, but say so"):
    /// <list type="number">
    /// <item><b>PRIMARY - physically-anchored.</b> The plain average of this source's own raw reading,
    /// recorded ONLY on frames independently detected as "physically at this car's own learned limit" -
    /// see <see cref="ObserveAtPhysicalLimit"/>. Ready once <see cref="MinPhysicalAnchorSamples"/>
    /// qualifying moments have been recorded.</item>
    /// <item><b>SECONDARY - percentile fallback.</b> The 99th percentile of EVERY qualifying raw reading
    /// this source produced (engaged+triggered, regardless of whether the physical limit was ever
    /// detected) - <see cref="ObserveGeneral"/>/<see cref="OnlineDistributionLearner"/>. Used ONLY while
    /// the primary tier is not yet ready (e.g. a session that never actually reaches the car's own true
    /// limit) - a coarser, session-shaped approximation, explicitly secondary.</item>
    /// </list>
    /// <para/>
    /// COLD START (neither tier ready): <see cref="Rescale"/> returns the raw value UNCHANGED (identity)
    /// - not an assumed rescale factor, a deliberate abstention. Every raw wheel value is ALREADY
    /// clamped to [0,100] before this class ever sees it, so an uncalibrated reading can never exceed
    /// what the source itself already reported - "prefer under-reporting to a wild over-scale, never
    /// let a cold start produce a full-scale cue" (this task's own requirement, mirroring
    /// <see cref="GripLearner.ColdStartCeilingRatio"/>'s precedent) is satisfied because a
    /// differently-scaled source's own SMALLER native numbers pass through smaller, not larger, until
    /// calibrated.
    /// <para/>
    /// HONEST LIMITATION (re-measured with real numbers in this class's own report): a single learned
    /// anchor can only guarantee exact agreement AT the calibrated point. If a source's own mapping from
    /// true severity to its own raw number is not linear throughout its range (the owner's own worked
    /// example is not - ShakeIt's 60/80/90/100 is not a fixed ratio of 30/60/80/100), checkpoints away
    /// from the anchor will not converge as tightly as the anchor itself does. This was measured, not
    /// hidden - see the acceptance test and report.
    /// </summary>
    public sealed class KeyedScaleLearner
    {
        /// <summary>Where the physically-anchored reading is mapped to on the canonical 0-100 scale -
        /// see <see cref="NormalizedWheelLockSlipEngine"/>'s own band description ("60-80: starting to
        /// lock-or-spin, very close but not yet") - deliberately just below the 80 boundary so genuine
        /// full lock/spin (which every source maps to 100, per the owner's own examples) still has
        /// headroom above this anchor.</summary>
        public const double CanonicalAtLimitAnchor = 75.0;

        /// <summary>Minimum physically-at-the-limit MOMENTS (not frames in general - see this class's
        /// own remarks) before the PRIMARY tier is trusted - deliberately small (these are inherently
        /// rare events within a session, unlike the general population the secondary tier draws from).
        /// Our own choice, documented, not a copy of any SimHub constant.</summary>
        public const int MinPhysicalAnchorSamples = 20;

        private readonly Dictionary<string, OnlineDistributionLearner> _physicalAnchor = new Dictionary<string, OnlineDistributionLearner>(StringComparer.Ordinal);
        private readonly Dictionary<string, OnlineDistributionLearner> _generalDistribution = new Dictionary<string, OnlineDistributionLearner>(StringComparer.Ordinal);

        // ---- COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3). This learner was, until now,
        // SESSION-SCOPED ONLY (see this class's own original remarks) - _coldCeiling/_hotDispersion below
        // add a persisted-per-key reference (see LoadCold) blended with THIS session's own hot evidence
        // by the SAME dispersion-weighted mechanism GripLearner uses (ColdWarmBlend) - "if the hot data
        // will cause more noise, prefer cold data only" applies here exactly as it does there. Dispersion
        // is tracked over the PRIMARY (physically-anchored) tier's own raw readings specifically - the
        // same readings ObserveAtPhysicalLimit already folds into _physicalAnchor - since that is this
        // class's own most trustworthy signal; a session that never reaches the primary tier (dispersion
        // count 0) is treated as maximally dispersed (see WelfordAccumulator.CoefficientOfVariation's own
        // remarks), biasing toward cold, the conservative direction, exactly as intended.
        private readonly Dictionary<string, double> _coldCeiling = new Dictionary<string, double>(StringComparer.Ordinal);
        private readonly Dictionary<string, WelfordAccumulator> _hotDispersion = new Dictionary<string, WelfordAccumulator>(StringComparer.Ordinal);

        /// <summary>PRIMARY tier - records this source's own raw reading at a moment independently
        /// detected (by the caller) as physically at this car's own learned grip limit.</summary>
        public void ObserveAtPhysicalLimit(string gameId, string carId, string sourceIdentity, double rawValue)
        {
            if (!ClampMath.IsFinite(rawValue) || rawValue <= 0.0) return;
            GetOrCreate(_physicalAnchor, gameId, carId, sourceIdentity).AddValue(rawValue);

            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            WelfordAccumulator dispersion = _hotDispersion.TryGetValue(key, out WelfordAccumulator existing) ? existing : WelfordAccumulator.Empty;
            dispersion.Observe(rawValue);
            _hotDispersion[key] = dispersion;
        }

        /// <summary>Seeds a persisted COLD ceiling for (gameId, carId, sourceIdentity) - called once, at
        /// Init, from whatever <c>RuntimeStore</c> loaded. From this point on, <see cref="PublishedCeiling"/>/
        /// <see cref="Rescale"/> blend this cold reference with however THIS session's own hot evidence
        /// (primary-tier dispersion, reset fresh here) develops - see <see cref="ColdWarmBlend"/>'s own
        /// remarks. A key that never has this called behaves EXACTLY as this class always has (this
        /// task's own explicit "cold start with no persisted value behaves as today" requirement).</summary>
        public void LoadCold(string gameId, string carId, string sourceIdentity, double coldCeiling)
        {
            if (!ClampMath.IsFinite(coldCeiling) || coldCeiling <= 0.0) return;
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            _coldCeiling[key] = coldCeiling;
            _hotDispersion[key] = WelfordAccumulator.Empty;
        }

        /// <summary>THE PUBLISHED REFERENCE <see cref="Rescale"/> actually divides by: a continuous
        /// dispersion-weighted blend of the persisted COLD ceiling and whatever <see cref="LearnedCeiling"/>
        /// (this session's own hot primary/secondary tiers) currently answers - null only when NEITHER a
        /// cold reference nor any hot evidence exists yet (true cold start, identical to
        /// <see cref="LearnedCeiling"/>'s own null in that case).</summary>
        public double? PublishedCeiling(string gameId, string carId, string sourceIdentity, out bool isPrimaryTier)
        {
            double? hot = LearnedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier);
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            if (!_coldCeiling.TryGetValue(key, out double cold)) return hot; // no persisted reference - behaves exactly as before this feature
            if (!hot.HasValue) return cold;

            WelfordAccumulator dispersion = DispersionFor(key);
            double weight = ColdWarmBlend.HotWeight(dispersion.Count, dispersion.CoefficientOfVariation);
            return ColdWarmBlend.Blend(cold, hot.Value, weight);
        }

        /// <summary>What SHOULD be written back to persistence right now for (gameId, carId,
        /// sourceIdentity) - the persisted COLD ceiling, UNCHANGED, unless this session's own primary-tier
        /// dispersion clears BOTH bars of <see cref="ColdWarmBlend.ShouldPersist"/> - see
        /// <see cref="Normalized.GripLearner.PersistedPeakG"/>'s own remarks for the identical reasoning
        /// applied there. Null when there is genuinely nothing to persist yet (no cold reference AND no
        /// hot evidence either).</summary>
        public double? PersistedCeiling(string gameId, string carId, string sourceIdentity, out bool isPrimaryTier)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            if (!_coldCeiling.TryGetValue(key, out double cold))
                return LearnedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier); // nothing to protect yet - hot itself becomes what gets persisted

            WelfordAccumulator dispersion = DispersionFor(key);
            if (ColdWarmBlend.ShouldPersist(dispersion.Count, dispersion.CoefficientOfVariation))
                return PublishedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier);

            isPrimaryTier = false;
            return cold;
        }

        private WelfordAccumulator DispersionFor(string key)
            => _hotDispersion.TryGetValue(key, out WelfordAccumulator acc) ? acc : WelfordAccumulator.Empty;

        /// <summary>Every key with something worth persisting - either an already-persisted cold
        /// reference (kept, protected per <see cref="PersistedCeiling"/>'s own remarks, unless this
        /// session earned the right to move it) or a brand-new key whose hot evidence already answers
        /// <see cref="LearnedCeiling"/> (mirroring <see cref="KeyedGripLearner.ExportAll"/>'s own "a
        /// brand-new key's hot state becomes the persisted seed" convention). Called once per frame from
        /// the composition root, same convention as <see cref="KeyedGripLearner.ExportAll"/>.</summary>
        public Dictionary<string, ScaleLearnerState> ExportAll()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string k in _physicalAnchor.Keys) keys.Add(k);
            foreach (string k in _generalDistribution.Keys) keys.Add(k);
            foreach (string k in _coldCeiling.Keys) keys.Add(k);

            var export = new Dictionary<string, ScaleLearnerState>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                double? persisted;
                if (_coldCeiling.TryGetValue(key, out double cold))
                {
                    WelfordAccumulator dispersion = DispersionFor(key);
                    persisted = ColdWarmBlend.ShouldPersist(dispersion.Count, dispersion.CoefficientOfVariation)
                        ? LearnedCeilingByKey(key, out _)
                        : cold;
                }
                else
                {
                    persisted = LearnedCeilingByKey(key, out _);
                }

                if (!persisted.HasValue) continue;

                bool isPrimaryTier = Find(_physicalAnchor, key) is OnlineDistributionLearner primaryLearner
                    && primaryLearner.Count >= MinPhysicalAnchorSamples;
                export[key] = new ScaleLearnerState { ColdCeiling = persisted.Value, ColdIsPrimaryTier = isPrimaryTier };
            }
            return export;
        }

        /// <summary>Restores every previously persisted (gameId, carId, sourceIdentity) ceiling - called
        /// once at Init, mirroring <see cref="KeyedGripLearner.ImportAll"/>.</summary>
        public void ImportAll(IDictionary<string, ScaleLearnerState> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, ScaleLearnerState> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                if (!ClampMath.IsFinite(pair.Value.ColdCeiling) || pair.Value.ColdCeiling <= 0.0) continue;
                _coldCeiling[pair.Key] = pair.Value.ColdCeiling;
                _hotDispersion[pair.Key] = WelfordAccumulator.Empty;
            }
        }

        private double? LearnedCeilingByKey(string key, out bool isPrimaryTier)
        {
            OnlineDistributionLearner primary = Find(_physicalAnchor, key);
            if (primary != null && primary.Count >= MinPhysicalAnchorSamples)
            {
                isPrimaryTier = true;
                return primary.GetAverage();
            }
            OnlineDistributionLearner secondary = Find(_generalDistribution, key);
            isPrimaryTier = false;
            return secondary?.GetPercentile(99.0);
        }

        private static OnlineDistributionLearner Find(Dictionary<string, OnlineDistributionLearner> store, string key)
            => store.TryGetValue(key, out OnlineDistributionLearner learner) ? learner : null;

        /// <summary>SECONDARY tier - records every qualifying raw reading regardless of whether the
        /// physical limit was detected this frame - the fallback envelope for a session that never
        /// reaches the primary tier's own trigger condition.</summary>
        public void ObserveGeneral(string gameId, string carId, string sourceIdentity, double rawValue)
        {
            if (!ClampMath.IsFinite(rawValue) || rawValue <= 0.0) return;
            GetOrCreate(_generalDistribution, gameId, carId, sourceIdentity).AddValue(rawValue);
        }

        /// <summary>This source's own learned near-the-limit ceiling, in the SOURCE'S OWN native units -
        /// the primary (physically-anchored) tier if ready, else the secondary (percentile) tier if
        /// ready, else null (cold start). Published as a diagnostic
        /// (<c>Diag.Lock.SourceScaleCeiling</c>/<c>Diag.Slip.SourceScaleCeiling</c>) alongside
        /// <see cref="IsPrimaryTier"/> so which mechanism actually produced the number is inspectable
        /// on a rig, per this task's own explicit request.</summary>
        public double? LearnedCeiling(string gameId, string carId, string sourceIdentity, out bool isPrimaryTier)
        {
            OnlineDistributionLearner primary = Find(_physicalAnchor, gameId, carId, sourceIdentity);
            if (primary != null && primary.Count >= MinPhysicalAnchorSamples)
            {
                isPrimaryTier = true;
                return primary.GetAverage();
            }

            OnlineDistributionLearner secondary = Find(_generalDistribution, gameId, carId, sourceIdentity);
            double? secondaryCeiling = secondary?.GetPercentile(99.0);
            isPrimaryTier = false;
            return secondaryCeiling;
        }

        /// <summary>
        /// Maps <paramref name="rawValue"/> (this source's own native scale) onto the canonical 0-100
        /// scale via this (game,car,source)'s own learned ceiling (primary tier preferred, secondary as
        /// fallback) - see this class's own remarks for the full mechanism and honest limitations.
        /// </summary>
        public double Rescale(string gameId, string carId, string sourceIdentity, double rawValue)
        {
            // COLD/WARM (item 3): PublishedCeiling, not the raw hot LearnedCeiling - identical to it for
            // every (game,car,source) with no persisted cold reference (see PublishedCeiling's own
            // remarks), so this is unchanged for every caller/test that predates LoadCold.
            double? ceiling = PublishedCeiling(gameId, carId, sourceIdentity, out _);
            if (!ceiling.HasValue || ceiling.Value <= 1e-6) return rawValue; // cold start - identity, see remarks

            return ClampMath.To0100(rawValue * (CanonicalAtLimitAnchor / ceiling.Value));
        }

        private static OnlineDistributionLearner Find(Dictionary<string, OnlineDistributionLearner> store, string gameId, string carId, string sourceIdentity)
            => store.TryGetValue(KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity), out OnlineDistributionLearner learner) ? learner : null;

        private static OnlineDistributionLearner GetOrCreate(Dictionary<string, OnlineDistributionLearner> store, string gameId, string carId, string sourceIdentity)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            if (!store.TryGetValue(key, out OnlineDistributionLearner learner))
            {
                learner = new OnlineDistributionLearner();
                store[key] = learner;
            }
            return learner;
        }

        /// <summary>Clears every learned key in both tiers, AND every persisted cold reference - mirrors
        /// <see cref="KeyedGripLearner.Reset"/> (a full "forget everything", not an ordinary game/car/
        /// source switch, which needs no clearing at all).</summary>
        public void Reset()
        {
            _physicalAnchor.Clear();
            _generalDistribution.Clear();
            _coldCeiling.Clear();
            _hotDispersion.Clear();
        }
    }

    /// <summary>Plain, Newtonsoft-round-trippable snapshot of one (gameId, carId, sourceIdentity)'s
    /// persisted scale-calibration state - see <see cref="KeyedScaleLearner.ExportAll"/>/
    /// <see cref="KeyedScaleLearner.ImportAll"/>, mirroring <see cref="GripLearnerState"/>'s own
    /// shape/role for <see cref="KeyedGripLearner"/>.</summary>
    public sealed class ScaleLearnerState
    {
        public double ColdCeiling;
        public bool ColdIsPrimaryTier;
    }
}
