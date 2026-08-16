using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// QAdvanceFeedback's OWN substitute for SimHub's internal cross-session calibration state - NOT a
    /// port of it, and NOT SimHub's own persisted, cross-session, per-(track,car) calibration data
    /// (that lives entirely inside SimHub's own process and is never exposed to a third-party plugin).
    /// Layer 3's <c>RawCalculator</c> formulas that need a rolling average/percentile of a value this
    /// plugin can itself observe (per-wheel rotation-rate/speed ratio, per-wheel raw slip ratio) learn
    /// that relationship FRESH, from telemetry this plugin already reads, entirely within the current
    /// SimHub session.
    /// <para/>
    /// WHY THIS IS A REASONABLE SUBSTITUTE, NOT A GUESS: a Python replay of this project's own captured
    /// session log, using exactly this class's own average/percentile logic fed causally (in frame
    /// order, using only each frame's own past) against the SAME telemetry this plugin reads, reproduced
    /// SimHub's real <c>Diag.Source.Lock/Slip.*</c> output closely enough to move the acceptance numbers
    /// from (braking frames) mean-error 30.4/43.4 to 5.2/9.3 - see
    /// docs\branch-dispatch-and-source-keyed-learning-report.md for the full replay. It deliberately does
    /// NOT reproduce SimHub's own bucketed-rounding histogram scheme (tuned for SimHub's own value
    /// ranges, up to the thousands) or its exact cold-start fallback formula
    /// (<c>Max(1,Max*0.9)*percentile/100</c>) - the SAME replay showed removing both changes the
    /// braking-frame numbers by nothing measurable (this class's own simpler, plain order-statistics
    /// approach is sufficient) while being far simpler to read, test, and reason about.
    /// <para/>
    /// SCOPE, DISCLOSED: this learner is SESSION-SCOPED, in-memory only - it does not persist across a
    /// SimHub restart, and it is not keyed per (game, car) the way <see cref="Normalized.KeyedGripLearner"/>
    /// is. A driver who restarts SimHub, or switches cars mid-session, gets a freshly-cold learner (for
    /// the first ~500 qualifying frames of the new session/car, <see cref="GetPercentile"/> returns null
    /// and the calling branch reads 0 rather than a fabricated number) rather than one contaminated by a
    /// different car's numbers. This was a deliberate scope decision, not an oversight - see this
    /// project's own report for why it was not chased further this pass.
    /// </summary>
    public sealed class OnlineDistributionLearner
    {
        /// <summary>Matches SimHub's own real calibration cap conceptually (its <c>CalibrationData</c>
        /// stops folding in new samples once <c>CalibrationPointsAdded</c> exceeds roughly 7000) - kept
        /// as OUR OWN constant, not a copy of SimHub's exact one, since our own bucketing differs.</summary>
        public const int MaxSamples = 7000;

        /// <summary>Minimum qualifying (nonzero) samples before <see cref="GetPercentile"/> trusts the
        /// distribution enough to answer - our own choice, deliberately not SimHub's exact 500 (matched
        /// anyway, since it validated well - see this class's own remarks), returns null below it so
        /// callers can treat "not ready yet" as "branch not engaged" rather than inventing a cold-start
        /// formula.</summary>
        public const int MinSamplesForPercentile = 500;

        private readonly Dictionary<double, int> _histogram = new Dictionary<double, int>();
        private double _sum;
        private int _count;

        /// <summary>Total qualifying samples folded in so far (used by the caller to decide whether to
        /// keep feeding this instance at all, mirroring SimHub's own <c>CalibrationPointsAdded &lt;=
        /// 7000</c> gate at the CALL site rather than inside <see cref="AddValue"/> - see
        /// <c>RawCalculatorEngine</c>'s own feeding logic).</summary>
        public int Count => _count;

        /// <summary>
        /// Folds one observation in (SimHub's own convention: the DISTRIBUTION is built from the
        /// absolute value - see <c>CalibrationData.AddValue</c>'s own <c>Math.Abs</c>). Ignored outright
        /// if non-finite. Values are rounded to 4 decimal places before bucketing (bounds the histogram's
        /// own memory for a long session without materially changing any percentile a caller would
        /// observe - verified in the same replay cited in this class's own remarks).
        /// </summary>
        public void AddValue(double value)
        {
            if (!ClampMath.IsFinite(value)) return;
            double abs = Math.Abs(value);
            _sum += abs;
            _count++;

            double bucket = Math.Round(abs, 4);
            _histogram.TryGetValue(bucket, out int existing);
            _histogram[bucket] = existing + 1;
        }

        /// <summary>Plain mean of every |value| folded in so far - null before the first observation
        /// (mirrors <c>CalibrationData.GetAverage</c>'s own <c>Count == 0 -&gt; null</c>, no minimum
        /// sample requirement beyond that - <see cref="GetPercentile"/> is the one with a maturity
        /// floor, not this).</summary>
        public double? GetAverage() => _count == 0 ? (double?)null : _sum / _count;

        /// <summary>
        /// Nearest-rank percentile (0-100) over every STRICTLY POSITIVE bucketed value observed so far
        /// (zeros excluded - matches the <c>includeZero: false</c> default SimHub's own
        /// <c>GetSlipCalibration</c>/<c>GetPercentile</c> call sites use), or null while fewer than
        /// <see cref="MinSamplesForPercentile"/> qualifying (positive) samples have been observed - the
        /// caller treats null as "not ready", not as zero.
        /// </summary>
        public double? GetPercentile(double percentile)
        {
            if (percentile <= 0.0) return 0.0;

            List<KeyValuePair<double, int>> positive = _histogram.Where(kv => kv.Key > 0.0).OrderBy(kv => kv.Key).ToList();
            int num = positive.Sum(kv => kv.Value);
            if (num < MinSamplesForPercentile) return null;

            double pos = (num - 1) * (percentile / 100.0) + 1.0;
            if (pos <= 1.0) return positive[0].Key;
            if (pos >= num) return positive[positive.Count - 1].Key;

            int targetRank = (int)pos - 1;
            int acc = 0;
            for (int i = 0; i < positive.Count; i++)
            {
                acc += positive[i].Value;
                if (targetRank < acc) return positive[i].Key;
            }
            return positive[positive.Count - 1].Key;
        }

        /// <summary>Clears all learned state - used when a caller decides a fresh cold start is
        /// warranted (e.g. an explicit "forget everything" action). NOT called on an ordinary game/car
        /// switch by anything in this plugin today - see this class's own remarks on scope.</summary>
        public void Reset()
        {
            _histogram.Clear();
            _sum = 0.0;
            _count = 0;
        }
    }
}
