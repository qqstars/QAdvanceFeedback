using QAdvanceFeedback.Core.Normalized;

namespace QAdvanceFeedback.Core.Projection
{
    /// <summary>
    /// Layer 5's orchestrator: projects Layer 4's Normalized result through each channel's
    /// <see cref="OutputProjector"/> curve, then through that channel's pulse, one instance per
    /// published target (nine per channel - <see cref="PublishedPropertyNames.Targets"/> order),
    /// since each target's pulse phase is independent (see <see cref="PulseGenerator"/>'s remarks).
    /// The projector itself is shared/reused across all nine targets within a channel - it is a pure,
    /// stateless function of its already-built curve, so calling it nine times a frame needs no
    /// per-target instance, unlike the pulse.
    /// </summary>
    public sealed class ProjectedWheelLockSlipEngine
    {
        // Indexed exactly as PublishedPropertyNames.Targets: FrontLeft, FrontRight, RearLeft,
        // RearRight, Front, Rear, Left, Right, All.
        private const int TargetCount = 9;

        private readonly OutputProjector _lockProjector;
        private readonly OutputProjector _slipProjector;
        private readonly PulseGenerator[] _lockPulses;
        private readonly PulseGenerator[] _slipPulses;

        public ProjectedWheelLockSlipEngine(
            OutputProjector lockProjector, PulseSettings lockPulse,
            OutputProjector slipProjector, PulseSettings slipPulse)
        {
            _lockProjector = lockProjector ?? throw new System.ArgumentNullException(nameof(lockProjector));
            _slipProjector = slipProjector ?? throw new System.ArgumentNullException(nameof(slipProjector));

            _lockPulses = new PulseGenerator[TargetCount];
            _slipPulses = new PulseGenerator[TargetCount];
            for (int i = 0; i < TargetCount; i++)
            {
                _lockPulses[i] = new PulseGenerator(lockPulse ?? throw new System.ArgumentNullException(nameof(lockPulse)));
                _slipPulses[i] = new PulseGenerator(slipPulse ?? throw new System.ArgumentNullException(nameof(slipPulse)));
            }
        }

        public ProjectedWheelLockSlipResult Compute(NormalizedWheelLockSlipResult reliable, double dtSeconds)
        {
            double[] lockRaw =
            {
                reliable.LockWheels.FrontLeft, reliable.LockWheels.FrontRight,
                reliable.LockWheels.RearLeft, reliable.LockWheels.RearRight,
                reliable.LockFront, reliable.LockRear, reliable.LockLeft, reliable.LockRight, reliable.LockAll
            };
            double[] slipRaw =
            {
                reliable.SlipWheels.FrontLeft, reliable.SlipWheels.FrontRight,
                reliable.SlipWheels.RearLeft, reliable.SlipWheels.RearRight,
                reliable.SlipFront, reliable.SlipRear, reliable.SlipLeft, reliable.SlipRight, reliable.SlipAll
            };

            double[] lockProjected = new double[TargetCount];
            double[] slipProjected = new double[TargetCount];
            // THE PULSE-INTO-SHAKE FIX (docs\raw-gap-and-pad-balance-report.md, see
            // ProjectedWheelLockSlipResult's own remarks): the curve-only value, BEFORE the pulse
            // stage - a pure recomputation of exactly what is fed INTO PulseGenerator.Advance below, so
            // it is trivially, structurally identical to the pulsed value whenever the pulse is not
            // currently engaged (PulseGenerator.Advance itself returns this same plain projected value
            // in that case), and differs from it only while a pulse cycle is actively modulating.
            double[] lockWithoutPulse = new double[TargetCount];
            double[] slipWithoutPulse = new double[TargetCount];
            for (int i = 0; i < TargetCount; i++)
            {
                lockWithoutPulse[i] = _lockProjector.Project(lockRaw[i]);
                slipWithoutPulse[i] = _slipProjector.Project(slipRaw[i]);

                lockProjected[i] = _lockPulses[i].Advance(dtSeconds, lockWithoutPulse[i]);
                slipProjected[i] = _slipPulses[i].Advance(dtSeconds, slipWithoutPulse[i]);
            }

            return new ProjectedWheelLockSlipResult(
                new Corners(lockProjected[0], lockProjected[1], lockProjected[2], lockProjected[3]),
                lockProjected[4], lockProjected[5], lockProjected[6], lockProjected[7], lockProjected[8],
                new Corners(slipProjected[0], slipProjected[1], slipProjected[2], slipProjected[3]),
                slipProjected[4], slipProjected[5], slipProjected[6], slipProjected[7], slipProjected[8],
                new Corners(lockWithoutPulse[0], lockWithoutPulse[1], lockWithoutPulse[2], lockWithoutPulse[3]),
                lockWithoutPulse[4], lockWithoutPulse[5], lockWithoutPulse[6], lockWithoutPulse[7], lockWithoutPulse[8],
                new Corners(slipWithoutPulse[0], slipWithoutPulse[1], slipWithoutPulse[2], slipWithoutPulse[3]),
                slipWithoutPulse[4], slipWithoutPulse[5], slipWithoutPulse[6], slipWithoutPulse[7], slipWithoutPulse[8]);
        }
    }
}
