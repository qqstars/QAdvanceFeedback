namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// One wheel's Lock and Slip readings from <see cref="LegacySlipAlgorithm"/>, still in SimHub's
    /// own native ~[0,1] scale (see that class's remarks on scaling) - NOT yet the 0-100 published
    /// Raw.* value. <see cref="LegacyWheelLockSlipEngine"/> is what scales and clamps these.
    /// </summary>
    public readonly struct WheelLegacyResult
    {
        public static readonly WheelLegacyResult Zero = new WheelLegacyResult(0.0, 0.0);

        public readonly double Lock;
        public readonly double Slip;

        public WheelLegacyResult(double lockValue, double slipValue)
        {
            Lock = lockValue;
            Slip = slipValue;
        }
    }
}
