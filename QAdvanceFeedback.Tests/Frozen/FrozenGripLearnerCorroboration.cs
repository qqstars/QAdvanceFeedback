// =====================================================================================================
// FROZEN TEST ORACLE - DO NOT EDIT, EVER.
//
// A verbatim copy of GripLearner's evidence-weighted peak estimator (AdaptivePeakState,
// RaiseConfidenceForHits, LowerConfidenceForHits, ApplyEvidenceWeightedObservation and every constant
// they use) exactly as it stood in QAdvanceFeedback\Core\Normalized\GripLearner.cs immediately BEFORE
// it was extracted into the shared CorroboratedLevelEstimator (slip-angle plan, Task 6). Only the type
// names were changed (Frozen* prefix), the members made internal, and doc-comment crefs that pointed at
// GripLearner members turned into plain text; every statement, operand order and constant value is
// untouched, so the floating-point results are bit-identical to the pre-extraction code.
// CorroboratedLevelEstimatorEquivalenceTests compares the shared estimator against this oracle with
// exact == on every field. If the shared estimator's behaviour ever needs to change on purpose, that is
// a NEW behaviour and needs its own tests - this file stays as the record of the old one.
// =====================================================================================================
using System;
using QAdvanceFeedback.Core;

namespace QAdvanceFeedback.Tests.Frozen
{
    internal static class FrozenGripLearnerCorroboration
    {
        // ---- Caller-side filter, copied from GripLearner.Observe's first two statements (pre-extraction):
        //   if (!ClampMath.IsFinite(magnitudeG) || magnitudeG <= 0.0 || magnitudeG > LearnCapG) return;
        //   ApplyEvidenceWeightedObservation(ref _flatPeak, magnitudeG);
        internal static void FrozenObserve(ref FrozenAdaptivePeakState state, double magnitudeG, double learnCapG)
        {
            if (!ClampMath.IsFinite(magnitudeG) || magnitudeG <= 0.0 || magnitudeG > learnCapG) return;

            ApplyEvidenceWeightedObservation(ref state, magnitudeG);
        }

        // ---- Verbatim constants (values copied from GripLearner.cs, pre-extraction) ----
        internal const double MaxPlausibleG = 8.0;
        internal const double MinPeakFloor = 0.1;
        internal const double AdaptiveConfidenceBase = 0.10;
        internal const double ToleranceFraction = 0.30;
        internal const double LowerToleranceFraction = 0.15;
        internal const double ToleranceFloorG = 0.05;
        internal const int RaiseCooldownQualifyingSamples = 3;
        internal const int LowerCooldownQualifyingSamples = 40;
        internal const double MaxPlausibleJerkG = 3.0;
        internal const int SampleCountSaturationCap = 1_000_000;
        internal const double StabilityMoveResetFraction = 0.01;

        // ---- Verbatim code (GripLearner.cs lines 314-448, pre-extraction; renamed only) ----
        /// <summary>
        /// One channel's (flat-scalar OR one speed bucket's) own evidence-weighted peak-estimator state -
        /// see this class's own remarks above AdaptiveConfidenceBase for the full mechanism.
        /// A plain mutable struct (not a class) so both _flatPeak and each
        /// SpeedBucket's own SpeedBucket.Peak can hold one inline, with no
        /// extra allocation, mirroring how SpeedBucket itself is already a plain struct.
        /// </summary>
        internal struct FrozenAdaptivePeakState
        {
            public double Level;

            public double RaiseCandidateG;
            public int RaiseHits;

            public double LowerCandidateG;
            public int LowerHits;

            /// <summary>Qualifying-call countdown before the NEXT hit may be credited in either
            /// direction - see this class's own "SEPARATELY-ARRIVING" remarks above.</summary>
            public int Cooldown;

            public double LastRawG;
            public bool HasLastRaw;

            /// <summary>STABILITY-GATED MATURITY (see the remarks above StabilityWeight) -
            /// consecutive qualifying observations since Level last moved meaningfully,
            /// reduced (never below 0) proportionally to how big the most recent move was, rather than
            /// hard-reset to exactly 0 - continuous, no step. Starts at 0 (maximally unstable/no
            /// evidence yet) for a brand-new state, matching this whole class's own "identity/no trust
            /// until evidence says otherwise" convention.</summary>
            public double QuietStreak;

            public static FrozenAdaptivePeakState Seeded(double level) => new FrozenAdaptivePeakState { Level = level };
        }

        /// <summary>0..1 confidence for the Nth RAISING hit - <c>min(1, base * 2^(hits-1))</c>: 10%, 20%,
        /// 40%, 80%, then capped at 100% from the 5th corroborating sighting onward. See this class's own
        /// remarks for the worked example this reproduces.</summary>
        internal static double RaiseConfidenceForHits(int hits)
        {
            if (hits <= 0) return 0.0;
            double confidence = AdaptiveConfidenceBase * Math.Pow(2.0, hits - 1);
            return confidence >= 1.0 ? 1.0 : confidence;
        }

