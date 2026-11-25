using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// What the Slip Angle UI needs, captured on the data thread as plain values (immutable). Built from
    /// one <see cref="SlipAngleFrameResult"/> plus the debounced source tier (the value that is broadcast
    /// as <c>SettingField.SourceAvailability</c>), so a warning never flickers frame to frame.
    /// <para>Ruling R19: the orange fallback warning keys on tier Fallback with the Plugin Internal source;
    /// tier Unknown with no Raw.All is the "no slip-angle data from this game" status (no warning).</para>
    /// </summary>
    public sealed class SlipAngleStatus
    {
        /// <summary>No game running.</summary>
        public static readonly SlipAngleStatus None = new SlipAngleStatus(SourceNativeness.Unknown, true, false,
            SlipAngleWindow.ShippedDefault, SlipAngleWindow.ShippedDefault, 0.0, ColdStartTier.Tier1, false, false, string.Empty, string.Empty);

        public SlipAngleStatus(SourceNativeness nativeness, bool sourceIsPluginInternal, bool hasData,
            SlipAngleWindow learnedWindow, SlipAngleWindow appliedWindow, double maturity, ColdStartTier borrowingTier,
            bool peakConfirmed, bool manualGateReady, string gameId, string sourceIdentity, bool faulted = false,
            double windowQuality = 0.0, SlipAngleUnit unit = SlipAngleUnit.Unverified,
            SurfaceBucket surface = SurfaceBucket.Sealed, SurfaceBucketSource surfaceSource = SurfaceBucketSource.Unknown,
            double surfaceWeight = 1.0, SlipAngleSourceState sourceState = SlipAngleSourceState.NoSource)
        {
            SourceState = sourceState;
            Surface = surface;
            SurfaceSource = surfaceSource;
            SurfaceWeight = ClampMath.IsFinite(surfaceWeight) ? ClampMath.Clamp(surfaceWeight, 0.0, 1.0) : 1.0;
            Unit = unit;
            Faulted = faulted;
            WindowQuality = ClampMath.IsFinite(windowQuality) ? ClampMath.Clamp(windowQuality, 0.0, 1.0) : 0.0;
            Nativeness = nativeness;
            SourceIsPluginInternal = sourceIsPluginInternal;
            HasData = hasData;
            LearnedWindow = learnedWindow;
            AppliedWindow = appliedWindow;
            Maturity = maturity;
            BorrowingTier = borrowingTier;
            PeakConfirmed = peakConfirmed;
            ManualGateReady = manualGateReady;
            GameId = gameId ?? string.Empty;
            SourceIdentity = sourceIdentity ?? string.Empty;
        }

        /// <summary>Where the game's per-wheel slip angle stands (the orange line's wording).</summary>
        public SlipAngleSourceState SourceState { get; }

        /// <summary>Debounced tier of the current source (Unknown with no game).</summary>
        public SourceNativeness Nativeness { get; }
        public bool SourceIsPluginInternal { get; }
        /// <summary>Raw.All had a value on the captured frame.</summary>
        public bool HasData { get; }
        public SlipAngleWindow LearnedWindow { get; }
        public SlipAngleWindow AppliedWindow { get; }
        public double Maturity { get; }
        public ColdStartTier BorrowingTier { get; }
        public bool PeakConfirmed { get; }
        public bool ManualGateReady { get; }
        /// <summary>Window quality 0..1 (display only; capped at 0.6 while the peak is unconfirmed - shown as
        /// "Max not yet confirmed").</summary>
        public double WindowQuality { get; }
        public string GameId { get; }
        public string SourceIdentity { get; }

        /// <summary>The surface bucket the learning goes to now - its key is the one whose window / progress is shown.</summary>
        public SurfaceBucket Surface { get; }

        /// <summary>Where this frame's surface came from (Unknown: nothing reported, the bucket is held).</summary>
        public SurfaceBucketSource SurfaceSource { get; }

        /// <summary>The weight of <see cref="Surface"/> in the published blend (below 1 while changing surface).</summary>
        public double SurfaceWeight { get; }

        /// <summary>The unit of the game's native field (a grip ratio shows the ratio warning).</summary>
        public SlipAngleUnit Unit { get; }

        /// <summary>The Slip Angle pipeline faulted this frame (health: <c>HealthSubsystems.SlipAngle</c>);
        /// outputs read 0. The UI shows the health message, not "no data".</summary>
        public bool Faulted { get; }

        /// <summary>Orange warning (R19; owner, 2026-11-20): Plugin Internal reading the car's body angle
        /// (<see cref="IsCarAngleFallback"/>) or a grip ratio turned into an angle (<see cref="IsRatioSource"/>).</summary>
        public bool ShowFallbackWarning => IsCarAngleFallback || IsRatioSource;

        /// <summary>Plugin Internal reading the body-motion estimate (the car angle).</summary>
        public bool IsCarAngleFallback => !Faulted && SourceIsPluginInternal && Nativeness == SourceNativeness.Fallback;

        /// <summary>Plugin Internal reading the game's native field, which is a grip ratio (angle = 20% x value).</summary>
        public bool IsRatioSource => !Faulted && SourceIsPluginInternal && Nativeness == SourceNativeness.Native
            && SlipAngleSourceScale.IsRatio(Unit);

        /// <summary>R19: "No slip-angle data from this game" - tier Unknown and no Raw.All (no warning).
        /// Keys on Raw.All being null, so a Custom source (always tier Unknown) with a value never shows it.
        /// False with no game running (<see cref="GameId"/> empty) and after a fault.</summary>
        public bool NoSlipAngleData => !Faulted && Nativeness == SourceNativeness.Unknown && !HasData && GameId.Length > 0;

        /// <summary>The status after a pipeline fault.</summary>
        public static SlipAngleStatus Fault(string gameId, bool sourceIsPluginInternal) =>
            new SlipAngleStatus(SourceNativeness.Unknown, sourceIsPluginInternal, false, SlipAngleWindow.ShippedDefault,
                SlipAngleWindow.ShippedDefault, 0.0, ColdStartTier.Tier1, false, false, gameId, string.Empty, faulted: true);

        public static SlipAngleStatus From(SlipAngleFrameResult r, SourceNativeness debouncedNativeness, string gameId)
        {
            if (r == null) return None;
            return new SlipAngleStatus(debouncedNativeness, r.SourceIsPluginInternal, r.RawAll.HasValue,
                r.LearnedWindow, r.AppliedWindow, r.Maturity, r.BorrowingTier, r.PeakConfirmed, r.ManualGateReady,
                gameId, r.SourceIdentity, windowQuality: r.WindowQuality, unit: r.Unit,
                surface: r.SurfaceBucket, surfaceSource: r.SurfaceSource,
                surfaceWeight: r.SurfaceBucket == SurfaceBucket.Sealed ? r.SurfaceWeightSealed
                    : r.SurfaceBucket == SurfaceBucket.Loose ? r.SurfaceWeightLoose : r.SurfaceWeightLowGrip,
                sourceState: r.SourceState);
        }
    }
}
