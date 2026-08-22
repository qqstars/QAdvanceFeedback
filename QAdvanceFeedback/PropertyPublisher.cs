using System;
using QAdvanceFeedback.Core;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.Health;
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
                AttachSafe(plugin, gforceNames[index], () => (object)_gforce[index]);
            }

            if (!diagnosticsEnabled) return;

            AttachSafe(plugin, "Diag.GameId", () => _gameId);
            AttachSafe(plugin, "Diag.CarId", () => _carId);
            AttachSafe(plugin, "Diag.Direction", () => _direction);
            AttachSafe(plugin, "Diag.MotionLevel", () => _motionLevel);
            AttachSafe(plugin, "Diag.MotionMagnitudeG", () => (object)_motionMagnitudeG);
            AttachSafe(plugin, "Diag.Lock.LearnedPeakG", () => (object)_lockLearnedPeakG);
            AttachSafe(plugin, "Diag.Lock.LearnerConfidence", () => (object)_lockLearnerConfidence);
            AttachSafe(plugin, "Diag.Slip.LearnedPeakG", () => (object)_slipLearnedPeakG);
            AttachSafe(plugin, "Diag.Slip.LearnerConfidence", () => (object)_slipLearnerConfidence);
            AttachSafe(plugin, "Diag.GForce.LearnedAccelMaxG", () => (object)_gforceLearnedAccelMaxG);
            AttachSafe(plugin, "Diag.GForce.LearnedDecelMaxG", () => (object)_gforceLearnedDecelMaxG);
            AttachSafe(plugin, "Diag.SelectedBranch.Lock", () => _selectedLockBranch);
            AttachSafe(plugin, "Diag.SelectedBranch.Slip", () => _selectedSlipBranch);
            AttachSafe(plugin, "Diag.Lock.SourceScaleCeiling", () => (object)_lockScaleCeiling);
            AttachSafe(plugin, "Diag.Lock.SourceScaleCeilingIsPrimaryTier", () => (object)_lockScaleCeilingIsPrimaryTier);
            AttachSafe(plugin, "Diag.Slip.SourceScaleCeiling", () => (object)_slipScaleCeiling);
            AttachSafe(plugin, "Diag.Slip.SourceScaleCeilingIsPrimaryTier", () => (object)_slipScaleCeilingIsPrimaryTier);
            AttachSafe(plugin, "Diag.Lock.SourceFallbackActive", () => (object)_lockSourceFallbackActive);
            AttachSafe(plugin, "Diag.Slip.SourceFallbackActive", () => (object)_slipSourceFallbackActive);

            AttachSafe(plugin, "Diag.Telemetry.WheelOnLooseSurface.FrontLeft", () => (object)_wheelOnLooseSurfaceFrontLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelOnLooseSurface.FrontRight", () => (object)_wheelOnLooseSurfaceFrontRight);
            AttachSafe(plugin, "Diag.Telemetry.WheelOnLooseSurface.RearLeft", () => (object)_wheelOnLooseSurfaceRearLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelOnLooseSurface.RearRight", () => (object)_wheelOnLooseSurfaceRearRight);
            AttachSafe(plugin, "Diag.Capabilities.SurfaceEverReportedLoose", () => (object)_surfaceEverReportedLoose);
            AttachSafe(plugin, "Diag.Lock.SurfaceLooseFraction", () => (object)_lockLooseFraction);
            AttachSafe(plugin, "Diag.Slip.SurfaceLooseFraction", () => (object)_slipLooseFraction);

            AttachTier(plugin, AllPublishedProperties.SourceLockPrefix, _sourceLock);
            AttachTier(plugin, AllPublishedProperties.SourceSlipPrefix, _sourceSlip);

            AttachTier(plugin, AllPublishedProperties.ProjectedLockWithoutPulsePrefix, _lockProjectedWithoutPulse);
            AttachTier(plugin, AllPublishedProperties.ProjectedSlipWithoutPulsePrefix, _slipProjectedWithoutPulse);

            // Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) - see the
            // backing fields' own remarks. Every delegate below returns a nullable boxed as object
            // (same pattern already used for _gforce above) so a missing reading publishes as SimHub's
            // own "no value" rather than a fabricated 0/false.
            AttachSafe(plugin, "Diag.Telemetry.GroundSpeedKmh", () => (object)_telemetryGroundSpeedKmh);
            AttachSafe(plugin, "Diag.Telemetry.SpeedKmh", () => (object)_telemetrySpeedKmh);
            AttachSafe(plugin, "Diag.Telemetry.GroundSpeedMps", () => (object)_telemetryGroundSpeedMps);
            AttachSafe(plugin, "Diag.Telemetry.Rpm", () => (object)_telemetryRpm);
            AttachSafe(plugin, "Diag.Telemetry.Gear", () => (object)_telemetryGear);
            AttachSafe(plugin, "Diag.Telemetry.BrakePercent", () => (object)_telemetryBrakePercent);
            AttachSafe(plugin, "Diag.Telemetry.ThrottlePercent", () => (object)_telemetryThrottlePercent);
            AttachSafe(plugin, "Diag.Telemetry.ClutchPercent", () => (object)_telemetryClutchPercent);
            AttachSafe(plugin, "Diag.Telemetry.LateralLocalVelocity", () => (object)_telemetryLateralLocalVelocity);
            AttachSafe(plugin, "Diag.Telemetry.LongitudinalG", () => (object)_telemetryLongitudinalG);
            AttachSafe(plugin, "Diag.Telemetry.LateralG", () => (object)_telemetryLateralG);

            AttachSafe(plugin, "Diag.Telemetry.WheelRPS.FrontLeft", () => (object)_wheelRpsFrontLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelRPS.FrontRight", () => (object)_wheelRpsFrontRight);
            AttachSafe(plugin, "Diag.Telemetry.WheelRPS.RearLeft", () => (object)_wheelRpsRearLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelRPS.RearRight", () => (object)_wheelRpsRearRight);

            AttachSafe(plugin, "Diag.Telemetry.WheelSpeed.FrontLeft", () => (object)_wheelSpeedFrontLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelSpeed.FrontRight", () => (object)_wheelSpeedFrontRight);
            AttachSafe(plugin, "Diag.Telemetry.WheelSpeed.RearLeft", () => (object)_wheelSpeedRearLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelSpeed.RearRight", () => (object)_wheelSpeedRearRight);

            AttachSafe(plugin, "Diag.Telemetry.WheelSlipRatio.FrontLeft", () => (object)_wheelSlipRatioFrontLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelSlipRatio.FrontRight", () => (object)_wheelSlipRatioFrontRight);
            AttachSafe(plugin, "Diag.Telemetry.WheelSlipRatio.RearLeft", () => (object)_wheelSlipRatioRearLeft);
            AttachSafe(plugin, "Diag.Telemetry.WheelSlipRatio.RearRight", () => (object)_wheelSlipRatioRearRight);

            AttachSafe(plugin, "Diag.Capabilities.WheelsSlip", () => (object)_capWheelsSlip);
            AttachSafe(plugin, "Diag.Capabilities.WheelsRPS", () => (object)_capWheelsRPS);
            AttachSafe(plugin, "Diag.Capabilities.WheelsSpeed", () => (object)_capWheelsSpeed);
            AttachSafe(plugin, "Diag.Capabilities.WheelsSlipDirectMode", () => (object)_capWheelsSlipDirectMode);
            AttachSafe(plugin, "Diag.Capabilities.WheelSlipUseSimpleBraking", () => (object)_capWheelSlipUseSimpleBraking);
            AttachSafe(plugin, "Diag.Capabilities.DetectLockFromWheelsSpeed", () => (object)_capDetectLockFromWheelsSpeed);
            AttachSafe(plugin, "Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius", () => (object)_capDetectLockFromWheelsRPSAndDummyRadius);
            AttachSafe(plugin, "Diag.Capabilities.Speed", () => (object)_capSpeed);
            AttachSafe(plugin, "Diag.Capabilities.Rpm", () => (object)_capRpm);
            AttachSafe(plugin, "Diag.Capabilities.GameFamily", () => (object)_capGameFamily);
            AttachSafe(plugin, "Diag.Capabilities.WheelSlipCalibrationProviderSlipScale", () => (object)_capWheelSlipCalibrationProviderSlipScale);
        }

        /// <summary>
        /// PIPELINE-EXCEPTION-SAFETY (docs\pipeline-exception-safety-report.md): every single published
        /// property funnels through here rather than a raw <c>AttachDelegate</c> call. <paramref name="provider"/>
        /// is wrapped in <see cref="SafeCall.Value{T}"/>, so even a future change that makes one of these
        /// lambdas capable of throwing (today, none can - every one is a direct read of a fixed-size
        /// backing array/field at a compile-time-constant index) degrades to publishing SimHub's own
        /// "no value" (null) for that ONE property, once, rather than propagating into whichever
        /// dashboard/ShakeIt effect/other plugin happens to be reading it at the time - see
        /// <c>PropertyEntry.Evaluate()</c>/<c>PropertyEntryWrapper.GetValue()</c>'s own decompiled,
        /// unguarded-primitive status in the pipeline-exception-safety report.
        /// </summary>
        private static void AttachSafe<TPlugin>(TPlugin plugin, string name, Func<object> provider) where TPlugin : IPlugin
            => plugin.AttachDelegate(name, () => SafeCall.Value(provider, (object)null, HealthSubsystems.PropertyPublish, "Health.Impact.PropertyPublish"));

        private static void AttachTier<TPlugin>(TPlugin plugin, string prefix, double[] values) where TPlugin : IPlugin
        {
            for (int i = 0; i < PublishedPropertyNames.Targets.Length; i++)
            {
                int index = i; // capture per iteration
                AttachSafe(plugin, prefix + PublishedPropertyNames.Targets[index], () => (object)values[index]);
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
                AttachSafe(plugin, prefix + PublishedPropertyNames.Targets[index], () => (object)(hasValue[index] ? (double?)values[index] : null));
            }
        }
    }
}
