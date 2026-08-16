namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Layer 5's full published shape: the same nine targets (FrontLeft/FrontRight/RearLeft/RearRight/
    /// Front/Rear/Left/Right/All), for both the Lock and the Slip channel, each already run through
    /// its channel's <see cref="OutputProjector"/> curve and <see cref="PulseGenerator"/> - the exact
    /// values published as <c>QAdvanceFeedback.WheelLock.Projected.*</c>/<c>WheelSlip.Projected.*</c>. Mirrors
    /// <see cref="LegacyWheelLockSlipResult"/>'s shape (Layer 3) and
    /// <see cref="Normalized.NormalizedWheelLockSlipResult"/>'s shape (Layer 4) exactly, one tier further
    /// down the pipeline.
    /// <para/>
    /// THE PULSE-INTO-SHAKE DEFECT AND ITS FIX (docs\raw-gap-and-pad-balance-report.md): with the
    /// G-Force "Integrate Wheel Lock and Slip" shake ON and a channel's pulse also enabled, the pulse's
    /// own 100-&gt;min-&gt;100 presentation waveform (a shaker-motor concern) was being read straight
    /// into the shake's own AMPLITUDE - one oscillation modulating another, felt as an unstable
    /// "wiggle" that has nothing to do with G. <c>LockAllWithoutPulse</c>/<c>SlipAllWithoutPulse</c>
    /// (and the matching per-wheel/aggregate set below) are the SAME curve-projected value with the
    /// pulse stage NOT applied - identical to the pulsed value whenever the pulse is not actually
    /// engaged (below saturation, or pulse disabled), differing only while a pulse cycle is running.
    /// <c>QAdvanceFeedback.cs</c> feeds these - not the pulsed <c>LockAll</c>/<c>SlipAll</c> - into
    /// <c>GForceEngine.Compute</c>'s shake integration; they are ALSO published under
    /// <c>WheelLock.ProjectedWithoutPulse.*</c>/<c>WheelSlip.ProjectedWithoutPulse.*</c>, gated behind
    /// <see cref="Settings.GeneralSettings.EnableDiagnostics"/> (the owner's own request - internal
    /// troubleshooting data, not part of the default published property set).
    /// </summary>
    public sealed class ProjectedWheelLockSlipResult
    {
        public Corners LockWheels { get; }
        public double LockFront { get; }
        public double LockRear { get; }
        public double LockLeft { get; }
        public double LockRight { get; }
        public double LockAll { get; }

        public Corners SlipWheels { get; }
        public double SlipFront { get; }
        public double SlipRear { get; }
        public double SlipLeft { get; }
        public double SlipRight { get; }
        public double SlipAll { get; }

        /// <summary>See this class's own remarks on the pulse-into-shake defect. Same shape as
        /// <see cref="LockWheels"/>, but with the pulse stage never applied.</summary>
        public Corners LockWheelsWithoutPulse { get; }
        public double LockFrontWithoutPulse { get; }
        public double LockRearWithoutPulse { get; }
        public double LockLeftWithoutPulse { get; }
        public double LockRightWithoutPulse { get; }
        public double LockAllWithoutPulse { get; }

        /// <summary>See <see cref="LockWheelsWithoutPulse"/> - the Slip channel's equivalent.</summary>
        public Corners SlipWheelsWithoutPulse { get; }
        public double SlipFrontWithoutPulse { get; }
        public double SlipRearWithoutPulse { get; }
        public double SlipLeftWithoutPulse { get; }
        public double SlipRightWithoutPulse { get; }
        public double SlipAllWithoutPulse { get; }

        public ProjectedWheelLockSlipResult(
            Corners lockWheels, double lockFront, double lockRear, double lockLeft, double lockRight, double lockAll,
            Corners slipWheels, double slipFront, double slipRear, double slipLeft, double slipRight, double slipAll,
            Corners lockWheelsWithoutPulse, double lockFrontWithoutPulse, double lockRearWithoutPulse, double lockLeftWithoutPulse, double lockRightWithoutPulse, double lockAllWithoutPulse,
            Corners slipWheelsWithoutPulse, double slipFrontWithoutPulse, double slipRearWithoutPulse, double slipLeftWithoutPulse, double slipRightWithoutPulse, double slipAllWithoutPulse)
        {
            LockWheels = lockWheels;
            LockFront = lockFront;
            LockRear = lockRear;
            LockLeft = lockLeft;
            LockRight = lockRight;
            LockAll = lockAll;

            SlipWheels = slipWheels;
            SlipFront = slipFront;
            SlipRear = slipRear;
            SlipLeft = slipLeft;
            SlipRight = slipRight;
            SlipAll = slipAll;

            LockWheelsWithoutPulse = lockWheelsWithoutPulse;
            LockFrontWithoutPulse = lockFrontWithoutPulse;
            LockRearWithoutPulse = lockRearWithoutPulse;
            LockLeftWithoutPulse = lockLeftWithoutPulse;
            LockRightWithoutPulse = lockRightWithoutPulse;
            LockAllWithoutPulse = lockAllWithoutPulse;

            SlipWheelsWithoutPulse = slipWheelsWithoutPulse;
            SlipFrontWithoutPulse = slipFrontWithoutPulse;
            SlipRearWithoutPulse = slipRearWithoutPulse;
            SlipLeftWithoutPulse = slipLeftWithoutPulse;
            SlipRightWithoutPulse = slipRightWithoutPulse;
            SlipAllWithoutPulse = slipAllWithoutPulse;
        }
    }
}
