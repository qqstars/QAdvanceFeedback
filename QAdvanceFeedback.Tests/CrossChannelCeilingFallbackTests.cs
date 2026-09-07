using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE CROSS-CHANNEL CEILING FALLBACK (docs\slip-smax-crossing-gate-design.md, section 7) -
    /// <see cref="KeyedScaleLearner.AttachCrossChannelLockReference"/> and what it does to Slip's
    /// published ceiling while Slip's own evidence is thin.
    /// <para/>
    /// Exercised directly against the learner rather than through the engine, so the arithmetic is
    /// checked against numbers a reader can follow: a constant-80 Lock distribution has a P90 of 80,
    /// which divided by <see cref="KeyedScaleLearner.LockToSlipRatio"/> is the value Lock contributes.
    /// </summary>
    public class CrossChannelCeilingFallbackTests
    {
        private const string Game = "G", Car = "C", Source = "Src";

        /// <summary>A Lock learner with abundant, tight, fully-confident evidence at one value.</summary>
        private static KeyedScaleLearner ConfidentLock(double atLimitValue, int samples = 400)
        {
            var learner = new KeyedScaleLearner(isLockChannel: true);
            for (int i = 0; i < samples; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, atLimitValue, 1.0);
            return learner;
        }

        /// <summary>
        /// A Slip learner that has been FED but has learned nothing - every fold-in at weight 0.0,
        /// exactly what the crossing gate does to a standing start. primary.Count is therefore &gt; 0
        /// (the learner has been called) while PositiveSampleCount is 0 (nothing qualified), which is
        /// the precise state section 7 exists to cover.
        /// </summary>
        private static KeyedScaleLearner StarvedSlip(int samples = 400)
        {
            var learner = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < samples; i++)
                learner.ObserveNonQualifyingAtPhysicalLimit(Game, Car, Source);
            return learner;
        }

        [Fact]
        public void Without_a_lock_reference_the_ceiling_is_the_bare_cold_anchor()
        {
            KeyedScaleLearner slip = StarvedSlip();
            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);

            Assert.NotNull(ceiling);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, ceiling.Value, 6);
        }

        [Fact]
        public void A_confident_lock_moves_a_starved_slip_ceiling_halfway_toward_its_scale_corrected_value()
        {
            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(ConfidentLock(80.0));

            // anchor 80, lock contributes 80 / 1.6 = 50, share = TierTrust(Tier1) 1.0 * cL 1.0 * cap 0.5
            //   -> 80 + 0.5 * (50 - 80) = 65
            double expected = KeyedScaleLearner.CanonicalAtLimitAnchor
                + KeyedScaleLearner.CrossChannelLockShareCap
                  * (80.0 / KeyedScaleLearner.LockToSlipRatio - KeyedScaleLearner.CanonicalAtLimitAnchor);

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.Equal(expected, ceiling.Value, 6);
        }

        [Fact]
        public void Lock_is_scale_corrected_not_used_raw()
        {
            // The single most important guard in this feature: measured on the corpus, injecting Lock's
            // RAW ceiling took the median cold-session error from 25% to 50%. If a future change drops
            // the divisor, this is what catches it.
            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(ConfidentLock(80.0));

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            double uncorrected = KeyedScaleLearner.CanonicalAtLimitAnchor
                + KeyedScaleLearner.CrossChannelLockShareCap * (80.0 - KeyedScaleLearner.CanonicalAtLimitAnchor);

            Assert.NotNull(ceiling);
            Assert.True(ceiling.Value < uncorrected - 5.0,
                $"ceiling {ceiling.Value:F2} is not scale-corrected - raw Lock would give {uncorrected:F2}");
        }

        [Fact]
        public void Lock_never_takes_more_than_the_share_cap()
        {
            // Even an absurdly high Lock cannot drag Slip past the halfway point of the anchor.
            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(ConfidentLock(100.0));

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            double lockContribution = 100.0 / KeyedScaleLearner.LockToSlipRatio;
            double halfway = KeyedScaleLearner.CanonicalAtLimitAnchor
                + KeyedScaleLearner.CrossChannelLockShareCap
                  * (lockContribution - KeyedScaleLearner.CanonicalAtLimitAnchor);

            Assert.NotNull(ceiling);
            Assert.Equal(halfway, ceiling.Value, 6);
        }

        [Fact]
        public void With_no_lock_confidence_the_formula_degenerates_to_the_previous_behaviour()
        {
            // THE BACKWARD-COMPATIBILITY PROPERTY. At cL == 0 the three-way blend collapses to the old
            // two-way one exactly - so wherever Lock has nothing to say, nothing changes.
            var coldLock = new KeyedScaleLearner(isLockChannel: true);
            coldLock.ObserveAtPhysicalLimit(Game, Car, Source, 80.0, 1.0);   // one sample: no confidence

            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(coldLock);

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, ceiling.Value, 6);
        }

        [Fact]
        public void A_lock_with_no_evidence_at_all_is_a_no_op()
        {
            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(new KeyedScaleLearner(isLockChannel: true));

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, ceiling.Value, 6);
        }

        [Fact]
        public void Slips_own_confident_evidence_displaces_the_fallback_entirely()
        {
            // The fallback changes what the ceiling falls back TO; it must contribute nothing once Slip
            // has genuinely learned its own value, or it would permanently bias a healthy channel.
            var slip = new KeyedScaleLearner(isLockChannel: false);
            slip.AttachCrossChannelLockReference(ConfidentLock(80.0));
            for (int i = 0; i < 400; i++) slip.ObserveAtPhysicalLimit(Game, Car, Source, 42.0, 1.0);

            double? ceiling = slip.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(ceiling);
            Assert.Equal(42.0, ceiling.Value, 1);
        }

        [Fact]
        public void A_real_borrowed_slip_reference_earns_a_smaller_lock_share_than_a_shipped_constant()
        {
            // MEASURED, not a preference (design doc section 7.4): against a genuine previous-session
            // Slip ceiling, Lock adds noise (16% -> 17-31% median error); against a shipped constant it
            // is real same-car evidence (43% -> 31%). So Tier 3 must lean on Lock LESS than Tier 1.
            var slip = new KeyedScaleLearner(isLockChannel: false);
            // A mature different-car key under the same game: a Tier-3 candidate for "CarB".
            for (int i = 0; i < 400; i++) slip.ObserveAtPhysicalLimit(Game, "CarA", Source, 40.0, 1.0);

            var lockLearner = new KeyedScaleLearner(isLockChannel: true);
            for (int i = 0; i < 400; i++) lockLearner.ObserveAtPhysicalLimit(Game, "CarB", Source, 80.0, 1.0);
            slip.AttachCrossChannelLockReference(lockLearner);

            double? tier3 = slip.LearnedCeiling(Game, "CarB", Source, out _);
            Assert.NotNull(tier3);

            // Lock contributes 50, the borrowed anchor is 40, so Lock pulls the answer UP - and by less
            // than the Tier-1 share would.
            double anchor = 40.0;
            double lockContribution = 80.0 / KeyedScaleLearner.LockToSlipRatio;
            double tier1Share = anchor + KeyedScaleLearner.CrossChannelLockShareCap * (lockContribution - anchor);

            Assert.True(tier3.Value > anchor, $"Lock should still contribute something ({tier3.Value:F2} vs anchor {anchor:F2})");
            Assert.True(tier3.Value < tier1Share,
                $"a borrowed Slip reference must earn Lock a SMALLER share: got {tier3.Value:F2}, Tier-1 share would be {tier1Share:F2}");
        }

        [Fact]
        public void The_reference_refuses_to_attach_to_a_lock_learner_or_to_itself()
        {
            // Both are wiring mistakes whose symptom - a ceiling quietly folded into its own anchor - is
            // close to invisible in a log.
            var lockLearner = new KeyedScaleLearner(isLockChannel: true);
            for (int i = 0; i < 400; i++) lockLearner.ObserveAtPhysicalLimit(Game, Car, Source, 20.0, 1.0);

            var other = ConfidentLock(20.0);
            lockLearner.AttachCrossChannelLockReference(other);
            double? lockCeiling = lockLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(lockCeiling);
            Assert.Equal(20.0, lockCeiling.Value, 1);

            KeyedScaleLearner slip = StarvedSlip();
            slip.AttachCrossChannelLockReference(slip);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, slip.LearnedCeiling(Game, Car, Source, out _).Value, 6);
        }

        [Fact]
        public void Lock_is_looked_up_under_locks_own_identity_not_slips()
        {
            // THE BUG THIS PINS. Each channel's source identity is SourceIdentity.Compute over that
            // channel's OWN four wheel properties, so the default configuration alone gives
            // "Plain:WheelLock.Raw.FrontLeft~..." for Lock and "Plain:WheelSlip.Raw.FrontLeft~..." for
            // Slip - different strings for the same physical source choice. Querying Lock under Slip's
            // identity finds nothing, and the whole fallback becomes a silent no-op in the shipped
            // plugin while still passing any test that lazily uses one identity for both channels.
            const string lockIdentity = "Plain:WheelLock.Raw.FrontLeft~Plain:WheelLock.Raw.FrontRight"
                                        + "~Plain:WheelLock.Raw.RearLeft~Plain:WheelLock.Raw.RearRight";
            const string slipIdentity = "Plain:WheelSlip.Raw.FrontLeft~Plain:WheelSlip.Raw.FrontRight"
                                        + "~Plain:WheelSlip.Raw.RearLeft~Plain:WheelSlip.Raw.RearRight";

            var lockLearner = new KeyedScaleLearner(isLockChannel: true);
            for (int i = 0; i < 400; i++)
                lockLearner.ObserveAtPhysicalLimit(Game, Car, lockIdentity, 80.0, 1.0);

            var slip = new KeyedScaleLearner(isLockChannel: false);
            for (int i = 0; i < 400; i++) slip.ObserveNonQualifyingAtPhysicalLimit(Game, Car, slipIdentity);
            slip.AttachCrossChannelLockReference(lockLearner);

            double withoutIdentity = slip.LearnedCeiling(Game, Car, slipIdentity, out _) ?? 0.0;

            slip.SetCrossChannelLockSourceIdentity(lockIdentity);
            double withIdentity = slip.LearnedCeiling(Game, Car, slipIdentity, out _) ?? 0.0;

            Assert.True(withIdentity < withoutIdentity - 1.0,
                $"Lock must be found under its own identity: {withIdentity:F2} vs {withoutIdentity:F2}");
        }

        [Fact]
        public void The_engine_wires_slip_to_lock_and_never_the_reverse()
        {
            var engine = new NormalizedWheelLockSlipEngine();
            for (int i = 0; i < 400; i++)
                engine.LockScaleLearner.ObserveAtPhysicalLimit(Game, Car, Source, 80.0, 1.0);
            for (int i = 0; i < 400; i++)
                engine.SlipScaleLearner.ObserveNonQualifyingAtPhysicalLimit(Game, Car, Source);

            double? slipCeiling = engine.SlipScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(slipCeiling);
            Assert.True(slipCeiling.Value < KeyedScaleLearner.CanonicalAtLimitAnchor - 1.0,
                $"the engine should have wired Slip to Lock (ceiling read {slipCeiling.Value:F2})");

            // And Lock, fed only its own evidence, is untouched by Slip.
            double? lockCeiling = engine.LockScaleLearner.LearnedCeiling(Game, Car, Source, out _);
            Assert.NotNull(lockCeiling);
            Assert.Equal(80.0, lockCeiling.Value, 1);
        }
    }
}
