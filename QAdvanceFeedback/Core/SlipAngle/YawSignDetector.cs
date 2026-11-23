using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Learns, per game, the sign <c>k ∈ {+1, −1}</c> that makes the game's yaw rate agree with its
    /// lateral acceleration: in a steady corner <c>lateral G ≈ k · V · r / g</c> (ruling R12; spike:
    /// <c>FeedbackData.YawChangeVelocity</c> is deg/s but its sign is not uniform across readers, and
    /// the lateral axis sign is itself a per-game convention, so only their RELATIVE sign is learnable).
    /// <para/>
    /// EVIDENCE: a frame votes only when it is not airborne, |V| is at least <see cref="MinSpeedKmh"/>,
    /// |lateral G| is at least <see cref="MinLateralG"/>, |V·r| is at least <see cref="MinCentripetalG"/>
    /// (a near-zero yaw rate carries no sign), and every input and dt is finite and positive. Each
    /// voting frame adds its dt to "agree" (sign(latG) == sign(V·r), k = +1) or "disagree" (k = −1).
    /// <para/>
    /// DECISION AND HYSTERESIS (our call, per the ruling): evidence is collected in windows of
    /// <see cref="EvidenceWindowSeconds"/> of voting time. Before a decision, a window decides k when
    /// at least <see cref="DecideFraction"/> of it agrees on one sign; otherwise it is discarded and a
    /// new window starts (mixed evidence stays undecided). After a decision, each further full window
    /// is checked: k flips only when at least <see cref="FlipFraction"/> of that window supports the
    /// opposite sign; any weaker reversal is discarded. So k never flips on less than a further 3 s of
    /// strongly reversed evidence.
    /// <para/>
    /// Pure: no SimHub types, never throws. Session-only (not persisted).
    /// </summary>
    public sealed class YawSignDetector
    {
        /// <summary>Voting time per evidence window (ruling: at least 3 s).</summary>
        public const double EvidenceWindowSeconds = 3.0;

        /// <summary>Frames below this |lateral G| do not vote (ruling: 0.3 g).</summary>
        public const double MinLateralG = 0.3;

        /// <summary>Frames below this |forward speed| do not vote (moving; same floor as learning).</summary>
        public const double MinSpeedKmh = 30.0;

        /// <summary>Frames whose |V·r| is below this (in g) do not vote: the yaw rate is too small to
        /// have a meaningful sign.</summary>
        public const double MinCentripetalG = 0.1;

        /// <summary>Share of a window that must agree to make the first decision.</summary>
        public const double DecideFraction = 0.7;

        /// <summary>Share of a window that must support the opposite sign to flip a decided k.</summary>
        public const double FlipFraction = 0.8;

        /// <summary>One frame never counts for more than this.</summary>
        public const double MaxFrameSeconds = 0.25;

        private const double Epsilon = 1e-9;

        private readonly Dictionary<string, State> _games = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        private sealed class State
        {
            public int Sign;
            public double AgreeSeconds;
            public double DisagreeSeconds;
        }

        /// <summary>
        /// Feeds one frame. <paramref name="lateralG"/> in g, <paramref name="forwardVelocityMps"/>
        /// in m/s (signed; reverse is negative), <paramref name="yawRateRadPerSec"/> raw-sign rad/s.
        /// </summary>
        public void Observe(string gameId, double? lateralG, double? forwardVelocityMps, double? yawRateRadPerSec,
            bool isAirborne, double dt)
        {
            if (string.IsNullOrEmpty(gameId) || isAirborne) return;
            if (!(dt > 0.0) || double.IsInfinity(dt)) return;
            if (!IsFinite(lateralG) || !IsFinite(forwardVelocityMps) || !IsFinite(yawRateRadPerSec)) return;

            double latG = lateralG.Value;
            double v = forwardVelocityMps.Value;
            double centripetalG = AccelerationUnits.Ms2ToG(v * yawRateRadPerSec.Value);
            if (Math.Abs(v) * 3.6 < MinSpeedKmh) return;
            if (Math.Abs(latG) < MinLateralG) return;
            if (Math.Abs(centripetalG) < MinCentripetalG) return;

            dt = Math.Min(dt, MaxFrameSeconds);
            if (!_games.TryGetValue(gameId, out State s))
            {
                s = new State();
                _games[gameId] = s;
            }

            if (Math.Sign(latG) == Math.Sign(centripetalG)) s.AgreeSeconds += dt;
            else s.DisagreeSeconds += dt;

            double window = s.AgreeSeconds + s.DisagreeSeconds;
            if (window < EvidenceWindowSeconds - Epsilon) return;

            double agree = s.AgreeSeconds / window;
            double disagree = s.DisagreeSeconds / window;
            if (s.Sign == 0)
            {
                if (agree >= DecideFraction) s.Sign = 1;
                else if (disagree >= DecideFraction) s.Sign = -1;
            }
            else
            {
                double opposite = s.Sign > 0 ? disagree : agree;
                if (opposite >= FlipFraction) s.Sign = -s.Sign;
            }

            s.AgreeSeconds = 0.0;
            s.DisagreeSeconds = 0.0;
        }

        /// <summary>The learned sign for <paramref name="gameId"/>; false (and k = 0) while undecided.</summary>
        public bool TryGetSign(string gameId, out int k)
        {
            k = 0;
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s) || s.Sign == 0) return false;
            k = s.Sign;
            return true;
        }

        private static bool IsFinite(double? x) => x.HasValue && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value);
    }
}
