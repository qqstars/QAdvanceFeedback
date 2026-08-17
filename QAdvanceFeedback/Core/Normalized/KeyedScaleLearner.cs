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
    /// <para/>
    /// COLD-START CONTINUITY AND CROSS-CAR SEEDING (docs\cold-start-and-timing-fix-report.md - the F1 25
    /// car-switch regression: with <c>NormalizedWheelLockSlipEngine</c>'s severity now
    /// <c>calibratedMean</c> alone, THIS class's own calibration carries the entire live signal, so a
    /// switch to a brand-new car - a brand-new (game,car,source) key - matters far more than it used to).
    /// Two changes, both scoped to this class:
    /// <list type="number">
    /// <item><b>No more hard step at <see cref="MinPhysicalAnchorSamples"/>.</b> The primary tier used to
    /// be ALL (once <see cref="MinPhysicalAnchorSamples"/> qualifying moments existed) or NOTHING (an
    /// identity/cold-reference floor below it) - an instant jump the moment sample #20 arrived. It now
    /// blends its own partial average toward that same trust continuously, using the SAME
    /// dispersion-weighted <see cref="ColdWarmBlend"/> mechanism this class already uses for a persisted
    /// COLD reference (a tight cluster of readings earns trust in a handful of samples; a scattered one
    /// stays near the floor regardless of count) - "blending in as evidence accumulates, continuously, no
    /// step", not a retuned threshold.</item>
    /// <item><b>Cross-car seeding - GATED, NEVER-AMPLIFYING (revised, docs\regression-fix-report.md,
    /// Regression 3)</b>. A brand-new (game,car,source) key with ZERO local evidence of its own falls
    /// back to bare identity - no rescale at all - even when this EXACT source has already been
    /// calibrated for a DIFFERENT car in the SAME game. This was tightened after an owner-reported
    /// regression: the FIRST design let a brand-new car's very FIRST query borrow the OTHER car's seed
    /// at FULL STRENGTH, uncapped - which could (and, on a lower-native-scale seed, WOULD) amplify a
    /// perfectly ordinary reading into a false, full-strength cue on a driver's first corner ("a missed
    /// cue is far better than a full-strength false alarm", this project's own established principle).
    /// The seed now only ever nudges the ramp's OWN starting point (see
    /// <see cref="LearnedCeilingForKey"/>'s own primary-tier branch) once THIS key has recorded at least
    /// one physical-limit observation of its own, blended in by the SAME weight that governs how much
    /// this key's own evidence is trusted overall - zero at zero evidence (identity, exactly), growing
    /// only as this car's own evidence grows. It is also CAPPED at
    /// <see cref="CanonicalAtLimitAnchor"/> so it can only ever REDUCE Rescale's factor, never amplify -
    /// only this car's OWN fully-earned average (once trusted) may push the ceiling below the anchor,
    /// which is a legitimate, evidence-based amplification the calibration exists to provide, never a
    /// borrowed guess. Once any (game,*,source) key's primary tier earns meaningful trust, its ceiling is
    /// still remembered per (game,source) - see <see cref="_crossCarSeed"/> - for exactly this narrow,
    /// gated use. A source never seen before in this game at all (no seed either) still falls back to
    /// plain identity.</item>
    /// </list>
    /// </summary>
    public sealed class KeyedScaleLearner
    {
        /// <summary>Where the physically-anchored reading is mapped to on the canonical 0-100 scale -
        /// see <see cref="NormalizedWheelLockSlipEngine"/>'s own band description ("60-80: starting to
        /// lock-or-spin, very close but not yet") - deliberately just below the 80 boundary so genuine
        /// full lock/spin (which every source maps to 100, per the owner's own examples) still has
        /// headroom above this anchor.</summary>
        public const double CanonicalAtLimitAnchor = 75.0;

        /// <summary>
        /// A discrete "is this worth labelling isPrimaryTier for persistence" cutoff - see
        /// <see cref="ExportAll"/>'s own use, the ONE place this still acts as a plain threshold (a
        /// label on what gets persisted, not a live-output gate). Deliberately small (these are
        /// inherently rare events within a session, unlike the general population the secondary tier
        /// draws from). Our own choice, documented, not a copy of any SimHub constant. NOT used by the
        /// live ramp's own confidence curve - see <see cref="CalibrationConfidenceScaleSamples"/> for
        /// that.
        /// </summary>
        public const int MinPhysicalAnchorSamples = 20;

        /// <summary>
        /// THE SOFT SCALE REFERENCE for the primary tier's own live, concave confidence curve
        /// (<see cref="ColdWarmBlend.ConcaveHotWeight"/>) - docs\regression-fix-report.md, the owner's
        /// own concrete blend specification and worked example ("10 samples (10/200=0.05)... 150 samples
        /// (150/200=0.75)... &gt;=200 samples -&gt; weight 1.0"). Matches their own example literally.
        /// NOT A REQUIREMENT (the owner's own explicit "no absolute-count gate" principle): the primary
        /// tier already contributes from the very FIRST physically-at-the-limit moment, with low but
        /// non-zero weight, long before this count is reached, and a title whose sessions never approach
        /// it still earns a real, if modest, weight from whatever evidence it does accumulate - it simply
        /// never needs to "unlock" anything. Deliberately a SEPARATE constant from
        /// <see cref="MinPhysicalAnchorSamples"/> (20) even though they are both about "how much evidence
        /// is enough" for the SAME tier - <see cref="MinPhysicalAnchorSamples"/> answers a discrete,
        /// persistence-labelling question about a PLAIN COUNT of taught observations, while this answers
        /// a continuous, curve-shaping question the owner's own example specifically anchors at 200 - conflating
        /// the two would make an unrelated change to one silently retune the other.
        /// </summary>
        public const int CalibrationConfidenceScaleSamples = 200;

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

        /// <summary>CROSS-CAR SEEDING (GATED, NEVER-AMPLIFYING - see this class's own remarks) - the best
        /// already-learned ceiling for (gameId, sourceIdentity), IGNORING carId, refreshed every time ANY
        /// car's own primary tier earns meaningful trust (see <see cref="ObserveAtPhysicalLimit"/>). A
        /// car with the SAME source, once IT ITSELF has recorded at least one physical-limit observation,
        /// nudges its own ramp's starting point toward a CAPPED (never-amplifying) version of this seed -
        /// never applied at zero local evidence, never uncapped - see
        /// <see cref="LearnedCeilingForKey"/>'s own primary-tier branch.</summary>
        private readonly Dictionary<string, ScaleLearnerState> _crossCarSeed = new Dictionary<string, ScaleLearnerState>(StringComparer.Ordinal);

        /// <summary>Sample-count-and-dispersion weight (see <see cref="ColdWarmBlend.HotWeight"/>) at or
        /// above which a key's own primary-tier evidence is trusted enough to become the (game,source)
        /// cross-car seed other cars can start from - deliberately a real, but not maximal, bar (0.5): a
        /// seed only needs to be a BETTER starting point than bare identity for a brand-new car, not a
        /// fully-mature reference in its own right (the receiving car's own continuous blend still keeps
        /// converging toward ITS OWN truth as its own evidence accumulates).</summary>
        private const double CrossCarSeedTrustThreshold = 0.5;

        private static string CrossCarKey(string gameId, string sourceIdentity)
            => (gameId ?? string.Empty) + "|#|" + (sourceIdentity ?? string.Empty);

        /// <summary>PRIMARY tier - records this source's own raw reading at a moment independently
        /// detected (by the caller) as physically at this car's own learned grip limit.</summary>
        /// <param name="observationWeight">
        /// CONTINUOUS CONFIDENCE (docs\regression-fix-report.md - the owner's own follow-up after the
        /// 200-&gt;60 sample-bar change): how much THIS specific observation should count toward the
        /// primary tier's own decaying weighted average (see
        /// <see cref="OnlineDistributionLearner.AddValue(double,double)"/>) - defaults to 1.0 (full
        /// trust) so every pre-existing direct caller/test of THIS method (which always meant "a fully
        /// trusted observation") keeps compiling and behaving exactly as before. The engine's own live
        /// call site instead passes a CONTINUOUS, dispersion-weighted trust level
        /// (<see cref="NormalizedWheelLockSlipEngine"/>'s shared physical-limit detector's own
        /// <c>GripLearner.HotEvidenceWeight</c>) - 0 at zero evidence for that detector, growing
        /// continuously, never gated by any absolute sample count. Distinct from this method's own local
        /// <c>weight</c> below (<see cref="ColdWarmBlend.HotWeight"/> of THIS key's own accumulated
        /// evidence) - two different, complementary continuous weightings, not the same number.</param>
        public void ObserveAtPhysicalLimit(string gameId, string carId, string sourceIdentity, double rawValue, double observationWeight = 1.0)
        {
            if (!ClampMath.IsFinite(rawValue) || rawValue <= 0.0) return;
            if (!ClampMath.IsFinite(observationWeight) || observationWeight <= 0.0) return;
            OnlineDistributionLearner primary = GetOrCreate(_physicalAnchor, gameId, carId, sourceIdentity);
            primary.AddValue(rawValue, observationWeight);

            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            WelfordAccumulator dispersion = _hotDispersion.TryGetValue(key, out WelfordAccumulator existing) ? existing : WelfordAccumulator.Empty;
            dispersion.Observe(rawValue);
            _hotDispersion[key] = dispersion;

            // CROSS-CAR SEEDING (this task) - see this class's own remarks and _crossCarSeed's.
            double weight = ColdWarmBlend.HotWeight(primary.Count, dispersion.CoefficientOfVariation);
            if (weight >= CrossCarSeedTrustThreshold && primary.GetAverage() is double average)
            {
                _crossCarSeed[CrossCarKey(gameId, sourceIdentity)] =
                    new ScaleLearnerState { ColdCeiling = average, ColdIsPrimaryTier = true };
            }
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
            ParseKey(key, out string gameId, out string sourceIdentity);
            return LearnedCeilingForKey(gameId, sourceIdentity, key, out isPrimaryTier);
        }

        /// <summary>Splits a composite key (see <see cref="KeyedGripLearner.MakeKey"/>) back into its
        /// gameId/sourceIdentity segments - reliable because this class only ever builds keys itself via
        /// that same method, never accepts one from outside. Used only to recover the (gameId,
        /// sourceIdentity) pair <see cref="ExportAll"/>'s own by-key loop needs for cross-car lookups
        /// (every other caller already has these split, via <see cref="LearnedCeiling"/>'s own
        /// parameters).</summary>
        private static void ParseKey(string key, out string gameId, out string sourceIdentity)
        {
            string[] parts = (key ?? string.Empty).Split(new[] { "|#|" }, StringSplitOptions.None);
            gameId = parts.Length > 0 ? parts[0] : string.Empty;
            sourceIdentity = parts.Length > 2 ? parts[2] : string.Empty;
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
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            return LearnedCeilingForKey(gameId, sourceIdentity, key, out isPrimaryTier);
        }

        /// <summary>The actual mechanism behind both <see cref="LearnedCeiling"/> (the public,
        /// gameId/carId/sourceIdentity-shaped entry point) and <see cref="LearnedCeilingByKey"/>
        /// (<see cref="ExportAll"/>'s own by-composite-key loop) - see this class's own remarks
        /// ("COLD-START CONTINUITY AND CROSS-CAR SEEDING") for the full derivation.</summary>
        private double? LearnedCeilingForKey(string gameId, string sourceIdentity, string key, out bool isPrimaryTier)
        {
            OnlineDistributionLearner primary = Find(_physicalAnchor, key);
            if (primary != null && primary.Count > 0)
            {
                isPrimaryTier = true;
                // NO MORE HARD STEP at MinPhysicalAnchorSamples (this task) - the SAME dispersion-weighted
                // blend PublishedCeiling already uses for a persisted cold reference, applied here to
                // primary's OWN partial evidence: a tight cluster earns trust within a handful of samples,
                // a scattered one stays near bare identity regardless of count - continuous, no step.
                WelfordAccumulator dispersion = DispersionFor(key);
                // CONCAVE, DISPERSION-WEIGHTED, NO HARD GATE (docs\regression-fix-report.md - the
                // owner's own concrete blend specification): front-loaded so the first couple of
                // physically-at-limit moments already move this ramp meaningfully, using
                // CalibrationConfidenceScaleSamples purely as a SOFT SCALE reference for the curve's own
                // shape - NOT a requirement (this ramp already contributes from the very first sample,
                // weight>0 at primary.Count==2 once dispersion is defined - see WelfordAccumulator's own
                // remarks).
                double weight = ColdWarmBlend.ConcaveHotWeight(primary.Count, dispersion.CoefficientOfVariation, CalibrationConfidenceScaleSamples);

                // CROSS-CAR SEED, GATED TO NEVER AMPLIFY A COLD READING (docs\regression-fix-report.md,
                // Regression 3 - the owner's own explicit requirement after the hard-shake-on-first-brake
                // report): the ramp's own "cold" starting point is bare IDENTITY
                // (CanonicalAtLimitAnchor - Rescale's own factor is 1.0 there, see its remarks), blended
                // toward a CAPPED cross-car seed by THIS SAME weight - i.e. the seed contributes NOTHING
                // at weight 0 (zero local evidence for THIS EXACT key - plain identity, exactly the
                // owner's own "the cold state before any local evidence must be identity, not a borrowed
                // scale" rule) and only nudges the ramp's own starting point as THIS car's own local
                // evidence begins accumulating - continuous, not a second step (composing two continuous
                // blends stays continuous). Capped at CanonicalAtLimitAnchor so the BORROWED portion can
                // only ever REDUCE Rescale's factor (anchor/ceiling &lt;= 1 whenever the anchor is >= 75),
                // never amplify - only THIS car's own genuinely-earned <c>average</c> below (blended in by
                // the SAME weight) may push the ceiling below 75 (a legitimate, evidence-based
                // amplification exactly the F1 fix/Regression 2 calibration needs, never a borrowed
                // guess). Deliberately never blended against a cross-car seed this SAME key is
                // simultaneously WRITING (see ObserveAtPhysicalLimit) - self-referential contamination,
                // not a genuine cross-car borrow (mirrors the reasoning this class always applied here).
                //
                // MUTATION EVIDENCE (docs\regression-fix-report.md): reverting this to the OLD
                // "_crossCarSeed applies at full strength the instant primary.Count == 0" behaviour
                // reproduces a >100%-of-source Rescale factor on a brand-new car's very first qualifying
                // frame whenever a lower-native-scale seed exists - exactly the "hard shake on the first
                // 1-2 braking events" the owner reported.
                double anchor = CanonicalAtLimitAnchor;
                if (_crossCarSeed.TryGetValue(CrossCarKey(gameId, sourceIdentity), out ScaleLearnerState seed))
                {
                    double cappedSeedCeiling = Math.Max(seed.ColdCeiling, CanonicalAtLimitAnchor);
                    anchor = ColdWarmBlend.Blend(CanonicalAtLimitAnchor, cappedSeedCeiling, weight);
                }

                double average = primary.GetAverage() ?? anchor;
                return ColdWarmBlend.Blend(anchor, average, weight);
            }

            OnlineDistributionLearner secondary = Find(_generalDistribution, key);
            double? secondaryCeiling = secondary?.GetPercentile(99.0);
            if (secondaryCeiling.HasValue)
            {
                isPrimaryTier = false;
                return secondaryCeiling.Value;
            }

            // TRULY NOTHING OBSERVED for this exact key this session (primary.Count == 0, secondary not
            // ready) - per the owner's own explicit cold-start rule (docs\regression-fix-report.md,
            // Regression 3): the state before ANY local evidence for the CURRENT key must be plain
            // IDENTITY, never a value borrowed from a different car - even a capped/non-amplifying one.
            // Rescale's own null-check applies identity here (this method's null return). The cross-car
            // seed is now consulted ONLY inside the primary-tier branch above, once this key has its OWN
            // first genuine physical-limit observation (primary.Count > 0) - it no longer has a path to
            // apply itself at zero local evidence.
            isPrimaryTier = false;
            return null;
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
            _crossCarSeed.Clear();
        }

        /// <summary>CROSS-CAR SEEDING persistence (this task, RuntimeDocument Version 4) - every
        /// (gameId,sourceIdentity) seed worth carrying to the next restart, mirroring
        /// <see cref="ExportAll"/>'s own convention. Called once per frame from the composition root.</summary>
        public Dictionary<string, ScaleLearnerState> ExportCrossCarSeeds()
            => new Dictionary<string, ScaleLearnerState>(_crossCarSeed, StringComparer.Ordinal);

        /// <summary>Restores every previously persisted (gameId,sourceIdentity) cross-car seed - called
        /// once at Init, mirroring <see cref="ImportAll"/>.</summary>
        public void ImportCrossCarSeeds(IDictionary<string, ScaleLearnerState> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, ScaleLearnerState> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                if (!ClampMath.IsFinite(pair.Value.ColdCeiling) || pair.Value.ColdCeiling <= 0.0) continue;
                _crossCarSeed[pair.Key] = pair.Value;
            }
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
