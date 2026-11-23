using System;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Tuning for one <see cref="CorroboratedLevelEstimator"/> use. Unit-neutral names: the values are
    /// in whatever unit the observed quantity uses (g for <see cref="GripLearner"/>, degrees for a
    /// slip-angle maximum, ...). See <see cref="GripLearner"/>'s own "EVIDENCE-WEIGHTED ADAPTIVE PEAK
    /// ESTIMATOR" remarks (and docs\adaptive-peak-learner-report.md) for what every value means and why
    /// the shipped Lock/Slip values are what they are.
    /// </summary>
    public sealed class CorroborationParameters
    {
        /// <summary>Observations above this (or non-finite, or non-positive) are rejected outright,
        /// before any bookkeeping. A caller that already rejects with its own cap must set this to
        /// THAT cap (see <see cref="GripLearner.LearnCapG"/>) so the two can never disagree.</summary>
        public double PlausibleCap;

        /// <summary>A jump larger than this from the previous accepted observation is treated as a
        /// collision/glitch signature and ignored entirely.</summary>
        public double MaxPlausibleJump;

        /// <summary>Raise-side corroboration band, as a fraction of the candidate being corroborated.</summary>
        public double RaiseTolerance;

        /// <summary>Lower-side corroboration band, as a fraction of the candidate being corroborated.</summary>
        public double LowerTolerance;

        /// <summary>Absolute minimum width of either band.</summary>
        public double ToleranceFloor;

        /// <summary>Observations to skip after a raise moves the level.</summary>
        public int RaiseCooldown;

        /// <summary>Observations to skip after a lower moves the level.</summary>
        public int LowerCooldown;

        /// <summary>Confidence of the first raising hit (doubling per further hit, capped at 1).</summary>
        public double ConfidenceBase;

        /// <summary>Hit counters saturate here instead of overflowing.</summary>
        public int HitSaturationCap;

        /// <summary>A relative level move of this fraction (or more) resets the quiet streak to 0;
        /// smaller moves reduce it proportionally.</summary>
        public double StabilityMoveResetFraction;

        /// <summary>The level never falls below this; also the minimum denominator of the relative
        /// move used for the quiet-streak update.</summary>
        public double LevelFloor;

        /// <summary>
        /// The exact pre-extraction <see cref="GripLearner"/> constants: cap 8, jump 3, tolerance
        /// 0.30 / 0.15, floor 0.05, cooldown 3 / 40, confidence base 0.10, hit cap 1,000,000, stability
        /// reset 0.01, level floor 0.1 - read straight from <see cref="GripLearner"/>'s own constants (the
        /// single source of truth, with their measured justifications). Returns a NEW instance on every read, so a caller adjusting a
        /// copy (e.g. <see cref="PlausibleCap"/> for a channel-specific learn cap) can never alter
        /// anyone else's defaults.
        /// </summary>
        public static CorroborationParameters GripLearnerDefaults => new CorroborationParameters
        {
            PlausibleCap = GripLearner.MaxPlausibleG,
            MaxPlausibleJump = GripLearner.MaxPlausibleJerkG,
            RaiseTolerance = GripLearner.ToleranceFraction,
            LowerTolerance = GripLearner.LowerToleranceFraction,
            ToleranceFloor = GripLearner.ToleranceFloorG,
            RaiseCooldown = GripLearner.RaiseCooldownQualifyingSamples,
            LowerCooldown = GripLearner.LowerCooldownQualifyingSamples,
            ConfidenceBase = GripLearner.AdaptiveConfidenceBase,
            HitSaturationCap = GripLearner.SampleCountSaturationCap,
            StabilityMoveResetFraction = GripLearner.StabilityMoveResetFraction,
            LevelFloor = GripLearner.MinPeakFloor,
        };

        /// <summary>A field-for-field copy.</summary>
        public CorroborationParameters Clone() => (CorroborationParameters)MemberwiseClone();
    }

    /// <summary>
    /// One evidence-weighted level estimate's mutable state - a plain struct so an owner can hold it
    /// inline with no allocation. Formerly <c>GripLearner.AdaptivePeakState</c>.
    /// </summary>
    public struct CorroboratedLevelState
    {
        public double Level;

        public double RaiseCandidate;
        public int RaiseHits;

        public double LowerCandidate;
        public int LowerHits;

        /// <summary>Observation countdown before the NEXT hit may be credited in either direction.</summary>
        public int Cooldown;

        public double LastRaw;
        public bool HasLastRaw;

        /// <summary>Consecutive observations since <see cref="Level"/> last moved meaningfully,
        /// reduced (never below 0) proportionally to how big the most recent move was. Starts at 0.</summary>
        public double QuietStreak;

        public static CorroboratedLevelState Seeded(double level) => new CorroboratedLevelState { Level = level };
    }

    /// <summary>
    /// The shared evidence-weighted ("corroborated") level estimator, extracted verbatim from
    /// <see cref="GripLearner"/> (slip-angle plan, Task 6; docs\slip-angle-design.md section 9.5b). A new
    /// observation moves the level by CONFIDENCE * (observation - level), where confidence grows with
    /// repeated, separately-arriving observations near the SAME new level; lowering needs one more
    /// sighting than raising. See <see cref="GripLearner"/>'s own remarks for the full derivation.
    /// <para/>
    /// BIT-IDENTICAL to the pre-extraction code with <see cref="CorroborationParameters.GripLearnerDefaults"/>
    /// - pinned by <c>CorroboratedLevelEstimatorEquivalenceTests</c> against a frozen verbatim copy.
    /// Do not reorder or "simplify" any arithmetic here.
    /// </summary>
    public static class CorroboratedLevelEstimator
    {
        /// <summary>0..1 confidence for the Nth RAISING hit - <c>min(1, base * 2^(hits-1))</c>.</summary>
        public static double RaiseConfidenceForHits(int hits, CorroborationParameters p)
        {
            if (hits <= 0) return 0.0;
            double confidence = p.ConfidenceBase * Math.Pow(2.0, hits - 1);
            return confidence >= 1.0 ? 1.0 : confidence;
        }

        /// <summary>0..1 confidence for the Nth LOWERING hit - one sighting more conservative than
        /// <see cref="RaiseConfidenceForHits"/>: the first sighting contributes zero.</summary>
        public static double LowerConfidenceForHits(int hits, CorroborationParameters p)
            => hits < 2 ? 0.0 : RaiseConfidenceForHits(hits - 1, p);

        /// <summary>Applies one observation to <paramref name="s"/>. Non-finite, non-positive and
        /// above-<see cref="CorroborationParameters.PlausibleCap"/> values are ignored entirely.</summary>
        public static void Observe(ref CorroboratedLevelState s, double value, CorroborationParameters p)
        {
            if (!ClampMath.IsFinite(value) || value <= 0.0 || value > p.PlausibleCap) return;

            // DATA QUALITY GATE: an implausible jump earns no confidence at all this observation.
            if (s.HasLastRaw && Math.Abs(value - s.LastRaw) > p.MaxPlausibleJump)
                return;

            s.LastRaw = value;
            s.HasLastRaw = true;

            if (s.Cooldown > 0)
            {
                s.Cooldown--;
                // A cooldown call cannot move Level - one more "quiet" observation.
                s.QuietStreak += 1.0;
                return;
            }

            double levelBeforeThisObservation = s.Level;

            if (value > s.Level)
            {
                double bandReference = s.RaiseHits > 0 ? s.RaiseCandidate : value;
                double band = Math.Max(p.RaiseTolerance * bandReference, p.ToleranceFloor);
                bool matches = s.RaiseHits > 0 && Math.Abs(value - s.RaiseCandidate) <= band;

                // INT32 OVERFLOW GUARD - saturate rather than wrap.
                s.RaiseHits = matches ? (s.RaiseHits < p.HitSaturationCap ? s.RaiseHits + 1 : s.RaiseHits) : 1;
                s.RaiseCandidate = value;
                // Fresh higher evidence supersedes any lower candidate in progress.
                s.LowerHits = 0;

                double confidence = RaiseConfidenceForHits(s.RaiseHits, p);
                s.Level += confidence * (value - s.Level);
                s.Cooldown = p.RaiseCooldown;
            }
            else if (value < s.Level)
            {
                double bandReference = s.LowerHits > 0 ? s.LowerCandidate : value;
                double band = Math.Max(p.LowerTolerance * bandReference, p.ToleranceFloor);
                bool matches = s.LowerHits > 0 && Math.Abs(value - s.LowerCandidate) <= band;

                s.LowerHits = matches ? (s.LowerHits < p.HitSaturationCap ? s.LowerHits + 1 : s.LowerHits) : 1;
                s.LowerCandidate = value;

                double confidence = LowerConfidenceForHits(s.LowerHits, p);
                if (confidence > 0.0)
                {
                    s.Level += confidence * (value - s.Level);
                    s.Cooldown = p.LowerCooldown;
                }
                // else: an uncorroborated first sighting - recorded, moves nothing, spends no cooldown.
            }

            // STABILITY: reduce the quiet streak in proportion to how far THIS call moved Level.
            double relativeMove = Math.Abs(s.Level - levelBeforeThisObservation)
                / Math.Max(levelBeforeThisObservation, p.LevelFloor);
            if (relativeMove > 0.0)
                s.QuietStreak *= ClampMath.Clamp(1.0 - relativeMove / p.StabilityMoveResetFraction, 0.0, 1.0);
            s.QuietStreak += 1.0;

            if (s.Level < p.LevelFloor) s.Level = p.LevelFloor;
        }
    }
}
