using System;
using System.Collections.Generic;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// One frame's context for <see cref="SlipAngleEarlyEstimator"/>, from the frame processor: the raw lateral G
    /// (before the speed scale), the speed band's reference, whether the car is sliding, and the axle angles.
    /// </summary>
    public readonly struct SlipAngleEarlyContext
    {
        public SlipAngleEarlyContext(double rawLatG, double bandPeakG, double bandMeanG, bool bandMatured, bool bandYoung,
            bool sliding, double axleMaxDeg, bool ratioSource, double carAngleDeg = double.NaN)
        {
            CarAngleDeg = carAngleDeg;
            RawLatG = rawLatG;
            BandPeakG = bandPeakG;
            BandMeanG = bandMeanG;
            BandMatured = bandMatured;
            BandYoung = bandYoung;
            Sliding = sliding;
            AxleMaxDeg = axleMaxDeg;
            RatioSource = ratioSource;
        }

        /// <summary>|lateral G| as read (not speed-scaled).</summary>
        public double RawLatG { get; }
        /// <summary>The speed band's peak lateral G (its reference while not matured); 0 when it has none.</summary>
        public double BandPeakG { get; }
        /// <summary>The band's learned mean lateral G at the limit (its reference once matured).</summary>
        public double BandMeanG { get; }
        public bool BandMatured { get; }
        /// <summary>The band has under <see cref="SlipAngleSpeedBands.YoungSeconds"/> of driving: its peak is no reference yet.</summary>
        public bool BandYoung { get; }
        /// <summary>The car's body angle reached <see cref="SlipAngleEarlyEstimator.SlideBodyAngleDeg"/> within the last second.</summary>
        public bool Sliding { get; }
        /// <summary>The larger of the front and rear axle angles this frame, degrees.</summary>
        public double AxleMaxDeg { get; }
        /// <summary>The source is a confirmed slip ratio (0-1 or 0-100): the ratio guard and the car-angle minimum apply.</summary>
        public bool RatioSource { get; }

        /// <summary>The car's body angle this frame, degrees (NaN: unknown).</summary>
        public double CarAngleDeg { get; }

        /// <summary>
        /// THE CAR-ANGLE MINIMUM FOR A SLIP-RATIO SOURCE (owner, 2026-11-22): a crossing teaches Max only when the car angle
        /// AT the crossing is at least <see cref="SlipAngleEarlyEstimator.RatioMinCarAngleDeg"/> (not a rotation that only
        /// came after the G peak). On FH6 the crossings counted
        /// without it were flat-out sweepers at 280-420 km/h with the car barely rotating (1-5 degrees): lateral G peaked
        /// because the corner tightened, not because the tyres reached their limit, and pulled Max to about 4 degrees. At
        /// 5 degrees the remaining corners teach 10.5-11.4 degrees - FH6's measured limit (ratio 1.0-1.1). A slip-ratio
        /// source has no car-angle slide guard (the ratio guard caps large angles); an angle source is unchanged.
        /// </summary>
        public bool CarAngleTooSmallForRatio(double carAngleDeg) =>
            RatioSource && !(carAngleDeg >= SlipAngleEarlyEstimator.RatioMinCarAngleDeg);

        /// <summary>The slide guard as it applies to this source: the car angle for an angle source, never for a slip ratio.</summary>
        public bool SlideGuard => Sliding && !RatioSource;
    }

    /// <summary>
    /// FAST SLIP ANGLE LEARNING (owner, 2026-11-21; docs\slip-angle-fast-learning-report.md). Before the window's peak is
    /// confirmed, every crossing teaches a weighted estimate of Max from the first corner on, instead of waiting for
    /// three consistent corners at 85% of the curve's peak (which on F1 and FH6 never came):
    /// <list type="bullet">
    /// <item>Crossings: a <see cref="CrossingGate"/> (the learner's parameters) armed at <see cref="ArmShare"/> of the
    /// speed band's reference lateral G.</item>
    /// <item>Weight, while the band is not matured: the crossing's lateral G as a share of the band's peak -
    /// at least 85% 1.0, 75% 0.8, 65% 0.6, 55% 0.3, below 0.1; a band with no reference yet (young) 0.1; the key's very first
    /// crossing at most 0.1 (it has no reference at all). Once the band is matured: 1.0 at 85% of its learned mean,
    /// otherwise ignored.</item>
    /// <item>Slide guard: a crossing is ignored when the car's body angle reached 10 degrees in the second before it
    /// (a slide, drift or spin, whatever the source), or - for a confirmed slip-ratio source - when either axle reached
    /// 90% of full slip (18 degrees) during it.</item>
    /// <item>Max = the weighted mean of the starting value (one corner's weight) and every crossing; the total weight is
    /// renormalised at <see cref="WeightWindow"/> so later corners keep counting and nothing overflows.</item>
    /// </list>
    /// The state is persisted with the learner (<see cref="SlipAngleLearnerState"/>), so a restart continues it.
    /// Data thread only.
    /// </summary>
    public sealed class SlipAngleEarlyEstimator
    {
        public const double ArmShare = 0.40;
        public static readonly double[] TierShares = { 0.85, 0.75, 0.65, 0.55 };
        public static readonly double[] TierWeights = { 1.0, 0.8, 0.6, 0.3, 0.1 };
        public const double YoungBandWeight = 0.1;
        public const double FirstCrossingWeight = 0.1;
        public const double MaturedShare = 0.85;
        public const double SlideBodyAngleDeg = 10.0;
        public const double RatioFullSlipShare = 0.90;
        /// <summary>The smallest car angle at which a slip-ratio crossing may teach Max (owner, 2026-11-22) - see
        /// <see cref="SlipAngleEarlyContext.CarAngleTooSmallForRatio"/>.</summary>
        public const double RatioMinCarAngleDeg = 5.0;
        /// <summary>The angle the ratio guard is 90% of (owner, 2026-11-22): 15 degrees = ratio 1.5.</summary>
        public const double RatioGuardBaseDeg = 15.0;

        /// <summary>The ratio slide guard: 90% of 15 degrees = 13.5 degrees (ratio 1.35 / 135; owner, 2026-11-22 - was 90% of
        /// full slip, 18 degrees on the old scale, which on FH6 threw away the corners driven at the limit).</summary>
        public const double RatioGuardDeg = RatioFullSlipShare * RatioGuardBaseDeg;
        /// <summary>The starting value counts as one corner.</summary>
        public const double StartWeight = 1.0;
        /// <summary>Total corners' worth at which the weights are scaled back (keeps the mean, lets new corners count).</summary>
        public const double WeightWindow = 200.0;
        /// <summary>Corners' worth of evidence at which the evidence half of the confidence is full (owner, 2026-11-21:
        /// 3 reported 99% while each new corner still moved Max a quarter of the way).</summary>
        public const double WeightForFullConfidence = 10.0;
        /// <summary>Taught corners the stability half looks back over.</summary>
        public const int StabilityCorners = 5;
        /// <summary>Max moving at most this share (max - min over the mean) across the last
        /// <see cref="StabilityCorners"/> corners counts as steady; at <see cref="UnstableSpread"/> or more,
        /// stability is 0.</summary>
        public const double SteadySpread = 0.03;
        public const double UnstableSpread = 0.15;

        private static readonly int RetroFrames = ImpactGuardParameters.SlipAngle.RetroFrames;

        private readonly CrossingGate _gate;
        private readonly double _maxCapDeg;

        private double _weight;
        private double _weightedAngle;
        private long _crossings;
        private long _guarded;
        private double _startMax;
        private bool _matured;
        private readonly Queue<double> _recentMax = new Queue<double>(StabilityCorners + 1);
        // The loaded summary of the recent Max values, saved back as it was until the next taught corner.
        private bool _loadedRecentActive;
        private double _loadedRecentMean;

        // Transient: the pending crossing.
        private int _pending;
        private double _pendingG, _pendingRef, _pendingAxleMax, _pendingCarAngle;
        private bool _pendingYoung, _pendingMatured, _pendingSlide;

        public SlipAngleEarlyEstimator(double maxCapDeg)
        {
            _maxCapDeg = ClampMath.IsFinite(maxCapDeg) && maxCapDeg > 0.0 ? maxCapDeg : SlipAngleWindowLearner.MaxCeilingDeg;
            _gate = new CrossingGate(CrossingGateParameters.SlipAngle)
            {
                MaxGCollapseFractionPerSecond = 0.0,
                MinSaturatedWheelsForRejection = 0,
                WalkBackGBandFraction = 0.10,
                NonCrossingWeight = 0.0,
            };
        }

        /// <summary>Corners' worth of evidence taught so far (renormalised at <see cref="WeightWindow"/>).</summary>
        public double Weight => _weight;

        /// <summary>Crossings taught (saturating).</summary>
        public long Crossings => _crossings;

        /// <summary>Crossings the slide guard ignored (saturating; diagnostics).</summary>
        public long Guarded => _guarded;

        /// <summary>True once anything was taught.</summary>
        public bool HasEstimate => _weight > 0.0 && _startMax > 0.0;

        /// <summary>The estimated Max: the weighted mean of the starting value and the crossings (0 with no estimate).</summary>
        public double Max => HasEstimate ? Math.Min(_maxCapDeg, (StartWeight * _startMax + _weightedAngle) / (StartWeight + _weight)) : 0.0;

        /// <summary>
        /// The indicator's confidence (owner, 2026-11-21): evidence (weight over <see cref="WeightForFullConfidence"/>)
        /// x stability (how little Max moved over the last <see cref="StabilityCorners"/> taught corners), at most 0.99;
        /// 1 once <see cref="Matured"/>.
        /// </summary>
        public double Confidence => _matured ? 1.0 : Math.Min(0.99, Evidence * EffectiveStability);

        /// <summary>The stability the confidence uses (owner, 2026-11-22): <see cref="Stability"/>, or - when a corner has
        /// knocked it to 0 - the last stability above 0, so one moving corner does not reset the LED to red.</summary>
        public double EffectiveStability => Stability > 0.0 ? Stability : _validStability;

        private double _validStability;

        /// <summary>0..1: corners' worth of evidence over <see cref="WeightForFullConfidence"/>.</summary>
        public double Evidence => ClampMath.To01(_weight / WeightForFullConfidence);

        /// <summary>0..1: 1 while Max moved at most <see cref="SteadySpread"/> over the last corners, 0 at
        /// <see cref="UnstableSpread"/> or more, linear between (1 with fewer than two corners).</summary>
        public double Stability
        {
            get
            {
                double spread = RecentSpread;
                return ClampMath.To01(1.0 - (spread - SteadySpread) / (UnstableSpread - SteadySpread));
            }
        }

        /// <summary>Latched: full evidence and Max steady over the last <see cref="StabilityCorners"/> corners. Learning
        /// continues (each corner then moves Max by under a tenth of the way); only the indicator stays full.</summary>
        public bool Matured => _matured;

        private double RecentSpread
        {
            get
            {
                if (_recentMax.Count < 2) return 0.0;
                double lo = double.MaxValue, hi = double.MinValue, sum = 0.0;
                foreach (double m in _recentMax) { lo = Math.Min(lo, m); hi = Math.Max(hi, m); sum += m; }
                double mean = sum / _recentMax.Count;
                return mean > 0.0 ? (hi - lo) / mean : 0.0;
            }
        }

        private void NoteMax()
        {
            _loadedRecentActive = false;
            _recentMax.Enqueue(Max);
            while (_recentMax.Count > StabilityCorners) _recentMax.Dequeue();
            double stability = Stability;
            if (stability > 0.0) _validStability = stability;
            if (!_matured && Evidence >= 1.0 && _recentMax.Count >= StabilityCorners && RecentSpread <= SteadySpread) _matured = true;
        }

        /// <summary>The tier weight for a share of the reference (see the class remarks).</summary>
        public static double TierWeight(double share)
        {
            if (!ClampMath.IsFinite(share)) return TierWeights[TierWeights.Length - 1];
            for (int i = 0; i < TierShares.Length; i++) if (share >= TierShares[i]) return TierWeights[i];
            return TierWeights[TierWeights.Length - 1];
        }

        /// <summary>A frame that may not teach (excluded, impact, gap): the pending crossing and the gate are dropped.</summary>
        public void Cancel()
        {
            _gate.Reset();
            _pending = 0;
        }

        /// <summary>
        /// One learnable frame of this key's target angle. <paramref name="startMax"/> is the learner's current
        /// starting Max (its prior, raise-only scaled), captured on the first taught crossing.
        /// </summary>
        public void Observe(double alphaDeg, in SlipAngleEarlyContext ctx, double startMax, double dt)
        {
            double g = Math.Abs(ctx.RawLatG);
            double reference = ctx.BandMatured ? ctx.BandMeanG : ctx.BandPeakG;
            if (!ClampMath.IsFinite(alphaDeg) || alphaDeg < 0.0 || !ClampMath.IsFinite(g) || !(reference > 0.0) || !ClampMath.IsFinite(reference))
            {
                Cancel();
                return;
            }

            _gate.Observe(alphaDeg, g, g, g >= ArmShare * reference, dt, 0);

            if (_pending > 0)
            {
                _pendingSlide |= ctx.SlideGuard;
                if (ClampMath.IsFinite(ctx.AxleMaxDeg)) _pendingAxleMax = Math.Max(_pendingAxleMax, ctx.AxleMaxDeg);
                if (--_pending == 0)
                {
                    bool ratioSlide = ctx.RatioSource && _pendingAxleMax >= RatioGuardDeg;
                    bool notAtLimit = ctx.CarAngleTooSmallForRatio(_pendingCarAngle);
                    Commit(_gate.TeachingBasis(alphaDeg), startMax, _pendingSlide || ratioSlide || notAtLimit, ctx.RatioSource);
                }
            }
            if (_gate.CrossedThisFrame)
            {
                _pendingG = _gate.TeachingFallbackBasis(g);
                _pendingRef = reference;
                _pendingYoung = ctx.BandYoung;
                _pendingMatured = ctx.BandMatured;
                _pendingSlide = ctx.SlideGuard;
                _pendingAxleMax = ClampMath.IsFinite(ctx.AxleMaxDeg) ? ctx.AxleMaxDeg : 0.0;
                _pendingCarAngle = ClampMath.IsFinite(ctx.CarAngleDeg) ? ctx.CarAngleDeg : 0.0;
                _pending = RetroFrames;
            }
        }

        private void Commit(double angle, double startMax, bool slide, bool ratioSource)
        {
            if (!ClampMath.IsFinite(angle) || !(angle > 0.0)) return;
            if (slide || (ratioSource && angle >= RatioGuardDeg))
            {
                if (_guarded < long.MaxValue) _guarded++;
                return;
            }
            double share = _pendingRef > 0.0 ? _pendingG / _pendingRef : 0.0;
            double w;
            if (_pendingMatured) w = share >= MaturedShare ? 1.0 : 0.0;
            else w = _pendingYoung ? YoungBandWeight : TierWeight(share);
            if (_crossings == 0) w = Math.Min(w, FirstCrossingWeight);
            if (!(w > 0.0)) return;

            if (!(_startMax > 0.0))
            {
                if (!(startMax > 0.0) || !ClampMath.IsFinite(startMax)) return;
                _startMax = Math.Min(startMax, _maxCapDeg);
            }
            _weight += w;
            _weightedAngle += w * Math.Min(angle, _maxCapDeg);
            if (_crossings < long.MaxValue) _crossings++;
            if (_weight > WeightWindow)
            {
                double k = WeightWindow / _weight;
                _weight *= k;
                _weightedAngle *= k;
            }
            NoteMax();
        }

        public void Export(SlipAngleLearnerState s)
        {
            s.EarlyWeight = _weight;
            s.EarlyWeightedAngle = _weightedAngle;
            s.EarlyCrossings = _crossings;
            s.EarlyGuarded = _guarded;
            s.EarlyStartMax = _startMax;
            s.EarlyMatured = _matured;
            s.EarlyValidStability = _validStability;
            // Single values (owner, 2026-11-21): the stability term needs only the count, lowest, highest and mean.
            s.EarlyRecentMax = null;
            s.EarlyRecentCount = _recentMax.Count;
            if (_recentMax.Count == 0) return;
            double lo = double.MaxValue, hi = double.MinValue, sum = 0.0;
            foreach (double m in _recentMax) { lo = Math.Min(lo, m); hi = Math.Max(hi, m); sum += m; }
            s.EarlyRecentLow = lo;
            s.EarlyRecentHigh = hi;
            s.EarlyRecentMean = _loadedRecentActive ? _loadedRecentMean : sum / _recentMax.Count;
        }

        /// <summary>Rebuilds the last few Max values from their count, lowest, highest and mean: the extremes plus
        /// equal values in between that keep the mean, so <see cref="Stability"/> reads back exactly. The extreme
        /// farther from the current Max is treated as the oldest.</summary>
        private void RebuildRecent(int count, double lo, double hi, double mean)
        {
            count = Math.Min(count, StabilityCorners);
            bool ok = count > 0 && Valid(lo) && Valid(hi) && lo <= hi && Valid(mean);
            if (!ok) return;
            if (count == 1) { _recentMax.Enqueue(lo); return; }
            double current = Max;
            bool loIsOldest = Math.Abs(current - lo) >= Math.Abs(current - hi);
            double first = loIsOldest ? lo : hi, newest = loIsOldest ? hi : lo;
            _recentMax.Enqueue(first);
            double mid = count > 2 ? ClampMath.Clamp((count * mean - lo - hi) / (count - 2), lo, hi) : 0.0;
            for (int i = 0; i < count - 2; i++) _recentMax.Enqueue(mid);
            _recentMax.Enqueue(newest);
        }

        private static bool Valid(double m) => ClampMath.IsFinite(m) && m > 0.0 && m <= SlipAngleWindowLearner.MaxCeilingDeg;

        /// <summary>Restores <see cref="Export"/>'s fields; anything non-finite, negative or inconsistent starts fresh.</summary>
        public void Import(SlipAngleLearnerState s)
        {
            Cancel();
            _weight = _weightedAngle = _startMax = 0.0;
            _crossings = _guarded = 0;
            _matured = false;
            _validStability = 0.0;
            _recentMax.Clear();
            _loadedRecentActive = false;
            if (s == null) return;
            bool ok = ClampMath.IsFinite(s.EarlyWeight) && s.EarlyWeight > 0.0 && s.EarlyWeight <= WeightWindow * 2.0
                      && ClampMath.IsFinite(s.EarlyWeightedAngle) && s.EarlyWeightedAngle >= 0.0
                      && s.EarlyWeightedAngle <= s.EarlyWeight * SlipAngleWindowLearner.MaxCeilingDeg
                      && ClampMath.IsFinite(s.EarlyStartMax) && s.EarlyStartMax > 0.0 && s.EarlyStartMax <= SlipAngleWindowLearner.MaxCeilingDeg;
            if (!ok) return;
            _weight = s.EarlyWeight;
            _weightedAngle = s.EarlyWeightedAngle;
            _startMax = Math.Min(s.EarlyStartMax, _maxCapDeg);
            _crossings = Math.Max(0L, s.EarlyCrossings);
            _guarded = Math.Max(0L, s.EarlyGuarded);
            if (s.EarlyRecentMax != null)   // an older file: the values themselves
            {
                foreach (double m in s.EarlyRecentMax)
                    if (Valid(m) && _recentMax.Count < StabilityCorners) _recentMax.Enqueue(m);
            }
            else
            {
                RebuildRecent(s.EarlyRecentCount, s.EarlyRecentLow, s.EarlyRecentHigh, s.EarlyRecentMean);
                _loadedRecentActive = _recentMax.Count > 0;
                _loadedRecentMean = s.EarlyRecentMean;
            }
            _matured = s.EarlyMatured;
            _validStability = ClampMath.IsFinite(s.EarlyValidStability) ? ClampMath.To01(s.EarlyValidStability) : 0.0;
        }
    }
}
