using System;
using System.Globalization;
using QAdvanceFeedback.Core.Localization;
using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.SlipAngle;
using QAdvanceFeedback.Settings;

namespace QAdvanceFeedback.Ui.ViewModels
{
    /// <summary>The LED colour of a learning indicator (owner, 2026-11-21, revised): Red only at 0%, Orange above 0
    /// and below 25%, Yellow 25-50%, Azure 50-75%, Blue 75-100%, Green fully matured. A boundary belongs to the
    /// higher colour.</summary>
    public enum LearningLevel
    {
        Red,
        Orange,
        Yellow,
        Azure,
        Blue,
        Green,
    }

    /// <summary>
    /// What one learning indicator shows (docs\learning-maturity-indicator-report.md): visible or collapsed, the
    /// LED colour, the whole percent and the tooltip. Immutable and compared by value, so a view model can
    /// expose it as one bindable property and the view binds one way to its members.
    /// </summary>
    public sealed class LearningIndicator : IEquatable<LearningIndicator>
    {
        /// <summary>Not shown (Manual, or a row that is hidden) - takes no space.</summary>
        public static readonly LearningIndicator Hidden = new LearningIndicator(false, LearningLevel.Red, 0, string.Empty);

        public LearningIndicator(bool isVisible, LearningLevel level, int percent, string toolTip, bool isBorrowed = false,
            LearningLevel ownLevel = LearningLevel.Red)
        {
            IsVisible = isVisible;
            Level = level;
            Percent = percent;
            ToolTip = toolTip ?? string.Empty;
            IsBorrowed = isBorrowed;
            OwnLevel = isBorrowed ? ownLevel : level;
        }

        /// <summary>The shown confidence is borrowed: the LED's left half shows <see cref="OwnLevel"/> (this key's own
        /// confidence - Red while it has none), its right half <see cref="Level"/> (owner, 2026-11-22).</summary>
        public bool IsBorrowed { get; }

        /// <summary>The colour of this key's own confidence - the left half while <see cref="IsBorrowed"/>; otherwise
        /// equal to <see cref="Level"/>.</summary>
        public LearningLevel OwnLevel { get; }

        public bool IsVisible { get; }
        public LearningLevel Level { get; }
        /// <summary>0-100, rounded down (99.6% shows 99, never claims 100).</summary>
        public int Percent { get; }
        public string ToolTip { get; }

        public bool Equals(LearningIndicator other) =>
            other != null && IsVisible == other.IsVisible && Level == other.Level && Percent == other.Percent
            && IsBorrowed == other.IsBorrowed && OwnLevel == other.OwnLevel && string.Equals(ToolTip, other.ToolTip, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as LearningIndicator);
        public override int GetHashCode() => (IsVisible ? 1 : 0) ^ ((int)Level << 1) ^ (Percent << 4);
        public override string ToString() => IsVisible ? Level + " " + Percent.ToString(CultureInfo.InvariantCulture) + "%" : "hidden";
    }

    /// <summary>The one rule from a 0..1 maturity to what the indicator shows.</summary>
    public static class LearningIndicatorRules
    {
        /// <summary>The indicator for <paramref name="rate"/> (0..1; null or non-finite = 0), shown only when
        /// <paramref name="visible"/> (Auto, and the row itself shown).</summary>
        public static LearningIndicator From(double? rate, bool visible) => From(rate, visible, null);

        /// <summary>As <see cref="From(double?, bool)"/>, with <paramref name="partialToolTip"/> replacing the
        /// partial ("Learning - x% confident") text when given - the 0% and fully-learned texts never change.</summary>
        public static LearningIndicator From(double? rate, bool visible, string partialToolTip) => From(rate, visible, partialToolTip, false);

        /// <summary>As above; <paramref name="borrowed"/> marks a borrowed confidence (the LED's left half the colour of
        /// <paramref name="ownRate"/>, this key's own confidence - Red at 0% - and its right half the shown level).
        /// Ignored at 0% (nothing to show) and when hidden.</summary>
        public static LearningIndicator From(double? rate, bool visible, string partialToolTip, bool borrowed, double? ownRate = null)
        {
            if (!visible) return LearningIndicator.Hidden;
            int percent = Percent(rate);
            string tip = percent > 0 && percent < 100 && !string.IsNullOrEmpty(partialToolTip) ? partialToolTip : ToolTipFor(percent);
            return new LearningIndicator(true, LevelFor(percent), percent, tip, borrowed && percent > 0, LevelFor(Percent(ownRate)));
        }

