using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// THE BORROWING-TIER RESOLVER (v1.0.7 tiered cold start, docs\v107-tiered-coldstart-report.md),
    /// extracted from <see cref="KeyedScaleLearner"/> so the Slip Angle learner can reuse the exact same
    /// selection (docs\slip-angle-design.md sections 4 and 9.5.7). Behaviour-preserving: proven
    /// bit-identical against a frozen copy of the pre-extraction code by
    /// <c>ColdStartTierResolverEquivalenceTests</c>.
    /// <para/>
    /// Given the key that is COLD (gameId, carId, sourceIdentity, currentSurfaceBucket), scans every
    /// OTHER known key that has evidence and the SAME source identity, and returns the most SPECIFIC
    /// tier with at least one candidate: <see cref="ColdStartTier.Tier4"/> (same game+car, different
    /// surface) over <see cref="ColdStartTier.Tier3"/> (same game, different car) over
    /// <see cref="ColdStartTier.Tier2"/> (different game) over <see cref="ColdStartTier.Tier1"/>
    /// (nothing). Within a tier the candidate with the MOST SAMPLES wins; a tie goes to the
    /// lexicographically (ordinal) smallest key. Tier 4 is only ever considered when the caller gives a
    /// real surface hint AND that exact surface has no evidence of its own yet (see
    /// <see cref="KeyedScaleLearner"/>'s remarks on why: a surface-tagged sibling of the same
    /// game/car/source cannot otherwise be told apart from the key's own additive alias).
    /// <para/>
    /// STATELESS. Caching (the write-once <c>lockToCache</c> latch and the per-surface-hint cache slot)
    /// is the CALLER's policy and stays in <see cref="KeyedScaleLearner"/>.
    /// <para/>
    /// WHY THE SIGNATURE CARRIES A REFERENCE LOOKUP, not only <c>hasEvidence</c>: choosing between two
    /// candidates of the same tier needs each candidate's sample count, and the caller needs the chosen
    /// candidate's reference ceiling - neither is a bool. <paramref name="ownKey"/> is passed explicitly
    /// because the learner's own key is its DEFAULT (unsurfaced) storage key, not one built from the
    /// surface hint (building it from the hint would make the key's own surface alias look like a
    /// Tier-4 candidate for itself).
    /// <para/>
    /// KEY SHAPE: <c>game|#|car|#|source|#|surface</c> (<see cref="Key"/>, identical to
    /// <see cref="KeyedGripLearner.MakeKey"/>). A caller with an extra dimension that must also match
    /// for a borrow to be legitimate (the Slip Angle learner's slip-angle target, for example) folds it
    /// into the source segment (any separator other than <c>|#|</c>), so "same source" means "same
    /// source and same target".
    /// </summary>
    public sealed class ColdStartTierResolver
    {
        /// <summary>A candidate's usable reference: false when the key has no usable value (for the
        /// learner: no decayed average yet), in which case it is skipped.</summary>
        public delegate bool TryGetReference(string key, out int samples, out double ceiling);

        /// <summary>The composite key - identical to <see cref="KeyedGripLearner.MakeKey"/>.</summary>
        public static string Key(string game, string car, string source, string surface = "")
            => KeyedGripLearner.MakeKey(game, car, source, surface);

        /// <summary>Splits a 4-part composite key into its segments (missing segments come back empty);
        /// false when fewer than three segments are present - such a key is never a candidate.</summary>
        public static bool TryParseKey(string key, out string gameId, out string carId, out string sourceIdentity, out string surfaceBucket)
        {
            string[] parts = (key ?? string.Empty).Split(new[] { "|#|" }, StringSplitOptions.None);
            gameId = parts.Length > 0 ? parts[0] : string.Empty;
            carId = parts.Length > 1 ? parts[1] : string.Empty;
            sourceIdentity = parts.Length > 2 ? parts[2] : string.Empty;
            surfaceBucket = parts.Length > 3 ? parts[3] : string.Empty;
            return parts.Length >= 3;
        }

        /// <param name="gameId">The cold key's game.</param>
        /// <param name="carId">The cold key's car.</param>
        /// <param name="sourceIdentity">The cold key's source - candidates must match it exactly.</param>
        /// <param name="currentSurfaceBucket">The surface hint; null/empty never yields Tier 4.</param>
        /// <param name="ownKey">The cold key's own storage key - never its own candidate.</param>
        /// <param name="hasEvidence">True when a key has evidence of its own (false for unknown keys).
        /// Also asked of (game, car, source, currentSurfaceBucket) for the Tier-4 guard.</param>
        /// <param name="knownKeys">Every candidate key, enumerated ONCE in the caller's order (order
        /// matters only for keys that tie on samples AND compare equal, which distinct keys never do).</param>
        /// <param name="tryGetReference">The candidate's sample count and reference ceiling.</param>
        /// <param name="borrowedKey">The chosen candidate's key; null for Tier 1.</param>
        /// <param name="referenceCeiling">The chosen candidate's ceiling; 0 for Tier 1.</param>
        public ColdStartTier Resolve(string gameId, string carId, string sourceIdentity, string currentSurfaceBucket,
            string ownKey, Func<string, bool> hasEvidence, IEnumerable<string> knownKeys, TryGetReference tryGetReference,
            out string borrowedKey, out double referenceCeiling)
        {
            ColdStartTier bestTier = ColdStartTier.Tier1;
            string bestKey = null;
            int bestSamples = -1;
            double bestCeiling = 0.0;

            // TIER 4 GUARD: if THIS EXACT surface already has evidence of its own for (game,car,source),
            // it is not cold for Tier 4's purpose - a different-surface candidate is never offered.
            bool currentSurfaceAlreadyHasOwnEvidence = !string.IsNullOrEmpty(currentSurfaceBucket)
                && hasEvidence(Key(gameId, carId, sourceIdentity, currentSurfaceBucket));

            foreach (string key in knownKeys)
            {
                if (string.Equals(key, ownKey, StringComparison.Ordinal)) continue;
                if (!hasEvidence(key)) continue;
                if (!TryParseKey(key, out string g, out string c, out string s, out string surf)) continue;
                // SAME INPUT SOURCE ONLY - a Raw reference must never be used for a ShakeIt key or vice versa.
                if (!string.Equals(s, sourceIdentity ?? string.Empty, StringComparison.Ordinal)) continue;

                ColdStartTier candidateTier;
                if (!string.Equals(g, gameId ?? string.Empty, StringComparison.Ordinal)) candidateTier = ColdStartTier.Tier2;
                else if (!string.Equals(c, carId ?? string.Empty, StringComparison.Ordinal)) candidateTier = ColdStartTier.Tier3;
                else if (!string.IsNullOrEmpty(currentSurfaceBucket) && !currentSurfaceAlreadyHasOwnEvidence
                    && !string.Equals(surf, currentSurfaceBucket, StringComparison.Ordinal))
                {
                    candidateTier = ColdStartTier.Tier4;
                }
                else continue; // identical in every dimension (or no real surface hint to compare) - not a candidate.

                if (candidateTier < bestTier) continue; // only the MOST specific tier with any candidate wins.

                if (!tryGetReference(key, out int samples, out double ceiling)) continue;

                bool better = candidateTier > bestTier
                    || samples > bestSamples
                    || (samples == bestSamples && bestKey != null && string.CompareOrdinal(key, bestKey) < 0);
                if (better)
                {
                    bestTier = candidateTier;
                    bestKey = key;
                    bestSamples = samples;
                    bestCeiling = ceiling;
                }
            }

            borrowedKey = bestKey;
            referenceCeiling = bestCeiling;
            return bestTier;
        }
    }
}
