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
        /// <summary>The game's own per-wheel surface (majority vote, see <see cref="SurfaceBucketMap"/>).</summary>
        Game = 1,
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
    /// Pure table: the game's per-wheel raw surface value (as Task 10's <see cref="SlipAngleInput.RawWheelSurface"/>
    /// exposes it - an invariant string, enum name or number) to a <see cref="SurfaceBucket"/>.
    /// <para><b>Ruling R13:</b> only values whose MEANING docs\slip-angle-spike-report.md documents are
    /// mapped; everything else returns false and the classifier infers the surface from grip.</para>
    /// <list type="table">
    /// <item><b>RFactor2</b> - spike report per-game table, Native (computed) row <c>RFactor2</c>
    /// (line 85): <c>mWheels[i].mSurfaceType (byte; enum Dry, Wet, Grass, Dirt, Gravel, Kerb, Special)</c>.
    /// A byte arrives as its number, so the enum order gives the values: 0 Dry -&gt; Sealed, 1 Wet -&gt;
    /// not mapped (tarmac, but its grip is not dry tarmac's - left to inference), 2 Grass -&gt; Loose,
    /// 3 Dirt -&gt; Loose, 4 Gravel -&gt; Loose, 5 Kerb -&gt; Sealed, 6 Special -&gt; not mapped.</item>
    /// <item><b>GranTurismo7</b> - spike report Fallback-only row <c>GranTurismo7</c> (line 143):
    /// <c>surfaceType[4] (SurfaceTypeID: kerb, dirt, grass, sand, snow, tarmac)</c>. Only the NAMES are
    /// documented (not their numbers), so only a name maps, case-insensitively: tarmac, kerb -&gt; Sealed;
    /// dirt, grass, sand -&gt; Loose; snow -&gt; LowGrip. A number is not mapped.</item>
    /// </list>
    /// Not mapped (meaning undocumented in the spike): LMU (<c>mSurfaceType (byte)</c> only), the F1 2019-23
    /// <c>m_surfaceType[4]</c> rows, AMS2 / PCars <c>mTerrain</c> (<c>eTerrain</c> values not listed), the rF1
    /// family, Wreckfest 2, KartKraft, PMR, NASCAR Heat, BeamNG and Rennsport.
    /// <para><b>Wheel order never matters:</b> <see cref="FromGame"/> takes a strict majority vote over
    /// the wheels that map (the F1 2019-22 rows, for one, have an unverified wheel order); a tie, or no
    /// mapped wheel, returns null (inference).</para>
    /// </summary>
    public static class SurfaceBucketMap
    {
        /// <summary>True for a game whose per-wheel surface this map can read (rFactor 2, GT7). For every other game the
        /// surface is Unknown and never switched (owner, 2026-11-22), so anything saved under its Loose or Low-grip
        /// bucket can only have come from the old guessing - it is dropped on load (<see cref="IsGuessedBucket"/>).</summary>
        public static bool ReportsSurface(string gameId) =>
            string.Equals(gameId, "RFactor2", System.StringComparison.Ordinal)
            || string.Equals(gameId, "GranTurismo7", System.StringComparison.Ordinal);

        /// <summary>True when <paramref name="bucket"/> of <paramref name="gameId"/> could only have been reached by the
        /// old surface guessing: a non-Sealed bucket of a game that reports no surface.</summary>
        public static bool IsGuessedBucket(string gameId, SurfaceBucket bucket) =>
            bucket != SurfaceBucket.Sealed && !ReportsSurface(gameId);

        /// <summary>One wheel's raw value. False when the game or the value is not documented.</summary>
        public static bool TryMapWheel(string gameId, string raw, out SurfaceBucket bucket)
        {
            bucket = SurfaceBucket.Sealed;
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrWhiteSpace(raw)) return false;
            string v = raw.Trim();
            switch (gameId)
            {
                case "RFactor2":
                    switch (v)
                    {
                        case "0": bucket = SurfaceBucket.Sealed; return true;   // Dry
                        case "2": bucket = SurfaceBucket.Loose; return true;    // Grass
                        case "3": bucket = SurfaceBucket.Loose; return true;    // Dirt
                        case "4": bucket = SurfaceBucket.Loose; return true;    // Gravel
                        case "5": bucket = SurfaceBucket.Sealed; return true;   // Kerb
                        default: return false;                                  // 1 Wet, 6 Special, other
                    }
                case "GranTurismo7":
                    switch (v.ToLowerInvariant())
                    {
                        case "tarmac":
                        case "kerb": bucket = SurfaceBucket.Sealed; return true;
                        case "dirt":
                        case "grass":
                        case "sand": bucket = SurfaceBucket.Loose; return true;
                        case "snow": bucket = SurfaceBucket.LowGrip; return true;
                        default: return false;
                    }
                default:
                    return false;
            }
        }

        /// <summary>Strict majority over the wheels that map; null on a tie or when none maps.</summary>
        public static SurfaceBucket? FromGame(string gameId, string[] rawWheelSurface)
        {
            if (rawWheelSurface == null) return null;
            int sealedVotes = 0, loose = 0, lowGrip = 0;
            foreach (string raw in rawWheelSurface)
            {
                if (!TryMapWheel(gameId, raw, out SurfaceBucket b)) continue;
                if (b == SurfaceBucket.Sealed) sealedVotes++;
                else if (b == SurfaceBucket.Loose) loose++;
                else lowGrip++;
            }
            if (sealedVotes > loose && sealedVotes > lowGrip) return SurfaceBucket.Sealed;
            if (loose > sealedVotes && loose > lowGrip) return SurfaceBucket.Loose;
            if (lowGrip > sealedVotes && lowGrip > loose) return SurfaceBucket.LowGrip;
            return null;
        }
    }

    /// <summary>
    /// Decides which surface bucket the car is on (docs\slip-angle-design.md 9.5.8).
    /// <para><b>ONLY THE GAME'S OWN SURFACE - NEVER GUESSED (owner, 2026-11-22: "If you cannot get the surface
    /// information, then assume the surface as unknown. NO NEED TO GUESS").</b> The candidate is the game-reported bucket
    /// (<see cref="SurfaceBucketMap.FromGame"/>: rFactor 2, GT7). A frame without one is Unknown and never moves the
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
        /// bucket is held, never guessed).</summary>
        public SurfaceBlend Step(SurfaceBucket? gameReported, double dt)
        {
            double aged = ClampMath.IsFinite(dt) && dt > 0.0 ? dt : NominalFrameSeconds;
            _source = gameReported.HasValue ? SurfaceBucketSource.Game : SurfaceBucketSource.Unknown;

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
