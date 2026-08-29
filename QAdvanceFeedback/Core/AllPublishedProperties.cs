using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;
using QAdvanceFeedback.Core.MotorsExport;

namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The exact, verbatim, SimHub-free set of every property-name suffix this plugin can publish -
    /// PRODUCT properties (Raw/Normalized/Projected x Wheel Lock/Wheel Slip, and the 8 G-force
    /// channels, 62 total) plus DIAGNOSTIC properties (gated by <see cref="Settings.GeneralSettings.EnableDiagnostics"/>).
    /// This is the ONE place that decides "which names get attached" - <c>PropertyPublisher.Register</c>
    /// is only the thin loop that dispatches each answer to the right SimHub <c>AttachDelegate</c> call,
    /// so the actual name set (and the diagnostics gate) is unit-tested here directly, without needing
    /// a live SimHub session - mirroring the sibling ReliableWheelLockSlip project's own
    /// <c>PublishedProperties.ForChannel</c> pattern.
    /// <para/>
    /// SimHub's own <c>PluginManager.GetName(name, pluginType)</c> is hard-coded as
    /// <c>pluginType.Name + "." + name</c> (decompiled and confirmed - see docs\layer123-report.md) -
    /// so the FULL registered name is "QAdvanceFeedback." + one of <see cref="ProductNames"/>/
    /// <see cref="DiagnosticNames"/>, e.g. "QAdvanceFeedback.WheelLock.Raw.All",
    /// "QAdvanceFeedback.GForce.Bottom.FrontLeft". Neither this class nor the plugin class itself
    /// repeats "QAdvanceFeedback." - GetName supplies it exactly once.
    /// </summary>
    public static class AllPublishedProperties
    {
        /// <summary>Layer 4's published prefix - "Normalized" (car-relative, grip-calibrated)
        /// projection, same nine targets as Layer 3's Raw (<see cref="PublishedPropertyNames.Targets"/>).
        /// Renamed from "Reliable" (an owner-requested rename - "Reliable" was a claim rather than a
        /// description; "Normalized" states what the layer actually does: normalises the raw signal
        /// against the learned per-car maximum so the bands mean the same thing in every car). This is
        /// a PUBLISHED PROPERTY NAME rename only - nothing here is a persisted settings key, so there is
        /// no config-migration concern (see docs\refinements-report.md for the full accounting of what
        /// was/was not renamed and why).</summary>
        public const string NormalizedLockPrefix = "WheelLock.Normalized.";

        /// <summary>See <see cref="NormalizedLockPrefix"/>.</summary>
        public const string NormalizedSlipPrefix = "WheelSlip.Normalized.";

        /// <summary>Layer 5's published prefix - after the curve/pulse projection. Renamed from
        /// "Final" (positional) to "Projected" (names the operation - matches the existing
        /// "OutputProjector"/curve-editor terminology already used elsewhere in this plugin).</summary>
        public const string ProjectedLockPrefix = "WheelLock.Projected.";

        /// <summary>See <see cref="ProjectedLockPrefix"/>.</summary>
        public const string ProjectedSlipPrefix = "WheelSlip.Projected.";

        /// <summary>
        /// DIAGNOSTIC prefix (docs\raw-gap-and-pad-balance-report.md) - the RESOLVED SOURCE value Layer
        /// 4 actually consumed for the Lock channel this frame (i.e. <c>QAdvanceFeedback.cs</c>'s own
        /// <c>lockSources</c> - our own Layer 3 Raw by default, or whatever the driver configured
        /// instead: a ShakeIt export, a Manual property/expression). Added specifically so a future
        /// "does our Raw match ShakeIt's real value" investigation never again needs to INVERT the
        /// Normalized transform to recover this - see that report's own account of how much work the
        /// inversion took the first time, with this diagnostic absent.
        /// </summary>
        public const string SourceLockPrefix = "Diag.Source.Lock.";

        /// <summary>See <see cref="SourceLockPrefix"/> - the Slip channel's equivalent.</summary>
        public const string SourceSlipPrefix = "Diag.Source.Slip.";

        /// <summary>
        /// DIAGNOSTIC prefix (docs\raw-gap-and-pad-balance-report.md, the pulse-into-shake fix) - the
        /// Lock channel's Layer 5 value with the pulse stage NOT applied (see
        /// <see cref="Projection.ProjectedWheelLockSlipResult"/>'s own remarks). Internal/troubleshooting
        /// only, per the owner's own request - NOT part of the default published property set, gated
        /// behind <see cref="Settings.GeneralSettings.EnableDiagnostics"/> exactly like every other name
        /// in <see cref="DiagnosticNames"/>.
        /// </summary>
        public const string ProjectedLockWithoutPulsePrefix = "WheelLock.ProjectedWithoutPulse.";

        /// <summary>See <see cref="ProjectedLockWithoutPulsePrefix"/> - the Slip channel's equivalent.</summary>
        public const string ProjectedSlipWithoutPulsePrefix = "WheelSlip.ProjectedWithoutPulse.";

        /// <summary>
        /// DIAGNOSTIC prefix (docs\telemetry-diagnostics-report.md) - the raw per-wheel
        /// <c>WheelRPS</c>/<c>WheelSpeed</c>/<c>WheelSlip</c> telemetry SimHub's own decompiled
        /// <c>WheelSlipEffect</c> candidate Lock branches (<c>LockFromWheelsRPSAndDummyRadius</c>/
        /// <c>GetRpsLock</c>/<c>GetLockFromWheelSpeed</c>) consume, added so a future capture can fit
        /// each branch's formula directly against real numbers instead of guessing again (see
        /// <c>docs\raw-match-rootcause-report.md</c> §2d). Only four wheels - no Front/Rear/Left/Right/
        /// All aggregate makes sense for a raw per-wheel physical reading the way it does for this
        /// plugin's own computed Lock/Slip channels.
        /// </summary>
        public const string TelemetryWheelRpsPrefix = "Diag.Telemetry.WheelRPS.";

        /// <summary>See <see cref="TelemetryWheelRpsPrefix"/>.</summary>
        public const string TelemetryWheelSpeedPrefix = "Diag.Telemetry.WheelSpeed.";

        /// <summary>See <see cref="TelemetryWheelRpsPrefix"/> - the raw per-wheel slip RATIO from
        /// <c>FeedbackData.WheelSlip</c> (deliberately named "WheelSlipRatio", not "WheelSlip", so it
        /// is never confused with this plugin's own already-computed <c>WheelSlip.*</c> channel).</summary>
        public const string TelemetryWheelSlipRatioPrefix = "Diag.Telemetry.WheelSlipRatio.";

        /// <summary>See <see cref="TelemetryWheelRpsPrefix"/> - the raw per-wheel loose-surface
        /// (grass/gravel) indicator, surface-keyed learning's own input
        /// (docs\branch-dispatch-and-source-keyed-learning-report.md).</summary>
        public const string TelemetryWheelOnLooseSurfacePrefix = "Diag.Telemetry.WheelOnLooseSurface.";

        /// <summary>
        /// The 54 Raw/Normalized/Projected names (9 targets x 3 tiers x 2 channels) plus the 8
        /// G-force names - 62 total, always published regardless of the diagnostics toggle. Order:
        /// Lock Raw, Slip Raw, Lock Normalized, Slip Normalized, Lock Projected, Slip Projected, then
        /// GForce.
        /// </summary>
        public static IEnumerable<string> ProductNames()
        {
            foreach (string t in PublishedPropertyNames.Targets) yield return PublishedPropertyNames.LockPrefix + t;
            foreach (string t in PublishedPropertyNames.Targets) yield return PublishedPropertyNames.SlipPrefix + t;

            foreach (string t in PublishedPropertyNames.Targets) yield return NormalizedLockPrefix + t;
            foreach (string t in PublishedPropertyNames.Targets) yield return NormalizedSlipPrefix + t;

            foreach (string t in PublishedPropertyNames.Targets) yield return ProjectedLockPrefix + t;
            foreach (string t in PublishedPropertyNames.Targets) yield return ProjectedSlipPrefix + t;

            foreach (string n in GForcePublishedNames.AllNames()) yield return n;
        }

        /// <summary>
        /// The internal/troubleshooting properties published ONLY when
        /// <see cref="Settings.GeneralSettings.EnableDiagnostics"/> is on - real state this plugin
        /// actually computes (Layer 4's measured direction and motion signal level, both channels'
        /// learned-grip state, the G-force learners' current values, and - see
        /// <see cref="SourceLockPrefix"/>/<see cref="SourceSlipPrefix"/> - the RESOLVED SOURCE value
        /// Layer 4 actually consumed, per wheel plus All, both channels), not placeholders.
        /// </summary>
        public static IEnumerable<string> DiagnosticNames()
        {
            // Diag.GameId/Diag.CarId (docs\shakeit-silence-diagnosis-report.md) - the resolved (gameId,
            // carId) every learner/scale-calibration key this frame is actually keyed under, so a
            // captured session can settle whether a car id is genuinely stable (vs. changing
            // frame-to-frame, or empty) - previously uncapturable at all.
            yield return "Diag.GameId";
            yield return "Diag.CarId";
            yield return "Diag.Direction";
            yield return "Diag.MotionLevel";
            yield return "Diag.MotionMagnitudeG";
            // Which of SimHub's own nine decompiled WheelSlipEffect.GetEffectValue branches Layer 3
            // actually ran this frame (docs\branch-dispatch-and-source-keyed-learning-report.md) - see
            // WheelSlipBranchNames/WheelSlipBranchSelector. Lets a driver confirm on ANY game which
            // branch is live and whether it matches what SimHub's own ShakeIt effect UI shows.
            yield return "Diag.SelectedBranch.Lock";
            yield return "Diag.SelectedBranch.Slip";

            // Per-source input calibration (docs\branch-dispatch-and-source-keyed-learning-report.md) -
            // the currently-configured source's own learned near-the-limit ceiling (native units, null
            // until calibrated) plus which tier produced it, per channel - so a driver can confirm on a
            // rig that a differently-scaled source really is being rescaled, not silently ignored.
            yield return "Diag.Lock.SourceScaleCeiling";
            yield return "Diag.Lock.SourceScaleCeilingIsPrimaryTier";
            yield return "Diag.Slip.SourceScaleCeiling";
            yield return "Diag.Slip.SourceScaleCeilingIsPrimaryTier";

            // SHAKEIT-SILENCE FALLBACK (docs\shakeit-silence-diagnosis-report.md) - whether the most
            // recent frame substituted Layer 3's own Raw for the configured source because the
            // configured source read near-zero while Raw independently read a genuine, well-above-floor
            // value (see NormalizedWheelLockSlipEngine's own remarks). Makes a degraded upstream source
            // (e.g. ShakeIt's own per-car calibration not yet mature) VISIBLE rather than indistinguishable
            // from "genuinely no lockup".
            yield return "Diag.Lock.SourceFallbackActive";
            yield return "Diag.Slip.SourceFallbackActive";

            // Surface-keyed learning (docs\branch-dispatch-and-source-keyed-learning-report.md) - each
            // channel's own current smoothed loose fraction (the per-wheel raw reading and the
            // "ever reported loose" latch are grouped further below, alongside the other
            // Diag.Telemetry.*/Diag.Capabilities.* names they belong with).
            yield return "Diag.Lock.SurfaceLooseFraction";
            yield return "Diag.Slip.SurfaceLooseFraction";
            yield return "Diag.Lock.LearnedPeakG";
            yield return "Diag.Lock.LearnerConfidence";
            yield return "Diag.Slip.LearnedPeakG";
            yield return "Diag.Slip.LearnerConfidence";
            yield return "Diag.GForce.LearnedAccelMaxG";
            yield return "Diag.GForce.LearnedDecelMaxG";

            foreach (string t in PublishedPropertyNames.Targets) yield return SourceLockPrefix + t;
            foreach (string t in PublishedPropertyNames.Targets) yield return SourceSlipPrefix + t;

            foreach (string t in PublishedPropertyNames.Targets) yield return ProjectedLockWithoutPulsePrefix + t;
            foreach (string t in PublishedPropertyNames.Targets) yield return ProjectedSlipWithoutPulsePrefix + t;

            // Diag.Telemetry.*/Diag.Capabilities.* (docs\telemetry-diagnostics-report.md) - raw
            // per-wheel/car-level telemetry inputs to SimHub's three candidate Lock branches, plus the
            // FeedbackCapabilities flags that pick which branch actually fires for the running title.
            // Diagnostics-only: never consumed by this plugin's own algorithm, never affects any
            // product-tier value. MUTATION EVIDENCE for this group is in
            // RawWheelTelemetryDiagnosticsTests (publishing these unconditionally, outside this gate,
            // is exactly the mutation exercised there).
            yield return "Diag.Telemetry.GroundSpeedKmh";
            yield return "Diag.Telemetry.SpeedKmh";
            yield return "Diag.Telemetry.GroundSpeedMps";
            yield return "Diag.Telemetry.Rpm";
            yield return "Diag.Telemetry.Gear";
            yield return "Diag.Telemetry.BrakePercent";
            yield return "Diag.Telemetry.ThrottlePercent";
            yield return "Diag.Telemetry.ClutchPercent";
            yield return "Diag.Telemetry.LateralLocalVelocity";
            yield return "Diag.Telemetry.LongitudinalG";
            yield return "Diag.Telemetry.LateralG";

            foreach (string w in MotorsExportPropertyNames.WheelSuffixes) yield return TelemetryWheelRpsPrefix + w;
            foreach (string w in MotorsExportPropertyNames.WheelSuffixes) yield return TelemetryWheelSpeedPrefix + w;
            foreach (string w in MotorsExportPropertyNames.WheelSuffixes) yield return TelemetryWheelSlipRatioPrefix + w;
            // Surface-keyed learning (docs\branch-dispatch-and-source-keyed-learning-report.md) - the
            // raw per-wheel loose-surface reading (FeedbackData.WheelInGrassOrGravel) plus whether a
            // genuine loose reading has EVER been observed this session (a latched observed-evidence
            // diagnostic, not a FeedbackCapabilities flag - none exists for this field, confirmed by
            // decompilation).
            foreach (string w in MotorsExportPropertyNames.WheelSuffixes) yield return TelemetryWheelOnLooseSurfacePrefix + w;
            yield return "Diag.Capabilities.SurfaceEverReportedLoose";

            yield return "Diag.Capabilities.WheelsSlip";
            yield return "Diag.Capabilities.WheelsRPS";
            yield return "Diag.Capabilities.WheelsSpeed";
            yield return "Diag.Capabilities.WheelsSlipDirectMode";
            yield return "Diag.Capabilities.WheelSlipUseSimpleBraking";
            yield return "Diag.Capabilities.DetectLockFromWheelsSpeed";
            yield return "Diag.Capabilities.DetectLockFromWheelsRPSAndDummyRadius";
            yield return "Diag.Capabilities.Speed";
            yield return "Diag.Capabilities.Rpm";
            yield return "Diag.Capabilities.GameFamily";
            yield return "Diag.Capabilities.WheelSlipCalibrationProviderSlipScale";

            // TIERED COLD-START REFERENCE SYSTEM (v1.0.7, docs\v107-tiered-coldstart-report.md) - which
            // tier (Tier1..Tier4) KeyedScaleLearner's own resolver most recently engaged for each
            // channel's currently-configured key, so the owner can see which tier engaged on a rig.
            // Appended at the END, mirroring every prior diagnostic addition to this method.
            yield return "Diag.Lock.ColdStartTier";
            yield return "Diag.Slip.ColdStartTier";
        }

        /// <summary>
        /// Every name this plugin will register this session: <see cref="ProductNames"/> always,
        /// PLUS <see cref="DiagnosticNames"/> when <paramref name="diagnosticsEnabled"/> is true.
        /// SimHub registers properties once at Init, so this must be evaluated once, at Init, from
        /// whatever the setting says at that moment - toggling it later needs a restart to change
        /// what this method would have returned (see the settings UI's own restart note).
        /// </summary>
        public static IEnumerable<string> AllNames(bool diagnosticsEnabled)
        {
            foreach (string n in ProductNames()) yield return n;
            if (!diagnosticsEnabled) yield break;
            foreach (string n in DiagnosticNames()) yield return n;
        }
    }
}
