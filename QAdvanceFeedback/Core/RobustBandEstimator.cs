using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// SHARED, PURE ROBUST-LIMIT ESTIMATOR (docs\robust-auto-gforce-report.md) - answers "what is a
    /// representative UPPER value for this signal" from a stream of noisy samples, without ever
    /// trusting the single highest reading seen, and WITHOUT ANY MINIMUM-SAMPLE GATE (owner's explicit,
    /// hard constraint - a fixed maturity bar was tried and removed once already in this plugin's own
    /// history for a related learner because real sessions never reliably reached it; this estimator
    /// must answer usefully from the very first sample, always).
    /// <para/>
    /// THE ALGORITHM - INDEX-BASED POOL SELECTION (owner's own design, refined against two measured
    /// flaws in the owner's own literal formula - see <see cref="ComputePoolBounds"/>'s own remarks):
    /// <list type="number">
    /// <item>Sort the current VALID samples (caller has already gated plausibility/session-validity)
    /// DESCENDING - index 0 is the largest.</item>
    /// <item>Select a POOL by index: skip roughly the top <see cref="TopTrimFraction"/> (outliers), take
    /// a band down to roughly <see cref="TopTrimFraction"/> + <see cref="PoolBandFraction"/> - but never
    /// narrower than <see cref="MinPoolSize"/> samples (or all of them, whichever is smaller) - see
    /// <see cref="ComputePoolBounds"/> for why an absolute floor is required.</item>
    /// <item>Report <see cref="MaxMeanBlendWeight"/> of the pool's own MAXIMUM blended with the pool's
    /// own MEAN - "very close to the largest value in the pool, but still impacted by the average" (the
    /// owner's own phrasing).</item>
    /// </list>
    /// <para/>
    /// WHY AN ABSOLUTE MINIMUM POOL WIDTH (owner's own Flaw 1, confirmed by working the arithmetic
    /// through): the owner's own literal formula (skip ~5%, take to ~14.5%) collapses to a POOL OF ONE -
    /// THE OUTLIER ITSELF - for n=10 (and stays a 1-2 sample pool through roughly n=40), which is exactly
    /// the outlier-sensitive behaviour trimming exists to prevent. <see cref="MinPoolSize"/> (10) is an
    /// ABSOLUTE floor the pool width never goes below, so it only ever starts shrinking toward the pure
    /// percentage band once n is large enough that the percentage band itself already exceeds 10 (around
    /// n~105) - see <see cref="ComputePoolBounds"/>'s own worked table (n=1,5,10,25,50,100,200,1000),
    /// MEASURED against this plugin's own captured logs (a single injected collision-adjacent spike moved
    /// this estimator by +0.001 to +0.010g at n=20/50/150 - see docs\robust-auto-gforce-report.md -
    /// against a decaying-maximum EMA's own +0.4 to +0.9g on the SAME data).
    /// <para/>
    /// WHY THE POOL EXPANDS DOWNWARD, NEVER BACK TOWARD THE TRIMMED OUTLIERS (owner's own Flaw 2): when
    /// <see cref="MinPoolSize"/> forces the pool wider than the raw percentage band would have given,
    /// <see cref="ComputePoolBounds"/> only ever extends the pool's END further down (toward smaller
    /// values / higher indices) - the START (the top-trim boundary) never moves back up toward the
    /// outliers that were deliberately skipped. Verified against this plugin's own captured logs
    /// (2949-7107 valid samples per session/direction) at n=20/50/150 above - expanding upward instead
    /// would have pulled exactly the discarded high readings back in, reproducing the blind-maximum
    /// failure mode this class exists to prevent, most aggressively at the small n where the estimate is
    /// already least reliable.
    /// <para/>
    /// n &lt;= <see cref="MinPoolSize"/>: the pool IS every valid sample (nothing is trimmed at all) -
    /// this is what makes n=1 answer with that one sample directly, with no special-cased "use everything
    /// below 5 samples" branch needed (the owner's own suggested carve-out falls out of the SAME
    /// mechanism that handles n=10..~40).
    /// <para/>
    /// RETENTION IS A PARAMETER, NOT A CONSTANT (owner's own explicit design point, unaffected by the
    /// above): <paramref name="window"/> - see the constructor's own remarks - is what still differs
    /// per call site (a bounded, sliding window for a signal whose true value can genuinely change over
    /// a session, unbounded/long for a signal that is a fixed property of its own source).
    /// <para/>
    /// EFFICIENCY (this runs per telemetry frame - "sorting per frame is not acceptable"): samples are
    /// bucketed into a FIXED-SIZE array of <c>bucketCount</c> buckets spanning the configured value
    /// domain. <see cref="Observe"/> is O(1) amortised (one bucket increment, one FIFO enqueue, plus
    /// amortised O(1) eviction of samples aged out of the window - each sample enqueued/evicted exactly
    /// once over its lifetime). <see cref="TryEstimate"/> scans the bucket array once, from the highest
    /// bucket down, which is O(bucketCount) - a FIXED, small constant (independent of the sample count N
    /// or the window's duration), never an O(N log N) sort of raw samples.
    /// </summary>
    public sealed class RobustBandEstimator
    {
        /// <summary>Fraction of the current valid population skipped as the highest, least-representative
        /// outliers before the pool begins - default 5% (owner's own figure).</summary>
        public const double DefaultTopTrimFraction = 0.05;

        /// <summary>Fraction of what REMAINS after the top exclusion that forms the pool - "take the top
        /// 10% of what remains" (the owner's own settled specification). Only actually WIDENS the pool
        /// once <c>remaining * DefaultPoolBandFraction</c> itself exceeds <see cref="DefaultMinPoolSize"/> -
        /// see this class's own remarks.</summary>
        public const double DefaultPoolBandFraction = 0.10;

        /// <summary>Absolute floor on pool width, in samples - see this class's own remarks on why an
        /// absolute floor (not a pure percentage) is required for the pool to stay meaningful at small
        /// and mid sample counts. 10 - large enough that a single outlier (or even two or three) cannot
        /// dominate the pool's own mean, small enough to still respond promptly once real evidence
        /// exists.</summary>
        public const int DefaultMinPoolSize = 10;

        /// <summary>How much of the reported estimate comes from the pool's own MAXIMUM versus its MEAN -
        /// "very close to the largest value in the pool, but still impacted by the average" (the owner's
        /// own phrasing). 0.75: the pool has ALREADY excluded the top outliers by construction, so its own
        /// maximum is a safe, pre-trimmed value worth weighting heavily - but a full 25% pull toward the
        /// pool's own mean still meaningfully damps a single anomalously-high reading that survived into
        /// the pool itself, rather than reporting the pool's raw max outright (which would just move the
        /// same fragility one level down). A simple, nameable linear blend (mirroring this codebase's own
        /// <c>ColdWarmBlend.Blend</c> convention) rather than a power-mean/quantile - easier to reason
        /// about and unit-test directly, and behaves identically at the pool's own two extremes (a
        /// perfectly uniform pool: max=mean, so the blend weight does not even matter).</summary>
        public const double MaxMeanBlendWeight = 0.75;

        /// <summary>INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - shared cap
        /// for BOTH <see cref="_validCount"/> and each <see cref="_bucketCounts"/> entry, identical to
        /// <see cref="Normalized.GripLearner.SampleCountSaturationCap"/>/<see cref="WelfordAccumulator.CountSaturationCap"/>
        /// (the same figure, not a new one - ample headroom below <see cref="int.MaxValue"/> for a
        /// window that (when unbounded) could in principle accumulate indefinitely across an
        /// implausibly long single session). <see cref="_validCount"/> freezes independently (a plain
        /// population counter, nothing else's own mean depends on it). A single bucket's
        /// <see cref="_bucketCounts"/>/<see cref="_bucketSums"/> pair freezes the COUNT but keeps the
        /// SUM field moving via a fixed, tiny (1-in-a-million) Welford-style nudge once capped - see
        /// <see cref="Observe"/>'s own remarks for why this, not "freeze both", is required here (unlike
        /// <see cref="Normalized.GripLearner.SpeedBucket"/>'s own Count/SpeedSumMps pairing, THIS
        /// class has no separate "actual learned quantity" structure alongside the bucket sum/count -
        /// the pool estimate <see cref="TryEstimate"/> reports IS this state, so it must keep moving
        /// after saturation, not freeze).</summary>
        public const int SampleCountSaturationCap = 1_000_000;

        private readonly double _valueMin;
        private readonly double _valueMax;
        private readonly int _bucketCount;
        private readonly double _bucketWidth;
        private readonly TimeSpan? _window;
        private readonly double _topTrimFraction;
        private readonly double _poolBandFraction;
        private readonly int _minPoolSize;
        private readonly double _maxMeanBlendWeight;

        private readonly int[] _bucketCounts;
        private readonly double[] _bucketSums;

        private struct QueuedSample
        {
            public DateTime TimestampUtc;
            public int Bucket;
            public double Value;
        }

        private readonly Queue<QueuedSample> _order = new Queue<QueuedSample>();
        private int _validCount;

        /// <param name="valueMin">Lower bound of the value domain this instance buckets - a sample below
        /// this is clamped into bucket 0 (defensive only; a well-behaved caller has already gated
        /// implausible values before calling <see cref="Observe"/>).</param>
        /// <param name="valueMax">Upper bound of the value domain - see <paramref name="valueMin"/>. Must
        /// exceed it.</param>
        /// <param name="bucketCount">Fixed number of histogram buckets spanning [<paramref
        /// name="valueMin"/>, <paramref name="valueMax"/>] - this is the constant that bounds
        /// <see cref="TryEstimate"/>'s own per-call cost. At least 1.</param>
        /// <param name="window">The sliding time window - null means unbounded (never age out); see this
        /// class's own remarks on why retention is a parameter, not a constant.</param>
        /// <param name="topTrimFraction">See <see cref="DefaultTopTrimFraction"/>.</param>
        /// <param name="poolBandFraction">See <see cref="DefaultPoolBandFraction"/>.</param>
        /// <param name="minPoolSize">See <see cref="DefaultMinPoolSize"/>.</param>
        /// <param name="maxMeanBlendWeight">See <see cref="MaxMeanBlendWeight"/>.</param>
        public RobustBandEstimator(
            double valueMin, double valueMax, int bucketCount, TimeSpan? window,
            double topTrimFraction = DefaultTopTrimFraction, double poolBandFraction = DefaultPoolBandFraction,
            int minPoolSize = DefaultMinPoolSize, double maxMeanBlendWeight = MaxMeanBlendWeight)
        {
            if (!ClampMath.IsFinite(valueMin) || !ClampMath.IsFinite(valueMax) || valueMax <= valueMin)
                throw new ArgumentException("valueMax must exceed valueMin and both must be finite.");
            if (bucketCount < 1) throw new ArgumentOutOfRangeException(nameof(bucketCount));
            if (!(topTrimFraction >= 0.0) || topTrimFraction >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(topTrimFraction));
            if (!(poolBandFraction > 0.0) || poolBandFraction > 1.0)
                throw new ArgumentOutOfRangeException(nameof(poolBandFraction));
            if (minPoolSize < 1) throw new ArgumentOutOfRangeException(nameof(minPoolSize));
            if (!(maxMeanBlendWeight >= 0.0) || maxMeanBlendWeight > 1.0)
                throw new ArgumentOutOfRangeException(nameof(maxMeanBlendWeight));

            _valueMin = valueMin;
            _valueMax = valueMax;
            _bucketCount = bucketCount;
            _bucketWidth = (valueMax - valueMin) / bucketCount;
            _window = window;
            _topTrimFraction = topTrimFraction;
            _poolBandFraction = poolBandFraction;
            _minPoolSize = minPoolSize;
            _maxMeanBlendWeight = maxMeanBlendWeight;

            _bucketCounts = new int[bucketCount];
            _bucketSums = new double[bucketCount];
        }

        /// <summary>How many valid samples currently sit inside the window (0 outside it, if a window is
        /// configured). NOTE - lazy eviction: a sample only actually leaves the window as a side effect
        /// of a LATER <see cref="Observe"/> call (there is no background timer) - a read-only query
        /// between observations reports whatever was current as of the last <see cref="Observe"/>
        /// call.</summary>
        public int CurrentValidSampleCount => _validCount;

        /// <summary>Feeds one sample. Non-finite values are ignored. Samples must arrive in
        /// non-decreasing <paramref name="timestampUtc"/> order (true of any real telemetry stream, which
        /// is always chronological) - eviction assumes the front of the internal queue is always the
        /// oldest.</summary>
        public void Observe(DateTime timestampUtc, double value)
        {
            if (!ClampMath.IsFinite(value)) return;

            EvictExpired(timestampUtc);

            double clamped = value < _valueMin ? _valueMin : (value > _valueMax ? _valueMax : value);
            int bucket = (int)((clamped - _valueMin) / _bucketWidth);
            if (bucket < 0) bucket = 0;
            if (bucket >= _bucketCount) bucket = _bucketCount - 1;

            // INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - see
            // SampleCountSaturationCap's own remarks. While this bucket's own count is still below the
            // cap, behaviour is byte-for-byte unchanged (raw sum, normal FIFO enqueue for later
            // eviction). Once it reaches the cap, the count freezes there forever, this sample is no
            // longer individually tracked for eviction (nothing to un-count later), and _bucketSums
            // is re-purposed from "raw sum" to "this bucket's own running MEAN" (self-consistent with
            // TryEstimate's unchanged `sum / count` read, since `count` is now the frozen constant
            // SampleCountSaturationCap) - updated via the SAME fixed, tiny per-sample weight
            // WelfordAccumulator already uses ("count saturates, mean keeps updating slowly, forever").
            if (_bucketCounts[bucket] < SampleCountSaturationCap)
            {
                _bucketCounts[bucket]++;
                _bucketSums[bucket] += value;
                _order.Enqueue(new QueuedSample { TimestampUtc = timestampUtc, Bucket = bucket, Value = value });
            }
            else
            {
                _bucketSums[bucket] += value - _bucketSums[bucket] / SampleCountSaturationCap;
            }

            if (_validCount < SampleCountSaturationCap) _validCount++;
        }

        private void EvictExpired(DateTime nowUtc)
        {
            if (!_window.HasValue) return;
            // Defensive against DateTime underflow (e.g. a caller using a small synthetic epoch near
            // DateTime.MinValue, such as GripLearner's own synthetic per-observation tick) - nothing can
            // be older than DateTime.MinValue, so that is the effective cutoff floor.
            DateTime cutoff = nowUtc.Ticks >= _window.Value.Ticks ? nowUtc - _window.Value : DateTime.MinValue;
            while (_order.Count > 0 && _order.Peek().TimestampUtc < cutoff)
            {
                QueuedSample expired = _order.Dequeue();
                _bucketCounts[expired.Bucket]--;
                _bucketSums[expired.Bucket] -= expired.Value;
                _validCount--;
            }
        }

        /// <summary>
        /// Computes the inclusive, 0-indexed (descending, index 0 = largest) pool bounds for <paramref
        /// name="n"/> valid samples - the owner's own SETTLED specification (docs\robust-auto-gforce-report.md),
        /// verified against n = 1, 2, 5, 10, 25, 50, 100, 200, 1000:
        /// <list type="number">
        /// <item>Exclude the top 5% as outliers: <c>excludeCount = (n == 1) ? 0 : Max(1, Ceil(n * 0.05))</c> -
        /// always at least ONE excluded sample once there is more than one, EXCEPT n=1 (nothing to
        /// exclude from a single sample without leaving an empty pool).</item>
        /// <item>Take the top 10% of what remains: <c>poolSize = Max(1, Ceil((n - excludeCount) * 0.10))</c>.</item>
        /// <item>Guarantee a minimum pool size of <paramref name="minPoolSize"/>: if the natural pool is
        /// narrower, expand DOWNWARD (toward higher indices / smaller values, toward ordinary-but-still-
        /// high samples) - NEVER upward back into the excluded outliers - until the pool holds
        /// <paramref name="minPoolSize"/> items or the candidate list (up to index n-1) is exhausted.</item>
        /// </list>
        /// The pool is NEVER empty for any n &gt;= 1, and the single largest sample (index 0) is NEVER in
        /// the pool unless n == 1. Exposed (static, pure) so it can be unit-tested directly against the
        /// owner's own requested table without needing a populated estimator instance.
        /// </summary>
        public static void ComputePoolBounds(
            int n, out int startIndex, out int endIndexInclusive,
            double topTrimFraction = DefaultTopTrimFraction, double poolBandFraction = DefaultPoolBandFraction,
            int minPoolSize = DefaultMinPoolSize)
        {
            if (n <= 0) { startIndex = 0; endIndexInclusive = -1; return; }

            int excludeCount = n == 1 ? 0 : Math.Max(1, (int)Math.Ceiling(n * topTrimFraction));
            if (excludeCount > n) excludeCount = n; // defensive; cannot actually happen for any n >= 1

            int remaining = n - excludeCount;
            int poolSize = remaining <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(remaining * poolBandFraction));

            int start = excludeCount;
            int end = Math.Min(start + poolSize - 1, n - 1);

            // Guarantee the minimum pool size by expanding DOWNWARD (higher indices, smaller values)
            // only - never moving start back up toward the excluded outliers.
            if (end - start + 1 < minPoolSize)
                end = Math.Min(start + minPoolSize - 1, n - 1);

            startIndex = start;
            endIndexInclusive = end;
        }

        /// <summary>
        /// Answers the pool-based estimate. Returns false (with <paramref name="estimate"/> 0.0) ONLY
        /// when there are truly NO valid samples in the window at all (n=0) - there is no other gate;
        /// the caller's own configured FIXED default is the fallback for that one case (see
        /// <see cref="GForce.GForceMaxLearner"/>/<see cref="Normalized.GripLearner"/> for how each call
        /// site handles it).
        /// </summary>
        public bool TryEstimate(out double estimate)
        {
            estimate = 0.0;
            int n = _validCount;
            if (n <= 0) return false;

            ComputePoolBounds(n, out int startIndex, out int endIndexInclusive, _topTrimFraction, _poolBandFraction, _minPoolSize);

            int toSkip = startIndex;
            int toTake = endIndexInclusive - startIndex + 1;
            double poolSum = 0.0;
            int poolTaken = 0;
            double poolMax = 0.0;
            bool haveMax = false;

            for (int b = _bucketCount - 1; b >= 0 && toTake > 0; b--)
            {
                int c = _bucketCounts[b];
                if (c <= 0) continue;
                double s = _bucketSums[b];

                if (toSkip >= c)
                {
                    toSkip -= c;
                    continue;
                }

                int availableHere = c - toSkip;
                toSkip = 0;
                int takeHere = availableHere < toTake ? availableHere : toTake;

                if (!haveMax)
                {
                    // The first bucket contributing to the pool holds the pool's own maximum - use the
                    // bucket's own representative value (its mean) rather than its upper edge, consistent
                    // with the same fine-resolution bucket-approximation this class already accepts
                    // elsewhere.
                    poolMax = s / c;
                    haveMax = true;
                }

                poolSum += s * ((double)takeHere / c);
                poolTaken += takeHere;
                toTake -= takeHere;
            }

            if (poolTaken <= 0) return false;
            double poolMean = poolSum / poolTaken;
            estimate = _maxMeanBlendWeight * poolMax + (1.0 - _maxMeanBlendWeight) * poolMean;
            return true;
        }

        /// <summary>Clears every observed sample - full reset (e.g. session end), not for an ordinary key
        /// switch (each key has its own independent instance).</summary>
        public void Reset()
        {
            Array.Clear(_bucketCounts, 0, _bucketCounts.Length);
            Array.Clear(_bucketSums, 0, _bucketSums.Length);
            _order.Clear();
            _validCount = 0;
        }
    }
}