        /// <summary>0..1 confidence for the Nth LOWERING hit - deliberately ONE SIGHTING MORE CONSERVATIVE
        /// than RaiseConfidenceForHits: the very first sighting of a new, lower level
        /// contributes ZERO confidence (a candidate is recorded, but nothing moves) - only the SECOND,
        /// separately-arriving corroborating sighting starts to move the estimate, then on the identical
        /// doubling schedule from there (10%, 20%, 40%, 80%, 100%). This is the load-bearing half of the
        /// ramp-down discriminator: an isolated single low reading (a driver momentarily not pushing as
        /// hard) can never, by itself, move the estimate down at all - only a REPEATED, consistent
        /// shortfall can, exactly implementing "tried and could not" (repeats) versus "did not try" (an
        /// isolated one-off that is never corroborated again before something else supersedes it).</summary>
        internal static double LowerConfidenceForHits(int hits) => hits < 2 ? 0.0 : RaiseConfidenceForHits(hits - 1);

        /// <summary>
        /// Applies one qualifying observation to <paramref name="state"/> - the shared mechanism behind
        /// BOTH _flatPeak and every SpeedBucket.Peak. See this class's own
        /// remarks above AdaptiveConfidenceBase for the full derivation.
        /// </summary>
        internal static void ApplyEvidenceWeightedObservation(ref FrozenAdaptivePeakState state, double magnitudeG)
        {
            // DATA QUALITY GATE: an implausible frame-to-frame jump (collision/telemetry-glitch
            // signature) earns no confidence at all this frame - excluded before it can ever start or
            // corroborate any candidate, in either direction.
            if (state.HasLastRaw && Math.Abs(magnitudeG - state.LastRawG) > MaxPlausibleJerkG)
                return;

            state.LastRawG = magnitudeG;
            state.HasLastRaw = true;

            if (state.Cooldown > 0)
            {
                state.Cooldown--;
                // STABILITY (see the remarks above StabilityWeight): a cooldown call is, by
                // construction, a call where Level cannot possibly move this frame - count it as one
                // more "quiet" observation toward the settling streak.
                state.QuietStreak += 1.0;
                return;
            }

            double levelBeforeThisObservation = state.Level;

            if (magnitudeG > state.Level)
            {
                double bandReference = state.RaiseHits > 0 ? state.RaiseCandidateG : magnitudeG;
                double band = Math.Max(ToleranceFraction * bandReference, ToleranceFloorG);
                bool matches = state.RaiseHits > 0 && Math.Abs(magnitudeG - state.RaiseCandidateG) <= band;

                // INT32 OVERFLOW GUARD (docs\release-1060-report.md, Part 5 overflow audit) - saturate
                // rather than let this wrap negative after enough cumulative real-world corroborating
                // hits. Harmless functionally (RaiseConfidenceForHits already clamps to 1.0 by the 5th
                // hit, so hits beyond that change nothing), but the counter itself must still not
                // overflow over a genuinely multi-year, never-restarted session.
                state.RaiseHits = matches ? (state.RaiseHits < SampleCountSaturationCap ? state.RaiseHits + 1 : state.RaiseHits) : 1;
                state.RaiseCandidateG = magnitudeG;
                // Fresh, confirmed-higher evidence supersedes whatever lower candidate was in progress -
                // it is no longer informative about where the CURRENT level actually sits.
                state.LowerHits = 0;

                double confidence = RaiseConfidenceForHits(state.RaiseHits);
                state.Level += confidence * (magnitudeG - state.Level);
                state.Cooldown = RaiseCooldownQualifyingSamples;
            }
            else if (magnitudeG < state.Level)
            {
                double bandReference = state.LowerHits > 0 ? state.LowerCandidateG : magnitudeG;
                double band = Math.Max(LowerToleranceFraction * bandReference, ToleranceFloorG);
                bool matches = state.LowerHits > 0 && Math.Abs(magnitudeG - state.LowerCandidateG) <= band;

                // INT32 OVERFLOW GUARD - see the RaiseHits guard's own remarks just above; identical
                // reasoning applies to the lowering side.
                state.LowerHits = matches ? (state.LowerHits < SampleCountSaturationCap ? state.LowerHits + 1 : state.LowerHits) : 1;
                state.LowerCandidateG = magnitudeG;

                double confidence = LowerConfidenceForHits(state.LowerHits);
                if (confidence > 0.0)
                {
                    state.Level += confidence * (magnitudeG - state.Level);
                    state.Cooldown = LowerCooldownQualifyingSamples;
                }
                // else: an uncorroborated first sighting of a new lower level - recorded as a candidate
                // for a FUTURE corroborating sighting to confirm, but moves nothing yet, and spends no
                // cooldown (so it does not delay a genuinely corroborating sighting arriving right
                // after it).
            }

            // STABILITY (see the remarks above StabilityWeight): how much THIS call actually moved
            // Level, as a fraction of where it started - a proportional, continuous reduction of the
            // quiet streak (never a hard reset to a fixed value), so a tiny nudge barely dents
            // settledness while a large revision resets it close to zero. A call that moved nothing
            // (magnitude equalled Level, or a candidate was recorded with zero confidence) simply adds
            // to the streak with no reduction at all.
            double relativeMove = Math.Abs(state.Level - levelBeforeThisObservation)
                / Math.Max(levelBeforeThisObservation, MinPeakFloor);
            if (relativeMove > 0.0)
                state.QuietStreak *= ClampMath.Clamp(1.0 - relativeMove / StabilityMoveResetFraction, 0.0, 1.0);
            state.QuietStreak += 1.0;

            if (state.Level < MinPeakFloor) state.Level = MinPeakFloor;
        }
    }
}
