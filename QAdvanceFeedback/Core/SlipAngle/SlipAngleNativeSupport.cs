using System;
using System.Collections.Generic;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Decides, per game and per session, whether the game's native per-wheel slip angle is real
    /// enough to use (docs\slip-angle-design.md §9.1 "A game counts as native only after sustained
    /// evidence", §9.2 "Unverified units"). Same promotion pattern as <see cref="KeyedTelemetrySupport"/>:
    /// evidence promotes, nothing ever demotes within a session.
    /// <para/>
    /// PROMOTION: <see cref="PromotionSeconds"/> of moving frames (speed above
    /// <see cref="MinMovingSpeedKmh"/>) whose four values are finite and all within
    /// <see cref="PlausibleMaxDeg"/> (in the game's pinned or detected unit), plus at least one frame
    /// that differs from the first such frame (a field that is always 0, or stuck, never promotes).
    /// Frames that are null, non-finite, slow, or have a non-positive/non-finite dt are ignored.
    /// <para/>
    /// THE 45° BLOCK (controller ruling, fix round 1). A frame above 45° never counts toward the
    /// 5 s. Whether it can BLOCK depends on the row:
    /// <list type="bullet">
    /// <item>Pinned unit (Drift21, LFS, the computed rF2 / LMU / NASCAR rows): &gt; 45° frames are
    /// neutral. Real drifting holds large angles for seconds and must not disable a drift title.</item>
    /// <item>Unverified unit: a &gt; 45° frame counts toward the block only while the unit is still
    /// undetermined (see below), |lateral G| is known and below <see cref="GarbageLateralG"/> (a large
    /// angle with no lateral load is garbage), and the car is not reversing (forward local velocity
    /// below 0). Such frames held back to back for more than
    /// <see cref="ImplausibleBlockSeconds"/> block that reading for the session. With load, or once the
    /// unit is determined, they are neutral (they neither count, block, nor break a run). A block
    /// already earned stays.</item>
    /// </list>
    /// <para/>
    /// UNIT AUTO-DETECTION (ruling R6; episode voting per ruling R16), for an
    /// <see cref="SlipAngleUnit.Unverified"/> row. Both readings ("degrees" and "radians") are tracked
    /// side by side. A QUALIFYING frame is a moving frame with |lateral G| at least
    /// <see cref="UnitDetectionLateralG"/>, max |wheel| at most <see cref="PlausibleMaxDeg"/>
    /// (implausible in either reading) and no frame-to-frame |lateral G| jump above
    /// <see cref="ImpactJumpG"/> (the ImpactGuard collision test). An EPISODE is a run of qualifying
    /// frames; it stays open through frames at or above <see cref="EpisodeEndLateralG"/> that do not
    /// qualify (they add nothing), and ends on a frame below <see cref="EpisodeEndLateralG"/> or with
    /// no lateral G. An ended episode with at least <see cref="MinEpisodeSeconds"/> of qualifying time
    /// votes: degrees when at least <see cref="DegreesFraction"/> of that time had max |wheel| at or
    /// above <see cref="RadiansCeiling"/>, else radians. Real cornering at 0.8 g needs several degrees
    /// of slip, so a degree-scaled field sits above 1.0 there; a radian-scaled one only does in a spin.
    /// The unit is DETERMINED once at least <see cref="MinEpisodeVotes"/> episodes have voted and at
    /// least <see cref="VoteAgreement"/> of the votes agree; it is fixed when the game promotes. Until
    /// then an unverified-unit game cannot promote (it stays on the fallback estimate);
    /// <see cref="IsUnitUndetermined"/> exposes that for diagnostics. One spin, however long, is one vote.
    /// <para/>
    /// CONSTANCY is judged per reading on counted (≤ 45°) frames only, so a stuck 90 followed by
    /// stuck zeros cannot pass as "varying".
    /// <para/>
    /// Pure: no SimHub types, never throws.
    /// </summary>
    public sealed class SlipAngleNativeSupport
    {
        /// <summary>Moving, plausible, finite time required before a game is native (brief: 5 s).</summary>
        public const double PromotionSeconds = 5.0;

        /// <summary>Frames count only above this ground speed (brief: &gt; 30 km/h).</summary>
        public const double MinMovingSpeedKmh = 30.0;

        /// <summary>Any wheel above this is implausible for a tyre slip angle (brief: 45°).</summary>
        public const double PlausibleMaxDeg = 45.0;

        /// <summary>Garbage (&gt; 45°, no lateral load) held for longer than this blocks promotion (brief: 1 s).</summary>
        public const double ImplausibleBlockSeconds = 1.0;

        /// <summary>A &gt; 45° frame below this |lateral G| is garbage, not a slide (fix round 1).</summary>
        public const double GarbageLateralG = 0.3;

        /// <summary>One frame never counts for more than this (a hitch or pause is not evidence).</summary>
        public const double MaxFrameSeconds = 0.25;

        /// <summary>Unit detection only looks at frames at or above this |lateral G| (ruling R6).</summary>
        public const double UnitDetectionLateralG = 0.8;

        /// <summary>A radian-scaled slip angle stays below this while cornering hard (ruling R6).</summary>
        public const double RadiansCeiling = 1.0;

        /// <summary>An episode ends on a frame below this |lateral G| (ruling R16).</summary>
        public const double EpisodeEndLateralG = 0.5;

        /// <summary>Episodes with less qualifying time than this do not vote (ruling R16).</summary>
        public const double MinEpisodeSeconds = 0.3;

        /// <summary>Share of an episode's qualifying time at or above <see cref="RadiansCeiling"/> that
        /// makes it vote degrees (ruling R16).</summary>
        public const double DegreesFraction = 0.7;

        /// <summary>Votes required before the unit can be determined (ruling R16).</summary>
        public const int MinEpisodeVotes = 3;

        /// <summary>Share of votes that must agree to determine the unit (ruling R16).</summary>
        public const double VoteAgreement = 2.0 / 3.0;

        /// <summary>A frame-to-frame |lateral G| change above this is an impact, not cornering
        /// (ImpactGuardParameters.SlipAngle.MaxJumpG).</summary>
        public const double ImpactJumpG = 3.0;

        private const double RadToDeg = 180.0 / Math.PI;
        private const double Epsilon = 1e-9;
        private const int AsDegrees = 0;
        private const int AsRadians = 1;

        private readonly Dictionary<string, State> _games = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        private sealed class State
        {
            public bool Promoted;
            public SlipAngleUnit PinnedUnit;
            public SlipAngleUnit DetectedUnit;
            public readonly double[] CountedSeconds = new double[2];
            public readonly double[] GarbageRunSeconds = new double[2];
            public readonly bool[] Blocked = new bool[2];
            public readonly bool[] HasReference = new bool[2];
            public readonly Corners[] Reference = new Corners[2];
            public readonly bool[] NonConstant = new bool[2];
            public double EpisodeSeconds;
            public double EpisodeAboveCeilingSeconds;
            public int VotesDegrees;
            public int VotesRadians;
            public double? PreviousLateralG;
        }

        /// <summary>Values already in degrees (a pinned-degrees game, or values converted by the caller).</summary>
        public void Observe(string gameId, Corners? wheelsDeg, double speedKmh, double dt)
            => Observe(gameId, wheelsDeg, SlipAngleUnit.Degrees, speedKmh, dt, null);

        /// <summary>
        /// Feeds one frame. <paramref name="wheels"/> are in <paramref name="unit"/> (after the table's
        /// transform); pass <see cref="SlipAngleUnit.Unverified"/> for a game whose unit must be
        /// detected, with <paramref name="lateralG"/> (in g) so detection and the garbage test can run,
        /// and <paramref name="forwardVelocityMps"/> (reversing frames never count toward the block).
        /// </summary>
        public void Observe(string gameId, Corners? wheels, SlipAngleUnit unit, double speedKmh, double dt, double? lateralG,
            double? forwardVelocityMps = null)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            if (!(dt > 0.0) || double.IsInfinity(dt)) return;
            dt = Math.Min(dt, MaxFrameSeconds);

            if (!_games.TryGetValue(gameId, out State s))
            {
                s = new State();
                _games[gameId] = s;
            }
            if (s.Promoted) return;
            if (unit != SlipAngleUnit.Unverified) s.PinnedUnit = unit;

            double? latG = lateralG.HasValue && !double.IsNaN(lateralG.Value) && !double.IsInfinity(lateralG.Value)
                ? lateralG : null;
            bool impact = latG.HasValue && s.PreviousLateralG.HasValue && Math.Abs(latG.Value - s.PreviousLateralG.Value) > ImpactJumpG;
            if (latG.HasValue) s.PreviousLateralG = latG;

            if (!wheels.HasValue || !AllFinite(wheels.Value)) return;
            if (!(speedKmh > MinMovingSpeedKmh)) return;

            Corners c = wheels.Value;
            double maxAbs = MaxAbs(c);

            if (s.PinnedUnit != SlipAngleUnit.Unverified)
            {
                // Pinned unit: > 45° frames are neutral (never count, never block, never vary).
                if (ToDegrees(maxAbs, s.PinnedUnit) <= PlausibleMaxDeg)
                {
                    s.CountedSeconds[AsDegrees] += dt;
                    TrackConstancy(s, AsDegrees, c);
                }
            }
            else
            {
                bool reversing = forwardVelocityMps.HasValue && forwardVelocityMps.Value < 0.0;
                bool garbage = !reversing && latG.HasValue && Math.Abs(latG.Value) < GarbageLateralG;
                EvaluateUnverified(s, AsDegrees, c, maxAbs, garbage, dt);
                EvaluateUnverified(s, AsRadians, c, maxAbs * RadToDeg, garbage, dt);
                TrackEpisode(s, latG, impact, maxAbs, dt);
            }

            TryPromote(s);
        }

        /// <summary>True once the game has been promoted this session; never reverts.</summary>
        public bool IsNative(string gameId)
            => !string.IsNullOrEmpty(gameId) && _games.TryGetValue(gameId, out State s) && s.Promoted;

        /// <summary>Diagnostics: true when garbage values (&gt; 45° without lateral load for &gt; 1 s)
        /// have ruled out the reading the evidence currently points to (or both readings while the
        /// unit evidence is still too short). Never true for a pinned-unit row.</summary>
        public bool IsBlocked(string gameId)
        {
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s) || s.Promoted) return false;
            if (s.PinnedUnit != SlipAngleUnit.Unverified) return false;
            if (TryCandidateUnit(s, out SlipAngleUnit candidate))
                return s.Blocked[candidate == SlipAngleUnit.Degrees ? AsDegrees : AsRadians];
            return s.Blocked[AsDegrees] && s.Blocked[AsRadians];
        }

        /// <summary>Diagnostics: true for a seen, unverified-unit game whose unit votes have not yet
        /// determined a unit (it cannot promote; Slip Angle stays on the fallback estimate).</summary>
        public bool IsUnitUndetermined(string gameId)
            => !string.IsNullOrEmpty(gameId) && _games.TryGetValue(gameId, out State s)
               && s.PinnedUnit == SlipAngleUnit.Unverified && s.DetectedUnit == SlipAngleUnit.Unverified
               && !TryCandidateUnit(s, out _);

        /// <summary>Diagnostics: the episode votes cast so far for an unverified-unit game.</summary>
        public bool TryGetUnitVotes(string gameId, out int degreesVotes, out int radiansVotes)
        {
            degreesVotes = radiansVotes = 0;
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s)) return false;
            degreesVotes = s.VotesDegrees;
            radiansVotes = s.VotesRadians;
            return true;
        }

        /// <summary>The game's unit: the pinned one, or the detected one (fixed at promotion). False
        /// (and <see cref="SlipAngleUnit.Unverified"/>) while unknown.</summary>
        public bool TryGetUnit(string gameId, out SlipAngleUnit unit)
        {
            unit = SlipAngleUnit.Unverified;
            if (string.IsNullOrEmpty(gameId) || !_games.TryGetValue(gameId, out State s)) return false;
            unit = s.PinnedUnit != SlipAngleUnit.Unverified ? s.PinnedUnit : s.DetectedUnit;
            return unit != SlipAngleUnit.Unverified;
        }

        /// <summary>Converts a value in <paramref name="unit"/> to degrees (Unverified passes through; a grip
        /// ratio becomes 20% x its 0..100 value, capped at 30 degrees - <see cref="SlipAngleSourceScale"/>).</summary>
        public static double ToDegrees(double value, SlipAngleUnit unit)
            => SlipAngleSourceScale.ToDegrees(value, unit);

        private static void EvaluateUnverified(State s, int reading, Corners c, double maxDeg, bool garbage, double dt)
        {
            if (maxDeg <= PlausibleMaxDeg)
            {
                s.GarbageRunSeconds[reading] = 0.0;
                s.CountedSeconds[reading] += dt;
                TrackConstancy(s, reading, c);
                return;
            }
            // > 45°: blocks only while the unit is undetermined, with no lateral load, not reversing; else neutral.
            if (!garbage || TryCandidateUnit(s, out _)) return;
            s.GarbageRunSeconds[reading] += dt;
            if (s.GarbageRunSeconds[reading] > ImplausibleBlockSeconds) s.Blocked[reading] = true;
        }

        /// <summary>Extends, keeps open, or ends (and maybe counts the vote of) the current episode.</summary>
        private static void TrackEpisode(State s, double? latG, bool impact, double maxAbs, double dt)
        {
            if (latG.HasValue && Math.Abs(latG.Value) >= UnitDetectionLateralG)
            {
                if (impact || maxAbs > PlausibleMaxDeg) return; // stays open, adds nothing
                s.EpisodeSeconds += dt;
                if (maxAbs >= RadiansCeiling) s.EpisodeAboveCeilingSeconds += dt;
                return;
            }
            if (latG.HasValue && Math.Abs(latG.Value) >= EpisodeEndLateralG) return; // 0.5..0.8 g: stays open

            if (s.EpisodeSeconds >= MinEpisodeSeconds - Epsilon)
            {
                if (s.EpisodeAboveCeilingSeconds >= DegreesFraction * s.EpisodeSeconds - Epsilon) s.VotesDegrees++;
                else s.VotesRadians++;
            }
            s.EpisodeSeconds = 0.0;
            s.EpisodeAboveCeilingSeconds = 0.0;
        }

        /// <summary>The unit the episode votes determine, once there are enough agreeing votes.</summary>
        private static bool TryCandidateUnit(State s, out SlipAngleUnit unit)
        {
            unit = SlipAngleUnit.Unverified;
            int total = s.VotesDegrees + s.VotesRadians;
            if (total < MinEpisodeVotes) return false;
            if (s.VotesDegrees >= VoteAgreement * total - Epsilon) unit = SlipAngleUnit.Degrees;
            else if (s.VotesRadians >= VoteAgreement * total - Epsilon) unit = SlipAngleUnit.Radians;
            return unit != SlipAngleUnit.Unverified;
        }

        private static void TryPromote(State s)
        {
            int reading = AsDegrees;
            SlipAngleUnit candidate = SlipAngleUnit.Unverified;
            if (s.PinnedUnit == SlipAngleUnit.Unverified)
            {
                if (!TryCandidateUnit(s, out candidate)) return; // unit still unknown
                reading = candidate == SlipAngleUnit.Degrees ? AsDegrees : AsRadians;
            }

            if (s.Blocked[reading] || !s.NonConstant[reading]) return;
            if (s.CountedSeconds[reading] < PromotionSeconds - Epsilon) return;

            if (candidate != SlipAngleUnit.Unverified) s.DetectedUnit = candidate;
            s.Promoted = true;
        }

        private static void TrackConstancy(State s, int reading, Corners c)
        {
            if (s.NonConstant[reading]) return;
            if (!s.HasReference[reading])
            {
                s.Reference[reading] = c;
                s.HasReference[reading] = true;
                return;
            }
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(c[i] - s.Reference[reading][i]) > Epsilon)
                {
                    s.NonConstant[reading] = true;
                    return;
                }
            }
        }

        private static bool AllFinite(Corners c)
        {
            for (int i = 0; i < 4; i++)
            {
                double v = c[i];
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            }
            return true;
        }

        private static double MaxAbs(Corners c)
            => Math.Max(Math.Max(Math.Abs(c.FrontLeft), Math.Abs(c.FrontRight)), Math.Max(Math.Abs(c.RearLeft), Math.Abs(c.RearRight)));
    }
}
