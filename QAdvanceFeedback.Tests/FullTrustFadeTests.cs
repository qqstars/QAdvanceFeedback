using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.Normalized;
using Xunit;
using Xunit.Abstractions;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// THE FULL-TRUST FADE (owner, 2026-09-25 - v1.1.0). The primary tier's blend weight used to plateau
    /// (DispersionQuality does not vary with sample count) and then jump to 1.0 on the single frame
    /// sample #200 arrived. Measured on the owner's FH6 capture, frames 7625 -&gt; 7626, that moved the
    /// published ceiling 80.152 -&gt; 88.574 - 8.4 points in ONE frame, mid-braking-zone, against roughly
    /// 0.4 points per frame everywhere else in that session.
    /// <para/>
    /// THE FIX FADES THE LAST 50 SAMPLES RATHER THAN DELAYING ANYTHING. Cold start is exactly as long as
    /// it was - full trust still at <see cref="KeyedScaleLearner.CalibrationConfidenceScaleSamples"/> -
    /// which is what these tests pin, alongside the continuity that motivated it.
    /// </summary>
    public class FullTrustFadeTests
    {
        private readonly ITestOutputHelper _out;
        public FullTrustFadeTests(ITestOutputHelper output) { _out = output; }

        private const string Game = "G";
        private const string Car = "C";
        private const string Source = "S";

        /// <summary>Feeds <paramref name="samples"/> at-limit observations with a realistic spread (so the
        /// coefficient of variation is nonzero, which is the whole reason the plateau existed) and returns
        /// the published ceiling after each one.</summary>
        private static List<double> CeilingSeries(int samples)
        {
            var learner = new KeyedScaleLearner();
            var series = new List<double>(samples);
            var rng = new Random(20260925);
            for (int i = 0; i < samples; i++)
            {
                // ~70 +/- 7: a CV near 0.10, the realistic band the plateau was measured at.
                double reading = 70.0 + (rng.NextDouble() - 0.5) * 14.0;
                learner.ObserveAtPhysicalLimit(Game, Car, Source, reading);
                double? ceiling = learner.LearnedCeiling(Game, Car, Source, out _);
                series.Add(ceiling ?? double.NaN);
            }
            return series;
        }

        [Fact]
        public void No_single_sample_moves_the_published_ceiling_by_more_than_a_point()
        {
            // THE DEFECT THIS CLOSES. The old code's step at sample 200 was 8.4 points on the owner's own
            // capture; every other frame moved ~0.4. A one-point bound is comfortably above normal
            // movement and far below the cliff, so it catches a regression without being brittle.
            List<double> series = CeilingSeries(260);

            double worst = 0.0;
            int worstAt = -1;
            for (int i = 1; i < series.Count; i++)
            {
                if (double.IsNaN(series[i]) || double.IsNaN(series[i - 1])) continue;
                double step = Math.Abs(series[i] - series[i - 1]);
                if (step > worst) { worst = step; worstAt = i; }
            }

            _out.WriteLine($"largest single-sample ceiling step: {worst:F3} at sample {worstAt}");
            Assert.True(worst < 1.0, $"ceiling stepped {worst:F2} points at sample {worstAt} - the cliff is back");
        }

        [Fact]
        public void The_hand_over_boundary_itself_is_no_longer_a_cliff()
        {
            // THE EXACT FRAME THE OWNER REPORTED. The previous code snapped the weight to 1.0 on the
            // sample that reached CalibrationConfidenceScaleSamples, which on the FH6 capture moved the
            // published ceiling 80.152 -> 88.574 between frames 7625 and 7626. This pins that ONE
            // boundary rather than the series as a whole, so a regression there cannot hide behind an
            // average.
            //
            // NOTE ON WHAT IS NOT ASSERTED: the ceiling is deliberately NOT monotone across the whole
            // series. At MinPhysicalAnchorSamples the at-limit percentile becomes available for the first
            // time and the ceiling steps DOWN off the cold anchor toward this key's own learned value -
            // that is the calibration working, and asserting monotonicity would forbid it.
            List<double> series = CeilingSeries(260);

            const int handover = KeyedScaleLearner.CalibrationConfidenceScaleSamples;   // 1-based sample
            double before = series[handover - 2];
            double after = series[handover - 1];
            double step = Math.Abs(after - before);

            _out.WriteLine($"sample {handover - 1} -> {handover}: {before:F4} -> {after:F4}  (step {step:F4})");
            Assert.True(step < 0.5,
                $"the hand-over to full trust stepped {step:F2} points in one sample - the cliff is back");
        }

        [Fact]
        public void Every_sample_inside_the_fade_window_moves_the_ceiling_only_slightly()
        {
            // The fade window is where the old cliff lived, so it gets its own tighter bound than the
            // whole-series test above.
            //
            // DIRECTION IS DELIBERATELY NOT ASSERTED. A first version of this required the ceiling to
            // travel one way across the window and it failed on a 0.003-point reversal at sample 157 -
            // the at-limit PERCENTILE still shifting as readings land in the histogram, which is nothing
            // to do with the weight and would have made this test a proxy for percentile stability.
            List<double> series = CeilingSeries(260);
            int from = KeyedScaleLearner.FullTrustFadeStartSamples;
            int to = KeyedScaleLearner.CalibrationConfidenceScaleSamples;

            double worst = 0.0;
            int worstAt = -1;
            for (int i = from; i < to; i++)
            {
                double step = Math.Abs(series[i] - series[i - 1]);
                if (step > worst) { worst = step; worstAt = i + 1; }
            }

            _out.WriteLine($"largest step inside the fade window: {worst:F4} at sample {worstAt}");
            Assert.True(worst < 0.5, $"ceiling stepped {worst:F2} points at sample {worstAt} inside the fade");
        }

        [Fact]
        public void Full_trust_still_arrives_at_exactly_two_hundred_samples()
        {
            // COLD START IS NOT LONGER - the owner's own question when this was proposed. The fade runs
            // BEFORE the scale sample count, not after it, so the hand-over completes where it always did.
            var learner = new KeyedScaleLearner();
            var rng = new Random(20260925);
            for (int i = 0; i < KeyedScaleLearner.CalibrationConfidenceScaleSamples; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 70.0 + (rng.NextDouble() - 0.5) * 14.0);

            double confidence = learner.CeilingHandoverConfidence(Game, Car, Source);
            _out.WriteLine($"hand-over confidence at {KeyedScaleLearner.CalibrationConfidenceScaleSamples} samples: {confidence:F4}");
            Assert.Equal(1.0, confidence, 6);
        }

        [Fact]
        public void Nothing_below_the_fade_start_changes_at_all()
        {
            // The first FullTrustFadeStartSamples observations must reproduce the PREVIOUS behaviour value
            // for value - this fix is a pure addition on the tail, not a retune of the early ramp. Checked
            // against ColdWarmBlend directly, which is the formula the old code used unmodified below 200.
            var learner = new KeyedScaleLearner();
            var rng = new Random(20260925);
            for (int i = 0; i < KeyedScaleLearner.FullTrustFadeStartSamples; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 70.0 + (rng.NextDouble() - 0.5) * 14.0);

            double reported = learner.CeilingHandoverConfidence(Game, Car, Source);
            double unfaded = ColdWarmBlend.ConcaveHotWeight(
                KeyedScaleLearner.FullTrustFadeStartSamples,
                learner.DispersionCoefficientOfVariationForTesting(Game, Car, Source),
                KeyedScaleLearner.CalibrationConfidenceScaleSamples);

            _out.WriteLine($"at {KeyedScaleLearner.FullTrustFadeStartSamples} samples: reported {reported:F6}, unfaded {unfaded:F6}");
            Assert.Equal(unfaded, reported, 6);
            Assert.True(reported < 1.0, "the fade must not have started yet at its own start count");
        }

        [Fact]
        public void The_fade_only_ever_raises_the_weight_never_lowers_it()
        {
            // The owner's own framing check: "the first 150 get less weight?" - no. Inside the fade window
            // the weight is strictly HIGHER than the old plateau, and identical everywhere else. Nothing
            // anywhere is trusted less than it was.
            var learner = new KeyedScaleLearner();
            var rng = new Random(20260925);
            for (int i = 0; i < 175; i++)
                learner.ObserveAtPhysicalLimit(Game, Car, Source, 70.0 + (rng.NextDouble() - 0.5) * 14.0);

            double reported = learner.CeilingHandoverConfidence(Game, Car, Source);
            double unfaded = ColdWarmBlend.ConcaveHotWeight(175,
                learner.DispersionCoefficientOfVariationForTesting(Game, Car, Source),
                KeyedScaleLearner.CalibrationConfidenceScaleSamples);

            _out.WriteLine($"at 175 samples: faded {reported:F4} vs old plateau {unfaded:F4}");
            Assert.True(reported > unfaded, "the fade must raise the mid-window weight, not lower it");
            Assert.True(reported < 1.0, "and must not reach full trust early");
        }

        [Fact]
        public void The_fade_boundaries_are_pinned()
        {
            // A drift in either constant changes both the cold-start duration and the step size, which is
            // exactly the pair this fix balances.
            Assert.Equal(150, KeyedScaleLearner.FullTrustFadeStartSamples);
            Assert.Equal(200, KeyedScaleLearner.CalibrationConfidenceScaleSamples);
            Assert.True(KeyedScaleLearner.FullTrustFadeStartSamples < KeyedScaleLearner.CalibrationConfidenceScaleSamples,
                "the fade must run BEFORE full trust, not after it - ending after would lengthen cold start");
        }
    }
}
