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
    /// <para/>
    /// NOT MIGRATED TO THE SHARED ROBUST-BAND ESTIMATOR (docs\robust-auto-gforce-report.md, evaluated
    /// and explicitly declined for THIS class): the auto max-G/grip-peak swap to
    /// <see cref="Core.RobustBandEstimator"/> was adopted because it measurably reduced outlier
    /// sensitivity on this plugin's own captured logs. THIS class's own PRIMARY tier already only ever
    /// observes a raw reading at a moment independently detected as "physically at this car's own
    /// learned limit" (<see cref="ObserveAtPhysicalLimit"/>) - i.e. it is already anchored to rare,
    /// pre-filtered physical events rather than a raw noisy G stream, which is a different and already
    /// much more outlier-resistant mechanism than what the auto max-G/grip-peak learners had. Its own
    /// cross-car seeding, cold/warm blend and concave confidence ramp (all extensively re-tuned across
    /// multiple prior passes - see this class's own remarks above) are tightly coupled to
    /// <see cref="OnlineDistributionLearner"/>'s specific weighted-average shape; swapping that
    /// foundation for a windowed trimmed-band estimate would risk exactly the regression the owner asked
    /// this task NOT to introduce, for a call site the measured evidence does not show is actually
    /// suffering from the blind-maximum failure mode. Left as-is.
    /// </summary>
    public sealed class KeyedScaleLearner
    {
        /// <summary>
        /// Which channel this learner serves, or null when the owner did not say. Used for exactly one
        /// thing: looking up <see cref="KnownSourceColdStartReference"/>'s shipped per-source SMax during
        /// a Tier-1 cold start (see <see cref="Tier1ColdCeiling"/>).
        /// <para/>
        /// Null is the DEFAULT and deliberately so: it disables that lookup entirely, so every caller
        /// that does not name its channel keeps the pre-existing identity cold start EXACTLY. There is no
        /// "probably Lock" fallback - guessing wrong would apply the other channel's measured SMax.
        /// </summary>
        private readonly bool? _isLockChannel;

        /// <summary>
        /// Positive-sample count in this key's own secondary (percentile) learner at which the shipped
        /// cold-start reference has been COMPLETELY handed over to that learner's own evidence. The ramp
        /// starts at <see cref="OnlineDistributionLearner.MinSamplesForPercentile"/> (below which the
        /// secondary cannot answer at all), so it spans the first ~1500 positive samples of real evidence.
        /// <para/>
        /// WHY A RAMP AND NOT A SWITCH. The shipped reference and this key's own percentile ceiling are
        /// different numbers; handing straight from one to the other steps the whole output the instant
        /// the secondary matures, which is a visible jolt part-way through the first lap. Ramping means
        /// the published ceiling is continuous everywhere: at exactly
        /// <see cref="OnlineDistributionLearner.MinSamplesForPercentile"/> the weight is 0, so the value
        /// equals what the previous frame published.
        /// </summary>
        public const int ShippedReferenceHandoverSamples = 2000;

        /// <summary>
        /// The percentile of the SOURCE's own distribution that defines this key's ceiling (SMax).
        /// <para/>
        /// A high percentile rather than a maximum, so one corrupt frame or a single kerb strike cannot
        /// define the scale for a whole car; and of the SOURCE's own values rather than of the source
        /// sampled at at-limit moments, because those two anti-correlate - see
        /// <see cref="LearnedCeilingForKey"/>'s own remarks at the point of use for the measurement that
        /// established this.
        /// <para/>
        /// Deliberately the SAME percentile <see cref="Tier1ColdCeiling"/> already used, so the cold path
        /// and the learned path converge on one quantity instead of two subtly different ones.
        /// </summary>
        /// <summary>
        /// Which percentile of the CONFIDENCE-WEIGHTED at-limit distribution is this source's ceiling.
        /// <para/>
        /// The distribution is fed by <see cref="ObserveAtPhysicalLimit"/> with a continuous corner-local
        /// confidence (see NormalizedWheelLockSlipEngine.ComputeCornerAtLimitConfidence), so it holds the
        /// whole approach to the limit, weighted, not just the limit itself. 90 reads the part the
        /// detector is genuinely confident about while still discarding the last sliver of tail where a
        /// single freak frame would otherwise set the scale. Measured on the owner's own four-session
        /// 1.7.1 capture, this yields 72-78 across two cars and two sources; the mean of the same
        /// distribution yields 41-55, dragged down by the low-confidence approach frames.
        /// </summary>
        public const double PhysicalAnchorCeilingPercentile = 90.0;

        /// <summary>
        /// Any percentile of this key's own CONFIDENCE-WEIGHTED at-limit distribution - the same
        /// distribution <see cref="PhysicalAnchorCeilingPercentile"/> reads SMax from, exposed so the
        /// four-range curve's lower anchors can be placed on the SAME physical event and the SAME scale
        /// rather than on a separate, incompatible measurement (docs\cross-channel-smax-report.md).
        /// <para/>
        /// Returns null until this key has <see cref="MinPhysicalAnchorSamples"/> equivalent samples, so
        /// a caller can fall back deterministically rather than act on two or three frames.
        /// </summary>
        /// <summary>
        /// How far this key's ceiling has handed over from its cold reference to its own learned value,
        /// 0..1 - the exact product <see cref="LearnedCeiling"/> uses as its blend weight. 0 means the
        /// published ceiling is entirely the tier reference (or the canonical anchor); 1 means it is
        /// entirely this key's own physically-anchored evidence.
        /// <para/>
        /// Diagnostic only - nothing in the pipeline consumes this. It exists so a replay can report when
        /// cold start actually finished rather than inferring it from the ceiling's own movement.
        /// </summary>
        public double CeilingHandoverConfidence(string gameId, string carId, string sourceIdentity)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, string.Empty);
            OnlineDistributionLearner primary = Find(_physicalAnchor, key);
            if (primary == null || primary.Count <= 0) return 0.0;

            double weight = primary.Count >= (int)CalibrationConfidenceScaleSamples
                ? 1.0
                : ColdWarmBlend.ConcaveHotWeight(primary.Count, DispersionFor(key).CoefficientOfVariation, CalibrationConfidenceScaleSamples);
            return ClampMath.To01(weight * PhysicalAnchorReadinessWeight(key));
        }

        /// <summary>
        /// The best ceiling this learner holds for a given SOURCE, across every (game, car) it has ever
        /// learned - or null if it has none.
        /// <para/>
        /// Exists for the settings page, which needs an answer BEFORE a game is running: the ordinary
        /// lookups all need a (game, car) key, and with SimHub sitting on the menu there is no such key
        /// yet - so the page would show a shipped default even with a parameters file full of real,
        /// previously-learned evidence for exactly the source the driver has configured.
        /// <para/>
        /// BOTH STORES ARE SEARCHED, and this is the whole subtlety. The at-limit distributions
        /// (<see cref="_physicalAnchor"/>) are SESSION-SCOPED - <see cref="ImportAll"/> restores only the
        /// resulting ceiling, into <see cref="_coldCeiling"/>. A first version of this searched the
        /// distributions alone and therefore found nothing at all after a restart, which is precisely
        /// when the settings page needs it. Live evidence is preferred when this session has some;
        /// otherwise the persisted ceiling answers.
        /// <para/>
        /// Among candidates the HIGHEST wins: these are ceilings, and a key that has seen a harder lock
        /// has simply seen more of the source's range than one that has not.
        /// </summary>
        public double? PersistedCeilingForSource(string sourceIdentity)
        {
            if (string.IsNullOrEmpty(sourceIdentity)) return null;

            double? live = null;
            foreach (KeyValuePair<string, OnlineDistributionLearner> pair in _physicalAnchor)
            {
                if (!KeyMatchesSource(pair.Key, sourceIdentity)) continue;
                double? ceiling = LearnedCeilingByKey(pair.Key, out _);
                if (ceiling.HasValue && ceiling.Value > 0.0 && (!live.HasValue || ceiling.Value > live.Value))
                    live = ceiling;
            }
            if (live.HasValue) return live;

            double? persisted = null;
            foreach (KeyValuePair<string, double> pair in _coldCeiling)
            {
                if (!KeyMatchesSource(pair.Key, sourceIdentity)) continue;
                if (pair.Value > 0.0 && (!persisted.HasValue || pair.Value > persisted.Value))
                    persisted = pair.Value;
            }
            return persisted;
        }

        /// <summary>Whether a learner key's own source segment is this source. Keys are
        /// <c>game|#|car|#|source|#|surface</c> - see <see cref="KeyedGripLearner.MakeKey"/>.</summary>
        private static bool KeyMatchesSource(string key, string sourceIdentity)
        {
            if (string.IsNullOrEmpty(key)) return false;
            string[] parts = key.Split(new[] { "|#|" }, StringSplitOptions.None);
            return parts.Length >= 3 && string.Equals(parts[2], sourceIdentity, StringComparison.OrdinalIgnoreCase);
        }

        public double? PhysicalAnchorLevel(string gameId, string carId, string sourceIdentity, double percentile)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, string.Empty);
            return Find(_physicalAnchor, key)?.GetPercentile(percentile, MinPhysicalAnchorSamples);
        }

        public const double SourceCeilingPercentile = 99.0;

        /// <summary>The cautious companion to <see cref="SourceCeilingPercentile"/>. A 95th percentile
        /// has five times as many samples in its tail, so it settles far sooner and is far less exposed
        /// to a handful of saturated readings - see <see cref="SourceCeiling"/>.</summary>
        public const double SourceCeilingLowerPercentile = 95.0;

        /// <summary>
        /// How far apart the two percentiles may sit, as a fraction of the upper one, before the tail is
        /// treated as untrustworthy and the ceiling falls back entirely to the cautious percentile.
        /// <para/>
        /// Calibrated against the owner's own four-session capture, where the two cases this must
        /// separate look like:
        /// <code>
        ///   session 1 (Haas, ShakeIt):  p95 84.0  p99 100.0  -> spread 0.154   p99 IS the clamp
        ///   session 3 (RedBull, ShakeIt): p95 75.7  p99  78.0  -> spread 0.029   tail is genuine
        /// </code>
        /// 0.15 sits between them, so a clamped tail is rejected and a tight one is trusted.
        /// </summary>
        public const double MaxTrustedRelativeSpread = 0.15;

        /// <summary>Equivalent recent samples at which the sample-count half of the confidence reaches
        /// full trust. Measured on the same capture, the ceiling is within 10% of its settled value after
        /// roughly two to five seconds of ENGAGED (braking) frames - two or three corners - so this is
        /// about "has this key seen a representative spread of corners", not "has it seen enough to
        /// answer at all", which <see cref="MinPhysicalAnchorSamples"/> already governs.</summary>
        public const int CeilingConfidenceFullSamples = 2000;

        /// <summary>
        /// This key's ceiling: a confidence-weighted blend of the cautious
        /// (<see cref="SourceCeilingLowerPercentile"/>) and the aggressive
        /// (<see cref="SourceCeilingPercentile"/>) percentile of the source's own distribution.
        /// <para/>
        /// WHY BLEND RATHER THAN PICK ONE. p99 is the quantity that actually matches measurement once a
        /// distribution is populated, but it is exactly the wrong choice when the tail is thin or
        /// saturated - on a full cold start the source clips at 100 often enough that p99 IS 100, which
        /// would hand back a ceiling meaning "no amplification at all". p95 is stable much sooner but
        /// systematically understates a genuine ceiling. The blend leans on p95 while the evidence is
        /// thin or the tail looks clipped, and converges on p99 once neither is true - which is the
        /// overwhelming majority of real driving.
        /// <para/>
        /// CONFIDENCE HAS TWO INDEPENDENT HALVES, multiplied so either one can veto:
        /// <list type="bullet">
        /// <item>EVIDENCE - how many equivalent recent samples stand behind the distribution.</item>
        /// <item>AGREEMENT - how close the two percentiles are. A wide gap means the top 5% of the
        /// distribution disagrees with the top 1%, which is the signature of a clipped or one-off tail;
        /// a narrow gap means the tail is genuine.</item>
        /// </list>
        /// </summary>
        /// <summary>Equivalent recent positive samples behind this key's own source distribution, or 0
        /// when it has none yet.</summary>
        private int PositiveSamplesFor(string key)
            => Find(_generalDistribution, key)?.PositiveSampleCount ?? 0;

        /// <summary>
        /// How far this key's own source distribution has faded IN, 0..1, over
        /// <see cref="MinPhysicalAnchorSamples"/> -> <see cref="CalibrationConfidenceScaleSamples"/>
        /// equivalent samples (about 0.3s to 3s of engaged braking at 60fps).
        /// <para/>
        /// WHY EVERY PATH THAT ADOPTS THE PERCENTILE MUST USE THIS. The percentile becomes available at
        /// a threshold, and adopting it outright at that threshold is a STEP in the published number -
        /// measured at 10 points on a constant series while this blend was being added. Fading it in
        /// from whatever the path's own cold value is (a shipped reference, a borrowed tier reference,
        /// or plain identity) keeps the output continuous at every sample count.
        /// </summary>
        private double ReadinessWeight(string key)
            => ClampMath.To01(
                (double)(PositiveSamplesFor(key) - MinPhysicalAnchorSamples)
                / Math.Max(1, CeilingReadinessSamples - MinPhysicalAnchorSamples));

        /// <summary>
        /// <see cref="ReadinessWeight"/>'s counterpart for the PHYSICALLY-ANCHORED distribution - the same
        /// ramp shape, counting the evidence that actually supplies the primary branch's ceiling.
        /// <para/>
        /// These must not be confused. The two distributions fill at very different rates: the general one
        /// takes every qualifying frame, while the at-limit one takes only frames the corner-local detector
        /// is confident about (a few per corner, weighted). Ramping the primary branch on the GENERAL
        /// distribution's count - as this did briefly while the ceiling was being moved back onto the
        /// physical anchor - reads "ready" almost immediately from evidence that is not the evidence being
        /// adopted, so a two-sample at-limit percentile would be published at full weight.
        /// </summary>
        private double PhysicalAnchorReadinessWeight(string key)
        {
            int samples = Find(_physicalAnchor, key)?.PositiveSampleCount ?? 0;
            return ClampMath.To01(
                (double)(samples - MinPhysicalAnchorSamples)
                / Math.Max(1, CeilingReadinessSamples - MinPhysicalAnchorSamples));
        }

        /// <summary>Equivalent samples at which the source distribution is fully faded in - see
        /// <see cref="ReadinessWeight"/>. 100 is about 1.7 seconds of engaged braking at 60fps, roughly
        /// one corner, which keeps the owner's "90% accurate within two to three corners" target intact
        /// while still being long enough that no single sample moves the published number visibly.</summary>
        public const int CeilingReadinessSamples = 100;

        private double? SourceCeiling(string key)
        {
            OnlineDistributionLearner distribution = Find(_generalDistribution, key);
            if (distribution == null) return null;

            double? upper = distribution.GetPercentile(SourceCeilingPercentile, MinPhysicalAnchorSamples);
            double? lower = distribution.GetPercentile(SourceCeilingLowerPercentile, MinPhysicalAnchorSamples);
            if (!upper.HasValue || !lower.HasValue) return upper ?? lower;
            if (upper.Value <= lower.Value) return upper;   // degenerate/flat distribution - nothing to weigh

            double evidence = ClampMath.To01(
                (double)(distribution.PositiveSampleCount - MinPhysicalAnchorSamples)
                / Math.Max(1, CeilingConfidenceFullSamples - MinPhysicalAnchorSamples));

            double relativeSpread = upper.Value > 1e-9 ? (upper.Value - lower.Value) / upper.Value : 0.0;
            double agreement = 1.0 - ClampMath.To01(relativeSpread / MaxTrustedRelativeSpread);

            double confidence = evidence * agreement;
            return lower.Value + (upper.Value - lower.Value) * confidence;
        }

        /// <param name="isLockChannel">See <see cref="_isLockChannel"/> - omit to opt out of the shipped
        /// cold-start reference altogether.</param>
        public KeyedScaleLearner(bool? isLockChannel = null)
        {
            _isLockChannel = isLockChannel;
        }

        /// <summary>
        /// This key's Tier-1 (nothing to borrow) cold ceiling: the shipped per-source reference, ramped
        /// continuously onto this key's OWN secondary percentile ceiling as that matures. Null when this
        /// table has no evidence for the source AND the secondary is not ready - in which case the caller
        /// falls back to plain identity, exactly as before this existed.
        /// <para/>
        /// Used from BOTH cold paths - as the anchor when this key has primary evidence but nothing to
        /// borrow, and as the return value when it has no evidence at all - so the two agree by
        /// construction and the transition between them cannot step.
        /// </summary>
        private double? Tier1ColdCeiling(string key, string sourceIdentity)
        {
            bool hasShipped = _isLockChannel.HasValue
                && KnownSourceColdStartReference.TryGetSMax(sourceIdentity, _isLockChannel.Value, out double shipped)
                && shipped > 1e-6;
            double shippedSMax = 0.0;
            if (hasShipped) KnownSourceColdStartReference.TryGetSMax(sourceIdentity, _isLockChannel.Value, out shippedSMax);

            OnlineDistributionLearner secondary = Find(_generalDistribution, key);
            // The SAME confidence-weighted blend the warm path uses, so the cold hand-off converges on
            // one quantity rather than two subtly different ones.
            double? secondaryCeiling = SourceCeiling(key);

            if (!secondaryCeiling.HasValue) return hasShipped ? shippedSMax : (double?)null;

            if (!hasShipped)
            {
                // NO SHIPPED REFERENCE for this source, so the cold state is plain identity - which is
                // the same thing as a ceiling of CanonicalAtLimitAnchor. Fading from there to the
                // measured percentile keeps that hand-off continuous; returning the percentile outright
                // was a 10-point step the frame it became available.
                return CanonicalAtLimitAnchor
                       + (secondaryCeiling.Value - CanonicalAtLimitAnchor) * ReadinessWeight(key);
            }

            double weight = ShippedHandoverWeight(secondary.PositiveSampleCount);
            return shippedSMax + (secondaryCeiling.Value - shippedSMax) * weight;
        }

        private static double ShippedHandoverWeight(int positiveSamples)
        {
            const int start = OnlineDistributionLearner.MinSamplesForPercentile;
            if (positiveSamples <= start) return 0.0;
            if (positiveSamples >= ShippedReferenceHandoverSamples) return 1.0;
            return (double)(positiveSamples - start) / (ShippedReferenceHandoverSamples - start);
        }

        /// <summary>Where the physically-anchored reading is mapped to on the canonical 0-100 scale -
        /// see <see cref="NormalizedWheelLockSlipEngine"/>'s own band description ("60-80: the ideal
        /// band, up to the measured grip limit; 80-100: past the limit - locking/spinning").
        /// RESCALED (docs\anchor-rescale-report.md) from the original 75.0 to 80.0 so this value
        /// COINCIDES EXACTLY with Layer 5's own top curve anchor input (<see cref="Settings.WheelChannelSettings"/>'s
        /// shipped Curve preset already places its "Critical"/"Max Grip" anchor at input 80 for BOTH
        /// channels - see <see cref="Projection.ProjectorSettings.ApplyPreset"/> - this class's own
        /// anchor previously did NOT coincide with that value, at 75, which is exactly why the top
        /// curve anchor used to describe a point ABOVE the true physical limit, "on the verge of
        /// locking - not locked yet", rather than the limit itself). A rawValue observed exactly AT the
        /// physical limit (rawValue == the learned ceiling) now Rescales to exactly 80, landing
        /// precisely on that top anchor - which is the entire point of this constant's value:
        /// wherever it sits IS what "at the grip limit" means on the canonical scale, and the top curve
        /// anchor's input position is what must coincide with it, not the other way around. Full
        /// lock/spin (native 100 from every source, per the owner's own examples) still calibrates
        /// somewhat above 80 whenever the learned ceiling is below 100 in the source's own native units
        /// (the common case - see docs\anchor-rescale-report.md's own worked numbers), preserving
        /// headroom for "past the limit" above this anchor.</summary>
        public const double CanonicalAtLimitAnchor = 80.0;

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

        // ---- TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) ----
        // RECONCILED WITH THE CROSS-CAR SEED ABOVE, NOT LAYERED ON TOP OF IT: _crossCarSeed/CrossCarKey/
        // CrossCarSeedTrustThreshold/ExportCrossCarSeeds/ImportCrossCarSeeds above are now VESTIGIAL -
        // kept ONLY so a pre-1.0.7 RuntimeDocument still deserialises (its LockScaleCrossCarSeed/
        // SlipScaleCrossCarSeed section is simply never acted upon again - a one-time, silent drop, not a
        // throw) and so no external caller/test signature breaks. ObserveAtPhysicalLimit below no longer
        // WRITES to _crossCarSeed, and LearnedCeilingForKey no longer READS it - both are superseded by
        // ResolveReference/ResolveTier just below, which generalise the SAME "borrow a starting point
        // for a cold key" idea across all FOUR tiers (same-game-different-car was ALL the old mechanism
        // ever covered - Tier 3 exactly) using the SAME substrate (_physicalAnchor) the old mechanism drew
        // from, rather than a second, separately-maintained dictionary.
        //
        // WHY THIS RECONCILIATION, NOT A SECOND SCHEME: the old gate was "capped, never-amplifying" -
        // Rescale's factor (anchor/ceiling) could only ever be pushed <= 1 by a borrowed seed. Tiers 2-4
        // exist PRECISELY so a well-matched reference can raise a cold key's starting point, which is the
        // opposite of "never amplify" - the two cannot coexist as independent mechanisms without one
        // silently overriding the other's intent. The reconciliation applied here (see
        // LearnedCeilingForKey's own remarks): Tier 2 (a DIFFERENT GAME - the same cross-context risk
        // profile the old regression was about, arguably worse, since even the SOURCE's own native scale
        // convention can differ wildly between two unrelated titles) KEEPS the old cap; Tier 3 (same
        // game, different car) and Tier 4 (same game AND car, different surface) DROP it, since a
        // same-game (same-car, for Tier 4) reference is judged genuinely comparable, and the owner's own
        // worked example (a Tier 4 case, dry 75 blending toward wet's own cold 68 to land at 73) is
        // itself an amplification relative to the OLD identity-only cold state. This is an interpretation
        // call, stated plainly here and in the v1.0.7 report, for the owner to confirm or correct.
        //
        // A candidate must itself be PRIMARY-TIER (a genuinely physically-anchored reading, never the
        // percentile-only secondary tier) and same-source ONLY (a Raw reference is never offered to a
        // ShakeIt key or vice versa - the composite key's own source segment already enforces this by
        // construction, see TryParseFullKey/ResolveReference below).
        private readonly Dictionary<string, ColdStartTier> _resolvedTierCache = new Dictionary<string, ColdStartTier>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _resolvedReferenceKeyCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _resolvedReferenceCeilingCache = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>Splits a FULL, 4-part composite key (game|#|car|#|source|#|surface - see
        /// <see cref="KeyedGripLearner.MakeKey"/>) into its four segments. Distinct from the older,
        /// 2-segment-only <see cref="ParseKey"/> above (which only ever needed game/source for the
        /// cross-car seed's own narrower key shape) - this resolver needs every dimension to classify a
        /// candidate's tier.</summary>
        private static bool TryParseFullKey(string key, out string gameId, out string carId, out string sourceIdentity, out string surfaceBucket)
        {
            string[] parts = (key ?? string.Empty).Split(new[] { "|#|" }, StringSplitOptions.None);
            gameId = parts.Length > 0 ? parts[0] : string.Empty;
            carId = parts.Length > 1 ? parts[1] : string.Empty;
            sourceIdentity = parts.Length > 2 ? parts[2] : string.Empty;
            surfaceBucket = parts.Length > 3 ? parts[3] : string.Empty;
            return parts.Length >= 3;
        }

        /// <summary>
        /// MUTATION-EVIDENCE TEST HOOK ONLY (v1.0.7, docs\v107-tiered-coldstart-report.md's own "force
        /// tier resolution to always return Tier 1" acceptance requirement) - when true,
        /// <see cref="ResolveReference"/> short-circuits to <see cref="ColdStartTier.Tier1"/>
        /// unconditionally, reproducing pre-1.0.7 behaviour exactly (no reference ever borrowed) so the
        /// real-log replay's own before/after comparison can be produced from the SAME engine/build
        /// rather than a separately-checked-out snapshot. Defaults to false; production code never sets
        /// this. NOT thread-safe by design - a static test-only switch, not a runtime feature flag.
        /// </summary>
        public static bool ForceTier1ForTesting = false;

        /// <summary>
        /// THE RESOLVER: given (gameId, carId, sourceIdentity, currentSurfaceBucket) - the key that is
        /// COLD, or about to be queried while still cold - scans every OTHER key's own primary-tier
        /// evidence (same source only, see this class's own remarks above) and returns the most SPECIFIC
        /// tier with at least one candidate: Tier 4 (same game+car, different surface) preferred over
        /// Tier 3 (same game, different car) over Tier 2 (different game) over Tier 1 (nothing at all).
        /// Within a tier, the candidate with the MOST SAMPLES wins; a tie is broken by the
        /// lexicographically smallest composite key (a fixed, deterministic, arbitrary-but-stable
        /// tie-break - stated explicitly since the brief calls this out).
        /// <para/>
        /// CACHED PER OWN KEY (this task's own explicit "cheap, not every frame" requirement): once a
        /// key resolves to Tier 2/3/4, that answer is reused for the rest of this instance's lifetime
        /// without re-scanning - a later, even-better candidate maturing elsewhere will not upgrade an
        /// already-resolved key's tier this session (a deliberate, stated trade-off - see the v1.0.7
        /// report). A key still at Tier 1 is re-scanned on every call (cheap - the candidate pool is the
        /// number of DISTINCT keys ever seen this session, not per-frame telemetry volume), since a
        /// reference may not exist YET the first time a brand-new key is queried but may appear moments
        /// later as another key matures.
        /// </summary>
        public ColdStartTier ResolveTier(string gameId, string carId, string sourceIdentity, string currentSurfaceBucket = null)
        {
            // NOTE: ownKey is the DEFAULT (unsurfaced) storage key - the SAME key LearnedCeilingForKey's
            // own callers always read/write - NOT a key built from currentSurfaceBucket. currentSurfaceBucket
            // is passed only as the comparison HINT (see ResolveReference's own remarks) - building ownKey
            // from it instead would make THIS key's own additive surface-specific alias (see
            // ObserveAtPhysicalLimit's own remarks) look like a legitimate "different surface" candidate
            // for itself. Caught and fixed via a real failing scenario during this task's own
            // implementation.
            string ownKey = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            return ResolveReference(gameId, carId, sourceIdentity, currentSurfaceBucket ?? string.Empty, ownKey, lockToCache: false, out _);
        }

        /// <param name="lockToCache">
        /// WRITE-ONCE LATCH (see this method's own remarks): true once this exact key has its OWN first
        /// primary-tier evidence (<see cref="LearnedCeilingForKey"/>'s <c>primary.Count &gt; 0</c> branch) -
        /// the resolved tier is then cached PERMANENTLY (even Tier 1) and never re-scanned again for this
        /// key, so a car's own already-settling calibration cannot retroactively wobble just because an
        /// UNRELATED car happens to mature later in the same session (a genuine, pinned invariant -
        /// "switching away and back within one session reproduces the first visit's mapping exactly" -
        /// caught failing during this task's own implementation and fixed by this latch). False for the
        /// ZERO-EVIDENCE case (this key has never had a physical-limit observation of its own AT ALL yet)
        /// - deliberately kept UNCACHED/re-scanned on every call there, since THAT is exactly the
        /// scenario the whole feature exists to keep improving as soon as a reference becomes available,
        /// not to freeze at whatever (possibly Tier 1) answer happened to be true on the very first frame.
        /// </param>
        private ColdStartTier ResolveReference(string gameId, string carId, string sourceIdentity, string currentSurfaceBucket, string ownKey, bool lockToCache, out double referenceCeiling)
        {
            if (ForceTier1ForTesting) { referenceCeiling = 0.0; return ColdStartTier.Tier1; }

            // CACHE KEY INCLUDES THE SURFACE HINT (not just ownKey): ownKey (the DEFAULT, unsurfaced
            // storage key - see LearnedCeilingForKey's own remarks) is the SAME string regardless of
            // which surface this particular frame classified as - only currentSurfaceBucket varies. Two
            // queries for the same ownKey but a DIFFERENT currentSurfaceBucket (e.g. an early,
            // pre-trigger-gate diagnostic placeholder call with no surface hint yet, versus the real,
            // later call with the frame's own classified surface) must resolve and cache INDEPENDENTLY -
            // sharing one cache slot between them let a Tier-4 candidate this exact key ADDITIVELY teaches
            // under its own real surface (see ObserveAtPhysicalLimit's own remarks) look like a
            // "different surface" reference to ITSELF whenever the two calls disagreed on the hint,
            // self-poisoning the cache. Caught and fixed via a real failing test during this task's own
            // implementation (two pre-existing NormalizedWheelLockSlipEngineTests briefly regressed).
            string cacheKey = ownKey + "|surf#" + (currentSurfaceBucket ?? string.Empty);
            if (_resolvedTierCache.TryGetValue(cacheKey, out ColdStartTier cachedTier))
            {
                referenceCeiling = _resolvedReferenceCeilingCache.TryGetValue(cacheKey, out double cachedCeiling) ? cachedCeiling : 0.0;
                return cachedTier;
            }

            ColdStartTier bestTier = ColdStartTier.Tier1;
            string bestKey = null;
            int bestSamples = -1;
            double bestCeiling = 0.0;

            // TIER 4 GUARD (this task's own explicit constraint #5 - "if the same surface already has
            // parameters, the key is not cold and none of this applies"): if THIS EXACT surface (the
            // caller's own hint) already has genuine primary-tier evidence of its own for this
            // (game,car,source), it is not cold for Tier 4's purpose at all - a different-surface
            // candidate must never be offered as a reference for it, regardless of how much evidence that
            // OTHER surface has.
            bool currentSurfaceAlreadyHasOwnEvidence = !string.IsNullOrEmpty(currentSurfaceBucket)
                && Find(_physicalAnchor, KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, currentSurfaceBucket)) is OnlineDistributionLearner ownSurfaceLearner
                && ownSurfaceLearner.Count > 0;

            foreach (KeyValuePair<string, OnlineDistributionLearner> pair in _physicalAnchor)
            {
                if (string.Equals(pair.Key, ownKey, StringComparison.Ordinal)) continue;
                if (pair.Value == null || pair.Value.Count <= 0) continue;
                if (!TryParseFullKey(pair.Key, out string g, out string c, out string s, out string surf)) continue;
                // SAME INPUT SOURCE ONLY - a Raw reference must never be used for a ShakeIt key or vice
                // versa (this task's own explicit, load-bearing requirement).
                if (!string.Equals(s, sourceIdentity ?? string.Empty, StringComparison.Ordinal)) continue;

                ColdStartTier candidateTier;
                if (!string.Equals(g, gameId ?? string.Empty, StringComparison.Ordinal)) candidateTier = ColdStartTier.Tier2;
                else if (!string.Equals(c, carId ?? string.Empty, StringComparison.Ordinal)) candidateTier = ColdStartTier.Tier3;
                else if (!string.IsNullOrEmpty(currentSurfaceBucket) && !currentSurfaceAlreadyHasOwnEvidence
                    && !string.Equals(surf, currentSurfaceBucket, StringComparison.Ordinal))
                {
                    // TIER 4 ONLY WHEN THE CALLER GAVE A REAL SURFACE HINT (caught via a real failing test
                    // during this task's own implementation): without knowing OUR OWN current surface, a
                    // surface-tagged sibling entry for the SAME (game,car,source) cannot be safely told
                    // apart from "this key's own additive alias under a different label" - see
                    // ObserveAtPhysicalLimit's own remarks on that alias. A caller that never passes a
                    // surface hint (the overwhelming majority of pre-existing callers/tests, including
                    // every direct KeyedScaleLearner unit test) therefore never risks Tier 4
                    // self-contamination - it simply never considers surface-tagged candidates at all,
                    // falling back to at most Tier 3.
                    candidateTier = ColdStartTier.Tier4;
                }
                else continue; // identical in every dimension (or no real surface hint to compare) - not a candidate.

                if (candidateTier < bestTier) continue; // only the MOST specific tier with any candidate wins.

                double? average = pair.Value.GetAverage();
                if (!average.HasValue) continue;
                int samples = pair.Value.Count;

                bool better = candidateTier > bestTier
                    || samples > bestSamples
                    || (samples == bestSamples && bestKey != null && string.CompareOrdinal(pair.Key, bestKey) < 0);
                if (better)
                {
                    bestTier = candidateTier;
                    bestKey = pair.Key;
                    bestSamples = samples;
                    bestCeiling = average.Value;
                }
            }

            referenceCeiling = bestCeiling;
            if (lockToCache)
            {
                _resolvedTierCache[cacheKey] = bestTier;
                if (bestTier != ColdStartTier.Tier1)
                {
                    _resolvedReferenceKeyCache[cacheKey] = bestKey;
                    _resolvedReferenceCeilingCache[cacheKey] = bestCeiling;
                }
            }
            return bestTier;
        }

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
        /// <param name="surfaceBucket">
        /// TIERED COLD-START REFERENCE SYSTEM (v1.0.7) - defaults to empty, so every pre-existing caller
        /// keeps writing to the SAME default (gameId,carId,sourceIdentity,"") key as before, byte-for-byte
        /// (the live severity Rescale/LearnedCeiling calls never pass anything else). A caller that DOES
        /// pass a real surface bucket (see <see cref="NormalizedWheelLockSlipEngine"/>'s own
        /// <c>observeBucket</c>) ADDITIONALLY teaches a surface-specific key, purely so
        /// <see cref="ResolveReference"/> has genuine Tier-4 (same game+car, different surface) candidates
        /// to find - this is a purely ADDITIVE side effect that never disturbs the default key's own
        /// already-tuned calibration.</param>
        public void ObserveAtPhysicalLimit(string gameId, string carId, string sourceIdentity, double rawValue, double observationWeight = 1.0, string surfaceBucket = "")
        {
            if (!ClampMath.IsFinite(rawValue) || rawValue <= 0.0) return;
            if (!ClampMath.IsFinite(observationWeight) || observationWeight <= 0.0) return;
            OnlineDistributionLearner primary = GetOrCreate(_physicalAnchor, gameId, carId, sourceIdentity, surfaceBucket);
            // 1.0.6.0 (docs\release-1060-report.md, Part 5) - the call-site gate OnlineDistributionLearner's
            // own remarks on MaxSamples describe ("mirroring SimHub's own CalibrationPointsAdded <= 7000
            // gate at the CALL site rather than inside AddValue") was never actually wired up anywhere - a
            // genuine gap found during the 1.0.6.0 overflow audit, since without it this tier's own
            // internal _histogram dictionary keeps accepting new distinct rounded buckets for the entire
            // life of the session with no ceiling at all. Wired up here, at the one call site that ever
            // feeds this tier.
            // FEED CAP REMOVED (the forgetting-distribution fix). This used to stop folding in new
            // observations past OnlineDistributionLearner.MaxSamples (7000). That cap was borrowed by
            // analogy from SimHub's own Raw-layer CalibrationPointsAdded gate, but here it FROZE the
            // distribution outright: after roughly two minutes of engaged driving nothing could change
            // it again, so a ceiling that had settled high could never come back down. Measured before
            // this change: a learner settled at 90, fed ten times as much evidence at 30, did not move.
            // The cap's original job - bounding how much influence any one stretch of driving keeps -
            // is now done properly by the histogram's own decay, which also keeps memory bounded (the
            // bucket count is bounded by rounding, not by sample count). Reinstating the cap would
            // reinstate the ratchet.
            primary.AddValue(rawValue, observationWeight);

            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, surfaceBucket);
            WelfordAccumulator dispersion = _hotDispersion.TryGetValue(key, out WelfordAccumulator existing) ? existing : WelfordAccumulator.Empty;
            dispersion.Observe(rawValue);
            _hotDispersion[key] = dispersion;

            // NOTE: this used to also seed _crossCarSeed here - see this class's own "TIERED COLD-START
            // REFERENCE SYSTEM" remarks above for why that is now superseded by ResolveReference (which
            // reads _physicalAnchor - the dictionary `primary` above already belongs to - directly,
            // rather than maintaining a second, narrower (game,source)-only seed dictionary in parallel).
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
        public double? PublishedCeiling(string gameId, string carId, string sourceIdentity, out bool isPrimaryTier, string currentSurfaceBucket = null)
        {
            double? hot = LearnedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier, currentSurfaceBucket);
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
                return LearnedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier, null); // nothing to protect yet - hot itself becomes what gets persisted

            WelfordAccumulator dispersion = DispersionFor(key);
            if (ColdWarmBlend.ShouldPersist(dispersion.Count, dispersion.CoefficientOfVariation))
                return PublishedCeiling(gameId, carId, sourceIdentity, out isPrimaryTier, null);

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
            TryParseFullKey(key, out string gameId, out string carId, out string sourceIdentity, out string surfaceBucket);
            return LearnedCeilingForKey(gameId, carId, sourceIdentity, key, surfaceBucket, out isPrimaryTier);
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
            // 1.0.6.0 (docs\release-1060-report.md, Part 5) - same call-site gate as ObserveAtPhysicalLimit
            // above; see that method's own remarks.
            OnlineDistributionLearner secondary = GetOrCreate(_generalDistribution, gameId, carId, sourceIdentity);
            // See ObserveAtPhysicalLimit's own note on why the former 7000-sample feed cap is gone.
            secondary.AddValue(rawValue);
        }

        /// <summary>This source's own learned near-the-limit ceiling, in the SOURCE'S OWN native units -
        /// the primary (physically-anchored) tier if ready, else the secondary (percentile) tier if
        /// ready, else null (cold start). Published as a diagnostic
        /// (<c>Diag.Lock.SourceScaleCeiling</c>/<c>Diag.Slip.SourceScaleCeiling</c>) alongside
        /// <see cref="IsPrimaryTier"/> so which mechanism actually produced the number is inspectable
        /// on a rig, per this task's own explicit request.</summary>
        public double? LearnedCeiling(string gameId, string carId, string sourceIdentity, out bool isPrimaryTier, string currentSurfaceBucket = null)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            return LearnedCeilingForKey(gameId, carId, sourceIdentity, key, currentSurfaceBucket, out isPrimaryTier);
        }

        /// <summary>The actual mechanism behind both <see cref="LearnedCeiling"/> (the public,
        /// gameId/carId/sourceIdentity-shaped entry point) and <see cref="LearnedCeilingByKey"/>
        /// (<see cref="ExportAll"/>'s own by-composite-key loop) - see this class's own remarks
        /// ("COLD-START CONTINUITY AND CROSS-CAR SEEDING") for the full derivation.</summary>
        /// <param name="currentSurfaceBucket">
        /// TIERED COLD-START REFERENCE SYSTEM (v1.0.7) - the CURRENT frame's own classified surface
        /// (Sealed/Loose/etc - see <see cref="NormalizedWheelLockSlipEngine"/>'s own <c>observeBucket</c>),
        /// used ONLY so <see cref="ResolveReference"/> can tell Tier 4 (same game+car, DIFFERENT surface)
        /// apart from "this exact surface already has evidence of its own" (see that method's own
        /// remarks). Defaults to null (treated as the empty-string surface) so every pre-existing
        /// caller/test keeps behaving exactly as before - it never changes WHICH default key's own
        /// ceiling is read/written, only which surface the resolver compares Tier-4 candidates against.
        /// </param>
        private double? LearnedCeilingForKey(string gameId, string carId, string sourceIdentity, string key, string currentSurfaceBucket, out bool isPrimaryTier)
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

                // FULL-TRUST FLOOR AT THE DOCUMENTED SAMPLE SCALE (docs\anchor-rescale-report.md - a
                // safety-relevant fix found while verifying the anchor rescale): this class's own
                // <see cref="CalibrationConfidenceScaleSamples"/> XML doc quotes the owner's own worked
                // example ending "...&gt;=200 samples -&gt; weight 1.0" - but <see cref="ColdWarmBlend.ConcaveHotWeight"/>
                // is a PRODUCT of the concave count term (which DOES reach exactly 1.0 at
                // CalibrationConfidenceScaleSamples) and <see cref="ColdWarmBlend.DispersionQuality"/>
                // (which is STRICTLY LESS THAN 1.0 for any nonzero coefficient of variation - i.e. any
                // real driving session, which never repeats the exact same G/pedal/speed at every
                // physically-at-the-limit moment) - so the PRODUCT never actually reaches 1.0 for real
                // data, no matter how many additional qualifying samples accumulate afterward (measured:
                // a session with a realistic CV of 0.05-0.15 permanently plateaus at weight 0.43-0.75,
                // however many hundreds of further samples arrive - see docs\anchor-rescale-report.md's
                // own worked numbers). That silently leaves Rescale's output for a genuine
                // physically-at-the-limit reading meaningfully off <see cref="CanonicalAtLimitAnchor"/>
                // FOREVER, not just during an honestly-disclosed cold-start ramp - which defeats the
                // point of the anchor for a safety-relevant cue. Fixed narrowly, HERE ONLY (not in the
                // shared <see cref="ColdWarmBlend"/> - <see cref="Normalized.GripLearner"/>'s own use of
                // it, and every one of its own already-tuned thresholds/tests, is deliberately left
                // untouched): once genuinely abundant primary-tier evidence exists
                // (<c>primary.Count &gt;= CalibrationConfidenceScaleSamples</c>), this key's own
                // accumulated <c>average</c> is trusted FULLY (weight 1.0), matching this class's own
                // documented contract literally, regardless of dispersion - a real, if noisy, cluster of
                // 200+ physically-at-the-limit observations IS this source's own true native ceiling,
                // not a value that should stay partially anchored to the canonical constant forever.
                // Below that sample count the existing concave, dispersion-weighted ramp is unchanged -
                // this only removes the PERMANENT asymptotic cap, not the graceful early ramp.
                if (primary.Count >= (int)CalibrationConfidenceScaleSamples) weight = 1.0;

                // TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) -
                // RECONCILED replacement for the old cross-car seed (see this class's own remarks above
                // ResolveReference for why, and what changed). The ramp's own "cold" starting point is
                // still bare IDENTITY (CanonicalAtLimitAnchor) when NO reference exists at all (Tier 1 -
                // unchanged), blended toward the resolved reference by THIS SAME weight - i.e. the
                // reference contributes NOTHING at weight 0 (zero local evidence for THIS EXACT key) and
                // only nudges the ramp's own starting point as this key's own local evidence begins
                // accumulating - continuous, not a second step.
                //
                // TIER-DEPENDENT CAP (the actual reconciliation): Tier 2 (a DIFFERENT GAME - the same
                // cross-context risk the old regression was about) keeps the OLD "never amplify" cap
                // (capped at CanonicalAtLimitAnchor, so the borrowed portion can only ever REDUCE Rescale's
                // factor). Tier 3/4 (same game, and same game+car respectively) DELIBERATELY DROP that cap
                // - a same-game (same-car, for Tier 4) reference is judged genuinely comparable, and
                // letting it raise a cold key's starting point is the entire point of this feature (the
                // owner's own worked Tier-4 example is itself an amplification versus the old identity-only
                // cold state). See the v1.0.7 report for this interpretation, stated explicitly for the
                // owner to confirm or correct.
                double anchor = CanonicalAtLimitAnchor;
                // lockToCache: TRUE - this key already has its OWN primary-tier evidence (we are inside
                // the primary.Count > 0 branch), so the tier resolved HERE is latched permanently (see
                // ResolveReference's own remarks on the write-once cache) - a later, unrelated car
                // maturing elsewhere must not retroactively change THIS key's own already-settling
                // calibration.
                ColdStartTier tier = ResolveReference(gameId, carId, sourceIdentity, currentSurfaceBucket ?? string.Empty, key, lockToCache: true, out double referenceCeiling);
                if (tier != ColdStartTier.Tier1)
                {
                    // CONTINUITY WITH THE ZERO-EVIDENCE BRANCH (caught via a real failing test during this
                    // task's own implementation): anchor is the reference ceiling OUTRIGHT, NOT itself
                    // blended by `weight` first - `weight` already does the ONE necessary blend, just
                    // below, between this anchor and this key's own accumulating average. Blending anchor
                    // by weight TOO would double-apply it, reverting to bare CanonicalAtLimitAnchor right
                    // at this key's own FIRST observation (weight~0 there, since dispersion needs >= 2
                    // samples to be defined at all) - a real, measured discontinuity against the
                    // zero-evidence branch's own full-strength borrow (a 10-point jump, captured live).
                    // With anchor set directly, weight=0 correctly reproduces the zero-evidence branch's
                    // own answer EXACTLY (Blend(anchor, average, 0) == anchor == referenceCeiling).
                    anchor = tier == ColdStartTier.Tier2 ? Math.Max(referenceCeiling, CanonicalAtLimitAnchor) : referenceCeiling;
                }
                else
                {
                    // TIER 1 WITH LOCAL PRIMARY EVIDENCE. Nothing to borrow, so the anchor is this key's
                    // own Tier-1 cold ceiling - the SAME value the zero-evidence branch below publishes,
                    // which is what makes the two branches agree at weight 0 and keeps the transition
                    // between them continuous (identical reasoning to the Tier 2-4 comment just above).
                    // Falls back to bare CanonicalAtLimitAnchor when the source is unknown AND the
                    // secondary is not ready, which is exactly the pre-existing behaviour.
                    anchor = Tier1ColdCeiling(key, sourceIdentity) ?? CanonicalAtLimitAnchor;
                }

                // ---- WHAT THE CEILING IS LEARNED FROM (root-caused from the owner's own 4-session
                // c_1_7_1_e_d capture; see this class's own remarks for the full derivation).
                //
                // This used to be primary.GetAverage() - the MEAN SOURCE VALUE AT AT-LIMIT MOMENTS - and
                // that is the wrong quantity, by physics rather than by tuning. The at-limit detector
                // fires at maximum achieved deceleration (or acceleration, for Slip). A lock/slip source
                // measures wheel-versus-car divergence, which is at its MINIMUM exactly when that
                // deceleration is at its maximum: peak braking requires the tyres to be gripping, i.e.
                // NOT locking. The two anti-correlate. Measured, source value binned by achieved G as a
                // fraction of peak decel:
                //
                //     G band      1.7.1 capture     1.5.3 capture
                //     50-70%          39.9              39.3
                //     70-85%          29.2              34.6
                //     85-95%          26.8               6.7
                //     95-101%         26.3               2.2      <- the frames this used to learn from
                //
                // So the ceiling converged toward the source's value during optimal grip with no lock -
                // 25.5 for Lock and 16.9 for Slip in that capture - instead of its value at the onset of
                // lock, which is what the canonical 80 anchor represents. Rescale then multiplied by
                // 80/25, everything saturated, and the channel fired far too early and far too hard.
                //
                // This was masked for a long time: in the 1.5.x captures the learner never matured
                // (LearnerConfidence stayed 0.00), so the blend stayed pinned to the anchor and the
                // ceiling sat near it by accident. Once the confidence machinery let the weight reach
                // 1.0, the anchor dropped out and the genuinely-wrong learned value surfaced. The
                // confidence work did not cause this; it removed the mask.
                //
                // THE FIRST FIX (SUPERSEDED, kept for the reasoning): the ceiling was moved off the
                // at-limit average and onto the source's own high percentile - the GENERAL distribution,
                // every qualifying frame, no physical event involved. That produced the right MAGNITUDE
                // (74-86 against the owner's ~85) but it treated the symptom. It made SMax a statement
                // about how rare a reading is rather than about what the car was doing, which is not a
                // physical quantity at all and cannot support S75/S90 on the same scale.
                //
                // THE ACTUAL FIX (docs\cross-channel-smax-report.md): the anti-correlation was never a
                // property of at-limit frames as such - it was a property of the DETECTOR that was
                // selecting them. `physicallyAtLimit` tripped at 85% of a session-wide best-ever G, which
                // in a downforce car happens at the fast, high-grip START of a braking zone; the owner's
                // own 1.0.6.9_anchor_sampling measured it firing 45.4% early in 87.7% of events. With the
                // corner-local detector restored upstream (G plateauing or falling WHILE the source's own
                // effort is sustained or rising - the owner's own "max grip is the moment the dec G-Force
                // starts REDUCING"), the at-limit distribution finally holds the frames it always should
                // have, and its own upper percentile is BOTH physically anchored and tighter across
                // sessions than the general-distribution percentile ever was: 72-78 across two cars and
                // two sources, a 1.08x spread against the general percentile's 1.17x.
                //
                // WHY A PERCENTILE AND NOT THE MEAN. ObserveAtPhysicalLimit is fed a CONTINUOUS
                // confidence, not a boolean, so the distribution legitimately contains many
                // low-confidence frames from the approach to the limit alongside the high-confidence
                // frames at it. Averaging those together lands at 41-55 - dragged down by the approach.
                // The upper percentile reads the part of the distribution the detector is actually
                // confident about, which is the quantity the canonical 80 anchor is defined against.
                //
                // Falls back to the anchor - never to an average - while this key is still short of its
                // own evidence bar, so a half-learned distribution cannot reach the output.
                double? sourceCeiling = primary.GetPercentile(PhysicalAnchorCeilingPercentile, MinPhysicalAnchorSamples);
                double learned = sourceCeiling ?? anchor;

                // READINESS RAMP - without it, the frame the percentile first becomes available is a
                // STEP. Measured while adding the confidence blend: a constant-90 series jumped the
                // published calibration by 10 points in a single sample, because the ceiling switched
                // from the anchor to the percentile outright. Folding readiness into the existing blend
                // weight means the percentile fades in from the anchor instead of replacing it, so the
                // published number is continuous at every sample count - the property
                // RegressionFixTests.Calibration_confidence_grows_continuously... defends.
                //
                // The ramp is deliberately SHORT (MinPhysicalAnchorSamples -> CalibrationConfidenceScaleSamples,
                // i.e. 20 -> 200 equivalent samples, about 0.3s to 3s of engaged braking at 60fps). That
                // matches what the owner's own capture shows: the ceiling is within 10% of its settled
                // value after two to five seconds of braking - two or three corners - and slowing this
                // ramp down would throw that away for no gain.
                return ColdWarmBlend.Blend(anchor, learned, weight * PhysicalAnchorReadinessWeight(key));
            }

            // ZERO LOCAL EVIDENCE (primary null or primary.Count == 0) - TIERED COLD-START REFERENCE
            // SYSTEM (v1.0.7): "identity as the cold state" was a hard, project-wide rule before this
            // task and is now DELIBERATELY RELAXED for Tiers 2-4 - THAT is the entire point of borrowing a
            // reference (a future reader must not "restore" identity-at-zero-evidence here as a bug fix
            // for Tiers 2-4; it remains exactly correct, and unchanged, for genuine Tier 1). A borrowed
            // reference is preferred here over even this key's OWN secondary (percentile) tier just below -
            // a cross-context PRIMARY-anchored reference is judged more trustworthy than this exact key's
            // own noisy, un-anchored percentile fallback.
            // lockToCache: FALSE - this key has NO primary-tier evidence of its own yet; keep re-scanning
            // every call so a reference that appears moments after this key's very first (still-cold)
            // query can start helping immediately (see ResolveReference's own remarks).
            ColdStartTier zeroEvidenceTier = ResolveReference(gameId, carId, sourceIdentity, currentSurfaceBucket ?? string.Empty, key, lockToCache: false, out double zeroEvidenceCeiling);
            if (zeroEvidenceTier != ColdStartTier.Tier1)
            {
                isPrimaryTier = false; // borrowed, not this key's own primary evidence.
                return zeroEvidenceTier == ColdStartTier.Tier2 ? Math.Max(zeroEvidenceCeiling, CanonicalAtLimitAnchor) : zeroEvidenceCeiling;
            }

            // GENUINE TIER 1 - nothing to borrow. Tier1ColdCeiling returns this key's own secondary
            // (percentile) ceiling once it is ready, the shipped per-source reference before then, and a
            // continuous ramp BETWEEN the two across the secondary's own maturing evidence. Replaces the
            // former bare `return secondaryCeiling.Value`, whose hand-off from the cold state was a step.
            //
            // WHEN THE SOURCE IS UNKNOWN this is bit-identical to the previous behaviour: the shipped
            // half simply does not exist, so the method returns the secondary ceiling outright, or null
            // (plain identity - the pre-existing "cold state must be identity" contract) when the
            // secondary is not ready either. Rescale's own null-check applies identity on null.
            isPrimaryTier = false;
            return Tier1ColdCeiling(key, sourceIdentity);
        }

        /// <summary>
        /// Maps <paramref name="rawValue"/> (this source's own native scale) onto the canonical 0-100
        /// scale via this (game,car,source)'s own learned ceiling (primary tier preferred, secondary as
        /// fallback) - see this class's own remarks for the full mechanism and honest limitations.
        /// </summary>
        /// <param name="currentSurfaceBucket">See <see cref="LearnedCeilingForKey"/>'s own remarks -
        /// passed straight through to the tiered reference resolver; defaults to null (behaves exactly as
        /// pre-1.0.7) for every caller that does not pass it.</param>
        public double Rescale(string gameId, string carId, string sourceIdentity, double rawValue, string currentSurfaceBucket = null)
        {
            // COLD/WARM (item 3): PublishedCeiling, not the raw hot LearnedCeiling - identical to it for
            // every (game,car,source) with no persisted cold reference (see PublishedCeiling's own
            // remarks), so this is unchanged for every caller/test that predates LoadCold.
            double? ceiling = PublishedCeiling(gameId, carId, sourceIdentity, out _, currentSurfaceBucket);
            if (!ceiling.HasValue || ceiling.Value <= 1e-6) return rawValue; // cold start - identity, see remarks

            return ClampMath.To0100(rawValue * (CanonicalAtLimitAnchor / ceiling.Value));
        }

        private static OnlineDistributionLearner Find(Dictionary<string, OnlineDistributionLearner> store, string gameId, string carId, string sourceIdentity)
            => store.TryGetValue(KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity), out OnlineDistributionLearner learner) ? learner : null;

        private static OnlineDistributionLearner GetOrCreate(Dictionary<string, OnlineDistributionLearner> store, string gameId, string carId, string sourceIdentity, string surfaceBucket = "")
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity, surfaceBucket);
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
            _resolvedTierCache.Clear();
            _resolvedReferenceKeyCache.Clear();
            _resolvedReferenceCeilingCache.Clear();
        }

        /// <summary>VESTIGIAL as of v1.0.7 (see this class's own "TIERED COLD-START REFERENCE SYSTEM"
        /// remarks above) - <see cref="_crossCarSeed"/> is never populated any more
        /// (<see cref="ObserveAtPhysicalLimit"/> no longer writes to it), so this now always returns an
        /// empty dictionary. Kept, rather than removed outright, purely so <c>RuntimeStore</c>/
        /// <c>RuntimeCache</c> and any external caller keep compiling against the same shape - see
        /// <c>RuntimeDocument</c>'s own Version 8-&gt;9 remarks for the persisted-file side of this.</summary>
        public Dictionary<string, ScaleLearnerState> ExportCrossCarSeeds()
            => new Dictionary<string, ScaleLearnerState>(_crossCarSeed, StringComparer.Ordinal);

        /// <summary>VESTIGIAL as of v1.0.7 - a no-op (see <see cref="ExportCrossCarSeeds"/>'s own
        /// remarks). A pre-1.0.7 file's <c>LockScaleCrossCarSeed</c>/<c>SlipScaleCrossCarSeed</c> section
        /// is therefore simply DROPPED on upgrade, silently and safely - it fed a mechanism this version
        /// no longer has, and every candidate it could ever have seeded is already re-derivable from
        /// <see cref="ImportAll"/>'s own <c>_physicalAnchor</c>-equivalent evidence as this session's own
        /// primary tier re-accumulates it.</summary>
        public void ImportCrossCarSeeds(IDictionary<string, ScaleLearnerState> data)
        {
            // Intentionally does nothing - see this method's own remarks.
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
