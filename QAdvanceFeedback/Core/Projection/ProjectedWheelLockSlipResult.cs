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

        public ProjectedWheelLockSlipResult(
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
