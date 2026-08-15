using System.Collections.Generic;
using QAdvanceFeedback.Core.GForce;

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
        /// learned-grip state, and the G-force learners' current values), not placeholders.
        /// </summary>
        public static IEnumerable<string> DiagnosticNames()
        {
            yield return "Diag.Direction";
            yield return "Diag.MotionLevel";
            yield return "Diag.MotionMagnitudeG";
            yield return "Diag.Lock.LearnedPeakG";
            yield return "Diag.Lock.LearnerConfidence";
            yield return "Diag.Slip.LearnedPeakG";
            yield return "Diag.Slip.LearnerConfidence";
            yield return "Diag.GForce.LearnedAccelMaxG";
            yield return "Diag.GForce.LearnedDecelMaxG";
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
