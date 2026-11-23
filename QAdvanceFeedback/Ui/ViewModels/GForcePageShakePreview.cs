using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.ShakeItEffects;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>
    /// The data of the G-Force tab's left/right preview graph (v1.0.8): ONE SECOND of the shake as the
    /// current settings would produce it - the same <see cref="GForceShake.FeelingPair"/> the engine calls,
    /// at the configured frequency, hold and feeling, so the picture cannot drift from the sound.
    /// <para/>
    /// Unit coordinates: X = seconds (0..1), Y = pad level (0..1, 1 = full). The view
    /// (<c>GForcePageShakePreviewGraph</c>) maps them to pixels and draws the axes. Pure, WPF-free.
    /// </summary>
    public sealed class GForcePageShakePreview
    {
        /// <summary>Samples across the second (401 points), as the old page drew.</summary>
        public const int Samples = 400;

        /// <summary>An empty frequency box draws at 5 Hz, an empty hold box at 0 (the old page's fallbacks).</summary>
        public const double FallbackFrequencyHz = 5.0;

        private GForcePageShakePreview(IReadOnlyList<GraphPoint> left, IReadOnlyList<GraphPoint> right)
        {
            Left = left;
            Right = right;
        }

        /// <summary>The left pad's trace (green).</summary>
        public IReadOnlyList<GraphPoint> Left { get; }

        /// <summary>The right pad's trace (red).</summary>
        public IReadOnlyList<GraphPoint> Right { get; }

        /// <param name="frequencyHz">The frequency box (null = empty).</param>
        /// <param name="holdPercent">The hold box in percent (null = empty).</param>
        public static GForcePageShakePreview Build(double? frequencyHz, double? holdPercent, ShakeFeeling feeling)
        {
            double frequency = frequencyHz ?? FallbackFrequencyHz;
            double hold = (holdPercent ?? 0.0) / 100.0;
            double effectiveHold = GForceShake.EffectiveHold(hold, feeling);

            var left = new GraphPoint[Samples + 1];
            var right = new GraphPoint[Samples + 1];
            for (int i = 0; i <= Samples; i++)
            {
                double seconds = i / (double)Samples;   // exactly one second
                GForceShake.FeelingPair(frequency, seconds, effectiveHold, feeling, out double l, out double r);
                left[i] = new GraphPoint(seconds, l);
                right[i] = new GraphPoint(seconds, r);
            }
            return new GForcePageShakePreview(left, right);
        }
    }
}
