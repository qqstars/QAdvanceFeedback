using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.MotorsExport;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// SHIPPED TIER-1 COLD-START REFERENCE. Covers the one branch of <see cref="KeyedScaleLearner"/> that
    /// used to fall back to plain identity - a genuine cold start with nothing to borrow - plus the two
    /// properties that make it safe: an unknown source still gets identity, and the hand-off from the
    /// shipped value to this key's own evidence is CONTINUOUS rather than a step.
    /// </summary>
    public class ShippedColdStartReferenceTests
    {
        private const string Game = "F12025";
        private const string Car = "Sauber";

        private static string RawIdentity(bool isLockChannel)
        {
            string channel = isLockChannel ? "WheelLock" : "WheelSlip";
            return Identity(
                "QAdvanceFeedback." + channel + ".Raw.FrontLeft", "QAdvanceFeedback." + channel + ".Raw.FrontRight",
                "QAdvanceFeedback." + channel + ".Raw.RearLeft", "QAdvanceFeedback." + channel + ".Raw.RearRight");
        }

        private static string ShakeItIdentity(bool isLockChannel)
            => Identity(
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontLeft),
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.FrontRight),
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearLeft),
                MotorsExportPropertyNames.GetWheelPropertyName(isLockChannel, MotorsExportPropertyNames.RearRight));

        private static string Identity(string fl, string fr, string rl, string rr)
            => SourceIdentity.Compute(fl, "Plain", fr, "Plain", rl, "Plain", rr, "Plain");

        /// <summary>The ceiling a learner is currently applying, recovered from Rescale's own
        /// <c>raw * (80 / ceiling)</c> - deliberately measured through the public path callers use rather
        /// than by reaching into internals.</summary>
        private static double EffectiveCeiling(KeyedScaleLearner learner, string identity)
        {
            const double probe = 10.0;
            double rescaled = learner.Rescale(Game, Car, identity, probe);
            return KeyedScaleLearner.CanonicalAtLimitAnchor * probe / rescaled;
        }

        // ------------------------------------------------------------------------------------
        // Classification guard rails.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void RecognisesBothKnownSources(bool isLock)
        {
            Assert.Equal(KnownFeedbackSource.QAdvanceFeedbackRaw, KnownSourceColdStartReference.Classify(RawIdentity(isLock), isLock));
            Assert.Equal(KnownFeedbackSource.ShakeItMotorsExport, KnownSourceColdStartReference.Classify(ShakeItIdentity(isLock), isLock));
        }

        [Fact]
        public void RefusesAMixedOrCrossWiredChannel()
        {
            string mixed = Identity(
                "QAdvanceFeedback.WheelLock.Raw.FrontLeft",
                MotorsExportPropertyNames.GetWheelPropertyName(true, MotorsExportPropertyNames.FrontRight),
                "QAdvanceFeedback.WheelLock.Raw.RearLeft", "QAdvanceFeedback.WheelLock.Raw.RearRight");

            Assert.Equal(KnownFeedbackSource.Unknown, KnownSourceColdStartReference.Classify(mixed, isLockChannel: true));
            // Lock pointed at the SLIP channel's Raw is a cross-wiring nothing has measured.
            Assert.Equal(KnownFeedbackSource.Unknown, KnownSourceColdStartReference.Classify(RawIdentity(false), isLockChannel: true));
        }

        [Fact]
        public void EverySmaxSitsBelowTheCanonicalAnchor_WhichIsWhyIdentityUnderstatedIt()
        {
            Assert.True(KnownSourceColdStartReference.LockRawSMax < KeyedScaleLearner.CanonicalAtLimitAnchor);
            Assert.True(KnownSourceColdStartReference.LockShakeItSMax < KeyedScaleLearner.CanonicalAtLimitAnchor);
            Assert.True(KnownSourceColdStartReference.SlipRawSMax < KeyedScaleLearner.CanonicalAtLimitAnchor);
            Assert.True(KnownSourceColdStartReference.SlipShakeItSMax < KeyedScaleLearner.CanonicalAtLimitAnchor);
        }

        // ------------------------------------------------------------------------------------
        // Backward compatibility - the paths that must stay exactly as 1.0.7 had them.
        // ------------------------------------------------------------------------------------

        [Fact]
        public void AnUnknownSourceStillGetsPlainIdentity()
        {
            var learner = new KeyedScaleLearner(isLockChannel: true);
            Assert.Equal(30.0, learner.Rescale(Game, Car, "Plain:SomeThirdPartyPlugin.Value", 30.0), 6);
        }

        [Fact]
        public void ALearnerThatDoesNotNameItsChannelIsUnchangedFrom107()
        {
            var learner = new KeyedScaleLearner();
            Assert.Equal(30.0, learner.Rescale(Game, Car, RawIdentity(isLockChannel: true), 30.0), 6);
        }

        // ------------------------------------------------------------------------------------
        // The shipped reference, and the CONTINUOUS hand-off to this key's own evidence.
        // ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(true, KnownSourceColdStartReference.LockRawSMax)]
        [InlineData(false, KnownSourceColdStartReference.SlipRawSMax)]
        public void AColdStartUsesThisChannelsOwnShippedSmax(bool isLock, double expectedSMax)
        {
            var learner = new KeyedScaleLearner(isLockChannel: isLock);
            string identity = RawIdentity(isLock);

            Assert.Equal(expectedSMax, EffectiveCeiling(learner, identity), 6);
            Assert.True(learner.Rescale(Game, Car, identity, 30.0) > 30.0,
                "the shipped reference must lift the cold output above bare identity");
        }

        [Fact]
        public void TheHandOffFromShippedToOwnEvidenceHasNoStep()
        {
            // THE POINT OF THE RAMP. The secondary percentile learner cannot answer below
            // MinSamplesForPercentile, so the frame it becomes ready is where a plain switch would jolt.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            string identity = RawIdentity(isLockChannel: true);
            const double ownCeiling = 40.0;   // deliberately far from the shipped 66

            for (int i = 0; i < OnlineDistributionLearner.MinSamplesForPercentile - 1; i++)
                learner.ObserveGeneral(Game, Car, identity, ownCeiling);

            double justBefore = EffectiveCeiling(learner, identity);
            Assert.Equal(KnownSourceColdStartReference.LockRawSMax, justBefore, 6);

            learner.ObserveGeneral(Game, Car, identity, ownCeiling); // the learner is now ready
            double justAfter = EffectiveCeiling(learner, identity);

            Assert.Equal(justBefore, justAfter, 6); // continuous - weight is exactly 0 at the bar
        }

        [Fact]
        public void TheRampMovesMonotonicallyOntoOwnEvidence_AndCompletes()
        {
            var learner = new KeyedScaleLearner(isLockChannel: true);
            string identity = RawIdentity(isLockChannel: true);
            const double ownCeiling = 40.0;

            for (int i = 0; i < OnlineDistributionLearner.MinSamplesForPercentile; i++)
                learner.ObserveGeneral(Game, Car, identity, ownCeiling);

            double previous = EffectiveCeiling(learner, identity);
            Assert.Equal(KnownSourceColdStartReference.LockRawSMax, previous, 6);

            for (int block = 0; block < 5; block++)
            {
                for (int i = 0; i < 250; i++) learner.ObserveGeneral(Game, Car, identity, ownCeiling);
                double now = EffectiveCeiling(learner, identity);
                Assert.True(now < previous, $"the ceiling must keep moving toward own evidence ({now} vs {previous})");
                previous = now;
            }

            // Past the handover point the shipped value contributes nothing at all.
            for (int i = 0; i < KeyedScaleLearner.ShippedReferenceHandoverSamples; i++)
                learner.ObserveGeneral(Game, Car, identity, ownCeiling);

            Assert.Equal(ownCeiling, EffectiveCeiling(learner, identity), 6);
        }

        [Fact]
        public void PrimaryEvidenceOverridesTheShippedReferenceEntirely()
        {
            // The shipped constant is a starting point, never a ceiling on what can be learned.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            string identity = RawIdentity(isLockChannel: true);

            for (int i = 0; i < 400; i++)
            {
                learner.ObserveAtPhysicalLimit(Game, Car, identity, 95.0);
                // Both observers, as the engine always does - since the anti-correlation fix the
                // ceiling comes from the source's own distribution, not from its value at
                // at-limit moments. See KeyedScaleLearner.LearnedCeilingForKey.
                learner.ObserveGeneral(Game, Car, identity, 95.0);
            }

            Assert.Equal(95.0, EffectiveCeiling(learner, identity), 3);
        }

        [Fact]
        public void PrimaryAndColdBranchesAgreeAtTheirBoundary()
        {
            // The two cold paths (primary-evidence-exists vs no-evidence-at-all) must publish the SAME
            // value at the moment the first primary observation lands, or the transition between them
            // steps. They agree because both anchor on Tier1ColdCeiling.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            string identity = RawIdentity(isLockChannel: true);

            double beforeAnyPrimary = EffectiveCeiling(learner, identity);
            learner.ObserveAtPhysicalLimit(Game, Car, identity, KnownSourceColdStartReference.LockRawSMax);
            learner.ObserveGeneral(Game, Car, identity, KnownSourceColdStartReference.LockRawSMax);
            double afterFirstPrimary = EffectiveCeiling(learner, identity);

            Assert.Equal(beforeAnyPrimary, afterFirstPrimary, 6);
        }
    }
}
