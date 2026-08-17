using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback
{
    /// <summary>
    /// The SimHub-FREE half of <see cref="PropertyPublisher"/>: every backing field, every
    /// <c>Update*</c> setter, every <c>*Snapshot</c> accessor, and <see cref="SnapshotAllValuesForCsv"/>
    /// itself. Split out (docs\regression-fix-report.md, CSV-header-only regression) specifically so
    /// this half - which holds the ONE place a header/row column-count mismatch can be introduced -
    /// can be link-compiled into <c>QAdvanceFeedback.Tests</c> (net8.0) and exercised against the REAL
    /// production class, the same way <c>AllPublishedProperties</c>/<c>CsvExportWriter</c> already are.
    /// The other half (<c>PropertyPublisher.cs</c>) keeps only <c>Register</c>/<c>AttachTier</c>/
    /// <c>AttachTierNullable</c> - the SimHub-<c>IPlugin</c>/<c>AttachDelegate</c>-dependent wiring that
    /// cannot compile without <c>SimHub.Plugins.dll</c> (net48-only; breaks net8.0 test discovery for
    /// the whole assembly if referenced - see <c>PropertyPublisherStructureTests</c>'s own remarks).
    /// Being one `partial class` across both files, every field/method here is exactly as reachable
    /// from <c>Register</c> as before this split - no behavioural change, a pure compile-unit move plus
    /// the one line-order/line-count fix this task made.
    /// </summary>
    public sealed partial class PropertyPublisher
    {
        private const int TargetCount = 9;

        // Raw (Layer 3), Normalized (Layer 4), Projected (Layer 5) - indexed exactly as
        // PublishedPropertyNames.Targets: FrontLeft, FrontRight, RearLeft, RearRight, Front, Rear,
        // Left, Right, All.
        private readonly double[] _lockRaw = new double[TargetCount];
        private readonly double[] _slipRaw = new double[TargetCount];

        // ABSENT-VS-ZERO (telemetry-integrity pass, item 1): whether each of the 9 Raw targets above
        // actually had a real reading this frame - see LegacyWheelLockSlipResult.LockHasValue/
        // SlipHasValue's own remarks. Defaults to all-true (every pre-existing scenario, before this
        // engine ever computes a real frame, publishes precisely what it always published: 0.0 - a
        // harmless startup default, not a claim).
        private readonly bool[] _lockRawHasValue = { true, true, true, true, true, true, true, true, true };
        private readonly bool[] _slipRawHasValue = { true, true, true, true, true, true, true, true, true };

        private readonly double[] _lockNormalized = new double[TargetCount];
        private readonly double[] _slipNormalized = new double[TargetCount];
        private readonly double[] _lockProjected = new double[TargetCount];
        private readonly double[] _slipProjected = new double[TargetCount];

        // G-force - indexed exactly as GForcePublishedNames.AllNames()'s own order (Bottom x4, then
        // Back x4). Nullable: null must publish as a real null (no G data this frame), never a 0.
        private readonly double?[] _gforce = new double?[8];

        // Diagnostics - only ever read back when EnableDiagnostics is on (see Register), but kept
        // updated unconditionally so toggling the setting and restarting always shows current state.
        // Diag.GameId/Diag.CarId (docs\shakeit-silence-diagnosis-report.md) - the RESOLVED (gameId,
        // carId) this frame's learners/scale calibration are actually keyed under. Added because no
        // capture previously carried this at all - the car-id-fragmentation question (the owner's own
        // Parameters.json shows the same physical F1 25 car appearing as BOTH "Sauber" and "F1 Generic")
        // could only be investigated indirectly, and a genuinely UNSTABLE (changing frame-to-frame, or
        // empty) car id for a from-scratch custom-built car could never be told apart from a merely
        // DIFFERENT-but-stable id without this - see that report's own "what capture would settle it".
        private string _gameId = string.Empty;
        private string _carId = string.Empty;

        private string _direction = "Unknown";
        private string _motionLevel = "Unavailable";
        private double _motionMagnitudeG;
        private double _lockLearnedPeakG;
        private double _lockLearnerConfidence;
        private double _slipLearnedPeakG;
        private double _slipLearnerConfidence;
        private double _gforceLearnedAccelMaxG;
        private double _gforceLearnedDecelMaxG;

        // Diag.SelectedBranch.* (docs\branch-dispatch-and-source-keyed-learning-report.md) - which of
        // SimHub's own nine decompiled WheelSlipEffect.GetEffectValue branches Layer 3 actually ran this
        // frame, for each channel - see LegacyWheelLockSlipResult.SelectedLockBranch/SelectedSlipBranch.
        private string _selectedLockBranch = WheelSlipBranchNames.NoBranchMatched;
        private string _selectedSlipBranch = WheelSlipBranchNames.NoBranchMatched;

        // Diag.Lock/Slip.SourceScaleCeiling* (docs\branch-dispatch-and-source-keyed-learning-report.md)
        // - the currently-configured source's own learned near-the-limit ceiling (native units), null
        // until calibrated, plus which tier (physically-anchored vs percentile-fallback) produced it.
        private double? _lockScaleCeiling;
        private bool _lockScaleCeilingIsPrimaryTier;
        private double? _slipScaleCeiling;
        private bool _slipScaleCeilingIsPrimaryTier;

        // Diag.Lock/Slip.SourceFallbackActive (docs\shakeit-silence-diagnosis-report.md) - whether the
        // MOST RECENT frame substituted Layer 3's own Raw for the configured source because the
        // configured source read near-zero while Raw independently read a genuine, well-above-floor
        // value - see NormalizedWheelLockSlipEngine's own remarks. Makes the degraded state VISIBLE so a
        // driver/rig can tell "the configured source went quiet, we substituted Raw" from "genuinely no
        // lockup".
        private bool _lockSourceFallbackActive;
        private bool _slipSourceFallbackActive;

        // Surface-keyed learning (docs\branch-dispatch-and-source-keyed-learning-report.md).
        private bool? _wheelOnLooseSurfaceFrontLeft, _wheelOnLooseSurfaceFrontRight, _wheelOnLooseSurfaceRearLeft, _wheelOnLooseSurfaceRearRight;
        private bool _surfaceEverReportedLoose;
        private double _lockLooseFraction;
        private double _slipLooseFraction;

        // Diag.Source.* (docs\raw-gap-and-pad-balance-report.md) - the RESOLVED SOURCE value Layer 4
        // actually consumed this frame, per wheel plus the same Front/Rear/Left/Right/All aggregates
        // every other tier publishes - indexed exactly like _lockRaw/_slipRaw (PublishedPropertyNames.
        // Targets order). Added so a future "does our Raw match the configured source" investigation
        // never again needs to invert the Normalized transform to recover this value.
        private readonly double[] _sourceLock = new double[TargetCount];
        private readonly double[] _sourceSlip = new double[TargetCount];

        // WheelLock/WheelSlip.ProjectedWithoutPulse.* (docs\raw-gap-and-pad-balance-report.md, the
        // pulse-into-shake fix) - internal/troubleshooting only, gated the same as every other
        // diagnostic name (see Register). Indexed exactly like _lockProjected/_slipProjected.
        private readonly double[] _lockProjectedWithoutPulse = new double[TargetCount];
        private readonly double[] _slipProjectedWithoutPulse = new double[TargetCount];

        // Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) - raw
        // per-wheel/car-level telemetry inputs to SimHub's own three candidate WheelRPS/WheelSpeed
        // Lock branches, plus the FeedbackCapabilities flags that pick which branch actually fires for
        // the running title - diagnostics-only, never consumed by this plugin's own algorithm. Every
        // field here is nullable and kept nullable end-to-end (through AttachDelegate and the CSV
        // snapshot) - null means "not supplied/not reachable this frame", never coerced to 0 (see
        // RawWheelTelemetrySnapshot's own remarks for why that distinction matters more than anything
        // else in this group).
        private double? _telemetryGroundSpeedKmh;
        private double? _telemetrySpeedKmh;
        private double? _telemetryGroundSpeedMps;
        private double? _telemetryRpm;
        private string _telemetryGear;
        private double? _telemetryBrakePercent;
        private double? _telemetryThrottlePercent;
        private double? _telemetryClutchPercent;
        private double? _telemetryLateralLocalVelocity;
        private double? _telemetryLongitudinalG;
        private double? _telemetryLateralG;

        private double? _wheelRpsFrontLeft, _wheelRpsFrontRight, _wheelRpsRearLeft, _wheelRpsRearRight;
        private double? _wheelSpeedFrontLeft, _wheelSpeedFrontRight, _wheelSpeedRearLeft, _wheelSpeedRearRight;
        private double? _wheelSlipRatioFrontLeft, _wheelSlipRatioFrontRight, _wheelSlipRatioRearLeft, _wheelSlipRatioRearRight;

        private bool? _capWheelsSlip;
        private bool? _capWheelsRPS;
        private bool? _capWheelsSpeed;
        private bool? _capWheelsSlipDirectMode;
        private bool? _capWheelSlipUseSimpleBraking;
        private bool? _capDetectLockFromWheelsSpeed;
        private bool? _capDetectLockFromWheelsRPSAndDummyRadius;
        private bool? _capSpeed;
        private bool? _capRpm;
        private string _capGameFamily;
        private double? _capWheelSlipCalibrationProviderSlipScale;

        public void UpdateRaw(LegacyWheelLockSlipResult result)
        {
            Fill(_lockRaw, _slipRaw, result);
            Array.Copy(result.LockHasValue ?? _lockRawHasValue, _lockRawHasValue, TargetCount);
            Array.Copy(result.SlipHasValue ?? _slipRawHasValue, _slipRawHasValue, TargetCount);
            _selectedLockBranch = result.SelectedLockBranch ?? WheelSlipBranchNames.NoBranchMatched;
            _selectedSlipBranch = result.SelectedSlipBranch ?? WheelSlipBranchNames.NoBranchMatched;
        }

        /// <summary>Diag.Lock/Slip.SourceScaleCeiling* (docs\branch-dispatch-and-source-keyed-learning-report.md)
        /// - read straight off <c>NormalizedWheelLockSlipEngine</c>'s own last-computed state (its
        /// <c>LockScaleCeiling</c>/<c>SlipScaleCeiling</c> properties), the same "expose last-computed
        /// state via a property" pattern <c>CurrentDirection</c> already uses on that engine.</summary>
        public void UpdateSourceScaleCalibration(double? lockCeiling, bool lockIsPrimaryTier, double? slipCeiling, bool slipIsPrimaryTier)
        {
            _lockScaleCeiling = lockCeiling;
            _lockScaleCeilingIsPrimaryTier = lockIsPrimaryTier;
            _slipScaleCeiling = slipCeiling;
            _slipScaleCeilingIsPrimaryTier = slipIsPrimaryTier;
        }

        /// <summary>Diag.Lock/Slip.SourceFallbackActive (docs\shakeit-silence-diagnosis-report.md) -
        /// read straight off <c>NormalizedWheelLockSlipEngine</c>'s own last-computed state, same
        /// pattern as <see cref="UpdateSourceScaleCalibration"/>.</summary>
        public void UpdateSourceFallback(bool lockFallbackActive, bool slipFallbackActive)
        {
            _lockSourceFallbackActive = lockFallbackActive;
            _slipSourceFallbackActive = slipFallbackActive;
        }

        /// <summary>Surface-keyed learning (docs\branch-dispatch-and-source-keyed-learning-report.md) -
        /// read straight off <c>NormalizedWheelLockSlipEngine</c>'s own last-computed state, same
        /// pattern as <see cref="UpdateSourceScaleCalibration"/>.</summary>
        public void UpdateSurfaceLearning(bool surfaceEverReportedLoose, double lockLooseFraction, double slipLooseFraction)
        {
            _surfaceEverReportedLoose = surfaceEverReportedLoose;
            _lockLooseFraction = lockLooseFraction;
            _slipLooseFraction = slipLooseFraction;
        }

        public void UpdateNormalized(NormalizedWheelLockSlipResult result)
        {
            _lockNormalized[0] = result.LockWheels.FrontLeft;
            _lockNormalized[1] = result.LockWheels.FrontRight;
            _lockNormalized[2] = result.LockWheels.RearLeft;
            _lockNormalized[3] = result.LockWheels.RearRight;
            _lockNormalized[4] = result.LockFront;
            _lockNormalized[5] = result.LockRear;
            _lockNormalized[6] = result.LockLeft;
            _lockNormalized[7] = result.LockRight;
            _lockNormalized[8] = result.LockAll;

            _slipNormalized[0] = result.SlipWheels.FrontLeft;
            _slipNormalized[1] = result.SlipWheels.FrontRight;
            _slipNormalized[2] = result.SlipWheels.RearLeft;
            _slipNormalized[3] = result.SlipWheels.RearRight;
            _slipNormalized[4] = result.SlipFront;
            _slipNormalized[5] = result.SlipRear;
            _slipNormalized[6] = result.SlipLeft;
            _slipNormalized[7] = result.SlipRight;
            _slipNormalized[8] = result.SlipAll;
        }

        public void UpdateProjected(ProjectedWheelLockSlipResult result)
        {
            _lockProjected[0] = result.LockWheels.FrontLeft;
            _lockProjected[1] = result.LockWheels.FrontRight;
            _lockProjected[2] = result.LockWheels.RearLeft;
            _lockProjected[3] = result.LockWheels.RearRight;
            _lockProjected[4] = result.LockFront;
            _lockProjected[5] = result.LockRear;
            _lockProjected[6] = result.LockLeft;
            _lockProjected[7] = result.LockRight;
            _lockProjected[8] = result.LockAll;

            _slipProjected[0] = result.SlipWheels.FrontLeft;
            _slipProjected[1] = result.SlipWheels.FrontRight;
            _slipProjected[2] = result.SlipWheels.RearLeft;
            _slipProjected[3] = result.SlipWheels.RearRight;
            _slipProjected[4] = result.SlipFront;
            _slipProjected[5] = result.SlipRear;
            _slipProjected[6] = result.SlipLeft;
            _slipProjected[7] = result.SlipRight;
            _slipProjected[8] = result.SlipAll;

            _lockProjectedWithoutPulse[0] = result.LockWheelsWithoutPulse.FrontLeft;
            _lockProjectedWithoutPulse[1] = result.LockWheelsWithoutPulse.FrontRight;
            _lockProjectedWithoutPulse[2] = result.LockWheelsWithoutPulse.RearLeft;
            _lockProjectedWithoutPulse[3] = result.LockWheelsWithoutPulse.RearRight;
            _lockProjectedWithoutPulse[4] = result.LockFrontWithoutPulse;
            _lockProjectedWithoutPulse[5] = result.LockRearWithoutPulse;
            _lockProjectedWithoutPulse[6] = result.LockLeftWithoutPulse;
            _lockProjectedWithoutPulse[7] = result.LockRightWithoutPulse;
            _lockProjectedWithoutPulse[8] = result.LockAllWithoutPulse;

            _slipProjectedWithoutPulse[0] = result.SlipWheelsWithoutPulse.FrontLeft;
            _slipProjectedWithoutPulse[1] = result.SlipWheelsWithoutPulse.FrontRight;
            _slipProjectedWithoutPulse[2] = result.SlipWheelsWithoutPulse.RearLeft;
            _slipProjectedWithoutPulse[3] = result.SlipWheelsWithoutPulse.RearRight;
            _slipProjectedWithoutPulse[4] = result.SlipFrontWithoutPulse;
            _slipProjectedWithoutPulse[5] = result.SlipRearWithoutPulse;
            _slipProjectedWithoutPulse[6] = result.SlipLeftWithoutPulse;
            _slipProjectedWithoutPulse[7] = result.SlipRightWithoutPulse;
            _slipProjectedWithoutPulse[8] = result.SlipAllWithoutPulse;
        }

        /// <summary>
        /// Diag.Source.* (docs\raw-gap-and-pad-balance-report.md): records exactly the per-wheel
        /// Corners Layer 4 (<c>NormalizedWheelLockSlipEngine.Compute</c>) was actually called with THIS
        /// frame - <c>lockSources</c>/<c>slipSources</c> in <c>QAdvanceFeedback.cs</c>'s own
        /// <c>DataUpdate</c>, i.e. our own Layer 3 Raw by default, or whatever the driver's Source
        /// fields resolved to instead (a ShakeIt export, a Manual property/expression, or the Layer 3
        /// fallback on any resolution failure - see <c>WheelSourceResolver</c>). Front/Rear/Left/Right/
        /// All are aggregated with the SAME owner-configured scheme every other tier uses for this
        /// channel (docs\aggregation-report.md), so this reads exactly like the Raw/Normalized/Projected
        /// tiers a driver already knows how to interpret.
        /// </summary>
        public void UpdateSource(Corners lockSources, Corners slipSources, AggregationWeights lockWeights, AggregationWeights slipWeights)
        {
            FillSource(_sourceLock, lockSources, lockWeights);
            FillSource(_sourceSlip, slipSources, slipWeights);
        }

        private static void FillSource(double[] values, Corners wheels, AggregationWeights weights)
        {
            values[0] = wheels.FrontLeft;
            values[1] = wheels.FrontRight;
            values[2] = wheels.RearLeft;
            values[3] = wheels.RearRight;
            WheelAggregate aggregate = Aggregator.Compute(wheels, weights);
            values[4] = aggregate.Front;
            values[5] = aggregate.Rear;
            values[6] = aggregate.Left;
            values[7] = aggregate.Right;
            values[8] = aggregate.All;
        }

        public void UpdateGForce(GForceOutput output)
        {
            _gforce[0] = output.BottomFrontLeft;
            _gforce[1] = output.BottomFrontRight;
            _gforce[2] = output.BottomRearLeft;
            _gforce[3] = output.BottomRearRight;
            _gforce[4] = output.BackLowLeft;
            _gforce[5] = output.BackLowRight;
            _gforce[6] = output.BackTopLeft;
            _gforce[7] = output.BackTopRight;
        }

        /// <summary>Diag.GameId/Diag.CarId (docs\shakeit-silence-diagnosis-report.md) - see those
        /// fields' own remarks.</summary>
        public void UpdateIdentity(string gameId, string carId)
        {
            _gameId = gameId ?? string.Empty;
            _carId = carId ?? string.Empty;
        }

        public void UpdateDiagnostics(
            LongitudinalMotionState direction, AchievedMotion.SignalLevel motionLevel, double motionMagnitudeG,
            double lockLearnedPeakG, double lockLearnerConfidence,
            double slipLearnedPeakG, double slipLearnerConfidence,
            double gforceLearnedAccelMaxG, double gforceLearnedDecelMaxG)
        {
            _direction = direction.ToString();
            _motionLevel = motionLevel.ToString();
            _motionMagnitudeG = motionMagnitudeG;
            _lockLearnedPeakG = lockLearnedPeakG;
            _lockLearnerConfidence = lockLearnerConfidence;
            _slipLearnedPeakG = slipLearnedPeakG;
            _slipLearnerConfidence = slipLearnerConfidence;
            _gforceLearnedAccelMaxG = gforceLearnedAccelMaxG;
            _gforceLearnedDecelMaxG = gforceLearnedDecelMaxG;
        }

        /// <summary>
        /// Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md). Two separate
        /// inputs, deliberately not merged into one type: <paramref name="carFrame"/> is this frame's
        /// ALREADY-COMPUTED <see cref="ITelemetryFrame"/> (<c>QAdvanceFeedback.cs</c>'s own
        /// <c>sample.New</c>) - reused as-is, not recomputed, since Layer 3 already reads it every
        /// frame for the SAME ground speed/RPM/gear/pedal/G values this just exposes;
        /// <paramref name="wheelTelemetry"/> is the new raw per-wheel + FeedbackCapabilities capture
        /// this task adds (see <see cref="ITelemetryAdapter.CaptureRawTelemetry"/>). Both may be null
        /// (a null-guard, not an expected case) - every field degrades to "absent" rather than 0/false.
        /// </summary>
        public void UpdateRawTelemetry(ITelemetryFrame carFrame, RawWheelTelemetrySnapshot wheelTelemetry)
        {
            _telemetryGroundSpeedKmh = carFrame?.GroundSpeedKmh;
            _telemetrySpeedKmh = carFrame?.SpeedKmh;
            _telemetryGroundSpeedMps = carFrame?.GroundSpeedMps;
            _telemetryRpm = carFrame?.Rpm;
            _telemetryGear = carFrame?.Gear;
            _telemetryBrakePercent = carFrame?.BrakePercent;
            _telemetryThrottlePercent = carFrame?.ThrottlePercent;
            _telemetryClutchPercent = carFrame?.ClutchPercent;
            _telemetryLateralLocalVelocity = carFrame?.LateralLocalVelocity;
            _telemetryLongitudinalG = carFrame?.LongitudinalG;
            _telemetryLateralG = carFrame?.LateralG;
            _wheelOnLooseSurfaceFrontLeft = carFrame?.WheelOnLooseSurfaceFrontLeft;
            _wheelOnLooseSurfaceFrontRight = carFrame?.WheelOnLooseSurfaceFrontRight;
            _wheelOnLooseSurfaceRearLeft = carFrame?.WheelOnLooseSurfaceRearLeft;
            _wheelOnLooseSurfaceRearRight = carFrame?.WheelOnLooseSurfaceRearRight;

            _wheelRpsFrontLeft = wheelTelemetry?.WheelRpsFrontLeft;
            _wheelRpsFrontRight = wheelTelemetry?.WheelRpsFrontRight;
            _wheelRpsRearLeft = wheelTelemetry?.WheelRpsRearLeft;
            _wheelRpsRearRight = wheelTelemetry?.WheelRpsRearRight;

            _wheelSpeedFrontLeft = wheelTelemetry?.WheelSpeedFrontLeft;
            _wheelSpeedFrontRight = wheelTelemetry?.WheelSpeedFrontRight;
            _wheelSpeedRearLeft = wheelTelemetry?.WheelSpeedRearLeft;
            _wheelSpeedRearRight = wheelTelemetry?.WheelSpeedRearRight;

            _wheelSlipRatioFrontLeft = wheelTelemetry?.WheelSlipRatioFrontLeft;
            _wheelSlipRatioFrontRight = wheelTelemetry?.WheelSlipRatioFrontRight;
            _wheelSlipRatioRearLeft = wheelTelemetry?.WheelSlipRatioRearLeft;
            _wheelSlipRatioRearRight = wheelTelemetry?.WheelSlipRatioRearRight;

            _capWheelsSlip = wheelTelemetry?.CapabilityWheelsSlip;
            _capWheelsRPS = wheelTelemetry?.CapabilityWheelsRPS;
            _capWheelsSpeed = wheelTelemetry?.CapabilityWheelsSpeed;
            _capWheelsSlipDirectMode = wheelTelemetry?.CapabilityWheelsSlipDirectMode;
            _capWheelSlipUseSimpleBraking = wheelTelemetry?.CapabilityWheelSlipUseSimpleBraking;
            _capDetectLockFromWheelsSpeed = wheelTelemetry?.CapabilityDetectLockFromWheelsSpeed;
            _capDetectLockFromWheelsRPSAndDummyRadius = wheelTelemetry?.CapabilityDetectLockFromWheelsRPSAndDummyRadius;
            _capSpeed = wheelTelemetry?.CapabilitySpeed;
            _capRpm = wheelTelemetry?.CapabilityRpm;
            _capGameFamily = wheelTelemetry?.CapabilityGameFamily;
            _capWheelSlipCalibrationProviderSlipScale = wheelTelemetry?.CapabilityWheelSlipCalibrationProviderSlipScale;
        }

        /// <summary>
        /// Every value this plugin currently holds, in EXACTLY the same order as
        /// <c>AllPublishedProperties.ProductNames()</c> followed by
        /// <c>AllPublishedProperties.DiagnosticNames()</c> - i.e. always ALL of them, regardless of
        /// whether diagnostics are actually published to SimHub, since "Export CSV" writes every
        /// property including internals whenever it is on (see <c>GeneralSettings.ExportCsv</c>'s
        /// remarks) independently of <c>GeneralSettings.EnableDiagnostics</c>.
        /// <para/>
        /// REGRESSION FIXED HERE (docs\regression-fix-report.md): this method previously OMITTED
        /// <c>_gameId</c>/<c>_carId</c> (Diag.GameId/Diag.CarId) and <c>_lockSourceFallbackActive</c>/
        /// <c>_slipSourceFallbackActive</c> (Diag.Lock/Slip.SourceFallbackActive) entirely - four
        /// values short of the header <see cref="AllPublishedProperties.DiagnosticNames"/> produces.
        /// <see cref="CsvExportWriter.WriteRow"/> silently no-ops on any column-count mismatch (by
        /// design - a caller bug should never crash a live session), so with diagnostics AND CSV export
        /// both enabled, EVERY row after the header was dropped: a header-only file, every time. Fixed
        /// by adding all four values back, in the exact order <c>DiagnosticNames</c> yields them.
        /// <c>CsvSnapshotOrderTests</c> now asserts the raw LENGTH match directly against
        /// <c>AllPublishedProperties.AllNames(true).Count()</c> so this can never silently drift again,
        /// and a companion test writes a real row end-to-end through <see cref="CsvExportWriter"/> with
        /// the full diagnostic set enabled to prove output is no longer header-only.
        /// </summary>
        public object[] SnapshotAllValuesForCsv()
        {
            var values = new System.Collections.Generic.List<object>(62 + 9);
            // Raw: empty (null), not 0, wherever this frame had nothing real to say - CsvExportWriter's
            // own FormatCell already renders a null object as an empty cell (see UpdateRawTelemetry's
            // own remarks for the identical convention already used for Diag.Telemetry.*).
            for (int i = 0; i < _lockRaw.Length; i++) values.Add(_lockRawHasValue[i] ? (object)_lockRaw[i] : null);
            for (int i = 0; i < _slipRaw.Length; i++) values.Add(_slipRawHasValue[i] ? (object)_slipRaw[i] : null);
            foreach (double v in _lockNormalized) values.Add(v);
            foreach (double v in _slipNormalized) values.Add(v);
            foreach (double v in _lockProjected) values.Add(v);
            foreach (double v in _slipProjected) values.Add(v);
            foreach (double? v in _gforce) values.Add(v);

            // ORDER BELOW MUST EXACTLY MATCH AllPublishedProperties.DiagnosticNames() - read that
            // method alongside this one before reordering either. (Diag.GameId/Diag.CarId and
            // Diag.Lock/Slip.SourceFallbackActive were the four values missing before this fix.)
            values.Add(_gameId);
            values.Add(_carId);
            values.Add(_direction);
            values.Add(_motionLevel);
            values.Add(_motionMagnitudeG);
            values.Add(_selectedLockBranch);
            values.Add(_selectedSlipBranch);
            values.Add(_lockScaleCeiling);
            values.Add(_lockScaleCeilingIsPrimaryTier);
            values.Add(_slipScaleCeiling);
            values.Add(_slipScaleCeilingIsPrimaryTier);
            values.Add(_lockSourceFallbackActive);
            values.Add(_slipSourceFallbackActive);
            values.Add(_lockLooseFraction);
            values.Add(_slipLooseFraction);
            values.Add(_lockLearnedPeakG);
            values.Add(_lockLearnerConfidence);
            values.Add(_slipLearnedPeakG);
            values.Add(_slipLearnerConfidence);
            values.Add(_gforceLearnedAccelMaxG);
            values.Add(_gforceLearnedDecelMaxG);

            foreach (double v in _sourceLock) values.Add(v);
            foreach (double v in _sourceSlip) values.Add(v);

            foreach (double v in _lockProjectedWithoutPulse) values.Add(v);
            foreach (double v in _slipProjectedWithoutPulse) values.Add(v);

            // Diag.Telemetry.*/Diag.Capabilities.* - EXACT same order as
            // AllPublishedProperties.DiagnosticNames() appends them; nullable fields added directly
            // (List<object> boxes a null double?/bool? as a true null, not 0/false - CsvExportWriter's
            // own FormatCell already renders a null object as an empty cell).
            values.Add(_telemetryGroundSpeedKmh);
            values.Add(_telemetrySpeedKmh);
            values.Add(_telemetryGroundSpeedMps);
            values.Add(_telemetryRpm);
            values.Add(_telemetryGear);
            values.Add(_telemetryBrakePercent);
            values.Add(_telemetryThrottlePercent);
            values.Add(_telemetryClutchPercent);
            values.Add(_telemetryLateralLocalVelocity);
            values.Add(_telemetryLongitudinalG);
            values.Add(_telemetryLateralG);

            values.Add(_wheelRpsFrontLeft);
            values.Add(_wheelRpsFrontRight);
            values.Add(_wheelRpsRearLeft);
            values.Add(_wheelRpsRearRight);

            values.Add(_wheelSpeedFrontLeft);
            values.Add(_wheelSpeedFrontRight);
            values.Add(_wheelSpeedRearLeft);
            values.Add(_wheelSpeedRearRight);

            values.Add(_wheelSlipRatioFrontLeft);
            values.Add(_wheelSlipRatioFrontRight);
            values.Add(_wheelSlipRatioRearLeft);
            values.Add(_wheelSlipRatioRearRight);

            values.Add(_wheelOnLooseSurfaceFrontLeft);
            values.Add(_wheelOnLooseSurfaceFrontRight);
            values.Add(_wheelOnLooseSurfaceRearLeft);
            values.Add(_wheelOnLooseSurfaceRearRight);
            values.Add(_surfaceEverReportedLoose);

            values.Add(_capWheelsSlip);
            values.Add(_capWheelsRPS);
            values.Add(_capWheelsSpeed);
            values.Add(_capWheelsSlipDirectMode);
            values.Add(_capWheelSlipUseSimpleBraking);
            values.Add(_capDetectLockFromWheelsSpeed);
            values.Add(_capDetectLockFromWheelsRPSAndDummyRadius);
            values.Add(_capSpeed);
            values.Add(_capRpm);
            values.Add(_capGameFamily);
            values.Add(_capWheelSlipCalibrationProviderSlipScale);

            return values.ToArray();
        }

        // Snapshot accessors for CSV export (Core/GForce/etc. types stay out of the CSV writer itself).
        public double[] LockRawSnapshot => (double[])_lockRaw.Clone();
        public double[] SlipRawSnapshot => (double[])_slipRaw.Clone();
        public double[] LockNormalizedSnapshot => (double[])_lockNormalized.Clone();
        public double[] SlipNormalizedSnapshot => (double[])_slipNormalized.Clone();
        public double[] LockProjectedSnapshot => (double[])_lockProjected.Clone();
        public double[] SlipProjectedSnapshot => (double[])_slipProjected.Clone();
        public double?[] GForceSnapshot => (double?[])_gforce.Clone();
        public string DirectionSnapshot => _direction;
        public string MotionLevelSnapshot => _motionLevel;
        public double MotionMagnitudeGSnapshot => _motionMagnitudeG;
        public double LockLearnedPeakGSnapshot => _lockLearnedPeakG;
        public double LockLearnerConfidenceSnapshot => _lockLearnerConfidence;
        public double SlipLearnedPeakGSnapshot => _slipLearnedPeakG;
        public double SlipLearnerConfidenceSnapshot => _slipLearnerConfidence;
        public double GForceLearnedAccelMaxGSnapshot => _gforceLearnedAccelMaxG;
        public double GForceLearnedDecelMaxGSnapshot => _gforceLearnedDecelMaxG;
        public string SelectedLockBranchSnapshot => _selectedLockBranch;
        public string SelectedSlipBranchSnapshot => _selectedSlipBranch;
        public double? LockScaleCeilingSnapshot => _lockScaleCeiling;
        public bool LockScaleCeilingIsPrimaryTierSnapshot => _lockScaleCeilingIsPrimaryTier;
        public double? SlipScaleCeilingSnapshot => _slipScaleCeiling;
        public bool SlipScaleCeilingIsPrimaryTierSnapshot => _slipScaleCeilingIsPrimaryTier;
        public bool SurfaceEverReportedLooseSnapshot => _surfaceEverReportedLoose;
        public double LockLooseFractionSnapshot => _lockLooseFraction;
        public double SlipLooseFractionSnapshot => _slipLooseFraction;
        public double[] SourceLockSnapshot => (double[])_sourceLock.Clone();
        public double[] SourceSlipSnapshot => (double[])_sourceSlip.Clone();
        public double[] LockProjectedWithoutPulseSnapshot => (double[])_lockProjectedWithoutPulse.Clone();
        public double[] SlipProjectedWithoutPulseSnapshot => (double[])_slipProjectedWithoutPulse.Clone();

        private static void Fill(double[] lockValues, double[] slipValues, LegacyWheelLockSlipResult result)
        {
            lockValues[0] = result.LockWheels.FrontLeft;
            lockValues[1] = result.LockWheels.FrontRight;
            lockValues[2] = result.LockWheels.RearLeft;
            lockValues[3] = result.LockWheels.RearRight;
            lockValues[4] = result.LockFront;
            lockValues[5] = result.LockRear;
            lockValues[6] = result.LockLeft;
            lockValues[7] = result.LockRight;
            lockValues[8] = result.LockAll;

            slipValues[0] = result.SlipWheels.FrontLeft;
            slipValues[1] = result.SlipWheels.FrontRight;
            slipValues[2] = result.SlipWheels.RearLeft;
            slipValues[3] = result.SlipWheels.RearRight;
            slipValues[4] = result.SlipFront;
            slipValues[5] = result.SlipRear;
            slipValues[6] = result.SlipLeft;
            slipValues[7] = result.SlipRight;
            slipValues[8] = result.SlipAll;
        }
    }
}
