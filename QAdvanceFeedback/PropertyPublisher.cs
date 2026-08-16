using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Projection;
using QAdvanceFeedback.Core.Normalized;
using SimHub.Plugins;

namespace QAdvanceFeedback
{
    /// <summary>
    /// Registers and serves every property this plugin publishes: Raw/Normalized/Projected for both Wheel
    /// Lock and Wheel Slip (54 properties), the 8 G-force channels, and - only when
    /// <see cref="Settings.GeneralSettings.EnableDiagnostics"/> is on - the internal/diagnostic
    /// properties. Values are held in fields and served through <c>AttachDelegate</c>, so publishing
    /// costs nothing extra in the per-frame path; <see cref="AllPublishedProperties"/> is the single
    /// source of truth for WHICH names exist and the diagnostics gate (unit-tested there, SimHub-free)
    /// - this class is only the thin loop that dispatches each one to the matching backing field.
    /// <para/>
    /// CRITICAL (has bitten this project family twice - read before changing the signature of
    /// <see cref="Register{TPlugin}"/>): <c>IPluginExtensions.AttachDelegate&lt;T,U&gt;</c> infers
    /// <c>T</c> from the STATIC type of its receiver, not <c>GetType()</c>. This method is therefore
    /// generic in the concrete plugin type (<typeparamref name="TPlugin"/>) rather than taking a
    /// plain <see cref="IPlugin"/> parameter - a fixed <c>IPlugin</c> parameter would make every
    /// property register under "IPlugin.*" regardless of what the plugin class is named, silently
    /// defeating the entire "QAdvanceFeedback." prefix this plugin class's own name is supposed to
    /// supply (see <c>PluginManager.GetName</c>, decompiled and confirmed - <c>QAdvanceFeedback.cs</c>'s
    /// own remarks). This is mutation (b) in the report.
    /// </summary>
    public sealed class PropertyPublisher
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

