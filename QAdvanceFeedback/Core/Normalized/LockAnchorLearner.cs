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
    /// measured to be reliable. Session-scoped only (not exported/imported) so the legacy absolute
    /// anchors above remain the persisted, backward-compatible answer and no saved-state format changes;
    /// gated by a minimum corroborating-hit count - a caller that never sees enough corroborating
    /// crossings this session simply falls back to the legacy anchor, unchanged.
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
                if (!hasLegacy && !hasRatio) continue;
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
                    k.E75 = new AnchorEstimate { Level = pair.Value.S75, CandidateValue = pair.Value.Candidate75 > 0.0 ? pair.Value.Candidate75 : pair.Value.S75, Hits = Math.Max(0, pair.Value.Hits75) };
                }
                if (pair.Value.S90 > 0.0)
                {
                    k.E90 = new AnchorEstimate { Level = pair.Value.S90, CandidateValue = pair.Value.Candidate90 > 0.0 ? pair.Value.Candidate90 : pair.Value.S90, Hits = Math.Max(0, pair.Value.Hits90) };
                }
                // RATIO-OF-Smax REFINEMENT - absent (both 0/default) on any save persisted before this
                // refinement shipped; that is the correct, harmless "no ratio evidence yet" cold state
                // (LearnedS75/90 fall straight back to the legacy k.E75/E90 above), not a migration.
                if (pair.Value.RatioLevel75 > 0.0)
                {
                    k.E75Ratio = new AnchorEstimate { Level = pair.Value.RatioLevel75, CandidateValue = pair.Value.RatioCandidate75 > 0.0 ? pair.Value.RatioCandidate75 : pair.Value.RatioLevel75, Hits = Math.Max(0, pair.Value.RatioHits75) };
                }
                if (pair.Value.RatioLevel90 > 0.0)
                {
                    k.E90Ratio = new AnchorEstimate { Level = pair.Value.RatioLevel90, CandidateValue = pair.Value.RatioCandidate90 > 0.0 ? pair.Value.RatioCandidate90 : pair.Value.RatioLevel90, Hits = Math.Max(0, pair.Value.RatioHits90) };
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
    }
}
