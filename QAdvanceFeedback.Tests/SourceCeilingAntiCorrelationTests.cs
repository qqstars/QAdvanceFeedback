using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE ANTI-CORRELATION FIX. Root-caused from the owner's own four-session capture: the learned
    /// scale ceiling (SMax) collapsed to 25.5 for Lock and 16.9 for Slip, against an independently
    /// estimated ~85 and ~70-80, and the channels fired far too early and far too hard.
    /// <para/>
    /// The cause was not tuning. <c>ObserveAtPhysicalLimit</c> fires at maximum achieved deceleration,
    /// but a lock source measures wheel-versus-car divergence, which is at its MINIMUM exactly then -
    /// peak braking requires the tyres to be gripping, i.e. NOT locking. Learning the ceiling from the
    /// MEAN SOURCE VALUE at those frames therefore converged on the source's value during optimal grip
    /// with no lock. Measured in the capture: source mean 39.9 in the 50-70% G band, falling to 26.3 at
    /// 95-101%; in an older capture it fell to 2.2.
    /// <para/>
    /// The at-limit detector now decides only WHEN a key has trustworthy evidence. WHAT the ceiling is
    /// comes from the source's own high percentile.
    /// </summary>
    public class SourceCeilingAntiCorrelationTests
    {
        private const string Game = "GameA";
        private const string Car = "Car1";
        private const string Source = "ShakeItLike";

        /// <summary>Recovers the ceiling a learner is applying, through the public Rescale path.</summary>
        private static double EffectiveCeiling(KeyedScaleLearner learner)
        {
            const double probe = 10.0;
            double rescaled = learner.Rescale(Game, Car, Source, probe);
            return KeyedScaleLearner.CanonicalAtLimitAnchor * probe / rescaled;
        }

        /// <summary>
        /// Reproduces the measured anti-correlation directly: the source reads HIGH during ordinary
        /// hard braking and LOW at the physical limit, which is what the capture shows and what the
        /// old code learned from.
        /// </summary>
        [Fact]
        public void TheCeilingTracksTheSourcesOwnHighPercentile_NotItsValueAtTheLimit()
        {
            var learner = new KeyedScaleLearner(isLockChannel: true);

            const double approachSource = 25.0;  // what the source reads on the way to the limit
            const double lockOnsetSource = 80.0; // what it reads when a wheel is actually locking

            // RE-SPECIFIED once the corner-local detector was restored upstream
            // (docs\cross-channel-smax-report.md). The anti-correlation was never a property of at-limit
            // frames as such - it was a property of the DETECTOR that selected them. This now models the
            // FIXED detector: it reports a continuous confidence that is LOW during the approach (where
            // the source reads low, because the tyre is still gripping) and HIGH at the onset of lock.
            // The ceiling must therefore land near the lock-onset value, and must NOT land on the
            // weighted mean of the whole approach.
            for (int i = 0; i < 3000; i++)
            {
                bool atOnset = i % 10 == 0;
                double source = atOnset ? lockOnsetSource : approachSource;
                learner.ObserveGeneral(Game, Car, Source, source);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, source, atOnset ? 1.0 : 0.05);
            }

            double ceiling = EffectiveCeiling(learner);

            Assert.True(ceiling > 60.0,
                $"the ceiling must track the confidence-weighted at-limit distribution's upper percentile (~{lockOnsetSource}), got {ceiling:F1}");
            Assert.True(ceiling < 100.0, $"and must not exceed the source's own range, got {ceiling:F1}");

            // THE REGRESSION THIS GUARDS: the old code returned primary.GetAverage(). Averaging the
            // approach together with the onset lands near approachSource and would have produced a 3x
            // over-scaling of every reading - the measured over-shake.
            Assert.True(ceiling > approachSource * 2.0,
                $"ceiling {ceiling:F1} is still near the approach mean {approachSource} - the anti-correlation has returned");
        }

        [Fact]
        public void AnOverScaledCeilingIsWhatMadeTheChannelFireTooEarly()
        {
            // The consequence, stated numerically so the cost of regressing is explicit.
            const double moderateSource = 40.0;
            double withBrokenCeiling = ClampMath.To0100(moderateSource * (KeyedScaleLearner.CanonicalAtLimitAnchor / 25.0));
            double withCorrectCeiling = ClampMath.To0100(moderateSource * (KeyedScaleLearner.CanonicalAtLimitAnchor / 78.0));

            Assert.Equal(100.0, withBrokenCeiling, 6);   // saturated - "kicks in too early and too strong"
            Assert.InRange(withCorrectCeiling, 35.0, 50.0);
        }

        [Fact]
        public void AtLimitEvidenceIsWhatDefinesTheCeiling()
        {
            // RE-SPECIFIED (docs\cross-channel-smax-report.md). An earlier revision of this test asserted
            // the OPPOSITE - that at-limit evidence must NOT define the ceiling - which was correct only
            // while the detector feeding it was known to be firing 45% early. With the corner-local
            // detector restored, this distribution holds the frames it always should have, and it IS the
            // definition of the ceiling; that is the whole point of the anchor being physical rather than
            // statistical.
            var learner = new KeyedScaleLearner(isLockChannel: true);
            for (int i = 0; i < 1000; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 78.0);

            double ceiling = EffectiveCeiling(learner);

            Assert.InRange(ceiling, 70.0, 86.0);
        }

        [Fact]
        public void TheSlipChannelIsFixedTheSameWay()
        {
            // Wheelspin REDUCES forward acceleration, so Slip's at-limit frames anti-correlate for the
            // same reason. The capture showed Slip collapsing furthest of all - to 16.9.
            var learner = new KeyedScaleLearner(isLockChannel: false);

            for (int i = 0; i < 3000; i++)
            {
                bool atOnset = i % 10 == 0;
                double source = atOnset ? 70.0 : 17.0;
                learner.ObserveGeneral(Game, Car, Source, source);
                learner.ObserveAtPhysicalLimit(Game, Car, Source, source, atOnset ? 1.0 : 0.05);
            }

            Assert.True(EffectiveCeiling(learner) > 50.0,
                $"Slip ceiling must track the onset of wheelspin, not the approach, got {EffectiveCeiling(learner):F1}");
        }
    }
}
