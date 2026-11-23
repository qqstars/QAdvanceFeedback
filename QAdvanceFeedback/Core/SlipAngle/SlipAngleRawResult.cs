namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// One frame's Slip Angle raw result (docs\slip-angle-design.md §9.1, §9.3). Degrees, always
    /// non-negative when present; null means "not available this frame", never a fake zero.
    /// </summary>
    public sealed class SlipAngleRawResult
    {
        /// <summary>Front axle angle (unscaled by k); null when unavailable (always null in the fallback tier).</summary>
        public double? FrontDeg { get; }

        /// <summary>Rear axle angle; null when unavailable.</summary>
        public double? RearDeg { get; }

        /// <summary>The combined, equalised "rear-equivalent" angle; null when nothing could be computed.</summary>
        public double? AllDeg { get; }

        /// <summary>Which source produced the value: Native, Fallback, or Unknown when nothing was computed.</summary>
        public SourceNativeness Tier { get; }

        /// <summary>Absolute per-wheel native angles (FL/FR/RL/RR); null in the fallback tier.</summary>
        public Corners? WheelsDeg { get; }

        public SlipAngleRawResult(double? frontDeg, double? rearDeg, double? allDeg, SourceNativeness tier, Corners? wheelsDeg)
        {
            FrontDeg = frontDeg;
            RearDeg = rearDeg;
            AllDeg = allDeg;
            Tier = tier;
            WheelsDeg = wheelsDeg;
        }

        public static readonly SlipAngleRawResult None =
            new SlipAngleRawResult(null, null, null, SourceNativeness.Unknown, null);
    }
}
