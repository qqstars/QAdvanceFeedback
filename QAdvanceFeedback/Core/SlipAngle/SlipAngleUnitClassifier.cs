using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Works out what an unlisted native slip-angle field really is - radians, degrees, a 0..1 grip ratio or
    /// a 0..100 grip ratio (owner, 2026-11-20) - by comparing it with the car's own body angle while
    /// cornering on grip. A true tyre angle stays close to the body angle; a ratio reads far above it.
    /// <para><b>The measure.</b> On a qualifying frame, r = mean |wheel| x 57.3 / body angle in degrees (the
    /// reading taken as radians). Its time-weighted median over the session's qualifying frames decides:
    /// r &lt;= <see cref="RadiansMaxR"/> radians; &lt;= <see cref="Ratio1MaxR"/> a 0..1 ratio;
    /// &lt;= <see cref="DegreesMaxR"/> degrees; above that a 0..100 ratio. Measured on the owner's logs
    /// (2026-11-20): F1 25 (radians) 1.9; FH6 (0..1 ratio) 8.1; the same F1 values as degrees 109; the FH6
    /// values as 0..100 812 - each band edge sits about 2x from its neighbours.</para>
    /// <para><b>Qualifying frame.</b> Moving (&gt; 30 km/h, forward &gt;= 5 m/s), |lateral G| at least
    /// <see cref="MinLateralG"/>, no |lateral G| jump above <see cref="ImpactJumpG"/> (a collision), body angle
    /// within [<see cref="MinBodyDeg"/>, <see cref="MaxBodyDeg"/>] (grip, not a slide or a straight), every wheel
    /// finite. An episode is a run of such cornering ending below <see cref="EpisodeEndLateralG"/>.</para>
    /// <para><b>Verdict.</b> Once <see cref="MinQualifyingSeconds"/> of qualifying time over at least
    /// <see cref="MinEpisodes"/> episodes is in, the verdict is fixed for the session. One long corner is not
    /// enough. Pure, time-injected, never throws; not thread-safe (the data thread owns it).</para>
    /// </summary>
    public sealed class SlipAngleUnitClassifier
    {
        public const double RadiansMaxR = 4.0;
        public const double Ratio1MaxR = 28.0;
        public const double DegreesMaxR = 229.0;

        public const double MinLateralG = 0.8;
        public const double EpisodeEndLateralG = 0.5;
        public const double ImpactJumpG = 3.0;
        public const double MinSpeedKmh = 30.0;
        public const double MinForwardMps = 5.0;
        public const double MinBodyDeg = 0.3;
        public const double MaxBodyDeg = 15.0;
        public const double MinQualifyingSeconds = 5.0;
        public const int MinEpisodes = 3;
        public const double MaxFrameSeconds = 0.25;

        // Histogram of log10(r): 0.02-decade bins from 10^-2 to 10^5 (r outside lands in the end bins).
        private const double LogMin = -2.0, LogMax = 5.0, LogStep = 0.02;
        private static readonly int BinCount = (int)Math.Round((LogMax - LogMin) / LogStep);

        private sealed class State
        {
            public readonly double[] Bins = new double[BinCount];
            public double Seconds;
            public int Episodes;
            public bool InEpisode;
            public double? PreviousLatG;
            public SlipAngleUnit Verdict = SlipAngleUnit.Unverified;
        }

        private readonly Dictionary<string, State> _games = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Feeds one frame. <paramref name="raw"/> are the four values after the table's transform, in the
        /// field's own (unknown) unit; <paramref name="lateralG"/> in g; velocities in m/s (local frame).
        /// </summary>
        public void Observe(string gameId, Corners? raw, double? lateralG, double? lateralVelocityMps,
            double? forwardVelocityMps, double speedKmh, double dt)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            if (!_games.TryGetValue(gameId, out State s)) { s = new State(); _games[gameId] = s; }
            if (s.Verdict != SlipAngleUnit.Unverified) return;

            double? lat = Finite(lateralG);
            bool impact = lat.HasValue && s.PreviousLatG.HasValue && Math.Abs(lat.Value - s.PreviousLatG.Value) > ImpactJumpG;
            if (lat.HasValue) s.PreviousLatG = lat;

            double absLat = lat.HasValue ? Math.Abs(lat.Value) : 0.0;
            if (absLat < EpisodeEndLateralG && s.InEpisode) s.InEpisode = false;

            if (!(dt > 0.0) || double.IsInfinity(dt) || impact || !raw.HasValue || !lat.HasValue) return;
            dt = Math.Min(dt, MaxFrameSeconds);
            double? vlat = Finite(lateralVelocityMps), vfwd = Finite(forwardVelocityMps);
            if (!vlat.HasValue || !vfwd.HasValue || vfwd.Value < MinForwardMps || !(speedKmh > MinSpeedKmh)) return;
            if (absLat < MinLateralG) return;

            double body = Math.Atan2(Math.Abs(vlat.Value), vfwd.Value) * SlipAngleSourceScale.RadToDeg;
            if (body < MinBodyDeg || body > MaxBodyDeg) return;

            Corners c = raw.Value;
            double sum = 0.0;
            for (int i = 0; i < 4; i++)
            {
                if (double.IsNaN(c[i]) || double.IsInfinity(c[i])) return;
                sum += Math.Abs(c[i]);
            }
            double mean = sum / 4.0;
            if (!(mean > 0.0)) return;

            if (!s.InEpisode) { s.InEpisode = true; s.Episodes++; }
            double r = mean * SlipAngleSourceScale.RadToDeg / body;
            s.Bins[Bin(r)] += dt;
            s.Seconds += dt;

            if (s.Seconds >= MinQualifyingSeconds && s.Episodes >= MinEpisodes)
                s.Verdict = Classify(MedianR(s));
        }

        /// <summary>The fixed verdict for the game (false while the evidence is short).</summary>
        public bool TryGetVerdict(string gameId, out SlipAngleUnit unit)
        {
            unit = SlipAngleUnit.Unverified;
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s)) return false;
            unit = s.Verdict;
            return unit != SlipAngleUnit.Unverified;
        }

        /// <summary>Diagnostics: qualifying seconds, episodes and the current median r (NaN with none).</summary>
        public bool TryGetEvidence(string gameId, out double seconds, out int episodes, out double medianR)
        {
            seconds = 0.0; episodes = 0; medianR = double.NaN;
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s)) return false;
            seconds = s.Seconds;
            episodes = s.Episodes;
            medianR = s.Seconds > 0.0 ? MedianR(s) : double.NaN;
            return true;
        }

        /// <summary>The unit band for a median r.</summary>
        public static SlipAngleUnit Classify(double medianR)
        {
            if (!(medianR > 0.0) || double.IsInfinity(medianR)) return SlipAngleUnit.Unverified;
            if (medianR <= RadiansMaxR) return SlipAngleUnit.Radians;
            if (medianR <= Ratio1MaxR) return SlipAngleUnit.Ratio1;
            if (medianR <= DegreesMaxR) return SlipAngleUnit.Degrees;
            return SlipAngleUnit.Ratio100;
        }

        private static int Bin(double r)
        {
            int b = (int)Math.Floor((Math.Log10(r) - LogMin) / LogStep);
            return b < 0 ? 0 : (b >= BinCount ? BinCount - 1 : b);
        }

        private static double MedianR(State s)
        {
            double half = s.Seconds / 2.0, acc = 0.0;
            for (int b = 0; b < BinCount; b++)
            {
                acc += s.Bins[b];
                if (acc >= half) return Math.Pow(10.0, LogMin + (b + 0.5) * LogStep);
            }
            return Math.Pow(10.0, LogMax);
        }

        private static double? Finite(double? v) =>
            v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) ? v : null;
    }

    /// <summary>
    /// Whether a ratio source's values go over full slip (100%) - which decides the drift start (owner,
    /// 2026-11-20): one moving frame above <see cref="OverPercent"/> means "goes over" (latched again on any
    /// later sighting); <see cref="SaturationEpisodes"/> separate reaches of at least <see cref="FullPercent"/>
    /// (each ending when the value falls below <see cref="EpisodeEndPercent"/>) with no sighting above means
    /// "stops at 100%". Per game, per session. Pure; not thread-safe.
    /// </summary>
    public sealed class SlipAngleRatioRangeTracker
    {
        public const double OverPercent = 100.5;
        public const double FullPercent = 99.5;
        public const double EpisodeEndPercent = 90.0;
        public const int SaturationEpisodes = 3;
        public const double MinSpeedKmh = 30.0;

        private sealed class State
        {
            public bool Exceeded;
            public bool InEpisode;
            public int Episodes;
        }

        private readonly Dictionary<string, State> _games = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Feeds one frame of a ratio game (<paramref name="unit"/> Ratio1 or Ratio100, raw values).</summary>
        public void Observe(string gameId, Corners? raw, SlipAngleUnit unit, double speedKmh)
        {
            if (string.IsNullOrEmpty(gameId) || !raw.HasValue || !SlipAngleSourceScale.IsRatio(unit) || !(speedKmh > MinSpeedKmh)) return;
            if (!_games.TryGetValue(gameId, out State s)) { s = new State(); _games[gameId] = s; }

            double max = 0.0;
            Corners c = raw.Value;
            for (int i = 0; i < 4; i++)
            {
                if (double.IsNaN(c[i]) || double.IsInfinity(c[i])) return;
                max = Math.Max(max, Math.Abs(SlipAngleSourceScale.ToPercent(c[i], unit)));
            }
            if (max > OverPercent) { s.Exceeded = true; return; }
            if (max >= FullPercent)
            {
                if (!s.InEpisode) { s.InEpisode = true; s.Episodes++; }
            }
            else if (max < EpisodeEndPercent) s.InEpisode = false;
        }

        /// <summary>True = goes over 100%, false = stops at 100% (after enough reaches), null = not known yet.</summary>
        public bool? Exceeds(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s)) return null;
            if (s.Exceeded) return true;
            return s.Episodes >= SaturationEpisodes ? false : (bool?)null;
        }
    }
}
