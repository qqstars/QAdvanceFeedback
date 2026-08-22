using System;
using QAdvanceFeedback.Core;
using Xunit;

namespace QAdvanceFeedback.Tests
{
    /// <summary>
    /// Tests for <see cref="RobustBandEstimator"/> - the shared, reusable index-based pool estimator
    /// (docs\robust-auto-gforce-report.md). NO MINIMUM-SAMPLE GATE anywhere (owner's hard constraint) -
    /// <see cref="RobustBandEstimator.TryEstimate"/> answers from n=1 upward; the pool bounds themselves
    /// (see <see cref="RobustBandEstimator.ComputePoolBounds"/>) are what stay meaningful across the
    /// whole range of n instead.
    /// </summary>
    public class RobustBandEstimatorTests
    {
        private static RobustBandEstimator Make(TimeSpan? window = null, double valueMax = 10.0)
            => new RobustBandEstimator(0.0, valueMax, bucketCount: 1000, window: window);

        // ---------------------------------------------------------------------------------------
        // Pool bounds table - the owner's own SETTLED specification, verified at n = 1, 2, 5, 10, 25,
        // 50, 100, 200, 1000. Confirms the pool is NEVER empty, the single largest sample is NEVER in
        // the pool except at n=1, and expansion (when the natural pool is narrower than the minimum)
        // always goes DOWNWARD (toward smaller values), never back toward the trimmed outliers.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(1, 0, 0)]
        [InlineData(2, 1, 1)]
        [InlineData(5, 1, 4)]
        [InlineData(10, 1, 9)]
        [InlineData(25, 2, 11)]
        [InlineData(50, 3, 12)]
        [InlineData(100, 5, 14)]
        [InlineData(200, 10, 28)]
        [InlineData(1000, 50, 144)]
        public void Pool_bounds_match_the_owners_own_settled_specification(int n, int expectedStart, int expectedEnd)
        {
            RobustBandEstimator.ComputePoolBounds(n, out int start, out int end);
            Assert.Equal(expectedStart, start);
            Assert.Equal(expectedEnd, end);
        }

        [Fact]
        public void Pool_is_never_empty_for_any_n_and_excludes_the_single_largest_sample_unless_n_is_1()
        {
            for (int n = 1; n <= 2000; n++)
            {
                RobustBandEstimator.ComputePoolBounds(n, out int start, out int end);
                Assert.True(end >= start, $"n={n}: pool is empty ({start}..{end})");
                if (n > 1) Assert.True(start >= 1, $"n={n}: pool includes index 0 (the single largest sample)");
            }
        }

        [Fact]
        public void Pool_never_collapses_below_the_minimum_floor_unless_candidates_are_exhausted()
        {
            for (int n = 2; n <= 60; n++)
            {
                RobustBandEstimator.ComputePoolBounds(n, out int start, out int end);
                int poolSize = end - start + 1;
                int candidatesAvailable = n - start;
                int expectedFloor = Math.Min(candidatesAvailable, RobustBandEstimator.DefaultMinPoolSize);
                Assert.True(poolSize >= expectedFloor,
                    $"n={n}: pool size {poolSize} is below the achievable minimum {expectedFloor}");
            }
        }

        [Fact]
        public void Pool_expansion_never_moves_the_start_back_toward_trimmed_outliers()
        {
            // For every n, the start index (the top-trim/exclude boundary) must be <= what the pure
            // exclude-count formula alone would give - i.e. the minimum-pool-size floor only ever pushes
            // the END further down, never the START back up.
            for (int n = 1; n <= 2000; n += 7)
            {
                int pureExcludeCount = n == 1 ? 0 : Math.Max(1, (int)Math.Ceiling(n * RobustBandEstimator.DefaultTopTrimFraction));
                RobustBandEstimator.ComputePoolBounds(n, out int start, out _);
                Assert.True(start <= pureExcludeCount, $"n={n}: start {start} moved ABOVE the pure exclude boundary {pureExcludeCount}");
            }
        }

        [Fact]
        public void TryEstimate_answers_from_a_single_sample_no_gate()
        {
            var e = Make();
            e.Observe(DateTime.UtcNow, 5.0);
            Assert.True(e.TryEstimate(out double estimate));
            Assert.Equal(5.0, estimate, 2);
        }

