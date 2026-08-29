namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md). On a cold key
    /// (this exact (gameId, carId, sourceIdentity[, surfaceBucket]) combination has little or no evidence
    /// of its own yet), <see cref="KeyedScaleLearner.ResolveTier"/> looks for an EXISTING persisted
    /// parameter set with the SAME input source to borrow as a reference, selecting the MOST SPECIFIC
    /// match available - four tiers, in increasing order of specificity/trust:
    /// <list type="bullet">
    /// <item><see cref="Tier1"/> - no reference with the same input source exists anywhere. Behaviour is
    /// UNCHANGED from pre-1.0.7: a linear 1:1 (identity) mapping of the source value. THIS is the one
    /// tier where "the cold state before any local evidence must be identity" (the old, project-wide
    /// hard rule) still applies - see <see cref="KeyedScaleLearner"/>'s own remarks for why it is
    /// DELIBERATELY relaxed for the other three.</item>
    /// <item><see cref="Tier2"/> - same source, a DIFFERENT game.</item>
    /// <item><see cref="Tier3"/> - same source AND same game, a DIFFERENT car.</item>
    /// <item><see cref="Tier4"/> - same source, same game AND same car, a DIFFERENT surface.</item>
    /// </list>
    /// The numeric values are deliberately ordinal (Tier4 &gt; Tier3 &gt; Tier2 &gt; Tier1) so a plain
    /// integer comparison already expresses "more specific than" - <see cref="KeyedScaleLearner"/>'s own
    /// resolver relies on this directly when preferring the most specific tier with at least one
    /// candidate.
    /// </summary>
    public enum ColdStartTier
    {
        Tier1 = 1,
        Tier2 = 2,
        Tier3 = 3,
        Tier4 = 4,
    }

    /// <summary>
    /// The Layer 5 <see cref="Projection.ColdStartScale"/> Stage-1 floor for each
    /// <see cref="ColdStartTier"/> - see that class's own remarks for the full derivation of why every
    /// one of these four values is safely above the hard 0.5 mathematical floor
    /// (<see cref="Projection.ColdStartScale.MinSafeFloor"/>).
    /// </summary>
    public static class ColdStartTierFloors
    {
        public const double Tier1Floor = 0.50;
        public const double Tier2Floor = 0.60;
        public const double Tier3Floor = 0.70;
        public const double Tier4Floor = 0.80;

        /// <summary>The Projected-layer Stage-1 floor for <paramref name="tier"/> - see this class's own
        /// remarks. An unrecognised value defensively falls back to <see cref="Tier1Floor"/> (the safest,
        /// most conservative choice), never higher.</summary>
        public static double FloorFor(ColdStartTier tier)
        {
            switch (tier)
            {
                case ColdStartTier.Tier2: return Tier2Floor;
                case ColdStartTier.Tier3: return Tier3Floor;
                case ColdStartTier.Tier4: return Tier4Floor;
                default: return Tier1Floor;
            }
        }
    }
}
