namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Tunables for <see cref="DriftAttenuator"/> (docs\slip-angle-design.md §9.7, parameter table).
    /// A plain mutable bag (persisted by Settings later): the setters do NOT clamp, so a value can be
    /// round-tripped exactly; <see cref="Sanitise"/> enforces the ranges in place (non-finite values
    /// fall back to the default), and <see cref="DriftAttenuator.Step"/> reads every value through the
    /// same clamps so an un-sanitised instance can never misbehave. Times are in SECONDS (the spec
    /// table quotes ms). <c>MaxFlightSeconds</c> / <c>MaxWobbleSeconds</c> live in
    /// <c>SlipAngleGateSettings</c>, not here.
    /// </summary>
    public sealed class DriftSettings
    {
        public const double DefaultHoldSeconds = 0.25, MinHoldSeconds = 0.05, MaxHoldSeconds = 2.0;
        public const double DefaultStabilityRange = 20, MinStabilityRange = 2, MaxStabilityRange = 60;
        public const double DefaultToPercent = 40, MinToPercent = 0, MaxToPercent = 100;
        public const double DefaultDurationSeconds = 0.12, MinDurationSeconds = 0, MaxDurationSeconds = 2.0;
        public const double DefaultGamma = 2.0, MinGamma = 0.5, MaxGamma = 6.0;
        public const double DefaultSmoothingSeconds = 0.05, MinSmoothingSeconds = 0, MaxSmoothingSeconds = 0.5;
        public const double DefaultSwingMargin = 8, MinSwingMargin = 1, MaxSwingMargin = 40;
        public const double DefaultTrendConfirmSeconds = 0.08, MinTrendConfirmSeconds = 0, MaxTrendConfirmSeconds = 0.5;
        public const double DefaultThrottleRiseClear = 20, MinThrottleRiseClear = 5, MaxThrottleRiseClear = 100;
        public const double DefaultThrottleLevelClear = 90, MinThrottleLevelClear = 50, MaxThrottleLevelClear = 100;

        /// <summary>Time Nᵤ must stay above the drift start before a drift is recognised.</summary>
        public double HoldSeconds { get; set; } = DefaultHoldSeconds;

        /// <summary>Largest rise (Nᵤ points) allowed inside the hold window; more is escalation (spin).</summary>
        public double StabilityRange { get; set; } = DefaultStabilityRange;

        /// <summary>Percent of the calculated output a held drift eases down to.</summary>
        public double ToPercent { get; set; } = DefaultToPercent;

        /// <summary>Length of the S-curve ramp from 100 % to <see cref="ToPercent"/>.</summary>
        public double DurationSeconds { get; set; } = DefaultDurationSeconds;

        /// <summary>S-curve exponent γ in S(t)=t^γ/(t^γ+(1−t)^γ); 1 = linear, 2 = symmetric ease in/out.</summary>
        public double Gamma { get; set; } = DefaultGamma;

        /// <summary>EMA time constant applied to Nᵤ; 0 = no smoothing.</summary>
        public double SmoothingSeconds { get; set; } = DefaultSmoothingSeconds;

        /// <summary>Nᵤ points a move must exceed (from the peak / trough) to count as a trend.</summary>
        public double SwingMargin { get; set; } = DefaultSwingMargin;

        /// <summary>How long a trend must persist before it is confirmed.</summary>
        public double TrendConfirmSeconds { get; set; } = DefaultTrendConfirmSeconds;

        /// <summary>Throttle re-press that clears a drift (owner, 2026-11-20): once the slip angle is back
        /// at or below Max, a rise of more than this many throttle points above the lowest throttle seen
        /// since then clears the drift flag (full output at once). A held-slide feather is ~5-15 points; a
        /// deliberate exit press is 20-40.</summary>
        public double ThrottleRiseClear { get; set; } = DefaultThrottleRiseClear;

        /// <summary>Throttle percent that clears a drift outright once the slip angle is back at or below
        /// Max, whatever the reference (owner, 2026-11-20).</summary>
        public double ThrottleLevelClear { get; set; } = DefaultThrottleLevelClear;

        /// <summary>A copy with <see cref="HoldSeconds"/> replaced (clamped into its range).</summary>
        public DriftSettings WithHoldSeconds(double holdSeconds)
        {
            var copy = (DriftSettings)MemberwiseClone();
            copy.HoldSeconds = Hold(holdSeconds);
            return copy;
        }

        /// <summary>Clamps every value into its §9.7 range in place; NaN/∞ become the default.</summary>
        public void Sanitise()
        {
            HoldSeconds = Hold(HoldSeconds);
            StabilityRange = Stability(StabilityRange);
            ToPercent = To(ToPercent);
            DurationSeconds = Duration(DurationSeconds);
            Gamma = GammaOf(Gamma);
            SmoothingSeconds = Smoothing(SmoothingSeconds);
            SwingMargin = Swing(SwingMargin);
            TrendConfirmSeconds = Confirm(TrendConfirmSeconds);
            ThrottleRiseClear = Rise(ThrottleRiseClear);
            ThrottleLevelClear = Level(ThrottleLevelClear);
        }

        internal static double Hold(double v) => Range(v, MinHoldSeconds, MaxHoldSeconds, DefaultHoldSeconds);
        internal static double Stability(double v) => Range(v, MinStabilityRange, MaxStabilityRange, DefaultStabilityRange);
        internal static double To(double v) => Range(v, MinToPercent, MaxToPercent, DefaultToPercent);
        internal static double Duration(double v) => Range(v, MinDurationSeconds, MaxDurationSeconds, DefaultDurationSeconds);
        internal static double GammaOf(double v) => Range(v, MinGamma, MaxGamma, DefaultGamma);
        internal static double Smoothing(double v) => Range(v, MinSmoothingSeconds, MaxSmoothingSeconds, DefaultSmoothingSeconds);
        internal static double Swing(double v) => Range(v, MinSwingMargin, MaxSwingMargin, DefaultSwingMargin);
        internal static double Confirm(double v) => Range(v, MinTrendConfirmSeconds, MaxTrendConfirmSeconds, DefaultTrendConfirmSeconds);
        internal static double Rise(double v) => Range(v, MinThrottleRiseClear, MaxThrottleRiseClear, DefaultThrottleRiseClear);
        internal static double Level(double v) => Range(v, MinThrottleLevelClear, MaxThrottleLevelClear, DefaultThrottleLevelClear);

        private static double Range(double v, double min, double max, double def) =>
            ClampMath.IsFinite(v) ? ClampMath.Clamp(v, min, max) : def;
    }
}
