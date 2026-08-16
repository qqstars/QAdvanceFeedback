using System;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// COLD/WARM PERSISTENCE (telemetry-integrity pass, item 3). Shared, pure mechanism behind BOTH
    /// <see cref="Normalized.GripLearner"/> (the Normalized-layer grip reference) and
    /// <see cref="Normalized.KeyedScaleLearner"/> (the per-source Raw-floor calibration) - kept here,
    /// generic and independently testable, rather than duplicated inside each learner.
    /// <para/>
    /// THE OWNER'S OWN CONSTRAINT, restated precisely: "if the hot data really helps... but if the hot
    /// data will cause more noise, then we would prefer cold data only." A noisy session accumulates
    /// SAMPLES just as fast as a clean one - sample count alone cannot tell the two apart. This class
    /// weights HOT evidence by its own DISPERSION (coefficient of variation - stddev/mean of the raw
    /// values HOT has actually observed this session, scale-invariant so the same thresholds apply
    /// whether the underlying values are g's or an arbitrary source's own native units): tight,
    /// repeatable observations earn weight quickly even from a handful of samples; scattered ones stay
    /// near zero weight no matter how many accumulate, so a genuinely noisy session converges toward
    /// PURE COLD (a reachable end state, not a mushy always-some-hot middle) rather than slowly
    /// overwhelming it by volume alone.
    /// <para/>
    /// CONTINUOUS BY CONSTRUCTION: both factors below are smooth, saturating functions of their own
    /// input (a ratio approaching 1, never a step) - there is no sample-count or dispersion THRESHOLD at
    /// which the LIVE blend jumps; <see cref="ShouldPersist"/> is the one deliberately discrete decision
    /// point in this mechanism, and it governs only whether HOT is allowed to overwrite the PERSISTED
    /// value, never the live published blend itself.
    /// <para/>
    /// HONESTLY, THE LIMITATIONS: (1) coefficient of variation from a SMALL sample is itself a noisy
    /// estimate - a handful of observations that happen to land close together can look artificially
    /// "clean" before enough evidence has accumulated to know better; this is mitigated, not eliminated,
    /// by <see cref="HotWeight"/>'s own count term still requiring some volume before the weight climbs
    /// far. (2) CV is undefined/unstable when the observed mean is near zero - guarded by returning "at
    /// least as dispersed as the cap" in that case, biasing toward cold, the conservative direction.
    /// (3) this is a heuristic, not a formal Bayesian estimator with a principled posterior - it was
    /// chosen for being simple to reason about, name constants for, and unit-test directly, not for
    /// statistical optimality.
    /// </summary>
    public static class ColdWarmBlend
    {
        /// <summary>Sample count at which the COUNT factor alone reaches 0.5 (see
        /// <see cref="HotWeight"/>: <c>count/(count+K)</c>) - chosen so a handful of braking
        /// events (this plugin's own captured-session convention: ~15-25 qualifying samples per ordinary
        /// braking zone) already carries meaningful weight, matching the owner's own acceptance bar
        /// ("fewer than ~3 full braking events should give stable, consistent output").</summary>
        public const double SampleSaturationK = 5.0;

        /// <summary>Coefficient of variation (stddev/mean of HOT's own observed values) at which the
        /// DISPERSION factor alone reaches 0.5 (see <see cref="HotWeight"/>:
        /// <c>1/(1+cv/D)</c>) - a genuinely tight, repeatable set of readings (CV a few percent) stays
        /// well above this; a scattered/erratic session (CV a third or more of its own mean) falls well
        /// below it. Our own chosen value, not derived from any external reference.</summary>
        public const double DispersionHalfLifeCv = 0.15;

        /// <summary>Minimum HOT sample count before <see cref="ShouldPersist"/> will ever allow HOT to
        /// overwrite a persisted COLD value - mirrors <see cref="Normalized.GripLearner.MinPersistSamples"/>'s
        /// own bar (a short session must not seed/overwrite a persisted profile at all).</summary>
        public const int MinSamplesForPersist = 30;

        /// <summary>Maximum coefficient of variation <see cref="ShouldPersist"/> will still accept as
        /// "clean enough to trust" - the dispersion half of the "minimum samples plus the dispersion
        /// test" gate the owner's own constraint requires (HOT must not corrupt COLD from a noisy
        /// session, however many samples it accumulates).</summary>
        public const double MaxCvForPersist = 0.20;

        /// <summary>
        /// 0..1 - how much a live blend should trust HOT over COLD, given HOT's own <paramref name="hotCount"/>
        /// (qualifying observations THIS session) and <paramref name="hotCoefficientOfVariation"/>
        /// (stddev/mean of the values actually observed). Zero samples -&gt; 0 (pure cold, by
        /// construction - see <see cref="Blend"/>'s own remarks on why this makes "no new driving
        /// reproduces the previous mapping" an exact, not approximate, guarantee). Both factors are
        /// continuous saturating curves - see this class's own remarks.
        /// </summary>
        public static double HotWeight(int hotCount, double hotCoefficientOfVariation)
        {
            if (hotCount <= 0) return 0.0;
            if (!ClampMath.IsFinite(hotCoefficientOfVariation) || hotCoefficientOfVariation < 0.0)
                hotCoefficientOfVariation = double.PositiveInfinity; // undefined/unstable -> treat as maximally dispersed (bias toward cold)

            double countTerm = hotCount / (hotCount + SampleSaturationK);
            double dispersionTerm = double.IsPositiveInfinity(hotCoefficientOfVariation)
                ? 0.0
                : 1.0 / (1.0 + hotCoefficientOfVariation / DispersionHalfLifeCv);

            return ClampMath.Clamp(countTerm * dispersionTerm, 0.0, 1.0);
        }

        /// <summary>Linear blend of <paramref name="cold"/> and <paramref name="hot"/> by
        /// <paramref name="hotWeight"/> (see <see cref="HotWeight"/>) - at weight 0 this is EXACTLY
        /// <paramref name="cold"/> (no floating-point drift from a trivially-weighted sum), which is what
        /// makes "a restart with no new driving reproduces the previous mapping" exact rather than
        /// approximate.</summary>
        public static double Blend(double cold, double hot, double hotWeight)
        {
            if (hotWeight <= 0.0) return cold;
            if (hotWeight >= 1.0) return hot;
            return cold * (1.0 - hotWeight) + hot * hotWeight;
        }

        /// <summary>Whether THIS session's HOT evidence is good enough to overwrite a persisted COLD
        /// value - both a minimum sample count AND a dispersion ceiling, per the owner's own "minimum
        /// samples plus the dispersion test" requirement. A noisy session, however long, never satisfies
        /// this; a short session, however clean, never satisfies this either - both halves are
        /// required.</summary>
        public static bool ShouldPersist(int hotCount, double hotCoefficientOfVariation)
        {
            if (hotCount < MinSamplesForPersist) return false;
            if (!ClampMath.IsFinite(hotCoefficientOfVariation) || hotCoefficientOfVariation < 0.0) return false;
            return hotCoefficientOfVariation <= MaxCvForPersist;
        }
    }

    /// <summary>
    /// A tiny, dependency-free streaming mean/variance accumulator (Welford's online algorithm) used to
    /// compute HOT's own coefficient of variation - kept separate from
    /// <see cref="RawCalculator.StreamingPercentileLearner"/>/<see cref="OnlineDistributionLearner"/>
    /// (which answer different questions - percentiles/histograms over a potentially very long session)
    /// since this only ever needs a running mean and variance, reset fresh each session.
    /// </summary>
    public struct WelfordAccumulator
    {
        private int _count;
        private double _mean;
        private double _m2;

        public int Count => _count;
        public double Mean => _mean;

        /// <summary>Population standard deviation of every value observed so far - 0 with fewer than 2
        /// observations (nothing to disperse yet).</summary>
        public double StdDev => _count < 2 ? 0.0 : Math.Sqrt(Math.Max(0.0, _m2 / _count));

        /// <summary>Coefficient of variation (StdDev/|Mean|) - <see cref="double.PositiveInfinity"/> when
        /// undefined (fewer than 2 samples, or a mean too close to zero to divide by safely) so
        /// <see cref="ColdWarmBlend.HotWeight"/>'s own "maximally dispersed" fallback applies rather than
        /// a divide-by-near-zero spike.</summary>
        public double CoefficientOfVariation
        {
            get
            {
                if (_count < 2) return double.PositiveInfinity;
                double meanMagnitude = Math.Abs(_mean);
                if (meanMagnitude < 1e-9) return double.PositiveInfinity;
                return StdDev / meanMagnitude;
            }
        }

        public void Observe(double value)
        {
            if (!ClampMath.IsFinite(value)) return;
            _count++;
            double delta = value - _mean;
            _mean += delta / _count;
            double delta2 = value - _mean;
            _m2 += delta * delta2;
        }

        public static readonly WelfordAccumulator Empty = default;
    }
}
