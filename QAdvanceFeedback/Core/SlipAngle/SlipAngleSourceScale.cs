using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Converts a native slip-angle reading in its <see cref="SlipAngleUnit"/> to degrees (owner,
    /// 2026-11-20). Radians x 180/pi; degrees as they are; a grip ratio is read on a 0..100 scale
    /// (<see cref="SlipAngleUnit.Ratio1"/> x 100 first) and turned into an angle as
    /// <see cref="DegreesPerPercent"/> x value.
    /// <para><b>RATIO SCALE 10 (owner, 2026-11-22; was 5 = 20% x value):</b> 10 ratio points per degree, so full slip
    /// (100, ratio 1.0) is <see cref="FullScaleDeg"/> = 10 degrees - the shipped default Max. FH6's measured limit
    /// (lateral G peaks at ratio 1.0-1.1, falling after it) lands at 10-11 degrees, around where most cars' Max sits.
    /// The learned Max of a slip-ratio source is capped at <see cref="MaxCapDeg"/> = 22.5 degrees (ratio 2.25 / 225),
    /// and the reading at <see cref="CapPercent"/> = 1.5 x that (End of the largest Max). Sign-preserving. Pure.</para>
    /// </summary>
    public static class SlipAngleSourceScale
    {
        public const double RadToDeg = 180.0 / Math.PI;

        /// <summary>Full slip on the 0..100 ratio scale.</summary>
        public const double FullScalePercent = 100.0;

        /// <summary>Angle per ratio point: value / 10 (0..100 -> 0..10 degrees; owner, 2026-11-22).</summary>
        public const double DegreesPerPercent = 0.1;

        /// <summary>The learned-Max cap of a slip-ratio source: 225 (ratio 2.25) = 22.5 degrees (owner, 2026-11-22).</summary>
        public const double MaxCapPercent = 225.0;

        /// <summary><see cref="MaxCapPercent"/> as an angle: 22.5 degrees.</summary>
        public const double MaxCapDeg = MaxCapPercent * DegreesPerPercent;

        /// <summary>The ratio reading is capped at the End of the largest Max: 1.5 x 225 = 337.5 = 33.75 degrees.</summary>
        public const double CapPercent = SlipAngleWindow.EndToMaxRatio * MaxCapPercent;

        /// <summary>Full slip as an angle: 10 degrees.</summary>
        public const double FullScaleDeg = FullScalePercent * DegreesPerPercent;

        /// <summary>The ratio reading cap as an angle: 33.75 degrees.</summary>
        public const double CapDeg = CapPercent * DegreesPerPercent;

        public static bool IsRatio(SlipAngleUnit unit) => unit == SlipAngleUnit.Ratio1 || unit == SlipAngleUnit.Ratio100;

        /// <summary>A ratio reading on the 0..100 scale (Ratio1 x 100); other units pass through.</summary>
        public static double ToPercent(double value, SlipAngleUnit unit) =>
            unit == SlipAngleUnit.Ratio1 ? value * FullScalePercent : value;

        /// <summary><paramref name="value"/> in <paramref name="unit"/> as degrees (Unverified passes through).</summary>
        public static double ToDegrees(double value, SlipAngleUnit unit)
        {
            switch (unit)
            {
                case SlipAngleUnit.Radians:
                    return value * RadToDeg;
                case SlipAngleUnit.Ratio1:
                case SlipAngleUnit.Ratio100:
                    if (double.IsNaN(value)) return value;
                    double percent = ToPercent(value, unit);
                    double capped = Math.Min(Math.Abs(percent), CapPercent) * DegreesPerPercent;
                    return percent < 0.0 ? -capped : capped;
                default:
                    return value;
            }
        }
    }
}
