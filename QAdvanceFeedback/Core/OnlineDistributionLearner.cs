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

        /// <summary>INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2 - the
        /// counter-saturation follow-up to docs\adaptive-peak-learner-report.md's own Part 1) -
        /// <see cref="_count"/> is an unguarded <c>int</c> incremented once per
        /// <see cref="AddValue(double,double)"/> call and is exactly what <see cref="Count"/> reports to
        /// every caller (<c>KeyedScaleLearner.LearnedCeilingForKey</c>'s own
        /// <see cref="ColdWarmBlend.ConcaveHotWeight"/> call, most notably - a negative wrap there would
        /// silently collapse a fully-earned calibration weight back toward zero). Saturating at
        /// 1,000,000 (identical cap to <see cref="Normalized.GripLearner.SampleCountSaturationCap"/>/
        /// <see cref="WelfordAccumulator.CountSaturationCap"/> - the same shared, ample-headroom
        /// figure, not a new one) stops the COUNTER, never the learning: <see cref="_histogram"/> and
        /// the decaying weighted-average state below (<see cref="_decayedWeightedSum"/>/
        /// <see cref="_decayedWeight"/>) are entirely independent fields, updated unconditionally on
        /// every call regardless of whether this cap has been reached - <see cref="GetAverage"/>/
        /// <see cref="GetPercentile"/> keep moving exactly as before.</summary>
        public const int SampleCountSaturationCap = 1_000_000;

        /// <summary>
        /// The bucketed distribution, holding DECAYING weights rather than plain counts.
        /// <para/>
        /// WHY IT FORGETS (root-caused from the owner's own c_1_7_1_e_d capture). A purely cumulative
        /// histogram makes every percentile a one-way ratchet: once a high tail exists it stays inside
        /// the top 1% until swamped by roughly a HUNDRED times as many lower samples, so a ceiling that
        /// settled high could never come back down when the car, the tyres or the conditions genuinely
        /// changed. Measured before this change: a learner settled at 90 was fed ten times as much
        /// evidence at 30 and did not move at all. That is the same one-way-ratchet failure this project
        /// already rejected in <c>ReferencedDistributionLearner.OwnKeyExportDamping</c>'s own remarks.
        /// <para/>
        /// HOW, WITHOUT AN O(n) PASS PER SAMPLE. Decaying every bucket on every fold-in would be a full
        /// dictionary walk at 60fps. Instead the INCOMING weight grows geometrically
        /// (<see cref="_weightScale"/>), which is mathematically identical to decaying everything already
        /// stored, and the whole table is renormalised only when that scale would threaten precision.
        /// </summary>
        private readonly Dictionary<double, double> _histogram = new Dictionary<double, double>();

        /// <summary>The weight the NEXT fold-in carries. Grows by 1/<see cref="HistogramDecayPerSample"/>
        /// per sample so that older entries are, in relative terms, decayed - see
        /// <see cref="_histogram"/>.</summary>
        private double _weightScale = 1.0;

        /// <summary>Per-sample retention for the histogram. 0.99995 gives an effective window of roughly
        /// 20,000 samples (about five minutes of engaged driving at 60fps).
        /// <para/>
        /// DELIBERATELY MUCH SLOWER than <see cref="WeightedAverageDecayPerSample"/> (0.997, ~330
        /// samples). A mean converges on a few hundred samples; a 99th percentile needs roughly a hundred
        /// times that many before its tail holds enough samples to be an estimate rather than a single
        /// reading. Using the mean's rate here would make the ceiling track noise.</summary>
        private const double HistogramDecayPerSample = 0.99995;

        /// <summary>Renormalisation threshold for <see cref="_weightScale"/> - far below double's range,
        /// so the scale never approaches overflow or loses precision against the stored weights.</summary>
        private const double WeightScaleRenormalisationLimit = 1e12;

        /// <summary>
        /// Converts the histogram's total stored weight into an EQUIVALENT RECENT SAMPLE COUNT, which is
        /// the unit the readiness bar in <see cref="GetPercentile"/> is expressed in.
        /// <para/>
        /// The k-th fold-in was stored with weight <c>(1/d)^k</c>, so after n samples the newest weighs
        /// <c>(1/d)^(n-1)</c>, which is <c>_weightScale * d</c>. Dividing the total by that renormalises
        /// the series so the newest sample counts as 1, giving <c>(1-d^n)/(1-d)</c> - which is
        /// approximately n while the history is short, and saturates at <c>1/(1-d)</c> (20,000 here) once
        /// it is long. That saturation IS the effective window.
        /// </summary>
        private double EquivalentSampleCount(double totalWeight)
            => totalWeight / (_weightScale * HistogramDecayPerSample);
        private double _sum;
        private int _count;

        // ---- WEIGHTED, DECAYING AVERAGE (docs\regression-fix-report.md - the sample-threshold
        // follow-up). Only <see cref="KeyedScaleLearner"/>'s PRIMARY (physically-anchored) tier
        // (`_physicalAnchor`) ever calls the weighted overload of <see cref="AddValue"/> - the SECONDARY
        // (percentile) tier still calls the unweighted one (weight 1.0), and neither `_histogram`/
        // `_count` above nor <see cref="GetPercentile"/> are touched by any of this - they still answer
        // exactly as before. `_decayedWeightedSum`/`_decayedWeight` track a DECAYING weighted mean (an
        // exponential moving average, not a plain cumulative one): a NEW observation with its own trust
        // WEIGHT (see <see cref="Normalized.GripLearner.HotEvidenceWeight"/>) always has a bounded,
        // non-vanishing influence on <see cref="GetAverage"/>, so the estimate keeps refining - and
        // keeps tracking a genuine change (different tyres, track evolution) - rather than becoming
        // ever more resistant to new evidence the longer a session runs, which a plain unweighted mean
        // over an unbounded history would do. For a CONSTANT input series (every existing test/caller
        // that feeds the same value repeatedly), this is mathematically IDENTICAL to the old plain mean
        // from the very first sample - both numerator and denominator carry the same geometric decay
        // factor, which cancels out exactly - so this is a behaviour-preserving change for every
        // pre-existing caller, and only visibly differs once the fed value actually changes over time.
        private double _decayedWeightedSum;
        private double _decayedWeight;

        /// <summary>Decay applied to the WEIGHTED average (see above) on every
        /// <see cref="AddValue(double,double)"/> call - deliberately the SAME rate
        /// <see cref="Normalized.GripLearner"/>'s own decaying peak uses
        /// (<c>GripLearner.ForgetPerSample</c>), for the same reason: slow enough that ordinary
        /// per-braking-zone variance does not visibly wander the estimate, fast enough that a genuine,
        /// sustained condition change is reflected within a handful of braking zones rather than never.
        /// (docs\robust-auto-gforce-report.md: a windowed trimmed-band swap was PROTOTYPED for
        /// <see cref="Normalized.GripLearner"/> and measurably reduced outlier sensitivity, but surfaced a
        /// regression in a different pinned cold-start invariant and was reverted - see that class's own
        /// remarks; this class was never touched.)</summary>
        private const double WeightedAverageDecayPerSample = 0.997;

        /// <summary>Total qualifying samples folded in so far (used by the caller to decide whether to
        /// keep feeding this instance at all, mirroring SimHub's own <c>CalibrationPointsAdded &lt;=
        /// 7000</c> gate at the CALL site rather than inside <see cref="AddValue"/> - see
        /// <c>RawCalculatorEngine</c>'s own feeding logic). A RAW count of every fold-in, regardless of
        /// its own weight - the weight governs how much a value CONTRIBUTES to the average, not whether
        /// it counts as "an observation happened" for this purpose or for <see cref="ColdWarmBlend.HotWeight"/>'s
        /// own separate, downstream count term.</summary>
        public int Count => _count;

        /// <summary>How many observations landed in a POSITIVE histogram bucket - the quantity
        /// <see cref="GetPercentile"/> actually gates on (<see cref="MinSamplesForPercentile"/>), which is
        /// not the same as <see cref="Count"/> (zero readings are counted there but contribute no
        /// percentile). Exposed so a caller ramping AWAY from a cold reference can measure the same
        /// evidence the percentile gate itself uses, rather than a looser proxy - see
        /// <c>Normalized.KeyedScaleLearner.Tier1ColdCeiling</c>.</summary>
        public int PositiveSampleCount
        {
            get
            {
                // The histogram now holds DECAYING weights rather than plain counts (see _histogram's
                // own remarks), so this reports the EQUIVALENT recent-sample count - what the stored
                // weight is worth against the current incoming weight - rather than a lifetime tally.
                double equivalent = EquivalentSampleCount(_histogram.Where(kv => kv.Key > 0.0).Sum(kv => kv.Value));
                return equivalent >= SampleCountSaturationCap ? SampleCountSaturationCap : (int)equivalent;
            }
        }

        /// <summary>
        /// Folds one FULLY-TRUSTED (weight 1.0) observation in - see
        /// <see cref="AddValue(double,double)"/> for the weighted overload every pre-existing caller's
        /// behaviour is unaffected by using.
        /// </summary>
        public void AddValue(double value) => AddValue(value, 1.0);

        /// <summary>
        /// Folds one observation in (SimHub's own convention: the DISTRIBUTION is built from the
        /// absolute value - see <c>CalibrationData.AddValue</c>'s own <c>Math.Abs</c>). Ignored outright
        /// if non-finite, or if <paramref name="weight"/> is non-finite/non-positive (a caller bug, not
        /// something worth corrupting the average over). Values are rounded to 4 decimal places before
        /// bucketing (bounds the histogram's own memory for a long session without materially changing
        /// any percentile a caller would observe - verified in the same replay cited in this class's own
        /// remarks). <paramref name="weight"/> scales the fold-in's contribution to BOTH
        /// <see cref="GetAverage"/>'s decaying weighted mean AND the histogram behind
        /// <see cref="GetPercentile"/>/<see cref="PositiveSampleCount"/>, so a caller that reports a
        /// continuous confidence gets a distribution weighted by that confidence rather than by raw frame
        /// count. A caller passing the default 1.0 is unaffected in every respect.
        /// </summary>
        public void AddValue(double value, double weight)
        {
            if (!ClampMath.IsFinite(value)) return;
            if (!ClampMath.IsFinite(weight) || weight <= 0.0) return;
            double abs = Math.Abs(value);
            _sum += abs;
            // INT32 OVERFLOW GUARD - see SampleCountSaturationCap's own remarks: the counter freezes,
            // never the histogram/decayed-average state below (unconditionally updated regardless).
            if (_count < SampleCountSaturationCap) _count++;

            _decayedWeightedSum = _decayedWeightedSum * WeightedAverageDecayPerSample + weight * abs;
            _decayedWeight = _decayedWeight * WeightedAverageDecayPerSample + weight;

            double bucket = Math.Round(abs, 4);
            _histogram.TryGetValue(bucket, out double existing);
            // The OBSERVATION weight multiplies the decay weight (docs\cross-channel-smax-report.md).
            // This path used to add _weightScale alone - i.e. every fold-in counted as exactly one
            // sample no matter how little the caller trusted it - which was harmless while the only
            // weighted caller fed GetAverage. It is NOT harmless now that KeyedScaleLearner reads its
            // ceiling from a percentile of the CONFIDENCE-WEIGHTED at-limit distribution: the corner-local
            // detector reports low confidence for the many approach frames and high confidence for the
            // few frames at the limit, so ignoring the weight let the approach outvote the limit by sheer
            // frame count and put the ceiling straight back onto the anti-correlated value.
            _histogram[bucket] = existing + weight * _weightScale;

            // Growing the incoming weight is equivalent to decaying everything already stored, without
            // walking the table - see _histogram's own remarks.
            _weightScale /= HistogramDecayPerSample;
            if (_weightScale > WeightScaleRenormalisationLimit) RenormaliseHistogram();
        }

        /// <summary>Decaying WEIGHTED mean of every |value| folded in so far (see this class's own
        /// remarks) - null before the first observation, mirroring the prior plain-mean behaviour's own
        /// <c>Count == 0 -&gt; null</c> (<see cref="GetPercentile"/> is the one with a maturity floor,
        /// not this).</summary>
        public double? GetAverage() => _decayedWeight <= 1e-12 ? (double?)null : _decayedWeightedSum / _decayedWeight;

        /// <summary>
        /// Nearest-rank percentile (0-100) over every STRICTLY POSITIVE bucketed value observed so far
        /// (zeros excluded - matches the <c>includeZero: false</c> default SimHub's own
        /// <c>GetSlipCalibration</c>/<c>GetPercentile</c> call sites use), or null while fewer than
        /// <paramref name="minSamples"/> qualifying (positive) samples have been observed - the
        /// caller treats null as "not ready", not as zero.
        /// <para/>
        /// <paramref name="minSamples"/> defaults to <see cref="MinSamplesForPercentile"/>, which is
        /// SimHub's own bar for the RAW slip band - a standalone consumer, where a noisy early percentile
        /// would reach the output unattenuated. A caller whose own value is already evidence-weighted may
        /// pass a lower bar: <c>Normalized.KeyedScaleLearner</c> does, because the ceiling it derives from
        /// this is blended toward its cold anchor by a dispersion-weighted ramp, so an early, thin
        /// percentile is damped rather than trusted outright. Requiring 500 there would have been a large
        /// regression in how quickly a car calibrates.
        /// </summary>
        /// <summary>Rescales every stored weight back down so <see cref="_weightScale"/> returns to 1.0.
        /// Purely a numeric-hygiene step: every weight is divided by the same factor, so no percentile
        /// this class reports changes across it.</summary>
        /// <summary>
        /// Weight below which a bucket is dropped at renormalisation time, as a fraction of the
        /// distribution's total weight.
        /// <para/>
        /// MULTI-YEAR MEMORY GUARD. Buckets are values rounded to 4 decimals over a 0-100 source scale,
        /// so this table can hold up to ~1,000,001 distinct entries per key - and there is one learner
        /// per (game, car, source, surface). Nothing ever removed an entry: a bucket touched once, years
        /// ago, stayed forever. Renormalisation runs roughly every 552,000 folds (~2.5 hours at 60fps),
        /// which is the natural place to sweep.
        /// <para/>
        /// 1e-9 is far below anything a percentile can resolve. The effective window is ~20,000 samples
        /// (<see cref="HistogramDecayPerSample"/>), so a bucket last touched 500,000 samples ago has
        /// already decayed to ~1.4e-11 of current weight - a hundred times below this threshold. Dropping
        /// it cannot move <see cref="GetPercentile"/>; keeping it only costs memory forever.
        /// </summary>
        private const double PruneWeightFraction = 1e-9;

        private void RenormaliseHistogram()
        {
            double total = 0.0;
            foreach (double weight in _histogram.Values) total += weight;
            double floor = total * PruneWeightFraction;

            foreach (double bucket in _histogram.Keys.ToList())
            {
                double rescaled = _histogram[bucket] / _weightScale;
                // Compare BEFORE rescaling (floor is on the same pre-rescale scale as the stored value),
                // then either drop the bucket or write the rescaled weight back.
                if (_histogram[bucket] <= floor) _histogram.Remove(bucket);
                else _histogram[bucket] = rescaled;
            }
            _weightScale = 1.0;
        }

        public double? GetPercentile(double percentile, int minSamples = MinSamplesForPercentile)
        {
            if (percentile <= 0.0) return 0.0;

            List<KeyValuePair<double, double>> positive = _histogram.Where(kv => kv.Key > 0.0).OrderBy(kv => kv.Key).ToList();
            if (positive.Count == 0) return null;

            // Weights are relative, so readiness is measured against the CURRENT incoming weight rather
            // than against a raw count - "how many recent samples is this distribution worth".
            double totalWeight = positive.Sum(kv => kv.Value);
            if (EquivalentSampleCount(totalWeight) < minSamples) return null;

            double pos = (totalWeight - 1) * (percentile / 100.0) + 1.0;
            if (pos <= 1.0) return positive[0].Key;
            if (pos >= totalWeight) return positive[positive.Count - 1].Key;

            double targetRank = pos - 1;
            double acc = 0.0;
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
            _weightScale = 1.0;
            _histogram.Clear();
            _sum = 0.0;
            _count = 0;
            _decayedWeightedSum = 0.0;
            _decayedWeight = 0.0;
        }
    }
}
