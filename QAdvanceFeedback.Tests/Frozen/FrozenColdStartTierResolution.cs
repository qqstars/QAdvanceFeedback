// =====================================================================================================
// FROZEN TEST ORACLE - verbatim test oracle, never edit.
//
// A verbatim copy of the tier-resolution slice of QAdvanceFeedback\Core\Normalized\KeyedScaleLearner.cs
// (the _resolvedTierCache / _resolvedReferenceKeyCache / _resolvedReferenceCeilingCache fields,
// TryParseFullKey, ForceTier1ForTesting, ResolveTier, ResolveReference and the key-only Find overload)
// exactly as it stood immediately BEFORE that logic was extracted into the shared ColdStartTierResolver
// (slip-angle plan, Task 8). Changes, all non-behavioural: the namespace (QAdvanceFeedback.Tests.Frozen,
// plus usings of the live namespaces so the code and doc crefs resolve), the type (an internal
// FrozenColdStartTierResolution holding only that slice), the _physicalAnchor field is now handed in
// through the constructor instead of being owned and fed by the learner, ResolveReference is internal
// (was private) so the equivalence test can drive the lockToCache path directly, and ONE additive
// read-only accessor (TryGetCachedReferenceKey) at the very bottom so the test can read the borrowed
// key this copy cached. Every statement, operand order and comparison is untouched.
// ColdStartTierResolverEquivalenceTests compares the shipped KeyedScaleLearner against this oracle.
// =====================================================================================================
using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Tests.Frozen
{
    internal sealed class FrozenColdStartTierResolution
    {
        private readonly Dictionary<string, OnlineDistributionLearner> _physicalAnchor;

        public FrozenColdStartTierResolution(Dictionary<string, OnlineDistributionLearner> physicalAnchor)
        {
            _physicalAnchor = physicalAnchor;
        }

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
            sourceIdentity = parts.Length > 2 ? QAdvanceFeedback.Core.KeyRules.SourceKeys.FromKey(parts[2]) : string.Empty;   // v21 keys hold the hashed source
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
        public static bool ForceTier1ForTesting = false; // frozen copy: never set by any test

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
        internal ColdStartTier ResolveReference(string gameId, string carId, string sourceIdentity, string currentSurfaceBucket, string ownKey, bool lockToCache, out double referenceCeiling)
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

        private static OnlineDistributionLearner Find(Dictionary<string, OnlineDistributionLearner> store, string key)
            => store.TryGetValue(key, out OnlineDistributionLearner learner) ? learner : null;

        // ---- ADDITIVE TEST ACCESSOR (not part of the verbatim copy) ----
        internal string TryGetCachedReferenceKey(string ownKey, string currentSurfaceBucket)
        {
            string cacheKey = ownKey + "|surf#" + (currentSurfaceBucket ?? string.Empty);
            return _resolvedReferenceKeyCache.TryGetValue(cacheKey, out string key) ? key : null;
        }
    }
}