        /// <summary>
        /// Attaches every property this plugin publishes this session. <paramref name="diagnosticsEnabled"/>
        /// gates the diagnostic set only (see <see cref="AllPublishedProperties.DiagnosticNames"/>) -
        /// the 62 product properties are ALWAYS attached, unconditionally. SimHub registers properties
        /// once at Init, so toggling the diagnostics setting only takes effect after a SimHub restart -
        /// the settings UI says so next to the checkbox.
        /// </summary>
        public void Register<TPlugin>(TPlugin plugin, bool diagnosticsEnabled) where TPlugin : IPlugin
        {
            // Raw (Layer 3) publishes EMPTY, not 0, for a target with no real reading this frame -
            // consistent with the G-force null rule and the CSV empty-not-zero convention (see
            // AttachTierNullable's own remarks) - the one tier close enough to per-wheel telemetry
            // absence for this distinction to be meaningful; Normalized/Projected below continue to
            // publish a plain double (their own "0" already means "channel not triggered/engaged", a
            // real, car-level answer derived from nullable-safe G/speed gates of their own, not a
            // per-wheel telemetry substitution - see docs\telemetry-integrity-report.md).
            AttachTierNullable(plugin, PublishedPropertyNames.LockPrefix, _lockRaw, _lockRawHasValue);
            AttachTierNullable(plugin, PublishedPropertyNames.SlipPrefix, _slipRaw, _slipRawHasValue);
            AttachTier(plugin, AllPublishedProperties.NormalizedLockPrefix, _lockNormalized);
            AttachTier(plugin, AllPublishedProperties.NormalizedSlipPrefix, _slipNormalized);
            AttachTier(plugin, AllPublishedProperties.ProjectedLockPrefix, _lockProjected);
            AttachTier(plugin, AllPublishedProperties.ProjectedSlipPrefix, _slipProjected);

            string[] gforceNames = new System.Collections.Generic.List<string>(GForcePublishedNames.AllNames()).ToArray();
            for (int i = 0; i < gforceNames.Length; i++)
            {
                int index = i;
                plugin.AttachDelegate(gforceNames[index], () => (object)_gforce[index]);
            }

            if (!diagnosticsEnabled) return;

            plugin.AttachDelegate("Diag.Direction", () => _direction);
            plugin.AttachDelegate("Diag.MotionLevel", () => _motionLevel);
            plugin.AttachDelegate("Diag.MotionMagnitudeG", () => _motionMagnitudeG);
            plugin.AttachDelegate("Diag.Lock.LearnedPeakG", () => _lockLearnedPeakG);
            plugin.AttachDelegate("Diag.Lock.LearnerConfidence", () => _lockLearnerConfidence);
            plugin.AttachDelegate("Diag.Slip.LearnedPeakG", () => _slipLearnedPeakG);
            plugin.AttachDelegate("Diag.Slip.LearnerConfidence", () => _slipLearnerConfidence);
            plugin.AttachDelegate("Diag.GForce.LearnedAccelMaxG", () => _gforceLearnedAccelMaxG);
            plugin.AttachDelegate("Diag.GForce.LearnedDecelMaxG", () => _gforceLearnedDecelMaxG);
            plugin.AttachDelegate("Diag.SelectedBranch.Lock", () => _selectedLockBranch);
            plugin.AttachDelegate("Diag.SelectedBranch.Slip", () => _selectedSlipBranch);
            plugin.AttachDelegate("Diag.Lock.SourceScaleCeiling", () => (object)_lockScaleCeiling);
            plugin.AttachDelegate("Diag.Lock.SourceScaleCeilingIsPrimaryTier", () => _lockScaleCeilingIsPrimaryTier);
            plugin.AttachDelegate("Diag.Slip.SourceScaleCeiling", () => (object)_slipScaleCeiling);
            plugin.AttachDelegate("Diag.Slip.SourceScaleCeilingIsPrimaryTier", () => _slipScaleCeilingIsPrimaryTier);

            plugin.AttachDelegate("Diag.Telemetry.WheelOnLooseSurface.FrontLeft", () => (object)_wheelOnLooseSurfaceFrontLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelOnLooseSurface.FrontRight", () => (object)_wheelOnLooseSurfaceFrontRight);
            plugin.AttachDelegate("Diag.Telemetry.WheelOnLooseSurface.RearLeft", () => (object)_wheelOnLooseSurfaceRearLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelOnLooseSurface.RearRight", () => (object)_wheelOnLooseSurfaceRearRight);
            plugin.AttachDelegate("Diag.Capabilities.SurfaceEverReportedLoose", () => _surfaceEverReportedLoose);
            plugin.AttachDelegate("Diag.Lock.SurfaceLooseFraction", () => _lockLooseFraction);
            plugin.AttachDelegate("Diag.Slip.SurfaceLooseFraction", () => _slipLooseFraction);

            AttachTier(plugin, AllPublishedProperties.SourceLockPrefix, _sourceLock);
            AttachTier(plugin, AllPublishedProperties.SourceSlipPrefix, _sourceSlip);

            AttachTier(plugin, AllPublishedProperties.ProjectedLockWithoutPulsePrefix, _lockProjectedWithoutPulse);
            AttachTier(plugin, AllPublishedProperties.ProjectedSlipWithoutPulsePrefix, _slipProjectedWithoutPulse);

            // Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) - see the
            // backing fields' own remarks. Every delegate below returns a nullable boxed as object
            // (same pattern already used for _gforce above) so a missing reading publishes as SimHub's
            // own "no value" rather than a fabricated 0/false.
            plugin.AttachDelegate("Diag.Telemetry.GroundSpeedKmh", () => (object)_telemetryGroundSpeedKmh);
            plugin.AttachDelegate("Diag.Telemetry.SpeedKmh", () => (object)_telemetrySpeedKmh);
            plugin.AttachDelegate("Diag.Telemetry.GroundSpeedMps", () => (object)_telemetryGroundSpeedMps);
            plugin.AttachDelegate("Diag.Telemetry.Rpm", () => (object)_telemetryRpm);
            plugin.AttachDelegate("Diag.Telemetry.Gear", () => (object)_telemetryGear);
            plugin.AttachDelegate("Diag.Telemetry.BrakePercent", () => (object)_telemetryBrakePercent);
            plugin.AttachDelegate("Diag.Telemetry.ThrottlePercent", () => (object)_telemetryThrottlePercent);
            plugin.AttachDelegate("Diag.Telemetry.ClutchPercent", () => (object)_telemetryClutchPercent);
            plugin.AttachDelegate("Diag.Telemetry.LateralLocalVelocity", () => (object)_telemetryLateralLocalVelocity);
            plugin.AttachDelegate("Diag.Telemetry.LongitudinalG", () => (object)_telemetryLongitudinalG);
            plugin.AttachDelegate("Diag.Telemetry.LateralG", () => (object)_telemetryLateralG);

            plugin.AttachDelegate("Diag.Telemetry.WheelRPS.FrontLeft", () => (object)_wheelRpsFrontLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelRPS.FrontRight", () => (object)_wheelRpsFrontRight);
            plugin.AttachDelegate("Diag.Telemetry.WheelRPS.RearLeft", () => (object)_wheelRpsRearLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelRPS.RearRight", () => (object)_wheelRpsRearRight);

            plugin.AttachDelegate("Diag.Telemetry.WheelSpeed.FrontLeft", () => (object)_wheelSpeedFrontLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelSpeed.FrontRight", () => (object)_wheelSpeedFrontRight);
            plugin.AttachDelegate("Diag.Telemetry.WheelSpeed.RearLeft", () => (object)_wheelSpeedRearLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelSpeed.RearRight", () => (object)_wheelSpeedRearRight);

            plugin.AttachDelegate("Diag.Telemetry.WheelSlipRatio.FrontLeft", () => (object)_wheelSlipRatioFrontLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelSlipRatio.FrontRight", () => (object)_wheelSlipRatioFrontRight);
            plugin.AttachDelegate("Diag.Telemetry.WheelSlipRatio.RearLeft", () => (object)_wheelSlipRatioRearLeft);
            plugin.AttachDelegate("Diag.Telemetry.WheelSlipRatio.RearRight", () => (object)_wheelSlipRatioRearRight);

            plugin.AttachDelegate("Diag.Capabilities.WheelsSlip", () => (object)_capWheelsSlip);
            plugin.AttachDelegate("Diag.Capabilities.WheelsRPS", () => (object)_capWheelsRPS);
            plugin.AttachDelegate("Diag.Capabilities.WheelsSpeed", () => (object)_capWheelsSpeed);
            plugin.AttachDelegate("Diag.Capabilities.WheelsSlipDirectMode", () => (object)_capWheelsSlipDirectMode);
            plugin.AttachDelegate("Diag.Capabilities.WheelSlipUseSimpleBraking", () => (object)_capWheelSlipUseSimpleBraking);
            plugin.AttachDelegate("Diag.Capabilities.DetectLockFromWheelsSpeed", () => (object)_capDetectLockFromWheelsSpeed);
            plugin.AttachDelegate("Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius", () => (object)_capDetectLockFromWheelsRPSAndDummyRadius);
            plugin.AttachDelegate("Diag.Capabilities.Speed", () => (object)_capSpeed);
            plugin.AttachDelegate("Diag.Capabilities.Rpm", () => (object)_capRpm);
            plugin.AttachDelegate("Diag.Capabilities.GameFamily", () => (object)_capGameFamily);
            plugin.AttachDelegate("Diag.Capabilities.WheelSlipCalibrationProviderSlipScale", () => (object)_capWheelSlipCalibrationProviderSlipScale);
        }

        private static void AttachTier<TPlugin>(TPlugin plugin, string prefix, double[] values) where TPlugin : IPlugin
        {
            for (int i = 0; i < PublishedPropertyNames.Targets.Length; i++)
            {
                int index = i; // capture per iteration
                plugin.AttachDelegate(prefix + PublishedPropertyNames.Targets[index], () => values[index]);
            }
        }

        /// <summary>Same as <see cref="AttachTier"/>, but returns SimHub's own "no value" (a boxed null
        /// double, the exact pattern <see cref="_gforce"/> already uses) for any target
        /// <paramref name="hasValue"/> says is currently absent, rather than the numeric placeholder
        /// underneath it - see <see cref="Register"/>'s own remarks on why only the Raw tier does
        /// this.</summary>
        private static void AttachTierNullable<TPlugin>(TPlugin plugin, string prefix, double[] values, bool[] hasValue) where TPlugin : IPlugin
        {
            for (int i = 0; i < PublishedPropertyNames.Targets.Length; i++)
            {
                int index = i; // capture per iteration
                plugin.AttachDelegate(prefix + PublishedPropertyNames.Targets[index], () => (object)(hasValue[index] ? (double?)values[index] : null));
            }
        }

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
            // method alongside this one before reordering either.
            values.Add(_direction);
            values.Add(_motionLevel);
            values.Add(_motionMagnitudeG);
            values.Add(_selectedLockBranch);
            values.Add(_selectedSlipBranch);
            values.Add(_lockScaleCeiling);
            values.Add(_lockScaleCeilingIsPrimaryTier);
            values.Add(_slipScaleCeiling);
            values.Add(_slipScaleCeilingIsPrimaryTier);
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
