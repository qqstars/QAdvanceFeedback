using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>The runtime file's single values for one <see cref="SlipAngleBalanceEstimator"/> (runtime file v15+).</summary>
    public sealed class SlipAngleBalanceState
    {
        /// <summary>The near-limit lateral G reference (speed-band scaled, cornering frames).</summary>
        public OnlineDistributionState Grip;
        /// <summary>The rear / front axle angle ratio over the near-limit frames.</summary>
        public OnlineDistributionState Ratio;
    }

    /// <summary>
    /// THE FRONT / REAR BALANCE k (owner, 2026-11-22; docs\slip-angle-design.md 9.3). k rescales the front axle angle
    /// onto the rear's before the two are combined, so each axle is judged against its own grip. It is the median
    /// rear / front axle angle over the frames where the car is NEAR ITS LIMIT, pulled toward 1 until
    /// <see cref="FullTrustFrames"/> such frames exist: <c>k = exp(w ln median)</c>, <c>w = n / FullTrustFrames</c>.
    /// <para><b>Why this and not the axles' confirmed peaks.</b> The previous k waited until BOTH axles had confirmed
    /// a peak (three consistent crossings each). On the owner's F1 log the rear crossed once in eight minutes, so k
    /// stayed 1 all session while the true balance was about 0.4: the front, which runs twice the rear's angle at the
    /// same grip, decided the output - vibrating through normal cornering and late on a rear stepping out. Replayed
    /// through this class on the owner's logs, k reaches 0.43 within F1's first hard corner (26.3 -> 27.1 s) and holds
    /// 0.39-0.49, and holds 1.00-1.10 on FH6 (true about 1.0); the axles' curve peaks need 33-35 s and wander
    /// 0.29-0.46 / 0.79-1.32. A save / restore half-way restores k exactly and carries on. Rougher early estimates (each corner's peak ratio, where each axle reaches
    /// 80-90% of the top G, the car angle - which follows the rear only, correlation 0.94 vs 0.33) were 25-60% off or
    /// swung 100-300% between corners.</para>
    /// <para><b>Near the limit</b> = a speed-band-scaled lateral G (the G the window learner itself reads, so a slow
    /// corner at its limit counts as much as a fast one with aero) at or above <see cref="NearLimitShare"/> of the
    /// G reference: the <see cref="GripPercentile"/>th percentile of the cornering frames (at least
    /// <see cref="CorneringMinG"/>, at most <see cref="ImplausibleG"/> - a spike above it is contact, not grip).</para>
    /// <para><b>Never taught by</b> (the caller passes <c>ok</c> = false): a frame the learning gate refuses (pit,
    /// replay, restart, airborne, landing wobble, a bad frame time), an impact verdict that forbids learning (contact,
    /// light or heavy, and its settling), or a slide (the body angle - a drift's large lateral G is not the balance
    /// at the limit). This class itself also refuses a front under <see cref="MinFrontDeg"/> (the ratio is
    /// meaningless near zero) and an axle above the source's angle cap (a spin).</para>
    /// <para>One per (game, car, source, surface bucket): a new car, source or surface starts again from 1 - nothing
    /// is borrowed (owner). Saved with the bucket's All-target key, so a restart carries on.</para>
    /// Not thread-safe: the data thread owns it.
    /// </summary>
    public sealed class SlipAngleBalanceEstimator
    {
        /// <summary>Chosen on the owner's logs with <see cref="MinGripSamples"/> (replayed through this class: 0.80 /
        /// 0.85 / 0.90 x 20 / 120 / 300). 0.85 / 120 reached F1's 0.43 within its first hard corner (26.3 -> 27.1 s)
        /// and held 0.39-0.49; FH6 held 1.00-1.10. A G reference trusted earlier (20) let the moderate early corners -
        /// where both axles read alike - pin k near 0.8 for most of the lap; a later one (300) waited a whole lap.</summary>
        public const double NearLimitShare = 0.85;
        public const double GripPercentile = 98.0;
        public const double CorneringMinG = 1.0;
        public const double ImplausibleG = 6.0;
        public const double MinFrontDeg = 0.3;
        public const int FullTrustFrames = 120;
        /// <summary>Cornering frames (about 2 s) before the G reference is trusted - see <see cref="NearLimitShare"/>.</summary>
        public const int MinGripSamples = 120;
        /// <summary>Near-limit frames before the median is read at all.</summary>
        public const int MinRatioSamples = 5;

        private readonly OnlineDistributionLearner _grip = new OnlineDistributionLearner();
        private readonly OnlineDistributionLearner _ratio = new OnlineDistributionLearner();
        private readonly double _nearLimitShare;
        private readonly int _minGripSamples;

        public SlipAngleBalanceEstimator() : this(NearLimitShare, MinGripSamples) { }

        /// <summary>For replays and tests only - the plugin uses the shipped constants.</summary>
        public SlipAngleBalanceEstimator(double nearLimitShare, int minGripSamples)
        {
            _nearLimitShare = nearLimitShare;
            _minGripSamples = minGripSamples;
        }

        /// <summary>Near-limit frames counted so far.</summary>
        public long NearLimitFrames => _ratio.Count;

        /// <summary>True once the median is read at all (<see cref="MinRatioSamples"/> near-limit frames).</summary>
        public bool HasEvidence => _ratio.Count >= MinRatioSamples;

        /// <summary>
        /// One frame. <paramref name="frontDeg"/> / <paramref name="rearDeg"/> are the UNSCALED axle angles (NaN: the
        /// axle is unavailable - nothing is learned); <paramref name="scaledLatG"/> the speed-band-scaled |lateral G|;
        /// <paramref name="ok"/> false for any frame that must not teach (see the class remarks).
        /// </summary>
        public void Observe(double frontDeg, double rearDeg, double scaledLatG, double maxAxleDeg, bool ok)
        {
            if (!ok || !ClampMath.IsFinite(frontDeg) || !ClampMath.IsFinite(rearDeg) || !ClampMath.IsFinite(scaledLatG)) return;
            if (scaledLatG > ImplausibleG) return;
            if (frontDeg > maxAxleDeg || rearDeg > maxAxleDeg) return;
            if (scaledLatG >= CorneringMinG) _grip.AddValue(scaledLatG);
            double? top = _grip.GetPercentile(GripPercentile, _minGripSamples);
            if (!top.HasValue || scaledLatG < _nearLimitShare * top.Value) return;
            if (frontDeg < MinFrontDeg || rearDeg < 0.0) return;
            _ratio.AddValue(rearDeg / frontDeg);
        }

        /// <summary>k for this key: <paramref name="start"/> (1 by default; another surface's k when borrowed - owner,
        /// 2026-11-22) with no near-limit evidence, then the median ratio, pulled toward the start until
        /// <see cref="FullTrustFrames"/> frames (in the log domain) - clamped to [<paramref name="kMin"/>, <paramref name="kMax"/>].</summary>
        public double K(double kMin, double kMax, double start = 1.0)
        {
            double s = ClampMath.IsFinite(start) && start > 0.0 ? ClampMath.Clamp(start, kMin, kMax) : 1.0;
            double? median = _ratio.GetPercentile(50.0, MinRatioSamples);
            if (!median.HasValue || !(median.Value > 0.0)) return s;
            double target = ClampMath.Clamp(median.Value, kMin, kMax);
            double w = ClampMath.To01((double)_ratio.Count / FullTrustFrames);
            return ClampMath.Clamp(Math.Exp(w * Math.Log(target) + (1.0 - w) * Math.Log(s)), kMin, kMax);
        }

        public SlipAngleBalanceState Export() =>
            new SlipAngleBalanceState { Grip = _grip.ExportState(), Ratio = _ratio.ExportState() };

        /// <summary>Restores <see cref="Export"/>; a null or malformed part starts that part fresh.</summary>
        public void Import(SlipAngleBalanceState state)
        {
            _grip.Reset();
            _ratio.Reset();
            if (state == null) return;
            if (state.Grip == null || !_grip.ImportState(state.Grip)) _grip.Reset();
            if (state.Ratio == null || !_ratio.ImportState(state.Ratio)) _ratio.Reset();
        }
    }
}
