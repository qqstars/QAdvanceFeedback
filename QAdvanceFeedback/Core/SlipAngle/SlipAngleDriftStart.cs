using System;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Where drift counting starts (owner, 2026-11-20; docs\slip-angle-design.md §9.7): not at Max but
    /// past it, at the angle Max(1.2·Max, Min(30°, 1.5·Max)). For a learned Max up to 20° that is End
    /// (1.5·Max, normalized 100: Max 15° → 22.5°); for Max 20-25° it is 30°; past 25° it is 1.2·Max
    /// (Max 35° → 42°), so a large Max still has room above it. Pure.
    /// </summary>
    public static class SlipAngleDriftStart
    {
        public const double LowerRatio = 1.2, UpperRatio = 1.5, CapDeg = 30.0;

        /// <summary>The drift start angle in degrees for a window's Max (0 when Max is not usable).</summary>
        public static double AngleDeg(double maxDeg)
        {
            if (!ClampMath.IsFinite(maxDeg) || maxDeg <= 0.0) return 0.0;
            return Math.Max(LowerRatio * maxDeg, Math.Min(CapDeg, UpperRatio * maxDeg));
        }

        /// <summary>The drift start as unclamped normalized Nᵤ for <paramref name="w"/>, never below
        /// <see cref="DriftAttenuator.DriftThreshold"/> (80, α = Max). An unusable window gives 80.</summary>
        public static double Nu(SlipAngleWindow w)
        {
            if (!w.IsValid) return DriftAttenuator.DriftThreshold;
            double n = SlipAngleNormalizer.NormalizeUnclamped(AngleDeg(w.Max), w);
            return ClampMath.IsFinite(n) ? Math.Max(DriftAttenuator.DriftThreshold, n) : DriftAttenuator.DriftThreshold;
        }

        /// <summary>A ceiling counts as reached at this share of it: a value held at a grip ratio's ceiling sits
        /// just above a start placed here (the smoothed value approaches the ceiling, never exceeds it).</summary>
        public const double CeilingReach = 0.98;

        /// <summary>The drift hold for a ratio source that stops at full slip: setting x 6, at most 2 s (owner,
        /// 2026-11-20) - the wait stands in for the climb from Max to 1.5 x Max it can never show.</summary>
        public const double SaturatingHoldMultiplier = 6.0, SaturatingHoldMaxSeconds = 2.0;

        /// <summary>
        /// The drift start (unclamped normalized Nᵤ) for a native GRIP-RATIO source (owner, 2026-11-20).
        /// <list type="bullet">
        /// <item>Values go over full slip, or not known yet (<paramref name="exceedsFullScale"/> true / null):
        /// the normal start <see cref="AngleDeg"/>, but never above <see cref="CeilingReach"/> x the 30-degree
        /// cap, so it stays reachable (Max 20: 1.5 x 20 = 30 is the cap itself).</item>
        /// <item>Values stop at full slip (false) and the normal start is past it (Max above ~13 degrees): the start
        /// is <see cref="CeilingReach"/> x full slip (20 degrees) and <paramref name="saturating"/> is true - the
        /// caller then uses the longer hold (<see cref="SaturatingHoldSeconds"/>). Otherwise the normal start.</item>
        /// </list>
        /// Never below <see cref="DriftAttenuator.MinDriftStart"/>; an unusable window gives 80.
        /// </summary>
        public static double RatioNu(SlipAngleWindow w, bool? exceedsFullScale, out bool saturating)
        {
            saturating = false;
            if (!w.IsValid) return DriftAttenuator.DriftThreshold;
            double startDeg = AngleDeg(w.Max);
            if (exceedsFullScale == false)
            {
                double ceiling = CeilingReach * SlipAngleSourceScale.FullScaleDeg;
                if (startDeg > ceiling) { startDeg = ceiling; saturating = true; }
            }
            else
            {
                startDeg = Math.Min(startDeg, CeilingReach * SlipAngleSourceScale.CapDeg);
            }
            double n = SlipAngleNormalizer.NormalizeUnclamped(startDeg, w);
            return ClampMath.IsFinite(n) ? Math.Max(DriftAttenuator.MinDriftStart, n) : DriftAttenuator.DriftThreshold;
        }

        /// <summary>The longer hold for a saturating ratio source: Min(2 s, <paramref name="holdSeconds"/> x 6).</summary>
        public static double SaturatingHoldSeconds(double holdSeconds) =>
            Math.Min(SaturatingHoldMaxSeconds, (ClampMath.IsFinite(holdSeconds) && holdSeconds > 0.0 ? holdSeconds : DriftSettings.DefaultHoldSeconds) * SaturatingHoldMultiplier);
    }
}
