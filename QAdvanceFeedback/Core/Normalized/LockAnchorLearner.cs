using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// FEATURE C (docs\v1068-four-range-report.md) - WHEELLOCK ONLY. Learns, per (gameId, carId,
    /// sourceIdentity), the two SOURCE-SPACE (native, same units as <see cref="KeyedScaleLearner"/>'s
    /// own calibration basis - the aggregated-All value for Lock, per Defect B) reference points the
    /// owner's four-range mapping needs:
    /// <list type="bullet">
    /// <item><b>S75</b> - the source value where deceleration reaches 75% of THIS SAME CORNER's own
    /// max-grip G.</item>
    /// <item><b>S90</b> - the source value where deceleration reaches 90% of the same corner's own
    /// max-grip G.</item>
    /// </list>
    /// <c>Smax</c> (source value AT max grip) is NOT learned here - it is
    /// <see cref="KeyedScaleLearner.LearnedCeiling"/>, already learned by the pre-existing, Defect-B-
    /// reconciled mechanism; this class only ever READS it, as the branch filter's own threshold.
    /// <para/>
    /// SPEED-AWARE, NARROWLY (owner's explicit scoping): "that corner's own max-grip G" is
    /// <see cref="GripLearner.SpeedAwarePeakG"/> at THIS frame's speed (available grip varies strongly
    /// with speed) - fed to the caller's own speed-aware ratio, passed in here as
    /// <paramref name="uSpeedAware"/>="/>. This class itself never sees speed or G directly; it only
    /// ever compares u-against-target and records a SOURCE value - keeping the "speed-aware may
    /// identify/validate the 30/60 anchors, never appear in the published projection" guard trivially
    /// satisfiable by construction (nothing downstream of this class's own output is speed-dependent).
    /// <para/>
    /// THE BRANCH FILTER (the owner's key idea - see <see cref="Observe"/>): a candidate crossing is
    /// accepted ONLY if its interpolated source value is BELOW the corner's own learned <c>Smax</c> -
    /// at/above it, the car is already PAST the limit (G is falling because the wheel is locking, not
    /// because the driver is still building toward the limit), so the frame is physically the WRONG
    /// side of the curve for a rising-branch (0-80) anchor and is discarded. Rejections are counted
    /// (<see cref="RejectedByBranchFilterCount"/>) - itself evidence the filter is doing real work.
    /// <para/>
    /// INVERSE-GAP INTERPOLATION, SIMPLIFIED (the owner's own suggested mechanism, realised directly): a
    /// 60Hz telemetry stream essentially never samples u AT exactly 0.75/0.90. Rather than a weighted
    /// average of nearby samples, this class tracks the immediately PRECEDING qualifying frame's own
    /// (u, source) pair per key and, whenever the target ratio falls BETWEEN the previous and current
    /// frame's own u (a bracket crossing, either direction), linearly interpolates the SOURCE value at
    /// exactly u=target between those two frames - the continuous-domain equivalent of "closer samples
    /// count more", collapsed to an exact answer since u is (to first order) locally linear in time
    /// across two adjacent 60Hz frames. This also naturally yields AT MOST ONE candidate per contiguous
    /// qualifying run (see <see cref="ResetRun"/>) - i.e. one observation per corner - which is exactly
    /// what "same-corner observations weighted above cross-corner" needs: since every fed observation IS
    /// already one corner's own answer, corroboration (see <see cref="ApplyImpactWeighted"/>) is always a
    /// CROSS-corner comparison, never a single corner's own frame count dominating the tally.
    /// <para/>
    /// DYNAMIC UPDATE (owner's own delegated choice - impact-weighted, reusing the exact confidence
    /// shape <see cref="GripLearner"/>'s own evidence-weighted peak estimator already uses and this
    /// codebase already trusts): <c>anchor += (observed - anchor) * impactRate</c>, where
    /// <c>impactRate</c> DOUBLES with each successive corroborating (within-tolerance-band) crossing -
    /// 15%, 30%, 60%, then capped at 100% - so a single outlier corner barely moves an established
    /// anchor, while several corners agreeing in a row converge it quickly (mirrors the owner's own
    /// worked scatter example: 50, 43, 58, 55 - a run of loosely-agreeing values pulls the anchor toward
    /// their neighbourhood without ever jumping on any ONE of them alone).
    /// <para/>
    /// NON-STOPPING: there is no sample cap anywhere in this class - <see cref="ApplyImpactWeighted"/>
    /// always has a strictly positive (if small, once <c>Hits</c> is large) impact rate, so a key can
    /// always still move given new, non-corroborating evidence - matching this project's own
    /// "reaching 200 samples must never freeze adaptation" constraint (there is no 200-sample anything
    /// here at all).
    /// <para/>
    /// RATIO-OF-Smax REFINEMENT (docs\s75-s90-slipratio-and-fit-report.md - measured, not assumed):
    /// learning the ABSOLUTE source value at a 75%/90% crossing (<see cref="Key.E75"/>/<see cref="Key.E90"/>
    /// above) means every session effectively re-derives its own answer from as few as 2-9 crossings,
    /// with no way to benefit from how reliable <see cref="KeyedScaleLearner.LearnedCeiling"/> (Smax)
    /// already is. Two OTHER approaches were tried and measured first: (1) raw wheel-speed slip ratio as
    /// the anchor's own unit - genuinely tighter cross-session PER CANDIDATE (RedBull/Dry per-session
    /// median max/min ratio 4.09x/10.71x source vs 1.36x/1.43x slip), but converting that stable slip
    /// value back into the SOURCE units the curve's own Smax/100 endpoints require reintroduces
    /// session-to-session calibration variance, leaving the shipped headline metric at rough parity with
    /// baseline (6.72x/7.03x vs 6.44x/7.67x) despite the promising diagnostic; (2) fitting the SAME
    /// source-space relationship from every qualifying frame instead of rare crossings - raised
    /// observation count 17-28x but did NOT tighten dispersion at all (8.54x/7.85x, no better than
    /// baseline), proving the earlier scarcity was not the primary driver. What DOES work, measured
    /// directly: learning the RATIO <c>interpolatedSource / Smax</c> (dimensionless, same crossing
    /// detection and branch filter as the legacy anchor, same impact-weighted update, just a rescaled
    /// tolerance floor - see <see cref="Key.E75Ratio"/>/<see cref="Key.E90Ratio"/>) and reading it back
    /// out by multiplying by THIS key's own most recently observed Smax
    /// (<see cref="Key.LastObservedSmax"/>) - RedBull/Dry (the one group with 8 independent sessions)
    /// collapses from 6.44x/7.67x to EXACTLY Smax's own 2.20x cross-session dispersion (S75=k*Smax
    /// inherits Smax's own dispersion by construction), because Smax is already the one anchor
    /// (<see cref="KeyedScaleLearner"/>'s primary, physically-anchored tier) this codebase has already
    /// measured to be reliable. PERSISTED across sessions (see <see cref="LockAnchorState"/>), which is
    /// what lets a ratio - a property of the tyre/source pairing rather than of one session's conditions
    /// - accumulate corroboration over time; gated by a minimum corroborating-hit count, so a caller that
    /// has not yet seen enough corroborating crossings falls back to the legacy anchor, unchanged.
    /// (An earlier revision of this paragraph described the ratio state as session-scoped. That was never
    /// true of the shipped code - ExportAll/ImportAll have always round-tripped it.)
    /// </summary>
    public sealed class LockAnchorLearner
    {
        public const double Target75 = 0.75;
        public const double Target90 = 0.90;

        /// <summary>0..1 impact rate for the Nth corroborating hit - <c>min(1, base*2^(hits-1))</c>:
        /// 15%, 30%, 60%, then capped at 100% from the 4th agreeing corner onward. Deliberately a
        /// faster ramp than <see cref="GripLearner"/>'s own 10%-base raise schedule - an anchor with no
        /// asymmetric "must not drift down on a single low reading" concern (unlike a MAX estimator,
        /// S75/S90 are free to move either direction as corroborated) can afford to converge a little
        /// faster once corners agree.</summary>
        private const double ImpactBase = 0.15;

        /// <summary>"Corroborating" = within this fraction of the in-progress candidate (floored, so a
        /// low-native-scale source is not held to an unrealistically tight absolute band) - mirrors
        /// <see cref="GripLearner"/>'s own tolerance-band precedent.</summary>
        private const double ToleranceFraction = 0.20;

        private const double ToleranceFloor = 2.0;

        // ---- RATIO-OF-Smax REFINEMENT (see this class's own remarks) ----

        /// <summary><see cref="ToleranceFloor"/>'s own equivalent for the ratio-space anchor (measured
        /// S75/S90 ratios-to-Smax across the 17 real logs span roughly 0.02-0.85 - a fraction of that
        /// range, not the native 0-100 source scale <see cref="ToleranceFloor"/> was tuned for).</summary>
        private const double RatioToleranceFloor = 0.03;

        /// <summary>Minimum corroborating hits on the ratio-space anchor before it is preferred over the
        /// legacy absolute anchor - a single crossing could still be a genuine outlier; mirrors the
        /// legacy impact-weighted rule's own "one corner is not enough to trust alone" philosophy.</summary>
        private const int MinRatioHitsToPrefer = 2;

        /// <summary>WHETHER the ratio-of-Smax refinement is even consulted - a single flip-to-false
        /// reverts every key to the pre-existing, byte-identical absolute-source-only behaviour
        /// (MUTATION EVIDENCE, docs\s75-s90-slipratio-and-fit-report.md).</summary>
        internal const bool PreferRatioAnchorWhenAvailable = true;

        /// <summary>INT32 OVERFLOW GUARD (docs\release-1060-report.md, Part 5 overflow audit) - identical
        /// cap/reasoning to <see cref="GripLearner.SampleCountSaturationCap"/>: every counter in this
        /// class saturates here rather than risk wrapping negative over a genuinely multi-year,
        /// never-restarted session. The learning ITSELF never stops - only these diagnostic/impact-rate
        /// counters freeze; <see cref="Level"/>-style estimates keep moving on every call regardless.</summary>
        private const int SampleCountSaturationCap = 1_000_000;

        private struct AnchorEstimate
        {
            /// <summary>0.0 = no evidence yet (a source reading is always &gt; 0 once genuinely
            /// learned - see <see cref="ApplyImpactWeighted"/>).</summary>
            public double Level;
            public double CandidateValue;
            public int Hits;
        }

        private sealed class Key
        {
            public AnchorEstimate E75;
            public AnchorEstimate E90;

            // RATIO-OF-Smax REFINEMENT (see this class's own remarks) - S75/S90 measured as a
            // dimensionless fraction of Smax instead of an absolute source value, fed by the SAME
            // bracket crossing as E75/E90 above. LastObservedSmax is the multiplier applied at read time
            // - updated every Observe call that carries a real Smax (kept, not nulled, on a frame that
            // does not - a session-scoped "most recent known Smax", not a per-frame requirement).
            public AnchorEstimate E75Ratio;
            public AnchorEstimate E90Ratio;
            public double? LastObservedSmax;

            // PHYSICALLY-DERIVED RATIOS (docs\cross-channel-smax-report.md) - learned RETROSPECTIVELY,
            // once a braking event has finished, from crossings of 0.75x and 0.90x THAT CORNER'S OWN
            // detector-identified limit G. Separate fields from E75Ratio/E90Ratio above, which keep
            // learning from the forward uSpeedAware crossing exactly as before - additive, so the legacy
            // path and everything that depends on it is untouched.
            public AnchorEstimate E75PhysicalRatio;
            public AnchorEstimate E90PhysicalRatio;

            /// <summary>The braking event currently in progress - (G, source, at-limit confidence) per
            /// qualifying frame. Extracted and cleared by <see cref="ResetRun"/>.</summary>
            public List<CornerFrame> Run;

            // Run-bracket tracking (see this class's own remarks) - the immediately preceding
            // qualifying frame's own (u, source) pair, per target. Reset by ResetRun whenever the
            // caller's own qualifying run breaks (mirrors _lockLastG's own remarks in
            // NormalizedWheelLockSlipEngine) so a bracket is never detected ACROSS a gap.
            public double? LastU;
            public double? LastSource;
        }

        private readonly Dictionary<string, Key> _keys = new Dictionary<string, Key>(StringComparer.Ordinal);
        private int _rejectedByBranchFilter;
        private int _acceptedObservations;

        /// <summary>How many candidate crossings the branch filter discarded (interpolated source at or
        /// above the corner's own learned Smax) - session-scoped, diagnostic only. A nonzero count is
        /// itself evidence the filter is engaging on real, contaminated data.</summary>
        public int RejectedByBranchFilterCount => _rejectedByBranchFilter;

        /// <summary>How many candidate crossings were accepted and folded into an anchor - session-scoped,
        /// diagnostic only.</summary>
        public int AcceptedObservationCount => _acceptedObservations;

        /// <summary>One buffered frame of the braking event in progress - see <see cref="Key.Run"/>.</summary>
        internal struct CornerFrame
        {
            public double G;
            public double Source;
            public double Confidence;
        }

        /// <summary>Below this peak confidence a braking event is not treated as having reached the
        /// limit at all, so it teaches nothing. Matches the floor the derivation was measured under
        /// (medians were unchanged from 0.25 to 0.50 - see <see cref="ObserveCornerFrame"/>).</summary>
        private const double MinCornerConfidenceToLearn = 0.25;

        /// <summary>Deceleration below this is too small for "75% of it" to mean anything physical.</summary>
        private const double MinLimitG = 0.3;

        /// <summary>Hard cap on the buffered event, so a pathological never-ending "braking run" cannot
        /// grow without bound. Far above any real braking event (a long one is a few hundred frames).</summary>
        private const int MaxRunFrames = 2048;

        /// <summary>
        /// Buffer one qualifying frame of the braking event in progress. Nothing is learned here - the
        /// anchors can only be extracted once the event has FINISHED, because the crossings of
        /// 0.75x/0.90x this corner's limit G happen BEFORE that limit is known.
        /// <para/>
        /// WHY RETROSPECTIVE AND NOT A RUNNING REFERENCE. The obvious cheaper design - divide this
        /// frame's G by a running estimate of the limit learned from earlier corners - was measured and
        /// rejected: the limit G varies from 1.14g to 4.22g BETWEEN corners on the owner's own capture
        /// (a hairpin and a fast sweeper are simply different), so a running mean is a poor proxy for
        /// the corner actually being driven. Measured against each corner's OWN limit the S90 ratio is
        /// 0.61-0.80 across four sessions; against a running mean it collapses to 0.44-0.54 and scatters.
        /// </summary>
        public void ObserveCornerFrame(string gameId, string carId, string sourceIdentity,
            double magnitudeG, double sourceRawValue, double atLimitConfidence, double? smaxRaw)
        {
            if (!ClampMath.IsFinite(magnitudeG) || !ClampMath.IsFinite(sourceRawValue)) return;
            if (!ClampMath.IsFinite(atLimitConfidence) || atLimitConfidence < 0.0) return;

            Key k = GetOrCreate(gameId, carId, sourceIdentity);
            if (smaxRaw.HasValue) k.LastObservedSmax = smaxRaw;
            if (k.Run == null) k.Run = new List<CornerFrame>();
            if (k.Run.Count >= MaxRunFrames) return;
            k.Run.Add(new CornerFrame { G = magnitudeG, Source = sourceRawValue, Confidence = atLimitConfidence });
        }

        /// <summary>
        /// Extract this key's S75/S90 ratios from the braking event that just finished, then clear it.
        /// <para/>
        /// The event's own limit is the frame of PEAK at-limit confidence - the same corner-local
        /// detector that defines SMax, so all three anchors come from one physical event. From there we
        /// walk BACK for the last rising crossing of 0.75x and 0.90x that limit's G and read the source,
        /// expressing each as a fraction of the source AT the limit. Those fractions are what is learned
        /// (dimensionless, and stable at ~0.49/~0.72 across two cars and two sources); the absolute
        /// crossings are not, because the limit G differs corner to corner.
        /// </summary>
        private static void ExtractCornerAnchors(Key k)
        {
            List<CornerFrame> run = k.Run;
            if (run == null || run.Count < 3) { k.Run?.Clear(); return; }

            int best = 0;
            for (int i = 1; i < run.Count; i++)
                if (run[i].Confidence > run[best].Confidence) best = i;

            double gLimit = run[best].G, sLimit = run[best].Source;
            if (run[best].Confidence < MinCornerConfidenceToLearn || gLimit < MinLimitG || sLimit <= 0.0)
            {
                run.Clear();
                return;
            }

            ApplyCrossing(run, best, gLimit, sLimit, Target75, ref k.E75PhysicalRatio);
            ApplyCrossing(run, best, gLimit, sLimit, Target90, ref k.E90PhysicalRatio);
            run.Clear();
        }

        private static void ApplyCrossing(List<CornerFrame> run, int limitIndex,
            double gLimit, double sLimit, double target, ref AnchorEstimate estimate)
        {
            double level = target * gLimit;
            for (int i = limitIndex; i > 0; i--)
            {
                if (run[i - 1].G >= level || level > run[i].G) continue;
                double span = run[i].G - run[i - 1].G;
                double t = span > 1e-9 ? (level - run[i - 1].G) / span : 0.0;
                double source = run[i - 1].Source + (run[i].Source - run[i - 1].Source) * t;
                if (source > 0.0)
                    ApplyImpactWeighted(ref estimate, source / sLimit, RatioToleranceFloor);
                return;
            }
        }

        /// <summary>
        /// Corroborating corners at which this key's own learned ratio is trusted COMPLETELY and the
        /// seed has faded out entirely. Below it the two are blended - see
        /// <see cref="PhysicalRatioOrSeed"/>.
        /// </summary>
        private const int RatioFullConfidenceHits = 5;

        /// <summary>
        /// This key's physically-derived S75/S90 ratio, FADED IN from the caller's seed as corroborating
        /// corners accumulate - never switched to outright.
        /// <para/>
        /// WHY A RAMP AND NOT A GATE. An earlier revision returned the learned ratio the moment a key
        /// reached <see cref="MinRatioHitsToPrefer"/> corroborating corners and the seed until then,
        /// which steps the published curve's knots the instant the second corner lands - the same class
        /// of single-sample discontinuity <see cref="KeyedScaleLearner"/>'s own readiness ramp exists to
        /// prevent, and visible to the driver as the shake changing character mid-lap for no reason the
        /// driving explains. The seed is a REFERENCE, so it should hand over gradually exactly the way
        /// the SMax tier reference does.
        /// </summary>
        private static double PhysicalRatioOrSeed(AnchorEstimate estimate, double seed)
        {
            if (estimate.Level <= 0.0 || estimate.Hits <= 0) return seed;
            double confidence = ClampMath.To01(
                (estimate.Hits - 1) / (double)Math.Max(1, RatioFullConfidenceHits - 1));
            return seed + (estimate.Level - seed) * confidence;
        }

        /// <summary>This key's S75 ratio blended with <paramref name="seedRatio"/> by how many
        /// corroborating corners have taught it - see <see cref="PhysicalRatioOrSeed"/>. Returns the seed
        /// unchanged when nothing has been learned yet.</summary>
        public double PhysicalS75Ratio(string gameId, string carId, string sourceIdentity, double seedRatio)
        {
            Key k = Find(gameId, carId, sourceIdentity);
            return k == null ? seedRatio : PhysicalRatioOrSeed(k.E75PhysicalRatio, seedRatio);
        }

        /// <summary>S90's counterpart to <see cref="PhysicalS75Ratio"/>.</summary>
        public double PhysicalS90Ratio(string gameId, string carId, string sourceIdentity, double seedRatio)
        {
            Key k = Find(gameId, carId, sourceIdentity);
            return k == null ? seedRatio : PhysicalRatioOrSeed(k.E90PhysicalRatio, seedRatio);
        }

        /// <summary>How far this key's own learned S75/S90 ratios have taken over from the seed, 0..1 -
        /// diagnostic, so a replay/report can show whether a number is seeded or genuinely learned.</summary>
        public double PhysicalRatioConfidence(string gameId, string carId, string sourceIdentity)
        {
            Key k = Find(gameId, carId, sourceIdentity);
            if (k == null) return 0.0;
            int hits = Math.Min(
                k.E75PhysicalRatio.Level > 0.0 ? k.E75PhysicalRatio.Hits : 0,
                k.E90PhysicalRatio.Level > 0.0 ? k.E90PhysicalRatio.Hits : 0);
            return ClampMath.To01((hits - 1) / (double)Math.Max(1, RatioFullConfidenceHits - 1));
        }

        public double? LearnedS75(string gameId, string carId, string sourceIdentity)
        {
            Key k = Find(gameId, carId, sourceIdentity);
            if (k == null) return null;
            if (PreferRatioAnchorWhenAvailable)
            {
                double? viaRatio = RatioAwareLevel(k.E75Ratio, k.LastObservedSmax);
                if (viaRatio.HasValue) return viaRatio;
            }
            return LevelOrNull(k.E75);
        }

        public double? LearnedS90(string gameId, string carId, string sourceIdentity)
        {
            Key k = Find(gameId, carId, sourceIdentity);
            if (k == null) return null;
            if (PreferRatioAnchorWhenAvailable)
            {
                double? viaRatio = RatioAwareLevel(k.E90Ratio, k.LastObservedSmax);
                if (viaRatio.HasValue) return viaRatio;
            }
            return LevelOrNull(k.E90);
        }

        /// <summary>RATIO-OF-Smax REFINEMENT read path (see this class's own remarks) - if the
        /// ratio-space anchor has enough corroborating hits AND a current Smax is known, returns the
        /// CONVERTED (source-units) value; otherwise null, so the caller falls back to the legacy
        /// absolute-source anchor untouched.</summary>
        private static double? RatioAwareLevel(AnchorEstimate ratioEstimate, double? lastObservedSmax)
        {
            if (ratioEstimate.Level <= 0.0 || ratioEstimate.Hits < MinRatioHitsToPrefer || !lastObservedSmax.HasValue) return null;
            double value = ratioEstimate.Level * lastObservedSmax.Value;
            return value > 0.0 ? value : (double?)null;
        }

        private static double? LevelOrNull(AnchorEstimate? estimate)
            => estimate.HasValue && estimate.Value.Level > 0.0 ? estimate.Value.Level : (double?)null;

        /// <summary>Breaks the run-bracket tracking for this key - call whenever the caller's own
        /// qualifying run ends (not triggered, direction unknown/wrong, or no G signal this frame) so
        /// the NEXT qualifying frame does not compare its own u against a stale value from before the
        /// gap. Does NOT reset the learned anchors themselves.</summary>
        public void ResetRun(string gameId, string carId, string sourceIdentity)
        {
            Key k = GetOrCreate(gameId, carId, sourceIdentity);
            k.LastU = null;
            k.LastSource = null;
            // THE BRAKING EVENT JUST ENDED, which is the only moment its own limit is known - so this is
            // where the physically-derived anchors are extracted (see ObserveCornerFrame's own remarks
            // for why it cannot be done frame-by-frame). Always clears the buffer, including when the
            // event taught nothing, so a run that never reached the limit cannot leak into the next one.
            ExtractCornerAnchors(k);
            // LastObservedSmax is deliberately NOT reset here - it is a session-scoped "most recently
            // known Smax", not run-bracket state; a gap between corners does not make Smax itself stale.
        }

        /// <summary>Folds one qualifying frame's own (speed-aware utilization, source reading) pair in -
        /// see this class's own remarks for the full bracket/filter/update mechanism.</summary>
        /// <param name="uSpeedAware">This frame's own G achieved as a fraction of THIS CORNER's own
        /// speed-aware max grip (see this class's own remarks) - NEVER the flat, non-speed-aware ratio
        /// the live severity/gate itself uses.</param>
        /// <param name="sourceRawValue">This frame's own calibration-basis source reading (native units -
        /// the SAME aggregated-All value <see cref="KeyedScaleLearner"/> calibrates against for Lock).</param>
        /// <param name="smaxRaw">This key's own currently-learned Smax (native units,
        /// <see cref="KeyedScaleLearner.LearnedCeiling"/>) - null while not yet calibrated, in which case
        /// the branch filter cannot evaluate anything yet and every candidate is held (neither accepted
        /// nor counted as rejected) until Smax itself exists.</param>
        public void Observe(string gameId, string carId, string sourceIdentity, double uSpeedAware, double sourceRawValue, double? smaxRaw)
        {
            if (!ClampMath.IsFinite(uSpeedAware) || !ClampMath.IsFinite(sourceRawValue)) return;

            Key k = GetOrCreate(gameId, carId, sourceIdentity);
            if (smaxRaw.HasValue) k.LastObservedSmax = smaxRaw;
            if (k.LastU.HasValue && k.LastSource.HasValue)
            {
                TryCrossing(k.LastU.Value, k.LastSource.Value, uSpeedAware, sourceRawValue, Target75, smaxRaw, ref k.E75, ref k.E75Ratio);
                TryCrossing(k.LastU.Value, k.LastSource.Value, uSpeedAware, sourceRawValue, Target90, smaxRaw, ref k.E90, ref k.E90Ratio);
            }
            k.LastU = uSpeedAware;
            k.LastSource = sourceRawValue;
        }

        private void TryCrossing(double uPrev, double sPrev, double uCur, double sCur, double target, double? smaxRaw, ref AnchorEstimate estimate, ref AnchorEstimate ratioEstimate)
        {
            if (uPrev == uCur) return;
            bool brackets = (uPrev - target) * (uCur - target) <= 0.0;
            if (!brackets) return;

            double t = ClampMath.Clamp(ClampMath.SafeDiv(target - uPrev, uCur - uPrev, 0.5), 0.0, 1.0);
            double interpolatedSource = sPrev + t * (sCur - sPrev);
            if (!ClampMath.IsFinite(interpolatedSource) || interpolatedSource <= 0.0) return;

            // THE BRANCH FILTER (see this class's own remarks) - only evaluable once Smax itself has
            // been learned; before that, a candidate is neither accepted nor counted as rejected (there
            // is nothing yet to filter against - not the same as "passed").
            if (smaxRaw.HasValue)
            {
                if (interpolatedSource >= smaxRaw.Value)
                {
                    if (_rejectedByBranchFilter < SampleCountSaturationCap) _rejectedByBranchFilter++;
                    return;
                }
            }
            else
            {
                return;
            }

            ApplyImpactWeighted(ref estimate, interpolatedSource);
            if (_acceptedObservations < SampleCountSaturationCap) _acceptedObservations++;

            // RATIO-OF-Smax REFINEMENT - the SAME accepted crossing, recording the dimensionless
            // fraction of Smax instead of the absolute source value (see this class's own remarks).
            // smaxRaw.Value is always > interpolatedSource >= 0 here (the branch filter above already
            // guarantees it), and is never zero (KeyedScaleLearner never publishes a zero ceiling), so
            // this division is always well-defined.
            double ratio = interpolatedSource / smaxRaw.Value;
            if (ClampMath.IsFinite(ratio) && ratio > 0.0)
            {
                ApplyImpactWeighted(ref ratioEstimate, ratio, RatioToleranceFloor);
            }
        }

        /// <summary>The dynamic update rule - see this class's own remarks.</summary>
        /// <param name="toleranceFloor">SCALE-DEPENDENT: <see cref="ToleranceFloor"/> (2.0) is calibrated
        /// for the SOURCE native scale (0-100). A caller learning a DIFFERENT-scale quantity (e.g. the
        /// ratio-of-Smax refinement's own 0-1-ish fraction) must pass a floor appropriate to that scale -
        /// reusing 2.0 unchanged would swamp <see cref="ToleranceFraction"/> entirely, making every
        /// observation "match" trivially regardless of actual agreement.</param>
        private static void ApplyImpactWeighted(ref AnchorEstimate estimate, double observed, double toleranceFloor = ToleranceFloor)
        {
            if (estimate.Level <= 0.0)
            {
                // First-ever evidence for this key/target - nothing established yet to protect, so the
                // very first corner's own answer becomes the starting estimate directly (mirrors
                // GripLearner.AdaptivePeakState.Seeded's own "first evidence IS the seed" convention).
                estimate.Level = observed;
                estimate.CandidateValue = observed;
                estimate.Hits = 1;
                return;
            }

            double bandReference = estimate.Hits > 0 ? estimate.CandidateValue : observed;
            double band = Math.Max(ToleranceFraction * bandReference, toleranceFloor);
            bool matches = estimate.Hits > 0 && Math.Abs(observed - estimate.CandidateValue) <= band;

            // INT32 OVERFLOW GUARD - see SampleCountSaturationCap's own remarks. Harmless functionally
            // (impact below already clamps to 1.0 well before this many hits), but must still not
            // overflow over a genuinely multi-year session.
            estimate.Hits = matches ? (estimate.Hits < SampleCountSaturationCap ? estimate.Hits + 1 : estimate.Hits) : 1;
            estimate.CandidateValue = observed;

            double impact = ImpactBase * Math.Pow(2.0, estimate.Hits - 1);
            if (impact > 1.0) impact = 1.0;
            estimate.Level += impact * (observed - estimate.Level);
        }

        /// <summary>
        /// Clamp a hit count arriving from persisted state into [0, <see cref="SampleCountSaturationCap"/>].
        /// <para/>
        /// Applied on EVERY import path, legacy included. In-process the counters can never exceed the cap
        /// (<see cref="ApplyImpactWeighted"/> saturates), so int is comfortably sufficient - the cap is
        /// 1,000,000 against int's own 2,147,483,647, a 2000x margin that a plugin running continuously
        /// for years cannot close. Persisted state is the one way a value could arrive from outside that
        /// invariant (a hand-edited or corrupted save), and while the downstream arithmetic does survive
        /// it - Math.Pow(2, huge) yields Infinity, which the existing `impact > 1.0` clamp catches - that
        /// is an accident of IEEE semantics rather than a designed guard, so the value is clamped here
        /// instead of relying on it.
        /// </summary>
        private static int ClampHits(int hits)
            => hits < 0 ? 0 : (hits > SampleCountSaturationCap ? SampleCountSaturationCap : hits);

        private Key Find(string gameId, string carId, string sourceIdentity)
            => _keys.TryGetValue(KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity), out Key k) ? k : null;

        private Key GetOrCreate(string gameId, string carId, string sourceIdentity)
        {
            string key = KeyedGripLearner.MakeKey(gameId, carId, sourceIdentity);
            if (!_keys.TryGetValue(key, out Key k))
            {
                k = new Key();
                _keys[key] = k;
            }
            return k;
        }

        /// <summary>Clears every learned key AND the run-bracket tracking - a full "forget everything",
        /// mirroring <see cref="KeyedGripLearner.Reset"/>. NOT called on an ordinary game/car/source
        /// switch (each key is already isolated).</summary>
        public void Reset()
        {
            _keys.Clear();
            _rejectedByBranchFilter = 0;
            _acceptedObservations = 0;
        }

        /// <summary>Snapshots every key with at least one learned anchor - mirrors
        /// <see cref="KeyedGripLearner.ExportAll"/>'s own convention.</summary>
        public Dictionary<string, LockAnchorState> ExportAll()
        {
            var export = new Dictionary<string, LockAnchorState>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Key> pair in _keys)
            {
                bool hasLegacy = pair.Value.E75.Level > 0.0 || pair.Value.E90.Level > 0.0;
                bool hasRatio = pair.Value.E75Ratio.Level > 0.0 || pair.Value.E90Ratio.Level > 0.0;
                bool hasPhysical = pair.Value.E75PhysicalRatio.Level > 0.0 || pair.Value.E90PhysicalRatio.Level > 0.0;
                if (!hasLegacy && !hasRatio && !hasPhysical) continue;
                export[pair.Key] = new LockAnchorState
                {
                    S75 = pair.Value.E75.Level,
                    Hits75 = pair.Value.E75.Hits,
                    Candidate75 = pair.Value.E75.CandidateValue,
                    S90 = pair.Value.E90.Level,
                    Hits90 = pair.Value.E90.Hits,
                    Candidate90 = pair.Value.E90.CandidateValue,
                    // RATIO-OF-Smax REFINEMENT (see this class's own remarks) - POOLED ACROSS SESSIONS,
                    // exactly like the legacy fields above, via the SAME RuntimeStore Import/Export
                    // round trip this class already goes through every Init - "pooling across sessions
                    // is legitimate for a constant" is otherwise unrealized if this state stays
                    // session-scoped (measured directly: a single session's own 2-9 crossings are just
                    // as noisy whether expressed as an absolute value or as ITS OWN ratio to that SAME
                    // session's Smax - the two are algebraically equivalent within one session; only
                    // pooling the ratio ACROSS independent sessions cancels the noise while Smax's own
                    // per-session value stays session-appropriate).
                    RatioLevel75 = pair.Value.E75Ratio.Level,
                    RatioHits75 = pair.Value.E75Ratio.Hits,
                    RatioCandidate75 = pair.Value.E75Ratio.CandidateValue,
                    RatioLevel90 = pair.Value.E90Ratio.Level,
                    RatioHits90 = pair.Value.E90Ratio.Hits,
                    RatioCandidate90 = pair.Value.E90Ratio.CandidateValue,
                    // See LockAnchorState.PhysicalRatioLevel75's own remarks for why these pool across
                    // sessions where SMax itself deliberately does not.
                    PhysicalRatioLevel75 = pair.Value.E75PhysicalRatio.Level,
                    PhysicalRatioHits75 = pair.Value.E75PhysicalRatio.Hits,
                    PhysicalRatioCandidate75 = pair.Value.E75PhysicalRatio.CandidateValue,
                    PhysicalRatioLevel90 = pair.Value.E90PhysicalRatio.Level,
                    PhysicalRatioHits90 = pair.Value.E90PhysicalRatio.Hits,
                    PhysicalRatioCandidate90 = pair.Value.E90PhysicalRatio.CandidateValue,
                };
            }
            return export;
        }

        /// <summary>Restores every previously persisted key - called once at Init, mirroring
        /// <see cref="KeyedGripLearner.ImportAll"/>.</summary>
        public void ImportAll(IDictionary<string, LockAnchorState> data)
        {
            if (data == null) return;
            foreach (KeyValuePair<string, LockAnchorState> pair in data)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                var k = new Key();
                if (pair.Value.S75 > 0.0)
                {
                    k.E75 = new AnchorEstimate { Level = pair.Value.S75, CandidateValue = pair.Value.Candidate75 > 0.0 ? pair.Value.Candidate75 : pair.Value.S75, Hits = ClampHits(pair.Value.Hits75) };
                }
                if (pair.Value.S90 > 0.0)
                {
                    k.E90 = new AnchorEstimate { Level = pair.Value.S90, CandidateValue = pair.Value.Candidate90 > 0.0 ? pair.Value.Candidate90 : pair.Value.S90, Hits = ClampHits(pair.Value.Hits90) };
                }
                // RATIO-OF-Smax REFINEMENT - absent (both 0/default) on any save persisted before this
                // refinement shipped; that is the correct, harmless "no ratio evidence yet" cold state
                // (LearnedS75/90 fall straight back to the legacy k.E75/E90 above), not a migration.
                if (pair.Value.RatioLevel75 > 0.0)
                {
                    k.E75Ratio = new AnchorEstimate { Level = pair.Value.RatioLevel75, CandidateValue = pair.Value.RatioCandidate75 > 0.0 ? pair.Value.RatioCandidate75 : pair.Value.RatioLevel75, Hits = ClampHits(pair.Value.RatioHits75) };
                }
                if (pair.Value.RatioLevel90 > 0.0)
                {
                    k.E90Ratio = new AnchorEstimate { Level = pair.Value.RatioLevel90, CandidateValue = pair.Value.RatioCandidate90 > 0.0 ? pair.Value.RatioCandidate90 : pair.Value.RatioLevel90, Hits = ClampHits(pair.Value.RatioHits90) };
                }
                // PHYSICALLY-DERIVED RATIOS - same convention: absent (0/default) on a save written
                // before this shipped, which reads as "nothing learned" and correctly starts the
                // seed-to-learned hand-over ramp from the beginning. Hits is clamped on the way IN as
                // well as on the way out, so a corrupt or hand-edited save cannot introduce a value the
                // ramp/impact arithmetic (which raises 2 to the power of Hits-1) would have to survive.
                if (pair.Value.PhysicalRatioLevel75 > 0.0)
                {
                    k.E75PhysicalRatio = new AnchorEstimate { Level = pair.Value.PhysicalRatioLevel75, CandidateValue = pair.Value.PhysicalRatioCandidate75 > 0.0 ? pair.Value.PhysicalRatioCandidate75 : pair.Value.PhysicalRatioLevel75, Hits = ClampHits(pair.Value.PhysicalRatioHits75) };
                }
                if (pair.Value.PhysicalRatioLevel90 > 0.0)
                {
                    k.E90PhysicalRatio = new AnchorEstimate { Level = pair.Value.PhysicalRatioLevel90, CandidateValue = pair.Value.PhysicalRatioCandidate90 > 0.0 ? pair.Value.PhysicalRatioCandidate90 : pair.Value.PhysicalRatioLevel90, Hits = ClampHits(pair.Value.PhysicalRatioHits90) };
                }
                _keys[pair.Key] = k;
            }
        }
    }

    /// <summary>Plain, Newtonsoft-round-trippable snapshot of one (gameId, carId, sourceIdentity)'s
    /// learned S75/S90 anchors - see <see cref="LockAnchorLearner.ExportAll"/>/<see cref="LockAnchorLearner.ImportAll"/>,
    /// mirroring <see cref="GripLearnerState"/>'s own shape/role. WheelLock ONLY - there is no Slip
    /// equivalent (Feature C is explicitly out of scope for Slip).</summary>
    public sealed class LockAnchorState
    {
        public double S75;
        public int Hits75;
        public double Candidate75;

        public double S90;
        public int Hits90;
        public double Candidate90;

        // RATIO-OF-Smax REFINEMENT (docs\s75-s90-slipratio-and-fit-report.md) - additive fields, absent
        // (defaulting to 0) on any save persisted before this refinement shipped; see
        // LockAnchorLearner.ImportAll's own remarks for why that default is the correct cold state, not
        // a migration concern.
        public double RatioLevel75;
        public int RatioHits75;
        public double RatioCandidate75;

        public double RatioLevel90;
        public int RatioHits90;
        public double RatioCandidate90;

        // PHYSICALLY-DERIVED RATIOS (docs\cross-channel-smax-report.md) - the S75/S90 ratios learned
        // retrospectively from each corner's own detector-identified limit G. Additive fields, absent
        // (defaulting to 0) on any save written before this shipped, which is exactly the correct cold
        // state: a zero Level means "nothing learned", so the seed ratio is used and the hand-over ramp
        // starts from the beginning - no migration needed.
        //
        // WHY THESE MUST PERSIST. A key needs several corroborating corners before its own ratios take
        // over from the seed, and a single session frequently does not supply them (measured: two of the
        // owner's own four sessions ended at 0% hand-over). Pooling ACROSS sessions is what makes the
        // learned pair reachable at all - and it is legitimate for these specifically, because a RATIO is
        // a property of the tyre/source pairing rather than of one session's conditions, unlike SMax
        // itself, whose absolute value stays session-appropriate.
        public double PhysicalRatioLevel75;
        public int PhysicalRatioHits75;
        public double PhysicalRatioCandidate75;

        public double PhysicalRatioLevel90;
        public int PhysicalRatioHits90;
        public double PhysicalRatioCandidate90;
    }
}
