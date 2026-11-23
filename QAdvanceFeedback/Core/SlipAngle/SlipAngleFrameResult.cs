using QAdvanceFeedback.Core.Normalized;
using QAdvanceFeedback.Core.Projection;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// One frame of the Slip Angle pipeline as PLAIN VALUES (docs\slip-angle-design.md 9.8), built on the
    /// data thread by <see cref="SlipAngleFrameProcessor"/> and never changed after it is returned. The
    /// publisher, the CSV and the UI snapshots read these, never the learner (its getters mutate).
    /// </summary>
    public sealed class SlipAngleFrameResult
    {
        /// <summary>No game / paused / menu / pipeline fault: no data, outputs 0.</summary>
        public static readonly SlipAngleFrameResult Empty = new SlipAngleFrameResult();

        // ---- product -------------------------------------------------------------------------------
        /// <summary>Combined angle, degrees; null when unavailable (never a fake 0).</summary>
        public double? RawAll { get; internal set; }
        public double NormalizedAll { get; internal set; }
        public double NormalizedUnclamped { get; internal set; }
        /// <summary>Published Projected.All (pulsed).</summary>
        public double ProjectedAll { get; internal set; }
        /// <summary>Projected.All before the pulse stage (for G-Force integration).</summary>
        public double ProjectedPulseFree { get; internal set; }

        // ---- diagnostics ---------------------------------------------------------------------------
        /// <summary>|α| per wheel FL, FR, RL, RR (degrees); null when no per-wheel source (fallback).</summary>
        public double? WheelFrontLeft { get; internal set; }
        public double? WheelFrontRight { get; internal set; }
        public double? WheelRearLeft { get; internal set; }
        public double? WheelRearRight { get; internal set; }
        public double? Front { get; internal set; }
        public double? Rear { get; internal set; }
        /// <summary>Axle equalisation ratio used for this frame's Raw.All (the previous frame's k).</summary>
        public double K { get; internal set; } = 1.0;
        public double FrontLearnedBest { get; internal set; }
        public double RearLearnedBest { get; internal set; }
        public SourceNativeness Tier { get; internal set; } = SourceNativeness.Unknown;
        public DriftState DriftState { get; internal set; } = DriftState.Normal;
        public double DriftFactor { get; internal set; } = 1.0;
        /// <summary>The learned (Auto) window of Raw.All.</summary>
        public SlipAngleWindow LearnedWindow { get; internal set; } = SlipAngleWindow.ShippedDefault;
        /// <summary>The window actually applied (learned, or the manual slot once its gate is ready).</summary>
        public SlipAngleWindow AppliedWindow { get; internal set; } = SlipAngleWindow.ShippedDefault;
        public double Maturity { get; internal set; }
        public double WindowQuality { get; internal set; }
        public ColdStartTier BorrowingTier { get; internal set; } = ColdStartTier.Tier1;
        public bool PeakConfirmed { get; internal set; }
        public double WobbleRemaining { get; internal set; }
        public bool PedalGateOpen { get; internal set; }
        public bool ImpactFlag { get; internal set; }
        public double ExclusionRemaining { get; internal set; }
        public double YawResidual { get; internal set; }
        public SurfaceBucket SurfaceBucket { get; internal set; } = SurfaceBucket.Sealed;
        public double SurfaceWeightSealed { get; internal set; } = 1.0;
        public double SurfaceWeightLoose { get; internal set; }
        public double SurfaceWeightLowGrip { get; internal set; }
        public SurfaceBucketSource SurfaceSource { get; internal set; } = SurfaceBucketSource.Unknown;
        public SlipAngleUnit Unit { get; internal set; } = SlipAngleUnit.Unverified;
        public bool UnitUndetermined { get; internal set; }
        public int UnitVotesDegrees { get; internal set; }
        public int UnitVotesRadians { get; internal set; }
        public bool NativeBlocked { get; internal set; }
        /// <summary>The speed-aware lateral G factor this frame (1 = none; see SlipAngleSpeedBands.Scale).</summary>
        public double SpeedScale { get; internal set; } = 1.0;
        /// <summary>Diagnostics (docs\learning-maturity-indicator-report.md): completed crossings of the selected key's
        /// active-bucket learner, and its curve's peak lateral G (the candidate bar is 85% of it).</summary>
        public int Crossings { get; internal set; }
        /// <summary>The slide guard is active (body angle at least 10 degrees within the last second).</summary>
        public bool Sliding { get; internal set; }
        /// <summary>The fast pre-confirmation estimate of the selected key's active bucket: corners' worth of
        /// evidence, and crossings the slide guard ignored.</summary>
        public double EarlyWeight { get; internal set; }
        public long EarlyGuarded { get; internal set; }
        /// <summary>True when the driver's Manual window drove the output this frame (Manual selected, warm-up done);
        /// false in Auto and while Manual waits for its warm-up (the learned window applies then).</summary>
        public bool ManualApplied { get; internal set; }
        public double CurvePeakG { get; internal set; }

        // ---- context (not published) ---------------------------------------------------------------
        public string SourceIdentity { get; internal set; } = string.Empty;
        /// <summary>Source mode is Plugin Internal (not Custom).</summary>
        public bool SourceIsPluginInternal { get; internal set; } = true;
        /// <summary>The manual key data gate (30 s driving + maturity) is ready for this context.</summary>
        public bool ManualGateReady { get; internal set; }
        /// <summary>True on the frame a manual slot was seeded from the learned window - the caller
        /// saves the config and bumps the key data revision.</summary>
        public bool KeyDataSeeded { get; internal set; }
        /// <summary>Learner state was handed to the persistence sink this frame.</summary>
        public bool LearnerSaved { get; internal set; }
    }
}
