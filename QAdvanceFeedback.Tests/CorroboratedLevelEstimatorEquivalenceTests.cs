using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Tests.Frozen;
using Xunit;
using Oracle = QAdvanceFeedback.Tests.Frozen.FrozenGripLearnerCorroboration;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// BEHAVIOUR-PRESERVATION PROOF for the <see cref="CorroboratedLevelEstimator"/> extraction
    /// (slip-angle plan, Task 6; docs\slip-angle-design.md section 9.5b): the shared estimator, driven with
    /// <see cref="CorroborationParameters.GripLearnerDefaults"/>, must be BIT-IDENTICAL (exact <c>==</c> on
    /// every double/int/bool field, after every single call) to the frozen verbatim copy of the
    /// pre-extraction <c>GripLearner</c> code in <see cref="FrozenGripLearnerCorroboration"/>, over
    /// randomised streams that exercise every branch: plateaus with noise, step ups/downs, single
    /// &gt;3 g spikes (jump gate), invalid readings (NaN/infinity/zero/negative/above-cap, filtered by
    /// the caller exactly as <c>GripLearner.Observe</c> does), exact repeats of the current level (the
    /// "neither raise nor lower" branch), sub-floor readings, and counters pre-set at the saturation
    /// cap.
    /// </summary>
    public class CorroboratedLevelEstimatorEquivalenceTests
    {
        private const int Seeds = 200;
        private const int ValuesPerSeed = 5000;

        [Fact]
        public void Defaults_match_the_pre_extraction_GripLearner_constants()
        {
            var p = CorroborationParameters.GripLearnerDefaults;
            Assert.Equal(Oracle.MaxPlausibleG, p.PlausibleCap);
            Assert.Equal(Oracle.MaxPlausibleJerkG, p.MaxPlausibleJump);
            Assert.Equal(Oracle.ToleranceFraction, p.RaiseTolerance);
            Assert.Equal(Oracle.LowerToleranceFraction, p.LowerTolerance);
            Assert.Equal(Oracle.ToleranceFloorG, p.ToleranceFloor);
            Assert.Equal(Oracle.RaiseCooldownQualifyingSamples, p.RaiseCooldown);
            Assert.Equal(Oracle.LowerCooldownQualifyingSamples, p.LowerCooldown);
            Assert.Equal(Oracle.AdaptiveConfidenceBase, p.ConfidenceBase);
            Assert.Equal(Oracle.SampleCountSaturationCap, p.HitSaturationCap);
            Assert.Equal(Oracle.StabilityMoveResetFraction, p.StabilityMoveResetFraction);
            Assert.Equal(Oracle.MinPeakFloor, p.LevelFloor);

            // GripLearner's own public constants are unchanged and agree with the shared defaults.
            Assert.Equal(GripLearner.MaxPlausibleG, p.PlausibleCap);
            Assert.Equal(GripLearner.RaiseCooldownQualifyingSamples, p.RaiseCooldown);
            Assert.Equal(GripLearner.LowerCooldownQualifyingSamples, p.LowerCooldown);
            Assert.Equal(GripLearner.SampleCountSaturationCap, p.HitSaturationCap);
        }

        [Fact]
        public void Confidence_schedules_are_bit_identical_to_frozen_copy()
        {
            var p = CorroborationParameters.GripLearnerDefaults;
            int[] hits = { int.MinValue, -5, -1, 0, 1, 2, 3, 4, 5, 6, 7, 10, 50, 1023, 1024, 1025, 999_999, 1_000_000, int.MaxValue };
            foreach (int h in hits)
            {
                Assert.True(Oracle.RaiseConfidenceForHits(h) == CorroboratedLevelEstimator.RaiseConfidenceForHits(h, p), $"raise hits={h}");
                Assert.True(Oracle.LowerConfidenceForHits(h) == CorroboratedLevelEstimator.LowerConfidenceForHits(h, p), $"lower hits={h}");
            }
        }

        [Fact]
        public void Bit_identical_to_frozen_copy_over_randomised_streams()
        {
            long compared = RunStreams(Oracle.MaxPlausibleG, CorroborationParameters.GripLearnerDefaults, Seeds, seedOffset: 0);
            Assert.Equal((long)Seeds * ValuesPerSeed, compared);
        }

        /// <summary>A channel-specific <see cref="GripLearner"/> uses a non-default learn cap (both
        /// tighter AND looser than 8 g are possible through the constructor) - the estimator's own
        /// <see cref="CorroborationParameters.PlausibleCap"/> must then mirror that instance's cap so it
        /// never rejects anything the caller admits (or admits anything it rejects).</summary>
        [Theory]
        [InlineData(4.5)]
        [InlineData(6.0)]
        [InlineData(12.0)]
        public void Bit_identical_to_frozen_copy_with_a_non_default_plausible_cap(double cap)
        {
            var p = CorroborationParameters.GripLearnerDefaults;
            p.PlausibleCap = cap;
            long compared = RunStreams(cap, p, Seeds / 4, seedOffset: 10_000 + (int)(cap * 100));
            Assert.Equal((long)(Seeds / 4) * ValuesPerSeed, compared);
        }

        /// <summary>End-to-end wiring check: a real <see cref="GripLearner"/> (default AND non-default
        /// learn cap) fed the same stream must expose exactly the frozen oracle's level / candidate /
        /// hit / quiet-streak values after every call.</summary>
        [Theory]
        [InlineData(GripLearner.MaxPlausibleG)]
        [InlineData(4.5)]
        [InlineData(12.0)]
        public void GripLearner_public_state_is_bit_identical_to_frozen_copy(double cap)
        {
            for (int seed = 0; seed < 40; seed++)
            {
                var rng = new Random(50_000 + seed);
                var learner = new GripLearner(cap);
                var frozen = Oracle.FrozenAdaptivePeakState.Seeded(GripLearner.SeedPeakG);
                foreach (double v in Stream(rng, ValuesPerSeed, () => frozen.Level, cap))
                {
                    learner.Observe(v);
                    Oracle.FrozenObserve(ref frozen, v, cap);
                    Assert.True(frozen.Level == learner.LearnedPeakG, $"seed {seed} level");
                    Assert.True(frozen.RaiseCandidateG == learner.RaiseCandidateG, $"seed {seed} raise candidate");
                    Assert.True(frozen.RaiseHits == learner.RaiseCandidateHits, $"seed {seed} raise hits");
                    Assert.True(frozen.LowerCandidateG == learner.LowerCandidateG, $"seed {seed} lower candidate");
                    Assert.True(frozen.LowerHits == learner.LowerCandidateHits, $"seed {seed} lower hits");
                    Assert.True(frozen.QuietStreak == learner.QuietStreak, $"seed {seed} quiet streak");
                }
            }
        }

        private static long RunStreams(double cap, CorroborationParameters p, int seeds, int seedOffset)
        {
            long compared = 0;
            for (int seed = 0; seed < seeds; seed++)
            {
                var rng = new Random(seedOffset + seed);
                double seedLevel = 0.2 + rng.NextDouble() * 3.0;
                var frozen = Oracle.FrozenAdaptivePeakState.Seeded(seedLevel);
                var shared = CorroboratedLevelState.Seeded(seedLevel);

                // A quarter of the seeds start with counters already at (or one below) the saturation
                // cap, so the "stop incrementing" guard is exercised on both sides.
                if (seed % 4 == 1)
                {
                    int hits = rng.Next(2) == 0 ? 999_999 : 1_000_000;
                    frozen.RaiseHits = shared.RaiseHits = hits;
                    frozen.RaiseCandidateG = shared.RaiseCandidate = seedLevel * 1.1;
                    frozen.LowerHits = shared.LowerHits = hits;
                    frozen.LowerCandidateG = shared.LowerCandidate = seedLevel * 0.95;
                }

                AssertSame(frozen, shared, seed, -1);
                int i = 0;
                foreach (double v in Stream(rng, ValuesPerSeed, () => frozen.Level, cap))
                {
                    Oracle.FrozenObserve(ref frozen, v, cap);
                    CorroboratedLevelEstimator.Observe(ref shared, v, p);
                    AssertSame(frozen, shared, seed, i);
                    i++;
                    compared++;
                }
            }
            return compared;
        }

        private static void AssertSame(Oracle.FrozenAdaptivePeakState f, CorroboratedLevelState s, int seed, int i)
        {
            if (f.Level == s.Level && f.RaiseCandidateG == s.RaiseCandidate && f.RaiseHits == s.RaiseHits
                && f.LowerCandidateG == s.LowerCandidate && f.LowerHits == s.LowerHits && f.Cooldown == s.Cooldown
                && f.LastRawG == s.LastRaw && f.HasLastRaw == s.HasLastRaw && f.QuietStreak == s.QuietStreak)
                return;
            Assert.Fail($"seed {seed} step {i}: frozen(L={f.Level:R} rc={f.RaiseCandidateG:R} rh={f.RaiseHits} lc={f.LowerCandidateG:R} lh={f.LowerHits} cd={f.Cooldown} last={f.LastRawG:R}/{f.HasLastRaw} q={f.QuietStreak:R}) " +
                        $"shared(L={s.Level:R} rc={s.RaiseCandidate:R} rh={s.RaiseHits} lc={s.LowerCandidate:R} lh={s.LowerHits} cd={s.Cooldown} last={s.LastRaw:R}/{s.HasLastRaw} q={s.QuietStreak:R})");
        }

        /// <summary>Randomised segment-based stream. <paramref name="currentLevel"/> lets the generator
        /// occasionally emit EXACTLY the estimator's current level (the equality branch).</summary>
        private static IEnumerable<double> Stream(Random rng, int count, Func<double> currentLevel, double cap)
        {
            double plateau = 0.3 + rng.NextDouble() * 2.5;
            double last = plateau;
            int produced = 0;
            while (produced < count)
            {
                int kind = rng.Next(100);
                int length = 1 + rng.Next(60);
                for (int k = 0; k < length && produced < count; k++, produced++)
                {
                    double v;
                    if (kind < 45)
                    {
                        // Plateau with +-10% noise.
                        v = plateau * (1.0 + (rng.NextDouble() * 0.2 - 0.1));
                    }
                    else if (kind < 60)
                    {
                        // Step up / down to a new plateau, then hold it with small noise.
                        if (k == 0) plateau = Math.Max(0.05, plateau * (0.5 + rng.NextDouble() * 1.2));
                        v = plateau * (1.0 + (rng.NextDouble() * 0.04 - 0.02));
                    }
                    else if (kind < 68)
                    {
                        // Single spike more than 3 g above (or below) the previous reading, then plateau.
                        v = k == 0 ? last + 3.0 + rng.NextDouble() * 2.0 : plateau * (1.0 + (rng.NextDouble() * 0.2 - 0.1));
                    }
                    else if (kind < 74)
                    {
                        // Invalid readings the caller must filter.
                        switch (rng.Next(7))
                        {
                            case 0: v = double.NaN; break;
                            case 1: v = double.PositiveInfinity; break;
                            case 2: v = double.NegativeInfinity; break;
                            case 3: v = -rng.NextDouble() * 3.0; break;
                            case 4: v = 0.0; break;
                            case 5: v = cap + rng.NextDouble() * 5.0 + 1e-9; break;
                            default: v = cap; break; // exactly at the cap: admitted
                        }
                    }
                    else if (kind < 80)
                    {
                        // Exactly the current level (neither raise nor lower), or the jump-gate boundary.
                        v = rng.Next(3) == 0 ? last + 3.0 : currentLevel();
                    }
                    else if (kind < 86)
                    {
                        // Sub-floor / very low readings (level floor + tolerance floor).
                        v = 0.001 + rng.NextDouble() * 0.15;
                    }
                    else if (kind < 92)
                    {
                        // Exact repeats of one value (tight corroboration, cooldown cycling).
                        v = plateau;
                    }
                    else
                    {
                        // Wide uniform scatter across the whole admitted range.
                        v = rng.NextDouble() * cap;
                    }

                    if (ClampMath_IsFiniteAndPositive(v)) last = v;
                    yield return v;
                }
            }
        }

        private static bool ClampMath_IsFiniteAndPositive(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v > 0.0;
    }
}
