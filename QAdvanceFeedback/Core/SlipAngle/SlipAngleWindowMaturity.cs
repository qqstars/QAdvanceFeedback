using System;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// How mature each value of a learned Slip Angle window is, 0..1 (docs\learning-maturity-indicator-report.md):
    /// before the peak is confirmed all three are the same progress (the fast estimate's evidence, or consistent
    /// crossings of 3, at most 0.99); after it, <see cref="Max"/> is 1 and <see cref="Best"/> / <see cref="Min"/> follow the curve
    /// they are read off. See <see cref="SlipAngleKeyDataModel.LearnedWindowMaturity"/>.
    /// </summary>
    public readonly struct SlipAngleWindowMaturity
    {
        public SlipAngleWindowMaturity(double max, double best, double min)
            : this(max, best, min, Normalized.ColdStartTier.Tier1, 0.0, false)
        {
        }

        /// <param name="windowBorrowedFrom">The tier the dominant bucket's window is borrowed from (Tier1: not borrowed).</param>
        /// <param name="ownMax">This key's OWN confidence (0 while borrowing) - the LED's left half.</param>
        /// <param name="kBorrowed">The front / rear balance k comes from another surface (tooltip only).</param>
        public SlipAngleWindowMaturity(double max, double best, double min, Normalized.ColdStartTier windowBorrowedFrom, double ownMax, bool kBorrowed)
        {
            Max = max;
            Best = best;
            Min = min;
            WindowBorrowedFrom = windowBorrowedFrom;
            OwnMax = ownMax;
            KBorrowed = kBorrowed;
        }

        /// <summary>Where a borrowed window comes from (Tier4 another surface, Tier3 another car); Tier1 = own / shipped.</summary>
        public Normalized.ColdStartTier WindowBorrowedFrom { get; }

        /// <summary>True when the window is borrowed (owner, 2026-11-22): the ONLY borrowing the LED colour shows.</summary>
        public bool WindowBorrowed => WindowBorrowedFrom > Normalized.ColdStartTier.Tier1;

        /// <summary>This key's own confidence (0 while the window is borrowed).</summary>
        public double OwnMax { get; }

        /// <summary>True when k is borrowed from another surface - shown in the tooltip only (owner, 2026-11-22).</summary>
        public bool KBorrowed { get; }

        public double Max { get; }
        public double Best { get; }
        public double Min { get; }

        /// <summary>True while the peak is not confirmed yet.</summary>
        public bool StillStarting => Max < 1.0;

        /// <summary>Consistent corners seen toward the 3 that confirm the peak (meaningful while
        /// <see cref="StillStarting"/>).</summary>
        public int ConsistentCorners => (int)Math.Round(Math.Max(0.0, Math.Min(1.0, Max)) * SlipAngleWindowLearner.ConfirmingCrossings);
    }
}
