using System;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.SlipAngle
{
    /// <summary>
    /// Plain persisted snapshot of one <see cref="SlipAngleWindowLearner"/> (plain public fields only).
    /// The crossing gate's transient trend/hold state (and any crossing event still inside its hold
    /// window) is deliberately NOT part of it: a restored learner starts with a fresh gate.
    /// </summary>
    internal static class SlipAngleLearnerStateExtensions
    {
        public static SlipAngleLearnerState WithEarly(this SlipAngleLearnerState s, SlipAngleEarlyEstimator early)
        {
            early?.Export(s);
            return s;
        }
    }

    /// <remarks>
    /// SINGLE VALUES (owner, 2026-11-21; docs\parameters-file-format-report.md). The arrays of earlier files -
    /// <see cref="Bins"/>, <see cref="CrossingSnapshots"/>, <see cref="BestHistory"/>, <see cref="EarlyRecentMax"/> -
    /// still load, but a save writes their summaries instead (<see cref="Curve"/>, the Snapshot*, Cluster*, Best*
    /// and EarlyRecent* fields). What the key data and its indicator show is saved as it was
    /// (<see cref="LearnedMin"/>..<see cref="LearnedMax"/>, <see cref="WindowQuality"/>, the consistent crossings
    /// in <see cref="ClusterRun"/>) and read back exactly until new driving changes it.
    /// </remarks>
    public sealed class SlipAngleLearnerState
    {
        /// <summary>An older file's grip curve, bin by bin (null in a new save: see <see cref="Curve"/>).</summary>
        public GripCurveBinsState Bins;
        public bool ShouldSerializeBins() => Bins != null;

        /// <summary>The grip curve as single values (Best / Min / peakG / shape check).</summary>
        public GripCurveSummary Curve;

        /// <summary>The learned window and its quality as they were at the save - read back exactly until the next
        /// learnable frame reaches the curve. A <see cref="LearnedMax"/> of 0 means "not saved" (an older file).</summary>
        public double LearnedMin;
        public double LearnedBest;
        public double LearnedMax;
        public double WindowQuality;

        /// <summary>The recent crossing snapshots as two groups: the older ones and the newest
        /// <see cref="CorroboratingSnapshots"/> (the level epoch), each by its count, lowest and highest.</summary>
        public int SnapshotCount;
        public double OlderSnapshotLow;
        public double OlderSnapshotHigh;
        public double EpochSnapshotLow;
        public double EpochSnapshotHigh;

        /// <summary>The largest group of recent crossings that agree within 15% (the confirmation cluster): how many,
        /// lowest and highest. After a restart the rebuilt snapshots count toward confirmation ONLY as this group, so
        /// evenly spread rebuilt values can never form a cluster that did not exist.</summary>
        public int ClusterRun;
        public double ClusterLow;
        public double ClusterHigh;

        /// <summary>The recent Best samples by count, mean and (population) standard deviation - all the stability
        /// term uses.</summary>
        public int BestCount;
        public double BestMean;
        public double BestSpread;

        /// <summary>The early estimator's last few Max values by count, lowest, highest and mean.</summary>
        public int EarlyRecentCount;
        public double EarlyRecentLow;
        public double EarlyRecentHigh;
        public double EarlyRecentMean;

        /// <summary>The corroborated Max level, degrees (Level 0 = no Max taught yet).</summary>
        public CorroboratedLevelState Max;

        /// <summary>Onset snapshots (degrees) of the most recent completed crossing events, oldest first,
        /// at most <see cref="SlipAngleWindowLearner.RecentCrossingCapacity"/> - the confirmation evidence.</summary>
        public double[] CrossingSnapshots;
        public bool ShouldSerializeCrossingSnapshots() => CrossingSnapshots != null;

        /// <summary>Completed crossing events, saturating at <see cref="SlipAngleWindowLearner.CounterSaturationCap"/>.</summary>
        public int CrossingCount;

        /// <summary>The level epoch: how many of the newest <see cref="CrossingSnapshots"/> completed after the
        /// Max level last moved by more than the tolerance floor - only those may corroborate a move.</summary>
        public int CorroboratingSnapshots;

        /// <summary>True once the peak has been confirmed (latched).</summary>
        public bool PeakConfirmed;

        /// <summary>Maturity observations, saturating.</summary>
        public long Observations;

        /// <summary>Maturity corners, saturating.</summary>
        public int Corners;

        /// <summary>True while a corner excursion (above 70% of peakG, not yet below 40%) is open.</summary>
        public bool InExcursion;

        /// <summary>Learnable time left before the next observation may be counted (10/s limit).</summary>
        public double ObservationWaitSeconds;

        /// <summary>The raise-only scale (at least 1) applied to the prior while the peak is unconfirmed.</summary>
        public double PriorScale;

        /// <summary>The last published Best values (degrees, oldest first, at most
        /// <see cref="SlipAngleWindowLearner.BestHistoryCapacity"/>) - the window-quality stability evidence.</summary>
        public double[] BestHistory;
        public bool ShouldSerializeBestHistory() => BestHistory != null;

        /// <summary>Learnable time left before the next Best sample.</summary>
        public double BestSampleWaitSeconds;

        /// <summary>The prior window (degrees) <see cref="PriorScale"/> is relative to - the borrowed window
        /// the keyed learner gave it, see <see cref="SlipAngleWindowLearner.SetPrior"/>. All zero (an older
        /// state): the constructor prior is kept.</summary>
        public double PriorMin;
        public double PriorBest;
        public double PriorMax;

        /// <summary>The fast pre-confirmation estimate (<see cref="SlipAngleEarlyEstimator"/>): corners' worth of
        /// evidence, its weighted angle sum (degrees x weight), crossings taught and guarded (long), and the
        /// starting Max it is blended from. All zero in an older state: it starts fresh.</summary>
        public double EarlyWeight;
        public double EarlyWeightedAngle;
        public long EarlyCrossings;
        public long EarlyGuarded;
        public double EarlyStartMax;
        /// <summary>The indicator's latch (full evidence and a steady Max) and the last few Max values behind its
        /// stability term.</summary>
        public bool EarlyMatured;
        /// <summary>The last stability above 0 (owner, 2026-11-22: a corner that knocks stability to 0 uses this instead).</summary>
        public double EarlyValidStability;
        /// <summary>The highest Max maturity shown so far: the indicator never falls below it (owner, 2026-11-22).</summary>
        public double MaxMaturityFloor;
        public double[] EarlyRecentMax;
        public bool ShouldSerializeEarlyRecentMax() => EarlyRecentMax != null;
    }

    /// <summary>
    /// Learns one Slip Angle window (docs\slip-angle-design.md 9.5 steps 4-6, 9.5a, 9.5b) from valid
    /// (alpha, |lateral G|) frames.
    /// <para><b>Max - crossings only.</b> A frame is a crossing CANDIDATE when |lateral G| is at least
    /// <see cref="CandidateShareOfPeak"/> of the curve's <see cref="GripCurveBins.PeakG"/> (nothing is a
    /// candidate while the curve is cold) AND the curve-plausibility check (9.5b.4: at most
    /// <see cref="PlausibilityMargin"/> x the curve's expected G at that alpha - skipped while the curve
    /// has no value there - and at most that margin x peakG) has held on every frame of the gate's trend
    /// (the detection frame and the <see cref="CrossingGateParameters.TrendFrames"/> before it - "the
    /// lateral G just before it"). A <see cref="CrossingGate"/> (<see cref="CrossingGateParameters.SlipAngle"/>,
    /// collapse test off, no saturated-wheel guard, walk-back band 0.10, non-crossing weight 0) detects
    /// the crossing and holds its onset snapshot.</para>
    /// <para><b>Event lifecycle.</b> Detection opens a PENDING event. After
    /// <see cref="ImpactGuardParameters.RetroFrames"/> further qualified frames its corroboration is
    /// DECIDED (below); a corroborated event then teaches the Max estimator its snapshot on every remaining
    /// qualified frame of the hold. The event is COMPLETED - added to the recent-snapshot ring, counted in
    /// <see cref="CrossingCount"/>, eligible for confirmation and as a corroborator - only when the hold
    /// expires naturally. Any excluded frame during the hold (impact, wobble, yaw residual, gating) resets
    /// the gate and DISCARDS the event: nothing counted, and whatever it taught the estimator is rolled
    /// back to the state saved at the decision.</para>
    /// <para><b>Event corroboration (9.5b.5).</b> The Max level is a <see cref="CorroboratedLevelEstimator"/>
    /// in degrees. The estimator itself corroborates per observation and one 1 s hold is ~60 observations,
    /// so an event may teach only when an earlier, SEPARATE completed event corroborates it: within the
    /// band of the new snapshot (raise tolerance 30% when the snapshot is above the current level, lower
    /// tolerance 15% when below, never narrower than the 0.2 deg floor) AND on the same side of the current
    /// level by more than that floor AND completed after the level last moved by more than that floor (the
    /// "level epoch", persisted). With no level yet, any earlier event within band counts. So the cluster
    /// that set Max - including its natural scatter - can never corroborate a move away from it: a single
    /// fake crossing can neither raise nor lower Max; two separate new ones on the same side can.</para>
    /// <para><b>Confirmation (9.5.5), latched.</b> Checked only when an event completes (so never on
    /// teaching that could still be rolled back): the peak becomes confirmed when at least 3 recent
    /// completed events lie pairwise within 15% (lowest at least 85% of highest), the curve is not still
    /// rising at its top (<see cref="GripCurveBins.IsStillRisingAtTop"/>) and a Max level exists. Once
    /// confirmed it stays confirmed (persisted): the shape check can no longer un-confirm it or raise the
    /// prior, and Max moves only by corroboration. Unconfirmed: the published window is the prior times
    /// <see cref="SlipAngleLearnerState.PriorScale"/>, which only ever grows: when the curve is clearly still
    /// rising at its highest populated bin (with a real populated bin <see cref="GripCurveBins.RisingLookbackDeg"/>
    /// below it) at an alpha above the published Best, the scale grows so Best equals that alpha (Max capped
    /// at 45 deg). Confirmed: Max = the corroborated level, Best/Min = <see cref="GripCurveBins.TryBestAndMin"/>;
    /// if that fails, the prior scaled to the learned Max.</para>
    /// <para><b>Retro delay line (9.5b.2).</b> Learnable frames reach the curve, the maturity counts, the
    /// prior raise and the Best history only <see cref="ImpactGuardParameters.RetroFrames"/> frames late;
    /// an <see cref="ImpactVerdict.RetroInvalidate"/> drops the frames still in the line, so the frames just
    /// before an impact never teach. <see cref="Export"/> is side-effect-free and omits the at most
    /// <see cref="ImpactGuardParameters.RetroFrames"/> frames still in the line.</para>
    /// <para><b>Maturity (9.5.6).</b> Independent of confirmation: an observation is a learnable frame
    /// with |lateral G| at least 70% of peakG, counted at most 10 per second of learnable time; a corner is
    /// one rise above 70% then fall below 40% of peakG.
    /// <c>Maturity = clamp01(min(observations / 50, corners / 3))</c>.</para>
    /// <para><b>Window quality (9.5.6).</b> coverage x stability, capped at <see cref="UnconfirmedQualityCap"/>
    /// (a min, not a multiplier) while unconfirmed. Coverage = share of populated bins across the published
    /// [Min, End]; stability = (n / <see cref="BestHistoryCapacity"/>) / (1 + cv / <see cref="StabilityCvReference"/>)
    /// over the last n Best samples (one per second of learnable time), cv = their standard deviation over
    /// their mean - saturating and continuous.</para>
    /// <para><b>Protection.</b> A frame with <c>learnAllowed</c> false (gating, landing wobble, hop) or
    /// <see cref="ImpactVerdict.LearnAllowed"/> false (impact exclusion, implausible G, yaw residual)
    /// teaches nothing - no curve, no candidates, no maturity - and resets the gate, so a crossing can
    /// never straddle an excluded stretch.</para>
    /// <para><b>Learning never stops</b>: every learnable frame keeps feeding everything, after maturity
    /// and confirmation alike; counters saturate at <see cref="CounterSaturationCap"/>.</para>
    /// Not thread-safe: the data thread owns it.
    /// </summary>
    public sealed class SlipAngleWindowLearner
    {
        /// <summary>Candidate: |lateral G| at least this share of peakG (9.5a).</summary>
        public const double CandidateShareOfPeak = 0.85;
        /// <summary>Curve-plausibility margin (9.5b.4).</summary>
        public const double PlausibilityMargin = 1.15;
        /// <summary>Maturity observation / corner-start threshold, share of peakG.</summary>
        public const double ObservationShareOfPeak = 0.70;
        /// <summary>Corner end threshold, share of peakG.</summary>
        public const double CornerEndShareOfPeak = 0.40;
        /// <summary>Minimum learnable time between two counted observations (10 per second).</summary>
        public const double ObservationIntervalSeconds = 0.1;
        public const double ObservationsToMature = 50.0;
        public const double CornersToMature = 3.0;
        /// <summary>Consistent events needed to confirm the peak.</summary>
        public const int ConfirmingCrossings = 3;
        /// <summary>Confirming events lie pairwise within this share.</summary>
        public const double ConfirmationTolerance = 0.15;
        /// <summary>Recent crossing snapshots kept (persisted) for confirmation and corroboration.</summary>
        public const int RecentCrossingCapacity = 16;
        /// <summary>Window quality cap while the peak is unconfirmed (9.5.6).</summary>
        public const double UnconfirmedQualityCap = 0.6;
        /// <summary>Best samples kept for the stability term ("the last N updates").</summary>
        public const int BestHistoryCapacity = 16;
        /// <summary>Learnable time between two Best samples.</summary>
        public const double BestSampleIntervalSeconds = 1.0;
        /// <summary>Coefficient of variation of Best at which stability halves.</summary>
        public const double StabilityCvReference = 0.02;
        /// <summary>Counters saturate here (the shared overflow cap).</summary>
        public const int CounterSaturationCap = GripLearner.SampleCountSaturationCap;
        /// <summary>Learned windows never exceed the gate's source ceiling - the 45-degree plausibility
        /// bound (only alphas within it are considered for learning; every alpha is still mapped).</summary>
        public const double MaxCeilingDeg = 45.0;

        /// <summary>THE LEARNED-MAX CAP, native tyre slip angle (owner, 2026-11-19): a car's best slip
        /// angle stays at or below ~15 degrees, so the learned (and typed) Max is at most 20 and End
        /// (= 1.5 x Max, output 100) at most 30.</summary>
        public const double MaxCapNativeDeg = 20.0;

        /// <summary>The learned-Max cap for the car-angle fallback (body slip, which runs larger than the
        /// tyres' own angle): Max at most 30, End at most 45.</summary>
        public const double MaxCapCarAngleDeg = 30.0;

        /// <summary>The learned-Max cap for a key's source identity: <see cref="MaxCapCarAngleDeg"/> for the
        /// car-angle fallback, <see cref="MaxCapNativeDeg"/> otherwise (native, and Custom - the driver's
        /// own per-wheel tyre slip angles).</summary>
        public static double MaxCapDegFor(string sourceIdentity) =>
            sourceIdentity == SlipAngleFrameProcessor.FallbackIdentity ? MaxCapCarAngleDeg
            : sourceIdentity == SlipAngleFrameProcessor.NativeRatioIdentity ? MaxCapRatioDeg
            : MaxCapNativeDeg;

        /// <summary>The learned-Max cap for a native slip-RATIO source (owner, 2026-11-22): 22.5 degrees = ratio 2.25.</summary>
        public const double MaxCapRatioDeg = SlipAngleSourceScale.MaxCapDeg;

        private const double NominalFrameSeconds = 1.0 / 60.0;
        private const double TimeEpsilon = 1e-9;

        private static readonly int RetroFrames = ImpactGuardParameters.SlipAngle.RetroFrames;

        /// <summary>
        /// The Max estimator's tuning in degrees: cap 45 (the gate ceiling), jump 15, tolerance 0.30 / 0.15,
        /// floor 0.2, cooldown 3 / 40, base 0.10 (spec values); hit cap 1,000,000 (the shared overflow cap);
        /// stability reset 0.01 (a 1% relative move - unit-free, GripLearner's value); level floor 0.5 deg
        /// (no real tyre peaks below it; GripLearner's 0.1 g floor plays the same "never collapse" role).
        /// </summary>
        public static CorroborationParameters MaxParameters => new CorroborationParameters
        {
            PlausibleCap = MaxCeilingDeg,
            MaxPlausibleJump = 15.0,
            RaiseTolerance = 0.30,
            LowerTolerance = 0.15,
            ToleranceFloor = 0.2,
            RaiseCooldown = 3,
            LowerCooldown = 40,
            ConfidenceBase = 0.10,
            HitSaturationCap = 1_000_000,
            StabilityMoveResetFraction = 0.01,
            LevelFloor = 0.5,
        };

        private SlipAngleWindow _prior;
        private readonly double _maxCapDeg;
        private readonly CorroborationParameters _maxParams = MaxParameters;
        private readonly GripCurveBins _bins = new GripCurveBins();
        private readonly CrossingGate _gate;
        private readonly SlipAngleEarlyEstimator _early;
        private readonly Ring _snapshots = new Ring(RecentCrossingCapacity);
        private readonly Ring _bestHistory = new Ring(BestHistoryCapacity);

        private CorroboratedLevelState _max;
        private int _crossingCount;
        private int _corroboratorCount;   // the level epoch: newest ring entries completed since the last move
        private bool _confirmed;
        private long _observations;
        private int _corners;
        private bool _inExcursion;
        private double _observationWait;
        private double _priorScale = 1.0;
        private double _bestSampleWait;

        // After a restart from single values: what was shown at the save, read back exactly until new driving changes
        // it: the window and quality until the next learnable frame reaches the curve (the rebuilt curve is close to,
        // not identical with, the original). The consistent crossings read back exactly through the restored cluster.
        private bool _pinWindowActive;
        private SlipAngleWindow _pinWindow;
        // The loaded curve and Best summaries, saved back as they were until the curve moves on - so a restart with no
        // driving writes exactly what it read and repeated restarts never drift the rebuilt curve.
        private GripCurveSummary _loadedCurve;
        private bool _loadedBestActive;
        private int _loadedBestCount;
        private double _loadedBestMean;
        private double _loadedBestSpread;
        private double _pinMaxLevel;
        private double _pinQuality = double.NaN;

        // After a restart from single values the oldest _restoredInRing snapshots are rebuilt (evenly spread between
        // their saved low and high), so they count toward a confirmation cluster only as the cluster they really had:
        // _restoredRun values from _restoredLo to _restoredHi. They still corroborate as rebuilt.
        private int _restoredInRing;
        private int _restoredRun;
        private double _restoredLo;
        private double _restoredHi;

        // Transient (not persisted): the retro delay line.
        private readonly double[] _delayAlpha = new double[RetroFrames];
        private readonly double[] _delayG = new double[RetroFrames];
        private readonly double[] _delayDt = new double[RetroFrames];
        private int _delayCount;
        private int _delayOldest;

        // Transient (not persisted): the live event inside the gate's hold window.
        private bool _eventPending;
        /// <summary>The current event's car angle at its crossing and whether it is a slip-ratio event (the car-angle minimum).</summary>
        private double _eventCarAngle;
        private bool _eventRatio;
        private bool _eventDecided;
        private int _pendingFrames;
        private double _eventSnapshot;
        private bool _teaching;
        private CorroboratedLevelState _maxBeforeEvent;
        private int _framesSincePlausibilityFailure;

        // Crossing gate switch (not persisted) and, while it is off, the live candidate run.
        private bool _crossingGateEnabled = true;
        private bool _runActive;
        private int _gateOffRuns;
        private readonly bool[] _delayCandidate = new bool[RetroFrames];
        private readonly double[] _sortScratch = new double[2 * RecentCrossingCapacity];

        /// <param name="prior">The cold-start window (must be valid).</param>
        /// <param name="maxCapDeg">The learned-Max cap for this key's source
        /// (<see cref="MaxCapDegFor"/>); every taught alpha, crossing snapshot and published Max is at
        /// most this. Defaults to the 45-degree plausibility bound (no extra cap).</param>
        public SlipAngleWindowLearner(SlipAngleWindow prior, double maxCapDeg = MaxCeilingDeg)
        {
            if (!prior.IsValid) throw new ArgumentException("The prior window must be valid.", nameof(prior));
            _prior = prior;
            _maxCapDeg = ClampMath.IsFinite(maxCapDeg) && maxCapDeg > 0.0 ? Math.Min(maxCapDeg, MaxCeilingDeg) : MaxCeilingDeg;
            _gate = new CrossingGate(CrossingGateParameters.SlipAngle)
            {
                MaxGCollapseFractionPerSecond = 0.0,
                MinSaturatedWheelsForRejection = 0,
                WalkBackGBandFraction = 0.10,
                NonCrossingWeight = 0.0,
            };
            _early = new SlipAngleEarlyEstimator(_maxCapDeg);
        }

        /// <summary>The fast pre-confirmation estimate (see <see cref="SlipAngleEarlyEstimator"/>).</summary>
        public SlipAngleEarlyEstimator Early => _early;

        /// <summary>The current prior (before the raise-only scale).</summary>
        public SlipAngleWindow Prior => _prior;

        /// <summary>This learner's learned-Max cap, degrees (see the constructor).</summary>
        public double MaxCapDeg => _maxCapDeg;

        private double AtMostCap(double alphaDeg) =>
            ClampMath.IsFinite(alphaDeg) && alphaDeg > _maxCapDeg ? _maxCapDeg : alphaDeg;

        /// <summary>
        /// Replaces the prior (the keyed learner re-priors an unconfirmed key when a better borrowing tier
        /// appears). The raise-only evidence is kept in ABSOLUTE terms: the alpha the old scale raised Best
        /// to (old prior Best x scale, when scaled) is re-expressed against the new prior - the new scale is
        /// that alpha / new Best when above it (capped at the 45 deg ceiling), else 1. Learned state (curve,
        /// Max, confirmation) is untouched; once confirmed the prior no longer publishes. Data thread only.
        /// </summary>
        public void SetPrior(SlipAngleWindow prior)
        {
            if (!prior.IsValid) throw new ArgumentException("The prior window must be valid.", nameof(prior));
            double raisedTo = _priorScale > 1.0 ? _prior.Best * _priorScale : 0.0;
            _prior = prior;
            _priorScale = raisedTo > prior.Best
                ? Math.Max(1.0, Math.Min(raisedTo / prior.Best, _maxCapDeg / prior.Max))
                : 1.0;
        }

        /// <summary>Completed crossing events (saturating).</summary>
        public int CrossingCount => _crossingCount;

        /// <summary>Counted maturity observations (saturating).</summary>
        public long Observations => _observations;

        /// <summary>Counted corners (saturating).</summary>
        public int Corners => _corners;

        /// <summary><c>clamp01(min(observations / 50, corners / 3))</c>.</summary>
        public double Maturity =>
            ClampMath.To01(Math.Min(_observations / ObservationsToMature, _corners / CornersToMature));

        /// <summary>Latched: true once 3 consistent completed crossings, a curve not still rising at its
        /// top and a taught Max level have been seen together.</summary>
        public bool PeakConfirmed => _confirmed;

        /// <summary>Read-only pass-through: the curve's expected |lateral G| at <paramref name="alphaDeg"/>
        /// (<see cref="GripCurveBins.ExpectedLatG"/>; NaN with no curve) - for the surface classifier.</summary>
        public double ExpectedLatG(double alphaDeg) => _bins.ExpectedLatG(alphaDeg);

        /// <summary>Read-only pass-through: the curve's <see cref="GripCurveBins.PeakG"/> (0 with no curve).</summary>
        public double PeakG => _bins.PeakG;

        /// <summary>Diagnostics: observations recorded in curve bin <paramref name="bin"/> (<see cref="GripCurveBins.SampleCount"/>).</summary>
        public int CurveSampleCount(int bin) => _bins.SampleCount(bin);

        /// <summary>Diagnostics: the curve's own shape check (<see cref="GripCurveBins.IsStillRisingAtTop"/>), which no
        /// longer affects a confirmed peak.</summary>
        public bool CurveStillRisingAtTop => _bins.IsStillRisingAtTop;

        /// <summary>Until confirmed: the fast estimate when it has one (the prior scaled to its Max), else the prior
        /// (raise-only scaled). Once confirmed: the learned window.</summary>
        public SlipAngleWindow Published
        {
            get
            {
                if (!_confirmed || !(_max.Level > 0.0))
                {
                    if (_early.HasEstimate && _prior.Max > 0.0) return Scaled(_prior, _early.Max / _prior.Max).CappedAt(_maxCapDeg);
                    return Scaled(_prior, _priorScale).CappedAt(_maxCapDeg);
                }
                if (_pinWindowActive && _max.Level == _pinMaxLevel) return _pinWindow;
                double max = Math.Min(_max.Level, _maxCapDeg);
                if (_bins.TryBestAndMin(max, out double best, out double min))
                    return new SlipAngleWindow(min, best, max);
                double s = max / _prior.Max;
                return new SlipAngleWindow(_prior.Min * s, _prior.Best * s, max);
            }
        }

        /// <summary>Display / blend weighting only: coverage x stability, at most
        /// <see cref="UnconfirmedQualityCap"/> while unconfirmed (see the class remarks).</summary>
        public double WindowQuality
        {
            get
            {
                if (!double.IsNaN(_pinQuality)) return _pinQuality;
                SlipAngleWindow w = Published;
                int total = 0, populated = 0;
                for (int i = 0; i < GripCurveBins.BinCount; i++)
                {
                    double c = GripCurveBins.BinCenterDeg(i);
                    if (c < w.Min || c > w.End) continue;
                    total++;
                    if (_bins.SampleCount(i) >= GripCurveBins.MinSamplesPerBin) populated++;
                }
                double coverage = total > 0 ? (double)populated / total : 0.0;
                double raw = ClampMath.To01(coverage * Stability());
                return _confirmed ? raw : Math.Min(raw, UnconfirmedQualityCap);
            }
        }

        private double Stability()
        {
            int n = _bestHistory.Count;
            if (n == 0) return 0.0;
            double mean = 0.0;
            for (int i = 0; i < n; i++) mean += _bestHistory.Get(i);
            mean /= n;
            double variance = 0.0;
            for (int i = 0; i < n; i++) { double d = _bestHistory.Get(i) - mean; variance += d * d; }
            variance /= n;
            double cv = mean > 0.0 ? Math.Sqrt(variance) / mean : 0.0;
            return (double)n / BestHistoryCapacity / (1.0 + cv / StabilityCvReference);
        }

        /// <summary>
        /// One frame. <paramref name="learnAllowed"/> is the learning gate (on ground, speed, no
        /// wobble/hop, not pit/replay); <paramref name="impact"/> the crash/contact guard's verdict.
        /// </summary>
        public void Observe(double alphaDeg, double absLatG, ImpactVerdict impact, bool learnAllowed, double dt) =>
            Observe(alphaDeg, absLatG, impact, learnAllowed, dt, null);

        /// <summary>As the five-argument form, plus <paramref name="early"/> (the frame processor's context): with it,
        /// the fast pre-confirmation estimate learns too, and a crossing event while the car is sliding never
        /// teaches Max (the slide guard). Without it the learner behaves exactly as before.</summary>
        public void Observe(double alphaDeg, double absLatG, ImpactVerdict impact, bool learnAllowed, double dt, SlipAngleEarlyContext? early)
        {
            NoteMaturityFloor();   // what the previous frame taught
            double g = Math.Abs(absLatG);
            if (impact.RetroInvalidate) _delayCount = 0;   // the frames just before the impact never teach
            if (impact.RetroInvalidate || !impact.LearnAllowed || !learnAllowed
                || !ClampMath.IsFinite(alphaDeg) || alphaDeg < 0.0 || !ClampMath.IsFinite(g))
            {
                CancelGate();
                _early.Cancel();
                return;
            }

            double aged = ClampMath.IsFinite(dt) && dt > 0.0 ? dt : NominalFrameSeconds;
            if (early.HasValue)
            {
                SlipAngleEarlyContext ctx = early.Value;
                if (!_confirmed) _early.Observe(alphaDeg, ctx, Scaled(_prior, _priorScale).CappedAt(_maxCapDeg).Max, aged);
                // SLIDE GUARD on the confirmed path: an event while the car slides teaches nothing (an angle source; a
                // slip ratio has the car-angle minimum instead - owner, 2026-11-22).
                if (ctx.SlideGuard && (_eventPending || _eventDecided))
                {
                    if (_teaching) _max = _maxBeforeEvent;
                    ClearEvent();
                }
            }
            double peak = _bins.PeakG;

            if (peak > 0.0 && IsPlausible(alphaDeg, g, peak))
            {
                if (_framesSincePlausibilityFailure < int.MaxValue) _framesSincePlausibilityFailure++;
            }
            else
            {
                _framesSincePlausibilityFailure = 0;
            }
            bool candidate = peak > 0.0 && g >= CandidateShareOfPeak * peak
                             && _framesSincePlausibilityFailure > _gate.TrendFrames;
            // THE CAR-ANGLE MINIMUM (slip-ratio source, owner, 2026-11-22): the largest car angle of the current event.
            SlipAngleEarlyContext? ctxRatio = early.HasValue && early.Value.RatioSource ? early : null;
            double carAngle = ctxRatio.HasValue && ClampMath.IsFinite(ctxRatio.Value.CarAngleDeg) ? ctxRatio.Value.CarAngleDeg : 0.0;
            if (!_crossingGateEnabled)
            {
                // Gate off: a slip-ratio frame below the car-angle minimum is no candidate.
                if (ctxRatio.HasValue && ctxRatio.Value.CarAngleTooSmallForRatio(carAngle)) candidate = false;
                // Gate off: the candidate flag rides the retro delay line; the run lifecycle and its
                // teaching happen as the frame leaves it (Apply), so the frames just before an impact
                // never teach Max either.
                Enqueue(alphaDeg, g, aged, candidate);
                return;
            }

            bool qualified = _gate.Observe(alphaDeg, alphaDeg, g, candidate, aged, 0);

            if (_gate.CrossedThisFrame)
            {
                // The previous hold may have expired on this very frame (the gate ages before it re-arms).
                if (_eventDecided) CompleteEvent();
                ClearEvent();
                _eventPending = true;
                _eventCarAngle = carAngle;
                _eventRatio = ctxRatio.HasValue;
            }
            else if (!qualified)
            {
                if (_eventDecided) CompleteEvent();   // the hold expired naturally
                ClearEvent();
            }
            else if (_eventPending && !_eventDecided)
            {
                if (++_pendingFrames >= RetroFrames)
                {
                    // A slip-ratio event whose car angle never reached the minimum is not at the limit: dropped.
                    if (_eventRatio && !(_eventCarAngle >= SlipAngleEarlyEstimator.RatioMinCarAngleDeg)) ClearEvent();
                    else DecideEvent(AtMostCap(_gate.TeachingBasis(alphaDeg)));
                }
            }

            if (_teaching && _gate.TeachingWeight() > 0.0)
                CorroboratedLevelEstimator.Observe(ref _max, AtMostCap(_gate.TeachingBasis(alphaDeg)), _maxParams);

            Enqueue(alphaDeg, g, aged);
        }

        /// <summary>
        /// Crossing gate on (default, 9.5a) or off. Off (Lock/Slip parity, rulings R29/R30): the gate is
        /// bypassed and EVERY candidate frame (the same candidate test - share of peakG and the
        /// plausibility trend - behind the same impact / gating protections) teaches its own alpha to the
        /// same corroborated Max estimator at weight 1, as it leaves the retro delay line. Each contiguous
        /// candidate run is handled exactly like a gate-on event: Max is saved when the run starts, the run
        /// teaches, and any cancel (excluded frame, impact / retro invalidation, switching the gate)
        /// restores the saved Max and stops the frames still in the delay line from teaching. A run is
        /// COMMITTED only when a learnable non-candidate frame ends it: the estimator's level joins the
        /// recent-event ring, counts in <see cref="CrossingCount"/> (so the keyed hot weight still grows)
        /// and starts a new level epoch when the run moved the level by more than the floor. Confirmation
        /// then needs a Max level and a curve not still rising at its top, plus either 3 consistent
        /// recorded levels (as with crossings) or - after at least 2 committed runs - the curve's own shape
        /// yielding Best/Min below that level. Not persisted (a settings value, re-applied each session).
        /// </summary>
        public bool CrossingGateEnabled
        {
            get => _crossingGateEnabled;
            set
            {
                if (value == _crossingGateEnabled) return;
                CancelGate();
                _crossingGateEnabled = value;
            }
        }

        /// <summary>Committed gate-off runs before the shape check alone may confirm (R30).</summary>
        public const int GateOffShapeConfirmRuns = 2;

        /// <summary>Gate off: one learnable frame leaving the delay line (see <see cref="CrossingGateEnabled"/>).</summary>
        private void ApplyWithoutGate(double alphaDeg, bool candidate)
        {
            if (candidate)
            {
                if (!_runActive)
                {
                    _runActive = true;
                    _teaching = true;
                    _maxBeforeEvent = _max;
                }
                CorroboratedLevelEstimator.Observe(ref _max, AtMostCap(alphaDeg), _maxParams);
                return;
            }
            if (_runActive) CommitRun();
        }

        private void CommitRun()
        {
            bool moved = Math.Abs(_max.Level - _maxBeforeEvent.Level) > _maxParams.ToleranceFloor;
            _runActive = false;
            _teaching = false;
            double level = _max.Level;
            if (!ClampMath.IsFinite(level) || !(level > 0.0)) return;
            if (moved) _corroboratorCount = 0;   // a new level epoch, exactly like a gate-on event
            AddSnapshot(Math.Min(level, _maxCapDeg));
            if (_corroboratorCount < _snapshots.Count) _corroboratorCount++;
            if (_crossingCount < CounterSaturationCap) _crossingCount++;
            if (_gateOffRuns < int.MaxValue) _gateOffRuns++;
            if (!_confirmed && !_bins.IsStillRisingAtTop
                && (HasConsistentCluster()
                    || (_gateOffRuns >= GateOffShapeConfirmRuns && _bins.TryBestAndMin(Math.Min(level, _maxCapDeg), out _, out _))))
                _confirmed = true;
        }

        private bool IsPlausible(double alphaDeg, double g, double peak)
        {
            if (g > PlausibilityMargin * peak) return false;
            double expected = _bins.ExpectedLatG(alphaDeg);
            return !ClampMath.IsFinite(expected) || g <= PlausibilityMargin * expected;
        }

        private void DecideEvent(double snapshot)
        {
            _eventDecided = true;
            _eventSnapshot = snapshot;
            _teaching = IsCorroborated(snapshot);
            if (_teaching) _maxBeforeEvent = _max;
        }

        private bool IsCorroborated(double snapshot)
        {
            if (!ClampMath.IsFinite(snapshot) || snapshot <= 0.0) return false;
            double level = _max.Level;
            bool hasLevel = level > 0.0;
            bool raising = !hasLevel || snapshot > level;
            double floor = _maxParams.ToleranceFloor;
            double band = Math.Max((raising ? _maxParams.RaiseTolerance : _maxParams.LowerTolerance) * snapshot, floor);
            int first = hasLevel ? _snapshots.Count - _corroboratorCount : 0;
            for (int i = first; i < _snapshots.Count; i++)
            {
                double e = _snapshots.Get(i);
                if (Math.Abs(e - snapshot) > band) continue;
                if (!hasLevel) return true;
                if (raising ? e > level + floor : e < level - floor) return true;
            }
            return false;
        }

        private void CompleteEvent()
        {
            if (!ClampMath.IsFinite(_eventSnapshot) || !(_eventSnapshot > 0.0)) return;
            // A completed event whose teaching moved the level by more than the floor starts a new level
            // epoch: events completed before it no longer corroborate. The event itself completed after
            // the move, so it belongs to the new epoch.
            if (_teaching && Math.Abs(_max.Level - _maxBeforeEvent.Level) > _maxParams.ToleranceFloor)
                _corroboratorCount = 0;
            AddSnapshot(_eventSnapshot);
            if (_corroboratorCount < _snapshots.Count) _corroboratorCount++;
            if (_crossingCount < CounterSaturationCap) _crossingCount++;
            UpdateConfirmation();
        }

        private void ClearEvent()
        {
            _eventPending = false;
            _eventDecided = false;
            _pendingFrames = 0;
            _eventSnapshot = 0.0;
            _teaching = false;
        }

        private void CancelGate()
        {
            _gate.Reset();
            _framesSincePlausibilityFailure = 0;
            if (_teaching) _max = _maxBeforeEvent;   // a discarded event (or gate-off run) teaches nothing
            ClearEvent();
            // Gate off: the cut run records nothing, and its frames still in the delay line never teach.
            _runActive = false;
            Array.Clear(_delayCandidate, 0, _delayCandidate.Length);
        }

        private void Enqueue(double alphaDeg, double g, double aged, bool candidate = false)
        {
            if (_delayCount == RetroFrames)
            {
                Apply(_delayAlpha[_delayOldest], _delayG[_delayOldest], _delayDt[_delayOldest], _delayCandidate[_delayOldest]);
                _delayOldest = (_delayOldest + 1) % RetroFrames;
                _delayCount--;
            }
            int slot = (_delayOldest + _delayCount) % RetroFrames;
            _delayAlpha[slot] = alphaDeg;
            _delayG[slot] = g;
            _delayDt[slot] = aged;
            _delayCandidate[slot] = candidate;
            _delayCount++;
        }

        /// <summary>A learnable frame leaving the delay line: curve, maturity, prior raise, Best history;
        /// with the crossing gate off also the Max run (<see cref="ApplyWithoutGate"/>).</summary>
        private void Apply(double alphaDeg, double g, double aged, bool candidate)
        {
            _pinWindowActive = false;   // the curve moves on: the rebuilt one speaks from here
            _pinQuality = double.NaN;
            _loadedCurve = null;
            _loadedBestActive = false;
            double peak = _bins.PeakG;
            _bins.Observe(alphaDeg, g);
            CountMaturity(g, peak, aged);
            if (!_confirmed) RaisePriorIfStillRising();
            SampleBest(aged);
            if (!_crossingGateEnabled) ApplyWithoutGate(alphaDeg, candidate);
        }

        private void UpdateConfirmation()
        {
            if (!_confirmed && _max.Level > 0.0 && HasConsistentCluster() && !_bins.IsStillRisingAtTop)
                _confirmed = true;
        }

        private void SampleBest(double aged)
        {
            _bestSampleWait = Math.Max(0.0, _bestSampleWait - aged);
            if (_bestSampleWait > TimeEpsilon) return;
            double best = Published.Best;
            if (ClampMath.IsFinite(best) && best > 0.0) _bestHistory.Add(best);
            _bestSampleWait = BestSampleIntervalSeconds;
        }

        /// <summary>The most recent crossings that agree within <see cref="ConfirmationTolerance"/>, capped at
        /// <see cref="ConfirmingCrossings"/> - the progress toward confirmation the maturity indicator shows
        /// (docs\learning-maturity-indicator-report.md). Read-only: uses its own scratch.</summary>
        public int ConsistentCrossings =>
            Math.Min(BestRun(new double[2 * RecentCrossingCapacity], out _, out _), ConfirmingCrossings);

        /// <summary>Adds a completed crossing to the ring; an evicted rebuilt snapshot leaves the restored group.</summary>
        private void AddSnapshot(double snapshot)
        {
            if (_snapshots.Count == RecentCrossingCapacity && _restoredInRing > 0) _restoredInRing--;
            _snapshots.Add(snapshot);
        }

        /// <summary>The largest group of crossings pairwise within <see cref="ConfirmationTolerance"/> (0 with none), its
        /// lowest and highest. Rebuilt snapshots take part only as their saved cluster (see the fields).</summary>
        private int BestRun(double[] scratch, out double low, out double high)
        {
            low = high = 0.0;
            int n = 0, rebuilt = Math.Min(_restoredInRing, _snapshots.Count), group = Math.Min(_restoredRun, rebuilt);
            for (int i = 0; i < group; i++)
                scratch[n++] = group == 1 ? _restoredLo : _restoredLo + (_restoredHi - _restoredLo) * i / (group - 1);
            for (int i = rebuilt; i < _snapshots.Count; i++) scratch[n++] = _snapshots.Get(i);
            if (n == 0) return 0;
            Array.Sort(scratch, 0, n);
            int best = 0;
            for (int i = 0, j = 0; i < n; i++)
            {
                if (j < i) j = i;
                while (j + 1 < n && scratch[i] >= (1.0 - ConfirmationTolerance) * scratch[j + 1]) j++;
                if (j - i + 1 > best) { best = j - i + 1; low = scratch[i]; high = scratch[j]; }
            }
            return best;
        }

        /// <summary>Maturity of the learned Max, 0..1: 1 once the peak is confirmed, otherwise the share of
        /// the <see cref="ConfirmingCrossings"/> consistent crossings seen, never above 0.99 (a full cluster
        /// still waits for the curve's top and a taught Max level).</summary>
        public double MaxMaturity => Math.Max(_maxMaturityFloor, RawMaxMaturity);

        private double RawMaxMaturity =>
            _confirmed || _early.Matured ? 1.0 : Math.Min(0.99, Math.Max(ConsistentCrossings / (double)ConfirmingCrossings, _early.Confidence));

        /// <summary>THE INDICATOR ONLY RISES (owner, 2026-11-22): the highest <see cref="MaxMaturity"/> seen - a corner that
        /// makes the computed value fall (Max moving, the stability term dropping) never takes the LED back down. Saved.</summary>
        private double _maxMaturityFloor;

        private void NoteMaturityFloor()
        {
            double raw = RawMaxMaturity;
            if (ClampMath.IsFinite(raw) && raw > _maxMaturityFloor) _maxMaturityFloor = raw;
        }

        private bool HasConsistentCluster() => BestRun(_sortScratch, out _, out _) >= ConfirmingCrossings;

        private void CountMaturity(double g, double peak, double aged)
        {
            _observationWait = Math.Max(0.0, _observationWait - aged);
            if (!(peak > 0.0)) return;
            if (g >= ObservationShareOfPeak * peak)
            {
                if (_observationWait <= TimeEpsilon)
                {
                    if (_observations < CounterSaturationCap) _observations++;
                    _observationWait = ObservationIntervalSeconds;
                }
                _inExcursion = true;
            }
            else if (_inExcursion && g < CornerEndShareOfPeak * peak)
            {
                if (_corners < CounterSaturationCap) _corners++;
                _inExcursion = false;
            }
        }

        /// <summary>Raise-only prior scaling (9.5.5): Best follows a curve still clearly rising above it.</summary>
        private void RaisePriorIfStillRising()
        {
            if (!_bins.IsStillRisingAtTop) return;
            int top = -1;
            for (int i = GripCurveBins.BinCount - 1; i >= 0; i--)
                if (_bins.SampleCount(i) >= GripCurveBins.MinSamplesPerBin) { top = i; break; }
            if (top < 0) return;
            // IsStillRisingAtTop is also true when there is too little curve to judge; raising needs a
            // real rise, i.e. a populated reference bin a full lookback below the top.
            int lookback = (int)Math.Round(GripCurveBins.RisingLookbackDeg / GripCurveBins.BinWidthDeg);
            bool hasReference = false;
            for (int i = top - lookback; i >= 0 && !hasReference; i--)
                hasReference = _bins.SampleCount(i) >= GripCurveBins.MinSamplesPerBin;
            if (!hasReference) return;

            double risingAlpha = GripCurveBins.BinCenterDeg(top);
            double scale = Math.Min(risingAlpha / _prior.Best, _maxCapDeg / _prior.Max);
            if (scale > _priorScale) _priorScale = scale;
        }

        private static SlipAngleWindow Scaled(SlipAngleWindow w, double s) =>
            new SlipAngleWindow(w.Min * s, w.Best * s, w.Max * s);

        /// <summary>
        /// A copy of the persistent state. Side-effect-free: it changes nothing in the learner. Transient
        /// parts are left out - the gate, the at most <see cref="ImpactGuardParameters.RetroFrames"/> frames
        /// still in the retro delay line, and a crossing event still inside its hold window (whose teaching
        /// may yet be rolled back, so the Max exported is the one from before that event). Like every
        /// member, call it on the data thread only (the class is not thread-safe).
        /// </summary>
        public SlipAngleLearnerState Export()
        {
            NoteMaturityFloor();
            CorroboratedLevelState max = _teaching ? _maxBeforeEvent : _max;
            // The window and quality as shown - computed with the Max being saved, so a restart reads them back.
            CorroboratedLevelState live = _max;
            _max = max;
            SlipAngleWindow shown = Published;
            double quality = WindowQuality;
            _max = live;

            int epoch = Math.Min(_corroboratorCount, _snapshots.Count), older = _snapshots.Count - epoch;
            int clusterRun = BestRun(new double[2 * RecentCrossingCapacity], out double clusterLow, out double clusterHigh);
            double bestMean = 0.0, bestSpread = 0.0;
            int bestCount = _bestHistory.Count;
            if (bestCount > 0)
            {
                for (int i = 0; i < bestCount; i++) bestMean += _bestHistory.Get(i);
                bestMean /= bestCount;
                double v = 0.0;
                for (int i = 0; i < bestCount; i++) { double d = _bestHistory.Get(i) - bestMean; v += d * d; }
                bestSpread = Math.Sqrt(v / bestCount);
            }

            if (_loadedBestActive)
            {
                bestCount = _loadedBestCount;
                bestMean = _loadedBestMean;
                bestSpread = _loadedBestSpread;
            }

            return new SlipAngleLearnerState
            {
                Curve = _loadedCurve ?? _bins.ExportSummary(),
                LearnedMin = shown.Min,
                LearnedBest = shown.Best,
                LearnedMax = shown.Max,
                WindowQuality = quality,
                SnapshotCount = _snapshots.Count,
                OlderSnapshotLow = RangeOf(0, older, low: true),
                OlderSnapshotHigh = RangeOf(0, older, low: false),
                EpochSnapshotLow = RangeOf(older, epoch, low: true),
                EpochSnapshotHigh = RangeOf(older, epoch, low: false),
                ClusterRun = clusterRun,
                ClusterLow = clusterLow,
                ClusterHigh = clusterHigh,
                BestCount = bestCount,
                BestMean = bestMean,
                BestSpread = bestSpread,
                Max = max,
                CrossingCount = _crossingCount,
                CorroboratingSnapshots = _corroboratorCount,
                PeakConfirmed = _confirmed,
                Observations = _observations,
                Corners = _corners,
                InExcursion = _inExcursion,
                ObservationWaitSeconds = _observationWait,
                PriorScale = _priorScale,
                BestSampleWaitSeconds = _bestSampleWait,
                PriorMin = _prior.Min,
                PriorBest = _prior.Best,
                PriorMax = _prior.Max,
                MaxMaturityFloor = _maxMaturityFloor,
            }.WithEarly(_early);
        }

        /// <summary>The lowest or highest of <paramref name="count"/> snapshots from ring position <paramref name="from"/>;
        /// 0 for none.</summary>
        private double RangeOf(int from, int count, bool low)
        {
            if (count <= 0) return 0.0;
            double r = _snapshots.Get(from);
            for (int i = from + 1; i < from + count; i++) r = low ? Math.Min(r, _snapshots.Get(i)) : Math.Max(r, _snapshots.Get(i));
            return r;
        }

        /// <summary>Adds <paramref name="count"/> snapshots evenly from <paramref name="lo"/> to <paramref name="hi"/>
        /// (just <paramref name="lo"/> for one); invalid values are skipped.</summary>
        private void AddSpread(int count, double lo, double hi)
        {
            for (int i = 0; i < count; i++)
            {
                double s = count == 1 ? lo : lo + (hi - lo) * i / (count - 1);
                if (ClampMath.IsFinite(s) && s > 0.0 && s <= MaxCeilingDeg) _snapshots.Add(s);
            }
        }

        /// <summary>Rebuilds <paramref name="count"/> Best samples with exactly the saved mean and (population) standard
        /// deviation, so the stability term reads back the same: values alternate around the mean (one at the mean
        /// itself for an odd count).</summary>
        private void RebuildBestHistory(int count, double mean, double spread)
        {
            count = Math.Min(count, BestHistoryCapacity);
            if (count <= 0 || !ClampMath.IsFinite(mean) || !(mean > 0.0) || mean > MaxCeilingDeg) return;
            double sd = ClampMath.IsFinite(spread) && spread > 0.0 ? spread : 0.0;
            int paired = count % 2 == 0 ? count : count - 1;
            double d = paired > 0 ? sd * Math.Sqrt((double)count / paired) : 0.0;
            if (count % 2 == 1) _bestHistory.Add(mean);
            for (int i = 0; i < paired; i++) _bestHistory.Add(Math.Max(1e-6, i % 2 == 0 ? mean + d : mean - d));
        }

        /// <summary>
        /// Restores <see cref="Export"/>'s state exactly. Null resets to fresh. Invalid parts are
        /// sanitised: counters clamped to [0, <see cref="CounterSaturationCap"/>], non-finite or
        /// out-of-range snapshots / Best samples dropped (the newest kept), an invalid Max state reset to
        /// "none", hit counts and cooldown clamped, waits clamped to [0, interval], the prior scale to
        /// [1, ceiling], and confirmation only kept with a Max level, the level epoch clamped to the snapshots kept. The gate and the delay line always
        /// start empty.
        /// </summary>
        public void Import(SlipAngleLearnerState state)
        {
            CancelGate();
            _delayCount = 0;
            _delayOldest = 0;
            if (state != null)
            {
                // The prior PriorScale is relative to (absent in older states: keep the constructor prior).
                var prior = new SlipAngleWindow(state.PriorMin, state.PriorBest, state.PriorMax);
                if (prior.IsValid && prior.Max <= MaxCeilingDeg) _prior = prior;
            }
            // Each part: an older file's arrays when present, else the single-value summary.
            _loadedCurve = null;
            _loadedBestActive = false;
            if (state?.Bins != null) _bins.Import(state.Bins);
            else
            {
                _bins.ImportSummary(state?.Curve);
                _loadedCurve = state?.Curve;
            }
            _snapshots.Clear();
            _restoredInRing = _restoredRun = 0;
            _restoredLo = _restoredHi = 0.0;
            if (state?.CrossingSnapshots != null)
            {
                foreach (double s in state.CrossingSnapshots)
                    if (ClampMath.IsFinite(s) && s > 0.0 && s <= MaxCeilingDeg) _snapshots.Add(s);
            }
            else if (state != null)
            {
                int total = Math.Min(Math.Max(state.SnapshotCount, 0), RecentCrossingCapacity);
                int epoch = Math.Min(Math.Max(state.CorroboratingSnapshots, 0), total);
                AddSpread(total - epoch, state.OlderSnapshotLow, state.OlderSnapshotHigh);
                AddSpread(epoch, state.EpochSnapshotLow, state.EpochSnapshotHigh);
                _restoredInRing = _snapshots.Count;
                bool cluster = ClampMath.IsFinite(state.ClusterLow) && ClampMath.IsFinite(state.ClusterHigh)
                               && state.ClusterLow > 0.0 && state.ClusterLow <= state.ClusterHigh && state.ClusterHigh <= MaxCeilingDeg;
                _restoredRun = cluster ? Math.Min(Math.Max(state.ClusterRun, 0), _restoredInRing) : 0;
                _restoredLo = cluster ? state.ClusterLow : 0.0;
                _restoredHi = cluster ? state.ClusterHigh : 0.0;
            }
            _bestHistory.Clear();
            if (state?.BestHistory != null)
            {
                foreach (double b in state.BestHistory)
                    if (ClampMath.IsFinite(b) && b > 0.0 && b <= MaxCeilingDeg) _bestHistory.Add(b);
            }
            else if (state != null)
            {
                RebuildBestHistory(state.BestCount, state.BestMean, state.BestSpread);
                _loadedBestActive = _bestHistory.Count > 0;
                _loadedBestCount = _bestHistory.Count;
                _loadedBestMean = state.BestMean;
                _loadedBestSpread = ClampMath.IsFinite(state.BestSpread) && state.BestSpread > 0.0 ? state.BestSpread : 0.0;
            }

            _max = SanitizeMax(state?.Max ?? default(CorroboratedLevelState));
            _crossingCount = ClampCount(state?.CrossingCount ?? 0);
            int corroborating = state?.CorroboratingSnapshots ?? 0;
            _corroboratorCount = corroborating < 0 ? 0 : Math.Min(corroborating, _snapshots.Count);
            _confirmed = (state?.PeakConfirmed ?? false) && _max.Level > 0.0;
            _observations = Math.Min(Math.Max(state?.Observations ?? 0L, 0L), CounterSaturationCap);
            _corners = ClampCount(state?.Corners ?? 0);
            _inExcursion = state?.InExcursion ?? false;
            _observationWait = ClampWait(state?.ObservationWaitSeconds ?? 0.0, ObservationIntervalSeconds);
            _bestSampleWait = ClampWait(state?.BestSampleWaitSeconds ?? 0.0, BestSampleIntervalSeconds);
            double scale = state?.PriorScale ?? 1.0;
            _priorScale = ClampMath.IsFinite(scale) ? ClampMath.Clamp(scale, 1.0, Math.Max(1.0, _maxCapDeg / _prior.Max)) : 1.0;
            _early.Import(state);
            // The indicator's high-water mark (owner, 2026-11-22): restored, at most 0.99 unless the peak is confirmed.
            double floor = state?.MaxMaturityFloor ?? 0.0;
            _maxMaturityFloor = ClampMath.IsFinite(floor) ? Math.Min(_confirmed ? 1.0 : 0.99, ClampMath.To01(floor)) : 0.0;

            // What was shown at the save, read back exactly (a single-value file only; an older file's arrays
            // already restore it).
            _pinWindowActive = false;
            _pinQuality = double.NaN;
            if (state != null && state.Bins == null && state.LearnedMax > 0.0)
            {
                var shown = new SlipAngleWindow(state.LearnedMin, state.LearnedBest, state.LearnedMax);
                if (_confirmed && shown.IsValid && shown.Max <= _maxCapDeg + 1e-9)
                {
                    _pinWindow = shown;
                    _pinMaxLevel = _max.Level;
                    _pinWindowActive = true;
                }
                if (ClampMath.IsFinite(state.WindowQuality)) _pinQuality = ClampMath.To01(state.WindowQuality);
            }
        }

        private static double ClampWait(double wait, double max) =>
            ClampMath.IsFinite(wait) ? ClampMath.Clamp(wait, 0.0, max) : 0.0;

        private static int ClampCount(int n) => n < 0 ? 0 : n > CounterSaturationCap ? CounterSaturationCap : n;

        private CorroboratedLevelState SanitizeMax(CorroboratedLevelState m)
        {
            bool finite = ClampMath.IsFinite(m.Level) && ClampMath.IsFinite(m.RaiseCandidate)
                && ClampMath.IsFinite(m.LowerCandidate) && ClampMath.IsFinite(m.LastRaw)
                && ClampMath.IsFinite(m.QuietStreak);
            if (!finite || m.Level < 0.0 || m.Level > MaxCeilingDeg) return default(CorroboratedLevelState);
            int cap = _maxParams.HitSaturationCap;
            m.RaiseHits = m.RaiseHits < 0 ? 0 : Math.Min(m.RaiseHits, cap);
            m.LowerHits = m.LowerHits < 0 ? 0 : Math.Min(m.LowerHits, cap);
            m.Cooldown = Math.Min(Math.Max(m.Cooldown, 0), Math.Max(_maxParams.RaiseCooldown, _maxParams.LowerCooldown));
            if (m.QuietStreak < 0.0) m.QuietStreak = 0.0;
            return m;
        }

        /// <summary>A fixed-capacity ring of doubles, oldest evicted first.</summary>
        private sealed class Ring
        {
            private readonly double[] _items;
            private int _next;

            public Ring(int capacity) { _items = new double[capacity]; }

            public int Count { get; private set; }

            /// <summary>Item <paramref name="i"/>, oldest first.</summary>
            public double Get(int i) => _items[(_next - Count + i + 2 * _items.Length) % _items.Length];

            public void Add(double value)
            {
                _items[_next] = value;
                _next = (_next + 1) % _items.Length;
                if (Count < _items.Length) Count++;
            }

            public void Clear()
            {
                Array.Clear(_items, 0, _items.Length);
                _next = 0;
                Count = 0;
            }

            public double[] ToArray()
            {
                var a = new double[Count];
                for (int i = 0; i < Count; i++) a[i] = Get(i);
                return a;
            }
        }
    }
}
