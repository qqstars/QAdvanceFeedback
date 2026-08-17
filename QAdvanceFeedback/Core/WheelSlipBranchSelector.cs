namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// Pure, SimHub-free selection logic: a priority-ordered decision over this frame's own capability
    /// snapshot (<see cref="RawWheelTelemetrySnapshot"/>) choosing WHICH signal shape a title actually
    /// supports, without computing any branch's actual value (that is
    /// <c>QAdvanceFeedback.Core.RawCalculator</c>'s job - see <c>RawCalculatorEngine</c>). Kept separate
    /// and public/testable because this selection is plain boolean priority over already-public
    /// diagnostic capability flags - it reveals no formula, unlike each branch's own arithmetic.
    /// <para/>
    /// This selector reads the same capability flags the running title reports through
    /// <c>pluginManager.GameManager.GetFeedbackCapabilities()</c>, so the same title always yields the
    /// same selected branch on every run.
    /// <para/>
    /// ONE GENUINE, UNCLOSEABLE GAP (see <see cref="WheelSlipBranchNames.RpmVsSpeedAssumedLegacy"/>):
    /// the legacy-vs-non-legacy sub-choice for the last branch depends on a per-effects-container UI
    /// setting that is not part of any capability this plugin can read - this selector always assumes
    /// the legacy sub-variant (the title's own default when that setting is unset), and says so
    /// explicitly in the returned branch name rather than silently reporting certainty it does not have.
    /// </summary>
    public static class WheelSlipBranchSelector
    {
        /// <summary>
        /// Selects the branch name for one channel (Lock when <paramref name="isLock"/>, otherwise
        /// Slip) from this frame's capability snapshot - mirrors <c>GetEffectValue</c>'s own nine-way
        /// if/else EXACTLY, including which conditions are Lock-only vs shared between both channels
        /// (see each <see cref="WheelSlipBranchNames"/> constant's own remarks for which is which).
        /// <para/>
        /// <paramref name="caps"/> null means "no capability/telemetry snapshot was supplied at all"
        /// (e.g. a pre-existing caller that predates this dispatch) - handled by the CALLER, not here
        /// (<c>RawCalculatorEngine</c>'s own null-check falls back to the pre-dispatch
        /// fixed behaviour BEFORE ever calling this method), so this method itself never receives null
        /// in practice; it is still defended here (returns <see cref="WheelSlipBranchNames.NoBranchMatched"/>)
        /// so a direct unit test against a null snapshot gets an honest, non-throwing answer rather than
        /// a NullReferenceException.
        /// </summary>
        public static string Select(RawWheelTelemetrySnapshot caps, bool isLock)
        {
            if (caps == null) return WheelSlipBranchNames.NoBranchMatched;

            // Branch 1: !IsLock && WheelSlipCalibrationProvider?.SlipScale.HasValue
            if (!isLock && caps.CapabilityWheelSlipCalibrationProviderSlipScale.HasValue)
                return WheelSlipBranchNames.PrecalibratedSlip;

            // Branch 2: IsLock && DetectLockFromWheelsRPSAndDummyRadius
            if (isLock && caps.CapabilityDetectLockFromWheelsRPSAndDummyRadius == true)
                return WheelSlipBranchNames.RpsAndDummyRadius;

            // Branch 3: IsLock && DetectLockFromWheelsSpeed
            if (isLock && caps.CapabilityDetectLockFromWheelsSpeed == true)
                return WheelSlipBranchNames.WheelSpeedLock;

            // Branch 4: WheelSlipUseSimpleBraking (shared)
            if (caps.CapabilityWheelSlipUseSimpleBraking == true)
                return WheelSlipBranchNames.BrakingVsSpeed;

            // Branch 5: IsLock && WheelsRPS
            if (isLock && caps.CapabilityWheelsRPS == true)
                return WheelSlipBranchNames.Rps;

            // Branch 6: WheelsSlipDirectMode (shared, genuine no-op in SimHub itself)
            if (caps.CapabilityWheelsSlipDirectMode == true)
                return WheelSlipBranchNames.DirectSlipFeedback;

            // Branch 7: WheelsSlip (shared)
            if (caps.CapabilityWheelsSlip == true)
                return WheelSlipBranchNames.SlipData;

            // Branch 8: WheelsSpeed (shared)
            if (caps.CapabilityWheelsSpeed == true)
                return WheelSlipBranchNames.WheelsSpeed;

            // Branch 9: Speed && RPM (shared) - sub-variant is unreadable, see this class's own remarks.
            if (caps.CapabilitySpeed == true && caps.CapabilityRpm == true)
                return WheelSlipBranchNames.RpmVsSpeedAssumedLegacy;

            return WheelSlipBranchNames.NoBranchMatched;
        }
    }
}
