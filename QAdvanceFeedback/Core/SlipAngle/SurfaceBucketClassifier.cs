using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>The three Slip Angle surface buckets (docs\slip-angle-design.md 9.5.8). Each has its own
    /// window and its own front/rear/All learners.</summary>
    public enum SurfaceBucket
    {
        /// <summary>Tarmac / sealed.</summary>
        Sealed = 0,
        /// <summary>Gravel / dirt / grass / sand.</summary>
        Loose = 1,
        /// <summary>Snow / ice.</summary>
        LowGrip = 2,
    }

    /// <summary>Where the classifier's current candidate came from (diagnostics).</summary>
    public enum SurfaceBucketSource
    {
        /// <summary>The game reports no usable surface this frame: UNKNOWN - never guessed (owner, 2026-11-22). The
        /// bucket is not switched: a game that never reports a surface keeps learning in one bucket (Sealed, the
        /// starting one).</summary>
        Unknown = 0,
        /// <summary>The game's own per-wheel surface field, translated by its source row (majority vote, see
        /// <see cref="SurfaceBucketMap.Vote"/>).</summary>
        Game = 1,
        /// <summary>SimHub's per-wheel grass/gravel flag (<see cref="SurfaceBucketMap.FromSimHubLoose"/>).</summary>
        SimHub = 2,
    }

    /// <summary>
    /// The classifier's output: the dominant (<see cref="Active"/>, hysteresis-switched) bucket that
    /// learning goes to, and the three blend weights (sum 1) that publishing uses.
    /// </summary>
    public readonly struct SurfaceBlend
    {
        public SurfaceBlend(SurfaceBucket active, double wSealed, double wLoose, double wLowGrip, SurfaceBucketSource source)
        {
            Active = active;
            WSealed = wSealed;
            WLoose = wLoose;
            WLowGrip = wLowGrip;
            Source = source;
        }

        public SurfaceBucket Active { get; }
        public double WSealed { get; }
        public double WLoose { get; }
        public double WLowGrip { get; }
        public SurfaceBucketSource Source { get; }

        /// <summary>The weight of <paramref name="bucket"/>.</summary>
        public double Weight(SurfaceBucket bucket) =>
            bucket == SurfaceBucket.Sealed ? WSealed : bucket == SurfaceBucket.Loose ? WLoose : WLowGrip;

        /// <summary>Fully in one bucket.</summary>
        public static SurfaceBlend Only(SurfaceBucket bucket, SurfaceBucketSource source = SurfaceBucketSource.Unknown) =>
            new SurfaceBlend(bucket,
                bucket == SurfaceBucket.Sealed ? 1.0 : 0.0,
                bucket == SurfaceBucket.Loose ? 1.0 : 0.0,
                bucket == SurfaceBucket.LowGrip ? 1.0 : 0.0, source);

        /// <summary>The brief's tuple shape: <c>var (active, wSealed, wLoose, wLowGrip) = blend;</c></summary>
        public void Deconstruct(out SurfaceBucket active, out double wSealed, out double wLoose, out double wLowGrip)
        {
            active = Active;
            wSealed = WSealed;
            wLoose = WLoose;
            wLowGrip = WLowGrip;
        }

        public override string ToString() => $"{Active} ({Source}) S{WSealed:0.00}/L{WLoose:0.00}/G{WLowGrip:0.00}";
    }

    /// <summary>
    /// The surface a frame reports, from the two sources the game gives (owner, 2026-11-23), wheel order never mattering:
    /// <list type="number">
    /// <item><b>The game's own per-wheel surface field</b>, translated by the row's <c>surfaceValues</c> in
    /// QAdvanceFeedback.SlipAngleSource.json (the reader does the translation and hands over one bucket per wheel;
    /// <see cref="SlipAngleInput.WheelSurface"/>). Only documented values are listed (ruling R13): rFactor 2's
    /// Dry / Kerb / Grass / Dirt / Gravel and GT7's tarmac / kerb / dirt / grass / sand / snow ship; any other value is
    /// unknown. <see cref="Vote"/>: a strict majority of the wheels that translate; a tie, or none, is unknown.</item>
    /// <item><b>SimHub's own per-wheel grass/gravel flag</b> (<see cref="ITelemetryFrame.WheelOnLooseSurfaceFrontLeft"/>,
    /// the one Lock / Slip already use), when the first gives nothing: <see cref="FromSimHubLoose"/>. It can only say
    /// "loose", and SimHub has no flag for whether a game fills it in, so "not loose" counts as tarmac only once the
    /// game has reported loose this session.</item>
    /// </list>
    /// </summary>
    public static class SurfaceBucketMap
    {
        /// <summary>Whether a game can report Low-grip (its source row translates a value to lowGrip). Set by the source
        /// table whenever a source file is put in use; null (no file yet) keeps every saved bucket.</summary>
        public static Func<string, bool> ReportsLowGrip { get; set; }

        /// <summary>
        /// True when <paramref name="bucket"/> of <paramref name="gameId"/> could only have been reached by the old surface
        /// guessing (owner, 2026-11-22: never guess) - dropped on load. Since SimHub's grass/gravel flag (2026-11-23) any
        /// game can genuinely learn Loose, so only a Low-grip bucket of a game that cannot report Low-grip is.
        /// </summary>
        public static bool IsGuessedBucket(string gameId, SurfaceBucket bucket) =>
            bucket == SurfaceBucket.LowGrip && ReportsLowGrip != null && !ReportsLowGrip(gameId);

        /// <summary>A <c>surfaceValues</c> bucket name: sealed, loose or lowGrip (case-insensitive).</summary>
        public static bool TryParseName(string name, out SurfaceBucket bucket)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "sealed": bucket = SurfaceBucket.Sealed; return true;
                case "loose": bucket = SurfaceBucket.Loose; return true;
                case "lowgrip": bucket = SurfaceBucket.LowGrip; return true;
                default: bucket = SurfaceBucket.Sealed; return false;
            }
        }

        /// <summary>Strict majority over the wheels that translate; null on a tie or when none does.</summary>
        public static SurfaceBucket? Vote(SurfaceBucket?[] wheels)
        {
            if (wheels == null) return null;
            int sealedVotes = 0, loose = 0, lowGrip = 0;
            foreach (SurfaceBucket? b in wheels)
            {
                if (!b.HasValue) continue;
                if (b.Value == SurfaceBucket.Sealed) sealedVotes++;
                else if (b.Value == SurfaceBucket.Loose) loose++;
                else lowGrip++;
            }
            if (sealedVotes > loose && sealedVotes > lowGrip) return SurfaceBucket.Sealed;
            if (loose > sealedVotes && loose > lowGrip) return SurfaceBucket.Loose;
            if (lowGrip > sealedVotes && lowGrip > loose) return SurfaceBucket.LowGrip;
            return null;
        }

        /// <summary>Wheels on grass/gravel by SimHub's flag that make a frame loose (most of the four).</summary>
        public const int SimHubLooseWheels = 3;

        /// <summary>
        /// SimHub's grass/gravel flag: Loose when at least <see cref="SimHubLooseWheels"/> wheels report it; Sealed when at
        /// most one does AND <paramref name="gameReportsLoose"/> (the game was seen reporting loose this session - before
        /// that "not loose" may only mean the game does not fill the flag in); otherwise (two wheels, or no evidence yet)
        /// unknown. Wheels with no value are not counted.
        /// </summary>
        public static SurfaceBucket? FromSimHubLoose(bool? fl, bool? fr, bool? rl, bool? rr, bool gameReportsLoose)
        {
            int known = 0, loose = 0;
            foreach (bool? w in new[] { fl, fr, rl, rr })
            {
                if (!w.HasValue) continue;
                known++;
                if (w.Value) loose++;
            }
            if (loose >= SimHubLooseWheels) return SurfaceBucket.Loose;
            if (gameReportsLoose && known >= SimHubLooseWheels && loose <= 1) return SurfaceBucket.Sealed;
            return null;
        }
    }

    /// <summary>
    /// Decides which surface bucket the car is on (docs\slip-angle-design.md 9.5.8).
    /// <para><b>ONLY WHAT THE GAME REPORTS - NEVER GUESSED (owner, 2026-11-22: "If you cannot get the surface
    /// information, then assume the surface as unknown. NO NEED TO GUESS").</b> The candidate is the game-reported bucket
    /// (<see cref="SurfaceBucketMap"/>: the game's own field, else SimHub's grass/gravel flag). A frame without one is Unknown and never moves the
    /// bucket: a game that reports no surface (FH6, F1, AC, ...) stays in the starting bucket for good, so all its
    /// learning is in one place. The earlier inference from grip (lateral G against the sealed curve at the same angle)
    /// flipped FH6 between sealed and loose every few seconds on tarmac and ended on low-grip: transients - turn-in,
    /// trail-braking, exits - read below the steady-state curve, and the learning was scattered over three buckets.</para>
    /// <para><b>Hysteresis.</b> The active bucket switches only after the same reported bucket has been reported on every
    /// frame for <see cref="HysteresisSeconds"/>; an Unknown frame, or the active bucket, restarts that timer.</para>
    /// <para><b>Blend.</b> The weights move toward the active bucket linearly over <see cref="BlendSeconds"/>
    /// (the active weight rises by dt / 1.5 s; the others shrink proportionally, so the sum stays 1).
    /// Learning goes to <see cref="SurfaceBlend.Active"/>; publishing blends by the weights.</para>
    /// Starts Sealed (1, 0, 0). Not thread-safe: the data thread owns it.
    /// </summary>
    public sealed class SurfaceBucketClassifier
    {
        public const double HysteresisSeconds = 1.0;
        public const double BlendSeconds = 1.5;

        private const double NominalFrameSeconds = 1.0 / 60.0;
        private const int Buckets = 3;

        private SurfaceBucket _active = SurfaceBucket.Sealed;
        private SurfaceBucket? _pending;
        private double _pendingSeconds;
        private readonly double[] _w = { 1.0, 0.0, 0.0 };
        private SurfaceBucketSource _source = SurfaceBucketSource.Unknown;

        /// <summary>The latest result (Sealed, 1/0/0, Unknown before the first step).</summary>
        public SurfaceBlend Current => new SurfaceBlend(_active, _w[0], _w[1], _w[2], _source);

        /// <summary>One frame. <paramref name="gameReported"/>: the game's bucket this frame; null = Unknown (the
        /// bucket is held, never guessed). <paramref name="source"/>: where a reported bucket came from (diagnostics).</summary>
        public SurfaceBlend Step(SurfaceBucket? gameReported, double dt, SurfaceBucketSource source = SurfaceBucketSource.Game)
        {
            double aged = ClampMath.IsFinite(dt) && dt > 0.0 ? dt : NominalFrameSeconds;
            _source = gameReported.HasValue ? source : SurfaceBucketSource.Unknown;

            if (!gameReported.HasValue || gameReported.Value == _active)
            {
                _pending = null;
                _pendingSeconds = 0.0;
            }
            else
            {
                if (_pending != gameReported) { _pending = gameReported; _pendingSeconds = 0.0; }
                _pendingSeconds += aged;
                if (_pendingSeconds >= HysteresisSeconds - 1e-9)
                {
                    _active = gameReported.Value;
                    _pending = null;
                    _pendingSeconds = 0.0;
                }
            }

            BlendToward((int)_active, aged);
            return Current;
        }

        private void BlendToward(int target, double aged)
        {
            double old = _w[target];
            if (old >= 1.0) return;
            double next = Math.Min(1.0, old + aged / BlendSeconds);
            double rest = 1.0 - old;
            double scale = rest > 0.0 ? (1.0 - next) / rest : 0.0;
            for (int b = 0; b < Buckets; b++) _w[b] = b == target ? next : _w[b] * scale;
            if (next >= 1.0) for (int b = 0; b < Buckets; b++) _w[b] = b == target ? 1.0 : 0.0;
        }
    }
}
