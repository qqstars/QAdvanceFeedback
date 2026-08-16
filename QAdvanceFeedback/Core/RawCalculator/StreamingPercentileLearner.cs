using System;
using System.Collections.Generic;
using System.Linq;

namespace QAdvanceFeedback.Core.RawCalculator
{
    /// <summary>
    /// A running histogram over every value it has been shown, answering two questions cheaply and
    /// without ever storing the raw sample list: "what is the mean so far" and "what value sits at
    /// percentile P". Layer 3's branch formulas (<see cref="DispatchBranchFormulas"/>,
    /// <see cref="RawCalculatorEngine"/>) use this wherever a live per-frame reading needs to be judged
    /// against the typical shape of its OWN signal, with no externally-supplied reference for what
    /// "normal" or "extreme" looks like for that signal.
    /// <para/>
    /// Every value is bucketed to <see cref="BucketDecimalPlaces"/> before counting, so memory stays
    /// bounded across a long session regardless of how many distinct floating-point values are ever
    /// observed, at a resolution fine enough that no caller here would notice the rounding. Percentiles
    /// are computed nearest-rank: sort every distinct bucket that was ever observed, walk the running
    /// counts until the requested rank is reached, and report that bucket's value.
    /// <para/>
    /// MATURITY GATE: <see cref="Percentile"/> refuses to answer (returns null) until at least
    /// <see cref="MinimumSamplesForMaturity"/> values have been observed - a percentile computed from a
    /// handful of samples describes noise, not the shape of the signal. <see cref="Average"/> has no
    /// such floor: a mean of one sample is still a real, honest mean, so it is only ever null when
    /// nothing has been observed at all. The maturity bar (200) matches this plugin's own established
    /// "how much evidence is enough" convention (<c>Normalized.GripLearner.MaturitySamples</c>) so every
    /// learner in this plugin waits for the same amount of evidence before trusting a distribution-shaped
    /// answer, rather than one part of it demanding an order of magnitude more evidence than another for
    /// no reason specific to this signal.
    /// </summary>
    internal sealed class StreamingPercentileLearner : IValueDistributionLearner
    {
        public const int MinimumSamplesForMaturity = 200;

        private const int BucketDecimalPlaces = 4;

        private readonly Dictionary<double, int> _bucketCounts = new Dictionary<double, int>();
        private double _runningSum;
        private int _totalObservations;

        public int Count => _totalObservations;

        public bool IsMature => CountOfPositiveBuckets() >= MinimumSamplesForMaturity;

        public void Observe(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            double magnitude = Math.Abs(value);
            _runningSum += magnitude;
            _totalObservations++;

            double bucket = Math.Round(magnitude, BucketDecimalPlaces);
            _bucketCounts.TryGetValue(bucket, out int countSoFar);
            _bucketCounts[bucket] = countSoFar + 1;
        }

        public double? Average() => _totalObservations == 0 ? (double?)null : _runningSum / _totalObservations;

        public double? Percentile(double percentileRank)
        {
            if (percentileRank <= 0.0) return 0.0;

            List<KeyValuePair<double, int>> sortedBuckets = PositiveBucketsAscending();
            int totalPositive = sortedBuckets.Sum(bucket => bucket.Value);
            if (totalPositive < MinimumSamplesForMaturity) return null;

            double targetRank = (totalPositive - 1) * (percentileRank / 100.0);
            int runningCount = 0;
            foreach (KeyValuePair<double, int> bucket in sortedBuckets)
            {
                runningCount += bucket.Value;
                if (targetRank < runningCount) return bucket.Key;
            }

            return sortedBuckets[sortedBuckets.Count - 1].Key;
        }

        private List<KeyValuePair<double, int>> PositiveBucketsAscending()
            => _bucketCounts.Where(entry => entry.Key > 0.0).OrderBy(entry => entry.Key).ToList();

        private int CountOfPositiveBuckets() => _bucketCounts.Where(e => e.Key > 0.0).Sum(e => e.Value);
    }
}