        // ---------------------------------------------------------------------------------------
        // INT32 OVERFLOW GUARD (docs\stability-confidence-fix-report.md, Part 2) - mirrors
        // AdaptivePeakLearnerTests' own precedent for GripLearner/WelfordAccumulator: both
        // CurrentValidSampleCount and a single bucket's own internal count must freeze at the shared
        // cap while the pool ESTIMATE (this class has no separate "learned quantity" structure
        // alongside its own bucket sum/count, unlike GripLearner's SpeedBucket.Peak - the estimate IS
        // this state) keeps responding to new evidence.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Bucket_count_and_validcount_saturate_at_the_cap_while_the_pool_estimate_keeps_learning()
        {
            // Two wide buckets (width 5.0 each) so a large, clearly-measurable post-cap value shift can
            // land in the SAME bucket as the pre-cap evidence.
            var estimator = new RobustBandEstimator(0.0, 10.0, bucketCount: 2, window: null);
            for (int i = 0; i < RobustBandEstimator.SampleCountSaturationCap + 5; i++)
                estimator.Observe(DateTime.UtcNow, 1.0);

            Assert.Equal(RobustBandEstimator.SampleCountSaturationCap, estimator.CurrentValidSampleCount);
            Assert.True(estimator.TryEstimate(out double atCap));
            Assert.Equal(1.0, atCap, 3);

            // THE LEARNING must not have frozen - further, genuinely different evidence landing in the
            // SAME (now-saturated) bucket must still move the pool estimate, proving the cap freezes
            // the COUNTER, not the bucket's own running mean. Deliberately a large post-cap sample
            // count (mirrors WelfordAccumulator's own precedent) since each individual post-cap sample
            // only carries a ~1-in-a-million weight once saturated.
            for (int i = 0; i < 150_000; i++) estimator.Observe(DateTime.UtcNow, 4.9);

            Assert.True(estimator.TryEstimate(out double afterMore));
            Assert.True(afterMore > 1.3, $"the pool estimate must keep moving after the bucket's own counter saturates: {atCap} -> {afterMore}");
            Assert.Equal(RobustBandEstimator.SampleCountSaturationCap, estimator.CurrentValidSampleCount); // still pinned
        }

        [Fact]
        public void TryEstimate_returns_false_only_when_there_are_truly_zero_valid_samples()
        {
            var e = Make();
            Assert.False(e.TryEstimate(out double estimate));
            Assert.Equal(0.0, estimate, 9);
        }

        [Fact]
        public void A_single_high_but_plausible_outlier_barely_moves_the_estimate_once_a_band_exists()
        {
            var e = Make();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 149; i++) e.Observe(t.AddMilliseconds(i), 3.0);
            Assert.True(e.TryEstimate(out double before));

            e.Observe(t.AddMilliseconds(149), 7.5); // one plausible-but-non-representative high reading
            Assert.True(e.TryEstimate(out double after));

            Assert.True(Math.Abs(after - before) < 0.2, $"a single outlier moved the estimate too much: {before} -> {after}");
        }

        [Fact]
        public void MUTATION_a_a_blind_maximum_would_report_the_outlier_but_this_estimator_does_not()
        {
            var e = Make();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 149; i++) e.Observe(t.AddMilliseconds(i), 3.0);
            e.Observe(t.AddMilliseconds(149), 7.5);

