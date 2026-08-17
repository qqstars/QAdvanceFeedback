using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
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
    /// SPLIT INTO TWO FILES (docs\regression-fix-report.md): this file keeps only <see cref="Register{TPlugin}"/>
    /// and the <c>AttachTier</c>/<c>AttachTierNullable</c> helpers - the parts that actually need
    /// <c>SimHub.Plugins</c> (<c>IPlugin</c>/<c>AttachDelegate</c>). Every backing field, every
    /// <c>Update*</c> setter, every <c>*Snapshot</c> accessor, and <c>SnapshotAllValuesForCsv</c> itself
    /// now live in the SimHub-free sibling partial file <c>PropertyPublisher.State.cs</c>, which
    /// <c>QAdvanceFeedback.Tests</c> link-compiles directly - so the CSV-snapshot ordering (the exact
    /// place the header-only-CSV regression lived) is now exercised against the REAL production class,
    /// not a hand-written mirror of it.
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
    public sealed partial class PropertyPublisher
    {
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

            plugin.AttachDelegate("Diag.GameId", () => _gameId);
            plugin.AttachDelegate("Diag.CarId", () => _carId);
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
            plugin.AttachDelegate("Diag.Lock.SourceFallbackActive", () => _lockSourceFallbackActive);
            plugin.AttachDelegate("Diag.Slip.SourceFallbackActive", () => _slipSourceFallbackActive);

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
    }
}
