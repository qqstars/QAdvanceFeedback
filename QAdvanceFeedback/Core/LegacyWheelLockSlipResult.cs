namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The full published shape of one Layer 3 update: four wheels plus the Front/Rear/Left/Right/All
    /// aggregates, for both the Lock and the Slip channel - all already scaled to 0-100 and clamped
    /// (see <see cref="LegacyWheelLockSlipEngine"/>). Mirrors <see cref="PublishedPropertyNames"/>'
    /// nine targets exactly, times two channels.
    /// </summary>
    public sealed class LegacyWheelLockSlipResult
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

        public LegacyWheelLockSlipResult(
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
