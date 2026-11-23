using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// A Slip Angle key-data window in degrees (docs\slip-angle-design.md 9.4): Min/Best/Max, with
    /// End = 1.5 x Max. Valid when <c>0 &lt; Min &lt; Best &lt; Max</c> with gaps of at least
    /// <see cref="MinGapDeg"/>.
    /// </summary>
    public readonly struct SlipAngleWindow
    {
        /// <summary>Minimum Min-Best and Best-Max gap, degrees (9.4).</summary>
        public const double MinGapDeg = 0.2;

        /// <summary>End as a multiple of Max (Max maps to 80, End to 100).</summary>
        public const double EndToMaxRatio = 1.5;

        // Absorbs floating-point noise when a gap was enforced as exactly MinGapDeg (e.g. 10 - 9.8).
        private const double GapTolerance = 1e-9;

        public SlipAngleWindow(double min, double best, double max)
        {
            Min = min;
            Best = best;
            Max = max;
        }

        public double Min { get; }
        public double Best { get; }
        public double Max { get; }
        public double End => EndToMaxRatio * Max;

        /// <summary>Derived Start knot (docs\slip-angle-design.md 8): floor(0.5 x Min) whole degrees, or
        /// <c>null</c> (absent) when that is 0 (Min below 2 degrees). Never persisted or edited.</summary>
        public double? Start
        {
            get
            {
                if (!ClampMath.IsFinite(Min)) return null;
                double s = Math.Floor(StartToMinRatio * Min);
                return s >= 1.0 ? s : (double?)null;
            }
        }

        /// <summary>Start as a fraction of Min before flooring.</summary>
        public const double StartToMinRatio = 0.5;

        public bool IsValid =>
            ClampMath.IsFinite(Min) && ClampMath.IsFinite(Best) && ClampMath.IsFinite(Max)
            && Min > 0.0
            && Best - Min >= MinGapDeg - GapTolerance
            && Max - Best >= MinGapDeg - GapTolerance;

        /// <summary>Tier 1 cold-start default (9.4): 5 / 8 / 10, End 15.</summary>
        public static SlipAngleWindow ShippedDefault => new SlipAngleWindow(5.0, 8.0, 10.0);

        /// <summary>This window with Max at most <paramref name="maxCapDeg"/>: when Max is above the cap the
        /// whole window is scaled by cap / Max (its shape kept, End still 1.5 x Max); otherwise unchanged.
        /// An invalid or non-positive cap changes nothing.</summary>
        public SlipAngleWindow CappedAt(double maxCapDeg)
        {
            if (!ClampMath.IsFinite(maxCapDeg) || maxCapDeg <= 0.0 || !(Max > maxCapDeg)) return this;
            double s = maxCapDeg / Max;
            return new SlipAngleWindow(Min * s, Best * s, maxCapDeg);
        }

        public override string ToString() => $"{Min:0.##}/{Best:0.##}/{Max:0.##} (End {End:0.##})";
    }
}
