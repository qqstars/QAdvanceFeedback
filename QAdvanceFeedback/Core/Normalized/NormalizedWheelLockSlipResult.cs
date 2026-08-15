namespace QAdvanceFeedback.Core.Normalized
{
    /// <summary>
    /// Layer 4's full published shape: the same nine targets as Layer 3
    /// (<see cref="PublishedPropertyNames.Targets"/>), for both the Lock and the Slip channel, all
    /// already scaled to 0-100 and clamped, band-calibrated against what THIS car actually achieves
    /// rather than a fixed physical constant - see <see cref="GripLearner"/> and
    /// <see cref="NormalizedWheelLockSlipEngine"/>. Mirrors <see cref="LegacyWheelLockSlipResult"/>'s
    /// shape exactly, one tier further down the pipeline.
    /// </summary>
    public sealed class NormalizedWheelLockSlipResult
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

        public NormalizedWheelLockSlipResult(
            Corners lockWheels, double lockFront, double lockRear, double lockLeft, double lockRight, double lockAll,
            Corners slipWheels, double slipFront, double slipRear, double slipLeft, double slipRight, double slipAll)
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
        }
    }
}
