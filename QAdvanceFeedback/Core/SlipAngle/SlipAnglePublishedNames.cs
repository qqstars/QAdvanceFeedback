namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Slip Angle published property suffixes (docs\slip-angle-design.md 9.8). SimHub prefixes each with
    /// "QAdvanceFeedback.". The three product names and the diagnostics are APPENDED at the end of
    /// <see cref="AllPublishedProperties.ProductNames"/> / <see cref="AllPublishedProperties.DiagnosticNames"/>,
    /// so every earlier name and CSV column keeps its position. The order of <see cref="DiagnosticNames"/>
    /// is the CSV order, and <c>PropertyPublisher.SnapshotAllValuesForCsv</c> writes the values in it.
    /// </summary>
    public static class SlipAnglePublishedNames
    {
        /// <summary>Combined Raw angle in degrees; null when there is no slip angle data.</summary>
        public const string RawAll = "SlipAngle.Raw.All";

        /// <summary>0-100 after the key data mapping.</summary>
        public const string NormalizedAll = "SlipAngle.Normalized.All";

        /// <summary>0-100 final value: attenuated, faded, gated, pulsed.</summary>
        public const string ProjectedAll = "SlipAngle.Projected.All";

        /// <summary>The three product names, in publish/CSV order.</summary>
        public static readonly string[] ProductNames = { RawAll, NormalizedAll, ProjectedAll };

        private const string D = "Diag.SlipAngle.";

        /// <summary>The diagnostics, in publish/CSV order.</summary>
        public static readonly string[] DiagnosticNames =
        {
            D + "Wheel.FrontLeft",
            D + "Wheel.FrontRight",
            D + "Wheel.RearLeft",
            D + "Wheel.RearRight",
            D + "Front",
            D + "Rear",
            D + "K",
            D + "Front.LearnedBest",
            D + "Rear.LearnedBest",
            D + "SourceTier",
            D + "DriftState",
            D + "DriftFactor",
            D + "Window.Min",
            D + "Window.Best",
            D + "Window.Max",
            D + "Maturity",
            D + "WindowQuality",
            D + "BorrowingTier",
            D + "WobbleRemaining",
            D + "PedalGateOpen",
            D + "ImpactFlag",
            D + "ExclusionRemaining",
            D + "YawResidual",
            D + "SurfaceBucket",
            D + "SurfaceWeight.Sealed",
            D + "SurfaceWeight.Loose",
            D + "SurfaceWeight.LowGrip",
            D + "SurfaceSource",
            D + "Unit",
            D + "UnitUndetermined",
            D + "UnitVotes.Degrees",
            D + "UnitVotes.Radians",
            D + "NativeBlocked",
            D + "SpeedScale",
            D + "Crossings",
            D + "PeakConfirmed",
            D + "CurvePeakG",
            D + "Identity",
            D + "Sliding",
            D + "EarlyWeight",
            D + "EarlyGuarded",
            // Manual mode (owner, 2026-11-22): the window the output used - the driver's Manual values once they apply,
            // else the learned window (LearnedWindow.* above is always the learned one) - and whether Manual applied.
            D + "AppliedWindow.Min",
            D + "AppliedWindow.Best",
            D + "AppliedWindow.Max",
            D + "ManualApplied",
        };
    }
}
