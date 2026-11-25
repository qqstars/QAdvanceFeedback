namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Where the game's per-wheel slip angle stands (owner, 2026-11-24) - what the orange line under Slip Angle says
    /// while the car angle is used. It is what is APPLIED for the projection, not what the game's source defines:
    /// </summary>
    public enum SlipAngleSourceState
    {
        /// <summary>The game has no per-wheel source in QAdvanceFeedback.SlipAngleSource.json.</summary>
        NoSource = 0,
        /// <summary>A source is set and read; it is not confirmed yet (it is promoted after a few seconds of driving
        /// and cornering).</summary>
        Checking = 1,
        /// <summary>A source is set but its properties are not reported (none of the four for 10 s while moving).</summary>
        NotReported = 2,
        /// <summary>The game's own per-wheel angle is in use.</summary>
        Native = 3,
        /// <summary>The car angle because Global Source Type Mode is "Always car angle".</summary>
        CarAngleAlways = 4,
        /// <summary>The car angle because "Source Type for this game" is Car Angle.</summary>
        CarAngleForGame = 5,
    }

    /// <summary>
    /// One frame's Slip Angle inputs that do not belong on <see cref="ITelemetryFrame"/> because they
    /// come from a game's own raw data (docs\slip-angle-design.md §9.1). Built per frame by Layer 2
    /// (<c>SlipAngleRawSourceReader</c>); body motion (local velocity, yaw rate, lateral G, airborne,
    /// kerbs) stays on the frame itself. Every reading is honest about absence: null means "not
    /// available this frame", never zero.
    /// </summary>
    public sealed class SlipAngleInput
    {
        /// <summary>The SimHub game id this frame was read for (may be empty).</summary>
        public string GameId { get; }

        /// <summary>True when the game has a native slip-angle row and its paths have not been ruled
        /// missing this session.</summary>
        public bool HasNativeSource { get; }

        /// <summary>See <see cref="SlipAngleSourceState"/>. Unless given, it follows <see cref="HasNativeSource"/> and
        /// <see cref="IsNative"/>.</summary>
        public SlipAngleSourceState SourceState { get; }

        /// <summary>
        /// Per-wheel tyre slip angle in degrees, FL/FR/RL/RR, signed as the game reports it after the
        /// table's transform. Null when the game has no native source, any wheel is unreadable or
        /// non-finite this frame, or the unit is still unknown (an unverified-unit game before
        /// detection). Not gated by <see cref="IsNative"/>: consumers decide.
        /// </summary>
        public Corners? NativeWheelDeg { get; }

        /// <summary>True once <see cref="SlipAngleNativeSupport"/> has promoted this game this session.</summary>
        public bool IsNative { get; }

        /// <summary>The unit used for <see cref="NativeWheelDeg"/>: the table's, or the game's list (radians when
        /// unlisted - <see cref="SlipAngleSourceUnits"/>); <see cref="SlipAngleUnit.Unverified"/> with no source.
        /// A grip ratio picks its own learning key and the ratio warning.</summary>
        public SlipAngleUnit Unit { get; }

        /// <summary>A ratio source: whether its values go over full slip (100%) - false switches the drift
        /// start to 100% with the longer delay (owner, 2026-11-20). Null when unknown or not a ratio.</summary>
        public bool? RatioExceedsFullScale { get; }

        /// <summary>True for a row the spike marks provisional (the NASCAR Heat reader family).</summary>
        public bool IsProvisionalSource { get; }

        /// <summary>
        /// Per-wheel "on a kerb" flags, FL/FR/RL/RR (copy of <see cref="ITelemetryFrame.WheelsOnKerbs"/>),
        /// or null when the frame has none.
        /// </summary>
        public bool[] KerbWheels { get; }

        /// <summary>
        /// The game's own per-wheel surface value, FL/FR/RL/RR, as an invariant string (enum name or
        /// number), with a null entry for a wheel that could not be read; null when the spike documents
        /// no per-wheel surface field for this game. Deliberately NOT interpreted here (ruling R13:
        /// mapping to surface buckets is a later task).
        /// </summary>
        public string[] RawWheelSurface { get; }

        /// <summary>
        /// <see cref="RawWheelSurface"/> translated by the game's source row (<c>surfaceValues</c>), FL/FR/RL/RR, with a
        /// null entry for a wheel whose value is not listed; null when the row has no surface translation.
        /// </summary>
        public SurfaceBucket?[] WheelSurface { get; }

        public static readonly SlipAngleInput Empty = new SlipAngleInput(string.Empty);

        public SlipAngleInput(
            string gameId,
            bool hasNativeSource = false,
            Corners? nativeWheelDeg = null,
            bool isNative = false,
            SlipAngleUnit unit = SlipAngleUnit.Unverified,
            bool isProvisionalSource = false,
            bool[] kerbWheels = null,
            string[] rawWheelSurface = null,
            bool? ratioExceedsFullScale = null,
            SurfaceBucket?[] wheelSurface = null,
            SlipAngleSourceState? sourceState = null)
        {
            GameId = gameId ?? string.Empty;
            HasNativeSource = hasNativeSource;
            SourceState = sourceState ?? (!hasNativeSource ? SlipAngleSourceState.NoSource
                : isNative ? SlipAngleSourceState.Native : SlipAngleSourceState.Checking);
            NativeWheelDeg = nativeWheelDeg;
            IsNative = isNative;
            Unit = unit;
            IsProvisionalSource = isProvisionalSource;
            KerbWheels = kerbWheels;
            RawWheelSurface = rawWheelSurface;
            WheelSurface = wheelSurface;
            RatioExceedsFullScale = ratioExceedsFullScale;
        }
    }
}