        /// <summary>The tooltip for where a learned SMax comes from (owner, 2026-11-21); null for its own evidence
        /// or nothing learned (the percent texts apply).</summary>
        public static string OriginToolTip(KeyDataOrigin? origin)
        {
            switch (origin)
            {
                case KeyDataOrigin.EarlierSession: return Strings.Get("Learning.Indicator.Origin.EarlierSession");
                case KeyDataOrigin.BorrowedSurface: return Strings.Get("Learning.Indicator.Origin.BorrowedSurface");
                case KeyDataOrigin.BorrowedCar: return Strings.Get("Learning.Indicator.Origin.BorrowedCar");
                case KeyDataOrigin.BorrowedGame: return Strings.Get("Learning.Indicator.Origin.BorrowedGame");
                case KeyDataOrigin.FromLock: return Strings.Get("Learning.Indicator.Origin.FromLock");
                default: return null;
            }
        }

        /// <summary>
        /// One Slip Angle LED (Max, Best or Min at <paramref name="rate"/>) from the key's maturity (owner, 2026-11-22):
        /// the colour shows a borrowed WINDOW only - half-and-half, its left half this key's own confidence - with "Borrowed
        /// from this car on another surface / another car in this game" as the tooltip. A borrowed k is never in the colour:
        /// a line in the tooltip only. Before the peak is confirmed the partial tooltip says so.
        /// </summary>
        public static LearningIndicator SlipAngle(double? rate, bool visible, SlipAngleWindowMaturity? m)
        {
            bool borrowed = m?.WindowBorrowed == true;
            string partial = m.HasValue && m.Value.StillStarting && !borrowed ? SlipAngleProgressToolTip(Percent(m.Value.Max)) : null;
            LearningIndicator led = From(rate, visible, partial, borrowed, borrowed ? m.Value.OwnMax : (double?)null);
            if (!led.IsVisible || !m.HasValue) return led;
            string tip = led.ToolTip;
            if (led.IsBorrowed)
                tip = Strings.Get(m.Value.WindowBorrowedFrom == ColdStartTier.Tier4
                    ? "Learning.Indicator.Origin.BorrowedSurface" : "Learning.Indicator.Origin.BorrowedCar");
            if (m.Value.KBorrowed) tip += Environment.NewLine + Strings.Get("Learning.Indicator.SlipAngle.KBorrowed");
            return string.Equals(tip, led.ToolTip, StringComparison.Ordinal)
                ? led : new LearningIndicator(true, led.Level, led.Percent, tip, led.IsBorrowed, led.OwnLevel);
        }

        /// <summary>The Slip Angle tooltip before the peak is confirmed: "Learning - 63% confident (peak not
        /// confirmed yet)".</summary>
        public static string SlipAngleProgressToolTip(int percent) =>
            string.Format(CultureInfo.InvariantCulture, Strings.Get("Learning.Indicator.SlipAngle.Progress"), percent);

        /// <summary>0 for no learning (or an unusable rate), 100 once matured, otherwise the whole percent
        /// rounded down and kept within 1..99 - so a partial rate never reads as 0 or 100.</summary>
        public static int Percent(double? rate)
        {
            double r = rate ?? 0.0;
            if (double.IsNaN(r) || r <= 0.0) return 0;
            if (r >= 1.0) return 100;
            int p = (int)Math.Floor(r * 100.0 + 1e-9);
            return p < 1 ? 1 : p > 99 ? 99 : p;
        }

        public static LearningLevel LevelFor(int percent)
        {
            if (percent >= 100) return LearningLevel.Green;
            if (percent >= 75) return LearningLevel.Blue;
            if (percent >= 50) return LearningLevel.Azure;
            if (percent >= 25) return LearningLevel.Yellow;
            if (percent > 0) return LearningLevel.Orange;
            return LearningLevel.Red;
        }

        public static string ToolTipFor(int percent)
        {
            if (percent <= 0) return Strings.Get("Learning.Indicator.None");
            if (percent >= 100) return Strings.Get("Learning.Indicator.Full");
            return string.Format(CultureInfo.InvariantCulture, Strings.Get("Learning.Indicator.Partial"), percent);
        }
    }
}
