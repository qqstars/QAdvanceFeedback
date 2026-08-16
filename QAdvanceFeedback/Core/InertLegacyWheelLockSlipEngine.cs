namespace QAdvanceFeedback.Core
{
    /// <summary>
    /// The PUBLIC fallback for <see cref="ILegacyWheelLockSlipEngine"/>, used only when
    /// <c>Private\QAdvanceFeedback\LegacyWheelLockSlipEngine.cs</c> (the withheld, decompiled-SimHub-
    /// derived implementation) is not present in this build - e.g. a fresh open-source clone before a
    /// third party has supplied their own implementation (see <c>..\Private\README.md</c>).
    /// <para/>
    /// Deliberately, clearly inert: every one of the 18 published Lock/Slip numbers is exactly 0 on
    /// every call, never a random/uninitialised/NaN value, and <see cref="Compute"/> never throws
    /// regardless of what <paramref name="sample"/> and shape it is given (it does not even have to
    /// look at it) - so a clean clone still builds, starts, and runs every other channel (Layer 4/5
    /// projection, G-force, UI, persistence) normally; only the Lock/Slip Raw numbers read as "nothing
    /// happening" instead of a real reading, which is the honest, safe degradation for "no algorithm
    /// is present" - never a crash, and never a value that could be mistaken for a genuine reading.
    /// </summary>
    public sealed class InertLegacyWheelLockSlipEngine : ILegacyWheelLockSlipEngine
    {
        public LegacyWheelLockSlipResult Compute(
            ITelemetrySample sample, LegacyThresholds? thresholds = null,
            AggregationWeights? lockAggregation = null, AggregationWeights? slipAggregation = null)
        {
            return new LegacyWheelLockSlipResult(
                Corners.Zero, 0.0, 0.0, 0.0, 0.0, 0.0,
                Corners.Zero, 0.0, 0.0, 0.0, 0.0, 0.0);
        }
    }
}
