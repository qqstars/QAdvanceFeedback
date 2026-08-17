namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The diagnostic label published as <see cref="LegacyWheelLockSlipResult.SelectedLockBranch"/>/
    /// <see cref="LegacyWheelLockSlipResult.SelectedSlipBranch"/> for each of the signal shapes
    /// <see cref="WheelSlipBranchSelector"/> can select. These labels are deliberately worded to match
    /// the "Algorythm" field SimHub's own ShakeIt effect UI shows for the equivalent selection, so a
    /// driver comparing this plugin's diagnostics against SimHub's own effect configuration for the
    /// same title sees the same name in both places.
    /// </summary>
    public static class WheelSlipBranchNames
    {
        /// <summary>Slip-only: a per-title precalibrated slip scale is available.</summary>
        public const string PrecalibratedSlip = "Precalibrated slip";

        /// <summary>Lock-only: wheel rotation rate plus an assumed wheel radius.</summary>
        public const string RpsAndDummyRadius = "RPSandDummyRadius";

        /// <summary>Lock-only: wheel linear speed compared directly against ground speed.</summary>
        public const string WheelSpeedLock = "WheelSpeedLock";

        /// <summary>Shared: pedal position and ground speed only, no wheel-level telemetry at all.</summary>
        public const string BrakingVsSpeed = "Braking vs speed";

        /// <summary>Lock-only: wheel rotation rate compared against a learned cruise reference. The
        /// branch this session's own captured capabilities dispatch Lock to.</summary>
        public const string Rps = "RPS";

        /// <summary>Shared: the title reports a direct slip-feedback capability with no signal this
        /// plugin can read yet - a genuine, deliberate zero (no title reaching this branch has ever
        /// been observed to expect anything else here), not a gap in this port.</summary>
        public const string DirectSlipFeedback = "Direct slip feedback";

        /// <summary>Shared: a learned slip-ratio percentile band. The branch this session's own
        /// captured capabilities dispatch Slip to.</summary>
        public const string SlipData = "Slip data";

        /// <summary>Shared: wheel linear speed compared against ground speed, with a gear-keyed cruise
        /// reference.</summary>
        public const string WheelsSpeed = "Wheels speed";

        /// <summary>Shared, legacy sub-variant: pedal position, RPM and ground speed only (no
        /// wheel-level telemetry), matching the title's "legacy iRacing algorithm" toggle being
        /// enabled.</summary>
        public const string RpmVsSpeedLegacy = "RPM vs Speed (legacy)";

        /// <summary>Shared, non-legacy sub-variant of <see cref="RpmVsSpeedLegacy"/>'s own capability
        /// gate - the per-effects-container toggle that distinguishes the two sub-variants is not part
        /// of any capability this plugin can read, so which sub-variant a given title/profile actually
        /// uses cannot be determined. This plugin always assumes the legacy sub-variant (the same
        /// assumption SimHub itself defaults to when the toggle is unset) and labels the assumption
        /// explicitly here, rather than silently reporting <see cref="RpmVsSpeedLegacy"/> as if it were
        /// certain.</summary>
        public const string RpmVsSpeedAssumedLegacy = "RPM vs Speed (assumed legacy - container flag unreadable)";

        /// <summary>No capability/telemetry snapshot was supplied to <c>Compute</c> at all (the
        /// <c>rawTelemetry</c> parameter was left at its default null) - e.g. an older caller/test that
        /// predates capability-based dispatch. Falls back to the same fixed branch this plugin used
        /// before that dispatch existed (<see cref="BrakingVsSpeed"/> for Lock,
        /// <see cref="RpmVsSpeedLegacy"/> for Slip).</summary>
        public const string NoCapabilitiesSuppliedLockFallback = "Braking vs speed (no capabilities supplied - legacy default)";

        /// <summary>See <see cref="NoCapabilitiesSuppliedLockFallback"/> - the Slip channel's
        /// equivalent.</summary>
        public const string NoCapabilitiesSuppliedSlipFallback = "RPM vs Speed (legacy) (no capabilities supplied - legacy default)";

        /// <summary>None of the recognised signal shapes were reported by this title (e.g. no
        /// Speed/RPM/wheel telemetry at all) - a flat 0 in exactly this situation is the honest
        /// outcome, not a defect.</summary>
        public const string NoBranchMatched = "No branch matched (title reports no usable capability)";
    }
}
