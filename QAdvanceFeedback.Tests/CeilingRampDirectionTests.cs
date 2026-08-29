using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// CAN A SEEDED CEILING RAMP DOWN? Asked directly by the owner before agreeing to seed the ceiling
    /// at a non-zero starting point: "check if we start from 0.15 instead of 0, can it ramp down from
    /// 0.15 to 0.1 for example, or will it never ramp down?"
    /// <para/>
    /// The answer must be demonstrated, not reasoned about - a seed that could only ever ramp UP would
    /// be a one-way ratchet, and this project has already rejected one of those (see
    /// ReferencedDistributionLearner.OwnKeyExportDamping's own remarks) because a value that cannot come
    /// back down turns a transient into a permanent defect.
    /// </summary>
    public class CeilingRampDirectionTests
    {
        private const string Game = "GameA";
        private const string Car = "Car1";
        private const string Source = "AnySource";

        private static double EffectiveCeiling(KeyedScaleLearner learner)
        {
            const double probe = 1.0;
            return KeyedScaleLearner.CanonicalAtLimitAnchor * probe / learner.Rescale(Game, Car, Source, probe);
        }

        /// <summary>Evidence BELOW the starting anchor must pull the ceiling down to it, not floor it.</summary>
        [Fact]
        public void TheCeilingRampsDownWhenTheEvidenceIsLowerThanTheAnchor()
        {
            var learner = new KeyedScaleLearner();

            // With no evidence at all the unknown-source cold state is identity, i.e. an effective
            // ceiling of the canonical anchor itself.
            double atStart = EffectiveCeiling(learner);
            Assert.Equal(KeyedScaleLearner.CanonicalAtLimitAnchor, atStart, 6);

            // Now show it evidence well BELOW that starting point.
            const double lowerTruth = 20.0;
            for (int i = 0; i < 3000; i++)
            {
                learner.ObserveGeneral(Game, Car, Source, lowerTruth);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, lowerTruth);
            }

            double settled = EffectiveCeiling(learner);
            Assert.True(settled < atStart - 1.0,
                $"the ceiling must be able to fall below its starting point - started {atStart:F1}, settled {settled:F1}");
            Assert.Equal(lowerTruth, settled, 1);
        }

        /// <summary>...and it must still be able to rise, so the mechanism is genuinely bidirectional
        /// rather than merely inverted.</summary>
        [Fact]
        public void TheCeilingAlsoRampsUpWhenTheEvidenceIsHigher()
        {
            var learner = new KeyedScaleLearner();
            const double higherTruth = 95.0;
            for (int i = 0; i < 3000; i++)
            {
                learner.ObserveGeneral(Game, Car, Source, higherTruth);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, higherTruth);
            }

            Assert.Equal(higherTruth, EffectiveCeiling(learner), 1);
        }

        /// <summary>
        /// KNOWN DEFECT, CHARACTERISED RATHER THAN ASSERTED AS DESIRABLE - do not read this test as
        /// approval of the behaviour it pins.
        /// <para/>
        /// A ceiling that has already settled high CANNOT currently come back down, for two compounding
        /// reasons in <see cref="OnlineDistributionLearner"/>:
        /// <list type="bullet">
        /// <item>its histogram is purely CUMULATIVE (<c>_histogram[bucket] = existing + 1</c>, never
        /// decayed), so once a high tail exists it stays in the top 1% until swamped by roughly a
        /// HUNDRED times as many low samples;</item>
        /// <item><c>ObserveGeneral</c> stops feeding at <c>MaxSamples</c> (7000), so after roughly two
        /// minutes of engaged driving the distribution is frozen outright and no amount of new evidence
        /// can move it at all.</item>
        /// </list>
        /// Together these make the percentile ceiling effectively a one-way ratchet upward - the same
        /// failure mode this project already rejected once, in
        /// <c>ReferencedDistributionLearner.OwnKeyExportDamping</c>'s own remarks.
        /// <para/>
        /// IMPORTANT SCOPE: this does NOT affect ramping down from a SEED or a shipped reference - those
        /// are blends against a separate anchor and move freely in both directions, as the other tests
        /// here prove. It only affects re-learning downward once the key's own distribution already
        /// contains high readings.
        /// <para/>
        /// The fix is a forgetting distribution (decayed counts, or a bounded window) rather than a
        /// cumulative one. Deliberately not applied yet - flagged for the owner's decision.
        /// </summary>
        [Fact]
        public void AlreadySettledHigh_TheCeilingComesBackDownAsNewEvidenceAccumulates()
        {
            var learner = new KeyedScaleLearner();
            for (int i = 0; i < 3000; i++)
            {
                learner.ObserveGeneral(Game, Car, Source, 90.0);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 90.0);
            }
            double settledHigh = EffectiveCeiling(learner);
            Assert.Equal(90.0, settledHigh, 1);

            // The truth changes. Recovery is deliberately gradual - the histogram's effective window is
            // ~20,000 samples, so a genuine change is tracked over minutes of driving rather than
            // instantly, which is what keeps a 99th percentile an estimate rather than noise.
            double after30k = Feed(learner, 30000, 30.0);
            double after120k = Feed(learner, 90000, 30.0);

            Assert.True(after120k < settledHigh - 5.0,
                $"a settled ceiling must track a genuine change downward - was {settledHigh:F1}, now {after120k:F1}");
            Assert.True(after120k <= after30k,
                $"recovery must be monotone, not oscillating - {after30k:F1} then {after120k:F1}");
        }

        private static double Feed(KeyedScaleLearner learner, int frames, double value)
        {
            // BOTH distributions - the ceiling is read from the physically-anchored one (see
            // KeyedScaleLearner.PhysicalAnchorCeilingPercentile), so feeding only the general one would
            // test nothing about what is actually published.
            for (int i = 0; i < frames; i++)
            {
                learner.ObserveGeneral(Game, Car, Source, value);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, value);
            }
            return EffectiveCeiling(learner);
        }

        /// <summary>
        /// The shipped cold reference is a starting point, not a floor: a source whose true ceiling is
        /// BELOW the shipped constant must converge onto its own value.
        /// </summary>
        [Fact]
        public void TheShippedColdReferenceIsAStartingPointNotAFloor()
        {
            var learner = new KeyedScaleLearner(isLockChannel: true);
            string identity = SourceIdentity.Compute(
                "QAdvanceFeedback.WheelLock.Raw.FrontLeft", "Plain",
                "QAdvanceFeedback.WheelLock.Raw.FrontRight", "Plain",
                "QAdvanceFeedback.WheelLock.Raw.RearLeft", "Plain",
                "QAdvanceFeedback.WheelLock.Raw.RearRight", "Plain");

            double shipped = KeyedScaleLearner.CanonicalAtLimitAnchor * 1.0
                             / learner.Rescale(Game, Car, identity, 1.0);
            Assert.Equal(KnownSourceColdStartReference.LockRawSMax, shipped, 6);

            const double lowerTruth = 30.0;   // well below the shipped 66
            for (int i = 0; i < 5000; i++)
            {
                learner.ObserveGeneral(Game, Car, identity, lowerTruth);
                learner.ObserveAtPhysicalLimit(Game, Car, identity, lowerTruth);
            }

            double settled = KeyedScaleLearner.CanonicalAtLimitAnchor * 1.0
                             / learner.Rescale(Game, Car, identity, 1.0);
            Assert.True(settled < shipped - 1.0,
                $"the shipped reference must not act as a floor - shipped {shipped:F1}, settled {settled:F1}");
            Assert.Equal(lowerTruth, settled, 1);
        }
    }
}