            Assert.True(e.TryEstimate(out double estimate));
            Assert.NotEqual(7.5, estimate, 2);
        }

        [Fact]
        public void Estimate_is_close_to_but_pulled_below_the_pools_own_maximum_by_the_mean()
        {
            var e = Make();
            DateTime t = DateTime.UtcNow;
            // n=100 -> pool is indices 5..14 (see the pool-bounds table above). 10 samples at 5.0 (the
            // top of the sort) plus 90 at 4.0 puts the pool boundary squarely straddling both values.
            for (int i = 0; i < 10; i++) e.Observe(t.AddMilliseconds(i), 5.0);
            for (int i = 10; i < 100; i++) e.Observe(t.AddMilliseconds(i), 4.0);

            Assert.True(e.TryEstimate(out double estimate));
            Assert.True(estimate < 5.0, "estimate should be pulled below the pure max by the mean term");
            Assert.True(estimate > 4.0, "estimate should still read closer to the max than the plain mean");
        }

        [Fact]
        public void Samples_age_out_of_the_window_by_timestamp_not_by_count()
        {
            var e = Make(window: TimeSpan.FromMinutes(2));
            DateTime t0 = DateTime.UtcNow;
            for (int i = 0; i < 50; i++) e.Observe(t0.AddSeconds(i), 5.0);
            Assert.True(e.TryEstimate(out double estimate));
            Assert.Equal(5.0, estimate, 2);

            DateTime farLater = t0.AddSeconds(49).AddMinutes(2).AddSeconds(1);
            e.Observe(farLater, 5.0); // triggers eviction; this one new sample is all that remains
            Assert.True(e.TryEstimate(out double afterAging));
            Assert.Equal(5.0, afterAging, 2); // still answers (no gate) - just from far fewer samples now
            Assert.Equal(1, e.CurrentValidSampleCount);
        }

        [Fact]
        public void MUTATION_c_a_count_based_window_would_not_age_out_stale_samples_but_the_timestamp_based_one_does()
        {
            var e = Make(window: TimeSpan.FromMinutes(2));
            DateTime t0 = DateTime.UtcNow;
            for (int i = 0; i < 50; i++) e.Observe(t0.AddSeconds(i), 8.0); // old, high-grip condition

            DateTime t1 = t0.AddSeconds(49).AddMinutes(2).AddSeconds(1);
            for (int i = 0; i < 50; i++) e.Observe(t1.AddSeconds(i), 3.0); // new, low-grip condition

            Assert.True(e.TryEstimate(out double estimate));
            Assert.True(estimate < 4.0, $"estimate {estimate} should reflect only the NEW condition");
        }

        [Fact]
        public void A_null_window_never_ages_out_evidence()
        {
            var e = Make(window: null);
            DateTime t0 = DateTime.UtcNow;
            for (int i = 0; i < 50; i++) e.Observe(t0.AddSeconds(i), 5.0);

            e.Observe(t0.AddDays(30), 5.0);
            Assert.True(e.TryEstimate(out double estimate));
            Assert.Equal(5.0, estimate, 2);
        }

        [Fact]
        public void Estimate_stays_within_the_configured_value_domain()
        {
            var e = Make(valueMax: 8.0);
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 20; i++) e.Observe(t.AddMilliseconds(i), 7.9);

            Assert.True(e.TryEstimate(out double estimate));
            Assert.InRange(estimate, 0.0, 8.0);
        }

        [Fact]
        public void Non_finite_values_are_ignored()
        {
            var e = Make();
            DateTime t = DateTime.UtcNow;
            e.Observe(t, double.NaN);
            e.Observe(t, double.PositiveInfinity);
            for (int i = 0; i < 5; i++) e.Observe(t.AddMilliseconds(i), 2.0);

            Assert.True(e.TryEstimate(out double estimate));
            Assert.Equal(2.0, estimate, 6);
        }

        [Fact]
        public void Reset_clears_all_observed_state()
        {
            var e = Make();
            DateTime t = DateTime.UtcNow;
            for (int i = 0; i < 10; i++) e.Observe(t.AddMilliseconds(i), 3.0);
            Assert.True(e.TryEstimate(out _));

            e.Reset();
            Assert.False(e.TryEstimate(out _));
            Assert.Equal(0, e.CurrentValidSampleCount);
        }

        [Fact]
        public void Construction_rejects_an_invalid_value_domain()
        {
            Assert.Throws<ArgumentException>(() => new RobustBandEstimator(5.0, 5.0, 10, null));
            Assert.Throws<ArgumentException>(() => new RobustBandEstimator(5.0, 1.0, 10, null));
        }

        [Fact]
        public void Construction_rejects_non_positive_bucket_count()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RobustBandEstimator(0.0, 10.0, 0, null));
        }

        /// <summary>Rough perf smoke test - a large number of Observe calls (well beyond any realistic
        /// 2-minute-window frame count) must still complete quickly, since each call is O(1) amortised
        /// (bucket increment + FIFO push/occasional pop) and TryEstimate is O(bucketCount), never a
        /// re-sort of raw samples.</summary>
        [Fact]
        public void A_large_number_of_observations_completes_quickly()
        {
            var e = Make(window: TimeSpan.FromMinutes(2));
            DateTime t = DateTime.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 200_000; i++) e.Observe(t.AddMilliseconds(i * 5), 5.0 + (i % 7) * 0.1);
            sw.Stop();

            Assert.True(e.TryEstimate(out _));
            Assert.True(sw.ElapsedMilliseconds < 5000, $"200k observations took {sw.ElapsedMilliseconds}ms - unexpectedly slow for an O(1)-ish per-frame operation");
        }
    }
}
