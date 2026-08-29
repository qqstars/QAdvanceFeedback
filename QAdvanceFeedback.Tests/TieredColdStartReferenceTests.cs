using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Unit-level acceptance tests for the v1.0.7 tiered cold-start reference system
    /// (docs\v107-tiered-coldstart-report.md) - each of the four tiers resolves correctly given a
    /// synthetic persisted store, the most-samples candidate is chosen (with a stated tie-break), a Raw
    /// store is never borrowed for a ShakeIt key, Tier 1 behaves exactly as pre-1.0.7, each tier's
    /// Projected floor is 50/60/70/80%, the ramp still reaches exactly 100%, and a warm key with its own
    /// parameters is unaffected.
    /// </summary>
    public class TieredColdStartReferenceTests
    {
        private const string GameA = "GameA";
        private const string GameB = "GameB";
        private const string ShakeIt = "ShakeIt";
        private const string Raw = "Raw";

        // ------------------------------------------------------------------------------------
        // TIER RESOLUTION - one of each tier, from a synthetic persisted store.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Tier1_resolves_when_no_reference_with_the_same_source_exists_anywhere()
        {
            var learner = new KeyedScaleLearner();
            // A reference exists, but under a DIFFERENT source entirely - never a candidate for ShakeIt.
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarA", Raw, 60.0);

            Assert.Equal(ColdStartTier.Tier1, learner.ResolveTier(GameA, "CarZ", ShakeIt));
        }

        [Fact]
        public void Tier1_identity_is_bit_identical_to_pre_1_0_7_for_a_genuinely_cold_reference_less_key()
        {
            var learner = new KeyedScaleLearner();
            // Nothing taught anywhere for any source - a genuinely fresh instance.
            double output = learner.Rescale(GameA, "CarZ", ShakeIt, 42.5);
            Assert.Equal(42.5, output, 9); // exact, not approximate - the pre-1.0.7 identity contract.
        }

        [Fact]
        public void Tier2_resolves_for_the_same_source_in_a_different_game()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 60.0);

            Assert.Equal(ColdStartTier.Tier2, learner.ResolveTier(GameB, "CarB", ShakeIt));
        }

        [Fact]
        public void Tier3_resolves_for_the_same_source_and_game_with_a_different_car()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 60.0);

            Assert.Equal(ColdStartTier.Tier3, learner.ResolveTier(GameA, "CarB", ShakeIt));
        }

        [Fact]
        public void Tier4_resolves_for_the_same_source_game_and_car_with_a_different_surface()
        {
            var learner = new KeyedScaleLearner();
            // Dry-surface reference, same car, same source, taught under an explicit surface bucket.
            for (int i = 0; i < 40; i++)
                learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 60.0, surfaceBucket: "Dry");

            // Querying the SAME car/source under a DIFFERENT surface, with a real surface hint - only
            // then can Tier 4 be told apart from a hint-less self-reference (see KeyedScaleLearner's own
            // remarks on why Tier 4 requires an explicit, non-empty surface hint).
            Assert.Equal(ColdStartTier.Tier4, learner.ResolveTier(GameA, "CarA", ShakeIt, "Wet"));
        }

        [Fact]
        public void Tier4_does_not_apply_when_the_current_surface_already_has_its_own_evidence()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++)
                learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 60.0, surfaceBucket: "Dry");
            for (int i = 0; i < 5; i++)
                learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 61.0, surfaceBucket: "Wet");

            // "Wet" already has SOME evidence of its own - it is not cold for Tier 4's own purpose, so
            // the Dry entry must not be offered as a "different surface" reference for it.
            Assert.Equal(ColdStartTier.Tier1, learner.ResolveTier(GameA, "CarA", ShakeIt, "Wet"));
        }

        [Fact]
        public void Tier4_requires_an_explicit_surface_hint_and_never_self_contaminates_a_hint_less_query()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++)
                learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 60.0, surfaceBucket: "Dry");

            // No surface hint at all (every pre-existing caller/test) - must never see the surface-tagged
            // entry as a spurious "different surface" reference for itself.
            Assert.Equal(ColdStartTier.Tier1, learner.ResolveTier(GameA, "CarA", ShakeIt));
        }

        // ------------------------------------------------------------------------------------
        // SOURCE ISOLATION - a Raw reference must never be used for a ShakeIt key, or vice versa.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_raw_reference_is_never_borrowed_for_a_shakeit_key()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarA", Raw, 60.0);
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarB", Raw, 60.0);

            // Same game, same cars, but a ShakeIt key - no ShakeIt reference exists anywhere, so this
            // MUST stay Tier 1 despite abundant same-game, same-car Raw evidence.
            Assert.Equal(ColdStartTier.Tier1, learner.ResolveTier(GameA, "CarC", ShakeIt));
        }

        // ------------------------------------------------------------------------------------
        // MOST SAMPLES WINS, WITH A STATED TIE-BREAK.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void Within_a_tier_the_candidate_with_the_most_samples_is_chosen()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 20; i++) learner.ObserveAtPhysicalLimit(GameA, "CarWeak", ShakeIt, 50.0);
            for (int i = 0; i < 100; i++) learner.ObserveAtPhysicalLimit(GameA, "CarStrong", ShakeIt, 70.0);

            double output = learner.Rescale(GameA, "CarCold", ShakeIt, 70.0);
            // CarStrong (100 samples, ceiling 70) must win over CarWeak (20 samples, ceiling 50) - a
            // 70-native reading against a 70 ceiling maps exactly to the canonical anchor, 80.
            Assert.Equal(80.0, output, 1);
        }

        /// <summary>TIE-BREAK, stated explicitly (the brief's own acceptance requirement): equal sample
        /// counts within the same tier are broken by the lexicographically smallest composite key - a
        /// fixed, deterministic, arbitrary-but-stable choice, not itself semantically meaningful.</summary>
        [Fact]
        public void A_tie_on_sample_count_is_broken_by_the_lexicographically_smallest_composite_key()
        {
            var learner = new KeyedScaleLearner();
            // Two candidates, SAME tier (Tier 3, same game, different car), SAME sample count (20), but
            // different ceilings - "CarAlpha" sorts before "CarBeta" lexicographically within the full
            // composite key (GameA|#|CarAlpha|#|... < GameA|#|CarBeta|#|...).
            for (int i = 0; i < 20; i++) learner.ObserveAtPhysicalLimit(GameA, "CarBeta", ShakeIt, 70.0);
            for (int i = 0; i < 20; i++) learner.ObserveAtPhysicalLimit(GameA, "CarAlpha", ShakeIt, 55.0);

            double output = learner.Rescale(GameA, "CarCold", ShakeIt, 55.0);
            // CarAlpha (lexicographically smaller) wins the tie - a 55-native reading against a 55
            // ceiling maps exactly to 80.
            Assert.Equal(80.0, output, 1);
        }

        // ------------------------------------------------------------------------------------
        // A WARM KEY WITH ITS OWN PARAMETERS IS UNAFFECTED.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void A_warm_key_with_abundant_own_evidence_is_unaffected_by_a_cross_context_reference()
        {
            var learner = new KeyedScaleLearner();
            // CarA matures fully on its own (well past CalibrationConfidenceScaleSamples=200) at a
            // DIFFERENT native ceiling (40) than any potential cross-game reference.
            // The engine feeds BOTH observers on the same frame, and since the anti-correlation fix the
            // ceiling is derived from the source's own distribution (ObserveGeneral), not from the
            // source value at at-limit moments - see KeyedScaleLearner.LearnedCeilingForKey. Feeding
            // only ObserveAtPhysicalLimit describes a state the engine never produces.
            for (int i = 0; i < 250; i++)
            {
                learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 40.0);
                learner.ObserveGeneral(GameA, "CarA", ShakeIt, 40.0);
            }
            double beforeReference = learner.Rescale(GameA, "CarA", ShakeIt, 40.0);

            // A wildly different-scale reference now appears (a different game, high native ceiling).
            for (int i = 0; i < 40; i++)
            {
                learner.ObserveAtPhysicalLimit(GameB, "CarB", ShakeIt, 95.0);
                learner.ObserveGeneral(GameB, "CarB", ShakeIt, 95.0);
            }
            double afterReference = learner.Rescale(GameA, "CarA", ShakeIt, 40.0);

            // CarA's own, already-warm mapping (40 native -> ~80 canonical) must not move.
            Assert.Equal(beforeReference, afterReference, 6);
            Assert.Equal(80.0, afterReference, 1);
        }

        // ------------------------------------------------------------------------------------
        // MUTATION EVIDENCE - forcing tier resolution to Tier 1 removes the improvement.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void MutationEvidence_forcing_tier1_removes_the_tier3_improvement()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 40; i++) learner.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 90.0);

            double withTiering = learner.Rescale(GameA, "CarCold", ShakeIt, 90.0);
            Assert.Equal(80.0, withTiering, 1); // Tier 3, full-strength borrow.

            KeyedScaleLearner.ForceTier1ForTesting = true;
            try
            {
                var forced = new KeyedScaleLearner();
                for (int i = 0; i < 40; i++) forced.ObserveAtPhysicalLimit(GameA, "CarA", ShakeIt, 90.0);
                double withoutTiering = forced.Rescale(GameA, "CarCold", ShakeIt, 90.0);

                Assert.Equal(90.0, withoutTiering, 1); // Tier 1 forced - plain identity, the pre-1.0.7 answer.
                Assert.True(withTiering < withoutTiering,
                    "forcing Tier 1 must remove the improvement a real Tier-3 reference otherwise provides");
            }
            finally
            {
                KeyedScaleLearner.ForceTier1ForTesting = false;
            }
        }

        // ------------------------------------------------------------------------------------
        // PROJECTED-SIDE FLOOR PER TIER, AND THE RAMP STILL REACHING EXACTLY 100%.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(ColdStartTier.Tier1, 0.50)]
        [InlineData(ColdStartTier.Tier2, 0.60)]
        [InlineData(ColdStartTier.Tier3, 0.70)]
        [InlineData(ColdStartTier.Tier4, 0.80)]
        public void Each_tiers_projected_floor_matches_the_documented_percentage(ColdStartTier tier, double expectedFloor)
        {
            Assert.Equal(expectedFloor, ColdStartTierFloors.FloorFor(tier), 6);
            // At zero confidence, ColdStartScale.Compute must read EXACTLY the tier's own floor.
            Assert.Equal(expectedFloor, ColdStartScale.Compute(0.0, expectedFloor), 6);
        }

        [Theory]
        [InlineData(0.50)]
        [InlineData(0.60)]
        [InlineData(0.70)]
        [InlineData(0.80)]
        public void The_ramp_still_reaches_exactly_100_percent_regardless_of_the_tiers_floor(double floor)
        {
            Assert.Equal(1.0, ColdStartScale.Compute(1.0, floor), 9);
        }

        [Fact]
        public void Tier1_floor_is_bit_identical_to_the_pre_1_0_7_default_overload()
        {
            for (double confidence = 0.0; confidence <= 1.0; confidence += 0.1)
                Assert.Equal(ColdStartScale.Compute(confidence), ColdStartScale.Compute(confidence, ColdStartScale.MinSafeFloor), 12);
        }

        [Fact]
        public void A_floor_below_the_hard_minimum_is_clamped_up_never_down()
        {
            // Passing a floor below MinSafeFloor (0.5) must still respect the hard mathematical floor -
            // see ColdStartScale's own remarks on why anything lower would invert the cue.
            Assert.Equal(ColdStartScale.MinSafeFloor, ColdStartScale.Compute(0.0, 0.2), 9);
        }

        [Fact]
        public void A_higher_floor_is_permitted_and_produces_a_higher_starting_scale()
        {
            double tier1Start = ColdStartScale.Compute(0.0, ColdStartTierFloors.Tier1Floor);
            double tier2Start = ColdStartScale.Compute(0.0, ColdStartTierFloors.Tier2Floor);
            double tier3Start = ColdStartScale.Compute(0.0, ColdStartTierFloors.Tier3Floor);
            double tier4Start = ColdStartScale.Compute(0.0, ColdStartTierFloors.Tier4Floor);

            Assert.True(tier1Start < tier2Start);
            Assert.True(tier2Start < tier3Start);
            Assert.True(tier3Start < tier4Start);
        }

        private static ProjectorSettings IdentitySettings()
        {
            var s = new ProjectorSettings();
            s.ApplyPreset(ProjectorPreset.Linear);
            return s;
        }

        [Fact]
        public void ProjectedEngine_floor_defaults_preserve_pre_1_0_7_behaviour()
        {
            // The new lockFloor/slipFloor parameters both default to MinSafeFloor - every pre-existing
            // caller/test that predates this feature keeps compiling and behaving exactly as before.
            var lockProjector = new OutputProjector(IdentitySettings());
            var slipProjector = new OutputProjector(IdentitySettings());
            var lockPulse = new PulseSettings { Enabled = false };
            var slipPulse = new PulseSettings { Enabled = false };
            var engine = new ProjectedWheelLockSlipEngine(lockProjector, lockPulse, slipProjector, slipPulse);

            var wheels = Corners.Uniform(50.0);
            var normalized = new NormalizedWheelLockSlipResult(
                wheels, 50.0, 50.0, 50.0, 50.0, 50.0,
                wheels, 50.0, 50.0, 50.0, 50.0, 50.0);

            var withDefaults = engine.Compute(normalized, 0.016, lockConfidence: 0.0, slipConfidence: 0.0);
            var withExplicitTier1 = engine.Compute(normalized, 0.016, lockConfidence: 0.0, slipConfidence: 0.0,
                lockFloor: ColdStartTierFloors.Tier1Floor, slipFloor: ColdStartTierFloors.Tier1Floor);

            Assert.Equal(withDefaults.LockAll, withExplicitTier1.LockAll, 9);
            Assert.Equal(withDefaults.SlipAll, withExplicitTier1.SlipAll, 9);
        }
    }
}
